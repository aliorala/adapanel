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
