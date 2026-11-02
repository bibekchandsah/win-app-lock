using System.Diagnostics;
using System.IO;
using System.Management;
using AppLock.Models;

namespace AppLock.Services.BitLocker;

public class BitLockerService : IBitLockerService
{
    private readonly BitLockerSupportService _supportService;

    public BitLockerService(BitLockerSupportService supportService)
    {
        _supportService = supportService;
    }

    public async Task<IReadOnlyList<BitLockerDrive>> GetDrivesAsync(CancellationToken ct = default)
    {
        return await Task.Run(() => QueryDrives(ct), ct);
    }

    public async Task<BitLockerActionResult> UnlockWithPassphraseAsync(
        string driveLetter,
        string passphrase,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(driveLetter))
            return BitLockerActionResult.Fail("Drive letter is required.");

        if (string.IsNullOrEmpty(passphrase))
            return BitLockerActionResult.Fail("Password cannot be empty.");

        var normalized = NormalizeDriveLetter(driveLetter);

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var scope = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption");
                scope.Connect();

                using var searcher = new ManagementObjectSearcher(
                    scope,
                    new ObjectQuery($"SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter = '{normalized}'"));
                using var collection = searcher.Get();

                ManagementObject? volumeMo = null;
                foreach (ManagementObject mo in collection)
                {
                    volumeMo = mo;
                    break;
                }

                if (volumeMo != null)
                {
                    using (volumeMo)
                    {
                        using var inParams = volumeMo.GetMethodParameters("UnlockWithPassphrase");
                        inParams["Passphrase"] = passphrase;
                        using var outParams = volumeMo.InvokeMethod("UnlockWithPassphrase", inParams, null);

                        if (outParams != null && outParams["ReturnValue"] != null)
                        {
                            var returnVal = Convert.ToUInt32(outParams["ReturnValue"]);
                            if (returnVal == 0)
                            {
                                return BitLockerActionResult.Ok();
                            }

                            return BitLockerActionResult.Fail(
                                GetBitLockerErrorMessage(returnVal),
                                unchecked((int)returnVal));
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WMI UnlockWithPassphrase failed: {ex.Message}. Attempting PowerShell fallback.");
            }

            // Fallback to PowerShell SDK without putting password on command line
            return UnlockWithPassphrasePowerShell(normalized, passphrase, ct);
        }, ct);
    }

    public async Task<BitLockerActionResult> UnlockWithNumericalPasswordAsync(
        string driveLetter,
        string numericalPassword,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(driveLetter))
            return BitLockerActionResult.Fail("Drive letter is required.");

        var formattedKey = NormalizeRecoveryKey(numericalPassword);
        if (formattedKey == null)
            return BitLockerActionResult.Fail("The BitLocker recovery key must contain 48 digits.");

        var normalized = NormalizeDriveLetter(driveLetter);

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var scope = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption");
                scope.Connect();

                using var searcher = new ManagementObjectSearcher(
                    scope,
                    new ObjectQuery($"SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter = '{normalized}'"));
                using var collection = searcher.Get();

                ManagementObject? volumeMo = null;
                foreach (ManagementObject mo in collection)
                {
                    volumeMo = mo;
                    break;
                }

                if (volumeMo != null)
                {
                    using (volumeMo)
                    {
                        using var inParams = volumeMo.GetMethodParameters("UnlockWithNumericalPassword");
                        inParams["NumericalPassword"] = formattedKey;
                        using var outParams = volumeMo.InvokeMethod("UnlockWithNumericalPassword", inParams, null);

                        if (outParams != null && outParams["ReturnValue"] != null)
                        {
                            var returnVal = Convert.ToUInt32(outParams["ReturnValue"]);
                            if (returnVal == 0)
                            {
                                return BitLockerActionResult.Ok();
                            }

                            return BitLockerActionResult.Fail(
                                GetBitLockerErrorMessage(returnVal),
                                unchecked((int)returnVal));
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WMI UnlockWithNumericalPassword failed: {ex.Message}. Attempting PowerShell fallback.");
            }

            // Fallback to PowerShell SDK
            return UnlockWithRecoveryKeyPowerShell(normalized, formattedKey, ct);
        }, ct);
    }

    public async Task<BitLockerActionResult> LockAsync(
        string driveLetter,
        bool forceDismount = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(driveLetter))
            return BitLockerActionResult.Fail("Drive letter is required.");

        var normalized = NormalizeDriveLetter(driveLetter);

        if (IsOperatingSystemOrAppDrive(normalized))
        {
            return BitLockerActionResult.Fail(
                "The operating system drive and the drive running AppLock cannot be locked.");
        }

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var scope = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption");
                scope.Connect();

                using var searcher = new ManagementObjectSearcher(
                    scope,
                    new ObjectQuery($"SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter = '{normalized}'"));
                using var collection = searcher.Get();

                ManagementObject? volumeMo = null;
                foreach (ManagementObject mo in collection)
                {
                    volumeMo = mo;
                    break;
                }

                if (volumeMo != null)
                {
                    using (volumeMo)
                    {
                        using var inParams = volumeMo.GetMethodParameters("Lock");
                        inParams["ForceDismount"] = forceDismount;
                        using var outParams = volumeMo.InvokeMethod("Lock", inParams, null);

                        if (outParams != null && outParams["ReturnValue"] != null)
                        {
                            var returnVal = Convert.ToUInt32(outParams["ReturnValue"]);
                            if (returnVal == 0)
                            {
                                return BitLockerActionResult.Ok();
                            }

                            return BitLockerActionResult.Fail(
                                GetBitLockerErrorMessage(returnVal),
                                unchecked((int)returnVal));
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WMI Lock failed: {ex.Message}. Attempting PowerShell fallback.");
            }

            // Fallback to PowerShell Lock-BitLocker
            return LockPowerShell(normalized, forceDismount, ct);
        }, ct);
    }

    private static BitLockerActionResult UnlockWithPassphrasePowerShell(
        string mountPoint,
        string passphrase,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"$p = Read-Host -AsSecureString; Unlock-BitLocker -MountPoint $env:APPLOCK_TARGET -Password $p -ErrorAction Stop\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.Environment["APPLOCK_TARGET"] = mountPoint;

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Feed password via stdin to prevent command-line visibility
            using (var writer = process.StandardInput)
            {
                writer.WriteLine(passphrase);
            }

            process.WaitForExit(15000);
            if (!process.HasExited)
            {
                process.Kill();
                return BitLockerActionResult.Fail("Unlock timed out.");
            }

            var stderr = process.StandardError.ReadToEnd();
            if (process.ExitCode == 0)
            {
                return BitLockerActionResult.Ok();
            }

            if (stderr.Contains("0x80310027") || stderr.Contains("incorrect", StringComparison.OrdinalIgnoreCase) || stderr.Contains("authentication", StringComparison.OrdinalIgnoreCase))
            {
                return BitLockerActionResult.Fail("The password provided is incorrect.", unchecked((int)0x80310027));
            }

            return BitLockerActionResult.Fail(string.IsNullOrWhiteSpace(stderr) ? "Unlock failed via PowerShell." : stderr.Trim(), process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BitLockerActionResult.Fail($"PowerShell unlock failed: {ex.Message}");
        }
    }

    private static BitLockerActionResult UnlockWithRecoveryKeyPowerShell(
        string mountPoint,
        string recoveryKey,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"$k = Read-Host; Unlock-BitLocker -MountPoint $env:APPLOCK_TARGET -RecoveryPassword $k -ErrorAction Stop\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.Environment["APPLOCK_TARGET"] = mountPoint;

            using var process = new Process { StartInfo = psi };
            process.Start();

            using (var writer = process.StandardInput)
            {
                writer.WriteLine(recoveryKey);
            }

            process.WaitForExit(15000);
            if (!process.HasExited)
            {
                process.Kill();
                return BitLockerActionResult.Fail("Unlock timed out.");
            }

            var stderr = process.StandardError.ReadToEnd();
            if (process.ExitCode == 0)
            {
                return BitLockerActionResult.Ok();
            }

            if (stderr.Contains("0x80310027") || stderr.Contains("0x80310034") || stderr.Contains("incorrect", StringComparison.OrdinalIgnoreCase))
            {
                return BitLockerActionResult.Fail("The recovery key provided is incorrect.", unchecked((int)0x80310027));
            }

            return BitLockerActionResult.Fail(string.IsNullOrWhiteSpace(stderr) ? "Recovery key unlock failed via PowerShell." : stderr.Trim(), process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BitLockerActionResult.Fail($"PowerShell recovery unlock failed: {ex.Message}");
        }
    }

    private static BitLockerActionResult LockPowerShell(
        string mountPoint,
        bool forceDismount,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var dismountArg = forceDismount ? "-ForceDismount" : "";
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"Lock-BitLocker -MountPoint $env:APPLOCK_TARGET {dismountArg} -ErrorAction Stop\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.Environment["APPLOCK_TARGET"] = mountPoint;

            using var process = new Process { StartInfo = psi };
            process.Start();

            process.WaitForExit(15000);
            if (!process.HasExited)
            {
                process.Kill();
                return BitLockerActionResult.Fail("Lock operation timed out.");
            }

            var stderr = process.StandardError.ReadToEnd();
            if (process.ExitCode == 0)
            {
                return BitLockerActionResult.Ok();
            }

            if (stderr.Contains("0x80310023") || stderr.Contains("already locked", StringComparison.OrdinalIgnoreCase))
            {
                return BitLockerActionResult.Fail("The drive is already locked.", unchecked((int)0x80310023));
            }

            return BitLockerActionResult.Fail(string.IsNullOrWhiteSpace(stderr) ? "Lock failed via PowerShell." : stderr.Trim(), process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BitLockerActionResult.Fail($"PowerShell lock failed: {ex.Message}");
        }
    }

    public static string NormalizeDriveLetter(string letter)
    {
        if (string.IsNullOrWhiteSpace(letter)) return "";
        var trimmed = letter.Trim();
        if (trimmed.EndsWith(":\\", StringComparison.Ordinal))
            return trimmed.Substring(0, 2).ToUpperInvariant();
        if (trimmed.EndsWith(":", StringComparison.Ordinal))
            return trimmed.ToUpperInvariant();
        return (trimmed + ":").ToUpperInvariant();
    }

    public static string? NormalizeRecoveryKey(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var digits = new string(input.Where(char.IsDigit).ToArray());
        if (digits.Length != 48) return null;
        return string.Join("-", Enumerable.Range(0, 8).Select(i => digits.Substring(i * 6, 6)));
    }

    public static bool IsOperatingSystemOrAppDrive(string driveLetter)
    {
        if (string.IsNullOrWhiteSpace(driveLetter)) return false;
        var normalized = NormalizeDriveLetter(driveLetter);

        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\').ToUpperInvariant();
        if (!string.IsNullOrEmpty(systemDrive) && string.Equals(normalized, systemDrive, StringComparison.OrdinalIgnoreCase))
            return true;

        var appExe = Environment.ProcessPath ?? AppContext.BaseDirectory;
        var appDrive = Path.GetPathRoot(appExe)?.TrimEnd('\\').ToUpperInvariant();
        if (!string.IsNullOrEmpty(appDrive) && string.Equals(normalized, appDrive, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public static IReadOnlyList<string> GetProcessesOnDrive(string driveLetter)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(driveLetter)) return result;

        var prefix = NormalizeDriveLetter(driveLetter) + "\\";
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var mainModulePath = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(mainModulePath) &&
                        mainModulePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add($"{p.ProcessName} (PID {p.Id})");
                    }
                }
                catch
                {
                    // Access denied on system/protected processes
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch
        {
        }
        return result;
    }

    public static string GetBitLockerErrorMessage(uint returnVal)
    {
        return returnVal switch
        {
            0 => "Operation completed successfully.",
            0x80310008 => "BitLocker operations are not allowed in Windows Safe Mode.",
            0x80310023 => "The drive is already locked.",
            0x80310025 => "BitLocker is not enabled on this drive.",
            0x80310027 => "The password or recovery key provided is incorrect.",
            0x80310034 => "The specified key protector was not found on this drive.",
            0x80310035 => "The drive is already unlocked.",
            0x80310036 => "The volume has open files or handles and cannot be locked without force dismount.",
            0x80070005 => "Access denied. Administrator privileges are required.",
            0x80070015 => "The device is not ready.",
            0x80070020 => "The drive cannot be locked because files are in use by another process.",
            0x80070032 => "The request is not supported.",
            _ => $"BitLocker error (0x{returnVal:X8})"
        };
    }

    private IReadOnlyList<BitLockerDrive> QueryDrives(CancellationToken ct)
    {
        var drivesList = new List<BitLockerDrive>();
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\').ToUpperInvariant() ?? "C:";

        // Get snapshot of mounted drives from DriveInfo
        var driveInfoMap = new Dictionary<string, DriveInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var di in DriveInfo.GetDrives())
            {
                if (di.DriveType == System.IO.DriveType.Fixed || di.DriveType == System.IO.DriveType.Removable)
                {
                    var letter = di.Name.TrimEnd('\\').ToUpperInvariant();
                    driveInfoMap[letter] = di;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to get DriveInfo: {ex.Message}");
        }

        var processedLetters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Try WMI query if supported
        bool wmiQueried = false;
        try
        {
            ct.ThrowIfCancellationRequested();

            var scope = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption");
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Win32_EncryptableVolume"));
            using var collection = searcher.Get();

            foreach (ManagementObject mo in collection)
            {
                ct.ThrowIfCancellationRequested();
                using (mo)
                {
                    var drive = ParseEncryptableVolume(mo, systemDrive, driveInfoMap);
                    drivesList.Add(drive);

                    if (!string.IsNullOrEmpty(drive.DriveLetter))
                    {
                        processedLetters.Add(drive.DriveLetter.ToUpperInvariant());
                    }
                }
            }

            wmiQueried = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WMI Win32_EncryptableVolume query failed (non-elevated or error): {ex.Message}");
        }

        // Add any remaining drives from DriveInfo that were not in WMI
        foreach (var kvp in driveInfoMap)
        {
            var letter = kvp.Key;
            var di = kvp.Value;

            if (processedLetters.Contains(letter)) continue;

            var isOs = string.Equals(letter, systemDrive, StringComparison.OrdinalIgnoreCase);
            var driveType = isOs
                ? AppLock.Models.DriveType.OperatingSystem
                : (di.DriveType == System.IO.DriveType.Removable ? AppLock.Models.DriveType.Removable : AppLock.Models.DriveType.Data);

            string label = "";
            long total = 0;
            long free = 0;
            bool isReady = false;

            try
            {
                isReady = di.IsReady;
                if (isReady)
                {
                    label = di.VolumeLabel;
                    total = di.TotalSize;
                    free = di.AvailableFreeSpace;
                }
            }
            catch { }

            var drive = new BitLockerDrive
            {
                DriveLetter = letter,
                VolumeLabel = label,
                VolumeGuid = "",
                DriveType = driveType,
                ProtectionStatus = wmiQueried ? ProtectionStatus.Off : ProtectionStatus.Unknown,
                LockStatus = isReady ? LockStatus.Unlocked : LockStatus.Unknown,
                EncryptionPercent = 0,
                ConversionStatus = wmiQueried ? "Fully Decrypted" : "Elevation required",
                EncryptionMethod = wmiQueried ? "None" : "Elevation required",
                TotalBytes = total,
                FreeBytes = free,
                KeyProtectors = new List<KeyProtectorInfo>()
            };

            drivesList.Add(drive);
        }

        // Sort: OS drive first, then Data drives by letter, then Removable drives
        return drivesList
            .OrderBy(d => d.DriveType == AppLock.Models.DriveType.OperatingSystem ? 0 : (d.DriveType == AppLock.Models.DriveType.Data ? 1 : 2))
            .ThenBy(d => d.DriveLetter)
            .ToList();
    }

    private static BitLockerDrive ParseEncryptableVolume(
        ManagementObject mo,
        string systemDrive,
        Dictionary<string, DriveInfo> driveInfoMap)
    {
        var deviceId = mo["DeviceID"] as string ?? "";
        var driveLetter = mo["DriveLetter"] as string ?? "";
        driveLetter = driveLetter.TrimEnd('\\').ToUpperInvariant();

        // Protection status
        uint protectionStatus = 3; // Unknown
        try
        {
            using var outParams = mo.InvokeMethod("GetProtectionStatus", null, null);
            if (outParams != null && outParams["ProtectionStatus"] != null)
            {
                protectionStatus = Convert.ToUInt32(outParams["ProtectionStatus"]);
            }
        }
        catch { }

        // Lock status
        uint lockStatus = 2; // Unknown
        try
        {
            using var outParams = mo.InvokeMethod("GetLockStatus", null, null);
            if (outParams != null && outParams["LockStatus"] != null)
            {
                lockStatus = Convert.ToUInt32(outParams["LockStatus"]);
            }
        }
        catch { }

        // Conversion status & encryption percentage
        uint conversionStatus = 0;
        uint encryptionPercentage = 0;
        try
        {
            using var outParams = mo.InvokeMethod("GetConversionStatus", null, null);
            if (outParams != null)
            {
                if (outParams["ConversionStatus"] != null)
                    conversionStatus = Convert.ToUInt32(outParams["ConversionStatus"]);
                if (outParams["EncryptionPercentage"] != null)
                    encryptionPercentage = Convert.ToUInt32(outParams["EncryptionPercentage"]);
            }
        }
        catch { }

        // Encryption method
        uint encryptionMethod = 0;
        try
        {
            using var outParams = mo.InvokeMethod("GetEncryptionMethod", null, null);
            if (outParams != null && outParams["EncryptionMethod"] != null)
            {
                encryptionMethod = Convert.ToUInt32(outParams["EncryptionMethod"]);
            }
        }
        catch { }

        // Key protectors
        var protectors = new List<KeyProtectorInfo>();
        try
        {
            using var inParams = mo.GetMethodParameters("GetKeyProtectors");
            inParams["KeyProtectorType"] = 0; // 0 = return all
            using var outParams = mo.InvokeMethod("GetKeyProtectors", inParams, null);
            if (outParams != null && outParams["VolumeKeyProtectorID"] is string[] protectorIds)
            {
                foreach (var id in protectorIds)
                {
                    int typeCode = 0;
                    try
                    {
                        using var inType = mo.GetMethodParameters("GetKeyProtectorType");
                        inType["VolumeKeyProtectorID"] = id;
                        using var outType = mo.InvokeMethod("GetKeyProtectorType", inType, null);
                        if (outType != null && outType["KeyProtectorType"] != null)
                        {
                            typeCode = Convert.ToInt32(outType["KeyProtectorType"]);
                        }
                    }
                    catch { }

                    protectors.Add(new KeyProtectorInfo
                    {
                        Id = id,
                        TypeCode = typeCode,
                        TypeName = MapProtectorType(typeCode)
                    });
                }
            }
        }
        catch { }

        // Match with DriveInfo for label and capacity
        string label = "";
        long total = 0;
        long free = 0;
        bool isRemovable = false;

        if (!string.IsNullOrEmpty(driveLetter) && driveInfoMap.TryGetValue(driveLetter, out var di))
        {
            isRemovable = di.DriveType == System.IO.DriveType.Removable;
            try
            {
                if (di.IsReady)
                {
                    label = di.VolumeLabel;
                    total = di.TotalSize;
                    free = di.AvailableFreeSpace;
                }
            }
            catch { }
        }

        var isOsDrive = string.Equals(driveLetter, systemDrive, StringComparison.OrdinalIgnoreCase);
        var driveType = isOsDrive
            ? AppLock.Models.DriveType.OperatingSystem
            : (isRemovable ? AppLock.Models.DriveType.Removable : AppLock.Models.DriveType.Data);

        var protection = protectionStatus switch
        {
            0 => ProtectionStatus.Off,
            1 => ProtectionStatus.On,
            2 => ProtectionStatus.Suspended,
            _ => ProtectionStatus.Unknown
        };

        var lockState = lockStatus switch
        {
            0 => LockStatus.Unlocked,
            1 => LockStatus.Locked,
            _ => LockStatus.Unknown
        };

        return new BitLockerDrive
        {
            DriveLetter = driveLetter,
            VolumeLabel = label,
            VolumeGuid = deviceId,
            DriveType = driveType,
            ProtectionStatus = protection,
            LockStatus = lockState,
            EncryptionPercent = (int)Math.Clamp(encryptionPercentage, 0, 100),
            ConversionStatus = MapConversionStatus(conversionStatus, encryptionPercentage),
            EncryptionMethod = MapEncryptionMethod(encryptionMethod),
            KeyProtectors = protectors,
            TotalBytes = total,
            FreeBytes = free
        };
    }

    public static string MapProtectorType(int typeCode)
    {
        return typeCode switch
        {
            1 => "TPM",
            2 => "External Key",
            3 => "Recovery Password",
            4 => "TPM + PIN",
            5 => "TPM + Key",
            6 => "TPM + PIN + Key",
            7 => "Public Key",
            8 => "Password",
            9 => "TPM Network Key",
            _ => "Unknown Protector"
        };
    }

    public static string MapEncryptionMethod(uint method)
    {
        return method switch
        {
            0 => "None",
            1 => "AES-128 (Diffuser)",
            2 => "AES-256 (Diffuser)",
            3 => "AES-128",
            4 => "AES-256",
            5 => "Hardware Encryption",
            6 => "XTS-AES 128",
            7 => "XTS-AES 256",
            _ => "Unknown"
        };
    }

    public static string MapConversionStatus(uint status, uint percentage)
    {
        return status switch
        {
            0 => "Fully Decrypted",
            1 => "Fully Encrypted",
            2 => $"Encrypting ({percentage}%)",
            3 => $"Decrypting ({percentage}%)",
            4 => $"Encryption Paused ({percentage}%)",
            5 => $"Decryption Paused ({percentage}%)",
            _ => percentage == 100 ? "Fully Encrypted" : (percentage == 0 ? "Fully Decrypted" : $"{percentage}%")
        };
    }
}
