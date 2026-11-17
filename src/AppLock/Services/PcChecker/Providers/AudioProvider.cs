using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;
using NAudio.CoreAudioApi;

namespace AppLock.Services.PcChecker.Providers;

/// <summary>
/// Playback and recording devices from the Windows audio stack (MMDevice API): name, default role,
/// state, format, volume and mute. No microphone audio is ever recorded.
/// </summary>
public sealed class AudioProvider : IInfoProvider
{
    public string Id => "audio";
    public string Category => "audio";
    public InfoTier Tier => InfoTier.Standard;
    public TimeSpan Timeout => TimeSpan.FromSeconds(8);
    public TimeSpan CacheLifetime => TimeSpan.FromMinutes(2);

    public bool IsSupported(SystemContext ctx) => true;

    public Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Collect(progress, ct), ct);

    private InfoSection Collect(IProgress<double> progress, CancellationToken ct)
    {
        var section = new InfoSection { Id = Id, Title = "Audio", Category = Category };
        const string source = "Source: MMDevice API";

        using var enumerator = new MMDeviceEnumerator();

        AddDevices(section, enumerator, DataFlow.Render, "Playback devices", "speaker", ct);
        progress?.Report(0.6);
        AddDevices(section, enumerator, DataFlow.Capture, "Recording devices", "microphone", ct);

        var defaults = section.Group("Default devices");
        foreach (var (role, label) in new[]
                 {
                     (Role.Console, "Default playback"),
                     (Role.Communications, "Default communications playback")
                 })
        {
            string value;
            try
            {
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
                value = device?.FriendlyName ?? "Not set";
            }
            catch
            {
                value = "Not set";
            }
            defaults.Items.Add(InfoItem.Of($"audio.default.{label.Replace(' ', '_')}", label, value, DetailLevel.Basic,
                "The device Windows uses for sounds (and for calls, for the communications role).", source));
        }

        progress?.Report(1);
        return section;
    }

    private static void AddDevices(InfoSection section, MMDeviceEnumerator enumerator, DataFlow flow, string groupTitle,
        string prefix, CancellationToken ct)
    {
        var group = section.Group(groupTitle);
        const string source = "Source: MMDevice API";

        List<MMDevice> devices;
        try
        {
            devices = new List<MMDevice>(enumerator.EnumerateAudioEndPoints(flow, DeviceState.All));
        }
        catch (Exception ex)
        {
            group.Items.Add(InfoItem.MissingValue($"{prefix}.error", groupTitle, MissingReason.Error, DetailLevel.Basic, source));
            System.Diagnostics.Debug.WriteLine($"[PcChecker] Audio enumeration failed: {ex.Message}");
            return;
        }

        if (devices.Count == 0)
        {
            group.Items.Add(InfoItem.MissingValue($"{prefix}.none", groupTitle, MissingReason.NotReported, DetailLevel.Basic, source));
            return;
        }

        for (int i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            try
            {
                var name = device.FriendlyName;
                var state = device.State switch
                {
                    DeviceState.Active => "Active",
                    DeviceState.Disabled => "Disabled",
                    DeviceState.Unplugged => "Unplugged",
                    _ => device.State.ToString()
                };

                var item = InfoItem.Of($"{prefix}.{i}", i == 0 ? groupTitle.TrimEnd('s') : $"{prefix} {i + 1}",
                    $"{name} · {state}", DetailLevel.Basic,
                    flow == DataFlow.Render ? "A device Windows can play sound on." : "A device Windows can record sound from.",
                    source);
                if (device.State != DeviceState.Active) item.Status = InfoStatus.Info;
                group.Items.Add(item);

                if (device.State == DeviceState.Active)
                {
                    try
                    {
                        var format = device.AudioClient?.MixFormat;
                        if (format != null)
                        {
                            group.Items.Add(InfoItem.Of($"{prefix}.{i}.format", $"{name} format",
                                $"{format.SampleRate / 1000.0:0.#} kHz · {format.BitsPerSample}-bit · {format.Channels} ch",
                                DetailLevel.Detailed, "The shared-mode format Windows mixes this device at.", source));
                        }
                    }
                    catch { }

                    try
                    {
                        var volume = device.AudioEndpointVolume;
                        var percent = volume.MasterVolumeLevelScalar * 100;
                        group.Items.Add(InfoItem.Of($"{prefix}.{i}.volume", $"{name} volume",
                            $"{percent:0}%{(volume.Mute ? " · muted" : string.Empty)}", DetailLevel.Detailed,
                            "The current level of this device.", source));
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PcChecker] Audio device read failed: {ex.Message}");
            }
            finally
            {
                device.Dispose();
            }

            ct.ThrowIfCancellationRequested();
        }
    }
}
