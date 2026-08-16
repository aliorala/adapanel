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
 *
 * ChangePassword ve ServiceSingleSignOn islevleri bir sure "uc yok" hatasi donuyordu; panele
 * karsiliklari eklendi (bkz. docs/whmcs-modulu.md) ve artik gercekten calisiyorlar.
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
 */
function adapanel_TerminateAccount(array $params)
{
    try {
        $client = new AdaPanelClient($params);
        $user = $client->requireUserByName((string) $params['username']);

        $removeSites = adapanel_bool($params, 'Iptalde siteleri de sil') ? 'true' : 'false';
        $client->delete('/api/v1/users/' . $user['id'] . '?removeSites=' . $removeSites);

        return 'success';
    } catch (Exception $exception) {
        return $exception->getMessage();
    }
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
