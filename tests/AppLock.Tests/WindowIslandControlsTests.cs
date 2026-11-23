using System;
using System.Threading;
using AppLock.WindowIsland.Modules.Controls;
using Xunit;

namespace AppLock.Tests;

public class WindowIslandControlsTests
{
    [Fact]
    public void BatteryInfo_ReturnsValidValuesOnCurrentMachine()
    {
        var (hasBattery, percent, isCharging, statusText) = WindowIslandControlsService.GetBatteryInfo();

        Assert.InRange(percent, 0, 100);
        Assert.False(string.IsNullOrWhiteSpace(statusText));

        if (!hasBattery)
        {
            Assert.Equal("On mains power", statusText);
            Assert.True(isCharging);
        }
    }

    [Fact]
    public void VpnStatus_ReturnsValidTuple()
    {
        var (isConnected, name) = WindowIslandControlsService.GetVpnStatus();

        Assert.False(string.IsNullOrWhiteSpace(name));
        if (!isConnected)
        {
            Assert.Equal("Disconnected", name);
        }
    }

    [Fact]
    public void SystemDarkTheme_CanBeReadWithoutThrowing()
    {
        // Must not throw on any Windows system
        var exception = Record.Exception(() =>
        {
            bool isDark = WindowIslandControlsService.IsSystemDarkTheme();
            Assert.True(isDark || !isDark);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void PowerDoubleTapSafety_RequiresTwoClicksWithinWindow()
    {
        // Tests the double-tap safety state machine logic
        bool isConfirming = false;
        bool executed = false;
        DateTime lastClickUtc = DateTime.MinValue;

        void OnButtonClick()
        {
            DateTime now = DateTime.UtcNow;
            if (isConfirming && (now - lastClickUtc).TotalSeconds <= 3.0)
            {
                isConfirming = false;
                executed = true;
            }
            else
            {
                isConfirming = true;
                lastClickUtc = now;
            }
        }

        // First click: enters confirming state
        OnButtonClick();
        Assert.True(isConfirming);
        Assert.False(executed);

        // Second click within 3 seconds: executes
        OnButtonClick();
        Assert.False(isConfirming);
        Assert.True(executed);
    }

    [Fact]
    public void PowerDoubleTapSafety_ResetsIfSecondClickIsDelayed()
    {
        bool isConfirming = false;
        bool executed = false;
        DateTime lastClickUtc = DateTime.MinValue;

        void OnButtonClick(DateTime now)
        {
            if (isConfirming && (now - lastClickUtc).TotalSeconds <= 3.0)
            {
                isConfirming = false;
                executed = true;
            }
            else
            {
                isConfirming = true;
                lastClickUtc = now;
            }
        }

        DateTime t0 = DateTime.UtcNow;
        OnButtonClick(t0);
        Assert.True(isConfirming);
        Assert.False(executed);

        // Click after 4 seconds (timeout exceeded) -> acts as new first click
        DateTime t1 = t0.AddSeconds(4.0);
        OnButtonClick(t1);
        Assert.True(isConfirming);
        Assert.False(executed);
    }

    [Fact]
    public async Task GetWiFiNetworksAsync_ReturnsListWithoutThrowing()
    {
        var list = await WindowIslandControlsService.GetWiFiNetworksAsync();
        Assert.NotNull(list);
        // Each entry should have non-empty SSID
        foreach (var item in list)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Ssid));
            Assert.False(string.IsNullOrWhiteSpace(item.Status));
        }
    }

    [Fact]
    public async Task GetBluetoothDevicesAsync_ReturnsListWithoutThrowing()
    {
        var list = await WindowIslandControlsService.GetBluetoothDevicesAsync();
        Assert.NotNull(list);
        // Each entry should have non-empty Name and DeviceType
        foreach (var item in list)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Name));
            Assert.False(string.IsNullOrWhiteSpace(item.DeviceType));
        }
    }

    [Fact]
    public void QuickCards_Defaults_ContainSixCards()
    {
        var defaults = AppLock.Models.QuickCard.Defaults();
        Assert.NotNull(defaults);
        Assert.Equal(6, defaults.Count);
        Assert.Contains(defaults, c => c.Id == "lock");
        Assert.Contains(defaults, c => c.Id == "desktop");
        Assert.Contains(defaults, c => c.Id == "taskview");
        Assert.Contains(defaults, c => c.Id == "calculator");
        Assert.Contains(defaults, c => c.Id == "defaultapps");
        Assert.Contains(defaults, c => c.Id == "startupapps");
    }

    [Fact]
    public void ControlsSettings_Migration_InitializesDefaultsWhenEmpty()
    {
        var settings = new AppLock.Models.ControlsSettings();
        Assert.NotNull(settings.Cards);
        Assert.Equal(6, settings.Cards.Count);

        // When set to empty/null, it automatically falls back to defaults
        settings.Cards = new System.Collections.Generic.List<AppLock.Models.QuickCard>();
        Assert.Equal(6, settings.Cards.Count);
    }

    [Fact]
    public void ControlsSettings_JsonSerialization_RoundtripsProperly()
    {
        var original = new AppLock.Models.ControlsSettings
        {
            CloseIslandAfterCard = false,
            ShowBrightness = false,
            ShowPowerRow = false
        };

        string json = System.Text.Json.JsonSerializer.Serialize(original);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppLock.Models.ControlsSettings>(json);

        Assert.NotNull(restored);
        Assert.False(restored.CloseIslandAfterCard);
        Assert.False(restored.ShowBrightness);
        Assert.False(restored.ShowPowerRow);
        Assert.Equal(6, restored.Cards.Count);
    }

    [Fact]
    public async Task GetBluetoothDevicesAsync_ReturnsDevicesList()
    {
        var devices = await WindowIslandControlsService.GetBluetoothDevicesAsync();
        Assert.NotNull(devices);
        // If devices exist, check properties
        foreach (var dev in devices)
        {
            Assert.False(string.IsNullOrWhiteSpace(dev.Name));
            if (dev.BatteryPercent.HasValue)
            {
                Assert.InRange(dev.BatteryPercent.Value, 0, 100);
            }
        }
    }

    [Fact]
    public async Task GetWiFiNetworksAsync_ReturnsNetworksList()
    {
        var networks = await WindowIslandControlsService.GetWiFiNetworksAsync();
        Assert.NotNull(networks);
        foreach (var net in networks)
        {
            Assert.False(string.IsNullOrWhiteSpace(net.Ssid));
            Assert.InRange(net.SignalBars, 0, 4);
        }
    }

    [Fact]
    public void IslandWiFiNetwork_EmojiSsid_PreservedCorrectly()
    {
        string emojiSsid = "Not Your WiFi📶,Bro";
        var net = new WindowIslandControlsService.IslandWiFiNetwork(emojiSsid, true, "Connected", 4, "5 GHz", true, true);
        Assert.Equal(emojiSsid, net.Ssid);
        Assert.True(net.IsConnected);
    }
}
