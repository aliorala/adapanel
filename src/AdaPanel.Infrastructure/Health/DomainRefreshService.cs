using AdaPanel.Core.Dtos.Health;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;
using AdaPanel.Core.Dtos.Firewall;

namespace AdaPanel.Infrastructure.Health;

/// <inheritdoc />
internal sealed class DomainRefreshService(
    IIisSiteService siteService,
    IDnsService dnsService,
    IMailService mailService,
    IFirewallService firewallService,
    JsonFileStore fileStore,
    ILogger<DomainRefreshService> logger) : IDomainRefreshService
{
    public Task<DomainRefreshResultDto> RefreshAsync(string siteName, CancellationToken cancellationToken = default)
        => RunAsync(siteName, applyChanges: true, cancellationToken);

    public Task<DomainRefreshResultDto> InspectAsync(string siteName, CancellationToken cancellationToken = default)
        => RunAsync(siteName, applyChanges: false, cancellationToken);

    private async Task<DomainRefreshResultDto> RunAsync(string siteName, bool applyChanges, CancellationToken cancellationToken)
    {
        var result = new DomainRefreshResultDto { SiteName = siteName, ZoneName = siteName };

        try
        {
            // 1) DNS varsayılanları: eksik olanlar EKLENİR, var olanlara dokunulmaz. Yıkıcı olmama
            //    şartı burada kritik — kullanıcı MX ya da SPF değerini elle düzeltmiş olabilir.
            result.AddedDnsRecords = await dnsService.RepairDefaultsAsync(siteName, applyChanges, cancellationToken);

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
                result.AppliedMailRecords = await CountMissingMailRecordsAsync(siteName, cancellationToken);
            }

            if (applyChanges)
            {
                result.AppliedMailRecords = await ApplyMailRecordsAsync(siteName, cancellationToken);

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
                result.MailCertificateRefreshed = await TrySyncMailCertificateAsync(cancellationToken);
            }

            // 4) Ucuz saglik kontrolleri. Hicbiri kendiliginden YIKICI is yapmaz; duzeltilebilenler
            //    duzeltilir, geri kalanlar onay/bilgi olarak raporlanir.
            result.Findings = await CollectFindingsAsync(siteName, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Tek alan adının hatası TOPLU işlemi durdurmamalı: 40 sitelik bir sunucuda ilk hatada
            // durmak, kalan 39 sitenin eksik kalmasına ve bunun görünmemesine yol açardı.
            logger.LogWarning(exception, "Alan adı tazelenemedi: {SiteName}", siteName);
            result.Error = exception.Message;
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

    private static DomainRefreshSummaryDto? _cachedInspection;
    private static DateTime _cachedInspectionAtUtc = DateTime.MinValue;
    private static string _cachedInspectionFingerprint = string.Empty;
    private static readonly SemaphoreSlim InspectionGate = new(1, 1);

    /// <summary>
    /// Zone durumunun "parmak izi": DNS kayıt dosyasının son yazılma anı + site sayısı.
    /// <para>
    /// Süreye ek olarak bu gerekli: yalnız zamana bakan bir önbellek, kullanıcı bir kayıt sildikten
    /// sonra 10 dakika boyunca "eksik yok" demeye devam ederdi. Yani uyarı, tam da uyarması gereken
    /// anda susardı. Canlı testte birebir bu görüldü: MX kaydı silindi, denetim yine 0 eksik dedi,
    /// ama tazeleme çalıştırılınca 1 alan adının güncellendiği ortaya çıktı.
    /// </para>
    /// </summary>
    private string ComputeFingerprint(int siteCount)
    {
        try
        {
            var path = fileStore.ResolvePath("config/dns-zones.json");
            var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0L;
            return $"{stamp}:{siteCount}";
        }
        catch (Exception)
        {
            // Parmak izi okunamıyorsa önbelleği GEÇERSİZ say: bayat veri göstermektense yeniden ölç.
            return Guid.NewGuid().ToString("N");
        }
    }

    public async Task<DomainRefreshSummaryDto> RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        var summary = await RunAllAsync(applyChanges: true, cancellationToken);

        // Tazeleme durumu DEĞİŞTİRDİ: bayat denetim sonucunu tutmak, rozetin düzeltilmiş bir sunucuda
        // 10 dakika boyunca "eksik var" demesine yol açardı.
        _cachedInspection = null;
        _cachedInspectionAtUtc = DateTime.MinValue;
        _cachedInspectionFingerprint = string.Empty;
        return summary;
    }

    private static bool IsCacheUsable(string fingerprint) =>
        _cachedInspection is not null &&
        string.Equals(_cachedInspectionFingerprint, fingerprint, StringComparison.Ordinal) &&
        DateTime.UtcNow - _cachedInspectionAtUtc < InspectionFreshness;

    public async Task<DomainRefreshSummaryDto> InspectAllAsync(CancellationToken cancellationToken = default)
    {
        // Önbellek HEM süreye HEM zone durumuna bağlı: kullanıcı bir kayıt sildiği anda parmak izi
        // değişir ve uyarı hemen görünür. Yalnız süreye bakmak, uyarıyı tam gerektiği anda susturuyordu.
        var siteCount = (await siteService.GetSitesAsync(cancellationToken)).Count;
        var fingerprint = ComputeFingerprint(siteCount);
        if (IsCacheUsable(fingerprint))
        {
            return _cachedInspection!;
        }

        // Tek geçit: birden çok panel sekmesi aynı anda rozetini yenilediğinde aynı ağır taramanın
        // paralel kopyalarını çalıştırmayalım.
        await InspectionGate.WaitAsync(cancellationToken);
        try
        {
            if (IsCacheUsable(fingerprint))
            {
                return _cachedInspection!;
            }

            var summary = await RunAllAsync(applyChanges: false, cancellationToken);
            _cachedInspection = summary;
            _cachedInspectionAtUtc = DateTime.UtcNow;
            _cachedInspectionFingerprint = fingerprint;
            return summary;
        }
        finally
        {
            InspectionGate.Release();
        }
    }

    private async Task<DomainRefreshSummaryDto> RunAllAsync(bool applyChanges, CancellationToken cancellationToken)
    {
        var sites = await siteService.GetSitesAsync(cancellationToken);
        var results = new List<DomainRefreshResultDto>(sites.Count);

        foreach (var site in sites)
        {
            results.Add(await RunAsync(site.Name, applyChanges, cancellationToken));
        }

        return new DomainRefreshSummaryDto
        {
            Results = results,
            TotalSites = results.Count,
            UpdatedSites = results.Count(item => item.Error.Length == 0 && !item.AlreadyCurrent),
            FailedSites = results.Count(item => item.Error.Length > 0),
            ServerFindings = await CollectServerFindingsAsync(applyChanges, cancellationToken)
        };
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
    private async Task<int> CountMissingMailRecordsAsync(string siteName, CancellationToken cancellationToken)
    {
        try
        {
            var dns = await mailService.GetDnsRecordsAsync(siteName, cancellationToken);
            var applicable = dns.Records.Where(record => !string.IsNullOrWhiteSpace(record.Value)).ToList();
            if (applicable.Count == 0)
            {
                return 0;
            }

            var mevcut = await ReadZoneKeysAsync(dns.Domain, cancellationToken);
            return applicable.Count(record => !mevcut.Contains($"{record.Type}|{record.Host}|{record.Value}"));
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

        try
        {
            // panelPort=0: katalogdaki panel girdisi zaten disarida birakiliyor.
            var ports = await firewallService.GetPortStatusAsync(0, cancellationToken);
            foreach (var port in ports.Where(item => item.Required && !item.FirewallAllowed && !string.Equals(item.Category, "panel", StringComparison.OrdinalIgnoreCase)))
            {
                var finding = new RepairFindingDto
                {
                    Key = "repair.firewall_blocked",
                    Severity = "fail",
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
        }

        return findings;
    }

    private async Task<IReadOnlyList<RepairFindingDto>> CollectFindingsAsync(string siteName, CancellationToken cancellationToken)
    {
        var findings = new List<RepairFindingDto>();

        // --- Site durumu ---
        // IIS API'si uzerinden okunur (PowerShell degil), bu yuzden ucuzdur.
        try
        {
            var site = await siteService.GetSiteAsync(siteName, cancellationToken);
            if (!string.Equals(site.State, "Started", StringComparison.OrdinalIgnoreCase))
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
        }

        // --- Bolge Windows DNS'te var mi ---
        try
        {
            var zone = await dnsService.GetZoneAsync(siteName, cancellationToken);
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
        }

        await AddMailFindingsAsync(siteName, findings, cancellationToken);
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
    private async Task AddMailFindingsAsync(string siteName, List<RepairFindingDto> findings, CancellationToken cancellationToken)
    {
        var mailExpected = false;
        try
        {
            var zone = await dnsService.GetZoneAsync(siteName, cancellationToken);
            mailExpected = zone.Records.Any(record => string.Equals(record.Type, "MX", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "MX kaydi okunamadi ({SiteName}).", siteName);
        }

        if (mailExpected)
        {
            try
            {
                await mailService.GetDnsRecordsAsync(siteName, cancellationToken);
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

        // --- Posta sertifikasi ---
        // Dosyadan okunur, ucuzdur. Yenileme BILEREK otomatik yapilmaz: sertifika alma basarisiz
        // olursa mevcut gecerli sertifikayi silip self-signed'a dusuruyor ve otomatik yenilemeyi
        // kalici olarak kapatiyor. Onarmanin her sitede bunu tetiklemesi felaket olurdu.
        try
        {
            var certificate = await mailService.GetCertificateStatusAsync(cancellationToken);
            if (certificate.Active && string.Equals(certificate.Source, "self-signed", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new RepairFindingDto
                {
                    Key = "repair.mail_certificate_selfsigned",
                    Severity = "warn",
                    NeedsConfirmation = true,
                    Detail = certificate.HostName
                });
            }
            else if (certificate.Active && certificate.NotAfterUtc is { } expiry && expiry - DateTime.UtcNow < TimeSpan.FromDays(15))
            {
                findings.Add(new RepairFindingDto
                {
                    Key = expiry <= DateTime.UtcNow ? "repair.mail_certificate_expired" : "repair.mail_certificate_expiring",
                    Severity = expiry <= DateTime.UtcNow ? "fail" : "warn",
                    NeedsConfirmation = true,
                    Detail = expiry.ToString("yyyy-MM-dd")
                });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Posta sertifikasi durumu okunamadi.");
        }
    }

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

            foreach (var record in applicable)
            {
                await dnsService.UpsertRecordAsync(
                    dns.Domain,
                    new UpsertDnsRecordRequest
                    {
                        Type = record.Type,
                        Host = record.Host,
                        Value = record.Value,
                        Priority = record.Priority,
                        Ttl = 3600
                    },
                    cancellationToken);
            }

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
