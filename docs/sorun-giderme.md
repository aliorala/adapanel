# Sorun giderme

Buradaki her madde koddan ya da kurulum betiklerinden doğrulanmıştır.

## Önce bakılacak yerler

| Kaynak | Yol |
|---|---|
| Panel günlükleri | `C:\ProgramData\AdaPanel\logs\adapanel-<tarih>.log` (14 gün saklanır) |
| Servis durumu | `Get-Service AdaPanel` |
| Tepsi programı | Bildirim alanındaki AdaPanel simgesi — panel **hiç açılmadığında** elde kalan tek araçtır: servis durumunu gösterir, yeniden başlatır, panel adresini söyler |
| Panelin kendi teşhisi | **Sol menü → Danışman** ve **Sunucu Araçları → Günlükler** |

---

## Panele hiç erişemiyorum

### 1. Servis çalışıyor mu?

```powershell
Get-Service AdaPanel
Start-Service AdaPanel
```

Servis çökerse Windows onu **5 sn / 10 sn / 30 sn** sonra kendi yeniden başlatır
(`scripts/install-service.ps1` → `sc failure`). Sürekli yeniden başlıyorsa günlüğe bakın.

### 2. Doğru adresi mi kullanıyorum?

Panel kök adreste açılmaz. Adres `https://<sunucu>:<port>/<gizli-yol>` biçimindedir. Gizli yolu
unuttuysanız tepsi simgesi panel adresini gösterir.

### 3. Port hangisi?

Kurulum portu servisin `ImagePath` değerinde durur:

```powershell
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\AdaPanel').ImagePath
```

Kurulum sihirbazının varsayılanı **8890**'dır (`appsettings.json` içindeki `8888` yalnız
yapılandırma varsayılanıdır; kurulum betiği portu `--urls` ile açıkça verir).

### 4. Yalnız sunucudan açılıyor, dışarıdan açılmıyor

İki olasılık:

* **Sertifika yok.** `C:\ProgramData\AdaPanel\certs\panel.pfx` ya da `panel.password` eksikse panel
  HTTPS kuramaz ve HTTP'ye düşer; `Panel:AllowHttpFallback` `false` iken dinleyici yalnızca
  `127.0.0.1` olur. Çözüm: `scripts\initialize-panel-certificate.ps1` betiğini yönetici olarak
  çalıştırıp servisi yeniden başlatın.
* **Güvenlik duvarı.** `scripts\configure-firewall.ps1 -Port <port>` çalıştırın; kurulumda bu adım
  *best-effort*'tur ve sessizce atlanmış olabilir.

### 5. Kendimi kilitledim (hiçbir yönetici giremiyor)

Sunucuda şu dosyayı oluşturun ve **servisi yeniden başlatın**:

```
C:\ProgramData\AdaPanel\config\recover-access.flag
```

Panel açılışta bütün giriş kilitlerini temizler, brute-force IP engellerini kaldırır, panelin kendi
**engelleme** güvenlik duvarı kurallarını siler ve bayrağı kendisi siler.

> Bu bayrak **parolayı sıfırlamaz**. Parolanızı unuttuysanız bir sonraki maddeye bakın.

### 6. Yönetici parolamı unuttum

**Veritabanını silmeyin.** `data\adapanel.db` panel kullanıcılarını, ayarlarını ve kurulu bileşen
kayıtlarını da taşır; silinirse hepsi gider. Doğru yol, parolayı sunucu konsolundan belirlemektir:

```powershell
# Komut istemini "Yönetici olarak çalıştır" ile açın
& "C:\Program Files\AdaPanel\AdaPanel.Api.exe" --parola-belirle
```

Komut yönetici hesaplarını listeler, hangisi olduğunu **size sordurur**, sonra yeni parolayı iki kez
sorar (ekrana yazılmaz). Doğrudan hedef vermek isterseniz:

```powershell
& "C:\Program Files\AdaPanel\AdaPanel.Api.exe" --parola-belirle yonetici
```

Ayrıntı ve güvenlik gerekçeleri: [Yönetici parolasını sunucudan
belirleme](parola-kurtarma.md).

---

## Güncellemeden sonra boş beyaz sayfa

Tarayıcı eski `index.html`'i tutup artık var olmayan `/assets/index-<eskihash>.js` dosyasını
istiyordur. **Ctrl+F5** ile sabit yenileyin.

Panel artık doğru başlıkları gönderir (`index.html` → `no-cache`, `/assets/*` → bir yıl
`immutable`), yani bu sorun yeni kurulumlarda tekrar etmemelidir. Sabit yenileme de çözmüyorsa
`C:\Program Files\AdaPanel\wwwroot` klasörünün **dolu** olduğunu doğrulayın; boşsa kurulum yarım
kalmış demektir, kurulum paketini yeniden çalıştırın.

---

## API'ye dışarıdan istek atıyorum, hep 404 dönüyor

`/api` altındaki her yol `X-AdaPanel-Entry-Path: <gizli-yol>` başlığı ister
(`SecureEntryPathMiddleware`). Başlık yoksa/yanlışsa panel `404 security.invalid_entry_path` döner.

Muaf olanlar yalnızca: `/api/v1/setup`, `/api/v1/health`, `/api/v1/dbtools`.

## POST/PUT/DELETE isteğim 400 `security.invalid_csrf_token` dönüyor

Panelde CSRF doğrulaması **genel**dir (`AutoValidateAntiforgeryToken`). GET dışı her istek
`X-CSRF-TOKEN` başlığı ve antiforgery çerezi ister.

Belirteç **oturum kimliğine bağlıdır**: `GET /api/v1/auth/csrf` ile alınan belirteç girişten sonra
geçersizleşir. Doğru sıra:

1. `GET /api/v1/auth/csrf`
2. `POST /api/v1/auth/login`
3. **Tekrar** `GET /api/v1/auth/csrf`
4. Asıl istekler

Çalışan örnek: `integrations/whmcs/adapanel/lib/AdaPanelClient.php`.

## API anahtarımla (`Bearer adap_…`) `/api/v1/...` çağırıyorum, 401 alıyorum

Beklenen davranış. `Program.cs` varsayılan kimlik şemasını **çerez** yapar; `PanelToken` (Bearer)
şeması **yalnızca `/mcp`** ucuna bağlanmıştır. REST denetleyicileri `Authorization` başlığını
okumaz.

## `403 security.ip_not_allowed`

Panelde izinli IP listesi açık ve isteğin geldiği IP listede değil.
**Sol menü → Güvenlik** ekranından ekleyin. Kural `/api` dâhil her yola uygulanır.

## `429` — çok fazla istek

Oran sınırları:

| Uç | Sınır |
|---|---|
| `POST /api/v1/auth/login` | IP başına dakikada **5** |
| `POST /api/v1/auth/change-password` | IP başına **15 dakikada 5** |
| `POST /api/v1/setup/initialize` | IP başına **10 dakikada 3** |
| `/mcp` | Anahtar başına dakikada **120** |

---

## Kurulum sihirbazı `403 setup.local_only` diyor

İlk kurulum **yalnızca sunucunun kendisinden** yapılabilir. Uzak masaüstüyle sunucuya bağlanıp
`https://localhost:<port>/<gizli-yol>` adresini açın.

## Ekranda çeviri yerine ham anahtar görüyorum (örn. `license.invalid`)

Sunucunun döndürdüğü mesaj anahtarının arayüz sözlüğünde karşılığı yok. `MessageKeyCoverageTests`
testi bu sınıfı kapatır; ham anahtar görüyorsanız o anahtar `frontend/src/i18n/tr.json` ve
`en.json` dosyalarına eklenmelidir.

## İki adımlı doğrulamaya erişemiyorum

Giriş ekranındaki kod alanına **kurtarma kodunuzu** yazın (etkinleştirme sırasında verilen 10
koddan biri; her biri tek kullanımlıktır). Kodlar da yoksa başka bir yöneticinin hesabınızdan 2FA'yı
kapatması gerekir.

---

## Veritabanı / bileşen sorunları

### Bileşen kaldırılamıyor ya da yeniden kurulamıyor

Bileşen zamanlanmış görevle çalışıyor olabilir ve dosyaları kilitlidir. Panel kaldırma sırasında
aynı adlı görevi sonlandırıp kurulum dizininden çalışan süreçleri öldürür. Yine takılırsa sunucuda
elle:

```
schtasks /End /TN <GorevAdi>
schtasks /Delete /TN <GorevAdi> /F
taskkill /IM <surec>.exe /F
```

### Bileşen güncellemesinde veri kaybı

Olmamalıdır: kurulum dizini silinmeden **önce** veri klasörleri dizinin yanına taşınır, kurulumdan
sonra geri konur; taşıma başarısız olursa kurulum **durur**. Bu davranış `DataPreservationTests`
ile teste bağlıdır. Yine de yaşadıysanız panel sürümünü ve günlükleri paylaşın.

---

## Posta çalışmıyor

Posta arka ucu **Stalwart**'tır ve panel kurulumunda otomatik kurulur. Panel posta ekranı
"kurulu değil" diyorsa **Sol menü → Yazılım Mağazası** üzerinden `stalwart-mail` ve `stalwart-cli`
bileşenlerinin **Kurulu** olduğunu doğrulayın.

Posta portları (25, 143, 465, 587, 993, 995) güvenlik duvarında açık olmalıdır; kurulumda bu adım
*best-effort*'tur.

---

## Sertifika alınamıyor (Let's Encrypt)

Ön koşullar: alan adı bu sunucuya çözümlenmeli ve **80/443** dışarıdan erişilebilir olmalı.

**Tekrar tekrar denemeyin** — Let's Encrypt'in haftalık deneme limiti vardır ve limiti yakarsanız
bir hafta bekleriz. Önce DNS'i ve port erişimini doğrulayın.

Panel sertifika kaynağını veren/konu ilişkisinden okur; başka bir otorite imzaladıysa `manual`
görünür — bu bir hata değildir.

---

## Yardım isterken ne gönderilmeli

1. Panel sürümü (**Sol menü → Ayarlar**).
2. `C:\ProgramData\AdaPanel\logs\adapanel-<tarih>.log` dosyasının ilgili bölümü.
3. Ekrandaki tam hata metni (mesaj anahtarı dâhil).
4. İşlemin tam adımları ve etkilenen site/kullanıcı adı.
