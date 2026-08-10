using System.ComponentModel;
using System.Security.Claims;
using AdaPanel.Api.Security;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using ModelContextProtocol.Server;

namespace AdaPanel.Api.Mcp;

/// <summary>
/// Değişiklik yapan araçlar. İkiye ayrılır ve ayrım araç gövdesinde ZORLANIR:
/// <list type="bullet">
/// <item>GERİ ALINABİLİR işlemler (site başlat/durdur, DNS kaydı ekleme) — ek yetki istemez.</item>
/// <item>YIKICI işlemler (silme, üzerine yazma) — anahtarın <c>AllowDestructive</c> yetkisini ister.</item>
/// </list>
/// <para>
/// Bu sınıfa da <c>ITerminalService</c>, <c>IFileService</c> ve <c>ISiteCascadeDeletionService</c>
/// enjekte EDİLMEZ. Site silme gibi geri alınamaz işlemler bilerek dışarıda: bir yapay zeka
/// istemcisine tek çağrıyla bir müşterinin tüm varlığını (site + veritabanı + posta + dosyalar)
/// silme yeteneği vermek, kazancıyla kıyaslanamayacak bir risktir. Bunlar panelden yapılır.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class AdaPanelWriteTools(
    IHttpContextAccessor httpContextAccessor,
    ICustomerScopeService customerScopes,
    IIisSiteService siteService,
    IDnsService dnsService,
    IMailService mailService,
    IAuditService auditService)
{
    [McpServerTool(Name = "site_start")]
    [Description("Durmuş bir siteyi başlatır. Geri alınabilir işlem.")]
    public async Task<object> StartSiteAsync(
        [Description("Site adı")] string siteName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        // ORTAK YAZMA KAPISI: abonelik askısı + alt-kullanıcı bölüm izni. Askı MVC filtresiyle
        // uygulanıyordu ve /mcp o boru hattından geçmediği için askıdaki abonelik buradan
        // YAZMAYA DEVAM EDEBİLİYORDU. Gerekçe McpCapability.EnsureWriteAllowedAsync'te.
        await McpCapability.EnsureWriteAllowedAsync(customerScopes, user, "sites", cancellationToken);
        await CustomerAccess.EnsureSiteAsync(customerScopes, user, siteName, cancellationToken);
        var site = await siteService.StartSiteAsync(siteName, cancellationToken);
        await WriteAuditAsync(user, "mcp.site.start", siteName, cancellationToken);
        return new { site.Name, site.State };
    }

    [McpServerTool(Name = "site_stop")]
    [Description("Çalışan bir siteyi durdurur. Site erişilemez hâle gelir; yıkıcı sayılır çünkü ziyaretçiler etkilenir.")]
    public async Task<object> StopSiteAsync(
        [Description("Site adı")] string siteName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        McpCapability.RequireDestructive(user);
        await McpCapability.EnsureWriteAllowedAsync(customerScopes, user, "sites", cancellationToken);
        await CustomerAccess.EnsureSiteAsync(customerScopes, user, siteName, cancellationToken);
        var site = await siteService.StopSiteAsync(siteName, cancellationToken);
        await WriteAuditAsync(user, "mcp.site.stop", siteName, cancellationToken);
        return new { site.Name, site.State };
    }

    [McpServerTool(Name = "dns_upsert_record")]
    [Description("DNS kaydı ekler ya da aynı tür+konak varsa günceller. Yaygın kullanım: eksik SPF/DMARC/DKIM kaydı eklemek.")]
    public async Task<object> UpsertDnsRecordAsync(
        [Description("Site adı")] string siteName,
        [Description("Kayıt türü: A, AAAA, CNAME, MX, TXT, SRV, CAA, TLSA, NS, PTR")] string type,
        [Description("Konak. Zone kökü için '@'. Alt çizgiyle başlayabilir: _dmarc, _acme-challenge")] string host,
        [Description("Kayıt değeri")] string value,
        [Description("MX/SRV için öncelik; diğerlerinde boş bırakın")] int? priority,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        // Kayıt ÜZERİNE YAZMA ihtimali taşıdığı için yıkıcı sayılıyor: var olan bir MX'i
        // değiştirmek postayı kesebilir.
        McpCapability.RequireDestructive(user);
        // ROL PARİTESİ: REST ikizi (DnsController) yalnız yönetici ve bayiye açık.
        McpCapability.RequireDnsRole(user);
        await McpCapability.EnsureWriteAllowedAsync(customerScopes, user, "sites", cancellationToken);
        // SAHİPLİK: bu denetim olmadan bir müşteri anahtarı BAŞKA bir müşterinin DNS kaydını
        // değiştirebilirdi. Controller'lar bunu yapıyor; araçların da yapması şart.
        await CustomerAccess.EnsureSiteAsync(customerScopes, user, siteName, cancellationToken);

        var zone = await dnsService.UpsertRecordAsync(
            siteName,
            new UpsertDnsRecordRequest { Type = type, Host = host, Value = value, Priority = priority, Ttl = 3600 },
            cancellationToken);

        await WriteAuditAsync(user, "mcp.dns.upsert", $"{siteName}: {type} {host}", cancellationToken);
        return new { zone.ZoneName, RecordCount = zone.Records.Count() };
    }

    [McpServerTool(Name = "mail_dns_apply")]
    [Description("Posta sunucusunun ürettiği DNS kayıtlarını (MX, SPF, DKIM, DMARC) sitenin bölgesine yazar. Giden postanın spam'e düşmesini önlemek için en sık gereken işlem.")]
    public async Task<object> ApplyMailDnsAsync(
        [Description("Alan adı")] string domain,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        McpCapability.RequireDestructive(user);
        // Bölüm "mail": REST ikizi (MailController → POST mail/dns/{domain}/apply) müşteriye açık ve
        // "mail" bölümüyle korunuyor; buradaki kilit de aynı olmalı.
        await McpCapability.EnsureWriteAllowedAsync(customerScopes, user, "mail", cancellationToken);
        await CustomerAccess.EnsureSiteAsync(customerScopes, user, domain, cancellationToken);

        var dns = await mailService.GetDnsRecordsAsync(domain, cancellationToken);
        var applied = 0;
        foreach (var record in dns.Records.Where(item => !string.IsNullOrWhiteSpace(item.Value)))
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
            applied++;
        }

        await WriteAuditAsync(user, "mcp.mail.dns_apply", $"{domain}: {applied} kayıt", cancellationToken);
        return new { dns.Domain, AppliedRecords = applied };
    }

    /// <summary>
    /// Her MCP çağrısı denetim kaydına yazılır. Panelden yapılan işlemlerle aynı iz bırakır ki
    /// "bunu kim/ne yaptı" sorusu sonradan yanıtlanabilsin — yapay zeka üzerinden gelen değişiklik
    /// izsiz kalmamalı.
    /// </summary>
    private Task WriteAuditAsync(ClaimsPrincipal user, string action, string resource, CancellationToken cancellationToken)
    {
        var userId = Guid.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var parsed) ? parsed : (Guid?)null;

        // Kaynak adres denetim kaydına YAZILIR. Önce null geçiliyordu; oysa panelden yapılan her
        // işlemde bu alan dolu ve "bu değişikliği kim/nereden yaptı" sorusunun yanıtı buna bağlı.
        // MCP çağrısı yapay zeka sağlayıcısının sunucusundan gelir, yani adres kullanıcının kendi
        // adresi değildir — ama sızan bir anahtarın nereden kullanıldığını gösteren tek iz odur.
        var ipAddress = httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

        return auditService.WriteAsync(action, resource, $"MCP aracı çağrıldı: {action}", userId, ipAddress, cancellationToken);
    }
}
