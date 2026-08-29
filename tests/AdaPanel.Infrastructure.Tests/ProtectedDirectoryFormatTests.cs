using System.Text;
using AdaPanel.Core.Dtos.Htaccess;
using AdaPanel.Infrastructure.Htaccess.Hosting;
using AdaPanel.Infrastructure.Htaccess.Htpasswd;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Korumali dizin BICIMI ve KAPI kararlari. Bu dosyanin konusu, panel ile IIS modulunun
/// PAYLASTIGI kaynak: biri yazar, oteki okur ve ikisi de bu dosyalardan derlenir.
/// </summary>
public sealed class ProtectedDirectoryFormatTests
{
    // ------------------------------------------------------------------ kapsam

    /// <summary>
    /// KAPSAM SINIRI. Duz bir StartsWith ile "yonetim" korumasi "yonetim-herkese-acik"
    /// klasorunu de kapatirdi; ayraç aranmasaydi "yonetimx" korumayi tasirdi. Ikisi de
    /// sessiz ve yanlis olurdu.
    /// </summary>
    [Theory]
    [InlineData("yonetim", "yonetim", true)]
    [InlineData("yonetim", "yonetim/x.html", true)]
    [InlineData("yonetim", "yonetim/alt/derin.php", true)]
    [InlineData("yonetim", "YONETIM/x.html", true)]
    [InlineData("yonetim", "yonetim-herkese-acik/x.html", false)]
    [InlineData("yonetim", "yonetimx", false)]
    [InlineData("yonetim", "baska/yonetim/x", false)]
    [InlineData("yonetim", "", false)]
    [InlineData("a/b", "a/b/c", true)]
    [InlineData("a/b", "a/bc", false)]
    public void Covers_RequiresAPathBoundary(string korunan, string istek, bool beklenen)
        => Assert.Equal(beklenen, ProtectedDirectoryFile.Covers(korunan, istek));

    /// <summary>Bos korunan yol HICBIR seyi kapsamaz: tek bozuk satir butun siteyi kilitleyemez.</summary>
    [Fact]
    public void AnEmptyProtectedPath_CoversNothing()
    {
        Assert.False(ProtectedDirectoryFile.Covers(string.Empty, "her/yer"));
        Assert.False(ProtectedDirectoryFile.Covers("/", "her/yer"));
    }

    // ------------------------------------------------------------------ ayristirici

    /// <summary>EKSIK ALANLI kayit KABUL EDILMEZ: dogrulanamayacak bir kapi acilmaz.</summary>
    [Theory]
    [InlineData("dir=yonetim\nrealm=R\nuser=ali")]              // passwd yok
    [InlineData("dir=yonetim\nrealm=R\npasswd=x.htpasswd")]     // user yok
    [InlineData("realm=R\nuser=ali\npasswd=x.htpasswd")]        // dir yok
    public void AnIncompleteRecord_IsDropped(string icerik)
        => Assert.Empty(ProtectedDirectoryFile.Parse(icerik));

    /// <summary>
    /// Parola dosyasi alani YALNIZ DOSYA ADI kabul eder. Yol kabul etseydi, betimleyiciye
    /// yazabilen biri panelin baska bir dosyasini "parola dosyasi" diye okutmayi deneyebilirdi.
    /// </summary>
    [Theory]
    [InlineData("../../config/panel.json")]
    [InlineData("C:\\ProgramData\\AdaPanel\\keys\\x")]
    public void APasswordFileFieldWithAPathSeparator_IsRejected(string kotu)
        => Assert.Empty(ProtectedDirectoryFile.Parse($"dir=yonetim\nrealm=R\nuser=ali\npasswd={kotu}"));

    /// <summary>
    /// SATIR ENJEKSIYONU: realm kullanicidan gelir. Satir sonlari temizlenmeseydi realm alanina
    /// yazilan "\ndir=/" ile BUTUN SITEYI kapsayan yeni bir kayit uretilebilirdi.
    /// </summary>
    [Fact]
    public void AMultiLineRealm_CannotInjectASecondRecord()
    {
        var icerik = ProtectedDirectoryFile.Render(new List<ProtectedDirectoryRecord>
        {
            new()
            {
                RelativePath = "yonetim",
                Realm = "Kotu\ndir=\nrealm=Hepsi\nuser=ali\npasswd=y.htpasswd",
                UserName = "ali",
                PasswordFileName = "x.htpasswd"
            }
        });

        var kayitlar = ProtectedDirectoryFile.Parse(icerik);

        Assert.Equal("yonetim", Assert.Single(kayitlar).RelativePath);
    }

    /// <summary>Yazilan ile okunan AYNI: panel ile modulun bicimi ayri dusmez.</summary>
    [Fact]
    public void RenderAndParse_RoundTrip()
    {
        var kaynak = new List<ProtectedDirectoryRecord>
        {
            new() { RelativePath = "a/b", Realm = "Gizli Alan", UserName = "ali", PasswordFileName = "a.htpasswd" },
            new() { RelativePath = "c", Realm = "C", UserName = "veli", PasswordFileName = "c.htpasswd" }
        };

        var okunan = ProtectedDirectoryFile.Parse(ProtectedDirectoryFile.Render(kaynak));

        Assert.Equal(2, okunan.Count);
        Assert.Equal("a/b", okunan[0].RelativePath);
        Assert.Equal("Gizli Alan", okunan[0].Realm);
        Assert.Equal("veli", okunan[1].UserName);
    }

    // ------------------------------------------------------------------ Basic basligi

    /// <summary>
    /// Parola IKI NOKTA icerebilir (RFC 7617: kullanici adi iceremez, parola icerebilir).
    /// Son iki noktadan bolmek, ':' iceren parolalari sessizce calismaz hale getirirdi.
    /// </summary>
    [Fact]
    public void BasicDecode_SplitsOnTheFirstColonOnly()
    {
        Assert.True(ProtectedDirectoryGate.BasicCoz(
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ali:a:b:c")),
            out var kullanici, out var parola));

        Assert.Equal("ali", kullanici);
        Assert.Equal("a:b:c", parola);
    }

    /// <summary>UTF-8 ile cozulur: Turkce karakterli parolalar sessizce yanlis eslesmemeli.</summary>
    [Fact]
    public void BasicDecode_UsesUtf8()
    {
        Assert.True(ProtectedDirectoryGate.BasicCoz(
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ali:gizliş")),
            out _, out var parola));

        Assert.Equal("gizliş", parola);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bearer abc")]
    [InlineData("Basic ***bozuk-base64***")]
    [InlineData("Basic " + "YWxp")] // iki nokta yok
    public void BasicDecode_RejectsAnythingElse_WithoutThrowing(string baslik)
        => Assert.False(ProtectedDirectoryGate.BasicCoz(baslik, out _, out _));

    // ------------------------------------------------------------------ bcrypt uretimi

    /// <summary>
    /// URETEN ile DOGRULAYAN ayni dosyada; uretilen ozet KENDI dogrulayicimizdan gecmeli.
    /// Gecmeseydi koruma kurulur ama dogru parola bile iceri alinmazdi.
    /// </summary>
    [Fact]
    public void AGeneratedHash_VerifiesWithOurOwnVerifier()
    {
        var ozet = BCryptVerifier.Generate("Gizli!2026Ada", 6);

        Assert.Equal(BCryptVerifier.Sonuc.Match, BCryptVerifier.Verify(ozet, "Gizli!2026Ada", 15));
        Assert.Equal(BCryptVerifier.Sonuc.NoMatch, BCryptVerifier.Verify(ozet, "Gizli!2026Adb", 15));
    }

    /// <summary>
    /// TUZ HER SEFERINDE FARKLI. Ayni parolanin iki kurulumu ayni ozeti verseydi, tuzun tek
    /// islevi (onceden hesaplanmis tablolari kirmak) ortadan kalkardi.
    /// </summary>
    [Fact]
    public void TwoHashesOfTheSamePassword_Differ()
        => Assert.NotEqual(BCryptVerifier.Generate("ayni", 4), BCryptVerifier.Generate("ayni", 4));

    /// <summary>Uretilen ozet, Apache htpasswd -B ile ayirt edilemez bicimde olmali.</summary>
    [Fact]
    public void AGeneratedHash_HasTheApacheBcryptShape()
    {
        var ozet = BCryptVerifier.Generate("x", 9);

        Assert.StartsWith("$2y$09$", ozet, StringComparison.Ordinal);
        Assert.Equal(60, ozet.Length);
    }

    /// <summary>
    /// Uretilen ozetin maliyeti, motorun kendi ust sinirini (MaxBcryptCost) ASMAMALI: assaydi
    /// dogrulayici kendi urettigimiz ozeti "maliyet cok yuksek" diye reddederdi.
    /// </summary>
    [Fact]
    public void TheProductionCost_StaysUnderTheEnginesOwnLimit()
        => Assert.True(ProtectedDirectoryStore.BcryptCost <= HtaccessLimits.Default.MaxBcryptCost);

    /// <summary>Gecersiz maliyet SESSIZCE kirpilmaz.</summary>
    [Fact]
    public void AnOutOfRangeCost_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => BCryptVerifier.Generate("x", 3));

    // ------------------------------------------------------------------ kapinin fail-open sinirlari

    /// <summary>
    /// Betimleyici YOKSA kapi hicbir seye dokunmaz. Fail-closed olsaydi, korumasiz butun
    /// siteler 401 dondururdu -- yani modulun kurulmasi sunucuyu kapatirdi.
    /// </summary>
    [Fact]
    public void WithoutADescriptor_TheGateDoesNothing()
    {
        var bos = Path.Combine(Path.GetTempPath(), $"adapanel-bos-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bos);
        try
        {
            var kapi = new ProtectedDirectoryGate(bos, TimeSpan.Zero);

            Assert.Equal(
                ProtectedDirectoryOutcome.NotProtected,
                kapi.Decide("site", "C:\\site", "C:\\site\\yonetim\\x", "/yonetim/x", null).Outcome);
        }
        finally
        {
            Directory.Delete(bos, recursive: true);
        }
    }

    /// <summary>Site adi bilinmiyorsa hangi kiracinin kuralini uygulayacagimizi da bilmiyoruz.</summary>
    [Fact]
    public void WithoutASiteName_TheGateDoesNothing()
        => Assert.Equal(
            ProtectedDirectoryOutcome.NotProtected,
            new ProtectedDirectoryGate(Path.GetTempPath(), TimeSpan.Zero)
                .Decide(null, "C:\\site", "C:\\site\\x", "/x", null).Outcome);
}
