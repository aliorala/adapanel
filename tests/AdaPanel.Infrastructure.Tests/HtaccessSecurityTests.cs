using System.Diagnostics;
using AdaPanel.Core.Dtos.Htaccess;
using AdaPanel.Infrastructure.Htaccess;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Motorun PAZARLIK DIŞI güvenlik sözleşmesi.
/// </summary>
/// <remarks>
/// Bu kod barındırılan her sitenin HER isteğinde çalışır ve <c>.htaccess</c>'i MÜŞTERİ yazar.
/// Buradaki her test, tek bir kiracının sunucudaki bütün siteleri düşürmesini ya da kendi
/// alanının dışına çıkmasını engelleyen bir savunmayı sabitler.
/// </remarks>
public sealed class HtaccessSecurityTests
{
    // ---------------------------------------------------------------- ReDoS

    /// <summary>
    /// Klasik üstel geri-izleme kalıbı: <c>(a+)+$</c> + hiç eşleşmeyen uzun girdi.
    /// Zaman aşımı olmasaydı bu tek istek CPU'yu dakikalarca meşgul ederdi.
    /// </summary>
    [Fact]
    public void Patolojik_desen_zaman_asimina_ugrar_cokmez_ve_bildirilir()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("""
RewriteEngine On
RewriteRule ^(a+)+$ /tuzak.php [L]
""");

        var uzunGirdi = "/" + new string('a', 60) + "!";
        var sinirlar = HtaccessLimits.Default with { RegexTimeout = TimeSpan.FromMilliseconds(100) };

        var kronometre = Stopwatch.StartNew();
        var sonuc = site.Engine.Evaluate(site.Request(uzunGirdi, sinirlar: sinirlar));
        kronometre.Stop();

        Assert.True(sonuc.EngineFailed);
        Assert.Equal(HtaccessDiagnosticCodes.RegexTimeout, sonuc.FailureCode);
        Assert.Equal(HtaccessOutcome.Failed, sonuc.Outcome);
        // Zaman aşımı GERÇEKTEN kesiyor mu: sınırın birkaç katından uzun sürmemeli.
        Assert.True(kronometre.Elapsed < TimeSpan.FromSeconds(5), $"süre: {kronometre.Elapsed}");
    }

    [Fact]
    public void Patolojik_kosul_deseni_de_zaman_asimina_ugrar()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("""
RewriteEngine On
RewriteCond %{HTTP_USER_AGENT} ^(x+x+)+y$
RewriteRule ^.*$ /engel.php [L]
""");

        var basliklar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = new string('x', 60)
        };
        var sinirlar = HtaccessLimits.Default with { RegexTimeout = TimeSpan.FromMilliseconds(100) };

        var kronometre = Stopwatch.StartNew();
        var sonuc = site.Engine.Evaluate(site.Request("/", basliklar: basliklar, sinirlar: sinirlar));
        kronometre.Stop();

        Assert.True(sonuc.EngineFailed);
        Assert.Equal(HtaccessDiagnosticCodes.RegexTimeout, sonuc.FailureCode);
        Assert.True(kronometre.Elapsed < TimeSpan.FromSeconds(5), $"süre: {kronometre.Elapsed}");
    }

    [Fact]
    public void Riskli_desen_KURAL_CALISIRKEN_de_onceden_uyarilir()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("""
RewriteEngine On
RewriteRule ^(a+)+b$ /x.php [L]
""");

        var rapor = site.Engine.Inspect(site.Root);

        Assert.Contains(rapor.Diagnostics, t => t.Code == HtaccessDiagnosticCodes.RiskyRegex);
    }

    [Fact]
    public void Regex_onbellegi_sinirsiz_buyumez()
    {
        var onbellek = new HtaccessRegexCache(enFazlaDesen: 10);
        for (var i = 0; i < 100; i++)
        {
            Assert.NotNull(onbellek.TryGet($"^desen{i}$", ignoreCase: false, TimeSpan.FromMilliseconds(50), out _));
        }

        Assert.True(onbellek.Count <= 10, $"önbellekte {onbellek.Count} desen var");
    }

    [Fact]
    public void Bozuk_desen_istisna_atmaz_kurali_devre_disi_birakir()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("""
RewriteEngine On
RewriteRule ^(bozuk /x.php [L]
RewriteRule ^gecerli$ /y.php [L]
""");

        var sonuc = site.Evaluate("/gecerli");

        Assert.Equal("/y.php", sonuc.RewrittenPath);
        Assert.Contains(sonuc.Diagnostics, t => t.Code == HtaccessDiagnosticCodes.InvalidRegex);
    }

    // ---------------------------------------------------------------- bütçe

    [Fact]
    public void Sonsuz_yeniden_yazma_dongusu_ic_ozyineleme_sinirinda_kesilir()
    {
        using var site = new HtaccessTestSite();
        // Kendi çıktısına da uyan bir kural: Apache'nin LimitInternalRecursion'a düştüğü durum.
        site.Htaccess("""
RewriteEngine On
RewriteRule ^(.*)$ /a/$1 [L]
""");

        var sonuc = site.Evaluate("/x");

        Assert.True(sonuc.EngineFailed);
        Assert.Equal(HtaccessDiagnosticCodes.RecursionBudgetExceeded, sonuc.FailureCode);
        Assert.True(sonuc.Budget.Exceeded);
        Assert.True(sonuc.Budget.InternalRedirects <= HtaccessLimits.Default.MaxInternalRedirects);
    }

    [Fact]
    public void N_bayragiyla_kurulan_sonsuz_dongu_de_kesilir()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("""
RewriteEngine On
RewriteRule ^(.*)$ /$1x [N]
""");

        var kronometre = Stopwatch.StartNew();
        var sonuc = site.Evaluate("/a");
        kronometre.Stop();

        Assert.True(sonuc.EngineFailed);
        Assert.True(kronometre.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Kural_sayisi_butcesi_asilinca_degerlendirme_durur()
    {
        using var site = new HtaccessTestSite();
        var kurallar = string.Join('\n', Enumerable.Range(0, 60).Select(i => $"RewriteRule ^hicbirseye-uymaz-{i}$ /x{i}.php [L]"));
        site.Htaccess("RewriteEngine On\n" + kurallar);

        var sinirlar = HtaccessLimits.Default with { MaxRuleEvaluations = 10 };
        var sonuc = site.Engine.Evaluate(site.Request("/z", sinirlar: sinirlar));

        Assert.True(sonuc.EngineFailed);
        Assert.Equal(HtaccessDiagnosticCodes.RuleBudgetExceeded, sonuc.FailureCode);
    }

    [Fact]
    public void Dev_dosya_UYGULANMAZ_ve_bildirilir()
    {
        using var site = new HtaccessTestSite();
        var sisirilmis = string.Join('\n', Enumerable.Range(0, 20000).Select(i => $"# doldurma satiri {i} ————————————————"));
        site.Htaccess("RewriteEngine On\nRewriteRule ^a$ /b [L]\n" + sisirilmis);

        var sonuc = site.Evaluate("/a");

        Assert.Equal(HtaccessOutcome.None, sonuc.Outcome);
        Assert.Contains(sonuc.Diagnostics,
            t => t is { Level: HtaccessDiagnosticLevel.Fatal, Code: HtaccessDiagnosticCodes.FileTooLarge });
        Assert.Empty(sonuc.AppliedFiles);
    }

    [Fact]
    public void Cok_fazla_direktif_dosyayi_dusurur()
    {
        using var site = new HtaccessTestSite();
        var cokKural = string.Join('\n', Enumerable.Range(0, 50).Select(i => $"RewriteRule ^{i}$ /x{i} [L]"));
        site.Htaccess("RewriteEngine On\n" + cokKural);

        var sinirlar = HtaccessLimits.Default with { MaxDirectivesPerFile = 10 };
        var sonuc = site.Engine.Evaluate(site.Request("/5", sinirlar: sinirlar));

        Assert.Contains(sonuc.Diagnostics,
            t => t is { Level: HtaccessDiagnosticLevel.Fatal, Code: HtaccessDiagnosticCodes.TooManyDirectives });
    }

    [Fact]
    public void Cok_uzun_satir_uygulanmaz()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("RewriteEngine On\nRewriteRule ^" + new string('a', 9000) + "$ /x [L]\nRewriteRule ^b$ /y [L]");

        var sonuc = site.Evaluate("/b");

        Assert.Equal("/y", sonuc.RewrittenPath);
        Assert.Contains(sonuc.Diagnostics, t => t.Code == HtaccessDiagnosticCodes.LineTooLong);
    }

    // ---------------------------------------------------------------- yol güvenliği

    [Theory]
    [InlineData("/../../gizli.php")]
    [InlineData("/a/../../../../Windows/System32/x")]
    public void Site_kokunun_disina_cikan_yeniden_yazma_hedefi_UYGULANMAZ(string hedef)
    {
        using var site = new HtaccessTestSite();
        site.Htaccess($"RewriteEngine On\nRewriteRule ^kacis$ {hedef} [L]\n");

        var sonuc = site.Evaluate("/kacis");

        Assert.Equal(HtaccessOutcome.None, sonuc.Outcome);
        Assert.Contains(sonuc.Diagnostics, t => t.Code == HtaccessDiagnosticCodes.PathEscapesSiteRoot);
    }

    [Fact]
    public void AuthUserFile_site_kokunun_disinda_olamaz()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("""
AuthType Basic
AuthName "Alan"
AuthUserFile ../../.htpasswd
Require valid-user
""");

        var sonuc = site.Evaluate("/gizli.html");

        Assert.Contains(sonuc.Diagnostics, t => t.Code == HtaccessDiagnosticCodes.PathEscapesSiteRoot);
        // Koruma kurulamadığı için erişim AÇILMAZ: "Require valid-user" var ama Auth eksik.
        Assert.Equal(HtaccessOutcome.AccessDenied, sonuc.Outcome);
        Assert.Null(sonuc.Auth);
    }

    [Fact]
    public void AuthUserFile_mutlak_sistem_yolu_reddedilir()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("""
AuthType Basic
AuthName "Alan"
AuthUserFile C:/Windows/win.ini
Require valid-user
""");

        var sonuc = site.Evaluate("/gizli.html");

        Assert.Contains(sonuc.Diagnostics, t => t.Code == HtaccessDiagnosticCodes.PathEscapesSiteRoot);
        Assert.Null(sonuc.Auth);
    }

    [Fact]
    public void ErrorDocument_kokun_disini_gosteremez()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("ErrorDocument 404 /../../gizli/404.html\nErrorDocument 500 /hata/500.html\n");

        var sonuc = site.Evaluate("/x.html");

        Assert.False(sonuc.ErrorDocuments.ContainsKey(404));
        Assert.Equal("/hata/500.html", sonuc.ErrorDocuments[500]);
        Assert.Contains(sonuc.Diagnostics, t => t.Code == HtaccessDiagnosticCodes.PathEscapesSiteRoot);
    }

    [Fact]
    public void Dosya_testleri_site_kokunun_disina_bakamaz()
    {
        using var site = new HtaccessTestSite();
        // Windows'ta kesin var olan bir dosya; koşul TUTMAMALI.
        site.Htaccess("""
RewriteEngine On
RewriteCond C:/Windows/win.ini -f
RewriteRule ^sizinti$ /evet.php [L]
""");

        var sonuc = site.Evaluate("/sizinti");

        Assert.Equal(HtaccessOutcome.None, sonuc.Outcome);
        Assert.Contains(sonuc.Diagnostics, t => t.Code == HtaccessDiagnosticCodes.PathEscapesSiteRoot);
    }

    [Fact]
    public void Istek_yolundaki_ust_dizin_kacisi_baska_dizinin_htaccess_ini_okutmaz()
    {
        using var site = new HtaccessTestSite();
        site.Htaccess("ErrorDocument 404 /kok.html\n");

        var sonuc = site.Engine.Evaluate(site.Request("/a/../../../komsu/sayfa.html"));

        Assert.Contains(sonuc.Diagnostics, t => t.Code == HtaccessDiagnosticCodes.PathEscapesSiteRoot);
        Assert.Empty(sonuc.AppliedFiles);
    }

    // ---------------------------------------------------------------- istisna sızdırmama

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("goreli/yol")]
    public void Gecersiz_site_koku_istisna_degil_BASARISIZLIK_dondurur(string? kok)
    {
        var motor = new HtaccessEngine();

        var sonuc = motor.Evaluate(new HtaccessRequestContext { SiteRootPath = kok!, RequestPath = "/x" });

        Assert.True(sonuc.EngineFailed);
        Assert.Equal(HtaccessOutcome.Failed, sonuc.Outcome);
        Assert.NotEmpty(sonuc.Diagnostics);
    }

    [Fact]
    public void Var_olmayan_site_koku_istisna_atmaz()
    {
        var motor = new HtaccessEngine();
        var yok = Path.Combine(Path.GetTempPath(), $"adapanel-yok-{Guid.NewGuid():N}");

        var sonuc = motor.Evaluate(new HtaccessRequestContext { SiteRootPath = yok, RequestPath = "/x" });

        Assert.Equal(HtaccessOutcome.None, sonuc.Outcome);
        Assert.False(sonuc.EngineFailed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\0\0\0")]
    [InlineData("RewriteRule")]
    [InlineData("<<<<<<<")]
    [InlineData("RewriteRule [ ] [ ]")]
    [InlineData("<IfModule><Files><FilesMatch>")]
    [InlineData("RewriteCond")]
    [InlineData("Header")]
    [InlineData("ErrorDocument abc")]
    [InlineData("ExpiresByType text/css \"cok garip bir sey\"")]
    [InlineData("Options +HicBoyle")]
    [InlineData("AuthUserFile")]
    [InlineData("Require")]
    public void Bozuk_girdiler_ayristiriciyi_cokertmez(string icerik)
    {
        var motor = new HtaccessEngine();

        var ayristirma = motor.Validate(icerik);
        Assert.NotNull(ayristirma);

        using var site = new HtaccessTestSite();
        site.Htaccess(icerik);
        var sonuc = site.Evaluate("/x");
        Assert.NotNull(sonuc);
    }

    /// <summary>
    /// Diskin BEKLENMEDİK biçimde patlaması motoru delip geçmemeli.
    /// </summary>
    /// <remarks>
    /// Dosya sistemi sarmalayıcısı bilinen G/Ç istisnalarını zaten yutuyor. Buradaki soru farklı:
    /// motorun EN DIŞTAKİ kalkanı, hiç beklenmeyen bir istisna türü için de duruyor mu? Kalkan
    /// olmazsa barındırma katmanına istisna sızar ve tek bir kusur siteyi 500'e düşürür.
    /// </remarks>
    private sealed class PatlayanDosyaSistemi : IHtaccessFileSystem
    {
        public bool FileExists(string path) => throw new InvalidOperationException("disk patladi");
        public bool DirectoryExists(string path) => throw new InvalidOperationException("disk patladi");
        public bool IsSymbolicLink(string path) => throw new InvalidOperationException("disk patladi");
        public bool IsNonEmptyFile(string path) => throw new InvalidOperationException("disk patladi");

        public bool TryReadText(string path, int maxBytes, out string content, out string hata) =>
            throw new InvalidOperationException("disk patladi");

        public (DateTime LastWriteUtc, long Length) Stamp(string path) =>
            throw new InvalidOperationException("disk patladi");
    }

    [Fact]
    public void Beklenmedik_istisna_MOTORDAN_SIZMAZ_basarisizlik_olarak_doner()
    {
        var motor = new HtaccessEngine(new PatlayanDosyaSistemi(), HtaccessLimits.Default);
        var istek = new HtaccessRequestContext { SiteRootPath = @"C:\siteler\ornek", RequestPath = "/x" };

        var sonuc = motor.Evaluate(istek);

        Assert.True(sonuc.EngineFailed);
        Assert.Equal(HtaccessOutcome.Failed, sonuc.Outcome);
        Assert.Contains(sonuc.Diagnostics, t => t.Message.Contains("InvalidOperationException"));

        // Inspect ve Validate de aynı sözleşmeye tabi.
        Assert.True(motor.Inspect(@"C:\siteler\ornek").HasFatal);
        Assert.False(motor.Authenticate(
            new HtaccessAuthChallenge { UserFilePath = @"C:\siteler\ornek\.htpasswd" }, "a", "b").Succeeded);
    }

    [Fact]
    public void Kimlik_dogrulama_bozuk_girdilerde_istisna_atmaz()
    {
        var motor = new HtaccessEngine();

        Assert.False(motor.Authenticate(null!, "a", "b").Succeeded);
        Assert.False(motor.Authenticate(new HtaccessAuthChallenge(), "a", "b").Succeeded);
        Assert.False(motor.Authenticate(
            new HtaccessAuthChallenge { UserFilePath = @"C:\yok\olmayan\.htpasswd" }, "a", "b").Succeeded);
    }

    [Fact]
    public void Cok_yuksek_maliyetli_bcrypt_REDDEDILIR()
    {
        // $2y$31$ tek doğrulamada dakikalarca CPU yer: hizmet dışı bırakma silahıdır.
        var (gecti, kod, sebep) = AdaPanel.Infrastructure.Htaccess.Htpasswd.HtpasswdFile.OzetiDogrula(
            "$2y$31$bWBMg3oUStnhfy5rFvoyreviPySU6hvEmBub5wIlM/D.c5FeYJQ6O", "bar", HtaccessLimits.Default);

        Assert.False(gecti);
        Assert.Equal(HtaccessDiagnosticCodes.UnsupportedHashScheme, kod);
        Assert.Contains("maliyet", sebep);
    }

    [Fact]
    public void Cok_buyuk_parola_dosyasi_okunmaz()
    {
        using var site = new HtaccessTestSite();
        site.Write(".htpasswd", string.Join('\n', Enumerable.Range(0, 5000).Select(i => $"kullanici{i}:$apr1$r31.....$HqJZimcKQFAMYayBlzkrA/")));
        site.Htaccess("""
AuthType Basic
AuthName "Alan"
AuthUserFile .htpasswd
Require valid-user
""");

        var sorgu = site.Evaluate("/gizli.html").Auth!;
        var sinirlar = HtaccessLimits.Default with { MaxAuthUserFileBytes = 1024 };

        var sonuc = site.Engine.Authenticate(sorgu, "kullanici0", "myPassword", sinirlar);

        Assert.False(sonuc.Succeeded);
        Assert.Equal(HtaccessDiagnosticCodes.AuthFileUnreadable, sonuc.FailureCode);
    }

    [Fact]
    public void Basamaklandirma_derinligi_sinirlanir()
    {
        using var site = new HtaccessTestSite();
        var yol = string.Join('/', Enumerable.Range(0, 40).Select(i => $"d{i}"));
        site.Write(yol + "/sayfa.html", "x");

        var sinirlar = HtaccessLimits.Default with { MaxCascadeDepth = 5 };
        var sonuc = site.Engine.Evaluate(site.Request("/" + yol + "/sayfa.html", sinirlar: sinirlar));

        Assert.Contains(sonuc.Diagnostics, t => t.Code == HtaccessDiagnosticCodes.CascadeDepthExceeded);
        Assert.False(sonuc.EngineFailed);
    }
}
