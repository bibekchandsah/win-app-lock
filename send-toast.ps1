# Send a real Windows toast notification for testing AppLock notifications feature
# This uses the Windows.UI.Notifications API directly

Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
$null = [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]

$APP_ID = 'AppLock.NotificationTest'

$template = @"
<toast>
    <visual>
        <binding template="ToastGeneric">
            <text>AppLock Test Notification</text>
            <text>This is a test notification from AppLock to verify toast detection</text>
        </binding>
    </visual>
</toast>
"@

$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml($template)
$toast = New-Object Windows.UI.Notifications.ToastNotification($xml)
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($APP_ID).Show($toast)

Write-Host "Toast notification sent! It should appear in the bottom-right corner." -ForegroundColor Green
Write-Host "Quickly switch to AppLock and click 'Refresh' to capture it." -ForegroundColor Yellow
