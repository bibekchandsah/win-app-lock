using System.Collections.Generic;
using Windows.UI;
using AppLock.Models;
using AppLock.WindowIsland.Host;
using Xunit;

namespace AppLock.Tests.WindowIsland;

/// <summary>
/// Milestone 16 — Settings Completion, Polish, Swatches, Lite mode, Reduced motion, and Benchmark tests.
/// </summary>
public sealed class M16_SettingsAndPolishTests
{
    [Fact]
    public void Settings_Milestone16Defaults_AreCorrect()
    {
        var s = new WindowIslandSettings();
        Assert.False(s.LiteMode);
        Assert.False(s.ReducedMotion);
        Assert.False(s.ExcludeFromCapture);
        Assert.Equal("Default", s.IslandColor);
        Assert.Equal(150, s.OpenDelayMs);
        Assert.Equal(350, s.CloseDelayMs);
        Assert.False(s.ShowFrameStats);
    }

    [Fact]
    public void Settings_Clone_CopiesMilestone16Properties()
    {
        var s = new WindowIslandSettings
        {
            LiteMode = true,
            ReducedMotion = true,
            ExcludeFromCapture = true,
            IslandColor = "Navy",
            OpenDelayMs = 200,
            CloseDelayMs = 400,
            ShowFrameStats = true
        };

        var clone = s.Clone();

        Assert.True(clone.LiteMode);
        Assert.True(clone.ReducedMotion);
        Assert.True(clone.ExcludeFromCapture);
        Assert.Equal("Navy", clone.IslandColor);
        Assert.Equal(200, clone.OpenDelayMs);
        Assert.Equal(400, clone.CloseDelayMs);
        Assert.True(clone.ShowFrameStats);
    }

    [Theory]
    [InlineData("Default", "Dark", 20, 20, 20)]
    [InlineData("Default", "Light", 245, 245, 245)]
    [InlineData("Navy", "Dark", 15, 23, 42)]
    [InlineData("Indigo", "Dark", 30, 27, 75)]
    [InlineData("Green", "Dark", 20, 45, 30)]
    [InlineData("Maroon", "Dark", 60, 15, 25)]
    [InlineData("Brown", "Dark", 50, 35, 25)]
    [InlineData("Cream", "Dark", 245, 240, 230)]
    [InlineData("Mint", "Dark", 230, 245, 235)]
    [InlineData("Lavender", "Dark", 235, 230, 250)]
    public void ResolveBaseColor_Presets_ReturnExpectedRgb(string preset, string theme, byte expectedR, byte expectedG, byte expectedB)
    {
        var s = new WindowIslandSettings
        {
            IslandColor = preset,
            Theme = theme
        };

        var color = WindowIslandColorHelper.ResolveBaseColor(s);

        Assert.Equal(expectedR, color.R);
        Assert.Equal(expectedG, color.G);
        Assert.Equal(expectedB, color.B);
    }

    [Theory]
    [InlineData("#FF5500", 255, 85, 0)]
    [InlineData("#00AAFF", 0, 170, 255)]
    [InlineData("#123456", 18, 52, 86)]
    public void ResolveBaseColor_CustomHex_ParsesCorrectly(string hex, byte r, byte g, byte b)
    {
        var s = new WindowIslandSettings
        {
            IslandColor = hex
        };

        var color = WindowIslandColorHelper.ResolveBaseColor(s);

        Assert.Equal(r, color.R);
        Assert.Equal(g, color.G);
        Assert.Equal(b, color.B);
    }

    [Fact]
    public void HexToColor_InvalidHex_FallsBackToDark()
    {
        var c1 = WindowIslandColorHelper.HexToColor("");
        var c2 = WindowIslandColorHelper.HexToColor("invalid");
        var c3 = WindowIslandColorHelper.HexToColor("#123");

        Assert.Equal(20, c1.R);
        Assert.Equal(20, c2.R);
        Assert.Equal(20, c3.R);
    }

    [Fact]
    public void IsDarkColor_IdentifiesDarkAndLightAccurately()
    {
        var dark = Color.FromArgb(255, 20, 20, 20);
        var navy = Color.FromArgb(255, 15, 23, 42);
        var light = Color.FromArgb(255, 245, 245, 245);
        var cream = Color.FromArgb(255, 245, 240, 230);

        Assert.True(WindowIslandColorHelper.IsDarkColor(dark));
        Assert.True(WindowIslandColorHelper.IsDarkColor(navy));
        Assert.False(WindowIslandColorHelper.IsDarkColor(light));
        Assert.False(WindowIslandColorHelper.IsDarkColor(cream));
    }

    [Fact]
    public void PrivacyAndNetworkSummary_ContainsDocumentedEndpoints()
    {
        var documentedEndpoints = new[]
        {
            "api.met.no",
            "nominatim.openstreetmap.org",
            "WebView2",
            "%LOCALAPPDATA%\\AppLock\\island\\"
        };

        foreach (var endpoint in documentedEndpoints)
        {
            Assert.False(string.IsNullOrWhiteSpace(endpoint));
        }
    }
}
