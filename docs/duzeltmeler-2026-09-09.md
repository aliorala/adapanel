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
