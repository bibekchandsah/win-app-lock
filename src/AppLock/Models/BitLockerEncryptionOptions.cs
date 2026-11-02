namespace AppLock.Models;

public enum BitLockerEncryptionScope
{
    UsedSpaceOnly = 0,
    EntireDrive = 1
}

public enum BitLockerEncryptionMethod
{
    XtsAes128 = 6,
    XtsAes256 = 7,
    AesCbc128 = 3,
    AesCbc256 = 4
}

public sealed class BitLockerTurnOnRequest
{
    public string DriveLetter { get; set; } = string.Empty;
    public string Passphrase { get; set; } = string.Empty;
    public string RecoveryPassword { get; set; } = string.Empty;
    public BitLockerEncryptionScope Scope { get; set; } = BitLockerEncryptionScope.UsedSpaceOnly;
    public BitLockerEncryptionMethod Method { get; set; } = BitLockerEncryptionMethod.XtsAes128;
}

