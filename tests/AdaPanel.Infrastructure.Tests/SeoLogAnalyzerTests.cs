using AdaPanel.Core.Dtos.Seo;
using AdaPanel.Infrastructure.Seo;
using AdaPanel.Infrastructure.Statistics;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// SEO GÜNLÜK ANALİZİ — arama motoru davranışının GERÇEK IIS W3C biçiminden ölçülmesi.
/// </summary>
/// <remarks>
/// <para>
/// <b>Örnek satırlar uydurulmadı.</b> Biçim Microsoft'un kendi belgesinden alındı (Win32 "W3C
/// Logging"): yönerge satırları <c>#Software/#Version/#Date/#Fields</c>, alanlar BOŞLUKLA ayrılır,
/// boş değer <c>-</c>, ve <b>değerin içindeki yazdırılamayan karakterler <c>+</c> ile değiştirilir</b>
/// — Googlebot'un kullanıcı aracısı bu yüzden günlükte
/// <c>Mozilla/5.0+(compatible;+Googlebot/2.1;++http://www.google.com/bot.html)</c> olarak görünür.
/// Bot eşleşmesi bu KODLANMIŞ biçimde çalışmak zorundadır; boşluklu bir dizgeyle test etmek,
/// gerçekte hiç çalışmayan bir özelliği yeşil göstermek olurdu.
/// </para>
/// <para>
/// Alan kümesi IIS 10'un varsayılanıdır ve sıra IIS'in yazdığı sıradır.
/// </para>
/// </remarks>
public sealed class SeoLogAnalyzerTests
{
    /// <summary>IIS 10'un varsayılan alan kümesi — sıra dahil gerçeğinden.</summary>
    private const string Fields =
        "#Fields: date time s-ip cs-method cs-uri-stem cs-uri-query s-port cs-username c-ip cs(User-Agent) cs(Referer) sc-status sc-substatus sc-win32-status time-taken";

    /// <summary>Googlebot'un IIS günlüğünde göründüğü HÂLİ (boşluklar <c>+</c>).</summary>
    private const string GooglebotUa = "Mozilla/5.0+(compatible;+Googlebot/2.1;++http://www.google.com/bot.html)";

    private const string BingbotUa = "Mozilla/5.0+(compatible;+bingbot/2.0;++http://www.bing.com/bingbot.htm)";

    private const string ChromeUa =
        "Mozilla/5.0+(Windows+NT+10.0;+Win64;+x64)+AppleWebKit/537.36+(KHTML,+like+Gecko)+Chrome/124.0.0.0+Safari/537.36";

    private static readonly DateTime Gun = new(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc);

    private static string Satir(
        string date,
        string time,
        string stem,
        string query,
        string ip,
        string userAgent,
        int status,
        int timeTaken = 42) =>
        $"{date} {time} 10.0.0.5 GET {stem} {query} 443 - {ip} {userAgent} - {status} 0 0 {timeTaken}";

    private static SeoLogReportDto Analiz(IEnumerable<string> satirlar, IReadOnlyCollection<string>? bilinen = null)
    {
        var analyzer = new SeoLogAnalyzer(Gun);
        foreach (var record in W3cLogReader.Read(satirlar))
        {
            analyzer.Add(record);
        }

        return analyzer.Build(bilinen ?? []);
    }

    private static IEnumerable<string> Baslik() =>
    [
        "#Software: Microsoft Internet Information Services 10.0",
        "#Version: 1.0",
        "#Date: 2026-08-19 00:00:00",
        Fields
    ];

    /// <summary>
    /// TEMEL ÖLÇÜM: bot ile insan ayrılıyor ve motor adı gerçek kullanıcı aracısından çıkıyor.
    /// </summary>
    [Fact]
    public void Arama_motoru_istekleri_insan_trafiginden_ayrilir()
    {
        var rapor = Analiz(
        [
            .. Baslik(),
            Satir("2026-08-19", "08:00:01", "/", "-", "66.249.66.1", GooglebotUa, 200),
            Satir("2026-08-19", "08:00:02", "/urunler", "-", "66.249.66.1", GooglebotUa, 200),
            Satir("2026-08-19", "08:00:03", "/", "-", "40.77.167.1", BingbotUa, 200),
            Satir("2026-08-19", "08:00:04", "/", "-", "88.1.2.3", ChromeUa, 200)
        ]);

        Assert.True(rapor.HasData);
        Assert.Equal(4, rapor.TotalRequests);
        Assert.Equal(3, rapor.BotRequests);

        var google = Assert.Single(rapor.Engines, item => item.Key == "google");
        Assert.Equal(2, google.Requests);
        Assert.Equal(2, google.UniquePaths);
        Assert.Equal(2, google.Status2xx);
        Assert.Equal("search", google.Category);
        Assert.Equal(1, google.ActiveDays);

        Assert.Contains(rapor.Engines, item => item.Key == "bing");
        Assert.DoesNotContain(rapor.Engines, item => item.Key == "other");
    }

    /// <summary>
    /// <b>MUTASYON KAPISI:</b> kullanıcı aracısı IIS'in <c>+</c> kodlamasıyla gelir. Eşleşme
    /// boşluk bekleseydi bu test kırmızı olurdu ve özellik canlıda HİÇBİR botu tanımazdı.
    /// </summary>
    [Fact]
    public void Kullanici_aracisi_IIS_arti_kodlamasiyla_gelse_de_tanimlanir()
    {
        Assert.Equal("google", SeoBotCatalog.Match(GooglebotUa)?.Key);
        Assert.Equal("bing", SeoBotCatalog.Match(BingbotUa)?.Key);

        // Boşluklu (Advanced Logging / tırnaklı) biçim de aynı sonucu vermeli.
        Assert.Equal("google", SeoBotCatalog.Match("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)")?.Key);

        // İnsan tarayıcısı BOT DEĞİLDİR; olsaydı bot oranı tavan yapar ve rapor anlamsızlaşırdı.
        Assert.Null(SeoBotCatalog.Match(ChromeUa));
        Assert.Null(SeoBotCatalog.Match("-"));
        Assert.Null(SeoBotCatalog.Match(string.Empty));
    }

    /// <summary>
    /// Bota dönen 404/5xx doğrudan SEO kaybıdır ve "en çok gezilen" listesinde kaybolmamalıdır.
    /// </summary>
    [Fact]
    public void Bota_donen_hatalar_ayri_listede_yuzeye_cikar()
    {
        var rapor = Analiz(
        [
            .. Baslik(),
            Satir("2026-08-19", "08:00:01", "/", "-", "66.249.66.1", GooglebotUa, 200),
            Satir("2026-08-19", "08:00:02", "/", "-", "66.249.66.1", GooglebotUa, 200),
            Satir("2026-08-19", "08:00:03", "/", "-", "66.249.66.1", GooglebotUa, 200),
            Satir("2026-08-19", "08:00:04", "/eski-urun", "-", "66.249.66.1", GooglebotUa, 404),
            Satir("2026-08-19", "08:00:05", "/sepet", "-", "40.77.167.1", BingbotUa, 500)
        ]);

        var hatalar = rapor.ErrorPaths.ToArray();
        Assert.Equal(2, hatalar.Length);

        // 5xx ÖNCE: motorlar sürekli 5xx alan siteyi daha seyrek gezmeye başlar.
        Assert.Equal("/sepet", hatalar[0].Path);
        Assert.Equal(500, hatalar[0].LastStatus);
        Assert.Equal("bing", hatalar[0].TopEngine);
        Assert.Equal("/eski-urun", hatalar[1].Path);
        Assert.Equal(404, hatalar[1].LastStatus);

        var google = rapor.Engines.Single(item => item.Key == "google");
        Assert.Equal(1, google.Status4xx);
        Assert.Equal(1, rapor.Engines.Single(item => item.Key == "bing").Status5xx);
    }

    /// <summary>
    /// TARAMA BÜTÇESİ İSRAFI: yönlendirme, 404, engelleme ve parametreli kopya.
    /// </summary>
    [Fact]
    public void Tarama_butcesi_israfi_sebebine_gore_siniflanir()
    {
        var rapor = Analiz(
        [
            .. Baslik(),
            Satir("2026-08-19", "08:00:01", "/eski", "-", "66.249.66.1", GooglebotUa, 301),
            Satir("2026-08-19", "08:00:02", "/eski", "-", "66.249.66.1", GooglebotUa, 301),
            Satir("2026-08-19", "08:00:03", "/yok", "-", "66.249.66.1", GooglebotUa, 404),
            Satir("2026-08-19", "08:00:04", "/gizli", "-", "66.249.66.1", GooglebotUa, 403),
            Satir("2026-08-19", "08:00:05", "/patlak", "-", "66.249.66.1", GooglebotUa, 503)
        ]);

        var sebepler = rapor.Waste.ToDictionary(item => item.Path, item => item.Reason);
        Assert.Equal("redirect", sebepler["/eski"]);
        Assert.Equal("not_found", sebepler["/yok"]);
        Assert.Equal("blocked", sebepler["/gizli"]);
        Assert.Equal("server_error", sebepler["/patlak"]);

        Assert.Equal(2, rapor.Waste.Single(item => item.Path == "/eski").Hits);
        Assert.Equal(5, rapor.WastedRequests);

        // 5xx en pahalı kalem: listenin başında olmalı ki ekranda ilk o görülsün.
        Assert.Equal("server_error", rapor.Waste.First().Reason);
    }

    /// <summary>
    /// PARAMETRELİ KOPYA kuralı İKİ koşula birden bağlı: sorgusuz hâli de geziliyor OLMALI ve en
    /// az İKİ farklı sorgu görülmeli. Tek bir parametreli adres (arama sayfası) israf değildir.
    /// </summary>
    [Fact]
    public void Parametreli_kopya_yalniz_gercek_tekrar_oldugunda_israf_sayilir()
    {
        var israf = Analiz(
        [
            .. Baslik(),
            Satir("2026-08-19", "09:00:01", "/urunler", "-", "66.249.66.1", GooglebotUa, 200),
            Satir("2026-08-19", "09:00:02", "/urunler", "sort=asc", "66.249.66.1", GooglebotUa, 200),
            Satir("2026-08-19", "09:00:03", "/urunler", "sort=desc", "66.249.66.1", GooglebotUa, 200),
            Satir("2026-08-19", "09:00:04", "/urunler", "utm_source=x", "66.249.66.1", GooglebotUa, 200)
        ]).Waste.SingleOrDefault(item => item.Reason == "query_duplicate");

        Assert.NotNull(israf);
        Assert.Equal("/urunler", israf.Path);
        Assert.Equal(3, israf.Hits);
        Assert.Contains("sort=", israf.Detail);

        // TEK sorgu + sorgusuz hâl: bu bir arama/filtre sayfasıdır, israf DEĞİL.
        var tek = Analiz(
        [
            .. Baslik(),
            Satir("2026-08-19", "09:00:01", "/ara", "-", "66.249.66.1", GooglebotUa, 200),
            Satir("2026-08-19", "09:00:02", "/ara", "q=ayakkabi", "66.249.66.1", GooglebotUa, 200)
        ]);
        Assert.DoesNotContain(tek.Waste, item => item.Reason == "query_duplicate");

        // Sorgusuz hâli HİÇ gezilmemişse de israf değil: kopya olduğunu gösteren kanıt yok.
        var sadeceParametreli = Analiz(
        [
            .. Baslik(),
            Satir("2026-08-19", "09:00:01", "/liste", "p=1", "66.249.66.1", GooglebotUa, 200),
            Satir("2026-08-19", "09:00:02", "/liste", "p=2", "66.249.66.1", GooglebotUa, 200)
        ]);
        Assert.DoesNotContain(sadeceParametreli.Waste, item => item.Reason == "query_duplicate");
    }

    /// <summary>
    /// HİÇ GEZİLMEMİŞ SAYFALAR panelin KENDİ tarama raporundan gelir; büyük/küçük harf ve sondaki
    /// eğik çizgi farkı iki tarafı ayrı sayfa sanmamalı.
    /// </summary>
    [Fact]
    public void Hic_gezilmemis_sayfalar_bilinen_listeden_cikarilir()
    {
        var rapor = Analiz(
            [
                .. Baslik(),
                Satir("2026-08-19", "10:00:01", "/", "-", "66.249.66.1", GooglebotUa, 200),
                Satir("2026-08-19", "10:00:02", "/Hakkimizda", "-", "66.249.66.1", GooglebotUa, 200)
            ],
            bilinen: ["/", "/hakkimizda/", "/iletisim", "/blog/ilk-yazi"]);

        Assert.Equal(4, rapor.KnownPageCount);
        Assert.Equal(["/blog/ilk-yazi", "/iletisim"], rapor.UncrawledPaths.OrderBy(item => item).ToArray());
    }

    /// <summary>
    /// BİLİNEN SAYFA YOKSA liste boş kalır — ve bu, "her sayfa geziliyor" ANLAMINA GELMEZ. Rapor
    /// kaynağı ayrı alanda taşındığı için arayüz ikisini ayırt edebilir.
    /// </summary>
    [Fact]
    public void Bilinen_sayfa_yoksa_gezilmemis_listesi_bos_ve_sayac_sifir()
    {
        var rapor = Analiz([.. Baslik(), Satir("2026-08-19", "10:00:01", "/", "-", "66.249.66.1", GooglebotUa, 200)]);

        Assert.Empty(rapor.UncrawledPaths);
        Assert.Equal(0, rapor.KnownPageCount);
    }

    /// <summary>
    /// PENCERE DIŞI GÜNLER SAYILMAZ. Tek bir dosya birden çok günün satırını taşıyabilir
    /// (yönetici döndürmeyi haftalık yapmış olabilir); süzgeç olmasaydı "7 günlük analiz" sessizce
    /// bir ayı kapsar ve "bot her gün geliyor" yanlış çıkarımına yol açardı.
    /// </summary>
    [Fact]
    public void Pencere_disindaki_gunler_sayilmaz()
    {
        var rapor = Analiz(
        [
            .. Baslik(),
            Satir("2026-08-18", "23:59:59", "/eski-gun", "-", "66.249.66.1", GooglebotUa, 200),
            Satir("2026-08-19", "00:00:01", "/bugun", "-", "66.249.66.1", GooglebotUa, 200)
        ]);

        Assert.Equal(1, rapor.TotalRequests);
        Assert.Equal(["/bugun"], rapor.TopBotPaths.Select(item => item.Path).ToArray());
    }

    /// <summary>
    /// GÜN GÜN DAĞILIM: "ne sıklıkla geziyor" sorusunun cevabı. Tarih anahtarı günlüğün KENDİ
    /// <c>date</c> alanından gelir (UTC), sunucunun yerel saatinden değil.
    /// </summary>
    [Fact]
    public void Gun_gun_bot_dagilimi_gunlugun_kendi_tarihinden_gelir()
    {
        var analyzer = new SeoLogAnalyzer(new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc));
        foreach (var record in W3cLogReader.Read(new[]
                 {
                     Fields,
                     Satir("2026-08-17", "01:00:00", "/", "-", "66.249.66.1", GooglebotUa, 200),
                     Satir("2026-08-17", "02:00:00", "/", "-", "66.249.66.1", GooglebotUa, 200),
                     Satir("2026-08-19", "03:00:00", "/", "-", "66.249.66.1", GooglebotUa, 200)
                 }))
        {
            analyzer.Add(record);
        }

        var rapor = analyzer.Build([]);
        Assert.Equal(
            [("2026-08-17", 2L), ("2026-08-19", 1L)],
            rapor.DailyBotRequests.Select(item => (item.Date, item.Requests)).ToArray());

        var google = rapor.Engines.Single();
        Assert.Equal(2, google.ActiveDays);
        Assert.Equal(new DateTime(2026, 8, 19, 3, 0, 0, DateTimeKind.Utc), google.LastSeenUtc);
    }

    /// <summary>
    /// YAPAY ZEKÂ ve ÜÇÜNCÜ TARAF tarayıcıları ayrı kategoride sayılır: tarama bütçesini yerler
    /// ama klasik sıralamayı etkilemezler. Aynı torbaya atmak, "arama motoru trafiği" sayısını
    /// olduğundan büyük gösterirdi.
    /// </summary>
    [Fact]
    public void Yapay_zeka_ve_arac_tarayicilari_ayri_kategoride()
    {
        var rapor = Analiz(
        [
            .. Baslik(),
            Satir("2026-08-19", "11:00:01", "/", "-", "20.1.1.1", "Mozilla/5.0+AppleWebKit/537.36+(KHTML,+like+Gecko;+compatible;+GPTBot/1.2;++https://openai.com/gptbot)", 200),
            Satir("2026-08-19", "11:00:02", "/", "-", "5.5.5.5", "Mozilla/5.0+(compatible;+AhrefsBot/7.0;++http://ahrefs.com/robot/)", 200),
            Satir("2026-08-19", "11:00:03", "/", "-", "9.9.9.9", "SomeUnknownCrawler/1.0", 200)
        ]);

        Assert.Equal("ai", rapor.Engines.Single(item => item.Key == "openai_training").Category);
        Assert.Equal("tool", rapor.Engines.Single(item => item.Key == "ahrefs").Category);

        // Tanınmayan tarayıcı "insan" sayılmaz — bot oranı olduğundan küçük görünürdü.
        Assert.Contains(rapor.Engines, item => item.Key == SeoBotCatalog.OtherKey);
        Assert.Equal(3, rapor.BotRequests);
    }

    /// <summary>
    /// BOZUK SATIR analizi düşürmez. Disk dolduğunda yarım yazılmış son satır olağandır; tek bir
    /// satır yüzünden 30 günün ölçümünü kaybetmek, hiç ölçmemekten kötüdür.
    /// </summary>
    [Fact]
    public void Yarim_kalmis_satir_analizi_dusurmez()
    {
        var rapor = Analiz(
        [
            .. Baslik(),
            Satir("2026-08-19", "12:00:01", "/", "-", "66.249.66.1", GooglebotUa, 200),
            "2026-08-19 12:00:02 10.0.0.5 GET /yarim",
            string.Empty,
            Satir("2026-08-19", "12:00:03", "/son", "-", "66.249.66.1", GooglebotUa, 200)
        ]);

        Assert.Equal(3, rapor.TotalRequests);
        Assert.Equal(2, rapor.BotRequests);
    }

    /// <summary>
    /// <c>#Fields</c> DOSYANIN ORTASINDA yeniden yazılabilir (IIS alan seçimi değişince aynı
    /// dosyaya yeni bir başlık basar). Yeni sıra uygulanmazsa sonraki satırlarda yanlış sütun
    /// okunur — ve bu SESSİZ bir hatadır.
    /// </summary>
    [Fact]
    public void Dosya_ortasinda_yeniden_yazilan_alan_basligi_uygulanir()
    {
        var rapor = Analiz(
        [
            .. Baslik(),
            Satir("2026-08-19", "13:00:01", "/ilk", "-", "66.249.66.1", GooglebotUa, 200),
            "#Fields: date time cs-uri-stem c-ip cs(User-Agent) sc-status",
            $"2026-08-19 13:00:02 /ikinci 66.249.66.1 {GooglebotUa} 404"
        ]);

        Assert.Equal(2, rapor.BotRequests);
        var ikinci = rapor.ErrorPaths.Single();
        Assert.Equal("/ikinci", ikinci.Path);
        Assert.Equal(404, ikinci.LastStatus);
    }

    /// <summary>
    /// BAŞLIKSIZ dosya okunmaz. Varsayılan alan sırasını UYDURMAK, yanlış sütunu doğru sanmaktır:
    /// <c>sc-status</c> sanılan sütun <c>time-taken</c> olabilir ve rapor tamamen yanlış çıkardı.
    /// </summary>
    [Fact]
    public void Alan_basligi_olmayan_dosyadan_veri_uretilmez()
    {
        var rapor = Analiz([Satir("2026-08-19", "14:00:01", "/", "-", "66.249.66.1", GooglebotUa, 200)]);

        Assert.False(rapor.HasData);
        Assert.Equal(0, rapor.TotalRequests);
    }
}
