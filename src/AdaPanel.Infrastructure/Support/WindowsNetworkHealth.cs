using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AdaPanel.Infrastructure.Support;

/// <summary>Read-only SCM and IP Helper queries; no shell or CIM provider startup.</summary>
internal static class WindowsNetworkHealth
{
    internal sealed record ServiceState(string Status, uint ProcessId, string StartMode, bool Delayed);
    internal sealed record Listeners(string[] Tcp, string[] Udp);

    internal static ServiceState ReadService(string name)
    {
        var manager = OpenSCManager(null, null, 1); // SC_MANAGER_CONNECT
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(manager, name, 4); // SERVICE_QUERY_STATUS
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 1060) return new("NotInstalled", 0, "Unknown", false);
                throw new Win32Exception(error);
            }
            try
            {
                if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
                var startMode = key?.GetValue("Start") is int start ? start switch
                { 2 => "Auto", 3 => "Manual", 4 => "Disabled", _ => "Unknown" } : "Unknown";
                var state = status.State switch
                {
                    1 => "Stopped", 2 => "StartPending", 3 => "StopPending", 4 => "Running",
                    5 => "ContinuePending", 6 => "PausePending", 7 => "Paused", _ => "Unknown"
                };
                // SCM does not guarantee a valid PID while starting or stopping.
                return new(state, status.State is >= 4 and <= 7 ? status.ProcessId : 0,
                    startMode, key?.GetValue("DelayedAutostart") is int delayed && delayed == 1);
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    internal static string[] ReadLocalIPv4() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
        .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
        .Select(address => address.Address)
        .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
        .Select(address => address.ToString()).Where(address => !address.StartsWith("169.254.", StringComparison.Ordinal))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static Listeners ReadListeners(uint processId, int port)
    {
        if (processId == 0) return new([], []);
        var tcp = new List<string>();
        var udp = new List<string>();
        if (Socket.OSSupportsIPv4)
        {
            ReadTable<Tcp4>(true, 2, row =>
            {
                if (row.Pid == processId && row.State == 2 && Port(row.LocalPort) == port)
                    tcp.Add(new IPAddress(row.LocalAddress).ToString());
            });
            ReadTable<Udp4>(false, 2, row =>
            {
                if (row.Pid == processId && Port(row.LocalPort) == port)
                    udp.Add(new IPAddress(row.LocalAddress).ToString());
            });
        }
        if (Socket.OSSupportsIPv6)
        {
            ReadTable<Tcp6>(true, 23, row =>
            {
                if (row.Pid == processId && row.State == 2 && Port(row.LocalPort) == port)
                    tcp.Add(new IPAddress(row.LocalAddress, row.LocalScope).ToString());
            });
            ReadTable<Udp6>(false, 23, row =>
            {
                if (row.Pid == processId && Port(row.LocalPort) == port)
                    udp.Add(new IPAddress(row.LocalAddress, row.LocalScope).ToString());
            });
        }
        return new(tcp.Distinct().ToArray(), udp.Distinct().ToArray());
    }

    private static int Port(uint value) => (int)(((value & 255) << 8) | ((value >> 8) & 255));

    private static void ReadTable<T>(bool tcp, int family, Action<T> consume) where T : struct
    {
        int Query(IntPtr buffer, ref int size) => tcp
            ? GetExtendedTcpTable(buffer, ref size, false, family, 3, 0)
            : GetExtendedUdpTable(buffer, ref size, false, family, 1, 0);
        var size = 0;
        var error = Query(IntPtr.Zero, ref size);
        if (error != 0 && error != 122) throw new Win32Exception(error);
        // Socket changes can grow the table between the size query and the data query.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (size < 4 || size > 64 * 1024 * 1024) throw new IOException("Invalid network table size.");
            var capacity = size;
            var buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                error = Query(buffer, ref size);
                if (error == 122) continue;
                if (error != 0) throw new Win32Exception(error);
                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<T>();
                if (count < 0 || count > (capacity - 4) / rowSize) throw new IOException("Invalid network table row count.");
                for (var i = 0; i < count; i++) consume(Marshal.PtrToStructure<T>(buffer + 4 + i * rowSize));
                return;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("Network table changed repeatedly during inspection.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint Type, State, Accepted, ExitCode, SpecificExitCode, Checkpoint, WaitHint, ProcessId, Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Tcp4 { public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, Pid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Udp4 { public uint LocalAddress, LocalPort, Pid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Tcp6
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddress;
        public uint LocalScope, LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddress;
        public uint RemoteScope, RemotePort, State, Pid;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Udp6
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddress;
        public uint LocalScope, LocalPort, Pid;
    }

    // Windows SDK: winsvc.h SERVICE_STATUS_PROCESS; tcpmib.h/udpmib.h *_OWNER_PID.
    // https://learn.microsoft.com/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable
    // https://learn.microsoft.com/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedudptable
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatus status, int size, out int needed);
    [DllImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool ordered, int family, uint kind, int reserved);
    [DllImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetExtendedUdpTable(IntPtr table, ref int size, bool ordered, int family, uint kind, int reserved);
}
