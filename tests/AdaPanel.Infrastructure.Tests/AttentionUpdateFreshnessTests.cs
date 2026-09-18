using System.Reflection;
using AdaPanel.Core.Dtos.Automation;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Advisor;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class AttentionUpdateFreshnessTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Advisor_and_attention_use_current_count_instead_of_yesterdays_automation(int pending)
    {
        var components = Stub<IComponentBootstrapper>((_, _) => Task.FromResult(pending));
        var automation = Stub<IAutomationService>((_, _) => Task.FromResult(new AutomationStatusDto
        {
            PendingComponentUpdates = 6, LastUpdateCheckDetails = "stale six updates"
        }));
        var advisor = new AdvisorService(null!, null!, automation, components, null!, null!, null!, null!, null!,
            NullLogger<AdvisorService>.Instance);
        var attention = new AttentionService(advisor, components, null!, null!, null!, null!);
        var report = await advisor.GetReportAsync();
        var summary = await attention.GetSummaryAsync();
        if (pending == 0)
        {
            Assert.DoesNotContain(report.Items, item => item.Id == "component-updates");
            Assert.DoesNotContain(summary.Items, item => item.Id == "component-updates");
            Assert.False(summary.SectionCounts.ContainsKey("software"));
        }
        else
        {
            Assert.Equal("2", Assert.Single(report.Items, item => item.Id == "component-updates").Detail);
            Assert.Equal("2", Assert.Single(summary.Items, item => item.Id == "component-updates").Detail);
            Assert.Equal(2, Assert.Single(summary.Items, item => item.Id == "component-updates").AffectedCount);
            // The same service instances must stop reporting the alert immediately after install.
            pending = 0;
            Assert.DoesNotContain((await attention.GetSummaryAsync()).Items, item => item.Id == "component-updates");
        }
    }

    [Fact]
    public async Task Failed_current_count_is_reported_as_unavailable_not_replaced_with_stale_success()
    {
        var components = Stub<IComponentBootstrapper>((_, _) => Task.FromException<int>(new IOException("cache unavailable")));
        var advisor = new AdvisorService(null!, null!, null!, components, null!, null!, null!, null!, null!,
            NullLogger<AdvisorService>.Instance);
        var report = await advisor.GetReportAsync();
        Assert.Contains("components", report.FailedChecks);
        Assert.DoesNotContain(report.Items, item => item.Id == "component-updates");
        var summary = await new AttentionService(advisor, components, null!, null!, null!, null!).GetSummaryAsync();
        Assert.Contains("components", summary.FailedSources);
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)proxy).Handler = handler;
        return proxy;
    }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
