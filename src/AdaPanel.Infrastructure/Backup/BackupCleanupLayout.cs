using System.Globalization;

namespace AdaPanel.Infrastructure.Backup;

internal static class BackupCleanupLayout
{
    internal static int FindDayDirectory(string[] folders, int rootDepth)
    {
        // Layouts have at most four levels beneath the day (site-owned database).
        for (var day = Math.Max(rootDepth, folders.Length - 5); day < folders.Length; day++)
        {
            if (!DateTime.TryParseExact(folders[day], BackupLayout.DayFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) continue;
            var levels = folders[(day + 1)..];
            var known = levels.Length == 0
                || (levels.Length is 1 or 2 && levels[0] is "sites" or "databases" or "subscriptions")
                || (levels.Length == 1 && levels[0] is "panel" or "mail" or "connection-test")
                || (levels.Length is 3 or 4 && levels[0] == "sites" && levels[2] == "databases");
            if (known) return day;
        }
        return -1;
    }
}
