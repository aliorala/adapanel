using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>Volatile VSS filesystem snapshot. No writers/application-consistency claim.</summary>
public sealed class WindowsBackupSnapshotRuntime : IBackupSnapshotRuntime
{
    // Diskshadow's script parser expects native single-byte commands, not UTF-16.
    // Keep paths out of its source entirely: working directory + fixed relative helper.
    internal static byte[] BuildNativeScript(IReadOnlyList<string> volumes)
    {
        if (volumes.Count == 0 || volumes.Any(volume => volume.Length != 3 ||
            !char.IsAsciiLetter(volume[0]) || volume[1] != ':' || volume[2] != '\\'))
            throw new ArgumentException("Only local drive roots may enter the native VSS script.", nameof(volumes));
        var script = new StringBuilder("SET CONTEXT VOLATILE NOWRITERS\r\nSET METADATA .\\metadata.cab\r\n");
        for (var i = 0; i < volumes.Count; i++) script.Append($"ADD VOLUME {volumes[i]} ALIAS APVSS{i}\r\n");
        script.Append("CREATE\r\nEXEC .\\hold.cmd\r\nEXIT\r\n");
        return Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(script.ToString());
    }

    internal const string NativeHelperCommand = "@echo off\r\n\"%APVSS_POWERSHELL%\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%~dp0hold.ps1\"\r\nexit /b %errorlevel%\r\n";

    internal static string ValidateRoot(string root)
    {
        var full = Path.GetFullPath(root);
        if (full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\')
            throw new IOException("Filesystem snapshots require a local drive path; UNC/device paths are unsupported.");
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        for (var info = new DirectoryInfo(full); info is not null; info = info.Parent)
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Snapshot source roots may not traverse reparse points.");
        return Path.TrimEndingDirectorySeparator(full);
    }

    public async Task<IBackupSnapshotLease> CaptureAsync(IReadOnlyCollection<string> roots, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("VSS requires Windows Server.");
        cancellationToken.ThrowIfCancellationRequested();
        var originals = roots.Select(ValidateRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (originals.Length == 0) throw new ArgumentException("No snapshot roots supplied.", nameof(roots));
        var volumes = originals.Select(Path.GetPathRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var directory = Path.Combine(Path.GetTempPath(), "AdaPanel-vss-" + Guid.NewGuid().ToString("N"));
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        var owner = WindowsIdentity.GetCurrent().User ?? throw new IOException("No process identity.");
        security.SetOwner(owner);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).Create(security);
        var lease = new Lease(directory, originals, volumes!);
        try
        {
            using var parent = Process.GetCurrentProcess();
            var config = new { ParentId = Environment.ProcessId, ParentStart = parent.StartTime.ToUniversalTime().Ticks, Count = volumes.Length };
            await File.WriteAllTextAsync(Path.Combine(directory, "config.json"), JsonSerializer.Serialize(config), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, "hold.ps1"), Helper, new UTF8Encoding(true), cancellationToken);
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            // Diskshadow EXEC officially takes one script path, not an arbitrary argument list.
            // Its own shadow aliases are inherited by this fixed helper. No site path enters CMD.
            await File.WriteAllTextAsync(Path.Combine(directory, "hold.cmd"),
                NativeHelperCommand, Encoding.ASCII, cancellationToken);
            var scriptPath = Path.Combine(directory, "capture.dsh");
            await File.WriteAllBytesAsync(scriptPath, BuildNativeScript(volumes!), cancellationToken);
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "diskshadow.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
            start.ArgumentList.Add("/s");
            start.ArgumentList.Add(scriptPath);
            start.Environment["APVSS_POWERSHELL"] = powershell;
            lease.Process = Process.Start(start) ?? throw new IOException("Cannot start Diskshadow.");
            // Drain concurrently, retaining at most 4 KiB per stream for actionable failures.
            lease.Output = DrainAsync(lease.Process.StandardOutput);
            lease.Error = DrainAsync(lease.Process.StandardError);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            while (!File.Exists(Path.Combine(directory, "ready.json")))
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (lease.Process.HasExited) throw new IOException($"VSS capture failed (exit {lease.Process.ExitCode}); no live-copy fallback was used. {lease.DiagnosticTail()}");
                await Task.Delay(100, timeout.Token);
            }
            var ready = JsonSerializer.Deserialize<Ready>(await File.ReadAllTextAsync(Path.Combine(directory, "ready.json"), timeout.Token))
                ?? throw new IOException("Invalid VSS capture result.");
            lease.Initialize(ready);
            return lease;
        }
        catch (Exception error)
        {
            try { await lease.DisposeAsync(); }
            catch (Exception cleanupError) { error.Data["VssCleanupError"] = cleanupError.Message; }
            throw;
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        var tail = string.Empty;
        int read;
        try
        {
            while ((read = await reader.ReadAsync(buffer)) > 0)
            {
                tail += new string(buffer, 0, read);
                if (tail.Length > 4096) tail = tail[^4096..];
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        return tail;
    }

    private sealed record Ready(DateTime CapturedAtUtc, string[] Devices);

    private sealed class Lease(string directory, string[] originals, string[] volumes) : IBackupSnapshotLease
    {
        public Process? Process { get; set; }
        public Task<string> Output { get; set; } = Task.FromResult(string.Empty);
        public Task<string> Error { get; set; } = Task.FromResult(string.Empty);
        private Dictionary<string, string> roots = new(StringComparer.OrdinalIgnoreCase);
        private int disposed;
        public DateTime CapturedAtUtc { get; private set; }
        public string DiagnosticTail() =>
            (Output.IsCompletedSuccessfully ? Output.Result : "stdout pending") + " " +
            (Error.IsCompletedSuccessfully ? Error.Result : "stderr pending");
        public void Initialize(Ready ready)
        {
            if (ready.Devices.Length != volumes.Length || ready.CapturedAtUtc == default ||
                ready.Devices.Any(device => !System.Text.RegularExpressions.Regex.IsMatch(device,
                    @"^\\\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy[0-9]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
                throw new IOException("VSS returned an invalid snapshot map.");
            CapturedAtUtc = ready.CapturedAtUtc;
            roots = originals.ToDictionary(root => root, root => ready.Devices[Array.FindIndex(volumes,
                volume => string.Equals(volume, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))] + "\\" + root[3..], StringComparer.OrdinalIgnoreCase);
        }
        public string ResolveRoot(string originalRoot)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (Process is null || Process.HasExited) throw new IOException("VSS lease ended before file copying completed.");
            return roots.TryGetValue(Path.TrimEndingDirectorySeparator(Path.GetFullPath(originalRoot)), out var root)
                ? root : throw new IOException("Path was not part of this snapshot set.");
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try
            {
                Exception? releaseError = null;
                try { await File.WriteAllTextAsync(Path.Combine(directory, "release"), string.Empty); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { releaseError = error; }
                if (Process is { } process)
                {
                    if (releaseError is not null && !process.HasExited) process.Kill(entireProcessTree: true);
                    using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    try { await process.WaitForExitAsync(grace.Token); }
                    catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await process.WaitForExitAsync(killed.Token);
                    try { await Task.WhenAll(Output, Error).WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch (TimeoutException) { /* A descendant must never hold backup cleanup indefinitely. */ }
                }
                if (releaseError is not null) throw new IOException("VSS release marker could not be written; own native process was terminated.", releaseError);
            }
            finally
            {
                Process?.Dispose();
                // Only files created by this lease, never any shadow-copy deletion command.
                foreach (var name in new[] { "config.json", "hold.ps1", "hold.cmd", "capture.dsh", "metadata.cab", "ready.json", "ready.tmp", "release" })
                    if (File.Exists(Path.Combine(directory, name))) File.Delete(Path.Combine(directory, name));
                if (Directory.Exists(directory)) Directory.Delete(directory, false);
            }
        }
    }

    private const string Helper = """
        $ErrorActionPreference = 'Stop'
        $cfg = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'config.json') -Raw | ConvertFrom-Json
        $devices = @()
        $set = $null
        $captured = $null
        for ($i = 0; $i -lt $cfg.Count; $i++) {
            $raw = [Environment]::GetEnvironmentVariable("APVSS$i")
            $id = [Guid]::Parse($raw)
            $copy = Get-CimInstance Win32_ShadowCopy -Filter ("ID='{0}'" -f $id.ToString('B'))
            if ($null -eq $copy) { throw 'Own shadow copy not found' }
            if ($null -eq $set) { $set = $copy.SetID } elseif ($set -ne $copy.SetID) { throw 'Mixed snapshot set' }
            if ($null -eq $captured) { $captured = $copy.InstallDate.ToUniversalTime().ToString('o') }
            $devices += $copy.DeviceObject
        }
        if ($devices.Count -ne $cfg.Count) { throw 'Incomplete snapshot set' }
        @{ CapturedAtUtc = $captured; Devices = @($devices) } |
            ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'ready.tmp') -Encoding UTF8
        Move-Item -LiteralPath (Join-Path $PSScriptRoot 'ready.tmp') -Destination (Join-Path $PSScriptRoot 'ready.json')
        while (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'release'))) {
            try {
                $p = Get-Process -Id $cfg.ParentId -ErrorAction Stop
                if ($p.StartTime.ToUniversalTime().Ticks -ne $cfg.ParentStart) { break }
            } catch { break }
            Start-Sleep -Milliseconds 200
        }
        """;
}
