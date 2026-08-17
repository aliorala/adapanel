# Yönetici kılavuzu

Panelin sahibi (rol: **Administrator**) için temel akışlar. Menü yolları Türkçe arayüze göredir.

## Roller

| Rol | Ne yapar |
|---|---|
| **Administrator** | Her şey. Sunucu araçları, yazılım mağazası, güvenlik, panel ayarları yalnız burada |
| **Reseller** (Bayi) | Kendi müşterilerini ve onların site kaynaklarını yönetir. Panel ayarları ve sunucu araçları kapalı |
| **Customer** (Müşteri) | Kendi sitelerini, veritabanlarını, postasını yönetir. Kotaya tabi |
| **Operator** | Kullanıcı oluştururken seçilebilen rol (`^(Administrator|Operator|Customer|Reseller)$`) |
| Alt kullanıcı | Bir müşterinin aboneliğini paylaşan ek giriş. Kendi kaynağı yoktur; hangi bölümleri göreceği ayrıca kısıtlanabilir |

Bayi bir müşteri oluşturduğunda rol **Customer**'a, sahip de bayinin kendisine **zorlanır** —
istekte başka bir rol/sahip gönderilse bile yok sayılır (`UserManagementService.CreateUserAsync`).

## 1. Müşteri ve bayi yönetimi

**Sol menü → Müşteriler & Bayiler** (bayi girişinde: **Müşteriler**).

### Servis planı önce, müşteri sonra

Aynı ekranın üstünde **servis planı** kartları vardır. Plan bir **şablon değil kalıcı bağdır**:
limitler okuma anında plandan çözülür, yani planı değiştirmek ona bağlı **bütün** abonelikleri
aynı anda değiştirir. 200 müşteriye aynı paketi satıyorsanız plan kullanın; aksi hâlde
"Başlangıç planının diskini 5 GB yap" demek 200 müşteriyi tek tek düzenlemek olur.

Plan alanları: en fazla site, disk (MB), veritabanı, posta kutusu, planlı görev, FTP hesabı,
aylık trafik (GB) ve geçerlilik süresi (gün). **0 = sınırsız.**

### Müşteri oluşturma

Kullanıcı adı 3–32 karakter, parola **en az 12 karakter**, e-posta benzersiz olmalıdır.
"Hoş geldin e-postası gönder" işaretlenirse giriş bilgileri e-postayla gider — bunun için SMTP'nin
yapılandırılmış olması gerekir; yapılandırılmamışsa **hesap yine oluşur**, yalnız e-posta atlanır.

Müşteriye **site adı atamak bir sahiplik iddiasıdır**. Panel site adını ve host adını sahiplik
kanıtı sayar; bir alan adını önceden müşterinin kapsamına yazmak onu o müşteriye rezerve eder ve
başka bir kiracının aynı adı almasını engeller.

### Askıya alma

**Askı girişi kapatmaz, siteleri durdurur.** Askıya alınan müşteri panele girebilmelidir — durumunu
görsün, kotasını okusun, planını yükseltsin. Girişi de kapatmak müşteriyi sorunu çözemez hâle
getirir ve destek yükünü artırır.

Trafik kotası dolduğunda ve abonelik süresi bittiğinde askı **kendiliğinden** uygulanır (arka plan
işçisi). Sebep ortadan kalkınca (yeni ay, sayaç sıfırlanır) **kendiliğinden geri açılır** — ama
**elle** askıya alınmış bir abonelik kendiliğinden açılmaz: yönetici bilerek kapattıysa kota
denetimi onu geri açmamalıdır.

Trafik **ölçülemiyorsa** (IIS günlüğü yok, klasör okunamıyor) kota **uygulanmaz**. Ölçemediğimiz
için askıya almak, kimsenin aşmadığı bir kotadan dolayı müşteriyi yayından kaldırmak olurdu.

### Kullanıcı silme

`removeSites` işaretlenirse (yalnız yönetici) atanan siteler **dosyalarıyla ve eşleşen
veritabanlarıyla** birlikte silinir. İşaretlenmezse siteler ve veriler sunucuda kalır, yönetime
devredilmiş olur.

### Kilit açma

Art arda hatalı girişten kilitlenen kullanıcı varsayılan **15 dakika** (`Panel:LoginLockoutMinutes`)
giremez. Kullanıcı satırındaki **kilidi aç** düğmesi beklemeden açar. Bayi yalnız kendi müşterisini
açabilir.

## 2. Web siteleri

**Sol menü → Web Siteleri ve Alan Adları.**

Site oluşturma tek akışta birden çok kaynağı hazırlayabilir: veritabanı, posta alan adı, FTP hesabı.
Bunlardan biri kurulamazsa **site yine kalır** (*best-effort*); yalnızca uyarı yazılır.

Yönetici olmayan kullanıcılarda panel şunları **zorlar** (çapraz kiracı koruması):

* Fiziksel yol daima varsayılan site köküne kilitlenir (kullanıcı seçemez).
* Uygulama havuzu seçilemez; panel yönetilen adı kendisi üretir.
* Site adı ve host adı başkasına ait olamaz.

Kota denetimi ile kaydın arası kilitlidir: `MaxSites=1` olan bir müşteri iki isteği aynı anda
gönderdiğinde ikisi de kotayı boş görüp iki site açamaz.

Yeni site başka bir yönetilen sitenin **alt alan adıysa**, üst bölgeye otomatik A kaydı düşer
(*best-effort*).

## 3. Veritabanları

**Sol menü → Veritabanları.**

Desteklenen motorlar: MySQL/MariaDB, PostgreSQL, MSSQL. Kota **motordan bağımsızdır**: müşterinin
`MaxDatabases` sınırı tüm motorlardaki toplam veritabanı sayısını sınırlar.

Panelin oluşturduğu veritabanı kullanıcılarının parolaları şifreli saklanır
(`DatabaseCredentialStore`), böylece phpMyAdmin/Adminer o kullanıcı kimliğiyle tek tıkla açılabilir
ve yalnız kendi veritabanını görür.

## 4. Posta

**Sol menü → Posta.**

Posta arka ucu **Stalwart**'tır ve panel kurulumunda **otomatik** kurulur. Alan adı, posta kutusu,
takma ad (alias), kota ve parola yönetimi panelden yapılır.

Webmail için **Roundcube** tek tıkla kurulabilir (Yazılım Mağazası).

## 5. DNS

**Sol menü → Web Siteleri ve Alan Adları → ilgili sitenin DNS sekmesi** (sunucu geneli bölge
yönetimi ayrıca vardır).

Panel Windows DNS Sunucusu rolünü kullanır; rol kurulu değilse panel kurulum ucunu sunar
(`POST /api/v1/dns/windows/install`). Varsayılan kayıt kümesi, DNSSEC ve kayıt ekleme/silme
panelden yönetilir. DNS şablonları ayrı bir ekrandadır.

## 6. SSL

Site başına: **Let's Encrypt ile al**, **var olan sertifikayı içe aktar**, **yenile**,
**HTTPS'e yönlendir**.

Sertifika kaynağı artık veren/konu ilişkisinden okunur; başka bir otorite imzaladıysa `manual`
olarak görünür. (Eskiden `FriendlyName` içinde "Let's Encrypt" aranıyordu ve gerçek sertifikalar
"self-signed" görünüyordu — kullanıcılar sertifikanın alınmadığını sanıp Let's Encrypt haftalık
limitini yakıyordu.)

Otomatik yenileme için bkz. **Otomasyon**.

## 7. Güvenlik

**Sol menü → Güvenlik.**

| Özellik | Ne yapar |
|---|---|
| Gizli giriş yolu | Panelin kök adres yerine `/<yol>` altında açılması. `/api` istekleri de `X-AdaPanel-Entry-Path` başlığı ister |
| İzinli IP listesi | Panel API'sine yalnız listedeki IP'lerden erişim. Kural `/api` dâhil her yola uygulanır |
| Brute-force koruması | Başarısız girişleri IP bazında sayar; eşik aşılınca Windows Güvenlik Duvarı'nda süreli engel kuralı açar |
| İki adımlı doğrulama | **Sol menü → Hesap** altında. Etkinleştirildiğinde 10 kurtarma kodu üretilir |

> **Kurtarma kodlarını saklayın.** Doğrulayıcıyı kaybederseniz giriş alanına kurtarma kodu
> yazabilirsiniz (kod tek kullanımlıktır).

### Kendinizi kilitlerseniz

Panelde kilidi açacak başka yönetici yoksa tek çıkış yolu sunucudaki bir bayrak dosyasıdır:

```
C:\ProgramData\AdaPanel\config\recover-access.flag
```

Bu dosyayı oluşturup **AdaPanel servisini yeniden başlatın**. Panel açılışta:

* bütün giriş kilitlerini temizler,
* brute-force IP engellerini kaldırır,
* panelin kendi **engelleme** güvenlik duvarı kurallarını kaldırır (yanlışlıkla panel portunu ya da
  uzak masaüstünü engellemiş olabilirsiniz),
* sonra bayrağı **siler**.

Dosyayı oluşturmak yönetici ACL'i gerektirir; sunucuya erişimi olan kişi zaten paneli
sıfırlayabilirdi, yeni bir açık doğmaz.

> Bu bayrak **parolayı sıfırlamaz** — yalnız kilitleri ve engelleri açar.

### Parolanızı unutursanız

Sunucuda, **yönetici** olarak açılmış bir komut isteminde:

```powershell
& "C:\Program Files\AdaPanel\AdaPanel.Api.exe" --parola-belirle
```

Komut yönetici hesaplarını listeler, hangisinin parolasını belirlediğinizi size seçtirir ve yeni
parolayı iki kez sorar (parola ekrana yazılmaz, komut satırına da yazılmaz). Parola değişince o
kullanıcının **açık oturumları düşer** ve **API anahtarları iptal edilir**; işlem denetim kaydına
`auth.password_set_from_console` olarak düşer (parolanın kendisi kaydedilmez).

İki adımlı doğrulama **varsayılan olarak korunur**. Doğrulayıcınızı da kaybettiyseniz önce kurtarma
kodunuzu deneyin; o da yoksa `--2fa-kapat` ekleyin (komut ayrıca `EVET` yazmanızı ister).

Tam yordam ve gerekçeler: [Yönetici parolasını sunucudan belirleme](parola-kurtarma.md).

## 8. Otomasyon

**Sol menü → Sunucu Araçları → Otomasyon.**

Panel içi arka plan zamanlayıcısı dakikada bir zamanı gelen işleri çalıştırır:

* Otomatik yedekleme (tüm siteler + isteğe bağlı veritabanları) + saklama süresine göre budama
* Otomatik Let's Encrypt yenileme
* Süreli brute-force engellerinin temizliği

Her iş elle de tetiklenebilir (`POST /api/v1/automation/backup/run`, `.../ssl/run`).

## 9. Yazılım mağazası ve bileşenler

**Sol menü → Yazılım Mağazası.**

Panel bileşenleri (PHP, MariaDB, PostgreSQL, Stalwart, Docker Engine, phpMyAdmin, Adminer,
Roundcube, WordPress vb.) buradan kurulur/kaldırılır.

> Veri koruması: bileşen güncellemesi/yeniden kurulumu sırasında veri klasörleri (örn. MariaDB
> `data/`) kurulum dizininin **yanına taşınır** ve kurulumdan sonra geri konur. Taşıma başarısız
> olursa kurulum **durur** — veriyi koruyamıyorsak silmeye devam edilemez.

## 10. Sunucu araçları

**Sol menü → Sunucu Araçları**: güvenlik duvarı, planlı görevler, günlükler, süreç izleme, terminal,
Docker, ters vekil, Nginx, antivirüs, WAF ve panelin kendi genel erişimi (ters vekil + Let's
Encrypt) burada.

**Sol menü → Danışman** panelin bulduğu yapılandırma sorunlarını ve önerileri listeler.

## 11. Panel güncellemesi

Panel kendi kendini güncelleyebilir (**Sol menü → Ayarlar** altındaki güncelleme bölümü). Güncelleme
kurulum paketini indirip sessiz kurulum çalıştırır; kurulum portu mevcut servisten okunur, yeniden
sorulmaz.

> Güncellemeden sonra tarayıcıda **boş sayfa** görürseniz sayfayı sabit yenileyin (Ctrl+F5). Panel
> artık doğru önbellek başlıkları gönderiyor (`index.html` → `no-cache`, `/assets/*` → bir yıl
> immutable), ama eski bir tarayıcı önbelleği kalmış olabilir.

## 12. API'yi dışarıdan kullanmak

* `/api/v1/**` uçları **çerez** kimlik doğrulaması kullanır. `Authorization: Bearer adap_…`
  anahtarları **yalnızca `/mcp` ucunda** geçerlidir (yapay zekâ istemcileri için).
* Her `/api` isteği `X-AdaPanel-Entry-Path` başlığı ister.
* GET dışı her istek `X-CSRF-TOKEN` başlığı ister; belirteç `GET /api/v1/auth/csrf` ile alınır ve
  **girişten sonra yenilenmelidir** (oturum kimliğine bağlıdır).
* `POST /api/v1/auth/login` IP başına **dakikada 5** istekle sınırlıdır.

Çalışan bir örnek: `integrations/whmcs/adapanel/lib/AdaPanelClient.php`.
