# Denetim düzeltmeleri — devam durumu

Kaynak: `docs/denetim-2026-08-19.md`. On ajana bölündü, kota sınırı nedeniyle altısı
iş ortasında durduruldu. Bu dosya, devam ettirmek için gereken her şeyi tutar.

Son durum: main `0d62744`, **2535 test yeşil**, typecheck + build temiz,
i18n TR/EN 4373 = 4373 parite tam.

## Birleşti (4)

| Ajan | Kapsam | Sonuç |
|---|---|---|
| Posta autoconfig arayüzü | F2 | Ölü özellik bağlandı; arka uç sözleşmesi canlıda **doğrulanmadı** |
| İşlevsel arızalar | K8, F1, F3, F6, F7, F10 | Arşiv içe aktarma artık siteyi boşaltmıyor; memcached gerçeği ölçülüyor; nginx kill yolu düzeldi |
| Yedek güvenilirliği | K10, Y9, yedek denetim kaydı | Atlanan dosya `created_with_errors`'a taşınıyor; budama son kullanılabilir noktayı silemiyor |
| Arayüz | P3 tamamı | Ham anahtarlar, response interceptor + 401, erişilebilirlik, kalan `window.confirm` gitti |

## Duruldu — kaldığı yerden devam edecek (6)

Her biri kendi worktree'sinde, işi duruyor. `SendMessage` ile devam ettirilecek.

| Ajan kimliği | Kapsam | Durduğu nokta |
|---|---|---|
| `a97f1dd82ef91c102` | **Sertleştirme** — K1 Stalwart parolası, K2 MariaDB veri dizini, K3 certs, K4 ACL mühürleme, K5 kurtarma yolu, Y2 bileşen kaydı, Y10 açık DNS | Mutasyonla bulduğu iki zayıf testi güçlendiriyordu |
| `aa63fb4d0ea15ed30` | **Yetki yükselmeleri** — K6 FTP kullanıcı adı sahiplenme, K7 WordPress klon alan adı, F4, F5 | `SitesController` giriş noktasını (müşterinin gönderdiği `FtpUserName`) test ediyordu |
| `a77458b6ae06bc0c6` | **Güncelleme + yayın** — K11 hüküm yarışı ve günlük çakışması, K12 installer taraması | Tam takımı çalıştırıyordu |
| `ad4ddeee2b9f7f9ee` | **Silme güvenliği** — K9 `IsSafeToDelete`, Y1 DB sahipliği, Y6 log temizliği, Y7 kısmi silme, Y8 yol paylaşımı | 2531 test yeşil, mutasyon doğrulamasına giriyordu |
| `a122b259bfd9d5c49` | **Kayıt↔gerçeklik** — Y3 SSL kaydı, Y4 korumalı dizin, Y5 WAF bayrakları + SSL/Security denetim kaydı | 37 test geçmiş, bağlama mutasyonlarını ekliyordu |
| `a79fe950e31864938` | **Denetim kaydı** — `AuditCoverageTests` yeniden yazımı, 18 denetleyici, redactor | Yeni ctor parametrelerinin kırdığı iki testi onarıyordu |

## Devam ettirme

Her ajana `SendMessage` ile: "kaldığın yerden devam et; işin bitince worktree'de
COMMIT et (push etme)". **Commit etmeyi hatırlatmak şart** — iki ajan işini bitirip
commit etmeden durdu, koordinatör elle commit etti.

Devam ederken hatırlatılacak riskler (ilk turda verilmişti, ara verildiği için tekrarlanmalı):

- **Sertleştirme:** fazla kapatırsan IIS, FTP ve posta çalışmaz. Her kapatma için
  "bu yola kim erişmek zorunda" sorusunu koddan cevapla.
- **Güncelleme:** bu yolu bozarsan kurulu paneller bir daha güncellenemez.
  `AdaPanel.iss` ve `install-service.ps1` yorumları gerçek olay analizlerinden çıkmış.
- **Silme:** beyaz listeyi fazla dar yaparsan meşru site silme çalışmaz; iki yönü de test et.
- **Yetki:** mevcut kurulumlarda zaten oluşturulmuş FTP hesapları var, çalışan kurulumları kırma.

## Birleştirme sırası ve bilinen çakışma

`frontend/src/i18n/tr.json` ve `en.json`'a birden çok ajan anahtar ekliyor; cherry-pick
sırasında çakışırlar. Çözüm: iki tarafın anahtar kümelerini birleştir (aynı anahtara farklı
değer bugüne kadar hiç çıkmadı), sonra pariteyi doğrula.

`ServiceCollectionExtensions.cs` de birden çok ajanın kayıt eklediği bir nokta — aynı yöntem.

## Bitince

Tam takım + `npm run typecheck` + `npm run build` yeşil olduğunda **0.7.0** olarak yayınla
(`scripts/publish-github-release.ps1` ile, elle PowerShell yazma). Sürüm güvenlik
sertleştirmesi ağırlıklı olduğu için küçük sürüm numarası yanıltıcı olur.

Sürüm notlarında **canlıda doğrulanmayanlar** açıkça yazılmalı: bu makinede IIS siteleri,
Windows DNS, veritabanı motorları, Stalwart, Docker, WHMCS ve bulut yedek hedefleri yok.
