using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

// DENETİM KAYDI: posta kutusu açma, PAROLA DEĞİŞTİRME ve alias oluşturma buradan yapılır ve
// bunların hiçbiri iz bırakmıyordu. Bu, kapatılan çapraz-kiracı açığının ana istismar yüzeyiydi:
// saldırgan kurbanın alan adında hesap açıp alias tanımlayarak postasını alabiliyordu ve panelde
// bunun HİÇBİR kaydı kalmıyordu — açık kapatıldıktan sonra bile "daha önce istismar edildi mi"
// sorusu cevapsızdı. Artık her durum değiştiren işlem yazılıyor; PAROLA DEĞERİ asla.
[ApiController]
[Authorize(Roles = "Administrator,Customer,Reseller")]
[RequireSubUserSection("mail")]
[Route("api/v1/mail")]
public sealed class MailController(
    IMailService mailService,
    IWebmailService webmailService,
    ICustomerScopeService customerScopes,
    IAuditService auditService) : ControllerBase
{
    /// <summary>
    /// Tek tık webmail (Roundcube): gerekirse kurar/yapılandırır ve açılacak URL'i döndürür.
    /// Tüm posta rolleri erişebilir; kurulum idempotenttir.
    /// </summary>
    [HttpPost("webmail/open")]
    public async Task<ActionResult<ApiResponse<WebmailOpenDto>>> OpenWebmailAsync(CancellationToken cancellationToken)
    {
        // "Aç" masum görünür ama gerekirse Roundcube'u KURAR/yapılandırır — yani sunucuda durum
        // değiştirir. Bu yüzden kayda geçer.
        var result = await webmailService.OpenAsync(Request.Host.Host, cancellationToken);
        await auditService.AuditAsync(HttpContext, "mail.webmail.opened", "webmail", "Webmail was opened (installed/configured if needed).");
        return Ok(ApiResponse<WebmailOpenDto>.Ok(result));
    }

    /// <summary>
    /// Webmail tanılama (salt-okunur): "neden açılmıyor"u tek raporda toplar — IIS site/binding, FastCGI'de yüklü
    /// PHP eklentileri, Roundcube kök yanıtı ve hata günlüğü. Yalnız admin (geçici diag dosyası yazıp log okur).
    /// </summary>
    [HttpGet("webmail/diagnose")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<WebmailDiagnosticsDto>>> DiagnoseWebmailAsync(CancellationToken cancellationToken)
    {
        var result = await webmailService.DiagnoseAsync(Request.Host.Host, cancellationToken);
        return Ok(ApiResponse<WebmailDiagnosticsDto>.Ok(
            result,
            result.Healthy ? "mail.webmail.diag_ok" : "mail.webmail.diag_fail"));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> GetAsync([FromQuery] string? domain, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(domain))
        {
            await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, domain, cancellationToken);
        }

        var workspace = await mailService.GetWorkspaceAsync(domain, cancellationToken);

        // Customer/Reseller yalnızca etkin kapsamındaki domainleri görür.
        var allowedDomains = await CustomerAccess.GetEffectiveMailDomainsAsync(customerScopes, User, cancellationToken);
        if (allowedDomains is not null)
        {
            workspace.Domains = workspace.Domains
                .Where(item => allowedDomains.Contains(item.Name, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }

        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace));
    }

    [HttpPost("configure")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> ConfigureAsync([FromBody] ConfigureMailRequest request, CancellationToken cancellationToken)
    {
        var workspace = await mailService.ConfigureAsync(request, cancellationToken);

        // request.AdminPassword KAYDA GİRMEZ — posta sunucusunun yönetici parolasıdır.
        await auditService.AuditAsync(HttpContext, "mail.configured", "mail-server", "Mail server was configured.");

        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace, "mail.configured"));
    }

    [HttpPost("domains")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> CreateDomainAsync(
        [FromBody] CreateMailDomainRequest request,
        [FromServices] IDnsService dnsService,
        [FromServices] ILogger<MailController> logger,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, request.DomainName, cancellationToken);
        var workspace = await mailService.CreateDomainAsync(request, cancellationToken);

        // profesyonel paneller gibi: domain panelde site olarak yönetiliyorsa MX/SPF/DKIM/DMARC kayıtları
        // panel DNS bölgesine OTOMATİK eklenir. Site yoksa (dış DNS) sessizce geçilir;
        // kullanıcı kayıtları ekrandaki DNS panelinden kopyalar.
        var dnsApplied = false;
        try
        {
            await ApplyDnsRecordsAsync(request.DomainName, dnsService, cancellationToken);
            dnsApplied = true;
        }
        catch (Exception exception)
        {
            logger.LogInformation(
                "Mail DNS records were not auto-applied for {Domain}: {Reason}",
                request.DomainName,
                exception.Message);
        }

        await auditService.AuditAsync(
            HttpContext,
            "mail.domain.created",
            request.DomainName,
            $"Mail domain created (DNS records auto-applied: {dnsApplied}).");

        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace, dnsApplied ? "mail.domain_created_dns" : "mail.domain_created"));
    }

    [HttpDelete("domains/{domainName}")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> DeleteDomainAsync(
        string domainName,
        [FromServices] IDnsService dnsService,
        [FromServices] ILogger<MailController> logger,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, domainName, cancellationToken);

        // Domaine ait DNS kayıtlarını domain silinmeden ÖNCE çöz (silindikten sonra
        // GetDnsRecordsAsync DKIM anahtarını bulamaz). Kaldırma domain silindikten sonra yapılır.
        Core.Dtos.Mail.MailDnsDto? mailDns = null;
        try
        {
            mailDns = await mailService.GetDnsRecordsAsync(domainName, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogInformation("Mail DNS kayıtları silme öncesi okunamadı ({Domain}): {Reason}", domainName, exception.Message);
        }

        var workspace = await mailService.DeleteDomainAsync(domainName, cancellationToken);

        // profesyonel paneller gibi CASCADE: domain panelde site olarak yönetiliyorsa otomatik eklenen
        // mail kayıtları (MX/SPF/DKIM/DMARC/mail A) panel DNS bölgesinden de kaldırılır.
        // Sitenin web kayıtları (kök A/www) mail'e ait olmadığından korunur. Best-effort.
        if (mailDns is not null)
        {
            try
            {
                await RemoveDnsRecordsAsync(domainName, mailDns, dnsService, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogInformation("Mail DNS kayıtları kaldırılamadı ({Domain}): {Reason}", domainName, exception.Message);
            }
        }

        // Alan adı silmek, o alandaki BÜTÜN posta kutularını ve içeriğini götürür — panelin en
        // yıkıcı posta işlemi; iz bırakmaması düşünülemez.
        await auditService.AuditAsync(HttpContext, "mail.domain.deleted", domainName, "Mail domain and its mailboxes were deleted.");

        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace, "mail.domain_deleted"));
    }

    private async Task RemoveDnsRecordsAsync(string domain, Core.Dtos.Mail.MailDnsDto mailDns, IDnsService dnsService, CancellationToken cancellationToken)
    {
        var zone = await dnsService.GetZoneAsync(domain, cancellationToken);
        foreach (var record in zone.Records)
        {
            var matchesMailRecord = mailDns.Records.Any(mail =>
                string.Equals(mail.Type, record.Type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(mail.Host.Trim('.'), record.Host.Trim('.'), StringComparison.OrdinalIgnoreCase) &&
                // TXT host'ları (SPF/DKIM/DMARC) çakışabilir; değer de eşleşmeli ki kullanıcının
                // başka TXT kaydı yanlışlıkla silinmesin. MX/A için host+tip yeterli (mail'e özgü).
                (!string.Equals(record.Type, "TXT", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(mail.Value.Trim(), record.Value.Trim(), StringComparison.Ordinal)));

            if (matchesMailRecord && !string.IsNullOrWhiteSpace(record.Id))
            {
                await dnsService.DeleteRecordAsync(domain, record.Id, cancellationToken);
            }
        }
    }

    [HttpPost("accounts")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> CreateAccountAsync([FromBody] CreateMailAccountRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, request.Domain, cancellationToken);
        await EnsureAddressDomainAsync(request.Address, request.Domain, cancellationToken);
        await EnsureMailboxQuotaAsync(cancellationToken);
        var workspace = await mailService.CreateAccountAsync(request, cancellationToken);

        // request.Password KAYDA GİRMEZ.
        await auditService.AuditAsync(
            HttpContext,
            "mail.account.created",
            NormalizeAddress(request.Address, request.Domain),
            $"Mailbox created with {request.MaxSizeMB} MB quota.");

        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace, "mail.account_created"));
    }

    [HttpDelete("accounts/{address}")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> DeleteAccountAsync(string address, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, ExtractDomain(address), cancellationToken);
        var workspace = await mailService.DeleteAccountAsync(address, cancellationToken);
        await auditService.AuditAsync(HttpContext, "mail.account.deleted", address, "Mailbox and its messages were deleted.");
        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace, "mail.account_deleted"));
    }

    [HttpPost("accounts/password")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> SetPasswordAsync([FromBody] SetMailAccountPasswordRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, ExtractDomain(request.Address), cancellationToken);
        var workspace = await mailService.SetAccountPasswordAsync(request, cancellationToken);

        // Posta kutusu parolasını değiştirmek, o kutunun TÜM geçmiş yazışmasını ele geçirmektir.
        // Bu yüzden en kritik izlerden biri — ama yazılan şey OLGUDUR, parolanın kendisi değil.
        await auditService.AuditAsync(
            HttpContext,
            "mail.account.password_changed",
            request.Address,
            "Mailbox password was changed.");

        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace, "mail.password_changed"));
    }

    [HttpGet("accounts/autoresponder")]
    public async Task<ActionResult<ApiResponse<MailAutoResponderDto>>> GetAutoResponderAsync([FromQuery] string address, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, ExtractDomain(address), cancellationToken);
        var dto = await mailService.GetAutoResponderAsync(address, cancellationToken);
        return Ok(ApiResponse<MailAutoResponderDto>.Ok(dto));
    }

    [HttpPost("accounts/autoresponder")]
    public async Task<ActionResult<ApiResponse<MailAutoResponderDto>>> SetAutoResponderAsync([FromBody] SetMailAutoResponderRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, ExtractDomain(request.Address), cancellationToken);
        var dto = await mailService.SetAutoResponderAsync(request, cancellationToken);

        // Otomatik yanıt METNİ yazılmaz (kişisel/serbest içerik); yalnız açıldı/kapandı olgusu.
        await auditService.AuditAsync(
            HttpContext,
            "mail.autoresponder.updated",
            request.Address,
            $"Auto-responder enabled={request.Enabled}.");

        return Ok(ApiResponse<MailAutoResponderDto>.Ok(dto, "mail.autoresponder_saved"));
    }

    [HttpPost("aliases")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> CreateAliasAsync([FromBody] CreateMailAliasRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, request.Domain, cancellationToken);
        await EnsureAddressDomainAsync(request.Address, request.Domain, cancellationToken);
        await EnsureAddressDomainAsync(request.Target, request.Domain, cancellationToken);
        var workspace = await mailService.CreateAliasAsync(request, cancellationToken);

        // ALIAS = SESSİZ POSTA YÖNLENDİRME. Kurbanın adresine tanımlanan bir alias, gelen postayı
        // saldırgana kopyalar ve kutuda hiçbir iz bırakmaz. HEDEF adres de yazılır: "nereye
        // yönlendirildi" sorusunun cevabı olmadan bu kayıt işe yaramaz.
        await auditService.AuditAsync(
            HttpContext,
            "mail.alias.created",
            NormalizeAddress(request.Address, request.Domain),
            $"Alias forwards to '{request.Target}'.");

        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace, "mail.alias_created"));
    }

    [HttpDelete("aliases/{address}")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> DeleteAliasAsync(string address, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, ExtractDomain(address), cancellationToken);
        var workspace = await mailService.DeleteAliasAsync(address, cancellationToken);
        await auditService.AuditAsync(HttpContext, "mail.alias.deleted", address, "Mail alias was removed.");
        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace, "mail.alias_deleted"));
    }

    [HttpPost("lists")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> CreateListAsync([FromBody] CreateMailListRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, request.Domain, cancellationToken);
        await EnsureAddressDomainAsync(request.Address, request.Domain, cancellationToken);
        var workspace = await mailService.CreateMailListAsync(request, cancellationToken);
        await auditService.AuditAsync(
            HttpContext,
            "mail.list.created",
            NormalizeAddress(request.Address, request.Domain),
            $"Mailing list created with {request.Recipients.Count} subscriber(s), posting policy '{request.PostingPolicy}'.");
        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace, "mail.list_created"));
    }

    [HttpPut("lists/recipients")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> SetListRecipientsAsync([FromBody] SetMailListRecipientsRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, ExtractDomain(request.Address), cancellationToken);

        // Hiçbir şey istenmemişse çağrıyı sunucuya taşımayız: "kaydedildi" yanıtı dönüp hiçbir şey
        // değiştirmemek, kullanıcıya işlem olmuş gibi görünür.
        if (request.Recipients is null && request.Add.Count == 0 && request.Remove.Count == 0
            && request.PostingPolicy is null && request.AllowedSenders is null)
        {
            throw new Core.Common.AppException("mail.list_no_change", 400, 400);
        }

        var workspace = await mailService.SetMailListRecipientsAsync(request, cancellationToken);

        // Abone kümesine yabancı bir adres eklemek, listeye giden her postayı dışarı taşır —
        // alias ile aynı sınıftan bir sızdırma yolu. Bu yüzden HANGİ adreslerin işlendiği yazılır;
        // yalnız "güncellendi" demek, sızdırmanın kime yapıldığını cevapsız bırakırdı.
        var changes = new List<string>();
        if (request.Recipients is not null)
        {
            changes.Add($"set to: {FormatAddresses(request.Recipients)}");
        }

        if (request.Add.Count > 0)
        {
            changes.Add($"subscribed: {FormatAddresses(request.Add)}");
        }

        if (request.Remove.Count > 0)
        {
            changes.Add($"unsubscribed: {FormatAddresses(request.Remove)}");
        }

        // GÖNDERİM POLİTİKASI bir GÜVENLİK ayarıdır: "yalnız aboneler"den "herkes"e geçmek, listeyi
        // kimliği doğrulanmamış dış göndericilere açar. Kimin ne zaman gevşettiği kayıtta durmalı.
        if (request.PostingPolicy is not null)
        {
            changes.Add($"posting policy: {request.PostingPolicy}");
        }

        if (request.AllowedSenders is not null)
        {
            changes.Add($"allowed senders: {FormatAddresses(request.AllowedSenders)}");
        }

        await auditService.AuditAsync(
            HttpContext,
            "mail.list.recipients_updated",
            request.Address,
            $"Mailing list subscribers changed ({string.Join("; ", changes)}).");

        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace, "mail.list_updated"));
    }

    /// <summary>
    /// Denetim kaydı için adres listesi. Uzun listeler KIRPILIR ama kaç adres olduğu yazılır:
    /// kayıt satırının kendisi bir sızıntı kanalı olmamalı, yine de "kaç kişiye açıldı" sorusu
    /// cevapsız kalmamalı.
    /// </summary>
    private const int AuditAddressLimit = 20;

    private static string FormatAddresses(IReadOnlyCollection<string> addresses)
    {
        if (addresses.Count == 0)
        {
            return "(none)";
        }

        var shown = string.Join(", ", addresses.Take(AuditAddressLimit));
        return addresses.Count > AuditAddressLimit
            ? $"{shown} (+{addresses.Count - AuditAddressLimit} more, {addresses.Count} total)"
            : shown;
    }

    [HttpDelete("lists/{address}")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> DeleteListAsync(string address, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, ExtractDomain(address), cancellationToken);
        var workspace = await mailService.DeleteMailListAsync(address, cancellationToken);
        await auditService.AuditAsync(HttpContext, "mail.list.deleted", address, "Mailing list was removed.");
        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace, "mail.list_deleted"));
    }

    [HttpGet("listeners")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<MailListenerDto>>>> GetListenersAsync(CancellationToken cancellationToken)
    {
        var listeners = await mailService.GetListenersAsync(cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<MailListenerDto>>.Ok(listeners));
    }

    /// <summary>Alan adının GİDEN posta limiti (saatlik).</summary>
    [HttpGet("domains/outbound-limit")]
    public async Task<ActionResult<ApiResponse<MailOutboundLimitDto>>> GetOutboundLimitAsync(
        [FromQuery] string domain,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, domain, cancellationToken);
        return Ok(ApiResponse<MailOutboundLimitDto>.Ok(await mailService.GetOutboundLimitAsync(domain, cancellationToken)));
    }

    /// <summary>
    /// Giden posta limitini ayarlar (0 = kaldır). YALNIZ YÖNETİCİ.
    /// </summary>
    /// <remarks>
    /// Bu limit sunucunun itibarını korur: hacklenen tek müşteri IP'yi kara listeye düşürünce
    /// o sunucudaki HERKESİN postası teslim edilmez olur. Dolayısıyla kendi limitini müşterinin
    /// (ya da bayinin) yükseltebilmesi, korumanın kendisini anlamsız kılardı.
    /// </remarks>
    [HttpPut("domains/outbound-limit")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<MailOutboundLimitDto>>> SetOutboundLimitAsync(
        [FromBody] SetMailOutboundLimitRequest request,
        CancellationToken cancellationToken)
    {
        var limit = await mailService.SetOutboundLimitAsync(request.Domain, request.MessagesPerHour, cancellationToken);
        await auditService.AuditAsync(
            HttpContext,
            "mail.outbound_limit.updated",
            request.Domain,
            $"Outbound limit set to {request.MessagesPerHour} message(s)/hour (0 = unlimited).");
        return Ok(ApiResponse<MailOutboundLimitDto>.Ok(limit, "mail.outbound_limit_saved"));
    }

    [HttpPost("domains/quota")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> SetDomainQuotaAsync([FromBody] SetMailDomainQuotaRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, request.Domain, cancellationToken);
        var workspace = await mailService.SetDomainQuotaAsync(request, cancellationToken);
        await auditService.AuditAsync(
            HttpContext,
            "mail.domain.quota_updated",
            request.Domain,
            $"Domain mail quota set to {request.MaxSizeMB} MB.");
        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace, "mail.quota_saved"));
    }

    [HttpPost("accounts/quota")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> SetAccountQuotaAsync([FromBody] SetMailAccountQuotaRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, ExtractDomain(request.Address), cancellationToken);
        var workspace = await mailService.SetAccountQuotaAsync(request, cancellationToken);

        // Kotayı KÜÇÜLTMEK gelen postanın reddedilmesine yol açar; sessiz bir hizmet kesintisi
        // yoludur (kapatılan çapraz-kiracı açığının somut etkilerinden biriydi).
        await auditService.AuditAsync(
            HttpContext,
            "mail.account.quota_updated",
            request.Address,
            $"Mailbox quota set to {request.MaxSizeMB} MB.");

        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace, "mail.quota_saved"));
    }

    [HttpGet("dns/{domain}")]
    public async Task<ActionResult<ApiResponse<MailDnsDto>>> GetDnsAsync(string domain, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, domain, cancellationToken);
        var dns = await mailService.GetDnsRecordsAsync(domain, cancellationToken);
        return Ok(ApiResponse<MailDnsDto>.Ok(dns));
    }

    /// <summary>
    /// Mail teşhisi: domainin yayınlanmış MX/A/SPF/DKIM/DMARC + PTR kayıtlarını canlı sorgulayıp
    /// panelin beklediğiyle karşılaştırır. Eksik olanı kullanıcıya gösterir (kendisi aramasın).
    /// </summary>
    [HttpGet("diagnostics/{domain}")]
    public async Task<ActionResult<ApiResponse<MailDiagnosticsDto>>> GetDiagnosticsAsync(string domain, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, domain, cancellationToken);
        var result = await mailService.GetDiagnosticsAsync(domain, cancellationToken);
        return Ok(ApiResponse<MailDiagnosticsDto>.Ok(result));
    }

    /// <summary>
    /// Ters DNS (rDNS / PTR) dogrulama. host bossa panel sunucu adresi kullanilir. PTR kaydi
    /// VPS saglayicida tutuldugundan panel yazamaz; bu uc yalnizca durumu kontrol edip yonlendirir.
    /// </summary>
    [HttpGet("rdns")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<RdnsCheckDto>>> CheckRdnsAsync(
        [FromQuery] string? host,
        [FromServices] IRdnsService rdnsService,
        CancellationToken cancellationToken)
    {
        var result = await rdnsService.CheckAsync(host, cancellationToken);
        return Ok(ApiResponse<RdnsCheckDto>.Ok(result));
    }

    /// <summary>
    /// Üretilen mail DNS kayıtlarını, aynı adlı sitenin panel DNS bölgesine tek tıkla uygular.
    /// Domain panelde site olarak yoksa kullanıcı kayıtları dış DNS sağlayıcısına elle ekler.
    /// </summary>
    [HttpPost("dns/{domain}/apply")]
    public async Task<ActionResult<ApiResponse<ApplyMailDnsResultDto>>> ApplyDnsAsync(
        string domain,
        [FromServices] IDnsService dnsService,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, domain, cancellationToken);

        try
        {
            var result = await ApplyDnsRecordsAsync(domain, dnsService, cancellationToken);
            await auditService.AuditAsync(
                HttpContext,
                "mail.dns.applied",
                domain,
                $"{result.AppliedCount} mail DNS record(s) written to zone '{result.ZoneName}'.");
            return Ok(ApiResponse<ApplyMailDnsResultDto>.Ok(result, "mail.dns_applied"));
        }
        catch (Core.Common.AppException exception) when (exception.HttpStatusCode == 404)
        {
            throw new Core.Common.AppException("mail.dns_zone_missing", 404, 404, exception.Message);
        }
    }

    /// <summary>Mail DNS kayıtlarını aynı adlı sitenin panel DNS bölgesine yazar.</summary>
    private async Task<ApplyMailDnsResultDto> ApplyDnsRecordsAsync(string domain, IDnsService dnsService, CancellationToken cancellationToken)
    {
        var dns = await mailService.GetDnsRecordsAsync(domain, cancellationToken);

        Core.Dtos.Sites.SiteDnsZoneDto? zone = null;
        foreach (var record in dns.Records.Where(record => !string.IsNullOrWhiteSpace(record.Value)))
        {
            zone = await dnsService.UpsertRecordAsync(dns.Domain, new Core.Dtos.Sites.UpsertDnsRecordRequest
            {
                Type = record.Type,
                Host = record.Host,
                Value = record.Value,
                Priority = record.Priority,
                Ttl = 3600
            }, cancellationToken);
        }

        return new ApplyMailDnsResultDto
        {
            ZoneName = zone?.ZoneName ?? dns.Domain,
            AppliedCount = dns.Records.Count(record => !string.IsNullOrWhiteSpace(record.Value))
        };
    }

    [HttpGet("certificate")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<MailCertificateDto>>> GetCertificateAsync(CancellationToken cancellationToken)
    {
        var status = await mailService.GetCertificateStatusAsync(cancellationToken);
        return Ok(ApiResponse<MailCertificateDto>.Ok(status));
    }

    [HttpPost("certificate")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<MailCertificateDto>>> IssueCertificateAsync(
        [FromBody] IssueMailCertificateRequest request)
    {
        // Sertifika alma uzun sürebilir (ACME doğrulaması); istemci iptaline bağlanmaz.
        var status = await mailService.IssueCertificateAsync(request, CancellationToken.None);
        await auditService.AuditAsync(
            HttpContext,
            "mail.certificate.issued",
            string.IsNullOrWhiteSpace(request.HostName) ? request.Domain : request.HostName,
            "Mail server TLS certificate was issued.");
        return Ok(ApiResponse<MailCertificateDto>.Ok(status, "mail.certificate_issued"));
    }

    [HttpGet("spam")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<MailSpamSettingsDto>>> GetSpamAsync(CancellationToken cancellationToken)
    {
        var settings = await mailService.GetSpamSettingsAsync(cancellationToken);
        return Ok(ApiResponse<MailSpamSettingsDto>.Ok(settings));
    }

    [HttpPut("spam")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<MailSpamSettingsDto>>> UpdateSpamAsync([FromBody] UpdateMailSpamSettingsRequest request, CancellationToken cancellationToken)
    {
        var settings = await mailService.UpdateSpamSettingsAsync(request, cancellationToken);

        // Spam korumasını kapatmak sunucu genelini etkiler (itibar/kara liste riski).
        await auditService.AuditAsync(
            HttpContext,
            "mail.spam.updated",
            "mail-server",
            $"Spam filtering enabled={request.Enabled}, spam score={request.ScoreSpam}, reject={request.ScoreReject}, greylist={request.GreylistEnabled}.");

        return Ok(ApiResponse<MailSpamSettingsDto>.Ok(settings, "mail.spam_saved"));
    }

    /// <summary>
    /// Giden posta kuyruğu (Plesk'teki "Mail Queue" ekranının karşılığı). YALNIZ YÖNETİCİ.
    /// </summary>
    /// <remarks>
    /// Kuyruk, sunucudaki BÜTÜN kiracıların zarf gönderici/alıcı adreslerini içerir; müşteriye
    /// açılması, komşusunun kiminle yazıştığını göstermek olurdu. Bu yüzden kapsam filtresiyle
    /// değil, doğrudan rolle sınırlandı.
    /// </remarks>
    [HttpGet("queue")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<MailQueueDto>>> GetQueueAsync(
        [FromQuery] int limit,
        CancellationToken cancellationToken)
    {
        var queue = await mailService.GetQueueAsync(limit, cancellationToken);
        return Ok(ApiResponse<MailQueueDto>.Ok(queue));
    }

    /// <summary>Kuyruktaki tek bir iletinin ayrıntısı (alıcı başına durum). YALNIZ YÖNETİCİ.</summary>
    [HttpGet("queue/{id}")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<MailQueueMessageDto>>> GetQueueMessageAsync(string id, CancellationToken cancellationToken)
    {
        var message = await mailService.GetQueueMessageAsync(id, cancellationToken);
        return message is null
            ? throw new Core.Common.AppException("mail.queue_message_not_found", 404, 404)
            : Ok(ApiResponse<MailQueueMessageDto>.Ok(message));
    }

    /// <summary>
    /// Kuyruktaki iletiyi siler (teslim edilmez). YALNIZ YÖNETİCİ.
    /// </summary>
    /// <remarks>
    /// Bir iletiyi kuyruktan silmek, GÖNDERİLMEMİŞ bir postayı geri dönülemez biçimde yok
    /// etmektir. Kimin hangi iletiyi düşürdüğü kayda geçmezse, "postam gitmedi" şikâyeti
    /// sonsuza kadar cevapsız kalır.
    /// </remarks>
    [HttpDelete("queue/{id}")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteQueueMessageAsync(string id, CancellationToken cancellationToken)
    {
        var deleted = await mailService.DeleteQueueMessageAsync(id, cancellationToken);
        if (!deleted)
        {
            throw new Core.Common.AppException("mail.queue_message_not_found", 404, 404);
        }

        await auditService.AuditAsync(HttpContext, "mail.queue.message_deleted", id, "Queued message was deleted before delivery.");
        return Ok(ApiResponse<bool>.Ok(true, "mail.queue_message_deleted"));
    }

    /// <summary>Giden posta rölesi (smarthost) ayarı. YALNIZ YÖNETİCİ; parola ASLA dönmez.</summary>
    [HttpGet("relay")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<MailRelayDto>>> GetRelayAsync(CancellationToken cancellationToken)
    {
        var relay = await mailService.GetRelayAsync(cancellationToken);
        return Ok(ApiResponse<MailRelayDto>.Ok(relay));
    }

    /// <summary>
    /// Giden posta rölesini kurar ya da kaldırır. YALNIZ YÖNETİCİ.
    /// </summary>
    /// <remarks>
    /// Sunucunun TÜM giden postasının hangi üçüncü tarafa teslim edildiğini belirler; müşteri ya
    /// da bayinin değiştirebilmesi, bütün kiracıların postasını başka bir sunucuya yönlendirme
    /// yetkisi vermek olurdu.
    /// </remarks>
    [HttpPut("relay")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<MailRelayDto>>> SetRelayAsync([FromBody] SetMailRelayRequest request, CancellationToken cancellationToken)
    {
        var relay = await mailService.SetRelayAsync(request, cancellationToken);

        // request.Password KAYDA GİRMEZ — kayıt, korumaya çalıştığı sırrın ikinci deposu olmamalı.
        // Yazılan şey OLGUDUR: giden postanın nereye teslim edildiği ve kimlik doğrulaması olup olmadığı.
        await auditService.AuditAsync(
            HttpContext,
            "mail.relay.updated",
            request.Enabled ? $"{request.Host}:{request.Port}" : "mail-server",
            request.Enabled
                ? $"Outbound mail relayed through '{request.Host}:{request.Port}' (implicit TLS: {request.ImplicitTls}, authenticated: {!string.IsNullOrWhiteSpace(request.Username)})."
                : "Outbound mail relay was removed; delivery returned to direct MX routing.");

        return Ok(ApiResponse<MailRelayDto>.Ok(relay, request.Enabled ? "mail.relay_saved" : "mail.relay_removed"));
    }

    /// <summary>
    /// Bir alan adının posta istemcisi ayarları (sunucu adı/port/şifreleme) — panel arayüzünün
    /// "posta istemci ayarları" kartı için. Autodiscover uçlarıyla AYNI kaynaktan gelir.
    /// </summary>
    [HttpGet("client-settings/{domain}")]
    public async Task<ActionResult<ApiResponse<MailClientSettingsDto>>> GetClientSettingsAsync(string domain, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, domain, cancellationToken);
        var settings = await mailService.GetClientSettingsAsync(domain, cancellationToken);
        return settings is null
            ? throw new Core.Common.AppException("mail.client_settings_unavailable", 404, 404)
            : Ok(ApiResponse<MailClientSettingsDto>.Ok(settings));
    }

    // M1 (panel MaxMailboxes): admin olmayan kullanıcının posta kutusu kotası doldu mu? Müşterinin etkin
    // mail domainlerindeki kutuları sayar. Admin sınırsız; MaxMailboxes=0 = limitsiz.
    private async Task EnsureMailboxQuotaAsync(CancellationToken cancellationToken)
    {
        if (User.IsInRole("Administrator"))
        {
            return;
        }

        var scope = await customerScopes.GetScopeAsync(User.GetRequiredUserId(), cancellationToken);
        if (scope.MaxMailboxes <= 0)
        {
            return;
        }

        var allowed = await CustomerAccess.GetEffectiveMailDomainsAsync(customerScopes, User, cancellationToken);
        if (allowed is null)
        {
            return;
        }

        var count = 0;
        foreach (var domain in allowed)
        {
            var workspace = await mailService.GetWorkspaceAsync(domain, cancellationToken);
            count += workspace.Accounts?.Count ?? 0;
            if (count >= scope.MaxMailboxes)
            {
                throw new Core.Common.AppException("mail.mailbox_quota_exceeded", 403, 403);
            }
        }
    }

    private static string ExtractDomain(string address)
    {
        var index = (address ?? string.Empty).IndexOf('@');
        return index >= 0 ? address![(index + 1)..].Trim() : string.Empty;
    }

    /// <summary>
    /// Denetim kaydının "neye" alanı için tam adres. İstekte adres yerel kısım ("info") ya da tam
    /// adres ("info@example.com") olabilir; kayıtta HER ZAMAN tam adres durmalı, yoksa iki farklı
    /// alan adındaki aynı yerel kısım kayıtta ayırt edilemez.
    /// </summary>
    private static string NormalizeAddress(string? address, string? fallbackDomain)
    {
        var trimmed = (address ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Contains('@'))
        {
            return trimmed;
        }

        var domain = (fallbackDomain ?? string.Empty).Trim();
        return domain.Length == 0 ? trimmed : $"{trimmed}@{domain}";
    }

    /// <summary>
    /// İstekteki bir ADRESİN alan adını, kullanıcının izinli mail domainlerine göre doğrular.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden ayrıca gerekli:</b> bu uçlarda <c>request.Domain</c> doğrulanıyordu ama servis
    /// adresi <c>NormalizeAddress(address, domain)</c> ile çözüyor ve o metot, adres bir "@"
    /// içeriyorsa alan adını <b>tamamen adresten</b> alıyor — doğrulanan <c>Domain</c> yok
    /// sayılıyor. Yani kapsam denetimi bir alan adına, işlem başka bir alan adına yapılıyordu.
    /// </para>
    /// <para>
    /// Somut sonuç: <c>{ Domain: "benim.com", Target: "ceo@kurban.com" }</c> ile alias, kurbanın
    /// POSTA HESABI NESNESİNE yazılıyordu (Stalwart'ta alias hedef hesabın üzerinde durur), ve
    /// <c>{ Domain: "benim.com", Address: "info@kurban.com", MaxSizeMB: 1 }</c> ile kurbanın
    /// kutusu 1 MB'a düşürülüp gelen postası reddedilir hâle geliyordu.
    /// </para>
    /// <para>
    /// Kontrol, koruduğu şeyle <b>aynı kuralı</b> uygular: adres "@" içeriyorsa alan adı ondan,
    /// içermiyorsa istekteki <paramref name="fallbackDomain"/>'den alınır.
    /// </para>
    /// </remarks>
    private Task EnsureAddressDomainAsync(string? address, string? fallbackDomain, CancellationToken cancellationToken)
    {
        var trimmed = (address ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return Task.CompletedTask;
        }

        var domain = trimmed.Contains('@') ? ExtractDomain(trimmed) : (fallbackDomain ?? string.Empty).Trim();
        return CustomerAccess.EnsureMailDomainAsync(customerScopes, User, domain, cancellationToken);
    }
}

/// <summary>Giden posta limiti isteği.</summary>
public sealed class SetMailOutboundLimitRequest
{
    public string Domain { get; set; } = string.Empty;

    /// <summary>Saatte en fazla ileti; 0 = limiti kaldır.</summary>
    public int MessagesPerHour { get; set; }
}
