# AdaPanel yerel GEO/AEO entegrasyonu — 29 Ekim 2026

Kök neden: ortak kitin AdaPanel profili 22 Ekim tarihli bağımsız bir snapshot'tı;
mevcut Python üreticisine doğrudan HTML eklemek canonical/JSON-LD kimliklerini
çoğaltabilirdi. Üretici şimdi güncel kaynaklarından TR/EN config oluşturup kitin
`validateConfig` işlevini Node.js 20+ ile build sırasında çağırır. Sunucuda Node
gerekmiyor. `ADAPANEL_NODE` veya PATH kullanılır; eksik runtime açık hata verir.

Eşleme:

| Alan | Yetkili yerel kaynak |
| --- | --- |
| title, description, canonical | Python `page_metadata` |
| shortAnswer, SoftwareApplication açıklaması, home ilk FAQ cevabı | `guides-product.json` ürün `intro` |
| faq | Python mevcut `FAQ` |
| publicUrls | güncel `PAGES` ve birleşik `TOPICS` |
| updatedAt / lastmod | Python `modified(path)` |
| relatedLinks | gerçek ürün rehberi |

Home cevap metni ortak kaynağa geçtiği için yalnız iki ana sayfanın içerik tarihi
2026-10-29 oldu. Ürün ve diğer rehber tarihleri korundu. 66 URL, mevcut TR/EN
alternatifleri ve `#organization`, `#website`, `#software`, `#page`, `#faq`,
soru/cevap kimlikleri; BreadcrumbList, TechArticle, görünür FAQ, filtre ve bağlar
korundu. Kit HTML renderer'ı kullanılmadı. Yeni özellik/SSS iddiası eklenmedi.

robots dört grupta (`*`, OAI-SearchBot, PerplexityBot, GPTBot) mevcut `Allow: /`
politikasını sürdürür. `/admin$`, `/admin?`, `/admin/` özel yolları engeller;
`/administrator` ve `/admin-tools` açık kalır. Sitemap satırı korunur. Eğitim
politikası genelleştirilmedi, llms.txt kapalıdır. robots kimlik doğrulaması değildir.
Doğrulama dosyaları ve başka kök uygulama dosyaları silinmedi.

Güncel config artefaktı `artifacts/geo-aeo/adapanel.profile.json`; soru planı ve
boş observations `scripts/geo-aeo/profiles/` içindedir. Bunların hiçbiri public
siteye kopyalanmaz. Kanıt yeri `scripts/geo-aeo/evidence/` ve CLI komutları aynı
dizinin README belgesindedir. Kanıtın mevcut yerel dosya olması, public dışında
kalması ve kaydın planVersion/conditions eşleşmesi doğrulanır. Kit
`parseNdjson`/`summarize` işlevleri kullanılır. Ölçüm yapılmadı: plan 72,
measured 0, coverage 0, accuracy/mentionedRate/citationRate null.

Yerel doğrulama: 66 sayfa build; 14 website testi ve 6 Node ölçüm testi geçti.
Gelecek tarihli metadata adaptörde reddedildi. robots tam `/admin` kuralı
kaldırılınca ilgili test kırıldı; geri yüklenince geçti. Eksik kanıt doğrulaması
mutasyonu da testte yakalandı. Bir test fixture'ında Windows C:/D: sürücü farkı
düzeltildi; son koşumda tüm testler geçti. Gerçek observations dosyası boş kaldı.
Canlı yayın, commit ve gerçek AI sorgusu yapılmadı.


## Tamamlanan bağımsız ve tarayıcı kontrolleri

- Ayrı rues-denetci kaynakları salt okunur inceleyerek ONAY verdi; website 14/14,
  Node 6/6 kontrolünü bağımsız tekrar çalıştırdı. Error-only kayıtlar da artık
  `status: ölçülmedi` üretir; sentetik unit fixture gerçek observations'a yazılmaz.
- Türkçe ve İngilizce değişen iki ana sayfa Chrome'da 1366×900 masaüstü ve
  390×844 mobil görünümde kontrol edildi (dört sayfa/boyut durumu). Belge yatay
  taşması yok; tek H1, tek canonical, tek JSON-LD, doğru dil ve gerçek 29 Ekim
  tarihi doğrulandı. Açılan ilk FAQ cevabı şemadaki cevap metniyle birebir aynı.
- EN gezinme bağlantısı doğru /en/ sayfasına geçti; rehber ve ürün bağlantıları
  66 sayfalık mevcut yerel bağlantı/fragment testi kapsamında korundu.
- Kanıtlar `artifacts/geo-aeo/browser-checks.json`, `desktop-tr.png`, `mobile-tr.png`,
  `desktop-en.png`, `mobile-en.png`. Önizleme yalnız 127.0.0.1 üzerinde çalıştırıldı;
  alan adında yayın ve arama motoru gönderimi yapılmadı. Geçici ekran boyutu
  ayarı testten sonra sıfırlandı.
- AdaPanel API Release derlemesi de 0 hata / 0 uyarıyla tamamlandı. Posta DNS
  düzeltmeleri ve canlı dış teslimat testinin kapsamı ayrı mail/FTP raporunda.

Kalan: 72 gerçek AI gözlemi henüz yoktur; oranlar null ve sonuç ölçülmedi.
Canlı SEO yayını ve gerçek arama motoru/AI performansı bu yerel turun dışındadır.

Ortak kaynaklar hazır paketin src dosyalarıyla metin olarak birebir karşılaştırıldı.
Paketin özgün 20/20 testi de geçti. Nihai boş CLI özeti
`artifacts/geo-aeo/measurement-summary.json` dosyasına kaydedildi.

## Sonraki yayın adımı

Kullanıcının daha sonraki açık yayın talebiyle kaynaklar 0.8.137'ye alındı ve
statik site FTPS üzerinden yayımlandı. 75 HTTPS dosya karşılaştırması ve 68
yönlendirme kontrolü geçti. Yukarıdaki "yerel tur" açıklamaları entegrasyon
aşamasının kaydıdır. Güncel yayın/kurulum ayrımı ve kanıtlar
`release-0.8.137-2026-10-29.md` belgesindedir.
