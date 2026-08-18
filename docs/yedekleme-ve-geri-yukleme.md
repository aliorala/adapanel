# Yedekleme ve geri yükleme

**Sol menü → Sunucu Araçları → Yedekleme** (müşteri girişinde: **Sol menü → Yedekleme**).

## 1. Yedek türleri

| Tür | Kim alabilir | İçerik |
|---|---|---|
| **Site yedeği** | Yönetici, bayi, müşteri | Site dosyaları + IIS yapılandırması (binding, uygulama havuzu, çalışma zamanı) + seçilen veritabanları + isteğe bağlı posta hesap/alias listesi |
| **Veritabanı yedeği** | Yalnız yönetici | Tek bir veritabanının dökümü |
| **Posta yedeği** | Yalnız yönetici | Stalwart veri dizini. **Posta servisi kısa süreliğine durdurulur**, zip alınır, yeniden başlatılır |
| **Panel yedeği** | Yalnız yönetici | `adapanel.db` (kullanıcılar, 2FA) + `config\*.json` (servis planları, müşteri kapsamları, otomasyon ayarları) |

### Panel yedeğinde ne YOK?

**DataProtection anahtarları (`keys` klasörü) bilerek dâhil değildir.**

Sebep: bu anahtarlar DPAPI ile **makineye** bağlıdır; başka bir makinede zaten çözülemezler.
Yedeğe konmaları güvenlik riskidir ve hiçbir faydası yoktur.

Pratik sonucu: panel yedeğini **başka bir makineye** geri yüklerseniz, o anahtarlarla şifrelenmiş
sırlar (panelin oluşturduğu veritabanı kullanıcı parolaları, lisans anahtarı) **çözülemez** ve
yeniden girilmeleri gerekir. Kullanıcılar, planlar, kapsamlar ve ayarlar geri gelir.

## 2. Site yedeği alırken seçenekler

| Seçenek | Varsayılan | Not |
|---|---|---|
| Dosyalar | Açık | Sitenin kök klasörü |
| Yapılandırma | Açık | IIS binding / havuz / çalışma zamanı, JSON olarak |
| Posta | Kapalı | Siteye ait posta alan adının hesap/alias listesi |
| Veritabanları | Boş | Motor + ad çiftleri; boş bırakılırsa veritabanı **eklenmez** |

> Veritabanı listesi boş bırakılan bir "tam site yedeği" aslında tam değildir. WordPress gibi
> veritabanı kullanan bir siteyi yedeklerken veritabanını **açıkça** ekleyin.

Seçilen veritabanlarının dökümü **aynı zip'in içine** (`databases/` klasörüne) girer; ayrı bir
dosya aramanız gerekmez.

### ⚠ Site yedeğindeki `mail/` klasörü posta kutusu DEĞİLDİR

Arşivdeki `mail/<alan-adı>.json` yalnızca **hesap / alias / yönlendirme listesidir** — içinde
**tek bir e-posta iletisi yoktur**. İletiler Stalwart'ın kendi deposunda yaşar ve o depodan tek bir
alan adını ayırmak dosya kopyalayarak mümkün değildir. **Posta kutusu içeriği yalnızca sunucu
geneli posta yedeğiyle korunur** (yukarıdaki tabloda "Posta yedeği"). Bu uyarı arşivin
`manifest.json` dosyasına da yazılır.

## 3. Yedeklerin klasör düzeni

Yedekler diskte **gün → kategori → kaynak** sırasıyla klasörlenir:

```
backups\2026-08-18\sites\ornek.com\ornek.com-20260818030000.zip
backups\2026-08-18\databases\mysql-shop_db\mysql-shop_db-20260818030500.sql.zst
backups\2026-08-18\subscriptions\<abonelik-kimliği>\abonelik-....zip
backups\2026-08-18\mail\stalwart-mail-....zip
backups\2026-08-18\panel\panel-config-....zip
```

**Neden bu sıra:** gün en üstte kaldığı için "bir günü komple taşı / sil" hâlâ **tek işlemdir**;
kaynak klasörü ise çok siteli bir sunucuda dosya adı okumadan gezinmeyi sağlar.

**Kaynağı tek olan kategoriler** (panel yedeği, sunucu geneli posta yedeği) fazladan bir seviye
açmaz — ayıracak bir şey yoktur.

**Siteye bağlı veritabanı sitenin klasörüne girer.** Panelden tek bir veritabanının yedeğini
aldığınızda, o veritabanı bir siteye kayıtlıysa yedek
`sites\<site>\databases\<motor>-<ad>\` altına düşer; böylece o sitenin her şeyi bir arada durur.
Bağ, panelin **sahiplik kaydından** okunur (ad benzerliğinden **tahmin edilmez**); kayıt yoksa ya
da okunamıyorsa yedek `databases\<motor>-<ad>\` altında kalır — yanlış siteye konmaz.

**Uzak (off-site) hedefte de aynı düzen kurulur:** FTP ve WebDAV'da ara klasörler açılır, S3'te
anahtar öneki olarak uygulanır.

> **Eski yedekler etkilenmez.** Her yedeğin yolu kaydında tam olarak saklanır; eski düzende alınmış
> yedekler listelenmeye, geri yüklenmeye, indirilmeye ve budanmaya (uzak kopyası dâhil) devam eder.
> Yeni düzen yalnızca **yeni** yedekler için geçerlidir.

Panelin yedek listesi klasöre değil **yedeğin alındığı ana** göre gün gün gruplanır; klasör düzeni
değişse de liste aynı kalır.

## 4. Off-site (uzak) hedefler

Yedek yereldeki dosyaya ek olarak uzak bir hedefe de gönderilebilir: **S3**, **FTP**, **WebDAV**.

Uzak hedefi **gerçek yedek almadan** sınayabilirsiniz: panel küçük bir test dosyasını **gerçek
yükleme yoluyla** gönderir; böylece kimlik, bağlantı ve yazma izni birlikte ölçülür. Sınama
başarısız olursa hata fırlatılmaz, sebebiyle birlikte sonuç gösterilir.

## 5. Otomatik yedekleme

**Sol menü → Sunucu Araçları → Otomasyon.**

Panel içi zamanlayıcı belirlenen sıklıkta tüm sitelerin yedeğini alır (isteğe bağlı veritabanlarıyla)
ve **saklama süresinden** eski yedekleri budar.

Budama kuralı: `retentionDays` günden eski yedeklerde yerel dosya + kayıt silinir. Uzak hedef
verilmişse **uzak kopya da** silinir (uzak rotasyon). **0 ya da negatif gün hiçbir şeyi silmez** —
yani "saklama süresi" alanını boş bırakmak yedekleri korur, silmez.

### 5.1 Yedeğin SAATİNİ seçmek

**Sol menü → Sunucu Araçları → Otomasyon → Otomatik yedekleme → "Zamanlama".**
(Aynı alanlar **Sol menü → Yedekleme** ekranındaki "Otomatik yedek" kartında da vardır.)

Dört seçenek var (Plesk'in `hourly | daily | weekly | monthly` seçicisiyle aynı mantık):

| Zamanlama | Sorulan | Ne zaman çalışır |
| --- | --- | --- |
| **Belirli aralıklarla** | Kaç saatte bir | Son çalışmadan N saat sonra |
| **Her gün** | Saat | Her gün o saatte |
| **Her hafta** | Gün + saat | Seçilen gün, o saatte |
| **Her ay** | Ayın günü + saat | Seçilen gün, o saatte |

**Saat SUNUCUNUN saatidir**, tarayıcınızın değil. Ekran hangi saat diliminde olduğunuzu ve
sunucuda o an saatin kaç olduğunu yazar. Kayda giren şey **duvar saatidir** (03:00), UTC damgası
ya da UTC farkı değil: sunucunun saat dilimi sonradan değişirse yedek yine yerel 03:00'te alınır.
Bu, yedek klasörlerindeki ayrımla aynıdır — **gün klasörü yerel tarihtir, dosya adındaki damga
UTC'dir.**

**Neden aralık yerine saat:** aralık modunda yedeğin saati **ilk tetiklenen ana kilitlenir**.
Bir kez 23:50'de "Şimdi yedekle" derseniz gecelik yedek 23:50'ye taşınır; panel yeniden başlarsa
ya da bir tur gecikirse saat her seferinde biraz daha ileri kayar ve er ya da geç trafiğin en
yoğun olduğu saate oturur. Saat seçildiğinde elle alınan yedek programı **kaydırmaz** — yalnızca
o günün turunu karşılar, yani aynı gün ikinci kez yedek alınmaz.

**Sunucu o saatte kapalıysa:** açılır açılmaz kaçırılan yedek alınır, ertesi gün beklenmez.
Ama sınırsız değil: kaçırılan an **dönemin yarısından** fazla geride kaldıysa (günlükte 12 saat)
tur atlanır ve bir sonraki saate bırakılır. Böylece 02:55'te açılan bir sunucu önce telafi turu,
beş dakika sonra 03:00 turu koşturmaz; atlamanın bedeli de sınırlıdır (bir sonraki tur en fazla
yarım dönem uzaktadır).

**Yaz saati (DST):** geçiş günlerinde tur ne tekrarlanır ne atlanır. Saat ileri alındığında
seçilen an o gün hiç yaşanmıyorsa (ör. 02:30) iş, saat atlar atlamaz çalışır; saat geri
alındığında aynı duvar saati iki kez yaşansa da yedek bir kez alınır.

**Eski kurulumlar:** zamanlama seçilmemiş kurulumlar **"Belirli aralıklarla"** modunda kalır ve
eski davranış birebir sürer. Saat seçmek isteğe bağlıdır; yükseltme hiçbir programı değiştirmez.

### 5.2 Abonelik başına program

**Sol menü → Yedekleme → Abonelik yedek programları.**

Aynı zamanlama alanları burada da vardır ve aynı kuralla çalışır. Plan kotası (**günde en çok N
yedek**) yalnızca "belirli aralıklarla" modunu daraltır: günlük/haftalık/aylık program zaten
günde en çok bir tur ürettiği için kotayı hiçbir değerde delemez.

Sunucu geneli program ile abonelik programları **ayrı kilitlerde** çalışır; ikisi aynı saate
denk gelirse ikisi de başlar ve disk yükünü paylaşır — hiçbiri atlanmaz. Aynı saate kurulmuş
çok sayıda abonelik programı ise sırayla koşar: tur başlarken zaman **bir kez** okunduğu için
sıranın sonundakiler de o turda yedeklenir, kendi telafi penceresinin dışına düşmez.

## 6. Geri yükleme

### 6.1 Tam geri yükleme

Yedek listesinden **geri yükle**. İsteğe bağlı bir **hedef yol** verebilirsiniz; boş bırakılırsa
yedek orijinal site köküne açılır.

Yalnız **yönetici** tam geri yükleme yapabilir.

### 6.2 Granüler (dosya seçerek) geri yükleme

Bir yedeğin içine **dizin dizin göz atabilir** ve yalnız seçtiğiniz dosya/klasörleri geri
yükleyebilirsiniz. Kazara silinen tek bir dosya için bütün siteyi geri yüklemek gerekmez.

### 6.3 İndirme

Her yerel yedek `.zip` olarak indirilebilir. İndirilen dosyayı sunucu dışında saklamak, off-site
hedef kurmadığınız durumda en basit güvencedir.

### 6.4 Silme

Bir yedeği silmek **yerel dosyayı ve kaydı** siler; **off-site uzak kopyaya dokunmaz** (uzak
rotasyon budamanın işidir). Yönetici her yedeği, müşteri/bayi yalnız kendi kapsamındaki **site**
yedeklerini silebilir.

## 7. Geri yükleme öncesi kontrol listesi

1. **Doğru yedeği seçtiğinizden emin olun.** Geri yükleme, hedefteki dosyaların üzerine yazar.
2. **Veritabanı yedeğe dâhil miydi?** Değilse dosyalar geri gelir ama site veriye bağlıysa yine
   çalışmaz.
3. **Büyük siteler için önce hedef yolu boş bir klasör verip içeriği doğrulayın**, sonra asıl köke
   geri yükleyin.
4. Panel yedeğini **aynı makineye** geri yüklüyorsanız sırlar da çalışmaya devam eder; **başka
   makineye** yüklüyorsanız 1. bölümdeki uyarıyı okuyun.

## 8. Felaket kurtarma (sunucu tamamen gitti)

Bu akış tek tek adımlarıyla **canlı olarak doğrulanmadı** — buradaki sıra, yukarıdaki doğrulanmış
davranışlardan çıkarılmış mantıksal sıradır:

1. Yeni sunucuya AdaPanel'i kurun ([kurulum.md](kurulum.md)).
2. Kurulum sihirbazını tamamlayın (geçici bir yönetici hesabıyla).
3. **Panel yedeğini** geri yükleyin → kullanıcılar, planlar, kapsamlar, ayarlar geri gelir.
4. Şifrelenmiş sırları yeniden girin: veritabanı kullanıcı parolaları, varsa lisans anahtarı
   (bkz. 1. bölüm — `keys` klasörü yedekte yoktur).
5. **Site yedeklerini** tek tek geri yükleyin.
6. DNS'i yeni sunucunun IP'sine yöneltin, sonra SSL sertifikalarını yeniden alın.

> Bu sırayı gerçekten güvenmek istiyorsanız **önce bir kez deneyin**. Denenmemiş bir yedek,
> yedek değildir.
