using System.Globalization;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.SiteBuilder;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>Removes expired daily backup folders only inside the configured server backup root.</summary>
internal static class BackupExpiredLocalDayCleanup
{
    internal sealed record Report(int RemovedDays, IReadOnlyList<string> Failures);

    internal static async Task<Report> SweepAsync(string configuredRoot, DateTime expiredThroughLocalDate,
        IReadOnlyCollection<string> protectedArchivePaths, Func<CancellationToken, Task>? beforeMutation,
        CancellationToken cancellationToken)
    {
        var removed = 0;
        var failures = new List<string>();
        if (!Path.IsPathFullyQualified(configuredRoot) || !Directory.Exists(configuredRoot))
            return new Report(0, failures);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
        var volume = Path.GetPathRoot(root)!;
        try { SiteBuilderPathGuard.EnsureNoReparsePointOnPath(volume, root); }
        catch (AppException) { return new Report(0, ["local_day_cleanup: root_is_link"]); }
        foreach (var dayRoot in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dayRoot);
            if (!DateTime.TryParseExact(name, BackupLayout.DayFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var day) || day > expiredThroughLocalDate.Date) continue;
            // A retained chain, failed deletion or another owner's record protects the WHOLE day.
            if (protectedArchivePaths.Any(path => Contains(dayRoot, path))) continue;
            try
            {
                var files = new List<string>();
                var directories = new List<string>();
                var visited = 0;
                Plan(dayRoot, 0);
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (beforeMutation is not null) await beforeMutation(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    SiteBuilderPathGuard.EnsureNoReparsePointOnPath(volume, file);
                    File.Delete(file);
                }
                // Postorder, non-recursive removal: new/unlisted files prevent deletion.
                foreach (var directory in directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (beforeMutation is not null) await beforeMutation(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    SiteBuilderPathGuard.EnsureNoReparsePointOnPath(volume, directory);
                    Directory.Delete(directory, recursive: false);
                }
                removed++;

                void Plan(string directory, int depth)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (depth > 32 || ++visited > 100_000) throw new IOException("scan_limit");
                    SiteBuilderPathGuard.EnsureNoReparsePointOnPath(volume, directory);
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++visited > 100_000) throw new IOException("scan_limit");
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("entry_is_link");
                        if ((attributes & FileAttributes.Directory) != 0) Plan(entry, depth + 1);
                        else files.Add(entry);
                    }
                    directories.Add(directory);
                }
            }
            catch (AppException exception) when (exception.MessageKey == "sitebuilder.target_is_link")
            { failures.Add($"{name}: local_day_cleanup: entry_is_link"); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { failures.Add($"{name}: local_day_cleanup: {exception.GetType().Name}"); }
        }
        return new Report(removed, failures);
    }

    private static bool Contains(string dayRoot, string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        var relative = Path.GetRelativePath(dayRoot, Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
