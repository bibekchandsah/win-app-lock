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

    /// <summary>Decodes the 15-byte base-24 packed key starting at offset 52 of the DigitalProductId.</summary>
    private static string Decode(byte[] digitalProductId)
    {
        const string digits = "BCDFGHJKMPQRTVWXY2346789";
        const int keyOffset = 52;

        var buffer = (byte[])digitalProductId.Clone();
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

        // Windows 8 and later move one digit out of the key and keep its position in the third-to-last byte;
        // the missing spot is shown as "N". Older keys decode straight into the 25 characters.
        bool windows8Style = ((buffer[66] >> 3) & 1) == 1;
        if (windows8Style && lastDigit is >= 0 and < 25)
        {
            raw = raw.Insert(lastDigit, "N");
        }

        var formatted = new StringBuilder(29);
        for (int i = 0; i < raw.Length; i++)
        {
            if (i > 0 && i % 5 == 0) formatted.Append('-');
            formatted.Append(raw[i]);
        }

        return formatted.ToString();
    }
}
