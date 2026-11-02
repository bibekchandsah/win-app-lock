using System;
using System.Threading.Tasks;
using AppLock.Models;
using Microsoft.UI.Xaml;

namespace AppLock.Services.Firewall;

/// <summary>
/// Handles authentication requirements for firewall rule operations
/// </summary>
public class FirewallAuthService
{
    private DateTime? _lastAuthTime;
    private readonly TimeSpan _authCacheWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Check if authentication is required for unblock/remove operations
    /// </summary>
    public bool RequiresAuthForUnblock => ServiceContainer.Settings.RequirePasswordForFirewallUnblock;

    /// <summary>
    /// Invalidate the cached authentication
    /// </summary>
    public void InvalidateAuth()
    {
        _lastAuthTime = null;
    }

    /// <summary>
    /// Check if user is currently authenticated (within cache window)
    /// </summary>
    public bool IsAuthenticated()
    {
        if (!RequiresAuthForUnblock)
            return true;

        if (_lastAuthTime.HasValue && 
            DateTime.Now - _lastAuthTime.Value < _authCacheWindow)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Request authentication for unblock/remove operations
    /// Returns true if authentication succeeded or is not required
    /// </summary>
    public async Task<bool> AuthenticateForUnblockAsync(XamlRoot xamlRoot, string operation = "unblock")
    {
        if (!RequiresAuthForUnblock)
            return true;

        if (IsAuthenticated())
            return true;

        // Show auth dialog
        var dialog = new Views.AuthDialog(ServiceContainer.AuthService)
        {
            Title = "Authentication Required",
            XamlRoot = xamlRoot
        };

        var result = await dialog.ShowAsync();
        
        if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary && dialog.IsAuthenticated)
        {
            _lastAuthTime = DateTime.Now;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Prompt for authentication with a custom message
    /// </summary>
    public async Task<bool> AuthenticateAsync(XamlRoot xamlRoot, string title, string subtitle)
    {
        if (!RequiresAuthForUnblock)
            return true;

        var dialog = new Views.AuthDialog(ServiceContainer.AuthService)
        {
            Title = title,
            XamlRoot = xamlRoot
        };

        var result = await dialog.ShowAsync();
        
        if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary && dialog.IsAuthenticated)
        {
            _lastAuthTime = DateTime.Now;
            return true;
        }

        return false;
    }
}
