using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace AppLock.Island.Modules;

/// <summary>What the Home tab needs to draw the player. Sizes and text are fixed by the UI.</summary>
public sealed record MediaSnapshot(
    bool HasSession,
    bool Playing,
    string Title,
    string Artist,
    string App,
    double PositionSeconds,
    double DurationSeconds,
    string ArtFile,
    int SessionCount,
    bool AnyPlaying = false)
{
    public static MediaSnapshot Empty { get; } = new(false, false, string.Empty, string.Empty, string.Empty, 0, 0, string.Empty, 0, false);
}

/// <summary>
/// Reads the current Windows media session (SMTC) and writes its album art into a folder the page
/// loads through its own virtual host, so images never travel over the message bridge.
/// Polled once a second, from the sampling thread, and only while the Home tab is visible.
/// </summary>
public sealed class MediaSessionSource : IDisposable
{
    private const int MaxArtBytes = 4 * 1024 * 1024;

    private readonly int _artVersion;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private IReadOnlyList<GlobalSystemMediaTransportControlsSession> _sessions =
        Array.Empty<GlobalSystemMediaTransportControlsSession>();
    private bool _pinned;   // the user picked this session with the chevron
    private string _artFile = string.Empty;
    private string _artKey = string.Empty;
    private bool _managerFailed;

    public MediaSessionSource()
    {
        // A per-run counter keeps stale art from a previous run from being served from the cache.
        _artVersion = Environment.TickCount;
    }

    public static string ArtFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppLock", "island", "cache", "art");

    public async Task<MediaSnapshot?> PollAsync()
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            if (!_managerFailed)
            {
                _managerFailed = true;
                Debug.WriteLine($"[Island] media manager unavailable: {ex.Message}");
            }

            return null;
        }

        try
        {
            var session = _session = ResolveSession();
            if (session == null)
            {
                _artKey = string.Empty;
                return MediaSnapshot.Empty;
            }

            var properties = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            bool playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            string title = properties?.Title ?? string.Empty;
            string artist = properties?.Artist ?? string.Empty;
            string app = FriendlyAppName(session.SourceAppUserModelId);

            double duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds);
            double position = Math.Max(0, (timeline.Position - timeline.StartTime).TotalSeconds);

            // SMTC only reports the position when it last changed; between updates the bar has to
            // advance by itself (scaled by playback rate) or it looks frozen or drifts at 1.5x/2x.
            if (playing && timeline.LastUpdatedTime != default)
            {
                double rate = playback.PlaybackRate is { } r && r > 0 ? r : 1.0;
                position += Math.Max(0, (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds) * rate;
            }

            if (duration > 0) position = Math.Min(position, duration);

            string key = $"{app}|{title}|{artist}";
            if (!string.Equals(key, _artKey, StringComparison.Ordinal))
            {
                _artKey = key;
                if (properties?.Thumbnail != null) await SaveArtAsync(properties.Thumbnail);
                else _artFile = string.Empty;
            }

            int count = 0;
            bool anyPlaying = playing;
            foreach (var candidate in _sessions)
            {
                var st = StatusOf(candidate);
                if (st != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed) count++;
                if (st == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) anyPlaying = true;
            }

            return new MediaSnapshot(true, playing, title, artist, app, position, duration, _artFile, count, anyPlaying);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Island] media poll failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The session the player should follow. Windows moves "current" to another app the instant
    /// this one pauses, so a paused track was replaced by whatever else had a session and the next
    /// Play hit that other app instead of resuming the track on screen.
    /// </summary>
    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager == null) return null;

        GlobalSystemMediaTransportControlsSession? current;
        try { current = _manager.GetCurrentSession(); } catch { current = null; }

        if (_session != null && StatusOf(_session) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused
            && StatusOf(current) != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            try
            {
                foreach (var candidate in _manager.GetSessions())
                {
                    if (ReferenceEquals(candidate, _session)) return _session;
                }
            }
            catch { }
        }

        if (StatusOf(current) != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            foreach (var candidate in _sessions)
            {
                if (StatusOf(candidate) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    return candidate;
                }
            }
        }

        return current;
    }

    /// <summary>
    /// The session to read: the one the user cycled to while it still exists, otherwise the
    /// automatic pick. Also refreshes the session list the chevron walks through.
    /// </summary>
    private GlobalSystemMediaTransportControlsSession? ResolveSession()
    {
        if (_manager == null) return null;

        try { _sessions = _manager.GetSessions(); }
        catch { _sessions = Array.Empty<GlobalSystemMediaTransportControlsSession>(); }

        if (_pinned && _session != null)
        {
            foreach (var candidate in _sessions)
            {
                if (ReferenceEquals(candidate, _session)) return _session;
            }
        }

        _pinned = false;
        return PickSession();
    }

    /// <summary>Shows the next app that has media; the list wraps so every app can be reached.</summary>
    public void CycleSession()
    {
        var sessions = new List<GlobalSystemMediaTransportControlsSession>();
        foreach (var candidate in _sessions)
        {
            if (StatusOf(candidate) != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed)
            {
                sessions.Add(candidate);
            }
        }

        if (sessions.Count < 2) return;

        // Keep currently playing sessions first, then paused/other sessions.
        sessions.Sort((a, b) =>
        {
            bool aPlay = StatusOf(a) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            bool bPlay = StatusOf(b) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            return bPlay.CompareTo(aPlay);
        });

        int index = -1;
        for (int i = 0; i < sessions.Count; i++)
        {
            if (ReferenceEquals(sessions[i], _session)) { index = i; break; }
        }

        _session = sessions[(index + 1) % sessions.Count];
        _pinned = true;
        _artKey = string.Empty; // force the new cover to load
    }

    private static GlobalSystemMediaTransportControlsSessionPlaybackStatus StatusOf(
        GlobalSystemMediaTransportControlsSession? session)
    {
        if (session == null) return GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
        try { return session.GetPlaybackInfo().PlaybackStatus; }
        catch { return GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed; }
    }

    public async Task CommandAsync(string command)
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = _session ?? PickSession() ?? _manager.GetCurrentSession();
            if (session == null) return;
            _session = session;

            switch (command)
            {
                case "toggle":
                    if (session.GetPlaybackInfo().PlaybackStatus
                        == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        await session.TryPauseAsync();
                    }
                    else
                    {
                        await session.TryPlayAsync();
                    }
                    break;

                case "next":
                    await session.TrySkipNextAsync();
                    break;

                case "previous":
                    await session.TrySkipPreviousAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Island] media command '{command}' failed: {ex.Message}");
        }
    }

    /// <summary>Moves the shown session's playhead, in seconds from the start.</summary>
    public async Task SeekAsync(double seconds)
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = _session ?? PickSession() ?? _manager.GetCurrentSession();
            if (session == null) return;
            _session = session;

            long ticks = (long)Math.Max(0, seconds * TimeSpan.TicksPerSecond);
            await session.TryChangePlaybackPositionAsync(ticks);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Island] media seek failed: {ex.Message}");
        }
    }

    private async Task SaveArtAsync(IRandomAccessStreamReference thumbnail)
    {
        try
        {
            using IRandomAccessStream stream = await thumbnail.OpenReadAsync();
            uint size = (uint)Math.Min(stream.Size, MaxArtBytes);
            if (size == 0) return;

            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync(size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);

            Directory.CreateDirectory(ArtFolder);
            string name = $"art-{_artVersion:X}-{Interlocked.Increment(ref _writeCounter):X}.png";
            string path = Path.Combine(ArtFolder, name);
            await File.WriteAllBytesAsync(path, bytes);

            // Only the current cover is kept: the folder is loaded by the page, not a library.
            foreach (string old in Directory.EnumerateFiles(ArtFolder, "art-*.png"))
            {
                if (string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(old); } catch { }
            }

            _artFile = name;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Island] album art write failed: {ex.Message}");
            _artFile = string.Empty;
        }
    }

    private long _writeCounter;

    /// <summary>Turns an AUMID ("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic") into a name.</summary>
    private static string FriendlyAppName(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId)) return string.Empty;

        string id = appUserModelId;
        int bang = id.IndexOf('!');
        if (bang > 0) id = id[..bang];

        string lower = id.ToLowerInvariant();
        if (lower.Contains("zunemusic") || lower.Contains("mediaplayer")) return "Media Player";
        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("msedge")) return "Microsoft Edge";
        if (lower.Contains("chrome")) return "Google Chrome";
        if (lower.Contains("firefox")) return "Firefox";
        if (lower.Contains("brave")) return "Brave";
        if (lower.Contains("opera")) return "Opera";
        if (lower.Contains("vlc")) return "VLC";
        if (lower.Contains("foobar")) return "foobar2000";
        if (lower.Contains("aimp")) return "AIMP";
        if (lower.Contains("tidal")) return "TIDAL";
        if (lower.Contains("amazonmusic")) return "Amazon Music";

        int dot = id.LastIndexOf('.');
        return dot >= 0 && dot < id.Length - 1 ? id[(dot + 1)..] : id;
    }

    /// <summary>Brings the window of the active media session's app to the foreground.</summary>
    public void FocusCurrentWindow(string? titleHint = null)
    {
        try
        {
            var session = _session ?? ResolveSession();
            if (session == null) return;

            string aumid = session.SourceAppUserModelId ?? string.Empty;
            string friendly = FriendlyAppName(aumid);
            string procHint = ExtractProcessHint(aumid);

            nint bestHwnd = nint.Zero;
            nint fallbackHwnd = nint.Zero;

            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return true;

                string procName;
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    procName = proc.ProcessName;
                }
                catch
                {
                    return true;
                }

                bool procMatches =
                    (!string.IsNullOrEmpty(procHint) && procName.Contains(procHint, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(aumid) && aumid.Contains(procName, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(friendly) && procName.Contains(friendly.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));

                if (!procMatches) return true;

                var sb = new StringBuilder(256);
                GetWindowText(hwnd, sb, sb.Capacity);
                string winTitle = sb.ToString();
                if (string.IsNullOrWhiteSpace(winTitle)) return true;

                if (fallbackHwnd == nint.Zero) fallbackHwnd = hwnd;

                if (!string.IsNullOrWhiteSpace(titleHint) &&
                    winTitle.Contains(titleHint, StringComparison.OrdinalIgnoreCase))
                {
                    bestHwnd = hwnd;
                    return false;
                }

                return true;
            }, nint.Zero);

            nint target = bestHwnd != nint.Zero ? bestHwnd : fallbackHwnd;
            if (target == nint.Zero) return;

            if (IsIconic(target))
                ShowWindow(target, SwRestore);
            else if (!IsWindowVisible(target))
                ShowWindow(target, SwShow);

            SetForegroundWindow(target);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Island] focus media window failed: {ex.Message}");
        }
    }

    private static string ExtractProcessHint(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return string.Empty;

        string id = aumid;
        int bang = id.IndexOf('!');
        if (bang > 0) id = id[..bang];

        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            id = id[..^4];

        string lower = id.ToLowerInvariant();
        if (lower.Contains("zunemusic") || lower.Contains("mediaplayer")) return "Microsoft.Media.Player";
        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("msedge")) return "msedge";
        if (lower.Contains("chrome")) return "chrome";
        if (lower.Contains("firefox")) return "firefox";
        if (lower.Contains("brave")) return "brave";
        if (lower.Contains("opera")) return "opera";
        if (lower.Contains("vlc")) return "vlc";

        int dot = id.LastIndexOf('.');
        return dot >= 0 && dot < id.Length - 1 ? id[(dot + 1)..] : id;
    }

    private const int SwShow = 5;
    private const int SwRestore = 9;

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder lpString, int nMaxCount);

    public void Dispose()
    {
        _manager = null;
    }
}
