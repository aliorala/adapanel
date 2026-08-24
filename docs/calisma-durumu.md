# Çalışma durumu — canlı kayıt

**Bu dosyanın amacı:** elektrik kesilse, oturum kapansa ya da kota dolsa bile
"kaldığımız yerden devam" dendiğinde nerede olduğumuzun görülmesi.
Belirli aralıklarla ve her önemli adımdan sonra güncellenir.

Son güncelleme: **24.08.2026** · Yayındaki sürüm: **0.7.0** · main: temiz
Testler: **3232 yeşil** · typecheck + build temiz · i18n TR/EN 4383 = 4383

---

## ŞU AN AÇIK İŞ YOK

Çalışan ajan yok. Denetimden çıkan bütün maddeler kapatıldı ve yayınlandı.

---

## Yayın geçmişi (bu çalışma turu)

| Sürüm | İçerik |
|---|---|
| 0.6.1 | Yedeklerde kaynak başına klasör + site başına posta yedeği |
| 0.6.2 | Sürüm notları panelde okunur hale geldi |
| 0.6.3 | Plesk paritesi: 11 alan + 4 sessiz arıza |
| 0.6.4 | Posta listeleri (gönderim kuralı + self-servis abonelikten çıkma) |
| 0.6.5 | Güvenlik sertleştirmesi (7 denetim, 10 düzeltme ekibi) |
| 0.6.6 | Silme bütünlüğü, tarama ilerlemesi, kiracı bağı, gözlemlenebilirlik |
| 0.7.0 | **Kendi `.htaccess` desteğimiz** — Plesk for Windows'da olmayan özellik |

---

## KULLANICININ YAPACAKLARI (bende değil)

1. **`Administrator` parolası** — mutasyon testi sırasında ajan tarafından değiştirildi.
   Hesap devre dışı, uzaktan erişilemez. `net user Administrator *` ile kendi parolasını atamalı.
2. **Antivirüs** — bu makinede `WinDefend` servisi YOK (ölçüldü) ve daha önce aktif PE infector
   çıkmıştı; müşteriye giden paket burada üretiliyor. **Kullanıcı bunu kapsam dışı bıraktı.**
3. **Kod imzalama sertifikası** — ticari karar. Kural yazılı ve testli, `RequireSignature`
   bayrağıyla açılıyor. Paket şu an imzasız.
4. **0.6.5 ve 0.6.6'yı deneme sunucusunda dene** — 0.6.5 erişim izinlerini ve silme kurallarını
   değiştirdi, 0.6.6 kullanıcı silme yanıtını değiştirdi.

---

## AÇIK KALAN TEKNİK BORÇ (öncelik sırasına göre)

### 1. Hiçbir şey canlıda doğrulanmadı — en büyük eksik
Bu makinede IIS siteleri, Windows DNS, veritabanı motorları, ayakta Stalwart, Docker, ClamAV
ve WHMCS **yok**. Son iki sürümde erişim izinleri, silme kuralları ve kullanıcı silme yanıtı
değişti. İlk kurulumda elle denenecekler: site açma/silme, yedek alma/geri yükleme,
posta gönderme, FTP bağlantısı, bir deneme müşterisini silme.

### 2. `CopyDataAsync` — sessizce yanlış veritabanına bağlanma
`WordPressToolkitService`: hedefin `wp-config.php`'sine DB bilgilerini geri yazan işlem bir regex
değiştirme. Kalıp bulunamazsa (kaynak `require`/`getenv()` kullanıyorsa ya da farklı tırnak
biçimindeyse) **hiçbir şey yapmadan döner**. O durumda canlı hedef, staging'in veritabanına
bağlanmaya devam eder — üstelik yıkıcı `/MIR` aynalamasından SONRA.
**Şu an ölçülüyor ve loglanıyor ama kullanıcıya SÖYLENMİYOR.** Gerçek düzeltme tasarım
değişikliği ister: `ReplaceDefine`'ın "değiştiremedim" diyen bir sürümü + kullanıcıya bildirim.

### ~~3. Denetim kaydında 5 uç kiracıya bağlanamadı~~ — KAPANDI
`KiraciBagiSonrakiTurda` listesi **boş**. Sahiplik her yerde ÖLÇÜLÜYOR: barındırma ayarlarında
site sahibi, cron'da görev adının abonelik öneki (`u<id>--`), faturalamada panelin eşleştirdiği
hesap, posta listesinde liste adresinin alan adı. Kimliksiz abonelikten çıkma ucu kiracıya
bağlandı — orada `UserId` yapısal olarak boş olduğu için konu, kaydın kiracıya ulaşmasının TEK
yoluydu. İki kayıt gerekçesiyle `KiraciyaAitOlmayanlar`'a taşındı (panel geneli faturalama
ayarı/raporu, makine PATH'i).

### ~~4. WHMCS modülü kalıntı listesini WHMCS notuna yazmıyor~~ — KAPANDI
Modül artık **her iki yolda da** gövdeyi okuyor (200 + `success:true` ve 409) ve kalıntıları
`AddClientNote` ile müşteri notuna yazıyor; not yazılamazsa metin modül günlüğüne düşüyor.
Sözleşme `developers.whmcs.com`'dan doğrulandı. **Gerçek WHMCS'te hiç çalıştırılmadı** —
PHP tarafının ölçümü `php integrations/whmcs/tests/leftover-note.test.php`.

### 5. `useRunningTask` sayfa yenilemeye dayanmıyor
Yalnız antivirüs taraması sunucu tarafı duruma kavuştu. Diğer uzun işler (yedek, göç, kurulum)
sayfa yenilenince "sürüyor" bilgisini hâlâ kaybediyor.

### 6. Plesk paritesinde bilerek kapsam dışı
SEO sıra takibi ve rakip analizi (SERP sağlayıcısı olmadan üretilecek sıra numarası uydurma
olurdu), log dosyası analizi (ayrıştırıcı zaten istatistik servisinde), site kurucuda çok
sayfalı site / iletişim formu / sürükle-bırak, posta listesi alias'ları.

### 8. `.htaccess` — CANLIDA DOĞRULANDI (24.08.2026)

`alioral.xyz` (185.7.242.225, IIS 10, Laravel) üzerinde dışarıdan ölçüldü:
`/karsilastir` → HTTP 200, 521 KB, Laravel oturum çerezi;
`/kategori/akilli-saat` → HTTP 200, 1.4 MB, çerez. İkisi de diskte DOSYA DEĞİL — Laravel
rotası. Yeniden yazma olmasaydı IIS 404 verirdi. Yani modül gerçek IIS'te, gerçek bir istekte,
"uygula" kipinde ÇALIŞIYOR.

Not: `/login` ilk denemede 404 verdi ama o KOORDİNATÖRÜN TAHMİNİYDİ — uygulamada öyle bir rota
yok. Sonuç ondan çıkarılmadı; sitenin gerçek bağlantıları HTML'den okunup onlarla ölçüldü.

Hâlâ ölçülmeyen: GAC kurulumunun ve `applicationHost.config` kaydının hangi adımda yapıldığı
(kurulum otomatik yaptı, adım adım izlenmedi); "yalnız gözlem" kipinin canlı davranışı;
Node/Python havuzlarında modülün gerçekten atlandığı.
Bilinen sapmalar (kodda ve arayüzde yazılı): erişim denetimi yeniden yazmadan ÖNCE çalışıyor
(Apache'de tersi, biz daha kısıtlayıcıyız); `IsSymbolicLink` `FileAttributes.ReparsePoint`
kullanıyor, yani OneDrive yer tutucusu gibi şeylere de "bağlantı" diyor (koruyucu yönde).
`EngineLoadedInIis = true` "şu anda yüklü" demek DEĞİL — panel `w3wp`'nin içini göremez,
ölçülebilen tek ön koşuldan türetiliyor; `false` tarafı kesin, `true` tarafı değil.

### 9. `.snk` özel anahtarı depoda
Strong-name kimlik mekanizmasıdır, güvenlik sınırı değil; ve `origin/main` yalnız README
içerdiği için anahtar bu makineden ÇIKMIYOR. Ama depo bir gün kaynak olarak yayınlanırsa
anahtar değiştirilmeli.

### 7. Düşük öncelik
Windows ayrılmış aygıt adları (`CON`, `NUL`) sanitize edilmiyor — ölçüldü, sessiz veri kaybı
değil gürültülü çökme üretiyor. `CacheService.ReadServiceStatusAsync` içindeki `JsonDocument.Parse`
korumasız — bilerek bırakıldı (gürültülü hata, sessiz hatadan iyidir).

---

## Referans belgeler

- `docs/denetim-2026-08-19.md` — yedi denetimin tam bulgu raporu
- `docs/denetim-duzeltme-durumu.md` — on düzeltme ekibinin sonuçları
- `CODEX_MEMORY.md` bölüm 217 — bu turun kalıcı kaydı
