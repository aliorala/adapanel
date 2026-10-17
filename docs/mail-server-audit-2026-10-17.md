# Posta sunucusu toplu denetimi — 17 Ekim 2026

Kapsam: AdaPanel posta API'si, Stalwart kurulumu/görevi/dinleyicileri, sertifika yaşam döngüsü, hesap/kota/alias/liste, DNS, kuyruk/röle, webmail, otomatik istemci ayarları ve posta yedeği. Kaynak incelemesi ve otomatik testler, canlı ölçümlerden ayrı değerlendirilmiştir. Bu rapor sınırsız bir güvenlik garantisi değildir.

## Canlı ölçüm

Sunucuya mevcut Chrome Uzak Masaüstü oturumundan erişildi. İşlemler salt okunurdu; servis yeniden başlatılmadı, kuyruk silinmedi, DNS/sertifika/posta ayarları değiştirilmedi. SMTP sınamasında DATA komutu veya ileti gönderimi yapılmadı.

| Alan | Sonuç | Kanıt / sınır |
|---|---|---|
| SMTP 587 STARTTLS ve 465 TLS | Sağlıklı | Gerçek bağlantılarda Python varsayılan güven deposu ile zincir, ad ve süre doğrulandı. TLS 1.3, Let's Encrypt YR1, `mail.videotools.com.tr`, bitiş 14 Ocak 2027 08:52:32 UTC. Doğrulama kapatılmadı. |
| IMAP 993 TLS | Sağlıklı | Aynı sertifika ve kontrollerle gerçek dış bağlantı başarılı. |
| IMAP 143 ve ManageSieve 4190 yerel TLS | Sağlıklı | Sunucuda loopback TCP + STARTTLS + varsayılan doğrulamalı SslStream ile `AuthenticateAsClient(mail.videotools.com.tr)` başarılı. |
| Dış 143 ve 995 | Erişilemiyor | Bu bilgisayardan genel posta adına bağlantı reddedildi; sunucuda iki port da dinliyor. NAT/güvenlik duvarı yolunun hangi noktasında engellendiği belirlenmedi. IMAPS 993 çalışıyor. |
| Yönetim ve filtre servisi | Yerel erişim | 8080 ve 4190 yalnız 127.0.0.1 / ::1 üzerinde; posta portları 25,143,465,587,993,995 IPv4/IPv6 genel adreslerde dinliyor. |
| Süreç/görev | Çalışıyor | Tek Stalwart PID 7780, başlangıç 15 Ekim 00:58:27. `AdaStalwart` Running; MultipleInstances=IgnoreNew, StartWhenAvailable=True, RestartCount=3, RestartInterval=PT1M. Çökme veya Windows yeniden açılışı canlıda tetiklenmedi. |
| Açık röle | Bu senaryoda kapalı | 25 üzerinde TLS sonrası kimliksiz harici RCPT: 550 `Relay not allowed`. 587 kimliksiz MAIL FROM: 503. Ön-TLS AUTH listesi yalnız XOAUTH2/OAUTHBEARER; PLAIN/LOGIN ilan edilmedi. Tüm oturum/politika birleşimleri sınanmış sayılmaz. |
| Giden posta | Sorun var | Panelde 10 bekleyen ileti. Açılan iletide 8 deneme, TemporaryFailure/connectionError, Hotmail MX hedefinde bağlantı zaman aşımı (10060). Sunucudan Hotmail ve Gmail MX hedeflerine TCP 25 bağlantıları 6 saniyede tamamlanmadı. Ağ/sağlayıcı filtresi olası; engelin kesin sahibi ölçülmedi. |
| MX/A/SPF/DMARC | Kayıtlar mevcut | MX mail.videotools.com.tr / öncelik 10; A 84.51.22.239; SPF `v=spf1 mx a ip4:84.51.22.239 ~all`; DMARC quarantine ve rapor adresi mevcut. Bu kayıtların varlığı gerçek bir iletinin SPF/DKIM/DMARC sonucunu kanıtlamaz. |
| PTR | Posta adıyla uyuşmuyor | 84.51.22.239 ters kaydı `host-84-51-22-239.teletektelekom.com`. Sağlayıcının PTR kaydını mail.videotools.com.tr yapması ve ileri A ile eşleşmesi gerekir. |
| Otomatik istemci yapılandırması | Yayında değil | Canlı kartta panelin otomatik yapılandırma yönlendirme kuralı olmadığı gösteriliyor. Manuel IMAP/SMTP ayarları mevcut. |
| DKIM seçicileri | İkisi de DNS'te mevcut | Canlı panelin gösterdiği `v1-rsa-20261014` ve `v1-ed25519-20261014` seçicileri ayrı DNS sorgularıyla bulundu; RSA/Ed25519 türleri ve dolu p değerleri mevcut. Tam anahtarın makine ile karşılaştırılması ve gerçek ileti imzası sınaması bu ölçüme dahil değil. |

Yerel kanıt dosyaları (yayımlanmaz): `.tmp/mail-live-audit-117.json`, `.tmp/mail-policy-audit-117.json`. Görev, kuyruk, dış TCP ve yerel TLS sonuçları uzak masaüstündeki terminal/panelden okundu. Canlı DKIM özel anahtarları ve müşteri parolaları okunmadı; herhangi bir sır rapora alınmadı.

## Kod incelemesi ve toplu düzeltme

| Bileşen / kod yolu | Bulgu ve işlem |
|---|---|
| `MailService.GetWorkspaceAsync` | Domain sorgusunun bozuk JSON'u başarılı boş listeye dönüşüyordu. Artık ölçülebilir envanter zorunlu. Hesap kimliği/ayrıntısı/adresi okunamazsa AccountsMeasured=false; eksik sayım müşteri veya domain kotasını atlatamaz. |
| `MailService.GetQueueAsync` | Bozuk/kısmi yanıt boş başarılı kuyruk sayılmıyor. Tarihi/boyutu dolu özette alıcılar yoksa da ayrıntı çekiliyor; canlı panelde görülen boş alıcı sütununun kod nedeni giderildi. |
| `MailService.DeleteDomainAsync` | Hesap silme/başka hesaptaki alias güncellemesi başarısızken domain silmeye devam ediliyordu. Artık ilk başarısız işlemde durur; okunamayan üye ayrıntısı da durdurur. Önceki başarılı alt işlemler işlemsel olarak geri alınmaz; kısmi tamamlanma mümkündür ve başarı dönülmez. |
| `MailController.RemoveDnsRecordsAsync`, `MailDnsPublication.MatchesRecord` | Önceki karşılaştırma bütün aynı host/tip MX ve A kayıtlarını seçebiliyordu. Hedef IP/MX adı ve MX önceliği de eşleşmeli; farklı yedek MX/A ve TXT içerikleri korunur. Birebir aynı kaydın tarihsel sahibini saptayan ayrı bir sahiplik günlüğü yoktur. |
| `MailDiagnosticsRunner` | Sadece `v=DKIM1` metni başarı sayılıyordu. Artık seçici başına beklenen p/k karşılaştırılır, boş/değişmiş/yinelenen anahtar reddedilir, bütün dönen etkin seçiciler kontrol edilir. SPF/DMARC beklenen yapılandırmayla karşılaştırılır ve çoğul kayıtlar başarı sayılmaz. Özel ve farklı fakat geçerli politika da yapılandırma farkı olarak fail gösterebilir; bu bir tam SPF yorumlayıcısı veya ileti imzası doğrulayıcısı değildir. İptal korunur. |
| `WebmailService`, yeni `WebmailTlsConfiguration` | Yönetilen Roundcube dosyası IMAP/SMTP TLS doğrulamasını kapatıyordu. Yeni dosyalarda ve sonraki Webmail aç eyleminde yönetilen mevcut dosyada verify_peer/verify_peer_name=true, allow_self_signed=false, peer_name=posta sertifikası adı, cafile=Windows LocalMachine Root deposundan yalnız genel kökler. Loopback bağlantısı korunur. ManageSieve STARTTLS açılır. Migrasyondan önce gerçek yerel IMAP/SMTP TLS kontrolü yapılır; hata varsa başarı verilmez. Hızlı yoldaki göç des_key ve diğer ayarları korur; işaretsiz özel dosya değişmez. |
| `MailService.CertificateIntegration`, `MailTlsProbe`, SSL yenileme | Certificate kaydı, zincir/anahtar, SNI, varsayılan sertifika koruması, yeniden yükleme ve gerçek 587/465 kontrolü mevcut. Bu tur çalışan sertifika kaydı yeniden yazılmadı. Web ve posta durumu ayrı kalır. Sertifika regresyonları toplu teste dahil. |
| `ComponentBootstrapper`, `StalwartRuntime` | Kurulum, yerel yönetim ucu, görev ve dinleyici yönetimi mevcut. Kurulum/güncelleme ve yeniden başlatma testleri dahil; canlı kurulum yeniden çalıştırılmadı. |
| `MailSenderPolicy`, listeler, spam ve röle | Gönderen politikası, tenant kapsam denetimleri, liste/alias, SMTP röle şifre koruması ve ayar yeniden yükleme testleri dahil. Mevcut diğer domainler ve özel röle kuralları değiştirilmedi. |
| `MailAutodiscoverPublisher`, `MailJmapProxy*`, `StalwartAccountArchiver`, `BackupService.MailLifecycle` | Otomatik istemci ayarları, IIS/JMAP kuralları, hesap başına arşiv ve çevrimdışı yedekleme/restart yolları incelendi; ilgili testler çalıştı. Canlı geri yükleme, gerçek müşteri hesabıyla oturum açma veya üretimde veri geri yükleme yapılmadı. |

## Test ve kapsam sınırları

- Önceki kapsam: 1.441 ilgili altyapı testi başarılı.
- Genişletilmiş kapsam: 1.543 test başarılı (Mail/Stalwart/Webmail/SSL/Posta/AuditCoverage/MessageKeyCoverage); günlük `.tmp/mail-comprehensive-final-117.log`.
- Son eklenen gerçek IMAP/SMTP STARTTLS red testleri dahil webmail grubu: 9/9 başarılı; `.tmp/webmail-tls-117.log`. Bu grubun 7 testi geniş kapsamla örtüşür.
- Toplam 49 yeni davranış testi: hesap/kuyruk/domain akışları, DNS kaydı koruma/politika/anahtar eşleşmesi, webmail göçü ve gerçek TLS üzerinden kendinden imzalı sertifika reddi.
- Posta/SSL arayüzü: 6 dosya, 155 test başarılı; `.tmp/mail-ui-117.log`.
- Güncelleme sunucuda henüz kurulmadı. Üretim Roundcube/PHP üzerinde uçtan uca kullanıcı oturumu testi yapılmadı; .NET ön kontrolü ve yapılandırma testleri PHP oturum açma testinin yerine geçmez.
- Webmail'in tarayıcı HTTPS yayını mevcut SecureWebmailForDomainAsync yoluna bağlıdır; HTTPS sağlanmamış kurulumlarda HTTP URL dönüşü halen ayrı bir açık iştir. Backend TLS düzeltmesi tarayıcı HTTPS kurulumunu tamamlanmış saydırmaz.
- DKIM canlı seçici/anahtar eşleşmesi ve gerçek gönderi teslimi henüz tamamlanmadı. Kuyruktaki iletiler yeniden gönderilmedi veya silinmedi.
- Canlı genel domain tablosundaki kutu sayısı 0 iken alan adı ayrıntısında bir hesap görüldü. Genel listede sayım yapılmayan durumun 0 gösterilmesi ayrıca ele alınmalı; bu paketteki kota düzeltmesi ayrıntı okuma başarısızlığını güvenli biçimde bildirir, genel tablo sayımını eklemez.

## Sunucuda kalan işler

1. Dış TCP 25 bağlantısının ağ/sağlayıcı tarafında neden tamamlanmadığını çözmek. Alternatif, işletmecinin sağlayacağı mevcut SMTP rölesini panelin röle ekranından yapılandırmak; bu incelemede röle kimliği/şifresi yoktu ve tahmin edilmedi.
2. Sağlayıcıda PTR'yi posta adına çevirmek. Panelin normal ileri DNS kaydı PTR sahibi değildir.
3. Güncellemeyi kurduktan sonra yönetilen Webmail aç eylemiyle göçü çalıştırmak ve PHP üzerinden gerçek IMAP/SMTP/filtre oturumunu doğrulamak. Özel dosyalar operatör kontrolünde ayrıca ele alınmalı; tarayıcı HTTPS ve yönlendirmesi doğrulanmalı.
4. İhtiyaç varsa dış 143/995 yolunu ağ cihazında incelemek; çalışan 993/587/465 için port değişikliği yapılmamalı.
5. Çıkış yolu düzeldikten sonra kontrollü bir hesapla gönder/al, teslim başlıklarında DKIM/SPF/DMARC ve geri yükleme provası yapmak. Bu adımlar tamamlanmadan tüm posta hizmeti sorunsuz ilan edilmemeli.

Kaynaklar: [Stalwart sertifika yönetimi](https://stalw.art/docs/server/tls/certificates/), [PHP SSL context seçenekleri](https://www.php.net/manual/en/context.ssl.php), [DKIM RFC 6376](https://www.rfc-editor.org/rfc/rfc6376). Sürüm bağımlı Stalwart davranışı için önceki 0.16.26 kaynak doğrulamaları `mail-tls-2026-10-17.md` içinde.

## Yayın kanıtı

- Tek toplu yayın: [0.8.117](https://github.com/aliorala/adapanel/releases/tag/0.8.117).
- Kurulum paketi: 91.215.362 bayt; SHA-256 `BD76117A208BB71A1251ADFF156ABBFB4533B124A777DC5DA8D8DA2E76F1C643`.
- Yükleme sonrası GitHub varlığı yeniden indirilerek yerel paketle hash eşitliği ve yayın gövdesinin kaynakla eşitliği doğrulandı; `.tmp/publish-117.log`.
- TypeScript kontrolü ve Vite derlemesi başarılı. Dist / API wwwroot / publish index hash'leri aynı: `80BA145E63061E5D462AA2D5EBF2DF25E98A7CFF3D16BF3838D2012D788D7F69`.
- Bileşen paket denetimi yeniden çalıştırıldı: 22 başarılı, 3 büyük paket bilinçli atlandı; indirme önbelleği kullanıldı. Kurulum içeriği ve sürüm denetimleri geçti. Yayın notu mahremiyet testleri 12/12.
- Core projesinde Mail ad filtresiyle test bulunmadı; bu çalıştırma ilave başarılı test sayısına eklenmedi.
- Yalnız ikili kurulum varlığı yayımlandı; kaynak kod push edilmedi. Canlı sunucuya bu sürüm kurulmadı.
