# İsteğe bağlı ikinci günlük yedek — 0.8.58

Günlük programda “Günde ikinci yedeği al” seçeneği ve kullanıcı tarafından seçilen ikinci saat eklendi. Otomasyon, Yedekleme kısayolu ve abonelik düzenleyicisi aynı alan bileşenini kullanır. Saatler sunucunun yerel saatidir; 03:00 / 15:00 örnektir, canlı program otomatik olarak değiştirilmez.

API ve kalıcı JSON kayıtlarında nullable `BackupScheduleSecondHour` / `ScheduleSecondHour`, ayrıca ikinci dakika alanı bulunur. Eksik veya null saat eski tek çalışma davranışını korur. Günlük dışındaki modlar ikinci saati çalıştırmaz. Özdeş saatler tek çalışma sayılır. Önceki/sonraki çalışma her günün gerçek UTC anları sıralanarak hesaplanır; yaz saati nedeniyle sıralama değişmesi ve aynı ana düşme testlidir. İki saatli programda telafi penceresi sıradaki gerçek saate kalan aralığın yarısıdır.

Abonelik günlük kotası 1 ise birinci saat çalışır ve mevcut kota uyarısı gösterilir. Kayıttaki ikinci saat korunur, plan kotası yükselince yeniden geçerli olur. Çakışan işler için mevcut RunningJobService ve otomasyon kilidi kullanılır. Çok uzun süren işler sırasında geçen program saati mevcut tamamlanma damgası davranışına tabidir; iki iş eşzamanlı başlatılmaz. Saklama süresi değişmedi; tam yedeklerde ikinci çalışma disk ve FTP kullanımını artırır.

Doğrulama: 195 ilgili backend testi (zamanlama, otomasyon, abonelik, budama ve sürüm), 104 frontend testi, typecheck ve build başarılı. Yeni testler keyfî/sırası ters saatler, gün değişimi, eşit saatler, kapatma, eşit olmayan telafi aralıkları, yaz saati, kota ve diske kaydetme/yeniden okuma akışlarını kapsar. İlk test denemesinde testlerin yanlış test sınıfına eklenmesi ve frontend test renderer'ındaki eksik SSR bağlamı düzeltildi; son koşular başarılıdır.

Kanıtlar: `.tmp/audit-058/`. Paketleme ve canlı doğrulama sonucu tamamlandığında aşağıya eklenecek.
