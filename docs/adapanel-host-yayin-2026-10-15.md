# adapanel.host — 15 Ekim 2026 yayını

AdaPanel ürününün ayrı tanıtım sitesi FTP üzerinden yayınlandı. AdaPanel kurulum
paketine veya panelin wwwroot dizinine site dosyaları kopyalanmadı.

## İçerik ve teknik değişiklikler

- Türkçe `/` ve İngilizce `/en/` bağımsız, tamamen statik sayfalar oldu. Dil seçimi
  gerçek bağlantılarla yapılır; JavaScript ve localStorage gerektirmez.
- `/kurulum/` ve `/en/installation/` sayfaları IIS hazırlığı, ilk yerel kurulum,
  NAT/genel IP ayrımı, DNS/SSL ve yedek geri yükleme testini anlatır.
- Dört sayfada ayrı title/description/canonical, karşılıklı hreflang, sosyal
  paylaşım metadata ve JSON-LD bulunur. SSS işaretlemesi görünür cevaplarla aynı
  kaynaktan üretilir; uydurma puan, değerlendirme veya fiyat eklenmedi.
- robots.txt, sitemap.xml, paylaşım görseli ve ayrı, önbelleklenebilir CSS eklendi.
- JavaScript kapalıyken içeriği görünmez yapan scroll-reveal bağımlılığı kaldırıldı.
- Ürünün kapsamı, kullanıcı rolleri, SEO/AEO/GEO denetiminin anlamı ve sınırları
  açıklandı. Sınırsız kaynak veya her bileşenin koşulsuz kurulması vaatleri kaldırıldı.

## Yayın doğrulaması

- Statik site kontrolleri: 5/5 başarılı; dört sayfa, iç bağlantılar/varlıklar,
  metadata/hreflang, SSS–JSON-LD tutarlılığı ve yayın klasörü ayrımı denetlendi.
- FTP ile alınan eski index.html, web.config ve README yerel, web dışındaki
  `artifacts/website-backup-2026-10-15/` klasöründe saklandı.
- Dosyalar geçici adla aktarılıp geri indirilerek doğrulandı; ana sayfa en son
  yayınlandı. Eski ana sayfanın ek kopyası sunucunun IIS tarafından yayınlanmayan
  App_Data klasöründe tutuldu.
- Dört sayfa ve beş yardımcı dosya HTTPS üzerinden HTTP 200 döndü. Dokuzunun da
  içeriği yerel yayın çıktısıyla birebir aynı; sonuçlar
  `artifacts/website-live-verification.json` içinde.
- HTTP → HTTPS 301 yönlendirmesi ve olmayan sayfalarda 404 doğrulandı.
- Uzak web.config yayın sonrasında tekrar indirilerek yedekle birebir karşılaştırıldı.

Yerel Türkçe sayfanın içerik ve bağlantı yapısı tarayıcı erişilebilirlik görünümünde
incelendi. Kullanıcının fare kontrolünü bırakma talebiyle masaüstü otomasyonu
durduruldu; son yayın için mobil/masaüstü ekran görüntüsü onayı alınmadı.

## Sınırlar ve sonraki ölçüm

Teknik hazırlık, arama sıralaması veya AI alıntısı garantisi değildir. Search Console
ve Bing Webmaster Tools hesabına sitemap gönderimi/hesap doğrulaması yapılmadı.
Gerçek indekslenme, arama performansı ve dönüşümler bu hesaplardan izlenmelidir.

Yalnız `artifacts/website-public/` FTP çıktısıdır. Kaynak betikleri, yedekler,
doğrulama kayıtları ve AdaPanelSetup.exe site köküne yüklenmez.
