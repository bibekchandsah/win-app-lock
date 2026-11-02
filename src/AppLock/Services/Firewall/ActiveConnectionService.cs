using System;
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

public class ActiveConnectionService
{
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
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;
    private const uint NO_ERROR = 0;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;
    public async Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken ct = default)
    {
        var connections = new List<ActiveConnection>();

        await Task.Run(() =>
        {
            if (ct.IsCancellationRequested) return;

            // Get TCP connections
            var tcpConnections = GetTcpConnections(ct);
            connections.AddRange(tcpConnections);

            if (ct.IsCancellationRequested) return;

            // Get UDP connections
            var udpConnections = GetUdpConnections(ct);
            connections.AddRange(udpConnections);

        }, ct);

        return connections;
    }

    private List<ActiveConnection> GetTcpConnections(CancellationToken ct)
    {
        var connections = new List<ActiveConnection>();

        try
        {
            int bufferSize = 0;
            uint result = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);

            if (result != ERROR_INSUFFICIENT_BUFFER)
                return connections;

            IntPtr tcpTablePtr = Marshal.AllocHGlobal(bufferSize);
            try
            {
                result = GetExtendedTcpTable(tcpTablePtr, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);

                if (result != NO_ERROR)
                    return connections;

                var table = Marshal.PtrToStructure<MIB_TCPTABLE_OWNER_PID>(tcpTablePtr);
                var rowPtr = IntPtr.Add(tcpTablePtr, Marshal.SizeOf<uint>());

                for (int i = 0; i < table.dwNumEntries; i++)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    
                    var processInfo = GetProcessInfo((int)row.dwOwningPid);
                    if (processInfo != null)
                    {
                        connections.Add(new ActiveConnection
                        {
                            ProcessId = (int)row.dwOwningPid,
                            ProcessName = processInfo.ProcessName,
                            ProcessPath = processInfo.ProcessPath,
                            Protocol = "TCP",
                            LocalAddress = new IPAddress(row.dwLocalAddr).ToString(),
                            LocalPort = (int)((row.dwLocalPort >> 8) | ((row.dwLocalPort << 8) & 0xFF00)),
                            RemoteAddress = new IPAddress(row.dwRemoteAddr).ToString(),
                            RemotePort = (int)((row.dwRemotePort >> 8) | ((row.dwRemotePort << 8) & 0xFF00)),
                            State = GetTcpState(row.dwState),
                            IsSystemProcess = processInfo.IsSystemProcess,
                            CanBlock = !processInfo.IsSystemProcess
                        });
                    }

                    rowPtr = IntPtr.Add(rowPtr, Marshal.SizeOf<MIB_TCPROW_OWNER_PID>());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(tcpTablePtr);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error getting TCP connections: {ex.Message}");
        }

        return connections;
    }

    private List<ActiveConnection> GetUdpConnections(CancellationToken ct)
    {
        var connections = new List<ActiveConnection>();

        try
        {
            int bufferSize = 0;
            uint result = GetExtendedUdpTable(IntPtr.Zero, ref bufferSize, false, AF_INET, UDP_TABLE_OWNER_PID, 0);

            if (result != ERROR_INSUFFICIENT_BUFFER)
                return connections;

            IntPtr udpTablePtr = Marshal.AllocHGlobal(bufferSize);
            try
            {
                result = GetExtendedUdpTable(udpTablePtr, ref bufferSize, false, AF_INET, UDP_TABLE_OWNER_PID, 0);

                if (result != NO_ERROR)
                    return connections;

                var table = Marshal.PtrToStructure<MIB_UDPTABLE_OWNER_PID>(udpTablePtr);
                var rowPtr = IntPtr.Add(udpTablePtr, Marshal.SizeOf<uint>());

                for (int i = 0; i < table.dwNumEntries; i++)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rowPtr);
                    
                    var processInfo = GetProcessInfo((int)row.dwOwningPid);
                    if (processInfo != null)
                    {
                        connections.Add(new ActiveConnection
                        {
                            ProcessId = (int)row.dwOwningPid,
                            ProcessName = processInfo.ProcessName,
                            ProcessPath = processInfo.ProcessPath,
                            Protocol = "UDP",
                            LocalAddress = new IPAddress(row.dwLocalAddr).ToString(),
                            LocalPort = (int)((row.dwLocalPort >> 8) | ((row.dwLocalPort << 8) & 0xFF00)),
                            RemoteAddress = "",
                            RemotePort = 0,
                            State = "LISTENING",
                            IsSystemProcess = processInfo.IsSystemProcess,
                            CanBlock = !processInfo.IsSystemProcess
                        });
                    }

                    rowPtr = IntPtr.Add(rowPtr, Marshal.SizeOf<MIB_UDPROW_OWNER_PID>());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(udpTablePtr);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error getting UDP connections: {ex.Message}");
        }

        return connections;
    }

    private ProcessInfo? GetProcessInfo(int pid)
    {
        try
        {
            if (pid == 0 || pid == 4)
            {
                return new ProcessInfo
                {
                    ProcessName = pid == 0 ? "System Idle" : "System",
                    ProcessPath = "System",
                    IsSystemProcess = true
                };
            }

            var process = Process.GetProcessById(pid);
            string path = "";
            
            try
            {
                path = process.MainModule?.FileName ?? "";
            }
            catch
            {
                // Access denied for some system processes
                path = process.ProcessName;
            }

            bool isSystem = InstalledAppsService.IsSystemCritical(path) || 
                           path.Equals("System", StringComparison.OrdinalIgnoreCase);

            return new ProcessInfo
            {
                ProcessName = process.ProcessName,
                ProcessPath = path,
                IsSystemProcess = isSystem
            };
        }
        catch
        {
            return null;
        }
    }

    private string GetTcpState(uint state)
    {
        return state switch
        {
            1 => "CLOSED",
            2 => "LISTEN",
            3 => "SYN_SENT",
            4 => "SYN_RCVD",
            5 => "ESTABLISHED",
            6 => "FIN_WAIT1",
            7 => "FIN_WAIT2",
            8 => "CLOSE_WAIT",
            9 => "CLOSING",
            10 => "LAST_ACK",
            11 => "TIME_WAIT",
            12 => "DELETE_TCB",
            _ => "UNKNOWN"
        };
    }
}

internal class ProcessInfo
{
    public string ProcessName { get; set; } = "";
    public string ProcessPath { get; set; } = "";
    public bool IsSystemProcess { get; set; }
}

// IP Helper API structures
[StructLayout(LayoutKind.Sequential)]
internal struct MIB_TCPTABLE_OWNER_PID
{
    public uint dwNumEntries;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MIB_TCPROW_OWNER_PID
{
    public uint dwState;
    public uint dwLocalAddr;
    public uint dwLocalPort;
    public uint dwRemoteAddr;
    public uint dwRemotePort;
    public uint dwOwningPid;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MIB_UDPTABLE_OWNER_PID
{
    public uint dwNumEntries;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MIB_UDPROW_OWNER_PID
{
    public uint dwLocalAddr;
    public uint dwLocalPort;
    public uint dwOwningPid;
}
