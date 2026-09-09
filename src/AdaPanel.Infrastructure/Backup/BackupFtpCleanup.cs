using System.Globalization;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>Only the known backup layout, from the file's parent up to its day directory.</summary>
internal static class BackupFtpCleanup
{
    internal static IReadOnlyList<Uri> DirectoriesFor(Uri file)
    {
        var folders = file.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).SkipLast(1).ToArray();
        // Layouts have at most four levels beneath the day (site-owned database).
        for (var day = Math.Max(0, folders.Length - 5); day < folders.Length; day++)
        {
            if (!DateTime.TryParseExact(folders[day], BackupLayout.DayFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                continue;

            var levels = folders[(day + 1)..];
            var known = levels.Length == 0
                || (levels.Length is 1 or 2 && levels[0] is "sites" or "databases" or "subscriptions")
                || (levels.Length == 1 && levels[0] is "panel" or "mail" or "connection-test")
                || (levels.Length is 3 or 4 && levels[0] == "sites" && levels[2] == "databases");
            if (!known) continue;

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
