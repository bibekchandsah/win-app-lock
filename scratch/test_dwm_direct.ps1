Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public class DwmTestDirect {
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public uint[] bmiColors;
    }

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, uint dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetIconicThumbnail(IntPtr hwnd, IntPtr hbmp, uint dwSITFlags);
}
"@

Start-Process notepad
Start-Sleep -Milliseconds 1500

$proc = Get-Process notepad | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$hwnd = $proc.MainWindowHandle
Write-Host "Notepad HWND: 0x$($hwnd.ToString('X'))"

$val = 1
$hr1 = [DwmTestDirect]::DwmSetWindowAttribute($hwnd, 7, [ref]$val, 4)
Write-Host "DwmSetWindowAttribute(7 FORCE_ICONIC): 0x$($hr1.ToString('X8'))"

$hr2 = [DwmTestDirect]::DwmSetWindowAttribute($hwnd, 10, [ref]$val, 4)
Write-Host "DwmSetWindowAttribute(10 HAS_ICONIC_BITMAP): 0x$($hr2.ToString('X8'))"

# Create a 200x150 32-bit DIB (bottom-up: biHeight = +150)
$bmi = New-Object DwmTestDirect+BITMAPINFO
$bmi.bmiHeader.biSize = [uint32][System.Runtime.InteropServices.Marshal]::SizeOf([type][DwmTestDirect+BITMAPINFOHEADER])
$bmi.bmiHeader.biWidth = 200
$bmi.bmiHeader.biHeight = 150
$bmi.bmiHeader.biPlanes = 1
$bmi.bmiHeader.biBitCount = 32
$bmi.bmiHeader.biCompression = 0
$bmi.bmiHeader.biSizeImage = 200 * 150 * 4

$pBits = [IntPtr]::Zero
$hbmp = [DwmTestDirect]::CreateDIBSection([IntPtr]::Zero, [ref]$bmi, 0, [ref]$pBits, [IntPtr]::Zero, 0)
Write-Host "CreateDIBSection (+150): 0x$($hbmp.ToString('X'))"

$hrThumb = [DwmTestDirect]::DwmSetIconicThumbnail($hwnd, $hbmp, 0)
Write-Host "DwmSetIconicThumbnail cross-process (+150): 0x$($hrThumb.ToString('X8'))"

[DwmTestDirect]::DeleteObject($hbmp)

# Create a 200x150 32-bit DIB (top-down: biHeight = -150)
$bmi.bmiHeader.biHeight = -150
$hbmp2 = [DwmTestDirect]::CreateDIBSection([IntPtr]::Zero, [ref]$bmi, 0, [ref]$pBits, [IntPtr]::Zero, 0)
$hrThumb2 = [DwmTestDirect]::DwmSetIconicThumbnail($hwnd, $hbmp2, 0)
Write-Host "DwmSetIconicThumbnail cross-process (-150): 0x$($hrThumb2.ToString('X8'))"

[DwmTestDirect]::DeleteObject($hbmp2)

$proc.Kill()
