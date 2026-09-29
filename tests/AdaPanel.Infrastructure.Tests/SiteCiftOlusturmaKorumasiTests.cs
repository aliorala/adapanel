using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// "Oluştur ve yayınla"ya ikinci kez basmak, az önce kurulan siteyi SİLİYOR muydu?
/// </summary>
/// <remarks>
/// <para>
/// <b>CANLIDA KULLANICI BİLDİRDİ.</b> Bir alan adı eklendi, site kuruldu, ama düğme hâlâ
/// "Oluştur ve yayınla" yazıyordu ve form açıktı; iş olmamış gibi görünüyordu. Tekrar basılabilirdi.
/// </para>
/// <para>
/// <b>İkinci basışın bedeli ölçüldü:</b> <c>WebAdministrationSiteService.CreateSiteAsync</c> aynı
/// adda bir site bulunca onu KALDIRIP yeniden oluşturur. Bu davranış bilinçliydi (önceki bir
/// AdaPanel kurulumundan kalan artık siteyi devralmak için) ama hiçbir onay istemiyordu. Üstelik
/// <c>EnsureSiteNameIsClaimableAsync</c> koruması <c>if (!User.IsInRole("Administrator"))</c>
/// bloğunun İÇİNDE — yani panelin sahibi bu korumanın tamamen dışındaydı.
/// </para>
/// <para>
/// <b>FAZLA KAPATMA BURADA GERÇEK RİSK:</b> "aynı ad varsa her zaman reddet" çözümü, alan adını
/// bilerek yeniden ekleme akışını (kaldırılmış kurulumdan kalan artık site) öldürürdü. Bu yüzden
/// red KOŞULLU: açık onay geldiğinde yol yine çalışır.
/// </para>
/// </remarks>
public sealed class SiteCiftOlusturmaKorumasiTests
{
    private const string Ad = "videotools.com.tr";

    /// <summary>
    /// AYNI AD VARKEN onaysız istek REDDEDİLİR ve sağlama HİÇ ÇALIŞMAZ.
    /// </summary>
    /// <remarks>
    /// "Sağlama hiç çalışmaz" kısmı iddianın yarısıdır: yalnız hata dönmesi yetmez, yıkıcı adıma
    /// GİRİLMEMİŞ olmalı. Sahte sağlama servisi çağrılırsa test patlar.
    /// </remarks>
    [Fact]
    public async Task Ayni_ad_varken_onaysiz_istek_reddedilir_ve_saglama_calismaz()
    {
        var saglama = new SahteSaglama();
        var denetleyici = Kur(new[] { Ad }, saglama);

        var hata = await Assert.ThrowsAsync<AppException>(() =>
            denetleyici.CreateSiteAsync(Istek(Ad, degistir: false), null!, null!, null!, null!, null!, CancellationToken.None));

        Assert.Equal("sites.name_exists", hata.MessageKey);
        Assert.Equal(409, hata.HttpStatusCode);
        Assert.Contains(Ad, hata.Details, StringComparison.Ordinal);
        Assert.Equal(0, saglama.CagriSayisi);
    }

    /// <summary>
    /// Karşılaştırma BÜYÜK/KÜÇÜK HARFE DUYARSIZ ve boşluk kırpar.
    /// </summary>
    /// <remarks>
    /// Kullanıcı formu ikinci kez gönderirken metni elle düzeltmiş olabilir; "VideoTools.com.tr"
    /// ile "videotools.com.tr" IIS için AYNI sitedir. Duyarlı bir karşılaştırma korumayı
    /// sessizce atlatırdı.
    /// </remarks>
    [Theory]
    [InlineData("VideoTools.com.tr")]
    [InlineData("  videotools.com.tr  ")]
    public async Task Ad_karsilastirmasi_harf_ve_bosluga_takilmaz(string yazilan)
    {
        var saglama = new SahteSaglama();
        var denetleyici = Kur(new[] { Ad }, saglama);

        var hata = await Assert.ThrowsAsync<AppException>(() =>
            denetleyici.CreateSiteAsync(Istek(yazilan, degistir: false), null!, null!, null!, null!, null!, CancellationToken.None));

        Assert.Equal("sites.name_exists", hata.MessageKey);
        Assert.Equal(0, saglama.CagriSayisi);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: AÇIK ONAY geldiğinde yol yine çalışır.
    /// </summary>
    /// <remarks>
    /// Alan adını bilerek yeniden ekleme akışı korunmalı. Bu test olmadan "hep reddet" çözümü
    /// yeşil görünür ve önceki kurulumdan kalan artık siteyi devralma yolu ölürdü.
    /// </remarks>
    [Fact]
    public async Task Acik_onayla_saglama_yine_calisir()
    {
        var saglama = new SahteSaglama();
        var denetleyici = Kur(new[] { Ad }, saglama);

        // Sağlama, kendi çağrıldığını kanıtlayan bir nişan fırlatır: boru hattının kalanını
        // (denetim kaydı, DNS, bileşen kataloğu) kurmadan "buraya GİRİLDİ" ölçülebilsin.
        await Assert.ThrowsAsync<SaglamaCagrildi>(() =>
            denetleyici.CreateSiteAsync(Istek(Ad, degistir: true), null!, null!, null!, null!, null!, CancellationToken.None));

        Assert.Equal(1, saglama.CagriSayisi);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: ad BOŞTAysa onay istenmez, sıradan oluşturma çalışır.
    /// </summary>
    [Fact]
    public async Task Ad_bostaysa_onay_istenmez()
    {
        var saglama = new SahteSaglama();
        var denetleyici = Kur(new[] { "baska-site.example" }, saglama);

        await Assert.ThrowsAsync<SaglamaCagrildi>(() =>
            denetleyici.CreateSiteAsync(Istek(Ad, degistir: false), null!, null!, null!, null!, null!, CancellationToken.None));

        Assert.Equal(1, saglama.CagriSayisi);
    }

    // =====================================================================================

    private static CreateSiteRequest Istek(string ad, bool degistir) => new()
    {
        Name = ad,
        PhysicalPath = "C:\\inetpub\\wwwroot\\" + ad,
        HostName = ad,
        Protocol = "http",
        Port = 80,
        RuntimeProfile = "auto",
        ReplaceExisting = degistir
    };

    /// <summary>
    /// Yönetici kimliğiyle denetleyici. Kota ve kapsam atama adımları yöneticide erken döndüğü
    /// için <c>customerScopes</c> ve <c>panelPreferences</c> bu yolda kullanılmaz.
    /// </summary>
    private static SitesController Kur(string[] mevcutSiteAdlari, SahteSaglama saglama)
    {
        var denetleyici = new SitesController(
            new SahteSiteServisiOkuma(mevcutSiteAdlari),
            saglama,
            null!, null!, null!, null!, null!, null!, null!, null!,
            new DogrudanKilit(),
            // GERCEK deger: EnsureAdminPathIsWritable veri kokunu buradan okuyor ve istenen
            // fiziksel yolun panelin kendi klasorune yazmadigini dogruluyor.
            Microsoft.Extensions.Options.Options.Create(
                new AdaPanel.Infrastructure.Options.PanelRuntimeOptions { DataRoot = @"C:\ProgramData\AdaPanel" }),
            NullLogger<SitesController>.Instance);

        var kimlik = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Role, "Administrator"),
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
            },
            "test"));

        denetleyici.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = kimlik }
        };

        return denetleyici;
    }

    private sealed class SaglamaCagrildi : Exception
    {
        public SaglamaCagrildi() : base("saglama cagrildi (nisan)") { }
    }

    private sealed class SahteSaglama : ISiteProvisioningService
    {
        public int CagriSayisi { get; private set; }

        public Task<SiteDto> CreateAndProvisionAsync(
            CreateSiteRequest request, Guid? actorUserId, string? ipAddress, CancellationToken cancellationToken)
        {
            CagriSayisi++;
            throw new SaglamaCagrildi();
        }

        public Task<SiteDto> SetDocumentRootAsync(string siteName, string documentRoot, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SiteDto> EnableFrameworkRewriteAsync(string siteName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SitePermissionRepairResultDto> RepairPermissionsAsync(string siteName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class SahteSiteServisiOkuma : IIisSiteService
    {
        private readonly IReadOnlyCollection<SiteDto> _siteler;

        public SahteSiteServisiOkuma(IEnumerable<string> adlar)
            => _siteler = adlar.Select(ad => new SiteDto { Name = ad }).ToArray();

        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_siteler);

        // Bu yolda çağrılmayan üyeler BİLEREK fırlatır: sessizce null dönerlerse test, ölçmek
        // istediği şeyi ölçmeden yeşil kalabilirdi.
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>Kilit sırayı değiştirmez; işi olduğu gibi çalıştırır.</summary>
    private sealed class DogrudanKilit : IConcurrencyLock
    {
        public Task RunExclusiveAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => operation();

        public Task<T> RunExclusiveAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => operation();

        public Task RunExclusiveAsync(string key, Func<Task> operation, CancellationToken cancellationToken = default)
            => operation();

        public Task<T> RunExclusiveAsync<T>(string key, Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => operation();
    }
}
