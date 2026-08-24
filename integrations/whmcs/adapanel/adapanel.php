<?php

/**
 * AdaPanel -- WHMCS sunucu (provisioning) modulu.
 *
 * Kurulum: bu klasoru `<whmcs>/modules/servers/adapanel/` altina kopyalayin.
 * Ayrinti icin `integrations/whmcs/README.md`.
 *
 * HER UC PANELIN C# DENETLEYICILERI OKUNARAK DOGRULANDI. Kullanilan uclar:
 *   GET    /api/v1/auth/csrf                      -> AuthController.GetCsrfToken
 *   POST   /api/v1/auth/login                     -> AuthController.LoginAsync
 *   GET    /api/v1/users                          -> UsersController.GetUsersAsync
 *   POST   /api/v1/users                          -> UsersController.CreateUserAsync
 *   PUT    /api/v1/users/{id}                     -> UsersController.UpdateUserAsync
 *   DELETE /api/v1/users/{id}?removeSites=        -> UsersController.DeleteUserAsync
 *   GET    /api/v1/subscriptions/{id}             -> SubscriptionsController.GetAsync
 *   PUT    /api/v1/subscriptions/{id}/plan        -> SubscriptionsController.SetPlanAsync
 *   POST   /api/v1/subscriptions/{id}/suspend     -> SubscriptionsController.SuspendAsync
 *   POST   /api/v1/subscriptions/{id}/resume      -> SubscriptionsController.ResumeAsync
 *   GET    /api/v1/service-plans                  -> ServicePlansController.GetAsync
 *   POST   /api/v1/auth/password/set/{id}         -> AuthController.SetUserPasswordAsync
 *   POST   /api/v1/auth/sso-link/{id}             -> AuthController.CreateSsoLinkAsync
 *   GET    /api/v1/billing/usage                  -> BillingController.GetUsageAsync
 *   GET    /api/v1/billing/terminate-check        -> BillingController.CheckTerminationAsync
 *
 * ChangePassword ve ServiceSingleSignOn islevleri bir sure "uc yok" hatasi donuyordu; panele
 * karsiliklari eklendi (bkz. docs/whmcs-modulu.md) ve artik gercekten calisiyorlar.
 *
 * UsageUpdate (disk/trafik kullanimi) ve TerminateAccount'un silme oncesi DOGRULAMASI icin
 * panele `BillingController` eklendi; ikisi de panelde faturalama entegrasyonunun ACIK olmasini
 * ister (Panel -> Faturalama).
 *
 * KULLANILAN WHMCS SOZLESMELERI (hepsi developers.whmcs.com'dan dogrulandi, hafizadan yazilmadi):
 *   localAPI($command, $values, $adminuser)      -> /api/internal-api/
 *   AddClientNote {userid, notes, sticky}        -> /api-reference/addclientnote/
 *   $params['userid'] / ['serviceid']            -> /provisioning-modules/module-parameters/
 *   logModuleCall($module,$action,$req,$res,$processed,$replaceVars)
 *                                                -> /provisioning-modules/module-logging/
 *
 * PHP tarafinin DAVRANIS testi: `php integrations/whmcs/tests/leftover-note.test.php`.
 * Bu depoda gercek bir WHMCS kurulumu YOK; modul uctan uca hic calistirilmadi.
 */

if (!defined('WHMCS')) {
    die('This file cannot be accessed directly');
}

require_once __DIR__ . '/lib/AdaPanelClient.php';

/**
 * Modulun WHMCS'teki kimlik karti.
 */
function adapanel_MetaData()
{
    return array(
        'DisplayName' => 'AdaPanel',
        'APIVersion' => '1.1',
        'RequiresServer' => true,
        // Panel kullanici adini WHMCS uretir ve hizmet omru boyunca degismez; modul kullaniciyi
        // bu adla bulur (GUID saklamaya gerek kalmaz).
        'DefaultNonSSLPort' => '8888',
        'DefaultSSLPort' => '8888',
        'ServiceSingleSignOnLabel' => 'Panele giris',
    );
}

/**
 * Urun (paket) basina yapilandirma alanlari.
 *
 * NOT: "Servis plani kimligi" doldurulursa abonelik panelde PLANA BAGLANIR ve asagidaki tekil
 * limitler YOK SAYILIR -- panelde plan bir sablon degil kalici bagdir (ServicePlanDto belgesi):
 * plani degistirmek bagli tum abonelikleri ayni anda degistirir. Plan kimligini
 * `GET /api/v1/service-plans` ile ogrenebilirsiniz (README'de nasil bakilacagi yazili).
 */
function adapanel_ConfigOptions()
{
    return array(
        'Servis plani kimligi' => array(
            'Type' => 'text',
            'Size' => '32',
            'Description' => 'Panel servis planinin kimligi. Doldurulursa asagidaki limitler yok sayilir.',
        ),
        'En fazla site' => array(
            'Type' => 'text',
            'Size' => '6',
            'Default' => '1',
            'Description' => '0 = sinirsiz',
        ),
        'Disk kotasi (MB)' => array(
            'Type' => 'text',
            'Size' => '8',
            'Default' => '1024',
            'Description' => '0 = sinirsiz',
        ),
        'En fazla veritabani' => array(
            'Type' => 'text',
            'Size' => '6',
            'Default' => '1',
            'Description' => '0 = sinirsiz',
        ),
        'En fazla posta kutusu' => array(
            'Type' => 'text',
            'Size' => '6',
            'Default' => '5',
            'Description' => '0 = sinirsiz',
        ),
        'Iptalde siteleri de sil' => array(
            'Type' => 'yesno',
            'Description' => 'ISARETLENIRSE hizmet iptalinde musterinin siteleri DOSYALARI ve eslesen '
                . 'VERITABANLARIYLA birlikte silinir (geri alinamaz). Isaretsiz birakilirsa yalniz panel '
                . 'hesabi silinir, siteler sunucuda kalir.',
        ),
    );
}

/**
 * Yeni musteri hesabi acar.
 *
 * Panelde bir "hosting hesabi" = Customer rolunde bir kullanici + ona atanmis kapsam (siteler,
 * kotalar). Alan adi, hesap acilirken kapsama EKLENIR: panel site adini ve host adini bir sahiplik
 * iddiasi sayar (SitesController.CreateSiteAsync -> CustomerAccess.EnsureDomainClaimAsync), yani
 * onceden atamak alan adini bu musteri icin REZERVE eder ve musteri siteyi kendi acabilir.
 *
 * Site/IIS kurulumu bilerek YAPILMAZ: `POST /api/v1/sites` yonetici olarak cagrildiginda fiziksel
 * yol zorunludur ve olusan site otomatik olarak musteriye ATANMAZ (AssignSiteToActorAsync yalniz
 * yonetici olmayan istekte calisir). Yani yoneticiyle site acmak, sahipligi ayrica duzeltmeyi
 * gerektiren yarim bir is olurdu.
 */
function adapanel_CreateAccount(array $params)
{
    try {
        $client = new AdaPanelClient($params);

        $password = (string) $params['password'];
        if (strlen($password) < 12) {
            // Panelin kurali: CreateUserRequest.Password -> [StringLength(128, MinimumLength = 12)].
            // Sessizce baska bir parola uretmiyoruz: musteriye e-postayla giden parola ile paneldeki
            // parola farkli olurdu.
            return 'AdaPanel en az 12 karakterlik parola ister; WHMCS bu hizmet icin '
                . strlen($password) . ' karakterlik bir parola uretti. WHMCS parola uzunlugunu '
                . '12 veya uzerine cikarin ve hizmeti yeniden olusturun.';
        }

        $domain = trim((string) $params['domain']);
        $assignedSites = $domain !== '' ? array($domain) : array();

        $request = array(
            'userName' => (string) $params['username'],
            'email' => (string) $params['clientsdetails']['email'],
            'password' => $password,
            'role' => 'Customer',
            'preferredLanguage' => adapanel_language($params),
            'assignedSites' => $assignedSites,
            'maxSites' => adapanel_int($params, 'En fazla site'),
            'maxDiskMb' => adapanel_int($params, 'Disk kotasi (MB)'),
            'maxDatabases' => adapanel_int($params, 'En fazla veritabani'),
            'maxMailboxes' => adapanel_int($params, 'En fazla posta kutusu'),
            'maxScheduledTasks' => 0,
            'sendWelcomeEmail' => false,
        );

        $created = $client->post('/api/v1/users', $request);
        $userId = isset($created['data']['id']) ? (string) $created['data']['id'] : '';

        if ($userId === '') {
            return 'Panel kullaniciyi olusturdu ama kimlik dondurmedi.';
        }

        // Plan bagi: doldurulmussa limitler plandan cozulur (panelde plan kalici bagdir).
        $planId = trim((string) adapanel_option($params, 'Servis plani kimligi'));
        if ($planId !== '') {
            $client->put('/api/v1/subscriptions/' . $userId . '/plan', array('planId' => $planId));
        }

        return 'success';
    } catch (Exception $exception) {
        return $exception->getMessage();
    }
}

/**
 * Hesabi askiya alir.
 *
 * Panelde aski GIRISI KAPATMAZ, SITELERI DURDURUR (ISubscriptionEnforcementService belgesi):
 * musteri panele girip durumunu gorebilsin ve odemesini yapip cozebilsin diye. Bu bilincli bir
 * urun karari; modul onu degistirmeye calismaz.
 */
function adapanel_SuspendAccount(array $params)
{
    try {
        $client = new AdaPanelClient($params);
        $user = $client->requireUserByName((string) $params['username']);
        $client->post('/api/v1/subscriptions/' . $user['id'] . '/suspend');

        return 'success';
    } catch (Exception $exception) {
        return $exception->getMessage();
    }
}

/** Askiyi kaldirir ve siteleri yeniden baslatir. */
function adapanel_UnsuspendAccount(array $params)
{
    try {
        $client = new AdaPanelClient($params);
        $user = $client->requireUserByName((string) $params['username']);
        $client->post('/api/v1/subscriptions/' . $user['id'] . '/resume');

        return 'success';
    } catch (Exception $exception) {
        return $exception->getMessage();
    }
}

/**
 * Hesabi ve -- yapilandirilmissa -- sitelerini siler.
 *
 * `removeSites=true` panelde CASCADE silmedir: atanan siteler dosyalari ve eslesen veritabanlariyla
 * birlikte YOK EDILIR (UsersController.DeleteUserAsync belgesi) ve YALNIZ yonetici cagirabilir.
 * Varsayilan `false` birakildi: WHMCS'te yanlislikla iptal edilen bir hizmet, geri alinamaz bir veri
 * kaybina donusmemeli. Sunucu tanimindaki "Siteleri de sil" secenegi ile acilir.
 *
 * SUNUCUDA KALANLAR ARTIK WHMCS'E YAZILIYOR. Varsayilan yolda siteler BILEREK birakildigi icin
 * panel 200 + success:true doner; "her sey silindi" ANLAMINA GELMEZ. Panel ne kaldigini
 * `data.leftovers` icinde adiyla sayar ve modul bunu musteri notuna yazar. Aksi halde yonetici
 * hizmeti iptal edip sunucuda kalanlari HIC ogrenemezdi (bkz. adapanel_recordLeftovers).
 */
function adapanel_TerminateAccount(array $params)
{
    try {
        $client = new AdaPanelClient($params);

        // SILMEDEN ONCE PANELE SOR. Eskiden hesap yalnizca KULLANICI ADIYLA bulunup dogrudan
        // siliniyordu; ad panelde elle degistirilmis ya da baska bir role ait bir hesapla
        // cakismissa, iptal edilen hizmet BASKA BIRININ verisini yok edebilirdi. Panel simdi
        // kanita bakarak karar veriyor: hesap Customer rolunde mi, hizmetin alan adi gercekten
        // bu hesabin kapsaminda mi, ada uyan tek bir hesap mi var.
        $domain = trim((string) $params['domain']);
        $check = $client->get(
            '/api/v1/billing/terminate-check'
            . '?userName=' . rawurlencode((string) $params['username'])
            . '&domain=' . rawurlencode($domain)
        );

        $decision = isset($check['data']) && is_array($check['data']) ? $check['data'] : array();

        if (empty($decision['safeToTerminate'])) {
            // BILEREK "success" DONMUYORUZ: WHMCS hizmeti iptal edilmis sayar ve kimse bir daha
            // bakmazdi. Hata mesaji yoneticiye neyin dogrulanamadigini soyler.
            return adapanel_terminateReason(
                isset($decision['reason']) ? (string) $decision['reason'] : 'unknown',
                (string) $params['username'],
                $domain
            );
        }

        $userId = isset($decision['userId']) ? (string) $decision['userId'] : '';
        if ($userId === '') {
            return 'Panel silme denetimini gecti ama kullanici kimligi dondurmedi; silme YAPILMADI.';
        }

        // Kimlik PANELIN DONDURDUGU degerdir; modul ayrica arama yapmaz. Ikinci bir arama,
        // denetimin onayladigi hesaptan BASKA birini bulma ihtimali demek olurdu.
        $removeSites = adapanel_bool($params, 'Iptalde siteleri de sil') ? 'true' : 'false';

        // GOVDE HER IKI YOLDA DA OKUNUR.
        //
        // DUZELTILEN ARIZA: modul yalnizca "istek patladi mi" sorusuna bakiyordu. Varsayilan
        // `removeSites=false` yolunda panel 200 + success:true doner (siteler BILEREK birakilmistir)
        // ama yanit govdesinde `data.leftovers` ile sunucuda ne kaldigini ADIYLA sayar. Modul
        // govdeyi okumadigi icin o liste WHMCS tarafinda HIC gorunmuyordu: yonetici hizmeti iptal
        // ediyor, sunucuda kalan siteleri/veritabanlarini ogrenemiyordu. Kalintilar artik musteri
        // notuna yaziliyor.
        try {
            $silme = $client->delete('/api/v1/users/' . rawurlencode($userId) . '?removeSites=' . $removeSites);
        } catch (AdaPanelApiException $exception) {
            // 409 users.delete.incomplete: kullanici kaydi silinmis olabilir ama sunucuda BASARISIZ
            // kalintilar var. Yapisal liste hata zarfinda gelir; not oraya da yazilir, cunku bu yol
            // tam olarak "yarim is" yoludur ve unutulmasi en pahali olan yoldur.
            adapanel_recordLeftovers($params, $exception->response, $decision);

            return $exception->getMessage();
        }

        adapanel_recordLeftovers($params, $silme, $decision);

        return 'success';
    } catch (Exception $exception) {
        return $exception->getMessage();
    }
}

/**
 * Panelin silme yanitindaki KALINTILARI WHMCS'e not olarak yazar.
 *
 * Kalinti yoksa hicbir sey yapmaz: her iptalde not birakmak, notlari gurultuye bogup gercek
 * uyarilari gorunmez kilardi -- denetim kaydinda oldugu gibi, degeri seyrekliginde.
 *
 * @param array $envelope Panelin cozulmus ApiResponse zarfi (basari ya da 409 hata zarfi).
 * @param array $decision `terminate-check` karari (hesap adi/kimligi notta yazili olsun diye).
 */
function adapanel_recordLeftovers(array $params, $envelope, array $decision)
{
    $data = isset($envelope['data']) && is_array($envelope['data']) ? $envelope['data'] : array();
    $leftovers = isset($data['leftovers']) && is_array($data['leftovers']) ? $data['leftovers'] : array();

    if (count($leftovers) === 0) {
        return;
    }

    $not = adapanel_leftoverNote($params, $data, $leftovers, $decision);

    // MODUL GUNLUGU HER ZAMAN yazilir, not yazilabilse bile: not silinebilir ya da yazilamayabilir,
    // gunluk ise kalicidir ve "hizmet iptal edilirken sunucuda ne kaldi" sorusunun ikinci adresidir.
    // Imza: logModuleCall($module, $action, $requestString, $responseData, $processedData, $replaceVars)
    // (developers.whmcs.com/provisioning-modules/module-logging).
    logModuleCall(
        'adapanel',
        'TerminateAccount.leftovers',
        array('username' => (string) $params['username'], 'serviceid' => (int) ($params['serviceid'] ?? 0)),
        $data,
        $not
    );

    adapanel_addClientNote($params, $not);
}

/**
 * Kalinti listesini WHMCS yoneticisinin okuyabilecegi bir nota cevirir.
 *
 * BILEREK BIRAKILANLAR DA yazilir ama "[bilerek]" diye isaretlenir: panelin kendi `details` metni
 * de ayni ayrimi yapiyor. Yonetici neyin ariza, neyin karar oldugunu ayirt edebilmeli -- ikisini
 * ayni torbaya koymak, "siteleri bilerek biraktik" satirini bir hata sanip aramaya yol acardi.
 */
function adapanel_leftoverNote(array $params, array $data, array $leftovers, array $decision)
{
    $satirlar = array();
    foreach ($leftovers as $leftover) {
        if (!is_array($leftover)) {
            continue;
        }

        $site = isset($leftover['siteName']) ? trim((string) $leftover['siteName']) : '';
        $satirlar[] = '- '
            . ($site !== '' ? $site . ' > ' : '')
            . (isset($leftover['kind']) ? (string) $leftover['kind'] : 'other') . ': '
            . (isset($leftover['name']) ? (string) $leftover['name'] : '?')
            . ' -- ' . (isset($leftover['reason']) ? (string) $leftover['reason'] : '')
            . (empty($leftover['deliberate']) ? ' [BASARISIZ]' : ' [bilerek birakildi]');
    }

    $tamamlandi = array_key_exists('completed', $data) ? !empty($data['completed']) : true;
    $silinen = isset($data['deletedSites']) && is_array($data['deletedSites']) ? $data['deletedSites'] : array();

    return '[AdaPanel] Hizmet iptalinde SUNUCUDA KALAN kaynaklar' . "\n"
        . 'Panel hesabi: ' . (isset($decision['userName']) ? (string) $decision['userName'] : (string) $params['username'])
        . ' | Hizmet no: ' . (int) ($params['serviceid'] ?? 0)
        . ' | Alan adi: ' . trim((string) $params['domain']) . "\n"
        . 'Siteler de silinsin mi: ' . (empty($data['siteRemovalRequested']) ? 'HAYIR (urun ayari)' : 'evet') . "\n"
        . 'Silinen siteler: ' . (count($silinen) > 0 ? implode(', ', $silinen) : 'yok') . "\n"
        . 'Panel isi eksiksiz tamamladi mi: ' . ($tamamlandi ? 'evet' : 'HAYIR') . "\n\n"
        . implode("\n", $satirlar) . "\n\n"
        . 'Bu kaynaklar sunucuda DURUYOR. Hizmeti tamamen kaldirilmis saymadan once panelden elle temizleyin.';
}

/**
 * Notu WHMCS'e yazar.
 *
 * NEDEN `AddClientNote`, `UpdateClientProduct` DEGIL: ikisi de belgeli (developers.whmcs.com/
 * api-reference/addclientnote ve /updateclientproduct) ama `UpdateClientProduct`'in `notes` alani
 * hizmetin mevcut yonetici notunun UZERINE YAZAR -- yani baska birinin yazdigi notu sessizce yok
 * ederdi. `AddClientNote` yeni bir kayit EKLER, hicbir seyi silmez; `sticky` de notu musterinin
 * hesabinda ve acilan biletlerde gosterir, yani iptali yapan kisi gormemezlik edemez.
 *
 * `localAPI($command, $values, $adminuser)` imzasi ayni belgeden. Ucuncu parametrenin bos
 * birakilabilecegi HICBIR YERDE yazmiyor, o yuzden varsayilmiyor: yonetici adi sunucu tanimindaki
 * Access Hash alanina `admin=...` ile konur. Yoksa cagri yine denenir ama SONUCU OLCULUR ve
 * basarisizsa not metni modul gunlugune dusurulur -- sessizce kaybolmaz.
 *
 * @return bool Not gercekten yazildi mi.
 */
function adapanel_addClientNote(array $params, $not)
{
    $clientId = (int) ($params['userid'] ?? 0);

    if ($clientId <= 0 || !function_exists('localAPI')) {
        logModuleCall('adapanel', 'TerminateAccount.note_skipped', $clientId, 'localAPI ya da musteri kimligi yok', $not);

        return false;
    }

    $secenekler = AdaPanelClient::parseAccessHash((string) ($params['serveraccesshash'] ?? ''));

    $sonuc = localAPI(
        'AddClientNote',
        array(
            'userid' => $clientId,
            'notes' => $not,
            'sticky' => true,
        ),
        $secenekler['admin']
    );

    // SONUC OLCULUR. "Cagirdim, oldu saydim" bu depodaki en pahali ariza sinifidir; yazilamayan
    // not, yazilmamis nottan daha kotudur cunku yazildigi sanilir.
    if (!is_array($sonuc) || !isset($sonuc['result']) || $sonuc['result'] !== 'success') {
        logModuleCall(
            'adapanel',
            'TerminateAccount.note_failed',
            array('userid' => $clientId, 'admin' => $secenekler['admin']),
            $sonuc,
            'Kalinti notu WHMCS\'e YAZILAMADI; metin burada: ' . "\n" . $not
        );

        return false;
    }

    return true;
}

/**
 * Silme denetiminin reddini WHMCS yoneticisinin okuyabilecegi bir cumleye cevirir.
 * Bilinmeyen sebep OLDUGU GIBI gosterilir; uydurulmus aciklama zaman kaybettirir.
 */
function adapanel_terminateReason($reason, $userName, $domain)
{
    $map = array(
        'not_found' => 'Panelde "' . $userName . '" adli hesap bulunamadi; silinecek bir sey yok '
            . '(hesap daha once elle silinmis olabilir). Hizmeti WHMCS\'te elle kapatin.',
        'ambiguous' => 'Panelde "' . $userName . '" adina uyan BIRDEN COK hesap var. Hangisinin '
            . 'silinecegi kanitlanamadigi icin HICBIRI silinmedi; panelde adlari ayirin.',
        'role_not_customer' => 'Panelde "' . $userName . '" adli hesap MUSTERI DEGIL '
            . '(yonetici/bayi/operator olabilir). Faturalama sistemi bu hesabi silemez.',
        'domain_mismatch' => 'Hizmetin alan adi ("' . $domain . '") panelde "' . $userName
            . '" hesabinin kapsaminda DEGIL. Yanlis hesabi silmemek icin islem durduruldu; '
            . 'panelde site atamasini kontrol edin.',
    );

    if (isset($map[$reason])) {
        return $map[$reason];
    }

    return 'Panel silme denetimi gecilemedi (sebep: ' . $reason . '); hesap SILINMEDI.';
}

/**
 * Paket degisimi (yukseltme/dusurme).
 *
 * Plan kimligi doluysa abonelik plana baglanir; degilse tekil limitler `PUT /api/v1/users/{id}` ile
 * guncellenir. Ikisi ayni cagride karistirilmaz: plan bagi varken tekil limit yazmak, planin bir
 * sonraki okumada limitleri yeniden cozmesi yuzunden SESSIZCE etkisiz kalirdi.
 */
function adapanel_ChangePackage(array $params)
{
    try {
        $client = new AdaPanelClient($params);
        $user = $client->requireUserByName((string) $params['username']);
        $planId = trim((string) adapanel_option($params, 'Servis plani kimligi'));

        if ($planId !== '') {
            $client->put('/api/v1/subscriptions/' . $user['id'] . '/plan', array('planId' => $planId));
            return 'success';
        }

        // Plan bagi yok: once varsa bagi kaldir (bos planId = bagi kaldir), sonra limitleri yaz.
        $client->put('/api/v1/subscriptions/' . $user['id'] . '/plan', array('planId' => null));

        // UpdateUserRequest'in TUM alanlari [Required]; eksik gonderilen alan 400 validation.failed
        // dondurur. Bu yuzden mevcut degerler korunarak komple gonderilir.
        $client->put('/api/v1/users/' . $user['id'], array(
            'email' => (string) $user['email'],
            'role' => (string) $user['role'],
            'ownerId' => isset($user['ownerId']) ? $user['ownerId'] : null,
            'preferredLanguage' => (string) $user['preferredLanguage'],
            'isActive' => !empty($user['isActive']),
            'assignedSites' => isset($user['assignedSites']) ? $user['assignedSites'] : array(),
            'maxSites' => adapanel_int($params, 'En fazla site'),
            'maxDiskMb' => adapanel_int($params, 'Disk kotasi (MB)'),
            'maxDatabases' => adapanel_int($params, 'En fazla veritabani'),
            'maxMailboxes' => adapanel_int($params, 'En fazla posta kutusu'),
            'maxScheduledTasks' => isset($user['maxScheduledTasks']) ? (int) $user['maxScheduledTasks'] : 0,
        ));

        return 'success';
    } catch (Exception $exception) {
        return $exception->getMessage();
    }
}

/**
 * Musterinin panel parolasini belirler.
 *
 * `POST /api/v1/auth/password/set/{id}` govdesi: {"newPassword": "..."} (en az 12 karakter).
 * Uc, kullanicinin KENDI parolasini degistirdigi `change-password` ucundan ayridir: mevcut parolayi
 * SORMAZ (faturalama sistemi zaten bilmiyor), yetkiyi cagiran hesabin rolunden ve kapsamindan alir.
 * Bayi hesabiyla baglanildiginda yalnizca KENDI musterileri icin calisir; baskasinin musterisinde
 * `users.forbidden` doner.
 *
 * ONEMLI YAN ETKI (musteriye soylenmeli): parola degisince o kullanicinin ACIK PANEL OTURUMLARI ve
 * URETTIGI API ANAHTARLARI dusurulur. Panel kac anahtarin dustugunu doner; WHMCS'in kendi
 * arayuzunde gosterecek yeri olmadigi icin burada yalnizca 'success' donuyoruz.
 */
function adapanel_ChangePassword(array $params)
{
    try {
        $client = new AdaPanelClient($params);

        $password = (string) $params['password'];
        if (strlen($password) < 12) {
            // Panelin kurali tek kaynakta: PasswordPolicy.MinimumLength = 12. Sessizce baska bir
            // parola uretmiyoruz -- musteriye giden parola ile paneldeki parola farkli olurdu.
            return 'AdaPanel en az 12 karakterlik parola ister; girilen parola '
                . strlen($password) . ' karakter. Daha uzun bir parola girip tekrar deneyin.';
        }

        $user = $client->requireUserByName((string) $params['username']);
        $client->post('/api/v1/auth/password/set/' . $user['id'], array('newPassword' => $password));

        return 'success';
    } catch (Exception $exception) {
        return $exception->getMessage();
    }
}

/**
 * Tek tikla panele giris (SSO).
 *
 * `POST /api/v1/auth/sso-link/{id}` -> data: { url, expiresAtUtc }. Panel TEK KULLANIMLIK ve
 * dakikalarla olculen omurde bir belirtec uretir; WHMCS musterinin tarayicisini o adrese
 * yonlendirir, adres belirteci oturum cereziyle takas eder.
 *
 * Neden `impersonate` DEGIL: o uc cagiranin oturum cerezini cevirir ve WHMCS panele sunucudan
 * sunucuya baglandigi icin cerez WHMCS sunucusunda kalirdi -- musterinin tarayicisina tasinamaz.
 *
 * Belirtec adreste gittigi icin sunucu gunlugune ve tarayici gecmisine duser; tek kullanimlik ve
 * kisa omurlu olmasinin sebebi tam olarak budur. Baglantiyi SAKLAMAYIN, dogrudan yonlendirin.
 *
 * ASKIDAKI HIZMET: panelde aski girisi degil SITELERI kapatir, bu yuzden askidaki musteri icin de
 * baglanti uretilir (musteri panele girip durumunu gorebilmeli). PASIF hesap icin uretilmez.
 */
function adapanel_ServiceSingleSignOn(array $params)
{
    try {
        $client = new AdaPanelClient($params);
        $user = $client->requireUserByName((string) $params['username']);
        $sonuc = $client->post('/api/v1/auth/sso-link/' . $user['id']);

        $url = isset($sonuc['data']['url']) ? (string) $sonuc['data']['url'] : '';
        if ($url === '') {
            return array(
                'success' => false,
                'errorMsg' => 'Panel giris baglantisi dondurmedi.',
            );
        }

        return array('success' => true, 'redirectTo' => $url);
    } catch (Exception $exception) {
        return array('success' => false, 'errorMsg' => $exception->getMessage());
    }
}

/**
 * Sunucu tanimini sinar: giris, yetki ve abonelik ucunun gercekten calistigini olcer.
 *
 * Yalniz "giris yapabildim" demek yetmez -- musteri rolundeki bir hesapla da giris yapilabilir ama
 * hicbir provisioning cagrisi calismaz. Bu yuzden yonetici-ozel bir uc da denenir.
 */
function adapanel_TestConnection(array $params)
{
    try {
        $client = new AdaPanelClient($params);
        $client->login();

        // Yonetici yetkisini OLC: /api/v1/users [Authorize(Roles="Administrator,Reseller")].
        $client->get('/api/v1/users');

        // Servis planlari YALNIZ yoneticiye acik; bayi hesabi burada 403 alir ve
        // ChangePackage plan bagi yolu calismaz -- bunu simdi soylemek daha iyi.
        $client->get('/api/v1/service-plans');

        return array('success' => true, 'error' => '');
    } catch (Exception $exception) {
        return array('success' => false, 'error' => $exception->getMessage());
    }
}

/**
 * WHMCS yonetici alanindaki hizmet sekmesinde panelden OKUNAN gercek kullanimi gosterir.
 * Salt okunurdur; hicbir sey degistirmez.
 */
function adapanel_AdminServicesTabFields(array $params)
{
    try {
        $client = new AdaPanelClient($params);
        $user = $client->requireUserByName((string) $params['username']);
        $usage = $client->get('/api/v1/subscriptions/' . $user['id']);
        $data = isset($usage['data']) && is_array($usage['data']) ? $usage['data'] : array();

        $satirlar = array();
        foreach ($data as $anahtar => $deger) {
            if (is_scalar($deger) || $deger === null) {
                $satirlar[] = htmlspecialchars($anahtar) . ': <b>' . htmlspecialchars((string) $deger) . '</b>';
            }
        }

        return array(
            'Panel kullanicisi' => htmlspecialchars((string) $user['userName']),
            'Abonelik kullanimi' => implode('<br>', $satirlar),
        );
    } catch (Exception $exception) {
        return array('AdaPanel' => htmlspecialchars($exception->getMessage()));
    }
}

/**
 * Disk ve trafik kullanimini panelden okuyup WHMCS'e yazar.
 *
 * SOZLESME (developers.whmcs.com/provisioning-modules/usage-update adresinden dogrulandi):
 *   - Islev WHMCS cron'u tarafindan, etkin HER SUNUCU icin BIR KEZ cagrilir.
 *   - "Runs per server not per product": $params yalnizca SUNUCU bilgilerini tasir
 *     (serverid, serverhostname, serverip, serverusername, serverpassword, serveraccesshash,
 *     serversecure). Hizmet listesi VERILMEZ.
 *   - Modul, sonuclari `tblhosting` tablosuna yazar: diskusage, disklimit, bwusage, bwlimit,
 *     lastupdate.
 *
 * Bu yuzden panelden TEK bir toplu rapor cekilir (`GET /api/v1/billing/usage`) ve satirlar
 * eslestirilir. Hizmet basina istek atmak, 500 musterili bir sunucuda cron turunu 500 HTTP
 * cagrisina cevirirdi.
 *
 * ESLESTIRME ANAHTARI = `username`. Resmi ornek `domain` ile eslestiriyor ama panelde bir hesabin
 * BIRDEN COK sitesi olabilir; alan adiyla eslestirmek ayni hesabin toplam kullanimini her satira
 * yazip kullanimi KATLARDI. Kullanici adi hizmet omru boyunca degismez ve modulun geri kalani da
 * hesabi bu adla buluyor.
 *
 * BIRIMLER: panel MB gonderir (WHMCS `tblhosting` sutunlari da MB'dir); cevrim panelde yapilir.
 */
function adapanel_UsageUpdate(array $params)
{
    try {
        $client = new AdaPanelClient($params);
        $sonuc = $client->get('/api/v1/billing/usage');
    } catch (Exception $exception) {
        // WHMCS bu islevin donus degerini kullanici arayuzunde gostermez; gorunur tek yer
        // modul gunlugudur. Sessizce yutmak, kullanimin neden guncellenmedigini arastirilamaz
        // hale getirirdi.
        logModuleCall('adapanel', 'UsageUpdate', $params, $exception->getMessage(), $exception->getTraceAsString());
        return array('success' => false, 'error' => $exception->getMessage());
    }

    $data = isset($sonuc['data']) && is_array($sonuc['data']) ? $sonuc['data'] : array();
    $accounts = isset($data['accounts']) && is_array($data['accounts']) ? $data['accounts'] : array();
    $serverId = (int) ($params['serverid'] ?? 0);

    $guncellenen = 0;

    foreach ($accounts as $account) {
        $userName = isset($account['userName']) ? (string) $account['userName'] : '';
        if ($userName === '') {
            continue;
        }

        $degerler = array(
            'diskusage' => (int) ($account['diskUsageMb'] ?? 0),
            'disklimit' => (int) ($account['diskLimitMb'] ?? 0),
            'bwlimit' => (int) ($account['bandwidthLimitMb'] ?? 0),
            'lastupdate' => \WHMCS\Database\Capsule::raw('now()'),
        );

        // OLCULEMEYEN TRAFIK YAZILMAZ. Panel trafigi okuyamadiginda (IIS gunlugu kapali,
        // klasor erisilemez) bunu "0 MB" diye yazmak, asim faturalamasini sessizce sifirlar ve
        // gecen ayin gercek kullanimini da silerdi. Eski deger korunur.
        if (!empty($account['bandwidthMeasured'])) {
            $degerler['bwusage'] = (int) ($account['bandwidthUsageMb'] ?? 0);
        }

        try {
            $guncellenen += \WHMCS\Database\Capsule::table('tblhosting')
                ->where('server', $serverId)
                ->where('username', $userName)
                ->update($degerler);
        } catch (Exception $exception) {
            // Tek satirin yazilamamasi turu durdurmamali; digerleri guncellenmeye devam eder.
            logModuleCall('adapanel', 'UsageUpdate', $userName, $exception->getMessage(), $exception->getTraceAsString());
        }
    }

    return array('success' => true, 'updated' => $guncellenen);
}

// --- kucuk yardimcilar ---

/**
 * Yapilandirma alanini tam sayiya cevirir. WHMCS alanlari `configoption1..N` olarak da gelir;
 * ad ile erisim daha okunur oldugu icin once o denenir.
 */
function adapanel_int(array $params, $label)
{
    return (int) adapanel_option($params, $label);
}

/** yesno alanlarini bool'a cevirir (WHMCS "on" ya da bos dizge dondurur). */
function adapanel_bool(array $params, $label)
{
    $deger = strtolower(trim((string) adapanel_option($params, $label)));

    return in_array($deger, array('on', '1', 'yes', 'true'), true);
}

/**
 * Yapilandirma alanini adiyla okur.
 *
 * WHMCS ayni degerleri hem `configoption1..N` (ConfigOptions dizisindeki SIRAYA gore) hem de
 * `configoptions[<etiket>]` olarak verir. Once etiketle bakariz -- sira degisirse indeksli erisim
 * SESSIZCE yanlis alani okur, etiketli erisim ise okumaz.
 */
function adapanel_option(array $params, $label)
{
    if (isset($params['configoptions'][$label])) {
        return $params['configoptions'][$label];
    }

    // Sira, adapanel_ConfigOptions() ile BIREBIR aynidir; degistirirsen ikisini birden degistir.
    $sira = array(
        'Servis plani kimligi' => 'configoption1',
        'En fazla site' => 'configoption2',
        'Disk kotasi (MB)' => 'configoption3',
        'En fazla veritabani' => 'configoption4',
        'En fazla posta kutusu' => 'configoption5',
        'Iptalde siteleri de sil' => 'configoption6',
    );

    return isset($sira[$label], $params[$sira[$label]]) ? $params[$sira[$label]] : '';
}

/**
 * Panel yalnizca "tr" ve "en" kabul eder (CreateUserRequest.PreferredLanguage deseni:
 * `^(tr|en)(-[A-Z]{2})?$`). WHMCS'in dil adi baska bir sey ise "en"e duseriz -- gecersiz bir
 * deger tum hesap olusturmayi 400 ile patlatirdi.
 */
function adapanel_language(array $params)
{
    $dil = strtolower((string) ($params['clientsdetails']['language'] ?? ''));

    if (strpos($dil, 'turk') !== false || $dil === 'tr') {
        return 'tr';
    }

    return 'en';
}
