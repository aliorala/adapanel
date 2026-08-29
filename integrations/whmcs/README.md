# AdaPanel — WHMCS sunucu (provisioning) modülü

WHMCS'te satılan bir barındırma ürününü AdaPanel'e bağlar: sipariş ödendiğinde panelde müşteri
hesabı açılır, ödeme gecikince askıya alınır, iptal edilince silinir.

> Bu belgedeki her uç, panelin C# denetleyicileri **okunarak** doğrulandı. Doğrulanamayan hiçbir
> davranış yazılmadı. Bir süre "panelde karşılığı yok" diye işaretli duran iki işlev
> (`ChangePassword` ve `ServiceSingleSignOn`) artık **çalışıyor**: panele karşılıkları eklendi.
>
> **Sonradan eklenenler:** `UsageUpdate` (disk/trafik kullanımı → fatura, bölüm 6), iptal öncesi
> **silme doğrulaması** (bölüm 8), **iptalde sunucuda kalanların not olarak yazılması** (bölüm 8.1)
> ve **API anahtarıyla bağlanma** (bölüm 5.5). Kullanılan WHMCS sözleşmelerinin **hepsi**
> `developers.whmcs.com` üzerinden doğrulandı; kullanılan sayfalar `adapanel.php` başında adres
> adres yazılı.
>
> **PHP tarafı artık gerçekten çalıştırılıyor** — tek komut:
>
> ```
> powershell -ExecutionPolicy Bypass -File scripts\run-whmcs-tests.ps1
> ```
>
> Betik PHP'yi arar (önce panelin kendi kurduğu sürüm), `php -l` ile sözdizimini ölçer ve iki
> davranış testini koşar. **PHP bulamazsa sessizce yeşil geçmez**, hata koduyla durur.
>
> | Test | Neyi ölçer |
> |---|---|
> | `tests/leftover-note.test.php` | Silme kalıntılarının müşteri notuna yazılması (`localAPI`/`logModuleCall` casus) |
> | `tests/module-contract.test.php` | **Sahte panele gerçek HTTP**: yol, metot, başlık, alan adları, hata yolları |
>
> İkincisi `AdaPanelClient`'ı mock'lamaz: `php -S` ile ayağa kalkan bir sahte panel (`fake-panel.php`)
> modülün gerçek curl isteklerini kaydeder ve test o kayda bakar. Uçlar ve alan adları panelin C#
> denetleyicileriyle karşılaştırıldı.
>
> **Hâlâ ölçülmeyen tek şey:** gerçek bir WHMCS sürümünün bu işlevleri beklediğimiz parametrelerle
> çağırdığı (bu depoda WHMCS+MySQL yığını yok). İlk kurulumda **Test Connection** ve bir deneme
> siparişiyle doğrulayın.

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

4. **Test Connection** düğmesine basın. Modül **dört** şeyi birden ölçer: giriş yapılabiliyor mu,
   sağlama yetkisi var mı (`GET /api/v1/users`), servis planları okunabiliyor mu
   (`GET /api/v1/service-plans`), **faturalama kapısı açık mı** (`GET /api/v1/billing/settings`).
   Dördü de geçmeden ürünü satışa açmayın.

   Dördüncüsü sonradan eklendi ve sebebi ölçüldü: ilk üç uç **Administrator ve Reseller** rollerine
   açıktır, oysa `BillingController` **yalnız Administrator**'a. Bir **bayi** hesabıyla kurulan
   sunucu tanımı eskiden "bağlantı başarılı" diyordu; sonra her hizmet iptali `403` ile
   reddediliyor ve disk/trafik kullanımı **hiç güncellenmiyordu** — üstelik sessizce, çünkü WHMCS
   `UsageUpdate`'in sonucunu arayüzde göstermez. Aynı uç, panelde **Panel → Faturalama** anahtarı
   kapalıyken de `403 billing.disabled` döner; bu da artık kurulum anında söyleniyor.

### Access Hash alanı — iki ayarı birden taşır

Panel `/api` altındaki **her** isteği `X-AdaPanel-Entry-Path` başlığıyla doğrular
(`SecureEntryPathMiddleware`). Başlık yanlışsa panel `404 security.invalid_entry_path` döner —
dışarıdan "modül çalışmıyor" gibi görünür. WHMCS'in sunucu tanımında başka serbest alan olmadığı
için bu bilgi **Access Hash** alanında taşınır:

```
panel                                        # yalnız gizli giriş yolu
entry=panel;insecure=1                       # gizli giriş yolu + sertifika doğrulamasını atla
entry=panel;token=adap_xxxxxxxx              # panel API anahtarıyla kimlik (önerilen)
entry=panel;token=adap_xx;admin=whmcsadmin   # + kalıntı notlarını yazacak WHMCS yöneticisi
```

* `entry` — panelde **sol menü → Güvenlik → gizli giriş yolu** alanındaki değerle **birebir** aynı
  olmalı (varsayılan `panel`).
* `insecure=1` — panel **kendinden imzalı** bir sertifika kullanıyorsa gerekir. AdaPanel kurulumu
  `panel.pfx`'i kendisi üretir; gerçek bir sertifika koymadıysanız cURL bağlantıyı reddeder.
  Gerçek sertifika varsa bu bayrağı **kullanmayın**.
* `token` — panel API anahtarı (`adap_…`). Verilirse modül **giriş yapmaz**, her isteği
  `Authorization: Bearer` başlığıyla gönderir. Bkz. bölüm 5.5.
* `admin` — **WHMCS yönetici kullanıcı adı**. Modül, iptalde sunucuda kalanları WHMCS'e not olarak
  yazarken Local API'yi kullanır ve bu API'nin imzası `localAPI($command, $values, $adminuser)`'dır.
  Üçüncü parametrenin boş bırakılabildiği WHMCS belgelerinde **yazmıyor**, o yüzden tahmin
  edilmiyor: burada bir yönetici adı verin. Boş bırakılırsa modül yine dener, **sonucu ölçer** ve
  yazamazsa not metnini modül günlüğüne düşürür (bkz. bölüm 8.1).

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
| `TerminateAccount` | Önce `GET /api/v1/billing/terminate-check` (silme **güvenli mi**), sonra `DELETE /api/v1/users/{id}?removeSites=true|false`; yanıttaki **kalıntılar** müşteri notuna yazılır (bkz. 8.1) |
| `UsageUpdate` | `GET /api/v1/billing/usage` → `tblhosting` satırlarına disk/trafik yazar |
| `ChangePackage` | Plan varsa `PUT /api/v1/subscriptions/{id}/plan`; yoksa plan bağını kaldırıp `PUT /api/v1/users/{id}` ile limitleri yazar |
| `TestConnection` | Giriş + `GET /api/v1/users` + `GET /api/v1/service-plans` + `GET /api/v1/billing/settings` |
| `AdminServicesTabFields` | `GET /api/v1/subscriptions/{id}` (salt okunur kullanım özeti) |
| `ChangePassword` | `POST /api/v1/auth/password/set/{id}` — gövde `{ "newPassword": "…" }` |
| `ServiceSingleSignOn` | `POST /api/v1/auth/sso-link/{id}` → `data.url` adresine yönlendirir |

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

## 5. Parola belirleme ve tek tıkla giriş

### 5.1 `ChangePassword` — `POST /api/v1/auth/password/set/{userId}`

Gövde: `{ "newPassword": "…" }` (en az 12 karakter — `PasswordPolicy.MinimumLength`).
Yetki: **Administrator** herkes için; **Reseller** yalnız **kendi** müşterisi için.

Bu uç, kullanıcının kendi parolasını değiştirdiği `change-password` ucundan ayrıdır: **mevcut
parolayı sormaz** (faturalama sistemi zaten bilmiyor), yetkiyi çağıran hesabın rolünden ve
kapsamından alır.

Bilinmesi gereken yan etkiler:

* **Hedefin açık panel oturumları düşer.** Oturum çerezinin kimlik damgası parola karmasından
  türetilir (`CookieSessionValidator`); karma değişince eski çerez en geç bir dakika içinde
  reddedilir.
* **Hedefin API anahtarları iptal edilir** (`RevokeAllForUserAsync`). Anahtar paroladan bağımsız bir
  kimlik bilgisidir; düşürülmezse parolası sıfırlanan kişi MCP/REST üzerinden erişimini sürdürürdü.
  Panel kaç anahtarın düştüğünü `data.revokedApiTokens` ile döner.
* **Hesap kilidi açılır** (hatalı giriş sayacı sıfırlanır).
* Denetim kaydına `auth.password_set_by_admin` yazılır — **parolanın kendisi kayda geçmez**.

Hesabın **kendi** kimliğiyle bu uç kullanılamaz (403 `users.forbidden`): mevcut parolayı sormayan
bir uç kendi hesabına da açık olsaydı, çalınmış bir çerez parolayı hiç bilmeden hesabı kalıcı olarak
devralabilirdi. Kendi parolanız için `POST /api/v1/auth/change-password`.

### 5.2 `ServiceSingleSignOn` — `POST /api/v1/auth/sso-link/{userId}`

Yanıt: `data.url` + `data.expiresAtUtc`. Modül müşterinin tarayıcısını `data.url` adresine
yönlendirir; adres belirteci oturum çereziyle takas eder ve panele geçirir.

* Belirteç **tek kullanımlıktır** ve ömrü **dakikalarla** ölçülür. Sebebi: belirteç adreste gittiği
  için panel istek günlüğüne, önündeki ters vekilin günlüğüne ve müşterinin tarayıcı geçmişine
  **düşer**. Bunu ortadan kaldıramayız (tarayıcı gezintisi özel başlık gönderemez), ama
  değersizleştirebiliriz: günlüğü sonradan okuyan biri çoktan tüketilmiş, süresi geçmiş bir dize
  bulur. **Bağlantıyı saklamayın, kaydetmeyin, e-postayla göndermeyin.**
* Belirteç sunucuda **yalnız SHA-256 özetiyle** saklanır (`config/sso-tokens.json`).
* Yeni bağlantı üretmek eskisini geçersiz kılar.
* **Askıdaki hizmet için de bağlantı üretilir.** Panelde askı girişi değil siteleri kapatır; müşteri
  panele girip neden askıda olduğunu görebilmeli. Tam da ödemesi geciken müşteri için "Panele git"
  düğmesini ölü bırakmak, askının amacına ters düşerdi.
* **Pasif hesap için üretilmez** (`auth.user_inactive`): normal giriş yolu pasif kullanıcıyı zaten
  reddediyor, burada üretmek hesabı kapatmanın etrafından dolaşan ikinci bir kapı olurdu.
* Kullanım ucu (`GET /api/v1/auth/sso`) gizli giriş yolu başlığından **muaftır** — onu tarayıcı
  gezer ve özel başlık ekleyemez. IP kısıtlaması bu uçta da uygulanır.
* Denetim kaydı: `auth.sso_link.created`, `auth.sso_link.redeemed`, `auth.sso_link.rejected`.
  **Belirteç kayda geçmez.**

### 5.3 Modül neden kullanıcı adı/parola ile giriş yapıyor?

Panelin `Bearer adap_…` API anahtarı artık `/api/v1/**` uçlarında **çalışıyor**
(`PanelAuthentication` yönlendirici şeması), ama **kimlik bilgisi yöneten uçlar bilerek dışarıda**:
`AuthController` ve `ApiTokensController` sınıf düzeyinde **çerez şemasına** sabitlenmiştir. Sebep
yetki yükseltmesi — bir anahtarla başka bir yöneticinin parolası belirlenebilseydi ya da yeni
anahtar basılabilseydi, anahtarın kısıtı kâğıt üzerinde kalırdı.

Yukarıdaki iki işlev tam olarak o uçları kullandığı için modül çerez oturumu açar. Anahtar
kullanmak isterseniz sağlama/askı/iptal işlevleri anahtarla da çalışır, ama parola belirleme ve tek
tıkla giriş için yönetici hesabı gerekir.

### 5.4 Giriş oranı sınırı

`POST /api/v1/auth/login` `EnableRateLimiting("login")` ile korunuyor: **IP başına dakikada 5**
istek. Modülün her WHMCS işlevi bir kez giriş yapar. Normal provisioning hacminde sorun çıkmaz,
ama toplu bir işlemde (örn. 20 hizmeti aynı anda askıya alma) bir kısmı `429` alabilir; WHMCS'te
işlemi tekrar denemek yeterlidir.

### 5.5 API anahtarı ile bağlanmak (önerilen)

Panelin `Bearer adap_…` anahtarı artık `/api/v1/**` uçlarında **çalışıyor**
(`PanelAuthentication` yönlendirici şeması). Anahtar yolunun üç somut faydası var:

* **Giriş oranı sınırına takılmaz.** `POST /auth/login` IP başına dakikada 5 istekle sınırlı;
  anahtar yolu hiç giriş yapmaz.
* **CSRF istemez** — `Authorization` başlığını tarayıcı kendiliğinden göndermez.
* **Gizli giriş yolu başlığından muaftır** (`SecureEntryPathMiddleware.CarriesApiToken`), yani
  yönetici gizli yolu değiştirdiğinde entegrasyon sessizce kırılmaz.

**Anahtar nasıl üretilir:** panelde **sol menü → Yapay Zeka** ekranı → yeni anahtar.

**Hangi yetki gerekir:**

| Kullanım | Gereken anahtar |
|---|---|
| Yalnız `UsageUpdate` (kullanım toplama) | **Salt okur** anahtar yeter — hepsi `GET` |
| Sağlama (`CreateAccount`, `Suspend`, `Terminate`, `ChangePackage`) | "**Yıkıcı işlemlere izin ver**" işaretli anahtar |

Salt-okur anahtarla yazma denenirse panel `403 auth.forbidden` + `apitoken.read_only` döner.

**İki uç anahtar KABUL ETMEZ** — ama artık bu sizin sorununuz değil. `AuthController` sınıf
düzeyinde çerez şemasına sabitlenmiştir (yetki yükseltmesini önlemek için), yani
`POST /auth/password/set/{id}` ve `POST /auth/sso-link/{id}` `Bearer` başlığıyla `401` döner.
Modül bunu **kendisi hallediyor**: `/api/v1/auth/**` çağrılarında anahtarı bir kenara bırakır ve
sunucu tanımındaki kullanıcı adı/parolayla çerezli oturum açar; diğer bütün uçlarda anahtarı
kullanmaya devam eder. Bu yüzden **Username/Password alanlarını token kullanırken de doldurun** —
boş bırakılırsa `ChangePassword` ve `ServiceSingleSignOn` eksik ayarı adıyla söyleyip durur.

> Bu davranış 0.8.x'te düzeltildi. Öncesinde kısıt yalnızca kaynak yorumlarında yazılıydı, kodda
> uygulanmıyordu: token yapılandırılmış her kurulumda o iki işlev **sessizce ölüydü** ve modülün
> hata metni ("oturum açılmamış ya da anahtar geçersiz/iptal edilmiş") yöneticiyi sağlam bir
> anahtarı yenilemeye gönderiyordu. Nöbetçi: `tests/module-contract.test.php` bölüm 15.

---

## 6. Kullanım verisi (`UsageUpdate`) — disk ve trafik

### 6.1 Sözleşme

WHMCS bu işlevi **sunucu başına** çağırır, ürün/hizmet başına DEĞİL: `$params` yalnız sunucu
bilgilerini taşır (`serverid`, `serverhostname`, `serverusername`, …) ve **hizmet listesi
verilmez**. Bu yüzden modül panelden **tek bir toplu rapor** çeker
(`GET /api/v1/billing/usage`) ve satırları eşleştirir. Hizmet başına istek atmak, 500 müşterili bir
sunucuda cron turunu 500 HTTP çağrısına çevirirdi.

Yazılan sütunlar: `tblhosting.diskusage`, `disklimit`, `bwusage`, `bwlimit`, `lastupdate`.

### 6.2 Eşleştirme `domain` ile değil `username` ile

WHMCS'in resmî örneği satırları **alan adıyla** eşleştirir. Bu modül **kullanıcı adıyla**
eşleştirir, çünkü panelde bir hesabın **birden çok sitesi** olabilir; alan adıyla eşleştirmek aynı
hesabın toplam kullanımını her satıra yazıp kullanımı **katlardı**. Kullanıcı adı hizmet ömrü
boyunca değişmez ve modülün geri kalanı da hesabı bu adla buluyor.

> Sonuç olarak: **panelde kullanıcı adını elle değiştirmeyin.** Değiştirirseniz o hizmetin
> kullanımı bir daha güncellenmez (ve askı/iptal de çalışmaz).

### 6.3 Birimler

Panel değerleri **MB** olarak verir; çeviri panelde yapılır (`BillingUnits`). WHMCS `tblhosting`
sütunları da MB'dir, yani modülde hiçbir bölme/çarpma yoktur — orada yapılacak tek bir birim hatası
müşteriye yanlış fatura demektir. `0` her iki tarafta da **sınırsız** anlamına gelir.

### 6.4 Ölçülemeyen trafik yazılmaz

Panel bir sitenin trafiğini okuyamazsa (IIS günlüğü kapalı, klasör erişilemez) raporda
`bandwidthMeasured: false` döner ve modül **o satırın `bwusage` sütununa dokunmaz**; eski değer
korunur. Sebebi: ölçemediğimiz bir ayı "0 MB" diye yazmak, aşım faturalamasını sessizce sıfırlar ve
o ayın gerçek kullanımını da silerdi. Panelin abonelik motoru da aynı sebeple ölçülemeyen trafikte
kota **uygulamaz**.

Kaç aboneliğin ölçülemediği panelde **Faturalama** ekranında uyarı olarak görünür.

---

## 7. Panelde faturalama entegrasyonunu açmak

Faturalama uçları (`/api/v1/billing/**`) panelde **varsayılan olarak KAPALIDIR**. Açmak için:

**sol menü → Faturalama → "Entegrasyon açık" → Kaydet**

Kapalıyken `UsageUpdate` ve `TerminateAccount` `billing.disabled` hatası alır. Bu anahtar
doğrulamasının *yerine* değil *üstüne* binen ikinci kapıdır: sızmış geçerli bir anahtar olduğunda
yöneticinin elinde anahtarları tek tek iptal etmeden entegrasyonu kesecek bir düğme olmalıdır.

Aynı ekranda görünürler:

* son başarılı çağrının zamanı ve rapora giren hesap sayısı,
* **son 20 çağrı** (işlem, sonuç, ayrıntı) — "entegrasyon neden çalışmıyor" sorusunun ilk bakılacak
  yeri,
* WHMCS'e verilen kullanım değerlerinin **aynısı** (fatura tartışmasında panel ile faturalama
  sisteminin aynı sayıyı söylediği buradan doğrulanır).

---

## 8. İptal (`TerminateAccount`) artık önce doğrulanıyor

`TerminateAccount` veri siler; `removeSites` işaretliyse siteler **dosyaları ve eşleşen
veritabanlarıyla** yok edilir. Modül eskiden hesabı yalnızca **kullanıcı adıyla** bulup doğrudan
siliyordu — ad panelde elle değiştirilmiş ya da başka bir role ait bir hesapla çakışmışsa iptal
edilen bir hizmet **başka birinin verisini** yok edebilirdi.

Artık modül silmeden önce `GET /api/v1/billing/terminate-check?userName=…&domain=…` çağırır. Panel
**varsayılan olarak reddeder** ve yalnız şu üç koşul birden sağlanırsa onay verir:

1. Ada uyan **tam olarak bir** hesap var,
2. hesabın rolü **Customer** (yönetici/bayi/operatör asla silinmez),
3. hizmetin alan adı hesabın kapsamında (`www.` ve büyük/küçük harf farkı yok sayılır).

Onay gelmezse modül **silmez** ve WHMCS'e sebebi açıklayan bir hata döndürür (bilerek `success`
dönmez; aksi hâlde WHMCS hizmeti iptal edilmiş sayar ve kimse bir daha bakmazdı):

| Sebep | Anlamı |
|---|---|
| `not_found` | Panelde o adda hesap yok (elle silinmiş olabilir) |
| `ambiguous` | Ada uyan birden çok hesap var; hangisi olduğu kanıtlanamıyor |
| `role_not_customer` | Hesap müşteri değil |
| `domain_mismatch` | Hizmetin alan adı hesabın kapsamında değil |

Alan adı olmayan ürünlerde (yalnız hesap satan paketler) 3. koşul uygulanmaz; 1. ve 2. koşul sürer.

### 8.1 İptalde **sunucuda kalanlar** artık WHMCS'e yazılıyor

**Kapatılan boşluk:** modülün varsayılanı `removeSites=false`'tur, yani siteler **bilerek**
sunucuda bırakılır. O yolda panel `200 OK` + `success: true` döner — bu "her şey silindi" **demek
değildir**. Panel ne kaldığını yanıt gövdesinde `data.leftovers` ile **adıyla** sayar (hangi site,
hangi veritabanı, hangi klasör, neden kaldı, bilerek mi). Modül gövdeyi okumadığı için bu liste
WHMCS tarafında **hiç görünmüyordu**: panelde kalıntılar duruyor, yönetici hizmeti iptal ediyor ve
sunucuda ne kaldığını hiç öğrenmiyordu.

Artık modül **her iki yolda da** gövdeyi okur:

| Panel yanıtı | Anlamı | Modül ne yapar |
|---|---|---|
| `200` + `success:true`, `leftovers` boş | Temiz silme | WHMCS'e `success`, **not yazılmaz** |
| `200` + `success:true`, `leftovers` dolu | Siteler **bilerek** bırakıldı | WHMCS'e `success` + **müşteri notu** |
| `409 users.delete.incomplete` | İş **yarım** kaldı | Hizmet iptal edilmiş sayılmaz (hata döner) + **müşteri notu** |

**Not nereye yazılır:** `AddClientNote` (WHMCS Local API) ile **müşterinin** notlarına, `sticky`
işaretiyle — yani not müşterinin hesabında ve açtığı biletlerde görünür. `UpdateClientProduct`'ın
`notes` alanı **bilerek kullanılmadı**: o alan hizmetin mevcut yönetici notunun **üzerine yazar** ve
başka birinin yazdığı notu sessizce yok ederdi. `AddClientNote` yeni bir kayıt **ekler**, hiçbir
şeyi silmez.

Notun içeriği: panel hesabı, WHMCS hizmet numarası, alan adı, `removeSites` seçeneğinin değeri,
gerçekten silinen siteler, işin eksiksiz tamamlanıp tamamlanmadığı ve **kalıntı satırları**. Her
satırda kaynağın türü, **adı** ve **neden kaldığı** yazılıdır; ayrıca `[bilerek bırakıldı]` /
`[BAŞARISIZ]` ayrımı vardır — yönetici neyin karar, neyin arıza olduğunu ayırt edebilmeli.

**Not yazılamazsa ne olur:** çağrının sonucu **ölçülür**. Yazılamayan bir not, hiç yazılmamış
nottan kötüdür çünkü yazıldığı sanılır. Başarısızlıkta **notun tam metni** modül günlüğüne düşer
(`Utilities → Logs → Module Log`, işlem adı `TerminateAccount.note_failed`). Kalıntı listesi ayrıca
**her zaman** modül günlüğüne yazılır (`TerminateAccount.leftovers`) — not silinse bile iz kalır.

> Local API'nin admin kullanıcı adını **Access Hash** alanındaki `admin=…` ile verin (bölüm 1).
> Verilmezse çağrı yine denenir ama WHMCS reddedebilir; o zaman not yalnız modül günlüğünde kalır.

**Kalıntılar sunucuda DURUYOR.** Not bir bilgilendirmedir, temizlik değildir; panelden elle
temizleyin.

---

## 9. Sorun giderme

| Belirti | Sebep |
|---|---|
| `Panel JSON olmayan bir yanit dondurdu` | Adres/port yanlış ya da o portta panel yok |
| `Gizli giris yolu yanlis…` | Access Hash alanındaki `entry` değeri panelin gizli giriş yoluyla aynı değil |
| `WHMCS sunucusunun IP adresi… izinli IP listesinde degil` | Panel → Güvenlik → izinli IP listesine WHMCS sunucusunu ekleyin |
| `Panele baglanilamadi: SSL certificate problem…` | Access Hash alanına `;insecure=1` ekleyin ya da panele gerçek sertifika koyun |
| `Panel hesabinda iki adimli dogrulama acik` | Provisioning hesabında 2FA'yı kapatın |
| `Bu islem icin panel hesabinin yetkisi yok` | Hesap `Administrator` rolünde değil |
| `AdaPanel en az 12 karakterlik parola ister` | WHMCS'in otomatik parola uzunluğunu 12+ yapın |
| `Bu kullanici uzerinde yetki yok…` | Bayi hesabıyla **başka** bir bayinin müşterisine işlem yapılıyor |
| `Panel hesabi PASIF…` | Tek tıkla giriş pasif hesap için üretilmez; önce hizmeti aktifleştirin |
| Giriş bağlantısı "geçersiz/süresi dolmuş" sayfası açıyor | Bağlantı tek kullanımlık ve dakikalık ömürlü; kaydedip sonra açmayın, düğmeye yeniden basın |
| `Panelde "…" adli kullanici bulunamadi` | Panel hesabı elle silinmiş; WHMCS hizmetini yeniden oluşturun |
| `Panelde faturalama entegrasyonu KAPALI` | Panel → Faturalama → "Entegrasyon açık" işaretleyin (bkz. bölüm 7) |
| Kullanım hep `0 MB` görünüyor | Panel → Faturalama ekranındaki uyarıya bakın: trafik ölçülemiyorsa (IIS günlüğü kapalı) `bwusage` bilerek yazılmaz |
| Kullanım hiç güncellenmiyor | Panelde kullanıcı adı elle değiştirilmiş olabilir — eşleştirme `tblhosting.username` üzerinden yapılır (bkz. 6.2) |
| İptal `Panel silme denetimi gecilemedi` diyor | Silme bilerek durduruldu; tablodaki sebebe göre panelde rolü/site atamasını düzeltin (bkz. bölüm 8) |
| Anahtar kullanırken `auth.forbidden` + `apitoken.read_only` | Sağlama işlemleri için anahtarın "yıkıcı işlemlere izin ver" yetkisi açık olmalı (bkz. 5.5) |
| İptalden sonra müşteriye **"sunucuda kalan kaynaklar"** notu düştü | Beklenen davranış: `removeSites` kapalıysa siteler bilerek bırakılır. Not neyin kaldığını adıyla söyler; panelden elle temizleyin (bkz. 8.1) |
| İptal `Panel kullaniciyi sildi ama sunucuda temizlenemeyen kaynaklar KALDI` diyor | İş **yarım** kaldı (`409`). Hizmeti tamamen kaldırılmış saymayın; listedeki kaynakları panelden temizleyip WHMCS'te elle kapatın |
| Kalıntı notu müşteride görünmüyor | Local API çağrısı reddedilmiş olabilir. `Utilities → Logs → Module Log` içinde `TerminateAccount.note_failed` kaydına bakın; notun tam metni oradadır. Access Hash alanına `admin=<whmcs yöneticisi>` ekleyin (bkz. 8.1) |

Modül panel kullanıcısını **kullanıcı adıyla** bulur (`GET /api/v1/users` içinde arayarak); WHMCS
tarafında panel kimliği (GUID) saklanmaz. Bu yüzden panelde kullanıcı adını elle değiştirmek
hizmetin bağını koparır.
