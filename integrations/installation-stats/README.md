# AdaPanel günlük kurulum bildirimi

Canlı toplayıcı `https://adapanel.host/installation-stats/`; PHP 8.3 ve IIS kullanır.
Yönetim ekranı `https://adapanel.host/admin/` adresine taşındı; kullanıcı adı ve
değiştirilebilir şifreyle giriş yapılır. `summary.php` ve eski `admin-api.php`
HTTP 410 döndürür; halka açık toplam yoktur. Eski erişim anahtarı geçersizdir.
Giriş ve güncelleme ayrıntıları `integrations/admin/README.md` belgesindedir.

## Saklama ve güvenlik

Web kökünün `App_Data/installation-stats/` klasöründe `config.json`, `registry.json`
ve kilit dosyası bulunur. IIS bu klasöre HTTP erişimini engellemelidir; yayında
doğrulandı. `config.json` yönetici anahtarının SHA-256 özetini ve hız sınırlama
anahtarını içerir. Eski adminHash artık kimlik doğrulamada kullanılmaz; yeni
şifre özeti ayrı `admin-auth.json` dosyasındadır. Dosyalar yalnız hosting hesabı/uygulama kimliğince erişilebilir
olmalıdır. Yedekler de özeldir; herkese açık FTP klasörüne kopyalanmamalıdır.

Kurulumun rastgele kimliği SHA-256 ile özetlenir. Kimlik özeti, sürüm, ilk/son bildirim ve ayrıntı tercihi tutulur. Ayrıntı paylaşımı açıksa doğrulanmış bağlantı IP'si kaydedilir; `lookup_geolocation()` IP'yi HTTPS ile ipwho.is servisine iletir (IPTV uygulamasındaki sağlayıcı). PHP cURL uzantısı ve geçerli CA deposu gerekir; eksikse veya servis başarısızsa konum boş kalır. TLS doğrulaması açık, yönlendirmeler kapalı, toplam süre 2 saniye ve cevap sınırı 4096 bayttır. Lookup kayıt kilidi dışında çalışır; kayıt başına başarısız deneme dahil 24 saat cache ve merkezi günlük 900 istek bütçesi uygulanır. Ayrıntı kapalı/tekrar/geçersiz rapor lookup yapmaz. Cevap ikinci kilitte IP, paylaşım tercihi, lastSeen ve rezervasyon zamanına karşı doğrulanır; eşzamanlı kapatma eski ayrıntıları geri getiremez. Yönetim ülke/şehir için Belirlenemedi, paylaşım kapalı ve eski veri yoksa Paylaşılmadı gösterir.

Ayrıntı kapalı bildirim aynı kimliği kullanır; yalnız version/lastSeen/shareDetails günceller, önceki gerçek IP/ülke/şehir/konum/datacenter bilgileri lastDetailsAt ile geçmiş olarak korunur. Hızlı tekrarda tercih/sürüm değişir, aktivite süresi uzamaz. Eski kayıtlarda son ayrıntı tarihi bir kez önceki lastSeen üzerinden türetilir. Başarılı ülke/şehir geoIp/geoUpdatedAt ile ayrı tarihli tutulur; başarısız yeni sorgu eski sonucu boşaltmaz. 30 gündür bildirim göndermeyen kayıtlar sonraki bakımda temizlenir.
Yeni kayıt hız sınırı için günlük anahtarlı IP özeti kullanılır; iki günlük dönemi aşan özetler bakımda temizlenir. Altyapı/IIS erişim günlükleri ve eski yedekler ayrı saklama politikasına tabidir.

0.8.126 istemcisi başarılı kabulden sonra 24 saat bekler. Başarısız teslimler
5 dakika, 30 dakika, 2 saat, ardından en fazla 6 saat arayla yeniden denenir;
yeniden başlatma veya tercih değiştirme zamanlamayı sıfırlamaz. Eski istemciler
başarısız denemeden sonra da 24 saat bekleyebilir.
48 saat içindeki farklı kimlikler aktif sayılır. Aynı IP'deki farklı kurulumlar
ayrı sayılır. Bir IP'den günde 20 yeni kayıt ve toplam 50.000 kayıt sınırı vardır.
Bu, günlük sürüm bildirimi sayımıdır; lisans doğrulaması veya bağımsız denetlenmiş
kullanıcı sayısı değildir. Kopyalanmış veri klasörleri aynı kimliği paylaşır;
yeniden kurulum yeni kimlik üretebilir. Eksik/bozuk depo HTTP 503 verir; sıfır sayılmaz.

## Panel entegrasyonu

`InstallationReportingService`, `InstallationReportingWorker` ve yöneticiye özel
`/api/v1/installation-reporting` API'si eklenmiştir. Günlük sürüm bildirimi otomatik çalışır; yönetici yalnız ayrıntı paylaşımını kapatabilir. Ayrıntı paylaşımının mevcut açık/kapalı tercihi korunur; yeni tercihlerde varsayılan açıktır. Ayrıntılar kapalıyken günlük sürüm bildirimi ve aktif sayımı aynı kimlikle sürer. HTTPS sertifika doğrulaması açıktır, yönlendirme
takibi kapalıdır. Token API cevabına veya loglara yazılmaz.

İstemci kodunun kullanıcılara ulaşması için yeni AdaPanel kurulum paketi yayımlanmalıdır.
Web sitesi yayını tek başına mevcut panellerde bildirimi başlatmaz.

## Test ve güncelleme

`php integrations/installation-stats/tests.php`

`python integrations/installation-stats/test-http.py`

`python integrations/admin/test-http.py`

İlk kurulum betiğini yeniden çalıştırmayın. Güncellemede yalnız PHP/HTML/CSS/JS
kodu değiştirin; mevcut registry/config ve yönetici anahtarını koruyun.
Yayın sonrası özel JSON dosyalarının 403/404, oturumsuz yeni admin stats isteğinin
401, eski summary/admin-api isteklerinin 410 döndürdüğünü kontrol edin.
Üretim sayısını test kayıtlarıyla artırmayın; testler izole geçici depoda çalışır.

Protokol 2 `schemaVersion`, `installationToken`, `panelVersion`, boolean `shareDetails` kabul eder; yalnız ayrıntılar açıkken opsiyonel geçerli genel IP literal `publicIp` alanı eklenebilir. Protokol 1 eski açık paylaşım tercihiyle uyumludur; ayrıntı açık kabul edilir ve bağlantı IP'si kullanılabilir, eski istemciden yeni konum/datacenter alanları atılır; önceden depolanmış gerçek geçmiş alanlar korunur. Yanıt şema sürümü isteğin sürümüyle eşleşir. Bilinmeyen alanlar reddedilir.

Yayın sırası: önce bu collector ve admin güncellemesini yayımlayın, sonra schema2 istemci paketini dağıtın. Eski collector schema2 raporu reddeder. Registry/config/auth dosyalarını koruyun.


Ayrıntılı paylaşım açık ve günlük bildirim zamanı gelmişse istemci https://ipwho.is/?fields=success,ip adresinden kendi çıkış IP adresini otomatik öğrenir (2 saniye, 4096 bayt, TLS doğrulaması açık, yönlendirme kapalı). Hata bildirimi engellemez. Genel bağlantı IP adresi önceliklidir; bağlantı yerelse geçerli genel istemci IP bildirimi yalnız görüntüleme/konum için kullanılır. ipSource kaynağı connection veya reported_egress olarak tutulur; connectionIp ve tarihi ayrıca gösterilir. İstemci bildirimi güvenlik doğrulaması değildir; hız sınırı gerçek REMOTE_ADDR ile hesaplanır, X-Forwarded-For kullanılmaz. Ayrıntılar kapalıyken bu sorgu ve publicIp alanı yoktur; geçmiş gerçek bilgiler tarihleriyle korunur.
