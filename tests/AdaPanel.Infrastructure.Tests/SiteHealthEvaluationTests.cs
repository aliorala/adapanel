using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Infrastructure.Health;
using System.Net.Security;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Dışarıdan bakan site sağlığının KARAR mantığı. 2026-07-23 olayının dersi burada test edilir:
/// alan adı halka açık çözümleyicilerden çözülmüyorsa sonuç "ok" OLAMAZ; ölçülemeyen bir kontrol
/// de asla "ok"a terfi etmemelidir.
/// </summary>
public sealed class SiteHealthEvaluationTests
{
    [Fact]
    public void EvaluateResolvers_FailsWhenNoPublicResolverCanAnswer()
    {
        var (status, value, addresses, _) = SiteHealthService.EvaluateResolvers(
        [
            new SiteHealthService.DnsResolverProbe { Server = "1.1.1.1", Addresses = [], Error = "DNS name does not exist" },
            new SiteHealthService.DnsResolverProbe { Server = "8.8.8.8", Addresses = [], Error = "DNS name does not exist" }
        ]);

        Assert.Equal("fail", status);
        Assert.Equal("çözülmüyor", value);
        Assert.Empty(addresses);
    }

    [Fact]
    public void EvaluateResolvers_WarnsWhenOnlyOneResolverAnswers()
    {
        var (status, _, addresses, _) = SiteHealthService.EvaluateResolvers(
        [
            new SiteHealthService.DnsResolverProbe { Server = "1.1.1.1", Addresses = ["185.7.242.225"] },
            new SiteHealthService.DnsResolverProbe { Server = "8.8.8.8", Addresses = [], Error = "timeout" }
        ]);

        Assert.Equal("warn", status);
        Assert.Equal(["185.7.242.225"], addresses);
    }

    [Fact]
    public void EvaluateResolvers_DeduplicatesAddressesAcrossResolvers()
    {
        var (status, value, addresses, _) = SiteHealthService.EvaluateResolvers(
        [
            new SiteHealthService.DnsResolverProbe { Server = "1.1.1.1", Addresses = ["185.7.242.225"] },
            new SiteHealthService.DnsResolverProbe { Server = "8.8.8.8", Addresses = ["185.7.242.225"] }
        ]);

        Assert.Equal("ok", status);
        Assert.Equal("185.7.242.225", value);
        Assert.Single(addresses);
    }

    [Fact]
    public void EvaluateResolvers_ReturnsUnknownWhenNothingWasQueried()
    {
        var (status, _, _, _) = SiteHealthService.EvaluateResolvers([]);
        Assert.Equal("unknown", status);
    }

    [Fact]
    public void EvaluateServerMatch_FailsWhenDomainPointsElsewhere()
    {
        var (status, value, _) = SiteHealthService.EvaluateServerMatch(["104.21.0.1"], "185.7.242.225");

        Assert.Equal("fail", status);
        Assert.Equal("104.21.0.1", value);
    }

    [Fact]
    public void EvaluateServerMatch_IsOkWhenAnyResolvedAddressMatches()
    {
        var (status, _, _) = SiteHealthService.EvaluateServerMatch(["104.21.0.1", "185.7.242.225"], " 185.7.242.225 ");
        Assert.Equal("ok", status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("panel.example.com")]
    public void EvaluateServerMatch_IsUnknownWithoutAComparableServerIp(string serverAddress)
    {
        var (status, _, _) = SiteHealthService.EvaluateServerMatch(["185.7.242.225"], serverAddress);
        Assert.Equal("unknown", status);
    }

    [Fact]
    public void EvaluateServerMatch_IsUnknownWhenDnsDidNotResolve()
    {
        var (status, _, _) = SiteHealthService.EvaluateServerMatch([], "185.7.242.225");
        Assert.Equal("unknown", status);
    }

    [Theory]
    [InlineData(200, "ok")]
    [InlineData(301, "ok")]
    [InlineData(302, "ok")]
    [InlineData(403, "warn")]
    [InlineData(404, "warn")]
    [InlineData(500, "fail")]
    [InlineData(503, "fail")]
    [InlineData(0, "unknown")]
    public void EvaluateHttpStatus_MapsCodesToHealth(int statusCode, string expected)
        => Assert.Equal(expected, SiteHealthService.EvaluateHttpStatus(statusCode));

    [Theory]
    [InlineData(null, false, true, "fail")]   // hiç sertifika yok
    [InlineData(-1, false, true, "fail")]     // süresi dolmuş
    [InlineData(60, true, true, "fail")]      // alan adıyla eşleşmiyor → tarayıcı uyarısı
    [InlineData(10, false, true, "warn")]     // yakında bitiyor
    [InlineData(60, false, false, "warn")]    // kayıt var ama 443'te sunulmuyor
    [InlineData(60, false, true, "ok")]
    public void EvaluateCertificateStatus_CoversTheRealFailureModes(int? daysLeft, bool nameMismatch, bool live, string expected)
        => Assert.Equal(expected, SiteHealthService.EvaluateCertificateStatus(daysLeft, nameMismatch, live));

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable)]
    public void CertificatePolicyErrorsCannotBeHiddenByAValidExpiry(SslPolicyErrors errors)
        => Assert.Equal("fail", SiteHealthService.EvaluateCertificateStatus(60, false, true, errors));

    [Fact]
    public void UnmeasuredMailDnsIsNotReportedAsMissingRecords()
    {
        var records = SiteHealthService.BuildMailChecks(null);
        Assert.Equal(4, records.Count);
        Assert.All(records, record => Assert.Equal("unknown", record.Status));
        var summary = SiteHealthService.BuildMailSummary(records);
        Assert.Equal("unknown", summary.Status);
        Assert.Equal("ölçülemedi", summary.Value);

        var measuredEmpty = SiteHealthService.BuildMailChecks(new SiteHealthService.DnsProbe());
        Assert.All(measuredEmpty, record => Assert.Equal("fail", record.Status));
        Assert.Equal("yok", SiteHealthService.BuildMailSummary(measuredEmpty).Value);
    }

    [Fact]
    public void DaysLeft_CountsWholeDaysAndGoesNegativeAfterExpiry()
    {
        var now = new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(30, SiteHealthService.DaysLeft(now.AddDays(30), now));
        Assert.Equal(-2, SiteHealthService.DaysLeft(now.AddDays(-2), now));
    }

    [Fact]
    public void EvaluateMail_IsUnknownWhenTheDomainHostsNoMailAtAll()
    {
        var (status, value) = SiteHealthService.EvaluateMail(false, false, false, false);

        Assert.Equal("unknown", status);
        Assert.Equal("yok", value);
    }

    [Fact]
    public void EvaluateMail_WarnsAboutMissingDeliverabilityRecords()
    {
        var (status, value) = SiteHealthService.EvaluateMail(true, true, false, false);

        Assert.Equal("warn", status);
        Assert.Contains("DKIM", value);
        Assert.Contains("DMARC", value);
    }

    [Fact]
    public void EvaluateMail_IsOkWhenEveryRecordExists()
    {
        var (status, _) = SiteHealthService.EvaluateMail(true, true, true, true);
        Assert.Equal("ok", status);
    }

    [Fact]
    public void CombineStatus_NeverPromotesUnknownToOk()
    {
        Assert.Equal("unknown", SiteHealthService.CombineStatus(["unknown", "unknown"]));
        Assert.Equal("ok", SiteHealthService.CombineStatus(["ok", "unknown"]));
        Assert.Equal("warn", SiteHealthService.CombineStatus(["ok", "warn", "unknown"]));
        Assert.Equal("fail", SiteHealthService.CombineStatus(["ok", "warn", "fail"]));
        Assert.Equal("unknown", SiteHealthService.CombineStatus([]));
    }

    [Fact]
    public void ParseDnsProbe_ReadsThePowerShellPayload()
    {
        const string json = """
            {"resolvers":[{"server":"1.1.1.1","addresses":["185.7.242.225"],"error":""},
            {"server":"8.8.8.8","addresses":[],"error":"timeout"}],
            "mxRecords":["mail.example.com"],"spf":"v=spf1 a mx -all","dkim":"","dmarc":"v=DMARC1; p=none","notes":[]}
            """;

        var probe = SiteHealthService.ParseDnsProbe(json);

        Assert.NotNull(probe);
        Assert.Equal(2, probe!.Resolvers!.Length);
        Assert.Equal(["185.7.242.225"], probe.Resolvers[0].Addresses!);
        Assert.Equal("timeout", probe.Resolvers[1].Error);
        Assert.Equal(["mail.example.com"], probe.MxRecords!);
        Assert.Equal("v=spf1 a mx -all", probe.Spf);
        Assert.Empty(probe.Dkim!);
    }

    [Fact]
    public void ParseDnsProbe_ToleratesNoiseBeforeTheJsonAndGarbage()
    {
        Assert.NotNull(SiteHealthService.ParseDnsProbe("WARNING: something\r\n{\"spf\":\"v=spf1\"}"));
        Assert.Null(SiteHealthService.ParseDnsProbe("no json here"));
        Assert.Null(SiteHealthService.ParseDnsProbe(null));
    }

    [Fact]
    public void ResolveDomain_PrefersTheBindingHostName()
    {
        var site = new SiteDto
        {
            Name = "Ornek Site",
            Bindings =
            [
                new SiteBindingDto { Protocol = "http", HostName = "", Port = 80 },
                new SiteBindingDto { Protocol = "http", HostName = "Ornek.COM.", Port = 80 }
            ]
        };

        Assert.Equal("ornek.com", SiteHealthService.ResolveDomain(site));
    }

    [Fact]
    public void ResolveDomain_FallsBackToTheSiteNameOnlyWhenItLooksLikeADomain()
    {
        Assert.Equal("ornek.com", SiteHealthService.ResolveDomain(new SiteDto { Name = "ornek.com" }));
        Assert.Equal(string.Empty, SiteHealthService.ResolveDomain(new SiteDto { Name = "Default Web Site" }));
    }

    [Theory]
    [InlineData("ornek.com", true)]
    [InlineData("default._domainkey.ornek.com", true)]
    [InlineData("_dmarc.ornek.com", true)]
    [InlineData("ornek", false)]
    [InlineData("ornek.com'; Remove-Item C:\\ -Recurse; '", false)]
    [InlineData("ornek com", false)]
    [InlineData("", false)]
    public void IsSafeDnsName_RejectsAnythingThatIsNotAPlainDnsName(string value, bool expected)
        => Assert.Equal(expected, SiteHealthService.IsSafeDnsName(value));

    [Fact]
    public void FindStoredCertificate_PrefersTheHostMatchOverTheSiteMatch()
    {
        SslCertificateDto[] certificates =
        [
            new() { SiteName = "ornek", HostName = "eski.com" },
            new() { SiteName = "baska", HostName = "ornek.com" }
        ];

        Assert.Equal("ornek.com", SiteHealthService.FindStoredCertificate(certificates, "ornek", "ornek.com")!.HostName);
        Assert.Equal("eski.com", SiteHealthService.FindStoredCertificate(certificates, "ornek", "yok.com")!.HostName);
        Assert.Null(SiteHealthService.FindStoredCertificate(certificates, "hicbiri", "hicbiri.com"));
    }
}
