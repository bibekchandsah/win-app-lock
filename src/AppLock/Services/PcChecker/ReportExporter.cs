using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using AppLock.Models.PcChecker;

namespace AppLock.Services.PcChecker;

/// <summary>
/// Writes PC Checker data as text, JSON, CSV or a single self-contained HTML file. Identifiers are redacted
/// by default, every value is HTML-escaped, control characters are stripped and lengths are capped.
/// </summary>
public static class ReportExporter
{
    public const string RedactionMark = "••••••";

    public sealed class Options
    {
        public DetailLevel Level { get; init; } = DetailLevel.Detailed;
        public bool RedactIdentifiers { get; init; } = true;
        public bool IncludeSoftware { get; init; } = true;
        public string? Title { get; init; }
    }

    public static string BuildText(IEnumerable<InfoSection> sections, Options options)
    {
        var builder = new StringBuilder();
        builder.AppendLine("PC Checker report");
        builder.AppendLine($"Generated {DateTime.Now.ToString("g", CultureInfo.CurrentCulture)}");
        builder.AppendLine($"Scope: {(options.RedactIdentifiers ? "identifiers redacted" : "identifiers shown")}");
        builder.AppendLine();

        foreach (var section in sections)
        {
            if (!options.IncludeSoftware && section.Id == "software") continue;

            builder.AppendLine(section.Title.ToUpperInvariant());

            if (!string.IsNullOrEmpty(section.Error))
            {
                builder.AppendLine($"  Not available ({section.Error})");
                builder.AppendLine();
                continue;
            }

            foreach (var group in section.Groups)
            {
                if (!group.Items.Any(i => i.Level <= options.Level)) continue;

                builder.AppendLine($"  {group.Title}");
                foreach (var item in group.Items.Where(i => i.Level <= options.Level))
                {
                    var value = Display(item, options.RedactIdentifiers);
                    builder.AppendLine($"    {Sanitize(item.Label)}: {value}");
                }
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    public static string BuildJson(IEnumerable<InfoSection> sections, Options options)
    {
        var payload = sections
            .Where(s => options.IncludeSoftware || s.Id != "software")
            .Select(section => new
            {
                section.Id,
                section.Title,
                section.Error,
                section.CollectedAt,
                Groups = section.Groups.Select(g => new
                {
                    g.Title,
                    Items = g.Items.Where(i => i.Level <= options.Level).Select(i => new
                    {
                        i.Label,
                        Value = Display(i, options.RedactIdentifiers),
                        i.Key,
                        Status = i.Status.ToString(),
                        i.Source
                    })
                })
            });

        return JsonSerializer.Serialize(new
        {
            tool = "AppLock PC Checker",
            generated = DateTime.Now,
            redacted = options.RedactIdentifiers,
            sections = payload
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string BuildCsv(IEnumerable<InfoSection> sections, Options options)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Section,Group,Label,Value,Status,Source");

        foreach (var section in sections)
        {
            if (!options.IncludeSoftware && section.Id == "software") continue;

            foreach (var group in section.Groups)
            {
                foreach (var item in group.Items.Where(i => i.Level <= options.Level))
                {
                    builder.AppendLine(string.Join(",",
                        Csv(section.Title),
                        Csv(group.Title),
                        Csv(item.Label),
                        Csv(Display(item, options.RedactIdentifiers)),
                        Csv(item.Status.ToString()),
                        Csv(item.Source)));
                }
            }
        }

        return builder.ToString();
    }

    public static string BuildHtml(IEnumerable<InfoSection> sections, Options options)
    {
        var title = string.IsNullOrWhiteSpace(options.Title) ? "PC Checker report" : options.Title!;
        var builder = new StringBuilder();

        builder.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
        builder.AppendLine($"<title>{Escape(title)}</title>");
        builder.AppendLine("<style>");
        builder.AppendLine("body{font-family:'Segoe UI',system-ui,sans-serif;margin:24px;background:#ffffff;color:#1b1b1b}");
        builder.AppendLine("@media (prefers-color-scheme:dark){body{background:#1f1f1f;color:#f3f3f3}table{color:#f3f3f3}th{background:#2d2d2d}}");
        builder.AppendLine("h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:24px 0 6px}h3{font-size:13px;margin:14px 0 4px;opacity:.7}");
        builder.AppendLine("table{border-collapse:collapse;width:100%;font-size:13px}th,td{text-align:left;padding:5px 8px;border-bottom:1px solid #00000022;vertical-align:top}");
        builder.AppendLine("th{background:#00000008;font-weight:600;width:34%}td{white-space:pre-wrap}");
        builder.AppendLine("</style></head><body>");
        builder.AppendLine($"<h1>{Escape(title)}</h1>");
        builder.AppendLine($"<p>{Escape(DateTime.Now.ToString("g", CultureInfo.CurrentCulture))} · {(options.RedactIdentifiers ? "identifiers redacted" : "identifiers shown")}</p>");

        foreach (var section in sections)
        {
            if (!options.IncludeSoftware && section.Id == "software") continue;

            builder.AppendLine($"<h2>{Escape(section.Title)}</h2>");
            if (!string.IsNullOrEmpty(section.Error))
            {
                builder.AppendLine($"<p>{Escape(section.Error)}</p>");
                continue;
            }

            foreach (var group in section.Groups)
            {
                var items = group.Items.Where(i => i.Level <= options.Level).ToList();
                if (items.Count == 0) continue;

                builder.AppendLine($"<h3>{Escape(group.Title)}</h3>");
                builder.AppendLine("<table>");
                foreach (var item in items)
                {
                    builder.AppendLine($"<tr><th>{Escape(item.Label)}</th><td>{Escape(Display(item, options.RedactIdentifiers))}</td></tr>");
                }
                builder.AppendLine("</table>");
            }
        }

        builder.AppendLine("</body></html>");
        return builder.ToString();
    }

    /// <summary>Short block for pasting into a chat or a support forum.</summary>
    public static string BuildSummary(IEnumerable<InfoSection> sections, Options options)
    {
        string? Value(string sectionId, string key)
            => sections.FirstOrDefault(s => s.Id == sectionId)?.Find(key) is { } item
                ? Display(item, options.RedactIdentifiers)
                : null;

        var lines = new List<string>
        {
            "PC Checker summary",
            $"OS: {Value("os", "os.name")} · {Value("os", "os.build")}",
            $"CPU: {Value("cpu", "cpu.name")} · {Value("cpu", "cpu.cores")} cores / {Value("cpu", "cpu.threads")} threads",
            $"Memory: {Value("memory", "memory.installed")}",
            $"GPU: {Value("gpu", "gpu.0.name")}",
            $"Storage: {Value("storage", "storage.total")}",
            $"Generated: {DateTime.Now.ToString("g", CultureInfo.CurrentCulture)}"
        };

        return string.Join(Environment.NewLine, lines.Where(l => !l.EndsWith(": ", StringComparison.Ordinal)));
    }

    private static string Display(InfoItem item, bool redact)
    {
        var text = item.Value;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = item.Missing == MissingReason.None ? "Not available" : InfoItem.ReasonText(item.Missing);
        }

        if (redact && item.Sensitivity == InfoSensitivity.Identifier) return RedactionMark;
        return Sanitize(text);
    }

    private static string Csv(string? value)
    {
        var text = Sanitize(value ?? string.Empty).Replace("\"", "\"\"");
        return $"\"{text}\"";
    }

    private static string Escape(string value) => System.Net.WebUtility.HtmlEncode(Sanitize(value));

    /// <summary>Drops control characters and caps the length so odd device names cannot break a report.</summary>
    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var builder = new StringBuilder(Math.Min(value.Length, 4000));
        foreach (var ch in value)
        {
            if (char.IsControl(ch) && ch != '\n' && ch != '\t') continue;
            builder.Append(ch);
            if (builder.Length >= 4000) break;
        }

        return builder.ToString();
    }
}
