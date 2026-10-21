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
    }

    public IReadOnlyList<LockRule> GetAllRules()
    {
        return _settings.LockRules.AsReadOnly();
    }

    public void AddRule(LockRule rule)
    {
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
                return window.ProcessName.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                       window.ProcessPath.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase);

            case LockRuleType.WindowTitle:
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
                    return window.WindowTitle.Contains(rule.WindowTitlePattern, StringComparison.OrdinalIgnoreCase);
                }

            case LockRuleType.PickedWindow:
                // For picked windows, match both process and title
                return window.ProcessName.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                       window.WindowTitle.Contains(rule.WindowTitlePattern, StringComparison.OrdinalIgnoreCase);

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
