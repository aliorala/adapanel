using AdaPanel.Api.Extensions;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Security;
using AdaPanel.Core.Dtos.Waf;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// DENETİM KAYDI: bu denetleyicinin uçlarının hiçbiri iz bırakmıyordu.
//
// En ağırı kaba-kuvvet YASAĞININ KALDIRILMASI: bir IP, başarısız giriş denemeleri yüzünden
// güvenlik duvarında engellenmişken buradan serbest bırakılabiliyor ve bunun kaydı hiçbir yerde
// tutulmuyordu. Saldırı sonrası "engel neden düştü, kim kaldırdı" sorusu cevapsız kalıyordu —
// üstelik ele geçirilmiş bir yönetici oturumunun kendi kaynak IP'sini serbest bırakması,
// arkasında iz bırakmayan tek adımlık bir işlemdi. Site güvenlik uçları da (izolasyon, ACL, hız
// sınırı, temel kurallar) sitenin güvenlik duruşunu değiştirdiği hâlde izsizdi.
[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/security")]
public sealed class SecurityController(
    IPanelSecurityService panelSecurityService,
    IWafService wafService,
    IBruteForceProtectionService bruteForceProtection,
    IAuditService auditService) : ControllerBase
{
    [HttpGet("bruteforce")]
    public async Task<ActionResult<ApiResponse<BruteForceStatusDto>>> GetBruteForceAsync(CancellationToken cancellationToken)
    {
        var status = await bruteForceProtection.GetStatusAsync(cancellationToken);
        return Ok(ApiResponse<BruteForceStatusDto>.Ok(status));
    }

    [HttpPut("bruteforce")]
    public async Task<ActionResult<ApiResponse<BruteForceStatusDto>>> UpdateBruteForceAsync(
        [FromBody] UpdateBruteForceSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var status = await bruteForceProtection.UpdateSettingsAsync(request, cancellationToken);

        // Eşiği yükseltmek ya da korumayı tamamen kapatmak, saldırıyı kolaylaştıran sessiz bir
        // ayardır: ekranda tek bir anahtardır ama etkisi tüm panel girişlerini kapsar.
        await auditService.AuditAsync(
            HttpContext,
            "security.bruteforce_settings_changed",
            "security:bruteforce",
            $"Kaba-kuvvet korumasi ayarlandi. Etkin={status.Settings.Enabled}, " +
            $"esik={status.Settings.MaxFailedAttempts}, pencere={status.Settings.WindowMinutes} dk, " +
            $"yasak={status.Settings.BanMinutes} dk, beyaz-liste={status.Settings.WhitelistedIps.Count} kayit.");

        return Ok(ApiResponse<BruteForceStatusDto>.Ok(status, "security.bruteforce.saved"));
    }

    [HttpPost("bruteforce/unban")]
    public async Task<ActionResult<ApiResponse<BruteForceStatusDto>>> UnbanAsync(
        [FromBody] UnbanIpRequest request,
        CancellationToken cancellationToken)
    {
        // Kaldırılan yasağın AYRINTISI, kaldırmadan ÖNCE okunur: işlemden sonra kayıt listeden
        // düşeceği için "kaç başarısız denemeden sonra engellenmişti" bilgisi bir daha elde
        // edilemez. Denetim kaydında asıl değerli olan da budur.
        var oncekiDurum = await bruteForceProtection.GetStatusAsync(cancellationToken);
        var yasak = oncekiDurum.BannedIps.FirstOrDefault(item =>
            string.Equals(item.IpAddress, request.IpAddress, StringComparison.OrdinalIgnoreCase));

        var status = await bruteForceProtection.UnbanAsync(request.IpAddress, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "security.bruteforce_unbanned",
            $"security:bruteforce:{request.IpAddress}",
            yasak is null
                ? $"Kaba-kuvvet yasagi kaldirma istendi: {request.IpAddress} (kayitli bir yasak bulunamadi)."
                : $"Kaba-kuvvet yasagi KALDIRILDI: {yasak.IpAddress}. " +
                  $"Yasaklanma={yasak.BannedAtUtc:yyyy-MM-dd HH:mm} UTC, " +
                  $"basarisiz deneme={yasak.FailedAttempts}, " +
                  $"kendiliginden bitis={(yasak.ExpiresAtUtc is null ? "yok (kalici)" : yasak.ExpiresAtUtc.Value.ToString("yyyy-MM-dd HH:mm") + " UTC")}, " +
                  $"kural={yasak.FirewallRuleName}.");

        return Ok(ApiResponse<BruteForceStatusDto>.Ok(status, "security.bruteforce.unbanned"));
    }

    [HttpGet("settings")]
    public async Task<ActionResult<ApiResponse<SecuritySettingsDto>>> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await panelSecurityService.GetSettingsAsync(cancellationToken);
        return Ok(ApiResponse<SecuritySettingsDto>.Ok(settings));
    }

    [HttpPut("settings")]
    public async Task<ActionResult<ApiResponse<SecuritySettingsDto>>> UpdateSettingsAsync(
        [FromBody] UpdateSecuritySettingsRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await panelSecurityService.UpdateSettingsAsync(
            request,
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<SecuritySettingsDto>.Ok(settings, "security.settings.updated"));
    }

    [HttpGet("/api/v1/sites/{siteName}/security")]
    public async Task<ActionResult<ApiResponse<SiteSecurityConfigurationDto>>> GetSiteSecurityAsync(string siteName, CancellationToken cancellationToken)
    {
        var configuration = await wafService.GetSiteConfigurationAsync(siteName, cancellationToken);
        return Ok(ApiResponse<SiteSecurityConfigurationDto>.Ok(configuration));
    }

    [HttpPost("/api/v1/sites/{siteName}/isolate")]
    public async Task<ActionResult<ApiResponse<SiteSecurityConfigurationDto>>> IsolateAsync(string siteName, CancellationToken cancellationToken)
    {
        var configuration = await wafService.IsolateAppPoolAsync(siteName, cancellationToken);
        await AuditSiteSecurityAsync("security.site_isolated", siteName, "Uygulama havuzu izolasyonu uygulandi", configuration);
        return Ok(ApiResponse<SiteSecurityConfigurationDto>.Ok(configuration, "security.site_saved"));
    }

    [HttpPost("/api/v1/sites/{siteName}/acl")]
    public async Task<ActionResult<ApiResponse<SiteSecurityConfigurationDto>>> ApplyAclAsync(string siteName, [FromBody] ApplyFolderAclRequest request, CancellationToken cancellationToken)
    {
        var configuration = await wafService.ApplyFolderAclAsync(siteName, request, cancellationToken);
        await AuditSiteSecurityAsync(
            "security.site_folder_acl_applied",
            siteName,
            $"Klasor izni uygulandi (istenen kimlik='{request.Principal}', haklar='{request.Rights}')",
            configuration);
        return Ok(ApiResponse<SiteSecurityConfigurationDto>.Ok(configuration, "security.site_saved"));
    }

    [HttpPost("/api/v1/sites/{siteName}/ratelimit")]
    public async Task<ActionResult<ApiResponse<SiteSecurityConfigurationDto>>> SetRateLimitAsync(string siteName, [FromBody] SetSiteRateLimitRequest request, CancellationToken cancellationToken)
    {
        var configuration = await wafService.SetRateLimitAsync(siteName, request, cancellationToken);
        await AuditSiteSecurityAsync(
            "security.site_rate_limit_changed",
            siteName,
            $"Istek hizi siniri ayarlandi (istenen={request.RequestsPerMinute}/dk)",
            configuration);
        return Ok(ApiResponse<SiteSecurityConfigurationDto>.Ok(configuration, "security.site_saved"));
    }

    [HttpPost("/api/v1/sites/{siteName}/basic-rules")]
    public async Task<ActionResult<ApiResponse<SiteSecurityConfigurationDto>>> EnableBasicRulesAsync(string siteName, CancellationToken cancellationToken)
    {
        var configuration = await wafService.EnableBasicRulesAsync(siteName, cancellationToken);
        await AuditSiteSecurityAsync("security.site_basic_rules_enabled", siteName, "Temel guvenlik kurallari uygulandi", configuration);
        return Ok(ApiResponse<SiteSecurityConfigurationDto>.Ok(configuration, "security.site_saved"));
    }

    /// <summary>
    /// Site güvenlik uçlarının ortak denetim kaydı.
    /// </summary>
    /// <remarks>
    /// Kayda yazılan durum SONUÇTUR ve ÖLÇÜLMÜŞ olup olmadığı da yazılır. "Uygulandı" demek ile
    /// "uygulandığı ölçüldü" demek aynı şey değildir; ölçülemeyen bir sonucu kayıtta kesinmiş gibi
    /// göstermek, olay sonrası incelemeyi yanlış yöne sürükler.
    /// </remarks>
    private Task AuditSiteSecurityAsync(
        string action,
        string siteName,
        string ozet,
        SiteSecurityConfigurationDto configuration)
        => auditService.AuditAsync(
            HttpContext,
            action,
            $"site-security:{siteName}",
            $"{ozet}. Olculen durum: havuz='{configuration.ApplicationPoolName}', " +
            $"izole={Olculmus(configuration.IsIsolated, configuration.IsIsolatedMeasured)}, " +
            $"klasor-izni={Olculmus(configuration.FolderAclApplied, configuration.FolderAclMeasured)}, " +
            $"temel-kurallar={Olculmus(configuration.BasicRulesEnabled, configuration.BasicRulesMeasured)}, " +
            $"hiz-siniri={Olculmus(configuration.RequestsPerMinute, configuration.RateLimitMeasured)}, " +
            $"kimlik='{configuration.Principal}'.");

    private static string Olculmus<T>(T value, bool measured) => measured ? $"{value}" : $"{value} (OLCULEMEDI)";
}
