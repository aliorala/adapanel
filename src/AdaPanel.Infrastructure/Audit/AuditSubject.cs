using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.Audit;

/// <summary>
/// Bir denetim kaydının KONUSU olan aboneliği ÖLÇEREK çözer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden var:</b> <c>AuditLog.SubscriptionUserId</c> alanı vardı, görünürlük motoru
/// (<see cref="AuditLogQueryEngine.ApplyVisibility"/>) onu sorguluyordu ama neredeyse hiçbir uç
/// doldurmuyordu. Sonuç ölçüldü: denetleyicilerdeki 104 kayıt çağrısının yalnız 26'sı konuyu
/// yazıyordu. Bir YÖNETİCİ kiracının posta kutusunu sildiğinde kaydın <c>UserId</c>'si yöneticidir
/// ve kiracının izinli kimlik kümesinde yoktur; konu da boş olduğu için kayıt kiracının
/// görünümünde HİÇ çıkmıyordu — "benim siteme/alan adıma kim dokundu" sorusu cevapsızdı.
/// </para>
/// <para>
/// <b>Neden ayrı bir yardımcı, her uçta elle çözüm değil:</b> aynı üç soru (site kimin, alan adı
/// kimin, veritabanı kimin) onlarca uçta soruluyor. Kopyalandığı anda biri <c>www.</c> kuralını
/// alır diğeri almaz, biri hatayı yutar diğeri isteği düşürür. Panelde bu hata sınıfı zaten
/// yaşandı (mail sahipliği ile alan adı iddiası farklı kurallara bağlanmıştı).
/// </para>
/// <para>
/// <b>TAHMİN YOK — ölçülemeyen konu BOŞ bırakılır.</b> Kaydı yanlış bir kiracıya bağlamak, hiç
/// bağlamamaktan kötüdür: yanlış kiracı komşusunun işlemlerini görür (çapraz-kiracı sızıntı) ve
/// gerçek sahibi yine göremez. Bu yüzden sahiplik belirsizse (birden çok site aynı adı
/// sahipleniyor ve sahipleri farklı) <c>null</c> dönülür.
/// </para>
/// <para>
/// <b>Neden hiçbir zaman fırlatmaz:</b> konu çözümü ASIL İŞLEMİN yan ürünüdür ve işlem çoktan
/// yapılmıştır. Sahiplik dosyası okunamadı diye posta kutusu açma isteğinin 500 dönmesi, kaydı
/// zenginleştirme çabasının kullanıcıya arıza olarak yansıması olurdu.
/// </para>
/// </remarks>
public static class AuditSubject
{
    /// <summary>Sitenin abonelik sahibi; site sahipsizse (yöneticinin kendi sitesi) <c>null</c>.</summary>
    public static async Task<Guid?> SiteOwnerAsync(
        ICustomerScopeService scopes,
        string? siteName,
        CancellationToken cancellationToken)
    {
        var ad = (siteName ?? string.Empty).Trim();
        if (ad.Length == 0)
        {
            return null;
        }

        try
        {
            return await scopes.GetSiteOwnerUserIdAsync(ad, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Posta alan adının abonelik sahibi.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden bu iki adım:</b> panelde bir aboneliğin izinli posta alan adları
    /// <c>CustomerScopeService.GetAllowedMailDomainsAsync</c> tarafından (a) atanan SİTE ADLARINDAN
    /// ve (b) o sitelerin BAĞLAMA host adlarından türetilir. Buradaki çözüm tam olarak aynı
    /// kaynağın tersidir; başka bir kural kullansaydık, kiracının erişebildiği bir alan adındaki
    /// işlem kiracıya BAŞKA bir sahiple etiketlenebilirdi.
    /// </para>
    /// <para>
    /// <c>www.</c> ön eki aynı sahiplik kuralıyla düşürülür (<c>CustomerAccess.DomainClaimForms</c>
    /// ile birebir): <c>www.ornek.com</c> bağlaması <c>ornek.com</c> postasını da sahiplenir.
    /// </para>
    /// </remarks>
    public static async Task<Guid?> MailDomainOwnerAsync(
        ICustomerScopeService scopes,
        IIisSiteService? iisSiteService,
        string? domain,
        CancellationToken cancellationToken)
    {
        var adaylar = AlanAdiBicimleri(domain);
        if (adaylar.Count == 0)
        {
            return null;
        }

        // 1) Alan adının kendisi bir SİTE ADI mı? En sık durum ve en ucuz sorgu (tek JSON okuması).
        foreach (var aday in adaylar)
        {
            var sahip = await SiteOwnerAsync(scopes, aday, cancellationToken);
            if (sahip is not null)
            {
                return sahip;
            }
        }

        if (iisSiteService is null)
        {
            return null;
        }

        // 2) Alan adı bir sitenin BAĞLAMASINDA olabilir (site adı "musteri1", bağlama "ornek.com").
        IReadOnlyCollection<Core.Dtos.Sites.SiteDto> siteler;
        try
        {
            siteler = await iisSiteService.GetSitesAsync(cancellationToken);
        }
        catch
        {
            return null;
        }

        var bulunanlar = new HashSet<Guid>();
        foreach (var site in siteler)
        {
            var sitenin = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var binding in site.Bindings)
            {
                foreach (var bicim in AlanAdiBicimleri(binding.HostName))
                {
                    sitenin.Add(bicim);
                }
            }

            if (!sitenin.Overlaps(adaylar))
            {
                continue;
            }

            if (await SiteOwnerAsync(scopes, site.Name, cancellationToken) is { } sahip)
            {
                bulunanlar.Add(sahip);
            }
        }

        // BİRDEN ÇOK SAHİP = ÖLÇEMEDİK. Aynı host adı iki farklı aboneliğin sitesine bağlıysa
        // hangisinin konu olduğunu söyleyemeyiz; birini seçmek yazı tura atmaktır.
        return bulunanlar.Count == 1 ? bulunanlar.First() : null;
    }

    /// <summary>Posta ADRESİNİN ("kutu@alan.com") alan adı sahibi.</summary>
    /// <remarks>
    /// Adres yerel kısımdan ibaretse (<c>info</c>) <paramref name="fallbackDomain"/> kullanılır —
    /// panelin posta uçları iki biçimi de kabul ediyor ve kapsam denetimi de aynı kuralı uyguluyor.
    /// </remarks>
    public static Task<Guid?> MailAddressOwnerAsync(
        ICustomerScopeService scopes,
        IIisSiteService? iisSiteService,
        string? address,
        string? fallbackDomain,
        CancellationToken cancellationToken)
    {
        var kirpilmis = (address ?? string.Empty).Trim();
        var at = kirpilmis.IndexOf('@');
        var alanAdi = at >= 0 ? kirpilmis[(at + 1)..] : fallbackDomain;
        return MailDomainOwnerAsync(scopes, iisSiteService, alanAdi, cancellationToken);
    }

    /// <summary>Veritabanının kayıtlı sahibi (config/db-ownership.json); kayıt yoksa <c>null</c>.</summary>
    /// <remarks>
    /// Sahiplik panelin KENDİ kaydından okunur, motordaki GRANT'lardan tahmin edilmez: bir
    /// kullanıcının bir veritabanına yetkisi olması onun sahibi olduğu anlamına gelmez (paylaşılan
    /// yetkiler, panel dışında verilmiş GRANT'lar).
    /// </remarks>
    public static async Task<Guid?> DatabaseOwnerAsync(
        IDatabaseOwnershipService ownership,
        string? engine,
        string? databaseName,
        CancellationToken cancellationToken)
    {
        var motor = (engine ?? string.Empty).Trim();
        var ad = (databaseName ?? string.Empty).Trim();
        if (motor.Length == 0 || ad.Length == 0)
        {
            return null;
        }

        try
        {
            return await ownership.GetOwnerAsync(motor, ad, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Bir alan adının sahiplik bakımından karşılık geldiği biçimler (kendisi + <c>www.</c>siz hâli).
    /// </summary>
    private static HashSet<string> AlanAdiBicimleri(string? domain)
    {
        var bicimler = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ad = (domain ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (ad.Length == 0)
        {
            return bicimler;
        }

        bicimler.Add(ad);
        if (ad.StartsWith("www.", StringComparison.Ordinal))
        {
            bicimler.Add(ad[4..]);
        }

        return bicimler;
    }
}
