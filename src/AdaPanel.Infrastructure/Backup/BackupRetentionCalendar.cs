namespace AdaPanel.Infrastructure.Backup;

/// <summary>Age retention follows the server's local calendar, including the entire expiry day.</summary>
internal static class BackupRetentionCalendar
{
    internal static DateTime? GetExpiredThroughLocalDate(
        DateTime nowUtc, int retentionDays, TimeZoneInfo? timeZone = null)
    {
        if (retentionDays <= 0) return null;
        var today = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, timeZone ?? TimeZoneInfo.Local).Date;
        // No representable backup date can be old enough in this case.
        if (retentionDays > (today - DateTime.MinValue).Days) return null;
        return today.AddDays(-retentionDays);
    }

    /// <summary>Exclusive UTC boundary for the planner's CreatedAtUtc &lt; cutoff comparison.</summary>
    internal static DateTime GetExclusiveCutoffUtc(
        DateTime nowUtc, int retentionDays, TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        var expiredThrough = GetExpiredThroughLocalDate(nowUtc, retentionDays, zone);
        if (expiredThrough is null) return DateTime.MinValue;
        var boundary = DateTime.SpecifyKind(expiredThrough.Value.AddDays(1), DateTimeKind.Unspecified);
        // Some zones advance clocks at midnight; their first real instant starts the new day.
        while (zone.IsInvalidTime(boundary)) boundary = boundary.AddMinutes(1);
        if (zone.IsAmbiguousTime(boundary))
            return new DateTimeOffset(boundary, zone.GetAmbiguousTimeOffsets(boundary).Max()).UtcDateTime;
        return TimeZoneInfo.ConvertTimeToUtc(boundary, zone);
    }
}
