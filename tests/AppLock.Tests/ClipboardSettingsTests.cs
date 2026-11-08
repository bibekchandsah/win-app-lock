using AppLock.Models;
using Xunit;

namespace AppLock.Tests;

public class ClipboardSettingsTests
{
    [Fact]
    public void ClipboardSettings_Defaults_AreValid()
    {
        var settings = new ClipboardSettings();

        Assert.False(settings.Enabled);
        Assert.Equal(100, settings.MaxItems);
        Assert.Equal("Ctrl+Shift+V", settings.Hotkey);
        Assert.False(settings.OverrideWinV);
        Assert.Equal("cursor", settings.PopupPosition);
        Assert.True(settings.AutoPaste);
        Assert.True(settings.CaptureText);
        Assert.True(settings.CaptureImages);
        Assert.False(settings.CaptureFiles);
        Assert.True(settings.KeepRichText);
        Assert.Equal(10, settings.MaxImageMb);
        Assert.Equal(1024, settings.MaxTextKb);
        Assert.Equal("System", settings.ThemeMode);
    }

    [Fact]
    public void ClipboardSettings_Clamp_ClampsInvalidValues()
    {
        var settings = new ClipboardSettings
        {
            MaxItems = 5,       // Min is 10
            MaxImageMb = 0,     // Min is 1
            MaxTextKb = 0,      // Min is 1
            StorageCapMb = -5,  // Min is 10
            AutoDeleteDays = -1,// Min is 0
            SensitiveExpireSeconds = -10, // Min is 0
            PopupPosition = "invalid_pos",
            ThemeMode = "NeonGreen"
        };

        settings.Clamp();

        Assert.Equal(10, settings.MaxItems);
        Assert.Equal(1, settings.MaxImageMb);
        Assert.Equal(1, settings.MaxTextKb);
        Assert.Equal(10, settings.StorageCapMb);
        Assert.Equal(0, settings.AutoDeleteDays);
        Assert.Equal(0, settings.SensitiveExpireSeconds);
        Assert.Equal("cursor", settings.PopupPosition);
        Assert.Equal("System", settings.ThemeMode);
    }

    [Fact]
    public void ClipboardSettings_Clamp_PreservesValidCustomValues()
    {
        var settings = new ClipboardSettings
        {
            MaxItems = 500,
            MaxImageMb = 20,
            MaxTextKb = 2048,
            StorageCapMb = 1000,
            AutoDeleteDays = 30,
            PopupPosition = "center",
            ThemeMode = "Dark",
            AutoPaste = false
        };

        settings.Clamp();

        Assert.Equal(500, settings.MaxItems);
        Assert.Equal(20, settings.MaxImageMb);
        Assert.Equal(2048, settings.MaxTextKb);
        Assert.Equal(1000, settings.StorageCapMb);
        Assert.Equal(30, settings.AutoDeleteDays);
        Assert.Equal("center", settings.PopupPosition);
        Assert.Equal("Dark", settings.ThemeMode);
        Assert.False(settings.AutoPaste);
    }
}
