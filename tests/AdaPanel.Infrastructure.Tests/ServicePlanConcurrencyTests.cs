using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Xunit;
namespace AdaPanel.Infrastructure.Tests;
public sealed class ServicePlanConcurrencyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-plans-" + Guid.NewGuid().ToString("N"));
    private JsonFileStore Store() => new(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
    [Fact]
    public async Task Concurrent_creation_preserves_both_plans_when_initial_publication_is_pending()
    {
        var store = Store();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initial = store.UpdateAsync("config/service-plans.json", Array.Empty<ServicePlanDto>(), plans => plans,
            async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); }, default);
        Task<ServicePlanDto>? first = null, second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The target still does not exist: old ReadAsync returns the empty snapshot synchronously,
            // then both WriteAsync operations queue behind initial publication.
            first = new ServicePlanService(Store()).CreatePlanAsync(new ServicePlanRequest { Name = "first" });
            second = new ServicePlanService(Store()).CreatePlanAsync(new ServicePlanRequest { Name = "second" });
            Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
        }
        finally { release.TrySetResult(); await initial; if (first is not null) await first; if (second is not null) await second; }
        Assert.Equal(new[] { "first", "second" }, (await new ServicePlanService(Store()).GetPlansAsync()).Select(plan => plan.Name));
    }
    [Fact]
    public async Task Parallel_updates_and_deletes_of_distinct_plans_do_not_restore_old_values()
    {
        var service = new ServicePlanService(Store());
        var owner = Guid.NewGuid();
        var plans = new List<ServicePlanDto>();
        for (var index = 0; index < 24; index++) plans.Add(await service.CreatePlanAsync(new ServicePlanRequest { Name = "plan" + index }, owner));
        await Task.WhenAll(plans.Select((plan, index) => index % 2 == 0
            ? service.DeletePlanAsync(plan.Id)
            : service.UpdatePlanAsync(plan.Id, new ServicePlanRequest { Name = "updated" + index, MaxSites = index })));
        var result = await service.GetPlansAsync();
        Assert.Equal(12, result.Count);
        Assert.All(result, plan => { Assert.StartsWith("updated", plan.Name); Assert.Equal(owner.ToString("N"), plan.OwnerId); Assert.True(plan.MaxSites % 2 == 1); });
        var missing = await Assert.ThrowsAsync<AppException>(() => service.UpdatePlanAsync("missing", new ServicePlanRequest { Name = "fixture" }));
        Assert.Equal("plans.not_found", missing.MessageKey);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
