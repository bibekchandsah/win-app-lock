using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AppLock.Models;

namespace AppLock.WindowIsland.Modules.Chat;

/// <summary>
/// Milestone 13: Manages AI chat sites, URL validation, and state for the Chat tab.
/// Encapsulates site list logic, idle timeout calculation, and user-data folder resolution.
/// </summary>
public static class WindowIslandChatManager
{
    public const int MaxChatSites = 6;
    public const int MinIdleMinutes = 1;
    public const int MaxIdleMinutes = 60;
    public const int DefaultIdleMinutes = 5;

    /// <summary>
    /// Gets the dedicated WebView2 user data folder for Window Island chat logins.
    /// Spec §9.4: %LOCALAPPDATA%\AppLock\island\webview so logins persist.
    /// </summary>
    public static string GetUserDataFolder()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "AppLock", "island", "webview");
        if (!Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
        return dir;
    }

    /// <summary>
    /// Validates whether a URL string is a valid web URL (http/https).
    /// </summary>
    public static bool IsValidUrl(string? url, out Uri? validUri)
    {
        validUri = null;
        if (string.IsNullOrWhiteSpace(url)) return false;

        string trimmed = url.Trim();

        // If it's already an absolute URI, verify the scheme is http or https
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var directUri))
        {
            if (directUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                directUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                validUri = directUri;
                return true;
            }
            return false;
        }

        // Scheme missing (e.g. "chat.openai.com") -> prepend https:// and test
        string withHttps = "https://" + trimmed;
        if (Uri.TryCreate(withHttps, UriKind.Absolute, out var httpsUri) &&
            httpsUri.Scheme == Uri.UriSchemeHttps &&
            !string.IsNullOrWhiteSpace(httpsUri.Host) &&
            httpsUri.Host.Contains('.'))
        {
            validUri = httpsUri;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Normalizes and cleans a user-provided site URL.
    /// </summary>
    public static string NormalizeUrl(string url)
    {
        if (IsValidUrl(url, out var uri) && uri != null)
        {
            return uri.ToString();
        }
        return url.Trim();
    }

    /// <summary>
    /// Adds a new chat site to the settings if within bounds (max 6).
    /// </summary>
    public static bool AddChatSite(WindowIslandSettings settings, string name, string url, string icon = "\uE8BD")
    {
        if (settings == null) return false;
        settings.ChatSites ??= WindowIslandSettings.DefaultChatSites();

        if (settings.ChatSites.Count >= MaxChatSites) return false;
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (!IsValidUrl(url, out var uri) || uri == null) return false;

        settings.ChatSites.Add(new ChatSite(name.Trim(), uri.ToString(), icon));
        return true;
    }

    /// <summary>
    /// Removes a chat site at the specified index, ensuring at least one site remains.
    /// </summary>
    public static bool RemoveChatSite(WindowIslandSettings settings, int index)
    {
        if (settings == null || settings.ChatSites == null) return false;
        if (settings.ChatSites.Count <= 1) return false; // keep at least 1 site
        if (index < 0 || index >= settings.ChatSites.Count) return false;

        settings.ChatSites.RemoveAt(index);
        if (settings.ActiveChatIndex >= settings.ChatSites.Count)
        {
            settings.ActiveChatIndex = settings.ChatSites.Count - 1;
        }
        return true;
    }

    /// <summary>
    /// Gets the currently active chat site, or falls back to the first available site.
    /// </summary>
    public static ChatSite GetActiveSite(WindowIslandSettings settings)
    {
        if (settings == null) return new ChatSite("Gemini", "https://gemini.google.com");
        settings.ChatSites ??= WindowIslandSettings.DefaultChatSites();

        if (settings.ChatSites.Count == 0)
        {
            settings.ChatSites = WindowIslandSettings.DefaultChatSites();
        }

        int idx = Math.Clamp(settings.ActiveChatIndex, 0, settings.ChatSites.Count - 1);
        settings.ActiveChatIndex = idx;
        return settings.ChatSites[idx];
    }

    /// <summary>
    /// Clamps idle minutes to valid range (1 - 60).
    /// </summary>
    public static int ClampIdleMinutes(int minutes)
    {
        if (minutes < MinIdleMinutes) return MinIdleMinutes;
        if (minutes > MaxIdleMinutes) return MaxIdleMinutes;
        return minutes;
    }
}
