<?php

/**
 * AdaPanel REST istemcisi (WHMCS sunucu modulu icin).
 *
 * TASARIM NOTLARI -- hepsi panelin C# kaynagi OKUNARAK dogrulandi, hicbiri varsayim degil:
 *
 *  1) KIMLIK DOGRULAMA = OTURUM CEREZI. Panelde bir "Bearer adap_..." semasi (PanelToken) var
 *     AMA `Program.cs` onu YALNIZCA `/mcp` ucuna bagliyor
 *     (`app.MapMcp("/mcp").RequireAuthorization(... AuthenticationSchemes = PanelToken)`).
 *     `AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)` varsayilan semayi
 *     cerez yapiyor ve `/api/v1/**` denetleyicilerindeki `[Authorize]` nitelikleri sema
 *     belirtmiyor -> API anahtari REST uclarinda CALISMAZ. Bu yuzden modul kullanici adi/parola
 *     ile giris yapip cerezi tasir.
 *
 *  2) GIZLI GIRIS YOLU BASLIGI ZORUNLU. `SecureEntryPathMiddleware.RequiresSecureEntryHeader`
 *     `/api` altindaki HER yol icin `X-AdaPanel-Entry-Path` bekler (yalniz `/api/v1/setup`,
 *     `/api/v1/health` ve `/api/v1/dbtools` muaf). Basliksiz istek 404 + `security.invalid_entry_path`
 *     doner -- yani "modul calismiyor" gibi gorunur. Yapilandirma alani bu yuzden ZORUNLU.
 *
 *  3) CSRF ZORUNLU. `Program.cs` global `AutoValidateAntiforgeryTokenAttribute` ekliyor; GET disi
 *     TUM isteklerde `X-CSRF-TOKEN` + antiforgery cerezi gerekir. Ustelik istek belirteci OTURUM
 *     KIMLIGINE baglanir: giristen SONRA yeniden alinmalidir, yoksa 400 `security.invalid_csrf_token`.
 *
 *  4) IP KISITLAMASI. Ayni ara katman once `IsIpAllowedAsync` calistirir. Panelde IP kisiti
 *     aciksa WHMCS sunucusunun cikis IP'si listeye eklenmelidir; aksi halde 403
 *     `security.ip_not_allowed`.
 *
 *  5) YANIT ZARFI. Tum uclar `ApiResponse<T>`: `{ success, data, message, code, details }`
 *     (camelCase). Hata yolunda da AYNI zarf doner (`ApiExceptionHandlingMiddleware`).
 */

if (!defined('WHMCS')) {
    // Modul dosyalari WHMCS disindan cagrilmamali.
    die('This file cannot be accessed directly');
}

class AdaPanelApiException extends Exception
{
    /** @var string Panelin dondurdugu ceviri anahtari (ornek: users.duplicate_username). */
    public $messageKey = '';

    /** @var int HTTP durum kodu. */
    public $statusCode = 0;

    public function __construct($message, $messageKey = '', $statusCode = 0)
    {
        parent::__construct($message);
        $this->messageKey = $messageKey;
        $this->statusCode = (int) $statusCode;
    }
}

class AdaPanelClient
{
    /** @var string https://sunucu:8888 */
    private $baseUrl;

    /** @var string Gizli giris yolu (ornek: "panel"). */
    private $entryPath;

    /** @var string */
    private $userName;

    /** @var string */
    private $password;

    /** @var bool Kendinden imzali sertifikaya izin ver. */
    private $insecureTls;

    /** @var string Cerez kavanozu dosyasi. */
    private $cookieJar;

    /** @var string Guncel CSRF istek belirteci. */
    private $csrfToken = '';

    /** @var bool */
    private $loggedIn = false;

    /**
     * @param array $params WHMCS sunucu modulu parametreleri.
     */
    public function __construct(array $params)
    {
        $host = trim((string) ($params['serverhostname'] ?? ''));
        if ($host === '') {
            $host = trim((string) ($params['serverip'] ?? ''));
        }

        $port = (int) ($params['serverport'] ?? 0);
        $secure = !empty($params['serversecure']);

        if ($host === '') {
            throw new AdaPanelApiException('Sunucu adresi (hostname/ip) bos.');
        }

        // Port bos birakilirsa panelin varsayilani kullanilir (PanelRuntimeOptions.PreferredHttpsPort = 8888).
        if ($port <= 0) {
            $port = 8888;
        }

        $this->baseUrl = ($secure ? 'https' : 'http') . '://' . $host . ':' . $port;
        $this->userName = (string) ($params['serverusername'] ?? '');
        $this->password = (string) ($params['serverpassword'] ?? '');

        // Sunucu tanimindaki "Access Hash" alani IKI ayari birden tasir. Neden burasi: WHMCS'in
        // sunucu tanimi bize baska serbest alan vermiyor ve bu iki ayar da SUNUCUYA aittir (urune
        // degil) -- urun yapilandirmasina konsaydi TestConnection sirasinda okunamazlardi, cunku
        // WHMCS sunucu sinamasini urun baglaminda cagirmaz.
        //
        // Kabul edilen bicimler:
        //   panel                       (yalniz gizli giris yolu)
        //   entry=panel;insecure=1      (anahtar=deger, noktali virgulle ayrilmis)
        $secenekler = self::parseAccessHash((string) ($params['serveraccesshash'] ?? ''));

        $this->entryPath = $secenekler['entry'];
        if ($this->entryPath === '') {
            // Panelin varsayilan gizli giris yolu (PanelRuntimeOptions.SecureEntryPath).
            $this->entryPath = 'panel';
        }

        $this->insecureTls = $secenekler['insecure'];
        $this->cookieJar = tempnam(sys_get_temp_dir(), 'adapanel_');
    }

    /**
     * "Access Hash" alanini cozer.
     *
     * @return array{entry: string, insecure: bool}
     */
    private static function parseAccessHash($raw)
    {
        $sonuc = array('entry' => '', 'insecure' => false);
        $raw = trim($raw);

        if ($raw === '') {
            return $sonuc;
        }

        // Icinde "=" yoksa alanin tamami gizli giris yoludur (en sik kullanim).
        if (strpos($raw, '=') === false) {
            $sonuc['entry'] = trim($raw, " \t\n\r/");
            return $sonuc;
        }

        foreach (explode(';', $raw) as $parca) {
            $parca = trim($parca);
            if ($parca === '' || strpos($parca, '=') === false) {
                continue;
            }

            list($anahtar, $deger) = explode('=', $parca, 2);
            $anahtar = strtolower(trim($anahtar));
            $deger = trim($deger);

            if ($anahtar === 'entry') {
                $sonuc['entry'] = trim($deger, " \t\n\r/");
            } elseif ($anahtar === 'insecure') {
                $sonuc['insecure'] = in_array(strtolower($deger), array('1', 'true', 'yes', 'evet'), true);
            }
        }

        return $sonuc;
    }

    public function __destruct()
    {
        if ($this->cookieJar !== '' && file_exists($this->cookieJar)) {
            @unlink($this->cookieJar);
        }
    }

    /**
     * Panele giris yapar. CSRF belirteci GIRISTEN SONRA yenilenir (panelin kendi arayuzu de
     * boyle yapar; aksi halde sonraki her yazma istegi 400 security.invalid_csrf_token alir).
     */
    public function login()
    {
        if ($this->loggedIn) {
            return;
        }

        if ($this->userName === '' || $this->password === '') {
            throw new AdaPanelApiException('Panel kullanici adi/parolasi bos (sunucu tanimini kontrol edin).');
        }

        $this->refreshCsrfToken();

        $result = $this->send('POST', '/api/v1/auth/login', array(
            'userNameOrEmail' => $this->userName,
            'password' => $this->password,
            'rememberMe' => false,
        ));

        $data = isset($result['data']) && is_array($result['data']) ? $result['data'] : array();

        if (!empty($data['requiresTwoFactor'])) {
            throw new AdaPanelApiException(
                'Panel hesabinda iki adimli dogrulama acik. WHMCS icin 2FA KAPALI, yalniz bu entegrasyona '
                . 'ayrilmis bir yonetici hesabi kullanin.'
            );
        }

        if (empty($data['isAuthenticated'])) {
            throw new AdaPanelApiException('Panel girisi basarisiz: kullanici adi ya da parola hatali.');
        }

        $this->loggedIn = true;

        // Belirtec oturum kimligine baglidir; giristen sonra MUTLAKA yenilenir.
        $this->refreshCsrfToken();
    }

    public function get($path)
    {
        return $this->authorized('GET', $path, null);
    }

    public function post($path, $body = null)
    {
        return $this->authorized('POST', $path, $body);
    }

    public function put($path, $body = null)
    {
        return $this->authorized('PUT', $path, $body);
    }

    public function delete($path, $body = null)
    {
        return $this->authorized('DELETE', $path, $body);
    }

    /**
     * Kullanici adina gore panel kullanicisini bulur.
     *
     * NEDEN arama: modul, panel kullanici kimligini (GUID) WHMCS tarafinda saklamak zorunda
     * kalmasin diye her cagrida kullanici adiyla arar. Kullanici adi WHMCS'in urettigi
     * `$params['username']` degeridir ve hizmet omru boyunca degismez.
     *
     * @return array|null
     */
    public function findUserByName($userName)
    {
        $result = $this->get('/api/v1/users');
        $users = isset($result['data']) && is_array($result['data']) ? $result['data'] : array();

        foreach ($users as $user) {
            if (isset($user['userName']) && strcasecmp($user['userName'], $userName) === 0) {
                return $user;
            }
        }

        return null;
    }

    /** Kullaniciyi bulur; yoksa anlasilir bir hata firlatir. */
    public function requireUserByName($userName)
    {
        $user = $this->findUserByName($userName);
        if ($user === null) {
            throw new AdaPanelApiException(
                'Panelde "' . $userName . '" adli kullanici bulunamadi (hesap panelden elle silinmis olabilir).'
            );
        }

        return $user;
    }

    // --- ic isleyis ---

    private function authorized($method, $path, $body)
    {
        $this->login();
        return $this->send($method, $path, $body);
    }

    private function refreshCsrfToken()
    {
        $result = $this->send('GET', '/api/v1/auth/csrf', null);
        $token = isset($result['data']['requestToken']) ? (string) $result['data']['requestToken'] : '';

        if ($token === '') {
            throw new AdaPanelApiException('Panelden CSRF belirteci alinamadi.');
        }

        $this->csrfToken = $token;
    }

    /**
     * Tek bir HTTP istegi gonderir ve zarfi cozer.
     *
     * @return array Cozulmus ApiResponse zarfi.
     * @throws AdaPanelApiException
     */
    private function send($method, $path, $body)
    {
        $url = $this->baseUrl . $path;

        $headers = array(
            'Accept: application/json',
            // Gizli giris yolu basligi: olmadan panel 404 doner (bkz. dosya basi, madde 2).
            'X-AdaPanel-Entry-Path: ' . $this->entryPath,
        );

        if ($this->csrfToken !== '') {
            $headers[] = 'X-CSRF-TOKEN: ' . $this->csrfToken;
        }

        $handle = curl_init();
        curl_setopt($handle, CURLOPT_URL, $url);
        curl_setopt($handle, CURLOPT_RETURNTRANSFER, true);
        curl_setopt($handle, CURLOPT_CUSTOMREQUEST, $method);
        curl_setopt($handle, CURLOPT_COOKIEJAR, $this->cookieJar);
        curl_setopt($handle, CURLOPT_COOKIEFILE, $this->cookieJar);
        curl_setopt($handle, CURLOPT_CONNECTTIMEOUT, 15);
        curl_setopt($handle, CURLOPT_TIMEOUT, 180);
        // Yonlendirme IZLENMEZ: panel bir yonlendirme dondurduyse bu bir hatadir, sessizce
        // baska bir sayfaya gidip "basarili" saymak yanlis olurdu.
        curl_setopt($handle, CURLOPT_FOLLOWLOCATION, false);

        if ($this->insecureTls) {
            curl_setopt($handle, CURLOPT_SSL_VERIFYPEER, false);
            curl_setopt($handle, CURLOPT_SSL_VERIFYHOST, 0);
        }

        if ($body !== null) {
            $encoded = json_encode($body);
            $headers[] = 'Content-Type: application/json; charset=utf-8';
            $headers[] = 'Content-Length: ' . strlen($encoded);
            curl_setopt($handle, CURLOPT_POSTFIELDS, $encoded);
        }

        curl_setopt($handle, CURLOPT_HTTPHEADER, $headers);

        $raw = curl_exec($handle);
        $curlError = curl_error($handle);
        $status = (int) curl_getinfo($handle, CURLINFO_HTTP_CODE);
        curl_close($handle);

        if ($raw === false) {
            throw new AdaPanelApiException('Panele baglanilamadi: ' . $curlError);
        }

        $decoded = json_decode($raw, true);
        if (!is_array($decoded)) {
            throw new AdaPanelApiException(
                'Panel JSON olmayan bir yanit dondurdu (HTTP ' . $status . '). '
                . 'Adres/port ya da gizli giris yolu yanlis olabilir.'
            );
        }

        if (empty($decoded['success'])) {
            $key = isset($decoded['message']) ? (string) $decoded['message'] : 'unknown_error';
            $details = isset($decoded['details']) ? trim((string) $decoded['details']) : '';
            throw new AdaPanelApiException(
                self::describe($key, $status) . ($details !== '' ? ' (' . $details . ')' : ''),
                $key,
                $status
            );
        }

        return $decoded;
    }

    /**
     * Panelin ceviri anahtarini WHMCS yoneticisinin okuyabilecegi bir cumleye cevirir.
     * Bilinmeyen anahtar OLDUGU GIBI gosterilir -- uydurulmus bir aciklama, hatanin kendisinden
     * daha cok zaman kaybettirir.
     */
    private static function describe($key, $status)
    {
        $map = array(
            'security.invalid_entry_path' => 'Gizli giris yolu yanlis (sunucu tanimindaki Access Hash alani).',
            'security.ip_not_allowed' => 'WHMCS sunucusunun IP adresi panelin izinli IP listesinde degil.',
            'security.invalid_csrf_token' => 'CSRF belirteci gecersiz.',
            'auth.unauthorized' => 'Oturum acilmamis ya da dusmus.',
            'auth.forbidden' => 'Bu islem icin panel hesabinin yetkisi yok (Yonetici olmali).',
            'users.forbidden' => 'Bu kullanici uzerinde yetki yok (bayi hesabi yalnizca KENDI '
                . 'musterileri icin parola belirleyebilir ve giris baglantisi uretebilir).',
            'users.password_too_short' => 'Parola en az 12 karakter olmali.',
            'auth.user_inactive' => 'Panel hesabi PASIF; pasif hesap icin giris baglantisi uretilmez. '
                . 'Once hizmeti aktiflestirin.',
            'users.duplicate_username' => 'Panelde ayni kullanici adi zaten var.',
            'users.duplicate_email' => 'Panelde ayni e-posta adresi zaten var.',
            'validation.failed' => 'Panel istegi dogrulayamadi (alan kurallari: parola en az 12 karakter, '
                . 'kullanici adi 3-32 karakter, gecerli e-posta).',
        );

        if (isset($map[$key])) {
            return $map[$key];
        }

        return 'Panel hatasi: ' . $key . ' (HTTP ' . $status . ')';
    }
}
