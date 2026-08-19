using AdaPanel.Infrastructure.Iis;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// SİTE SİLERKEN HANGİ KLASÖR ÖZYİNELEMELİ SİLİNEBİLİR? (denetim K9 + Y8)
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN AÇIK:</b> eski <c>IsSafeToDelete</c> bir KARA LİSTEYDİ; yalnız <c>%WINDIR%</c> ve iki
/// segmentlik <c>Program Files</c> yollarını eliyordu. Fonksiyonun birebir kopyası çalıştırıldığında
/// tablo şuydu:
/// </para>
/// <code>
/// C:\Windows          False  ✓      C:\inetpub          True  ✗
/// C:\Program Files    False  ✓      C:\Users            True  ✗
///                                   C:\ProgramData      True  ✗
///                                   \\server\share      True  ✗
/// </code>
/// <para>
/// Yani fiziksel yolu <c>C:\inetpub\wwwroot</c> olan tek bir site,
/// <c>DELETE /sites/{ad}?deleteFiles=true</c> ile sunucudaki BÜTÜN siteleri götürüyor ve panel
/// <c>200 OK</c> dönüyordu. Aşağıdaki ilk test tam olarak o tablodur — artık dördü de REDDEDİLİYOR.
/// </para>
/// <para>
/// <b>İKİNCİ YÖN AYNI DERECEDE ÖNEMLİ:</b> kapı fazla dar olursa meşru site silme çalışmaz,
/// yönetici işi elle yapar ve bir dahakine onay ekranını okumadan tıklar. Bu yüzden burada
/// "reddedilmeli" kadar "silinebilmeli" durumları da ölçülüyor.
/// </para>
/// </remarks>
public sealed class SiteDeletionPathGuardTests
{
    /// <summary>Sistem sürücüsü ölçülür, "C:" diye VARSAYILMAZ.</summary>
    private static string SistemSurucusu =>
        Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";

    private static string SistemYolu(params string[] parcalar) =>
        Path.Combine(new[] { SistemSurucusu }.Concat(parcalar).ToArray());

    /// <summary>Panelin varsayılan kurulumundaki politika: site kökü <c>&lt;sürücü&gt;\inetpub\wwwroot</c>.</summary>
    private static SiteDeletionPathPolicy VarsayilanPolitika(
        IReadOnlyCollection<string>? baskaSiteYollari = null,
        IReadOnlyCollection<string>? adanmisAdlar = null) => new()
    {
        KnownSiteRoots = [SiteDeletionPathGuard.DefaultIisSiteRoot()],
        ForbiddenSubtrees = SiteDeletionPathGuard.SystemForbiddenSubtrees(SistemYolu("ProgramData", "AdaPanel")),
        ForbiddenContainers = SiteDeletionPathGuard.SystemForbiddenContainers(),
        OtherSitePaths = baskaSiteYollari ?? [],
        DedicatedFolderNames = adanmisAdlar ?? []
    };

    // ---- K9: raporun ölçüm tablosu, birebir ----

    [Fact]
    public void TheMeasuredTableFromTheAudit_IsRejectedInEveryRow()
    {
        var politika = VarsayilanPolitika();

        // Eskiden GEÇEN dört yol (rapordaki "True ✗" satırları).
        Assert.NotEqual(SiteDeletionPathVerdict.Deletable, SiteDeletionPathGuard.Evaluate(SistemYolu("inetpub"), politika));
        Assert.NotEqual(SiteDeletionPathVerdict.Deletable, SiteDeletionPathGuard.Evaluate(SistemYolu("Users"), politika));
        Assert.NotEqual(
            SiteDeletionPathVerdict.Deletable,
            SiteDeletionPathGuard.Evaluate(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), politika));
        Assert.Equal(
            SiteDeletionPathVerdict.NetworkPath,
            SiteDeletionPathGuard.Evaluate(@"\\server\share", politika));

        // Eskiden de REDDEDİLEN iki yol (rapordaki "False ✓" satırları) — koruma kaybolmadı.
        Assert.NotEqual(
            SiteDeletionPathVerdict.Deletable,
            SiteDeletionPathGuard.Evaluate(Environment.GetFolderPath(Environment.SpecialFolder.Windows), politika));
        Assert.NotEqual(
            SiteDeletionPathVerdict.Deletable,
            SiteDeletionPathGuard.Evaluate(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), politika));
    }

    /// <summary>
    /// AÇIĞIN TA KENDİSİ: fiziksel yolu site KÖKÜNE eşit olan site. Bu yol silinirse sunucudaki
    /// bütün sitelerin klasörleri gider.
    /// </summary>
    [Fact]
    public void ASiteWhosePathIsTheSiteRootItself_IsNeverDeleted()
    {
        Assert.Equal(
            SiteDeletionPathVerdict.SiteRootItself,
            SiteDeletionPathGuard.Evaluate(SiteDeletionPathGuard.DefaultIisSiteRoot(), VarsayilanPolitika()));
    }

    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\server\share\musteri")]
    [InlineData(@"\\?\C:\inetpub\wwwroot\musteri.com")]
    [InlineData(@"\\.\PhysicalDrive0")]
    public void NetworkAndDevicePaths_AreRejected(string yol)
    {
        Assert.NotEqual(SiteDeletionPathVerdict.Deletable, SiteDeletionPathGuard.Evaluate(yol, VarsayilanPolitika()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("httpdocs")]          // göreli yol: neye çözüleceği çalışma dizinine bağlı
    [InlineData(@"..\..\Windows")]
    public void RelativeOrUnreadablePaths_AreRejected(string yol)
    {
        Assert.Equal(SiteDeletionPathVerdict.InvalidPath, SiteDeletionPathGuard.Evaluate(yol, VarsayilanPolitika()));
    }

    [Fact]
    public void ADriveRoot_IsRejected()
    {
        Assert.Equal(SiteDeletionPathVerdict.TooShallow, SiteDeletionPathGuard.Evaluate(SistemSurucusu, VarsayilanPolitika()));
        Assert.Equal(SiteDeletionPathVerdict.TooShallow, SiteDeletionPathGuard.Evaluate(@"D:\", VarsayilanPolitika()));
    }

    /// <summary>Panel verisi (veritabanı, anahtarlar, YEDEKLER) taşınmış olsa bile korunur.</summary>
    [Fact]
    public void TheRelocatedPanelDataRoot_IsProtectedToo()
    {
        var politika = VarsayilanPolitika() with
        {
            ForbiddenSubtrees = SiteDeletionPathGuard.SystemForbiddenSubtrees(@"D:\AdaPanelVeri")
        };

        Assert.Equal(SiteDeletionPathVerdict.SystemPath, SiteDeletionPathGuard.Evaluate(@"D:\AdaPanelVeri", politika));
        Assert.Equal(SiteDeletionPathVerdict.SystemPath, SiteDeletionPathGuard.Evaluate(@"D:\AdaPanelVeri\backups", politika));
    }

    // ---- İKİNCİ YÖN: meşru silme ÇALIŞMALI ----

    [Theory]
    [InlineData("musteri.com")]
    [InlineData("musteri.com\\public")]
    [InlineData("bir\\iki\\uc")]
    public void AFolderUnderThePanelSiteRoot_IsDeletable(string goreli)
    {
        var yol = Path.Combine(SiteDeletionPathGuard.DefaultIisSiteRoot(), goreli);

        Assert.Equal(SiteDeletionPathVerdict.Deletable, SiteDeletionPathGuard.Evaluate(yol, VarsayilanPolitika()));
    }

    /// <summary>
    /// Yönetici siteyi başka bir sürücüye koyabilir; panelin tanımadığı bir kök altında da olsa
    /// üst klasörü bir KAPSAYICI olduğu sürece silme meşrudur.
    /// </summary>
    [Theory]
    [InlineData(@"D:\web\musteri.com")]
    [InlineData(@"E:\musteriler\a\site")]
    public void ASiteUnderACustomContainer_IsDeletable(string yol)
    {
        Assert.Equal(SiteDeletionPathVerdict.Deletable, SiteDeletionPathGuard.Evaluate(yol, VarsayilanPolitika()));
    }

    /// <summary>
    /// Sürücü kökünün hemen altındaki tek seviyelik klasör (<c>D:\Yedekler</c>) tek başına
    /// silinemez — bu, sunucunun ortak veri klasörü olabilir ve K9'un sınıfı tam olarak budur.
    /// AMA klasörün adı sitenin adıysa ADANMIŞLIK kanıtlanmıştır ve silme çalışır.
    /// </summary>
    [Fact]
    public void ATopLevelFolder_NeedsProofThatItBelongsToTheSite()
    {
        Assert.Equal(
            SiteDeletionPathVerdict.UnknownRoot,
            SiteDeletionPathGuard.Evaluate(@"D:\Yedekler", VarsayilanPolitika(adanmisAdlar: ["musteri.com"])));

        Assert.Equal(
            SiteDeletionPathVerdict.Deletable,
            SiteDeletionPathGuard.Evaluate(@"D:\musteri.com", VarsayilanPolitika(adanmisAdlar: ["musteri.com"])));
    }

    /// <summary>Adanmışlık kanıtı kapıyı yalnızca GENİŞLETİR: yasak yolları asla açamaz.</summary>
    [Fact]
    public void TheDedicatedNameProof_CannotOpenAForbiddenPath()
    {
        // Site adı "Users" olsa bile.
        Assert.NotEqual(
            SiteDeletionPathVerdict.Deletable,
            SiteDeletionPathGuard.Evaluate(SistemYolu("Users"), VarsayilanPolitika(adanmisAdlar: ["Users"])));

        Assert.NotEqual(
            SiteDeletionPathVerdict.Deletable,
            SiteDeletionPathGuard.Evaluate(SistemYolu("inetpub"), VarsayilanPolitika(adanmisAdlar: ["inetpub"])));
    }

    /// <summary>
    /// Ön ek tuzağı: "…\site1" silinirken "…\site1-evil" ETKİLENMEMELİ. Kapsama sorusu
    /// <c>PathContainment</c>'a sorulduğu için düz <c>StartsWith</c> hatası buraya sızamaz.
    /// </summary>
    [Fact]
    public void ASiblingWithASharedPrefix_IsNotTreatedAsInside()
    {
        var politika = VarsayilanPolitika(baskaSiteYollari: [@"D:\web\site1-evil"]);

        Assert.Equal(SiteDeletionPathVerdict.Deletable, SiteDeletionPathGuard.Evaluate(@"D:\web\site1", politika));
    }

    // ---- Y8: klasör başka bir siteye de ait mi? ----

    [Fact]
    public void AFolderThatContainsAnotherSite_IsNeverDeleted()
    {
        var politika = VarsayilanPolitika(baskaSiteYollari: [@"D:\web\musteri.com\alt-site"]);

        Assert.Equal(
            SiteDeletionPathVerdict.SharedWithAnotherSite,
            SiteDeletionPathGuard.Evaluate(@"D:\web\musteri.com", politika));
    }

    [Fact]
    public void AFolderSharedWithAnotherSite_IsNeverDeleted()
    {
        // İki site AYNI fiziksel yolu gösteriyor: birini silmek diğerini de boşaltırdı.
        var politika = VarsayilanPolitika(baskaSiteYollari: [@"D:\web\ortak"]);

        Assert.Equal(
            SiteDeletionPathVerdict.SharedWithAnotherSite,
            SiteDeletionPathGuard.Evaluate(@"D:\web\ortak", politika));
    }

    /// <summary>Başka sitenin klasörünün ALTINDA kalmak sorun değil: yalnız kendi alt klasörümüzü sileriz.</summary>
    [Fact]
    public void BeingInsideAnotherSiteFolder_DoesNotBlockDeletingOurOwnSubfolder()
    {
        var politika = VarsayilanPolitika(baskaSiteYollari: [@"D:\web\ust"]);

        Assert.Equal(SiteDeletionPathVerdict.Deletable, SiteDeletionPathGuard.Evaluate(@"D:\web\ust\alt", politika));
    }

    // ---- Y8: belge kökünün bir seviye YUKARI taşınması (Laravel "public") ----

    [Fact]
    public void TheApplicationRootLift_IsRefusedWhenTheParentIsASiteRoot()
    {
        var politika = VarsayilanPolitika();

        // C:\inetpub\wwwroot\public → üst klasör BÜTÜN sitelerin kökü olurdu.
        Assert.False(SiteDeletionPathGuard.IsUsableApplicationRoot(SiteDeletionPathGuard.DefaultIisSiteRoot(), politika));
        Assert.False(SiteDeletionPathGuard.IsUsableApplicationRoot(SistemYolu("inetpub"), politika));
        Assert.False(SiteDeletionPathGuard.IsUsableApplicationRoot(SistemYolu("Users"), politika));
        Assert.False(SiteDeletionPathGuard.IsUsableApplicationRoot(@"\\server\share", politika));
    }

    /// <summary>...ama gerçek bir Laravel kökü tek seviyede bile olsa YÜKSELTİLİR (fazla dar olmasın).</summary>
    [Theory]
    [InlineData(@"D:\laravel")]
    [InlineData(@"D:\web\laravel")]
    public void TheApplicationRootLift_StillWorksForARealApplicationRoot(string yol)
    {
        Assert.True(SiteDeletionPathGuard.IsUsableApplicationRoot(yol, VarsayilanPolitika()));
    }

    // ---- Kapsayıcı kuralı ----

    [Theory]
    [InlineData(@"D:\web", true)]
    [InlineData(@"D:\web\musteriler", true)]
    [InlineData(@"D:\", false)]              // SÜRÜCÜ KÖKÜ kapsayıcı sayılmaz: K9'un kapısı burasıdır
    [InlineData(@"\\server\share\web", false)]
    public void OnlyARealContainerCanJustifyDeletingWhatIsInsideIt(string yol, bool beklenen)
    {
        Assert.Equal(beklenen, SiteDeletionPathGuard.CanContainSites(yol, VarsayilanPolitika()));
    }

    // ---- KABLOLAMA: kapı doğru olsa da çağrılmıyorsa hiçbir şey korunmaz ----

    /// <summary>
    /// Cascade silme, klasörü SİLMEDEN ÖNCE kapıya sorar ve eski kara liste
    /// (<c>IsSafeToDelete</c>) depoda kalmaz — iki kural bir arada yaşarsa hangisinin geçerli
    /// olduğu belirsizleşir.
    /// </summary>
    [Fact]
    public void TheCascade_AsksTheGuardBeforeDeletingAnyFolder()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Iis", "SiteCascadeDeletionService.cs");

        Assert.Contains("var karar = SiteDeletionPathGuard.Evaluate(full, policy);", kaynak, StringComparison.Ordinal);
        Assert.Contains("if (karar != SiteDeletionPathVerdict.Deletable)", kaynak, StringComparison.Ordinal);

        // Eski kara liste gitti (yorumlarda adı geçebilir; ÇAĞRILAN kod aranıyor).
        Assert.DoesNotContain("private static bool IsSafeToDelete", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// Y8 girdisi ÖLÇÜLEREK kuruluyor: başka sitelerin yolları okunamazsa dosya silme HİÇ yapılmaz.
    /// "Ölçemedik" ile "sorun yok" aynı şey değildir.
    /// </summary>
    [Fact]
    public void WhenTheOtherSitesCannotBeRead_NoFileIsDeleted()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Iis", "SiteCascadeDeletionService.cs");

        Assert.Contains("OtherSitePaths = baskaSiteYollari", kaynak, StringComparison.Ordinal);
        Assert.Contains("if (deleteFiles && silmePolitikasi is not null)", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// Belge kökünün bir seviye yukarı taşınması artık kapıdan geçiyor (Y8). Gerçek bir Laravel
    /// ağacıyla ölçülür: <c>artisan</c> + <c>composer.json</c> varsa yükseltme YAPILIR.
    /// </summary>
    [Fact]
    public void TheApplicationRootLift_StillLiftsARealLaravelTree()
    {
        var kok = Path.Combine(Path.GetTempPath(), $"ada-laravel-{Guid.NewGuid():N}");
        var belgeKoku = Path.Combine(kok, "public");
        try
        {
            Directory.CreateDirectory(belgeKoku);
            File.WriteAllText(Path.Combine(kok, "artisan"), "#!/usr/bin/env php");
            File.WriteAllText(Path.Combine(kok, "composer.json"), "{}");

            Assert.Equal(kok, WebAdministrationSiteService.ResolveApplicationRootPath(belgeKoku));
        }
        finally
        {
            try { Directory.Delete(kok, recursive: true); } catch { /* en iyi çaba */ }
        }
    }

    /// <summary>...ve kanıt eksikse (yalnız klasör adı "public") yükseltme YAPILMAZ.</summary>
    [Fact]
    public void TheApplicationRootLift_NeedsMoreThanTheFolderBeingCalledPublic()
    {
        var kok = Path.Combine(Path.GetTempPath(), $"ada-public-{Guid.NewGuid():N}");
        var belgeKoku = Path.Combine(kok, "public");
        try
        {
            Directory.CreateDirectory(belgeKoku);

            Assert.Equal(belgeKoku, WebAdministrationSiteService.ResolveApplicationRootPath(belgeKoku));
        }
        finally
        {
            try { Directory.Delete(kok, recursive: true); } catch { /* en iyi çaba */ }
        }
    }

    /// <summary>Ve yükseltme kararı kapıya soruluyor — kanıt tam olsa bile üst klasör site kökü olamaz.</summary>
    [Fact]
    public void TheApplicationRootLift_ConsultsTheGuard()
    {
        var kaynak = OkuKaynak("src", "AdaPanel.Infrastructure", "Iis", "WebAdministrationSiteService.cs");

        Assert.Contains("SiteDeletionPathGuard.IsUsableApplicationRoot(parent, ApplicationRootPolicy)", kaynak, StringComparison.Ordinal);
    }

    private static string OkuKaynak(params string[] parcalar)
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            var aday = Path.Combine(new[] { klasor }.Concat(parcalar).ToArray());
            if (File.Exists(aday))
            {
                return File.ReadAllText(aday);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new FileNotFoundException(string.Join('/', parcalar));
    }

    /// <summary>Yasak kapsayıcılar hiçbir zaman "kapsayıcı" olarak kullanılamaz.</summary>
    [Fact]
    public void ForbiddenContainers_CannotJustifyDeletingWhatIsInsideThem()
    {
        var politika = VarsayilanPolitika();

        Assert.False(SiteDeletionPathGuard.CanContainSites(SistemYolu("inetpub"), politika));
        Assert.False(SiteDeletionPathGuard.CanContainSites(SistemYolu("Users"), politika));
        Assert.False(SiteDeletionPathGuard.CanContainSites(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), politika));
    }
}
