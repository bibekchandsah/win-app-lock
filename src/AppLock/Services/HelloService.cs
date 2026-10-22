using System;
using System.Threading.Tasks;
using Windows.Security.Credentials.UI;

namespace AppLock.Services;

/// <summary>
/// Service for Windows Hello biometric authentication
/// </summary>
public class HelloService
{
    /// <summary>
    /// Check if Windows Hello is available on this device
    /// </summary>
    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            return availability == UserConsentVerifierAvailability.Available;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error checking Windows Hello availability: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Request user verification using Windows Hello
    /// </summary>
    /// <param name="message">Message to display to the user</param>
    /// <returns>True if verification succeeded</returns>
    public async Task<bool> RequestVerificationAsync(string message = "Verify your identity")
    {
        try
        {
            var result = await UserConsentVerifier.RequestVerificationAsync(message);
            
            bool success = result == UserConsentVerificationResult.Verified;
            
            System.Diagnostics.Debug.WriteLine($"Windows Hello verification: {result}");
            
            return success;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error requesting Windows Hello verification: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Get a user-friendly description of the Windows Hello availability
    /// </summary>
    public async Task<string> GetAvailabilityDescriptionAsync()
    {
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            
            return availability switch
            {
                UserConsentVerifierAvailability.Available => "Available",
                UserConsentVerifierAvailability.DeviceBusy => "Device busy",
                UserConsentVerifierAvailability.NotConfiguredForUser => "Not configured for this user",
                UserConsentVerifierAvailability.DisabledByPolicy => "Disabled by policy",
                UserConsentVerifierAvailability.DeviceNotPresent => "No biometric device found",
                _ => "Not available"
            };
        }
        catch
        {
            return "Not available";
        }
    }
}
