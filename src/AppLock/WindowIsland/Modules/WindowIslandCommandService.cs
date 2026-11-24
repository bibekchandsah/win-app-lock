using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using AppLock.Models;

namespace AppLock.WindowIsland.Modules;

public enum CommandItemKind
{
    Calculator,
    SystemCommand,
    Application,
    FileOrFolder,
    LauncherShortcut,
    WebSearch
}

/// <summary>One result item displayed in the Command Bar.</summary>
public sealed class CommandBarItem
{
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string IconGlyph { get; set; } = "\uE721";
    public string AccentHex { get; set; } = "#60CDFF";
    public CommandItemKind Kind { get; set; }
    public string ActionData { get; set; } = string.Empty;
    public bool IsSelected { get; set; }
}

/// <summary>
/// Evaluates queries for the Command Bar: math calculations, built-in system commands,
/// indexed files & apps, launcher shortcuts, and web search fallback (island.md Section 8).
/// </summary>
public sealed class WindowIslandCommandService
{
    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("PowrProf.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    private readonly WindowIslandFileIndex _fileIndex;
    private readonly WindowIslandLauncher _launcher;
    private WindowIslandSettings _settings;
    private static readonly DataTable _mathTable = new();

    public event Action<CustomSearchCommand>? SearchCommandAdded;

    public WindowIslandCommandService(WindowIslandFileIndex fileIndex, WindowIslandLauncher launcher, WindowIslandSettings? settings = null)
    {
        _fileIndex = fileIndex;
        _launcher  = launcher;
        _settings  = settings ?? new WindowIslandSettings();
    }

    public void UpdateSettings(WindowIslandSettings settings)
    {
        _settings = settings ?? new WindowIslandSettings();
    }

    public List<CommandBarItem> Query(string rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery)) return new List<CommandBarItem>();
        string query = rawQuery.Trim();
        var results = new List<CommandBarItem>();

        // 0. Check @ Search Commands & Quick Add
        if (query.StartsWith("@"))
        {
            if (query.StartsWith("@add ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = query[5..].Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    string tag = CustomSearchCommand.NormalizeCommand(parts[0]);
                    string url = parts[1].Trim();
                    results.Add(new CommandBarItem
                    {
                        Title = $"Add Search Command: {tag}",
                        Subtitle = $"Save query URL: {url} (Enter to save)",
                        IconGlyph = "\uE710",
                        AccentHex = "#60CDFF",
                        Kind = CommandItemKind.SystemCommand,
                        ActionData = $"addsearch:{tag}|{url}"
                    });
                    if (results.Count > 0) results[0].IsSelected = true;
                    return results;
                }
            }

            int spaceIndex = query.IndexOf(' ');
            string cmdTag = (spaceIndex > 0 ? query[..spaceIndex] : query).Trim();
            string searchTerm = (spaceIndex > 0 ? query[(spaceIndex + 1)..].Trim() : string.Empty);

            var searches = _settings.GetCustomSearches();
            var exactMatch = searches.FirstOrDefault(s => MatchesCommand(s, cmdTag));

            if (exactMatch != null)
            {
                if (!string.IsNullOrEmpty(searchTerm))
                {
                    string targetUrl = exactMatch.BuildUrl(searchTerm);
                    results.Add(new CommandBarItem
                    {
                        Title = $"Search {exactMatch.Name}: \"{searchTerm}\"",
                        Subtitle = $"{targetUrl} (Enter to search)",
                        IconGlyph = exactMatch.IconGlyph,
                        AccentHex = exactMatch.AccentHex,
                        Kind = CommandItemKind.WebSearch,
                        ActionData = exactMatch.IsIncognito ? $"incognito:{targetUrl}" : targetUrl
                    });

                    if (!exactMatch.Command.Equals("@google", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new CommandBarItem
                        {
                            Title = $"Search Google: \"{searchTerm}\"",
                            Subtitle = "Search with default browser on Google",
                            IconGlyph = "\uE721",
                            AccentHex = "#4285F4",
                            Kind = CommandItemKind.WebSearch,
                            ActionData = $"https://www.google.com/search?q={Uri.EscapeDataString(searchTerm)}"
                        });
                    }

                    if (exactMatch.Command.Equals("@weather", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new CommandBarItem
                        {
                            Title = "Open Island Weather Tab",
                            Subtitle = "View local weather forecast in Island",
                            IconGlyph = "\uE9CA",
                            AccentHex = "#FFB900",
                            Kind = CommandItemKind.SystemCommand,
                            ActionData = "tab:weather"
                        });
                    }
                }
                else
                {
                    results.Add(new CommandBarItem
                    {
                        Title = $"{exactMatch.Command}  ·  Search {exactMatch.Name}",
                        Subtitle = $"Type query (e.g. \"{exactMatch.Command} something\") or Enter to open {exactMatch.BaseUrl}",
                        IconGlyph = exactMatch.IconGlyph,
                        AccentHex = exactMatch.AccentHex,
                        Kind = CommandItemKind.WebSearch,
                        ActionData = exactMatch.IsIncognito ? $"incognito:{exactMatch.BaseUrl}" : exactMatch.BaseUrl
                    });
                }

                if (results.Count > 0) results[0].IsSelected = true;
                return results;
            }

            // No exact match — list all matching search commands
            string filter = cmdTag.TrimStart('@');
            var matchingEngines = searches.Where(s =>
                string.IsNullOrEmpty(filter) ||
                s.Command.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var eng in matchingEngines)
            {
                if (!string.IsNullOrEmpty(searchTerm))
                {
                    string u = eng.BuildUrl(searchTerm);
                    results.Add(new CommandBarItem
                    {
                        Title = $"Search {eng.Name}: \"{searchTerm}\"",
                        Subtitle = $"{eng.Command}  ·  {u}",
                        IconGlyph = eng.IconGlyph,
                        AccentHex = eng.AccentHex,
                        Kind = CommandItemKind.WebSearch,
                        ActionData = eng.IsIncognito ? $"incognito:{u}" : u
                    });
                }
                else
                {
                    results.Add(new CommandBarItem
                    {
                        Title = $"{eng.Command}  ·  Search {eng.Name}",
                        Subtitle = $"Type \"{eng.Command} <query>\" to search {eng.Name} ({eng.BaseUrl})",
                        IconGlyph = eng.IconGlyph,
                        AccentHex = eng.AccentHex,
                        Kind = CommandItemKind.WebSearch,
                        ActionData = eng.IsIncognito ? $"incognito:{eng.BaseUrl}" : eng.BaseUrl
                    });
                }
            }

            if (results.Count > 0) results[0].IsSelected = true;
            return results;
        }

        // 1. Check Calculator
        if (TryEvaluateMath(query, out string calcResult))
        {
            results.Add(new CommandBarItem
            {
                Title = $"= {calcResult}",
                Subtitle = $"Math calculation for \"{query}\" (Enter to copy)",
                IconGlyph = "\uE1D2",
                AccentHex = "#60CDFF",
                Kind = CommandItemKind.Calculator,
                ActionData = calcResult
            });
        }

        // 2. Check Built-in Commands
        string qLower = query.ToLowerInvariant();
        if (qLower == "lock" || qLower.StartsWith("lock "))
        {
            results.Add(new CommandBarItem
            {
                Title = "Lock Workstation",
                Subtitle = "Lock current Windows session immediately",
                IconGlyph = "\uE72E",
                AccentHex = "#FFB900",
                Kind = CommandItemKind.SystemCommand,
                ActionData = "lock"
            });
        }
        else if (qLower == "sleep")
        {
            results.Add(new CommandBarItem
            {
                Title = "Sleep PC",
                Subtitle = "Put computer to sleep",
                IconGlyph = "\uE708",
                AccentHex = "#FFB900",
                Kind = CommandItemKind.SystemCommand,
                ActionData = "sleep"
            });
        }
        else if (qLower == "restart")
        {
            results.Add(new CommandBarItem
            {
                Title = "Restart PC",
                Subtitle = "Reboot computer",
                IconGlyph = "\uE777",
                AccentHex = "#FF4343",
                Kind = CommandItemKind.SystemCommand,
                ActionData = "restart"
            });
        }
        else if (qLower == "shutdown" || qLower == "shut down")
        {
            results.Add(new CommandBarItem
            {
                Title = "Shut Down PC",
                Subtitle = "Power off computer",
                IconGlyph = "\uE7E8",
                AccentHex = "#FF4343",
                Kind = CommandItemKind.SystemCommand,
                ActionData = "shutdown"
            });
        }
        else if (qLower.StartsWith("note ") || qLower == "note")
        {
            string noteText = query.Length > 5 ? query[5..].Trim() : string.Empty;
            results.Add(new CommandBarItem
            {
                Title = string.IsNullOrEmpty(noteText) ? "New Note" : $"Note: {noteText}",
                Subtitle = "Save note in Island Note tab",
                IconGlyph = "\uE70B",
                AccentHex = "#FFD700",
                Kind = CommandItemKind.SystemCommand,
                ActionData = $"note:{noteText}"
            });
        }
        else if (qLower.StartsWith("remind ") || qLower == "remind")
        {
            string remindText = query.Length > 7 ? query[7..].Trim() : string.Empty;
            results.Add(new CommandBarItem
            {
                Title = string.IsNullOrEmpty(remindText) ? "New Reminder" : $"Remind: {remindText}",
                Subtitle = "Set a reminder",
                IconGlyph = "\uEA8F",
                AccentHex = "#FF8C00",
                Kind = CommandItemKind.SystemCommand,
                ActionData = $"remind:{remindText}"
            });
        }
        else if (qLower.StartsWith("timer ") || qLower == "timer")
        {
            string timerText = query.Length > 6 ? query[6..].Trim() : string.Empty;
            results.Add(new CommandBarItem
            {
                Title = string.IsNullOrEmpty(timerText) ? "Start Timer" : $"Timer: {timerText}",
                Subtitle = "Start a countdown timer",
                IconGlyph = "\uE916",
                AccentHex = "#60CDFF",
                Kind = CommandItemKind.SystemCommand,
                ActionData = $"timer:{timerText}"
            });
        }
        else if (qLower == "weather")
        {
            results.Add(new CommandBarItem
            {
                Title = "Weather",
                Subtitle = "Open Island Weather tab",
                IconGlyph = "\uE9CA",
                AccentHex = "#60CDFF",
                Kind = CommandItemKind.SystemCommand,
                ActionData = "tab:weather"
            });
        }
        else if (qLower == "clipboard")
        {
            results.Add(new CommandBarItem
            {
                Title = "Clipboard History",
                Subtitle = "Open Island Clipboard tab",
                IconGlyph = "\uF0E3",
                AccentHex = "#60CDFF",
                Kind = CommandItemKind.SystemCommand,
                ActionData = "tab:clipboard"
            });
        }
        else if (qLower == "shelf")
        {
            results.Add(new CommandBarItem
            {
                Title = "Shelf",
                Subtitle = "Open Island Shelf tab",
                IconGlyph = "\uE8F1",
                AccentHex = "#60CDFF",
                Kind = CommandItemKind.SystemCommand,
                ActionData = "tab:shelf"
            });
        }
        else if (qLower == "settings")
        {
            results.Add(new CommandBarItem
            {
                Title = "Island Settings",
                Subtitle = "Open Island configuration",
                IconGlyph = "\uE713",
                AccentHex = "#60CDFF",
                Kind = CommandItemKind.SystemCommand,
                ActionData = "settings"
            });
        }

        // 3. Check Launcher shortcuts
        foreach (var sc in _launcher.Items)
        {
            if (sc.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                sc.Target.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new CommandBarItem
                {
                    Title = sc.Name,
                    Subtitle = $"Shortcut: {sc.Target}",
                    IconGlyph = "\uE714",
                    AccentHex = "#60CDFF",
                    Kind = CommandItemKind.LauncherShortcut,
                    ActionData = sc.Target
                });
            }
        }

        // 4. Check File & App Index
        var fileResults = _fileIndex.Search(query, maxResults: 8);
        foreach (var f in fileResults)
        {
            results.Add(new CommandBarItem
            {
                Title = f.Name,
                Subtitle = f.Path,
                IconGlyph = f.IsApp ? "\uE71D" : (f.IsDirectory ? "\uE8B7" : "\uE756"),
                AccentHex = f.IsApp ? "#60CDFF" : (f.IsDirectory ? "#FFD700" : "#C0FFFFFF"),
                Kind = f.IsApp ? CommandItemKind.Application : CommandItemKind.FileOrFolder,
                ActionData = f.Path
            });
        }

        // 5. Fallback: Search across web engines
        results.Add(new CommandBarItem
        {
            Title = $"Search Google: \"{query}\"",
            Subtitle = "Search with default browser on Google",
            IconGlyph = "\uE774",
            AccentHex = "#4285F4",
            Kind = CommandItemKind.WebSearch,
            ActionData = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}"
        });

        results.Add(new CommandBarItem
        {
            Title = $"Search YouTube: \"{query}\"",
            Subtitle = "Search videos on YouTube (@youtube)",
            IconGlyph = "\uE714",
            AccentHex = "#FF0000",
            Kind = CommandItemKind.WebSearch,
            ActionData = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(query)}"
        });

        results.Add(new CommandBarItem
        {
            Title = "More searches with @command (e.g. @youtube, @google, @github…)",
            Subtitle = "Type @ in command bar to search 25+ services or add custom URLs",
            IconGlyph = "\uE721",
            AccentHex = "#60CDFF",
            Kind = CommandItemKind.SystemCommand,
            ActionData = "settings"
        });

        if (results.Count > 0)
        {
            results[0].IsSelected = true;
        }

        return results;
    }

    public async Task ExecuteAsync(CommandBarItem item, Action<int>? switchTabAction = null, Action? openSettingsAction = null)
    {
        try
        {
            switch (item.Kind)
            {
                case CommandItemKind.Calculator:
                    SetClipboard(item.ActionData);
                    break;

                case CommandItemKind.SystemCommand:
                    HandleSystemCommand(item.ActionData, switchTabAction, openSettingsAction);
                    break;

                case CommandItemKind.Application:
                case CommandItemKind.FileOrFolder:
                case CommandItemKind.LauncherShortcut:
                    Process.Start(new ProcessStartInfo(item.ActionData) { UseShellExecute = true });
                    break;

                case CommandItemKind.WebSearch:
                    ExecuteWebSearch(item.ActionData);
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowIslandCommandService] Execute error: {ex.Message}");
        }
        await Task.CompletedTask;
    }

    public void ShowInFolder(CommandBarItem item)
    {
        try
        {
            string path = item.ActionData;
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
        }
        catch { }
    }

    public void CopyPathOrData(CommandBarItem item)
    {
        SetClipboard(item.ActionData);
    }

    private void HandleSystemCommand(string cmd, Action<int>? switchTabAction, Action? openSettingsAction)
    {
        if (cmd == "lock")
        {
            LockWorkStation();
        }
        else if (cmd == "sleep")
        {
            SetSuspendState(false, false, false);
        }
        else if (cmd == "restart")
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = true });
        }
        else if (cmd == "shutdown")
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { UseShellExecute = true });
        }
        else if (cmd == "settings" || cmd == "command_help")
        {
            openSettingsAction?.Invoke();
        }
        else if (cmd.StartsWith("addsearch:"))
        {
            var payload = cmd[10..];
            var parts = payload.Split('|');
            if (parts.Length >= 2)
            {
                string tag = CustomSearchCommand.NormalizeCommand(parts[0]);
                string url = parts[1].Trim();
                string name = tag.TrimStart('@');
                if (name.Length > 0) name = char.ToUpper(name[0]) + name[1..];
                var search = new CustomSearchCommand(tag, name, url);
                _settings.GetCustomSearches().Add(search);
                SearchCommandAdded?.Invoke(search);
            }
        }
        else if (cmd.StartsWith("tab:"))
        {
            string tab = cmd[4..];
            int idx = tab switch
            {
                "clipboard" => 1,
                "shelf"     => 2,
                "text"      => 3,
                "chat"      => 4,
                "note"      => 5,
                "weather"   => 6,
                "controls"  => 7,
                _           => 0
            };
            switchTabAction?.Invoke(idx);
        }
    }

    public static void ExecuteWebSearch(string actionData)
    {
        if (string.IsNullOrWhiteSpace(actionData)) return;

        if (actionData.StartsWith("incognito:", StringComparison.OrdinalIgnoreCase))
        {
            string url = actionData[10..];
            LaunchIncognitoBrowser(url);
        }
        else
        {
            try
            {
                Process.Start(new ProcessStartInfo(actionData) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowIslandCommandService] Failed to launch web search: {ex.Message}");
            }
        }
    }

    public static void LaunchIncognitoBrowser(string url)
    {
        string[] browsers = ["msedge.exe", "chrome.exe", "brave.exe", "firefox.exe"];
        foreach (var b in browsers)
        {
            try
            {
                string flag = b.Contains("firefox") ? "-private-window" : (b.Contains("msedge") ? "-inprivate" : "-incognito");
                var psi = new ProcessStartInfo(b, $"{flag} \"{url}\"") { UseShellExecute = true };
                var p = Process.Start(psi);
                if (p != null) return;
            }
            catch { }
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private static bool MatchesCommand(CustomSearchCommand c, string cmd)
    {
        string norm = CustomSearchCommand.NormalizeCommand(cmd);
        return string.Equals(c.Command, norm, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Command.TrimStart('@'), norm.TrimStart('@'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryEvaluateMath(string expr, out string result)
    {
        result = string.Empty;
        if (string.IsNullOrWhiteSpace(expr)) return false;

        // Expression must contain at least one math operator or digit
        if (!Regex.IsMatch(expr, @"[0-9]") || !Regex.IsMatch(expr, @"[\+\-\*\/\%]|sqrt|abs", RegexOptions.IgnoreCase))
            return false;

        try
        {
            string clean = expr.Replace("x", "*").Replace("X", "*");
            // Handle square root e.g. sqrt(144)
            var sqrtMatch = Regex.Match(clean, @"sqrt\(([^)]+)\)", RegexOptions.IgnoreCase);
            if (sqrtMatch.Success && double.TryParse(sqrtMatch.Groups[1].Value, out double val))
            {
                clean = clean.Replace(sqrtMatch.Value, Math.Sqrt(val).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            object computed = _mathTable.Compute(clean, null);
            if (computed != null && double.TryParse(computed.ToString(), out double num) && !double.IsInfinity(num) && !double.IsNaN(num))
            {
                result = num % 1 == 0 ? num.ToString("F0") : num.ToString("G6");
                return true;
            }
        }
        catch { }

        return false;
    }

    private static void SetClipboard(string text)
    {
        try
        {
            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);
        }
        catch { }
    }
}
