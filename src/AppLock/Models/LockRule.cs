namespace AppLock.Models;

public enum LockRuleType
{
    ProcessName,
    WindowTitle,
    PickedWindow
}

public class LockRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public LockRuleType Type { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string WindowTitlePattern { get; set; } = string.Empty;
    public bool IsRegex { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string DisplayName { get; set; } = string.Empty;
}
