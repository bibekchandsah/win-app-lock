using System;
using System.Text;
using Microsoft.Win32;

namespace AppLock.Services.PcChecker;

/// <summary>
/// Reads the Windows product key Windows itself stores in the registry (the same DigitalProductId that
/// licensing tools decode) and turns it back into the familiar 25-character form.
///
/// This is the key installed on this PC, not a proof of purchase: a digital licence bought from the Store is
/// tied to the Microsoft account and the hardware, and the value here can be a generic upgrade key.
/// </summary>
public static class ProductKeyReader
{
    private const string KeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public static string? ReadInstalledKey()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(KeyPath, writable: false);
            if (key?.GetValue("DigitalProductId") is not byte[] id || id.Length < 66) return null;

            return Decode(id);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PcChecker] Product key read failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Decodes the 15-byte base-24 packed key starting at offset 52 of the DigitalProductId. Windows 8 and
    /// later store the position of the "N" placeholder in the last bytes, so the plain 25 characters have to
    /// be reshaped the same way Windows does before they form a valid key.
    /// </summary>
    private static string Decode(byte[] digitalProductId)
    {
        const string digits = "BCDFGHJKMPQRTVWXY2346789";
        const int keyOffset = 52;

        var buffer = (byte[])digitalProductId.Clone();

        // Windows 8+ style keys are flagged in byte 66; bit 3 of the same byte is a marker Windows clears
        // before the packed digits are decoded. Reading it wrongly shifts every character of the result.
        int windows8Style = (buffer[66] / 6) & 1;
        buffer[66] = (byte)(buffer[66] & 0xF7);

        var characters = new char[25];
        int lastDigit = 0;

        for (int i = 24; i >= 0; i--)
        {
            int current = 0;
            for (int j = 14; j >= 0; j--)
            {
                current = (current << 8) + buffer[keyOffset + j];
                buffer[keyOffset + j] = (byte)(current / 24);
                current %= 24;
            }

            characters[i] = digits[current];
            lastDigit = current;
        }

        var raw = new string(characters);

        if (windows8Style == 1)
        {
            // Windows 8+ keys drop the first decoded character and put the "N" placeholder where the last
            // division remainder says. Without this the key is valid-looking but simply wrong.
            int position = Math.Clamp(lastDigit, 0, raw.Length - 2);
            raw = raw.Substring(1, position) + "N" + raw.Substring(position + 1);
        }

        var formatted = new StringBuilder(29);
        for (int i = 0; i < raw.Length; i++)
        {
            if (i > 0 && i % 5 == 0) formatted.Append('-');
            formatted.Append(raw[i]);
        }

        return formatted.ToString();
    }

    /// <summary>
    /// True when the decoded key ends with the same five characters Windows reports for the installed licence.
    /// Used as a sanity check: a mismatch means the decoded key should not be presented as the real one.
    /// </summary>
    public static bool MatchesPartialKey(string decodedKey, string? partialKey)
    {
        if (string.IsNullOrWhiteSpace(partialKey)) return true;

        var compact = decodedKey.Replace("-", string.Empty, StringComparison.Ordinal);
        var expected = partialKey.Trim().Replace("-", string.Empty, StringComparison.Ordinal);
        if (expected.Length == 0 || compact.Length < expected.Length) return false;

        return compact.EndsWith(expected, StringComparison.OrdinalIgnoreCase);
    }
}
