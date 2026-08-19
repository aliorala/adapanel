using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Korumali dizin listesi KAYDA bakiyordu, DOSYAYA degil.
/// <para>
/// Koruma tek bir <c>web.config</c>'de yasar. O dosyayi silebilecek yollar panelin kendi icinde
/// sayilabiliyor: FTP kullanicisi (klasorde Modify izni var), dosya yoneticisi,
/// <c>git reset --hard</c> ile dagitim, SiteBuilder yayini, WordPress yeniden kurulumu. Panel
/// bunlarin hicbirini gormeden kendi kaydina bakip "korumali" diyordu.
/// </para>
/// <para>
/// Bu bir GUVENLIK YANLIS-GUVENCESIDIR: ekran yesil gorunurken klasor herkese aciktir ve
/// kullanicinin bunu fark etmesi icin hicbir sebep yoktur. Testler dosya sisteminde GERCEKTEN
/// olcum yapar (sahte bir soyutlama uzerinden degil) -- olculen sey zaten dosyanin kendisidir.
/// </para>
/// </summary>
public sealed class DirectoryProtectionMeasurementTests
{
    [Fact]
    public async Task AProtectedDirectory_IsReportedActive_WhileTheWebConfigIsInPlace()
    {
        using var alan = new Alan();
        var servis = alan.Servis();

        await servis.ProtectAsync("site", Istek("yonetim"));

        var liste = await servis.GetProtectedDirectoriesAsync("site");

        Assert.True(Assert.Single(liste).IsActive);
    }

    /// <summary>
    /// ASIL BULGU: web.config silindiginde panel artik "korumali" DEMEZ. Kayit yerinde kalir
    /// (kullanici adi/realm kaybolmasin, tek tikla yeniden uygulanabilsin) ama durumu YANLIS
    /// degil GERCEK olur.
    /// </summary>
    [Fact]
    public async Task WhenTheWebConfigIsDeleted_ThePanelStopsClaimingTheDirectoryIsProtected()
    {
        using var alan = new Alan();
        var servis = alan.Servis();
        await servis.ProtectAsync("site", Istek("yonetim"));

        // FTP kullanicisi / dosya yoneticisi / git dagitimi: dosya gider, kayit kalir.
        File.Delete(Path.Combine(alan.SiteKoku, "yonetim", "web.config"));

        var kayit = Assert.Single(await servis.GetProtectedDirectoriesAsync("site"));

        Assert.False(kayit.IsActive);
        Assert.Equal("yonetim", kayit.RelativePath);
        Assert.Equal("ali", kayit.UserName);
    }

    /// <summary>
    /// Dosya VAR ama BIZIM degil: SiteBuilder yayini / WordPress kurulumu kendi web.config'ini
    /// yazdiginda koruma kurali gitmistir. "Dosya var" ile "koruma var" ayni sey degildir.
    /// </summary>
    [Fact]
    public async Task WhenSomeoneElsesWebConfigReplacesOurs_ProtectionIsReportedAsGone()
    {
        using var alan = new Alan();
        var servis = alan.Servis();
        await servis.ProtectAsync("site", Istek("yonetim"));

        File.WriteAllText(
            Path.Combine(alan.SiteKoku, "yonetim", "web.config"),
            "<configuration><system.webServer /></configuration>");

        Assert.False(Assert.Single(await servis.GetProtectedDirectoriesAsync("site")).IsActive);
    }

    /// <summary>Klasorun tamami silinmisse de cevap ayni: koruma YOK (ve bu OLCULMUS bir gercektir).</summary>
    [Fact]
    public async Task WhenTheWholeFolderIsGone_ProtectionIsReportedAsGone()
    {
        using var alan = new Alan();
        var servis = alan.Servis();
        await servis.ProtectAsync("site", Istek("yonetim"));

        Directory.Delete(Path.Combine(alan.SiteKoku, "yonetim"), recursive: true);

        Assert.False(Assert.Single(await servis.GetProtectedDirectoriesAsync("site")).IsActive);
    }

    /// <summary>
    /// ORTAK KURAL: OLCEMEDIYSEN "YOK" DEME. Site kokU okunamiyorsa (IIS erisilemiyor) cevap
    /// <c>null</c>'dur -- "korumaniz gitti" alarmi DEGIL. Aksi hâlde IIS'in anlik bir arizasi,
    /// butun korumali dizinleri "acik" gosteren bir panik ekrani uretirdi.
    /// </summary>
    [Fact]
    public async Task WhenTheSiteRootCannotBeRead_TheStateIsUnknown_NotUnprotected()
    {
        using var alan = new Alan();
        await alan.Servis().ProtectAsync("site", Istek("yonetim"));

        // Ayni kayit dosyasi, ama IIS artik siteyi vermiyor.
        var kirikServis = alan.Servis(iisCalisiyorMu: false);

        Assert.Null(Assert.Single(await kirikServis.GetProtectedDirectoriesAsync("site")).IsActive);
    }

    /// <summary>Olcum fonksiyonunun sozlesmesi, cagiran zincirden bagimsiz olarak da sabitlenir.</summary>
    [Fact]
    public void MeasureProtection_WithoutASiteRoot_ReturnsUnknown()
    {
        Assert.Null(DirectoryProtectionService.MeasureProtection(null, "yonetim"));
        Assert.Null(DirectoryProtectionService.MeasureProtection("   ", "yonetim"));
    }

    /// <summary>
    /// Kayittaki goreli yol artik gecersizse (ust dizine cikan bir sey yazilmissa) sonuc
    /// "koruma yok" degil "olculemedi"dir: yol cozulemeden dosyaya hic bakilmamistir.
    /// </summary>
    [Fact]
    public void MeasureProtection_WithAnUnresolvablePath_ReturnsUnknown()
    {
        using var alan = new Alan();

        Assert.Null(DirectoryProtectionService.MeasureProtection(alan.SiteKoku, "../disari"));
    }

    private static ProtectDirectoryRequest Istek(string yol) =>
        new() { RelativePath = yol, UserName = "ali", Password = "gizli-parola", Realm = "Yonetim" };

    private sealed class Alan : IDisposable
    {
        private readonly string _ust;

        public Alan()
        {
            _ust = Path.Combine(Path.GetTempPath(), $"adapanel-protected-{Guid.NewGuid():N}");
            SiteKoku = Path.Combine(_ust, "site");
            VeriKoku = Path.Combine(_ust, "veri");
            Directory.CreateDirectory(SiteKoku);
            Directory.CreateDirectory(VeriKoku);
        }

        public string SiteKoku { get; }

        public string VeriKoku { get; }

        public DirectoryProtectionService Servis(bool iisCalisiyorMu = true) =>
            new(new SahteSiteServisi(SiteKoku, iisCalisiyorMu),
                // Tam nitelenmis: "Options" adi burada AdaPanel.Infrastructure.Options ad alaniyla cakisiyor.
                new JsonFileStore(Microsoft.Extensions.Options.Options.Create(
                    new PanelRuntimeOptions { DataRoot = VeriKoku })));

        public void Dispose()
        {
            try
            {
                Directory.Delete(_ust, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class SahteSiteServisi(string kok, bool calisiyorMu) : IIisSiteService
    {
        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) =>
            calisiyorMu
                ? Task.FromResult(new SiteDto { Name = siteName, PhysicalPath = kok, ApplicationRootPath = kok })
                : throw new AppException("sites.iis_not_available", 503, 503);

        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
