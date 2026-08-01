using AdaPanel.Core.Dtos.Advisor;
using AdaPanel.Infrastructure.Advisor;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Rozet/bildirim listesinin saf mantığı: aynı sorunu iki kaynak da bildirdiğinde tek madde kalmalı,
/// kritik olan hep üstte olmalı ve bölüm sayaçları menü rozetleriyle tutarlı olmalı.
/// </summary>
public sealed class AttentionPrioritizationTests
{
    [Fact]
    public void Prioritize_SortsCriticalFirst()
    {
        var ordered = AttentionService.Prioritize(
        [
            new AttentionItemDto { Id = "a", Severity = "info", Section = "settings" },
            new AttentionItemDto { Id = "b", Severity = "critical", Section = "dns" },
            new AttentionItemDto { Id = "c", Severity = "warning", Section = "software" }
        ]);

        Assert.Equal(["b", "c", "a"], ordered.Select(item => item.Id));
    }

    [Fact]
    public void Prioritize_KeepsTheMoreSevereDuplicate()
    {
        var ordered = AttentionService.Prioritize(
        [
            new AttentionItemDto { Id = "component-updates", Severity = "info", Section = "advisor" },
            new AttentionItemDto { Id = "component-updates", Severity = "warning", Section = "software" }
        ]);

        var single = Assert.Single(ordered);
        Assert.Equal("warning", single.Severity);
        Assert.Equal("software", single.Section);
    }

    [Fact]
    public void Prioritize_IsStableAcrossRunsForEqualSeverity()
    {
        AttentionItemDto[] items =
        [
            new() { Id = "service-stopped:W3SVC", Severity = "warning", Section = "health" },
            new() { Id = "panel-update", Severity = "warning", Section = "updates" },
            new() { Id = "component-updates", Severity = "warning", Section = "software" }
        ];

        var first = AttentionService.Prioritize(items).Select(item => item.Id).ToArray();
        var second = AttentionService.Prioritize(items.Reverse()).Select(item => item.Id).ToArray();

        Assert.Equal(first, second);
    }

    [Fact]
    public void CountBySection_GroupsForTheMenuBadges()
    {
        var counts = AttentionService.CountBySection(
        [
            new AttentionItemDto { Id = "a", Section = "software" },
            new AttentionItemDto { Id = "b", Section = "software" },
            new AttentionItemDto { Id = "c", Section = "health" },
            new AttentionItemDto { Id = "d", Section = "" }
        ]);

        Assert.Equal(2, counts["software"]);
        Assert.Equal(1, counts["health"]);
        Assert.Equal(1, counts["advisor"]);
    }

    [Theory]
    [InlineData("W3SVC", "critical")]
    [InlineData("WAS", "critical")]
    [InlineData("DNS", "critical")]
    [InlineData("dns", "critical")]
    [InlineData("MariaDB", "warning")]
    [InlineData("FTPSVC", "warning")]
    public void ServiceSeverity_TreatsWebAndDnsOutagesAsCritical(string serviceName, string expected)
        => Assert.Equal(expected, AttentionService.ServiceSeverity(serviceName));
}
