using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace AppLock.Services.Vault.Crypto;

public class Argon2Kdf : IKdf
{
    public byte[] DeriveKey(string password, byte[] salt, int memorySizeKb, int iterations, int parallelism, int keyLengthBytes = 32)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon2 = new Argon2id(passwordBytes)
            {
                Salt = salt,
                MemorySize = memorySizeKb,
                Iterations = iterations,
                DegreeOfParallelism = parallelism
            };
            return argon2.GetBytes(keyLengthBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }
}

public class FastTestKdf : IKdf
{
    public byte[] DeriveKey(string password, byte[] salt, int memorySizeKb, int iterations, int parallelism, int keyLengthBytes = 32)
    {
        // Fast PBKDF2 with SHA-256 for test execution speed
        return Rfc2898DeriveBytes.Pbkdf2(
            password: password,
            salt: salt,
            iterations: 100,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: keyLengthBytes);
    }
}
