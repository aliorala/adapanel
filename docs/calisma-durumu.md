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
**KAPANDI (28.08.2026):** `GetDiskUsageMbAsync` artik `DiskUsage(UsageMb, Measured,
TruncatedReasonKey)` donuyor; ayrintisi asagidaki "ACIK KALAN BORCLAR" 1. maddesinde.

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

---

## 0.7.7 — denetim turu (28.08.2026)

`SHA256 B999ED354C69094A9CE26BDFFE0970811B4E320E75717C739DD7ACD335C15890`
**3724 test yesil** (onceki 3667). Paket yuklendi ve indirilip ozeti karsilastirildi.

Bulunanlarin buyuk kismi ONCEKI IKI SURUMDE BIZIM actigimiz hatalardi. Kayda geciyor:
- 0.7.6'da dort agac yuruyusunu tek kaynaga indirirken, 0.7.5'te acilan BESINCI yuruyusu
  (`KlasoruKopyala`) gormedik. Nobetci testin dosya listesinde `FileService.cs` yoktu.
  **DERS: nobetci yazarken "hangi dosyalara bakiyor" da denetlenmeli.**
- Yayin nobetcisini eklerken bayrak birlesimini dusunmedim ve testleri yalniz kaynak metnine
  baktigi icin bunu goremedi. **DERS: nobetcinin kendisi de davranissal olarak test edilmeli.**
- Iki mutasyon KIZARMADI: biri kodun degil YORUMUN icindeki kelimeye tutunuyordu
  (`FileMode.CreateNew` hem kodda hem yorumda geciyor). **DERS: kaynaga bakan test, yoruma
  da denk gelebilecek bir dizeye tutunmamali.**

### Duzeltilen (ozet)
Kota 4 yoldan asilabiliyordu (kopyalama/yazma/arsiv acma kotasiz; MB tamsayi bolmesi;
alt kullaniciya kota HIC uygulanmiyordu) · ozyinelemeli+butcesiz kopyalama · "zaten var" yalani
(yarim yazma kalintisi) · sunucu geneli .htaccess'in eksik govdeyi "kapat" sayması ·
tanimsiz CSS belirtecleri (10 ad) · klavyeyle dosya yukleme imkansizdi · .htaccess ekraninin
6 bolumu bicimsizdi · "INFO" Turkce buyuk harf hatasi · onaysiz 2 yikici islem.

### ACIK KALAN BORCLAR (siradaki turda)

1. ~~**Disk kotasi "olculdu mu" bilgisi tasimiyor.**~~ **KAPANDI (28.08.2026).**
   `GetDiskUsageMbAsync` artik `DiskUsage(UsageMb, Measured, TruncatedReasonKey)` donuyor
   (kalip trafik kotasindan kopyalandi). Verilen kararlar:
   - ZORLAMA (yukleme/yazma/kopyalama/arsiv acma): olculemeyen kullanim SIGIYORSA kota
     uygulanmaz (trafikle ayni felsefe); ama ALT SINIR tavani asiyorsa red KESINDIR — trafikten
     tek ayrilma noktasi, cunku oradaki yaptirim askiya almak, buradaki tek bir yazmayi
     reddetmek.
   - GOSTERIM: `UserSummaryDto` + `SubscriptionUsageDto` + WHMCS hesap satiri artik
     `DiskMeasured` + `DiskUsageTruncatedReasonKey` tasiyor; WHMCS modulu olculemeyen diski
     YAZMIYOR (eski degeri koruyor — bant genisligiyle ayni kural, aksi halde eksik olcum
     musteriden kullandigindan az ucret almak demekti).
   - IIS'ten okunamayan site artik "0 bayt" SAYILMIYOR (Measured=false).
   - **ARAYUZ BORCU:** `frontend/` bu turda kapsam disiydi. Uc DTO alani API'de VAR ama hicbir
     ekran GOSTERMIYOR; sayinin eksik oldugu kullaniciya soylenmiyor. Gereken tek yeni anahtar:
     "bu sayi eksik olabilir" rozeti (ornek ad `users.disk_usage_partial`); "neden" ayrintisi
     icin yeni anahtar GEREKMEZ, `files.size_truncated_entries|_time|_denied` tr/en'de zaten var.
2. **`SiteCascadeDeletionService.CountFilesSafely`** elle yazilmis DORDUNCU yuruyus. Kavsak
   atlamasi ve limiti VAR (yani guvenli) ama `SafeDirectoryWalk`'a tasinmali.
3. **`MessageKeyCoverageTests` iki korluk:** `Detail` alanini hic taramiyor; degisken uzerinden
   atilan anahtarlari goremiyor.
4. **`BackupService`'te 2 sabit Turkce `Detail`** daha var (satir ~3290 ve ~4440); ikincisi
   Turkce karakter tasiyor, Ingilizce panelde yari-cevrilmis gorunuyor.
5. **`base.css`'e `--on-warn` gerekiyor** (`--on-accent`in uyari karsiligi); simdilik `#000`.
6. **`SubscriptionCard` kota gostergesi** `15.5 GB` yerine `16 GiB` gosteriyor — 10-100 arasinda
   bir ondalik kaybi. Tek kaynaga gecisin GORUNEN yan etkisi; kullanicinin fark edecegi tek yer.
7. **27 gorunum** `empty-state`'i "yuklendi mi" bayragi olmadan gosteriyor: gelmemis liste
   "burada bir sey yok" gibi okunuyor. Dogru ornek `SiteBackupView` (`listLoaded`).
8. **`AttentionDrawer.vue:325`** `outline: none` var ve yerine bir gosterge KONULMAMIS.

### HALA YAPILAMAYAN: `.htaccess` canli ayrim testi
Iki sey gerekiyor: (a) 0.7.7'nin kurulmasi, (b) panel `admin` PAROLASI.
Denenen ve REDDEDILEN parolalar: `AdaPanelTest2026!`, `AdaPanelTest2026!yeni`.
Olculdu: hesap KILITLI DEGIL (kilit `auth.user_locked`, IP engeli `auth.ip_banned` doner;
alinan cevap `auth.invalid_credentials`). Esik 10 deneme, 3 kullanildi.

---

## 0.7.8 — sekiz borcun hepsi kapandi (28.08.2026)

`SHA256 897AFD96C00B30A4302CC56EC314B3FB74766DFCA10A22BC7E6D47CAE154600B`
**3739 test yesil** (onceki 3724). Paket yuklendi ve indirilip ozeti karsilastirildi.

Kapatirken cikan UC sey, listedekilerden onemliydi:
1. **Fatura musteriden AZ ucret alabiliyordu** — eksik olculmus disk degeri fatura
   entegrasyonuna gidiyordu. Modul artik olculemeyen diski yazmiyor.
2. **Bildirim zilindeki sayac rozeti okunmuyordu** — varsayilan acik temada 4.09, koyu temada
   2.21 kontrast. Panelde kac sorun oldugunu soyleyen sayi.
3. **"Silinemedi" diyen ekran yaniliyor olabilirdi** — iki olcum de 20.000 tavanina dayaninca
   fark 0 cikiyordu. OLCULDU: panelin kendi node_modules agaci 17.459 dosya (%87).

### Bu turun kalici dersleri
- **Nobetci, bakmadigi dosyadaki ihlali goremez.** (`FileService.cs` liste disindaydi.)
- **Nobetcinin kendisi de davranissal test edilmeli.** (Yayin nobetcisi bayrak birlesimiyle
  atlanabiliyordu, testleri yalniz metne bakiyordu.)
- **Kaynaga bakan test, YORUMDA da gecen bir dizeye tutunmamali.** (`FileMode.CreateNew` hem
  kodda hem yorumda geciyordu; mutasyon kacti.)
- **Kapsam genisletmek tek basina yetmez.** Cevirisiz metin nobetcisi genisletildi ama hicbir
  test kizarmadi (anahtarlarin cevirisi zaten vardi) — yani geri alinsa kimse fark etmezdi.
  Kapsamin KENDISINI olcen ayri test gerekti.
- **Ajan raporunu dogrula:** bu turda benim verdigim iki sayi/tarif yanlis cikti (27 gorunum ->
  gercekte 16 oge/12 gorunum; ayrilmis aygit adlari 500 degil 400 donuyordu).

### ACIK KALAN
1. **`ClassifyDeletion`'a "olcum kesildi" ucuncu durumu** — 200.000 ustunde ayni dusme suruyor.
   Mesaj sozlesmesini degistiriyor, ayri tur.
2. **`SubscriptionCard`/`SiteHostingView` birim tutarsizligi** — kullanilan deger GiB, sinir ham
   `trafficLimitGb` + "GB" etiketi; disk satiri ham MB basiyor.
3. **Disk olcum sebep onceligi** (butce > erisim reddi) davranissal test edilmedi (gercek ACL
   manipulasyonu gerekiyor).
4. **Fatura modulu (PHP/WHMCS) calistirilarak denenmedi** — yalniz kaynak nobetcisi var.

### HALA YAPILAMAYAN: `.htaccess` canli ayrim testi
(a) 0.7.8'in kurulmasi, (b) panel `admin` PAROLASI.
Denenen ve REDDEDILEN: `AdaPanelTest2026!`, `AdaPanelTest2026!yeni`.
Olculdu: hesap KILITLI DEGIL (kilit `auth.user_locked`, IP engeli `auth.ip_banned` doner;
alinan cevap `auth.invalid_credentials`). Esik 10 deneme, 3 kullanildi.

---

## 0.7.9 (28.08.2026)

`SHA256 975F818323301FC385EB6259CC929124587851B6EDB640917E670D1263E18247`
**3758 test yesil** (onceki 3739). 22 mutasyon denendi, 22'si kizardi.

Kapatilan: silme siniflandirmasina "olculemedi" ucuncu durumu (arayuze de baglandi) ·
kota gostergelerindeki birim tutarsizligi (7 gosterim + 6 i18n etiketi) · olcum sebebi
onceliginin testi.

**Onceki turdan devralinan "test edilemez" iddiasi CURUTULDU:** sebep onceligi icin
"gercek ACL manipulasyonu gerekiyor" denmisti; o gerekce YURUYUSUN TAMAMI icin dogru,
KURALIN KENDISI icin degil. Kural `CustomerScopeService.SebepSec` adli SAF bir
fonksiyonda yasiyor -> internal yapilip dogrudan olculdu.
**DERS: "test edilemez" denen seyin hangi PARCASININ test edilemez oldugunu ayirt et.**

### ACIK KALAN

1. **Sunucudaki alan adlari yanlis birim soyluyor** (kullaniciya gorunen etiketler
   duzeldi, kodun ici DUZELMEDI):
   - `SubscriptionUsageDto.TrafficLimitGb`, `ServicePlanDto.MaxTrafficGbPerMonth` -> GiB
   - `DiskUsageMb` / `MaxDiskMb` / `AllocatedDiskMb` / `DiskUsage.UsageMb` -> MiB
   - `MailAccountDto.MaxSizeMB` / `CurrentSizeMB`, `MailDomainDto.AllocatedMB` -> MiB
   - `BillingUnits.BytesToMegabytes` / `GigabytesToMegabytes` -> MiB/GiB
     (dosyanin kendi yorumu "1 MB = 1048576 bayt" diyerek yanlisi zaten kabul ediyor)
   Yeniden adlandirmak JSON alan adlarini degistirir -> panel + WHMCS modulu birlikte
   kirilir. AYRI TUR, tek seferde ve tam yapilmali.
2. **Fatura modulu (PHP/WHMCS) calistirilarak denenmedi** — yalniz kaynak nobetcisi var.
3. **200.000+ ogelik gercek agacta uctan uca silme denenmedi** — ucuncu durum kucuk
   butceyle gercek dosyalar uzerinde uretildi, uretim olceginde olculmedi.
4. **Kart mount testi yok:** depoda `@vue/test-utils` YOK, bileseni monte eden test
   yazilamiyor. Birim mantigi `bytes.test.ts` ile dolayli kilitlendi.

### HALA YAPILAMAYAN: `.htaccess` canli ayrim testi
(a) 0.7.9'un kurulmasi, (b) panel `admin` PAROLASI.
Denenen ve REDDEDILEN: `AdaPanelTest2026!`, `AdaPanelTest2026!yeni`.
Olculdu: hesap KILITLI DEGIL (`auth.user_locked` / `auth.ip_banned` gelmedi,
`auth.invalid_credentials` geldi). Esik 10, kullanilan 3.

---

## NOT — canli sunucuda bakilacak alan adi degisti (29.08.2026)

Bu belgede `alioral.xyz` gecen yerler GECMIS KAYDIDIR (0.7.0-0.7.2'de yanlislikla ".htaccess
calisiyor" dedigim olayin anlatimi). **`alioral.xyz` ARTIK AKTIF DEGIL** — kullanici bildirdi.

Canli sunucuda bakilacak alan adi: **`adapanel.host`**.
Olculdu (29.08.2026): `adapanel.host` -> 185.7.242.225, HTTPS 200, sertifika gecerli
(`ssl_verify_result=0`), `X-Powered-By: ASP.NET`.

`.htaccess` ayrim testi bundan ETKILENMEZ: test `test.local` uzerinde, BU makinede yapiliyor.

---

## `.htaccess` CANLI AYRIM TESTI YAPILDI VE GECTI (29.08.2026, 0.7.9)

Kurulu 0.7.9 uzerinde, panelin KENDI uclariyla, ucten uca:

| Adim | Yontem | Olcum |
|---|---|---|
| Sunucu geneli anahtar `off` -> `enforce` | `POST ~/api/v1/htaccess/server-mode` (0.7.5'te eklendi) | mode=enforce, dosya olustu |
| Site kipi -> `enforce` | `POST /sites/test.local/htaccess/mode` | mode=enforce |
| `.htaccess` olusturma | `PUT /files` + `failIfExists` (0.7.5'te eklendi) | 75 bayt, icerik dogru |
| A) dosya YERINDE | `curl -H "Host: test.local" http://127.0.0.1:8081/adapanel-test-42` | **302 -> http://test.local/iisstart.htm** |
| B) `.htaccess.kapali` olarak yeniden adlandir | `POST /files/move` (0.7.5'te eklendi) | **404** |
| C) geri ac | `POST /files/move` | **302** yine |

B ADIMI KANIT: dosya kapatilinca adres BOZULUYOR. 302'yi ureten baska bir sey degil.
0.7.0-0.7.2'de "calisiyor" derken bu adimi atlamistim.

**Yan kazanc:** 0.7.5'te eklenen UC ozellik de ayni testte canli dogrulandi — sunucu geneli
anahtar ucu, panelden dosya olusturma, panelden yeniden adlandirma.

### OLCUM HATASI — kayda geciyor cunku neredeyse yanlis sonuc bildirecektim

Ilk turda `http://127.0.0.1/` + `Host: test.local` ile olctum ve **404** aldim; "ozellik
calismiyor" sonucuna varmak uzereydim. Yanlisti: `test.local` **8081** portuna bagli
(`bindings:http/*:8081:test.local`), 80 portunda ise `Default Web Site` yakalayici
(`http/*:80:`) duruyor. Yani isteklerim hic test.local'e gitmiyordu; 404 de 200 de
BASKA SITEDEN geliyordu.

Farki yakalatan sey: `appcmd list wp` ciktisinda `AdaPanel.test.local` icin isci sureci YOKTU
ama istekler yanitlaniyordu. Bu celiski olculmeseydi yanlis bir "bozuk" raporu yazacaktim.

**DERS: Host basligi dogru olsa bile PORT yanlissa istek baska siteye gider. Site bagininin
portunu ONCE olc.**

### ACIK KALAN GERCEK HATA (bu testte bulundu, duzeltiliyor)

Panel `engineLoadedInIis: True` diyor ama BILMIYOR: bu alan yalnizca `support == "supported"`
den turetiliyor, o da yalnizca havuzun CLR SURUMUNE bakiyor. Modulun IIS'e kayitli olup
olmadigi HIC olculmuyor. Bu makinede kayit dogruydu (`preCondition="runtimeVersionv4.0"`,
`managedHandler` YOK -- dogrusu bu), ama kayit bozuk olsa panel yine "yuklu" derdi.

---

## 0.8.2 — HIC CALISMAYAN IKI OZELLIK (30.08.2026)

**3922 test yesil.** Yayin: 0.8.2.

### SINIF: "yesil test, olu ozellik"
Panel dis bir sisteme (IIS, Windows) bir AD ya da YOL uretip veriyor, o sistemin
aynisini bekledigini VARSAYIYOR. Cagri basarili donuyor. Iki taraf farkli seyden
bahsediyor. Bu sinifin uc ornegi artik kayitli: .htaccess modulu pakete hic
girmemisti (0.7.0-0.7.2), FTP ev klasoru (asagida), site yazma izni (asagida).

### FTP -- kok neden
Musteri sunucusunda HICBIR hesap giris yapamiyordu: 530 home directory
inaccessible. Panelin kontrol ettigi her sey dogruydu. Cevabi IIS GUNLUGU verdi:
sc-win32-status = 3 (ERROR_PATH_NOT_FOUND) -- "yasak" degil "bulamadim", yani
ugrastigimiz izinler bastan yanlis adresti.
Microsoft <userIsolation> belgesi IKI hiyerarsi tanimlar: yerel hesaplar
%FtpRoot%\LocalUser\%User%, ETKI ALANI hesaplari %FtpRoot%\%Domain%\%User%.
Panel hep birincisini kuruyordu; o sunucuda IIS yerel hesabi MAKINE ADIYLA
niteleyip ikincisini ariyordu. KANIT: makine adli yola ayni junction elle
kurulunca giris aninda 230 oldu.
COZUM: FtpService.HomeLinkPaths -- kurulum da silme de IKI yolu birden isler.
Nobetci FtpHomeProbeTests (6 test), mutasyonla dogrulandi.
AYRICA: FtpService.ProbeHome eklendi -- panel ev klasorunu artik DISKTEN olcuyor
(HomePath bir KAYITTI, olcum degil). Bosa dusmus junction AYRI olculur: site
silininde baglanti geride kalir ve Directory.Exists true doner.

### SITE YAZMA IZNI -- ayni sinif, daha genis etki
SiteProvisioningService havuz adini "site.Name" diye uretiyordu; IIS'teki gercek
ad "AdaPanel." + temizlenmis ad. Panelden gelen HER create isteginde
ApplicationPoolName bos oldugu icin icacls hep yanlis hesabi deniyor, hata 1332,
HICBIR IZIN UYGULANMIYORDU. Laravel storage/, WP uploads yazilamiyordu.
BELIRTIYI MASKELEYEN: "Izinleri onar" ayni metodu DOGRU adla cagiriyordu -- yani
"onarinca duzeliyor, yeni sitede hic calismiyor" gibi gorunuyordu.

### .htaccess veri koku sozlesmesi
ADAPANEL_DATA_ROOT degiskenini "kurulum yazar" diye yorum vardi; tarandi, YAZAN
HICBIR SEY YOK. Varsayilan kurulumda iki taraf ayni koke dustugu icin gorunmuyor.
Panel:DataRoot degisirse panel yeni koke yazar, modul varsayilana bakar -> panel
"acik" der, HICBIR KURAL uygulanmaz. EngineLoadedInIis artik DORT olcumun
birlesimi; ayrisma htaccess.blocker.data_root_mismatch olarak bildiriliyor.

### Kaldirma sizintisi
install-tray-task.ps1 gorevi KOKTE kuruyor; uninstall-cleanup.ps1 listesinde
yoktu ve \AdaPanel\ supurmesi koku taramiyor. Kaldirma sonrasi gorev kaliyor,
silinmis exe'yi calistirmayi deniyor.

### OLCULDU-SAGLAM (elenen hipotezler -- bunlari tekrar arama)
Teslim yolu TEMIZ: .iss'teki 8 betigin hepsi [Files]'ta, kurulu makinede
{app}\scripts sekizini de tasiyor, HtaccessModule.dll GAC'ta + applicationHost'ta
kayitli · modul surum sapmasi yok (AssemblyVersion bilerek 1.0.0.0) ·
harden-adapanel.ps1 cagrilmiyor ama bu KUSUR DEGIL, bilincli manuel admin araci ·
scripts/lib pakete girmeyecek.

### GUVENILMEZ NOBETCI ONARILDI
MemcachedRuntimeTruthTests gercek 11211 portunu paylasiyordu; sonuc koda degil
ORTAMA bagliydi (tam kosumda kirmizi, tek basina yesil). Sonda portu artik
disaridan verilebiliyor, testler bos port aliyor. KANIT: 11211 kasten isgal
edilip kosuldu, 5/5 yesil. Uretim varsayilani ayrica sabitlendi.

### ACIK KALANLARIN UCU DE KAPANDI (0.8.2 sonrasi)

1. [KAPANDI] WafService havuz adini SANITIZE ETMEDEN uretiyordu. Kok neden: onek
   IKI dosyada ayri yaziliydi ve temizleme regex'i yalnizca birinde vardi -- yani
   "ayni sozlesme" diyen yorum yanlisti. ManagedAppPoolName tek kaynak oldu;
   WafService ve WebAdministrationSiteService ayni Build()'i cagiriyor.
   Nobetci ManagedAppPoolNameTests (10 test). Yasak, dizgenin KENDISINE degil
   hatanin SEKLINE kondu: ilk hali "AdaPanel." dizgesini ariyordu ve HAKLI olarak
   kirmizi yandi -- ayni dosyada InternalSiteNamePrefix diye AYRI bir sabit var,
   o havuz degil panelin kendi SITE adlarini uretiyor. Metni yasaklamak mesru
   kullanimi da vururdu. Mutasyonla dogrulandi.

2. [KAPANDI] SchedulerService kayittan sonra geri okuyor ama bulamayinca uydurma
   DTO donuyordu (State="Ready"). Artik cron.write_unverified atiyor.
   "Kaydedilemedi" DEMIYOR, cunku bilmiyoruz -- komut basarili donmustu; soylenen
   sey olculen sey: kayit DOGRULANAMADI.

3. [KAPANDI] WinAcmeSslService HTTPS yonlendirmesi: $ErrorActionPreference='Stop'
   + yazdigini GERI OKUMA eklendi. IKINCI BIR HATA da bulundu ve kapatildi:
   IsHttpsRedirectEnabledAsync yalnizca o ADDA kural var mi diye bakiyordu, yani
   YARIM yazilmis bir kural (ad eklenmis, action yazilmamis) "yonlendirme acik"
   diye raporlaniyordu. Yazma ve okuma yollari birbirinin yalanini dogruluyordu;
   ikisi de artik action/type='Redirect' sartini olcuyor.

4. [ACIK] Firewall'da PhpMyAdmin kurali IKI KEZ duruyor (netsh ayni adla
   cogaltip 0 donduruyor); harden-adapanel.ps1 B2 temizliyor, kok neden acik.

### NOBETCI OLCUMU HAKKINDA BIR DERS (0.8.2)
WriteThenVerifyContractTests'in ilk hali BOS BIR GUVENCEYDI:
Assert.Contains("$ErrorActionPreference = 'Stop'") yaziyordu ama o satirdan ayni
dosyada ZATEN BES TANE vardi (baska betiklerde). Duzeltme geri alinsa test yesil
kalirdi. MUTASYON YAKALADI: koruma kasten bozuldu ve test KIRMIZI YANMADI.
Denetim tam diziliye (uc satirin sirasi) daraltildi, sonra mutasyon kirmizi yandi.
DERS: "test yazdim" yetmez, testin BIR SEY OLCTUGU kanitlanmali.

---

# OLCULDU, HENUZ KAPATILMADI (30.08.2026 aksami)

Iki denetim ajani kota limitine takildi ama ALT OLCUMLERI tamamlandi. Asagidakiler
DOSYA:SATIR ile olculmus bulgulardir; yeniden kesfetmeye gerek yok.

## SSRF -- kullanicidan gelen hedef adreslerde host denetimi YOK
(Bir ajan su an bunlari kapatiyor; bitince bu bolum guncellenecek.)
* BackupService.ResolveTarget (BackupService.cs:4647-4747) <- request.RemoteUrl.
  Yalniz tur/mutlak URI/sema denetleniyor. Backup/ altinda
  IsLoopback|127\.0\.0|169\.254|GetHostAddresses aramasi SIFIR sonuc.
  targetType=http + http://127.0.0.1:9200/... -> ic servislere PUT/DELETE/HEAD.
  webdav ayrica PROPFIND + MKCOL. "Baglantiyi test et" (:4236,:4250) verilen
  adrese HEMEN dosya yukluyor.
* FileMigrationService.cs:43-46 yalniz "bos degil mi"; :178 (FTP), :269 (SFTP)
  request.Host + request.Port -> keyfi host:port TCP baglantisi.
* SchedulerService.cs:1206 curl.exe "<request.Url>" ve gorev SYSTEM kosuyor;
  IsAcceptableTaskUrl (:1035-1051) yalniz SEMA denetliyor.
* NotificationService.cs:94 (webhook POST) ve :182 (SmtpClient.ConnectAsync);
  ayarlar govdeden yaziliyor, yalniz .Trim().
* GitDeploymentService.ValidateRepositoryUrl (:238-289) iyi yazilmis (bastaki -,
  bosluk, :: taşiyici, sema beyaz listesi, GIT_ALLOW_PROTOCOL) ama HOST denetimi yok.

SAGLAM CIKANLAR (tekrar aranmasin): self-update UpdatePackageTrust host beyaz
listesi + depo eslestirmesi + https zorunlu · Dropbox/OneDrive SABIT adresler ·
CacheService/WebDbClientService sabit 127.0.0.1 · ClusterAddressGuard sertlestirilmis ·
RdnsService/SeoBotVerifier/WinAcmeSslService:494 yalniz DNS, baglanmiyor.

## KAYNAK TUKETIMI -- kiraci sunucuyu durdurabilir
* EN UCUZ YOL: FileService sikistirma (:914), cikarma (:951), kopyalama (:294),
  boyut hesabi (:1101). Customer'a acik (FilesController.cs:21), SIFIR eszamanlilik
  denetimi, SIFIR hiz siniri, hepsi istek is parcaciginda ve LocalSystem olarak.
  Boyut hesabi HttpPost ve hiz sinirsiz: N istek = N x 15 saniye tam hizda tarama.
* AntivirusService.cs:116 (oku) ile :127 (yaz) ARASINDA await var -> kontrol-sonra-yaz
  YARISI. N istek ayni anda "tarama yok" gorup N tane clamscan.exe baslatiyor, her biri
  2 saat zaman asimli. DOGRU KALIP DEPODA VAR: RunningJobService.cs:101-132 kontrol ve
  yazmayi AYNI JsonFileStore.UpdateAsync kilidi icinde yapiyor; JsonFileStore.WriteAsync
  (:37) kilit ALMIYOR, UpdateAsync (:80) aliyor.
* RunningJobService.cs:117 slot anahtari IS TURU, kiraci degil. Bir Customer'in uzun
  yedegi butun otekilere VE yoneticiye 409 donduruyor -- caprazi kiraci DoS.
* Hiz siniri YALNIZ login/password-change/setup/mcp uclarinda. Global sinirlayici
  cerezli ve anonim istekler icin GetNoLimiter -> ~66 kimlik dogrulamali denetleyicinin
  HICBIRINDE hiz siniri yok. Anonim posta gonderen ucta da yok
  (MailListPublicController.cs:65 -> unsubscribeService.RequestAsync = kimlik
  dogrulamasiz posta yukseltme).
* Kestrel Limits HIC yapilandirilmamis: MaxConcurrentConnections SINIRSIZ,
  RequestTimeout yok. DbToolsProxy 1 GiB govde + EnableBuffering (disk'e tasar,
  toplam sinir yok) + 30 dakika timeout.
* UseForwardedHeaders HIC yapilandirilmamis. Dogrudan Kestrel'de GUVENLI (sahte baslik
  guvenilmiyor) ama panel bir ters vekil arkasina konursa butun istemciler tek bolume
  duser: 5 giris/dk BUTUN internet icin, ve gizli yol IP beyaz listesi anlamsizlasir.

## BELLEGE TAM OKUMA (sinirsiz)
* ProcessRunner.cs:62-63 stdout/stderr ReadToEndAsync -- SINIR YOK, tek sinir 10 dk.
  Deponun EN COK kullanilan ilkeli; kiraciya ulasilabilir (FileService.cs:876
  icacls /T kiracinin kendi agaci uzerinde, cikti kiracinin dosya sayisiyla buyuyor).
* BackupService.cs:2251 gorev XML'i File.ReadAllText + Unicode.GetBytes +
  ToBase64String (~2.67x) ve bu bir DONGUDE, kaynak saldirgan arsivinden.
* DbToolsProxyController.cs:130 16 MB siniri "contentLength is null or <= 16MB" --
  Content-Length OLMAYAN (chunked) yanit siniri TAMAMEN atlıyor.
* NginxInstallationSupport.cs:462 ve WebmailService.cs:346: DONMEYEN error.log'u
  TAMAMEN belege okuyup son 4 KB / 1.5 KB'sini aliyor.
* Kiraci yazabildigi dosyalari sinirsiz okuyan yollar: DirectoryProtectionService.cs:382
  (her korumali dizin icin bir web.config, TEK istekte N dosya),
  WordPressToolkitService.cs:1456 (wp-config.php + tum dize uzerinde Regex, HER WP
  panosu yuklenisinde).
SAGLAM CIKANLAR: LogViewerService.ReadTailLines 2 MB pencereyle SEEK ediyor (1 GB
gunluk 2 MB'a mal oluyor) · W3cLogReader satir satir akiyor · SeoCrawler MaxResponseBytes ·
HtaccessFileSystem.TryReadText maxBytes parametreli · Branding yukleme cift katmanli.

---

# DEVAM NOKTASI (29.08.2026 20:40 -- kota kesintisi icin yazildi)

## ONCE BUNU CALISTIR, IS YAPMAYA BASLAMA
    powershell -File scripts\devam-durumu.ps1

Betik agacin SAGLAM mi YARIM mi oldugunu OLCER ve ne yapman gerektigini soyler.
Kendiliginden hicbir sey silmez. Mantigi: bu depoda her commit YESIL bir durumdur,
yani commit'lenmemis her sey TANIMI GEREGI ucus halindedir. "YARIM" derse iki
secenek vardir, ucuncusu yoktur: (a) dosyayi tamamla, (b) `git checkout -- <dosya>`
ile son yesil commit'e don.

NOT: betigin ilk surumu `2>&1` kullandigi icin SAGLAM bir agaci "YARIM" diye
raporluyordu (PowerShell 5.1 yerel komut stderr'ini NativeCommandError'a sarar ve
cikis kodu 0 olsa bile hata uretir). Duzeltildi; bu betikte `2>&1` YOKTUR, karar
yalniz $LASTEXITCODE'a bakar. Ayni tuzaga tekrar dusme.

## SON YESIL NOKTA
5b2aeff  Kalan 11 ekranin tasarim turu: olculmus yalanlar kapatildi
Bu commit'te: 3895 test yesil, i18n TR/EN 5006 = 5006, typecheck+build temiz.
Yayinlanan son surum 0.8.1 (SHA256 6FDF19399BA6B1B915EA832CFB0083649D8A9E0B10451A0C71517B09B7F6BFF3).
Commit'lenmemis ne varsa 5b2aeff'ten SONRA gelen ucus halindeki istir.

## SIRADAKI ISLER (oncelik sirasiyla)

1. [KAPANDI] MIGRATIONVIEW -- iki bulgu da kapatildi:
   (a) "Dosyalari cek" ve "Veritabanini aktar" artik GERCEK `h3`. Onay kutusu basligin
       ICINDE kaldi (kutunun basligi ayni zamanda o kaynagi acan anahtar -- bilincli
       tasarim). Yerel `.section-toggle strong { font-size: --fs-h4 }` SILINDI: o kural
       bu iki basligi 14.3px'te tutuyordu, kardes bolum basliklari ise 15.9px -- ayni
       rutbe iki ayri boyda ciziliyordu. Boy artik merdivenden geliyor.
   (b) DTO AYRILDI (onerilen dogru duzeltme yapildi, borc birakilmadi):
       `PleskDomainDto.HostingType` (yalniz Plesk) + `.AccountUser` (yalniz cPanel).
       `SiteMigrationService` cPanel dali artik `AccountUser` yaziyor. Arayuzde alan
       ETIKETLI ("Barindirma turu: virtual" / "Hesap kullanicisi: ali_shop") ve uzak yol
       sablonundaki `{user}` `accountUser`'dan doluyor -- eskiden Plesk'te "virtual"
       degerini `{user}` yerine gecirip `/home/virtual/...` uretebiliyordu.
       Nobetci: `DiscoveryFieldMeaningTests` (4 test, mutasyonla dogrulandi).

2. [KAPANDI] SERVERHEALTHVIEW artiklari:
   (a) `dns.details` `<details>` icine KATLANDI -- ama yalniz MAKINE CIKTISI oldugunda.
       Alan iki ayri sey tasiyor: olcum tuttugunda "service=...; startMode=..." (katlanir),
       tutmadiginda `dns.health_unmeasurable` ANAHTARI (cevrilir ve ACIKTA kalir, cunku o
       kartin cevabidir). Ayrimi panelin ortak `looksLikeMessageKey` sozlugu yapiyor.
   (b) `dns.startMode` artik cevriliyor: Win32_Service'in GERCEK deger kumesi
       (Boot/System/Auto/Automatic/Manual/Disabled/Unknown). BILINMEYEN deger anahtar
       SANILMIYOR, oldugu gibi geciyor -- ham anahtar basmaktansa ham deger dogrudur.
   (c) Arka uc anahtar donusunu BASKA BIR AJAN yapmisti (`dns.watchdog.*`, ceviriler dahil)
       -- olculdu, dogruydu, DOKUNULMADI. Kalan yari renkti: basarisizlik hala NOTR
       yaziliyordu. Dizge/anahtar koklamak yamalama olurdu (sunucu anahtari degistirdigi gun
       sessizce bozulur), bu yuzden sunucu artik AYRI bir alanla soyluyor:
       `DnsServerHealthDto.LastAutoRepairSucceeded` (bool?, `null` = OLCULMEDI, eski nobetci
       kaydi yanlis renge boyanmaz). Nobetci: `DnsWatchdogSeverityTests` (4 test, mutasyonla
       dogrulandi) -- durum METNI yazan her dal SIDDETINI de yazmak zorunda.
   (d) DNS kurulu degilken kart artik GORUNUYOR, ucuncu bir durumda: `idle` (ne kirmizi ne
       yesil), notr rozet "Kurulu degil" + rolun neden kurulu olmayabilecegini soyleyen bir
       cumle. Olcum satirlari (dinlenen/beklenen adres, bolge sayisi) gizleniyor: hepsi
       bos/sifir olurdu ve "olculemedi" ile "olculdu, bos cikti" ayni gorunurdu.
       ONCE/SONRA OLCULDU: bu gelistirme makinesinde DNS servisi de DnsServer modulu de YOK,
       yani eski kosul kartı burada TAMAMEN gizliyordu.

3. TAM TASARIM TURU GORMEYEN, KALAN 4 EKRAN:
   AppStoreView · ProxyView · SiteDetailView · WebsiteWorkspaceView
   (Son ucune 0.8.1'de kismen dokunuldu: ikon dili, h4 basligi, "Siteyi ac"
   tekrari. Tam tur gormediler.)

4. FIREWALLVIEW `form.port` varsayilani 80 (korumali web portu). SecurityView ayni
   riski olcup varsayilani 0'a cekmisti. Onay diyalogu `requireText` ile korudugu
   icin degistirilmedi; karar acik.

5. IZLEME ekranlarinin DUZEN/HIZALAMA turu yapilmadi (ajan canli olcum yapamadi:
   kendi imzali sertifikanin uyari sayfasi otomasyonla gecilemedi). Icerik hatalari
   kapandi, duzen turu acik.

6. SUNUCUDAN GELEN SABIT TURKCE METIN -- AYIKLANDI, UC BOLUM KAPANDI, BUYUK BOLUM ACIK.
   AYIKLAMA (yorumlar elenerek yeniden olculdu: 783 dizge / 93 dosya). Ham sayinin cogu
   kullaniciya HIC ULASMIYOR: 183 yalniz `ILogger` satiri, 81 konsol kurtarma araci +
   tepsi penceresi (KAPSAM DISI, kendi metin tablolari var), ~24 MCP arac aciklamasi
   (yapay zeka istemcisine gider, panelin i18n'ine bagli degil), ~30 SiteBuilder/
   abonelikten-cikma sayfasi (MUSTERININ SITESI, panel arayuzu degil), 14 gelistirici
   istisnasi. GERCEKTEN EKRANA ULASAN: ~330 dizge.
   KAPANANLAR (53 dizge):
   (a) Uygulama katalogu (31) -- `AppStoreService` `Description`/`AutomaticSetup`/
       `Requirements` artik ANAHTAR tasiyor, `AppStoreView` `serverText` ile cevirir.
       Arama da cevrilmis metinde yapiliyor (yoksa kullanici EKRANDA GORDUGU kelimeyi
       aratamazdi). Urun/dosya adlari (SQLite, PHP 8.3+, Composer) BILEREK ham birakildi.
       Nobetci: `AppStoreCatalogLocalizationTests` (3 test, mutasyonla dogrulandi).
   (b) DNS nobetci durumu + saglik ayrintisi (4) -- `dns.watchdog.*`, `dns.health_unmeasurable`.
   (c) Denetim kaydi AYRINTI satiri (18) -- `AuditLogsView` eylem ADINI cevirir ama
       ayrintiyi HAM basar; oraya yazilan Turkce cumle Ingilizce panelde de gorunuyordu.
       Cozum cevirmek DEGIL, tekrari atmak: eylem etiketi zaten ayni seyi soyluyordu,
       geriye dilden bagimsiz OLCUM kaldi (`zone=...; mode=secondary`). Bu arada
       `panel.activity.action.dns.zone_switched_to_primary` cevirisinin HIC OLMADIGI
       bulundu (ekranda ham kimlik goruluyordu) ve eklendi.
       Nobetci: `AuditDetailLanguageTests` (mutasyonla dogrulandi; ilk surumu yanlis
       pozitif verdi -- denetim cagrisinin ICINDEKI yorumlar elenmeden taraniyordu).
   TEK KAYNAK: `useNotice.useServerText` -- `SiteRepairNotice`'in elle yazilmis kopyasi
   silinip oraya bagli. Kural yeni degil, `utils/apiError`'in kuralinin ayni.
   ACIK KALANLAR (sayilariyla, oncelik sirasiyla):
   - `.htaccess` teshis metinleri ~200 dizge / 9 dosya (`HtaccessDirectiveCatalog` 76,
     `HtaccessConfigurationBuilder` 36, `HtaccessRewriteModel` 24, `HtaccessParser` 15,
     `HtpasswdFile` 14, `HtaccessAccessControl` 13, `HtaccessRewriteRunner` 10, digerleri).
     `HtaccessDiagnostic.message` -> `SiteHtaccessView`, HAM basiliyor. EN BUYUK KALAN
     BLOK. Yarim birakmak yanlis olurdu: 200 Apache-ozel cumle tek turda cevrilmeli.
   - `SiteCascadeDeletionService` ~36 (site silme raporu: adim etiketleri, uyarilar,
     "KISMEN SILINDI" satirlari) + `UserManagementService` ~8 ayni sinif (artik/leftover
     gerekceleri). Cogu PARAMETRELI cumle: bileşik "anahtar — ayrinti" bicimi yetmez,
     yer tutuculu anahtar gerekir.
   - `SiteHealthService` 16 (site sagligi satirlari, agir interpolasyonlu).
   - `WebmailService` 17, `MailService` 6, `BackupService` 6, `PhpService` 3, kalanlar tekil.
   - `UserManagementService` hos geldin E-POSTASI (2 dizge): panel ekrani degil, alicinin
     dili de bilinmiyor -- ayri bir karar gerektiriyor, kapsama alinmadi.
   - `AppTemplateDto.DefaultRootFolder` bes sablonda `"site koku"` (Turkce ifade, klasor
     adi degil). HICBIR YERDE OKUNMUYOR (ne arayuz ne kurulum kodu) -- olu alan; silinmesi
     mi doldurulmasi mi gerektigi ayri bir karar, dokunulmadi.

## SUREN AJANLAR (kesildiyse isleri yarim kalmis olabilir)
- WHMCS/PHP modulunu GERCEKTEN calistirma (integrations/whmcs/ altinda yeni test
  dosyalari acti: fake-panel.php, module-contract.test.php, whmcs-stub.php).
- MigrationView + ServerHealthView artiklari (madde 1 ve 2).
- Kalan 4 ekranin tasarim turu (madde 3).

---

## 0.8.1 — yalan soyleyen ekranlar (29.08.2026)

**3883 test yesil**, 25 arayuz nobetcisi yesil (yeni: SiteToolIconTests).

### Kapanan yalanlar
Basarisiz zamanlanmis gorev YESIL "Hazir" rozeti tasiyordu (rozet Windows gorev durumuna bakiyor,
son calistirma SONUCUNA bakmiyordu) · yuklenirken "0 gorev" yaziyordu · Danisman "0 kritik" satirini
KIRMIZI yaziyordu · Faturalama karti basligi durum soyluyordu, rozet "Reddedildi" (neyin?) ·
`attention.domains.staleDefaults` basligi tam cumleydi ve `.desc` onu tekrarliyordu (C# tarafi
DOGRUYDU, hata tek sozluk degerindeydi) · ham `mail.not_configured` anahtari ekrandaydi (Turkcesi
sozlukte VARDI, eksik olan `t()` cagrisiydi).

### Olculen kok nedenler
Baslik merdiveni `.panel` sarmalayicisina bagliydi, o sarmalayici 14 bilesende YOKTU -> dikkat
cekmecesinin `h2`si 13.77px (govde 13.5px) · sol menu listesi kaydirmali, acilista `scrollTop`
hep 0 -> secili "Sunucular" maddesinin 40px'inin 16.2px'i gorunuyordu (yapiskan kutu BINMIYORDU;
"alt bosluk ekle" yanlis cozum olurdu) · ust cubuk izgara ogesiydi, yapisma alani kendi satiriyla
sinirli -> 560px kaydirmada `top: -548px` · Yedeklemede `showCreateForm` bagimsizdi, liste
seciliyken form da ciziliyordu -> ayni ekranda IKI sekme seridi.

### Yazi olcegi tabani
`--fs-2xs` = 10.53px ilan edildi; 20 dosyada 38 bildirim tabana cekildi (en kucugu 8.1px idi).
Olculdu: koyu + acik temada, 15 bolumde 10.5px alti metin 0.

### Ikon dili
Site araclarindaki uydurulmus damgalar (PX/KOR/IST/GOC/FL/BK — bes ayri kisaltma dili + dort yazi
tipi simgesi) kaldirildi; 21 arac + 4 ust sekme `utils/siteToolIcons.ts` icindeki 23 cizime
tasindi. `ToolDef.glyph` alani TIPTEN CIKARILDI ki yeni arac eklenirken kimse ikonu degil kisaltmayi
doldurmasin. Nobetci: `SiteToolIconTests` — ikonsuz arac ve olu ikon kaydi kirmizi yanar.

### Ad birligi
"Kayitlar"/"Audit kayitlari"/"Loglar" ayrildi: **Denetim Kayitlari** (kim ne yapti) ve **Gunlukler**
(log dosyalari). Posta modulunun uc adi tek ada indi. Sunucu olcumleri **Izleme** oldu —
"Istatistikler" SECILMEDI, cunku o ad zaten site ziyaret istatistiklerinindi.

### YAPILMAYAN (bilincli)
Bagiran buyuk harfler: 167 anahtarda 240 gecis OLCULDU, yalnizca denetimde isaret edilen 5 nokta
duzeltildi. Kalanin cogu yikici-islem uyarisi ("PAROLANIZI KAYBEDERSENIZ ... GERI GETIRILEMEZ");
toplu duzeltme o uyarilari zayiflatirdi.
`ProxyView` disindaki ekranlarin bir kismi hala "tam tasarim turu gormedi" listesinde (asagi bkz).

---

## 0.8.0 — panel gorsel elden gecirme (29.08.2026)

`SHA256 84E6FDDC1209825CD0D3B73BD579F3C83050D7408654F3690E0DDBFE22C85314`
**3881 test yesil**, 21 arayuz nobetcisi yesil.

### Kok neden (kullanici: "birbirine girdi her sey")
Vue'da `<style scoped>` bilesenler arasi GECMEZ ama paylasilan siniflar oyle kullaniliyordu:
`.wide` 8 dosyada kullanilip HIC tanimli degildi · `.section-head` 48 kopyaydi, 8 dosyada yoktu ·
`.ghost`/`.link-button` tanimsizdi (sessizce BIRINCIL mavi dugme). Ortak siniflar base.css'e
alindi, kopyalar silindi, nobetci eklendi (>=2 bilesende kullanilan sinif tanimli olmali).
Baslik merdiveni DUZDU (govde 13.5 / h4 13.5 / h3 13.5 / h2 15.9) -> 13.5/14.3/15.9/19.2.

### Canli olculen gorsel hatalar (hepsi kapandi)
Posta silme dugmelerinin YAZISI acik temada 1.09 kontrast · pano halkalarinin rengi SABITTI
(disk hep kehribar, %98 CPU sakin mavi) · DNS'te 40 sahte yesil "iyi" rozeti · Yazilim
ekraninda 1120px altinda TABLO BASLIKLARI siliniyordu · yedek uyari kutusu ALTTAKI satirin
basligi gibi okunuyordu · menude 10 bolum ikonsuzdu.

### TAM TASARIM TURU GORMEYEN EKRANLAR (kota nedeniyle ajanlar yarida durduruldu)
UsersView · WebsiteWorkspaceView · SiteDetailView · SiteBackupView · AppToolkitView ·
SiteBuilderView · MonitoringView · SecurityView · MigrationView · AppsView · AppStoreView ·
ServerHealthView · ClusterView · FirewallView · ProxyView
NOT: hepsi derleniyor, 21 nobetciden geciyor, sabit renk tasimiyor ve YENI base.css'i
(bosluk olcegi, baslik merdiveni, odak halkasi, kontrast duzeltmeleri, ortak siniflar)
devraldi. Yani bozuk degiller; yalniz kendilerine ozgu bir duzen turu gormediler.

### ACIK KALAN (onceki turlardan)
1. [KAPANDI 0.8.2] Birim borcu OLCULDU ve dogru sekilde kapatildi. Bulgu: hesap DOGRUYDU --
   bes cevrim noktasinin besi de 1024 tabaninda ve arayuz (frontend/src/utils/bytes.ts) zaten
   ekrana durustce "GiB"/"MiB" yaziyor. Yani ortada hesap hatasi yoktu; TABAN BES AYRI YERDE
   ELLE yaziliydi ve birini 1000'e ceviren duzenleme HICBIR YERDE kirmizi yakmazdi. Sonucu
   sessiz olurdu: ekran %80 dolu gosterirken yaptirim ~%7 sapardi ve TRAFIKTE yaptirim ASKIYA
   ALMAK oldugu icin musterinin siteleri beklenmedik anda kapanirdi.
   YAPILAN: AdaPanel.Core.Common.PanelUnits tek kaynak oldu; CustomerScopeService,
   SubscriptionEnforcementService, FileQuotaGuard (2 yer) ve BillingUnits oradan besleniyor.
   Nobetci: PanelUnitsTests -- (a) taban ikilik mi (ayirt edici olcum: 2.000.000 bayt ikilikte
   1 MiB, ondalikta 2 MB), (b) faturalama ayni tabandan mi, (c) kota dosyalarinda HAM carpan
   kaldi mi. Mutasyonla dogrulandi: taban 1000'e cevrilince 5 test kirmizi (FileQuotaTests ve
   BillingIntegrationTests dahil -- zincirin gercekten aktigini kanitlar); ham carpan geri
   konunca DEGER AYNI KALDIGI HALDE tek-kaynak nobetcisi kirmizi.
   ALAN ADLARI BILEREK DEGISTIRILMEDI, gerekcesi uc madde halinde PanelUnits'te yazili:
   (1) JSON sozlesmesi degisirse panel + WHMCS modulu AYNI ANDA kirilir, modulun testi yok,
   (2) WHMCS dokumantasyonu da barindirma kotalari icin 1 GB = 1024 MB kabul eder,
   (3) Plesk/cPanel dahil butun sektor ikilik sayip etikete "GB" yazar.
2. [KAPANDI] Fatura modulu (PHP/WHMCS) artik GERCEKTEN calistiriliyor.
   PHP bulundu: panelin barindirma icin kurdugu surum (`%ProgramData%\AdaPanel\runtimes\php\8.3`,
   8.3.33). Kaynak paketin SHA-256'si components.json'daki karma ile BIREBIR ayni, `php.exe`
   zip icindekiyle ayni ve `CompanyName = "The PHP Group"` (makinedeki PE infector yuzunden
   zorunlu kontrol).
   OLCUM: `php -l` -> 3 dosya, 0 hata (bu tek satir bile bugune kadar kosmamisti).
   YENI NOBETCI: `integrations/whmcs/tests/module-contract.test.php` -- `AdaPanelClient`
   MOCK'LANMIYOR; `php -S` ile sahte bir panel ayaga kalkiyor, modul ona gercek curl istegi
   atiyor ve test yol/metot/baslik/alan adlarina bakiyor. 15 bolum, 135 denetim
   (leftover-note ile birlikte PHP tarafinda toplam 162).
   Sozlesme C# denetleyicileriyle karsilastirildi: uc/metot/alan adlarinda uyusmazlik YOK.
   BIRIM: PHP tarafi hicbir cevrim yapmiyor, MB degeri aynen geciyor -- yani panelin 1024
   tabaniyla ayrisma YOK; bu artik testle kilitli.
   BULUNAN VE DUZELTILEN IKI GERCEK HATA:
   (a) `TestConnection` YANLIS SEYI olcuyordu. Denedigi iki uc de Administrator VE Reseller'a
       acik; asil yonetici kapisi `BillingController` (Administrator-only) hic denenmiyordu.
       Bayi hesabiyla kurulan sunucu tanimi "baglanti basarili" diyor, sonra her iptal 403
       aliyor ve `UsageUpdate` SESSIZCE hic guncellemiyordu (WHMCS donusu gostermez). Ayni uc
       faturalama anahtari kapaliyken de 403 doner. Artik `GET /api/v1/billing/settings`
       deneniyor; 403 (rol) ile `enabled:false` (anahtar kapali) AYRI mesajlar.
   (b) Panel API anahtari yapilandirilmisken `ChangePassword` ve `ServiceSingleSignOn` OLUYDU.
       `AuthController` cerez semasina sabitli, anahtarli istek 401 aliyor; kisit dosya bas
       yorumunda YAZILIYDI ama koda hic gecmemisti ve modulun mesaji yoneticiyi saglam bir
       anahtari yenilemeye gonderiyordu. Artik `/api/v1/auth/**` cagrilarinda anahtar bir
       kenara birakilip cerezli oturum aciliyor (kural cagri yerinde degil `authorized()`
       icinde tek yerde).
   Dordu de MUTASYONLA dogrulandi (her duzeltme tek tek bozuldu, ilgili denetimler kirmizi
   yandi, geri alindi).
   KOSUM: `powershell -ExecutionPolicy Bypass -File scripts\run-whmcs-tests.ps1` -- PHP'yi arar,
   `php -l` + iki davranis testini kosar; PHP bulamazsa SESSIZCE YESIL GECMEZ, cikis kodu 2.
   HALA OLCULMEYEN: gercek bir WHMCS surumunun bu islevleri bekledigimiz parametrelerle
   cagirdigi (bu depoda WHMCS+MySQL yigini yok).
3. ~~Panel icerigine okunabilir bir max-width karari~~ -- **KAPANDI.**
   OLCULDU (canli panel, yayinlanan derleme, `html` 2560px'e zorlanarak): `.main-column` ve
   `.content-column` hicbir ust sinir tasimiyordu (yayinlanan CSS'te de dogrulandi). 2560px'de
   icerik sutunu 2306px, uc sutunlu formun her alani 738px, bes sutunlu tablo satirinda satirin
   ADI ile kendi islem dugmesi arasi 2184px, aciklama paragrafi satir basina 310 KARAKTER.
   KARAR -- iki kademe, ture gore (belirtecler `styles/base.css`, tek kaynak):
   `--content-max: 1600px` tablo/izgara/grafigin dis siniri, `--reading-max: 72ch` yalniz okuma
   metni. 1600'un alt sinirini TABLOLAR belirledi: sutun genisligi supurulerek olculdu, tablolar
   1030px'e kadar ic kaydirma acmiyor, kaynaktaki en dar sigan satir DnsView'in alti sutunlusu
   (762px) -- 1600'de tabloya 1530px kaliyor, yani sinir hicbir tabloya kaydirma cubugu
   TAKTIRMIYOR. `ch` secildi cunku ogenin KENDI yazi boyuna gore cozulur: `.meta` 556px,
   `.hint` 524px, `.field-hint` 485px -- ucu de ~72-78 karakter, "rahat" yogunluk kipinde
   kendiliginden buyur. Sinir `.content-column`a DEGIL `.main-column`a kondu ki ust cubuk da
   ayni genislikte kalsin. SOLA YASLI: ortalansaydi menu ile ilk panel arasinda 353px bosluk
   olurdu. SONUC (2560px'de, gercek derleme): ad->dugme 2184 -> 1478px, form alani 738 -> 503px,
   paragraf 310 -> 78 karakter; kirpilan oge 0, kaydirma acan tablo 0, yatay sayfa kaymasi yok.
   Nobetci: `CssContentWidthTests` (6 test) -- belirtecler yalniz base.css'te bir kez, sinir
   yalniz kabukta ve belirtece bagli, kabuk sutunlarina baska dosyadan sinir konamaz, dort
   haneli sabit `max-width` yasak, `.wide` geri gelemez. Bes korumanin BESI de mutasyonla
   kirmizi yandi ve geri alindi.
4. ~~`.wide` gercek anlam kazanacaksa~~ -- **KAPANDI: olu kalmiyor, cunku ARTIK YOK.**
   Tarandi: depoda `.wide` sinifinin ne bir kullanimi ne bir tanimi var (yayinlanan CSS'te de
   sifir); 0.8.0'daki "8 cagri, sifir tanim" bulgusu `CssSharedClassTests` eklenirken silinmis.
   DIRILTILMEDI, gerekcesi olculdu: yeni duzende sinir tek bir DIS kabukta duruyor, panel panel
   daraltma yapilmiyor -- kacilacak bir daraltma olmadigi icin sinifin yapacagi is de yok.
   Karar `CssContentWidthTests.Olu_wide_sinifi_geri_gelmemis` ile sabitlendi.
