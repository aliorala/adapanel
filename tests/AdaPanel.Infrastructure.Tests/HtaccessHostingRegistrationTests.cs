using System.Diagnostics;
using AdaPanel.Infrastructure.Htaccess.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// IIS KAYIT SÖZLEŞMESİ + PERFORMANS ÖLÇÜMÜ.
///
/// Buradaki testlerin bir kısmı NÖBETÇİDİR: C# sabitleri ile kurulum betiğinin ve gerçek
/// derlemenin AYNI şeyi söylediğini disk üzerinden doğrular. Elle yazılmış iki liste yerine
/// tek kaynak + nöbetçi, bu deponun ölçülmüş tercihidir.
/// </summary>
public sealed class HtaccessHostingRegistrationTests(ITestOutputHelper output)
{
    private static string RepoRoot()
    {
        // Test derlemesinden yukari yurunerek depo kokunu bul (AdaPanel.sln nerede ise orasi).
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "AdaPanel.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string InstallScript()
        => Path.Combine(RepoRoot(), "scripts", "install-htaccess-module.ps1");

    // ------------------------------------------------------- ÖN KOŞUL (en kritik tek satır)

    [Fact]
    public void ON_KOSUL_managedHandler_ICERMEZ()
    {
        // managedHandler olsaydi OZELLIK HIC CALISMAZDI. Belge (modules/add):
        // "Specify the managedHandler value when the module should process requests only for
        //  managed resources, such as .aspx files, and should not respond to requests for
        //  non-managed resources, such as .html files."
        // .htaccess'in butun amaci PHP ve STATIK dosyalardir.
        Assert.DoesNotContain("managedHandler", HtaccessModuleRegistration.PreCondition);
    }

    [Fact]
    public void ON_KOSUL_runtimeVersionv4_ICERIR()
    {
        // Iki felaketi birden onler:
        //  a) v2.0 havuzlari -> CLR 4 derlemesi CLR 2'de yuklenemez.
        //  b) "Yonetilen kod yok" havuzlari (Node/Python; AppRuntimeService.cs:466) -> CLR yok.
        Assert.Contains("runtimeVersionv4.0", HtaccessModuleRegistration.PreCondition);
    }

    [Fact]
    public void ON_KOSUL_BITNESS_ICERMEZ()
    {
        // PHP icin 32-bit havuz (enable32BitAppOnAppPool) COK yaygin; bitness yazmak modulu
        // o havuzlarda sessizce olduraurdu. Derleme AnyCPU.
        Assert.DoesNotContain("bitness", HtaccessModuleRegistration.PreCondition);
    }

    [Fact]
    public void KURULUM_BETIGI_runAllManagedModulesForAllRequests_KULLANMAZ()
    {
        // Belge (modules): "When you use this property, the 'managedHandler' precondition has no
        // effect and all managed modules run for all requests." YANI musterinin sitesindeki BUTUN
        // yonetilen modullerin (Session, FormsAuthentication, Profile...) davranisini degistirirdi.
        // Bizim ozelligimiz ugruna musterinin sitesini degistirmek KABUL EDILEMEZ.
        var script = File.ReadAllText(InstallScript());
        Assert.DoesNotContain("runAllManagedModulesForAllRequests", script);
    }

    // ------------------------------------------------------- NÖBETÇİ: betik <-> C# sabitleri

    [Theory]
    [InlineData("$ModuleName   = \"", nameof(HtaccessModuleRegistration.ModuleName))]
    [InlineData("$TypeName     = \"", nameof(HtaccessModuleRegistration.TypeName))]
    [InlineData("$AssemblyName = \"", nameof(HtaccessModuleRegistration.AssemblyName))]
    [InlineData("$PreCondition = \"", nameof(HtaccessModuleRegistration.PreCondition))]
    public void BETIKTEKI_DEGERLER_C_SHARP_SABITLERIYLE_AYNI(string prefix, string constantName)
    {
        // Bu dordunu elle iki yerde tutmak, "panel kurulu der / IIS baska ad arar" seklinde
        // SESSIZ bir arizaya acik kapi birakirdi. Nobetci onu derleme zamanina cekiyor.
        var expected = constantName switch
        {
            nameof(HtaccessModuleRegistration.ModuleName) => HtaccessModuleRegistration.ModuleName,
            nameof(HtaccessModuleRegistration.TypeName) => HtaccessModuleRegistration.TypeName,
            nameof(HtaccessModuleRegistration.AssemblyName) => HtaccessModuleRegistration.AssemblyName,
            _ => HtaccessModuleRegistration.PreCondition
        };

        var script = File.ReadAllText(InstallScript());
        Assert.Contains(prefix + expected + "\"", script);
    }

    [Fact]
    public void MODUL_PROJESI_COZUMDE_ve_net48_HEDEFLI()
    {
        // net48 ZORUNLU: IIS'in yonetilen modul sozlesmesi System.Web.IHttpModule'dur ve
        // System.Web .NET (Core) icinde YOKTUR.
        var csproj = Path.Combine(RepoRoot(), "src", "AdaPanel.HtaccessModule", "AdaPanel.HtaccessModule.csproj");
        Assert.True(File.Exists(csproj), "modul projesi bulunamadi");

        var content = File.ReadAllText(csproj);
        Assert.Contains("<TargetFramework>net48</TargetFramework>", content);
        Assert.Contains("<SignAssembly>true</SignAssembly>", content);

        // GAC kimliginin panel surumunden BAGIMSIZ olmasi sart (yoksa her surumde yan yana
        // kurulum olusur ve applicationHost.config eski surumu gostermeye devam eder).
        Assert.Contains("<AssemblyVersion>1.0.0.0</AssemblyVersion>", content);

        var solution = File.ReadAllText(Path.Combine(RepoRoot(), "AdaPanel.sln"));
        Assert.Contains("AdaPanel.HtaccessModule", solution);
    }

    [Fact]
    public void PAYLASILAN_DOSYALAR_MODUL_PROJESINE_BAGLI()
    {
        // Tek kaynak, iki cikti. Bir dosya Hosting'e eklenip buraya eklenmezse modul o mantiktan
        // YOKSUN derlenir ve panel ile modul FARKLI davranir -- sessiz sapma.
        var csproj = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "AdaPanel.HtaccessModule", "AdaPanel.HtaccessModule.csproj"));

        var hostingDirectory = Path.Combine(
            RepoRoot(), "src", "AdaPanel.Infrastructure", "Htaccess", "Hosting");

        // Modulun ICINDE calismasi gereken dosyalar. Panel'e ozgu olanlar (ACL yazan
        // HtaccessSwitchStore gibi) BILEREK disarida -- net48'e baglanmalari gerekmiyor.
        var mustBeLinked = new[]
        {
            "HtaccessHostingPaths.cs",
            "HtaccessSwitchFile.cs",
            "HtaccessRuntimeGate.cs",
            "HtaccessFailSafe.cs",
            "HtaccessFileCache.cs",
            "HtaccessModuleRegistration.cs"
        };

        foreach (var file in mustBeLinked)
        {
            Assert.True(File.Exists(Path.Combine(hostingDirectory, file)), $"{file} diskte yok");
            Assert.Contains(file, csproj);
        }
    }

    // ------------------------------------------------------- SİTE DESTEĞİ (dürüst raporlama)

    [Fact]
    public void YONETILEN_KOD_YOK_HAVUZU_DESTEKLENMEZ_olarak_SINIFLANIR()
    {
        // AppRuntimeService.cs:466 Node/Python siteleri icin havuzu BILEREK bu duruma ceviriyor.
        // Panel bunu KULLANICIYA SOYLEMEK zorunda: sebebi bilinmeyen "calismiyor" en pahali
        // destek biletidir.
        Assert.Equal(HtaccessSupport.UnsupportedNoManagedCode, HtaccessModuleRegistration.ClassifySupport(""));
        Assert.Equal(HtaccessSupport.UnsupportedNoManagedCode, HtaccessModuleRegistration.ClassifySupport("   "));
        Assert.Equal(HtaccessSupport.UnsupportedNoManagedCode, HtaccessModuleRegistration.ClassifySupport(null));
    }

    [Fact]
    public void V4_HAVUZU_DESTEKLENIR()
    {
        // SiteProvisioningService.cs:259/290/316/338 panelin actigi havuzlari "v4.0" ile kuruyor.
        Assert.Equal(HtaccessSupport.Supported, HtaccessModuleRegistration.ClassifySupport("v4.0"));
        Assert.Equal(HtaccessSupport.Supported, HtaccessModuleRegistration.ClassifySupport("V4.0"));
    }

    [Fact]
    public void V2_HAVUZU_DESTEKLENMEZ()
    {
        Assert.Equal(HtaccessSupport.UnsupportedRuntimeVersion, HtaccessModuleRegistration.ClassifySupport("v2.0"));
    }

    // ------------------------------------------------------- KURULUM / KALDIRMA BAĞLANTILARI

    [Fact]
    public void KURULUM_MODULU_ACMAZ_genel_anahtar_OLUSTURULMAZ()
    {
        // "Varsayilan KAPALI baslamasi" gereksinimi KODDA degil, KURULUMDA da tutulmali.
        // Kurulum betigi module.switch dosyasini OLUSTURURSA, kurulan her sunucuda modul
        // aninda canlanir ve butun sitelerin istek yoluna girer. Bu test tam olarak onu yasaklar.
        var script = File.ReadAllText(InstallScript());

        // Dosya adi betikte GECIYOR (kullaniciya "acil kapatma" komutu gosteriliyor) ama
        // OLUSTURULMUYOR. Fark kritik: yazma cagrisi olmamali.
        Assert.DoesNotContain("Set-Content", script);
        Assert.DoesNotContain("Out-File", script);
        Assert.DoesNotContain("New-Item -ItemType File", script);
    }

    [Fact]
    public void KURULUM_ADIMI_BEST_EFFORT_CAGRILIR()
    {
        // Adim GAC'a yaziyor ve applicationHost.config'e dokunuyor -- kurulumun IIS'i etkileyen
        // EN RISKLI adimi. Basarisiz olursa dogru davranis kurulumu iptal etmek DEGIL, ozelligi
        // atlamaktir: panel bu modul olmadan da tam calisir.
        var iss = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "AdaPanel.iss"));

        // Cagrinin HEMEN ONUNDEKI cagirici adi olculur; satir sonu bicimine bagli kalmamak icin
        // bosluklar temizlenerek karsilastirilir.
        var sikistirilmis = new string(iss.Where(character => !char.IsWhiteSpace(character)).ToArray());

        const string cagri = "RunPowerShellScript('install-htaccess-module.ps1'";
        Assert.Contains("Try" + cagri, sikistirilmis);

        // KRITIK NEGATIF: KRITIK cagirici (duz RunPowerShellScript) ile CAGRILMAMALI -- o, hata
        // durumunda RaiseException ile KURULUMU IPTAL EDER ve wwwroot yedegini geri alir.
        // Bir IIS modulu kurulamadi diye butun panel kurulumunun geri alinmasi kabul edilemez.
        //
        // DIKKAT: duz "DoesNotContain(cagri)" YANLIS olurdu -- "TryRunPowerShellScript" dizesi
        // "RunPowerShellScript"i ZATEN ICERIR, yani o iddia her zaman kirilirdi. Bu yuzden her
        // gecis TEK TEK bulunup oncesinde "Try" olup olmadigina bakiliyor.
        var index = sikistirilmis.IndexOf(cagri, StringComparison.Ordinal);
        var gecisSayisi = 0;
        while (index >= 0)
        {
            gecisSayisi++;
            Assert.True(
                index >= 3 && sikistirilmis.Substring(index - 3, 3) == "Try",
                "install-htaccess-module.ps1 KRITIK cagirici ile cagriliyor; best-effort olmali.");
            index = sikistirilmis.IndexOf(cagri, index + 1, StringComparison.Ordinal);
        }

        Assert.Equal(1, gecisSayisi);
    }

    [Fact]
    public void KALDIRMADA_MODUL_KAYDI_SILINIYOR()
    {
        // BU EKSIK OLSAYDI "AdaPanel'i kaldir" MUSTERININ BUTUN SITELERINI DUSURURDU:
        // modul GAC'ta ve applicationHost.config'te yasiyor, ikisi de {app} disinda.
        // {app} silinince IIS her istekte var olmayan bir derlemeyi yuklemeye calisirdi -> 500.
        var iss = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "AdaPanel.iss"));

        // BOLUM SINIRI SATIR BAZLI BULUNUR.
        // Ilk surum iss.IndexOf("[UninstallRun]") yapiyordu ve YANLIS YERI buluyordu: bu dosyada
        // "[UninstallRun]" ifadesi bir YORUM SATIRINDA da geciyor (bkz. [Files] bolumundeki
        // aciklama) ve arama once ONU buluyordu -> "bolum" [Files]'tan itibaren basliyor,
        // test bambaska bir satiri dogruluyordu. Kendi testim yanlis seyi olcuyordu.
        var satirlar = iss.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var basla = satirlar.FindIndex(line => line.Trim() == "[UninstallRun]");
        Assert.True(basla >= 0, "[UninstallRun] bolumu bulunamadi");

        var bitis = satirlar.FindIndex(basla + 1, line => line.TrimStart().StartsWith("[", StringComparison.Ordinal));
        if (bitis < 0) { bitis = satirlar.Count; }

        // YORUM SATIRI SAYILMAZ. Ilk surumu duz Contains yapiyordu ve mutasyon testinde HAYATTA
        // KALDI: satirin basina ';' konarak etkisizlestirildiginde metin hala dosyada oldugu icin
        // test yesil kaliyordu. Inno'da ';' ile baslayan satir YORUMDUR ve CALISMAZ.
        var etkinSatirlar = satirlar
            .GetRange(basla, bitis - basla)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith(";", StringComparison.Ordinal))
            .ToList();

        var kaldirmaSatiri = etkinSatirlar.FirstOrDefault(
            line => line.Contains("install-htaccess-module.ps1", StringComparison.OrdinalIgnoreCase));

        Assert.True(
            kaldirmaSatiri is not null,
            "[UninstallRun] icinde ETKIN bir modul kaldirma satiri yok " +
            "(yorumlanmis satir SAYILMAZ). Bu satir olmadan kaldirma islemi musterinin " +
            "butun sitelerini 500'e dusurur: GAC ve applicationHost.config kayitlari {app} disinda kalir.");

        Assert.Contains("-Remove", kaldirmaSatiri!);
    }

    [Fact]
    public void PAKETLEME_MODUL_DLLINI_TASIYOR_ve_GUCLU_AD_DENETLIYOR()
    {
        var build = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "build-installer.ps1"));

        Assert.Contains("AdaPanel.HtaccessModule.csproj", build);
        Assert.Contains("AdaPanel.HtaccessModule.dll", build);

        // Guclu ad olmadan GAC'a kurulum IMKANSIZ; kurulum sessizce "modul yok" moduna duser ve
        // ozellik hicbir zaman calismaz. Paketleme aninda yakalanmali.
        Assert.Contains("GetPublicKeyToken", build);

        // Yeni bir ikilinin sessizce paketlenmesini engelleyen denetim listesine EKLENMIS olmali.
        //
        // IKI AYRI LISTE, IKI AYRI IDDIA. Ilk surum tek bir Contains yapiyordu ve mutasyonda
        // HAYATTA KALDI: DLL adi iki listede birden geciyor, birinden silmek digerini bulan
        // iddiayi kirmiyordu. Beklenen-ikili listesinden dusmek paketlemeyi DURDURUR
        // ("beklenmeyen calistirilabilir"), imzasiz-izinli listesinden dusmek de DURDURUR
        // ("imzasiz ikili") -- ikisi de gercek ariza, ikisi de ayri ayri korunmali.
        var verify = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "verify-publish-output.ps1"));

        var beklenenIkililer = Section(verify, "$script:BeklenenIkililer");
        var imzasizIzinliler = Section(verify, "$script:ImzasizIzinliler");

        Assert.Contains("AdaPanel.HtaccessModule.dll", beklenenIkililer);
        Assert.Contains("AdaPanel.HtaccessModule.dll", imzasizIzinliler);
    }

    /// <summary>
    /// Betikteki bir dizi tanimini (<c>$ad = @( ... )</c>) blok olarak cikarir.
    ///
    /// SATIR BAZLI: ilk surum kapanisi ilk ")" karakterinde ariyordu ve YANLISTI --
    /// listedeki aciklama yorumlarinin icinde de parantez var ("(net48, guclu adli, AnyCPU)"),
    /// blok oraya kadar kirpiliyor ve test aradigini bulamiyordu.
    /// Kapanis, YALNIZ BASINA ")" olan satirdir.
    /// </summary>
    private static string Section(string script, string variableName)
    {
        var satirlar = script.Split('\n').Select(line => line.TrimEnd('\r')).ToList();

        var basla = satirlar.FindIndex(line => line.TrimStart().StartsWith(variableName, StringComparison.Ordinal));
        Assert.True(basla >= 0, $"{variableName} bulunamadi");

        var bitis = satirlar.FindIndex(basla + 1, line => line.Trim() == ")");
        Assert.True(bitis > basla, $"{variableName} blogu kapanmiyor");

        return string.Join("\n", satirlar.GetRange(basla, bitis - basla));
    }

    // ------------------------------------------------------- PERFORMANS ÖLÇÜMÜ

    [Fact]
    public void ISTEK_BASINA_EK_MALIYET_OLCULUR_ve_SINIRLANIR()
    {
        // Gorev: "Modulun istek basina ek maliyetini OLC ve RAPORLA."
        // Olculen sey SICAK YOL: modul yuklu ama kapali (sunucudaki sitelerin ezici cogunlugu).
        // Bu, .htaccess KULLANMAYAN musterilerin odedigi bedeldir ve sifira yakin olmak ZORUNDA.
        var dizin = Directory.CreateTempSubdirectory("adapanel-htaccess-perf");
        try
        {
            var dataRoot = dizin.FullName;

            // --- SENARYO A: kapi KAPALI (genel anahtar dosyasi yok) ---
            var kapali = new HtaccessRuntimeGate(dataRoot, TimeSpan.FromSeconds(2));
            var kapaliNs = Measure(() => kapali.Evaluate("olcum.example"));

            // --- SENARYO B: kapi ACIK (iki anahtar dosyasi da var, onbellekten okunuyor) ---
            var htaccessRoot = Path.Combine(dataRoot, "htaccess");
            var sitesRoot = Path.Combine(htaccessRoot, "sites");
            Directory.CreateDirectory(sitesRoot);
            File.WriteAllText(Path.Combine(htaccessRoot, "module.switch"), "enabled=1\nmode=enforce\n");
            File.WriteAllText(
                Path.Combine(sitesRoot, HtaccessHostingPaths.SiteSwitchFileName("olcum.example")),
                "enabled=1\nmode=enforce\n");

            var acik = new HtaccessRuntimeGate(dataRoot, TimeSpan.FromSeconds(2));
            Assert.Equal(HtaccessMode.Enforce, acik.Evaluate("olcum.example"));
            var acikNs = Measure(() => acik.Evaluate("olcum.example"));

            output.WriteLine("=== MODUL EK MALIYETI (istek basina, bu makine) ===");
            output.WriteLine($"  Kapi KAPALI (modul olu)   : {kapaliNs,8:F1} ns");
            output.WriteLine($"  Kapi ACIK  (onbellekten)  : {acikNs,8:F1} ns");
            output.WriteLine("  Karsilastirma: bir dosya damgasi okuma ~13.700 ns (olculdu).");

            // NOBETCI ZAMAN DEGIL TAHSISAT OLCUYOR -- ve bu bilincli bir degisiklik.
            //
            // Yakalanan regresyon suydu: kapi HER ISTEKTE anahtar dosyasinin yolunu yeniden
            // uretiyordu (ExpandEnvironmentVariables + GetFullPath + Combine). Olculen degerler
            // KAPALI 820,6 ns / ACIK 1.894,8 ns idi; yollar kurucuya tasininca 32,9 / 66,7 ns'ye
            // dustu (~25x).
            //
            // Esik eskiden 400 ns'ydi ve TAM TAKIM YUKU altinda rastgele kirmizi veriyordu: mutlak
            // duvar saati, CPU cekismesine dogrudan bagli. Esigi gevsetmek ise nobetciyi ISE
            // YARAMAZ yapardi -- 2000 ns'e cikarsaydik yakalamasi gereken 820 ns'lik regresyonu
            // KACIRIRDI.
            //
            // Cozum: sorunun KENDISINI olc. O regresyonun urettigi sey dizgiydi, yani TAHSISAT.
            // Sicak yolda tahsisat sifir olmak ZORUNDA ve bu olcum makinenin yukunden bagimsizdir.
            // Sure yine raporlaniyor (yukarida) ama artik iddia edilmiyor -- bilgi, nobetci degil.
            var kapaliBayt = MeasureAllocation(() => kapali.Evaluate("olcum.example"));
            var acikBayt = MeasureAllocation(() => acik.Evaluate("olcum.example"));

            output.WriteLine($"  Kapi KAPALI tahsisat      : {kapaliBayt,8:F2} bayt/istek");
            output.WriteLine($"  Kapi ACIK  tahsisat       : {acikBayt,8:F2} bayt/istek");

            // 8 bayt pay: JIT/GC olcum gurultusu icin. Yol yeniden uretilirse istek basina
            // yuzlerce bayt tahsis edilir, yani bu esik o hatayi kesin yakalar.
            Assert.True(kapaliBayt < 8, $"kapi kapaliyken sicak yol tahsisat yapiyor: {kapaliBayt:F2} bayt/istek");
            Assert.True(acikBayt < 8, $"kapi acikken sicak yol tahsisat yapiyor: {acikBayt:F2} bayt/istek");
        }
        finally
        {
            dizin.Delete(recursive: true);
        }
    }

    private static double Measure(Func<HtaccessMode> action)
    {
        for (var i = 0; i < 10_000; i++) { action(); }   // isinma + JIT

        const int iterations = 200_000;
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) { action(); }
        stopwatch.Stop();

        return stopwatch.Elapsed.TotalMilliseconds * 1_000_000.0 / iterations;
    }

    /// <summary>
    /// İstek başına TAHSİSAT (bayt). Duvar saatinin aksine makinenin yükünden etkilenmez —
    /// nöbetçinin ölçmesi gereken şey bu: sıcak yola dizgi/dosya yolu üretimi eklendi mi.
    /// </summary>
    private static double MeasureAllocation(Func<HtaccessMode> action)
    {
        for (var i = 0; i < 10_000; i++) { action(); }   // ısınma + JIT (JIT'in kendi tahsisatı sayılmasın)

        const int iterations = 100_000;
        var oncesi = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++) { action(); }
        var sonrasi = GC.GetAllocatedBytesForCurrentThread();

        return (sonrasi - oncesi) / (double)iterations;
    }
}
