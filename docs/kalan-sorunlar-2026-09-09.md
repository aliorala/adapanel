# 0.8.31 — Kalan sorunların düzeltilmesi

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
- Ön yüzün 149 dosyası publish çıktısıyla SHA256 üzerinden eşleştirildi; fark yok. 0.8.31 paketlemesi sürüyor; canlı yayın sonucu aşağıya eklenecek.

## Kullanıcıya bağlı nokta

Canlıda yedek şifrelemesi kapalı. Kullanıcıdan parolayı **sohbete yazmadan**, panelde belirleyip panel dışında saklayarak kendisinin etkinleştirmesi istendi. Kullanıcı yanıtı/işlemi olmadan parola oluşturulmadı, ayar değiştirilmedi. Şifrelenmemiş eski arşivler bu kod değişikliğiyle geriye dönük şifrelenmiş sayılmaz.

Canlıdaki önceki FTP 421 kaydı bir veritabanı arşivine ait; yerel kopyası mevcut. Sunucunun FTP karşılama yanıtı yeniden okunabildi (220). Bu yalnız bağlantı kanıtıdır; eski arşivin yeniden yüklenmesi veya yeni sürümde gerçek aktarım başarısı olarak sayılmaz.
