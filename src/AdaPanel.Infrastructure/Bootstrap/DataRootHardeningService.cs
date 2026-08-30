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
        ["data", "keys", "certs", "config", "backups", "mail-certs", "updates", "downloads", "components", "logs"];

    /*
     * "components" ve "logs" SONRADAN EKLENDI -- ikisi de ayni denetim turunda OLCULDU
     * (icacls ile, canli): ikisinde de Everyone:(I)(CI)(F) vardi.
     *
     * components: iis-arr / iis-url-rewrite kurulum paketlerini tutar ve ComponentBootstrapper
     *   bunlari SYSTEM olarak `msiexec /i` ile KURAR. Yani `downloads` ile birebir ayni sinif --
     *   kiraci paketi degistirir, panel onu SYSTEM olarak kurar. Klasore dokunan baska bir taraf
     *   yok: kod tarandi, yalniz ComponentBootstrapper (SYSTEM) okuyor.
     *
     * logs: panelin KENDI gunlukleri (Serilog, Program.cs). Burada yukseltme YOK -- SYSTEM bu
     *   klasorden bir sey CALISTIRMIYOR. Kapatilma sebebi baska: kiraci panel gunluklerini
     *   OKUYABILIYORDU. Gunluk satirlarindan sir gecebilir (yol, kullanici adi, hata ayrintisi) ve
     *   kiracinin baska kiracilarin islemlerini okumasi tek basina bir ifsadir. Ayrica yazma hakki
     *   gunlugu bozmaya (delil karartma) yeter.
     *   FAZLA KAPATMA OLCULDU: gunlugu yazan panel servisi SYSTEM'dir, okuyan yonetici
     *   Administrators'tadir; ikisi de yeni DACL'de duruyor. Tepsi uygulamasinda bu klasoru okuyan
     *   bir yol aranmis ve BULUNMAMISTIR.
     *
     * "sitebuilder" BILEREK EKLENMEDI: ayni turda olculdu ve onda da Everyone:(I)(CI)(F) var, ama
     * icerigi kullanicinin site projeleri. Bir projenin bir siteye yayinlanip IIS tarafindan
     * sunulup sunulmadigi OLCULEMEDI; korlemesine kapatmak site kurucuyu kirabilirdi. Fazla
     * kapatma, bu depoda acik birakmak kadar pahali bir hatadir -- olcum yapilmadan kapatilmaz.
     */

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
    /// <c>runtimes</c> ağacında kiracıya BIRAKILAN gruplar. Haklarıysa tek bir şey olabilir:
    /// <see cref="FileSystemRights.ReadAndExecute"/>.
    /// <para>
    /// <b>Neden yalnız bu ikisi.</b> Bu makinede ölçüldü: <c>BUILTIN\Users</c> üyeleri arasında
    /// <c>NT AUTHORITY\Authenticated Users</c> var — yani uygulama havuzu sanal hesapları,
    /// <c>LOCAL SERVICE</c> (LimitedIdentityRunner) ve <c>NETWORK SERVICE</c> (EDB PostgreSQL servisi)
    /// RX'i <c>Users</c> üzerinden alır. <c>IUSR</c> <c>Users</c>'ta DEĞİLDİR, <c>IIS_IUSRS</c>'tedir;
    /// <c>fastcgi.impersonate=1</c> ile dosya erişimi IUSR jetonuyla yapıldığı için o grup AYRICA
    /// yazılır. <c>Everyone</c> ve <c>Guests</c> geri konmaz: ikisi de RX'i başka bir yoldan almayan
    /// hiçbir meşru tarafı temsil etmiyor.
    /// </para>
    /// </summary>
    private static IReadOnlyList<SecurityIdentifier> RuntimeReaderSids() =>
    [
        new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
        new SecurityIdentifier(IisIusrsSid)
    ];

    /// <summary>
    /// Bir hakkın "okuma/gezinme" kısmı. Yazma, ekleme, silme, DACL değiştirme ve sahiplenme
    /// bitlerinin HİÇBİRİNİ içermez.
    /// </summary>
    internal const FileSystemRights ReadOnlyMask = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;

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

        // K6 — SIRA ÖNEMLİ: önce TÜM runtimes ağacı kiracıya salt-okunur yapılır, sonra içindeki
        // tamamı panele ait yollar (stalwart, db veri dizinleri) BÜSBÜTÜN kapatılır. Ters sırada
        // kapatılan yollara RX geri düşme riski olurdu.
        try
        {
            HardenRuntimeTree(dataRoot, logger);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "runtimes salt-okunur sertleştirme atlandı.");
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

        // K6 son adım: veri kökünün KENDİSİ. runtimes'i salt-okunur yapmak, kiracı üst klasörde
        // FILE_DELETE_CHILD taşıdığı sürece yeterli DEĞİLDİR (aşağıdaki gerekçeye bakınız).
        try
        {
            HardenDataRootContainer(dataRoot, logger);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "veri kökü klasör ACL sertleştirmesi atlandı.");
        }

        return Task.CompletedTask;
    }

    // ===================================================================================
    // K6 — runtimes: KİRACI OKUR VE ÇALIŞTIRIR, YAZAMAZ
    // ===================================================================================

    /// <summary>
    /// K6: <c>runtimes</c> ağacında kiracı kimliklerine <b>ReadAndExecute EVET, yazma/silme HAYIR</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ÖLÇÜLEN AÇIK (bu makinede, icacls):</b> <c>C:\ProgramData\AdaPanel\runtimes</c> ve
    /// <c>...\runtimes\php\8.3</c> DACL'i <c>Everyone:(I)(CI)(F)</c> taşıyordu — <c>C:\ProgramData</c>
    /// kökündeki <c>Everyone:(CI)(F)</c>'den MİRASLA. <c>(CI)</c> olduğu için ACE dosyalara inmez
    /// (php.ini'nin kendi izni <c>BUILTIN\Users:(I)(RX)</c> idi) <b>ama klasör üzerindeki FullControl
    /// dosyanın kendi izninden BAĞIMSIZ olarak FILE_ADD_FILE + FILE_DELETE_CHILD verir</b>: kiracı
    /// <c>php.ini</c>'yi silip yerine kendininkini koyabilir, <c>php-cgi.exe</c>'nin yanına DLL
    /// düşürebilir. Bir önceki turda gerçek <c>php-cgi.exe</c> ile ölçüldü: kiracı PHP'si bu klasöre
    /// 26 baytlık bir dosyayı BAŞARIYLA yazdı. Ayrıca <c>BUILTIN\Users:(I)(CI)(WD,AD,WEA,WA)</c>
    /// (ProgramData'nın standart ACE'i) Everyone düşse bile dosya/klasör OLUŞTURMAYA yeter.
    /// </para>
    /// <para>
    /// <b>Neden bu tam da şimdi kritik:</b> site başına PHP izolasyonu (<c>PhpIniSiteIsolation</c>)
    /// PAYLAŞILAN <c>php.ini</c> içindeki <c>[PATH=]</c> bloklarına dayanır. O dosya yazılabilir/
    /// silinebilir kaldıkça <c>open_basedir</c> dahil bütün izolasyon altından oyulur. Panelin kendisi
    /// (SYSTEM) cron/composer için aynı ağaçtaki <c>php.exe</c>'yi çalıştırır → kiracı → SYSTEM.
    /// </para>
    /// <para>
    /// <b>Neden "hepsini kapat" DEĞİL de "RX bırak":</b> IIS FastCGI <c>php-cgi.exe</c>'yi uygulama
    /// havuzu kimliğiyle çalıştırır; zamanlanmış görevler ve <c>LimitedIdentityRunner</c> node/python/
    /// php'yi LOCAL SERVICE olarak çalıştırır. RX kaldırılırsa siteler 500 verir. Eski gerekçe
    /// ("runtimes'a HİÇ dokunma") <b>RX'i haklı çıkarır, YAZMAYI çıkarmaz</b> — ayrım burada yapılıyor.
    /// </para>
    /// <para>
    /// <b>YAZMA İHTİYACI ARANDI, MEŞRU OLANI BULUNAMADI (ölçümler):</b>
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>error_log</c> (ComponentBootstrapper.EnsurePhpIniAsync) <c>runtimes\php\8.3\php_errors.log</c>'u
    /// gösteriyor — TEK aday. Ama canlıda ölçüldü: dosyanın kendi DACL'i
    /// <c>BUILTIN\Users:(I)(RX)</c>, yani kiracı O DOSYAYA ZATEN YAZAMIYOR (klasördeki Everyone ACE'i
    /// <c>(CI)</c>, dosyalara inmiyor). Dolayısıyla klasörü kapatmak var olan bir yeteneği KALDIRMIYOR;
    /// yalnız "sil + yerine koy" yolunu kapatıyor. Panel bu dosyayı okumuyor da: depoda
    /// <c>php_errors.log</c>'un tek okuyucusu Roundcube tanılaması ve o KENDİ günlüğüne
    /// (<c>tools\roundcube\logs</c>) bakıyor. Bu yüzden ağaçta hiçbir yer yazmaya açılmadı.
    /// </description></item>
    /// <item><description>
    /// <c>npm -g</c> (SiteHostingSettingsService.RunNodeCommandAsync): npm'in Windows'taki varsayılan
    /// prefix'i node.exe'nin klasörüdür → <c>runtimes\nodejs</c>. Komut LOCAL SERVICE ile koşuyor ve
    /// bu değişiklikten SONRA global kurulum EACCES ile düşer. BU KIRILMA BİLEREK KABUL EDİLDİ:
    /// paylaşılan node kurulumuna paket yazmak, bir sonraki kiracıya (ve panele) kod geçirmenin ta
    /// kendisidir. Panelin kendisi hiçbir yerde <c>-g</c> kullanmıyor (kaynak tarandı, sıfır sonuç);
    /// site-içi <c>npm install</c> etkilenmez, çünkü <c>node_modules</c> çalışma dizinine yazılır ve
    /// npm önbelleği kimliğin kendi profilindedir.
    /// </description></item>
    /// <item><description>
    /// composer/wp-cli: <c>tools\composer</c> altında (components.json okundu), <c>runtimes</c> DIŞINDA.
    /// <c>LimitedIdentityRunner</c> yazma iznini yalnız SİTE KÖKÜNE verir (grantRoot = site kökü);
    /// çıktı/sarmalayıcı dosyaları <c>%TEMP%</c>'e yazılır. runtimes'a hiç dokunmaz.
    /// </description></item>
    /// <item><description>
    /// Motorlar ve daemon'lar: mariadb/mysql <c>--install</c> (LocalSystem), redis
    /// <c>--service-install</c> (LocalSystem), memcached + nginx + stalwart zamanlanmış görev
    /// (<c>UserId S-1-5-18</c> — ComponentBootstrapper.BuildLongRunningTaskXml'de okundu), freshclam ve
    /// docker'ı panel (SYSTEM) çalıştırır. Hepsi SYSTEM → FullControl korunuyor. Tek istisna EDB
    /// PostgreSQL'in NETWORK SERVICE servisidir; onun yazdığı yer <c>postgresql\*\data</c>'dır, o da
    /// zaten <see cref="HardenRuntimes"/> ile mirası KOPYALAYARAK kapatılır (explicit NETWORK SERVICE
    /// ACE'i korunur). ÖLÇÜLEMEDİ: bu makinede PostgreSQL kurulu değil.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>Neden kök + yalnız mirası kırılmış çocuklar:</b> DACL kökte değiştiğinde Windows
    /// devralınabilir ACE'leri mirası AÇIK olan çocuklara kendisi yayar (canlıda ölçüldü: runtimes
    /// altındaki çocukların hiçbiri korumalı değil, yalnız zip açılışından kalan
    /// <c>BUILTIN\Administrators</c> explicit ACE'leri var). Yayılmayan tek durum, mirası KIRILMIŞ bir
    /// bileşen klasörüdür (başka bir kurulumun bıraktığı) — o yüzden yalnız <c>AreAccessRulesProtected</c>
    /// olan birinci seviye klasörlere ayrıca yazılır. Ağacın tamamını gezmek mariadb veri dizinindeki
    /// on binlerce dosyayı her açılışta dolaşmak olurdu.
    /// </para>
    /// </remarks>
    internal static void ApplyRuntimeReadOnlyRules(DirectorySecurity security)
    {
        // Miras KOPYALANIR (sıfırdan kurulmaz): bir bileşen kurulumunun bıraktığı kendi servis
        // hesabı ACE'i düşerse motor başlamaz. Kiracı grupları zaten aşağıda tek tek düşürülüyor.
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

        // ...ve YALNIZ okuma/çalıştırma geri verilir. Yazma/silme geri GELMEZ.
        foreach (var reader in RuntimeReaderSids())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                reader,
                FileSystemRights.ReadAndExecute,
                inherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
    }

    /// <summary>K6 uygulaması: <see cref="ApplyRuntimeReadOnlyRules"/> kararını diske yazar.</summary>
    private static void HardenRuntimeTree(string dataRoot, ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataRoot));
        var runtimesRoot = Path.Combine(root, "runtimes");

        try
        {
            // KLASÖR YOKSA OLUŞTURULUR. Sebebi ölçülmüş bir yarıştır: bileşenler açılıştan SONRA
            // kurulur ve o an oluşan 'runtimes' üst klasörden mirasla Everyone:FullControl alırdı;
            // düzeltme bir sonraki servis başlangıcına kadar gelmezdi. Korumalı klasörü ÖNCE kurmak,
            // sonradan kurulan her bileşenin doğuştan salt-okunur mirasla gelmesini sağlar.
            Directory.CreateDirectory(runtimesRoot);

            var info = new DirectoryInfo(runtimesRoot);
            var security = ProtectAndMaterialize(info);
            ApplyRuntimeReadOnlyRules(security);
            info.SetAccessControl(security);
            logger.LogInformation("runtimes kiracıya SALT OKUNUR yapıldı (RX evet, yazma hayır): {Directory}", runtimesRoot);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "runtimes salt-okunur ACL ayarı '{Directory}' için atlandı.", runtimesRoot);
            return;
        }

        foreach (var component in SafeEnumerate(runtimesRoot))
        {
            var name = Path.GetFileName(component);
            if (IsRuntimeLockdownTarget(name))
            {
                // Bu ağaç birazdan BÜSBÜTÜN kapatılacak; RX verip geri almanın anlamı yok.
                continue;
            }

            try
            {
                var info = new DirectoryInfo(component);
                var security = info.GetAccessControl();
                if (!security.AreAccessRulesProtected)
                {
                    // Miras açık → kökteki yeni DACL zaten indi.
                    continue;
                }

                ApplyRuntimeReadOnlyRules(security);
                info.SetAccessControl(security);
                logger.LogInformation("runtimes bileşeni (mirası kırık) salt okunur yapıldı: {Directory}", component);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "runtimes bileşen ACL ayarı '{Directory}' için atlandı.", component);
            }
        }
    }

    // ===================================================================================
    // K6 — veri kökünün KENDİSİ: kiracı içeride bir şeyi yeniden adlandıramaz/silemez
    // ===================================================================================

    /// <summary>
    /// K6 tamamlayıcısı: veri kökü klasörünün KENDİSİNDE kiracıya yalnız okuma/gezinme bırakır;
    /// çocukların izinleri BİRE BİR korunur.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden gerekli — runtimes'i kapatmak tek başına YETMİYOR.</b> Bu makinede ölçüldü:
    /// <c>C:\ProgramData\AdaPanel</c> DACL'i <c>Everyone:(I)(CI)(F)</c> taşıyor. NTFS'te bir nesneyi
    /// yeniden adlandırmak için nesnenin kendisinde <c>DELETE</c> ya da ÜST KLASÖRDE
    /// <c>FILE_DELETE_CHILD</c> yeter. Yani <c>runtimes</c> salt-okunur olsa bile kiracı onu
    /// <c>runtimes.eski</c> yapıp yerine kendi <c>runtimes\php\8.3\php-cgi.exe</c>'sini
    /// koyabilirdi — IIS FastCGI ve panelin cron'u MUTLAK yolla çağırdığı için sıradaki istek
    /// kiracının ikilisini çalıştırırdı. Aynı hak <c>WRITE_DAC</c> da içerdiğinden kök DACL'i
    /// yeniden yazmak da mümkündü.
    /// </para>
    /// <para>
    /// <b>Neden "kökü kilitle" DEĞİL.</b> Bu deponun ölçülmüş tarihi: tüm kökü kilitlemek paneli
    /// KIRDI (sonradan kurulan PHP kilitli ebeveyni miras alıyor → app pool okuyamıyor → 500). Bu
    /// yüzden burada çocukların izinlerine HİÇ dokunulmuyor: kiracı gruplarının devralınabilir
    /// kuralları AYNI haklarla, yalnızca <see cref="PropagationFlags.InheritOnly"/> eklenerek geri
    /// konur (çocuklar için sonuç birebir aynı), klasörün KENDİSİNE ise aynı kuralın sadece
    /// <see cref="ReadOnlyMask"/> ile maskelenmiş hâli yazılır.
    /// </para>
    /// <para>
    /// <b>Gezinme KORUNUR — fazla kapatmanın en kolay yapılacağı yer burasıydı.</b>
    /// <c>Users:(OI)(CI)(RX)</c> kuralını topluca yalnız-miras yapmak, <c>Users</c>'ı veri kökünde
    /// GEZİNEMEZ hâle getirir ve <c>runtimes\php\php-cgi.exe</c> yolunun tamamı çöker. Bu yüzden
    /// kural ikiye ayrılıyor, silinmiyor.
    /// </para>
    /// <para>
    /// <b>Deny kuralları AYNEN geri konur:</b> bir <c>Deny</c>'yi maskelemek onu zayıflatmak olurdu.
    /// </para>
    /// </remarks>
    internal static void ApplyDataRootContainerRules(DirectorySecurity security)
    {
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

        var tenants = TenantSidTypes
            .Select(type => new SecurityIdentifier(type, null))
            .Append(new SecurityIdentifier(IisIusrsSid))
            .ToArray();

        var mevcut = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => tenants.Any(sid => sid.Equals(rule.IdentityReference)))
            .Select(rule => (
                rule.IdentityReference,
                rule.FileSystemRights,
                rule.InheritanceFlags,
                rule.PropagationFlags,
                rule.AccessControlType))
            .ToArray();

        foreach (var sid in tenants)
        {
            security.PurgeAccessRules(sid);
        }

        foreach (var (identity, rights, inheritanceFlags, propagation, type) in mevcut)
        {
            if (type == AccessControlType.Deny)
            {
                security.AddAccessRule(new FileSystemAccessRule(identity, rights, inheritanceFlags, propagation, type));
                continue;
            }

            // (1) ÇOCUKLAR DEĞİŞMEZ: aynı haklar, aynı miras bayrakları, yalnız-miras olarak.
            if (inheritanceFlags != InheritanceFlags.None)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    identity,
                    rights,
                    inheritanceFlags,
                    propagation | PropagationFlags.InheritOnly,
                    type));
            }

            // (2) KLASÖRÜN KENDİSİ: yalnız okuma/gezinme. FILE_DELETE_CHILD, FILE_ADD_FILE,
            //     FILE_ADD_SUBDIRECTORY, WRITE_DAC ve WRITE_OWNER burada düşer.
            if (propagation.HasFlag(PropagationFlags.InheritOnly))
            {
                continue;
            }

            var okuma = rights & ReadOnlyMask;
            if (okuma != 0)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    identity,
                    okuma,
                    InheritanceFlags.None,
                    PropagationFlags.None,
                    type));
            }
        }
    }

    /// <summary>K6 uygulaması: <see cref="ApplyDataRootContainerRules"/> kararını diske yazar.</summary>
    private static void HardenDataRootContainer(string dataRoot, ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataRoot));
        if (!Directory.Exists(root))
        {
            return;
        }

        try
        {
            var info = new DirectoryInfo(root);
            var security = ProtectAndMaterialize(info);
            ApplyDataRootContainerRules(security);
            info.SetAccessControl(security);
            logger.LogInformation("Veri kökü klasörünün kendisinde kiracı yazma/silme hakkı kaldırıldı: {Directory}", root);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Veri kökü klasör ACL ayarı '{Directory}' için atlandı.", root);
        }
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
    /// Mirası kapatır ve MİRASLA GELEN ACE'leri EXPLICIT hâle getirip DACL'i geri okur.
    /// Mirası koruyan her sertleştirme buradan geçmek ZORUNDA.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ÖLÇÜLMÜŞ HATA — sertleştirme İKİ AÇILIŞ sürüyordu.</b> Geçici klasörde ölçüldü:
    /// üst klasöre <c>Everyone:(OI)(CI)F</c> yazılıp alt klasörde
    /// <c>SetAccessRuleProtection(true, preserveInheritance: true)</c> + <c>PurgeAccessRules(Everyone)</c>
    /// TEK yazmada uygulandığında sonuç <c>D:PAI(A;OICI;FA;;;WD)...</c> oldu — yani
    /// <b>Everyone hayatta kaldı</b>. Sebep: <see cref="CommonObjectSecurity.PurgeAccessRules"/>
    /// bellekteki DACL'de MİRAS BAYRAKLI ACE'lere dokunmaz; miras→explicit dönüşümünü ise Windows
    /// ancak DACL diske "korumalı" olarak yazılırken yapar. Aynı işlem İKİNCİ kez koşulduğunda ACE
    /// artık explicit olduğu için düşüyordu.
    /// </para>
    /// <para>
    /// Pratik sonucu: <c>runtimes\stalwart</c>, <c>mariadb\*\data</c> ve <c>tools\win-acme</c> gibi
    /// "kapatıldı" denilen yollar, TAZE KURULUMDAN ve GÜNCELLEMEDEN sonraki ilk çalışma boyunca
    /// <c>Everyone = FullControl</c> ile AÇIK kalıyordu; ancak servis bir kez daha yeniden başlatılınca
    /// kapanıyordu. Canlı makinede kapalı görünmelerinin sebebi, panelin defalarca yeniden başlatılmış
    /// olmasıydı — düzeltmenin doğru çalışması değil.
    /// </para>
    /// <para>
    /// Çözüm iki yazmadır: önce YALNIZ koruma yazılır (Windows mirası explicit'e çevirir), sonra DACL
    /// geri OKUNUR ve kiracı temizliği o açık liste üzerinde yapılır.
    /// </para>
    /// </remarks>
    private static DirectorySecurity ProtectAndMaterialize(DirectoryInfo info)
    {
        var security = info.GetAccessControl();
        if (!security.AreAccessRulesProtected)
        {
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            info.SetAccessControl(security);
            security = info.GetAccessControl();
        }

        return security;
    }

    /// <summary>
    /// Mirası KOPYALAYARAK kapatır (mevcut hak sahipleri korunur), SYSTEM/Administrators/servis hesabını
    /// garantiler ve kiracı gruplarını (Users, Authenticated Users, Everyone, Guests, IIS_IUSRS) DÜŞÜRÜR.
    /// </summary>
    private static void RemoveTenantAccess(string directory)
    {
        var info = new DirectoryInfo(directory);
        var security = ProtectAndMaterialize(info);

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
