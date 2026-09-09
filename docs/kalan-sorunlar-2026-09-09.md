# 0.8.31–0.8.36 — Kalan sorunların düzeltilmesi

Kullanıcının devir notlarında kalan sorunları da düzeltme isteği üzerine çalışıldı. Yedekleme modülü korunuyor; mevcut canlı yayın izni geçerli.

**Son durum (09.09.2026): 0.8.36 canlı.** Son tam test koşusu 5.260 .NET, son ön yüz 58 test; canlıda iki yeni iptvhub yedeği yerel ve FTP olarak doğrulandı. Son 62 kayıt doğrulamasında erişilemeyen/açılamayan arşiv yok. Eksik 18 uzak kopya geçmişte yereli de silinmiş kayıtlardır; geri oluşturulmuş sayılmaz. Ayrıntılı kronolojik çalışma ve doğrulama kayıtları aşağıdadır.

## Güncelleme sırasında servis dosyası kilidi

0.8.29 ilk denemesinde görülen yarış yeniden incelendi: kurulum `taskkill /IM AdaPanel.Api.exe` ile süreci kapatıyor, SCM kurtarması 5 saniye sonra hizmeti yeniden başlatabiliyordu. Sadece yeniden durdurmak veya kurtarma eylemini değiştirmek kuyruktaki yeniden başlatmayı iptal etmez. [Microsoft'un ChangeServiceConfig2 açıklaması](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-changeserviceconfig2a) bu durumda hizmetin geçici olarak devre dışı bırakılması gerektiğini belirtiyor.

- `scripts/update-service-guard.ps1` kurulumun geçici dizinine çıkarılıyor. İlk durdurmadan önce eski başlangıç türünü ve gecikmeli başlangıç değerini saklıyor; ikinci durdurma eski değeri ezmiyor.
- Hizmet başlangıcı dosya değiştirme süresince `Disabled`. Normal durdurma süresi sınırlı; gerekirse yalnız hizmet kaydındaki **tam exe yoluyla eşleşen** süreçler kapatılıyor. Aynı isimli başka kurulumlara dokunulmuyor.
- Hizmet durmadan veya exe üzerindeki kilit kalkmadan `wwwroot` taşınmıyor ve dosyalar değiştirilmiyor. Başarısız durdurma kurulumu kesiyor. İptal/hata yolunda başlangıç ayarı geri yüklenip panel tekrar başlatılıyor; başarılı kurulumda da önceki başlangıç ayarı geri yükleniyor.
- Güncelleme çalıştırıcısının `Stop-Service` çağrısı, sınırlı bekleme döngüsüne ulaşmayı engelleyebiliyordu. Artık durdurma isteği `ServiceController.Stop()` ile gönderiliyor ve bekleme ayrı sınırlandırılıyor.
- Windows PowerShell 5.1'de gerçek yardımcı betik, servis/süreç sınırları taklit edilerek çalıştırıldı: ikinci durdurma, zorla durdurma, yetki hatası, başka kurulum ve Automatic/Manual/Disabled geri dönüşleri. Gerçek Inno Setup derleme testi de var. Bu yerel testler gerçek SCM çökmesi oluşturulmuş gibi sunulmaz.

## FTP aktarımı ve eski yedek kayıtları

- Geçici 421/425/426/450/451/452 ve geçici bağlantı hatalarında en fazla üç aktarım denemesi, 1 ve 2 saniye bekleme. Aynı yerel arşiv aynı hedefe baştan yazılır; ekleme veya yeni bir yedek üretme yok. 530/550 kimlik/izin hataları tekrar edilmez. Başarısız aktarımda yerel arşiv korunur ve hata kaydı devam eder.
- Yüklemede `KeepAlive=false` ve pasif kip açıkça verilir. Asenkron FTP çağrıları yalnız `Timeout` özelliğine bırakılmaz: klasör hazırlama, listeleme, silme ve yüklemede gerçek iptal/zaman aşımı bağlantıyı `Abort` ile kapatır. Yüklemede toplam süre ve ilerleme duraklaması ayrı sınırlıdır.
- Bağlantı testinin iptali genel hata sonucuna çevrilmiyor.
- Canlıda üst gün klasörü silinmiş 18 eski kayıt sürekli `remote_delete_failed` gösteriyordu. Başarısız alt dizin listelemesinden sonra üst dizinlere bakılıyor. Yalnız başarılı bir üst listeleme ilgili klasörün yokluğunu kanıtlarsa `Missing`; klasör varsa veya listelemeye erişilemiyorsa `Unreachable`. Sadece 550 metnine bakarak kayıt kapatma yok.
- Gerçek yerel TCP/FTP düzeneğinde iki 421 sonrası başarı ve sunucunun aldığı baytların yerel arşivle eşitliği ölçüldü. Sürekli 421, 530/550, asılı MKD iptali, cevapsız sunucunun zaman aşımı ve yeniden deneme sırasında iptal testleri eklendi. Üst klasör yok/var ve erişim reddi karşı kontrolleri var.

## Test uyarıları

Testlerdeki bloklayan `.GetAwaiter().GetResult()` / `.Wait()` işlemleri `await` ve süre sınırlı `WaitAsync` ile değiştirildi. Eksik null kontrolleri, gereksiz async işaretlemesi ve xUnit assertion uyarıları giderildi; uyarılar topluca bastırılmadı.

## Doğrulama ve yayın durumu

- Hedefli son koşu: **65/65 geçti**, atlanan test yok (`.tmp/remaining-fixes/targeted-final.trx`). İlk koşudaki iptal testi gerçek bir eksik yakaladı; düzeltildikten sonra geçti.
- Ön yüz `typecheck` ve `build` başarılı (`.tmp/remaining-fixes/frontend.log`).
- Tam .NET koşusu: **Infrastructure 5.235/5.235 + Core 2/2 = 5.237 başarılı**, başarısız/atlanan yok. Altyapı koşusu 3 dakika 37 saniye; derlemede **0 uyarı / 0 hata**. Kanıtlar: `.tmp/remaining-fixes/infrastructure-final.trx`, `core-final.trx` ve ilgili günlükler.
- Ön yüz testleri: **8 dosyada 58/58 başarılı** (`.tmp/remaining-fixes/frontend-tests.log`).
- Kurulum yardımcısı solid-compressed paketin başına alındı ve yalnız bir kez çıkarılıyor. Son Inno değişikliği ardından mevcut 6 kurulum kontrolü ayrıca tekrar geçti (`installer-final.log`).
- Ön yüzün 149 dosyası publish çıktısıyla SHA256 üzerinden eşleştirildi; fark yok. 22 bileşen denetimi geçti, betiğin büyük paket istisnasındaki 3 bileşen atlandı. Publish ve son kurulum dosyası içerik denetimleri geçti (`package.log`).
- Kaynak commit'i `01ce3c9`. [0.8.31 yayımlandı](https://github.com/aliorala/adapanel/releases/tag/0.8.31); kaynak geçmişi açık depoya gönderilmedi. Paket 90.757.936 bayt, SHA256 `6D19478CA8863E05B20AFC41619D566EE5C9DEC3DA38129DC75854D630FFD4B8`. Yayınlanan dosya yeniden indirilerek yerel dosyayla eşleştirildi (`publish.log`); canlı panel de aynı özeti doğruladı.
- Canlı güncelleme ekranından 0.8.30 → 0.8.31 kuruldu; sayfa otomatik geri geldi ve mevcut/son sürüm 0.8.31, “Panel güncel” durumunu gösterdi. `last-attempt.json`: `succeeded`, çıkış kodu 0, `isFinished=true`, `isFailed=false`, `isDecidedByPanel=true`. Hizmet `Running / Automatic`.
- Canlı Inno günlüğünde servis korumasının iki `Stop` adımı 17:39:06'da, `Installation process succeeded` 17:39:47'de ve `Restore` adımı 17:40:27'de başarılı. Çalıştırıcının başlangıçta servis başlatma tekrarları oldu; sonrasında hizmetin çalıştığı doğrulandı. Tek kurulum denemesi başarılı; 0.8.29'daki gibi kod 5 / geri alma görülmedi. Günlüğün kapanması da doğrulandı.

## Kullanıcıya bağlı nokta

Canlıda yedek şifrelemesi kapalı. Kullanıcıdan parolayı **sohbete yazmadan**, panelde belirleyip panel dışında saklayarak kendisinin etkinleştirmesi istendi. Kullanıcı **“Şimdilik kapalı kalsın”** yanıtını verdi; ayar açık tercihi doğrultusunda kapalı bırakıldı. Bekleyen onay yok; yeni talep olmadan etkinleştirmeyin. Parola oluşturulmadı. Şifrelenmemiş eski arşivler bu kod değişikliğiyle geriye dönük şifrelenmiş sayılmaz.

Canlıdaki önceki FTP 421 kaydı bir veritabanı arşivine ait; yerel kopyası mevcut. 0.8.31'de otomasyon ekranından kayıtlı FTP hedefine gerçek test dosyası yazıldı; “Bağlantı başarılı — hedefe yazılabiliyor” sonucu alındı. Bu, eski arşivin yeniden yüklendiği anlamına gelmez. Geçmiş iş sonuçları başarılıymış gibi değiştirilmedi.

## Canlı doğrulamanın ortaya çıkardığı ek durum — 0.8.32

0.8.31 üzerinde 60 kayıt doğrulandı: 15 uzak kopyanın yokluğu kanıtlandı; 4 kayıt FTP sunucusunun **421 Could not create socket** yanıtı nedeniyle ölçülemedi ve kayıp sayılmadı. Arşiv açılamadı sayısı 0. Sunucunun geçici soket hatası yalnız yüklemeyi değil NLST doğrulamasını da etkiliyordu.

Bu nedenle sınırlı yeniden deneme listeleme, dosya silme ve boş dizin kaldırmaya da uygulandı. Ortak yeniden deneme kodu sonuç olarak dönen geçici FTP kodlarını da işler; 530/550 yeniden denenmez. Gerçek TCP sunucusunun iki 421 yanıtından sonra üçüncü bağlantıda listeleme/silme/boş dizin kaldırmayı kabul ettiği ve liste içeriğinin okunduğu ölçüldü. 530/550'nin tek denemede hatayı koruduğu da test edildi.

Ek düzeltme ardından **73 ilgili test geçti**, atlanan yok (`ftp-command-final.trx`). 0.8.32 tam koşusunda **Infrastructure 5.240/5.240 + Core 2/2 = 5.242 test geçti**, atlanan/başarısız yok, derlemede 0 uyarı/0 hata. Altyapı koşusu 2 dakika 52 saniye (`infrastructure-032.trx`, `core-032.trx`). Ön yüz yalnız sürüm bilgisiyle yeniden derlendi (`frontend-032.log`); işlevleri değişmedi, önceki 58 testlik başarılı ön yüz koşusu geçerli.

0.8.32 kaynak commit'i `c555ac4`. [Sürüm yayımlandı](https://github.com/aliorala/adapanel/releases/tag/0.8.32). Paket 90.758.804 bayt; SHA256 `087492DAC6C0BC69FEEF4749AD915DBA601CD5AF9F94AFB5748435E1648C0A2A`. Yayından yeniden indirilen dosya aynı özetle doğrulandı (`publish-032.log`). 149 ön yüz dosyası publish ile eşleşti; 22 bileşen denetimi geçti, 3 büyük paket betiğin istisnasıyla atlandı. Publish ve installer denetimleri başarılı (`package-032.log`). Son manifestler artık bu sürüme aittir.

## 0.8.32 son canlı doğrulama — 09.09.2026

- Yayın paketi yüklenirken yapılan ilk panel kontrolü geçici “.exe bulunamadı” sonucunu gösterdi. Herkese açık son sürüm API'sinde 0.8.32 ve tek AdaPanelSetup.exe varlığı doğrulandı; panelden yeniden denetleme sonra doğru sürümü aldı. Paket doğrulamasını atlayan bir yöntem kullanılmadı.
- Canlı panel paketi yukarıdaki SHA256 ile doğruladı. Mevcut yayın izni kapsamında arayüzden 0.8.31 → 0.8.32 kuruldu. Panel otomatik geri döndü; mevcut/son sürüm 0.8.32 ve “Panel güncel” görüldü.
- Hizmet `Running / Automatic`. `last-attempt.json`: `phase=succeeded`, `isFinished=true`, `isFailed=false`, `isDecidedByPanel=true`, `updatedAtUtc=2026-09-09T14:58:00.9743071Z`. `installerExitCode=null`; bu değer 0 olarak raporlanmadı. Terminalin kendi çıkış kodu 0, kurulumun çıkış kodu değildir.
- Inno günlüğünde iki `Stop` 17:57:36.102 ve 17:57:36.649'da başarılı; `Installation process succeeded` 17:58:12.030; `Restore` başarılı ve `Log closed` 17:58:36.780. Böylece yalnız panelin erken geri dönmesi değil, kurulumun tamamlanması da doğrulandı.
- Yeni sürümde **Yedekleri doğrula** çalıştırıldı: **60 kayıt denetlendi; 0 yerel dosya yok, 18 uzak kopya yok, 0 arşiv açılamadı, 0 kayıt ölçülemedi.** Önceki koşudaki 4 erişim hatası bu koşuda görülmedi. Eksik uzak kopyalar 17 Ağustos'tan 14 ve 28 Ağustos'tan 4 eski kayda ait. 0.8.31'de erişilemeyen 3 eski kayıt artık eksik olarak kanıtlandı; erişilemeyen diğer güncel kayıt da ölçülebildi.
- Bu işlem veri silmedi. Eksik uzak kopyalar yeniden oluşturulmadı ve eski başarısız veritabanı aktarımı yeniden gönderilmiş sayılmadı. FTP yetim taraması desteklenmediği için yapılmadı; arayüz bunu açıkça bildirdi. Gerçek site/veritabanı geri yüklemesi yapılmadı.
- Kullanıcı yedek şifrelemesini şimdilik kapalı bırakmayı seçti; ayar korunuyor, bekleyen onay yok. Sohbette parola istenmedi veya otomatik parola üretilmedi.

Devir dosyaları `CODEX_MEMORY.md`, `docs/calisma-durumu.md` ve bu belge güncellendi; önceki sürüm notları tarihsel bağlam olarak korundu. Kaynak geçmişi herkese açık GitHub deposuna gönderilmedi.

## Devam çalışması — 0.8.33

Kullanıcı “devam et” dedi. Canlı `config/backups.json` yalnız kimlik/ad/durum/yol/boyut/varlık alanlarıyla incelendi: 18 eski kayıt `deleted_remote_failed`, yerel yolları boş ve dosyaları yok. Bunlar önceki silme işlemlerinden kalmış kayıtlardır; “0 yerel dosya yok” sayacı onların yerel kopyasının bulunduğunu göstermiyordu. `cb1c201e630d4a709b16ff309007f325` kimlikli `mariadb-iptvhub-20260909000737.sql.zst` ise yerelde mevcut (16.075.861 bayt), ilk uzak aktarımı başarısız.

- Doğrulama sonucuna `PreviouslyDeletedLocal` eklendi; önceki silme işleminde kaldırılmış yerel kopyalar açıkça sayılır. Yerel yolu olmayan kayıt `OffsiteCopyLost` ile yerel kopyası varmış gibi sunulmaz; uzak kopya da kesin yoksa `AllCopiesMissing` olur. Kayıtlar otomatik silinmez.
- Yalnız yöneticiye, ilk FTP aktarımı başarısız sunucu yedeklerinde **Uzağa yeniden gönder** eklendi. Mevcut FTP hedefi onayda gösterilir; sunucu onaylanan adresin hâlâ kayıtlı hedef olduğunu kontrol eder. Kimlik ve hedef aynı ayar okumasından alınır. Abonelik kayıtları ve tamamlanmış/önceden silinmiş kayıtlar bu işlemde kabul edilmez.
- Yerel dosya mevcut yedek kökü altında, yeniden yönlendirme noktaları olmadan doğrulanır. İlk gün/kategori/kaynak yolu korunur. Dosya aktarım boyunca yazmaya/silmeye karşı açık tutulur; boyut ve kayıtlı SHA256 doğrulanır. Damgasız, değişmiş veya ölçülemeyen dosya gönderilmez. Sabit tek dosya hedefleri reddedilir.
- Aktarım başarıyla bittikten sonra aynı kaydın uzak adresi/durumu atomik güncellenir; yalnız aktarım hatası uyarısı kaldırılır. Kaynak dosya eksikliği gibi diğer sorunlar `created_with_errors` kalır. Yerel arşiv, kimlik, tarih ve hash korunur. Hata/iptalde eski kayıt ve yerel arşiv korunur; geçmiş gece işi başarılıymış gibi değiştirilmez.
- İlk hedefli koşuda test hedefinin eksik kullanıcı adı bulundu ve test düzeneği düzeltildi; ardından 52 ilgili test geçti. Ek eksik-arşiv uyarısı/rol kontrolleri dahil tam son koşu, paket ve canlı sonuçları aşağıya eklenecek.
- İlk tam koşu 5.251 testte 10 hata verdi: yeni uç için statik denetim kaydı kontrolü ve 9 PowerShell ortam hatası. Denetim kaydı controller'da mevcut kullanıcı/kaynak sahibi çözümüyle yazılacak şekilde düzenlendi; gerçek aktör/konu testi eklendi. Testlere muafiyet eklenmedi.
- PowerShell hatasının nedeni ölçüldü: Codex'in PowerShell 7 modül yolu .NET alt sürecindeki Windows PowerShell 5.1'e taşınıyor, `Get-FileHash` ve `Get-Acl` bulunamıyordu. Sadece ana kabuğu 5.1 seçmek çözmedi. Test/paket komutunun **yalnız süreç ortamında** `PSModulePath=C:\Windows\system32\WindowsPowerShell\v1.0\Modules;C:\Program Files\WindowsPowerShell\Modules` verildi; Python üzerinden başlatılan gerçek 5.1 alt sürecinde Get-FileHash/Get-Acl/Expand-Archive üçünün de çözüldüğü doğrulandı. Kalıcı kullanıcı/sistem ortamı değiştirilmedi. Son tam koşu bu ortamla `infrastructure-final-033.*` dosyalarındadır.
- Ön yüz son `typecheck`, 58/58 test ve `build` geçti (`frontend-033.log`). Canlı Chrome bağlantısı koptu; yeniden açılan sekmeye erişim de zaman aşımına uğradı. Kullanıcıdan bağlantıyı yenilemesi istendi; yerel doğrulama devam ediyor.
- Son tam koşu **Infrastructure 5.252/5.252 + Core 2/2 = 5.254 başarılı**, başarısız/atlanan yok; tam test derlemesinde uyarı/hata yok. Altyapı koşusu 6 dakika 9 saniye (`infrastructure-final-033.trx`, `core-033.trx`). Aynı kaynakta denetim kaydı ve PowerShell kontrolleri dahil tamamı geçti; ilk başarısız koşular kanıt klasöründe korundu.
- Kaynak commit'i `35e3b37`. Paket **0.8.33**, 90.763.158 bayt; SHA256 `CC925CDA64D3F543D22A1CA81B0AFD68CBA241F3FA0E044272E5382995D394B7`. 149 ön yüz dosyası publish ile eşleşti; 22 bileşen geçti, 3 büyük paket betiğin istisnasıyla atlandı. Publish ve installer içerik denetimleri geçti; Inno derleme 27,407 saniye (`package-033.log`). Mevcut manifestler artık 0.8.33 paketine aittir.
- Canlı Chrome bağlantısı daha sonra aynı yeni sekmede geri geldi; kullanıcıya bağlantı yenileme adımının artık gerekmediği bildirildi.
- [0.8.33 yayımlandı](https://github.com/aliorala/adapanel/releases/tag/0.8.33). Yayından yeniden indirilen dosya yerel paketle SHA256 üzerinden eşleşti (`publish-033.log`); canlı panel de aynı özeti doğruladı. Arayüzden 0.8.32 → 0.8.33 kurulumu başlatıldı.
- 0.8.33 canlı kurulumu tamamlandı: mevcut/son sürüm 0.8.33, “Panel güncel”, hizmet Running/Automatic. `last-attempt.json`: succeeded, installerExitCode=0, isFinished=true, isFailed=false. Inno Stop 18:34:23.207 ve 18:34:24.479; kurulum başarılı 18:35:05.344; Restore ve Log closed 18:35:21.786.

### Canlı onay kontrolünde bulunan arayüz hatası — 0.8.34

0.8.33'te gerçek başarısız yedeğin yeniden gönderme onayı açıldı. Ad ve FTP hedefi doğruydu; ancak eklenen çağrıda ortak ConfirmDialog'un varsayılan `danger` tonu/etiketi değiştirilmediğinden düğme **Sil**, uyarı **Bu işlem geri alınamaz** görünüyordu. Eylem henüz çalıştırılmadan **Vazgeç** ile iptal edildi; bu noktada arşiv gönderilmedi veya silinmedi.

Çağrıya açık `tone: neutral` ve `confirmLabel: panel.backup.retryRemote` eklendi. İşlem sürerken ortak pencerenin nötr çalışma etiketi kullanılır. Arka uç değişmedi; 0.8.33'teki 5.254 başarılı .NET testi bu mantığı kapsar. 0.8.34 ön yüz kontrolü/paket/canlı sonucu aşağıya eklenecek. 0.8.33'ün yayımlanmış paketi değiştirilmedi; düzeltme yeni sürüm olarak yayımlanır.

- 0.8.34 kaynak commit'i `cf28ae9`. Ön yüz `typecheck`, 58/58 test ve `build` tekrar geçti (`frontend-034.log`); arka uç işlevleri değişmediğinden tam .NET koşusu yeniden çalıştırılmadı.
- Paket **90.761.846 bayt**, SHA256 `A5D93F6B010C056FC8C7F6672CCB8607477D2645A89A29B6F312711A8310EAFA`. 149 ön yüz dosyası publish ile eşleşti. Bileşen denetimi, publish ve son installer içerik denetimi geçti; Inno 20,172 saniye (`package-034.log`). Mevcut manifestler artık **0.8.34** paketine aittir.
- Canlıda (arka uç 0.8.33) bekleyen arşiv filtrelenip masaüstü görünümü incelendi: uzun ad ve yeni yeniden gönderme düğmesi taşmıyor. Onay görünümü yeni paket kurulunca tekrar incelenecek.
- [0.8.34 yayımlandı](https://github.com/aliorala/adapanel/releases/tag/0.8.34); yeniden indirilen yayın paketi aynı SHA256 ile doğrulandı (`publish-034.log`). Canlı panel de aynı özeti doğruladı; 0.8.33 → 0.8.34 kurulumu arayüzden başlatıldı. 22 bileşen denetimi geçti, 3 büyük paket betiğin istisnasıyla atlandı.


### 0.8.34 canlı sonucu ve 0.8.35 devamı

- 0.8.34 canlı: panel mevcut/son sürüm 0.8.34 ve Panel güncel. Hizmet Running/Automatic. last-attempt succeeded, installerExitCode=0, isFinished=true/isFailed=false, updatedAtUtc=2026-09-09T15:45:28.8729941Z. Inno Stop 18:45:05.871 ve 18:45:06.418; Installation process succeeded 18:45:47.299; Restore 18:46:03.983; Log closed 18:46:04.514.
- Düzeltilmiş nötr onay gerçek ekran görüntüsünde incelendi: doğru arşiv/hedef, Uzağa yeniden gönder düğmesi, silme/geri alınamazlık uyarısı yok ve taşma yok. Aktarım denendi fakat **backup.retry_not_available ile aktarım başlamadan reddedildi**. Başarılı aktarım diye raporlamayın.
- Salt okunur canlı inceleme: eski `cb1c201e630d4a709b16ff309007f325` kaydında **ContentHash boş**. Yerel dosya 16.075.861 bayt; mevcut SHA256 `519710EBA644CDC85F77C263FE606EA08E3AA584B6399E215B713D3492DFCB4A`. Bu ölçüm oluşturma anının damgası değildir; kayda eklenmedi, geçmiş doğrulanmış sayılmadı. Durum created_remote_failed ve uzak adres boş kaldı. Eski dosya korunuyor.
- 0.8.35'te ön yüz yeniden gönderme uygunluğu kayıtlı hash ve boş uzak adresi de dikkate alır; damgasız eski kayıt için görünür açıklama gösterir. Arka uçta damgasız dosya reddi korunur.
- Manuel veritabanı yedeği formunda kayıtlı FTP kimliğini tekrar parola istemeden kullanma seçeneği yoktu. Yönetici DB ucuna açık `UseConfiguredFtpTarget` ve arayüze ayrı onaylı eylem eklendi. Güncel ayar tek okumada çözülür; tarayıcının onayladığı URL değişmişse döküm başlamadan reddedilir. Sabit dosya hedefi reddedilir, mevcut ağ adresi denetimi uygulanır. Parola sunucuda kalır; gelen isteğe yazılmaz, tarayıcının kimliği kullanılmaz. Varsayılan/manual hedef akışı ve otomasyon ayarları değiştirilmez.
- 0.8.35 yeni testleri gerçek FTP sunucusunda yeni dökümün aynı baytlarla gönderilmesi/hash kaydı, kayıtlı kullanıcı/parolanın kullanılması ve yanıtta sızmaması; değişmiş/boş/FTP olmayan/sabit dosya hedeflerinin dökümden önce reddi ve yönetici rolünü kapsar. Son koşu/paket/canlı sonuçları aşağıya eklenecek.

- 0.8.35 kaynak commit'i `63b3555`. Son hedefli koşu **67/67**, tam Infrastructure **5.258/5.258** (3 dakika 8 saniye) ve Core **2/2**, toplam **5.260 başarılı**, atlanan/başarısız yok. Hedefli derleme ve paket derlemesinde uyarı/hata yok. Ön yüz `typecheck`, **58/58** test ve `build` geçti (`backup-target-035.*`, `infrastructure-final-035.*`, `core-035.*`, `frontend-final-035.log`).
- 0.8.35 paket **90.763.234 bayt**, SHA256 `3734F574565EBC939D4BE5AE4A6F300B12168884E11756196870714DFDA14751`. 149 ön yüz dosyası publish ile eşleşti. 22 bileşen geçti, 3 büyük paket betiğin istisnasıyla atlandı; publish ve installer içerik denetimleri geçti. Inno 27,391 saniye (`package-035.log`). Son manifestler **0.8.35**'e aittir. Yayın betiği çalıştırıldı; canlı sonuç henüz bu satırda tamamlanmış sayılmaz.

- [0.8.35 yayımlandı](https://github.com/aliorala/adapanel/releases/tag/0.8.35). Yayından yeniden indirilen paket yerel dosyayla aynı SHA256 ile doğrulandı (publish-035.log). Canlı panel de aynı özeti doğruladı; 0.8.34 → 0.8.35 kurulumu arayüzden başlatıldı. Kaynak geçmişi açık depoya gönderilmedi.


### 0.8.35 canlı yeni veritabanı yedeği — başarı

- Canlı panel mevcut/son sürüm 0.8.35, Panel güncel. Hizmet Running/Automatic; last-attempt succeeded, installerExitCode=0, isFinished=true/isFailed=false, updatedAtUtc=2026-09-09T16:03:29.8120324Z. Inno Stop 19:03:07.842 ve 19:03:08.280; kurulum başarılı 19:03:40.841; Restore 19:03:48.592; Log closed 19:03:48.607.
- 0.8.35'te eski hash'siz kaydın açıklaması gerçek masaüstü ekranında incelendi: açıklama satır kırıyor, düğmeler taşmıyor ve kullanılamayan yeniden gönderme düğmesi yok.
- Yeni **Kayıtlı FTP hedefine yedek al** onayı, `mariadb: iptvhub` ve `ftp://ev.alioral.com.tr` ile doğru, nötr ve taşmasız. Eylem canlıda çalıştırıldı, parola tekrar girilmedi. **Yeni yedek başarıyla uzak hedefe kopyalandı**; bu eski arşivin yeniden gönderilmesi değildir.
- Yeni kayıt `d6eb5f54008e4c6fb00cbf5367c422c7`, `mariadb-iptvhub-20260909160457.sql.zst`, 16.083.356 bayt, created_remote_synced. Yerel yol `C:/ProgramData/AdaPanel/backups/2026-09-09/databases/mariadb-iptvhub/mariadb-iptvhub-20260909160457.sql.zst`; uzak yol `ftp://ev.alioral.com.tr/2026-09-09/databases/mariadb-iptvhub/mariadb-iptvhub-20260909160457.sql.zst`. Kayıtlı SHA256 ve canlı Get-FileHash sonucu aynı: `975E14E364F645B5CAF057859B92E0AF0E0CEA3D741D2460C6FCC14229811E33`. encrypted=false; tek uyarı backup.remote_unencrypted. Aktarım hatası/içerik eksikliği yok. Toplam kayıt 60 → 61; eski kayıt/dosya silinmedi.
- Uzak varlığın toplu doğrulaması ayrıca başlatıldı; ilk kayıt anındaki remoteState=unknown aktarımın yapılmadığı anlamına gelmez. Son toplu sonuç aşağıya eklenecek.

### Sonuç bildiriminde canlıda bulunan ek hata — 0.8.36

Canlı yeni yedek created_remote_synced ve yalnız backup.remote_unencrypted uyarılı olduğu halde `reportCreateResult` her uyarıyı eksik içerik gibi sunuyordu. Bildirimde warningsListTitle çevirisine name verilmediği için `{name}` kalıyordu. Önceki arama filtresi de yeni kaydı gizliyordu; gerçek kayıt, filtre genişletilince görüldü.

0.8.36 yalnız ön yüzü düzeltir: created_with_errors dışındaki uyarılar genel uyarı bildirimi alır; arşivde eksik öğe varmış gibi sunulmaz. Bildirime gerçek kayıt adı verilir. Yeni yedek sonrası arama/sayfa sıfırlanır ve yeni kaydın günü açılır. Typecheck, 58/58 test ve build geçti (`frontend-036.log`). Arka uç işlevi değişmedi; 0.8.35'in 5.260 başarılı .NET testi geçerli, tam .NET koşusu yeniden çalıştırılmadı. Paket/canlı sonucu aşağıya eklenecek.

- 0.8.35 toplu canlı doğrulama: 61 kayıt denetlendi; 0 yerel dosya yok, 18 kaydın yereli önceki silme işleminde kaldırılmış, 18 uzak kopya yok, 0 arşiv açılamadı, 0 kayıt ölçülemedi. Yeni d6eb5f54008e4c6fb00cbf5367c422c7 kaydı sorunlar listesinde yok. Eski 18 kayıt aynı 17 Ağustos/28 Ağustos kayıtları. FTP yetim taraması desteklenmiyor; boş liste temizlik kanıtı sayılmadı. 0.8.36 kaynak commit'i 6a6fa9a.

- 0.8.36 paket: 90.763.324 bayt; SHA256 6CED764AD5662C470FFD6C2D7016FA6B197B31D2D23CC7E193597575ED6F57AB. 149 ön yüz dosyası publish ile eşleşti. 22 bileşen geçti, 3 büyük paket betiğin istisnasıyla atlandı; publish/installer içerik denetimleri geçti. Inno 19,359 saniye (package-036.log). Son manifestler 0.8.36'ya aittir. Yayın betiği başlatıldı.

- 0.8.36 canlı sunucuda son paket SHA256 ile doğrulandı ve 0.8.35 → 0.8.36 kurulumu arayüzden başlatıldı. Son tam test koşusu 0.8.35 arka ucunda 5.260, son ön yüz koşusu 0.8.36'da 58 testtir; tam .NET testleri tekrar çalıştırılmış gibi raporlamayın.

- [0.8.36 yayımlandı](https://github.com/aliorala/adapanel/releases/tag/0.8.36); yayın dosyası yeniden indirilerek yerel paketle SHA256 üzerinden eşleşti (publish-036.log). Canlı panel otomatik geri döndü, mevcut/son sürüm 0.8.36 ve Panel güncel görüldü. Son sonuç-bildirimi kontrolü için eski kayıt adına filtre uygulanıp bugünün grubu kapatıldı; yalnız iptvhub için kayıtlı hedefte yeni yedek alma işlemi başlatıldı. Sonuç aşağıya eklenecek.

- 0.8.36 canlı akış kontrolü geçti: eski dosyaya göre filtre ve kapalı gün varken alınan yeni yedek sonrası arama boşaldı, gün açıldı, liste ilk sayfada yeni kaydı gösterdi. Bildirim Yedek oluşturuldu / uyarıları inceleyin metnini ve gerçek dosya adını taşıdı; eski içerik eksikliği iddiası ve name yer tutucusu yok. Ekran görüntüsünde yeni satır, ad ve uyarı bölümü taşmadan görüldü. Yeni kayıt c3de560c02774c9aac73cbcdc0a74cfc, mariadb-iptvhub-20260909161552.sql.zst, 19:16:04, Uzak hedefe kopyalandı, tek şifreleme kapalı uyarısı. Kayıt sayısı 61 → 62; iki yeni kopya da normal yedek kayıtları olarak korunuyor. Son 62 kayıt doğrulaması başlatıldı.


## Nihai canlı doğrulama — 0.8.36

- Hizmet Running/Automatic. last-attempt: succeeded, installerExitCode=0, isFinished=true, isFailed=false, isDecidedByPanel=true, updatedAtUtc=2026-09-09T16:14:59.3050639Z. Inno Stop 19:14:35.956 ve 19:14:36.441, Installation process succeeded 19:15:10.064, Restore ve Log closed 19:15:45.711. Kurulumun kapanışı da ölçüldü.
- Son yedek c3de560c02774c9aac73cbcdc0a74cfc, mariadb-iptvhub-20260909161552.sql.zst, 16.083.344 bayt, created_remote_synced. Kayıtlı/ölçülen SHA256 aynı: `6CF8FA78C8CAFDA2A8CE1F1C7068F1646B56972099980058680BB8A6C46BC8E5`. remoteState=present, remoteMissing=false, remoteVerifiedUtc=2026-09-09T16:17:19.7680604Z. encrypted=false; tek uyarı backup.remote_unencrypted. Önceki yeni d6eb5f54008e4c6fb00cbf5367c422c7 kaydı da present/remoteMissing=false.
- **Son toplu ölçüm: 62 kayıt; 0 yerel dosya yok; 18 kaydın yereli önceki silme işleminde kaldırılmış; 18 uzak kopya yok; 0 arşiv açılamadı; 0 kayıt ölçülemedi.** Eksik uzak kayıtlar yine 17 Ağustos'tan 14, 28 Ağustos'tan 4 kayıt. Bu işlem hiçbir arşivi/kaydı silmedi; eski boş yerel yolları olan kayıtlar geri kurtarılmadı. FTP yetim taraması yapılamadı; gerçek geri yükleme yapılmadı.
- Yedek modülü, şifreleme kapalı tercihi ve önceki kompakt görünüm korundu. CODEX_MEMORY.md ve docs/calisma-durumu.md üst bölümleri son kesin sonuca göre yenilendi; eski bölümler tarihsel bağlam olarak korundu. Kaynak kod açık depoya push edilmedi.
