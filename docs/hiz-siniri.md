# Hız sınırı ve istek katmanı tavanları

Bu belge **kararların** yeridir; **değerlerin** tek kaynağı koddur:

- `src/AdaPanel.Api/Security/PanelRateLimits.cs` — bütün politikalar ve limitler
- `src/AdaPanel.Api/Security/PanelServerLimits.cs` — Kestrel ve form ayrıştırıcı tavanları

Bir sayıyı burada okuyup koda güvenmeyin; belge bayatlayabilir, `PanelRateLimitTests` ve
`PanelRequestSurfaceWiringTests` bayatlayamaz.

---

## 1. Ölçüm: arayüz hangi ucu ne sıklıkla yokluyor

Fazla kapatma en büyük risktir — bir yoklama ucunu dar sınıra almak paneli kırar ve kullanıcı
bunu "panel bozuldu" diye görür. Sınırlar aşağıdaki ölçümden türetildi, tahminden değil.

| Yer (dosya:satır) | Uç | Aralık | İstek/dk |
|---|---|---|---|
| `frontend/src/views/MonitoringView.vue:495` | `GET /api/v1/monitoring/processes` | 2 sn | **30** |
| `frontend/src/components/ResourceConsumers.vue:465` | `GET /api/v1/monitoring/consumers` | 3 sn, tur başına **iki** istek olabilir (`:423` IIS ilişkilendirmesi) | **20–40** |
| `frontend/src/views/MonitoringView.vue:492` | `GET /api/v1/system/history` | 60 sn | 1 |
| `frontend/src/views/SetupWizardView.vue:1698` | `GET /api/v1/system/overview` | 15 sn | 4 |
| `frontend/src/views/SetupWizardView.vue:1706` | dikkat rozeti | 60 sn | 1 |
| `frontend/src/views/SeoView.vue:611` | `GET /api/v1/seo/sites/{ad}` | 3 sn | 20 |
| `frontend/src/views/CronView.vue:777` | görev durumu | 2,5 sn, **görev başına** | 24 × N |
| `frontend/src/views/SecurityView.vue:621` | antivirüs durumu | 15 sn | 4 |
| `frontend/src/composables/useRunningTask.ts:234` | `.../running` | 15 sn | 4 |
| `frontend/src/views/UpdateView.vue:271` | `GET /api/v1/update/check` | 5 sn (yalnız kurulum sırasında) | 12 |
| `frontend/src/views/SetupWizardView.vue:1246` | `GET /api/v1/setup/progress` — **kimliksiz** | 1,5 sn, kurulum bitene kadar (20 dk+ sürebilir) | **40** |

**Tek sekmede ölçülen en kötü hâl: 71 istek/dk** (İzleme ekranı = 30 + 40 + 1).

**Kimliksiz tarafta ölçülen en yoğun hâl: 40 istek/dk** (kurulum sihirbazının ilerleme yoklaması).
`public-read` sınırı (300/dk) bunun yedi katıdır; dar bir sınır panelin **ilk açılışını** —
kullanıcının gördüğü ilk ekranı — kırardı.

`ResourceConsumers.vue` iki ekranda birden kullanılıyor (`MonitoringView.vue:332`,
`DatabasesView.vue:63`) ama SPA'da aynı anda tek görünüm bağlı olduğu için toplanmazlar; sekme
sayısı çarpandır.

---

## 2. Politikalar

| Politika | Bölüm | Sınır | Neden bu değer |
|---|---|---|---|
| *(küresel)* anahtar | anahtar özeti | 300/dk | değişmedi; WHMCS eşitlemesi/izleme aracı tek turda onlarca uç çağırır |
| *(küresel)* yoklama | oturum | 900/dk | ölçülen 71/dk'nın ~12 katı = ~12 sekme; **ayrı kova** |
| *(küresel)* genel | oturum | 600/dk | sayfa açılışı ≈ 40 istek; kovaya düşen diğer yoklamalar (SEO 20, cron 24×N, güncelleme 12) toplansa bile ~150/dk |
| `login` | IP | 5/dk | değişmedi |
| `password-change` | IP | 5/15dk | değişmedi |
| `setup` | IP | 3/10dk | değişmedi |
| `mcp` | anahtar | 120/dk | değişmedi |
| `public-mail-send` | IP | 10/15dk | **kimliksiz posta gönderimi**; gerçek bir abone 1–3 istek yapar |
| `public-read` | IP | 300/dk | kimliksiz okuma; vekil arkasındaki autodiscover yüzünden cömert (aşağı bkz.) |
| `git-webhook` | IP | 30/dk | sağlayıcı push başına bir istek gönderir |

### Neden yoklama ayrı kovada

Tek kovada olsaydı İzleme ekranını açık bırakan bir yönetici, **hiçbir kötüye kullanım
olmadan** kendi tıklama bütçesini tüketirdi. Yani sınır tam da her şeyin normal olduğu durumda
paneli kırardı.

Ayrı kovaya alınan yollar `PanelRateLimits.PolledPathPrefixes` içinde ve liste **kasten kısa**:
listede unutulan yeni bir yoklama ucu genel kovaya (600/dk) düşer, yani listenin bayatlaması
paneli **kırmaz**, yalnız daha dar bir kovaya düşürür.

### "Kimliksiz uç" tanımı: `[AllowAnonymous]` yetmez

Panelde küresel bir yetkilendirme ilkesi **yok** (`AddAuthorization()` çağrısında `FallbackPolicy`
verilmemiş). Yani `[Authorize]` **hiç taşımayan** bir denetleyici de tamamen açıktır — bunu
`[AllowAnonymous]` arayan bir tarama göremez.

Ölçüldü: `HealthController` ve `SetupController` tam olarak bu durumdaydı ve nöbetçinin ilk hâli
onları yeşil geçiriyordu. `PanelRateLimitTests.Kimliksiz_UCLARIN_HEPSI_bir_politikaya_bagli`
artık **iki** durumu da kimliksiz sayar ve yansımayla (metin taramasıyla değil) ölçer.

### Neden oturum çerezine göre bölümleniyor

Hız sınırlayıcı ara katmanı `UseAuthentication`'dan **önce** koşar (429'un güvenlik
başlıklarını kaybetmemesi için sıralama sabit), dolayısıyla `context.User` daha boştur. Elde
kalan tek oturum tanımlayıcısı çerezin kendisidir; onun **özeti** bölüm anahtarı olur (değeri
değil — bölüm anahtarları tanılama yüzeylerinde görünebilir). Çerez yoksa IP'ye düşülür.

Yalnız IP kullanılsaydı, aynı ofisten (tek NAT adresi) çalışan on yönetici tek kovayı paylaşır
ve biri diğerlerini kilitlerdi.

---

## 3. `UseForwardedHeaders` — BİLEREK KAPALI

**Karar: açılmadı.** Gerekçe `src/AdaPanel.Api/Program.cs` içinde de yazılı ve
`PanelRequestSurfaceWiringTests.ForwardedHeaders_kapali_ve_gerekcesi_YAZILI` ile sabitli.

**Neden:** AdaPanel paylaşımlı barındırma panelidir; aynı makinede kiracıların PHP/ASP kodu
koşar. Tek "güvenli görünen" seçenek olan `KnownProxies = 127.0.0.1` açılsaydı, herhangi bir
kiracının sitesi panele localhost'tan istek atıp `X-Forwarded-For` alanına istediği adresi
yazabilirdi:

- **Kaçış:** her istekte yeni bir sahte adresle hız sınırını ve IP yasağını sıfırlamak.
- **Saldırı:** kurbanın adresini yazarak onu giriş kaba-kuvvet yasağına düşürmek — yani
  yöneticiyi kendi panelinden kilitlemek.

Yani bu makinede loopback'e güvenmek, kimlik doğrulamasız bir "IP değiştirme" ucuna eşdeğerdir.

**Kabul edilen bedel (ölçüldü, gizlenmiyor):** autodiscover uçları müşterinin IIS sitesindeki
URL Rewrite/ARR kuralıyla panele iletiliyor
(`src/AdaPanel.Infrastructure/Mail/MailAutodiscoverRewriteRules.cs:27`,
`MailAutodiscoverPublisher.cs:76`). O isteklerin `RemoteIpAddress`'i **127.0.0.1**'dir ve
sunucudaki bütün posta istemcileri **tek** hız sınırı bölümünü paylaşır. `public-read` sınırının
dar değil **300/dk** seçilmesinin sebebi budur.

**Panel bir ters vekilin arkasına konacaksa:** `app.UseForwardedHeaders(...)` açılmalı ve
`KnownProxies` **yalnız o vekilin adresiyle** doldurulmalıdır — loopback'e güvenerek değil.
Açılmadan konursa "5 giriş/dk" bütün internet için tek kova olur ve gizli giriş yolunun IP beyaz
listesi anlamsızlaşır.

---

## 4. Kestrel ve gövde tavanları

Ölçülen durum: `ConfigureKestrel` yalnızca sertifika varken çağrılıyordu ve `Limits`'e hiç
dokunmuyordu. Bu "hiçbir sınır yok" demek **değildi** — Kestrel'in kendi varsayılanları (30 MB
gövde, 30 sn başlık zaman aşımı, 240 B/sn en az veri hızı) yürürlükteydi.

Gerçekten sınırsız olan tek şey **`MaxConcurrentConnections`**'dı; asıl düzeltme odur. Bağlantı
açıp hiç istek göndermeyen bir istemci kümesi, hız sınırlayıcının **göremediği** bir tüketimdir.

**Uzun işler kırılmadı:** istek süresine dair bir sınır yok. Kestrel'in ölçütü süre değil
**hız**dır (`MinRequestBodyDataRate` / `MinResponseDataRate`). Yedek indirme, bileşen kurulumu ve
1 GiB'lık SQL içe aktarımı veri aktığı sürece istedikleri kadar sürebilir.

**Yolda bulunan gerçek kopukluk:** `FilesController.UploadAsync` `[RequestSizeLimit(200 MiB)]`
taşıyor ama yükleme `IFormFile` ile ayrıştırılıyor ve form ayrıştırıcısının ayrı tavanı vardı
(`FormOptions.MultipartBodyLengthLimit`, varsayılan **128 MiB**). 128–200 MiB arasındaki her
yükleme, uç "izin veriyor" göründüğü hâlde `InvalidDataException` ile ölüyordu. İkisi artık aynı
sabiti okuyor.

---

## 5. Git webhook jetonu

**Ölçülen açık:** jeton yalnızca `?token=…` sorgu dizesiyle kabul ediliyordu. Sorgu dizeleri
IIS/W3C erişim günlüklerine olduğu gibi yazılır ve panelin kendi günlük görüntüleyicisi vardır —
yani dağıtım anahtarı, günlükleri okuyabilen herkese görünüyordu.

**Değişen:** `X-AdaPanel-Token` başlığı tercih edilen taşıyıcı oldu; panel arayüzü jetonsuz
adresi ve başlık satırını üretiyor.

**Değişmeyen (kasten):** sorgu dizesi **kabul edilmeye devam ediyor**. Kullanıcıların
GitHub/GitLab'a yapıştırdığı mevcut adresler onu taşıyor; kapatmak, panel güncellendiği anda o
kancaların hepsini sessizce öldürürdü.

**Görünür kılınan:** sorgu yolu kullanıldığında denetim kaydı bunu açıkça yazar
(`token transport: QUERY STRING — the secret is written to IIS/W3C access logs…`) ve panel
arayüzü eski biçimi bir uyarıyla gösterir.

**Dokunulmayan:** karşılaştırma `CryptographicOperations.FixedTimeEquals` ile sabit zamanlı
(`GitDeploymentService.cs:117`) ve dağıtım `concurrencyLock` ile seri.
