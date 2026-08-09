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

## 3. Off-site (uzak) hedefler

Yedek yereldeki dosyaya ek olarak uzak bir hedefe de gönderilebilir: **S3**, **FTP**, **WebDAV**.

Uzak hedefi **gerçek yedek almadan** sınayabilirsiniz: panel küçük bir test dosyasını **gerçek
yükleme yoluyla** gönderir; böylece kimlik, bağlantı ve yazma izni birlikte ölçülür. Sınama
başarısız olursa hata fırlatılmaz, sebebiyle birlikte sonuç gösterilir.

## 4. Otomatik yedekleme

**Sol menü → Sunucu Araçları → Otomasyon.**

Panel içi zamanlayıcı belirlenen sıklıkta tüm sitelerin yedeğini alır (isteğe bağlı veritabanlarıyla)
ve **saklama süresinden** eski yedekleri budar.

Budama kuralı: `retentionDays` günden eski yedeklerde yerel dosya + kayıt silinir. Uzak hedef
verilmişse **uzak kopya da** silinir (uzak rotasyon). **0 ya da negatif gün hiçbir şeyi silmez** —
yani "saklama süresi" alanını boş bırakmak yedekleri korur, silmez.

## 5. Geri yükleme

### 5.1 Tam geri yükleme

Yedek listesinden **geri yükle**. İsteğe bağlı bir **hedef yol** verebilirsiniz; boş bırakılırsa
yedek orijinal site köküne açılır.

Yalnız **yönetici** tam geri yükleme yapabilir.

### 5.2 Granüler (dosya seçerek) geri yükleme

Bir yedeğin içine **dizin dizin göz atabilir** ve yalnız seçtiğiniz dosya/klasörleri geri
yükleyebilirsiniz. Kazara silinen tek bir dosya için bütün siteyi geri yüklemek gerekmez.

### 5.3 İndirme

Her yerel yedek `.zip` olarak indirilebilir. İndirilen dosyayı sunucu dışında saklamak, off-site
hedef kurmadığınız durumda en basit güvencedir.

### 5.4 Silme

Bir yedeği silmek **yerel dosyayı ve kaydı** siler; **off-site uzak kopyaya dokunmaz** (uzak
rotasyon budamanın işidir). Yönetici her yedeği, müşteri/bayi yalnız kendi kapsamındaki **site**
yedeklerini silebilir.

## 6. Geri yükleme öncesi kontrol listesi

1. **Doğru yedeği seçtiğinizden emin olun.** Geri yükleme, hedefteki dosyaların üzerine yazar.
2. **Veritabanı yedeğe dâhil miydi?** Değilse dosyalar geri gelir ama site veriye bağlıysa yine
   çalışmaz.
3. **Büyük siteler için önce hedef yolu boş bir klasör verip içeriği doğrulayın**, sonra asıl köke
   geri yükleyin.
4. Panel yedeğini **aynı makineye** geri yüklüyorsanız sırlar da çalışmaya devam eder; **başka
   makineye** yüklüyorsanız 1. bölümdeki uyarıyı okuyun.

## 7. Felaket kurtarma (sunucu tamamen gitti)

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
