using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Abonelik alanları, ALAKASIZ bir çağrıyla silinemez.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ölçülen açık:</b> <c>SetScopeAsync</c> kaydı SIFIRDAN kuruyordu (<c>new ScopeEntry { … }</c>)
/// ve korunacak alanları tek tek sayıyordu. Listede olmayan her alan sessizce varsayılana
/// düşüyordu: <c>SuspendedAtUtc</c>, <c>SuspendReason</c>, <c>PlanId</c>, <c>ExpiresAtUtc</c>,
/// <c>MaxFtpUsers</c>, <c>MaxTrafficGbPerMonth</c>.
/// </para>
/// <para>
/// Bu, "panel yapamadığı şeye başardım der" sınıfının <b>yönü ters</b> hâliydi: panel askıyı
/// gerçekten uygulamıştı, sonra ALAKASIZ bir çağrı (müşteri site oluşturuyor, yönetici posta kutusu
/// sayısını düzenliyor) askıyı SESSİZCE KALDIRIYORDU. Ekranda "askıda" yazan abonelik bir sonraki
/// site oluşturmadan sonra etkin oluyor ve bunu söyleyen hiçbir kayıt kalmıyordu. Silinen trafik
/// kotası (<c>0</c>) arayüzde "sınırsız" ile aynı görünür.
/// </para>
/// <para>
/// <b>Sıra önemlidir.</b> Mevcut <c>SubscriptionSuspensionEnforcementTests</c> düzeneği önce
/// <c>SetScopeAsync</c> sonra <c>SuspendAsync</c> çağırıyor — o sırada hata GÖRÜNMEZ. Hata yalnız
/// gerçek hayattaki sırada ortaya çıkar: önce askıya al, sonra kullanıcı bir şey yapsın.
/// </para>
/// <para>
/// <b>Test alan listesi saymaz.</b> Saysaydı bir sonraki alan eklendiğinde o da eksik kalırdı;
/// hatanın kök nedeni tam olarak "koru" listesinin varlığıydı. Ölçülen şey DAVRANIŞ.
/// </para>
/// </remarks>
public sealed class AbonelikAlanKorumaTests : IDisposable
{
    private readonly string kok;
    private readonly ICustomerScopeService kapsamlar;

    public AbonelikAlanKorumaTests()
    {
        kok = Path.Combine(Path.GetTempPath(), "adapanel-abonelik-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(kok);

        var store = new JsonFileStore(
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = kok }));

        kapsamlar = new CustomerScopeService(store, new PlansizServis(), new TavansizServis(), new ResellerSiteAssignmentTests.KullanilmayanSiteServisi());
    }

    public void Dispose()
    {
        try { Directory.Delete(kok, recursive: true); } catch { /* temizlik zorunlu değil */ }
    }

    [Fact]
    public async Task Kapsam_yazmak_ASKIYI_kaldirmaz()
    {
        var kullanici = Guid.NewGuid();
        await kapsamlar.SuspendAsync(kullanici, "odeme yok");
        Assert.True((await kapsamlar.GetScopeAsync(kullanici)).IsSuspended);

        // ALAKASIZ CAGRI: musteri/bayi bir site olusturunca bu yol kosuyor.
        await kapsamlar.SetScopeAsync(kullanici, ["a.test"], maxSites: 5, maxDiskMb: 1024);

        Assert.True(
            (await kapsamlar.GetScopeAsync(kullanici)).IsSuspended,
            "Site olusturmak askiyi kaldirdi -- odeme yapmayan musteri calismaya devam eder.");
    }

    /// <summary>
    /// Kapsam yazmak, aboneligin BITIS TARIHINI silmez.
    /// </summary>
    /// <remarks>
    /// Once PlanId ile olculmek istendi ve test kirmizi yandi -- ama sebep duzeltme DEGILDI:
    /// <c>GetScopeAsync</c> plan kimligini plan servisinden COZUYOR ve buradaki cift "plansiz"
    /// oldugu icin var olmayan bir plan bosa dusuyor. Yani olculen sey alan korunmasi degil, plan
    /// cozumlemesi olurdu. Bitis tarihi ayni sinifta bir alandir ve hicbir servise bagli degildir,
    /// bu yuzden korumanin kendisini olcer.
    /// </remarks>
    [Fact]
    public async Task Kapsam_yazmak_BITIS_TARIHINI_silmez()
    {
        var kullanici = Guid.NewGuid();
        var bitis = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await kapsamlar.SetExpiryAsync(kullanici, bitis);

        await kapsamlar.SetScopeAsync(kullanici, ["a.test"], maxSites: 5, maxDiskMb: 1024);

        Assert.Equal(bitis, (await kapsamlar.GetScopeAsync(kullanici)).ExpiresAtUtc);
    }

    /// <summary>
    /// Sitesi olmayan ama ASKIDA olan kayıt SİLİNMEZ.
    /// </summary>
    /// <remarks>
    /// Kaydın silinmesi de askıyı kaldırır: olmayan kayıt "askıda değil" demektir. Eski "boş mu"
    /// koşulu yalnız sitelere ve iki kotaya bakıyordu, abonelik alanlarına bakmıyordu.
    /// </remarks>
    [Fact]
    public async Task Sitesiz_ama_askida_olan_kayit_SILINMEZ()
    {
        var kullanici = Guid.NewGuid();
        await kapsamlar.SuspendAsync(kullanici, "odeme yok");

        await kapsamlar.SetScopeAsync(kullanici, [], maxSites: 0, maxDiskMb: 0);

        Assert.True(
            (await kapsamlar.GetScopeAsync(kullanici)).IsSuspended,
            "Sitesiz kalan askili kayit silindi -- aski sessizce kalkti.");
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: metodun KENDİ alanları yazılmaya devam etmeli.
    /// </summary>
    /// <remarks>
    /// "Hiçbir şeyi değiştirme" de yukarıdaki üç testi geçerdi ve kapsam yazmayı tümüyle öldürürdü.
    /// </remarks>
    [Fact]
    public async Task Metodun_kendi_alanlari_YAZILMAYA_devam_eder()
    {
        var kullanici = Guid.NewGuid();
        await kapsamlar.SetScopeAsync(kullanici, ["a.test", "b.test"], maxSites: 7, maxDiskMb: 2048);

        var kapsam = await kapsamlar.GetScopeAsync(kullanici);

        Assert.Equal(7, kapsam.MaxSites);
        Assert.Equal(2048, kapsam.MaxDiskMb);
        Assert.Contains("a.test", kapsam.Sites, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("b.test", kapsam.Sites, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Kapsam yazımı, ESKİ siteleri de gerçekten değiştirebilmeli.
    /// </summary>
    /// <remarks>
    /// İkinci fazla-kapatma nöbetçisi: "yerinde güncelle" düzeltmesi, alanların ÜZERİNE
    /// yazılmasını engelleyecek şekilde yanlış yapılabilirdi (örneğin yalnız boş alanları
    /// doldurmak). O zaman site listesi bir daha hiç daralmazdı.
    /// </remarks>
    [Fact]
    public async Task Kapsam_yazmak_eski_site_listesinin_UZERINE_yazar()
    {
        var kullanici = Guid.NewGuid();
        await kapsamlar.SetScopeAsync(kullanici, ["a.test", "b.test"], maxSites: 7, maxDiskMb: 2048);
        await kapsamlar.SetScopeAsync(kullanici, ["a.test"], maxSites: 1, maxDiskMb: 100);

        var kapsam = await kapsamlar.GetScopeAsync(kullanici);

        Assert.DoesNotContain("b.test", kapsam.Sites, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1, kapsam.MaxSites);
    }

    // --- duzenek ---
    //
    // NOT: bu iki cift depoda BESINCI kez yaziliyor (McpToolGuard, QuotaDirectoryWalk,
    // ResellerParity, SubscriptionSuspensionEnforcement ve burada). Hepsi `private` oldugu icin
    // paylasilamiyor. Ortak bir test yardimcisina cikarilmalari ayri ve kucuk bir is; kota
    // baskisi altinda ilgisiz dort dosyaya dokunmak yerine buraya not dusuldu.
    // (IIisSiteService cifti paylasilabiliyor: ResellerSiteAssignmentTests'teki `internal` olan.)

    private sealed class PlansizServis : IServicePlanService
    {
        public Task<ServicePlanDto?> GetPlanAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult<ServicePlanDto?>(null);

        public Task<IReadOnlyCollection<ServicePlanDto>> GetPlansAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<ServicePlanDto>>([]);

        public Task<ServicePlanDto> CreatePlanAsync(ServicePlanRequest request, Guid? ownerId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ServicePlanDto> UpdatePlanAsync(string id, ServicePlanRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeletePlanAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TavansizServis : IResellerQuotaService
    {
        public Task<ResellerQuotaDto?> GetQuotaAsync(Guid resellerId, CancellationToken cancellationToken = default)
            => Task.FromResult<ResellerQuotaDto?>(null);

        public Task<ResellerQuotaDto> SetQuotaAsync(Guid resellerId, ResellerQuotaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveQuotaAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
