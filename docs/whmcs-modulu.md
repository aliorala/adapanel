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
| `ChangePassword` | **Uç yok** | Çalışmıyor — açık hata döner |
| `ServiceSingleSignOn` | **Uç yok** | Çalışmıyor — açık hata döner |

## Panele eklenmesi gereken iki uç

Modülün eksik iki işlevi, panel tarafında karşılığı olmadığı için **uydurulmadı**. Eklenmeleri
hâlinde modül tarafında yapılacak değişiklik küçüktür.

### 1. Yönetici tarafından parola belirleme

Bugün panelde bir yöneticinin **başka** bir kullanıcının parolasını belirlediği uç yoktur:

* `PUT /api/v1/users/{id}` gövdesi (`UpdateUserRequest`) parola alanı **taşımaz**.
* `POST /api/v1/auth/change-password` **mevcut** parolayı ister ve bilerek yalnız çerez şemasını
  kabul eder (API anahtarıyla parola değiştirilememesi kasıtlı bir güvenlik kararıdır).
* `POST /api/v1/auth/password/forgot` parola **belirlemez**, e-posta gönderir.

Önerilen uç:

```
POST /api/v1/users/{userId}/password     [Authorize(Roles = "Administrator,Reseller")]
Body: { "newPassword": "..." }           (en az 12 karakter)
```

Aynı akış zaten alt kullanıcılar için var: `UserManagementService.UpdateSubUserAsync` parolayı
değiştirir **ve** `apiTokenService.RevokeAllForUserAsync` ile o kullanıcının API anahtarlarını
düşürür. Yeni uç aynı disiplini izlemelidir.

### 2. Tek kullanımlık giriş bağlantısı (SSO)

`POST /api/v1/auth/impersonate/{id}` yöneticinin **kendi tarayıcı oturumunu** hedef kullanıcıya
çevirir (yanıtta `Set-Cookie`). WHMCS bu çağrıyı sunucudan sunucuya yaptığı için çerez WHMCS
sunucusunda kalır; müşterinin tarayıcısına taşınamaz.

Önerilen uç:

```
POST /api/v1/auth/sso-link/{userId}      [Authorize(Roles = "Administrator")]
-> { "url": "https://panel:8890/<gizli-yol>/sso?token=<tek-kullanimlik>" }
```

Panelde bunun kanıtlanmış örnekleri zaten var: `WordPressController.CreateLoginLinkAsync`
(tek kullanımlık, kısa ömürlü sihirli bağlantı) ve `WebDbController` signon URL'leri.

## Denetimde çıkan uyumsuzluk: API anahtarı REST uçlarında geçmiyor

Modül, kullanıcı adı/parola ile **çerez** oturumu açar — API anahtarı kullanamaz. Sebep:

* `Program.cs` `AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)` ile varsayılan
  şemayı çerez yapıyor.
* `PanelTokenAuthenticationHandler` (Bearer `adap_…`) kayıtlı, ama **yalnızca** `/mcp` ucuna
  bağlanmış.
* `/api/v1/**` denetleyicilerindeki `[Authorize]` nitelikleri şema belirtmediği için varsayılan
  (çerez) şema kullanılır → `Authorization` başlığı hiç okunmaz.

`AddAuthorization` içinde iki şemayı da kabul eden bir varsayılan politika tanımlanırsa modül
parolasız çalışabilir ve `POST /api/v1/auth/login` üzerindeki **dakikada 5 istek** sınırı da
sorun olmaktan çıkar.
