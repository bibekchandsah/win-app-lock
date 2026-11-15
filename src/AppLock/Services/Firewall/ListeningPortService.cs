using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AppLock.Models;

namespace AppLock.Services.Firewall;

public interface IListeningPortService
{
    Task<IReadOnlyList<ListeningPort>> GetListeningPortsAsync(CancellationToken ct = default);
}

public class ListeningPortService : IListeningPortService
{
    #region Win32 IP Helper P/Invoke

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int dwOutBufLen,
        bool sort,
        int ipVersion,
        int tblClass,
        int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int dwOutBufLen,
        bool sort,
        int ipVersion,
        int tblClass,
        int reserved);

    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;
    private const uint MIB_TCP_STATE_LISTEN = 2;
    private const uint NO_ERROR = 0;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ucLocalAddr;
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ucRemoteAddr;
        public uint dwRemoteScopeId;
        public uint dwRemotePort;
        public uint dwState;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ucLocalAddr;
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    #endregion

    #region Win32 Process and Service API

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusEx(
        IntPtr hSCManager,
        int infoLevel,
        uint dwServiceType,
        uint dwServiceState,
        IntPtr lpServices,
        uint cbBufSize,
        out uint pcbBytesNeeded,
        out uint lpServicesReturned,
        ref uint lpResumeHandle,
        string? pszGroupName);

    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const int SC_ENUM_PROCESS_INFO = 0;
    private const uint SERVICE_WIN32 = 0x00000030;
    private const uint SERVICE_STATE_ALL = 0x00000003;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ENUM_SERVICE_STATUS_PROCESS
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    #endregion

    private readonly ConcurrentDictionary<int, (string Name, string? Path, string? Publisher, IReadOnlyList<string> Services, string? ServiceLabel)> _processInfoCache = new();

    public async Task<IReadOnlyList<ListeningPort>> GetListeningPortsAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var list = new List<ListeningPort>();

            // 1. TCP IPv4 listeners
            GetTcp4Listeners(list, ct);
            if (ct.IsCancellationRequested) return list;

            // 2. TCP IPv6 listeners
            GetTcp6Listeners(list, ct);
            if (ct.IsCancellationRequested) return list;

            // 3. UDP IPv4 bound sockets
            GetUdp4Sockets(list, ct);
            if (ct.IsCancellationRequested) return list;

            // 4. UDP IPv6 bound sockets
            GetUdp6Sockets(list, ct);

            return list;
        }, ct).ConfigureAwait(false);
    }

    private void GetTcp4Listeners(List<ListeningPort> results, CancellationToken ct)
    {
        try
        {
            int bufferSize = 0;
            uint ret = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (ret != ERROR_INSUFFICIENT_BUFFER) return;

            IntPtr pTable = Marshal.AllocHGlobal(bufferSize);
            try
            {
                ret = GetExtendedTcpTable(pTable, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                if (ret != NO_ERROR) return;

                int numEntries = Marshal.ReadInt32(pTable);
                var rowPtr = IntPtr.Add(pTable, Marshal.SizeOf<uint>());

                for (int i = 0; i < numEntries; i++)
                {
                    if (ct.IsCancellationRequested) break;

                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    if (row.dwState == MIB_TCP_STATE_LISTEN)
                    {
                        var localAddr = new IPAddress(row.dwLocalAddr);
                        int localPort = ConvertPort(row.dwLocalPort);
                        int pid = (int)row.dwOwningPid;
                        var (procName, procPath, publisher, services, serviceLabel) = ResolveProcess(pid);
                        var scope = ClassifyScope(localAddr);
                        var classification = PortClassifier.Classify("TCP", localPort, scope);
                        var exposure = EvaluateExposure(scope, "TCP", localPort, procPath);

                        results.Add(new ListeningPort(
                            Protocol: "TCP",
                            LocalAddress: localAddr,
                            Port: localPort,
                            Pid: pid,
                            AppPath: procPath,
                            AppName: procName,
                            Publisher: publisher,
                            Services: services,
                            Scope: scope,
                            Exposure: exposure,
                            ServiceLabel: serviceLabel,
                            Hint: classification.Hint,
                            ServiceName: classification.ServiceName));
                    }

                    rowPtr = IntPtr.Add(rowPtr, Marshal.SizeOf<MIB_TCPROW_OWNER_PID>());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pTable);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ListeningPortService] GetTcp4Listeners failed: {ex.Message}");
        }
    }

    private void GetTcp6Listeners(List<ListeningPort> results, CancellationToken ct)
    {
        try
        {
            int bufferSize = 0;
            uint ret = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0);
            if (ret != ERROR_INSUFFICIENT_BUFFER) return;

            IntPtr pTable = Marshal.AllocHGlobal(bufferSize);
            try
            {
                ret = GetExtendedTcpTable(pTable, ref bufferSize, false, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0);
                if (ret != NO_ERROR) return;

                int numEntries = Marshal.ReadInt32(pTable);
                var rowPtr = IntPtr.Add(pTable, Marshal.SizeOf<uint>());

                for (int i = 0; i < numEntries; i++)
                {
                    if (ct.IsCancellationRequested) break;

                    var row = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(rowPtr);
                    if (row.dwState == MIB_TCP_STATE_LISTEN)
                    {
                        var localAddr = new IPAddress(row.ucLocalAddr, row.dwLocalScopeId);
                        int localPort = ConvertPort(row.dwLocalPort);
                        int pid = (int)row.dwOwningPid;
                        var (procName, procPath, publisher, services, serviceLabel) = ResolveProcess(pid);
                        var scope = ClassifyScope(localAddr);
                        var classification = PortClassifier.Classify("TCP", localPort, scope);
                        var exposure = EvaluateExposure(scope, "TCP", localPort, procPath);

                        results.Add(new ListeningPort(
                            Protocol: "TCP",
                            LocalAddress: localAddr,
                            Port: localPort,
                            Pid: pid,
                            AppPath: procPath,
                            AppName: procName,
                            Publisher: publisher,
                            Services: services,
                            Scope: scope,
                            Exposure: exposure,
                            ServiceLabel: serviceLabel,
                            Hint: classification.Hint,
                            ServiceName: classification.ServiceName));
                    }

                    rowPtr = IntPtr.Add(rowPtr, Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pTable);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ListeningPortService] GetTcp6Listeners failed: {ex.Message}");
        }
    }

    private void GetUdp4Sockets(List<ListeningPort> results, CancellationToken ct)
    {
        try
        {
            int bufferSize = 0;
            uint ret = GetExtendedUdpTable(IntPtr.Zero, ref bufferSize, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
            if (ret != ERROR_INSUFFICIENT_BUFFER) return;

            IntPtr pTable = Marshal.AllocHGlobal(bufferSize);
            try
            {
                ret = GetExtendedUdpTable(pTable, ref bufferSize, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
                if (ret != NO_ERROR) return;

                int numEntries = Marshal.ReadInt32(pTable);
                var rowPtr = IntPtr.Add(pTable, Marshal.SizeOf<uint>());

                for (int i = 0; i < numEntries; i++)
                {
                    if (ct.IsCancellationRequested) break;

                    var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rowPtr);
                    var localAddr = new IPAddress(row.dwLocalAddr);
                    int localPort = ConvertPort(row.dwLocalPort);
                    int pid = (int)row.dwOwningPid;
                    var (procName, procPath, publisher, services, serviceLabel) = ResolveProcess(pid);
                    var scope = ClassifyScope(localAddr);
                    var classification = PortClassifier.Classify("UDP", localPort, scope);
                    var exposure = EvaluateExposure(scope, "UDP", localPort, procPath);

                    results.Add(new ListeningPort(
                        Protocol: "UDP",
                        LocalAddress: localAddr,
                        Port: localPort,
                        Pid: pid,
                        AppPath: procPath,
                        AppName: procName,
                        Publisher: publisher,
                        Services: services,
                        Scope: scope,
                        Exposure: exposure,
                        ServiceLabel: serviceLabel,
                        Hint: classification.Hint,
                        ServiceName: classification.ServiceName));

                    rowPtr = IntPtr.Add(rowPtr, Marshal.SizeOf<MIB_UDPROW_OWNER_PID>());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pTable);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ListeningPortService] GetUdp4Sockets failed: {ex.Message}");
        }
    }

    private void GetUdp6Sockets(List<ListeningPort> results, CancellationToken ct)
    {
        try
        {
            int bufferSize = 0;
            uint ret = GetExtendedUdpTable(IntPtr.Zero, ref bufferSize, false, AF_INET6, UDP_TABLE_OWNER_PID, 0);
            if (ret != ERROR_INSUFFICIENT_BUFFER) return;

            IntPtr pTable = Marshal.AllocHGlobal(bufferSize);
            try
            {
                ret = GetExtendedUdpTable(pTable, ref bufferSize, false, AF_INET6, UDP_TABLE_OWNER_PID, 0);
                if (ret != NO_ERROR) return;

                int numEntries = Marshal.ReadInt32(pTable);
                var rowPtr = IntPtr.Add(pTable, Marshal.SizeOf<uint>());

                for (int i = 0; i < numEntries; i++)
                {
                    if (ct.IsCancellationRequested) break;

                    var row = Marshal.PtrToStructure<MIB_UDP6ROW_OWNER_PID>(rowPtr);
                    var localAddr = new IPAddress(row.ucLocalAddr, row.dwLocalScopeId);
                    int localPort = ConvertPort(row.dwLocalPort);
                    int pid = (int)row.dwOwningPid;
                    var (procName, procPath, publisher, services, serviceLabel) = ResolveProcess(pid);
                    var scope = ClassifyScope(localAddr);
                    var classification = PortClassifier.Classify("UDP", localPort, scope);
                    var exposure = EvaluateExposure(scope, "UDP", localPort, procPath);

                    results.Add(new ListeningPort(
                        Protocol: "UDP",
                        LocalAddress: localAddr,
                        Port: localPort,
                        Pid: pid,
                        AppPath: procPath,
                        AppName: procName,
                        Publisher: publisher,
                        Services: services,
                        Scope: scope,
                        Exposure: exposure,
                        ServiceLabel: serviceLabel,
                        Hint: classification.Hint,
                        ServiceName: classification.ServiceName));

                    rowPtr = IntPtr.Add(rowPtr, Marshal.SizeOf<MIB_UDP6ROW_OWNER_PID>());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pTable);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ListeningPortService] GetUdp6Sockets failed: {ex.Message}");
        }
    }

    public static PortScope ClassifyScope(IPAddress address)
    {
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return PortScope.AllInterfaces;

        if (IPAddress.IsLoopback(address))
            return PortScope.LocalhostOnly;

        return PortScope.LocalNetwork;
    }

    private static int ConvertPort(uint dwPort)
    {
        return (int)(((dwPort & 0xFF) << 8) | ((dwPort >> 8) & 0xFF));
    }

    private static Dictionary<int, List<string>>? _cachedServiceMap;
    private static DateTime _lastServiceMapCheck = DateTime.MinValue;

    private static Dictionary<int, List<string>> GetServiceMap()
    {
        if (_cachedServiceMap != null && (DateTime.Now - _lastServiceMapCheck).TotalSeconds < 30)
            return _cachedServiceMap;

        var map = new Dictionary<int, List<string>>();
        _lastServiceMapCheck = DateTime.Now;

        IntPtr scm = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;

        try
        {
            scm = OpenSCManager(null, null, SC_MANAGER_ENUMERATE_SERVICE);
            if (scm != IntPtr.Zero)
            {
                uint bytesNeeded = 0;
                uint servicesReturned = 0;
                uint resumeHandle = 0;

                EnumServicesStatusEx(
                    scm,
                    SC_ENUM_PROCESS_INFO,
                    SERVICE_WIN32,
                    SERVICE_STATE_ALL,
                    IntPtr.Zero,
                    0,
                    out bytesNeeded,
                    out servicesReturned,
                    ref resumeHandle,
                    null);

                if (bytesNeeded > 0)
                {
                    buffer = Marshal.AllocHGlobal((int)bytesNeeded);
                    if (EnumServicesStatusEx(
                        scm,
                        SC_ENUM_PROCESS_INFO,
                        SERVICE_WIN32,
                        SERVICE_STATE_ALL,
                        buffer,
                        bytesNeeded,
                        out _,
                        out servicesReturned,
                        ref resumeHandle,
                        null))
                    {
                        int structSize = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS>();
                        IntPtr current = buffer;

                        for (int i = 0; i < servicesReturned; i++)
                        {
                            var ssp = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESS>(current);
                            int sPid = (int)ssp.dwProcessId;

                            if (sPid > 0)
                            {
                                string? svcName = Marshal.PtrToStringUni(ssp.lpServiceName);
                                string? dispName = Marshal.PtrToStringUni(ssp.lpDisplayName);

                                string label = !string.IsNullOrEmpty(dispName) && dispName != svcName
                                    ? $"{dispName} ({svcName})"
                                    : svcName ?? string.Empty;

                                if (!string.IsNullOrEmpty(label))
                                {
                                    if (!map.TryGetValue(sPid, out var list))
                                    {
                                        list = new List<string>();
                                        map[sPid] = list;
                                    }
                                    list.Add(label);
                                }
                            }

                            current = IntPtr.Add(current, structSize);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ListeningPortService] GetServiceMap failed: {ex.Message}");
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (scm != IntPtr.Zero) CloseServiceHandle(scm);
        }

        return _cachedServiceMap = map;
    }

    private (string Name, string? Path, string? Publisher, IReadOnlyList<string> Services, string? ServiceLabel) ResolveProcess(int pid)
    {
        if (pid <= 0) return ("System Idle", null, null, Array.Empty<string>(), null);
        if (pid == 4) return ("System", null, "Microsoft Corporation", Array.Empty<string>(), null);

        if (_processInfoCache.TryGetValue(pid, out var cached))
            return cached;

        string name = $"PID {pid}";
        string? path = null;
        string? publisher = null;
        var services = new List<string>();
        string? serviceLabel = null;

        try
        {
            using var proc = Process.GetProcessById(pid);
            name = proc.ProcessName;
            try
            {
                path = proc.MainModule?.FileName;
            }
            catch { }
        }
        catch
        {
            name = "System / protected process";
        }

        // Get Publisher from FileVersionInfo if path is accessible
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                publisher = vi.CompanyName;
            }
            catch { }
        }

        // Check services for this PID (e.g. svchost)
        var serviceMap = GetServiceMap();
        if (serviceMap.TryGetValue(pid, out var matchedServices) && matchedServices.Count > 0)
        {
            services.AddRange(matchedServices);
            serviceLabel = string.Join(", ", matchedServices);
        }

        var entry = (name, path, publisher, (IReadOnlyList<string>)services, serviceLabel);
        _processInfoCache[pid] = entry;
        return entry;
    }

    #region Firewall Exposure Estimation

    private class CachedFwRules
    {
        public bool IsEnabled = true;
        public HashSet<int> AllowedTcpPorts = new();
        public HashSet<int> AllowedUdpPorts = new();
        public HashSet<int> BlockedTcpPorts = new();
        public HashSet<int> BlockedUdpPorts = new();
        public HashSet<string> AllowedApps = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> BlockedApps = new(StringComparer.OrdinalIgnoreCase);
    }

    private static CachedFwRules? _cachedFw;
    private static DateTime _lastFwCheck = DateTime.MinValue;

    public static Func<AppSettings?>? SettingsProvider { get; set; }

    public static FirewallExposure EvaluateExposure(PortScope scope, string protocol, int port, string? appPath)
    {
        // 1. Loopback addresses are not filtered by Windows Firewall
        if (scope == PortScope.LocalhostOnly)
            return FirewallExposure.LocalhostUnfiltered;

        // 2. Check AppLock's rules
        try
        {
            var settings = SettingsProvider?.Invoke();
            if (settings != null)
            {
                // AppLock port rules
                if (settings.ListeningPorts?.PortRules != null &&
                    settings.ListeningPorts.PortRules.Any(r =>
                        string.Equals(r.Protocol, protocol, StringComparison.OrdinalIgnoreCase) &&
                        IsPortMatch(r.LocalPorts, port)))
                {
                    return FirewallExposure.BlockedByAppLock;
                }

                // AppLock app inbound rules
                if (!string.IsNullOrEmpty(appPath))
                {
                    var appRule = settings.NetworkRules?.FirstOrDefault(r =>
                        string.Equals(r.ExePath, appPath, StringComparison.OrdinalIgnoreCase));
                    if (appRule != null && appRule.BlockInbound)
                    {
                        return FirewallExposure.BlockedByAppLock;
                    }
                }
            }
        }
        catch { }

        // 3. Check Windows Firewall state via cached rules
        var fwStatus = GetCachedFirewallRules();
        if (!fwStatus.IsEnabled)
            return FirewallExposure.FirewallOff;

        bool isTcp = protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase);
        bool isUdp = protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase);

        if (isTcp)
        {
            if (fwStatus.BlockedTcpPorts.Contains(port)) return FirewallExposure.BlockedByDefault;
            if (fwStatus.AllowedTcpPorts.Contains(port)) return FirewallExposure.AllowedByRule;
        }
        else if (isUdp)
        {
            if (fwStatus.BlockedUdpPorts.Contains(port)) return FirewallExposure.BlockedByDefault;
            if (fwStatus.AllowedUdpPorts.Contains(port)) return FirewallExposure.AllowedByRule;
        }

        if (!string.IsNullOrEmpty(appPath))
        {
            if (fwStatus.BlockedApps.Contains(appPath)) return FirewallExposure.BlockedByDefault;
            if (fwStatus.AllowedApps.Contains(appPath)) return FirewallExposure.AllowedByRule;
        }

        // On Windows, the default inbound policy when no allow rule matches is Block
        return FirewallExposure.BlockedByDefault;
    }

    private static CachedFwRules GetCachedFirewallRules()
    {
        if (_cachedFw != null && (DateTime.Now - _lastFwCheck).TotalSeconds < 30)
            return _cachedFw;

        var result = new CachedFwRules();
        _lastFwCheck = DateTime.Now;

        try
        {
            Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType == null) return _cachedFw = result;

            dynamic? policy = Activator.CreateInstance(policyType);
            if (policy == null) return _cachedFw = result;

            int activeProfiles = 7;
            try { activeProfiles = policy.CurrentProfileTypes; } catch { }

            bool enabled = false;
            try
            {
                if ((activeProfiles & 1) != 0 && policy.FirewallEnabled(1)) enabled = true;
                if ((activeProfiles & 2) != 0 && policy.FirewallEnabled(2)) enabled = true;
                if ((activeProfiles & 4) != 0 && policy.FirewallEnabled(4)) enabled = true;
            }
            catch
            {
                enabled = true; // assume enabled on query failure
            }

            result.IsEnabled = enabled;
            if (!enabled) return _cachedFw = result;

            dynamic rules = policy.Rules;
            foreach (dynamic rule in rules)
            {
                try
                {
                    if (!rule.Enabled) continue;
                    if (rule.Direction != 1) continue; // 1 = Inbound

                    int action = rule.Action; // 1 = Allow, 0 = Block
                    int ruleProto = rule.Protocol; // 6 = TCP, 17 = UDP, 256 = Any
                    string? localPorts = rule.LocalPorts;
                    string? app = rule.ApplicationName;

                    if (!string.IsNullOrEmpty(app))
                    {
                        if (action == 1) result.AllowedApps.Add(app);
                        else result.BlockedApps.Add(app);
                    }

                    if (!string.IsNullOrEmpty(localPorts))
                    {
                        ParsePortRanges(localPorts, p =>
                        {
                            if (ruleProto == 6 || ruleProto == 256)
                            {
                                if (action == 1) result.AllowedTcpPorts.Add(p);
                                else result.BlockedTcpPorts.Add(p);
                            }
                            if (ruleProto == 17 || ruleProto == 256)
                            {
                                if (action == 1) result.AllowedUdpPorts.Add(p);
                                else result.BlockedUdpPorts.Add(p);
                            }
                        });
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ListeningPortService] GetCachedFirewallRules failed: {ex.Message}");
        }

        return _cachedFw = result;
    }

    private static void ParsePortRanges(string portsStr, Action<int> onPort)
    {
        if (string.IsNullOrWhiteSpace(portsStr) || portsStr == "*") return;
        var parts = portsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var dash = part.IndexOf('-');
            if (dash > 0)
            {
                if (int.TryParse(part[..dash], out int start) && int.TryParse(part[(dash + 1)..], out int end))
                {
                    int max = Math.Min(end, start + 1000); // cap range to prevent massive loops
                    for (int p = start; p <= max; p++)
                        onPort(p);
                }
            }
            else if (int.TryParse(part, out int port))
            {
                onPort(port);
            }
        }
    }

    private static bool IsPortMatch(string? rulePorts, int targetPort)
    {
        if (string.IsNullOrWhiteSpace(rulePorts)) return false;
        if (rulePorts == "*") return true;
        var parts = rulePorts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var dash = part.IndexOf('-');
            if (dash > 0)
            {
                if (int.TryParse(part[..dash], out int start) && int.TryParse(part[(dash + 1)..], out int end))
                {
                    if (targetPort >= start && targetPort <= end) return true;
                }
            }
            else if (int.TryParse(part, out int p) && p == targetPort)
            {
                return true;
            }
        }
        return false;
    }

    #endregion
}
