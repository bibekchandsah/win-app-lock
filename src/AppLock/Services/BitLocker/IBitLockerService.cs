using AppLock.Models;

namespace AppLock.Services.BitLocker;

public interface IBitLockerService
{
    Task<IReadOnlyList<BitLockerDrive>> GetDrivesAsync(CancellationToken ct = default);
}
