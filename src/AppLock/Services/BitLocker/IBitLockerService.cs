using AppLock.Models;

namespace AppLock.Services.BitLocker;

public interface IBitLockerService
{
    Task<IReadOnlyList<BitLockerDrive>> GetDrivesAsync(CancellationToken ct = default);
    Task<BitLockerActionResult> UnlockWithPassphraseAsync(string driveLetter, string passphrase, CancellationToken ct = default);
    Task<BitLockerActionResult> UnlockWithNumericalPasswordAsync(string driveLetter, string numericalPassword, CancellationToken ct = default);
    Task<BitLockerActionResult> LockAsync(string driveLetter, bool forceDismount = true, CancellationToken ct = default);
    Task<BitLockerActionResult> TurnOnBitLockerAsync(BitLockerTurnOnRequest request, CancellationToken ct = default);
    Task<BitLockerActionResult> TurnOffBitLockerAsync(string driveLetter, CancellationToken ct = default);
    Task<BitLockerActionResult> SuspendProtectionAsync(string driveLetter, int rebootCount = 1, CancellationToken ct = default);
    Task<BitLockerActionResult> ResumeProtectionAsync(string driveLetter, CancellationToken ct = default);
    Task<BitLockerActionResult> ChangePasswordAsync(string driveLetter, string oldPassphrase, string newPassphrase, CancellationToken ct = default);
    Task<IReadOnlyList<KeyProtectorInfo>> GetKeyProtectorsAsync(string driveLetter, CancellationToken ct = default);
    Task<BitLockerActionResult> DeleteKeyProtectorAsync(string driveLetter, string protectorId, CancellationToken ct = default);
    Task<BitLockerActionResult> AddPasswordProtectorAsync(string driveLetter, string passphrase, CancellationToken ct = default);
    Task<BitLockerActionResult> AddRecoveryPasswordProtectorAsync(string driveLetter, string? recoveryPassword = null, CancellationToken ct = default);
    Task<string?> GetRecoveryKeyAsync(string driveLetter, string protectorId, CancellationToken ct = default);
    Task<string?> GenerateRecoveryPasswordAsync(string driveLetter, CancellationToken ct = default);
}
