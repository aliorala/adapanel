using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Otomatik yapılandırma (autodiscover/autoconfig) ARAYÜZDEN GERÇEKTEN ÇAĞRILIYOR mu?
/// </summary>
/// <remarks>
/// <para>
/// <b>Bu test bir denetim bulgusundan doğdu (F2).</b> Arka uç eksiksizdi: üç uç, altı adımlık
/// durum raporu, gereken DNS kayıtları. Çeviriler de eksiksizdi: on dört anahtar, hem TR hem EN.
/// Eksik olan tek şey ARAYÜZDÜ — hiçbir ekran bu uçları çağırmıyordu, bir API sarmalayıcısı bile
/// yoktu. Yani özellik "yazılmış" görünüyor, kullanıcı için var olmuyordu.
/// </para>
/// <para>
/// <b>Neden derleyici yakalayamaz:</b> kullanılmayan bir uç ne C# tarafında ne TypeScript
/// tarafında hata üretir; iki taraf da kendi içinde tutarlıdır. Kopukluk yalnızca ARADADIR.
/// Bu yüzden bağlantı ölçülerek korunuyor.
/// </para>
/// <para>
/// <b>Ölçüm sınırı — dürüstlük:</b> bu test kaynak metnini okur. "Düğme tıklanınca uç çağrılıyor"
/// gerçeğini bir tarayıcı ölçümü kanıtlar; buradaki ölçüm yalnızca çağıranın VAR OLDUĞUNU ve
/// sunucunun üretebildiği her adım/durum için bir çevirinin bulunduğunu kanıtlar. Bulunan hata
/// sınıfı (sıfır çağrı) tam olarak buydu.
/// </para>
/// </remarks>
public sealed class MailAutodiscoverUiWiringTests
{
    /// <summary>Sunucudaki adım kimliklerini ve durumlarını üreten tek yer.</summary>
    private const string YayinciDosyasi = "src/AdaPanel.Infrastructure/Mail/MailAutodiscoverPublisher.cs";

    private const string SarmalayiciDosyasi = "frontend/src/api/autodiscover.ts";

    private const string EkranDosyasi = "frontend/src/views/MailView.vue";

    [Fact]
    public void Otomatik_yapilandirma_uclarinin_bir_API_sarmalayicisi_olmali()
    {
        var sarmalayici = Oku(SarmalayiciDosyasi);

        // Yol denetleyiciden alındı: [Route("api/v1/mail/autoconfig")], istemci baseURL'i "/api/v1".
        Assert.Contains("/mail/autoconfig/", sarmalayici, StringComparison.Ordinal);

        // Üç fiil de bağlanmış olmalı: ölçüm olmadan kullanıcı zinciri göremez, DELETE olmadan
        // yaptığını geri alamaz. Yalnız POST bağlamak "kur ama sökemezsin" demek olurdu.
        Assert.Contains("apiClient.get<", sarmalayici, StringComparison.Ordinal);
        Assert.Contains("apiClient.post<", sarmalayici, StringComparison.Ordinal);
        Assert.Contains("apiClient.delete<", sarmalayici, StringComparison.Ordinal);

        // Sertifika iletişim adresi sunucuya SORGU DİZESİNDE gider ([FromQuery] string? contactEmail).
        Assert.Contains("contactEmail", sarmalayici, StringComparison.Ordinal);
    }

    [Fact]
    public void MailView_sarmalayiciyi_gercekten_cagirmali()
    {
        var ekran = Oku(EkranDosyasi);

        Assert.Contains("../api/autodiscover", ekran, StringComparison.Ordinal);

        foreach (var fonksiyon in new[] { "getMailAutoconfig", "publishMailAutoconfig", "unpublishMailAutoconfig" })
        {
            // İçe aktarım + en az bir ÇAĞRI: yalnız import etmek, ölü kodun adını değiştirmekten
            // ibaret olurdu.
            var cagriSayisi = Regex.Matches(ekran, Regex.Escape(fonksiyon) + @"\s*\(").Count;
            Assert.True(cagriSayisi > 0, $"MailView.vue '{fonksiyon}' fonksiyonunu hiç çağırmıyor.");
        }

        // Kart yalnız yöneticiye gösterilmeli: uçlar [Authorize(Roles = "Administrator")].
        Assert.Contains("v-if=\"isAdmin\" class=\"form-surface autoconfig-card\"", ekran, StringComparison.Ordinal);
    }

    [Fact]
    public void Sunucunun_uretebildigi_her_adim_ve_durum_icin_ceviri_olmali()
    {
        var kok = BulKok();
        var tr = OkuSozluk(Path.Combine(kok, "frontend", "src", "i18n", "tr.json"));
        var en = OkuSozluk(Path.Combine(kok, "frontend", "src", "i18n", "en.json"));

        var (adimlar, durumlar) = YayincidanOku(Oku(YayinciDosyasi));

        // ÖLÇEMEDİK ile SORUN YOK ayrımı: tarama bozulursa test sessizce geçmesin.
        Assert.True(adimlar.Count >= 6, $"Adım taraması bozulmuş olabilir: {adimlar.Count} adım bulundu.");
        Assert.True(durumlar.Count >= 4, $"Durum taraması bozulmuş olabilir: {durumlar.Count} durum bulundu.");

        var eksik = new List<string>();
        foreach (var anahtar in adimlar.Select(id => $"panel.mail.autoconfig.step.{id}")
                     .Concat(durumlar.Select(durum => $"panel.mail.autoconfig.state.{durum}")))
        {
            var trVar = tr.Contains(anahtar);
            var enVar = en.Contains(anahtar);
            if (!trVar || !enVar)
            {
                eksik.Add($"{anahtar} (tr={(trVar ? "var" : "YOK")}, en={(enVar ? "var" : "YOK")})");
            }
        }

        Assert.True(
            eksik.Count == 0,
            "Otomatik yapılandırma ekranında ham anahtar görünecek:\n  " + string.Join("\n  ", eksik));
    }

    /// <summary>
    /// Adım kimliklerini ve durum adlarını YAYINCININ KENDİSİNDEN okur.
    /// </summary>
    /// <remarks>
    /// Elle yazılmış bir liste, sunucuya yedinci bir adım eklendiğinde sessizce eskir ve test
    /// hiçbir şeyi korumaz; bu depoda "elle yazılmış liste" hata sınıfı zaten yaşanmış
    /// (bkz. <c>AuditCoverageTests</c> notu).
    /// </remarks>
    private static (HashSet<string> Adimlar, HashSet<string> Durumlar) YayincidanOku(string kaynak)
    {
        var adimlar = new HashSet<string>(StringComparer.Ordinal);
        var durumlar = new HashSet<string>(StringComparer.Ordinal);

        // Adim("rewrite", "fail", …) — yardımcı fabrikanın ilk iki bağımsız değişkeni.
        foreach (Match eslesme in Regex.Matches(kaynak, @"Adim\(\s*""([a-z]+)""\s*,\s*""([a-z]+)"""))
        {
            adimlar.Add(eslesme.Groups[1].Value);
            durumlar.Add(eslesme.Groups[2].Value);
        }

        return (adimlar, durumlar);
    }

    private static string Oku(string goreliYol)
        => File.ReadAllText(Path.Combine(BulKok(), goreliYol.Replace('/', Path.DirectorySeparatorChar)));

    private static HashSet<string> OkuSozluk(string yol)
    {
        using var belge = JsonDocument.Parse(File.ReadAllText(yol));
        var kume = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ozellik in belge.RootElement.EnumerateObject())
        {
            kume.Add(ozellik.Name);
        }

        return kume;
    }

    private static string BulKok()
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            if (File.Exists(Path.Combine(klasor, "AdaPanel.sln")))
            {
                // Normalize edilir: aksi halde yolda "\bin\" birikir (bkz. MessageKeyCoverageTests).
                return Path.GetFullPath(klasor);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new DirectoryNotFoundException("Depo koku bulunamadi.");
    }
}
