using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AppLock.Models;

namespace AppLock.WindowIsland.Modules.Controls;

public static class QuickCardRunner
{
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    private const byte VK_LWIN = 0x5B;
    private const byte VK_TAB = 0x09;
    private const byte VK_D = 0x44;
    private const byte VK_SHIFT = 0x10;
    private const byte VK_S = 0x53;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public static async Task<(bool Success, string Error)> ExecuteCardAsync(QuickCard card, Action? onBeforeRun = null)
    {
        if (card == null) return (false, "Card is null.");

        onBeforeRun?.Invoke();

        return await Task.Run(async () =>
        {
            try
            {
                switch (card.Id)
                {
                    case "lock":
                        WindowIslandControlsService.LockComputer();
                        return (true, string.Empty);

                    case "desktop":
                        ToggleDesktop();
                        return (true, string.Empty);

                    case "taskview":
                        await Task.Delay(100);
                        OpenTaskView();
                        return (true, string.Empty);

                    case "calculator":
                        try { LaunchProcess("calc.exe", ""); }
                        catch { LaunchProcess("calculator:", ""); }
                        return (true, string.Empty);

                    case "screenshot":
                        try { LaunchProcess("ms-screenclip:", ""); }
                        catch
                        {
                            keybd_event(VK_LWIN, 0, 0, 0);
                            keybd_event(VK_SHIFT, 0, 0, 0);
                            keybd_event(VK_S, 0, 0, 0);
                            keybd_event(VK_S, 0, KEYEVENTF_KEYUP, 0);
                            keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, 0);
                            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, 0);
                        }
                        return (true, string.Empty);

                    case "terminal":
                        try { LaunchProcess("wt.exe", card.Arguments ?? ""); }
                        catch { LaunchProcess("cmd.exe", card.Arguments ?? ""); }
                        return (true, string.Empty);

                    default:
                        if (!string.IsNullOrWhiteSpace(card.Command))
                        {
                            LaunchProcess(card.Command, card.Arguments, card.RunAsAdmin);
                            return (true, string.Empty);
                        }
                        return (false, "Card has no command.");
                }
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        });
    }

    private static void ToggleDesktop()
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType != null)
            {
                dynamic? shell = Activator.CreateInstance(shellType);
                shell?.ToggleDesktop();
                return;
            }
        }
        catch { }

        // Fallback Win+D
        keybd_event(VK_LWIN, 0, 0, 0);
        keybd_event(VK_D, 0, 0, 0);
        keybd_event(VK_D, 0, KEYEVENTF_KEYUP, 0);
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, 0);
    }

    private static void OpenTaskView()
    {
        keybd_event(VK_LWIN, 0, 0, 0);
        keybd_event(VK_TAB, 0, 0, 0);
        keybd_event(VK_TAB, 0, KEYEVENTF_KEYUP, 0);
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, 0);
    }

    private static void LaunchProcess(string command, string? arguments, bool runAsAdmin = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments ?? string.Empty,
            UseShellExecute = true
        };
        if (runAsAdmin)
        {
            psi.Verb = "runas";
        }
        Process.Start(psi);
    }
}
