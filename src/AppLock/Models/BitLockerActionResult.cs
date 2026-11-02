namespace AppLock.Models;

public record BitLockerActionResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public int ErrorCode { get; init; }

    public static BitLockerActionResult Ok() => new()
    {
        Success = true,
        ErrorCode = 0,
        ErrorMessage = null
    };

    public static BitLockerActionResult Fail(string errorMessage, int errorCode = -1) => new()
    {
        Success = false,
        ErrorMessage = errorMessage,
        ErrorCode = errorCode
    };
}

