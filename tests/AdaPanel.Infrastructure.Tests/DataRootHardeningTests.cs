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

    private static SecurityIdentifier IisIusrs => new("S-1-5-32-568");

    /// <summary>
    /// "Yazma" sayilan HER bit. Bir korumanin ise yarayip yaramadigini olcerken hakki adiyla degil
    /// BITIYLE sormak gerekiyor: <c>Modify</c> ve <c>FullControl</c> ayri adlardir ama ayni bitleri
    /// tasir; ad karsilastirmasi (bu depoda bir kez oldugu gibi) hicbir sey olcmez.
    /// </summary>
    private const FileSystemRights YazmaMaskesi =
        FileSystemRights.WriteData |                        // = CreateFiles
        FileSystemRights.AppendData |                       // = CreateDirectories
        FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles |     // FILE_DELETE_CHILD: php.ini'yi SILDIREN bit
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership;

    /// <summary>
    /// Verilen SID'e NESNENIN KENDISINDE tanınan Allow haklarının birleşimi.
    /// <c>InheritOnly</c> kurallar sayılmaz: onlar yalnız çocuklara işler.
    /// </summary>
    private static FileSystemRights NesneyeUygulananHaklar(string yol, SecurityIdentifier sid)
    {
        FileSystemRights toplam = 0;
        var kurallar = new DirectoryInfo(yol)
            .GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier));

        foreach (FileSystemAccessRule kural in kurallar)
        {
            if (kural.AccessControlType != AccessControlType.Allow ||
                !sid.Equals(kural.IdentityReference) ||
                kural.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
            {
                continue;
            }

            toplam |= kural.FileSystemRights;
        }

        return toplam;
    }

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

    /// <summary>
    /// <c>C:\ProgramData</c>'nın BU MAKİNEDE ÖLÇÜLEN DACL'ini bir test klasörüne kurar; veri kökünün
    /// canlıda ne devraldığı böylece taklit değil, kopya olur.
    /// </summary>
    /// <remarks>
    /// icacls çıktısı (ölçüm):
    /// <code>
    /// C:\ProgramData Everyone:(CI)(F)
    ///                NT AUTHORITY\SYSTEM:(OI)(CI)(F)
    ///                BUILTIN\Administrators:(OI)(CI)(F)
    ///                CREATOR OWNER:(OI)(CI)(IO)(F)
    ///                BUILTIN\Users:(OI)(CI)(RX)
    ///                BUILTIN\Users:(CI)(WD,AD,WEA,WA)
    /// </code>
    /// İki ayrıntı ölçümün özüdür: <c>Everyone</c> ACE'i <c>(CI)</c>'dir (dosyalara İNMEZ ama
    /// klasörlerde FullControl'dür → FILE_DELETE_CHILD) ve <c>Users</c> ayrı bir yazma ACE'i taşır
    /// (Everyone düşse bile dosya/klasör oluşturmaya yeter).
    /// </remarks>
    private static void ProgramDataAclindenKopyala(string dizin)
    {
        var info = new DirectoryInfo(dizin);
        var security = info.GetAccessControl();

        security.AddAccessRule(new FileSystemAccessRule(
            Everyone, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            Users, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            Users,
            FileSystemRights.WriteData | FileSystemRights.AppendData |
            FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes,
            InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));

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
    public async Task Hassas_klasorde_Everyone_ACE_i_muhurlenmez_TEMIZLENIR()
    {
        var dataRoot = Path.Combine(calisma, "veri-koku");
        var data = Path.Combine(dataRoot, "data");
        Directory.CreateDirectory(data);
        HerkeseTamYetkiVer(data);

        // Ön koşul: Everyone GERÇEKTEN yazılabildi (yazılamadıysa test bir şey kanıtlamaz).
        Assert.Contains(Everyone, DiskteAcikDuranSidler(data));

        await DataRootHardeningService.HardenAsync(dataRoot, NullLogger.Instance);

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
    public async Task Hassas_klasorun_DACL_i_miras_almaz()
    {
        var dataRoot = Path.Combine(calisma, "miras");
        Directory.CreateDirectory(dataRoot);

        await DataRootHardeningService.HardenAsync(dataRoot, NullLogger.Instance);

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
    public async Task Kurtarma_agi_sifirladigi_izinleri_AYNI_cagrida_yeniden_kapatir()
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

        await DataRootHardeningService.EnsureWritableAsync(dataRoot, NullLogger.Instance);

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
    public async Task Guncelleme_paketi_klasoru_kiraciya_KAPATILIR()
    {
        var dataRoot = Path.Combine(calisma, "guncelleme-koku");
        var updates = Path.Combine(dataRoot, "updates");
        Directory.CreateDirectory(updates);
        HerkeseTamYetkiVer(updates);

        // Ön koşul: Everyone GERÇEKTEN yazılabildi (yazılamadıysa test bir şey kanıtlamaz).
        Assert.Contains(Everyone, DiskteAcikDuranSidler(updates));

        await DataRootHardeningService.HardenAsync(dataRoot, NullLogger.Instance);

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
    public async Task Indirilen_paket_klasoru_kiraciya_KAPATILIR()
    {
        var dataRoot = Path.Combine(calisma, "indirme-koku");
        var downloads = Path.Combine(dataRoot, "downloads");
        Directory.CreateDirectory(downloads);
        HerkeseTamYetkiVer(downloads);

        Assert.Contains(Everyone, DiskteAcikDuranSidler(downloads));

        await DataRootHardeningService.HardenAsync(dataRoot, NullLogger.Instance);

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
    /// HASSAS SAYILAN HER KLASOR GERCEKTEN KAPANIR — tek tek degil, LISTENIN TAMAMI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden veri gudumlu:</b> bu listeye bir klasor eklemek ucuz, ama eklemenin ISE YARADIGINI
    /// olcmek ayri bir istir. Tek tek yazilan testler yalnizca o gun akla gelen klasoru korur;
    /// sonradan eklenen (bu turda <c>components</c> ve <c>logs</c>) sessizce olcusuz kalirdi.
    /// Liste buyudukce koruma da kendiliginde buyusun diye test listeden BESLENIR.
    /// </para>
    /// <para>
    /// <b>Olculen sey taklit degil:</b> gercek bir Everyone ACE'i diske yazilir, <c>HardenAsync</c>
    /// kosar, ACL DISKTEN geri okunur. Ayrica FAZLA KAPATMA olculur: SYSTEM ve Administrators
    /// dusmus olmamali — panel kendi verisine erisemezse acigi kapatmis olmayiz, urunu kirmis
    /// oluruz.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Hassas_klasorlerin_HEPSI_kiraciya_kapatilir()
    {
        var dataRoot = Path.Combine(calisma, "toplu-hassas");
        var klasorler = DataRootHardeningService.SensitiveDirectories;

        // Kendi kendini denetleyen alt sinir: liste bosalirsa test "sorun yok" diyerek YESIL kalmasin.
        Assert.True(klasorler.Length >= 8, $"Hassas klasor listesi daralmis: {klasorler.Length}");

        foreach (var ad in klasorler)
        {
            var yol = Path.Combine(dataRoot, ad);
            Directory.CreateDirectory(yol);
            HerkeseTamYetkiVer(yol);
            Assert.Contains(Everyone, DiskteAcikDuranSidler(yol));
        }

        await DataRootHardeningService.HardenAsync(dataRoot, NullLogger.Instance);

        foreach (var ad in klasorler)
        {
            var yol = Path.Combine(dataRoot, ad);
            var kalanlar = DiskteAcikDuranSidler(yol);

            Assert.DoesNotContain(Everyone, kalanlar);
            Assert.DoesNotContain(Users, kalanlar);
            Assert.True(
                new DirectoryInfo(yol).GetAccessControl().AreAccessRulesProtected,
                $"'{ad}' DACL'i korumasiz; koktekiler mirasla geri duser.");

            // FAZLA KAPATMA: panelin kendisi (SYSTEM) ve yonetici erisimini KAYBETMEMELI.
            Assert.Contains(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), kalanlar);
            Assert.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), kalanlar);
        }
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
    public async Task Tools_win_acme_kapatilir_phpmyadmin_e_DOKUNMAZ()
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

        await DataRootHardeningService.HardenAsync(dataRoot, NullLogger.Instance);

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
    public async Task Runtimes_sertlestirmesi_stalwart_i_kapatir_php_ye_DOKUNMAZ()
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

        await DataRootHardeningService.HardenAsync(dataRoot, NullLogger.Instance);

        Assert.DoesNotContain(Everyone, DiskteAcikDuranSidler(stalwart));
        Assert.DoesNotContain(Everyone, DiskteAcikDuranSidler(stalwartEtc));  // ağacın tamamı kapandı

        // PHP: ÇALIŞTIRMA hakkı KORUNMALI. (K6'dan önce burada "Everyone hâlâ duruyor mu" sorulurdu;
        // o soru artık YANLIŞ — Everyone bilerek düşürüldü, kiracıya RX 'Users' üzerinden veriliyor.
        // Ölçülmesi gereken şey grubun adı değil, php-cgi.exe'nin ÇALIŞTIRILABİLİR kalması.)
        foreach (var yol in new[] { php, phpSurum })
        {
            var kullanici = NesneyeUygulananHaklar(yol, Users);
            Assert.True(
                kullanici.HasFlag(FileSystemRights.ReadAndExecute),
                $"'{yol}' kiracıya okuma/çalıştırma vermiyor; IIS FastCGI php-cgi.exe'yi çalıştıramaz (500).");
            Assert.True(
                NesneyeUygulananHaklar(yol, IisIusrs).HasFlag(FileSystemRights.ReadAndExecute),
                $"'{yol}' IIS_IUSRS'a okuma/çalıştırma vermiyor; IUSR (fastcgi.impersonate=1) php.ini'yi okuyamaz.");
        }
    }

    // ===================================================================================
    // K6 — runtimes: KİRACI OKUR VE ÇALIŞTIRIR, YAZAMAZ
    // ===================================================================================

    /// <summary>
    /// K6 ANA ÖLÇÜM: <c>runtimes</c> ağacında kiracı kimlikleri okuyup çalıştırabilmeli ama
    /// YAZAMAMALI — ne kökte, ne alt klasörde.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ÖLÇÜLEN AÇIK (canlı, icacls): <c>C:\ProgramData\AdaPanel\runtimes</c> ve
    /// <c>...\runtimes\php\8.3</c> DACL'i <c>Everyone:(I)(CI)(F)</c> taşıyordu; gerçek
    /// <c>php-cgi.exe</c> ile denendiğinde kiracı PHP'si klasöre 26 baytlık dosyayı YAZABİLDİ.
    /// Aynı ACE <c>FILE_DELETE_CHILD</c> de verdiği için <c>php.ini</c> silinip değiştirilebiliyordu —
    /// yani site başına PHP izolasyonunun (<c>[PATH=]</c> blokları) dayandığı dosya, izole edilen
    /// tarafın elindeydi.
    /// </para>
    /// <para>
    /// Test ACL'i DİZE OLARAK karşılaştırmaz (bu depoda bir sertleştirme testi tam da bu yüzden hiçbir
    /// şey ölçmemişti): gerçek bir <c>Everyone = FullControl</c> ACE'i diske yazılır, sertleştirme
    /// koşar, ACL diskten <see cref="DirectorySecurity"/> olarak geri okunur ve HAKLAR BİT BİT sınanır.
    /// </para>
    /// <para>
    /// Alt klasör ayrıca ölçülüyor çünkü sertleştirme yalnız KÖKE yazıyor: burada sınanan şey,
    /// Windows'un devralınabilir ACE'leri mirası açık çocuklara gerçekten yayıp yaymadığıdır.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Runtimes_agacinda_kiraci_OKUR_CALISTIRIR_ama_YAZAMAZ()
    {
        var dataRoot = Path.Combine(calisma, "runtimes-salt-okunur");
        var runtimes = Path.Combine(dataRoot, "runtimes");
        var phpSurum = Path.Combine(runtimes, "php", "8.3");
        Directory.CreateDirectory(phpSurum);
        // Canlıdaki durum: geniş ACE runtimes KÖKÜNDE durur ve alt klasörlere mirasla iner.
        HerkeseTamYetkiVer(runtimes);

        // Ön koşul: Everyone GERÇEKTEN yazılabildi ve YAZMA hakkı hem kökte hem altta duruyor.
        Assert.NotEqual((FileSystemRights)0, NesneyeUygulananHaklar(runtimes, Everyone) & YazmaMaskesi);
        Assert.NotEqual((FileSystemRights)0, NesneyeUygulananHaklar(phpSurum, Everyone) & YazmaMaskesi);

        // Panel bunu HER AÇILIŞTA koşturur → iki kez koşup kendi çıktısını bozmadığı da ölçülür.
        await DataRootHardeningService.HardenAsync(dataRoot, NullLogger.Instance);
        await DataRootHardeningService.HardenAsync(dataRoot, NullLogger.Instance);

        foreach (var yol in new[] { runtimes, phpSurum })
        {
            foreach (var kiraci in new[] { Everyone, Users, IisIusrs })
            {
                var haklar = NesneyeUygulananHaklar(yol, kiraci);
                Assert.True(
                    (haklar & YazmaMaskesi) == 0,
                    $"'{yol}' hâlâ '{kiraci}' kimliğine yazma/silme veriyor: {haklar & YazmaMaskesi}");
            }

            // FAZLA KAPATMA KONTROLÜ — kapatmak kadar önemli:
            // RX düşerse IIS FastCGI php-cgi.exe'yi başlatamaz ve barındırılan HER site 500 verir.
            Assert.True(
                NesneyeUygulananHaklar(yol, Users).HasFlag(FileSystemRights.ReadAndExecute),
                $"'{yol}' Users'a okuma/çalıştırma vermiyor; siteler çöker.");
            Assert.True(
                NesneyeUygulananHaklar(yol, IisIusrs).HasFlag(FileSystemRights.ReadAndExecute),
                $"'{yol}' IIS_IUSRS'a okuma/çalıştırma vermiyor; IUSR php.ini'yi okuyamaz.");

            // Panel kendi bileşenlerini kurabilmeli (SYSTEM) ve yönetici erişimi kalmalı.
            Assert.True(NesneyeUygulananHaklar(yol, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null))
                .HasFlag(FileSystemRights.Modify));
            Assert.True(NesneyeUygulananHaklar(yol, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null))
                .HasFlag(FileSystemRights.Modify));
        }

        // Kök korumalı olmalı: değilse ProgramData'daki Everyone:(CI)(F) mirasla GERİ düşer.
        Assert.True(
            new DirectoryInfo(runtimes).GetAccessControl().AreAccessRulesProtected,
            "runtimes DACL'i korumasız; kökteki Everyone mirasla geri iner.");
    }

    /// <summary>
    /// MİRASLA GELEN geniş ACE, TEK açılışta düşmeli — İKİ açılışta değil.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ÖLÇÜLEN HATA (geçici klasörde, gerçek ACL): üst klasörde <c>Everyone:(OI)(CI)F</c> varken alt
    /// klasöre <c>SetAccessRuleProtection(true, preserveInheritance: true)</c> +
    /// <c>PurgeAccessRules(Everyone)</c> TEK yazmada uygulanınca sonuç
    /// <c>D:PAI(A;OICI;FA;;;WD)...</c> oldu — <b>Everyone hayatta kaldı</b>, üstelik artık explicit
    /// olarak. <c>PurgeAccessRules</c> miras bayraklı ACE'lere dokunmuyor; miras→explicit dönüşümünü
    /// Windows ancak DACL korumalı olarak DİSKE yazılırken yapıyor. Yani sertleştirme ancak İKİNCİ
    /// çalışmasında kapatıyordu.
    /// </para>
    /// <para>
    /// Bu, "kapatıldı" denilen <c>runtimes\stalwart</c> / <c>mariadb\*\data</c> / <c>tools\win-acme</c>
    /// yollarının taze kurulumdan ve güncellemeden sonraki İLK çalışma boyunca açık kalması demekti.
    /// Canlı makinede kapalı görünmeleri, panelin defalarca yeniden başlatılmış olmasındandı.
    /// </para>
    /// <para>
    /// Test tam olarak bu kurguyu yapar: geniş ACE ÜST klasörde durur, sertleştirme BİR KEZ koşar.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Mirasla_gelen_genis_ACE_TEK_calismada_duser()
    {
        var dataRoot = Path.Combine(calisma, "tek-calisma");
        Directory.CreateDirectory(dataRoot);
        // Geniş ACE veri kökünün KENDİSİNDE: runtimes ve stalwart onu MİRASLA alır.
        HerkeseTamYetkiVer(dataRoot);
        var stalwart = Path.Combine(dataRoot, "runtimes", "stalwart");
        Directory.CreateDirectory(stalwart);

        Assert.Contains(Everyone, DiskteAcikDuranSidler(stalwart));

        await DataRootHardeningService.HardenAsync(dataRoot, NullLogger.Instance);

        // TEK çalışmadan sonra: tamamı panele ait ağaçta kiracıdan eser kalmamalı.
        var kalanlar = DiskteAcikDuranSidler(stalwart);
        Assert.DoesNotContain(Everyone, kalanlar);
        Assert.DoesNotContain(Users, kalanlar);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), kalanlar);
    }

    /// <summary>
    /// K6 YARIŞ DURUMU: <c>runtimes</c> açılışta HENÜZ YOKKEN de kurulup korunmalı.
    /// </summary>
    /// <remarks>
    /// Bileşenler (php, nodejs...) panel açılışından SONRA kurulur. Klasör o an oluşursa üst
    /// klasörden <c>Everyone:FullControl</c> mirasını alır ve düzeltme BİR SONRAKİ servis
    /// başlangıcına kadar gelmez — kurulumdan sonraki ilk saatler açık kalırdı. Korumalı klasörü
    /// önden kurmak, sonradan kurulan her bileşenin doğuştan salt-okunur mirasla gelmesini sağlar.
    /// </remarks>
    [Fact]
    public async Task Runtimes_klasoru_YOKKEN_de_korunarak_olusturulur()
    {
        var dataRoot = Path.Combine(calisma, "runtimes-yok");
        Directory.CreateDirectory(dataRoot);
        HerkeseTamYetkiVer(dataRoot);
        var runtimes = Path.Combine(dataRoot, "runtimes");
        Assert.False(Directory.Exists(runtimes));

        await DataRootHardeningService.HardenAsync(dataRoot, NullLogger.Instance);

        Assert.True(Directory.Exists(runtimes), "runtimes oluşturulmadı; bileşen kurulunca açık DACL ile doğardı.");
        Assert.Equal((FileSystemRights)0, NesneyeUygulananHaklar(runtimes, Everyone) & YazmaMaskesi);

        // Sonradan kurulan bir bileşen KORUNMUŞ mirası devralmalı (asıl ölçülen şey bu).
        var sonradanKurulan = Path.Combine(runtimes, "nodejs");
        Directory.CreateDirectory(sonradanKurulan);
        Assert.Equal((FileSystemRights)0, NesneyeUygulananHaklar(sonradanKurulan, Everyone) & YazmaMaskesi);
        Assert.True(NesneyeUygulananHaklar(sonradanKurulan, Users).HasFlag(FileSystemRights.ReadAndExecute));
    }

    /// <summary>
    /// K6: Mirası KIRILMIŞ bir bileşen klasörü kökten düzelmez — ayrıca yazılmalı.
    /// </summary>
    /// <remarks>
    /// Kökteki DACL değişikliği yalnız mirası AÇIK çocuklara yayılır. Başka bir kurulumun
    /// (ör. EDB PostgreSQL) mirası kırıp geniş bir ACE gömdüğü bir bileşen klasörü, yalnız köke
    /// yazan bir düzeltmede SESSİZCE açık kalırdı. Mevcut kurulumlarda bu, "sertleştirildi" denilen
    /// ağacın yarısının açık kalması demektir.
    /// </remarks>
    [Fact]
    public async Task Runtimes_altinda_mirasi_KIRILMIS_bilesen_de_kapatilir()
    {
        var dataRoot = Path.Combine(calisma, "runtimes-kirik-miras");
        var bilesen = Path.Combine(dataRoot, "runtimes", "postgresql");
        Directory.CreateDirectory(bilesen);

        var info = new DirectoryInfo(bilesen);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        security.AddAccessRule(new FileSystemAccessRule(
            Everyone,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.SetAccessControl(security);

        Assert.True(new DirectoryInfo(bilesen).GetAccessControl().AreAccessRulesProtected);
        Assert.NotEqual((FileSystemRights)0, NesneyeUygulananHaklar(bilesen, Everyone) & YazmaMaskesi);

        await DataRootHardeningService.HardenAsync(dataRoot, NullLogger.Instance);

        Assert.Equal((FileSystemRights)0, NesneyeUygulananHaklar(bilesen, Everyone) & YazmaMaskesi);
        Assert.Equal((FileSystemRights)0, NesneyeUygulananHaklar(bilesen, Users) & YazmaMaskesi);
        Assert.True(NesneyeUygulananHaklar(bilesen, Users).HasFlag(FileSystemRights.ReadAndExecute));
    }

    /// <summary>
    /// K6 (saf fonksiyon): Kural kümesinde kiracıya BIRAKILAN tek şey ReadAndExecute'tur.
    /// </summary>
    [Fact]
    public void Runtimes_kural_kumesinde_kiraciya_yalniz_RX_kalir()
    {
        var security = new DirectorySecurity();
        security.AddAccessRule(new FileSystemAccessRule(
            Everyone,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        DataRootHardeningService.ApplyRuntimeReadOnlyRules(security);

        Assert.True(security.AreAccessRulesProtected);

        var kurallar = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();

        foreach (var kiraci in new[] { Everyone, Users, IisIusrs })
        {
            var haklar = kurallar
                .Where(kural => kural.AccessControlType == AccessControlType.Allow && kiraci.Equals(kural.IdentityReference))
                .Aggregate((FileSystemRights)0, (toplam, kural) => toplam | kural.FileSystemRights);

            Assert.True((haklar & YazmaMaskesi) == 0, $"'{kiraci}' hâlâ yazma taşıyor: {haklar & YazmaMaskesi}");
        }

        // Everyone geri KONMAZ; RX yalnız Users + IIS_IUSRS üzerinden verilir.
        Assert.DoesNotContain(Everyone, kurallar.Select(kural => (SecurityIdentifier)kural.IdentityReference));
        Assert.Contains(Users, kurallar.Select(kural => (SecurityIdentifier)kural.IdentityReference));
        Assert.Contains(IisIusrs, kurallar.Select(kural => (SecurityIdentifier)kural.IdentityReference));
    }

    // ===================================================================================
    // K6 tamamlayıcısı — veri kökünün KENDİSİ
    // ===================================================================================

    /// <summary>
    /// K6: <c>runtimes</c> salt-okunur olsa bile, ÜST klasördeki <c>FILE_DELETE_CHILD</c> onu
    /// yeniden adlandırmaya yeterdi — o hak da düşmeli. Ama GEZİNME düşmemeli.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ÖLÇÜLDÜ (canlı): <c>C:\ProgramData\AdaPanel</c> DACL'i <c>Everyone:(I)(CI)(F)</c> taşıyor.
    /// NTFS'te yeniden adlandırma için nesnede <c>DELETE</c> ya da ÜST klasörde
    /// <c>FILE_DELETE_CHILD</c> yeter; yani kiracı <c>runtimes</c>'i kenara alıp yerine kendi
    /// ağacını koyabilirdi (IIS FastCGI ve panel cron'u php'yi MUTLAK yolla çağırır). Aynı
    /// FullControl <c>WRITE_DAC</c> da içerdiğinden veri kökünün DACL'ini yeniden yazmak da mümkündü.
    /// </para>
    /// <para>
    /// FAZLA KAPATMANIN EN KOLAY OLDUĞU YER BURASI: kiracı grubunun kuralını topluca kaldırmak
    /// <c>Users</c>'ı veri kökünde GEZİNEMEZ yapar ve <c>runtimes\php\...\php-cgi.exe</c> yolunun
    /// tamamı çöker. Bu yüzden test hem yazmanın düştüğünü hem RX'in ve ÇOCUKLARIN aynen kaldığını
    /// birlikte ölçer.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Veri_kokunun_KENDISINDE_kiraci_yazamaz_ama_GEZINEBILIR_ve_cocuklar_DEGISMEZ()
    {
        var dataRoot = Path.Combine(calisma, "veri-koku-kabi");
        Directory.CreateDirectory(dataRoot);
        ProgramDataAclindenKopyala(dataRoot);

        // Kasten hassas listede OLMAYAN bir çocuk: değişmediğini ölçebilmek için.
        var dokunulmayan = Path.Combine(dataRoot, "sitebuilder");
        Directory.CreateDirectory(dokunulmayan);

        var oncekiCocukEveryone = NesneyeUygulananHaklar(dokunulmayan, Everyone);
        var oncekiCocukUsers = NesneyeUygulananHaklar(dokunulmayan, Users);
        Assert.NotEqual((FileSystemRights)0, oncekiCocukEveryone & YazmaMaskesi);
        Assert.NotEqual((FileSystemRights)0, NesneyeUygulananHaklar(dataRoot, Everyone) & YazmaMaskesi);
        Assert.True(NesneyeUygulananHaklar(dataRoot, Users).HasFlag(FileSystemRights.ReadAndExecute));

        // İKİ KEZ koşuluyor. Sertleştirme HER AÇILIŞTA çalışıyor; bir kere doğru olması yetmez,
        // ikinci koşumda kendi çıktısını okuyup bozmaması gerekir (kuralları yalnız-mirasa çevirip
        // sonra o çevrilmiş kuralı bir kez daha çevirmek, gezinme hakkını sessizce silebilirdi).
        await DataRootHardeningService.HardenAsync(dataRoot, NullLogger.Instance);
        await DataRootHardeningService.HardenAsync(dataRoot, NullLogger.Instance);

        // (1) KÖK: yazma/silme/DACL değiştirme düştü.
        foreach (var kiraci in new[] { Everyone, Users, IisIusrs })
        {
            var haklar = NesneyeUygulananHaklar(dataRoot, kiraci);
            Assert.True(
                (haklar & YazmaMaskesi) == 0,
                $"Veri kökü hâlâ '{kiraci}' kimliğine yazma/silme veriyor: {haklar & YazmaMaskesi}");
        }

        // (2) KÖK: gezinme/okuma KALDI (yoksa altındaki her şey erişilemez olurdu).
        Assert.True(
            NesneyeUygulananHaklar(dataRoot, Users).HasFlag(FileSystemRights.ReadAndExecute),
            "Veri kökünde Users gezinemiyor; runtimes\\php yolunun tamamı çöker.");

        // (3) ÇOCUK: BİRE BİR aynı kaldı (fazla kapatma yok). Değişiklik yalnız KABIN kendisinde.
        Assert.Equal(oncekiCocukEveryone, NesneyeUygulananHaklar(dokunulmayan, Everyone));
        Assert.Equal(oncekiCocukUsers, NesneyeUygulananHaklar(dokunulmayan, Users));

        // (4) Panel ve yönetici kökte tam yetkili kalmalı.
        Assert.True(NesneyeUygulananHaklar(dataRoot, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null))
            .HasFlag(FileSystemRights.Modify));
    }
}
