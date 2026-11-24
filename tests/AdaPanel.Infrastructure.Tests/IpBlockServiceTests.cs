using System.Net;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Firewall;
using AdaPanel.Infrastructure.Firewall;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>IP/CIDR engel listesi: kilitlenme korumasi, sureli engel otomatik kaldirma, durust kural durumu.</summary>
public sealed class IpBlockServiceTests : IDisposable
{
    private readonly BulutAlani area = new();
    private readonly FakeWriter writer = new();
    private DateTime now = new(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);

    public void Dispose() => area.Dispose();

    private IpBlockService Create() => new(area.DosyaDeposu, writer, NullLogger<IpBlockService>.Instance)
    {
        LocalAddresses = () => [IPAddress.Parse("10.0.0.5"), IPAddress.Parse("fe80::1")],
        UtcNow = () => now
    };

    private static readonly string[] Admin = ["203.0.113.50"];

    private Task<IpBlockListDto> List() => Create().GetAsync(area.Iptal);

    [Fact]
    public async Task AddingASingleAddressWritesAnOwnedRuleAndReadsItBack()
    {
        var entry = await Create().AddAsync(new AddIpBlockRequest { Target = "198.51.100.7", Reason = "scanner" }, "admin", Admin, area.Iptal);

        Assert.Equal("198.51.100.7/32", entry.Target);
        Assert.Equal("applied", entry.RuleState);
        Assert.StartsWith("AdaPanel IpBlock ", entry.RuleName, StringComparison.Ordinal);
        Assert.Equal([entry.RuleName], writer.Applied);
        Assert.Single((await List()).Entries);
        Assert.False((await List()).CountryBlockSupported);
    }

    [Theory]
    [InlineData("203.0.113.50")]            // the administrator session address itself
    [InlineData("203.0.113.0/24")]          // a range containing it
    [InlineData("::ffff:203.0.113.50")]     // IPv4-mapped spelling of the same address
    [InlineData("10.0.0.5")]                // the panel's own interface
    [InlineData("10.0.0.0/8")]              // range containing the panel's own interface
    [InlineData("127.0.0.1")]               // loopback
    [InlineData("fe80::/32")]               // IPv6 range containing a local address
    public async Task ATargetCoveringTheAdminOrPanelAddressIsRefusedAndNothingIsWritten(string target)
    {
        var error = await Assert.ThrowsAsync<AppException>(() =>
            Create().AddAsync(new AddIpBlockRequest { Target = target }, "admin", Admin, area.Iptal));

        Assert.Equal("firewall.ipblock_would_lock_out", error.MessageKey);
        Assert.Empty(writer.Applied);
        Assert.Empty((await List()).Entries);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("1.2.3.4/33")]
    [InlineData("1.2.3.4/-1")]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("1.2.3.4, 5.6.7.8")]
    [InlineData("1.2.3.4'; calc; '")]
    public async Task MalformedTargetsAreRejectedBeforeAnyRuleIsWritten(string target)
    {
        var error = await Assert.ThrowsAsync<AppException>(() =>
            Create().AddAsync(new AddIpBlockRequest { Target = target }, "admin", Admin, area.Iptal));
        Assert.Equal("firewall.ipblock_target_invalid", error.MessageKey);
        Assert.Empty(writer.Applied);
    }

    [Theory]
    [InlineData("10.0.0.0/7")]
    [InlineData("1.0.0.0/1")]
    [InlineData("2001::/31")]
    [InlineData("::/8")]
    public async Task ARangeWiderThanIpv4Slash8OrIpv6Slash32IsRefused(string target)
    {
        var error = await Assert.ThrowsAsync<AppException>(() =>
            Create().AddAsync(new AddIpBlockRequest { Target = target }, "admin", Admin, area.Iptal));
        Assert.Equal("firewall.ipblock_range_too_wide", error.MessageKey);
        Assert.Empty(writer.Applied);
    }

    [Theory]
    [InlineData("198.0.0.0/8")]
    [InlineData("2001:db8::/32")]
    public async Task TheMinimumAllowedPrefixesAreAccepted(string target)
    {
        var entry = await Create().AddAsync(new AddIpBlockRequest { Target = target }, "admin", Admin, area.Iptal);
        Assert.Equal("applied", entry.RuleState);
    }

    [Fact]
    public async Task ACountryBlockIsRefusedBecauseThereIsNoGeoIpSource()
    {
        var error = await Assert.ThrowsAsync<AppException>(() =>
            Create().AddAsync(new AddIpBlockRequest { Target = "198.51.100.7", CountryCode = "RU" }, "admin", Admin, area.Iptal));
        Assert.Equal("firewall.geoip_unavailable", error.MessageKey);
        Assert.Empty(writer.Applied);
    }

    [Fact]
    public async Task ARuleWriteFailureIsRecordedAndReportedAsNotEnforced()
    {
        writer.ApplyFails = true;
        var entry = await Create().AddAsync(new AddIpBlockRequest { Target = "198.51.100.7" }, "admin", Admin, area.Iptal);

        Assert.Equal("failed", entry.RuleState);
        Assert.Contains("denied", entry.RuleError);
        Assert.Equal("failed", Assert.Single((await List()).Entries).RuleState);
    }

    [Fact]
    public async Task ADuplicateTargetIsRefused()
    {
        var service = Create();
        await service.AddAsync(new AddIpBlockRequest { Target = "198.51.100.0/24" }, "admin", Admin, area.Iptal);
        var error = await Assert.ThrowsAsync<AppException>(() =>
            service.AddAsync(new AddIpBlockRequest { Target = "198.51.100.77/24" }, "admin", Admin, area.Iptal));
        Assert.Equal("firewall.ipblock_duplicate", error.MessageKey);
    }

    [Fact]
    public async Task ATimedBanIsRemovedAutomaticallyOnceItExpires_AndOnlyThen()
    {
        var service = Create();
        var timed = await service.AddAsync(new AddIpBlockRequest { Target = "198.51.100.7", DurationMinutes = 30 }, "admin", Admin, area.Iptal);
        var permanent = await service.AddAsync(new AddIpBlockRequest { Target = "198.51.100.8" }, "admin", Admin, area.Iptal);
        Assert.Equal(now.AddMinutes(30), timed.ExpiresAtUtc);
        Assert.Null(permanent.ExpiresAtUtc);

        now = now.AddMinutes(29);
        Assert.Empty(await service.SweepExpiredAsync(area.Iptal));
        Assert.Equal(2, (await List()).Entries.Count);

        now = now.AddMinutes(2);
        var removed = Assert.Single(await service.SweepExpiredAsync(area.Iptal));

        Assert.Equal(timed.Id, removed.Id);
        Assert.Equal([timed.RuleName], writer.Removed);
        Assert.Equal(permanent.Id, Assert.Single((await List()).Entries).Id);
    }

    [Fact]
    public async Task AnExpiredBanWhoseRuleCannotBeRemovedIsKeptForRetryNotForgotten()
    {
        var service = Create();
        await service.AddAsync(new AddIpBlockRequest { Target = "198.51.100.7", DurationMinutes = 1 }, "admin", Admin, area.Iptal);
        now = now.AddMinutes(5);
        writer.RemoveFails = true;

        Assert.Empty(await service.SweepExpiredAsync(area.Iptal));
        Assert.Single((await List()).Entries);

        writer.RemoveFails = false;
        Assert.Single(await service.SweepExpiredAsync(area.Iptal));
        Assert.Empty((await List()).Entries);
    }

    [Fact]
    public async Task ManualRemovalKeepsTheEntryWhenTheRuleCannotBeVerifiedGone()
    {
        var service = Create();
        var entry = await service.AddAsync(new AddIpBlockRequest { Target = "198.51.100.7" }, "admin", Admin, area.Iptal);
        writer.RemoveFails = true;

        await Assert.ThrowsAsync<AppException>(() => service.RemoveAsync(entry.Id, area.Iptal));
        Assert.Single((await List()).Entries);

        writer.RemoveFails = false;
        await service.RemoveAsync(entry.Id, area.Iptal);
        Assert.Empty((await List()).Entries);
    }

    [Fact]
    public async Task TheExpiryWorkerWritesAnAuditRecordForEveryAutomaticRemoval()
    {
        var service = Create();
        await service.AddAsync(new AddIpBlockRequest { Target = "198.51.100.7", DurationMinutes = 1 }, "admin", Admin, area.Iptal);
        now = now.AddMinutes(2);

        await IpBlockExpiryWorker.SweepOnceAsync(service, area.Denetim, area.Iptal);

        Assert.Contains(area.Denetim.Kayitlar, record => record.Action == "firewall.ipblock.expired"
            && record.Details.Contains("198.51.100.7/32", StringComparison.Ordinal));
    }

    [Fact]
    public void NetworkContainmentMatchesPrefixBoundaries()
    {
        var network = IpBlockService.ParseNetwork("192.168.4.0/22");
        Assert.True(network.Contains(IPAddress.Parse("192.168.7.255")));
        Assert.False(network.Contains(IPAddress.Parse("192.168.8.0")));
        Assert.False(network.Contains(IPAddress.Parse("2001:db8::1")));
    }

    private sealed class FakeWriter : IIpBlockRuleWriter
    {
        internal readonly List<string> Applied = [];
        internal readonly List<string> Removed = [];
        internal bool ApplyFails;
        internal bool RemoveFails;

        public Task<(bool Applied, string Error)> ApplyAsync(string ruleName, string network, CancellationToken cancellationToken)
        {
            if (ApplyFails) return Task.FromResult((false, "access denied"));
            Applied.Add(ruleName);
            return Task.FromResult((true, string.Empty));
        }

        public Task<(bool Removed, string Error)> RemoveAsync(string ruleName, CancellationToken cancellationToken)
        {
            if (RemoveFails) return Task.FromResult((false, "still present"));
            Removed.Add(ruleName);
            return Task.FromResult((true, string.Empty));
        }
    }
}
