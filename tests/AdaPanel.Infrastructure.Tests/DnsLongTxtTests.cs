using AdaPanel.Infrastructure.Dns;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// 255 karakteri asan TXT kayitlari (DKIM'in RSA anahtari).
/// <para>
/// Canli olay: RSA DKIM kaydi HICBIR kurulumda yazilamiyordu. Once cmdlet uzun degeri reddediyor
/// sanildi ve dnscmd'e gecildi; dnscmd de birden cok tirnakli parcayi kabul etmeyip
/// ERROR_INVALID_PARAMETER (87) dondurdu. Dogru yol, cmdlet'e TEK bir metin verip parcalari
/// CRLF ile ayirmakmis -- Windows DNS her CRLF ayrimini ayri bir character-string olarak kaydeder.
/// Ed25519 kaydi kisa oldugu icin hep yaziliyordu; "biri oluyor digeri olmuyor" tablosu bundandi.
/// </para>
/// </summary>
public sealed class DnsLongTxtTests
{
    private static string Key(int length) => "v=DKIM1; k=rsa; p=" + new string('A', length);

    /// <summary>Uretilen komuttaki tek tirnakli parcalari cikarir.</summary>
    private static IReadOnlyList<string> ExtractChunks(string line) =>
        System.Text.RegularExpressions.Regex.Matches(line, "'([^']*)'")
            .Select(match => match.Groups[1].Value)
            .Where(value => !value.EndsWith("._domainkey", StringComparison.Ordinal) && value != "@")
            .ToList();

    [Fact]
    public void BuildTxtLine_ShortValue_StaysOnASingleString()
    {
        var line = DnsService.BuildTxtLine("@", "v=spf1 mx a ~all", 3600);

        Assert.Contains("Add-DnsServerResourceRecord", line, StringComparison.Ordinal);
        Assert.DoesNotContain("[char]13", line, StringComparison.Ordinal);
    }

    // dnscmd YOLU TERK EDILDI: belgelenen imzasi tek bir <rrdata> alir.
    [Fact]
    public void BuildTxtLine_LongValue_DoesNotUseDnscmd()
    {
        var line = DnsService.BuildTxtLine("v1-rsa-20260626._domainkey", Key(400), 3600);

        Assert.DoesNotContain("dnscmd", line, StringComparison.Ordinal);
        Assert.Contains("Add-DnsServerResourceRecord", line, StringComparison.Ordinal);
    }

    // Parcalar CRLF ile ayrilmali: Windows DNS her ayrimi ayri bir character-string yapar.
    [Fact]
    public void BuildTxtLine_LongValue_JoinsChunksWithCarriageReturnLineFeed()
    {
        var line = DnsService.BuildTxtLine("selector._domainkey", Key(400), 3600);

        Assert.Contains("[char]13 + [char]10", line, StringComparison.Ordinal);
    }

    // 252, 255 DEGIL: CRLF de parcanin icinde sayiliyor.
    [Fact]
    public void BuildTxtLine_ChunksStayWithinTheProtocolLimitIncludingTheSeparator()
    {
        foreach (var chunk in ExtractChunks(DnsService.BuildTxtLine("selector._domainkey", Key(700), 3600)))
        {
            Assert.True(chunk.Length <= 252, $"parca cok uzun: {chunk.Length}");
        }
    }

    [Fact]
    public void BuildTxtLine_KeepsEveryCharacterOfTheOriginalValue()
    {
        var key = Key(600);

        Assert.Equal(key, string.Concat(ExtractChunks(DnsService.BuildTxtLine("selector._domainkey", key, 3600))));
    }

    // TTL kayitla birlikte yazilmali: dnscmd yolunda kaybediliyordu.
    [Fact]
    public void BuildTxtLine_LongValue_StillSetsTheTimeToLive()
    {
        var line = DnsService.BuildTxtLine("selector._domainkey", Key(400), 3600);

        Assert.Contains("FromSeconds(3600)", line, StringComparison.Ordinal);
    }
}
