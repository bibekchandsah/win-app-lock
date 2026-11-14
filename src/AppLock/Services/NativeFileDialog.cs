using System;
using System.Runtime.InteropServices;

namespace AppLock.Services;

/// <summary>
/// Win32 common file dialogs. The WinRT pickers need package identity in some builds and failed
/// silently here, which is why "Export settings", "Import settings" and "Export CSV" did nothing.
/// </summary>
public static class NativeFileDialog
{
    private const int OFN_OVERWRITEPROMPT = 0x00000002;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_NOCHANGEDIR = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public string lpstrFile;
        public int nMaxFile;
        public string lpstrFileTitle;
        public int nMaxFileTitle;
        public string lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int flagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSaveFileNameW(ref OPENFILENAME dialog);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAME dialog);

    /// <summary>Shows a native save dialog; returns null when the user cancels.</summary>
    public static string? Save(string title, string filter, string defaultExtension, string suggestedName)
    {
        var buffer = new string('\0', 4096);

        var dialog = new OPENFILENAME
        {
            lStructSize = Marshal.SizeOf<OPENFILENAME>(),
            hwndOwner = OwnerHandle(),
            lpstrFilter = filter,
            lpstrFile = suggestedName + new string('\0', Math.Max(1, 4095 - suggestedName.Length)),
            nMaxFile = 4096,
            lpstrTitle = title,
            lpstrDefExt = defaultExtension,
            Flags = OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
        };

        if (!GetSaveFileNameW(ref dialog)) return null;

        return dialog.lpstrFile?.TrimEnd('\0');
    }

    /// <summary>Shows a native open dialog; returns null when the user cancels.</summary>
    public static string? Open(string title, string filter, string defaultExtension)
    {
        var dialog = new OPENFILENAME
        {
            lStructSize = Marshal.SizeOf<OPENFILENAME>(),
            hwndOwner = OwnerHandle(),
            lpstrFilter = filter,
            lpstrFile = new string('\0', 4096),
            nMaxFile = 4096,
            lpstrTitle = title,
            lpstrDefExt = defaultExtension,
            Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
        };

        if (!GetOpenFileNameW(ref dialog)) return null;

        return dialog.lpstrFile?.TrimEnd('\0');
    }

    private static IntPtr OwnerHandle()
    {
        try
        {
            return App.MainWindowInstance is { } window
                ? WinRT.Interop.WindowNative.GetWindowHandle(window)
                : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}
