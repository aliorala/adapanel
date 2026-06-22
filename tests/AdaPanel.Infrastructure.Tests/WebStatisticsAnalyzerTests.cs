using AdaPanel.Infrastructure.Statistics;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class WebStatisticsAnalyzerTests
{
    [Fact]
    public void AnalyzeLines_AggregatesW3CLogFields()
    {
        var today = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");
        var lines = new[]
        {
            "#Software: Microsoft Internet Information Services 10.0",
            "#Fields: date time c-ip cs-method cs-uri-stem sc-status sc-bytes cs(Referer)",
            $"{today} 10:00:00 203.0.113.5 GET /index.html 200 1024 https://google.com/search",
            $"{today} 10:00:01 203.0.113.5 GET /index.html 200 2048 https://google.com/search",
            $"{today} 10:00:02 198.51.100.7 GET /missing 404 512 -",
            $"{today} 10:00:03 198.51.100.7 GET /app 500 256 -"
        };

        var cutoff = DateTime.UtcNow.Date.AddDays(-1);
        var result = WebStatisticsService.AnalyzeLines(lines, cutoff);

        Assert.True(result.HasData);
        Assert.Equal(4, result.TotalRequests);
        Assert.Equal(2, result.UniqueVisitors);
        Assert.Equal(1024 + 2048 + 512 + 256, result.TotalBytes);
        Assert.Equal(2, result.Status2xx);
        Assert.Equal(1, result.Status4xx);
        Assert.Equal(1, result.Status5xx);

        var topPage = result.TopPages.First();
        Assert.Equal("/index.html", topPage.Key);
        Assert.Equal(2, topPage.Count);

        Assert.Contains(result.TopReferrers, bucket => bucket.Key == "google.com" && bucket.Count == 2);
    }

    [Fact]
    public void AnalyzeLines_SkipsEntriesOlderThanCutoff()
    {
        var lines = new[]
        {
            "#Fields: date time c-ip cs-uri-stem sc-status sc-bytes",
            "2000-01-01 10:00:00 203.0.113.5 /old 200 100"
        };

        var cutoff = DateTime.UtcNow.Date.AddDays(-7);
        var result = WebStatisticsService.AnalyzeLines(lines, cutoff);

        Assert.False(result.HasData);
        Assert.Equal(0, result.TotalRequests);
    }
}
