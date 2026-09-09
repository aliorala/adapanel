# Denetim sonrası düzeltmeler — 9 Eylül 2026

Yedekleme modülü korunmuştur. Yedek oluşturma, listeleme, şifreleme, zincir ve geri yükleme işlevleri kaldırılmamıştır. Sunucuyla eşitleme adına bir modül değişimi yapılmamıştır.

## Uygulanan değişiklikler

| Bulgu | Düzeltme |
|---|---|
| F1 — Geri yükleme | Dosyalar, site yedekleri ve genel yedek ekranı ortak tam geri yükleme akışını kullanır. Yönetici yetkisi, zincir sağlamlığı, şifreli yedek parolası, hedef adı yazılarak onay ve işlem kilidi uygulanır. Kısmi hata durumu, uyarı ayrıntıları ve korunan dosyaların yolu gösterilir. Liste yenileme hatası geri yükleme sonucunu gizlemez. |
| F2 — Oturum kalıntıları | Oturum bitişi, çıkış ve hesap değişiminde tüm Pinia depoları temizlenir. Önceki oturuma ait geç gelen yanıtlar reddedilir; eski bir 401 yeni oturumu kapatamaz. Marka ve yerel hesap bağlamı temizlenir, açık onay/parola pencereleri kapatılır. Girişte tam oturum/yetki bilgisi gelmeden panel açılmaz. |
| F3 — Kaybolan dosya değişiklikleri | Son kaydedilen içerik izlenir. Yenileme, klasör/site/bölüm değişimi, çıkış ve geçmişte gezinme için Kaydet / Değişiklikleri bırak / Sayfada kal seçenekleri vardır. Kaydetme başarısız olursa içerik ve mevcut sayfa korunur. Sekmeden ayrılmada tarayıcı uyarısı kullanılır. Eski dosya okuma yanıtı yeni siteyi ezemez. |
| F4 — Kaybolan site seçici | Genel Dosyalar ekranında site seçici daima görünür ve etiketlidir. Site araçlarının içindeki görünüm açık bir `embedded` parametresi kullanır; mevcut “Web Siteleri” geri yolu korunur. |
| F5 — Yetkisiz düğmeler | ACL, sunucu veritabanı yedeği ve tam geri yükleme yöneticiye özel gösterilir. Diğer rollerin dosya seçerek geri yükleyebilecekleri Yedekleme ekranına geçişi bulunur. Site listesindeki araç kısayolları ve URL ile açılan araçlar da rol kapsamına göre süzülür. Sunucu yetki kontrolleri korunur. |
| F6 — Gezinme bağlamı | Vue Router hash geçmişi etkinleştirildi. Bölüm, seçili site, site aracı ve ayar sekmesi adresle tutulur; yenileme ve geri/ileri desteklenir. Sunucunun güvenli giriş yolu değişmez. Yetkisiz/bilinmeyen bölüm veya araç açılmaz. Parolalar URL'ye yazılmaz. |
| F7 — Kurulumda sonsuz bekleme | İlerleme isteği 10 saniyede zaman aşımına uğrar. Üç ardışık bağlantı hatasında otomatik yoklama durur; son bilinen sunucu durumu, son başarılı kontrol ve “Yeniden bağlan” düğmesi gösterilir. Bağlantı hatası kurulumu başarısız ilan etmez. Görünüm kapandığında istek ve bekleme iptal edilir. |
| F8 — Yükleme kontrastı | Yükleme düğmesi ortak birincil düğme renklerine ve odak halkasına bağlandı. Dosya düzenlerken oluşturma/yükleme araçları gizlenir. |
| F9 — Başlangıç paketi | Türkçe ve İngilizce ayrı dinamik parçalara taşındı; etkin dil yüklenince uygulama açılır. Güvenli mesaj derleyicisi korunur. Dil yüklenemezse yeniden deneme gösterilir. |

Menü satırlarının gereksiz gölge/hareketi kaldırıldı. Temel yazı boyutu 14 px, en küçük ortak metin boyutu yaklaşık 12 px oldu. Ana sayfa kaynak halkaları küçültüldü. Genel Dosyalar ekranındaki yinelenen başlık kaldırıldı; düzenleyiciye sabit kaydetme çubuğu ve değişiklik göstergesi eklendi. Onay penceresindeki genel etki başlığı “Etkilenecek öğeler” olarak düzeltildi.

PowerShell test düzeneği iki çıktı kanalını eşzamanlı okur ve süreç/çıktı beklemesinin tamamına zaman sınırı uygular. Sonsuz betik, dolu stdout/stderr ve çağıranın eşzamanlama bağlamına dönmeden tamamlanma senaryoları test edildi. Senkron testlerden çağrılan asenkron yardımcı `ConfigureAwait(false)` kullanır. Süre ölçen süreç testleri, binlerce testle süreç başlatma yarışı yaşamamaları için paralellik dışındaki bir koleksiyonda çalışır; üç kontrol ayrı koşuda geçti. SSL yenileme yanıtındaki olası null çıktı da boş metne dönüştürüldü.

## Tarayıcı doğrulaması ve sınırlar

Yerel Vite ortamında gerçek Vue bileşenleri ve örnek API yanıtları kullanıldı. Kaydetmeden yenileme, bölüm ve site aracından çıkış, başarılı/başarısız kaydetme, site değişiminden vazgeçme, tarayıcı geri/ileri ve site aracı adresini yenileme denendi. Şifreli yedekte parola ve hedef adı onayı gözlendi; son geri yükleme işlemi iptal edildi. Açık/koyu tema ve 390 × 844 mobil görünüm incelendi; mobil düzenleyicide yatay taşma görülmedi.

Örnek düzeneğin eksik sistem/katalog yanıtlarının ürettiği ilk önizleme hataları canlı sunucu bulgusu olarak sayılmadı; gerekli örnek alanlar düzeltildi. Gerçek IIS, posta, DNS veya yedek arşivine yazılmadı. Önizleme dosyaları ve geçici geliştirme sunucusu kaldırıldı. Canlıya dağıtım yapılmadı.

Arka uç testlerinde Windows PowerShell modül yolu yalnız test süreci için açıkça verildi: `C:\Windows\system32\WindowsPowerShell\v1.0\Modules;C:\Program Files\WindowsPowerShell\Modules`. Makinenin kalıcı ortam ayarları değiştirilmedi.


## Ön yüz test ve derleme sonucu

- `npm run typecheck`: başarılı.
- `npm test`: 8 dosyada 58 test başarılı. Yeni testler eski oturum yanıtlarını, geri yükleme onay/parola/rol/zincir kontrollerini, kısmi hata raporunu, bağlantı kesintisini ve eşzamanlı gezinme kararlarını kapsar.
- `npm run build`: başarılı. Ana JavaScript parçası yaklaşık 126 KB (önce 915 KB); Türkçe yaklaşık 404 KB, İngilizce yaklaşık 391 KB ayrı yüklenir. Bunlar parça boyutlarıdır; toplam ilk yükleme süresinde %86 iyileşme iddiası değildir. 500 KB parça uyarısı kalktı.
- `git diff --check`: boşluk/patch hatası bulunmadı.


## Son .NET doğrulaması

Son tam altyapı koşusunda **5.220 / 5.220 test geçti**, başarısız veya atlanan test yok; süre 7,15 dakika. Çekirdek projesinin **2 / 2 testi** de geçti. Böylece 5.222 .NET testi ve 58 ön yüz testi doğrulandı.

Kanıtlar: `.tmp/audit-fixes/final/infrastructure-final.trx`, `.tmp/audit-fixes/infrastructure-final.log`, `.tmp/audit-fixes/final/final.trx` (çekirdek) ve `.tmp/audit-fixes/frontend-build.log`. Önceki kesilen denemeler başarılı tam koşu olarak sayılmadı. Test projelerindeki mevcut analiz uyarıları ayrı teknik borç olarak kalır; son koşuda test hatası yoktur.

## 0.8.29 yayını ve canlı doğrulama

Kullanıcının canlıya yayınlama izniyle 9 Eylül 2026 tarihinde 0.8.29 yayımlandı: https://github.com/aliorala/adapanel/releases/tag/0.8.29 . Yerel kaynak commit'i `68180e6`; uzaktaki depo mevcut yönteme uygun olarak yalnız kurulum paketi ve sürüm notlarını taşır, kaynak geçmişi gönderilmedi.

- Kurulum dosyası: 90.744.360 bayt. SHA256: `F0AB907400FA6F2B01040CF96DF9C2813EF3B94CB333CD85AF22F518EF36CA54`.
- Sürüm kapısı, yayın notu mahremiyet denetimi, publish ve installer içerik denetimleri geçti. Ön yüzün 149 dosyası publish çıktısıyla SHA256 üzerinden eşleştirildi. Bileşen denetiminde 22 paket geçti; betiğin büyük paket istisnasındaki 3 paket atlandı.
- Yayındaki paket yeniden indirildi ve yerel dosyayla SHA256 eşleşmesi doğrulandı. GitHub `latest` sürümü 0.8.29 olarak doğrulandı.
- Canlı panelde paket indirildi, aynı SHA256 ile hazırlandı ve kurulum onaylandı. İlk deneme, servis süreç dosyasını yeniden kilitlediği için kod 5 ile geri alındı. Windows olayları zorla sonlanan hizmet için 5 saniyelik otomatik yeniden başlatma kaydını gösterdi. Bu turda servis kurtarma ayarı değiştirilmedi; yarışın kalıcı olarak giderildiği iddia edilmez.
- Sonraki kurulum denemesi sunucu kayıtlarında görüldü ve başarıyla sonuçlandı. Bu ikinci planlama denetim kaydında yerel sunucu oturumundan (`::1`) gelir; bu görevin tarayıcı eylemi olarak sayılmadı. `last-attempt.json` sonucu `succeeded`, `isFinished=true`, `isFailed=false`; çalışan panelin ve diskteki uygulamanın sürümü 0.8.29. Kurulum günlüğü 14:58:47'de kapandı ve Windows yeniden başlatması gerektirmedi.
- AdaPanel servisi `Running / Automatic`; kurulu .htaccess modülü 184.832 bayt. Yeni arayüzde `#/?section=sites` gezinmesi, üç sitenin `Çalışıyor` durumu ve yedek ekranındaki 60 kayıt doğrulandı. Gerçek yedek geri yükleme veya site verisi değiştirme işlemi yapılmadı.

Yayın öncesine ait ayrı işletim bulgusu: gece oluşturulan bir veritabanı yedeği yerelde mevcut, uzak FTP aktarımı `421 Service not available` nedeniyle başarısız. Bazı eski kayıtlar ayrıca yedek şifrelemesi yapılandırılmadığını bildiriyor. Bu kayıtlar yeni sürümün hatası olarak sınıflandırılmadı; uzak aktarım sorunu bu yayınla çözülmüş sayılmaz.

Yerel yayın kanıtları: `.tmp/audit-fixes/release-package.log`, `.tmp/audit-fixes/release-publish.log`, `artifacts/publish-manifest.json`, `artifacts/installer-manifest.json` ve `test-results/component-package-audit.json`.
