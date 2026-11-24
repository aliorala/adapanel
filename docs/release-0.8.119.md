# AdaPanel 0.8.119 yayın kaydı

24 Ekim 2026 tarihinde Windows x64 kurulum paketi yayımlandı:
https://github.com/aliorala/adapanel/releases/tag/0.8.119

## Kapsam

- IIS sertifika farklılığı uyarısından inceleme ve kontrollü onarım; gerçek yerel TLS doğrulaması ve başarısız sonucun görünür tutulması.
- SSL onarım denetim kaydında yönetici aktör ile gerçek site sahibi ayrı tutulur.
- MailEnable sertifika, relay, dağıtım listesi ve mevcut bağımsız webmail uygulaması entegrasyonları; işlem/kurtarma kontrolleri.
- Zamanlanmış işler, yedekleme, günlük toplama, terminal süreçleri ve güncelleme başlatıcısı düzeltmeleri; isteğe bağlı kurulum bildirimi.

Kaynak hazırlık commit'i: `18ffb77`. Mevcut yayın yöntemiyle kurulum dosyası yayımlandı; kaynak geçmişi uzak depoya gönderilmedi. Ayrı web sitesi ve özel yönetim uygulaması bu paketleme işleminde yayımlanmadı.

## Doğrulama

- Önceki aynı özellik kaynaklarında frontend: 2.706/2.706 test, tür kontrolü ve üretim derlemesi başarılı. Son CSS belirteci düzeltmesinden sonra üretim derlemesi ve CSS belirteç testleri tekrar geçti.
- Tam backend taraması: Core 2/2; Infrastructure 10.241 test, ilk turda 10.235 başarılı, 6 başarısız, 0 atlanan. Bu tur tamamen yeşil olarak raporlanmamalıdır.
- Üç denetim kapsamı hatası düzeltildi: kayıt konusu, bağımlılık adı ve salt okunur plan sınıflandırması. Gerçek controller/servis yolundan dört yeni aktör/konu senaryosu eklendi. Son hedefli AuditCoverage + SslBindingRepair turu 36/36 geçti (`release-0.8.119-audit-repair.trx`).
- CSS testi tanımsız `--border-color` kullanımını yakaladı; mevcut `--panel-border` kullanıldı. İki diğer ilk-tur hata, test ömrü iptalinden kaynaklanan OperationCanceledException idi. İlgili dosya erişimi, DNS ve CSS tekrarında 13/13 geçti; süre sınırları veya üretim davranışı gevşetilmedi (`release-0.8.119-recheck.trx`).
- Bileşen denetimi: 22 Passed, mevcut büyük paket kapsamı nedeniyle 3 Skipped. Ara paketleme tekrarında dış indirme TLS hatası oldu; son normal paketleme turunda kontrol atlanmadan tamamlandı.
- Son publish envanteri: 172 dosya, 0 bulgu. Installer sürüm/içerik kontrolü: 0 bulgu.
- GitHub releases/latest 0.8.119; uploaded EXE digest'i yerel SHA256 ile aynı. Yayımlanan EXE yeniden indirilip SHA256 karşılaştırıldı.

Paket boyutu: 91.126.486 bayt.

SHA256: `0A09D15D6539FD322909361F8C1688D4ED4CAF534A82EF2A3D333568960D1BA9`

Gerçek çalışan ajanlar: `rues_gelistirici` (dar audit düzeltmesi ve 36 test), `rues_denetci` (bağımsız yayın ve audit incelemesi, ONAY). Paketleme, tam test taraması, CSS düzeltmesi ve yayın ana oturumda yapıldı.

## Canlı sunucuda kalan doğrulama

Bu işlem sunucuya kurulum veya canlı sertifika değişikliği yapmadı. Güncelleme sunucuya kurulduktan sonra kullanımda olan sitelerin SSL uyarıları yeni incele/düzelt akışıyla değerlendirilmeli ve uygulama sonucu doğrulanmalıdır. Kullanıcının süresi bittiğini belirttiği alan adı müdahale kapsamından çıkarıldı.

MailEnable için ayrı gerçek kurulum/yenileme doğrulaması halen gereklidir; mevcut Stalwart sağlayıcısı değiştirilmedi. Otomatik testler gerçek Windows IIS/MailEnable dağıtımının tamamlandığı anlamına gelmez.
