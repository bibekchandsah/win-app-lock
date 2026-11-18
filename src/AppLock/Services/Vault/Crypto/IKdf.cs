namespace AppLock.Services.Vault.Crypto;

public interface IKdf
{
    byte[] DeriveKey(string password, byte[] salt, int memorySizeKb, int iterations, int parallelism, int keyLengthBytes = 32);
}
