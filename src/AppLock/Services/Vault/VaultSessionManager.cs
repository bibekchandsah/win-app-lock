using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models.Vault;
using AppLock.Services.Vault.Crypto;

namespace AppLock.Services.Vault;

/// <summary>
/// Holds the in-memory state of one open (unlocked) vault.
/// Keys are pinned in memory; call Dispose() to zero them.
/// </summary>
public sealed class VaultSession : IDisposable
{
    public Guid VaultId { get; }
    public string VaultPath { get; }
    public string VaultName { get; }
    public byte[] Vmk { get; }
    public byte[] IndexKey { get; }
    public byte[] FekWrapKey { get; }
    public byte[] ConfigMacKey { get; }
    public DateTime UnlockedAt { get; } = DateTime.UtcNow;
    public VaultIndexData Index { get; set; }
    public VaultLockHandle? LockHandle { get; set; }

    private bool _disposed;

    public VaultSession(
        Guid vaultId,
        string vaultPath,
        string vaultName,
        byte[] vmk,
        VaultIndexData index)
    {
        VaultId = vaultId;
        VaultPath = vaultPath;
        VaultName = vaultName;
        Vmk = vmk;
        Index = index;

        // Derive sub-keys eagerly
        IndexKey     = VaultCrypto.DeriveIndexKey(vmk, vaultId);
        FekWrapKey   = VaultCrypto.DeriveFekWrapKey(vmk, vaultId);
        ConfigMacKey = VaultCrypto.DeriveConfigMacKey(vmk, vaultId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            LockHandle?.Dispose();
            LockHandle = null;
        }
        catch { }

        CryptographicOperations.ZeroMemory(Vmk);
        CryptographicOperations.ZeroMemory(IndexKey);
        CryptographicOperations.ZeroMemory(FekWrapKey);
        CryptographicOperations.ZeroMemory(ConfigMacKey);
    }
}

/// <summary>
/// Singleton that tracks all open vault sessions and handles unlock/lock lifecycle.
/// </summary>
public sealed class VaultSessionManager
{
    private readonly Dictionary<Guid, VaultSession> _sessions = new();
    private readonly object _lock = new();

    public event EventHandler<Guid>? VaultLocked;
    public event EventHandler<Guid>? VaultUnlocked;

    // ---- Unlock -------------------------------------------------------

    /// <summary>
    /// Unlocks a vault with a password. Returns the new session on success.
    /// Throws CryptographicException on wrong password, or VaultLockConflictException if open elsewhere.
    /// </summary>
    public async Task<VaultSession> UnlockWithPasswordAsync(
        VaultRegistration reg,
        string password,
        bool forceTakeOver = false,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            // 1. Acquire single-writer lock
            var lockHandle = VaultLockFile.Acquire(reg.Path, forceTakeOver);

            try
            {
                var config = VaultStorage.LoadConfig(reg.Path);
                var kdf = new Argon2Kdf();

                // Try every password slot
                foreach (var slot in config.Slots)
                {
                    if (slot.Type != "password") continue;
                    try
                    {
                        var vmk = KeySlots.UnlockPasswordSlot(slot, config.VaultId, password, kdf);
                        ct.ThrowIfCancellationRequested();

                        // Verify with verifier
                        var macKey = VaultCrypto.DeriveConfigMacKey(vmk, config.VaultId);
                        if (!VaultCrypto.VerifyConfigMac(config, macKey))
                            throw new CryptographicException("Config MAC mismatch after unlock.");

                        if (!VaultCrypto.CheckVerifier(vmk, config.VaultId, config.Verifier))
                            throw new CryptographicException("Verifier check failed.");

                        var indexKey = VaultCrypto.DeriveIndexKey(vmk, config.VaultId);
                        var index    = VaultStorage.LoadIndex(reg.Path, indexKey, config.VaultId,
                                           reg.HighestIndexRevision > 0 ? reg.HighestIndexRevision : null);

                        var session = new VaultSession(config.VaultId, reg.Path, reg.Name, vmk, index)
                        {
                            LockHandle = lockHandle
                        };
                        AddSession(session);
                        return session;
                    }
                    catch (CryptographicException)
                    {
                        // Wrong slot or wrong password — try next slot
                    }
                }

                throw new CryptographicException("Invalid password.");
            }
            catch
            {
                lockHandle.Dispose();
                throw;
            }
        }, ct);
    }

    /// <summary>
    /// Unlocks a vault with a formatted recovery key string.
    /// </summary>
    public async Task<VaultSession> UnlockWithRecoveryKeyAsync(
        VaultRegistration reg,
        string formattedKey,
        bool forceTakeOver = false,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (!RecoveryKey.TryParse(formattedKey, out var rawKey, out var err))
                throw new CryptographicException(err ?? "Invalid recovery key format.");

            // 1. Acquire single-writer lock
            var lockHandle = VaultLockFile.Acquire(reg.Path, forceTakeOver);

            try
            {
                var config = VaultStorage.LoadConfig(reg.Path);

                foreach (var slot in config.Slots)
                {
                    if (slot.Type != "recovery") continue;
                    try
                    {
                        var vmk = KeySlots.UnlockRecoverySlot(slot, config.VaultId, rawKey);
                        ct.ThrowIfCancellationRequested();

                        if (!VaultCrypto.CheckVerifier(vmk, config.VaultId, config.Verifier))
                            throw new CryptographicException("Verifier check failed.");

                        var indexKey = VaultCrypto.DeriveIndexKey(vmk, config.VaultId);
                        var index    = VaultStorage.LoadIndex(reg.Path, indexKey, config.VaultId);
                        var session  = new VaultSession(config.VaultId, reg.Path, reg.Name, vmk, index)
                        {
                            LockHandle = lockHandle
                        };
                        AddSession(session);
                        return session;
                    }
                    catch (CryptographicException)
                    {
                        // Wrong slot — try next
                    }
                }

                throw new CryptographicException("Recovery key did not match any slot.");
            }
            catch
            {
                lockHandle.Dispose();
                throw;
            }
        }, ct);
    }

    // ---- Lock ---------------------------------------------------------

    public void Lock(Guid vaultId)
    {
        try
        {
            TempOpenService.CloseAllSessionsForVaultAsync(vaultId).GetAwaiter().GetResult();
        }
        catch { }

        VaultSession? session;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(vaultId, out session))
                return;
            _sessions.Remove(vaultId);
        }
        session.Dispose();
        VaultLocked?.Invoke(this, vaultId);
    }

    public void LockAll()
    {
        try
        {
            TempOpenService.CloseAllSessionsAsync().GetAwaiter().GetResult();
        }
        catch { }

        List<VaultSession> sessions;
        lock (_lock)
        {
            sessions = new(_sessions.Values);
            _sessions.Clear();
        }
        foreach (var s in sessions)
        {
            s.Dispose();
            VaultLocked?.Invoke(this, s.VaultId);
        }
    }

    // ---- Query --------------------------------------------------------

    public bool IsUnlocked(Guid vaultId)
    {
        lock (_lock)
            return _sessions.ContainsKey(vaultId);
    }

    public VaultSession? GetSession(Guid vaultId)
    {
        lock (_lock)
            return _sessions.GetValueOrDefault(vaultId);
    }

    public IReadOnlyList<VaultSession> GetAllSessions()
    {
        lock (_lock)
            return new List<VaultSession>(_sessions.Values);
    }

    // ---- Helpers ------------------------------------------------------

    private void AddSession(VaultSession session)
    {
        lock (_lock)
        {
            // If already open, dispose the old one first
            if (_sessions.TryGetValue(session.VaultId, out var old))
            {
                old.Dispose();
            }
            _sessions[session.VaultId] = session;
        }
        VaultUnlocked?.Invoke(this, session.VaultId);
    }
}
