using System.Security.AccessControl;
using System.Security.Principal;
using AdaPanel.Infrastructure.Bootstrap;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// K1/K2/K4/K5 — veri kökü ve <c>runtimes</c> sertleştirmesinin GERÇEK davranışı.
/// </summary>
/// <remarks>
/// <para>
/// Denetimde ÖLÇÜLEN durum: <c>data</c>, <c>keys</c>, <c>certs</c>, <c>config</c>, <c>backups</c>,
/// <c>mail-certs</c> klasörlerinin hepsinde miras kapalıydı ve <c>Everyone = FullControl</c>
/// GÖMÜLÜYDÜ; çünkü sertleştirme mirası KOPYALAYIP mühürlüyor, sonra yalnız iki SID siliyordu.
/// Miras kapalı olduğu için kök düzeltilse bile o ACE bir daha düşmüyordu.
/// </para>
/// <para>
/// Buradaki testler ACL'i taklit ETMEZ: geçici klasöre gerçek bir <c>Everyone</c> ACE'i yazar,
/// sertleştirmeyi uygular ve ACL'i diskten GERİ OKUR. Yükseltilmiş yetki gerekmez — testin kendi
/// oluşturduğu klasörün sahibi olduğu için DACL'ini yazabilir.
/// </para>
/// </remarks>
public sealed class DataRootHardeningTests : IDisposable
{
    private readonly string calisma = Path.Combine(Path.GetTempPath(), $"ada-acl-{Guid.NewGuid():N}");

    public DataRootHardeningTests() => Directory.CreateDirectory(calisma);

    public void Dispose()
    {
        try { Directory.Delete(calisma, recursive: true); } catch { /* en iyi caba */ }
    }

    private static SecurityIdentifier Everyone => new(WellKnownSidType.WorldSid, null);

    private static SecurityIdentifier Users => new(WellKnownSidType.BuiltinUsersSid, null);

    private static void HerkeseTamYetkiVer(string dizin)
    {
        var info = new DirectoryInfo(dizin);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            Everyone,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    private static IReadOnlyList<SecurityIdentifier> DiskteAcikDuranSidler(string dizin)
    {
        var rules = new DirectoryInfo(dizin)
            .GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier));

        return rules
            .Cast<FileSystemAccessRule>()
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// K4: Sertleştirme geniş izinleri MÜHÜRLEMEMELİ, TEMİZLEMELİ.
    /// Klasöre önce gerçek bir <c>Everyone = FullControl</c> ACE'i yazılır; sertleştirmeden sonra
    /// diskten geri okunan ACL'de o ACE OLMAMALIDIR.
    /// </summary>
    [Fact]
    public void Hassas_klasorde_Everyone_ACE_i_muhurlenmez_TEMIZLENIR()
    {
        var dataRoot = Path.Combine(calisma, "veri-koku");
        var data = Path.Combine(dataRoot, "data");
        Directory.CreateDirectory(data);
        HerkeseTamYetkiVer(data);

        // Ön koşul: Everyone GERÇEKTEN yazılabildi (yazılamadıysa test bir şey kanıtlamaz).
        Assert.Contains(Everyone, DiskteAcikDuranSidler(data));

        DataRootHardeningService.HardenAsync(dataRoot, NullLogger.Instance).GetAwaiter().GetResult();

        var kalanlar = DiskteAcikDuranSidler(data);
        Assert.DoesNotContain(Everyone, kalanlar);
        Assert.DoesNotContain(Users, kalanlar);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), kalanlar);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), kalanlar);
    }

    /// <summary>
    /// K4: Kurulan DACL "korumalı" (miras kapalı) olmalı, aksi halde kökten yeni geniş ACE'ler düşer.
    /// </summary>
    [Fact]
    public void Hassas_klasorun_DACL_i_miras_almaz()
    {
        var dataRoot = Path.Combine(calisma, "miras");
        Directory.CreateDirectory(dataRoot);

        DataRootHardeningService.HardenAsync(dataRoot, NullLogger.Instance).GetAwaiter().GetResult();

        var security = new DirectoryInfo(Path.Combine(dataRoot, "keys")).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
    }

    /// <summary>
    /// K4 (saf fonksiyon): Sıfırdan kurulan tanımlayıcıda yalnız SYSTEM + Administrators
    /// (+ süreç kimliği) bulunur ve miras KOPYALANMAZ.
    /// </summary>
    [Fact]
    public void Sifirdan_kurulan_DACL_yalniz_sahiplerini_icerir()
    {
        var security = DataRootHardeningService.BuildSensitiveDirectorySecurity();
        var sidler = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .ToArray();

        Assert.True(security.AreAccessRulesProtected);
        Assert.DoesNotContain(Everyone, sidler);
        Assert.DoesNotContain(Users, sidler);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), sidler);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), sidler);
        Assert.All(
            security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            rule => Assert.Equal(AccessControlType.Allow, rule.AccessControlType));
    }

    /// <summary>
    /// K5: Kurtarma ağı <c>icacls /reset</c> ile mirası geri açıyor ve K1'i iptal ediyordu; yeniden
    /// sertleştirme BİR SONRAKİ SERVİS BAŞLANGICINA kadar yapılmıyordu.
    /// Bu test gerçek akışı koşturur: reset'in ardından AYNI çağrıda klasör tekrar kapanmış olmalı.
    /// </summary>
    [Fact]
    public void Kurtarma_agi_sifirladigi_izinleri_AYNI_cagrida_yeniden_kapatir()
    {
        // Gerçek arıza kurgusu: geniş ACE veri kökünün ÜSTÜNDE durur (canlıda ProgramData'daki
        // Everyone=FullControl) ve alt klasörlere MİRASLA iner. `icacls /reset` tam da bu mirası
        // geri açtığı için K1'i iptal ediyordu.
        var dataRoot = Path.Combine(calisma, "kurtarma");
        Directory.CreateDirectory(dataRoot);
        HerkeseTamYetkiVer(dataRoot);
        var data = Path.Combine(dataRoot, "data");
        Directory.CreateDirectory(data);
        Assert.Contains(Everyone, DiskteAcikDuranSidler(data));

        DataRootHardeningService.EnsureWritableAsync(dataRoot, NullLogger.Instance).GetAwaiter().GetResult();

        var security = new DirectoryInfo(data).GetAccessControl();
        // /reset mirası GERİ AÇAR; aynı çağrıda yeniden sertleştirme yapılmazsa burası "false" kalır.
        Assert.True(security.AreAccessRulesProtected, "icacls /reset sonrası miras yeniden KAPATILMAMIŞ (K5).");

        var kalanlar = DiskteAcikDuranSidler(data);
        Assert.DoesNotContain(Everyone, kalanlar);
        Assert.DoesNotContain(Users, kalanlar);
        // Panel kendi db'sini yazabilmeli: SYSTEM/Administrators tam yetki KALMALI (fazla kapatma kontrolü).
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), kalanlar);
    }

    /// <summary>K5: Adım sırasının SON adımı yeniden sertleştirme olmalı — sıra tek kaynaktan sürülür.</summary>
    [Fact]
    public void Kurtarma_adim_sirasi_harden_ile_biter()
    {
        var adimlar = DataRootHardeningService.RecoveryStepOrder();

        Assert.Equal("harden", adimlar[^1]);
        Assert.Contains("reset:data", adimlar);
        Assert.Contains("reset:keys", adimlar);
    }

    /// <summary>
    /// K1: Tek dosya kilidi — posta sunucusunun korunan kimlik dosyası ve panel sertifika parolası
    /// bu yolla kapatılır. Everyone ACE'i yazılır, kilit uygulanır, ACL geri okunur.
    /// </summary>
    [Fact]
    public void ProtectFile_dosyayi_kiraciya_kapatir()
    {
        var dizin = Path.Combine(calisma, "dosya-kilidi");
        Directory.CreateDirectory(dizin);
        HerkeseTamYetkiVer(dizin);
        var dosya = Path.Combine(dizin, "gizli.dat");
        File.WriteAllText(dosya, "sir");

        DataRootHardeningService.ProtectFile(dosya, NullLogger.Instance);

        var security = new FileInfo(dosya).GetAccessControl();
        var sidler = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .ToArray();

        Assert.True(security.AreAccessRulesProtected);
        Assert.DoesNotContain(Everyone, sidler);
        Assert.DoesNotContain(Users, sidler);
    }

    /// <summary>
    /// GÜNCELLEME KLASÖRÜ (K1 — bu makinede ölçülerek bulundu, listede YOKTU).
    /// </summary>
    /// <remarks>
    /// <para>
    /// ÖLÇÜM: 0.8.4 kurulu ve çalışır durumdayken
    /// <c>C:\ProgramData\AdaPanel\updates</c> DACL'i <c>(A;CIID;FA;;;WD)</c> taşıyordu — Everyone =
    /// FullControl, mirasla gelmiş, koruma bayrağı yok. Aynı anda ölçülen <c>config</c> ise
    /// <c>D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)</c> ile kapalıydı. Tek fark: <c>updates</c>
    /// <see cref="DataRootHardeningService.SensitiveDirectories"/> listesinde yoktu.
    /// </para>
    /// <para>
    /// NEDEN EN AĞIRI: bu klasördeki <c>apply-update.ps1</c> zamanlanmış görevle SYSTEM olarak
    /// (<c>-RunLevel Highest</c>) çalıştırılıyor. Klasör üzerindeki FullControl, dosyanın kendi
    /// izninden BAĞIMSIZ olarak "sil + yerine koy" hakkı verir. Paket tarafındaki üç savunma
    /// (SHA256, yayıncı/CompanyName, izinli depo) betiğe hiç bakmaz — yani kiracıdan SYSTEM'e
    /// denetimsiz bir yol açıktı.
    /// </para>
    /// <para>
    /// Test ACL'i TAKLİT ETMEZ: gerçek bir Everyone ACE'i yazar, sertleştirmeyi çalıştırır ve
    /// ACL'i diskten geri okur. Liste değiştirilirse (mutasyon) burası kırmızı yanar.
    /// </para>
    /// </remarks>
    [Fact]
    public void Guncelleme_paketi_klasoru_kiraciya_KAPATILIR()
    {
        var dataRoot = Path.Combine(calisma, "guncelleme-koku");
        var updates = Path.Combine(dataRoot, "updates");
        Directory.CreateDirectory(updates);
        HerkeseTamYetkiVer(updates);

        // Ön koşul: Everyone GERÇEKTEN yazılabildi (yazılamadıysa test bir şey kanıtlamaz).
        Assert.Contains(Everyone, DiskteAcikDuranSidler(updates));

        DataRootHardeningService.HardenAsync(dataRoot, NullLogger.Instance).GetAwaiter().GetResult();

        var kalanlar = DiskteAcikDuranSidler(updates);
        Assert.DoesNotContain(Everyone, kalanlar);
        Assert.DoesNotContain(Users, kalanlar);

        // Kökten yeni geniş ACE düşmesin: miras KAPALI olmalı.
        Assert.True(
            new DirectoryInfo(updates).GetAccessControl().AreAccessRulesProtected,
            "updates klasörünün DACL'i korumasız; kökteki Everyone mirasla geri düşer.");

        // FAZLA KAPATMA KONTROLÜ: klasöre dokunan her şey SYSTEM'dir (panel servisi ve onun
        // kurduğu zamanlanmış görev). SYSTEM düşerse güncelleme yolu tamamen ölür.
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), kalanlar);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), kalanlar);
    }

    /// <summary>
    /// İNDİRME KLASÖRÜ (K1 — updates'in kardeşi, bu makinede ölçülerek bulundu).
    /// </summary>
    /// <remarks>
    /// <para>
    /// ÖLÇÜM: <c>C:\ProgramData\AdaPanel\downloads</c> DACL'i <c>Everyone:(I)(CI)(F)</c> +
    /// <c>BUILTIN\Users:(WD,AD)</c> taşıyordu; içinde <c>requestRouter_amd64.msi</c>,
    /// <c>rewrite_amd64_en-US.msi</c> ve mariadb/php zip'leri duruyordu. <c>ComponentBootstrapper</c>
    /// bu klasöre indirip dosyayı SYSTEM olarak <c>msiexec /i</c> ile çalıştırıyor. sha256 doğrulaması
    /// yalnız hash YAYIMLAYAN kaynaklar için var; <c>sha256: string.Empty</c> ile inen bileşenlerde
    /// hiç doğrulama yok. Yani kiracı, dosyayı kurulumdan önce değiştirerek SYSTEM'e kod geçirebilir.
    /// </para>
    /// <para>
    /// Test ACL'i TAKLİT ETMEZ: gerçek bir Everyone ACE'i yazar, sertleştirmeyi koşar, ACL'i diskten
    /// geri okur. Liste değiştirilirse (mutasyon: "downloads" silinirse) burası kırmızı yanar.
    /// </para>
    /// </remarks>
    [Fact]
    public void Indirilen_paket_klasoru_kiraciya_KAPATILIR()
    {
        var dataRoot = Path.Combine(calisma, "indirme-koku");
        var downloads = Path.Combine(dataRoot, "downloads");
        Directory.CreateDirectory(downloads);
        HerkeseTamYetkiVer(downloads);

        Assert.Contains(Everyone, DiskteAcikDuranSidler(downloads));

        DataRootHardeningService.HardenAsync(dataRoot, NullLogger.Instance).GetAwaiter().GetResult();

        var kalanlar = DiskteAcikDuranSidler(downloads);
        Assert.DoesNotContain(Everyone, kalanlar);
        Assert.DoesNotContain(Users, kalanlar);
        Assert.True(
            new DirectoryInfo(downloads).GetAccessControl().AreAccessRulesProtected,
            "downloads klasörünün DACL'i korumasız; kökteki Everyone mirasla geri düşer.");

        // FAZLA KAPATMA KONTROLÜ: indiren/çalıştıran tek taraf SYSTEM (ComponentBootstrapper).
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), kalanlar);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), kalanlar);
    }

    /// <summary>
    /// tools\win-acme (K1 — updates'in kardeşi, bu makinede ölçüldü): <c>wacs.exe</c> panel (SYSTEM)
    /// tarafından çalıştırılıyor ve klasör <c>Everyone:(I)(CI)(F)</c> idi → kiracı betiği/DLL'i
    /// değiştirip SYSTEM olur. <c>tools</c> ÜSTÜ kapatılamaz (phpMyAdmin/Roundcube IIS ile kiracıya
    /// sunulur), yalnız <c>win-acme</c> düşürülür.
    /// </summary>
    /// <remarks>
    /// Bu test HEM kapanmayı HEM fazla-kapatmama garantisini birlikte ölçer: win-acme'de Everyone
    /// düşmeli, phpmyadmin'de KALMALI. Mutasyon (LockedToolDirectories boşaltılırsa VEYA phpmyadmin
    /// de listeye eklenirse) ikisinden biri kırmızı yanar.
    /// </remarks>
    [Fact]
    public void Tools_win_acme_kapatilir_phpmyadmin_e_DOKUNMAZ()
    {
        var dataRoot = Path.Combine(calisma, "tools-koku");
        var winAcme = Path.Combine(dataRoot, "tools", "win-acme");
        var phpMyAdmin = Path.Combine(dataRoot, "tools", "phpmyadmin");
        Directory.CreateDirectory(winAcme);
        Directory.CreateDirectory(phpMyAdmin);
        HerkeseTamYetkiVer(winAcme);
        HerkeseTamYetkiVer(phpMyAdmin);

        // Ön koşul: iki klasörde de Everyone GERÇEKTEN yazılabildi.
        Assert.Contains(Everyone, DiskteAcikDuranSidler(winAcme));
        Assert.Contains(Everyone, DiskteAcikDuranSidler(phpMyAdmin));

        DataRootHardeningService.HardenAsync(dataRoot, NullLogger.Instance).GetAwaiter().GetResult();

        // win-acme: SYSTEM'in çalıştırdığı araç → kiracı düşürülür.
        var winAcmeKalan = DiskteAcikDuranSidler(winAcme);
        Assert.DoesNotContain(Everyone, winAcmeKalan);
        Assert.DoesNotContain(Users, winAcmeKalan);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), winAcmeKalan);

        // phpMyAdmin: IIS kiracı havuz kimliğiyle sunar → kapatılırsa phpMyAdmin 500 verir. DOKUNULMAZ.
        Assert.Contains(Everyone, DiskteAcikDuranSidler(phpMyAdmin));
    }

    /// <summary>
    /// Hassas klasör listesi İKİ YERDE yazılı: servis açılışta uygular
    /// (<see cref="DataRootHardeningService.SensitiveDirectories"/>), <c>harden-adapanel.ps1</c>
    /// elle uygular. Birine eklenip diğerine eklenmemesi, "sertleştirildi" denilen bir klasörün
    /// yalnızca yarı yarıya kapanması demektir — <c>updates</c>'te tam olarak bu yaşandı.
    /// Bu yüzden liste betikten OKUNUR, tekrar YAZILMAZ.
    /// </summary>
    [Fact]
    public void Betikteki_hassas_klasor_listesi_servisinkiyle_AYNI()
    {
        var betik = Path.Combine(RepoKoku(), "scripts", "harden-adapanel.ps1");
        Assert.True(File.Exists(betik), $"Sertleştirme betiği bulunamadı: {betik}");

        var eslesme = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(betik),
            @"\$sensitive\s*=\s*@\(([^)]*)\)");
        Assert.True(eslesme.Success, "harden-adapanel.ps1 içinde '$sensitive = @(...)' bulunamadı.");

        var betiktekiler = System.Text.RegularExpressions.Regex
            .Matches(eslesme.Groups[1].Value, "\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.Equal(DataRootHardeningService.SensitiveDirectories, betiktekiler);
    }

    /// <summary>Test derlemesinden depo köküne çık (bin\&lt;yapılandırma&gt;\&lt;tfm&gt;\ → 5 seviye).</summary>
    private static string RepoKoku()
    {
        var dizin = new DirectoryInfo(AppContext.BaseDirectory);
        while (dizin is not null && !File.Exists(Path.Combine(dizin.FullName, "Directory.Build.props")))
        {
            dizin = dizin.Parent;
        }

        Assert.NotNull(dizin);
        return dizin!.FullName;
    }

    // ===================================================================================
    // K2 — runtimes ayrımı: IIS'in RX'e MUHTAÇ olduğu yollar ile olmadığı yollar
    // ===================================================================================

    /// <summary>
    /// K2: IIS FastCGI php-cgi.exe'yi uygulama havuzu kimliğiyle, zamanlanmış görevler node/python'ı
    /// sınırlı yerel kullanıcıyla çalıştırır. Bu yollar KAPATILAMAZ — kapatılırsa siteler çöker.
    /// </summary>
    [Theory]
    [InlineData("php")]
    [InlineData("php\\8.3")]
    [InlineData("nodejs")]
    [InlineData("python\\3")]
    [InlineData("nginx")]
    [InlineData("redis")]
    [InlineData("memcached")]
    [InlineData("docker")]
    [InlineData("mariadb")]          // motor kökü: bin\ burada, istemciler/araçlar kırılmasın
    [InlineData("mariadb\\11")]      // sürüm klasörünün kendisi de kapatılmaz
    [InlineData("mariadb\\11\\bin")]
    public void IIS_ve_zamanlanmis_gorevlerin_muhtac_oldugu_runtimes_yollari_KAPATILMAZ(string goreli)
    {
        Assert.False(DataRootHardeningService.IsRuntimeLockdownTarget(goreli));
    }

    /// <summary>
    /// K2/K1: Veritabanı veri dizinleri (ölçüldü: <c>global_priv.frm</c> yükseltilmemiş kullanıcı
    /// olarak açıldı) ve tamamı panele ait posta ağacı KAPATILIR.
    /// </summary>
    [Theory]
    [InlineData("mariadb\\11\\data")]
    [InlineData("mysql\\8\\data")]
    [InlineData("postgresql\\16\\data")]
    [InlineData("postgresql\\data")]
    [InlineData("stalwart")]
    [InlineData("stalwart\\etc")]
    [InlineData("stalwart-cli")]
    [InlineData("vandelay")]
    public void Kiracinin_isi_olmayan_runtimes_yollari_KAPATILIR(string goreli)
    {
        Assert.True(DataRootHardeningService.IsRuntimeLockdownTarget(goreli));
    }

    /// <summary>
    /// K2: Üstü kapatılan bir ağacın altı ayrıca yazılmaz (tek ACL yazımı tüm ağacı kapsar) ve
    /// dokunulmaması gereken yollar sonuçta HİÇ görünmez.
    /// </summary>
    [Fact]
    public void Kapatilacak_yollarin_EN_AZ_kumesi_dondurulur()
    {
        var hedefler = DataRootHardeningService.ResolveRuntimeLockdownTargets(
        [
            "php",
            "php\\8.3",
            "stalwart",
            "stalwart\\etc",
            "stalwart\\data",
            "mariadb",
            "mariadb\\11",
            "mariadb\\11\\data",
            "nodejs"
        ]);

        Assert.Contains("stalwart", hedefler);
        Assert.Contains("mariadb\\11\\data", hedefler);
        Assert.DoesNotContain("stalwart\\etc", hedefler);   // üstü zaten kapalı
        Assert.DoesNotContain("php", hedefler);             // IIS'e lazım
        Assert.DoesNotContain("php\\8.3", hedefler);
        Assert.DoesNotContain("nodejs", hedefler);
        Assert.DoesNotContain("mariadb", hedefler);         // yalnız data kapanır, motor kökü değil
        Assert.Equal(2, hedefler.Count);
    }

    /// <summary>
    /// K1/K2 uygulama: Sertleştirme çalıştığında <c>runtimes\stalwart</c> gerçekten kiracıya kapanır,
    /// <c>runtimes\php</c> ise DOKUNULMADAN kalır (fazla kapatma IIS'i kırardı).
    /// </summary>
    [Fact]
    public void Runtimes_sertlestirmesi_stalwart_i_kapatir_php_ye_DOKUNMAZ()
    {
        var dataRoot = Path.Combine(calisma, "runtimes-koku");
        var stalwart = Path.Combine(dataRoot, "runtimes", "stalwart");
        var stalwartEtc = Path.Combine(stalwart, "etc");
        var php = Path.Combine(dataRoot, "runtimes", "php");
        var phpSurum = Path.Combine(php, "8.3");
        Directory.CreateDirectory(stalwartEtc);
        Directory.CreateDirectory(phpSurum);
        // Geniş erişim BİLEŞEN KÖKÜNE yazılır: alt klasörler onu mirasla alır (canlıdaki durum).
        HerkeseTamYetkiVer(stalwart);
        HerkeseTamYetkiVer(php);

        DataRootHardeningService.HardenAsync(dataRoot, NullLogger.Instance).GetAwaiter().GetResult();

        Assert.DoesNotContain(Everyone, DiskteAcikDuranSidler(stalwart));
        Assert.DoesNotContain(Everyone, DiskteAcikDuranSidler(stalwartEtc));  // ağacın tamamı kapandı
        // PHP: kiracı erişimi KORUNMALI — Everyone'ın hem kökte hem sürüm klasöründe durması
        // "dokunulmadı"nın kanıtı. Kapanırsa IIS FastCGI php-cgi.exe'yi çalıştıramaz, siteler 500 verir.
        Assert.Contains(Everyone, DiskteAcikDuranSidler(php));
        Assert.Contains(Everyone, DiskteAcikDuranSidler(phpSurum));
    }
}
