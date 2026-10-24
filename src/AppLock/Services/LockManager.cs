using AppLock.Models;

namespace AppLock.Services;

public class LockManager
{
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;

    public bool IsProtectionEnabled { get; private set; }

    public event EventHandler<LockRule>? RuleAdded;
    public event EventHandler<Guid>? RuleRemoved;
    public event EventHandler<bool>? ProtectionStateChanged;

    public LockManager(SettingsStore settingsStore, AppSettings settings)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        
        // Restore protection state from settings
        IsProtectionEnabled = settings.IsProtectionEnabled;

        NormalizeRules();
    }

    private void NormalizeRules()
    {
        bool changed = false;
        var uniqueRules = new List<LockRule>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in _settings.LockRules)
        {
            if (rule.DisplayName.StartsWith("Picked: ", StringComparison.OrdinalIgnoreCase))
            {
                string title = rule.DisplayName.Substring("Picked: ".Length).Trim();
                if (string.IsNullOrWhiteSpace(rule.ProcessName))
                {
                    rule.ProcessName = !string.IsNullOrWhiteSpace(rule.ProcessPath)
                        ? System.IO.Path.GetFileNameWithoutExtension(rule.ProcessPath)
                        : title;
                }
                rule.DisplayName = $"{rule.ProcessName} - {title}";
                rule.Type = LockRuleType.ProcessName;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(rule.ProcessName) && !string.IsNullOrWhiteSpace(rule.ProcessPath))
            {
                rule.ProcessName = System.IO.Path.GetFileNameWithoutExtension(rule.ProcessPath);
                changed = true;
            }

            string key = !string.IsNullOrEmpty(rule.ProcessName)
                ? rule.ProcessName
                : (!string.IsNullOrEmpty(rule.DisplayName) ? rule.DisplayName : rule.Id.ToString());

            if (seenKeys.Add(key))
            {
                uniqueRules.Add(rule);
            }
            else
            {
                changed = true;
            }
        }

        if (changed)
        {
            _settings.LockRules.Clear();
            _settings.LockRules.AddRange(uniqueRules);
            _settingsStore.Save(_settings);
        }
    }

    public IReadOnlyList<LockRule> GetAllRules()
    {
        return _settings.LockRules.AsReadOnly();
    }

    public void AddRule(LockRule rule)
    {
        // Prevent duplicate rules for the same process
        bool alreadyExists = _settings.LockRules.Any(r =>
            (!string.IsNullOrEmpty(r.ProcessName) && !string.IsNullOrEmpty(rule.ProcessName) && r.ProcessName.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(r.DisplayName) && !string.IsNullOrEmpty(rule.DisplayName) && r.DisplayName.Equals(rule.DisplayName, StringComparison.OrdinalIgnoreCase)));

        if (alreadyExists)
            return;

        _settings.LockRules.Add(rule);
        _settingsStore.Save(_settings);
        RuleAdded?.Invoke(this, rule);
    }

    public void RemoveRule(Guid ruleId)
    {
        var rule = _settings.LockRules.FirstOrDefault(r => r.Id == ruleId);
        if (rule != null)
        {
            _settings.LockRules.Remove(rule);
            _settingsStore.Save(_settings);
            RuleRemoved?.Invoke(this, ruleId);
        }
    }

    public bool IsWindowLocked(RunningWindow window)
    {
        if (!IsProtectionEnabled)
            return false;

        foreach (var rule in _settings.LockRules)
        {
            if (RuleMatches(rule, window))
                return true;
        }

        return false;
    }

    private bool RuleMatches(LockRule rule, RunningWindow window)
    {
        switch (rule.Type)
        {
            case LockRuleType.ProcessName:
                if (string.IsNullOrWhiteSpace(rule.ProcessName))
                    return false;

                // 1. Exact process name match (most reliable — e.g. "excel", "notepad")
                if (!string.IsNullOrEmpty(window.ProcessName) &&
                    window.ProcessName.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // 2. Exact process path match (most reliable for UWP — e.g. full .exe path)
                if (!string.IsNullOrEmpty(window.ProcessPath) &&
                    !string.IsNullOrEmpty(rule.ProcessPath) &&
                    window.ProcessPath.Equals(rule.ProcessPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // 3. UWP: when the resolved UWP process name contains or starts with the rule name
                //    (e.g., rule: "Calculator", window.ProcessName: "CalculatorApp")
                //    Only allow this when both names share a common prefix of at least 5 chars
                //    to avoid false matches like "Calculator" matching "CalendarApp".
                if (!string.IsNullOrEmpty(window.ProcessName) && !string.IsNullOrEmpty(rule.ProcessName))
                {
                    string rn = rule.ProcessName;
                    string wn = window.ProcessName;
                    // Only allow prefix matching for clearly related names (e.g., "Calculator"/"CalculatorApp")
                    if ((wn.StartsWith(rn, StringComparison.OrdinalIgnoreCase) ||
                         rn.StartsWith(wn, StringComparison.OrdinalIgnoreCase)) &&
                        Math.Min(rn.Length, wn.Length) >= 5)
                    {
                        return true;
                    }
                }

                // 4. UWP title match: ONLY when window title exactly equals the rule process name
                //    (e.g., rule: "Calculator", window.WindowTitle: "Calculator")
                //    No StartsWith — that was causing false positives across all UWP apps.
                if (!string.IsNullOrEmpty(window.WindowTitle) &&
                    window.WindowTitle.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return false;

            case LockRuleType.WindowTitle:
                if (string.IsNullOrWhiteSpace(rule.WindowTitlePattern))
                    return false;
                if (rule.IsRegex)
                {
                    try
                    {
                        return System.Text.RegularExpressions.Regex.IsMatch(
                            window.WindowTitle,
                            rule.WindowTitlePattern,
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                }
                else
                {
                    return !string.IsNullOrEmpty(window.WindowTitle) &&
                           window.WindowTitle.Contains(rule.WindowTitlePattern, StringComparison.OrdinalIgnoreCase);
                }

            case LockRuleType.PickedWindow:
                string proc = !string.IsNullOrWhiteSpace(rule.ProcessName)
                    ? rule.ProcessName
                    : (!string.IsNullOrWhiteSpace(rule.ProcessPath) ? System.IO.Path.GetFileNameWithoutExtension(rule.ProcessPath) : string.Empty);

                // For PickedWindow: require process path OR process name match (exact/prefix).
                // Title alone is NOT sufficient — it would match unrelated apps with same title.
                bool procMatches = false;
                if (!string.IsNullOrWhiteSpace(proc))
                {
                    // Exact process name
                    procMatches = (!string.IsNullOrEmpty(window.ProcessName) &&
                                   window.ProcessName.Equals(proc, StringComparison.OrdinalIgnoreCase));

                    // Exact process path
                    if (!procMatches && !string.IsNullOrEmpty(window.ProcessPath) && !string.IsNullOrEmpty(rule.ProcessPath))
                        procMatches = window.ProcessPath.Equals(rule.ProcessPath, StringComparison.OrdinalIgnoreCase);

                    // UWP prefix match (at least 5 common chars)
                    if (!procMatches && !string.IsNullOrEmpty(window.ProcessName))
                    {
                        string rn = proc;
                        string wn = window.ProcessName;
                        if ((wn.StartsWith(rn, StringComparison.OrdinalIgnoreCase) ||
                             rn.StartsWith(wn, StringComparison.OrdinalIgnoreCase)) &&
                            Math.Min(rn.Length, wn.Length) >= 5)
                        {
                            procMatches = true;
                        }
                    }
                }

                if (!procMatches)
                    return false;

                // If we have a title pattern, ALSO require it (AND, not OR)
                if (!string.IsNullOrWhiteSpace(rule.WindowTitlePattern))
                {
                    return !string.IsNullOrEmpty(window.WindowTitle) &&
                           (window.WindowTitle.Contains(rule.WindowTitlePattern, StringComparison.OrdinalIgnoreCase) ||
                            rule.WindowTitlePattern.Contains(window.WindowTitle, StringComparison.OrdinalIgnoreCase));
                }

                return true;

            default:
                return false;
        }
    }

    public void EnableProtection()
    {
        IsProtectionEnabled = true;
        _settings.IsProtectionEnabled = true;
        _settingsStore.Save(_settings);
        ProtectionStateChanged?.Invoke(this, true);
    }

    public void DisableProtection()
    {
        IsProtectionEnabled = false;
        _settings.IsProtectionEnabled = false;
        _settingsStore.Save(_settings);
        ProtectionStateChanged?.Invoke(this, false);
    }
}
