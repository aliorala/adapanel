using AdaPanel.Core.Dtos.Audit;
using AdaPanel.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Infrastructure.Audit;

/// <summary>
/// Denetim kaydı filtresinin TEK KAYNAĞI: kapsam + ölçütler burada uygulanır.
/// </summary>
/// <remarks>
/// <para>
/// Aynı filtre üç tüketiciye hizmet eder: sayfa listesi, toplam sayı ve CSV akışı. Ayrı ayrı
/// yazılsalardı biri güncellenip diğeri unutulur, "ekranda 12 kayıt / CSV'de 40 kayıt" gibi
/// birbirini tutmayan sonuçlar çıkardı — denetim kaydında bu, aracın güvenilirliğini yok eder.
/// </para>
/// <para>
/// <b>Kapsam ÖNCE uygulanır</b> ve ölçütlerden ayrı bir adımdır: hiçbir ölçüt kombinasyonu
/// kapsamı genişletemez.
/// </para>
/// </remarks>
public static class AuditLogQueryEngine
{
    public const int MaxPageSize = 200;

    /// <summary>Sayfa boyutunu/sayfa numarasını güvenli aralığa çeker.</summary>
    public static (int Page, int PageSize) NormalizePaging(AuditLogQuery query)
    {
        var pageSize = Math.Clamp(query.PageSize <= 0 ? 25 : query.PageSize, 1, MaxPageSize);
        var page = Math.Max(1, query.Page);
        return (page, pageSize);
    }

    /// <summary>
    /// Kapsamı uygular. Sınırsız değilse ve kimlik listesi boşsa HİÇBİR kayıt dönmez (kapalı-varsayılan).
    /// </summary>
    public static IQueryable<AuditLog> ApplyVisibility(IQueryable<AuditLog> source, AuditVisibility visibility)
    {
        if (visibility.Unrestricted)
        {
            return source;
        }

        if (visibility.UserIds.Count == 0)
        {
            // Where(false): sorgu yine de veritabanına gider ama hiçbir satır dönmez. Erken
            // "boş liste" dönmek yerine sorguyu bozmak, çağıranın bu dalı atlamasını imkânsız kılar.
            return source.Where(_ => false);
        }

        var izinli = visibility.UserIds.ToArray();

        // UserId'si olmayan ("system") kayıtlar kapsamlı role GÖSTERİLMEZ: sahibi belirsiz bir
        // kaydın hangi aboneliğe ait olduğunu söyleyemeyiz, dolayısıyla göstermek çapraz-kiracı
        // sızıntı riskidir.
        return source.Where(log => log.UserId != null && izinli.Contains(log.UserId.Value));
    }

    /// <summary>
    /// Kullanıcı ölçütleri. <paramref name="userIdsMatchingName"/> null ise kullanıcı adı filtresi yok;
    /// boş koleksiyon ise "adı eşleşen kullanıcı yok" demektir → sonuç boş olmalıdır.
    /// </summary>
    public static IQueryable<AuditLog> ApplyFilters(
        IQueryable<AuditLog> source,
        AuditLogQuery query,
        IReadOnlyCollection<Guid>? userIdsMatchingName)
    {
        if (query.FromUtc is { } baslangic)
        {
            source = source.Where(log => log.CreatedAtUtc >= baslangic);
        }

        if (query.ToUtc is { } bitis)
        {
            source = source.Where(log => log.CreatedAtUtc <= bitis);
        }

        if (userIdsMatchingName is not null)
        {
            if (userIdsMatchingName.Count == 0)
            {
                return source.Where(_ => false);
            }

            var adEslesenler = userIdsMatchingName.ToArray();
            source = source.Where(log => log.UserId != null && adEslesenler.Contains(log.UserId.Value));
        }

        var eylem = query.Action.Trim();
        if (eylem.Length > 0)
        {
            // Nokta içermeyen değer ÖN EK sayılır: kullanıcı "mail" seçince tüm mail.* eylemleri
            // gelir. Tam eylem adı ("mail.account.created") seçildiğinde ise yalnız o gelir.
            source = eylem.Contains('.')
                ? source.Where(log => log.Action == eylem)
                : source.Where(log => log.Action == eylem || log.Action.StartsWith(eylem + "."));
        }

        var kaynak = query.Resource.Trim();
        if (kaynak.Length > 0)
        {
            var desen = Like(kaynak);
            source = source.Where(log => EF.Functions.Like(log.Resource, desen, LikeEscape));
        }

        var arama = query.Search.Trim();
        if (arama.Length > 0)
        {
            var desen = Like(arama);
            source = source.Where(log =>
                EF.Functions.Like(log.Details, desen, LikeEscape)
                || EF.Functions.Like(log.Action, desen, LikeEscape)
                || EF.Functions.Like(log.Resource, desen, LikeEscape)
                || (log.IpAddress != null && EF.Functions.Like(log.IpAddress, desen, LikeEscape)));
        }

        return source;
    }

    /// <summary>LIKE deseninde joker kaçırma karakteri (SQL ESCAPE yan tümcesine geçer).</summary>
    private const string LikeEscape = "\\";

    /// <summary>
    /// Serbest metni LIKE desenine çevirir. Joker karakterler KAÇIRILIR: aksi hâlde kullanıcının
    /// yazdığı "%" tüm kayıtları getirir ve arama sessizce anlamsızlaşırdı.
    /// <para>
    /// LIKE bilinçli tercih: SQLite'ta <c>string.Contains</c> <c>instr()</c>'a çevrilir ve BÜYÜK/küçük
    /// harfe duyarlıdır — kullanıcı "Mail" yazınca "mail.*" kayıtlarını bulamazdı.
    /// </para>
    /// </summary>
    private static string Like(string term)
    {
        var kacirilmis = term
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

        return $"%{kacirilmis}%";
    }
}
