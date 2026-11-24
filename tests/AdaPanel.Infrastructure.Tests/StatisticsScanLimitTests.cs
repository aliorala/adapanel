using AdaPanel.Infrastructure.Statistics;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class StatisticsScanLimitTests
{
    [Theory]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public async Task Reports_partial_only_when_records_are_left_unread(int records, bool partial)
    {
        var directory = Directory.CreateTempSubdirectory("adapanel-stats-limit-");
        try
        {
            await WriteLog(directory.FullName, "new.log", records);
            var result = await WebStatisticsService.ReadLogsAsync("example.test", 7, directory.FullName, 3);
            Assert.Equal(Math.Min(3, records), result.TotalRequests);
            Assert.Equal(partial, result.ScanLimitReached);
            Assert.Equal(Math.Min(3, records) * 10, result.TotalBytes);
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task Shared_budget_detects_unread_records_in_another_file(int olderRecords, bool partial)
    {
        var directory = Directory.CreateTempSubdirectory("adapanel-stats-limit-");
        try
        {
            var older = await WriteLog(directory.FullName, "old.log", olderRecords);
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-1));
            await WriteLog(directory.FullName, "new.log", 3);
            var result = await WebStatisticsService.ReadLogsAsync("example.test", 7, directory.FullName, 3);
            Assert.Equal(3, result.TotalRequests);
            Assert.Equal(partial, result.ScanLimitReached);
        }
        finally { directory.Delete(true); }
    }

    private static async Task<string> WriteLog(string root, string name, int records)
    {
        var path = Path.Combine(root, name);
        var row = $"{DateTime.UtcNow:yyyy-MM-dd} 12:00:00 192.0.2.1 GET / 200 10\n";
        await File.WriteAllTextAsync(path, "#Fields: date time c-ip cs-method cs-uri-stem sc-status sc-bytes\n" + string.Concat(Enumerable.Repeat(row, records)));
        return path;
    }
}
