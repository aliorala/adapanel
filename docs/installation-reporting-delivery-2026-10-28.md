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

Son tamponlanmış gönderim testiyle backend dar teslimat kümesi 27/27 oldu; bağımsız denetçi aynı Release testlerini doğruladı. Arayüzün geniş kümesi 2.775 testi geçti. Geniş backend kümesindeki iki audit kapsam sınıflandırması düzeltildikten sonra ilgili 62 test geçti; geniş backend kümesi tekrar koşulmadı.

0.8.126 son paketi ev sunucusuna kuruldu ve yayımlandı. Önceki başarısız denemenin yalnız `nextAttemptAtUtc` zamanı bir kez öne alındı; kimlik, paylaşım tercihi, hata geçmişi ve başarılı kabul zamanı değiştirilmedi. Panelden aynı tercih kaydedilerek gerçek bildirim denendi. 28 Ekim 2026 12:30:46'da kabul edildi; hata boş, sonraki bildirim 29 Ekim 12:30:46 oldu. Özel yönetimde 1 kayıtlı / 1 aktif kurulum ve sürüm 0.8.126 görüldü. Yayımlanan kurulum dosyası tekrar indirilip SHA-256 eşleşmesi doğrulandı.

Ek canlı bulgu: merkezi toplayıcı bu bağlantıyı 127.0.0.1 olarak görüyor. Bu nedenle ülke/şehir bilinmiyor kaldı. İstemci başlıklarına koşulsuz güvenerek sahte IP kabul edilmedi.

## 0.8.127: yerel bağlantıda otomatik çıkış IP'si

Yalnız ayrıntı paylaşımı açık ve günlük bildirim zamanı gelmişse istemci sabit HTTPS ipwho.is adresinden kendi çıkış IP'sini öğrenir. Sorgu iki saniye ve 4096 baytla sınırlıdır; sertifika doğrulaması açıktır, yönlendirme izlenmez. Sağlayıcı hatasında günlük bildirim devam eder. Kapalı paylaşımda sağlayıcı sorgusu ve `publicIp` alanı yoktur.

Merkezi toplayıcı genel bağlantı IP'sini tercih eder. Bağlantı yerelse otomatik bildirilen genel IP'yi görüntüleme ve konum sorgusunda kullanır; kaynak `reported_egress` olarak ayrılır. Bağlantı adresi kendi tarihiyle ayrıca tutulur. İstemci bildirimi kimlik doğrulama veya hız sınırında kullanılmaz. Eski IP/konum, paylaşım kapatma veya başarısız yeni IP sorgusunda korunur. Ülke/şehir IP'nin yaklaşık konumudur; fiziksel sunucu adresi veya datacenter garantisi değildir.

42 backend, 67 PHP ve 9 arayüz testi, iki izole HTTP test grubu, TypeScript ve üretim derlemesi geçti. Bağımsız denetçinin bulduğu 6to4 adres doğrulama farkı iki tarafta reddedilerek düzeltildi. Publish 174 dosya ve sıfır bulgu; kurulum paketi sürüm/içerik denetimi geçti.

0.8.127 toplayıcısı canlıya uygulandı; yönetim HTML/JS ve bilgilendirme sayfası kaynakla hash olarak eşleşti. App_Data güncelleme paketine dahil edilmedi. 0.8.127 ev sunucusuna kuruldu, panelde sürüm doğrulandı ve GitHub'a yayımlandı. İndirilen kurulum paketi kaynak paketle SHA-256 olarak eşleşti. Güncelleme sonrası özel yönetimde aktif/kayıtlı sayı 1 kaldı. Panelde son kabul 28 Ekim 12:30:46, sonraki deneme 29 Ekim 12:30:46 ve hata Yok doğrulandı; başarılı günlük kabul zamanı sıfırlanmadı. Bu nedenle yeni çıkış IP'si, konum ve raporlanan sürüm bir sonraki günlük bildirimde güncellenir. Bu oturumda 0.8.127'nin üretim konum kaydı henüz alınmadı; sunucunun sağlayıcıya gerçek GET isteği başarıyla yaklaşık ülke/şehir döndürdü. Üretime sahte kayıt eklenmedi.
