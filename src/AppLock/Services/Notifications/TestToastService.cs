using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System;
using System.Threading.Tasks;

namespace AppLock.Services.Notifications;

public class TestToastService
{
    private static bool _isRegistered;

    public static void EnsureRegistered()
    {
        if (_isRegistered) return;
        
        try
        {
            AppNotificationManager.Default.Register();
            _isRegistered = true;
            LogService.LogAction("notifications", "AppNotificationManager registered successfully");
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"Failed to register AppNotificationManager: {ex.Message}");
        }
    }

    public static async Task SendTestNotificationAsync(int delaySeconds = 0)
    {
        try
        {
            EnsureRegistered();

            if (delaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            }

            var builder = new AppNotificationBuilder()
                .AddText("AppLock test notification")
                .AddText("This is a test notification to help position and animate toasts");

            var notification = builder.BuildNotification();
            AppNotificationManager.Default.Show(notification);
            
            LogService.LogAction("notifications", "Test notification sent successfully");
        }
        catch (Exception ex)
        {
            LogService.LogAction("notifications", $"Failed to send test notification: {ex.Message}");
        }
    }
}
