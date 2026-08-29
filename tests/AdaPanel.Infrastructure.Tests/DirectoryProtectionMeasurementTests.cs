using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Htaccess.Hosting;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// KORUMALI DIZIN: parola artik DUZ METIN saklanmiyor.
/// <para>
/// Eski tasarim korunan klasore bir web.config yaziyor ve kurala
/// <c>^Basic base64(kullanici:parola)$</c> gomuyordu. Canli olculdu:
/// <c>YWRha3VsbGFuaWNpOkdpemxpITIwMjZBZGE=</c> -> <c>adakullanici:Gizli!2026Ada</c>.
/// Dosya MUSTERININ SITE KOKUNDE oldugu icin parolayi FTP kullanicisi, sitedeki her PHP betigi
/// ve yedegi indiren herkes okuyabiliyordu.
/// </para>
/// <para>
/// Bu testler DOSYA SISTEMINDE gercekten olcum yapar (sahte soyutlama uzerinden degil):
/// olculen sey zaten uretilen dosyalarin kendisidir.
/// </para>
/// </summary>
public sealed class DirectoryProtectionMeasurementTests
{
    // ------------------------------------------------------------------ ASIL BULGU

    /// <summary>
    /// NOBETCI TEST. Uretilen HICBIR dosya parolayi -- ne duz metin ne base64 -- tasimaz.
    /// Bu test kirmiziya donerse aciklik geri gelmis demektir.
    /// </summary>
    [Fact]
    public async Task NoFileWrittenAnywhere_ContainsThePasswordInPlaintextOrBase64()
    {
        using var alan = new Alan();
        await alan.Servis().ProtectAsync("site", Istek("yonetim"));

        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("ali:Gizli!2026Ada"));

        foreach (var dosya in alan.TumDosyalar())
        {
            var icerik = File.ReadAllText(dosya);
            Assert.DoesNotContain("Gizli!2026Ada", icerik, StringComparison.Ordinal);
            Assert.DoesNotContain(base64, icerik, StringComparison.Ordinal);

            // base64 dolgu karakteri kirpilarak yazilsaydi da yakalanmali.
            Assert.DoesNotContain(base64.TrimEnd('='), icerik, StringComparison.Ordinal);
        }
    }

    /// <summary>Sitenin KENDI kokunde hicbir sey birakilmaz: yedege, FTP'ye, PHP'ye sizacak dosya yok.</summary>
    [Fact]
    public async Task NothingIsWrittenIntoTheCustomersSiteRoot()
    {
        using var alan = new Alan();
        await alan.Servis().ProtectAsync("site", Istek("yonetim"));

        Assert.Empty(Directory.GetFiles(alan.SiteKoku, "*", SearchOption.AllDirectories));
    }

    /// <summary>Parola dosyasi bcrypt tasir ve o ozet gercekten dogru parolayi dogrular.</summary>
    [Fact]
    public async Task ThePasswordIsStoredAsABcryptHashThatVerifies()
    {
        using var alan = new Alan();
        await alan.Servis().ProtectAsync("site", Istek("yonetim"));

        var satir = File.ReadAllLines(alan.ParolaDosyasi("yonetim"))
            .First(item => item.Length > 0 && item[0] != '#');
        var ozet = satir[(satir.IndexOf(':') + 1)..];

        Assert.StartsWith("$2y$10$", ozet, StringComparison.Ordinal);
        Assert.Equal("ali", satir[..satir.IndexOf(':')]);

        // Ozet, ISTEK YOLUNDAKI dogrulayicinin kabul ettigi ozettir: uretici ile dogrulayicinin
        // ayri dusmedigi BURADA olculur.
        Assert.True(alan.Kapi().Decide("site", alan.SiteKoku, Path.Combine(alan.SiteKoku, "yonetim", "x.html"),
            "/yonetim/x.html", Yetki("ali", "Gizli!2026Ada")).Outcome == ProtectedDirectoryOutcome.Granted);
    }

    // ------------------------------------------------------------------ istek yolundaki karar

    [Fact]
    public async Task WithoutCredentials_TheGateChallenges()
    {
        using var alan = new Alan();
        await alan.Servis().ProtectAsync("site", Istek("yonetim"));

        var karar = alan.Kapi().Decide("site", alan.SiteKoku,
            Path.Combine(alan.SiteKoku, "yonetim", "x.html"), "/yonetim/x.html", null);

        Assert.Equal(ProtectedDirectoryOutcome.Challenge, karar.Outcome);
        Assert.Equal("Yonetim", karar.Realm);
    }

    [Fact]
    public async Task WithTheWrongPassword_TheGateChallenges()
    {
        using var alan = new Alan();
        await alan.Servis().ProtectAsync("site", Istek("yonetim"));

        Assert.Equal(
            ProtectedDirectoryOutcome.Challenge,
            alan.Kapi().Decide("site", alan.SiteKoku, Path.Combine(alan.SiteKoku, "yonetim", "x.html"),
                "/yonetim/x.html", Yetki("ali", "yanlis")).Outcome);
    }

    /// <summary>Korunan klasorun DISI etkilenmez: koruma tek bir agaci kapatir, siteyi degil.</summary>
    [Fact]
    public async Task OutsideTheProtectedFolder_TheGateDoesNothing()
    {
        using var alan = new Alan();
        await alan.Servis().ProtectAsync("site", Istek("yonetim"));

        Assert.Equal(
            ProtectedDirectoryOutcome.NotProtected,
            alan.Kapi().Decide("site", alan.SiteKoku, Path.Combine(alan.SiteKoku, "index.html"), "/index.html", null).Outcome);
    }

    /// <summary>Koruma KALDIRILINCA klasor gercekten acilir (kayit da parola dosyasi da gider).</summary>
    [Fact]
    public async Task AfterUnprotect_TheGateStopsChallenging()
    {
        using var alan = new Alan();
        var servis = alan.Servis();
        await servis.ProtectAsync("site", Istek("yonetim"));

        await servis.UnprotectAsync("site", "yonetim");

        Assert.Equal(
            ProtectedDirectoryOutcome.NotProtected,
            alan.Kapi().Decide("site", alan.SiteKoku, Path.Combine(alan.SiteKoku, "yonetim", "x.html"),
                "/yonetim/x.html", null).Outcome);
        Assert.False(File.Exists(alan.ParolaDosyasi("yonetim")));
        Assert.Empty(await servis.GetProtectedDirectoriesAsync("site"));
    }

    // ------------------------------------------------------------------ durustluk

    /// <summary>
    /// MODUL IIS'E KAYITLI DEGILSE koruma KURULMAZ. Dogrulamayi modul yapiyor; kayit yoksa
    /// yazilan dosyalar hicbir istegi etkilemez ve panel "korumali" derken klasor herkese acik olur.
    /// </summary>
    [Fact]
    public async Task WhenTheIisModuleIsNotRegistered_ProtectionIsRefused_NotFaked()
    {
        using var alan = new Alan();

        var hata = await Assert.ThrowsAsync<AppException>(
            () => alan.Servis(modulKayitli: false).ProtectAsync("site", Istek("yonetim")));

        Assert.Equal("protected.engine_not_ready", hata.MessageKey);
        Assert.False(Directory.Exists(ProtectedDirectoryFile.SiteRoot(alan.VeriKoku, "site")));
    }

    /// <summary>
    /// Kayit VAR ama modul artik kayitli degil: panel "korumali" DEMEZ. Ekranin yesil gorunurken
    /// klasorun acik olmasi, bu servisin daha once kapattigi hata sinifinin ta kendisi.
    /// </summary>
    [Fact]
    public async Task WhenTheModuleRegistrationDisappears_TheDirectoryIsNoLongerReportedActive()
    {
        using var alan = new Alan();
        await alan.Servis().ProtectAsync("site", Istek("yonetim"));

        Assert.False(Assert.Single(await alan.Servis(modulKayitli: false).GetProtectedDirectoriesAsync("site")).IsActive);
    }

    /// <summary>Kayit OKUNAMIYORSA cevap "koruma yok" degil "olculemedi"dir.</summary>
    [Fact]
    public async Task WhenTheModuleRegistrationCannotBeRead_TheStateIsUnknown_NotUnprotected()
    {
        using var alan = new Alan();
        await alan.Servis().ProtectAsync("site", Istek("yonetim"));

        Assert.Null(Assert.Single(await alan.Servis(kayitOkunabilir: false).GetProtectedDirectoriesAsync("site")).IsActive);
    }

    /// <summary>Parola dosyasi elle silinmisse koruma UYGULANMAZ; panel de bunu yesil gostermez.</summary>
    [Fact]
    public async Task WhenThePasswordFileIsDeleted_TheDirectoryIsNotReportedActive()
    {
        using var alan = new Alan();
        var servis = alan.Servis();
        await servis.ProtectAsync("site", Istek("yonetim"));

        File.Delete(alan.ParolaDosyasi("yonetim"));

        Assert.False(Assert.Single(await servis.GetProtectedDirectoriesAsync("site")).IsActive);

        // Ve kapi FAIL-CLOSED davranir: korumanin VARLIGI olculdukten sonra dogrulayamamak,
        // istegi iceri almak icin sebep degildir.
        Assert.Equal(
            ProtectedDirectoryOutcome.Challenge,
            alan.Kapi().Decide("site", alan.SiteKoku, Path.Combine(alan.SiteKoku, "yonetim", "x.html"),
                "/yonetim/x.html", Yetki("ali", "Gizli!2026Ada")).Outcome);
    }

    /// <summary>Var olmayan klasor "korundu" diye raporlanmaz.</summary>
    [Fact]
    public async Task ProtectingAMissingDirectory_IsRefused()
    {
        using var alan = new Alan();

        var hata = await Assert.ThrowsAsync<AppException>(
            () => alan.Servis().ProtectAsync("site", Istek("hic-olmayan")));

        Assert.Equal("protected.directory_not_found", hata.MessageKey);
    }

    /// <summary>
    /// Kullanici adindaki iki nokta REDDEDILIR: .htpasswd satiri "kullanici:ozet" oldugu icin
    /// kabul edilseydi dosyada baska bir kullanici/ozet cifti gibi okunabilirdi.
    /// </summary>
    [Theory]
    [InlineData("ali:kotu")]
    [InlineData("ali\nadmin")]
    public async Task AUserNameThatWouldForgeTheFileFormat_IsRefused(string ad)
    {
        using var alan = new Alan();
        var istek = Istek("yonetim");
        istek.UserName = ad;

        var hata = await Assert.ThrowsAsync<AppException>(() => alan.Servis().ProtectAsync("site", istek));
        Assert.Equal("protected.invalid_user_name", hata.MessageKey);
    }

    // ------------------------------------------------------------------ gecis (eski duz metin)

    /// <summary>
    /// GECIS: eski duz metin korumasi otomatik donusturulur, web.config SILINIR ve parola
    /// DEGISMEZ (kullanici hicbir sey yapmaz). Base64 geri cozulebildigi icin bu mumkun --
    /// aciklik da tam olarak budur.
    /// </summary>
    [Fact]
    public async Task ALegacyPlaintextProtection_IsConvertedToBcrypt_AndThePlaintextFileIsDeleted()
    {
        using var alan = new Alan();
        alan.EskiKorumaYaz("yonetim", "eskikullanici", "EskiParola!42");

        var liste = await alan.Servis().GetProtectedDirectoriesAsync("site");

        var kayit = Assert.Single(liste);
        Assert.False(kayit.IsLegacyPlaintext);
        Assert.Equal("eskikullanici", kayit.UserName);
        Assert.False(File.Exists(Path.Combine(alan.SiteKoku, "yonetim", "web.config")));

        // ESKI PAROLA CALISMAYA DEVAM EDER: gecis kullaniciyi disari kilitlemez.
        Assert.Equal(
            ProtectedDirectoryOutcome.Granted,
            alan.Kapi().Decide("site", alan.SiteKoku, Path.Combine(alan.SiteKoku, "yonetim", "x.html"),
                "/yonetim/x.html", Yetki("eskikullanici", "EskiParola!42")).Outcome);
    }

    /// <summary>
    /// MODUL YOKKEN DONUSTURULMEZ. Donusturmek, calisan bir URL Rewrite korumasini kaldirip
    /// yerine hicbir sey koymamak olurdu -- klasor herkese acilirdi. Bunun yerine kayit
    /// GORUNUR kalir ve duz metin oldugu SOYLENIR.
    /// </summary>
    [Fact]
    public async Task WithoutTheModule_ALegacyProtectionIsLeftIntact_ButReportedAsPlaintext()
    {
        using var alan = new Alan();
        alan.EskiKorumaYaz("yonetim", "eskikullanici", "EskiParola!42");

        var kayit = Assert.Single(await alan.Servis(modulKayitli: false).GetProtectedDirectoriesAsync("site"));

        Assert.True(kayit.IsLegacyPlaintext);
        Assert.True(File.Exists(Path.Combine(alan.SiteKoku, "yonetim", "web.config")));
    }

    /// <summary>Eski bicimin gercekten geri cozulebildigi (yani gizlemedigi) ayrica sabitlenir.</summary>
    [Fact]
    public void TheLegacyFormatIsReversible_WhichIsTheFindingItself()
    {
        using var alan = new Alan();
        alan.EskiKorumaYaz("yonetim", "adakullanici", "Gizli!2026Ada");

        var kimlik = DirectoryProtectionService.LegacyKimlikCoz(
            Path.Combine(alan.SiteKoku, "yonetim", "web.config"));

        Assert.Equal(("adakullanici", "Gizli!2026Ada"), kimlik);
    }

    // ------------------------------------------------------------------ yardimcilar

    private static string Yetki(string kullanici, string parola)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(kullanici + ":" + parola));

    private static ProtectDirectoryRequest Istek(string yol) =>
        new() { RelativePath = yol, UserName = "ali", Password = "Gizli!2026Ada", Realm = "Yonetim" };

    private sealed class Alan : IDisposable
    {
        private readonly string _ust;

        public Alan()
        {
            _ust = Path.Combine(Path.GetTempPath(), $"adapanel-protected-{Guid.NewGuid():N}");
            SiteKoku = Path.Combine(_ust, "site");
            VeriKoku = Path.Combine(_ust, "veri");
            Directory.CreateDirectory(Path.Combine(SiteKoku, "yonetim"));
            Directory.CreateDirectory(VeriKoku);
        }

        public string SiteKoku { get; }

        public string VeriKoku { get; }

        public DirectoryProtectionService Servis(
            bool iisCalisiyorMu = true, bool modulKayitli = true, bool kayitOkunabilir = true) =>
            new(new SahteSiteServisi(SiteKoku, iisCalisiyorMu),
                // Tam nitelenmis: "Options" adi burada AdaPanel.Infrastructure.Options ad alaniyla cakisiyor.
                new JsonFileStore(Microsoft.Extensions.Options.Options.Create(
                    new PanelRuntimeOptions { DataRoot = VeriKoku })),
                new SahteKayit(modulKayitli, kayitOkunabilir),
                Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = VeriKoku }));

        /// <summary>Modulun istek yolunda kullandigi kapinin AYNISI (TTL sifir: onbellek testi bozmasin).</summary>
        public ProtectedDirectoryGate Kapi() => new(VeriKoku, TimeSpan.Zero);

        public string ParolaDosyasi(string goreliYol) => Path.Combine(
            ProtectedDirectoryFile.SiteRoot(VeriKoku, "site"),
            ProtectedDirectoryFile.PasswordFileName(goreliYol));

        public IEnumerable<string> TumDosyalar() =>
            Directory.GetFiles(_ust, "*", SearchOption.AllDirectories);

        /// <summary>0.7.9'un URETTIGI dosyanin birebir kalibi (canli olcumden kopyalandi).</summary>
        public void EskiKorumaYaz(string goreliYol, string kullanici, string parola)
        {
            var klasor = Path.Combine(SiteKoku, goreliYol);
            Directory.CreateDirectory(klasor);
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{kullanici}:{parola}"))
                .Replace("=", "\\=").Replace("+", "\\+").Replace("/", "\\/");

            // $$ ve {{...}}: ham dizede tek $ kullanilsaydi {HTTP_AUTHORIZATION} bir enterpolasyon
            // deligi sayilirdi; oysa o, URL Rewrite'in KENDI degiskeni ve birebir yazilmali.
            File.WriteAllText(Path.Combine(klasor, "web.config"), $$"""
                <?xml version="1.0" encoding="utf-8"?>
                <!-- adapanel-protected -->
                <configuration>
                  <system.webServer>
                    <rewrite>
                      <rules>
                        <rule name="adapanel-basic-auth" stopProcessing="true">
                          <match url="(.*)" />
                          <conditions>
                            <add input="{HTTP_AUTHORIZATION}" pattern="^Basic {{token}}$" negate="true" />
                          </conditions>
                          <action type="CustomResponse" statusCode="401" statusReason="Unauthorized" statusDescription="Authentication required" />
                        </rule>
                      </rules>
                    </rewrite>
                  </system.webServer>
                </configuration>
                """);

            Directory.CreateDirectory(Path.Combine(VeriKoku, "config"));
            File.WriteAllText(
                Path.Combine(VeriKoku, "config", "protected-dirs.json"),
                "[{\"siteName\":\"site\",\"relativePath\":\"" + goreliYol +
                "\",\"userName\":\"" + kullanici + "\",\"realm\":\"Eski\"}]");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_ust, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>IIS kaydinin uc degerli olcumu: kayitli / degil / OKUNAMADI.</summary>
    private sealed class SahteKayit(bool kayitli, bool okunabilir) : IHtaccessModuleRegistry
    {
        public HtaccessModuleRegistrationState Read() => new()
        {
            Readable = okunabilir,
            Present = kayitli,
            TypeName = kayitli ? HtaccessModuleRegistration.TypeName : string.Empty,
            PreCondition = HtaccessModuleRegistration.PreCondition
        };

        public HtaccessModuleRegistrationState Register() => throw new NotSupportedException();
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
