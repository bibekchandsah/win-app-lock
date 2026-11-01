namespace AppLock.Services;

/// <summary>
/// Abstraction over DateTime.Now / DateTime.Today for unit-testability.
/// </summary>
public interface IClock
{
    DateTime Now { get; }
    DateOnly Today { get; }
}

/// <summary>
/// Production implementation backed by the real system clock.
/// </summary>
public class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime Now => DateTime.Now;
    public DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
}

