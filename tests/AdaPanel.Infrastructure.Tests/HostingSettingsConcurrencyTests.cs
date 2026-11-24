using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class HostingSettingsConcurrencyTests
{
    [Fact]
    public async Task Concurrent_first_reads_preserve_every_sites_settings()
    {
        using var f = new Fixture();
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        f.Sites.BeforeRead = async () =>
        {
            if (Interlocked.Increment(ref count) == 32) allEntered.TrySetResult();
            await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => f.Service.GetAsync($"site-{i}")));
        var records = await f.Store.ReadAsync("config/site-hosting-settings.json", Array.Empty<SiteHostingSettingsDto>());
        Assert.Equal(32, records.Length);
        Assert.Equal(results.Select(row => row.SiteName).Order(), records.Select(row => row.SiteName).Order());
    }

    [Fact]
    public async Task Same_site_updates_keep_web_config_native_limits_and_metadata_in_the_same_order()
    {
        using var f = new Fixture();
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = f.Gates.Inner.RunExclusiveAsync(ConcurrencyKeys.Iis, async () =>
        { holding.TrySetResult(); await release.Task; });
        await holding.Task;
        try
        {
            var first = f.Service.UpdateAsync("same", new() { DirectoryBrowsingEnabled = true, MaximumConnections = 10 });
            await f.Gates.IisRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var second = f.Service.UpdateAsync("SAME", new() { DirectoryBrowsingEnabled = false, MaximumConnections = 20 });
            // Both service calls have run to their first asynchronous wait. The second
            // must be waiting outside site lookup and before its web.config write.
            Assert.Equal(1, f.Sites.Reads);
            var config = System.Xml.Linq.XDocument.Load(Path.Combine(f.SiteRoot, "web.config"));
            Assert.Equal("true", (string?)config.Descendants("directoryBrowse").Single().Attribute("enabled"));
            release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            var record = Assert.Single(await f.Store.ReadAsync("config/site-hosting-settings.json", Array.Empty<SiteHostingSettingsDto>()));
            Assert.False(record.DirectoryBrowsingEnabled);
            Assert.Equal(20, record.MaximumConnections);
            Assert.Equal(new long[] { 10, 20 }, f.NativeConnections);
            config = System.Xml.Linq.XDocument.Load(Path.Combine(f.SiteRoot, "web.config"));
            Assert.Equal("false", (string?)config.Descendants("directoryBrowse").Single().Attribute("enabled"));
        }
        finally { release.TrySetResult(); await blocker; }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "hosting-settings-" + Guid.NewGuid().ToString("N"));
        internal string SiteRoot { get; }
        internal JsonFileStore Store { get; }
        internal FakeSites Sites { get; }
        internal ObservedGates Gates { get; } = new();
        internal SiteHostingSettingsService Service { get; }
        internal List<long> NativeConnections { get; } = [];
        internal Fixture()
        {
            SiteRoot = Path.Combine(root, "site"); Directory.CreateDirectory(SiteRoot);
            var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root });
            Store = new(options); Sites = new(SiteRoot);
            Service = new(Sites, null!, null!, options, Gates, Store)
            {
                WebConfigurationProbe = _ => true,
                IisLimitsWriter = (_, _, connections) => NativeConnections.Add(connections)
            };
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class FakeSites(string root) : IIisSiteService
    {
        internal Func<Task>? BeforeRead;
        internal int Reads;
        public async Task<SiteDto> GetSiteAsync(string name, CancellationToken token = default)
        {
            Interlocked.Increment(ref Reads);
            if (BeforeRead is not null) await BeforeRead();
            return new() { Id = 1, Name = name, PhysicalPath = root, ApplicationRootPath = root,
                ApplicationPoolName = "pool", Bindings = [new() { Protocol = "http", HostName = name, Port = 80 }] };
        }
        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken token = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string name, string path, CancellationToken token = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string name, CancellationToken token = default) => throw new NotSupportedException();
        public Task<SiteDto> StartSiteAsync(string name, CancellationToken token = default) => throw new NotSupportedException();
        public Task<SiteDto> StopSiteAsync(string name, CancellationToken token = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string name, SiteBindingRequest request, CancellationToken token = default) => throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string name, RemoveSiteBindingRequest request, CancellationToken token = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken token = default) => throw new NotSupportedException();
    }

    private sealed class ObservedGates : IConcurrencyLock
    {
        internal ConcurrencyLock Inner { get; } = new();
        internal TaskCompletionSource IisRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RunExclusiveAsync(Func<Task> operation, CancellationToken token = default) => RunExclusiveAsync(ConcurrencyKeys.Iis, operation, token);
        public Task<T> RunExclusiveAsync<T>(Func<Task<T>> operation, CancellationToken token = default) => RunExclusiveAsync(ConcurrencyKeys.Iis, operation, token);
        public Task RunExclusiveAsync(string key, Func<Task> operation, CancellationToken token = default)
        { if (key == ConcurrencyKeys.Iis) IisRequested.TrySetResult(); return Inner.RunExclusiveAsync(key, operation, token); }
        public Task<T> RunExclusiveAsync<T>(string key, Func<Task<T>> operation, CancellationToken token = default)
        { if (key == ConcurrencyKeys.Iis) IisRequested.TrySetResult(); return Inner.RunExclusiveAsync(key, operation, token); }
    }
}
