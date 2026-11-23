using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AppLock.Island.Events;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using ZXing;
using ZXing.Common;

namespace AppLock.Island.Modules;

/// <summary>A card the Home tile wants to show, plus what its buttons need.</summary>
public sealed record TileOutcome(
    string Title,
    string Detail,
    string Icon,
    string Accent,
    string Kind = "hint",
    string? QrText = null,
    IReadOnlyList<IslandCardAction>? Actions = null);

/// <summary>
/// The Home tiles that close the island and send a Windows chord, capture the screen, or scan a QR
/// code (island.md 7). Every chord is sent through SendInput so the target app sees a real key press.
/// </summary>
public sealed class IslandTileActions
{
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_LWIN = 0x5B;
    private const ushort KEY_S = 0x53;
    private const ushort KEY_T = 0x54;
    private const ushort KEY_R = 0x52;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    /// <summary>
    /// The Snipping Tool's own protocol. Handing it the source makes the tool open the right mode
    /// (rectangle snip, screen recording, text extraction) instead of guessing from a key chord.
    /// </summary>
    private const string SnippingAreaUri = "ms-screenclip:///?source=PrintScreen";
    private const string SnippingRecordUri = "ms-screenclip:///?source=ScreenRecorderHotKey&type=recording";
    private const string SnippingTextUri = "ms-screenclip:///?source=TextExtractorHotKey&type=textExtractor";

    private readonly Func<Task> _hideIsland;
    private readonly Func<Task> _showIsland;
    private readonly Func<string> _screenshotsFolder;

    public IslandTileActions(Func<Task> hideIsland, Func<Task> showIsland, Func<string> screenshotsFolder)
    {
        _hideIsland = hideIsland;
        _showIsland = showIsland;
        _screenshotsFolder = screenshotsFolder;
    }

    /// <summary>Runs a tile. A null result means the tile only closed the island and sent a chord.</summary>
    public async Task<TileOutcome?> RunAsync(string id)
    {
        switch (id)
        {
            case "area":
                await OpenSnippingToolAsync(SnippingAreaUri, KEY_S, 1200);
                return null;

            case "record":
                await OpenSnippingToolAsync(SnippingRecordUri, KEY_R, 1400);
                return null;

            case "screentext":
                await OpenSnippingToolAsync(SnippingTextUri, KEY_T, 1200);
                return null;

            case "fullscreen":
                return await CaptureScreenAsync();

            case "qr":
                return await ScanQrAsync();

            default:
                return null;
        }
    }

    // ── Chords ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Hides the island, then asks the Snipping Tool for the requested mode through its protocol.
    /// The Win + Shift chord is only a fallback, for a machine where the protocol is not registered.
    /// </summary>
    private async Task OpenSnippingToolAsync(string uri, ushort fallbackKey, int holdMs)
    {
        try
        {
            await _hideIsland();

            // The island has to be gone before the capture UI opens, or it shows up in it.
            await Task.Delay(180);

            if (!TryActivateProtocol(uri))
            {
                SendWinShift(fallbackKey);
            }

            await Task.Delay(holdMs);
        }
        finally
        {
            await _showIsland();
        }
    }

    private static bool TryActivateProtocol(string uri)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return process != null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Island] protocol launch failed for {uri}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Win + Shift + <paramref name="key"/>, with the modifiers released last.</summary>
    private static void SendWinShift(ushort key)
    {
        var inputs = new List<INPUT>(8)
        {
            Key(VK_LWIN), Key(VK_SHIFT), Key(key),
        };
        inputs.Add(KeyUp(key));
        inputs.Add(KeyUp(VK_SHIFT));
        inputs.Add(KeyUp(VK_LWIN));

        uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        if (sent != inputs.Count)
        {
            Debug.WriteLine($"[Island] SendInput sent {sent} of {inputs.Count} events");
        }
    }

    private static INPUT Key(ushort vk) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk } },
    };

    private static INPUT KeyUp(ushort vk) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } },
    };

    // ── Full screen screenshot ───────────────────────────────────────────────

    private async Task<TileOutcome?> CaptureScreenAsync()
    {
        string? fileName = null;
        string? error = null;

        try
        {
            await _hideIsland();
            await Task.Delay(180);

            var bounds = ScreenCapture.MonitorUnderCursor();
            using Bitmap bitmap = ScreenCapture.Capture(bounds);

            string folder = ResolveFolder(_screenshotsFolder());
            Directory.CreateDirectory(folder);
            fileName = $"Island_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
            string path = Path.Combine(folder, fileName);
            bitmap.Save(path, ImageFormat.Png);

            await CopyImageToClipboardAsync(path);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Logger.Write($"tile: full screen failed ({ex.GetType().Name}: {ex.Message})");
        }
        finally
        {
            await _showIsland();
        }

        return error == null
            ? new TileOutcome("Screenshot saved", fileName ?? string.Empty, "\uE722", "#FFFFFF", "screenshot")
            : new TileOutcome("Screenshot failed", error, "\uE722", "#FF8A80");
    }

    private static string ResolveFolder(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots", "Island");
    }

    private static async Task CopyImageToClipboardAsync(string path)
    {
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            var package = new DataPackage();
            package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            // The file is already saved; a clipboard failure is not worth a failed tile.
            Logger.Write($"tile: clipboard image copy failed ({ex.GetType().Name})");
        }
    }

    // ── QR code ──────────────────────────────────────────────────────────────

    private async Task<TileOutcome?> ScanQrAsync()
    {
        string? text = null;
        string? error = null;

        try
        {
            await _hideIsland();
            await Task.Delay(180);

            var bounds = ScreenCapture.MonitorUnderCursor();
            using Bitmap bitmap = ScreenCapture.Capture(bounds);
            text = DecodeQr(bitmap);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Logger.Write($"tile: qr capture failed ({ex.GetType().Name}: {ex.Message})");
        }
        finally
        {
            await _showIsland();
        }

        if (error != null) return new TileOutcome("QR code scan failed", error, "\uE9F9", "#FF8A80", "qr");

        if (string.IsNullOrWhiteSpace(text))
        {
            return new TileOutcome("No QR code found on screen", string.Empty, "\uE9F9", "#FFFFFF", "qr");
        }

        bool isLink = Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
                      && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        var actions = new List<IslandCardAction>();
        if (isLink) actions.Add(new IslandCardAction("qr.open", "Open", true));
        actions.Add(new IslandCardAction("qr.copy", "Copy"));

        Logger.Write($"tile: qr found ({text.Length} chars)");
        return new TileOutcome("QR code", Trim(text, 90), "\uE9F9", "#FFFFFF", "qr", text, actions);
    }

    private static string Trim(string value, int max)
    {
        string single = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= max ? single : single[..max] + "\u2026";
    }

    private static string? DecodeQr(Bitmap bitmap)
    {
        byte[] pixels = ScreenCapture.ToBgra(bitmap, out int width, out int height);
        var source = new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = false,
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = new[] { BarcodeFormat.QR_CODE },
            },
        };

        return reader.Decode(source)?.Text;
    }

    /// <summary>Small log shim so the tile code does not depend on the host namespace.</summary>
    private static class Logger
    {
        public static void Write(string message) => Host.IslandSpikeLog.Write(message);
    }
}
