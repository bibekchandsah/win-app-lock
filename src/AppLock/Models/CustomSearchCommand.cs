using System;

namespace AppLock.Models;

/// <summary>
/// Represents a custom or built-in search engine command in the Window Island Command Bar.
/// Supports search shortcuts such as "@youtube nepali music" or "@github winui".
/// </summary>
public sealed class CustomSearchCommand
{
    /// <summary>
    /// Command keyword with prefix, e.g. "@youtube", "@google", "@github".
    /// </summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// Friendly display name, e.g. "YouTube", "Google", "GitHub".
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Search query URL or template.
    /// E.g. "https://www.youtube.com/results?search_query=" or "https://example.com/search?q={q}".
    /// </summary>
    public string QueryUrl { get; set; } = string.Empty;

    /// <summary>
    /// Base website URL when query is empty, e.g. "https://www.youtube.com".
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Segoe Fluent icon glyph.
    /// </summary>
    public string IconGlyph { get; set; } = "\uE774";

    /// <summary>
    /// Accent color in hex for branding.
    /// </summary>
    public string AccentHex { get; set; } = "#60CDFF";

    /// <summary>
    /// Whether this search launches in an incognito/private browser window.
    /// </summary>
    public bool IsIncognito { get; set; }

    /// <summary>
    /// Parameterless constructor for System.Text.Json deserialization.
    /// </summary>
    public CustomSearchCommand() { }

    public CustomSearchCommand(
        string command,
        string name,
        string queryUrl,
        string baseUrl = "",
        string iconGlyph = "\uE774",
        string accentHex = "#60CDFF",
        bool isIncognito = false)
    {
        Command = NormalizeCommand(command);
        Name = name;
        QueryUrl = queryUrl;
        BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? ExtractBaseUrl(queryUrl) : baseUrl;
        IconGlyph = iconGlyph;
        AccentHex = accentHex;
        IsIncognito = isIncognito;
    }

    public static string NormalizeCommand(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return string.Empty;
        cmd = cmd.Trim();
        return cmd.StartsWith("@") ? cmd.ToLowerInvariant() : "@" + cmd.ToLowerInvariant();
    }

    public static string ExtractBaseUrl(string queryUrl)
    {
        if (string.IsNullOrWhiteSpace(queryUrl)) return string.Empty;
        try
        {
            if (Uri.TryCreate(queryUrl, UriKind.Absolute, out var uri))
            {
                return $"{uri.Scheme}://{uri.Authority}";
            }
        }
        catch { }
        return queryUrl;
    }

    /// <summary>
    /// Builds the target search URL given a user search term.
    /// </summary>
    public string BuildUrl(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return !string.IsNullOrWhiteSpace(BaseUrl) ? BaseUrl : QueryUrl;
        }

        string escaped = Uri.EscapeDataString(query.Trim());
        if (QueryUrl.Contains("{q}"))
        {
            return QueryUrl.Replace("{q}", escaped);
        }
        if (QueryUrl.Contains("{query}"))
        {
            return QueryUrl.Replace("{query}", escaped);
        }
        return QueryUrl + escaped;
    }

    public CustomSearchCommand Clone() => new()
    {
        Command = Command,
        Name = Name,
        QueryUrl = QueryUrl,
        BaseUrl = BaseUrl,
        IconGlyph = IconGlyph,
        AccentHex = AccentHex,
        IsIncognito = IsIncognito
    };
}
