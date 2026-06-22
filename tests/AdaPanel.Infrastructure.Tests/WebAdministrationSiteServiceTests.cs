using System.Runtime.InteropServices;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Iis;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class WebAdministrationSiteServiceTests
{
    [Fact]
    public void MapSite_UsesUnknownState_WhenIisReturnsAStaleComObject()
    {
        var service = new WebAdministrationSiteService(
            new ImmediateConcurrencyLock(),
            NullLogger<WebAdministrationSiteService>.Instance);

        var site = service.MapSite(new StaleStateSite());

        Assert.Equal(42, site.Id);
        Assert.Equal("example.test", site.Name);
        Assert.Equal("Unknown", site.State);
        Assert.True(site.AutoStart);
        Assert.Equal(@"C:\sites\example.test", site.PhysicalPath);
        Assert.Equal("ExamplePool", site.ApplicationPoolName);
        Assert.Single(site.Bindings);
    }

    private sealed class StaleStateSite
    {
        public long Id => 42;
        public string Name => "example.test";
        public object State => throw new COMException(
            "The object identifier does not represent a valid object.",
            unchecked((int)0x800710D8));
        public bool ServerAutoStart => true;
        public IReadOnlyCollection<FakeBinding> Bindings =>
        [
            new FakeBinding()
        ];
        public IReadOnlyCollection<FakeApplication> Applications =>
        [
            new FakeApplication()
        ];
    }

    private sealed class FakeBinding
    {
        public string Protocol => "http";
        public string BindingInformation => "*:80:example.test";
    }

    private sealed class FakeApplication
    {
        public string Path => "/";
        public string ApplicationPoolName => "ExamplePool";
        public IReadOnlyCollection<FakeVirtualDirectory> VirtualDirectories =>
        [
            new FakeVirtualDirectory()
        ];
    }

    private sealed class FakeVirtualDirectory
    {
        public string Path => "/";
        public string PhysicalPath => @"C:\sites\example.test";
    }

    private sealed class ImmediateConcurrencyLock : IConcurrencyLock
    {
        public Task RunExclusiveAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default) => operation();

        public Task<T> RunExclusiveAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default) => operation();
    }
}
