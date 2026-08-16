# WHMCS modülü

AdaPanel için bir WHMCS **sunucu (provisioning) modülü** `integrations/whmcs/adapanel/` altında
bulunur. Sipariş ödendiğinde panelde müşteri hesabı açar, ödeme gecikince askıya alır, iptal
edilince siler.

**Kurulum, yapılandırma ve sorun giderme adımlarının tamamı:**
[`integrations/whmcs/README.md`](../integrations/whmcs/README.md)

## Kısaca

| WHMCS işlevi | Panelde karşılığı | Durum |
|---|---|---|
| `CreateAccount` | `POST /api/v1/users` (+ isteğe bağlı plan bağı) | Çalışıyor |
| `SuspendAccount` | `POST /api/v1/subscriptions/{id}/suspend` | Çalışıyor |
| `UnsuspendAccount` | `POST /api/v1/subscriptions/{id}/resume` | Çalışıyor |
| `TerminateAccount` | `DELETE /api/v1/users/{id}?removeSites=` | Çalışıyor |
| `ChangePackage` | `PUT /api/v1/subscriptions/{id}/plan` ya da `PUT /api/v1/users/{id}` | Çalışıyor |
| `TestConnection` | Giriş + `GET /api/v1/users` + `GET /api/v1/service-plans` | Çalışıyor |
| `ChangePassword` | `POST /api/v1/auth/password/set/{id}` | Çalışıyor |
| `ServiceSingleSignOn` | `POST /api/v1/auth/sso-link/{id}` | Çalışıyor |

## Sonradan eklenen iki uç

Bu iki işlev bir süre "panelde karşılığı yok" diyen açık bir hata döndürüyordu (uydurulmamıştı).
Uçlar eklendi; ikisi de `AuthController` içinde, yani sınıf düzeyinde **çerez şemasına** sabitli.

### 1. Yönetici/bayi tarafından parola belirleme

```
POST /api/v1/auth/password/set/{userId}   [Authorize(Roles = "Administrator,Reseller")]
Body: { "newPassword": "..." }            (en az 12 karakter)
->   { "revokedApiTokens": <sayı> }
```

* **Yetki:** yönetici herkes için; bayi **yalnız kendi** müşterisi için; müşteri hiç kimse için.
  Kural tek yerde: `ManagedUserGuard` (aynı guard `UpdateUser`, `UnlockUser`, `DeleteUser` ve
  SSO bağlantısı tarafından da kullanılıyor — beş kopya yerine tek uygulama).
* **Kendi hesabınız için kullanılamaz** (403). Bu uç mevcut parolayı sormaz; kendi hesabına da açık
  olsaydı çalınmış bir çerez, parolayı hiç bilmeden hesabı kalıcı olarak devralabilir ve
  `change-password` üzerindeki mevcut-parola koşulu anlamsızlaşırdı.
* **Parola politikası tek kaynaktan:** `PasswordPolicy.Ensure` (min 12). Aynı kaynak artık
  `CreateUserAsync`, `CreateSubUserAsync`, `UpdateSubUserAsync`, `ChangePasswordAsync` ve
  `ResetPasswordAsync` yollarında da çağrılıyor. **Ölçülen asimetri kapandı:** `CreateSubUserAsync`
  uzunluğu HİÇ denetlemiyordu, yani abonelik altı ek kullanıcı tek karakterlik parolayla
  açılabiliyordu.
* **Erişim gerçekten kesilir:** açık çerez oturumları düşer (kimlik damgası parola karmasından
  türer — `CookieSessionValidator`, ikinci bir mekanizma yazılmadı) ve API anahtarları iptal edilir.
* Denetim kaydı `auth.password_set_by_admin`; **parola kayda yazılmaz**.

### 2. Tek kullanımlık giriş bağlantısı (SSO)

```
POST /api/v1/auth/sso-link/{userId}       [Authorize(Roles = "Administrator,Reseller")]
->   { "url": "https://<panel>/api/v1/auth/sso?token=…", "expiresAtUtc": "…" }

GET  /api/v1/auth/sso?token=…             [AllowAnonymous]  ← müşterinin TARAYICISI gezer
```

* Belirteç **tek kullanımlık**, **2 dakika** ömürlü, hedef kullanıcıya bağlı ve sunucuda yalnız
  **SHA-256 özetiyle** saklanıyor (`config/sso-tokens.json`). Yeni bağlantı eskisini geçersiz kılar.
* **Neden bu kadar kısa ömürlü:** belirteç adreste gittiği için panel istek günlüğüne, önündeki ters
  vekilin günlüğüne ve müşterinin tarayıcı geçmişine **düşer**. Tarayıcı gezintisi özel başlık
  gönderemediği için bunu ortadan kaldıramayız; tek savunma belirteci değersizleştirmek — günlüğü
  sonradan okuyan biri çoktan tüketilmiş ve süresi geçmiş bir dize bulur. Panelin küresel
  `Referrer-Policy: same-origin` başlığı adresin üçüncü taraflara sızmasını da engeller.
* **Kripto icat edilmedi:** üretim/depolama/tüketim disiplininin tamamı parola sıfırlama
  belirteciyle **ortak** bir yerden geliyor (`SingleUseTokenStore`); bulma ve tüketme dosya kilidi
  altında tek parça yapılır, yoksa eşzamanlı iki istek aynı belirteci iki kez kullanabilirdi.
* **Askıdaki abonelik için bağlantı ÜRETİLİR.** Panelde askı girişi değil siteleri kapatır
  (`ISubscriptionEnforcementService`): müşteri panele girip neden askıda olduğunu, kotasını ve
  faturasını görebilmeli — çözüm orada. Tam da ödemesi geciken müşteri için "Panele git" düğmesini
  ölü bırakmak askının amacına ters düşerdi. **Pasif hesap için üretilmez**
  (`auth.user_inactive`): normal giriş yolu pasif kullanıcıyı zaten reddediyor.
* **İki adımlı doğrulama bu yolda sorulmaz.** Belirteci üretebilen kimlik zaten yönetici/bayi
  yetkisindedir ve aynı hesaba kimliğe bürünme ya da parola belirleme ile de ulaşabilir; ikinci
  adımı burada sormak hiçbir saldırganı durdurmayıp yalnız meşru düğmeyi bozardı.
* **Kullanım ucu JSON değil SAYFA döner** ve 302 yerine ara sayfa kullanır: panelin oturum çerezi
  `SameSite=Strict`, faturalama sisteminden gelen gezinti ise çapraz sitedir. Doğrudan 302 ile
  panele atsaydık tarayıcı taze çerezi bir sonraki isteğe eklemeyebilir ve müşteri giriş ekranına
  düşerdi. Ara sayfa panelin kendi kaynağında durur; oradan yapılan gezinti aynı sitedir.
* Uç, gizli giriş yolu başlığından **muaftır** (`SecureEntryPathMiddleware`) — tarayıcı gezintisi
  özel başlık ekleyemez; aynı gerekçe `/api/v1/dbtools` için de yazılı. IP kısıtlaması kaldırılmadı.
* Denetim kaydı: `auth.sso_link.created` / `.redeemed` / `.rejected`; **belirteç kayda yazılmaz**.

## Modül neden hâlâ kullanıcı adı/parola ile giriş yapıyor?

API anahtarı (`Bearer adap_…`) artık `/api/v1/**` uçlarında **çalışıyor** (`PanelAuthentication`
yönlendirici şeması). Ama **kimlik bilgisi yöneten uçlar bilerek dışarıda**: `AuthController` ve
`ApiTokensController` sınıf düzeyinde çerez şemasına sabitlenmiştir — aksi hâlde salt-okur olmayan
bir anahtar başka bir yöneticinin parolasını belirleyip tam yetkili bir oturum açabilir, yani
anahtarın kısıtı kâğıt üzerinde kalırdı.

Yukarıdaki iki uç tam olarak o sınıfta olduğu için modül çerez oturumu açmaya devam ediyor.
