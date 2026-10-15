# SEO / AEO / GEO geliştirmesi — 15 Ekim 2026

AdaPanel’in site denetimi geliştirildi. Çalışma panelin analiz ve öneri özelliğini kapsar; barındırılan sitelerin içeriklerini, robots.txt dosyalarını, DNS veya IIS ayarlarını değiştirmez.

## İnceleme sonucu

Mevcut modül başlık/açıklama, H1, görsel alt özniteliği, bağlantı, robots.txt, sitemap ve günlük kontrolleri yapıyordu. Canonical ayrıştırılıyor ama rapora taşınmıyordu. JSON-LD script gövdesiyle birlikte atılıyor, HTTP X-Robots-Tag göz ardı ediliyor, OpenAI arama/eğitim/kullanıcı ziyaretleri aynı günlük satırında toplanıyordu. İlk HTTPS bağlamasını seçmek mail veya önizleme alan adının taranmasına neden olabiliyordu.

## Değişiklikler

- Sayfa başına canonical sayısı/adresi, HTML dili, title sayısı, H2–H6 sayısı ve HTML gövde metni ölçülür. Tek canonical varlığı doğruluk veya indekslenme kanıtı sayılmaz.
- JSON-LD JSON sözdizimi, kök nesne/dizi ve iç içe derinlik açısından denetlenir; `@graph`, tür dizileri ve iç içe `@type` değerleri raporlanır. Microdata/RDFa işaretleri ayrıca gösterilir. Harici JSON-LD bağlamı indirilmez; script çalıştırılmaz.
- En çok 32 JSON-LD bloğu, blok başına 256 KiB ve 32 JSON derinliği işlenir. Aşılan blok sınırları raporda belirtilir. HTML yanıt sınırı 2 MiB; kesilmiş sayfalar eksik içerik hatası üretmez ve hazırlık sayılarına katılmaz.
- Meta robots ve HTTP X-Robots-Tag genel `noindex`/`none`, `nosnippet` ve `max-snippet:0` kuralları ölçülür. Googlebot/Bingbot’a özel yönergeler ayrıca gösterilir; genel izin gibi değerlendirilmez.
- Googlebot, Bingbot, OAI-SearchBot ve GPTBot için robots.txt politika tablosu eklendi. Ardışık User-agent satırları, tekrar eden gruplar ve bota özel grup önceliği uygulanır. Karmaşık yol istisnaları için “adres bazında inceleyin” sonucu verilir; bu bir tam URL izin motoru değildir.
- robots.txt erişim hatası, yönlendirme, kesilmiş içerik veya HTML yanıtında bot durumu “ölçülemedi” olur. 404/410 durumunda dosyadan kaynaklı kısıtlama olmadığı gösterilir; CDN/WAF erişimi hakkında hüküm verilmez.
- OpenAI günlükleri arama (`OAI-SearchBot`), eğitim (`GPTBot`) ve kullanıcı isteği (`ChatGPT-User`) olarak ayrılır. Eski `openai` raporlarının çevirisi korunur. Kimlik doğrulaması desteklenmeyen botlar doğrulanmış gösterilmez.
- Ana web alan adı tarama hedefinde mail ve önizleme bağlamalarından önce seçilir. SSRF/loopback taşıma sınırları korunur.
- Dekoratif boş `alt` hata sayılmaz; yalnız eksik öznitelik raporlanır. `noindex` alt dize eşleşmesi yerine tam yönerge olarak yorumlanır. İzlenemeyen 3xx yanıtlarının boş gövdesi eksik title/H1 sayılmaz.
- Türkçe/İngilizce hazırlık görünümü, sayfa bazında kanıtlar, öncelikli uygulama rehberi ve bulguya özel çözüm önerileri eklendi. Bulgu sayısı sınırında hata/uyarılar bilgilendirmelerden önce korunur.

## Kullanım ve eski raporlar

Güncelleme kurulduktan sonra SEO bölümünde siteyi seçip **Yeniden tara** çalıştırın. Yeni raporlar `analysisVersion: 1` ve sayfa başına `contentSignals` taşır. Eski raporlarda bu alanların yokluğu başarı veya sıfır sorun anlamına gelmez; arayüz yeniden tarama ister. Günlük ayrımı için günlük analizini yeniden çalıştırın.

0.8.95 kurulum paketi hazırlandı; GitHub yayını bağlantı bekliyor. 0.8.94 yayın
paketi değiştirilmedi. adapanel.host ayrı bir FTP yayınıdır ve kurulum paketinde yoktur.

## Ölçümün sınırları

Analiz IIS üzerinden statik HTML okur; JavaScript render, Google/Bing indeks sorgusu, dış CDN/WAF erişim testi veya gerçek AI kaynak gösterimi ölçümü yapmaz. JSON sözdizimi kontrolü, schema.org doğrulaması veya zengin sonuç uygunluğu değildir. Görünür metin sayısı kalite puanı değildir; CSS ile gizlenmiş metin ve yalnız navigasyondan oluşan içerik için insan incelemesi gerekir. Regex tabanlı HTML çıkarımı tarayıcı DOM ayrıştırıcısı değildir.

Yapılandırılmış veri isteğe bağlıdır. Genel FAQ işaretlemesi veya `llms.txt` şartı getirilmez. Yazar/kurum, güncellik ve kaynak önerileri içeriğin türüne bağlıdır. Gerçek sonuçlar Search Console, Bing Webmaster Tools/AI Performance ve site dönüşüm verileriyle izlenmelidir; bot ziyareti kaynak gösterilme kanıtı değildir.

## Doğrulama

- SEO altyapı testleri: **204 geçti, 0 başarısız**. `dotnet test --filter FullyQualifiedName~Seo`; sonuç: `tests/AdaPanel.Infrastructure.Tests/TestResults/seo-aeo-geo.trx`.
- Arayüz: yeni bileşen, SeoView ve iki dil kataloğunda **12 test geçti, 0 başarısız**. Eski raporlar, eksik/kesilmiş ölçümler, JSON-LD hata durumu, bot amacı, bilinmeyen erişim ve HTML kaçışı kapsandı.
- TypeScript kontrolü ve Vite üretim derlemesi başarılı.
- Türkçe hazırlık kartları ve bot tablosu yerel örnek verilerle tarayıcıda kontrol edildi.

### 0.8.95 yayın kontrolleri

- Yeni görünümde tanımsız `--border` kullanımı geniş testte yakalandı ve mevcut
  `--panel-border` tema belirtecine düzeltildi. SEO + CSS belirteci: **207/207 geçti**.
- Arayüzün tamamı: **138 dosya, 2.651 test geçti**. Fork worker koşumu bu ortamda
  takıldığı için `--pool=threads --maxWorkers=1` ile tamamlandı; testler atlanmadı.
- Altyapı geniş koşumu: **9.480 geçti**, 500 dosyalı gerçek ACL testi 90 saniyelik
  fixture süresini aştı. Aynı test değişiklik yapılmadan, tek başına yeniden
  çalıştırıldı ve geçti. İki FTP kullanıcı oluşturma testi (`FtpCreateRollbackTests`)
  bu bilgisayarda gerçek hesap açmamak için kapsam dışında bırakıldı.
- TypeScript ve Vite üretim derlemesi başarılı. Yayın notu mahremiyet testleri 12/12.
- Bileşen paket denetimi: **22 geçti**, mevcut `SkipLargePackages` seçimine göre
  üç büyük bileşen indirmesi atlandı. Taze denetim tamamlandıktan sonra kurulum
  paketlemesindeki aynı indirme denetimi ikinci kez çalıştırılmadı.
- Üretilen paket içerik ve sürüm denetiminden geçti. Panel wwwroot/index.html
  hash'i frontend/dist/index.html ile aynı; tanıtım sitesi içermez. SEO görünümünün
  yeni JavaScript ve CSS dosyaları pakette bulundu.
- Kurulum paketi: `artifacts/installer/AdaPanelSetup.exe`, **91.186.988 bayt**.
- SHA256: `F2385EC29D40D6650F391442F05D3B61141F22668EFD09218847449274EB0D6C`.

GitHub'a kaynak gönderilmez; genel depo yalnız kurulum sürümleri içindir. Son
yayın adımı için tarayıcı bağlantısı istek politikasını yükleyemedi; komut satırında
da kayıtlı GitHub kimlik bilgisi yok. Bu nedenle 0.8.95'in yayımlandığı veya genel
indirme dosyasının doğrulandığı iddia edilmez. Kullanıcının fare kontrolünü bırakma
talebi nedeniyle masaüstü giriş otomasyonu kullanılmaz.

## Resmî kaynaklar

- [Google: AI features and your website](https://developers.google.com/search/docs/appearance/ai-features)
- [Google: Optimizing for generative AI features](https://developers.google.com/search/docs/fundamentals/ai-optimization-guide)
- [OpenAI: Overview of crawlers](https://developers.openai.com/api/docs/bots)
- [Bing: AI Performance](https://www.bing.com/webmasters/help/ai-performance-9f8e7d6c)
