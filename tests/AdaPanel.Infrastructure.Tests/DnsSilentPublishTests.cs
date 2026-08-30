using System.Reflection;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Dns;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// DNS yazma yolu "Windows DNS yok" durumunu SESSIZCE YUTMAZ.
/// </summary>
/// <remarks>
/// <para>
/// OLCULEN DURUM: <c>DnsService.TrySyncWindowsDnsAsync</c> Windows DNS rolu kurulu degilken log,
/// istisna ve donus degeri OLMADAN <c>return</c> ediyordu. Dokuz cagiranin hepsi islemi basarili
/// sayip 200 + <c>dns.record_saved</c> donduruyordu. En gorunur sonucu site olusturma ekraniydi:
/// "dns: blog.ornek.com A 1.2.3.4" yaziyor, DNS'te hicbir sey olmuyordu.
/// </para>
/// <para>
/// Ilginc olan, dogru olcumun ayni sinifin ICINDE zaten var olmasiydi (53 portu dinleyici
/// olcumu, <c>GetServerHealthAsync</c>); yazma yolu ona hic sormuyordu.
/// </para>
/// </remarks>
public sealed class DnsSilentPublishTests
{
    /// <summary>
    /// Yayin yolu SONUC DONDURMELI. <c>Task</c> donen bir imza, "yayinlandi mi" sorusunu
    /// cagirana sormanin hicbir yolunu birakmaz -- kapatilan acigin tam sekli buydu.
    /// </summary>
    [Fact]
    public void ThePublishPathReportsAnOutcome()
    {
        var method = typeof(DnsService).GetMethod(
            "TrySyncWindowsDnsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<string>), method!.ReturnType);
    }

    /// <summary>
    /// "Panele yazildi" ile "DNS'te var" AYRI alanlarda tasinir. Tek bir <c>Created</c> bayragi,
    /// ikinci olguyu VARSAYMAK demekti.
    /// </summary>
    [Fact]
    public void TheSubdomainResultCarriesThePublishOutcomeSeparately()
    {
        var sonuc = new SubdomainRecordResult { Created = true, PublishWarningKey = "dns.not_published_no_server" };

        Assert.True(sonuc.Created);
        Assert.Equal("dns.not_published_no_server", sonuc.PublishWarningKey);
        Assert.Equal(string.Empty, new SubdomainRecordResult().PublishWarningKey);
    }

    /// <summary>
    /// YAYINLANMAMIS KAYIT "olusturuldu" LISTESINE YAZILMAZ. Bu, kullanicinin gordugu tek fark
    /// ve duzeltmenin butun amaci.
    /// </summary>
    [Fact]
    public void AnUnpublishedRecordIsReportedAsAWarning_NotAsCreated()
    {
        var rapor = DnsSubdomainDecision.Anlat(
            new SubdomainRecordResult
            {
                Created = true,
                Address = "1.2.3.4",
                PublishWarningKey = "dns.not_published_no_server"
            },
            "blog.ornek.com");

        Assert.NotNull(rapor);
        Assert.True(rapor!.Uyari, "Yayinlanmamis kayit UYARI olarak bildirilmeli.");
        Assert.StartsWith("dns.not_published_no_server", rapor.Satir, StringComparison.Ordinal);
        Assert.Contains("blog.ornek.com", rapor.Satir, StringComparison.Ordinal);
    }

    /// <summary>
    /// GERCEKTEN yayinlanan kayit hala BASARI olarak bildirilir. Duzeltmenin bedeli "her seye
    /// uyari basmak" olsaydi, gercek uyarilar da okunmaz hale gelirdi.
    /// </summary>
    [Fact]
    public void APublishedRecordIsStillReportedAsCreated()
    {
        var rapor = DnsSubdomainDecision.Anlat(
            new SubdomainRecordResult { Created = true, Address = "1.2.3.4" },
            "blog.ornek.com");

        Assert.NotNull(rapor);
        Assert.False(rapor!.Uyari);
        Assert.Equal("dns: blog.ornek.com A 1.2.3.4", rapor.Satir);
    }

    /// <summary>Onceki dallar aynen korunmali: bu bir davranis DEGISIKLIGI degil, EKLEMESI.</summary>
    [Theory]
    [InlineData(true, true, false)]    // zaten var + dogru -> bilgi
    [InlineData(true, false, true)]    // zaten var + baska yeri gosteriyor -> uyari
    public void TheExistingBranchesAreUnchanged(bool zatenVar, bool zatenDogru, bool uyariBekleniyor)
    {
        var rapor = DnsSubdomainDecision.Anlat(
            new SubdomainRecordResult
            {
                AlreadyExists = zatenVar,
                AlreadyCorrect = zatenDogru,
                ExistingRecord = "A 9.9.9.9"
            },
            "blog.ornek.com");

        Assert.NotNull(rapor);
        Assert.Equal(uyariBekleniyor, rapor!.Uyari);
    }

    /// <summary>Soylenecek bir sey yoksa satir da uretilmez (bos uyari gurultudur).</summary>
    [Fact]
    public void NothingToSayProducesNoLine()
    {
        Assert.Null(DnsSubdomainDecision.Anlat(new SubdomainRecordResult(), "blog.ornek.com"));
    }

    /// <summary>
    /// Uretilen anahtarlarin IKI DILDE de karsiligi olmali. <c>MessageKeyCoverageTests</c> bunu
    /// zaten sinif olarak koruyor; burada ADIYLA sabitlenmesinin sebebi, anahtarlarin
    /// kullaniciya AYNEN gosterilen bir uyari satirinda tasinmasi.
    /// </summary>
    [Theory]
    [InlineData("dns.not_published_no_server")]
    [InlineData("dns.not_published_secondary_no_master")]
    public void ThePublishFailureKeysAreTranslatedInBothLanguages(string anahtar)
    {
        var kok = BulKok();

        foreach (var dosya in new[] { "tr.json", "en.json" })
        {
            using var belge = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(kok, "frontend", "src", "i18n", dosya)));

            Assert.True(
                belge.RootElement.TryGetProperty(anahtar, out var deger) &&
                !string.IsNullOrWhiteSpace(deger.GetString()),
                $"{dosya} icinde {anahtar} yok; kullanici ekranda ham anahtari gorurdu.");
        }
    }

    private static string BulKok()
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            if (File.Exists(Path.Combine(klasor, "AdaPanel.sln")))
            {
                return Path.GetFullPath(klasor);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new DirectoryNotFoundException("Depo koku bulunamadi.");
    }
}
