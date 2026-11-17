using AppLock.Models;
using AppLock.Services.VolumeMixer;

namespace AppLock.ViewModels;

/// <summary>One row of the auto-mute range list in the Volume Mixer Appearance tab.</summary>
public sealed class MuteWindowItem
{
    public MuteWindowItem(MuteWindow window) => Window = window;

    public MuteWindow Window { get; }

    public string Display => MuteSchedule.Describe(Window);

    public override string ToString() => Display;
}
