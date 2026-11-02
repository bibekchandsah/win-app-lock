using AppLock.Models;

namespace AppLock.Services.BitLocker;

public interface IBitLockerService
{
    Task<IReadOnlyList<BitLockerDrive>> GetDrivesAsync(CancellationToken ct = default);
    Task<BitLockerActionResult> UnlockWithPassphraseAsync(string driveLetter, string passphrase, CancellationToken ct = default);
    Task<BitLockerActionResult> UnlockWithNumericalPasswordAsync(string driveLetter, string numericalPassword, CancellationToken ct = default);
    Task<BitLockerActionResult> LockAsync(string driveLetter, bool forceDismount = true, CancellationToken ct = default);
    Task<BitLockerActionResult> TurnOnBitLockerAsync(BitLockerTurnOnRequest request, CancellationToken ct = default);
    Task<string?> GenerateRecoveryPasswordAsync(string driveLetter, CancellationToken ct = default);
}
