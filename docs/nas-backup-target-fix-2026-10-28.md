# FTP NAS yedek hedefi — 28 Ekim 2026

## Sorun

Bir FTP hedefi özel ağ adresine çözüldüğünde, yedekleme hedef denetimi isteği engelliyordu. Bu davranış istenmeyen iç ağ erişimini önler, ancak kullanıcının bilerek seçtiği yerel NAS için arayüzde dar kapsamlı bir izin yolu yoktu. Eski hata mesajı bütün panel bağlantılarını etkileyen `Panel:AllowInternalOutboundTargets` ayarını açmayı öneriyordu.

## Düzeltmenin kapsamı

Sunucu sahibi, Otomasyon ekranında belirli FTP hedefi için iç ağ izni kaydedebilir veya kaldırabilir. İzin, tam sunucu adı ve etkin portla eşleşir; alt alan adlarına veya başka portlara yayılmaz. Kayıtta parola, kullanıcı adı, sorgu veya yedek klasörü tutulmaz. Yetki, panelin yönetici rolünden ayrı olarak gerçek sunucu sahipliğiyle doğrulanır; taklit oturumları izin yönetemez.

Bu istisna yalnız FTP yedek hedefleri içindir. HTTP, WebDAV, S3, görevler ve diğer dış bağlantıların politikalarını değiştirmez. Özel NAS izni RFC1918 IPv4 ve uygun IPv6 ULA adreslerini kapsar; loopback, link-local, çözülemeyen hedefler ve bilinen bulut meta veri adresleri için geçerli değildir.

İzin her işlemde dosyadan okunur; bu izin değişikliği için servis yeniden başlatılmaz. Bağlantı testi, yükleme, yeniden deneme, uzak dosya doğrulama/listeleme ve silme akışlarının aynı hedef iznini kullanması gerekir. Yalnız bağlantı testinin geçmesi bütün yedek içeriğinin başarıyla üretildiği anlamına gelmez.

## Dağıtım ve sınırlar

Bu belge bir yayın veya canlı NAS testi kanıtı değildir. Yeni kodun kurulu sunucuya dağıtılması ayrıca doğrulanmalıdır. Eski genel `AllowInternalOutboundTargets=true` ayarı geriye uyumluluk için korunur; yeni dar izin bunun otomatik olarak kapatıldığı anlamına gelmez.

FTP taşıyıcısı mevcut `FtpWebRequest` uygulamasını kullanır. Ön denetimden sonra taşıyıcının DNS'i tekrar çözmesi nedeniyle bu değişiklik, FTP için soket düzeyinde DNS sabitlemesi sağladığı iddiasında bulunmaz.

## Doğrulama

- Geliştiricinin ilgili backend test kümesi: 153/153; frontend test kümesi: 65/65; API derlemesi ve arayüzün üretim derlemesi başarılı.
- Bağımsız `rues-denetci`: yeni backend testleri 27/27, ilgili frontend testleri 44/44, TypeScript tür kontrolü ve değişikliklerde boşluk kontrolü başarılı; karar ONAY.
- Testler varsayılan ret, tam sunucu/port eşleşmesi, izin kalıcılığı ve iptali, gerçek bağlantı testi/yedek oluşturma ön denetimi, uzak doğrulama ve silmede FTP komutlarının engellenmesi, sahiplik/taklit oturumu ve arayüzde eski yanıtların yok sayılmasını kapsar.
- İzin dalı geçici olarak devreden çıkarılınca ilgili test beklenen şekilde başarısız oldu. Kaynak birebir geri getirildikten sonra 153 test yeniden geçti; mutation kanıtı `.tmp/nas-trust-mutation.log`.

Canlı ev sunucusunda kullanıcının kaydettiği `AllowInternalOutboundTargets=true` değeri Not Defteri'nde doğrulandı. Yalnız AdaPanel Windows hizmeti yeniden başlatıldı; ardından aynı FTP bağlantı testinde iç ağ yasağı ortadan kalktı. Ancak test başarılı olmadı: FTP klasör hazırlığı ve yükleme `Unable to connect to the remote server` hatası verdi; sunucudan bir FTP yanıt kodu alınmadı.

İstemci bilgisayarda ayrı, yalnız seçilen hedefe yönelik kontrolde NAS adı özel ağ adresine çözüldü; TCP 21 bağlantısı 4 saniyede zaman aşımına uğradı. Bu, sunucudaki testin yerine geçmez ve tek başına modem/güvenlik duvarı/FTP servisinden hangisinin sorumlu olduğunu kanıtlamaz. NAS'ın FTP servis durumu ve doğru portu ayrıca doğrulanmalıdır. Canlı yedekleme başarılı olarak raporlanmadı.

Ekran kanıtı: `.tmp/migration-home/home-nas-after-restart-connection-failure.png`. Yeni hedefe özel izin kodu bu canlı test sırasında henüz sunucuya dağıtılmamıştı.

## Canlı FTP bağlantısının çözümü

Kullanıcı, hedefin NAS değil bu istemci bilgisayardaki FileZilla Server olduğunu ve yerel IP'sinin `192.168.1.100` olduğunu doğruladı. FileZilla Server hizmeti çalışıyor ve TCP 21'i dinliyor; yerel adrese bağlantıda FTP karşılama yanıtı alındı.

`ev.alioral.com.tr`, istemcinin ev ağı DNS'inde `192.168.1.120`, dış DNS sorgusunda evin genel IP'si olarak döndü. Genel IP üzerinden TCP 21 FileZilla yanıtı verirken `192.168.1.120:21` bağlantıyı reddetti. Eski sunucuda çalışan alan adı bu ağ içinde farklı hedefe gidiyordu.

Evdeki Dell AdaPanel otomasyonunda hedef `ftp://192.168.1.100` olarak değiştirildi. Mevcut kimlik bilgileriyle sunucunun kendi bağlantı testi çalıştırıldı ve **“Bağlantı başarılı — hedefe yazılabiliyor”** sonucu görüldü; ayarlar kaydedildi. Kullanıcı da başarılı sonucu doğruladı. Modem, DNS ve FileZilla ayarları değiştirilmedi.

Bu sonuç küçük test dosyasının yazıldığını doğrular; bütün sitelerin/veritabanlarının tam yedeğinin başarıyla üretildiği veya geri yükleme denemesinin geçtiği anlamına gelmez. Önceki VideoTools kilitli dosya uyarıları bu işlemde çözülmedi. Yeni kod düzeltmesi de bu bağlantı için canlıya dağıtılmadı.

Son ekran kanıtı: `.tmp/migration-home/home-ftp-local-target-success.png`.
