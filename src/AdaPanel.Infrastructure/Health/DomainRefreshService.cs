using AdaPanel.Core.Dtos.Health;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Mail;
using System.Text.Json;
using System.Diagnostics;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;
using AdaPanel.Core.Dtos.Firewall;
using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Health;

/// <inheritdoc />
internal sealed class DomainRefreshService(
    IIisSiteService siteService,
    IDnsService dnsService,
    IMailService mailService,
    IFirewallService firewallService,
    ISslService sslService,
    JsonFileStore fileStore,
    ILogger<DomainRefreshService> logger,
    IDnsInspectionService? dnsInspectionService = null) : IDomainRefreshService
{
    public async Task<DomainRefreshResultDto> RefreshAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(siteName, applyChanges: true, cancellationToken);

        // Tek site onarilirken de sunucu geneli adimlar bir kez calisir: kullanici site
        // sayfasindan onardiginda posta sertifikasi ve karsilama adi geride kalmamali.
        result.MailCertificateRefreshed = await RunServerWideAsync(cancellationToken);

        // TEK SITE onarildiginda da denetim onbellegi dusurulur.
        //
        // Onceden yalnizca TOPLU onarma dusuruyordu; site sayfasindan onaran kullanici, sorun
        // gercekten cozulmus olsa bile "Dikkat" rozetinin 10 dakika boyunca ayni uyariyi
        // gostermesini izliyordu ve haklı olarak "onardim ama gitmedi" diyordu.
        InvalidateInspectionCache();
        return result;
    }

    /// <summary>
    /// Bir istisnanin KULLANICIYA ANLATAN metnini uretir.
    /// <para>
    /// Neden gerekli: panelin ic hatalari <c>AppException</c> ile firlatilir ve
    /// <c>Message</c> yalnizca bir ceviri anahtaridir ("dns.sync_failed"). Asil sebep
    /// (isletim sisteminin verdigi metin) <c>Details</c> alanindadir. Rapor yalnizca
    /// Message'i tasidigi icin kullanici alti kez "dns.sync_failed" goruyor ve HICBIR
    /// sey ogrenmiyordu -- hata gosteriliyor ama anlatmiyordu.
    /// </para>
    /// </summary>
    private static string DescribeError(Exception exception)
    {
        if (exception is AppException app && !string.IsNullOrWhiteSpace(app.Details))
        {
            var details = app.Details.Trim();
            const int maxLength = 1500;
            if (details.Length > maxLength)
            {
                details = details[..maxLength] + "...";
            }

            return $"{app.MessageKey}: {details}";
        }

        return exception.Message;
    }

    /// <summary>Denetim sonucu artik bayat: durum degisti.</summary>
    private static void InvalidateInspectionCache()
    {
        Interlocked.Increment(ref _inspectionGeneration);
        Volatile.Write(ref _cachedInspection, null);
    }

    public async Task<DomainRefreshResultDto> InspectAsync(string siteName, CancellationToken cancellationToken = default)
    {
        if (dnsInspectionService is null) return await RunAsync(siteName, false, cancellationToken);
        var site = await siteService.GetSiteAsync(siteName, cancellationToken);
        var snapshots = await ReadDnsInspectionsAsync([site], cancellationToken);
        return await RunAsync(siteName, false, cancellationToken, snapshots[site.Name]);
    }

    private async Task<DomainRefreshResultDto> RunAsync(string siteName, bool applyChanges, CancellationToken cancellationToken,
        DnsInspectionDto? snapshot = null)
    {
        var result = new DomainRefreshResultDto { SiteName = siteName, ZoneName = siteName };
        // Only this read-only inspection shares measurements. Repairs must read again after writes.
        var reads = applyChanges ? null : new InspectionReads(siteName, dnsService, mailService, cancellationToken, snapshot);
        if (!string.IsNullOrWhiteSpace(snapshot?.Error))
        {
            result.Error = snapshot.Error;
            return result;
        }

        try
        {
            // 1) DNS varsayılanları: eksik olanlar EKLENİR, var olanlara dokunulmaz. Yıkıcı olmama
            //    şartı burada kritik — kullanıcı MX ya da SPF değerini elle düzeltmiş olabilir.
            // ADIM YALITIMI: bir adimin hatasi digerlerini IPTAL ETMEMELI.
            //
            // Onceden hepsi tek try icindeydi. Yazilamayan tek bir DKIM kaydi yuzunden DNS
            // varsayilanlari adimi firlatinca, ondan sonraki HER SEY atlaniyordu: posta kayitlari,
            // bolgenin yeniden yayinlanmasi, sertifika senkronu ve SMTP karsilama adi. Canlida tam
            // olarak bu oldu -- kullanici "onar" diyor, hicbiri calismiyor ve sebep gorunmuyor.
            try
            {
                result.AddedDnsRecords = snapshot?.MissingDefaults
                    ?? await dnsService.RepairDefaultsAsync(siteName, applyChanges, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "DNS varsayilanlari uygulanamadi ({SiteName}).", siteName);
                result.Error = DescribeError(exception);
            }

            // 2) Posta kayıtları (DKIM dahil): DKIM anahtarları posta sunucusunda üretilir, DNS
            //    varsayılanlarında yoktur. Posta alan adı yoksa bu adım sessizce atlanır — posta
            //    kullanmayan bir siteye DKIM yazmak anlamsız olurdu.
            //    Denetim (kuru) modunda hiç çağrılmaz: yazma yapan tek adım budur.
            if (!applyChanges)
            {
                // DENETIM (kuru) modu da posta kayitlarina BAKAR -- yalnizca yazmaz.
                //
                // Neden sart: "Eksikleri tamamla" dugmesi bu denetimin sonucuna gore gorunuyor.
                // Denetim yalnizca DNS sablonuna baksaydi (eski hali), DKIM kaydi eksik olan bir
                // alan adi "guncel" sayilir, dugme GIZLENIR ve kullanici duzeltmeye hic ulasamazdi.
                // Canli sunucuda tam olarak bu oldu: DKIM eksikti, onarma bunu duzeltebiliyordu,
                // ama dugme gorunmedigi icin kullanici "onar butonu nerede" diye sordu.
                result.AppliedMailRecords = await CountMissingMailRecordsAsync(siteName, reads!, cancellationToken);
            }

            if (applyChanges)
            {
                MailRecordErrors = [];
                result.AppliedMailRecords = await ApplyMailRecordsAsync(siteName, cancellationToken);
                if (MailRecordErrors.Count > 0)
                {
                    // Hata METNI kullaniciya ulasmali: "yazilamadi" demek yetmez, isletim
                    // sisteminin verdigi sebep olmadan sorun korlemesine aranir.
                    //
                    // EKLENIR, uzerine YAZILMAZ: 1. adim (DNS varsayilanlari) da patlamis olabilir
                    // ve o hata daha onemli olabilir. Uzerine yazmak, tam da bu turlarda ayikladigimiz
                    // "hatayi gizleme" davranisinin baska bir bicimi olurdu.
                    var postaHatasi = string.Join(" | ", MailRecordErrors);
                    result.Error = string.IsNullOrWhiteSpace(result.Error)
                        ? postaHatasi
                        : result.Error + " || " + postaHatasi;
                }

                // 2b) Bolgeyi Windows DNS'e YENIDEN YAYINLA.
                //
                // Neden sart: RepairDefaultsAsync eksik kayit yoksa erken donuyor ve Windows DNS'e
                // hic yazmiyor. Oysa panelin dosyasi dogru, Windows tarafi bozuk olabilir -- kayit
                // yazilirken hata almis (canlida DKIM'de tam olarak bu oldu), bolge elle silinmis
                // ya da kurulusta yayinlanamamis olabilir. O durumda onarma "eksik yok" deyip
                // geciyor ve kullanici duzeltmeye hic ulasamiyordu. Yayinlama panelin durumunu
                // oldugu gibi basar; kullanicinin kayitlarina dokunmaz.
                await TryRepublishZoneAsync(siteName, cancellationToken);

                // 3) Posta sunucusunun TLS sertifikasi. Stalwart'a yalniz YENILENIRKEN gonderilir;
                //    dolayisiyla panelin sertifika birlestirmesindeki bir kusur guncelleme sonrasi
                //    bile kendiliginden duzelmez, uc ay sonraki yenilemeyi bekler. Canlida tam
                //    olarak bu oldu (ara sertifika dusuyordu). Senkron icerik karmasini
                //    karsilastirdigi icin degisiklik yoksa hicbir sey yapmaz -- her onarmada
                //    gereksiz yeniden gonderim olmaz.
                // SUNUCU GENELI adimlar (posta sertifikasi, SMTP karsilama adi) burada DEGIL,
                // RunServerWideAsync icinde -- tarama basina BIR KEZ. Ikisi de sunucuda tektir;
                // site dongusunde calistirmak 40 siteli bir sunucuda ayni isi 40 kez yapar ve
                // her biri birkac CLI sureci baslatir. Ayrica sunucu geneli bir olayi ILK sitenin
                // sonucuna yazmak raporu keyfi bir siteye baglardi.

                // 5) Domainsiz onizlemenin biraktigi gecici host'u temizle.
                result.RemovedPreviewBindings = await TryRemovePreviewBindingsAsync(siteName, cancellationToken);
            }

            // 4) Ucuz saglik kontrolleri. Hicbiri kendiliginden YIKICI is yapmaz; duzeltilebilenler
            //    duzeltilir, geri kalanlar onay/bilgi olarak raporlanir.
            result.Findings = await CollectFindingsAsync(siteName, cancellationToken, reads);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Tek alan adının hatası TOPLU işlemi durdurmamalı: 40 sitelik bir sunucuda ilk hatada
            // durmak, kalan 39 sitenin eksik kalmasına ve bunun görünmemesine yol açardı.
            logger.LogWarning(exception, "Alan adı tazelenemedi: {SiteName}", siteName);
            result.Error = DescribeError(exception);
        }

        return result;
    }

    /// <summary>
    /// Denetim sonucunun önbelleği. Servis Scoped olduğu için istekler arasında yaşaması gerekiyor.
    /// <para>
    /// Önbellek ŞART: "Dikkat" rozeti bu denetimi 60 SANİYEDE BİR çağırıyor ve denetim SİTE BAŞINA
    /// IIS yapılandırmasını açıp zone dosyasını okuyor. 40 siteli bir sunucuda bu, boştaki panelde
    /// saatte binlerce dosya okuması ve applicationHost.config ayrıştırması demek — kullanıcı hiçbir
    /// şey yapmadan sunucu sürekli meşgul görünür.
    /// </para>
    /// <para>
    /// 10 dakika güvenli: eksik varsayılanlar ancak panel güncellemesiyle ya da kullanıcının kayıt
    /// silmesiyle oluşur, ikisi de saniyeler içinde fark edilmesi gereken şeyler değildir. Tazeleme
    /// UYGULANDIĞINDA önbellek hemen düşürülür, böylece rozet "hâlâ eksik var" demeye devam etmez.
    /// </para>
    /// </summary>
    private static readonly TimeSpan InspectionFreshness = TimeSpan.FromMinutes(10);

    private sealed record InspectionCache(DomainRefreshSummaryDto Summary, DateTime AtUtc, string Fingerprint, long Generation);
    private static InspectionCache? _cachedInspection;
    private static long _inspectionGeneration;
    private static readonly SemaphoreSlim InspectionGate = new(1, 1);

    /// <summary>
    /// Zone durumunun parmak izi: kayıt dosyası, site kimlikleri ve çalışma durumları.
    /// <para>
    /// Süreye ek olarak bu gerekli: yalnız zamana bakan bir önbellek, kullanıcı bir kayıt sildikten
    /// sonra 10 dakika boyunca "eksik yok" demeye devam ederdi. Yani uyarı, tam da uyarması gereken
    /// anda susardı. Canlı testte birebir bu görüldü: MX kaydı silindi, denetim yine 0 eksik dedi,
    /// ama tazeleme çalıştırılınca 1 alan adının güncellendiği ortaya çıktı.
    /// </para>
    /// </summary>
    private string ComputeFingerprint(IReadOnlyCollection<SiteDto> sites)
    {
        try
        {
            var path = fileStore.ResolvePath("config/dns-zones.json");
            var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0L;
            return JsonSerializer.Serialize(new
            {
                Path = path,
                Stamp = stamp,
                Sites = sites.OrderBy(site => site.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(site => new { site.Id, site.Name, site.State })
            });
        }
        catch (Exception)
        {
            // Parmak izi okunamıyorsa önbelleği GEÇERSİZ say: bayat veri göstermektense yeniden ölç.
            return Guid.NewGuid().ToString("N");
        }
    }

    public async Task<DomainRefreshSummaryDto> RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        // Sunucu geneli adimlar taramadan ONCE bir kez: sertifika ve karsilama adi tum
        // sitelerin ortak altyapisidir, site basina tekrarlanmasi anlamsizdir.
        await RunServerWideAsync(cancellationToken);

        var summary = await RunAllAsync(applyChanges: true, cancellationToken);

        // Tazeleme durumu DEĞİŞTİRDİ: bayat denetim sonucunu tutmak, rozetin düzeltilmiş bir sunucuda
        // 10 dakika boyunca "eksik var" demesine yol açardı.
        InvalidateInspectionCache();
        return summary;
    }

    private static DomainRefreshSummaryDto? ReadCache(string fingerprint)
    {
        var cache = Volatile.Read(ref _cachedInspection);
        return cache is not null && cache.Generation == Interlocked.Read(ref _inspectionGeneration) &&
            string.Equals(cache.Fingerprint, fingerprint, StringComparison.Ordinal) &&
            DateTime.UtcNow - cache.AtUtc < InspectionFreshness ? cache.Summary : null;
    }

    public async Task<DomainRefreshSummaryDto> InspectAllAsync(CancellationToken cancellationToken = default)
    {
        // Önbellek HEM süreye HEM zone durumuna bağlı: kullanıcı bir kayıt sildiği anda parmak izi
        // değişir ve uyarı hemen görünür. Yalnız süreye bakmak, uyarıyı tam gerektiği anda susturuyordu.
        var sites = await siteService.GetSitesAsync(cancellationToken);
        var fingerprint = ComputeFingerprint(sites);
        if (ReadCache(fingerprint) is { } cached)
        {
            return cached;
        }

        // Tek geçit: birden çok panel sekmesi aynı anda rozetini yenilediğinde aynı ağır taramanın
        // paralel kopyalarını çalıştırmayalım.
        await InspectionGate.WaitAsync(cancellationToken);
        try
        {
            // The sites/config may have changed while another inspection held the gate.
            var generation = Interlocked.Read(ref _inspectionGeneration);
            sites = await siteService.GetSitesAsync(cancellationToken);
            fingerprint = ComputeFingerprint(sites);
            if (ReadCache(fingerprint) is { } current)
            {
                return current;
            }

            var summary = await RunAllAsync(applyChanges: false, cancellationToken, sites);
            if (summary.FailedSites == 0 &&
                !summary.ServerFindings.Concat(summary.Results.SelectMany(item => item.Findings))
                    .Any(item => item.Key == "repair.inspection_failed"))
            {
                Volatile.Write(ref _cachedInspection, new InspectionCache(summary, DateTime.UtcNow, fingerprint, generation));
            }
            return summary;
        }
        finally
        {
            InspectionGate.Release();
        }
    }

    private async Task<DomainRefreshSummaryDto> RunAllAsync(bool applyChanges, CancellationToken cancellationToken,
        IReadOnlyCollection<SiteDto>? sites = null)
    {
        sites ??= await siteService.GetSitesAsync(cancellationToken);
        var results = new List<DomainRefreshResultDto>(sites.Count);
        var timer = Stopwatch.StartNew();
        var snapshots = !applyChanges && dnsInspectionService is not null
            ? await ReadDnsInspectionsAsync(sites, cancellationToken) : null;
        var dnsMs = timer.ElapsedMilliseconds;
        timer.Restart();

        foreach (var site in sites)
        {
            results.Add(await RunAsync(site.Name, applyChanges, cancellationToken, snapshots?[site.Name]));
        }

        var siteMs = timer.ElapsedMilliseconds;
        timer.Restart();
        var serverFindings = await CollectServerFindingsAsync(applyChanges, cancellationToken);
        logger.LogInformation("Domain inspection durations (ms): dns={DnsMs}, sites={SiteMs}, server={ServerMs}, repair={Repair}",
            dnsMs, siteMs, timer.ElapsedMilliseconds, applyChanges);
        return new DomainRefreshSummaryDto
        {
            Results = results,
            TotalSites = results.Count,
            UpdatedSites = results.Count(item => item.Error.Length == 0 && !item.AlreadyCurrent),
            FailedSites = results.Count(item => item.Error.Length > 0),
            ServerFindings = serverFindings
        };
    }

    private async Task<IReadOnlyDictionary<string, DnsInspectionDto>> ReadDnsInspectionsAsync(
        IReadOnlyCollection<SiteDto> sites, CancellationToken cancellationToken)
    {
        try
        {
            var snapshots = await dnsInspectionService!.InspectZonesAsync(sites, cancellationToken);
            return sites.ToDictionary(site => site.Name, site => snapshots.TryGetValue(site.Name, out var snapshot)
                ? snapshot : new DnsInspectionDto { Error = "DNS inspection result missing." }, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Bulk DNS inspection failed.");
            return sites.ToDictionary(site => site.Name, _ => new DnsInspectionDto { Error = DescribeError(exception) },
                StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Posta sunucusunun ürettiği kayıtları (MX/SPF/DKIM/DMARC) zone'a yazar.
    /// Posta kurulu değilse ya da bu alan adının posta alan adı yoksa 0 döner.
    /// </summary>
    /// <summary>
    /// Posta sertifikasini gerekiyorsa yeniden gonderir. Posta sunucusu kurulu degilse ya da
    /// sertifika Let's Encrypt kaynakli degilse sessizce atlanir -- bu bir hata degildir.
    /// </summary>
    private async Task<bool> TrySyncMailCertificateAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await mailService.SyncCertificateAsync(cancellationToken) is not null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Posta sertifikasi senkronize edilemedi; posta sunucusu kurulu olmayabilir.");
            return false;
        }
    }

    /// <summary>
    /// Posta sunucusunun urettigi kayitlardan KACININ bolgede eksik oldugunu sayar. Hicbir sey
    /// yazmaz -- denetim modunun yazma yapmama sozu korunur.
    /// </summary>
    private async Task<int> CountMissingMailRecordsAsync(string siteName, InspectionReads reads, CancellationToken cancellationToken)
    {
        try
        {
            var dns = await reads.Mail.Value;
            var applicable = dns.Records.Where(record => !string.IsNullOrWhiteSpace(record.Value)).ToList();
            if (applicable.Count == 0)
            {
                return 0;
            }

            // GERCEKTEN YAYINLANMIS kayitlara bakilir, panelin kendi dosyasina DEGIL.
            //
            // Neden: panel kaydi once durum dosyasina yazar, sonra Windows DNS'e basar. Basma
            // basarisiz olursa dosyada kayit VARDIR ama bolgede YOKTUR. Dosyaya bakan denetim
            // "eksik yok" der ve panel, kayit yayinlanmamisken SUSAR. Canlida tam olarak bu oldu:
            // DKIM'in RSA kaydi bolgede yokken panel hicbir uyari vermiyordu.
            //
            // Deger DEGIL, "TUR|konak" karsilastirilir: Windows DNS uzun TXT'i parcalara bolerek
            // saklar, dolayisiyla degerin birebir esitligini beklemek her turda yanlis alarm uretir.
            var yayinlanan = await reads.Published.Value;
            if (yayinlanan is null)
            {
                // Olcemedik. "Eksik yok" demek yerine panelin kendi dosyasina duseriz -- ama bu
                // yalnizca Windows DNS hic yokken olur (o durumda zaten ayri bir bulgu uretiliyor).
                var dosya = string.Equals(dns.Domain, siteName, StringComparison.OrdinalIgnoreCase)
                    ? (await reads.Zone.Value).Records.Select(record => $"{record.Type}|{record.Host}|{record.Value}")
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : await ReadZoneKeysAsync(dns.Domain, cancellationToken);
                return applicable.Count(record => !dosya.Contains($"{record.Type}|{record.Host}|{record.Value}"));
            }

            return applicable.Count(record => !yayinlanan.Contains($"{record.Type}|{record.Host}"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Posta kayitlari denetlenemedi ({SiteName}); posta alan adi yok olabilir.", siteName);
            return 0;
        }
    }

    /// <summary>
    /// Bolgeyi Windows DNS'e yeniden yayinlar. Yikici degildir: panelin durumunu oldugu gibi yazar,
    /// yeni kayit uretmez, kullanicinin ekledigi kayitlari silmez.
    /// </summary>
    private async Task TryRepublishZoneAsync(string siteName, CancellationToken cancellationToken)
    {
        try
        {
            await dnsService.EnsureZonePublishedAsync(siteName, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Bolge yeniden yayinlanamadi ({SiteName}).", siteName);
        }
    }

    /// <summary>
    /// Sunucu geneli bulgular. Site basina DEGIL, tum tarama icin BIR KEZ calisir.
    /// <para>
    /// Guvenlik duvari kurallari otomatik acilir: bu duzeltme geri alinabilir, veri silmez ve
    /// eksikligi dogrudan "hizmet disaridan erisilemiyor" demektir. Panel portu bilerek
    /// DISARIDA birakilir -- onu acmak panelin yonetim arayuzunu internete acmaktir ve bu bir
    /// guvenlik karari, onarma isi degil.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<RepairFindingDto>> CollectServerFindingsAsync(bool applyChanges, CancellationToken cancellationToken)
    {
        var findings = new List<RepairFindingDto>();

        // SMTP KARSILAMA ADI.
        //
        // Bu bulgu olmadan kisir dongu olusuyordu: onarma adi duzeltebiliyor ama hicbir denetim
        // eksikligi gormuyor, dolayisiyla dugme cikmiyor, dolayisiyla onarma hic calismiyordu.
        // Canlida tam olarak bu oldu -- karsilama "vm-185-7-242-225" kaldigi halde panel sustu.
        try
        {
            if (await mailService.EnsureServerHostnameAsync(applyChanges: false, cancellationToken))
            {
                findings.Add(new RepairFindingDto
                {
                    Key = "repair.mail_hostname_mismatch",
                    Severity = "warn",
                    // Repaired = false, applyChanges DEGIL.
                    //
                    // Bu bulgu yalnizca uyumsuzluk HALA VARKEN uretiliyor (olcum applyChanges:false
                    // ile yapiliyor). Onarma denendigi halde basarisiz olduysa "onarildi" demek
                    // duz yalan olurdu -- ustelik AttentionService onarilmis bulgulari eliyor,
                    // yani rozet gercek sorunu GIZLERDI.
                    Repaired = false
                });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Posta karsilama adi denetlenemedi; posta sunucusu kurulu olmayabilir.");
            findings.Add(InspectionFailure("SMTP hostname", exception));
        }

        try
        {
            // panelPort=0: katalogdaki panel girdisi zaten disarida birakiliyor.
            var ports = await firewallService.GetPortStatusAsync(0, cancellationToken);
            if (ports.Any(item => !item.FirewallMeasured || !item.ListeningMeasured))
                findings.Add(InspectionFailure("Firewall / TCP listeners"));
            foreach (var port in ports.Where(item => item.Required && item.FirewallMeasured && !item.FirewallAllowed && !string.Equals(item.Category, "panel", StringComparison.OrdinalIgnoreCase)))
            {
                var finding = new RepairFindingDto
                {
                    Key = "repair.firewall_blocked",
                    Severity = "fail",
                    // Port bilgisi ayrinti alaninda: rozet tarafi bulgulari Key'e gore
                    // tekillestirdigi icin, uc kapali port varken tek madde gorunuyordu.
                    // Tekillestirme anahtarina port da giriyor (asagida Detail'den uretiliyor).
                    Detail = $"{port.Protocol}/{port.Port} ({port.ServiceKey})"
                };

                if (applyChanges)
                {
                    try
                    {
                        await firewallService.AllowPortAsync(
                            new UpdateFirewallRuleRequest
                            {
                                Port = port.Port,
                                Protocol = port.Protocol,
                                RuleName = $"AdaPanel {port.ServiceKey} ({port.Protocol}/{port.Port})"
                            },
                            cancellationToken);
                        finding.Repaired = true;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning(exception, "Guvenlik duvari kurali acilamadi: {Port}", port.Port);
                        finding.Detail += " - " + exception.Message;
                    }
                }

                findings.Add(finding);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Guvenlik duvari durumu okunamadi.");
            findings.Add(InspectionFailure("Firewall", exception));
        }

        return findings;
    }

    private static RepairFindingDto InspectionFailure(string source, Exception? exception = null) => new()
    {
        Key = "repair.inspection_failed", Severity = "warn", NeedsConfirmation = true,
        Detail = exception is null ? source : $"{source}: {DescribeError(exception)}"
    };

    /// <summary>
    /// Domainsiz onizlemenin biraktigi gecici bagalamalari kaldirir ve kac tane kaldirildigini doner.
    /// <para>
    /// Onizleme host'u (<c>&lt;site&gt;.&lt;ip&gt;.nip.io</c>) kullanicinin sahip oldugu bir alan adi
    /// DEGILDIR; yalnizca alan adi yonlendirilmeden siteyi acabilmek icin uretilir. Kalinca panelde
    /// gercek bir alan adi gibi listeleniyor ve yer kapliyordu.
    /// </para>
    /// <para>
    /// Silmek KAYIP DEGIL: onizleme ucu idempotenttir, kullanici tekrar "Onizleme" dediginde
    /// baglamayi yeniden olusturur. Yani temizlik ozelligi bozmaz, yalnizca artigi kaldirir.
    /// </para>
    /// </summary>
    private async Task<int> TryRemovePreviewBindingsAsync(string siteName, CancellationToken cancellationToken)
    {
        try
        {
            var site = await siteService.GetSiteAsync(siteName, cancellationToken);

            // ONIZLEME HALA GEREKLIYSE SILINMEZ.
            //
            // Onizleme host'u, alan adi henuz sunucuya yonlendirilmemis siteler icin TEK erisim
            // yoludur. Alan adini yeni almis bir musteri "Eksikleri tamamla"ya bastiginda sitesine
            // erisimini kaybetmemeli. Bu yuzden yalnizca site GERCEK bir alan adiyla erisilebilir
            // durumdayken temizlik yapilir; aksi halde artik degil, hala kullanilan bir baglantidir.
            var gercekBaglama = site.Bindings.Any(binding =>
                !string.IsNullOrWhiteSpace(binding.HostName)
                && !binding.HostName.EndsWith(".nip.io", StringComparison.OrdinalIgnoreCase));

            if (!gercekBaglama)
            {
                return 0;
            }

            var previews = site.Bindings
                .Where(binding => !string.IsNullOrWhiteSpace(binding.HostName)
                                  && binding.HostName.EndsWith(".nip.io", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (previews.Count == 0)
            {
                // Temizlenecek bir sey yoksa sertifika deposuna dokunma: her sitede gereksiz
                // okuma/yazma yapmanin anlami yok.
                return 0;
            }

            var kaldirilan = 0;
            foreach (var binding in previews)
            {
                try
                {
                    await siteService.RemoveBindingAsync(
                        siteName,
                        new RemoveSiteBindingRequest
                        {
                            Protocol = binding.Protocol,
                            HostName = binding.HostName,
                            IpAddress = binding.IpAddress,
                            Port = binding.Port
                        },
                        cancellationToken);
                    kaldirilan++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogDebug(exception, "Onizleme baglamasi kaldirilamadi: {Host}", binding.HostName);
                }
            }

            // Baglamayla birlikte SERTIFIKA KAYDI da gider: aksi halde oksuz kayit SSL listesinde
            // gercek bir sertifika gibi durmaya devam ederdi.
            await sslService.RemovePreviewCertificatesAsync(siteName, cancellationToken);

            return kaldirilan;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Onizleme baglamalari okunamadi ({SiteName}).", siteName);
            return 0;
        }
    }

    /// <summary>
    /// SUNUCU GENELI onarma adimlari: posta sertifikasi senkronu ve SMTP karsilama adi.
    /// Sertifika yeniden gonderildiyse true doner.
    /// <para>
    /// Site dongusunun DISINDA calisir. Ikisi de sunucuda tektir; site basina calistirmak ayni
    /// isi N kez yapar (her biri birkac CLI sureci) ve sonucu keyfi bir siteye yazar.
    /// </para>
    /// </summary>
    private async Task<bool> RunServerWideAsync(CancellationToken cancellationToken)
    {
        var sertifika = await TrySyncMailCertificateAsync(cancellationToken);
        await TryEnsureMailHostnameAsync(cancellationToken);
        return sertifika;
    }

    /// <summary>
    /// Posta sunucusunun karsilama adini garanti eder. Posta sunucusu kurulu degilse sessizce atlar.
    /// </summary>
    private async Task TryEnsureMailHostnameAsync(CancellationToken cancellationToken)
    {
        try
        {
            await mailService.EnsureServerHostnameAsync(true, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Posta karsilama adi ayarlanamadi; posta sunucusu kurulu olmayabilir.");
        }
    }

    private async Task<IReadOnlyList<RepairFindingDto>> CollectFindingsAsync(string siteName, CancellationToken cancellationToken,
        InspectionReads? reads = null)
    {
        var findings = new List<RepairFindingDto>();

        // --- Site durumu ---
        // IIS API'si uzerinden okunur (PowerShell degil), bu yuzden ucuzdur.
        try
        {
            var site = await siteService.GetSiteAsync(siteName, cancellationToken);
            if (string.Equals(site.State, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(InspectionFailure("IIS"));
            }
            else if (!string.Equals(site.State, "Started", StringComparison.OrdinalIgnoreCase))
            {
                // ONAY ISTER: kullanici siteyi BILEREK durdurmus olabilir (bakim, odeme, ihlal).
                // Onarmanin bunu sessizce geri almasi, kullanicinin kararini ezmek olurdu.
                findings.Add(new RepairFindingDto
                {
                    Key = "repair.site_stopped",
                    Severity = "fail",
                    NeedsConfirmation = true,
                    Detail = site.State
                });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Site durumu okunamadi ({SiteName}).", siteName);
            findings.Add(InspectionFailure("IIS", exception));
        }

        // --- Bolge Windows DNS'te var mi ---
        IReadOnlyList<SiteDnsRecordDto> zoneKayitlari = [];
        var windowsDnsHazir = false;
        try
        {
            var zone = await (reads?.Zone.Value ?? dnsService.GetZoneAsync(siteName, cancellationToken));
            zoneKayitlari = zone.Records.ToList();
            windowsDnsHazir = zone.IsWindowsDnsAvailable && zone.IsWindowsZonePresent;
            if (!zone.IsWindowsZonePresent)
            {
                // Yayinlama adimi (2b) bunu zaten denedi; hala yoksa gercek bir sorun var
                // (DNS rolu kurulu degil, yetki yok vb.) ve kullaniciya SOYLENMELI.
                findings.Add(new RepairFindingDto
                {
                    Key = "repair.zone_not_published",
                    Severity = "fail",
                    Detail = zone.ZoneName
                });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Bolge durumu okunamadi ({SiteName}).", siteName);
            findings.Add(InspectionFailure("DNS", exception));
        }

        // Bolge BIR KEZ okunur ve posta bulgularina gecirilir. Eskiden her iki yer de ayri ayri
        // GetZoneAsync cagiriyordu; her cagri 3 PowerShell sureci baslatiyor, yani site basina 3
        // gereksiz surec. 40 siteli bir sunucuda denetim bu yuzden dakikalara ciakabiliyordu.
        // PANELIN DOSYASI ILE GERCEKTEN YAYINLANANI KARSILASTIR.
        //
        // RepairDefaultsAsync yalnizca "sablonda olup panelin dosyasinda olmayan" kayitlari sayar.
        // Kayit dosyaya yazilmis ama Windows DNS'e islenememisse (yazma hatasi, bolge kilidi,
        // script calistirilamamasi) panel "eksik yok" der ve SUSAR. Canlida tam olarak bu oldu:
        // istemci otomatik yapilandirma (SRV) kayitlari panelin dosyasindaydi ama bolgede yoktu,
        // ve panel hicbir uyari vermedi.
        //
        // Karsilastirma DEGERE degil "TUR|konak" ikilisine yapilir: Windows uzun TXT'i parcalara
        // bolerek sakladigi icin deger esitligi beklemek her turda yanlis alarm uretirdi.
        try
        {
            var yayinlanan = await (reads?.Published.Value ?? dnsService.GetPublishedRecordKeysAsync(siteName, cancellationToken));

            // "OLCEMEDIK" ILE "SORUN YOK" AYNI SEY DEGIL.
            //
            // Okuma basarisiz oldugunda sessizce geciyordum -- yani kendi uyardigim hatayi kendim
            // yapiyordum. Windows DNS kurulu ve bolge yerinde oldugu halde kayitlar okunamiyorsa
            // bu BASLI BASINA bir sorundur ve soylenmelidir; aksi halde panel bilmedigi seye
            // "iyi" demis olur.
            if (yayinlanan is null && windowsDnsHazir)
            {
                findings.Add(new RepairFindingDto
                {
                    Key = "repair.publish_state_unknown",
                    Severity = "warn",
                    NeedsConfirmation = true
                });
            }

            if (yayinlanan is not null && zoneKayitlari.Count > 0)
            {
                var eksik = zoneKayitlari
                    .Select(record => $"{record.Type}|{record.Host}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(key => !yayinlanan.Contains(key))
                    .ToList();

                if (eksik.Count > 0)
                {
                    findings.Add(new RepairFindingDto
                    {
                        Key = "repair.records_not_published",
                        Severity = "fail",
                        Detail = string.Join(", ", eksik.Take(6)) + (eksik.Count > 6 ? " …" : string.Empty)
                    });
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Yayinlanmis kayitlar okunamadi ({SiteName}).", siteName);
        }

        await AddMailFindingsAsync(siteName, zoneKayitlari, findings, cancellationToken, reads);
        return findings;
    }

    /// <summary>
    /// Posta tarafinin bulgulari.
    /// <para>
    /// En kritigi "DNS'te MX var ama posta sunucusunda alan yok": bu durumda dunya bu alana posta
    /// gondermeye baslar ve karsiliginda hicbir sey yoktur. Panel bunu eskiden "posta
    /// kullanilmiyor" ile AYNI gosteriyordu (istisna yutuluyordu), yani en bozuk hal en saglikli
    /// hal gibi gorunuyordu.
    /// </para>
    /// </summary>
    private async Task AddMailFindingsAsync(
        string siteName,
        IReadOnlyList<SiteDnsRecordDto> zoneKayitlari,
        List<RepairFindingDto> findings,
        CancellationToken cancellationToken,
        InspectionReads? reads = null)
    {
        // Bolge zaten okundu; tekrar okumak gereksiz PowerShell sureci demek.
        var mailExpected = zoneKayitlari.Any(record => string.Equals(record.Type, "MX", StringComparison.OrdinalIgnoreCase));

        if (mailExpected)
        {
            try
            {
                await (reads?.Mail.Value ?? mailService.GetDnsRecordsAsync(siteName, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                findings.Add(new RepairFindingDto
                {
                    Key = "repair.mail_domain_missing",
                    Severity = "fail",
                    NeedsConfirmation = true,
                    Detail = exception.Message
                });
            }
        }

        // NOT: posta sertifikasi bulgulari BURADA DEGIL, CollectServerFindingsAsync icinde.
        // Sertifika sunucu genelinde TEKTIR; site basina raporlanirsa 40 siteli bir sunucuda ayni
        // uyari 40 kez cikar ve "40/40 alan adi guncel degil" gibi anlamsiz bir tablo olusur.
    }

    private sealed class InspectionReads(string siteName, IDnsService dns, IMailService mail, CancellationToken token,
        DnsInspectionDto? snapshot = null)
    {
        public Lazy<Task<MailDnsDto>> Mail { get; } = new(() => mail.GetDnsRecordsAsync(siteName, token));
        public Lazy<Task<SiteDnsZoneDto>> Zone { get; } = new(() => snapshot is null
            ? dns.GetZoneAsync(siteName, token) : Task.FromResult(snapshot.Zone));
        public Lazy<Task<IReadOnlyCollection<string>?>> Published { get; } = new(() => snapshot is null
            ? dns.GetPublishedRecordKeysAsync(siteName, token) : Task.FromResult(snapshot.PublishedRecordKeys));
    }

    /// <summary>Son posta kaydi uygulamasinda olusan hatalar; cagiran bunu rapora tasir.</summary>
    private IReadOnlyList<string> MailRecordErrors { get; set; } = [];

    private async Task<int> ApplyMailRecordsAsync(string siteName, CancellationToken cancellationToken)
    {
        try
        {
            var dns = await mailService.GetDnsRecordsAsync(siteName, cancellationToken);
            var applicable = dns.Records.Where(record => !string.IsNullOrWhiteSpace(record.Value)).ToList();
            if (applicable.Count == 0)
            {
                return 0;
            }

            // Yazmadan ÖNCEKİ hâli al. Upsert idempotenttir: aynı kaydı ikinci kez yazmak hiçbir şey
            // değiştirmez. Bu yüzden "kaç kayıt gönderildi" DEĞİL, "kaç kayıt gerçekten değişti"
            // raporlanır — yoksa her tazeleme, hiçbir eksiği olmayan alan adlarını da "güncellendi"
            // diye gösterir ve rapor güvenilmez hâle gelir.
            var before = await ReadZoneKeysAsync(dns.Domain, cancellationToken);

            // TEK SENKRON, tum kayitlar birden.
            //
            // CANLI KOK NEDEN: kayitlar tek tek yaziliyordu ve HER yazma bolgenin tamamini
            // yeniden yazan bir senkron baslatiyordu. DKIM gibi uzun TXT'ler bu senkronun
            // ardindan bolge dosyasina yaziliyor ve bolgeyi KILITLIYOR; hemen arkasindan gelen
            // kaydin senkronu kilide carpiyordu. Sonuc, kullanicinin gonderdigi tabloydu:
            // "TXT @" ve "TXT _dmarc" icin ust uste "Failed to create resource record ...".
            // Silme basarisiz oluyor, ardindan ekleme "zaten var" diye reddediliyordu.
            var hatalar = new List<string>();
            try
            {
                await dnsService.EnsureMissingRecordsAsync(
                    dns.Domain,
                    applicable.Select(record => new UpsertDnsRecordRequest
                    {
                        Type = record.Type,
                        Host = record.Host,
                        Value = record.Value,
                        Priority = record.Priority,
                        Ttl = 3600
                    }).ToList(),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Posta kayitlari yazilamadi: {Domain}", dns.Domain);
                hatalar.Add(DescribeError(exception));
            }

            MailRecordErrors = hatalar;

            var after = await ReadZoneKeysAsync(dns.Domain, cancellationToken);
            return after.Count(key => !before.Contains(key));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Posta tarafının yokluğu bir HATA DEĞİLDİR: posta sunucusu kurulu olmayan bir panelde
            // ya da posta kullanmayan bir sitede bu beklenen durumdur. DNS tazelemesi yine de geçerli.
            logger.LogDebug(exception, "Posta kayıtları uygulanmadı ({SiteName}); posta alan adı yok olabilir.", siteName);
            return 0;
        }
    }

    /// <summary>Zone'daki kayıtların "TÜR|konak|değer" kimlikleri — değişiklik saymak için.</summary>
    private async Task<HashSet<string>> ReadZoneKeysAsync(string siteName, CancellationToken cancellationToken)
    {
        var zone = await dnsService.GetZoneAsync(siteName, cancellationToken);
        return zone.Records
            .Select(record => $"{record.Type}|{record.Host}|{record.Value}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
