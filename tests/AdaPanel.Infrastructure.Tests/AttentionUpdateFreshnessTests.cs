using System.Reflection;
using AdaPanel.Core.Dtos.Automation;
using AdaPanel.Core.Dtos.Advisor;
using AdaPanel.Core.Dtos.Health;
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

    [Fact]
    public async Task Partial_advisor_domain_and_service_failures_are_not_reported_as_all_clear()
    {
        var advisor = Stub<IAdvisorService>((_, _) => Task.FromResult(new AdvisorReportDto { FailedChecks = ["ssl", "automation"] }));
        var domains = Stub<IDomainRefreshService>((_, _) => Task.FromResult(new DomainRefreshSummaryDto
        {
            FailedSites = 1, Results = [new() { SiteName = "example.test", Error = "probe failed" }]
        }));
        var health = Stub<IHealthService>((_, _) => Task.FromResult<IReadOnlyCollection<ServiceHealthDto>>(
            [new() { Name = "DNS", Status = "Unknown" }, new() { Name = "W3SVC", Status = "Unknown" }]));
        var summary = await new AttentionService(advisor,
            Stub<IComponentBootstrapper>((_, _) => Task.FromResult(0)), null!, null!, domains, health).GetSummaryAsync();
        Assert.Contains("ssl", summary.FailedSources);
        Assert.Contains("automation", summary.FailedSources);
        Assert.Contains("domains", summary.FailedSources);
        Assert.Single(summary.FailedSources, item => item == "services");
        Assert.DoesNotContain(summary.Items, item => item.Id.StartsWith("service-stopped:"));
    }

    [Fact]
    public async Task Disconnected_attention_request_does_not_continue_the_remaining_expensive_probes()
    {
        using var cts = new CancellationTokenSource();
        var nextCalls = 0;
        var advisor = Stub<IAdvisorService>((_, _) => { cts.Cancel(); return Task.FromCanceled<AdvisorReportDto>(cts.Token); });
        var components = Stub<IComponentBootstrapper>((_, _) => { nextCalls++; return Task.FromResult(0); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AttentionService(advisor, components, null!, null!, null!, null!).GetSummaryAsync(cts.Token));
        Assert.Equal(0, nextCalls);
    }

    [Fact]
    public async Task Disconnected_advisor_request_stops_instead_of_turning_cancellation_into_partial_success()
    {
        using var cts = new CancellationTokenSource();
        var nextCalls = 0;
        var components = Stub<IComponentBootstrapper>((_, _) => { cts.Cancel(); return Task.FromCanceled<int>(cts.Token); });
        var automation = Stub<IAutomationService>((_, _) => { nextCalls++; return Task.FromResult(new AutomationStatusDto()); });
        var advisor = new AdvisorService(null!, null!, automation, components, null!, null!, null!, null!, null!,
            NullLogger<AdvisorService>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => advisor.GetReportAsync(cts.Token));
        Assert.Equal(0, nextCalls);
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
