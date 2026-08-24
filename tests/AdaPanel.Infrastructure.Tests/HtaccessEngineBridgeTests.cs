using System.Text.RegularExpressions;
using AdaPanel.Core.Dtos.Htaccess;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Htaccess;
using AdaPanel.Infrastructure.Htaccess.Hosting;
using Xunit;

// AD CAKISMASI: Hosting ad alaninda da bir HtaccessOutcome var (HtaccessFailSafe'in sonucu).
// Iki using ayni adi getirdiginde derleyici SECMEZ, hata verir; takma ad hangisini kastettigimizi
// tek yerde sabitler.
using HtaccessOutcome = AdaPanel.Core.Dtos.Htaccess.HtaccessOutcome;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// KÖPRÜ testleri — çekirdek motor ile IIS modülü arasındaki bağlantı.
/// </summary>
/// <remarks>
/// <para>
/// Buradaki asıl soru "doğru kararı üretiyor mu" DEĞİL (onu motorun kendi testleri ölçüyor);
/// <b>bağlantının kendisi isteği bozabilir mi</b>. Modülün fail-open sarmalayıcısı zaten var ama
/// o, arızayı devre kesici sayacına yazar ve beş üst üste arızada modülü o havuzda 60 saniye
/// kapatır — yani köprünün kendi kusuru, motoru sağlam olan bir sitede özelliği sessizce
/// öldürebilir. Bu yüzden köprü ayrıca ve kendi başına fail-open olmak zorunda.
/// </para>
/// </remarks>
public sealed class HtaccessEngineBridgeTests
{
    /// <summary>Her çağrısında istisna fırlatan motor — sözleşme ihlalinin taklidi.</summary>
    private sealed class PatlayanMotor(Exception istisna) : IHtaccessEngine
    {
        public HtaccessEvaluationResult Evaluate(HtaccessRequestContext request) => throw istisna;

        public HtaccessInspectionResult Inspect(string siteRootPath, string relativeDirectory = "")
            => throw istisna;

        public HtaccessParseResult Validate(string content, string virtualPath = "/", HtaccessLimits? limits = null)
            => throw istisna;

        public HtaccessAuthResult Authenticate(
            HtaccessAuthChallenge challenge, string userName, string password, HtaccessLimits? limits = null)
            => throw istisna;

        public void InvalidateCache(string? siteRootPath = null) => throw istisna;
    }

    /// <summary>Sabit bir sonuç dönen motor — eşleme kurallarını ölçmek için.</summary>
    private sealed class SabitMotor(HtaccessEvaluationResult? sonuc) : IHtaccessEngine
    {
        public HtaccessEvaluationResult Evaluate(HtaccessRequestContext request) => sonuc!;

        public HtaccessInspectionResult Inspect(string siteRootPath, string relativeDirectory = "") => new();

        public HtaccessParseResult Validate(string content, string virtualPath = "/", HtaccessLimits? limits = null)
            => new();

        public HtaccessAuthResult Authenticate(
            HtaccessAuthChallenge challenge, string userName, string password, HtaccessLimits? limits = null)
            => new();

        public void InvalidateCache(string? siteRootPath = null)
        {
        }
    }

    private static HtaccessBridgeDecision Karar(IHtaccessEngine? motor, string yol = "/x", string sorgu = "")
        => new HtaccessEngineBridge(motor).Decide(@"C:\site", @"C:\site", yol, sorgu, "GET", "203.0.113.10", false, "ornek.com");

    // ---------------------------------------------------------------- FAIL-OPEN

    public static TheoryData<Exception> HerTurluIstisna() =>
    [
        new InvalidOperationException("motor sozlesmeyi ihlal etti"),
        new NullReferenceException(),
        new ArgumentOutOfRangeException("x"),
        new IOException("disk gitti"),
        new UnauthorizedAccessException(),
        new OutOfMemoryException(),
        new AggregateException(new Exception("ic")),
        new StackOverflowException_Taklidi()
    ];

    /// <summary>
    /// <see cref="StackOverflowException"/> .NET'te elle fırlatılamaz; davranışı ölçmek için
    /// yakalanabilir bir taklit kullanılıyor. Gerçeği zaten yakalanamaz ve o, motorun
    /// özyineleme sınırıyla önlenir (bkz. <c>HtaccessFailSafe</c> sınıf yorumu).
    /// </summary>
    public sealed class StackOverflowException_Taklidi() : Exception("yigin tasmasi taklidi");

    [Theory]
    [MemberData(nameof(HerTurluIstisna))]
    public void MOTOR_NE_FIRLATIRSA_FIRLATSIN_kopru_SIZDIRMAZ_ve_KARAR_URETMEZ(Exception firlatilan)
    {
        // Filtre YOK: secilmeyen tek bir istisna tipi, o tipin sunucudaki butun siteleri
        // dusurmesi demektir. Kopru "beklenmeyen istisna" kavramini tanimaz.
        var karar = Karar(new PatlayanMotor(firlatilan));

        Assert.Equal(HtaccessBridgeActionKind.None, karar.Kind);
    }

    [Fact]
    public void MOTOR_YOKSA_kopru_sessizce_KARAR_URETMEZ()
    {
        // Motor bagli degilken (HtaccessEngineRegistry.Current null) modul zaten istegi
        // dokunmadan geciriyor; kopru de ayni cevabi vermeli ki iki katman celismesin.
        Assert.Equal(HtaccessBridgeActionKind.None, Karar(null).Kind);
    }

    [Fact]
    public void MOTOR_NULL_DONERSE_kopru_KARAR_URETMEZ()
    {
        Assert.Equal(HtaccessBridgeActionKind.None, Karar(new SabitMotor(null)).Kind);
    }

    [Fact]
    public void Prepare_BOZUK_YOLDA_FIRLATMAZ()
    {
        var kopru = new HtaccessEngineBridge(new SabitMotor(new HtaccessEvaluationResult()));

        Assert.Null(kopru.Prepare(null));
        Assert.Null(kopru.Prepare(string.Empty));
        // Yalniz dosya adi -> dizin bos -> belirtec YOK. Modul istegi dokunmadan gecirir.
        Assert.Null(kopru.Prepare(".htaccess"));
    }

    [Fact]
    public void Prepare_MOTOR_YOKKEN_BELIRTEC_URETMEZ()
    {
        // Motor bagli degilse derleme belirteci uretmek, modulu bos yere Evaluate'e sokardi.
        Assert.Null(new HtaccessEngineBridge(null).Prepare(@"C:\site\.htaccess"));
    }

    [Fact]
    public void MOTOR_BASARISIZ_BILDIRIRSE_karar_URETILMEZ()
    {
        // EngineFailed bir DEGERDIR, istisna degil. Yarim uygulanmis bir kural kumesi hic
        // uygulanmamis olandan tehlikelidir -> karar yok.
        var motor = new SabitMotor(new HtaccessEvaluationResult
        {
            Outcome = HtaccessOutcome.Rewritten,
            RewrittenPath = "/index.php",
            EngineFailed = true
        });

        Assert.Equal(HtaccessBridgeActionKind.None, Karar(motor).Kind);
    }

    // ---------------------------------------------------------------- ESLEME

    [Fact]
    public void REWRITE_sorgu_dizesini_KAYBETMEZ()
    {
        // RewritePath tek dizge alir; sorguyu birlestirmezsek index.php?q=... ureten HER
        // WordPress/Laravel kurali sorgusunu kaybederdi.
        var motor = new SabitMotor(new HtaccessEvaluationResult
        {
            Outcome = HtaccessOutcome.Rewritten,
            RewrittenPath = "/index.php",
            RewrittenQuery = "q=deneme&s=1"
        });

        var karar = Karar(motor);

        Assert.Equal(HtaccessBridgeActionKind.Rewrite, karar.Kind);
        Assert.Equal("/index.php?q=deneme&s=1", karar.Target);
    }

    [Fact]
    public void REWRITE_hedefi_BOSSA_karar_URETILMEZ()
    {
        var motor = new SabitMotor(new HtaccessEvaluationResult { Outcome = HtaccessOutcome.Rewritten });

        Assert.Equal(HtaccessBridgeActionKind.None, Karar(motor).Kind);
    }

    [Fact]
    public void REWRITE_hedefi_BASTA_EGIK_CIZGI_ile_normallesir()
    {
        var motor = new SabitMotor(new HtaccessEvaluationResult
        {
            Outcome = HtaccessOutcome.Rewritten,
            RewrittenPath = "index.php"
        });

        Assert.Equal("/index.php", Karar(motor).Target);
    }

    [Fact]
    public void REDIRECT_hedefsizse_UYGULANMAZ()
    {
        // Tarayiciya "baska yere git" deyip nereye oldugunu soylememek sayfayi oldururdu.
        var motor = new SabitMotor(new HtaccessEvaluationResult
        {
            Outcome = HtaccessOutcome.Redirect,
            StatusCode = 301
        });

        Assert.Equal(HtaccessBridgeActionKind.None, Karar(motor).Kind);
    }

    [Fact]
    public void REDIRECT_durum_kodu_YOKSA_302_olur()
    {
        var motor = new SabitMotor(new HtaccessEvaluationResult
        {
            Outcome = HtaccessOutcome.Redirect,
            RedirectLocation = "https://ornek.com/yeni"
        });

        var karar = Karar(motor);

        Assert.Equal(HtaccessBridgeActionKind.Redirect, karar.Kind);
        Assert.Equal(302, karar.StatusCode);
        Assert.Equal("https://ornek.com/yeni", karar.Target);
    }

    [Theory]
    [InlineData(HtaccessOutcome.Forbidden, 403)]
    [InlineData(HtaccessOutcome.AccessDenied, 403)]
    [InlineData(HtaccessOutcome.Gone, 410)]
    public void RED_sonuclari_DOGRU_durum_koduyla_esleser(HtaccessOutcome sonuc, int beklenenKod)
    {
        var motor = new SabitMotor(new HtaccessEvaluationResult { Outcome = sonuc });
        var karar = Karar(motor);

        Assert.Equal(HtaccessBridgeActionKind.Deny, karar.Kind);
        Assert.Equal(beklenenKod, karar.StatusCode);
    }

    /// <summary>
    /// 401 UYGULANMAZ ve bu bilinçli.
    /// </summary>
    /// <remarks>
    /// <c>WWW-Authenticate</c> başlığı olmadan 401 dönmek, tarayıcının kimlik SORMAMASI demektir:
    /// kullanıcı boş bir "Unauthorized" görür ve korunan dizine bir daha giremez. Yarım bir
    /// kimlik doğrulama, hiç olmamasından beterdir.
    /// </remarks>
    [Fact]
    public void KIMLIK_DOGRULAMA_GEREKEN_istek_YARIM_UYGULANMAZ()
    {
        var motor = new SabitMotor(new HtaccessEvaluationResult
        {
            Outcome = HtaccessOutcome.AuthenticationRequired,
            StatusCode = 401
        });

        Assert.Equal(HtaccessBridgeActionKind.None, Karar(motor).Kind);
    }

    [Fact]
    public void BILINMEYEN_sonuc_HICBIR_SEY_YAPMAZ()
    {
        // Motora ileride yeni bir Outcome eklendiginde kopru onu YANLIS uygulamak yerine
        // hic uygulamaz. Varsayilan dalin varlik sebebi budur.
        var motor = new SabitMotor(new HtaccessEvaluationResult { Outcome = (HtaccessOutcome)9999 });

        Assert.Equal(HtaccessBridgeActionKind.None, Karar(motor).Kind);
    }

    // ---------------------------------------------------------------- NOBETCI: ENUM ESITLIGI

    /// <summary>
    /// Köprünün karar türü ile MODÜLÜN karar türü sayısal olarak AYNI olmak zorunda.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uyarlayıcı (<c>HtaccessModuleEngineAdapter</c>) ikisi arasında <c>(int)</c> dönüşümü
    /// yapıyor ve o dosya net48'de derlendiği için buradaki testlerden GEÇMEZ. Yani eşitlik
    /// bozulursa hiçbir derleme hatası oluşmaz: modül sessizce YANLIŞ eylemi uygular —
    /// <c>Redirect</c> yerine <c>Deny</c> gibi. Bu yüzden eşitlik tahmin edilmiyor, modülün
    /// KAYNAK DOSYASI okunup karşılaştırılıyor.
    /// </para>
    /// </remarks>
    [Fact]
    public void KOPRU_ve_MODUL_karar_turleri_SAYISAL_OLARAK_AYNI()
    {
        var kaynak = ModulKaynagi("IHtaccessEngine.cs");

        // enum HtaccessActionKind { None = 0, Rewrite = 1, ... } govdesindeki "Ad = Sayi" ciftleri.
        var govde = Regex.Match(kaynak, @"enum\s+HtaccessActionKind\s*\{(?<govde>[^}]*)\}", RegexOptions.Singleline);
        Assert.True(govde.Success, "Modulde HtaccessActionKind enum'u BULUNAMADI - nobetci koru kalmis olabilir.");

        var modulUyeleri = Regex.Matches(govde.Groups["govde"].Value, @"(?<ad>\w+)\s*=\s*(?<deger>\d+)")
            .Select(e => (Ad: e.Groups["ad"].Value, Deger: int.Parse(e.Groups["deger"].Value)))
            .ToDictionary(e => e.Ad, e => e.Deger, StringComparer.Ordinal);

        Assert.True(modulUyeleri.Count >= 4, $"Enum ayristirmasi bozulmus olabilir: {modulUyeleri.Count} uye bulundu.");

        foreach (var uye in Enum.GetValues<HtaccessBridgeActionKind>())
        {
            var ad = uye.ToString();
            Assert.True(modulUyeleri.ContainsKey(ad), $"Modulun HtaccessActionKind'inda '{ad}' YOK.");
            Assert.Equal(modulUyeleri[ad], (int)uye);
        }

        // Ters yon de denetlenir: modul yeni bir eylem eklerse kopru onu ureteme z ve
        // "yeni eylem eklendi ama koprude karsiligi yok" sessiz kalirdi.
        foreach (var ad in modulUyeleri.Keys)
        {
            Assert.True(
                Enum.TryParse<HtaccessBridgeActionKind>(ad, out _),
                $"Modulde '{ad}' eylemi var ama koprude karsiligi YOK; kopru o eylemi hic uretemez.");
        }
    }

    /// <summary>
    /// Motoru modüle bağlayan uyarlayıcı DOSYASI, modül projesine BAĞLI mı?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bu test bugün bilerek "bağlı DEĞİL" diyor ve bu bir borç kaydıdır.</b> Ölçüldü: motorun
    /// ve <c>AdaPanel.Core</c>'daki bağımlılıklarının bütün <c>.cs</c> dosyaları net48 hedefiyle
    /// derlendi; dokuz dosyada, yalnız .NET (Core) API'lerine dayanan satırlar kaldı ve bunların
    /// hiçbiri dışarıdan bir tiple telafi edilemez (hepsi BCL tiplerinin static/örnek üyeleri).
    /// Motor net48 uyumlu hale getirilmeden bağlantı FİZİKSEL OLARAK yapılamaz.
    /// </para>
    /// <para>
    /// <b>Neden yine de test:</b> bu satırlar düzeltilip csproj'a bağlantı eklendiğinde test
    /// KIRILIR ve o an güncellenmesi gerekir — yani borcun kapandığı gün kimse fark etmeden
    /// geçemez. Tersi de doğru: bağlantı bugün "var" sanılıyorsa test onu hemen yalanlar.
    /// </para>
    /// </remarks>
    [Fact]
    public void MOTOR_modul_projesine_HENUZ_BAGLI_DEGIL_ve_sebebi_OLCULDU()
    {
        var csproj = File.ReadAllText(Path.Combine(ModulKlasoru(), "AdaPanel.HtaccessModule.csproj"));

        var bagli = csproj.Contains("HtaccessModuleEngineAdapter.cs", StringComparison.Ordinal);

        Assert.False(
            bagli,
            "Motor artik modul projesine BAGLANMIS gorunuyor. Bu iyi haber -- ama bu nobetci "
            + "guncellenmeli: HtaccessPanelService.MotorEngelleri() bos donmeli ve "
            + "HtaccessSiteReport.EngineLoadedInIis artik false sabitlenmemeli, yoksa panel "
            + "CALISAN bir ozelligi 'calismiyor' diye gosterir.");
    }

    private static string ModulKaynagi(string dosyaAdi)
        => File.ReadAllText(Path.Combine(ModulKlasoru(), dosyaAdi));

    /// <summary>
    /// Modül projesinin klasörü. Yol TAHMİN EDİLMİYOR: derleme çıktısından yukarı doğru
    /// <c>AdaPanel.sln</c> aranıyor, böylece test klasör derinliği değişse de çalışır.
    /// </summary>
    private static string ModulKlasoru()
    {
        var dizin = new DirectoryInfo(AppContext.BaseDirectory);
        while (dizin is not null && !File.Exists(Path.Combine(dizin.FullName, "AdaPanel.sln")))
        {
            dizin = dizin.Parent;
        }

        Assert.True(dizin is not null, "AdaPanel.sln bulunamadi; nobetci kaynak dosyalarini okuyamiyor.");
        return Path.Combine(dizin!.FullName, "src", "AdaPanel.HtaccessModule");
    }
}
