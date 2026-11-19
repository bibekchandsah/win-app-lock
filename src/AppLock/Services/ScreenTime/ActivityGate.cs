using System;

namespace AppLock.Services.ScreenTime;

/// <summary>
/// Section 5.2 of the Screen Time spec: a second counts only while the session is unlocked, the PC
/// is not suspended, and the user is not idle — unless a video/song is playing and the user's
/// shared setting allows media to keep the app active.
/// </summary>
public sealed class ActivityGate
{
    private readonly Func<double> _idleSeconds;
    private readonly Func<bool> _mediaPlaying;
    private readonly Func<bool> _sessionAvailable;
    private readonly Func<bool> _countMedia;

    public ActivityGate(
        Func<double> idleSeconds,
        Func<bool> mediaPlaying,
        Func<bool> sessionAvailable,
        Func<bool> countMedia)
    {
        _idleSeconds = idleSeconds;
        _mediaPlaying = mediaPlaying;
        _sessionAvailable = sessionAvailable;
        _countMedia = countMedia;
    }

    public bool IsActive(int idlePauseSeconds)
        => IsActive(_idleSeconds(), _mediaPlaying(), _countMedia(), _sessionAvailable(), idlePauseSeconds);

    public static bool IsActive(
        double idleSeconds,
        bool mediaPlaying,
        bool countMedia,
        bool sessionAvailable,
        int idlePauseSeconds)
    {
        if (!sessionAvailable) return false;
        if (idleSeconds < idlePauseSeconds) return true;
        return countMedia && mediaPlaying;
    }
}
