using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace AppLock.Services;

/// <summary>
/// Reads the Wi-Fi profiles saved on this PC together with their plaintext keys.
/// Uses the same approach as the classic "netsh wlan export profile key=clear" script:
/// netsh exports one XML per profile into a temp folder and the XML is parsed for
/// &lt;name&gt; and &lt;keyMaterial&gt;, which keeps it independent of the display language.
/// </summary>
public static class WifiPasswordService
{
    public sealed class WifiNetwork
    {
        public string Ssid { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;
        public string Authentication { get; init; } = string.Empty;

        /// <summary>Open networks have no key at all - that is not the same as "key unavailable".</summary>
        public bool IsOpen => Authentication.Equals("open", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<List<WifiNetwork>> GetSavedNetworksAsync()
    {
        return await Task.Run(async () =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "AppLockWifi_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            try
            {
                var startInfo = new ProcessStartInfo("netsh.exe", $"wlan export profile key=clear folder=\"{folder}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    return new List<WifiNetwork>();
                }

                await process.WaitForExitAsync();

                var networks = new List<WifiNetwork>();
                foreach (var file in Directory.EnumerateFiles(folder, "*.xml"))
                {
                    var network = ParseProfile(file);
                    if (network != null)
                    {
                        networks.Add(network);
                    }
                }

                return networks
                    .OrderBy(n => n.Ssid, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { }
            }
        });
    }

    private static WifiNetwork? ParseProfile(string path)
    {
        try
        {
            var document = XDocument.Load(path);

            string Value(string localName) =>
                document.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == localName)?.Value ?? string.Empty;

            string ssid = Value("name");
            if (string.IsNullOrWhiteSpace(ssid))
            {
                return null;
            }

            return new WifiNetwork
            {
                Ssid = ssid,
                Password = Value("keyMaterial"),
                Authentication = Value("authentication")
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WifiPasswordService] Could not read '{path}': {ex.Message}");
            return null;
        }
    }
}
