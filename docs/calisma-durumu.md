# Çalışma durumu — canlı kayıt

**Bu dosyanın amacı:** elektrik kesilse, oturum kapansa ya da kota dolsa bile
"kaldığımız yerden devam" dendiğinde nerede olduğumuzun görülmesi.
Belirli aralıklarla ve her önemli adımdan sonra güncellenir.

Son güncelleme: **27.08.2026** · Yayındaki sürüm: **0.7.2** · main: temiz
Testler: **3578 yeşil** · typecheck + build temiz · i18n TR/EN 4588 = 4588

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
| 0.7.1 | Kalan bütün teknik borçların kapatılması + iki kırılgan testin düzeltilmesi |
| 0.7.2 | Site kurucuda çok sayfa + çalışan iletişim formu, SEO günlük analizi, istatistikte sıkıştırılmış günler |

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

## AÇIK KALAN TEKNİK BORÇ

Bu turda 2-7 arası maddelerin **hepsi kapatıldı** (WordPress veri kopyalama, kiracı bağı,
WHMCS notu, uzun işlerin sayfa yenilemeye dayanması, posta listesi alias'ları, ayrılmış
aygıt adları) ve iki kırılgan test saatten kurtarıldı.

### 1. CANLIDA DOĞRULAMA — tek gerçek eksik
Bu makinede IIS siteleri, Windows DNS, veritabanı motorları, ayakta Stalwart, Docker, ClamAV
ve WHMCS **yok**. Son sürümlerde erişim izinleri, silme kuralları, kullanıcı silme yanıtı
değişti ve her isteğe bir modül eklendi.

**DÜZELTME (27.08.2026):** Daha önce buraya *".htaccess canlıda doğrulandı"* yazmıştım —
**YANLIŞTI.** `alioral.xyz` üzerinde gördüğüm temiz adresler, panelin ZATEN VAR OLAN Laravel/
WordPress `web.config` yeniden yazma özelliğinden geliyor olabilir; iki mekanizmayı ayırt etmeden
sonuca atladım. Doğru test: `.htaccess` dosyasını geçici olarak yeniden adlandırıp adreslerin hâlâ
çalışıp çalışmadığına bakmak.

Dahası: bu makineye 0.7.2 kurulduğunda ölçüldü — **modül DLL'i pakette HİÇ YOKTU.** Modül
solution'da olduğu için testleri yeşildi ama publish yalnız API projesini alıyordu. Yani
`.htaccess` özelliği 0.7.0, 0.7.1 ve 0.7.2'de ÖLÜ yayınlandı. Paketleme düzeltildi ve
`HtaccessModulePackagingTests` ile nöbete bağlandı.

Hâlâ elle denenmesi gerekenler: site açma/silme, yedek alma/geri yükleme, posta gönderme,
FTP bağlantısı, bir deneme müşterisi silme, `.htaccess` "yalnız gözlem" kipi.

### 2. Bilerek kapsam dışı bırakılanlar (gerekçeli)
- **SEO sıra takibi ve rakip analizi** — ücretli SERP sağlayıcısı olmadan üretilecek her sıra
  numarası UYDURMA olur. Altyapı hazırlanabilir; sayı uydurulmaz.
- **Site kurucuda sürükle-bırak** — üretilen işaretlemenin şeklini kullanıcıya bırakmak,
  kaçırma güvenilirliğini bırakmaktır (depolanmış XSS).
- ~~Site kurucuda çok sayfa / iletişim formu, SEO log dosyası analizi~~ — **0.7.2'de yapıldı**.
- **Kod imzalama** — paket imzasız; kural yazılı ve testli, `RequireSignature` bayrağıyla
  açılır. Sertifika ticari karar.

### 3. Küçük kalıntılar
- ClamAV kurulumunun iş kaydı **Yazılım** ekranının şeridinde görünüyor (Antivirüs ekranının
  kendi şeridi yok) — arayüz işi.
- `MailController.public-base-url` hâlâ somut servisi çağırıyor (alias uçları arayüze taşındı).
- `.snk` özel anahtarı depoda; `origin/main` yalnız README içerdiği için makineden çıkmıyor,
  ama depo kaynak olarak yayınlanırsa değiştirilmeli.
- `CacheService.ReadServiceStatusAsync` içindeki `JsonDocument.Parse` korumasız — bilerek
  (gürültülü hata, sessiz hatadan iyidir).

## Referans belgeler

- `docs/denetim-2026-08-19.md` — yedi denetimin tam bulgu raporu
- `docs/denetim-duzeltme-durumu.md` — on düzeltme ekibinin sonuçları
- `CODEX_MEMORY.md` bölüm 217 — bu turun kalıcı kaydı

---

## Açık iş: Dosya yöneticisi Plesk seviyesine çıkarılıyor (28.08.2026)

Kullanıcı siteye `.htaccess` koyamadı. Sebep panelde ölçüldü: **dosya oluşturma yok.**
Bakınca eksik tek başına değildi.

### Ölçülen durum
- `PUT /files` (`FileService.WriteAsync`) var olmayan dosyayı **yaratıyor** — arka uç hazır,
  arayüzde düğme yok. `.htaccess` koyamamanın tek sebebi buydu.
- `POST /files/move` var, uzun bir gerekçe yorumu da yazılmış ("taşıma hiç yoktu, kullanıcı
  indirip tekrar yüklemek zorundaydı") — ama `frontend/` genelinde **hiç çağrılmıyor.**
  `.htaccess`'in 0.7.0-0.7.2'de ölü yayınlanmasıyla aynı sınıf: arka uç hazır, teslim yolu kopuk.
- **Kopyalama hiç yok** — ne serviste, ne uçta, ne arayüzde.
- Nokta dosyaları listeleniyor (`EnumerateFileSystemInfos()`, filtre yok) — `.htaccess` panelde
  görünür. Bu KAPALI değil.

### Yürüyen iki ajan
1. Arka uç: `CopyAsync` + `POST /files/copy`; Move'un güvenlik ön hazırlığı tek kaynağa
   çıkarılıyor. Ayrıca ÖLÇÜLECEK: `WriteAsync` `EnsureInsideSandbox` çağırmıyor, `MoveAsync`
   çağırıyor — `ResolveTargetAsync` denetimi kendisi yapıyor mu? Delik olabilir.
2. Arayüz: yeni dosya / yeniden adlandır / taşı / kopyala + i18n anahtarları.

### Bitmeyen iş
- **`.htaccess` canlı ayrım testi hâlâ YAPILMADI.** `C:\inetpub\wwwroot\test.local` ve
  `C:\ProgramData\AdaPanel\htaccess` yükseltilmiş yetki istiyor; ölçüldü, yazılamıyor.
  Dosya oluşturma arayüze gelince test **panelin kendisiyle** yapılacak — servis o klasörlere
  zaten yazabiliyor.
  Test: `RewriteRule ^adapanel-test-42$ /iisstart.htm [R=302,L]` → adres 302 dönmeli →
  dosya `.htaccess.kapali` yapılınca **404 dönmeli**. Üçüncü adım şart: geçen sefer onu
  atladığım için `alioral.xyz`'de yanlışlıkla "çalışıyor" demiştim.

### 0.7.5 yayinlandi (28.08.2026)

`SHA256 E1D4E804E57D6A16DB553BC3822F0FD20F68C42141CE6294006E525227895464` — 90.540.472 bayt.
Paket yuklendi, indirilip ozeti yerel dosyayla karsilastirildi. **3633 test yesil.**

Yapilanlar: dosya olusturma/yeniden adlandirma/tasima/kopyalama arayuze baglandi; sunucu geneli
`.htaccess` anahtari panele geldi (yonetici-only, rota site agacinin disinda); "yeni dosya"
var olanin ustune yazamiyor (`FileMode.CreateNew`); yarim yazilan `module.switch` artik daha
ACIK kip uretmiyor.

**Yayin sirasinda ayri bir hata olculdu ve kapatildi:** `publish-github-release.ps1`
`-InstallerPath` verilmeden calistirilinca release'i PAKETSIZ olusturup "Tamamlandi" dedi.
Paketsiz yayin, kurulu tum panellerde kendi kendine guncellemeyi kirar. Nobetci eklendi
(`ReleaseAssetGuardTests`), iki yonu de olculdu.

### SIRADAKI IS — `.htaccess` canli ayrim testi

0.7.5 bu makineye kurulunca panelin KENDISIYLE yapilacak (artik elle komut gerekmiyor):

1. Barindirma -> .htaccess -> **Sunucu geneli anahtar** = Uygula
2. Ayni ekranda site kipi = Uygula (`test.local`)
3. Dosyalar -> **Yeni dosya** -> `.htaccess`, icerik:
   `RewriteEngine On` / `RewriteRule ^adapanel-test-42$ /iisstart.htm [R=302,L]`
4. `curl -I -H "Host: test.local" http://127.0.0.1/adapanel-test-42` -> **302** beklenir
5. Dosyayi **Yeniden adlandir** -> `.htaccess.kapali`
6. Ayni adres -> **404** DONMELI

6. adim sart: gecen sefer onu atladigim icin `alioral.xyz`'de yanlislikla "calisiyor" demistim.

---

## KAVSAK DONGUSU (junction loop) — sinif capinda hizmet reddi (28.08.2026)

Dosya yoneticisine klasor boyutu eklerken cikti. Kiraci KENDI sitesinin kokune, kendi ust
agacini gosteren bir junction koyarsa, o agaci gezen her kod SONSUZA doner.

**Kapatildi:**
- `CustomerScopeService.DirectorySize` — `SearchOption.AllDirectories` kullaniyordu (baglantilari
  TAKIP EDER, butcesiz). Bu SICAK YOLDA: her dosya yuklemesinde, kullanici listesindeki HER
  Customer satirinda ayri ayri, abonelik/fatura ekranlarinda cagriliyor.
- Yuruyus tek kaynaga cikarildi: `src/AdaPanel.Infrastructure/Files/SafeDirectoryWalk.cs`
  (yigin, ReparsePoint atlama, oge+sure butcesi, kismi sonucu DAMGALAMA).
- Eski `catch { }` iptali de yutuyordu; iptal artik yukari cikiyor.

**Kota icin verilen karar:** kesilmis toplam ALT SINIR sayilir, islem BLOKE EDILMEZ. Cunku
kesilmenin uc sebebinden ikisi kiracinin elinde degil (yavas/ag diski, ACL reddi) ve bloke etmek
sunucunun yavasligi yuzunden mesru musterinin hicbir dosya yukleyememesi olurdu. Kaybedilen:
butceyi bilerek dolduran kiraciya karsi kesinlik. **Gercek cozum** donus tipinin "olculdu mu"
bilgisini tasimasidir — trafik kotasinda ZATEN oyle (`TrafikOlcAsync` -> `(Bytes, Measured)`).
`ICustomerScopeService` sozlesmesi ve 15+ cagiran ilgilendirdigi icin ayri bir is olarak duruyor.

**Ayni sinifta kalan, su an duzeltilen uc yer:**
- `SiteMigrationService:516` — `.user.ini` taramasi; `.Take(200)` SONUCU sinirlar, YURUYUSU degil.
- `BackupService.ScanFiles:1159` — elle `Stack` yuruyusu, grep'e TAKILMIYOR; ReparsePoint atlamasi
  ve butce YOK. Yedegi donguye sokar ya da arsivi sisirir.
- `SiteHostingSettingsService:719,816,822` — indirilen zip'in agaci; kavsak riski yok ama zip
  bombasina karsi butce yok.

**Guvenli oldugu OKUNARAK dogrulanan 32 kullanim** panelin kendi klasorlerinde (ProgramData
runtimes, bilesen InstallDir, indirme calisma alani). Not: bu grubun guvenligi K1 (ProgramData
ACL) bulgusuna bagli.

**Ders:** `grep "AllDirectories"` bu sinifi TAM BULMAZ — `BackupService.ScanFiles` elle yazilmis
bir yuruyustu ve yalnizca dosya dosya okunarak goruldu.
