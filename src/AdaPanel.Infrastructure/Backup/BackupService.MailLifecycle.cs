using System.IO.Compression;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.SiteBuilder;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Backup;

public sealed partial class BackupService
{
    private IMailBackupRuntime MailRuntime => mailBackupRuntime ?? DefaultMailRuntime.Instance;

    // All requests and service instances operate on the same server-wide mail store.
    private static readonly SemaphoreSlim MailOfflineGate = new(1, 1);

    private async Task<BackupRestoreWarningDto?> WithMailOfflineAsync(
        string installDirectory, Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        await MailOfflineGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exception? original = null;
            BackupRestoreWarningDto? warning = null;
            try
            {
                // Stop can take effect and then throw, so it belongs inside the protected region.
                await MailRuntime.StopAsync(installDirectory, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await operation(cancellationToken);
            }
            catch (Exception exception) { original = exception; throw; }
            finally
            {
                using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { warning = await MailRuntime.StartAsync(recovery.Token); }
                catch (Exception exception)
                {
                    warning = new BackupRestoreWarningDto
                    {
                        MessageKey = "backup.mail_service_start_failed", Target = StalwartTaskName,
                        Detail = exception.Message
                    };
                }
                if (warning is not null)
                {
                    logger?.LogWarning("Mail restart after backup/restore failed: {Key}: {Detail}", warning.MessageKey, warning.Detail);
                    if (original is not null) original.Data[warning.MessageKey] = warning.Detail;
                }
            }
            return warning;
        }
        finally { MailOfflineGate.Release(); }
    }

    private static async Task CreateMailArchiveAsync(string dataDirectory, string targetPath, CancellationToken cancellationToken)
    {
        EnsureRestoreRootIsNotLink(dataDirectory);
        using var artifact = PendingBackupArtifact.Reserve(targetPath);
        await using (var output = new FileStream(targetPath, FileMode.Open, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("data/");
            var pending = new Stack<string>();
            pending.Push(dataDirectory);
            while (pending.TryPop(out var directory))
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(path);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                        throw new AppException("backup.invalid_entry_path", 400, 400, path);
                    var name = "data/" + Path.GetRelativePath(dataDirectory, path).Replace('\\', '/');
                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        archive.CreateEntry(name + "/");
                        pending.Push(path);
                    }
                    else
                    {
                        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        await using var destination = entry.Open();
                        await source.CopyToAsync(destination, cancellationToken);
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        artifact.Keep();
    }

    private static async Task StageMailArchiveAsync(string archivePath, string stagingDirectory, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count == 0) throw new AppException("backup.invalid_entry_path", 400, 400, "Missing data directory");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(stagingDirectory);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var directory = name.EndsWith('/');
            var trimmed = name.TrimEnd('/');
            var parts = trimmed.Split('/');
            if (parts.Length == 0 || !parts[0].Equals("data", StringComparison.OrdinalIgnoreCase) ||
                (parts.Length == 1 && !directory) ||
                parts.Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':') || part.TrimEnd(' ', '.') != part) ||
                !seen.Add(trimmed) || (directory && entry.Length != 0) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new AppException("backup.invalid_entry_path", 400, 400, entry.FullName);
            // Use a canonical root on every platform. Nothing from the ZIP can address an executable/configuration.
            parts[0] = "data";
            var destination = Path.GetFullPath(Path.Combine(stagingDirectory, Path.Combine(parts)));
            if (!PathContainment.IsInsideRoot(stagingDirectory, destination))
                throw new AppException("backup.invalid_entry_path", 400, 400, entry.FullName);
            SiteBuilderPathGuard.EnsureNoReparsePointOnPath(stagingDirectory, destination);
            if (directory) Directory.CreateDirectory(destination);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var source = entry.Open();
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                var crc = uint.MaxValue;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    for (var i = 0; i < read; i++) crc = MailCrcTable[(crc ^ buffer[i]) & 255] ^ (crc >> 8);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                if (output.Length != entry.Length) throw new InvalidDataException($"Incomplete mail archive entry: {entry.FullName}");
                // ZipArchive decompresses but does not validate entry CRC on .NET 8.
                if (~crc != entry.Crc32) throw new InvalidDataException($"Corrupt mail archive entry: {entry.FullName}");
            }
        }
    }

    private static readonly uint[] MailCrcTable = CreateMailCrcTable();
    private static uint[] CreateMailCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xEDB88320u);
            table[i] = value;
        }
        return table;
    }

    private sealed class DefaultMailRuntime : IMailBackupRuntime
    {
        internal static readonly DefaultMailRuntime Instance = new();
        public string GetInstallDirectory() => ResolveStalwartInstallDirectory();
        public Task StopAsync(string installDirectory, CancellationToken cancellationToken)
            => StopStalwartAsync(installDirectory, cancellationToken);
        public Task<BackupRestoreWarningDto?> StartAsync(CancellationToken cancellationToken)
            => StartStalwartAsync(cancellationToken);
    }
}
