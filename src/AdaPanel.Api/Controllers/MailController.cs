using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdaPanel.Api.Controllers;

[ApiController]
[Authorize(Roles = "Administrator,Customer,Reseller")]
[Route("api/v1/mail")]
public sealed class MailController(
    IMailService mailService,
    IWebmailService webmailService,
    ICustomerScopeService customerScopes) : ControllerBase
{
    /// <summary>
    /// Tek tık webmail (Roundcube): gerekirse kurar/yapılandırır ve açılacak URL'i döndürür.
    /// Tüm posta rolleri erişebilir; kurulum idempotenttir.
    /// </summary>
    [HttpPost("webmail/open")]
    public async Task<ActionResult<ApiResponse<WebmailOpenDto>>> OpenWebmailAsync(CancellationToken cancellationToken)
    {
        var result = await webmailService.OpenAsync(Request.Host.Host, cancellationToken);
        return Ok(ApiResponse<WebmailOpenDto>.Ok(result));
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

        // Plesk gibi: domain panelde site olarak yönetiliyorsa MX/SPF/DKIM/DMARC kayıtları
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

        // Plesk gibi CASCADE: domain panelde site olarak yönetiliyorsa otomatik eklenen
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
        await EnsureMailboxQuotaAsync(cancellationToken);
        var workspace = await mailService.CreateAccountAsync(request, cancellationToken);
        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace, "mail.account_created"));
    }

    [HttpDelete("accounts/{address}")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> DeleteAccountAsync(string address, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, ExtractDomain(address), cancellationToken);
        var workspace = await mailService.DeleteAccountAsync(address, cancellationToken);
        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace, "mail.account_deleted"));
    }

    [HttpPost("accounts/password")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> SetPasswordAsync([FromBody] SetMailAccountPasswordRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, ExtractDomain(request.Address), cancellationToken);
        var workspace = await mailService.SetAccountPasswordAsync(request, cancellationToken);
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
        return Ok(ApiResponse<MailAutoResponderDto>.Ok(dto, "mail.autoresponder_saved"));
    }

    [HttpPost("aliases")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> CreateAliasAsync([FromBody] CreateMailAliasRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, request.Domain, cancellationToken);
        var workspace = await mailService.CreateAliasAsync(request, cancellationToken);
        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace, "mail.alias_created"));
    }

    [HttpDelete("aliases/{address}")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> DeleteAliasAsync(string address, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, ExtractDomain(address), cancellationToken);
        var workspace = await mailService.DeleteAliasAsync(address, cancellationToken);
        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace, "mail.alias_deleted"));
    }

    [HttpPost("lists")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> CreateListAsync([FromBody] CreateMailListRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, request.Domain, cancellationToken);
        var workspace = await mailService.CreateMailListAsync(request, cancellationToken);
        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace, "mail.list_created"));
    }

    [HttpPut("lists/recipients")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> SetListRecipientsAsync([FromBody] SetMailListRecipientsRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, ExtractDomain(request.Address), cancellationToken);
        var workspace = await mailService.SetMailListRecipientsAsync(request, cancellationToken);
        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace, "mail.list_updated"));
    }

    [HttpDelete("lists/{address}")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> DeleteListAsync(string address, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, ExtractDomain(address), cancellationToken);
        var workspace = await mailService.DeleteMailListAsync(address, cancellationToken);
        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace, "mail.list_deleted"));
    }

    [HttpGet("listeners")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<MailListenerDto>>>> GetListenersAsync(CancellationToken cancellationToken)
    {
        var listeners = await mailService.GetListenersAsync(cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<MailListenerDto>>.Ok(listeners));
    }

    [HttpPost("domains/quota")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> SetDomainQuotaAsync([FromBody] SetMailDomainQuotaRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, request.Domain, cancellationToken);
        var workspace = await mailService.SetDomainQuotaAsync(request, cancellationToken);
        return Ok(ApiResponse<MailWorkspaceDto>.Ok(workspace, "mail.quota_saved"));
    }

    [HttpPost("accounts/quota")]
    public async Task<ActionResult<ApiResponse<MailWorkspaceDto>>> SetAccountQuotaAsync([FromBody] SetMailAccountQuotaRequest request, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureMailDomainAsync(customerScopes, User, ExtractDomain(request.Address), cancellationToken);
        var workspace = await mailService.SetAccountQuotaAsync(request, cancellationToken);
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
        return Ok(ApiResponse<MailSpamSettingsDto>.Ok(settings, "mail.spam_saved"));
    }

    // M1 (Plesk MaxMailboxes): admin olmayan kullanıcının posta kutusu kotası doldu mu? Müşterinin etkin
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
}
