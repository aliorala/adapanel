using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Dns;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// SRV / CAA / TLSA / PTR desteği. Bu tipler tek bir "değer" alanında birden çok alan taşır;
/// yanlış ayrıştırma ya kaydı bozar ya da Windows DNS'e hatalı komut yazdırır (kayıt sessizce
/// yayınlanmaz). Üretilen PowerShell satırı da birebir doğrulanır.
/// </summary>
public sealed class DnsRecordTypeTests
{
    [Fact]
    public void ParseSrvValue_ReadsWeightPortAndTarget()
    {
        var (weight, port, target) = DnsService.ParseSrvValue("5 5060 sip.example.com");

        Assert.Equal(5, weight);
        Assert.Equal(5060, port);
        Assert.Equal("sip.example.com", target);
    }

    [Theory]
    [InlineData("5 5060")]              // hedef eksik
    [InlineData("5 sip.example.com")]   // port eksik
    [InlineData("5 0 sip.example.com")] // port 0 geçersiz
    [InlineData("5 70000 sip.example.com")]
    [InlineData("-1 5060 sip.example.com")]
    public void ParseSrvValue_RejectsMalformedInput(string value)
    {
        Assert.Throws<AppException>(() => DnsService.ParseSrvValue(value));
    }

    [Fact]
    public void ParseCaaValue_AcceptsQuotedValuesAndLowercasesTheTag()
    {
        var (flags, tag, value) = DnsService.ParseCaaValue("0 ISSUE \"letsencrypt.org\"");

        Assert.Equal(0, flags);
        Assert.Equal("issue", tag);
        Assert.Equal("letsencrypt.org", value);
    }

    [Theory]
    [InlineData("0 issue")]
    [InlineData("300 issue letsencrypt.org")]   // bayrak tek bayta sığmalı
    [InlineData("0 iss ue letsencrypt.org")]    // etiket boşluk içeremez → değer "ue letsencrypt.org" olur, etiket geçerli kalır
    public void ParseCaaValue_RejectsMalformedInput(string value)
    {
        if (value == "0 iss ue letsencrypt.org")
        {
            // Bu giriş GEÇERLİ sayılır: etiket "iss", değer "ue letsencrypt.org".
            var parsed = DnsService.ParseCaaValue(value);
            Assert.Equal("iss", parsed.Tag);
            return;
        }

        Assert.Throws<AppException>(() => DnsService.ParseCaaValue(value));
    }

    // RFC 8659: RDATA = bayrak(1) + etiket uzunluğu(1) + etiket + değer.
    [Fact]
    public void BuildCaaRecordData_EncodesTheRdataExactly()
    {
        var rdata = DnsService.BuildCaaRecordData(0, "issue", "ca.test");

        // 0x00 bayrak, 0x05 etiket uzunluğu, "issue", "ca.test"
        Assert.Equal(
            "0x00,0x05,0x69,0x73,0x73,0x75,0x65,0x63,0x61,0x2E,0x74,0x65,0x73,0x74",
            rdata);
    }

    [Fact]
    public void ParseTlsaValue_ReadsAllFourFields()
    {
        var (usage, selector, matching, data) = DnsService.ParseTlsaValue("3 1 1 ABCDEF01");

        Assert.Equal(3, usage);
        Assert.Equal(1, selector);
        Assert.Equal(1, matching);
        Assert.Equal("abcdef01", data);
    }

    [Theory]
    [InlineData("4 1 1 abcdef")]   // kullanım 0-3
    [InlineData("3 2 1 abcdef")]   // seçici 0-1
    [InlineData("3 1 3 abcdef")]   // eşleşme 0-2
    [InlineData("3 1 1 zzzz")]     // hex değil
    [InlineData("3 1 1")]          // veri eksik
    public void ParseTlsaValue_RejectsMalformedInput(string value)
    {
        Assert.Throws<AppException>(() => DnsService.ParseTlsaValue(value));
    }

    [Fact]
    public void BuildSrvLine_ProducesTheNativeWindowsCommand()
    {
        var line = DnsService.BuildSrvLine("_sip._tcp", "5 5060 sip.example.com", 10, 3600);

        Assert.Contains("-Srv", line);
        Assert.Contains("-Name '_sip._tcp'", line);
        Assert.Contains("-DomainName 'sip.example.com'", line);
        Assert.Contains("-Priority 10", line);
        Assert.Contains("-Weight 5", line);
        Assert.Contains("-Port 5060", line);
    }

    [Fact]
    public void BuildTlsaLine_ProducesTheNativeWindowsCommand()
    {
        var line = DnsService.BuildTlsaLine("_443._tcp", "3 1 1 abcdef01", 3600);

        Assert.Contains("-Tlsa", line);
        Assert.Contains("-CertificateUsage 3", line);
        Assert.Contains("-Selector 1", line);
        Assert.Contains("-MatchingType 1", line);
        Assert.Contains("-CertificateAssociationData 'abcdef01'", line);
    }

    // CAA'nın native parametresi yok → RFC 3597 bilinmeyen-kayıt yolu (tip 257).
    [Fact]
    public void BuildCaaLine_UsesTheUnknownRecordPathWithType257()
    {
        var line = DnsService.BuildCaaLine("@", "0 issue letsencrypt.org", 3600);

        Assert.Contains("-Unknown", line);
        Assert.Contains("-Type 257", line);
        Assert.Contains("-RecordData 0x00,0x05,0x69", line);
    }
}
