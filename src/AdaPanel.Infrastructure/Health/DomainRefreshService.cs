using AdaPanel.Core.Dtos.Health;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Health;

/// <inheritdoc />
internal sealed class DomainRefreshService(
    IIisSiteService siteService,
    IDnsService dnsService,
    IMailService mailService,
    JsonFileStore fileStore,
    ILogger<DomainRefreshService> logger) : IDomainRefreshService
{
    public Task<DomainRefreshResultDto> RefreshAsync(string siteName, CancellationToken cancellationToken = default)
        => RunAsync(siteName, applyChanges: true, cancellationToken);

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
            if (applyChanges)
            {
                result.AppliedMailRecords = await ApplyMailRecordsAsync(siteName, cancellationToken);
            }
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
            FailedSites = results.Count(item => item.Error.Length > 0)
        };
    }

    /// <summary>
    /// Posta sunucusunun ürettiği kayıtları (MX/SPF/DKIM/DMARC) zone'a yazar.
    /// Posta kurulu değilse ya da bu alan adının posta alan adı yoksa 0 döner.
    /// </summary>
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
