<?php

/**
 * AdaPanel REST istemcisi (WHMCS sunucu modulu icin).
 *
 * TASARIM NOTLARI -- hepsi panelin C# kaynagi OKUNARAK dogrulandi, hicbiri varsayim degil:
 *
 *  1) IKI KIMLIK YOLU VAR; hangisinin kullanilacagi yapilandirmadan gelir.
 *
 *     a) PANEL ANAHTARI (tercih edilen). `PanelAuthentication` artik bir YONLENDIRICI semadir:
 *        istek `Authorization: Bearer adap_...` tasiyorsa `/api/v1/**` uclarinda da anahtar semasi
 *        calisir (eskiden yalniz `/mcp` ucuna bagliydi). Anahtar yolunun uc avantaji var:
 *        dakikada 5 istekle sinirli `login` politikasina TAKILMAZ, CSRF ISTEMEZ (bkz. madde 3) ve
 *        gizli giris yolu basligindan MUAFTIR (`SecureEntryPathMiddleware.CarriesApiToken`).
 *        Kullanim toplama (UsageUpdate) bu yuzden anahtarla calisir ve SALT-OKUR bir anahtar yeter.
 *
 *     b) OTURUM CEREZI. Kullanici adi/parola ile giris yapilir. Parola belirleme ve tek tikla
 *        giris ZORUNLU olarak bu yolu kullanir: `AuthController` ve `ApiTokensController` sinif
 *        duzeyinde CEREZ semasina sabitlenmistir (yetki yukseltmesini onlemek icin), yani o uclar
 *        anahtarla CALISMAZ.
 *
 *        BU KISIT ARTIK KODDA (eskiden yalniz bu yorumda yaziyordu ve modul kisiti ihlal
 *        ediyordu): anahtar yapilandirilmis olsa bile `/api/v1/auth/**` cagrilarinda anahtar bir
 *        kenara birakilir ve cerezli oturum acilir -- bkz. `requiresCookieScheme()`.
 *
 *     UYARI: anahtarla YAZMA islemi (POST/PUT/DELETE) yapilacaksa anahtarin "yikici islemlere izin
 *     ver" yetkisi ACIK olmalidir (`PanelTokenCapabilities`); salt-okur anahtar 403
 *     `auth.forbidden` + `apitoken.read_only` alir.
 *
 *  2) GIZLI GIRIS YOLU BASLIGI (cerez yolunda) ZORUNLU.
 *     `SecureEntryPathMiddleware.RequiresSecureEntryHeader` `/api` altindaki HER yol icin
 *     `X-AdaPanel-Entry-Path` bekler (yalniz `/api/v1/setup`, `/api/v1/health` ve `/api/v1/dbtools`
 *     muaf). Basliksiz istek 404 + `security.invalid_entry_path` doner -- yani "modul calismiyor"
 *     gibi gorunur. Baslik anahtar yolunda da zararsizca gonderilir.
 *
 *  3) CSRF (yalniz cerez yolunda). `PanelAntiforgeryFilter` GET disi TUM cerezli isteklerde
 *     `X-CSRF-TOKEN` + antiforgery cerezi ister. Ustelik istek belirteci OTURUM KIMLIGINE baglanir:
 *     giristen SONRA yeniden alinmalidir, yoksa 400 `security.invalid_csrf_token`. Anahtarli
 *     istekte dogrulama HIC calismaz (Authorization basligini tarayici kendiliginden gondermez).
 *
 *  4) IP KISITLAMASI. Ayni ara katman once `IsIpAllowedAsync` calistirir. Panelde IP kisiti
 *     aciksa WHMCS sunucusunun cikis IP'si listeye eklenmelidir; aksi halde 403
 *     `security.ip_not_allowed`. Bu kisit ANAHTAR yolunda da uygulanir.
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

    /**
     * Panelin dondurdugu COZULMUS zarfin tamami (`{success, data, message, code, details}`).
     *
     * NEDEN VAR: panel hata yolunda da `data` doldurabiliyor ve o veri bazen hatanin KENDISINDEN
     * daha degerli. Somut ornek: kullanici silme yarim kaldiginda uc 409 doner ve `data.leftovers`
     * icinde sunucuda KALAN kaynaklari (site, veritabani, klasor) adiyla listeler. Istisna yalniz
     * metin tasisaydi o yapisal liste catch blogunda kaybolurdu -- yani modul, yoneticiye "neyin
     * kaldigini" yazamazdi. Basari yolunda ise cagiran zarfi zaten dogrudan aliyor.
     *
     * @var array
     */
    public $response = array();

    public function __construct($message, $messageKey = '', $statusCode = 0, array $response = array())
    {
        parent::__construct($message);
        $this->messageKey = $messageKey;
        $this->statusCode = (int) $statusCode;
        $this->response = $response;
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

    /** @var string Panel API anahtari (adap_...). Bos ise cerez yolu kullanilir. */
    private $apiToken;

    /** @var string Cerez kavanozu dosyasi. */
    private $cookieJar;

    /** @var string Guncel CSRF istek belirteci. */
    private $csrfToken = '';

    /** @var bool Cerezli oturum acildi mi. */
    private $cookieSession = false;

    /**
     * Bu istekte `Authorization` basligi GONDERILMESIN mi.
     *
     * Cerez semasina sabitlenmis uclarda anahtarli bir baslik gondermek, panelin yonlendirici
     * semasini anahtar yoluna sokar ve cerezli kimlik HIC degerlendirilmez -- yani oturum acmis
     * olsak bile 401 alirdik.
     *
     * @var bool
     */
    private $tokenSuppressed = false;

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
        //   panel                                 (yalniz gizli giris yolu)
        //   entry=panel;insecure=1                (anahtar=deger, noktali virgulle ayrilmis)
        //   entry=panel;token=adap_xxx            (panel API anahtariyla kimlik)
        //   entry=panel;admin=whmcsadmin          (WHMCS Local API cagrilarinin kosacagi admin)
        $secenekler = self::parseAccessHash((string) ($params['serveraccesshash'] ?? ''));

        $this->apiToken = $secenekler['token'];
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
     * @return array{entry: string, insecure: bool, token: string, admin: string}
     */
    public static function parseAccessHash($raw)
    {
        $sonuc = array('entry' => '', 'insecure' => false, 'token' => '', 'admin' => '');
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
            } elseif ($anahtar === 'token') {
                $sonuc['token'] = $deger;
            } elseif ($anahtar === 'admin') {
                // WHMCS Local API'nin ucuncu parametresi (developers.whmcs.com/api/internal-api:
                // `localAPI($command, $values, $adminuser)`). Belge bu parametrenin bos
                // birakilabilecegini SOYLEMIYOR, o yuzden tahmin etmiyoruz: yonetici adi buraya
                // yazilir. Bos birakilirsa modul yine dener ama sonucu OLCER ve basarisizsa
                // not metnini modul gunlugune yazar (bkz. adapanel_addClientNote).
                $sonuc['admin'] = $deger;
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
        // ANAHTAR VARSA GIRIS YOK. Anahtar zaten bir kimliktir; ustune oturum acmak dakikada 5
        // istekle sinirli login politikasina gereksiz yere girmek olurdu.
        if ($this->apiToken !== '') {
            return;
        }

        $this->ensureCookieSession();
    }

    /**
     * Cerezli oturumu (varsa yeniden kullanarak) acar.
     *
     * TEK KAYNAK: giris el sikismasi (csrf -> login -> csrf) burada bir kez yazilir. Anahtar yolu
     * da cerez semasina sabitlenmis uclar icin buraya duser, yani el sikismanin ikinci bir kopyasi
     * YOK.
     */
    private function ensureCookieSession()
    {
        if ($this->cookieSession) {
            return;
        }

        if ($this->userName === '' || $this->password === '') {
            throw new AdaPanelApiException('Panel kullanici adi/parolasi bos (sunucu tanimini kontrol edin).');
        }

        // El sikismanin TAMAMI anahtarsiz yapilir: `csrf` ve `login` uclari [AllowAnonymous] olsa da
        // istekte `Authorization` bulunmasi panelin yonlendirici semasini anahtar yoluna sokar ve
        // antiforgery cerezi cerezli oturuma baglanmayabilir.
        $this->tokenSuppressed = true;

        try {
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

            // Panel bu ucta 200 + success:true doner, kimlik dogrulanMAMIS olsa bile; karar
            // `data.isAuthenticated` alanindadir. Yalniz zarfa bakmak, hatali parolayi BASARILI
            // sayardi.
            if (empty($data['isAuthenticated'])) {
                throw new AdaPanelApiException('Panel girisi basarisiz: kullanici adi ya da parola hatali.');
            }

            $this->cookieSession = true;

            // Belirtec oturum kimligine baglidir; giristen sonra MUTLAKA yenilenir.
            $this->refreshCsrfToken();
        } finally {
            $this->tokenSuppressed = false;
        }
    }

    /**
     * Bu yol CEREZ semasi ISTIYOR mu.
     *
     * OLCUM (C# kaynagi): `AuthController` sinif duzeyinde
     * `[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme)]`
     * tasir -- yetki yukseltmesini onlemek icin bilincli bir karar. Sonucu: `password/set/{id}` ve
     * `sso-link/{id}` uclari panel API ANAHTARIYLA CALISMAZ, 401 `auth.unauthorized` doner.
     *
     * BU ESKIDEN SESSIZ BIR ARIZAYDI: sunucu tanimina `token=adap_...` yazan yoneticide parola
     * belirleme ve tek tikla giris HER ZAMAN basarisiz oluyordu ve modulun mesaji ("oturum acilmamis
     * ya da anahtar gecersiz/iptal edilmis") yoneticiyi saglam bir anahtari yenilemeye
     * gonderiyordu -- yani hatanin gosterdigi yer, hatanin oldugu yer DEGILDI. Kisit dosyanin bas
     * yorumunda YAZILIYDI ama koda hic gecmemisti.
     *
     * Cozum kisiti kaldirmak degil, ona UYMAK: bu uclarda anahtar bir kenara birakilir ve ayni
     * sunucu tanimindaki yonetici kullanici adi/parolasiyla cerezli oturum acilir.
     */
    private static function requiresCookieScheme($path)
    {
        return strpos($path, '/api/v1/auth/') === 0;
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
        // Kural CAGRI YERINDE DEGIL BURADA: "bu uc anahtarla calismaz" bilgisi her cagriya tek tek
        // yazilsaydi, yeni eklenen bir auth cagrisinda unutulur ve ayni sessiz ariza geri gelirdi.
        if ($this->apiToken !== '' && self::requiresCookieScheme($path)) {
            $this->ensureCookieSession();

            $this->tokenSuppressed = true;
            try {
                return $this->send($method, $path, $body);
            } finally {
                $this->tokenSuppressed = false;
            }
        }

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
            // Anahtar yolunda panel bu basliktan muaf tutar; yine de gonderilmesi zararsizdir.
            'X-AdaPanel-Entry-Path: ' . $this->entryPath,
        );

        if ($this->apiToken !== '' && !$this->tokenSuppressed) {
            $headers[] = 'Authorization: Bearer ' . $this->apiToken;
        }

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
                $status,
                // Cozulmus zarf OLDUGU GIBI tasinir: hata yolundaki `data` (ornegin silme
                // kalintilari) catch blogunda kaybolmasin.
                $decoded
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
            'auth.unauthorized' => 'Oturum acilmamis ya da dusmus (ya da API anahtari gecersiz/iptal edilmis).',
            'auth.forbidden' => 'Bu islem icin panel hesabinin yetkisi yok (Yonetici olmali). '
                . 'API anahtari kullaniyorsaniz YAZMA islemleri icin anahtarin "yikici islemlere izin ver" '
                . 'yetkisi acik olmalidir.',
            'billing.disabled' => 'Panelde faturalama entegrasyonu KAPALI. Panel -> Faturalama ekranindan acin.',
            'users.forbidden' => 'Bu kullanici uzerinde yetki yok (bayi hesabi yalnizca KENDI '
                . 'musterileri icin parola belirleyebilir ve giris baglantisi uretebilir).',
            'users.password_too_short' => 'Parola en az 12 karakter olmali.',
            'auth.user_inactive' => 'Panel hesabi PASIF; pasif hesap icin giris baglantisi uretilmez. '
                . 'Once hizmeti aktiflestirin.',
            'users.delete.incomplete' => 'Panel kullaniciyi sildi ama sunucuda temizlenemeyen kaynaklar KALDI. '
                . 'Hizmeti tamamen kaldirilmis saymayin; kalinti listesi asagida ve musteri notlarina yazildi.',
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
