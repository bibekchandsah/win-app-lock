using System.Collections.Generic;
using System.Linq;
using AppLock.Models.PcChecker;
using AppLock.Services.PcChecker;
using AppLock.Services.PcChecker.Health;

namespace AppLock.Tests;

public class PcCheckerTests
{
    [Theory]
    [InlineData(0x0000009Fu, "0x0000009F DRIVER_POWER_STATE_FAILURE")]
    [InlineData(0x00000124u, "0x00000124 WHEA_UNCORRECTABLE_ERROR")]
    public void BugCheckTable_FormatsKnownCodes(uint code, string expected)
        => Assert.Equal(expected, BugCheckTable.Format(code));

    [Fact]
    public void BugCheckTable_KeepsUnknownCodesReadable()
        => Assert.Equal("0x0000ABCD", BugCheckTable.Format(0xABCDu));

    [Fact]
    public void BugCheckTable_ParsesHexText()
        => Assert.Equal("0x0000009F DRIVER_POWER_STATE_FAILURE", BugCheckTable.Format("0x0000009f"));

    [Fact]
    public void ReportExporter_RedactsIdentifiers()
    {
        var section = Section("storage",
            InfoItem.Of("storage.serial", "Serial", "SN-1234", DetailLevel.Basic, sensitivity: InfoSensitivity.Identifier),
            InfoItem.Of("storage.free", "Free", "100 GB", DetailLevel.Basic));

        var text = ReportExporter.BuildText(new[] { section }, new ReportExporter.Options { RedactIdentifiers = true });

        Assert.Contains(ReportExporter.RedactionMark, text);
        Assert.DoesNotContain("SN-1234", text);
        Assert.Contains("100 GB", text);
    }

    [Fact]
    public void ReportExporter_EscapesHtml()
    {
        var section = Section("os", InfoItem.Of("os.name", "Name", "<script>alert(1)</script>", DetailLevel.Basic));

        var html = ReportExporter.BuildHtml(new[] { section }, new ReportExporter.Options { RedactIdentifiers = true });

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void ReportExporter_Csv_QuotesCommasAndQuotes()
    {
        var section = Section("cpu", InfoItem.Of("cpu.name", "Name", "Core \"i7\", 8 cores", DetailLevel.Basic));

        var csv = ReportExporter.BuildCsv(new[] { section }, new ReportExporter.Options { RedactIdentifiers = true });

        Assert.Contains("\"Core \"\"i7\"\", 8 cores\"", csv);
    }

    [Fact]
    public void ReportExporter_RespectsDetailLevel()
    {
        var section = Section("cpu",
            InfoItem.Of("cpu.name", "Name", "CPU", DetailLevel.Basic),
            InfoItem.Of("cpu.id", "Processor ID", "BFEBFBFF", DetailLevel.Everything));

        var basic = ReportExporter.BuildText(new[] { section }, new ReportExporter.Options { Level = DetailLevel.Basic });

        Assert.Contains("CPU", basic);
        Assert.DoesNotContain("BFEBFBFF", basic);
    }

    [Fact]
    public void HealthRules_ReportLowDiskSpace()
    {
        var storage = Section("storage",
            InfoItem.Of("storage.systemFree", "System drive free space", "2% left", DetailLevel.Basic, status: InfoStatus.Problem));

        var findings = HealthRuleEngine.Evaluate(Sections(storage), new PcCheckerSettings());

        Assert.Contains(findings, f => f.Id == "storage.systemDriveCritical" && f.Severity == InfoStatus.Problem);
    }

    [Fact]
    public void HealthRules_SkipDismissedFindings()
    {
        var storage = Section("storage",
            InfoItem.Of("storage.systemFree", "System drive free space", "2% left", DetailLevel.Basic, status: InfoStatus.Problem));

        var settings = new PcCheckerSettings();
        settings.DismissedFindings.Add("storage.systemDriveCritical");

        var findings = HealthRuleEngine.Evaluate(Sections(storage), settings);

        Assert.DoesNotContain(findings, f => f.Id == "storage.systemDriveCritical");
    }

    [Fact]
    public void HealthRules_OrderWorstFirst()
    {
        var storage = Section("storage",
            InfoItem.Of("storage.systemFree", "System drive free space", "8% left", DetailLevel.Basic, status: InfoStatus.Warning));

        var firmware = Section("firmware",
            InfoItem.Of("firmware.secureBoot", "Secure Boot", "Off", DetailLevel.Basic, status: InfoStatus.Warning));

        var findings = HealthRuleEngine.Evaluate(Sections(storage, firmware), new PcCheckerSettings());

        Assert.NotEmpty(findings);
        for (var i = 1; i < findings.Count; i++)
        {
            Assert.True(findings[i - 1].Severity >= findings[i].Severity);
        }
    }

    [Fact]
    public void Categories_HaveUniqueIdsAndEveryCategoryIsReachable()
    {
        var ids = PcCategories.All.Select(c => c.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(PcCategories.OverviewId, PcCategories.All[0].Id);
    }

    private static InfoSection Section(string id, params InfoItem[] items)
    {
        var section = new InfoSection { Id = id, Title = id, Category = id };
        section.Group("Values").Items.AddRange(items);
        return section;
    }

    private static Dictionary<string, InfoSection> Sections(params InfoSection[] sections)
        => sections.ToDictionary(s => s.Id);
}
