# adapanel.host — ayrı web sitesi

Bu klasör AdaPanel ürününün tanıtım sitesidir; panel arayüzü değildir. Site dosyaları
AdaPanelSetup.exe içine alınmaz ve `src/AdaPanel.Api/wwwroot` dizinine kopyalanmaz.

## Üretim ve doğrulama

Python 3 ve Node.js 20+ ile, depo kökünde (Node yalnız üretim sırasında kullanılır):

```text
python scripts/build-website.py
python scripts/test-website.py
```

Kaynak şablon `scripts/website/index.template.html`, içerik ve metadata
`scripts/build-website.py` içindedir. Ayrıntılı Türkçe/İngilizce rehber içerikleri
`scripts/website/guides.json`, `guides-operations.json` ve
`guides-administration.json`, `guides-product.json` ve `guides-alternatives.json` içindedir. FTP için temiz çıktı yalnız
`artifacts/website-public/` klasörüdür. Bu klasörün içeriğini adapanel.host sitesinin
FTP köküne yükleyin; depo kaynaklarını, testleri, README dosyasını, yedekleri veya
AdaPanel kurulum paketini FTP alanına yüklemeyin.

Üretilen 66 sayfa: Türkçe/İngilizce ana sayfa, kurulum ve 31 ürün/kullanım rehberi.
Konular IIS, posta, SEO/AEO/GEO, DNS, SSL, PHP/ASP.NET, Python/Node.js,
veritabanı, WordPress, dosya/FTP, yedekleme, taşıma, bayi/müşteri, güvenlik ve
otomasyondur. 21 Ekim'de ürün tanıtımı ve Windows hosting paneli seçme rehberi
iki dilde eklendi. Tam URL listesi üretilen sitemap.xml içindedir. Ana sayfada beş
kategori ve her rehberde üç ilgili konu bağlantısı bulunur.
İçerik sunucuda build veya JavaScript gerektirmez. Kurulum sayacı kullanıcı
talebiyle ana sayfalardan kaldırıldı; istatistikler yalnız `/admin/` ekranında
şifreli girişle görünür. `integrations/admin/README.md` belgesine bakın. Buradaki statik
sayfalar üretilen çıktının kaynak kontrolde tutulan kopyasıdır.

## Yayın ve geri dönüş

Önce değiştirilecek uzak dosyaları ve `web.config` dosyasını yerel, web dışı bir yedeğe alın.
Mevcut `web.config`, App_Data ve diğer uygulama dosyalarını koruyun. Yeni dosyaları
geçici isimle aktarın ve içerik özetlerini kontrol ettikten sonra asıl adlarına
taşıyın. Varlıkları ve alt sayfaları önce, ana sayfayı en son yayınlayın.

Yayından sonra tüm sayfaları, robots.txt, sitemap.xml ve assets dosyalarını HTTPS
üzerinden tekrar indirerek yerel çıktıyla karşılaştırın. Geri dönüşte yedekteki
ana sayfayı geri yükleyin; site dışındaki alan adlarına/DNS kayıtlarına dokunmayın.

18 Ekim 2026 yayınında mevcut IIS ayarları korunarak yalnız bilinen 34 sayfanın
`index.html` adresini canonical dizin adresine 301 ile yönlendiren kural eklendi.
Kural örneği `scripts/website/canonical-index-rule.xml` içindedir; dosya mevcut
web.config yerine yüklenmez. IIS'in özgün isteği için `UNENCODED_URL` kullanılır;
dizin isteğinin default-document çözümlemesi yönlendirme döngüsüne girmemelidir.
Uzak yedek içindeki yapılandırmayı `web.config.original` gibi etkin olmayan bir
adla saklayın. Hem HTML hem yapılandırma yedeğine HTTP erişiminin engellendiğini
doğrulayın. Sitemap lastmod ve görünür güncelleme tarihi gerçek içerik değişimiyle
birlikte güncellenir; her derlemede otomatik olarak ileri alınmaz. Tarih artık
sayfa bazındadır: yeni ürün rehberleri `updated`, ana sayfalar `HOME_UPDATED`,
değişmeyen rehberler önceki tarihi kullanır. Görünür tarih, JSON-LD dateModified
ve sitemap lastmod aynı kaynaktan üretilir. 21 Ekim yayınında canonical kuralı
38 sayfayı kapsayacak şekilde genişletildi; diğer IIS kuralları korundu.

## Arama ve yanıt sistemleri

Sayfalar kendine ait canonical, karşılıklı hreflang, Open Graph, JSON-LD ve HTML
olarak okunabilir içerik sağlar. FAQ verisi görünen soru/cevaplarla aynı kaynaktan
üretilir. Sıralama, zengin sonuç veya yapay zekâ alıntısı garanti edilmez.

18 Ekim 2026'da Search Console ve Bing Webmaster Tools sahipliği doğrulandı ve
sitemap gönderildi. Resmî hesaplardan alınan doğrulama dosyaları
`scripts/website/verification/` içinde tutulur ve derleme çıktısına kopyalanır;
site kökündeki karşılıklarını kaldırmayın. Rehber genişletmesi sonrasında Google
sitemap'i işleyerek 34 sayfa keşfetti; Bing yeniden gönderimi işleniyordu. Gerçek arama performansı
takibi ayrı işlemdir. Özel yapay zekâ dosyası zorunlu değildir:
https://developers.google.com/search/docs/appearance/ai-features

21 Ekim 2026 güncellemesinde 38 sayfalık sitemap Google'a yeniden gönderildi;
Bing mevcut kaydı yeniden işleme aldı. Gönderim, dizine eklenme garantisi değildir.
Yayın kanıtları ve kalan www hosting sorunu için
`docs/website-seo-publication-2026-10-21.md` raporuna bakın.

21 Ekim ek yayınında Windows için Plesk alternatifi rehberi iki dilde eklendi;
seçim rehberi Türkçe ve İngilizce kontrol paneli aramalarına göre genişletildi.
Sitemap ve canonical yönlendirme kuralı o yayında 40 sayfayı kapsıyordu.

22 Ekim GEO/AEO yayınında Türkçe ve İngilizce güncelleme/doğrulama rehberleri
eklendi; toplam 42 sayfa yayımlandı. Soru-cevap bağlantıları, görünür kaynaklarla
eşleşen TechArticle citation verisi ve sayfa/ürün/cevap ilişkileri geliştirildi.
Ana sayfalara Windows için Plesk alternatifi ve Linux uyumluluğu cevapları eklendi.
Google sitemap yeniden gönderimi başarılıdır; yeni sayfaların indekslenmesi
henüz doğrulanmadı. Kanıtlar: `docs/website-geo-aeo-2026-10-22.md`.

22 Ekim kapsamlı ek yayını 12 yeni konuyu iki dilde ekleyerek toplamı 66 sayfaya
çıkardı. İçerik kaynağı `scripts/website/guides-expanded.json`; ana sayfalardaki
rehber arama/kategori filtresi `scripts/website/guides.js` içindedir. JavaScript
olmadan bütün rehber bağlantıları ve içerik HTML'de kalır. Üç başlangıç yolu,
eski rehberlerden yeni konulara bağlantılar ve görünür listeyle eşleşen ItemList
şeması eklendi. Yeni içerikte 10.652 kelime ve iki dil toplamı 96 soru-cevap var.
75 canlı dosya ve 68 yönlendirme doğrulandı; Google gönderimi başarılı, Bing
yeniden gönderimi Processing durumunda. Ayrıntılı kayıt:
`docs/website-geo-aeo-expansion-2026-10-22.md`.

29 Ekim ortak GEO/AEO kit entegrasyonu mevcut üreticiye uygulanmıştır. Profil üretimi,
kanıtlı ölçüm planı ve yerel doğrulamalar için `scripts/geo-aeo/README.md` belgesine bakın.
Ölçüm verileri ve kaynak profilleri FTP çıktısına alınmaz.
