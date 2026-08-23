using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// Reseller (Bayi) yalnız kendi müşterilerinin sitelerinin DNS bölgesini yönetebilir
// (EnsureSiteAsync). Windows DNS özelliği KURULUMU server-level → admin-only.
//
// DENETİM KAYDI: durum değiştiren her uç iz bırakır. Bu denetleyicide hiç yoktu; DNS ise
// panelin en yıkıcı yüzeylerinden biridir — tek bir A ya da MX değişikliği alan adının
// trafiğini veya postasını başka bir sunucuya taşır. "Kim, ne zaman, neyi değiştirdi"
// sorusunun cevapsız kalması, bir olaydan sonra elde hiçbir şey bırakmaz.
[ApiController]
[Authorize(Roles = "Administrator,Reseller")]
[Route("api/v1/sites/{siteName}/dns")]
public sealed class DnsController(
    IDnsService dnsService,
    ICustomerScopeService customerScopes,
    IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<SiteDnsZoneDto>>> GetAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<SiteDnsZoneDto>.Ok(await dnsService.GetZoneAsync(siteName, cancellationToken)));
    }

    [HttpPut("zone")]
    public async Task<ActionResult<ApiResponse<SiteDnsZoneDto>>> UpdateZoneAsync(
        string siteName,
        [FromBody] UpdateDnsZoneRequest request,
        [FromServices] IIisSiteService iisSiteService,
        [FromServices] IMailService mailService,
        [FromServices] ILogger<DnsController> logger,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);

        // GÜVENLİK (çapraz-kiracı): site kapsamı doğrulanıyordu ama BÖLGE ADI serbestti
        // (DnsService: state.ZoneName = request.ZoneName). Kendi sitesinin bölge adını
        // "kurban.com" yapan bir kiracı, kurbanın alan adının DNS kayıtlarını yönetir hâle
        // geliyordu — A kaydını istediği yere çevirmek, sertifika almak dahil.
        await CustomerAccess.EnsureDomainClaimAsync(
            customerScopes, iisSiteService, mailService, User, request.ZoneName, siteName, logger, cancellationToken);

        var zone = await dnsService.UpdateZoneAsync(siteName, request, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "dns.zone_updated",
            $"dns:{siteName}",
            $"DNS bölge ayarları kaydedildi: {zone.ZoneName}. " +
            $"Ad sunucuları={FormatList(zone.NameServers)}, transfer izinli={FormatList(zone.ZoneTransferIps)}.",
            await SiteSahibiAsync(siteName, cancellationToken));

        return Ok(ApiResponse<SiteDnsZoneDto>.Ok(zone, "dns.zone_saved"));
    }

    /// <summary>
    /// Bölgeyi BİRİNCİL ↔ İKİNCİL (slave) kipleri arasında çevirir — Plesk'in bölge başına
    /// "Switch to Secondary / Switch to Primary" düğmesinin karşılığı.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden admin-only:</b> ikincil kip bölgenin YETKİLİ KAYNAĞINI bu sunucudan alıp dışarıdaki
    /// bir IP'ye devreder. O IP'yi belirleyen, alan adının tüm kayıtlarını (A, MX, TXT) belirler.
    /// Bir kiracının bunu kendi başına yapabilmesi, panelin barındırdığı bir alan adını sessizce
    /// dışarıdan yönetilebilir hâle getirmesi demektir. Aynı gerekçeyle DNSSEC ve Windows DNS
    /// kurulumu da yönetici işidir.
    /// </para>
    /// <para>
    /// Yönetici olsa bile site kapsamı DOĞRULANIR: bayi rolü bu uca hiç giremez ama kapsam denetimi
    /// yine de yapılır ki, rol listesi ileride genişletildiğinde koruma kendiliğinden yerinde olsun.
    /// </para>
    /// </remarks>
    [HttpPost("mode")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<SiteDnsZoneDto>>> SetZoneModeAsync(
        string siteName,
        [FromBody] SetDnsZoneModeRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);

        var zone = await dnsService.SetZoneModeAsync(siteName, request, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            zone.IsSecondary ? "dns.zone_switched_to_secondary" : "dns.zone_switched_to_primary",
            $"dns:{siteName}",
            zone.IsSecondary
                ? $"Bölge İKİNCİL (slave) yapıldı: {zone.ZoneName}. Master sunucular={FormatList(zone.MasterServers)}. " +
                  "Kayıtların kaynağı artık bu panel değil."
                : $"Bölge BİRİNCİL yapıldı: {zone.ZoneName}. Panelin kayıtları yeniden yayınlandı.",
            await SiteSahibiAsync(siteName, cancellationToken));

        return Ok(ApiResponse<SiteDnsZoneDto>.Ok(
            zone,
            zone.IsSecondary ? "dns.secondary_enabled" : "dns.primary_restored"));
    }

    /// <summary>İkincil bölge için master'dan tam zone transferini (AXFR) hemen başlatır.</summary>
    [HttpPost("transfer")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<SiteDnsZoneDto>>> TransferNowAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);

        var zone = await dnsService.TransferZoneNowAsync(siteName, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "dns.zone_transfer_started",
            $"dns:{siteName}",
            $"İkincil bölge için tam zone transferi (AXFR) başlatıldı: {zone.ZoneName}. " +
            $"Master sunucular={FormatList(zone.MasterServers)}.",
            await SiteSahibiAsync(siteName, cancellationToken));

        return Ok(ApiResponse<SiteDnsZoneDto>.Ok(zone, "dns.transfer_started"));
    }

    [HttpPost("windows/install")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<SiteDnsZoneDto>>> InstallWindowsDnsAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        var zone = await dnsService.InstallWindowsDnsAsync(siteName, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "dns.windows_installed",
            $"dns:{siteName}",
            $"Windows DNS Server rolü kuruldu ve bölge yayınlandı: {zone.ZoneName}.",
            await SiteSahibiAsync(siteName, cancellationToken));

        return Ok(ApiResponse<SiteDnsZoneDto>.Ok(zone, "dns.windows_installed"));
    }

    [HttpPost("defaults")]
    public async Task<ActionResult<ApiResponse<SiteDnsZoneDto>>> ResetDefaultsAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);

        var zone = await dnsService.ResetDefaultsAsync(siteName, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "dns.defaults_restored",
            $"dns:{siteName}",
            $"Panelin yönettiği DNS kayıtları varsayılana sıfırlandı: {zone.ZoneName}.",
            await SiteSahibiAsync(siteName, cancellationToken));

        return Ok(ApiResponse<SiteDnsZoneDto>.Ok(zone, "dns.defaults_restored"));
    }

    // DNSSEC aç/kapat (Windows DNS zone-imzalama). Server-level imzalama → admin-only.
    [HttpPost("dnssec")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<SiteDnsZoneDto>>> SetDnssecAsync(
        string siteName,
        [FromBody] SetDnssecRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);

        var zone = await dnsService.SetDnssecAsync(siteName, request.Enabled, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            request.Enabled ? "dns.dnssec_enabled" : "dns.dnssec_disabled",
            $"dns:{siteName}",
            $"DNSSEC {(request.Enabled ? "açıldı (bölge imzalandı)" : "kapatıldı (imza kaldırıldı)")}: {zone.ZoneName}.",
            await SiteSahibiAsync(siteName, cancellationToken));

        return Ok(ApiResponse<SiteDnsZoneDto>.Ok(
            zone,
            request.Enabled ? "dns.dnssec_enabled" : "dns.dnssec_disabled"));
    }

    [HttpPost("records")]
    public async Task<ActionResult<ApiResponse<SiteDnsZoneDto>>> UpsertRecordAsync(
        string siteName,
        [FromBody] UpsertDnsRecordRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);

        var zone = await dnsService.UpsertRecordAsync(siteName, request, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "dns.record_saved",
            $"dns:{siteName}",
            $"DNS kaydı yazıldı ({zone.ZoneName}): {request.Type} {request.Host} → {request.Value} (TTL {request.Ttl}).",
            await SiteSahibiAsync(siteName, cancellationToken));

        return Ok(ApiResponse<SiteDnsZoneDto>.Ok(zone, "dns.record_saved"));
    }

    [HttpDelete("records/{recordId}")]
    public async Task<ActionResult<ApiResponse<SiteDnsZoneDto>>> DeleteRecordAsync(
        string siteName,
        string recordId,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);

        // Silinen kaydın NE OLDUĞU silmeden ÖNCE okunur: sonrasında yalnız kimlik kalır ve denetim
        // kaydı "bir kayıt silindi" demekten öteye gidemezdi.
        var oncesi = await dnsService.GetZoneAsync(siteName, cancellationToken);
        var silinen = oncesi.Records.FirstOrDefault(record =>
            string.Equals(record.Id, recordId, StringComparison.OrdinalIgnoreCase));

        var zone = await dnsService.DeleteRecordAsync(siteName, recordId, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "dns.record_deleted",
            $"dns:{siteName}",
            silinen is null
                ? $"DNS kaydı silindi ({zone.ZoneName}): {recordId}."
                : $"DNS kaydı silindi ({zone.ZoneName}): {silinen.Type} {silinen.Host} → {silinen.Value}.",
            await SiteSahibiAsync(siteName, cancellationToken));

        return Ok(ApiResponse<SiteDnsZoneDto>.Ok(zone, "dns.record_deleted"));
    }

    /// <summary>
    /// Bölgenin ait olduğu sitenin ABONELİK SAHİBİ — kaydın kiracıya görünmesini sağlayan alan.
    /// </summary>
    /// <remarks>
    /// Kapsam denetimi (EnsureSiteAsync) ÇAĞIRANIN siteye erişebildiğini doğrular; buradaki soru
    /// başka: kaynak KİME ait. İkisi ayrıldığı an tam da kaydın en çok gerektiği andır — bir
    /// yönetici kiracının bölgesinde A ya da MX kaydını değiştirir. Bu alan olmadan kiracı, kendi
    /// alan adının trafiğini/postasını taşıyan o değişikliği denetim ekranında HİÇ göremezdi.
    /// Site sahipsizse (yöneticinin kendi sitesi) boş kalır; tahmin edilmez.
    /// </remarks>
    private Task<Guid?> SiteSahibiAsync(string siteName, CancellationToken cancellationToken)
        => AuditSubject.SiteOwnerAsync(customerScopes, siteName, cancellationToken);

    /// <summary>Denetim kaydı için IP/ad listesi; boş liste "yok" olarak yazılır ki satır anlamsız kalmasın.</summary>
    private static string FormatList(string[] values)
        => values.Length == 0 ? "(yok)" : string.Join(", ", values);
}
