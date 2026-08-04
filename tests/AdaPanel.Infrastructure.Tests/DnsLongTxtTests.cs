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
    [InlineData("ZoneReload")]
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

        // Olcum artik tek bir script blogunda tanimlanip her yoldan sonra CAGIRILIYOR
        // ($adaOlcum.Invoke()) -- tekrar tekrar ayni sorguyu yazmak yerine.
        Assert.Contains("$adaOlcum =", line, StringComparison.Ordinal);
        Assert.True(System.Text.RegularExpressions.Regex.Matches(line, @"\$adaOlcum\.Invoke\(\)").Count >= 4);
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

    // CANLI BULGU: bolge dosyasina eklemek TEK BASINA yetmiyordu.
    // DNS servisi bolgeyi bellekte tutuyor; once bellek dosyaya yazilmazsa (ZoneWriteBack)
    // servis kendi kopyasini uzerine yaziyor ve eklenen satir kayboluyor -- ZoneReload
    // "basarili" diyor ama kayit ortada yok.
    [Fact]
    public void BuildTxtLine_ZoneFilePath_FlushesMemoryToDiskBeforeAppending()
    {
        var line = DnsService.BuildTxtLine("selector._domainkey", Key(400), 3600);

        var writeBack = line.IndexOf("ZoneWriteBack", StringComparison.Ordinal);
        var append = line.IndexOf("Add-Content", StringComparison.Ordinal);

        Assert.True(writeBack >= 0, "ZoneWriteBack uretilmemis");
        Assert.True(writeBack < append, "ZoneWriteBack, dosyaya eklemeden ONCE calismali");
    }

    // DNS_ERROR_ZONE_LOCKED (9607) gecicidir: senkron ayni bolgede calisirken kilit olusuyor.
    [Fact]
    public void BuildTxtLine_ZoneFilePath_RetriesWhileTheZoneIsLocked()
    {
        var line = DnsService.BuildTxtLine("selector._domainkey", Key(400), 3600);

        Assert.Contains("for ($adaDeneme", line, StringComparison.Ordinal);
        Assert.Contains("if ($adaReloadKod -eq 0) { break }", line, StringComparison.Ordinal);
    }

    // AJAN BULGUSU: dogrulama "bu konakta HERHANGI bir TXT var mi" diye soruyordu. Ayni konakta
    // baska bir TXT bulunmasi yeterdi (ornegin '@' konagindaki SPF): olcum true doner, kalan
    // yollar atlanir ve panel kayit yazilmamisken TAM BASARI raporlardi.
    [Fact]
    public void BuildTxtLine_LongValue_VerifiesOurOwnValueNotJustAnyTxtRecord()
    {
        var line = DnsService.BuildTxtLine("selector._domainkey", Key(400), 3600);

        Assert.Contains("$adaTam", line, StringComparison.Ordinal);
        Assert.Contains("DescriptiveText -join", line, StringComparison.Ordinal);
    }

    // AJAN BULGUSU: yollardan biri sonlandirici hata firlatirsa geri yukleme satirina hic
    // gelinmiyor ve $ErrorActionPreference 'Continue' olarak siziyordu. O durumda scriptin geri
    // kalanindaki hatalar ne firlar ne toplanir -- panel "onarildi" der, kayitlar yoktur.
    [Fact]
    public void BuildTxtLine_LongValue_RestoresErrorPreferenceEvenWhenAStrategyThrows()
    {
        var line = DnsService.BuildTxtLine("selector._domainkey", Key(400), 3600);

        Assert.Contains("} finally { $ErrorActionPreference = $adaEskiEap }", line, StringComparison.Ordinal);
    }

    // CANLI KOK NEDEN: uzun TXT icin gereken bolge dosyasi + ZoneReload adimlari BOLGEYI
    // KILITLIYOR. Ana senkron scriptinin icinde yapildiginda sonraki kayit islemleri sessizce
    // dusuyor ve SPF/DMARC "zaten var, silinemedi, eklenemedi" durumunda kaliyordu -- yani uzun
    // TXT cozumu senkronun GERI KALANINI bozuyordu. Uzun kayitlar artik ayri turda yaziliyor.
    [Fact]
    public void BuildTxtLine_LongValue_IsSelfContainedForItsOwnRun()
    {
        var line = DnsService.BuildTxtLine("selector._domainkey", Key(400), 3600);

        // Ayri turda calisacagi icin kendi degiskenlerini kendisi kurmali.
        Assert.Contains("$adaAd =", line, StringComparison.Ordinal);
        Assert.Contains("$adaTam =", line, StringComparison.Ordinal);
        Assert.Contains("$adaOlcum =", line, StringComparison.Ordinal);
    }
}
