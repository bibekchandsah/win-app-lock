using System;
using System.Text.Json.Serialization;

namespace AppLock.Models.Vault;

public class KeySlot
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("type")]
    public string Type { get; set; } = "password"; // "password", "recovery", "windows_hello"

    [JsonPropertyName("kdfParams")]
    public KdfParams? Kdf { get; set; }

    [JsonPropertyName("hkdfSalt")]
    public string? HkdfSalt { get; set; } // Base64 for recovery slot

    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty; // Base64 (12 bytes)

    [JsonPropertyName("wrappedVmk")]
    public string WrappedVmk { get; set; } = string.Empty; // Base64 (32 bytes VMK ciphertext)

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = string.Empty; // Base64 (16 bytes tag)
}

public class KdfParams
{
    [JsonPropertyName("algorithm")]
    public string Algorithm { get; set; } = "argon2id";

    [JsonPropertyName("m")]
    public int MemorySizeKb { get; set; } = 262144; // 256 MiB

    [JsonPropertyName("t")]
    public int Iterations { get; set; } = 3;

    [JsonPropertyName("p")]
    public int Parallelism { get; set; } = 4;

    [JsonPropertyName("salt")]
    public string Salt { get; set; } = string.Empty; // Base64 (16 bytes)
}
