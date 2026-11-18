using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AppLock.Models.Vault;

public class VaultConfig
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = 1;

    [JsonPropertyName("vaultId")]
    public Guid VaultId { get; set; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    public DateTime Created { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("cipherSuite")]
    public string CipherSuite { get; set; } = "AES-256-GCM";

    [JsonPropertyName("chunkSize")]
    public int ChunkSize { get; set; } = 262144; // 256 KiB

    [JsonPropertyName("kdf")]
    public KdfParams Kdf { get; set; } = new();

    [JsonPropertyName("slots")]
    public List<KeySlot> Slots { get; set; } = new();

    [JsonPropertyName("verifier")]
    public VerifierData Verifier { get; set; } = new();

    [JsonPropertyName("helloChallenge")]
    public string? HelloChallenge { get; set; }

    [JsonPropertyName("configMac")]
    public string ConfigMac { get; set; } = string.Empty; // Base64 HMAC-SHA256
}

public class VerifierData
{
    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty; // Base64 (12 bytes)

    [JsonPropertyName("ciphertext")]
    public string Ciphertext { get; set; } = string.Empty; // Base64 (32 bytes)

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = string.Empty; // Base64 (16 bytes)
}
