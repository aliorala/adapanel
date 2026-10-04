using System.IO.Compression;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Backup;

public sealed partial class BackupService
{
    public async Task<BackupItemsDownload> DownloadItemsAsync(DownloadBackupItemsRequest request,
        Func<BackupRecordDto, CancellationToken, Task> beforeRead, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beforeRead);
        // Snapshot user input before waiting for an archive's lifecycle lease.
        var backupId = request.BackupId;
        var password = request.EncryptionPassword;
        var items = (request.Items ?? []).Select(SelectedEntryPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (items.Length == 0) throw new AppException("backup.items_required", 400, 400);
        var snapshot = await EnterRecordAsync(backupId, cancellationToken, beforeRead);
        using var sourceLease = snapshot.Lease;
        var record = snapshot.Record;
        ValidateZipRecord(record);
        using var content = await OpenContentAsync(record, password, cancellationToken);
        using var archive = ZipFile.OpenRead(content.FilePath);
        var selected = new List<(ZipArchiveEntry Entry, string Name)>();
        var matched = new bool[items.Length];
        var selection = items.Select((name, index) => (name, index))
            .ToDictionary(item => item.name, item => item.index, StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = entry.FullName.Replace('\\', '/');
            var included = false;
            if (selection.TryGetValue(normalized, out var exact))
            { matched[exact] = true; included = true; }
            // Each directory selection is an ancestor prefix. Looking those up avoids
            // archive entries × selected items work for large archives/selections.
            for (var slash = normalized.IndexOf('/'); slash >= 0; slash = normalized.IndexOf('/', slash + 1))
            {
                if (selection.TryGetValue(normalized[..(slash + 1)], out var ancestor))
                { matched[ancestor] = true; included = true; }
            }
            if (!included) continue;
            var name = SelectedEntryPath(normalized);
            if (!names.Add(name)) throw new AppException("backup.invalid_entry_path", 400, 400, name);
            selected.Add((entry, name));
        }
        // A partial match must not be presented as a complete selection download.
        if (matched.Any(found => !found)) throw new AppException("backup.items_not_found", 404, 404);

        var path = SecureTempFile.BuildPath("adabk-selected", ".zip");
        FileStream? result = null;
        try
        {
            // Private ACL exists before the first plaintext byte; no web/site path is used.
            await using (var output = SecureTempFile.Create(path))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var buffer = new byte[81920];
                    foreach (var (entry, name) in selected)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var destination = zip.CreateEntry(name, CompressionLevel.Fastest);
                        if (name.EndsWith('/')) continue;
                        await using var target = destination.Open();
                        await CopyVerifiedSelectedEntryAsync(entry, target, buffer, cancellationToken);
                    }
                }
                await output.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // The HTTP result owns the open handle, not a physical URL. Completion/abort
            // disposes it and the OS removes the private file, including aborted responses.
            result = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            await beforeRead(record, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new BackupItemsDownload(result, BackupPathName.Sanitize(record.Source, "backup") + "-selected.zip", selected.Count);
        }
        catch
        {
            result?.Dispose();
            TryDeleteFile(path);
            throw;
        }
    }

    private static async Task CopyVerifiedSelectedEntryAsync(ZipArchiveEntry entry, Stream output,
        byte[] buffer, CancellationToken token)
    {
        try
        {
            // ZipArchive decompresses entries but .NET 8 does not validate their CRC or
            // declared length. Repacking unchecked data would give damaged legacy bytes
            // a new valid ZIP checksum. Only the selected entries need to be decoded.
            await using var input = entry.Open();
            var remaining = entry.Length;
            if (remaining < 0) throw new InvalidDataException();
            var crc = uint.MaxValue;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                token.ThrowIfCancellationRequested();
                if (read == 0) throw new InvalidDataException();
                for (var i = 0; i < read; i++) crc = SelectedEntryCrcTable[(crc ^ buffer[i]) & 255] ^ (crc >> 8);
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                remaining -= read;
            }
            // Do not write excess decompressed content beyond the declared length.
            var extra = await input.ReadAsync(buffer.AsMemory(0, 1), token);
            token.ThrowIfCancellationRequested();
            if (extra != 0 || ~crc != entry.Crc32) throw new InvalidDataException();
        }
        catch (InvalidDataException)
        {
            // Do not expose entry names or decompressor diagnostics in the response.
            // The outer owner deletes the incomplete private output and releases its lease.
            throw new AppException("backup.archive_corrupt", 400, 400);
        }
    }

    private static readonly uint[] SelectedEntryCrcTable = CreateSelectedEntryCrcTable();

    private static uint[] CreateSelectedEntryCrcTable()
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

    private static string SelectedEntryPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new AppException("backup.items_required", 400, 400);
        var path = value.Replace('\\', '/');
        if (path.StartsWith('/') || path.Contains(':') || path.Any(char.IsControl) ||
            path.TrimEnd('/').Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new AppException("backup.invalid_entry_path", 400, 400);
        return path;
    }
}
