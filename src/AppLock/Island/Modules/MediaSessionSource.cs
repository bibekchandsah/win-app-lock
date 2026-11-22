using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    string ArtFile)
{
    public static MediaSnapshot Empty { get; } = new(false, false, string.Empty, string.Empty, string.Empty, 0, 0, string.Empty);
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
        "AppLock", "island", "art");

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
            var session = _manager.GetCurrentSession();
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
            // advance by itself or it looks frozen.
            if (playing && timeline.LastUpdatedTime != default)
            {
                position += Math.Max(0, (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds);
            }

            if (duration > 0) position = Math.Min(position, duration);

            string key = $"{app}|{title}|{artist}";
            if (!string.Equals(key, _artKey, StringComparison.Ordinal))
            {
                _artKey = key;
                if (properties?.Thumbnail != null) await SaveArtAsync(properties.Thumbnail);
                else _artFile = string.Empty;
            }

            return new MediaSnapshot(true, playing, title, artist, app, position, duration, _artFile);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Island] media poll failed: {ex.Message}");
            return null;
        }
    }

    public async Task CommandAsync(string command)
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = _manager.GetCurrentSession();
            if (session == null) return;

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

    public void Dispose()
    {
        _manager = null;
    }
}
