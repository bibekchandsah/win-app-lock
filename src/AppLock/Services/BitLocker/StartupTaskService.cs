using System.Diagnostics;
using AppLock.Models;

namespace AppLock.Services.BitLocker;

/// <summary>
/// Manages the Windows Scheduled Task (AppLock_BitLocker_AutoUnlock)
/// that runs headless with Highest privileges at user logon to auto-unlock saved BitLocker volumes.
/// </summary>
public sealed class StartupTaskService
{
    public const string TaskName = "AppLock_BitLocker_AutoUnlock";

    public static string CurrentExePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AppLock.exe");

    /// <summary>Queries the scheduled task via schtasks.exe and parses its status.</summary>
    public async Task<StartupTaskInfo> GetStatusAsync(CancellationToken ct = default)
    {
        var info = new StartupTaskInfo();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/query /tn \"{TaskName}\" /fo LIST /v",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return info;

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                info.Exists = false;
                info.State = "Not created";
                return info;
            }

            info.Exists = true;
            info.State = "Ready";

            var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var key = line.Substring(0, colon).Trim();
                var val = line.Substring(colon + 1).Trim();

                if (string.Equals(key, "Status", StringComparison.OrdinalIgnoreCase))
                {
                    info.State = val;
                }
                else if (string.Equals(key, "Last Run Time", StringComparison.OrdinalIgnoreCase))
                {
                    info.LastRunTime = val;
                }
                else if (string.Equals(key, "Last Result", StringComparison.OrdinalIgnoreCase))
                {
                    info.LastResult = val;
                }
                else if (string.Equals(key, "Task To Run", StringComparison.OrdinalIgnoreCase))
                {
                    info.RegisteredExePath = val;
                }
            }

            var currentExe = CurrentExePath;
            if (!string.IsNullOrEmpty(info.RegisteredExePath))
            {
                info.ExePathMatches = info.RegisteredExePath.Contains(currentExe, StringComparison.OrdinalIgnoreCase);
            }

            return info;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupTaskService] Error querying task: {ex.Message}");
            return info;
        }
    }

    /// <summary>Creates or updates the scheduled task with Highest privileges at ONLOGON and allows running on battery.</summary>
    public async Task<bool> CreateOrUpdateTaskAsync(CancellationToken ct = default)
    {
        string? tempXmlPath = null;
        try
        {
            var exe = CurrentExePath;
            var xmlContent = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>AppLock BitLocker Auto-Unlock</Description>
    <URI>\{TaskName}</URI>
  </RegistrationInfo>
  <Principals>
    <Principal id=""Author"">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <StartWhenAvailable>true</StartWhenAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
  </Settings>
  <Triggers>
    <LogonTrigger />
  </Triggers>
  <Actions Context=""Author"">
    <Exec>
      <Command>{exe}</Command>
      <Arguments>--bitlocker-autounlock</Arguments>
    </Exec>
  </Actions>
</Task>";

            tempXmlPath = Path.Combine(Path.GetTempPath(), $"AppLock_Task_{Guid.NewGuid():N}.xml");
            await File.WriteAllTextAsync(tempXmlPath, xmlContent, System.Text.Encoding.Unicode, ct);

            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/create /tn \"{TaskName}\" /xml \"{tempXmlPath}\" /f",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync(ct);
                if (process.ExitCode == 0) return true;
            }

            // Fallback to command-line creation if XML import fails
            var fallbackCommand = $"\"\"{exe}\" --bitlocker-autounlock\"";
            var fallbackPsi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/create /tn \"{TaskName}\" /tr {fallbackCommand} /sc ONLOGON /rl HIGHEST /f",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var fallbackProc = Process.Start(fallbackPsi);
            if (fallbackProc == null) return false;
            await fallbackProc.WaitForExitAsync(ct);
            return fallbackProc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupTaskService] Error creating task: {ex.Message}");
            return false;
        }
        finally
        {
            if (tempXmlPath != null && File.Exists(tempXmlPath))
            {
                try { File.Delete(tempXmlPath); } catch { }
            }
        }
    }

    /// <summary>Deletes the scheduled task.</summary>
    public async Task<bool> DeleteTaskAsync(CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/delete /tn \"{TaskName}\" /f",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupTaskService] Error deleting task: {ex.Message}");
            return false;
        }
    }

    /// <summary>Runs the scheduled task immediately.</summary>
    public async Task<bool> RunTaskNowAsync(CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/run /tn \"{TaskName}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupTaskService] Error running task: {ex.Message}");
            return false;
        }
    }
}

