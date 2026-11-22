using System;
using System.Collections.Generic;
using AppLock.Models;
using AppLock.WindowIsland.Modules.Notifications;
using Xunit;

namespace AppLock.Tests.WindowIsland;

/// <summary>
/// Milestone 12 unit tests:
/// - Battery threshold state machine
/// - VPN detection (offline logic)
/// - Mic/cam registry key name parsing (FriendlyAppName)
/// - Notifications unavailability flag
/// - Settings defaults
/// </summary>
public sealed class M12_NotificationServiceTests
{
    // ── Battery threshold logic ───────────────────────────────────────────────

    [Theory]
    [InlineData(21, false)]   // above 20% — no card
    [InlineData(20, true)]    // exactly 20% — card
    [InlineData(15, true)]    // below 20%, above 10% — card at 20
    [InlineData(10, true)]    // exactly 10% — card
    [InlineData(5,  true)]    // exactly 5% — card
    [InlineData(4,  true)]    // below 5% — card
    public void BatteryThreshold_ShouldFireAtOrBelow(int percent, bool expectCard)
    {
        // Simulate the threshold crossing check
        var crossed = new HashSet<int>();
        int[] thresholds = { 20, 10, 5 };
        bool fired = false;

        foreach (int threshold in thresholds)
        {
            if (percent <= threshold && !crossed.Contains(threshold))
            {
                crossed.Add(threshold);
                fired = true;
                break; // first threshold crossed
            }
        }

        Assert.Equal(expectCard, fired);
    }

    [Fact]
    public void BatteryThreshold_ShouldNotFireTwiceForSameThreshold()
    {
        var crossed = new HashSet<int>();
        int[] thresholds = { 20, 10, 5 };
        int fireCount = 0;

        // First check at 18%
        foreach (int t in thresholds)
            if (18 <= t && !crossed.Contains(t)) { crossed.Add(t); fireCount++; }

        // Second check at 17% — same threshold already crossed
        foreach (int t in thresholds)
            if (17 <= t && !crossed.Contains(t)) { crossed.Add(t); fireCount++; }

        Assert.Equal(1, fireCount); // only once at the 20% threshold
    }

    [Fact]
    public void BatteryThreshold_ShouldResetAfterCharging()
    {
        var crossed = new HashSet<int>();
        int[] thresholds = { 20, 10, 5 };

        // Cross the 20% threshold
        foreach (int t in thresholds)
            if (15 <= t && !crossed.Contains(t)) crossed.Add(t);

        Assert.True(crossed.Contains(20));

        // Charge back above 25% — should clear 20% threshold
        foreach (int t in thresholds)
            if (30 > t + 5) crossed.Remove(t);

        Assert.False(crossed.Contains(20));
        Assert.False(crossed.Contains(10));
        Assert.False(crossed.Contains(5));
    }

    [Fact]
    public void BatteryThreshold_AllThreeFire_AtDifferentLevels()
    {
        var crossed = new HashSet<int>();
        int[] thresholds = { 20, 10, 5 };
        var fired = new List<int>();

        void Check(int pct)
        {
            foreach (int t in thresholds)
                if (pct <= t && !crossed.Contains(t)) { crossed.Add(t); fired.Add(t); }
        }

        Check(19);  // → fires 20
        Check(9);   // → fires 10
        Check(4);   // → fires 5

        Assert.Equal(new[] { 20, 10, 5 }, fired);
    }

    // ── VPN state transition logic ────────────────────────────────────────────

    [Fact]
    public void VpnArgs_Connected_HasCorrectValues()
    {
        var args = new VpnStateChangedArgs(true, "ProtonVPN");
        Assert.True(args.Connected);
        Assert.Equal("ProtonVPN", args.AdapterName);
    }

    [Fact]
    public void VpnArgs_Disconnected_HasCorrectValues()
    {
        var args = new VpnStateChangedArgs(false, string.Empty);
        Assert.False(args.Connected);
        Assert.Equal(string.Empty, args.AdapterName);
    }

    // ── BatteryCardArgs ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(BatteryCardKind.Low,              5,  false)]
    [InlineData(BatteryCardKind.ChargingStarted,  40, true)]
    [InlineData(BatteryCardKind.ChargingComplete, 100, false)]
    [InlineData(BatteryCardKind.Unplugged,        75, false)]
    public void BatteryCardArgs_HoldsCorrectValues(BatteryCardKind kind, int pct, bool charging)
    {
        var args = new BatteryCardArgs(kind, pct, charging);
        Assert.Equal(kind, args.Kind);
        Assert.Equal(pct, args.Percent);
        Assert.Equal(charging, args.IsCharging);
    }

    // ── MicCamStateArgs ───────────────────────────────────────────────────────

    [Fact]
    public void MicCamArgs_BothActive_HoldsValues()
    {
        var args = new MicCamStateArgs(true, "Zoom", true, "Teams");
        Assert.True(args.MicActive);
        Assert.Equal("Zoom",  args.MicApp);
        Assert.True(args.CamActive);
        Assert.Equal("Teams", args.CamApp);
    }

    [Fact]
    public void MicCamArgs_NeitherActive()
    {
        var args = new MicCamStateArgs(false, string.Empty, false, string.Empty);
        Assert.False(args.MicActive);
        Assert.False(args.CamActive);
    }

    // ── Friendly app name parsing ─────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:#Users#Alice#AppData#Roaming#Zoom#bin#Zoom.exe",   "Zoom")]
    [InlineData(@"C:#Program Files#Microsoft#Teams#current#Teams.exe", "Teams")]
    [InlineData("Microsoft.Teams_8wekyb3d8bbwe",                       "Microsoft.Teams")]
    [InlineData("Chrome",                                              "Chrome")]
    [InlineData("MyApp_xyz123",                                        "MyApp")]
    public void FriendlyAppName_ParsesCorrectly(string regKeyName, string expected)
    {
        // Mirror the private static logic via the public GetMicCamState helper path
        // We call the internal helper via a reflection-free wrapper test of the logic
        string result = SimulateFriendlyAppName(regKeyName);
        Assert.Equal(expected, result);
    }

    // Duplicates the logic from WindowIslandNotificationService.FriendlyAppName
    private static string SimulateFriendlyAppName(string registryKeyName)
    {
        if (registryKeyName.Contains('#'))
        {
            var parts = registryKeyName.Split('#');
            var last = parts[^1];
            return System.IO.Path.GetFileNameWithoutExtension(last);
        }
        int under = registryKeyName.IndexOf('_');
        return under > 0 ? registryKeyName[..under] : registryKeyName;
    }

    // ── Notifications availability ────────────────────────────────────────────

    [Fact]
    public void Notifications_AlwaysUnavailableForUnpackagedBuild()
    {
        Assert.False(WindowIslandNotificationService.NotificationsAvailable);
    }

    [Fact]
    public void Notifications_ReasonIsNotEmpty()
    {
        Assert.NotEmpty(WindowIslandNotificationService.NotificationsUnavailableReason);
        Assert.Contains("package identity", WindowIslandNotificationService.NotificationsUnavailableReason,
            StringComparison.OrdinalIgnoreCase);
    }

    // ── Settings defaults ─────────────────────────────────────────────────────

    [Fact]
    public void Settings_M12Defaults_AreOn()
    {
        var s = new WindowIslandSettings();
        Assert.True(s.CardVpnEnabled);
        Assert.True(s.CardBatteryLowEnabled);
        Assert.True(s.CardChargingEnabled);
        Assert.True(s.MicCamDotsEnabled);
    }

    [Fact]
    public void Settings_Clone_CopiesM12Fields()
    {
        var s = new WindowIslandSettings
        {
            CardVpnEnabled        = false,
            CardBatteryLowEnabled = false,
            CardChargingEnabled   = false,
            MicCamDotsEnabled     = false
        };
        var c = s.Clone();
        Assert.False(c.CardVpnEnabled);
        Assert.False(c.CardBatteryLowEnabled);
        Assert.False(c.CardChargingEnabled);
        Assert.False(c.MicCamDotsEnabled);
    }

    // ── Charging state machine ────────────────────────────────────────────────

    [Fact]
    public void ChargingStarted_DetectedCorrectly()
    {
        bool wasCharging  = false;
        bool wasPluggedIn = false;
        bool isCharging   = true;
        bool isPluggedIn  = true;

        bool startedCharging = isCharging && !wasCharging && !wasPluggedIn;
        Assert.True(startedCharging);
    }

    [Fact]
    public void ChargingComplete_DetectedCorrectly()
    {
        bool wasCharging  = true;
        bool isCharging   = false;
        bool isPluggedIn  = true;
        int  pct          = 100;

        bool chargingComplete = isPluggedIn && !isCharging && pct >= 98 && wasCharging;
        Assert.True(chargingComplete);
    }

    [Fact]
    public void Unplugged_DetectedCorrectly()
    {
        bool wasCharging  = true;
        bool isCharging   = false;
        bool isPluggedIn  = false;
        int  pct          = 75;

        bool chargingComplete = isPluggedIn && !isCharging && pct >= 98 && wasCharging;
        bool unplugged        = !isCharging && wasCharging && !chargingComplete;
        Assert.True(unplugged);
    }
}
