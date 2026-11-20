using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppLock.Models;
using Microsoft.Win32;

namespace AppLock.Services.WebFilter;

/// <summary>
/// Manages machine-level browser policies to disable Secure DNS (DNS-over-HTTPS) in Chrome,
/// Edge, Brave, and Firefox, ensuring queries flow through the local DNS filter.
/// </summary>
public sealed class BrowserPolicyService
{
    private const string ChromePolicyKey = @"SOFTWARE\Policies\Google\Chrome";
    private const string EdgePolicyKey = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string BravePolicyKey = @"SOFTWARE\Policies\BraveSoftware\Brave";
    private const string DnsOverHttpsModeValue = "DnsOverHttpsMode";
    private const string DnsOverHttpsModeOff = "off";

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetGetJoinInformation(string? server, out IntPtr nameBuffer, out int bufferType);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    public bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if this PC is joined to an Active Directory domain (NetSetupDomainName = 3).
    /// </summary>
    public static bool IsDomainJoined()
    {
        try
        {
            int res = NetGetJoinInformation(null, out IntPtr pName, out int bufferType);
            if (pName != IntPtr.Zero)
            {
                NetApiBufferFree(pName);
            }
            return res == 0 && bufferType == 3;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Writes machine policies disabling DoH in Chrome, Edge, Brave, and Firefox.
    /// Records previous state in settings.Applied.PolicyValues.
    /// </summary>
    public void ApplyBrowserPolicies(WebFilterSettings settings)
    {
        if (!IsElevated())
        {
            Debug.WriteLine("[BrowserPolicy] Not elevated; cannot write HKLM browser policies.");
            return;
        }

        if (IsDomainJoined())
        {
            Debug.WriteLine("[BrowserPolicy] PC is domain-joined; skipping browser policy enforcement.");
            return;
        }

        try
        {
            // Apply Chromium policies (Chrome, Edge, Brave)
            ApplyRegistryPolicy(settings, ChromePolicyKey, DnsOverHttpsModeValue, DnsOverHttpsModeOff);
            ApplyRegistryPolicy(settings, EdgePolicyKey, DnsOverHttpsModeValue, DnsOverHttpsModeOff);
            ApplyRegistryPolicy(settings, BravePolicyKey, DnsOverHttpsModeValue, DnsOverHttpsModeOff);

            // Apply Firefox policy if Firefox is installed
            ApplyFirefoxPolicy(settings);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BrowserPolicy] ApplyBrowserPolicies error: {ex.Message}");
        }
    }

    /// <summary>
    /// Reverts all policies recorded in settings.Applied.PolicyValues.
    /// </summary>
    public void RemoveBrowserPolicies(WebFilterSettings settings)
    {
        if (!IsElevated()) return;

        try
        {
            foreach (var pv in settings.Applied.PolicyValues)
            {
                if (pv.KeyPath.EndsWith("policies.json", StringComparison.OrdinalIgnoreCase))
                {
                    RevertFirefoxPolicy(pv);
                }
                else
                {
                    RevertRegistryPolicy(pv);
                }
            }

            settings.Applied.PolicyValues.Clear();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BrowserPolicy] RemoveBrowserPolicies error: {ex.Message}");
        }
    }

    private static void ApplyRegistryPolicy(
        WebFilterSettings settings,
        string subKeyPath,
        string valueName,
        string desiredValue)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            using var existingKey = baseKey.OpenSubKey(subKeyPath, writable: false);

            var policyRecord = new PolicyValue
            {
                KeyPath = subKeyPath,
                ValueName = valueName
            };

            if (existingKey != null)
            {
                object? existingVal = existingKey.GetValue(valueName);
                if (existingVal != null)
                {
                    policyRecord.Value = existingVal.ToString() ?? string.Empty;
                    policyRecord.WasMissing = false;
                }
                else
                {
                    policyRecord.WasMissing = true;
                }
            }
            else
            {
                policyRecord.WasMissing = true;
            }

            // Only record if we haven't already recorded this value
            if (!settings.Applied.PolicyValues.Exists(p =>
                string.Equals(p.KeyPath, subKeyPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.ValueName, valueName, StringComparison.OrdinalIgnoreCase)))
            {
                settings.Applied.PolicyValues.Add(policyRecord);
            }

            // Write desired value
            using var writeKey = baseKey.CreateSubKey(subKeyPath, writable: true);
            writeKey.SetValue(valueName, desiredValue, RegistryValueKind.String);
            Debug.WriteLine($"[BrowserPolicy] Set HKLM\\{subKeyPath}\\{valueName} = {desiredValue}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BrowserPolicy] Failed to write HKLM\\{subKeyPath}\\{valueName}: {ex.Message}");
        }
    }

    private static void RevertRegistryPolicy(PolicyValue pv)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            using var writeKey = baseKey.OpenSubKey(pv.KeyPath, writable: true);
            if (writeKey == null) return;

            if (pv.WasMissing)
            {
                writeKey.DeleteValue(pv.ValueName, throwOnMissingValue: false);
                Debug.WriteLine($"[BrowserPolicy] Removed HKLM\\{pv.KeyPath}\\{pv.ValueName}");
            }
            else
            {
                writeKey.SetValue(pv.ValueName, pv.Value, RegistryValueKind.String);
                Debug.WriteLine($"[BrowserPolicy] Restored HKLM\\{pv.KeyPath}\\{pv.ValueName} = {pv.Value}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BrowserPolicy] Failed to revert {pv.KeyPath}\\{pv.ValueName}: {ex.Message}");
        }
    }

    private static void ApplyFirefoxPolicy(WebFilterSettings settings)
    {
        var firefoxDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox")
        };

        foreach (var dir in firefoxDirs)
        {
            if (!Directory.Exists(dir)) continue;

            string distDir = Path.Combine(dir, "distribution");
            string policiesPath = Path.Combine(distDir, "policies.json");

            try
            {
                var policyRecord = new PolicyValue
                {
                    KeyPath = policiesPath,
                    ValueName = "policies.json"
                };

                if (File.Exists(policiesPath))
                {
                    policyRecord.Value = File.ReadAllText(policiesPath);
                    policyRecord.WasMissing = false;
                }
                else
                {
                    policyRecord.WasMissing = true;
                }

                if (!settings.Applied.PolicyValues.Exists(p =>
                    string.Equals(p.KeyPath, policiesPath, StringComparison.OrdinalIgnoreCase)))
                {
                    settings.Applied.PolicyValues.Add(policyRecord);
                }

                Directory.CreateDirectory(distDir);

                // Build or merge policies.json
                JsonObject rootObj;
                if (!policyRecord.WasMissing)
                {
                    try
                    {
                        rootObj = JsonNode.Parse(policyRecord.Value)?.AsObject() ?? new JsonObject();
                    }
                    catch
                    {
                        rootObj = new JsonObject();
                    }
                }
                else
                {
                    rootObj = new JsonObject();
                }

                if (!rootObj.ContainsKey("policies") || rootObj["policies"] is not JsonObject)
                {
                    rootObj["policies"] = new JsonObject();
                }

                var policiesNode = rootObj["policies"]!.AsObject();
                policiesNode["DNSOverHTTPS"] = new JsonObject
                {
                    ["Enabled"] = false,
                    ["Locked"] = true
                };

                File.WriteAllText(policiesPath, rootObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Debug.WriteLine($"[BrowserPolicy] Written Firefox policies.json to {policiesPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BrowserPolicy] Failed to write Firefox policy to {policiesPath}: {ex.Message}");
            }
        }
    }

    private static void RevertFirefoxPolicy(PolicyValue pv)
    {
        try
        {
            if (pv.WasMissing)
            {
                if (File.Exists(pv.KeyPath))
                {
                    File.Delete(pv.KeyPath);
                    Debug.WriteLine($"[BrowserPolicy] Deleted Firefox policies.json at {pv.KeyPath}");
                }

                string? distDir = Path.GetDirectoryName(pv.KeyPath);
                if (distDir != null && Directory.Exists(distDir) && Directory.GetFileSystemEntries(distDir).Length == 0)
                {
                    Directory.Delete(distDir);
                }
            }
            else
            {
                File.WriteAllText(pv.KeyPath, pv.Value);
                Debug.WriteLine($"[BrowserPolicy] Restored Firefox policies.json at {pv.KeyPath}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BrowserPolicy] Failed to revert Firefox policy at {pv.KeyPath}: {ex.Message}");
        }
    }
}
