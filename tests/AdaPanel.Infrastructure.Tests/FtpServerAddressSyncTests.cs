using System.Reflection;
using System.Text.Json;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Dns;
using AdaPanel.Infrastructure.Ftp;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FtpServerAddressSyncTests
{
    [Theory]
    [InlineData("84.51.22.239", true, false, true, "176.43.253.97")]
    [InlineData("176.43.253.97", true, false, false, "176.43.253.97")]
    [InlineData("203.0.113.8", false, true, false, "203.0.113.8")]
    [InlineData("", false, true, false, "")]
    public async Task Exact_old_migrates_and_other_values_are_preserved(string current, bool success, bool skipped, bool changed, string expected)
    {
        using var f = new Fixture { Current = current };
        var result = await f.Service.SyncServerAddressAsync("84.51.22.239", "176.43.253.97");
        Assert.Equal(success, result.Succeeded); Assert.Equal(skipped, result.Skipped); Assert.Equal(changed, result.Changed);
        Assert.Equal(expected, result.CurrentAddress); Assert.Equal(expected, f.Current);
        Assert.Equal(changed ? 1 : 0, f.Writes);
        if (changed) Assert.Single(Directory.GetFiles(f.Store.ResolvePath("config/ftp-address-backups")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Missing_or_foreign_FTP_site_is_skipped_without_native_write(bool exists, bool owned)
    {
        using var f = new Fixture { Exists = exists, Owned = owned };
        var result = await f.Service.SyncServerAddressAsync("84.51.22.239", "176.43.253.97");
        Assert.True(result.Skipped); Assert.False(result.Succeeded); Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("malformed")]
    [InlineData("write")]
    [InlineData("readback")]
    public async Task Failed_or_unmeasured_native_configuration_never_reports_success(string fail)
    {
        using var f = new Fixture { Failure = fail };
        var result = await f.Service.SyncServerAddressAsync("84.51.22.239", "176.43.253.97");
        Assert.False(result.Succeeded); Assert.False(result.Skipped); Assert.NotEmpty(result.Error);
    }

    [Fact]
    public async Task Concurrent_retries_use_infrastructure_lock_and_only_write_once()
    {
        using var f = new Fixture();
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => f.Service.SyncServerAddressAsync("84.51.22.239", "176.43.253.97")));
        Assert.All(results, result => Assert.True(result.Succeeded)); Assert.Equal(1, f.Writes);
    }

    [Fact]
    public async Task Dns_job_persists_FTP_failure_and_retries_even_when_no_DNS_records_remain_to_migrate()
    {
        using var f = new Fixture();
        var fails = true;
        var attempts = 0;
        var ftp = DispatchProxy.Create<IFtpService, Proxy>();
        ((Proxy)(object)ftp).Handler = (_, _) => {
            attempts++;
            return Task.FromResult(fails ? new FtpServerAddressSyncDto { Error = "Native readback failed." }
                : new FtpServerAddressSyncDto { Succeeded = true, CurrentAddress = "176.43.253.97" });
        };
        DnsService Service() => new(null!, null!, f.Store, NullLogger<DnsService>.Instance, ftp);
        var failed = await Service().SyncServerAddressAsync("84.51.22.239", "176.43.253.97");
        Assert.True(failed.Pending); Assert.False(failed.Ftp!.Succeeded);
        Assert.NotNull((await Service().GetServerAddressDnsSyncAsync())!.Ftp);
        fails = false;
        var retry = await Service().SyncServerAddressAsync("84.51.22.239", "176.43.253.97");
        Assert.False(retry.Pending); Assert.True(retry.Ftp!.Succeeded); Assert.Equal(2, attempts);
    }

    private sealed class Fixture : IDisposable
    {
        private string Root { get; } = Path.Combine(Path.GetTempPath(), "adapanel-ftp-address-" + Guid.NewGuid().ToString("N"));
        internal JsonFileStore Store { get; }
        internal FtpService Service { get; }
        internal string Current = "84.51.22.239";
        internal bool Exists = true;
        internal bool Owned = true;
        internal string Failure = "";
        internal int Writes;
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root });
            Store = new JsonFileStore(options);
            Service = new FtpService(null!, Store, null!, options) { NativeScript = Run };
        }
        private async Task<CommandResult> Run(string script, bool strict, CancellationToken token)
        {
            Assert.True(strict);
            Assert.Contains("AdaPanel FTP", script);
            Assert.Contains(Path.Combine(Root, "ftp-root"), script);
            Assert.DoesNotContain("Restart-Service", script);
            Assert.DoesNotContain("New-WebFtpSite", script);
            Assert.DoesNotContain("dataChannelPort", script);
            Assert.DoesNotContain("ssl", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", script, StringComparison.OrdinalIgnoreCase);
            await Task.Yield();
            if (script.Contains("ADAPANEL-FTP-ADDRESS-WRITE"))
            {
                Assert.Contains("GetAttributeValue('externalIp4Address') -cne '84.51.22.239'", script);
                Assert.Contains("SetAttributeValue('externalIp4Address', '176.43.253.97')", script);
                Assert.Contains("CommitChanges()", script);
                Writes++;
                if (Failure == "write") return new CommandResult { ExitCode = 1 };
                if (Failure != "readback") Current = "176.43.253.97";
                return new CommandResult { ExitCode = 0 };
            }
            if (Failure == "read") return new CommandResult { ExitCode = 1 };
            return new CommandResult { ExitCode = 0, StandardOutput = Failure == "malformed" ? "{}"
                : JsonSerializer.Serialize(new { Exists, Owned, Address = Current }) };
        }
        public void Dispose() => Directory.Delete(Root, true);
    }

    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
