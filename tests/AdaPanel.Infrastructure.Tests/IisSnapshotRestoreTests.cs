using System.Text.Json;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class IisSnapshotRestoreTests
{
    private static SiteDto MakeSite(string name, params SiteBindingDto[] bindings) => new()
    {
        Id = 1,
        Name = name,
        State = "Started",
        AutoStart = true,
        PhysicalPath = $"C:\\inetpub\\{name}",
        ApplicationPoolName = $"{name}-pool",
        Bindings = bindings
    };

    [Fact]
    public void GetMissingSites_IsCaseInsensitiveAndSkipsExisting()
    {
        var snapshot = new[] { MakeSite("SiteA"), MakeSite("siteb"), MakeSite("SiteC") };

        var missing = IisSnapshotRestore.GetMissingSites(snapshot, ["sitea", "SITEB"]);

        Assert.Single(missing);
        Assert.Equal("SiteC", missing[0].Name);
    }

    [Fact]
    public void ToProvisioningPlan_UsesFirstHttpBindingAndSkipsHttps()
    {
        var site = MakeSite(
            "example",
            new SiteBindingDto { Protocol = "https", HostName = "example.com", IpAddress = "*", Port = 443 },
            new SiteBindingDto { Protocol = "http", HostName = "example.com", IpAddress = "", Port = 80 },
            new SiteBindingDto { Protocol = "http", HostName = "www.example.com", IpAddress = "*", Port = 80 });

        var (create, extras) = IisSnapshotRestore.ToProvisioningPlan(site);

        Assert.Equal("example", create.Name);
        Assert.Equal("C:\\inetpub\\example", create.PhysicalPath);
        Assert.Equal("example-pool", create.ApplicationPoolName);
        Assert.Equal("example.com", create.HostName);
        Assert.Equal("*", create.IpAddress); // boş IP '*' olur
        Assert.Equal(80, create.Port);
        Assert.Equal("http", create.Protocol);
        Assert.False(create.ProvisionDefaultServices); // DR'da yeni kaynak üretilmez
        Assert.False(create.InstallRuntimeIfMissing);

        var extra = Assert.Single(extras); // https atlandı, ikinci http ek bağlama oldu
        Assert.Equal("www.example.com", extra.HostName);
        Assert.Equal(80, extra.Port);
    }

    [Fact]
    public void ToProvisioningPlan_NoHttpBindingFallsBackToPort80()
    {
        var site = MakeSite("yalniz-https", new SiteBindingDto { Protocol = "https", HostName = "x.com", Port = 443 });

        var (create, extras) = IisSnapshotRestore.ToProvisioningPlan(site);

        Assert.Equal(80, create.Port);
        Assert.Equal(string.Empty, create.HostName);
        Assert.Empty(extras);
    }

    [Fact]
    public void Snapshot_SerializationRoundTripsThroughRestoreShape()
    {
        // BackupPanelAsync'in yazdığı biçim ↔ TryRestoreIisSitesAsync'in okuduğu biçim birebir uyumlu olmalı.
        var snapshot = new[] { MakeSite("rt", new SiteBindingDto { Protocol = "http", HostName = "rt.test", IpAddress = "*", Port = 8081 }) };
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });

        var restored = JsonSerializer.Deserialize<List<SiteDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(restored);
        var site = Assert.Single(restored!);
        Assert.Equal("rt", site.Name);
        Assert.Equal("rt-pool", site.ApplicationPoolName);
        var binding = Assert.Single(site.Bindings);
        Assert.Equal(8081, binding.Port);
        Assert.Equal("rt.test", binding.HostName);
    }
}
