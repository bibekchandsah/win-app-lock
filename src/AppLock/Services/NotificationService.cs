namespace AppLock.Services;

public enum NotificationSeverity
{
    Informational,
    Success,
    Warning,
    Error
}

public class AppNotification
{
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public NotificationSeverity Severity { get; set; } = NotificationSeverity.Informational;
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(3.5);
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

/// <summary>
/// Service providing centralized in-app toast notification dispatch for actions and events across the app.
/// </summary>
public class NotificationService
{
    public event EventHandler<AppNotification>? NotificationTriggered;

    public void Show(string title, string message, NotificationSeverity severity, TimeSpan? duration = null)
    {
        var notification = new AppNotification
        {
            Title = title,
            Message = message,
            Severity = severity,
            Duration = duration ?? (severity == NotificationSeverity.Error ? TimeSpan.FromSeconds(5.0) : TimeSpan.FromSeconds(3.5))
        };

        NotificationTriggered?.Invoke(this, notification);
    }

    public void ShowSuccess(string title, string message, TimeSpan? duration = null)
        => Show(title, message, NotificationSeverity.Success, duration);

    public void ShowError(string title, string message, TimeSpan? duration = null)
        => Show(title, message, NotificationSeverity.Error, duration);

    public void ShowWarning(string title, string message, TimeSpan? duration = null)
        => Show(title, message, NotificationSeverity.Warning, duration);

    public void ShowInfo(string title, string message, TimeSpan? duration = null)
        => Show(title, message, NotificationSeverity.Informational, duration);
}
