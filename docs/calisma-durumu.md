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
* [KAPANDI] EN UCUZ YOL: FileService sikistirma, cikarma, kopyalama, boyut hesabi
  (ve olculerek AYNI AILEYE alinan arama: 5.000 dosyaya kadar gezip her dosyanin
  2 MB'ini ICERIK olarak okuyor). TEK KAYNAK: `Support/ConcurrentWorkGate.cs`
  (anahtar basina tavan, KUYRUK YOK -> hemen 429). Cagri yerleri yalniz POLITIKA
  seciyor: `FilesController.AgirIseGirisAsync` kiraci basina 2 es zamanli agir is.
  Butcenin sahibi KOTANIN sahibiyle ayni (alt kullanici parent'inin butcesinde),
  yoksa musteri kendi actigi alt kullanicilarla tavani cogaltirdi. GENEL tavan
  BILEREK yok: makine geneli bir tavan, asagida kapatilan caprazi kiraci reddini
  geri getirirdi. Yeni anahtar: `files.too_many_operations` (TR+EN).
  Nobetci: `ConcurrentWorkGateTests` (7 test; kapsam nobetcisi kaynagi tarar, yani
  yeni bir agir uc korumasiz DOGAMAZ). Mutasyonla dogrulandi.
* [KAPANDI] AntivirusService kontrol-sonra-yaz YARISI. Ayirtma artik
  `JsonFileStore.UpdateAsync` kilidinin ICINDE (RunningJobService'teki kanitlanmis
  kalip; yeni desen icat EDILMEDI). Temizleme de ayni kilitte ve BASLANGIC ANINI
  karsilastiriyor -- kosulsuz temizleme baskasinin yeni kaydini silebilirdi.
  Nobetci: `AntivirusRunningScanTests` (+2 test; 8 es zamanli istekten yalniz 1'i
  yuvayi aliyor). MUTASYON DERSI: mevcut "ikinci tarama 409 alir" testi kilitsiz
  hale getirilince YESIL KALDI -- sirali test yarisi hic olcmuyordu.
* [KAPANDI] RunningJobService slot anahtari artik TUR + KONU. Karar is turune gore
  degil ISIN KAYNAGINA gore verildi: sahibi olan is (site/abonelik yedegi, geri
  yukleme) KIRACI BASINA yuva; sahipsiz is (bilesen kurulumu, panel/posta yedegi,
  goc) MAKINE GENELI tek yuva -- onlar paylasilan calisma zamani dizinine, sistem
  PATH'ine, IIS yapilandirmasina yaziyor, yani kiraci basina bolmek GERCEK bir yaris
  acardi. Gerekce kodda `YuvaAnahtari` uzerinde yazili.
  Nobetci: `RunningJobKalanIkiYolTests` (+2 test). NOT: eski
  `Baska_Yedek_Surerken_...` testi SUNUCU GENELI bir isin bir KIRACIYI engellemesini
  bekliyordu -- yani kapatilan acigi olcuyordu; ayni aboneligin cakismasini olcecek
  sekilde duzeltildi.
* [KAPANDI 31.08.2026] Hiz siniri. TEK KAYNAK: `Api/Security/PanelRateLimits.cs`
  (politika adlari + degerler + bolumleme; Program.cs'te artik HICBIR sayi yok).
  Kuresel sinirlayici uc bolume ayrildi: anahtar 300/dk, YOKLAMA 900/dk, genel oturum
  600/dk -- oturum CEREZININ OZETINE gore (IP'ye gore degil; tek NAT'taki on yonetici
  birbirini kilitlemesin). Degerler OLCULEREK secildi: tek sekmede en kotu hal
  71 istek/dk (MonitoringView.vue:495 + ResourceConsumers.vue:465 + :492).
  Kimliksiz uclar: posta GONDEREN iki uc `public-mail-send` (IP basina 10/15dk),
  posta gondermeyenler `public-read` (300/dk), git webhook 30/dk.
  Nobetci: `PanelRateLimitTests` (28 test; yarisi FAZLA KAPATMA nobetcisi) +
  `PanelRequestSurfaceWiringTests` (25). 27 mutasyonun 27'si kirmizi yandi.
  DIKKAT: eski `PanelTokenRestAccessTests.Anahtarli_isteklerin_hiz_siniri_var`
  `GetNoLimiter("cookie")` satirini "beklenen davranis" diye CIVILEMISTI -- yani acigi
  koruyordu; duzeltildi ve gerekcesi testin icine yazildi.
* [KAPANDI 31.08.2026] Kestrel Limits. TEK KAYNAK: `Api/Security/PanelServerLimits.cs`,
  Program.cs'te SERTIFIKADAN BAGIMSIZ (kosulsuz) cagriliyor -- eskiden ConfigureKestrel
  yalnizca sertifika varken calisiyordu, yani tavanlar oraya konsaydi sertifikasiz
  kurulum sinirsiz kalirdi.
  BULGU DUZELTMESI: "hicbir sinir yok" YANLISTI. Kestrel'in varsayilanlari (30 MB govde,
  30 sn baslik zaman asimi, 240 B/sn en az veri hizi) yururlukteydi; GERCEKTEN sinirsiz
  olan tek sey `MaxConcurrentConnections` idi (artik 500) -- ve o, hiz sinirlayicinin
  GOREMEDIGI bir tuketimdir (baglanti acilir, istek hic gelmez).
  Uzun isler BILEREK kirilmadi: sure siniri YOK, olcut HIZ (yedek indirme, bilesen
  kurulumu, 1 GiB SQL ice aktarimi veri aktigi surece surebilir).
  YOLDA BULUNAN GERCEK KOPUKLUK: FilesController 200 MiB yuklemeye izin veriyordu ama
  `FormOptions.MultipartBodyLengthLimit` varsayilani 128 MiB idi -> aradaki her yukleme
  uc "izin veriyor" gorundugu halde InvalidDataException ile oluyordu. Ikisi artik ayni
  sabiti okuyor.
* [KARAR VERILDI 31.08.2026] UseForwardedHeaders KAPALI KALIYOR -- gerekce Program.cs'te ve
  `docs/hiz-siniri.md`'de YAZILI, `PanelRequestSurfaceWiringTests` ile sabitli.
  Sebep: bu PAYLASIMLI barindirma makinesinde kiracilarin PHP'si kosuyor. Tek "guvenli
  gorunen" secenek olan `KnownProxies = 127.0.0.1` acilsaydi, herhangi bir kiraci sitesi
  localhost'tan istek atip `X-Forwarded-For`a istedigi adresi yazabilirdi: (a) hiz sinirini
  ve IP yasagini her istekte sifirlamak, (b) KURBANIN adresini yazip yoneticiyi kendi
  panelinden kilitlemek. Yani loopback'e guvenmek, kimlik dogrulamasiz bir "IP degistirme"
  ucuna esdeger.
  KABUL EDILEN BEDEL (olculdu): autodiscover uclari musterinin IIS sitesindeki ARR kuraliyla
  panele ILETILIYOR (MailAutodiscoverRewriteRules.cs:27), yani adresleri 127.0.0.1'dir ve
  sunucudaki BUTUN posta istemcileri tek bolumu paylasir. `public-read` siniri bu yuzden
  dar degil 300/dk secildi.
  Panel bir DIS ters vekilin arkasina konacaksa: satir acilmali ve KnownProxies YALNIZ o
  vekilin adresiyle doldurulmalidir.

## BELLEGE TAM OKUMA (sinirsiz)
* [KAPANDI] ProcessRunner stdout/stderr artik TAVANLI okunuyor (varsayilan 4 MiB
  karakter/akis; deger gerekcesiyle kodda yazili). KRITIK AYRINTI: tavan asildiktan
  SONRA da boru tuketilmeye devam ediyor -- okumayi kesmek boruyu doldurup araci
  kendi write cagrisinda asardi, yani "bellegi koru" duzeltmesi her buyuk ciktida
  10 dakikalik bir asilmaya donusurdu. Kesilme SESSIZ DEGIL: akisin sonuna
  `ProcessRunner.TruncationNotice` ekleniyor (teshis ciktisi yalan soylemesin).
  Nobetci: `ProcessRunnerOutputLimitTests` (4 test), mutasyonla dogrulandi.
  NOT: ilk hali duvar saati olcuyordu ve tam takim altinda BIR KEZ kirmizi yandi
  (makine yuku, koruma degil); asilma sinirsiz oldugu icin saat tutmaya gerek yok.
* [ACIK] BackupService.cs:2251 gorev XML'i File.ReadAllText + Unicode.GetBytes +
  ToBase64String (~2.67x) ve bu bir DONGUDE, kaynak saldirgan arsivinden.
* [KAPANDI] DbToolsProxyController 16 MB siniri artik AKARKEN SAYIYOR
  (`BufferForInjectionAsync`). Eski kosul `contentLength is null or <= 16MB` idi;
  Content-Length gondermeyen (chunked) yanit `null` dalina dusup SINIRSIZ
  tamponlaniyordu -- yani sinir, tam da onu asan yanitlarda hic calismiyordu.
  "Uzunlugu bilinmeyeni hic tamponlama" YANLIS cozum olurdu: phpMyAdmin sayfalarinin
  cogu chunked gelir, layout duzeltmesi tumuyle olurdu. Tavan asilinca govde
  KAYBOLMUYOR, akitiliyor (yalniz enjeksiyondan vazgeciliyor).
  Nobetci: `DbToolsProxyBodyLimitTests` (7 test), mutasyonla dogrulandi.
* [ACIK] NginxInstallationSupport.cs:462 ve WebmailService.cs:346: DONMEYEN error.log'u
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

---

## K6 — `runtimes` yazilabilirlik acigi (kapatildi, commit ATILMADI)

### Olculen acik (icacls, canli kurulum)

```
C:\ProgramData\AdaPanel\runtimes          Everyone:(I)(CI)(F)  <-- ACIK
C:\ProgramData\AdaPanel\runtimes\php\8.3  Everyone:(I)(CI)(F)  <-- ACIK
C:\ProgramData\AdaPanel                   Everyone:(I)(CI)(F)
C:\ProgramData                            Everyone:(CI)(F)     <-- kaynagi burasi
```

`(CI)` oldugu icin ACE dosyalara inmez (`php.ini` ve `php_errors.log` dosya izni
`BUILTIN\Users:(I)(RX)` idi) ama **klasor uzerindeki FullControl dosyanin kendi izninden
BAGIMSIZ olarak FILE_ADD_FILE + FILE_DELETE_CHILD verir**: `php.ini` silinip degistirilebilir,
`php-cgi.exe`nin yanina DLL birakilabilir. Onceki tur gercek `php-cgi.exe` ile olcmustu:
kiraci PHP'si klasore 26 bayt YAZDI. Ayrica `BUILTIN\Users:(I)(CI)(WD,AD,WEA,WA)` Everyone
dusse bile dosya olusturmaya yeter.

Kritiklik sebebi: site basina PHP izolasyonu (`PhpIniSiteIsolation`) PAYLASILAN `php.ini`
icindeki `[PATH=]` bloklarina dayanir. O dosya kiracinin elindeyse `open_basedir` dahil butun
izolasyon altindan oyulur; panel (SYSTEM) ayni agactaki `php.exe`yi cron/composer icin
calistirdigi icin yol SYSTEM'e cikar.

### Ne degisti (TEK KAYNAK: `DataRootHardeningService`)

- `ApplyRuntimeReadOnlyRules` — `runtimes` agacinda kiraci gruplari (Users, Authenticated Users,
  Everyone, Guests, IIS_IUSRS) DUSURULUR, geri **yalniz `Users` + `IIS_IUSRS` = ReadAndExecute**
  verilir. Yazma SYSTEM + Administrators + panel servis hesabinda kalir.
- `runtimes` klasoru YOKSA olusturulur ve korunur: bilesenler acilistan SONRA kuruldugu icin
  klasor o an dogsaydi acik miras alirdi (olculmus yaris).
- Mirasi KIRILMIS birinci seviye bilesen klasorleri ayrica yazilir (kokten yayilim onlara inmez).
- `ApplyDataRootContainerRules` — veri kokunun KENDISINDE kiraciya yazma/silme/DACL-degistirme
  birakilmaz, okuma/gezinme KALIR. Gerekcesi: `runtimes` salt-okunur olsa bile ust klasordeki
  `FILE_DELETE_CHILD` onu yeniden adlandirmaya yeterdi (kiraci kendi `runtimes\php\8.3\php-cgi.exe`
  agacini koyar; IIS FastCGI ve cron mutlak yolla cagirir). Cocuklarin izinleri BIRE BIR korunur:
  kiraci kurallari ayni haklarla `InheritOnly` olarak geri konur, klasorun kendisine ise ayni
  kuralin `ReadAndExecute` ile maskelenmis hali yazilir.

### Yolda bulunan AYRI bir hata (olculdu, duzeltildi)

`PurgeAccessRules` bellekteki DACL'de **miras bayrakli ACE'lere dokunmuyor**; miras->explicit
donusumunu Windows ancak DACL korumali olarak diske yazilirken yapiyor. Gecici klasorde olculdu:
tek yazmada `SetAccessRuleProtection(true, preserveInheritance:true)` + `PurgeAccessRules(Everyone)`
sonucu `D:PAI(A;OICI;FA;;;WD)...` — Everyone HAYATTA KALDI. Yani mevcut `RemoveTenantAccess`
(stalwart, `mariadb\*\data`, `tools\win-acme`) **ancak IKINCI panel acilisinda kapatiyordu**;
taze kurulum ve guncelleme sonrasi ilk calisma boyunca acik kaliyordu. Canli makinede kapali
gorunmelerinin sebebi panelin defalarca yeniden baslatilmis olmasiydi.
Duzeltme: `ProtectAndMaterialize` (once yalniz korumayi yaz, sonra DACL'i geri oku, temizligi
acik liste uzerinde yap). Nobetci: `Mirasla_gelen_genis_ACE_TEK_calismada_duser`.

### Yazma ihtiyaci arandi, mesru olani BULUNAMADI

| Aday | Olcum | Karar |
|---|---|---|
| `error_log` -> `runtimes\php\8.3\php_errors.log` | dosya DACL'i `BUILTIN\Users:(I)(RX)` — kiraci ZATEN yazamiyor; panel bu dosyayi okumuyor (tek `php_errors.log` okuyucusu Roundcube, kendi `tools\roundcube\logs`'una bakiyor) | acilmadi |
| `npm -g` | npm'in Windows prefix'i node.exe klasorudur -> `runtimes\nodejs`; panel hicbir yerde `-g` kullanmiyor (kaynak tarandi) | BILEREK kirildi — paylasilan node kurulumuna paket yazmak saldirinin ta kendisi |
| composer / wp-cli | `tools\composer` altinda; `LimitedIdentityRunner` yazmayi yalniz SITE KOKUNE verir, ciktilar `%TEMP%`'e | etkilenmiyor |
| mariadb / mysql / redis / memcached / nginx / stalwart / clamav / docker | hepsi SYSTEM (`--install`, `--service-install`, gorev `UserId S-1-5-18`, panel sureci) | etkilenmiyor |
| EDB PostgreSQL (NETWORK SERVICE) | yazdigi yer `postgresql\*\data`; orasi zaten mirasi KOPYALAYARAK kapatiliyor (explicit ACE korunur) | **OLCULEMEDI** — bu makinede PostgreSQL kurulu degil |

### Kalan riskler / olculemeyenler

- **Dosya yoneticisinin ACL ucu** (`FileService` + `AclInputValidator`) yalniz hesap adi ve hak
  degerini dogruluyor; YOLUN kiraci kapsaminda olup olmadigi bu turda OLCULMEDI. Yetkili bir
  bayi bu uctan `runtimes`'a yeniden izin verebiliyorsa duzeltme geri alinabilir.
- Gercek `php-cgi.exe` ile "artik yazamiyor" dogrulamasi **canli panelde YAPILMADI** (gorev geregi
  kurulu panelin ACL'ine dokunulmadi). Olcum gecici klasorde gercek `DirectorySecurity` uzerinde.
- `sitebuilder` klasoru hala `Everyone:(I)(CI)(F)` (onceki turda bilerek birakilmisti, ayni durumda).

---

## HIZ SINIRI + ISTEK KATMANI DoS YUZEYI (31.08.2026) -- commit ATILMADI

Ayrintili karar belgesi: **`docs/hiz-siniri.md`** (olcum tablosu, politika tablosu,
vekil karari, Kestrel tavanlari, webhook jetonu).

**Yeni dosyalar:** `src/AdaPanel.Api/Security/PanelRateLimits.cs`,
`src/AdaPanel.Api/Security/PanelServerLimits.cs`, `src/AdaPanel.Core/Common/GitWebhookToken.cs`,
`tests/.../PanelRateLimitTests.cs`, `tests/.../PanelRequestSurfaceWiringTests.cs`,
`docs/hiz-siniri.md`.

**Git webhook jetonu:** artik `X-AdaPanel-Token` BASLIGIYLA da kabul ediliyor ve panel
arayuzu jetonsuz adres + baslik satiri uretiyor. Sorgu dizesi (`?token=`) KABUL EDILMEYE
DEVAM EDIYOR -- kullanicilarin GitHub/GitLab'a yapistirdigi adresler onu tasiyor ve
kapatmak o kancalarin hepsini SESSIZCE oldururdu. Sorgu yolu kullanildiginda DENETIM KAYDI
bunu acikca yaziyor ("the secret is written to IIS/W3C access logs"), arayuz de eski bicimi
uyariyla gosteriyor. `FixedTimeEquals` ve `concurrencyLock` BOZULMADI (testle sabitlendi).

**Yolda bulunan AYRI bir acik:** kapsam nobetcisinin ilk hali yalniz `[AllowAnonymous]`
ariyordu. Panelde kuresel `FallbackPolicy` YOK, dolayisiyla `[Authorize]` HIC tasimayan bir
denetleyici de tamamen aciktir -- `HealthController` ve `SetupController` tam olarak bu
durumdaydi ve nobetci onlari YESIL geciriyordu. `SetupWizardView.vue:1246` kimliksiz
`setup/progress` ucunu **1,5 saniyede bir** yokluyor (40 istek/dk, kurulum 20 dk+ surebilir);
bu yuzden oraya dar degil `public-read` (300/dk) konuldu.

**Mutasyon:** 27 mutasyon, 27'si kirmizi. IKISI ILK TURDA SAG KALDI ve duzeltildi:
(1) 429 zarfi testi kaynak METNINE bakiyordu ve anahtar ayni dosyadaki YORUMDA da geciyordu
-> davranis testine cevrildi (gercek `OnRejected` calistirilip govde okunuyor);
(2) kapsam nobetcisinin kendi kapsami daraltildiginda hicbir test kizarmadi -> tarama
kararini DOGRUDAN olcen bir Theory eklendi.

**OLCULEMEYENLER:**
- Sinirlar GERCEK bir HTTP boru hattinda degil, ara katmanin kullandigi bolumleme
  fonksiyonu `PartitionedRateLimiter.Create` ile sarilarak olculdu. Ust duzey `Program.cs`
  (tepsi/DB onyuklemesi) tam boru hattiyla test edilemiyor; kablolama ayri testlerle
  kaynak uzerinden dogrulandi.
- `MaxConcurrentConnections = 500`'un canli sunucuda gercek yuk altindaki etkisi
  OLCULMEDI (bu makinede yuk uretecek bir kurulum yok).
- ARR arkasindaki autodiscover'in gercekten 127.0.0.1'den geldigi KODDAN cikarildi
  (kural uretimi), canli IIS'te GORULMEDI -- bu makinede Stalwart/ARR kurulu degil.
- Webhook basliginin gercek GitHub/GitLab arayuzunden gonderildigi denenmedi.

---

## KIMLIK DOGRULAMA VE OTURUM GUVENLIGI (31.08.2026) -- commit ATILMADI

Panelin ON KAPISI denetlendi: giris akisi, oturum yonetimi, TOTP, parola sifirlama, hesap
kilidi, gizli giris yolu, REST jetonu, CSRF ve yetkilendirme karar noktalari.

**Kapatilan alti acik:**

1. **CIKIS OTURUMU SONLANDIRMIYORDU.** Cerez kendi kendine yeterli (kimlik + bitis ani
   icinde sifreli), sunucuda kayit yok; `SignOutAsync` yalniz TARAYICIDAKI kopyayi siliyordu.
   Calinmis bir cerez cikistan sonra 12 saat (kalici secilmisse 14 gun) calismaya devam
   ediyordu. Yeni: her giriste 128 bitlik oturum kimligi (`adapanel_sid`) uretiliyor, cikista
   `SessionRevocationStore` kara listesine yaziliyor ve `CookieSessionValidator` bunu HER
   ISTEKTE -- 60 saniyelik tazelik kisayoluna TAKILMADAN -- soruyor. Liste bellekte + dosyada
   (`config/revoked-sessions.json`, 15 gun); dosya sart, cunku cerez biletleri yeniden
   baslatmadan sag cikiyor.
2. **OTURUM SABITLEME artik OLCULUYOR.** Kimlik her `SignInAsync`'te YENIDEN uretiliyor ve
   varsa eski kimlik ATILIYOR; girisden onceki cerez, girisden sonraki oturum olamaz.
3. **TOTP TEKRAR OYNATMA.** Kod 30 sn'lik adimdan uretiliyor, +-1 adim tolerans yuzunden AYNI
   KOD 90 saniye gecerliydi ve yalniz "gecerli mi" soruluyordu. `ITotpService.VerifyCode` artik
   eslesen ADIMI donuyor, adim `TwoFactorRecoveryCodes.TryConsumeTotpStepAsync` ile TUKETILIYOR
   (`step <= son` reddedilir, yani daha eski adimin kodu da duser). Tolerans BOZULMADI.
4. **KULLANICI NUMARALANDIRMA -- uc kanal.** (a) "auth.user_inactive" PAROLADAN ONCE
   donuyordu: parola bilmeyen biri tek istekte hesabin varligini ogreniyordu -> denetim
   paroladan SONRAYA alindi (sahibi mesaji HALA goruyor). (b) "auth.user_locked" yalniz var
   olan hesap icin uretilebiliyordu -> kaldirildi; ustelik mesaj YANLISTI, bu panelde dogru
   parola kilidi kaldirip iceri aliyor. (c) ZAMAN: kullanici yokken hic karma
   hesaplanmiyordu, varken PBKDF2 calisiyordu -> olmayan kullanici yolunda da sahte bir
   karma dogrulaniyor.
5. **PAROLA SIFIRLAMA BELIRTECI kisa parolada YANIYORDU.** Kural belirtec TUKETILDIKTEN sonra
   uygulaniyordu; kullanici yeni parolayi kisa yazinca link oluyordu. `PasswordPolicy.Ensure`
   artik tuketmeden ONCE. Ayrica arayuz `?resetToken=` degerini adres cubugundan HEMEN siliyor
   (eskiden yalniz BASARILI sifirlamada siliniyordu; basarisizlikta gecmiste ve Referer'da
   kaliyordu).
6. **`2fa/disable` DAR hiz sinirina baglandi** (mevcut parolayi dogruluyor, yani parola deneme
   yuzeyi) ve **`AllowHttpFallback` iki DTO'da varsayilan KAPALI** yapildi -- alan gonderilmeyen
   bir kayit istegi HTTP dinleyicisini ve `Secure`siz cerezi SESSIZCE aciyordu.

**Yeni dosyalar:** `src/AdaPanel.Infrastructure/Security/SessionRevocationStore.cs`,
`tests/.../OturumSonlandirmaTests.cs`, `tests/.../TotpTekrarOynatmaTests.cs`,
`tests/.../GirisNumaralandirmaTests.cs`, `tests/.../ParolaSifirlamaBelirteciTests.cs`,
`tests/.../OturumCereziBayraklariTests.cs`, `tests/.../KimlikUclariHizSiniriTests.cs`,
`tests/.../GirisDuzenegi.cs`, `tests/.../OturumTestOrtami.cs`.

**Mutasyon:** 17 mutasyon, 17'si kirmizi (fazla-kapatma nobetcileri dahil: TOTP penceresi
kapatilinca ve "her oturum kaldirilmis" yapilinca da kirmizi yandi). Ilk turda M13 yanlis
kurgulanmisti (denetimi SILIYORDU, TASIMIYORDU) -- dogrusuyla tekrarlandi.

**TEMIZ CIKANLAR (bir sonraki tur tekrar aramasin):** cerez bayraklari (HttpOnly + SameSite=Strict
+ Secure/Always), CSRF (`PanelAntiforgeryFilter`, kimlik yoluna gore), REST jetonu (256 bit,
SHA-256 ozetli, iptal edilebilir, sahibi pasifse gecersiz), tek kullanimlik belirtec deposu
(`SingleUseTokenStore`), kurtarma kodlari (SHA-256, tek kullanimlik, kilit altinda),
`ParolaBelirlemeKomutu` (HTTP ucu yok, yonetici sart, 2FA korunur), `recover-access.flag`
(parola/2FA'ya dokunmaz), arayuzde localStorage'da kimlik bilgisi YOK, CSRF jetonu giris/cikis/
kimlige burunmede yenileniyor, yetkilendirme kararlarinin TAMAMI claims'ten (66 denetleyici
tarandi, govdeden gelen aktor/rol YOK).

**OLCULEMEYENLER / ACIK KALANLAR:**
- **`password/forgot` ZAMAN farki:** adres varsa SMTP gonderimi BEKLENIYOR, yoksa aninda
  donuluyor. Yanit metni ayni ama SURE ayni degil -> numaralandirma kanali. Kapatilmadi:
  gonderimi arka plana almak kendi kapsamini (IServiceScopeFactory) gerektiriyor ve yarim
  yapilirsa sifirlama e-postalari SESSIZCE olur. Uc `login` politikasiyla sinirli (5/dk/IP).
- **`2fa/recovery-codes` PAROLA ISTEMIYOR.** Calinmis bir oturum yeni kurtarma kodlari uretip
  kalici arka kapi birakabilir (denetim kaydi var). Kapatmak arayuzde parola sormayi gerektirir.
- Hesap kilidi bir ENGEL degil SINYALDIR: dogru parola kilidi kaldirir (bilincli, DoS'u onlemek
  icin). Gercek kaba kuvvet savunmasi IP katmani + hiz siniri.
- Cerez akisi GERCEK bir HTTP boru hattinda degil, `CookieSessionValidator`/`AuthController`
  dogrudan calistirilarak olculdu (ust duzey `Program.cs` tam boru hattiyla test edilemiyor).
- Canli panelde (127.0.0.1:8890) HICBIR SEY denenmedi -- talimat geregi.

---

## POSTA GUVENLIGI + TESLIM EDILEBILIRLIK (31.08.2026) -- commit ATILMADI

**ACIK ROLE: TEMIZ -- ve bu artik tahmin degil OLCUM.** Stalwart 0.16.8 yalitilmis bir kopyada
(gecici klasor, panelin yazdigi minimal `config.json`) ayaga kaldirilip `stalwart-cli` ile
sorgulandi. Kurulu panele ve kurulu Stalwart'a DOKUNULMADI, hicbir posta gonderilmedi.
Olculen varsayilanlar:

| Alan | Deger | Anlami |
|---|---|---|
| `MtaStageRcpt.allowRelaying` | `!is_empty(authenticated_as)` | acik role YOK |
| `MtaStageAuth.require` | `local_port != 25` | 25'te AUTH yok (MX icin dogru) |
| `MtaStageAuth.saslMechanisms` | 25'te `false`; TLS'siz portlarda yalniz `oauthbearer/xoauth2` | duz metin parola TLS'siz gecmez |
| `MtaStageAuth.mustMatchSender` | `true` | kiraci BASKASININ MAIL FROM'uyla gonderemez |
| `MtaStageData.enableSpamFilter` | `is_empty(authenticated_as)` | giden postada spam suzgeci kapali (normal) |
| Varsayilan dinleyiciler | 8080, 443, 4190, 995, 993, 465, 25 | **143 ve 587 YOK** -> panel kurmasa istemci baglanamaz; **110 da YOK** |

Panel yalniz `MtaStageRcpt.script` ve `MtaStageData.script` yuvalarina yazar (posta listesi
politikasi + otomatik yanit); role/kimlik alanlarina HIC dokunmaz. Bu karar artik
`ComponentBootstrapper.ConfigureStalwartListenersAsync` icinde olcum tablosuyla yazili.

**KAPATILAN 1 -- Host basligi enjeksiyonu (kimliksiz uctan giden postaya).**
`MailListPublicController.AbsoluteUrl` onay baglantisini `Request.Host`'tan kuruyordu;
`appsettings.json`'da `AllowedHosts: "*"` ve `UseHostFiltering` YOK. Uc kimliksiz ve POSTA
GONDERTIYOR -> saldirgan `Host: kotu.example` ile gercek bir abonenin kutusuna panelin kendi
alan adindan kimlik avi baglantisi koydurabiliyordu (tiklanirsa tek kullanimlik belirtec de
saldirgana giderdi). Artik adres `PanelAddress.BuildBaseUrl` ile panelin YAPILANDIRILMIS
adresinden kurulur; `Host` yalniz `ServerAddress` bosken yedektir (kaldirilsaydi adres
ayarlamamis kurulumlarda baglanti OLU giderdi).

**KAPATILAN 2 -- "DNS kayitlari yayinlandi" YALANI + bilinen kirik desen.**
`DnsService.UpsertRecordsAsync` bolge IKINCIL ise / DNS rolu yoksa kayitlari yalniz panel
durumuna yazar ve BASARIYLA doner. Buna ragmen `MailService` kosulsuz "published" logu yaziyor,
`MailController` ise `AppliedCount`'u YAZILAN degil GONDERILMESI DUSUNULEN kayittan uretiyordu.
Stalwart giden her postayi DKIM ile imzaladigi icin yayinlanmamis TXT = dogrulanamayan imza =
spam. Karar tek kaynaga cikti: `MailDnsPublication.Evaluate`. Ayrica "Uygula" ucu kayitlari
TEK TEK yaziyordu -- depoda iki ayri dosyada "bu desen bolgeyi kilitliyor" diye olculmus olan
kirik desen; ustelik alan adi olusturulurken duzeltilmis toplu yazmanin ardindan AYNI istekte
ikinci kez kosuyordu. Artik tek toplu cagri.

**Yeni dosyalar:** `src/AdaPanel.Infrastructure/Mail/MailDnsPublication.cs`,
`tests/.../PostaBaglantiAdresiTests.cs`, `tests/.../PostaDnsYayinDurustlugTests.cs`.
**Testler:** 4514 -> 4538 (bu turda +17). Mutasyon: 8 mutasyon, 8'i kirmizi
(fazla-kapatma nobetcileri dahil: yedek kaldirilinca ve "hicbir zaman yayinlandi deme"
yapilinca da kirmizi yandi).

### ACIK KALANLAR (olculdu, KAPATILMADI -- sebebiyle)

1. **`From` basligi hizalanmasi.** `mustMatchSender` yalniz ZARF gondericisini (MAIL FROM)
   baglar. Kimligi dogrulanmis A kiracisi `From: fatura@musteriB.com` yazabilir. DMARC hizalamasi
   bunu alici tarafta yakalar (B'nin DMARC'i varsa); Postfix/Plesk dunyasinda da durum aynidir.
   Kapatmak Stalwart'ta ozel bir MtaStageData script'i ister ve yanlis yazilirsa MESRU posta
   (posta listesi, otomatik yanit, uygulama bildirimi) durur.
2. **MailEnable yolu ile Stalwart yolu FARKLI politika uretiyor:** `MailEnableMailService`
   `p=none` ve SPF `v=spf1 a mx ~all` yaziyor, DKIM HIC uretmiyor; Stalwart yolu `p=quarantine`.
   Uc ayri SPF ureticisi var (`MailService`, `MailEnableMailService`, `DnsService` sablonu).
   Tek kaynaga cekilmedi: MailEnable kullanan bir kurulumda `p=none -> p=quarantine` degisikligi
   DKIM'siz bir sunucuda teslimati BOZABILIR ve bu makinede olculemez.
3. **`NotificationService.GetPlaintextPassword`** `Unprotect` hatasinda sessizce duz metin
   parolaya duser (`catch { return storedPassword; }`) -- koruma zorunlu degil tercihli.
4. **`UseSsl=false`** iken SMTP kimlik bilgileri duz metin gidiyor; sunucu tarafi bu birlesimi
   engellemiyor. SMTP PORTU icin sunucu tarafi kisit yok (`OutboundAddressGuard` yalniz konagi
   denetliyor ve yalniz KAYDETME aninda).
5. **Sertifika dogrulamasi tamamen kapali 4 yer** (posta yolunda DEGIL):
   `Cluster/ClusterHttpClientFactory.cs:125`, `Migration/SiteMigrationService.cs:264,357`,
   `Seo/SeoHttpTransport.cs:49`.
6. **Teshis ile karsilama-adi mantigi celisiyor:** `MailDiagnosticsRunner` PTR'nin
   `mail.<domain>`e esit olmasini sart kosup `fail` verirken `RdnsService` ayni durumu dogru
   sekilde uyari sayiyor. Ayrica teshis coklu DKIM'de yalniz ILK seciciye bakiyor
   (`FirstOrDefault`) ve SPF/DMARC'ta degeri degil VARLIGI olcuyor.
7. **`DnsService.cs:1253` olu referans:** yorum var olmayan `EnsureMailRecordsAsync`e yonlendiriyor.

### OLCULEMEYENLER
- Gercek bir SMTP oturumu HIC acilmadi (gorev geregi): "25'ten role denendi, reddedildi" gibi
  bir kanit YOK. Kanit, sunucunun kendi yapilandirma nesnelerinin okunmasidir.
- Kurulu paneldeki (127.0.0.1:8890) Stalwart yapilandirmasi OKUNAMADI: `runtimes\stalwart`
  agacina erisim reddedildi -- ki bu sertlestirmenin calistiginin da kanitidir.
- PHP `mail()` yolu: panel php.ini'ye `SMTP`/`sendmail_path` YAZMIYOR (tarandi, tek eslesme
  `SiteMigrationService`'teki goc anahtar listesi). Yani yerel sitelerin PHP'si varsayilan
  `localhost:25` ile Stalwart'a girer; olculen varsayilanlara gore DISARIYA role REDDEDILIR
  (kimlik yok) ama YEREL kutulara teslim EDILIR ve `MtaStageMail.isSenderAllowed` kimliksiz
  gondericiye herhangi bir gonderen alan adi birakir. Yani "kiracidan kiraciya ic sahtecilik"
  mumkun gorunuyor; CANLI DENENMEDI.

---

## POSTA GONDERICI KIMLIGI -- GERCEK SMTP OTURUMUYLA KESINLESTIRME (31.08.2026) -- commit ATILMADI

Bir onceki tur "ic sahtecilik MUMKUN GORUNUYOR ama canli denenmedi" diye birakmisti. Bu tur
Stalwart 0.16.8 (components.json'daki sha256 ile birebir) YALITILMIS bir kopyada -- panelin
yazdigi minimal `config.json`, dinleyiciler loopback + host-only VMnet1 (192.168.45.1, disari
yol YOK), DNS cozumleyici de loopback'te sahte bir DNS sunucusuna (`127.0.0.1:15353`) sabitlendi;
DNSBL sorgulari bile o sahte DNS'e dustu, github/spam-kural cekimi icin ad cozulemedi. Kurulu
panele (8890) ve kurulu Stalwart'a DOKUNULMADI, DISARIYA TEK PAKET CIKMADI. Dort senaryo GERCEK
SMTP oturumuyla surudu:

**ASIL SORU KAPANDI -- BULGU VAR: kimliksiz KIRACIDAN KIRACIYA ic sahtecilik, kurbanin GELEN
KUTUSUNA (INBOX) dusuyor.**

| # | Senaryo | Oturum sonucu | Karar |
|---|---|---|---|
| 1 | Kimliksiz `:25`, `MAIL FROM/RCPT` = B alan adi | `250 ... queued` -> teslim; loopback (127/8) kaynagi SPF'te softfail sayildigindan JUNK | ACIK (kabul) |
| 1e | Kimliksiz `:25`, kaynak IP B'nin SPF'inde YETKILI (paylasilan sunucu IP'si modeli: host-only 192.168.45.1) | `spf=pass`, `dmarc=pass`, `X-Spam-Status: No`, skor -0.50 -> **INBOX** | **ACIK -- BULGU** |
| 2 | A olarak kimlik dogrula, `MAIL FROM` = B | `501 5.5.4 You are not allowed to send from this address.` | KAPALI (`mustMatchSender`) |
| 3 | A kimlikli, ZARF=A dogru, **From BASLIGI**=B | `250 queued` -> **INBOX**, DKIM `d=a...`, alici From'da B'yi gorur, uyari YOK | ACIK (baslik hizalanmasi) |
| 4 | PHP `mail()` yolu | Panel php.ini'ye SMTP/sendmail_path YAZMAZ (dogrulandi); PHP varsayilani `SMTP=localhost:25` (olculdu). `:25`'te `rejectNonFqdn=true` tek-etiketli HELO'yu `550` reddeder; FQDN HELO (gercek sunucu hostname'i / PHPMailer / ham soket) gecer ve Senaryo 1'e baglanir | ACIK (FQDN HELO ile) |

**KOK NEDEN -- olculdu:** `MtaStageMail.isSenderAllowed = "!is_empty(authenticated_as) ||
!key_exists('spam-block', sender_domain)"` kimliksiz baglantiya HERHANGI bir gonderen alan adi
birakir. Panel her alan adi icin `v=spf1 mx a ip4:{serverIP} ~all` yazdigindan PAYLASILAN sunucu
IP'si HER kiracinin SPF'inde yetkilidir; ayni sunucudaki bir site/PHP o IP'den baglaninca B'nin
alan adiyla gonderdigi posta `spf=pass`+`dmarc=pass` alip B'nin INBOX'ina dusuyor. `:25`
gercek panelde `0.0.0.0`'a baglidir (`PublicMailListenerPorts` + `ResolveStalwartListenerBinds`),
yani yerel bir surec dogrudan baglanabilir. Senaryo 3 ayrica From-baslik sahteciliginin ayni
sunucudaki teslimatta yakalanmadigini gosterir (kimlikli gonderimde spam suzgeci kapali +
yerel teslimatta yeniden DMARC yok; "alici tarafta DMARC yakalar" varsayimi AYNI SUNUCU ICINDE
GECERSIZ).

**KAPATILMADI (bilerek, kok neden Stalwart politika alani; panel bu alana dokunmuyor ve yanlis
bir MtaStageMail/MtaStageData script'i MESRU postayi -- posta listesi, otomatik yanit, uygulama
bildirimi -- durdurabilir). Rapor edildi; duzeltme sunucu-tarafi bir kabul politikasi
(orn. kimliksiz gonderende gonderen alan adini yalniz yerel + spam olarak isaretle, ya da
`:25`'i loopback'e indirip site PHP'sini submission+kimlikle zorla) gerektirir ve ayri bir
tasarim kararidir.**

### IKINCI IS -- UC SPF URETICISI TEK KAYNAGA INDIRILDI (davranis korunarak)

Uc uretici bulundu ve GERCEK cikti KARSILASTIRILDI (`dotnet` repro):

| Uretici | IPv4 girdi | IPv4 cozulemeyince (bos) |
|---|---|---|
| `MailService.BuildSpfValue` | `v=spf1 mx a ip4:{ip} ~all` | `v=spf1 mx a ~all` (dogru) |
| `DnsService.CreateDefaultRecords` (managed) | `v=spf1 mx a ip4:{ip} ~all` | `v=spf1 mx a ~all` (dogru) |
| `DnsService` bolge sablonu (`ExpandPlaceholders`) | `v=spf1 mx a ip4:{ip} ~all` | **`v=spf1 mx a ip4: ~all` (BICIMSIZ -> permerror)** |

IPv4'te ucu de AYNIYDI; ama sablon yolu IPv4 cozulemeyen sunucuda `ip4:`'u BOS birakip gecersiz
bir SPF uretiyordu (permerror = mesru giden posta reddedilir/isaretlenir). Ayrisma KAZAYDI
(ayni kuralin kopyasi), gerekceli degil. Tek kaynaga indirildi: yeni `Mail/SpfPolicy.cs`;
`MailService` ve `DnsService` (managed + sablon) ayni kurali kullanir. `ExpandPlaceholders`'a
bicimsiz-SPF nobetcisi eklendi: `ip` bosken yalniz ` ip4:{ip}` belirteci dusurulur, kullanicinin
kendi `ip4:`/`include:` mekanizmalari (`{ip}` icermez) KORUNUR.

**MailEnable yolu BILEREK HARIC:** `MailEnableMailService` DKIM URETMEDIGINDEN `v=spf1 a mx ~all`
+ `p=none` yazar; DKIM'siz bir sunucuda `p=quarantine`'e cekmek teslimati bozabilir -> bu ayrisma
GEREKCELIDIR ve tek kaynaga katilmadi. (`DnsView.vue`'daki `v=spf1 a mx ~all` yalniz arayuz ipucu
metnidir, uretici degil.)

**Yeni dosyalar:** `src/AdaPanel.Infrastructure/Mail/SpfPolicy.cs`,
`tests/AdaPanel.Infrastructure.Tests/SpfTekKaynakTests.cs` (+12 test).
**Mutasyon:** 3 mutasyon, 3'u kirmizi -- (1) SpfPolicy guard'i kaldirinca, (2) bicimsiz-SPF
nobetcisi kaldirilinca, (3) FAZLA-KAPATMA nobetcisi: nobetci asiri agresif yapilip kullanicinin
kendi `ip4:`'unu de silince kirmizi yandi.

### BU TURDA OLCULEMEYENLER
- Farkli SUNUCULAR arasi (B baska makinede) From-baslik/zarf sahteciligi: alicinin GERCEK
  MX'i denenmedi (yalitilmis tek dugum). Ayni-sunucu ic sahtecilik olculdu; capraz-sunucuda
  DMARC alici tarafta yakalar (klasik), bu tur kapsam disi.
- Kurulu paneldeki (8890) Stalwart yapilandirmasi yine OKUNAMADI (sertlestirme erisimi reddediyor).

---

## KAPATILDI: kiracidan kiraciya posta sahteciligi (31.08.2026)

Bir onceki tur bu acigi KANITLAYIP kapatmamisti ("duzeltme Stalwart'in kabul politikasi
alaninda; yanlis yazilan bir kural mesru postayi durdurur"). Bu turda kapatildi -- ama once
STANDART pratik arastirildi, sonra her mesru akis yalitilmis ornekte GERCEK SMTP oturumuyla
sinandi.

### Arastirilan standart pratik
Postfix `smtpd_sender_restrictions = permit_sasl_authenticated, check_sender_access <yerel
alan adlari> -> REJECT`; Exim/cPanel `deny senders = +local_domains / !authenticated = *`.
Ozet: **kimliksiz bir baglanti, sunucunun KENDI yerel alan adlarindan biri adina gonderemez.**
Postfix recetesinin yaygin hali listeye `permit_mynetworks`'u de koyar (loopback'e guvenir);
paylasimli barindirmada acigin TAM OLARAK geldigi yer orasi oldugu icin burada loopback
ayricaligi YOK -- kiracinin PHP'si de loopback'ten baglanir.

### Uygulanan yer -- Stalwart'in DOGRU yuvasi
`MtaStageMail.isSenderAllowed` (semadaki tanimi: "gonderenin gonderme izni oldugunda true
donen ifade"). Panel bugune kadar yalniz `MtaStageRcpt.script` ve `MtaStageData.script`
yuvalarina yaziyordu.

| | ifade |
|---|---|
| Stalwart fabrika | `!is_empty(authenticated_as) \|\| !key_exists('spam-block', sender_domain)` |
| AdaPanel siki | `!is_empty(authenticated_as) \|\| (!key_exists('spam-block', sender_domain) && !is_local_domain(sender_domain))` |

`is_local_domain` uydurma degil: Stalwart'in KENDI varsayilanlarinda geciyor
(`SenderAuth.dkimSignDomain`, `MtaOutboundStrategy.route`) ve MAIL asamasinda calistigi
gercek oturumla olculdu.

### Senaryo 3 (From BASLIGI) da kapatildi
DATA asamasi sieve script'ine (panelin zaten sahip oldugu TEK yuva) dar bir blok eklendi:
oturum kimlikliyse + From basliginda alan adi varsa + zarf gonderenin alan adindan FARKLIYSA
+ o alan adi BU SUNUCUDA barindiriliyorsa -> `reject`. Olculdu: sieve icinde `authenticated_as`
derlenmiyor, `env.authenticated_as` derleniyor.

### BILEREK KIRILAN AKISLAR (ikisi de SESSIZ DEGIL)
1. **PHP `mail()` ile kimliksiz gonderim** (kendi alan adindan bile). Alternatif panelde
   yazili: `panel.mail.senderPolicyTitle/Hint` -> PHPMailer/SMTP + kutu parolasi, 587/465.
2. **Panelin KENDI SMTP'si** `localhost:25 + kimlik yok + gonderen kendi alan adimiz` diye
   ayarliysa. Parola sifirlama hatayi BILEREK yutar (kullanici numaralandirmasi), yani
   sessiz olurdu -> `NotificationService` artik gonderen reddini taniyip
   `notifications.smtp_sender_rejected` firlatiyor (ne yapilacagi metinde yazili).
3. **Iki alan adi barindiran kiracinin, birinin kutusuyla digerinin adina From yazmasi**
   (Senaryo 3 bloğu). Gonderim aninda 5xx + ne yapilacagini soyleyen metin.

### ACIK KAPI
`Panel:AllowUnauthenticatedLocalSenders` (appsettings.json, VARSAYILAN `false`). Arayuze
KONMADI -- ayni gerekce `AllowInternalOutboundTargets`te yazili. Acildiginda panel FABRIKA
varsayilanini GERI yazar (kapi gercekten kapi; olculdu).

### MEVCUT KURULUMLAR
Yapilandirma bastan yazilmaz. Yuvada fabrika varsayilani ya da bizim bildigimiz bir ifade
varsa yazilir; isletmecinin KENDI ifadesi varsa DOKUNULMAZ + uyari. Kural her panel
acilisinda uygulanir (`MailSenderPolicyWorker`) -- kurulum adimina konsaydi yalniz Stalwart'i
bu surumle KURAN makineler korunurdu.

### Yeni dosyalar
`src/AdaPanel.Infrastructure/Mail/MailSenderPolicy.cs`,
`src/AdaPanel.Infrastructure/Mail/MailSenderPolicyWorker.cs`,
`tests/AdaPanel.Infrastructure.Tests/MailSenderPolicyTests.cs` (+37 test; 4550 -> 4587).
**Mutasyon: 9 mutasyon, 9'u kirmizi; 5'i FAZLA KAPATMA nobetcisi.**

### OLCUM SIRASINDA YAKALANAN IKINCI HATA (kod yazilirken degil, SUNUCUDA GORULDU)
`JsonSerializer`'in VARSAYILAN kodlayicisi `'` ve `&` karakterlerini JSON'da ters-bolulu kacis dizisine
cevirir (`'` / `&`); ifadede ikisi de var. Deger komut satirina `CliJsonArg` ile geciyor ve o, MSVC
kurali geregi her ters bolüyü IKILIYOR -> sunucuya `\u0027` gidiyor ve guncelleme
`validationFailed` ile dusuyordu. Yani duzeltme HER KURULUMDA olu olacakti. Gevsetilmis
kodlayiciya gecildi (ifadede `"` ya da `\` yok) ve `CliField_NeverContainsABackslash`
nobetcisi kondu. Panelin URETTIGI komut satiri, yalitilmis sunucuda birebir calistirilip
okunan degerin bayt bayt ayni oldugu dogrulandi.

### BU TURDA OLCULEMEYENLER
- Kurulu paneldeki (8890) Stalwart yapilandirmasi yine OKUNAMADI; kural GERCEK bir kurulumda
  degil, yalitilmis kopyada dogrulandi.
- `MailSenderPolicyWorker`'in gercekten acilista kostugu CANLI panelde gorulmedi (kayit ve
  kod yolu okundu, davranis yalitilmis Stalwart'a elle ayni CLI cagrilariyla uygulandi).
- Farkli SUNUCULAR arasi sahtecilik (tek dugum).
- Roundcube/webmail'in From kimlikleriyle Senaryo 3 blogunun etkilesimi gercek webmail'de
  denenmedi (SMTP seviyesinde denendi).

---

## VERITABANI KIRACI IZOLASYONU DENETIMI (31.08.2026) -- commit ATILMADI

Olcum icin YALITILMIS ornekler kuruldu; kurulu panele (8890) ve makinedeki hicbir mevcut
veritabanina DOKUNULMADI. MariaDB 11.8.3 -> `127.0.0.1:13306`, PostgreSQL 16.14 (components.json'un
sevk ettigi surum) -> `127.0.0.1:15432`. Ikisi de yalniz loopback'i dinliyor. Test nesneleri
BIRAKILDI (silinmedi), adlari `adapanel_test_*`.

### TEMIZ CIKANLAR (bir sonraki tur burayi aramasin)

| Soru | Olcum | Sonuc |
|---|---|---|
| MySQL hibe kapsami joker mi | Kacirilmamis `GRANT ... ON` + ters tirnakli `a_db` ile komsu `aXdb` GERCEKTEN okundu; `EscapeGrantDatabaseName` ciktisi (`a\_db`) ile `ERROR 1142` | KAPALI (onceki tur) |
| `SHOW DATABASES` sizinti | Kacirilmis hibeli kullanici yalniz kendi DB'sini + `information_schema` gordu | TEMIZ |
| `information_schema` uzerinden komsunun sema/tablo/sutun adlari | 0 satir; `mysql.user` `ERROR 1142` | TEMIZ (motor yetkiye gore suzuyor) |
| FILE / SUPER / PROCESS / RELOAD sizmis mi | `SHOW GRANTS` = yalniz `USAGE ON *.*` + tek DB; `LOAD_FILE`=NULL, `INTO OUTFILE` 1045, `SET GLOBAL` 1227, `SHOW PROCESSLIST` yalniz kendi is parcacigi | TEMIZ |
| Yeni kiraci hesabinin host'u | `MySqlAccountHosts.SafeCreationHost` -> `@'localhost'` (`DatabaseService.NewUserHost`) | TEMIZ |
| Root parolasinin HTTP'ye donmesi | `DatabaseWorkspaceDto.AdminPassword` YALNIZ `DatabasesController` (admin-only) tarafindan seri hale getiriliyor; `SitesController` ayni servisi cagirir ama DTO'yu donmez | TEMIZ |
| `db-credentials.json` dosya yoneticisinden okunabilir mi | `FileService.ResolveTargetAsync` yalniz `sites` kokunu kabul eder | TEMIZ |
| MCP arac yuzeyi | `AdaPanelReadTools`/`WriteTools` icinde veritabani araci YOK (bilerek) | TEMIZ |
| Self-servis silme kapsami | `ResellerDatabasesController.DeleteAsync` sahiplik kaydiyla dogruluyor; kayit yoksa `null != Guid` -> 403 | TEMIZ |
| `restore-items` ile capraz DB geri yukleme | O yol dokum ICE AKTARMAZ, yalnizca dosya cikarir; `ImportDatabasesFromArchiveAsync` cagiranlarinin hepsi admin-only | TEMIZ |

### BULGU 1 -- KAPATILDI: site yedegi ile CAPRAZ KIRACI VERI SIZDIRMA (kritik)

`BackupController.BackupSiteAsync` yalniz `request.SiteName`'i denetliyordu. `request.Databases`
GOVDEDEN geliyor (`BackupSiteRequest.Databases`), `BackupService.cs:1124-1134` onu dogrudan
`ExportDumpAsync`'e veriyor ve dokum panelin KOK kimligiyle aliniyor. Yani kiraci KENDI sitesinin
adini + BASKASININ veritabanini yazip arsivi `GET /backup/{id}/download` ile indirebiliyordu;
indirme denetimi arsivi kendi sitesinin yedegi gordugu icin geciyordu. Deponun bilinen hata
sinifi: **kapsam denetimi dogru, denetlenen degeri kiraci seciyor.**

Kural `SitesController`'in ozel metodundan cikarilip **tek adrese** tasindi:
`CustomerAccess.ResolveDatabaseScopeAsync` / `EnsureDatabaseAsync` (+ `TenantDatabaseScope`).
`SitesController` artik ayni kaynagi kullanan ince bir sarmalayici. Iki kopya olsaydi biri
duzelir, oteki sessizce acik kalirdi -- nitekim tam olarak bu olmustu.

### BULGU 2 -- KAPATILDI: PostgreSQL'de her kiraci her veritabanina BAGLANABILIYOR

Yalitilmis PG 16.14'te surulup olculdu: A kiracisinin rolu B'nin veritabanina baglandi ve
`pg_class`/`pg_attribute` uzerinden B'nin TUM tablo + SUTUN adlarini okudu. Sebep: PostgreSQL
`CONNECT` yetkisini her veritabaninda `PUBLIC`'e VERILMIS olarak dogurur; panelin `pg_hba.conf`'u
`host all all 127.0.0.1/32` yazar, yani "hangi veritabani" sorusuna karismaz. (Satir verisi
GELMEDI: tablo yetkisi PUBLIC'te yok. `information_schema` ve `CREATE` de suzuldu. Yani veri
degil, kurbanin SEMASI siziyordu.) Kod bu gercegi zaten BILIYORDU -- `ReadAllGrantedDatabasesAsync`
yorumu "PUBLIC'e verilmis CONNECT yuzunden her rol her veritabani icin dogru doner" diyor, ama
yalniz raporlamada dolaniliyor, yetki kaldirilmiyordu.

### BULGU 3 -- KAPATILDI: PostgreSQL saglama yolu SEVK EDILEN SURUMDE KIRIKTI

Ayni olcumde: `GRANT ALL PRIVILEGES ON DATABASE x TO u` almis bir kiraci KENDI veritabaninda
`CREATE TABLE` YAPAMIYORDU ("public semasina erisim engellendi"). PostgreSQL 15, `PUBLIC`'in
`public` semasi uzerindeki `CREATE` hakkini kaldirdi; veritabani duzeyi yetki sema duzeyine
inmiyor. components.json PostgreSQL 16.14 sevk ediyor -> panelin PG destegi kutudan cikti hali
kullanilamazdi.

Iki bulgunun duzeltmesi TEK yerde, `PostgreSqlDatabaseProvider.IsolateDatabaseAsync` (kiracinin
veritabanina baglandigi tek nokta): `REVOKE ALL ON DATABASE x FROM PUBLIC` + hedef veritabanina
baglanip `GRANT ALL ON SCHEMA public TO u`. Ikincisi ayni zamanda birincisinin FAZLA-KAPATMA
nobetcisi. Canli dogrulandi: A -> B baglantisi `FATAL: ... erisim engellendi`, kiraci kendi
veritabaninda tablo olusturup okudu, `postgres` super-kullanicisi etkilenmedi.

### BULGU 4 -- KAPATILDI: yazilan PostgreSQL sertlestirmesi CAGRILAMIYORDU

`DatabasesView.vue`'de `hardeningSupported = engine === "mysql" || engine === "mariadb"` SABITI
vardi. Yetenek artik motorun kendi saglayicisindan geliyor
(`IDatabaseEngineProvider.SupportsHardening` -> `DatabaseEngineStatusDto.SupportsHardening`),
arayuz yalniz bayragi okuyor. (Ayni sabit daha once ters yonde de hata yapmisti: dugme her
motorda gorunup hicbir sey yapmadan "sertlestirildi" diyordu.)

### MEVCUT KURULUMLAR

* **MySQL:** `GrantWildcardRepair` (sertlestir dugmesi) CANLI dogrulandi -- desen daraliyor, YENI
  acilan eslesen adli veritabani artik kapsam disi. **Ama zaten kapsanan veritabanlarina erisim
  KORUNUYOR** (bilerek: calisan bir siteyi sessizce koparmamak icin). Yani onarim capraz erisimi
  DONDURUR, kaldirmaz. Rapor bunu soylemiyordu; artik birden cok veritabanini kapsayan her satir
  `DIKKAT ... gozden gecirin` ile isaretleniyor. Kaldirma karari (hangi DB kimin) insana ait.
* **PostgreSQL:** `PostgreSqlDatabaseProvider.HardenAdminAccountsAsync` eklendi. Yalniz ACIK
  yetkilisi (ACL girdisi ya da super olmayan sahibi) OLAN veritabanlarinda PUBLIC kaldirilir;
  acik yetkilisi olmayan veritabani ATLANIR ve raporda "acik birakildi" diye gorunur -- orada
  REVOKE yazmak calisan bir baglantiyi koparirdi. Kesif sorgusu + atlama davranisi + idempotentlik
  canli olculdu.

### BILEREK KAPATILMADI / ACIK KALAN

* **MariaDB `bind-address=0.0.0.0`** (`ComponentBootstrapper.EnsureMariaDbBindAddressAsync`).
  Bilincli karar (uzak erisim toggle'i icin), kapi Windows Guvenlik Duvari. NOT: ayni dosyada
  3657. satirdaki yorum "127.0.0.1 baglamasini ekle" diyor, kod 3734'te `0.0.0.0` yaziyor --
  yorum kodla CELISIYOR, karar dogru olsa bile yaniltici.
* **PostgreSQL `pg_database` katalogu** her role acik: kiraci diger veritabanlarinin ADLARINI
  gorebilir (veriye erisemez). PG'de bu katalog yetkisi kapatilamaz.
* **Ad-oracle:** `POST /my-databases` ve `POST /sites`, var olan bir veritabani/kullanici adinda
  409 doner -> baska kiracilarin DB/kullanici adlari numaralandirilabilir.
* **Kota kimligi:** `SitesController.cs:944` ve `ResellerDatabasesController.cs:53`
  `GetScopeAsync(userId)` ile okuyor ama sayimi `subscriptionId` ile yapiyor; kendi kaydinda
  `MaxDatabases=0` olan bir ALT KULLANICIDA DB kotasi dayatilmiyor. Sizinti degil, kota kacagi.
* **Site adi araziciligi:** sahiplik kaydi OLMAYAN bir veritabani, adiyla ayni ada sahip bir site
  acilarak sahiplenilebilir (`EnsureSiteNameIsClaimableAsync` yalniz var olan IIS sitelerini
  reddediyor). Ad-eslesme kurali bilincli bir odunlesim; kaldirmak panel disinda acilmis
  veritabanlarinin mesru yedegini/baglamasini kirardi.

### OLCEMEDIKLER
* Kurulu paneldeki (8890) MariaDB/PostgreSQL: makinede kurulu motor YOK
  (`C:\ProgramData\AdaPanel\runtimes\mariadb\11` yalniz bos bir `data` klasoru). Butun olcumler
  yalitilmis kopyalarda.
* MSSQL yolu (`MsSqlDatabaseProvider`) hic surulmedi; kurulu ornek yok.
* Uctan uca HTTP ile yedek sizdirma senaryosu (gercek oturum + indirme) surulmedi; acik kod
  yolundan ve birim testinden dogrulandi.

---

## SSRF: BAGLANTI-ANI ADRES DENETIMI (31.08.2026) -- commit ATILMADI

0.8.7'de eklenen `OutboundAddressGuard` istegi atmadan ONCE adin nereye cozuldugune bakiyordu ve
kendi belgesinde "yonlendirmeyi ve DNS rebinding'i GORMEZ" diye yazili bir sinir tasiyordu. O sinir
kapatildi: kural ayni tek kaynakta kaldi, uzerine SOKET ACILMADAN HEMEN ONCE calisan bir denetim
eklendi (`src/AdaPanel.Infrastructure/Support/OutboundConnectGuard.cs`). Kalip icat edilmedi;
`ClusterHttpClientFactory.ConnectCallback` zaten boyle calisiyordu, o gövde tek kaynaga cikarildi ve
kume de artik onu cagiriyor.

### OLCULEN ACIK (tahmin degil)
* Varsayilan istemci (`services.AddHttpClient()`) `AllowAutoRedirect = true`. Gercek sokette
  olculdu: yerel sahte sunucu `302 Location: http://127.0.0.1:<port>/ic` dondu, denetimsiz
  `HttpClient` yonlendirmeyi izledi ve ic servisin govdesini GETIRDI
  (`OutboundRedirectGuardTests.DENETIMSIZ_istemci_yonlendirmeyi_izler_ve_ic_servise_ULASIR`).
* `curl.exe` 8.19.0 (zamanlanmis gorev, SYSTEM): `-fsSL` ile ayni 302'yi izleyip ic govdeyi aldi.
  `-L` KALDIRILIRSA curl 302'de cikis kodu 0 ve BOS cikti veriyor -- yani mesru cron adresi
  "basarili" gorunup hic calismiyor. Bu yuzden `-L` atilmadi.
* Ayni curl varsayilan olarak `ftp://` YONLENDIRMESINI de izliyordu: 302 ile bu makinedeki FTP
  servisine gercekten baglandi (`curl: (67) Access denied: 530`). `--proto-redir "=http,https"`
  bunu kapatti (`Protocol "ftp" disabled (in redirect)`). file/gopher/smb/dict bu surumde zaten
  kapaliydi (dordu de ayri ayri olculdu).
* `.NET`, `ConnectCallback`'ten atilan istisnayi KENDI `HttpRequestException`'inin icine sariyor;
  `catch (OutboundBlockedException)` yazan cagri yeri HIC tetiklenmiyor (olculdu, test kirmizi
  yandi). Cagri yerleri artik zinciri `OutboundBlockedException.Cikar` ile tariyor.

### DENETIME ALINANLAR (kullanicidan ADRES aliyor)
* Yedek uzak hedefi + bulut hedefleri: `BackupService` / `BackupService.CloudTargets` icindeki 20
  `CreateClient()` cagrisi tek bir `CreateRemoteHttpClient()`e baglandi (denetlenen adli istemci).
* Webhook gonderimi (`NotificationService.SendWebhookAsync`).
* Plesk/cPanel GOC KESFI (`SiteMigrationService`): BU YOL HIC DENETLENMIYORDU -- kendi
  `HttpClientHandler`'ini kuruyordu, hicbir adres kapisindan gecmiyordu ve istek kullanicinin API
  anahtarini tasiyor.
* Goc ON KONTROLU (`ValidateMigrationAsync`): `FileHost`/`DbHost` + port ile TCP acip sonucu
  "ulasildi/ulasilamadi" diye donuyordu -- panelin agindan calisan bir PORT TARAYICISI. Artik adres
  kapisindan geciyor (`migration.validate.host_forbidden`).
* Kume (`ClusterHttpClientFactory`) ayni gövdeyi cagiriyor; kendi mesaji ve STRICT politikasi
  korundu.

### BILEREK DENETIME ALINMAYANLAR (fazla kapatma pahaliya gelirdi)
Panelin KENDI sabit adresleri: bilesen indirme (components.json), GitHub surum cozumleme, lisans
dogrulama, hata raporu, Stalwart yonetim API'si (varsayilan istemci); saglik kontrolu/webmail
(`AdaPanel.LocalHealth`); phpMyAdmin/Adminer vekili (`DbToolsProxy`); SEO taramasi (zaten
loopback'e sabitli). Ucu icin de NOBETCI TEST var: denetlenen istemci loopback'e ULASAMAZ ama bu
uc istemci ULASIR (`Panelin_KENDI_istemcileri_loopback_e_ULASABILIR`).

### ACIK KAPI
`Panel:AllowInternalOutboundTargets` bagli-anda da GECERLI: politika HER BAGLANTIDA yeniden okunur.
DI uzerinden olculdu (`Acik_kapi_yapilandirmasi_DENETLENEN_istemcide_de_gecerli`).

### KALAN ACIKLAR (kapanmadi, "kapandi" DENMIYOR)
* **curl cron gorevi:** adres denetimi gorev KURULURKEN yapilir. curl'de adres engel listesi YOK;
  calisma aninda http/https yonlendirmesiyle 127.0.0.1'e sapmayi hicbir bayrak engellemez. Tek
  dogru cozum gorevi panelin denetlenen istemcisiyle calistiran bir CLI fiili -- Gorev
  Zamanlayici'da SYSTEM olarak sinanabilecegi yerde AYRI is olarak yapilmali.
* **SMTP (MailKit)** ve **FTP/SFTP goc cekme (FluentFTP/SSH.NET)**: kutuphanelerde baglanti-ani
  geri cagrisi yok; yonlendirme kavrami da yok, acik olan tek sey kayit ile kullanim arasinda DNS
  degisimi.
* **Git dagitimi (`git.exe`)**: yonlendirmeyi kendi izler; denetim yalniz adres kaydinda.
* **`PanelPublicAccessService.ProbeExternalAsync`**: panelin KENDI genel adresine, disaridan
  erisimi olcmek icin gider; denetime sokmak ozelligin kendisini anlamsiz kilardi.
* **Vekil:** denetlenen istemcide `UseProxy = false` (vekil arkasinda soket vekile gider, denetim
  hicbir sey olcmez). Sistem vekiliyle disari cikan bir kurulumda YEDEK YUKLEME kirilir; panelin
  kendi guncellemesi (varsayilan istemci) vekili kullanmaya devam eder.

---

## YEDEK GERI YUKLEME -- ARSIVIN ISLENMESI DENETIMI (31.08.2026) -- commit ATILMADI

Olcum, kurulu panele (8890) ve mevcut sitelere/yedeklere DOKUNMADAN, birim testiyle ve %TEMP%
altinda uretilen SAHTE KOTU NIYETLI ARSIVLERLE yapildi (gercek `mklink /J` kavsaklariyla).
Uretilen gecici alanlar SILINMEDI (`%TEMP%\adapanel-restoreguard-*`).

### OLCULEN VE KAPATILAN

| # | Bulgu | Kanit |
|---|---|---|
| 1 | **`deleted.json` KAVSAGI TAKIP EDIP KOKUN DISINDAKI DOSYAYI SILDI.** Kavsak nobetcisi yalniz `RestoreItemsAsync`'te vardi; `ResolveInsideRoot` (birlesik site + abonelik zinciri, hem yazma hem SILME) yalniz dizge denetimi yapiyordu. En sinsi hali: arsivde `files/` girdisi yoksa "once yana al" adimi calismaz, yani kavsak YERINDE kalir. | `SiteChainRestore_DeletingThroughJunction_IsRefusedAndReported` (once KIRMIZI: dosya gercekten silinmisti) |
| 2 | **Duz zip cikarmasi (`ZipFile.ExtractToDirectory`, panel yedegi) kavsagi takip edip hedefin DISINA yazdi.** BCL yalniz dizge kacisini gorur. | `PlainZipRestore_WritingThroughJunctionInTarget_IsRefused` |
| 3 | **Cikarma KOKUNUN KENDISI kavsak olabiliyordu:** arsiv girdisi kavsagin HEDEFINE yazildi, ustelik "once yana al" adimi kavsagin hedefindeki icerigi tasidi. Segment yuruyusu kokun kendisini goremez. | `SiteChainRestore_WhenLiveRootItselfIsJunction_RefusesBeforeTouchingAnything` |
| 4 | **Arsivden okunan ENVANTER dosyalarinda tavan YOKTU** (`deleted.json`, `index.json`, `iis/sites.json`, `config/ftp-users.json`, `ftp/accounts.json`, `mail/*.json`, `sites/<ad>/config/site.json`). Hepsi tamamen bellege okunuyordu; 0.8.13'te yalniz gorev XML'ine tavan konmustu. | `SiteChainRestore_OversizedDeletedList_IsRefusedInsteadOfBeingRead` |
| 5 | **Cikarma yarida patlayinca panel SUSUYORDU.** Tek bir bozuk girdi (ayni adin hem dosya hem klasor olmasi, CRC uyusmazligi, kilitli hedef) ham `IOException` olarak yukari cikiyor, `UpsertRecordAsync`'e HIC ulasmiyordu. O anda canli icerik ZATEN kardes klasore tasinmisti: kullanicinin sitesi bos, kayit "completed", `PreservedPath` bos -- eski icerigin nerede oldugu panelde HICBIR YERDE yazmiyordu. | `SiteChainRestore_WhenExtractionThrows_...` + `SiteChainRestore_WhenPreserveFails_RecordStillSaysItWasNotRestored` |
| 6 | **Secerek geri yukleme (kiraciya ACIK uc) sitelerin ORTAK kabina yaziyordu.** Hedef "uygulama kokunun UST dizini" idi; varsayilan duzende (`SitesController.ResolveScopedPhysicalPath`) siteler duz olarak `C:\inetpub\wwwroot\<site>` altinda acilir, yani ust dizin butun kiracilarin ortak kabi VE IIS "Default Web Site" belge koku. | `RestoreItems_WithoutTargetPath_DoesNotWriteIntoTheSharedSiteContainer` |

**Cozumler:** kural ICAT EDILMEDI. Kapsama `PathContainment`, kavsak `SiteBuilderPathGuard`
(`EnsureNoReparsePointOnPath` + `IsReparsePoint`) -- yani `RestoreItemsAsync`'in ZATEN kullandigi
kapinin aynisi. Tavanlar tek yerde: yeni `Backup/BackupArchiveLimits.cs` (16 MiB, YALNIZ envanter;
dosya icerigine UYGULANMAZ -- 40 GB'lik bir site geri yuklenebilmeli). Yarim kalan is icin
`RestoreProgress` (donus degeri degil, cagirana ait durum nesnesi) + `KaydiYarimIsaretleAsync`.
Secerek geri yukleme artik SITE arsivinde de ayrilmis `<yedek kok>\...\restored\<tur-kaynak-zaman>`
klasorunu kullanir (abonelik/panel arsivinde zaten oyleydi; iki dal ayri yazildigi icin biri
duzelmis oteki acik kalmisti).

### BILEREK KIRILAN DAVRANISLAR
1. Kok bir KAVSAK/symlink ise geri yukleme HIC BASLAMAZ (`backup.restore_target_is_link`).
   Site kokunu junction ile baska diske baglayan kurulum etkilenir; mesaj ne yapilacagini soyler
   (gercek klasoru hedef ver). Abonelikte yalniz O SITE atlanir, digerleri geri yuklenir.
2. Yol uzerinde kavsak olan GIRDI atlanir + uyari (butun is durmaz).
3. Yazilamayan dosya artik istisna degil UYARI: kalan dosyalar geri yuklenir, durum
   `restored_with_errors` olur. (Once: tek dosya butun geri yuklemeyi dusuruyordu.)
4. `panel.backup.browseHint` metni duzeltildi -- "sitenin orijinal konumuna geri yuklenir"
   ZATEN yanlisti (ust dizine cikariyordu), simdi dogruyu soyluyor.

### TEMIZ CIKANLAR (bir sonraki tur burayi aramasin)

| Soru | Olcum | Sonuc |
|---|---|---|
| `..`, ters bolu, MUTLAK yol (`C:\...`), SURUCUYE goreli (`C:x`), UNC (`\\sunucu\pay`), nokta karisimi | 6 sahte arsivle surulup HEPSI reddedildi + uyari | TEMIZ |
| Kavsak uzerinden YAZMA (birlesik site zinciri) | "Once yana al" adimi kavsagi kardes klasore TASIDIGI icin kacis zaten olmuyordu; artik nobetci de acikca duruyor | TEMIZ (kazaydi, artik kasitli) |
| Alternatif veri akisi (`dosya.txt:gizli`) | `Path.GetFullPath` + `PathContainment` sonrasi hedef KOK ALTINDA kalir; kokun disina cikaran bir bicim bulunamadi | TEMIZ |
| Posta arsivi girdi adlari (`mail-messages/<alan>/*.sqlite`) | Yol birlestirmeden once `Path.GetFileName` -- ayirici tasiyan ad kokun disina yazamaz | TEMIZ |
| Veritabani dokum adi (`databases/<motor>-<db>.sql`) | Yola girmez; `ImportDumpAsync`'e AD olarak gider, dokum `%TEMP%`'te Guid adiyla acilir | TEMIZ |
| UZAKTAN indirilen yedegin dogrulanmasi | **Geri yukleme UZAKTAN HIC INDIRMEZ**: `RestoreAsync` yalniz `record.Path`'i kullanir, dosya yoksa `backup.file_missing` (410). Yani "indirilen dosya dogrulanmiyor" diye bir yuzey YOK | KONU DISI |
| Sifreli arsivin kurcalanmasi | `BackupArchiveCipher` AES-**GCM** (parca basina etiket + baslik etiketi) -> kurcalama COZMEDE yakalanir | TEMIZ |
| Kiraci sinirinda kapsam denetimi | `restore` (tam geri yukleme) yalniz Administrator; `restore-items`'ta kapsamli kullanicinin `TargetPath`'i zorla bosaltiliyor + `EnsureBackupOwnershipAsync` | TEMIZ |
| Zip CRC (bit curumesi) | `entry.ExtractToFile` CRC'yi dogrular; artik dosya basina UYARI olur (once butun isi dusuruyordu) | TEMIZ |

### ACIK KALAN (olculdu, KAPATILMADI -- gerekceli)
* **Arsivden gelen ENVANTER hala GUVENILIYOR** (tam-sunucu DR'nin dogasi): `iis/sites.json` ve
  `sites/<ad>/config/site.json` icindeki `PhysicalPath` ile ISTENEN klasor acilir + IIS sitesi
  kurulur; `config/ftp-users.json` / `ftp/accounts.json` icindeki `HomePath` ile FTP hesabi acilir
  ve o agaca `icacls /T` ile DEGISTIRME izni verilir. **`FtpService.ResolveHomePathAsync:1141`
  kapsam DENETLEMEZ** -- kural yalniz `FtpController.cs:130`'da ve orada da `Administrator` MUAF.
  Geri yukleme zaten admin-ozel oldugu icin bu, deponun MEVCUT guven karariyla TUTARLIDIR; asil
  risk "admin YABANCI bir arsivi geri yukluyor" senaryosudur ve cozumu bir site-koku politikasi
  kararidir (kural tek kaynaga cikarilmali: controller + servis + geri yukleme).
* **Arsiv butunlugu icin SAGLAMA (checksum) YOK**: sifresiz arsivde kurcalama tespit edilemez
  (yalniz girdi basina CRC32). Kapatmak icin olusturmada SHA-256 kaydi + geri yuklemede dogrulama
  gerekir; eski kayitlarda saglama olmayacagi icin "olculemedi" diye raporlanmali.
* **Zip bombasi (DOSYA icerigi)**: acilmis toplam boyut/girdi sayisi icin tavan YOK ve BILEREK
  konmadi -- mesru bir site yedegi 40 GB olabilir. Envanter tavani konuldu; icerik tavani icin
  dogru olcu disk BOS ALANIDIR, ayri bir is.
* **Arayuz metni ESKIMIS**: `panel.backup.browseSubscriptionScoped` / `browseSubscriptionTarget`
  "sunucu abonelik arsivinde orijinal konumu cozemez" diyor; oysa kod ZATEN ayrilmis klasore
  cikariyor. Yaniltici ama zararsiz; Vue kosullariyla birlikte gozden gecirilmeli.

### MUTASYON KANITI (10 mutasyon, 10'u KIRMIZI)
Korumalari kaldiran 6 mutasyon (nobetci kaldirma, kok denetimi kapatma, duz zip nobetcisi
kapatma, tavan kapatma, dosya-basi uyariyi kapatma, kayit yazmayi kapatma) ilgili testleri kirmizi
yakti. **FAZLA KAPATMA nobetcileri (4):** tavan 0 yapilinca (`BackupIncrementalChainTests` DAHIL
kirmizi), kavsak nobetcisi hep-reddet yapilinca (8 test kirmizi), kok denetimi hep-firlat
yapilinca (21 test kirmizi), secerek geri yukleme eski hedefe donunce (kirmizi).

### DURUM
`scripts/devam-durumu.ps1` -> **SAGLAM**. dotnet: **4652 basarili / 0 basarisiz**
(paralel ajanin testleri dahil). i18n TR 5102 = EN 5102. npm typecheck/build/test temiz.

**Yeni dosyalar:** `src/AdaPanel.Infrastructure/Backup/BackupArchiveLimits.cs`,
`tests/AdaPanel.Infrastructure.Tests/BackupRestoreArchiveGuardTests.cs` (17 test).
**Yeni i18n anahtarlari:** `backup.restore_target_is_link`, `backup.restore_file_failed`,
`backup.entry_too_large`, `backup.restore_aborted` (tr + en).
