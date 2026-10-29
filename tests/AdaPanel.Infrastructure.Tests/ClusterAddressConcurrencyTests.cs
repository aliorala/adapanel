using System.Net;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Cluster;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Cluster;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class ClusterAddressConcurrencyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-cluster-race-" + Guid.NewGuid().ToString("N"));
    private ClusterNodeStore Store(ISecretProtector protector) => new(
        new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root })),
        protector, NullLogger<ClusterNodeStore>.Instance);

    [Fact]
    public async Task Concurrent_registration_checks_uniqueness_inside_atomic_write()
    {
        using var barrier = new Barrier(2);
        var store = Store(new Protector(barrier));
        var service = new ClusterService(store, new NoNetwork(), NullLogger<ClusterService>.Instance)
        { Resolver = (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }) };
        async Task<Exception?> Register(string label) => await Record.ExceptionAsync(() => service.RegisterAsync(
            new RegisterClusterNodeRequest { Label = label, Address = "https://node.example.test:9443", ApiToken = "fixture" }));
        // Protect is called after the service's pre-read duplicate check and before the atomic add.
        var results = await Task.WhenAll(Task.Run(() => Register("node-a")), Task.Run(() => Register("node-b")));
        Assert.Single(results, error => error is null);
        var rejected = Assert.IsType<AppException>(Assert.Single(results, error => error is not null));
        Assert.Equal(ClusterMessages.DuplicateAddress, rejected.MessageKey);
        Assert.Single(await store.GetAllAsync());
    }

    [Fact]
    public async Task Updating_to_an_existing_address_fails_without_persisting_mutation()
    {
        var store = Store(new Protector());
        var a = await store.AddAsync(new ClusterNodeRecord { Address = "https://a.test", Label = "a" }, "fixture");
        var b = await store.AddAsync(new ClusterNodeRecord { Address = "https://b.test", Label = "b" }, "fixture");
        var error = await Assert.ThrowsAsync<AppException>(() => store.UpdateAsync(b.Id, node => { node.Address = a.Address.ToUpperInvariant(); node.Label = "changed"; }, null));
        Assert.Equal(ClusterMessages.DuplicateAddress, error.MessageKey);
        var persisted = await store.GetAsync(b.Id);
        Assert.Equal("https://b.test", persisted!.Address);
        Assert.Equal("b", persisted.Label);
        await store.UpdateAsync(a.Id, node => node.Label = "same-address", null);
        Assert.Equal("same-address", (await store.GetAsync(a.Id))!.Label);
    }

    private sealed class Protector(Barrier? barrier = null) : ISecretProtector
    {
        public string Protect(string value) { if (barrier is not null && !barrier.SignalAndWait(TimeSpan.FromSeconds(15))) throw new TimeoutException("fixture barrier"); return value; }
        public string Unprotect(string value) => value;
    }
    private sealed class NoNetwork : IClusterNodeClient
    {
        public Task<ClusterCallResult<ClusterNodeInfoDto>> FetchInfoAsync(ClusterNodeRecord node, string token, CancellationToken ct) => throw new InvalidOperationException("No network in fixture");
        public Task<ClusterCallResult<JsonElement>> PostAsync(ClusterNodeRecord node, string token, string path, object payload, CancellationToken ct) => throw new InvalidOperationException("No network in fixture");
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
