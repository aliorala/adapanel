using System.Globalization;
using System.Net;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>Removes expired day trees, bounded to the panel's generated backup layout.</summary>
internal static class BackupExpiredFtpDayCleanup
{
    internal sealed record Report(int RemovedDays, IReadOnlyList<string> Failures);
    private sealed record DirectoryListing(IReadOnlyCollection<string> Names, bool RequiresRemoveProof = false);
    private sealed record PlannedDirectory(Uri Uri, bool RequiresRemoveProof);

    internal static async Task<Report> SweepAsync(IBackupFtpCommands ftp, Uri configuredRoot,
        NetworkCredential credential, DateTime expiredThroughLocalDate, IReadOnlyCollection<Uri> protectedArchiveUris,
        Func<CancellationToken, Task>? beforeMutation, CancellationToken ct)
    {
        if (!configuredRoot.IsAbsoluteUri || configuredRoot.Scheme != "ftp"
            || configuredRoot.UserInfo.Length != 0 || configuredRoot.Query.Length != 0 || configuredRoot.Fragment.Length != 0)
            throw new ArgumentException("A credential-free FTP directory root is required.", nameof(configuredRoot));
        var root = new Uri(configuredRoot.AbsoluteUri.TrimEnd('/') + "/");
        var failures = new List<string>();
        var removed = 0;
        var entries = 0;
        var directories = 0;
        var listing = await ReadAsync(root);
        if (listing is null) return new Report(0, failures);
        foreach (var name in listing.Names)
        {
            ct.ThrowIfCancellationRequested();
            if (!DateTime.TryParseExact(name, BackupLayout.DayFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var day) || day.Date > expiredThroughLocalDate.Date) continue;
            var dayRoot = Child(root, name, true);
            if (protectedArchiveUris.Any(uri => ProtectsDay(dayRoot, uri))) continue;
            var files = new List<Uri>();
            var folders = new List<PlannedDirectory>();
            // Preflight the entire day before deleting anything. Unknown trees may be links
            // to unrelated data; NLST has no portable type or symlink information.
            if (!await PlanAsync(dayRoot, [])) continue;
            var success = true;
            foreach (var file in files)
            {
                await BeforeAsync();
                var result = await ftp.DeleteFileAsync(file, credential, ct);
                if (!result.Success)
                {
                    failures.Add($"FTP DELE {file.AbsolutePath}: {result.Code}");
                    success = false;
                    break;
                }
            }
            if (!success) continue;
            foreach (var planned in folders)
            {
                var folder = planned.Uri;
                // Empty NLST may return 550 on IIS/FileZilla. Only successful RMD proves
                // emptiness; it cannot remove a nonempty directory or delete its contents.
                var remaining = planned.RequiresRemoveProof
                    ? new DirectoryListing([], RequiresRemoveProof: true)
                    : await ReadAsync(folder, allowRemoveProof: true);
                if (remaining is null || (!remaining.RequiresRemoveProof && remaining.Names.Count != 0))
                {
                    if (remaining is { Names.Count: > 0 }) failures.Add($"FTP directory changed or remains nonempty: {folder.AbsolutePath}");
                    success = false;
                    break;
                }
                await BeforeAsync();
                var result = await ftp.RemoveDirectoryAsync(folder, credential, ct);
                if (!result.Success)
                {
                    failures.Add($"FTP RMD {folder.AbsolutePath}: {result.Code}");
                    success = false;
                    break;
                }
            }
            if (success) removed++;

            async Task<bool> PlanAsync(Uri directory, string[] levels)
            {
                var children = await ReadAsync(directory, allowRemoveProof: true);
                if (children is null) return false;
                foreach (var childName in children.Names)
                {
                    // TestRemoteTargetAsync owns this exact, case-sensitive day-relative
                    // probe path. Never broaden deletion to arbitrary text files or trees.
                    var isConnectionProbe = levels.Length == 1 && levels[0] == "connection-test"
                        && childName == "adapanel-connection-test.txt";
                    if (IsArchiveName(childName) || isConnectionProbe)
                    {
                        files.Add(Child(directory, childName, false));
                        continue;
                    }
                    string[] next = [.. levels, childName];
                    string[] layout = [name, .. next];
                    if (next.Length > 4 || BackupCleanupLayout.FindDayDirectory(layout, 0) != 0)
                    {
                        failures.Add($"FTP unknown backup entry: {directory.AbsolutePath}{childName}");
                        return false;
                    }
                    if (!await PlanAsync(Child(directory, childName, true), next)) return false;
                }
                folders.Add(new PlannedDirectory(directory, children.RequiresRemoveProof));
                return true;
            }
        }
        return new Report(removed, failures);

        async Task BeforeAsync()
        {
            ct.ThrowIfCancellationRequested();
            if (beforeMutation is not null) await beforeMutation(ct);
            ct.ThrowIfCancellationRequested();
        }

        async Task<DirectoryListing?> ReadAsync(Uri directory, bool allowRemoveProof = false)
        {
            ct.ThrowIfCancellationRequested();
            if (++directories > 2000) throw new InvalidOperationException("FTP day directory scan limit reached.");
            var result = await ftp.ListDirectoryAsync(directory, credential, ct);
            if (!result.Success)
            {
                // The configured root never receives this permission. The caller may
                // only queue a known day/layout directory for an empty-only RMD probe.
                if (allowRemoveProof && result.Code == FtpCommandResult.FileUnavailable)
                    return new DirectoryListing([], RequiresRemoveProof: true);
                failures.Add($"FTP NLST {directory.AbsolutePath}: {result.Code}");
                return null;
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            var prefix = Uri.UnescapeDataString(directory.AbsolutePath);
            foreach (var raw in result.Names)
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > 10000) throw new InvalidOperationException("FTP day entry scan limit reached.");
                var name = raw.TrimEnd('/');
                if (name.StartsWith(prefix, StringComparison.Ordinal)) name = name[prefix.Length..];
                if (name is "." or "..") continue;
                if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains('%')
                    || name.Any(char.IsControl) || name != name.Trim() || name is "." or "..")
                {
                    failures.Add($"FTP unsafe listing entry below {directory.AbsolutePath}");
                    return null;
                }
                names.Add(name);
            }
            return new DirectoryListing(names.ToList());
        }
    }

    private static Uri Child(Uri parent, string name, bool directory) =>
        new(parent, Uri.EscapeDataString(name) + (directory ? "/" : string.Empty));

    private static bool ProtectsDay(Uri day, Uri archive)
    {
        if (!archive.IsAbsoluteUri || !string.Equals(day.Scheme, archive.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(day.Host, archive.Host, StringComparison.OrdinalIgnoreCase) || day.Port != archive.Port) return false;
        var prefix = Uri.UnescapeDataString(day.AbsolutePath).TrimEnd('/');
        var path = Uri.UnescapeDataString(archive.AbsolutePath).TrimEnd('/');
        return path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsArchiveName(string name)
    {
        if (name.EndsWith(BackupArchiveCipher.EncryptedExtension, StringComparison.OrdinalIgnoreCase))
            name = name[..^BackupArchiveCipher.EncryptedExtension.Length];
        if (name.EndsWith(BackupDumpCompression.CompressedExtension, StringComparison.OrdinalIgnoreCase))
            name = name[..^BackupDumpCompression.CompressedExtension.Length];
        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) name = name[..^3];
        return new[] { ".zip", ".sql", ".bak", ".dump", ".tar", ".sqlite", ".db" }
            .Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }
}
