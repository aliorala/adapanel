# FTPS sertifika yaşam döngüsü — 28 Ekim 2026

## Sorun ve kapsam

Canlı dış FTP hedefi, AdaPanel'in IIS FTP hizmeti yerine başka bilgisayardaki
FileZilla hizmetine yönleniyordu. AUTH TLS isteği 502 ile reddediliyordu; kimlik
bilgileri gönderilmedi. Kullanıcı modem yönlendirmesini düzelttikten sonra IIS
AUTH TLS isteğine 234 verdi; fakat bilgisayar adına ait kendinden imzalı sertifika
sundu. Ad eşleşmesi ve güven zinciri başarısızdı. İki hata ayrı ölçüldü.

Kodda tam altyapı yapılandırması mevcut FTP sertifikasını AdaPanel FTPS adlı
kendinden imzalı sertifikayla değiştirebiliyordu. Atama hatası yutuluyor, yalnız
sertifika hash'inin dolu olması gerçek TLS doğrulaması yerine kullanılabiliyordu.
Geçerli sertifika seçme, FTP adına göre yenileme ve mevcut/yeni hesaplara doğru
bağlantı adresini aktarma tamamlandı.

## Uygulama

- Sunucu genelindeki IIS FTP hizmeti için açık DNS bağlantı adı kaydedilir.
  Tüm mevcut/yeni hesaplar bu adresi kullanır; her siteye ayrı sertifika verilmez.
  [Plesk'in resmî davranışı da sunucu genelinde FTPS sertifikasıdır](https://support.plesk.com/hc/en-us/articles/12377457047959-Is-it-possible-to-install-a-certificate-to-secure-FTP-for-a-specific-domain-on-Plesk-server).
- Yerel makinenin My/WebHosting deposunda SAN, süre, özel anahtar, sunucu
  kimlik doğrulama amacı ve normal güven zinciriyle sertifika seçilir.
- Admin yetkisi ve ortak FTP mutasyon kilidi altında hash/depo yazılır; mevcut
  kontrol/veri kanalı politikaları karşılaştırılıp korunur, bağlama geri okunur.
- Sabit 127.0.0.1:21 üzerinden AUTH TLS ve seçilen DNS adıyla SNI/ad/zincir/parmak izi
  doğrulaması yapılır. Kullanıcı adı/parola gönderilmez. Başarısızsa eski bağlama
  geri alınır; geri alma başarısızlığı ayrıca bildirilir.
- Yerel sertifika doğrulaması; modem üzerinden dış erişimden ve kimlik doğrulamalı
  veri aktarımından ayrı gösterilir. FTPS'ten düz FTP'ye otomatik geçiş yapılmaz.
- Eski RequireSsl ve bilinmeyen JSON alanları korunur. Hesaplar, parolalar, site
  dosyaları ve mevcut TLS politikaları sertifika işlemiyle değiştirilmez.
- Windows sertifikası yenilendiğinde yalnız kayıtlı FTP adına uygun sertifika
  eşitlenir. Kullanılan FTP sertifikası Windows deposu temizliğinde korunur.
- FTP API/arayüzüne gerçek durum, ortak bağlantı adı ve Türkçe/İngilizce hata
  açıklamaları eklendi. Eski oturum veya site bağlamına ait sonuçlar atılır.

## Testler ve denetim

- Backend uygulayıcı son derlemeyle 147 FTP/TLS/SSL testi ve ayrı 41 hesap yaşam
  döngüsü testini çalıştırdı: tamamı başarılı. Birleşik turda aynı statik test
  hesabını paylaşan referans sayacı testi çakıştı; aynı test izole çalışınca geçti.
- Bağımsız rues-denetci: 144 backend ve 14 yeni arayüz testi başarılı, ONAY.
- Ana oturumun birleşik posta/FTP/çeviri arayüz kontrolü: 133/133 başarılı.
- vue-tsc, üretim derlemesi ve diff kontrolü başarılı.
- Başarısız AUTH TLS kontrolü kaldırıldığında backend regresyon testi kırıldı;
  arayüzde doğrulama koşulu kaldırıldığında üç test kırıldı. Kaynaklar geri alındı
  ve kontroller tekrar başarılı oldu.
- İki rues-gelistirici backend/arayüzü ayrı dosyalarda uyguladı; farklı
  rues-denetci bağımsız kontrol etti. Canlı kontroller ana oturumda yürütüldü.

0.8.130'un canlı uygulaması ve son veri kanalı ölçümü henüz tamamlanmadı.

## Resmî kaynaklar

- https://learn.microsoft.com/en-us/iis/configuration/system.applicationhost/sites/sitedefaults/ftpserver/security/ssl
- https://docs.plesk.com/en-US/obsidian/administrator-guide/plesk-administration/securing-plesk/using-secure-ftp.72173/
