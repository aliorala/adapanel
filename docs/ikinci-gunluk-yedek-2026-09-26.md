# İsteğe bağlı ikinci günlük yedek — 0.8.58

Günlük programda “Günde ikinci yedeği al” seçeneği ve kullanıcı tarafından seçilen ikinci saat eklendi. Otomasyon, Yedekleme kısayolu ve abonelik düzenleyicisi aynı alan bileşenini kullanır. Saatler sunucunun yerel saatidir; 03:00 / 15:00 örnektir, canlı program otomatik olarak değiştirilmez.

API ve kalıcı JSON kayıtlarında nullable `BackupScheduleSecondHour` / `ScheduleSecondHour`, ayrıca ikinci dakika alanı bulunur. Eksik veya null saat eski tek çalışma davranışını korur. Günlük dışındaki modlar ikinci saati çalıştırmaz. Özdeş saatler tek çalışma sayılır. Önceki/sonraki çalışma her günün gerçek UTC anları sıralanarak hesaplanır; yaz saati nedeniyle sıralama değişmesi ve aynı ana düşme testlidir. İki saatli programda telafi penceresi sıradaki gerçek saate kalan aralığın yarısıdır.

Abonelik günlük kotası 1 ise birinci saat çalışır ve mevcut kota uyarısı gösterilir. Kayıttaki ikinci saat korunur, plan kotası yükselince yeniden geçerli olur. Çakışan işler için mevcut RunningJobService ve otomasyon kilidi kullanılır. Çok uzun süren işler sırasında geçen program saati mevcut tamamlanma damgası davranışına tabidir; iki iş eşzamanlı başlatılmaz. Saklama süresi değişmedi; tam yedeklerde ikinci çalışma disk ve FTP kullanımını artırır.

Doğrulama: 195 ilgili backend testi (zamanlama, otomasyon, abonelik, budama ve sürüm), 104 frontend testi, typecheck ve build başarılı. Yeni testler keyfî/sırası ters saatler, gün değişimi, eşit saatler, kapatma, eşit olmayan telafi aralıkları, yaz saati, kota ve diske kaydetme/yeniden okuma akışlarını kapsar. İlk test denemesinde testlerin yanlış test sınıfına eklenmesi ve frontend test renderer'ındaki eksik SSR bağlamı düzeltildi; son koşular başarılıdır.

Kanıtlar: `.tmp/audit-058/`. Canlı doğrulama tamamlandı.

## Yayın ve canlı doğrulama

195 ilgili backend ve104 frontend testi, typecheck/build başarılı. Kaynak4acb4bb. Standart paket denetimi21Passed/3mevcut büyükpaketSkipped/1PHPStaleFallback; installer denetimi geçti. Paket90.818.087bayt SHA256 2DAF8F8317BECF548E76FE50DD8453ABBDA962DDFCFD92958596F13B3DEF5E92; GitHub yeniden indirme ve canlı aynı. 0.8.58 canlı kurulum succeeded/exit0/error boş, isFinished=true/isFailed=false, updatedAtUtc=2026-09-26T12:01:45.6321622Z. Panel/MariaDB/Redis/DNS/FTP/IIS/WAS Running; Stalwart9164; üçHTTPS200.

Canlı otomasyonda03:00+20:45 ve abonelik formunda03:00+06:15 kaydetmeden denendi; masaüstü1524/1524px taşma yok. Yenileme/iptal sonrası günlük03:00, ikinci saatkapalı, saklama5gün, şifrelemekapalı korundu. Yedek ayarları kısayolunda da seçenek var. Canlıda iki gerçek zamanlanmış tur henüz çalıştırılmadı; zamanlama/saklama/kota senaryoları otomatik testlerle doğrulandı. Ayrıntı docs/ikinci-gunluk-yedek-2026-09-26.md, kanıt .tmp/audit-058. Kaynak/history/tag push yapılmadı.
