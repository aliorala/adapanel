using AdaPanel.Infrastructure.Dns;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// 255 karakteri asan TXT kayitlari (DKIM'in RSA anahtari).
/// <para>
/// Canli olay: RSA DKIM kaydi HICBIR kurulumda yazilamiyordu ve tek bir yontem secip gondermek
/// ust uste UC kez yanlis cikti (cmdlet'e ayrilmamis deger, dnscmd'ye ayri parcalar, cmdlet'e
/// CRLF ile ayrilmis deger). Bu yuzden panel artik tek yola bahse girmez: bilinen yollari sirayla
/// dener ve HER BIRINDEN SONRA kaydi geri okuyarak dogrular. Ed25519 kaydi 255'in altinda kaldigi
/// icin hep yaziliyordu; "biri oluyor digeri olmuyor" tablosu bu uzunluk farkindan geliyordu.
/// </para>
/// </summary>
public sealed class DnsLongTxtTests
{
    private static string Key(int length) => "v=DKIM1; k=rsa; p=" + new string('A', length);

    /// <summary>
    /// Yalnizca CRLF ile birlestirilen ifadedeki parcalari cikarir.
    /// <para>
    /// Script birden cok yol urettigi icin (cmdlet, dnscmd, WMI) tum tirnakli metinleri toplamak
    /// yanlis olur: tam deger ve komut satiri da tirnak icinde gecer.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> ExtractChunks(string script)
    {
        var satir = script
            .Split((char)10)
            .First(line => line.TrimStart().StartsWith("$adaCrlf =", StringComparison.Ordinal));

        return System.Text.RegularExpressions.Regex.Matches(satir, "'([^']*)'")
            .Select(match => match.Groups[1].Value)
            .ToList();
    }

    [Fact]
    public void BuildTxtLine_ShortValue_StaysOnASingleString()
    {
        var line = DnsService.BuildTxtLine("@", "v=spf1 mx a ~all", 3600);

        Assert.Contains("Add-DnsServerResourceRecord", line, StringComparison.Ordinal);
        Assert.DoesNotContain("[char]13", line, StringComparison.Ordinal);
    }

    // Uzun deger icin UC yol da uretilmeli: tek bir yola bahse girmek, bu oturumda ust uste
    // uc kez yanlis cikti.
    [Theory]
    [InlineData("Add-DnsServerResourceRecord")]
    [InlineData("dnscmd")]
    [InlineData("MicrosoftDNS_TXTType")]
    public void BuildTxtLine_LongValue_OffersEveryKnownStrategy(string beklenen)
    {
        var line = DnsService.BuildTxtLine("v1-rsa-20260626._domainkey", Key(400), 3600);

        Assert.Contains(beklenen, line, StringComparison.Ordinal);
    }

    // Her denemeden sonra kayit GERI OKUNUR: "komut hata vermedi" ile "kayit yazildi" ayni sey
    // degildir -- bu oturumda dnscmd tam olarak boyle yaniltti.
    [Fact]
    public void BuildTxtLine_LongValue_VerifiesTheRecordAfterEachAttempt()
    {
        var line = DnsService.BuildTxtLine("selector._domainkey", Key(400), 3600);

        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(line, "Get-DnsServerResourceRecord").Count - 1);
    }

    // Hicbiri tutmazsa UC hatanin da bildirilmesi sart: tek hata gostermek, sonraki turu yine
    // tahmine birakirdi.
    [Fact]
    public void BuildTxtLine_LongValue_ReportsEveryFailureTogether()
    {
        var line = DnsService.BuildTxtLine("selector._domainkey", Key(400), 3600);

        Assert.Contains("$adaHata -join", line, StringComparison.Ordinal);
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
