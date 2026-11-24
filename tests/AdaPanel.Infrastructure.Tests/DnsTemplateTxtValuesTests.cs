using System.Reflection;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Dns;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class DnsTemplateTxtValuesTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData(" ", " ")]
    [InlineData("  MiXeD+/=;  ", "  MiXeD+/=;  ")]
    [InlineData("  {domain}|{ip}|{ns1}|{ns2}  ", "  example.test|192.0.2.10|ns1.example.test|ns2.example.test  ")]
    public async Task Save_reload_apply_and_zone_cache_preserve_complete_TXT(string value, string expected)
    {
        using var f = new Fixture();
        var saved = await f.Service().UpdateTemplateAsync(Request(value));
        Assert.Equal(value, Assert.Single(saved.Records).Value);
        Assert.Equal(value, Assert.Single((await f.Service().GetTemplateAsync()).Records).Value);
        var zone = await f.Service().GetZoneAsync("example.test");
        Assert.False(zone.IsWindowsDnsAvailable); // This fixture measures stored intent, not native publication.
        Assert.Equal(expected, Assert.Single(zone.Records, r => r.Host == "_proof").Value);
        Assert.Equal(expected, Assert.Single((await f.Service().GetZoneAsync("example.test")).Records, r => r.Host == "_proof").Value);
        using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(f.Store.ResolvePath("config/dns-zones.json")));
        Assert.Equal(expected, persisted.RootElement[0].GetProperty("records").EnumerateArray()
            .Single(r => r.GetProperty("host").GetString() == "_proof").GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("  MiXeD  ", "  MiXeD  ")]
    [InlineData("  {domain}  ", "  example.test  ")]
    public async Task Existing_template_is_expanded_without_trimming_or_dropping_TXT(string value, string expected)
    {
        using var f = new Fixture();
        await f.Store.WriteAsync("config/dns-template.json", new DnsTemplateDto { Records = Request(value).Records });
        Assert.Equal(expected, Assert.Single((await f.Service().GetZoneAsync("example.test")).Records, r => r.Host == "_proof").Value);
    }

    [Fact]
    public async Task Non_TXT_template_values_hosts_and_empty_row_rules_keep_their_existing_normalization()
    {
        using var f = new Fixture();
        var saved = await f.Service().UpdateTemplateAsync(new() { Records = [
            new() { Type = " a ", Host = " @ ", Value = "  {ip}  " },
            new() { Type = "CNAME", Host = " alias ", Value = "  {domain}  " },
            new() { Type = "A", Host = "missing", Value = " " }
        ] });
        Assert.Equal(2, saved.Records.Count);
        Assert.Contains(saved.Records, r => r.Type == "A" && r.Host == "@" && r.Value == "{ip}");
        Assert.Contains(saved.Records, r => r.Host == "alias" && r.Value == "{domain}");
        var zone = await f.Service().GetZoneAsync("example.test");
        Assert.Contains(zone.Records, r => r.Type == "A" && r.Host == "@" && r.Value == "192.0.2.10");
        Assert.Contains(zone.Records, r => r.Type == "CNAME" && r.Host == "alias" && r.Value == "example.test");
        Assert.DoesNotContain(zone.Records, r => r.Host == "missing");
    }

    public static IEnumerable<object?[]> InvalidValues() => new string?[] { null, "\t", "key\n", "\rkey", new('A', 513) }.Select(v => new object?[] { v });

    [Theory, MemberData(nameof(InvalidValues))]
    public async Task Invalid_TXT_save_fails_without_replacing_the_previous_template(string? value)
    {
        using var f = new Fixture();
        await f.Service().UpdateTemplateAsync(Request("previous"));
        var before = await File.ReadAllBytesAsync(f.Store.ResolvePath("config/dns-template.json"));
        var exception = await Assert.ThrowsAsync<AppException>(() => f.Service().UpdateTemplateAsync(Request(value!)));
        Assert.Equal("dns.invalid_record_value", exception.MessageKey);
        Assert.Equal(before, await File.ReadAllBytesAsync(f.Store.ResolvePath("config/dns-template.json")));
    }

    [Fact]
    public async Task Placeholder_expansion_cannot_persist_an_overlength_TXT_as_a_successful_zone()
    {
        using var f = new Fixture();
        await f.Service().UpdateTemplateAsync(Request(new string('A', 501) + "{domain}"));
        await Assert.ThrowsAsync<AppException>(() => f.Service().GetZoneAsync("example.test"));
        Assert.Empty(await f.Store.ReadAsync("config/dns-zones.json", Array.Empty<JsonElement>()));
    }

    private static UpdateDnsTemplateRequest Request(string value) => new()
    { Records = [new() { Type = "TXT", Host = "_proof", Value = value, Ttl = 3600 }] };

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-txt-template-" + Guid.NewGuid().ToString("N"));
        internal JsonFileStore Store { get; }
        internal Fixture() => Store = new(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
        internal DnsService Service()
        {
            var site = new SiteDto { Name = "example.test", Bindings = [new() { HostName = "example.test", IpAddress = "192.0.2.10" }] };
            return new DnsService(Stub<IIisSiteService>((_, _) => Task.FromResult(site)),
                Stub<IPanelPreferencesService>((_, _) => Task.FromResult(new PanelPreferencesDto { ServerAddress = "192.0.2.10" })),
                Store, NullLogger<DnsService>.Instance)
            {
                AvailabilityOverride = _ => Task.FromResult(false),
                NativeCommand = (_, _) => throw new Xunit.Sdk.XunitException("Native commands are outside this stored-template fixture.")
            };
        }
        public void Dispose()
        {
            var full = Path.GetFullPath(root);
            if (Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(full).StartsWith("adapanel-txt-template-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its root.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    { var proxy = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)proxy).Handler = handler; return proxy; }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
