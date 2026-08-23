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
- ~~**`subscriptionUserId` 219 kaydın 26'sında dolu**~~ → **KAPANDI (sonraki tur).** Denetleyici
  kayıt çağrılarının 26/104'ü konu yazıyordu; artık 70/140. Konu, site sahipliğinden, posta alan
  adı sahipliğinden ya da `IDatabaseOwnershipService`'ten **ölçülerek** çözülüyor
  (`AuditSubject`); ölçülemeyen konu **boş bırakılıyor**. Kalan 70 çağrının 65'i gerekçesiyle
  `AuditCoverageTests.KiraciyaAitOlmayanlar` içinde (sunucu geneli ya da aktör = konu), 5'i
  `KiraciBagiSonrakiTurda` borç listesinde.
- ~~**`SubscriptionEnforcementService` kaydı aktörü yanlış yazıyor**~~ → **KAPANDI (sonraki tur).**
  Kayıt gerçek aktörü ve IP'yi HTTP bağlamından okuyor, konu abonelik sahibi; tetikleyici
  (panel isteği / arka plan turu) ölçülerek yazılıyor. Denetleyicideki yama niteliğindeki ikinci
  kayıt kaldırıldı — tek eylem artık tek satır üretiyor.

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

---

# Ikinci tur — kalan maddeler (KOTA NEDENIYLE DURDURULDU)

Kullanici "antivirus haricinde hepsini duzelt" dedi. Dort ekip basladi, kota
sinirina takilmamak icin is ortasinda durduruldu. Worktree'leri ve isleri yerinde.

| Ajan kimligi | Kapsam | Durdugu nokta |
|---|---|---|
| `a9906ab5b48031bce` | **Kiraci bagi**: 219 denetim kaydinin yalniz 26'sinda `subscriptionUserId` dolu; nobetcideki 22 ucluk borc listesi. Ayrica `SubscriptionEnforcementService.cs:198,225` askiya alma kaydinda AKTORU yanlis yaziyor ("musteri kendini askiya aldi"). | `WordPressController`e geciyordu (o denetleyicide hic denetim servisi yok) |
| `a30c8c4df96731df0` | **Silme butunlugu**: `UserManagementService.cs:521-534` cascade uyarilarini atiyor, `200 OK` donuyor; yarim silinmis site sahipsiz kaliyor ve WHMCS `success` goruyor. + `SiteCascadeDeletionService.cs:463-468` `_not_found` son ekiyle "zaten silinmis" varsayimi. | Kod ve testler YAZILDI; mutasyon dogrulamasinin 1. adimindaydi (cascade kalintilarini yine atma mutasyonu). Devam edince mutasyon turunu bitirip COMMIT etmeli. |
| `ad61a66cb86a73e3e` | **Gozlemlenebilirlik**: `CacheService`, `FileMigrationService`, `WordPressToolkitService` (1018 satir) — `ILogger` hic yok, denetim kaydi da yok. | (durum bildirilmedi) |
| `acc99529e08e59037` | **Tarama ilerlemesi**: 125 dakikalik virus taramasi sayfa yenilenince kayboluyor (`/antivirus/status` yalniz BITEN taramayi donduruyor; `IOperationProgressService` referans alinacak). + Docker ornek imaji (linux imaj / Windows container motoru), ~14 olu i18n anahtari, "Sil"/"Kaldir" tutarsizligi, firewall "Kapali" ile `status.disabled` "Kapali" karisikligi. | (durum bildirilmedi) |

## Devam ettirirken tekrarlanacak sartlar

- **COMMIT ETMEYI HATIRLAT** — bu oturumda uc ajan isini bitirip commit etmeden durdu.
- **Silme ekibi:** "tamamen silinsin" daha AGRESIF silme demek DEGIL. Cascade kapilari
  (`SiteDeletionPathGuard`) yerinde kalsin; istenen sey yarim kalani durustce bildirmek.
  Olcut: WHMCS gibi bir istemci yanita bakip "hizmet tamamen kaldirildi" diyememeli.
- **Kiraci bagi:** kiraci OLCULECEK, tahmin edilmeyecek. Cozulemiyorsa alan BOS kalacak --
  yanlis kiraciya baglamak, hic baglamamaktan kotudur.
- **Tarama ilerlemesi:** sahte yuzde YASAK. Olculemiyorsa "suruyor, X dakikadir" denecek.
- **Log ekibi:** o uc serviste parola ve FTP kimlik bilgisi dolasiyor; ILogger merkezi
  temizleyiciden GECMEZ, sirri kendisi dusurecek.

## Bu turda YAPILMAYACAK (kullanicinin isi ya da ticari karar)

- **Antivirus kurulumu** — kullanici acikca kapsam disi biraktı. Bu makinede `WinDefend`
  servisi YOK (olculdu) ve daha once aktif PE infector cikmisti; paket uretilen makine burasi.
- **Kod imzalama** — paket imzasiz; kural yazili ve testli, `RequireSignature` bayragiyla
  acilir. Sertifika alinmasi ticari karar.
