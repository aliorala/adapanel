using System.Diagnostics;
using AdaPanel.Core.Dtos.Seo;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Statistics;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Seo;

/// <summary>SEO günlük analizinin denetleyiciye bakan yüzü.</summary>
public interface ISeoLogService
{
    /// <summary>Saklanan SON analiz; hiç çalıştırılmadıysa <c>null</c>.</summary>
    Task<SeoLogReportDto?> GetLatestAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>Analizi ÇALIŞTIRIR, sonucu saklar ve döner.</summary>
    Task<SeoLogReportDto> AnalyzeAsync(
        string siteName,
        StartSeoLogAnalysisRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// SEO GÜNLÜK ANALİZİ — arama motoru botlarının siteyi gerçekte nasıl gezdiğini IIS erişim
/// günlüklerinden ölçer (Plesk SEO Toolkit'in "Log File Analyzer" karşılığı).
/// </summary>
/// <remarks>
/// <para>
/// <b>KİRACI İZOLASYONU — bu modülün en kritik sınırı.</b> Okunacak klasör
/// <c>IisLogPaths.ResolveSiteDirectory(site.Id)</c> ile SİTENİN KENDİ IIS kimliğinden türetilir.
/// İstek gövdesinde yol alanı YOKTUR ve site adı denetleyicide
/// <c>CustomerAccess.EnsureSiteAsync</c> kapsam doğrulamasından geçer. Erişim günlüğü hassastır:
/// ziyaretçi IP'leri, yönetim panelinin adresleri, sorgu dizesindeki belirteçler oradadır. Bir
/// kiracının başka bir kiracının günlüğünü okutabilmesi, panelin en pahalı açığı olurdu.
/// Testi: <c>SeoLogTenantIsolationTests</c>.
/// </para>
/// <para>
/// <b>NEDEN İKİNCİ BİR AYRIŞTIRICI YAZILMADI:</b> W3C biçimini panelde tek bir yer bilir
/// (<see cref="W3cLogReader"/>); istatistik ekranı da aynı ayrıştırıcıyı kullanır.
/// </para>
/// <para>
/// <b>NEDEN <c>IRunningJobService</c> KULLANILMADI</b> (okundu, ÖLÇÜLDÜ, uymuyor): o kayıt SAATLER
/// süren, arka planı olmayan işler içindir (yedek, göç, bileşen kurulumu) ve iş TÜRÜ
/// <c>RunningJobKinds</c>'ta sayılıdır. Buradaki iş üç eksende birden sınırlıdır — en fazla
/// <see cref="SeoLogLimits.MaxFiles"/> dosya, <see cref="SeoLogLimits.MaxLines"/> satır ve
/// <see cref="SeoLogLimits.TotalTimeout"/> süre — yani ÜST SINIRI bilinen bir istektir.
/// <b>ÖLÇÜLDÜ</b> (bu makinede, gerçek W3C biçiminde üretilmiş günlük; test:
/// <c>W3cLogReaderSingleSourceTests.Buyuk_gunluk_akis_halinde_okunur_ve_sure_kayda_gecer</c>):
/// <b>200.000 satır / 29,2 MB → 1,09 sn</b> ham dosyadan, <b>0,72 sn</b> ZSTD arşivinden
/// (arşivden okumak DAHA HIZLI: diskten 29,2 MB yerine 0,8 MB okunuyor, çözme maliyeti bunun
/// altında kalıyor). Bu hızda satır bütçesinin TAMAMI (2 milyon satır) ~11 saniyedir. Ölçülmeden
/// "uzun sürebilir" varsayıp yeni bir iş türü icat etmek, tam da bu depoda beş kez ısıran
/// "aynı kuralın ikinci kopyası" hatası olurdu. Süre yine de aşılırsa iş KIRPILIR ve rapor bunu
/// <c>LimitReached</c> ile SÖYLER — sahte yüzde ya da sahte tamlık üretilmez.
/// </para>
/// </remarks>
public sealed class SeoLogAnalysisService(
    IIisSiteService iisSiteService,
    SeoReportStore reportStore,
    SeoLogReportStore logReportStore,
    ISeoReverseDnsResolver reverseDns,
    ILogger<SeoLogAnalysisService> logger) : ISeoLogService
{
    /// <summary>
    /// Site kimliğinden günlük klasörünü çözen kural. Üretimde <c>IisLogPaths</c>.
    /// </summary>
    /// <remarks>
    /// <b>internal ve DEĞİŞTİRİLEBİLİR olmasının tek sebebi ÖLÇÜLEBİLİRLİKTİR:</b> kiracı
    /// izolasyonu ancak "site A'nın analizi site B'nin klasörüne HİÇ dokunmuyor" diye ÖLÇÜLDÜĞÜNDE
    /// kanıtlanır, ve bu makinede gerçek IIS yoktur. Kuralı testte yeniden yazmak, testin kendi
    /// kopyasını doğrulaması olurdu; klasörü dışarıdan vermek ise GERÇEK zinciri (site.Id → klasör
    /// → dosyalar) çalıştırır. Varsayılan üretim yolunu HİÇBİR uç değiştiremez: burası public
    /// değildir ve istek gövdesinde karşılığı yoktur.
    /// </remarks>
    internal Func<long, string> LogDirectoryResolver { get; init; } = IisLogPaths.ResolveSiteDirectory;

    public async Task<SeoLogReportDto?> GetLatestAsync(string siteName, CancellationToken cancellationToken = default)
    {
        // Site GERÇEKTEN var mı: kapsam denetimini geçmiş ama silinmiş bir ad için diskteki bayat
        // raporu dönmek, olmayan bir site hakkında rapor göstermek olurdu.
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        return await logReportStore.ReadAsync(site.Name, cancellationToken);
    }

    public async Task<SeoLogReportDto> AnalyzeAsync(
        string siteName,
        StartSeoLogAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var days = SeoLogLimits.NormalizeDays(request.Days);
        var cutoff = DateTime.UtcNow.Date.AddDays(-(days - 1));

        // SİTENİN KENDİ klasörü — sabit kodlu değil, IIS'in gerçek ayarından (bkz. IisLogPaths).
        // Girdi SİTE KİMLİĞİDİR; istekten gelen hiçbir dizge bu yola karışmaz.
        var directory = LogDirectoryResolver(site.Id);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SeoLogLimits.TotalTimeout);

        var saat = Stopwatch.StartNew();
        var report = await RunAsync(site.Name, site.Id, directory, days, cutoff, request.VerifyBots, saat, timeout.Token, cancellationToken);

        await logReportStore.SaveAsync(site.Name, report, cancellationToken);
        return report;
    }

    private async Task<SeoLogReportDto> RunAsync(
        string siteName,
        long siteId,
        string directory,
        int days,
        DateTime cutoff,
        bool verifyBots,
        Stopwatch saat,
        CancellationToken budgetToken,
        CancellationToken callerToken)
    {
        if (!Directory.Exists(directory))
        {
            // SESSİZ SIFIR YASAK: klasör yoksa "bot hiç gelmemiş" demek, kullanıcıyı yanlış bir SEO
            // teşhisine iter. Sebep dile bağımsız bir anahtar olarak döner ve yol da gösterilir.
            logger.LogInformation(
                "SEO gunluk analizi: {Site} (id {Id}) icin gunluk klasoru yok: {Directory}", siteName, siteId, directory);

            return Compose(siteName, days, directory, saat, new SeoLogAnalyzer(cutoff), [], 0, 0, 0, 0, false, new SeoLogVerificationDto(), "log_directory_missing", string.Empty);
        }

        var files = SeoLogFileSet.Discover(directory, cutoff);
        var analyzer = new SeoLogAnalyzer(cutoff);

        long lines = 0;
        var scanned = 0;
        var compressed = 0;
        var skipped = 0;
        var limitReached = false;

        foreach (var file in files)
        {
            if (budgetToken.IsCancellationRequested || lines >= SeoLogLimits.MaxLines)
            {
                // Kalanları SAYARIZ, sessizce atmayız: kullanıcı raporun neyi kapsamadığını görmeli.
                skipped = files.Count - scanned;
                limitReached = true;
                break;
            }

            try
            {
                using var reader = SeoLogFileSet.OpenText(file.Path);
                await foreach (var record in W3cLogReader.ReadAsync(reader, budgetToken))
                {
                    if (lines >= SeoLogLimits.MaxLines)
                    {
                        limitReached = true;
                        break;
                    }

                    lines++;
                    analyzer.Add(record);
                }

                scanned++;
                if (file.Compressed)
                {
                    compressed++;
                }
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
            {
                // SÜRE bütçesi doldu (çağıran iptal etmedi). Elde ne varsa onu bildiririz.
                skipped = files.Count - scanned;
                limitReached = true;
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // TEK dosya okunamadı — analizi düşürmek yerine sayıp devam ediyoruz. Bozuk/kilitli
                // bir arşiv yüzünden 29 günün ölçümünü kaybetmek, hiç ölçmemekten kötüdür.
                skipped++;
                logger.LogWarning(exception, "SEO gunluk analizi: {File} okunamadi, atlandi.", file.Path);
            }
        }

        var known = await ReadKnownPathsAsync(siteName, callerToken);
        var verification = await new SeoBotVerifier(reverseDns)
            .VerifyAsync(analyzer.BuildClaims(), verifyBots, budgetToken);

        return Compose(
            siteName, days, directory, saat, analyzer, known.Paths, scanned, compressed, skipped, lines,
            limitReached || analyzer.Saturated, verification, string.Empty, known.ReportId);
    }

    private SeoLogReportDto Compose(
        string siteName,
        int days,
        string directory,
        Stopwatch saat,
        SeoLogAnalyzer analyzer,
        IReadOnlyCollection<string> knownPaths,
        int filesScanned,
        int filesCompressed,
        int filesSkipped,
        long linesScanned,
        bool limitReached,
        SeoLogVerificationDto verification,
        string error,
        string knownSourceId)
    {
        var taban = analyzer.Build(knownPaths);

        return new SeoLogReportDto
        {
            SiteName = siteName,
            GeneratedAtUtc = DateTime.UtcNow,
            DurationMs = saat.ElapsedMilliseconds,
            PeriodDays = days,
            LogDirectory = directory,
            Error = error,
            HasData = taban.HasData,
            FilesScanned = filesScanned,
            FilesCompressed = filesCompressed,
            FilesSkipped = filesSkipped,
            LinesScanned = linesScanned,
            LimitReached = limitReached,
            TotalRequests = taban.TotalRequests,
            BotRequests = taban.BotRequests,
            Engines = taban.Engines,
            DailyBotRequests = taban.DailyBotRequests,
            TopBotPaths = taban.TopBotPaths,
            ErrorPaths = taban.ErrorPaths,
            Waste = taban.Waste,
            WastedRequests = taban.WastedRequests,
            UncrawledPaths = taban.UncrawledPaths,
            UncrawledSourceReportId = knownSourceId,
            KnownPageCount = taban.KnownPageCount,
            Verification = verification
        };
    }

    /// <summary>
    /// "Sitede hangi sayfalar VAR" bilgisinin kaynağı: panelin KENDİ SEO taraması.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Burada site haritası ayrıştırılmaz — <c>SeoSitemapDto</c> adres LİSTESİNİ değil sayısını
    /// saklar, ve sitemap'i yeniden indirmek bu okuma ucunu bir tarayıcıya çevirirdi. Tarayıcının
    /// zaten ölçtüğü sayfa listesini kullanmak, aynı işi ikinci kez yapmamaktır.
    /// </para>
    /// <para>
    /// Yalnız <b>200 dönen, hata almamış ve noindex OLMAYAN</b> sayfalar sayılır: 404 dönen ya da
    /// bilerek dizine kapatılmış bir sayfanın "botun uğramadığı sayfa" diye listelenmesi yanlış
    /// alarm olurdu.
    /// </para>
    /// </remarks>
    private async Task<(IReadOnlyCollection<string> Paths, string ReportId)> ReadKnownPathsAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        var history = await reportStore.ReadAsync(siteName, cancellationToken);
        var latest = history.Reports.FirstOrDefault(report =>
            string.Equals(report.Status, "completed", StringComparison.Ordinal));

        if (latest is null)
        {
            return ([], string.Empty);
        }

        var paths = latest.Pages
            .Where(page => page.StatusCode == 200 && page.Error.Length == 0 && !page.NoIndex)
            .Select(page => page.Path)
            .ToArray();

        return (paths, latest.Id);
    }
}
