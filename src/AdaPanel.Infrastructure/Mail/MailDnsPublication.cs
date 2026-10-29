using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>
/// Posta DNS kayıtlarının bölgeye YAZILIP YAZILMADIĞINA dair TEK karar noktası.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN YALAN — kapatıldı.</b> İki ayrı yol vardı ve ikisi de "yayınlandı" diyordu:
/// </para>
/// <list type="number">
/// <item><c>MailService.TryPublishDnsRecordsAsync</c> toplu yazmadan sonra KOŞULSUZ
/// <c>"Mail DNS records published for {Domain}: {Count} record(s)."</c> yazıyordu. Oysa
/// <c>DnsService.UpsertRecordsAsync</c> bölge İKİNCİL ise kayıtları yalnız panel durumuna
/// yazar, Windows DNS'e HİÇ dokunmaz ve başarıyla döner (bilinçli karar: posta kutusu açmayı
/// DNS'e bağlamamak için). Windows DNS rolü hiç kurulu değilse de aynı şey olur.</item>
/// <item><c>MailController.ApplyDnsRecordsAsync</c> <c>AppliedCount</c>'u YAZILAN kayıttan
/// değil, GÖNDERİLMESİ DÜŞÜNÜLEN kayıt sayısından üretiyordu; kullanıcı ekranda
/// "5 kayıt uygulandı" görüyordu.</item>
/// </list>
/// <para>
/// <b>Neden bu sınıfın en pahalı olduğu hata sınıfı budur:</b> DKIM anahtarını Stalwart alan adı
/// açılırken üretir ve giden HER postayı onunla imzalar. Karşılık gelen TXT bölgeye yazılmazsa
/// posta imzalı ama DOĞRULANAMAZ gider — yani DKIM BAŞARISIZ sayılır ve spam'e düşer. Panel
/// "yayınlandı" dediği için kimse sebebi aramaz. Aynısı SPF/DMARC için de geçerlidir.
/// </para>
/// <para>
/// <b>Neden ayrı bir sınıf:</b> aynı karar üç yerde (otomatik yayın, "Uygula" düğmesi, alan adı
/// oluşturma) veriliyordu. Kararı bir yere toplamak, birinin düzeltilip ötekinin sessizce yalan
/// söylemeye devam etmesini engeller.
/// </para>
/// </remarks>
public static class MailDnsPublication
{
    public static void EnsureDomainZone(SiteDnsZoneDto zone, string domain)
    {
        if (!string.Equals(zone.ZoneName.Trim().TrimEnd('.'), domain.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
            throw new AdaPanel.Core.Common.AppException("mail.dns_zone_missing", 409, 409,
                "The managed site's DNS zone does not match the requested mail domain.");
    }

    // Delete only the record we can identify, never all MX/A records sharing its owner name.
    public static bool MatchesRecord(MailDnsRecordDto expected, SiteDnsRecordDto actual)
    {
        if (!string.Equals(expected.Type, actual.Type, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.Host.TrimEnd('.'), actual.Host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(expected.Value)) return false;
        return expected.Type.ToUpperInvariant() switch
        {
            "MX" => expected.Priority == actual.Priority && string.Equals(expected.Value.Trim().TrimEnd('.'),
                actual.Value.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase),
            "A" or "AAAA" => System.Net.IPAddress.TryParse(expected.Value.Trim(), out var left)
                && System.Net.IPAddress.TryParse(actual.Value.Trim(), out var right) && left.Equals(right),
            "TXT" => string.Equals(expected.Value.Trim(), actual.Value.Trim(), StringComparison.Ordinal),
            _ => false
        };
    }

    /// <summary>Yayın sonucu: gerçekten yazıldı mı, kaç kayıt, hangi sebeple yazılmadı.</summary>
    /// <param name="Published">Kayıtlar Windows DNS bölgesine GERÇEKTEN yazıldı mı.</param>
    /// <param name="AppliedCount">Bölgeye yazılan kayıt sayısı. Yayınlanmadıysa 0.</param>
    /// <param name="StatusKey">Çeviri anahtarı; yayınlandıysa <c>mail.dns_applied</c>.</param>
    public readonly record struct Result(bool Published, int AppliedCount, string StatusKey);

    /// <summary>Bölge ikincil: kayıtlar panelde durur, master'a yazılamaz.</summary>
    public const string SecondaryZoneKey = "mail.dns_not_published_secondary";

    /// <summary>Windows DNS rolü kurulu değil: panel bölgeyi yayınlayamaz.</summary>
    public const string NoDnsServerKey = "mail.dns_not_published_no_server";

    /// <summary>Bölge Windows DNS'te yok: alan adının DNS'i başka bir sağlayıcıda.</summary>
    public const string ZoneMissingKey = "mail.dns_not_published_zone_missing";

    /// <summary>Kayıtlar bölgeye yazıldı.</summary>
    public const string AppliedKey = "mail.dns_applied";

    /// <summary>Yazılacak kayıt yoktu (posta alan adı henüz kayıt üretmiyor).</summary>
    public const string NothingToApplyKey = "mail.dns_nothing_to_apply";

    /// <summary>
    /// Posta DNS kayıtlarını bölgeye yazılacak isteklere çevirir. DEĞERİ BOŞ OLAN kayıt atlanır:
    /// <c>ServerAddress</c> ayarlı değilken A kaydının değeri boş kalır ve boş bir A kaydı yazmak
    /// bölgeyi bozardı.
    /// </summary>
    public static List<UpsertDnsRecordRequest> BuildRequests(MailDnsDto dns) =>
        dns.Records
            .Where(record => !string.IsNullOrWhiteSpace(record.Value))
            .Select(record => new UpsertDnsRecordRequest
            {
                Type = record.Type,
                Host = record.Host,
                Value = record.Value,
                Priority = record.Priority,
                // 3600 = 1 saat. Posta kayıtları sık değişmez ama DKIM rotasyonunda bir günlük
                // TTL beklemek imzasız posta demektir; bir saat iki ihtiyacın ortasıdır.
                Ttl = 3600
            })
            .ToList();

    /// <summary>
    /// Bölgenin ÖLÇÜLEN durumundan yayın sonucunu çıkarır.
    /// </summary>
    /// <param name="zone">
    /// Yazma çağrısının döndürdüğü bölge DTO'su. <c>null</c> ise hiç yazma yapılmamıştır.
    /// </param>
    /// <param name="requestedCount">Yazılması istenen kayıt sayısı.</param>
    /// <remarks>
    /// Sıra önemlidir: bölge ikincilse Windows DNS kurulu ve bölge mevcut olsa BİLE kayıt
    /// yazılmaz — en dıştaki engel önce sorulur.
    /// </remarks>
    public static Result Evaluate(SiteDnsZoneDto? zone, int requestedCount)
    {
        if (requestedCount <= 0)
        {
            return new Result(false, 0, NothingToApplyKey);
        }

        if (zone is null)
        {
            return new Result(false, 0, ZoneMissingKey);
        }

        if (zone.IsSecondary)
        {
            return new Result(false, 0, SecondaryZoneKey);
        }

        if (!zone.IsWindowsDnsAvailable)
        {
            return new Result(false, 0, NoDnsServerKey);
        }

        if (!zone.IsWindowsZonePresent)
        {
            return new Result(false, 0, ZoneMissingKey);
        }

        return new Result(true, requestedCount, AppliedKey);
    }
}
