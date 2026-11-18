using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.Vault;
using AppLock.Services.Vault.Crypto;

namespace AppLock.Services.Vault;

/// <summary>
/// Creates a new vault on disk: initializes folders, generates crypto material,
/// writes config and index, and registers the vault in app settings.
/// </summary>
public static class VaultFactory
{
    /// <summary>
    /// Creates a new vault.
    /// </summary>
    /// <param name="name">Display name for the vault.</param>
    /// <param name="vaultPath">Empty or new folder for vault files.</param>
    /// <param name="password">Password to protect the vault.</param>
    /// <param name="rawRecoveryKey">160-bit recovery key raw bytes.</param>
    /// <param name="usesAdminPassword">True if password == AppLock admin password.</param>
    /// <param name="autoLockMinutes">Per-vault auto-lock override (null = use global default).</param>
    /// <param name="progress">Optional progress reporter (0–100).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new VaultRegistration that was added to settings.</returns>
    public static async Task<VaultRegistration> CreateVaultAsync(
        string name,
        string vaultPath,
        string password,
        byte[] rawRecoveryKey,
        bool usesAdminPassword = false,
        int? autoLockMinutes = null,
        IProgress<(int Percent, string Status)>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((5, "Initializing vault folder…"));

            // 1. Create folder layout
            VaultStorage.InitializeVaultFolder(vaultPath);

            ct.ThrowIfCancellationRequested();
            progress?.Report((15, "Generating encryption keys…"));

            // 2. Generate VMK
            var vaultId = Guid.NewGuid();
            var vmk     = VaultCrypto.GenerateVmk();

            try
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((25, "Deriving password key (this may take a moment)…"));

                // 3. KDF parameters — start with 256 MiB / 3 iterations / 4 threads
                //    (calibration is optional in future; for now use defaults)
                var salt = new byte[16];
                RandomNumberGenerator.Fill(salt);

                var kdfParams = new KdfParams
                {
                    Algorithm   = "argon2id",
                    MemorySizeKb = 262144, // 256 MiB
                    Iterations  = 3,
                    Parallelism = 4,
                    Salt        = Convert.ToBase64String(salt)
                };

                var kdf = new Argon2Kdf();

                ct.ThrowIfCancellationRequested();
                progress?.Report((30, "Creating password slot…"));

                // 4. Password slot
                var passwordSlot = KeySlots.CreatePasswordSlot(vmk, vaultId, password, kdfParams, kdf);

                ct.ThrowIfCancellationRequested();
                progress?.Report((55, "Creating recovery slot…"));

                // 5. Recovery slot
                var recoverySlot = KeySlots.CreateRecoverySlot(vmk, vaultId, rawRecoveryKey);

                ct.ThrowIfCancellationRequested();
                progress?.Report((65, "Creating verifier…"));

                // 6. Verifier
                var verifier = VaultCrypto.CreateVerifier(vmk, vaultId);

                ct.ThrowIfCancellationRequested();
                progress?.Report((70, "Writing vault configuration…"));

                // 7. Build config
                var config = new VaultConfig
                {
                    FormatVersion = 1,
                    VaultId       = vaultId,
                    Name          = name,
                    Created       = DateTime.UtcNow,
                    CipherSuite   = "AES-256-GCM",
                    ChunkSize     = 262144, // 256 KiB
                    Kdf           = kdfParams,
                    Slots         = [passwordSlot, recoverySlot],
                    Verifier      = verifier
                };

                // ConfigMac is computed inside SaveConfig using the derived key
                var configMacKey = VaultCrypto.DeriveConfigMacKey(vmk, vaultId);
                try
                {
                    VaultStorage.SaveConfig(vaultPath, config, configMacKey);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(configMacKey);
                }

                ct.ThrowIfCancellationRequested();
                progress?.Report((80, "Writing encrypted index…"));

                // 8. Create empty index
                var indexData = new VaultIndexData
                {
                    VaultId  = vaultId,
                    Revision = 1,
                    Items    = []
                };
                var indexKey = VaultCrypto.DeriveIndexKey(vmk, vaultId);
                try
                {
                    VaultStorage.SaveIndex(vaultPath, indexData, indexKey);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(indexKey);
                }

                ct.ThrowIfCancellationRequested();
                progress?.Report((90, "Registering vault…"));

                // 9. Register in settings
                var registration = new VaultRegistration
                {
                    Id                   = vaultId,
                    Name                 = name,
                    Path                 = vaultPath,
                    LastOpened           = DateTime.UtcNow,
                    AutoLockMinutes      = autoLockMinutes,
                    UsesAdminPassword    = usesAdminPassword,
                    PendingRewrap        = false,
                    HighestIndexRevision = 1
                };

                Services.ServiceContainer.Settings.Vault.Vaults.Add(registration);
                Services.ServiceContainer.SettingsStore.Save(Services.ServiceContainer.Settings);

                progress?.Report((100, "Done!"));
                return registration;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(vmk);
            }
        }, ct);
    }

    /// <summary>
    /// Returns the default vault path: %LOCALAPPDATA%\AppLock\Vaults\{name}
    /// </summary>
    public static string GetDefaultVaultPath(string name)
    {
        var vaultsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AppLock", "Vaults");
        // Sanitize name for use as folder name
        var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(vaultsRoot, safeName);
    }

    /// <summary>
    /// Validates the chosen vault path and returns null on success or an error message.
    /// </summary>
    public static string? ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Please choose a folder.";

        // Must not be inside Windows or Program Files
        var winDir     = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var progFiles  = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var progFilesX = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var appDataDir = Services.ServiceContainer.SettingsStore.SettingsDirectory;

        if (IsSubPath(path, winDir))     return "Cannot create a vault inside the Windows folder.";
        if (IsSubPath(path, progFiles))  return "Cannot create a vault inside Program Files.";
        if (IsSubPath(path, progFilesX)) return "Cannot create a vault inside Program Files (x86).";
        if (appDataDir != null && IsSubPath(path, appDataDir))
            return "Cannot create a vault inside the AppLock settings folder.";

        // Must be empty or not exist
        if (Directory.Exists(path))
        {
            var entries = Directory.GetFileSystemEntries(path);
            if (entries.Length > 0)
            {
                // If it already has vault.config it might be an existing vault
                if (File.Exists(Path.Combine(path, VaultStorage.ConfigFileName)))
                    return "This folder already contains a vault. Use 'Open existing vault' instead.";
                return "The chosen folder is not empty. Choose an empty or new folder.";
            }
        }

        return null; // valid
    }

    private static bool IsSubPath(string child, string parent)
    {
        try
        {
            var childFull  = Path.GetFullPath(child  + Path.DirectorySeparatorChar);
            var parentFull = Path.GetFullPath(parent + Path.DirectorySeparatorChar);
            return childFull.StartsWith(parentFull, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
