# 0.8.31–0.8.41 — Kalan sorunların düzeltilmesi

## 2026-09-17 — 12 Eylül yedek grubunun kalma nedeni doğrulandı

Kullanıcının son sıraya bıraktığı bildirim canlı listeden incelendi. 12 Eylül grubu boş değil: **6 yedek / yaklaşık 90 MiB** var. Bunlar iptvhub (03:06:53), karsilastir (03:07:31), rsmc_alarmm (03:07:33), travian_bot (03:07:34), posta (03:07:36) ve panel (03:07:40). Hepsi 12.09.2026 sunucu yerel saati. Önceki aktif doğrulama bu kayıtlar dahil 51 kayıtta eksik yerel/uzak veya açılamayan arşiv bulmadı.

Canlı ayar 5 gün, günlük 03:00. Son iş 17.09.2026 03:06:56'da tamamlandı; özeti 3 eski yedeğin silindiğini gösteriyor. `BackupService.PruneRecordsAsync` seçim anında `DateTime.UtcNow.AddDays(-retentionDays)` kullanıyor; takvim günü değil **120 saat** hesabı. 12 Eylül turu 17 Eylül turundan biraz daha geç tamamlandığından, erken 3 site yedeği seçilirken sonraki 6 yedek o anda yaş sınırını geçmemişti. Bu öğeler başarısız silme kalıntısı değil, seçim dışında kalan gerçek arşivler. Gün klasörü içerik durduğu için kalıyor. Sonraki iş 18.09.2026 03:00; mevcut kurala göre o turda bu 6 kayıt süre bakımından silinmeye aday olacak. Son kullanılabilir yedek ve zincir korumaları ayrıca uygulanır; sonraki tur henüz gözlenmediğinden silinmiş sayılmıyor.

Bu incelemede dosya silinmedi, saklama politikası veya kod değiştirilmedi. Takvim günü hesabına sessizce geçmek bazı arşivleri 120 saat dolmadan sileceği için yapılmadı. Bu açıklama FTP'deki bütün kayıtsız/eski boş klasörlerin tarandığı anlamına gelmez.
## 2026-09-17 — Kapsamlı denetim, 0.8.41 canlı


Kullanıcı düzeltmesi (17.09.2026): alioral.xyz alan adı kapandı. DNS/SSL uyarıları panel hatası sayılmayacak; alan adı yeniden etkinleştirilmeyecek ve DNS/SSL değiştirilmeyecek. Joker sertifika için webmail özelliği engellenmedi; kök alan adı üretimi düzeltildi.

Kaynak commit: `3ede41a`. 49 altyapı modülü, 66 controller / 427 eylem, 60 Vue ekranı ve 840 kaynak dosyası envantere alındı. Ayrıntı, sınırlar ve modül listesi: [17 Eylül denetimi](denetim-2026-09-17.md).

- Dış komut iptal/timeout/stdin yönetimi, FTP hedef kökü sınırı, Node/Python/ters vekil junction yolları, site veritabanı ve SEO cevap sıralaması, 19 çeviri, UTC saatleri, görev olarak çalışan Stalwart sağlık durumu, terminal UTF-8/CLIXML, paket indirme süresi ve bağımlılıklar düzeltildi.
- SDK 8.0.425 / paket .NET 8.0.31. Son tam koşu 5.310 arka uç testi; 74 ön yüz testi, tür kontrolü, üretim derlemesi, WHMCS ve paket kontrolleri geçti. npm/NuGet açık sayısı 0. 22 bileşen Passed / 3 büyük paket mevcut politikayla Skipped.
- Sonradan canlıda webmailin durduğu bulundu: joker sertifikaya doğrudan `webmail.` ekleniyordu. Kök neden düzeltildi; `*.example.com` artık `webmail.example.com` üretir, joker desteği korunur. 34 ilgili test ayrıca geçti. IIS yedeği `AdaPanel-Webmail-20260917` alınıp tek geçersiz bağ kaldırıldı; webmail Started / AdaPanel-Webmail-OK, SSL yenilemesi 21:16:01 sunucu saatinde tamamlandı.
- 0.8.40 GitHub dosya yüklemesi `Error saving asset` ile başarısız; boş yayın taslak yapıldı, canlıya kurulmadı. Son paket 0.8.41, 90.795.576 bayt, SHA256 `30B445EBDD6E66EC417924D8CCBC4DA8E40262F240E9383277780FC148F86F31`. GitHub 504 yanıtına rağmen dosya uploaded durumunda tamamlandı; tekrar indirilip aynı SHA256 ile doğrulandı (verify-published041.log). Kaynak/history/tag push edilmedi.
- **Canlı sonuç:** mevcut/son sürüm 0.8.41; AdaPanel Running/Auto; installerExitCode=0, isFinished=true, isFailed=false, updatedAtUtc=2026-09-17T18:31:26.3707516Z; Inno hizmet Restore/Log closed 21:31:47.841. Stalwart sağlık ekranında Çalışıyor; webmail Started ve sağlık OK. Terminal Türkçe karakterleri doğru, CLIXML yok. Yayın betiği yeni sürümleri dosya yüklenene kadar taslakta tutacak şekilde düzeltildi; 12 ilgili test geçti. Betik/devir commit 420c923.
- Mevcut 3 IIS sitesi çalışıyor; 51 yedek kaydı, 17 Eylül 9 yeni uzak kopya. Günlük 03:00 / 5 gün saklama korunuyor. Yedekleme kalacak, şifreleme kullanıcı kararıyla kapalı; kompakt görünüm korunacak.
- Kurulum sonrası aktif yedek doğrulaması: 51 kayıt, 0 eksik yerel, 0 eksik uzak, 0 açılamayan arşiv, 0 ölçülemeyen kayıt. FTP yetim/kayıt dışı dosya taraması desteklenmiyor; bu sonuç eski kayıp arşivlerin geri üretildiği ya da gerçek geri yükleme yapıldığı anlamına gelmez.
- Son canlı UI kontrolü: .NET 8.0.31 / AdaPanel 0.8.41; kompakt ana sayfa kartlarında taşma yok. SEO 21:35:57 taraması tamamlanınca sırada bildirimi kalktı; denetim kaydı aynı yerel saati gösteriyor. Ana sayfa işlem adları Türkçe.
- Harici epey botlarında hata/uzun çalışma, site robots/sitemap eksikleri ve 6 üçüncü taraf bileşen güncellemesi ayrı operasyonel bulgular. Canlı veri silme/geri yükleme/göç ve her düğmenin bütün olası yolları sınanmış sayılmaz. Kaynak/history/tag public GitHub'a ASLA push edilmeyecek; yalnız mevcut yayın betiğiyle kurulum dosyası.

Kullanıcının devir notlarında kalan sorunları da düzeltme isteği üzerine çalışıldı. Yedekleme modülü korunuyor; mevcut canlı yayın izni geçerli.

**Son durum (17.09.2026): 0.8.41 canlı; bu tur 51 yedek kontrol edildi, eksik dosya veya açılamayan arşiv bulunmadı.** Önceki canlı doğrulamadaki iki yeni iptvhub yedeği ve 62 kayıt hakkındaki sonuçlar aşağıda korunuyor; eksik 18 uzak kopya geri oluşturulmuş sayılmaz.

## 2026-09-10 — 0.8.39 canlı; 50 eski boş FTP klasörü temizlendi

Kullanıcı, süresi dolan FTP yedeklerinin dosyaları silinirken boş klasörlerin kaldığını bildirdi. Önceki "doğrulanmış hata kalmadı" ifadesi bu yeni bulguyla güncellendi. Eski temizlik yalnız dosyanın doğrudan üstü tarih klasörü ise çalışıyordu; gün/kategori/kaynak düzenini atlıyordu. 8 regresyon düzeltmeden önce başarısız oldu (`ftp-folders-before-039.trx`).

- Bilinen yedek düzenindeki klasörler alttan gün klasörüne kadar temizleniyor; dolu klasörde duruyor. NLST 550 yanıtı boş sayılmıyor, yalnız boş dizin kaldırabilen RMD sonucu belirleyici. Dosya silme hatalarının dürüst raporlanması korunuyor.
- Eski boş klasörler için Windows yöneticisine özel yerel komut: `AdaPanel.Api.exe --cleanup-empty-ftp-backup-folders C:/ProgramData/AdaPanel`. Web sunucusunu açmadan, mevcut otomasyon hedefi/korunan kimlikleri ve pozitif saklama süresiyle çalışır. Yalnız eski tarih klasörleri ve bilinen alt düzen; dosya silen DELE yok. En fazla 2.000 dizin/5 dakika. Güncel günler, dolu dizinler, tanınmayan alt düzen ve hedef kökü korunur. Gizli bilgi çıktıya yazılmaz.
- İlk son hedefli koşu 104/104; bakım senaryolarıyla 106/106. Gerçek TCP/FTP sunucusunda NLST/RMD, boş NLST 550, başka dosyanın korunması ve kök sınırı ölçüldü. Siteye ait veritabanının ara klasörü de son tam koşuya dahil edildi. Ön yüz yalnız sürüm için yeniden derlendi; işlev değişmedi.
- Canlı saklama süresi 5 gün, hedef türü FTP. Tam koşu Infrastructure 5.284 + Core 2 = 5.286 başarılı (7 dakika 10 saniye, atlanan/başarısız yok). Sonrasında panel/mail/connection-test alt düzen sınırı daraltıldı ve korunan gerçek ayar parolasıyla bakım testi eklendi; son 107 ilgili test yeniden geçti. Paket/yayın, canlı kurulum ve geçmiş boş klasör temizliği tamamlandı. Kanıtlar: `infrastructure-final-039.trx`, `core-039.trx`, `ftp-folders-release-039.trx`, `frontend-build-039.log`. Yedek modülü ve kapalı şifreleme tercihi korunuyor. Kaynak/history/tag public GitHub'a push edilmeyecek.

0.8.39 kaynak commit'i **`4c18a44`**. Paket **90.766.562 bayt**, SHA256 **`C6164DE1FFAA680C723021E29578DAC75F4B325435034EC37942F115465F77FE`**. Publish/installer içerik denetimleri geçti; 149 ön yüz dosyası eşleşti, 22 bileşen Passed/3 büyük paket mevcut istisnayla Skipped. İlk paket koşusunda PHP yedek URL'sinin TLS bağlantısı ve phpMyAdmin indirmesi takıldı; aynı manifest URL'lerinden curl HTTP/1.1 ile tam arşivler indirilip ZIP işaretleri doğrulandı, mevcut paket betiği yeniden çalıştırıldı ve bütün denetimler geçti. İlk günlük `package-initial-039.log` korundu. Son Inno 19,094 saniye. [0.8.39 yayını](https://github.com/aliorala/adapanel/releases/tag/0.8.39) mevcut betikle yapıldı; yayınlanan dosya tekrar indirilip aynı hash ile doğrulandı (`publish-039.log`). Kaynak/history/tag push edilmedi. Canlı kurulum ve temizlik sonucu aşağıda kayıtlı.

**0.8.39 canlı sonuç:** panel mevcut/son sürümü 0.8.39 gösteriyor. AdaPanel Running/Automatic; last-attempt succeeded, installerExitCode=0, isFinished=true, isFailed=false, updatedAtUtc=2026-09-09T22:04:04.2671161Z. Inno Stop 01:03:41.732 / 01:03:42.326; Installation process succeeded 01:04:20.317; Restore ve Log closed 01:04:23.708 (10.09.2026 sunucu yerel zamanı).

Eski boş FTP klasörleri kayıtlı 5 günlük saklama süresine göre yerel bakım komutuyla temizlendi: **Removed=50, Unavailable=0, Visited=50**. İkinci tarama **Removed=0, Unavailable=0, Visited=0**, MaintenanceExitCode=0; hizmet Running/Automatic. **62 yedek kaydı korunuyor**. İlk PowerShell kontrolündeki `@(ConvertFrom-Json).Count=1`, tek dizi nesnesinin sayısıydı; değişkene alınan gerçek dizinin `.Count` değeri 62 olarak doğrulandı.

Canlı kanıtlar: `C:/ProgramData/AdaPanel/updates/ftp-folders-039.out.json`, `.err.log`, `.pid`, `ftp-folders-039-verify.out.json` ve `ftp-folders-039-verify.err.log`. İlk başlatıcı terminal isteği 32.010 ms'de zaman aşımına uğradı fakat bağımsız bakım süreci (PID 11432, başlangıç 01:05:30) çalışmaya devam etti ve sonuç dosyasını yazdı; bitişi ayrıca doğrulandı. Bu, temizliğin başarısız olduğu anlamına gelmez. Bakım komutu yalnız RMD uygular; dosya silmez, yedek geçmişini değiştirmez. Geçmişte kaybolmuş 18 uzak arşiv geri oluşturulmadı. Boş klasör taraması, FTP'de kayıtsız dosya/arşiv arama desteği değildir. Şifreleme kullanıcının tercihiyle kapalı kalır.
## 0.8.38 — Terminalin gerçek süre sınırı ve taslak koruması

`TerminalService` ana süreç çıkışını süreyle sınırlıyor fakat stdout/stderr okumalarını yalnız istek iptaline bağlıyordu. Alt sürecin boruları devraldığı gerçek Windows süreci testinde 2 saniyelik terminal sınırına rağmen 10 saniyede yanıt dönmedi (`terminal-before-038.trx`). Süre token'ı şimdi okuyuculara da bağlı; ana süreç ve iki çıktı birlikte gözleniyor. Terminalin seçtiği okuyucu kipi iptalden önce toplanmış çıktıyı koruyor. Diğer ProcessRunner çağrılarının varsayılan davranışı değişmedi. Süre dolunca çalışan ana süreç ağacına Kill gönderiliyor; sınırsız WaitForExit kaldırıldı. Ana süreç zaten çıkmışsa bağımsız arka plan işlerinin tamamlandığı/sonlandırıldığı varsayılmıyor.

Ön yüzde geciktirilmiş gerçek SFC setup akışlarıyla iki hata yeniden üretildi: yeni taslak boşaltılıyordu ve stderr varken zaman aşımı metni yoktu (`terminal-ui-before-038.log`, 2 başarısız/3 başarılı). Artık komut ve dizin yalnız gönderim anındaki değerle hâlâ aynıysa güncelleniyor; geçmiş kaydı gönderilen komuta ve sonuç dizinine ait kalıyor. Zaman aşımı metni stderr'e ekleniyor. Türkçe/İngilizce metin, bütün alt süreçlerin sonlandığını kesin olarak söylemiyor.

4 terminal testi normal çıkış kodu/iki çıktı, çalışan komutta süre sınırı, çağıranın iptali ve ana süreçten devralınmış boruları kapsıyor. İlk tam koşuda yeni yardımcı süreç yoğun yük altında 5 saniyede başlayamadı; boru senaryosu oluşmadığı için test anlamlı biçimde başarısız oldu. Süre testleri mevcut `Script host timing` koleksiyonuna alındı. Sonraki koşuda yine yardımcı açılışı gecikti ve kapanmakta olan süreç çalışma dizinini kilitli tuttu. Üretimde Kill sonrasına her denemede en fazla 2 saniyelik kapanış beklemesi eklendi. Testte 60 saniye çalışan yardımcı için 15 saniyelik komut sınırı ve 25 saniyelik dış test sınırı kullanılıyor; PID dosyasının oluştuğu ve kısmi çıktının korunduğu ayrıca şart. Bu, üretimdeki kullanıcının seçtiği sınırı değiştirmez. Yardımcı süreçlerin yalnız testin yazdığı PID'leri kapatılıp kapanışları bekleniyor. İlk koşuların kayıtları `infrastructure-initial-038.*` ve `infrastructure-second-038.*` olarak korunuyor. Ön yüzde **10 dosya/65 test**, typecheck ve build geçti. Son tam koşu, yayın ve canlı doğrulama aşağıdaki sonuçlarla tamamlandı.

Son doğrulama: **Infrastructure 5.264/5.264 + Core 2/2 = 5.266 başarılı**, atlanan/başarısız yok; Infrastructure 3 dakika 28 saniye. Kanıtlar `infrastructure-final-038.trx`, `core-038.trx`, `terminal-final-038.trx` (4/4), `frontend-test-038.log` (65/65), `typecheck-038.log`, `frontend-build-038.log`. İlk başarısız koşular silinmedi; yukarıdaki test ve kapanış düzeltmeleri sonrası tam koşu tekrar geçti.

Kaynak commit'i **`90f6e1f`**. Paket **90.762.989 bayt**, SHA256 **`C0F13FDA18C9AE9695FB01E6405CB1A73DD8786756E613BDA4B65E16AD946752`**. Publish ve installer içerik denetimleri geçti; ön yüzün 149 dosyası birebir eşleşti. 22 bileşen Passed, mevcut büyük paket istisnasıyla 3 Skipped. Inno derlemesi 31,703 saniye; kanıt `package-038.log`, `artifacts/installer-manifest.json`, `test-results/component-package-audit.json`.

[0.8.38 yayını](https://github.com/aliorala/adapanel/releases/tag/0.8.38) mevcut `publish-github-release.ps1` betiğiyle tamamlandı; yayın dosyası yeniden indirilerek aynı SHA256 ile doğrulandı (`publish-038.log`). Canlı panel paketi doğruladı ve 0.8.37 → 0.8.38 kurulumunu tamamladı. Kaynak kod/tarihçe/tag push edilmedi.

Canlı terminalde salt okunur kurulum kontrolü: hizmet **Running / Automatic**; son deneme **succeeded**, installerExitCode **0**, isFinished **true**, isFailed **false**, isDecidedByPanel **true**, updatedAtUtc **2026-09-09T21:23:19.4367739Z**. Son Inno günlüğünde Stop **00:22:52.312 / 00:22:53.797**, Installation process succeeded **00:24:09.520**, Restore **00:24:30.396**, Log closed **00:24:30.412** (10.09.2026 sunucu yerel zamanı). Panel mevcut/son sürümü **0.8.38** gösteriyor. Kontrol komutu exitCode **0**, süre **1.777 ms**.

Canlı işlev denemesi: 5 saniye süre sınırıyla stdout'a `TERMINAL-KONTROL`, stderr'e `DENETIM-HATA-CIKTISI` yazan ve 20 saniye bekleyen komut çalıştırıldı. İşlem sürerken yeni komut `Get-Date`, dizin `C:/ProgramData/AdaPanel` yapıldı. Sonuç **exitCode=-1, toplam 6.297 ms**; iki çıktı ve **“Komut zaman aşımına uğradı; alınan çıktı tamamlanmamış olabilir.”** birlikte göründü. Yeni taslak ve dizin aynen korundu; geçmişte gerçek gönderilen komut kaldı. Süre 30 saniyeye döndürüldü. Canlıda yalnız zararsız bekleme denemesi yapıldı; ana süreçten miras kalan açık boru senaryosu yerelde gerçek süreçlerle sınandı. Yedekleme/şifreleme ve kompakt görünüm tercihleri değişmedi.

## 0.8.37 — İptal edilen yedeğin kaydı ve kaynak izleme yarışı

Tamamlanmış yerel arşiv FTP'ye aktarılırken istek iptal edilirse `FinalizeBackupAsync` kayıt oluşturmadan çıkıyordu. Gerçek yerel TCP/FTP sunucusunda MKD aşamasında iptal edilerek iki kez yeniden üretildi: sağlam içerik ve boş veritabanı nedeniyle uyarılı içerik. İkisinde de ilk test listeyi boş buldu (`cancel-before-037.trx`).

Hash uzak aktarımdan önce hesaplanıyor. Aktarım iptal edilince `created_remote_failed` kaydı, hash, yan indeks ve varsa eksik içerik uyarıları iptal edilmiş istek token'ından bağımsız yazılıyor; ardından asıl iptal yeniden iletiliyor. Site/abonelik/posta içerik uyarıları artık ikinci kayıt güncellemesine bırakılmıyor. Test, kayıtlı FTP hedefine yeniden gönderimde aynı id/hash'i, doğru son durumu ve sunucunun aldığı baytları da kontrol ediyor. Servis çökmesi/elektrik kesintisi için kalıcı iş kuyruğu eklenmedi; arşiv oluşturma veya hash aşamasındaki iptal bu düzeltmenin kapsamı dışında.

`ResourceConsumers.vue` eski isteğin `finally` bloğunda yeni isteğin `inFlight` kilidini kaldırıyordu. Gerçek Vue watcher/yaşam döngüsü, bellek içi render host'u ve geciktirilmiş API yanıtlarıyla hem eski başarı hem eski hata sınandı: düzeltmeden önce 3 yerine 5 istek oluştu (`consumers-before-037.log`). Güncel olmayan istek artık kilidi/denetleyiciyi değiştirmiyor; kaynak değişimi yükleniyor durumunu temizliyor, kapanış eski istekleri geçersiz kılıyor.

- Son tam test: **Infrastructure 5.260/5.260 + Core 2/2 = 5.262**, başarısız/atlanan yok; Infrastructure süresi 3 dakika 8 saniye. `infrastructure-final-037.trx`, `core-037.trx`.
- Ön yüz: **9 dosya / 60 test**, typecheck ve build başarılı. `.tmp/remaining-fixes/typecheck-037.log`, `frontend-test-037.log`, `frontend-build-037.log`.
- Ana sayfa CPU/bellek/disk kartları canlı masaüstünde kontrol edildi; yazı taşması görülmedi. Tasarım ölçeği ve şifreleme tercihi değiştirilmedi. Canlıya yapay FTP kesintisi uygulanmadı.
- Kaynak commit'i **`2c96972`**. Kurulum paketi **90.762.779 bayt**, SHA256 **`521A8A418520C4A4C7433A08A76896BB004FFEC84C61F29C7A5385F7853E3A03`**. Publish/installer içerik denetimleri geçti; ön yüzün 149 dosyası paketle birebir eşleşti. 22 bileşen denetimi geçti, mevcut betiğin büyük paket istisnasıyla 3 paket atlandı. Inno derlemesi 28,250 saniye. Kanıt: `.tmp/remaining-fixes/package-037.log`, `artifacts/installer-manifest.json`, `test-results/component-package-audit.json`.
- [0.8.37](https://github.com/aliorala/adapanel/releases/tag/0.8.37) yalnız mevcut yayın betiğiyle yayımlandı; public dosya yeniden indirildi, SHA256 yerel dosyayla eşleşti (`publish-037.log`). Canlı panel aynı sürüm/hash'i gösterdi, paketi doğruladı ve 0.8.36 → 0.8.37 güncellemesini tamamladı. Kaynak kod, tarihçe veya tag push edilmedi.
- Canlı terminalde salt okunur kontrol: hizmet **Running / Automatic**, son deneme **succeeded**, installerExitCode **0**, isFinished **true**, isFailed **false**, isDecidedByPanel **true**, updatedAtUtc **2026-09-09T18:14:01.5866171Z**. Inno günlüğü: Stop **21:13:36.171 / 21:13:36.734**, Installation process succeeded **21:14:11.301**, Restore + Log closed **21:14:18.707**. Kontrol komutu exitCode **0**. Panelde mevcut ve son sürüm **0.8.37** doğrulandı.

### Canlı IIS İstek İzleyici eksikliği

Yayın sonrası Kaynak → IIS (web) görünümü `request_monitor_missing` bildirdi. Salt okunur kontrolde `Get-WindowsFeature Web-Request-Monitor` Installed=false/Available ve `appcmd list requests /xml` içinde ERROR hresult=80004001 görüldü. Bu kez yalnız uyarı kontrolüyle bırakılmadı; mevcut sunucunun eksik Windows rol bileşeni kuruldu.

Microsoft'un [IIS modülleri açıklaması](https://learn.microsoft.com/en-us/iis/get-started/introduction-to-iis/iis-modules-overview) ve [Install-WindowsFeature belgesi](https://learn.microsoft.com/en-us/powershell/module/servermanager/install-windowsfeature) kontrol edildi. Yalnız `Web-Request-Monitor`, `-Restart:$false` ile kuruldu. Terminalin 60 saniye sınırından bağımsız tamamlanabilmesi için sunucuda tek kullanımlık betik, gizli PowerShell süreci ve ayrı çıktı/hata günlükleri kullanıldı. Betik: `C:/ProgramData/AdaPanel/updates/enable-request-monitor-20260909.ps1`; aynı kökte `.out.log` ve `.err.log` tutuluyor.

Kurulum çıktısı Success=true, ExitCode=0; son feature durumu Installed=true/Installed. Son appcmd yanıtı geçerli boş `<appcmd>` XML'i; 80004001 hatası yok. Boş listede appcmd çıkış kodu 1 görülebildiği için tek başına hata sayılmadı; XML ve panelin gerçek sonucu birlikte doğrulandı. Canlı panelde MariaDB → IIS geçişinden sonra **“0 aktif / Şu an işlenen istek yok”** görüldü. AdaPanel, W3SVC ve WAS Running; sunucu yeniden başlatılmadı. Canlıya yapay yavaş istek veya site dosyası eklenmedi. Bu mevcut sunucunun yapılandırma düzeltmesidir; 0.8.37 paketinin isteğe bağlı IIS özellik listesi değişmedi.

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
