using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.PcChecker;
using Microsoft.Win32;

namespace AppLock.Services.PcChecker.Providers;

/// <summary>
/// Software: installed apps (registry, never Win32_Product), startup items, services, developer runtimes,
/// the PATH checker, Windows update history and a short process list. Everything is read-only.
/// </summary>
public sealed class SoftwareProvider : IInfoProvider
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public string Id => "software";
    public string Category => "software";
    public InfoTier Tier => InfoTier.Standard;
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public TimeSpan CacheLifetime => TimeSpan.FromMinutes(5);

    public bool IsSupported(SystemContext ctx) => true;

    public Task<InfoSection> CollectAsync(CollectOptions options, IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Collect(progress, ct), ct);

    private sealed record AppEntry(string Name, string? Version, string? Publisher, string? InstallDate,
        ulong SizeBytes, string? Location, bool Machine);

    private InfoSection Collect(IProgress<double> progress, CancellationToken ct)
    {
        var section = new InfoSection { Id = Id, Title = "Software", Category = Category };

        var apps = ReadInstalledApps();
        var startup = ReadStartupItems();
        var services = Wmi.Query("SELECT Name, DisplayName, State, StartMode, PathName, StartName FROM Win32_Service");
        var updates = Wmi.Query("SELECT HotFixID, Description, InstalledOn FROM Win32_QuickFixEngineering");
        var processes = Process.GetProcesses();

        var summary = section.Group("Summary");
        summary.Items.Add(InfoItem.Of("software.apps", "Installed apps", apps.Count.ToString(), DetailLevel.Basic,
            "Apps with an entry in Windows' installed-apps list. Store apps and portable apps may not count here.",
            "Source: registry Uninstall keys"));
        summary.Items.Add(InfoItem.Of("software.startup", "Startup items", startup.Count.ToString(), DetailLevel.Basic,
            "Programs that start with Windows. Many startup items slow boot time.", "Source: registry Run keys and Startup folders"));
        summary.Items.Add(InfoItem.Of("software.services", "Services", services.Count.ToString(), DetailLevel.Basic,
            "Background services Windows knows about.", "Source: WMI Win32_Service"));
        summary.Items.Add(InfoItem.Of("software.processes", "Running processes", processes.Length.ToString(), DetailLevel.Basic,
            "How many processes are running right now.", "Source: Process.GetProcesses"));

        foreach (var process in processes) process.Dispose();
        ct.ThrowIfCancellationRequested();
        progress?.Report(0.3);

        // ── Installed apps ──────────────────────────────────────────────────────
        var appGroup = section.Group("Installed apps");
        foreach (var app in apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).Take(120))
        {
            var facts = new List<string>();
            if (!string.IsNullOrWhiteSpace(app.Version)) facts.Add(app.Version!);
            if (!string.IsNullOrWhiteSpace(app.Publisher)) facts.Add(app.Publisher!);
            if (app.SizeBytes > 0) facts.Add(PcFormat.Size(app.SizeBytes));

            appGroup.Items.Add(InfoItem.Of($"software.app.{app.Name}", app.Name, string.Join(" · ", facts), DetailLevel.Basic,
                "An app Windows lists in Settings → Apps.", app.Machine ? "Source: registry HKLM Uninstall" : "Source: registry HKCU Uninstall"));
        }

        if (apps.Count > 120)
        {
            appGroup.Items.Add(InfoItem.Of("software.app.more", "Installed apps",
                $"{apps.Count - 120} more…", DetailLevel.Everything,
                "The list is capped so the page stays fast.", "Source: registry Uninstall keys"));
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.5);

        // ── Startup items ───────────────────────────────────────────────────────
        var startupGroup = section.Group("Startup items");
        if (startup.Count == 0)
        {
            startupGroup.Items.Add(InfoItem.Of("software.startup.none", "Startup items", "None", DetailLevel.Basic,
                "Nothing starts automatically with Windows.", "Source: registry Run keys"));
        }

        foreach (var item in startup.OrderBy(s => s.Source).ThenBy(s => s.Name))
        {
            var startupItem = InfoItem.Of($"software.startup.{item.Source}.{item.Name}", item.Name,
                $"{item.Command} · {(item.Enabled ? "enabled" : "disabled")}", DetailLevel.Detailed,
                "A program that starts with Windows. Disable it in Task Manager → Startup apps.",
                item.Source);
            if (!item.Enabled) startupItem.Status = InfoStatus.Info;
            startupGroup.Items.Add(startupItem);
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.65);

        // ── Services worth a look ───────────────────────────────────────────────
        var serviceGroup = section.Group("Services with unusual paths");
        var suspicious = services
            .Where(s => s.Str("PathName") is { Length: > 0 } path && !IsStandardPath(path))
            .OrderBy(s => s.Str("DisplayName"))
            .ToList();

        if (suspicious.Count == 0)
        {
            serviceGroup.Items.Add(InfoItem.Of("software.services.ok", "Services", "All service binaries are in Windows or Program Files",
                DetailLevel.Detailed, "Services running from elsewhere are worth a second look.", "Source: WMI Win32_Service",
                InfoStatus.Ok));
        }

        foreach (var service in suspicious.Take(25))
        {
            var name = service.Str("DisplayName") ?? service.Str("Name") ?? "Service";
            var state = service.Str("State");
            serviceGroup.Items.Add(InfoItem.Of($"software.service.{service.Str("Name")}", name,
                $"{state} · {service.Str("StartMode")} · {service.Str("PathName")}", DetailLevel.Detailed,
                "A service whose program lives outside the usual Windows and Program Files folders.",
                "Source: WMI Win32_Service", InfoStatus.Info));
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.75);

        // ── Runtimes and developer tools ────────────────────────────────────────
        var runtimeGroup = section.Group("Runtimes and developer tools");
        AddDotNetRuntimes(runtimeGroup);
        AddPathTool(runtimeGroup, "Python", "python.exe");
        AddPathTool(runtimeGroup, "Node.js", "node.exe");
        AddPathTool(runtimeGroup, "Java", "java.exe");
        AddPathTool(runtimeGroup, "Git", "git.exe");
        AddPathTool(runtimeGroup, "winget", "winget.exe");

        var vcRedists = apps.Count(a => a.Name.Contains("Visual C++", StringComparison.OrdinalIgnoreCase));
        runtimeGroup.Items.Add(InfoItem.Of("software.vcredist", "Visual C++ redistributables",
            vcRedists == 0 ? "None found" : $"{vcRedists} installed", DetailLevel.Detailed,
            "Runtime libraries many games and apps need.", "Source: registry Uninstall keys"));

        ct.ThrowIfCancellationRequested();
        progress?.Report(0.85);

        // ── PATH checker ────────────────────────────────────────────────────────
        var pathGroup = section.Group("PATH checker");
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var entries = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim())
            .Where(e => e.Length > 0)
            .ToList();

        var missing = entries.Where(e => !SafeDirectoryExists(e)).ToList();
        var duplicates = entries.GroupBy(e => e, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        var veryLong = entries.Where(e => e.Length > 200).ToList();

        pathGroup.Items.Add(InfoItem.Of("software.path.entries", "PATH entries", entries.Count.ToString(), DetailLevel.Basic,
            "Folders Windows searches when you type a program name.", "Source: PATH environment variable"));

        var missingItem = InfoItem.Of("software.path.missing", "Missing folders",
            missing.Count == 0 ? "None" : $"{missing.Count}: {string.Join("; ", missing.Take(5))}", DetailLevel.Basic,
            "PATH entries that point at folders that no longer exist. They slow down command lookups.",
            "Source: PATH environment variable");
        if (missing.Count > 0) missingItem.Status = InfoStatus.Info;
        pathGroup.Items.Add(missingItem);

        if (duplicates.Count > 0)
        {
            pathGroup.Items.Add(InfoItem.Of("software.path.duplicates", "Duplicate folders",
                $"{duplicates.Count}: {string.Join("; ", duplicates.Take(5))}", DetailLevel.Detailed,
                "The same folder listed more than once.", "Source: PATH environment variable", InfoStatus.Info));
        }

        if (veryLong.Count > 0)
        {
            pathGroup.Items.Add(InfoItem.Of("software.path.long", "Very long entries",
                $"{veryLong.Count} entries longer than 200 characters", DetailLevel.Detailed,
                "A very long PATH can break older installers.", "Source: PATH environment variable", InfoStatus.Info));
        }

        // ── Windows updates ─────────────────────────────────────────────────────
        var updateGroup = section.Group("Windows updates");
        var dated = updates
            .Select(u => new { Id = u.Str("HotFixID"), Description = u.Str("Description"), Date = u.Date("InstalledOn") })
            .OrderByDescending(u => u.Date)
            .Take(12)
            .ToList();

        if (dated.Count == 0)
        {
            updateGroup.Items.Add(InfoItem.MissingValue("software.updates.none", "Update history", MissingReason.NotReported,
                DetailLevel.Detailed, "Source: WMI Win32_QuickFixEngineering"));
        }

        foreach (var update in dated)
        {
            updateGroup.Items.Add(InfoItem.Of($"software.update.{update.Id}", update.Id ?? "Update",
                $"{update.Date?.ToString("d") ?? "date unknown"} · {update.Description}", DetailLevel.Detailed,
                "One installed Windows update.", "Source: WMI Win32_QuickFixEngineering"));
        }

        // ── Processes ───────────────────────────────────────────────────────────
        var processGroup = section.Group("Processes (largest by memory)");
        var topProcesses = Process.GetProcesses()
            .OrderByDescending(p =>
            {
                try { return p.WorkingSet64; } catch { return 0L; }
            })
            .Take(15)
            .ToList();

        foreach (var process in topProcesses)
        {
            try
            {
                processGroup.Items.Add(InfoItem.Of($"software.process.{process.Id}", process.ProcessName,
                    $"PID {process.Id} · {PcFormat.Size((ulong)Math.Max(0, process.WorkingSet64))} · {process.Threads.Count} threads",
                    DetailLevel.Detailed, "One running process, largest memory users first.", "Source: Process API"));
            }
            catch { }
            finally { process.Dispose(); }
        }

        progress?.Report(1);
        return section;
    }

    private static bool IsStandardPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path).Trim().Trim('"');
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        foreach (var root in new[] { windows, programFiles, programFilesX86 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            if (expanded.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool SafeDirectoryExists(string path)
    {
        try { return Directory.Exists(Environment.ExpandEnvironmentVariables(path)); }
        catch { return false; }
    }

    private static List<AppEntry> ReadInstalledApps()
    {
        var result = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(UninstallKey);
                    if (uninstall == null) continue;

                    foreach (var subKeyName in uninstall.GetSubKeyNames())
                    {
                        using var key = uninstall.OpenSubKey(subKeyName);
                        if (key == null) continue;

                        var name = key.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        if (key.GetValue("SystemComponent") is int system && system == 1) continue;
                        if (key.GetValue("ParentKeyName") is string parent && !string.IsNullOrWhiteSpace(parent)) continue;

                        ulong sizeKb = key.GetValue("EstimatedSize") is int kb && kb > 0 ? (ulong)kb : 0;

                        result[name!] = new AppEntry(
                            name!,
                            key.GetValue("DisplayVersion") as string,
                            key.GetValue("Publisher") as string,
                            key.GetValue("InstallDate") as string,
                            sizeKb * 1024,
                            key.GetValue("InstallLocation") as string,
                            hive == RegistryHive.LocalMachine);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PcChecker] Uninstall key read failed: {ex.Message}");
                }
            }
        }

        return result.Values.ToList();
    }

    private sealed record StartupItem(string Name, string Command, string Source, bool Enabled);

    private static List<StartupItem> ReadStartupItems()
    {
        var items = new List<StartupItem>();
        var approved = ReadStartupApproved();

        void ReadRunKey(RegistryHive hive, RegistryView view, string source)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var runKey = baseKey.OpenSubKey(RunKey);
                if (runKey == null) return;

                foreach (var valueName in runKey.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(valueName)) continue;
                    var command = runKey.GetValue(valueName)?.ToString() ?? string.Empty;
                    var enabled = !approved.TryGetValue(valueName, out var state) || state;
                    items.Add(new StartupItem(valueName, command, source, enabled));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PcChecker] Run key read failed: {ex.Message}");
            }
        }

        ReadRunKey(RegistryHive.CurrentUser, RegistryView.Default, "HKCU Run");
        ReadRunKey(RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM Run");
        ReadRunKey(RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM Run (32-bit)");

        // Startup folders
        try
        {
            var folders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
            };

            foreach (var folder in folders.Where(f => !string.IsNullOrEmpty(f) && Directory.Exists(f)))
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    items.Add(new StartupItem(name, file, folder.Contains("Common", StringComparison.OrdinalIgnoreCase)
                        ? "Startup folder (all users)" : "Startup folder", true));
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PcChecker] Startup folder read failed: {ex.Message}");
        }

        return items;
    }

    /// <summary>Enabled state from Windows' StartupApproved data (first byte even means enabled).</summary>
    private static Dictionary<string, bool> ReadStartupApproved()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedRun);
            if (key == null) return result;

            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is byte[] data && data.Length > 0)
                {
                    result[name] = (data[0] & 1) == 0;
                }
            }
        }
        catch { }

        return result;
    }

    private static void AddDotNetRuntimes(InfoGroup group)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
            var release = key?.GetValue("Release") as int?;
            var version = key?.GetValue("Version") as string;

            group.Items.Add(InfoItem.Of("software.dotnet", ".NET Framework",
                version == null ? null : $"{version} (release {release})", DetailLevel.Detailed,
                "The classic .NET Framework most Windows apps use.",
                "Source: registry NET Framework Setup\\NDP"));
        }
        catch { }

        try
        {
            var dotnetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared");
            if (Directory.Exists(dotnetRoot))
            {
                var runtimes = Directory.EnumerateDirectories(dotnetRoot)
                    .SelectMany(p => Directory.EnumerateDirectories(p).Select(v => $"{Path.GetFileName(p)} {Path.GetFileName(v)}"))
                    .ToList();

                group.Items.Add(InfoItem.Of("software.dotnetRuntime", ".NET runtimes",
                    runtimes.Count == 0 ? null : string.Join(", ", runtimes), DetailLevel.Detailed,
                    "Modern .NET runtimes installed on this PC.", "Source: Program Files\\dotnet\\shared"));
            }
        }
        catch { }
    }

    private static void AddPathTool(InfoGroup group, string label, string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string? found = null;

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(entry.Trim()), executable);
                if (File.Exists(candidate))
                {
                    found = candidate;
                    break;
                }
            }
            catch { }
        }

        if (found == null)
        {
            group.Items.Add(InfoItem.Of($"software.tool.{executable}", label, "Not on PATH", DetailLevel.Detailed,
                "Not found in any folder listed in PATH.", "Source: PATH environment variable"));
            return;
        }

        string? version = null;
        try { version = FileVersionInfo.GetVersionInfo(found).ProductVersion; } catch { }

        group.Items.Add(InfoItem.Of($"software.tool.{executable}", label,
            string.IsNullOrWhiteSpace(version) ? found : $"{version} · {found}", DetailLevel.Detailed,
            "Found on the PATH, with the version from the executable's own metadata.",
            "Source: PATH environment variable"));
    }
}
