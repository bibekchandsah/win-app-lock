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

        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

        private CancellationTokenSource? _diskTest;
        private CancellationTokenSource? _networkTest;
        private WaveOutEvent? _toneOutput;
        private WaveInEvent? _microphone;
        private MediaCapture? _camera;
        private MediaPlayer? _cameraPlayer;

        private int _keyCount;
        private int _leftClicks;
        private int _rightClicks;
        private int _otherClicks;

        /// <summary>Key caps keyed by <see cref="Windows.System.VirtualKey"/> name, so a key press can light one up.</summary>
        private readonly Dictionary<string, Border> _keyCaps = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _heldKeys = new();
        private Brush? _keyCapBackground;
        private Brush? _keyCapForeground;
        private Brush? _keyCapHighlight;

        public HardwareTestsPage()
        {
            this.InitializeComponent();

            Loaded += HardwareTestsPage_Loaded;
            Unloaded += HardwareTestsPage_Unloaded;
        }

        private void HardwareTestsPage_Loaded(object sender, RoutedEventArgs e)
        {
            FillDrives();
            FillSpeakers();
            FillMicrophones();
            DescribeTouch();
            BuildKeyboardLayout();
        }

        private void BuildKeyboardLayout()
        {
            if (_keyCaps.Count > 0) return;

            _keyCapBackground = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
            _keyCapForeground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
            _keyCapHighlight = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

            KeyboardLayoutHost.Children.Clear();
            _keyCaps.Clear();

            // label | VirtualKey name | width in key units
            var mainRows = new[]
            {
                new[] { "Esc|Escape|1.3", "F1|F1|1", "F2|F2|1", "F3|F3|1", "F4|F4|1", "F5|F5|1", "F6|F6|1",
                        "F7|F7|1", "F8|F8|1", "F9|F9|1", "F10|F10|1", "F11|F11|1", "F12|F12|1" },
                new[] { "\u0060|OemTilde|1", "1|Number1|1", "2|Number2|1", "3|Number3|1", "4|Number4|1", "5|Number5|1",
                        "6|Number6|1", "7|Number7|1", "8|Number8|1", "9|Number9|1", "0|Number0|1", "-|OemMinus|1",
                        "=|OemPlus|1", "Backspace|Back|2" },
                new[] { "Tab|Tab|1.5", "Q|Q|1", "W|W|1", "E|E|1", "R|R|1", "T|T|1", "Y|Y|1", "U|U|1", "I|I|1",
                        "O|O|1", "P|P|1", "[|OemOpenBrackets|1", "]|OemCloseBrackets|1", "\\|OemPipe|1.5" },
                new[] { "Caps|CapitalLock|1.8", "A|A|1", "S|S|1", "D|D|1", "F|F|1", "G|G|1", "H|H|1", "J|J|1",
                        "K|K|1", "L|L|1", ";|OemSemicolon|1", "'|OemQuotes|1", "Enter|Enter|2.2" },
                new[] { "Shift|LeftShift|2.3", "Z|Z|1", "X|X|1", "C|C|1", "V|V|1", "B|B|1", "N|N|1", "M|M|1",
                        ",|OemComma|1", ".|OemPeriod|1", "/|OemQuestion|1", "Shift|RightShift|2.7" },
                new[] { "Ctrl|Control|1.3", "Win|LeftWindows|1.1", "Alt|Menu|1.1", "Space|Space|6.3",
                        "Alt|RightMenu|1.1", "Win|RightWindows|1.1", "Menu|Application|1.1", "Ctrl|RightControl|1.3" }
            };

            var navigationRows = new[]
            {
                new[] { "PrtSc|Snapshot|1", "ScrLk|Scroll|1", "Pause|Pause|1" },
                new[] { "Ins|Insert|1", "Home|Home|1", "PgUp|PageUp|1" },
                new[] { "Del|Delete|1", "End|End|1", "PgDn|PageDown|1" },
                new[] { "|None|1", "\u2191|Up|1", "|None|1" },
                new[] { "\u2190|Left|1", "\u2193|Down|1", "\u2192|Right|1" }
            };

            var numpadRows = new[]
            {
                new[] { "Num|NumberKeyLock|1", "/|Divide|1", "*|Multiply|1", "-|Subtract|1" },
                new[] { "7|NumberPad7|1", "8|NumberPad8|1", "9|NumberPad9|1", "+|Add|1" },
                new[] { "4|NumberPad4|1", "5|NumberPad5|1", "6|NumberPad6|1", "|None|1" },
                new[] { "1|NumberPad1|1", "2|NumberPad2|1", "3|NumberPad3|1", "Enter|NumberPadEnter|1" },
                new[] { "0|NumberPad0|1.6", ".|Decimal|1", "|None|1.4" }
            };

            KeyboardLayoutHost.Children.Add(BuildCluster(mainRows));
            KeyboardLayoutHost.Children.Add(BuildCluster(navigationRows));
            KeyboardLayoutHost.Children.Add(BuildCluster(numpadRows));
        }

        private StackPanel BuildCluster(string[][] rows)
        {
            var cluster = new StackPanel { Spacing = 3 };

            foreach (var row in rows)
            {
                var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
                foreach (var definition in row)
                {
                    var parts = definition.Split('|');
                    string label = parts[0];
                    string keyName = parts.Length > 1 ? parts[1] : string.Empty;
                    double units = parts.Length > 2 && double.TryParse(parts[2], out var value) ? value : 1;

                    var cap = new Border
                    {
                        Width = Math.Round(26 * units) + Math.Max(0, (int)Math.Round(units) - 1) * 3,
                        Height = 26,
                        CornerRadius = new CornerRadius(4),
                        Background = parts.Length > 1 && keyName == "None" ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : _keyCapBackground,
                        BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
                        BorderThickness = new Thickness(keyName == "None" ? 0 : 1),
                        Child = new TextBlock
                        {
                            Text = label,
                            FontSize = 9.5,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = _keyCapForeground
                        }
                    };

                    if (!string.IsNullOrEmpty(keyName) && keyName != "None")
                    {
                        _keyCaps[keyName] = cap;
                    }

                    rowPanel.Children.Add(cap);
                }
                cluster.Children.Add(rowPanel);
            }

            return cluster;
        }

        private void HardwareTestsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _diskTest?.Cancel();
            _networkTest?.Cancel();
            StopTone();
            StopMicrophone();
            _ = StopCameraAsync();
            _http.Dispose();
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

        private void FillDrives()
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;

                    string label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? $"{drive.Name.TrimEnd('\\')} ({drive.DriveFormat})"
                        : $"{drive.Name.TrimEnd('\\')} {drive.VolumeLabel} ({drive.DriveFormat})";

                    DiskDriveBox.Items.Add($"{label} — {drive.AvailableFreeSpace / (1024 * 1024 * 1024)} GB free");
                }
                catch
                {
                    // A drive that refuses to report its state is simply not offered.
                }
            }

            if (DiskDriveBox.Items.Count > 0) DiskDriveBox.SelectedIndex = 0;
            else DiskDriveBox.PlaceholderText = "No fixed drive available";
        }

        private async void RunDiskTest_Click(object sender, RoutedEventArgs e)
        {
            if (DiskDriveBox.SelectedIndex < 0)
            {
                DiskResultText.Text = "Choose a drive first.";
                return;
            }

            var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToList();
            if (DiskDriveBox.SelectedIndex >= drives.Count) return;

            var root = drives[DiskDriveBox.SelectedIndex].RootDirectory.FullName;
            const long bytes = 256L * 1024 * 1024;

            if (drives[DiskDriveBox.SelectedIndex].AvailableFreeSpace < bytes * 2)
            {
                DiskResultText.Text = "This drive does not have enough free space for the 256 MB test file.";
                return;
            }

            _diskTest?.Cancel();
            _diskTest = new CancellationTokenSource();

            RunDiskButton.IsEnabled = false;
            DiskProgress.Value = 0;
            DiskResultText.Text = $"Writing and reading {bytes / (1024 * 1024)} MB on {root}…";

            try
            {
                var progress = new Progress<double>(value => DiskProgress.Value = value);
                var result = await HardwareTestService.RunDiskTestAsync(root, bytes, 1024 * 1024, progress, _diskTest.Token);

                DiskResultText.Text =
                    $"Write: {result.WriteMegabytesPerSecond:0.0} MB/s{Environment.NewLine}" +
                    $"Read : {result.ReadMegabytesPerSecond:0.0} MB/s{Environment.NewLine}" +
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

            UpdateInputCounts();
        }

        private void InputPad_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // Hover alone is not a click; only report movement while a button is held.
            var point = e.GetCurrentPoint(InputPad);
            if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed) return;

            LastInputText.Text = $"Dragging at {point.Position.X:0}, {point.Position.Y:0}";
        }

        private void InputPad_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            string name = e.Key.ToString();
            bool firstPress = HighlightKey(name, true);

            if (firstPress)
            {
                _keyCount++;
                _heldKeys.Add(name);
            }

            LastInputText.Text = $"Key: {DescribeKey(name)} (virtual key {(int)e.Key})";
            UpdateInputCounts();
            UpdateHeldKeys();
        }

        private void InputPad_KeyUp(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            string name = e.Key.ToString();
            HighlightKey(name, false);
            _heldKeys.Remove(name);
            UpdateHeldKeys();
        }

        private void InputPad_LostFocus(object sender, RoutedEventArgs e)
        {
            // A key held while focus left would stay lit forever; let go of every cap.
            foreach (var cap in _keyCaps.Values) ResetKeyCap(cap);
            _heldKeys.Clear();
            UpdateHeldKeys();
        }

        /// <summary>Lights a key cap up or restores it. Returns false when the key was already lit.</summary>
        private bool HighlightKey(string keyName, bool down)
        {
            if (!_keyCaps.TryGetValue(keyName, out var cap)) return false;

            if (!down)
            {
                ResetKeyCap(cap);
                return true;
            }

            if (cap.Tag is true) return false;

            cap.Tag = true;
            cap.Background = _keyCapHighlight;
            if (cap.Child is TextBlock text) text.Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
            return true;
        }

        private void ResetKeyCap(Border cap)
        {
            cap.Tag = null;
            cap.Background = _keyCapBackground;
            if (cap.Child is TextBlock text) text.Foreground = _keyCapForeground;
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
            foreach (var cap in _keyCaps.Values) ResetKeyCap(cap);

            LastInputText.Text = "No input yet.";
            UpdateInputCounts();
            UpdateHeldKeys();
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
