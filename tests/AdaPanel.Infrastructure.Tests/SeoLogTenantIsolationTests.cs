using System.Reflection;
using System.Text;
using AdaPanel.Core.Dtos.Seo;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Seo;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// SEO GÜNLÜK ANALİZİNİN KİRACI SINIRI ve BÜTÇE DÜRÜSTLÜĞÜ — gerçek dosyalarla ölçülür.
/// </summary>
/// <remarks>
/// <para>
/// <b>Korunan hata sınıfı:</b> erişim günlüğü panelin en hassas dosyalarından biridir (ziyaretçi
/// IP'leri, yönetim adresleri, sorgu dizesindeki belirteçler). Bir kiracının BAŞKA bir kiracının
/// günlüğünü analiz ettirebilmesi, panelin en pahalı açığı olurdu. Sınır iki halkalıdır: (1) site
/// adı denetleyicide kapsam doğrulamasından geçer, (2) okunacak klasör SİTE KİMLİĞİNDEN türetilir
/// ve istek gövdesinde yol alanı YOKTUR.
/// </para>
/// <para>
/// <b>Neden gerçek dosya:</b> "kapsam çağrılıyor mu" sorusu kaynak taramasıyla, "başka klasöre
/// dokunuluyor mu" sorusu ise ancak İKİ AYRI klasör kurup birinin analizini çalıştırıp ötekinin
/// içeriğinin rapora HİÇ girmediği ölçülerek cevaplanır.
/// </para>
/// </remarks>
public sealed class SeoLogTenantIsolationTests : IDisposable
{
    private const string Fields =
        "#Fields: date time s-ip cs-method cs-uri-stem cs-uri-query s-port cs-username c-ip cs(User-Agent) cs(Referer) sc-status sc-substatus sc-win32-status time-taken";

    private const string GooglebotUa = "Mozilla/5.0+(compatible;+Googlebot/2.1;++http://www.google.com/bot.html)";

    private readonly string kok = Path.Combine(Path.GetTempPath(), "ada-seo-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(kok))
            {
                Directory.Delete(kok, recursive: true);
            }
        }
        catch (IOException)
        {
            // Geçici klasör silinemediyse test sonucu değişmez.
        }
    }

    private string GunlukKlasoru(long siteId)
    {
        var yol = Path.Combine(kok, "logs", $"W3SVC{siteId}");
        Directory.CreateDirectory(yol);
        return yol;
    }

    private static string Satir(string stem, string ip, int status = 200, string date = "", string ua = GooglebotUa)
    {
        var gun = date.Length == 0 ? DateTime.UtcNow.ToString("yyyy-MM-dd") : date;
        return $"{gun} 08:00:00 10.0.0.5 GET {stem} - 443 - {ip} {ua} - {status} 0 0 12";
    }

    private static void Yaz(string klasor, string ad, IEnumerable<string> satirlar)
    {
        var icerik = string.Join(Environment.NewLine, new[] { "#Software: Microsoft Internet Information Services 10.0", Fields }.Concat(satirlar));
        File.WriteAllText(Path.Combine(klasor, ad), icerik + Environment.NewLine, Encoding.UTF8);
    }

    private SeoLogAnalysisService Servis(
        IReadOnlyDictionary<string, long> siteler,
        ISeoReverseDnsResolver? dns = null,
        Func<long, string>? klasorCozucu = null)
    {
        var options = Microsoft.Extensions.Options.Options.Create(
            new PanelRuntimeOptions { DataRoot = Path.Combine(kok, "data") });
        var depo = new JsonFileStore(options);

        return new SeoLogAnalysisService(
            new SahteSiteler(siteler),
            new SeoReportStore(depo),
            new SeoLogReportStore(depo),
            dns ?? new SessizDns(),
            NullLogger<SeoLogAnalysisService>.Instance)
        {
            // GERÇEK zincir çalışır: site.Id -> klasör -> dosyalar. Yalnız kökü test belirler,
            // çünkü bu makinede IIS yok.
            LogDirectoryResolver = klasorCozucu ?? GunlukKlasoru
        };
    }

    /// <summary>
    /// <b>ASIL ÖLÇÜM:</b> A sitesinin analizi B sitesinin günlüğüne HİÇ dokunmaz.
    /// </summary>
    [Fact]
    public async Task Bir_kiraci_baska_kiracinin_gunlugunu_analiz_ettiremez()
    {
        Yaz(GunlukKlasoru(1), "u_ex260819.log", [Satir("/alice-gizli-sayfa", "66.249.66.1")]);
        Yaz(GunlukKlasoru(2), "u_ex260819.log", [Satir("/bob-gizli-sayfa", "66.249.66.9")]);

        var servis = Servis(new Dictionary<string, long> { ["alice.com"] = 1, ["bob.com"] = 2 });

        var alice = await servis.AnalyzeAsync("alice.com", new StartSeoLogAnalysisRequest());
        var bob = await servis.AnalyzeAsync("bob.com", new StartSeoLogAnalysisRequest());

        Assert.Equal(["/alice-gizli-sayfa"], alice.TopBotPaths.Select(item => item.Path).ToArray());
        Assert.DoesNotContain(alice.TopBotPaths, item => item.Path.Contains("bob", StringComparison.Ordinal));

        Assert.Equal(["/bob-gizli-sayfa"], bob.TopBotPaths.Select(item => item.Path).ToArray());
        Assert.DoesNotContain(bob.TopBotPaths, item => item.Path.Contains("alice", StringComparison.Ordinal));

        // Rapor okunan klasörü de söyler: yanlış klasör okunsaydı bu alan onu ele verirdi.
        Assert.EndsWith("W3SVC1", alice.LogDirectory, StringComparison.Ordinal);
        Assert.EndsWith("W3SVC2", bob.LogDirectory, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>MUTASYON KAPISI (yapısal):</b> isteğe bir yol/dosya alanı eklenirse bu test kırılır.
    /// Böyle bir alan, kiracıya "hangi günlüğü okuyayım" diye sormak olurdu ve iki halkalı
    /// korumanın ikincisini tek satırda yok ederdi.
    /// </summary>
    [Fact]
    public void Istek_govdesinde_yol_alani_YOKTUR()
    {
        var yasakli = new[] { "path", "directory", "folder", "file", "log", "root", "url", "host", "site" };

        var ihlal = typeof(StartSeoLogAnalysisRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(string))
            .Where(property => yasakli.Any(kelime => property.Name.Contains(kelime, StringComparison.OrdinalIgnoreCase)))
            .Select(property => property.Name)
            .ToArray();

        Assert.True(
            ihlal.Length == 0,
            "Gunluk analizi isteginde hedef belirleyebilecek alan(lar) var: " + string.Join(", ", ihlal));
    }

    /// <summary>
    /// <b>MUTASYON KAPISI (kaynak):</b> her iki günlük ucu da kapsam doğrulamasını çağırmalı.
    /// Okuma ucunu muaf tutmak, raporu (ve içindeki ziyaretçi IP'lerini) kapsam dışına açardı.
    /// </summary>
    [Fact]
    public void Gunluk_uclarinin_ikisi_de_kapsam_dogrulamasi_cagirir()
    {
        var kaynak = File.ReadAllText(Path.Combine(Kok(), "src", "AdaPanel.Api", "Controllers", "SeoController.cs"));

        foreach (var metot in new[] { "GetLogReportAsync", "AnalyzeLogsAsync" })
        {
            var basla = kaynak.IndexOf(metot, StringComparison.Ordinal);
            Assert.True(basla > 0, $"{metot} bulunamadi -- uc yeniden adlandirilmissa bu nobetci de guncellenmeli.");

            var govde = kaynak[basla..Math.Min(kaynak.Length, basla + 900)];
            Assert.Contains("CustomerAccess.EnsureSiteAsync", govde, StringComparison.Ordinal);
        }

        // Durum yazan uc denetim kaydi da birakmali (kalip: AuditCoverageTests).
        Assert.Contains("\"seo.log_analysis\"", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>ZSTD ARŞİVLERİ DE OKUNUR.</b> Log döndürme kapanmış günlükleri sıkıştırıyor; yalnız
    /// <c>*.log</c> okuyan bir analiz pencerenin eski günlerini SESSİZCE kaybederdi ve kullanıcı
    /// bunu "bot uğramamış" diye okurdu.
    /// </summary>
    [Fact]
    public async Task Sikistirilmis_gunlukler_de_okunur()
    {
        var klasor = GunlukKlasoru(7);
        var dun = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");

        Yaz(klasor, "u_ex260819.log", [Satir("/bugun", "66.249.66.1")]);

        // GERÇEK arşiv: panelin kendi sıkıştırma yolundan üretiliyor, elle bayt uydurulmuyor.
        var ham = Path.Combine(klasor, "u_ex260818.log");
        Yaz(klasor, "u_ex260818.log", [Satir("/dun", "66.249.66.1", date: dun)]);
        await BackupDumpCompression.CompressFileAsync(ham, ham + BackupDumpCompression.CompressedExtension);
        File.Delete(ham);

        var rapor = await Servis(new Dictionary<string, long> { ["arsiv.com"] = 7 })
            .AnalyzeAsync("arsiv.com", new StartSeoLogAnalysisRequest { Days = 7 });

        Assert.Equal(2, rapor.FilesScanned);
        Assert.Equal(1, rapor.FilesCompressed);
        Assert.Equal(
            ["/bugun", "/dun"],
            rapor.TopBotPaths.Select(item => item.Path).OrderBy(item => item, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// KLASÖR YOKSA SESSİZ SIFIR DÖNMEZ. "Bot hiç gelmemiş" ile "okuyacak bir şey bulamadım"
    /// aynı ekran olamaz: birincisi SEO kararı doğurur, ikincisi yapılandırma sorunudur.
    /// </summary>
    [Fact]
    public async Task Gunluk_klasoru_yoksa_sebep_bildirilir()
    {
        var servis = Servis(
            new Dictionary<string, long> { ["bos.com"] = 99 },
            klasorCozucu: siteId => Path.Combine(kok, "hic-olmayan", $"W3SVC{siteId}"));

        var rapor = await servis.AnalyzeAsync("bos.com", new StartSeoLogAnalysisRequest());

        Assert.Equal("log_directory_missing", rapor.Error);
        Assert.False(rapor.HasData);
        Assert.Contains("W3SVC99", rapor.LogDirectory, StringComparison.Ordinal);
    }

    /// <summary>
    /// SONUÇ DİSKE YAZILIR ve sayfa yenilendiğinde geri okunur. Yalnız HTTP yanıtında dönseydi,
    /// kullanıcı sayfayı yenilediğinde analizi yeniden başlatırdı (panelde daha önce virüs
    /// taramasında yaşanan hatanın aynısı).
    /// </summary>
    [Fact]
    public async Task Son_rapor_saklanir_ve_geri_okunur()
    {
        Yaz(GunlukKlasoru(11), "u_ex260819.log", [Satir("/", "66.249.66.1")]);
        var servis = Servis(new Dictionary<string, long> { ["kalici.com"] = 11 });

        Assert.Null(await servis.GetLatestAsync("kalici.com"));

        var yazilan = await servis.AnalyzeAsync("kalici.com", new StartSeoLogAnalysisRequest());
        var okunan = await servis.GetLatestAsync("kalici.com");

        Assert.NotNull(okunan);
        Assert.Equal(yazilan.GeneratedAtUtc, okunan.GeneratedAtUtc);
        Assert.Equal(yazilan.BotRequests, okunan.BotRequests);
    }

    /// <summary>
    /// AYRILMIŞ AYGIT ADI tuzağı burada da çözülmüş olmalı: adı <c>CON</c> olan bir sitede
    /// <c>CON.logs.json</c> bir AYGITTIR ve rapor yazılamaz — analiz tamamlanır, sonuç kaybolurdu.
    /// Kural SeoReportStore'da TEK yerde durur; bu test onun günlük tarafında da kullanıldığını
    /// ölçer.
    /// </summary>
    [Fact]
    public async Task Ayrilmis_aygit_adli_sitede_de_rapor_saklanir()
    {
        Yaz(GunlukKlasoru(13), "u_ex260819.log", [Satir("/", "66.249.66.1")]);
        var servis = Servis(new Dictionary<string, long> { ["CON"] = 13 });

        await servis.AnalyzeAsync("CON", new StartSeoLogAnalysisRequest());
        Assert.NotNull(await servis.GetLatestAsync("CON"));
    }

    private static string Kok()
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            if (Directory.Exists(Path.Combine(klasor, "src")) && Directory.Exists(Path.Combine(klasor, "frontend")))
            {
                return klasor;
            }

            klasor = Path.GetFullPath(Path.Combine(klasor, ".."));
        }

        throw new DirectoryNotFoundException("Depo koku bulunamadi.");
    }

    /// <summary>Site adı → IIS kimliği. Başka hiçbir çağrıyı desteklemez (kapsam dar tutulur).</summary>
    private sealed class SahteSiteler(IReadOnlyDictionary<string, long> siteler) : IIisSiteService
    {
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) =>
            siteler.TryGetValue(siteName, out var id)
                ? Task.FromResult(new SiteDto { Name = siteName, Id = id })
                : throw new KeyNotFoundException(siteName);

        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Ağa ÇIKMAYAN çözücü: gerçek DNS'e bağlı bir test ağın durumuna göre kırmızı verirdi.</summary>
    private sealed class SessizDns : ISeoReverseDnsResolver
    {
        public Task<string> ReverseAsync(string ip, CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<string>> ForwardAsync(string hostName, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
