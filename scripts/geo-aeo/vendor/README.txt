GEO / AEO entegrasyon kiti — Node.js 20+, harici bağımlılık yok

Örnek Uygulama ve Örnek Yayıncı kurmacadır. example.com üretim adresi değildir.
examples/app.json yalnız çalıştırılabilir şablondur. factsVerified, operatörün
isim, yayıncı, URL, açıklama ve tarihi gerçek kaynaklarla doğruladığına dair
beyanıdır; yazılım bunun doğruluğunu dışarıdan doğrulayamaz. Örnekteki true
yalnız fixture içindir. Kendi config dosyanızda bütün alanları doğrulayın.
updatedAt yalnız gerçek içerik değişikliğinde değişmelidir; build tarihi değildir.
Güncel takvim günü Europe/Istanbul ile doğrulanır; measuredAt UTC zamanıdır.

Kullanım (bu klasörde):
  node --test
  node src/cli.mjs build examples/app.json generated-example
  node src/cli.mjs summarize examples/questions.json examples/observations.ndjson
Build hedef dizini önceden bulunmamalı; var olan dosya/dizin üzerine yazılmaz.
Yeni hedefin üst dizini mevcut olmalı. CLI ağ isteği veya dağıtım yapmaz.

Entegrasyon:
- head.html içeriğini sunucudan dönen HTML head bölümüne yerleştirin.
- content.html içeriğini aynı sayfada görünür render edin. Mevcut sayfada H1
  varsa buradaki başlığı mevcut başlıkla birleştirin; çift H1 oluşturmayın.
- Aynı config JSON-LD ile görünür cevap/SSS üretir. Mevcut schema ile @id
  çakışmalarını kontrol edin. FAQPage schema, fiyat ve rating üretilmez.
  publisher.type Person veya Organization olabilir; belirtilmezse Organization.
- canonical temiz, mutlak HTTPS adresidir. Dil alternatifleri gerekiyorsa
  her dilin gerçek URL'sini ayrı configle yönetin; hreflang ayrıca bağlanır.
- publicUrls yalnız erişime açık ve indekslenebilir 200 URL'lerden oluşmalı.
  Kod HTTP durumu veya noindex kontrol etmez. privatePaths sitemapten elenir.
  publicUrls[].updatedAt isteğe bağlıdır; bilinmiyorsa alanı atlayın, lastmod
  üretilmez. Sayfanın ana updatedAt alanı zorunlu kalır; tarih uydurulmaz.
  Dil seçimi için ?lang=en gibi query URL'ler açık publicUrls listesinde
  desteklenir; araç kendiliğinden query veya takip parametresi eklemez.
  privatePaths dosya yollarını destekler; saf public sitelerde [] olabilir.
  Özel yol tam eşleşme, query ve alt yol kurallarıyla engellenir; /api kuralı
  /apiary yolunu engellemez. robots adayı bu sınırları ayrı satırlarla taşır.
- sitemap.xml bir taslaktır: üretimde mevcut sitemap/sitemap index düzenine
  birleştirin. robots.candidate.txt mevcut robots.txt üstüne kopyalanmaz;
  mevcut kurallarla gözden geçirilip birleştirilir. Onaylanmış kök sitemap
  URL'sini robots.txt Sitemap satırına ekleyin.
- OAI-SearchBot ve PerplexityBot gruplarında özel yollar tekrar engellenir.
  training allow/block GPTBot içindir; tüm eğitim botları için genel politika
  değildir. Bot kuralları kimlik doğrulama veya veri güvenliği yerine geçmez.
- llms.experimental.txt opt-in deneydir. Etkinse gözden geçirerek /llms.txt
  adıyla yayınlayabilirsiniz; indeksleme veya alıntı garantisi sağlamaz.

AI ölçümü:
examples/questions.json sabit soru seti ve planı tutar. Köşeli parantezli
soruları gerçek kategori/ihtiyaçla değiştirip sürümleyin; ölçümler arasında
metni değiştirmeyin. Engine, model, tarih ve koşulları gerçek oturumdan kaydedin.
targetUrlPrefixes hedef uygulamanın HTTPS domain/yolunu belirler. citationRate
yalnız bu sınırların içindeki atıfları sayar; rakip veya ilgisiz kaynakları saymaz.
Bu kit AI motorlarına otomatik soru göndermez, sentetik gözlem üretmez.
examples/observations.ndjson bilerek boştur. Her satır bir JSON gözlemdir.
Gerekli sözleşme:
  questionId: plan soru id'si
  engine: plandaki motor adı; model: oturumda görülen model/sürüm etiketi
  locale, country: planla aynı; country iki büyük harfli ülke kodu
  measuredAt: gerçek UTC ISO zaman (ör. 2026-10-29T08:00:00Z)
  repetition: 1..plan.repetitions
  status: measured veya error
  mentioned: boolean
  correctDescription: true / false / null (değerlendirilmedi)
  citedUrls: yanıtta gerçekten atıf verilen HTTP(S) URL dizisi
  evidenceRef: cevabın veya hatanın saklandığı dosya/kayıt referansı
error için mentioned=false, correctDescription=null, citedUrls=[] zorunlu;
bunlar sonuç oranlarına katılmaz. Başarısız denemeyi tekrar işlerken aynı
kimliğin eski error satırını yeni kanıtla değiştirin; iki satırı tutmayın.
Model değişse bile aynı soru/motor/ülke/dil/tekrar yinelenmesi reddedilir.
Kanıt dosyasının varlığı ve değerlendirme doğruluğu insan tarafından kontrol edilir.

Özet: coverage=measured/planned; unobserved henüz kaydı olmayan denemedir.
mentionedRate ve citationRate paydası measured; accuracy paydası yalnız
correctDescription boolean olan measured kayıtlardır. Payda sıfırsa oran
null gelir. Error ve eksik kayıt başarı ya da sıfır sonuç sayılmaz.

Bu dosyalar uygulamalara taşınacak temeli sağlar; canlı entegrasyon, arama
konsolu kurulumu, tarayıcı erişim doğrulaması ve gerçek AI ölçümü ayrıca yapılır.
