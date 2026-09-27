namespace AdaPanel.Infrastructure.Backup;

/// <summary>Only the known backup layout, from the file's parent up to its day directory.</summary>
internal static class BackupFtpCleanup
{
    internal static bool IsDirectoryMarker(string name, Uri directory)
    {
        var entry = name.TrimEnd('/');
        var prefix = Uri.UnescapeDataString(directory.AbsolutePath).TrimEnd('/') + "/";
        if (entry.StartsWith(prefix, StringComparison.Ordinal)) entry = entry[prefix.Length..];
        return entry is "." or "..";
    }

    internal static IReadOnlyList<Uri> DirectoriesFor(Uri file, string? configuredRoot = null)
    {
        var folders = file.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).SkipLast(1).ToArray();
        var rootDepth = 0;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            if (!Uri.TryCreate(configuredRoot.TrimEnd('/') + "/", UriKind.Absolute, out var root)
                || !root.IsBaseOf(file)) return [];
            rootDepth = root.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
        }
        var day = BackupCleanupLayout.FindDayDirectory(folders, rootDepth);
        if (day >= 0)
        {
            return Enumerable.Range(day, folders.Length - day).Reverse()
                .Select(index => new UriBuilder(file)
                {
                    Path = "/" + string.Join('/', folders.Take(index + 1)) + "/",
                    Query = string.Empty, Fragment = string.Empty,
                    UserName = string.Empty, Password = string.Empty
                }.Uri).ToArray();
        }
        return [];
    }
}
