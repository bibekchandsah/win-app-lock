namespace AppLock.Models
{
    public class KeyboardSettings
    {
        public bool Enabled { get; set; } = true;
        
        public void Validate()
        {
            // No validation needed for simple boolean
        }
    }
}
