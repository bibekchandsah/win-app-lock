namespace AppLock.WebFilter.Engine;

/// <summary>The decision for one query. First match wins, so the reason is kept for the log.</summary>
public readonly record struct FilterVerdict(bool IsBlocked, string? Reason)
{
    public static FilterVerdict Allow => new(false, null);

    public static FilterVerdict Block(string reason) => new(true, reason);
}

/// <summary>
/// What the server asks before forwarding. Implemented by the block-list matcher (milestone 3);
/// keeping it an interface lets the listener be tested with a trivial policy.
/// </summary>
public interface IDnsFilterPolicy
{
    FilterVerdict Check(in DnsQuery query);
}
