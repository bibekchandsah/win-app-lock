using System;
using System.Collections.Generic;
using System.Linq;

namespace AppLock.Models.PcChecker;

/// <summary>One entry of the PC Checker category rail.</summary>
public sealed class PcCategory
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    /// <summary>Segoe Fluent Icons glyph.</summary>
    public string Glyph { get; init; } = "\uE946";

    /// <summary>Provider ids that fill this category. Empty until the category's milestone lands.</summary>
    public string[] ProviderIds { get; init; } = Array.Empty<string>();

    /// <summary>True when part of this category can only be read with administrator rights.</summary>
    public bool RequiresAdmin { get; init; }

    public bool HasProviders => ProviderIds.Length > 0;
}

/// <summary>The category rail, in the order required by the spec (section 3.1).</summary>
public static class PcCategories
{
    public const string OverviewId = "overview";

    public static IReadOnlyList<PcCategory> All { get; } = new List<PcCategory>
    {
        new() { Id = OverviewId, Title = "Overview", Glyph = "\uE71D", ProviderIds = new[] { "os", "cpu", "memory", "gpu", "storage" } },
        new() { Id = "os", Title = "Operating system", Glyph = "\uE774", ProviderIds = new[] { "os" } },
        new() { Id = "firmware", Title = "Motherboard & firmware", Glyph = "\uE9F3", ProviderIds = new[] { "firmware" }, RequiresAdmin = true },
        new() { Id = "cpu", Title = "Processor", Glyph = "\uE9E9", ProviderIds = new[] { "cpu" } },
        new() { Id = "memory", Title = "Memory", Glyph = "\uE8D2", ProviderIds = new[] { "memory" } },
        new() { Id = "storage", Title = "Storage", Glyph = "\uE8B7", ProviderIds = new[] { "storage", "storage.health" }, RequiresAdmin = true },
        new() { Id = "graphics", Title = "Graphics", Glyph = "\uE785", ProviderIds = new[] { "gpu" } },
        new() { Id = "displays", Title = "Displays", Glyph = "\uE8B9", ProviderIds = new[] { "display" } },
        new() { Id = "audio", Title = "Audio", Glyph = "\uE767", ProviderIds = new[] { "audio" } },
        new() { Id = "network", Title = "Network", Glyph = "\uE701", ProviderIds = new[] { "network" } },
        new() { Id = "battery", Title = "Battery & power", Glyph = "\uEDA2", ProviderIds = new[] { "battery" } },
        new() { Id = "devices", Title = "Devices & peripherals", Glyph = "\uE71B", ProviderIds = new[] { "devices" } },
        new() { Id = "sensors", Title = "Sensors", Glyph = "\uE946" },
        new() { Id = "software", Title = "Software", Glyph = "\uE8D7" },
        new() { Id = "security", Title = "Security", Glyph = "\uE72E" },
        new() { Id = "performance", Title = "Performance (live)", Glyph = "\uEC4A" },
        new() { Id = "stability", Title = "Stability & events", Glyph = "\uE7BA" },
        new() { Id = "health", Title = "Health check", Glyph = "\uE73E" },
        new() { Id = "tools", Title = "Tools", Glyph = "\uE90F" },
        new() { Id = "compatibility", Title = "Compatibility", Glyph = "\uE762" },
        new() { Id = "reports", Title = "Reports & history", Glyph = "\uE712" }
    };

    public static PcCategory? ById(string id)
        => All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
}
