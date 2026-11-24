# AdaPanel 0.8.137 yayın ve site doğrulaması — 29 Ekim 2026

## Yayımlanan paket

- Kaynak commit: `79da6a8`; tag: `0.8.137`.
- Sürüm: https://github.com/aliorala/adapanel/releases/tag/0.8.137
- `AdaPanelSetup.exe`: 91.237.746 bayt.
- SHA-256: `75C5272808F8A7C92EAEF8492DAD40BC720FC0FF7B6D2D95E341D0D4DB4701D0`.
- Paket yalnız commit edilmiş temiz kaynak arşivinden derlendi. Beş sürüm
  kaynağı tutarlı; frontend üretim derlemesi, API publish, .htaccess modülü,
  publish içeriği ve kurulum paketi doğrulamaları geçti. GitHub'dan indirilen
  varlık yerel paketle birebir eşleşti; yayın notu UTF-8 geri okuması geçti.
- Bileşen tanımları değişmediği için önceki doğrulanmış bileşen denetiminin
  tam rapor/manifest özetleri yeniden kontrol edilerek kullanıldı (22 başarılı,
  üç büyük paket atlanmış). Yeni bileşen ağ taraması yapılmadı. Yeni üretilen
  publish çıktısı ve installer ayrıca denetlendi.

Mail DNS düzeltmesi teknik IIS site adını gerçek posta alan adıyla doğru
eşleştirir ve başka bölgeye yazma/silmeyi engeller. Kurulumla birlikte DNS kayıt
migrasyonu veya MX/SPF/DKIM değiştiren bir işlem eklenmedi. Önceki posta kimliği,
Webmail ve FTP değişiklikleri kaynak geçmişinde korunur. Ayrıntılı test ve kök
neden `mail-ftp-local-review-2026-10-29.md` belgesindedir (34/34 dar test;
bağımsız ilgili grup 18/18; mutasyonlar hatayı yakaladı).

## Canlı SEO yayını

`artifacts/website-public` çıktısı, kaydedilmiş yalnız bu siteye ait profil
üzerinden FTPS ile yayımlandı. TLS zinciri ve sunucu adı doğrulaması açık kaldı;
veri kanalı şifrelendi. Mevcut 66 URL korunarak 66 değişen public dosya güncellendi.
Rehber dosyalarındaki ek değişiklikler satır sonu boşluk normalizasyonudur;
değişmeyen rehberlerin tarihleri ileri alınmadı.

- 75 public dosya HTTPS üzerinden geri indirildi ve yerel çıktıyla birebir
  karşılaştırıldı; 66 HTML sayfa, varlıklar, robots, sitemap ve doğrulama dosyaları.
- 68 kalıcı yönlendirme kontrolü geçti: HTTP→HTTPS, canonical index yolları ve
  sorgu parametresinin korunması.
- `web.config`, yönetim giriş kodu ve istatistik giriş kodunun önce/sonra
  özetleri aynı; yönetim ekranı HTTP 200 verdi. Kullanıcı verileri değiştirilmedi.
- Yalnız bu yayına ait geçici uzak dosya ve yedekler silindi. Geri dönüş kopyası
  yerelde, web dışında korundu. Diğer uzak dosyalar silinmedi.
- Googlebot, bingbot, OAI-SearchBot ve PerplexityBot User-Agent istekleri mevcut
  rehber için aynı HTML ve HTTP 200 verdi. Bu, gerçek bot IP erişimi veya
  indekslenme kanıtı değildir; bu tur Search Console/Bing oturumundan gönderim yok.
- Yerel 14/14 site testi ve 6/6 ölçüm testi tekrar geçti. Önceki mobil/masaüstü
  tarayıcı kontrolleri aynı içerik üzerinde geçti; bu yayında tarayıcı kontrol
  aracı yoktu. Canlı doğrulama HTTP ve dosya özeti karşılaştırmasıyla yapıldı.
- Gerçek AI ölçümü hâlâ yapılmadı: 72 planlı, sıfır ölçülmüş gözlem. Performans
  oranları uydurulmadı. Ölçüm dosyaları, kaynaklar ve kanıtlar web'e yüklenmedi.

Web dışı kanıtlar: `artifacts/website-publish-20261029.json`,
`.tmp/release137-public-readback.json`, `.tmp/release137-publish.log`,
`.tmp/release137-package.log`, `.tmp/release137-component-audit-reuse.json`.

## Ev sunucusu kurulumu ve posta durumu

Kullanıcı ev sunucusunda güncellemeyi tamamladığını ve panelde 0.8.137 sürümünü
gördüğünü bildirdi. Kurulum durumu bu açık kullanıcı teyidine dayanır; bu
oturumda tarayıcı/uzak masaüstü kontrol aracı bulunmadığından kurulu ikilinin
sürümü bağımsız olarak okunmadı.

Yayın sonrası mevcut çalışan sunucuda üç posta adı için 587 STARTTLS ve 465 TLS
üzerinden altı gerçek sertifika doğrulaması geçti. Karşılama adı
`mail.adapanel.host`; alan adı, süre ve güven zinciri geçerli. Bu kontrol mevcut
sunucuya aittir. Kullanıcının kurulum teyidinden sonra aynı altı test yeniden
çalıştırıldı: tamamı geçti, karşılama adı yine `mail.adapanel.host` kaldı.
Türkçe/İngilizce ana sayfa, robots.txt ve sitemap.xml canlı içerikleri yerel
çıktıyla eşleşti; yönetim giriş ekranı da HTTP 200 verdi. Kanıtlar
`.tmp/mail-sni-probe/release137-after-install.json` ve
`.tmp/release137-after-install-web.json` dosyalarındadır.

Önceki dış teslimat testinin uzak MX kaynaklı Spamhaus reddi devam eden ayrı
engeldir; posta DNS düzeltmesi bunu giderdi diye raporlanmadı. Bu son kontrolde
yeni bir dış teslimat denemesi yapılmadı.

## Çalışan gerçek ajanlar

Önceki geliştirme `rues-gelistirici` rolleriyle, bağımsız inceleme farklı
`rues-denetci` ile tamamlandı. Bu yayında `/root/integration_independent_review_oct29`
son sürüm/açıklama/README ve boşluk normalizasyonuna da ONAY verdi. Erişim
envanteri için açılan `/root/release137_access_inventory` yönlendirme zaman
aşımıyla başlayamadı; erişim ve yayın işlemlerini ana ajan tamamladı.
