# Kurulum

## 1. Gereksinimler

| Konu | Değer | Kaynak |
|---|---|---|
| İşletim sistemi | 64-bit Windows (kurulum paketi `x64compatible` ister) | `installer/AdaPanel.iss` |
| Yetki | **Yönetici** (UAC onayı şart) | Kurulum servis kurar, güvenlik duvarı kuralı açar, IIS özelliklerini etkinleştirir |
| .NET | **Gerekmez** — panel tek EXE olarak kendi çalışma zamanını taşır | `scripts/verify-publish-output.ps1`, self-contained yayın |
| IIS | Site barındırmak için gerekir; panelin **kendisi** IIS'siz de açılır | `ConfigureInstallation` IIS adımını *best-effort* çalıştırır |
| Disk | Kurulum dizini `C:\Program Files\AdaPanel`, veriler `C:\ProgramData\AdaPanel` | `DefaultDirName={autopf}\AdaPanel`, `Panel:DataRoot` |

> Windows istemci sürümlerinde (10/11) IIS ve DNS Sunucusu rolleri sınırlıdır; panel açılır ama
> DNS bölge yönetimi gibi sunucu rolüne bağlı özellikler çalışmaz.

## 2. Kurulum adımları

1. `AdaPanelSetup.exe` dosyasını sunucuda **yönetici olarak** çalıştırın.
2. Kurulum sihirbazı bir **port** sorar. Varsayılan **8890**'dır
   (`installer/AdaPanel.iss` → `PortPage`). Güncelleme kurulumunda bu adım atlanır; mevcut port
   servisin `ImagePath` değerinden okunur.
3. Kurulum sırayla şunları yapar:
   1. `enable-iis-features.ps1` — IIS özellikleri (*best-effort*, hata kurulumu bozmaz)
   2. `initialize-panel-certificate.ps1` — kendinden imzalı `panel.pfx` üretir (*best-effort*)
   3. `install-service.ps1` — **kritik adım**: `AdaPanel` adlı Windows servisini
      `https://0.0.0.0:<port>` ile kurar/yeniden yapılandırır ve başlatır
   4. `configure-firewall.ps1` — güvenlik duvarı kuralları (*best-effort*)
4. Kurulum bitince tarayıcı `https://localhost:<port>/panel` adresine açılır.

### Açılan güvenlik duvarı portları

`scripts/configure-firewall.ps1` şu gelen (inbound) TCP kurallarını oluşturur:

* Panel portu (kuruluma verilen port)
* Web: **80, 443**
* Posta: **25, 143, 465, 587, 993, 995**
* DNS: **53** (TCP + UDP; DNS Sunucusu rolü varsa)

Panel dışındaki kurallar *best-effort*'tur: oluşturulamazsa uyarı yazılır, kurulum devam eder.

### Sertifika

`initialize-panel-certificate.ps1` `AdaPanel Self Signed` adlı, **3 yıl** geçerli, RSA-2048 /
SHA-256 bir sertifika üretir ve `%ProgramData%\AdaPanel\certs\panel.pfx` + `panel.password`
olarak yazar. DNS adları: `localhost` ve makine adı.

`Program.cs` açılışta bu iki dosyayı arar:

* **Varsa** → Kestrel HTTPS dinler.
* **Yoksa** → panel HTTP'ye düşer. Bu bilinçli bir sağlamlık kararıdır: sertifikasız HTTPS'e
  bağlanmaya çalışıp **çökmek** yerine panel açılır. Bu durumda `Panel:AllowHttpFallback`
  `false` ise dinleyici yalnızca `127.0.0.1` olur — yani panele **sadece sunucunun kendisinden**
  erişilir.

Kendinden imzalı sertifika tarayıcıda uyarı verir. Panele kendi alan adıyla dışarıdan erişmek ve
gerçek sertifika almak için: **sol menü → Ayarlar** altındaki panel genel erişim (ters vekil +
Let's Encrypt) akışı.

## 3. İlk giriş (kurulum sihirbazı)

Panel ilk açılışta bir kurulum sihirbazı gösterir. **Bu adım yalnızca sunucunun kendisinden
yapılabilir**: `SetupController.InitializeAsync` çağrının `IPAddress.IsLoopback` olmasını şart
koşar, aksi hâlde `403 setup.local_only` döner. Yani uzak masaüstüyle sunucuya bağlanıp
`https://localhost:<port>/panel` adresini açmanız gerekir.

Sihirbazda istenenler (`InitialSetupRequest` doğrulama kuralları):

| Alan | Kural |
|---|---|
| Kullanıcı adı | 3–32 karakter |
| E-posta | Geçerli e-posta, en fazla 256 karakter |
| Parola / Parola tekrar | **En az 12 karakter** |
| Gizli giriş yolu | `^[a-z0-9-]{3,64}$` — küçük harf, rakam, tire (varsayılan `panel`) |
| Dil | `tr` ya da `en` |
| Panel portu | 1–65535 |
| Önerilen bileşenleri kur | Varsayılan açık; kurulum arka planda sürer, ilerleme `GET /api/v1/setup/progress` ile yoklanır |

### Gizli giriş yolu nedir?

Panel kök adreste **açılmaz**. `SecureEntryPathMiddleware` isteği şöyle işler:

* `/` → `/<gizli-yol>` adresine yönlendirir
* `/<gizli-yol>/...` → panele girer
* Başka her yol → `404`
* `/api/...` → **`X-AdaPanel-Entry-Path` başlığı zorunludur** (yalnız `/api/v1/setup`,
  `/api/v1/health` ve `/api/v1/dbtools` muaf)

Bu, panelin adresini tarayan botlara karşı bir gürültü azaltma önlemidir; kimlik doğrulamanın
yerine geçmez. Değeri sonradan **sol menü → Güvenlik** ekranından değiştirebilirsiniz.

Adresi unutursanız: sunucudaki tepsi (bildirim alanı) simgesi panel adresini gösterir
(`src/AdaPanel.Api/Tray/PanelAdresi.cs`).

## 4. Kurulumdan sonra

1. **İki adımlı doğrulamayı açın**: sol menü → **Hesap**. Kurtarma kodlarını üretip saklayın —
   doğrulayıcıyı kaybederseniz panele girmenin başka yolu yoktur (aşağıdaki kurtarma dosyası hariç).
2. **IP kısıtlaması** (isteğe bağlı): sol menü → **Güvenlik**. Panel API'sine yalnız listedeki
   IP'lerden erişilir; kural `/api` dâhil her yola uygulanır.
3. **Yedeklemeyi kurun**: bkz. [yedekleme-ve-geri-yukleme.md](yedekleme-ve-geri-yukleme.md).
4. **Lisans** kullanacaksanız: bkz. [lisans-aktivasyon.md](lisans-aktivasyon.md).

## 5. Kaldırma

Kaldırma sihirbazı `uninstall-cleanup.ps1` betiğini çalıştırır ve **verileri koruyup korumayacağınızı
sorar** (`GetUninstallKeepData`). Verileri korumayı seçerseniz `C:\ProgramData\AdaPanel` yerinde
kalır; bir sonraki kurulum aynı veritabanı ve yapılandırmayla devam eder.

## 6. Önemli dosya yolları

| Yol | İçerik |
|---|---|
| `C:\Program Files\AdaPanel\` | Uygulama (tek EXE + `wwwroot` + `scripts`) |
| `C:\ProgramData\AdaPanel\data\adapanel.db` | Panel veritabanı (SQLite): kullanıcılar, 2FA, denetim kaydı |
| `C:\ProgramData\AdaPanel\config\` | Yapılandırma JSON'ları (servis planları, müşteri kapsamları, otomasyon, lisans) |
| `C:\ProgramData\AdaPanel\keys\` | DataProtection anahtarları (DPAPI ile, makineye bağlı) |
| `C:\ProgramData\AdaPanel\certs\` | `panel.pfx` + `panel.password` |
| `C:\ProgramData\AdaPanel\logs\adapanel-*.log` | Günlük dosyaları (14 gün saklanır) |

> `keys` klasörü **kritiktir**: içindeki anahtarlarla şifrelenmiş her şey (veritabanı parolaları,
> lisans anahtarı) o klasör kaybolursa çözülemez. Panel yedeği bu klasörü **bilerek dışarıda
> bırakır** (`IBackupService.BackupPanelAsync`), çünkü anahtarlar DPAPI ile makineye bağlıdır ve
> başka makinede zaten işe yaramaz.
