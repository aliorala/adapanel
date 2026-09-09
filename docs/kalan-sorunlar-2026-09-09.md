# 0.8.31–0.8.32 — Kalan sorunların düzeltilmesi

Kullanıcının devir notlarında kalan sorunları da düzeltme isteği üzerine çalışıldı. Yedekleme modülü korunuyor; mevcut canlı yayın izni geçerli.

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
