namespace AppLock.Models
{
    public class LockKeyIndicatorSettings
    {
        public bool Enabled { get; set; } = true;
        public bool ShowCapsLock { get; set; } = true;
        public bool ShowNumLock { get; set; } = true;
        public bool ShowScrollLock { get; set; } = false;
        
        public void Validate()
        {
            // No validation needed for booleans
        }
    }
}
