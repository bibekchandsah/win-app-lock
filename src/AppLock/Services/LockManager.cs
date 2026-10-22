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

                // 1. Direct process name or path match
                if ((!string.IsNullOrEmpty(window.ProcessName) && window.ProcessName.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(window.ProcessPath) && window.ProcessPath.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(window.ProcessPath) && !string.IsNullOrEmpty(rule.ProcessPath) && window.ProcessPath.Equals(rule.ProcessPath, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                // 2. UWP alias matching (e.g. rule: "CalculatorApp", window: "Calculator" or vice versa)
                if (!string.IsNullOrEmpty(window.WindowTitle) &&
                    (window.WindowTitle.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                     rule.ProcessName.StartsWith(window.WindowTitle, StringComparison.OrdinalIgnoreCase) ||
                     window.WindowTitle.StartsWith(rule.ProcessName, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(window.ProcessName) && !string.IsNullOrEmpty(rule.ProcessName) &&
                    (rule.ProcessName.StartsWith(window.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                     window.ProcessName.StartsWith(rule.ProcessName, StringComparison.OrdinalIgnoreCase)))
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

                bool procMatches = !string.IsNullOrWhiteSpace(proc) &&
                    ((!string.IsNullOrEmpty(window.ProcessName) && window.ProcessName.Equals(proc, StringComparison.OrdinalIgnoreCase)) ||
                     (!string.IsNullOrEmpty(window.ProcessPath) && !string.IsNullOrEmpty(rule.ProcessPath) && window.ProcessPath.Equals(rule.ProcessPath, StringComparison.OrdinalIgnoreCase)) ||
                     (!string.IsNullOrEmpty(window.ProcessName) && (proc.StartsWith(window.ProcessName, StringComparison.OrdinalIgnoreCase) || window.ProcessName.StartsWith(proc, StringComparison.OrdinalIgnoreCase))));

                bool titleMatches = string.IsNullOrEmpty(rule.WindowTitlePattern) ||
                    (!string.IsNullOrEmpty(window.WindowTitle) && (window.WindowTitle.Contains(rule.WindowTitlePattern, StringComparison.OrdinalIgnoreCase) || rule.WindowTitlePattern.Contains(window.WindowTitle, StringComparison.OrdinalIgnoreCase)));

                return procMatches || (titleMatches && !string.IsNullOrWhiteSpace(rule.WindowTitlePattern));

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
