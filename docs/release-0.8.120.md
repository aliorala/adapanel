# AdaPanel 0.8.120 — SSL kayıt seçimi

24 Ekim 2026 tarihinde yayımlandı: https://github.com/aliorala/adapanel/releases/tag/0.8.120

Kullanıcının ilettiği ekranda bağlı sertifika doğrulanıyor, panelde beklenen daha uzun süreli sertifika doğrulanamıyordu. Ekran tek başına adayın kesin hata nedenini açıklamıyordu. Ev sunucusuna erişilmedi, canlı IIS veya sertifika deposu değiştirilmedi.

## Düzeltme

Önceki reconciliation yolu aynı alan adına ait adayları özel anahtar ve tarihlerine göre sıralıyor, zincir doğrulaması yapmadan beklenen parmak izini değiştirebiliyordu. Ayrı reconciliation seçimi artık LocalMachine My/WebHosting envanterinde SAN, özel anahtar, zaman, ServerAuth ve çevrimiçi iptal kontrolüyle zincir doğrulaması yapıyor. Sertifika alma yolunun eski keşif sözleşmesi değiştirilmedi.

Güvenilir, kesin geçersiz ve belirsiz sonuçlar ayrıdır. Kayıtlı sertifikanın iptal veya ara sertifika erişimi belirsizse, başka aday doğrulansa bile kayıtlı parmak izi korunur. Kısmi depo okumaları başka sertifikaya geçiş veya yanlış yokluk sonucu üretmez. Gerçek, doğrulanan yeni sertifika halen keşfedilir ve IIS eski sertifikayı kullanıyorsa farklılık uyarısı korunur.

İnceleme ekranı artık özel anahtar, alan adı, tarih, güvenilmeyen kök, eksik zincir, iptal veya iptal kontrolüne erişim sorununu ayrı gösterir. Doğrulanamayan aday uygulanamaz. Panel kaydının düzeltilmesi IIS bağlantısının değiştirilmesi değildir.

## Kanıtlar

- Kaynak commit'i: `0b8c7ea`; kaynak geçmişi uzak depoya gönderilmedi, mevcut yöntemle installer yayımlandı.
- Odaklı backend: 93/93. Geri yüklenmiş üretim kaynaklarıyla geniş SSL grubu: 720/720, 0 başarısız/atlanan (`ssl-trusted-reconcile-restored-all.trx`). Bu tur tüm backend testlerinin tekrar koşulduğu iddia edilmez.
- Eski seçim geri getirildiğinde kritik test 1/1 başarısız oldu. Unknown koruması çıkarıldığında 4/4 test başarısız oldu. Kaynaklar geri yüklendi ve son geniş tur geçti.
- Frontend: 141 dosyada 2.707/2.707; tür kontrolü ve üretim derlemesi başarılı. İlk yeni render testi SSR derlemesine uygun fixture'a dönüştürüldü; gerçek derlenmiş şablon hata nedenini ve uygulama engelini doğruluyor.
- 390/1280 piksel, açık/koyu tema: gerçek Vue bileşenleri taklit API ile incelendi; taşma veya JavaScript hatası yok, geçersiz sertifika için uygulama düğmesi yok. Bu bir canlı sunucu testi değildir.
- Bileşen denetimi 22 Passed / mevcut kapsam gereği 3 büyük paket Skipped. Publish envanteri 172 dosya, bulgu yok; installer sürüm/içerik kontrolü geçti.
- GitHub latest 0.8.120, uploaded EXE digest'i yerel hash ile eşleşiyor. Yayımlanan paket tekrar indirilip SHA256 karşılaştırıldı.

Paket: 91.128.952 bayt.

SHA256: `5E290483E6357104048D9A4586825D7766E3C7111F7AB88EB6FCB942F39A997C`

Gerçek ajanlar: `rues_gelistirici` seçim ve regresyonlar; `rues_denetci` bağımsız inceleme. Denetçinin yakaladığı Unknown + Trusted aday karışımı düzeltildi; geri yüklenmiş kaynak için ONAY alındı. Tanılama arayüzü, paketleme ve yayın ana oturumda yapıldı.

Sunucuda yapılacak işlem: 0.8.120'yi kurup SSL sayfasını yenilemek. Uyarı devam ederse yeni ayrıntıdan kesin neden incelenmeli; bu çalışmada ev sunucusundaki aday sertifikanın zinciri doğrudan ölçülmedi.
