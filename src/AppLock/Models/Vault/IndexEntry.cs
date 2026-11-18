using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AppLock.Models.Vault;

public class VaultIndexData
{
    [JsonPropertyName("vaultId")]
    public Guid VaultId { get; set; }

    [JsonPropertyName("revision")]
    public long Revision { get; set; } = 1;

    [JsonPropertyName("updated")]
    public DateTime Updated { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("items")]
    public List<IndexItem> Items { get; set; } = new();
}

public class IndexItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("parentId")]
    public Guid? ParentId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("isDirectory")]
    public bool IsDirectory { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("createdTimeUtc")]
    public DateTime CreatedTimeUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("modifiedTimeUtc")]
    public DateTime ModifiedTimeUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("attributes")]
    public int Attributes { get; set; } = 0;

    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }

    [JsonPropertyName("objectId")]
    public string? ObjectId { get; set; } // e.g. "a1b2c3d4e5f6...obj"

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("isTrash")]
    public bool IsTrash { get; set; }

    [JsonPropertyName("deletedTimeUtc")]
    public DateTime? DeletedTimeUtc { get; set; }
}
