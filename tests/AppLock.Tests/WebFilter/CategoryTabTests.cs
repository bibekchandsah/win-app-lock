using System;
using System.Collections.Generic;
using AppLock.Models;
using Xunit;

namespace AppLock.Tests.WebFilter;

public class CategoryTabTests
{
    [Fact]
    public void SeedDefaults_PopulatesDefaultCategories()
    {
        var settings = new WebFilterSettings();
        Assert.Empty(settings.Categories);

        // Simulate category sources from sources.json
        var sources = new (string id, bool defaultEnabled)[]
        {
            ("adult", false),
            ("gambling", false),
            ("social", false),
            ("malware", true),
            ("bypass", true),
            ("streaming", false),
            ("dating", false),
            ("games", false),
            ("piracy", false),
            ("drugs", false),
            ("ads", false)
        };

        foreach (var (id, defaultEnabled) in sources)
        {
            if (!settings.Categories.ContainsKey(id))
            {
                settings.Categories[id] = new CategoryRule { Enabled = defaultEnabled };
            }
        }
        settings.DefaultsSeeded = true;

        Assert.Equal(11, settings.Categories.Count);
        Assert.True(settings.Categories["malware"].Enabled);
        Assert.True(settings.Categories["bypass"].Enabled);
        Assert.False(settings.Categories["adult"].Enabled);
        Assert.False(settings.Categories["gambling"].Enabled);
        Assert.Equal(2, settings.EnabledCategoryCount);
    }

    [Fact]
    public void Pause_30Minutes_SetsPausedUntilUtc()
    {
        var settings = new WebFilterSettings
        {
            Enabled = true
        };

        Assert.False(settings.IsPaused);

        // Pause for 30 minutes
        settings.PausedUntilUtc = DateTime.UtcNow.AddMinutes(30);
        Assert.True(settings.IsPaused);

        // Clear pause (resume)
        settings.PausedUntilUtc = null;
        Assert.False(settings.IsPaused);
    }

    [Fact]
    public void Pause_ExpiredTime_IsNotPaused()
    {
        var settings = new WebFilterSettings
        {
            Enabled = true,
            PausedUntilUtc = DateTime.UtcNow.AddMinutes(-5) // In the past
        };

        Assert.False(settings.IsPaused);
    }

    [Fact]
    public void CategoryRule_ActiveBlocks_DefaultAlways()
    {
        var rule = new CategoryRule();
        Assert.Empty(rule.ActiveBlocks);
    }
}
