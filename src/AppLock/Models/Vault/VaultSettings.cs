using System;
using System.Collections.Generic;

namespace AppLock.Models.Vault;

public class VaultSettings
{
    public List<VaultRegistration> Vaults { get; set; } = new();
    public int DefaultAutoLockMinutes { get; set; } = 10;
    public bool LockOnPcLock { get; set; } = true;
    public int LockOnMinimizeMinutes { get; set; } = 5;
    public bool LockOnExit { get; set; } = true;
    public bool ExcludeFromCapture { get; set; } = true;
    public bool SecurePreviewOnly { get; set; }
    public bool TempGenericNames { get; set; } = true;
    public bool TempOverwriteOnDelete { get; set; } = true;
    public bool DeleteOriginalsAfterVerify { get; set; } = true;
    public bool OverwriteOriginals { get; set; }
    public int TrashDays { get; set; } = 30;
    public bool SendToEnabled { get; set; }
    public bool RequireFreshAuth { get; set; } = true;
}

public class VaultRegistration
{
    public Guid Id { get; set; }                 // matches vault.config id
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public DateTime LastOpened { get; set; }
    public int? AutoLockMinutes { get; set; }    // per-vault override
    public bool? SecurePreviewOnly { get; set; }
    public bool UsesAdminPassword { get; set; }
    public bool PendingRewrap { get; set; }
    public long HighestIndexRevision { get; set; }
}
