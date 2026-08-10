using System.Security.Claims;
using AdaPanel.Api.Extensions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Api.Security;

/// <summary>
/// Kaynak-kapsam denetimleri. Administrator/Operator muaftır. KAPSAMLI roller:
/// <list type="bullet">
/// <item>Customer → yalnızca kendisine atanan siteler.</item>
/// <item>Reseller (Bayi) → sahip olduğu TÜM müşterilerin sitelerinin birleşimi.</item>
/// </list>
/// Her iki rol de yalnızca etkin kapsamındaki sitelerin/mail domainlerinin kaynaklarına erişebilir.
/// </summary>
public static class CustomerAccess
{
    public static bool IsCustomer(ClaimsPrincipal user) => user.IsInRole("Customer");

    public static bool IsReseller(ClaimsPrincipal user) => user.IsInRole("Reseller");

    /// <summary>Kapsam denetimine tabi rol mü (Customer veya Reseller).</summary>
    public static bool IsScoped(ClaimsPrincipal user) => IsCustomer(user) || IsReseller(user);

    /// <summary>
    /// Etkin abonelik sahibi: alt-kullanıcı ise parent müşteri, değilse kullanıcının kendisi.
    /// Aboneliğe bağlı kaynaklar (cron görevleri, self-servis veritabanları) bu kimliğe yazılır →
    /// parent ve tüm alt-kullanıcılar aynı kaynak kümesini ve kotayı paylaşır (Plesk davranışı).
    /// </summary>
    public static async Task<Guid> GetSubscriptionUserIdAsync(
        ICustomerScopeService scopes,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var userId = user.GetRequiredUserId();
        if (IsCustomer(user) && await scopes.GetParentSubscriptionAsync(userId, cancellationToken) is { } parent)
        {
            return parent;
        }

        return userId;
    }

    /// <summary>
    /// Alt-kullanıcı (abonelik altı ek kullanıcı) BÖLÜM İZNİ denetimi — Plesk'in "additional user"
    /// izin matrisinin karşılığı.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden burada, neden yalnız öznitelikte değil:</b> kural <c>RequireSubUserSectionAttribute</c>
    /// içinde yazılıydı ve öznitelik bir MVC filtresidir — MCP araçları MVC boru hattından GEÇMEZ.
    /// Sonuç ölçüldü: bölüm izinleri kısıtlanmış bir alt-kullanıcı, kendi API anahtarıyla
    /// <c>sites_list</c> / <c>dns_upsert_record</c> gibi araçlara erişebiliyordu; yani panelde
    /// kapatılan kapı MCP'de açıktı. Karar buraya (kapsam denetimlerinin tek adresine) taşındı;
    /// öznitelik ve MCP araçları AYNI fonksiyonu çağırıyor.
    /// </para>
    /// <para>
    /// Top-level müşteri, bayi ve yönetici etkilenmez: izin listesi yalnız alt-kullanıcıda bulunur.
    /// Liste <c>null</c> ise (bütün bölümler seçili) kısıt yoktur.
    /// </para>
    /// </remarks>
    public static async Task EnsureSectionAsync(
        ICustomerScopeService scopes,
        ClaimsPrincipal user,
        string section,
        CancellationToken cancellationToken)
    {
        if (!IsCustomer(user))
        {
            return;
        }

        var userId = user.GetRequiredUserId();
        if (await scopes.GetParentSubscriptionAsync(userId, cancellationToken) is null)
        {
            return;
        }

        var allowed = await scopes.GetSubUserSectionsAsync(userId, cancellationToken);
        if (allowed is not null && !allowed.Contains(section, StringComparer.OrdinalIgnoreCase))
        {
            throw new AppException("auth.section_forbidden", 403, 403);
        }
    }

    /// <summary>Kullanıcının erişebileceği site adları; kapsamsız rol (admin/operator) için null.</summary>
    public static async Task<IReadOnlyCollection<string>?> GetEffectiveSitesAsync(
        ICustomerScopeService scopes,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (IsReseller(user))
        {
            return await scopes.GetResellerSitesAsync(user.GetRequiredUserId(), cancellationToken);
        }

        if (IsCustomer(user))
        {
            return await scopes.GetAssignedSitesAsync(user.GetRequiredUserId(), cancellationToken);
        }

        return null;
    }

    /// <summary>Kullanıcının erişebileceği mail domainleri; kapsamsız rol için null.</summary>
    public static async Task<IReadOnlyCollection<string>?> GetEffectiveMailDomainsAsync(
        ICustomerScopeService scopes,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (IsReseller(user))
        {
            return await scopes.GetResellerMailDomainsAsync(user.GetRequiredUserId(), cancellationToken);
        }

        if (IsCustomer(user))
        {
            return await scopes.GetAllowedMailDomainsAsync(user.GetRequiredUserId(), cancellationToken);
        }

        return null;
    }

    public static async Task EnsureSiteAsync(
        ICustomerScopeService scopes,
        ClaimsPrincipal user,
        string? siteName,
        CancellationToken cancellationToken)
    {
        var allowed = await GetEffectiveSitesAsync(scopes, user, cancellationToken);
        if (allowed is null)
        {
            return;
        }

        if (!allowed.Contains(siteName?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            throw new AppException("auth.site_forbidden", 403, 403);
        }
    }

    /// <summary>
    /// Bir kiracının, BAŞKA bir kiracıya ait alan adını sahiplenmesini engeller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bu kontrolün varlık sebebi mail: panelde bir kullanıcının hangi mail domainlerini
    /// yönetebileceği <see cref="GetEffectiveMailDomainsAsync"/> tarafından <b>site
    /// bağlamalarından</b> türetilir. Yani alan adı iddiası, mail sahipliğinin güven kökü. Bu
    /// iddia doğrulanmadığında zincir şöyle işliyordu: kiracı kendi sitesine
    /// <c>kurban.com</c> bağlaması ekler → izinli mail domainleri artık <c>kurban.com</c>'u da
    /// içerir → <see cref="EnsureMailDomainAsync"/> geçer → kurbanın mail domainini yönetir,
    /// hesap açar, postasını alır.
    /// </para>
    /// <para>
    /// İkinci etki SSL/DNS: alan adı bu sunucuya çözülüyorsa HTTP-01 doğrulaması saldırganın
    /// sitesinden yanıtlanır ve kurbanın alan adı için geçerli sertifika alınabilir; DNS bölgesi
    /// sahiplenildiğinde ise kayıtlar tümüyle saldırganın denetimine geçer.
    /// </para>
    /// <para>
    /// Aynı soru panelde ÜÇ yerde soruluyor: site oluşturma (host adı), bağlama ekleme ve DNS
    /// bölge adı. Tek uygulama burada — ayrı ayrı yazılsaydı biri doğru diğeri eksik olurdu.
    /// </para>
    /// <para>
    /// Yalnızca <b>başkasının sahip olduğu</b> adı reddeder; sahipsiz bir ad serbesttir. Bu
    /// bilinçli: mevcut meşru akışlar (yeni alan adı bağlama) aynen çalışmaya devam eder.
    /// </para>
    /// </remarks>
    public static async Task EnsureDomainClaimAsync(
        ICustomerScopeService scopes,
        IIisSiteService iisSiteService,
        IMailService? mailService,
        ClaimsPrincipal user,
        string? domainName,
        string? ownSiteName,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        if (!IsScoped(user))
        {
            return;
        }

        var iddia = DomainClaimForms(domainName);
        if (iddia.Count == 0)
        {
            return;
        }

        var kapsamdakiSiteler = await GetEffectiveSitesAsync(scopes, user, cancellationToken);
        if (kapsamdakiSiteler is null)
        {
            return;
        }

        // 1) Alan adı kapsam DIŞINDAKİ bir siteye zaten bağlı mı?
        foreach (var site in await iisSiteService.GetSitesAsync(cancellationToken))
        {
            if (string.Equals(site.Name, ownSiteName, StringComparison.OrdinalIgnoreCase)
                || kapsamdakiSiteler.Contains(site.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // Sitenin sahiplendiği bütün adlar: site adının kendisi + bağlamalarındaki host adları.
            var sitenin = DomainClaimForms(site.Name);
            foreach (var binding in site.Bindings)
            {
                sitenin.UnionWith(DomainClaimForms(binding.HostName));
            }

            // TAM eşleşme.
            if (sitenin.Overlaps(iddia))
            {
                throw new AppException("sites.host_name_taken", 403, 403);
            }

            // ALT ALAN ADI: iddia edilen ad, başkasının alan adının ALTINDA mı?
            //
            // Bu denetim eskiden yoktu ve yalnız tam eşleşmeye bakılıyordu: "kurban.com" başkasınınken
            // "blog.kurban.com" serbestçe alınabiliyordu. Bedeli sadece içerik değildi — mail sahipliği
            // site BAĞLAMALARINDAN türetildiği için alt alan adının postası da saldırgana geçiyordu,
            // panel o ad için sertifika isteyebiliyordu ve `.kurban.com` kapsamlı çerezler açığa çıkıyordu.
            //
            // Yalnız bu YÖN kapatılır (başkasının alanının altına girmek). Tersi — birinin
            // "blog.kurban.com"u varken "kurban.com" iddia etmek — serbest bırakıldı: yönetici bir
            // kurumsal alan adını bilerek birden çok müşteriye bölmüş olabilir ve o yönü de kapatmak
            // var olan kurulumları kırardı.
            if (iddia.Any(istenen => sitenin.Any(sahip => DomainNames.IsSubdomainOf(istenen, sahip))))
            {
                throw new AppException("sites.host_name_taken", 403, 403);
            }
        }

        // 2) Alan adı kapsam DIŞINDA var olan bir mail domaini mi?
        var kendiDomainleri = await GetEffectiveMailDomainsAsync(scopes, user, cancellationToken);
        if (kendiDomainleri is not null && kendiDomainleri.Any(iddia.Contains))
        {
            return; // zaten onun; yeniden sahiplenmesi meşru
        }

        if (mailService is null)
        {
            return;
        }

        MailWorkspaceDto? calismaAlani = null;
        try
        {
            calismaAlani = await mailService.GetWorkspaceAsync(null, cancellationToken);
        }
        catch (Exception exception)
        {
            // ÖLÇEMEDİK. Bunu "sorun yok" saymıyoruz, ama işlemi de kilitlemiyoruz: mail
            // sunucusu okunamayan bir makinede genellikle hiç mail domaini YOKTUR, dolayısıyla
            // bu yoldan bir sahiplenme de olamaz. Asıl kontrol (1) her hâlükârda çalıştı.
            // Sessizce yutmuyoruz — sorun çıkarsa günlükte görünsün.
            logger?.LogWarning(
                exception,
                "Alan adi sahiplik denetiminde mail domainleri okunamadi ({Domain}); yalniz site baglamalari denetlendi.",
                domainName);
        }

        if (calismaAlani is { Installed: true }
            && calismaAlani.Domains.Any(domain => DomainClaimForms(domain.Name).Overlaps(iddia)))
        {
            throw new AppException("sites.host_name_taken", 403, 403);
        }
    }

    /// <summary>
    /// Kapsamlı bir kullanıcı, VAR OLAN bir IIS sitesinin adıyla yeni site kuramaz — o site zaten
    /// kendi kapsamında değilse. Aksi hâlde 409 <c>sites.duplicate_name</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden gerekli:</b> <c>WebAdministrationSiteService.CreateSiteAsync</c> aynı adlı IIS
    /// sitesini ONAY SORMADAN kaldırıp yeniden kurar. Bu davranış bilinçli (kaldırılmış bir
    /// kurulumdan kalan artık siteyi devralabilmek için) ama oradaki tek koruma
    /// <c>EnsureSiteNameIsNotReserved</c>'dı ve o yalnızca PANELİN KENDİ altyapısını (AdaPanel.*,
    /// Default Web Site) korur. Müşterilerin siteleri hiç korunmuyordu: bir kiracı, kurbanın site
    /// adıyla site açarak onun IIS sitesini sessizce kaldırabiliyordu.
    /// </para>
    /// <para>
    /// <b>Neden burada:</b> altyapı katmanı çağıranın kim olduğunu bilmez; sahiplik bir
    /// yetkilendirme sorusudur ve panelde bu soruların tek adresi bu sınıftır.
    /// </para>
    /// <para>
    /// <b>Neden <see cref="IIisSiteService.GetSiteAsync"/>:</b> liste ucu sistem sitelerini
    /// (panelin araçları, yalnız loopback dinleyen iç siteler) gizler. Gizlenmiş bir siteyi "yok"
    /// saymak, tam da üzerine yazılmasına izin vermek olurdu.
    /// </para>
    /// <para>
    /// Kendi sitesinin adını yeniden kullanmak SERBEST: bugün çalışan meşru bir akış (aynı domaini
    /// yeniden ekleme) ve kiracı zaten o siteyi silebiliyor.
    /// </para>
    /// </remarks>
    public static async Task EnsureSiteNameIsClaimableAsync(
        ICustomerScopeService scopes,
        IIisSiteService iisSiteService,
        ClaimsPrincipal user,
        string? siteName,
        CancellationToken cancellationToken)
    {
        if (!IsScoped(user))
        {
            return;
        }

        var ad = (siteName ?? string.Empty).Trim();
        if (ad.Length == 0)
        {
            return;
        }

        string mevcutAd;
        try
        {
            mevcutAd = (await iisSiteService.GetSiteAsync(ad, cancellationToken)).Name;
        }
        catch (AppException exception) when (exception.HttpStatusCode == 404)
        {
            return; // ad boşta
        }

        var kapsam = await GetEffectiveSitesAsync(scopes, user, cancellationToken);
        if (kapsam is null || kapsam.Contains(mevcutAd, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        // Anahtar YENİDEN ÜRETİLMİYOR: "sites.duplicate_name" panelde zaten var ve iki dilde de
        // çevrili ("Bu site adı zaten IIS içinde kullanılıyor."). Aynı anlam için ikinci bir anahtar
        // açmak, kullanıcının ekranda ham anahtar görmesi riskini bedava üretmek olurdu.
        throw new AppException("sites.duplicate_name", 409, 409);
    }

    /// <summary>
    /// Bir alan adının mail sahipliği bakımından karşılık geldiği adlar.
    /// <para>
    /// <see cref="GetAllowedMailDomainsAsync"/> ile <b>aynı kuralı</b> uygular: <c>www.</c> ön eki
    /// olan bir bağlama, öneksiz domaini de sahiplenir. Kontrolün, koruduğu şeyle aynı kuralı
    /// kullanması şart — aksi hâlde <c>www.kurban.com</c> bağlayıp <c>kurban.com</c> mailini almak
    /// gibi bir aralık açık kalırdı.
    /// </para>
    /// </summary>
    private static HashSet<string> DomainClaimForms(string? domainName)
    {
        var formlar = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ad = (domainName ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (ad.Length == 0)
        {
            return formlar;
        }

        formlar.Add(ad);
        if (ad.StartsWith("www.", StringComparison.Ordinal))
        {
            formlar.Add(ad[4..]);
        }

        return formlar;
    }

    public static async Task EnsureMailDomainAsync(
        ICustomerScopeService scopes,
        ClaimsPrincipal user,
        string? domain,
        CancellationToken cancellationToken)
    {
        var allowed = await GetEffectiveMailDomainsAsync(scopes, user, cancellationToken);
        if (allowed is null)
        {
            return;
        }

        if (!allowed.Contains(domain?.Trim().ToLowerInvariant() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            throw new AppException("auth.domain_forbidden", 403, 403);
        }
    }

    /// <summary>
    /// Dosya yöneticisi sanal yolu denetimi ("sites/&lt;site&gt;/...").
    /// <paramref name="allowRoot"/> true ise kök ve "sites" listelemesine izin verilir
    /// (kök listesi controller'da etkin sitelerle sentezlenir).
    /// </summary>
    public static async Task EnsureFilesPathAsync(
        ICustomerScopeService scopes,
        ClaimsPrincipal user,
        string? virtualPath,
        bool allowRoot,
        CancellationToken cancellationToken)
    {
        if (!IsScoped(user))
        {
            return;
        }

        var normalized = (virtualPath ?? string.Empty).Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || string.Equals(normalized, "sites", StringComparison.OrdinalIgnoreCase))
        {
            if (allowRoot)
            {
                return;
            }

            throw new AppException("auth.site_forbidden", 403, 403);
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !string.Equals(segments[0], "sites", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("auth.site_forbidden", 403, 403);
        }

        await EnsureSiteAsync(scopes, user, segments[1], cancellationToken);
    }

    public static async Task<IReadOnlyCollection<string>?> GetSiteScopeAsync(
        ICustomerScopeService scopes,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        return await GetEffectiveSitesAsync(scopes, user, cancellationToken);
    }
}
