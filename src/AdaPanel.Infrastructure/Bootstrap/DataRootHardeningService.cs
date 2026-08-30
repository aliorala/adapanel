using System.Security.AccessControl;
using System.Security.Principal;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Bootstrap;

/// <summary>
/// K1/K2/K4 GÜVENLİK: Veri kökündeki HASSAS alt klasörlere (panel DB, DataProtection anahtarları, sertifikalar,
/// config, yedekler) ve <c>runtimes</c> altındaki KİRACIYA AÇIK OLMAMASI GEREKEN yollara (veritabanı veri
/// dizinleri, posta sunucusu ağacı) yalnız SYSTEM + Administrators + panelin kendi servis hesabı erişsin;
/// barındırılan tenant'lar (Users / Authenticated Users = her sitenin PHP'si, IUSR, FTP) adapanel.db
/// (admin hash/2FA), key-*.xml (DataProtection), <c>global_priv</c> ve posta sunucusunun süper-admin
/// kimliğini okuyamasın.
///
/// KÖK NEDEN DÜZELTMESİ — neden .NET, neden icacls DEĞİL: Eski icacls yaklaşımı (`/inheritance:r` ya da `/grant
/// SID:(OI)(CI)F`) bazı Windows Server'larda izni klasörün KENDİSİNE değil "yalnız miras (inherit-only)" olarak
/// uygulayabiliyor → SYSTEM klasöre dosya YAZAMIYOR → hem "SQLite Error 14" hem antiforgery/CSRF (DataProtection
/// anahtarı yazılamıyor) patlıyordu. .NET <see cref="FileSystemAccessRule"/> ile izin AÇIKÇA "bu klasör + alt
/// klasör + dosyalar"a (PropagationFlags.None) uygulanır → belirsizlik yok. db AÇILMADAN önce çağrılır,
/// idempotent + best-effort. Yine de sorun olursa <see cref="EnsureWritableAsync"/> kurtarma ağı devrede.
///
/// K4 DÜZELTMESİ — neden "sıfırdan kur", neden "mirası koru" DEĞİL: Eski kod
/// <c>SetAccessRuleProtection(true, preserveInheritance: true)</c> ile mirası AÇIK KOPYA olarak mühürlüyor,
/// sonra yalnız iki SID'i siliyordu. Kökte ne varsa (bu makinede ölçüldü: <c>Everyone = FullControl</c>)
/// hassas klasöre KALICI explicit ACE olarak gömülüyor ve miras kapalı olduğu için kök düzeltilse bile bir daha
/// düşmüyordu. Artık hassas klasörlerin DACL'i <see cref="BuildSensitiveDirectorySecurity"/> ile SIFIRDAN
/// kurulur: listede olmayan hiçbir hesap (Everyone dahil) hayatta kalamaz.
/// </summary>
public static class DataRootHardeningService
{
    /// <summary>
    /// Tamamı panele ait, kiracının HİÇBİR parçasına erişmemesi gereken veri kökü klasörleri.
    /// <para>
    /// AYNI LİSTE <c>scripts\harden-adapanel.ps1</c> içindeki <c>$sensitive</c> dizisinde de var
    /// (servis açılışta uygular, betik elle uygular). İkisinin ayrışamaması
    /// <c>DataRootHardeningTests</c> içinde ayrıca sınanır.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>updates — bu makinede ÖLÇÜLDÜ, listede YOKTU.</b> 0.8.4 kurulu ve çalışır durumdayken
    /// <c>C:\ProgramData\AdaPanel\updates</c> DACL'i:
    /// <c>O:BAG:SYD:(A;;FA;;;BA)(A;CIID;FA;;;WD)(A;OICIID;FA;;;SY)...</c> —
    /// yani <c>WD</c> (Everyone) = FullControl, MİRASLA gelmiş ve DACL korumasız (<c>P</c> bayrağı
    /// yok). Karşılaştırma için aynı anda ölçülen <c>config</c>: <c>D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)</c>
    /// — kapalı. Fark tek sebepten: <c>updates</c> bu dizide yoktu.
    /// </para>
    /// <para>
    /// <b>Neden bu klasör hassas olanların en hassası:</b> içinde iki dosya SYSTEM olarak
    /// ÇALIŞTIRILIYOR — <c>apply-update.ps1</c> (zamanlanmış görev,
    /// <c>-UserId 'SYSTEM' -RunLevel Highest</c>, tetik kayıttan 15 saniye sonra) ve
    /// <c>AdaPanelSetup.exe</c>. Klasör üzerindeki FullControl, dosyaların kendi izinlerinden
    /// BAĞIMSIZ olarak FILE_ADD_FILE + FILE_DELETE_CHILD verir: kiracı betiği silip yerine kendi
    /// betiğini koyabilir. Paket tarafındaki üç savunma (SHA256, <c>UpdatePackageTrust</c> yayıncı
    /// denetimi, izinli depo) bu yola KÖRDÜR; hiçbiri betiğe bakmaz.
    /// </para>
    /// <para>
    /// Sonuç: barındırılan bir sitenin PHP'si ya da herhangi bir yerel hesap → SYSTEM. Bu tam olarak
    /// <c>UpdatePackageTrust</c>'ın "panel yöneticisi bir Windows yöneticisi değildir" diyerek
    /// kapattığı sınırın, çok daha ucuz bir yoldan aşılmasıydı.
    /// </para>
    /// <para>
    /// KAPATMAK GÜVENLİ: klasöre dokunan HER ŞEY zaten SYSTEM — panel servisi
    /// (<c>SelfUpdateService</c>, <c>WindowsUpdateInstallerLauncher</c>) ve onun kurduğu zamanlanmış
    /// görev. Kod tarandı, üçüncü bir okuyucu yok (tepsi programı bu klasöre hiç bakmıyor).
    /// </para>
    /// <para>
    /// <b>downloads — updates'in KARDEŞİ, aynı denetimde ölçüldü.</b> DACL'i
    /// <c>Everyone:(I)(CI)(F)</c> + <c>BUILTIN\Users:(WD,AD)</c> taşıyordu (bu makinede ölçüldü,
    /// içinde <c>requestRouter_amd64.msi</c>, <c>rewrite_amd64_en-US.msi</c>, mariadb/php zip'leri
    /// duruyor). <c>ComponentBootstrapper</c> bu klasöre indiriyor ve dosyayı SYSTEM olarak
    /// ÇALIŞTIRIYOR: <c>msiexec /i "downloads\...msi"</c> (bkz. ComponentBootstrapper.cs:2794) ya da
    /// zip'i açıp içindeki ikiliyi. SHA256 doğrulaması SADECE sha256 YAYIMLAYAN kaynaklar için var
    /// (PHP/MariaDB/Node/Composer/GitHub); <c>CopyComponent(..., sha256: string.Empty)</c> ile inen
    /// bileşenlerde HİÇ doğrulama yok, üstelik doğrulanan dosyada bile "doğrula sonra çalıştır"
    /// arasında yazılabilir bir dosya TOCTOU'ya açık. Tek yazan/okuyan SYSTEM (ComponentBootstrapper);
    /// tepsi ve IIS bu klasöre bakmaz → kapatmak güvenli.
    /// </para>
    /// </remarks>
    internal static readonly string[] SensitiveDirectories =
        ["data", "keys", "certs", "config", "backups", "mail-certs", "updates", "downloads"];

    /// <summary>
    /// <c>tools</c> ALTINDA, tamamı panele ait olup SYSTEM olarak ÇALIŞTIRILAN alt araç dizinleri.
    /// <para>
    /// <b>Neden ayrı liste, neden <c>tools</c> topluca kapatılmıyor:</b> <c>tools</c> klasörü KARIŞIK.
    /// İçinde bir yanda yalnız SYSTEM'in çalıştırdığı <c>win-acme</c> var, öbür yanda IIS'in
    /// KİRACI havuz kimliğiyle sunduğu <c>phpmyadmin</c> / <c>roundcube</c> (Firewall.cs: 8896/8897
    /// portları, "tools" siteleri) ve kiracının SINIRLI kimliğiyle koşan <c>composer</c> / <c>wp-cli</c>
    /// (LimitedIdentityRunner) duruyor. <c>tools</c>'u topluca kilitlemek phpMyAdmin/Roundcube'ü ve
    /// composer'ı KIRARDI. O yüzden yalnız SYSTEM-only alt dizinler tek tek kapatılır.
    /// </para>
    /// <para>
    /// <b>win-acme — updates'in KARDEŞİ, bu makinede ölçüldü.</b> <c>C:\ProgramData\AdaPanel\tools\win-acme</c>
    /// DACL'i <c>Everyone:(I)(CI)(F)</c> taşıyordu; klasör üzerindeki FullControl, <c>wacs.exe</c>'nin
    /// kendi izni (Users:RX) ne olursa olsun FILE_DELETE_CHILD + FILE_ADD_FILE verir → kiracı
    /// <c>wacs.exe</c>'yi silip yerine kendininkini koyabilir VEYA .NET aracının klasöründen yüklediği
    /// bir DLL'i düşürebilir. <c>wacs.exe</c> panel (SYSTEM) tarafından çalıştırılıyor: elle yenileme
    /// (WinAcmeSslService.RenewAllAsync) VE gözetimsiz otomasyon (AutomationService.cs:585) — yani
    /// yönetici hiçbir şey yapmasa bile bir sonraki otomatik yenilemede kiracı → SYSTEM.
    /// </para>
    /// <para>
    /// KAPATMAK GÜVENLİ: win-acme'yi çalıştıran/okuyan tek taraf SYSTEM'dir (panel + onun MailService
    /// ve ComponentBootstrapper çağrıları). IIS bu dizini sunmaz. Üstteki <c>tools</c> kiracıya açık
    /// kalır; yalnız bu alt dizin düşer (fazla kapatma yok).
    /// </para>
    /// </summary>
    internal static readonly string[] LockedToolDirectories = ["win-acme"];

    /// <summary>
    /// K2: <c>runtimes</c> altında KİRACIYA kapatılacak yolların bileşen bazlı tanımı.
    ///
    /// "Bu yola kim erişmek zorunda" sorusu KODDAN cevaplanmıştır:
    ///  - <c>php</c>: IIS FastCGI <c>php-cgi.exe</c>'yi UYGULAMA HAVUZU KİMLİĞİYLE çalıştırır (SiteProvisioningService,
    ///    PhpService). Havuz sanal hesapları Authenticated Users → Users üzerinden RX alır. KAPATILAMAZ.
    ///  - <c>nodejs</c>, <c>python</c>: zamanlanmış görev/komut uçları LimitedIdentityRunner ile SINIRLI YEREL
    ///    KULLANICI olarak çalışır (ScriptTaskPlanner.LimitedUserId) → RX gerekir. KAPATILAMAZ.
    ///  - <c>nginx</c>, <c>redis</c>, <c>memcached</c>, <c>clamav</c>, <c>docker</c>, <c>vc-redist-x64</c>:
    ///    site trafiği/PATH üzerinden erişilebilir; denetimde bulgu yok, körlemesine kapatmıyoruz.
    ///  - <c>mariadb\*\data</c>, <c>mysql\*\data</c>, <c>postgresql\*\data</c>: motorun KENDİ servis hesabı okur;
    ///    kiracı TCP (<c>bind-address</c>) üzerinden bağlanır, DOSYAYA hiç dokunmaz. KAPATILIR (K2).
    ///  - <c>stalwart</c>, <c>stalwart-cli</c>, <c>vandelay</c>: posta sunucusu SYSTEM olarak zamanlanmış görevle
    ///    çalışır; CLI'yi yalnız panel (SYSTEM) çağırır; Roundcube posta sunucusuna TCP (143/587) ile bağlanır.
    ///    Dosya erişimine ihtiyacı olan tek taraf SYSTEM. KAPATILIR (K1).
    /// </summary>
    private static readonly string[] LockedRuntimeRoots = ["stalwart", "stalwart-cli", "vandelay"];

    /// <summary>Sürüm klasörünün ALTINDA veri tutan motorlar: <c>mariadb\11\data</c> gibi.</summary>
    private static readonly string[] VersionedDataRuntimeRoots = ["mariadb", "mysql", "postgresql"];

    /// <summary>Kiracıyı temsil eden ve hassas yollardan DÜŞÜRÜLECEK gruplar.</summary>
    private static readonly WellKnownSidType[] TenantSidTypes =
    [
        WellKnownSidType.BuiltinUsersSid,
        WellKnownSidType.AuthenticatedUserSid,
        WellKnownSidType.WorldSid,          // Everyone — bu makinede kökte ölçüldü (K4)
        WellKnownSidType.BuiltinGuestsSid
    ];

    /// <summary>IIS_IUSRS grubunun sabit SID'i (WellKnownSidType karşılığı yok); tüm havuz kimliklerini kapsar.</summary>
    private const string IisIusrsSid = "S-1-5-32-568";

    /// <summary>
    /// KURTARMA AĞI ADIM SIRASI — K5'in tek doğruluk kaynağı.
    /// <c>icacls /reset</c> mirası geri açar ve K1'i İPTAL EDER; bu yüzden sıfırlamadan sonra AYNI çağrıda
    /// yeniden sertleştirme ZORUNLUDUR (eskiden bir sonraki servis başlangıcına kadar açık kalıyordu).
    /// <see cref="EnsureWritableAsync"/> akışını bu listeden sürer: listeden "harden" düşerse davranış da düşer.
    /// </summary>
    internal static IReadOnlyList<string> RecoveryStepOrder() => ["reset:data", "reset:keys", "harden"];

    public static Task HardenAsync(string dataRoot, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }

        try
        {
            var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataRoot));

            foreach (var sub in SensitiveDirectories)
            {
                var dir = Path.Combine(root, sub);
                Directory.CreateDirectory(dir);

                try
                {
                    // K4: DACL SIFIRDAN kurulur (miras kopyalanmaz, eski explicit ACE'ler taşınmaz).
                    var info = new DirectoryInfo(dir);
                    info.SetAccessControl(BuildSensitiveDirectorySecurity());
                }
                catch (Exception dirException)
                {
                    logger.LogWarning(dirException, "Veri kökü ACL ayarı '{Directory}' için atlandı.", dir);
                }

                // K3 -- CANLIDA ÖLÇÜLDÜ, klasörü kilitlemek YETMİYOR.
                //
                // 0.7.3 kurulu bir makinede yetkisiz kullanıcı olarak ölçüldü: `certs` klasörünün
                // ACL'i okunamıyordu (yani klasör sertleşmişti) ama içindeki `panel.password`
                // (32 bayt) ve `panel.pfx` (2725 bayt) HÂLÂ OKUNABİLİYORDU. Sebep: klasörün DACL'ini
                // sıfırdan kurmak, o klasördeki DOSYALARIN kendi üzerlerindeki açık ACE'leri
                // KALDIRMAZ. Sertifika betiği dosyaları kurulum sırasında yazıyor; sonra servis
                // açılışında klasör sertleşiyor ama dosyaların izni olduğu gibi kalıyor.
                //
                // Pratik sonucu: barındırılan herhangi bir sitenin kodu panelin HTTPS özel anahtar
                // parolasını okuyabiliyordu.
                //
                // YALNIZ ÜST SEVİYE, özyineleme YOK: `backups` altında yüz binlerce dosya olabilir
                // ve her servis açılışında hepsini gezmek açılışı kilitlerdi. Hassas dosyalar
                // (parola, pfx, anahtar, veritabanı) klasörün doğrudan altında duruyor.
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                    {
                        ProtectFile(file, logger);
                    }
                }
                catch (Exception fileException)
                {
                    logger.LogWarning(fileException, "Veri kökü dosya ACL kilidi '{Directory}' için atlandı.", dir);
                }
            }

            logger.LogInformation("Veri kökü hassas klasörleri sertleştirildi (DACL sıfırdan: SYSTEM/Administrators/servis hesabı).");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Veri kökü ACL sertleştirme atlandı.");
        }

        try
        {
            HardenRuntimes(dataRoot, logger);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "runtimes ACL sertleştirme atlandı.");
        }

        try
        {
            HardenTools(dataRoot, logger);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "tools ACL sertleştirme atlandı.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// K1: <c>tools</c> altındaki SYSTEM-only araç dizinlerini (bkz. <see cref="LockedToolDirectories"/>)
    /// kiracıya kapatır. <c>runtimes</c> ile AYNI disiplin: DACL sıfırdan KURULMAZ, mevcut ACE'ler
    /// korunup yalnız kiracı grupları düşürülür (üstteki <c>tools</c> phpMyAdmin/Roundcube için açık
    /// kalmalı; sıfırdan kurmak win-acme'nin ihtiyaç duyabileceği bir hesabı da düşürme riski taşırdı).
    /// </summary>
    private static void HardenTools(string dataRoot, ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataRoot));
        var toolsRoot = Path.Combine(root, "tools");
        if (!Directory.Exists(toolsRoot))
        {
            return;
        }

        foreach (var name in LockedToolDirectories)
        {
            var dir = Path.Combine(toolsRoot, name);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            try
            {
                RemoveTenantAccess(dir);
                logger.LogInformation("tools kiracıya kapatıldı: {Directory}", dir);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "tools ACL ayarı '{Directory}' için atlandı.", dir);
            }
        }
    }

    /// <summary>
    /// K4: Hassas klasörün DACL'ini SIFIRDAN kurar. Miras KAPALI ve KOPYALANMAZ; sonuçta yalnız
    /// SYSTEM + Administrators + panelin çalıştığı hesap kalır. Saf fonksiyondur (diske dokunmaz),
    /// böylece yükseltilmiş yetki olmadan da doğrulanabilir.
    /// </summary>
    internal static DirectorySecurity BuildSensitiveDirectorySecurity()
    {
        var security = new DirectorySecurity();

        // preserveInheritance: FALSE — miras ACE'leri KOPYALANMAZ. Kökteki Everyone=FullControl gibi
        // her şey burada ölür; K4'ün "mühürleme" davranışı budur ve kaldırılmıştır.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var owner in ResolveOwnerSids())
        {
            // PropagationFlags.None => kural NESNENİN KENDİSİNE de uygulanır (icacls'in inherit-only
            // belirsizliği YOK) → SYSTEM bu klasöre db/anahtar YAZABİLİR.
            security.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                inherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>
    /// Hassas yollara erişmesi ZORUNLU olan hesaplar: SYSTEM, Administrators ve panelin ÇALIŞTIĞI hesap.
    /// Servis hesabı TAHMİN EDİLMEZ — süreç kimliğinden okunur; panel özel bir hesapla çalışıyorsa
    /// (harden-adapanel.ps1'in de ele aldığı durum) sıfırdan kurulan ACL onu kilitlemesin.
    /// </summary>
    private static IReadOnlyList<SecurityIdentifier> ResolveOwnerSids()
    {
        var owners = new List<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null)
        };

        try
        {
            var current = WindowsIdentity.GetCurrent().User;
            if (current is not null && !owners.Any(sid => sid.Equals(current)))
            {
                owners.Add(current);
            }
        }
        catch (Exception)
        {
            // Kimlik okunamazsa SYSTEM+Administrators yeter; panel zaten bunlardan biriyle çalışır.
        }

        return owners;
    }

    /// <summary>
    /// K2 — SAF KARAR: <c>runtimes</c> altında gördüğü göreli yollardan KAPATILACAK olanların EN AZ kümesini döner.
    /// Bir üst klasör kapatılıyorsa altındakiler ayrıca dönülmez (tek ACL yazımı tüm ağacı kapsar).
    /// Diske hiç dokunmaz → yükseltilmiş yetki olmadan test edilebilir.
    /// </summary>
    internal static IReadOnlyList<string> ResolveRuntimeLockdownTargets(IEnumerable<string> relativeDirectories)
    {
        var normalized = relativeDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace('/', '\\').Trim('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Count(character => character == '\\'))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var targets = new List<string>();
        foreach (var path in normalized)
        {
            if (!IsRuntimeLockdownTarget(path))
            {
                continue;
            }

            // Üstü zaten kapatılıyorsa tekrar yazma (stalwart kapalıysa stalwart\etc gereksiz).
            if (targets.Any(existing => path.StartsWith(existing + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            targets.Add(path);
        }

        return targets;
    }

    /// <summary>
    /// <c>runtimes</c> altındaki TEK bir göreli yol kiracıya kapatılmalı mı?
    /// Beyaz liste DEĞİL kara liste kullanılır: kapatma kararı yalnız erişim ihtiyacı KODDAN doğrulanmış
    /// yollar için verilir; tanımadığımız bir bileşeni körlemesine kapatıp IIS/FTP'yi kırmayız.
    /// </summary>
    internal static bool IsRuntimeLockdownTarget(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var segments = relativePath
            .Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        // Tamamı panele ait ağaçlar: stalwart / stalwart-cli / vandelay (yalnız SYSTEM erişir).
        if (LockedRuntimeRoots.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // Veritabanı motorları: YALNIZ veri dizini kapatılır. bin/ ve paylaşılan dosyalar dokunulmaz
        // kalır ki motorun kendi araçları ve yönetici komutları çalışmaya devam etsin.
        if (VersionedDataRuntimeRoots.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
        {
            // mariadb\11\data (sürümlü) veya postgresql\data (sürümsüz) — ikisi de veri dizinidir.
            return segments.Length is 2 or 3 &&
                   segments[^1].Equals("data", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// K1/K2 UYGULAMA: <see cref="ResolveRuntimeLockdownTargets"/> kararını diske yazar.
    ///
    /// Hassas veri kökünden FARKI — burada DACL sıfırdan KURULMAZ, mevcut ACE'ler korunup yalnız kiracı
    /// grupları düşürülür. NEDEN: veritabanı motorlarının servis hesabı (PostgreSQL/EDB kurulumunda
    /// <c>NETWORK SERVICE</c>) veri dizinine KURULUM SIRASINDA explicit ACE ile yetkilendirilir; sıfırdan
    /// kurmak o hesabı düşürüp motoru başlatılamaz hale getirirdi. Kapatmak, açık bırakmaktan daha görünür
    /// ama daha acil bir arıza olurdu.
    /// </summary>
    private static void HardenRuntimes(string dataRoot, ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataRoot));
        var runtimesRoot = Path.Combine(root, "runtimes");
        if (!Directory.Exists(runtimesRoot))
        {
            return;
        }

        foreach (var relative in ResolveRuntimeLockdownTargets(EnumerateRuntimeCandidates(runtimesRoot)))
        {
            var dir = Path.Combine(runtimesRoot, relative);
            try
            {
                RemoveTenantAccess(dir);
                logger.LogInformation("runtimes kiracıya kapatıldı: {Directory}", dir);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "runtimes ACL ayarı '{Directory}' için atlandı.", dir);
            }
        }
    }

    /// <summary>
    /// <c>runtimes</c> altındaki ilk üç seviyeyi listeler (bileşen / sürüm / veri dizini).
    /// Daha derine inmeyiz: karar verdiğimiz yollar en fazla üç segment.
    /// </summary>
    private static IEnumerable<string> EnumerateRuntimeCandidates(string runtimesRoot)
    {
        foreach (var component in SafeEnumerate(runtimesRoot))
        {
            var componentName = Path.GetFileName(component);
            yield return componentName;

            foreach (var second in SafeEnumerate(component))
            {
                var secondName = Path.Combine(componentName, Path.GetFileName(second));
                yield return secondName;

                foreach (var third in SafeEnumerate(second))
                {
                    yield return Path.Combine(secondName, Path.GetFileName(third));
                }
            }
        }
    }

    private static IEnumerable<string> SafeEnumerate(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Mirası KOPYALAYARAK kapatır (mevcut hak sahipleri korunur), SYSTEM/Administrators/servis hesabını
    /// garantiler ve kiracı gruplarını (Users, Authenticated Users, Everyone, Guests, IIS_IUSRS) DÜŞÜRÜR.
    /// </summary>
    private static void RemoveTenantAccess(string directory)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);

        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var owner in ResolveOwnerSids())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                inherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        foreach (var tenant in TenantSidTypes)
        {
            security.PurgeAccessRules(new SecurityIdentifier(tenant, null));
        }

        security.PurgeAccessRules(new SecurityIdentifier(IisIusrsSid));

        info.SetAccessControl(security);
    }

    /// <summary>
    /// K1: TEK bir dosyayı (ör. posta sunucusunun korunan kimlik dosyası, panel sertifika parolası)
    /// kiracıya kapatır: miras kapalı, DACL sıfırdan, yalnız SYSTEM + Administrators + servis hesabı.
    /// Best-effort — yetki yetmezse çağıran akış bozulmaz.
    /// </summary>
    public static void ProtectFile(string filePath, ILogger logger)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(filePath))
        {
            return;
        }

        try
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var owner in ResolveOwnerSids())
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    owner,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
            }

            new FileInfo(filePath).SetAccessControl(security);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Dosya ACL kilidi uygulanamadı: {File}", filePath);
        }
    }

    /// <summary>
    /// KURTARMA AĞI: db/anahtar hâlâ açılamıyorsa (ACL herhangi bir sebeple bozuksa) 'data' VE 'keys'
    /// klasörlerinin ACL'ini SIFIRLA (mirası geri ver → SYSTEM yazabilir) ki panel kilitlenmesin.
    ///
    /// K5: Sıfırlama K1'i İPTAL EDER (miras geri açılır, tüm kiracılar adapanel.db + key-*.xml okur).
    /// Bu yüzden AYNI çağrının sonunda <see cref="HardenAsync"/> tekrar çalıştırılır — açıklık bir sonraki
    /// servis başlangıcına kadar sürmez. Adım sırası <see cref="RecoveryStepOrder"/> ile tanımlıdır.
    /// </summary>
    public static async Task EnsureWritableAsync(string dataRoot, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataRoot));
        foreach (var step in RecoveryStepOrder())
        {
            if (step.Equals("harden", StringComparison.OrdinalIgnoreCase))
            {
                // K5: sıfırlanan izinleri AYNI istekte yeniden kapat.
                await HardenAsync(root, logger, cancellationToken);
                logger.LogWarning("Veri klasörü ACL'leri sıfırlandı ve AYNI adımda yeniden sertleştirildi (K5).");
                continue;
            }

            var sub = step["reset:".Length..];
            try
            {
                var dir = Path.Combine(root, sub);
                Directory.CreateDirectory(dir);
                await ProcessRunner.RunAsync(
                    "icacls.exe",
                    $"\"{dir}\" /reset /T /C",
                    cancellationToken: cancellationToken,
                    timeout: TimeSpan.FromSeconds(60));
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Veri klasörü ACL sıfırlama atlandı: {Sub}", sub);
            }
        }
    }
}
