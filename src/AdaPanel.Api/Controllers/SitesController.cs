using AdaPanel.Api.Extensions;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;

namespace AdaPanel.Api.Controllers;

// Reseller (Bayi) yalnız sahip olduğu müşterilerin sitelerini yönetir; her site-uçlu işlem
// CustomerAccess.EnsureSiteAsync ile etkin kapsama göre korunur. Site OLUŞTURMA/içe aktarma
// admin-only kalır (bayi atama/limit modeli kapsam dışı).
[ApiController]
[Authorize(Roles = "Administrator,Reseller,Customer")]
[RequireSubUserSection("sites")]
[Route("api/v1/sites")]
public sealed class SitesController(
    IIisSiteService iisSiteService,
    ISiteProvisioningService siteProvisioningService,
    ISiteDefaultsService siteDefaultsService,
    ISiteCascadeDeletionService siteCascadeDeletionService,
    ICustomerScopeService customerScopes,
    IDatabaseOwnershipService dbOwnership,
    ISiteDatabaseLinkService siteDatabaseLinks,
    IPanelPreferencesService panelPreferences,
    ISslService sslService,
    IAuditService auditService,
    IConcurrencyLock concurrencyLock,
    Microsoft.Extensions.Options.IOptions<AdaPanel.Infrastructure.Options.PanelRuntimeOptions> panelRuntimeOptions,
    ILogger<SitesController> logger) : ControllerBase
{
    /// <summary>
    /// YÖNETİCİNİN verdiği fiziksel yolu denetler; sistem alanıysa istek reddedilir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ÖLÇÜLEN AÇIK:</b> yol kısıtlaması yalnız <c>!User.IsInRole("Administrator")</c> dalında
    /// vardı; yönetici dalında HİÇ denetim yoktu. <c>PhysicalPath = C:\</c> olan bir site
    /// açıldığında dosya yöneticisinin sanal alanı (<c>FileService.ResolveSiteTargetAsync</c>
    /// sitenin kökünü kök kabul eder) ve <c>/files/acl</c> ucu SYSTEM olarak TÜM DİSKİ açıyordu —
    /// <c>%ProgramData%\AdaPanel\config\server-owner.json</c> dahil. Yani sahip olmayan bir
    /// yönetici sahipliği ele geçirip terminale (SYSTEM kabuğu) girebiliyordu.
    /// </para>
    /// <para>
    /// <b>FAZLA KAPATMA NÖBETÇİSİ:</b> yöneticinin site kökü dışına kurması YASAKLANMADI —
    /// <c>D:\siteler\musteri.com</c> gibi ayrı disk yolları meşrudur ve kabul edilir. Reddedilen
    /// şey yalnız sistem alanı ve ortak kapsayıcının KENDİSİDİR; kararın tek adresi
    /// <see cref="PanelWriteTargetGuard"/>'dır (silme yolundaki listeyle AYNI kaynak).
    /// </para>
    /// <para>
    /// <b>İki uçta birden uygulanır</b> (<c>POST /sites</c> ve <c>PUT /sites/{name}/document-root</c>):
    /// yalnız oluşturmayı kapatmak, aynı sonucu SONRADAN belge kökü değiştirerek veren bir yan kapı
    /// bırakırdı — bu asimetri kiracı dalında zaten bir kez ısırmıştı.
    /// </para>
    /// </remarks>
    private void EnsureAdminPathIsWritable(string? physicalPath)
    {
        // BOŞ YOL DENETLENMEZ: panel o durumda yolu KENDİ üretir (site varsayılanları), yani
        // kullanıcıdan gelen bir hedef yoktur. Boş yolu reddetmek, en olağan yönetici isteğini
        // (yolu panele bıraktığı isteği) kırardı.
        if (string.IsNullOrWhiteSpace(physicalPath))
        {
            return;
        }

        var veriKoku = Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(panelRuntimeOptions.Value.DataRoot));
        var karar = PanelWriteTargetGuard.Evaluate(physicalPath, veriKoku);
        if (karar != PanelWriteTargetVerdict.Allowed)
        {
            throw new AppException(
                "sites.physical_path_forbidden",
                400,
                400,
                $"{physicalPath}: {PanelWriteTargetGuard.Describe(karar)}");
        }
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<SiteDto>>>> GetSitesAsync(CancellationToken cancellationToken)
    {
        var sites = await iisSiteService.GetSitesAsync(cancellationToken);

        // Bayi yalnızca etkin kapsamındaki siteleri görür.
        var allowed = await CustomerAccess.GetEffectiveSitesAsync(customerScopes, User, cancellationToken);
        if (allowed is not null)
        {
            sites = sites
                .Where(site => allowed.Contains(site.Name, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }

        return Ok(ApiResponse<IReadOnlyCollection<SiteDto>>.Ok(sites));
    }

    // Domain yönlendirmeden siteyi test et (Plesk "Preview" gibi): nip.io tıklanır link (domainsiz çalışır) +
    // her durumda çalışan hosts satırı. nip.io için sunucu IP'sine bir IIS bağlaması eklenir (idempotent).
    [HttpGet("{siteName}/preview")]
    public async Task<ActionResult<ApiResponse<SitePreviewDto>>> GetPreviewAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var realHost = site.Bindings
            .Select(binding => binding.HostName)
            .FirstOrDefault(host => !string.IsNullOrWhiteSpace(host) && !host.EndsWith(".nip.io", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

        var preferences = await panelPreferences.GetAsync(cancellationToken);
        var serverAddress = (preferences.ServerAddress ?? string.Empty).Trim();

        var dto = new SitePreviewDto
        {
            RealHost = realHost,
            ServerAddress = serverAddress,
            HostsLine = string.IsNullOrWhiteSpace(realHost) || serverAddress.Length == 0 ? string.Empty : $"{serverAddress}\t{realHost}"
        };

        // Sunucu adresi IPv4 ise nip.io ile domainsiz tıklanır link üret (host DNS'siz sunucuya çözülür).
        if (System.Net.IPAddress.TryParse(serverAddress, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var previewHost = $"{BuildPreviewLabel(siteName)}.{serverAddress}.nip.io";
            dto.PreviewHost = previewHost;
            dto.Method = "nipio";

            // 1) HTTP bağlaması (host-header eşleşsin ki nip.io host'u bu siteye gelsin).
            if (!site.Bindings.Any(binding => string.Equals(binding.HostName, previewHost, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    await iisSiteService.AddBindingAsync(siteName, new SiteBindingRequest { Protocol = "http", HostName = previewHost, IpAddress = "*", Port = 80 }, cancellationToken);
                    dto.BindingAdded = true;
                }
                catch
                {
                    // Bağlama eklenemezse (zaten var / port çakışması) link yine de denenebilir.
                }
            }

            // 2) WordPress ise: mu-plugin ile WP'yi önizleme host'unda servis et (kendi asıl adresine yönlendirmesin).
            TryWriteWordPressPreviewPlugin(site);

            // 3) Önizleme host'una HIZLI self-signed HTTPS bağlaması (site HTTPS zorlarsa https resetlenmesin; ACME
            //    beklemesi YOK → buton donmaz). Zaten varsa tekrar üretme. Tarayıcı bir kez sertifika uyarısı gösterir.
            var hasHttps = site.Bindings.Any(binding =>
                string.Equals(binding.HostName, previewHost, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(binding.Protocol, "https", StringComparison.OrdinalIgnoreCase));
            var scheme = "http";
            if (hasHttps)
            {
                scheme = "https";
            }
            else
            {
                try
                {
                    await sslService.EnsurePreviewCertificateAsync(siteName, previewHost, cancellationToken);
                    scheme = "https";
                }
                catch
                {
                    // SSL bağlanamadıysa http linki döneriz (site https zorlamıyorsa çalışır).
                }
            }
            dto.Url = $"{scheme}://{previewHost}/";
        }
        else
        {
            // Sunucu adresi IP değil (makine adı) → nip.io kurulamaz; yalnız hosts yöntemi.
            dto.Method = "hosts";
            dto.Url = string.IsNullOrWhiteSpace(realHost) ? string.Empty : $"http://{realHost}/";
        }

        return Ok(ApiResponse<SitePreviewDto>.Ok(dto));
    }

    // Site adından geçerli bir DNS etiketi türetir (küçük harf, alnum+tire, tek tire, ≤40).
    private static string BuildPreviewLabel(string siteName)
    {
        var label = new string(siteName.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
        while (label.Contains("--"))
        {
            label = label.Replace("--", "-");
        }
        if (label.Length > 40)
        {
            label = label[..40].Trim('-');
        }
        return label.Length == 0 ? "site" : label;
    }

    // WordPress sitesiyse: nip.io önizleme host'unda WP'nin kendi (asıl) adresine yönlendirmesini engelleyen mu-plugin yazar
    // (option_siteurl/home'u istenen host'a çevirir). Böylece önizleme host'unda WP'nin tüm bağlantı/varlıkları çalışır.
    private static void TryWriteWordPressPreviewPlugin(SiteDto site)
    {
        try
        {
            var root = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
            if (string.IsNullOrWhiteSpace(root) || !System.IO.File.Exists(System.IO.Path.Combine(root, "wp-config.php")))
            {
                return;
            }

            var muDir = System.IO.Path.Combine(root, "wp-content", "mu-plugins");
            System.IO.Directory.CreateDirectory(muDir);
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(muDir, "adapanel-preview.php"),
@"<?php
/* AdaPanel Preview: nip.io onizleme host'unda WordPress'i kendi asil adresine yonlendirmeden servis eder. */
if (!defined('ABSPATH')) { exit; }
if (!empty($_SERVER['HTTP_HOST']) && preg_match('/\.nip\.io$/i', (string) $_SERVER['HTTP_HOST'])) {
    $scheme = (!empty($_SERVER['HTTPS']) && strtolower((string) $_SERVER['HTTPS']) !== 'off') ? 'https' : 'http';
    $base = $scheme . '://' . $_SERVER['HTTP_HOST'];
    add_filter('option_siteurl', static function () use ($base) { return $base; });
    add_filter('option_home', static function () use ($base) { return $base; });
}
");
        }
        catch
        {
            // mu-plugin yazılamazsa önizleme yine denenebilir (WP olmayan sitelerde zaten gerekmez).
        }
    }

    [HttpGet("{siteName}")]
    public async Task<ActionResult<ApiResponse<SiteDto>>> GetSiteAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        return Ok(ApiResponse<SiteDto>.Ok(site));
    }

    /// <summary>
    /// Sitenin veritabanı görünümü (Plesk gibi): BAĞLI olanlar (açık seçimle bağlanan + adı eşleşen) ve bağlanabilecek
    /// ADAYLAR (henüz bağlı olmayan mevcut veritabanları). Böylece kullanıcı isim tahminine bağlı kalmadan seçerek bağlar.
    /// </summary>
    [HttpGet("{siteName}/databases")]
    public async Task<ActionResult<ApiResponse<SiteDatabasesDto>>> GetSiteDatabasesAsync(
        string siteName,
        [FromServices] IDatabaseService databaseService,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);

        // Kurulu tüm motorlardaki veritabanlarını topla (motor erişilemezse sessizce atla).
        var all = new List<SiteDatabaseRefDto>();
        foreach (var engine in await databaseService.GetEnginesAsync(cancellationToken))
        {
            if (!engine.Installed)
            {
                continue;
            }

            try
            {
                var workspace = await databaseService.GetWorkspaceAsync(engine.Engine, cancellationToken);
                foreach (var database in workspace.Databases)
                {
                    all.Add(new SiteDatabaseRefDto { Engine = engine.Engine, Name = database.Name });
                }
            }
            catch
            {
                // motor erişilemiyor → atla
            }
        }

        // Bağlı = açık link VEYA ad eşleşmesi (site adı / DatabaseNameFromSite). Geri kalan mevcut DB'ler aday.
        var links = await siteDatabaseLinks.GetForSiteAsync(siteName, cancellationToken);
        var wantedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            siteName.Trim(),
            AdaPanel.Core.Common.ResourceNaming.DatabaseNameFromSite(siteName)
        };

        bool IsLinked(SiteDatabaseRefDto reference) =>
            links.Any(link =>
                string.Equals(link.Engine, reference.Engine, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(link.DatabaseName, reference.Name, StringComparison.OrdinalIgnoreCase)) ||
            wantedNames.Contains(reference.Name);

        // GÜVENLİK (sızıntı): aday listesi kullanıcının SAHİP OLDUĞU veritabanlarıyla sınırlıdır.
        // Bağlı olanlar filtrelenmez: bir site zaten bağlıysa kullanıcı onu görmeli, aksi hâlde
        // ekranında sebepsiz kaybolurdu.
        var baglanabilir = await ResolveLinkableDatabasesAsync(cancellationToken);
        var adaylar = all.Where(reference => !IsLinked(reference));
        if (baglanabilir is not null)
        {
            adaylar = adaylar.Where(reference => baglanabilir.Icerir(reference.Engine, reference.Name));
        }

        var result = new SiteDatabasesDto
        {
            Linked = all.Where(IsLinked).ToList(),
            Candidates = adaylar.ToList()
        };
        return Ok(ApiResponse<SiteDatabasesDto>.Ok(result));
    }

    /// <summary>Mevcut bir veritabanını siteye BAĞLA (seçerek; ad eşleşmesi gerekmez). Site silinince cascade gider.</summary>
    [HttpPost("{siteName}/databases/link")]
    public async Task<ActionResult<ApiResponse<object>>> LinkSiteDatabaseAsync(
        string siteName,
        [FromBody] LinkSiteDatabaseRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);

        // GÜVENLİK (çapraz-kiracı): site kapsamı doğrulanıyordu ama veritabanı adı SERBESTTİ.
        // Başkasının veritabanını kendi sitesine bağlayan kiracı, sitesini silince cascade o
        // veritabanını DROP ediyordu. Gerekçe: ResolveLinkableDatabasesAsync.
        var baglanabilir = await ResolveLinkableDatabasesAsync(cancellationToken);
        if (baglanabilir is not null && !baglanabilir.Icerir(request.Engine, request.Name))
        {
            return StatusCode(403, ApiResponse<object>.Fail("sites.database_forbidden", 403));
        }

        await siteDatabaseLinks.LinkAsync(request.Engine, request.Name, siteName, cancellationToken);
        await WriteAuditAsync("sites.database.linked", siteName, cancellationToken, ayrinti: $"{request.Engine}:{request.Name}");
        return Ok(ApiResponse<object>.Ok(new { }, "sites.database.linked"));
    }

    /// <summary>Bir veritabanının siteyle BAĞINI kaldırır (veritabanını SİLMEZ).</summary>
    [HttpDelete("{siteName}/databases/link")]
    public async Task<ActionResult<ApiResponse<object>>> UnlinkSiteDatabaseAsync(
        string siteName,
        [FromQuery] string engine,
        [FromQuery] string name,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);

        // GÜVENLİK (çapraz-kiracı): kaldırma da BAĞLAMA ile aynı sahiplik denetiminden geçer.
        // UnlinkAsync imzasında site adı YOK ve kaydı yalnız motor+ad eşleşmesiyle siliyor; yani
        // denetimsiz bırakılırsa kendi sitesinin adını yazan bir kiracı, BAŞKASININ site↔DB bağını
        // sessizce koparabilirdi. Sonuç kurbanda görünmez bir bozulma: veritabanı site panelinden
        // kaybolur ve site silinirken cascade ile temizlenmediği için yetim kalır.
        var baglanabilir = await ResolveLinkableDatabasesAsync(cancellationToken);
        if (baglanabilir is not null && !baglanabilir.Icerir(engine, name))
        {
            return StatusCode(403, ApiResponse<object>.Fail("sites.database_forbidden", 403));
        }

        await siteDatabaseLinks.UnlinkAsync(engine, name, cancellationToken);
        await WriteAuditAsync("sites.database.unlinked", siteName, cancellationToken, ayrinti: $"{engine}:{name}");
        return Ok(ApiResponse<object>.Ok(new { }, "sites.database.unlinked"));
    }

    // #1 ⭐ Belge kökü / web root: IIS fiziksel yolunu değiştir (Laravel/Symfony için ...\public). IIS'te KALICI
    // (panel sonraki düzenlemelerde proje köküne geri döndürmez). Yeni kökte index.php varsayılan belge yapılır.
    [HttpPut("{siteName}/document-root")]
    public async Task<ActionResult<ApiResponse<SiteDto>>> SetDocumentRootAsync(
        string siteName,
        [FromBody] UpdateDocumentRootRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);

        // GÜVENLİK (çapraz-kiracı): admin DIŞINDAKİ kullanıcılar belge kökünü sitenin KENDİ
        // klasörünün dışına taşıyamaz.
        //
        // Site OLUŞTURULURKEN yol zaten zorlanıyor (ResolveScopedPhysicalPath) — ama bu kontrol
        // olmadan aynı kısıtlama SONRADAN belge kökü değiştirilerek aşılabiliyordu: ön kapı
        // kapalı, yan kapı açıktı. Kiracı belge kökünü başka bir müşterinin klasörüne (ya da
        // C:\ gibi bir sistem yoluna) çevirip o içeriği web üzerinden okuyabilir, IIS'in o
        // klasöre yazma iznini kendi lehine kullanabilirdi.
        //
        // Sitenin kökünün KENDİSİ geçerlidir: "public"ten proje köküne geri dönmek meşru bir
        // işlemdir ve en olağan durumdur.
        //
        // SINIR SİTE DEĞİL KİRACIDIR — ve bu, kullanıcı bildirimiyle düzeltildi. Önceki hâl
        // yalnız DÜZENLENEN sitenin kökünü kabul ediyordu; oysa alt alan adı AYRI BİR SİTEDİR ve
        // en olağan kullanım tam da şudur: "webtv.ornek.com" alt alan adını ana sitedeki
        // "httpdocs/iptv/public" klasörüne bakacak şekilde ayarlamak (Plesk'in olağan davranışı).
        // Site sınırı bunu 403 ile reddediyordu — yani koruma, kiracıyı BAŞKASININ klasöründen
        // korurken KENDİ klasöründen de koruyordu.
        //
        // Yeni kural: yol, kiracının SAHİP OLDUĞU sitelerden HERHANGİ BİRİNİN kökünde olabilir.
        // Çapraz kiracı koruması aynen duruyor — küme kapsam servisinden geliyor, istekten değil
        // (bu depoda kapanmış hata sınıfı: "kapsam denetimi doğru ama denetlenen değeri kiracı
        // seçiyor"). Kapsamsız rol (operator) için davranış DEĞİŞMEDİ: küme null döner ve eski
        // tek-site kuralı uygulanır; bu düzeltmenin amacı kiracıya alan açmak, kapsamsız role
        // yeni yetki vermek değil.
        if (!User.IsInRole("Administrator"))
        {
            var mevcut = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
            var duzenlenenKok = string.IsNullOrWhiteSpace(mevcut.ApplicationRootPath)
                ? mevcut.PhysicalPath
                : mevcut.ApplicationRootPath;

            List<string>? kiracininKokleri = null;
            var kiracininSiteleri = await CustomerAccess.GetEffectiveSitesAsync(customerScopes, User, cancellationToken);
            if (kiracininSiteleri is not null)
            {
                // TEK çağrı: kiracının site sayısı kadar IIS okuması yapmak, olağan bir ayar
                // değişikliğini site sayısıyla birlikte yavaşlatırdı.
                var hepsi = await iisSiteService.GetSitesAsync(cancellationToken);
                kiracininKokleri = hepsi
                    .Where(site => kiracininSiteleri.Contains(site.Name, StringComparer.OrdinalIgnoreCase))
                    .Select(site => string.IsNullOrWhiteSpace(site.ApplicationRootPath)
                        ? site.PhysicalPath
                        : site.ApplicationRootPath)
                    .ToList();
            }

            if (!SiteDocumentRootScope.IsAllowed(request.PhysicalPath, duzenlenenKok, kiracininKokleri))
            {
                return StatusCode(403, ApiResponse<SiteDto>.Fail("sites.document_root_outside_site", 403));
            }
        }
        else
        {
            // YÖNETİCİ DALI DA DENETLENİR. Kiracı sınırı KİRACI kapsamıdır (SiteDocumentRootScope);
            // yöneticinin kapsamı yoktur, o yüzden ondan istenen şey kapsam değil SİSTEM ALANINA
            // dokunmamaktır. Aksi hâlde "belge kökü = C:\" ile dosya yöneticisi tüm diski açıyordu.
            EnsureAdminPathIsWritable(request.PhysicalPath);
        }

        var site = await siteProvisioningService.SetDocumentRootAsync(siteName, request.PhysicalPath, cancellationToken);
        await WriteAuditAsync("sites.document_root.updated", siteName, cancellationToken, ayrinti: site.PhysicalPath);
        return Ok(ApiResponse<SiteDto>.Ok(site, "sites.document_root.updated"));
    }

    // #2 Laravel/WordPress pretty-URL yönlendirmesi (index.php). URL Rewrite modülü yoksa 409 (site bozulmaz).
    [HttpPost("{siteName}/framework-rewrite")]
    public async Task<ActionResult<ApiResponse<SiteDto>>> EnableFrameworkRewriteAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var site = await siteProvisioningService.EnableFrameworkRewriteAsync(siteName, cancellationToken);
        await WriteAuditAsync("sites.framework_rewrite.enabled", siteName, cancellationToken);
        return Ok(ApiResponse<SiteDto>.Ok(site, "sites.framework_rewrite.enabled"));
    }

    // #5 izin onar — mevcut siteye yazma iznini yeniden uygula (app pool kök Modify).
    // --- Alan adı takma adları (ör. ornek.com + ornek.com.tr aynı siteye) ---

    [HttpGet("{siteName}/aliases")]
    public async Task<ActionResult<ApiResponse<SiteAliasWorkspaceDto>>> GetAliasesAsync(
        string siteName,
        [FromServices] ISiteAliasService aliasService,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        return Ok(ApiResponse<SiteAliasWorkspaceDto>.Ok(await aliasService.GetAliasesAsync(siteName, cancellationToken)));
    }

    [HttpPost("{siteName}/aliases")]
    public async Task<ActionResult<ApiResponse<SiteAliasWorkspaceDto>>> AddAliasAsync(
        string siteName,
        [FromBody] AddSiteAliasRequest request,
        [FromServices] ISiteAliasService aliasService,
        [FromServices] IMailService mailService,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);

        // GÜVENLİK (çapraz-kiracı): takma ad eklemek, bağlama eklemekle AYNI alan adı iddiasını
        // üretir — mail sahipliği bağlamalardan türetildiği için denetlenmezse bir kiracı
        // başkasının alan adını (ve postasını) bu yoldan devralabilirdi.
        await CustomerAccess.EnsureDomainClaimAsync(
            customerScopes, iisSiteService, mailService, User, request.HostName, siteName, logger, cancellationToken);

        var workspace = await aliasService.AddAliasAsync(siteName, request, cancellationToken);
        await WriteAuditAsync("sites.alias.added", siteName, cancellationToken, ayrinti: request.HostName);
        return Ok(ApiResponse<SiteAliasWorkspaceDto>.Ok(workspace, "alias.added"));
    }

    [HttpDelete("{siteName}/aliases")]
    public async Task<ActionResult<ApiResponse<SiteAliasWorkspaceDto>>> RemoveAliasAsync(
        string siteName,
        [FromQuery] string hostName,
        [FromServices] ISiteAliasService aliasService,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var workspace = await aliasService.RemoveAliasAsync(siteName, hostName, cancellationToken);
        await WriteAuditAsync("sites.alias.removed", siteName, cancellationToken, ayrinti: hostName);
        return Ok(ApiResponse<SiteAliasWorkspaceDto>.Ok(workspace, "alias.removed"));
    }

    /// <summary>
    /// Site kökündeki yazma iznini yeniden uygular ve SONUCU DİSKTEN OKUYARAK bildirir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uç eskiden koşulsuz <c>"sites.permissions.repaired"</c> döndürüyor ve denetim kaydına da
    /// bunu yazıyordu. Servis tarafı en iyi çaba olduğu için BAŞARISIZ DÖNMESİ imkânsızdı: hesap
    /// adı çözülemediğinde <c>icacls</c> hata 1332 ile hiçbir şey yapmıyor, kullanıcı yine de
    /// yeşil onay görüyordu.
    /// </para>
    /// <para>
    /// <b>KAYIT HER DURUMDA YAZILIR.</b> Başarısızlıkta istisna fırlatıp kaydı hiç yazmamak,
    /// yöneticinin denemenin YAPILDIĞINI bile göremediği ikinci bir körlük olurdu — "izinleri kim
    /// bozdu/kim onarmaya çalıştı" sorusunun başlangıç noktası kaybolurdu. Kayıt, yapılan iddiayı
    /// değil ÖLÇÜLEN DURUMU taşır.
    /// </para>
    /// </remarks>
    [HttpPost("{siteName}/repair-permissions")]
    public async Task<ActionResult<ApiResponse<SitePermissionRepairResultDto>>> RepairPermissionsAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var result = await siteProvisioningService.RepairPermissionsAsync(siteName, cancellationToken);

        var action = result.State switch
        {
            SitePermissionRepairResultDto.StateGranted => "sites.permissions.repaired",
            SitePermissionRepairResultDto.StateNotGranted => "sites.permissions.repair_failed",
            _ => "sites.permissions.repair_unverified"
        };

        await WriteAuditAsync(action, siteName, cancellationToken, ayrinti: result.Detail);
        return Ok(ApiResponse<SitePermissionRepairResultDto>.Ok(result, action));
    }

    // --- Korumalı dizinler (panel "Password-Protected Directories") ---

    [HttpGet("{siteName}/protected-dirs")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ProtectedDirectoryDto>>>> GetProtectedDirsAsync(
        string siteName,
        [FromServices] IDirectoryProtectionService service,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var items = await service.GetProtectedDirectoriesAsync(siteName, cancellationToken);
        return Ok(ApiResponse<IReadOnlyCollection<ProtectedDirectoryDto>>.Ok(items));
    }

    [HttpPost("{siteName}/protected-dirs")]
    public async Task<ActionResult<ApiResponse<ProtectedDirectoryDto>>> ProtectDirAsync(
        string siteName,
        [FromBody] ProtectDirectoryRequest request,
        [FromServices] IDirectoryProtectionService service,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var dto = await service.ProtectAsync(siteName, request, cancellationToken);
        await WriteAuditAsync("sites.protected_dir.added", siteName, cancellationToken, ayrinti: dto.RelativePath);
        return Ok(ApiResponse<ProtectedDirectoryDto>.Ok(dto, "protected.created"));
    }

    [HttpDelete("{siteName}/protected-dirs")]
    public async Task<ActionResult<ApiResponse<object>>> UnprotectDirAsync(
        string siteName,
        [FromQuery] string relativePath,
        [FromServices] IDirectoryProtectionService service,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        await service.UnprotectAsync(siteName, relativePath, cancellationToken);
        await WriteAuditAsync("sites.protected_dir.removed", siteName, cancellationToken, ayrinti: relativePath);
        return Ok(ApiResponse<object>.Ok(new { }, "protected.removed"));
    }

    /// <summary>
    /// profesyonel panel benzeri birleşik oluşturma: siteyi kurar ve istenirse aynı akışta veritabanı,
    /// mail domaini ve FTP hesabını da best-effort oluşturur (biri kurulamazsa site yine kalır).
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Administrator,Reseller,Customer")]
    public async Task<ActionResult<ApiResponse<SiteCreationResultDto>>> CreateSiteAsync(
        [FromBody] CreateSiteRequest request,
        [FromServices] IDatabaseService databaseService,
        [FromServices] IMailService mailService,
        [FromServices] IFtpService ftpService,
        [FromServices] IDnsService dnsService,
        [FromServices] IComponentBootstrapper componentBootstrapper,
        CancellationToken cancellationToken)
    {
        // GÜVENLİK (internet/güvenilmez kiracı): admin DIŞINDAKİ kullanıcılar (bayi/müşteri) fiziksel yolu SEÇEMEZ —
        // panel yolu DAİMA varsayılan site köküne (kiracı-izole) zorlar. Aksi halde kötü-niyetli istek başka sitenin
        // klasörüne işaret edip okuyabilir/ACL'ini bozabilirdi (çapraz-kiracı). Admin tam serbest (mevcut davranış).
        if (!User.IsInRole("Administrator"))
        {
            var prefs = await panelPreferences.GetAsync(cancellationToken);
            request.PhysicalPath = ResolveScopedPhysicalPath(request, prefs.DefaultSiteRoot);

            // GÜVENLİK (çapraz-kiracı): uygulama havuzu da SEÇİLEMEZ.
            //
            // Yol zorlanıyor ama havuz serbest kalırsa kiracı, sitesini BAŞKA bir müşterinin
            // havuzunda çalıştırabilir. Site o havuzun kimliğiyle koşar; o kimliğe verilmiş
            // klasör izinleri (diğer müşterinin dosyaları) sitenin PHP koduna açılır — yani
            // yol kısıtlaması hiç yokmuş gibi olur.
            //
            // Boş bırakılınca panel yönetilen adı kendisi üretir (BuildManagedPoolName).
            request.ApplicationPoolName = string.Empty;

            // Host adı başkasının olamaz — gerekçe CustomerAccess.EnsureDomainClaimAsync'te.
            await CustomerAccess.EnsureDomainClaimAsync(
                customerScopes, iisSiteService, mailService, User, request.HostName, null, logger, cancellationToken);

            // SİTE ADI DA BİR SAHİPLİK İDDİASIDIR — ve denetlenmiyordu.
            //
            // Panel bugüne kadar sahiplik sorusunu yalnız HOST ADI için soruyordu. Oysa site adının
            // kendisi de tam olarak aynı ağırlıkta bir iddia:
            //   * CustomerScopeService.GetAllowedMailDomainsAsync atanan site ADINI izinli mail
            //     domaini sayar → "kurban.com" adlı bir site açan kiracı, kurbanın posta kutularını
            //     listeler, PAROLALARINI DEĞİŞTİRİR, alias ile postasını kendine yönlendirir;
            //   * DnsService varsayılan bölge adını site adından türetir → DNS bölgesi de devralınır;
            //   * CustomerAccess.EnsureDomainClaimAsync zaten BAŞKA sitelerin ADINI bir sahiplik
            //     kaynağı sayıyordu — yani panel bu adı "iddia" kabul ediyor ama iddia edilirken
            //     hiç sormuyordu. Asimetri açığın ta kendisiydi.
            //
            // Aynı fonksiyon kullanılıyor, ikinci bir kural yazılmıyor: kontrol ile koruduğu şey
            // aynı kuraldan gelmezse arada sessiz bir aralık kalır.
            await CustomerAccess.EnsureDomainClaimAsync(
                customerScopes, iisSiteService, mailService, User, request.Name, null, logger, cancellationToken);

            // ...ve ad boşta olsa bile ÜZERİNE YAZILAMAZ.
            //
            // EnsureDomainClaimAsync yalnız "başkasının" adını reddeder ve GetSitesAsync sistem
            // sitelerini listeden düşürür. Oysa IIS'te var olan bir siteyi kaldırmak yıkıcı bir
            // iştir; kiracıya ancak KENDİ sitesi için verilebilir.
            await CustomerAccess.EnsureSiteNameIsClaimableAsync(
                customerScopes, iisSiteService, User, request.Name, cancellationToken);
        }
        else
        {
            // YÖNETİCİ DALI ARTIK SERBEST DEĞİL. Yol hâlâ site kökü dışında olabilir
            // (D:\siteler\...) — reddedilen yalnız sistem alanıdır; gerekçe
            // EnsureAdminPathIsWritable'da.
            EnsureAdminPathIsWritable(request.PhysicalPath);
        }

        // KOTA KONTROLÜ İLE KAYIT ARASINDA BAŞKA İSTEK GİREMEZ.
        //
        // Kontrol (oku) ile atama (yaz) arasında uzun süren IIS kurulumu var ve arada hiçbir kilit
        // yoktu: MaxSites=1 olan bir müşteri iki isteği aynı anda gönderdiğinde İKİSİ DE kotayı boş
        // görüyor ve iki site birden açılıyordu. Kotanın kendisi doğru sayıyordu; sorun sayımın
        // ne zaman yapıldığındaydı.
        //
        // Kuyruk ABONELİK BAŞINA: farklı müşterilerin site kurulumları birbirini beklemez. Kilit
        // sırası da tutarlı (önce kota, sonra IIS) — IIS kuyruğunu bekleyen bir istek asla kota
        // kuyruğunu tutmuyor, dolayısıyla kilitlenme oluşmaz.
        var aboneligim = User.IsInRole("Administrator")
            ? Guid.Empty
            : await CustomerAccess.GetSubscriptionUserIdAsync(customerScopes, User, cancellationToken);

        var site = await concurrencyLock.RunExclusiveAsync(
            $"site-quota:{aboneligim}",
            async () =>
            {
                // M1 (profesyonel paneller gibi): Admin sınırsız; Bayi yalnız planının MaxSites kotası kadar site açabilir.
                await EnsureSiteQuotaAsync(cancellationToken);

                // AYNI ADDA SITE VARSA ACIK ONAY SART -- canlida olculen tehlike.
                //
                // WebAdministrationSiteService.CreateSiteAsync ayni adli bir site bulunca onu
                // KALDIRIP yeniden olusturur. Davranis bilincliydi (onceki kurulumdan kalan artik
                // siteyi devralmak) ama hicbir onay istemiyordu ve YONETICI, yukaridaki
                // EnsureSiteNameIsClaimableAsync denetiminin tamamen disindaydi -- o kontrol
                // `if (!User.IsInRole("Administrator"))` blogunun icinde.
                //
                // Sonuc: "Olustur ve yayinla" dugmesine kazayla ikinci kez basmak, az once kurulan
                // siteyi SILIP yeniden kuruyor ve yan kaynaklari (veritabani, FTP kullanicisi, posta
                // alani) yeniden sagliyordu. Kullanici bunu canlida bildirdi.
                //
                // KONTROL KILIDIN ICINDE: disarida olsaydi iki es zamanli istek arasinda kontrol ile
                // olusturma arasi bir pencere kalirdi (TOCTOU).
                //
                // FAIL-OPEN degil, FAIL-SAFE degil -- OLCULEBILIR: site listesi okunamazsa istisna
                // yukari cikar ve olusturma hic baslamaz. "Okuyamadim" ile "ad bos" ayni sayilmaz.
                if (!request.ReplaceExisting)
                {
                    var mevcutlar = await iisSiteService.GetSitesAsync(cancellationToken);
                    var istenenAd = (request.Name ?? string.Empty).Trim();
                    if (mevcutlar.Any(mevcut => string.Equals(mevcut.Name, istenenAd, StringComparison.OrdinalIgnoreCase)))
                    {
                        // 409: istek gecerli, DURUM cakisiyor. Ayrintiya ad yazilir ki panel
                        // kullaniciya HANGI sitenin degistirilecegini sorabilsin.
                        throw new AppException("sites.name_exists", 409, 409, istenenAd);
                    }
                }

                var olusan = await siteProvisioningService.CreateAndProvisionAsync(
                    request,
                    User.GetRequiredUserId(),
                    HttpContext.Connection.RemoteIpAddress?.ToString(),
                    cancellationToken);

                // M1: admin değilse yeni siteyi oluşturanın kapsamına EKLE (erişebilsin + kota sayımı doğru olsun).
                await AssignSiteToActorAsync(olusan.Name, cancellationToken);
                return olusan;
            },
            cancellationToken);
        await WriteAuditAsync("sites.create.completed", site.Name, cancellationToken);

        var result = new SiteCreationResultDto { Site = site };

        // ALT ALAN ADI: yeni site başka bir yönetilen sitenin ALTINDAysa, üst bölgeye A kaydı düşer.
        //
        // Bu adım eksikti: alt alan adı sitesi kuruluyor, IIS bağlaması hazır oluyor, ama alan adı
        // hiçbir yere çözmüyordu. Kullanıcı "site açtım, açılmıyor" diyor ve kaydı elle aramak
        // zorunda kalıyordu. Kaydı panel atmalı — çünkü üst alan adının bölgesi de panelde.
        //
        // BEST-EFFORT: burada bir sorun çıkarsa site yine durur, sadece uyarı yazılır. Site
        // oluşturmayı DNS'e bağlamak, çalışan bir akışı DNS'in durumuna teslim etmek olurdu.
        await TryEnsureSubdomainRecordAsync(result, site, request.HostName, dnsService, cancellationToken);
        var installedComponents = (await componentBootstrapper.GetCatalogAsync(cancellationToken))
            .Installed
            .Where(component => string.Equals(component.Status, "Installed", StringComparison.OrdinalIgnoreCase))
            .Select(component => component.ComponentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var databaseEngine = ResolveDatabaseEngine(request.DatabaseEngine, installedComponents);
        // Veritabanı YALNIZ kullanıcı açıkça istediğinde oluşturulur (ProvisionDefaultServices
        // artık DB'yi zorlamaz); ismi/kullanıcıyı/parolayı kullanıcı belirleyebilir.
        var shouldCreateDatabase = request.CreateDatabase;
        var shouldCreateMailDomain = request.CreateMailDomain ||
            (request.ProvisionDefaultServices && installedComponents.Contains("stalwart-mail"));
        var shouldCreateFtpUser = request.CreateFtpUser || request.ProvisionDefaultServices;

        // M1 (panel MaxDatabases): DB kotası doluysa DB oluşturmayı atla (site yine oluşur). Admin sınırsız.
        if (shouldCreateDatabase && !string.IsNullOrWhiteSpace(databaseEngine) && await IsDatabaseQuotaReachedAsync(cancellationToken))
        {
            shouldCreateDatabase = false;
            result.Warnings.Add("database: quota reached (max databases in your plan).");
        }

        if (shouldCreateDatabase && !string.IsNullOrWhiteSpace(databaseEngine))
        {
            var dbName = string.IsNullOrWhiteSpace(request.DatabaseName)
                ? AdaPanel.Core.Common.ResourceNaming.DatabaseNameFromSite(site.Name)
                : request.DatabaseName.Trim();
            var databaseUser = string.IsNullOrWhiteSpace(request.DatabaseUserName)
                ? BuildAccountName(site.Name, "db")
                : request.DatabaseUserName.Trim();
            var databasePassword = string.IsNullOrWhiteSpace(request.DatabasePassword)
                ? GeneratePassword()
                : request.DatabasePassword;
            try
            {
                await databaseService.CreateDatabaseAsync(
                    new Core.Dtos.Databases.CreateDatabaseRequest
                    {
                        Engine = databaseEngine,
                        Name = dbName,
                        UserName = databaseUser,
                        Password = databasePassword
                    },
                    cancellationToken);
                result.Created.Add($"{databaseEngine}:{dbName}");
                result.GeneratedCredentials.Add(new GeneratedCredentialDto
                {
                    Service = $"database:{databaseEngine}:{dbName}",
                    UserName = databaseUser,
                    Password = databasePassword
                });
                // M1: yeni DB'yi oluşturan müşteriye + siteye ait kaydet (kota sayımı + sahiplik).
                if (!User.IsInRole("Administrator"))
                {
                    await dbOwnership.RecordAsync(
                        databaseEngine,
                        dbName,
                        await CustomerAccess.GetSubscriptionUserIdAsync(customerScopes, User, cancellationToken),
                        site.Name,
                        cancellationToken);
                }
            }
            catch (Exception exception)
            {
                result.Warnings.Add($"database ({databaseEngine}:{dbName}): {DescribeProvisionError(exception)}");
            }
        }

        if (shouldCreateMailDomain)
        {
            var domain = string.IsNullOrWhiteSpace(request.HostName) ? site.Name : request.HostName.Trim();
            try
            {
                await mailService.CreateDomainAsync(
                    new Core.Dtos.Mail.CreateMailDomainRequest { DomainName = domain },
                    cancellationToken);
                result.Created.Add($"mail: {domain}");

                // profesyonel paneller gibi: mail domaini sitenin DNS bölgesine MX/SPF/DKIM/DMARC kayıtlarını
                // otomatik yazsın (mail ekranındaki davranışla tutarlı). Best-effort.
                try
                {
                    var mailDns = await mailService.GetDnsRecordsAsync(domain, cancellationToken);
                    foreach (var record in mailDns.Records.Where(r => !string.IsNullOrWhiteSpace(r.Value)))
                    {
                        await dnsService.UpsertRecordAsync(domain, new Core.Dtos.Sites.UpsertDnsRecordRequest
                        {
                            Type = record.Type,
                            Host = record.Host,
                            Value = record.Value,
                            Priority = record.Priority,
                            Ttl = 3600
                        }, cancellationToken);
                    }
                }
                catch
                {
                    // DNS otomatik uygulanamadıysa kullanıcı mail ekranından uygular.
                }
            }
            catch (Exception exception)
            {
                result.Warnings.Add($"mail ({domain}): {DescribeProvisionError(exception)}");
            }
        }

        if (shouldCreateFtpUser)
        {
            var ftpUserName = string.IsNullOrWhiteSpace(request.FtpUserName)
                ? BuildAccountName(site.Name, "ftp", maxTotalLength: 20)
                : request.FtpUserName.Trim();
            var ftpPassword = string.IsNullOrWhiteSpace(request.FtpPassword)
                ? GeneratePassword()
                : request.FtpPassword;
            try
            {
                await ftpService.CreateFtpUserAsync(
                    new Core.Dtos.Ftp.CreateFtpUserRequest
                    {
                        UserName = ftpUserName,
                        Password = ftpPassword,
                        SiteName = site.Name,
                        HomePath = string.Empty
                    },
                    cancellationToken);
                result.Created.Add($"ftp: {ftpUserName}");
                result.GeneratedCredentials.Add(new GeneratedCredentialDto
                {
                    Service = "ftp",
                    UserName = ftpUserName,
                    Password = ftpPassword
                });
            }
            catch (Exception exception)
            {
                result.Warnings.Add($"ftp ({ftpUserName}): {DescribeProvisionError(exception)}");
            }
        }

        return Ok(ApiResponse<SiteCreationResultDto>.Ok(result, "sites.create.success"));
    }

    // Alt-kaynak (DB/mail/FTP) provisyon hatasını okunur özetler: AppException ise GERÇEK sebebi (Details =
    // altyapı/CLI çıktısı, tek satıra indirilip kısaltılmış) anahtarla birlikte gösterir → kullanıcı "ham anahtar"
    // yerine asıl nedeni görür. AppException değilse düz mesaj.
    private static string DescribeProvisionError(Exception exception)
    {
        if (exception is AdaPanel.Core.Common.AppException app && !string.IsNullOrWhiteSpace(app.Details))
        {
            var detail = app.Details.Trim().ReplaceLineEndings(" ");
            if (detail.Length > 300)
            {
                detail = detail[..300] + "…";
            }

            return $"{app.MessageKey} — {detail}";
        }

        return exception.Message;
    }

    /// <summary>Silmeden önce siteye bağlı tüm kaynakların (profesyonel panel benzeri) önizlemesi.</summary>
    [HttpGet("{siteName}/deletion-plan")]
    public async Task<ActionResult<ApiResponse<SiteDeletionPlanDto>>> GetDeletionPlanAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var plan = await siteCascadeDeletionService.GetPlanAsync(siteName, cancellationToken);
        return Ok(ApiResponse<SiteDeletionPlanDto>.Ok(plan));
    }

    /// <summary>
    /// Siteyi ve ona bağlı tüm kaynakları (mail domaini, veritabanı, FTP, DNS, proxy, kota,
    /// git, müşteri atamaları ve isteğe bağlı dosyalar) profesyonel paneller gibi cascade siler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// YIKICI PARAMETRELERİN VARSAYILANI <c>false</c>: veritabanı düşürmek ve site dosyalarını
    /// silmek geri alınamaz işlerdir; "istemedim" diyen bir çağrı bunları yapmamalıdır. Eskiden
    /// ikisi de <c>true</c> idi, yani parametreyi hiç göndermeyen bir istemci (kendi betiği, MCP,
    /// API anahtarıyla otomasyon) siteyle birlikte veritabanını ve dosyaları da sessizce yok
    /// ediyordu. Panelin kendi arayüzü iki parametreyi de HER ZAMAN açıkça gönderiyor
    /// (frontend/src/api/sites.ts), dolayısıyla bu değişiklik arayüzün davranışını değiştirmez.
    /// </para>
    /// </remarks>
    [HttpDelete("{siteName}")]
    public async Task<ActionResult<ApiResponse<SiteDeletionResultDto>>> DeleteSiteAsync(
        string siteName,
        CancellationToken cancellationToken,
        [FromQuery] bool deleteFiles = false,
        [FromQuery] bool deleteDatabases = false)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);

        // KAYDIN KONUSU SİLMEDEN ÖNCE ÇÖZÜLÜR. Cascade silme, siteyi bütün müşteri kapsamlarından
        // da düşürür (RemoveSiteFromAllScopesAsync); sonrasında sorulsaydı sahip DAİMA boş dönerdi
        // ve panelin en yıkıcı işlemi, tam da sahibinin göremeyeceği tek kayıt olurdu.
        var sahip = await AuditSubject.SiteOwnerAsync(customerScopes, siteName, cancellationToken);

        var result = await siteCascadeDeletionService.DeleteAsync(siteName, deleteFiles, deleteDatabases, cancellationToken);

        // SAHİPLİK KAYDI BURADA SİLİNMİYOR — bilerek (denetim Y1).
        //
        // Eskiden burada KOŞULSUZ `dbOwnership.RemoveBySiteAsync(siteName)` vardı: `deleteDatabases`
        // varsayılanı `false` olmasına rağmen kayıt her hâlükârda gidiyordu. Sonuç: veritabanı
        // motorda duruyor, ama panelde SAHİPSİZ. Sahipsiz bir veritabanına panelden sahip atayan
        // hiçbir uç yok (DatabasesController mevcut sahip yoksa sessizce atlar) — müşteri onu bir
        // daha göremiyor, yedekleyemiyor, bağlayamıyordu; kotasından ise düşmeye devam ediyordu.
        // "Veritabanımı koru" seçeneği, veritabanını erişilemez hâle getiren bir tuzaktı.
        //
        // Karar artık GERÇEĞİ bilen yerde: cascade servisi yalnızca DROP'u başarılı olan
        // veritabanının kaydını siler, hayatta kalanların sahipliğini KORUYUP ölü site adını koparır.
        await WriteAuditAsync("sites.delete.completed", siteName, cancellationToken, sahip);
        return Ok(ApiResponse<SiteDeletionResultDto>.Ok(result, "sites.delete.success"));
    }

    [HttpPost("{siteName}/start")]
    public async Task<ActionResult<ApiResponse<SiteDto>>> StartSiteAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var site = await iisSiteService.StartSiteAsync(siteName, cancellationToken);
        await WriteAuditAsync("sites.start.completed", site.Name, cancellationToken);
        return Ok(ApiResponse<SiteDto>.Ok(site, "sites.start.success"));
    }

    [HttpPost("{siteName}/stop")]
    public async Task<ActionResult<ApiResponse<SiteDto>>> StopSiteAsync(string siteName, CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var site = await iisSiteService.StopSiteAsync(siteName, cancellationToken);
        await WriteAuditAsync("sites.stop.completed", site.Name, cancellationToken);
        return Ok(ApiResponse<SiteDto>.Ok(site, "sites.stop.success"));
    }

    [HttpPost("{siteName}/bindings")]
    public async Task<ActionResult<ApiResponse<SiteDto>>> AddBindingAsync(
        string siteName,
        [FromBody] SiteBindingRequest request,
        [FromServices] IMailService mailService,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        await CustomerAccess.EnsureDomainClaimAsync(
            customerScopes, iisSiteService, mailService, User, request.HostName, siteName, logger, cancellationToken);
        var site = await iisSiteService.AddBindingAsync(siteName, request, cancellationToken);
        await WriteAuditAsync("sites.binding.added", site.Name, cancellationToken);
        return Ok(ApiResponse<SiteDto>.Ok(site, "sites.binding.add.success"));
    }

    [HttpDelete("{siteName}/bindings")]
    public async Task<ActionResult<ApiResponse<SiteDto>>> RemoveBindingAsync(
        string siteName,
        [FromQuery] RemoveSiteBindingRequest request,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var site = await iisSiteService.RemoveBindingAsync(siteName, request, cancellationToken);
        await WriteAuditAsync("sites.binding.removed", site.Name, cancellationToken);
        return Ok(ApiResponse<SiteDto>.Ok(site, "sites.binding.remove.success"));
    }

    [HttpPost("import")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<SiteDto>>>> ImportExistingSitesAsync(CancellationToken cancellationToken)
    {
        var sites = await iisSiteService.ImportExistingSitesAsync(cancellationToken);

        // SONUC ATILMIYOR ARTIK.
        //
        // OLCULEN ARIZA: bu dongu `_ =` ile sonucu ISKARTAYA CIKARIYORDU ve kayit kosulsuz
        // "... imported and defaults applied" yaziyordu. Oysa SiteDefaultsService BASARISIZ
        // ADIMLARI ZATEN ADIYLA donduruyor (FailedFeatures) -- servis dogruyu soyluyor, cagiran
        // taraf duymuyordu.
        //
        // Atilan sey onemsiz degil: "dns" (bolge hic yayinlanmadi), "security" (kiraci yalitimi
        // ve klasor ACL'i uygulanmadi -- yani devralinan site komsusunun wp-config.php'sini
        // okuyabilir), "ssl", "ftp", "aspnet", "runtime", "proxy". Bu uc IIS'te ELDE kurulmus
        // siteleri panelin yonetimine alir; yonetici "devraldim, varsayilanlar uygulandi" kaydina
        // bakip yalitimin yerinde oldugunu SANIR.
        //
        // KARDES YOL ZATEN DOGRUYU SOYLUYOR: tek siteye varsayilan uygulayan uc (asagida)
        // `sites.defaults.partial` donuyor. Ayni bilgi burada da kaybolmamali.
        // Kayit cumlesi TEK KAYNAKTAN gelir (SiteImportAudit): basarisiz adimlar site adi VE adim
        // adiyla yazilir -- "3 sitede eksik" demek yoneticiye HANGI sitede NEYIN uygulanmadigini
        // soylemez, kayit okunabilir ama ise yaramaz olurdu.
        var sonuclar = new List<SiteImportAudit.Outcome>(sites.Count);
        foreach (var site in sites)
        {
            var varsayilanlar = await siteDefaultsService.EnsureDefaultsAsync(site, cancellationToken);
            sonuclar.Add(new SiteImportAudit.Outcome(site.Name, varsayilanlar.FailedFeatures));
        }

        // İZ YOKTU. Bu uç IIS'te ELDE kurulmuş siteleri panelin yönetimine alır ve her birine
        // varsayılanları (uygulama havuzu, izinler, yeniden yazma kuralları) UYGULAR — yani
        // dokunduğu her site sunucuda değişir. Kaç sitenin ve HANGİLERİNİN devralındığı
        // yazılmazsa, sonradan "bu sitenin ayarlarını ne değiştirdi" sorusunun başlangıç noktası
        // bile kalmaz.
        //
        // KİRACI BAĞI YOK ve olamaz: içe aktarılan siteler tanım gereği HENÜZ hiçbir aboneliğe
        // atanmamıştır (atama ayrı bir yönetici işidir), yani bu kaydın bir konusu yoktur.
        // Sunucu geneli bir yönetici işlemi olarak kalır.
        await auditService.WriteAsync(
            "sites.import.completed",
            "sites",
            SiteImportAudit.Describe(sonuclar),
            User.GetRequiredUserId(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return Ok(ApiResponse<IReadOnlyCollection<SiteDto>>.Ok(sites, "sites.import.success"));
    }

    [HttpPost("{siteName}/defaults")]
    public async Task<ActionResult<ApiResponse<SiteDefaultsResultDto>>> EnsureDefaultsAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        await CustomerAccess.EnsureSiteAsync(customerScopes, User, siteName, cancellationToken);
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var result = await siteDefaultsService.EnsureDefaultsAsync(site, cancellationToken);
        await WriteAuditAsync("sites.defaults.completed", site.Name, cancellationToken);
        return Ok(ApiResponse<SiteDefaultsResultDto>.Ok(
            result,
            result.IsComplete ? "sites.defaults.success" : "sites.defaults.partial"));
    }

    // Admin olmayan site oluşturmada fiziksel yolu DAİMA varsayılan site kökünün altında, güvenli bir klasör adına
    // sabitler (istemcinin gönderdiği yol yok sayılır). Geçersiz/escape eden ad → rastgele güvenli klasör. Çapraz-kiracı engeli.
    private static string ResolveScopedPhysicalPath(CreateSiteRequest request, string defaultSiteRoot)
    {
        var baseName = string.IsNullOrWhiteSpace(request.Name) ? request.HostName : request.Name;
        var safe = new string((baseName ?? string.Empty).Trim()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-')
            .ToArray())
            .Trim('.', '-', '_');
        if (string.IsNullOrWhiteSpace(safe) || safe.Contains("..", StringComparison.Ordinal))
        {
            safe = "site-" + Guid.NewGuid().ToString("N")[..8];
        }

        var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(
            string.IsNullOrWhiteSpace(defaultSiteRoot) ? "C:\\inetpub\\wwwroot" : defaultSiteRoot));
        var full = Path.GetFullPath(Path.Combine(root, safe));

        // Containment: sonuç kökün ALTINDA değilse (escape) rastgele güvenli klasöre düş.
        var rootPrefix = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            full = Path.Combine(root, "site-" + Guid.NewGuid().ToString("N")[..8]);
        }

        return full;
    }

    // M1: Bayi/Müşteri site kotası. Admin sınırsız. MaxSites=0 → limitsiz (mevcut davranışı korur).
    private async Task EnsureSiteQuotaAsync(CancellationToken cancellationToken)
    {
        if (User.IsInRole("Administrator"))
        {
            return;
        }

        var scope = await customerScopes.GetScopeAsync(User.GetRequiredUserId(), cancellationToken);
        if (scope.MaxSites > 0 && scope.Sites.Count >= scope.MaxSites)
        {
            throw new Core.Common.AppException("sites.quota_exceeded", 403, 403);
        }
    }

    // M1 (panel MaxDatabases): admin olmayan kullanıcının sahip olduğu DB sayısı kotayı doldurdu mu?
    private async Task<bool> IsDatabaseQuotaReachedAsync(CancellationToken cancellationToken)
    {
        if (User.IsInRole("Administrator"))
        {
            return false;
        }

        var userId = User.GetRequiredUserId();
        var scope = await customerScopes.GetScopeAsync(userId, cancellationToken);
        if (scope.MaxDatabases <= 0)
        {
            return false; // 0 = limitsiz
        }

        // Alt-kullanıcıda sayım abonelik (parent) kimliğiyle yapılır → kota abonelik genelinde tektir.
        var subscriptionId = await CustomerAccess.GetSubscriptionUserIdAsync(customerScopes, User, cancellationToken);
        return await dbOwnership.CountForOwnerAsync(subscriptionId, cancellationToken) >= scope.MaxDatabases;
    }

    /// <summary>
    /// Bu kullanıcının BAĞLAYABİLECEĞİ/GÖREBİLECEĞİ veritabanları. <c>null</c> = kısıtsız (yönetici).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kural artık <see cref="CustomerAccess.ResolveDatabaseScopeAsync"/> içinde, kapsam
    /// denetimlerinin TEK adresinde. Burada duruyordu ve doğruydu; ama yalnız burada bilindiği
    /// için veritabanı adını kiracıdan alan İKİNCİ uç (site yedeği) hiç denetlenmeden kalmıştı.
    /// Bu metot yalnızca çağrı yerlerini kısaltan bir sarmalayıcıdır.
    /// </para>
    /// <para>
    /// Kapattığı iki açık (gerekçe orada yazılı): aday listesi sızıntısı ve başkasının
    /// veritabanını kendi sitesine bağlayıp cascade ile DROP ettirme.
    /// </para>
    /// </remarks>
    private Task<TenantDatabaseScope?> ResolveLinkableDatabasesAsync(CancellationToken cancellationToken) =>
        CustomerAccess.ResolveDatabaseScopeAsync(customerScopes, dbOwnership, User, cancellationToken);

    // M1: admin olmayan oluşturucunun yeni sitesini kendi kapsamına ekler (yoksa). OwnerId korunur (SetScopeAsync).
    private async Task AssignSiteToActorAsync(string siteName, CancellationToken cancellationToken)
    {
        if (User.IsInRole("Administrator"))
        {
            return;
        }

        // Alt-kullanıcıda site PARENT'ın kapsamına yazılır (alt-kullanıcının kendi kaydına yazılırsa
        // ResolveEffective parent'ı döndürdüğünden site görünmez kalır + kayıt tutarsızlaşır).
        var userId = await CustomerAccess.GetSubscriptionUserIdAsync(customerScopes, User, cancellationToken);
        var scope = await customerScopes.GetScopeAsync(userId, cancellationToken);
        if (scope.Sites.Contains(siteName, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var sites = scope.Sites.ToList();
        sites.Add(siteName);
        await customerScopes.SetScopeAsync(userId, sites, scope.MaxSites, scope.MaxDiskMb, cancellationToken);
    }

    /// <summary>
    /// Site işlemlerinin ORTAK denetim izi.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Kiracı bağı (bu turda kapatıldı):</b> kayıt <c>WriteAsync</c> ile yazılıyordu, yani
    /// eylemin KONUSU boştu. Bir yönetici kiracının sitesini durdurduğunda ya da SİLDİĞİNDE kaydın
    /// <c>UserId</c>'si yöneticidir; kiracının izinli kimlik kümesinde bulunmaz. Konu da boş olunca
    /// kayıt kiracının denetim ekranında HİÇ çıkmıyordu — "sitem neden durdu / kim sildi" sorusu
    /// panelin en yıkıcı uçlarında cevapsız kalıyordu.
    /// </para>
    /// <para>
    /// <b>Kaynak artık HEDEFİ söylüyor:</b> bütün site kayıtları <c>"sites"</c> yazıyordu; hangi
    /// siteye ait olduğu yalnızca serbest metin ayrıntının içindeydi ve kaynağa göre süzülemiyordu.
    /// </para>
    /// </remarks>
    /// <param name="sahip">
    /// Sahip çağrı yerinde ÖNCEDEN çözülmüşse (site silme) yeniden sorulmasın diye: silmeden sonra
    /// site hiçbir kapsamda kalmaz ve sahibi bir daha okunamaz.
    /// </param>
    /// <param name="ayrinti">İşlemin hedefine dair ek bilgi (bağlanan veritabanı, eklenen takma ad).</param>
    private async Task WriteAuditAsync(
        string action,
        string siteName,
        CancellationToken cancellationToken,
        Guid? sahip = null,
        string? ayrinti = null)
    {
        await auditService.WriteForSubscriptionAsync(
            action,
            $"site:{siteName}",
            ayrinti is null
                ? $"IIS site '{siteName}' operation completed."
                : $"IIS site '{siteName}' operation completed ({ayrinti}).",
            User.GetRequiredUserId(),
            sahip ?? await AuditSubject.SiteOwnerAsync(customerScopes, siteName, cancellationToken),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
    }

    private static string ResolveDatabaseEngine(string requestedEngine, HashSet<string> installedComponents)
    {
        if (!string.IsNullOrWhiteSpace(requestedEngine))
        {
            return requestedEngine.Trim().ToLowerInvariant();
        }

        if (installedComponents.Contains("mariadb-11"))
        {
            return "mariadb";
        }

        if (installedComponents.Contains("mysql-8"))
        {
            return "mysql";
        }

        return installedComponents.Contains("postgresql-16") ? "postgresql" : string.Empty;
    }

    // FTP kullanıcısı Windows YEREL hesabı olduğundan toplam ad EN FAZLA 20 karakter olmalı
    // (net user limiti). DB kullanıcısı için 32 yeterli. Uzun site adlarında base kırpılırken
    // benzersizliği korumak için kısa kararlı bir hash eklenir (örn. "golftaksiatak_a1b2_ftp").
    private static string BuildAccountName(string siteName, string suffix, int maxTotalLength = 32)
    {
        var normalized = new string(siteName
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray())
            .Trim('_');
        normalized = string.IsNullOrWhiteSpace(normalized) ? "site" : normalized;

        var maxBaseLength = Math.Max(1, maxTotalLength - suffix.Length - 1);
        if (normalized.Length > maxBaseLength)
        {
            // Kırpılan adda çakışmayı önlemek için 4 karakterlik kararlı hash ekle.
            var code = StableShortCode(normalized);
            var keep = Math.Max(1, maxBaseLength - code.Length - 1);
            normalized = $"{normalized[..keep]}_{code}";
        }

        return $"{normalized}_{suffix}";
    }

    /// <summary>İsimden türetilen, süreçler arası KARARLI 4 karakterlik kod (çakışma önler).</summary>
    private static string StableShortCode(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 2).ToLowerInvariant();
    }

    private static string GeneratePassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%*-_";
        const string all = upper + lower + digits + symbols;
        var chars = new[]
        {
            upper[RandomNumberGenerator.GetInt32(upper.Length)],
            lower[RandomNumberGenerator.GetInt32(lower.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)],
            symbols[RandomNumberGenerator.GetInt32(symbols.Length)]
        }.ToList();

        while (chars.Count < 20)
        {
            chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        }

        for (var index = chars.Count - 1; index > 0; index--)
        {
            var swapIndex = RandomNumberGenerator.GetInt32(index + 1);
            (chars[index], chars[swapIndex]) = (chars[swapIndex], chars[index]);
        }

        return new string(chars.ToArray());
    }

    /// <summary>
    /// Yeni site bir alt alan adıysa üst alan adının DNS bölgesine A kaydı düşürür.
    /// </summary>
    /// <remarks>
    /// Üst alan adı, YÖNETİLEN siteler arasında aranır: panelin bölgesini yönetmediği bir alan adına
    /// kayıt atmak zaten mümkün değildir. En UZUN eşleşen üst alan seçilir — hem <c>ornek.com</c>
    /// hem <c>ic.ornek.com</c> yönetiliyorsa, <c>test.ic.ornek.com</c> için doğru bölge ikincisidir;
    /// en kısası seçilseydi kayıt yanlış bölgeye düşer ve hiç çözmezdi.
    /// </remarks>
    private async Task TryEnsureSubdomainRecordAsync(
        SiteCreationResultDto result,
        SiteDto site,
        string? requestedHostName,
        IDnsService dnsService,
        CancellationToken cancellationToken)
    {
        var host = DomainNames.Normalize(
            string.IsNullOrWhiteSpace(requestedHostName) ? site.Name : requestedHostName);
        if (host.Length == 0 || !host.Contains('.'))
        {
            return;
        }

        try
        {
            var siteler = await iisSiteService.GetSitesAsync(cancellationToken);
            var ust = siteler
                .Where(item => !string.Equals(item.Name, site.Name, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Name)
                .Where(ad => DomainNames.IsSubdomainOf(host, ad))
                .OrderByDescending(ad => ad.Length)
                .FirstOrDefault();

            if (ust is null)
            {
                return;
            }

            var etiket = DomainNames.LabelUnder(host, ust);
            var sonuc = await dnsService.EnsureSubdomainRecordAsync(ust, etiket, cancellationToken);

            // KARAR SAF BIR ISLEVDE: "hangi listeye, hangi cumle" zinciri buradayken hic
            // sinanamiyordu (denetleyicinin butun bagimliliklari gerekiyordu) ve bir dali yillarca
            // yanlisti -- kayit Windows DNS'e YAYINLANMAMIS olsa bile "olusturuldu" yaziyordu.
            // Ayni gerekce DnsSubdomainDecision.ZatenDogruMu'yu da buradan cikarmisti.
            var rapor = DnsSubdomainDecision.Anlat(sonuc, $"{etiket}.{ust}");
            if (rapor is not null)
            {
                (rapor.Uyari ? result.Warnings : result.Created).Add(rapor.Satir);
            }
        }
        catch (Exception exception)
        {
            result.Warnings.Add($"dns ({host}): {DescribeProvisionError(exception)}");
        }
    }
}
