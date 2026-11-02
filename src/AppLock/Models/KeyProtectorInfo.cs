namespace AppLock.Models;

public class KeyProtectorInfo
{
    public string Id { get; set; } = "";
    public string TypeName { get; set; } = "";
    public int TypeCode { get; set; }

    public string IconGlyph => TypeCode switch
    {
        1 => "\uE964",          // TPM
        2 => "\uE8D7",          // External key
        3 => "\uE8D7",          // Numerical / Recovery password
        4 or 5 or 6 => "\uE964",// TPM combo
        7 => "\uE8D7",          // Certificate / Public key
        8 => "\uE72E",          // Password
        _ => "\uE8D7"
    };
}
