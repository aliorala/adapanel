using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.Subscriptions;

/// <summary>
/// Bayi tavanının ÖLÇÜMÜ ve DAYATILMASI. Tek kaynak: tavanı soran her yol buradan geçer.
/// </summary>
/// <remarks>
/// <para>
/// Statik ve bağımlılıkları parametre: tavan ölçümü hem kapsam servisini hem tavan deposunu
/// gerektiriyor, ikisini tek bir servise enjekte etmek ise DI döngüsü doğururdu
/// (bkz. <see cref="IResellerQuotaService"/>).
/// </para>
/// <para>
/// <b>Denetim DAĞITILANA bakar, kullanılana değil.</b> Bayi tavanının anlamı "bu kadarını
/// satabilirsin"dir; müşteri hakkı verildiği anda satılmıştır. Kullanılana bakılsaydı bayi
/// 50 sitelik tavanla 500 sitelik hak dağıtır, tavan yalnız müşteriler siteyi AÇTIKÇA
/// devreye girer ve fatura ile gerçeklik tutmazdı.
/// </para>
/// </remarks>
public static class ResellerQuotaGuard
{
    /// <summary>
    /// Bayi site tavanı aşıldı.
    /// <para>
    /// Anahtar fırlatma yerlerinde METİN olarak yazılır, bu sabitten okunmaz: çeviri kapsamı testi
    /// (<c>MessageKeyCoverageTests</c>) kaynağı düz metin arayarak tarar ve sabitten okunan anahtar
    /// taramada görünmez — çevirisi unutulduğunda kullanıcı ekranda ham anahtarı görür. Sabit
    /// yalnız okunabilirlik ve testlerin başvurusu için var.
    /// </para>
    /// </summary>
    public const string SiteQuotaKey = "reseller.site_quota_exceeded";

    /// <summary>Bayi disk tavanı aşıldı (gerekçe: <see cref="SiteQuotaKey"/>).</summary>
    public const string DiskQuotaKey = "reseller.disk_quota_exceeded";

    /// <summary>
    /// Bayinin tavanı ve o tavanın ne kadarının dağıtıldığı. Tavan yoksa limitler 0 (sınırsız) döner.
    /// </summary>
    public static async Task<ResellerQuotaUsageDto> MeasureAsync(
        IResellerQuotaService quotas,
        ICustomerScopeService scopes,
        Guid resellerId,
        CancellationToken cancellationToken = default)
    {
        var tavan = await quotas.GetQuotaAsync(resellerId, cancellationToken);
        var musteriler = await scopes.GetOwnedUserIdsAsync(resellerId, cancellationToken);

        var kullanim = new ResellerQuotaUsageDto
        {
            ResellerId = resellerId,
            MaxSites = tavan?.MaxSites ?? 0,
            MaxDiskMb = tavan?.MaxDiskMb ?? 0,
            CustomerCount = musteriler.Count
        };

        foreach (var musteriId in musteriler)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kapsam = await scopes.GetScopeAsync(musteriId, cancellationToken);

            // 0 = SINIRSIZ. Sınırsız bir müşteri, tavanı olan bir bayide tavanı fiilen deler;
            // toplama katılamaz (kaç edeceği bilinmiyor) ama sessizce yok da sayılamaz.
            if (kapsam.MaxSites <= 0 || kapsam.MaxDiskMb <= 0)
            {
                kullanim.HasUnlimitedCustomer = true;
            }

            kullanim.AllocatedSites += Math.Max(0, kapsam.MaxSites);
            kullanim.AllocatedDiskMb += Math.Max(0, kapsam.MaxDiskMb);
        }

        // Bayinin KENDİ doğrudan siteleri de tavandan düşer: aksi hâlde bayi müşteri açmadan
        // sınırsız site kurarak tavanı tamamen atlardı.
        var kendiSiteleri = await scopes.GetAssignedSitesAsync(resellerId, cancellationToken);
        kullanim.AllocatedSites += kendiSiteleri.Count;

        var etkinSiteler = await scopes.GetResellerSitesAsync(resellerId, cancellationToken);
        kullanim.UsedSites = etkinSiteler.Count;

        return kullanim;
    }

    /// <summary>
    /// Bir müşteriye verilecek YENİ limitlerin bayinin tavanına sığdığını doğrular; sığmıyorsa 403.
    /// </summary>
    /// <param name="targetCustomerId">
    /// Limiti değişen müşteri. Toplam hesaplanırken bu müşterinin ESKİ payı çıkarılır, yenisi
    /// eklenir — aksi hâlde bir müşterinin planını AYNI plana yeniden bağlamak bile tavanı
    /// aşıyor görünürdü (payı iki kez sayılırdı).
    /// </param>
    public static async Task EnsureAllocationFitsAsync(
        IResellerQuotaService quotas,
        ICustomerScopeService scopes,
        Guid resellerId,
        Guid targetCustomerId,
        int newMaxSites,
        int newMaxDiskMb,
        CancellationToken cancellationToken = default)
    {
        var tavan = await quotas.GetQuotaAsync(resellerId, cancellationToken);
        if (tavan is null)
        {
            return; // tavan tanımlı değil → mevcut davranış (sınırsız) korunur
        }

        var digerSiteler = 0;
        var digerDisk = 0;
        foreach (var musteriId in await scopes.GetOwnedUserIdsAsync(resellerId, cancellationToken))
        {
            if (musteriId == targetCustomerId)
            {
                continue;
            }

            var kapsam = await scopes.GetScopeAsync(musteriId, cancellationToken);
            digerSiteler += Math.Max(0, kapsam.MaxSites);
            digerDisk += Math.Max(0, kapsam.MaxDiskMb);

            // Sınırsız bir müşteri varken tavan zaten ölçülemez; yeni bir tahsis kabul edilirse
            // tavan "geçildi mi" sorusu yanıtsız kalır. Ölçemediğimizi kabul edip reddediyoruz.
            if (tavan.MaxSites > 0 && kapsam.MaxSites <= 0)
            {
                throw new AppException("reseller.site_quota_exceeded", 403, 403);
            }

            if (tavan.MaxDiskMb > 0 && kapsam.MaxDiskMb <= 0)
            {
                throw new AppException("reseller.disk_quota_exceeded", 403, 403);
            }
        }

        var kendiSiteleri = (await scopes.GetAssignedSitesAsync(resellerId, cancellationToken)).Count;

        if (tavan.MaxSites > 0)
        {
            // Tavanı olan bayi SINIRSIZ hak dağıtamaz; bu tek başına tavanı anlamsız kılardı.
            if (newMaxSites <= 0)
            {
                throw new AppException("reseller.site_quota_exceeded", 403, 403);
            }

            if (digerSiteler + kendiSiteleri + newMaxSites > tavan.MaxSites)
            {
                throw new AppException("reseller.site_quota_exceeded", 403, 403);
            }
        }

        if (tavan.MaxDiskMb > 0)
        {
            if (newMaxDiskMb <= 0)
            {
                throw new AppException("reseller.disk_quota_exceeded", 403, 403);
            }

            if (digerDisk + newMaxDiskMb > tavan.MaxDiskMb)
            {
                throw new AppException("reseller.disk_quota_exceeded", 403, 403);
            }
        }
    }

    /// <summary>
    /// Bayinin TANIMLADIĞI bir planın, kendi tavanının üstünde limit vermediğini doğrular.
    /// </summary>
    /// <remarks>
    /// Tahsis denetimi (yukarıdaki) tek başına yetmez: 10 sitelik tavanı olan bayi 100 sitelik bir
    /// plan tanımlayıp müşteri ekranlarında bunu satabilir, tahsis denetimi ancak plan BAĞLANIRKEN
    /// devreye girerdi. Kullanıcıya satılamayacak bir planı önce tanımlatıp sonra reddetmek,
    /// hatayı en geç ve en pahalı yerde göstermek olurdu.
    /// </remarks>
    public static async Task EnsurePlanFitsAsync(
        IResellerQuotaService quotas,
        Guid resellerId,
        int planMaxSites,
        int planMaxDiskMb,
        CancellationToken cancellationToken = default)
    {
        var tavan = await quotas.GetQuotaAsync(resellerId, cancellationToken);
        if (tavan is null)
        {
            return;
        }

        if (tavan.MaxSites > 0 && (planMaxSites <= 0 || planMaxSites > tavan.MaxSites))
        {
            throw new AppException("reseller.site_quota_exceeded", 403, 403);
        }

        if (tavan.MaxDiskMb > 0 && (planMaxDiskMb <= 0 || planMaxDiskMb > tavan.MaxDiskMb))
        {
            throw new AppException("reseller.disk_quota_exceeded", 403, 403);
        }
    }
}
