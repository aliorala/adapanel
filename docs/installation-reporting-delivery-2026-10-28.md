# Günlük kurulum bildirimi ve otomatik konum — 28 Ekim 2026

## Canlı teşhis

Ev sunucusunda AdaPanel 0.8.125 çalışıyordu. Ayrıntı paylaşımı açıktı; yerel durum dosyasında son deneme 28 Ekim 2026 11:20:05 (UTC+03), son hata `timeout`, son kabul zamanı ise boştu. Önceki sürüm başarısız denemeyi de günlük sınıra dahil ettiğinden 24 saat yeniden denemiyordu. Özel yönetimde hem toplam kayıt hem aktif kayıt sıfırdı.

Site taşımasından kalan merkezi toplayıcı eski şema sürümündeydi. 28 Ekim 11:48'de yalnız 13 program dosyası yeni toplayıcı/yönetim sürümüyle değiştirildi. `App_Data` içindeki kimlik doğrulama, ayar, oturum ve kayıt dosyaları pakete dahil edilmedi. Normal TLS üzerinden yönetim HTML/JS/CSS ve bilgilendirme sayfasının kaynakla SHA-256 eşleşmesi doğrulandı. Kimliksiz yönetim API isteği 401, GET heartbeat isteği 405, özel kayıt dosyası isteği 404 verdi.

## Kod değişikliği

- Canlı IIS yolunda `.NET PostAsJsonAsync` ile parçalı gönderilen geçersiz `{}` isteği 12.074 ms sonunda zaman aşımına uğradı. Aynı uç ve içerik `StringContent` ile 138 ms'de beklenen HTTP 400 yanıtını verdi. Bu karşılaştırma kayıt oluşturmadan yapıldı. İstemcinin gerçek JSON gövdesi önce UTF-8 baytlarına çevrilip belirli `Content-Length` ile gönderilecek şekilde düzeltildi.
- Başarılı bildirimler 24 saatte bir; başarısız denemeler 5 dakika, 30 dakika, 2 saat, ardından en fazla 6 saat arayla tekrar denenir.
- Zamanlama diske ağ isteğinden önce yazılır; yeniden başlatma veya tercih değişikliği bekleme süresini sıfırlamaz. Aynı durum dosyasını kullanan servis örnekleri ortak kilitle korunur.
- Tercih kaydında zamanı gelen bildirim hemen denenir. Kabul yalnız geçerli `accepted=true` ve doğru şema yanıtıyla kaydedilir.
- Panel son deneme, son kabul, sonraki deneme ve hata bilgisini ayrı gösterir.
- Konum/datacenter giriş alanları kaldırıldı. Ayrıntı paylaşımı açıkken merkezi toplayıcı bağlantı IP adresinden ipwho.is üzerinden yaklaşık ülke ve şehri bulur. TLS doğrulaması açık; kısa zaman aşımı, yanıt sınırı, önbellek ve günlük kota uygulanır. Uydurma konum/datacenter yazılmaz.
- Ayrıntı paylaşımı kapalıyken yeni IP/konum alınmaz. Önceki gerçek bilgiler tarihli geçmiş kayıt olarak kalır; günlük sürüm ve sabit rastgele kurulum kimliği bildirimi sürer.
- Aynı kimlik son 48 saatte bildirim yaptıysa bir aktif kurulum sayılır. Üretime deneme amaçlı kurulum kaydı eklenmedi.

## Doğrulama

Teslimat değişikliği: 26 backend ve 9 frontend testi, Vue TypeScript kontrolü ve üretim derlemesi geçti. Bağımsız denetçi aynı dar testleri tekrar çalıştırıp ONAY verdi. Toplayıcı PHP testleri ve toplayıcı/yönetim HTTP senaryoları gerçek kayıt deposundan ayrı geçici dizinlerde geçti.

0.8.126 kurulum paketinin son kontrolleri ve canlı sunucudan ilk gerçek bildirimin kabul doğrulaması sürüyor. Bu belge tek başına canlı teslimatın başarılı olduğu anlamına gelmez.
