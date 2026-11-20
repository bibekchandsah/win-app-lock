using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Services.PcChecker;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace AppLock.Views
{
    /// <summary>
    /// The interactive side of PC Checker: every test here measures something real (a benchmark, a file, a
    /// tone, a level, a frame) instead of restating what the hardware reports about itself.
    /// </summary>
    public sealed partial class HardwareTestsPage : Page
    {
        private const int SM_DIGITIZER = 94;
        private const int SM_MAXIMUMTOUCHES = 95;

        // Windows constants for the digitizer bitmask returned by GetSystemMetrics(SM_DIGITIZER).
        private const int NID_INTEGRATED_TOUCH = 0x01;
        private const int NID_INTEGRATED_PEN = 0x04;
        private const int NID_EXTERNAL_PEN = 0x08;
        private const int NID_EXTERNAL_TOUCH = 0x02;
        private const int NID_MULTI_INPUT = 0x40;
        private const int NID_READY = 0x80;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        // A low-level hook is the only way to tell left from right Shift/Ctrl/Alt and main from
        // numpad Enter, and to see keys XAML never receives (Win, Print Screen).
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const uint LLKHF_EXTENDED = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(nint hhk);

        [DllImport("user32.dll")]
        private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern nint GetModuleHandleW(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

        private CancellationTokenSource? _diskTest;
        private CancellationTokenSource? _networkTest;
        private WaveOutEvent? _toneOutput;
        private WaveInEvent? _microphone;
        private MediaCapture? _camera;
        private MediaPlayer? _cameraPlayer;

        private int _keyCount;
        private nint _keyboardHook;
        private LowLevelKeyboardProc? _keyboardHookProc;
        private int _leftClicks;
        private int _rightClicks;
        private int _otherClicks;

        /// <summary>Key caps keyed by <see cref="Windows.System.VirtualKey"/> name, so a key press can light one up.</summary>
        private readonly Dictionary<string, Border> _keyCaps = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Border> _mouseCaps = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _heldKeys = new();
        private readonly HashSet<string> _testedKeys = new(StringComparer.OrdinalIgnoreCase);
        private bool _layoutBuilt;

        public HardwareTestsPage()
        {
            this.InitializeComponent();

            Loaded += HardwareTestsPage_Loaded;
            Unloaded += HardwareTestsPage_Unloaded;

            // Arrow keys and Tab are consumed by focus navigation, so listen for handled events too.
            AddHandler(KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(HardwareTestsPage_KeyDown), true);
            AddHandler(KeyUpEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(HardwareTestsPage_KeyUp), true);
        }

        private void HardwareTestsPage_Loaded(object sender, RoutedEventArgs e)
        {
            FillDrives();
            FillSpeakers();
            FillMicrophones();
            DescribeTouch();
            BuildKeyboardLayout();
            InstallKeyboardHook();
        }

        private void InstallKeyboardHook()
        {
            if (_keyboardHook != 0) return;

            try
            {
                _keyboardHookProc = KeyboardHookProc;
                _keyboardHook = SetWindowsHookExW(WH_KEYBOARD_LL, _keyboardHookProc, GetModuleHandleW(null), 0);
                if (_keyboardHook == 0)
                    System.Diagnostics.Debug.WriteLine($"[HardwareTests] keyboard hook failed: {Marshal.GetLastWin32Error()}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HardwareTests] keyboard hook error: {ex.Message}");
            }
        }

        private nint KeyboardHookProc(int nCode, nint wParam, nint lParam)
        {
            if (nCode >= 0 && _keyboardHook != 0 && ReferenceEquals(TestsTabs.SelectedItem, KeyboardTab) && IsAppForeground())
            {
                try
                {
                    int message = (int)wParam;
                    bool down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
                    bool up = message is WM_KEYUP or WM_SYSKEYUP;

                    if (down || up)
                    {
                        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                        string? name = MapPhysicalKey(info.vkCode, info.scanCode, info.flags);
                        if (!string.IsNullOrEmpty(name))
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (!ReferenceEquals(TestsTabs.SelectedItem, KeyboardTab)) return;

                                if (down) RegisterKeyDown(name, (Windows.System.VirtualKey)info.vkCode);
                                else RegisterKeyUp(name);
                            });
                        }
                    }
                }
                catch
                {
                    // A hook must never throw into the input pipeline.
                }
            }

            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        private static bool IsAppForeground()
        {
            try
            {
                var window = App.MainWindowInstance;
                return window != null && GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(window);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Maps the physical key, including left/right variants and OEM keys XAML reports as numbers.</summary>
        private static string? MapPhysicalKey(uint vk, uint scanCode, uint flags)
        {
            bool extended = (flags & LLKHF_EXTENDED) != 0;

            switch (vk)
            {
                case 0x10: return scanCode == 0x36 ? "RightShift" : "LeftShift";
                case 0xA0: return "LeftShift";
                case 0xA1: return "RightShift";
                case 0x11: return extended ? "RightControl" : "Control";
                case 0xA2: return "Control";
                case 0xA3: return "RightControl";
                case 0x12: return extended ? "RightMenu" : "Menu";
                case 0xA4: return "Menu";
                case 0xA5: return "RightMenu";
                case 0x5B: return "LeftWindows";
                case 0x5C: return "RightWindows";
                case 0x5D: return "Application";
                case 0x2C: return "Snapshot";
                case 0x91: return "Scroll";
                case 0x2D: return "Insert";
                case 0x0D: return extended ? "NumberPadEnter" : "Enter";
                case 0x6E: return "Decimal";
                case 0x6F: return "Divide";
                case 0x6A: return "Multiply";
                case 0x6B: return "Add";
                case 0x6D: return "Subtract";
                case 0x90: return "NumberKeyLock";
                case 0xC0: return "OemTilde";
                case 0xBD: return "OemMinus";
                case 0xBB: return "OemPlus";
                case 0xDB: return "OemOpenBrackets";
                case 0xDD: return "OemCloseBrackets";
                case 0xDC: return "OemPipe";
                case 0xBA: return "OemSemicolon";
                case 0xDE: return "OemQuotes";
                case 0xBC: return "OemComma";
                case 0xBE: return "OemPeriod";
                case 0xBF: return "OemQuestion";
            }

            if (vk is >= 0x60 and <= 0x69) return "NumberPad" + (vk - 0x60);
            if (vk is >= 0x30 and <= 0x39) return "Number" + (vk - 0x30);
            if (vk is >= 0x41 and <= 0x5A) return ((char)vk).ToString();
            if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);

            return ((Windows.System.VirtualKey)vk).ToString();
        }

        private const double KeyGap = 4;
        private const double NavKeyFactor = 1.35;
        private const double MouseMaxWidth = 136;
        private const double GapBeforeMouse = 16;

        private double _keyUnit = 24;
        private double _mouseWidth = MouseMaxWidth;

        private void BuildKeyboardLayout()
        {
            if (_layoutBuilt) return;
            _layoutBuilt = true;

            _keyUnit = ChooseKeyUnit(AvailableLayoutWidth());
            _mouseWidth = MouseWidth(AvailableLayoutWidth(), _keyUnit);
            RebuildKeyCaps();
        }

        /// <summary>The cap size is picked so the whole keyboard and mouse fill the card instead of scrolling.</summary>
        private void RebuildKeyCaps()
        {
            KeyboardLayoutHost.Children.Clear();
            MouseDiagramHost.Children.Clear();
            _keyCaps.Clear();
            _mouseCaps.Clear();

            double available = AvailableLayoutWidth();
            _keyUnit = ChooseKeyUnit(available);
            _mouseWidth = MouseWidth(available, _keyUnit);

            // label | VirtualKey name | width in key units
            var mainRows = new[]
            {
                new[] { "Esc|Escape|3", "F1|F1|1", "F2|F2|1", "F3|F3|1", "F4|F4|1", "F5|F5|1", "F6|F6|1",
                        "F7|F7|1", "F8|F8|1", "F9|F9|1", "F10|F10|1", "F11|F11|1", "F12|F12|1" },
                new[] { "`|OemTilde|1", "1|Number1|1", "2|Number2|1", "3|Number3|1", "4|Number4|1", "5|Number5|1",
                        "6|Number6|1", "7|Number7|1", "8|Number8|1", "9|Number9|1", "0|Number0|1", "-|OemMinus|1",
                        "=|OemPlus|1", "Backspace|Back|2" },
                new[] { "Tab|Tab|1.5", "Q|Q|1", "W|W|1", "E|E|1", "R|R|1", "T|T|1", "Y|Y|1", "U|U|1", "I|I|1",
                        "O|O|1", "P|P|1", "[|OemOpenBrackets|1", "]|OemCloseBrackets|1", "\\|OemPipe|1.5" },
                new[] { "Caps|CapitalLock|1.8", "A|A|1", "S|S|1", "D|D|1", "F|F|1", "G|G|1", "H|H|1", "J|J|1",
                        "K|K|1", "L|L|1", ";|OemSemicolon|1", "'|OemQuotes|1", "Enter|Enter|2.2" },
                new[] { "Shift|LeftShift|2.3", "Z|Z|1", "X|X|1", "C|C|1", "V|V|1", "B|B|1", "N|N|1", "M|M|1",
                        ",|OemComma|1", ".|OemPeriod|1", "/|OemQuestion|1", "Shift|RightShift|2.7" },
                new[] { "Ctrl|Control|1.3", "Win|LeftWindows|1.2", "Alt|Menu|1.2", "Space|Space|6.4",
                        "Alt|RightMenu|1.2", "Win|RightWindows|1.2", "Menu|Application|1.2", "Ctrl|RightControl|1.3" }
            };

            // Six rows to match the main block: the arrows sit in the last two, below a blank row.
            var navigationRows = new[]
            {
                new[] { "PrtSc|Snapshot|1", "ScrLk|Scroll|1", "Pause|Pause|1" },
                new[] { "Ins|Insert|1", "Home|Home|1", "PgUp|PageUp|1" },
                new[] { "Del|Delete|1", "End|End|1", "PgDn|PageDown|1" },
                new[] { "|None|1", "|None|1", "|None|1" },
                new[] { "|None|1", "\u2191|Up|1", "|None|1" },
                new[] { "\u2190|Left|1", "\u2193|Down|1", "\u2192|Right|1" }
            };

            KeyboardLayoutHost.Children.Add(BuildCluster(mainRows));
            KeyboardLayoutHost.Children.Add(BuildCluster(navigationRows, NavKeyFactor));
            KeyboardLayoutHost.Children.Add(BuildNumpad());

            BuildMouseDiagram();

            foreach (var cap in _keyCaps.Values) ApplyKeyCapState(cap);
            UpdateCoverage();
        }

        private double AvailableLayoutWidth()
        {
            double width = InputPad.ActualWidth - InputPad.Padding.Left - InputPad.Padding.Right;
            return width > 240 ? width : 740;
        }

        private static double ChooseKeyUnit(double available)
        {
            for (double unit = 26; unit >= 15; unit -= 0.5)
            {
                if (EstimatedWidth(unit) <= available) return unit;
            }
            return 15;
        }

        /// <summary>Width of a row of caps: each cap is <paramref name="factor"/> units plus the gaps between them.</summary>
        private static double ClusterWidth(int keyCount, double unit, double factor = 1) =>
            keyCount * factor * (unit + KeyGap) - KeyGap;

        /// <summary>Width of the main block, navigation block and numpad, including the gaps between them.</summary>
        private static double KeyboardWidth(double unit) =>
            ClusterWidth(14, unit) + 10 +
            ClusterWidth(3, unit, NavKeyFactor) + 10 +
            ClusterWidth(4, unit, NavKeyFactor);

        /// <summary>The plan reserves the widest mouse; the real column then takes whatever is left.</summary>
        private static double EstimatedWidth(double unit) => KeyboardWidth(unit) + GapBeforeMouse + MouseMaxWidth;

        /// <summary>Mouse column: never narrower than a usable mouse, never wider than the free space.</summary>
        private static double MouseWidth(double available, double unit) =>
            Math.Clamp(Math.Round(available - KeyboardWidth(unit) - GapBeforeMouse), 112, MouseMaxWidth);

        private void InputPad_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_keyCaps.Count == 0) return;

            double available = AvailableLayoutWidth();
            double unit = ChooseKeyUnit(available);
            double mouse = MouseWidth(available, unit);

            if (Math.Abs(unit - _keyUnit) < 0.5 && Math.Abs(mouse - _mouseWidth) < 1) return;

            _keyUnit = unit;
            _mouseWidth = mouse;
            RebuildKeyCaps();
        }

        private StackPanel BuildCluster(string[][] rows, double factor = 1)
        {
            var cluster = new StackPanel { Spacing = KeyGap };

            foreach (var row in rows)
            {
                var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = KeyGap };
                foreach (var definition in row)
                {
                    var parts = definition.Split('|');
                    string label = parts[0];
                    string keyName = parts.Length > 1 ? parts[1] : string.Empty;
                    double units = parts.Length > 2 && double.TryParse(parts[2], out var value) ? value : 1;

                    rowPanel.Children.Add(CreateCap(label, keyName,
                        units * factor * (_keyUnit + KeyGap) - KeyGap, _keyUnit + KeyGap));
                }
                cluster.Children.Add(rowPanel);
            }

            return cluster;
        }

        /// <summary>
        /// The numpad uses a grid so Enter can span two rows and 0 can span two columns, like a real tenkey.
        /// </summary>
        private Grid BuildNumpad()
        {
            double cell = Math.Round(NavKeyFactor * (_keyUnit + KeyGap) - KeyGap);
            double height = Math.Round(_keyUnit + KeyGap);

            var grid = new Grid { RowSpacing = KeyGap, ColumnSpacing = KeyGap };
            for (int i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cell) });
            for (int i = 0; i < 6; i++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });

            void Place(string definition, int row, int column, int columnSpan = 1, int rowSpan = 1)
            {
                var parts = definition.Split('|');
                var cap = CreateCap(parts[0], parts[1],
                    cell * columnSpan + KeyGap * (columnSpan - 1),
                    height * rowSpan + KeyGap * (rowSpan - 1));

                Grid.SetRow(cap, row);
                Grid.SetColumn(cap, column);
                Grid.SetColumnSpan(cap, columnSpan);
                Grid.SetRowSpan(cap, rowSpan);
                grid.Children.Add(cap);
            }

            Place("Num|NumberKeyLock", 0, 0);
            Place("/|Divide", 0, 1);
            Place("*|Multiply", 0, 2);
            Place("-|Subtract", 0, 3);
            Place("7|NumberPad7", 1, 0);
            Place("8|NumberPad8", 1, 1);
            Place("9|NumberPad9", 1, 2);
            Place("+|Add", 1, 3);
            Place("4|NumberPad4", 2, 0);
            Place("5|NumberPad5", 2, 1);
            Place("6|NumberPad6", 2, 2);
            Place("1|NumberPad1", 3, 0);
            Place("2|NumberPad2", 3, 1);
            Place("3|NumberPad3", 3, 2);
            Place("Enter|NumberPadEnter", 3, 3, rowSpan: 2);
            Place("0|NumberPad0", 4, 0, columnSpan: 2);
            Place(".|Decimal", 4, 2);

            return grid;
        }

        private Border CreateCap(string label, string keyName, double width, double height)
        {
            bool blank = keyName == "None";
            var cap = new Border
            {
                Width = Math.Round(width),
                Height = Math.Round(height),
                Tag = keyName,
                Style = (Style)Resources[blank ? "BlankKeyCapStyle" : "KeyCapStyle"],
                Child = new TextBlock
                {
                    Text = label,
                    FontSize = Math.Clamp(Math.Round(_keyUnit * 0.42), 8, 11),
                    Style = (Style)Resources["KeyCapTextStyle"]
                }
            };

            if (!blank && !string.IsNullOrEmpty(keyName)) _keyCaps[keyName] = cap;
            return cap;
        }

        /// <summary>
        /// A top-down mouse: left / wheel / right on the top and the two side buttons on the left flank, so a
        /// button that never lights up is visibly a button that never reached Windows.
        /// </summary>
        private void BuildMouseDiagram()
        {
            // Everything is derived from the column that is actually free, so the mouse and its caps
            // never run past the pad when the window gets narrower.
            double column = _mouseWidth;
            const double bodyPadding = 8;
            double inner = column - bodyPadding * 2 - 2; // minus padding and the 1px border each side
            double buttonWidth = Math.Max(22, Math.Round((inner - 14 - 10) / 2));
            double buttonHeight = Math.Round(Math.Clamp(column * 0.30, 28, 40));
            double sideWidth = Math.Round(Math.Clamp(inner * 0.32, 26, 40));
            double sideHeight = Math.Round(Math.Clamp(column * 0.18, 18, 24));

            // A gentle corner keeps the outline a mouse shape without the rounding eating the buttons.
            var body = new Border
            {
                Width = column,
                CornerRadius = new CornerRadius(14, 14, 22, 22),
                Style = (Style)Resources["KeyCapStyle"],
                Padding = new Thickness(bodyPadding)
            };

            var layout = new StackPanel { Spacing = 6 };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            buttons.Children.Add(MouseCap("left", "Left", buttonWidth, buttonHeight));
            buttons.Children.Add(MouseCap("middle", "\u22EE", 14, buttonHeight));
            buttons.Children.Add(MouseCap("right", "Right", buttonWidth, buttonHeight));
            layout.Children.Add(buttons);

            var bodyArea = new Grid { Height = Math.Round(Math.Clamp(column * 0.70, 62, 92)) };

            var sideButtons = new StackPanel
            {
                Spacing = 5,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            sideButtons.Children.Add(MouseCap("back", "Back", sideWidth, sideHeight));
            sideButtons.Children.Add(MouseCap("forward", "Fwd", sideWidth, sideHeight));
            bodyArea.Children.Add(sideButtons);

            bodyArea.Children.Add(new TextBlock
            {
                Text = "Mouse",
                FontSize = 10,
                Style = (Style)Resources["KeyCapTextStyle"]
            });

            layout.Children.Add(bodyArea);
            body.Child = layout;
            MouseDiagramHost.Children.Add(body);

            MouseDiagramHost.Children.Add(new TextBlock
            {
                Text = "Click a button here to see it light up.",
                Width = column,
                Style = (Style)Resources["CapHintTextStyle"]
            });
        }

        private Border MouseCap(string key, string label, double width, double height)
        {
            var cap = new Border
            {
                Width = width,
                Height = height,
                Style = (Style)Resources["MouseCapStyle"],
                Child = new TextBlock
                {
                    Text = label,
                    // Shrink the label with the cap so a narrow column never clips "Right".
                    FontSize = Math.Clamp(Math.Round(width * 0.26), 7, 9.5),
                    Style = (Style)Resources["KeyCapTextStyle"]
                }
            };

            _mouseCaps[key] = cap;
            return cap;
        }

        private void HardwareTestsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _diskTest?.Cancel();
            _networkTest?.Cancel();
            StopTone();
            StopMicrophone();
            _ = StopCameraAsync();
            _http.Dispose();

            if (_keyboardHook != 0)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = 0;
                _keyboardHookProc = null;
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
            else Frame.Navigate(typeof(PcCheckerPage));
        }

        // ── CPU & RAM ────────────────────────────────────────────────────────────

        private async void RunCpuTest_Click(object sender, RoutedEventArgs e)
        {
            RunCpuButton.IsEnabled = false;
            CpuRing.IsActive = true;
            CpuResultText.Text = "Measuring… this takes a couple of seconds.";

            try
            {
                var (cpu, ram) = await Task.Run(() =>
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var cpuResult = HardwareTestService.RunCpuTest(cts.Token);
                    var ramResult = HardwareTestService.RunRamTest(cts.Token);
                    return (cpuResult, ramResult);
                });

                CpuResultText.Text =
                    $"CPU single thread : {FormatRate(cpu.SingleThreadOps)} ops/s{Environment.NewLine}" +
                    $"CPU all cores     : {FormatRate(cpu.MultiThreadOps)} ops/s on {Environment.ProcessorCount} thread(s){Environment.NewLine}" +
                    $"RAM copy speed    : {ram.GigabytesPerSecond:0.00} GB/s ({ram.BytesCopied / (1024 * 1024)} MB copied){Environment.NewLine}" +
                    $"Total time        : {cpu.Elapsed.TotalSeconds:0.0} s";
            }
            catch (Exception ex)
            {
                CpuResultText.Text = $"The test could not finish: {ex.Message}";
            }
            finally
            {
                CpuRing.IsActive = false;
                RunCpuButton.IsEnabled = true;
            }
        }

        private static string FormatRate(double value) => value switch
        {
            >= 1_000_000_000 => $"{value / 1_000_000_000:0.00} B",
            >= 1_000_000 => $"{value / 1_000_000:0.0} M",
            >= 1_000 => $"{value / 1_000:0.0} K",
            _ => $"{value:0}"
        };

        // ── Disk ─────────────────────────────────────────────────────────────────

        private readonly List<(DriveInfo Drive, DriveMediaTypeInfo Info)> _driveInfos = new();

        private void FillDrives()
        {
            DiskDriveBox.Items.Clear();
            _driveInfos.Clear();

            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;

                    var info = DiskTypeDetector.GetDriveInfo(drive.Name);
                    _driveInfos.Add((drive, info));

                    string label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? $"{drive.Name.TrimEnd('\\')} ({drive.DriveFormat})"
                        : $"{drive.Name.TrimEnd('\\')} {drive.VolumeLabel} ({drive.DriveFormat})";

                    string mediaTag = !string.IsNullOrEmpty(info.DisplaySummary) ? $" [{info.DisplaySummary}]" : "";
                    DiskDriveBox.Items.Add($"{label}{mediaTag} — {drive.AvailableFreeSpace / (1024 * 1024 * 1024)} GB free");
                }
                catch
                {
                    // A drive that refuses to report its state is simply not offered.
                }
            }

            if (DiskDriveBox.Items.Count > 0)
            {
                DiskDriveBox.SelectedIndex = 0;
            }
            else
            {
                DiskDriveBox.PlaceholderText = "No fixed drive available";
                DiskTypeDetailsText.Text = "";
            }
        }

        private void DiskDriveBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DiskDriveBox.SelectedIndex >= 0 && DiskDriveBox.SelectedIndex < _driveInfos.Count)
            {
                var (drive, info) = _driveInfos[DiskDriveBox.SelectedIndex];
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(info.Model)) parts.Add(info.Model);
                parts.Add(info.DisplaySummary);
                parts.Add($"Format: {drive.DriveFormat}");
                DiskTypeDetailsText.Text = string.Join(" • ", parts);
            }
            else
            {
                DiskTypeDetailsText.Text = "";
            }
        }

        private async void RunDiskTest_Click(object sender, RoutedEventArgs e)
        {
            if (DiskDriveBox.SelectedIndex < 0 || DiskDriveBox.SelectedIndex >= _driveInfos.Count)
            {
                DiskResultText.Text = "Choose a drive first.";
                return;
            }

            var (selectedDrive, mediaInfo) = _driveInfos[DiskDriveBox.SelectedIndex];
            var root = selectedDrive.RootDirectory.FullName;
            const long bytes = 256L * 1024 * 1024;

            if (selectedDrive.AvailableFreeSpace < bytes * 2)
            {
                DiskResultText.Text = "This drive does not have enough free space for the 256 MB test file.";
                return;
            }

            _diskTest?.Cancel();
            _diskTest = new CancellationTokenSource();

            RunDiskButton.IsEnabled = false;
            DiskProgress.Value = 0;
            DiskResultText.Text = $"Writing and reading {bytes / (1024 * 1024)} MB on {root} ({mediaInfo.DisplaySummary})…";

            try
            {
                var progress = new Progress<double>(value => DiskProgress.Value = value);
                var result = await HardwareTestService.RunDiskTestAsync(root, bytes, 1024 * 1024, progress, _diskTest.Token);

                string modelPart = !string.IsNullOrEmpty(mediaInfo.Model) ? $" ({mediaInfo.Model})" : "";
                DiskResultText.Text =
                    $"Write : {result.WriteMegabytesPerSecond:0.0} MB/s{Environment.NewLine}" +
                    $"Read  : {result.ReadMegabytesPerSecond:0.0} MB/s{Environment.NewLine}" +
                    $"Media : {mediaInfo.DisplaySummary}{modelPart}{Environment.NewLine}" +
                    $"Tested {result.Bytes / (1024 * 1024)} MB on {result.Folder}{Environment.NewLine}" +
                    "The temporary file was deleted.";
            }
            catch (OperationCanceledException)
            {
                DiskResultText.Text = "The disk test was cancelled.";
            }
            catch (Exception ex)
            {
                DiskResultText.Text = $"The disk test could not finish: {ex.Message}";
            }
            finally
            {
                RunDiskButton.IsEnabled = true;
                DiskProgress.Value = 0;
            }
        }

        // ── Network ──────────────────────────────────────────────────────────────

        private async void RunNetworkTest_Click(object sender, RoutedEventArgs e)
        {
            _networkTest?.Cancel();
            _networkTest = new CancellationTokenSource();

            RunNetworkButton.IsEnabled = false;
            NetworkProgress.Value = 0;
            NetworkResultText.Text = "Downloading the 25 MB test file…";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://speed.cloudflare.com/__down?bytes=25000000");
                request.Headers.UserAgent.ParseAdd("AppLock-PcChecker/1.0");

                var progress = new Progress<double>(value => NetworkProgress.Value = value);
                var result = await Task.Run(() => HardwareTestService.RunNetworkTestAsync(
                    _http, 25_000_000, progress, _networkTest.Token));

                NetworkResultText.Text =
                    $"Download: {result.MegabitsPerSecond:0.0} Mbit/s{Environment.NewLine}" +
                    $"That is about {result.MegabitsPerSecond / 8:0.0} MB/s{Environment.NewLine}" +
                    $"Transferred {result.Bytes / 1_000_000} MB in {result.Elapsed.TotalSeconds:0.0} s from {result.Host}";
            }
            catch (OperationCanceledException)
            {
                NetworkResultText.Text = "The speed test was cancelled or timed out.";
            }
            catch (HttpRequestException ex)
            {
                NetworkResultText.Text = $"Could not reach the test server: {ex.Message}\nCheck the internet connection and try again.";
            }
            catch (Exception ex)
            {
                NetworkResultText.Text = $"The speed test could not finish: {ex.Message}";
            }
            finally
            {
                RunNetworkButton.IsEnabled = true;
                NetworkProgress.Value = 0;
            }
        }

        // ── Speaker ──────────────────────────────────────────────────────────────

        private void FillSpeakers()
        {
            try
            {
                int count = WaveOut.DeviceCount;
                for (int i = 0; i < count; i++)
                {
                    var capabilities = WaveOut.GetCapabilities(i);
                    SpeakerBox.Items.Add($"{i}: {capabilities.ProductName}");
                }
            }
            catch (Exception ex)
            {
                SpeakerBox.PlaceholderText = $"No speaker devices: {ex.Message}";
            }

            if (SpeakerBox.Items.Count > 0) SpeakerBox.SelectedIndex = 0;
            else SpeakerBox.PlaceholderText = "No speaker devices reported";
        }

        private void PlayTone_Click(object sender, RoutedEventArgs e)
        {
            StopTone();

            try
            {
                int device = Math.Max(0, SpeakerBox.SelectedIndex);
                _toneOutput = new WaveOutEvent { DeviceNumber = device };

                var tone = new SignalGenerator(44100, 1)
                {
                    Gain = 0.2,
                    Frequency = 440,
                    Type = SignalGeneratorType.Sin
                };

                _toneOutput.Init(tone);
                _toneOutput.Play();

                PlayToneButton.IsEnabled = false;
                StopToneButton.IsEnabled = true;
                SpeakerStatusText.Text = "Playing a 440 Hz tone for 3 seconds…";

                _ = StopToneAfterDelayAsync();
            }
            catch (Exception ex)
            {
                SpeakerStatusText.Text = $"The tone could not play: {ex.Message}";
                StopTone();
            }
        }

        private async Task StopToneAfterDelayAsync()
        {
            await Task.Delay(3000);
            if (!StopToneButton.IsEnabled) return;

            StopTone();
            SpeakerStatusText.Text = "Tone finished. If you did not hear it, check the cable and the Windows volume mixer.";
        }

        private void StopTone_Click(object sender, RoutedEventArgs e)
        {
            StopTone();
            SpeakerStatusText.Text = "Stopped.";
        }

        private void StopTone()
        {
            try
            {
                _toneOutput?.Stop();
                _toneOutput?.Dispose();
            }
            catch { }

            _toneOutput = null;
            PlayToneButton.IsEnabled = true;
            StopToneButton.IsEnabled = false;
        }

        // ── Microphone ───────────────────────────────────────────────────────────

        private void FillMicrophones()
        {
            try
            {
                int count = WaveIn.DeviceCount;
                for (int i = 0; i < count; i++)
                {
                    var capabilities = WaveIn.GetCapabilities(i);
                    MicrophoneBox.Items.Add($"{i}: {capabilities.ProductName}");
                }
            }
            catch (Exception ex)
            {
                MicrophoneBox.PlaceholderText = $"No microphone devices: {ex.Message}";
            }

            if (MicrophoneBox.Items.Count > 0) MicrophoneBox.SelectedIndex = 0;
            else MicrophoneBox.PlaceholderText = "No microphone devices reported";
        }

        private void StartMicTest_Click(object sender, RoutedEventArgs e)
        {
            StopMicrophone();

            try
            {
                _microphone = new WaveInEvent
                {
                    DeviceNumber = Math.Max(0, MicrophoneBox.SelectedIndex),
                    WaveFormat = new WaveFormat(44100, 16, 1),
                    BufferMilliseconds = 100
                };

                _microphone.DataAvailable += OnMicrophoneData;
                _microphone.StartRecording();

                StartMicButton.IsEnabled = false;
                StopMicButton.IsEnabled = true;
                MicStatusText.Text = "Listening… speak normally and watch the bar.";
            }
            catch (Exception ex)
            {
                MicStatusText.Text = $"The microphone could not start: {ex.Message}";
                StopMicrophone();
            }
        }

        private void OnMicrophoneData(object? sender, WaveInEventArgs e)
        {
            float peak = 0;
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                short sample = BitConverter.ToInt16(e.Buffer, i);
                float level = Math.Abs(sample / 32768f);
                if (level > peak) peak = level;
            }

            int percent = (int)Math.Round(Math.Min(1f, peak * 1.6f) * 100);
            DispatcherQueue.TryEnqueue(() => MicLevelBar.Value = percent);
        }

        private void StopMicTest_Click(object sender, RoutedEventArgs e)
        {
            StopMicrophone();
            MicStatusText.Text = "Stopped. The recording was never written to disk.";
        }

        private void StopMicrophone()
        {
            try
            {
                if (_microphone != null)
                {
                    _microphone.DataAvailable -= OnMicrophoneData;
                    _microphone.StopRecording();
                    _microphone.Dispose();
                }
            }
            catch { }

            _microphone = null;
            StartMicButton.IsEnabled = true;
            StopMicButton.IsEnabled = false;
            MicLevelBar.Value = 0;
        }

        // ── Keyboard & mouse ─────────────────────────────────────────────────────

        private void InputPad_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            InputPad.Focus(FocusState.Programmatic);

            var point = e.GetCurrentPoint(InputPad);
            var properties = point.Properties;

            if (properties.IsLeftButtonPressed) _leftClicks++;
            else if (properties.IsRightButtonPressed) _rightClicks++;
            else if (properties.IsMiddleButtonPressed || properties.IsXButton1Pressed || properties.IsXButton2Pressed) _otherClicks++;

            LastInputText.Text = $"Mouse at {point.Position.X:0}, {point.Position.Y:0} — " +
                                 (properties.IsLeftButtonPressed ? "left button"
                                     : properties.IsRightButtonPressed ? "right button"
                                     : properties.IsMiddleButtonPressed ? "middle button"
                                     : properties.IsXButton1Pressed ? "back button"
                                     : properties.IsXButton2Pressed ? "forward button"
                                     : "pointer down");

            HighlightMouseButtons(properties);
            UpdateInputCounts();
        }

        private void InputPad_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            ResetMouseCaps();
        }

        private void InputPad_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            ResetMouseCaps();
        }

        private void InputPad_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // Hover alone is not a click; only report movement while a button is held.
            var point = e.GetCurrentPoint(InputPad);
            var properties = point.Properties;
            HighlightMouseButtons(properties);

            if (!properties.IsLeftButtonPressed && !properties.IsRightButtonPressed &&
                !properties.IsMiddleButtonPressed && !properties.IsXButton1Pressed && !properties.IsXButton2Pressed)
            {
                return;
            }

            LastInputText.Text = $"Dragging at {point.Position.X:0}, {point.Position.Y:0}";
        }

        private void HardwareTestsPage_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (!ReferenceEquals(TestsTabs.SelectedItem, KeyboardTab)) return;
            if (_keyboardHook != 0) return; // the low-level hook handles every physical key

            RegisterKeyDown(e.Key.ToString(), e.Key);
        }

        private void HardwareTestsPage_KeyUp(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (!ReferenceEquals(TestsTabs.SelectedItem, KeyboardTab)) return;
            if (_keyboardHook != 0) return;

            RegisterKeyUp(e.Key.ToString());
        }

        private void RegisterKeyDown(string name, Windows.System.VirtualKey key)
        {
            _keyCount++;

            if (!_heldKeys.Contains(name)) _heldKeys.Add(name);
            _testedKeys.Add(name);

            if (_keyCaps.TryGetValue(name, out var cap)) ApplyKeyCapState(cap);

            LastInputText.Text = $"Key: {DescribeKey(name)} (virtual key {(int)key})";
            UpdateInputCounts();
            UpdateHeldKeys();
            UpdateCoverage();
        }

        private void RegisterKeyUp(string name)
        {
            _heldKeys.Remove(name);

            if (_keyCaps.TryGetValue(name, out var cap)) ApplyKeyCapState(cap);

            UpdateHeldKeys();
        }

        private void InputPad_LostFocus(object sender, RoutedEventArgs e)
        {
            // A key held while focus left would stay lit forever; let go of every cap and keep the coverage.
            _heldKeys.Clear();
            foreach (var cap in _keyCaps.Values) ApplyKeyCapState(cap);
            ResetMouseCaps();
            UpdateHeldKeys();
        }

        /// <summary>
        /// Three states per cap: held (accent), already tested (normal), never tested (faint). The faint caps are
        /// what makes it obvious at a glance which keys still need pressing.
        /// </summary>
        private void ApplyKeyCapState(Border cap)
        {
            string name = cap.Tag as string ?? string.Empty;
            bool held = name.Length > 0 && _heldKeys.Contains(name, StringComparer.OrdinalIgnoreCase);
            bool tested = name.Length > 0 && _testedKeys.Contains(name);

            if (cap.Child is TextBlock text)
                text.Style = (Style)Resources[held ? "KeyCapHeldTextStyle" : "KeyCapTextStyle"];

            cap.Style = (Style)Resources[held ? "KeyCapHeldStyle" : "KeyCapStyle"];
            cap.Opacity = held || tested ? 1 : 0.42;
        }

        private void UpdateCoverage()
        {
            int total = _keyCaps.Count;
            int tested = _keyCaps.Keys.Count(_testedKeys.Contains);
            CoverageText.Text = tested == 0
                ? $"Keys tested: 0 of {total} — faint keys have not been pressed yet"
                : $"Keys tested: {tested} of {total}" + (tested == total ? " — every key covered" : " — faint keys have not been pressed yet");
        }

        /// <summary>Mirrors the pressed state of every mouse button onto the diagram.</summary>
        private void HighlightMouseButtons(Microsoft.UI.Input.PointerPointProperties properties)
        {
            SetMouseCap("left", properties.IsLeftButtonPressed);
            SetMouseCap("middle", properties.IsMiddleButtonPressed);
            SetMouseCap("right", properties.IsRightButtonPressed);
            SetMouseCap("back", properties.IsXButton1Pressed);
            SetMouseCap("forward", properties.IsXButton2Pressed);
        }

        private void SetMouseCap(string key, bool down)
        {
            if (!_mouseCaps.TryGetValue(key, out var cap)) return;

            cap.Style = (Style)Resources[down ? "MouseCapHeldStyle" : "MouseCapStyle"];
            if (cap.Child is TextBlock text)
                text.Style = (Style)Resources[down ? "KeyCapHeldTextStyle" : "KeyCapTextStyle"];
        }

        private void ResetMouseCaps()
        {
            foreach (var key in _mouseCaps.Keys.ToList()) SetMouseCap(key, false);
        }

        private static string DescribeKey(string keyName) => keyName switch
        {
            "CapitalLock" => "Caps Lock",
            "NumberKeyLock" => "Num Lock",
            "Snapshot" => "Print Screen",
            "OemTilde" => "`",
            "OemMinus" => "-",
            "OemPlus" => "=",
            "OemOpenBrackets" => "[",
            "OemCloseBrackets" => "]",
            "OemPipe" => "\\",
            "OemSemicolon" => ";",
            "OemQuotes" => "'",
            "OemComma" => ",",
            "OemPeriod" => ".",
            "OemQuestion" => "/",
            "Space" => "Space",
            "Control" => "Left Ctrl",
            "RightControl" => "Right Ctrl",
            "LeftWindows" => "Left Win",
            "RightWindows" => "Right Win",
            "Menu" => "Left Alt",
            "RightMenu" => "Right Alt",
            "Application" => "Menu",
            "LeftShift" => "Left Shift",
            "RightShift" => "Right Shift",
            "NumberPadEnter" => "Numpad Enter",
            _ => keyName.StartsWith("NumberPad", StringComparison.Ordinal) ? $"Numpad {keyName[9..]}" : keyName
        };

        private void UpdateHeldKeys()
        {
            HeldKeysText.Text = _heldKeys.Count == 0
                ? "Keys held: none"
                : "Keys held: " + string.Join(" + ", _heldKeys.Select(DescribeKey));
        }

        private void UpdateInputCounts()
        {
            InputCountText.Text = $"Keys: {_keyCount} left clicks: {_leftClicks} right clicks: {_rightClicks} other buttons: {_otherClicks}";
        }

        private void ResetInput_Click(object sender, RoutedEventArgs e)
        {
            _keyCount = _leftClicks = _rightClicks = _otherClicks = 0;
            _heldKeys.Clear();
            _testedKeys.Clear();
            foreach (var cap in _keyCaps.Values) ApplyKeyCapState(cap);

            ResetMouseCaps();
            LastInputText.Text = "No input yet.";
            UpdateInputCounts();
            UpdateHeldKeys();
            UpdateCoverage();
        }

        // ── Camera ───────────────────────────────────────────────────────────────

        private async void StartCamera_Click(object sender, RoutedEventArgs e)
        {
            StartCameraButton.IsEnabled = false;
            CameraStatusText.Text = "Starting the camera…";

            try
            {
                _camera = new MediaCapture();
                await _camera.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    SharingMode = MediaCaptureSharingMode.ExclusiveControl
                });

                var frameSource = _camera.FrameSources.Values
                    .FirstOrDefault(source => source.Info.SourceKind == MediaFrameSourceKind.Color);

                if (frameSource == null)
                    throw new InvalidOperationException("No camera video source was found.");

                // MediaPlayer renders camera frames itself. Pulling single frames into a SoftwareBitmap and
                // pushing them into an Image only produced a blank surface, because the frames arrive in a
                // pixel format the XAML image source rejects.
                _cameraPlayer = new MediaPlayer { RealTimePlayback = true };
                _cameraPlayer.Source = MediaSource.CreateFromMediaFrameSource(frameSource);
                CameraPreview.SetMediaPlayer(_cameraPlayer);
                _cameraPlayer.Play();

                StopCameraButton.IsEnabled = true;
                CameraStatusText.Text = "Preview running. Nothing is recorded or saved.";
            }
            catch (Exception ex)
            {
                CameraStatusText.Text = $"The camera could not start: {ex.Message}\n" +
                                        "Check Windows Settings › Privacy › Camera for app access, and that no other app is using it.";
                await StopCameraAsync();
            }
        }

        private async void StopCamera_Click(object sender, RoutedEventArgs e)
        {
            await StopCameraAsync();
            CameraStatusText.Text = "Stopped.";
        }

        private async Task StopCameraAsync()
        {
            try
            {
                _cameraPlayer?.Pause();
                CameraPreview.SetMediaPlayer(null);
                _cameraPlayer?.Dispose();
            }
            catch { }

            _cameraPlayer = null;

            try { _camera?.Dispose(); } catch { }
            _camera = null;

            StartCameraButton.IsEnabled = true;
            StopCameraButton.IsEnabled = false;

            await Task.CompletedTask;
        }

        // ── Touch & pen ──────────────────────────────────────────────────────────

        private void DescribeTouch()
        {
            try
            {
                int digitizer = GetSystemMetrics(SM_DIGITIZER);
                int maxTouches = GetSystemMetrics(SM_MAXIMUMTOUCHES);

                bool touch = (digitizer & (NID_INTEGRATED_TOUCH | NID_EXTERNAL_TOUCH)) != 0;
                bool pen = (digitizer & (NID_INTEGRATED_PEN | NID_EXTERNAL_PEN)) != 0;
                bool ready = (digitizer & NID_READY) != 0;
                bool multi = (digitizer & NID_MULTI_INPUT) != 0;

                var lines = new List<string>
                {
                    $"Touch input : {(touch ? "Yes — this display accepts touch" : "No touch digitizer reported")}",
                    $"Pen / stylus: {(pen ? "Yes — pen input is present" : "No pen digitizer reported")}",
                    $"Finger limit: {(touch ? $"{Math.Max(1, maxTouches)} contact(s) at once" : "not applicable")}",
                    $"Multi-touch : {(multi ? "Supported" : "Not supported")}",
                    $"Driver ready: {(ready ? "Yes" : "Digitizer reports no ready state")}"
                };

                TouchSummaryText.Text = string.Join(Environment.NewLine, lines) +
                    $"{Environment.NewLine}{Environment.NewLine}Source: Windows system metrics (SM_DIGITIZER, SM_MAXIMUMTOUCHES)";
            }
            catch (Exception ex)
            {
                TouchSummaryText.Text = $"Could not read the touch capabilities: {ex.Message}";
            }
        }
    }
}
