# Mail/FTP yerel inceleme ve dış teslimat testi — 29 Ekim 2026

## Yerel kod düzeltmesi

İçe aktarılan bir IIS sitesinde teknik site adı ile gerçek alan adı farklı
olabilir. MailService otomatik MX/DKIM yayını ve MailController DNS uygulama /
kayıt temizleme yolları alan adını IDnsService'e teknik site adı diye geçiriyordu.
IDnsService bu argümanı IIS site adı olarak kullanır. Ortak doğrulanmış eşleme
artık teknik siteyi bulur; Stalwart'ta kullanılan gerçek posta alan adı değişmez.

GetZoneAsync sonucunun ZoneName değeri istenen posta alan adıyla eşleşmezse
başka bölgeye MX/DKIM yazılmaz veya oradan kayıt silinmez. Belirsiz/sahipsiz IIS
eşlemesi de durur. Alias alanları için yeni bir DNS bölge modeli eklenmedi;
böyle bir uyuşmazlık mevcut hata yolundan bildirilir. Kaydedilmiş MX değerleri,
sertifika/SNI seçimi, posta sunucusu adı ve kullanıcı sırları değiştirilmedi.

Değişen üretim kaynakları: MailService.cs, MailService.ServerIdentity.cs
(resolver erişimi), MailDnsPublication.cs koruması ve MailController.cs.
Testler: MailDomainDnsProvisioningTests.cs ve PostaDnsYayinDurustlugTests.cs.
Geliştirici fake/memory grubu 34/34 geçti; bağımsız denetçi ilgili 18/18'i
ayrıca çalıştırdı ve ONAY verdi. Teknik site argümanı ve zone korumasına yönelik
iki mutasyon ayrı ayrı 2 testi kırdı; kaynaklar geri yüklendi, final test geçti.
API Release build: 0 uyarı, 0 hata. Tam Windows kullanıcı oluşturan suite
çalıştırılmadı. FTP üretim kodunda yeni doğrulanmış hata bulunmadı.

## Canlı test (mevcut kurulu 0.8.136)

Kullanıcının açık Webmail hesabından kendisine ait açık Outlook hesabına yalnız
zararsız bir test mesajı gönderildi. Gönderici info@adapanel.host, test konusu
ADAPANEL-DELIVERY-20261029-A1. Alıcı adresi ve sunucunun ev IP'si bu raporda
kopyalanmadı. Gönderilmiş klasöründe aynı kod ve 01:48 kayıt zamanı görüldü;
aynı anda ilgili teslimat başarısızlığı bildirimi geldi.

Sonuç: TESLİM EDİLMEDİ. Uzak MX hotmail-com.olc.protection.outlook.com bağlantıyı
kabul edip MAIL FROM aşamasında 550 5.7.1 / Spamhaus / AS3130 yanıtıyla reddetti.
Reporting-MTA mail.adapanel.host. Bu deneme sunucu ortamından TCP 25 ile uzak
MX'e ulaşıldığını kanıtlar; SMTP kabulü veya gönderilmiş kaydı dış teslimat
başarısı diye raporlanmadı. Buradaki engel SSL/SMTP kimlik doğrulaması değil,
alıcının IP itibar politikasıdır. İkinci sağlayıcıya ait kullanıcı test adresi
henüz verilmedi. Teslimat engelini kaldıran bir canlı değişiklik yapılmadı.

Ayrıca üç posta adı (mail.adapanel.host, mail.travianpanel.com,
mail.videotools.com.tr) için 587 STARTTLS ve 465 TLS üzerinde toplam 6/6 gerçek
SAN, süre, güven zinciri ve SMTP karşılaması kontrolü geçti; karşılamadaki
kalıcı ad mail.adapanel.host olarak kaldı. TLS doğrulaması kapatılmadı.

İnternet ve iç ağ FTPS yollarında TLS doğrulaması, LIST, yalnız UUID test
bileti yükleme/indirme SHA-256 karşılaştırması ve bu test dosyasını temizleme
başarılı. Mevcut site dosyaları değiştirilmedi. Kanıtlar .tmp/ftps-probe içindeki
oct29 sonuçları ve .tmp/mail-sni-probe/oct29-check.json dosyasında (web dışı).

## Kapsam ve kalan

Kod/SEO entegrasyonu ve derlemeler yereldir; yeni sürüm yayımlanmadı ve ev
sunucusuna bu turun DNS düzeltmeleri kurulmadı. Canlı test yalnız mevcut
kurulumun durumunu ölçer. Hotmail'in Spamhaus engeli devam ediyor; gerçek
harici alıcı teslimatı başarılı olarak doğrulanmış değildir. Mevcut TLS,
parolalar, MX/DKIM ve genel posta ayarları test için zayıflatılmadı.

## Sonraki yayın adımı

Kullanıcının yayın talebiyle DNS kod düzeltmesi 0.8.137 olarak yayımlandı.
Paketin GitHub'dan indirilen kopyası SHA-256 ile doğrulandı; kullanıcı ev sunucusunda
0.8.137 kurulumunu tamamladığını bildirdi. Sonrasında altı posta TLS kontrolü tekrar
geçti. Kurulu ikili sürümü bu oturumda bağımsız okunmadı. Önceki "yayımlanmadı" açıklaması yerel inceleme
aşamasına aittir. Güncel durum `release-0.8.137-2026-10-29.md` belgesindedir.
