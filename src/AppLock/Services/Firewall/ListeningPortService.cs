using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

    private readonly ConcurrentDictionary<int, (string Name, string? Path)> _processCache = new();

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
                        var (procName, procPath) = ResolveProcess(pid);
                        var scope = ClassifyScope(localAddr);

                        results.Add(new ListeningPort(
                            Protocol: "TCP",
                            LocalAddress: localAddr,
                            Port: localPort,
                            Pid: pid,
                            AppPath: procPath,
                            AppName: procName,
                            Publisher: null,
                            Services: Array.Empty<string>(),
                            Scope: scope,
                            Exposure: FirewallExposure.Unknown,
                            ServiceLabel: null,
                            Hint: null));
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
                        var (procName, procPath) = ResolveProcess(pid);
                        var scope = ClassifyScope(localAddr);

                        results.Add(new ListeningPort(
                            Protocol: "TCP",
                            LocalAddress: localAddr,
                            Port: localPort,
                            Pid: pid,
                            AppPath: procPath,
                            AppName: procName,
                            Publisher: null,
                            Services: Array.Empty<string>(),
                            Scope: scope,
                            Exposure: FirewallExposure.Unknown,
                            ServiceLabel: null,
                            Hint: null));
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
                    var (procName, procPath) = ResolveProcess(pid);
                    var scope = ClassifyScope(localAddr);

                    results.Add(new ListeningPort(
                        Protocol: "UDP",
                        LocalAddress: localAddr,
                        Port: localPort,
                        Pid: pid,
                        AppPath: procPath,
                        AppName: procName,
                        Publisher: null,
                        Services: Array.Empty<string>(),
                        Scope: scope,
                        Exposure: FirewallExposure.Unknown,
                        ServiceLabel: null,
                        Hint: null));

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
                    var (procName, procPath) = ResolveProcess(pid);
                    var scope = ClassifyScope(localAddr);

                    results.Add(new ListeningPort(
                        Protocol: "UDP",
                        LocalAddress: localAddr,
                        Port: localPort,
                        Pid: pid,
                        AppPath: procPath,
                        AppName: procName,
                        Publisher: null,
                        Services: Array.Empty<string>(),
                        Scope: scope,
                        Exposure: FirewallExposure.Unknown,
                        ServiceLabel: null,
                        Hint: null));

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

    private (string Name, string? Path) ResolveProcess(int pid)
    {
        if (pid <= 0) return ("System Idle", null);
        if (pid == 4) return ("System", null);

        if (_processCache.TryGetValue(pid, out var cached))
            return cached;

        string name = $"PID {pid}";
        string? path = null;

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

        var entry = (name, path);
        _processCache[pid] = entry;
        return entry;
    }
}
