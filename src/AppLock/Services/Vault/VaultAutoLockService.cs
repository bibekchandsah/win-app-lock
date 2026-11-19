using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using AppLock.Models.Vault;

namespace AppLock.Services.Vault;

/// <summary>
/// Monitors triggers that require automatic vault locking:
/// - System and user idle timeout (IdleService)
/// - PC lock and remote disconnect (SessionSwitch)
/// - System suspend/sleep/hibernate (PowerModeChanged)
/// - Window minimized/hidden for N minutes
/// - Application exit
/// - Emergency hotkey (Ctrl+Alt+L)
/// - Vault volume / drive disconnection
/// </summary>
public sealed class VaultAutoLockService : IDisposable
{
    private readonly VaultSessionManager _sessionManager;
    private Timer? _checkTimer;
    private DateTime? _minimizedStartTime;
    private bool _isDisposed;

    public VaultAutoLockService(VaultSessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    public void Start()
    {
        if (_checkTimer != null) return;

        // Check triggers every 5 seconds
        _checkTimer = new Timer(OnCheckTick, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        catch (Exception ex)
        {
            LogService.LogAction("Vault", $"Could not hook SystemEvents: {ex.Message}");
        }

        LogService.LogAction("Vault", "VaultAutoLockService started.");
    }

    public void Stop()
    {
        _checkTimer?.Dispose();
        _checkTimer = null;

        try
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        catch { }

        LogService.LogAction("Vault", "VaultAutoLockService stopped.");
    }

    public void OnWindowMinimized()
    {
        if (!_minimizedStartTime.HasValue)
        {
            _minimizedStartTime = DateTime.UtcNow;
        }
    }

    public void OnWindowRestored()
    {
        _minimizedStartTime = null;
    }

    public async Task EmergencyLockAllAsync()
    {
        LogService.LogAction("Vault", "Emergency lock triggered: wiping temp files and locking all vaults immediately.");

        // 1. Close and wipe all active temp sessions
        await TempOpenService.CloseAllSessionsAsync();

        // 2. Lock all vault sessions (which zeroes keys, closes lock files)
        _sessionManager.LockAll();
    }

    public void OnAppExit()
    {
        var settings = ServiceContainer.Settings.Vault;
        if (settings.LockOnExit)
        {
            try
            {
                TempOpenService.CloseAllSessionsAsync().GetAwaiter().GetResult();
            }
            catch { }

            _sessionManager.LockAll();
            LogService.LogAction("Vault", "All vaults locked on application exit.");
        }
    }

    private void OnCheckTick(object? state)
    {
        if (_isDisposed) return;

        try
        {
            var openSessions = _sessionManager.GetAllSessions();
            if (openSessions.Count == 0) return;

            var settings = ServiceContainer.Settings.Vault;

            // 1. Check window minimize duration
            if (_minimizedStartTime.HasValue && settings.LockOnMinimizeMinutes > 0)
            {
                var minMinutes = (DateTime.UtcNow - _minimizedStartTime.Value).TotalMinutes;
                if (minMinutes >= settings.LockOnMinimizeMinutes)
                {
                    LogService.LogAction("Vault", $"Auto-locking all vaults after {minMinutes:F1} min minimized.");
                    _minimizedStartTime = null;
                    _sessionManager.LockAll();
                    return;
                }
            }

            // 2. Check idle time and drive reachability per vault
            double currentIdleMinutes = IdleService.CurrentIdleMinutes;

            foreach (var session in openSessions)
            {
                // Drive disconnected check
                if (!Directory.Exists(session.VaultPath))
                {
                    LogService.LogAction("Vault", $"Auto-locking vault '{session.VaultName}' because vault path is missing or disconnected.");
                    _sessionManager.Lock(session.VaultId);
                    continue;
                }

                // Idle timeout check
                var reg = settings.Vaults.FirstOrDefault(v => v.Id == session.VaultId);
                int thresholdMinutes = reg?.AutoLockMinutes ?? settings.DefaultAutoLockMinutes;

                if (thresholdMinutes > 0 && currentIdleMinutes >= thresholdMinutes)
                {
                    LogService.LogAction("Vault", $"Auto-locking vault '{session.VaultName}' after {currentIdleMinutes:F1}m idle (threshold: {thresholdMinutes}m).");
                    _sessionManager.Lock(session.VaultId);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.LogAction("Vault", $"AutoLock check error: {ex.Message}");
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        var settings = ServiceContainer.Settings.Vault;
        if (!settings.LockOnPcLock) return;

        if (e.Reason == SessionSwitchReason.SessionLock ||
            e.Reason == SessionSwitchReason.RemoteDisconnect ||
            e.Reason == SessionSwitchReason.ConsoleDisconnect)
        {
            LogService.LogAction("Vault", $"PC lock/disconnect detected ({e.Reason}). Locking all vaults.");
            _sessionManager.LockAll();
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        var settings = ServiceContainer.Settings.Vault;
        if (!settings.LockOnPcLock) return;

        if (e.Mode == PowerModes.Suspend)
        {
            LogService.LogAction("Vault", "System entering suspend/sleep. Locking all vaults.");
            _sessionManager.LockAll();
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
    }
}
