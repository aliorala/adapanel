using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Database;
using AdaPanel.Infrastructure.Iis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// MÜŞTERİ KENDİ SİTESİNİN SSL'İNİ YÖNETİR (Plesk/ISPConfig paritesi).
//
// Uç, kodu tamamen yazılmış olmasına rağmen "Administrator,Reseller" ile müşteriye KAPALIYDI:
// abonelik sahibi kendi alan adına ücretsiz Let's Encrypt sertifikası alamıyor, her HTTPS isteği
// için yöneticiye başvurmak zorunda kalıyordu. Oysa barındırma panellerinde bu, abonelik
// sahibinin en temel self-servis işidir.
//
// Rol eklemek TEK BAŞINA yetmez; her uç kaynağın çağırana ait olduğunu doğrular:
//   • Site kapsamı  → CustomerAccess.EnsureSiteAsync (her eylemde)
//   • Alan adı iddiası → CustomerAccess.EnsureDomainClaimAsync (bağlama EKLEYEN eylemlerde)
// SUNUCU GENELİ olanlar müşteriye AÇILMADI:
//   • renew (tüm sertifikaları yenile) → tek çağrıda sunucudaki HER sitenin sertifikasını
//     yeniler ve win-acme'yi sunucu genelinde çalıştırır: başkasının kaynağı + DoS yüzeyi.
//   • wildcard (DNS-01) → sunucunun DNS bölgesine kayıt yazar; gövde içinde zaten admin-only.
[ApiController]
[Authorize(Roles = "Administrator,Reseller,Customer")]
[RequireSubUserSection("sites")]
[Route("api/v1/ssl")]
public sealed class SslController(
    ISslService sslService,
    ICustomerScopeService customerScopes,
    IWebmailService webmailService,
    IAuditService auditService) : ControllerBase
{
    [HttpGet("{siteName}")]
    public async Task<ActionResult<ApiResponse<SslWorkspaceDto>>> GetWorkspaceAsync(string siteName, CancellationToken cancellationToken)
    {
        var services = HttpContext.RequestServices;
        using var target = await SiteMutationGuard.CaptureAsync(
            services.GetRequiredService<IIisSiteService>(), services.GetRequiredService<IConcurrencyLock>(),
            siteName, cancellationToken);
        var db = services.GetRequiredService<AdaPanelDbContext>();
        var authority = await SiteActorAuthority.CaptureAsync(db, customerScopes, User, cancellationToken);
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        using var subscription = await DatabaseLifecycle.EnterSubscriptionAsync(authority.SubscriptionId, cancellationToken);
        await authority.RevalidateAsync(db, customerScopes, User, cancellationToken);
        var workspace = await sslService.GetSiteWorkspaceAsync(siteName, async (current, token) =>
        {
            target.ValidateSnapshot(current);
            await authority.RevalidateAsync(db, customerScopes, User, token);
            await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, token);
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Ok(ApiResponse<SslWorkspaceDto>.Ok(workspace));
    }

    [HttpPost("letsencrypt")]
    public async Task<ActionResult<ApiResponse<SslCertificateDto>>> IssueLetsEncryptAsync(
        [FromBody] IssueLetsEncryptRequest request,
        [FromServices] IIisSiteService iisSiteService,
        [FromServices] IMailService mailService,
        [FromServices] ILogger<SslController> logger,
        CancellationToken cancellationToken)
    {
        var input = new IssueLetsEncryptRequest
        {
            SiteName = request.SiteName, HostName = request.HostName, ContactEmail = request.ContactEmail,
            UseStaging = request.UseStaging, Wildcard = request.Wildcard
        };
        var services = HttpContext.RequestServices;
        using var target = await SiteMutationGuard.CaptureAsync(iisSiteService,
            services.GetRequiredService<IConcurrencyLock>(), input.SiteName, cancellationToken);
        if (string.IsNullOrWhiteSpace(input.HostName))
            input.HostName = target.Site.Bindings.FirstOrDefault(binding => !string.IsNullOrWhiteSpace(binding.HostName))?.HostName ?? string.Empty;
        var db = services.GetRequiredService<AdaPanelDbContext>();
        var authority = await SiteActorAuthority.CaptureAsync(db, customerScopes, User, cancellationToken);
        if (input.Wildcard && authority.Role != "Administrator")
            throw new AppException("ssl.wildcard_admin_only", 403, 403);
        using var subscription = await DatabaseLifecycle.EnterSubscriptionAsync(authority.SubscriptionId, cancellationToken);
        async Task ApproveAsync(SiteDto current, CancellationToken token)
        {
            target.ValidateSnapshot(current);
            await authority.RevalidateAsync(db, customerScopes, User, token);
            await CustomerAccess.EnsureSiteAsync(customerScopes, User, input.SiteName, token);
            await CustomerAccess.EnsureDomainClaimAsync(customerScopes, iisSiteService, mailService,
                User, input.HostName, input.SiteName, logger, token);
            await authority.RevalidateAsync(db, customerScopes, User, token);
            await CustomerAccess.EnsureSiteAsync(customerScopes, User, input.SiteName, token);
            target.ValidateSnapshot(current);
            token.ThrowIfCancellationRequested();
        }
        await ApproveAsync(target.Site, cancellationToken);
        var certificate = await sslService.IssueLetsEncryptAsync(input, ApproveAsync, cancellationToken);
        var owner = await SiteSahibiAsync(certificate.SiteName, cancellationToken);
        await target.RunAsync(current => ApproveAsync(current, cancellationToken), cancellationToken);

        // Sertifika ALMAK da durum değiştiren bir işlemdir: siteye HTTPS bağlaması ekler,
        // HTTP→HTTPS yönlendirmesini açar ve sunucu adına Let's Encrypt'e istek yapar (hesap
        // limitleri paylaşımlıdır). "Bu alan adına sertifikayı kim, ne zaman aldı" sorusunun
        // cevapsız kalmaması için iz bırakılır. PAROLA/ANAHTAR yazılmaz.
        await auditService.AuditAsync(
            HttpContext,
            "ssl.issued",
            $"ssl:{certificate.SiteName}",
            $"Sertifika alindi: {certificate.HostName} (kaynak={certificate.Source}, " +
            $"joker={input.Wildcard}, staging={input.UseStaging}, bitis={certificate.NotAfterUtc:yyyy-MM-dd}" +
            (string.IsNullOrWhiteSpace(certificate.FallbackReason)
                ? ")"
                : $", kendinden-imzaliya dusuldu: {certificate.FallbackReason})")
            + BaglamaNotu(certificate),
            owner);

        await target.RunAsync(current => ApproveAsync(current, cancellationToken), cancellationToken);
        await TrySecureWebmailAsync(certificate.HostName, input.ContactEmail, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Ok(ApiResponse<SslCertificateDto>.Ok(certificate, "ssl.issued"));
    }

    [HttpPost("import")]
    public async Task<ActionResult<ApiResponse<SslCertificateDto>>> ImportAsync(
        [FromBody] ImportCertificateRequest request,
        [FromServices] IIisSiteService iisSiteService,
        [FromServices] IMailService mailService,
        [FromServices] ILogger<SslController> logger,
        CancellationToken cancellationToken)
    {
        var input = new ImportCertificateRequest
        {
            SiteName = request.SiteName, HostName = request.HostName, Port = request.Port,
            Base64Content = request.Base64Content, Password = request.Password, FileName = request.FileName
        };
        var services = HttpContext.RequestServices;
        using var target = await SiteMutationGuard.CaptureAsync(iisSiteService,
            services.GetRequiredService<IConcurrencyLock>(), input.SiteName, cancellationToken);
        if (string.IsNullOrWhiteSpace(input.HostName))
            input.HostName = target.Site.Bindings.FirstOrDefault(binding => !string.IsNullOrWhiteSpace(binding.HostName))?.HostName ?? string.Empty;
        var db = services.GetRequiredService<AdaPanelDbContext>();
        var authority = await SiteActorAuthority.CaptureAsync(db, customerScopes, User, cancellationToken);
        using var subscription = await DatabaseLifecycle.EnterSubscriptionAsync(authority.SubscriptionId, cancellationToken);
        async Task ApproveAsync(SiteDto current, CancellationToken token)
        {
            target.ValidateSnapshot(current);
            await authority.RevalidateAsync(db, customerScopes, User, token);
            await CustomerAccess.EnsureSiteAsync(customerScopes, User, input.SiteName, token);
            await CustomerAccess.EnsureDomainClaimAsync(customerScopes, iisSiteService, mailService,
                User, input.HostName, input.SiteName, logger, token);
            // Both scope and domain inventory may have awaited a stale read.
            await authority.RevalidateAsync(db, customerScopes, User, token);
            await CustomerAccess.EnsureSiteAsync(customerScopes, User, input.SiteName, token);
            target.ValidateSnapshot(current);
            token.ThrowIfCancellationRequested();
        }
        await ApproveAsync(target.Site, cancellationToken);
        var certificate = await sslService.ImportCertificateAsync(input, ApproveAsync, cancellationToken);
        var owner = await SiteSahibiAsync(certificate.SiteName, cancellationToken);
        await target.RunAsync(current => ApproveAsync(current, cancellationToken), cancellationToken);

        // DENETİM KAYDI BURADA EN ÇOK GEREKİYOR: bu uç sunucuya DIŞARIDAN gelen bir sertifikayı
        // ÖZEL ANAHTARIYLA birlikte LocalMachine deposuna yazar ve siteye bağlar. Olay sonrası
        // "bu sertifikayı sunucuya kim koydu" sorusunun cevabı başka hiçbir yerde yok.
        //
        // Kayda YALNIZCA sertifikanın kendi kimliği (parmak izi, konu, bitiş) yazılır;
        // request.Base64Content ve request.Password ASLA yazılmaz — denetim kaydı yedeklere
        // taşınır ve orada bir özel anahtar/parola kopyası bulunmamalıdır.
        await auditService.AuditAsync(
            HttpContext,
            "ssl.imported",
            $"ssl:{certificate.SiteName}",
            $"Sertifika ice aktarildi: {certificate.HostName}:{certificate.Port} " +
            $"(parmak izi={certificate.Thumbprint}, konu={certificate.Subject}, " +
            $"kaynak={certificate.Source}, bitis={certificate.NotAfterUtc:yyyy-MM-dd})."
            + BaglamaNotu(certificate),
            owner);

        await target.RunAsync(current => ApproveAsync(current, cancellationToken), cancellationToken);
        await TrySecureWebmailAsync(certificate.HostName, string.Empty, cancellationToken);
        return Ok(ApiResponse<SslCertificateDto>.Ok(certificate, "ssl.imported"));
    }

    /// <summary>
    /// Denetim kaydına eklenen ölçüm notu. Her şey yolundaysa BOŞTUR — kayıt sade kalır.
    /// </summary>
    /// <remarks>
    /// "Sertifika alindi" satırı, sertifika HTTPS bağlamasına hiç işlenmemişken de yazılıyordu.
    /// Kaydı sonradan okuyan kişi sitenin 443'te çalıştığını sanır; oysa bağlama sertifikasız
    /// kalmış olabilir ve site orada ölüdür (bkz. SslBindingVerification).
    /// </remarks>
    private static string BaglamaNotu(SslCertificateDto certificate)
        => certificate.IsCertificateBound switch
        {
            true => string.Empty,
            false => $" UYARI: sertifika HTTPS baglamasina ISLENMEDI (olculdu). {certificate.BindingDetail}",
            _ => " NOT: sertifikanin baglamaya islendigi DOGRULANAMADI."
        };

    // Best-effort: site SSL aldıktan sonra webmail.<domain>'i de otomatik HTTPS yap (profesyonel paneller gibi).
    // İsteği ASLA bozmaz — webmail kurulu değilse / DNS yoksa / sertifika alınamazsa sessiz geçer.
    private async Task TrySecureWebmailAsync(string? domain, string? contactEmail, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return;
        }

        try
        {
            await webmailService.SecureWebmailForDomainAsync(domain, contactEmail ?? string.Empty, cancellationToken);
        }
        catch
        {
            // best-effort — SSL akışını bozma
        }
    }

    [HttpPost("renew")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<RenewAllResultDto>>> RenewAllAsync(CancellationToken cancellationToken)
    {
        if (!User.IsInRole("Administrator")) throw new AppException("auth.forbidden", 403, 403);
        var db = HttpContext.RequestServices.GetRequiredService<AdaPanelDbContext>();
        var authority = await SiteActorAuthority.CaptureAsync(db, customerScopes, User, cancellationToken);
        if (authority.Role != "Administrator") throw new AppException("auth.forbidden", 403, 403);
        using var actorLease = await DatabaseLifecycle.EnterSubscriptionAsync(authority.SubscriptionId, cancellationToken);
        await authority.RevalidateAsync(db, customerScopes, User, cancellationToken);
        var result = await sslService.RenewAllAsync(
            token => authority.RevalidateAsync(db, customerScopes, User, token), cancellationToken);

        // SUNUCU GENELİ bir işlem: tek çağrıda her sitenin sertifikası yenilenmeye çalışılır ve
        // Let's Encrypt hız limitleri tüketilir. Kayıt, sonucu OLDUĞU GİBİ yazar — "ölçülemedi"
        // de bir sonuçtur ve kayıtta "başarılı" görünmemelidir.
        await auditService.AuditAsync(
            HttpContext,
            "ssl.renew_all",
            "ssl:*",
            $"Tum sertifikalar icin yenileme calistirildi. Olculdu={result.Measured}, " +
            $"yenilenen={result.RenewedCount}, dogrulanan={result.VerifiedCount}, " +
            $"depoda-yok={result.MissingFromStoreCount}, dogrulanamayan={result.UnverifiedCount}" +
            (string.IsNullOrWhiteSpace(result.FailureReason) ? "." : $". Sebep: {result.FailureReason}"));

        return Ok(ApiResponse<RenewAllResultDto>.Ok(result, "ssl.renewed"));
    }

    [HttpPost("https-redirect")]
    public async Task<ActionResult<ApiResponse<SslWorkspaceDto>>> SetHttpsRedirectAsync([FromBody] SetHttpsRedirectRequest request, CancellationToken cancellationToken)
    {
        var services = HttpContext.RequestServices;
        using var target = await SiteMutationGuard.CaptureAsync(
            services.GetRequiredService<IIisSiteService>(), services.GetRequiredService<IConcurrencyLock>(),
            request.SiteName, cancellationToken);
        var db = services.GetRequiredService<AdaPanelDbContext>();
        var authority = await SiteActorAuthority.CaptureAsync(db, customerScopes, User, cancellationToken);
        using var subscription = await DatabaseLifecycle.EnterSubscriptionAsync(authority.SubscriptionId, cancellationToken);
        await authority.RevalidateAsync(db, customerScopes, User, cancellationToken);
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, request.SiteName, cancellationToken);
        var workspace = await sslService.SetHttpsRedirectAsync(request, async (current, token) =>
        {
            target.ValidateSnapshot(current);
            await authority.RevalidateAsync(db, customerScopes, User, token);
            await CustomerAccess.EnsureSiteAsync(customerScopes, User, request.SiteName, token);
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await authority.RevalidateAsync(db, customerScopes, User, cancellationToken);

        // Yönlendirmenin KAPATILMASI bir güvenlik gerilemesidir (site düz HTTP'den servis edilmeye
        // başlar) ve dışarıdan bakan biri için sessizdir. AÇILMASI da erişilebilirliği etkiler:
        // sertifika yoksa site erişilemez hâle gelebilir. İki yön de kayda geçer.
        await auditService.AuditAsync(
            HttpContext,
            "ssl.https_redirect_changed",
            $"ssl:{workspace.SiteName}",
            $"HTTP->HTTPS yonlendirmesi {(request.Enabled ? "ACILDI" : "KAPATILDI")}. " +
            $"Uygulanan durum (olculmus)={workspace.HttpsRedirectEnabled}.",
            await SiteSahibiAsync(workspace.SiteName, cancellationToken));

        return Ok(ApiResponse<SslWorkspaceDto>.Ok(workspace, "ssl.https_redirect_saved"));
    }

    /// <summary>
    /// Sitenin HTTPS bağlamasını kaldırır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bu uç uzun süre YOKTU: <c>RemoveSslCertificateRequest</c> DTO'su yazılmış ama ne servis
    /// metodu ne uç ne de ekran yapılmıştı — yani panelde bir HTTPS bağlamasını kaldırmanın
    /// hiçbir yolu bulunmuyordu. Yanlış alan adına sertifika bağlayan kullanıcının tek çaresi
    /// IIS Yöneticisi'ne düşmekti.
    /// </para>
    /// <para>
    /// Yıkıcı sayılır ve bu yüzden denetim kaydı yazar: sertifikanın ne zaman, kim tarafından
    /// kaldırıldığı sonradan sorulabilecek bir sorudur.
    /// </para>
    /// </remarks>
    [HttpPost("remove")]
    public async Task<ActionResult<ApiResponse<RemoveSslCertificateResultDto>>> RemoveCertificateAsync(
        [FromBody] RemoveSslCertificateRequest request,
        CancellationToken cancellationToken)
    {
        var services = HttpContext.RequestServices;
        using var target = await SiteMutationGuard.CaptureAsync(
            services.GetRequiredService<IIisSiteService>(), services.GetRequiredService<IConcurrencyLock>(),
            request.SiteName, cancellationToken);
        // Capture before the first scope wait; a same-name replacement is not this request's target.
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, request.SiteName, cancellationToken);
        var db = services.GetRequiredService<AdaPanelDbContext>();
        var authority = await SiteActorAuthority.CaptureAsync(db, customerScopes, User, cancellationToken);
        using var subscription = await DatabaseLifecycle.EnterSubscriptionAsync(authority.SubscriptionId, cancellationToken);
        await authority.RevalidateAsync(db, customerScopes, User, cancellationToken);
        var result = await sslService.RemoveCertificateAsync(request, async (current, token) =>
        {
            target.ValidateSnapshot(current);
            await authority.RevalidateAsync(db, customerScopes, User, token);
            await CustomerAccess.EnsureSiteAsync(customerScopes, User, request.SiteName, token);
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await authority.RevalidateAsync(db, customerScopes, User, cancellationToken);

        await auditService.AuditAsync(
            HttpContext,
            "ssl.binding_removed",
            $"ssl:{result.SiteName}",
            // Cumle degil OLCUM (bkz. DnsController): eylem etiketi zaten "HTTPS binding
            // removed" diyor; ayrinti HAM basildigi icin buradaki Turkce cumle Ingilizce
            // panelde de gorunurdu.
            $"binding={result.HostName}:{result.Port}; removed={result.BindingRemoved}; " +
            $"httpsRedirectDisabled={result.HttpsRedirectDisabled}; certificate={result.CertificateAction}",
            await SiteSahibiAsync(result.SiteName, cancellationToken));

        return Ok(ApiResponse<RemoveSslCertificateResultDto>.Ok(result, "ssl.binding_removed"));
    }

    /// <summary>
    /// Sitenin ABONELİK SAHİBİ — kaydın kiracıya görünmesini sağlayan alan.
    /// </summary>
    /// <remarks>
    /// Kapsam denetimi ÇAĞIRANIN siteye erişebildiğini doğrular; buradaki soru başka: kaynak KİME
    /// ait. İkisi ayrıldığı an (yönetici/bayi kiracının sitesine dokunur) tam da kaydın en çok
    /// gerektiği andır. Bu alan olmadan kiracı, kendi sitesinde yapılan işlemi denetim ekranında
    /// HİÇ göremezdi. Site sahipsizse (yöneticinin kendi sitesi) boş kalır; tahmin edilmez.
    /// </remarks>
    private Task<Guid?> SiteSahibiAsync(string? siteName, CancellationToken cancellationToken)
        => AuditSubject.SiteOwnerAsync(customerScopes, siteName, cancellationToken);
}
