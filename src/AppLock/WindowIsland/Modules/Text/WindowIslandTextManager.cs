using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AppLock.WindowIsland.Modules.Text;

public record TextStatistics(int Words, int Characters, int CharactersNoSpaces, int Lines);

/// <summary>
/// Milestone 14: Text Tab module (island.md §9.3 / §18).
/// Provides text transforms (UPPERCASE, lowercase, Title Case, Trim, Remove Line Breaks, Sort Lines),
/// statistics (word, character, line counts), plain text stripping, and Win+H dictation triggering.
/// </summary>
public static class WindowIslandTextManager
{
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_CONTROL = 0x11;
    private const ushort KEY_H = 0x48;
    private const ushort KEY_V = 0x56;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    /// <summary>
    /// Converts text to UPPERCASE.
    /// </summary>
    public static string ToUpperCase(string? input) => input?.ToUpperInvariant() ?? string.Empty;

    /// <summary>
    /// Converts text to lowercase.
    /// </summary>
    public static string ToLowerCase(string? input) => input?.ToLowerInvariant() ?? string.Empty;

    /// <summary>
    /// Converts text to Title Case.
    /// </summary>
    public static string ToTitleCase(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(input.ToLower());
    }

    /// <summary>
    /// Trims leading and trailing whitespace from the text and each individual line.
    /// </summary>
    public static string TrimText(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var lines = input.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var trimmedLines = lines.Select(l => l.Trim());
        return string.Join(Environment.NewLine, trimmedLines).Trim();
    }

    /// <summary>
    /// Removes line breaks by replacing CRLF, LF, CR with a single space and collapsing consecutive spaces.
    /// </summary>
    public static string RemoveLineBreaks(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        string noBreaks = Regex.Replace(input, @"\r\n?|\n", " ");
        return Regex.Replace(noBreaks, @"[ \t]+", " ").Trim();
    }

    /// <summary>
    /// Sorts lines alphabetically (case-insensitive by default).
    /// </summary>
    public static string SortLines(string? input, bool descending = false)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var lines = input.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var sorted = descending
            ? lines.OrderByDescending(l => l, StringComparer.CurrentCultureIgnoreCase)
            : lines.OrderBy(l => l, StringComparer.CurrentCultureIgnoreCase);
        return string.Join(Environment.NewLine, sorted);
    }

    /// <summary>
    /// Computes word count, character count (with and without spaces), and line count.
    /// </summary>
    public static TextStatistics ComputeStatistics(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return new TextStatistics(0, 0, 0, 0);
        }

        int charCount = input.Length;
        int noSpacesCount = input.Count(c => !char.IsWhiteSpace(c));

        var words = Regex.Matches(input, @"\S+");
        int wordCount = words.Count;

        var lines = input.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        int lineCount = lines.Length;

        return new TextStatistics(wordCount, charCount, noSpacesCount, lineCount);
    }

    /// <summary>
    /// Strips rich text formatting or markup to pure plain text.
    /// </summary>
    public static string StripFormatting(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        string plain = input;

        // Strip RTF markup if present
        if (plain.TrimStart().StartsWith("{\\rtf", StringComparison.OrdinalIgnoreCase))
        {
            plain = Regex.Replace(plain, @"\\[a-zA-Z0-9]+(-?[0-9]+)? ?", " ");
            plain = Regex.Replace(plain, @"[{}]", "");
            plain = plain.Trim();
        }

        // Strip basic HTML tags if present
        if (plain.Contains('<') && plain.Contains('>'))
        {
            plain = Regex.Replace(plain, @"<[^>]+>", " ");
            plain = Regex.Replace(plain, @"[ \t]+", " ");
        }

        return plain.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine).Trim();
    }

    /// <summary>
    /// Triggers Windows Voice Typing (Win + H).
    /// </summary>
    public static async Task TriggerDictationAsync(IntPtr targetHwnd)
    {
        if (targetHwnd != IntPtr.Zero && IsWindow(targetHwnd))
        {
            SetForegroundWindow(targetHwnd);
            await Task.Delay(100);
        }

        var inputs = new List<INPUT>(4)
        {
            Key(VK_LWIN),
            Key(KEY_H),
            KeyUp(KEY_H),
            KeyUp(VK_LWIN)
        };
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Pastes text into the target window using Ctrl + V.
    /// </summary>
    public static async Task<bool> PasteToWindowAsync(IntPtr targetHwnd)
    {
        if (targetHwnd != IntPtr.Zero && IsWindow(targetHwnd))
        {
            SetForegroundWindow(targetHwnd);
            await Task.Delay(100);
        }

        var inputs = new List<INPUT>(4)
        {
            Key(VK_CONTROL),
            Key(KEY_V),
            KeyUp(KEY_V),
            KeyUp(VK_CONTROL)
        };
        uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        return sent == inputs.Count;
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
}
