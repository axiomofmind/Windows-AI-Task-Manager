using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Management;
using AITaskManager.Application;

namespace AITaskManager.Infrastructure.Windows;

public sealed class WindowsEndpointProcessResolver : ILocalEndpointProcessResolver
{
    private const int AddressFamilyInet = 2;
    private const int ErrorInsufficientBuffer = 122;
    private const int TcpTableOwnerPidListener = 3;

    public LocalEndpointProcess? Resolve(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!OperatingSystem.IsWindows() || !IsLoopback(endpoint.Host))
        {
            return null;
        }

        var processId = FindListeningProcessId(endpoint.Port);
        if (processId is null)
        {
            return null;
        }

        return ReadProcess(processId.Value);
    }

    public IReadOnlyList<LocalRuntimeListener> DiscoverListeners(IReadOnlyCollection<string> executableNames)
    {
        ArgumentNullException.ThrowIfNull(executableNames);
        if (!OperatingSystem.IsWindows() || executableNames.Count == 0)
        {
            return [];
        }

        var acceptedNames = executableNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => Path.GetFileNameWithoutExtension(name.Trim()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (acceptedNames.Count == 0)
        {
            return [];
        }

        var listeners = new List<LocalRuntimeListener>();
        foreach (var row in GetListeningRows()
            .GroupBy(row => (row.OwningProcessId, Port: DecodePort(row.LocalPort)))
            .Select(group => group.Key)
            .OrderBy(key => key.Port))
        {
            if (row.OwningProcessId > int.MaxValue || row.Port <= 0)
            {
                continue;
            }

            var process = ReadProcess((int)row.OwningProcessId, includeCommandLine: false);
            if (process is null || !acceptedNames.Contains(process.ExecutableName))
            {
                continue;
            }

            listeners.Add(new(
                new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), row.Port).Uri,
                process));
        }

        return listeners;
    }

    private static int? FindListeningProcessId(int port)
    {
        var owner = GetListeningRows().FirstOrDefault(row => DecodePort(row.LocalPort) == port).OwningProcessId;
        return owner is > 0 and <= int.MaxValue ? (int)owner : null;
    }

    private static IReadOnlyList<TcpRowOwnerPid> GetListeningRows()
    {
        var size = 0;
        var result = GetExtendedTcpTable(
            0,
            ref size,
            order: false,
            AddressFamilyInet,
            TcpTableOwnerPidListener,
            reserved: 0);
        if (result != ErrorInsufficientBuffer || size <= sizeof(uint))
        {
            return [];
        }

        var table = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(
                table,
                ref size,
                order: false,
                AddressFamilyInet,
                TcpTableOwnerPidListener,
                reserved: 0);
            if (result != 0)
            {
                return [];
            }

            var count = Marshal.ReadInt32(table);
            var rowPointer = table + sizeof(uint);
            var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
            var rows = new List<TcpRowOwnerPid>(count);
            for (var index = 0; index < count; index++)
            {
                rows.Add(Marshal.PtrToStructure<TcpRowOwnerPid>(rowPointer + (index * rowSize)));
            }

            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    private static LocalEndpointProcess? ReadProcess(int processId, bool includeCommandLine = true)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var executableName = process.ProcessName;
            DateTimeOffset? startedAt = null;
            try
            {
                startedAt = process.StartTime.ToUniversalTime();
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
            }

            return new LocalEndpointProcess(
                process.Id,
                executableName,
                startedAt,
                includeCommandLine ? ReadCommandLine(process.Id) : null);
        }
        catch (Exception exception) when (exception is ArgumentException or Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? ReadCommandLine(int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
            using var results = searcher.Get();
            foreach (ManagementObject process in results)
            {
                using (process)
                {
                    return process["CommandLine"] as string;
                }
            }
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or COMException)
        {
        }

        return null;
    }

    private static int DecodePort(uint encodedPort)
    {
        var bytes = BitConverter.GetBytes(encodedPort);
        return (bytes[0] << 8) | bytes[1];
    }

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TcpRowOwnerPid
    {
        public readonly uint State;
        public readonly uint LocalAddress;
        public readonly uint LocalPort;
        public readonly uint RemoteAddress;
        public readonly uint RemotePort;
        public readonly uint OwningProcessId;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        nint table,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        int tableClass,
        uint reserved);
}
