using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;

namespace AdaPanel.Infrastructure.Mail;

internal static class MailEnableInstallation
{
    internal static string? DiscoverRoot() => OperatingSystem.IsWindows()
        ? DiscoverRoot(ReadRegisteredServicePath) : null;

    internal static string? DiscoverRoot(Func<string?> registeredServicePath)
    {
        try { return ResolveRoot(registeredServicePath()); }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException
            or SecurityException or ArgumentException or NotSupportedException) { return null; }
    }

    private static string? ResolveRoot(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || imagePath.Any(char.IsControl)) return null;
        var command = Environment.ExpandEnvironmentVariables(imagePath).Trim();
        string executable;
        var quoted = command.StartsWith('"');
        if (quoted)
        {
            var end = command.IndexOf('"', 1);
            if (end < 2 || end + 1 < command.Length && !char.IsWhiteSpace(command[end + 1])) return null;
            executable = command[1..end];
        }
        else
        {
            // Match the actual connector filename, never the first incidental '.exe'
            // in a directory name or a different executable's command line.
            var match = Regex.Match(command, @"^(.+?[\\/]MESMTPC\.exe)(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return null;
            executable = match.Groups[1].Value;
            // SCM tries earlier whitespace-delimited executable candidates first when
            // an ImagePath is unquoted. Reject a path that could launch another binary.
            for (var index = 0; index < executable.Length; index++)
            {
                if (!char.IsWhiteSpace(executable[index])) continue;
                var prefix = executable[..index];
                if (File.Exists(prefix) || File.Exists(prefix + ".exe")) return null;
            }
        }
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"') || !File.Exists(executable)
            || !Path.GetFileName(executable).Equals("MESMTPC.exe", StringComparison.OrdinalIgnoreCase)) return null;
        var bin = Path.GetDirectoryName(Path.GetFullPath(executable));
        if (bin is null || !(Path.GetFileName(bin).Equals("BIN", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(bin).Equals("BIN64", StringComparison.OrdinalIgnoreCase))) return null;
        var root = Path.GetDirectoryName(bin);
        return root is not null && (File.Exists(Path.Combine(root, "BIN", "MEInstaller.exe"))
            || File.Exists(Path.Combine(root, "BIN64", "MEInstaller.exe"))) ? root : null;
    }

    private static string? ReadRegisteredServicePath()
    {
        const uint connect = 1, queryConfig = 1;
        var manager = OpenSCManager(null, null, connect);
        if (manager == IntPtr.Zero) return null;
        try
        {
            var service = OpenService(manager, "MESMTPC", queryConfig);
            if (service == IntPtr.Zero) return null;
            try
            {
                QueryServiceConfig(service, IntPtr.Zero, 0, out var needed);
                if (Marshal.GetLastWin32Error() != 122 || needed == 0 || needed > 8 * 1024) return null;
                var buffer = Marshal.AllocHGlobal(checked((int)needed));
                try
                {
                    if (!QueryServiceConfig(service, buffer, needed, out _)) return null;
                    var configuration = Marshal.PtrToStructure<ServiceConfiguration>(buffer);
                    return Marshal.PtrToStringUni(configuration.BinaryPathName);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfiguration
    {
        public uint ServiceType, StartType, ErrorControl;
        public IntPtr BinaryPathName, LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies, ServiceStartName, DisplayName;
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);
    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint desiredAccess);
    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(IntPtr service, IntPtr configuration, uint bufferSize, out uint bytesNeeded);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
