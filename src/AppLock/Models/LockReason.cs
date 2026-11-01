namespace AppLock.Models;

/// <summary>
/// Reason a rule was forcibly locked by the self-discipline service.
/// </summary>
public enum LockReason
{
    Manual,
    Quota,
    Schedule,
    Idle
}

