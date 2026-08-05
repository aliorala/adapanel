using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.Php;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.WebDb;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.WebDbClient;

public sealed class WebDbClientService(
    AdaPanelDbContext dbContext,
    IComponentBootstrapper componentBootstrapper,
    IIisSiteService iisSiteService,
    IPhpService phpService,
    ISecretProtector secretProtector,
    Database.DatabaseCredentialStore credentialStore,
    IHttpClientFactory httpClientFactory,
    JsonFileStore fileStore,
    ILogger<WebDbClientService> logger) : IWebDbClientService
{
    private const string StatePath = "config/webdb.json";
    private const string GeneratedPasswordKey = "generatedPassword";

    public async Task<WebDbWorkspaceDto> GetWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        var states = await fileStore.ReadAsync(StatePath, Array.Empty<WebDbState>(), cancellationToken);
        var installedMap = await dbContext.InstalledComponents
            .Where(item => item.Status == ComponentInstallStatus.Installed)
            .ToDictionaryAsync(item => item.ComponentId, cancellationToken);

        var deployments = new[]
        {
            BuildDeployment(
                "phpmyadmin",
                "phpMyAdmin",
                "phpmyadmin",
                8896,
                "MySQL / MariaDB",
                "iis-web",
                states,
                installedMap),
            BuildDeployment(
                "adminer",
                "Adminer",
                "adminer",
                8897,
                "MySQL / MariaDB / PostgreSQL",
                "iis-web",
                states,
                installedMap),
            BuildDeployment(
                "pgadmin",
                "pgAdmin 4",
                "pgadmin4",
                0,
                "PostgreSQL",
                "desktop",
                states,
                installedMap)
        };

        foreach (var deployment in deployments)
        {
            await UpdateHealthAsync(deployment, cancellationToken);
        }

        return new WebDbWorkspaceDto { Deployments = deployments };
    }

    public async Task<WebDbSignonDto> CreatePhpMyAdminSignonAsync(
        string engine,
        string? databaseName,
        CancellationToken cancellationToken = default)
    {
        var states = await fileStore.ReadAsync(StatePath, Array.Empty<WebDbState>(), cancellationToken);
        var state = states.FirstOrDefault(item => item.Provider == "phpmyadmin");
        if (state is null || string.IsNullOrWhiteSpace(state.EntryFilePath))
        {
            throw new AppException("webdb.health.not_deployed", 409, 409);
        }

        var entryRoot = Path.GetDirectoryName(state.EntryFilePath)!;
        // "Aç" anında config'i tazele: yeni build'in TempDir/Error_Handler/ayarları, phpMyAdmin'i
        // yeniden dağıtmaya gerek kalmadan uygulanır (config yalnız deploy'da yazılıyordu → eski
        // config diskte kalıp aynı hatayı tekrarlıyordu).
        await EnsurePhpMyAdminConfigAsync(entryRoot, cancellationToken);
        // Var olan dağıtımda da paket/metadata dosyalarını HTTP'den engelle (yeniden dağıtım beklemeden).
        HardenWebDbWebConfig(entryRoot);
        var engineKey = (engine ?? string.Empty).Trim().ToLowerInvariant();
        var (port, rootComponentId) = engineKey == "mysql"
            ? (3307, "mysql-8")
            : (3306, "mariadb-11");

        var user = "root";
        var password = await ResolveRootPasswordAsync(rootComponentId, cancellationToken);
        string? onlyDb = null;

        if (!string.IsNullOrWhiteSpace(databaseName))
        {
            onlyDb = databaseName.Trim();
            var cred = await credentialStore.FindForDatabaseAsync(engineKey, onlyDb, cancellationToken);
            if (cred is not null && !string.IsNullOrWhiteSpace(cred.Value.Password))
            {
                // Gerçek izolasyon: domainin kendi DB kullanıcısı olarak gir (yalnız o DB'yi görür).
                user = cred.Value.UserName;
                password = cred.Value.Password;
            }
            // Kullanıcı bulunamazsa root + only_db (en azından yalnız o DB listelenir).
        }

        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var tokensDir = Path.Combine(entryRoot, "adapanel-tokens");
        Directory.CreateDirectory(tokensDir);
        // phpMyAdmin (IIS app pool) token dosyasını okuyup siler → yazma izni gerekir.
        // Dizin, parola YAZILMADAN ÖNCE yalnızca bu araca kilitlenir; kilitlenemezse işlem DURUR.
        if (!SecureTokenDirectory(tokensDir, await ResolveToolAppPoolNameAsync(state.SiteName, cancellationToken)))
        {
            throw new AppException("webdb.token_dir_unprotected", 500, 500);
        }

        CleanupOldTokens(tokensDir);

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            user,
            password,
            host = "127.0.0.1",
            port = port.ToString(),
            only_db = onlyDb ?? string.Empty
        });
        await File.WriteAllTextAsync(
            Path.Combine(tokensDir, token + ".json"),
            payload,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        // B3 (panel HTTPS reverse-proxy): tarayıcı dış *:8896 düz-HTTP portuna DEĞİL, panelin kimlik-doğrulamalı
        // HTTPS'ine gider; panel isteği yerel phpMyAdmin'e (127.0.0.1) iletir. URL panel-GÖRELİ olduğundan
        // frontend'in openReachable'ı aynı-köken açar (panel cookie'si taşınır, proxy [Authorize] geçer).
        // 8896 ayrıca açık kalır (fallback); ileride loopback'e alınacak.
        return new WebDbSignonDto { Url = $"/api/v1/dbtools/phpmyadmin/adapanel-signon.php?token={token}" };
    }


    /// <summary>
    /// Adminer'i TEK TIKLA acacak baglantiyi uretir.
    /// <para>
    /// Adminer'in phpMyAdmin gibi bir "signon" kipi YOKTUR. Bunun yerine belgelenmis eklenti
    /// mekanizmasi kullanilir: adminer_object() ile dondurulen sinifta credentials() ve login()
    /// gecersiz kilinirsa Adminer o kimlikle oturum acar. Sarmalayici PHP asagida yaziliyor.
    /// </para>
    /// <para>
    /// Kimlik secimi phpMyAdmin ile AYNI: veritabaninin kendi kullanicisinin parolasi panelde
    /// kayitliysa onunla girilir (gercek izolasyon), degilse root'a dusulur. Root'a dusuldugunde
    /// bile sarmalayicidaki databases() filtresi listeyi TEK veritabaniyla sinirlar -- yoksa
    /// yonetici yetkisiyle acilan bir oturum diger musterilerin veritabanlarini goz onune sererdi.
    /// </para>
    /// </summary>
    public async Task<WebDbSignonDto> CreateAdminerSignonAsync(
        string engine,
        string? databaseName,
        CancellationToken cancellationToken = default)
    {
        var states = await fileStore.ReadAsync(StatePath, Array.Empty<WebDbState>(), cancellationToken);
        var state = states.FirstOrDefault(item => item.Provider == "adminer");
        if (state is null || string.IsNullOrWhiteSpace(state.EntryFilePath))
        {
            throw new AppException("webdb.health.not_deployed", 409, 409);
        }

        var entryRoot = Path.GetDirectoryName(state.EntryFilePath)!;
        await EnsureAdminerSignonAsync(entryRoot, cancellationToken);
        HardenWebDbWebConfig(entryRoot);

        var engineKey = (engine ?? string.Empty).Trim().ToLowerInvariant();
        var (port, rootComponentId) = engineKey == "mysql"
            ? (3307, "mysql-8")
            : (3306, "mariadb-11");

        var user = "root";
        var password = await ResolveRootPasswordAsync(rootComponentId, cancellationToken);
        string? onlyDb = null;

        if (!string.IsNullOrWhiteSpace(databaseName))
        {
            onlyDb = databaseName.Trim();
            var cred = await credentialStore.FindForDatabaseAsync(engineKey, onlyDb, cancellationToken);
            if (cred is not null && !string.IsNullOrWhiteSpace(cred.Value.Password))
            {
                user = cred.Value.UserName;
                password = cred.Value.Password;
            }
        }

        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var tokensDir = Path.Combine(entryRoot, "adapanel-tokens");
        Directory.CreateDirectory(tokensDir);
        // Sarmalayici belirteci OKUYUP SILER: uygulama havuzuna yazma izni gerekir.
        // Dizin, parola YAZILMADAN ÖNCE yalnızca bu araca kilitlenir; kilitlenemezse işlem DURUR.
        if (!SecureTokenDirectory(tokensDir, await ResolveToolAppPoolNameAsync(state.SiteName, cancellationToken)))
        {
            throw new AppException("webdb.token_dir_unprotected", 500, 500);
        }

        CleanupOldTokens(tokensDir);

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            user,
            password,
            host = "127.0.0.1",
            port = port.ToString(),
            only_db = onlyDb ?? string.Empty
        });
        await File.WriteAllTextAsync(
            Path.Combine(tokensDir, token + ".json"),
            payload,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        // Adres panel-GORELI: tarayici ham IIS portuna degil panelin kimlik-dogrulamali vekiline
        // gider. Ham port loopback'e bagli oldugu icin disaridan zaten 400 donerdi.
        return new WebDbSignonDto { Url = $"/api/v1/dbtools/adminer/adapanel-signon.php?token={token}" };
    }

    /// <summary>
    /// Adminer sarmalayicisini diske yazar. Her acilista tazelenir ki panel guncellemesiyle gelen
    /// duzeltmeler yeniden dagitim beklemeden uygulansin.
    /// </summary>
    private static async Task EnsureAdminerSignonAsync(string entryRoot, CancellationToken cancellationToken)
    {
        const string signon = """
<?php
/*
 * AdaPanel — Adminer tek tıkla giriş.
 *
 * phpMyAdmin'in "signon" kipinin Adminer karşılığı. Adminer'in signon diye bir kipi YOKTUR;
 * bunun yerine belgelenmiş eklenti mekanizması kullanılır: adminer_object() ile döndürülen
 * sınıfta credentials() ve login() geçersiz kılınırsa Adminer o kimlikle oturum açar.
 *
 * Belirteç TEK KULLANIMLIKTIR ve okunur okunmaz silinir. Kalıcı olsaydı, tarayıcı geçmişinde
 * ya da sunucu günlüğünde kalan bir adres veritabanına sınırsız erişim verirdi.
 */

/*
 * BU DOSYA ADMINER'IN GİRİŞ NOKTASIDIR: yalnızca ilk tıklamada değil, kullanıcı Adminer içinde
 * gezerken AÇILAN HER SAYFADA yeniden çalışır.
 *
 * İlk sürüm bunu gözden kaçırıyordu: belirteç tek kullanımlık olduğu için ilk istekte siliniyor,
 * Adminer giriş sonrası kendi giriş noktasına (yani buraya) yönlendiriyor ve ikinci istekte
 * belirteç bulunamadığı için kullanıcı "Baglanti suresi dolmus" görüyordu. Yani tek tık girişi,
 * sınıf hatası olmasa bile ilk yönlendirmede ölürdü. (Gerçek Adminer 5.4.2 ile ölçüldü.)
 *
 * Doğru davranış: belirteç YALNIZCA otomatik girişi başlatır. Sonraki isteklerde Adminer'ın
 * KENDİ oturumu iş görür — panel paralel bir oturum mekanizması icat etmez, aracın kanıtlanmış
 * kendi mekanizmasını kullanır.
 */

// Belirteç yalnızca onaltılık olabilir. Bu, dosya adının yola çevrilmesinden ÖNCE yapılır:
// "../" gibi bir girdi panelin veri klasörünün dışına çıkabilirdi.
$adaToken = isset($_GET['token']) ? preg_replace('/[^a-f0-9]/', '', (string) $_GET['token']) : '';

if ($adaToken !== '' && strlen($adaToken) >= 16) {
    $adaFile = __DIR__ . DIRECTORY_SEPARATOR . 'adapanel-tokens' . DIRECTORY_SEPARATOR . $adaToken . '.json';
    if (is_file($adaFile)) {
        $adaRaw = file_get_contents($adaFile);
        @unlink($adaFile); // TEK KULLANIMLIK: okundu, artık yok.

        $adaData = json_decode($adaRaw, true);
        if (is_array($adaData) && isset($adaData['user'])) {
            $GLOBALS['ADA_LOGIN'] = $adaData;

            /*
             * Adminer bir oturum denemesi olduğunu anlamak için POST/GET'te kimlik alanı arar.
             * Alanlar doldurulmazsa credentials() hiç çağrılmaz ve kullanıcı giriş formunu görür.
             *
             * Bu alanlar YALNIZCA belirteçli ilk istekte doldurulur. Her istekte doldurmak,
             * Adminer'in her sayfada yeniden giriş yapıp yönlendirmesine — yani bir yönlendirme
             * döngüsüne — yol açardı.
             */
            $_POST['auth'] = array(
                'driver'   => 'server',
                'server'   => $adaData['host'] . ':' . $adaData['port'],
                'username' => $adaData['user'],
                'password' => isset($adaData['password']) ? $adaData['password'] : '',
                'db'       => isset($adaData['only_db']) ? $adaData['only_db'] : '',
            );
        }
    }
    // Belirteç geçersiz/kullanılmışsa HATA VERİLMEZ: bu, Adminer içindeki gezinmenin normal
    // hâlidir (tarayıcı eski adresi tekrar isteyebilir). Adminer kendi oturumuna bakar; oturum
    // yoksa kendi giriş formunu gösterir. Panelin vekili zaten kimlik doğruluyor.
}

/*
 * Adminer'in eklenti giriş noktası. Bu fonksiyon KÜRESEL ad alanında kalmalı: Adminer,
 * "function_exists('adminer_object')" ile bakar ve dizge biçiminde verilen ad, bulunduğu ad
 * alanına GÖRE çözülmez — her zaman küresel ada bakar. Fonksiyonu "Adminer" ad alanına taşımak,
 * Adminer'in eklentiyi hiç görmemesine yol açardı.
 */
function adminer_object()
{
    /*
     * ADMINER SÜRÜMÜ ÖLÇÜLÜR, TAHMİN EDİLMEZ.
     *
     * Adminer 5'te her şey "Adminer" ad alanına taşındı: temel sınıfın adı artık
     * \Adminer\Adminer. Panelin dağıttığı sürüm 5.x olduğu hâlde sarmalayıcı 4.x'in küresel
     * \Adminer sınıfından türüyordu; sayfa "Class \"Adminer\" not found" ile ölüyordu ve
     * sunucuda display_errors kapalı olduğundan kullanıcı bu satırı hiç görmeden yalnızca boş
     * bir sayfa görüyordu.
     *
     * Bu kontrol, sınıflar TANIMLANDIKTAN sonra (yani index.php yüklenirken, Adminer bu
     * fonksiyonu çağırdığı anda) çalışır. Dosyanın başında yapılsaydı hiçbir sınıf henüz
     * yüklenmemiş olurdu.
     */
    $adaBase = null;
    if (class_exists('Adminer\\Adminer')) {
        $adaBase = 'Adminer\\Adminer';   // Adminer 5.x
    } elseif (class_exists('Adminer')) {
        $adaBase = 'Adminer';            // Adminer 4.x
    }

    if ($adaBase === null) {
        // Sessizce boş sayfa DÖNMEYİZ: teşhis edilemeyen bir hata, çözülemeyen bir hatadır.
        http_response_code(500);
        exit('Adminer surumu taninmadi: temel sinif bulunamadi. Panelden Adminer bilesenini yeniden kurun.');
    }

    class_alias($adaBase, 'AdaPanelAdminerBase');

    class AdaPanelAdminer extends \AdaPanelAdminerBase
    {
        public function name()
        {
            return 'AdaPanel';
        }

        /*
         * Aşağıdaki geçersiz kılmaların HEPSİ, panel kimliği YOKKEN Adminer'in kendi davranışına
         * düşer. Belirteç tek kullanımlıktır: kullanıcı Adminer içinde gezerken ADA_LOGIN artık
         * yoktur ve bu geçersiz kılmalar o istekte de çalışır. Diziye körlemesine erişmek her
         * sayfada ölümcül hata verirdi.
         */

        /**
         * Hangi veritabanıyla sınırlıyız: ilk istekte belirteçten, sonraki isteklerde Adminer'ın
         * adres satırında ZATEN taşıdığı seçili veritabanından.
         * <p>
         * Belirteç tek kullanımlıktır; yalnızca ona bakılırsa süzgeç ilk sayfadan sonra hiçbir
         * yerde uygulanmaz. Gerçek Adminer 5.4.2 ile ölçüldüğünde, ilk sayfadan sonra üstteki "DB"
         * seçici sunucudaki bütün veritabanlarını listeliyordu — yani panel tutamadığı bir söz
         * veriyordu.
         * </p>
         * <p>
         * Adminer'in oturumuna yazmak DENENDI VE OLCULDU: adaOnlyDb çağrıldığı anda
         * session_status() PHP_SESSION_NONE döner (Adminer oturum kilidini erkenden bırakıyor),
         * yani oraya hiçbir şey yazılamıyor. Bu yüzden kaynak olarak Adminer'in kendi "db"
         * parametresi kullanılır.
         * </p>
         * <p>
         * Bu bir YETKİ SINIRI DEĞİL, bir gösterim sınırıdır ve öyle olması yeterlidir: Adminer'i
         * açan uç yalnızca yöneticiye açıktır (WebDbController ve DbToolsProxyController
         * "Administrator" rolü ister) ve yönetici zaten bütün veritabanlarını görmeye yetkilidir.
         * Gerçek kiracı izolasyonu, bağlantının veritabanının KENDİ kullanıcısıyla kurulmasıyla
         * sağlanır; root'a yalnızca o kullanıcı bilinmediğinde düşülür.
         * </p>
         */
        private function adaOnlyDb()
        {
            $ada = isset($GLOBALS['ADA_LOGIN']) ? $GLOBALS['ADA_LOGIN'] : null;
            if (is_array($ada) && isset($ada['only_db']) && $ada['only_db'] !== '') {
                return $ada['only_db'];
            }

            if (isset($_GET['db']) && is_string($_GET['db']) && $_GET['db'] !== '') {
                return $_GET['db'];
            }

            return '';
        }
        public function credentials()
        {
            $ada = isset($GLOBALS['ADA_LOGIN']) ? $GLOBALS['ADA_LOGIN'] : null;
            if (!is_array($ada)) {
                return parent::credentials();
            }

            return array(
                $ada['host'] . ':' . $ada['port'],
                $ada['user'],
                isset($ada['password']) ? $ada['password'] : '',
            );
        }

        /**
         * Kimlik doğrulaması PANEL TARAFINDA zaten yapıldı: kullanıcı panele giriş yapmış,
         * yönetici yetkisi doğrulanmış ve belirteç tek kullanımlık üretilmişti. Adminer'in
         * kendi form doğrulamasını burada tekrarlamak, aynı kararı ikinci kez ve daha zayıf
         * bilgiyle vermek olurdu.
         *
         * Panel kimliği yoksa karar Adminer'a bırakılır — kendi giriş formundan gelen bir
         * denemeyi panel adına onaylamak, vermediğimiz bir güvence vermek olurdu.
         */
        public function login($login, $password)
        {
            if (!isset($GLOBALS['ADA_LOGIN'])) {
                return parent::login($login, $password);
            }

            return true;
        }

        public function database()
        {
            $ada = $this->adaOnlyDb();
            return $ada !== '' ? $ada : parent::database();
        }

        /**
         * Tek bir veritabanı için açıldıysa YALNIZCA o listelenir.
         * <p>
         * Panel bağlantıyı o veritabanının kendi kullanıcısıyla kurduğunda zaten başkasını
         * göremez; ama kullanıcı bilinmediği için root'a düşüldüğünde bu filtre, yönetici
         * yetkisiyle açılmış bir oturumda diğer müşterilerin veritabanlarını göz önüne
         * sermemeyi sağlar.
         * </p>
         */
        public function databases($flush = true)
        {
            $ada = $this->adaOnlyDb();
            if ($ada !== '') {
                return array($ada);
            }

            return parent::databases($flush);
        }
    }

    return new AdaPanelAdminer();
}

// Adminer'in kendisi dağıtım sırasında webroot'a index.php olarak kopyalanır.
require __DIR__ . DIRECTORY_SEPARATOR . 'index.php';

""";

        await File.WriteAllTextAsync(
            Path.Combine(entryRoot, "adapanel-signon.php"),
            signon,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    // Panel reverse-proxy (B3) hedef portu: dağıtılmış aracın yerel IIS portu (IIS fallback seçmiş olabilir).
    public async Task<int> GetDeployedToolPortAsync(string provider, CancellationToken cancellationToken = default)
    {
        var states = await fileStore.ReadAsync(StatePath, Array.Empty<WebDbState>(), cancellationToken);
        var state = states.FirstOrDefault(item => string.Equals(item.Provider, provider, StringComparison.OrdinalIgnoreCase));
        return state?.Port ?? 0;
    }

    private async Task<string> ResolveRootPasswordAsync(string componentId, CancellationToken cancellationToken)
    {
        var component = await dbContext.InstalledComponents
            .FirstOrDefaultAsync(item => item.ComponentId == componentId && item.Status == ComponentInstallStatus.Installed, cancellationToken);
        return component is null ? string.Empty : ReadRootPassword(component);
    }

    // GÜVENLİK: DB aracı portunu (phpMyAdmin/Adminer) Windows Güvenlik Duvarı'nda KAPALI tutar —
    // eski sürümde public açılmış "AdaPanel WebDb {port}" kuralını kaldırır. Best-effort, idempotent.
    private static void EnsureFirewallPortClosed(int port)
    {
        try
        {
            var ruleName = $"AdaPanel WebDb {port}";
            RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
        }
        catch
        {
            // En iyi çaba; kural yoksa zaten kapalıdır.
        }
    }

    private static void RunNetsh(string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("netsh.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = System.Diagnostics.Process.Start(psi);
        process?.WaitForExit(15000);
    }

    // IIS app pool kimliği (IIS_IUSRS üyesi) bir dizine YAZABİLSİN (phpMyAdmin tmp/şablon önbelleği,
    // signon token dosyalarını okuma+silme). Best-effort; netsh/icacls yoksa sessizce geçer.
    /// <summary>
    /// Belirteç dizinini YALNIZCA o aracın kendi uygulama havuzuna açar.
    /// <para>
    /// GÜVENLİK — DÜZELTİLEN AÇIK: burada eskiden <c>IIS_IUSRS</c> grubuna izin veriliyordu. O grup
    /// sunucudaki BÜTÜN uygulama havuzlarını kapsar; yani her müşterinin PHP kodu bu dizini
    /// okuyabiliyordu. Dizinde ise veritabanı motorunun <b>root parolası düz metin</b> duruyor.
    /// Bir kiracının sitesine konan üç satırlık bir PHP dosyası, yönetici Adminer'ı her açtığında
    /// bu parolayı toplayıp sunucudaki TÜM müşterilerin veritabanlarına erişebilirdi.
    /// </para>
    /// <para>
    /// Miras da kırılır: üst klasörden gelen geniş izinler yoksa "yalnızca bu havuz" sözü
    /// tutulamaz. Kalanlar: SYSTEM (panel bu hesapla çalışır ve dosyayı o yazar) ve
    /// Administrators.
    /// </para>
    /// <para>
    /// Sonuç ÖLÇÜLÜR ve başarısızsa çağıran işlemi DURDURUR: parolayı korumasız bir dizine
    /// yazmaktansa aracı hiç açmamak doğrudur.
    /// </para>
    /// </summary>
    /// <summary>IIS_IUSRS grubunun sabit SID'i — sunucudaki TÜM uygulama havuzlarını kapsar.</summary>
    private const string IisIusrsSid = "S-1-5-32-568";

    /// <summary>
    /// Aracın uygulama havuzunun GERÇEK adını IIS'ten okur.
    /// <para>
    /// Ad TAHMİN EDİLMEZ. Panel siteyi <c>AdaPanel.{siteAdı}</c> havuzuyla kuruyor ama bu bir
    /// uygulama ayrıntısıdır: daha eski sürümlerle dağıtılmış kurulumlarda ad farklı olabilir,
    /// yönetici IIS'ten değiştirmiş olabilir. Yanlış hesap adı verildiğinde <c>icacls</c>
    /// hiçbir şey uygulamadan çıkıyor (bu makinede ölçüldü: hata 1332) — yani tahmin, dizini
    /// korumasız bırakmakla kalmaz, aracı da tamamen açılamaz hale getirir.
    /// </para>
    /// <para>Okunamazsa boş döner; çağıran o zaman işlemi durdurur.</para>
    /// </summary>
    private async Task<string> ResolveToolAppPoolNameAsync(string toolSiteName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(toolSiteName))
        {
            return string.Empty;
        }

        try
        {
            var site = await iisSiteService.GetSiteAsync(toolSiteName.Trim(), cancellationToken);
            return site?.ApplicationPoolName?.Trim() ?? string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "DB araci uygulama havuzu adi okunamadi ({SiteName}).", toolSiteName);
            return string.Empty;
        }
    }

    private static bool SecureTokenDirectory(string directory, string appPoolName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(appPoolName))
            {
                // Havuz adi bilinmiyorsa TAHMIN ETMEYIZ: yanlis hesap adi verilince icacls
                // hicbir seyi uygulamiyor (bu makinede olculdu: 1332) ve dizin korumasiz kalir.
                return false;
            }

            var havuz = appPoolName.Trim();
            var arguments =
                $"\"{directory}\" /inheritance:r " +
                "/grant \"*S-1-5-18:(OI)(CI)F\" " +      // SYSTEM — SID kullanilir: Turkce Windows'ta hesap ADI farklidir
                "/grant \"*S-1-5-32-544:(OI)(CI)F\" " +  // Administrators
                $"/grant \"IIS AppPool\\{havuz}:(OI)(CI)M\" /T /C /Q";

            var psi = new System.Diagnostics.ProcessStartInfo("icacls.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(20000);

            // CIKIS KODUNA GUVENILMEZ: bu makinede olculdu, icacls bir alt ogeye erisemedigi
            // halde ("Failed processing 1 files") 0 donuyor. Sonucu SONUCTAN okuruz.
            return VerifyTokenDirectoryLocked(directory);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Kilidin GERÇEKTEN uygulandığını dizinin kendi erişim listesinden doğrular.
    /// <para>
    /// Aranan iki şey: miras kapalı olmalı (yoksa üst klasörün geniş izinleri geçerli kalır) ve
    /// listede IIS_IUSRS bulunmamalı (o grup bütün müşteri havuzlarını kapsar).
    /// </para>
    /// </summary>
    private static bool VerifyTokenDirectoryLocked(string directory)
    {
        try
        {
            var guvenlik = new DirectoryInfo(directory).GetAccessControl();

            if (guvenlik.AreAccessRulesProtected == false)
            {
                return false;
            }

            var kurallar = guvenlik.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
            foreach (System.Security.AccessControl.FileSystemAccessRule kural in kurallar)
            {
                if (kural.AccessControlType == System.Security.AccessControl.AccessControlType.Allow &&
                    string.Equals(kural.IdentityReference.Value, IisIusrsSid, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            // Okuyamadiysak "kilitli" DEMEYIZ: parolayi yazmak icin kanit gerekir.
            return false;
        }
    }

    private static void CleanupOldTokens(string tokensDir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(tokensDir, "*.json"))
            {
                if (File.GetCreationTimeUtc(file) < DateTime.UtcNow.AddMinutes(-5))
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Best-effort temizlik.
        }
    }

    public Task<WebDbDeploymentDto> DeployPhpMyAdminAsync(CancellationToken cancellationToken = default)
    {
        return DeployAsync("phpmyadmin", "phpMyAdmin", "AdaPanel.WebDb.PhpMyAdmin", 8896, cancellationToken);
    }

    public Task<WebDbDeploymentDto> DeployAdminerAsync(CancellationToken cancellationToken = default)
    {
        return DeployAsync("adminer", "Adminer", "AdaPanel.WebDb.Adminer", 8897, cancellationToken);
    }

    private async Task<WebDbDeploymentDto> DeployAsync(
        string componentId,
        string displayName,
        string siteName,
        int defaultPort,
        CancellationToken cancellationToken)
    {
        var component = await EnsureInstalledAsync(componentId, cancellationToken);
        var phpComponentId = await EnsurePhpInstalledAsync(cancellationToken);
        var entryRoot = await PrepareEntryRootAsync(componentId, component.InstallDir, cancellationToken);

        if (componentId == "phpmyadmin")
        {
            await EnsurePhpMyAdminConfigAsync(entryRoot, cancellationToken);
        }
        Iis.WebAppPublisher.EnsurePhpEntry(entryRoot);
        EnsureHealthEndpoint(entryRoot);

        var states = (await fileStore.ReadAsync(StatePath, Array.Empty<WebDbState>(), cancellationToken)).ToList();
        var existing = states.FirstOrDefault(item => item.Provider == componentId);
        var requestedPort = existing?.Port is > 0 ? existing.Port : defaultPort;

        // Recreate this dedicated managed site so stale localhost-only bindings cannot survive
        // an automatic repair. IIS may select a fallback port if the requested port is occupied.
        await DeleteManagedSiteIfPresentAsync(siteName, cancellationToken);
        var createdSite = await CreateSiteAsync(siteName, entryRoot, requestedPort, cancellationToken);

        // DB araçları (phpMyAdmin/Adminer) profesyonel paneller gibi HER ZAMAN sıcak kalsın: app pool AlwaysRunning +
        // idle timeout kapalı. Aksi halde ~20 dk boştan sonra pool durur, sonraki istek PHP'yi SOĞUK
        // başlatır ve sağlık kontrolü "TaskCanceled" verir ("çalışıyordu sonra olmadı" şikâyeti).
        await KeepAppPoolWarmAsync($"AdaPanel.{siteName}", cancellationToken);

        var port = createdSite.Bindings
            .Where(binding =>
                binding.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(binding.HostName))
            .Select(binding => binding.Port)
            .FirstOrDefault();
        if (port <= 0)
        {
            throw new AppException(
                "webdb.health_failed",
                500,
                500,
                $"IIS did not create a wildcard HTTP binding for site '{siteName}'.");
        }

        // TÜM PHP app'leriyle ortak: placeholder index.html'i kaldır + index.php varsayılan
        // belge web.config'ini yaz ki "/" doğrudan phpMyAdmin/Adminer'i açsın (403.14 önlenir).
        Iis.WebAppPublisher.EnsurePhpEntry(entryRoot);
        // GÜVENLİK (savunma derinliği): paket/metadata dosyalarını (composer.json, vendor/ ...) HTTP'den
        // engelle — codex bulgusu: 8896/composer.json + vendor/composer/installed.json okunuyordu.
        HardenWebDbWebConfig(entryRoot);
        await phpService.RegisterFastCgiAsync(phpComponentId, cancellationToken);
        await phpService.SetSitePhpVersionAsync(siteName, new SetSitePhpVersionRequest
        {
            ComponentId = phpComponentId
        }, cancellationToken);

        // GÜVENLİK: DB aracı public portu AÇILMAZ (loopback + panel proxy yeterli). Eski sürümlerde
        // açılmış "AdaPanel WebDb {port}" kuralı varsa KALDIRILIR ki phpMyAdmin/Adminer dışarıdan
        // erişilebilir kalmasın (codex bulgusu: 8896 public). Best-effort; idempotent.
        EnsureFirewallPortClosed(port);

        var state = new WebDbState
        {
            Provider = componentId,
            DisplayName = displayName,
            ComponentId = componentId,
            SiteName = siteName,
            Port = port,
            Url = $"http://127.0.0.1:{port}/",
            InstallDir = component.InstallDir,
            EntryFilePath = Path.Combine(entryRoot, "index.php"),
            PhpComponentId = phpComponentId
        };

        var index = states.FindIndex(item => item.Provider == componentId);
        if (index >= 0)
        {
            states[index] = state;
        }
        else
        {
            states.Add(state);
        }

        await fileStore.WriteAsync(StatePath, states, cancellationToken);
        var deployment = MapState(state, true, true);
        await UpdateHealthAsync(deployment, cancellationToken, retry: true);
        if (!deployment.IsHealthy)
        {
            throw new AppException(
                "webdb.health_failed",
                500,
                500,
                string.IsNullOrWhiteSpace(deployment.HealthDetails)
                    ? deployment.HealthMessage
                    : $"{deployment.HealthMessage}\r\n{deployment.HealthDetails}");
        }

        return deployment;
    }

    private async Task DeleteManagedSiteIfPresentAsync(
        string siteName,
        CancellationToken cancellationToken)
    {
        try
        {
            await iisSiteService.DeleteSiteAsync(siteName, cancellationToken);
        }
        catch (AppException exception) when (exception.MessageKey == "sites.site_not_found")
        {
            // The first deployment has no existing site.
        }
    }

    private async Task<InstalledComponentDto> EnsureInstalledAsync(string componentId, CancellationToken cancellationToken)
    {
        var installed = await dbContext.InstalledComponents
            .Where(item => item.ComponentId == componentId && item.Status == ComponentInstallStatus.Installed)
            .Select(item => new InstalledComponentDto
            {
                ComponentId = item.ComponentId,
                DisplayName = item.DisplayName,
                InstallDir = item.InstallDir,
                Status = item.Status.ToString()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (installed is not null)
        {
            return installed;
        }

        var result = await componentBootstrapper.InstallAsync(componentId, null, null, force: false, cancellationToken: cancellationToken);
        return new InstalledComponentDto
        {
            ComponentId = result.ComponentId,
            DisplayName = result.DisplayName,
            InstallDir = result.InstallDir,
            Status = result.Status
        };
    }

    private async Task<string> EnsurePhpInstalledAsync(CancellationToken cancellationToken)
    {
        var installedPhp = await dbContext.InstalledComponents
            .Where(item => item.Status == ComponentInstallStatus.Installed && item.ComponentId.StartsWith("php-"))
            .OrderByDescending(item => item.ComponentId)
            .Select(item => item.ComponentId)
            .FirstOrDefaultAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(installedPhp))
        {
            return installedPhp;
        }

        var result = await componentBootstrapper.InstallAsync("php-8.3", null, null, force: false, cancellationToken: cancellationToken);
        return result.ComponentId;
    }

    private async Task<string> PrepareEntryRootAsync(string componentId, string installDir, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (componentId == "phpmyadmin")
        {
            // phpMyAdmin kökünde index.php + config.sample.inc.php birlikte bulunur. Zip genelde
            // "phpMyAdmin-X.Y.Z-all-languages\" alt klasörüne açılır; yapı/derinlik ne olursa olsun
            // RECURSIVE arayıp kesin kökü buluruz (eski kod tek seviye bakıp installDir'e düşüyordu
            // -> 403.14: index.php yok). config.sample.inc.php phpMyAdmin'e özgü güvenilir işarettir.
            if (File.Exists(Path.Combine(installDir, "config.sample.inc.php")))
            {
                return installDir;
            }

            var byMarker = Directory.EnumerateFiles(installDir, "config.sample.inc.php", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .Where(directory => directory is not null && File.Exists(Path.Combine(directory, "index.php")))
                .OrderBy(directory => directory!.Length)
                .FirstOrDefault();
            if (byMarker is not null)
            {
                return byMarker;
            }

            // Yedek: en sığ index.php'nin dizini.
            var anyIndex = Directory.EnumerateFiles(installDir, "index.php", SearchOption.AllDirectories)
                .OrderBy(path => path.Length)
                .FirstOrDefault();
            return anyIndex is not null ? Path.GetDirectoryName(anyIndex)! : installDir;
        }

        var webRoot = Path.Combine(installDir, "webroot");
        Directory.CreateDirectory(webRoot);
        var adminerFile = Path.Combine(installDir, "adminer.php");
        var targetFile = Path.Combine(webRoot, "index.php");

        if (File.Exists(adminerFile))
        {
            File.Copy(adminerFile, targetFile, overwrite: true);
        }
        else if (!File.Exists(targetFile))
        {
            await File.WriteAllTextAsync(targetFile, "<?php echo 'Adminer deployment is incomplete.'; ?>", cancellationToken);
        }

        return webRoot;
    }

    private async Task EnsurePhpMyAdminConfigAsync(
        string entryRoot,
        CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(entryRoot, "config.inc.php");
        const string managedMarker = "Managed by AdaPanel";
        if (File.Exists(configPath) &&
            !File.ReadAllText(configPath).Contains(managedMarker, StringComparison.OrdinalIgnoreCase))
        {
            // Preserve a configuration explicitly maintained by the administrator.
            return;
        }

        var components = await dbContext.InstalledComponents
            .Where(item =>
                item.Status == ComponentInstallStatus.Installed &&
                (item.ComponentId == "mariadb-11" || item.ComponentId == "mysql-8"))
            .ToArrayAsync(cancellationToken);
        // Root parolası için önce MariaDB (3306), yoksa MySQL kaydı.
        var dbComponent = components.FirstOrDefault(item => item.ComponentId == "mariadb-11")
            ?? components.FirstOrDefault(item => item.ComponentId == "mysql-8");

        var secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

        _ = dbComponent;

        // phpMyAdmin RESMİ SIGNON akışı (examples/signon.php yöntemi). İlk denememdeki "Internal error"
        // sebebi: signon oturumuna PMA_single_signon_HMAC_secret KOYMUYORDUM (phpMyAdmin 5.x doğruluyor)
        // ve SignonScript yerine SignonURL kullanılmalıydı. adapanel-signon.php artık resmi örneğe göre
        // oturumu (user/password/host/port/HMAC + only_db) kurup phpMyAdmin'e yönlendiriyor → DB
        // kullanıcısıyla girince YALNIZ o DB görünür (profesyonel paneller gibi gerçek izolasyon, cookie/CSRF derdi yok).

        // phpMyAdmin AÇILIŞTA Twig şablon motorunu kullanır; TempDir yazılabilir olmalı.
        var tempDir = Path.Combine(entryRoot, "tmp");
        Directory.CreateDirectory(tempDir);
        // Burada sır YOK (yalnız Twig şablon önbelleği), ama izin yine de tek havuza daraltılır:
        // "her havuza açık" olmasının hiçbir gerekçesi yok.
        //
        // Başarısız olursa işlem DURMAZ: bu klasörde parola yok, ve phpMyAdmin şablon önbelleğini
        // yazamazsa açılmaz. Güvenlik kazancı burada, aracı kırma riskini haklı çıkarmaz.
        var pmaHavuzu = await ResolveToolAppPoolNameAsync("AdaPanel.WebDb.PhpMyAdmin", cancellationToken);
        if (!string.IsNullOrWhiteSpace(pmaHavuzu))
        {
            SecureTokenDirectory(tempDir, pmaHavuzu);
        }
        var tempDirPhp = tempDir.Replace('\\', '/');

        // ÜRETİM: phpMyAdmin public portta açık; PHP hatalarını EKRANA YAZDIRMA (yol/iç bilgi sızmasın).
        // (Teşhis için geçici display_errors=On kullanılmıştı; signon düzeldikten sonra kapatıldı.)
        var userIni = "display_errors = Off\r\n";
        await File.WriteAllTextAsync(
            Path.Combine(entryRoot, ".user.ini"),
            userIni,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        var config = $$"""
            <?php
            // Managed by AdaPanel. Custom files without this marker are preserved.
            $cfg['blowfish_secret'] = '{{secret}}';
            $cfg['TempDir'] = '{{tempDirPhp}}';
            $i = 0;
            $i++;
            $cfg['Servers'][$i]['verbose'] = 'AdaPanel';
            $cfg['Servers'][$i]['host'] = '127.0.0.1';
            $cfg['Servers'][$i]['port'] = '3306';
            $cfg['Servers'][$i]['auth_type'] = 'signon';
            $cfg['Servers'][$i]['SignonSession'] = 'AdaPanelSSO';
            $cfg['Servers'][$i]['SignonURL'] = 'adapanel-signon.php';
            $cfg['Servers'][$i]['AllowNoPassword'] = true;
            $cfg['UploadDir'] = '';
            $cfg['SaveDir'] = '';
            // B3: panel reverse-proxy arkasındayken phpMyAdmin mutlak URL'leri (signon redirect, asset, AJAX)
            // panel yolundan üretmeli. YALNIZ proxy üzerinden gelindiğinde (X-Forwarded-Host var) ayarlanır;
            // doğrudan 8896 erişimi (forwarded header yok) etkilenmez → fallback bozulmaz.
            if (!empty($_SERVER['HTTP_X_FORWARDED_HOST'])) {
                $pmaProto = !empty($_SERVER['HTTP_X_FORWARDED_PROTO']) ? $_SERVER['HTTP_X_FORWARDED_PROTO'] : 'https';
                $cfg['PmaAbsoluteUri'] = $pmaProto . '://' . $_SERVER['HTTP_X_FORWARDED_HOST'] . '/api/v1/dbtools/phpmyadmin/';
            }
            """;

        await File.WriteAllTextAsync(
            configPath,
            config,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        await WriteSignonPageAsync(entryRoot, cancellationToken);
    }

    /// <summary>
    /// phpMyAdmin RESMİ signon sayfasını (examples/signon.php yöntemi) yazar: tek seferlik token'daki
    /// kimlik bilgileriyle signon oturumunu (HMAC_secret dahil) kurup phpMyAdmin'e yönlendirir.
    /// DB kullanıcısıyla girince phpMyAdmin yalnız o kullanıcının yetkili olduğu DB'yi gösterir.
    /// </summary>
    private static async Task WriteSignonPageAsync(string entryRoot, CancellationToken cancellationToken)
    {
        var tokensDir = Path.Combine(entryRoot, "adapanel-tokens");
        Directory.CreateDirectory(tokensDir);

        // Token dosyaları parola içerir; HTTP ile OKUNMASINI engelle (handlers temizlenir).
        await File.WriteAllTextAsync(
            Path.Combine(tokensDir, "web.config"),
            "<?xml version=\"1.0\"?>\r\n<configuration><system.webServer><handlers><clear /></handlers></system.webServer></configuration>",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        // Config'teki SignonSession ile AYNI olmalı.
        const string signon = """
            <?php
            // AdaPanel signon — phpMyAdmin resmi examples/signon.php yöntemi.
            $signonSession = 'AdaPanelSSO';
            $user = ''; $pass = ''; $host = '127.0.0.1'; $port = '3306'; $onlyDb = '';
            if (!empty($_GET['token'])) {
                $token = preg_replace('/[^a-f0-9]/', '', (string)$_GET['token']);
                $file = __DIR__ . '/adapanel-tokens/' . $token . '.json';
                if ($token !== '' && is_file($file)) {
                    $raw = file_get_contents($file);
                    @unlink($file); // tek seferlik
                    $data = json_decode($raw, true);
                    if (is_array($data)) {
                        $user = isset($data['user']) ? (string)$data['user'] : '';
                        $pass = isset($data['password']) ? (string)$data['password'] : '';
                        $host = isset($data['host']) ? (string)$data['host'] : '127.0.0.1';
                        $port = isset($data['port']) ? (string)$data['port'] : '3306';
                        $onlyDb = isset($data['only_db']) ? (string)$data['only_db'] : '';
                    }
                }
            }

            if ($user === '') {
                header('Location: index.php'); // token yok/geçersiz → normal phpMyAdmin
                exit;
            }

            session_name($signonSession);
            @session_start();
            $_SESSION['PMA_single_signon_user'] = $user;
            $_SESSION['PMA_single_signon_password'] = $pass;
            $_SESSION['PMA_single_signon_host'] = $host;
            $_SESSION['PMA_single_signon_port'] = $port;
            // phpMyAdmin 5.x bu HMAC secret'i DOĞRULAR; eksikse signon başarısız olur.
            $_SESSION['PMA_single_signon_HMAC_secret'] = hash('sha1', uniqid(strval(rand()), true));
            if ($onlyDb !== '') {
                $_SESSION['PMA_single_signon_cfgupdate'] = array('only_db' => $onlyDb);
            }
            @session_write_close();

            // DB belirtildiyse phpMyAdmin'i O VERİTABANI SEÇİLİ aç → "İçe Aktar" doğrudan o DB'ye gider,
            // "#1046 No database selected" olmaz (profesyonel paneller gibi: DB'ye tıklayınca o DB seçili açılır).
            if ($onlyDb !== '') {
                header('Location: index.php?db=' . urlencode($onlyDb));
            } else {
                header('Location: index.php');
            }
            exit;
            """;
        await File.WriteAllTextAsync(
            Path.Combine(entryRoot, "adapanel-signon.php"),
            signon,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    private static void AppendServer(System.Text.StringBuilder builder, string verbose, int port, string rootPassword)
    {
        builder.AppendLine();
        builder.AppendLine("$i++;");
        builder.AppendLine($"$cfg['Servers'][$i]['verbose'] = '{EscapePhp(verbose)}';");
        builder.AppendLine("$cfg['Servers'][$i]['host'] = '127.0.0.1';");
        builder.AppendLine($"$cfg['Servers'][$i]['port'] = '{port}';");

        if (!string.IsNullOrWhiteSpace(rootPassword))
        {
            // config auth: phpMyAdmin root ile doğrudan bağlanır (login ekranı atlanır).
            builder.AppendLine("$cfg['Servers'][$i]['auth_type'] = 'config';");
            builder.AppendLine("$cfg['Servers'][$i]['user'] = 'root';");
            builder.AppendLine($"$cfg['Servers'][$i]['password'] = '{EscapePhp(rootPassword)}';");
        }
        else
        {
            builder.AppendLine("$cfg['Servers'][$i]['auth_type'] = 'cookie';");
            builder.AppendLine("$cfg['Servers'][$i]['AllowNoPassword'] = false;");
        }
    }

    private string ReadRootPassword(AdaPanel.Core.Entities.InstalledComponent component)
    {
        if (string.IsNullOrWhiteSpace(component.ProtectedConfigurationJson))
        {
            return string.Empty;
        }

        try
        {
            var payload = secretProtector.Unprotect(component.ProtectedConfigurationJson);
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(GeneratedPasswordKey, out var value) &&
                value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // Okunamadıysa cookie auth'a düş.
        }

        return string.Empty;
    }

    // PHP tek-tırnak literal kaçışı: \ ve ' kaçırılır.
    private static string EscapePhp(string value)
        => (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'");

    private static void EnsureHealthEndpoint(string entryRoot)
    {
        File.WriteAllText(
            Path.Combine(entryRoot, "adapanel-health.php"),
            "<?php header('Content-Type: text/plain; charset=utf-8'); echo 'AdaPanel-WebDb-OK';",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // GÜVENLİK (savunma derinliği): WebDB aracının kök web.config'ine, paket/metadata dosyalarının HTTP ile
    // OKUNMASINI engelleyen requestFiltering kuralları ekler. Codex bulgusu: /composer.json ve
    // /vendor/composer/installed.json public okunuyordu. vendor/ sunucu-tarafı include'dur (autoload);
    // HTTP erişimini kapatmak phpMyAdmin'i bozmaz, asset'leri (js/, themes/) etkilemez. Idempotent.
    private static void HardenWebDbWebConfig(string entryRoot)
    {
        try
        {
            var webConfigPath = Path.Combine(entryRoot, "web.config");
            var document = File.Exists(webConfigPath)
                ? System.Xml.Linq.XDocument.Load(webConfigPath)
                : new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("configuration"));

            var configuration = document.Root ?? new System.Xml.Linq.XElement("configuration");
            var requestFiltering = EnsureXmlChild(
                EnsureXmlChild(
                    EnsureXmlChild(configuration, "system.webServer"),
                    "security"),
                "requestFiltering");

            var hiddenSegments = EnsureXmlChild(requestFiltering, "hiddenSegments");

            // KÖK NEDEN DÜZELTMESİ: 'vendor' gizli-segmenti, IIS hiddenSegments YOLDAKİ HER segmenti eşlediği için
            // phpMyAdmin'in JS kütüphanelerini (js/vendor/jquery, js/vendor/codemirror ...) de 404 yapıyordu →
            // jQuery yüklenmeyince phpMyAdmin'in TÜM JS'i çalışmıyordu (sol menü '+' açılmıyor, hiç AJAX yok; sayfa
            // yalnız CSS'le biçimli görünüyor). 'vendor'ı ENGELLEME (root composer dizini zaten loopback + auth
            // proxy arkasında, asıl koruma o). Eski dağıtımlarda kalmış 'vendor' kaydını da TEMİZLE.
            hiddenSegments.Elements("add")
                .Where(element => string.Equals((string?)element.Attribute("segment"), "vendor", StringComparison.OrdinalIgnoreCase))
                .ToList()
                .ForEach(element => element.Remove());

            foreach (var segment in new[]
                     {
                         "composer.json", "composer.lock", "package.json",
                         "package-lock.json", "yarn.lock", ".git", ".github", "examples", "test", "node_modules",
                         // Belirteç dizini web kökünün İÇİNDE duruyor ve içinde veritabanı parolası
                         // var. Asıl koruma dosya sistemi izinleridir (SecureTokenDirectory); bu,
                         // HTTP tarafını da kapatan ikinci katmandır.
                         "adapanel-tokens"
                     })
            {
                AddXmlAttributeChildIfMissing(hiddenSegments, "add", "segment", segment);
            }

            var fileExtensions = EnsureXmlChild(requestFiltering, "fileExtensions");
            foreach (var extension in new[] { ".md", ".lock", ".dist", ".sh", ".yml", ".yaml" })
            {
                AddDenyExtensionIfMissing(fileExtensions, extension);
            }

            document.Save(webConfigPath);
        }
        catch
        {
            // Best-effort; asıl koruma loopback binding'dir (public erişim zaten kapalı).
        }
    }

    private static System.Xml.Linq.XElement EnsureXmlChild(System.Xml.Linq.XElement parent, string name)
    {
        var child = parent.Element(name);
        if (child is null)
        {
            child = new System.Xml.Linq.XElement(name);
            parent.Add(child);
        }

        return child;
    }

    private static void AddXmlAttributeChildIfMissing(
        System.Xml.Linq.XElement parent,
        string elementName,
        string attributeName,
        string value)
    {
        var exists = parent.Elements(elementName).Any(element =>
            string.Equals((string?)element.Attribute(attributeName), value, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            parent.Add(new System.Xml.Linq.XElement(elementName, new System.Xml.Linq.XAttribute(attributeName, value)));
        }
    }

    private static void AddDenyExtensionIfMissing(System.Xml.Linq.XElement fileExtensions, string extension)
    {
        var exists = fileExtensions.Elements("add").Any(element =>
            string.Equals((string?)element.Attribute("fileExtension"), extension, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            fileExtensions.Add(new System.Xml.Linq.XElement(
                "add",
                new System.Xml.Linq.XAttribute("fileExtension", extension),
                new System.Xml.Linq.XAttribute("allowed", "false")));
        }
    }

    private Task<SiteDto> CreateSiteAsync(
        string siteName,
        string physicalPath,
        int port,
        CancellationToken cancellationToken)
    {
        return iisSiteService.CreateSiteAsync(new CreateSiteRequest
        {
            Name = siteName,
            PhysicalPath = physicalPath,
            ApplicationPoolName = $"AdaPanel.{siteName}",
            HostName = string.Empty,
            // GÜVENLİK (profesyonel paneller gibi): DB araçları YALNIZ loopback'te (127.0.0.1) dinler — dışarıdan
            // doğrudan *:8896/8897 erişimi YOK. Erişim panelin kimlik-doğrulamalı HTTPS reverse-proxy'si
            // (DbToolsProxyController [Authorize(Roles="Administrator")]) üzerinden; o da 127.0.0.1'e iletir.
            IpAddress = "127.0.0.1",
            Port = port,
            Protocol = "http",
            AutoStart = true
        }, cancellationToken);
    }

    // App pool'u sürekli ayakta tutar (DB aracı soğuk başlamasın → sağlık kontrolü timeout vermesin).
    // Best-effort: başarısız olsa da dağıtım bozulmaz (araç çalışır, sadece ilk istek yavaş olabilir).
    private static async Task KeepAppPoolWarmAsync(string appPoolName, CancellationToken cancellationToken)
    {
        var pool = appPoolName.Replace("'", "''");
        var script = $$"""
            Import-Module WebAdministration -ErrorAction Stop
            $pool = '{{pool}}'
            if (Test-Path "IIS:\AppPools\$pool") {
              Set-ItemProperty "IIS:\AppPools\$pool" -Name startMode -Value 'AlwaysRunning'
              Set-ItemProperty "IIS:\AppPools\$pool" -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
              Start-WebAppPool -Name $pool -ErrorAction SilentlyContinue
            }
            """;
        await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
    }

    private static WebDbDeploymentDto BuildDeployment(
        string provider,
        string displayName,
        string componentId,
        int defaultPort,
        string supportedEngines,
        string accessMode,
        IReadOnlyCollection<WebDbState> states,
        IReadOnlyDictionary<string, AdaPanel.Core.Entities.InstalledComponent> installedMap)
    {
        var state = states.FirstOrDefault(item => item.Provider == provider);
        var isInstalled = installedMap.TryGetValue(componentId, out var component);

        return state is null
            ? new WebDbDeploymentDto
            {
                Provider = provider,
                DisplayName = displayName,
                ComponentId = componentId,
                InstallDir = component?.InstallDir ?? string.Empty,
                Port = defaultPort,
                SupportedEngines = supportedEngines,
                AccessMode = accessMode,
                IsInstalled = isInstalled
            }
            : MapState(state, isInstalled, true, supportedEngines, accessMode);
    }

    private static WebDbDeploymentDto MapState(
        WebDbState state,
        bool isInstalled,
        bool isDeployed,
        string? supportedEngines = null,
        string? accessMode = null)
    {
        return new WebDbDeploymentDto
        {
            Provider = state.Provider,
            DisplayName = state.DisplayName,
            ComponentId = state.ComponentId,
            SiteName = state.SiteName,
            Port = state.Port,
            Url = state.Url,
            InstallDir = state.InstallDir,
            EntryFilePath = state.EntryFilePath,
            PhpComponentId = state.PhpComponentId,
            SupportedEngines = supportedEngines ?? ResolveSupportedEngines(state.Provider),
            AccessMode = accessMode ?? "iis-web",
            IsInstalled = isInstalled,
            IsDeployed = isDeployed,
            IsHealthy = accessMode == "desktop" && isInstalled,
            HealthMessage = accessMode == "desktop" && isInstalled
                ? "webdb.health.ready"
                : string.Empty
        };
    }

    private async Task UpdateHealthAsync(
        WebDbDeploymentDto deployment,
        CancellationToken cancellationToken,
        bool retry = false)
    {
        if (deployment.AccessMode == "desktop")
        {
            deployment.IsHealthy = deployment.IsInstalled;
            deployment.HealthMessage = deployment.IsInstalled
                ? "webdb.health.ready"
                : "webdb.health.not_installed";
            return;
        }

        if (!deployment.IsInstalled)
        {
            SetUnhealthy(deployment, "webdb.health.not_installed");
            return;
        }

        if (!deployment.IsDeployed)
        {
            SetUnhealthy(deployment, "webdb.health.not_deployed");
            return;
        }

        if (string.IsNullOrWhiteSpace(deployment.EntryFilePath) ||
            !File.Exists(deployment.EntryFilePath))
        {
            SetUnhealthy(deployment, "webdb.health.entry_missing");
            return;
        }

        try
        {
            var site = await iisSiteService.GetSiteAsync(deployment.SiteName, cancellationToken);
            var binding = site.Bindings.FirstOrDefault(binding =>
                binding.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(binding.HostName));
            if (binding is null)
            {
                SetUnhealthy(
                    deployment,
                    "webdb.health.binding_invalid",
                    $"Site={deployment.SiteName}\r\nBindings={string.Join(", ", site.Bindings.Select(item => item.BindingInformation))}");
                return;
            }

            deployment.Port = binding.Port;
            deployment.Url = $"http://127.0.0.1:{binding.Port}/";
            if (!site.State.Equals("Started", StringComparison.OrdinalIgnoreCase))
            {
                SetUnhealthy(
                    deployment,
                    "webdb.health.site_stopped",
                    $"Site={deployment.SiteName}\r\nState={site.State}");
                return;
            }
        }
        catch (Exception exception)
        {
            SetUnhealthy(deployment, "webdb.health.iis_missing", exception.Message);
            return;
        }

        var attempts = retry ? 8 : 1;
        HttpProbeResult? lastProbe = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            lastProbe = await ProbeAsync(
                deployment.Port,
                "/adapanel-health.php",
                "AdaPanel-WebDb-OK",
                cancellationToken);
            if (lastProbe.Success)
            {
                var applicationProbe = await ProbeAsync(
                    deployment.Port,
                    "/",
                    expectedBodyMarker: null,
                    cancellationToken);
                if (applicationProbe.Success)
                {
                    deployment.IsHealthy = true;
                    deployment.HealthMessage = "webdb.health.ready";
                    deployment.HealthDetails = string.Empty;
                    return;
                }

                lastProbe = applicationProbe;
            }

            if (attempt + 1 < attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }

        SetUnhealthy(
            deployment,
            "webdb.health.http_failed",
            lastProbe?.Details ?? $"No HTTP response was received from port {deployment.Port}.");
    }

    private async Task<HttpProbeResult> ProbeAsync(
        int port,
        string path,
        string? expectedBodyMarker,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // PHP soğuk başlangıcı (app pool ilk uyandığında) birkaç saniye sürebilir; 3sn çok kısaydı
            // ve "TaskCanceled" veriyordu. 12sn cold-start'ı tolere eder (bağlantı reddi yine hızlı döner).
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{path}");
            using var response = await httpClientFactory.CreateClient("AdaPanel.LocalHealth").SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var success =
                (int)response.StatusCode is >= 200 and < 400 &&
                (expectedBodyMarker is null ||
                 body.Contains(expectedBodyMarker, StringComparison.Ordinal));
            return new HttpProbeResult
            {
                Success = success,
                Details = BuildProbeDetails(port, path, response, body, expectedBodyMarker)
            };
        }
        catch (Exception exception)
        {
            return new HttpProbeResult
            {
                Details = $"Url=http://127.0.0.1:{port}{path}\r\n{exception.GetType().Name}: {exception.Message}"
            };
        }
    }

    private static string BuildProbeDetails(
        int port,
        string path,
        HttpResponseMessage response,
        string body,
        string? expectedBodyMarker)
    {
        var preview = body.Length <= 1200 ? body : body[..1200] + "...";
        return
            $"Url=http://127.0.0.1:{port}{path}\r\n" +
            $"Status={(int)response.StatusCode} {response.ReasonPhrase}\r\n" +
            (expectedBodyMarker is null ? string.Empty : $"Expected={expectedBodyMarker}\r\n") +
            $"Response={preview}";
    }

    private static void SetUnhealthy(
        WebDbDeploymentDto deployment,
        string message,
        string details = "")
    {
        deployment.IsHealthy = false;
        deployment.HealthMessage = message;
        deployment.HealthDetails = details;
    }

    private static string ResolveSupportedEngines(string provider)
        => provider.Equals("adminer", StringComparison.OrdinalIgnoreCase)
            ? "MySQL / MariaDB / PostgreSQL"
            : "MySQL / MariaDB";

    private sealed class WebDbState
    {
        public string Provider { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string ComponentId { get; set; } = string.Empty;

        public string SiteName { get; set; } = string.Empty;

        public int Port { get; set; }

        public string Url { get; set; } = string.Empty;

        public string InstallDir { get; set; } = string.Empty;

        public string EntryFilePath { get; set; } = string.Empty;

        public string PhpComponentId { get; set; } = string.Empty;
    }

    private sealed class HttpProbeResult
    {
        public bool Success { get; init; }

        public string Details { get; init; } = string.Empty;
    }
}
