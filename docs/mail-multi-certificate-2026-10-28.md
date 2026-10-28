# Çoklu posta sertifikası düzeltmesi — 28 Ekim 2026

## Kök neden

Stalwart'a bağlanılabiliyor fakat mail.travianpanel.com için sunulan yaprak sertifika
yalnız mail.videotools.com.tr SAN'ını içeriyor. Bu bir güven zinciri hatası değil,
sunucu adı uyuşmazlığıdır. config/mail-cert.json yalnız son MailCertificateState
kaydını tuttuğundan başka alan adı eklendiğinde öncekinin yenileme/eşitleme takibi
kaybolabiliyordu. Alan adı almayan durum sorgusu da seçili Travian alanında
VideoTools sertifikasını başarılı gösteriyordu.

## Kaynak değişiklikleri

- MailCertificateRegistry: eski tek kaydı koruyarak certificates[] envanteri;
  primaryHostName ile ilk posta/Roundcube ilişkisinin korunması; büyük/küçük harfli
  eski JSON alanlarının güvenli birleştirilmesi ve bilinmeyen metaverinin korunması.
- MailService ve CertificateIntegration: alan adına göre durum ve istemci adı;
  bütün sertifikaların yenileme/eşitleme takibi; önce Windows deposundaki uygun
  sertifikanın kullanılması, yoksa ACME akışı. Diğer alanın hatası bağımsız alanları
  engellemez; hata sonuçtan gizlenmez.
- MailCertificateStoreReference ve WebmailTlsConfiguration: bütün sertifikaların
  kullanım koruması ve ilk ortak posta adının korunması.
- IMailService, MailServiceRouter, MailEnable platformu ve MailController:
  seçili domain parametresinin uçtan uca aktarılması.
- MailTlsProbe ve DTO: gerçek SAN, başlangıç/bitiş ve zincir durumları.
- frontend API, MailView, TR/EN: alan değişiminde eski durum/form temizlenir;
  yanlış alan adına dönen cevap reddedilir; self-signed başarı izlenimi kaldırılır.

Stalwart Certificate kaydı hedef SAN'a göre eşleşir. Var olan global varsayılan ve
başka alanın kaydı korunur; paylaşılan SAN/wildcard çakışması sessizce ezilmez.
Sertifika uygulama, ReloadTlsCertificates ve doğrulamalı SMTP testlerini içerir.
TLS doğrulaması kapatılmaz; özel anahtarlar loglara veya rapora yazılmaz.

## Otomatik kontroller

- Birleşik hedefli backend kontrolü: 539/539 başarılı.
- Posta sertifikası, kutu sayımı, alan bağlamı ve çeviri frontend kontrolleri:
  57/57 başarılı.
- vue-tsc ve Vite üretim derlemesi başarılı.
- rues-gelistirici uyguladı; farklı rues-denetci bağımsız kontrol ederek ONAY verdi.
  rues-arastirmaci Stalwart 0.16.26 belge/kaynak sözleşmesini doğruladı.

## Canlı başlangıç ölçümü

28.10.2026 18:04 (+03), dış ağdan SNI ve normal zincir/ad doğrulaması ile:

| Sunucu adı | 587 STARTTLS | 465 TLS |
|---|---|---|
| mail.travianpanel.com | Ad uyuşmazlığı | Ad uyuşmazlığı |
| mail.videotools.com.tr | Başarılı, TLS 1.3 | Başarılı, TLS 1.3 |

Sunulan SAN mail.videotools.com.tr; Let's Encrypt YR1; geçerlilik
16.10.2026 08:52:33Z–14.01.2027 08:52:32Z. VideoTools parmak izi
F257474DAE80AED77926925B33066CAA4E3EEF99. Zincir geçerli.
Test yalnız EHLO/STARTTLS/TLS el sıkışması yapar, AUTH/DATA göndermez.

## Canlı düzeltme ve son ölçüm

0.8.128 yayımlandı; indirilen kurulumun SHA-256 değeri kaynak paketle eşleşti
(C0A0585BF76C5E71B874D26A5FEFA31F00460B6455BA37533AE35A0AF78D35F8).
Ev sunucusunda çalışan sürüm 0.8.128 olarak doğrulandı.

Mevcut sertifikayı kullan işlemi mail.travianpanel.com için uygun, güvenilir ve
özel anahtarlı Windows sertifikası bulamadı; mevcut kayıtları değiştirmedi.
ACME ile yeni Let's Encrypt sertifikası alındı, zincir/özel anahtar Stalwart'a
uygulandı ve TLS haritası yeniden yüklendi. Yeni kayıt jlxuyeemagqa;
eski global varsayılan jjpqdqkqabqa olarak korundu.

Bağımsız dış bağlantı testi sonuçları (.tmp/mail-sni-probe/after.json):

| Sunucu adı | 587 STARTTLS | 465 TLS | Bitiş (UTC) |
|---|---|---|---|
| mail.travianpanel.com | Başarılı | Başarılı | 26.01.2027 14:27:26 |
| mail.videotools.com.tr | Başarılı | Başarılı | 14.01.2027 08:52:32 |

Her bağlantıda istenen adın SAN eşleşmesi, süre ve normal güven zinciri doğrulaması
başarılıdır. Travian parmak izi 086E697445A9402712665D3E4906D81136DF9410;
SAN yalnız mail.travianpanel.com; başlangıç 28.10.2026 14:27:27Z.
VideoTools parmak izi başlangıçla birebir aynıdır. TLS doğrulaması kapatılmadı.

Travian Panel'in Sertifikadaki sunucu adı alanı açıkça mail.travianpanel.com olarak
kaydedildi. 587 STARTTLS ile Bana test e-postası gönder sonucunda uygulama
"Test iletisi SMTP sunucusuna teslim edildi" döndürdü. Bu, SMTP kabulüdür;
alıcının gelen kutusuna teslimi ayrıca görülmedi. SMTP parolası korunmuştur.

Posta adının 443 web uç noktası ayrı doğrulanamıyor; bu, başarılı 587/465 ölçümünden
ayrı gösterilir. Mevcut web sitesi sertifikaları ve MX/DKIM ayarları değiştirilmedi.

## Canlıda bulunan ek kutu sayımı uyumluluğu

0.8.128 kurulunca sıfır yerine Ölçülemedi görüldü. Canlı Stalwart JSON @type User/Group
kullanır; Account/User ve Account/Group CLI oluşturma yollarıdır. Dar 0.8.129 düzeltmesi
kısa JSON biçimlerini de tanır; gruplar bireysel hesap sayımına dahil edilmez. Bilinmeyen
türler ölçülemedi kalır. İlgili 39 backend testi başarılı; bağımsız denetçi 33 hesap
testini tekrar çalıştırarak ONAY verdi. Düzeltme kaldırıldığında gerçek JSON biçimini
ölçen dört testin başarısız olmasıyla testlerin hatayı yakaladığı doğrulandı.

0.8.129 paketinin canlı kurulumu ve son kutu sayımı henüz doğrulanmadı.

## Kaynaklar

- https://stalw.art/docs/server/tls/certificates/
- https://stalw.art/docs/ref/object/certificate/
- https://github.com/stalwartlabs/stalwart/blob/v0.16.26/crates/common/src/config/server/tls.rs
