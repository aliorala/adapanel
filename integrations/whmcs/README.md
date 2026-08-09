# AdaPanel — WHMCS sunucu (provisioning) modülü

WHMCS'te satılan bir barındırma ürününü AdaPanel'e bağlar: sipariş ödendiğinde panelde müşteri
hesabı açılır, ödeme gecikince askıya alınır, iptal edilince silinir.

> Bu belgedeki her uç, panelin C# denetleyicileri **okunarak** doğrulandı. Doğrulanamayan hiçbir
> davranış yazılmadı; panelde karşılığı olmayan iki WHMCS işlevi "Bilinen sınırlar" başlığında
> açıkça listelendi.

---

## 1. Kurulum

1. `integrations/whmcs/adapanel/` klasörünün **tamamını** WHMCS sunucusunda
   `<whmcs-kök>/modules/servers/adapanel/` altına kopyalayın. Sonuç şu olmalı:

   ```
   modules/servers/adapanel/adapanel.php
   modules/servers/adapanel/lib/AdaPanelClient.php
   ```

2. WHMCS yönetici arayüzünde sunucu listesine gidin (WHMCS sürümüne göre
   *Setup → Products/Services → Servers* ya da *Configuration → System Settings → Servers*) ve
   **Add New Server** deyin.

3. Alanları şöyle doldurun:

   | WHMCS alanı | Değer |
   |---|---|
   | Name | Serbest (örn. `ada-01`) |
   | Hostname | Panelin çalıştığı sunucunun adı ya da IP'si |
   | Server IP | (isteğe bağlı; Hostname boşsa kullanılır) |
   | Module | **AdaPanel** |
   | Username | Panelde **Administrator** rolündeki hesabın kullanıcı adı |
   | Password | O hesabın parolası |
   | Access Hash | Panelin **gizli giriş yolu** (aşağıya bakın) |
   | Secure | Panel HTTPS dinliyorsa işaretleyin |
   | Port | Panelin portu (varsayılan `8888`) |

4. **Test Connection** düğmesine basın. Modül üç şeyi birden ölçer: giriş yapılabiliyor mu,
   hesap yönetici yetkisine sahip mi (`GET /api/v1/users`), servis planları okunabiliyor mu
   (`GET /api/v1/service-plans`). Üçü de geçmeden ürünü satışa açmayın.

### Access Hash alanı — iki ayarı birden taşır

Panel `/api` altındaki **her** isteği `X-AdaPanel-Entry-Path` başlığıyla doğrular
(`SecureEntryPathMiddleware`). Başlık yanlışsa panel `404 security.invalid_entry_path` döner —
dışarıdan "modül çalışmıyor" gibi görünür. WHMCS'in sunucu tanımında başka serbest alan olmadığı
için bu bilgi **Access Hash** alanında taşınır:

```
panel                      # yalnız gizli giriş yolu
entry=panel;insecure=1     # gizli giriş yolu + sertifika doğrulamasını atla
```

* `entry` — panelde **sol menü → Güvenlik → gizli giriş yolu** alanındaki değerle **birebir** aynı
  olmalı (varsayılan `panel`).
* `insecure=1` — panel **kendinden imzalı** bir sertifika kullanıyorsa gerekir. AdaPanel kurulumu
  `panel.pfx`'i kendisi üretir; gerçek bir sertifika koymadıysanız cURL bağlantıyı reddeder.
  Gerçek sertifika varsa bu bayrağı **kullanmayın**.

Alanın tamamında `=` yoksa değer doğrudan giriş yolu sayılır (en sık kullanım).

---

## 2. Ürün (paket) yapılandırması

Ürünü oluşturup **Module Settings** sekmesinde `AdaPanel` modülünü seçtiğinizde şu alanlar çıkar:

| Alan | Anlamı |
|---|---|
| Servis planı kimliği | Doldurulursa abonelik panelde **plana bağlanır** ve aşağıdaki limitler yok sayılır |
| En fazla site | `0` = sınırsız |
| Disk kotası (MB) | `0` = sınırsız |
| En fazla veritabanı | `0` = sınırsız |
| En fazla posta kutusu | `0` = sınırsız |
| İptalde siteleri de sil | İşaretlenirse iptalde siteler **dosyaları ve eşleşen veritabanlarıyla** silinir |

### Plan bağı mı, tekil limitler mi?

Panelde servis planı bir **şablon değil kalıcı bağdır** (`ServicePlanDto` belgesi): limitler okuma
anında plandan çözülür, yani planı değiştirmek ona bağlı **bütün** abonelikleri aynı anda
değiştirir. WHMCS'te aynı paketi 200 müşteriye satıyorsanız plan bağı kullanın — aksi hâlde
"Başlangıç paketinin diskini 5 GB yap" demek 200 hizmeti tek tek düzenlemek olur.

**Plan kimliğini nerede bulurum:** panelde **sol menü → Müşteriler & Bayiler** ekranının üstündeki
servis planı kartlarında. Kimliği doğrudan görmek isterseniz `GET /api/v1/service-plans` yanıtındaki
`data[].id` alanı.

Plan kimliği **boş** bırakılırsa modül `ChangePackage` sırasında önce plan bağını kaldırır
(`planId: null`), sonra tekil limitleri yazar. İkisi karıştırılmaz: plan bağı dururken tekil limit
yazmak sessizce etkisiz kalırdı.

---

## 3. Panel tarafında ön koşullar

1. **Ayrı bir yönetici hesabı açın** (örn. `whmcs-provision`), rolü **Administrator**.
2. O hesapta **iki adımlı doğrulama KAPALI** olmalı. Panel `POST /api/v1/auth/login` yanıtında
   `requiresTwoFactor: true` dönerse modül devam edemez ve bunu açıkça söyler.
3. Parola en az 12 karakter (panelin kendi kuralı).
4. Panelde **IP kısıtlaması** kullanıyorsanız (**sol menü → Güvenlik**) WHMCS sunucusunun çıkış
   IP'sini izinli listeye ekleyin. Aksi hâlde her çağrı `403 security.ip_not_allowed` alır.
5. **WHMCS'in ürettiği parola en az 12 karakter olmalı.** Panel `CreateUserRequest.Password` için
   `MinimumLength = 12` ister. Daha kısa parolayla gelen `CreateAccount`, hesap açmak yerine bunu
   söyleyen bir hata döndürür (sessizce başka parola üretmez — o zaman müşteriye e-postayla giden
   parola ile paneldeki parola farklı olurdu).

---

## 4. Modül hangi WHMCS olayında ne yapıyor?

| WHMCS işlevi | Panelde yaptığı |
|---|---|
| `CreateAccount` | `POST /api/v1/users` (rol `Customer`, kotalar, alan adı `assignedSites`'a eklenir) + plan kimliği doluysa `PUT /api/v1/subscriptions/{id}/plan` |
| `SuspendAccount` | `POST /api/v1/subscriptions/{id}/suspend` |
| `UnsuspendAccount` | `POST /api/v1/subscriptions/{id}/resume` |
| `TerminateAccount` | `DELETE /api/v1/users/{id}?removeSites=true|false` |
| `ChangePackage` | Plan varsa `PUT /api/v1/subscriptions/{id}/plan`; yoksa plan bağını kaldırıp `PUT /api/v1/users/{id}` ile limitleri yazar |
| `TestConnection` | Giriş + `GET /api/v1/users` + `GET /api/v1/service-plans` |
| `AdminServicesTabFields` | `GET /api/v1/subscriptions/{id}` (salt okunur kullanım özeti) |

### Hesap açarken site kurulmaz — bilerek

Modül yalnızca **hesabı** açar ve alan adını müşterinin kapsamına ekler. IIS sitesini kurmaz.

Sebep koddan geliyor: `POST /api/v1/sites` yönetici olarak çağrıldığında fiziksel yol zorunludur ve
oluşan site **oluşturana atanmaz** (`AssignSiteToActorAsync` yalnız yönetici *olmayan* istekte
çalışır). Yani yöneticiyle site açmak, sahipliği ayrıca düzeltmeyi gerektiren yarım bir iş olurdu.

Buna karşılık alan adını `assignedSites`'a önceden yazmak **alan adını o müşteriye rezerve eder**:
panel site adını ve host adını bir sahiplik iddiası sayar
(`SitesController.CreateSiteAsync → CustomerAccess.EnsureDomainClaimAsync`), böylece başka bir
kiracı o adı alamaz ve müşteri siteyi panelden kendisi açabilir.

### Askıya alma müşteriyi panelden atmaz

Panelde askı **girişi kapatmaz, siteleri durdurur** (`ISubscriptionEnforcementService` belgesi):
müşteri panele girip durumunu görsün, kotasını okusun, ödemesini yapıp çözsün diye. Bu bilinçli bir
ürün kararı; modül onu değiştirmeye çalışmaz.

---

## 5. Bilinen sınırlar (panelde uç yok — uydurulmadı)

### 5.1 `ChangePassword` çalışmıyor

Ölçüldü:

* `PUT /api/v1/users/{id}` gövdesi (`UpdateUserRequest`) **parola alanı taşımaz**.
* `POST /api/v1/auth/change-password` **mevcut parolayı** ister (`ChangePasswordRequest.CurrentPassword`,
  `[Required]`) ve bilerek yalnızca çerez şemasını kabul eder — API anahtarıyla parola
  değiştirilememesi kasıtlı bir güvenlik kararıdır (`AuthController` belgesi).
* `POST /api/v1/auth/password/forgot` parola **belirlemez**, e-posta gönderir.

Modül bu yüzden açıkça başarısız olur. Sessizce `success` dönmek en kötü seçenek olurdu: WHMCS
parolanın değiştiğini yazar, müşteri eski parolayla kalır ve kimse fark etmez.

**Panele eklenmesi gereken uç:**

```
POST /api/v1/users/{userId}/password        [Authorize(Roles = "Administrator,Reseller")]
Body: { "newPassword": "..." }              (en az 12 karakter)
```

Not: aynı akış zaten alt kullanıcılar için var — `UserManagementService.UpdateSubUserAsync` parolayı
değiştirir **ve** `apiTokenService.RevokeAllForUserAsync` ile o kullanıcının API anahtarlarını
düşürür. Yeni uç aynı diskini izlemeli.

### 5.2 `ServiceSingleSignOn` çalışmıyor

`POST /api/v1/auth/impersonate/{id}` yöneticinin **kendi tarayıcı oturumunu** hedef kullanıcıya
çevirir: yanıtta `Set-Cookie` döner. WHMCS bu çağrıyı sunucudan sunucuya yaptığı için çerez WHMCS
sunucusunda kalır; müşterinin tarayıcısına taşınamaz ve zaten başka bir alan adı için geçerli
olmaz (`SameSite=Strict`, `HttpOnly`).

**Panele eklenmesi gereken uç:** tek kullanımlık, kısa ömürlü bir giriş belirteci üreten bir uç —
panelde bunun kanıtlanmış bir örneği zaten var: `WordPressController.CreateLoginLinkAsync`
("tek kullanımlık ve kısa ömürlü sihirli bağlantı") ve `WebDbController` signon URL'leri.

```
POST /api/v1/auth/sso-link/{userId}         [Authorize(Roles = "Administrator")]
-> { "url": "https://panel:8888/<gizli-yol>/sso?token=<tek-kullanimlik>" }
```

### 5.3 API anahtarı (`Bearer adap_…`) REST uçlarında çalışmıyor

Bu, modülün neden kullanıcı adı/parola ile giriş yaptığının sebebidir ve denetimde bulunan gerçek
bir uyumsuzluktur:

* `Program.cs` `AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)` ile
  **varsayılan şemayı çerez** yapıyor.
* `PanelTokenAuthenticationHandler` (Bearer) kayıtlı, ama **yalnızca** `/mcp` uçuna bağlanmış:
  `app.MapMcp("/mcp").RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = PanelToken })`.
* `/api/v1/**` denetleyicilerindeki `[Authorize]` nitelikleri şema belirtmiyor → varsayılan (çerez)
  şema kullanılır → `Authorization: Bearer …` başlığı **hiç okunmaz**.

Panelde `AddAuthorization` içinde iki şemayı da kabul eden bir varsayılan politika tanımlanırsa
(ya da `POST /api-tokens` ile üretilen anahtar denetleyicilerde kabul edilirse) modül parolasız
çalışabilir ve aşağıdaki oran sınırı sorunu da ortadan kalkar.

### 5.4 Giriş oranı sınırı

`POST /api/v1/auth/login` `EnableRateLimiting("login")` ile korunuyor: **IP başına dakikada 5**
istek. Modülün her WHMCS işlevi bir kez giriş yapar. Normal provisioning hacminde sorun çıkmaz,
ama toplu bir işlemde (örn. 20 hizmeti aynı anda askıya alma) bir kısmı `429` alabilir; WHMCS'te
işlemi tekrar denemek yeterlidir.

---

## 6. Sorun giderme

| Belirti | Sebep |
|---|---|
| `Panel JSON olmayan bir yanit dondurdu` | Adres/port yanlış ya da o portta panel yok |
| `Gizli giris yolu yanlis…` | Access Hash alanındaki `entry` değeri panelin gizli giriş yoluyla aynı değil |
| `WHMCS sunucusunun IP adresi… izinli IP listesinde degil` | Panel → Güvenlik → izinli IP listesine WHMCS sunucusunu ekleyin |
| `Panele baglanilamadi: SSL certificate problem…` | Access Hash alanına `;insecure=1` ekleyin ya da panele gerçek sertifika koyun |
| `Panel hesabinda iki adimli dogrulama acik` | Provisioning hesabında 2FA'yı kapatın |
| `Bu islem icin panel hesabinin yetkisi yok` | Hesap `Administrator` rolünde değil |
| `AdaPanel en az 12 karakterlik parola ister` | WHMCS'in otomatik parola uzunluğunu 12+ yapın |
| `Panelde "…" adli kullanici bulunamadi` | Panel hesabı elle silinmiş; WHMCS hizmetini yeniden oluşturun |

Modül panel kullanıcısını **kullanıcı adıyla** bulur (`GET /api/v1/users` içinde arayarak); WHMCS
tarafında panel kimliği (GUID) saklanmaz. Bu yüzden panelde kullanıcı adını elle değiştirmek
hizmetin bağını koparır.
