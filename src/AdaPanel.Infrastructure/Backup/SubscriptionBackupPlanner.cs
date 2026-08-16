using AdaPanel.Core.Dtos.Backup;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// Bir ABONELİĞİN yedeğine tam olarak neyin gireceğine karar veren saf kural kümesi.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden abonelik bazında yedek:</b> Plesk site başına değil ABONELİK başına yedek alır. Bir
/// müşteriyi başka sunucuya taşımak ya da tek hamlede geri getirmek, onun bütün sitelerini,
/// veritabanlarını, posta kutularını ve FTP hesaplarını TEK arşivde toplamayı gerektirir. Site
/// başına yedekle bu iş, kullanıcının parçaları elle eşleştirmesine dönüşür.
/// </para>
/// <para>
/// <b>Neden saf ve ayrı:</b> buradaki tek gerçek risk KİRACI İZOLASYONUDUR — bir aboneliğin
/// arşivine başka bir müşterinin veritabanı ya da posta alanı girerse, o arşivi indiren müşteri
/// başkasının verisini eline geçirir. Bu kararın on bağımlılıklı bir servisin içinde gömülü
/// olması, ölçülemez olması demektir.
/// </para>
/// </remarks>
internal static class SubscriptionBackupPlanner
{
    /// <summary>Bir aboneliğin yedeğine girecek veritabanı.</summary>
    public sealed record PlannedDatabase(string Engine, string DatabaseName, string SiteName);

    /// <summary>Bir aboneliğin yedeğine girecek FTP hesabı (parola YEDEĞE GİREMEZ).</summary>
    public sealed record PlannedFtpUser(string UserName, string SiteName, string HomePath);

    public sealed record Plan(
        IReadOnlyList<string> Sites,
        IReadOnlyList<PlannedDatabase> Databases,
        IReadOnlyList<string> MailDomains,
        IReadOnlyList<PlannedFtpUser> FtpUsers)
    {
        /// <summary>Arşivdeki mantıksal öğe sayısı (ekranda "8 öğeden 6'sı geri yüklendi" için).</summary>
        public int ItemCount => Sites.Count + Databases.Count + MailDomains.Count + FtpUsers.Count;
    }

    /// <summary>
    /// Aboneliğin kapsamından yedek planını üretir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Veritabanı kuralı (kiracı izolasyonu):</b> otorite SAHİPLİK KAYDIDIR, ad benzerliği değil.
    /// </para>
    /// <list type="bullet">
    /// <item>Kayıtta site VARSA: site aboneliğin sitelerinden biri olmalı. Sahibi bu abonelik
    /// görünse bile başka bir aboneliğin sitesine bağlı veritabanı ALINMAZ — site, sahiplikten
    /// daha yeni ve daha kesin bilgidir (veritabanı devredilmiş olabilir).</item>
    /// <item>Kayıtta site YOKSA: sahibi bu abonelik olmalı. Sahipsiz/başkasının veritabanı ALINMAZ.</item>
    /// </list>
    /// <para>
    /// <b>Posta kuralı:</b> yalnızca aboneliğin izinli alan adları
    /// (<c>ICustomerScopeService.GetAllowedMailDomainsAsync</c>) — site adları + bağlama host'ları.
    /// </para>
    /// <para>
    /// <b>FTP kuralı:</b> hesabın bağlı olduğu site aboneliğin sitelerinden biri olmalı. Siteye
    /// bağlı OLMAYAN (sunucu geneli) FTP hesapları abonelik yedeğine girmez.
    /// </para>
    /// </remarks>
    public static Plan BuildPlan(
        Guid userId,
        IReadOnlyCollection<string> scopeSites,
        IReadOnlyCollection<string> allowedMailDomains,
        IReadOnlyCollection<(string Engine, string DatabaseName, Guid OwnerUserId, string SiteName)> ownership,
        IReadOnlyCollection<(string UserName, string SiteName, string HomePath)> ftpUsers)
    {
        var siteler = scopeSites
            .Where(site => !string.IsNullOrWhiteSpace(site))
            .Select(site => site.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(site => site, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var siteKumesi = new HashSet<string>(siteler, StringComparer.OrdinalIgnoreCase);

        var veritabanlari = ownership
            .Where(kayit => !string.IsNullOrWhiteSpace(kayit.Engine) && !string.IsNullOrWhiteSpace(kayit.DatabaseName))
            .Where(kayit => string.IsNullOrWhiteSpace(kayit.SiteName)
                ? kayit.OwnerUserId == userId
                : siteKumesi.Contains(kayit.SiteName.Trim()))
            .Select(kayit => new PlannedDatabase(
                kayit.Engine.Trim(),
                kayit.DatabaseName.Trim(),
                kayit.SiteName?.Trim() ?? string.Empty))
            .DistinctBy(db => $"{db.Engine}|{db.DatabaseName}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(db => db.Engine, StringComparer.OrdinalIgnoreCase)
            .ThenBy(db => db.DatabaseName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var alanlar = allowedMailDomains
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Select(domain => domain.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var ftp = ftpUsers
            .Where(kullanici => !string.IsNullOrWhiteSpace(kullanici.UserName))
            .Where(kullanici => !string.IsNullOrWhiteSpace(kullanici.SiteName) && siteKumesi.Contains(kullanici.SiteName.Trim()))
            .Select(kullanici => new PlannedFtpUser(
                kullanici.UserName.Trim(),
                kullanici.SiteName.Trim(),
                kullanici.HomePath ?? string.Empty))
            .DistinctBy(kullanici => kullanici.UserName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(kullanici => kullanici.UserName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new Plan(siteler, veritabanlari, alanlar, ftp);
    }

    /// <summary>
    /// Kısmi başarısızlığı DÜRÜST anlatan özet: "8 öğeden 6'sı geri yüklendi, 2'si YÜKLENMEDİ".
    /// </summary>
    /// <remarks>
    /// Bu metnin tek kaynak olmasının sebebi, geçmişte panelde yarım kalan işe "restored" denmiş
    /// olmasıdır. Sayı ve uyarı listesi TEK yerden üretilirse, bir akış bunu söylemeyi unutamaz.
    /// </remarks>
    public static string OzetMetni(int toplam, int basarili, IReadOnlyCollection<BackupRestoreWarningDto> uyarilar)
    {
        var basarisiz = Math.Max(0, toplam - basarili);
        if (basarisiz == 0)
        {
            return $"{toplam} ogeden {basarili} tanesi geri yuklendi.";
        }

        var sebepler = uyarilar
            .Take(8)
            .Select(uyari => string.IsNullOrWhiteSpace(uyari.Detail)
                ? $"{uyari.Target}: {uyari.MessageKey}"
                : $"{uyari.Target}: {uyari.MessageKey} ({uyari.Detail})");

        return $"{toplam} ogeden {basarili} tanesi geri yuklendi, {basarisiz} tanesi YUKLENMEDI. " +
               string.Join(" | ", sebepler);
    }
}
