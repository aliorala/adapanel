using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupRetentionCalendarTests
{
    private static readonly TimeZoneInfo Turkey = TimeZoneInfo.CreateCustomTimeZone(
        "Retention Turkey", TimeSpan.FromHours(3), "Retention Turkey", "Retention Turkey");

    [Fact]
    public void FiveDays_ExpiresEveryInstantOfOctober27_AndPreservesFollowingDays()
    {
        var now = new DateTime(2026, 11, 1, 0, 15, 0, DateTimeKind.Utc);
        var cutoff = BackupRetentionCalendar.GetExclusiveCutoffUtc(now, 5, Turkey);
        Assert.Equal(new DateTime(2026, 10, 27, 21, 0, 0, DateTimeKind.Utc), cutoff);
        Assert.Equal(new DateTime(2026, 10, 27), BackupRetentionCalendar.GetExpiredThroughLocalDate(now, 5, Turkey));
        var records = Enumerable.Range(0, 24).Select(hour => Record("expired-" + hour,
            TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 27, hour, 59, 59), Turkey))).ToList();
        records.Add(Record("last-tick", cutoff.AddTicks(-1)));
        records.Add(Record("next-day", cutoff));
        records.Add(Record("today", now));
        records.Add(Record("future", now.AddDays(1)));

        var expired = BackupPrunePlanner.Select(records, cutoff, 0, _ => true);

        Assert.Equal(25, expired.Count);
        Assert.All(expired, record => Assert.True(record.CreatedAtUtc < cutoff));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void DisabledOrUnreachableRetention_ExpiresNothing(int days)
    {
        var now = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Null(BackupRetentionCalendar.GetExpiredThroughLocalDate(now, days, Turkey));
        var cutoff = BackupRetentionCalendar.GetExclusiveCutoffUtc(now, days, Turkey);
        Assert.Equal(DateTime.MinValue, cutoff);
        Assert.Empty(BackupPrunePlanner.Select([Record("old", now.AddYears(-10))], cutoff, 0, _ => true));
    }

    [Theory]
    [InlineData(20, 59, 26)]
    [InlineData(21, 0, 27)]
    public void LocalMidnight_ChangesExpiryEvenWhileUtcDateStaysTheSame(int hour, int minute, int expiredDay)
    {
        var now = new DateTime(2026, 10, 31, hour, minute, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 10, expiredDay),
            BackupRetentionCalendar.GetExpiredThroughLocalDate(now, 5, Turkey));
    }

    [Theory]
    [InlineData(3, 9, 4)] // Spring transition: March 8 has only 23 hours.
    [InlineData(11, 2, 5)] // Autumn transition: November 1 has 25 hours.
    public void DaylightSaving_UsesLocalDayBoundaryInsteadOfSubtracting24Hours(int month, int day, int utcHour)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var now = new DateTime(2026, month, day, 18, 0, 0, DateTimeKind.Utc);
        var cutoff = BackupRetentionCalendar.GetExclusiveCutoffUtc(now, 1, zone);

        Assert.Equal(new DateTime(2026, month, day, utcHour, 0, 0, DateTimeKind.Utc), cutoff);
        Assert.True(TimeZoneInfo.ConvertTimeFromUtc(cutoff.AddTicks(-1), zone).Date <
            TimeZoneInfo.ConvertTimeFromUtc(cutoff, zone).Date);
    }

    [Fact]
    public void DefaultZone_UsesServerLocalTimeZone()
    {
        var now = new DateTime(2026, 11, 1, 0, 15, 0, DateTimeKind.Utc);
        Assert.Equal(BackupRetentionCalendar.GetExclusiveCutoffUtc(now, 5, TimeZoneInfo.Local),
            BackupRetentionCalendar.GetExclusiveCutoffUtc(now, 5));
    }

    private static BackupRecordDto Record(string id, DateTime createdAtUtc) => new()
    {
        Id = id, Type = "site", Source = "site", CreatedAtUtc = createdAtUtc
    };
}
