using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// 125 dakikalık taramanın UÇTAN UCA ayakta kalması: uç, istek ömrüne bağlanmamalı; ekran, süren
/// taramayı sunucudan geri okumalı; metinler sahte yüzde vaat etmemeli.
/// <para>
/// Bu üç şey ayrı dosyalarda yaşıyor ve üçünden biri geri alınırsa hata sessizce döner: tarama
/// yine görünür şekilde başlar, yine "çalışıyor" görünür, ama sayfa yenilendiğinde yine kaybolur.
/// </para>
/// </summary>
public sealed class AntivirusScanLifetimeWiringTests
{
    /// <summary>
    /// TARAMA UCU İSTEK İPTAL TOKEN'INA BAĞLANMAMALI.
    /// <para>
    /// KÖK NEDEN: ASP.NET Core, eylem imzasındaki <c>CancellationToken</c>'a
    /// <c>HttpContext.RequestAborted</c>'ı bağlar. Kullanıcı sayfayı yenilediğinde bağlantı kopar,
    /// token iptal olur ve <c>ProcessRunner</c> istisna fırlatır; clamscan.exe ise sunucuda koşmaya
    /// DEVAM eder. Yani iki saatlik iş hem sürer hem de sonucu hiçbir yere yazılmazdı. Bu yüzden
    /// uzun uçlar <c>CancellationToken.None</c> kullanır (SoftwareController'daki kurulum uçlarıyla
    /// aynı kural).
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("ScanAsync")]
    [InlineData("UpdateDefinitionsAsync")]
    public void Uzun_Antivirus_Uclari_Istek_Iptaline_Baglanmaz(string eylem)
    {
        var kaynak = Oku("src/AdaPanel.Api/Controllers/AntivirusController.cs");
        var govde = EylemGovdesi(kaynak, eylem);

        Assert.DoesNotContain("CancellationToken cancellationToken", govde, StringComparison.Ordinal);
        Assert.Contains("CancellationToken.None", govde, StringComparison.Ordinal);
    }

    /// <summary>
    /// Durum ucu SÜREN taramayı da döndürmeli; sayfa yenilendikten sonra geri okunacak tek kaynak
    /// budur.
    /// </summary>
    [Fact]
    public void Durum_Sozlesmesi_Suren_Taramayi_Tasiyor()
    {
        var dto = Oku("src/AdaPanel.Core/Dtos/Antivirus/AntivirusDtos.cs");
        var istemci = Oku("frontend/src/api/antivirus.ts");

        Assert.Contains("AntivirusRunningScanDto? RunningScan", dto, StringComparison.Ordinal);
        Assert.Contains("runningScan: AntivirusRunningScan | null", istemci, StringComparison.Ordinal);
    }

    /// <summary>
    /// Güvenlik ekranı süren taramayı SUNUCUDAN okumalı ve yoklamayı bırakmamalı; yalnız kendi
    /// belleğine bakan bir şerit sayfa yenilenince boşalır.
    /// </summary>
    [Fact]
    public void Guvenlik_Ekrani_Suren_Taramayi_Sunucudan_Okuyor_Ve_Yokluyor()
    {
        var ekran = Oku("frontend/src/views/SecurityView.vue");

        Assert.Contains("avStatus.value?.runningScan", ekran, StringComparison.Ordinal);
        Assert.Contains("avBannerRunning", ekran, StringComparison.Ordinal);
        Assert.Contains("avSyncPolling", ekran, StringComparison.Ordinal);

        // Zamanlayıcı bileşen yok olduğunda DURDURULMALI (sızıntı olurdu).
        Assert.Contains("onUnmounted(avStopPolling)", ekran, StringComparison.Ordinal);
    }

    /// <summary>
    /// SAHTE YÜZDE YASAĞI metin düzeyinde de korunur: "sürüyor, X dakikadır" dürüsttür, "%63"
    /// uydurmadır. Ekran yüzde biçimlendirmesi yapmamalı, süren tarama metinleri dakika
    /// göstermeli.
    /// </summary>
    [Fact]
    public void Suren_Tarama_Metinleri_Yuzde_Uydurmuyor()
    {
        var ekran = Oku("frontend/src/views/SecurityView.vue");
        var sozluk = JsonSerializer.Deserialize<Dictionary<string, string>>(
            Oku("frontend/src/i18n/tr.json"))!;

        // Süren tarama bloğunda yüzde hesabı olmamalı.
        Assert.DoesNotContain("avServerPercent", ekran, StringComparison.Ordinal);
        Assert.DoesNotContain("scanPercent", ekran, StringComparison.Ordinal);

        Assert.True(sozluk.ContainsKey("panel.antivirus.elapsedMinutes"));
        Assert.True(sozluk.ContainsKey("panel.antivirus.elapsedHours"));
        Assert.Contains("yüzde", sozluk["panel.antivirus.resumedHint"], StringComparison.OrdinalIgnoreCase);

        foreach (var anahtar in new[]
                 {
                     "panel.antivirus.resumedHint",
                     "panel.antivirus.elapsedMinutes",
                     "panel.antivirus.elapsedHours",
                     "panel.antivirus.scanBusyHint"
                 })
        {
            Assert.DoesNotContain("{percent", sozluk[anahtar], StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Yarıda kalan tarama ekranda AYRI bir durum olarak görünmeli; "temiz" ya da "bulgu yok"
    /// diye gösterilmesi, koşmamış bir taramayı başarılı saymak olurdu.
    /// </summary>
    [Fact]
    public void Yarida_Kalan_Tarama_Ekranda_Ayri_Durum_Olarak_Gosteriliyor()
    {
        var ekran = Oku("frontend/src/views/SecurityView.vue");
        var tr = JsonSerializer.Deserialize<Dictionary<string, string>>(Oku("frontend/src/i18n/tr.json"))!;
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(Oku("frontend/src/i18n/en.json"))!;

        Assert.Contains("'interrupted'", ekran, StringComparison.Ordinal);
        Assert.True(tr.ContainsKey("panel.antivirus.status.interrupted"));
        Assert.True(en.ContainsKey("panel.antivirus.status.interrupted"));
        Assert.True(tr.ContainsKey("panel.antivirus.interruptedHint"));
        Assert.True(en.ContainsKey("panel.antivirus.interruptedHint"));
    }

    // ===============================================================================================
    // Yardimcilar
    // ===============================================================================================

    private static string EylemGovdesi(string kaynak, string eylemAdi)
    {
        var baslangic = kaynak.IndexOf($"> {eylemAdi}(", StringComparison.Ordinal);
        if (baslangic < 0)
        {
            baslangic = kaynak.IndexOf($" {eylemAdi}(", StringComparison.Ordinal);
        }

        Assert.True(baslangic >= 0, $"Eylem bulunamadı: {eylemAdi}");

        // İmza + gövde: bir sonraki HTTP niteliğine, ilk özel yardımcıya ya da dosya sonuna kadar.
        // Yardımcıları da içeri almak yanlış pozitif üretiyordu: `WriteAuditAsync`'in KENDİ
        // CancellationToken parametresi, eylemin istek iptaline bağlı sanılmasına yol açıyordu.
        var sonraki = Regex.Match(kaynak[baslangic..], @"\n\s*(\[Http|private )");
        var uzunluk = sonraki.Success ? sonraki.Index : kaynak.Length - baslangic;
        return kaynak.Substring(baslangic, uzunluk);
    }

    private static string Oku(string goreliYol)
    {
        var dizin = new DirectoryInfo(AppContext.BaseDirectory);
        while (dizin is not null && !File.Exists(Path.Combine(dizin.FullName, "AdaPanel.sln")))
        {
            dizin = dizin.Parent;
        }

        var kok = dizin?.FullName ?? throw new InvalidOperationException("Depo kökü bulunamadı.");
        return File.ReadAllText(Path.Combine(kok, goreliYol.Replace('/', Path.DirectorySeparatorChar)));
    }
}
