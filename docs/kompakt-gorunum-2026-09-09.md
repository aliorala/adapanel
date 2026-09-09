# 0.8.30 — Kompakt görünüm

Kullanıcının ana sayfadaki halka yazılarının taşması ve genel görünümün büyük kalması geri bildirimi üzerine düzeltildi.

- Kompakt masaüstü tabanı 14 px → 13 px; kart dolgusu 16 px → 12 px, panel aralığı 12 px → 10 px. Menü satırı dolgusu ve ikonları küçültüldü. Rahat görünüm ve 700 px altındaki mobil yazı tabanı 14 px olarak korundu.
- Halkalar 160 px kaldı; iç metinler tüm yüksekliğe dağıtılmak yerine ortada gruplandı. Başlıklarda büyük harf zorlaması kaldırıldı; uzun açıklamalar dar bir alanda satır kırar. Özet kartları da sıkılaştırıldı.
- Mobil başlıklar halkanın üstünde, yüzde dairenin içinde. Dar halkalarda çizgi inceltilerek %100 için yeterli alan bırakıldı.

Doğrulama: Gerçek MainLayout ve DashboardOverviewView bileşenleri örnek verilerle çalıştırıldı. 320, 390, 1024 ve 1486 px genişlikler; Türkçe/koyu/kompakt ve İngilizce/açık/rahat görünümler kontrol edildi. %100, 128 mantıksal çekirdek ve TiB ölçeğindeki değerler kullanıldı. Metin satırlarının köşeleri dairenin iç yarıçapına göre ölçüldü; son durumda halka içinde taşma veya yatay sayfa taşması yok. Geçici önizleme dosyaları kaldırıldı.

`npm run typecheck`, `npm run build` ve mevcut 13 CSS kontrolü geçti. Bu değişiklik CSS ve sürüm bilgileriyle sınırlı; önceki sürümün tüm .NET işlev testleri yeniden çalıştırılmış gibi sunulmadı.

## Yayın ve canlı sonuç

0.8.30, mevcut canlı yayın izni kapsamında yayımlandı ve canlı panelin güncelleme ekranından kuruldu. Kaynak commit'i `03cbea2`. Kurulum dosyası 90.746.342 bayt, SHA256 `10C78CA89291F580C24CEA891E0425BC5B60F48E83F603DA8EC123636190B0AA`. GitHub'dan yeniden indirilen paket aynı özetle doğrulandı; canlı sunucu da aynı paketi doğruladı.

Son paketlemede 149 ön yüz dosyası publish çıktısıyla SHA256 üzerinden eşleştirildi. 22 bileşen denetimi geçti; betiğin büyük paket istisnasındaki 3 bileşen atlandı. İlk paketleme denemesinde ön yüz eşitlemesi sırasında eski statik dosya listesi oluştu; paket yayımlanmadı, tamamlanmış ön yüz çıktısından yeniden paketlendi. Bir sonraki bileşen denetimi geçici bağlantı hatasıyla durdu; tekrarında bütün zorunlu kontroller geçti. Bu başarısız denemeler başarılı paket sayılmadı.

Canlı sayfa yeniden yüklendikten sonra arayüz ve sunucu bilgisi 0.8.30 gösterdi. Hesaplanan kök yazı boyu 13 px; gerçek CPU/bellek/disk metinlerinde daire sınırının dışına çıkan satır bulunmadı. 1486 px genişlikte yatay sayfa taşması yok. IIS, WAS ve FTP servisleri sağlık ekranında çalışıyor olarak görüldü. Ana sayfa kullanıcı için açık bırakıldı.

Kanıtlar: `.tmp/audit-fixes/compact-css-tests.log`, `compact-component-audit.log`, `compact-finalize.log`, `compact-publish.log` ve `artifacts/installer-manifest.json`.
