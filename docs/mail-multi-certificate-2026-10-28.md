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

Dağıtım ve son canlı doğrulama henüz tamamlanmadı; başlangıç ölçümü başarı
kanıtı olarak sunulmaz. Sonuçlar doğrulamadan sonra bu rapora eklenecek.

## Kaynaklar

- https://stalw.art/docs/server/tls/certificates/
- https://stalw.art/docs/ref/object/certificate/
- https://github.com/stalwartlabs/stalwart/blob/v0.16.26/crates/common/src/config/server/tls.rs
