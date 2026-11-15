using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AppLock.Models;

namespace AppLock.Services.Firewall;

public record PortClassification(
    string? ServiceName,
    string? Category,
    PortHint? Hint
);

public class PortDatabaseEntry
{
    public string Protocol { get; set; } = "TCP";
    public int Port { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Hint { get; set; } = string.Empty;
    public string Severity { get; set; } = "Info";
}

public static class PortClassifier
{
    private static readonly Dictionary<(string Proto, int Port), PortDatabaseEntry> _entries = new();
    private static bool _initialized = false;
    private static readonly object _initLock = new();

    static PortClassifier()
    {
        EnsureInitialized();
    }

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            LoadPorts();
            _initialized = true;
        }
    }

    private static void LoadPorts()
    {
        // 1. Try to load from Assets/ports.json
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var path = Path.Combine(baseDir, "Assets", "ports.json");
            if (!File.Exists(path))
            {
                // Fallback check parent paths
                var alt = Path.Combine(baseDir, "..", "..", "..", "..", "Assets", "ports.json");
                if (File.Exists(alt)) path = alt;
            }

            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var list = JsonSerializer.Deserialize<List<PortDatabaseEntry>>(json, options);
                if (list != null)
                {
                    foreach (var entry in list)
                    {
                        var proto = entry.Protocol.ToUpperInvariant();
                        if (proto == "ANY")
                        {
                            _entries[("TCP", entry.Port)] = entry;
                            _entries[("UDP", entry.Port)] = entry;
                        }
                        else
                        {
                            _entries[(proto, entry.Port)] = entry;
                        }
                    }
                }
            }
        }
        catch { }

        // 2. Resilient fallback entries (if json not found)
        AddFallback("TCP", 21, "FTP", "File Transfer", "FTP transmits credentials and files in plaintext without encryption.", "Review");
        AddFallback("TCP", 22, "SSH", "Remote Access", "Secure encrypted remote terminal access.", "Info");
        AddFallback("TCP", 23, "Telnet", "Remote Access", "Telnet transmits all data unencrypted. Use SSH instead.", "Important");
        AddFallback("ANY", 53, "DNS", "Network", "Domain Name System server or resolver.", "Info");
        AddFallback("TCP", 80, "HTTP", "Web", "Standard unencrypted web server port.", "Info");
        AddFallback("TCP", 135, "RPC Endpoint Mapper", "System", "Windows Remote Procedure Call service. Keep blocked from untrusted networks.", "Info");
        AddFallback("ANY", 137, "NetBIOS Name Service", "Network", "Legacy Windows network name resolution service.", "Info");
        AddFallback("UDP", 138, "NetBIOS Datagram", "Network", "Legacy Windows NetBIOS datagram distribution service.", "Info");
        AddFallback("TCP", 139, "NetBIOS Session", "File Sharing", "Legacy NetBIOS file and print sharing.", "Review");
        AddFallback("TCP", 443, "HTTPS", "Web", "Secure encrypted web server port (SSL/TLS).", "Info");
        AddFallback("TCP", 445, "SMB", "File Sharing", "Windows File and Printer Sharing. Only expose to trusted home/work networks.", "Review");
        AddFallback("TCP", 1433, "SQL Server", "Database", "Microsoft SQL Server database engine.", "Review");
        AddFallback("UDP", 1900, "SSDP / UPnP", "Discovery", "Universal Plug and Play local device discovery.", "Info");
        AddFallback("TCP", 3000, "Dev Server (Node/React)", "Development", "A development server is reachable from other devices on this network (bound to all interfaces). Bind it to localhost if you don't need that.", "Review");
        AddFallback("TCP", 3306, "MySQL", "Database", "MySQL / MariaDB database server.", "Review");
        AddFallback("TCP", 3389, "Remote Desktop", "Remote Access", "Remote Desktop is open to your network. If you don't use it, block it.", "Important");
        AddFallback("TCP", 5037, "ADB Server", "Development", "Android Debug Bridge daemon for local Android debugging.", "Info");
        AddFallback("TCP", 5040, "Device Sharing", "System", "Windows Connected Devices Service.", "Info");
        AddFallback("TCP", 5173, "Dev Server (Vite)", "Development", "A development server is reachable from other devices on this network (bound to all interfaces). Bind it to localhost if you don't need that.", "Review");
        AddFallback("UDP", 5353, "mDNS", "Discovery", "Multicast DNS / Bonjour zero-configuration discovery.", "Info");
        AddFallback("TCP", 5432, "PostgreSQL", "Database", "PostgreSQL relational database server.", "Review");
        AddFallback("TCP", 5555, "ADB over Network", "Development", "Android Debug Bridge is reachable on the network. Anyone on this network could try to connect to ADB.", "Important");
        AddFallback("TCP", 5900, "VNC", "Remote Access", "Virtual Network Computing remote screen sharing.", "Review");
        AddFallback("TCP", 5985, "WinRM HTTP", "Remote Access", "Windows Remote Management unencrypted HTTP endpoint.", "Review");
        AddFallback("TCP", 5986, "WinRM HTTPS", "Remote Access", "Windows Remote Management encrypted HTTPS endpoint.", "Info");
        AddFallback("TCP", 6379, "Redis", "Database", "Redis in-memory data store. Ensure authentication is enabled.", "Review");
        AddFallback("TCP", 8000, "Dev Server (HTTP/Python)", "Development", "A development server is reachable from other devices on this network (bound to all interfaces). Bind it to localhost if you don't need that.", "Review");
        AddFallback("TCP", 8080, "Dev Server (HTTP-Alt)", "Development", "A development server is reachable from other devices on this network (bound to all interfaces). Bind it to localhost if you don't need that.", "Review");
        AddFallback("TCP", 8081, "Dev Server", "Development", "A development server is reachable from other devices on this network (bound to all interfaces). Bind it to localhost if you don't need that.", "Review");
        AddFallback("TCP", 9200, "Elasticsearch", "Database", "Elasticsearch distributed search and analytics engine.", "Review");
        AddFallback("TCP", 27017, "MongoDB", "Database", "MongoDB document database server.", "Review");
    }

    private static void AddFallback(string proto, int port, string name, string category, string hint, string severity)
    {
        proto = proto.ToUpperInvariant();
        var entry = new PortDatabaseEntry
        {
            Protocol = proto,
            Port = port,
            ServiceName = name,
            Category = category,
            Hint = hint,
            Severity = severity
        };

        if (proto == "ANY")
        {
            if (!_entries.ContainsKey(("TCP", port))) _entries[("TCP", port)] = entry;
            if (!_entries.ContainsKey(("UDP", port))) _entries[("UDP", port)] = entry;
        }
        else
        {
            if (!_entries.ContainsKey((proto, port))) _entries[(proto, port)] = entry;
        }
    }

    public static PortClassification Classify(string protocol, int port, PortScope scope)
    {
        EnsureInitialized();
        string upperProto = protocol.ToUpperInvariant();

        // Check exact match in database
        if (_entries.TryGetValue((upperProto, port), out var entry))
        {
            var severity = entry.Severity?.ToLowerInvariant() switch
            {
                "important" => HintSeverity.Important,
                "review"    => HintSeverity.Review,
                _           => HintSeverity.Info
            };

            // Dynamic scope adaptation
            PortHint hint;
            if (scope == PortScope.LocalhostOnly)
            {
                hint = new PortHint("Listening only on localhost: other devices cannot connect.", HintSeverity.Info);
            }
            else
            {
                hint = new PortHint(entry.Hint, severity);
            }

            return new PortClassification(entry.ServiceName, entry.Category, hint);
        }

        // Check Windows dynamic RPC range 49152 - 65535
        if (port >= 49152 && port <= 65535)
        {
            var rpcHint = scope == PortScope.LocalhostOnly
                ? new PortHint("Listening only on localhost: other devices cannot connect.", HintSeverity.Info)
                : new PortHint("Windows dynamic RPC ephemeral port.", HintSeverity.Info);

            return new PortClassification("Windows dynamic port", "System", rpcHint);
        }

        // Fallback for unlisted ports
        PortHint? defaultHint = scope switch
        {
            PortScope.LocalhostOnly => new PortHint("Listening only on localhost: other devices cannot connect.", HintSeverity.Info),
            PortScope.AllInterfaces => new PortHint("Reachable from any device on your Wi-Fi or local network (bound to 0.0.0.0 or ::).", HintSeverity.Info),
            PortScope.LocalNetwork  => new PortHint("Reachable by other devices on your local Wi-Fi or home network.", HintSeverity.Info),
            _                       => null
        };

        return new PortClassification(null, null, defaultHint);
    }
}
