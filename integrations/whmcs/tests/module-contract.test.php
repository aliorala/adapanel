<?php

/**
 * AdaPanel WHMCS modulu -- PANEL SOZLESMESI davranis testi.
 *
 * NEDEN VAR: modul 1430 satir ve bugune kadar tek satiri CALISTIRILMADI. `php -l` yalnizca
 * sozdizimini olcer; `leftover-note.test.php` de yalnizca not uretimini olcuyor ve
 * `AdaPanelClient`'a hic dokunmuyor. Yani modulun panele HANGI YOLA, hangi METOTLA, hangi
 * BASLIKLARLA ve hangi ALAN ADLARIYLA gittigi hicbir yerde olculmemisti -- oysa yanlis yol ya da
 * eksik baslik, panelde "uc yok" (404) gibi gorunur ve modul sessizce olu kalir.
 *
 * NASIL OLCULUYOR: istemci mock'lanMIYOR. `fake-panel.php` gercek bir HTTP sunucusu olarak
 * ayaga kaldiriliyor ve modul ona GERCEK curl istegi atiyor. Sunucu gordugu her istegi
 * kaydediyor; test o kayda bakiyor. Istemciyi mock'lamak, tam olarak sinanmasi gereken kodu
 * (curl kurulumu, basliklar, zarf cozumu, hata yolu) atlamak olurdu.
 *
 * Calistirma:  php integrations/whmcs/tests/module-contract.test.php
 * Cikis kodu:  0 = gecti, 1 = kaldi.
 */

define('WHMCS', true);

require_once __DIR__ . '/whmcs-stub.php';
// Yalnizca `ada_fake_dizin()` icin: dosya CLI'da calistirilmadigi icin yonlendirici govdesini
// atlar (bkz. fake-panel.php icindeki SAPI kontrolu). Yolu iki yerde yazmamak icin boyle.
require_once __DIR__ . '/fake-panel.php';
require_once __DIR__ . '/../adapanel/adapanel.php';

$basarisiz = 0;
$port = (int) (getenv('ADA_FAKE_PORT') ?: 8977);

function ada_assert($kosul, $mesaj)
{
    global $basarisiz;
    if ($kosul) {
        echo "  gecti : $mesaj\n";
        return;
    }

    echo "  KALDI : $mesaj\n";
    $basarisiz++;
}

// --- sahte panelin ayaga kaldirilmasi ---

$sunucu = null;
$borular = array();

/** Sunucunun stdin'i icin bos dosya (Windows'ta /dev/null yok). */
function ada_fake_bos_giris()
{
    $yol = ada_fake_dizin() . DIRECTORY_SEPARATOR . 'stdin.empty';
    if (!is_file($yol)) {
        file_put_contents($yol, '');
    }

    return $yol;
}

function ada_sunucu_baslat($port)
{
    global $sunucu, $boru;

    // PANELIN PHP'SI `proc_open`'i KAPATIR (php.ini disable_functions: barindirma sertlestirmesi,
    // dogru bir karar). Yani bu test o ini ile dogrudan calistirilamaz. SESSIZCE ATLAMIYORUZ --
    // "test kosmadi" ile "test gecti" ayni sey degil; sebebi ve cozumu yaziliyor.
    if (!function_exists('proc_open')) {
        echo "KALDI: `proc_open` kapali (php.ini disable_functions), sahte panel baslatilamaz.\n";
        echo "       Cozum: pwsh scripts/run-whmcs-tests.ps1  (betik php'yi -d disable_functions= ile cagirir)\n";
        exit(1);
    }

    $komut = escapeshellarg(PHP_BINARY) . ' -d disable_functions= -S 127.0.0.1:' . $port . ' '
        . escapeshellarg(__DIR__ . DIRECTORY_SEPARATOR . 'fake-panel.php');

    // ÇIKTI BORUYA DEGIL DOSYAYA. `php -S` her istek icin stderr'e bir satir yazar; boru
    // okunmazsa isletim sistemi tamponu dolar ve SUNUCU KILITLENIR (ilk denemede tam olarak bu
    // oldu: test iki dakika asildi). Dosyaya yazmak hem kilitlenmeyi kaldirir hem de arizada
    // sunucunun ne gordugunu okunabilir birakir.
    $gunluk = ada_fake_dizin() . DIRECTORY_SEPARATOR . 'server.log';
    $tanimlar = array(
        0 => array('file', ada_fake_bos_giris(), 'r'),
        1 => array('file', $gunluk, 'w'),
        2 => array('file', $gunluk, 'a'),
    );
    // `bypass_shell` SART: Windows'ta proc_open varsayilan olarak komutu `cmd.exe /c` icine sarar
    // ve `proc_terminate` yalnizca o SARMALAYICIYI oldururdu -- `php -S` yetim kalir, portu tutmaya
    // devam eder ve testin bir sonraki kosumu ONCEKI kosumun sunucusuyla konusurdu (olculdu: test
    // bitip cikamiyordu). Bayrakla komut dogrudan calistirilir, oldurulen sey gercek sunucudur.
    $sunucu = proc_open($komut, $tanimlar, $boru, __DIR__, null, array('bypass_shell' => true));

    if (!is_resource($sunucu)) {
        echo "KALDI: sahte panel baslatilamadi\n";
        exit(1);
    }

    // Port ACILANA KADAR beklenir; sabit bir uyku, yavas makinede kirilgan test demek olurdu.
    for ($deneme = 0; $deneme < 100; $deneme++) {
        $baglanti = @fsockopen('127.0.0.1', $port, $hata, $hataMesaji, 0.2);
        if ($baglanti) {
            fclose($baglanti);
            return;
        }
        usleep(100000);
    }

    echo "KALDI: sahte panel " . $port . " portunda 10 saniyede ayaga kalkmadi\n";
    exit(1);
}

function ada_sunucu_durdur()
{
    global $sunucu, $boru;

    if (is_resource($sunucu)) {
        foreach ($boru as $b) {
            if (is_resource($b)) {
                fclose($b);
            }
        }
        proc_terminate($sunucu);
        proc_close($sunucu);
    }
}

/** Sahte panelin bir sonraki istekleri icin yanit tarifi yazar ve istek gunlugunu sifirlar. */
function ada_senaryo(array $senaryo)
{
    $dizin = ada_fake_dizin();
    file_put_contents($dizin . DIRECTORY_SEPARATOR . 'scenario.json', json_encode($senaryo));
    @unlink($dizin . DIRECTORY_SEPARATOR . 'requests.jsonl');

    $GLOBALS['ada_test']['localApi'] = array();
    $GLOBALS['ada_test']['moduleLog'] = array();
    $GLOBALS['ada_test']['capsule'] = array();
}

/** Sahte panelin GORDUGU istekler. */
function ada_istekler()
{
    $dosya = ada_fake_dizin() . DIRECTORY_SEPARATOR . 'requests.jsonl';
    if (!is_file($dosya)) {
        return array();
    }

    $sonuc = array();
    foreach (explode("\n", (string) file_get_contents($dosya)) as $satir) {
        $satir = trim($satir);
        if ($satir === '') {
            continue;
        }
        $cozulmus = json_decode($satir, true);
        if (is_array($cozulmus)) {
            $sonuc[] = $cozulmus;
        }
    }

    return $sonuc;
}

/** Kaydedilen istekler icinde "METOT /yol" arar; bulursa istegi, bulamazsa null doner. */
function ada_istek($metot, $yolParcasi)
{
    foreach (ada_istekler() as $istek) {
        if ($istek['method'] === $metot && strpos($istek['path'], $yolParcasi) !== false) {
            return $istek;
        }
    }

    return null;
}

function ada_govde($istek)
{
    if ($istek === null) {
        return array();
    }

    $cozulmus = json_decode((string) $istek['body'], true);

    return is_array($cozulmus) ? $cozulmus : array();
}

/** WHMCS'in modul islevlerine verdigi parametreler (bu testte gereken kismi). */
function ada_params(array $ustune = array())
{
    global $port;

    return array_merge(array(
        'serverhostname' => '127.0.0.1',
        'serverip' => '127.0.0.1',
        'serverport' => $port,
        'serversecure' => false,
        'serverusername' => 'paneladmin',
        'serverpassword' => 'panel-parolasi',
        'serveraccesshash' => 'entry=gizliyol;admin=whmcsadmin',
        'serverid' => 3,
        'username' => 'musteri1',
        'password' => 'cokuzunparola12',
        'domain' => 'ornek.com',
        'userid' => 9,
        'serviceid' => 42,
        'clientsdetails' => array('email' => 'musteri@ornek.com', 'language' => 'turkish'),
        'configoption1' => '',
        'configoption2' => '3',
        'configoption3' => '2048',
        'configoption4' => '2',
        'configoption5' => '10',
        'configoption6' => '',
    ), $ustune);
}

/** Panelin `ApiResponse<T>` zarfi (camelCase; ApiExceptionHandlingMiddleware hata yolunda da ayni). */
function ada_zarf($data = null, $success = true, $message = '')
{
    return array('success' => $success, 'data' => $data, 'message' => $message, 'code' => 0, 'details' => '');
}

/** Modulun her cagriida once gectigi giris/CSRF/kullanici-arama uclari. */
function ada_temel_senaryo(array $ustune = array())
{
    $kullanici = array(
        'id' => '11111111-1111-1111-1111-111111111111',
        'userName' => 'musteri1',
        'email' => 'musteri@ornek.com',
        'role' => 'Customer',
        'ownerId' => null,
        'preferredLanguage' => 'tr',
        'isActive' => true,
        'assignedSites' => array('ornek.com'),
        'maxSites' => 1,
        'maxDiskMb' => 1024,
        'maxDatabases' => 1,
        'maxMailboxes' => 5,
        'maxScheduledTasks' => 0,
    );

    return array_merge(array(
        'GET /api/v1/auth/csrf' => array('status' => 200, 'body' => ada_zarf(array('requestToken' => 'csrf-belirteci'))),
        'POST /api/v1/auth/login' => array('status' => 200, 'body' => ada_zarf(array('isAuthenticated' => true, 'requiresTwoFactor' => false))),
        'GET /api/v1/users' => array('status' => 200, 'body' => ada_zarf(array($kullanici))),
    ), $ustune);
}

ada_sunucu_baslat($port);

register_shutdown_function('ada_sunucu_durdur');

echo "0) Modul WHMCS sozlesmesindeki islevleri GERCEKTEN tanimliyor\n";
$beklenen = array(
    'adapanel_MetaData', 'adapanel_ConfigOptions', 'adapanel_CreateAccount', 'adapanel_SuspendAccount',
    'adapanel_UnsuspendAccount', 'adapanel_TerminateAccount', 'adapanel_ChangePackage',
    'adapanel_ChangePassword', 'adapanel_ServiceSingleSignOn', 'adapanel_TestConnection',
    'adapanel_AdminServicesTabFields', 'adapanel_UsageUpdate',
);
foreach ($beklenen as $islev) {
    ada_assert(function_exists($islev), $islev . ' tanimli');
}
// ConfigOptions ile adapanel_option()'daki configoptionN sirasi AYNI olmali; ayrisirsa
// indeksli erisim SESSIZCE yanlis alani okur (ornegin disk kotasi yerine site sayisi).
$etiketler = array_keys(adapanel_ConfigOptions());
ada_assert(count($etiketler) === 6, 'ConfigOptions 6 alan tanimliyor');
foreach ($etiketler as $indeks => $etiket) {
    $p = ada_params(array('configoption' . ($indeks + 1) => 'ISARET' . $indeks));
    ada_assert(adapanel_option($p, $etiket) === 'ISARET' . $indeks, 'sira dogru: "' . $etiket . '" -> configoption' . ($indeks + 1));
}

echo "1) CreateAccount: yol, metot, basliklar ve ALAN ADLARI\n";
ada_senaryo(ada_temel_senaryo(array(
    'POST /api/v1/users' => array('status' => 200, 'body' => ada_zarf(array('id' => '22222222-2222-2222-2222-222222222222'))),
)));
$sonuc = adapanel_CreateAccount(ada_params());
ada_assert($sonuc === 'success', 'CreateAccount "success" dondu (donen: ' . var_export($sonuc, true) . ')');

$olustur = ada_istek('POST', '/api/v1/users');
ada_assert($olustur !== null, 'POST /api/v1/users gercekten cagrildi');
$govde = ada_govde($olustur);
foreach (array('userName', 'email', 'password', 'role', 'preferredLanguage', 'assignedSites',
             'maxSites', 'maxDiskMb', 'maxDatabases', 'maxMailboxes', 'maxScheduledTasks') as $alan) {
    ada_assert(array_key_exists($alan, $govde), 'CreateUserRequest alani gonderildi: ' . $alan);
}
ada_assert(($govde['role'] ?? '') === 'Customer', 'rol Customer');
ada_assert(($govde['preferredLanguage'] ?? '') === 'tr', 'WHMCS dili "turkish" -> panel "tr"');
// BIRIM SOZLESMESI KILITLENIYOR. Panelin tabani IKILIKTIR (PanelUnits: 1 GiB = 1024 MiB) ve
// tel uzerindeki ad "Mb" olsa da deger MiB'dir; `BillingUnits` de WHMCS'e ayni tabanla yazar.
// PHP tarafinin dogru davranisi CEVIRMEMEKTIR: urun ayarindaki sayi oldugu gibi gider. Buraya bir
// gun /1000 ya da *1024 sizarsa -- iki taraf ayrisir ve musteriye YANLIS kota/fatura gider --
// bu denetim kirmizi yanar.
ada_assert(($govde['maxDiskMb'] ?? null) === 2048, 'disk kotasi urun ayarindan MB olarak AYNEN gecti (1024 tabani, cevrim YOK)');
ada_assert(($govde['assignedSites'] ?? array()) === array('ornek.com'), 'alan adi kapsama eklendi');

// GIZLI GIRIS YOLU BASLIGI: yoksa panel 404 doner ve modul "uc yok" gibi olur.
ada_assert(($olustur['headers']['x-adapanel-entry-path'] ?? '') === 'gizliyol', 'X-AdaPanel-Entry-Path basligi Access Hash icinden geldi');
ada_assert(($olustur['headers']['x-csrf-token'] ?? '') === 'csrf-belirteci', 'X-CSRF-TOKEN basligi var (cerez yolunda GET disi istek sart)');
ada_assert(strpos((string) ($olustur['headers']['content-type'] ?? ''), 'application/json') === 0, 'Content-Type application/json');

// CSRF belirteci GIRISTEN SONRA yenilenmeli: panel belirteci oturum kimligine baglar.
$sira = array();
foreach (ada_istekler() as $istek) {
    $sira[] = $istek['method'] . ' ' . $istek['path'];
}
$girisIndeksi = array_search('POST /api/v1/auth/login', $sira, true);
$sonCsrf = 0;
foreach ($sira as $i => $ad) {
    if ($ad === 'GET /api/v1/auth/csrf') {
        $sonCsrf = $i;
    }
}
ada_assert($girisIndeksi !== false && $sonCsrf > $girisIndeksi, 'CSRF belirteci girisTEN SONRA yenilendi');

echo "2) Kisa parola: panele HIC gidilmez (WHMCS parolasi ile panel parolasi ayrismasin)\n";
ada_senaryo(ada_temel_senaryo());
$sonuc = adapanel_CreateAccount(ada_params(array('password' => 'kisa')));
ada_assert($sonuc !== 'success', 'kisa parolada CreateAccount basarili SAYILMIYOR');
ada_assert(count(ada_istekler()) === 0, 'kisa parolada panele hicbir istek gitmedi');

echo "3) Suspend / Unsuspend dogru abonelik uclarina gidiyor\n";
ada_senaryo(ada_temel_senaryo(array(
    'POST /api/v1/subscriptions/*' => array('status' => 200, 'body' => ada_zarf(array('suspended' => true))),
)));
ada_assert(adapanel_SuspendAccount(ada_params()) === 'success', 'SuspendAccount success');
$askiya = ada_istek('POST', '/suspend');
ada_assert($askiya !== null && $askiya['path'] === '/api/v1/subscriptions/11111111-1111-1111-1111-111111111111/suspend', 'POST /api/v1/subscriptions/{id}/suspend');

ada_senaryo(ada_temel_senaryo(array(
    'POST /api/v1/subscriptions/*' => array('status' => 200, 'body' => ada_zarf(array('suspended' => false))),
)));
ada_assert(adapanel_UnsuspendAccount(ada_params()) === 'success', 'UnsuspendAccount success');
ada_assert(ada_istek('POST', '/resume') !== null, 'POST /api/v1/subscriptions/{id}/resume');

echo "4) TerminateAccount ONCE panele soruyor, SONRA siliyor\n";
$karar = array(
    'safeToTerminate' => true,
    'reason' => '',
    'userId' => '11111111-1111-1111-1111-111111111111',
    'userName' => 'musteri1',
);
ada_senaryo(ada_temel_senaryo(array(
    'GET /api/v1/billing/terminate-check' => array('status' => 200, 'body' => ada_zarf($karar)),
    'DELETE /api/v1/users/*' => array('status' => 200, 'body' => ada_zarf(array(
        'userId' => '11111111-1111-1111-1111-111111111111',
        'userName' => 'musteri1',
        'userDeleted' => true,
        'siteRemovalRequested' => false,
        'deletedSites' => array(),
        'leftovers' => array(array(
            'siteName' => 'ornek.com', 'kind' => 'site', 'name' => 'ornek.com',
            'reason' => 'removeSites=false', 'deliberate' => true,
        )),
        'completed' => true,
    ))),
)));
ada_assert(adapanel_TerminateAccount(ada_params()) === 'success', 'TerminateAccount success');
$denetim = ada_istek('GET', '/api/v1/billing/terminate-check');
ada_assert($denetim !== null, 'silmeden ONCE terminate-check cagrildi');
ada_assert(strpos((string) $denetim['query'], 'userName=musteri1') !== false, 'terminate-check userName sorgu alani');
ada_assert(strpos((string) $denetim['query'], 'domain=ornek.com') !== false, 'terminate-check domain sorgu alani');
$sil = ada_istek('DELETE', '/api/v1/users/');
ada_assert($sil !== null, 'DELETE /api/v1/users/{id} cagrildi');
ada_assert($sil !== null && strpos($sil['path'], '11111111-1111-1111-1111-111111111111') !== false, 'silinen kimlik PANELIN dondurdugu kimlik (ikinci arama yok)');
ada_assert($sil !== null && $sil['query'] === 'removeSites=false', 'varsayilan removeSites=false (kaza ile veri kaybi olmasin)');
ada_assert(count($GLOBALS['ada_test']['localApi']) === 1, 'kalinti notu WHMCS tarafina yazildi');

echo "5) Denetim GECILEMEZSE silme YAPILMAZ ve WHMCS'e success DONMEZ\n";
ada_senaryo(ada_temel_senaryo(array(
    'GET /api/v1/billing/terminate-check' => array('status' => 200, 'body' => ada_zarf(array(
        'safeToTerminate' => false, 'reason' => 'domain_mismatch',
    ))),
)));
$sonuc = adapanel_TerminateAccount(ada_params());
ada_assert($sonuc !== 'success', 'guvenli degilse success DONMUYOR');
ada_assert(strpos($sonuc, 'ornek.com') !== false, 'hata mesaji yoneticiye alan adini soyluyor');
ada_assert(ada_istek('DELETE', '/api/v1/users/') === null, 'HICBIR silme istegi gitmedi');

echo "6) ChangePackage: plan bagi varken tekil limit YAZILMAZ\n";
ada_senaryo(ada_temel_senaryo(array(
    'PUT /api/v1/subscriptions/*' => array('status' => 200, 'body' => ada_zarf(array('planId' => 'plan-1'))),
    'PUT /api/v1/users/*' => array('status' => 200, 'body' => ada_zarf(null)),
)));
ada_assert(adapanel_ChangePackage(ada_params(array('configoption1' => 'plan-1'))) === 'success', 'plan bagli ChangePackage success');
$plan = ada_istek('PUT', '/plan');
ada_assert($plan !== null && (ada_govde($plan)['planId'] ?? '') === 'plan-1', 'PUT {id}/plan govdesi {planId}');
ada_assert(ada_istek('PUT', '/api/v1/users/') === null, 'plan bagliyken tekil limitler YAZILMADI');

echo "7) ChangePackage: plan bagi YOKKA once bag kaldirilir, sonra TUM alanlar yazilir\n";
ada_senaryo(ada_temel_senaryo(array(
    'PUT /api/v1/subscriptions/*' => array('status' => 200, 'body' => ada_zarf(null)),
    'PUT /api/v1/users/*' => array('status' => 200, 'body' => ada_zarf(null)),
)));
ada_assert(adapanel_ChangePackage(ada_params()) === 'success', 'plansiz ChangePackage success');
$plan = ada_istek('PUT', '/plan');
ada_assert($plan !== null && array_key_exists('planId', ada_govde($plan)) && ada_govde($plan)['planId'] === null, 'bag kaldirmak icin planId=null gonderildi');
$guncelle = ada_istek('PUT', '/api/v1/users/');
$govde = ada_govde($guncelle);
foreach (array('email', 'role', 'ownerId', 'preferredLanguage', 'isActive', 'assignedSites',
             'maxSites', 'maxDiskMb', 'maxDatabases', 'maxMailboxes', 'maxScheduledTasks') as $alan) {
    ada_assert(array_key_exists($alan, $govde), 'UpdateUserRequest alani gonderildi: ' . $alan);
}
ada_assert(($govde['maxDiskMb'] ?? null) === 2048, 'yeni disk kotasi urun ayarindan geldi');
ada_assert(($govde['email'] ?? '') === 'musteri@ornek.com', 'degismeyen alanlar paneldeki degerle korundu');

echo "8) ChangePassword ve SSO\n";
ada_senaryo(ada_temel_senaryo(array(
    'POST /api/v1/auth/password/set/*' => array('status' => 200, 'body' => ada_zarf(array('revokedTokens' => 2))),
)));
ada_assert(adapanel_ChangePassword(ada_params()) === 'success', 'ChangePassword success');
$parola = ada_istek('POST', '/api/v1/auth/password/set/');
ada_assert($parola !== null && (ada_govde($parola)['newPassword'] ?? '') === 'cokuzunparola12', 'govde alani {newPassword}');

ada_senaryo(ada_temel_senaryo());
$sonuc = adapanel_ChangePassword(ada_params(array('password' => 'kisa')));
ada_assert($sonuc !== 'success', 'kisa parolada ChangePassword basarisiz');
ada_assert(count(ada_istekler()) === 0, 'kisa parolada panele istek gitmedi');

ada_senaryo(ada_temel_senaryo(array(
    'POST /api/v1/auth/sso-link/*' => array('status' => 200, 'body' => ada_zarf(array(
        'url' => 'https://panel.ornek.com/gizliyol/sso?t=abc', 'expiresAtUtc' => '2026-01-01T00:00:00Z',
    ))),
)));
$sso = adapanel_ServiceSingleSignOn(ada_params());
ada_assert(is_array($sso) && !empty($sso['success']), 'SSO success:true dondu');
ada_assert(($sso['redirectTo'] ?? '') === 'https://panel.ornek.com/gizliyol/sso?t=abc', 'redirectTo panelin urettigi adres');

ada_senaryo(ada_temel_senaryo(array(
    'POST /api/v1/auth/sso-link/*' => array('status' => 200, 'body' => ada_zarf(array('url' => ''))),
)));
$sso = adapanel_ServiceSingleSignOn(ada_params());
ada_assert(is_array($sso) && empty($sso['success']) && !empty($sso['errorMsg']), 'bos adreste SSO basarisiz sayiliyor');

echo "9) TestConnection rol/plan uclarini VE FATURALAMA KAPISINI olcuyor\n";
$saglam = array(
    'GET /api/v1/service-plans' => array('status' => 200, 'body' => ada_zarf(array())),
    'GET /api/v1/billing/settings' => array('status' => 200, 'body' => ada_zarf(array('enabled' => true))),
);
ada_senaryo(ada_temel_senaryo($saglam));
$test = adapanel_TestConnection(ada_params());
ada_assert(!empty($test['success']), 'TestConnection basarili');
ada_assert(ada_istek('GET', '/api/v1/users') !== null, '/api/v1/users denendi (yalniz giris yetmez)');
ada_assert(ada_istek('GET', '/api/v1/service-plans') !== null, '/api/v1/service-plans denendi (plan bagi yolu)');
ada_assert(ada_istek('GET', '/api/v1/billing/settings') !== null, '/api/v1/billing/settings denendi (YONETICI kapisi)');

// BAYI hesabi: users ve service-plans GECER (ikisi de Administrator,Reseller), yalniz faturalama
// 403 verir. Sinama eskiden bunu HIC gormuyordu; TerminateAccount ve UsageUpdate olu kalirdi.
ada_senaryo(ada_temel_senaryo(array(
    'GET /api/v1/service-plans' => array('status' => 200, 'body' => ada_zarf(array())),
    'GET /api/v1/billing/settings' => array('status' => 403, 'body' => ada_zarf(null, false, 'auth.forbidden')),
)));
$test = adapanel_TestConnection(ada_params());
ada_assert(empty($test['success']), 'bayi hesabiyla kurulan sunucu tanimi BASARILI sayilmiyor');
ada_assert(strpos((string) $test['error'], 'YONETICI') !== false, 'mesaj gereken ROLU soyluyor');

// Faturalama anahtari KAPALI: uc var, rol dogru, ama iptal ve kullanim yine calismaz.
ada_senaryo(ada_temel_senaryo(array(
    'GET /api/v1/service-plans' => array('status' => 200, 'body' => ada_zarf(array())),
    'GET /api/v1/billing/settings' => array('status' => 200, 'body' => ada_zarf(array('enabled' => false))),
)));
$test = adapanel_TestConnection(ada_params());
ada_assert(empty($test['success']), 'faturalama KAPALIYKEN sunucu tanimi BASARILI sayilmiyor');
ada_assert(strpos((string) $test['error'], 'KAPALI') !== false, 'mesaj panelde neyin acilacagini soyluyor');
// Iki ariza AYRI mesajlar: yonetici hangisini duzeltecegini bilmeli.
ada_assert(strpos((string) $test['error'], 'YONETICI') === false, 'kapali anahtar mesaji rol mesajiyla karismiyor');

ada_senaryo(ada_temel_senaryo(array(
    'GET /api/v1/service-plans' => array('status' => 403, 'body' => ada_zarf(null, false, 'auth.forbidden')),
)));
$test = adapanel_TestConnection(ada_params());
ada_assert(empty($test['success']), '403 alan sunucu tanimi BASARILI sayilmiyor');
ada_assert(strpos((string) $test['error'], 'yetkisi yok') !== false, 'ceviri anahtari okunur cumleye cevrildi');

echo "10) API anahtari yolu: giris YAPILMAZ, Authorization basligi gider\n";
ada_senaryo(ada_temel_senaryo($saglam));
$test = adapanel_TestConnection(ada_params(array('serveraccesshash' => 'entry=gizliyol;token=adap_deneme')));
ada_assert(!empty($test['success']), 'anahtarla TestConnection basarili');
ada_assert(ada_istek('POST', '/api/v1/auth/login') === null, 'anahtar varken giris ucu HIC cagrilmadi (5/dk limitine girilmez)');
ada_assert(ada_istek('GET', '/api/v1/auth/csrf') === null, 'anahtar varken CSRF ucu HIC cagrilmadi');
$kullanicilar = ada_istek('GET', '/api/v1/users');
ada_assert($kullanicilar !== null && ($kullanicilar['headers']['authorization'] ?? '') === 'Bearer adap_deneme', 'Authorization: Bearer adap_... basligi gonderildi');

echo "11) UsageUpdate tblhosting'e YAZIYOR; OLCULEMEYEN deger yazilmiyor\n";
ada_senaryo(ada_temel_senaryo(array(
    'GET /api/v1/billing/usage' => array('status' => 200, 'body' => ada_zarf(array('accounts' => array(
        array(
            'userName' => 'musteri1', 'diskUsageMb' => 512, 'diskLimitMb' => 2048,
            'bandwidthUsageMb' => 100, 'bandwidthLimitMb' => 10240,
            'diskMeasured' => true, 'bandwidthMeasured' => true,
        ),
        array(
            'userName' => 'musteri2', 'diskUsageMb' => 0, 'diskLimitMb' => 1024,
            'bandwidthUsageMb' => 0, 'bandwidthLimitMb' => 5120,
            'diskMeasured' => false, 'bandwidthMeasured' => false,
        ),
        array('userName' => '', 'diskUsageMb' => 9),
    )))),
)));
$sonuc = adapanel_UsageUpdate(ada_params());
ada_assert(!empty($sonuc['success']), 'UsageUpdate basarili');
$yazmalar = $GLOBALS['ada_test']['capsule'];
ada_assert(count($yazmalar) === 2, 'adi bos olan satir atlandi, iki hesap yazildi');
ada_assert(($yazmalar[0]['table'] ?? '') === 'tblhosting', 'tblhosting tablosuna yazildi');
ada_assert(($yazmalar[0]['where']['server'] ?? null) === 3, 'satir SUNUCU kimligiyle daraltildi');
ada_assert(($yazmalar[0]['where']['username'] ?? '') === 'musteri1', 'eslestirme anahtari username (domain DEGIL)');
ada_assert(($yazmalar[0]['values']['diskusage'] ?? null) === 512, 'olculmus disk yazildi');
ada_assert(($yazmalar[0]['values']['bwusage'] ?? null) === 100, 'olculmus trafik yazildi');
ada_assert(($yazmalar[0]['values']['disklimit'] ?? null) === 2048, 'disk limiti yazildi');
ada_assert(($yazmalar[0]['values']['bwlimit'] ?? null) === 10240, 'trafik limiti yazildi');
ada_assert(!array_key_exists('diskusage', $yazmalar[1]['values']), 'OLCULEMEYEN disk YAZILMADI (eski deger korunur)');
ada_assert(!array_key_exists('bwusage', $yazmalar[1]['values']), 'OLCULEMEYEN trafik YAZILMADI');
ada_assert(array_key_exists('disklimit', $yazmalar[1]['values']), 'limit olcume bagli degil, yine de yazildi');

echo "12) UsageUpdate: tek satirin patlamasi TURU DURDURMUYOR\n";
ada_senaryo(ada_temel_senaryo(array(
    'GET /api/v1/billing/usage' => array('status' => 200, 'body' => ada_zarf(array('accounts' => array(
        array('userName' => 'musteri1', 'diskMeasured' => true, 'diskUsageMb' => 5),
    )))),
)));
$GLOBALS['ada_test']['capsuleFirlat'] = true;
$sonuc = adapanel_UsageUpdate(ada_params());
$GLOBALS['ada_test']['capsuleFirlat'] = false;
ada_assert(is_array($sonuc), 'veritabani patlasa da islev cokmedi');
ada_assert(count($GLOBALS['ada_test']['moduleLog']) >= 1, 'yazilamayan satir modul gunlugune dustu');

echo "13) HATA YOLLARI: modul sessizce 'basarili' DEMIYOR\n";

// (a) panel 500 + JSON zarf
ada_senaryo(ada_temel_senaryo(array(
    'POST /api/v1/users' => array('status' => 500, 'body' => ada_zarf(null, false, 'server.error')),
)));
ada_assert(adapanel_CreateAccount(ada_params()) !== 'success', '500 yaniti basarili sayilmiyor');

// (b) JSON OLMAYAN govde (yanlis port/gizli yol tipik belirtisi)
ada_senaryo(ada_temel_senaryo(array(
    'POST /api/v1/users' => array('status' => 200, 'raw' => '<html>IIS 404</html>'),
)));
$sonuc = adapanel_CreateAccount(ada_params());
ada_assert($sonuc !== 'success', 'JSON olmayan govde basarili sayilmiyor');
ada_assert(strpos($sonuc, 'JSON olmayan') !== false, 'mesaj sebebi soyluyor');

// (c) bozuk JSON
ada_senaryo(ada_temel_senaryo(array(
    'POST /api/v1/users' => array('status' => 200, 'raw' => '{"success":true,"data":'),
)));
ada_assert(adapanel_CreateAccount(ada_params()) !== 'success', 'yarim JSON basarili sayilmiyor');

// (d) success:true ama kimlik YOK
ada_senaryo(ada_temel_senaryo(array(
    'POST /api/v1/users' => array('status' => 200, 'body' => ada_zarf(array('userName' => 'musteri1'))),
)));
ada_assert(adapanel_CreateAccount(ada_params()) !== 'success', 'kimliksiz yanit basarili sayilmiyor');

// (e) AG KOPUK: kapali porta gidilir
ada_senaryo(ada_temel_senaryo());
$kapali = ada_params(array('serverport' => 1));
$sonuc = adapanel_CreateAccount($kapali);
ada_assert($sonuc !== 'success', 'baglanti kurulamayinca basarili sayilmiyor');
ada_assert(strpos($sonuc, 'baglanilamadi') !== false, 'mesaj baglanti hatasini soyluyor');
$sonuc = adapanel_TerminateAccount($kapali);
ada_assert($sonuc !== 'success', 'ag kopukken TerminateAccount da basarili sayilmiyor (hizmet olu kapatilmaz)');
$sonuc = adapanel_UsageUpdate($kapali);
ada_assert(empty($sonuc['success']), 'ag kopukken UsageUpdate basarisiz doner');
ada_assert(count($GLOBALS['ada_test']['capsule']) === 0, 'ag kopukken tblhosting tablosuna HICBIR sey yazilmadi');

// (f) panelde kullanici YOK
ada_senaryo(ada_temel_senaryo(array(
    'GET /api/v1/users' => array('status' => 200, 'body' => ada_zarf(array())),
)));
$sonuc = adapanel_SuspendAccount(ada_params());
ada_assert($sonuc !== 'success', 'kullanici bulunamayinca aski basarili sayilmiyor');
ada_assert(ada_istek('POST', '/suspend') === null, 'bulunamayan kullanici icin aski istegi gitmedi');

echo "14) AdminServicesTabFields salt-okur ve hata yutmuyor\n";
ada_senaryo(ada_temel_senaryo(array(
    'GET /api/v1/subscriptions/*' => array('status' => 200, 'body' => ada_zarf(array(
        'status' => 'Active', 'diskUsageMb' => 512, 'planName' => 'Baslangic',
    ))),
)));
$alanlar = adapanel_AdminServicesTabFields(ada_params());
ada_assert(is_array($alanlar) && isset($alanlar['Panel kullanicisi']), 'sekme alanlari dondu');
ada_assert(strpos((string) $alanlar['Abonelik kullanimi'], 'diskUsageMb') !== false, 'abonelik degerleri gosteriliyor');
// Giris (POST /auth/login) kimlik dogrulamadir, YAZMA degil; salt-okurluk kimlik uclarinin
// DISINDA olculur. Aksi halde denetim, cerez yolunun kacinilmaz giris cagrisina takilirdi.
$yazan = array();
foreach (ada_istekler() as $istek) {
    if ($istek['method'] !== 'GET' && strpos($istek['path'], '/api/v1/auth/') !== 0) {
        $yazan[] = $istek['method'] . ' ' . $istek['path'];
    }
}
ada_assert($yazan === array(), 'sekme kimlik uclari disinda YALNIZCA GET yapti (salt-okur): ' . implode(', ', $yazan));

echo "15) CEREZ SEMASINA SABITLI UCLAR anahtar yapilandirilmisken de CALISIYOR\n";
// `AuthController` sinif duzeyinde cerez semasina sabitli: anahtarli istek 401 alir. Modul bunu
// biliyordu (dosya bas yorumu) ama UYMUYORDU; parola belirleme ve tek tikla giris, anahtar
// yapilandirilmis her kurulumda SESSIZCE oluydu.
$anahtarli = ada_params(array('serveraccesshash' => 'entry=gizliyol;token=adap_deneme'));

ada_senaryo(ada_temel_senaryo(array(
    'POST /api/v1/auth/password/set/*' => array('status' => 200, 'body' => ada_zarf(array('revokedApiTokens' => 2))),
)));
ada_assert(adapanel_ChangePassword($anahtarli) === 'success', 'anahtar yapilandirilmisken ChangePassword calisti');
ada_assert(ada_istek('POST', '/api/v1/auth/login') !== null, 'auth ucu icin CEREZLI oturum acildi');
$parola = ada_istek('POST', '/api/v1/auth/password/set/');
ada_assert($parola !== null && !isset($parola['headers']['authorization']), 'auth ucune Authorization basligi GONDERILMEDI (yoksa panel anahtar yoluna dusup 401 verir)');
$giris = ada_istek('POST', '/api/v1/auth/login');
ada_assert($giris !== null && !isset($giris['headers']['authorization']), 'giris el sikismasi da anahtarsiz yapildi');
// Anahtar diger uclarda KULLANILMAYA DEVAM EDER; cerez yoluna komple dusmek, dakikada 5 istekle
// sinirli login politikasina her cagride girmek olurdu.
$kullanicilar = ada_istek('GET', '/api/v1/users');
ada_assert($kullanicilar !== null && ($kullanicilar['headers']['authorization'] ?? '') === 'Bearer adap_deneme', 'auth DISINDAKI uc anahtarla cagrildi');

ada_senaryo(ada_temel_senaryo(array(
    'POST /api/v1/auth/sso-link/*' => array('status' => 200, 'body' => ada_zarf(array('url' => 'https://p/x', 'expiresAtUtc' => '2026-01-01T00:00:00Z'))),
)));
$sso = adapanel_ServiceSingleSignOn($anahtarli);
ada_assert(!empty($sso['success']), 'anahtar yapilandirilmisken SSO calisti');
$baglanti = ada_istek('POST', '/api/v1/auth/sso-link/');
ada_assert($baglanti !== null && !isset($baglanti['headers']['authorization']), 'sso-link ucune de Authorization gitmedi');

// Yalniz anahtar var, kullanici adi/parola YOK: modul uyduramaz, ama sebebi SOYLEMELI.
ada_senaryo(ada_temel_senaryo());
$sonuc = adapanel_ChangePassword(ada_params(array(
    'serveraccesshash' => 'entry=gizliyol;token=adap_deneme',
    'serverusername' => '',
    'serverpassword' => '',
)));
ada_assert($sonuc !== 'success', 'parolasiz+anahtarli tanimda ChangePassword basarili SAYILMIYOR');
ada_assert(strpos($sonuc, 'kullanici adi/parolasi bos') !== false, 'mesaj eksik olan ayari soyluyor');

echo "\n";
if ($basarisiz > 0) {
    echo "SONUC: $basarisiz denetim KALDI\n";
    exit(1);
}

echo "SONUC: hepsi gecti\n";
exit(0);
