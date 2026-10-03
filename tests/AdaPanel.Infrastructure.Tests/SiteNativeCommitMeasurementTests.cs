using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SiteNativeCommitMeasurementTests
{
    [Fact]
    public async Task CreateMeasuresAssignedIdWithFreshManagerBeforeReleasingIis()
    {
        var gates = new ConcurrencyLock();
        var name = "commit-" + Guid.NewGuid().ToString("N");
        var committed = false;
        var factoryCalls = 0;
        var callbackRan = false;
        Task? contender = null;
        var managers = new List<Manager>();
        var service = new WebAdministrationSiteService(gates, NullLogger<WebAdministrationSiteService>.Instance, () =>
        {
            factoryCalls++;
            if (factoryCalls == 2)
            {
                Assert.True(committed);
                // A competing native mutation must queue until measurement has returned.
                contender = SiteMutationGuard.RunMutationAsync(gates, name, true, () =>
                { callbackRan = true; return Task.FromResult(0); }, default);
                Assert.False(contender.IsCompleted);
                Assert.False(callbackRan);
            }
            var manager = new Manager(name, committed ? 37 : 0, () => committed = true);
            managers.Add(manager);
            return manager;
        });
        var measured = await Write(service, manager =>
        {
            Assert.Equal(0, Assert.IsType<Manager>(manager).Sites.Single().Id);
            return new SiteDto { Name = name, Id = 0 };
        }, name).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(37, measured.Id);
        Assert.Equal(name, measured.Name);
        Assert.Equal(2, factoryCalls);
        Assert.All(managers, manager => Assert.True(manager.Disposed));
        Assert.NotNull(contender);
        await contender.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(callbackRan);
    }

    [Fact]
    public async Task FailedCommitNeverMeasuresOrReturnsAnUncommittedSite()
    {
        var factoryCalls = 0;
        var name = "commit-fail-" + Guid.NewGuid().ToString("N");
        var service = new WebAdministrationSiteService(new ConcurrencyLock(), NullLogger<WebAdministrationSiteService>.Instance, () =>
        {
            factoryCalls++;
            return new Manager(name, 0, () => throw new IOException("commit failed"));
        });
        await Assert.ThrowsAsync<AppException>(() => Write(service, _ => new SiteDto { Name = name }, name));
        Assert.Equal(1, factoryCalls);
    }

    private static Task<SiteDto> Write(WebAdministrationSiteService service, Func<object, SiteDto> operation, string name)
    {
        var read = typeof(WebAdministrationSiteService).GetMethod("ReadCreatedSite", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<SiteDto, SiteDto>>(service);
        var write = typeof(WebAdministrationSiteService).GetMethod("ExecuteWriteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(SiteDto));
        return (Task<SiteDto>)write.Invoke(service,
            [operation, "sites.write_failed", CancellationToken.None, name, SiteMutationGuard.MutationKind.Create, read])!;
    }

    public sealed class Manager(string name, long id, Action commit) : IDisposable
    {
        public Site[] Sites { get; } = [new(name, id)];
        public bool Disposed { get; private set; }
        public void CommitChanges() => commit();
        public void Dispose() => Disposed = true;
    }
    public sealed class Site(string name, long id)
    {
        public long Id { get; } = id;
        public string Name { get; } = name;
        public string State => "Started";
        public bool ServerAutoStart => true;
        public object[] Bindings => [];
        public Application[] Applications => [new()];
    }
    public sealed class Application
    {
        public string Path => "/";
        public string ApplicationPoolName => "fixture-pool";
        public VirtualDirectory[] VirtualDirectories => [new()];
    }
    public sealed class VirtualDirectory
    {
        public string Path => "/";
        public string PhysicalPath => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fixture-root");
    }
}
