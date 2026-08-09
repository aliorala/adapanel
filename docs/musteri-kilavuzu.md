# Müşteri kılavuzu

Bu belge **abonelik sahibi** (rol: Customer) içindir.

## Panele giriş

Panelin adresi normal bir kök adres değildir; barındırma sağlayıcınızın verdiği **gizli giriş
yolunu** içerir:

```
https://<sunucu>:<port>/<gizli-yol>
```

Adresi kaybederseniz sağlayıcınızdan isteyin — tahmin edilemez, bilerek böyledir.

Art arda hatalı parola girerseniz hesabınız bir süre kilitlenir (varsayılan 15 dakika).
Bekleyebilir ya da sağlayıcınızdan kilidi açmasını isteyebilirsiniz.

## Menüde ne var?

Müşteri girişinde şu bölümler görünür:

| Bölüm | Ne yaparsınız |
|---|---|
| **Ana Sayfa** | Aboneliğinizin özeti ve kullanımınız |
| **Web Siteleri ve Alan Adları** | Site açma, PHP sürümü, SSL, DNS, FTP, WordPress araçları, istatistikler |
| **Posta** | Posta kutusu, takma ad, parola, kota |
| **Dosyalar** | Dosya yöneticisi (yalnız kendi sitelerinizin kökü) |
| **Yedekleme** | Kendi sitelerinizin yedekleri |
| **Veritabanları** | Veritabanı ve kullanıcı yönetimi, phpMyAdmin/Adminer |
| **Planlı İşlemler** | Zamanlanmış görevler (cron benzeri) |
| **Yapay Zeka** | Kendi API anahtarınızı üretip bir yapay zekâ istemcisini panele bağlama |
| **Hesap** | Parolanız, iki adımlı doğrulama, kurtarma kodları |

Sağlayıcınız size **alt kullanıcı** verdiyse menü daha dar olabilir: alt kullanıcı, abonelik
sahibinin izin verdiği bölümleri görür. Ana Sayfa, Yapay Zeka, Hesap ve Çıkış her zaman açıktır.

## Kotanız

Aboneliğinizin sınırları sağlayıcınızın verdiği plana göredir. **0 sınırsız demektir.**

| Sınır | Anlamı |
|---|---|
| En fazla site | Açabileceğiniz site sayısı |
| Disk (MB) | Sitelerinizin toplam disk kullanımı |
| En fazla veritabanı | **Tüm motorların toplamı** (MySQL + MariaDB + PostgreSQL + MSSQL) |
| En fazla posta kutusu | Toplam posta hesabı |
| En fazla planlı görev | Zamanlanmış görev sayısı |
| Aylık trafik (GB) | Sitelerinizin gönderdiği veri. **Takvim ayı** başında sıfırlanır |

Kullanımınızı **Ana Sayfa**'da görürsünüz. Kotayı doldurduğunuzda yeni kaynak açamazsınız;
aylık trafik kotasını aşarsanız aboneliğiniz **otomatik askıya alınır**.

## Askıya alınırsam ne olur?

* **Siteleriniz durur** (ziyaretçiler siteye erişemez).
* **Panele girmeye devam edersiniz.** Durumunuzu görebilir, kotanızı okuyabilir, sağlayıcınızla
  iletişime geçebilirsiniz. Bu bilinçlidir: giriş de kapansaydı sorunu çözemezdiniz.
* Sebep trafik kotasıysa **yeni ay başında kendiliğinden** geri açılır.
* Sebep ödeme/elle askıysa sağlayıcınızın açması gerekir.

## Site açma

**Web Siteleri ve Alan Adları → yeni site.**

Sağlayıcınız alan adınızı hesabınıza önceden atadıysa o adla site açabilirsiniz. Başkasına ait bir
alan adını ya da site adını kullanamazsınız — panel bunu bir sahiplik iddiası sayar ve reddeder.

Fiziksel yolu ve uygulama havuzunu **siz seçemezsiniz**; panel bunları kendi güvenli kökünüze
kilitler. Bu, başka müşterilerin dosyalarına erişimi engelleyen bir korumadır.

Site açarken aynı akışta veritabanı, posta alan adı ve FTP hesabı da isteyebilirsiniz. Bunlardan
biri kurulamazsa siteniz yine oluşur, yalnızca bir uyarı görürsünüz.

## SSL sertifikası

Site sekmelerindeki SSL bölümünden **Let's Encrypt** ile ücretsiz sertifika alabilirsiniz. Bunun
için alan adının sunucuya çözümlenmesi ve 80/443 portlarının açık olması gerekir.

Sertifika alındıktan sonra **HTTPS'e yönlendir** seçeneğiyle bütün trafiği HTTPS'e taşıyabilirsiniz.

> Sertifika alma başarısız olursa tekrar tekrar denemeyin: Let's Encrypt'in haftalık deneme limiti
> vardır. Önce alan adının gerçekten bu sunucuya çözümlendiğini doğrulayın.

## Yedekleriniz

**Yedekleme** bölümünde kendi sitelerinizin yedeklerini görür, yenisini alır, `.zip` olarak indirir
ve geri yükleyebilirsiniz. Ayrıntı: [yedekleme-ve-geri-yukleme.md](yedekleme-ve-geri-yukleme.md).

## Güvenlik

**Hesap** bölümünden:

* Parolanızı değiştirin (en az 12 karakter).
* **İki adımlı doğrulamayı açın.** Açtığınızda 10 adet **kurtarma kodu** verilir — bunları
  saklayın. Telefonunuzu kaybederseniz giriş ekranındaki koda kurtarma kodunuzu yazabilirsiniz
  (her kod bir kez kullanılır).

> Parolanızı değiştirdiğinizde daha önce ürettiğiniz API anahtarları **iptal edilir**. Bu
> kasıtlıdır: parola değiştirmek "erişimi kes" demektir; anahtar ayrı bir kimlik bilgisi olduğu için
> açıkça düşürülmezse eski erişim sürerdi.

## Yapay zekâ bağlantısı

**Yapay Zeka** bölümünden kendinize bir API anahtarı üretebilir ve bir yapay zekâ istemcisini
(örn. Claude) panele bağlayabilirsiniz. Anahtarın yetkisi sizin yetkinizden türer: **sizin
gördüğünüz kadarını görür**, fazlasını değil.

Anahtarın düz metin değeri **yalnızca üretildiği anda** gösterilir; kaybederseniz iptal edip
yenisini üretmeniz gerekir.
