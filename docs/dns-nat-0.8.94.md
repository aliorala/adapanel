# AdaPanel 0.8.94 — DNS ve NAT

Bu sürümün yayınlanması kullanıcı tarafından onaylandı. Üretim Windows DNS kayıtları, modem ve kurulu panel değiştirilmemiştir.

## Düzeltilen nedenler

Panel JSON dosyası canlı Windows DNS değişikliklerini okumuyordu. Varsayılan oluşturma yerel IIS binding adresini yayın adresinden önce seçebiliyordu. Posta onarımı genel kayıt yazma yolunu kullanarak mevcut kullanıcı kayıtlarıyla çakışabiliyordu. Alan adı kontrolü binding sırasından etkileniyordu.

Windows DNS aktif ve bölge mevcutken desteklenen canlı kayıtlar doğruluk kaynağıdır. JSON dosyası kayıt kimliklerini ve yönetim metadata bilgisini koruyan önbellektir. Yenileme yayınlama yapmaz. Okuma hatası açık hata verir. Yazma öncesinde etkilenen kayıt grupları yeniden kontrol edilir; başarısız veya doğrulanamayan yayın başarı sayılmaz. Harici DNS değişiklikleriyle işlemler tam bir dağıtık işlem oluşturmaz; kısmi hatalar ayrıca bildirilir.

## Plesk benzeri işlemler

- DNS Şablonu ekranında yerel IPv4 → genel IPv4 eşleştirmesi eklenebilir. IIS binding yerel IP’de kalır; yeni DNS varsayılanları eşleşen genel IP’yi kullanır. Eşleştirme yoksa ayarlı sunucu adresi kullanılır.
- Mevcut alan adlarına uygulama seçilirse yalnız eşleşen yerel/önceki genel IP değerine sahip A kayıtları değiştirilir. İlgisiz çoklu A ve diğer kayıtlar korunur. Bölge bazında sonuç gösterilir; başarısız IP geçişleri yeniden uygulanabilir.
- **Onar** yalnız eksik kayıtları ekler. Mevcut SPF, DKIM, DMARC, MX, TXT, SRV ve çoklu A kayıtlarını korur.
- **Varsayılanlara dön** yöneticinin alan adını yazarak onaylamasıyla bölgenin panel tarafından desteklenen kayıtlarını sunucu şablonuyla değiştirir. Özel kayıtlar da kaldırılır. İşlemden önce yedek alınır. SOA ve panelin desteklemediği Windows DNS kayıt türleri korunur.
- Ana web alan adı ile posta kontrol alan adı ayrı seçilir ve ölçülen posta alan adı ekranda gösterilir.

Bu eşleştirme modem port yönlendirmesi veya ağ kartı yapılandırması yapmaz. Davranış referansları: [Plesk NAT/IP yönetimi](https://docs.plesk.com/en-US/obsidian/administrator-guide/server-administration/ip-addresses-management.59410/) ve [DNS varsayılanlarına dönüş](https://www.plesk.com/blog/various/configuring-dns-for-a-domain-with-plesk/).

## Yedekli geçiş

İlk canlı eşitlemeden önce `C:\ProgramData\AdaPanel\config\dns-zones.pre-native-authority.json` alınır. Tam sıfırlama yedekleri `config\dns-reset-backups`, NAT kayıt yedekleri `config\dns-nat-backups` altındadır. Şablon değişiklikleri de değiştirilmeden önce yedeklenir. Bunlar JSON durum yedekleridir; otomatik Windows DNS geri yükleme düğmesi değildir. Canlı DNS esas olduğu için eski JSON’u yerine koymak tek başına Windows DNS’i geri döndürmez. Güncelleme öncesinde ayrıca Windows DNS bölge dışa aktarımı ve panel yapılandırma yedeği saklanmalıdır.

## Doğrulama

- DNS/onarım/alan adı/sağlık ve ilgili sürüm-katalog testleri: **389 geçti**.
- Son geniş altyapı çalıştırması: **9442 geçti, 0 başarısız, 0 atlanan**. Gerçek Windows hesabı oluşturan iki FtpCreateRollbackTests testi filtreyle kapsam dışında bırakıldı. Son CAA düzeltmesi dahil Release yapılandırmasında DNS/sağlık/onarım/aygıt adı/IIS testleri: **500 geçti, 0 başarısız**.
- Core testleri: **2 geçti**.
- Son arayüz tam çalıştırması: **2644 test / 137 dosya geçti**; TypeScript kontrolü ve üretim derlemesi başarılı.
- Canlı DNS betikleri gerçek PowerShell içinde izole sahte Windows DNS cmdletleriyle sınandı: dış değişiklik, servis yeniden başlatma, art arda Onar, NAT, tam sıfırlama/yedek, çoklu/özel kayıt koruma, okuma/yazma hataları ve eşzamanlı değişiklik.
- Yerel gerçek Vue ekranı ve NAT onayındaki IP eşleştirmesi kontrol edildi. Üretim DNS sağlayıcısında kurulum sonrası prova henüz yapılmadı.
- Geniş altyapı çalıştırmasındaki 13 ilgisiz Windows aygıt adı testi hatası, değiştirilmemiş 0.8.93 kaynakta aynı şekilde doğrulandı. Son kontrolde aygıt adı testleri işletim sisteminin ham ad davranışını varsaymak yerine panelin güvenli ad üretimini ve kalıcı dosya yazımını doğrulayacak şekilde düzeltildi. Özel IIS hata sayfalarında aygıt adlarını reddeden açık kontrol eklendi. Başlangıçtaki sürüm notu testi yeni sürüm bilgileriyle düzeldi.
- Bileşen paket denetimi: **22 geçti**, 3 büyük paket standart seçenekle atlandı. Paketleme sırasında aynı denetim tekrar çalıştırılmadı.

Kurulum paketi üretim derlemesinden hazırlanır; içerik ve sürüm denetimi paketleme betiğince uygulanır. Yayın sonucu aşağıda kaydedilir.

## Yayın paketi

- Sürüm: 0.8.94
- Boyut: 91.175.402 bayt
- SHA-256: 44678848B9855F1BF4B1A05887120B50604352FF8E9B05479D7563FA4E1932DE
- Paket içerik denetimi: başarılı (170 dosya).
- Kurulum dosyası sürüm ve içerik denetimi: başarılı.
- Yayın durumu: 14 Ekim 2026 22:50 (Europe/Istanbul) tarihinde GitHub üzerinden yayınlandı; Latest = 0.8.94. Kullanıcının açtığı Chrome oturumuyla yükleme tamamlandı.
- Yayın: https://github.com/aliorala/adapanel/releases/tag/0.8.94
- Kimliksiz API ile sürüm, yayın durumu, dosya boyutu ve sürüm notlarının kaynakla eşleşmesi doğrulandı. Kurulum dosyası yeniden indirilerek SHA-256 değeri yerel paketle birebir eşleştirildi.
- Kaynak commit: 968e3dc (yerel; kaynak deposu gönderilmedi).
