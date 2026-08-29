using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Dtos.Store;
using AdaPanel.Infrastructure.AppStore;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// UYGULAMA KATALOGU SABİT TÜRKÇE CÜMLE TAŞIYAMAZ.
/// <para>
/// <b>ÖLÇÜLEN HATA:</b> <c>AppStoreService.Templates</c> içindeki <c>Description</c> ve
/// <c>AutomaticSetup</c> değerleri düz Türkçe cümlelerdi ("Laravel 13 uygulamasını üretim
/// ayarları… hazırlar."), <c>AppStoreView.vue</c> ise onları <c>t()</c>'den GEÇİRMEDEN
/// basıyordu. Sonuç: dili İngilizce olan müşteri panelin en çok bakılan ekranında Türkçe cümle
/// görüyordu. Bu bir yazım hatası değil, bir HATA SINIFI: katalog verisi ile arayüz metni aynı
/// alanda taşınıyordu.
/// </para>
/// <para>
/// <b>NEDEN TEST:</b> bu hata istisna fırlatmaz, günlüğe düşmez, derlemeyi kızartmaz — yalnızca
/// yanlış dili gösterir. Yeni bir şablon eklendiğinde (ki katalog düzenli büyüyor) aynı hata
/// hiçbir uyarı vermeden geri gelir. <c>MessageKeyCoverageTests</c> burayı GÖRMEZ: onun deseni
/// <c>...Key</c> ile biten alanları ve hata yollarını tarar, <c>Description</c> ise ikisi de
/// değildir.
/// </para>
/// <para>
/// <b>KAPSAM SINIRI BİLİNÇLİ:</b> <c>Requirements</c> ve bazı <c>AutomaticSetup</c> girdileri
/// ("PHP 8.3+", "Composer", "SQLite", "web.config") çevrilebilir metin değil, ÜRÜN/DOSYA
/// adıdır; onları anahtara çevirmek iki sözlüğe de aynen kopyalanan ölü anahtar üretirdi. Bu
/// yüzden kural iki katmanlı: açıklama HER ZAMAN anahtar olmalı; geri kalan alanlar ham
/// kalabilir ama Türkçeye özgü harf TAŞIYAMAZ.
/// </para>
/// </summary>
public sealed class AppStoreCatalogLocalizationTests
{
    /// <summary>Türkçeye özgü harfler. Bir katalog değerinde geçiyorsa o değer BİR CÜMLEDİR.</summary>
    private const string TurkceHarfler = "çÇğĞıİöÖşŞüÜ";

    /// <summary>
    /// Arayüzün anahtar tanıma kuralının AYNISI (<c>utils/apiError.looksLikeMessageKey</c>).
    /// Kopyalanması bilinçli: burada asıl ölçtüğümüz şey "arayüz bunu anahtar SAYAR MI".
    /// Tire içeren bir anahtar (<c>store.template.plain-php.description</c>) arayüzde anahtar
    /// sayılmaz ve kullanıcı ham anahtarı görürdü — desen onu da yakalar.
    /// </summary>
    private static readonly Regex AnahtarBicimi = new(@"^[a-z][a-z0-9_]*(?:\.[a-z0-9_]+)+$", RegexOptions.Compiled);

    [Fact]
    public void Her_Sablon_Aciklamasi_Ceviri_Anahtaridir_Ve_Iki_Dilde_De_Karsiligi_Vardir()
    {
        var sablonlar = Katalog();
        var (tr, en) = Sozlukler();

        // OLCEMEDIK ile SORUN YOK ayrimi: yansima alan adini bulamazsa liste boşalır ve test
        // sessizce geçer, bir daha hiçbir şeyi korumaz.
        Assert.True(sablonlar.Count >= 8, $"Katalog taraması bozuk olabilir: {sablonlar.Count} şablon bulundu.");

        var ihlaller = new List<string>();
        foreach (var sablon in sablonlar)
        {
            if (!AnahtarBicimi.IsMatch(sablon.Description))
            {
                ihlaller.Add($"{sablon.Id}: açıklama çeviri anahtarı değil -> \"{sablon.Description}\"");
                continue;
            }

            if (!tr.Contains(sablon.Description) || !en.Contains(sablon.Description))
            {
                ihlaller.Add(
                    $"{sablon.Id}: {sablon.Description} (tr={(tr.Contains(sablon.Description) ? "var" : "YOK")}, " +
                    $"en={(en.Contains(sablon.Description) ? "var" : "YOK")})");
            }
        }

        Assert.True(
            ihlaller.Count == 0,
            "Uygulama kataloğu açıklamaları arayüzde ÇEVRİLEMEZ; kullanıcı yanlış dilde metin görür:\n  " +
            string.Join("\n  ", ihlaller));
    }

    [Fact]
    public void Katalogda_Sabit_Turkce_Cumle_Kalmadi()
    {
        var ihlaller = new List<string>();
        foreach (var sablon in Katalog())
        {
            // DisplayName/Runtime/Category ve URL'ler bilinçli olarak DIŞARIDA: ürün adı ve
            // adres dilden bağımsızdır. Kategori adı arayüzde `categoryLabel` ile zaten
            // çevriliyor (ham değere düşerek).
            var degerler = new List<string> { sablon.Description };
            degerler.AddRange(sablon.Requirements);
            degerler.AddRange(sablon.AutomaticSetup);

            foreach (var deger in degerler.Where(deger => deger.Any(harf => TurkceHarfler.Contains(harf))))
            {
                ihlaller.Add($"{sablon.Id}: \"{deger}\"");
            }
        }

        Assert.True(
            ihlaller.Count == 0,
            "Katalogda sabit Türkçe metin var; İngilizce panelde de aynen görünür:\n  " + string.Join("\n  ", ihlaller));
    }

    [Fact]
    public void Kurulum_Adimlarinin_Anahtar_Olanlarinin_Iki_Dilde_De_Karsiligi_Vardir()
    {
        var (tr, en) = Sozlukler();

        // Anahtar BİÇİMİNDEKİ her değer sözlükte olmalı. Biçime uymayan değerler ("SQLite",
        // "web.config", "PHP 8.x") ham geçer ve ARANMAZ: bunlar ürün/dosya adıdır, çevirileri
        // yoktur ve olmamalıdır.
        var eksik = Katalog()
            .SelectMany(sablon => sablon.AutomaticSetup.Concat(sablon.Requirements)
                .Where(deger => AnahtarBicimi.IsMatch(deger))
                .Select(deger => (sablon.Id, deger)))
            .Where(item => !tr.Contains(item.deger) || !en.Contains(item.deger))
            .Select(item => $"{item.Id}: {item.deger}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            eksik.Length == 0,
            "Karşılığı olmayan katalog anahtarları var; kullanıcı ham anahtar görür:\n  " + string.Join("\n  ", eksik));
    }

    /// <summary>
    /// Katalog <c>private static</c> bir alandır ve servisin kurulması 8 bağımlılık ister;
    /// yansıma, sahte bağımlılık kurmaktan hem ucuz hem de kırılgan olmayan yoldur (alan adı
    /// değişirse test AÇIKÇA patlar, sessizce boş geçmez — bkz. yukarıdaki sayı kontrolü).
    /// </summary>
    private static IReadOnlyList<AppTemplateDto> Katalog()
    {
        var alan = typeof(AppStoreService).GetField("Templates", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AppStoreService.Templates alanı bulunamadı (yeniden adlandırıldı mı?).");

        return (AppTemplateDto[])(alan.GetValue(null)
            ?? throw new InvalidOperationException("AppStoreService.Templates okunamadı."));
    }

    private static (HashSet<string> Tr, HashSet<string> En) Sozlukler()
    {
        var kok = BulKok();
        return (Oku(Path.Combine(kok, "frontend", "src", "i18n", "tr.json")),
                Oku(Path.Combine(kok, "frontend", "src", "i18n", "en.json")));

        static HashSet<string> Oku(string yol)
        {
            using var belge = JsonDocument.Parse(File.ReadAllText(yol));
            var kume = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ozellik in belge.RootElement.EnumerateObject())
            {
                kume.Add(ozellik.Name);
            }

            return kume;
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

        throw new DirectoryNotFoundException("Depo kökü bulunamadı.");
    }
}
