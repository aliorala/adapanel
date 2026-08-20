# Denetim düzeltmeleri — TAMAMLANDI

Kaynak: `docs/denetim-2026-08-19.md`. On ajana bölündü, **hepsi bitti ve birleşti**.

**Sonuç: 0.6.5 yayınlandı** — SHA256 `488E4F5888671430224F1ACE6D6E5AE5A7AAC98406BB213A6DFFF91EB33C8F0F`,
90.299.181 byte. İndirilen varlık yerel dosyayla birebir aynı, `releases/latest` = 0.6.5.

Testler: **2488 → 2810** (+322). typecheck + build temiz, i18n TR/EN 4378 parite tam.

## Kapatılan kritikler

| Bulgu | Ne yapıldı |
|---|---|
| K1 Stalwart parolası düz metin, kiracıya okunabilir | DPAPI ile korunuyor + dosya kilidi + `runtimes\stalwart` ağacı kapatıldı |
| K2 MariaDB veri dizini kiracıya okunabilir | Veri dizinleri kapatıldı; PHP/Node/Python dokunulmadı (IIS ve sınırlı kimlik onları çalıştırıyor) |
| K3 `certs\panel.password` okunabilir | `icacls /reset` kaldırıldı, doğru ACL kuruluyor |
| K4 Sertleştirme geniş ACE'leri mühürlüyor | `preserveInheritance: false`, DACL sıfırdan; servis hesabı tahmin edilmiyor, okunuyor |
| K5 Kurtarma yolu K1'i iptal ediyor | Yeniden sertleştirme `EnsureWritableAsync`'in kendi içine kondu — unutulamaz |
| K6 Bayi → Administrator parola değişimi | Kullanıcı adı artık sahiplik iddiası; koruma serviste, tüm çağıranlar kapsanıyor |
| K7 WordPress klonu alan adı ele geçirme | Klon, site oluşturmayla aynı iki denetimden geçiyor |
| K8 Arşiv içe aktarma siteyi boşaltıyor | Arşivin dosya içerdiği yıkıcı adımdan ÖNCE doğrulanıyor; `ContentSideline` geri sarıyor |
| K9 `IsSafeToDelete` sistem yollarını geçiriyor | Kara liste → KANIT: üç pozitif kanıttan biri şart |
| K10 Yedek dosya atlayıp "başarılı" diyor | `BackupSkipLog` + `created_with_errors`; atlamak artık derlenmiyor |
| K11 "Kurulum tamamlanamadı" | Kök neden kanıtlandı; hüküm sahipliği + ayrı çalıştırıcı günlüğü |
| K12 Üretilen installer taranmıyor | ISCC sonrası denetim iki paketleme betiğine de bağlandı |
| Y1–Y10 | DB sahipliği, bileşen kaydı, SSL tazeleme, korumalı dizin, WAF, log temizliği, kısmi silme, yol paylaşımı, budama, açık DNS |

## Kapanmayanlar (bilerek, gerekçeli)

- **İmza zorunluluğu açık değil**: üretilen paket imzasız; zorunlu kılmak kurulu her paneli
  güncellenemez yapardı. Kural yazılı ve testli, `RequireSignature` bayrağıyla açılıyor.
- **Kullanıcı silme ucu hâlâ 200 dönüyor**: uyarıları yanıta taşımak Core imza değişikliği ister.
  Kalıntılar denetim kaydında, ama WHMCS hâlâ `success` görüyor.
- **`subscriptionUserId` 219 kaydın 26'sında dolu**: kalan denetleyicilerin listesi denetim
  ajanının raporunda; sonraki tura.
- **`SubscriptionEnforcementService` kaydı aktörü yanlış yazıyor** (elle askıya almada
  "müşteri kendini askıya aldı" diyor). Denetleyiciye doğru kayıt eklendi, asıl düzeltme kaldı.

## Canlıda doğrulanmayanlar

Bu makinede IIS siteleri, Windows DNS, veritabanı motorları, Stalwart, Docker, WHMCS ve bulut
hesapları yok. Erişim izni değişiklikleri gerçek dosya sistemi üzerinde test edildi ama **kurulu
bir panelde uygulanmadı**. DNS özyineleme kapatma ve PostgreSQL veri dizini adımları Windows
Server'da doğrulanmalı.

**İlk kurulumda elle denenecekler:** site açma, site silme, yedek alma ve geri yükleme, posta
gönderme, FTP bağlantısı.

## Bu turdan çıkan ders

Denetim raporunu depoya koymak, on ajana bulguları tekrar anlatmadan `dosya:satır` referansı
vermeyi sağladı. Yeni denetim nöbetçisi ilk turunda kendi borç listesinin bayatladığını yakaladı
(17 uç kayıt yazmaya başlamıştı) — yani nöbetçi gerçekten çalışıyor.
