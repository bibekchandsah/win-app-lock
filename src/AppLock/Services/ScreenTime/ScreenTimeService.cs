using System;
using System.Threading.Tasks;
using AppLock.Models;

namespace AppLock.Services.ScreenTime;

/// <summary>
/// Owns the Screen Time history store and the one shared foreground sampler. Self-Discipline
/// starts the sampler so history is recorded even before the Screen Time tab is ever opened.
/// </summary>
public sealed class ScreenTimeService : IAsyncDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _appSettings;

    public ScreenTimeService(SettingsStore settingsStore, AppSettings settings)
    {
        _settingsStore = settingsStore;
        _appSettings = settings;

        Store = new ScreenTimeStore();
        Sampler = new ForegroundUsageSampler(() => Settings, Store);
    }

    public ScreenTimeStore Store { get; }
    public ForegroundUsageSampler Sampler { get; }

    public ScreenTimeSettings Settings => _appSettings.ScreenTime ??= new ScreenTimeSettings();

    public void Save() => _settingsStore.Save(_appSettings);

    /// <summary>Prunes history once per app start and then at most once a day.</summary>
    public void PruneIfDue()
    {
        try
        {
            Store.Prune(Settings.RetentionDays);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ScreenTime] prune failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Sampler.Dispose();
        await Store.DisposeAsync().ConfigureAwait(false);
    }
}
