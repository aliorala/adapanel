# adapanel.host — ayrı web sitesi

Bu klasör AdaPanel ürününün tanıtım sitesidir; panel arayüzü değildir. Site dosyaları
AdaPanelSetup.exe içine alınmaz ve `src/AdaPanel.Api/wwwroot` dizinine kopyalanmaz.

## Üretim ve doğrulama

Python 3 ile, depo kökünde:

```text
python scripts/build-website.py
python scripts/test-website.py
```

Kaynak şablon `scripts/website/index.template.html`, içerik ve metadata
`scripts/build-website.py` içindedir. FTP için temiz çıktı yalnız
`artifacts/website-public/` klasörüdür. Bu klasörün içeriğini adapanel.host sitesinin
FTP köküne yükleyin; depo kaynaklarını, testleri, README dosyasını, yedekleri veya
AdaPanel kurulum paketini FTP alanına yüklemeyin.

Üretilen sayfalar: `/` (Türkçe), `/en/` (İngilizce), `/kurulum/` ve
`/en/installation/`. Sunucuda build veya JavaScript gerektirmez. Buradaki statik
sayfalar üretilen çıktının kaynak kontrolde tutulan kopyasıdır.

## Yayın ve geri dönüş

Önce uzak `index.html` ve `web.config` dosyalarını yerel, web dışı bir yedeğe alın.
Mevcut `web.config`, App_Data ve diğer uygulama dosyalarını koruyun. Yeni dosyaları
geçici isimle aktarın ve içerik özetlerini kontrol ettikten sonra asıl adlarına
taşıyın. Varlıkları ve alt sayfaları önce, ana sayfayı en son yayınlayın.

Yayından sonra dört sayfayı, robots.txt, sitemap.xml ve assets dosyalarını HTTPS
üzerinden tekrar indirerek yerel çıktıyla karşılaştırın. Geri dönüşte yedekteki
ana sayfayı geri yükleyin; site dışındaki alan adlarına/DNS kayıtlarına dokunmayın.

## Arama ve yanıt sistemleri

Sayfalar kendine ait canonical, karşılıklı hreflang, Open Graph, JSON-LD ve HTML
olarak okunabilir içerik sağlar. FAQ verisi görünen soru/cevaplarla aynı kaynaktan
üretilir. Sıralama, zengin sonuç veya yapay zekâ alıntısı garanti edilmez.

Search Console ve Bing Webmaster Tools hesabından sitemap gönderimi ve gerçek
arama performansı takibi ayrı işlemlerdir. Doğrulama kodu veya ölçüm hesabı bu
sitede varsayılarak oluşturulmaz. Özel yapay zekâ dosyası zorunlu değildir:
https://developers.google.com/search/docs/appearance/ai-features
