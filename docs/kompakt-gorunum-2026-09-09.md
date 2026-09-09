# 0.8.30 — Kompakt görünüm

Kullanıcının ana sayfadaki halka yazılarının taşması ve genel görünümün büyük kalması geri bildirimi üzerine düzeltildi.

- Kompakt masaüstü tabanı 14 px → 13 px; kart dolgusu 16 px → 12 px, panel aralığı 12 px → 10 px. Menü satırı dolgusu ve ikonları küçültüldü. Rahat görünüm ve 700 px altındaki mobil yazı tabanı 14 px olarak korundu.
- Halkalar 160 px kaldı; iç metinler tüm yüksekliğe dağıtılmak yerine ortada gruplandı. Başlıklarda büyük harf zorlaması kaldırıldı; uzun açıklamalar dar bir alanda satır kırar. Özet kartları da sıkılaştırıldı.
- Mobil başlıklar halkanın üstünde, yüzde dairenin içinde. Dar halkalarda çizgi inceltilerek %100 için yeterli alan bırakıldı.

Doğrulama: Gerçek MainLayout ve DashboardOverviewView bileşenleri örnek verilerle çalıştırıldı. 320, 390, 1024 ve 1486 px genişlikler; Türkçe/koyu/kompakt ve İngilizce/açık/rahat görünümler kontrol edildi. %100, 128 mantıksal çekirdek ve TiB ölçeğindeki değerler kullanıldı. Metin satırlarının köşeleri dairenin iç yarıçapına göre ölçüldü; son durumda halka içinde taşma veya yatay sayfa taşması yok. Geçici önizleme dosyaları kaldırıldı.

`npm run typecheck`, `npm run build` ve mevcut 13 CSS kontrolü geçti. Bu değişiklik CSS ve sürüm bilgileriyle sınırlı; önceki sürümün tüm .NET işlev testleri yeniden çalıştırılmış gibi sunulmadı.
