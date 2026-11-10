using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Infrastructure.Migration.Sources;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class CpanelCronSchedulingTests
{
    [Theory]
    [InlineData("*/7 * * * *")]
    [InlineData("*/15 * * * *")]
    [InlineData("17 * * * *")]
    [InlineData("* * * * *")]
    [InlineData("*/5 * * * *")]
    [InlineData("0 */2 * * *")]
    [InlineData("0 2 * * MON")]
    [InlineData("@hourly")]
    public void Wall_clock_cron_is_manual_mapping_instead_of_a_continuous_scheduler_repetition(string expression)
    {
        var issues = new List<string>();
        var task = Read(expression, issues);
        Assert.True(task.ScheduleUnknown);
        Assert.Equal("cron", task.ScheduleType);
        Assert.Equal(expression, task.ScheduleValue);
        Assert.Contains("cpanel.task.schedule_unknown", issues);
        Assert.Equal("url", task.Kind); Assert.Equal("https://example.test/run", task.Url);
        Assert.Equal(new[] { "source/cron.txt" }, task.ArtifactPaths);
    }

    [Theory]
    [InlineData("15 2 * * *", "02:15")]
    [InlineData("0 0 * * *", "00:00")]
    [InlineData("@daily", "00:00")]
    [InlineData("@midnight", "00:00")]
    public void Daily_wall_clock_time_uses_the_actual_supported_scheduler_contract(string expression, string time)
    {
        var issues = new List<string>();
        var task = Read(expression, issues);
        Assert.False(task.ScheduleUnknown);
        Assert.Equal("daily", task.ScheduleType);
        Assert.Equal(time, task.TimeOfDay);
        Assert.Equal(expression, task.ScheduleValue);
        Assert.Empty(issues);
    }

    [Fact]
    public void Unsupported_original_expression_keeps_internal_whitespace_for_manual_review()
    {
        const string expression = "17  *\t* * *";
        var task = Read(expression, []);
        Assert.True(task.ScheduleUnknown);
        Assert.Equal(expression, task.ScheduleValue);
    }

    private static PortableMigrationTask Read(string expression, List<string> issues)
        => CpanelCronReader.Read(expression + " curl -fsS https://example.test/run", 1, "source/cron.txt",
            _ => ["site:example"], _ => ([], []), (code, _, _) => issues.Add(code));
}
