using AppLock.Views;
using Microsoft.UI.Xaml;

namespace AppLock.Services.BitLocker;

/// <summary>
/// Security gate for sensitive BitLocker operations:
/// requires AppLock admin password or Windows Hello verification.
/// Caches successful verification for at most 2 minutes per Section 6 rules.
/// </summary>
public static class BitLockerSecurityGate
{
    private static DateTime? _lastAuthenticatedTime;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    /// <summary>Checks whether an admin authentication is currently cached.</summary>
    public static bool IsCachedValid =>
        _lastAuthenticatedTime.HasValue && (DateTime.UtcNow - _lastAuthenticatedTime.Value) < CacheDuration;

    /// <summary>Clears any cached authentication (forces next sensitive operation to re-prompt).</summary>
    public static void InvalidateCache() => _lastAuthenticatedTime = null;

    /// <summary>
    /// Ensures the user has authenticated via Admin Password or Windows Hello.
    /// If cached within 2 minutes, returns true immediately. Otherwise shows AuthDialog.
    /// </summary>
    public static async Task<bool> EnsureAuthenticatedAsync(XamlRoot xamlRoot)
    {
        if (IsCachedValid)
        {
            return true;
        }

        try
        {
            var authDialog = new AuthDialog(ServiceContainer.AuthService)
            {
                XamlRoot = xamlRoot
            };

            await authDialog.ShowAsync();

            if (authDialog.IsAuthenticated)
            {
                _lastAuthenticatedTime = DateTime.UtcNow;
                return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BitLockerSecurityGate] Auth error: {ex.Message}");
        }

        return false;
    }
}

