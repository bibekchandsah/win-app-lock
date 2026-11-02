using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AppLock.Models;

namespace AppLock.Services.Firewall;

public class FirewallExportData
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("exportedAt")]
    public DateTime ExportedAt { get; set; }

    [JsonPropertyName("rules")]
    public List<NetworkRuleExport> Rules { get; set; } = new();
}

public class NetworkRuleExport
{
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("exePath")]
    public string ExePath { get; set; } = "";

    [JsonPropertyName("packageFamilyName")]
    public string? PackageFamilyName { get; set; }

    [JsonPropertyName("blockOutbound")]
    public bool BlockOutbound { get; set; }

    [JsonPropertyName("blockInbound")]
    public bool BlockInbound { get; set; }

    [JsonPropertyName("profiles")]
    public int Profiles { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

public class FirewallImportExportService
{
    public async Task<string> ExportToJsonAsync(List<NetworkRuleEntry> rules)
    {
        var exportData = new FirewallExportData
        {
            Version = 1,
            ExportedAt = DateTime.Now,
            Rules = rules.Select(r => new NetworkRuleExport
            {
                DisplayName = r.DisplayName,
                ExePath = r.ExePath,
                PackageFamilyName = r.PackageFamilyName,
                BlockOutbound = r.BlockOutbound,
                BlockInbound = r.BlockInbound,
                Profiles = r.Profiles,
                Notes = r.Notes
            }).ToList()
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        return await Task.Run(() => JsonSerializer.Serialize(exportData, options));
    }

    public async Task ExportToFileAsync(List<NetworkRuleEntry> rules, string filePath)
    {
        var json = await ExportToJsonAsync(rules);
        await File.WriteAllTextAsync(filePath, json);
    }

    public async Task<FirewallExportData?> ImportFromJsonAsync(string json)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var data = await Task.Run(() => JsonSerializer.Deserialize<FirewallExportData>(json, options));
            return data;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<FirewallExportData?> ImportFromFileAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        var json = await File.ReadAllTextAsync(filePath);
        return await ImportFromJsonAsync(json);
    }

    public List<NetworkRuleEntry> ConvertToRules(FirewallExportData exportData)
    {
        return exportData.Rules.Select(r => new NetworkRuleEntry
        {
            Id = Guid.NewGuid(), // Generate new IDs on import
            DisplayName = r.DisplayName,
            ExePath = r.ExePath,
            PackageFamilyName = r.PackageFamilyName,
            BlockOutbound = r.BlockOutbound,
            BlockInbound = r.BlockInbound,
            Profiles = r.Profiles,
            Notes = r.Notes,
            Blocked = false // Don't auto-block on import
        }).ToList();
    }

    public (List<NetworkRuleEntry> New, List<NetworkRuleEntry> Conflicts) 
        CheckConflicts(List<NetworkRuleEntry> importedRules, List<NetworkRuleEntry> existingRules)
    {
        var newRules = new List<NetworkRuleEntry>();
        var conflicts = new List<NetworkRuleEntry>();

        foreach (var rule in importedRules)
        {
            var key = rule.PackageFamilyName ?? rule.ExePath;
            var exists = existingRules.Any(e =>
                (e.PackageFamilyName != null && e.PackageFamilyName.Equals(rule.PackageFamilyName, StringComparison.OrdinalIgnoreCase)) ||
                (e.ExePath.Equals(rule.ExePath, StringComparison.OrdinalIgnoreCase)));

            if (exists)
                conflicts.Add(rule);
            else
                newRules.Add(rule);
        }

        return (newRules, conflicts);
    }
}
