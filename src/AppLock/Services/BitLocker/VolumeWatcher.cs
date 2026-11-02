using System.Management;

namespace AppLock.Services.BitLocker;

public class VolumeWatcher : IDisposable
{
    private ManagementEventWatcher? _watcher;
    private bool _isStarted;
    private readonly object _lock = new();

    public event EventHandler? VolumeChanged;

    public void Start()
    {
        lock (_lock)
        {
            if (_isStarted) return;
            try
            {
                var query = new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent");
                _watcher = new ManagementEventWatcher(query);
                _watcher.EventArrived += OnEventArrived;
                _watcher.Start();
                _isStarted = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to start VolumeWatcher: {ex.Message}");
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_isStarted) return;
            try
            {
                if (_watcher != null)
                {
                    _watcher.EventArrived -= OnEventArrived;
                    _watcher.Stop();
                    _watcher.Dispose();
                    _watcher = null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to stop VolumeWatcher: {ex.Message}");
            }
            finally
            {
                _isStarted = false;
            }
        }
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        try
        {
            VolumeChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in VolumeChanged handler: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}

