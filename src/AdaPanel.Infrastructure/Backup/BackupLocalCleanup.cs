using AdaPanel.Infrastructure.SiteBuilder;

namespace AdaPanel.Infrastructure.Backup;

internal static class BackupLocalCleanup
{
    internal static void RemoveEmptyParents(string file, string configuredRoot)
    {
        if (!Path.IsPathFullyQualified(file) || !Path.IsPathFullyQualified(configuredRoot)) return;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
        var fullPath = Path.GetFullPath(file);
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;

        // Share the recognized day/layout rules with FTP, including the root boundary.
        var folders = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries).SkipLast(1).ToArray();
        var day = BackupCleanupLayout.FindDayDirectory(folders, 0);
        if (day < 0) return;
        // Include ancestors of the configured root: it may itself be under a junction.
        SiteBuilderPathGuard.EnsureNoReparsePointOnPath(Path.GetPathRoot(root)!,
            Path.GetDirectoryName(fullPath)!);
        for (var index = folders.Length; index > day; index--)
        {
            var path = Path.Combine(root, Path.Combine(folders[..index]));
            // Recheck before each operation; never recursively remove folder contents.
            SiteBuilderPathGuard.EnsureNoReparsePointOnPath(Path.GetPathRoot(root)!, path);
            if (!Directory.Exists(path)) continue;
            if (Directory.EnumerateFileSystemEntries(path).Any()) break;
            Directory.Delete(path, recursive: false);
        }
    }
}
