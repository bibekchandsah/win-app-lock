using Microsoft.Win32;
using System.Management;
using System.Security.Principal;

namespace AppLock.Services.BitLocker;

public class BitLockerSupportService
{
    private bool? _isSupported;
    private bool? _isElevated;
    private bool? _isWmiAvailable;
    private string? _editionId;

    public bool IsSupported => _isSupported ??= CheckSupport();
    public bool IsElevated => _isElevated ??= CheckElevation();
    public bool IsWmiAvailable => _isWmiAvailable ??= CheckWmiAvailable();
    public string EditionId => _editionId ??= GetEditionId();

    private static readonly HashSet<string> HomeEditions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Core", "CoreN", "CoreSingleLanguage", "CoreCountrySpecific"
    };

    private bool CheckSupport()
    {
        // 1. Check edition
        var edition = GetEditionId();
        if (HomeEditions.Contains(edition)) return false;

        // 2. Check manage-bde.exe
        var manageBde = Path.Combine(
            Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
            "System32", "manage-bde.exe");
        if (!File.Exists(manageBde)) return false;

        // 3. WMI check (access denied = supported but not elevated, still counts as supported)
        // IsWmiAvailable handles the distinction
        return true;
    }

    private string GetEditionId()
    {
        if (_editionId != null) return _editionId;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            _editionId = key?.GetValue("EditionID") as string ?? "Unknown";
            return _editionId;
        }
        catch { _editionId = "Unknown"; return _editionId; }
    }

    private bool CheckElevation()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private bool CheckWmiAvailable()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption");
            scope.Connect();
            using var query = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Win32_EncryptableVolume"));
            using var results = query.Get();
            // just check we can iterate
            foreach (var _ in results) { break; }
            return true;
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            // Supported but not elevated
            _isElevated = false;
            return false; // WMI not accessible but edition is supported
        }
        catch
        {
            return false;
        }
    }

    public void Refresh()
    {
        _isSupported = null;
        _isElevated = null;
        _isWmiAvailable = null;
        _editionId = null;
    }
}
