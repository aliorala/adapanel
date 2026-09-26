using System.Reflection;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Dns;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class DnsReadOnlyInspectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dry_run_does_not_create_or_upgrade_zone_files(bool existing)
    {
        var root = Path.Combine(Path.GetTempPath(), "adapanel-readonly-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "config", "dns-zones.json");
        const string original = """
            [{"siteName":"example.test","zoneName":"example.test","defaultsVersion":0,"records":[]}]
            """;
        if (existing)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, original);
        }
        try
        {
            var sites = Stub<IIisSiteService>((_, _) => Task.FromResult(new SiteDto { Id = 1, Name = "example.test" }));
            var preferences = Stub<IPanelPreferencesService>((_, _) => Task.FromResult(
                new PanelPreferencesDto { ServerAddress = "192.0.2.10" }));
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            var dns = new DnsService(sites, preferences, store, NullLogger<DnsService>.Instance);
            var missing = await dns.RepairDefaultsAsync("example.test", applyChanges: false);
            if (existing)
            {
                Assert.NotEmpty(missing);
                Assert.Equal(original, await File.ReadAllTextAsync(path));
            }
            else Assert.False(File.Exists(path));
        }
        finally
        {
            // Only files created by this test; no recursive deletion or computed external target.
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
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
