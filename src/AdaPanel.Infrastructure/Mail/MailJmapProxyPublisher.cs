using System.Xml.Linq;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>
/// Stalwart'ın JMAP ucunu, onu DUYURDUĞU ana adı servis eden IIS sitesi üzerinden yayımlar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ana adı SUNUCUDAN okunuyor:</b> vandelay, JMAP oturumunun duyurduğu adrese gitmek
/// zorunda; kural o adresi karşılayan siteye yazılmazsa vekil yazılmış ama ÖLÜ olur. Bu yüzden
/// hedef ana ad tahmin edilmez, <see cref="IMailAccountArchiver.CheckAsync"/>'in yokladığı adresle
/// AYNI kaynaktan (sunucunun duyurduğu <c>apiUrl</c>) alınır.
/// </para>
/// <para>
/// <b>Neden ayrı bir uç/eylem yok:</b> kurallar mevcut "otomatik yapılandırmayı yayınla" akışının
/// içinde, AYNI kilit ve AYNI ARR ön koşullarıyla yazılır (bkz. <see cref="MailAutodiscoverPublisher"/>).
/// İkinci bir yol açmak, aynı <c>web.config</c> bölgesine iki ayrı kuyruktan yazmak demekti.
/// </para>
/// </remarks>
public sealed class MailJmapProxyPublisher(
    IIisSiteService iisSiteService,
    IReverseProxyService reverseProxyService,
    IConcurrencyLock concurrencyLock,
    ILogger<MailJmapProxyPublisher>? logger = null)
{
    /// <summary>
    /// <paramref name="jmapHostName"/> adını servis eden sitede JMAP vekilini kurar.
    /// </summary>
    /// <returns>Kural yazıldıysa sitenin adı; yazılacak site yoksa <c>null</c>.</returns>
    public async Task<string?> PublishAsync(string jmapHostName, CancellationToken cancellationToken = default)
    {
        var host = (jmapHostName ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0)
        {
            return null;
        }

        var site = await FindSiteAsync(host, cancellationToken);
        if (site is null)
        {
            // YAZACAK YER YOK. Rastgele bir siteye yazmak, başka bir müşterinin web.config'ini
            // değiştirmek olurdu; sessizce "oldu" demek de ön kontrolü yalancı çıkarırdı.
            logger?.LogWarning(
                "JMAP vekili yayınlanamadı: '{Host}' adına bağlı bir IIS sitesi yok. Site yedekleri posta İLETİLERİNİ içermeyecek.",
                host);
            return null;
        }

        // ARR/URL Rewrite ön koşulları. AYRI ÇAĞRI ve KİLİTTEN ÖNCE: kendi içinde IIS kuyruğunu
        // alır, kilit ise yeniden girişe kapalıdır.
        await reverseProxyService.EnsureArrPrerequisitesAsync(site.Name, cancellationToken);

        await concurrencyLock.RunExclusiveAsync(ConcurrencyKeys.Iis, () =>
        {
            var yol = WebConfigPath(site);
            var belge = LoadWebConfig(yol);
            MailJmapProxyRules.Apply(belge);
            belge.Save(yol);
            return Task.CompletedTask;
        }, cancellationToken);

        return site.Name;
    }

    /// <summary>Panelin JMAP kurallarını kaldırır (yayından alma).</summary>
    public async Task UnpublishAsync(string jmapHostName, CancellationToken cancellationToken = default)
    {
        var host = (jmapHostName ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        var site = host.Length == 0 ? null : await FindSiteAsync(host, cancellationToken);
        if (site is null)
        {
            return;
        }

        await concurrencyLock.RunExclusiveAsync(ConcurrencyKeys.Iis, () =>
        {
            var yol = WebConfigPath(site);
            if (!File.Exists(yol))
            {
                return Task.CompletedTask;
            }

            var belge = LoadWebConfig(yol);
            if (MailJmapProxyRules.Remove(belge) > 0)
            {
                belge.Save(yol);
            }

            return Task.CompletedTask;
        }, cancellationToken);
    }

    /// <summary>Kurallar yerinde ve SAĞLIKLI mı (red kuralı hâlâ en başta mı)?</summary>
    public async Task<bool> IsPublishedAsync(string jmapHostName, CancellationToken cancellationToken = default)
    {
        var host = (jmapHostName ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        var site = host.Length == 0 ? null : await FindSiteAsync(host, cancellationToken);
        if (site is null)
        {
            return false;
        }

        var yol = WebConfigPath(site);
        return File.Exists(yol) && MailJmapProxyRules.IsHealthy(LoadWebConfig(yol));
    }

    private async Task<SiteDto?> FindSiteAsync(string host, CancellationToken cancellationToken)
    {
        var siteler = await iisSiteService.GetSitesAsync(cancellationToken);
        return siteler.FirstOrDefault(site => site.Bindings.Any(binding =>
            string.Equals(
                (binding.HostName ?? string.Empty).Trim().TrimEnd('.'),
                host,
                StringComparison.OrdinalIgnoreCase)));
    }

    private static string WebConfigPath(SiteDto site)
    {
        // PhysicalPath (ApplicationRootPath DEĞİL): IIS kuralları sitenin KÖKÜNDEN okur.
        var kok = Environment.ExpandEnvironmentVariables((site.PhysicalPath ?? string.Empty).Trim());
        if (kok.Length == 0)
        {
            throw new AppException("sites.invalid_physical_path", 400, 400);
        }

        Directory.CreateDirectory(kok);
        return Path.Combine(Path.GetFullPath(kok), "web.config");
    }

    private static XDocument LoadWebConfig(string path)
        => File.Exists(path)
            ? XDocument.Load(path, LoadOptions.PreserveWhitespace)
            : new XDocument(new XElement("configuration"));
}
