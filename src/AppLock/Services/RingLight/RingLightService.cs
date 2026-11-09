using System;
using System.Threading.Tasks;
using AppLock.Models;

namespace AppLock.Services.RingLight;

/// <summary>
/// State machine: Off → FadingIn → On → FadingOut → Off.
/// All triggers (toggle, hotkey, tray) call <see cref="SetEnabled(bool, string)"/>.
/// Must be used on the UI (STA) thread (overlay windows are UI objects).
/// </summary>
public sealed class RingLightService : IDisposable
{
    public enum State { Off, FadingIn, On, FadingOut }

    // ── Events ────────────────────────────────────────────────────────────────
    public event EventHandler<bool>?   EnabledChanged;
    public event EventHandler<State>?  StateChanged;

    // ── Fields ────────────────────────────────────────────────────────────────
    private RingOverlayManager? _manager;
    private State _state = State.Off;
    private bool _disposed;

    // ── Public properties ─────────────────────────────────────────────────────
    public State CurrentState => _state;
    public bool  IsOn => _state == State.On || _state == State.FadingIn;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Turn ring light on or off. Safe to call repeatedly.</summary>
    public void SetEnabled(bool enable, string reason = "user")
    {
        if (enable && (_state == State.Off || _state == State.FadingOut))
            _ = TurnOnAsync();
        else if (!enable && (_state == State.On || _state == State.FadingIn))
            _ = TurnOffAsync();
    }

    /// <summary>Toggle the current state.</summary>
    public void Toggle() => SetEnabled(!IsOn);

    /// <summary>Apply live settings changes to running overlays without restart.</summary>
    public void ApplySettingsLive(RingLightSettings settings)
    {
        if (_state == State.On || _state == State.FadingIn)
            _manager?.UpdateSettings(settings);
    }

    /// <summary>
    /// Called when WM_DISPLAYCHANGE is received (monitor added, removed, or resolution changed).
    /// Rebuilds overlays to cover the correct displays.
    /// </summary>
    public void OnDisplayChanged()
    {
        if (_state == State.On || _state == State.FadingIn)
        {
            var settings = AppLock.Services.ServiceContainer.Settings.RingLight;
            _manager?.Rebuild(settings);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _manager?.Dispose();
        _manager = null;
    }

    // ── Private state machine ─────────────────────────────────────────────────

    private async Task TurnOnAsync()
    {
        if (_disposed) return;
        var settings = AppLock.Services.ServiceContainer.Settings.RingLight;

        SetState(State.FadingIn);
        EnabledChanged?.Invoke(this, true);

        // Create the manager and show overlays (must be on UI thread)
        _manager?.Dispose();
        _manager = new RingOverlayManager();
        _manager.Show(settings);

        // Wait for the fade-in duration
        await Task.Delay(Math.Max(50, settings.FadeMs));

        if (_state == State.FadingIn)
        {
            SetState(State.On);

            // Persist if RememberState
            if (settings.RememberState)
            {
                settings.Enabled = true;
                AppLock.Services.ServiceContainer.SettingsStore.Save(AppLock.Services.ServiceContainer.Settings);
            }
        }
    }

    private async Task TurnOffAsync()
    {
        if (_disposed || _manager == null) return;
        var settings = AppLock.Services.ServiceContainer.Settings.RingLight;

        SetState(State.FadingOut);
        EnabledChanged?.Invoke(this, false);

        await _manager.HideAllAsync(settings.FadeMs);
        _manager.Dispose();
        _manager = null;

        if (_state == State.FadingOut)
        {
            SetState(State.Off);

            // Persist if RememberState
            if (settings.RememberState)
            {
                settings.Enabled = false;
                AppLock.Services.ServiceContainer.SettingsStore.Save(AppLock.Services.ServiceContainer.Settings);
            }
        }
    }

    private void SetState(State s)
    {
        _state = s;
        StateChanged?.Invoke(this, s);
    }
}

