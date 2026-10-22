using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Security.Credentials.UI;
using Windows.Win32;
using Windows.Win32.Foundation;

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
    /// <param name="cancellationToken">Optional token to cancel the verification</param>
    /// <param name="callerHwnd">Optional HWND of calling window to keep enabled</param>
    /// <returns>True if verification succeeded</returns>
    public async Task<bool> RequestVerificationAsync(
        string message = "Verify your identity",
        CancellationToken cancellationToken = default,
        HWND callerHwnd = default)
    {
        using var localCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var keepEnabledTask = Task.Run(() => KeepAppWindowsEnabledAsync(callerHwnd, localCts.Token));

        try
        {
            // Immediately ensure caller window and process windows are enabled
            EnsureWindowsEnabled(callerHwnd);

            var op = UserConsentVerifier.RequestVerificationAsync(message);
            var opTask = op.AsTask();

            if (cancellationToken.CanBeCanceled)
            {
                var tcs = new TaskCompletionSource<bool>();
                using (cancellationToken.Register(() =>
                {
                    try { op.Cancel(); } catch { }
                    DismissHelloPrompt();
                    tcs.TrySetResult(false);
                }))
                {
                    var completed = await Task.WhenAny(opTask, tcs.Task);
                    if (completed == tcs.Task)
                    {
                        System.Diagnostics.Debug.WriteLine("Windows Hello verification cancelled by user via app.");
                        return false;
                    }
                }
            }

            var result = await opTask;
            bool success = result == UserConsentVerificationResult.Verified;
            System.Diagnostics.Debug.WriteLine($"Windows Hello verification: {result}");
            return success;
        }
        catch (OperationCanceledException)
        {
            DismissHelloPrompt();
            System.Diagnostics.Debug.WriteLine("Windows Hello verification cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error requesting Windows Hello verification: {ex.Message}");
            return false;
        }
        finally
        {
            localCts.Cancel();
            try { await keepEnabledTask; } catch { }
            // Ensure window remains enabled after prompt concludes
            EnsureWindowsEnabled(callerHwnd);
        }
    }

    private static void EnsureWindowsEnabled(HWND specificHwnd)
    {
        try
        {
            if (specificHwnd != default && !PInvoke.IsWindowEnabled(specificHwnd))
            {
                PInvoke.EnableWindow(specificHwnd, true);
            }

            uint currentPid = PInvoke.GetCurrentProcessId();
            unsafe
            {
                PInvoke.EnumWindows((h, lParam) =>
                {
                    uint pid;
                    PInvoke.GetWindowThreadProcessId(h, &pid);
                    if (pid == currentPid)
                    {
                        if (!PInvoke.IsWindowEnabled(h))
                        {
                            PInvoke.EnableWindow(h, true);
                        }
                    }
                    return true;
                }, 0);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"EnsureWindowsEnabled error: {ex.Message}");
        }
    }

    private static async Task KeepAppWindowsEnabledAsync(HWND specificHwnd, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                EnsureWindowsEnabled(specificHwnd);
                await Task.Delay(25, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"KeepAppWindowsEnabled error: {ex.Message}");
        }
    }

    /// <summary>
    /// Finds and sends WM_CLOSE and WM_SYSCOMMAND/SC_CLOSE to the Windows Hello / Credential UI dialog window to dismiss it immediately.
    /// </summary>
    public static void DismissHelloPrompt()
    {
        DismissHelloPromptInternal();

        // Also schedule a quick delayed pass in case the prompt was in the middle of appearing
        Task.Run(async () =>
        {
            await Task.Delay(100);
            DismissHelloPromptInternal();
        });
    }

    private static void DismissHelloPromptInternal()
    {
        try
        {
            unsafe
            {
                PInvoke.EnumWindows((hwnd, lParam) =>
                {
                    const int bufferSize = 256;
                    var classBuffer = stackalloc char[bufferSize];
                    int classLen = PInvoke.GetClassName(hwnd, new PWSTR(classBuffer), bufferSize);
                    string className = classLen > 0 ? new string(classBuffer, 0, classLen) : string.Empty;

                    var titleBuffer = stackalloc char[bufferSize];
                    int titleLen = PInvoke.GetWindowText(hwnd, new PWSTR(titleBuffer), bufferSize);
                    string title = titleLen > 0 ? new string(titleBuffer, 0, titleLen) : string.Empty;

                    bool isTarget = false;
                    if (!string.IsNullOrEmpty(className))
                    {
                        if (className.Contains("Credential Dialog", StringComparison.OrdinalIgnoreCase) ||
                            className.Contains("CredentialDialog", StringComparison.OrdinalIgnoreCase) ||
                            className.Contains("CredentialUIBroker", StringComparison.OrdinalIgnoreCase))
                        {
                            isTarget = true;
                        }
                    }

                    if (!isTarget && !string.IsNullOrEmpty(title))
                    {
                        if (title.Equals("Windows Security", StringComparison.OrdinalIgnoreCase) ||
                            title.Equals("Windows Hello", StringComparison.OrdinalIgnoreCase))
                        {
                            isTarget = true;
                        }
                    }

                    if (isTarget)
                    {
                        const uint WM_CLOSE = 0x0010;
                        const uint WM_SYSCOMMAND = 0x0112;
                        const nuint SC_CLOSE = 0xF060;

                        PInvoke.PostMessage(hwnd, WM_CLOSE, default, default);
                        PInvoke.PostMessage(hwnd, WM_SYSCOMMAND, (WPARAM)SC_CLOSE, default);
                    }

                    return true; // continue enumerating to close all matching prompt windows
                }, 0);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DismissHelloPrompt exception: {ex.Message}");
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
