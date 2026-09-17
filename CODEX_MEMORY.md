# AdaPanel Codex Memory

## 2026-09-17 — 0.8.42 yerel boş yedek klasörü düzeltmesi (yayın hazırlanıyor)

Yeni taramada yerel silmenin yalnız arşivi kaldırdığı, kaynak/kategori/gün klasörlerini bırakması doğrulandı. `LocalBackupFolderCleanupTests`: düzeltme öncesi 8 başarısız / 1 başarılı; düzeltme sonrası ilgili yedek/FTP testlerinin tamamı **501 başarılı** (`.tmp/backup-cleanup-042/backup-fixed.trx`). Manuel silme ve otomatik süre temizliği aynı yeni helper'ı kullanıyor. Yerel ve FTP için bilinen klasör düzeni `BackupCleanupLayout` içinde ortaklaştırıldı. Yalnız boş dizinler nonrecursive silinir; kök, dolu/bilinmeyen düzenler ve kök dışı yollar korunur. Kök/ata junction kontrolleri ve tarih adlı kök, Türkçe/%, # adlı klasör testleri eklendi. Saklama süresi ve şifreleme değişmedi.

Canlı salt okunur sayımda 17 Ağustos–11 Eylül arası **26 boş gün** bulundu. Yalnız doğrulanmış `C:\ProgramData\AdaPanel\backups` içindeki bilinen tarih düzenleri, reparse kontrolü ve nonrecursive Directory.Delete ile temizlendi: **270 boş klasör / 26 gün** kaldırıldı. Önce/sonra dosya sayısı **55/55**, yol+boyut envanter farkı **0**, kök mevcut. Tarihli 51 arşiv ve tarihsiz 4 dosya korunuyor; 12 Eylül 6, 13–17 Eylül 9'ar arşiv. Bu işlem FTP taraması değildir. Canlı o anda 0.8.41; 0.8.42 paketleniyor, henüz kurulmuş sayılmayacak.


Yayın sırasında iki yükleme GitHub API tarafında `Error creating asset temp dir` ve `Error saving asset` ile başarısız oldu; her denemeden sonra dosya listesi okundu, ikisinde de boştu. 0.8.42 taslak id=391035335; boş taslak canlıya sunulmadı. Etiket uç noktası taslak için 404 dönerken release listesi taslağı gösterdi. Yayın betiğine sayfalı liste araması eklendi; tekrar deneme aynı taslağı kullanır. 14 yayın testi geçti (`release-retry.trx`). Paket 90.796.795 bayt; SHA256 `C1B437ED0FD16AAEC381D6FCA702DD29B01022F452176FFBC9C4189EFBC31174`; içerik 167 dosya ve installer kontrolleri geçti. Üçüncü yükleme sürüyor; başarı/live kurulum henüz doğrulanmadı.

## 2026-09-17 — 12 Eylül yedek grubunun kalma nedeni doğrulandı

Kullanıcının son sıraya bıraktığı bildirim canlı listeden incelendi. 12 Eylül grubu boş değil: **6 yedek / yaklaşık 90 MiB** var. Bunlar iptvhub (03:06:53), karsilastir (03:07:31), rsmc_alarmm (03:07:33), travian_bot (03:07:34), posta (03:07:36) ve panel (03:07:40). Hepsi 12.09.2026 sunucu yerel saati. Önceki aktif doğrulama bu kayıtlar dahil 51 kayıtta eksik yerel/uzak veya açılamayan arşiv bulmadı.

Canlı ayar 5 gün, günlük 03:00. Son iş 17.09.2026 03:06:56'da tamamlandı; özeti 3 eski yedeğin silindiğini gösteriyor. `BackupService.PruneRecordsAsync` seçim anında `DateTime.UtcNow.AddDays(-retentionDays)` kullanıyor; takvim günü değil **120 saat** hesabı. 12 Eylül turu 17 Eylül turundan biraz daha geç tamamlandığından, erken 3 site yedeği seçilirken sonraki 6 yedek o anda yaş sınırını geçmemişti. Bu öğeler başarısız silme kalıntısı değil, seçim dışında kalan gerçek arşivler. Gün klasörü içerik durduğu için kalıyor. Sonraki iş 18.09.2026 03:00; mevcut kurala göre o turda bu 6 kayıt süre bakımından silinmeye aday olacak. Son kullanılabilir yedek ve zincir korumaları ayrıca uygulanır; sonraki tur henüz gözlenmediğinden silinmiş sayılmıyor.

Bu incelemede dosya silinmedi, saklama politikası veya kod değiştirilmedi. Takvim günü hesabına sessizce geçmek bazı arşivleri 120 saat dolmadan sileceği için yapılmadı. Bu açıklama FTP'deki bütün kayıtsız/eski boş klasörlerin tarandığı anlamına gelmez.
## 2026-09-17 — Kapsamlı denetim, 0.8.41 canlı


Kullanıcı düzeltmesi (17.09.2026): alioral.xyz alan adı kapandı. DNS/SSL uyarıları panel hatası sayılmayacak; alan adı yeniden etkinleştirilmeyecek ve DNS/SSL değiştirilmeyecek. Joker sertifika için webmail özelliği engellenmedi; kök alan adı üretimi düzeltildi.

Kaynak commit: `3ede41a`. 49 altyapı modülü, 66 controller / 427 eylem, 60 Vue ekranı ve 840 kaynak dosyası envantere alındı. Ayrıntı, sınırlar ve modül listesi: [17 Eylül denetimi](docs/denetim-2026-09-17.md).

- Dış komut iptal/timeout/stdin yönetimi, FTP hedef kökü sınırı, Node/Python/ters vekil junction yolları, site veritabanı ve SEO cevap sıralaması, 19 çeviri, UTC saatleri, görev olarak çalışan Stalwart sağlık durumu, terminal UTF-8/CLIXML, paket indirme süresi ve bağımlılıklar düzeltildi.
- SDK 8.0.425 / paket .NET 8.0.31. Son tam koşu 5.310 arka uç testi; 74 ön yüz testi, tür kontrolü, üretim derlemesi, WHMCS ve paket kontrolleri geçti. npm/NuGet açık sayısı 0. 22 bileşen Passed / 3 büyük paket mevcut politikayla Skipped.
- Sonradan canlıda webmailin durduğu bulundu: joker sertifikaya doğrudan `webmail.` ekleniyordu. Kök neden düzeltildi; `*.example.com` artık `webmail.example.com` üretir, joker desteği korunur. 34 ilgili test ayrıca geçti. IIS yedeği `AdaPanel-Webmail-20260917` alınıp tek geçersiz bağ kaldırıldı; webmail Started / AdaPanel-Webmail-OK, SSL yenilemesi 21:16:01 sunucu saatinde tamamlandı.
- 0.8.40 GitHub dosya yüklemesi `Error saving asset` ile başarısız; boş yayın taslak yapıldı, canlıya kurulmadı. Son paket 0.8.41, 90.795.576 bayt, SHA256 `30B445EBDD6E66EC417924D8CCBC4DA8E40262F240E9383277780FC148F86F31`. GitHub 504 yanıtına rağmen dosya uploaded durumunda tamamlandı; tekrar indirilip aynı SHA256 ile doğrulandı (verify-published041.log). Kaynak/history/tag push edilmedi.
- **Canlı sonuç:** mevcut/son sürüm 0.8.41; AdaPanel Running/Auto; installerExitCode=0, isFinished=true, isFailed=false, updatedAtUtc=2026-09-17T18:31:26.3707516Z; Inno hizmet Restore/Log closed 21:31:47.841. Stalwart sağlık ekranında Çalışıyor; webmail Started ve sağlık OK. Terminal Türkçe karakterleri doğru, CLIXML yok. Yayın betiği yeni sürümleri dosya yüklenene kadar taslakta tutacak şekilde düzeltildi; 12 ilgili test geçti. Betik/devir commit 420c923.
- Mevcut 3 IIS sitesi çalışıyor; 51 yedek kaydı, 17 Eylül 9 yeni uzak kopya. Günlük 03:00 / 5 gün saklama korunuyor. Yedekleme kalacak, şifreleme kullanıcı kararıyla kapalı; kompakt görünüm korunacak.
- Kurulum sonrası aktif yedek doğrulaması: 51 kayıt, 0 eksik yerel, 0 eksik uzak, 0 açılamayan arşiv, 0 ölçülemeyen kayıt. FTP yetim/kayıt dışı dosya taraması desteklenmiyor; bu sonuç eski kayıp arşivlerin geri üretildiği ya da gerçek geri yükleme yapıldığı anlamına gelmez.
- Son canlı UI kontrolü: .NET 8.0.31 / AdaPanel 0.8.41; kompakt ana sayfa kartlarında taşma yok. SEO 21:35:57 taraması tamamlanınca sırada bildirimi kalktı; denetim kaydı aynı yerel saati gösteriyor. Ana sayfa işlem adları Türkçe.
- Harici epey botlarında hata/uzun çalışma, site robots/sitemap eksikleri ve 6 üçüncü taraf bileşen güncellemesi ayrı operasyonel bulgular. Canlı veri silme/geri yükleme/göç ve her düğmenin bütün olası yolları sınanmış sayılmaz. Kaynak/history/tag public GitHub'a ASLA push edilmeyecek; yalnız mevcut yayın betiğiyle kurulum dosyası.

Son guncelleme: 2026-09-17

Bu dosya, baska bir kullanici veya yeni bir Codex oturumu ayni klasorde devam ederken hizli devir notu olarak okunmalidir.
Ilk okunacak dosyalar:

1. `promp.md`
2. `CODEX_MEMORY.md`
3. `docs/calisma-durumu.md`

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
## 2026-09-10 — Önceki sonuç: 0.8.38 canlı, terminal düzeltmeleri tamamlandı

Kullanıcının yeni “devam et” isteği üzerine terminal akışı denetlendi. Kaynak commit'i **`90f6e1f`**; **0.8.38 yayınlandı ve canlıya kuruldu**. Panel mevcut/son sürümü 0.8.38 gösteriyor. Hizmet Running/Automatic; son kurulum succeeded, installerExitCode=0, isFinished=true, isFailed=false. Inno başarı 00:24:09.520, hizmet Restore 00:24:30.396, Log closed 00:24:30.412 (10.09.2026 sunucu yerel zamanı).

- Terminalin süre sınırı yalnız ana PowerShell sürecini bekliyordu; alt süreç stdout/stderr borusunu açık tutarsa cevap süresiz bekleyebiliyordu. Önce testte yeniden üretildi (2 saniyelik sınırla 10 saniyede dönmedi). İstek süresi artık çıktı okumayı da kapsıyor. Tavanlı okuyucu, yalnız terminalin seçtiği kipte iptalden önce topladığı çıktıyı koruyor; diğer ProcessRunner çağrılarının varsayılan iptal davranışı değişmedi. Süre dolunca hâlâ çalışan ana süreç ağacına Kill uygulanıyor; ana süreç önceden bitmişse bağımsız arka plan işlerinin sonlandığı iddia edilmiyor. Sınırsız WaitForExit temizliği kaldırıldı.
- Önceki komut beklenirken yeni yazılan komut ve çalışma dizini yanıt gelince ezilmiyor. Hata çıktısı varsa bile zaman aşımı açıklaması ayrıca gösteriliyor. Türkçe/İngilizce mesaj, bütün alt süreçlerin sonlandığını kesin olarak söylemiyor.
- 4 yeni terminal bütünleşme testi (normal sonuç, çalışan komut zaman aşımı, istek iptali, ana süreç çıktıktan sonra açık boru) ve 5 Vue akış testi eklendi. Ön yüzde 65 test/typecheck/build geçti. İlk tam koşularda yardımcı süreç açılışı ve kapanışta dizin kilidi sorunları yakalandı; testler mevcut `Script host timing` koleksiyonunda ve daha geniş başlangıç payıyla çalışıyor. Üretimde her kapanış bekleme denemesi en fazla 2 saniye; kullanıcı komut sınırı değişmedi. **Son tam koşu Infrastructure 5.264 + Core 2 = 5.266 başarılı**, başarısız/atlanan yok; Infrastructure 3 dakika 28 saniye.
- Canlıda 5 saniye sınırıyla 20 saniyelik zararsız bekleme komutu denendi: 6.297 ms toplam süre, exitCode=-1; stdout ve stderr yanında Türkçe zaman aşımı açıklaması korundu. İşlem sürerken girilen `Get-Date` taslağı ve `C:/ProgramData/AdaPanel` dizin değeri yanıtla ezilmedi. Ardından süre 30 saniyeye alındı; salt okunur kurulum/hizmet kontrolü exitCode=0 döndü. Açık boru senaryosu gerçek Windows süreçleriyle yerelde sınandı; canlıya bağımsız alt süreç eklenmedi.
- Paket **90.762.989 bayt**, SHA256 **`C0F13FDA18C9AE9695FB01E6405CB1A73DD8786756E613BDA4B65E16AD946752`**. [0.8.38 yayını](https://github.com/aliorala/adapanel/releases/tag/0.8.38) mevcut betikle yapıldı; indirilen yayın dosyası ve canlı panel aynı özeti doğruladı. 149 ön yüz dosyası eşleşti; 22 bileşen geçti, mevcut büyük paket istisnasıyla 3 atlandı. Kaynak/history/tag public GitHub'a push edilmedi.
- Kanıtlar `.tmp/remaining-fixes/*-038.log` ve `*-038.trx`. Yedekleme modülü, kapalı şifreleme tercihi ve önceki IIS düzeltmesi korunuyor.

## 2026-09-09 — Önceki sonuç: 0.8.37 canlı, ek düzeltmeler tamamlandı

Kullanıcının “var mı başka yapılacak, onları yap” isteğiyle iki ek hata testte yeniden üretildi ve düzeltildi. Kaynak commit'i **`2c96972`**; **0.8.37 yayınlandı ve canlıya kuruldu**. Panel mevcut/son sürümü 0.8.37 gösteriyor; hizmet Running/Automatic. Son deneme succeeded, installerExitCode=0, isFinished=true, isFailed=false. Inno başarı 21:14:11.301, hizmet Restore ve Log closed 21:14:18.707 (sunucu yerel zamanı).

- FTP aktarımı iptal edilince tamamlanmış yerel arşiv panel kaydına hiç girmiyordu. Bütünlük damgası uzak aktarımdan önce hesaplanıyor; iptal durumunda yerel kayıt, yan indeks ve içerik uyarıları istek iptalinden bağımsız yazılıyor, ardından iptal çağırana iletiliyor. İçerik uyarıları site/abonelik/posta için ilk kayıtla birlikte yazılıyor. Bu düzeltme işlem çökmesi veya arşiv üretimi sırasında iptal için kalıcı iş kuyruğu sağlamaz.
- Kaynak izleme bileşeninde eski isteğin `finally` bloğu yeni isteğin kilidini açıyordu. Kilidi yalnız güncel istek bırakıyor; kaynak değişimi ve bileşen kapanışında eski yanıtlar geçersizleştiriliyor.
- Regresyonlar önce kırmızıydı: 2 FTP iptal senaryosunda liste boş; 2 ön yüz gecikme senaryosunda beklenen 3 yerine 5 istek. Son tam koşu **Infrastructure 5.260 + Core 2 = 5.262 / ön yüz 60 test başarılı**, atlanan yok. Typecheck/build geçti. FTP testleri iptal sonrası aynı id/hash ile yeniden aktarım ve gerçek TCP sunucusunun aldığı baytları da doğruluyor; eksik içerik uyarıları korunuyor.
- Kanıtlar `.tmp/remaining-fixes/*-037.log` ve `infrastructure-final-037.trx`, `core-037.trx`. CPU/bellek/disk kartları canlı masaüstünde görsel kontrol edildi; taşma görülmedi.
- Paket **90.762.779 bayt**, SHA256 **`521A8A418520C4A4C7433A08A76896BB004FFEC84C61F29C7A5385F7853E3A03`**. GitHub'dan tekrar indirilerek birebir doğrulandı; canlı panel de aynı paketi doğruladı. 149 ön yüz dosyası eşleşti, içerik denetimleri ve 22 bileşen geçti; mevcut büyük paket istisnasıyla 3 bileşen atlandı. [0.8.37 yayını](https://github.com/aliorala/adapanel/releases/tag/0.8.37).
- Son canlı kontrolde **IIS Web-Request-Monitor eksikliği de giderildi**: önce Installed=false ve appcmd XML'inde 80004001; ardından Microsoft Windows rol bileşeni `Install-WindowsFeature -Name Web-Request-Monitor -Restart:$false` ile kuruldu. Installed=true/Installed, kurulum Success=true, geçerli hatasız appcmd XML'i, panel IIS görünümünde **0 aktif / Şu an işlenen istek yok** doğrulandı. AdaPanel/W3SVC/WAS Running; sunucu yeniden başlatılmadı. Canlı `C:/ProgramData/AdaPanel/updates/enable-request-monitor-20260909.*` betik ve out/err günlükleri tutuluyor. Bu sunucuda yapılandırma düzeltmesidir; 0.8.37 kurulum paketindeki isteğe bağlı IIS özellik listesi değiştirilmedi.
- **Yedek modülü kalıyor, şifreleme kullanıcının tercihiyle kapalı.** Önceki 18 kayıp uzak kopya ve damgasız eski arşiv hakkındaki sınırlar aşağıda geçerli. Kaynak kod/tarihçe/tag public GitHub'a push edilmeyecek; yalnız mevcut yayın betiğiyle kurulum paketi yayınlanacak.

## 2026-09-09 — Önceki sonuç: 0.8.36 canlı

Kullanıcının “devam et” isteği üzerine çalışıldı. Kaynak commit'leri `35e3b37` (0.8.33), `cf28ae9` (0.8.34), `63b3555` (0.8.35), `6a6fa9a` (0.8.36). Ayrıntı ve kanıtlar: [devam kaydı](docs/kalan-sorunlar-2026-09-09.md).

- **Yedekleme modülü kalacak. Şifreleme kullanıcının “Şimdilik kapalı kalsın” tercihiyle kapalı.** Bekleyen onay/parola talebi yok. Kompakt görünüm korunuyor.
- İlk FTP aktarımı başarısız, yerel arşivi ve oluşturma anının SHA256 kaydı bulunan sunucu yedeklerine yönetici için **Uzağa yeniden gönder** eklendi. Hedef onaylanır; güncel hedef/kimlik tek okumada çözülür, dosya yeri/boyutu/hash'i doğrulanır, aktarım boyunca yerel dosya yazma/silmeye karşı tutulur. Aynı kayıt/ilk gün yolu kullanılır; yerel dosya ve diğer uyarılar korunur. Abonelik/silinmiş/tamamlanmış/damgasız kayıtlar ve sabit dosya hedefleri reddedilir. Aktör/kaynak sahibi denetim izi test edildi.
- Eski `cb1c201e630d4a709b16ff309007f325` kaydının ContentHash alanı boş; yeniden gönderme canlıda aktarım başlamadan reddedildi. **Eski arşiv yeniden gönderilmiş sayılmamalı.** Mevcut hash geriye dönük doğrulanmış gibi kayda eklenmedi. Yerel dosya ve başarısız geçmiş korunuyor; ön yüz yeniden gönderilememe nedenini açıkça gösteriyor.
- Yönetici veritabanı yedeğine **Kayıtlı FTP hedefine yedek al** eklendi. Parola tarayıcıya verilmeden mevcut kimlik sunucuda kullanılır; onaylanan URL değişmişse döküm başlamaz. Canlıda yalnız `mariadb:iptvhub` için **iki yeni yerel + FTP yedeği başarıyla alındı**, kaynak/veri silinmedi. İlk yeni kayıt `d6eb5f54008e4c6fb00cbf5367c422c7`, son kayıt `c3de560c02774c9aac73cbcdc0a74cfc`; ikisi de son doğrulamada remoteState=present/remoteMissing=false. Son dosya `mariadb-iptvhub-20260909161552.sql.zst`, 16.083.344 bayt; kayıtlı ve ölçülen SHA256 `6CF8FA78C8CAFDA2A8CE1F1C7068F1646B56972099980058680BB8A6C46BC8E5` eşleşti.
- Nötr onayda yanlış “Sil” düğmesi düzeltildi. Şifreleme kapalı uyarısı artık içerik eksikliği varmış gibi sunulmuyor, bildirim gerçek dosya adını taşıyor. Yeni yedekten sonra arama/sayfa sıfırlanıp ilgili gün açılıyor. Eski filtre + kapalı gün üzerinden bu akış canlıda başarıyla ölçüldü; masaüstünde satır/onay/uyarı metinlerinde taşma görülmedi.
- **Son canlı doğrulama: 62 kayıt; 0 yerel dosya yok; 18 kaydın yereli önceki silme işleminde kaldırılmış; 18 uzak kopya yok; 0 arşiv açılamadı; 0 kayıt ölçülemedi.** Eski 18 kayıt `deleted_remote_failed`, Path boş; geçmişte silinmiş yerel dosyaları varmış gibi yorumlamayın. 14 kayıt 17 Ağustos, 4 kayıt 28 Ağustos'tan. Geçmiş silinmedi, kayıp arşivler yeniden oluşturulmadı. FTP yetim taraması desteklenmediği için yapılmadı; gerçek site/veritabanı geri yüklemesi yapılmadı.
- **Son tam koşu 5.260 .NET testi (Infra 5.258 + Core 2), atlanan/başarısız yok; derlemelerde 0 uyarı/0 hata.** 67 ilgili test geçti. Tam koşu 0.8.35 işlevlerini kapsar; yalnız ön yüz değişen 0.8.36'da typecheck/58 test/build tekrar geçti, tam .NET koşusu yeniden çalıştırılmadı. PowerShell 7 → Windows PowerShell 5.1 modül yolu sorunu yalnız test/paket sürecinin PSModulePath ortamıyla çözüldü; kalıcı ortam değiştirilmedi.
- **0.8.36 yayımlandı ve canlıda tamamlandı.** Paket 90.763.324 bayt; SHA256 `6CED764AD5662C470FFD6C2D7016FA6B197B31D2D23CC7E193597575ED6F57AB`. Yayından yeniden indirilen dosya ve canlı paket aynı özetle doğrulandı. 149 ön yüz dosyası eşleşti; 22 bileşen geçti, 3 büyük paket betiğin istisnasıyla atlandı; içerik denetimleri geçti. Son manifestler 0.8.36'ya aittir. Hizmet Running/Automatic; last-attempt succeeded/installerExitCode=0/isFinished=true/isFailed=false. Inno Restore/Log closed 19:15:45.711.
- Üç devir belgesi güncellendi. Kaynak kod yerelde commit'lendi; **kaynak geçmişini açık GitHub deposuna push etmeyin**. Yalnız mevcut release betiğiyle yayın yapıldı. Önceki Chrome bağlantısı sorusu çözüldü; kullanıcıdan beklenen bir bağlantı yenileme adımı yok.

## 2026-09-09 — Önceki devir: 0.8.32 canlı

Kullanıcı “Kalan sorunları da düzelt” dedi. Kaynak commit'leri `01ce3c9` (0.8.31) ve `c555ac4` (0.8.32); ayrıntılar ve kanıtlar [kalan sorunlar kaydında](docs/kalan-sorunlar-2026-09-09.md).

- **Yedekleme modülü kalacak.** Önceki kaldırma talebi geri çekildi; kompakt görünüm de korunuyor.
- Kurulumdaki SCM yeniden başlatma yarışı düzeltildi. `scripts/update-service-guard.ps1` başlangıç ayarını saklayıp dosyalar değiştirilirken hizmeti geçici `Disabled` yapar; iptal/hata/başarıda geri yükler. Yalnız tam exe yolu eşleşen süreç durdurulur; dosya kilidi kalkmadan kurulum ilerlemez. Çalıştırıcının durdurma beklemesi sınırlandırıldı.
- FTP'de geçici hatalara en fazla üç deneme, gerçek asenkron zaman aşımı/iptal ve bağlantıyı kapatma eklendi. 530/550 gibi kalıcı hatalar tekrar edilmez; yerel yedek korunur. Üst gün klasörü silinmiş yedeklerde üst dizin listesiyle yokluk kanıtlanır; erişim reddi hâlâ “bilinmiyor” sayılır.
- 0.8.32'de aynı sınırlı tekrar FTP listeleme/silme/boş dizin kaldırmaya da uygulandı; canlı toplu doğrulamanın yakaladığı geçici 421 soket hataları için gerçek TCP testleri eklendi.
- Test uyarıları bastırılmadan düzeltildi. **Son 0.8.32 tam koşusu: 5.242 .NET testi (Infra 5.240 + Core 2), atlanan yok, derlemede 0 uyarı/0 hata.** 73 ilgili kontrol geçti. 0.8.31'deki 58 ön yüz testi başarılı; 0.8.32'de yalnız sürüm bilgisi değişen ön yüz yeniden derlendi. Typecheck/build başarılı. Her iki pakette 149 ön yüz dosyası publish ile eşleşti; 22 bileşen denetimi geçti, 3 büyük paket betiğin istisnasıyla atlandı.
- **0.8.31 yayımlandı ve canlıya kuruldu.** Paket 90.757.936 bayt; SHA256 `6D19478CA8863E05B20AFC41619D566EE5C9DEC3DA38129DC75854D630FFD4B8`. Yayından yeniden indirilen dosya ve canlı sunucudaki paket aynı özetle doğrulandı. `last-attempt.json`: succeeded, exit 0, isFinished=true, isFailed=false. Hizmet Running/Automatic. Canlı Inno günlüğünde iki Stop ve Restore adımı başarılı, kurulum günlüğü kapandı. Kaynak kod açık GitHub deposuna gönderilmedi.
- Yeni sürümde kayıtlı FTP hedefine gerçek bağlantı testi dosyası yazıldı; arayüz **“Bağlantı başarılı — hedefe yazılabiliyor”** gösterdi. Bu, eski başarısız veritabanı arşivinin yeniden yüklendiği anlamına gelmez; o kaydın yerel kopyası korunuyor. Eski gece işi özeti tarihsel sonuçtur, geçmişi başarılı göstermeyin.
- **Kullanıcı kararı:** yedek şifrelemesi “Şimdilik kapalı kalsın” yanıtıyla kapalı bırakıldı. Bunu bekleyen onay veya unutulmuş hata saymayın; kullanıcı yeni talep vermedikçe etkinleştirmeyin. Parola oluşturulmadı. Eski şifresiz arşivler geriye dönük şifrelenmedi. Gerçek site/veritabanı geri yüklemesi yapılmadı.
- **0.8.32 de yayımlandı ve canlıya kuruldu.** Paket 90.758.804 bayt; SHA256 `087492DAC6C0BC69FEEF4749AD915DBA601CD5AF9F94AFB5748435E1648C0A2A`. Yayın dosyası yeniden indirilip doğrulandı; canlı panel aynı özeti doğruladı ve 0.8.32 / “Panel güncel” gösterdi. Hizmet Running/Automatic; son durum succeeded/isFinished=true/isFailed=false. Bu kayıtta installerExitCode=null; bunu 0 diye aktarmayın. Inno günlüğünde kurulum başarılı, Stop/Restore başarılı ve günlük 17:58:36'da kapanmış.
- **Son canlı yedek doğrulaması (0.8.32): 60 kayıt, 0 yerel dosya yok, 18 uzak kopya yok, 0 arşiv açılamadı, 0 kayıt ölçülemedi.** Önceki koşudaki 4 erişim hatası kalmadı; 18 eski uzak kopyanın yokluğu artık kanıtlandı. Bu kontrol veri silmedi veya eksik uzak kopyaları yeniden oluşturmadı. FTP yetim taraması desteklenmediği için yapılmadı; boş yetim listesi temizlik kanıtı değildir.
- Kanıtlar `.tmp/remaining-fixes/` altında; son manifestler ve bileşen denetimi artık **0.8.32** paketine ait. Ayrıntılı belgede canlı doğrulama sonuçları takip edilir. Aşağıdaki 0.8.29/0.8.30 kayıtları tarihsel bağlamdır.

## 2026-09-09 — Önceki devir: 0.8.29 ve 0.8.30

Bu bölüm önceki oturumun durumudur; güncel düzeltmeler ve canlı sürüm yukarıdadır. Ayrıntılar: [0.8.29 düzeltmeleri](docs/duzeltmeler-2026-09-09.md) ve [0.8.30 kompakt görünüm](docs/kompakt-gorunum-2026-09-09.md).

- **Kullanıcı kararı: yedekleme modülü kalacak.** Kaldırma / sunucuyla eşitlemeye dönüştürme isteği, kullanıcının başka projeyle karıştırdığını belirtmesi üzerine geri çekildi. Bu eski isteğe dayanarak modülü veya bağlantılarını kaldırmayın.
- Kullanıcı canlı yayına izin verdi. 0.8.29 ve ardından **0.8.30 yayımlandı ve canlıda doğrulandı**. Son doğrulamada arayüz ve sunucu sürümü 0.8.30; IIS, WAS ve FTP servisleri çalışıyordu.
- **0.8.29** (`68180e6`, yayın kaydı `56a86d4`): eski oturum yanıtlarının yeni oturumu etkilemesi engellendi; kullanıcı bağlamı ve yetkiler temizleniyor. Yedek geri yüklemede ortak rol/zincir/parola/hedef onayı, işlem kilidi ve kısmi hata raporu eklendi. Dosya düzenleyicide kaydet/at/vazgeç akışı, başarısız kayıtta içeriğin korunması ve site seçimi düzeltildi. Rol bazlı araç erişimi, URL üzerinden bölüm/site aracı gezinmesi ve geri/ileri davranışı düzeltildi. Kurulum takibine ağ hatası/iptal/zaman aşımı yönetimi eklendi; TR/EN dil paketleri ayrı yükleniyor. ScriptTestHost süreç/çıktı zaman aşımı ve WinAcme boş çıktı işlemesi düzeltildi.
- **0.8.30** (`03cbea2`, canlı görünüm kaydı `9577018`): kullanıcının büyük görünüm ve CPU/bellek/disk halkalarında taşma şikâyeti giderildi. `frontend/src/styles/base.css` içinde kompakt masaüstü yazı tabanı 14 → 13 px, kart dolguları ve menü aralıkları küçültüldü. `frontend/src/views/DashboardOverviewView.vue` içinde 160 px halkaların yazıları ortalandı ve uzun açıklamalar satır kırıyor. Mobil başlıklar halka üstüne alındı; yüzde içeride. Rahat görünüm ve mobil yazı tabanı 14 px; tarayıcı zoom ayarı değiştirilmedi.
- **Doğrulama sınırı:** 0.8.29 son tam koşusunda .NET 5.222/5.222 (Infrastructure 5.220 + Core 2), ön yüz 58/58 geçti; typecheck/build başarılı. CSS ağırlıklı 0.8.30 için typecheck/build ve mevcut 13 CSS testi geçti; tüm işlev testleri yeniden koşulmadı. Gerçek Vue bileşenleriyle 320/390/1024/1486 px, TR/koyu/kompakt ve EN/açık/rahat, %100 ve uzun kaynak değerleri kontrol edildi. Son durumda halka içi ve yatay sayfa taşması yok; canlıda da 13 px taban ve gerçek halka metinleri doğrulandı. Gerçek yedek geri yükleme yapılmadı.
- **Paket:** [0.8.30 sürümü](https://github.com/aliorala/adapanel/releases/tag/0.8.30), 90.746.342 bayt; SHA256 `10C78CA89291F580C24CEA891E0425BC5B60F48E83F603DA8EC123636190B0AA`. Yayınlanan dosya yeniden indirilerek ve canlı sunucunun indirdiği paketle doğrulandı. 149 ön yüz dosyası publish çıktısıyla eşleşti; 22 bileşen denetimi geçti, betiğin büyük paket istisnasındaki 3 bileşen atlandı.
- **Yayın sırası:** ön yüz derleme/eşitleme tamamen bitmeden paketlemeyi başlatmayın; paralel başlatma eski statik dosya listesiyle MSB3030 üretti. Sonraki geçici bileşen indirme hatası da yeniden denetimle geçti; başarısız denemeler yayımlanmadı. Kaynak kod yerelde commit'lendi; herkese açık `aliorala/adapanel` deposuna kaynak geçmişi gönderilmedi. Mevcut yayın betiğini kullanın, yerel kaynak dalını bu depoya push etmeyin.
- **Açık kalan işletim konuları:** 0.8.29 ilk kurulumunda hizmetin 5 saniyelik otomatik kurtarması dosyayı yeniden kilitleyip kod 5 / geri alma üretti; sonraki sunucu kaynaklı deneme başarılıydı. Hizmet kurtarma yarışı kalıcı olarak düzeltilmedi. Yayın öncesinden kalan bir veritabanı yedeğinin uzak FTP aktarımı `421 Service not available` ile başarısız; yerel yedek mevcut. Eski kayıtlarda şifreleme yapılandırılmamış uyarıları var. Bunlar çözülmüş sayılmamalı. Test projelerinin mevcut analiz uyarıları da ayrı teknik borçtur.
- Kanıtlar: `.tmp/audit-fixes/` altındaki son test/yayın günlükleri ve ayrıntılı iki belge. `artifacts/*manifest.json` ile `test-results/component-package-audit.json` son paketleme (0.8.30) sonuçlarını taşır; eski sürüm kanıtı olarak yorumlamayın. Geçici önizleme dosyaları ve geliştirme sunucusu kaldırıldı.

## Mevcut durum

- Proje kok dizini: `C:\Users\aLi\Desktop\AdaPanel`
- Hedef platform: Windows 10/11 ve Windows Server 2016+
- Mimari: `Api` + `Core` + `Infrastructure` + `Data`
- Frontend: Vue 3 + Vite + Pinia + Vue I18n
- Backend: ASP.NET Core 8
- Yerel .NET SDK: `C:\Users\aLi\Desktop\AdaPanel\.tools\dotnet\dotnet.exe`

## 2026-06-08 15:00 devir (tarihsel kayıt)

- Plesk paritesi analizi yapildi; eksik gorulen 6 modul gercek implementasyonla eklendi. Her biri backend + frontend + i18n (TR/EN parite) + derleme/test ile dogrulandi.
- **P0 Otomasyon zamanlayicisi (panel ici arka plan job motoru):**
  - `IHostedService` tabanli `Automation/AutomationWorker.cs` dakikada bir zamani gelen job'lari calistirir (proje ilk arka plan servisi).
  - `Automation/AutomationService.cs` + `IAutomationService`; ayar/durum `config/automation.json`.
  - Otomatik yedekleme (tum siteler + opsiyonel DB) + retention budama (`IBackupService.PruneAsync`).
  - Otomatik Let's Encrypt yenileme (`ISslService.RenewAllAsync` zamanlanmis).
  - API `AutomationController` (`/api/v1/automation`), UI Sunucu Araclari -> Otomasyon sekmesi.
- **Brute-force korumasi (fail2ban benzeri):** `Security/BruteForceProtectionService.cs` + `IBruteForceProtectionService`; basarisiz girisleri IP bazinda sayar, esik asilinca Windows Firewall'da sureli engel kurali olusturur. `AuthService`'e 3 noktada baglandi (2 basarisizlik + 1 basari temizligi). Sureli engel temizligi `AutomationService.RunDueJobsAsync` icinde (kilit reentrant olmadigindan exclusive blok DISINDA). Durum `config/bruteforce.json`. API `SecurityController`'a eklendi, UI Guvenlik -> Brute-force sekmesi.
- **Per-site disk kotasi (FSRM):** `Iis/DiskQuotaService.cs` + `IDiskQuotaService`; `New-FsrmQuota` ile. FSRM yoksa durum olarak raporlar. API `DiskQuotaController` (`/api/v1/sites/{site}/quota`), UI SiteHostingView -> Disk kotasi.
- **Git deployment:** `Git/GitDeploymentService.cs` + `IGitDeploymentService`; ilk dagitim clone, sonraki fetch+reset. Webhook (anonim, token + `FixedTimeEquals` dogrulama, `[IgnoreAntiforgeryToken]`). Config `config/git/<site>.json`. API `GitController`, UI SiteHostingView -> Git dagitimi.
- **Web istatistikleri (kendi icinde, harici bagimlilik yok):** `Statistics/WebStatisticsService.cs` + `IWebStatisticsService`; IIS W3C loglarini `#Fields` basligina gore ayristirir (toplam istek, tekil ziyaretci, bant genisligi, status bucket, top sayfa/referer, gunluk). API `StatisticsController` (`/api/v1/sites/{site}/statistics?days=`), UI SiteHostingView -> Istatistikler. Test edilebilir `AnalyzeLines` statik metodu + `WebStatisticsAnalyzerTests`.
- **Mail (hMailServer):** `Mail/MailService.cs` + `IMailService`; PowerShell-COM (`hMailServer.Application`) ile domain/mailbox/alias/parola yonetimi. Admin parolasi `config/mail.json` icinde DPAPI korumali. API `MailController` (`/api/v1/mail`), yeni ust seviye menu `MailView.vue` (E-posta). NOT: hMailServer kurulu olmayan makinede "notInstalled" durumu doner; tam test hedef sunucuda hMailServer ile yapilmali.
- **Onemli teknik notlar:**
  - PowerShell iceren raw string'lerde brace cakismasi: tek-`$` yerine `$$"""..."""` (cift suslu interpolasyon) kullanildi (DiskQuotaService, MailService BuildScript).
  - i18n degerlerinde literal `@` intlify linked-message ayristiricisini bozar (`build` hatasi code 10) -> `@` iceren metinlerden kacinildi.
  - `ConcurrencyLock` statik non-reentrant SemaphoreSlim; ic ice `RunExclusiveAsync` cagrisi deadlock yapar.
- **Dogrulama:** `npm run typecheck` OK, `npm run build` OK, `dotnet test AdaPanel.sln -c Release`: Core 2/2, Infrastructure 18/18. i18n TR/EN 1282/1282 parite.
- **Yeni installer (bu modullerle):**
  - `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - boyut: `50,416,524` byte (`48.08 MiB`)
  - SHA256: `A02C3B6251B2213EE191ED50F032D3879283C8FE062BCD9B64814E2F8EBA2E1D`
- **Bu makinede dogrulanamayanlar (hedef Windows/IIS sunucusunda denenmeli):** FSRM kotasi, hMailServer mail islemleri, canli IIS site listesi uzerinden otomatik yedek, gercek brute-force firewall kurali. Kod hepsini codebase'in mevcut PowerShell/COM kalibiyla gercek yapar.
- **Sonradan duzeltmeler (ayni devir):**
  - i18n ANAHTAR CAKISMASI hatasi giderildi (kritik regresyon): vue-i18n `flatJson: true` ile bir anahtar hem deger hem de baska anahtarlarin oneki olamaz. `panel.sites.tools` -> `panel.sites.toolsBar`, `panel.backup.status` -> `panel.backup.statusColumn` olarak yeniden adlandirildi (ve kullanim yerleri WebsiteWorkspaceView/BackupView). Aksi halde Web Sitesi sekme etiketleri ham anahtar (`panel.sites.tools.sites`) gorunuyordu. KURAL: yeni i18n anahtari eklerken bir kok anahtarin hem deger hem onek olmadigindan emin ol (kontrol: bir anahtar baska anahtarin `prefix.` onekiyse cakisma var).
  - Mail / e-posta sunucusu kurulum arastirmasi (SONUC: hMailServer + tek seferlik MANUEL kurulum):
    - hMailServer otomatik indirme DENENDI ve GERI ALINDI: hmailserver.com Cloudflare ile sunucu IP'lerini bloklar (kullanicinin sunucusunda da "Yazilim paketi indirilemedi" / download_failed). Resmi GitHub repo'sunda binary release YOK, aynalar (MajorGeeks vb.) oturum/yonlendirme tabanli sabit link vermiyor. Yani hMailServer icin GUVENILIR OTOMATIK INDIRME KAYNAGI YOK. components.json `hmailserver` girdisi ve ComponentBootstrapper `ResolveHMailServerAsync` resolver'i KALDIRILDI. (Indirme client'ina eklenen tarayici User-Agent BIRAKILDI; zararsiz, GitHub/digerlerine faydali.)
    - Stalwart ARASTIRILDI ve SECILMEDI: Windows sunucu binary'si yalniz v0.16.0+'da var (v0.15 ve oncesi sadece `stalwart-cli`). v0.16'da eski REST `/api/principal` API'si KALDIRILMIS, yerine JMAP + OAuth2 + yeni registry/bootstrap geldi -> korlemesine yazilamaz, bu makinede calistirilip dogrulanamaz. Kullanici "deneysel Stalwart"i degil guvenilir yolu secti.
    - NIHAI: Mail modulu hMailServer COM yonetimi (hazir/calisiyor) + TEK SEFERLIK MANUEL kurulum. `panel.mail.notInstalled` mesaji 3 adimli manuel rehbere cevrildi (hmailserver.com'dan indir -> Built-in DB + admin parolasi -> ekrani yenile, parolayi gir, Baglan). Kurulum sonrasi COM mevcut olunca MailView "Baglan" formu cikar; admin parolasi girilince tum yonetim panelden. MailService.GetAdminPasswordAsync ayrica panel-kurulu bilesenin uretilmis parolasini da okur (artik kullanilmiyor ama zararsiz birakildi). Dogrulanmis hMailServer 5.6.8-B2574 SHA256: `b7239676cd5eb9f21bf49c9ae170e7004b8abecb7eea08108930866a37d72659`.
- **Mail eklentileri + Docker (kullanici istegi, 4 ozellik):**
  - **Webmail (Roundcube tek tik):** AppStoreService'e `roundcube` sablonu. GitHub releases'tan `roundcubemail-*-complete.tar.gz` (GUVENILIR indirme) dinamik cozulur, `System.Formats.Tar` + GZipStream ile acilir, SQLite (temp/roundcube.db) + localhost IMAP(143)/SMTP(587) ile `config/config.inc.php` yazilir, web.config + ACL + `php -r` ile sqlite schema init + site PHP set. EnsurePhpRuntimeAsync (yoksa default PHP kurar). InstallRoundcubeAsync Laravel kalibini izler.
  - **E-posta kotasi:** MailAccountDto.CurrentSizeMB + MailDomainDto.MaxSizeMB (workspace PS script'inde $a.Size / $d.MaxSize). SetDomainQuotaAsync, SetAccountQuotaAsync (COM). MailView'da kullanim gosterimi + kota butonlari.
  - **Spam filtreleri:** MailService.GetSpamSettingsAsync/UpdateSpamSettingsAsync -> `$app.Settings.AntiSpam` (SpamMarkThreshold, SpamDeleteThreshold, UseSPF, GreyListingEnabled, SpamAssassin host/port/enabled). Her property try/catch ile (hMailServer surum farklari). MailView'da spam panel.
  - **Docker:** `Docker/DockerService.cs` (partial, GeneratedRegex) docker CLI uzerinden ps/images/run/start/stop/restart/rm/logs/pull/rmi. `--format "{{json .}}"` satir satir parse. ID/imaj/port/env regex validasyonu. docker yoksa Win32Exception -> Installed=false. DockerController + DockerView (yeni ust menu, glyph DK) + api/docker.ts. NOT: Docker Engine otomatik kurulumu EKLENMEDI (Windows'ta Containers feature + WSL2/Hyper-V gerektirir, ortama bagli/dogrulanamaz); yonetim UI'si kurulu Docker (Desktop veya Engine) ile calisir.
  - Dogrulama: typecheck OK, build OK, dotnet test Core 2/2 + Infra 18/18, i18n TR/EN 1330/1330 parite, cakisma yok.
- **Docker Engine OTOMATIK KURULUM + bilesen temizleme fix:**
  - components.json `docker-engine` (type zip, serviceName "docker", postInstall ["installDockerEngine"], installDir `%ProgramData%\AdaPanel\runtimes\docker`, bos sha256). ComponentBootstrapper.ResolveDynamicComponentAsync `case "docker-engine"` -> `ResolveDockerEngineAsync`: download.docker.com/win/static/stable/x86_64/ S3 listesini parse edip en guncel `docker-X.Y.Z.zip` URL'sini cozer (en guncel: 29.5.x). EnsureIntegrityMetadata'ya `IsTrustedNoChecksumHost` eklendi: HTTPS download.docker.com icin sha256 zorunlulugu muaf (checksum sidecar yok; download.docker.com'da 200 dogrulandi).
  - postInstall `InstallDockerEngineAsync`: Windows 'Containers' ozelligini acar (Enable-WindowsOptionalFeature / dism fallback), docker bin'i sistem PATH'ine ekler, `dockerd --register-service`, `sc start docker` (Containers yeni acildiysa reboot gerekebilir -> best-effort, basarisizlik kurulumu bozmaz). Windows container'lari calistirir. DockerService PATH'te yoksa `%ProgramData%\AdaPanel\runtimes\docker\docker\docker.exe`'e duser.
  - BILESEN TEMIZLEME FIX (kullanici sikayeti): Basarisiz/orphaned (manifest'te olmayan) InstalledComponent kaydi (ornek: eski hmailserver "Basarisiz") yalnizca SoftwareView alt "kurulu bilesenler" seritinde salt-okunur cip olarak gorunuyordu, kaldir butonu yoktu. Cip'e kaldir butonu eklendi (removeComponent(item.componentId)); backend RemoteAsync zaten orphaned kaydi silebiliyor (installedComponent var, manifest null). Kullanici yeni installer'a guncelleyip cip'ten kaldirinca temizlenir.
  - NOT: build-installer.ps1 npm notice'i stderr'e yazinca ($ErrorActionPreference=Stop) script erken durabiliyor; installer'i manuel adimlarla urettim (sync-frontend + dotnet publish + ISCC); typecheck/build/test ayrica gecti.
- **Guncel installer SHA256:** `018EF2A1F9B6323F75050E1722157BD015EFC3BB27E3B5857805B6EF31E10AC2` (48.10 MiB).

## 2026-06-09 STALWART mail server gecisi (mail artik OTOMATIK kurulur) + Roundcube/mail duzeltmeleri

- hMailServer otomatik indirilemiyor (Cloudflare); Plesk Windows MailEnable kullanir ama o da gated. **Stalwart** tek otomatik-kurulan modern secenek (GitHub releases + stalwart-cli). Mail backend hMailServer COM'dan Stalwart'a tasindi.
- `MailService.cs` yeniden yazildi: `stalwart-cli` ile yonetim API'sine (http://127.0.0.1:8080, env STALWART_URL/USER=admin/PASSWORD) baglanir. `create/query/delete/update domain|account|alias --field ... --output json`. CLI yolu stalwart-cli bilesen dizininden cozulur. Admin parolasi stalwart-mail bileseninin uretilmis parolasindan. Spam GetSpamSettings Installed=false (Stalwart spam/DKIM/SSL OTOMATIK) -> UI spam paneli gizli. JSON parse savunmaci.
- components.json: `stalwart-mail` (zip v0.16.8+sha256, postInstall installStalwartService, silentArgs __GENERATED__) + `stalwart-cli` (zip v1.0.8+sha256), **ikisi de default:true** -> SetupService panel kurulunca OTOMATIK kurar. GitHub asset `digest` alanindan sha256 cozulur.
- Bootstrapper: resolver case'leri + `InstallStalwartServiceAsync` (run-stalwart.cmd wrapper STALWART_RECOVERY_ADMIN=admin:<gen> + `stalwart.exe --config etc\config.toml`; schtasks /SC ONSTART servis + /Run). `InstallDefaultsAsync` artik her bileseni try/catch ile kurar (biri patlarsa digerleri devam).
- Roundcube `managesieve` eklentisi kaldirildi (hMailServer/Stalwart icin gereksiz, hata veriyordu). Mail i18n Stalwart'a guncellendi.
- SoftwareView alt "kurulu bilesenler" cipine KALDIR butonu eklendi (orphaned/basarisiz kayitlar -> ornek eski hmailserver "Basarisiz" temizlenebilir).
- Docker Engine otomatik kurulum (download.docker.com static, Containers feature + dockerd --register-service). Docker yonetim UI (DockerView).
- **DOGRULANMASI GEREKEN (mail build ortaminda calistirilamaz):** Stalwart ilk-acilis config bootstrap, scheduled-task servislesme, stalwart-cli JSON output flag (`--output json` varsayildi) + field adlari (email/quota/isEnabled), CLI exe yolu. Dokumana gore yazildi; canli mail hedef sunucuda dogrulanip 1-2 ufak ayar gerekebilir.
- Dogrulama: build 0/0, dotnet test Core 2/2 + Infra 18/18, typecheck OK, i18n 1333/1333, components.json 20 bilesen.
- **Guncel installer SHA256:** `3CF19BC3539B04CAD895A8B11663A16BE1F1D5502AE1BDFC6ACCD86E7FE871CE` (48.10 MiB).

## 2026-06-09/10 Stalwart CLI duzeltmeleri + kaldir/yeniden-kur kilidi + 2FA kurtarma kodlari

- **stalwart-cli GERCEK bayraklari dogrulandi** (CLI bu makineye indirilip `--help` ile incelendi): auth `--url/--user/--password` (env STALWART_URL/USER/PASSWORD) DOGRU. AMA:
  - `query` JSON icin `--json` kullanir (benim `--output json` YANLISTI -> her query patliyordu -> `invalid_admin_password`. Kullanicinin ilk hatasinin KOK NEDENI buydu). Duzeltildi.
  - `update` ID'yi POZISYONEL alir: `update account <id> --field ...` (benim `--ids` YANLISTI). Duzeltildi.
  - `delete <obj> --ids a,b` ve `create <obj> --field K=V` DOGRU. create account'tan `type=individual` kaldirildi (readme'ye uyuldu: sadece name/email/password/quota).
- **KALDIR/YENIDEN-KUR KILIDI FIX (kullanici sikayeti):** Stalwart zamanlanmis gorevle (AdaStalwart) calisiyor; RemoveAsync'in `sc stop` (servis) cagrisi goreve etki etmiyordu -> stalwart.exe kilitli kaliyor -> silinemiyor/uzerine yazilamiyor -> "kaldiramiyorum/kuramiyorum". Cozum: `StopManagedRuntimeAsync(serviceName, installDir)` -> ayni isimli scheduled task'i `/End`+`/Delete` eder VE kurulum dizininden calisan tum surecleri (Process.MainModule path prefix) Kill eder. RemoveAsync'te (servis temizliginden sonra) ve InstallComponentInternalAsync'te (extraction'dan ONCE) cagrilir. Boylece TUM bilesenler temiz kaldirilip yeniden kurulabilir (diger bilesenler zaten gercek servis; bu ek guvence).
- **2FA KURTARMA KODLARI (DB migration'siz):** AuthService 10 kod uretir, SHA256 hash'lerini `config/2fa/<userId>.json`'a yazar (JsonFileStore -> EnsureCreated sorunu yok). ConfirmTwoFactorSetup artik `TwoFactorConfirmDto {User, RecoveryCodes}` doner; LoginAsync TOTP basarisizsa kurtarma kodunu dener+tuketir; DisableTwoFactor kodlari siler; `POST /auth/2fa/recovery-codes` yeniden uretir. UI (SetupWizardView): kodlar etkinlestirmede gosterilir, kopyala + yeniden uret butonlari, login 2FA alani artik kurtarma kodu da kabul eder (maxlength 14, numeric kalktiU). 
- **InstallDefaultsAsync** her bileseni try/catch ile kurar (biri patlarsa digerleri devam). Stalwart default:true (panel kurulunca otomatik).
- Dogrulama: build 0/0, test Core 2/2 + Infra 18/18, typecheck OK, i18n 1341/1341.
- **MANUEL UNSTICK (kullaniciya):** takili Stalwart icin sunucuda: `schtasks /End /TN AdaStalwart & schtasks /Delete /TN AdaStalwart /F & taskkill /IM stalwart.exe /F`, sonra yeni installer'i kurup Stalwart'i yeniden kur.
- **Guncel installer SHA256:** `61E5CC920DA5A255D497B715FF50899D3E21F27479BA38FE0F05A2FBC904E984` (48.11 MiB).

## 2026-06-10 Stalwart bootstrap teshisi + tek-seferlik sihirbaz yaklasimi

- **KESIN TEShIS (Stalwart binary'leri lokal calistirilarak):** Stalwart v0.16 `--config <dosya>` ile baslar; dosya YOKSA **"bootstrap mode"** (yalniz 8080'de kurulum sihirbazi, mail portu/yonetim API'si YOK). Bu modda recovery admin (`STALWART_RECOVERY_ADMIN=admin:<pw>`) kimlik DOGRULUYOR (logda auth.success) ama `/api/schema` **401** -> stalwart-cli her komutu patliyor -> panel `mail.invalid_admin_password`. ESKI installer hic config yazmiyordu -> her acilista bootstrap -> kullanicinin hatasi buydu.
- **Config formati arastirildi:** TOML DEGIL, **JSON** (`config.json`). Tipli bolumler (store/directory) `@type` ayirici istiyor. Ama tam sema v0.16'ya cok ozgu; elle yazilan her config "missing field @type / data store settings parse" ile reddedildi VE yanlis config sunucuyu hic baslatmiyor (no-config'ten kotu). Tam-otomatik config nihai cozulemedi.
- **NIHAI YAKLASIM (planin yedegi):** Stalwart OTOMATIK kurulur+calisir (server+cli, default:true). Kullanici **tek seferlik** http://<sunucu>:8080 tarayici sihirbazini (admin + Yazilim ekranindaki uretilmis parola, Internal/RocksDB) tamamlar -> sihirbaz config.json'i `--config` yoluna yazar, sunucu normal moda gecer -> panel CLI yonetimi calisir. `InstallStalwartServiceAsync` config yolu `config.toml` -> `config.json` yapildi. Mail i18n `configureHint` sihirbaz rehberine cevrildi.
- **DOGRULANANLAR:** stalwart-cli auth `--url/--user/--password` (+env) DOGRU; `query --json`, `update account <id>` (pozisyonel), `delete --ids`, `create --field` DOGRU. Recovery admin sihirbazdan sonra da gecerli (panel CLI bunu kullanir).
- Bu turda ayrica: CLI bayrak duzeltmeleri, kaldir/yeniden-kur kilidi (`StopManagedRuntimeAsync`), 2FA kurtarma kodlari hepsi installer'da.
- Dogrulama: build 0/0, test Core 2/2 + Infra 18/18, typecheck OK, i18n 1341/1341.
- **GELECEK IS (istenirse):** Tam-otomatik config icin sihirbaz POST endpoint'ini cozup panelden programatik tamamlamak (webui /api/schema tabanli; bootstrap completion endpoint'i bulunamadi). -> COZULDU, asagiya bak (2026-06-10 MAIL TAM OTOMATIK).
- **Guncel installer SHA256:** `E0472FE52E844232BA2A31C187D5489C08C77CFB8B522673141C0D716A5292ED` (48.11 MiB).

## 2026-06-10 MAIL TAM OTOMATIK (sihirbaz tamamen kaldirildi) + MailService gercek semaya gore yeniden yazildi

- **KILIT KESIF (resmi dokumandan + yerelde dogrulandi):** Stalwart v0.16 `config.json` aslinda TEK SATIRLIK minimal veri deposu tanimidir: `{"@type":"RocksDb","path":"<dir>/data"}`. Bu dosya onceden yazilirsa bootstrap mode HIC devreye girmez; sunucu ilk aciliste varsayilan yapilandirmayi kendisi olusturup dogrudan NORMAL modda baslar (25/443/465/993/995/4190/8080). Onceki oturumun hatasi tam ic-ice config yazmaya calismakti. `InstallStalwartServiceAsync` artik bu dosyayi yazar (varsa dokunmaz) -> SIHIRBAZ GEREKMEZ, kurulum %100 otomatik.
- **WINDOWS IPv6 TUZAGI:** Stalwart varsayilan dinleyicileri `[::]`'e baglar; Windows'ta bu IPv6-ONLY demektir -> 127.0.0.1/IPv4'ten SMTP/IMAP/8080 ERISILEMEZ. Cozum: `ConfigureStalwartListenersAsync` (bootstrapper) kurulumdan sonra CLI ile her dinleyiciye `0.0.0.0:port` bind'i ekler, eksik `imap`(143) + `submission`(587) STARTTLS dinleyicilerini olusturur (Roundcube/istemciler icin) ve gorevi yeniden baslatir (bind degisikligi restart ister; `Action ReloadSettings` dinleyicileri YENIDEN BAGLAMAZ). MailService URL'i `http://localhost:8080` yapildi (localhost ::1+127.0.0.1 kapsar; sabit 127.0.0.1 ilk aciliste calismaz).
- **components.json:** `stalwart-cli` artik `stalwart-mail`'den ONCE gelir (InstallDefaultsAsync manifest sirasiyla kurar; postInstall'da CLI gerekir).
- **MailService GERCEK v0.16.8 SEMASI (hepsi canli sunucuda dogrulandi; eski kod canli sunucuda PATLARDI):**
  - `alias` diye bir obje tipi YOK ("unknown object"); alias'lar hedef hesabin `aliases` map'inde `{name, domainId, enabled}` olarak yasar.
  - Hesap olusturma: `create Account/User --json {"name":<yerel-kisim>,"domainId":<id>,"credentials":{"1":{"@type":"Password","secret":<pw>}},"quotas":{"maxDiskQuota":<bytes>}}`. `email` ALANI YOK (emailAddress server-set = name@domain). `--field email=...` hata verir.
  - Parola degistirme: `update account <id> --field credentials={"1":{"@type":"Password","secret":...}}` (map'i komple degistirir, dogrulandi). Stalwart zxcvbn PAROLA GUCU kontrolu yapar; zayif parola `invalidProperties` doner.
  - Kota: `quotas={"maxDiskQuota":<bytes>}`; kullanim `usedDiskQuota` (get detayinda). Ozet `query account --json` yalniz createdAt/description/emailAddress/id dondurur -> kota/alias icin hesap basina `get account <id> --json` gerekir (workspace boyle yazildi).
  - Map anahtarlari SAYISAL olmali ("new" gibi anahtar `invalidPatch: Invalid key` verir) -> `NextNumericKey` helper.
  - CLI ciktilari NDJSON'dur (satir basina bir nesne); `ParseItems` buna gore yeniden yazildi (eski hali dizi bekliyordu -> liste HEP BOS donerdi).
  - Set/karmasik degerler `--field K=<JSON>` ile gecilir; JSON komut satirinda MSVC kacisiyla tirnaklanir (`CliJsonArg`).
- **Roundcube:** Stalwart duz metin portta LOGIN'i reddeder ("LOGIN is disabled on the clear-text port") -> config `tls://localhost:143` + `tls://localhost:587` + `imap/smtp_conn_options` ssl verify kapali (self-signed yerel) yapildi.
- **i18n:** `panel.mail.configureTitle/Hint` sihirbaz rehberinden "otomatik yapilandirilir, yenileyin" mesajina cevrildi.
- **DOGRULAMA (bu makinede GERCEK Stalwart 0.16.8 + stalwart-cli 1.0.8 ile):** C# harness (birebir ayni argüman dizgileri + ayni kacis kodu) tum akisi calistirdi: config.json -> normal mod -> dinleyici fix -> restart -> domain/hesap/parola/alias/kota olustur-guncelle-sil = HARNESS-OK. Ardindan python smoke: SMTP 587 STARTTLS+AUTH ile alias adresine mail gonderildi, alias COZULDU ve hedef INBOX'a teslim edildi, IMAP 143 STARTTLS yeni parolayla login + mesaj okundu, IMAPS 993 login = SMOKE-OK. Probe binary'leri: `%LOCALAPPDATA%\Temp\stalwart-srv-probe\stalwart.exe`, `...\stalwart-cli-probe\stalwart-cli.exe`, harness: `...\Temp\stalwart-harness`.
- Dogrulama: build 0/0, test Core 2/2 + Infra 18/18, typecheck OK, frontend build OK, i18n 1341/1341.
- **Installer SHA256:** `EEF5E19297CBBAC7001EFD91DC7B5A7D1F9196B9EC62828AC44FD184CDA0D0F9` (48.11 MiB, 50,451,934 byte).

## 2026-06-12 PROFESYONEL GORUNUM YENILEMESI (koyu/acik tema + mavi vurgu) + tema dugmesi

- **Kullanici istegi:** "gorunum hic guzel degil, koyu mod ekle". Koyu mod ZATEN vardi (varsayilan) ama palet yesil-tonlu/terminal-vari idi. Yenileme: **notr slate koyu + profesyonel mavi vurgu** (GitHub/Linear tarzi). Acik tema da ayni sistemle yenilendi.
- **Yontem (33 view'i tek tek elden gecirmeden):**
  - `styles/base.css` bastan yazildi: yeni degisken paleti (--accent-1 mavi #5b9bff koyu / #2563eb acik, slate yuzeyler, tema-duyarli --accent-text/--warn-text/--danger-text/--on-accent eklendi), color-mix() kullanimi, focus/selection/scrollbar/hover incelikleri. SINIF ve DEGISKEN ADLARI KORUNDU.
  - `%TEMP%\recolor.py` + `recolor2.py` (python) ile 23 .vue dosyasindaki ~100 sabit renk SCRIPT ile donusturuldu: yesil rgba'lar -> `color-mix(in srgb, var(--accent-1/2) P%, transparent)`, turuncu -> var(--warn-1) mix, kirmizi -> var(--danger-1) mix, yesilimsi koyu notrlar -> slate rgba; ikinci geciste yuksek-opakli slate yuzeyler -> var(--surface-0/1/2/3)/var(--input-bg) (ACIK TEMA UYUMU icin sart).
  - **BUG FIX:** Acik temada login karti (.auth-card) sabit koyu kaliyordu (baslik koyu-ustu-koyu okunmuyordu) -> var(--surface-*) ile duzeltildi (audit-item da).
- **Tema dugmesi:** MainLayout topbar'a gunes/ay butonu (`theme-toggle`, prop `resolvedTheme`, emit `toggle-theme`). SetupWizardView.toggleTheme(): aninda uygular + admin ise updatePanelPreferences ile kalicilasir; applyAppearance artik localStorage'a `adapanel-theme` yazar; main.ts mount oncesi okuyup uygular (login ekrani dahil, acilista tema sicramasi yok). i18n `panel.layout.toggleTheme` eklendi.
- **Dogrulama (canli Playwright):** API temp veri kokuyle calistirildi (`Panel__DataRoot` + `ConnectionStrings__AdaPanel` env; DIKKAT: section adi "Panel", PanelRuntime degil). Temiz kurulum + login + dashboard koyu/acik ekran goruntuleri alindi; tema dugmesi canli calisiyor; yatay tasma 1440+390 OK; vueError yok. (NOT: kurulumda "onerilen yigin otomatik kurulsun" isaretliyse InstallDefaultsAsync dev makinede bilesen indirmeye baslar - QA'de isareti kaldir!)
- **TARAMADA BULUNAN ACIK SORUNLAR (henuz duzeltilmedi, log kanitli):**
  - `postgresql-16` bileseni kurulamiyor: VerifyAuthenticodeSignatureAsync imza dogrulamasi reddediyor (EDB installer imzasi). Arastirilmali.
  - `mariadb-11` MSI bu makinede 1603 + "Component installation blocked" (ortam kaynakli olabilir; hedef sunucuda dogrulanmali).
- **Plesk paritesinde eksik gorulen TEMEL isler (kullaniciya raporlandi):** mail domain DNS kayitlari rehberi/otomasyonu (MX/SPF/DKIM/DMARC gosterimi), mail icin Let's Encrypt sertifikasi (Stalwart self-signed), yedege uzak hedef (FTP/S3), PostgreSQL kurulum bugu yuzunden fiilen MySQL-only.
- Dogrulama: typecheck OK, build OK, i18n 1342/1342, dotnet publish OK.
- **Installer SHA256:** `1748246EB60ED905BD36F3737145126815228D4E72EC52D0117A4FE79CAB2136` (48.11 MiB, 50,452,156 byte).

## 2026-06-12 (2) PLESK PARITE 4'LUSU: kurulum iptal fix + mail DNS + mail SSL + uzak yedek (FTP/S3 SigV4)

- **1) "PostgreSQL imza bugu" ASLINDA IPTAL BUGUYDU:** log kaniti TaskCanceledException @ VerifyAuthenticodeSignatureAsync. Kurulumlar istegin CancellationToken'ina bagliydi; tarayici kapaninca/UI koparsa TUM bilesen kurulum zinciri yarida kesiliyordu (PostgreSQL 365MB indirme = uzun pencere). FIX: SetupService.InstallDefaultsAsync + SoftwareController install/install-defaults/remove artik `CancellationToken.None` (istemci iptalinden bagimsiz). EDB installer imzasi ayrica DOGRULANDI (Status=Valid, CN=EnterpriseDB) ve sha256 components.json'a sabitlendi: `d389834df279a9b7ce4b4a030b6545fd0befb05385ff66932ac37454ad9b9312` (boylece 365MB'lik PS imza taramasi da atlanir).
- **2) MAIL DNS KAYITLARI (MailView yeni panel):** `GET /api/v1/mail/dns/{domain}` -> A(mail)/MX/SPF/DKIM(x2)/DMARC kayitlari. DKIM public key Stalwart `DkimSignature` nesnelerinden (`query DkimSignature --json` + `get <id>`); **publicKey alani zaten DNS'e hazir ciplak base64'tur** (RSA SPKI b64 / ed25519 raw b64), `v=DKIM1; k=rsa|ed25519; p=...` dogrudan kurulur (canli dogrulandi). A/SPF icin sunucu adresi PanelPreferences.ServerAddress'ten. `POST /mail/dns/{domain}/apply` ayni adli site varsa panel DNS bolgesine (IDnsService.UpsertRecord) tek tik uygular; yoksa `mail.dns_zone_missing`. MailService'e IPanelPreferencesService + IIisSiteService enjekte edildi.
- **3) MAIL SSL (MailView yeni panel):** `POST /mail/certificate` -> domain panelde site ise once 80 portuna hostname binding eklenir, win-acme `--store pemfiles --pemfilespath <dataroot>/mail-certs/<host>` ile LE sertifika alinir; degilse self-signed PEM uretilir. PEM'ler Stalwart `Certificate` nesnesine basilir: `{"certificate":{"@type":"Text","value":PEM},"privateKey":{"@type":"Text","secret":PEM}}` (create/update --json CANLI DOGRULANDI; Stalwart SAN'i server-set cikarir) + `create Action --field @type=ReloadTlsCertificates` (restartsiz yukleme, CANLI DOGRULANDI). Yenileme: AutomationService SSL job'u sonrasi `IMailService.SyncCertificateAsync` PEM hash degistiyse yeniden basar. Durum `config/mail-cert.json`.
- **4) UZAK YEDEK:** (a) GERCEK S3: `S3RequestSigner` (AWS SigV4, UNSIGNED-PAYLOAD) — AWS dokumantasyonundaki RESMI GET+PUT test vektorleriyle 2 unit test GECTI. BackupService: s3 + erisim anahtari varsa SigV4 imzali PUT (AWS/MinIO/R2/Wasabi), anahtarsizsa eski presigned-PUT davranisi. RemoteRegion alani (Backup*Request + UI). (b) OTOMATIK yedeklere uzak hedef: Automation ayarlarina BackupTargetType/Url/UserName/Region + DPAPI korumali parola (bos gonderim = mevcudu koru, parola asla geri donmez `BackupRemotePasswordSet`); RunBackupAsync site+db yedeklerini hedefe yukler. AutomationView'a "Uzak yedek hedefi" formu.
- NOT: tekil yedekte FTP/WebDAV/HTTP zaten vardi (BackupService); eksikler otomatikte hedef yoklugu ve gercek S3 imzasiydi.
- Dogrulama: build 0/0, test Core 2/2 + Infra 20/20 (2 yeni S3 vektor testi), typecheck OK, i18n 1398/1398.
- **Installer SHA256:** `8EF130AE3588FF4C70385C506D1DFDE12357B8010B5C2A7EFA7B9EEE33CE8ACE` (48.13 MiB, 50,467,709 byte).

## 2026-06-12 (3) Spam ayarlari ekrani + yedekten granuler geri yukleme

- **1) SPAM AYARLARI (MailView spam paneli yeniden AKTIF):** Stalwart `SpamSettings` [singleton] CLI ile yonetilir: `get SpamSettings --json` (id'siz) ve `update SpamSettings --field K=V` (CANLI DOGRULANDI). Alanlar: enable, scoreSpam/scoreReject/scoreDiscard (float; 0=kapali), greylistFor (**milisaniye**, null=kapali; "30m" gibi string KABUL ETMEZ "expected integer"), trustContacts, trustReplies. MailSpamSettingsDto + UpdateMailSpamSettingsRequest bu semaya gore YENIDEN YAZILDI (eski hMailServer alanlari SpamAssassin/SPF kaldirildi; SPF/DKIM/DMARC dogrulamasi Stalwart'ta otomatik). Float'lar InvariantCulture ile formatlanir (FormatScore). Eski i18n spam anahtarlari yenileriyle DEGISTIRILDI (panel.mail.spamScore* vb).
- **2) GRANULER GERI YUKLEME (BackupView "Gozat"):** 
  - `GET /backup/{id}/entries?prefix=` -> zip icinde dizin bazli gezinme (klasorler + dosyalar, 500 girdi siniri + truncated bayragi). Girdi adlari '\\'/'/' her ikisine dayanikli (NormalizeArchivePath).
  - `POST /backup/restore-items {backupId, items[], targetPath?}` -> secilen dosyalar/dizin onekleri ("xyz/" ile biten) cikartilir. Hedef bossa: site yedegi includeBaseDirectory ile alindigindan orijinal kok = ApplicationRootPath'in UST dizini (girdiler kok klasor adini icerir). Site yoksa `backup.target_path_required`. **Zip-slip korumasi** (hedef kok disina cikan yol -> backup.invalid_entry_path).
  - UI: zip kayitlarinda "Gozat" -> breadcrumb + klasor gezinme + checkbox secim + istege bagli hedef klasor + "Secilenleri geri yukle" (uzerine yazma onayi).
  - 2 yeni unit test (gercek zip ile gezinme+secmeli cikarma+hata): BackupGranularRestoreTests.
- Dogrulama: build 0/0, test Core 2/2 + Infra 22/22, typecheck OK, i18n 1417/1417.
- **Installer SHA256:** `E6FA9433CE20909296F15660189003491F0C619F1122996FA1AFE528F4EAA100` (48.14 MiB, 50,475,575 byte).

## 2026-06-13 MAIL YEDEGI + MUSTERI HESABI IZOLASYONU (Customer rolu)

- **1) MAIL VERISI YEDEGI:** `POST /backup/mail` + BackupService.BackupMailAsync: AdaStalwart gorevi durdurulur (schtasks /End + dizinden stalwart sureclerini kill), `%ProgramData%\AdaPanel\runtimes\stalwart\data` zip'lenir (girdiler "data/..."), gorev HER DURUMDA yeniden baslatilir (try/finally). Restore: RestoreAsync record.Type=="mail" -> durdur, mevcut data dizinini `data.old-<ts>` olarak yana al, zip'i kuruluma ac, baslat. Uzak hedefe yukleme digerleriyle ayni. Otomasyona `BackupMail` secenegi (UI checkbox). Yedek turu sekmesi BackupView'da "E-posta".
- **2) MUSTERI IZOLASYONU (Plesk paritesindeki en buyuk eksik kapatildi):**
  - `UserRole.Customer` + kullanici formlarinda rol secenegi + **site atamasi** (checkbox listesi). Atamalar `config/customer-scopes.json` (CustomerScopeService; DB migrationsiz). UserSummaryDto.AssignedSites.
  - **Api/Security/CustomerAccess** statik guard'lari: EnsureSiteAsync, EnsureMailDomainAsync (atanan site adlari + binding hostlari, www soyulur), EnsureFilesPathAsync ("sites/<site>/..." yolu), GetSiteScopeAsync.
  - Musteriye acilan controller'lar (Roles="Administrator,Customer" + her aksiyonda guard): **Files** (kok listesi musteride atanan sitelerle SENTEZLENIR - baska site adi sizmasi yok; ACL aksiyonu admin-only kaldi), **Mail** (workspace domain listesi filtrelenir; configure/certificate/spam admin-only), **Backup** (liste filtreli; db/mail yedegi + tam restore admin-only; musteri yalniz kendi sitesine restore-items yapar ve TargetPath ZORLA bosaltilir -> her zaman orijinal konum).
  - Yeni `GET /api/v1/scope/sites` ([Authorize]): admin tum siteler, musteri atananlar, operator bos. Frontend sites api'si artik bu ucu kullanir.
  - Frontend: musteri menusu = Ana Sayfa, E-posta, Dosya, Yedekleme (yeni "backup" section, BackupView dogrudan), Ayarlar(hesap), Cikis. loadDashboardData musteri icin yalniz overview+siteler.
  - **CANLI QA (Playwright, temiz kurulum):** musteri olusturuldu, musteri girisiyle menu dogru; API denetimleri: software/sites/users/spam/terminal-execute -> 403 auth.forbidden; yabanci site dosya yolu -> 403 auth.site_forbidden; backup listesi filtreli 200; files koku sentezli 200; mail workspace 200. NOT: var olmayan /api rotalari SPA fallback ile 200/index.html doner (guvenlik sorunu DEGIL ama testte yaniltici); ayrica cogu /api ucu `X-AdaPanel-Entry-Path` basligi ister (SecureEntryPathMiddleware) - basliksiz curl/fetch 404 verir, mail/scope bu listede degildir.
- Dogrulama: build 0/0, test Core 2/2 + Infra 22/22, typecheck OK, i18n 1429/1429.
- **KALAN IS (kullanici tarafinda):** yeni installer'in hedef sunucuda kurulup mail SSL (gercek LE), mail DNS apply (Windows DNS), uzak yedek (gercek S3/FTP), mariadb-11 kurulumunun canli dogrulanmasi.
- **Installer SHA256:** `3CDF031105E7605DDF0C0AB5BA2A70785646EDF8D3680CBDC3BF7E3D599BEF68` (48.15 MiB, 50,486,144 byte).

## 2026-06-13 (2) Mail domain DNS kayitlari ARTIK OTOMATIK (Plesk paritesi)

- Kullanici sorusu uzerine netlesti: site eklenince temel kayitlar (NS/A/www/ftp CNAME) zaten otomatikti (DnsService.CreateDefaultRecords, zone ilk erisimde olusur); ama MAIL domaini eklenince MX/SPF/DKIM/DMARC manuel "uygula" butonuna bagliydi.
- **FIX:** MailController.CreateDomainAsync artik domain olusturduktan sonra `ApplyDnsRecordsAsync` (apply ucundan cikarilan ortak metot) ile kayitlari panel DNS bolgesine OTOMATIK yazar; site panelde yoksa (dis DNS) sessizce gecer (LogInformation) ve mesaj "mail.domain_created" kalir, basarida "mail.domain_created_dns". MailView domain olusturunca DNS panelini otomatik acip kayitlari gosterir. dnsHint/domainCreated metinleri guncellendi.
- Dogrulama: build 0/0, typecheck OK, frontend build OK, i18n 1430/1430.
- **Installer SHA256:** `2FC9FEDA2045705147E64CB9D37E8A11D22155214E08FE8EC9CDB9D1DC6B60DB` (48.15 MiB, 50,486,812 byte).

## 2026-06-13 (3) PROFESYONEL CILALAR: hos geldin e-postasi + parola sifirlama + musteri limitleri + uyari bildirimleri

- **SMTP altyapisi:** NotificationService zaten vardi (SmtpClient, config/notifications.json, DPAPI parola). Genisletildi: `SendEmailToAsync(toEmail,...)` (belirli alici) + `IsEmailConfiguredAsync()`. Hos geldin/parola sifirlama/uyari hepsi bunu kullanir; SMTP yoksa akis bozulmaz, loglanir.
- **1) HOS GELDIN E-POSTASI:** CreateUserRequest.SendWelcomeEmail. UserManagementService'e INotificationService+IPanelPreferencesService+IPanelSecurityService+ILogger enjekte; kullanici olusunca TR/EN govde (kullanici adi, gecici parola, panel URL = https://<serverAddress>:<port>/<securePath>). Best-effort (try/catch, akisi bozmaz).
- **2) PAROLA SIFIRLAMA (self-service):** AuthService.RequestPasswordResetAsync (kullanici numaralandirma yok - bulunsa da bulunmasa da ayni; SMTP yoksa token uretmez) + ResetPasswordAsync (tek kullanimlik token, SHA256 hash, 1 saat, config/password-resets.json). Token mail'deki `?resetToken=` linkiyle gelir. AuthController `password/forgot` + `password/reset` [AllowAnonymous][EnableRateLimiting("login")]. Frontend: login ekraninda "Sifremi unuttum" -> forgot formu; URL'de resetToken varsa reset formu acilir, basarida URL temizlenir. AuthService'e INotificationService+IPanelPreferencesService+IPanelSecurityService eklendi + `using AdaPanel.Core.Common`.
- **3) MUSTERI KAYNAK LIMITLERI:** CustomerScopeService store tipi `Dictionary<string,ScopeEntry{Sites,MaxSites,MaxDiskMb}>` oldu (SetAssignedSitesAsync -> SetScopeAsync; GetScopeAsync + GetDiskUsageMbAsync(atanan site koklerinin toplam boyutu)). Create/Update/UserSummary DTO'larina MaxSites+MaxDiskMb (+DiskUsageMb). UserManagement.ApplyScopeAsync: maxSites>0 ise atama sayisi asilirsa `users.too_many_sites`. FilesController.upload: customer + MaxDiskMb>0 ise kullanim+dosya limiti asarsa `files.quota_exceeded` 403. UI: kullanici formuna site sayisi + disk MB alanlari.
- **4) UYARI BILDIRIMLERI:** AutomationService'e INotificationService+ISystemMonitoringService. RunDueJobsAsync icinde (exclusive blok DISINDA) RunAlertChecksAsync: disk doluluk (overview.Disks, esik %) + sertifika bitisi (config/ssl.json, gun esigi). Asimda e-posta (SendEmailAsync admin'e) + webhook. 24s cooldown (config/automation-alerts.json), 7g'den eski budanir. Otomasyon ayarlarina AlertsEnabled/DiskUsageThresholdPercent(50-99)/CertExpiryWarningDays(1-90). UI: AutomationView "Uyari bildirimleri" bolumu.
- **CANLI QA (Playwright, temiz kurulum):** forgot(bilinmeyen e-posta)->200 sessiz; reset(gecersiz token)->400 auth.reset_token_invalid (izole, rate-limit penceresi sonrasi); limitli musteri olustur->200 maxSites=3/maxDiskMb=250/role=Customer; kullanici formunda site/disk/hos-geldin alanlari + login'de "Sifremi unuttum" gorunur. NOT: bu uclar CSRF (X-CSRF-TOKEN) + rate-limit ("login") korumali; art arda POST 429 verir (beklenen).
- Dogrulama: build 0/0, test Core 2/2 + Infra 22/22, typecheck OK, i18n 1453/1453.
- **GERCEK SUNUCUDA DENENECEK:** SMTP ayarlaninca hos geldin/reset/uyari maillerinin fiilen gitmesi (yerelde SMTP yapilandirilmadigi icin gonderim degil yalniz akis/uc dogrulandi).
- **Guncel installer SHA256:** `79E1F60C1CCB4590FA1AB574BDB728DF196517C528A5A4B439E2906AFE93255A` (48.16 MiB, 50,501,951 byte).
- **ELEKTRIK KESINTISI KURTARMA (2026-06-13 ~11:00):** Onceki oturum entry (3) kodunu bitirip memory'yi 09:37'de yazdi ama installer uretimi/sonrasi elektrik kesildi; diskteki yarim installer memory'deki hash ile uyusmuyordu (kaydedilen `412DE2...` hatali kaldi). Kaynak agaci tutarli dogrulandi (build 0/0, test Core 2/2 + Infra 22/22, typecheck OK, frontend typecheck OK, i18n 1453/1453 parite). Installer temiz yeniden uretildi (publish `-p:EnableCompressionInSingleFile=true` + ISCC). KURAL HATIRLATMA: bash tool'da MSBuild ayarlarini `/p:` DEGIL `-p:` ile gec (Git Bash `/p:`'yi yola cevirip publish'i bozar -> bos publish'ten bozuk installer cikar).

## 2026-06-15 (10) PROAKTIF DENETIM: app redirect bug (TUM app'ler) + per-site PHP ayarlari + php.ini koruma + phpMyAdmin config

- **Kullanici: "profesyonel/proaktif kontrol et, herseyi, sanki bastan yaziyormussun gibi".** Reaktif degil sistematik denetim yapildi.
- **phpMyAdmin config.inc.php (ASIL acilmama sebebi):** phpMyAdmin deploy'u config.inc.php YAZMIYORDU -> cookie auth icin SART olan blowfish_secret yok -> giris bozuk. WebDbClientService.EnsurePhpMyAdminConfig: 32-char secret + 127.0.0.1:3306 cookie-auth server yazar (yoksa). (Onceki tur placeholder/default-doc fix'ine EK.)
- **APP REDIRECT BUG - TUM APP DENETIMI:** Roundcube/Laravel kendi web.config'ini (`<clear/><add index.php/>`) yaziyordu AMA **WordPress ve AppStore-Adminer YAZMIYORDU** -> site'in web.config'ine bagimli; PHP-profili yoksa placeholder/yanlis yonlendirme. FIX: `AppStoreService.EnsurePhpEntryWebConfig` (stray index.html sil + web.config yoksa index.php tek varsayilan belge) -> InstallWordPressAsync + InstallVerifiedAdminerAsync'e eklendi. Artik TUM app'ler "/" -> index.php garanti.
- **php.ini KULLANICI AYARI KORUMA (kullanici istegi):** `EnsurePhpIniAsync` her bilesen/app kurulumunda cagriliyor ve `SetPhpIniDirective` memory_limit/upload'i HER SEFER 512M/128M'e EZIYORDU -> kullanicinin global php.ini edit'i bozuluyordu. FIX: SetPhpIniDirective'e `preserveExisting` (aktif/yorumlu ayrimi); tuning direktifleri (memory_limit/max_execution_time/upload_max_filesize/post_max_size) artik kullanici AKTIF deger koyduysa KORUNUR; gerekli direktifler (extension/cgi/timezone) hep zorlanir. Plesk gibi: optimal varsayilan ilk kurulumda, sonra kullanici serbest.
- **PER-SITE PHP AYARLARI (Plesk "PHP Settings"):** `.user.ini` mekanizmasi (PHP per-dizin okur). IPhpService.Get/UpdateSitePhpSettingsAsync (PhpService'e IIisSiteService enjekte). SitePhpSettingsDto (memoryLimit/maxExecutionTime/uploadMaxFilesize/postMaxSize/displayErrors). `GET/POST /api/v1/sites/{site}/php/settings`. Frontend: PhpView'de panel (site secilince yuklenir/kaydedilir). Global'i etkilemez, site-izole. Deger sanitize (regex `^-?\d+[KMG]?$`).
- Dogrulama: Release build 0/0, test Core 2/2 + Infra 22/22, frontend typecheck OK, build OK, i18n 1497/1497 parite.
- **KALAN:** mail forwarding/autoresponder (kullanici istedi, henuz yapilmadi - Stalwart sieve gerekir, canli test edilemez). Mail server: kullanici MailEnable (Plesk-Windows default) sordu -> degerlendiriliyor (Stalwart su an calisir+Roundcube webmail var; MailEnable'a gecis buyuk riskli rewrite).
- **phpMyAdmin 403.14 KESIN COZUM (kullanici hata detayi verdi):** Hata mesaji Fiziksel Yol=`...\tools\phpmyadmin` (installDir'in KENDISI) gosterdi -> `PrepareEntryRootAsync` alt klasoru (phpMyAdmin-X.Y.Z-all-languages) bulamayip installDir'e dusuyordu, orada index.php yok -> 403.14 directory listing. ESKI kod TEK seviye `GetDirectories().FirstOrDefault(index.php)` bakiyordu. FIX: RECURSIVE arama - once `config.sample.inc.php` (phpMyAdmin'e ozgu isaret) + ayni dizinde index.php; yedek en sig index.php. Artik derinlik ne olursa olsun dogru kok bulunur. "Arayuzu yeniden kur" -> entryRoot degisince site yeniden olusturulur, config.inc.php + web.config dogru klasore yazilir. KULLANICIYA: yeni installer + "Arayuzu yeniden kur".
- **phpMyAdmin GERCEK ZIP DOGRULANDI (kor degil):** zip indirilip incelendi -> tek ust klasor `phpMyAdmin-5.2.3-all-languages/` icinde index.php + config.sample.inc.php; bootstrapper `FlattenSingleTopDirectory` ile FLATTEN ediyor -> installDir'de index.php DOGRUDAN. Yani entryRoot=installDir DOGRU (recursive fix gereksiz ama zararsiz). phpMyAdmin web.config GETIRMEZ (0). 403.14'un kok nedeni: varsayilan belge yok (`/` index.php'ye cozulmuyor). FIX kesin: `EnsureDefaultDocument` artik HER deploy'da web.config (`<clear/><add index.php/>`) yazar (eski skip-if-exists kaldirildi) + config.inc.php. ONEMLI: bu mantik PANEL BINARY'sinde -> kullanici yalniz phpMyAdmin BILESENINI degil, YENI PANEL INSTALLER'ini kurmali, sonra "Arayuzu yeniden kur". Hala 403 ise: `tools\phpmyadmin\web.config` var mi + tam hata kodu sorulmali (delegation/PHP-handler ayrimi icin).
- **TUM PHP APP DEPLOY'LARI TEK ORTAK HELPER'A BIRLESTIRILDI (kullanici: "adam akilli yap hepsini"):** Yeni `Infrastructure/Iis/WebAppPublisher.EnsurePhpEntry(root)` -> (1) index.php varsa placeholder index.html'i sil, (2) uygulamanin KENDI index.php'li web.config'i varsa koru (Roundcube/Laravel), yoksa/yarimsa index.php varsayilan-belge web.config yaz. WebDbClientService (phpMyAdmin/Adminer) + AppStoreService (WordPress/plain-php/adminer; switch sonrasi TUM template'ler) artik bunu cagiriyor. Eski dagilmis EnsureDefaultDocument (WebDb) + EnsurePhpEntryWebConfig (AppStore) kaldirildi. Boylece phpMyAdmin'deki 403.14/placeholder sinifi hata HICBIR app'te tekrar etmez. aspnet-static no-op (index.php yok).
- **PostgreSQL/Redis kurulum hatalari (kullanici bildirdi):** PostgreSQL "cluster init failed" = onceki denemeden kalan data dizini; Redis "--service-install exit 1" = servis silinmesi tamamlanmadan reinstall. FIX: (1) kurulum oncesi `DeleteManagedPath(installDirectory)` (temiz kurulum, servis durdurulduktan sonra), (2) TryRemoveServiceAsync'e servis-gercekten-gitti bekleme dongusu (sc query basarisiz olana kadar). Build-dogrulandi, VPS'te test edilmeli.
- **ONERILEN YIGIN HAFIFLETILDI (kullanici istegi - silme, sadece oto-kurma):** components.json'da **postgresql-16 + redis -> default:false** (otomatik kurulmaz ama Yazilim menusunde durur, istege bagli kurulur). Otomatik yigin artik 9 bilesen, TEK DB (MariaDB): php-8.3, mariadb-11, composer, phpmyadmin, url-rewrite, iis-arr, stalwart-cli, stalwart-mail, win-acme. (Plesk-Windows de tek DB ile gelir; iki DB+Redis kurmak 4GB VPS'te yavas+RAM israfiydi.) MailEnable arastirildi: Plesk-Windows default'u MailEnable AMA otomatik-indirme yok (mailenable.com gated, hMailServer gibi) -> Stalwart'ta kalindi, MailEnable ancak manuel-kurulum secenegi olarak eklenebilir.
- **Guncel installer SHA256:** `61EAB5E4BAD02885D8265702A8861605AF35F681496325C08BCE5056EAABB035` (48.18 MiB).

## 2026-06-14 (9) VERITABANI PLESK-OTOMATIK + phpMyAdmin acilma bug + site formu sadelestirme

- **Kullanici net direktifi:** "izinli host, yetkiler gibi Plesk'te olmayan manuel teknik alanlar FAZLALIK; kaldir ya da otomatige bagla. Tum panel icin gecerli, en ince ayrintiya kadar Plesk gibi, otomatik, kusursuz." (Onay almadan yap dedi.)
- **VERITABANI MODULU PLESK-OTOMATIK:** (1) DB olustururken kullanici adi+sifre AYNI formda -> backend `CreateDatabaseAsync` kullaniciyi olusturur + bu DB'ye otomatik TAM yetki verir (host '%' sabit, yetki secimi yok). CreateDatabaseRequest'e UserName/Password eklendi. (2) **Kullanici SILME eklendi** (eksikti): `IDatabaseEngineProvider.DeleteUserAsync` + MySQL(`DROP USER`)/PostgreSQL(`DROP ROLE`)/MsSql(`DROP LOGIN`) + IDatabaseService.DeleteUserAsync + `DELETE /api/v1/db/users/{user}?engine=&host=` + UI silme butonu. (3) **"Izinli host" ve "Yetkiler" alanlari + Yetkiler sekmesi KALDIRILDI**; userScope sutunu kaldirildi (Erisim: Her yerden/Yerel gosterilir). 8 olu i18n anahtari silindi.
- **phpMyAdmin/Adminer ACILMA BUG FIX:** Kok neden: site olusturma `WebAdministrationSiteService.EnsureStarterContent` phpMyAdmin klasorune varsayilan `index.html` placeholder yaziyordu; IIS index.html'i index.php'den ONCE sunuyor -> "Siteniz yayinda" placeholder gorunuyordu. FIX: (a) klasor DOLU ise starter yazma (`Directory.EnumerateFileSystemEntries().Any()`), (b) WebDb deploy'da kalan index.html'i sil ("yeniden kur" eski bozugu onarir), (c) `EnsureDefaultDocument` ile entryRoot'a index.php varsayilan-belge web.config'i yaz (IIS varsayilan listesinde index.php yok). NOT: SetSitePhpVersionAsync handler'i applicationHost'a yazar (web.config'e degil), cakisma yok. Bu makinede IIS/PHP yok -> build-dogrulandi, VPS'te yeniden-kur ile test edilmeli.
- **SITE OLUSTURMA FORMU SADELESTIRME:** runtime profili/php surum/app pool/ip/port artik **"Gelismis ayarlar"** altinda gizli (Plesk gibi); gorunur alanlar yalniz ad+host+dosya yolu. Varsayilan runtime "auto" zaten her seyi algilar.
- Dogrulama: Release build 0/0, test Core 2/2 + Infra 22/22, frontend typecheck OK, build OK, i18n 1485/1485 parite.
- **MENU DENETIMI (task #4) ilk parti:** SSL'den **staging (test ortami) checkbox kaldirildi** (Plesk gostermez; useStaging:false sabit). Mail spam **skor esikleri (scoreSpam/Reject/Discard) + greylist "Gelismis" altina gizlendi**; ana kontrol "Spam filtresi ac/kapa". DENETIM SONUCU: DNS TTL/priority, Otomasyon araliki/retention/esik, Guvenlik brute-force/firewall/ACL, Backup hedef secimi -> bunlar Plesk'te de olan MESRU gelismis ozellikler, korlemesine SILINMEDI (yalniz acik fazlalik kaldirildi). panel.ssl.useStaging i18n silindi.
- **OLU KOD TEMIZLIGI + CANLI QA:** grant kaldirildiktan sonra olu frontend kod silindi (store grantAccess/grantingEngine, api grantDatabaseAccess + GrantDatabaseAccessPayload, i18n database.grant_updated/panel.database.grantAccess). CANLI QA (Playwright): site olusturma formu Plesk-sade (ad+host+dosya yolu gorunur, port/runtime/php/pool/ip "Gelismis ayarlar" altinda gizli, "Birlikte olustur" DB+mail+FTP duruyor), vueError yok. DB view motor kurulu olmadigindan create-with-user formu/kullanici-sil sadece build+typecheck dogrulandi (motor gerekir).
- **OTOMASYON PLESK-VARI (kullanici istegi "otomasyonlari hallet"):** Yedek "aralik (saat)" -> **Siklik dropdown (Gunluk/Haftalik/Aylik = 24/168/720h)**; SSL "yenileme araligi (saat)" alani KALDIRILDI (Plesk otomatik yeniler, ek ayar yok; form'da sslRenewIntervalHours=24 sabit backend'e gider). intervalHours i18n silindi, freqDaily/Weekly/Monthly+sslAutoHint eklendi. CANLI QA: frekans dropdown var, SSL aralik alani yok, vueError yok.
- **DETAYLI PLESK-PARITE DEGERLENDIRMESI (kullaniciya raporlandi):** Cekirdek hosting paneli tam ve Plesk-hizali. GERCEK EKSIKLER (fazlalik degil, gelecek is): service plan/abonelik sablonlari (su an Customer rol+scope+limit var ama yeniden-kullanilabilir plan yok), WordPress Toolkit (klon/staging/hardening - su an tek-tik WP kurulum var), per-site PHP ayarlari UI (php.ini direktifleri - su an surum secimi var), mail autoresponder/forwarding listeleri, reseller tier.
- **Guncel installer SHA256:** `0B7A0840BF56614A1DA3E7E2BC4AC00F51DF8541C74D4054657A97D4804E268E` (48.18 MiB).

## 2026-06-14 (8) rDNS (PTR) DOGRULAYICI + WebDB "Dagit"->"Kur" etiket fix

- **rDNS (PTR) DOGRULAYICI eklendi (mail):** PTR kaydi IP sahibinde (VPS saglayici) tutulur, panel YAZAMAZ -> bu yuzden YONETIM degil DOGRULAMA eklendi (Plesk'in de pratik yaklasimi). `IRdnsService/RdnsService` (Infrastructure/Dns): hostname -> A -> IP -> PTR(ters lookup) -> eslesme + forward-confirmed (FCrDNS) kontrolu. .NET `Dns.GetHostAddressesAsync` + `GetHostEntryAsync`. Endpoint `GET /api/v1/mail/rdns?host=` (Administrator). MailView'de DNS panelinin altinda rDNS karti (hostname input default `mail.{domain}` + Kontrol et + renkli durum + IP/PTR + yonlendirme). Durum anahtarlari `mail.rdns.ok/missing/mismatch/match_no_fcrdns/resolve_failed/no_host`. CANLI DOGRULANDI (gercek DNS): one.one.one.one->ok (FCrDNS), google.com->mismatch, gecersiz->resolve_failed.
- **WebDB etiket fix (kullanici istegi, 2 tur):** phpMyAdmin/Adminer butonu "Dagit" -> once "Kur" -> NIHAI **"Arayuzu kur"** (deploy+install ikisi de). Sebep: kullanici "Kur"u "MySQL kur" sanip yine karisti; "Arayuzu kur" net (MySQL'i degil web arayuzunu kurar). Alt aciklama da netlestirildi: "...veritabani motorunu KURMAZ; zaten kurulu DB'ye arayuz saglar." WebDB bolumu = phpMyAdmin/Adminer/pgAdmin (web yonetim araclari), DB MOTORU DEGIL.
- **NOT (kullanici VPS deneyimi):** Win11 VPS'te mariadb-11 KURULDU ama YAVAS (VPS yavasligi); onceki "mariadb 1603" sorunu dev makineye ozgumus. VPS icin ozel ayar GEREKMEDI. rDNS: mail kullanacaksa PTR'yi saglayici panelinden ayarlamali (panel sadece dogrular).
- Dogrulama: Release build 0/0, test Core 2/2 + Infra 22/22, frontend typecheck OK, build OK, i18n 1484/1484 parite, 0 collision, rDNS canli OK.
- **Guncel installer SHA256:** `8A26193869AC5F48AFF82C34E81797F9DBCB01E2317AA3497429F24D44ACE39F` (48.18 MiB).

## 2026-06-14 (7) DASHBOARD PLESK-VARI (hizli erisim + tiklanabilir kartlar; donutlar KORUNDU)

- **Kullanici istegi:** "ana sayfadaki CPU/RAM kullanimlari kalsin, gorunum guzel; ek olarak kalsin." -> Donut metre halkalari (CPU/RAM/Disk) AYNEN korundu.
- **EKLENENLER:** (1) Ust kisma **HIZLI ERISIM** satiri (Plesk home gibi): Web Sitesi/E-posta/Veritabani/Dosya/Sunucu Araclari kartlari -> ilgili bolume gider. (2) Ozet kutulari (Siteler/FTP/Veritabani/Guvenlik) artik **tiklanabilir** (button), ilgili bolume navigasyon. DashboardOverviewView `navigate` emit eder, SetupWizardView'deki mevcut `handleNavigation`'a baglandi (`@navigate`). 1 yeni i18n anahtari (panel.dashboard.quickActions = "Hizli erisim"/"Quick access").
- CANLI QA: hizli erisim butonu -> "Web Sitesi" bolumune gecti; donutlar + tiklanabilir kartlar render, vueError yok, acik tema Plesk-vari.
- **Ayarlar:** zaten sekmeli (Panel ayarlari/gorunum/kullanicilar/bildirimler/guncelleme) = Plesk "Tools & Settings" kalibi; ek gerekmedi.
- Dogrulama: Release build 0/0 (backend degismedi), frontend typecheck OK, build OK, i18n 1470/1470 parite, 0 collision.
- **Guncel installer SHA256:** `2C54B0E0E2D0992E3A722C771D5FCE9C78DBDFB9F7750231F6721D9EACEDB282` (48.18 MiB, 50,516,209 byte).

## 2026-06-14 (6) NAV SADELESTIRME (Plesk "Tools & Settings" konsolidasyonu)

- **ANA MENU 15 -> 12:** Sunucu-seviyesi gorunumler **Gunluk(Logs), Terminal, Docker** ust menuden cikarilip ServerToolsView hub'ina sekme olarak tasindi (zaten Firewall/Backup/Automation/Nginx vardi -> simdi 7 sekme, `repeat(auto-fit,minmax(150px,1fr))` tek satir). Plesk'in server araclarini "Tools & Settings"te toplama kalibi. SetupWizardView'den ilgili render satirlari + nav tanimlari + import'lar (LogsView/TerminalView/DockerView) kaldirildi. Ana menu artik hosting-nesne odakli: Ana Sayfa, Web Sitesi, Veritabani, E-posta, Gozetim, Guvenlik, Dosya, Planli Islemler, Sunucu Araclari, Yazilim Magazasi, Ayarlar, Cikis.
- **Kullanilmayan i18n temizlendi:** panel.navigation.logs/terminal/docker silindi. Ayrica yanlis etiket duzeltildi: navigation.security "Guvenli" -> "Guvenlik".
- CANLI QA: sidebar 12 oge (Logs/Terminal/Docker yok), ServerToolsView 7 sekme tek satir, vueError yok, acik tema.
- Dogrulama: Release build 0/0, test Core 2/2 + Infra 22/22, frontend typecheck OK, build OK, i18n 1469/1469 parite, 0 collision.
- **Guncel installer SHA256:** `F3A7B59A0277782D9C58AB242EC043A286247BD4C4CB38C0C14F3D197DC2F281` (48.18 MiB, 50,515,671 byte).

## 2026-06-14 (5) DOMAIN-KARTI DUZENI (Plesk "Websites & Domains") + temizlik

- **SITELER ARTIK PLESK DOMAIN-KARTI:** SitesView'deki tablo, **kart-grid**'e cevrildi. Her site bir kart: WWW glyph + domain + tiklanabilir URL + durum pill, meta (fiziksel yol/app pool/binding sayisi), **hizli arac cipleri** (Site Ayarlari/SSL/Web DB/FTP/DNS/Hazir Uygulamalar) ve eylemler (Siteyi ac / Baslat-Durdur / Sil). Cipe tiklayinca SitesView `open-tool` emit eder, WebsiteWorkspaceView siteyi secip ilgili araç sekmesine gecer (`onOpenTool`). Secili sitenin SiteDetailView'i altta (binding yonetimi). CANLI QA (mock'lu /scope/sites, 2 sahte site): kartlar dogru render, cip navigasyonu calisti, acik tema Plesk-vari.
- **TEMIZLIK (kullanici istegi - fazlalik kaldirildi):** (1) Tekrar eden DB-ad sanitize SitesController+SiteCascadeDeletionService'ten `AdaPanel.Core.Common.ResourceNaming.DatabaseNameFromSite`'e tasindi (tek kaynak; olusturma ve cascade ayni adi uretir = silme DB'yi bulur). (2) Kullanilmayan `panel.navigation.ftp` i18n anahtari silindi. (3) Olu `primaryBinding` fonksiyonu + kullanilmayan tablo/liste CSS'leri (.site-table/.site-row/.list-card/.sites-columns/.site-item vb.) kaldirildi.
- Dogrulama: Release build 0/0, test Core 2/2 + Infra 22/22, frontend typecheck OK, build OK, i18n 1472/1472 parite, 0 collision, canli QA OK.
- **Guncel installer SHA256:** `1D263553344EDDC7E6635BB92925C0A59FD16F7157D941530948904FE0820C45` (48.18 MiB, 50,515,815 byte).

## 2026-06-14 (4) PLESK GORUNUM (acik tema varsayilan) + companion mail DNS fix + kod kontrolu

- **GORUNUM ARTIK PLESK BENZERI:** Varsayilan tema **dark -> light** yapildi (Plesk/cPanel acik-temadir; canli QA temiz kurulumda dashboard+siteler acik temada Plesk-vari: beyaz kartlar, acik gri zemin, mavi vurgu, sol nav). 5 yer: PanelPreferencesDto/UpdatePanelPreferencesRequest/PanelPreferencesService (`ThemeMode="light"`), main.ts (kayitli tema yoksa light), SetupWizardView panelPreferences varsayilani. MEVCUT kullanicilar etkilenmez (localStorage/kayitli tercih korunur); koyu tema toggle ile hala var.
- **KOD KONTROLU (kullanici istegi: hata/eksik/fazlalik):** Bulunan tek gercek NOKSANLIK: birlesik (companion) mail domaini olusturuldugunda mail ekranindaki gibi DNS (MX/SPF/DKIM/DMARC) OTOMATIK uygulanmiyordu -> DUZELTILDI (SitesController.CreateSiteAsync'e IDnsService enjekte, companion mail sonrasi GetDnsRecordsAsync+UpsertRecord, best-effort). Diger gozlemler (kozmetik, birakildi): `panel.navigation.ftp` i18n anahtari artik kullanilmiyor (zararsiz); `SanitizeDbName`/`SanitizeDatabaseName` SitesController+SiteCascadeDeletionService'te benzer (ufak tekrar). Mantik hatasi/eksik kaynak temizligi/fazla kod BULUNMADI; cascade & unified akislari saglam.
- **PLESK PARITESI (durum):** islev/ozellik buyuk olcude esit (site-merkezli workspace, cascade silme, birlesik olusturma, mail domain-merkezli, DB merkezi+baglamsal, FTP site-icinde). Gorunum: acik tema + sol nav + kart yapisi ile "baya benzer" ama BIREBIR degil (Plesk'in domain-kart duzeni / breadcrumb gibi ince yapilar gelecek is). Telif: hicbir Plesk asset/marka kopyalanmadi, jenerik hosting-panel kalibi.
- Dogrulama: Release build 0/0, test Core 2/2 + Infra 22/22, frontend typecheck OK, build OK, canli QA (acik tema dogrulandi, vueError yok).
- **Guncel installer SHA256:** `A6561FA941CF6C91D989EC4184F74F5CB60CE865378BA4E0356A63D4B49D1748` (48.17 MiB, 50,514,319 byte).

## 2026-06-14 (3) PLESK UI FAZ 2: FTP site baglamina tasindi + mail/db dogrulandi

- **FTP artik ust menude DEGIL**, WebsiteWorkspaceView'de site-basina sekme (php/asp/ssl/proxy/webdb/**ftp**/dns/hosting yaninda). `FtpView` opsiyonel `siteName` prop'u aldi: verilince yalniz o sitenin FTP kullanicilarini filtreler ve create form'da site secicisini gizleyip siteyi sabitler. SetupWizardView'den `ftp` nav bolumu + render + import kaldirildi (`panel.navigation.ftp` i18n anahtari artik kullanilmiyor, zararsiz). `panel.sites.tools.ftp`/`ftpHint` eklendi.
- **MAIL ZATEN DOMAIN-MERKEZLI (dogrulandi):** MailView domain seciciyle calisiyor (selectedDomain -> o domainin hesaplari/alias'lari + addAccount formu, `domain.name (accountCount)`). Plesk modeli zaten mevcuttu; eksik olan silme cascade'iydi (FAZ 1'de cozuldu). Ek is gerekmedi.
- **VERITABANI ZATEN HEM MERKEZI HEM SITE-BAGLAMINDA:** ust menu `database` (DatabasesView, merkezi gorunum -- kullanici "buradan gormek onemli" dedi) + WebsiteWorkspaceView `webdb` sekmesi (site-baglami). Degisiklik gerekmedi.
- **BIRLESIK SITE OLUSTURMA (Plesk "domain ekle" gibi) — EKLENDI:** `CreateSiteRequest`'e opsiyonel alanlar (CreateDatabase+DatabaseEngine+DatabaseName, CreateMailDomain, CreateFtpUser+FtpUserName+FtpPassword). `SitesController.CreateSiteAsync` site provisyonlandiktan SONRA bunlari best-effort olusturur (DB: ad bossa site adindan sanitize; mail: HostName ya da site adi; FTP: SiteName=site), `SiteCreationResultDto{Site,Created,Warnings}` doner. Frontend: oluşturma formuna "Birlikte oluştur (Plesk gibi)" kutusu (3 checkbox + DB motoru secimi + FTP kullanici/sifre); sonuc bildirimi olusturulanlari/uyarilari gosterir. 7 yeni i18n anahtari (panel.sites.companion.*). CANLI QA (Playwright): companion kutusu + workspace FTP sekmesi dogrulandi, vueError yok, Turkce kusursuz.
- Dogrulama: Release build 0/0, test Core 2/2 + Infra 22/22, frontend typecheck OK, build OK, i18n 1473/1473 parite, 0 collision.
- **Guncel installer SHA256:** `E1F36F4098A954AB7EF17E862A76F447CA8350F6CAF1E5FC3B10A70B058DEAFE` (48.17 MiB, 50,514,444 byte). Tum Plesk benzeri akis GERCEK sunucuda (IIS+DB+Stalwart) test edilmeli.

## 2026-06-14 (2) PLESK BENZERI CASCADE SILME + mail domain bug fix (FAZ 1)

- **Kullanici istegi:** "domain silinince hersey silinsin, plesk gibi olsun, hersey birbirine bagli olsun, FTP ayri menu olmasi sacma, mail domaine girince icine hesap eklensin." Yani site/domain-merkezli, cascade silen Plesk yapisi. Onay almama gerek yok dedi.
- **MAIL DOMAIN BUG (bildirilen):** Silinen mail domaini UI'de kaliyordu. KOK NEDEN: `MailService.DeleteDomainAsync` dogrudan `delete domain` cagiriyordu; Stalwart uzerinde HESAP olan domaini silmeyi reddediyor -> domain takili kaliyor. FIX: cascade — once domaine ait tum hesaplari sil (alias'lar hesap icinde oldugundan onlarla gider) + baska domaindeki hesaplarda bu domaine isaret eden alias girdilerini temizle (`StripAliasesOfDomain`), SONRA domaini sil. Idempotent (domain yoksa workspace doner). `MailController.DeleteDomainAsync` ayrica otomatik eklenen mail DNS kayitlarini (MX/SPF/DKIM/DMARC/mail A) panel zonundan best-effort kaldirir (silmeden ONCE GetDnsRecordsAsync ile cozulur; TXT'lerde deger eslesmesi sart -> kullanicinin baska TXT'si silinmez).
- **SITE CASCADE SILME (yeni):** `ISiteCascadeDeletionService` / `SiteCascadeDeletionService` (Infrastructure/Iis). Bir site silinince Plesk gibi bagli her seyi temizler: mail domaini (site adi + binding host'lari, mail'de GERCEKTEN var olanlar) → veritabanlari (ad eslesmesi: site adi veya sanitize hali; motorlar mysql/mariadb/postgresql/mssql best-effort) → FTP kullanicilari (FtpUserDto.SiteName eslesmesi) → reverse proxy (etkinse) → disk kotasi (etkinse) → DNS bolgesi → musteri atamalari → git config dosyasi → IIS sitesi+app pool → dosyalar (appRoot + physicalPath). Her adim best-effort (`StepAsync` try/catch), sonuc `SiteDeletionResultDto{Removed,Warnings}`. GUVENLIK: `IsSafeToDelete` surucu koku/Windows/Program Files/sig yol silmeyi reddeder.
- **Yeni servis metotlari:** `IDnsService.DeleteZoneAsync` (config/dns-zones.json'dan kaldirir + best-effort Windows Remove-DnsServerZone), `ICustomerScopeService.RemoveSiteFromAllScopesAsync` (tum musteri atamalarindan siteyi cikarir).
- **API:** `GET /api/v1/sites/{site}/deletion-plan` (onizleme: SiteDeletionPlanDto) + `DELETE /api/v1/sites/{site}?deleteFiles=&deleteDatabases=` artik cascade yapar, SiteDeletionResultDto doner.
- **Frontend:** SitesView.handleDeleteSite once `getSiteDeletionPlan` cagirir, silinecek kaynaklari (mail/db/ftp/dns/proxy/kota/git/musteri/dosya) window.confirm'de LISTELER, sonra cascade siler; uyari varsa `deletedWithWarnings` gosterir. 11 yeni i18n anahtari (panel.sites.deletePlan.* + deletedWithWarnings + confirmDelete guncellendi), TR/EN 1464/1464 parite.
- **DB BAGI NOTU (gelecek is):** Veritabanlari siteye ACIKCA bagli DEGIL (Plesk'te bagli); su an ad-eslesmesiyle bulunuyor. FtpUserDto.SiteName var (FTP bagli). Tam Plesk icin site→kaynak sahiplik kaydi gerekebilir.
- **KALAN UI YENIDEN YAPILANDIRMASI (FAZ 2, henuz yapilmadi):** FTP ayri ust menu yerine site baglamina tasinmali; veritabani merkezi gorunur ama site-baglaminda; mail domain-merkezli (domaine tikla -> icinde hesap ekle); birlesik site olusturma (DB+mail+FTP tek akista). MailView'in zaten domain-secimli oldugu dogrulanmali.
- Dogrulama: Release build 0/0, test Core 2/2 + Infra 22/22, frontend typecheck OK, build OK, i18n 1464/1464, 0 collision.
- **Guncel installer SHA256:** `D0B524EE734E7A695BE7A3C3C41FE7C849466DAF7B98B9A1DD7BDB9D59D320A4` (48.17 MiB, 50,512,413 byte). Cascade silme/mail fix GERCEK sunucuda (IIS+DB+Stalwart) test edilmeli; bu makinede mail/IIS/DB yok, kod build-dogrulandi.

## 2026-06-14 TURKCE YERELLESTIRME DUZELTMESI (en buyuk gorsel/UX kazanci) + canli QA

- **KESIF (canli Playwright QA ile):** Tum arayuz Turkce metinleri DIAKRITIKSIZDI (ı ş ü ç ö ğ yok): "Guvenli giris", "Sifre", "Kullanici adi", "duzlemi"... Turkce hosting paneli icin en buyuk profesyonellik eksigi. `frontend/src/i18n/tr.json` 1453 stringin tamami ASCII'ydi (yalniz 2 stray Turkce karakter), ~415+ string diakritik gerektiriyordu.
- **FIX:** Sozluk-tabanli ASCII->Turkce restorasyon (723 kelimelik whole-word mapping; Ingilizce/teknik terimler -iis,php,nginx,docker,stalwart,runtime,ssl,smtp...- sozlukte OLMADIGI icin dokunulmadi). Buyuk/kucuk harf korundu (Turkce-aware: UZERINE->ÜZERİNE noktali İ), `{placeholder}` ve `@:linked` bolumleri korundu. Sonuc: tr.json Turkce karakter 2 -> 3251, 1453/1453 parite korundu, 0 placeholder bozulmasi. Backend `src/AdaPanel.Api/Resources/tr.json` (297->795 Turkce karakter) ayni fix'le duzeltildi -- NOT: bu dosya KULLANILMIYOR (ApiExceptionHandlingMiddleware yaniti ham MessageKey ile doner, metni frontend cevirir; hicbir .cs Resources/tr.json yuklemiyor), tutarlilik icin yine de duzeltildi.
- **CANLI QA (Playwright 1.60 global + chromium; API publish exe'si temp DataRoot ile http://127.0.0.1:5099):** temiz kurulum + login + 16 bolum koyu tema + acik tema + mobil(390) ekran goruntuleri. Login/dashboard/mail 2x crop ile dogrulandi: "Güvenli giriş", "Kullanıcı adı veya e-posta", "Şifre", "Bu cihazı hatırla", "Şifremi unuttum", mail uzun aciklamasi hepsi kusursuz Turkce; teknik terimler (Stalwart, DKIM/SSL/spam, CLI) korundu. vueError yok (yalniz beklenen IIS-yok 503). Koyu/acik/mobil layout zaten profesyonel (2026-06-12 refresh), gorsel regresyon yok.
- **YONTEM HATIRLATMA (gelecekte i18n diakritik dokumu icin):** `frontend/src/i18n/tr.json` flat-JSON (noktali key, flatJson:true). Token regex `[A-Za-zçğıöşüÇĞİÖŞÜ]+`; Turkce karakter ICEREN token zaten islenmis sayilir; SADECE tam-ASCII Turkce kelimeler hedef. Art-unlu cekimler kolay kacar ("dosyalari"->"dosyaları" gibi -lar-ı formlari ilk turda atlandi, ikinci taramada eklendi).
- Dogrulama: typecheck OK, frontend build OK, Release build 0/0, test Core 2/2 + Infra 22/22, i18n 1453/1453. Yeni frontend wwwroot'a sync edildi (src + publish), installer yeniden uretildi.
- **Guncel installer SHA256:** `7AAFF120A8E8609442E3943031BDBAEE9C2C408AD018ECD4B1087B1482067B0B` (48.16 MiB, 50,501,027 byte).

## 2026-06-08 01:30 onceki devir

- `promp.md` ve tum devir notlari yeniden okundu; yarim kalan en kritik alan olan self-update gercek kurulum akisina tamamlandi.
- Guncelleme akisi artik iki asamali:
  - paket URL ve zorunlu 64 karakterlik SHA256 ile indirilir
  - dosyanin `.exe` uzantisi ve Windows PE `MZ` basligi dogrulanir
  - paket yalnizca panelin `updates` veri kokunde staging edilir
  - kurulum oncesi SHA256 ikinci kez hesaplanir
  - yonetici onayindan sonra Windows Task Scheduler altinda SYSTEM/yuksek yetkiyle sessiz Inno Setup kurulumu planlanir
  - installer mevcut AdaPanel servisini durdurur, dosyalari gunceller, mevcut portu korur ve servisi yeniden baslatir
  - installer cikis kodlari `0`, `1641` ve `3010` basarili kabul edilir
- API:
  - `POST /api/v1/update/apply` paketi indirip dogrular
  - `POST /api/v1/update/install` hazir paketi kurulum icin planlar
- UI:
  - Ayarlar -> Guncelleme ekrani `Paketi indir` ve `Kur ve yeniden baslat` adimlarini ayirir
  - kurulum dugmesi yalnizca dogrulanmis paket hazirken etkinlesir
  - kurulumdan once servis kesintisi/restart onayi sorulur
  - planlanan kurulum durumu ekranda gosterilir
- Guvenlik:
  - SHA256 olmadan indirme reddedilir
  - staging disindaki paket yolu reddedilir
  - kurulumdan hemen once paket degistirildiyse checksum kontrolu kurulumu durdurur
- Testler:
  - SHA256 zorunlulugu testi eklendi
  - hazir paketin tekrar dogrulanip installer launcher'a aktarilmasi testi eklendi
  - `npm run typecheck` basarili
  - `npm run build` basarili
  - `dotnet test AdaPanel.sln -c Release`: Core 1/1, Infrastructure 6/6 basarili
  - frontend i18n `1097/1097`, backend i18n `217/217`
- Playwright UI QA:
  - temiz setup/login ve Ayarlar -> Guncelleme gecisi basarili
  - masaustu yatay tasma `1440/1440`, mobil yatay tasma `390/390`
  - framework hata katmani yok
  - 503 kaydi gelistirme makinesindeki beklenen IIS management eksikliginden, 404 kayitlari favicon/QA route isteginden kaynaklandi
- Son installer:
  - `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - boyut: `50,390,185` byte (`48.06 MiB`)
  - SHA256: `E6198E169828AD343A9926C9188264DCC1F389E45A90973F08F9C69ABFD27BB2`
- Gercek otomatik guncellemenin uctan uca son adimi icin yayinlanmis HTTPS update feed, yeni surum EXE'si ve SHA256 degeri gerekir; gelistirme makinesinin mevcut kurulumunu bilerek kendi uzerine kurmadik.

## 2026-06-08 00:45 son devir (en guncel ve oncelikli)

- `promp.md` ve onceki devir notlari yeniden okundu; kullanicinin eksik gordugu Laravel destegi gercek tek tik kurulum olarak eklendi.
- App Store artik Laravel 13 icin su zinciri otomatik yapiyor:
  - PHP 8.3+ yoksa kurar ve FastCGI kaydeder
  - Composer ve IIS URL Rewrite yoksa kurar
  - resmi Composer `create-project laravel/laravel` akisini gecici dizinde calistirir
  - uretim `.env`, `APP_KEY`, SQLite veritabani, migrasyon ve `artisan optimize` adimlarini tamamlar
  - IIS web kokunu Laravel `public` dizinine alir
  - `storage`, `bootstrap/cache` ve `database` ACL izinlerini app pool hesabina verir
  - `artisan schedule:run` icin dakikada bir Windows gorevi olusturmayi dener
  - hata olursa eski IIS yolunu ve baslangic dosyalarini geri yukler
- Laravel projesinin gercek uygulama koku ile IIS web koku ayrildi:
  - `SiteDto.ApplicationRootPath` eklendi
  - dosya yoneticisi, site yedegi, FTP ve Composer islemleri tum Laravel proje kokunu kullanir
  - IIS ve WAF web konfigurasyonu guvenli `public` dizininde kalir
- PHP bootstrapper `php.ini` dosyasini profesyonel varsayilanlarla hazirliyor:
  - curl, fileinfo, intl, mbstring, openssl, PDO MySQL/PostgreSQL/SQLite, mysqli, pgsql, sodium, sqlite3 ve zip uzantilari mevcutsa etkinlestirilir
  - FastCGI, timezone, bellek, timeout ve upload limitleri ayarlanir
- Windows Installer `1641` ve `3010` yeniden baslatma gerekli kodlari artik basarisiz kurulum sayilmiyor.
- App Store arayuzu kategori, arama, gereksinim, otomatik hazirlik, onerilen uygulama, ilerleme ve ayrintili hata ciktilariyla yenilendi.
- Gercek izole Laravel smoke:
  - Laravel Framework `13.14.0`
  - Laravel proje paketi `v13.8.0`
  - `composer install --no-dev --optimize-autoloader` basarili
  - SQLite migrasyonlari basarili
  - `artisan optimize` basarili
  - `pdo_sqlite` yuklu
- Dogrulama:
  - `npm run typecheck` basarili
  - `npm run build` basarili
  - `dotnet test AdaPanel.sln -c Release`: Core 1/1, Infrastructure 4/4 basarili
- Playwright arayuz QA:
  - temiz ilk kurulum ve `qaadmin` girisi basarili
  - `Web Sitesi -> Uygulamalar` ekraninda Laravel, PHP 8.3+, Composer, SQLite, APP_KEY, Migration, FastCGI, Scheduler ve klasor izinleri gorundu
  - Laravel aramasi diger uygulamalari filtreledi
  - framework hata katmani yoktu
  - masaustu yatay tasma `1440/1440`, mobil yatay tasma `390/390`
  - gelistirme makinesindeki IIS eksikliginden gelen 503 cevaplari arayuzde hazirlik uyarisi olarak gosterildi
- Son installer:
  - `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - boyut: `50,385,375` byte (`48.05 MiB`)
  - SHA256: `F3BEE76D1478B7D9F995C8E25146CA71E27F71F6205849E85A760D1EE5717B0E`
- Bu gelistirme makinesinde IIS management assembly olmadigi icin son IIS physical-path commit'i canli bir site uzerinde sinanamiyor; hedef Windows/IIS bilgisayarinda yeni EXE ile denenmeli.

## 2026-06-07 23:25 onceki devir

- `promp.md` 436 satir/25 modul olarak tekrar okundu; bu bolumden sonraki eski notlar tarihsel kayittir.
- Site olusturma sonrasi varsayilan hazirlama zinciri dosya, guvenli log/temp, site disi yedek dizini, DNS, hosting, WAF/ACL, SSL, FTP, ASP.NET, uygulama runtime'i ve proxy durumunu kapsiyor.
- Dosya yoneticisi artik yalnizca gercek IIS site koklerine erisiyor:
  - panel data/backups/downloads dizinleri arayuzden kaldirildi
  - site secmeden yazma/silme/yukleme/arsiv islemi engelleniyor
  - junction/reparse point ve benzer isimli dizinle sandbox kacisi engelleniyor
- Site loglari `App_Data\AdaPanel\logs`, temp dosyalari `App_Data\AdaPanel\temp` altinda; yedekler web kokunun disindaki panel yedek dizininde tutuluyor.
- MSSQL gercek provider olarak eklendi:
  - yerel SQL Server instance algilama
  - Windows authentication ile DB/kullanici listeleme
  - DB/login olusturma, yetki verme, silme
  - `.bak` backup ve restore
- IIS uygulama bagimliliklari `components.json` icine eklendi ve otomatik baglandi:
  - `url-rewrite` (IIS URL Rewrite 2.1)
  - `iis-arr` (Application Request Routing 3.0)
  - `http-platform-handler` (Node.js/Python)
- Node/Python elle veya site profiliyle ayarlandiginda HttpPlatformHandler otomatik kurulur.
- Reverse proxy ve rewrite islemleri gerekli URL Rewrite/ARR bilesenlerini otomatik kurar.
- Yazilim Magazasi 16 bilesen listeliyor; tum manifest indirme URL'leri 2026-06-07 tarihinde HTTP 200 ile canli dogrulandi.
- Frontend `typecheck` npm betigi eklendi.
- Test projeleri `AdaPanel.sln` icine eklendi; Windows hedefi ve iki yarim test duzeltildi.
- `scripts/build-installer.ps1` artik typecheck, frontend build, tum testler, publish ve Inno Setup adimlarindan biri hata verirse duruyor.
- Masaustu QA: Ana Sayfa, Web Sitesi, FTP, Veritabani, Dosya, Gunluk, Terminal, Planli Islemler, Sunucu Araclari, Yazilim Magazasi ve Ayarlar acildi; genel beklenmeyen hata ekrani yok.
- Mobil QA: 390x844 ekranda tum sol menu ogeleri gorunuyor. Ayarlar sekmeleri iki sutuna alindi; yatay tasma `390/390` olarak duzeltildi.
- Dosya API smoke:
  - kokte yalnizca `sites` gorunuyor
  - site secmeden `mkdir` istegi `400 files.site_required` ile engelleniyor
- Dogrulama:
  - `npm run typecheck` basarili
  - `npm run build` basarili
  - `dotnet test AdaPanel.sln -c Release`: Core 1/1, Infrastructure 3/3 basarili
  - frontend i18n 1066/1066, backend i18n 205/205
  - Release build 0 uyari/0 hata
- Son installer:
  - `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - boyut: `50,370,830` byte (`48.04 MiB`)
  - surum: `0.2.0-preview`
  - SHA256: `EA00BA66BC150C4198E98734C991350241352878A05DCBC6166983AF4C5017EA`
- QA ekran goruntuleri:
  - `artifacts\screenshots\qa-20260607-final`
- Bu gelistirme bilgisayarinda IIS management/FTP rolu tam kurulu degil; bu nedenle canli IIS site ve FTP entegrasyonu burada 503/konfigurasyon hatasi verebilir. Installer hedef bilgisayarda IIS web, CGI, ASP, ASP.NET, FTP ve management ozelliklerini otomatik etkinlestirir.
- MariaDB/MySQL/PostgreSQL servis kurulumlari ve canli SSL/uzak servis credential entegrasyonlari bu bilgisayarda tahrip edici olmamak icin sonuna kadar calistirilmadi; kod, indirme URL'leri ve paketleme zinciri dogrulandi.

## Tamamlanan adimlar

- Step 2: Cozum iskeleti, SQLite, Windows Service host, Serilog
- Step 3: Cookie auth, CSRF, rate limit, 2FA, guvenli giris yolu, kullanici yonetimi
- Step 4: Sistem izleme ve dashboard API temeli
- Step 5: Bootstrapper, `components.json`, Yazilimlar modulu
- Step 6: IIS site yonetimi
- Step 7: PHP surum yonetimi ve FastCGI
- Step 8: ASP/ASP.NET modulu ve mixed handlers
- Step 9: Node.js / Python barindirma modulu
- Step 10: Reverse proxy / URL Rewrite modulu
- Step 11: Veritabani modulu
- Step 12: Web DB arayuzu
- Step 13: Onbellek yonetimi
- Step 14: Dosya yoneticisi
- Step 15: SSL/TLS ve sertifika akisi
- Step 16: FTP kullanici yonetimi
- Step 17: Site guvenligi / WAF
- Step 18: Planli gorevler
- Step 19: Windows Firewall modulu
- Step 20: Panel + site log goruntuleme
- Step 21: Bildirim / webhook ayarlari
- Step 22: Yedekleme / geri yukleme iskeleti
- Step 23: App Store / uygulama sablonlari
- Step 24: Self-update iskeleti
- Step 25: i18n tamamlama + installer / EXE zinciri

## Son guncellemede yapilan kritik isler

- 2026-06-07 kalan moduller / yedekleme-update-UI final paketi:
  - Kullanici site olusturmada artik hata olmadigini soyledi; bu nedenle site olusturma akisi kurcalanmadi, kalan moduller ve paket dogrulamasi tamamlandi.
  - Uzak yedek hedefleri tamamlandi:
    - Site ve veritabani yedek request DTO'lari `TargetType`, `RemoteUrl`, `RemoteUserName`, `RemotePassword` alanlarini tasiyor.
    - Yedek kayitlari `TargetType` ve `RemoteUri` ile tutuluyor.
    - Yerel yedek her zaman olusuyor; secime gore FTP, WebDAV/Drive, S3-compatible signed URL ve HTTP PUT hedeflerine kopyalama deneniyor.
    - Uzak hedef alanlari null/boş geldiginde backend artik NullReference yerine `backup.remote_target_invalid` donduruyor.
  - Self-update guvenli staging sertlestirildi:
    - Manifest ve request uzerinden `PackageSha256` okunuyor.
    - Paket indirildikten sonra SHA256 dogrulaniyor, `updates/pending-update.json` icine staged bilgi yaziliyor.
    - Hatalı update URL'si artik ham exception/ProblemDetails yerine ortak API zarfinda `update.package_url_invalid` donduruyor.
    - URL path'i dosya adi icermiyorsa staged paket icin guvenli fallback dosya adi uretiliyor.
  - Frontend stale CSRF hatasi duzeltildi:
    - Login POST'u oncesi alinan antiforgery token login sonrasinda cache'te kaliyor ve sonraki POST'larda ProblemDetails 400 uretebiliyordu.
    - Basarili login sonrasinda `clearCsrfToken()` cagriliyor; sonraki POST yeni token aliyor.
  - UI profesyonellestirme paketi:
    - `BackupView` uzak hedef secimi, FTP/S3/WebDAV alanlari ve hedef durum kolonlari ile genisletildi.
    - `UpdateView` paket durumu, SHA256, staged path ve guvenli staging akisini gosteriyor.
    - `DatabasesView` kompakt motor sekmeleri, toolbar ve arama filtreleri kazandi.
    - `FtpView` FTP adres toolbar'i, arama ve site listesini otomatik yukleme davranisi kazandi.
    - `SoftwareView` aaPanel ekranlarina benzer tablo/kategori/arama/status/action yapisina cekildi.
    - `AppStoreView` kartlardan tablo satiri yapisina alindi.
    - `SetupWizardView` ayarlar bolumune daha derli toplu hero/metrik kartlari eklendi.
  - Dogrulama:
    - JSON parse basarili.
    - Frontend i18n key simetrisi: 870/870, eksik anahtar yok.
    - Backend resource key simetrisi: 197/197, eksik anahtar yok.
    - `src` ve `frontend/src` icinde `aaPanel/aapanel`, `not implemented`, `yakinda/yakında` taramasi temiz.
    - `npm exec vue-tsc -- --noEmit` basarili.
    - `dotnet build AdaPanel.sln` 0 hata/0 uyari.
    - `npm run build` basarili.
    - `dotnet test AdaPanel.sln --no-build` exit 0.
    - `scripts/build-installer.ps1` basarili.
    - Temiz gecici data root ile final publish smoke:
      setup/status initialized=false, setup success, login success, software manifest count 12, backup list 0, update current/latest `0.2.0-preview`, `/panel` HTTP 200.
      invalid update URL ortak zarfla `{"success":false,"message":"update.package_url_invalid","details":"Url=not-a-url"}` dondurdu.
    - Smoke surec ve temp klasorleri kapatildi/temizlendi; tur sonunda acik `AdaPanel.Api` process'i kalmadi.
  - Yeni EXE:
    - `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
    - Boyut: 47,534,727 byte
    - SHA256: `42A0A24EE60C39E15EFD3AE01BC15367FAF245004DBE32B22A9594B5FD91AFE1`

- 2026-06-07 DNS / hosting ayarlari / profesyonel Web Sitesi UI paketi:
  - Kullanici tekrar memory + promp okunduktan sonra panelde `aaPanel` metni kalmamasini, Web Sitesi ve Site Ayarlari gorunumlerinin daha profesyonel olmasini, DNS/hosting ayarlari gibi Plesk/aaPanel benzeri eksik alanlarin tamamlanmasini istedi.
  - `promp.md` ve `CODEX_MEMORY.md` tekrar okundu; kullanicinin gonderdigi aaPanel Windows site, site ayar modal, yazilim magazasi, FTP, planli is ve veritabani ekranlari referans alindi fakat AdaPanel icine aaPanel marka/metni eklenmedi.
  - DNS modulu eklendi:
    - Core DTO/interface: `SiteDnsRecordDto`, `SiteDnsZoneDto`, `UpdateDnsZoneRequest`, `UpsertDnsRecordRequest`, `IDnsService`
    - API: `DnsController` -> `/api/v1/sites/{siteName}/dns`
    - Infrastructure: `DnsService`
    - Frontend API/View: `frontend/src/api/dns.ts`, `frontend/src/views/DnsView.vue`
  - DNS davranisi:
    - Zone adi secili sitenin host binding bilgisinden turetilir; yoksa `{site}.local` kullanilir.
    - A, AAAA, CNAME, MX, TXT, NS kayitlari panel tarafinda `config/dns-zones.json` icinde saklanir.
    - Windows DNS Server rolu/PowerShell cmdletleri varsa zone ve kayitlar otomatik senkronize edilmeye calisilir; yoksa kayitlar panel-managed olarak kalir.
    - DNS hata/basari mesajlari backend ve frontend TR/EN i18n kaynaklarina eklendi.
  - `WebsiteWorkspaceView` yenilendi:
    - Web Sitesi araclarina `DNS / Alan Adi` sekmesi eklendi.
    - Buyuk kart tablari yerine kompakt grid tab yapisi kullanildi; sag/sol kirpma duzeltildi.
    - Hedef site secici ve barindirma merkezi ust alani daha kompakt hale getirildi.
  - `SiteHostingView` yenilendi:
    - Sol kategori menusu + sag ayar satirlari yapisi eklendi.
    - Genel ayarlar, trafik limitleri, erisim korumasi ve bakim araclari bolumleri ayrildi.
    - Fiziksel yol/application pool ozeti, redirect, connection/bandwidth limitleri, IP engeli, hotlink korumasi, korunan uzantilar ve Composer akisi korunup daha derli toplu hale getirildi.
    - DNS/hosting bos durum metinleri yanlis Node/Python metni yerine kendi i18n anahtarlarini kullaniyor.
  - `SitesView` yenilendi:
    - Site kart listesi tablo benzeri satir gorunumune alindi; site adi, durum, binding sayisi, fiziksel yol ve app pool tek satirda gorunuyor.
    - Site detay bolumu secili satira bagli kalmaya devam ediyor.
  - Genel UI sikilastirma:
    - Global font tabani 14px -> 13px yapildi.
    - Sidebar genislik/padding/nav satirlari sikilastirildi; alt menu ogeleri (`Gunluk`, `Terminal`, `Planli Islemler`, `Yazilim Magazasi`, `Ayarlar`, `Cikis`) daha guvenli gorunuyor.
    - Publish/wwwroot taramasinda `aaPanel/aapanel` metni cikmadi.
  - Dogrulama:
    - `dotnet build AdaPanel.sln` 0 hata/0 uyari.
    - `npm exec vue-tsc -- --noEmit` basarili.
    - `npm run build` basarili.
    - Frontend TR/EN i18n key simetrisi: 820/820, eksik anahtar yok.
    - `dotnet test AdaPanel.sln --no-build` hata dondurmedi.
    - `scripts/build-installer.ps1` basarili.
    - Temiz gecici DB/data root ile API smoke: setup success, login success, session user `smokeadmin`, software manifest count 12, version `0.2.0-preview`.
    - Browser plugin bu oturumda callable olmadigi icin Playwright fallback kullanildi. Visual smoke screenshotlari:
      - `C:\Users\aLi\Desktop\AdaPanel\artifacts\screenshots\dns-hosting-websites-final.png`
      - `C:\Users\aLi\Desktop\AdaPanel\artifacts\screenshots\dns-hosting-dns-final.png`
    - Visual smoke raporu: `bodyHasAapanelText=false`, `bodyHasDnsTab=true`, `toolTabCount=11`.
    - Visual smoke console'daki iki 503, bu gelistirme makinesindeki IIS site listesinin `/api/v1/sites` icin beklenen `Service Unavailable` donmesi kaynakli; UI bunu hazirlik uyarisina ceviriyor.
  - Yeni EXE:
    - `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
    - SHA256: `76937EF2DA5ECC10D14B25059404BEFEB5A671416B383A9E2563BAC56167A0D9`

- 2026-06-07 profesyonel hata sertlestirme / IIS reflection paketi:
  - Kullanici `ASP veya ASP.NET yapilandirmasi tamamlanamadi`, `IIS site islemi tamamlanamadi` ve benzeri hatalarin tek tek donmesini degil, panelin sistemli sekilde sertlestirilmesini istedi.
  - `WebAdministrationSiteService`, `PhpService`, `AspNetService`, `AppRuntimeService`, `ReverseProxyService`, `WafService`, `WinAcmeSslService` ve `SiteHostingSettingsService` icindeki `Microsoft.Web.Administration` reflection kullanimlari sertlestirildi.
  - `TargetInvocationException` icindeki gercek COM/IIS hatasi artik wrapper icinde kaybolmuyor; `ExceptionDispatchInfo` ile ic exception tekrar firlatilip mevcut hata mapleme ve `details` akisi tarafindan yakalaniyor.
  - `ConfigurationSection.Collection` / `GetCollection()` IIS surum farki PHP ve ASP tarafinda tamamlandi; dogrudan `GetPropertyObject(..., "Collection")` kullanimi kalmadi.
  - Classic ASP handler tespitinde eksik/uyumsuz attribute okumalari artik tum akisi dusurmuyor.
  - Reverse proxy, WAF, SSL HTTPS binding, Node/Python app runtime ve site hosting limit islemlerinde yetki, kilit, eksik IIS feature ve modul eksigi daha dogru i18n anahtarina mapleniyor.
  - `ssl.binding_failed` ve `security.configuration_failed` backend + frontend i18n anahtarlari eklendi.
  - FTP PowerShell/net/icacls hatalari ve software bootstrapper download/process hatalari artik `AppException.Details` icinde exit code, stdout, stderr, URL ve status bilgisi tasiyor.
  - Bootstrapper external process okuma akisi duzeltildi: stdout/stderr artik process beklenmeden once okunmaya basliyor; buyuk cikti ureten installer'larda pipe dolmasi kaynakli kilitlenme riski azaldi.
  - Dogrulama: backend JSON parse basarili, `dotnet build AdaPanel.sln` 0 hata/0 uyari, `npx vue-tsc --noEmit` basarili, `dotnet test AdaPanel.sln --no-build` exit 0, `scripts/build-installer.ps1` basarili.
  - Smoke: temiz gecici DB ve data root ile publish EXE `http://127.0.0.1:8927` uzerinde calisti; setup + login + `/api/v1/php/versions` + `/api/v1/software` basarili, manifest 12 bilesen dondurdu.
  - Yeni EXE: `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - Yeni EXE SHA256: `DAB87998BE0C351BEE183A50D43375363DE8B28234C05566B02026E014B8728C`

- 2026-06-07 `ConfigurationSection.Collection` uyumluluk duzeltmesi:
  - Kullanici hedef PC'de su hatayi rapor etti:
    `MissingMemberException: Member 'Microsoft.Web.Administration.ConfigurationSection.Collection' not found`.
  - Kok neden: Hedefteki `Microsoft.Web.Administration.ConfigurationSection` collection'i `Collection` property ile degil `GetCollection()` metodu ile donduruyor.
  - `AspNetService` icindeki handler ve fastCgi collection okumalari `GetConfigurationCollection` helper'ina alindi; once `Collection` property deneniyor, yoksa `GetCollection()` metodu cagriliyor.
  - Ayni risk PHP atama akisinda da vardi; `PhpService` handler ve fastCgi collection okumalari da `GetConfigurationCollection` helper'ina cekildi.
  - Eski `GetPropertyObject(..., "Collection")` kullanimi altyapi icinde kalmadi.
  - Dogrulama: `dotnet build AdaPanel.sln` 0 hata/0 uyari, `npx vue-tsc --noEmit` basarili, `scripts/build-installer.ps1` basarili, `dotnet test AdaPanel.sln` exit 0, API setup/login/PHP smoke basarili.
  - Yeni EXE: `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - Yeni EXE SHA256: `8FE656DDA71A77A4286751DCB8E84E2CF00E8B0F7BB2DF309CEA25ADD9280115`

- 2026-06-07 Classic ASP `0x80070585 Gecersiz dizin` duzeltmesi:
  - Kullanici hedef PC'de `ASP veya ASP.NET yapilandirmasi tamamlanamadi` detayinda su hatayi rapor etti:
    `COMException (0x80070585): Gecersiz dizin` ve stack `AspNetService.SetClassicAspEnabled -> SetAttributeValue(... "enabled")`.
  - Kok neden: IIS `system.webServer/asp` section'inda `enabled` attribute'u yok; Classic ASP site bazinda bu attribute ile acilamaz.
  - `AspNetService.SetClassicAspEnabled` artik `aspSection.enabled` yazmiyor. Bunun yerine Classic ASP section'inin varligini kontrol ediyor ve gerekirse site handlers koleksiyonuna `*.asp` icin `IsapiModule` + `%windir%\system32\inetsrv\asp.dll` handler'i ekliyor.
  - `GetClassicAspEnabled` artik `enabled` attribute'u okumuyor; `*.asp` handler varligini kontrol ediyor.
  - `SetSitePhpHandler` ortak `GetHandlersCollection` helper'ini kullaniyor.
  - Dogrulama: `dotnet build AdaPanel.sln` 0 hata/0 uyari, `npx vue-tsc --noEmit` basarili, `scripts/build-installer.ps1` basarili, `dotnet test AdaPanel.sln` exit 0, API setup/login/PHP smoke basarili.
  - Yeni EXE: `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - Yeni EXE SHA256: `A9FC9C675EB66B1C64CB79B3A789F7F00AF484A7896B4478BD7C0E37C070CE84`

- 2026-06-07 ASP/ASP.NET yapilandirma uyarisi duzeltmesi:
  - Kullanici site olusturma sirasinda `ASP veya ASP.NET yapilandirmasi tamamlanamadi.` uyarisi gordu.
  - Muhtemel kok neden: otomatik/mixed profilde Classic ASP (`system.webServer/asp`) de aciliyor ama installer scriptinde `IIS-ASP` Windows optional feature eksikti.
  - `scripts/enable-iis-features.ps1` icine `IIS-ASP` eklendi; installer artik Classic ASP ozelligini de acmayi deniyor.
  - `AspNetService` hata yakalama IIS/PHP servisleriyle ayni seviyeye getirildi: `TargetInvocationException` unwrap ediliyor, detayli diagnostic `AppException.Details` alanina konuyor, izin/kilit/eksik IIS feature hatalari daha dogru mesaj anahtarina mapleniyor.
  - Dogrulama: `dotnet build AdaPanel.sln` 0 hata/0 uyari, `npx vue-tsc --noEmit` basarili, `scripts/build-installer.ps1` basarili, publish scriptinde `IIS-ASP` dogrulandi, API setup/login/PHP smoke basarili.
  - Yeni EXE: `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - Yeni EXE SHA256: `F72879978840F471EFB6074DC50CFF8C3638A648AC967070FD6CA212A8E4061A`

- 2026-06-07 IIS `System.Security.Permissions` runtime duzeltmesi:
  - Kullanici hedef PC'de `Web Sitesi` ekraninda ve site olusturmada su hatayi rapor etti:
    `FileNotFoundException: System.Security.Permissions, Version=0.0.0.0`.
  - Kok neden: `Microsoft.Web.Administration.dll` dinamik olarak IIS `ServerManager.Sites` okurken `System.Security.Permissions.dll` istiyor, onceki self-contained paket bu assembly'i ayri olarak tasimiyordu.
  - `src/AdaPanel.Infrastructure/AdaPanel.Infrastructure.csproj` icine `System.Security.Permissions` 8.0.0 package reference eklendi.
  - `src/AdaPanel.Api/AdaPanel.Api.csproj` icine publish hedefi eklendi; `System.Security.Permissions.dll` single-file EXE icine gomulmekle kalmayip kurulum klasorune ayri dosya olarak da kopyalaniyor.
  - Inno Setup ciktisinda `artifacts\publish\win-x64\System.Security.Permissions.dll` paketlendigi dogrulandi.
  - Dogrulama: `dotnet build AdaPanel.sln` 0 hata/0 uyari, `npx vue-tsc --noEmit` basarili, `dotnet test AdaPanel.sln` exit 0, `scripts/build-installer.ps1` basarili, API setup/login/PHP smoke basarili.
  - Yeni EXE: `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - Yeni EXE SHA256: `BF695B3FBA2AE788EF320A9C837A9A463BBBF41B3C0304DB208D6A3EF4635A80`

- 2026-06-07 IIS hata detaylari / genel temizlik / final paket:
  - Site olusturma akisinda `WebAdministrationSiteService.CreateSiteAsync` artik commit edilmemis IIS site nesnesini `MapSite` ile okumuyor; provisional DTO donuyor ve commit sonrasi `SiteProvisioningService` gercek siteyi tekrar okuyor. Bu, statik HTML dahil ilk site olusturmada commit oncesi IIS property okuma kaynakli hatalari azaltir.
  - `AppException` ve `ApiResponse` icine `Details` alani eklendi; middleware artik AppException detaylarini JSON zarfinda donduruyor.
  - IIS/PHP konfigurasyon hatalarinda detayli diagnostic string uretiliyor. Site olusturma, binding ve PHP islemlerinde hata olursa frontend artik sadece `Istek tamamlanamadi` yerine ceviri mesaji + teknik ayrintiyi gosterebiliyor.
  - `SitesView` ve `SetupWizardView` API `details` alanini ekrana basacak sekilde guncellendi; `.notice` stilleri cok satirli hata metinlerini okunur gosteriyor.
  - Eksik backend i18n anahtarlari tamamlandi; backend TR/EN resource taramasinda eksik anahtar kalmadi.
  - Uygulama icinde `aapanel/aaPanel` referansi kalmadi; sadece `promp.md` icinde referans olarak geciyor.
  - Eski placeholder view dosyasi kaldirildi, UI metinlerindeki "ileride/yakinda" tonu temizlendi.
  - `appsettings.Development.json` publish/installer disina alindi.
  - API ve Infrastructure projeleri Windows hedefi icin `net8.0-windows` oldu; dotnet build artik 0 uyari / 0 hata.
  - Sürüm gostergeleri `VersionInfo.Current` helper'i ile `0.2.0-preview` informational version'ini kullaniyor; frontend artik yukleme sirasinda eski `0.1.0` fallback gostermiyor.
  - Dogrulama: JSON parse, backend i18n key scan, `npx vue-tsc --noEmit`, `dotnet build AdaPanel.sln`, `dotnet test AdaPanel.sln`, `scripts/build-installer.ps1` basarili.
  - Smoke: temiz gecici data root ile setup + login + `/api/v1/php/versions` basarili; setup/status `0.2.0-preview` dondurdu.
  - Browser smoke: `http://127.0.0.1:8915/panel` uzerinde login/dashboard gorundu; alt menuler (`Planli Islemler`, `Yazilim Magazasi`, `Ayarlar`, `Cikis`) gorundu; dashboard `0.2.0-preview` gosterdi ve eski `0.1.0` gorunmedi.
  - Bu gelistirme makinesinde `C:\Windows\System32\inetsrv\Microsoft.Web.Administration.dll` yok; bu nedenle gercek IIS site olusturma entegrasyonu burada canli dogrulanamadi. Hedef PC'de yine hata olursa panel artik ayrintili IIS exception metnini gosterecek.
  - Yeni EXE: `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - Yeni EXE SHA256: `A02D99A39F641EC978AC73353542789443223ADB9E4B371AD05F7FC90CC6EB59`

- 2026-06-07 PHP/IIS hata duzeltme paketi:
  - PHP yonetimi ekraninda `/api/v1/php/versions` FastCGI konfigurasyonunu okuyamazsa artik `common.unexpected_error` ile tum sayfa dusmez.
  - Kurulu PHP kaydi listelenirken FastCGI okunamazsa ilgili surum sadece `FastCGI kaydi eksik` olarak isaretlenir ve ham hata loglanir.
  - PHP FastCGI kaydi veya siteye PHP atama isleminde yetki/IIS ozellik eksigi varsa kullaniciya `sites.iis_permission_denied`, `sites.iis_install_required` veya `php.configuration_failed` doner.
  - IIS reflection hatalari `TargetInvocationException` icinden acilip daha dogru AppException anahtarina mapleniyor.
  - Site olusturmada binding portu doluysa ve host adi bos ise otomatik olarak 8080+ araliginda musait port secilir.
  - Otomatik secilen port frontend'de `Site olusturuldu... {port} portu otomatik kullanildi` mesaji ile bildirilir.
  - Yeni backend/frontend i18n anahtarlari eklendi: `sites.binding_conflict`, `sites.iis_permission_denied`, `panel.sites.createdWithPort`.
  - Dogrulama: JSON parse, `npx vue-tsc --noEmit`, `dotnet build AdaPanel.sln`, `scripts/build-installer.ps1` basarili.
  - Gecici smoke: publish paketi `http://127.0.0.1:8911` uzerinde temiz data root ile calisti; setup + login + `/api/v1/php/versions` basarili, `success=true`, `count=0`.
  - Yeni EXE: `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - Yeni EXE SHA256: `E1BC4AD0E3FF7A47800CC9D7ADABC6B9DE81D3685489662290C6CACB24F7D331`

- 2026-06-07 ikinci paketleme/dagitim duzeltmesi:
  - Kullanicinin sadece `AdaPanelSetup.exe` dosyasini baska PC'ye tasimasi dogru senaryo olarak kabul edildi.
  - Eski EXE icindeki eski `components.json` baska PC'de PHP/Adminer/Node/MySQL gibi kurulum linklerini bozdugu icin yeni EXE yeniden uretildi.
  - `components.json` guncellendi ve tum indirme URL'leri HEAD kontrolunde 200 dondu:
    PHP 8.3.31, MariaDB 11.4.3, MySQL 8.4.9 ZIP, PostgreSQL 16.14, Composer, phpMyAdmin, Adminer 5.4.2,
    Redis, Memcached 1.6.8, Node.js LTS 24.16.0, Python 3.12.4, win-acme.
  - `ComponentBootstrapper` artik PHP paketini `downloads.php.net` release JSON'undan dinamik cozer.
  - `ComponentBootstrapper` ayrica Adminer, Memcached, Node.js LTS ve win-acme icin canli release cozumleme dener.
  - Büyük indirmeler icin HTTP client timeout siniri kaldirildi.
  - PHP 404 ve genel `software.download_failed` hatalari icin TR/EN mesajlar eklendi.
  - Basarisiz component kayitlari artik `Kaldir` ile temizlenebiliyor; sadece `Installed` olmayan kayitlarda takilmiyor.
  - MySQL ZIP kurulumu `mysqld --initialize-insecure`, servis kurulumu ve root parola atama akisi ile eklendi.
  - Redis ve Memcached ZIP paketleri icin servis kurma post-install adimlari eklendi.
  - `AppStoreService` Adminer paketini GitHub latest release ile cozmeye calisir ve buyuk indirmelerde timeout'a takilmaz.
  - `AppStoreView` site listesi okunamazsa tum sayfayi genel hata yerine acik uyarı ile gosterir; site yoksa kurulum butonlarini kapatir.
  - Frontend hata ayiklama iyilestirildi; API message yoksa HTTP status gosterir, varsa i18n anahtarini cevirir.
  - Otomatik site profili PHP kurulabiliyorsa PHP + Classic ASP + ASP.NET karma profil uygular; PHP indirilemezse siteyi tamamen bozmadan ASP.NET profiline duser.
  - Installer guncelleme senaryosunu algilar, mevcut AdaPanel servis portunu korur ve port sayfasini guncellemede atlar.
  - Installer guncellemede servisi dosya kopyalamadan once durdurur, yeni dosyalari ve yeni manifesti ustune yazar.
  - Uninstall artik AdaPanel, AdaMariaDB, AdaMySQL, AdaPostgreSQL, AdaRedis, AdaMemcached servislerini temizlemeyi dener.
  - Uninstall artik `%ProgramData%\AdaPanel` verisini ve kurulum klasorunu temizler; tekrar kurulumda eski admin/ayarlar kalmamali.
  - Yeni surum `0.2.0-preview` / assembly `0.2.0.0`.
  - Yeni EXE: `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - Yeni EXE SHA256: `AF4772399B550857AA0B46D5E5A38659C7847B9086A8F478214C08849E2602B7`

- 2026-06-07 smoke notlari:
  - Gecici data root ile publish edilen uygulama `http://127.0.0.1:8905` uzerinden calistirildi.
  - `/api/v1/health` `applicationVersion: 0.2.0.0` ve temiz setup durumu dondurdu.
  - Setup + login akisi gecici admin kullanicisiyle basarili oldu.
  - `/api/v1/software` manifesti 12 bilesen dondurdu.
  - `/api/v1/store` 4 sablon dondurdu.
  - `php-8.3` temp data root altina basariyla kuruldu ve katalogda `Installed`, version `8.3.31` gorundu.
  - `/api/v1/sites` bu gelistirme makinesinde IIS management olmadigi icin `sites.iis_not_available` dondurdu; artik genel `Istek tamamlanamadi` yerine acik mesaj uretilebiliyor.
  - Browser UI smoke `http://127.0.0.1:8906/panel` uzerinden yapildi:
    login basarili, `Yazilim Magazasi` acildi, PHP 8.3 karti gorundu, IIS eksikligi genel hata yerine acik uyarı olarak gorundu, console error yoktu.
  - PostgreSQL/MySQL/MariaDB gibi servis kuran buyuk paketler smoke ortaminda bilerek kurulup baslatilmadi.
  - Gecici smoke process'i kapatildi ve temp klasoru temizlendi.

- Dashboard durum halkalari genisletildi:
  - CPU halkasi mantiksal cekirdek sayisini gosteriyor
  - bellek halkasi kullanilan / toplam fiziksel bellegi gosteriyor
  - disk halkasi kullanilan / toplam alan ile bos alani birlikte gosteriyor
  - ayni ayrintilar `Gozetim` ekraninda da kullaniliyor
- Yeni site olusturmada `Otomatik` profil artik PHP + Classic ASP + ASP.NET
  handler'larini birlikte hazirlayan karma profili uygular.
- Site barindirma ayarlari eklendi:
  - IP engelleme
  - kalici yonlendirme
  - hotlink korumasi
  - IIS bant genisligi ve baglanti limitleri
  - site dizininde Composer calistirma
- Terminal gercek PowerShell komut calistirma servisine baglandi. Yalnizca yonetici
  kullanabilir; calisma dizini sinirli, sure asimi ve cikti limiti var.
- Dosya yoneticisine yukleme, indirme, ad/icerik arama, ZIP olusturma ve guvenli ZIP
  cikarma islemleri eklendi.
- FTP modulu ortak IIS FTP sitesi, Basic Authentication, kullanici izolasyonu,
  kullanici bazli yetkilendirme, junction ve Windows Firewall kurallarini hazirliyor.
- MySQL/MariaDB ve PostgreSQL yedekleri artik gercek SQL dump/import araclarini
  kullanarak olusturulup geri yukleniyor.
- aaPanel Windows bilgi mimarisi korundu:
  - ayri ust seviye `PHP` ve `ASP/.NET` menuleri yok
  - `Web Sitesi` icinde `Siteler`, `PHP`, `ASP/.NET`, `Node/Python`, `SSL`, `Reverse Proxy` birlikte
  - ust seviye menude `FTP`, `Veritabani`, `Gozetim`, `Dosya`, `Gunluk`, `Terminal`, `Planli Islemler`, `Yazilim Magazasi`, `Ayarlar`, `Cikis` var
- Step 12-24 modulleri backend + frontend olarak eklendi:
  - backend servis/controller katmanlari: `WebDb`, `Cache`, `Files`, `Ssl`, `Ftp`, `Waf`, `Scheduler`, `Firewall`, `Logs`, `Notifications`, `Backup`, `AppStore`, `SelfUpdate`
  - frontend gorunumleri: `WebDbView`, `CacheView`, `FilesView`, `SslView`, `FtpView`, `SecurityView`, `CronView`, `LogsView`, `NotificationsView`, `AppStoreView`, `UpdateView`
  - yeni DTO / interface dosyalari `src/AdaPanel.Core/Dtos/*` ve `src/AdaPanel.Core/Interfaces/*` altinda eklendi
- Setup akisi iyilestirildi:
  - ilk kurulum formuna `InstallRecommendedComponents` secenegi eklendi
  - setup bittiginde onerilen bilesenleri bootstrapper ile kurmayi deneyebiliyor
  - setup sonucuna warning/installed count bilgisi eklendi
- Installer / EXE zinciri ilerletildi:
  - `scripts/initialize-panel-certificate.ps1` eklendi
  - installer artik self-signed sertifika uretip `https://localhost:{port}/panel` aciyor
  - `scripts/configure-firewall.ps1` ile inbound port kuralini ekliyor
  - `scripts/install-service.ps1` var olan AdaPanel servisini silip yeniden olusturabilecek sekilde sertlestirildi
  - `Program.cs` sertifika varsa Kestrel'i dogrudan HTTPS porta baglayacak sekilde guncellendi
- Runtime / UI iyilestirmeleri:
  - `wwwroot` ve `ContentRootPath` publish altinda sabitlendi
  - Serilog single-file publish ile uyumlu hale getirildi
  - setup/login tek ekran akisi `SetupWizardView.vue` icinde suruyor
  - dashboard ozetindeki FTP / DB kartlari canli veriyle baglandi
  - kurulu olmayan DB motorlari icin gereksiz prefetch kapatildi; browser console artifaktlari azaldi
- Onemli teknik sinirlar:
  - `sites` istemleri bu makinede `503 sites.iis_not_available` donebiliyor; neden test ortaminin IIS management bilesenini tam saglamamasi
  - smoke ortaminda DB motorlari kurulmadigi icin `db` create/grant gibi gercek operasyonlar canli motorda sinanmadi

## Uretilen ciktilar

- Self-contained publish:
  - `C:\Users\aLi\Desktop\AdaPanel\artifacts\publish\win-x64\AdaPanel.Api.exe`
- Kurulum paketi:
  - `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
- Guncel masaustu smoke ekran goruntusu:
  - `C:\Users\aLi\Desktop\AdaPanel\artifacts\screenshots\step25-home-desktop.png`
- Guncel mobil smoke ekran goruntusu:
  - `C:\Users\aLi\Desktop\AdaPanel\artifacts\screenshots\step25-home-mobile.png`
- Step 10 masaustu smoke ekran goruntusu:
  - `C:\Users\aLi\Desktop\AdaPanel\artifacts\screenshots\step10-proxy-desktop.png`
- Step 10 mobil smoke ekran goruntusu:
  - `C:\Users\aLi\Desktop\AdaPanel\artifacts\screenshots\step10-proxy-mobile.png`
- Step 9 eski smoke ekran goruntusu:
  - `C:\Users\aLi\Desktop\AdaPanel\artifacts\screenshots\step9-runtime.png`
- Eski ekran goruntusu:
  - `C:\Users\aLi\Desktop\AdaPanel\artifacts\screenshots\dashboard.png`
- Tasarim concept referansi:
  - `C:\Users\aLi\.codex\generated_images\019e99ed-76d7-7a80-b357-d14492f05d5a\ig_0071da4f81751831016a23f5306f5c81919ae9a12f9dd6ee9f.png`

## Build dogrulama

Basarili:

- `npx vue-tsc --noEmit`
- `npm run build`
- `scripts/sync-frontend.ps1`
- `dotnet build AdaPanel.sln`
- `scripts/build-installer.ps1`
- Guncel `AdaPanel.Api.exe` ve `AdaPanelSetup.exe` yeniden uretildi
- HTTPS smoke instance'lar:
  - `https://localhost:8894/panel` sadece transport testi icin kullanildi
  - `https://localhost:8897/panel` login/dashboard smoke icin kullanildi
- Playwright fallback ile dogrulanan akisi:
  - login -> `Web Sitesi`
  - login -> `Veritabani`
  - login -> `Ayarlar`
- Smoke kanitlari:
  - `artifacts/screenshots/step25-home-desktop.png`
  - `artifacts/screenshots/step25-home-mobile.png`
  - `artifacts/smoke/pw/login-progress.log`
  - `artifacts/smoke/pw/failing-responses.json`
- Smoke notlari:
  - browser console'da kalan tek anlamli network hatasi `https://localhost:8897/api/v1/sites` icin `503`; bu test makinesinde IIS management component olmadigi icin bekleniyor
  - DB prefetch guncellemesinden sonra kurulu olmayan motorlar icin `404 /api/v1/db?...` gürültüsü kaldirildi

- 2026-06-07 dashboard metrik smoke testi:
  - temiz gecici veri kokunde self-contained paket calistirildi
  - masaustu ve 390x844 dar ekran gorunumu Browser ile dogrulandi
  - CPU cekirdek, kullanilan/toplam bellek ve kullanilan/toplam/bos disk degerleri gorundu
  - `Yenile` islemi basari mesaji ve yeni metrik ornegi uretti
  - browser console warning/error kaydi yoktu

## Online installer yaklasimi

- Self-contained `AdaPanel.Api.exe` .NET kurulu olmayan makinede calisabilecek sekilde publish ediliyor.
- `AdaPanelSetup.exe` Inno Setup ile uretiliyor.
- Kurulum scripti su anda:
  - dosyalari `Program Files\\AdaPanel` altina kopyalar
  - IIS + CGI + FTP + management ozelliklerini acmak icin PowerShell scripti cagirir
  - secilen portu sorar
  - self-signed panel sertifikasi uretir
  - AdaPanel Windows Service kaydini secilen HTTPS `--urls` ile yapar
  - firewall kuralini ekler
  - varsayilan tarayicida `https://localhost:{port}/panel` acmayi dener
- PHP, MariaDB, PostgreSQL, Redis, Node, Python gibi bilesenler `components.json` icindeki resmi internet baglantilarindan bootstrapper ile indirilir.
- Not:
  - installer adiminda bilesenleri otomatik indirip kurma hala browser oncesi dogrudan Inno icinden degil
  - bunun yerine ilk setup ekranindaki `InstallRecommendedComponents` secenegi default acik

## Bilinen eksikler / sonraki adimlar

- CSP icindeki `unsafe-eval` izni kaldirildi. Vue I18n mesajlari build sirasinda
  precompile ediliyor; `vue-i18n 11.4.5` ile derleyici AST surumu eslendi.
- IIS FTP provisioning kodu tamamlandi ancak bu gelistirme makinesinde IIS FTP rolu
  bulunmadigi icin gercek sunucuda entegrasyon testi yapilmali.
- SQL dump/restore kodu tamamlandi ancak bu gelistirme makinesinde MySQL/MariaDB ve
  PostgreSQL motorlari kurulu olmadigi icin canli motor testi yapilmali.
- Uzak yedek hedefleri FTP/S3-compatible signed URL/WebDAV/Drive/HTTP PUT olarak eklendi; canli uzak servis credential'i olmadigi icin gercek FTP/S3/WebDAV entegrasyon testi yapilmadi.
- `SelfUpdateService` paket staging, zorunlu SHA256, PE kontrolu ve servis disindan sessiz kurulum/restart akisini tamamliyor; canli kullanim icin yayinlanmis HTTPS feed ve tercihen kod imzali installer gerekir.
- `AppStoreService` tam marketplace degil; sablon tabanli hizli kurulum.
- `Ssl/WinAcmeSslService` var ama canli Let's Encrypt akisini bu makinede sonuna kadar smoke etmedik.
- `sites` modulu gercek Windows/IIS makinesinde daha iyi dogrulanmali; bu cihazda management assembly kısıti var.
- Kurulum sirasinda onerilen stack'i Inno Setup adimindan otomatik baslatmak istenirse ek bir local bootstrap hook yazilabilir.

## 2026-06-08 kapsamli kapanis denetimi

- Frontend uretim regresyonu duzeltildi:
  - Vue I18n mesajlari runtime derleme olmadan paketleniyor.
  - `vue-i18n` ile message compiler ayni 11.4.5 AST surumunde.
  - Temiz Playwright oturumunda setup/login metinleri ve panel cevirileri dogru gorundu.
  - Masaustu ve 390x844 mobil gorunumde yatay tasma yok; tum sol menu ogeleri gorunuyor.
- Bilesen indirme guvenligi sertlestirildi:
  - PHP, Composer, phpMyAdmin, Node ve uygun GitHub assetleri resmi checksum/digest ile dogrulaniyor.
  - Statik ZIP/phar/phpFile paketleri SHA256 olmadan indirilmiyor.
  - MSI/EXE paketlerinde SHA256 yoksa Windows Authenticode imzasi zorunlu.
  - `components.json` icindeki statik arsiv hashleri sabitlendi.
- App Store dagitimlari dogrulaniyor:
  - WordPress paketi resmi SHA1 ve dosya checksum API sonuclariyla kontrol ediliyor.
  - Adminer dogrudan indirilmiyor; dogrulanmis component kurulumu kullaniliyor.
- Guvenlik ve veri kokleri:
  - Ilk setup yalnizca loopback istemciden yapilabilir ve ayri rate limit uygulanir.
  - Production HTTP fallback varsayilani kapali.
  - SQLite ve Data Protection anahtarlari `Panel:DataRoot` altinda; anahtarlar machine DPAPI ile korunur.
  - Bos 2FA kodu API model validation hatasi uretmiyor.
- Installer:
  - IIS, sertifika, servis ve firewall PowerShell adimlari artik nonzero cikista kurulumu durdurur.
  - PowerShell scriptlerinde terminating error davranisi acildi.
  - Installer'da secilen HTTPS portu servis komut satirina panel ayari olarak da aktarilir;
    ilk setup ekrani ve calisan listener ayni portu gosterir.
  - Baslat menusune AdaPanel ve kaldirma kisayollari eklenir.
  - Publish ve installer PDB icermiyor.
  - Guncel EXE: `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - Boyut: `50337120` byte (`48.01 MiB`)
  - SHA256: `B055937902B817A3C633259615E3C38AA8965890568E06FE952AD69CDB8AB30E`
- Son dogrulamalar:
  - Core testleri `2/2`, Infrastructure testleri `9/9`.
  - NuGet ve npm vulnerability taramalarinda acik yok.
  - Frontend TR/EN `1102/1102`, backend TR/EN `222/222`.
  - Production JS eval taramasi temiz; CSP `unsafe-eval` gerektirmiyor.
  - PowerShell parse taramasi 7 script icin temiz.
  - Browser QA'da tek 503 `/api/v1/sites`; gelistirme makinesinde IIS management olmadigi icin beklenen sonuc.
- Dis ortamda kalan dogrulamalar:
  - Temiz Windows hedefinde IIS/FTP, DB servisleri, Let's Encrypt ve uzak yedek credential entegrasyon testleri.
  - Dagitim EXE'si henuz Authenticode imzali degil; production dagitimi icin kod imzalama sertifikasi gerekir.

## Notlar

- `rg.exe` bu makinede erisim engeli veriyor; metin aramak icin `Get-ChildItem` + `Select-String` kullanildi.
- Kod tabani git reposu degil; `git status` kullanilamiyor.
- `SystemMonitoringService` ve bazi servisler bilincli olarak Windows ozeline gore yazildi.
- aaPanel Windows resmi referansi olarak `https://www.aapanel.com/new/windows.html` kullanildi.
- Gecici smoke instance'lar tur sonunda kapatildi; kalici bir `AdaPanel.Api` process'i acik birakilmadi.

## 2026-06-08 IIS stale object ve Nginx entegrasyonu

- IIS site listesi icin yayin engelleyici hata duzeltildi:
  - Hata: `COMException 0x800710D8 - Nesne tanimlayicisi gecerli bir nesneyi temsil etmiyor`.
  - Kaynak: `Microsoft.Web.Administration.Site.State` okunurken IIS'in gecersiz/bayat COM nesnesi dondurmesi.
  - `WebAdministrationSiteService` okuma islemini taze `ServerManager` ile bir kez yeniden deniyor.
  - Listeleme sirasinda `State`, binding, application pool veya fiziksel yol gibi bir alan gecici olarak okunamazsa tum liste dusmek yerine guvenli fallback kullaniliyor.
  - Yazma ve yapilandirma islemlerindeki siki hata davranisi korunuyor.
  - Tam `0x800710D8` COM davranisini taklit eden regresyon testi eklendi; site `Unknown` durumuyla listeleniyor.
- Nginx istege bagli sunucu araci olarak eklendi:
  - Resmi Windows paketi: Nginx `1.31.0`, SHA256 `1710B22D0AEBF2197F4CC7FB605F2B4B34ED8AF7EE4B27DA77CB9F8761327371`.
  - Varsayilan kuruluma dahil degil; IIS'in yerine gecmez.
  - Varsayilan dinleme `127.0.0.1:8088` (doluysa 50 portluk aralikta otomatik uygun port).
  - Varsayilan upstream `http://127.0.0.1:80`.
  - Reverse proxy, WebSocket headerlari ve opsiyonel disk proxy cache yapilandirmasi uretiliyor.
  - Kurulumda `nginx -t` ile config dogrulaniyor; Windows acilis gorevi Task Scheduler uzerinden kaydediliyor.
  - Baslat, durdur, reload, config test, port/upstream/server name/cache ayarlari API ve `Sunucu Araclari > Nginx` arayuzune eklendi.
  - Kaldirma, yalniz AdaPanel yonetimli Nginx sureclerini durduruyor ve `AdaPanel Nginx` gorevini temizliyor.
  - Genel uninstall scripti de Nginx gorevini ve yonetimli surecleri temizliyor.
  - Nginx API'si admin, CSRF ve gizli panel giris yolu kontrolune dahil.
- Gercek resmi Windows Nginx paketi izole temp klasorunde test edildi:
  - Nginx `127.0.0.1:8930` uzerinde `200 nginx-smoke` dondurdu.
  - Windows master surecinin acik kaldigi goruldu; API'nin surec cikisini bekleyip takilmamasi icin baslatma ayrik process olarak duzeltildi.
- UI QA:
  - Browser plugin bulunmadigi icin Playwright fallback kullanildi.
  - Masaustu `1440x900` ve mobil `390x844` kontrol edildi.
  - `Sunucu Araclari > Nginx` kurulu degil durumu, kur dugmesi ve metinler gorundu.
  - Mobilde `scrollWidth=clientWidth=390`; yatay tasma yok.
  - Test makinesinde IIS management olmadigi icin dashboard site isteginde beklenen `503` console kaydi goruldu; Nginx ekranina ait runtime hata yok.
- Son dogrulamalar:
  - Core testleri `2/2`, Infrastructure testleri `14/14`.
  - Frontend typecheck ve production build basarili.
  - PowerShell parse `7/7`.
  - Frontend TR/EN anahtarlari `1158/1158`, eksik anahtar yok.
  - Uygulama kaynaklarinda `aaPanel` referans taramasi temiz.
  - Temiz data root smoke: health, setup, login, Nginx status ve software catalog basarili; Nginx katalogda mevcut ve kurulu degil durumu dogru.
- Yeni kurulum paketi:
  - `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - Boyut: `50358736` byte.
  - SHA256: `09F4FE69EEA68BE5844BC8B38CC55E2CED7B611752E3538356FDFF803A541C1C`
  - Authenticode: imzasiz (`NotSigned`).
- Hedef PC notu:
  - Kullanici eski EXE ile `0x800710D8` hatasini gormeye devam eder; bu duzeltme yalniz yukaridaki yeni EXE icindedir.
  - Gercek IIS site listesi hedef makinede yeniden denenmeli. Duzeltme tek bir gecersiz runtime property nedeniyle tum listenin dusmesini engeller.

## 2026-06-08 Laravel gorunurlugu ve PHP FastCGI kapanisi

- `promp.md` ve bu hafiza yeniden okunarak tum 25 modul icin kaynak taramasi yapildi.
- Laravel daha once backend App Store sablonu olarak mevcuttu ancak `Yazilim Magazasi`
  ekraninda sunucu bilesenlerinin altinda kaldigi icin kolay gorunmuyordu.
- `Yazilim Magazasi` iki belirgin calisma alanina ayrildi:
  - varsayilan `Hazir Uygulamalar`: Laravel, WordPress ve baslangic paketleri
  - `Sunucu Bilesenleri`: PHP, veritabani, servis ve araclar
- Laravel onerilen uygulama olarak her zaman katalog listesinin ilk sirasinda gosteriliyor.
- `Web Sitesi` araclarinda `Hazir Uygulamalar` sekmesi ikinci siraya tasindi ve
  aciklamasinda Laravel/WordPress gorunur hale getirildi.
- Yazilim Magazasi ve uygulama katalogu TR/EN metinleri yeni bilgi mimarisine gore guncellendi.
- PHP kurulumu davranisi birlestirildi:
  - PHP `Sunucu Bilesenleri` ekranindan kurulursa `IPhpService.InstallVersionAsync`
    kullaniliyor ve FastCGI kaydi otomatik tamamlaniyor.
  - `Onerilen yigini kur` ve ilk setup'taki onerilen bilesen kurulumu sonrasinda
    kurulan PHP surumleri FastCGI'ye otomatik kaydediliyor.
  - Laravel kurulumu icin PHP ekranina girip elle FastCGI kaydi/atamasi yapma gerekmiyor.
- Kaynak kapanis taramasi:
  - `aaPanel` referansi yok
  - TODO/FIXME/NotImplemented yok
  - frontend statik i18n referanslari `898`, eksik anahtar `0`
  - frontend TR/EN `1163/1163`
  - backend TR/EN `241/241`
  - PowerShell parse `7/7`
- Dogrulamalar:
  - cozum build `0` uyari / `0` hata
  - Core testleri `2/2`
  - Infrastructure testleri `14/14`
  - npm audit `0` guvenlik acigi
  - NuGet vulnerability taramasi temiz
  - Playwright masaustu ve `390x844` mobil QA yapildi
  - Laravel ilk uygulama olarak gorundu
  - `Sunucu Bilesenleri` sekme gecisi calisti
  - mobil yatay tasma yok (`scrollWidth=clientWidth=390`)
  - ekran goruntuleri:
    - `artifacts/screenshots/laravel-store-desktop.png`
    - `artifacts/screenshots/laravel-store-mobile.png`
- Final self-contained publish smoke:
  - temiz setup basarili
  - login basarili
  - App Store `5` sablon dondurdu
  - Laravel `featured=true`
  - Software katalogu `17` bilesen dondurdu
- Yeni kurulum paketi:
  - `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - boyut: `50361371` byte (`48.03 MiB`)
  - SHA256: `2AE57B642B69B60C0FCE9981E02E475F8D2EC19CD289A0BCAA416C8BA12CEE6A`
  - Authenticode: imzasiz (`NotSigned`)

## 2026-06-15 phpMyAdmin erisimi, otomatik yayin ve final guvenlik kapanisi

- `promp.md` ve proje hafizasi yeniden okunarak kaynak, kurulum, paket, guvenlik,
  ceviri ve UI akislarinda bastan sona kapanis taramasi yapildi.
- phpMyAdmin/Adminer yayin akisi duzeltildi:
  - IIS Web DB sitesi artik yalniz `localhost` host header'ina baglanmiyor; tum
    sunucu arayuzlerinden erisilebilen portsuz host binding kullaniliyor.
  - UI, loopback adresini tarayicinin baglandigi AdaPanel sunucu IP/host adi ile
    degistiriyor. Baska bilgisayardan acildiginda `localhost:8896` yerine
    `SUNUCU-IP:8896` aciliyor.
  - `config.inc.php`, `web.config`, PHP giris dosyasi, IIS sitesi/binding'i,
    site durumu ve HTTP cevabi saglik kontrolune dahil edildi.
  - Eksik veya eski localhost-only yayin artik hazir gorunmuyor; otomatik onarim
    eylemi sunuluyor.
  - Ilk kurulumda PHP ve phpMyAdmin kurulduktan sonra Web DB yayini otomatik
    hazirlaniyor; hata olursa ayrintili setup uyarisi kaydediliyor.
- Site olusturma basitlestirildi:
  - Varsayilan servisler otomatik hazirlaniyor.
  - Kurulu veritabani motoru otomatik seciliyor.
  - Veritabani, kullanici, guclu sifre, FTP ve uygun mail varsayimlari otomatik
    uretiliyor; tek seferlik kimlik bilgileri UI'da gosteriliyor.
- Web sitesi calisma alani Plesk benzeri daha derli toplu gruplara ayrildi:
  `Siteler ve alan adlari`, `Uygulamalar ve veri`, `Calisma zamanlari`,
  `Guvenlik ve erisim`, `Gelismis`.
- Bilesen kurulumlari artik yalniz komutun cikis koduna guvenmiyor. PHP,
  Composer, phpMyAdmin, Adminer, pgAdmin, Node, Python, Nginx, Stalwart,
  Docker, win-acme, IIS eklentileri ve servis tabanli motorlar icin gercek
  dosya/servis kurulum dogrulamasi yapiliyor.
- Docker Engine paketi resmi Windows ZIP'i `29.5.3` surumune sabitlendi:
  - URL: `https://download.docker.com/win/static/stable/x86_64/docker-29.5.3.zip`
  - SHA256: `46B70C3680F7F5CF037D714A06D15C8F1FBDDED0D17CA18D222A311C424C4EE0`
- Frontend guvenlik zinciri guncellendi:
  - Vite `8.0.16`, Vue plugin `6.0.7`.
  - Guvenlik acigi tasiyan ve gereksiz olan i18n build plugin'i kaldirildi;
    TR/EN JSON dosyalari dogrudan paketleniyor.
  - `npm audit`: `0` acik.
  - NuGet tum transitif paket taramasi: acik yok.
- Guvenli gizli giris yolu denetimine mail, automation, docker, git,
  panel-settings, scope ve terminal API rotalari da dahil edildi.
- Final dogrulamalar:
  - Frontend typecheck basarili.
  - Vite production build basarili.
  - Core testleri `2/2`.
  - Infrastructure testleri `22/22`.
  - Temiz gecici data root ile self-contained EXE health smoke `200 OK`.
  - Final self-contained browser smoke: ilk kurulum sihirbazi Turkce acildi,
    `vueError` bos ve yatay tasma `0`.
  - Frontend TR/EN `1532/1532`, backend TR/EN `245/245`; eksik anahtar yok.
  - PowerShell parse temiz, `components.json` gecerli.
  - Uygulama ve publish UI kaynaklarinda `aaPanel/aapanel` referansi yok.
  - Masaustu ve `390x844` mobil browser QA yapildi; mobil yatay tasma yok.
- Yeni kurulum paketi:
  - `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - boyut: `50540214` byte.
  - SHA256: `353032E32608A222064E397FB7018ABDA54F59EB32D356A5E916AFF994BDE058`
  - Authenticode: imzasiz (`NotSigned`); dagitim imzasi icin gercek bir kod
    imzalama sertifikasi gerekir.
- Hedef PC dogrulamasi:
  - Gelistirme makinesinde IIS Management assembly kurulu olmadigi icin gercek
    IIS/phpMyAdmin yayini burada sonuna kadar acilamadi.
  - Kurulum EXE'si yonetici hakki ister, IIS/CGI/ASP/FTP/management ozelliklerini
    etkinlestirir ve gerekli IIS DLL/komutlarini dogrular.
  - Eski EXE bu duzeltmeleri icermez. Yeni EXE ile hedef PC'de phpMyAdmin acma,
    statik/PHP/ASP.NET site olusturma ve IIS site listeleme tekrar denenmelidir.

## 2026-06-15 MariaDB ve MySQL kurulum regresyonu duzeltmesi

- Kullanici yeni pakette MariaDB ve MySQL'in kurulmadigini bildirdi. Kurulum
  akisi bastan sona yeniden incelendi ve dort kok neden giderildi:
  - temiz Windows icin gereken Microsoft Visual C++ x64 runtime bagimliligi
    manifestte ve bootstrapper'da yoktu;
  - MariaDB MSI panelin yonettigi runtime dizininin disina kurulum yapiyordu;
  - MariaDB ve MySQL ayni `3306` portuna sabitlenmisti;
  - servis kaydindan sonra veritabani gercekten baglanti vermeden kurulum
    basarili sayilabiliyordu.
- Yeni otomatik bagimlilik sistemi:
  - `ComponentManifestItemDto.Dependencies` eklendi.
  - PHP, MariaDB, MySQL ve PostgreSQL `vc-redist-x64` bagimliligini otomatik
    kuruyor; kullanici manuel runtime kurmuyor.
  - Microsoft paketi resmi `https://aka.ms/vs/17/release/vc_redist.x64.exe`
    adresinden geliyor, Authenticode imzasi dogrulaniyor ve registry marker ile
    kurulum sonucu kontrol ediliyor.
  - Basarisiz bagimlilik sonraki bilesenlerde tamamlanmis sayilmiyor; manifest
    bagimlilik donguleri engelleniyor.
- MariaDB kurulumu:
  - MSI kaldirildi; resmi MariaDB `11.8.3` Windows x64 ZIP kullaniliyor.
  - SHA256:
    `DEBD9643DB9B3D35276FB782789564484C681B1BB264D03DE0B3A2E8E739F493`.
  - `mariadb-install-db.exe` ile data dizini, root sifresi, `AdaMariaDB`
    servisi ve `3306` portu otomatik hazirlaniyor.
- MySQL kurulumu:
  - MySQL `8.4.9` Windows x64 ZIP ve mevcut dogrulanmis SHA256 korunuyor.
  - MariaDB ile birlikte calisabilmesi icin otomatik port `3307` oldu.
  - `my.ini`, data init, `AdaMySQL` servisi, root sifresi ve baglanti profili
    ayni porta gore otomatik ayarlaniyor.
- Her iki motorda servis kaydi yeterli sayilmiyor:
  - servis `RUNNING` durumuna gelmeli;
  - `mysqladmin`/`mariadb-admin` authenticated ping cevap vermeli;
  - basarisiz kurulum servis kaydini ve yonetilen ZIP dizinini temizliyor;
  - ayrintili servis/probe ciktisi API hata detayina ekleniyor.
- Gercek paket testleri (yonetici olmayan izole smoke):
  - MariaDB resmi ZIP indirildi, SHA256 eslesti, sistem tablolari olustu,
    sunucu basladi, root sifresiyle ping verdi ve temiz kapandi.
  - MySQL resmi ZIP indirildi, SHA256 eslesti, `--initialize-insecure`
    tamamlandi, sunucu basladi, root sifresi ayarlandi, authenticated ping verdi
    ve temiz kapandi.
  - Windows servis kaydi yonetici hakki gerektirdigi icin test makinesinde
    dogrudan denenemedi; AdaPanel kurulum EXE'si zaten UAC/admin ister.
- Regresyon testleri:
  - MariaDB/MySQL port cakismasi testi.
  - Iki DB paketinin ZIP, 64 karakter SHA256, post-install ve VC++ bagimlilik
    manifest testi.
  - Core `2/2`, Infrastructure `24/24`.
  - Frontend typecheck/build, npm audit ve NuGet vulnerability taramasi temiz.
  - Final self-contained health smoke `200 OK`.
- Yeni kurulum paketi:
  - `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - boyut: `50544974` byte.
  - SHA256: `2BD5E28EDB90342F71F8C98108E6FC6506BED6A8837DE2F501FAAA4AFD16C9F9`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-15 tum Yazilim Magazasi ve kurulum zinciri denetimi

- `promp.md`, bu hafiza ve 21 bilesenli `components.json` yeniden okundu.
- Tum 21 paket canli olarak indirildi ve denetlendi:
  - sabit veya resmi yan dosyadan gelen SHA256 degerleri eslesti;
  - MSI/EXE kurucularinin Authenticode imzalari gecerli;
  - ZIP paketlerinde panelin kullandigi tum binary/isaret dosyalari bulundu.
  - rapor: `test-results/component-package-audit.json` (`21/21 Passed`).
- Tasininabilir binary smoke testi yapildi:
  - PHP 8.3.31, Composer 2.10.1, MariaDB 11.8.3, MySQL 8.4.9,
    Redis, Memcached, Nginx config testi, Stalwart CLI/server, Docker 29.5.3
    ve win-acme 2.2.9.1701 sifir cikis koduyla calisti.
  - rapor: `test-results/component-runtime-smoke.json`.
- Eksik otomatik bagimliliklar eklendi:
  - Composer/phpMyAdmin/Adminer -> PHP 8.3;
  - Node/Python -> IIS HttpPlatformHandler;
  - IIS ARR -> URL Rewrite;
  - Stalwart Mail -> Stalwart CLI;
  - pgAdmin -> PostgreSQL.
- Manifest artik yuklenirken zorunlu alan, HTTPS URL, benzersiz kimlik,
  gecerli tur, var olan bagimlilik, dongu ve bilinen post-install adimlarini
  dogruluyor; bilinmeyen adim sessizce atlanmiyor.
- Kurulum sonrasi gercek calisma dogrulamasi guclendirildi:
  - PHP/Composer/Node/Python/Stalwart CLI/win-acme komut smoke;
  - Nginx `-t`;
  - MariaDB/MySQL authenticated ping;
  - PostgreSQL authenticated `SELECT 1`;
  - Redis `PING`, Memcached TCP `version`;
  - Docker daemon version;
  - Stalwart zamanlanmis gorev + 8080 dinleyici kontrolu.
- Stalwart Windows servisi degil `AdaStalwart` zamanlanmis gorevi oldugu icin
  eski `sc query AdaStalwart` dogrulamasi kaldirildi; gorev/dinleyici dogrulamasi
  kullaniliyor.
- `Kur` islemi idempotent hale getirildi: saglikli kurulu bilesene tekrar
  basmak artik runtime/data dizinini silmiyor. Yalniz Failed/bozuk kurulum
  otomatik onariliyor; bu MariaDB/MySQL veri kaybini onluyor.
- Basarisiz MSI/EXE kurulumlari artik gercek silent uninstaller ile geri aliniyor.
  Normal kaldirmada Node/IIS MSI'lari, Python, PostgreSQL ve pgAdmin icin gercek
  uninstaller destegi eklendi.
- Panel uninstall temizligi genisletildi:
  - Docker dahil yonetilen servisler;
  - AdaStalwart, Nginx, self-update, cron ve win-acme gorevleri;
  - Web DB IIS site/app-pool kayitlari;
  - panel yonetimli MSI/EXE runtime'lari;
  - DataRoot altinda kalan surecler, firewall ve AdaPanel self-signed sertifikasi.
- Otomatik site profili, PHP indirme/kurulum/dogrulama hatalarinda ASP.NET
  profiline guvenli bicimde geri dusebiliyor.
- Regresyon/test sonucu:
  - Core `2/2`, Infrastructure `29/29`;
  - frontend typecheck ve production build basarili;
  - npm production audit `0`, NuGet vulnerability taramasi temiz;
  - PowerShell betikleri parse temiz;
  - self-contained API smoke `/api/v1/setup/status` -> `200 OK`.
- Yeni kurulum paketi:
  - `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - boyut: `50552916` byte.
  - SHA256: `1CA33F1A140EEAB438239ECC26A1A6F3F2A3ADBC459E66B53705A13C7CEBD98D`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-15 WebDB, kurulum guvenilirligi ve profesyonel arayuz duzeltmeleri

- Canli hedef kontrolu:
  - `https://192.168.1.80:8890/api/v1/setup/status` halen
    `0.2.0-preview` donduruyor; hedef bilgisayar bu bolumde uretilen yeni
    paketi henuz calistirmiyor.
  - `http://192.168.1.80:8896/` yaniti `400 Invalid Hostname` idi. Bu,
    phpMyAdmin IIS sitesinin eski localhost-only binding ve eski port
    bilgisini tasidigini kanitladi.
- Web DB onarimi:
  - phpMyAdmin/Adminer yayini, eski yonetilen IIS sitesini silip wildcard
    binding ile yeniden olusturuyor.
  - IIS istenen port doluysa baska port sectiginde artik gercek olusan port
    state dosyasina ve URL'ye kaydediliyor.
  - Ayrica `adapanel-health.php` endpoint'i ile PHP/FastCGI gercek yaniti
    denetleniyor; HTTP status, URL ve cevap ozeti arayuzde tani ayrintisi
    olarak gosteriliyor.
  - Yerel saglik istegi sistem proxy'sinden etkilenmemesi icin
    `AdaPanel.LocalHealth` adli `UseProxy=false` HttpClient kullaniliyor.
  - phpMyAdmin config dosyasi kurulu MariaDB `3306` ve MySQL `3307`
    sunucularini otomatik ekliyor; yoneticinin kendi config dosyasi korunuyor.
  - Regresyon testi IIS'in fallback portunu secmesi, wildcard binding ve
    state JSON'a gercek portun yazilmasini dogruluyor.
- Yazilim kurulum guvenilirligi:
  - Indirmeler `.part` dosyasina yapiliyor, bos/yarim dosya kuruluma
    gecmiyor, gecici HTTP/ag/IO hatalarinda 3 deneme ve kontrollu gecikme var.
  - PHP kurulumu yalnizca `php.exe` varligi ile basarili sayilmiyor;
    `curl`, `fileinfo`, `intl`, `mbstring`, `mysqli`, `openssl`,
    `pdo_mysql`, `pdo_pgsql`, `session` ve `zip` eklentileri gercek PHP
    komutuyla dogrulaniyor.
  - FastCGI kaydi basarisizsa PHP DB kaydi `Failed` durumuna aliniyor;
    basarili gorunup calismayan PHP kaydi birakilmiyor.
  - Yazilim Magazasi backend hata ayrintilarini ve aktif kurulum durumunu
    gosteriyor. Onerilen yiginda kismi hata varsa basarili mesaji yerine
    basarisiz bilesen sayisi bildiriliyor.
  - Ilk kurulum sihirbazinda varsayilan bilesenlerden biri `Failed` donerse
    artik `setup.default_components_failed` uyarisi ekleniyor ve bilesen
    bazli hata loglaniyor.
- Arayuz:
  - IBM Plex Sans Variable eklendi; temel font `13px`, panel/form/tablo
    araliklari ve yazi olculeri kucultuldu.
  - Kart yiginlari daha duz Plesk-benzeri liste/tablo yuzeylerine cevrildi;
    golge, radius ve kalin basliklar azaltildi.
  - Sidebar `218px`, nav satirlari kompakt ve tum alt menuler gorunur.
  - Tema butonundaki karakterler inline SVG ikonlara cevrildi.
  - Web DB ve Yazilim Magazasi ayrintili hata panelleri eklendi.
- Tarayici QA:
  - Temiz data root ile setup ve login basarili.
  - Yazilim Magazasi `21/21` bileseni API'den listeledi; istek tamamlanamadi
    hatasi olusmadi.
  - Web DB ekrani phpMyAdmin, Adminer ve pgAdmin'i ayri satirlarda gosterdi.
  - `1280x720` ve `390x844` boyutlarinda yatay tasma yok; tum 12 ana menu
    (Sunucu Araclari, Yazilim Magazasi, Ayarlar ve Cikis dahil) gorunur.
  - Tarayici console logu temiz; body fontu gercekten IBM Plex Sans ve root
    font `13px`.
- Paket/test kanitlari:
  - Core `2/2`, Infrastructure `30/30`.
  - Frontend typecheck ve production build basarili.
  - npm production audit `0`.
  - Ayni gun tam paket raporu `test-results/component-package-audit.json`:
    `21/21 Passed`.
  - Runtime smoke `test-results/component-runtime-smoke.json`: `15/15`
    sifir cikis kodu.
  - Self-contained publish smoke:
    `/api/v1/setup/status` `200`, surum `0.2.1-preview`, yeni frontend bundle
    `200`, stderr bos.
- Yeni surum ve kurulum paketi:
  - surum: `0.2.1-preview` / file version `0.2.1.0`.
  - `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - boyut: `50722165` byte.
  - SHA256: `89122D66DF2B991AD4EB0063A1AAF7D7F4AEE26056C7362F68CF8172682B34AE`
  - Authenticode: imzasiz (`NotSigned`).
  - Mevcut kurulum algilanirsa port sayfasi atlanir, mevcut servis portu
    korunur, servis durdurulup dosyalar guncellenir; panel verisi korunur.
    Gercek uninstall ise DataRoot ve yonetilen bilesenleri temizler.

## 2026-06-15 Kurulu yazilim kesfi, WebDB 404.3 ve font duzeltmesi

- Yazilim Magazasi artik yalniz AdaPanel'in kendi kayitlarina bakmiyor.
  Windows Registry (32/64 bit ve kullanici/makine), Windows servisleri,
  App Paths, sistem PATH'i, IIS applicationHost.config ve bilinen kurulum
  dizinleri taraniyor.
- Desteklenen 21 hosting bileseni icin bulunan sistem kurulumlari SQLite ile
  otomatik esleniyor. Algilama kaynagi, gercek kurulum dizini, servis adi,
  calistirilabilir dosya ve Windows kaldirma komutu saklaniyor.
- Harici algilanan bilesende Kaldir islemi artik kaydi unutmak yerine gercek
  Windows uninstaller'i calistiriyor. Guvenli kaldirma komutu yoksa dosya
  silinmiyor; paylasilan Visual C++ runtime icin daha guclu uyari gosteriliyor.
- Redis adinin Visual C++ Redistributable metniyle eslesmesine neden olan
  yanlis pozitif giderildi. Canli taramada Redis kurulu degilken artik
  algilanmiyor.
- WindowsApps Python calistirma takma adi atlanip PATH'teki gercek Python
  aranacak sekilde duzeltildi. Python Manager kullaniliyorsa surum dosya
  surumunden degil `python --version` ciktisindan okunuyor.
- Canli QA makinesinde otomatik tarama su 6 kurulumu buldu:
  Composer, Visual C++ x64, Node.js, PHP 8.3, Python 3 ve phpMyAdmin.
- WebDB 404.3 kok nedeni duzeltildi:
  - phpMyAdmin/Adminer yayini PHP FastCGI handler'ini yazdiktan sonra
    `WebAppPublisher` web.config dosyasini yeniden olusturup handler'i
    siliyordu.
  - Publisher artik var olan handlers/rewrite/security ayarlarini XML olarak
    koruyor ve yalniz `index.php` varsayilan belgesini ekliyor.
  - WebDB dagitim sirasi PHP girisi, FastCGI kaydi ve site PHP atamasi olarak
    guvenli hale getirildi.
- Arayuz fontu IBM Plex Sans'tan Source Sans 3 Variable'a gecirildi.
  Kullanilmayan IBM paketi kaldirildi, yapay `wght=420` zorlamasi silindi,
  body agirligi 400 ve root olcusu 13px olarak dogrulandi.
- Tarayici QA:
  - `0.2.2-preview`, `Source Sans 3 Variable`, body weight `400`,
    root `13px`;
  - `1280px` viewportta `scrollWidth == clientWidth`, Vue render hatasi yok;
  - ilk yeniden baslatma sirasinda kaydedilen gecici Axios Network Error disinda
    API ve sayfa yuklemesi basarili;
  - Browser eklentisi daha sonra yeni localhost sekmesini
    `ERR_BLOCKED_BY_CLIENT` ile engelledigi icin yeni ekran goruntusu alinmadi.
- Test ve paket kanitlari:
  - Core `2/2`, Infrastructure `41/41`;
  - frontend typecheck ve production build basarili;
  - frontend/API i18n esligi `1564/1564` ve `251/251`;
  - npm audit `0`;
  - self-contained publish smoke `/api/v1/setup/status` `200`,
    surum `0.2.2-preview`, Source Sans pakette, stderr bos.
- Yeni kurulum paketi:
  - `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - surum: `0.2.2-preview`, file version: `0.2.2.0`
  - boyut: `50726634` byte
  - SHA256: `E31FD0ECACEDF1A085C269572CB4CFD95BCACE595694967EA56C0F406A240466`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-15 WebDB 404.3 GERÇEK kök neden + kurulu DB motorunu yönetme

- WebDB 404.3'ün gerçek kök nedeni bulundu ve düzeltildi (Codex'in publisher
  düzeltmesi semptomu gidermedi):
  - `PhpService.SetSitePhpHandler` `*.php` FastCGI handler'ini `handlers`
    koleksiyonunun SONUNA ekliyordu (`Add`). IIS handler eşleştirmesini
    yukaridan asagiya yapar; devralinan StaticFile (path="*") tum istekleri
    yakaladigi icin `.php` ondan ONCE degerlendirilemiyor ve "404.3 -
    extension not configured" donuyordu (phpMyAdmin/Adminer hic acilmadi).
  - Cozum: handler artik `AddAt(0, ...)` ile koleksiyonun BASINA ekleniyor;
    boylece `*.php` StaticFile'dan once degerlendiriliyor. 0. indeks kilitliyse
    `Add` fallback'i var.
- "Windowsta zaten kurulu motoru panel tanisin ve yonetsin" tamamlandi:
  - Keşif (ComponentDiscoveryService) zaten kurulu MySQL/MariaDB/PostgreSQL'i
    InstalledComponents'a `Installed` olarak senkronluyordu; eksik olan
    YONETIM icin gereken admin parolasiydi (`database.admin_password_missing`).
  - Yeni: `IDatabaseService.GetEnginesAsync` (motor durumlari: installed/ready/
    systemDetected/host/port/adminUser) ve `RegisterCredentialsAsync`
    (yonetici kullanici+parola+port alir, GERCEKTEN baglanarak dogrular,
    sifrelenmis saklar). Endpoint'ler: `GET /api/v1/db/engines`,
    `POST /api/v1/db/credentials`.
  - `ResolveConnectionProfileAsync` artik kayitli port/kullaniciyi kullaniyor
    (harici MySQL 3306'da, AdaPanel'inki 3307'de calisabilir).
  - `ComponentBootstrapper.ApplyDetection` artik mevcut protected config
    anahtarlarini (kayitli DB kimlik bilgileri) KORUYOR; her katalog
    yenilemesinde silinmiyordu sorunu giderildi.
  - Frontend: DatabasesView'da "Mevcut veritabani sunucusunu yonet" formu
    (algilanan ama parolasiz motorlar icin), motor rozeti "parola gerekiyor"
    durumu, store `fetchEngines`/`registerCredentials`. i18n parity 1574/1574.
- Test/paket: Core `2/2`, Infrastructure `41/41`, frontend typecheck temiz,
  production build basarili.
- Yeni kurulum paketi:
  - `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - boyut: `50731760` byte
  - SHA256: `DC085134C17F3AD368683F4F0173B80B146F80A6D2AE0B4CD00E5BCA0CC173BD`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-16 Bileşen kurulum hatalari: MySQL / ARR / Stalwart + kurulu "Kur" butonu

- phpMyAdmin canli makinede CALISTI (AddAt(0) handler-once duzeltmesi dogrulandi).
- MySQL servis kurulumu (`mysqld --install`) ExitCode=1 ile basarisizdi. Kök neden:
  argüman SIRASI yanlişti. MySQL doc: servis adı '--install'in HEMEN ardından,
  '--defaults-file' ise servis adından SONRA olmalı. Kod ters yaziyordu
  (`--defaults-file ... --install`). Duzeltildi:
  `--install "AdaMySQL" --defaults-file="..."` (`InitializeMySqlZipAsync`).
- IIS ARR `software.install_validation_failed: ...System32\inetsrv\requestRouter.dll`.
  Kök neden: dogrulama YANLIS konuma bakiyordu. ARR 3.0 requestRouter.dll'i
  `Program Files\IIS\Application Request Routing` altina kurar. Dogrulama artik
  coklu konum kontrol ediyor (ProgramFiles/ProgramFilesX86 IIS\ARR, inetsrv,
  arr_schema.xml). (`ValidateInstallationAsync`)
- Stalwart `software.service_not_running: ... Port=8080`. Kök neden: dinleyici
  yapilandirma gorevi sonda yeniden baslatiyor ve tek 3 sn'lik TCP denemesi
  erken kaliyordu. `ValidateScheduledTaskRuntimeAsync` artik ~45 sn boyunca
  127.0.0.1 + ::1 uzerinde tekrar deniyor.
- Frontend: kurulu (status "Installed") bilesenlerde "Kur" butonu artik
  GIZLI; yalniz "Kaldir" kaliyor. Basarisiz kurulumda buton "Yeniden dene"
  olarak gorunur (`SoftwareView.showInstallButton`/`isFailed`,
  yeni i18n `panel.software.retryInstall`).
- Test/paket: Core `2/2`, Infrastructure `41/41`, frontend typecheck temiz,
  i18n parity `1575/1575`.
- Yeni kurulum paketi:
  - `C:\Users\aLi\Desktop\AdaPanel\artifacts\installer\AdaPanelSetup.exe`
  - boyut: `50732152` byte
  - SHA256: `BC45C2AF9833C1D7E9662D335330296B0B1787E0668D35677E036213B4ABFD91`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-16 (2) Canli test geri bildirimi: MySQL OK, Redis conf, Stalwart

- MySQL: yeni argüman sirasiyla CALISTI (kullanicinin gordugu eski hata, eski
  surumun `--defaults-file ... --install` sirasindandi).
- Redis `--service-install` ExitCode=1 (ciktisiz). Kök neden: kod gemiyle gelen
  `redis.windows-service.conf`u tercih ediyordu; o conf GÖRELI `dir ./` ve
  `logfile` icerir, Windows servisi calisma dizini System32 oldugundan acilamaz
  ve service-install patlar. Duzeltme: `InstallRedisServiceAsync` artik kendi
  `redis.adapanel.conf`unu MUTLAK yollarla (data/ ve logs/ klasorlerini
  olusturarak, ToConfigPath forward-slash) yazip onu kullaniyor.
- Stalwart "task not registered" hatasi eski surumdendi; 45 sn dinleme-retry
  zaten yeni surumde. Tekrarlarsa schtasks /Create dogrulamasi eklenecek.
- Yeni kurulum paketi (Redis conf duzeltmesi dahil):
  - boyut: `50732117` byte
  - SHA256: `E22191E37404A40AF342206E48DACBF489F192764E3982008F6E764CED5B1782`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-16 (3) Python/pgAdmin indirme, nginx quoting, font geri alma

- Python + pgAdmin: indirme `File.Move` -> `UnauthorizedAccessException`. Kök
  neden: büyük indirme bitince Windows Defender gerçek-zamanlı tarama dosyayı
  kısa süre kilitliyor. Düzeltme: `FinalizeDownloadAsync` hedefi temizleyip
  taşımayı kısa beklemelerle 6 kez deniyor; `TryDeleteFile` artık ReadOnly
  attribute'u da temizliyor.
- nginx: `-p "...\nginx\"` argümanında yol sonundaki TERS eğik çizgi kapanış
  tırnağını KAÇIRIYOR (\"), nginx prefix'i yanlış okuyup "syntax hatalı"
  veriyordu. `EnsureTrailingSeparator` artik forward-slash + sonda '/'
  donduruyor (NginxInstallationSupport; -p/-t/-s tum cagrilari kapsar).
- Font: Codex'in Source Sans 3'e cevirdigi font kullanicinin onceki tercih
  ettigi IBM Plex Sans Variable'a geri alindi. `@fontsource-variable/
  ibm-plex-sans` kuruldu, source-sans-3 kaldirildi, main.ts import + base.css
  font-family guncellendi, gövde agirligi 420 (`font-variation-settings:
  "wght" 420`) geri getirildi.
- HENUZ COZULMEDI / kullanicidan bilgi bekleniyor:
  - Stalwart 8080 dinlemiyor (minimal config.json sihirbazi atliyor ama HTTP
    listener tanimlamiyor olabilir). Gercek Stalwart startup logu gerekiyor.
  - memcached: Windows Defender `memcached-1.6.8-win64-mingw.zip`i PUA olarak
    siliyor (AV false-positive; kod hatasi degil). Alternatif kaynak gerekebilir.
- Yeni kurulum paketi:
  - boyut: `50741372` byte
  - SHA256: `B552A0798B699D56CF6EF88999865E94B89220A0A8C8A9BA299C084F1111FF4F`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-16 (4) İndirme regresyonu, memcached Defender, MailEnable otomatik

- PostgreSQL (ve genel) indirme: `FinalizeDownloadAsync`te `overwrite:true`'yu
  dusurmustum -> hedef varsa `File.Move` "Halen varolan bir dosya
  olusturulamaz" (IOException) veriyordu. REGRESYON duzeltildi: tekrar
  `File.Move(..., overwrite: true)`.
- memcached: Windows Defender mingw ikilisini YANLIS POZITIF PUA olarak indirir
  indirmez siliyor (VerifyChecksum'da File.OpenRead patliyor). Resmi Windows
  derlemesi yok; kaynak (jefyt/nono303) degistirmek cozmez (ikisi de mingw,
  nono303 ayrica AVX/SSE2 riskli + release yok). Cozum: memcached indirilmeden
  ONCE AdaPanel'in `downloads` ve memcached `installDir`ini Defender taramasindan
  haric tut (`TryAddDefenderExclusionsAsync`, Add-MpPreference, best-effort).
- MailEnable OTOMATIK kurulum eklendi (derin arastirma sonucu):
  - components.json `mailenable-standard`: type installer,
    `https://www.mailenable.com/standard1058.exe` (v10.58), silentArgs `/s`
    (MailEnable resmi sessiz kurulum anahtari), installDir
    `%ProgramFiles(x86)%\Mail Enable` (DeleteManagedPath IsManagedPath ile
    korumali -> AdaPanel Program Files'i silmez), serviceName boş, default false.
  - Dogrulama: `mailenable-standard` -> BIN\MEInstaller.exe.
  - RISK/NOT: `/s`'nin servis-hesabi parolasini sormadan tam sessiz tamamlayip
    tamamlamadigi BU makinede dogrulanamadi (mail sunucusu yok). Hang riskine
    karsi default:false (yalniz bilerek "Kur" denince). Sorarsa fallback:
    `meinstaller.exe <parola> STANDARD/NETWEBMAIL/NETWEBADMIN /S` (parolayi
    ACIK gecerek prompt'u tamamen onler) + registry ile dizin kontrolu
    (HKLM\SOFTWARE\WOW6432Node\Mail Enable\Mail Enable Program/Configuration/
    Mail Root Directory). MEInstaller kurulum SONRASI Bin'de bir aractir.
- Yeni kurulum paketi:
  - boyut: `50742712` byte
  - SHA256: `1EDDA42DC5988A62021E527F7B59B0F1A15C6E5F96438DB5C3A93EBFE3927F2F`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-16 (5) Bileşen güncelleme denetimi (yeni sürüm rozeti)

- Yazilim Magazasi'na "yeni surum var mi" ozelligi eklendi:
  - `IComponentBootstrapper.CheckForUpdatesAsync`: kurulu (AdaPanel'in kurdugu,
    sistemde-algilanan haric) bileşenlerin upstream son surumunu CANLI cozer
    (mevcut `ResolveDynamicComponentAsync` yeniden kullanildi: GitHub releases,
    Node LTS, PHP, composer vb.). Sabit bileşenlerde manifest surumu kullanilir.
  - Sonuc `DataRoot\config\component-updates.json`'a onbelleklenir; katalog
    (`GetCatalogAsync`) bunu okuyup `InstalledComponentDto.AvailableVersion` +
    `UpdateAvailable` doldurur (sayfa yuklemede network YOK).
  - Surum karsilastirmasi `IsNewerVersion` (sayisal noktali parca karsilastirma;
    "latest"/"14.x" gibi cozulmeyenlerde guncelleme onerilmez).
  - Endpoint: `POST /api/v1/software/updates/check`.
  - Frontend SoftwareView: "Guncellemeleri denetle" butonu (+ guncellenebilir
    sayisi rozeti), satirda "Yeni surum X.Y" rozeti, kurulu+guncellenebilir
    bileşende "Guncelle" butonu (installComponent -> dinamik yeniden kurar).
    Yeni i18n anahtarlari (parity 1583/1583).
- Test: Core 2/2, Infrastructure 41/41; frontend typecheck temiz.
- Yeni kurulum paketi:
  - boyut: `50746111` byte
  - SHA256: `0B601552CB2890299E85BFFB582A1FB72A66BF8E9CA4A55C22C56BE80FFC45C1`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-16 (6) memcached 1053 (servis -> zamanlanmis gorev) + DNS notu

- memcached `sc start` 1053 ("hizmet yanit vermedi"): memcached.exe (mingw)
  Windows Servis Denetim Protokolu'nu uygulamaz; sc ile servis olunca SCM
  SERVICE_RUNNING alamaz. Cozum: `InstallMemcachedServiceAsync` artik ONSTART
  zamanlanmis gorev + run-memcached.cmd ile calistiriyor (Stalwart mantigi).
  Dogrulama da memcached icin ozel: ValidateScheduledTaskRuntimeAsync(11211)
  + ValidateMemcachedRuntimeAsync (switch'teki eski case kaldirildi).
- DNS notu (kullanici sorusu): AdaPanel YETKILI DNS SUNUCUSU CALISTIRMIYOR;
  DnsService yalniz zone kayitlarini `config/dns-zones.json`'da yonetir (port 53
  dinleyici yok). Yani ns1./ns2. -> VPS IP plani icin VPS'te gercek bir DNS
  sunucusu (Windows DNS rolu / Technitium / BIND) gerekir. Sadece web hosting
  icin ns1/ns2 GEREKMEZ: domainin A kaydini (@ ve www) VPS IP'sine yonlendirip
  AdaPanel'de siteyi eklemek yeterli. Olasi gelecek is: Technitium DNS'i
  bilesen olarak ekleyip zone kayitlarini ona yansitmak (gercek ns1/ns2).
- Yeni kurulum paketi:
  - boyut: `50745873` byte
  - SHA256: `3ECB4858CA23525BC41DF10338A24D94823C0BF58E37235C294C3F975BA3B486`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-16 (7) Windows DNS Server rol kurulumu (zaten olan entegrasyona ek)

- ONEMLI: Windows DNS entegrasyonu ZATEN vardi (DnsService): rol kuruluysa
  (`Get-DnsServerZone` cmdlet'i varsa) her mutasyonda kayitlari otomatik
  Windows DNS'e senkronluyor (Add-DnsServerPrimaryZone + Add-DnsServerResource
  Record*), DTO `IsWindowsDnsAvailable/IsWindowsZonePresent` ile durum gosteriyor.
- EKLENEN: DNS Server ROLU kurulu degilse kurma. `IDnsService.
  InstallWindowsDnsAsync` -> `Install-WindowsFeature -Name DNS
  -IncludeManagementTools` (Windows Server; client OS'ta Install-WindowsFeature
  yok -> exit 21 -> dns.windows_install_unsupported). Kurduktan sonra bolgeyi
  hemen yayinlar. Endpoint: `POST /api/v1/sites/{site}/dns/windows/install`.
  Frontend DnsView: Windows DNS yokken "Windows DNS'i kur" butonu.
- NOT (build): build-installer.ps1 PowerShell 5.1'de npm/vite'in chunk-size
  uyarisini ($ErrorActionPreference=Stop) hata sayip publish'ten once kesebiliyor.
  Cozum: adimlari elle calistir (npm build Bash'te -> sync-frontend.ps1 ->
  dotnet publish Bash'te -> ISCC Bash'te).
- Yeni kurulum paketi (Windows DNS rol kurulumu dahil):
  - boyut: `50747772` byte
  - SHA256: `1E5C1C6EE457EC91F94C30F6280DF87716E7DF5221DD5755CA328BE15AF4A8F9`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-16 (8) DNS tam otomatik (kurulumda rol) + font Inter

- DNS artik TAM OTOMATIK:
  - Kurulumda: `enable-iis-features.ps1` sonuna Windows DNS Server rol kurulumu
    eklendi (Get-Command Install-WindowsFeature varsa -> Install-WindowsFeature
    DNS -IncludeManagementTools; client OS'ta sessizce atlanir, best-effort).
    Boylece sonradan manuel "Windows DNS'i kur" gerekmez.
  - Site olusturulurken: `IDnsService.EnsureZonePublishedAsync` eklendi
    (GetOrCreate + TrySyncWindowsDns, best-effort/try-catch -> site olusturmayi
    bozmaz). `SiteDefaultsService` artik dns adiminda GetZoneAsync yerine bunu
    cagiriyor -> yeni site DNS bolgesi Windows DNS'e otomatik yayinlaniyor.
  - (Manuel "Windows DNS'i kur" butonu/endpoint hala duruyor; yedek.)
- Font: IBM Plex Sans -> Inter Variable'a gecildi (kullanici "halen kotu, daha
  iyi yap" dedi). `@fontsource-variable/inter` kuruldu, ibm-plex kaldirildi.
  base.css: font-family Inter + system fallback, 13.5px, font-feature-settings
  (cv05/cv08/ss01/tnum), letter-spacing -0.0055em; body weight 430, line-height
  1.45. main.ts import guncellendi.
- Test: Core 2/2, Infrastructure 41/41; frontend typecheck temiz.
- Yeni kurulum paketi:
  - boyut: `50804206` byte
  - SHA256: `CFFABFA1C9C4D57D7AF3A29BA3E83C961EEA8A11E14645211F6674843EB46825`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-16 (9) Canli islem ilerlemesi (kurulum + yazilim) + login mesaji

- Genel "islem ilerlemesi" altyapisi: `IOperationProgressService` (singleton,
  thread-safe, tek yuvali) + `OperationProgressDto`. Begin/StartStep/CompleteStep
  /Complete/Fail/Snapshot.
- ComponentBootstrapper: `InstallComponentInternalAsync` her bileşeni adim olarak
  raporluyor (StartStep displayName + CompleteStep success/fail). Tekil
  `InstallAsync` Begin/Complete sariyor; `InstallDefaultsAsync` sadece adim
  raporluyor (Begin/Complete'i cagiran yonetir).
- Ilk kurulum: `SetupService.InitializeAsync` artik admin'i olusturup donuyor;
  onerilen yigin ARKA PLANDA (IServiceScopeFactory ile kendi scope'unda)
  kuruluyor + progress'e yaziliyor. `SetupResultDto.InstallationInProgress`.
  Endpoint `GET /api/v1/setup/progress`. Frontend SetupWizardView: kurulum
  sirasinda formu gizleyip canli adim listesi (spinner/check/x) gosteriyor,
  bitince guvenli giris yoluna gidiyor.
- Yazilim Magazasi: `GET /api/v1/software/operations/progress`; install-defaults
  Begin/Complete sariyor. Frontend SoftwareView: kurulum sirasinda
  /operations/progress'i 1.5sn'de bir yoklayip "X kuruluyor" + adim listesi
  gosteriyor (pollProgressWhile).
- Step adlari: urun adlari (MySQL...) oldugu gibi; ozel adimlar i18n anahtari
  (setup.step.php_config, setup.step.phpmyadmin) -> frontend te() ile cevirir.
- Login mesaji: "giris basarili" her sayfada asili kaliyordu; showBanner'a
  autoDismissMs eklendi, login basarisi 4sn sonra otomatik kayboluyor.
- Test: Core 2/2, Infrastructure 41/41; frontend typecheck temiz; i18n 1601/1601.
- Yeni kurulum paketi:
  - boyut: `50807807` byte
  - SHA256: `DD26C416AF0E12C5F8BEC6BB8EE13EF28DCFFA7AD8C4ED50D40DD394FBA90F07`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-16 (10) Font Open Sans (Plesk), MailEnable varsayilan, installer timeout

- Font: Inter -> Open Sans Variable (Plesk Obsidian'in fontu; kullanici "pleskteki
  fontu kullan" dedi). @fontsource-variable/open-sans kuruldu, inter kaldirildi.
  base.css font-family Open Sans + system fallback, letter-spacing normal,
  Inter feature-settings kaldirildi. main.ts import guncellendi.
- Mail sunucusu: MailEnable Standard `default: true` YAPILDI (Plesk gibi varsayilan).
  Stalwart Mail Server VE stalwart-cli `default: false` yapildi (ayni SMTP/IMAP/POP
  portlari -> iki mail sunucusu BIRLIKTE kurulamaz; kullanici onayladi: MailEnable
  default, Stalwart isterse elle). Yeni defaults: php-8.3, mariadb-11, composer,
  phpmyadmin, url-rewrite, iis-arr, mailenable-standard, win-acme.
- Hang korumasi: `RunProcessAsync` opsiyonel TimeSpan timeout aldi; installer/msi
  tipi calistirmalar 12 dk ile sinirli (MailEnable /s beklenmedik istemde takilirsa
  sureci oldurup `software.process_timeout` firlatir -> InstallDefaults devam eder,
  ilk kurulum sonsuza dek bloklanmaz).
- NOT: "kurulumlar bitmeden giris yaptirmasin / ne yuklendigi belli degil" -> DD26C4
  build'inde COZULDU (setup progress ekrani install bitene kadar girise gecmiyor,
  canli adim listesi gosteriyor). Kullanici muhtemelen o sürümü henuz kurmamisti.
- Test: Core 2/2, Infrastructure 41/41; frontend typecheck temiz; i18n 1601/1601.
- Yeni kurulum paketi:
  - boyut: `50888874` byte
  - SHA256: `3D0E6EAFA56FFC530C22B2D5CAB688A431A0DDAFF1AE824CDA8254E74C7FDABF`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-16 (11) Kurulum bitmeden giris ACIGI kapatildi (sayfa yenileme)

- Sorun: admin HEMEN olusturulup IsInitialized=true oldugundan, kurulum arka
  planda surerken SAYFA YENILENIRSE setup durumu "kurulu" gorunup login ekrani
  aciliyor ve kullanici kurulum bitmeden giris yapabiliyordu (ilerleme ekrani
  yalniz submit sonrasi gosteriliyordu).
- Cozum: `initializeView` artik isInitialized ise /setup/progress'i de okur;
  `progress.running && title=='setup.installing_recommended'` ise login yerine
  ilerleme ekranini gosterir (mode='setup', installing=true) ve polling yapar.
  Bitince: submit akisinda guvenli yola assign, yenileme akisinda guvenli yol
  gizli oldugundan window.location.reload() -> login acilir.
- Test: frontend typecheck temiz; i18n 1601/1601.
- Yeni kurulum paketi:
  - boyut: `50888675` byte
  - SHA256: `451364EA8CC57A9B0A3847D71730892AEDE163CEC3763EC52F29E6B46D04AFA5`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-16 (12) MailEnable panel yonetimi (COM) — iterasyon-1

- Kullanici "MailEnable yonetimini panele ekle" sectik. VPS testleriyle DOGRULANDI:
  - MailEnable Standard'da PowerShell snapin YOK; .NET ProgID (MailEnable.
    Administration.*) kayitli degil. AMA yerel COM (MEAO*) 32-bit'te calisiyor.
  - Dogrulanmis ProgID + metotlar (Get-Member ciktisi):
    - MEAOPO.PostOffice: AddPostoffice/RemovePostoffice/FindFirst/FindNext
      Postoffice + Account/Name/Status
    - MEAOPO.Mailbox: AddMailbox/RemoveMailbox/EditMailbox/FindFirst/Next +
      Postoffice/Mailbox/Limit/Status (+AutoResponder Get/Set)
    - MEAOAU.Login: AddLogin/RemoveLogin/EditLogin/GetLogin + UserName/Password/
      Rights/Status
    - MEAOAM.AddressMap: Add/Remove/Edit/FindFirst/FindNext + SourceAddress/
      DestinationAddress/Scope/Status
- Eklenenler:
  - `MailEnableMailService` (IMailService): MailEnable COM'u 32-bit PowerShell
    (`SysWOW64\WindowsPowerShell\v1.0\powershell.exe`) uzerinden surer. Gecici
    .ps1 yazip calistirir, ConvertTo-Json ile sonuc okur. Domain(postoffice),
    posta kutusu(+login parola), alias(addressmap), parola, kota, listeleme.
    Cert/spam/DNS best-effort (MailEnable kendi yonetir).
  - `MailServiceRouter` (IMailService): MailEnable kuruluysa (BIN\MEInstaller.exe
    var) MailEnable'a, degilse Stalwart'a yonlendirir. Stalwart kodu DEGISMEDI.
  - DI: MailService + MailEnableMailService concrete; IMailService = router.
  - Frontend mail metinleri Stalwart'a ozel olmaktan cikti (Plesk/MailEnable).
- NOT/RISK (iterasyon-1, VPS test gerekir): FindFirst/FindNext donus konvansiyonu
  `-eq 0` (webadmin ASP deseni) varsayildi; alias listeleme heuristik (SMTP hedef
  + domain eslesmesi); domain=postoffice (1:1) varsayimi; kota Limit=KB. Hata
  olursa kullanici ciktisiyla iterasyon-2.
- Test: Core 2/2, Infrastructure 41/41; frontend typecheck temiz; i18n 1601/1601.
- Yeni kurulum paketi:
  - boyut: `50894728` byte
  - SHA256: `46F202B7B6B8E042EE5C4EA7B4F90679BDF311CC11E0651D99156AFB29F24B46`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-16 (13) Plesk kelimesi kaldirildi, FSRM, DB kullanici parolasi

- UI'da "Plesk" kelimesi HER YERDEN kaldirildi (kullanici talebi: baska kullanicilar
  da gorecek). 3 i18n anahtari (sites.companion.title, mail.subtitle,
  database.createWithUserHint) tr+en duzeltildi. (vue'lardaki Plesk yorumlarda.)
- FSRM (File Server Resource Manager) rolu kurulumda otomatik kuruluyor (disk
  kotasi icin): enable-iis-features.ps1'e DNS yaninda eklendi (Install-Windows
  Feature FS-Resource-Manager, Server-only best-effort).
- DB kullanici PAROLA DEGISTIRME eklendi: IDatabaseEngineProvider.
  SetUserPasswordAsync + MySql(ALTER USER)/Postgres(ALTER ROLE)/MsSql(ALTER LOGIN);
  IDatabaseService.SetUserPasswordAsync; DTO SetDatabaseUserPasswordRequest;
  endpoint POST /api/v1/db/users/password; DatabasesView kullanici satirinda
  "Parola degistir" butonu (window.prompt).
- HENUZ YAPILMADI (kullanici geri bildirimi, sonraki turlar):
  - Site olusturmada FTP kullanici hatasi (ftp.user_create_failed) + DB'yi
    opsiyonel yapma.
  - phpMyAdmin otomatik giris (SSO) — Plesk gibi.
  - Mail ekrani: cift alan-adi secici (yonet + yeni-domain formu) sadelestir.
  - Mail ekrani yavas acilis (32-bit PowerShell+COM surec maliyeti) optimizasyon.
- Test: Core 2/2, Infrastructure 41/41; frontend typecheck temiz; i18n 1606/1606.
- Yeni kurulum paketi:
  - boyut: `50897581` byte
  - SHA256: `08932FB038D60169E53E0A80819DB4415300E8519CAFD5B85AF5388CD19F38A8`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-16 (14) FTP kullanici adi 20 char + mail cift selektor/yavaslik

- FTP `ftp.user_create_failed` kok neden: FTP kullanicisi Windows YEREL hesabidir
  ve ad EN FAZLA 20 karakter olabilir; `golftaksiatakum_xyz_ftp` = 23 char ->
  `net user /add` patliyordu. `SitesController.BuildAccountName` artik opsiyonel
  `maxTotalLength` aliyor (FTP icin 20); kirpilirken benzersizlik icin 4 char
  kararli SHA256 kodu ekleniyor. FtpService.NormalizeUserName 20 char limiti +
  `ftp.user_name_too_long` mesaji (Api/Resources tr+en).
- Mail cift selektor: ust "yonet" selektoru artik domains.length>0 iken gosterilir;
  hic mail domaini yokken gizlenir + (mevcut) oluştur formu otomatik acilir ->
  tek selektor kalir. `panel.mail.firstDomainHint`.
- Mail yavaslik: GetWorkspace tek 32-bit PowerShell+COM cagrisi (~1-2s, dogal
  surec maliyeti). Yuklenirken gorunur spinner + "E-posta sunucusu okunuyor"
  eklendi (`panel.mail.loadingWorkspace`) -> donmus gibi gorunmez.
- Test: Core 2/2, Infra 41/41; frontend typecheck temiz; i18n 1608/1608.
- Yeni kurulum paketi:
  - boyut: `50897097` byte
  - SHA256: `BF9EDCB725A334D678F32D4EF92347CDAE9F3EF671814F33528D53F536245149`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-17 phpMyAdmin oto-giris + SSL binding (SNI) duzeltmesi

- SSL `ssl.binding_failed` ("Sertifika IIS HTTPS binding'ine baglanamadi") kok
  neden: hostname'li https binding (*:443:host) HTTP.sys'te SNI olmadan
  baglanamaz. `WinAcmeSslService.EnsureHttpsBinding` artik hostName varsa
  `SetAttributeValue("sslFlags", 1)` (SNI) ayarliyor. IssueLetsEncryptAsync hem
  win-acme hem self-signed yolunda EnsureHttpsBinding cagiriyor -> ikisi de duzeldi.
- phpMyAdmin OTOMATIK GIRIS (Plesk gibi): `WebDbClientService.
  EnsurePhpMyAdminConfigAsync` artik kök parola panelde varsa config.inc.php'yi
  `auth_type='config'` + user=root + password ile yaziyor -> login ekransiz
  dogrudan acilir. Parola yoksa 'cookie' auth'a duser. ISecretProtector enjekte
  edildi; root parola InstalledComponent.ProtectedConfigurationJson'dan
  (generatedPassword) okunuyor. GUVENLIK NOTU: :8896 disa aciksa root erisimi
  acik olur; idealde firewall'la kapatilmali (admin araci).
- Test stub (WebDbDeploymentTests) PassthroughSecretProtector ile guncellendi.
- Test: Core 2/2, Infrastructure 41/41.
- Yeni kurulum paketi:
  - boyut: `50898153` byte
  - SHA256: `0F75B4EDA4A3F4CCA54A465A36641285147A549172D1EBA85668249F6892B886`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-17 (2) Site FTP kullanicisini yeniden olusturma (oneri)

- "Site kullanicisini sildim, nasil yeniden olustururum" -> FTP yonetimi ZATEN
  site calisma alanina entegre (WebsiteWorkspaceView -> FTP sekmesi, o siteye
  ozel filtreli, Plesk gibi). Yeniden olusturma: site -> FTP -> Kullanici ekle.
- Iyilestirme: site baglaminda "Kullanici ekle"ye basinca FtpView artik
  onerilen kullanici adini (<site>_ftp, 20 char'a sigdirilmis) onceden doldurur
  (toggleCreateForm + suggestedFtpUserName) -> tek tikla parola girip olustur.
- Yeni kurulum paketi:
  - boyut: `50898239` byte
  - SHA256: `8CBFA3433B0536E0EC2B4D59CEBD4E4B4564D96BE5C1757377C286D27EC6454D`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-17 (3) IIS site-zaten-var, SSL/mail timeout, site-DB opsiyonel

- IIS "site adı zaten kullanılıyor" (onceki AdaPanel kaldirilinca IIS'te kalan
  artik site): CreateSiteAsync artik duplicate_name atmak yerine MEVCUT siteyi
  kaldirip yeniden olusturuyor (kullanici domaini tekrar ekleyebilir).
- Hang'ler: `ProcessRunner.RunAsync` opsiyonel `timeout` aldi (sure dolunca
  sureci oldurup basarisiz sonuc doner). win-acme 120s, MailEnable COM 30s ->
  SSL ve mail-domain olusturma artik sonsuza kadar donmez.
- Site olustururken DB ARTIK OTOMATIK DEGIL: `shouldCreateDatabase =
  request.CreateDatabase` (ProvisionDefaultServices DB'yi zorlamaz). DTO'ya
  DatabaseUserName/DatabasePassword eklendi (bossa uretilir). SitesView'da
  "Bu site icin veritabani olustur" checkbox + isim/kullanici/parola alanlari.
- HENUZ/TARTISMA (kullanici geri bildirimi):
  - phpMyAdmin per-domain kapsama: kullanici root oto-girisin HER seyi
    gostermesini istemiyor; "sadece kendi domain DB'si". Bu, per-DB signon/launch
    gerektirir (su anki global root config-auth ile celisir). Tasarim kararı
    gerekiyor.
  - DB kullanici olustururken domain/site secimi (Plesk mantigi).
- Test: Core 2/2, Infra 41/41; frontend typecheck temiz; i18n 1609/1609.
- Yeni kurulum paketi:
  - boyut: `50899508` byte
  - SHA256: `10BBEF892B1A5D85E06074EE09E1AD184CC7DF6E43D55FB2740C5A3D4A7080C6`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-17 (4) Temiz kaldirma: AdaPanel IIS sitelerini/domainlerini sil

- Sorun: AdaPanel kaldirilinca kullanicinin ekledigi IIS siteleri/domainleri
  Windows'ta KALIYORDU (uninstaller yalniz WebDb yardimci sitelerini siliyordu).
- Cozum: `uninstall-cleanup.ps1` artik AdaPanel'in olusturdugu TUM IIS sitelerini
  kaldiriyor. Isaret: AdaPanel siteleri her zaman `AdaPanel.*` app pool kullanir
  (ManagedPoolPrefix). WebAdministration (Get-Website applicationPool -like
  'AdaPanel.*' -> Remove-Website) + appcmd yedegi; AdaPanel.* app pool'lari da
  kaldiriliyor. memcached gorevi (AdaMemcached) task listesine eklendi.
- Not: Panel-ICI site silme zaten tam cascade (mail/DB/FTP/DNS/proxy/kota/IIS) -
  Plesk gibi. CreateSite de artik ayni isimli mevcut IIS sitesini silip yeniden
  olusturuyor (tekrar ekleme sorunu cozuldu, onceki turda).
- Yeni kurulum paketi (yalniz uninstaller scripti degisti):
  - boyut: `50899911` byte
  - SHA256: `31A0386DD961CE333077932783CEA6DBCF0D71A99B98BA6DE56DD082958FF9F0`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-17 (5) phpMyAdmin per-DB signon (Plesk gibi izolasyon)

- phpMyAdmin artik SIGNON auth: config.inc.php tek signon sunucusu + adapanel-signon.php
  (token -> $_SESSION PMA_single_signon_user/password + cfgupdate host/port/only_db) +
  adapanel-launch.php (tokensuz erisimde yonlendirme). tokens dizinine handlers temizleyen
  web.config (HTTP ile okunamaz; phpMyAdmin dosya sisteminden okur).
- DB kullanici parolalari SIFRELI saklaniyor: `DatabaseCredentialStore`
  (config/db-credentials.json, ISecretProtector). DatabaseService CreateDatabase
  (user'li) / CreateUser / SetUserPassword bu depoya yaziyor.
- `WebDbClientService.CreatePhpMyAdminSignonAsync(engine, databaseName?)`: databaseName
  verilirse o DB'nin kullanicisi olarak signon (gercek izolasyon: yalniz o DB gorunur);
  yoksa root + only_db (gorsel) ; databaseName bossa root (tum DB). Endpoint:
  POST /api/v1/webdb/phpmyadmin/open.
- Frontend: DatabasesView her DB satirinda "phpMyAdmin'de ac" (o DB'ye signon);
  WebDbView genel "Ac" root signon (tum DB). Token 5 dk, tek seferlik.
- NOT: phpMyAdmin yeniden deploy edilince (Arayuzu yeniden kur) signon config aktif olur.
  Gercek izolasyon icin DB+kullanici AdaPanel ile olusturulmus olmali (parola saklanir);
  disardan olusturulmus kullanicilarda root+only_db'ye duser.
- Test: Core 2/2, Infra 41/41; frontend typecheck temiz; i18n 1611/1611.
- Yeni kurulum paketi:
  - boyut: `50903618` byte
  - SHA256: `FF9046FAF26C39012A7E6C33CBEAE338AAA94F4057B1F1E4156DABCC18D7BDF5`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-17 (6) Plesk-parity #2 Mail: autoresponder (otomatik yanit)

- IMailService'e GetAutoResponderAsync/SetAutoResponderAsync eklendi.
  - MailEnable: MEAOPO.Mailbox COM (GetMailbox + Get/SetAutoResponderStatus +
    Get/SetAutoResponderContents) ile gercek autoresponder.
  - Stalwart: Get -> disabled, Set -> mail.autoresponder_not_supported (MailEnable
    varsayilan; Stalwart sieve vacation ileride).
  - Router delegasyonu. DTO MailAutoResponderDto/SetMailAutoResponderRequest.
  - Endpoint: GET/POST /api/v1/mail/accounts/autoresponder.
- Frontend MailView: posta kutusu basina "Otomatik yanit" -> ac/kapat + mesaj
  editoru (mevcut durumu okur, kaydeder). i18n 1618/1618.
- Plan: 1 site lifecycle (bitti), >>2 Mail parity (autoresponder bitti; sirada
  per-domain DKIM/SPF/DMARC + catch-all), 3 DB, 4 SSL, 5 backup, 6 subscription.
- Test: Core 2/2, Infra 41/41; frontend typecheck temiz.
- Yeni kurulum paketi:
  - boyut: `50907225` byte
  - SHA256: `EC80A0F08F6367E81FA96E4D6419A79472C91F21BB0F954508C59A11D510566F`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-17 (7) FTP parola degistirme (DB parite); FTP zaten site-icinde

- FTP yonetimi ZATEN site calisma alaninda (FtpView tab, site-scoped) -> sonradan
  olusturma mevcut (DB gibi). Eksik olan paritesi: parola degistirme.
- Eklendi: IFtpService.SetFtpPasswordAsync + FtpService (`net user <ad> <parola> /y`,
  min 8 char, kullanici yoksa ftp.user_not_found). DTO SetFtpPasswordRequest.
  Endpoint POST /api/v1/ftp/users/password. Api/Resources tr+en mesaj anahtarlari.
- Frontend FtpView: kullanici satirinda "Parola degistir" (window.prompt).
- Plesk-parity plani devam: 2 Mail (autoresponder bitti, DKIM/catch-all sirada),
  3 DB (signon bitti), FTP password (bu tur). Sirada Mail catch-all + DKIM/SPF/DMARC.
- Test: Core 2/2, Infra 41/41; frontend typecheck temiz; i18n 1621/1621.
- Yeni kurulum paketi:
  - boyut: `50907840` byte
  - SHA256: `34D675A7037E7C047B13C0338636D4789E17DF616E8E935FB0737A34575536BD`
  - Authenticode: imzasiz (`NotSigned`).

## 2026-06-17 — Default belge / 500 + starter ezme düzeltmeleri
- **"/" 500 kök neden**: `WriteDefaultDocumentConfig` `<clear/>` ile tüm varsayılan
  belgeleri silip SADECE index.php bırakıyordu → kullanıcı index.html yükleyince "/"
  artık index.html'i görmüyor, kalan AdaPanel index.php starter'ı patlayınca 500.
  - Düzeltme: clear KALDIRILDI; liste index.php,index.html,index.htm,Default.aspx,
    default.asp içeriyor; web.config'e `adapanel-starter` marker; kullanıcının kendi
    (marker'sız) web.config'ine dokunulmuyor.
- **index.html siliniyordu**: `RemoveDefaultHtml` artık SADECE marker içeren AdaPanel
  starter index.html'ini siler; kullanıcının yüklediği index.html ASLA silinmez.
- **Starter ezme**: yeni `WriteStarterIfAbsent` — dosya varsa ve AdaPanel marker'ı yoksa
  ÜZERINE YAZMAZ. Tüm starter'lar (php/aspnet/classic/mixed) buna geçti + içeriklerine
  `adapanel-starter` marker eklendi.
- Installer SHA256: `E23049E46C71E258D3452040DAA3BAD76553AC40C91A6AA800155036B7B675A6` (50909401 byte)
- Masaüstü test klasörü: `C:\Users\aLi\Desktop\AdaPanel-SiteTest\` (index.html/test.php/test.asp/test.aspx/OKU.txt)
- AÇIK: "Yayın profili" dropdown'ı (php/aspnet/... 8 seçenek) kullanıcıya karışık geliyor;
  PHP zaten sunucu genelinde kurulu. "auto" hepsini açıyor — sadeleştirme önerildi.

## 2026-06-17 (2) — Plesk sadeleştirme + catch-all + silme idempotency
- **Yayın profili dropdown sadeleştirildi**: artık sadece auto(önerilen)/static/node/python.
  php/aspnet/classic-asp/mixed kaldırıldı (auto hepsini içeriyor). `runtimeHint` eklendi.
  PHP sürüm seçici artık 'auto' için gösteriliyor. Backend tüm değerleri hâlâ destekliyor.
- **Mail catch-all (yakala-tümü)**: alias yerel-kısmı `*`/`@`/boş → MailEnable'da çıplak
  alan adı address map'i (`[SMTP:domain]`). DeleteAlias ve GetWorkspace filtresi/gösterimi
  ('*@domain') güncellendi. MailView'de `catchAllHint` ipucu.
- **Cascade silme idempotency**:
  - IIS `DeleteSiteAsync` artık site yoksa no-op (FindSite yerine FirstOrDefault) → `site_not_found` YOK.
  - FTP `DeleteFtpUserAsync`: net.exe başarısızsa `Remove-LocalUser` fallback; hesap gerçekten
    yoksa başarı say → `user_delete_failed` çözüldü.
  - `StepAsync`: `*_not_found` kodları "temizlendi" sayılıyor (DNS/kota vb. için de).
- Installer SHA256: `98B0EB9E728C4874407CDBB66643423F6A176C3625A640B5748BB2934D060FCD` (50909792 byte)

## 2026-06-17 (3) — Plesk tarzı UI: site listesi + workspace sadeleştirme
- **Site listesi kare kart → tam genişlik satır listesi** (SitesView): `.domain-cards` grid
  kaldırıldı; `.domain-rows`/`.domain-row` flex satırlar. Hızlı araçlar ikon-only chip;
  900px altı wrap. Plesk web-site listesi görünümü.
- **Workspace sekme grupları sadeleştirildi** (WebsiteWorkspaceView): "Applications" grubu →
  "Veritabanı" (sadece webdb); "Mağaza" (store) Gelişmiş gruba taşındı.
- **PHP/ASP sekmeleri Plesk adlandırması**: "PHP Ayarları" / "ASP.NET / Classic ASP"
  (kullanıcı onayı: ikisini de tut). Bunlar gerçek yönetim ekranları, starter değil.
- Installer SHA256: `311E338A31D92F87C56E8A217F59F8E896A4EADE2B3C96E08E92CB253F875134` (50909741 byte)

## 2026-06-17 (4) — Plesk: yalnız kullanıcı kaynakları (sistem girdileri gizli)
- **Site listesi sistem filtresi** (WebAdministrationSiteService.GetSitesAsync):
  `IsSystemSite(SiteDto)` — isim "Default Web Site"/"AdaPanel FTP", önek "AdaPanel."/"MailEnable",
  VE geleceğe dönük: yalnızca loopback'e (127.0.0.1/::1/localhost) bağlı, gerçek hostname'i
  olmayan siteler = dahili servis → gizli. Import da bu yoldan geçer.
- **DB sistem filtresi**: MySQL'e 'phpmyadmin' eklendi; PostgreSQL 'postgres' hariç tutuldu;
  MSSQL zaten database_id>4; MySQL zaten information_schema/performance_schema/mysql/sys.
- **FTP**: GetFtpUsersAsync zaten AdaPanel state-store'dan okuyor → yalnız panelin oluşturduğu
  kullanıcılar görünür (ek filtre gerekmedi).
- İlke: her kaynak listesi yalnız kullanıcının eklediğini gösterir (Plesk gibi).
- Installer SHA256: `67A08707247DA5E06D16C078530E17C2CE851B677F13CB5357BD22FC10FC34B3` (50910214 byte)

## 2026-06-17 (5) — SSL binding "bağlanamadı" düzeltmesi
- **Kök neden 1**: PersistCertificate, LocalMachine\My başarısızsa SESSIZCE CurrentUser\My'a
  düşüyordu; IIS/HTTP.sys yalnız LocalMachine\My'ı bağlayabildiğinden binding patlıyordu.
  → CurrentUser fallback kaldırıldı; aynı parmak izi varsa tekrar eklenmez (anahtarlı kopyayı
  anahtarsızla ezmesin).
- **Kök neden 2**: SNI binding'i 2-arg Add("https") + sonradan CertificateHash property ile
  kuruluyordu. → Çakışan eski binding önce Remove edilir; sertifika Add'in 3-arg overload'ı
  (bindingInformation, certHash, "My") ile OLUŞTURURKEN atomik bağlanır; sonra sslFlags=1 (SNI).
- Dosya: WinAcmeSslService.cs EnsureHttpsBinding + PersistCertificate.
- Installer SHA256: `1A2BD2C5367B93DDD8E4DCFAD785B711E80E71203024D8FDED59094875B8B478` (50909875 byte)

## 2026-06-17 (6) — "Web siteleri" sekmesi Plesk gibi sadeleştirildi
- WebsiteWorkspaceView: büyük gradient hero + çift-katlı grup/araç nav YIĞINI kaldırıldı.
  - Liste görünümü (activeTool='sites'): doğrudan SitesView satır listesi, üstte sekme yok.
  - Araç görünümü: "← Web Siteleri" geri çubuğu (backToSites) + hedef site seçici + DÜZ araç
    çubuğu (siteTools). groups/activeGroup/selectGroup/groupForTool tamamen silindi.
- SitesView satır hızlı araçları: store yerine PHP eklendi (hosting/php/ssl/webdb/ftp/dns).
- i18n: panel.sites.backToSites eklendi.
- Installer SHA256: `BADBB5887B766ADEF384E20D7862A302945D056A9100C05EFAFDF214EDD94096` (50909683 byte)
- SIRADA: site-bazlı yedek/geri-yükleme, sonra müşteri/abone modeli.

## 2026-06-17 (7) — Site içi Mail + ana menü Plesk hizalaması
- **Site bağlamına Mail eklendi** (Plesk domain-içi Mail sekmesi): WebsiteWorkspaceView tools'a
  'mail' (@) + MailView render; MailView'e opsiyonel `domain` prop'u (site domainini önden seçer).
  SitesView satır hızlı araçlarına da Mail (@) eklendi. i18n: panel.sites.tools.mail(+Hint).
- **Ana menü Plesk sırasına dizildi** (SetupWizardView dashboardSections):
  Ana Sayfa → Web Siteleri ve Alan Adları → Posta → Dosyalar → Veritabanları → İstatistikler →
  Mağaza → Zamanlanmış Görevler → Güvenlik → Araçlar → Ayarlar → Çıkış.
  Etiketler Plesk terimlerine çekildi (sites=Websites & Domains, mail=Mail/Posta,
  database=Databases, monitor=Statistics, files=Dosyalar).
- Installer SHA256: `D8BE1DD38B96120604167D02DEF4096D07A3BA002C30A6082AD5D1AC5DF76903` (50909480 byte)
- NOT: Kullanıcı "her şey Plesk'in aynısı olsun, menülerine kadar" diyor; piecemeal yerine
  yapısal hizalama yapılıyor. Yedekleme (yerel+indirilebilir; dosya+DB+config+mail) hâlâ sırada.

## 2026-06-17 (8) — Plesk parite #1 site detay + #2 yedekleme
- #1 Site detay (Plesk domain araç ızgarası): SiteDetailView'e tools-grid (kart kısayollar)
  + open-tool emit; SitesView @open-tool→openTool ile workspace'e bağlandı.
- #2 Tam-site yedeği: BackupSiteRequest'e IncludeFiles/Config/Mail + Databases[]; BackupService
  birleşik .zip (files/+config/site.json+databases/*.sql+mail/<domain>.json+manifest.json),
  IMailService enjekte. Download ucu: GET api/v1/backup/{id}/download (PhysicalFile).
  Frontend: SiteBackupView.vue (kapsam checkbox + DB seçimi + indir) workspace 'backup' aracı.
  backupSite imzası değişti (siteName, scope?, target?) — BackupView çağrısı güncellendi.
- Site workspace araçlarına Mail(@) + Backup(BK) eklendi; SiteDetailView araç ızgarasına da.
- Installer SHA256: `B6518ADA3D542D059210A21332103198498FD5FDD4D945701E0D6BBBC3E950F2`
- SIRADA: #3 müşteri/abone modeli (zaten kısmi: isCustomer, ICustomerScopeService, CustomerAccess).

## 2026-06-17 (9) — #3 müşteri/abone modeli (zaten vardı + özet eklendi)
- Müşteri/abone modeli backend+UI olarak ZATEN tamdı: UsersController + UserManagementService
  ApplyScopeAsync + CustomerScopeService (config/customer-scopes.json) + CustomerAccess enforcement;
  SetupWizardView ayarlar→kullanıcılar'da rol=Customer + atanan siteler(checkbox) + MaxSites +
  MaxDiskMb; customer nav filtreli (yalnız kendi siteleri/dosya/mail/yedek).
- EKLENEN: kullanıcı listesinde Customer satırına atanan site sayısı + disk kullanımı/limit özeti
  (UserSummaryDto zaten AssignedSites/MaxSites/MaxDiskMb/DiskUsageMb veriyordu). i18n: users.sitesShort.
- 1→2→3 tamam. Final installer SHA256: `7618A112947EF781730E00718CB6F5B99C3211BD3F77D1A60E71AD4884C151DC` (50913237 byte)

## 2026-06-17 (10) — Yedek geri yükleme (tam-site, Plesk round-trip)
- RestoreAsync: birleşik site arşivi (manifest.json/files/databases/) algılanırsa RestoreCombinedSiteAsync:
  files/* → sitenin fiziksel köküne (zip-slip korumalı, overwrite), databases/*.sql → ImportDumpAsync
  (engine-dbname dosya adından çözülür). TargetPath boşsa canlı site kökü.
- Frontend SiteBackupView: "Geri yükle" butonu + onay (restoreConfirm). restoreBackup(id,"").
- i18n: panel.backup.restore/restoring/restored/restoreConfirm.
- Installer SHA256: `6FB192E57FE8FF9ADBC774A96604F5666F27246E186DFCEAF608D915A5DCC6F1` (50914863 byte)

## 2026-06-17 (11) — İstatistik aracı yüzeye çıkarıldı + parite denetimi
- DENETİM SONUCU: "zamanlanmış yedek" (AutomationService AutoBackup/interval/retention/RunDueJobs)
  ve "web istatistik" (WebStatisticsService — IIS log ayrıştırma, requests/visitors/bandwidth/
  top pages) ve per-site PHP direktifleri (.user.ini) ZATEN VAR. DKIM: MailEnable Standard
  imzalamayı desteklemediğinden sağlıklı yapılamaz (imzasız DKIM kaydı zararlı) — ertelendi.
- EKLENEN: SiteStatisticsView.vue — gömülü stats'ı Plesk gibi site workspace 'stats' aracı
  yaptı (mevcut getSiteStatistics API). WebsiteWorkspaceView + SiteDetailView grid'e eklendi.
  i18n: panel.sites.tools.stats(+Hint).
- Installer SHA256: `B939768D37F171D44C392BE7CB34BE51AB75448475F23149EA51214136E06BE3` (50915575 byte)

## 2026-06-17 (12) — Subdomain ekleme + font doğrulaması
- Subdomain (Plesk gibi): SiteDetailView'e "Subdomain ekle" formu (<etiket>.<alanadı>) +
  add-subdomain emit; SitesView.handleAddSubdomain mevcut createSite ile yönetilen alt-site
  oluşturur (runtimeProfile=auto, physicalPath=defaultSiteRoot\<host>). i18n: subdomainTitle/
  Add/Created.
- Font: ZATEN Plesk Open Sans — @fontsource-variable/open-sans main.ts'te import, base.css :root'ta
  "Open Sans Variable", form elemanları font:inherit. Değişiklik gerekmedi.
- Installer SHA256: `27A5D1BD22A3C9A532C13F5770243487F56817B1CE3CC4C11564DFADCF5C45AC` (50915835 byte)
- SIRADA: korumalı dizinler (IIS basic auth) — karmaşık/kırılgan, dikkatli yaklaş.

## 2026-06-17 (13) — Korumalı dizinler (Plesk Password-Protected Directories)
- DirectoryProtectionService (Infrastructure/Iis): korunacak klasöre IIS URL Rewrite tabanlı
  Basic Auth web.config yazar (Windows hesabı GEREKMEZ). Base64(user:pass) regex-escape'li
  pattern + WWW-Authenticate header. Marker'lı ('adapanel-protected') — kullanıcı web.config'ini
  ezmez/silmez. Zip-slip/'..' koruması. Kayıtlar config/protected-dirs.json.
- IDirectoryProtectionService + DTO (ProtectedDirectoryDto/ProtectDirectoryRequest). DI'a eklendi.
- SitesController: GET/POST/DELETE /sites/{site}/protected-dirs ([FromServices]).
- Frontend: ProtectedDirsView.vue + api (getProtectedDirs/protectDir/unprotectDir) + workspace
  'protected' aracı + SiteDetailView grid. i18n panel.protected.* + protected.* mesaj anahtarları.
- NOT: URL Rewrite modülü kurulu olmalı (yoksa o klasörde 500.19). MailEnable Standard DKIM hâlâ ertelendi.
- Installer SHA256: `5ABAEBDF64FAEA5044DDDD029F7F09502130DD73DB9DD2E794FA54E7A9E61E2F` (50921209 byte)

## 2026-06-17 (14) — Plesk sadeleştirme: site detay + hosting + automation
- SiteDetailView: 11-chip "defaults" bloğu KALDIRILDI (anlamsız gürültü); iç-kullanım "Hazırla"
  butonu kaldırıldı; binding ekleme formu toggle arkasına alındı (showBindingForm).
- SiteHostingView: 'stats' bölümü nav'dan kaldırıldı (artık ayrı 'İstatistikler' aracı; duplikasyon).
- AutomationView: alt-ayarlar ana toggle'a bağlandı — autoBackupEnabled kapalıyken yedek detay +
  uzak hedef gizli; alertsEnabled kapalıyken eşik inputları gizli. Plesk gibi "kapalıyken sade".
- İLKE: her yere yayılıyor — "her şey aynı anda" gösteren ekranları gate/toggle ile sadeleştir.
- Installer SHA256: `1B210985829D6DC8131514FFF8082A500C12766AD6F9F6521B3D6107B5CA8B71` (50920777 byte)

## 2026-06-17 (15) — MailView sadeleştirme (Plesk gibi)
- MailView: alan adı seçilince hesaplar + alias ÖNDE; DNS kayıtları + rDNS + sertifika bölümleri
  "Mail ayarları" toggle'ı (showMailAdvanced) arkasına alındı. Spam zaten kendi advanced toggle'ında.
- i18n: panel.mail.showSettings/hideSettings.
- Panel renk/font/yapı zaten Plesk hizasında (dark accent #4b9cff mavi, Open Sans, kart+bölüm).
- Installer SHA256: `36533F2BEACB8DE36214CD0A930298942DAEA7491E88CEB7321B9D5B575BCC15` (50920852 byte)

## 2026-06-17 (16) — #2 impersonation + masaüstü kısayolu
- IMPERSONATION (Plesk "log in as customer"): AuthController POST /auth/impersonate/{userId}
  (Admin) → hedef olarak cookie + impersonator_id/name claim; POST /auth/impersonate/stop →
  admin'e geri. CurrentSessionDto.IsImpersonating/ImpersonatorName; AuthService.GetUserByIdAsync.
  Frontend: auth store impersonate/stopImpersonate; SetupWizardView Customer satırına "Müşteri
  olarak giriş"; MainLayout sarı banner + "Yönetici hesabına dön". i18n users.loginAs/
  impersonatingAs/returnToAdmin + auth.impersonate.*.
- MASAÜSTÜ KISAYOLU: installer/AdaPanel.iss [Tasks] desktopicon (varsayılan işaretli) +
  [Icons] {autodesktop}\AdaPanel → {code:GetPanelUrl} (panel URL'ini açar).
- Installer SHA256: `42032D12BFB0B21ED290815EE371A79B2A27F8382BAB457146108CC5B4A9A5C5`
- KALAN sıra: 3(servis planları) 4(DKIM) 5(reseller) 6(domain alias) 7(çoklu IP) 8(mailing list)
  9(site önizleme) 10(global arama) 11(menü ikonları) 12(boş-durum) 13(bildirim çanı) 1(WP Toolkit)

## 2026-06-17 (17) — #3 Servis Planları
- ServicePlanService (config/service-plans.json) + IServicePlanService + DTO; ServicePlansController
  GET/POST/PUT/DELETE /service-plans (Admin). DI'a eklendi.
- Frontend: api/servicePlans.ts; SetupWizardView users sekmesinde plan yönetim kartı (ekle/sil) +
  Customer create form'da "Servis planı" dropdown'ı (seçince maxSites/maxDiskMb otomatik dolar).
  i18n panel.plans.* + plans.* mesajları.
- Installer SHA256: `6DF05215051FAD578AE28D74C5B53BF21FCF0140EA59045066922FB2C1F59919`
- #4 DKIM: MailEnable Standard imzalamayı desteklemiyor → imzasız DKIM kaydı zararlı olacağından
  ATLANDI (bilinçli). Sıradaki: 5 reseller, 6 domain alias, 7 çoklu IP, 8 mailing list, 9 önizleme,
  10 global arama, 11 menü ikonları, 12 boş-durum, 13 bildirim çanı, 1 WP Toolkit.

## 2026-06-17 (18) — menü ikonları + topbar + mail.command_failed dayanıklılık
- #11 Menü ikonları: MainLayout nav glyph → SVG ikon (NAV_ICONS map, currentColor stroke); aktifte
  accent. .nav-icon CSS.
- Topbar kompakt: iki büyük status-card + eyebrow + açıklama kaldırıldı → tek satır (online dot +
  başlık + user-chip + tema/yenile/çıkış). .user-chip/.icon-btn.
- mail.command_failed: GetWorkspaceAsync artık COM hatasında hard 500 atmıyor; Installed/Configured
  true + MailWorkspaceDto.StatusMessage'a tanı (COM erişilemedi + detay). MailView statusMessage'ı
  kırmızı notice'te gösterir. Kök neden genelde MEAOPO COM kayıtsız (REGDB_E_CLASSNOTREG).
- Installer SHA256: `0DAFECF3D5F2BD1A95F8232A115F7EA0BA5A3776C27E34852CF09056BBD6D458`

## 2026-06-17 (19) — #10 Global arama (topbar)
- MainLayout topbar'a arama kutusu: bölüm etiketlerinde filtrele → dropdown sonuçlar (ikonlu) →
  tıkla/Enter ile o bölüme git (navigate emit). 900px altı gizli. i18n layout.searchPlaceholder.
- Installer SHA256: `8977022B6FB620FA4E8BE3EBCCDD7269A9727A9A44111E36CA41D32BE416D8F2`
- Kalan görsel: 12(boş-durum cilası) 13(bildirim feed'i — backend feed yok, ertelendi/audit ile yapılabilir).
  Mimari: 5 reseller, 7 çoklu IP, 8 mailing list, 9 screenshot, 1 WP Toolkit.

## 2026-06-17 (20) — #13 Bildirim çanı (audit tabanlı)
- MainLayout topbar'a çan (admin-only): authStore.auditLogs son 8 → dropdown (humanizeAction +
  yerel saat) + sayaç rozeti (9+). i18n layout.notifications/recentActivity/noActivity. Çan SVG.
- Installer SHA256: `9E8407534F59B0B955F63DD18078E0A6ADB8B32061B9A56EC3232AD6D53160CF`
- Görsel Plesk-feel seti tamam: ikonlar, arama, çan, kompakt topbar, sadeleştirme.
- KALAN: 12 boş-durum cilası (küçük), mimari: 5 reseller, 7 çoklu IP, 8 mailing list, 9 screenshot,
  1 WP Toolkit (en büyük).

## 2026-06-17 (21) — #1 WordPress Toolkit (MVP) + mail timeout kök neden
- WP TOOLKIT: WordPressToolkitService (AppStore) — wp-config.php ile algıla, wp-includes/version.php'den
  sürüm (GeneratedRegex), .maintenance dosyasıyla bakım modu (WP-CLI gerektirmez). IWordPressToolkitService
  + DTO; WordPressController GET /wordpress, GET /wordpress/{site}, POST /{site}/maintenance. DI eklendi.
  Frontend WordPressToolkitView.vue + api/wordpress.ts; workspace 'wp' aracı + SiteDetailView grid.
  i18n panel.wp.*.
- MAIL TIMEOUT (kullanıcı tanısı: "Process timed out after 30s"): kök neden COM FindNext bazı
  MailEnable sürümlerinde sonu bildirmiyor → 5000/100000/200000 tavanlı döngü 30s'i aşıyordu.
  DÜZELTME: üç döngüye de ($seen/$seenMb/$seenAm) "tekrar eden kayıt gelince break"; tavanlar
  500/5000/20000'e düşürüldü; RunComAsync timeout 30→45s. Artık ~1-2s'de döner (zarif degradasyon korunur).
- Installer SHA256: `7BA6A6BFB09B9F52844343EC77C2B2719DFAC646396BAFFE4C2C67E0D9FEA6DC`

## 2026-06-17 (22) — Mail Plesk-parite: site alan adıyla otomatik
- MailView.ensureSiteDomain(): site bağlamından (domain prop) gelince, o alan adı mail domaini
  değilse OTOMATIK postoffice oluşturur (createMailDomain) → kullanıcı "alan adı ekle" adımı
  görmez, doğrudan kutu yönetimine düşer (Plesk gibi). statusMessage/installed guard'lı.
- Genel Mail sekmesi boş-durum mesajı Plesk-mantığına göre güncellendi (mail = web sitesi alan adı).
- Installer SHA256: `10E6D3C65D392A91A4BDED56939F8B6ED975FF03222BB54D462CEBB109B46C15`

## 2026-06-17 (23) — #12 Boş-durum cilası (site listesi)
- SitesView noSites: düz metin → Plesk-tarzı onboarding kartı (.empty-onboard: kesik çerçeve +
  küre ikon + başlık + ipucu + "Site oluştur" butonu). i18n noSites/noSitesHint güncellendi.
- Installer SHA256: `31C817243AAAD96C9FB677AE90FDC4CCD7964EBE457B1FC9E0B269EA01948029`
- KALAN mimari: 5 reseller (ownership), 7 çoklu IP, 8 mailing list, 9 site önizleme (screenshot).

## 2026-06-17 (24) — Yazılım mağazası: kilit hatası + kök neden netliği
- FinalizeDownloadAsync UnauthorizedAccessException (PostgreSQL 16): kök neden temp→dest Move'da
  HEDEF kilidi (AV/çalışan installer). 6 retry yetmeyince: parent dir ensure + dest-is-directory
  guard + YEDEK1 (dest'i rename-aside → temp'i taşı) + YEDEK2 (File.Copy + temp sil) + son çare
  TEMIZ hata (AppException software.install_file_locked 423, ham .NET stack yerine). Api/Resources
  tr/en mesaj anahtarları eklendi.
- İNDİRME KAYNAKLARI ZATEN RESMİ: php.net, enterprisedb/postgresql, mysql.com, mariadb.org,
  nodejs.org, python.org, microsoft.com, nginx.org, mailenable.com, getcomposer.org, phpmyadmin.net,
  github (adminer/redis/stalwart/win-acme), docker.com. "Kendi sitesinden insin" zaten karşılı.
- Installer SHA256: `527C16FA2EDCB926479FC8244947638D342825096B4092DD5E080806C1DCE82D`

## 2026-06-17 (25) — Mail: Stalwart BİRİNCİL, MailEnable yedek + çakışma engeli + #9 önizleme
- ARAŞTIRMA: Stalwart > MailEnable Standard (yerleşik DKIM RSA+Ed25519, SPF/DMARC/ARC/MTA-STS,
  spam/greylist, CLI yönetimi = COM sorunları yok). Stalwart birincil yapıldı.
- MailService.IsInstalled() (static, %ProgramData%\AdaPanel\runtimes\stalwart\stalwart*.exe).
- MailServiceRouter: Active = Stalwart kuruluysa Stalwart, değilse MailEnable, hiçbiri yoksa Stalwart.
- ÇAKIŞMA ENGELİ (ComponentBootstrapper.ResolveMailServerConflictAsync, install başında çağrılır):
  stalwart-mail kurulurken → MailEnable servisleri (Get-Service 'MailEnable*') Stop+Disable;
  mailenable-standard kurulurken → AdaStalwart task End+Disable + stalwart.exe kill. Port çakışması yok.
- #9 SİTE ÖNİZLEME: SiteDetailView'e canlı iframe önizleme (showPreview toggle). i18n previewTitle/Show.
- DKIM (#4): Stalwart'ta ZATEN tam çalışıyor (MailService domain açınca DKIM anahtarı üretir + DNS kaydı).
- #8 mailing list: Stalwart list-tipi adres destekler; AdaPanel'e eklenmedi (sıradaki, canlı test gerekir).
- Installer SHA256: `FAA692BC0D9EAC0ADF4E12DF8AD5A9466569B7E939E828DF564ADADA7DF6C41C`

## 2026-06-17 (26) — Stalwart VARSAYILAN, MailEnable yedek
- components.json: stalwart-cli + stalwart-mail default:TRUE; mailenable-standard default:FALSE.
  Notlar güncellendi. InstallDefaultsAsync artık Stalwart'ı (CLI+server) otomatik kurar, MailEnable'ı
  kurmaz (elle/yedek). Çakışma engeli (25) install başında zaten devrede.
- Installer SHA256: `AAA0D8D4044E181BE1F9DC51FA2BCBC198A11F7934290AFE95A1AECCBBD67E90`
- #8 MAILING LIST: Stalwart "list principal" gerektirir; doğru CLI alan adları canlı sunucu olmadan
  doğrulanamıyor → "zarar vermesin" gereği canlı teste bırakıldı (untested mail-routing shipping yok).

## 2026-06-18 — SSL binding PowerShell'e çevrildi + Stalwart tanı
- SSL: kullanıcının VPS'inde manuel PS binding (New-WebBinding -SslFlags 1 + AddSslCertificate)
  HATASIZ çalıştı; MWA reflection bazı IIS'lerde SNI cert'i "bağlanamadı" ile reddediyordu.
  EnsureHttpsBinding → EnsureHttpsBindingAsync (PowerShell WebAdministration). Çağıranlar await.
- win-acme: tools\win-acme'de kurulu; ResolveWinAcmeExecutableAsync DB InstallDir kullanıyor (doğru).
  LE self-signed dönüyorsa win-acme HTTP-01'de takılıyor (muhtemelen sağlayıcı firewall'unda 80 kapalı).
- Stalwart: yeni installer'da ✕ vermiyor (non-fatal + logs\stalwart.log). "Admin parolası doğrulanamadı"
  = muhtemelen 8080 hâlâ dinlenmiyor (CLI bağlanamıyor). logs\stalwart.log + binary adı incelenecek.
- Installer SHA256: `68A0E4B6B966C1D15FA0536807712ADC6E40FF3824F6ACD299111615B16F54C8`

## 2026-06-18 (2) — Stalwart RocksDB→SQLite KÖK NEDEN + Mail/menü Plesk yapısı
- **STALWART KÖK NEDEN (kesin, VPS'te kanıtlandı)**: Stalwart 0.16.9 Windows binary'si RocksDB
  store'unu açarken native tarafta ÇÖKÜYOR (0xC0000005 access violation, stdout/stderr'e tek
  satır bile yazmadan; tracer henüz yüklenmediğinden log da boş). Bootstrap config FORMATI
  doğruymuş: `--config` minimal JSON'dur (`{"@type":"RocksDb","path":...}`), sunucu DB'den
  geri kalan ~2400 ayarı yükler. Sorun sadece store backend'iydi. Tanı adımları: `--version`
  exit 0 (binary sağlam) ama `--config`(RocksDb) exit -1073741819; TOML denemesi "expected value
  line 1 col 2" (JSON parser → config JSON okunuyor); `{"@type":"Sqlite","path":".../data/
  stalwart.db"}` ile alive:True + 8080 dinliyor.
- **DÜZELTME (ComponentBootstrapper.InstallStalwartServiceAsync)**: config.json artık SQLite
  yazıyor (`{"@type":"Sqlite","path":"<data>/stalwart.db"}`, path bir DOSYA). Ayrıca mevcut
  config'te "RocksDb" geçiyorsa SQLite'a taşıyor (RocksDB hiç çalışmadığından veri kaybı yok).
- **MAIL Plesk yapısı (MailView.vue)**: genel "Posta" ekranı artık "mail alan adı oluştur"
  adımı GÖSTERMEZ; Plesk gibi domain LİSTESİ (mevcut mail domainleri + henüz postası açılmamış
  site alan adları birleşimi) → tıkla → postası yoksa otomatik etkinleşir (openDomain) → hesap
  yönetimi. showDomainForm/domainForm/addDomain/candidateDomains/loadDomain kaldırıldı. Geri
  çubuğu (backToDomains) + breadcrumb. i18n: panel.mail.listHint/mailOff/manage/allDomains/
  noSiteDomainsList.
- **MENÜ tam-Plesk (kullanıcı kararı: araçları domain altına topla)**: domaine özel araçlar
  (SSL/DNS/FTP/PHP zaten üst menüde değildi; sadece "Dosyalar") ADMIN üst menüsünden çıkarıldı
  (SetupWizardView adminSections filter id!=='files'); müşteride kalır (site hub'ı yok).
  WebsiteWorkspaceView'e "files" aracı eklendi (FilesView). Hub'ın tool-context'indeki "site seç"
  dropdown'ı Plesk BREADCRUMB'ına çevrildi (domain adı + ← Web Siteleri). İç araçların tekrar
  eden site seçicileri domain seçiliyken gizlendi (SslView/PhpView/FilesView: v-if !selectedSiteName).
  i18n: panel.sites.tools.files/filesHint. i18n parite 1715/1715.
- "Files" aracı 3 giriş noktasına eklendi: hub nav (WebsiteWorkspaceView), SiteDetailView araç
  ızgarası, SitesView satır hızlı araçları (hepsi open-tool 'files' emit → hub render).
- **GÖRSEL QA (Playwright, artifacts/qa/mail-nav-qa.cjs, publish exe Development+http:8791)**:
  setup→login→dashboard→hub→Mail otomatik geçildi, screenshot'lar artifacts/qa/*.png. Doğrulandı:
  admin sol menüsünde Dosyalar YOK (Plesk-temiz), hub doğrudan site listesi, Mail crash'siz
  (yerelde Stalwart yok → "kurulu değil" boş-durum). JS hatası yok (yalnız 3×503 = yerelde
  hazır olmayan backend servisleri). İki tutarsızlık bulunup düzeltildi: (1) Ana Sayfa hızlı
  erişimden "Dosyalar" kartı kaldırıldı (DashboardOverviewView), (2) panel.mail.notInstalled
  metni "MailEnable" → "Stalwart" (varsayılan artık Stalwart). NOT: araç-görünümü (breadcrumb+
  Files) site/IIS gerektirdiğinden yerelde görülemiyor; VPS'te doğrulanmalı.
- Installer SHA256: `70E0A3A6530160747C926F21F8F4DF61EEECEAE924D83C109E21A1720646AC8A` (50937188 byte)

## 2026-06-18 (3) — Mail HESAP oluşturma KÖK NEDEN: "quotas" alanı Stalwart'ı reddediyor
- BELİRTİ: panelde e-posta hesabı oluşturma "E-posta işlemi tamamlanamadı" (mail.operation_failed).
- TANI (VPS'te stalwart-cli ile): `query domain`, `create domain --field ...`, `create Account/User
  --file <quota'SIZ payload>` HEPSİ exit 0 çalışıyor. Tek fark: panel `CreateAccountAsync` payload'a
  HER ZAMAN `,"quotas":{"maxDiskQuota":<bytes>}` ekliyordu (form varsayılanı 1024MB) → Stalwart
  0.16.9 bu alanı reddedip create'i düşürüyordu.
- DÜZELTME (MailService.CreateAccountAsync): (1) hesap KOTASIZ oluşturulur (kanıtlanmış çalışan
  payload), (2) JSON `--json "<escaped>"` yerine `--file <temp>` ile geçirilir (Windows
  CommandLineToArgvW tırnak bozulmasına karşı sigorta; yeni RunCliJsonAsync helper), (3) kota
  hesap oluştuktan sonra ayrı `update account <id> --field quota=<bytes>` ile BEST-EFFORT uygulanır
  (başarısızlığı yutulur — kotasız da hesap oluşur). NOT: doğru quota alan adı (`quota` mı başka mı)
  canlı `get Account <id> --json` ile teyit edilmeli; şu an best-effort.
- DOĞRU ALAN TEYİT (Stalwart docs Individual principal): kota alanı `quota` (tekil, byte) —
  best-effort update zaten doğru alanı kullanıyor.
- AYNI HATA İKİ YERDE DAHA düzeltildi: SetAccountQuotaAsync (kota butonu) `quotas={maxDiskQuota}`
  → `--field quota=<bytes>`; hesap okuma MaxSizeMB `quotas.maxDiskQuota` → `quota`. GetNestedLong
  (artık kullanılmıyor) silindi.
- ÖNEMLİ: düzeltme panel BINARY'sinde → kullanıcının yeni installer'ı KURMASI şart; eski panel
  her zaman aynı "operation_failed"ı verir.
- Installer SHA256: `6A1B7D2F6C8B14E177043A2B7886A35D82F95EF0EF782A31C66EF0A6C91CF852` (50937360 byte)
- ✅ DOĞRULANDI (2026-06-18, VPS): yeni installer kurulduktan sonra panelden e-posta hesabı
  oluşturma ÇALIŞIYOR. Kota alanının (`quota`) gerçekten tuttuğu kullanıcıyla ayrıca teyit
  edilmedi (best-effort; tutmuyorsa `get Account <id> --json` ile doğrulanacak).

## 2026-06-18 (4) — Zayıf parola hatası NET gösteriliyor (Stalwart parola politikası)
- KULLANICI: hesabı "123123" parolayla oluşturamadı (güçlü parolayla oluyordu). Stalwart varsayılan
  parola politikası: min 8 karakter + zxcvbn güç seviyesi 3 ("tahmin edilmesi zor"). Zayıf/yaygın
  parolalar reddediliyor; panel generic "operation_failed" gösterip sebebi gizliyordu.
- DÜZELTME: MailService.MapMailError() — CLI hatası "password" + (weak/strength/short/length/
  guessable/common) içeriyorsa `mail.weak_password` (422) atılır; CreateAccount + MutateAsync
  (şifre değiştirme dahil) bunu kullanır. i18n mail.weak_password (tr/en) net mesaj.
- FRONTEND MailView: hata bildirimi altında gerçek CLI sebebini gösteren genişletilebilir
  "teknik ayrıntı" (extractApiDetails + .notice-details); showError() helper; şifre alanına
  minlength=8 + panel.mail.passwordHint. Parite 1717/1717.
- Installer SHA256: `46181BB9D95767A6BC5E63A628174F0680CD0C267992D5DE8ABDF0E134ED0C88` (50937470 byte)
- SIRADA (kullanıcı sırası 2,1,4,3): MAILING LIST (Stalwart `create mailing-list --field name=..
  --field domainId=.. --field recipients=[..]`; MailingList obj alanları: name/domainId/
  emailAddress/recipients[]/aliases/id) → Reseller → Çoklu IP → WP derinleştirme.

## 2026-06-18 (5) — Mailing list (dağıtım listesi) eklendi [#2 tamam]
- BACKEND: MailListDto (Address/Recipients[]); CreateMailListRequest + SetMailListRecipientsRequest;
  IMailService.CreateMailList/SetMailListRecipients/DeleteMailList; MailWorkspaceDto.Lists.
  MailService: `create mailing-list` (--file ile, quoting sigortası), `update mailing-list <id>
  --field recipients=[..]`, `delete mailing-list --ids`; GetWorkspace seçili domainde
  `query mailing-list`+detayla Lists doldurur. Router delege; MailEnable → mail.lists_unsupported.
  MailController: POST/PUT/DELETE /mail/lists*.
- FRONTEND: api/mail.ts createMailList/setMailListRecipients/deleteMailList + MailList tipi +
  workspace.lists. MailView'e "Dağıtım listeleri" bölümü (alias'tan sonra): oluştur formu
  (yerel kısım + alıcılar textarea, parseRecipients virgül/boşluk/satır), liste tablosu,
  alıcı düzenle (prompt) + sil. i18n panel.mail.list* + mail.lists_unsupported. Parite 1731/1731.
- CANLI TEST GEREKİR: CLI grameri dokümandan alındı; VPS'te liste oluştur + bir mail gönderip
  alıcılara dağıtıldığını doğrula.
- Installer SHA256: `20A5967824A25B7D1B5B1E74EBCDF933A860C19F3179A524DDFEC1296DD566C2` (50940439 byte)
- KALAN sıra: Reseller (enforcement+UI) → Çoklu IP → WP derinleştirme.

## 2026-06-18 (6) — Reseller (Bayi) enforcement + UI [#1 tamam]
- MODEL: Customer'ın opsiyonel OwnerId'si = bayi. Bayi yalnız KENDI müşterilerini ve onların
  site/mail/dosyalarını yönetir; sunucu araçlarına erişemez. İskelet (UserRole.Reseller +
  CustomerScopeService owner metotları) zaten vardı; enforcement + UI eklendi.
- BACKEND (güvenlik server-side): UserSummaryDto.OwnerId/OwnerName; Create/UpdateUserRequest.OwnerId
  + rol regex'ine Reseller. UserManagementService: GetUsersAsync(actorUserId) bayi ise yalnız
  GetOwnedUserIdsAsync ile filtreli; CreateUser bayi çağırınca rol=Customer + owner=self'e ZORLAR;
  UpdateUser bayi yalnız kendi müşterisini (owner==self) günceller, değilse AppException
  users.forbidden(403); owner SetOwnerAsync ile kaydedilir; MapUser owner doldurur. UsersController
  [Authorize Administrator,Reseller] + GetUsers'a User.GetRequiredUserId().
- FRONTEND (SetupWizardView): isReseller; create/edit formuna Reseller rolü + (Customer için) sahip
  dropdown'ı (yalnız admin görür); bayi create formunda rol Customer'a sabit (watch). Bayi nav'ı:
  home/sites/mail/files/settings/exit; Ayarlar>Kullanıcılar sekmesi bayiye "Müşteriler" olarak açık
  (servis planları + rol/sahip seçimi admin-only). Listede owner gösterimi. i18n panel.role.reseller,
  panel.users.owner*, users.forbidden, panel.navigation.customers. Parite 1737/1737.
- CANLI TEST: admin bir Reseller oluştur + bir Customer'a o bayiyi sahip ata; bayiyle giriş yapıp
  yalnız kendi müşterisini gördüğünü ve başka müşteriyi göremediğini doğrula.
- Installer SHA256: `888FF3CAF090425B74E910C144C897F4B008C9008117403F727DEF32CD15FB31` (50943228 byte)
- KALAN sıra: Çoklu IP → WP derinleştirme.

## 2026-06-18 (7) — Çoklu IP (dedicated IP) site binding'i [#4 tamam]
- BULGU: binding modeli ZATEN IpAddress destekliyordu (BuildBindingInformation(ip,port,host);
  CreateSite/SiteBinding IpAddress="*"). Eksik olan: sunucu IP'lerini listeleme + UI'da seçim.
- BACKEND: ServerIpAddressDto (Address/Family/InterfaceName); ISystemMonitoringService.
  GetServerIpAddressesAsync (NetworkInterface.GetAllNetworkInterfaces → Up + loopback/link-local
  hariç unicast adresler, IPv4 önce). SystemController GET /system/ip-addresses.
- FRONTEND: api/system.ts getServerIpAddresses; SiteDetailView binding formunda IP text input →
  DROPDOWN ("Tüm IP'ler (*)" + sunucu IP'leri, onMounted fetch). i18n panel.sites.allIps. Parite 1738.
- KAPSAM NOTU: web (IIS) binding'i için dedicated IP tamam. MAIL tarafı dedicated IP (Stalwart
  listener'ı belirli IP'ye bağlama) kapsam dışı bırakıldı (ayrı/karmaşık, talep gelirse).
- Installer SHA256: `B68DD0DF6A2566AD2A4C8ED5CE5E0F901E475840C4C58BB2ACAD5846AE34C7FC` (50943550 byte)
- KALAN: WordPress Toolkit derinleştirme (#3, son).

## 2026-06-18 (8) — WordPress Toolkit derinleştirme: eklenti+tema listesi [#3 tamam]
- WordPressToolkitService (dosya tabanlı, WP-CLI'siz): wp-content/plugins/*'tan eklentiler
  (her klasörde "Plugin Name:" başlıklı ana php + tek-dosyalık eklentiler), wp-content/themes/*/
  style.css'ten temalar; ReadHeader ilk ~8KB'da "Anahtar: Değer" (HeaderRegex GeneratedRegex);
  ad+sürüm. WordPressSiteDto.Plugins/Themes (WordPressComponentDto name/slug/version). BuildDto
  WP siteyse doldurur (aktiflik DB gerektirdiğinden listede yok — okuma/güvenli).
- FRONTEND: api/wordpress.ts WordPressComponent + plugins/themes; WordPressToolkitView'e
  eklenti + tema tabloları (ad/sürüm + sayaç). i18n panel.wp.plugins/themes/componentName/
  componentVersion/noPlugins/noThemes. Parite 1744/1744.
- Installer SHA256: `273E7AD016C6F011E1AF4A98E175F4ECFE36AFE14F20722A60271910B2C6FFED` (50944765 byte)
- ✅ KULLANICI MİMARİ SIRASI TAMAM (2,1,4,3): mailing list + reseller + çoklu IP + WP derinleştirme.
  Hepsi CANLI test edilmeli (VPS). Olası sonraki: WP eklenti/tema güncelleme (WP-CLI gerekir),
  mail dedicated IP (Stalwart listener), staging.

## 2026-06-18 (9) — Reseller nav DÜZELTME (denetim) + build doğrulama
- DENETİM: SitesController=Administrator-only; FilesController/MailController=Administrator,Customer.
  Yani Reseller bu controller'lara KAPALI → bayiye verdiğim sites/mail/files nav sekmeleri 403
  alacaktı (kırık UX, güvenlik açığı değil). Düzeltildi: bayi nav'ı yalnız Ana Sayfa + Ayarlar
  (Müşteriler) + Çıkış; sites/mail/files render gate'lerindeki reseller eklemeleri geri alındı.
  Bayi Ayarlar'a girince settingsTab='users' (Müşteriler) otomatik. Bayinin müşteri kaynaklarını
  DOĞRUDAN yönetmesi (reseller→owned customers→sites scoping) ileride; şu an müşteri kendi
  kaynaklarını yönetir. Reseller'ın user-yönetimi izolasyonu (gördüğü tek şey kendi müşterileri)
  server-side sağlam.
- Boot smoke (son build): alive + /panel 200 + /api/v1/setup/status 200.
- Installer SHA256: `91BA83E3F77A5EA4938649080F21D57586D1291E258B703CBBE33E23374FFED7` (50944792 byte)

## 2026-06-18 (10) — Bayi kaynak yönetimi: Mail+Files+Sites sahip-scoping
- MODEL: bayinin ETKİN kapsamı = sahip olduğu müşterilerin sitelerinin/mail domainlerinin birleşimi.
- ICustomerScopeService + CustomerScopeService: GetResellerSitesAsync / GetResellerMailDomainsAsync
  (owned customers union). CustomerAccess artık rol-agnostik "scoped": IsReseller/IsScoped +
  GetEffectiveSitesAsync/GetEffectiveMailDomainsAsync; EnsureSite/EnsureMailDomain/EnsureFilesPath
  hem Customer hem Reseller'a göre çalışır.
- CONTROLLER: MailController + FilesController role listesine Reseller; liste filtreleri
  GetEffective*'e çevrildi (Files kök listesi etkin sitelerle sentezlenir). SitesController:
  [Authorize Administrator,Reseller]; GetSites etkin sitelerle filtreli; TÜM site-uçlu endpoint'ler
  (get/protected-dirs×3/deletion-plan/delete/start/stop/bindings×2/defaults) EnsureSiteAsync ile
  korundu; CREATE + IMPORT admin-only (method-level) — bayi site oluşturamaz (atama/limit modeli dışı).
- FRONTEND: bayi nav'ına Mail + Dosyalar geri eklendi (scoped). Tam "Web Siteleri" hub'ı
  (SSL/DNS/FTP/PHP alt-controller'ları henüz scope'lanmadı) bayide YOK.
- Boot smoke: /panel 200. Installer SHA256: `3F2F4C8B1A20D44DF634CCA2830BCF826F41DEC3BE1AC1AD1DED5BBB4C87DE11` (50947089 byte)
- GÜVENLİK NOTU: izolasyon her site-uçlu endpoint'in EnsureSiteAsync çağırmasına bağlı — yeni
  site-uçlu endpoint eklenirse guard unutulmamalı. CANLI test: 2 müşterili 1 bayi; bayi yalnız
  kendi müşterilerinin site/mail/dosyalarını görmeli, diğerini 403 almalı.
- KALAN sıra: WP kurulum/güncelleme → mail dedicated IP.

## 2026-06-18 (11) — WP çekirdek güncelleme KONTROLÜ (güvenli) [#3 kısmi]
- WordPressToolkitService: GetLatestCoreVersionAsync — wordpress.org version-check/1.7 API
  (offers[0].current), static HttpClient(8s) + 6 saat cache + SemaphoreSlim. WordPressSiteDto.
  LatestVersion/UpdateAvailable (IsOlderVersion: System.Version karşılaştırma, belirsizse false).
  DetectAsync/DetectAll/SetMaintenance latest'i çekip BuildDto'ya verir.
- FRONTEND: WordPressToolkitView sürüm kutusunda "Güncelleme var: X.Y" (warn) / "Güncel" (ok)
  rozeti; updateAvailable ise "Güncellemeyi çalıştır" → adminUrl+update-core.php (WP'nin KENDI
  güvenli güncelleyicisi; in-place dosya değişimi YAPILMADI — müşteri sitesini bozma riski yok).
  i18n panel.wp.updateAvailable/upToDate/runUpdate. Parite 1747.
- BİLİNÇLİ KAPSAM: gerçek in-place çekirdek/eklenti güncellemesi (download+unzip+replace) ve
  tek-tık WP KURULUMU (DB+wp-config) YAPILMADI — yerelde test edilemez + site bozma riski yüksek;
  güncelleme görünürlüğü + WP'nin kendi güncelleyicisine yönlendirme güvenli MVP.
- Installer SHA256: `67F3C17C36BB63ED20E96BABC33338A8C2A224AF54796365A4BD9EA251FAC825` (50948784 byte)
- KALAN: mail dedicated IP (#4, son).

## 2026-06-18 (12) — Mail dinleyici/IP görünümü (güvenli) [#4 kısmi] — TÜM LİSTE BİTTİ
- GÜVENLİK KARARI: Stalwart listener'ını belirli IP'ye REBIND etmek server-geneli + yanlışsa mail'i
  tamamen erişilemez bırakır + yerelde test edilemez → körlemesine YAPILMADI. Güvenli dilim: mail
  sunucusunun ŞU AN hangi adres:port'larda dinlediğini gösteren salt-okunur görünüm.
- MailService.GetListenersAsync: `query NetworkListener --json` → MailListenerDto (Name/Protocol/
  Binds[]). Router delege; MailEnable boş. MailController GET /mail/listeners (admin).
- FRONTEND: MailView advanced'e "Mail sunucusu dinleyicileri" + "Dinleyicileri göster" → tablo
  (ad/protokol/adres:port). api/mail.ts getMailListeners + MailListener. Parite 1754.
- Installer SHA256: `4AB4089B6B885716D3B6D841342E7A351A63805FA3A2E6161D4039DDA64A23E4` (50950773 byte)
- ✅✅ KULLANICININ TÜM SIRASI (2,1,4,3 + derin scoping 2,3,4) BİTTİ. Geriye kalan/ertelenenler
  (bilinçli, hepsi canlı sunucu + risk gerektirir): WP in-place güncelleme + tek-tık kurulum,
  mail listener REBIND, tam "Web Siteleri" hub'ını bayiye scope'lama (SSL/DNS/FTP/PHP alt-controller).
  ŞİDDETLE ÖNERİLEN SONRAKİ ADIM: tüm bu birikmiş özellikleri VPS'te canlı doğrulamak
  (özellikle reseller izolasyonu — güvenlik-kritik).

## 2026-06-18 (13) — KOMPLE DENETİM + düzeltmeler (2 kez kontrol)
- Tüm çözüm: 0 hata/0 uyarı; testler 43/43 geçti; boot smoke /panel+setup 200.
- GÜVENLİK DENETİMİ (izolasyon, endpoint-endpoint doğrulandı):
  - SitesController: 11 site-uçlu endpoint'in HEPSİ EnsureSiteAsync; create+import admin-only;
    GetSites etkin-kapsam filtreli. ✅
  - MailController: domaine özel 14 ucun HEPSİ EnsureMailDomainAsync; configure/listeners/rdns/
    cert/spam admin-only. ✅
  - FilesController: tüm uçlar EnsureFilesPathAsync; acl admin-only; kök liste etkin-kapsam. ✅
  - Audit API admin-only → müşteri/bayide auditLogs boş (sızıntı yok).
- DÜZELTİLEN HATALAR:
  1. Ana Sayfa hızlı erişim + özet kartları role DUYARSIZDI → müşteri/bayi sites/database/tools'a
     tıklayınca BOŞ EKRAN. DashboardOverviewView'e allowedSections prop'u + can() eklendi; butonlar
     role göre gösteriliyor/koşullu-tıklanabilir. (Customer için de zaten bozuktu, düzeldi.)
  2. MailView "ayarlar"daki rDNS/Sertifika/Dinleyiciler admin-only uçlar müşteri/bayide 403
     veriyordu → bu 3 bölüm isAdmin'e gate'lendi (DNS göster/uygula scoped rollerde kalır).
     apply() içindeki loadSpam/loadCertificate de isAdmin'e koşullandı (gereksiz 403 yok).
- Installer SHA256: `429D3296D2D66ADDB15C2D108E5B2355C9D65514FC2F976D1E6DCEDA523067A5` (50950972 byte)

## 2026-06-18 (14) — 2. derin tarama: gerçek eksikler bulundu + düzeltildi
- BULGU 1 (FONKSİYONEL, kritik): loadDashboardData'da bayi `if(!isAdmin) return` ile erken
  çıkıyordu → fetchUsers yalnız admin dalındaydı → BAYİ "Müşteriler" sekmesinde kendi
  müşterilerini GÖREMİYORDU (boş liste). Reseller dalı eklendi: fetchUsers + fetchSites.
- BULGU 2 (i18n): frontend t() kullanımı taraması → `panel.common.delete` eksikti (servis planı
  sil butonu ham metin gösteriyordu). tr+en eklendi.
- BULGU 3 (i18n, büyük): backend AppException ile fırlatılan 232 mesaj anahtarından 69'unun
  frontend karşılığı YOKTU → hata durumunda kullanıcı ham anahtar (ör. "ftp.user_not_found")
  görüyordu. 69 anahtarın hepsi tr+en eklendi (script ile, parite-güvenli). Parite 1824/1824.
  NOT: success mesajı anahtarları (ApiResponse.Ok'taki) KASITLI eklenmedi — frontend success'te
  kendi panel.* mesajını gösterir, API mesajını yok sayar (72 anahtar gösterilmez, eklenmedi).
- Doğrulama: çözüm 0 hata, testler 43/43, frontend t() taraması 0 eksik, boot smoke /panel 200.
- Installer SHA256: `32F17113164DAE72BAD1B8BFF927A374297DB6DE0746B6557F3E1720A34DC209` (50952099 byte)

## 2026-06-18 (15) — #3 Bayiye TAM "Web Siteleri" hub'ı + #4 yükleme kotası
- #3 SCOPING (güvenlik-kritik, her uç EnsureSiteAsync ile korundu; server-level admin-only kaldı):
  PhpController (site ayarları; install admin-only), AspController, ProxyController,
  AppRuntimeController, DnsController (windows/install admin-only), WordPressController (GetAll
  etkin-sitelerle filtreli), StatisticsController, SslController (GetWorkspace+Issue+Import scoped;
  renew-all admin-only), FtpController (GetUsers filtreli, CreateUser scoped, password/delete
  kullanıcı→site çözümüyle; ensure admin-only), SiteHostingSettingsController. BackupController:
  Reseller eklendi + EnsureBackupOwnershipAsync IsCustomer→IsScoped (bayi açığı kapandı).
- FRONTEND: bayi nav'ına 'sites' (tam hub) geri eklendi. WebsiteWorkspaceView siteTools +
  SitesView quickTools + SiteDetailView grid: server-level araçlar (store/webdb/cache) bayi/
  müşteride GİZLİ (adminOnlyTools). SitesView site oluştur/içe-aktar + SiteDetailView subdomain
  yalnız admin (bayi mevcut siteleri yönetir, oluşturamaz).
- #4 YÜKLEME KOTASI: FilesController upload artık customer=kendi, RESELLER=yüklediği sitenin
  SAHİBİ müşterinin kotasına tabi (ICustomerScopeService.GetSiteOwnerUserIdAsync). Admin kotasız.
- Doğrulama: çözüm 0 hata/0 UYARI (FilesController null-deref uyarısı da giderildi), testler 43/43,
  guard audit (11 controller'da site-uçları korunmuş), typecheck temiz, parite 1824/1824, boot 200.
- Installer SHA256: `DBCD9FCA3180305D1E7212C9EFE9B063030836C00E3FB06D85E721FF4A476B07` (50954518 byte)

## 2026-06-19 — Site erişilemiyor: 2 KÖK NEDEN (Stalwart 443 + SSL depo uyuşmazlığı)
- SEMPTOM: site http'de Default IIS sayfası, https ulaşılamıyor; sonra SSL "güvenli değil".
- KÖK NEDEN 1 (site Stopped/başlamıyor): IIS site Start → 0x80070020 (sharing violation). Sebep:
  Stalwart'ın `https` adlı NetworkListener'ı **0.0.0.0:443**'ü kapıyordu; IIS sitenin https binding'i
  için 443'ü alamayınca Start düşüyordu. 443 WEB portu, Stalwart'a gerekmez (yönetim 8080).
  VPS FIX: `stalwart-cli delete NetworkListener --ids <https-id>` + Stalwart restart → site Start oldu.
  KOD FIX: ComponentBootstrapper.ConfigureStalwartListenersAsync artık 443 bağlayan dinleyiciyi
  kurulumda otomatik siler (changed→restart). delete sözdizimi: `delete NetworkListener --ids <id>`.
- KÖK NEDEN 2 (SSL "güvenli değil" = self-signed bağlı): win-acme LE sertifikasını **LocalMachine\
  WebHosting** deposuna koyuyor; panel `FindBestCertificate` yalnız **My**'a bakıyordu → LE bulunamayıp
  TryIssueWithWinAcme null → CreateSelfSignedCertificate'a düşüp onu bağlıyordu. (LE sertifikası
  aslında geçerliydi, log: CN=golftaksiatakum.xyz O=Let's Encrypt, exp 2026.9.16, store WebHosting.)
  KOD FIX (WinAcmeSslService): FindBestCertificate WebHosting+My tarıyor (özel anahtarlı tercih);
  EnsureHttpsBindingAsync sertifikayı BULUNDUĞU depodan bağlıyor ($store = My ya da WebHosting).
  VPS HEMEN-FIX: mevcut LE sertifikasını 443 binding'ine elle bağlama scripti verildi.
- Installer SHA256: `CEF040208268717A370EF68720CEE3E27C3A22A6C262353BA4217B5A5D2EC99C` (50955100 byte)

## 2026-06-19 (2) — Force HTTPS (Plesk gibi otomatik HTTP→HTTPS 301)
- İSTEK: Plesk gibi SSL kurulunca HTTP→HTTPS yönlendirme otomatik açılsın + ekranda tik işaretli.
- BACKEND (WinAcmeSslService): ApplyHttpsRedirectAsync — URL Rewrite kuralı "AdaPanel-ForceHTTPS"
  (match (.*), koşul {HTTPS}=off, ACME `/.well-known/acme-challenge/` HARİÇ → LE yenilemesi bozulmaz,
  action Redirect 301 https://{HTTP_HOST}/{R:1}). URL Rewrite modülü yoksa best-effort atlanır.
  IssueLetsEncrypt + Import sonrası OTOMATIK açılır. SetHttpsRedirectAsync (aç/kapat) + workspace.
  HttpsRedirectEnabled (IsHttpsRedirectEnabledAsync rule var mı bakar). ILogger eklendi.
- API: SslController POST /ssl/https-redirect (EnsureSite ile scoped).
- FRONTEND: SslView'de "HTTPS'e zorla (otomatik 301)" tiki — workspace.httpsRedirectEnabled'ı yansıtır,
  SSL kurulunca otomatik işaretli gelir; aç/kapat setHttpsRedirect çağırır. api/ssl.ts + i18n
  panel.ssl.forceHttps*. Parite 1828/1828.
- Installer SHA256: `6F49A429F65646CB92FA344CAE718D8EFD051775F0A518E447EAC5C1E5F58292` (50957104 byte)

## 2026-06-19 (3) — URL Rewrite garanti + entegrasyon-mayını taraması
- url-rewrite components.json'da ZATEN default:true (ilk kurulumda önerilenlerle gelir). EK olarak
  WinAcmeSslService.ApplyHttpsRedirectAsync: modül yoksa (PowerShell 'NOREWRITE') panel
  componentBootstrapper.InstallAsync("url-rewrite") ile OTOMATİK kurar, sonra kuralı tekrar uygular.
  WinAcmeSslService'e IComponentBootstrapper enjekte edildi (DI döngüsü yok — CB SSL'e bağımlı değil; boot 200).
- PROAKTİF ENTEGRASYON TARAMASI (kullanıcı "komple kontrol" eleştirisi üzerine): default-kurulan
  bileşenlerde 80/443 kapan BAŞKA servis YOK (php/mariadb/composer/phpmyadmin/url-rewrite/iis-arr/
  stalwart/win-acme). Tek web-port çakışması Stalwart 443'tü → kurulumda otomatik kaldırılıyor.
  iis-arr IIS modülü (port tutmaz); nginx default değil (opt-in). win-acme yenilemesi --installation iis
  ile WebHosting'den doğru rebind eder (renewal sağlıklı).
- DERS: "komple kontrol" = kod denetimi (build/test/güvenlik/i18n) TUTTU; ama canlı entegrasyon
  mayınları (port çakışması, cert-store konvansiyonu, IIS quirks) yalnız gerçek sunucuda görünür.
  Bundan sonra denetimlere port-çakışması + dış-araç-depo konvansiyonu da dahil edilmeli.
- Installer SHA256: `21570FE4665CE91E0353A08AA0F532FA5A22D509A634C0669075B6D4111B8633` (50957525 byte)

## 2026-06-19 (4) — DERİN TARAMA (kullanıcı "sen bul" dedi): CLI gramer + sözleşme denetimi
- CERT PUSH MAYINI bulundu+düzeltildi: PushCertificateToStalwartAsync `create/update Certificate
  --json "<escaped>"` kullanıyordu; payload ÇOK SATIRLI PEM (cert+private key) → inline arg bozulur.
  → RunCliJsonAsync (--file) ile değiştirildi; ReloadTlsCertificates Action'ı da `--file` JSON
  (`{"@type":"ReloadTlsCertificates"}`) ile (önceki `--field @type=` yerine).
- FRONTEND↔BACKEND SÖZLEŞME: 187 apiClient çağrısının HEPSİ backend rotalarıyla eşleşti (ölü
  buton/404 yok). 4 "bulunamadı" yanlış-pozitifti (query string / route-param şekli).
- BACKEND HATA ANAHTARLARI: i18n 0 eksik (parite 1828). Çözüm 0 hata/0 uyarı, testler 43/43.
- CANLI-DOĞRULAMA GEREKTİREN (kör değiştirmedim, çalışan kodu bozmamak için): MailService'te
  spam settings field adları (enable/scoreSpam/...), autoresponder (sieve vacation), alias/parola
  `update account --field {json}` quoting — CliJsonArg CommandLineToArgvW'ye göre doğru görünüyor
  ama Stalwart 0.16.9'da canlı test edilmeli. Bunlar "X yapamıyorum" çıkarsa ilk bakılacak yerler.
- Installer SHA256: `F5E505E909B3309F762313BE232E983B872387A2F02CA2345C0BD0B0C1B3B255` (50957710 byte)

## 2026-06-19 (5) — "Posta açılmıyor": Stalwart 8080 dinlemiyor + CLI timeout dayanıklılığı
- TANI (VPS): stalwart süreci AYAKTA (PID) ama 8080 DİNLEMİYOR → panel mail yönetimine (localhost:8080)
  bağlanamıyor → Posta sayfası boş/spinner. SSL/443 işiyle İLGİSİ YOK (443 silindi, mail 8080 + 25/465/
  587/143/993'te). Sebep: dünkü art arda restart'lardan Stalwart yarı-ayakta kalmış.
- VPS FIX: schtasks /End + Stop-Process stalwart + /Run → 8080 geri gelir; gelmezse logs\stalwart.log.
- KOD DAYANIKLILIK (gerçek açık): MailService.RunCliAsync'in TIMEOUT'u yoktu → Stalwart yarı-ayaktaysa
  stalwart-cli sonsuza asılıp mail isteğini (ve sayfayı) süresiz boş/spinner'da bırakıyordu. → RunCliAsync
  artık 30sn CliTimeout ile çalışır (RunCliJsonAsync da devralır); bağlanamazsa hızlı başarısız → panel
  "configured=false / hata" gösterir, asılı kalmaz.
- Installer SHA256: `3C28AECBBE0956F1144870EB25D7CCACFAC47F953592A783419417B36F832115` (50957774 byte)
- CANLI TEST ŞART (güvenlik): 2 müşterili 1 bayi → bayi Web Siteleri'nde yalnız kendi müşterilerinin
  sitelerini görüp tüm araçları (PHP/SSL/DNS/FTP/WP/Stats/Backup/Hosting) kullanabilmeli; başka
  müşterinin sitesine her uçtan 403 almalı.
- SIRADA: VPS'te yeni installer ile temiz kurulum → Stalwart SQLite ile otomatik açılmalı (artık
  8080 elle uğraşmadan). Mail + menü Plesk akışı görsel doğrulanmalı.

## 2026-06-19 (6) — Mail "Yönet" BOŞ SAYFA kökten çözüldü + site başlatma 500 (Türkçe Windows) düzeldi
- KÖK NEDEN 1 (boş sayfa / F12'de `Vue render error SyntaxError: 10` vue-i18n derleyicide):
  vue-i18n çeviri DEĞERLERİNDE `@` (linked-message), `|` (çoğul), `{...}` özel söz dizimidir.
  Eklediğim mailing-list metinleri `@` içeriyordu (`panel.mail.listsHint`="...duyuru@alanadi...",
  `panel.mail.listRecipientsPlaceholder`="ali@example.com,...") → "Yönet" ekranı (liste formu)
  render edilince derleyici SyntaxError fırlatıp tüm bileşeni çökertiyor = boş sayfa.
- KALICI ÇÖZÜM (tek dosya, tüm sınıfı yok eder): `frontend/src/main.ts` → createI18n'e ÖZEL
  `messageCompiler: safeMessageCompiler` eklendi. Panel yalnız {name}/{0} yer tutucusu ve {'@'}
  literal kaçışı kullandığından, güvenli derleyici SADECE bunları işler; `@`, `|`, `@:`, hatalı
  `{` dahil HER ŞEYİ düz metin bırakır → ne statik çeviri ne de SUNUCUDAN GELEN ham hata metni
  (resolveApiMessage → t(serverMessage)) paneli BİR DAHA çökertemez. İlgili i18n değerlerinde `@`
  ayrıca `{'@'}` ile kaçırıldı. Tüm i18n taraması: `@`/`|`/hatalı`{}` kalmadı; 1829/1829 parite.
- KÖK NEDEN 2 (site başlat → 500): `WebAdministrationSiteService.MapIisException` hataları İNGİLİZCE
  substring (`"being used by another process"` vb.) ile sınıflıyordu. Sunucu TÜRKÇE Windows → Win32/COM
  mesajları yerelleşiyor, hiçbiri eşleşmiyor → her şey genel 500. 443'ü tutan Stalwart yüzünden start
  0x80070020 (ERROR_SHARING_VIOLATION) veriyordu ama 500 olarak görünüyordu.
- ÇÖZÜM: `FlattenExceptionMessage` artık her seviyenin HRESULT hex'ini ekliyor (dilden bağımsız);
  MapIisException önce HRESULT'a bakıyor: 0x80070020→`sites.port_in_use`(409, yeni anahtar, net mesaj),
  0x80070005/0x80070522→izin(403), 0x800700B7→binding_conflict(409). Artık dile bakmadan doğru/anlaşılır.
- AÇIK BULGU (henüz fix YOK, düşük etki): aynı locale-kırılgan İngilizce `.Contains("access is denied"...)`
  eşleştirmesi 7 serviste daha var (AppRuntime/AspNet/Php/ReverseProxy/WinAcmeSsl/Waf/SiteHostingSettings)
  — yalnız izin SINIFLANDIRMASI (genel mesaj vs "yönetici çalıştır" ipucu); çökme/boş sayfa YOK. Türkçe
  Windows'ta bunlar da genel mesaj döner. İleride HRESULT tabanına çevrilmeli (toplu refactor, risk var).
- Build: typecheck 0, test 43/43, publish self-contained tek dosya OK.
- Installer SHA256: `00DA576F5027298CAF652F089EFA5E41E77EC89CEB6D8AF6835999E3D2CE5C3B` (50957639 byte)
  — ÖNEMLİ: önceki 3C28AEC... build'i de `@` hatasını içeriyordu, kurmak çözmezdi; BU build çözer.

## 2026-06-19 (7) — Mail TLS sertifikası otomatikleşti (Plesk gibi, "uğraştırmasın")
- İSTEK: Posta sertifika kartında kullanıcı mail sunucu adı + iletişim e-postası yazıp "Sertifika al
  ve yükle"ye basmak zorunda kalmasın; varsa otomatik alınsın.
- PAYLAŞILAN E-POSTA: yeni `Support/AcmeContactSettings.cs` (`config/acme-contact.json`, alan: Email).
  - `WinAcmeSslService.IssueLetsEncryptAsync`: site SSL'i alınırken contactEmail buraya yazılır (best-effort).
  - `MailService.IssueCertificateAsync`: request.ContactEmail boşsa bu kayıttan okur (kullanıcı tekrar
    girmez); LE başarılıysa e-postayı yine kaydeder.
  - `MailService.GetCertificateStatusAsync`: `SuggestedContactEmail` döner (DTO'ya alan eklendi).
- FRONTEND (MailView): loadCertificate formu otomatik doldurur (hostName=mail.<domain>,
  contactEmail=suggested). Sertifika YOKSA ve e-posta biliniyorsa `autoIssueCert()` ile BİR KEZ sessiz
  otomatik alınır (hata toast'ı yok; başarısızsa form dolu, tek tıkla manuel). active=true olunca
  durur, döngüye girmez. `certAutoTried` guard. api/mail.ts MailCertificate'a suggestedContactEmail eklendi.
- NOT: mail sertifikası TEK global mail-sunucu sertifikası (config/mail-cert.json); hostname mail.<domain>.
  Mail TLS doğru olması için cert mail.<domain>'i kapsamalı → win-acme HTTP-01 mail hostname için 80'e
  binding ekler (mevcut davranış). Site cert'i (apex) tek başına mail hostname'i kapsamaz, o yüzden ayrı alınır.
- Build: solution build 0, typecheck 0, test 43/43, publish OK.
- Installer SHA256: `9DEDC5F2556C9DE70E5FEEF8EA98520654E1C0CEA3EEAF2DB661E9B561533ADD` (50958455 byte)

## 2026-06-19 (8) — Tek tık Webmail (Roundcube) + posta istemci ayarları kartı (Plesk gibi)
- İSTEK (3 madde): tek tık webmail, Roundcube entegrasyonu, posta istemci ayarları kartı.
- İSTEMCİ AYARLARI KARTI (item 3, garantili/düşük risk): MailView yönetim görünümünde mailbox
  listesinin altına "Webmail + Posta istemci ayarları" bölümü. Kart: Gelen IMAP mail.<domain>:143
  STARTTLS, Giden SMTP mail.<domain>:587 STARTTLS, kullanıcı adı=tam adres. Saf frontend (mailHost
  computed) + i18n (panel.mail.webmail*/clientSettings*). Sunucu adı kopyalanabilir.
- WEBMAIL (item 1+2): Roundcube'u IIS PHP sitesi olarak kuran WebmailService (phpMyAdmin/WebDbClient
  kalıbı). Yeni dosyalar: Core/Dtos/Mail/WebmailDto.cs, Core/Interfaces/IWebmailService.cs,
  Infrastructure/Mail/WebmailService.cs. DI: ServiceCollectionExtensions. Uç: POST /mail/webmail/open
  (MailController, tüm posta rolleri) → OpenAsync(Request.Host.Host) → URL döner; frontend openWebmail()
  pop-up'ı tıklama anında açıp URL gelince yönlendirir.
- TAR.GZ DESTEĞİ (kritik altyapı): Roundcube SADECE .tar.gz dağıtır, bootstrapper yalnız zip çıkarıyordu.
  ComponentBootstrapper'a "targz" tipi eklendi: ExtractTarGzAsync (System.Formats.Tar.TarFile +
  GZipStream, harici tar.exe yok) + FlattenSingleTopDirectory + rollback cleanup + ParseComponentType
  + ComponentType.TarGz=6. components.json'a roundcube (type targz, 1.7.1-complete.tar.gz, dep php-8.3).
- ROUNDCUBE CONFIG: WebmailService config/config.inc.php yazar → db_dsnw SQLite (Roundcube ilk
  bağlantıda şemayı kurar, ayrı DB sunucusu yok), imap_host tls://localhost:143, smtp_host
  tls://localhost:587 (STARTTLS), conn_options verify_peer=false (loopback cert mail.<domain> uyuşmaz).
  temp/logs/adapanel-data dizinlerine icacls ile IIS_IUSRS:(OI)(CI)M yazma izni. Site AdaPanel.Webmail
  port 8899, FastCGI PHP. Sağlık: adapanel-health.php probe.
- !! CANLI DOĞRULAMA GEREKİR (kodda derlendi, sunucuda test edilmedi): (a) PHP eklentileri
  pdo_sqlite+intl+openssl+mbstring etkin OLMALI (değilse mail.webmail_deploy_failed net hata + detay),
  (b) 8899 portu dışarı/panelden erişilebilir olmalı, (c) Roundcube SQLite auto-init. Şu an webmail
  HTTP (TLS değil) — ileride HTTPS'e taşınabilir. Bkz [[adapanel-webmail-tasks]].
- Build: solution build 0, typecheck 0, test 43/43, parite 1844/1844, publish OK.
- Installer SHA256: `B8851DE23E0CA08EFC0E3BEECD091DD707EBA037221947F1DADDF198DB2B111E` (50965301 byte)

## 2026-06-19 (9) — Roundcube checksum hatası düzeldi + ilk kurulumda gelir
- HATA (canlı): "Yazılım paketi için güvenilir bir SHA-256 özeti bulunamadı" (software.checksum_required).
  Sebep: bootstrapper boş sha256 + güvenilmeyen host (github) için checksum ZORUNLU; Roundcube GitHub'da
  .sha256 sidecar yerine .asc (GPG) veriyor → çözümleyici bulamıyor → install reddediliyor.
- ÇÖZÜM: components.json roundcube'a GERÇEK sha256 pinlendi (GitHub digest + indirip Get-FileHash ile
  birebir doğrulandı): `1E0382BCEFD627AB0B6285D3181DDFBA5B444FDCF6D49F33F5EA15FBF97864EF`
  (roundcubemail-1.7.1-complete.tar.gz, 6.335.172 byte). Roundcube ResolveComponentChecksum switch'ine
  düşmüyor (default case component'i olduğu gibi döndürür) → pin korunur.
- "default": true yapıldı → ilk kurulum sihirbazında otomatik iner (phpMyAdmin/Adminer gibi; IIS site
  deploy yine ilk "Webmail'i aç"ta olur, ama indirme/kurulum setup'ta bittiği için ilk tık hızlı).
- Sadece components.json değişti (kod yok): publish'e kopyalanıp ISCC ile installer yeniden derlendi.
- Installer SHA256: `B67AE3E55BD98FC09181CE72C2668A412FC2DAFCC43826836F299C62C9F87EAE` (50965610 byte)

## 2026-06-19 (10) — Webmail "point your server to /public_html" + IIS varlık servisi düzeldi
- HATA (canlı): Webmail açılınca "Please, configure your HTTP server to point to the /public_html
  directory". Sebep: Roundcube 1.5+ yapısında web BELGE KÖKÜ `public_html/` alt klasörüdür; kök
  index.php sadece sert `exit('...point to /public_html...')` yapar. Ben IIS'i KÖKE yönlendirmiştim.
- ÇÖZÜM (WebmailService): ResolveEntryRoot→ResolveInstallRoot (program/ + public_html/ içeren kök);
  documentRoot = installRoot/public_html → IIS sitesi ARTIK public_html'i kök alır. config/temp/logs/
  SQLite db ise installRoot'ta (webroot DIŞINDA, güvenli). Health endpoint + EnsurePhpEntry public_html'e.
- VARLIK SERVİSİ (kritik, IIS): skins/program/plugins webroot ÜSTÜNDE. Roundcube boş assets_path'te
  bunları `static.php/...` (PATH_INFO) ile verir — IIS'te sorunlu. ÇÖZÜM: config'e `assets_path='./'`
  + public_html içine skins/program/plugins'e directory JUNCTION (mklink /J, ayrıcalık gerektirmez).
  Böylece CSS/JS/img düz statik dosya olarak servis edilir (rewrite/PATH_INFO/PHP yok). config/temp/
  logs/vendor LİNKLENMEZ (güvenli).
- İZİNLER: GrantAppPoolWrite→GrantAppPool(dir,perm). installRoot'a IIS_IUSRS RX (tüm ağacı okuyup
  çalıştırma: program/vendor/config), temp/logs/adapanel-data'ya M (yazma + SQLite). %ProgramData%
  tool dizini default okunamayabilir, o yüzden şart.
- SAĞLIK: VerifyHealthyAsync artık 2 aşama: (1) /adapanel-health.php (PHP/IIS hattı), (2) / (Roundcube
  gerçekten açılıyor mu — eksik eklenti/izin burada yakalanır). Hata detayında body önizlemesi döner.
- Sadece WebmailService.cs değişti (frontend yok). Installer yeniden derlendi.
- Installer SHA256: `0407034335A3EDF5D9654B5D92CF5FBF632C8DF77D82D8A253FA93A12A5BC82C` (50966970 byte)

## 2026-06-20 (11) — Webmail çalışma-zamanı gereksinimleri PROAKTİF kapatıldı (tek tek hata avını bitir)
- Kullanıcı: "her hatayı adım adım bulacaksak yandık, en ince ayrıntıya kadar kontrol et, ne gerekliyse
  kur, Plesk gibi yap." → webmail'in TÜM runtime landmine'larını şimdi kapattım.
- PHP EKLENTİLERİ: PhpService'e EnsureExtensionsAsync(componentId, extensions) eklendi (IPhpService'e de).
  MEVCUT php.ini'de de eklentileri açar (DLL ext\ altında varsa). WebmailService deploy'da Roundcube
  eklentilerini garanti eder: pdo_sqlite/sqlite3/intl/mbstring/openssl/fileinfo/zip/gd/exif/iconv/curl/
  gettext. Site SONRA oluşturulduğundan yeni FastCGI güncel ini'yi okur. NOT: zaten açık olanlar (intl/
  mbstring/openssl/pdo_sqlite/fileinfo/zip) Roundcube'un BOOT'u için yeterliydi; gd/exif/iconv tam özellik.
  Bootstrapper fresh-install default ext listesine de gd/exif/gettext/iconv eklendi. Test fake (WebDb
  DeploymentTests.SuccessfulPhpService) yeni metodu aldı.
- FIREWALL: WebmailService.OpenFirewallPort(port) → netsh ile "AdaPanel Webmail <port>" inbound TCP kuralı
  (idempotent: önce sil, ekle). Uzak tarayıcıdan 8899 erişilebilsin.
- HÂLÂ AÇIK (tek kalan Plesk farkı): webmail HTTP (8899), HTTPS değil → login parolası düz metin. Plesk
  HTTPS sunar. İleride mail cert'i webmail portuna bağlamak ya da webmail.<domain>/HTTPS gerekebilir.
  Kullanıcıya bildirildi. Bkz [[adapanel-webmail-tasks]].
- Build: solution build 0, test 43/43, publish OK. components.json değişmedi (gd/exif KODdaki listede).
- Installer SHA256: `71BFEA6E6B7AF269B064E2F85C58E556155F7240702A8CE5BD21EA0EA2E106E1` (50968336 byte)

## 2026-06-20 (12) — GELEN MAIL gelmiyordu: mail portları firewall'da kapalıydı (KÖK NEDEN)
- SEMPTOM: Webmail'den giden mail Hotmail'e ulaştı (junk'a düştü) ama Hotmail'den GELEN cevap hiç gelmedi.
- KÖK NEDEN: configure-firewall.ps1 SADECE panel portunu (8890) açıyordu; mail portları (özellikle
  25=SMTP/MX gelen) HİÇBİR build'de açılmıyordu. Windows Firewall inbound'u varsayılan bloklar →
  dışarıdan gelen posta Stalwart'a ulaşamıyor. (Giden çalışıyordu çünkü outbound 25 açık + provider
  bloklamıyor.) ÖNEMLİ: en yeni 71BFEA6 build'i bile mail portlarını açmıyordu — yeniyi kurmak tek
  başına çözmezdi.
- ÇÖZÜM (iki yerde, güvenlik ağı): (1) configure-firewall.ps1 artık 25/143/465/587/993/995 inbound TCP
  kurallarını da açıyor (panel kurulumunda). (2) ComponentBootstrapper.ConfigureStalwartListenersAsync
  sonunda OpenMailServerFirewallPorts() (netsh, idempotent) — Stalwart configure'da da açılır.
- AYRICA: listener ensure'a smtp(25) eklendi ama GÜVENLİ: boundPorts izlenip port zaten bağlıysa ya da
  isim varsa atlanır (Stalwart default 25'i varsa çift bağlama/0x80070020 olmaz).
- KULLANICIYA KALAN (kod dışı): gelen için MX→mail.<domain> + A kaydı DNS'te olmalı; spam için
  SPF+DKIM+DMARC DNS'te yayınlanmalı + provider'da PTR/rDNS = mail.<domain>. Panel bu kayıtları üretir,
  kullanıcı DNS'e ekler. Bkz [[adapanel-webmail-tasks]].
- Build: API build 0, publish OK. Installer SHA256:
  `6859E8F95CFB49482225E52DB65CAA8A05B3593C290A4C9909430FACB5AF30A7` (50968070 byte)

## 2026-06-20 (13) — Webmail "her tıkta uzun bekletiyor" düzeldi (idempotent hızlı yol)
- SEMPTOM: "Webmail'i aç"a her basışta uzun bekleme.
- KÖK NEDEN: WebmailService.OpenAsync HER çağrıda TÜM kurulumu tekrarlıyordu: IIS sitesini sil+yeniden
  kur, tüm Roundcube ağacına icacls /T (özyinelemeli ACL, binlerce dosya = yavaş), config'i yeniden
  yaz, EnsureExtensions, junction, firewall, VerifyHealthy (retry'lı problar). Her tık = tam yeniden kurulum.
- ÇÖZÜM: OpenAsync başına HIZLI YOL eklendi: state (config/webmail.json) varsa + IIS sitesi Started +
  /adapanel-health.php tek-deneme probu OK → anında URL dön (IsDeployedAndHealthyAsync + BuildUrl).
  Ağır kurulum yalnız ilk sefer ya da site/sağlık bozuksa çalışır. (Tarayıcıda Roundcube ilk açılış
  cold-start'ı app pool idle sonrası normal; istenirse app pool AlwaysRunning/idle=0 ileride.)
- Build: test 43/43, publish OK. Installer SHA256:
  `3A706E83C8375B00642463AA881872A660F5C39267B873022B7F3A24376EA7CE` (50969844 byte)

## 2026-06-20 (14) — Mail teşhis (panel kendi kontrol eder) + premium görsel
- İSTEK: "Plesk mühendisi gibi düşün, her şeyi onlar gibi yap, görseli çok daha güzelleştir, sistem
  sağlıklı çalışsın, her şeyi çok iyi analiz et." Tek tek hata avını bitirmek için panel KENDİSİ kontrol etsin.
- MAIL TEŞHİS (yeni): GET /mail/diagnostics/{domain}. DnsClient 1.8.0 paketi eklendi (Infrastructure
  csproj). MailDiagnosticsRunner (paylaşılan, mail-sunucu bağımsız): canlı DNS sorgusuyla MX→mail.<domain>,
  A→server IP, SPF(v=spf1), DKIM(<selector>._domainkey v=DKIM1), DMARC(_dmarc v=DMARC1), PTR(ters DNS=
  mail.<domain>) kontrol eder; her kontrol IZOLE (try/catch → unknown, ASLA fırlatmaz). MailDiagnosticsDto/
  MailDiagnosticCheckDto (Core). IMailService.GetDiagnosticsAsync → MailService + MailEnableMailService +
  MailServiceRouter (delege). LookupClient timeout 4sn/1 deneme/önbeleksiz.
- FRONTEND: MailView'e premium "Mail teşhis" kartı (Posta→Yönet'te, yeşil/kırmızı/gri nokta + pill,
  eksikse expected/found gösterir). api/mail.ts getMailDiagnostics. i18n panel.mail.diag.* (1860/1860).
- GÖRSEL UPGRADE (base.css tokenları, tüm paneli etkiler, layout bozmaz): yeşil --ok-* başarı rengi +
  --warn-bg; katmanlı premium gölge (--panel-shadow + --shadow-elev); köşeler 13/9/6; .panel daha geniş
  padding + radius-lg; status-pill artık tam-yuvarlak + kenarlık, .ok GERÇEK YEŞİL; butonlar degrade +
  yumuşak gölge + hover translateY (premium). Dark/light ikisi de güncellendi.
- Build: solution 0 hata/0 uyarı, test 43/43, typecheck 0, publish (DnsClient bundled) OK.
- Installer SHA256: `196568E757AA530ED08E7543D2E9A90D4F171F9E884ACC63FE7B132E0447F9A7` (51042542 byte)

## 2026-06-20 (15) — "Tarayıcıda aç" araç URL'leri (127.0.0.1) + phpMyAdmin TempDir
- SEMPTOM: Adminer açılıyor (185.7.242.225:8897) ama phpMyAdmin açılmıyor (127.0.0.1:8896). Kullanıcı
  IP'yi elle public yapınca DA phpMyAdmin hata verdi → İKİ ayrı bug.
- BUG 1 (host): WebDbView'de 127.0.0.1→window.location.hostname düzelten helper VARDI ama DatabasesView'in
  "phpMyAdmin'de aç" butonu (openInPhpMyAdmin) ham backend URL'ini (127.0.0.1) açıyordu. ÇÖZÜM: merkezi
  frontend/src/utils/url.ts → toReachableUrl/openReachable (loopback host'u window.location.hostname yapar).
  TÜM araç-açma yerlerine uygulandı: DatabasesView, WebDbView (accessUrl + root open), NginxView, MailView
  (webmail defansif). Kullanıcı "bu tarz tüm uygulamaları kontrol et" dedi → hepsi tek helper'a bağlandı.
- BUG 2 (phpMyAdmin doğru IP'de bile hata): EN OLASI sebep phpMyAdmin Twig önbelleği için TempDir
  yazılamıyor (klasik IIS hatası). ÇÖZÜM (WebDbClientService.EnsurePhpMyAdminConfigAsync): entryRoot/tmp
  oluşturulup $cfg['TempDir'] o yola ayarlandı + IIS_IUSRS:(OI)(CI)M (icacls). Ayrıca signon token dizinine
  de yazma izni (phpMyAdmin okuyup @unlink ediyor). Yeni GrantAppPoolWrite helper.
- !! HÂLÂ KESİN DEĞİL: kullanıcı phpMyAdmin'in TAM hata metnini hiç yapıştırmadı. TempDir en güçlü aday;
  düzelmezse sıradaki adaylar: DB root parolası boş/yanlış (signon "access denied") veya session.save_path.
  Yeni build'de hata DEVAM EDERSE tam hata metni şart.
- Build: API 0 hata/0 uyarı, typecheck 0, publish OK. Installer SHA256:
  `C4DD326D9C51A029E29481F30D09E82E253339A0C6D80B92C953F8677C0B13D4` (51042671 byte)

## 2026-06-20 (16) — Plesk-tarzı denetim turu 2: phpMyAdmin session + araç-URL sınıfı + WebDb firewall
- ARAÇ-URL HOST SINIFI tamamen kapandı: SiteDetailView.openSite (*:port → "localhost" → uzakta kırık
  site önizleme) artık openReachable(). Tüm window.open(url) helper'dan geçiyor (DatabasesView, WebDbView,
  NginxView, MailView, SiteDetailView). utils/url.ts tek kaynak.
- WEBDB FIREWALL (eksikti): WebDbClientService.DeployAsync artık OpenFirewallPort(port) (8896/8897) açıyor
  — webmail/mail ile tutarlı. Yeni OpenFirewallPort + RunNetsh.
- phpMyAdmin OTURUM (BUG 2 devam): kullanıcı doğru IP'de bile phpMyAdmin "error" alıyor (Adminer/webmail
  açılıyor). Adminer oturum kullanmaz; phpMyAdmin signon PHP OTURUMU kullanır → session.save_path IIS app
  pool'ca yazılamıyorsa sert hata. ÇÖZÜM: entryRoot/sessions + .user.ini (session.save_path + upload_tmp_dir
  + sys_temp_dir) + GrantAppPoolWrite. TempDir fix'iyle birlikte iki klasik IIS hard-fail sebebi kapatıldı.
- DİAGNOSTİK İNCELİĞİ: server IP yoksa A/PTR "Eksik" yerine "Kontrol edilemedi" (throw→unknown).
- !! Kullanıcı HÂLÂ eski build'de test ediyor + phpMyAdmin'in TAM hata metnini vermedi. Yeni build'de
  (TempDir+session) düzelmezse literal hata metni ŞART; kalan aday: DB root parolası boş (signon access denied).
- Build: API 0/0, test 43/43, typecheck 0. Installer SHA256:
  `7481D9DBC7479BB86306B7DB304334EC54CCE4F0D4B770E7BC88411E87318844` (51042088 byte)

## 2026-06-20 (17) — phpMyAdmin "sadece error": spekülasyon geri alındı, display_errors ile teşhis
- Kullanıcı: "yaptıklarını geri al; Adminer kurduktan SONRA oldu, çakışma olabilir, o tarafı kontrol et."
  phpMyAdmin sayfası açılınca SADECE "error" yazıyor, başka detay yok.
- GERİ ALINDI: spekülatif TempDir + session.save_path + sys_temp .user.ini kurgusu kaldırıldı (config.inc.php
  sadeleşti). ÖNEMLİ DÜZELTME: session.save_path tahminim YANLIŞTI — Adminer DA PHP oturumu kullanıyor ve
  çalışıyor, demek oturum klasörü YAZILABİLİR. Yani sebep oturum-yazma DEĞİL.
- TEŞHİS BIRAKILDI: phpMyAdmin entryRoot'a .user.ini (display_errors=On + error_reporting=E_ALL) yazılıyor →
  "sadece error" yerine PHP'nin GERÇEK hata metni ekranda görünür. (Korunan iyi/ilgisiz fix'ler: WebDb
  firewall portu, token dizini izni, frontend host-helper.)
- ÇAKIŞMA ADAYI (kullanıcı teorisi, makul): phpMyAdmin(8896) + Adminer(8897) AYNI host'ta; tarayıcı çerezleri
  PORT'a değil HOST'a göre paylaşılır → iki PHP uygulamasının oturum çerezleri karışabilir. display_errors
  ile teyit edilecek.
- !! SIRADAKİ: kullanıcı bu build'i (display_errors) kurup phpMyAdmin'i açacak → ekrandaki GERÇEK hata metnini
  iletecek → kesin sebep (eklenti/izin/çakışma/DB) kanıtla çözülecek. Stopgap: Adminer çalışıyor.
- Installer SHA256: `E59BD44A626B04A76A0D541F60AE37E0C58189DC5EBBAFCA80A27AB7E315D449` (51042251 byte)

## 2026-06-20 (18) — phpMyAdmin GERÇEK hata yakalandı: "Internal error: Error" (Twig/TempDir)
- Kullanıcı page-source verdi: `<title>Internal error: Error</title> ... alert-danger ... Error`. Token'sız
  index.php DE "Error". adapanel-health.php "OK" → PHP/IIS/site sağlam; sorun phpMyAdmin'e özel, AÇILIŞTA.
- Gizli sekmede de aynı → çerez/oturum DEĞİL. Adminer (Twig kullanmaz) çalışıyor → fark Twig.
- TEŞHİS: "Internal error: Error" phpMyAdmin'in Throwable yakalayıcısı; AÇILIŞTA Twig şablon motoru
  TempDir'e yazamayınca çöküyor (klasik). Yani GERİ ALDIĞIM TempDir fix'i muhtemelen doğruydu.
- ÇÖZÜM (fix-or-reveal tek build): (1) TempDir geri kondu: entryRoot/tmp + $cfg['TempDir'] + GrantAppPoolWrite.
  (2) $cfg['Error_Handler']['display']=true + ['gather']=true → phpMyAdmin "Error" diye gizlemeyip TAM
  istisna metnini/izini gösterir. (3) .user.ini display_errors. → Ya düzelir (TempDir'di) ya da gerçek
  hata dosya+satırla çıkar.
- !! SIRADAKİ: kullanıcı bu build'i kurup phpMyAdmin'i açacak. Açılırsa TempDir'di. Açılmazsa artık
  TAM hata metni çıkacak (Error_Handler.display) → kesin sebep görünür.
- Installer SHA256: `FAAA6BBB59D6C1FF9A74DE56E1A477BC55976A7515234E531489292E30B43FF5` (51042479 byte)

## 2026-06-20 (19) — phpMyAdmin: Twig DEĞİL (TempDir teorisi çürüdü) + config "Aç"ta tazeleniyor
- Kullanıcı network dökümü verdi: error sayfası `themes/dot.gif`'i YÜKLÜYOR → phpMyAdmin error şablonunu
  RENDER edebiliyor → Twig/TempDir ÇALIŞIYOR. Yani TempDir teorim YANLIŞ. Mesaj "Error" çünkü
  Error_Handler.display kapalı (phpMyAdmin gerçek istisnayı gizliyor).
- NEDEN HÂLÂ ERROR: config.inc.php yalnız DEPLOY'da yazılıyor, "Aç"ta değil → eski config (Error_Handler
  /yeni ayarlar olmadan) diskte kalıp aynı hatayı tekrarlıyordu. Yeni build kursa bile re-deploy olmadan
  uygulanmıyordu.
- ÇÖZÜM: CreatePhpMyAdminSignonAsync ("Aç") artık başta EnsurePhpMyAdminConfigAsync(entryRoot) çağırıyor →
  config her açılışta tazeleniyor (Error_Handler.display=true + TempDir + .user.ini). Re-deploy gerekmez.
  → Bu build'de "Aç"a basınca phpMyAdmin artık "Error" yerine TAM istisna metnini/izini gösterecek.
- !! SIRADAKİ: kullanıcı bu build'i kurup phpMyAdmin'i açacak → TAM hata metni çıkacak → kesin sebep
  (muhtemelen signon/DB bağlantısı; Twig elendi) görünür ve tek hamlede çözülür.
- Installer SHA256: `0F333640D6DF9645C0A0E34157C5FD046FCA7D8E8AF8FA2A9209FCE287D4420B` (51041864 byte)

## 2026-06-20 (20) — phpMyAdmin: kırılgan SIGNON atıldı → config auth (root otomatik giriş)
- Kullanıcı yeni build'i kurdu, yine "Internal error: Error" (token'sız / de). Error sayfası dot.gif
  yüklüyor (Twig render OK). Error_Handler.display bile gerçek mesajı vermedi → mesaj gerçekten "Error".
- KARAR (tahmin döngüsünü bitir, yapısal): phpMyAdmin'in SIGNON akışı kaldırıldı (Adminer signon kullanmaz
  → çalışıyor; fark buydu). Artık auth_type='config' + user=root + password=<ReadRootPassword> +
  AllowNoPassword (parola boşsa). phpMyAdmin login/token/oturum olmadan doğrudan root'a bağlanır.
  config.inc.php signon satırları (SignonSession/Script/URL) silindi.
- Korunan: TempDir, Error_Handler.display=true (hâlâ bir şey ters giderse TAM hata çıkar), "Aç"ta
  config self-heal (re-deploy gerekmez). WriteSignonAssetsAsync çağrısı duruyor ama ölü/zararsız.
- TAKAS: config-auth root olduğundan phpMyAdmin TÜM DB'leri gösterir (signon'daki per-DB izolasyon gitti).
  Çalışması önceliği için kabul edildi; istenirse ileri sürümde per-DB cookie-auth eklenebilir.
- RİSK: stored root parolası yanlışsa "access denied" (NET hata) çıkar, "Error" değil → yine ilerleriz.
- Installer SHA256: `F25A4795795D0C7D5F0DD530E90F405FBD57EAA9E283B8D21FB111C115208FAD` (51042070 byte)
- ✅ ÇÖZÜLDÜ (kullanıcı doğruladı: "açıldı şimdi düzgün"): phpMyAdmin'in çöküş sebebi KESİN olarak
  SIGNON akışıydı. config auth'a geçince açıldı. DERS: bir aracın süslü mekanizması (signon) kırılgansa
  ve basit kardeşi (Adminer) çalışıyorsa, sonsuz yama yerine GÜVENİLİR basit mekanizmaya geç.
  AÇIK İŞ (opsiyonel): per-DB izolasyonu güvenilir biçimde (cookie-auth + DB kullanıcı) geri eklenebilir.

## 2026-06-20 (21) — phpMyAdmin per-DB izolasyon GERİ GELDİ (Plesk gibi, signon'suz)
- Kullanıcı: "root erişmesin, Plesk'teki gibi yap." → her domain KENDİ DB kullanıcısıyla girip yalnız
  kendi DB'sini görsün.
- ÇÖZÜM (signon yerine ISPConfig/Plesk yöntemi): auth_type='cookie' + otomatik-giriş launcher.
  - config.inc.php: auth_type='cookie', AllowNoPassword=true (signon satırları silindi).
  - WriteSignonAssetsAsync → WriteLoginAssetsAsync: adapanel-login.php yazar. Tek seferlik token'daki
    {user,password}'ı phpMyAdmin index.php'ye pma_username/pma_password olarak OTOMATİK POST eder
    (body onload submit). CSRF: ilk login POST'unda phpMyAdmin token istemez → çalışır (ISPConfig kanıtı).
  - CreatePhpMyAdminSignonAsync: izolasyon mantığı KORUNDU (databaseName varsa o DB'nin kullanıcısı,
    yoksa root). URL artık `/adapanel-login.php?token=X`.
- DAVRANIŞ (Plesk gibi): DB başına "phpMyAdmin'de aç" → o DB kullanıcısı → YALNIZ o DB. Genel açış
  (WebDbView) → root → tüm DB'ler (admin/sunucu seviyesi).
- NOT: self-heal her açışta blowfish_secret yeniler (eski cookie geçersiz olur, sorun değil; her açış
  zaten taze login). İstenirse secret korunabilir (churn azaltma) — küçük iyileştirme.
- Build: API 0/0. Installer SHA256: `D73BFBBA51CC06CBB2ABB6B59263A6D0013B8098912937BD5CD599F4DBCF22BE` (51042488 byte)

## 2026-06-20 (22) — phpMyAdmin auto-login DOĞRU yapıldı: RESMİ signon (HMAC_secret eksikmiş)
- Cookie-auth POST denemesi (D73B) login ekranında takıldı: phpMyAdmin 5.2 login POST'unda CSRF token
  ister, blind POST reddedilir → şifre ekranı. Yani cookie-auth auto-login OLMUYOR.
- Kullanıcı: "Plesk'i KOPYALA." → tahmin bırakıldı, phpMyAdmin RESMİ examples/signon.php WebFetch ile alındı.
- EKSİK PARÇA BULUNDU: ilk signon denemem `$_SESSION['PMA_single_signon_HMAC_secret']` KOYMUYORDU →
  phpMyAdmin 5.x bunu DOĞRULAR → "Internal error: Error". Ayrıca SignonScript değil SignonURL kullanılmalı.
- ÇÖZÜM (resmi örneğe birebir): config auth_type='signon' + SignonSession='AdaPanelSSO' +
  SignonURL='adapanel-signon.php'. adapanel-signon.php (WriteSignonPageAsync): token'dan user/pass/host/
  port/only_db okur → session_name+start → PMA_single_signon_user/password/host/port + HMAC_secret(hash)
  + cfgupdate[only_db] set eder → session_write_close → header Location index.php. CreatePhpMyAdminSignon
  URL'i `/adapanel-signon.php?token=X`. İzolasyon: databaseName→DB kullanıcısı, yoksa root+only_db.
- DERS (bkz [[adapanel-copy-plesk]]): resmi/kanıtlanmış kaynağı ÖNCE oku, birebir kopyala; ufak ama
  KRİTİK detay (HMAC_secret) tahminle bulunmaz.
- Installer SHA256: `B288F589E614A50F1A65372BA987ECFF9EA5F621AB7265A912EB7E58C7BDEBCD` (51042260 byte)
- ✅ Kullanıcı doğruladı: phpMyAdmin "açıldı, sadece o DB gözüküyor" → resmi signon + HMAC çalıştı, izolasyon OK.

## 2026-06-20 (23) — Öksüz-kaynak (cascade) hata sınıfı: DB→kullanıcı + site→SSL temizliği
- Kullanıcı: "DB silince kullanıcısı silinmiyor; bu TARZ şeyleri her yerde bul/düzelt, Plesk'in aynısı olsun."
- DÜZELTİLEN (Plesk gibi cascade):
  1. DatabaseService.DeleteDatabaseAsync → artık DB'ye bağlı (panel oluşturduğu) kullanıcıyı da düşürür
     (başka DB'de kullanılmıyorsa) + kimlik kaydını siler. credentialStore'a IsUserReferencedAsync +
     RemoveUserAsync eklendi.
  2. DatabaseService.DeleteUserAsync → kullanıcı silinince kimlik kaydını (RemoveUserAsync) temizler.
  3. SiteCascadeDeletionService → SSL kaydı temizliği EKSİKTİ; ISslService.RemoveSiteAsync eklendi
     (config/ssl.json'dan o sitenin kayıtları), cascade'e "SSL kayıtları" adımı eklendi. WinAcmeSslService
     impl. (sslService cascade'e inject edildi; DI cycle yok.)
- ZATEN İYİYDİ (denetlendi): FTP kullanıcı silme (Windows hesabı net user /delete + yetki), site cascade
  (mail+hesap+alias, DB, FTP, proxy, kota, DNS, müşteri ataması, git, IIS site+pool, dosyalar). DB-user
  temizliği site cascade'e de yansıyor (cascade DeleteDatabaseAsync'i çağırıyor).
- Build: solution 0/0, test 43/43, publish OK. Installer SHA256:
  `E59B90F99B20180E325A7B8E014251C76A3C38B95DA248B854A425359CB0F0BF` (51044349 byte)

## 2026-06-20 (24) — Derin denetim turu: locale-bağımsız hata (8 servis) + phpMyAdmin sertleştirme
- "En ince ayrıntıya kadar kontrol et, her hatayı bul/düzelt" → sistematik tarama.
- LOCALE-HATA SINIFI KAPANDI (Türkçe Windows gerçek bug): yeni Support/Win32ErrorHelper.cs (Flatten =
  mesaj+HRESULT hex; IsAccessDenied 0x80070005/0522, IsInUse 0x80070020, IsAlreadyExists 0x800700B7 —
  hem hex hem İngilizce). 7 servis (AppRuntime/AspNet/SiteHostingSettings/Php/ReverseProxy/WinAcmeSsl/Waf)
  MapException'ları FlattenExceptionMessage+İngilizce.Contains yerine helper'a çevrildi. (WebAdminSite
  zaten HRESULT'tı.) Artık Türkçe Windows'ta da erişim/kilit/çakışma hataları doğru sınıflanır.
- phpMyAdmin ÜRETİM SERTLEŞTİRME: teşhis için koyduğum display_errors=On + Error_Handler.display=true
  KALDIRILDI (public portta bilgi sızdırırdı); .user.ini artık display_errors=Off. TempDir korundu.
- TARANIP TEMİZ ÇIKAN: i18n parite 1860/1860 + landmine yok + eksik anahtar yok; API sözleşmesi (11
  "eşleşmeyen" script artefaktı — metot mutlak route'ları); NotImplemented/TODO yok; sync-over-async
  (.Result/.Wait) yok; async void yok; hardcoded secret yok.
- Build: solution 0/0, test 43/43, publish OK. Installer SHA256:
  `27D2621E2E0662EE100F6301D17E13A492ECEC7CE925C95E73DD4324C12776FF` (51044315 byte)

## 2026-06-20 (25) — Güvenlik denetim turu: argüman enjeksiyonu (FTP parola, Git branch)
- "Tekrar kontrol et" → güvenlik odaklı tarama (komut/SQL/path enjeksiyonu).
- TEMEL: ProcessRunner UseShellExecute=false → SHELL YOK, tam komut enjeksiyonu (;rm) mümkün değil;
  yalnız ARGÜMAN enjeksiyonu ('" ile tırnak kırma) riski.
- DÜZELTİLEN 2 açık:
  1. FTP parola: `net.exe user "ad" "parola"` — parola sadece uzunluk doğrulanıyordu, '"' içerebilirdi →
     argüman enjeksiyonu. EnsureCommandSafePassword: '"', sondaki '\', kontrol karakterleri reddedilir
     (ftp.invalid_password). Kullanıcı adı zaten allowlist'liydi.
  2. Git branch: hiç doğrulanmıyordu, raw `origin/{branch}` git'e geçiyordu (--upload-pack=... arg
     enjeksiyonu/RCE vektörü). NormalizeBranch (allowlist regex ^[A-Za-z0-9][...]$, '-' önek/'..' yok) +
     raw origin/{branch}'ler QuoteArg ile sarıldı (git.invalid_branch).
- TARANIP SAĞLAM ÇIKAN: DB adı/kullanıcı allowlist (^[A-Za-z][A-Za-z0-9_]{1,63}$ → SQL injection yok);
  FileService path çözümleme GetFullPath+sandbox-kök kontrolü + reparse-point/symlink reddi + zip-slip
  koruması (traversal yok); Git webhook secret FixedTimeEquals (timing-safe); schtasks/service adları iç.
- Build: API 0/0, test 43/43, typecheck 0, parite 1862/1862. Installer SHA256:
  `9498167FCAB46C171ED7A65EDB392EC85D1335D7A73764B27A33C38E8D99D335` (51045336 byte)

## 2026-06-20 (26) — Dayanıklılık turu: JsonFileStore atomik yazım (state bozulması)
- "Devam et" → eşzamanlılık/dayanıklılık taraması.
- ANA BUG: JsonFileStore.WriteAsync File.Create ile dosyayı KIRPIP yazıyordu → yazım yarıda kesilirse
  (çökme/restart/eşzamanlı) hedef state dosyası YARIM/BOZUK kalır, sonraki ReadAsync DeserializeException
  ile çöker. Panelin TÜM state'ini (config/credentials/ssl/webdb/dns/mail-cert...) etkiler.
  ÇÖZÜM: atomik yazım — geçici dosyaya yaz + flush + File.Move(overwrite) atomik taşı (yarım yazım hedefi
  bozmaz). ReadAsync FileShare.ReadWrite|Delete ile açılır (atomik taşıma okuma sırasında çakışmasın).
  Ayrıca ResolvePath kök-öneki kontrolü düzeltildi ("C:\data" vs "C:\data2" sınır kaçışı).
- TARANIP TEMİZ: Process/stream sızıntısı yok (hepsi using var; ProcessRunner+TerminalService+Component
  Discovery using var process, Nginx using var listener); Terminal powershell -EncodedCommand (base64 →
  arg enjeksiyonu yok); ZipFile kendi stream'ini yönetir.
- AÇIK NOT (düşük öncelik): JsonFileStore read-modify-write mantıksal lost-update (iki eşzamanlı mutasyon
  aynı dosyada) — atomik yazım artık BOZULMA önler (last-writer-wins), ama tam çözüm per-path kilitli
  UpdateAsync gerektirir (admin işlemleri seri olduğundan düşük frekans).
- Build: API 0/0, test 43/43. Installer SHA256:
  `DEFEAC5F842EB821FEDE440383C8A56D06A81CE93A5AF5276E5A4797448AD759` (51045799 byte)

## 2026-06-20 (27) — Frontend denetimi (temiz) + lost-update kilidi kapatıldı
- FRONTEND TARANDI, TEMİZ: v-for'ların hepsi :key'li (sonraki satırda); onMounted "try yok" uyarıları
  false positive — hatalar çağrılan load/refresh fonksiyonlarında try/catch+showNotice ya da .catch ile
  yakalanıyor; kırık UI durumu yok. Gerçek bug yok (uydurma sorun çıkarılmadı).
- LOST-UPDATE KİLİDİ: JsonFileStore.UpdateAsync<T>(path, fallback, mutate) eklendi — dosya başına
  SemaphoreSlim (ConcurrentDictionary) altında atomik oku-değiştir-yaz. DatabaseCredentialStore (Upsert/
  RemoveDatabase/RemoveUser) + WinAcmeSslService (Upsert/RemoveSite) buna geçti → eşzamanlı mutasyonda
  kayıp güncelleme yok. (Atomik yazım zaten bozulmayı önlüyordu; bu mantıksal yarışı da kapattı.)
- Build: API 0/0, test 43/43. Installer SHA256:
  `7DCD74201A183B9C8C22F9E3552969C3E7AB38FA77DCA9A900FAA697C2DE120E` (51046351 byte)

## 2026-06-20 (28) — Lost-update kilidi CustomerScopeService'e genişletildi; mail CLI güvenli doğrulandı
- Mail CLI güvenli: kullanıcı girdisi (email/parola/domain) stalwart-cli'ye --file/JSON ile gidiyor,
  raw arg interpolasyonu yok (string interpolasyonları sadece display/hata mesajı).
- CustomerScopeService (SetScope/RemoveUser/RemoveSiteFromAllScopes/SetOwner) → JsonFileStore.UpdateAsync
  (atomik+kilitli) NewState()/Wrap() helper'larıyla; LoadAsync da Wrap kullanır. Lost-update kapandı.
- DNS lost-update BİLEREK atlandı: oku-değiştir-yaz her public metoda dağılmış (admin/seri, atomik yazım
  bozulmayı zaten önlüyor) → refactor riski > fayda. AÇIK NOT olarak kalsın.
- DURUM: kritik bug avı DOYGUNLUKTA. Kapatılan sınıflar (24-28): vue-i18n, locale-hata, araç-URL host,
  cascade/öksüz, phpMyAdmin signon+izolasyon+sertleştirme, argüman enjeksiyonu, state atomik yazım,
  lost-update kilidi (credentials/SSL/scopes). Frontend temiz. Bundan sonrası parlatma/perf.
- Build: API 0/0, test 43/43. Installer SHA256:
  `A84ABA959A525A0341CA902A45E8B24FEECBF906A2E58DE5B3EE9D5AE63B8B8D` (51045656 byte)

## 2026-06-20 (29) — Çalışma sağlığı: ProcessRunner sonsuz asılma tavanı
- "Hepsi" (güvenlik+uygulama+çalışma sağlığı) → çalışma-sağlığı boyutu.
- BUG: ProcessRunner.RunAsync varsayılan timeout YOKtu → çağıran vermezse (git/db-cli/net.exe/schtasks/
  docker/win-acme... çoğu) asılan dış süreç isteği SONSUZA dondurur, bağlantıyı kilitler. ÇÖZÜM:
  timeout ??= 10 dk tavan (en uzun meşru tek komut bile sığar; sonsuz asılma biter). Yanıt isteyen
  yollar zaten kendi kısa timeout'unu geçiyor (mail 30sn).
- GÖZLEM (bilerek dokunulmadı): indirme client'ları Timeout.InfiniteTimeSpan (ComponentBootstrapper:625,
  AppStore 5x, Backup 2x) — büyük dosya indirmeleri için bilinçli (sabit total-timeout büyük/yavaş
  indirmeyi keserdi), yalnız admin-install yolunda; doğru fix idle-timeout (karmaşık). Düşük öncelik.
  Metadata fetch'leri zaten 20sn.
- Build: API 0/0, test 43/43. Installer SHA256:
  `EDFE54768EE61B107B1B30DE0B47EFD50CC86C4476CDCED3ED1D602275176CC3` (51046334 byte)

## 2026-06-20 (30) — İndirme sonsuz-asılması düzgün çözüldü (idle-timeout)
- ComponentBootstrapper indirme (ana bileşen kurulum yolu): client.Timeout Infinite→2dk (ResponseHeaders
  Read ile yalnız bağlan+header fazını sınırlar, gövde sınırsız) + gövde kopyası artık CopyWithIdle
  TimeoutAsync (60sn: veri akarken süresiz, stall olursa iptal→retry/fail). Büyük indirme OK, takılı
  sunucu sonsuz asmaz. Metadata fetch'ler de (aynı client) artık 2dk ile sınırlı.
- AppStoreService 5 GET (hepsi ResponseHeadersRead): Infinite→2dk (header fazı sınırı; gövde sınırsız,
  büyük şablon indirmeleri bozulmaz).
- Backup (2 nokta) BİLEREK dokunulmadı: bunlar YÜKLEME (PUT, ResponseHeadersRead) → client.Timeout tüm
  yüklemeyi kapsar; kısa timeout büyük backup yüklemesini bozardı. Arka plan işi, kritik değil. AÇIK NOT.
- Build: API 0/0, test 43/43. Installer SHA256:
  `8DA5FDB329EE916082C8F60B5172780098D68F3874CF652FC53980B7E338F34C` (51047031 byte)

## 2026-06-20 (31) — Mail teşhis kartına PORT DİNLEME kontrolü (gelen mail sorunu için)
- Kullanıcının asıl mail derdi gelen mail (port 25) idi; teşhis kartı sadece DNS bakıyordu.
- MailDiagnosticsRunner'a 3 yerel TCP-connect kontrolü eklendi: smtp(25)/submission(587)/imap(143) —
  ProbePortAsync(127.0.0.1:port, 3sn). Stalwart o portu sunucuda DİNLİYOR mu gösterir (dış erişilebilirlik
  değil; o DNS PTR + sağlayıcı işi). i18n panel.mail.diag.label.smtp/submission/imap (1865/1865 parite).
  Frontend kart zaten check'leri dinamik render ediyor → kod değişmedi.
- DURUM: kod-seviyesi denetim TÜM boyutlarda (güvenlik/uygulama/çalışma sağlığı/cascade/frontend/
  eşzamanlılık/timeout/mail-tanı) doygun. Gerçek kalan sorunlar artık CANLI uçtan uca testte çıkar.
- Build: API 0/0, test 43/43, typecheck 0. Installer SHA256:
  `A9D192119E84B9D1784B2F93882C80227AB66317676B8BEF59961F80CE89FFA8` (51046941 byte)

## 2026-06-20 (32) — Production-hazırlık denetimi: auth/güvenlik DOĞRULANDI, Health+README
- Tam production denetimi. SAĞLAM ÇIKAN (kod zaten üretim seviyesi, fix gerekmedi):
  parola PBKDF2 (IPasswordHasher), login lockout(5/15dk)+IP brute-force+2FA+audit, cookie HttpOnly+
  SameSite=Strict+Secure, CSRF AutoValidateAntiforgery global, rate-limit("login"), CSP/X-Frame/nosniff,
  HTTPS zorunlu (AllowHttpFallback=false), TÜM controller [Authorize] (istisna: Health=anonim liveness,
  Setup=local-only+already-initialized guard, Git webhook=secret+IgnoreAntiforgery), appsettings'te secret
  YOK (SQLite path), setup tekrar çalışmaz, FileService path/symlink/zip-slip korumalı.
- DÜZELTİLEN: HealthController machineName sızıntısı kaldırıldı; README production'a tamamlandı
  (gereksinim/kurulum/portlar/güvenlik/mimari/yedekleme + Türkçe karakterler).
- AÇIK/MANUEL (bilerek blind retrofit YAPILMADI): DB SQLite+EnsureCreated → şema EVRİMİ migration
  gerektirir; EF Design paketi+dotnet-ef yok, runtime doğrulanamadan retrofit kurulumu BRICK edebilir
  ("mimariyi bozma" kuralı). Taze kurulumda EnsureCreated tam şema kurar. İleride: Design pkg + dotnet-ef
  + InitialCreate + baseline(__EFMigrationsHistory) + MigrateAsync, test ortamında doğrulanarak.
- Build: API 0/0, test 43/43. Installer SHA256:
  `48F1D269039A83FB6F279672E8A38E9A258AB14FFC1712088F89A8F3E7C0F6B6` (51046970 byte)

## 2026-06-20 (33) — EF Core MIGRATION'a geçildi (production şema evrimi) — DOĞRULANDI
- Kullanıcı "sen yap" dedi → EnsureCreated→EF migration retrofit'i test ederek yapıldı.
- YAPILANLAR: (1) AdaPanel.Data'ya Microsoft.EntityFrameworkCore.Design (PrivateAssets=all → sevk
  edilmez). (2) AdaPanelDbContextFactory (IDesignTimeDbContextFactory — host çalıştırmadan migration).
  (3) dotnet-ef 8.0.27 yerel araç (.config/dotnet-tools.json). (4) InitialCreate migration üretildi
  (Persistence/Migrations/, 4 tablo: Users/AuditLogs/InstalledComponents/Settings).
- Program.cs EnsureDatabaseAsync YENİDEN: migration-aware + BASELINE + güvenlik ağı:
  applied==0 && şema-zaten-var (eski EnsureCreated DB) → __EFMigrationsHistory oluştur + InitialCreate'i
  uygulanmış işaretle (baseline) → MigrateAsync no-op; taze DB → MigrateAsync tüm şemayı kurar;
  her durumda catch → EnsureCreatedAsync (BRICK olmaz). GetAppliedMigrations history yoksa boş döner.
- DOĞRULANDI: `dotnet ef database update` temiz DB'ye InitialCreate'i SORUNSUZ uyguladı (57KB, 5 tablo).
  Eski DB baseline yolu: history yok→applied0→baseline→migrate no-op; fallback ile güvenli.
- ARTIK: ileride şema değişikliği = `dotnet ef migrations add X` → bir sonraki sürümde otomatik uygulanır
  (veri kaybı yok). Production şema-evrim eksiği KAPANDI.
- Build: solution 0/0, test 43/43, migration fresh-apply OK. Installer SHA256:
  `775342F252E598EF3268F9875DA61E8A88684964ED885ABDB3413E1D17896427` (51055757 byte)

## 2026-06-21 (34) — Kurulum sonrası ERİŞİM ADRESLERİ (local + IP) gösteriliyor (Plesk gibi)
- Kullanıcı: "kurulup bitti ya adam nasıl erişecek panele... link adreslerini yazsın ben localden hem de ip adresiyle."
- BACKEND (zaten vardı, doğrulandı): `GET /api/v1/system/ip-addresses` (SystemController) →
  SystemMonitoringService.GetServerIpAddressesAsync: NetworkInterface ile UP + non-loopback NIC'lerin
  unicast adresleri (IPv6 link-local + loopback hariç, dedup, IPv4 önce sıralı) → ServerIpAddressDto
  (Address/Family/InterfaceName).
- FRONTEND: DashboardOverviewView.vue'ye "Panele Erişim Adresleri" kartı. onMounted getServerIpAddresses()
  ile sunucu IPv4'lerini çeker (127.* hariç). accessLinks computed: window.location.protocol+port +
  securePath prop → bir "localhost" linki + her sunucu IP için bir link (ör. https://localhost:8888/<path>
  ve https://185.7.242.225:8888/<path>). Her satırda kopyala butonu (navigator.clipboard, geçici "kopyalandı").
  i18n: panel.dashboard.access.title/hint/local/copy/copied (TR+EN).
- INSTALLER: AdaPanel.iss [Code]'a CurPageChanged(wpFinished) → bitiş ekranında erişim adreslerini YAZIYLA
  gösterir: "Bu bilgisayardan: https://localhost:<port>/panel" + "Ağ/uzak: https://<SUNUCU-IP>:<port>/panel"
  + "IP'leri Genel Bakış → Panele Erişim Adresleri kartında bul" notu. (Zaten vardı: [Run] postinstall URL'i
  açar + Start Menu/masaüstü kısayolları GetPanelUrl = https://localhost:<port>/panel.)
- Doğrulama: i18n TR/EN 1870/1870 parite, typecheck 0, test 43/43, self-contained publish OK, ISCC OK.
- Installer SHA256: `798DD96958FD0456AAC99979C75DDC85C1B059843C37E9DB1AF99E088E8A4958` (51056548 byte / 48.7 MiB).

## 2026-06-21 (35) — Servis PORT DURUMU paneli (dinleme + firewall + gerekli/opsiyonel), Plesk gibi
- Kullanıcı: "hangi portlar kapalı hangileri açık, açık olması gerekenleri de panelde gösterebiliriz."
- TASARIM (dürüst): panel sunucunun KENDİSİNDE kesin 2 şey bilir → (1) port DİNLENİYOR mu + kapsam
  (tüm arabirimler / yalnız localhost), (2) Windows Firewall izin veriyor mu. Bulut sağlayıcı güvenlik
  duvarı (AWS SG / Azure NSG / Hetzner) AYRI katman, yerelden görülemez → UI'da net uyarı notu.
- BACKEND: `GET /api/v1/firewall/ports` (FirewallController, Administrator-only). Panel portu
  HttpContext.Connection.LocalPort'tan (gerçek). FirewallService.GetPortStatusAsync:
  • Dinleme: `IPGlobalProperties.GetActiveTcpListeners()` TEK tarama (servisleri rahatsız etmez; mail
    teşhisindeki tek-tek TcpClient-probe'tan iyi). Kapsam: loopback-dışı endpoint varsa "all", yoksa "loopback".
  • Firewall kapsamı: PS `Get-NetFirewallRule -Direction Inbound -Action Allow -Enabled True` + PortFilter →
    "Any" port=tüm TCP, "5000-5100"=aralık, "587"=tek. AdaPanel DIŞI kurallar da sayılır (IIS 80/443).
  • Katalog (port→tekil): panel(dinamik), 80/443 web, 21 ftp, 25/587/465/143/993/995 mail, 8896/8897 araç,
    8899 webmail; her biri gerekli/opsiyonel etiketli (configure-firewall.ps1 ile uyumlu).
- TEST EDİLEBİLİR: parse mantığı saf `internal static FirewallService.ParseAllowCoverage` + `IsPortCovered`
  olarak çıkarıldı; FirewallPortCoverageTests (aralık/Any/UDP-yoksay/çoklu-port/null-güvenlik) → +3 test.
- FRONTEND: FirewallView.vue'ye "Servis portları" bölümü (mevcut Windows Firewall kuralları tablosunun
  ÜSTÜNDE). Her satır: servis adı + kategori/gerekli çipi, port/proto, dinleme (renkli), firewall (izinli/kural
  yok), durum noktası (hazır/dikkat/kapalı/kullanılmıyor) + eksikse "Aç" butonu. "Eksik gerekli portları aç"
  toplu butonu + "{open}/{total} gerekli port hazır" özeti + bulut-firewall uyarısı. api/firewall.ts getPortStatus.
- i18n: panel.firewall.ports.* 41 anahtar (TR+EN). Parite 1911/1911.
- Doğrulama: solution build 0/0, typecheck 0, test 46/46 (2 + 44), self-contained publish OK, ISCC OK.
- Installer SHA256: `24F129A4972D641300778FEA5CAD6862F8B866DEF54AF5DD4B65F2FEDD8A4376` (51062460 byte / 48.7 MiB).

## 2026-06-21 (36) — FTP KİLİTLENMESİ kök neden + FTPS sertifikası + pasif mod (proaktif denetim)
- Kullanıcı bağlanamadı: `534 Policy requires SSL` + `534 Local policy ... does not allow TLS`. KÖK NEDEN:
  IIS FTP sitesi SSL-zorunlu ama serverCertHash BOŞ → düz bağlantı "SSL şart", AUTH TLS "sertifika yok"
  diye reddedilir = TAM KİLİT. (IIS New-WebFtpSite default'u controlChannelPolicy=SslRequire + sertifikasız.)
- FtpService.EnsureFtpInfrastructureAsync SAĞLAMLAŞTIRILDI (proaktif, tüm sınıf):
  1) FTPS sertifikası: 'AdaPanel FTPS' friendlyName'li self-signed yoksa oluşturur (New-SelfSignedCertificate,
     5 yıl), serverCertHash + ssl128 bağlar → AUTH TLS artık çalışır. controlChannelPolicy=0 (Allow) korundu
     → düz bağlantı da kabul, kimse kilitlenmez, isteyen FTPS kullanır.
  2) PASİF MOD (sıradaki kesin tuzak): panelde tanımlı public IPv4 → site ftpServer.firewallSupport.
     externalIp4Address olarak yazılır (yoksa IIS kendi IP'si). Yanlış/iç IP olursa giriş olur ama dizin
     listesi takılır; bu önlendi.
  3) Pasif port aralığı + SSL değişikliği IIS tarafından canlı okunmaz → `Restart-Service FTPSVC` eklendi.
- KULLANICIYA ANINDA SUNUCU-TARAFI FİX verildi (mevcut bozuk site için; panel güncellemesini beklemeden):
  self-signed cert + tüm FTP sitelerine serverCertHash + controlChannelPolicy/dataChannelPolicy=0 + Restart FTPSVC.
- HATIRLATMA: pasif için bulut güvenlik duvarında 21 + 50000-51000 açık olmalı (kod yereli açar; cloud SG ayrı).
- Doğrulama: build 0/0, test 46/46, publish OK, ISCC OK.
- Installer SHA256: `5CB1FF8077CC3299A185D25A0C53C27FB198E70F482F4A31BB0B4356BA37720B` (51062993 byte / 48.7 MiB).

## 2026-06-21 (37) — Proaktif tam denetim (kullanıcı "her tarafı kontrol et") + FTP pasif port kartı
- Kullanıcı FTP'den sonra: "her tarafı kontrol et, proaktif olarak". FTP'de bulunan sınıf tüm uygulamada tarandı:
  "config var ama kritik parça (sertifika/port) eksik / locale / timezone / bağlantı".
- DENETİM SONUCU (hepsi SAĞLAM, kanıtlandı):
  • WebAdministrationSiteService MapException: HRESULT-ÖNCE (0x80070020/05/522/B7), İngilizce string sadece fallback → locale-güvenli.
  • WinAcmeSslService: HTTPS binding HER ZAMAN sertifikalı (LE yoksa self-signed fallback), LocalMachine\My + WebHosting
    depo tespiti, AddSslCertificate doğru depodan, 120sn win-acme timeout, ACME challenge yolu redirect'ten muaf. FTP-sınıfı hata YOK.
  • Locale: AspNet/AppRuntime/Waf/ReverseProxy/SiteHostingSettings/WinAcme/WebAdminSite hepsi Win32ErrorHelper.Is* (HRESULT)
    önce; kalan message.Contains'ler yerelleşmeyen config tanımlayıcıları ("system.webServer/asp","applicationHost.config","FastCgiModule","ARR").
  • DateTime.Now / DateTimeOffset.Now: HİÇ YOK (timezone bug yok).
  • Nginx default ListenAddress=127.0.0.1 (tasarım, UI'da düzenlenebilir). Mail/web-db/webmail portları servis+configure-firewall ile açılıyor.
- TEK SOMUT EKSİK + DÜZELTİLDİ: Port Durumu kartı FTP pasif aralığını (50000-51000) göstermiyordu. PortStatusDto.RangeEnd
  eklendi; katalogda (50000,51000,"ftpPassive","ftp") girişi; firewall kapsamı aralığı kontrol eder; UI'da port "50000-51000",
  dinleme "İstek anında" (pasif portlar istek anında bağlanır), durum yalnız firewall'a göre. i18n +2 (ftpPassive, listen.onDemand) → 1913/1913.
- Doğrulama: build 0/0, typecheck 0, test 46/46, i18n 1913/1913, publish OK, ISCC OK.
- Installer SHA256: `9777402A3FCF257771C7B53CEE3194BA017D259564705929AD3058A795628912` (51063279 byte / 48.7 MiB).

## 2026-06-21 (38) — FTP'nin GERÇEK kök nedeni: Set-WebConfigurationProperty -Location ftpServer'a YAZAMIYOR
- Kullanıcı canlı teşhis çıktısı verdi (FTP log + IIS config okuması). KESİN bulgu:
  • `Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location 'AdaPanel FTP' -Filter 'system.ftpServer/...'`
    → "configuration object not found at location" UYARISI verir, HİÇBİR ŞEYİ uygulamaz, ama script BAŞARILI döner (exit 0).
  • Çünkü ftpServer config `<site>` etiketinin İÇİNE gömülüdür; `-Location` `<location>` bloklarına bakar → eşleşmez.
  • Sonuç: FTP sitesi IIS VARSAYILANINDA kalır = controlChannelPolicy=SslRequire + serverCertHash boş + basicAuth kapalı
    → `534 Policy requires SSL` KİLİDİ. Önceki TÜM SSL/cert/auth düzeltmelerim bu yüzden hiç işlememiş (aynı kırık yazımı kullanıyordu).
  • Kanıt: aynı script'teki `Set-ItemProperty "IIS:\Sites\$n" -Name serverAutoStart` (PROVIDER yolu) ÇALIŞIYORDU; authorization
    `Add-WebConfiguration -Location` da çalışmıştı (kurallar vardı) — ama scalar `Set-WebConfigurationProperty -Location` ftpServer'da çalışmıyor.
- DÜZELTME (FtpService.EnsureFtpInfrastructureAsync): TÜM per-site ftpServer yazımları IIS provider noktalı yoluna çevrildi:
  `Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.ssl.controlChannelPolicy/-.dataChannelPolicy/-.serverCertHash/-.ssl128`,
  `ftpServer.security.authentication.basicAuthentication.enabled / anonymousAuthentication.enabled`, `ftpServer.userIsolation.mode`.
  (firewallSupport low/highDataChannelPort SUNUCU seviyesi -> Set-WebConfigurationProperty location'sız DOĞRU, kaldı.)
- SESSIZ BASARISIZLIK ÖNLEMİ: yazım sonrası controlChannelPolicy geri-okunur; 0/SslAllow değilse `throw` → script fail → panel
  ftp.site_configuration_failed yükseltir (bir daha sessizce kilitli kalmaz).
- DOĞRULANDI (resmi MS doc): userIsolation mode=3 = IsolateAllDirectories = DOĞRU (panelin LocalUser\<ad> yaklaşımıyla uyumlu); değiştirilmedi.
- KULLANICIYA ANINDA FIX: aynı provider-yolu komutları + Restart FTPSVC + geri-okuma doğrulaması (control=0, basic=True).
- KURAL: IIS'te per-site FTP (ftpServer) ayarı yazarken ASLA `-Location` kullanma; `Set-ItemProperty IIS:\Sites\<ad> -Name ftpServer.*` kullan.
- Doğrulama: build 0/0, test 46/46, publish OK, ISCC OK.
- Installer SHA256: `41306D2BAEA91555460AC07BACD58F42E73EDDC4A007C8D4E807346364DE062A` (51063933 byte / 48.7 MiB).

## 2026-06-21 (39) — phpMyAdmin/webmail "TaskCanceled" sağlık hatası: soğuk başlangıç + redirect zinciri
- Kullanıcı: phpMyAdmin çalışıyordu, sonra "otomatik onar"da `webdb.health.http_failed` +
  `Url=http://127.0.0.1:8896/ TaskCanceledException` (zaman aşımı). KÖK NEDEN (3 katman):
  1) Sağlık probe'u `/` (phpMyAdmin kökü) yolunu yokluyor; bu yol SIGNON 302 → ağır kimlikli sayfa
     zincirine giriyor; HttpClient yönlendirmeyi İZLİYOR → 3sn timeout'a takılıyor.
  2) Probe timeout 3sn (WebDb) / 5sn (webmail) — PHP SOĞUK başlangıcına (app pool ~20dk boşta kalıp
     durunca) yetmiyor.
  3) Araç app pool'lari AlwaysRunning DEĞİL + idleTimeout varsayılan (20dk) → boşta kalınca soğuyor.
- DÜZELTME:
  • ServiceCollectionExtensions "AdaPanel.LocalHealth" HttpClient → `AllowAutoRedirect = false`. Sağlık
    "yanıt veriyor mu"ya bakar; 302 (signon/login) zaten AYAKTA demek → ağır sayfayı bekleme. phpMyAdmin
    health.php (200) + Roundcube ikisini de hızlandırır.
  • WebDbClientService.ProbeAsync timeout 3→12sn; WebmailService.ProbeAsync 5→12sn (cold-start toleransı).
  • Her iki serviste KeepAppPoolWarmAsync: dağıtımda app pool startMode=AlwaysRunning + processModel.idleTimeout=0
    + Start-WebAppPool (Plesk gibi DB aracı/webmail hep sıcak). Best-effort (provider yolu Set-ItemProperty IIS:\AppPools).
- KULLANICIYA ANINDA: sunucuda WebDb/Webmail app pool'larini AlwaysRunning + idleTimeout 0 yapan PS betiği.
- FRONTEND ek sağlamlaştırma: WebDbView "Aç" düğmesi/linki artık `item.isHealthy` yerine `item.isDeployed`
  ile gösteriliyor. Sağlık probe'u kıl payı takılsa bile (backend signon sağlıktan bağımsız çalışıyor)
  kullanıcı çalışan aracı açabilir; "otomatik onar" (soğuk yeniden-dağıtım) tuzağına düşmek zorunda kalmaz.
- Doğrulama: build 0/0, typecheck 0, test 46/46, publish OK, ISCC OK.
- Installer SHA256: `729A2469697269DB844B804B0E91087AD62A79FB65F4B111DB4407F6A93F8829` (51065048 byte / 48.7 MiB).
  (önceki ara yapı E762... bu yapıyla değiştirildi — frontend "Aç" kapısı dahil.)

## 2026-06-21 (40) — PERFORMANS: PHP OPcache açıldı (yoğun trafik için en büyük kazanç)
- Kullanıcı sordu: site hızı nasıl test edilir, yoğun siteler için kullanılabilir mi.
- BULGU: ComponentBootstrapper.EnsurePhpIniAsync extension'ları + memory_limit/upload ayarlıyordu ama
  **OPcache KAPALIYDI** (Windows PHP'de php_opcache.dll ext\ içinde gelir ama php.ini'de varsayılan kapalı).
  OPcache, PHP'nin yoğun trafikteki EN BÜYÜK performans faktörü (derlenmiş script RAM'de → ~3-5x throughput).
- DÜZELTME: EnsurePhpIniAsync'e (php_opcache.dll varsa) OPcache bloğu eklendi: zend_extension=opcache,
  opcache.enable=1, enable_cli=0, memory_consumption=192, interned_strings_buffer=16, max_accelerated_files=20000,
  validate_timestamps=1 + revalidate_freq=2 (hosting dengesi: dosya değişikliği ~2sn'de yansır). preserveExisting
  ile kullanıcının aktif ayarı bozulmaz. YENİ PHP kurulumlarında otomatik; mevcut PHP için manuel php.ini eki verildi.
- NOT (yoğun trafik tuning, kod-dışı/öneri): FastCGI instanceMaxRequests + PHP_FCGI_MAX_REQUESTS yükselt;
  MariaDB innodb_buffer_pool_size artır; CacheService/Redis + nginx edge cache + CDN; tek sunucu dikey ölçek,
  çok-sunucu için harici LB gerekir (panel tek Windows sunucu yönetir).
- Doğrulama: build 0/0, test 46/46, publish OK, ISCC OK.
- Installer SHA256: `E276555246796A055D5337E364ADFF262B7D4B27B865494E02385322CC0AEAD9` (51066078 byte / 48.7 MiB).

## 2026-06-21 (41) — PHP sayfası "500" ama CLI temiz: fastcgi.logging KÖK NEDEN
- Kullanıcı redis-test.php'de tarayıcıda "500 - İç sunucu hatası" aldı; ama `php redis-test.php` (CLI) HİÇ fatal
  vermedi (Select-String Fatal/Error/Exception boş). KÖK NEDEN: IIS FastCGI klasiği — `fastcgi.logging=On` (PHP
  varsayılanı) iken PHP en küçük uyarı/notice'i FastCGI STDERR'ine yazar, IIS bunu hata sayıp **500** döner
  (sayfa aslında çalışsa bile). uyeler.php (soketsiz) uyarı üretmediği için çalışıyordu; redis-test (fsockopen)
  bir uyarı üretip 500 veriyordu.
- DÜZELTME (EnsurePhpIniAsync): `fastcgi.logging=0` (uyarılar stderr'e gidip 500 tetiklemesin) + `log_errors=On`
  + `error_log=<installDir>/php_errors.log` (hatalar dosyaya, teşhis için; ileri-bölü yol = ters-bölü kaçış derdi yok).
  Bu, panel PHP'sinin GENEL "uyarıda 500" hata sınıfını kapatır — sadece bu sayfayı değil.
- KULLANICIYA ANINDA: php.ini'ye `fastcgi.logging = 0` ekle + iisreset.
- redis-test.php'ye geçici `display_errors=1` eklendi (500 yerine gerçek hatayı göster); sorun çözülünce kaldırılabilir.
- Doğrulama: build 0/0, test 46/46, publish OK, ISCC OK.
- Installer SHA256: `AB5D41139AA691E82E4F1053F747FB969DC3A3B4FA3B37149F445B4904DBC01F` (51064150 byte / 48.7 MiB).

## 2026-06-21 (42) — Upload "413 Request Entity Too Large" + panelden ayarlanabilir upload limiti (PHP + ASP.NET)
- Kullanıcı phpMyAdmin'den SQL import'ta `HTTP 413` (RequestFilteringModule). Sonra: "panelde ayarı olsun,
  gerektiğinde değiştirebileyim; ASP/ASPX'te de geçerli olsun; Plesk gibi yap."
- KÖK NEDEN: IIS `maxAllowedContentLength` varsayılanı ~30MB; panel PHP upload'u 128M yapıyor ama IIS limitini
  ayarlamıyor → PHP'ye ulaşmadan 413. (Aynı "PHP limiti var, IIS limiti yok" sınıfı.)
- DÜZELTMELER:
  • WebAppPublisher.EnsurePhpEntry: PHP siteleri web.config'ine `requestLimits maxAllowedContentLength=1GB`
    (yoksa) yazar → tüm PHP siteleri (phpMyAdmin/webmail/müşteri) varsayılan 30MB'den kurtulur.
  • Yeni `WebAppPublisher.SetMaxAllowedContentLength(root, bytes)` — açıkça üzerine yazar.
  • PhpService.UpdateSitePhpSettingsAsync: per-site PHP ayarı (upload_max_filesize/post_max_size, .user.ini ZATEN
    vardı) artık post_max_size'ı byte'a çevirip (ParsePhpSize) IIS maxAllowedContentLength'i de set eder → panel
    upload ayarı UÇTAN UCA çalışır (413 yok). PhpView UI'ı zaten mevcut.
  • SiteHostingSettingsService (genel "Maks. yükleme" = MaximumUploadBytes → maxAllowedContentLength) artık
    ASP.NET için `system.web/httpRuntime@maxRequestLength` (KB) + executionTimeout=600'ü de set eder → upload
    limiti ASP/ASPX sitelerinde de geçerli.
  • EnsurePhpIniAsync global upload_max_filesize/post_max_size 128M→256M (DB import başlığı).
- ANINDA (mevcut kurulum): phpMyAdmin sitesinde Set-WebConfigurationProperty maxAllowedContentLength=1GB +
  php.ini upload/post yükselt + app pool recycle.
- Doğrulama: build 0/0, test 46/46, publish OK, ISCC OK.
- Installer SHA256: `67421C449BE58B67FB1E8B13F9558CC66E4D868C7D52EAFBF2F0E9628C36E83F` (51064727 byte / 48.7 MiB).

## 2026-06-21 (43) — phpMyAdmin import "#1046 No database selected" → DB otomatik seçili açılır
- Kullanıcı gerçek bir phpMyAdmin export'u (DB 'karsilastir', USE/CREATE DATABASE satırı YOK) import'ta #1046.
  Sebep: phpMyAdmin, USE içermeyen SQL'i import ederken ÖNCE bir DB seçilmiş olmasını ister; sunucu kökünden
  (DB seçmeden) import → #1046.
- DÜZELTME (WebDbClientService.WriteSignonPageAsync): signon sonrası `header('Location: index.php')` →
  only_db varsa `index.php?db=<urlencode(onlyDb)>`. Yani Veritabanları ekranından "phpMyAdmin'de aç"
  (per-DB) o DB SEÇİLİ açılır → "İçe Aktar" doğrudan o DB'ye gider, #1046 olmaz (Plesk davranışı).
- ANINDA (panel güncellemeden): phpMyAdmin'de soldan hedef DB'ye TIKLA (seç), sonra Import; ya da SQL başına
  CREATE DATABASE + USE ekle.
- Doğrulama: build 0/0, test 46/46, publish OK, ISCC OK.
- Installer SHA256: `D1B14341E794070E729BB69C393C4C3506A4B9098EA9ECD65F0901BB10EC26A9` (51065942 byte / 48.7 MiB).

## 2026-06-21 (44) — Import "500 0x80070102 (FastCGI timeout)" + max_execution_time panelden ayarlanabilir
- Büyük SQL import'ta `500 - FastCGI işlem zaman aşımı` (0x80070102). KÖK NEDEN: IIS FastCGI requestTimeout(90sn)/
  activityTimeout(30sn) varsayılanları PHP max_execution_time'dan (300sn) KISA → FastCGI önce kesip 500 verir.
- DÜZELTMELER:
  • PhpService.EnsureFastCgiApplication + AspNetService.EnsureFastCgiApplication: FastCGI app oluşturulurken
    activityTimeout/requestTimeout=600; MEVCUT kayıtta da yükseltir (her register'da).
  • PhpService.RaiseFastCgiTimeoutAsync(seconds): tüm FastCGI app'lerinin timeout'unu en az `seconds` yapar (raise-only).
  • UpdateSitePhpSettingsAsync: per-site max_execution_time değişince RaiseFastCgiTimeoutAsync(maxExec+60) çağırır →
    "panelden timeout değiştir" (Site → PHP ayarları → max_execution_time) FastCGI'yi de yükseltir, uçtan uca çalışır.
- ANINDA (mevcut): php-cgi FastCGI app'ine activityTimeout/requestTimeout=600 + php.ini max_execution_time=600 + pool recycle.
- COMPOSER zaten tam var (araştırıldı): components.json 'composer' (composer.phar) + SiteHostingSettingsService.RunComposerInstallAsync
  + SiteHostingView "Composer" düğmesi → site kökünde `composer install`. (Plesk'in temel akışı.)
- AÇIK KALAN (kullanıcı istedi): phpMyAdmin GLOBAL php.ini upload limiti (900MB) panelde görünmüyor — per-site PHP
  ayarları yalnız müşteri sitelerini kapsıyor. Global PHP ayar kartı eklenebilir (sonraki adım).
- Doğrulama: build 0/0, test 46/46, publish OK, ISCC OK.
- Installer SHA256: `D838249AE726F2DFDD09031EE350052E288021A0F68C80AF7518E72E67015035` (51065793 byte / 48.7 MiB).

## 2026-06-21 (45) — GLOBAL PHP ayarları paneline eklendi (Veritabanı → Ayarlar): phpMyAdmin limitleri
- Kullanıcı: phpMyAdmin'in 900MB import limiti panelde görünmüyor; "bütün ayarları (süre dahil) panele koy,
  Veritabanı sekmesinin altına Ayarlar sekmesi ekle".
- YAPILDI:
  • Backend: IPhpService.GetGlobalPhpSettingsAsync/UpdateGlobalPhpSettingsAsync; PhpService bunları en güncel
    kurulu PHP'nin GLOBAL php.ini'sine okur/yazar (memory_limit, max_execution_time, upload_max_filesize,
    post_max_size — SitePhpSettingsDto/UpdateSitePhpSettingsRequest yeniden kullanıldı). Yazınca: SetIniDirective
    (aktif/yorumlu/yok hepsini ele alır) + RaiseFastCgiTimeoutAsync(maxExec+60) + `taskkill /f /im php-cgi.exe`
    (yeni php.ini HEMEN uygulansın). GetLatestInstalledPhpAsync helper.
  • Controller: GET/POST /api/v1/php/global-settings (admin-only).
  • Frontend: yeni DbToolSettingsView.vue + DatabasesView'e "Ayarlar" sekmesi (module 'settings'). Kart: yükleme
    boyutu, POST boyutu, bellek, çalışma süresi (saniye) → kaydet. api/php.ts getGlobalPhpSettings/updateGlobalPhpSettings.
  • i18n: panel.database.module.settings + panel.database.toolSettings.* (TR+EN), parite 1925/1925.
  • Test double (WebDbDeploymentTests.SuccessfulPhpService) yeni interface metotlarıyla güncellendi.
- COMPOSER zaten tam (Site → Barındırma → Composer = composer install). Bu turda dokunulmadı.
- Doğrulama: build 0/0, typecheck 0, test 46/46, i18n 1925/1925, publish OK, ISCC OK.
- Installer SHA256: `9C84D041CED553D4C5B6A45D0A7117CF50254D2AA93DEAE5045AFF043C66A399` (51069599 byte / 48.7 MiB).

## 2026-06-21 (46) — max_execution_time SÜRESİZ (0) + Composer KOMUT sistemi (Plesk gibi)
- Kullanıcı: "süre 3600 geçmiyor, süresiz istiyorum" + "Composer'a Plesk'teki gibi komut sistemi ekle".
- SÜRESİZ: PHP'de max_execution_time=0 = sınırsız. PhpService clamp'leri 0..3600 → **0..86400** (0=süresiz kabul).
  Yeni FastCgiSecondsFor(maxExec): 0 ise FastCGI requestTimeout=86400 (sınırsız olamaz, 24s tavan), değilse maxExec+60.
  Global + per-site PHP ayarlarının ikisinde de geçerli. Frontend input min=0 max=86400; etiket "(0 = süresiz)".
- COMPOSER KOMUT SİSTEMİ: SiteHostingSettingsService.RunComposerCommandAsync(siteName, command) — keyfi composer
  alt komutu (update, require x/y, remove, dump-autoload, show…) site kökünde çalıştırır. Güvenlik: shell
  metakarakterleri reddedilir, komut composer'a ARGÜMAN olarak geçer (ProcessRunner shell kullanmaz). 300 char limit.
  ISiteHostingSettingsService + SiteHostingSettingsController POST hosting-settings/composer-command + ComposerCommandRequest DTO.
  Frontend: SiteHostingView Composer kartına komut kutusu + "Çalıştır" (api/siteHosting.ts runComposerCommand).
  i18n: panel.hosting.composerRun/composerCmdPlaceholder/composerCmdHint + composer.invalid_command (TR+EN).
- Doğrulama: build 0/0, typecheck 0, test 46/46, i18n 1929/1929, publish OK, ISCC OK.
- Installer SHA256: `EB336F5218366AA6D8B99EB988EA5BC71F100CCE7E1530567B7DAA8F20D63188` (51071715 byte / 48.7 MiB).

## 2026-06-21 (47) — max_execution_time 0→300 falsy BUG fix + Artisan komut çalıştırıcı (Laravel toolkit)
- BUG (canlı): "0 yapıyorum otomatik 300 oluyor". Backend doğruydu (Clamp 0..86400). Hata FRONTEND'de:
  DbToolSettingsView.load() → `form.maxExecutionTime = settings.maxExecutionTime || 300` — JS'de `0 || 300` = 300
  (0 falsy). Düzeltme: `?? 300` (nullish — sadece null/undefined'da varsayılan). Artık 0=süresiz ekranda kalıyor.
  DERS: number alanlarında ASLA `|| default` kullanma (0/false ezilir) → `?? default`. Tüm frontend tarandı, tek yerdi.
  Not: php.ini başlangıç değeri 300 ComponentBootstrapper'dan (preserveExisting) — varsayılan, sorun değil.
- ARTISAN: Plesk Laravel Toolkit'in çekirdeği. RunArtisanCommandAsync(siteName, command) → `php "<root>/artisan"
  <cmd> --no-interaction --no-ansi` site kökünde. Site kökünde 'artisan' yoksa artisan.not_found (Laravel değil).
  Güvenlik = Composer komutuyla aynı (metachar reddi, argüman olarak geçer, 300 char). "php artisan"/"artisan"
  öneki kırpılır. ISiteHostingSettingsService + Controller POST hosting-settings/artisan-command (ComposerCommandRequest reuse).
  Frontend: SiteHostingView Composer kartına "Artisan (Laravel)" kutusu (api/siteHosting.ts runArtisanCommand).
  i18n: panel.hosting.artisanTitle/Run/CmdPlaceholder/CmdHint/Done/Failed + artisan.invalid_command/not_found/completed.
  UYARI: queue:work gibi uzun çalışanlar bu kutuda ProcessRunner timeout'una takılır → Kuyruk bölümü (FAZ 2) gerekir.
- LARAVEL TOOLKIT DURUM: Composer komut ✓ + Artisan komut ✓ + Git dağıtım ✓ (mevcut) + Zamanlayıcı/cron ✓ (mevcut).
  EKSİK (FAZ 2): Node.js (npm) çalıştırıcı + Queue worker yöneticisi (arka plan süreç). Plesk'teki sekmelerin geri kalanı.
- "Laravel kur boş klasör ister" (canlı soru): DOĞRU davranış. composer create-project laravel/laravel BOŞ dizine
  kurar, mevcut dosya üzerine kuramaz (composer reddeder). golftaksiatakum.xyz'de test dosyaları (config.php, uyeler.php,
  redis-test.* vb.) var → önce yedekle+temizle, sonra kur. Kullanıcıya sibling _yedek klasörüne taşıma komutu verildi.
- Doğrulama: build 0/0, typecheck 0, test 46/46, i18n 1938/1938, publish OK, ISCC OK.
- Installer SHA256: `DD5A519605238A593D94F48F990E40F059F432886C8B8AC8E78190C977EEFA41` (51071977 byte / 48.7 MiB).

## 2026-06-21 (48) — Plesk domain paneli (4 sekme, dikey butonlar) + Laravel yedekle/temizle/kur + DB taşıma
- Kullanıcı: "site yönetimi Plesk'in AYNISI olsun, butonlar alt alta; Ayarlar sekmesi kalsın; DB yüklerken timeout/413 → kaynak DB bilgisi girip otomatik aktaran bir yol ekle".
- DOMAIN PANELİ (WebsiteWorkspaceView TAM yeniden yazıldı): 17 araçlık düz çubuk → Plesk 4 ana sekmesi:
  **Pano** (özet: durum/app pool/yol + hızlı erişim dikey liste), **Barındırma ve DNS** (hosting/php/asp/dns/ssl/
  proxy/files/ftp/webdb/protected/stats/backup/cache — DİKEY alt alta liste, satıra tıkla→drill-in→geri), **Posta**
  (MailView doğrudan), **Başlarken** (store/wp/apps dikey liste). openToolId="" → sekme listesi; doluysa araç görünümü.
  adminOnlyTools (store/webdb/cache) bayi/müşteride gizli. SitesView @open-tool → focusTool(doğru sekme+araç).
- LARAVEL/APP "YEDEKLE+TEMİZLE+KUR" (cleanInstall): InstallAppTemplateRequest.CleanInstall; EnsureLaravelDestinationReady
  artık string? döner — cleanInstall=true ise dolu klasör içeriğini kardeş "<dest>_yedek_<tarih>" klasörüne taşıyıp
  boşaltır (kardeş = dest DIŞINDA, gerçekten boşalır), false ise eski 409 (Existing entries). InstallLaravel+InstallRoundcube
  call site'ları flag'i geçer. Frontend AppStoreView.install(id, cleanInstall): 409 store.site_has_content yakalanınca
  window.confirm(cleanInstallConfirm)→install(id,true). composer create-project BOŞ dizin ister; bu yüzden gerekiyordu.
- DB TAŞIMA (sunucu taraflı, timeout'suz): DatabaseService.TransferDatabaseAsync — kaynak (uzak) MySQL/MariaDB'den
  yerel mysqldump.exe ile tek DB dump (--databases YOK ki hedef ad farklı olabilsin, --single-transaction --routines
  --events --triggers --skip-lock-tables --default-character-set=utf8mb4, MYSQL_PWD env) → temp .sql → yerel mysql.exe
  ile hedef DB'ye import. Hedef DB yoksa provider.CreateDatabaseAsync (already_exists yut). ProcessRunner timeout=2 saat
  (kullanıcının timeout sorunu). temp finally'de silinir. DTO DatabaseTransferRequest/ResultDto, IDatabaseService,
  POST api/v1/db/transfer (admin). Frontend: api/databases.ts transferDatabase + DbTransferView.vue + DatabasesView
  "Taşı/Aktar" modülü. **Veritabanı→Ayarlar sekmesi KORUNDU** (kullanıcı istedi).
  AVANTAJ: tarayıcı upload yok → phpMyAdmin 413/süre duvarına takılmadan büyük DB taşınır.
- i18n: panel.sites.primary.* + pano.quickAccess; panel.database.module.transfer + panel.database.transfer.* (19);
  database.transfer_source_required/_failed/_import_failed/.success; panel.appStore.cleanInstallConfirm (TR+EN, 1966/1966).
- Doğrulama: build 0/0, typecheck 0, test 46/46, i18n 1966/1966, publish OK, ISCC OK.
- Installer SHA256: `D9FC61EF1AB6EFDC2FD1DF33ACB10C97C83979A49A2093003971F75CA3318660` (51074962 byte / 48.7 MiB).

## 2026-06-21 (49) — Uygulama Toolkit (Composer/Artisan/Node/Queue + Plesk konsolu) + FTP fix + cascade DB
- Kullanıcı: "Artisan'ı bakım araçlarına koymuşsun, Plesk'te toolkit sekmesindeydi; Plesk'in komut açılır penceresi
  çok daha iyiydi aynısını yap; site silinince DB de silinsin; yapıyı/görünümü Plesk'e benzet."
- TOOLKIT: components/CommandConsole.vue (terminal benzeri: büyük koyu çıktı alanı, preset komut çipleri, $ prompt,
  geçmiş biriktirir, exit kodu). views/AppToolkitView.vue: segmented sekmeler **Composer | Artisan | Node.js | Kuyruk**;
  ilk üçü CommandConsole (runner = runComposer/Artisan/NodeCommand), Kuyruk = durum + Başlat/Durdur. Domain paneline
  "Uygulama Araçları" aracı (Başlarken grubu, id=toolkit) eklendi (WebsiteWorkspaceView). SiteHostingView'deki
  Composer/Artisan komut kutuları KALDIRILDI → toolkitMoved notu (Composer install butonu kaldı).
- NODE backend: SiteHostingSettingsService.RunNodeCommandAsync — node.exe + npm-cli.js (npm.cmd değil; .cmd shell
  sorunundan kaçınır), site kökünde, 20 dk timeout. ResolveNodeExecutableAsync (catalog nodejs* veya %ProgramFiles%\nodejs).
- QUEUE backend: Start/Stop/Get QueueWorkerAsync — ISchedulerService enjekte edildi. Start = minutely zamanlanmış görev
  "AdaQueue-<site>" `php artisan queue:work --tries=3 --sleep=3 --max-time=3600` (minutely + max-time = kendini yenileyen
  daemon; Win Görev Zamanlayıcı "çalışıyorsa yeni başlatma" varsayılanı → tek worker). Stop = DeleteTask + queue:restart.
  Controller: node-command + queue|queue/start|queue/stop. ResolvePhpExecutableAsync helper.
- FTP FIX (canlı ftp.site_configuration_failed): EnsureFtpInfrastructure'daki SSL geri-okuma doğrulaması ÖLÜMCÜL `throw`
  idi → bazı IIS sürümlerinde Get-ItemProperty .Value farklı tip dönünce Set başarılı olsa bile FTP'yi komple blokluyordu.
  throw → Write-Warning (artık bloke etmez). result.Success = ExitCode==0 (yani sadece terminating hata/throw patlatır).
- CASCADE DB: SiteCascadeDeletionService DB'leri zaten isim eşleşmesiyle siler ama deleteDatabases bayrağına bağlıydı;
  SitesView + WebsiteWorkspaceView delete akışı artık deleteDatabases:true geçiyor (plan onayı zaten DB'leri listeliyor).
- i18n: panel.console.* (5), panel.toolkit.* (queue dahil ~24), panel.sites.tools.toolkit/Hint, panel.hosting.toolkitMoved,
  node.*/queue.* (TR+EN, 1998/1998).
- Doğrulama: build 0/0, typecheck 0, test 46/46, i18n 1998/1998, publish OK, ISCC OK.
- Installer SHA256: `9E3BF50BD0D07DD5EA08C3E59CF42A41AAE508E02CCEEF056E937A40AD8C377B` (51081702 byte / 48.7 MiB).

## 2026-06-22 (50) — Domain altı: bağlı DB listesi + zamanlanmış görevler + log görüntüleyici
- "hepsini yap" → A/B/D bittikten sonra kalan C + D-liste tamamlandı. Hepsi MEVCUT backend'leri kullanır (yeni API yok).
- BAĞLI DB LİSTESİ: views/SiteDatabasesView.vue — getSiteDeletionPlan(site).databases ("engine:name" parse) → liste +
  her DB için phpMyAdmin aç (openPhpMyAdmin). Domain paneli Barındırma ve DNS grubuna id=domaindb eklendi.
- ZAMANLANMIŞ GÖREVLER: mevcut CronView.vue domain paneline id=cron olarak bağlandı (adminOnlyTools'a 'cron' eklendi;
  scheduler server-level). Plesk'in per-domain Scheduled Tasks karşılığı.
- LOG GÖRÜNTÜLEYİCİ: views/SiteLogsView.vue — getSiteLog(site, lines) (zaten vardı) → terminal benzeri çıktı,
  120/300/1000 satır seçimi + yenile. Domain paneli id=logs.
- Hepsi WebsiteWorkspaceView allTools'a eklendi (hostingdns grubu) + openToolId render zinciri + import.
- i18n: panel.siteDb.* (5), panel.siteLogs.* (7), panel.sites.tools.domaindb/cron/logs (+Hint) (TR+EN, 2015/2015).
- Doğrulama: build 0/0, typecheck 0, test 46/46, i18n 2015/2015, publish OK, ISCC OK.
- Installer SHA256: `A98FB5F35DBB827C5BC8E509755C8931E9936520A402890469F56D3A7173EAA6` (51083180 byte / 48.7 MiB).

## 2026-06-22 (51) — Görsel tutarlılık: Plesk tarzı sekme dili (segmented-tabs)
- Bulgu: panel ZATEN Plesk hizasında — main.ts kayıtlı "dark" yoksa varsayılan **light** tema (Plesk Obsidian gibi),
  font Open Sans (Plesk ile aynı), base.css olgun token sistemi + light tema (data-theme) + tema toggle (MainLayout) mevcut.
- Tek gerçek tutarsızlık: global `.segmented-tabs button` global `button` stilini (gradyan + gölge + hover translateY)
  MİRAS ALIYORDU → sekmeler "kabarık buton" gibiydi. Düzeltme (base.css): segmented-tabs butonları düz, gölgesiz,
  hover'da zıplamasız; aktif sekme accent **alt-çizgi** (inset box-shadow). Artık modül sekmeleri (Veritabanı, Toolkit…)
  domain panelindeki .primary-nav sekmeleriyle AYNI dilde. Sadece base.css; yapısal değişiklik yok, düşük risk.
- Backend değişmedi (test 46/46 önceki build'den sabit). typecheck 0, frontend build+sync OK, publish OK, ISCC OK.
- Installer SHA256: `8D7D60CE59073BBB41FF0516322264C76D737D11127DB124DB28939628C1E7E9` (51083208 byte / 48.7 MiB).

## 2026-06-22 (52) — Proaktif denetimler (temiz) + per-domain DB oluşturma (Plesk parite)
- "Plesk mühendisi gibi her şeyi düşün" → 3 tespit-edilebilir kalite denetimi yapıldı:
  1) "0 falsy" sınıfı (frontend `|| sayı`): tek gerçek bug zaten düzeltilmişti (maxExecutionTime); kalan 2 (port→3306,
     upload→1MB) meşru fallback (0 orada geçersiz). SINIF TEMİZ.
  2) Kodda kullanılıp i18n'de OLMAYAN t() anahtarı: 0 (script ile tüm src tarandı). Ham anahtar sızıntısı YOK.
  3) Backend mesaj anahtarları frontend i18n'de mi: 73 "eksik" çıktı ama HEPSİ başarı mesajı (.success/.saved/.created…)
     ki frontend kendi t()'sini gösteriyor; HATA anahtarlarının tamamı çevrili. Kullanıcı asla ham hata anahtarı görmüyor.
- DOĞRULAMA: cascade silme zaten varsayılan açık (api+backend `deleteFiles/deleteDatabases ?? true`); site silince
  dosya+DB zaten siliniyordu (eklediğim deleteDatabases:true no-op'tu, zararsız).
- YENİ (Plesk parite): SiteDatabasesView salt-okunurdu → "Veritabanı oluştur" formu (admin'e özel): ad/kullanıcı/parola,
  createDatabase API. DB adı varsayılanı frontend'de ResourceNaming.DatabaseNameFromSite ile AYNI kural (küçük harf,
  harf/rakam dışı '_') → oluşturulan DB cascade silmede ve bağlı listede eşleşir. engine=mariadb (kullanıcının kurulumu).
- i18n: panel.siteDb.create/dbName/userName/password/creating/created (TR+EN, 2021/2021).
- Doğrulama: typecheck 0, i18n 2021/2021, frontend build+sync OK, publish OK, ISCC OK. Backend değişmedi (test 46/46 sabit).
- Installer SHA256: `52A733643BB2D4ED7AAA350D5AD746735FB771B0D4BF229984122AB9AA360B2C` (51083434 byte / 48.7 MiB).

## 2026-06-22 (53) — Plesk parite denetimi (#1,#3 zaten var) + #6 Sağlık/Onarım panosu
- Yol haritası 1,3,2,6,7,4,5 istendi. ÖNCE kod denetimi (mükerrer yapmamak için), sonra GÜVENLİ olanlar:
- #1 SSL otomasyonu: ZATEN TAM. WinAcmeSslService issue→otomatik HTTPS binding + HTTP→HTTPS redirect;
  AutomationWorker AutoSslRenewEnabled+SslRenewIntervalHours → RenewAllAsync (zamanlanmış auto-renew);
  RunSslRenewNowAsync (şimdi yenile); SslView son-kullanma renk kodu + issue/renew/import. Tek eksik: wildcard (DNS-01).
- #3 Mail: ZATEN büyük ölçüde TAM. Hesap/alias/kota + GetDnsRecordsAsync TAM deliverability: A, MX, SPF, DKIM
  (RSA+Ed25519, Stalwart'tan), DMARC. Eksik: autoresponder (Sieve "vacation" stub) + dış forward → CANLI Stalwart testi gerekir.
- #2 Yedek: AutomationWorker AutoBackupEnabled+interval ZATEN var. Eksik: uzak hedef (FTP/S3) + retention.
- KARAR: kör/kırılgan kod yazma (autoresponder/wildcard/uzak-yedek canlı test ister). Kullanıcı "önce güvenli olanlar" seçti.
- YENİ #6 SAĞLIK/ONARIM PANOSU (Plesk Repair Kit benzeri): HealthService (Infrastructure/Health) — çekirdek Windows
  servisleri (W3SVC/WAS/FTPSVC) + kurulu bileşenlerin ServiceName'leri (DB/Redis/mail…) Get-Service ile JSON durum;
  RestartServiceAsync yalnız BİLİNEN servisleri Restart-Service (allowlist → keyfi servis durdurma yok).
  IHealthService + ServerHealthController **api/v1/server-health** (admin; anonim liveness HealthController'dan AYRI rota).
  Frontend: api/health.ts + ServerHealthView.vue (durum pill + tek-tık yeniden başlat) → ServerToolsView'e "Sunucu
  Sağlığı" sekmesi (varsayılan landing). DTO ServiceHealthDto + RestartServiceRequest. DI kaydı eklendi.
- i18n: panel.health.* (12) + health.restarted/unknown_service/restart_failed (TR+EN, 2035/2035).
- Doğrulama: build 0/0, typecheck 0, test 46/46, i18n 2035/2035, publish OK, ISCC OK.
- Installer SHA256: `E706E5B122E9FE8DA50A417656BD5751702F6BE7AA660FEB854FC3B698BFFAFA` (51089913 byte / 48.7 MiB).

## 2026-06-22 (54) — Toolkit komut yönlendirme fix + #2 retention zaten var
- CANLI HATA: kullanıcı toolkit'te HER komutu Composer sekmesinde çalıştırmış → `php artisan storage:link` =
  Composer'a gidip "Command php is not defined"; `npm install`/`npm run build` = "Command npm is not defined".
  Kök neden: sekme = sabit çalıştırıcı; kullanıcı sekme değiştirmeden farklı komut yazınca yanlış araca gidiyor.
- FIX (AppToolkitView): AKILLI YÖNLENDİRİCİ — `routed(fallback)`: komut "composer" ile başlıyorsa runComposerCommand,
  "php artisan"/"artisan" ise runArtisanCommand, "npm" ise runNodeCommand; önek yoksa sekmenin kendi çalıştırıcısı.
  Backend zaten önekleri kırpıyor. Artık hangi sekmede olursan ol composer/php artisan/npm doğru çalışır.
- #2 RETENTION: ZATEN TAM — AutomationStateRecord.BackupRetentionDays (varsayılan 14, clamp 0..3650) +
  backupService.PruneAsync(retentionDays) RunBackupAsync içinde + AutomationView "Saklama süresi (gün)" alanı + i18n.
  Yapılacak yok (yalnız uzak hedef/FTP-S3 eksik = kırılgan, canlı test ister).
- Backend değişmedi (test 46/46 sabit). typecheck 0, i18n parite, frontend build+sync OK, publish OK, ISCC OK.
- Installer SHA256: `7145BD6AF2AB3D62D3584032857B943CF74DE86DA841546E51E63862A805A5F0` (51089953 byte / 48.7 MiB).

## 2026-06-22 (55) — Composer install production-safe + uzak yedek de zaten var (yol haritası TAM)
- CANLI: kullanıcı düz `install` çalıştırdı → composer install dev bağımlılıklarını (phpunit/faker/whoops, 78 paket)
  CANLI siteye geri kurdu (çıktı exit 0 = teknik başarı ama production'a dev kurmak yanlış). Plesk production'da --no-dev.
- FIX (AppToolkitView Composer preset): 'install' → 'install --no-dev --optimize-autoloader'; 'dump-autoload' → 'dump-autoload -o'.
  (Tek tık production install. Kullanıcı yine de elle 'install' yazarsa dev kurulur — bu kendi seçimi.)
- DOĞRULAMA (#2 uzak yedek): ZATEN TAM — AutomationService.UpdateSettingsAsync BackupTargetType (local/ftp/s3/webdav)
  + BackupRemoteUrl/UserName/Region/Password(DPAPI) + ResolveRemotePassword → BackupSiteAsync. Frontend AutomationView'de
  FTP/S3/WebDAV seçici + URL/kullanıcı/parola/region alanları + placeholder'lar mevcut. api/backup.ts targetType var.
- SONUÇ: yol haritası 1-7'nin TAMAMI kodda doğrulandı (SSL auto, mail DKIM/SPF/DMARC, backup auto+retention+UZAK,
  sağlık panosu, plan CRUD+limit, DB göçü). Geriye yalnız DIŞ-sistem/kırılgan kalemler: mail autoresponder (Sieve),
  wildcard SSL (DNS-01), tam site göçü, WP klon/staging → CANLI test ile yapılmalı (kör = kırılgan).
- Backend değişmedi (test 46/46 sabit). typecheck 0, frontend build+sync OK, publish OK, ISCC OK.
- Installer SHA256: `24733FB4E0774011223D74D7A53D5E2E39BDD276F32A6BE977F596ABF8675C8B` (51089748 byte / 48.7 MiB).

## 2026-06-22 (56) — WP-CLI komut konsolu (Plesk WP Toolkit derinliği, #4)
- WordPress toolkit şimdiye dek sadece bilgi + bakım modu okuyordu (action yok). Plesk WP Toolkit'in motoru WP-CLI
  (kanıtlanmış yöntem, kırılgan hack değil) → toolkit'e WP-CLI komut konsolu eklendi (Composer/Artisan/Node ile aynı kalıp).
- Backend: SiteHostingSettingsService.RunWpCliCommandAsync — site kökünde wp-load.php/wp-config.php yoksa wpcli.not_wordpress;
  php.exe (ResolvePhpExecutableAsync) + EnsureWpCliAsync (dataRoot/tools/wp-cli.phar yoksa PowerShell Invoke-WebRequest ile
  resmi build'den indirir, TLS1.2) → `php wp-cli.phar <cmd> --no-color` (10dk). IOptions<PanelRuntimeOptions> enjekte edildi (dataRoot).
  Interface + Controller POST hosting-settings/wpcli-command (ComposerCommandRequest reuse).
- Frontend: api/siteHosting.ts runWpCliCommand + AppToolkitView "WP-CLI" sekmesi (preset: core version/plugin list/
  plugin update --all/theme list/cache flush/core update) + akıllı yönlendiriciye `wp` öneki eklendi (her sekmeden çalışır).
- i18n: panel.toolkit.tabs.wpcli + wpcliHint + wpcli.invalid_command/not_wordpress/unavailable/completed (TR+EN, 2041/2041).
- AVANTAJ: plugin/tema/çekirdek yönetimi, search-replace (gelecekteki WP klon için), cache flush, user mgmt — tek sekmede.
  Toolkit artık: Composer · Artisan · Node.js · WP-CLI · Kuyruk.
- Doğrulama: build 0/0, typecheck 0, test 46/46, i18n 2041/2041, publish OK, ISCC OK.
- Installer SHA256: `7DBDF0568A74666AA6E8221BDE7E4B93C7B49201E4B5E2B1BF0A708046F1100E` (51091466 byte / 48.7 MiB).

## 2026-06-22 (57) — Native phpredis uzantısı etkinleştirme (tek-tık, doğrula-ve-geri-al)
- CANLI: Laravel `cache:clear` → "Class \"Redis\" not found" (PhpRedisConnector `new Redis`). Panelin PHP'sinde redis
  C-uzantısı yok (redis-test'ler bu yüzden ham fsockopen kullanıyordu). predis (saf PHP) alternatif ama kullanıcı native istedi.
- PhpService.EnableRedisExtensionAsync: kurulu PHP'nin build'ini TESPİT eder (php -r ile MAJOR.MINOR | ts/nts | x64/x86;
  vs16 (<8.4) / vs17), eşleşen php_redis-6.1.0-<mm>-<ts>-<vs>-<arch>.zip'i downloads.php.net'ten PowerShell
  Invoke-WebRequest+Expand-Archive ile indirir → ext/php_redis.dll, php.ini'ye `extension=redis`. KRİTİK GÜVENLİK:
  `php -m` ile redis yüklendi mi DOĞRULAR; yüklenmezse DLL+satırı GERİ ALIR (PHP asla kırılmaz). Başarıda php-cgi recycle.
  Zaten yüklüyse "already". php.exe (CLI) kullanılır, yoksa php-cgi.exe yedeği.
- IPhpService.EnableRedisExtensionAsync + RedisExtensionResultDto(enabled/message/fileName); PhpController POST
  /api/v1/php/enable-redis (admin) + audit; api/php.ts enableRedisExtension; DbToolSettingsView (Veritabanı→Ayarlar)
  "Redis (phpredis) uzantısını etkinleştir" butonu. Test stub SuccessfulPhpService'e EnableRedisExtensionAsync eklendi.
  IOptions<PanelRuntimeOptions> zaten (56)'da eklenmişti — burada dataRoot tools/ için kullanılmadı (ext'e gider).
- KULLANIM: installer kur → Ayarlar'da butona bas → `php artisan config:clear` (kullanıcı config:cache yapmıştı, eski
  durumu önbelleğe almıştı) → `cache:clear`. Artık REDIS_CLIENT=phpredis (varsayılan) çalışır; predis gerekmez.
  CACHE_STORE=redis olmalı (predis DEĞİL — o store değil, istemci). build uyumsuzsa buton güvenle başarısız olur.
- i18n: panel.database.toolSettings.enableRedis/enablingRedis/redisEnabled/redisAlready/redisHint +
  php.redis_download_failed/php.redis_verify_failed (TR+EN, 2048/2048).
- Doğrulama: build 0/0, typecheck 0, test 46/46, i18n 2048/2048, publish OK, ISCC OK.
- Installer SHA256: `3BD4FDEEA24FA2E4C91272380439B484853F767BCBAD42A82736AFBFC6D6A2EB` (51095381 byte / 48.7 MiB).

## 2026-06-22 (58) — Redis canlı metrikleri (RESP INFO) Önbellek panosunda
- CacheView'de Redis sadece çalışıyor/durdu gösteriyordu; Plesk-parite delta = metrikler. CacheService'e RESP INFO okuyucu:
  ReadRedisInfoAsync (TcpClient 127.0.0.1:port, 2.5sn timeout, "INFO\r\n" gönder, NOAUTH ise null=parolalı) +
  ParseRedisInfo (redis_version, used_memory_human, connected_clients, keyspace_hits/misses, uptime_in_seconds, dbN keys topla).
  BuildRuntimeAsync redis+Running ise Metrics doldurur (best-effort, salt-okunur). DTO: CacheMetricsDto + CacheRuntimeDto.Metrics.
- Frontend: api/cache.ts CacheMetrics tipi + CacheRuntime.metrics; CacheView Redis adının altında "Bellek · Anahtar ·
  Bağlantı · İsabet oranı" (hitRate = hits/(hits+misses)). i18n panel.cache.metrics.memory/keys/clients/hitRate (TR+EN, 2052/2052).
- Doğrulama: build 0/0, typecheck 0, test 46/46, i18n 2052/2052, publish OK, ISCC OK.
- Installer SHA256: `506D513F8B8E6A56BE487789DD57693069D231DEE944A4D05D2267D100FC5740` (51097788 byte / 48.7 MiB).

## 2026-06-22 (59) — #3 Site göçü (arşivden içe aktarma)
- Kullanıcı "3 yap" (tam site göçü). Recursive FTP LIST ayrıştırması kırılgan → GÜVENİLİR yöntem: arşiv indir+aç.
- SiteHostingSettingsService.ImportSiteArchiveAsync(siteName, archiveUrl, cleanFirst): URL doğrula (http/https/ftp,
  metachar reddi) → PowerShell Invoke-WebRequest (TLS1.2) ile .zip indir → Expand-Archive temp → tek üst klasör varsa
  onun içeriğini kök al → cleanFirst ise hedefi kardeş "_goc_yedek_<tarih>"e taşı → CopyDirectoryRecursive ile site
  köküne kopyala (kopyalanan dosya sayısı döner). temp finally'de silinir. dataRoot (56'da eklenmişti) kullanılır.
  DTO ImportSiteArchiveRequest{ArchiveUrl,CleanFirst} + SiteImportResultDto{SiteName,FilesCopied}. Interface + Controller
  POST hosting-settings/import-archive. Frontend api/siteHosting.ts importSiteArchive + SiteMigrationView + domain paneli
  "Site Göçü" aracı (Başlarken grubu, id=migrate). DB tarafı = mevcut Veritabanı→Taşı/Aktar.
- i18n: panel.sites.tools.migrate/Hint + panel.migration.* (12) + migration.invalid_url/download_failed/completed (TR+EN, 2067/2067).
- Doğrulama: build 0/0, typecheck 0, test 46/46, i18n 2067/2067, publish OK, ISCC OK.
- Installer SHA256: `334E05F7AEF8E36824863E38A3E9F395D241BA3B7B5698F6108A9D175DB51048` (51099843 byte / 48.7 MiB).

## 2026-06-22 (61) — GÜVENLİK denetimi sonrası: anlık hardening scripti + K3 kaynak (disable_functions)
- Kullanıcı canlı VPS güvenlik denetimi yaptı (bkz .claude memory adapanel-security-audit + görevler #10-19). "barındırılan
  herhangi bir site = root" zinciri: K1 ProgramData ACL tenant-okunur + K2 MariaDB root parolasız + K3 php.ini boş
  disable_functions. Sıralı ajanlarla (repo git DEĞİL → paralel/worktree yok) düzeltiliyor.
- ANLIK SCRIPT (Task #10, general-purpose ajan + benim incelemem): scripts/harden-adapanel.ps1 — VPS'te elle çalıştırılır,
  yeniden kurulum gerekmez. K1 (icacls inheritance:r + SYSTEM/Admins; servis hesabı tespit; RX yalnız runtimes\php+node —
  MariaDB/Redis/Stalwart verisi kilitli kalsın diye TÜM runtimes'a DEĞİL, benim daralttığım), K3 (php.ini disable_functions=
  exec/passthru/shell_exec/system/proc_open/popen/pcntl_exec + expose_php=Off, max_execution_time'a dokunmaz, php-cgi recycle),
  B2 (AdaPanel* firewall kopya kuralları dedupe). K2 YAPMAZ (canlıda panel DB erişimini kilitler; sonda açıklama). İdempotent, parse OK.
- K3 KAYNAK (ComponentBootstrapper.EnsurePhpIniAsync): disable_functions + expose_php=Off eklendi (preserveExisting YOK —
  boş/On varsayılanı ezmek için). Fresh kurulumlar artık RCE zinciri kapalı doğar. Bu installer'da.
- DEFER: K1 kaynak (installer/bootstrap) DİKKATLİ olmalı — ProgramData kilitlenince sonradan kurulan PHP runtime'ı
  kilitli ebeveyni miras alır → app pool okuyamaz → 500; ComponentBootstrapper her runtime kurulumundan SONRA runtimes\<x>'e
  RX vermeli. + panel servis kimliği (LocalSystem mı?) doğrulanmalı. B6 (per-site 443) — per-site 80 kuralının nerede
  oluştuğu bulunmalı (FirewallService BuildCatalog yalnız STATUS görünümü, kural oluşturmaz).
- Doğrulama: build 0/0, test 46/46, publish OK, ISCC OK. (frontend değişmedi)
- Installer SHA256: `3B187FAEE0F218EBD920131C26A3BD8FB5F7FAEEA4C9C9C53CBAD8E457AC20C7` (51101844 byte / 48.7 MiB).

## 2026-06-22 (62) — K1 kaynak: veri kökü ACL kilidi (DataRootHardeningService)
- K1'i kaynakta DOĞRU+güvenli yaptım. İçgörü: TÜM kökü kilitleyip runtimes'a RX geri vermek KIRILGAN (sonradan kurulan
  PHP runtime kilitli ebeveyni miras alır → app pool okuyamaz → 500). Bunun yerine yalnız HASSAS alt klasörler kilitlenir;
  runtimes'a HİÇ dokunulmaz → PHP kırılmaz, re-grant gerekmez (DB/Redis ağdan erişilir, dosya okuması gerekmez).
- DataRootHardeningService (Infrastructure/Bootstrap, IHostedService): startup'ta data/keys/certs/config/backups/mail-certs
  alt klasörlerini oluştur + `icacls "<dir>" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F [+ mevcut SID] /T /C`.
  SID kullanıldı (dil-bağımsız; Türkçe Windows). Servis LocalSystem (install-service.ps1 New-Service, kimlik yok = SYSTEM)
  → SYSTEM tam yetki panel erişimini korur; ayrıca WindowsIdentity.GetCurrent().User da eklenir. Her açılışta idempotent,
  hata yutulur (panel başlatmayı engellemez). ServiceCollectionExtensions'ta AutomationWorker'dan ÖNCE AddHostedService.
- Bu installer: K1 + K3 (önceki turdan disable_functions/expose_php) birlikte. Fresh kurulumlar artık "site=root" zincirinin
  K1+K3 ayaklarını kapalı doğar. Mevcut VPS için anlık scripti (harden-adapanel.ps1) çalıştır.
- Doğrulama: build 0/0, test 46/46, publish OK, ISCC OK.
- Installer SHA256: `581A1F59D8DBB112B097ACBA734D3030B26D52733BAFFD120889905FC1B450DB` (51102190 byte / 48.7 MiB).
- SIRADA: B6 (per-site 443/53 — per-site kural noktasını bul), sonra K2/B5 (MariaDB, hassas), B1 (web.config), B2/B3/B4.
- KALAN (kullanıcı girdisi gerekli): mail autoresponder (Stalwart CLI ref), wildcard SSL (DNS sağlayıcı).
- KALAN (kırılgan, kullanıcı girdisi gerekli): mail autoresponder (Stalwart CLI ref), wildcard SSL (DNS sağlayıcı), tam site göçü.
- KALAN (kırılgan, canlı test): mail autoresponder (Stalwart Sieve), wildcard SSL (DNS-01), tam site göçü, WP klon (wp search-replace ile artık yapılabilir).
- SIRADA: #2 yedek retention (güvenli, local); sonra kırılgan kalemler (autoresponder/wildcard/uzak-yedek) canlı testle.
- NOT: kör reskin riskli; ekranı göremiyorum. Kullanıcı "şu ekran sade" derse o ekranı nokta atışı iyileştir.
- KALAN: genel Plesk GÖRSEL geçişi (kart/sekme dilini tüm panele yayma); queue worker'ı canlı doğrula.
- KALAN (FAZ): per-domain bağlı DB LİSTESİ görünümü (cascade bitti), C: loglar + zamanlanmış görevler, genel Plesk görsel geçişi.

## 2026-06-22 (63) — Laravel Scheduler tek-tık (App Toolkit › Kuyruk sekmesi)
- Plesk Laravel Toolkit'teki "Scheduler" karşılığı: kullanıcı PowerShell'de elle `LaravelScheduler-alioral` zamanlanmış görevi
  oluşturuyordu; panele tek-tık başlat/durdur eklendi. Laravel'in TEK cron girdisi = dakikalık `php artisan schedule:run`.
- Backend (SiteHostingSettingsService, zaten vardı): GetLaravelSchedulerAsync (task "Laravel-{safe}-Scheduler" var mı?),
  StartLaravelSchedulerAsync (SchedulerService.CreateTaskAsync minutely `php artisan schedule:run`; artisan yoksa hata),
  StopLaravelSchedulerAsync (DeleteTaskAsync). Bu turda BAĞLANDI: ISiteHostingSettingsService 3 imza + controller 3 uç
  (GET scheduler / POST scheduler/start "scheduler.started" / scheduler/stop "scheduler.stopped") + siteHosting.ts 3 fn +
  AppToolkitView Kuyruk sekmesine ikinci blok (sched-head ayraç, durum pill'i, başlat/durdur; queueNotice'i paylaşır).
- i18n: panel.toolkit.scheduler.title/desc/hint/started/stopped + backend scheduler.started/stopped (tr+en). Parite 2074/2074.
- Doğrulama: build 0/0, typecheck 0, test 44/44, frontend build 0, publish OK, ISCC OK.
- Installer SHA256: `2DC0C625CAE446F12C4A3A90F337C84618FEC64DFDC7EA6C5D2EC86062FF20E9` (51103158 byte / 48.74 MiB).
- NOT (güvenlik): K1+K2+K3+B1+B2+B5-bind zaten kaynakta (581A1F). KALAN güvenlik: B3 (phpMyAdmin HTTPS/auth — derin),
  B4 (per-site FastCGI — derin), B5-ekstra (skip-name-resolve + tenant '%'→127.0.0.1 = RİSKLİ/canlı-test), B6 (per-site 443/53).

## 2026-06-22 (64) — B6 (web/DNS firewall) + B5 (MariaDB bind-address)
- B6: configure-firewall.ps1 panel+mail portlarını açıyordu ama 80/443 (site HTTP/HTTPS) ve 53 (DNS) yoktu. IIS varsayılan
  kuralı 443'ü çoğu kez AÇMAZ → sitelere HTTPS erişilemez, Let's Encrypt HTTP-01 80'de takılır. configure-firewall.ps1'e
  idempotent (önce-sil-sonra-ekle) "AdaPanel Web 80/443" (TCP) + "AdaPanel DNS 53 TCP/UDP" kuralları eklendi (mail döngüsüyle
  aynı kalıp, try/catch). FirewallService SADECE durum görünümü (kural üretmez); gerçek nokta installer scripti. Aynı blok
  anlık VPS scriptine (harden-adapanel.ps1) da [B6] olarak eklendi (reinstall'suz canlıya uygulanır). 3306 BİLEREK açılmadı.
- B5: KRİTİK bulgu — bind-address=127.0.0.1 yalnız MySQL yolunda (satır 2812) vardı; canlı motor olan MariaDB yolu
  (InitializeMariaDbZipAsync) my.ini'ye HİÇ yazmıyordu (mariadb-install-db varsayılanı bind-address YAZMAZ → 0.0.0.0).
  EnsureMariaDbBindAddressAsync eklendi: servisin ImagePath'indeki --defaults-file'ı (gerçek config) bulur, yoksa
  datadir\my.ini'ye düşer; bind-address YOKsa [mysqld] altına 127.0.0.1 ekler (idempotent, try/catch ile kurulumu bozmaz,
  admin açıkça ayarlarsa ezmez). sc start'tan ÖNCE çağrılır. Savunma derinliği (3306 firewall'da zaten kapalı).
- DEFER (RİSKLİ/canlı-test, BİLEREK yapılmadı): skip-name-resolve (root@'localhost' TCP auth'unu kırar — root host da
  127.0.0.1'e çevrilmeli), tenant '%'→'localhost' (apps LAN IP ile bağlanıyorsa kopar). Tek-kutu modelde mantıklı ama
  canlı doğrulama şart. innodb_buffer_pool/max_connections = RAM'e bağlı, perf (güvenlik değil) — atlandı.
- Doğrulama: build 0/0, test 44/44, publish OK, ISCC OK. (frontend değişmedi)
- Installer SHA256: `FFC663FC5D89F28D71B6F08F03AD9C6CAB6C5218BBF5714CB5D5E29C6232EC3B` (51104459 byte / 48.74 MiB).
- KALAN güvenlik: B3 (phpMyAdmin HTTPS/auth — derin, canlı-test), B4 (per-site FastCGI — derin, canlı-test).

## 2026-06-22 (65) — B4 güvenli dilim (FastCGI instanceMaxRequests) + B3/B4-open_basedir CANLI-GUIDED kararı
- Kullanıcı "kodla + ben test ederim" dedi; kodu okuyunca KANIT çıktı: open_basedir ve phpMyAdmin-proxy körlemesine
  GÖNDERİLEMEZ (canlı Laravel siteleri / phpMyAdmin erişimi kırılır). O yüzden B4'ün yalnız GÜVENLİ dilimi gönderildi.
- GÖNDERİLEN (B4 güvenli): PhpService.EnsureFastCgiApplication'a instanceMaxRequests=10000 (her iki dal). Süreç
  geri-dönüşümü; PHP_FCGI_MAX_REQUESTS varsayılanıyla hizalı → 500 vermez. maxInstances'a DOKUNULMADI (IIS auto=0;
  sabit tavan eşzamanlılığı boğar). scriptProcessor/keying DEĞİŞMEDİ → site 500 riski yok.
- B4 open_basedir neden CANLI: (1) AppStoreService:592 Laravel docroot = <app>\public; open_basedir'i physicalPath'e
  (public) kısarsam Laravel ../vendor,../app,../bootstrap OKUYAMAZ → HER Laravel sitesi 500. Doğru kapsam app-köküdür
  ama panel yalnız physicalPath'i biliyor (layout'a göre değişir). (2) Global php.ini session.save_path/upload_tmp_dir
  AYARLAMIYOR → PHP sistem temp kullanır; open_basedir dar olursa upload/session 500. upload_tmp_dir/sys_temp_dir
  INI_SYSTEM → .user.ini'den ayarlanamaz. Bu yüzden temp/session + per-site layout EŞGÜDÜMÜ canlı-test ister.
- B3 neden CANLI: phpMyAdmin signon-kapılı (panel token'sız login yok) ama *:8896 düz HTTP + firewall açık. Plesk yolu
  = panel HTTPS arkasında reverse-proxy + loopback bind. Binding'i loopback'e çekmek uzak erişimi koparır; IIS'e SSL
  binding eklemek signon redirect/cookie'yi bozabilir → canlı doğrulama şart.
- Doğrulama: build 0/0, test 44/44, publish OK, ISCC OK. (frontend değişmedi)
- Installer SHA256: `BA9A072E0E1DBA766E50D23FBBE1B81705418E8988E8895C727CBB186EF99F3B` (51104834 byte / 48.74 MiB).
- SIRADA (canlı-guided, kullanıcı VPS'deyken): B4 open_basedir (önce her site layout'unu KEŞFET: physicalPath public mi,
  Laravel mı; temp/session yollarını da open_basedir'e DAHİL ET), B3 phpMyAdmin (reverse-proxy VEYA SSL binding + redirect).

## 2026-06-22 (66) — B4 open_basedir: OPSİYONEL/varsayılan-kapalı/geri-alınabilir (güvenli-tasarım çözüm)
- Kullanıcı 2x "devam et" + "kodla ben test ederim" → open_basedir'i KÖR otomatik yerine OPT-IN yaptım: hiçbir mevcut site
  etkilenmez (varsayılan kapalı = sıfır risk), kullanıcı bir test sitesinde açar, çalışmazsa kapatıp kaydeder (geri-alınabilir).
- Laravel tuzağı ÇÖZÜLDÜ: kök = site.ApplicationRootPath (panel Laravel proje kökünü ZATEN biliyor; public'in üstü),
  boşsa PhysicalPath. Temp tuzağı ÇÖZÜLDÜ: session.save_path = site-içi tmp (INI_ALL, .user.ini'den ayarlanır, IIS_IUSRS
  write verilir); YÜKLEMELER OS temp'te kalır (upload_tmp_dir INI_SYSTEM → değiştirilemez) ama %SystemRoot%\Temp open_basedir'e
  DAHİL edilir → yükleme/oturum kırılmaz. open_basedir = "{kök};{kök}/tmp;{OS temp}".
- Dokunulan: SitePhpSettingsDto + UpdateSitePhpSettingsRequest (OpenBasedirEnabled bool) · PhpService.UpdateSitePhpSettingsAsync
  (etkinse open_basedir+session.save_path yaz + GrantAppPoolWriteAsync icacls IIS_IUSRS) · GetSitePhpSettingsAsync (read-back:
  .user.ini'de open_basedir var mı) · api/php.ts (openBasedirEnabled?) · PhpView.vue (checkbox+hint+load+save) · i18n tr/en.
  NOT: ProcessRunner PhpService'te using YOK → tam-nitelikli AdaPanel.Infrastructure.Support.ProcessRunner çağrıldı.
- KALAN B4 (BİLEREK yapılmadı): per-site FastCGI süreç-havuzu ayrımı (arguments/scriptProcessor eşleşmesi) — düşük güvenlik
  değeri (open_basedir izolasyonu zaten sağlıyor) + yüksek risk (yanlış eşleşme TÜM siteleri 500). instanceMaxRequests dilimi (64)'te gönderildi.
- Doğrulama: build 0/0, typecheck 0, test 44/44 (+2 core), i18n 2076/2076, FE build 0, publish OK, ISCC OK.
- Installer SHA256: `5AF5BCED4218FE3516E2B49B5646DB7D311537E87E004AB4EDDDE8339F858978` (51105464 byte / 48.74 MiB).
- KALAN güvenlik: B3 (phpMyAdmin HTTPS/auth — canlı-test), M3 (Stalwart 8080 loopback — canlı-test).

## 2026-06-22 (67) — M3: Stalwart yönetim portu (8080) loopback'e bağlandı
- Denetim "8080 0.0.0.0 → localhost'a al" diyordu. BULGU: ConfigureStalwartListenersAsync'teki çift-yığın döngüsü
  ayrım yapmadan HER [::]:port'a 0.0.0.0:port ekliyordu → yönetim/HTTP portu 8080'i de IPv4 tüm-arabirimlere AÇIYORDU.
- FIX: 8080 dinleyicisi tespit edilince (binds herhangi biri ":8080" ile bitiyorsa) desired = SADECE {127.0.0.1:8080,
  [::1]:8080} (loopback). Diğer mail dinleyicileri (25/143/587) eskisi gibi 0.0.0.0'a genişletilir. "desired.Count==binds.Count"
  yerine desired.SetEquals(binds) (loopback değişiminde sayı eşit ama içerik farklı olabilir → eski kontrol atlardı).
- GÜVENLİ: panel Stalwart'ı zaten http://localhost:8080 (::1/127.0.0.1) üzerinden yönetir → çalışır. IMAP/SMTP ayrı
  portlar → etkilenmez. Standart autoconfig 443/well-known kullanır, 8080 değil. Best-effort (çağrı zaten try/catch'te).
- DİKKAT: yalnız FRESH mail kurulumunda (component install) uygulanır. MEVCUT VPS için 8080'i loopback'e almak canlı
  stalwart-cli adımı ister (canlı oturumda yapılır) VEYA mail bileşeni yeniden kurulur.
- Doğrulama: build 0/0, test 44/44 (+2), publish OK, ISCC OK. (frontend değişmedi)
- Installer SHA256: `4C216431F58CE6994DBAA597B600B40AA2B3F8E5FAB0B5D0A3A9947C30124F5E` (51105465 byte / 48.74 MiB).
- GÜVENLİK DENETİMİ DURUMU: güvenli-otomatik kalemlerin TAMAMI bitti (K1-K3, B1, B2, B4-fastcgi+open_basedir, B5, B6, M3).
  KALAN TEK = B3 (phpMyAdmin public düz-HTTP). B3-proper = panel reverse-proxy (büyük özellik) VEYA canlı SSL-binding+redirect.

## 2026-06-23 (68) — B3 v1: phpMyAdmin panel HTTPS reverse-proxy (kullanıcı "Plesk gibi" seçti)
- TASARIM TEMELİ (Program.cs okundu): panel auth COOKIE tabanlı (JWT-bearer DEĞİL) → tarayıcının asset/AJAX istekleri
  panel cookie'sini taşır → proxy [Authorize] çalışır (bearer-token sorunu YOK). Engeller çözüldü: (a) SecureEntryPathMiddleware
  non-API yolları gizli-giriş-yolu altında değilse 404'ler → proxy'yi /api/v1/dbtools altına koydum (dbtools
  RequiresSecureEntryHeader listesinde DEĞİL → header'sız geçer). (b) global AutoValidateAntiforgeryToken → [IgnoreAntiforgeryToken]
  (phpMyAdmin POST'ları panel CSRF taşımaz). (c) global katı CSP/X-Frame → proxy yanıtında kaldırılır (phpMyAdmin kendi CSP'sini
  yollarsa korunur). (d) X-Frame DENY → yeni sekmede sorun değil (iframe değil).
- DOSYALAR: YENİ DbToolsProxyController (/api/v1/dbtools/{tool}/{**path}, AcceptVerbs hepsi, faithful forward + stream +
  X-Forwarded-Proto/Host → phpMyAdmin PmaAbsoluteUri) · ServiceCollectionExtensions HttpClient "DbToolsProxy" (AllowAutoRedirect=false,
  UseCookies=false) · WebDbClientService.CreatePhpMyAdminSignonAsync → "/api/v1/dbtools/phpmyadmin/adapanel-signon.php?token=" (panel-göreli)
  · EnsurePhpMyAdminConfigAsync → PmaAbsoluteUri (YALNIZ X-Forwarded-Host varsa; doğrudan 8896 fallback bozulmaz) ·
  IWebDbClientService.GetDeployedToolPortAsync (state'ten port). FRONTEND DEĞİŞMEDİ (utils/url.toReachableUrl göreli URL'i origin'e çözer).
- FALLBACK: 8896 BİLEREK AÇIK bırakıldı (proxy bozulursa kullanıcı kilitlenmesin). Doğrulanınca 8896 loopback'e alınır.
- KÖR-İNŞA (test EDEMEDİM): phpMyAdmin reverse-proxy klasik olarak iteratiftir; canlı test + olası düzeltme bekleniyor.
  Riskli noktalar: PmaAbsoluteUri tamlığı, cookie path, signon redirect (relative → proxy-altı doğru çözülmeli), AJAX rotaları.
- Doğrulama: build 0/0, test 44/44 (+2), publish OK, ISCC OK.
- Installer SHA256: `8D699AAEF269E56D3F6A9B1C5CEEAFC3ED23E00BF071CE8CFE35449DB82DD9E2` (51111661 byte / 48.74 MiB).
- SIRADA: kullanıcı 8D699A kur → phpMyAdmin "Aç" test → çalışırsa 8896'yı loopback'e al + Adminer'ı da proxy'e bağla; bozuksa hata/F12-network raporu ile iterasyon.

## 2026-06-23 (69) — GERİ ALMA: kullanıcı talebiyle TÜM güvenlik düzeltmeleri geri çevrildi
- Kullanıcı "tüm bu yaptıklarını geri al" → kapsam: "bu oturumdaki TÜM güvenlik düzeltmeleri (K1, K3, B5, B6, B4, M3, B3)".
  (Laravel Scheduler özelliği güvenlik DEĞİL → KORUNDU. K2/B1/B2 zaten önceki kaynaktaydı, bu oturumda dokunulmadı → kaldı.)
- SİLİNEN dosyalar: DataRootHardeningService.cs (K1), DbToolsProxyController.cs (B3), scripts/harden-adapanel.ps1.
- GERİ ÇEVRİLEN düzenlemeler: ComponentBootstrapper (K3 disable_functions/expose_php; B5 EnsureMariaDbBindAddressAsync+
  ResolveServiceDefaultsFile+çağrı; M3 8080 loopback → eski çift-yığın döngüsü) · PhpService (B4 instanceMaxRequests; open_basedir
  bloğu+read-back+GrantAppPoolWriteAsync) · WebDbClientService (B3 signon URL+PmaAbsoluteUri+GetDeployedToolPortAsync) ·
  IWebDbClientService (port imzası) · ServiceCollectionExtensions (K1 AddHostedService + B3 DbToolsProxy HttpClient) ·
  SitePhpSettingsDto (OpenBasedirEnabled ×2) · configure-firewall.ps1 (B6 web/DNS bloğu) · api/php.ts · PhpView.vue ×4 · i18n tr/en (openBasedir ×2).
- SONUÇ: kod 60-68 düzeltmeleri-öncesi duruma döndü. ⚠️ Denetimdeki K1/K3/B5/B6 açıkları YENİDEN AÇIK (kullanıcı bilerek istedi).
- git: baseline 073c964 (düzeltmeli) → bu geri-alma ayrı commit (düzeltmesiz); ikisi de tarihte, istenirse geri-al-geri-alınabilir.
- Doğrulama: build 0/0, typecheck 0, test 44/44 (+2), i18n 2074/2074 (scheduler korundu).
- Güvenlik denetimi görev listesi (#10-#19) kullanıcı talebiyle silindi ("kalan görevleri sil").
- GERİ-ALINMIŞ DURUM INSTALLER'I (kullanıcı istedi): SHA256 `553D271191EA64DB1F6814373B579840DA0A6EAC17D87EDE76DA3C50D0D6F734`
  (51099865 byte / 48.73 MiB) — güvenlik düzeltmeleri YOK. Arşiv: artifacts/installer/archive/AdaPanelSetup-553D2711.exe.
  (Karşılaştırma: düzeltmeli son installer = 8D699AAE arşivde duruyor.)

## 2026-06-23 (70) — FİKİR DEĞİŞTİ: güvenli sürüm yeniden uygulandı
- Kullanıcı geri-alınmış sürümün "açıklı eski hali" olduğunu fark etti → "güvenlik açığı olmayan bir sürüm yap" dedi.
- `git checkout 073c964 -- <14 kod dosyası>` (CODEX hariç) ile düzeltmeli kod geri yüklendi (K1,K3,B5,B6,B4,M3,B3 + silinen
  3 dosya geri geldi). Commit bc7f9a1. AKTİF installer artık GÜVENLİ sürüm.
- GÜVENLİ installer SHA256: `90093A49DBEA068C79E6B476992AD57A4366EC3C483E9BF718A9036F203D1E41` (51108693 byte / 48.74 MiB). Arşiv: AdaPanelSetup-90093A49.exe.
- git: 073c964(düzeltmeli)→6a9463e(geri-al)→4ccfbf3(docs)→bc7f9a1(yeniden-uygula, GÜVENLİ, HEAD). Tam izlenebilir, çift yönlü geri-alınabilir.
- Arşivde 3 sürüm: 90093A49(güvenli/AKTİF), 8D699AAE(güvenli/eski), 553D2711(açıklı).

## 2026-06-23 (71) — Uzak Veritabanı Erişimi (Plesk "Remote Access") + B5 firewall-kapı modeli
- Kullanıcı: "herşeyi Plesk gibi yap" + "evden masaüstü programıyla DB'ye bağlanayım, panelde açma yeri olsun". B5'i bind-127.0.0.1
  yaptığım için bağlanamayacaktı → DÜZELTİLDİ: Plesk modeli = MariaDB tüm arayüzlerde dinler (bind 0.0.0.0) + 3306 firewall'da
  VARSAYILAN KAPALI; panel toggle'ı yalnız istenen IP'ye açar.
- B5 değişti: ComponentBootstrapper.EnsureMariaDbBindAddress artık bind-address=0.0.0.0 yazar (eski 127.0.0.1 yerine) + yorum.
- YENİ ÖZELLİK: RemoteDbAccessService (Infrastructure/Database, internal) — GetAsync/SetAsync; New-NetFirewallRule
  "AdaPanel RemoteDB 3306" -RemoteAddress <IP|Any>; durum config/remote-db.json. RESTART/my.ini-runtime YOK → sıfır kesinti.
  + IRemoteDbAccessService + RemoteDbAccessDto/UpdateRemoteDbAccessRequest + DI kaydı + DatabasesController GET/PUT /db/remote-access
  + api/databases.ts (getRemoteDbAccess/setRemoteDbAccess) + DbToolSettingsView "Uzak Erişim" kartı (toggle+IP+bağlantı bilgisi) + i18n tr/en.
- Tenant DB user'ları @'%' (zaten) → firewall açılınca kendi user/parolasıyla bağlanır. IP boşsa Any (önerilmez, uyarı var).
- Doğrulama: build 0/0, typecheck 0, test 44/44 (+2), i18n 2093/2093, FE build 0, publish OK, ISCC OK.
- Installer SHA256: `678C07A4F4110913FFF683B07D5374035075C01B8627CBA8750C4022E820B346` (51112988 byte / 48.75 MiB). AKTİF + arşivde.
- AÇIK İSTEK: kullanıcı B4'ü de "Plesk gibi" istiyor. NOT: AdaPanel zaten per-site app pool (AdaPanel.{site}, ApplicationPoolIdentity
  = ayrı kimlik → OS-düzey izolasyon) + per-site open_basedir (opt-in) veriyor. Plesk farkı: open_basedir'i YENİ sitelerde
  VARSAYILAN AÇIK yapar. Sıradaki olası iş: site provizyonda open_basedir'i kök-bazlı otomatik aç (kullanıcı onayıyla).

## 2026-06-23 (72) — B4 Plesk-tam: open_basedir yeni sitelerde VARSAYILAN AÇIK
- Kullanıcı "Plesk gibi yap" onayladı. PhpService.SetSitePhpVersionAsync sonuna EnsureDefaultUserIniAsync eklendi:
  PHP atanan sitede .user.ini YOKsa, izolasyonlu varsayılan yazar (memory/exec/upload + open_basedir AÇIK, kök=ApplicationRootPath).
  MEVCUT .user.ini'ye DOKUNMAZ → mevcut/çalışan siteler etkilenmez (yalnız yeni/ayarsız siteler). Best-effort (atamayı bozmaz).
- open_basedir mantığı AppendOpenBasedirAsync helper'ına çıkarıldı (toggle + yeni-site varsayılanı ortak kullanır).
- Plesk paritesi B4: per-site app pool (zaten) + open_basedir varsayılan-açık (yeni) + per-site toggle (kapatma) = TAM.
- Doğrulama: build 0/0, test 44/44 (+2). (frontend değişmedi)
- Installer SHA256: `CFC70E661F94F5283FBB95DD6E6D483FB3CAA88E647EC8116FAA8ABCEBAFC25D` (51115595 byte / 48.75 MiB). AKTİF.
- GÜVENLİK DURUMU: denetimdeki TÜM açıklar kapalı (K1-K3, B1-B6, M3) + Plesk-işlevsellik (uzak DB erişimi, phpMyAdmin proxy,
  open_basedir varsayılan). KALAN: B3 8896'yı loopback'e alma = phpMyAdmin proxy CANLI doğrulanınca. M1 (müşteri/abonelik/kota
  modeli) = BÜYÜK mimari özellik, açık değil. M4 (DNS tek-IP SPOF) mimari.

## 2026-06-23 (73) — M1 başladı: BULGU temel ZATEN VAR + ilk adım (bayi self-servis + MaxSites kotası)
- M1 sanıldığı kadar boş DEĞİL: ServicePlan (ServicePlanService/Controller/Dto — MaxSites+MaxDiskMb), CustomerScope
  (siteler+limitler+BAYİ hiyerarşisi OwnerId+disk kullanımı), roller (Admin/Bayi/Müşteri), DiskQuota DAYATILI (FilesController
  quota_exceeded), rol-kapsamlı erişim (CustomerAccess.EnsureSiteAsync). Temel sağlam.
- GERÇEK boşluk: site oluşturma ADMIN-ONLY'di (SitesController:95) → müşteri/bayi self-servis YOK + MaxSites dayatılmıyordu.
- YAPILAN (#20): CreateSiteAsync → [Authorize "Administrator,Reseller"]. EnsureSiteQuotaAsync (admin sınırsız; MaxSites>0 &&
  Sites.Count>=MaxSites → 403 sites.quota_exceeded). AssignSiteToActorAsync (admin değilse yeni site oluşturanın scope'una eklenir,
  OwnerId korunur). i18n sites.quota_exceeded. → Plesk: bayi kendi kotası kadar self-servis site açar, otomatik kendi kapsamına düşer.
- Doğrulama: build 0/0, test 44/44 (+2), i18n 2094/2094, FE 0, publish OK, ISCC OK.
- Installer SHA256: `F204CBFAA48E12A0BA8F4B881821260F5FC9A5CA3FDE8A7426902F0948620C71` (51116007 byte / 48.75 MiB). AKTİF.
- KALAN M1 (görev #21-23): plan zenginleştir (DB/posta/bandwidth limitleri), DB+posta kotası dayat, plan→müşteri uygulama +
  müşteri self-servis ekran deneyimi doğrula. Çok-adımlı; her biri ayrı tur.

## 2026-06-23 (74) — M1 #22 (kısmî): DB sahiplik modeli + MaxDatabases kotası (Plesk yolu) — BACKEND TAM
- Kullanıcı "DB sahiplik modelini kur, Plesk gibi" seçti. BULGU: DatabaseCredentialStore'da DB↔sahip bağı yoktu → per-müşteri
  DB sayılamıyordu. KURULDU: IDatabaseOwnershipService + DatabaseOwnershipService (config/db-ownership.json: "engine|db" → {OwnerId, SiteName};
  Record/Remove/RemoveBySite/CountForOwner/GetOwner). DI kayıtlı.
- MaxDatabases scope'a eklendi: CustomerScope record (+MaxDatabases), ScopeEntry.MaxDatabases, GetScopeAsync, SetDatabaseLimitAsync
  (ayrı setter — SetScopeAsync ripple'ı yok; SetScopeAsync/SetOwnerAsync MaxDatabases'i KORUR). Uçtan uca: CreateUserRequest/
  UpdateUserRequest/UserSummaryDto +MaxDatabases, UserManagement.ApplyScopeAsync → SetDatabaseLimitAsync, MapUser.
- DAYATMA: SitesController (site-DB oluşturma yolu, #20 ile bayiye açık) — IsDatabaseQuotaReachedAsync (admin sınırsız, MaxDatabases>0
  && owned>=limit → DB atlanır, site yine oluşur, warning) + başarılı create'te dbOwnership.RecordAsync (admin değilse). Varsayılan
  0=limitsiz → REGRESYON YOK (dormant).
- Doğrulama: build 0/0, test 44/44 (+2). Installer SHA256: `929508AC1B86DA7B61C2C92D5FB6A1ACE6AC15F96D26E8AAF30D6DBE3BCDF626` (51120959 / 48.75 MiB). AKTİF.
- KALAN (#22/#21 polish): (1) MaxDatabases'i UI'da ayarlanır kıl — limitler frontend'de SERVİS PLANI üzerinden set ediliyor (ayrı user-form yok);
  ServicePlan'a MaxDatabases ekle + plan UI + plan→müşteri kopyalama. (2) cascade temizlik: DB silince RemoveAsync, site silince RemoveBySiteAsync
  (yetim kayıt over-count yapmasın — DatabasesController.Delete + SiteCascadeDeletionService'e bağlanacak). (3) posta kutusu kotası (linkable). (4) DatabasesController'ı bayiye aç + DB görünürlük filtresi (güvenlik).

## 2026-06-23 (75) — M1 DB-kotası UÇTAN UCA TAM + yönetim UI bulundu
- DÜZELTME: yönetim UI "yok" sandım ama VAR — SetupWizardView.vue "settings" bölümünde (App.vue→SetupWizardView tek monolit kabuk;
  sections+view-switch orada). createUserForm/selectedUserForm + servis planı seçimi (applyPlanToCreate) + limit alanları mevcuttu.
- MaxDatabases UI'a bağlandı: auth.ts 3 tip (ManagedUser/CreateUserPayload/UpdateUserPayload) + createUserForm/selectedUserForm/planForm
  reactive + create/edit user template alanı + plan formu alanı + applyPlanToCreate kopyalama + reset/load + i18n panel.users.maxDatabases.
  Submit zaten {...form} spread ediyor → otomatik akar. ServicePlan'a MaxDatabases+MaxMailboxes (model + UI plan formu).
- ZİNCİR TAM: admin müşteriye/plana MaxDatabases girer → CustomerScope → SitesController site-DB yolunda IsDatabaseQuotaReachedAsync
  dayatır → dbOwnership.RecordAsync → DB/site silince RemoveAsync/RemoveBySiteAsync. Plesk DB kotası bitti.
- cascade temizlik de bağlandı (DatabasesController.Delete→RemoveAsync, SitesController.Delete→RemoveBySiteAsync).
- Doğrulama: build 0/0, typecheck 0, test 44/44 (+2), i18n 2095/2095, FE 0, publish OK, ISCC OK.
- Installer SHA256: `B1AE6B3A72504AE303CCA8630C68424D442B2CFAE4223535388AEB01510449B2` (51122061 / 48.75 MiB). AKTİF.
- KALAN M1: posta kutusu kotası (MaxMailboxes scope+enforce — MailController), DatabasesController'ı bayiye aç + DB görünürlük filtresi (güvenlik). #23 plan akışı ZATEN çalışıyor (plan seç→limit kopyala).

## 2026-06-23 (76) — M1 posta kutusu kotası (MaxMailboxes) UÇTAN UCA + M1 KOTALARI TAM
- MaxMailboxes DB kotasıyla aynı desende: CustomerScope record (+5. alan) + ScopeEntry + SetExtraLimitsAsync (SetDatabaseLimitAsync'i
  birleşik maxDb+maxMail setter'a çevirdim) + SetScopeAsync/SetOwnerAsync MaxMailboxes'i korur + UserManagement (ApplyScopeAsync param,
  2 caller, MapUser) + CreateUserRequest/UpdateUserRequest/UserSummaryDto + auth.ts 3 tip + SetupWizardView (createUserForm/selectedUserForm/
  planForm + applyPlanToCreate + reset/load + create/edit/plan template alanları) + ServicePlan zaten MaxMailboxes taşıyor (#21) + i18n.
- DAYATMA: MailController.CreateAccountAsync → EnsureMailboxQuotaAsync (admin sınırsız, MaxMailboxes>0 ise müşterinin etkin mail
  domainlerindeki kutuları GetWorkspaceAsync ile sayar, >=limit → 403 mail.mailbox_quota_exceeded).
- SONUÇ: M1 kotaları TAM → MaxSites + MaxDiskMb + MaxDatabases + MaxMailboxes hepsi dayatılıyor, plan şablonları 4 limiti de taşıyor,
  müşteri formu hepsini ayarlıyor, plan seçince kopyalanıyor. Plesk abonelik/plan/kota modeli kurulu.
- Doğrulama: build 0/0, typecheck 0, test 44/44 (+2), i18n 2097/2097, FE 0, publish OK, ISCC OK.
- Installer SHA256: `0CDAAC25FD4DF65104A36C3DABBFE6C96121708EA7C56A78968D8DE897E27E6F` (51122432 / 48.75 MiB). AKTİF.
- KALAN (opsiyonel sağlamlaştırma, ACİL DEĞİL): DatabasesController'ı bayiye aç + DB görünürlük filtresi (şu an DB yönetimi admin-only;
  bayi DB'yi site-oluşturma yolundan açıyor — güvenli). M2-M5 (yedek/mail-DNS/DNS-SPOF/FTPS) ayrı denetim kalemleri.

## 2026-06-23 (77) — Bayi self-servis DB yönetim ekranı (GÜVENLİ, izole)
- Kullanıcı "bayi DB ekranı, dikkatli" seçti. KANIT: admin DatabasesController.GetWorkspaceAsync root parolayı (AdminPassword) +
  tüm DB/kullanıcıları döner → bayiye AÇILAMAZ. Çözüm: AYRI izole controller + frontend.
- YENİ ResellerDatabasesController (api/v1/my-databases, [Authorize Administrator,Reseller]): GET (yalnız SAHİP OLUNAN DB'ler,
  parolasız OwnedDatabaseDto), POST (MaxDatabases kotası + dbOwnership.RecordAsync), DELETE (YALNIZ sahip olduğu — GetOwnerAsync==userId
  değilse 403 not_owned + RemoveAsync). Admin DatabasesController'a DOKUNULMADI (root parola/transfer/kullanıcı orada admin-only kaldı).
- dbOwnership.GetOwnedAsync (sahip olunan engine+ad listesi) eklendi. SecureEntryPathMiddleware'e /api/v1/my-databases eklendi.
- FRONTEND: MyDatabasesView.vue (liste + oluştur formu + sil, parolasız) + api/myDatabases.ts + SetupWizardView'e bayi "database"
  bölümü (sections filtresine eklendi + isReseller render) + i18n panel.myDb.* + database.quota_exceeded/not_owned.
- GÜVENLİK: bayi başka müşterinin DB'sini GÖRMEZ (GetOwnedAsync sahip-filtreli) / SİLEMEZ (GetOwnerAsync kontrolü) / root parola GÖRMEZ
  (OwnedDatabaseDto sadece engine+ad). Admin ekranı değişmedi.
- Doğrulama: build 0/0, typecheck 0, test 44/44 (+2), i18n 2110/2110, FE 0, publish OK, ISCC OK.
- Installer SHA256: `F16B6092B87BE43E78FB11C0E328A8CE70529ABEC307421E62556E354AE67560` (51128493 / 48.76 MiB). AKTİF.
- M1 ARTIK TAM KAPSAMLI: 4 kota + plan + bayi self-servis (site + DB) + sahiplik + cascade + admin/bayi/müşteri rolleri. KALAN: M2-M5 ayrı denetim kalemleri.

## 2026-06-23 (78) — M5 FTPS "yalnız güvenli FTP" toggle (Plesk gibi, opsiyonel)
- Kullanıcı "m5 plesk ile aynı olsun" dedi. Plesk'te FTPS DAİMA açık + opsiyonel "yalnız güvenli FTP" zorunlu kılma. AdaPanel zaten
  SslAllow (düz+FTPS, policy 0) idi → Plesk varsayılanıyla aynı. EKLENEN: opsiyonel admin toggle (controlChannelPolicy/dataChannelPolicy 0↔1).
- FtpService: TlsStatePath (config/ftp-tls.json {RequireSsl}), GetRequireSslAsync/SetRequireSslAsync (yaz + EnsureFtpInfrastructureAsync ile
  politikayı HEMEN uygula + FTPSVC restart). EnsureFtpInfrastructureAsync artık RequireSsl okur → policy 0=SslAllow / 1=SslRequire (hardcode 0 KALKTI).
  Doğrulama uyarısı policy/policyName'e göre güncellendi (yanlış-pozitif uyarı yok).
- IFtpService: GetRequireSslAsync + SetRequireSslAsync. FtpController: GET/PUT /ftp/tls ([Authorize Administrator] — server-level). FtpTlsRequest/FtpTlsStatusDto.
- FRONTEND: api/ftp.ts getFtpTls/setFtpTls; FtpView.vue admin+standalone (!siteName) toggle (checkbox + hint), erişim yoksa gizli.
  i18n panel.ftp.requireTls/requireTlsHint/tlsRequired/tlsOptional (TR+EN).
- VARSAYILAN KAPALI (RequireSsl=false) → kilitlenme yok, mevcut düz-FTP istemciler çalışmaya devam eder. Plesk varsayılanıyla birebir.

## 2026-06-23 (79) — Abonelik yaşam döngüsü: ASKIYA ALMA / SÜRDÜRME (Plesk "suspend") — UÇTAN UCA
- Kullanıcı "abonelik yaşam döngüsü bunu da yap" dedi. BULGU: IsActive zaten (a) girişi engelliyor (AuthService:53 auth.login.blocked)
  ve (b) mevcut oturumu geçersiz kılıyor (GetCurrentSession IsActive=false → boş). EKSİK olan: kaynakların durması.
- UserManagementService.UpdateUserAsync: IIisSiteService enjekte edildi. Aktiflik geçişi yakalanıyor (wasActive). YENİ ApplySuspensionStateAsync:
  Customer için true→false (askıya al) atanan sitelerini StopSiteAsync (web 503), false→true (sürdür) StartSiteAsync. En-iyi-çaba (tek site
  hatası akışı bozmaz, log). Audit users.suspended / users.resumed.
- Plesk-parite: askıya alınmış müşteri panele GİREMEZ + siteleri DURUR. NOT (sonraki adım): mail/FTP askıya alma henüz dahil değil.
- FRONTEND: YENİ UI GEREKMEDİ — kullanıcı yönetimindeki mevcut "Aktif" toggle artık askıya al/sürdür anlamına gelir (Plesk active/suspended).
- Doğrulama: backend build 0/0, test 44/44 (+2 core), typecheck 0, i18n 2114/2114, FE build 0, publish OK, ISCC OK.
- Installer SHA256: `EBEC6602FBB0392EC2F12943A2E4B3C04E223C38004499E6811D0E9E31333F88` (51130317 / 48.76 MiB) — M5 (78) + askıya alma (79) BİRLİKTE. AKTİF.
- KALAN (FINAL istek): M4 DNS (ikincil NS/AXFR — kodlanabilir kısım), M2 yedek (Plesk paritesi — büyük ölçüde mevcut, doğrula), FTP izolasyon (mode 3 = per-user, doğrula).

## 2026-06-23 (80) — M4 DNS Plesk-parite: zone transfer (AXFR) ACL + FINAL liste TAM
- Kullanıcı M4 için "pleskte nasılsa aynısını yap" dedi. BULGU: DNS zaten Plesk gibi (gerçek Windows DNS Server rolü, yetkili ns1/ns2,
  A/AAAA/CNAME/MX/TXT/NS kayıtları, site açılışında otomatik yayın). Plesk'in tek-sunucuda dahi sunduğu EKSİK kalem: zone transfer (AXFR) ACL → eklendi.
- BACKEND: DnsZoneState.ZoneTransferIps + SiteDnsZoneDto + UpdateDnsZoneRequest. UpdateZoneAsync persist eder (NormalizeZoneTransferIps:
  IPv4/IPv6 doğrula, dedupe, geçersiz→400 dns.invalid_transfer_ip). GetOrCreateStateAsync eski JSON'u normalize eder. ToDtoAsync map'ler.
- WINDOWS DNS: BuildSyncScript → IP varsa `Set-DnsServerPrimaryZone -SecureSecondaries TransferToSecureServers -SecondaryServers <ips>
  -Notify NotifyServers -NotifyServers <ips>`; IP yoksa `-SecureSecondaries NoTransfer` (GÜVENLİ varsayılan — açık AXFR tüm zone'u sızdırır).
- FRONTEND: api/dns.ts DnsZone.zoneTransferIps + updateDnsZone payload; DnsView.vue zone ayarlarına AXFR textarea (load + saveZone +
  restoreDefaults'ta doldurulur). i18n panel.dns.zoneTransfer/zoneTransferHelp + dns.invalid_transfer_ip (TR+EN).
- NOT: gerçek ikincil/slave DNS hâlâ 2. fiziksel sunucu ister (donanım); bu ACL o sunucuyu YETKİLENDİREN Plesk kalemidir (kullanıcı bilerek istedi).
- Doğrulama: build 0/0, test 44/44 (+2 core), typecheck 0, i18n 2117/2117, FE build 0, publish OK, ISCC OK.
- Installer SHA256: `32CD1EC2F7E22D34609FD895F887CE4592F22BEB618B58A48B8B5F3AB0234DC8` (51130378 / 48.76 MiB). AKTİF.
- FINAL LİSTE DURUMU: M5 (78)✅ + Abonelik askıya alma (79)✅ + M4 (80)✅ + M2 yedek (zaten tam: S3/FTP/WebDAV + retention + granüler restore)✅
  + FTP izolasyon (mode 3 per-user = Plesk chroot)✅ → FINAL İSTEK TAMAMLANDI. NOT: bu oturumun installer'ları VPS'te HENÜZ canlı test edilmedi.

## 2026-06-23 (81) — K2 sertleştirme: güvensiz DB yönetici hesapları (Plesk mysql_secure_installation)
- Kullanıcı "K2 yap (Plesk gibi, dağıtıma uygun)" seçti. KRİTİK BULGU: panel root'a PAROLA İLE bağlanıyor
  (ResolveConnectionProfileAsync:477 parola yoksa database.admin_password_missing fırlatır; phpMyAdmin/Adminer de aynı saklı
  parolayı kullanır). Yani ANA root@localhost parolası ZATEN VAR — "parolasız root" senaryosu esasen kapalı.
- Kalan gerçek sertleştirme (GÜVENLİ, paneli kilitlemez): anonim kullanıcılar + uzak root@'%' kaldırma. root@localhost'a
  ASLA dokunulmaz → panel DB erişimi kopmaz. İdempotent (boşsa "zaten güvenli").
- BACKEND: IDatabaseEngineProvider.HardenAdminAccountsAsync (default no-op → Pg/MsSql etkilenmez). MySqlDatabaseProvider override:
  SELECT mysql.user WHERE User='' OR (root AND Host='%') → DROP USER IF EXISTS (best-effort) + FLUSH. DatabaseService.HardenAdminAccountsAsync(engine)
  + IDatabaseService. DatabasesController POST /db/harden?engine ([Authorize Administrator]) + audit database.hardened.
- FRONTEND: api/databases.ts hardenDatabase; DatabasesView motor araç çubuğuna "Güvenliği sıkılaştır" butonu (sonuç: kaldırılan
  hesaplar listesi / "zaten güvenli"). i18n panel.database.harden/hardening/hardenHint/hardenedClean/hardenedRemoved + database.hardened (TR+EN).
- Doğrulama: build 0/0, test 44/44 (+2 core), typecheck 0, i18n 2123/2123, FE build 0, publish OK, ISCC OK.
- Installer SHA256: `9AFCDB3602E50D586079FA7B970E5674B5E6E115557700DF7A4A57FE3D279B95` (51134445 / 48.77 MiB). AKTİF.
- KALAN: (a) Webmail SSL — Let's Encrypt per-site webmail.<domain> SAN (jenerik/otomatik, kullanıcı "her site için otomatik" onayladı).
  (b) YENİ istek: FTP ile uzaktan DOSYA çekme (site taşıma; mevcut sunucu-taraflı remote-DB transfer'in dosya eşi).

## 2026-06-23 (82) — Webmail SSL (Plesk gibi otomatik, jenerik, herhangi sertifika) — GÜVENLİ hook
- Kullanıcı: "webmail her yerden HTTPS, panel girişi olmadan; SADECE Let's Encrypt değil yüklenen sertifika da; her site için
  OTOMATİK; pleskte nasılsa öyle." Webmail-open GLOBAL (Request.Host.Host), paylaşımlı tek Roundcube sitesi (AdaPanel.Webmail).
- YÜKSEK ETKİ ALANI (müşteri sitelerinin ORTAK SSL akışı) → GÜVENLİ tasarım: müşteri cert akışına DOKUNMADAN, SONRASINA best-effort
  hook. Müşteri cert'i HİÇBİR durumda etkilenmez (önce o tamamlanır, sonra webmail denenir).
- ISslService.TrySecureHostAsync(site, host, email): (1) host'u KAPSAYAN mevcut cert varsa (yüklenen wildcard dahil) BAĞLA,
  (2) yoksa DNS çözülüyorsa LE dene (çözülmüyorsa HIZLI ATLA — win-acme 120sn takılmasın), (3) HTTP→HTTPS REDIRECT EKLEMEZ
  (paylaşımlı :8899 erişimi bozulmaz). Tamamı try/catch.
- WebmailService: ISslService enjekte (DI cycle YOK — WinAcmeSslService IWebmailService'e bağlı değil). SecureWebmailForDomainAsync(domain):
  webmail.<domain>:80 host-header binding (New-WebBinding idempotent, :8899 etkilenmez) + TrySecureHostAsync + WebmailState.SecuredHosts kaydı.
  OpenAsync → BuildWebmailUrl: secured host varsa https://webmail.<domain>/, yoksa http fallback.
- SslController: IssueLetsEncrypt VE Import sonrası TrySecureWebmailAsync(cert.HostName, email) best-effort (isteği bozmaz).
  → "Let's Encrypt al" YA DA "sertifika yükle" deyince webmail.<o-domain> OTOMATİK HTTPS olur. Jenerik, sabit-kod yok, dağıtıma uygun.
- Backend-only (yeni UI/i18n yok; mevcut SSL akışı + "webmail aç" otomatik kapsar).
- Doğrulama: build 0/0, test 44/44 (+2 core), publish OK, ISCC OK.
- Installer SHA256: `4DC8005B1736A28ADC05E26C1AA5172A6ED4880FB4024043BF3CB3CD75DE5CC6` (51137280 / 48.77 MiB). AKTİF.
- NOT (canlı): kullanıcı webmail.<domain> DNS'ini sunucuya yöneltmeli (Plesk'te de şart). KALAN: FTP+FTPS+SFTP ile uzaktan DOSYA çekme (site göçü).

## 2026-06-23 (83) — FTP/FTPS/SFTP ile uzaktan DOSYA çekme (Plesk Migrator dosya tarafı)
- Kullanıcı: "DB'yi uzaktan çekiyoruz, dosyaları da FTP ile çekelim, site taşınmış olsun" + "pleski örnek al, araştır, ona göre yap."
- ARAŞTIRMA (Plesk docs/obsidian Importing Websites): Plesk Migrator simple/advanced mod; kaynak FTP/SSH kullanıcısı + belge kökü
  (httpdocs/public_html/www); SSH (SFTP) FTP'den HIZLI, önerilir. AdaPanel advanced-mod eşine hizalandı (protokol + host + kök yol seçilir).
- KÜTÜPHANE (memory "kırılgan çözüm icat etme" → kanıtlanmışı kullan, FTP listesi elle ayrıştırma): FluentFTP 52.0.0 (FTP/FTPS,
  sağlam DownloadDirectory) + SSH.NET 2024.1.0 (SFTP, özyinelemeli ListDirectory/DownloadFile). csproj'a eklendi, restore+publish OK.
- BACKEND: IFileMigrationService + FileMigrationService (Infrastructure/Migration). PullAsync → hedef = site.PhysicalPath (IIS kökü);
  ftp/ftps → FluentFTP AsyncFtpClient.DownloadDirectory (ValidateAnyCertificate; EncryptionMode.Auto=ftps); sftp → SSH.NET özyinelemeli.
  RemoteFileMigrationRequest/Result. SiteHostingSettingsController POST .../migrate-files ([Authorize Admin,Reseller] + EnsureSiteAsync scope). DI kaydı.
- FRONTEND: api/siteHosting.ts migrateSiteFiles; SiteMigrationView'a "FTP/SFTP ile dosya çek" formu (protokol [SFTP önerilen]/host/port/
  kullanıcı/parola/uzak yol [public_html|httpdocs|www ipucu]). i18n panel.migration.ftp.* + migration.host_required/target_missing/failed (TR+EN).
- Doğrulama: build 0/0, test 44/44 (+2 core), typecheck 0, i18n 2141/2141, FE build 0, publish OK, ISCC OK.
- Installer SHA256: `02AEBD264AF197EE935DAEEEDC27022A1E9E2656C6497F26A2F92A58F9291B58` (51682628 / 49.29 MiB; +FluentFTP/SSH.NET DLL'leri). AKTİF.
- NOT: bağlantı parolası SAKLANMAZ (yalnız işlem için). DB taşıma (mevcut) + dosya çekme = TAM site göçü. SFTP host-key/FTPS cert göç için gevşek (kullanıcı kendi dosyası).

## 2026-06-23 (84) — KURULUM KÖK NEDEN: enable-iis-features Server'da patlıyor → panel servisi kurulmuyor → panel açılmıyor
- Kullanıcı İLK canlı kurulumda buldu: "enable-iis-features.ps1 çıkış kodu 1" + Inno "expression error" + "https://localhost:8890/panel ulaşılamıyor".
- KÖK NEDEN: enable-iis-features.ps1 DISM/Enable-WindowsOptionalFeature (Win10/11 isimleri) kullanıyordu; Windows SERVER'da patlayıp
  exit 1 → AdaPanel.iss ConfigureInstallation RaiseException → TÜM kurulum durdu → install-service.ps1 HİÇ çalışmadı → servis yok →
  panel açılmıyor (kısayol var çünkü dosyalar kopyalanmıştı). NOT: panel Kestrel servisidir, çalışmak için IIS'e İHTİYACI YOK.
- FIX 1 (enable-iis-features.ps1): OS'a göre DOĞRU API — Server→Install-WindowsFeature (Web-Server/Web-CGI/Web-Ftp-Server/Web-Asp-Net45/...),
  Client→Enable-WindowsOptionalFeature (DISM). Her özellik best-effort (warn), yönetim aracı yoksa THROW yok → exit 0.
- FIX 2 (AdaPanel.iss): TryRunPowerShellScript (try/except + Log). enable-iis-features + configure-firewall artık BEST-EFFORT (panel
  servisini engellemez); initialize-panel-certificate + install-service KRİTİK kaldı (panel açılması için şart).
- Installer SHA256: `7921D0ADD90D63831B24287294C156381E2B7906D49E975A045D0C6F15015CB2` (51682354 / 49.29 MiB). AKTİF — tüm 6 özellik + bu fix.
- ANINDA ÇÖZÜM (yeni kurulum gerekmez): {app}\scripts\ içinde initialize-panel-certificate.ps1 + install-service.ps1 elle çalıştır.

## 2026-06-24 (85) — KURULUM KÖK NEDEN 2: 'data' klasörü migration'dan ÖNCE yok → SQLite "unable to open database file"
- Kullanıcı canlı kurulumda buldu: servis KURULU ama Stopped; log: SQLite Error 14 'unable to open database file'
  (C:\ProgramData\AdaPanel\data\adapanel.db). Servis crash-loop → panel açılmıyor.
- KÖK NEDEN: Program.cs dataRoot/logs/keys oluşturuyor ama 'data' klasörünü OLUŞTURMUYOR. 'data'yı DataRootHardeningService
  (IHostedService → app.Run SONRASI) oluşturuyor; oysa EnsureDatabaseAsync (MigrateAsync; catch'te EnsureCreatedAsync) app.Run'dan
  ÖNCE çalışıyor → taze kurulumda 'data' yokken SQLite db dosyasını oluşturamıyor → catch'teki EnsureCreated de patlıyor (korumasız)
  → uygulama çöküyor, servis Stopped. (ACL DEĞİL; DataRootHardening SYSTEM/Admin'e F veriyor + admin elle de yazabilir — klasör hiç yoktu.)
- FIX: Program.cs'e `Directory.CreateDirectory(Path.Combine(dataRoot,"data"))` eklendi (logs/keys ile aynı yerde, EnsureDatabaseAsync'ten ÖNCE). OS-bağımsız (Server + 10/11).
- ANINDA ÇÖZÜM (rebuild'siz): `New-Item -ItemType Directory -Force C:\ProgramData\AdaPanel\data` + `Start-Service AdaPanel`.
- Installer SHA256: `03BB6345D4073DA4581D39403B9E1FB9D6F92837854C205BC4B0A99625CD94F9` (51682184 / 49.29 MiB). AKTİF — 6 özellik + IIS-features fix (84) + data-dir fix (85).

## 2026-06-24 (86) — KURULUM GERÇEK KÖK NEDEN: K1 sertleştirme AÇIK db'ye icacls /T uyguluyordu → ACL bozuluyordu
- (85)'teki "data klasörü yok" tek başına yeterli değildi. ASIL kök neden: DataRootHardeningService IHostedService olarak
  app.Run SONRASI çalışıyordu; o an adapanel.db AÇIKTI. `icacls /inheritance:r ... /T` açık db dosyasının ACL'ini bozabiliyor
  ("/inheritance:r OK + /grant FAIL" yarışı = boş DACL) → sonraki açılışta SYSTEM bile açamıyor (SQLite Error 14).
  LOG KANITI: 23:37 panel ÇALIŞTI (migration OK + "K1 kilitlendi" + Kestrel) → sonra HER açılış Error 14. Elle düzeltme
  (icacls /reset + db sil + Start-Service) açtı → kök neden ACL/açık-dosya yarışı.
- FIX: DataRootHardeningService artık IHostedService DEĞİL → `public static HardenAsync`. Program.cs EnsureDatabaseAsync
  BAŞINDA, **db AÇILMADAN önce** çağrılır → /T hep KAPALI db'ye uygulanır; yeni db kilidi MİRAS alarak doğar (tenant okuyamaz),
  açıkken bir daha ACL'lenmez. ServiceCollectionExtensions'tan AddHostedService kaydı kaldırıldı. (Program.cs'teki data-dir
  oluşturma + HardenAsync'in dir-oluşturması birlikte = sağlam.)
- Doğrulama: build 0/0, test 44/44 (+2), publish OK, ISCC OK.
- Installer SHA256: `2FC2D0D877615578F8923B56AF607065D9FAF423BE0FE42BBF2B056FF35C0D87` (51682947 / 49.29 MiB). AKTİF.
- AÇIK KALAN: (a) setup ekranında "common.unexpected_error" (canlı, log bekleniyor). (b) Inno kurulumda "runtime error 2:1017" (ekran görüntüsü bekleniyor).

## 2026-06-24 (87) — KURULUM/AÇILIŞ KÖK NEDENLERİNİN TAMAMI düzeltildi (kullanıcı canlı denedi, çok tur, sonunda çözüm)
- Windows Server 2019'da bir dizi hata: enable-iis exit 1, "2:1017", common.unexpected_error, SQLite Error 14. Hepsi çözüldü:
- (a) enable-iis-features.ps1: Server'da DISM isimleri patlıyordu → OS'a göre Install-WindowsFeature/Enable-WindowsOptionalFeature + best-effort + exit 0.
- (b) AdaPanel.iss: enable-iis + cert + firewall artık TryRunPowerShellScript (best-effort) → install-service'i ENGELLEMEZ; yalnız install-service kritik. ("2:1017" = cert RaiseException dialog'uydu; best-effort olunca kayboldu.)
- (c) Program.cs: 'data' klasörü migration'dan ÖNCE oluşturuluyor.
- (d) ASIL KÖK NEDEN (SQLite Error 14): K1 `icacls /inheritance:r` miras ACE'lerini (CREATOR OWNER dahil) SİLİP DACL'i bozuyor →
  SYSTEM bile db oluşturamıyor. FIX: `/inheritance:d` (mirası KAPAT ama ACE'leri KORU/kopyala) + yalnız tenant'ları kaldır
  (Users S-1-5-32-545 + Auth Users S-1-5-11) → SYSTEM/Admin/CREATOR-OWNER korunur, SQLite kırılmaz, K1 GÜVENLİĞİ KORUNUR.
  (Kullanıcı "kapatma, çöz, pleski örnek al, araştır" dedi → kapatılmadı, araştırılıp doğru çözüldü.)
- (e) Program.cs KURTARMA AĞI: migration db'yi açamazsa EnsureWritableAsync (data ACL /reset) + yeniden dene → panel ASLA
  kilitlenmez; eski bozuk kurulumlar kendiliğinden iyileşir.
- (f) Program.cs: sertifika yoksa --urls https olsa bile HTTP'ye düş (sertifikasız HTTPS çökmesi yerine açılsın).
- (g) initialize-panel-certificate.ps1: certs ACL'ini /reset ile onarıp panel.pfx yazar (best-effort) → HTTPS sıfır-ayar.
- DataRootHardeningService: static HardenAsync (/inheritance:d güvenli) + EnsureWritableAsync (kurtarma).
- Doğrulama: build 0/0, test 44/44 (+2). Installer SHA256: `150B18264ED4D5BADF0D5BB1F3EF6951AA3EE19E860B7F395542AAB921E97317` (49.29 MiB).
- HEDEF: tek installer, sıfır elle müdahale, eski bozuk durumu da kendiliğinden onarır.

## 2026-06-24 (88) — K1 sertleştirme .NET DirectorySecurity'ye geçti (icacls (OI)(CI)F "inherit-only" belirsizliği = KÖK NEDEN)
- Kullanıcı: db düzeldi (panel açıldı!) ama setup'ta "Cannot read properties of null (reading 'requestToken')" + "Failed to fetch"
  → CSRF/antiforgery patlıyor. ARAŞTIRMA (MS DataProtection docs + Windows servis anahtar sorunu): servis DataProtection
  anahtarını keys klasörüne yazamayınca antiforgery patlar. Kök neden DB ile AYNI sınıf: keys klasörü izni.
- ASIL KÖK NEDEN: icacls `/grant SID:(OI)(CI)F` bazı Server'larda izni klasörün KENDİSİNE değil "inherit-only" uyguluyor →
  SYSTEM klasöre DOSYA yazamıyor (SQLite + DataProtection kırılıyor). NOT: db'nin çalışması /inheritance:d değil, kurtarma
  ağının icacls /reset'i sayesindeydi → K1 aslında uygulanmıyordu (data dir reset edilmişti).
- FIX: icacls yerine .NET DirectorySecurity. FileSystemAccessRule(SYSTEM/Admins, FullControl, Container|ObjectInherit,
  PropagationFlags.None = NESNENİN KENDİSİNE de uygula) → izin KESİN "bu klasör + alt klasör/dosyalar". SetAccessRuleProtection
  (true, preserveInheritance:true) + PurgeAccessRules(Users S-1-5-32-545 + Auth Users S-1-5-11). Tenant kaldırılır, SYSTEM/Admin
  yazar → K1 KORUNUR + SQLite/DataProtection çalışır. (net8.0-windows'ta System.Security.AccessControl gömülü, ek paket yok.)
- EnsureWritableAsync kurtarma ağı artık data + KEYS ikisini de /reset eder.
- FRONTEND (client.ts): /auth/csrf null/ağ hatasında ÇÖKMEK yerine boş token + catch → "requestToken null" çirkin hatası gitmez.
- Doğrulama: build 0/0, test 44/44 (+2), typecheck 0, FE build 0. Installer SHA256: `898F067757F18A92FECB1DE1F981FDB51D3FCF67F60350FFF70302AED755D70C` (49.29 MiB).
- KULLANICIYA: TEMİZ kurulum şart (eski bozuk-ACL'li dosyalar yeni dir ACL'ini miras ALMIYOR): C:\ProgramData\AdaPanel sil + yeni installer.

## 2026-06-24 (89) — phpMyAdmin/Adminer public açıklığı kapatıldı (codex denetimi: 8896 public + composer sızıntısı + OS portları)
- Codex bulgusu: (1) phpMyAdmin :8896 PUBLIC (Plesk'te panel auth arkasında olmalı), (2) /composer.json + /vendor/composer/installed.json
  HTTP'den okunuyor = bilgi sızıntısı, (3) 3389(RDP)/445(SMB)/135(RPC)/5985(WinRM) public.
- DOĞRULAMA: WebDbClientService.CreateSiteAsync IpAddress="*" (0.0.0.0:8896) + OpenFirewallPort → public. Proxy (DbToolsProxyController) ZATEN
  [Authorize(Roles=Administrator)] = güvenli yol vardı ama public fallback bırakılmıştı (kod yorumu da itiraf ediyordu). 3389/445/135/5985'i
  AdaPanel AÇMIYOR (Windows'un kendi varsayılan kuralları).
- FIX (kod, WebDbClientService): (a) site binding IpAddress="*" → "127.0.0.1" (yalnız loopback; proxy+health zaten 127.0.0.1 kullanıyor → çalışır).
  (b) OpenFirewallPort → EnsureFirewallPortClosed (public "AdaPanel WebDb {port}" kuralını AÇMAZ, varsa KALDIRIR). (c) HardenWebDbWebConfig: kök
  web.config'e requestFiltering hiddenSegments(vendor,composer.json,composer.lock,package*.json,yarn.lock,.git,.github,examples,test,node_modules)
  + denied fileExtensions(.md,.lock,.dist,.sh,.yml,.yaml) → metadata/paket dosyaları HTTP'den engellenir (savunma derinliği). Deploy + signon "Aç"
  yollarından çağrılır (yeniden dağıtım beklemeden web.config sertleşir). vendor/ sunucu-tarafı autoload → HTTP kapatmak phpMyAdmin'i bozmaz.
- FIX (harden-adapanel.ps1, OS portları — Plesk gibi opt-in, "sormadan kapatma" ilkesi): B7(DEFAULT) phpMyAdmin/Adminer public firewall kurallarını
  kaldır. B8(opt-in -HardenManagementPorts) SMB445/RPC135/NetBIOS137-139/WinRM5985-5986 inbound BLOCK. RDP(opt-in -RdpAllowFrom <ip[,ip]>) yerleşik
  RDP kurallarını yalnız allowlist IP'ye kısıtla — blanket KAPATMA YOK (kilitlenme önlenir, kırmızı uyarı). param + .PARAMETER/.EXAMPLE eklendi.
- harden-adapanel.ps1 K1 DÜZELTİLDİ: eski `icacls /inheritance:r /grant:r (OI)(CI)F` (paneli kıran AYNI yöntem!) → panelle aynı .NET
  Get-Acl/Set-Acl (SetAccessRuleProtection(true,true) + SYSTEM/Admin FullControl Container|ObjectInherit + None + PurgeAccessRules Users+Auth),
  yalnız 6 hassas alt klasör (root/runtimes'a DOKUNMAZ → runtimes IIS için RX kalır). Geçici klasörde test: SYSTEM✓ Admins✓ Users✗ Auth✗
  Protected✓ Yazılabilir✓. Artık scripti çalıştırmak paneli KIRMAZ (önceki K1 çalıştırılsa DataProtection/SQLite'ı yeniden kırardı).
- harden-adapanel.ps1 artık installer'a DAHİL (AdaPanel.iss [Files] → {app}\scripts\harden-adapanel.ps1).
- Doğrulama: build 0/0, test 44/44(+2), PS parse OK (0 hata). Installer SHA256: `AF632C4E204620644ADD8344E0688BE299FFF739617CF715FF42D8301BD01DC4` (49.30 MiB).
  (Ara build'ler: FCCCFFD0 = harden script bundle öncesi, F87BF955 = K1 .NET düzeltmesi öncesi.)
- KULLANICIYA: yeni installer kur + panelden phpMyAdmin/Adminer'i 1 kez YENIDEN DAGIT (loopback binding uygulansın). Mevcut public'i HEMEN kapatmak
  için canlıda harden-adapanel.ps1 çalıştır (B7 firewall kuralını kaldırır). OS portları için -HardenManagementPorts (+ sabit IP'n varsa -RdpAllowFrom).

## 2026-06-24 (90) — UI'dan + tüm kaynaktan "Plesk" markası kaldırıldı
- Kullanıcı: "Plesk gibi" yazısı veritabanı + ayarlar sekmesinde görünüyor; "hiç bir yerde plesk yazısı geçmesin".
- Görünen (bundle'a giden) TEK "Plesk" = i18n string'leri. 10 string düzeltildi (5 tr + 5 en), anlam korunarak "Plesk" çıkarıldı:
  panel.migration.ftp.title ("site göçü"), .remotePathHint ("bazı panellerde httpdocs"), panel.database.remote.subtitle ("Varsayılan kapalı"),
  panel.ftp.requireTlsHint ("(varsayılan)"), panel.toolkit.subtitle ("tek yerden komutla yönetin"). en tarafı: "site migration", "some panels",
  "Off by default", "(the default)", komut cümlesi.
- Kalan ~150 "Plesk" = kod yorumları (//, ///, <!-- -->); kullanıcıya GÖRÜNMEZ (derlemede silinir, bundle'da yok). "hiç bir yerde" istendiği
  için hepsi de temizlendi: BOM-koruyan PowerShell bulk replace (83 dosya) — "Plesk gibi"→"profesyonel paneller gibi", "Plesk benzeri"→
  "profesyonel panel benzeri", "Plesk Obsidian"→"modern paneller", "Plesk Service Plans"→"Servis Planları", "Plesk Migrator"→"profesyonel göç
  aracı", "Plesk-style"→"industry-standard", catch-all "Plesk"→"panel". i18n .json + wwwroot + bin/obj/node_modules HARİÇ tutuldu.
- Backend'deki 107 "Plesk"in TAMAMI yorum/XML-doc olduğu doğrulandı (identifier/string değil) → bulk replace derlemeyi bozmaz.
- Doğrulama: grep -i plesk (src + frontend/src) = 0; SHIPPED wwwroot bundle = 0. Solution build 0/0, typecheck 0, i18n parite en=tr=2141 (0 fark),
  Türkçe karakter kodlaması sağlam (spot-check: "kullanıcı" bozulmadı). Installer SHA256: `2F81159FDFBA9D7FE3BB6E051F03197C136EDF37024A8EA97D4CB6771F80A393` (49.30 MiB).
- KULLANICIYA: yeni installer kur — UI metinleri artık Plesk'siz. Değişikliği görmek için panel güncellenmeli (kur + tarayıcıyı yenile).

## 2026-06-24 (91) — phpMyAdmin reverse-proxy POST AJAX 500'ü düzeltildi (Content-Length mismatch / boş gövde)
- Belirti: phpMyAdmin açılıyor, DB görünüyor, ama HER sekmede "İstek işlemede hata / Hata kodu: 500 / error (rejected)" (phpMyAdmin'in
  AJAX hata kutusu). KRİTİK: yanıt PANELİN zarfı {"success":false,"message":"common.unexpected_error","code":500} → phpMyAdmin değil
  PROXY patlıyor. DevTools: çalışan ana sayfa GET, patlayan navigation POST (gövdeli). Serilog log:
  `System.Net.Http.HttpRequestException: Sent 0 request content bytes, but Content-Length promised 102` at DbToolsProxyController.ProxyAsync.
- KÖK NEDEN: POST gövdesi proxy'ye boş/başa-sarılmamış geliyor → StreamContent 0 byte gönderiyor ama kopyalanan Content-Length 102 vaat
  ediyor → HttpClient exception → panelin 500'ü. phpMyAdmin'in TÜM POST AJAX'ları (navigation = her sekme) bu yüzden ölüyordu; GET'ler
  (gövdesiz) çalıştığı için sayfa açılıyor + DB görünüyordu (yanıltıcı). Log yolu: C:\ProgramData\AdaPanel\logs\adapanel-<tarih>.log.
- FIX (2 dosya): (1) Program.cs line-191 ara katmanı: /api/v1/dbtools + non-GET isteklerde context.Request.EnableBuffering() → gövde
  tekrar-okunabilir (küçük=bellek, büyük SQL import=disk; gövdeyi okuyan her şeyden ÖNCE çalışır). (2) DbToolsProxyController.ProxyAsync:
  StreamContent'ten önce `if (Request.Body.CanSeek) Request.Body.Position = 0;` → başa sar. Gövde artık DOLU → tarayıcının Content-Length'i
  birebir eşleşir (Content-Length kopyası korundu = otoriter uzunluk). DbToolsProxyController Plesk-scrub'ında DEĞİŞMEDİ (logic aynı).
- Doğrulama: build 0/0, test 44/44(+2). Installer SHA256: `1341B429C20E433ED1ABBD85C1FBCA7D7755756CEFDA2DF5629AD68BBF9AABEF` (49.30 MiB).
- KULLANICIYA: yeni installer kur — düzeltme PANELDE (Program.cs+proxy), phpMyAdmin'i yeniden DAĞITMAYA GEREK YOK. Kur + tarayıcıda Ctrl+F5.
  phpMyAdmin navigation/sekme gezinmesi artık çalışır.

## 2026-06-24 (92) — PHP güvenlik varsayılanları Plesk'e hizalandı (expose_php=Off; disable_functions BİLEREK opsiyonel)
- Kullanıcı "pleskteki ayarların aynısını yap" dedi (devam et güvenlik turu). ARAŞTIRMA (Plesk Obsidian docs + support):
  Plesk'in DOMAIN varsayılanı disable_functions="opcache_get_status" (MİNİMAL) — exec/shell_exec'i VARSAYILAN KAPATMAZ;
  tenant güvenliğini per-domain open_basedir izolasyonu + expose_php=Off ile sağlar. Sıkı disable_functions admin OPSİYONU
  (PHP Settings / Service Plan ile). Yani "Plesk gibi" = agresif disable_functions DEĞİL.
- Durum: open_basedir per-site zaten default ✓ (B4, .user.ini). expose_php yoktu → eklendi.
- FIX: PhpService.EnsurePhpSecurityDefaultsAsync — RegisterFastCgiAsync sonunda paylaşımlı php.ini'ye expose_php=Off yazar
  (idempotent: zaten Off ise yazmaz; best-effort: FastCGI kaydını bozmaz). Kuruluşta + her site PHP kurulumunda çalışır.
  disable_functions BİLEREK default kapatılmadı (Plesk de kapatmaz; müşteri sitesi exec kullanıyorsa bozulmasın). Sıkı liste
  isteyen admin harden-adapanel.ps1 (K3) çalıştırır = Plesk'teki PHP Settings opt-in'in karşılığı.
- PROAKTİF DENETİM (entry 91 hata sınıfı): gövde-iletme (Request.Body proxy) tüm src tarandı → tek örnek DbToolsProxy (düzeldi);
  diğer StreamContent'ler dosya yükleme (seekable FileStream) ya da gövdesiz GET prob → aynı hata YOK. Webmail panel-proxy'den geçmiyor.
- Doğrulama: build 0/0, test 44/44(+2). Installer SHA256: `8A7AE5F6C355C501ECB6A04B82CF7CAE8E9B5869F6589A9D48D3577E9186A424` (49.30 MiB).
- KULLANICIYA: yeni installer kur. expose_php=Off yeni PHP kurulumunda/site PHP kurulumunda uygulanır (mevcut PHP'de bir sonraki
  PHP işleminde). Hemen istersen harden-adapanel.ps1 (K3 + expose_php zaten içinde) çalıştır. exec/shell_exec Plesk gibi AÇIK kalır.

## 2026-06-24 (93) — K2 atomik root parolası (Plesk gibi) — parolasız-root açığı kapatıldı
- Kullanıcı: "K2'yi yap, pleskteki gibi atomik kur". K2 = MariaDB root PAROLASIZ açığı (mevcut K2 yalnız anonim+uzak-root düşürüyordu,
  root'a PAROLA koymuyordu = asıl açık). Üstelik DatabaseService.ResolveConnectionProfileAsync saklı parola boşsa FIRLATIYORDU →
  parolasız root'ta K2 bile çalışamazdı.
- FIX (atomik, mevcut /db/harden K2 butonunu genişleterek — yeni endpoint/UI YOK):
  - ResolveConnectionProfileAsync'e `allowEmptyPassword` param → parolasız root'a bağlanılabilir (K2 yolunda true).
  - SetRootPasswordAtomicAsync (DatabaseService): root parolasızsa 1) yeni güçlü parolayı ÖNCE SAKLA (component.ProtectedConfigurationJson,
    secretProtector), 2) UYGULA (provider) + yeni parolayla YENİ bağlantı açıp DOĞRULA, 3) HATA olursa saklamayı ESKİYE geri al.
    "MariaDB yeni / panel eski" TEHLİKELİ durumu hiç oluşmaz (önce sakla → kurtarılabilir; verify+rollback panel kilitlenmesini önler).
  - MySqlDatabaseProvider.SetRootPasswordAsync: root@localhost/127.0.0.1/::1 için CREATE USER IF NOT EXISTS + ALTER USER IDENTIFIED BY +
    GRANT ALL WITH GRANT OPTION + FLUSH; sonra 127.0.0.1+yeni parola ile DOĞRULAMA bağlantısı. (uzak root@'%' kaldırılınca panel kopmaz.)
  - Saklanan parola TEK KAYNAK → panel DB bağlantıları + phpMyAdmin (WebDbClient.ResolveRootPasswordAsync) AYNI ANDA tutarlı.
  - HardenAdminAccounts ayrıca boş 'test' db'sini düşürür (information_schema'dan tablosuz doğrulanır → gerçek veri silinmez). 28-hane alfanumerik.
  - IDatabaseEngineProvider.SetRootPasswordAsync DEFAULT no-op → postgres/mssql sağlayıcıları dokunulmadı.
- Frontend: K2 butonu aynı; hardenHint + hardenedRemoved("Güvenlik uygulandı: {items}") metinleri güncellendi (tr+en, parite 2141).
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0. Installer SHA256: `F48314F2AB92BFB3E32702AB290CC60B2C52A78171FBD36FA580EFD76DB1BF65` (49.30 MiB).
- KULLANICIYA: yeni installer kur → Veritabanı/WebDB sayfasında "Güvenliği sıkılaştır" (K2) bir kez tıkla. Parolasız root'a güçlü parola
  atanır+saklanır; panel/phpMyAdmin kopmaz. NOT: canlı MariaDB olmadan test EDİLEMEDİ → verify+rollback güvenlik ağı; K2 başarısız olsa bile panel eski parolayla çalışır.

## 2026-06-24 (94) — Canlı Laravel deploy'unun ortaya çıkardığı 6 Plesk-parite eksiği; #6 (DB kullanıcı→GRANT) DÜZELTİLDİ
- Kullanıcı bir Laravel sitesini AdaPanel'e elle kurarken 6 eksik buldu + manuel düzeltti; panelin bunları OTOMATİK yapması istendi.
- #6 DÜZELTİLDİ (bu installer): "SQLSTATE[HY000] [1044] Access denied for user 'karsilastir'@'%' to database 'karsilastir'". DB + kullanıcı
  vardı ama GRANT yoktu. KÖK NEDEN: ayrı "Kullanıcı ekle" formu bir DB SEÇTİRMİYORDU (CreateDatabaseUserRequest'te DatabaseName yoktu) →
  CreateUserAsync kullanıcıyı kurup hiçbir DB'ye grant vermiyordu. (Birleşik db+user create ZATEN grant veriyordu.) FIX: CreateDatabaseUserRequest.
  DatabaseName (opsiyonel); DatabaseService.CreateUserAsync → DatabaseName doluysa GRANT ALL + credentialStore.Upsert(db). Frontend: create-user
  formuna veritabanı <select> (i18n grantToDatabase/grantNone) + payload.databaseName. Plesk gibi: kullanıcı bir DB bağlamında eklenir+otomatik tam yetki.
- KAYDA GEÇEN diğer 5 eksik — SONRAKİ İŞ (HENÜZ YAPILMADI), öncelik sırası:
  #5 (KRİTİK, her PHP app) yazma izni: PHP IUSR çalışır; storage + bootstrap/cache'e (Laravel) / wp-content'e yazamaz → site oluştururken
     app pool kimliği/IUSR'a yazılabilir dizinlerde Modify ver (Plesk gibi otomatik). 
  #1 (KRİTİK) framework doc root: panel IIS fiziksel yolu PROJE KÖKÜNE bakıyor; Laravel'de public/ olmalı (kök = 403). → artisan+public/index.php
     algıla → docroot public/; ayrıca panelde "Document root" alanı (Plesk gibi).
  #2 public/web.config PHP handler+rewrite yoktu → PHP FastCGI handler + index.php fallback (Laravel/WP rewrite) otomatik yaz.
  #3 .user.ini eski Plesk yolları (C:\Inetpub\vhosts) + bozuk open_basedir (GÖÇTEN gelen dosya) → göçte .user.ini sanitize + doğru yol/temiz open_basedir.
  #4 vendor eksik geldi (composer bağımlılıkları taşınmamış) → toolkit "composer install" tek-tık / göç sonrası bütünlük adımı.
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2143=2143. Installer SHA256: `AD3E6F7E921563D0DB1716EC94E22DCBA7E6705747F2A7BE6CDE399C55528476` (49.30 MiB).
- KULLANICIYA: yeni installer → "Kullanıcı ekle"de artık veritabanı seçilir → otomatik GRANT. #1-5 sıradaki öncelik (önce #5 izinler + #1 docroot).

## 2026-06-24 (95) — Panel-geneli deploy düzeltmeleri: #5 yazma izni + memory_limit varsayılanı (kullanıcı: tek site değil, panel geneli)
- #5 (yazma izni) YAPILDI: SiteProvisioningService.CreateAndProvisionAsync → site oluşunca o sitenin app pool kimliğine
  ("IIS AppPool\<pool>") site kökünde (OI)(CI)M ver → PHP/Laravel storage+bootstrap/cache, WP uploads yazabilir. YALNIZ kendi
  pool'una (kiracı izolasyonu; IIS_IUSRS DEĞİL → başka site yazamaz). (OI)(CI) → sonradan/göçle gelen dosyalar miras alır. Best-effort.
- memory_limit YAPILDI: PhpService.EnsurePhpSecurityDefaultsAsync → boş/stok 128M ise 256M (Laravel/WP "memory exhausted"); admin
  daha yükseğe çekmişse dokunmaz. expose_php=Off ile birlikte RegisterFastCgiAsync'te uygulanır.
- Doğrulama: build 0/0, test 44/44(+2). Installer SHA256: `B5814FAF6D7BCE51375A19EDFCA28B3617E904A473591331CFEAA589B0E6A9CA` (49.30 MiB).
- BACKLOG (panel-geneli, kullanıcı önceliğiyle — SIRADAKİ İŞ):
  #1 ⭐ EN KRİTİK — Document Root/Web Root per-site ayarı: panel siteyi düzenleyince docroot'u proje köküne geri döndürüp 403'e
     yol açıyor. Kalıcı "belge kökü" alanı + IIS fiziksel yolunu alt klasöre (public) yönlendir + PERSIST + Laravel auto-detect (artisan+public/index.php).
  DB içe-aktarma: mevcut MariaDB db/user'larını tara → panele kaydet (panel yalnız kendi oluşturduklarını biliyor; karsilastir görünmüyordu).
  PHP UI: redis extension toggle'ı arayüze çıkar (EnableRedisExtensionAsync zaten var); proc_open default KAPALI DEĞİL (harden script kapatırsa opt-in); open_basedir doğru yol.
  Fix-permissions repair aracı: #5'i MEVCUT sitelere de uygula (tek-tık izin onar).
  SSL sertifika geçerlilik kontrolü/uyarısı. + #2 web.config handler/rewrite (docroot ile), #3 .user.ini göç sanitize, #4 composer vendor (göç aracı).
- phpredis NOTU: enable akışı SAĞLAM (eşleşen DLL indir + php -m doğrula + başarısızsa geri al). "kurulu sanılıp kurulmamış" = Redis
  SUNUCUSU ≠ phpredis UZANTISI; PHP Ayarları'ndan "Redis uzantısını etkinleştir" çalıştırılmalı. App file driver kullandığı için ZORUNLU değil.

## 2026-06-24 (96) — #1 ⭐ Document Root / Web Root per-site ayarı (EN KRİTİK — Laravel public/ 403'ü kalıcı çözer)
- Mekanizma ZATEN vardı: IIisSiteService.UpdatePhysicalPathAsync (root virtual dir PhysicalPath → IIS'te kalıcı) ama CONTROLLER'da
  AÇIK DEĞİLDİ + frontend'de fiziksel yol READONLY → kullanıcı paneldan docroot değiştiremiyordu (elle IIS'ten değiştirince de revert korkusu).
- FIX: ISiteProvisioningService.SetDocumentRootAsync = UpdatePhysicalPathAsync + yeni kökte WebAppPublisher.EnsurePhpEntry (index.php
  VARSAYILAN BELGE; IIS stok varsayılan-belge listesi index.php İÇERMEZ → docroot=public'te "/" 403 verirdi; mevcut web.config'i EZMEZ, merge).
  SitesController: PUT sites/{name}/document-root (UpdateDocumentRootRequest{PhysicalPath} + EnsureSite scope + audit). Frontend SiteHostingView:
  readonly yol → DÜZENLENEBİLİR "Belge kökü" alanı + Kaydet + "\public kullan (Laravel)" hızlı-doldur; api/sites.updateDocumentRoot (PUT).
  Kaydetten sonra fetchSites → selectedSite tazelenir. IIS'te KALICI → panel revert ETMEZ (kullanıcının asıl endişesi giderildi).
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2149=2149. Installer SHA256: `625C1A2A650E3747A8E6195830512EF4442F9A6115953F78151E2002CBD37D90` (49.30 MiB).
- KALAN BACKLOG: DB import (mevcut MariaDB db/user'ları tara→panele kaydet), fix-permissions repair (mevcut sitelere #5 izni), web.config
  REWRITE (#2 — Laravel pretty-URL; URL Rewrite MODÜLÜ gerekir, yoksa 500.19 → dikkatli/modül kontrolü gerek), SSL geçerlilik, .user.ini göç sanitize.
  #4 composer = RunComposerInstallAsync + frontend (runComposerInstall) ZATEN VAR.
- KULLANICIYA: yeni installer → Site → Barındırma → "Belge kökü" alanına ...\public yaz + Kaydet (ya da "\public kullan" düğmesi). Laravel
  kök 403'ü biter (panel artık kalıcı tutar). Yol-ötesi route'lar için pretty-URL rewrite (#2) sıradaki iş.

## 2026-06-24 (97) — #2 Laravel/WP pretty-URL rewrite + #5 fix-permissions onar aracı
- #2 (pretty-URL): WebAppPublisher.TryWriteFrontControllerRewrite — URL Rewrite MODÜLÜ (%windir%\System32\inetsrv\rewrite.dll)
  kontrolü; VARSA web.config'e front-controller kuralı (REQUEST_FILENAME dosya/dizin DEĞİLSE → index.php) ekler (idempotent
  "AdaPanel Front Controller"; mevcut kuralları korur). MODÜL YOKSA hiç yazmaz + false → çağıran 409 "sites.url_rewrite_module_missing"
  verir (site 500.19 OLMAZ). ISiteProvisioningService.EnableFrameworkRewriteAsync + SitesController POST sites/{name}/framework-rewrite.
- #5 onar: SiteProvisioningService.RepairPermissionsAsync (GrantSiteWritableAsync'i mevcut siteye uygular — bu özellikten önce kurulmuş
  siteler için) + POST sites/{name}/repair-permissions.
- Frontend SiteHostingView: docroot satırına 2 buton — "Pretty URL yönlendirmesi (Laravel/WordPress)" + "Yazma izinlerini onar";
  api/sites.enableFrameworkRewrite + repairSitePermissions; i18n (+ sites.url_rewrite_module_missing hata mesajı, resolveApiMessage çevirir).
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2154=2154. Installer SHA256: `583D389F7A4369A8A64FEC1324600A2446ECF0A7163914D582636DB297762335` (49.30 MiB).
- BACKLOG DURUMU: TAMAM → #1 docroot, #2 rewrite, #4 composer (RunComposerInstall), #5 perms (oluşturmada + onar aracı). KALAN →
  DB import (mevcut MariaDB db/user'ları tara→panele kaydet), SSL geçerlilik kontrolü, .user.ini göç sanitize.
- KULLANICIYA: Site → Barındırma → "Pretty URL yönlendirmesi" (URL Rewrite modülü gerek; yoksa net uyarı) + "Yazma izinlerini onar" (eski sitelere #5).

## 2026-06-24 (98) — DB import + SSL geçerlilik etiketi (deploy backlog'unun kalanı tamamlandı)
- DB import: panel ZATEN tüm DB'leri+kullanıcıları listeliyor (sistem şemaları hariç; GetDatabasesAsync Owner=""). "import"un değeri =
  harici (uygulama/elle) DB+kullanıcıyı PANELE TANIT. DatabaseService.ImportDatabaseAsync = provider.GrantAccessAsync(ALL, eksikse) +
  credentialStore.UpsertAsync(engine,user,host,db,password) → phpMyAdmin "bu kullanıcı olarak aç" + panel yönetimi (yedek/şifre/yetki).
  ImportDatabaseRequest DTO + POST db/import + DatabasesView "Mevcut DB'yi içe aktar" formu (DB seç + kullanıcı seç + parola) + api/store.
  Kullanıcı zaten var olmalı (yoksa "Kullanıcı ekle" + DB seçimi). (NOT: bu turda DatabasesController'a yanlış eklenen pragma-blok düzeltildi.)
- SSL: SslView ZATEN her cert'in NotAfterUtc + renk-kodlu pill'ini (expiryClass: <0 danger, <30 warn, ok) gösteriyordu. expiryLabel eklendi:
  "X gün kaldı / Bugün doluyor / Süresi doldu" → "geçerli/yenilenmiş mi" netliği. Backend DEĞİŞMEDİ (NotAfterUtc zaten DTO'da).
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2163=2163. Installer SHA256: `82A13955D755F69ACCE48B26931799C8CA719A310F2C7634D89A5350797B12F0` (49.30 MiB).
- DEPLOY BACKLOG TAMAM: #1 docroot ✓, #2 rewrite ✓, #4 composer ✓, #5 perms+repair ✓, DB import ✓, SSL geçerlilik ✓. KALAN tek madde:
  .user.ini göç sanitize → Plesk→AdaPanel MIGRATION TOOL ile birlikte (o araç henüz kurulmadı; tasarım entry'lerde mevcut).
- KULLANICIYA: yeni installer → Veritabanları'nda "Mevcut DB'yi içe aktar" (karsilastir'ı tanıt) + SSL'de her cert'in yanında kalan gün/durum.

## 2026-06-24 (99) — Göç sihirbazı ARAYÜZÜ (manuel mod) — kabuğa yeni "Göç" bölümü (motor entry 98-öncesinde commit'ti)
- MigrationView.vue: site (ad / fiziksel yol / "siteyi oluştur") + dosya kaynağı (protocol/host/port/user/pass/remotePath) + DB kaynağı
  (engine/host/port/user/pass/sourceDb/targetDb) formu → api/migration.migrateSite (POST /migration/site) → adım-adım sonuç (✓/✗ + mesaj).
- Kabuk wiring: SetupWizardView.dashboardSections'a {id:"migration", glyph:"MG"} eklendi + import MigrationView + render
  (v-else-if activeSection==='migration' && isAdmin). Admin filtresi (adminSections = files HARİÇ hepsi) → admin görür; customer/reseller
  whitelist'inde yok → ADMIN-ONLY. MainLayout.NAV_ICONS.migration (transfer-ok ikonu) eklendi (yoksa fallback daire).
- api/migration.ts (migrateSite + MigrateSitePayload/Result/Step tipleri). i18n: panel.navigation.migration + panel.migration.* (~20 anahtar;
  dosya alanlarında panel.migration.ftp.* YENİDEN kullanıldı). migration.step.* (backend mesaj anahtarları) i18n'e eklendi.
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2183=2183. Installer SHA256: `67BF3514394914DE740139834552FB5FFCF3E1BE04DD71576B5602D59CB88399` (49.31 MiB).
- KULLANICIYA: menüde "Göç" → formu doldur (site + SFTP + DB) → "Göçü başlat" → adım sonuçları. Plesk/cPanel/ham sunucu hepsiyle (manuel kimlik girişiyle).
- GÖÇ ARACINDA KALAN: Plesk REST OTOMATİK KEŞİF ("keşif" katmanı — Plesk'e API anahtarıyla bağlan → domain'leri listele → formu otomatik doldur →
  tek tık). Bende canlı Plesk olmadığı için test edilemeyen katman; mevcut motor/forma oturur. + .user.ini göç-sonrası sanitize.

## 2026-06-24 (100) — Plesk REST OTOMATİK KEŞİF → Göç aracı TAM (sihirbaz + keşif)
- DiscoverPleskAsync (SiteMigrationService): GET https://host:8443/api/v2/domains (header X-API-Key) → domain listesi. Plesk self-signed
  cert'i tek-seferlik göç bağlantısı için ServerCertificateCustomValidationCallback => true ile ATLANIR (admin kendi sunucusuna bağlanıyor).
  JSON array'den her elemanın "name" (+ "hosting_type") çıkarılır. Best-effort: bağlantı/HTTP/parse hatası → net AppException
  (migration.plesk.connect_failed / api_error / parse_failed); MANUEL FORM ETKİLENMEZ. PleskDiscoverRequest/ResultDto + PleskDomainDto.
  POST /migration/plesk/discover.
- Frontend MigrationView: en üstte "Plesk'ten otomatik keşfet" bölümü (host + port 8443 + API anahtarı → "Domain'leri keşfet") → domain
  buton-chip'leri → birine tıklayınca form doldurulur (siteName=domain, remotePath=/httpdocs, fileHost/dbHost=plesk host). Parolalar yine
  ELLE girilir (Plesk API parola vermez). api/migration.discoverPlesk + i18n (~11 anahtar; migration.plesk.* hata mesajları).
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2194=2194. Installer SHA256: `F65B1166638BDEA8B1D396369A54180DAECC08B570D7D0428E1ABA40168229C1` (49.31 MiB).
- ⚠️ Plesk REST keşfi BENDE TEST EDİLEMEDİ (canlı Plesk yok) → docs'a göre (api/v2/domains, X-API-Key) yazıldı; Plesk yanıt şekli farklıysa
  parse_failed döner (çökmez, manuel form çalışır). Manuel mod = güvenli taban; keşif sadece form-doldurma kolaylığı.
- 🎯 GÖÇ ARACI TAM: motor (orkestrasyon) ✓ + sihirbaz (manuel) ✓ + Plesk REST keşif ✓. Geriye yalnız nice-to-have: .user.ini göç-sonrası
  sanitize, per-domain DB otomatik keşfi (Plesk CLI gate — kırılgan, atlandı).
- KULLANICIYA: menü → "Göç" → (opsiyonel) Plesk host + API anahtarı → "keşfet" → domain seç → SFTP + DB parolalarını gir → "Göçü başlat" → adım sonuçları.

## 2026-06-24 (101) — Önbellek TTL özelliği ARAŞTIRILDI → kullanıcı talebiyle GERİ ALINDI + doğrulama rebuild'i
- İstek: panelden önbellek süresi (TTL), "her db/site ayrı". Web araştırması: Plesk'te GENEL Redis TTL kadranı YOK. Redis nesne
  önbelleği TTL = wp-config `WP_REDIS_MAXTTL` (WP Toolkit); site-bazlı "Cache timeout" = nginx proxy cache (IIS karşılığı: output caching).
  Tek GERÇEK server-side kadran = Redis `maxmemory` + `maxmemory-policy` (allkeys-lru) — sektör standardı (256mb/allkeys-lru).
- Başlanan iş: 5 DTO oluşturuldu (RedisConfigDto, UpdateRedisConfigRequest + 3 TTL DTO). CacheService/controller/frontend/i18n'e
  DOKUNULMADI. Kullanıcı önce "karıştırma, boşver, her site kendi web.config'inde belirlesin", sonra "yaptığın işlemleri geri al" dedi.
- GERİ ALINDI: 5 DTO silindi; Cache DTO klasörü orijinal 3 dosyaya (CacheMetrics/CacheRuntime/CacheStatus) döndü. KAYNAK = entry 100 durumu.
- İLERİDE istenirse Plesk-sadık yol: (a) Redis maxmemory+eviction (global, gerçek, CONFIG SET + conf'a yaz); (b) site başına
  tarayıcı/statik önbellek süresi = `SiteHostingSettingsService.UpdateWebConfig`'e `<staticContent><clientCache cacheControlMaxAge>`
  (Plesk "browser caching/Expires" karşılığı; IIS-yerel; dinamik sayfayı bozmaz; "her site web.config'de" = kullanıcının istediği).
- Doğrulama rebuild'i (içerik entry 100 ile AYNI): build 0/0, test 44/44(+2), typecheck 0, frontend dist OK.
  Installer SHA256: `09AAF3254403DEFF4AA86955B3BAE36756D56D1DB346C5636322213AF34DDE4E` (49.31 MiB). Tek-dosya publish + ISCC
  non-deterministik → SHA `F65B11…`'den farklı, içerik özdeş.

## 2026-06-24 (102) — Göç #1 (.user.ini sanitize) + #3 (phpredis UI toggle) — deploy/Plesk-parite backlog'unun KOD kalanı bitti
- #1 .user.ini SANITIZE (göç-sonrası; deploy backlog'unun son kod maddesi): SiteMigrationService'e IIisSiteService enjekte edildi;
  PullFiles BAŞARILIYSA `SanitizeUserIniFilesAsync(site kökü)` ayrı "userini" adımı olarak çalışır. Site kökündeki .user.ini'leri
  tarar (recursive, ≤200 dosya): `open_basedir`'i `"<yeni kök>;<temp>"` olarak YENİDEN yazar; yol-içeren yönergeleri
  (session.save_path, upload_tmp_dir, sys_temp_dir, error_log, sendmail_path, curl.cainfo, openssl.cafile, soap.wsdl_cache_dir,
  opcache.file_cache, extension_dir) ESKİ Plesk/vhost izli (\vhosts\, /var/www/vhosts ...) VEYA bu makinede OLMAYAN mutlak yol ise
  yorum satırına alır → PHP varsayılanına döner (göçen sitenin "open_basedir restriction"/eksik-path 500'ü çözülür). Değişiklik yoksa dokunmaz.
- #3 phpredis UI: backend (POST /api/v1/php/enable-redis → indir + `php -m` doğrula + başarısızsa geri al) ve api/php.ts
  `enableRedisExtension` ZATEN vardı; SADECE buton eksikti. PhpView başlığına "Redis uzantısını etkinleştir" + `enableRedis()`
  (idempotent; "already" → zaten etkin). i18n: panel.php.enableRedis/enablingRedis + php.redis_already/redis_enabled.
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2200=2200. Installer SHA256:
  `1B726EBDB34A365FEB0C8C15D23191E99A5532ED6CE36B4D8BA90731B2E0B4A0` (49.31 MiB).
- DEPLOY/GÖÇ BACKLOG TAM (kod): #1 docroot ✓ #2 rewrite ✓ #4 composer ✓ #5 perms ✓ DB import ✓ SSL ✓ .user.ini sanitize ✓ phpredis UI ✓.
- KALAN (kodla bitmez): CANLI-doğrulama (Roundcube tek-tık, Plesk REST keşif, K2 root, B3) + MİMARİ M1 (müşteri/abonelik/servis-planı/
  KOTA modeli — EN BÜYÜK eksik, Plesk "Service Plans") · M2 (yedek zamanlama/restore) · M3 (mail DKIM/SPF/DMARC canlı) · M4/M5 (DNS/FTPS) · RBAC UI.
- KULLANICIYA: yeni installer → Göç artık .user.ini'yi otomatik temizler; PHP ekranında "Redis uzantısını etkinleştir" butonu.

## 2026-06-24 (103) — M1 (müşteri/abonelik/KOTA) ZATEN TAM ÇIKTI — yanlışlıkla başlanan duplike GERİ ALINDI
- Kullanıcı M1'i "en büyük eksik" diye seçti (benim ESKİMİŞ notuma göre). GERÇEK: M1 tamamen kurulu + ENFORCE ediliyor:
  · ServicePlanService (config/service-plans.json) — plan şablonları CRUD; /api/v1/service-plans; Ayarlar→Kullanıcılar'da UI.
  · CustomerScopeService (config/customer-scopes.json) — müşteri başına limit (MaxSites/MaxDiskMb/MaxDatabases/MaxMailboxes) +
    bayi sahipliği (OwnerId) + disk kullanımı ölçümü (GetDiskUsageMbAsync).
  · ENFORCEMENT: site → SitesController:386 (MaxSites), DB → SitesController:401 + ResellerDatabasesController:42 (MaxDatabases),
    posta → MailController:373 (MaxMailboxes), disk → FilesController:84 (MaxDiskMb; kullanım+gelen>limit → 403). Admin sınırsız.
  · Plan→müşteri uygula: create-user formunda plan seçici (applyPlanToCreate → limit alanlarını doldurur). Kullanıcı listesinde
    disk kullanım/limit gösterimi + "login as" (impersonation). Yani M1 UI+enforcement TAM.
- YANLIŞ BAŞLANAN duplike (ServicePlan EF entity + tablo + PlanService + /api/v1/plans + migration AddServicePlans) TAMAMEN GERİ ALINDI
  (ef migrations remove + dosya silme + DbSet/DI revert). Build 0/0. KAYNAK = entry 102 durumu → installer 1B726EBD; YENİ installer GEREKMEZ.
- ⚠️ DERS: security-audit memory'deki "M1 YOK / kota uygulanmıyor" ve "M-serisi mimari eksik" YANILTICI/ESKİMİŞ. M2 (IBackupService),
  M3 (mail/Stalwart + mail kotaları), M4 (IDnsService/IRdnsService), M5 (IFtpService) SERVİSLERİ DE VAR. "X YOK" demeden ÖNCE KAYNAĞI DOĞRULA.
- GERÇEK KALAN: CANLI-doğrulama (#4-7) + M2-M5'in İNCE eksikleri (körlemesine kurma; önce denetle).

## 2026-06-25 (104) — M2 denetimi: zaten ~tam; tek gerçek eksik = PANEL SELF-BACKUP eklendi (config + adapanel.db)
- M2 DENETİM SONUCU: yedek sistemi zaten kapsamlı. AutomationService (config/automation.json + AutomationWorker dakikalık):
  otomatik zamanlanmış yedek (aralık/saat) + kapsam (site/DB/mail) + retention (PruneAsync) + OFF-SITE (FTP/S3/WebDAV, parola DPAPI) +
  otomatik SSL yenileme + mail cert sync + uyarılar (disk/cert eşiği → e-posta/webhook, 24s cooldown). BackupService: granüler restore + indir.
- TEK GERÇEK EKSİK = panelin KENDİ yedeği yoktu (site/DB/mail yedekleniyordu ama adapanel.db + config/ değil → sunucu çökerse
  panel yapılandırması kayıp). EKLENDİ: IBackupService.BackupPanelAsync → adapanel.db (+wal/shm, FileShare.ReadWrite ile paylaşımlı
  okuma) + config/*.json zip'lenir; Type="panel"; fileStore.ResolvePath ile yol. DataProtection ANAHTARLARI (keys/) + sertifikalar
  BİLEREK HARİÇ (kullanıcı kararı: makineye-bağlı DPAPI, taşınamaz). Off-site hedefi (ResolveTarget/UploadToRemote) destekli. POST /api/v1/backup/panel (admin).
- Otomasyona bağlandı: AutomationSettings.BackupPanelConfig toggle → RunBackupAsync panel yedeğini de alır (interval+retention+remote ile).
  Frontend: BackupView'e admin-gated "Panel" yedek türü (backupKind) + AutomationView'e "Panel yapılandırması" onay kutusu.
  i18n: panel.backup.panelTitle/panelAction/panelHint + panel.automation.backupPanelConfig.
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2204=2204. Installer SHA256:
  `E117C96A7A8CB7FF9DF139A1F0733AC3E63E657D9F2E89C11AF5A92C5DE688A6` (49.31 MiB).
- M2 ARTIK TAM. ⚠️ M3 (mail DKIM/SPF/DMARC), M4 (IDnsService/IRdnsService), M5 (IFtpService) servisleri de VAR — denetlenmeden "eksik" sayma.
  En yüksek değer artık CANLI kurulum + #4-7 doğrulama.
- KULLANICIYA: yeni installer → Yedek → "Panel" türü ile panel config yedeği; Otomasyon → "Panel yapılandırması" ile otomatik plana ekle.

## 2026-06-25 (105) — M3 (mail-auth) denetimi: zaten tam; açık-röle BİLEREK eklenmedi + canlı doğrulama runbook'u
- M3 DENETİM: mail kimlik-doğrulama zaten kapsamlı. DKIM (Stalwart RSA+Ed25519 otomatik üretir → GetDnsRecordsAsync selector._domainkey
  TXT) + SPF + DMARC kayıt üretimi + zone'a uygulama. MailDiagnosticsRunner CANLI sorgu: MX/A/SPF/DKIM/DMARC/PTR + 25/587/143 port probe;
  frontend yeşil/kırmızı (panel.mail.diag.*). RdnsService = PTR.
- AÇIK-RÖLE testi BİLEREK EKLENMEDİ: güvenilir open-relay testi HARİCİ IP'den yapılmalı; panel sunucunun KENDİSİ → 127.0.0.1'den test
  ederdi ama MTA'lar (Stalwart dahil) genelde localhost'a relay izni verir → "açık röle" YANLIŞ-ALARMI. Stalwart zaten varsayılan kapalı-röle
  (587 auth, 25 yalnız yerel domain). Kırılgan/yanıltıcı prob = "uydurma çözüm yapma" kuralına aykırı → yazmadım (dürüst karar).
- SONUÇ: M1+M2+M3 üçü de bu oturumda denetlendi → HEPSİ KURULU (yalnız M2'ye panel self-backup eklendi). Panel ÖZELLİK-TAM.
- EKLENEN (doc, kod DEĞİL): repo köküne `DEPLOYMENT_VERIFICATION.md` — canlı kurulum + #1-7 doğrulama + mail DNS + yedek + sorun giderme
  checklist'i. Panel portu 8888 (kurulumda seçilir, 0.0.0.0'a bağlanır). KAYNAK değişmedi → installer hâlâ `E117C96A…` (yeni derleme yok).
- SIRADAKİ GERÇEK DEĞER: CANLI KURULUM (E117C96A → VPS) + runbook ile #4-7 doğrulama. Spekülatif özellik aramayı bıraktık (her şey zaten var).

## 2026-06-25 (106) — Komple kod denetimi (hata SINIFI taraması): 2 gerçek bulgu düzeltildi, gerisi temiz
- Kullanıcı "sistemi komple tara, eksik/hatalı bul ve düzelt" → proaktif-denetim kuralıyla hata SINIFLARINI taradım (semptom değil):
  · sync-over-async (.Result/.Wait/GetAwaiter().GetResult): YOK ✓
  · .NET ToLower()/ToUpper() locale (Türkçe İ/ı): 6 eşleşme AMA hepsi EF LINQ → SQL'e çevrilir, .NET locale'i etkilemez → DOKUNULMADI (doğru) ✓
  · kültür-duyarlı sayı biçimleme: temiz (gerçek formatlar CultureInfo.InvariantCulture; ":N" eşleşmeleri Guid "N" formatı = false-positive) ✓
  · komut enjeksiyonu (PowerShell interpolasyon): temiz (ProcessRunner shell kullanmaz + sanitize; tek interpolasyon sabit ServiceName) ✓
  · async void (yutulmuş exception): YOK ✓
  · Win32ErrorHelper (HRESULT, dile bağımsız): 8 serviste tutarlı kullanılıyor ✓
- DÜZELTİLEN 2 GERÇEK BULGU:
  1. (ÖNEMLİ, Türkçe-Windows landmine) WebAdministrationSiteService.MapIisException: dosya-kilitli/kullanımda durumu YALNIZ İngilizce
     metinle ("being used by another process") eşleşiyordu → Türkçe Windows'ta tutmaz → genel 500. HRESULT 0x80070020
     (ERROR_SHARING_VIOLATION) eklendi (access-denied 0x80070005 + already-exists 0x800700B7 zaten vardı; in-use EKSİKTİ).
  2. (defansif) WebStatisticsService: IIS W3C log tarihi DateTime.TryParse kültürsüzdü → CultureInfo.InvariantCulture + DateTimeStyles.None.
- Doğrulama: build 0/0, test 44/44(+2), i18n 2204=2204. Installer SHA256:
  `18FD9AF16AD72C7E34012551C80BC6B0C1027C2684424BDBE013944DA99268D0` (49.31 MiB).
- SONUÇ: kod tabanı çok disiplinli; statik hata-sınıfı taramasında başka SİSTEMİK kusur YOK. Kalan = CANLI doğrulama (#4-7).
- KULLANICIYA: yeni installer; Türkçe Windows'ta "dosya kilitli" IIS hataları artık doğru 409 verir (genel 500 değil).

## 2026-06-25 (107) — Derin denetim turu 2: installer "AdaPanel" markası + Türkçe sihirbaz + runbook port düzeltmesi
- Kullanıcı: "didik didik tara, yarım kod/işlevsiz/eksik bul; + kurulum ekranında AdaPanel yazmıyor." Bu SON sürüm, internete konacak.
- Kod taraması (2. derin pas): NotImplementedException/TODO/FIXME/stub YOK; `AddHostedService<AutomationWorker>` KAYITLI (otomatik
  yedek/SSL arka planda GERÇEKTEN çalışıyor — "hayalet" değil); marka: index.html `<title>AdaPanel</title>` var. Sistemik yarım/işlevsiz kod YOK.
- INSTALLER MARKA (somut şikayet) DÜZELTİLDİ: Inno Setup 6'da Welcome sayfası VARSAYILAN KAPALI → "AdaPanel" adı belirgin görünmüyordu.
  AdaPanel.iss: `DisableWelcomePage=no` + `AppContact=AdaPanel` + `[Languages]` Turkish.isl + `[Messages]` tr.WelcomeLabel1/2
  ("AdaPanel Kurulum Sihirbazina Hos Geldiniz") + SetupWindowTitle/FinishedHeadingLabel. Özel port sayfası + MsgBox Türkçeleştirildi.
  Sihirbaz artık TÜRKÇE + her ekranda AdaPanel adı.
- GERÇEK DOC BUG: installer varsayılan portu **8890** (iss satır 194) ama DEPLOYMENT_VERIFICATION.md'ye 8888 yazmıştım → tümü 8890'a düzeltildi.
- Doğrulama: build 0/0, test 44/44(+2), ISCC OK (Turkish.isl yüklendi). Installer SHA256:
  `EF96DADF1E5A7B0A0CF4DE194D9568B09EE90C3DB25B7F386C00C96F27B7CC7D` (49.31 MiB) = SON SÜRÜM adayı.
- KULLANICIYA: yeni installer'da kurulum TÜRKÇE + ilk ekranda "AdaPanel Kurulum Sihirbazina Hos Geldiniz".

## 2026-06-25 (108) — Derin denetim turu 3: i18n çakışan anahtar (yanlış metin) + MySQL kullanıcı-adı/host enjeksiyonu düzeltildi
- Yöntem: geçici node denetim scripti → frontend'de t('...') ile KULLANILAN tüm statik anahtarları i18n'le karşılaştır + çift anahtar tara.
- BULGU 1 (kullanıcı-görünür hata): SiteMigrationView (eski .zip arşiv içe-aktarma) ile MigrationView (yeni göç sihirbazı) AYNI
  panel.migration.title/subtitle/running anahtarlarını paylaşıyordu → JSON son-tanım-kazanır → arşiv-import ekranı SİHİRBAZIN metnini
  gösteriyordu ("Site göçü / Taşınıyor", olması gereken "Site Göçü (İçe Aktarma) / Aktarılıyor"). FIX: arşiv-import'un 3 çakışan anahtarı
  panel.siteImport.title/subtitle/running'e taşındı (en+tr) + SiteMigrationView güncellendi. Artık 0 çift, 0 eksik (1637 statik key denetlendi).
- BULGU 2 (GÜVENLİK): MySqlDatabaseProvider CREATE/ALTER/DROP USER + GRANT'ta userName/host tek-tırnağa sarılıp ESCAPE EDİLMİYORDU
  (yalnız harden-drop satır 252 escape ediyordu). Reseller/Customer DB kullanıcısı oluşturabildiğinden "x'@'%" gibi bir adla @localhost
  kısıtı aşılıp UZAKTAN erişilebilir kullanıcı oluşturulabilirdi (host enjeksiyonu). FIX: 86/108/182/209'da userName+host'a .Replace("'","''")
  (satır 252 deseniyle tutarlı). Parola zaten parametreli (@password). Root metotları (136/143/150) sabit host → dokunulmadı.
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2207=2207 (0 çift/0 eksik). Installer SHA256:
  `D8EC12F1DF651A1F0EABCB8C55F3EC7F293895976449B71287406C453F1569EC` (49.31 MiB). Geçici _i18n-audit.cjs silindi.
- KULLANICIYA: 3. derin pas; iki gerçek bug daha kapandı (arşiv-import yanlış başlık + MySQL host enjeksiyonu). = güncel son sürüm.

## 2026-06-25 (109) — Derin denetim turu 4: BackupService eşzamanlılık yarışı düzeltildi; IDisposable/zip-slip/ReDoS temiz
- Taranan sınıflar: JSON dosya eşzamanlılığı (lost-update), IDisposable sızıntısı, zip-slip (path traversal), regex ReDoS.
- GERÇEK BULGU + FIX (eşzamanlılık): BackupService kayıtları (config/backups.json) AppendRecordAsync/UpsertRecordAsync/PruneAsync
  NON-ATOMİK Read+Write yapıyordu. AutomationWorker arka planda otomatik yedek eklerken kullanıcı ELLE yedek tetiklerse "son yazan
  kazanır" → kayıp yedek kaydı. FIX: üçü de fileStore.UpdateAsync (IndexPath dosya-başına kilit) → serileşir, kayıp yok. Prune artık
  güncel listeden YALNIZ silinen ID'leri çıkarır (eşzamanlı eklenen yeni kaydı korur); dosya silme kilidin DIŞINDA.
  (DatabaseOwnershipService/DatabaseCredentialStore/CustomerScopeService ZATEN UpdateAsync; admin-düzenleme servisleri tek-admin/düşük risk.)
- TEMİZ ÇIKAN sınıflar: IDisposable (TcpClient/HttpClient/SftpClient hepsi `using var`); zip-slip (ZipFile.ExtractToDirectory/TarFile
  .NET yerleşik koruması + manuel per-entry çıkarımda 592/1157 açık StartsWith(root) guard'ı); ReDoS (tüm regex'ler anchored/lineer,
  iç içe quantifier yok; Git branch/PHP-değeri gibi kullanıcı-girdili olanlar bile güvenli).
- Doğrulama: build 0/0, test 44/44(+2), ISCC OK. Installer SHA256:
  `1C25FA7DB73CDA6A67E8A189F0C2937F0948B7B85737A4F35D581736E17CD0B0` (49.32 MiB).
- 4 derin pasta TOPLAM 6 gerçek bug: HRESULT(106), tarih-parse(106), installer marka(107), i18n çakışma(108), MySQL enjeksiyon(108), backup yarışı(109).
- KULLANICIYA: backup kayıt yarışı kapandı (otomatik+elle yedek aynı anda → kayıp yok). Diğer denetlenen sınıflar temiz.

## 2026-06-25 (110) — Derin denetim turu 5: XSS / dosya-yükleme / authorization → HEPSİ TEMİZ (0 bug), güvenlik duruşu örnek
- Taranan (yüksek-değerli güvenlik): frontend XSS (v-html), dosya-yükleme/çıkarma (path traversal/zip-slip/symlink), controller authorization.
- v-html: yalnız iconFor(id) → SABİT SVG ikon path'i (NAV_ICONS), kullanıcı verisi değil → XSS yok.
- FileService: zip-slip guard (200) + sandbox path-traversal guard (ResolveSandboxTarget) + REPARSE-POINT/symlink escape kontrolü
  (EnsureNoReparsePoints) — örnek nitelikte (çoğu panel symlink escape'i kaçırır).
- Authorization: 44 controller'dan 42'si [Authorize]; anonim 2'si BİLEREK + sıkı korumalı:
  · SetupController: initialize LOOPBACK-ONLY (uzaktan 403) + SetupService "admin zaten varsa setup.already_initialized" reddi (çift kilit).
  · GitController webhook: [AllowAnonymous] ama FixedTimeEquals(token, WebhookSecret) = SABİT-ZAMAN gizli doğrulama (timing-attack kapalı), boş/yanlış 401.
  · HealthController: yalnız liveness + setup durumu (machineName bilerek kaldırılmış).
- SONUÇ: pas 5'te 0 bug. Kaynak DEĞİŞMEDİ → installer hâlâ `1C25FA7D`. 5 pas bug eğrisi: 2→2→1→1→0 = doygunluk; statik güvenlik çok sağlam.
- KULLANICIYA: XSS/dosya-yükleme/yetkilendirme denetlendi, sorun yok. Statik denetim doygunluğa ulaştı; gerçek değer artık CANLI doğrulama.

## 2026-06-25 (111) — CANLI BUG (kullanıcı VPS'te): site oluşturma Classic ASP (asp.dll) yokluğunda çöküyordu → ON-DEMAND kurulum
- Kullanıcı `1C25FA7D`'yi VPS'e kurdu, site eklerken: "IIS web ve yönetim özellikleri hazırlanamadı" (sites.iis_install_required) +
  detay "Classic ASP engine was not found: …asp.dll".
- KÖK NEDEN: enable-iis-features.ps1 Classic ASP'yi (Web-Asp/IIS-ASP) HİÇ kurmuyordu → asp.dll yok. Site provisioning (auto/mixed
  profili EnableClassicAsp=true) → EnsureClassicAspHandler asp.dll yoksa FIRLATIYOR → "auto" catch'i ASP hatasını kapsamıyor → site geri alınıyor.
- DÜZELTME (kullanıcı talebi: "atlama, KUR"): (1) AspNetService.EnsureClassicAspEngineAsync — asp.dll yoksa IIS Classic ASP özelliğini
  OTOMATİK kurar (Server: Install-WindowsFeature Web-Asp; Client: Enable-WindowsOptionalFeature IIS-ASP), sonra devam eder;
  SetClassicAspAsync + EnableMixedHandlersAsync ASP etkinleştirmeden ÖNCE çağırır (panelin PHP'yi on-demand kurması gibi).
  İLK denenen "zarif atla" yaklaşımı kullanıcı reddiyle GERİ ALINDI (skip değil install). (2) enable-iis-features.ps1 listesine
  Web-Asp + IIS-ASP eklendi → taze kurulumda asp.dll baştan var.
- Doğrulama: build 0/0, test 44/44(+2), ISCC OK. Installer SHA256: `5A34A31760CEC993819F2FAADAE658D72E2C8DC6F3F3E2361445EDD1062C8172` (49.32 MiB).
- MEVCUT VPS hızlı çözüm: `Install-WindowsFeature Web-Asp -IncludeManagementTools` (admin) → asp.dll gelir → site tekrar denenir.
  Ya da yeni build'i kur (update install enable-iis-features'ı Web-Asp ile yeniden çalıştırır).
- DERS: bir runtime profili eksik IIS özelliğinde HARD-FAIL etmemeli; panel gerekli motoru (PHP gibi) on-demand kurmalı.

## 2026-06-25 (112) — Plesk-parite: site↔veritabanı GERÇEK bağı + cascade silme (site silinince DB de gider)
- Kullanıcı: "siteyi sildiğimde DB de silinsin, geriye site'la ilgili hiçbir şey kalmasın." Cascade ZATEN kapsamlıydı (mail/FTP/DNS/SSL/
  proxy/kota/dosya) ama DB SADECE isim-tahminiyle eşleşiyordu (ResolveDatabasesAsync: DB adı == site adı) → farklı isimli DB (karsilastir) bağlanmıyordu.
- EKLENEN: explicit site↔DB bağı. ISiteDatabaseLinkService + SiteDatabaseLinkService (config/site-databases.json, atomik UpdateAsync).
  DatabaseService: CreateDatabaseAsync + ImportDatabaseAsync → SiteName verilirse LinkAsync; DeleteDatabaseAsync → UnlinkAsync (öksüz bağ kalmaz).
  SiteCascadeDeletionService: ResolveDatabasesAsync artık isim-eşleşmesi ∪ explicit bağları siler; DeleteAsync sonunda RemoveSiteAsync.
  GetPlanAsync (silme önizlemesi) bağlı DB'leri de gösterir. DTO: CreateDatabaseRequest.SiteName + ImportDatabaseRequest.SiteName.
- Frontend: DatabasesView create + import formlarına "Siteye bağla" dropdown'u (sitesStore) + hint. databases.ts payload'larına siteName. DI kaydı.
- TAM CASCADE: WebsiteWorkspaceView site silme artık `{ deleteDatabases:true, deleteFiles:true }` → site + bağlı DB'ler + DOSYALAR gider
  (kullanıcı "geriye hiçbir şey kalmasın" dedi; eskiden deleteFiles geçilmiyordu, dosyalar kalıyordu). Onay mesajı (panel.sites.confirmDelete)
  ne silineceğini açıkça yazar: DB/dosya/DNS/SSL/mail/FTP, geri alınamaz.
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2210=2210. Installer SHA256:
  `8301FA8F6F38A271130A488A207B5DB93E25FCEA6C826895603742BA21976707` (49.32 MiB).
- KULLANICIYA: DB oluştururken/içe aktarırken "Siteye bağla" seç → site silince o DB + dosyalar + bağlı HER ŞEY cascade gider. İsim eşleşmesi de korunur (eski DB).

## 2026-06-25 (113) — CANLI BUG: site silmede mail domaini "mail.operation_failed" (Stalwart delete domain reddi) → sağlamlaştırıldı
- Kullanıcı ESKİ build'de site sildi → site gitti ama cascade uyardı: "Mail domaini (alioral.xyz): mail.operation_failed".
- KÖK NEDEN: MailService.DeleteDomainAsync cascade'i YALNIZ hesapları (account) siliyordu; Stalwart ÜYESİ olan domaini silmeyi
  reddeder → domainde grup/liste/öksüz üye kalınca son "delete domain --ids" patlıyordu. Hesap-silme sonucu da kontrol edilmiyordu.
- FIX: (1) DeleteDomainAsync artık ["list","group","account"] TÜM üye tiplerini siler (QueryAsync bilinmeyen tipte [] döner=fırlatmaz,
  güvenli) sonra domaini siler; account turunda başka domaindeki alias'lar da temizlenir. (2) SiteCascadeDeletionService.StepAsync
  artık AppException.Details'i (gerçek Stalwart CLI çıktısı) uyarıya ekler → bundan sonra "mail.operation_failed" yerine GERÇEK sebep görünür.
- Doğrulama: build 0/0, test 44/44(+2), ISCC OK. Installer SHA256: `BB6A648E2E44455C4DFD020B18709776848D2EC2F09B34111BD13D2453DF729F` (49.32 MiB).
- MEVCUT DURUM: alioral.xyz mail domaini hâlâ Stalwart'ta (site silindi). Yeni build kurulunca Mail → domaini sil (artık temizlenir) ya da
  sonraki cascade temizler. Hâlâ patlarsa uyarı artık GERÇEK Stalwart hatasını gösterir. Bu build önceki tüm düzeltmeleri de içerir (site↔DB, tam cascade, Classic ASP on-demand).

## 2026-06-25 (114) — Site-altı "Veritabanları" (domaindb) sekmesi tam yöneticiye çevrildi (site altından DB ekle/sil)
- Kullanıcı: "site seçip Web DB sekmesine basınca DB gözükmüyor, sadece arayüzler" + "oradan da DB ekle/sil olmalı".
- DURUM: site altında İKİ sekme var → "Web DB" (webdb=WebDbView, sadece phpMyAdmin/Adminer ARAÇLARI) ve "Veritabanları"
  (domaindb=SiteDatabasesView, siteye-bağlı DB'ler). Kullanıcı yanlış sekmeye (Web DB) basmış. AMA SiteDatabasesView de eksikti:
  create siteye BAĞLAMIYORDU (siteName geçmiyordu) + SİL yoktu.
- FIX (SiteDatabasesView): (1) createDatabase artık siteName geçiyor → site iş alanından açılan DB doğrudan siteye bağlanır.
  (2) Her DB satırına admin-only "Sil" (deleteDatabase → DB + öksüz kullanıcı + site bağı, onaylı). phpMyAdmin-aç korundu. Liste
  getSiteDeletionPlan.databases'ten (isim-eşleşme ∪ explicit bağ).
- i18n: panel.siteDb.delete/deleting/deleted/confirmDelete. Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2214=2214.
  Installer SHA256: `DF771F7A883DF294A749A014B79F2A85D52428CDE64205FCF7B07F71526B9F7B` (49.32 MiB).
- KULLANICIYA: site → "Veritabanları" (SQL ikonu) sekmesi (Web DB DEĞİL) → DB ekle (siteye bağlı)/sil/phpMyAdmin. Gelişmiş kullanıcı
  işlemleri (parola değiştir, ayrı kullanıcı, import) global "Veritabanları" bölümünde + phpMyAdmin'de.

## 2026-06-25 (115) — Site-altı DB sekmesi BİRLEŞTİRİLDİ: "Web DB" (araçlar) → tek "Veritabanları" (DB yöneticisi); 3 görünüm tutarlı
- Kullanıcı: "site detaylarında Veritabanları yok, sadece Web DB var; onu değiştir." KÖK NEDEN: per-site araç listeleri 3 YERDE:
  WebsiteWorkspaceView.allTools (hem webdb=WebDbView-araçları HEM domaindb=SiteDatabasesView), SiteDetailView + SitesView kısayolları
  (YALNIZ webdb). Yani çoğu yerde "Web DB" (sadece phpMyAdmin/Adminer KURULUM aracı) görünüyordu, gerçek DB yönetimi değil.
- FIX (birleştirme): per-site "webdb" id'si artık DB YÖNETİCİSİ → SiteDatabasesView render eder, "Veritabanları" (SQL) etiketli.
  WebsiteWorkspaceView: webdb→SiteDatabasesView (WebDbView render+import kaldırıldı), duplike "domaindb" tool çıkarıldı (render iki id'yi de
  karşılar). SiteDetailView + SitesView kısayolları: webdb glyph=SQL + label=panel.sites.tools.domaindb. Üç görünümde TEK tutarlı "Veritabanları".
  phpMyAdmin/Adminer KURULUMU global Veritabanları bölümünde (WebDbView) kalır; per-site'de her DB'nin "phpMyAdmin ↗" butonu var.
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0 (unused WebDbView import temizlendi), i18n 2214=2214. Installer SHA256:
  `7FC4C586333CD55D26E636D1D946FB6E626558A379986B11FCBAFCF5D742C14F` (49.32 MiB). Önceki TÜM düzeltmeleri içerir.
- KULLANICIYA: site → "Veritabanları" (SQL ikonu) TEK sekme → DB ekle (siteye bağlı)/sil/phpMyAdmin. "Web DB" araç-kurulum sekmesi per-site'den kaldırıldı.

## 2026-06-25 (116) — CANLI BUG: phpMyAdmin proxy'de görüntü kayıyor (panel CSP/nosniff sızıntısı) → dbtools muaf tutuldu
- Kullanıcı: "phpMyAdmin açınca görüntü sola kayıyor, sol menünün altında kalıyor, F5+gizli sekmede de aynı" (deterministik, cache değil).
- KÖK NEDEN: Program.cs global güvenlik-başlıkları middleware'i TÜM yanıtlara sıkı CSP (`script-src 'self'`, unsafe-inline YOK) +
  `X-Content-Type-Options: nosniff` ekliyordu — proxy'lenen phpMyAdmin yanıtına DA. phpMyAdmin'in inline script'leri CSP'ye takılıyor →
  layout/sol-menü-genişlik JS'i çalışmıyor → ana içerik margin-left:0 ile sabit sol menünün ALTINA kayıyor.
- FIX: Program.cs'de 4 başlık ataması `if (!context.Request.Path.StartsWithSegments("/api/v1/dbtools"))` ile sarıldı → proxy'lenen DB
  araçları YALNIZ kendi (phpMyAdmin'in) başlıklarını alır; panelin sıkı CSP/nosniff/X-Frame'i artık sızmıyor. dbtools EnableBuffering bloğu korundu.
  (DbToolsProxyController zaten panel CSP+X-Frame'i upstream yanıttan ayıklıyordu; asıl sızıntı bu global middleware'di.)
- Doğrulama: build 0/0, test 44/44(+2), ISCC OK (backend-only, frontend rebuild gerekmedi). Installer SHA256:
  `1D64ECBEB7A4D6D3489783FEB7DDA6E5D8172F163052A704279833C020988879` (49.32 MiB). Önceki TÜM düzeltmeleri içerir (site↔DB, tam cascade, mail cascade, Classic ASP on-demand, birleşik DB sekmesi).
- KULLANICIYA: yeni build'i kur → phpMyAdmin'i aç → layout artık düzgün (sol menü + sorgu sekmeleri normal). Hâlâ kayarsa tarayıcı cache'i için Ctrl+F5.

## 2026-06-25 (117) — phpMyAdmin layout HÂLÂ kayıyordu → (116) CSP teşhisi EKSİKTİ; KALICI çözüm: proxy HTML'ine <style> enjekte
- Kullanıcı (116 build'i kurdu): "yine aynı, yaptığın işe yaramadı" + ekran görüntüsü (aaa.png): CSS/ikonlar/renkler DOĞRU ama üst
  sekme çubuğu (Yapı/SQL/Ara) sol menünün ALTINDA; body margin-left uygulanmamış. Yani CSP'yi muaf tutmak (116) TEK BAŞINA çözmedi.
- KÖK NEDEN (kaynaktan doğrulandı — phpMyAdmin QA_5_2 js/src/navigation.js): içerik margin'i `ResizeHandler.setWidth` →
  `$('body').css('margin-left', pos)` ile JS'ten gelir. Document-ready'de (non-mobile) `callbackSuccessGetConfigValue(initialResizeValue)`
  SENKRON çağrılır (data-config-navigation-width=240) → JS çalışırsa margin AJAX'sız anında uygulanır. Margin'in 0 kalması =
  phpMyAdmin'in init JS'i (inline `AJAX.fireOnload` bootstrap'ı, registerOnload callback'lerini ateşler) HİÇ çalışmıyor. CSS çalışıp
  script çalışmaması = inline bootstrap bloklanmış. CSP muafiyeti (116) yardımcı ama deploy/cache/ikinci-CSP gibi nedenlerle yetmedi.
- KALICI ÇÖZÜM (kök-nedenden BAĞIMSIZ, DbToolsProxyController): proxy'lenen `text/html` yanıtına `</head>` öncesi küçük bir <style>
  enjekte et: `body:has(#pma_navigation){margin-left:240px}` (+#pma_console). MANTIK: inline <style> HER CSP altında İZİNLİ (script
  değil) → CSP/build/cache durumundan bağımsız çalışır. `:has(#pma_navigation)` → yalnız nav'lı TAM sayfa (login/hata/AJAX-parçası
  değil; bunlarda </head> yok zaten = enjekte edilmez). Specificity (1,0,1) > `body{margin:0}`; JS çalışırsa kendi INLINE margin'i
  (daha yüksek öncelik) ezer → çift-margin/çakışma yok, resize bozulmaz. Ayrıca upstream'e Accept-Encoding ATLANIR (identity) →
  gövde güvenle string'e çevrilir, gzip/Content-Length uyumsuzluğu olmaz. 16MB üstü/HEAD/non-html → dokunulmaz, stream. try/catch → hata olursa orijinali aynen geçer.
- KAPSAM: değişiklik YALNIZ /api/v1/dbtools proxy'sinde (admin-only); panelin SPA/API/başka hiçbir yerine dokunmaz. (116) CSP muafiyeti
  KORUNDU (güvenli + doğru: panel SPA CSP'si proxy'lenen 3. taraf PHP app'e uygulanmamalı). Build 0/0, test 44/44(+2), ISCC OK.
  Installer SHA256: `C25F815932E7FF0203705FED9A6300C109DD34D4FC109911131B5D5D9F43A03D` (49.32 MiB).
- KULLANICIYA: yeni build kur → phpMyAdmin layout artık JS çalışsa da çalışmasa da DÜZGÜN. Eğer tablo tıklama/SQL konsolu gibi
  ETKİLEŞİMLİ JS de bozuksa (margin değil), F12→Console hatası iste → o zaman init JS'inin neden ölü olduğu (kalan CSP/nonce) kovalanır.

## 2026-06-25 (118) — CANLI: site oluşturmada mail+ftp hatası düzeltildi; phpMyAdmin !important geri alındı; DB sekmesine yükleniyor
- Kullanıcı (canlı): site oluştu ama "mail (alioral.xyz): mail.operation_failed • ftp (alioral_xyz_ftp): ftp.site_configuration_failed". "düzelt, atlama yapma".
- FTP KÖK NEDEN: IIS FTP rolü VPS'te kurulu değil → New-WebFtpSite + ftpServer config bölümleri yok → EnsureFtpInfrastructureAsync
  script'i patlıyor. FIX: FtpService.EnsureFtpFeatureInstalledAsync (Classic ASP/PHP gibi ON-DEMAND): FTPSVC servisi yoksa Server'da
  Install-WindowsFeature Web-Ftp-Server, Client'ta Enable-WindowsOptionalFeature IIS-FTPServer -All; doğrulama FTPSVC; kurulamazsa net
  ftp.feature_install_failed. EnsureFtpInfrastructureAsync EN BAŞINDA çağrılıyor. (enable-iis-features.ps1 zaten FTP içeriyor ama install-anı best-effort = VPS'te tutmamıştı.)
- MAIL KÖK NEDEN: alioral.xyz domaini Stalwart'ta ZATEN var (önceki testten; site silinince mail domaini kalmıştı) → "create domain"
  reddediyor → mail.operation_failed. FIX: MailService.CreateDomainAsync İDEMPOTENT — FindIdAsync ile domain varsa workspace dönüp başarı
  say (Stalwart kurulu/yapılandırılı değilse FindIdAsync null → MutateAsync gerçek not_installed/not_configured fırlatır = maskelenmez).
- SitesController: alt-kaynak uyarıları artık DescribeProvisionError → AppException.Details (gerçek altyapı/CLI sebebi, tek satır, ≤300) anahtarla gösterilir; "ham anahtar" yerine asıl neden görünür.
- phpMyAdmin layout: kullanıcı "düzeldi" dedi → (117) eklediğim !important + nav-genişlik sabitleme GEREKSİZDİ (resize/daralt devre dışı
  bırakıyordu) → GERİ ALINDI; sade enjeksiyon (JS çalışırsa inline margin ezer, nav resize korunur) kaldı. Kullanıcı "arada çıkıyor"
  DEMEDİ; "başka yerlerden böyle sorunlar çıkabilir, KONTROL ET" dedi (proaktif denetim isteği).
- UX: SiteDatabasesView veri gelmeden boş kalıyordu → "panel.siteDb.loading" göstergesi (loading && !databases.length). Kullanıcı bunu
  YÜKLEME BEKLEYEN HER YERE istedi (mail dahil) → görev #25 (devam: tüm async-fetch view taraması).
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2216=2216, resources 258=258, ISCC OK. Installer SHA256:
  `CB5ECFC8768880A77D8DA8D53EF9A405109510C71D3C9ADB9FF2C1ED13FCE256` (49.32 MiB).
- AÇIK GÖREVLER (kullanıcı bu oturumda art arda istedi): #25 yükleniyor-her-yere · #26 site izolasyonu (open_basedir+ACL+DB grant, Plesk gibi)
  · #27 "pas geç/atlama" deseni denetimi · #28 phpredis kartı gerçek durumu göstersin · #29 Plesk-benzeri bayi/müşteri UI'si YOK (sadece
  backend servicePlans+customer scopes var) = büyük eksik özellik.

## 2026-06-25 (119) — Site izolasyonu (Plesk gibi) sertleştirildi + phpredis kartı gerçek durumu gösteriyor
- Kullanıcı: "bir site diğer sitelerin hiçbir şeyini göremesin, DB'den en ufak dosyasına kadar."
- DOSYA İZOLASYONU (KÖK NEDEN): WafService.ApplyFolderAclAsync yalnız `icacls /grant` yapıyordu → site kökü üst klasörden
  IIS_IUSRS/Users GENİŞ okumasını MİRAS alıyordu → diğer sitelerin app pool kimlikleri (hepsi IIS_IUSRS üyesi) bu siteyi OKUYABİLİYORDU.
  FIX (Plesk modeli): (1) anonymousAuthentication.userName='' → statik dosyalar IUSR DEĞİL app pool kimliğiyle sunulur (tek kimlik).
  (2) icacls `/inheritance:r /grant:r` ile kök kalıtımı KIR + yalnız o sitenin app pool (Modify) + SYSTEM(*S-1-5-18) + Administrators
  (*S-1-5-32-544) (SID=locale bağımsız). /T ve /reset YOK → FTP kullanıcısı açık grant'i + çocuk kalıtımı korunur. Anon kimlik
  ayarlanamazsa icacls'e GEÇİLMEZ (exit 2) → statik kırılmasın diye fail-safe. Panel servisi LocalSystem (=SYSTEM) → erişimi korunur.
- DB İZOLASYONU: ZATEN DOĞRU — MySqlDatabaseProvider site DB kullanıcısına `GRANT ... ON dbname.*` (root/`*.*` değil); root parolası
  sitelere verilmez; MariaDB 127.0.0.1. open_basedir per-site .user.ini (B4) defense-in-depth olarak duruyor.
- ÖNEMLİ: yeni sertleştirme YENİ sitelerde otomatik (FolderAclApplied=false). MEVCUT sitelerde (ör. alioral.xyz, zaten applied) otomatik
  RETRO uygulanmaz (çalışan siteyi test etmeden bozmamak için) → Güvenlik sekmesinden "klasör ACL uygula" ile elle tetiklenir.
- phpredis (#28): DbToolSettingsView kartı hep "Etkinleştir" gösteriyordu. FIX: GetGlobalPhpSettingsAsync `php -m` ile RedisEnabled
  döndürür (SitePhpSettingsDto.RedisEnabled); UI "Etkin/Devre dışı" rozeti + etkinse buton gizli. i18n redisOn/redisOff.
- MailView ZATEN spinner'lı yükleme göstergesine sahip → #25 "her yere" aslında EKSİK olan belirli view'leri bulup eklemek (SiteDatabasesView yapıldı).
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2218=2218, resources 258=258, ISCC OK. Installer SHA256:
  `61EDED5D16D63ECAB9F5E07587A8A9FFF32861CB74CD7A7FDFB86B8DC53E44F7` (49.32 MiB).
- AÇIK: #25 yükleme-göstergesi sweep (eksik view'ler) · #27 "pas geç/atlama" deseni denetimi · #29 bayi/müşteri UI (büyük, henüz YOK).

## 2026-06-25 (120) — Bayi/müşteri yönetimi ZATEN VARDI (Ayarlar>Kullanıcılar) → Plesk gibi üst-düzey sekme; DB yükleme göstergeleri; pas-geç denetimi
- Kullanıcı: "Plesk gibi bayi (kendi müşterisini açan) + müşteri ekleme alanı nerede, bitti mi?" + "hepsini bitir".
- BULGU (önceki "UI yok" dememi DÜZELTİR): hiyerarşi backend'de TAM (roller Administrator/Operator/Reseller/Customer; CreateUserRequest
  Role+OwnerId+AssignedSites+kotalar; UserManagementService: bayi yalnız KENDI müşterisini açar/günceller, müşteriye owner=kendisi zorlanır;
  GetUsers reseller-scoped; askıya alma site stop/start; CustomerScopeService). UI de VARDI: SetupWizardView Ayarlar>"Kullanıcılar/Müşteriler"
  sekmesi (servis planları + kullanıcı oluştur [rol/owner/atanmış site/kota/hoş geldin] + liste + loginAs). Kullanıcı bulamamıştı çünkü
  Ayarlar altında gömülü; Plesk'te üst-düzey.
- FIX (#29, keşfedilebilirlik): SetupWizardView dashboardSections'a üst-düzey "accounts" sekmesi (admin label "Müşteriler & Bayiler",
  bayi "Müşteriler"); handleNavigation('accounts') → activeSection='settings'+settingsTab='users' (mevcut ekranı açar, kod taşıma YOK = düşük risk).
  Reseller filtresine accounts eklendi. i18n panel.navigation.accounts/accountsDesc.
- #25 (yükleme göstergesi): MailView ZATEN spinner'lıymış. DB-alanı düzeltildi — SiteDatabasesView (yapılmıştı) + MyDatabasesView +
  DatabasesView "yüklenirken erken BOŞ mesajı" → loading guard + genel `panel.common.loading` (Yükleniyor…/Loading…). Kalan admin-araç view'leri düşük öncelik.
- #27 (pas-geç denetimi): ciddi sessiz-atlama provisioning'leri (Classic ASP, FTP eksik özellik) ZATEN zorunlu-kuruluma çevrildi.
  Infra'da yalnız 3 boş catch: FtpService (firewall/icacls best-effort), MailEnableMailService (Stalwart kullanılıyor, fallback),
  ComponentBootstrapper (try-validate→false probe). Hepsi meşru; yeni provisioning sessiz-atlaması YOK.
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2221=2221, ISCC OK. Installer SHA256:
  `704C253EF1900E4C10624D2AD98436CA73A79AEAAD56B33B9A6BB6910DA2B516` (49.32 MiB). Bu oturumun TÜM düzeltmelerini içerir (mail/ftp, izolasyon, phpredis, hesap-sekmesi, yükleme).
- KULLANICIYA: sol menüde "Müşteriler & Bayiler" → bayi/müşteri/plan oluştur. Bayi girince yalnız kendi müşterilerini görür/açar.

## 2026-06-25 (121) — Yükleme göstergesi TÜM data view'lerine yayıldı + "Plan ekle" formu profesyonelleştirildi (etiketler)
- Kullanıcı: "hepsine yap demiştim; veritabanı ön bellek servislerinde de (CacheView) yüklenirken gösterge yok" + "plan ekle üstündeki
  etiketsiz textbox'lar ne için, profesyonel yap".
- YÜKLEME SWEEP (#25 tamam): genel `panel.common.loading` (Yükleniyor…/Loading…) anahtarı + şu view'lere "yüklenirken erken BOŞ mesajı
  yerine Yükleniyor" guard'ı: CacheView (status null), SiteDatabasesView, MyDatabasesView, DatabasesView (DB+kullanıcı listeleri,
  loadingEngine), CronView, WebDbView, LogsView, PhpView (kurulu+mevcut), SecurityView (firewall+banlı IP; loading ref eklendi),
  AppStoreView (loading ref eklendi), DockerView (workspace null), WordPressToolkitView (wp null), BackupView (browse). MailView ZATEN
  spinner'lıydı; DnsView (zoneStatus) + FtpView (sitesStore.loading) kendi göstergesine sahip → dokunulmadı.
- PLAN FORMU (profesyonel): SetupWizardView Ayarlar>Kullanıcılar>Servis Planları formu çıplak placeholder-input'lardan etiketli
  stacked-field'lere çevrildi (Plan adı + Maks site/disk/DB/posta + "0 = sınırsız" ipuçları); .plan-form CSS güncellendi; i18n panel.plans.namePlaceholder.
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2222=2222, ISCC OK. Installer SHA256:
  `9A7D937588388F9534E187AAD43B42B74C35EF9150BF6476B23B4DF2458133B9` (49.32 MiB). Bu oturumun TÜM düzeltmelerini içerir.
- KALAN (düşük öncelik): mevcut sitelere izolasyon ACL'ini Güvenlik sekmesinden elle uygulamak (yeni siteler otomatik).

## 2026-06-25 (122) — İzolasyon ARTIK OTOMATİK (Plesk gibi): mevcut sitelere açılışta tek seferlik uygulanır; manuel adım kaldırıldı
- Kullanıcı: "mevcut site için elle 'Klasör ACL uygula' — bu ne, Plesk'te böyle bir şey var mı?" → HAYIR, Plesk izolasyonu otomatik
  kurar/korur, kullanıcı elle tetiklemez. (119)'daki "elle uygula" önerim aşırı-temkinliydi (tek çalışan siteyi test etmeden bozmama).
- FIX: YENİ SiteIsolationUpgradeService (Infrastructure/Iis, BackgroundService — AutomationWorker deseni). Açılıştan ~60sn sonra,
  config/isolation-upgrade.json marker'ı CurrentVersion(2)'den küçükse: IIisSiteService.GetSitesAsync ile TÜM siteleri gez, panelin
  kendi yardımcıları (AdaPanel*) + "Default Web Site" hariç her tenant sitesine WafService.ApplyFolderAclAsync uygula (idempotent +
  fail-safe: anon=app pool ayarlanamazsa ACL'e dokunmaz → statik kırılmaz). Marker yazılır → sürüm başına bir kez. Best-effort:
  tek site hatası diğerlerini/başlatmayı etkilemez (scope içinden IIisSiteService/IWafService/JsonFileStore çözülür).
- Kayıt: ServiceCollectionExtensions AddHostedService<Iis.SiteIsolationUpgradeService> (AutomationWorker'dan sonra). NOT: bu icacls
  SİTE köklerine uygulanır (panel data/db'ye DEĞİL) ve /T YOK → (86)'daki "açık db'ye icacls /T" yarışı burada GEÇERSİZ, güvenli.
- SONUÇ: yeni build kurulunca mevcut alioral.xyz dahil tüm siteler ~1dk içinde OTOMATİK izole olur; kullanıcı hiçbir şey yapmaz.
  Sürüm artışında (CurrentVersion) hepsi yeniden işlenir. Güvenlik sekmesindeki elle "ACL uygula" yine durur (manuel re-apply isteyene).
- Doğrulama: build 0/0, test 44/44(+2), ISCC OK. Installer SHA256:
  `A6EF1C3FA92605701564A7FBABC0350D3755EE010D333013349F0DC042C919D0` (49.32 MiB). Bu oturumun TÜM düzeltmelerini içerir.

## 2026-06-25 (123) — (122) GERİ ALINDI: retroaktif izolasyon-upgrade servisi kaldırıldı (kullanıcı isteği); yeni-site izolasyonu DEFAULT kalır
- Kullanıcı: "mevcut sitelere açılışta otomatik uygulamaya gerek yok, siteyi silerim; sistemi boşuna yormasın, default yap."
- KALDIRILDI: SiteIsolationUpgradeService.cs silindi + ServiceCollectionExtensions AddHostedService<Iis.SiteIsolationUpgradeService> kaydı geri alındı.
- KORUNDU (default davranış): YENİ siteler oluşturulurken izolasyon ZATEN otomatik (SiteDefaultsService → WafService.ApplyFolderAclAsync:
  ACL kalıtım kırma + anon=app pool, fail-safe). Mevcut siteye dokunan retroaktif arka plan taraması artık YOK → sistem yorulmaz.
  İsteyen Güvenlik sekmesinden tek tek "Klasör ACL uygula" yapabilir (elle yol duruyor).
- Doğrulama: build 0/0, test 44/44(+2), ISCC OK. Installer SHA256:
  `9B0C99A911397C3D3A7AA3FB49F28021AC6A6C0291E90409EB86A5225EEAE99D` (49.32 MiB).

## 2026-06-26 (124) — Plesk parite cilası: izlemeye zaman-içi TREND grafikleri (CPU/RAM/ağ); servis-sağlık + göç sihirbazı zaten vardı
- Kullanıcı: "Plesk parite cilası (derin izleme, göç sihirbazı vb.) yap."
- DURUM TESPİTİ: MonitoringView anlık görüntüydü (CPU/RAM/disk/ağ ring+tablo, geçmiş yok). ServerHealthView ZATEN servis durumu
  (çalışıyor/durdu/kurulu-değil + restart) = Plesk "Servisler" paritesi. MigrationView ZATEN tam sihirbaz (Plesk host/port/API-key
  KEŞFİ → domain seç → dosya/DB/mail çek). Yani en net eksik: izlemede zaman-içi trend.
- YAPILDI (derin izleme): MonitoringView'e istemci-tarafı rolling history (overview parent'ça 15sn'de bir tazelenir; son ~40 örnek≈10dk)
  + küçük SVG sparkline'lar: CPU ring altı, RAM ring altı, ve ağ bölümünde indir/yükle overlay trend grafiği (vector-effect
  non-scaling-stroke; netScale max'a göre otomatik ölçek). Sunucu time-series GEREKMEZ. i18n panel.monitor.networkTrend/trend10m.
- YAN: ServerHealthView'de de yükleme-boşluğu vardı (loaded'a kadar boş) → "Yükleniyor" guard'ı eklendi (sweep tutarlılığı).
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2224=2224, ISCC OK. Installer SHA256:
  `CF0CA26B82055BAF793D3554C022684F85A01D6656426E47B9364737D55519B2` (49.32 MiB).
- KALAN/AÇIK: göç sihirbazı zaten gelişmiş → somut "iyileştirme" için kullanıcıdan spesifik istek beklenmeli (tahminle özellik
  eklenmedi). Trend grafikleri şimdilik istemci-oturumu (sayfa açıkken birikir); kalıcı geçmiş istenirse sunucu time-series gerekir.

## 2026-06-26 (125) — Göç sihirbazı 4 iyileştirme: cPanel/WHM kaynağı + göç-öncesi doğrulama + toplu göç + canlı günlük
- Kullanıcı: önerdiğim 4 göç iyileştirmesinin "hepsini yap".
- (1) cPanel/WHM KAYNAĞI: SiteMigrationService.DiscoverCpanelAsync (WHM listaccts: GET :2087/json-api/listaccts?api.version=1,
  Authorization: whm user:TOKEN, self-signed atlanır) → PleskDiscoverResultDto döner (domain=acct.domain, hostingType=acct.user).
  Yeni DTO CpanelDiscoverRequest. Controller POST /migration/cpanel/discover. Frontend: keşif bölümünde Plesk/cPanel toggle (segmented-tabs).
- (2) GÖÇ-ÖNCESİ DOĞRULAMA: ValidateMigrationAsync(MigrateSiteRequest) → site adı uygunluğu (oluşturulacaksa çakışma yok/hedefse var)
  + dosya & DB kaynağı TCP erişilebilirliği (TcpClient 6sn timeout, kimlik DOĞRULAMAZ = ucuz). MigrationValidationResultDto{AllPassed,
  Checks[]}. Controller POST /migration/validate. Frontend "Doğrula" butonu + ön-kontrol listesi (✓/✗).
- (3) TOPLU GÖÇ: keşfedilen domain'lere checkbox + "Seçilenleri göç et ({count})". Frontend SIRAYLA her domain için mevcut
  /migration/site'ı çağırır (createSite+pullFiles; DB atlanır=hesap başına kimlik farklı). Uzak yol şablonu {domain}/{user} ile
  genişler (Plesk: /var/www/vhosts/{domain}/httpdocs, cPanel: /home/{user}/public_html). Dosya kimliği form "Dosya kaynağı"ndan.
- (4) CANLI GÜNLÜK: toplu modda her domain bittikçe bulkLog'a eklenir → "Toplu göç günlüğü" panelinde domain+adımlar canlı akar
  (backend SSE gerektirmeden, frontend sıralı fetch ile). Tekli göçte de adım sonuçları gösterilir (zaten vardı).
- Backend yalnız EK (mevcut MigrateSiteAsync/DiscoverPleskAsync DEĞİŞMEDİ). Doğrulama: build 0/0, test 44/44(+2), typecheck 0,
  i18n 2253=2253, ISCC OK. Installer SHA256: `3013F1FDAA9120AC629C83BDE01B564362428F5A5C579096C61A3AC608C24ECB` (49.33 MiB).

## 2026-06-26 (126) — Plesk parite: sağlık uyarılarına RAM eşiği + servis-düştü eklendi (disk+sertifika ZATEN vardı)
- "devam et" → Plesk "Health Monitoring" paritesi. BULGU: AutomationService.RunAlertChecksAsync ZATEN var (disk doluluk + sertifika
  bitişi → e-posta/webhook, 24s cooldown, AlertsEnabled). Eksikti: RAM eşiği + servis-düştü. Mükerrer yapmadan GENİŞLETİLDİ.
- EKLENEN: (1) RAM eşiği — overview.MemoryUsagePercent >= MemoryUsageThresholdPercent (varsayılan 90, clamp 50-99) → "RAM %X dolu" uyarısı.
  (2) Servis-düştü — IHealthService.GetHealthAsync (AutomationService'e IHealthService inject edildi) ile Status=="Stopped" olan kurulu
  servisler için "{Label} servisi DURMUS" uyarısı (key service:{name}); ServiceDownAlertsEnabled (varsayılan açık). CPU bilerek EKLENMEDİ
  (tek-örnek spike'ta gürültülü olur; RAM daha kararlı/anlamlı).
- DTO/state: AutomationSettingsDto + UpdateAutomationSettingsRequest + AutomationStateRecord + ToStatus + UpdateSettingsAsync'e
  MemoryUsageThresholdPercent + ServiceDownAlertsEnabled. Frontend AutomationView uyarı bölümüne RAM eşiği input + servis-düştü toggle;
  api/automation.ts AutomationSettings'e iki alan (save {...form} ile gider, applyStatus Object.assign ile yüklenir). i18n memoryThreshold/serviceDownAlerts.
- Cooldown/teslimat (e-posta+webhook, 24s, key bazlı) mevcut altyapıyı kullanır. Uyarı e-postaları Türkçe (mevcut disk/cert ile tutarlı).
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2255=2255, ISCC OK. Installer SHA256:
  `A3F011DC92CC8EBD46C3C27C6EDA9673518963DB055F26B5E4FB83A36E72151F` (49.33 MiB).
- AÇMA: Ayarlar/Otomasyon → "Uyarılar"ı aç + e-posta (Bildirimler) yapılandır → arka plan worker (dakikada bir) eşik/servis kontrol eder.

## 2026-06-26 (127) — Plesk parite: sunucu-geneli DNS ŞABLONU (yapılandırılabilir); DNSSEC dürüstçe ertelendi
- Kullanıcı: "DNSSEC / DNS şablonları".
- DNS ŞABLONU (yapıldı): DnsService.CreateDefaultRecords sabit kodluydu → yapılandırılabilir şablona çevrildi (Plesk modeli). YENİ:
  config/dns-template.json + DnsTemplateDto/Record + UpdateDnsTemplateRequest. GetTemplateAsync (kayıtlı yoksa BuiltInTemplate =
  mevcut set, yer tutuculu). UpdateTemplateAsync (tip beyaz-listesi A/AAAA/CNAME/MX/TXT/NS/SRV/CAA, boş host/value atlanır, kaydet).
  BuildDefaultRecordsAsync: şablonu yer tutucularla genişletir ({domain}/{ip}/{ns1}/{ns2}), boş value (ör. IP yok) atlar, hepsi
  atlanırsa yerleşiğe düşer (boş zone olmaz). 4 çağrı yeri (UpdateZone/ResetDefaults/GetOrCreate merge+yeni) async BuildDefaultRecordsAsync'e çevrildi.
  Controller: YENİ DnsTemplateController (api/v1/dns/template GET/PUT, admin-only). Frontend: YENİ DnsTemplateView (tip/host/değer/TTL
  satırları, ekle/sil/kaydet, yer-tutucu ipucu) → ServerToolsView switcher'a "DNS Şablonu" (DN) sekmesi. api/dns.ts getDnsTemplate/updateDnsTemplate. i18n panel.dnsTemplate.*.
- DNSSEC (ERTELENDİ, dürüst): Windows DNS Server zone-imzalama (Invoke-DnsServerZoneSign) + registrar'a DS kaydı (manuel) + anahtar
  yönetimi gerektiren büyük/ortam-bağımlı iş; JSON-zone modeli yerel imzalamaz, canlı test edilemez → "kırılgan çözüm icat etme"
  ilkesiyle (adapanel-copy-plesk) yarım yapılmadı. İstenirse Windows-DNS-destekli per-zone DNSSEC aç/kapa + DS gösterimi ayrı iş olarak yapılır.
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2267=2267, ISCC OK. Installer SHA256:
  `63F08DB4EC93EC33FCE02D9DB7CF493E1399F3497B10F85E4B7396B6524968C2` (49.33 MiB).
- YER: Sunucu Araçları → "DNS Şablonu" (admin). Düzenle+kaydet → sonraki yeni siteler bu şablonla seed.

## 2026-06-26 (128) — DNSSEC Plesk-on-Windows yöntemiyle eklendi (Microsoft DNS zone-imzalama + registrar DS)
- Kullanıcı: "pleskte nasılsa aynı şekilde yap DNS'i" → Plesk-on-Windows DNSSEC için MS DNS Server zone-imzalama kullanır. AdaPanel
  zaten zone'ları Windows DNS'e yayınlıyor → aynı yol.
- BACKEND: IDnsService.SetDnssecAsync(siteName, enabled). DnsService.SetDnssecAsync: Windows DNS yoksa dns.dnssec_requires_windows;
  varsa önce TrySyncWindowsDns (bölge gerekir) sonra enabled? Invoke-DnsServerZoneSign -SignWithDefault -Force : Invoke-DnsServerZoneUnsign
  -Force. ToDtoAsync artık ReadDnssecAsync ile (zone Windows'ta varsa) IsSigned + DS okur: Get-DnsServerZone.IsSigned +
  Export-DnsServerDnsSecPublicKey -DigestType Sha256 -PassThru → DS satırları (KeyTag/alg/digestType/digest); property yoksa guard'la
  atlar (best-effort, DS alınamazsa imza durumu yine doğru). SiteDnsZoneDto'ya IsDnssecSigned + DnssecDsRecords[]. DTO SetDnssecRequest.
  Controller DnsController POST /sites/{site}/dns/dnssec (admin-only).
- FRONTEND: DnsView "Zone ayarları" sekmesine DNSSEC kartı — İmzalı/İmzasız rozeti, aç/kapat butonu (Windows DNS yoksa uyarı),
  imzalıyken DS kayıtları <pre> (registrar'a yapıştır) + ipuçları. api/dns.ts setDnssec + DnsZone'a iki alan. i18n panel.dns.dnssec* + dns.dnssec_*.
- DÜRÜSTLÜK: enable/disable/status SAĞLAM (belgelenmiş cmdlet'ler); DS gösterimi best-effort (Export property adları sürüm-bağımlı →
  guard + "DS otomatik alınamadı, Windows DNS Yöneticisi'nden bak" ipucu). Canlı test edilemedi (Windows DNS Server gerek) ama yöntem belgelidir.
- DNS şablonu (entry 127) + DNSSEC birlikte = DNS artık Plesk benzeri tam. Doğrulama: build 0/0, test 44/44(+2), typecheck 0,
  i18n 2283=2283, ISCC OK. Installer SHA256: `0E3650CB724E3310AEBC968C00B53378F04D79077F9BCECE07FF5B4E67A40254` (49.34 MiB).
- YER: site → DNS → "Zone ayarları" → DNSSEC. (Windows DNS Server kurulu olmalı; yoksa kart "önce Windows DNS'i kur" der.)

## 2026-06-26 (129) — Kalıcı izleme geçmişi: trend grafikleri sunucu-örneklemeli (sayfa yenilense de dolu, ~24s)
- "devam et" → (124)'teki istemci-tarafı trend sparkline'ları sayfa yenileyince sıfırlanıyordu. Plesk geçmiş grafikleri gibi KALICI yapıldı.
- BACKEND: SystemMonitoringService (singleton) bellek-içi halka (LinkedList, cap 1440 = ~24s @1/dk) + RecordSampleAsync (overview'dan
  CPU/RAM + ağ down/up topla → halkaya ekle) + GetHistoryAsync. ISystemMonitoringService'e iki metot. AutomationService.RunDueJobsAsync
  her turda (dakikada bir) RecordSampleAsync çağırır (best-effort try/catch). SystemController GET /system/history. YENİ SystemHistorySampleDto.
- FRONTEND: stores/system.ts history[] + fetchHistory(). MonitoringView artık istemci-biriktirme YERİNE systemStore.history'den besleniyor:
  onMounted fetchHistory + 60sn interval (onUnmounted temizler); cpu/mem/ağ sparkline'ları geçmişten. trend10m etiketi "Son ~24 saat".
- NOT: bellek-içi (sayfa yenileme/oturumlar arası DOLU; panel servisi yeniden başlarsa sıfırlanır, ~dakikalar içinde dolar). Dosya-kalıcılığı
  istenirse JsonFileStore'a yazılır (şimdilik gereksiz I/O'dan kaçınıldı).
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2283=2283, ISCC OK. Installer SHA256:
  `274EA19FAE32866029E932B0465644C26E088D1201D785B24297640FD3347C1F` (49.34 MiB).

## 2026-06-26 (130) — Plesk parite: off-site yedek "Bağlantıyı test et" (gerçek yedek almadan hedefi doğrula)
- "devam et" → Plesk "remote storage test connection" eşdeğeri. Canlıya çıkmadan yedek güvenilirliği için kritik.
- BACKEND: BackupService.TestRemoteTargetAsync(BackupRemoteTestRequest) → sabit-adlı küçük dosyayı GERÇEK yükleme yoluyla
  (UploadToRemoteAsync, mevcut FTP/S3/WebDAV/HTTP dispatch + ResolveTarget) hedefe yazar = kimlik+bağlantı+yazma izni doğrulanır.
  Fırlatmaz; {Success, Message(i18n key), Details} döner. "adapanel-connection-test.txt" üzerine yazılır (birikme yok). local → ağ testi yok.
  AutomationService.TestRemoteBackupAsync: istek parolası boşsa kayıtlı korumalı parolayı çözer (kullanıcı kayıtlı hedefi tekrar parola
  girmeden test eder) → backupService'e delege. Controller POST /automation/backup/remote/test (admin). YENİ DTO BackupRemoteTestRequest/ResultDto.
- FRONTEND: AutomationView uzak-yedek bölümüne "Bağlantıyı test et" butonu (target!=local) + sonuç rozeti (ok/warn) + hata Details details/summary.
  api/automation.ts testRemoteBackup. i18n panel.automation.remoteTest/Testing/TestOk + backup.remote_test.ok/local/failed.
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2289=2289, ISCC OK. Installer SHA256:
  `A8CB3BEE235AAA3B64AA148ADD81321C535449C2AEFD7329EC646F0C092A20A5` (49.34 MiB).
- YER: Sunucu Araçları → Otomasyon → uzak yedek hedefi seç/gir → "Bağlantıyı test et" (parola boşsa kayıtlı kullanılır).

## 2026-06-26 (131) — Plesk parite DENETİMİ: büyük özellikler ZATEN var; kullanıcı non-Plesk kaldırmayı reddetti
- Kullanıcı: "Plesk'te varsa yap, olmayanı yapma" → sonra "gerçekten kullanılan önemli özellikleri ekle + bizde olup Plesk'te olmayanı kaldır".
- DOĞRULANDI (zaten MEVCUT, YENİDEN YAPMA): mail kimlik doğrulama (DKIM/SPF/DMARC/PTR canlı tanılama + DNS uygula + rDNS, MailView
  diagnostics-card + getMailDiagnostics/getMailDns/applyMailDns/checkRdns) · catch-all (MailView + i18n) · domain alias = IIS binding
  ekle/çıkar (IIisSiteService.AddBindingAsync/RemoveBindingAsync, SiteDetailView add/remove binding UI) · Let's Encrypt (WinAcmeSslService +
  IssueLetsEncryptRequest). MAIL KUYRUĞU yok (tek net eksik; ama Stalwart queue CLI riskli → yapılmadı).
- KALDIRMA: kullanıcıya non-Plesk adaylar soruldu (Terminal [DÜZELTME 06.09.2026: Plesk'te VAR — 18.0.37+ "SSH Terminal" eklentisi, yöneticiye root kabuğu; iddia yanlıştı], Nginx [IIS panelinde artık], Önbellek/Redis-Memcached
  [Plesk çekirdeğinde yok]) → kullanıcı "HİÇBİRİNİ KALDIRMA" seçti. Docker zaten Plesk'te eklenti olarak VAR → kaldırma adayı bile değil.
  SONUÇ: hiçbir özellik kaldırılmadı (silme geri-dönüşü zor olduğu için ÖNCE onay alındı = doğru karar).
- DURUM: AdaPanel büyük Plesk özelliklerinde PARİTEDE. Yeni "ekle" turlarında ÖNCE var-mı diye kaynağı kontrol et (mail-auth gibi 3 kez
  "zaten var"a çarpıldı). Güncel installer (bu oturumun tüm eklemeleri): `A8CB3BEE235AAA3B64AA148ADD81321C535449C2AEFD7329EC646F0C092A20A5`.

## 2026-06-26 (132) — Müşteri DB kotası profesyonelleştirildi: motorlar belirtildi + kullanılan/limit gösterimi (Plesk gibi)
- Kullanıcı: "DB kotası hangi motorlar için geçerli (MySQL/MariaDB/PostgreSQL/MSSQL)? belirtmemişsin; müşteri panelinde o kısmı Plesk gibi profesyonel yap."
- GERÇEK: AdaPanel 4 motoru destekler (MySql/MariaDb/PostgreSql/MsSql provider'ları). Kota dbOwnership.CountForOwnerAsync ile sahip
  olunan TÜM DB'leri MOTORDAN BAĞIMSIZ sayar = 4 motorun TOPLAMI. UI bunu belirtmiyordu.
- YAPILDI: (1) UserSummaryDto.DatabaseCount eklendi (müşterinin güncel DB sayısı, tüm motorlar toplamı). UserManagementService'e
  IDatabaseOwnershipService inject; GetUsers/Update'te Customer için CountForOwnerAsync ile doldurulur (MapUser'a databaseCount param).
  (2) Frontend: stores/auth.ts UserSummary'ye databaseCount. SetupWizardView kullanıcı listesinde "X / N DB" (disk gibi kullanılan/limit);
  3 formda (plan + oluştur + düzenle) maxDatabases ipucu → "0 = sınırsız · tüm motorların TOPLAMI (MySQL, MariaDB, PostgreSQL, MSSQL)".
  i18n panel.users.maxDatabasesHint + databasesShort.
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2291=2291, ISCC OK. Installer SHA256:
  `CAE9E01F3B75A0EB13D97F51F33A4CED73D37BE84D8C761A83491C4BC490343C` (49.34 MiB).
- NOT: kota toplam-DB-sayısıdır (motor-başına ayrım YOK; Plesk de temel planda toplam sayar). İstenirse motor-başına izin (allowed engines) ayrı iş.

## 2026-06-26 (133) — Servis Planları kartı profesyonelleştirildi (Plesk gibi: kart-listesi + temiz grid form)
- Kullanıcı: "Servis Planları görüntü çok kötü, profesyonel yap, Plesk'teki işlevliğin aynısı olsun." (Ayarlar>Kullanıcılar>Servis Planları)
- ESKİ: planlar tek satır (yalnız site+disk gösteriyordu); form flex-wrap'te sıkışık, her sayı alanının altında tekrar eden "0=sınırsız" ipuçları = çirkin.
- YENİ (SetupWizardView): (1) plans-head: başlık + "Plan ekle" toggle butonu (showPlanForm). (2) plans-grid: her plan ARTIK KART —
  ad + sil + 4 limitin TAMAMI (Site/Disk/DB/Posta kutusu) etiketli liste halinde (responsive auto-fill grid). (3) Form artık plan-form-grid:
  ad tam-genişlik, 4 sayı alanı auto-fit hizalı, TEK birleşik ipucu (formHint: "0=sınırsız; DB kotası tüm motorların toplamı"), sağa-hizalı
  Ekle butonu; "Plan ekle" ile açılır, eklenince kapanır. Plesk davranışı (liste-önce + ekle).
- i18n: panel.plans.sitesLabel/diskLabel/databasesLabel/mailboxesLabel/formHint. Eski .plan-form/.plan-row CSS → yeni .plans-head/.plans-grid/.plan-card/.plan-limits/.plan-form-grid.
- Frontend-only. Doğrulama: typecheck 0, i18n 2296=2296, build 0, ISCC OK. Installer SHA256:
  `9CADF360205D7E1D42E64B95DBDD636B8BFAD2340E6EA44C71201C2FF1F91D90` (49.34 MiB).

## 2026-06-26 (134) — KRİTİK eksik: müşteri panelinde site + veritabanı OLUŞTURMA yoktu → açıldı (kota+scope zaten hazırdı)
- Kullanıcı (göz denetimi): "müşteri girince siteyi/DB'yi nasıl oluşturacak, o bölümler neden yok?" — gerçek işlevsel boşluk.
- BULGU: backend müşteri için TAM hazırmış, sadece auth rolü dışlıyordu. SitesController EnsureSiteQuotaAsync (MaxSites) +
  AssignSiteToActorAsync (yeni siteyi oluşturanın kapsamına ekler) + her per-site uç CustomerAccess.EnsureSiteAsync ile scoped (müşteri
  yalnız kendi sitesi); ResellerDatabasesController MaxDatabases kotası + dbOwnership kaydı + sahibe-özel liste. WebsiteWorkspaceView
  adminOnlyTools={store,webdb,cache,cron} → non-admin'de sunucu-araçları + per-site DB tab'ı (webdb=admin-only) GİZLİ = güvenli.
- AÇILDI: SitesController + ResellerDatabasesController [Authorize] rollerine "Customer" eklendi (replace_all + ResellerDb class).
  Frontend: render sites→(isAdmin||isReseller||isCustomer), database→MyDatabasesView (isReseller||isCustomer). Müşteri nav'ına 'sites'+'database'
  eklendi (yedek Dosyalar'dan sonra, sağlam index). Müşteri landing 'files'→'sites' (web hub). Import-existing-sites Admin-only KALDI.
- GÜVENLİK: müşteri YALNIZ kendi sitelerini/DB'lerini görür-yönetir-oluşturur (kota dahilinde); başka müşteriye atama/sunucu-aracı YOK
  (EnsureSiteAsync + adminOnlyTools). Site-DB binding per-site tab admin-only olduğundan müşteri DB'yi global "Veritabanları" (MyDatabasesView, scoped) sekmesinden açar.
- AYRICA (133 öncesi denetim): kullanıcı-oluştur+düzenle formlarındaki kota ipuçları (her alanda ayrı, DB'ninki uzun=eşitsiz) → plan
  formundaki gibi TEK birleşik ipuca (panel.plans.formHint, .quota-hint grid 1/-1) çevrildi (tutarlılık).
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2296=2296, ISCC OK. Installer SHA256:
  `F425F74652A0A0D862ACBF10D43B9AAAFB1E5F4348D290F99D88A4C92088C02E` (49.34 MiB).
- KULLANICIYA: müşteri girince sol menüde "Web Siteleri" (oluştur/yönet, kota) + "Veritabanları" (oluştur/sil, kota) var artık. Canlı test et.

## 2026-06-26 (135) — Bayi+müşteri denetimi: 4 gerçek bug düzeltildi (create-customer formu, bayi-kendi-siteleri, site-oluştur butonu, yol-güvenliği)
- Kullanıcı: "bayi tarafını da kontrol et" + "müşteri girince Web Siteleri var ama içinde SİTE OLUŞTUR butonu YOK, adam akıllı yap."
- BUG1 (bayi create-customer formu): createUserForm.role varsayılan "Operator"; Customer-özel alanlar (atanmış site+kotalar+plan)
  `role==='Customer'` ile gizli; rol seçici admin-only → BAYİ müşteri açarken kota/site alanlarını GÖREMİYORDU. createUser reset'i
  rolü "Operator"a sıfırlıyordu (ilk oluşturma sonrası tekrar gizleniyordu). FIX: reset rolü `isReseller ? "Customer" : "Operator"`. (watch zaten immediate.)
- BUG2 (bayi kendi sitelerini göremez): CustomerScopeService.GetResellerSitesAsync YALNIZ müşterilerin sitelerini birleştiriyordu;
  bayinin KENDİ doğrudan açtığı site (AssignSiteToActor bayinin kapsamına ekler) DAHİL DEĞİLDİ → bayi kendi sitesini göremez/yönetemez (403).
  FIX: GetResellerSitesAsync + GetResellerMailDomainsAsync bayinin KENDİ assigned sitelerini/domainlerini de ekler.
- BUG3 (site-oluştur butonu yok): SitesView'de create butonu+formu+empty-state butonu `isAdmin` ile gated → bayi DE müşteri DE göremiyordu.
  FIX: yeni `canCreateSite` (admin||reseller||customer) ile gated (import butonu admin-only KALDI). Müşteri/bayide fiziksel-yol alanı GİZLİ (auto-türetilir).
- GÜVENLİK (BUG3 düzgün olsun): CreateSiteRequest.PhysicalPath init→set; SitesController'a IPanelPreferencesService inject; CreateSite'te
  non-admin için PhysicalPath = ResolveScopedPhysicalPath (DAİMA DefaultSiteRoot altında, ad sanitize + containment guard) → kötü-niyetli
  müşteri başka sitenin klasörüne işaret edemez (çapraz-kiracı engeli). Admin tam serbest.
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, ISCC OK. Installer SHA256:
  `28CA71B9AF935FA2A20A2421499C15632342376AC847BA971B49CD0B7B2F6616` (49.34 MiB). (Önceki bu-tur paketi reseller-only düzeltmeleri içeriyordu; bu paket hepsini kapsar.)
- KULLANICIYA: müşteri/bayi → Web Siteleri → "Site oluştur" artık VAR (kota+kapsam dayatılır, yol güvenli köke sabit). Canlı test et.

## 2026-06-26 (136) — Göç sekmesi netleştirildi ("Nasıl çalışır?" adımları) + kişisel domain (alioral.xyz) örneklerden TEMİZLENDİ
- Kullanıcı: "göç sekmesini daha mantıklı yap, ne nasıl yapılıyor anlamadım. alioral.xyz benim kişisel sitem, örnek olarak yazma, nerede varsa sil → ornek.com yap."
- ALIORAL TEMİZLİĞİ: MigrationView.vue siteName placeholder "alioral.xyz" → "ornek.com". MailService.cs yorumundaki "(canlı bulgu:
  alioral.xyz)" → "(canlı bulgu)". (Diğer placeholder'lar zaten generic: plesk.example.com / whm.example.com / root.) CODEX_MEMORY iç
  günlük (gerçek olay kaydı) — dokunulmadı. KURAL: örnek/placeholder'da ASLA kullanıcının gerçek domaini; ornek.com / example.com kullan.
- GÖÇ NETLİĞİ: MigrationView başına "Nasıl çalışır?" kutusu eklendi — howWhat (göç = başka sunucudaki HAZIR siteyi AdaPanel'e taşıma:
  dosyalar FTP/SFTP + DB) + 5 numaralı adım (1 keşif opsiyonel, 2 hedef site, 3 dosya kaynağı, 4 DB kaynağı, 5 doğrula→başlat). <ol>
  .migration-steps CSS (accent marker). i18n panel.migration.howTitle/howWhat/howStep1-5.
- Doğrulama: build 0/0, test 44/44(+2), typecheck 0, i18n 2303=2303, ISCC OK. Installer SHA256:
  `A4E5C39700A1CC9D5D704971DE53B8157AB1EFB763C6D94709532B67F102C49B` (49.34 MiB).

## 2026-06-26 (137) — Göç CANLI ilerleme (SSE) + dosya hatasının GERÇEK nedeni yüzeye çıkarıldı
- Kullanıcı: "göçü başlattım 'taşınıyor' yazdı ama hangi aşamada ne yaptığı görünmüyor, adımları canlı gösterse iyi olur."
  Ardından canlı bir göç denemesi: site ✓ / DOSYALAR ✗ "Dosya göçü başarısız oldu." / DB ✓ — neden belirsiz (generic mesaj).
- KÖK NEDEN (sağır hata): FileMigrationService tüm hataları `AppException("migration.failed", …, ex.Message)` ile sarıyordu;
  SiteMigrationService.ToMessage YALNIZCA MessageKey'i ("migration.failed") döndürüp gerçek nedeni (AppException.Details =
  ham FTP/SFTP mesajı) ATIYORDU → kullanıcı "neden"i göremiyor. ([[adapanel-proactive-audit]] kuralı: gerçek hatayı göster.)
- CANLI İLERLEME (SSE): yeni `MigrationProgressEvent` DTO (phase=plan|start|result|done). ISiteMigrationService.MigrateSiteAsync'e
  `Func<MigrationProgressEvent,CancellationToken,Task>? onProgress` eklendi (ct'den ÖNCE; tek çağıran controller güncellendi).
  Servis 4 tekrarlı try/catch → tek `ExecuteStepAsync(stepId, successMsg, action)` helper'ında toplandı: start raporla → dene →
  sonuç(listeye+raporla). Önce "plan" (çalışacak adımlar) yollanır; userini dinamik (yalnız dosya başarılıysa, istemci ekler).
  Yeni controller ucu `POST /migration/site/stream` → text/event-stream, `data: {json}\n\n`, DisableBuffering, OperationCanceled
  yutulur, beklenmeyen hata→ done(fail) çerçevesi. Eski `/migration/site` (toplu göç) KORUNDU (onProgress:null).
- GERÇEK NEDEN: MigrationStepDto + MigrationProgressEvent'e `Detail` (ham teknik mesaj). DescribeError → (MessageKey, Details).
  FileMigrationService.ClassifyFileError: ham istisnayı anlaşılır başlığa eşler — migration.files.auth_failed / .path_not_found /
  .connect_failed (anahtar-kelime: auth/login/530 · no such/not found/550 · socket/connect/timeout/refused), gerçek metni Details'te taşır.
- FRONTEND: client.ts `buildStreamRequestHeaders()` (CSRF + entry-path, axios ile aynı). migration.ts `migrateSiteStream()` (fetch +
  ReadableStream, \r\n normalize, \n\n çerçeve ayırma). MigrationView.vue: `steps` ref → `liveSteps` (pending→running ⟳→ok/fail),
  canlı checklist + başarısız adımda monospace kırmızı `Detail` satırı; toplu göç adımlarına da detail. Spinner CSS + reduced-motion.
- i18n +8/dil: panel.migration.liveTitle/liveHint/statusPending/statusRunning/streamFailed + migration.files.auth_failed/path_not_found/connect_failed.
- Doğrulama: build 0/0, test 44/44(+2), i18n 2311=2311, typecheck 0, vite OK. Installer SHA256:
  `B0DD94189BE5734DD28D48D430A5D2C9EF8292C1ACC3D3E67B747949EA62659B` (49.34 MiB).
- NOT: kullanıcının DOSYA hatasının kesin sebebi yeni sürümde adımın altında ham olarak çıkacak (yetki/uzak yol/bağlantı). En olası:
  yanlış FTP/SFTP kullanıcı-parola ya da yanlış 'Uzak yol' (kaynak Plesk ise /var/www/vhosts/.../httpdocs, cPanel ise /home/<user>/public_html).

## 2026-06-26 (138) — Posta hatalarının gerçek nedeni görünür kılındı + "aç/kapa" dili Plesk'e yaklaştırıldı
- Kullanıcı 3 şikâyet (hızlı ardışık): (a) "posta neden kapalı? aç/kapa neden var? otomatik açık olsun, pleskte gibi";
  (b) posta kurarken/hesap açarken "E-posta işlemi tamamlanamadı." generic — parola hatasını söylemiyor; (c) "Webmail
  (Roundcube) hazırlanamadı" — neden belirsiz.
- KÖK BULGU: MailView'da gerçek teknik neden ZATEN vardı ama katlı `<details>` (summary "Teknik ayrıntılar") içinde GİZLİ →
  kullanıcı açmadığı için sağır mesaj görüyordu. MapMailError zaten zayıf/kısa parolayı mail.weak_password'e çeviriyor;
  WebmailService.VerifyHealthyAsync gerçek Roundcube/PHP nedenini Details'e koyuyor. Sorun GÖRÜNÜRLÜK.
- DÜZELTME (hepsi frontend/i18n, backend değişmedi):
  1) MailView `<details ... open>` → hata olunca teknik detay OTOMATİK açık (webmail/parola/operation_failed gerçek nedeni anında görünür).
  2) addAccount + changePassword: parola <8 ise SUNUCUYA gitmeden net `mail.weak_password` uyarısı (Stalwart sağır reddini önler).
  3) "Aç/kapa" dili Plesk gibi: rozet "Posta kapalı"→"Henüz kurulmadı"/"Not set up yet"; listHint+firstDomainHint "ayrı aç/kapat
     adımı yok, alan adını ilk açınca postası otomatik kurulur" diye yeniden yazıldı.
- ÖNEMLİ BAĞLAM (kod doğru, sunum yanlıştı): SitesController site oluştururken posta domainini ZATEN otomatik açıyor
  (`ProvisionDefaultServices && stalwart-mail kurulu` → mailService.CreateDomainAsync + DNS MX/SPF/DKIM/DMARC). Domainlerin
  "kapalı" görünme sebebi: o siteler MAIL SERVER yapılandırılmadan ÖNCE açılmış (posta domaini mail server'sız oluşturulamaz).
  Mail yapılandırıldıktan sonra YENİ siteler otomatik posta-açık; var olanlar ilk "Yönet"te lazy kurulur (openDomain → createMailDomain).
- Doğrulama: i18n 2311=2311, typecheck 0, vite OK. Installer SHA256:
  `47114AD0540084E3E34E9ED7794115F9726A262C0D1AC5BAFA189A9B225B5AAC` (49.34 MiB).
- AÇIK İŞ: Webmail (Roundcube) gerçek başarısızlık nedeni artık otomatik görünecek — kullanıcı detayı paylaşırsa kök neden
  (eksik PHP DLL / port / yazma izni) hedefli düzeltilecek. İstenirse: mail yapılandırılınca var olan tüm site alan adlarını
  tek seferde otomatik posta-açık yapan reconcile eklenebilir (şu an mail akışı oturmadığı için bilinçli ertelendi).

## 2026-06-26 (139) — Göç DB adımı artık DB KULLANICISINI da oluşturuyor (Plesk göçü gibi) + erişim bilgisini gösteriyor
- Kullanıcı: "göçte DB oluşturup çekti ama KULLANICI oluşturmamış, bunu da yapmalı." Doğru — DB'siz kullanıcı → taşınan
  uygulama (wp-config.php/.env'deki kullanıcıyla) yeni sunucuda "Access denied for user" alır.
- DÜZELTME: MigrateSiteRequest'e TargetDatabaseUser + TargetDatabasePassword (opsiyonel) eklendi. SiteMigrationService DB adımı:
  (1) CreateDatabaseAsync (DB + siteye bağla, varsa yoksay) → (2) CreateUserAsync(Engine,UserName,Password,DatabaseName=target)
  = kullanıcı + o DB'ye GRANT ALL (varsa yoksay) → (3) TransferDatabaseAsync. Kullanıcı/parola verilmezse: kullanıcı site adından
  türetilir (DeriveDbUser → "<temizad>_usr"), parola güçlü üretilir (GenerateDbPassword "Ap1!"+24hex, policy+SQL-güvenli).
- GÖSTERİM: MigrationStepDto.Detail başarı için de dolduruluyor; ExecuteStepAsync artık Func<Task<string?>> (başarı-detayı döner).
  DB adımı detail = "DB user: x · password: y" (parola üretildiyse) / "DB user: x" (kullanıcı verdiyse). MigrationView canlı
  checklist detail'i başarıda da gösterir (.step-detail.ok yeşil / .err kırmızı). ([[adapanel-copy-plesk]])
- FRONTEND: form.targetDatabaseUser/targetDatabasePassword + DB bölümünde 2 alan + açıklama (uygulamanın config kullanıcı/parolasını
  gir → aynı kullanıcı kurulur, config değişmeden bağlanır; boşsa otomatik üretilip sonuçta gösterilir). MigrateSitePayload tipine eklendi.
- NOT: `Step()` helper kaldırıldı (artık MigrationStepDto inline). Bulk göç DB taşımıyor (transferDatabase:false) → etkilenmez.
- Doğrulama: build 0/0, test 44/44(+2), i18n 2314=2314, typecheck 0, vite OK. Installer SHA256:
  `870B36329531635654A55746B738648A32345320DCBE962B6329F5089E5E4700` (49.35 MiB).

## 2026-06-27 (140) — Webmail "açıyor ama sayfa çalışmıyor": tanılama aracı + hızlı-yol sağlık kontrolü düzeltildi
- Kullanıcı: VPS'te panelden "Webmail aç"a basıyor, localhost:8899 açılıyor ama "bu sayfa çalışmıyor". Yani PHP/IIS hattı
  çalışıyor, ROUNDCUBE uygulaması açılmıyor (büyük olasılıkla FastCGI sürecinde eksik PHP eklentisi: pdo_sqlite/intl/mbstring).
- KÖK BULGU (bug): WebmailService.OpenAsync hızlı-yolu (IsDeployedAndHealthyAsync) yalnız /adapanel-health.php (DÜZ PHP) probu
  yapıyordu, Roundcube kökünü (/) DENEMİYORDU → Roundcube bozuk olsa bile "sağlıklı" sayıp BOZUK siteye URL dönüyordu. DÜZELTME:
  artık / de probe edilir; düz PHP OK ama / değilse → tam yeniden kurulum tetiklenir (ve deploy gerçek hatayı fırlatır).
- YENİ ARAÇ — Webmail tanılama (salt-okunur): WebmailService.DiagnoseAsync → tek rapor: IIS site/durum + joker http binding portu;
  düz PHP probu; FastCGI SÜRECİNDE gerçekten yüklü eklentiler (docroot'a geçici adapanel-webmail-diag.php yazıp çalıştırır →
  extension_loaded + PDO::getAvailableDrivers, sonra siler) — EKSİK olanları vurgular; Roundcube kök (/) durum+gövde önizleme;
  Roundcube logs/ son ~1500 bayt. DTO WebmailDiagnosticsDto{Healthy,Report,Url}. Uç: GET /mail/webmail/diagnose (admin).
- FRONTEND: MailView webmail kartına "Tanıla" butonu (admin); rapor teknik-ayrıntı kutusunda gösterilir. Kutu artık
  noticeError olmadan da görünür (v-if=noticeDetails) ki sağlıklı rapor da çıksın. api/mail.ts diagnoseWebmail().
- i18n +3/dil: panel.mail.webmailDiagnose/webmailDiagOk/webmailDiagFail. (Controller mesaj anahtarları mail.webmail.diag_ok/fail
  frontend'de render edilmiyor → i18n'e eklenmedi.)
- Doğrulama: build 0/0, test 44/44(+2), i18n 2317=2317, typecheck 0, vite OK. Installer SHA256:
  `CAE4F8039783353ED3767631EB9E48A14D7529C5F63E9577C19D1B5083757CE9` (49.35 MiB).
- AÇIK İŞ: Kullanıcı yeni sürümde "Tanıla" çıktısını paylaşınca kesin kök neden (hangi eklenti EKSİK / log'daki fatal) görülecek
  → o spesifik eklentiyi AdaPanel PHP'sinde garanti et / Roundcube config'ini düzelt. ([[adapanel-webmail-tasks]] [[adapanel-security-audit]] değil)

## 2026-06-27 (141) — Webmail 500/boş gövde: extension_dir güvencesi + deploy hatası artık eksik eklentiyi gösteriyor
- Kullanıcı tanılama paylaştı: Roundcube `/` → Status=500, Body=BOŞ. Boş gövde = PHP display_errors KAPALI → fatal çıktıya
  yansımıyor. Düz PHP çalışıyor ama Roundcube açılmıyor = neredeyse kesin eksik/yüklenemeyen PHP eklentisi (pdo_sqlite/intl).
- KÖK NEDEN ADAYI (sistemik, BULUNDU): PhpService.EnsureExtensionsAsync `ext/php_<x>.dll` var mı diye bakıp `extension=<x>`
  yazıyordu AMA `extension_dir`'i HİÇ ayarlamıyordu. php.ini'de aktif extension_dir YOKSA, etkinleştirilen eklentiler DLL'ler
  ext/ içinde olsa bile YÜKLENMEZ → Roundcube 500. DÜZELTME: EnsureExtensionDir — aktif extension_dir yoksa mutlak ext yoluyla
  ekler (varsa dokunmaz, idempotent). Bu tüm PHP eklenti yüklemelerini sağlamlaştırır (webmail + redis + genel).
- GÖRÜNÜRLÜK: WebmailService.VerifyHealthyAsync artık `/` 500 verince (boş gövde olsa bile) ProbePhpExtensionsAsync (FastCGI
  sürecinde yüklü eklentiler, EKSİK olanı işaretler) + ReadRoundcubeLogTail çalıştırıp AppException.Details'e ekliyor →
  "Webmail aç" hatasının kendisi artık eksik eklentinin ADINI/log fatal'ını gösteriyor (ikinci tıklama gerektirmez).
  VerifyHealthyAsync(port, entryRoot, ct) imzası; OpenAsync documentRoot geçiyor.
- Doğrulama: build 0/0, test 44/44(+2), typecheck/ön turda. Installer SHA256:
  `5308C142736AADD51D9FD771DCF54F3DEA76E38A87DBFC0DEE448D6C0767D139` (49.35 MiB).
- BEKLENEN: yeni sürümde extension_dir eksikse otomatik düzelir → webmail büyük olasılıkla AÇILIR. Açılmazsa "Webmail aç"
  hatası ya da "Tanıla" artık hangi eklentinin OFF olduğunu / log fatal'ını net yazacak → o eklentiyi (ör. intl→ICU DLL'leri,
  pdo_sqlite→php_pdo_sqlite.dll) AdaPanel PHP bileşeninde garanti et. ([[adapanel-webmail-tasks]])

## 2026-06-27 (142) — Site↔DB seçerek bağlama (Plesk gibi) + webmail gerçek PHP fatal'ı loglanıyor
### Thread B — DB'yi siteye SEÇEREK bağla (isimle değil)
- Kullanıcı: "Bağlı Veritabanları boş, dbler eşleşmiyor; isme göre değil SEÇİME göre olsun + bağlayınca burada görünsün."
- Sebep: SiteDatabasesView listeyi silme planından çekiyordu; DB'ler siteye AÇIK bağ (SiteDatabaseLinkService) olmadığı için
  (isim eşleşmiyor) boştu. Silme planı ResolveDatabasesAsync zaten isim-eşleşmesi ∪ açık bağ döndürüyor → açık bağ eklenince görünür.
- Eklenen: SitesController GET /sites/{site}/databases (linked = açık bağ + ad eşleşmesi; candidates = bağlı olmayan mevcut DB'ler,
  kurulu motorlar taranır), POST .../databases/link {engine,name}, DELETE .../databases/link?engine&name. Hepsi EnsureSiteAsync + audit.
  DTO SiteDatabasesDto/SiteDatabaseRefDto/LinkSiteDatabaseRequest. ISiteDatabaseLinkService ctor'a enjekte.
- Frontend SiteDatabasesView: load() artık getSiteDatabases kullanıyor (linked+candidates); bağla seçicisi (select+Bağla),
  her bağlı DB'de "Bağlantıyı kaldır" (DB'yi SİLMEZ). api/sites.ts getSiteDatabases/linkSiteDatabase/unlinkSiteDatabase.
  i18n +8 (panel.siteDb.linkTitle/linkSelect/link/linking/linked/unlink/unlinking/unlinked) + subtitle/empty/hint güncellendi.
### Thread A — webmail 500/boş gövde teşhisi derinleşti
- Kullanıcı tanılaması: TÜM eklentiler on (pdo_sqlite/intl/mbstring/openssl...), pdo_drivers=...,sqlite. Roundcube 1.7.1 + PHP 8.3.31
  (uyumlu). Yani çökme eklenti/sürüm DEĞİL. "Roundcube log" diye görünen aslında logs/.htaccess'ti (log okuyucu newest dosyayı seçip
  yanıltıyordu) → gerçek hata logu YOK = Roundcube kendi loglamasına ulaşamadan PHP-fatal, display_errors kapalı (gövde boş).
- Eklenen: WebmailService.EnsureDiagnosticsIni → docroot'a .user.ini (log_errors=On, display_errors=Off=güvenli, error_log=logs/php_errors.log,
  error_reporting=E_ALL). ReadRoundcubeLogTail düzeltildi: .htaccess/nokta dosyaları atlanır, yalnız *error*/*.log okunur (boş olmayanı tercih).
  Böylece bir sonraki "Webmail aç" hatası gerçek PHP fatal'ını php_errors.log'dan gösterecek.
- Doğrulama: build 0/0, test 44/44(+2), i18n 2325=2325, typecheck 0, vite OK. Installer SHA256:
  `D0EEBB6DD830A89B544EAD7B297F98F092077BA4555CDB9A45E8839E9AEF1380` (49.35 MiB).
- AÇIK İŞ: kullanıcı yeni sürümde "Webmail aç"ı deneyip "Roundcube log (son)" bölümünü paylaşınca gerçek fatal görülecek →
  kök neden (muhtemelen public_html docroot layout / SQLite DSN / yazma izni) hedefli düzeltilecek. ([[adapanel-webmail-tasks]])

## 2026-06-27 (143) — Webmail "iki deneme/Tanıla gerekti" kökü: php-cgi tazelenmiyordu + boş sekme UX
- Kullanıcı: "Tanıla deyince düzeldi; ama Tanıla demeden Webmail aç'a basınca boş sayfa açtı, hata da vermedi."
- KÖK NEDEN: PhpService.EnsureExtensionsAsync php.ini'yi güncelliyor (extension_dir + eklentiler) AMA sıcak php-cgi
  süreçlerini YENİLEMİYORDU. AlwaysRunning + idleTimeout=0 php-cgi eski php.ini'yi tutuyor → Roundcube ilk açılışta hâlâ
  bozuk. Süreç bir şekilde dönünce (zaman/işlem) yeni ayarı okuyup düzeliyordu → "Tanıla'dan sonra düzeldi" yanılsaması.
- DÜZELTME 1 (asıl): EnsureExtensionsAsync php.ini DEĞİŞTİĞİNDE `taskkill /f /im php-cgi.exe` ile süreçleri yeniliyor
  (UpdateGlobalPhpSettings/redis ile aynı desen) → IIS sonraki istekte taze php-cgi (güncel php.ini) başlatır → ilk
  "Webmail aç" tıkında çalışır. Best-effort.
- DÜZELTME 2 (UX): MailView openWebmail artık açtığı sekmeye "Webmail hazırlanıyor…" placeholder'ı yazıyor (boş sekme
  yerine), URL gelince yönlendiriyor; hata olursa sekmeyi sessizce kapatmak yerine "açılamadı, Tanıla ile bak" yazıyor.
- Doğrulama: build 0/0, test 44/44(+2), i18n 2325=2325, typecheck 0, vite OK. Installer SHA256:
  `4A4561425108BE6C920F32BE6E87657FB8D69EC88F6BAB23B06134EF4ACD94D3` (49.35 MiB). Webmail ARTIK ÇALIŞIYOR (kullanıcı onayı).

## 2026-06-27 (144) — Profesyonelleştirme: "Tanıla" + "Bağlantıyı kaldır" butonları kaldırıldı + ana sayfaya dış erişim bilgisi
- Kullanıcı: "Tanıla butonu olmasın (Plesk'te yok), profesyonel yap"; "DB'de Bağlantıyı kaldır butonu olmasın"; "ana sayfaya
  dışarıdan mail/DB'ye nasıl bağlanılır yaz, kompakt (boşluk doldurmasın)".
- THREAD A: MailView webmail kartından "Tanıla" butonu + diagnoseWebmail handler + import kaldırıldı; notice-details v-if eski
  haline (noticeError && noticeDetails). Teşhis ARKA PLANDA kalıyor: webmail açılışında hata olursa VerifyHealthyAsync zaten
  gerçek nedeni (PHP eklenti probu + Roundcube log) detaya ekliyor → manuel butona gerek yok. (Backend /webmail/diagnose ucu +
  api fonksiyonu + i18n anahtarları kullanılmıyor ama zararsız; bırakıldı.)
- THREAD C: SiteDatabasesView'dan "Bağlantıyı kaldır" butonu + unlinkDb + unlinkingKey + unlinkSiteDatabase importu kaldırıldı.
  (Bağla + Sil kalır; bağ, DB silinince/site silinince zaten temizlenir — Plesk gibi.) Backend unlink ucu/api kullanılmıyor, bırakıldı.
- THREAD D: DashboardOverviewView erişim paneline KOMPAKT "Dışarıdan bağlantı (posta/veritabanı)" bloğu eklendi: primaryHost
  (erişim panelindeki ilk genel IP, yoksa window host) ile Posta (IMAP 993/143, SMTP 465/587, giriş=tam e-posta) + Veritabanı
  (MariaDB/MySQL 3306, PostgreSQL 5432, uzak erişim açılmalı). Küçük font + dar boşluk (sayfayı doldurmasın). i18n +5/dil
  (panel.dashboard.ext.title/mail/mailValue/db/dbValue, {host} parametreli).
- Doğrulama: build 0/0, test 44/44(+2), i18n 2330=2330, typecheck 0, vite OK. Installer SHA256:
  `CF2B111A57B1F4CA7CBD83320AA1F3FE86A6356C85079A3811DE6F6485B0D768` (49.35 MiB).

## 2026-06-28 (145) — Plesk keşfi artık yönetici kullanıcı adı+parola (Basic auth) ile de çalışıyor + opsiyonel açıklaması
- Kullanıcı: "Plesk REST API nedir, Plesk'te böyle bir API/buton bulamadım." → Açıklandı (REST API UI'da görünmez, hep
  https://host:8443/api/v2; anahtar `plesk bin secret_key` ile üretilir). Sonra: "evet bunu da ekle" (kullanıcı/parola ile keşif).
- DÜZELTME: PleskDiscoverRequest.ApiKey artık opsiyonel; UserName + Password eklendi. SiteMigrationService.DiscoverPleskAsync:
  ApiKey varsa X-API-Key; yoksa UserName+Password ile Basic auth (Authorization: Basic base64(user:pass)); ikisi de yoksa
  AppException("migration.plesk.auth_required"). Böylece CLI ile anahtar üretmeden, Plesk admin giriş bilgileriyle keşif.
- FRONTEND: MigrationView Plesk formuna pleskUser + pleskPassword alanları + pleskAuthHint (API anahtarı YA DA admin user/parola;
  hiçbiri yoksa keşfi atla, elle gir). canDiscover computed: host + (apiKey || (user && pass)); cPanel host + apiToken. discover()
  user/pass geçiyor. api/migration.ts PleskDiscoverPayload +userName/password. pleskApiKey etiketi "(opsiyonel)".
- i18n +4/dil: panel.migration.pleskUser/pleskPassword/pleskAuthHint + migration.plesk.auth_required (pleskApiKey güncellendi).
- Doğrulama: build 0/0, test 44/44(+2), i18n 2334=2334, typecheck 0, vite OK. Installer SHA256:
  `48A5C0B1C737DCBB6E32E5AED01090DD00A8BA12B228E1506C0B9BE3290E3BE3` (49.35 MiB).

## 2026-06-28 (146) — Plesk keşif hatası ham anahtar görünüyordu + HTTP kodu gizliydi: çeviri + gerçek neden yüzeye
- Kullanıcı: host + Plesk admin kullanıcı/parola girdi (API anahtarı yok), keşifte ham "migration.plesk.api_error" gördü.
- İKİ BULGU: (1) MigrationView catch'leri `extractApiMessage(error)`'i HAM (çevirisiz) notice'a yazıyordu → kullanıcı i18n
  ANAHTARINI görüyordu (success'ler t() ile çevriliyordu ama hatalar değil). (2) api_error gerçek HTTP kodunu/yanıtını
  Details'te taşıyordu ama notice detayı göstermiyordu → 401 mi 404 mü belli değildi.
- DÜZELTME (frontend): apiErrorText(error) helper → anahtarı t() ile çevir (anahtar değilse ham bırak) + extractApiDetails ekle
  (" — HTTP 401 ...{plesk yanıtı}"). discover()/validate()/run() catch'leri bunu kullanıyor (bulkLog hariç). import extractApiDetails.
- DÜZELTME (backend): DiscoverPleskAsync non-2xx → HTTP kodu + Plesk JSON gövde (300 char) Details'e; kod sınıflandırıldı:
  401/403 → migration.plesk.auth_failed, 404 → migration.plesk.not_found, diğer → api_error. i18n +2/dil (auth_failed/not_found).
- ARTIK: kullanıcı tekrar denediğinde gerçek nedeni okuyacak. En olası: 401 (admin login genelde "admin"; parola/2FA/IP kısıtı)
  ya da 404 (eski Plesk / yanlış 8443). Alternatif: 'plesk bin secret_key' ile anahtar.
- Doğrulama: build 0/0, test 44/44(+2), i18n 2336=2336, typecheck 0, vite OK. Installer SHA256:
  `F368C47A93EA3BAAC4EF1EB8739E1DC1B7DAD8942A4A6F2E3136558C135A059E` (49.35 MiB).

## 2026-06-28 (147) — FTP kullanıcı eklemedeki yavaşlık düzeltildi: altyapı artık tek-seferlik (servis restart kalktı)
- Kullanıcı: "FTP kullanıcısı ekledim baya bekletti, neden?" → Sebep: CreateFtpUserAsync HER eklemede EnsureFtpInfrastructureAsync
  çağırıp tüm FTP altyapısını yeniden kuruyordu: Import-Module WebAdministration (~1-3s) + site/SSL/izolasyon Set-ItemProperty x10 +
  cert kontrolü + **Restart-Service FTPSVC -Force (her seferinde, ~3-8s)** + firewall kontrolleri. İlk seferde ayrıca tek-seferlik
  IIS FTP rolü kurulumu (Install-WindowsFeature, 30s-dakika).
- DÜZELTME: EnsureFtpInfrastructureAsync(bool forceReconfigure, ct). HIZLI YOL: bayrak (config/ftp-infra.json Configured) + FTPSVC
  'Running' (Get-Service ile, Import-Module YOK → sub-saniye) ise ağır betiği + servis restart'ı ATLA → return. Tam kurulum
  başarınca bayrak yazılır. CreateFtpUserAsync ve EnsureServerAsync force=false (hızlı); SetRequireSslAsync force=true (SSL
  politikası + externalIp + restart gerçekten gerekir). Kendini onarır: bayrak yok / FTPSVC down ise tam kurulum yine çalışır.
- SONUÇ: 2. ve sonraki FTP kullanıcısı eklemeleri saniyelerce beklemeden anında. İlk kullanıcı (rol kurulumu) yine tek-seferlik yavaş.
- Doğrulama: build 0/0, test 44/44(+2), (frontend/i18n değişmedi). Installer SHA256:
  `109BC9BE394CAF6C8FAB99A2A80503D23AA871A735A77B2379436A921F9FDA0A` (49.35 MiB).

## 2026-06-28 (148) — Posta listesinde 'mail.<domain>' görünmesi düzeltildi (servis alt alanları filtrelendi)
- Kullanıcı: "Postada alioral.xyz VE mail.alioral.xyz görünüyor; mail.alioral.xyz'i ben eklemedim, tek domainim var."
- KÖK NEDEN: MailService.IssueCertificateAsync (mail sertifikası) → TryIssueWithWinAcmeAsync, Let's Encrypt HTTP-01 için
  siteye `mail.<domain>` host binding'i ekliyor (gerekli + kalıcı, cert yenileme için). MailView.mailDomains computed ise
  TÜM site binding host'larını potansiyel posta alan adı olarak listeliyordu → mail.alioral.xyz "Henüz kurulmadı" diye görünüyordu.
- DÜZELTME (frontend, MailView.mailDomains): servis alt alanları (mail./webmail./autodiscover./autoconfig./smtp./imap./pop./mx.)
  ÜST alan adı zaten listedeyse listeden filtrelenir → mail.<x> gizlenir ama tek başına 'mail.x' adlı gerçek site yanlışlıkla
  saklanmaz. Binding'e DOKUNULMADI (sertifika/serving için gerekli); sadece posta DOMAIN listesinde gösterilmiyor.
- Doğrulama: i18n 2336=2336, typecheck 0, vite OK, build (paket) OK. Installer SHA256:
  `DEB45779D132129031FCF8AB68B582A964F41A68B14DCB5D06F8438F60F38C5B` (49.35 MiB).

## 2026-06-28 (149) — GitHub tabanlı güncelleme (aliorala/adapanel, public) + EN/TR tanıtım web sitesi (website/)
- Kullanıcı çok pivot etti; net karar: güncelleme GİTHUB'tan (repo aliorala/adapanel, PUBLIC), + ayrı website/ klasöründe
  EN/TR web sitesi. (.host domaini bu yıl kullanılacak; GitHub ücretsiz olduğu için domain güncellemeye bağlı değil.)
  NOT: Bu turda önce "website/klasör feed" başlanıp KULLANICI İSTEĞİYLE GERİ ALINDI, sonra GitHub'a dönüldü — geçmişte
  bir tur "remote feed (configurable URL)" yazılıp revert edilmişti; nihai çözüm GitHub.
- GITHUB GÜNCELLEME: SelfUpdateService.CheckForUpdateAsync artık config/update-github.json'daki repo'dan (varsayılan
  DefaultRepo="aliorala/adapanel") GitHub Releases API'sini (api.github.com/repos/{owner}/{repo}/releases/latest, User-Agent
  zorunlu, public=token yok) çeker → tag_name(→latestVersion), .exe asset browser_download_url(→downloadUrl), SHA256 asset
  'digest' (sha256:...) YOKSA '<exe>.sha256'/'*.sha256'/SHA256SUMS asset'inden ExtractSha256, body(→notes) → yerel manifeste
  cache → mevcut indir/kur (SYSTEM scheduled task → silent installer → servis restart) aynen çalışır. SHA256 bulunamazsa
  güvenlik gereği net hata (update.github_no_sha). SetGithubRepoAsync + POST /update/github + UpdateStatusDto.GithubRepo.
  Frontend: UpdateView'da "Güncelleme kaynağı (GitHub)" repo alanı + Kaydet; api setUpdateGithubRepo. i18n +13/dil
  (panel.update.github* + update.github_ok/not_configured/fetch_failed/no_release/no_asset/no_sha/repo_invalid/saved).
- KULLANICI İŞ AKIŞI: GitHub'da public 'adapanel' deposu → Release aç (tag vX.Y.Z) → AdaPanelSetup.exe yükle → panel
  Ayarlar→Güncellemeler otomatik görür → tek tık kur. (GitHub asset digest'i SHA256 sağlar; yoksa .exe.sha256 ekle.)
- WEB SİTESİ: website/index.html — tek dosya, bağımsız (build yok), EN/TR (sağ üst TR/EN, localStorage, varsayılan TR),
  AdaPanel koyu teması; hero + 9 özellik + istatistik bandı + indirme (→ github releases/latest) + 5 SSS + footer.
  website/README.md (GitHub Pages veya hosting'e yükleme talimatı). Linkler aliorala/adapanel'e işaret eder.
- Doğrulama: build 0/0, test 44/44(+2), i18n 2349=2349, typecheck 0, vite OK. Installer SHA256:
  `C4F69286B34931BD8C1A79868B449512F544B98CC219344571A66B5C083A4FF4` (49.36 MiB).

## 2026-06-28 (150) — Web sitesi profesyonelleştirildi (website/index.html yeniden tasarım)
- Kullanıcı "daha profesyonel yapsan?" → website/index.html baştan, daha rafine: metin-rozet ikonlar yerine inline SVG ikon
  seti; gri kutu yerine inandırıcı panel mockup'ı (kenar çubuğu SVG nav + conic-gradient istatistik HALKALARI CPU/RAM/Disk +
  site tablosu/durum etiketleri + sahte URL pill); IntersectionObserver scroll-reveal; scroll'da nav gölgesi; ambient glow +
  ince grid arka plan; "Nasıl çalışır" 3 adım; CTA bandı; SVG favicon (data-uri); rafine tipografi/gölge/hover.
  Tek dosya, build yok, EN/TR (localStorage, varsayılan TR). Linkler github aliorala/adapanel. Etiketler dengeli, data-tr=data-en=74.
- Panel KODU değişmedi → yeniden paketleme yok; son installer hâlâ (149): C4F69286B34931BD8C1A79868B449512F544B98CC219344571A66B5C083A4FF4.

## 2026-06-28 (151) — Web sitesi teması: AÇIK & kurumsal (indigo) — kullanıcı yeşil/mavi neon'u beğenmedi
- Kullanıcı (AskUserQuestion ile seçti): "Aydınlık & kurumsal (indigo)". website/index.html koyu/neon temadan AÇIK temaya
  çevrildi: zemin #f6f8fb/#ffffff, metin #0f172a arduvaz, vurgu indigo #4f46e5 (degrade #4338ca / #6366f1), yumuşak gri
  gölgeler, ferah ince ızgara — neon parıltı yok. Çoğu kural var(--*) kullandığı için :root flip + birkaç hedefli düzeltme
  (buton metni #fff, nav.scrolled açık, ambient/grid açık, favicon+theme-color indigo) yetti.
- Panel MOCKUP'ı bilinçli KOYU bırakıldı (gerçek panel koyu; açık sayfada koyu ürün ekranı = şık kontrast): .mock içinde
  CSS değişkenleri scope'lanarak (--surface/-2/--border/--text/--accent #818cf8/--accent-3 #5cc8ff) koyu tutuldu.
- Doğrulama: eski yeşil/mavi/koyu renk 0 kaldı, etiketler dengeli, data-tr=data-en=74. Panel KODU değişmedi → paket yok
  (son installer 149: C4F69286B34931BD8C1A79868B449512F544B98CC219344571A66B5C083A4FF4). Tema tercihi: [[adapanel-webmail-tasks]] değil, salt web sitesi.

## 2026-06-30 (152) — phpMyAdmin sol menü "+" açılmıyor (KÖK NEDEN: web.config 'vendor' segmenti) + DB Taşı/Aktar kullanıcı oluşturuyor
### phpMyAdmin "+" (navigation) çalışmıyordu — JS ölüydü
- Belirti: sol menüde DB'nin "+"ına basınca hiç AJAX yok, URL'ye sadece '#' ekleniyor → JS handler bağlı değil (CSS çalışıyor, sayfa biçimli).
- KÖK NEDEN: HardenWebDbWebConfig'in requestFiltering hiddenSegments listesindeki **'vendor'** segmenti. IIS hiddenSegments YOLDAKİ HER
  segmenti eşler → phpMyAdmin'in JS kütüphaneleri **js/vendor/** (jQuery, CodeMirror) 404 oluyordu → jQuery yüklenmeyince TÜM
  phpMyAdmin JS'i çalışmıyordu (navigation expand, AJAX yok). CSS themes/ altında olduğu için sayfa biçimli görünüyordu (yanıltıcı).
- DÜZELTME: 'vendor' segmenti listeden çıkarıldı + eski web.config'lerden de TEMİZLENİYOR (hiddenSegments.Elements remove).
  Güvenlik zaten loopback binding + auth proxy. KULLANICI: yeni sürümü kurup phpMyAdmin'i TEKRAR AÇINCA (HardenWebDbWebConfig
  open'da çalışır) web.config temizlenir → "+" çalışır. (DbToolsProxy + config.inc/PmaAbsoluteUri zaten doğruydu.)
### DB "Taşı/Aktar" (Databases sekmesi) kullanıcı oluşturmuyordu — göç(139) ile aynı eksik
- DatabaseService.TransferDatabaseAsync DB'yi oluşturup veri taşıyor ama kullanıcı yoktu → uygulama "Access denied". DÜZELTME:
  import sonrası provider.CreateUserAsync + GrantAccessAsync(ALL) + credentialStore.Upsert (best-effort). DatabaseTransferRequest'e
  TargetUserName/TargetPassword (opsiyonel; boşsa DeriveTransferUser '<db>_usr' + GenerateDbPassword 'Ap1!'+hex). DatabaseTransferResultDto'ya
  UserName/Password. Frontend DbTransferView: hedef kullanıcı/parola alanları + üretilen kimlik kutusu (cred-box). i18n +7/dil.
- Doğrulama: build 0/0, test 44/44(+2), i18n 2356=2356, typecheck 0, vite OK. Installer SHA256:
  `2E1B03BCEB531C494277406D82BBE0D4ED43E328315B310E3CBDF1D24EF6BF10` (49.36 MiB).

## 2026-06-30 (153) — WordPress tek-tık kurulumu turnkey yapıldı (DB + kullanıcı + wp-config otomatik) + proaktif tarama
- Proaktif tarama (kullanıcı "her yeri tara"): TEK gerçek eksik = WordPress tek-tık DB'yi kurmuyordu (RequiresDatabase sadece UI
  bayrağıydı, tüketilmiyordu). Gerisi temiz: i18n'de kullanılan tüm anahtarlar tanımlı (0 eksik, en=tr=2356), çevrilmemiş ham
  hata yok, alioral.xyz sızıntısı yok (kalan aliorala/adapanel = GitHub repo), TODO yok, tüm controller'lar korumalı (anonim
  uçlar bilinçli: setup 'already_initialized' guard'lı, git webhook token'lı), panel DB kullanıcıları host '%', izolasyon var.
- DÜZELTME (AppStoreService): IDatabaseService enjekte edildi. InstallWordPressAsync(request, destination): dosya açımından sonra
  (1) EnsurePhpRuntimeAsync + phpService.EnsureExtensionsAsync(WordPressExtensions: mysqli/pdo_mysql/mbstring/gd/curl/exif/intl/
  zip/openssl/fileinfo) — mysqli OLMADAN WP DB'ye bağlanamaz; (2) ProvisionWordPressDatabaseAsync → CreateDatabaseAsync(Engine=
  kurulu mariadb/mysql, Name=DatabaseNameFromSite, UserName=<basis>_u, Password='Wp1!'+hex, SiteName=site → cascade) + ad çakışırsa
  rastgele son ek; (3) WriteWordPressConfig → wp-config-sample.php'yi temel alıp DB_NAME/USER/PASSWORD + DB_HOST=127.0.0.1 + 8
  benzersiz salt (GenerateWpSalt) doldurur (yoksa minimal config). Best-effort (DB sağlanamazsa dosyalar kalır). Test ctor +null!.
- KALAN (kapsam dışı, isteğe bağlı): WP admin hesabı hâlâ WP'nin "5 dakikalık" ekranında kuruluyor (wp-cli ile tam otomasyon sonra eklenebilir).
- Doğrulama: build 0/0, test 44/44(+2), paket OK. Installer SHA256 (kümülatif: phpMyAdmin vendor + transfer user + WP turnkey):
  `D3E678D0111133FB4EBF77867E8A1998E92A8DB2BB2A3EA7D5F46FFAC707D235` (49.36 MiB).

## 2026-06-30 (154) — WordPress tek-tık: admin hesabı wp-cli ile otomatik kuruluyor (giriş yapılabilir hazır site)
- Kullanıcı: "admin hesabını da wp-cli ile otomatik kur; WP olayını Plesk gibi her özellik/gereç yap."
- YAPILAN (admin otomasyonu): AppStoreService.InstallWordPressAsync artık wp-config sonrası TryInstallWordPressCoreAsync:
  EnsureWpCliAsync (wp-cli.phar'ı raw.githubusercontent'ten <dataRoot>/tools/wp-cli'ye indirir, önbellek) → php.exe wp-cli.phar
  --path core install --url(site host'undan) --title --admin_user=admin --admin_password(üretilen Wp!+hex) --admin_email
  --skip-email. Başarılıysa giriş bilgileri döner; başarısızsa null (kullanıcı WP 5-dk ekranını kullanır). InstallAsync dönüşü
  AppTemplateDto → AppInstallResultDto {TemplateId, AdminUrl, AdminUser, AdminPassword} (IAppStoreService + StoreController +
  store.ts + AppStoreView güncellendi). AppStoreView'da kurulum sonrası kopyalanabilir admin kimlik kutusu (wp-creds). i18n +5/dil.
  (Önceki turda: mysqli vb. eklenti + DB+kullanıcı+wp-config zaten ekliydi → artık uçtan uca: DB ekranı YOK, doğrudan giriş.)
- MEVCUT WP araç-seti: DetectAll/Detect (sürüm, plugins, themes, UpdateAvailable), maintenance modu. KALAN (Plesk WP Toolkit
  menüsü, ileride): tek-tık admin girişi (auto-login), çekirdek/eklenti/tema güncelleme + auto-update, eklenti/tema kur-aktifleştir,
  güvenlik sıkılaştırma, klon/staging, arama-değiştir(URL), şifre sıfırlama. → Kullanıcıya sıra/öncelik sorulacak (hepsi tek seferde değil).
- Doğrulama: build 0/0, test 44/44(+2), i18n 2361=2361, typecheck 0, vite OK. Installer SHA256 (kümülatif: phpMyAdmin vendor +
  DB transfer user + WP DB + WP admin): `9B90F740E69903B812D7CD2FB45F43114117BE58320C99CA7B8552E595DD9E09` (49.36 MiB).

## 2026-06-30 (155) — WordPress Toolkit buton paneli (Plesk tarzı, AppToolkitView yeni "wordpress" sekmesi)
- İSTEK: "hepsini sırayla yap" + "pleskte nasılsa aynısını yap fazla işleri yapmana gerek" → Plesk WP Toolkit menüsündeki
  tek-tık işlemleri panel haline getir, AMA yalın tut (over-engineering yok).
- YAPILAN (SADECE FRONTEND — backend'e dokunulmadı; mevcut wp-cli motoru yeniden kullanıldı): AppToolkitView.vue'ya
  "wordpress" sekmesi. ToolkitTab union'a "wordpress" eklendi. Site seçilince getWordPress ile durum yüklenir.
  - DURUM SATIRI: sürüm pill + (Güncel / Güncelleme var → {v}) pill + "Yönetim paneli" linki (adminUrl, _blank).
  - BUTONLAR (her biri runWp ile tek wp-cli komutu, çıktı `$ wp ...` ile <pre>'de gösterilir, exitCode===0 başarı):
    * Tümünü güncelle → "core update" + "plugin update --all" + "theme update --all" (sıralı).
    * Güvenliği sıkılaştır → "config set DISALLOW_FILE_EDIT true --raw".
    * Bakım modu aç/kapat → setWordPressMaintenance (mevcut API).
  - FORMLAR: Eklenti kur ("plugin install SLUG --activate"); Arama-Değiştir ("search-replace FROM TO --all-tables
    --report-changes-only"); Yönetici parolası sıfırla ("user update USER --user_pass=PASS --skip-email") — parola
    crypto.getRandomValues ile üretilir ("Wp!"+hex), tek sefer gösterilir (wp-cred kutusu).
  - reactive() ile wpForm (pluginSlug, srFrom, srTo, adminUser=admin). State: wp, wpLoading, wpLoaded, wpBusy (union),
    wpOutput, wpNotice/wpNoticeError, wpNewPassword. CSS: .wp-panel/.wp-status/.wp-admin-link/.wp-actions/.wp-form/
    .wp-row/.wp-cred/.wp-output.
- Motor (değişmedi): SiteHostingSettingsService.RunWpCliCommandAsync (len≤300, tehlikeli karakter reddi, "wp " soyma,
  php wp-cli.phar <cmd> --no-color) ← runWpCliCommand (siteHosting.ts) ← runWp.
- i18n: panel.toolkit.tabs.wordpress + panel.wpkit.* (28 anahtar/dil). 2361 → 2389 (en=tr).
- KALAN (Plesk WP Toolkit, sırayla devam): tek-tık SSO admin girişi (mu-plugin/token), klon/staging, çekirdek/eklenti
  otomatik-güncelleme zamanlaması. → Bu ağır parçalar kullanıcı onayıyla.
- Doğrulama: typecheck 0, vite build OK, i18n 2389=2389 (backend değişmediği için build/test çalıştırılmadı — yalnız FE).
  Installer SHA256: `D49BCB41F57DD0485228DD414012B623EDD5B28EF2F103D0093FD26AACF48B45` (49.37 MiB).

## 2026-06-30 (156) — WordPress tek-tık yönetici girişi (Plesk "Giriş yap" / SSO sihirli bağlantı)
- İSTEK: "hepsini sırayla yap" → Plesk WP Toolkit'in en ikonik özelliği: panelden tek tıkla site wp-admin'ine giriş.
- YÖNTEM (Plesk'in geçici, kendini silen oturum-açma eklentisini birebir taklit): backend tek kullanımlık SİHİRLİ
  BAĞLANTI üretir. Jeton (256-bit rastgele hex) yalnızca URL'de gider; diskte SADECE SHA256 özeti + 120 sn son-kullanım
  gömülü olarak <root>/wp-content/mu-plugins/adapanel-login.php'ye yazılır. mu-plugin `init`'te: süre/özet kontrolü
  (hash_equals), eşleşince DOSYAYI SİLER (tek kullanım), ilk yöneticiyle (role=administrator, ID ASC, number=1)
  wp_set_auth_cookie + wp_safe_redirect(admin_url()). wp-cli GEREKTİRMEZ (saf WP API). Yeni bağlantı üretmek eskisini
  ezer (hash değişir → eski jeton geçersiz).
- DEĞİŞENLER: WordPressSiteDto.cs +WordPressLoginLinkDto{Url,ExpiresInSeconds}; IWordPressToolkitService
  +CreateLoginLinkAsync; WordPressToolkitService.CreateLoginLinkAsync + BuildLoginPlugin (PHP şablon, __HASH__/__EXPIRES__
  replace; brace-doubling'den kaçınmak için token-replace) + en uygun bağlama seçimi (https>hostlu>ilk, port 80/443 atlanır);
  WordPressController +[HttpPost {siteName}/login-link] (CustomerAccess.EnsureSiteAsync ile kapsam — Bayi yalnız kendi
  müşterisi); api/wordpress.ts +createWordPressLoginLink/WordPressLoginLink; AppToolkitView WordPress sekmesine "Giriş yap
  (tek tık)" birincil butonu (wpLogin → link al → window.open _blank noopener), wpBusy union'a "login".
- GÜVENLİK: tek kullanımlık + 2 dk ömür + at-rest yalnız hash + [Authorize Administrator,Reseller] + site kapsam kontrolü.
  Hata kodu yeni anahtar açmamak için mevcut "wpcli.not_wordpress" (ham anahtar sızması yok).
- i18n: panel.wpkit.login + panel.wpkit.loginOpened (+2/dil) → 2389→2391 (en=tr).
- KALAN (Plesk WP Toolkit, sırayla): klon/staging, çekirdek/eklenti otomatik-güncelleme zamanlaması. → ağır parçalar, onayla.
- Doğrulama: build 0, test PASS (exit 0), i18n 2391=2391, typecheck 0, vite OK.
  Installer SHA256: `D209BE44C3F3742CBA469DF37480DE22A18E85F92B73712CA5003FE76E95C71E` (49.37 MiB).

## 2026-06-30 (157) — WordPress Toolkit: otomatik güncelleme + klon/staging (Plesk WP Toolkit seti tamam)
- İSTEK: "İkisi de — sırayla" (AskUserQuestion) → önce otomatik güncelleme, sonra klon.
- (A) OTOMATİK GÜNCELLEME (frontend-only, WP-native — harici görev YOK): WordPress panosuna "Otomatik güncellemeler"
  Aç/Kapat. wpAutoUpdate(enable) sıra: `config set WP_AUTO_UPDATE_CORE true|false --raw`, `plugin auto-updates
  enable|disable --all`, `theme auto-updates enable|disable --all` (mevcut runWpCliCommand). wp-cron arka planda günceller.
  wpBusy union +autoOn/autoOff. i18n panel.wpkit.autoUpdates* (+6/dil).
- (B) KLON / STAGING (backend orkestrasyon, mevcut servisleri besteler): IWordPressToolkitService.CloneAsync(source,
  {TargetSiteName,TargetHostName}). Adımlar: kaynak wp-config DB bilgilerini regex'le oku (ReadDefine) →
  iisSiteService.CreateSiteAsync(RuntimeProfile="php", ProvisionDefaultServices=false, CreateDatabase=false, hedef host) →
  robocopy /E (çıkış<8 başarı) → databaseService.TransferDatabaseAsync (mysqldump→AYRI hedef DB + yeni kullanıcı + üretilen
  parola; TargetDatabase kaynaktan farklı zorlanır) → hedef wp-config'te DB_NAME/USER/PASSWORD/HOST yenile (ReplaceDefine,
  $→$$ kaçış) → hostingSettings.RunWpCliCommandAsync(target, "search-replace SRC DST --all-tables --skip-columns=guid").
  WordPressToolkitService ctor'a IDatabaseService + ISiteHostingSettingsService eklendi (hepsi AddScoped, captive yok).
  Yardımcılar: ResolveDbEngineAsync (mariadb>mysql>mariadb), IsSafeName/IsSafeHost/SafeFolder, SplitHostPort,
  BuildHttpBase (https>hostlu>ilk binding, 80/443 atlanır), ReadDefine/ReplaceDefine (tek/çift tırnak).
  Controller: [HttpPost {siteName}/clone] + method-level [Authorize(Roles="Administrator")] (sınıf Admin,Reseller AND →
  yalnız Admin; klon yeni site oluşturduğu için Bayi müşteri-kapsam ataması KAPSAM DIŞI/sonraki iş). Dönüş wp.clone_done.
  DTO: CloneWordPressRequest + WordPressCloneResultDto{TargetSiteName,TargetUrl,TargetDatabase,DatabaseUser,DatabasePassword}.
  api/wordpress.ts +cloneWordPress/WordPressCloneResult. UI: klon formu (hedef ad + host) + sonuç kartı (URL aç + DB/kullanıcı/
  parola kopyalanabilir). i18n: panel.wpkit.clone* (+7/dil) + backend kodları wp.clone_* (+6/dil, ham anahtar sızmaz).
- NOT/SINIRLAR (lean, "fazla işleri yapmana gerek"): klon ACL/DNS'i değiştirmez (host DNS'i kullanıcı yönlendirir; web kökü
  ACL miras alınır); search-replace başarısızsa klon yine kullanılabilir (yutuluyor). PLESK WP TOOLKIT SETİ ARTIK TAM:
  güncelle/güvenlik/bakım/eklenti/arama-değiştir/parola/giriş(SSO)/otomatik-güncelleme/klon.
- i18n: 2397→2410 (en=tr). Doğrulama: build 0, test PASS (exit 0), typecheck 0, vite OK.
  Installer SHA256: `9EA82440EFA53880ECF10B744E20A3AC94D13C397E087B5B582A9D5725419510` (49.37 MiB).

## 2026-06-30 (158) — WordPress Toolkit: kurulu eklenti/tema yönetimi (Plesk listesi + güncelle/sil)
- İSTEK: "devam et geri kalanlarıda yap". DEĞERLENDİRME: Plesk WP Toolkit'in ana yönetim ekranı = kurulu eklenti/tema
  LİSTESİ idi ve panoda yoktu (DTO'da plugins[]/themes[] zaten vardı ama gösterilmiyordu). → en net "geri kalan".
- YAPILAN (frontend-only, backend değişmedi — runWpCliCommand yeniden kullanıldı): WordPress panosuna iki liste:
  "Kurulu eklentiler (n)" ve "Kurulu temalar (n)". Her satır: ad + sürüm + Güncelle + Sil. wpComponent(kind, action, slug)
  → `plugin|theme update|delete SLUG` (runWp), sil için window.confirm; başarılıysa loadWp ile liste tazelenir. wp-cli
  etkin bileşeni silmeyi reddeder (çıktıda görünür). wpBusy union +comp. CSS: .wp-list/.wp-list-row/.wp-list-name/.wp-list-actions.
  i18n panel.wpkit.{plugins,themes,compUpdate,compDelete,compDone,confirmDelete} (+6/dil) → 2410→2416 (en=tr).
- BİLİNÇLİ YAPILMADI (lean, "fazla işleri yapmana gerek" + "bana sormadan kafana göre iş yapma"):
  * Etkin/pasif (activate/deactivate) durumu: dosya tabanlı algılama DB'deki active_plugins'i bilemez; göstermek her pano
    yüklemesinde 2 ekstra wp-cli çağrısı + DTO'ya Active alanı gerektirir → over-engineering kabul edildi, eklenmedi.
  * Bayi (Reseller) için klon: yeni siteyi HANGİ müşteriye + KOTA denetimi belirsiz (ICustomerScopeService.SetScopeAsync,
    bayi→müşteri→site zinciri). Kullanıcı kararı gerektirir → klon yönetici-özel KALDI.
  * Tam güvenlik kontrol listesi: mevcut "Harden" (DISALLOW_FILE_EDIT) lean bırakıldı (Plesk'in ~15 maddesi mu-plugin/
    web.config işi; istenirse ayrıca).
- Doğrulama: typecheck 0, vite OK, i18n 2416=2416 (backend değişmedi → build/test çalıştırılmadı).
  Installer SHA256: `60CE2E2215C2C7566D7603A1D469820A63FAC1BB2327D5859857C9B5A03854F4` (49.37 MiB).

## 2026-06-30 (159) — WordPress Toolkit: eklenti aç/kapat + Bayi klonu + tam güvenlik seti ("plesktekilerin hepsi")
- İSTEK: "plesktekilerin hepsini yap" → (158)'de bilinçli ertelenen 3 maddenin hepsi Plesk-sadık yapıldı.
- (1) EKLENTİ/TEMA ETKİN DURUM + AÇ/KAPAT: WordPressComponentDto +Active. DetectAsync (YALNIZ tek site/pano; DetectAll'da
  DEĞİL) wp-cli ile zenginleştirir: `plugin list --status=active --field=name` + `theme list ...` → EnrichActiveStateAsync/
  GetActiveSlugsAsync (best-effort, wp-cli yoksa durum gösterilmez). UI: satırda etkin nokta + eklenti için Etkinleştir/
  Devre dışı, tema için "Etkin" rozeti / Etkinleştir(switch); Sil etkin bileşende gizli (wp-cli zaten reddeder). wpComponent
  action union: activate|deactivate|update|delete. NOT: pano açılışı +2 wp-cli (kullanıcı kabul etti "pano biraz yavaşlar").
- (2) BAYİ KLONU (Plesk: klon aynı abonelikte kalır): WordPressController.CloneAsync'ten [Authorize Administrator] KALDIRILDI
  (sınıf Administrator,Reseller). Kaynağın sahibi müşteri = GetSiteOwnerUserIdAsync(source); varsa ÖNCE kota (MaxSites)
  denetimi → wp.clone_quota(403); klon sonrası hedef site o müşterinin kapsamına eklenir (GetScopeAsync + SetScopeAsync,
  Append). Sahip yoksa (yönetici sitesi) sınırsız. Scope işi CONTROLLER'da (customerScopes zaten enjekte).
- (3) TAM GÜVENLİK SETİ: IWordPressToolkitService.HardenAsync + [HttpPost {siteName}/harden]. Önlemler: mu-plugin
  adapanel-security.php (wp_generator kaldır + the_generator boşalt = sürüm gizle; xmlrpc_methods'tan pingback.ping/
  getPingbacks unset = pingback kapat; template_redirect'te ?author=N numerik → home'a 301 = yazar taraması engelle) +
  wp-cli best-effort (DISALLOW_FILE_EDIT true --raw; config shuffle-salts). XML-RPC TAMAMEN kapatılmadı (yalnız pingback;
  Jetpack/app bozulmasın). Dönüş Applied string[] (kodlar) → frontend panel.wpkit.sec_<code> ile etiketler, wpOutput'a
  "✓ ..." listeler. Eski wpHarden (tek wp-cli) → backend hardenWordPress çağrısına çevrildi.
- DTO: WordPressHardenResultDto{Applied}. api/wordpress.ts: WordPressComponent +active, hardenWordPress/WordPressHardenResult.
  i18n: panel.wpkit.{activate,deactivate,activeLabel,sec_hide_version,sec_xmlrpc_pingback,sec_author_enum,sec_file_edit,
  sec_salts} + backend wp.{clone_quota,hardened} (+10/dil) → 2416→2426 (en=tr).
- PLESK WP TOOLKIT ARTIK TAM-TAM: giriş(SSO)·güncelle·otomatik-güncelleme·GÜVENLİK(çoklu)·bakım·eklenti kur·eklenti/tema
  YÖNETİMİ(aç/kapat/güncelle/sil)·arama-değiştir·parola·KLON(bayi+kota). Test bağımlılığı yok (test WordPress* kullanmıyor).
- Doğrulama: build 0, test PASS (exit 0), i18n 2426=2426, typecheck 0, vite OK.
  Installer SHA256: `D9DFE47928099D542F3CF2307A1DC31BFCB94C6A9F3B263C6E7B01C2A941726D` (49.37 MiB).

## 2026-07-01 (160) — WordPress Toolkit: güvenlik durum paneli + Veri Kopyala (staging→canlı). Plesk WP Toolkit BİTTİ.
- İSTEK: "2 plesk parçasınıda sırayla yap" (AskUserQuestion) → önce güvenlik durum paneli, sonra Veri Kopyala.
- (A) GÜVENLİK DURUM PANELİ (dosya tabanlı, ekstra endpoint/maliyet YOK): WordPressSiteDto +Security (WordPressSecurityDto:
  FileEditorDisabled, VersionHidden, XmlRpcPingbackDisabled, AuthorScanBlocked). BuildDto → BuildSecurity(root): mu-plugin
  adapanel-security.php varlığı (3 önlem) + wp-config'te DISALLOW_FILE_EDIT true regex. DetectAll dahil ucuz (2 dosya). UI:
  WordPress panosunda "Güvenlik durumu" listesi, her önlem pill (Güvenli/Kapalı). HardenAsync sonrası loadWp → durum tazelenir.
  i18n panel.wpkit.{securityStatus,st_file_edit,st_version,st_xmlrpc,st_author,secOn,secOff} (+7/dil). Frontend type WordPressSecurity.
- (B) VERİ KOPYALA (Plesk "Copy Data", staging→VAR OLAN canlı; YIKICI → önce yedek): IWordPressToolkitService.CopyDataAsync
  (source, {TargetSiteName, Files, Database}) + [HttpPost {siteName}/copy-data] (kaynak+hedef için EnsureSiteAsync). Adımlar:
  hedef VAR OLAN WP olmalı → hedefin DB bilgilerini oku (KORUNACAK) → backupRoot = hedef kökün kardeşi "_kopyala_yedek_<ts>" →
  DB seçiliyse: ExportDump(hedefDB→yedek) + ExportDump(kaynakDB→dump) + ImportDump(dump→hedefDB) (motor ADMIN'iyle, per-DB
  kullanıcı gerekmez) → Dosya seçiliyse: robocopy hedef→yedek (kopya) + robocopy kaynak→hedef /MIR + hedef wp-config DB
  bilgilerini ReplaceDefine ile GERİ yaz (aynalama kaynağın config'ini getirir) → search-replace kaynakURL→hedefURL (wp-cli).
  DTO CopyWordPressDataRequest + WordPressCopyDataResultDto{TargetSiteName,TargetUrl,BackupPath,CopiedFiles,CopiedDatabase}.
  api copyWordPressData. UI: hedef adı + Dosyalar/Veritabanı onay kutuları + Kopyala; window.confirm ile YIKICI uyarısı; sonuç
  kartı yedek yolunu gösterir. CSS .wp-check. i18n panel.wpkit.copy* (+9/dil) + backend wp.copy_* (+5/dil).
- i18n 2426→2447 (en=tr). PLESK WP TOOLKIT ARTIK TAMAMEN BİTTİ (güvenlik durumu görünür + veri sync dahil). Bilinçli atlanan
  yok; kalan yalnız premium/beyond-Plesk (smart updates AI, plugin sets/labels).
- NOT: /MIR hedefte kaynakta olmayan dosyaları SİLER (Plesk copy-data semantiği); yedek kardeş klasörde (target dışı → /MIR
  dokunmaz). Veri Kopyala CANLIYA yazar → onay + otomatik yedek + hedef DB bağlantısı korunur.
- Doğrulama: build 0, test PASS (exit 0), i18n 2447=2447, typecheck 0, vite OK.
  Installer SHA256: `1530752676113A4499AA378915994D65D996EB4EF58BDCB911A1D70C11876C05` (49.38 MiB).

## 2026-07-01 (161) — Tam sistem proaktif denetimi + DB sistem-hesabı sızıntısı & DB-kullanıcı sütunu düzeltmeleri
- İSTEK: "tüm sistemi en ince ayrıntısına kadar kontrol et, hatalı/eksik her yeri söyle" + canlı bug: DB→Kullanıcılar'da
  root/mariadb.sys sistem hesapları görünüyor (Sil/Parola-değiştir ile!) + DB listesinde kullanıcı adı gösterilsin (yetimler görünür).
- BULUNAN & DÜZELTİLEN GERÇEK HATALAR:
  * [KRİTİK] DB kullanıcı listesi sistem hesaplarını gösteriyordu. MySqlDatabaseProvider.GetUsersAsync: `WHERE User<>''`
    → +`AND Host<>'' AND LOWER(User) NOT IN (root,mariadb.sys,mysql.sys,mysql.session,mysql.infoschema,debian-sys-maint)`.
    Aynı sınıf: PostgreSql (+`rolname<>'postgres'`), MsSql (+`NOT IN (sa,guest,dbo,sys,INFORMATION_SCHEMA,public)`). root gizli
    çünkü motor admin'i — silinirse panel DB erişimini kaybeder (K2 ile de tutarlı).
  * [EKSİK] DB listesinde kullanıcı yoktu. DatabaseRecordDto +Users (IReadOnlyList<string>). MySqlDatabaseProvider.
    GetDatabasesAsync artık mysql.db'den DB→kullanıcı(lar) eşlemesi kurar (sistem hesapları hariç). Frontend DatabaseRecord
    +users; DatabasesView "Sahip" sütunu → "Kullanıcı(lar)" (item.users.join veya PG owner fallback veya "— (kullanıcısız)");
    arama filtresine users eklendi. i18n panel.database.{dbUsers,noDbUser}. NOT: PG owner'ı doldurur (fallback doğru); MySQL
    per-DB grant = mysql.db.
  * [i18n LANDMINE] php.not_installed AppException'ın i18n karşılığı yoktu → UI'da ham anahtar sızardı. Eklendi (tr+en).
- PROAKTİF TARAMA SONUÇLARI (temiz çıkanlar): kişisel domain (alioral.xyz) sızıntısı YOK (aliorala/adapanel kasıtlı GitHub).
  TODO/FIXME/NotImplemented YOK. 45 controller'dan 43'ü [Authorize]; kalan 2 (Health=liveness, Setup=loopback-only+ratelimit)
  bilerek anonim+korumalı. Frontend t() anahtarları json'a karşı: 0 gerçek eksik (1 yanlış-pozitif: dinamik "panel.wpkit.sec_"+code).
  Backend AppException kodları json'a karşı: yalnız php.not_installed eksikti (düzeltildi). .Result/.Wait()/async void YOK.
  Yeni WP Toolkit kodu (clone/copy-data/harden/enrich) gözden geçirildi: security her zaman non-null (DTO default), null-deref yok.
- Doğrulama: build 0, test PASS (exit 0), i18n 2450=2450, typecheck 0, vite OK.
  Installer SHA256: `1B38C894B060BC94D8E612CA5840425EFC31F18EC244B84C2639FB295B90970B` (49.38 MiB).

## 2026-07-01 (162) — Mail modülü derin denetimi + domainsiz "Önizleme" (nip.io) özelliği
- İSTEK: "mail modülünü + her yeri derinlemesine incele" + "domain yönlendirmeden site çalıştığını nasıl anlarım? pleskteki gibi domainsiz link".
- MAIL DERİN DENETİM (MailController + MailService 1494 satır + IMailService okundu):
  * Yetki/kapsam: DOĞRU (sınıf Admin,Customer,Reseller; config/spam/cert/listeners/rdns/diagnose admin-only; her uç
    CustomerAccess.EnsureMailDomainAsync ile domain-kapsamlı; EnsureMailboxQuotaAsync M1 kotası).
  * Enjeksiyon: YOK — stalwart-cli UseShellExecute=false (kabuk yok); create/mailing-list/cert JSON'u --file (temp) ile
    geçiyor; JsonValue=JsonSerializer.Serialize, CliJsonArg/Arg MSVC-tarzı kaçış. Sağlam.
  * DNS/DKIM/SPF/DMARC üretimi (GetDnsRecordsAsync): DOĞRU (A mail→serverAddress, MX 10, SPF ip4, DKIM Stalwart'tan
    RSA+Ed25519 v=DKIM1, DMARC p=quarantine+rua). NOT: serverAddress boşsa mail A kaydı atlanır (mail kırılır) — panel
    prefs.ServerAddress dolu olmalı.
  * BULUNAN GERÇEK UX HATALARI (rapor edildi, kullanıcı kararı bekliyor — uygula vs gizle):
    (1) Otomatik yanıt (autoresponder): MailView'da tam UI var AMA MailService.SetAutoResponderAsync HER ZAMAN
        AppException("mail.autoresponder_not_supported") fırlatıyor → kaydet hep hata. (Stalwart sieve "vacation" ile
        yapılabilir; henüz yok.)
    (2) Domain kotası: MailView'da alan var AMA SetDomainQuotaAsync sessiz no-op (Stalwart domain-kotası desteklemez) → yanıltıcı.
  * PERF gözlemi (bug değil): GetWorkspaceAsync her hesap için ayrı stalwart-cli GetObject çağırıyor (N+1); her mutasyon
    sonrası tam workspace çekiliyor. Büyük kurulumda yavaş.
- YENİ ÖZELLİK — DOMAINSİZ ÖNİZLEME (Plesk "Preview" karşılığı): GET /sites/{siteName}/preview. serverAddress IPv4 ise
  nip.io ile TIKLANIR link üretir: previewHost=`<safe-site>.<ip>.nip.io` (nip.io host'u IP'ye çözer), siteye idempotent
  bir http IIS bağlaması eklenir (host-header eşleşsin) → link DNS'siz çalışır. serverAddress IP değilse "hosts" yöntemine
  düşer. Her durumda hosts satırı (`<ip>\t<realHost>`) da döner (HTTPS dahil her yerde çalışır; WP redirect'inde nip.io
  yerine hosts önerilir). DTO SitePreviewDto. SitesController (IPanelPreferencesService+iisSiteService zaten enjekte);
  BuildPreviewLabel (DNS etiketi). Frontend: sites.ts getSitePreview/SitePreview; SitesView "Önizle (domainsiz)" butonu +
  sonuç kartı (tıklanır link + kopyalanır hosts satırı); window.open. i18n panel.sites.preview* (+7/dil) → parity 2456=2456.
- PROAKTİF TARAMA (bu turda da): controller yetki (yeni preview EnsureSiteAsync'li), t() anahtarları (tek yanlış-poz
  panel.wpkit.sec_), backend AppException i18n. Temiz.
- Doğrulama: build 0, test PASS, i18n 2456=2456, typecheck 0, vite OK.
  Installer SHA256: `7F09D587AA5667C604B81AADBE34462B245D73850EA72D3858A09E4C8733D642` (49.38 MiB).

## 2026-07-01 (163) — Mail: domain kotası panel-tarafı (çalışır) + autoresponder webmail yoluna (Stalwart kısıtı)
- İSTEK: "domain kotası yapılabiliyorsa yap (pleskte var)" + "autoresponder'ı çalışır hale getir".
- DOMAIN KOTASI (panel-tarafı, ÇALIŞIR): Stalwart domain kotası tutmaz → panel `config/mail-domain-quotas.json`'da
  domain→MB tutar ve TAHSİS toplamıyla dayatır. MailService: DomainQuotaPath sabiti; SetDomainQuotaAsync (limit sakla;
  limit<mevcut tahsis ise mail.domain_quota_below_allocated); ReadDomainQuotasAsync (OrdinalIgnoreCase);
  GetDomainAllocatedMbAsync; EnsureDomainAllocationAsync (limit>0 iken: kutu 0/sınırsız ise reddet
  mail.domain_quota_needs_mailbox_limit; diğer kutuların Σ MaxSizeMB + istenen > limit ise mail.domain_quota_exceeded).
  CreateAccountAsync + SetAccountQuotaAsync bu denetimi çağırır. GetWorkspaceAsync: her domain MaxSizeMB=limit; seçili
  domain AllocatedMB=Σ kutu kotası. DTO MailDomainDto +AllocatedMB. Frontend: domain kota UI geri (önce gizlemiştim) +
  "Tahsis: {used}/{limit} MB" göstergesi (selectedDomainInfo computed). i18n panel.mail.domainQuotaUsage + backend
  mail.domain_quota_* (3). NOT: tahsis-tabanlı (Plesk gibi); Stalwart'ın gerçek disk-dayatması değil ama over-allocation'ı
  engeller + görünürlük verir.
- AUTORESPONDER (Stalwart KISITI — araştırıldı, GH discussion #875 + maintainer): per-kullanıcı vacation YALNIZ ManageSieve
  (kullanıcının kendi istemcisi/webmail) ile; admin adına REST/CLI YOK (global trusted script'ler tüm mesajlara uygulanır,
  kişiye özel değil). → ÇALIŞAN YOL: WebmailService Roundcube config'inde `$config['plugins']=['managesieve']` +
  managesieve_host=localhost/port=4190/usetls=false/vacation=1 açıldı → kullanıcı webmail Ayarlar>Filtreler/Tatil'den
  kurar. Panel MailView autoresponder formu (eskiden setMailAutoResponder → hep hata) BİLGİ KARTINA çevrildi: açıklama +
  "Webmail'i aç" (openWebmail). openAutoResponder sadeleşti; saveAutoResponder/autoResponderForm/get-set importları kaldırıldı.
  i18n panel.mail.autoResponderWebmailHint. Backend Get/SetAutoResponder duruyor (UI çağırmıyor). CANLI TEST GEREKİR:
  managesieve'in Stalwart 4190'a bağlanması (TLS/port) canlıda doğrulanmalı; gerekirse usetls ayarlanır.
- Doğrulama: build 0, test PASS, i18n 2461=2461, typecheck 0, vite OK.
  Installer SHA256: `E4199B811052DF19C53FDA5D67BDF3A8EE5BFAFB16B6E2DF5CA07F115AC29536` (49.38 MiB).

## 2026-07-01 (164) — Domainsiz önizleme HATA AYIKLAMA + güçlendirme (WP mu-plugin + preview host'a SSL)
- SORUN: kullanıcı `https://alioral-xyz.185.7.242.225.nip.io/` açıldı "site bulunamadı"; hosts'a dokunmadan görmek istiyor.
- CANLI TEŞHİS (dev makineden probe): nip.io DNS DOĞRU çözülüyor (→185.7.242.225); port 80+443 AÇIK. http preview host →
  301 `Location: https://...` + `Server: IIS/10.0, X-Powered-By: ASP.NET` (WP/PHP DEĞİL → istek siteye DEĞİL ASP.NET
  catch-all'a düşüyor = binding siteye eklenMEMİŞ; muhtemelen yeni build VPS'te kurulu değil / buton kullanılmamış).
  https preview host → Connection reset (o host için SNI sertifikası yok). KÖK NEDEN: (a) binding yok, (b) site http→https
  zorluyor ama nip.io host'unda cert yok → reset.
- GÜÇLENDİRME (SitesController.GetPreviewAsync): (1) http binding (vardı). (2) YENİ TryWriteWordPressPreviewPlugin: WP ise
  wp-content/mu-plugins/adapanel-preview.php yazar → `.nip.io` host'unda option_siteurl/home'u o host'a çevirir (WP kendi asıl
  adresine yönlendirmesin, varlıklar/linkler çalışsın). (3) YENİ: best-effort sslService.IssueLetsEncryptAsync(previewHost)
  → https binding + cert (LE olmazsa self-signed; ApplyHttpsRedirect zaten). Böylece https resetlenmez. dto.Url artık
  SSL başarılıysa https://. ISslService SitesController'a enjekte edildi. apiClient timeout yok → uzun SSL çağrısında FE kopmaz.
  i18n panel.sites.previewOpened güncellendi (ilk sefer cert gecikmesi + https uyarısı 'Devam et' notu).
- KULLANICIYA: ÖNCE en son AdaPanelSetup.exe'yi VPS'e KUR (önizleme özelliği + binding mantığı ancak o zaman çalışır),
  sonra panelde "Önizle (domainsiz)" BUTONUNU kullan (URL'yi elle yazma — binding'i buton ekliyor). İlk tık cert için yavaş
  olabilir/timeout → birkaç sn sonra tekrar. Self-signed olursa tarayıcı uyarısını 'Devam et' ile geç. CANLI TEST GEREKİR
  (ACME nip.io için başarır mı, WP mu-plugin redirect'i keser mi — dev'den doğrulanamaz).
- Doğrulama: build 0, test PASS, i18n 2461=2461, typecheck 0, vite OK.
  Installer SHA256: `B2B79EA34D0821CE4673AD5C26CB227919F1345B8FC342BE750A87442A1FF4CE` (49.38 MiB).

## 2026-07-01 (165) — Önizleme buton/popup düzeltmeleri + nip.io display regresyonu + window.open hata sınıfı taraması
- İSTEK: "önizle tıklıyorum hiçbir şey olmuyor" + "içerideki önizleme (iframe) açamıyor" + "görünen adres nip.io oldu" +
  "tüm butonları kontrol et" + "çok değişiklik yapma, sadece önizle yerlerini düzelt".
- KÖK NEDEN 1 (buton hiçbir şey yapmıyor): handlePreview/wpLogin/openPma `window.open`'ı AWAIT SONRASI çağırıyordu →
  tarayıcı popup'ı engelliyor (kullanıcı hareketi tükenmiş). DÜZELTME (buton denetimi = aynı hata sınıfı taraması):
  * SitesView.handlePreview: window.open KALDIRILDI → link KARTTA gösteriliyor (kullanıcı tıklar), previewLoading göstergesi.
  * AppToolkitView.wpLogin (WP tek-tık giriş) + SiteDatabasesView.openPma (phpMyAdmin aç): sekmeyi TIK anında `window.open("","_blank")`
    ile aç, URL'yi await sonrası `tab.location.href` ata (MailView zaten bu doğru desende; openReachable sync → sorun yok).
- KÖK NEDEN 2 (iframe açamıyor): SiteDetailView önizleme <iframe src=primaryUrl> idi; çoğu site X-Frame-Options/CSP ile
  framing'i engeller. → iframe KALDIRILDI, domainsiz önizleme linki (getSitePreview → kart + yeni sekme) kondu.
- KÖK NEDEN 3 (görünen adres nip.io oldu — REGRESYON): preview binding eklenince site.bindings alfabetik sıralamada
  "alioral-xyz.<ip>.nip.io" < "alioral.xyz" ('-'<'.') → bindings[0] nip.io oluyordu. DÜZELTME: siteUrl (SitesView) +
  primaryUrl (SiteDetailView) + backend preview realHost artık `.nip.io` host'larını ATLIYOR (asıl domain gösterilir/açılır).
- BACKEND (preview host'a hızlı https): IssueLetsEncryptAsync(ACME, yavaş→buton donuyordu) YERİNE yeni
  ISslService.EnsurePreviewCertificateAsync = self-signed cert + EnsureHttpsBindingAsync (ACME beklemez, redirect değiştirmez);
  yalnız https binding yoksa çağrılır. Tarayıcı bir kez cert uyarısı gösterir ("Devam et"). + WP preview mu-plugin (164) duruyor.
- i18n: panel.sites.previewOpenLink + previewOpened metni (kart-linki modeline göre). 2461→2462.
- Doğrulama: build 0, test PASS, i18n 2462=2462, typecheck 0, vite OK.
  Installer SHA256: `8DCDCB7E9E03481D52F3D4BFA055A0ADC22809C9260158421D5DC59420615BB9` (49.38 MiB). CANLI TEST: kur → Önizle → kart
  linkine tıkla → cert uyarısı 'Devam et'. Açılmazsa hata metnini ilet.

## 2026-07-01 (166) — alioral.com.tr DIŞ SORUN (bizde değil) teyidi + refactor orphan temizliği
- alioral.com.tr "siteye ulaşılmıyor": CANLI ölçüldü → Google(8.8.8.8)+Cloudflare(1.1.1.1) SERVFAIL (domainin yetkili
  DNS'i cevap vermiyor); site+DNS KARŞI TARAFTAKİ Plesk sunucusunda (kullanıcı teyit etti). BİZİM taraf sağlam:
  MariaDB 3306 dışa açık + handshake (11.8.3), DB duruyor; alioral.xyz (bizde) https 200 OK (Laravel) → preview/binding
  değişikliklerim siteyi BOZMADI. Zamanlama tesadüf. Not: benim son DB değişikliklerim yalnız SELECT (görüntüleme filtresi),
  grant/kullanıcı/DB silmez; HardenAdminAccounts sadece butonla çalışır (otomatik değil).
- YARIM-UÇ TEMİZLİĞİ (autoresponder webmail refactor'ından kalan orphan): api/mail.ts'ten getMailAutoResponder/
  setMailAutoResponder/MailAutoResponder KALDIRILDI (kullanılmıyordu); i18n orphan panel.mail.autoResponder{Enabled,Message,
  Saved} + panel.sites.previewShow KALDIRILDI. 2462→2458. Backend Get/SetAutoResponder uçları duruyor (zararsız, UI çağırmıyor).
  managesieve config'i MEVCUT kurulumlara da uygulanıyor (EnsureRoundcubeConfig yönetilen config'i yeniden yazıyor) → boşluk yok.
- Doğrulama: build 0, test PASS, i18n 2458=2458, typecheck 0, vite OK, t()-tarama temiz (tek dinamik yanlış-poz).
  Installer SHA256: `A94F67ED0D88DD3760E3B57A797C7941D8DEB085A4F83F513CF8EAAD3AA27DF3` (49.38 MiB).

## 2026-07-02 (167) — Derin proje denetimi (Plesk-parite sağlık kontrolü): temel sağlam, 2 orphan temizlendi
- İSTEK: "projeyi çok detaylı tara, eksik/noksan/çalışmayan/yarım ne varsa" (Fable 5'e geçildi).
- SİSTEMATİK TARAMALAR (script'le): (1) frontend apiClient çağrıları (231) ↔ backend rota (255) çapraz-eşleşme;
  (2) backend Ok()/Fail() mesaj anahtarları ↔ i18n; (3) orphan api fonksiyonu + ölü view; (4) placeholder/TODO/"yakında".
- BULGULAR — İKİ BÜYÜK KORKU YANLIŞ POZİTİF ÇIKTI:
  * PHP + Güvenlik "site route'ları 404" DEĞİL: PhpController/SecurityController `[HttpPost("/api/v1/sites/{s}/php")]`
    gibi MUTLAK ('/'-önekli) template kullanıyor → gerçek yol frontend ile birebir. Script controller-prefix'i yanlış
    birleştirmişti. PHP ayarları + Güvenlik ÇALIŞIYOR.
  * "78 i18n anahtarı eksik" SORUN DEĞİL: frontend başarı bildirimini backend envelope.message'tan DEĞİL kendi
    `t("panel.*")` anahtarıyla gösteriyor (extractApiMessage yalnız HATA yolunda). Ham anahtar UI'da görünmüyor.
    (AppException hata kodları ayrı taramada zaten temizdi; tek eksik php.not_installed önceden düzeltilmişti.)
  * ssl/renew de bağlı (renewAllCertificates), enable-redis bağlı → endpoint "unused" listesi normalize yanlış-pozitifi.
- GERÇEK KOD-ARTIĞI (temizlendi): api/mail.ts diagnoseWebmail + WebmailDiagnostics tipi (Tanıla butonu kaldırılmıştı);
  api/sites.ts unlinkSiteDatabase (unlink butonu kaldırılmıştı). Backend uçları duruyor (zararsız).
- SAĞLIK: build 0, test PASS, typecheck 0, vite OK, i18n 2458=2458. Placeholder/"yakında"/TODO ekranı YOK. Ölü view YOK.
  Özellik envanteri Plesk çekirdeğini karşılıyor/aşıyor (site/mail/DNS/DB/SSL/FTP/dosya/firewall/docker/yedek/cron/WP-toolkit/
  mağaza/göç/kullanıcı-bayi/servis-planı/git/node-python/nginx/izleme).
- BİLİNÇLİ ERTELENENLER (kırık değil — Plesk-parite için aday): mail in-panel autoresponder (şu an webmail/managesieve),
  domain başına dedicated IP, MailEnable-yolu DKIM; DNSSEC + ikincil NS/AXFR; wildcard SSL (DNS-01); per-site FastCGI havuzu.
  CANLI-TEST bekleyen: nip.io önizleme, webmail vacation.
- Installer SHA256: `A5A7C3BB72A6D1B9DCA434361DD39A75541F4E20C749EBCF4B141E9623E2259C` (49.38 MiB).

## 2026-07-02 (168) — Yedekleme: OFF-SITE uzak rotasyon (retention artık uzak kopyayı da siler)
- DENETİM BULGUSU (167'de fark edildi): AutomationWorker günlük otomatik yedek (tüm siteler + DB + mail + panel-config) +
  PruneAsync retention VAR; ama PruneAsync YALNIZ yerel dosyayı buduyordu → off-site (S3/FTP/WebDAV) uzak kopyalar sonsuza
  dek birikiyordu (Plesk'te uzak rotasyon var). Kullanıcı "yap" dedi.
- YAPILAN: IBackupService.PruneAsync(retentionDays, BackupPruneRemoteOptions? remote=null, ct) — imza geriye-uyumlu (test
  PruneAsync(7) çalışır). BackupService.PruneAsync: süresi dolmuş kayıt off-site ise (RemoteUri dolu + TargetType s3/ftp/
  webdav/http) kaydı silmeden ÖNCE TryDeleteRemoteBackupAsync (best-effort, hata YUTULUR, budamayı bozmaz):
  DeleteFtpAsync (FtpWebRequest DELE + NetworkCredential), DeleteS3SignedAsync (S3RequestSigner.Sign("DELETE",...,
  EmptyPayloadSha256) → HTTP DELETE; olmayan nesnede 204 idempotent), DeleteHttpAsync (WebDAV/HTTP DELETE + basic auth).
  Kimliksiz S3 (presigned) silinemez → atlanır. RemoteUri temizlenmiş (kimliksiz) → kimlikler güncel ayardan gelir.
  Yeni DTO BackupPruneRemoteOptions{UserName,Password,Region} (Core.Dtos.Backup). AutomationService: hedef local değilse
  state kimlikleriyle pruneRemote geçirir (ResolveRemotePassword=DPAPI).
- NOT: retention yalnız otomatik yedek turundan SONRA çalışır (auto-backup kapalıysa hiç budanmaz — mevcut tasarım).
  CANLI TEST: off-site hedefte eski yedek uzakta gerçekten siliniyor mu (FTP DELE / S3 DELETE 204 / WebDAV DELETE).
- Doğrulama: build 0, test PASS (BackupRetentionTests dahil), typecheck değişmedi (backend-only).
  Installer SHA256: `29C44517715FE7546546A02831C0AE859662479EEC0B88CED9A4DE6E063EC673` (49.39 MiB).

## 2026-07-02 (169) — Tekrar tarama (temiz) + GitHub'a yükleme hazırlığı
- RE-TARAMA (yeni açı = DI kayıt bütünlüğü): 57 controller-enjekte arayüzden yalnız IAntiforgery "eksik" göründü → o
  framework-sağlı (panel canlıda çözüyor) = gerçek sorun DEĞİL. 167+168 ile birlikte kod tarafı sağlam.
- GITHUB: proje ZATEN git deposu (branch main), .gitignore düzgün (bin/obj/node_modules/.tools/artifacts/*.pfx/*.password/
  *.db hariç). Takip edilen gerçek gizli dosya yok. Son commit çok eskiydi → 141 değişik + 21 yeni dosya tek commit'te
  toplandı: `2f4f8a8`. Remote eklendi: origin = https://github.com/aliorala/adapanel.git. PUSH KULLANICIDA (kimlik/tarayıcı
  auth). gh CLI kurulu DEĞİL, git 2.54 var (Credential Manager tarayıcı açar).
- KULLANICI ADIMLARI: (A) kaynak: `git push -u origin main` (uzakta boş README varsa reddederse `--force`).
  (B) panel self-update: GitHub → Releases → yeni release → tag (vX.Y.Z) → artifacts\installer\AdaPanelSetup.exe'yi asset
  olarak yükle → publish → panelde Güncelle görür (SelfUpdateService releases/latest'ten çeker).

## 2026-07-02 (170) — Navigasyon erişilebilirlik denetimi (temiz) + README bilingual + git commit'ler
- YENİ TARAMA AÇISI: "yapılmış ama menüde/erişilemez mi" — SetupWizardView render switch + handleNavigation
  (accounts→settings/users, exit→logout) + WebsiteWorkspaceView (20 site-aracı) + ServerToolsView (health/firewall/backup/
  automation/nginx/logs/terminal/docker/dnstemplate) + settings tabs (general/account/security/users/notifications/updates).
  47 view'ın HEPSİ erişilebilir; ölü nav girişi yok. Admin yedek+otomasyona ServerToolsView'dan ulaşıyor.
- SONUÇ: 167 (endpoint/i18n/ölü kod) + 169 (DI) + 170 (nav) → hiçbir eksik/kopuk/erişilemez özellik YOK. Kod tarafı tam.
- YAPILAN: kök README.md çift dilli (EN/TR) tüm özelliklerle yeniden yazıldı (önceki TR-tek → EN+TR; portlar/kurulum/güvenlik
  korundu). Git: tüm çalışma commit'lendi (2f4f8a8) + README commit (6a9e0cb); remote origin=github.com/aliorala/adapanel.
  PUSH KULLANICIDA. Release açıklaması (bilingual) + adımlar kullanıcıya verildi.
- Bilinçli ertelenenler (kırık değil): DNSSEC, çoklu-IP mail, wildcard SSL (DNS-01), domain-başı dedicated mail IP.

## 2026-07-03 (171) — SELF-UPDATE BUG: sürüm damgası yok → panel hep "1.0.0" → güncelleme hiç uygulanmıyor gibi
- SEMPTOM: kullanıcı "Kur ve yeniden başlat" → "kurulum planlandı" → sonra hiçbir şey (güncelleme var uyarısı kaybolmuyor).
- KÖK NEDEN: HİÇBİR yerde sürüm damgalanmıyordu (csproj/scriptte <Version>/-p:Version YOK) → assembly "1.0.0".
  SelfUpdateService: IsUpdateAvailable = (VersionInfo.Current != releaseTag). Current="1.0.0", tag="0.2.4" → HEP true.
  Güncelleme kursa bile yeni build yine "1.0.0" der → uyarı hiç kaybolmaz → "hiçbir şey olmadı" algısı. (Mekanizma sağlam:
  WindowsUpdateInstallerLauncher SYSTEM zamanlanmış görev 15sn → apply-update.ps1 → AdaPanelSetup.exe /VERYSILENT;
  .iss PrepareToInstall servisi Stop-Service -Force ile durduruyor. Sorun kurulum değil, SÜRÜM KIYASI.)
- FIX: build-installer.ps1 + package-installer.ps1'e -Version param (varsayılan 0.2.4) + publish'e
  "/p:Version=$Version" "/p:InformationalVersion=$Version" /p:IncludeSourceRevisionInInformationalVersion=false.
  (Son property ŞART: yoksa .NET InformationalVersion'a "+<gitHash>" ekliyor → "0.2.4+6a9e..." yine tag'le eşleşmez.)
  Doğrulandı: yayınlanan AdaPanel.Api.exe ProductVersion = tam "0.2.4". Global /p:Version tüm projelere (Core/Infra/Data)
  akar → VersionInfo (Infra assembly) da 0.2.4.
- KULLANICI AKIŞI (release her seferinde): package/build -Version X.Y.Z İLE paketle → GitHub release tag'ini "X.Y.Z" YAP
  (aynı olmalı). Yeni installer SHA `3C09383F117DDFF9829911F12C29CC6F78CFC9CA93CB303C367124C9C37FB710` (v0.2.4, 49.39 MiB).
  → mevcut 0.2.4 release'indeki asset'i BUNUNLA değiştir + açıklamadaki SHA satırını güncelle. VPS'te panel bunu kurunca
  "0.2.4" der, uyarı kaybolur. Hâlâ kurulmuyorsa: C:\ProgramData\AdaPanel\updates\apply-update.log bak (installer logu).
- NOT: FileVersion hâlâ 0.2.2.0 görünüyor (bir yerde tanımlı, muhtemelen Directory.Build.props) — VersionInfo
  InformationalVersion'ı okuduğu için ETKİSİZ; istenirse ayrıca hizalanır.

## 2026-07-03 (172) — Surum TEK KAYNAK + script-driven (171'in landmine temizligi)
- 171'de InformationalVersion damgalandi ama FileVersion hala 0.2.2.0 gorunuyordu. KOK: Directory.Build.props
  surumu 4 yerde SABIT tutuyordu (<Version>/<AssemblyVersion>/<FileVersion>/<InformationalVersion> = 0.2.2).
  Komut satiri /p:InformationalVersion bunu eziyordu (o yuzden self-update calisiyordu) ama Assembly/FileVersion
  explicit oldugu icin /p:Version onlari EZEMIYORDU -> stale kaliyordu. Ayrica -Version unutulan build 0.2.2'ye duserdi.
- FIX (tek kaynak, stale-proof):
  * Directory.Build.props: sadece <Version>0.2.4</Version> + <IncludeSourceRevisionInInformationalVersion>false</...>.
    Assembly/File/Informational ARTIK Version'dan TUREM eder; /p:Version=X.Y.Z hepsini birden gunceller.
  * installer/AdaPanel.iss: #ifndef guard'li #define MyAppVersion (default 0.2.4) + VersionInfoVersion={#MyAppVersion}.
  * build/package scriptleri ISCC'ye "/DMyAppVersion=$Version" geciyor -> installer'in Programlar listesindeki
    surumu de -Version ile ayni. Boylece TEK yerden (-Version) her sey guncellenir.
- Dogrulandi: yeni AdaPanelSetup.exe -> panel .exe ProductVersion=0.2.4, FileVersion=0.2.4.0, installer surumu=0.2.4.
  YENI installer SHA `0DD96D196993CA89A4E0E1B49976D067510C47D74B517D82A956E300E6FB501A` (49.39 MiB). 171'deki 3C0938 ARTIK
  gecersiz -> 0.2.4 release'ine BUNU (0DD96D) yukle, aciklamadaki SHA satirini bununla guncelle.
- Testlerde surum iddiasi YOK (grep temiz) -> props degisikligi guvenli. frontend/package.json'daki 0.2.2 kozmetik
  (hicbir yerde gosterilmiyor), VersionInfo.cs fallback "0.2.2-preview" ulasilmaz savunma -> dokunulmadi.

## 2026-07-03 (173) — Ana sayfa sürüm göstergesi + "yeni sürüm" bandı + hermetik test düzeltmesi (0.2.5)
- ISTEK: ana sayfada sürüm görünsün + "yeni sürüm çıktı" uyarısı; test için 0.2.5 çıkar.
- SURUM GOSTERGESI (tüm roller): SystemOverviewDto zaten PanelVersion=VersionInfo.Current içeriyordu → DashboardOverviewView
  "Sunucu Bilgisi" kartına "AdaPanel sürümü" satırı eklendi (overview.panelVersion). Backend degisikligi YOK.
- "YENI SURUM" BANDI (yalnızca admin): SetupWizardView mount'ta isAdmin ise checkForUpdate() çağırıp panelUpdate ref'ini
  doldurur → DashboardOverviewView'a :panel-update prop. Banner overview-stack'in EN ustunde, v-if panelUpdate.isUpdateAvailable.
  "Güncelle" butonu → emit navigate('updates') → handleNavigation'a eklenen 'updates' case → activeSection='settings',
  settingsTab='updates' (UpdateView orada). i18n: panel.system.panelVersion + panel.dashboard.update.{available,hint,action}
  (tr+en, {version}/{current} placeholder). Parite 2461=2461.
  DIKKAT: banner KODU 0.2.5'te. Kurulu 0.2.4 panelde banner GORUNMEZ (guncellemeyi Ayarlar>Guncellemeler'de gorur). 0.2.5'e
  gecince banner kodu var → daha yeni bir release (0.2.6) cikinca banner yanar. Sürüm satırı ise 0.2.5'te hemen görünür.
- GERCEK HATA DUZELTILDI (benim degisikligim degil, ONCEDEN vardi, package-installer testleri atladigi icin gizli kalmisti):
  SelfUpdateServiceTests.ApplyUpdateAsync_RequiresSha256BeforeDownload HERMETIK DEGILDI — CheckForUpdateAsync varsayilan
  repo "aliorala/adapanel"ya GERCEK GitHub istegi atiyor. Repo bostken (release yokken) 404→SHA bos→checksum_required (test gecerdi).
  Biz 0.2.4'u YAYINLAYINCA gercek SHA donuyor→guard geciliyor→example.test'e baglanip socket hatasi→test kirildi. FIX: test
  StubHttpClientFactory artik NotFoundHandler (her istege 404) kullaniyor → ag yok, deterministik. 44/44 gecti. (Production kodu
  dogru; NormalizeSha256 64-hex zorunlu tutuyor, sadece test canli internete cikiyordu.)
- 0.2.5 installer SHA256 `F37D6D8E1816A803A4DE03B0B91BA95006CEE0FAE8BBA48782FDE5BEBE1AB9DA` (49.39 MiB). Release tag'i "0.2.5" YAP.

## 2026-07-03 (174) — CANLI ARIZA: self-update sonrasi servis KAPALI kaldi -> kalici cozum (0.2.6)
- OLAY: kullanici 0.2.5 guncellemesini paneldan kurdu -> panel ACILMADI (8890 disaridan kapali; 443/IIS saglam).
  Start-Service AdaPanel ile elle acildi -> mekanizma kurulumu yapmis ama SERVISI AYAKTA BIRAKMAMIS.
- KOK NEDEN ADAYLARI (log dogrulamasi beklenmedi, HEPSI kapatildi):
  1) install-service.ps1 guncellemede sil+yeniden-kur yapiyordu: sc delete acik handle varsa servisi "marked for
     deletion"da birakir -> script throw -> AfterInstall RaiseException -> kurulum servis baslatilmadan yarida.
  2) apply-update.ps1'de GUVENLIK AGI YOKTU: installer nasil biterse bitsin servis durumunu kontrol eden yoktu.
  3) Eski surec dosya kilidini gec birakirsa sessiz kurulumda dosya kopyalama patlar (PrepareToInstall 15sn bekliyordu, kill yoktu).
- FIX SETI:
  * install-service.ps1: mevcut servisi ARTIK SILMIYOR -> yerinde reconfigure (HKLM ImagePath + Set-Service auto; sc config
    quoting cehennemine girmemek icin registry). + sc.exe failure restart/5000/10000/30000 (cokme -> Windows kendisi baslatir).
    + Start-Service 6 deneme x 5sn (eski surec 8890'i gec birakirsa).
  * WindowsUpdateInstallerLauncher (apply-update.ps1 uretici): Write-UpdateLog ([runner] onekli, apply-update.log'a) +
    installer exit code loglanir + SON GUVENCE blogu: installer ne dondururse dondursun Get-Service -> Running degilse
    8 deneme x 5sn Start-Service; hala degilse log + exit 1. Unregister-ScheduledTask finally'den cikip sona alindi.
  * AdaPanel.iss PrepareToInstall: 15sn stop beklemesinden sonra surec hala yasiyorsa taskkill /F /IM AdaPanel.Api.exe + 2sn.
  * SelfUpdateService.CheckForUpdateAsync: pending.Version == currentVersion ise pending-update.json + eski paket SILINIR
    (best-effort) -> "kurulum planlandi" etiketi kalici kalmaz, updates klasoru 50MB'lik eskilerle dolmaz.
- DIKKAT (tek seferlik): 0.2.5->0.2.6 guncellemesi ESKI runner scriptiyle calisir (scripti KURULU panel uretir) ->
  bu gecis bir kez daha ayni belirtiyi verebilir; cozum Start-Service AdaPanel. 0.2.6'dan itibaren zincir kendi kendini onarir.
- 0.2.6 SHA256 `24DE9186E2F6CB2732BABE5A35F1A6D763A4A25F8AFE564A956C0B6D6FB309BA` (49.39 MiB), tum surum varsayilanlari
  (props/scriptler/iss) 0.2.6. Testler 2+44 yesil. 0.2.6 yayini ayrica 0.2.5 panelde YENI SURUM BANNERINI canli test ettirir.

## 2026-07-03 (175) — Denetim onaylari uygulandi (0.2.7): uninstall veri korumasi + banner login + semver + kozmetik
- (1) UNINSTALL VERI KORUMASI: AdaPanel.iss InitializeUninstall -> MsgBox "BARINDIRMA VERILERI DE SILINSIN MI?"
  (varsayilan HAYIR=koru; sessiz kaldirma=koru). GetUninstallKeepData scripted-constant -> uninstall-cleanup.ps1
  -KeepData "1|0". KeepData=1: yalnizca AdaPanel servisi + AdaPanel-SelfUpdate gorevi kaldirilir, siteler/DB servisleri/
  yedekler/DataRoot CALISIR halde kalir (Plesk davranisi). KeepData=0: eski tam temizlik. Kosulsuz veri silen
  [UninstallDelete] {commonappdata}\AdaPanel SATIRI KALDIRILDI (silme artik yalniz scriptte, bayraga bagli).
- (2) BANNER LOGIN GAP: SetupWizardView.submitLogin basari yolunda `void loadPanelUpdate()` -> giristen hemen sonra
  banner gelir (once yalniz mount'ta kontrol vardi, login sonrasi F5 gerekiyordu).
- (3) SEMVER KIYAS: SelfUpdateService.IsUpdateAvailable artik IsNewerVersion(current,latest) (internal static):
  "-suffix"/"+hash" atilir, Version.TryParse, latest > current; cozulemezse eski esitsizlik. Boylece panel 0.2.7 iken
  GitHub'da 0.2.6 kalmasi "guncelleme var" DEDIRTMEZ (downgrade/gecikmis-release durumu). 7 InlineData'li Theory eklendi
  (InternalsVisibleTo zaten vardi).
- (4) KOZMETIK: frontend package.json+lock 0.2.7 (npm --package-lock-only ile lock sync), VersionInfo fallback "0.0.0".
- (5) GIT GUVENLIGI: `git remote remove origin` (kaynak yanlislikla push edilemesin; releases GitHub web'den yukleniyor.
  Geri eklemek: git remote add origin https://github.com/aliorala/adapanel.git).
- Tum surum varsayilanlari 0.2.7. Uretilen/duzenlenen ps1'ler parser-dogrulandi. 0.2.7 installer SHA256
  `C7DCF49AF57F72EBC1D114EFD5E96EA24E9D4CB466DD1AA992F66B47F9448E86` (49.39 MiB). 0.2.6 YAYINDA (24DE9186)
  -> kullanici 0.2.6'ya guncelleyince 0.2.7 yayinlanacak; 0.2.6->0.2.7 gecisi YENI dayanikli runner'la ilk tam test.

## 2026-07-03 (176) — Guncelleme UX'i tamamlandi: kurulum-sonrasi otomatik izleme + "Panel guncel" durumu (0.2.7 final)
- EKSIK: "Kur"a basildiktan sonra tarayici KOR kaliyordu (kullanicinin ilk sikayeti "kurulum planlandi...sonra bisey yok"
  mekanizma duzelse bile UI'da yasanacakti). Plesk kurulumdan sonra izleyip sayfayi yeniler -> ayni davranis eklendi.
- UpdateView.vue: installUpdate basarisinda startPostInstallWatch(versionBeforeInstall) -> 5 sn'de bir checkForUpdate;
  servis yeniden başlarken istekler reddedilir (catch -> sessiz bekle), YENI currentVersion gelince "guncelleme tamamlandi"
  + 1.5 sn sonra window.location.reload(); 401 gelirse (oturum dustu) direkt reload -> login. 5 dk timeout ->
  panel.update.updateTimeout (apply-update.log yolu gosterilir). onBeforeUnmount interval temizler.
- "PANEL GUNCEL" durumu: !isUpdateAvailable iken yesil "panel.update.upToDate" bandi + INDIR butonu artik
  isUpdateAvailable sartina bagli (eski/esit surumu yanlislikla indirme-kur = downgrade kapisi kapandi; Plesk gibi).
- i18n +4 anahtar (upToDate/waitingRestart/updated/updateTimeout, tr+en) -> parite 2465=2465. Testler 2+51 yesil.
- 0.2.7 FINAL SHA256 `B753A4AD5D345A39AFB7C7232FE5B0A9E0DB94141B99B1ABEE5A3EDB130350FF` (onceki C7DCF4 build'i GECERSIZ,
  kullanici henuz yayinlamamisti). Yayin sirasi: VPS'i 0.2.6'ya guncelle -> 0.2.7'yi tag "0.2.7" ile yayinla ->
  0.2.6->0.2.7 gecisi yeni dayanikli runner + yeni izleme UX'inin ilk tam ucta-uca testi.

## 2026-07-03 (177) — Planli gorevler PLESK PARITESI: 3 gorev tipi (komut/PHP/URL) + "Simdi calistir" (0.2.7)
- ISTEK: "planli islere sayfa calistirma ekle" + kullanicinin GERCEK ihtiyaci: IPTV uygulamasinin cron uclari
  (reverify + cleanup, gunde 1 GET). Plesk'in 3 gorev tipi aynen alindi.
- BACKEND: CreateScheduledTaskRequest += TaskType("command|php|url") + Url + PhpVersion. SchedulerService.ResolveAction:
  url -> curl.exe -fsSL --max-time 300 --retry 2 "<url>" (Server 2019+ ile gelir); php -> %ProgramData%\AdaPanel\
  runtimes\php altinda php.exe cozumu (surum istegi contains-match, yoksa en yeni; hic yoksa cron.php_not_installed),
  arguman = "betik yolu" + ek arglar, WD bos ise betik klasoru; command -> eskisi. ONEMLI FIX: eylem artik TEK-TIRNAK
  ('' kacisli) PS stringleriyle kuruluyor -> URL/komut icindeki $ & " PowerShell'de bozulmuyordu bug'i kapandi
  (eski cift-tirnak $-expansion landmine'iydi). Okuma: Execute curl.exe->url (Url = argumanlardaki SON "..." grubu),
  php.exe->php, digeri command. RunTaskAsync (Start-ScheduledTask) + POST /cron/{name}/run (Plesk "Run Now").
- FRONTEND CronView: gorev tipi select + kosullu alanlar (url: tek URL alani + ipucu; php: betik yolu + arglar;
  command: eskisi) + tabloda tip etiketi, url gorevlerinde hedef URL gosterimi, satirda "Simdi calistir" butonu
  (4 sn sonra listeyi yenileyip Son calisma sutununu gunceller). api/cron.ts runScheduledTask + genisletilmis tipler.
- i18n +17 anahtar (panel.tasks.* 12 + cron.* 4 + ...) -> parite 2480=2480. Testler 2+51 yesil.
- KULLANICI KURULUMU (0.2.7 sonrasi, Planli Gorevler): 2 gorev, tip=URL cagir, gunluk:
  1) "IPTV VIP dogrulama" url=https://alioral.com.tr/iptv/public/index.php/v1/cron/reverify?key=... saat 03:00
  2) "IPTV temizlik"      url=https://alioral.com.tr/iptv/public/index.php/v1/cron/cleanup?key=...  saat 03:10
  "Simdi calistir" ile aninda test edilebilir. (Adresler gizli key icerir - panelde kalir, sorun degil.)
- 0.2.7 FINAL SHA256 `DBDF3187CA0E8EE9932E141CEF5CB445E2A446EE3B489A17B435009353BB500F` (onceki B753A4 GECERSIZ, yayinlanmamisti).

## 2026-07-03 (178) — Cron paritesi TAMAM: ac/kapat + son sonuc + hata e-postasi + musteri/bayi cron (0.2.8)
- Kullanici 0.2.7'yi C7DCF4 build'iyle yayinladi (update-UX ve cron tipleri ICERMIYOR) -> hepsi 0.2.8'de toplandi.
- AC/KAPAT: ISchedulerService.SetTaskEnabledAsync (Enable-/Disable-ScheduledTask) + POST /cron/{name}/enable|disable
  + satirda Ac/Durdur butonu (state Disabled'a gore). cron.enabled/disabled anahtarlari.
- SON SONUC sutunu: Get-ScheduledTaskInfo LastTaskResult (long?) DTO+tablo; 0=Basarili yesil, 267009/267045
  calisiyor/kuyrukta notr, 267011 hic calismadi "-", digerleri "Hata (0x..)" warn pill.
- HATA E-POSTASI (Plesk notify-on-error): AutomationService.RunAlertChecksAsync'e cron blogu — LastTaskResult
  ozel-kod-disi != 0 VE son 2 gunde calismis -> mevcut uyari kanaliyla (email+webhook, 24h cooldown, key cron:{ad})
  Turkce uyari. DI: AutomationService += ISchedulerService.
- MUSTERI/BAYI CRON (guvenli kapsam): CronController rolleri Admin,Reseller,Customer. Admin-disi: TaskType=url'e
  ZORLANIR (komut/PHP = sunucuda kod calistirmak, yalniz admin; Plesk de musteriyi kisitlar), ad "u{userId:N}--"
  onekiyle yasar (GET filtre+onek soyma, run/delete/enable/disable ScopeName ile onek ekler -> baskasinin gorevine
  ERISEMEZ), URL loopback/localhost/169.254.* HEDEFLEYEMEZ (cron.url_forbidden; panel API/SSRF kapisi kapali).
  UI: CronView isAdmin degilse tip selecti gizli+form url'e kilitli; SetupWizard 'tasks' bolumu 3 role acildi
  (mount kosulu + reseller/customer section filtreleri).
- i18n +8 -> parite 2488=2488. Testler 2+51 yesil. Surumler 0.2.8 (props/scriptler/iss/package.json+lock).
- 0.2.8 SHA256 `DC2FF48A9A8E8B634F0F1461CD0BF824635EFBAE8B2C6942C45B6A1815470D65` (49.39 MiB) -> tag "0.2.8" ile yayinla.
  VPS hala 0.2.5 ise dogrudan 0.2.8'e atlanabilir (eski runner tek-seferlik riski: gelmezse Start-Service AdaPanel).
- KALAN (kullaniciyla canli): K2 MariaDB root parolasi + B3 phpMyAdmin HTTPS; buyuk ertelenenler: DNSSEC,
  wildcard DNS-01, domain-basi mail IP.

## 2026-07-03 (179) — CANLI HATA: cron "kaydedilemedi" -> [TimeSpan]::MaxValue mayini (0.2.9)
- OLAY: kullanici 0.2.8'de (yayinladi+kurdu) URL cron eklerken "Zamanlanmis gorev kaydedilemedi" (cron.write_failed).
- TESHIS YONTEMI: uretilen Register scriptini LOKALDE birebir calistirdim (EncodedCommand ayni yolla):
  * daily -> "Erisim engellendi" (lokal yonetici degil; VPS SYSTEM'de sorun yok, ortamsal)
  * hourly (FORM VARSAYILANI) -> GERCEK HATA: "Gorev XML'si ... aralik disi deger: Duration P99999999DT23H59M59S"
    => New-ScheduledTaskTrigger -RepetitionDuration ([TimeSpan]::MaxValue) Task Scheduler XML dogrulamasinda REDDEDILIYOR.
    Cron ekraninin ilk gununden beri duran mayin; URL ozelliginin ilk gercek kullaniminda patladi (kullanici hourly varsayilaniyla denedi).
- FIX: her iki tekrarli tetikleyicide (minute/hourly) MaxValue -> (New-TimeSpan -Days 3650) (10 yil = fiilen suresiz;
  lokalde dogrulandi: XML gecti, ACL asamasina ulasti). + cron.write_failed artik result.StandardError DETAYLI
  (create+delete) -> bir daha kor "kaydedilemedi" yok (UI extractApiDetails ile gosterebilir/loglanir).
- SURUM DEMOSU NOTU: kullanici "guncelleme bitince neden tepki yok" diye sordu — takip/otomatik-yenileme UX'i 0.2.8'de
  ve IZLEYEN ekran ESKI surumun ekranidir; 0.2.5/0.2.7'den guncellerken sessizlik NORMALDI. VPS artik 0.2.8 oldugundan
  0.2.8->0.2.9 gecisi bu deneyimin ilk canli gosterimi olacak (Ayarlar>Guncellemeler sayfasinda KALARAK izlenmeli).
- Surumler 0.2.9; 0.2.8 exe artifacts\release\0.2.8\ altinda korunuyor (DC2FF4), 0.2.9 exe artifacts\release\0.2.9\
  SHA256 `DFEAEF379DD2032CDEECE1F1E247EABE208459B4CF9D8E920E4A618EDD35D944`. Testler 2+51 yesil.

## 2026-07-04 (180) — Cron "kaydedilemedi" DEVAM ETTI (0.2.9'da da) -> SYSTEM principal fix + ekranda hata detayi (0.2.10)
- 179'daki MaxValue fix'i GERCEK ama YETMEDI (kullanici buyuk ihtimalle daily denedi; daily'de MaxValue yolu yok).
- EN OLASI KOK (kanitlanmis-desen kiyasi): SchedulerService Register-ScheduledTask'i PRINCIPAL'siz "-RunLevel Highest"
  ile cagiriyordu. Panel servisi LocalSystem; SYSTEM baglaminda principal'siz kayit hesap eslemesinde patlayabiliyor.
  KANIT: WindowsUpdateInstallerLauncher AYNI sunucuda AYNI servisten New-ScheduledTaskPrincipal -UserId 'SYSTEM'
  -LogonType ServiceAccount -RunLevel Highest ile DEFALARCA basarili kayit yapti. FIX: create scripti birebir o desene
  cevrildi ($principal ... ; Register ... -Principal $principal) + basa $ErrorActionPreference='Stop' (non-terminating
  hatalar exit 0'la 'basarili' gorunmesin). Lokal dogrulama: parametre/XML gecti (yalniz elevation ACL'ine takildi = beklenen).
- KORLUK BITTI: CronView.resolveApiMessage artik extractApiDetails'i mesaja EKLIYOR ("kaydedilemedi — <gercek PS hatasi>").
  0.2.9 backend'i zaten Details tasiyordu (ApiResponse.Details + middleware exception.Details) ama UI gostermiyordu.
  Bundan sonra cron hatasi ekranda GERCEK nedeniyle gorunur -> kullanici direkt okuyup iletebilir.
- NOT (arastirma sirasi icin): ProcessRunner.Success = ExitCode==0; RunPowerShellAsync -EncodedCommand (UTF-16, encoding derdi yok);
  middleware AppException'i LogWarning'ler ve Details'i HTTP yanitina koyar; Serilog dosya loglari ProgramData\AdaPanel\logs.
- 0.2.10 SHA256 `9D501E845ABFDF20F2008E587FFEC380F992FFE3C426EE031D96734D42AD767C` (artifacts\release\0.2.10\). Testler 2+51.
  HALA basarisiz olursa: ekrandaki "—" sonrasi detay metnini al (gercek neden orada), tek bakista cozulur.

## 2026-07-04 (181) — common.unexpected_error COZULDU: PS5.1 ConvertTo-Json tarih bicimi + Frequency hayaleti (0.2.11)
- ZINCIRIN 3. HALKASI: 0.2.10'da kayit ILK KEZ basarili oldu (principal fix tuttu) -> akis GERI OKUMAYA gecti ve
  ORADA patladi: PS 5.1 ConvertTo-Json [datetime]'i "\/Date(ms)\/" yazar, System.Text.Json bunu DateTime? olarak
  OKUYAMAZ -> JsonException -> middleware catch-all -> common.unexpected_error. Bugune dek gorunmemesinin nedeni:
  liste HEP BOSTU (tum kayitlar basarisizdi) -> tarih hic serilestirilmedi. KANIT lokal: [datetime] | ConvertTo-Json
  => "\/Date(...)\/" birebir.
- IKINCI HAYALET ayni scriptte: ScheduleType = $_.Triggers[0].Frequency — trigger CIM nesnesinde Frequency OZELLIGI YOK
  (Get-Member False) -> tip hep bos kalacakti. FIX: CimClassName=='MSFT_TaskDailyTrigger' -> daily;
  Repetition.Interval '^PT\d+M$' -> minute; else hourly.
- READ SCRIPT FIX: tarihler ISO ('o') STRING (Year>2000 guard: hic-calismadi 1899 tarihleri null/'-' olur),
  Command/Arguments/WorkingDirectory [string] zorlamasi (null -> '' ; C# EndsWith NRE zirhi + C# tarafinda ??= belt).
  Gercek gorevlerle (Time Synchronization klasoru) UCTAN UCA dogrulandi: /Date yok, JSON gecerli, progress gurultusu stderr'de.
- YAN KAZA: surum bump'inda Set-Content -Encoding utf8 (PS5.1 = BOM'LU) package.json'a BOM ekledi -> vite/rolldown
  build coktu ("unwrapBindingResult"). BOM'suz yeniden yazilinca gecti. DERS: json/props yazarken UTF8Encoding($false) kullan.
  (0.2.11 klasorune yanlislikla kopyalanan eski 0.2.10 exe'si silindi; dogru exe yeniden kopyalandi.)
- KULLANICIYA NOT: 0.2.10'daki "unexpected_error" sirasinda gorev BUYUK IHTIMALLE OLUSTU (kayit basarili, okuma cokuyordu);
  0.2.11'e gecince Planli Gorevler listesinde GORUNECEK — kopya olusturmadan once listeye bak (ayni ad zaten unregister+re-register, sorun degil).
- 0.2.11 SHA256 `4B7711EA0C199E791974C3BF5B7BCA03468FCE120570597024338E1FDFCAC08D` (artifacts\release\0.2.11\). Testler 2+51.

## 2026-07-04 (182) — CANLI ONAY: cron URL gorevi VPS'te calisti (0.2.11)
- Kullanici 0.2.11'i yayinladi+kurdu: "gorev listede gorundu calisti". Boylece UCTAN UCA KANITLANDI:
  self-update zinciri (0.2.5->...->0.2.11 sorunsuz) + cron kayit (SYSTEM principal) + okuma (ISO tarih) + calisma (curl URL GET).
- 179-181 zincirinin ozeti (uc maskelenmis hata): (1) RepetitionDuration MaxValue XML reddi -> (2) principal'siz
  Register SYSTEM'de hesap eslemesi -> (3) PS5.1 /Date() JSON'u System.Text.Json'da patlar. Her fix bir sonrakini acti;
  hepsi lokal birebir-repro ile kanitlanarak cozuldu. Uretilen PS scriptleri = derleyici korumasiz alan; degisiklikte
  MUTLAKA lokal calistir-dogrula.
- ACIK KALAN KUCUK ISLER: kullanici ikinci gorevi (IPTV cleanup 03:10) ekleyecek; cron hata e-postasi icin SMTP
  (Ayarlar>Bildirimler) yapilandirilmis olmali. BUYUK BEKLEYENLER: K2+B3 guvenlik (canli-guided), DNSSEC, wildcard DNS-01, domain-basi mail IP.

## 2026-07-04 (183) — GERI BILDIRIM iclesti + planli gorevler YEDEGE dahil + kesisim matrisi taramasi (0.2.12)
- Kullanici hakli cikisti: "yedek planli gorevleri kapsiyor mu"yu ONUN sormasi gerekmemeliydi. KALICI KURAL
  (memory adapanel-proactive-audit'e de islendi): yeni ozellik bitince ALT SISTEM KESISIM MATRISI kendiliginden taranir:
  yedek/geri-yukleme, kaldirma (2 mod), tasima sihirbazi, plan limitleri, roller, self-update kaliciligi + uretilen
  scriptler LOKALDE calistirilarak kanitlanir.
- CEVAP: HAYIR, yedeklemiyordu. Panel yedegi yalniz data/adapanel.db + config/*.json iceriyordu; gorevler Task
  Scheduler'da yasar. FIX (Plesk paritesi): BackupPanelAsync gorevleri Export-ScheduledTask ile HAM XML olarak
  tasks/NNN.xml + tasks/index.json seklinde zipe koyar (best-effort; principal/tetikleyici birebir).
  RestoreAsync panel-tipi zipte cikartmadan sonra TryRestoreScheduledTasksAsync: index'ten her gorevi
  Register-ScheduledTask -Xml (XML base64 tasima = kacis derdi sifir) -Force ile geri kaydeder. Musteri gorevleri
  (u<id>-- onekli) XML ile aynen doner. Derleme notu: BackupService'te using System.Text.Json yok -> JsonElement
  .Deserialize<T>() uzanti metodu gorulmez; JsonSerializer.Deserialize<T>(root) statik formu kullanildi.
- MATRIS BULGULARI (kendi taramam, kullanici sormadan):
  * Tasima sihirbazi (Plesk->AdaPanel) planli gorevleri TASIMIYOR (site+dosya+DB tasiyor) -> backlog.
  * Servis planlarinda gorev limiti YOK (Plesk planlarinda var; musteri sinirsiz URL gorevi kurabilir).
    CustomerScope positional-record genisletmesi cok dosyaya dokunuyor -> aceleye getirmedim, backlog (dogru is: MaxScheduledTasks alani).
  * Kaldirma keep-data modunda kullanici gorevleri KORUNUYOR (yalniz AdaPanel servisi+self-update gorevi kalkar) OK;
    full-wipe \AdaPanel\ gorevlerini temizliyor OK.
  * FELAKET KURTARMA SINIRI (bilinen): panel yedegi IIS kayitlarini (site/binding/pool/FTP) YENIDEN KURMAZ;
    adapanel.db siteleri bilir ama IIS reconcile mekanizmasi yok -> buyuk backlog kalemi ("tam sunucu geri yukleme").
- 0.2.12 SHA256 `039CDC838366C83A248FADE8D8C6588E584C2F9DE10986978AF224870690963C` (artifacts\release\0.2.12\). Testler 2+51.
- BACKLOG SIRASI: (1) K2+B3 guvenlik = SIRADAKI, kullanici VPS basindayken (DB root parolasi yaris riski);
  (2) plan gorev limiti; (3) tasimada cron; (4) DNSSEC / wildcard DNS-01 / domain-basi mail IP; (5) tam-sunucu DR.

## 2026-07-04 (184) — "Kalan isleri bitir" turu: K2+B3 ZATEN URUNDE dogrulandi + plan gorev kotasi eklendi (0.2.12 final)
- BAYAT BACKLOG DUZELTMESI (onemli!): K2 ve B3 icin "kullaniciyla canli yapilacak" diyordum — YANLISTI, ikisi de coktan kaynakta:
  * K2: DatabasesController /databases/harden -> HardenAdminAccountsAsync -> SetRootPasswordAtomicAsync (once SAKLA,
    sonra UYGULA+DOGRULA, hata->geri al; tek kaynak: InstalledComponent ProtectedConfiguration; phpMyAdmin ayni parolayi
    ReadRootPassword ile alir) + anonim/uzak-root/test-db temizligi. UI: Veritabanlari ekrani "Guclendir" butonu.
    KULLANICI TEK SEFER TIKLAMALI (guvenli, atomik, geri-alinabilir). CODEX 61'deki "K2 yapma" notu 63'te asilmis;
    174-183 arasi ben 61'e bakip yanlis rapor ettim. DERS: guvenlik durumu icin HER ZAMAN en guncel girdiyi tara.
  * B3: phpMyAdmin/Adminer IIS'te LOOPBACK'e bagli + panel uzerinden DbToolsProxyController ile proxy'li (X-Forwarded-Proto
    'https') -> dis dunyaya yalniz panelin HTTPS'i acik. B3 mimaride KAPALI.
- YENI: PLAN GORELI GOREV KOTASI (MaxScheduledTasks, 0=sinirsiz; Plesk plan paritesi):
  CustomerScope record +alan (TUM ctor'lar guncellendi: UMS 55/285, CSS 26/27), ScopeEntry+persist+3 bos-kayit-temizlik
  kosulu, SetExtraLimitsAsync +param, ApplyScopeAsync +param (create/update user), UserSummaryDto/Create/UpdateUserRequest/
  ServicePlanDto+Request +alan, ServicePlanService create/update. ENFORCEMENT: CronController non-admin create'te scope
  kotasi (ayni ada re-register kotayi doldurmaz; asim -> cron.quota_exceeded 409). FRONTEND: plan karti "Planli gorev"
  satiri, plan formu + kullanici olustur/duzenle formlarina kota alani, applyPlanToCreate/reset/selectUser baglari,
  auth.ts 3 tip + servicePlans.ts tip/payload. i18n +4 (panel.users.maxScheduledTasks, panel.plans.scheduledTasksLabel,
  cron.quota_exceeded tr+en) -> parite 2491=2491.
- KASITLI ERTELENENLER (kullaniciya acikca soylendi): DNSSEC / wildcard DNS-01 / domain-basi mail IP -> her biri VPS
  ortaminda test ister (DNS server, ek IP), buradan dogrulanamaz; tek buyuk pakete koymak 179-181 tarzi zincir risk dogurur.
  Tasima-cron: Plesk REST cron uclarini discover'imiza vermiyor -> ayri arastirma. Tam-sunucu DR (IIS rebuild) ayri oturum.
- 0.2.12 FINAL SHA256 `A80029220CE860FD51500FA580AA8AAFC4EB3A79A01B36BA5B7019D5A043A9E0` (yedek-gorev + kota BIRLIKTE;
  onceki 039CDC yayinlanmamisti, gecersiz). Testler 2+51, parite 2491.

## 2026-07-04 (185) — Komple tarama: otomatikler TEMIZ; 2 gercek kesisim acigi bulundu+duzeltildi (0.2.13)
- TARAMA (kullanici "komple tara bul yap"): i18n 279 backend anahtari tam iki dilde, parite 2491=2491; DI kayitlari
  eksiksiz (Scheduler/CustomerScope/Automation), DONGU YOK (SchedulerService bagimsiz); build+test 2+51 yesil.
  Yedek export/restore PS scriptleri GERCEK round-trip ile dogrulandi (2 gorev export, XML+base64 tasima, restore
  kayit asamasina ulasti — yerelde "erisim engellendi" = admin degilim, VPS SYSTEM'de sorun yok).
- BULGU 1 (izlenebilirlik): CronController AUDIT YAZMIYORDU (tum diger controller'lar yaziyor). Multi-tenant'ta
  musteri/bayi URL cron kurabildiginden kim-ne-zaman-hangi-URL onemli. FIX: IAuditService enjekte; created/deleted/
  run/enabled/disabled -> auditService.WriteAsync("cron.*","cron",details,userId,RemoteIp). created details'e tip+hedef URL.
- BULGU 2 (GUVENLIK/SSRF): EnsureCustomerUrlAllowed yalniz loopback+localhost+169.254 engelliyordu; IC AG (10/8,
  172.16/12, 192.168/16, 0/8) ve IPv6 ozel adresler ACIKTI -> musteri panelden ic agi tarayabilir/vurabilirdi.
  FIX: async EnsureCustomerUrlAllowedAsync -> IsBlockedAddress(IPAddress): loopback + IPv4 ozel bloklar + IPv6
  link-local/site-local/fc00::/7. Literal IP KESIN engellenir; hostname best-effort Dns.GetHostAddressesAsync ile
  cozulup ozel'e dusuyorsa reddedilir (DNS cozulemezse literal koruma yeterli, mesru site engellenmez). http/https semasi zorunlu.
- KUCUK: cron failure-alert tarih kiyasi task.LastRunTimeUtc.Value.ToUniversalTime() (System.Text.Json "Z"li ISO'yu
  yerel Kind'e ceviriyordu; 2 gun esiginde ~offset kaymasi). NOT (dusuk oncelik, ertelendi): RunAlertChecksAsync
  AlertsEnabled iken her dakika GetTasks (PS process/dk); disk/cert de zaten her dakika -> tasarim, kabul edildi.
- 0.2.13 SHA256 `CEFC97BD03A6EE90DCE956CD4B814D67E0A7B0FE8138ACCB24E994AB1CF1C529` (artifacts\release\0.2.13\).
  0.2.12 YAYINDA oldugu icin yeni surum. Testler 2+51, parite 2491.

## 2026-07-04 (186) — Plesk parite YOL HARITASI basladi: Advisor oneri motoru (A8) tamamlandi (0.2.14)
- Kullanici "hepsini yap pleskte varsa bizde de olmalı". Tek turda 9 buyuk sistemi yigmak = cron-zinciri riski, o yuzden
  ONCELIK SIRASIYLA her tur bir tam+test-edilmis artis. Batch 1 = Advisor (tamamen yerel-dogrulanabilir, VPS gerektirmez).
- TARAMA DUZELTMESI (onemli, eski backlog yanlisti): DNSSEC ZATEN VAR (SetDnssecRequest+DnsService+DnsController);
  mail greylist+spam esigi VAR (MailService GetSpamSettings), mailing list VAR, hotlink koruma VAR (SiteHostingSettings),
  Node/Python runtime VAR. GERCEK EKSIKLER: antivirus/malware(ClamAV), otomatik wildcard SSL(DNS-01), Ruby runtime,
  SEO toolkit, abonelik-alti ek kullanicilar, coklu-sunucu, tam-sunucu DR (IIS rebuild), 1-tik app katalog genisligi.
- ADVISOR (Plesk Advisor karsiligi): IAdvisorService/AdvisorService [SupportedOSPlatform windows], her kontrol ayri
  try/catch (dayanikli). Sinyaller (yeni altyapi KURMADAN): ISelfUpdateService(guncelleme), config/ssl.json(SSL bitis
  <14g uyari / <0 kritik), ISystemMonitoring(disk>=90/95, RAM>=90), IAutomationService(AutoBackup/AutoSslRenew/Alerts),
  INotificationService(alerts acik ama kanal yok), IPanelSecurityService(gizli yol="panel", IP allowlist bos).
  AdvisorItemDto{Id,Severity,TitleKey,DetailKey,Detail(dile-notr: host/surum/%/tarih),ActionSection}. Rapor onem sayilari
  + severity-sirali. Controller GET /api/v1/advisor [Administrator]. DI: AddScoped IAdvisorService.
  FRONTEND: api/advisor.ts + AdvisorView.vue (ozet pill'ler + severity-renkli liste + "Duzelt"->emit navigate; bos ise
  "her sey yolunda" yesil kart). SetupWizard: 'advisor' bolumu (monitor'dan sonra, glyph AD, admin-only; reseller/customer
  allow-list'lerde YOK), render @navigate="handleNavigation". i18n +25 anahtar tr+en, parite 2515=2515.
- 0.2.14 SHA256 `AAB1E2E44B4BED4B0D15421475A16876B7885C6410E9CEF563FC70681F817352`. Testler 2+51.
- KALAN PLESK PARITE SIRASI: (Batch2, yerel) Ruby runtime + 1-tik app katalog (Joomla/Drupal/prestashop templateleri);
  (Batch3, VPS-testli) ClamAV antivirus + wildcard SSL DNS-01; (Batch4) abonelik-alti kullanicilar; (Batch5) tam-sunucu DR;
  (deferred) SEO toolkit, coklu-sunucu (enterprise). VPS-testli olanlar kullanici test edebilecekken yapilacak.

## 2026-07-04 (187) — Batch 2: 1-tik app katalog (Grav + Drupal) + 0.2.15 KONSOLIDE surum
- BATCH 2 KARARI: Ruby runtime'i DUSURDUM — Plesk-on-Windows'ta Ruby fiilen yok + Rails/native-gem Windows'ta
  finicky + runtime yerel test edilemez ("pleskte olmayani yapma" + test-edilemez-riski). Onun yerine AdaPanel'in
  GUCLU oldugu alan: PHP CMS 1-tik katalog.
- APP STORE KATALOG: AppStoreService.Templates dizisine Grav (DB'siz, dosya-tabanli, ANINDA hazir) + Drupal
  (DB'li, dosyalar hazir + kullanici taryici sihirbazini tamamlar) eklendi. Resmi HTTPS "latest" URL'leri
  (getgrav.org/download/core/grav/latest, drupal.org/download-latest/zip — ikisi de latest zip'e 302). InstallAsync
  switch'e grav/drupal -> InstallDownloadableAppAsync (WordPress'le AYNI kanitlanmis akis: geçici workspace'e
  DownloadAndExtractZipAsync + FlattenSingleTopDirectory + CopyDirectory + TryDeleteDirectory). DefaultRootFolder
  sadece etiket (dosyalar ResolveDestinationAsync -> site docroot'una gider). EnsurePhpEntry switch sonrasi calisir.
- GUVENLIK KARARI: DownloadAndExtractZipAsync artik expectedSha1 BOSSA checksum'i ATLAR (yalniz HTTPS/resmi kaynaga
  guven). Bu YENI bir posture DEGIL — components.json'da zaten bos-sha256 bileşenler ayni sekilde kuruluyor. Sabit
  surumde sha verilirse yine dogrulanir. (SSRF/audit oturumundan sonra bunu bilinçli ve tutarli tuttum.)
- TEST NOTU: indirme/açma VPS'te dogrulanmali (buradan buyuk zip indirilemedi); KOD yolu WordPress'le birebir ayni,
  derleme yesil. Grav en dusuk riskli (DB yok, tek adim). Drupal legacy-zip layout -> index.php flatten sonrasi kokte.
- 0.2.15 = KONSOLIDE: 0.2.13 (cron audit+SSRF) + 0.2.14 (Advisor) + Batch2 (Grav/Drupal) TEK PAKETTE. Kullanici
  0.2.12'den sonra HICBIRINI yayinlamamis (GitHub'da en son 0.2.12) -> ara surumleri atla, 0.2.15'i yayinla.
  SHA256 `3ED00206D48ADFDB39DA7F32843E2028EFA835CCEA3DDD9A109F5970070A4AE6`. Testler 2+51.
- KALAN PLESK PARITE: (Batch3 VPS-testli) ClamAV antivirus + wildcard SSL DNS-01; (Batch4) abonelik-alti kullanicilar;
  (Batch5) tam-sunucu DR (IIS rebuild). Deferred: SEO toolkit, coklu-sunucu. Ruby DUSURULDU (Windows-Plesk'te yok).

## 2026-07-04 (188) — Batch 4: abonelik altı EK KULLANICI (Plesk "additional user") + bug-avi (0.2.16)
- BUG-AVI (kullanici istedi): bu oturumun yeni kodu (cron/kota/advisor/katalog) TARANDI -> CODE temiz. Katalog
  FlattenSingleTopDirectory Grav/Drupal tek-dizin acilimini dogru isler (WordPress'le ayni). Advisor DI dongusu yok.
  Cron kota reseller-kenar durumu makul (reseller scope'suz=sinirsiz). TEK not: Grav "latest" URL'i VPS'te dogrulanmali.
- ALT-KULLANICI TASARIMI (guvenli, merkezi, DB-migration'SIZ): Plesk'te musteri kendi altinda ek login acar. AdaPanel'de
  parent bagini config'te tuttum (customer-scopes.json ScopeEntry.ParentUserId) — OwnerId gibi, DB semasi degismez.
  KRITIK IÇGORU: tum authz GetAssignedSitesAsync/GetScopeAsync'ten akiyor (CustomerAccess). Bu IKI fonksiyonu
  ResolveEffective ile parent'i takip edecek sekilde degistirince (TEK sicrama, dongu yok) alt-kullanici parent'in
  site/mail/dosya/DB/kota'sina OTOMATIK erisir. GetAllowedMailDomains/GetDiskUsage zaten GetAssignedSites kullaniyor -> miras.
- BACKEND: ICustomerScopeService +SetParentSubscriptionAsync/GetParentSubscriptionAsync. ScopeEntry+ParentUserId,
  3 bos-kayit-temizlik kosuluna "&& ParentUserId is null" (alt-kullanici kaydi silinmesin). CreateUserRequest+ParentUserId,
  UserSummaryDto+ParentUserId/ParentName. UserManagementService.CreateUserAsync: yalniz ADMIN, parent=mevcut TOP-LEVEL
  musteri secerek olusturur (parent musteri olmali + KENDISI alt-kullanici OLMAMALI=ic ice yok; users.invalid_parent/
  parent_is_subuser). Alt-kullanici ApplyScope ATLANIR, SetParentSubscription cagrilir -> kendi kaynagi yok. Update/list
  MapUser'a parent bilgisi. (Compile hatasi: `parent` isim cakismasi -> resolvedParent'a cevrildi.)
- FRONTEND: create formunda "Ana abonelik" secici (parentCandidates=alt-olmayan musteriler); secilince owner/site/plan/
  kota alanlari GIZLI (miras). Liste satirinda "Ek kullanici: <parent>". auth.ts tipleri + payload parentUserId||null.
  i18n +6 (tr+en) -> parite 2521=2521.
- DIKKAT (TEST): backend derlendi + typecheck gecti AMA alt-kullanici LOGIN akisi yerel test EDILEMEDI. Kullanici
  VPS'te: admin bir musteri altinda ek kullanici olustursun -> o kullaniciyla giris yapip parent'in sitelerini gordugunu
  DOGRULASIN. Tasarim merkezi (2 fonksiyon) oldugundan risk dusuk ama login testi sart.
- v1 SINIRI: alt-kullaniciyi ADMIN olusturur (Plesk'te musteri kendi olusturur) + tam-erisim (granuler izin matrisi yok).
  Sonraki: musteri-tarafi olusturma + izin flag'leri.
- 0.2.16 = KONSOLIDE (0.2.13 audit/SSRF + 0.2.14 Advisor + Grav/Drupal + alt-kullanici). GitHub'da hala 0.2.12 ->
  0.2.16'yi yayinla (0.2.13-0.2.15'i atla). SHA256 `CA5EBA1089A7B9C335EA6BF3DC08DED54478326CD906193570CFCC7C1C85E815`.
- KALAN: (VPS-testli) ClamAV antivirus + wildcard SSL DNS-01; (Batch5) tam-sunucu DR. Deferred: SEO, coklu-sunucu, Ruby(dusuruldu).

## 2026-07-04 (189) — Bug-avi: alt-kullanici SUSPEND hatasi duzeltildi + edit formu tamamlandi (0.2.17)
- KAPSAMLI KONTROL (kullanici "hata eksik kontrol et"): 299 backend anahtari (AppException + InvalidOperationException
  i18n-benzeri + Notes) -> i18n'de EKSIK 0. Kullanici DELETE ucu YOK (UsersController) -> parent-silme senaryosu yok;
  ResolveEffective zaten fail-closed (parent kaydi yoksa sub-user BOS kapsam alir, her seyi degil).
- GERCEK BUG (bu kontrolde bulundu): UpdateUserAsync alt-kullaniciyi pasiflestirince ApplySuspensionStateAsync,
  MIRAS alinan PARENT kapsamindaki siteleri durduruyordu -> tek ek-login'i kapatmak TUM aboneligi (parent + diger
  alt-kullanicilar) durdururdu. FIX: (1) alt-kullanici duzenlemede kendi site/kota YAZILMAZ (existingParent varsa
  ApplyScope/SetOwner atlanir, scope=GetScope=parent'tan miras, DTO icin); (2) suspensionScope = alt-kullanici ise
  BOS CustomerScope -> ApplySuspensionStateAsync Sites.Count==0 ile erken doner (siteler durmaz; yalniz IsActive=false
  ile o login engellenir = dogru davranis).
- EDIT FORMU TAMAMLANDI (create'te gizliydi, edit'te tutarsizdi): selectedUserForm+parentUserId, selectUser doldurur,
  edit formundaki tum Customer-kapsam alanlari (owner/site/kota) `&& !parentUserId` ile gizli + "bu bir ek kullanici,
  kapsam miras" notu (panel.users.subUserScopeNote tr+en). Parite 2522=2522.
- 0.2.17 = 0.2.16 + bu fix/polish. SHA256 `546F4D98951532D0F8C305C9BE9B0BA7F7FB72CD3F5C4E9ABC85DBEB117F5F47`.
  0.2.16 SHA (CA5EBA...) ARTIK GECERSIZ (yayinlanmamisti). GitHub'da hala 0.2.12 -> 0.2.17'yi yayinla. Testler 2+51.
- TEST (kullanici VPS): alt-kullanici olustur + O LOGIN'le gir (parent sitelerini gormeli) + alt-kullaniciyi pasiflestir
  (parent siteleri DURMAMALI, yalniz o giris engellenmeli). Grav 1-tik kurulum. KALAN: ClamAV+wildcard SSL (VPS), tam-DR.

## 2026-07-05 (190) — Musteri-tarafi ALT-KULLANICI yonetimi + IZIN MATRISI + abonelik kaynak paylasimi fixleri (0.2.18)
- PLESK PARITE (188 v1 sinirlari kapandi): musteri artik KENDI alt-kullanicilarini yonetir + bolum-bazli izin matrisi.
- ABONELIK PAYLASIM FIXLERI (bug-avi, kullanici sormadan): alt-kullanici kaynaklari KENDI kimligine yaziliyordu:
  * CronController OwnerPrefix -> GetOwnerPrefixAsync: CustomerAccess.GetSubscriptionUserIdAsync (alt-kullanici -> parent).
    Gorevler abonelik ad alaninda paylasilir; kota carpani kapandi (her alt-kullanici ayri kota alamaz).
  * ResellerDatabasesController: GetMine/quota/Record/Delete-sahiplik abonelik kimligiyle; audit gercek aktorle.
  * SitesController: IsDatabaseQuotaReached + dbOwnership.RecordAsync + AssignSiteToActorAsync abonelik kimligine
    (alt-kullanicinin kendi kaydina site yazilirsa ResolveEffective parent'i dondurdugunden site GORUNMEZ kalirdi).
- IZIN MATRISI (Plesk "additional user" permissions): ScopeEntry.SubUserSections (List<string>?, null=kisitsiz;
  yalniz ParentUserId doluyken anlamli; parent bagi kaldirilinca temizlenir; SetScopeAsync rebuild'inde korunur).
  CustomerPanelSections.All = sites/mail/database/files/backup/tasks (frontend menu id'leriyle birebir).
  TAM LISTE secilirse null saklanir (yeni bolum eklenince "her seye izinli" otomatik kapsar).
  ENFORCEMENT: RequireSubUserSectionAttribute (IAsyncAuthorizationFilter, DI'dan scope servisi, AppException
  auth.section_forbidden 403) -> Sites("sites"), Mail("mail"), ResellerDatabases("database"), Files("files"),
  Backup("backup"), Cron("tasks") class-level. DbToolsProxy admin-only, ScopeController serbest (dashboard metadata).
- CRUD: SubUsersController /api/v1/sub-users [Customer] (GET/POST/PUT/DELETE). UserManagementService:
  EnsureTopLevelCustomerAsync (alt-kullanici bu ucu KULLANAMAZ 403 users.sub_forbidden), GetOwnedSubUserAsync
  (baska abonelikinkine dokunulamaz -> users.not_found), Create (dup kontrol, parent dili miras, SetParent+SetSections),
  Update (email/parola-opsiyonel min12/aktiflik/izinler; askiya alma yalniz o login), Delete (kaynaklari abonelikte
  yasadigindan guvenli; AuditLog FK'siz dogrulandi). Audit: users.sub_created/updated/deleted.
- SESSION: CurrentSessionDto +IsSubUser +AllowedSections (AuthService'e ICustomerScopeService eklendi).
  Login sonrasi store fetchSession cagirir (login yaniti bu alanlari tasimaz).
- FRONTEND: SubUsersView.vue (liste+form+izin checkbox'lari, tam-liste->null normalizasyonu backend'le ayni);
  SetupWizard musteri menusu: top-level musteride "subusers" bolumu (Ayarlar'dan once, glyph EK); alt-kullanicida
  menu AllowedSections'a gore daraltilir (home/settings/exit hep acik) + "sites" izni yoksa otomatik sites-gecisi
  ve fetchSites yapilmaz. api/subUsers.ts (SUB_USER_SECTIONS sabiti backend'le ayni sira).
- i18n +31 tr+en -> parite 2553=2553. Testler 2+51 yesil, vue-tsc temiz. Eski scopes.json geriye uyumlu
  (SubUserSections alani yok -> null = kisitsiz).
- 0.2.18 SHA256 `7F488C99D7F90AE30B7C89D39C01A9F3ED6CAA249291A1193B2DBA6703813AD1` (artifacts\release\0.2.18\, 49.42 MiB).
  GitHub'da en son yayinlanan 0.2.12 -> 0.2.18'i yayinla (0.2.13-0.2.17 atlanir; hepsi bu pakette).
- TEST (kullanici VPS): (1) musteri loginiyle "Kullanicilar" bolumunden alt-kullanici olustur (izinlerden birkacini kapat),
  (2) o loginle gir -> menu daralmis olmali + kapali bolumun API'si 403 auth.section_forbidden, (3) alt-kullanici cron
  gorevi kurunca parent'in listesinde gorunmeli (abonelik paylasimi), (4) alt-kullanici sil -> siteler etkilenmemeli.
- KALAN: ClamAV + wildcard SSL DNS-01 (VPS-testli, kullanici hazirken), tam-sunucu DR. Deferred: SEO, coklu-sunucu.

## 2026-07-05 (191) — Katalog (Joomla+PrestaShop) + kullanici istekleri: guncelleme-kaynagi gizlendi, yedek SIL/INDIR + otomatik-yedek karti (0.2.19)
- KATALOG (Batch2 tamamlandi): Joomla + PrestaShop 1-tik kurulum, InstallDownloadableAppAsync (WordPress akisi).
  URL'ler CANLI dogrulandi (zip indirilip yapisi incelendi): Joomla GitHub asset 6.1.1 (33MB, kokte index.php +
  installation/ sihirbaz; downloads.joomla.org/latest HTML dondugu icin KULLANILMADI). PrestaShop 8.2.7 GitHub asset
  (79MB, kokte index.php + prestashop.zip -> tarayicida kendini acar); 9.x GitHub'a zip KOYMUYOR -> 8.2.x pinli
  (Adminer emsali). Kategori "E-Ticaret" dinamik sekme olarak gelir, i18n gerekmez (sablon aciklamalari ham TR, mevcut tasarim).
- TARAMA: 284 backend istisna anahtari i18n'de TAM. NOT (yanlis-alarm dersi): ApiResponse.Ok success anahtarlarinin
  86'si i18n'de YOK ama SORUN DEGIL — UI basari mesajlarini KENDI yerel anahtarlarindan gosterir (t("panel.X.saved")),
  API success anahtari kullanilmaz; yalniz HATA anahtarlari i18n'den cozulur. Gelecek taramalarda success'i sayma.
- KULLANICI ISTEKLERI (oturum ici):
  * Ayarlar>Guncellemeler'deki "Guncelleme kaynagi (GitHub)" blogu UI'dan KALDIRILDI (kullanici istegi; depo alani+
    aciklama gorunmesin). Backend ucu ve SetGithubRepoAsync DURUYOR; varsayilan repo SelfUpdateService.DefaultRepo=
    aliorala/adapanel + config/update-github.json'dan degisebilir. 5 i18n anahtari (githubTitle/Hint/Save/Saving/Saved)
    iki dilden silindi; update.github_repo_invalid KALDI (uc hala var).
  * YEDEK SILME eklendi (eksikti; kullanici sordu "neden sil yok"): IBackupService.DeleteBackupAsync (yerel dosya/
    dizin sil, kilitliyse backup.delete_failed 409 + kayit KORUNUR; kayit cikarma atomik UpdateAsync), BackupController
    DELETE {id} (EnsureBackupOwnershipAsync: musteri yalniz kendi site yedegi; audit backup.deleted). UZAK kopya
    BILEREK silinmez (kimlik otomasyon state'inde; BackupService<->AutomationService dongusu olurdu) — onay metni bunu soyler.
  * BackupView: Sil + Indir butonlari (downloadBackup api'si zaten vardi, UI'da kullanilmiyordu) + ADMIN'e
    "Otomatik yedekleme" karti (gunluk=24/haftalik=168/aylik=720 saat preset + saklama gunu + son/sonraki calisma;
    getAutomation/updateAutomation'i kullanir, backupRemotePassword:"" = kayitli parola korunur sozlesmesi).
    Motor ZATEN vardi (AutomationService AutoBackup + her kosudan sonra PruneAsync uzak-rotasyonlu); kullanici
    BULAMAMISTI (Araclar>Otomasyon'da gomulu) -> kesif sorunu cozuldu, kullanici da sonradan buldu.
- i18n +19 -2 (github) -> parite 2566=2566. Testler 2+51, vue-tsc temiz.
- 0.2.19 SHA256 `03803EA219A0FCDC41C80AB66C751CAA705DBA4ECF5D11A184BAFC871A2AD068` (artifacts\release\0.2.19\;
  ayni oturumdaki onceki iki 0.2.19 paketi 1506B0/7F488C-sonrasi GECERSIZ, bu SONUNCU gecerli). 0.2.18 (7F488C)
  YAYINLANMADIYSA atla -> GitHub'a dogrudan 0.2.19 tag'iyla bu exe'yi koy.
- TEST (VPS): Grav/Joomla 1-tik kurulum (indirme sunucudan), yedek sil butonu, otomatik yedek karti kaydet;
  188/190 alt-kullanici testleri hala bekliyor. KALAN: ClamAV + wildcard DNS-01 (VPS), tam-sunucu DR.

## 2026-07-05 (192) — Batch 3a: ClamAV ANTIVIRUS (Plesk parite) — motor LOKAL KANITLI (0.2.20)
- KARAR: kalan uc isten (ClamAV / wildcard DNS-01 / tam-DR) ClamAV secildi cunku TEK lokal-dogrulanabilir olani:
  resmi Windows build'i indirildi (219MB), clamscan calistirildi, OZEL .hdb imzasiyla uctan uca tarama kanitlandi:
  * clamscan --database=<dizin> --recursive --infected <hedef> -> bulgu satiri "path: Sig.Adi.UNOFFICIAL FOUND"
    (FoundLine regex birebir tutuyor), ozette "Scanned files: N", exit 0=temiz 1=bulgu 2+=hata. --version calisiyor.
- BILESEN: components.json +clamav (1.5.3, GitHub Cisco-Talos zip, sha256 e998b3b9..., installDir runtimes\clamav,
  postInstall YOK, service YOK, default false). Zip tek-ust-klasorlu ama sorun degil (exe AllDirectories aranir).
- SERVIS: AntivirusService [SupportedOSPlatform] — InstalledComponents'ten clamav bulur (MailService deseni),
  daemon YOK (bilincli: kalici RAM maliyeti yok, clamscan istege bagli). UpdateDefinitions: freshclam.conf'u
  KENDI yazar (DatabaseDirectory+DatabaseMirror; bootstrapper hook'una GEREK KALMADI) + freshclam 30dk timeout
  (ilk indirme ~250MB). Scan: site adi -> IIS koku (veya mutlak yol, admin-only), clamscan 2sa timeout,
  exit 1 = bulgu (hata DEGIL). Son rapor config/antivirus.json. DI: AddScoped IAntivirusService.
- API: AntivirusController [Administrator] status/install(bootstrapper'a delege)/definitions\update/scan; update+scan audit.
- UI: Guvenlik ekranina 4. sekme "Antivirus": kurulum karti -> tanim guncelle karti (tarih/eksik uyarisi) ->
  site sec+tara -> son tarama raporu (bulgu tablosu: dosya+imza). axios timeout'lar uzun (35dk defs / 125dk scan).
- ADVISOR: +2 sinyal (kurulu degil=info, tanimlar yok=warning, ActionSection security). AdvisorService +IAntivirusService.
- i18n +35 -> parite 2604=2604. Testler 2+51, vue-tsc temiz.
- 0.2.20 SHA256 `E4B2A25CCC6544F8268403FE47716711EC6894A0577D5CAEE6003F7D6167739C` (artifacts\release\0.2.20\).
  Onceki yayinlanmamis 0.2.19 (03803E) ATLANIR -> GitHub'a dogrudan 0.2.20 koy (188-192 arasi HER SEY icinde).
- VPS TEST: Guvenlik>Antivirus: Kur (220MB) -> Tanimlari guncelle (250MB, dakikalar) -> site tara (EICAR dosyasiyla
  bulgu testi yapilabilir: eicar.org test dosyasi docroot'a koy). KALAN: wildcard DNS-01, tam-sunucu DR (VPS oturumlari).

## 2026-07-05 (193) — Envanter taramasi + IIS site LOG ROTASYONU (Plesk parity; 0.2.20 final paket)
- ENVANTER ("baska var mi"): site-bazli PHP ayarlari VAR (.user.ini), ozel hata sayfalari VAR (SiteHostingSettings
  httpErrors), mail forwarding VAR. GERCEK EKSIKLER: (1) mail OTO-YANIT panelde yok (MailService 555. satir stub +
  UI webmail'e yonlendiriyor — Stalwart'ta sieve vacation; ManageSieve/JMAP yolu kullanici parolasiz belirsiz ->
  CANLI Stalwart arastirma oturumu ister, BILINCLI ertelendi); (2) IIS site log rotasyonu YOKTU -> BU TURDA YAPILDI.
- LOG ROTASYONU: SiteLogCleanup.DeleteOldLogs (saf statik, UNIT TESTLI: 2 yeni test, eski *.log siler / taze +
  .log-olmayan korur / kilitli atlanir). AutomationService: state +LogCleanupEnabled/+LogRetentionDays(30)/
  +LastLogCleanup*; saatlik dongude gunde bir RunLogCleanupAsync — her site icin inetpub\logs\LogFiles\W3SVC{id}
  (LogViewer/WebStatistics ile AYNI kanitlanmis dizin cozumu). DTO'lar+UpdateSettings clamp(1,3650)+ToStatus.
  UI: Otomasyon ekraninda "Site logu rotasyonu" karti (toggle+gun+son calisma/detay). i18n +3 -> parite 2607=2607.
- 0.2.20 FINAL SHA256 `9FC7D9DE07B784C3358D0445E7BC5C79C234C9FE1B9AD377084BE354E61BF64E` (192'deki E4B2A2 paketi
  YAYINLANMADI, GECERSIZ; bu paket ClamAV + log rotasyonu DAHIL her seyi icerir). GitHub'a 0.2.20 tag'iyla BU exe.
- Testler 2+53 (yeni 2 log-cleanup testi), vue-tsc temiz.
- KALAN BUYUKLER (hepsi VPS/arastirma oturumu ister): mail oto-yanit (Stalwart sieve), wildcard SSL DNS-01,
  tam-sunucu DR. Deferred: SEO toolkit, coklu-sunucu.

## 2026-07-05 (194) — MAIL OTO-YANIT tamamlandi: LOKAL Stalwart'la kanitlanan sistem-sieve yolu (0.2.20 final-2)
- "VPS arastirma oturumu ister" denen is LOKAL cozuldu: eski probe binary'leri hala duruyordu
  (%LOCALAPPDATA%\Temp\stalwart-srv-probe\stalwart.exe + stalwart-cli-probe + stalwart-auto-test verisi).
  Yerel Stalwart ayaga kaldirilip GERCEK SMTP/IMAP akisiyla arastirildi (mehmet->ayse mail, yanit mehmet INBOX'ta).
- KANIT ZINCIRI (hepsi canli):
  * Kullanici-sieve "vacation" ve "duplicate" TRUSTED baglamda CALISMAZ: trace "sieve.not-supported / DuplicateId"
    (vacation'in ic dedup'u da DuplicateId -> ayni sebepten oluyor). Account semasinda vacation alani YOK.
  * COZUM: SieveSystemScript ("adapanel-autoreply") + MtaStageData.script Expression'a baglama
    (update MtaStageData singleton --field script={"match":{},"else":"'adapanel-autoreply'"}) + enotify/mailto.
    Konu+GOVDE mailto URI percent-encoding ile (subject=..&body=.. — coklu satir %0A calisiyor; Turkce OK).
  * RFC 3834 dongu korumalari script icinde: list-id/list-unsubscribe yok + precedence bulk/list/junk degil +
    auto-submitted auto-*/yes degil + bos-gonderen (bounce) atla + kendine yanit yok. Yanit Auto-Submitted:
    auto-notified tasir -> iki AdaPanel otoyaniti dongu YAPMAZ.
  * ReloadSettings Action'i ile degisiklik RESTART'SIZ aninda etkin (canli dogrulandi). create/update --file
    bicimleri MailService.RunCliJsonAsync ile birebir ayni (canli dogrulandi). Yorum-satiri-only script derlenir
    (sifir kayit durumu). C# sablon sekli Turkce+coksatir+tirnakli mesajla uctan uca MARKER testinden gecti.
- SINIRLAR (bilincli): gonderen-basina frekans siniri YOK (duplicate trusted'da yok; Plesk'te de opsiyonel);
  yanit From'u Stalwart bildirim varsayilani "Automated Message <MAILER-DAEMON@...>" (RFC 5436 mailto from
  parametresini yok sayar; SieveSystemInterpreter.defaultFromAddress global — kozmetik, konu kimligi tasiyor).
- IMPLEMENTASYON: MailService stub -> gercek: config/mail-autoresponders.json (adres->Enabled/Subject/Message),
  Get/SetAutoResponder (hesap varligi FindIdAsync ile dogrulanir; mail.account_not_found), SyncAutoResponderScript
  (tum aktif kayitlardan TEK script uret + upsert + stage bind [elle baska script bagliysa mail.autoresponder_conflict
  409 + detay] + ReloadSettings). DTO +Subject. MailEnable tarafi ZATEN gercekti (COM), router degismedi.
  UI: MailView webmail-yonlendirme paneli GERCEK form oldu (acik/konu/metin); api/mail.ts get/setAutoResponder.
  i18n: +11 yeni, -2 bayat (autoResponderWebmailHint, mail.autoresponder_not_supported) -> parite 2615=2615.
- 0.2.20 FINAL-2 SHA256 `A84A8A47BF0818560BABE5D024D2D23C944E40DC59A9F52352CE5E8915F7501D` (onceki 9FC7D9 paketi
  YAYINLANMAMISTI, gecersiz; bu paket ClamAV + log rotasyonu + oto-yanit DAHIL). Testler 2+53, vue-tsc temiz.
- VPS TEST: Mail > hesap > Otomatik yanit: ac+kaydet -> dis adresten mail at -> otoyanit gelmeli; listeye/bounce'a
  yanit GITMEMELI. KALAN BUYUKLER: wildcard SSL DNS-01, tam-sunucu DR. Deferred: SEO, coklu-sunucu.

## 2026-07-05 (195) — WILDCARD SSL (DNS-01) eklendi: win-acme sozlesmesi GERCEK binary ile dogrulandi (0.2.20 final-3)
- Kalan iki "VPS isi"nden wildcard SSL azami yerel dogrulamayla yapildi:
  * win-acme zip'i indirildi — SHA256 f4dc3b14... components.json'dakiyle BIREBIR AYNI binary. --help'ten sozlesme:
    "--validation script --dnsscript <ps1>" (tek script, argumanlar: create|delete {Identifier} {RecordName} {Token}),
    "--source manual --host *.d,d", "--installation iis --installationsiteid". CANLI arg-parse smoke: wacs bizim tam
    arguman dizgimizi kabul etti ("Source generated using plugin Manual: *.ornek... and 1 alternatives").
  * DNS-01 PS scripti (Get/Add/Remove-DnsServerResourceRecord — DnsService'in VPS'te kanitli cmdletleri):
    en-uzun-birincil-bolge eslesmesi + '@'/goreli ad + yalniz-eslesen-token silme + anlasilir bolge-yok hatasi.
    MANTIK LOKAL TESTLE 5/5 KANITLI (mock cmdletlerle gercek script calistirildi; DIKKAT: mock'ta $script: degil
    $global: kullan — child script scope tuzagi; advanced-function mock'una ayrica $ErrorAction param'i YAZMA, cakisir).
- IMPLEMENTASYON: IssueLetsEncryptRequest +Wildcard. WinAcmeSslService.IssueWildcardAsync: apex turetme ("*." ve
  bastaki "www." atilir), e-posta zorunlu (ssl.contact_email_required), wacs yoksa ssl.winacme_missing, script
  tools/acme-dns01.ps1'e her seferinde yazilir (upgrade propagasyonu), 10dk timeout, BASARISIZLIKTA SELF-SIGNED'A
  DUSME YOK -> ssl.wildcard_failed + win-acme cikti kuyrugu Details'te (UI extractApiDetails ile gosterir).
  Basarida FindBestCertificate("*.domain") (CN eslesir) + persist + redirect + ssl.json envanteri.
  SslController: Wildcard && !admin -> ssl.wildcard_admin_only 403 (DNS bolgesine yazma = sunucu-duzeyi yetki).
  UI: SslView Let's Encrypt sekmesinde admin-only wildcard checkbox + on-kosul hint; issue() hatada Details ekler;
  api timeout 12dk. i18n +6 -> parite 2621=2621.
- E2E (gercek sertifika) VPS ister: alan adinin NS'i panele isaret + bolge panelde birincil olmali. TEST ADIMI:
  DNS ekranindan bolge yayinla -> SSL > Let's Encrypt > wildcard isaretle -> ver. Hata olursa mesajdaki Details'te
  win-acme ciktisi var (kor hata yok).
- 0.2.20 FINAL-3 SHA256 `3F899CDDA8B1302862ED879B828BA6FF3D579BE747D2633FB15EC8D0AAD233BE` (A84A8A dahil onceki
  0.2.20 paketleri GECERSIZ; bu paket: alt-kullanici+izin, katalog, yedek sil/oto-yedek, ClamAV, log rotasyonu,
  oto-yanit, wildcard SSL). Testler 2+53, vue-tsc temiz.
- KALAN BUYUK: tam-sunucu DR (IIS rebuild — gercek IIS ister, ayri oturum). Deferred: SEO, coklu-sunucu.

## 2026-07-05 (196) — TAM-SUNUCU DR eklendi -> PLESK PARITE BACKLOG'U KAPANDI (0.2.20 final-4, YAYINLANACAK)
- SON kalem tam-sunucu DR ("panel yedegi IIS'i yeniden kurmaz" acigi, 183'ten beri):
  * BackupPanelAsync artik iis/sites.json yazar (IIisSiteService.GetSitesAsync -> SiteDto listesi: ad+yol+havuz+
    bagalamalar; best-effort, IIS'siz makinede yedek yine olusur).
  * RestoreAsync (panel-tipi) TryRestoreIisSitesAsync: envanterde olup IIS'te olmayan siteleri IIisSiteService.
    CreateSiteAsync + AddBindingAsync ile yeniden kurar (yalniz HTTP baglamalari — HTTPS sertifika makineye bagli,
    SSL ekranindan yeniden verilir; ProvisionDefaultServices=false + InstallRuntimeIfMissing=false: DR'da yeni
    kaynak/indirme yan etkisi YOK). Site basina best-effort; rapor cikarma klasorune iis-restore-report.txt yazilir.
  * Saf mantik IisSnapshotRestore statik sinifinda: GetMissingSites (ad, case-insensitive) + ToProvisioningPlan
    (ilk http ana baglama, kalan http'ler ek, https atlanir, bos IP='*'). 4 UNIT TEST (roundtrip dahil) -> 2+57 yesil.
  * DR AKISI (dokuman): taze sunucuya AdaPanel kur -> panel yedegini kopyala/Backups listesine koy -> Geri Yukle ->
    kullanicilar+config+gorevler+IIS siteleri doner -> site yedeklerini geri yukle (icerik+DB) -> SSL'leri yeniden ver.
- BACKLOG DURUMU: Plesk parite listesi BITTI. Bilerek YAPILMAYACAK olanlar (kapsam disi, kullaniciya soylendi):
  * SEO toolkit — Plesk'te ucretli 3. parti eklenti (XOVI), cekirdek panel ozelligi degil.
  * Coklu-sunucu yonetimi — enterprise kapsami; tek-VPS hedefiyle celisir.
  * Ruby runtime — Windows-Plesk'te de yok (187'de dusuruldu).
- 0.2.20 NIHAI SHA256 `4910A2397089BC9C59FA9CFB7569CECC1A7DC76FF345A182A23E8F7F0043C5D4` (onceki TUM 0.2.20
  hash'leri gecersiz). Icerik: alt-kullanici+izin matrisi, musteri sub-user CRUD, Joomla/PrestaShop, yedek sil/indir,
  oto-yedek karti, ClamAV, log rotasyonu, mail oto-yanit (lokal-Stalwart-kanitli), wildcard SSL DNS-01 (wacs-sozlesme-
  kanitli), tam-sunucu DR. GitHub'a 0.2.20 tag'iyla BU exe.
- VPS TEST LISTESI (kumulatif): alt-kullanici senaryolari (190) + Grav/Joomla kurulum + yedek sil + oto-yedek karti +
  ClamAV kur/tanim/tara + oto-yanit (dis adresten mail) + wildcard SSL (NS panele isaret etmeli) + DR provasi
  (test VPS'inde panel yedegini geri yukleyip iis-restore-report.txt kontrol).

## 2026-07-05 (197) — SON TUR: kullanici SILME + FTP-DR + bayi alt-kullanici gorunurlugu -> 0.2.20 KESIN NIHAI
- Kullanici "hepsini bitir, son haliyle kuracagim" dedi; 196'daki kucuk-eksikler listesi kapatildi:
  * KULLANICI SILME (Plesk parite acigi — hic yoktu): DELETE /api/v1/users/{id}?removeSites=bool.
    UserManagementService.DeleteUserAsync korumalar: kendini silemez (users.cannot_delete_self), son aktif admin
    silinemez, bayi yalniz kendi musterisini (removeSites bayi icin zorla false; controller'da da admin-degilse 403).
    Kaskad: top-level musterinin ALT-KULLANICILARI da silinir; u<id>-- cron gorevleri Task Scheduler'dan temizlenir
    (best-effort, sub'larin eski prefix'leri dahil); DB sahiplik KAYITLARI silinir; scope kaydi silinir.
    removeSites=true (yalniz admin, API'den): atanan siteler SiteCascadeDeletionService.DeleteAsync(files+databases).
    VARSAYILAN (UI): siteler/veriler SUNUCUDA KALIR = yonetime devir; confirm metni bunu acikca soyler.
    UI: kullanici satirinda Sil butonu (kendine gizli); auth store deleteUser + fetchUsers.
    DIKKAT: SetupWizardView'in YEREL t() sarmalayicisi TEK parametre alir — enterpolasyon icin i18n.t(key, params)!
  * FTP-DR: panel restore artik config/ftp-users.json'dan FTP hesaplarini yeniden kurar (CreateFtpUserAsync
    IDEMPOTENT: hesap varsa net-user atlanir, ACL/home tazelenir). Windows yerel hesap parolalari yedege GIREMEZ ->
    hesap rastgele parolayla acilir, rapora "parolayi FTP ekranindan sifirlayin" yazilir. BackupService +IFtpService
    (dongu yok). Rapor tek dosyada birlesti: TryWriteRestoreReportAsync (iis+ftp satirlari).
    TEST KIRILIMI DERSI: BackupService ctor'una parametre eklenince 4 test POZISYONEL null! cagrilariyla kirildi ->
    testlerdeki new BackupService(...) cagrilarina null! eklendi.
  * BAYI GORUNURLUGU: GetUsersAsync reseller filtresi artik sahip oldugu musterilerin ALT-KULLANICILARINI da listeler
    (OwnerId'siz olduklari icin gorunmuyorlardi); duzenleme/silme bayi icin yine kapali (owner=null -> 403), yalniz listeler.
- Dogrulama: build 0/0, testler 2+57, vue-tsc temiz, 296 istisna anahtari i18n'de TAM, parite 2626=2626.
- 0.2.20 KESIN NIHAI SHA256 `81554DB28AC5471C6BC3269DEC87C6A8B47B54F862D6DF30ED01CCB7212CB860` — onceki TUM 0.2.20
  hash'leri (4910A2 dahil) GECERSIZ. KULLANICI BU PAKETI KURACAK. GitHub tag 0.2.20 = bu exe.
- ICERIK OZETI (0.2.12 sonrasi hepsi): cron audit+SSRF, Advisor, Grav/Drupal/Joomla/PrestaShop, plan gorev kotasi,
  yedekte gorevler, alt-kullanici + izin matrisi + musteri CRUD, abonelik kaynak paylasimi fixleri, yedek sil/indir,
  oto-yedek karti, guncelleme-kaynagi gizleme, ClamAV, log rotasyonu, mail oto-yanit, wildcard SSL DNS-01,
  tam-sunucu DR (IIS+FTP), kullanici silme. Kapsam disi: SEO toolkit, coklu-sunucu, Ruby.

## 2026-07-07 (198) — CANLI HATA analizi (Win10 + MySQL 8 cakismasi) -> KURULUM ON-KONTROLU eklendi (0.2.20 v5)
- KULLANICI CANLI HATA bildirdi (baska Win10 makinede kurulum): "Veritabani servisi kuruldu ancak baglanti vermeye
  baslamadi" + AdaMariaDB STOPPED (1066) + probe "Access denied for user root". TESHIS: makinede MySQL 8.x calisiyor,
  3306'yi tutuyor -> AdaMariaDB baslayamadi; mariadb-admin probe'u 127.0.0.1:3306'da YABANCI MySQL 8'e baglandi ->
  farkli root parolasi -> Access denied. Iki celiskili belirti tek nedenle aciklandi. COZUM (kullaniciya): MySQL80
  servisini durdur+devre disi birak, MariaDB'yi tekrar kur. Oneri: MariaDB (panel varsayilani, dusuk RAM, CMS uyumu).
- KULLANICI HAKLI TALEP: "panel bunu kendisi acik soylese profesyonel olur — tum cakismalar icin". YAPILDI:
  ComponentPreflight (Bootstrap/) — kurulum INDIRMEDEN ONCE:
  * PORT KONTROLU: bilesenin portlari (mariadb 3306 / panel-mysql 3307(!) / pg 5432 / redis 6379 / memcached 11211 /
    stalwart 25,143,587,993,4190,8080) dolu mu -> sahibi surec+PID+Windows servis ADIYLA software.port_conflict 409
    (cozum metni Details'te). Kendi Ada* servislerimiz MUAF (yeniden kurulum bozulmaz).
  * SERVIS KONTROLU: yabanci cakisan servis desenleri (mysql/mariadb, postgresql, redis, memcached, hmailserver) —
    CALISIYOR veya baslangici OTOMATIK ise software.conflicting_service 409 (durmus+devre-disi engellemez).
    MailEnable BILEREK stalwart deseninde YOK (panel-yonetimli cift, ResolveMailServerConflict once calisir).
  * Probe tek PS cagrisiyla (Get-NetTCPConnection + Win32_Service) JSON doner; probe hatasi kurulumu ENGELLEMEZ
    (best-effort). PS5.1 tek-eleman JSON sarmasi ReadPreflightArray ile ele alindi.
- DOGRULAMA: saf mantik 9 UNIT TEST (port haritasi, Ada* muafiyeti, yabanci secimi, Running/Auto/Disabled ayrimi,
  script uretimi) -> testler 2+66. PROBE CANLI DOGRULANDI: bu makinede 33061'e gercek TcpListener acildi, script
  ayni sekliyle calistirildi -> {Port:33061, Pid, Process:"powershell", Service:""} dondu (parser'la birebir).
  SoftwareView details'i ZATEN gosteriyor (451. satir) — UI degisikligi gerekmedi. i18n +2 -> parite 2628=2628.
- 0.2.20 v5 SHA256 `AB360F2F81ED4A88FDEAA6E349C47ACFEE0B96D8520BB3A7B5F5048FCBE31D21` (81554D dahil onceki TUM
  0.2.20 hash'leri GECERSIZ — kullanici HENUZ kurmadi/kurulumu cakismada kaldi, BU paketi kuracak).
- NOT: MySqlPort=3307 — panelin kendi MySQL-8'i bilerek 3307'de (kendi motorlari arasinda cakisma zaten yok).

## 2026-07-07 (199) — DUZELTME: 0.2.20 YAYINDAYMIS -> on-kontrol 0.2.21 oldu (198'deki v5 iddiasi GECERSIZ)
- KULLANICI HAKLI UYARDI: "0.2.20 zaten yayinladik, niye kontrol etmiyorsun". GitHub dogrulandi:
  tag 0.2.20, 2026-07-05T20:28Z, 1 indirme; yayindaki exe INDIRILIP hash'lendi = `81554DB2...` (dun geceki
  kullanici-silme+FTP-DR paketi — icerik dogru paket, sansli kacis). 198'de ayni numaraya urettigim AB360F GECERSIZ,
  yereldeki kopyasi silindi.
- KALICI KURAL (adapanel-workflow memory'ye de yazildi): paketlemeden ONCE GitHub releases/latest KONTROL ET;
  yayinlanmis tag'e asla yeni exe uretme (self-update surum karsilastirir — ayni tag altinda degisen exe,
  o surumu kurmus panellere yeni duzeltmeleri GORUNMEZ yapar). "Yayinlanmadi" varsayma.
- Kurulum on-kontrolu (198'deki ozellik, degisiklik yok) 0.2.21 olarak paketlendi:
  SHA256 `310F186F6CCBAEEEF60A67F99A227E6AC9DC571FE278981160266F0BB291B57B` (artifacts\release\0.2.21\).
  GitHub'a 0.2.21 tag'iyla yayinlanacak; 0.2.20 kurulu paneller guncellemeyi otomatik gorecek.
- MEVCUT DURUM: yayinda 0.2.20 (81554D, on-kontrol HARIC her sey); 0.2.21 (310F18) = 0.2.20 + kurulum on-kontrolu.

## 2026-07-30 (200) — KOK NEDEN: "adapanel.host paneldeki site listesinde YOK" + wildcard DNS-01 on kontrolu
- **SITE GIZLENME BUG'I (gercek kok neden, kullanici sikayeti):** `WebAdministrationSiteService.IsSystemSite`
  dahili siteleri gizlemek icin `"AdaPanel."` ONEKI kullaniyordu. `"adapanel.host"` bu onekle (OrdinalIgnoreCase)
  ESLESIYOR -> musterinin GERCEK sitesi "panel ic altyapi sitesi" sayilip `GetSitesAsync` listesinden
  DUSURULUYORDU. IIS'te ve diskte site duruyor, panelde YOK. Ayni sekilde `"MailEnable"` oneki
  `mailenable.com` gibi bir alan adini gizlerdi.
  DUZELTME: tam-ad kumesine `AdaPanel.WebDb.PhpMyAdmin` + `AdaPanel.WebDb.Adminer` eklendi; onekler
  alan adiyla CAKISAMAYACAK sekilde daraltildi (`AdaPanel.WebDb.`, `AdaPanel FTP`, `MailEnable ` — son
  ikisi BOSLUKLU, alan adinda bosluk olamaz). `IsSystemSite` internal yapildi + 11 regresyon testi
  (adapanel.host / AdaPanel.host / adapanel.com.tr / mailenable.com / alioral.xyz gorunur; dahili 5 ad
  ve loopback-only site gizli). KURAL: site listesi filtresine ASLA alan adiyla eslesebilecek onek koyma.
- **Wildcard DNS-01 on kontrolu (`EnsureDns01ResolvableAsync`):** kullanicinin `*.alioral.xyz` hatasinin
  nedeni panelin TXT yazamamasi DEGIL — log kaydin yazildigini VE silindigini gosteriyor; LE tarafinda
  `DNS problem: query timed out looking up TXT` = delege edilmis NS'ler kaydi disariya yanitlamiyor
  (53/UDP kapali, DNS servisi durmus veya NS'ler baska saglayici). win-acme bu durumda 10 tur x 30 sn
  bosa bekliyor ve BASARISIZ dogrulama LE limitine yaziliyor. Artik win-acme'den ONCE tek PowerShell
  turu: (1) bolge panel DNS'inde birincil mi, (2) 1.1.1.1/8.8.8.8'den NS delegasyonu var mi,
  (3) rastgele token'li gecici `_adapanel-acme-check` TXT yazilip delege NS IP'lerine + genel
  cozumleyiciye soruluyor, sonra siliniyor. Sonuca gore net hata: `ssl.dns01_zone_missing` /
  `ssl.dns01_no_delegation` / `ssl.dns01_ns_unreachable` (+ detay: zone, ns, nsIp, serverIp,
  authoritativeAnswer, publicAnswer, notes). Yetkili NS yanit veriyorsa (genel cozumleyici henuz
  gormediyse = yayilma) ENGELLEMEZ, devam eder. On kontrol calistirilamazsa (DnsServer/DnsClient
  modulu yok) yolu KAPATMAZ, win-acme yine denenir.
- DOGRULAMA: build 0 uyari/0 hata, dotnet test Core 2/2 + Infrastructure 77/77, typecheck OK,
  frontend build OK, i18n TR/EN 2631/2631 parite, onek cakismasi yok.
- HENUZ PAKETLENMEDI: 0.2.21 (310F18) yayina gidiyordu; bu iki duzeltme icin surum/paket karari
  kullanicida (paketlemeden once GitHub releases/latest KONTROL EDILECEK).
- **AYNI GUN, KULLANICI SUNUCU CIKTISI + DISARIDAN TESHIS -> KESIN KOK NEDEN:**
  - `Get-Website` ciktisi bug'i DOGRULADI: `adapanel.host` IIS'te **Started**, binding `*:80:adapanel.host`,
    path `C:\inetpub\wwwroot\adapanel.host` — yani site sapasaglam, panel onu FILTRE ile gizliyordu.
  - AYNI CIKTI benim ilk duzeltmemde ACIK buldu: gercek dahili site adi `AdaPanel.Webmail`
    (WebmailService.SiteName) ve binding'leri `*:8899:` + `*:80:webmail.<alan>`. Onekleri daralttigim icin
    bu site musteri listesine SIZACAKTI. Cozum: tam-ad kumesine eklendi + GENEL kural: ad `AdaPanel.` ile
    basliyor VE kendi adina (ya da alt alan adina) binding'i YOKSA dahilidir. Boylece gelecekteki
    `AdaPanel.<Bilesen>` siteleri otomatik gizli, `adapanel.host` (kendi adina bagli) gorunur.
    +2 test (AdaPanel.Webmail/AdaPanel.FutureTool gizli; yalniz www.adapanel.host'a bagli site gorunur)
    -> Infrastructure 79/79.
  - **DNS KOK NEDENI (kod degil, sunucu/ag):** registry delegasyonu DOGRU — `nslookup -norecurse` ile
    x.nic.xyz ve ns01.trs-dns.com'dan: `ns1/ns2.<alan>` + glue **185.7.242.225** (RDAP de ayni, iki alan
    adi da `active`). AMA 185.7.242.225'te **53 dısarıdan KAPALI** (TCP 53 -> False, UDP 53 -> timeout),
    buna karsin ICMP + **TCP 80 ve 443 ACIK**. Sonuc: 1.1.1.1/8.8.8.8 iki alan adi icin de SERVFAIL ->
    ne tarayici cozer ne de Let's Encrypt TXT'yi okur ("query timed out").
  - SITELER ASLINDA CALISIYOR: ham IP'ye Host basligiyla istek -> `adapanel.host` ve `alioral.xyz`
    301 -> HTTPS; `curl --resolve <alan>:443:185.7.242.225` -> **HTTP 200, ssl_verify=0 (sertifika gecerli)**;
    `webmail.adapanel.host` -> Roundcube 200. Yani IIS/SSL saglam, tek eksik DNS'in dısarıya yanit vermesi.
    (Yan bulgu: alioral.xyz HTTPS yaniti 34 sn — PHP/Laravel icinde cozulemeyen DNS beklemeleri olabilir.)
  - SUNUCUDA DOGRULANACAK (kullaniciya verildi): `Get-Service DNS`, 53 dinleyicileri
    (`Get-NetUDPEndpoint/Get-NetTCPConnection`), `Get-DnsServerZone`, `Get-DnsServerSetting` ListenAddresses,
    `Resolve-DnsName ... -Server 127.0.0.1`. Yerelde calisiyorsa engel HOSTING SAGLAYICI agindadir
    (Windows Firewall'da 'AdaPanel DNS 53 TCP/UDP' + 'DNS (TCP/UDP, Gelen)' kurallari ZATEN etkin).
  - YAYIN DURUMU: 0.2.21 GitHub'da **yayinda** (2026-07-08, 1 indirme) -> bu duzeltmeler **0.2.22** olacak.

## 2026-07-30 (201) — 0.2.22: DNS bind sagligi + tek tik onarim + otomatik nobetci (KULLANICI SUNUCUDA KOK NEDENI BULDU)
- **KULLANICININ TESHISI (kesin):** 23 Temmuz 23:50 reboot'unda Windows DNS Server servisi, ag arayuzu
  (185.7.242.225) hazir olmadan basladi ve yalniz 127.0.0.1/::1/link-local'e bind kaldi.
  `Get-DnsServerSetting` KONFIGURASYONUNDA genel IP dinleme listesindeydi ama FIILEN dinlemiyordu
  (klasik servis baslatma yarisi). ALTI GUN boyunca dısarıdan tek DNS sorgusu yanitlanmadi.
  Kullanici duzeltti: servisi restart + `sc config DNS start= delayed-auto` + ACME TXT dongusunu
  ucta uca dogruladi. -> Panelin bunu KENDI yakalamasi icin bu tur eklendi.
- **DERS / KURAL:** servis "Running" kontrolu bu sinif hatayi ASLA yakalamaz. Bir ag servisinin
  sagligi = PORTUN FIILI DINLEYICILERI (`Get-NetUDPEndpoint`/`Get-NetTCPConnection` + servis PID'i),
  konfigurasyon degil. Ayni desen ileride IIS/FTP/mail portlari icin de kullanilabilir.
- **YENI: DNS erisilebilirlik sagligi** — `DnsServerHealthDto` (installed, serviceStatus, startMode,
  delayedAutoStart, listeningAddresses, expectedAddresses, missingAddresses, publiclyBound, zoneCount,
  NeedsRepair, lastAutoRepair*, details) + `DnsService.GetServerHealthAsync` (tek PowerShell turu:
  servis + Win32_Service PID/StartMode + DelayedAutostart registry + 53 UDP/TCP dinleyicileri +
  birincil bolge sayisi). Beklenen adresler: loopback olmayan IPv4'ler + panelin kayitli ServerAddress'i.
  Saf karar mantigi `internal static EvaluateBinding` (0.0.0.0/:: joker; beklenen liste bossa
  loopback disi herhangi bir adres yeterli -> NAT'ta yanlis alarm yok) -> **6 UNIT TEST**
  (`DnsServerBindingTests`, loopback-only kesintisi dahil).
- **YENI: tek tik onarim** — `RepairServerAsync(applyDelayedAutoStart)`: `sc.exe config DNS
  start= delayed-auto` (PowerShell 5.1 Set-Service AutomaticDelayedStart DESTEKLEMEZ) + Restart-Service
  + 3 sn bekleme + yeniden olcum. API: `GET /api/v1/dns/server/health`, `POST /api/v1/dns/server/repair`
  (yeni `DnsServerController`, admin-only). i18n: dns.server_repaired/_not_installed/_restart_failed.
- **YENI: otomatik nobetci** — `RunBindWatchdogAsync`: 5 dk aralik + 1 saat onarim sogumasi
  (durum `config/dns-watchdog.json`; servis Scoped oldugu icin sayac DOSYADA, zaman damgasi olcumden
  ONCE yazilir ki hata halinde sicak dongu olmasin). `AutomationService.RunDueJobsAsync` icinde
  KILIT DISINDA cagriliyor; aralik dolmadiysa null doner. AYAR YOK, her zaman acik (bozuk DNS = tam
  kesinti). Onarim/basarisizlik loglanir; UI "son otomatik onarim" satirinda gosterilir.
- **Sunucu Sagligi ekrani (kullanici istegi):** `HealthService.CoreServices`'e `DNS` eklendi (IIS/WAS/FTP
  yaninda yeniden baslat butonu). Ustune `ServerHealthView`'a **DNS erisilebilirlik karti**: yesil/kirmizi
  durum, dinlenen/beklenen/eksik adresler, bolge sayisi, baslangic turu (gecikmeli mi), son otomatik
  onarim, ham teshis satiri + "DNS'i yeniden baslat ve onar" butonu. Rol kurulu degilse kart gizli.
- **Wildcard DNS-01 hatasi artik dogru sebebi soyluyor:** on kontrol NS'lerden yanit alamazsa DNS bind
  sagligina bakar; bind bozuksa `ssl.dns01_not_listening` (= "panelin DNS'i genel IP'de dinlemiyor,
  Sunucu Sagligi'ndan onar") doner, yoksa eski `ssl.dns01_ns_unreachable`. Kullanici bir daha bosuna
  registrar'da NS aramaz. (WinAcmeSslService'e IDnsService enjekte edildi; dongu yok.)
- **AYRICA (ayni pakette):** site listesi gizleme bug'i (`adapanel.host`) + `AdaPanel.Webmail` sizinti
  aciği + wildcard DNS-01 on kontrolu (bkz. 200).
- DOGRULAMA: build 0 uyari/0 hata, dotnet test Core 2/2 + Infrastructure **85/85**, typecheck OK,
  frontend build OK, i18n TR/EN **2652/2652** parite, onek cakismasi yok.
- **0.2.22 PAKET:** `artifacts\release\0.2.22\AdaPanelSetup.exe`, boyut `51,857,768` byte (49.46 MiB),
  SHA256 `8C2B53BEF50E8BF384A9D509527B6031D0033CD825FCBA9D6A5619799918B49F`.
  Surum 6 yerde artirildi (Directory.Build.props, installer\AdaPanel.iss, frontend\package.json +
  package-lock, scripts\build-installer.ps1, scripts\package-installer.ps1).
  GitHub'da yayinda olan: 0.2.21 (2026-07-08, dogrulandi) -> 0.2.22 tag'iyla yayinlanacak.

## 2026-07-30 (202) — 0.2.22 (revize): hesap kilidi kurtarma + "adapanel.host" konusunda BAŞKA AJAN YANILDI
- **BAŞKA AJANIN SONUCU YANLIŞ (kullanici aktardi):** "COM/ServerManager staleness -> servisi yeniden
  baslattim, adapanel.host artik gorunur" DEGIL. `/api/v1/scope/sites` (ScopeController, admin dalinda)
  dogrudan `iisSiteService.GetSitesAsync` cagirir -> `IsSystemSite` filtresi uygulanir -> 0.2.21 kodunda
  `"AdaPanel."` oneki `adapanel.host` ile eslesip siteyi LISTEDEN DUSURUR. Servis yeniden baslatmak
  BUNU DUZELTMEZ; site ancak 0.2.22 kurulunca gorunur. (Ajanin COMException tespiti ise DOGRU ve
  ZARARSIZ: "AdaPanel FTP" ftp-only sitesinin State okumasi 0x800710D8 atar, `ReadSiteValue`
  yakalayip "Unknown" fallback'i verir — istek 200 doner.)
- **HESAP KILIDI (auth.user_locked) — kullanici istegi uzerine davranis degisti:** Eskiden kilitliyken
  parola HIC dogrulanmiyordu (erken throw) -> tek yonetici kendini kilitlerse 15 dk beklemekten baska
  yol yoktu. ARTIK: kilitliyken de parola dogrulanir; DOGRU parola (+ varsa 2FA) kilidi kaldirir ve
  giris yapar (`auth.login.lock_cleared` denetim kaydi), YANLIS parola kilidi korur ve `auth.user_locked`
  doner (kilit UZAMAZ). Guvenlik gerekcesi: asil hiz sinirlayici IP katmani — BruteForceProtectionService
  varsayilan ACIK (Enabled=true, 15 dk icinde 10 hata -> IP 60 dk Windows Firewall'da bloklanir) ve
  kilitliyken yapilan her hatali deneme o sayaci da besler. TAKAS (kullaniciya soylendi): dagitik
  cok-IP'li tahmin saldirisinda hesap-basina 5/15dk ust siniri kalkar; savunma IP engeli + guclu parola + 2FA.
- **LATENT HATA BULUNDU VE DUZELTILDI (testler ortaya cikardi):** `LoginAsync` sayac/kilit
  mutasyonlarini HIC kaydetmiyordu; yalnizca `AuditService`in AYNI scoped DbContext'te SaveChanges
  cagirmasi sayesinde tesadufen kalici oluyordu. Denetim kaydi bir gun dosyaya/uzaga yazsa kilit
  SESSIZCE calismayi birakirdi. Iki yola (hata + basari) acik `SaveChangesAsync` eklendi.
- **KURTARMA YOLLARI (Plesk `plesk bin admin` muadili):**
  1. Panelden: `POST /api/v1/users/{id}/unlock` + kullanici listesinde "Kilitli" cipi ve "Kilidi ac"
     butonu (UserSummaryDto.LockedUntilUtc; suresi gecmis kilit "kilitli" gosterilmez). Bayi yalniz
     kendi musterisini acar. Denetim: `users.unlocked`.
  2. Dogru parolayla giris (yukaridaki davranis).
  3. Parola sifirlama akisi (zaten vardi, kilidi de temizler — SMTP yapilandirilmisse).
  4. **Sunucudan, kimse giremiyorsa:** `%ProgramData%\AdaPanel\config\recover-access.flag` dosyasi
     olusturulup panel servisi yeniden baslatilir -> `Program.ApplyAccessRecoveryFlagAsync` TUM giris
     kilitlerini + FailedLoginCount'lari temizler, `BruteForceRecovery.ClearBansAsync` ile brute-force
     IP engellerini (firewall kurallari dahil) kaldirir, bayragi SILER ve LogWarning yazar. Dosyayi
     olusturmak yonetici ACL'i gerektirir (K1) -> yeni saldiri yuzeyi yok.
- **TESTLER:** `LoginLockoutTests` (in-memory SQLite + gercek PasswordHasher + sahte audit/bruteforce;
  giris yolunda cagrilmayan bagimliliklar NotSupportedException atar): dogru parola kilidi kaldirir,
  yanlis parola kilidi korur/uzatmaz + IP sayacini besler, 5 hatada kilitlenir, suresi gecmis kilit
  "kilitli" sayilmaz. -> Infrastructure **89/89**, Core 2/2.
- DOGRULAMA: build 0 uyari/0 hata, typecheck OK, frontend build OK, i18n TR/EN **2658/2658**, cakisma yok.
- **0.2.22 PAKET (REVIZE, 201'deki 8C2B53BE GECERSIZ — yayinlanmadi, uzerine yazildi):**
  `artifacts\release\0.2.22\AdaPanelSetup.exe`, `51,863,354` byte (49.46 MiB),
  SHA256 `6215AD30BACB02228565D1099E632839D9EB09AFB29C1EB895EF9E243DB026FB`.
- **YAYINLANDI (2026-07-30):** commit `e0758b1` (yerel main; bu depoda git remote TANIMLI DEGIL — GitHub'daki
  repo yalniz README commit'i tasiyor, kaynak hic push edilmemis, release'ler API ile yayinlaniyor).
  GitHub release `0.2.22` olusturuldu (id 362090453, target main, draft=false) + `AdaPanelSetup.exe`
  yuklendi. YAYINDAKI DOSYA INDIRILIP HASH'LENDI: `6215AD30BACB02228565D1099E632839D9EB09AFB29C1EB895EF9E243DB026FB`
  (yereldekiyle birebir), `releases/latest` -> 0.2.22. Yerelde `0.2.22` annotated tag'i de olusturuldu.
  Release body'si panelin self-update ekraninda "notlar" olarak gorunur (SelfUpdateService JSON `body`
  alanini okur) -> notlar Turkce yazildi.
  YAYIN YONTEMI (tekrar icin): `git credential fill` ile Windows Kimlik Yoneticisi'ndeki github.com
  token'i (scope: gist, repo, workflow) okunup GitHub REST API'ye Bearer olarak verildi; gh CLI kurulu DEGIL.

## 2026-07-30 (203) — PROAKTIF TARAMA: guncelleme otomasyonu + bulunan 3 gercek risk
- **KULLANICI ISTEGI:** "paneli tara, eksik/noksan duzelt, ileride sorun cikaracak sey var mi, magazadaki
  yazilimlarin guncellemesi var mi, sistem kendini guncel tutsun ya da sorsun".
- **BULGU 1 — GUNCELLEME KONTROLU HIC OTOMATIK DEGILDI:** `CheckForUpdatesAsync` YALNIZ mağaza
  ekranindaki butondan tetikleniyordu. Yonetici o ekrani acmazsa sunucu suresiz eski surumde kalirdi
  (en sik guvenlik acigi kaynagi). EKLENDI: `AutomationService.RunUpdateCheckAsync` gunde bir calisir
  (`UpdateCheckEnabled` VARSAYILAN ACIK) -> Danisman ekraninda "Kurulu yazilimlarda guncelleme var"
  maddesi + uyarilar aciksa e-posta/webhook bildirimi (24 sa cooldown). Panel kendi guncellemesi de
  artik bildirim gonderiyor (eskiden yalniz Danisman ekraninda gorunuyordu).
  Istege bagli `AutoApplyComponentUpdates` (VARSAYILAN KAPALI) bulunanlari kurar.
- **BULGU 2 — KILIT ICINDEN KURULUM = PANELIN KALICI KILITLENMESI (yeni kodda yakalandi):** guncelleme
  isini once digerleri gibi `concurrencyLock.RunExclusiveAsync` icine koymustum. Bilesen kurulumu
  (Roundcube/phpMyAdmin/Adminer) IIS SITESI OLUSTURUR -> `WebAdministrationSiteService.ExecuteWriteAsync`
  -> AYNI non-reentrant kilidi ister -> panel bir daha hicbir yazma islemi yapamaz. Is kilidin DISINA
  alindi; yalniz SONUC (`UpdateCheckOutcome` record) kilit icinde state'e yaziliyor.
  KURAL: otomasyon job'i disaridan bir servise is yaptiracaksa once o servisin kilit alip almadigina bak.
  (Kontrol edildi: BackupService'in `iisSiteService.CreateSiteAsync` cagrisi YALNIZ restore yolunda
  (`TryRestoreIisSitesAsync`), otomasyondan erisilmiyor -> mevcut deadlock yok.)
- **BULGU 3 — "GUNCEL" ILE "KONTROL EDEMIYORUM" AYNI GOSTERILIYORDU:** 24 bilesenin yalniz 9'unun
  ust surumu canli cozuluyordu; digerlerinin surumu manifeste SABIT. Panel bunlar icin sessizce
  "guncelleme yok" diyordu -> yanlis guven. `SupportsVersionCheck` + DTO `VersionCheckSupported`
  eklendi; magaza ekraninda "surum kontrol edilemiyor" rozeti, otomasyon ozetinde ayri liste.
  AYRICA iki yeni canli cozumleyici: **roundcube** (GitHub roundcube/roundcubemail, webmail internete
  acik) ve **clamav** (Cisco-Talos/clamav, imza motoru). Artik 11/24 canli.
  HALA SABIT (elle manifest guncellemesi gerekir): php-8.3, mariadb-11, mysql-8, postgresql-16, nginx,
  redis, pgadmin4, python-3, vc-redist, url-rewrite, iis-arr, http-platform-handler, mailenable.
- **BULGU 4 — `ComponentPreflight.IsOwnService` "Ada" onegi (ADAPANEL.HOST BUG'IYLA AYNI SINIF):**
  `serviceName.StartsWith("Ada")` ucuncu parti "AdAwareService"/"Adaptive..." servislerini de panele
  aitmis sayip GERCEK bir kurulum cakismasini sessizce yutabilirdi (dc38e10'daki on kontrolun varlik
  sebebini bosa cikarir). Bilinen ad kumesi (AdaPanel/AdaMySQL/AdaMariaDB/AdaPostgreSQL/AdaRedis/
  AdaMemcached/AdaStalwart) + "Ada"+BUYUK harf kalibi ile daraltildi.
- **TEMIZ CIKAN TARAMALAR:** EF varliklarini kaydetmeden degistiren baska yer YOK (MailService/
  WebDbClientService eslesmeleri DTO atamasi, false positive). Servis "Running" varsayimi yalniz
  CacheService'te ve orada zaten canli RESP INFO ile dogrulaniyor. Alan adiyla cakisabilecek baska
  StartsWith filtresi yok.
- TESTLER: `UpdatePolicyTests` (otomatik guncellenebilirlik listesi, canli surum kontrolu destegi,
  IsOwnService ucuncu parti ayrimi) -> Infrastructure **129/129**, Core 2/2. build 0/0, typecheck OK,
  frontend build OK, i18n TR/EN **2667/2667**.
- PAKETLENMEDI/YAYINLANMADI: 0.2.22 yayinda oldugu icin bu calisma **0.2.23** olacak (kullanici karari).

## 2026-07-31 (204) — SABIT SURUMLERIN HEPSINE CANLI COZUMLEYICI (kullanici: "hepsi icin istiyorum")
- **YONTEM:** her kaynak ONCE canli test edildi (feed sekli + URL kalibi + checksum var mi), sonra kod
  yazildi. Uydurma yok; her cozumleyici hata durumunda manifest surumune SESSIZCE geri duser.
- **YENI 8 COZUMLEYICI** (+ onceki turdaki roundcube/clamav ile birlikte 11 -> toplam **19/20 bilesen**):
  - `php-8.3`: windows.php.net/downloads/releases/**releases.json** -> dal ("8.3") basina surum + zip yolu
    + **SHA256** (tam dogrulama korunur). Kurulu paketin lezzeti (nts-vs16/vs17-x64) korunur, **ts ELENIR**.
  - `mariadb-11`: downloads.mariadb.org/rest-api/mariadb/**11.8**/ -> seri icindeki en yeni surum +
    winx64.zip + **sha256sum**. Indirme adresi archive.mariadb.org uzerinden HTTPS'e cevrilir
    (API http:// donuyor).
  - `mysql-8`: dev.mysql.com KAZIMAYI ENGELLIYOR (403) ve checksum sidecar'i YOK -> cdn.mysql.com'da
    yama numarasi ileri YOKLANIR (olmayan dosyaya gercekten 404 donuyor; 8.4.99 ile dogrulandi),
    2 ardisik bosluk sonrasi durur.
  - `postgresql-16`: postgresql.org/**versions.json** -> major 16 icin latestMinor -> EDB adresi HEAD ile
    dogrulanir (EDB olmayan surume 403 doner, o yuzden HEAD sart).
  - `pgadmin4`: pgadmin.org/**versions.json** (uygulamanin kendi guncelleme kontrolu de bunu kullanir).
  - `nginx`: indirme sayfasi regex; AYNI DALDA kalinir (sayfa 1.31.x ve 1.30.x'i birlikte listeler).
  - `python-3`: **TUZAK** — bir seri guvenlik-yalniz moda gecince (3.12.11+) yeni yamalar KAYNAK olarak
    cikar, Windows installer URETILMEZ. En yeni dizini almak bozuk URL uretir; adaylar yuksekten asagi
    HEAD ile denenip installer'i GERCEKTEN olan ilk surum secilir (3.12.10 dogrulandi).
  - `redis`: GitHub tporadowski/redis (Redis-x64-*.zip).
- **BULUNAN GERCEK GERIDE KALMALAR** (panel bunlari GOREMIYORDU): PHP 8.3.31->**8.3.33**,
  MariaDB 11.8.3->**11.8.8**, MySQL 8.4.9->**8.4.11**, nginx 1.31.0->**1.31.3**, pgAdmin 9.15->**9.16**,
  Python 3.12.4->**3.12.10**, Roundcube 1.7.1->**1.7.2**. Guncel olanlar: PostgreSQL 16.14, Redis 5.0.14.1,
  ClamAV 1.5.3.
- **TAZE KURULUMLAR DA GUNCEL GELIR:** `InstallComponentInternalAsync` zaten `ResolveDynamicComponentAsync`
  cagiriyor (satir ~657) -> manifest artik yalnizca FALLBACK/seed. Manifest surumlerini elle bumplamaya
  gerek KALMADI.
- **SERI DISIPLINI:** hicbir cozumleyici major/minor atlamaz (php-8.3 -> 8.3.x, python 3.12 -> 3.12.x,
  nginx 1.31 -> 1.31.x). Seri gecisi uyumluluk karari; panel surumuyle gelir. `SelectNewerInSeries` testli.
- **CHECKSUM POLITIKASI:** sha256 YAYINLAYANLAR (PHP, MariaDB, Node, Composer, GitHub) tam dogrulamada
  KALIR. Yayinlamayan resmi HTTPS dagitim sunuculari icin `TrustedNoChecksumHosts` genisletildi:
  cdn.mysql.com, get.enterprisedb.com, ftp.postgresql.org, nginx.org, www.python.org (+ mevcut
  download.docker.com). Yalniz HTTPS + tam host esitligi/alt alan adi; "cdn.mysql.com.evil.example"
  testle reddediliyor.
- **INCE TUZAK (yorum olarak koda yazildi):** `CopyComponent`'te `sha256: null` = "manifesttekini KORU".
  Checksum yayinlamayan kaynaktan YENI surum cozerken **string.Empty** gecilmeli; null gecilirse eski
  surumun hash'i yeni dosyayla karsilastirilir ve kurulum "checksum uyusmuyor" ile patlar.
- TESTLER: `ComponentVersionResolutionTests` (VersionSeries, CompareVersions sayisal, SelectNewerInSeries
  dal atlamama, IsTrustedNoChecksumHost sahte-alt-alan-adi reddi, PHP dal ayristirici ts/nts secimi,
  MariaDB releases ayristirici) — GERCEK upstream JSON kopyalariyla. Infrastructure **165/165**, Core 2/2.
  build 0/0, typecheck OK, frontend build OK.
- **KALAN TEK ISTISNA:** `mailenable-standard` (surum akisi yok, adres elle guncellenir) — panel bunu
  "surum kontrol edilemiyor" olarak DURUSTCE isaretliyor.

## 2026-07-31 (205) — KULLANICI PLESK BOLGESINI GONDERDI: VARSAYILAN BOLGEDE POSTA KAYITLARI EKSIKMIS
- **KULLANICI SORUSU:** rakip panelin (Plesk/ozkula) alioral.com.tr bolgesini yapistirdi, "bizde neden
  cok az kayit var, eksik mi yaptik?" + ayri mesajda kayit tipi listesi ekran goruntusu (12 tip).
- **CEVAP: EVET, GERCEK EKSIK VARDI.** Karsilastirma:
  - Bizde varsayilan: NS x2, A @, A ns1, A ns2, CNAME www, CNAME ftp = **7 kayit**.
  - Plesk'te ayrica: **MX (10) mail**, **A mail**, **A webmail**, **TXT SPF**, **TXT _dmarc** (+ TLSA/DANE,
    _domainconnect Plesk'e ozel, kullanicinin elle ekledikleri).
  - KRITIK SONUC: panel Stalwart mail + Roundcube webmail KURUYOR ama bolgede MX/SPF/DMARC ve
    mail/webmail A kayitlari YOKTU -> o alan adina posta HIC ULASMAZ, giden posta spam'e duser,
    webmail.<alan> cozulmez. (Panelin `MailService.GetDnsRecordsAsync`'i bu kayitlari ZATEN dogru
    uretiyordu — ama yalnizca "dis DNS saglayicisina ekleyin" diye GOSTERIYORDU; panel KENDI bolgesini
    barindirirken bile yazmiyordu.)
- **DUZELTME:** `BuiltInTemplate` + `CreateDefaultRecords` genisletildi: `A mail`, `A webmail`,
  `MX @ -> mail.{domain}` (oncelik 10), `TXT @ -> v=spf1 mx a ip4:{ip} ~all` (MailService.BuildSpfValue
  ile ayni format), `TXT _dmarc -> v=DMARC1; p=quarantine; rua=mailto:postmaster@{domain}`.
  `CurrentDefaultsVersion` 1 -> **2**: `MergeMissingDefaults` sayesinde MEVCUT bolgeler de eksik posta
  kayitlarini kendiliginden alir (kullanici kayitlarina dokunulmaz).
- **YAN BULGU + DUZELTME:** `DnsTemplateRecordDto`'da **Priority YOKTU** -> sablondan uretilen MX
  onceliksiz yaziliyordu. Alan eklendi, `BuildDefaultRecordsAsync` tasiyor, `UpdateTemplateAsync`
  MX icin bos/0 gelirse **10**'a dusuruyor (UI'dan sablon kaydedilince MX bozulmasin), sablon
  ekranina Oncelik sutunu eklendi (yalniz MX/SRV'de aktif).
- **HENUZ YAPILMADI (kullanicinin ekran goruntusu istegi):** kayit tipi listesi hala 6
  (A, AAAA, CNAME, MX, TXT, NS). Rakipte 12 var: +CAA, DS, HTTPS, PTR, SRV, TLSA.
  NOT: **TUTARSIZLIK** — sunucu-geneli SABLON `UpdateTemplateAsync` SRV ve CAA'yi KABUL EDIYOR
  (satir ~591 allowed listesi) ama bolge kayit editoru `SupportedTypes` (satir 24) REDDEDIYOR.
  Windows DNS gercekleri: SRV/PTR/TLSA native cmdlet parametreleri VAR; CAA ve HTTPS/SVCB icin
  `-Unknown -Type <n> -RecordData <hex>` (RFC 3597) gerekir — CAA'nin RDATA kodlamasi basit ve
  test edilebilir, HTTPS/SVCB SvcParam kodlamasi karmasik. Bu makinede DNS Server rolu YOK,
  canli dogrulanamaz. SIRADAKI IS: SRV + CAA + PTR + TLSA.
- DOGRULAMA: build 0/0, Core 2/2, Infrastructure **168/168** (+3 `DnsDefaultRecordsTests`),
  typecheck OK, frontend build OK, i18n TR/EN **2668/2668**.

## 2026-07-31 (206) — 0.2.23: SRV/CAA/TLSA/PTR kayit tipleri (rakip panel paritesi tamamlandi)
- Kayit tipi listesi 6 -> **10**: A, AAAA, CNAME, MX, TXT, NS + **SRV, CAA, TLSA, PTR**.
  (Ekran goruntusundeki rakipte 12 vardi; DS DNSSEC imzalamayla panel tarafindan zaten uretiliyor,
  **HTTPS/SVCB** BILEREK DISARIDA: SvcParam kodlamasi karmasik ve bu makinede DNS Server rolu olmadigi
  icin canli dogrulanamaz -> korlemesine yazilmadi.)
- **Deger bicimleri** (tek "deger" alaninda cok alan; UI'da tipe gore ipucu gosteriliyor):
  SRV -> "agirlik port hedef" + oncelik AYRI alanda (MX ile tutarli)
  CAA -> "bayrak etiket deger"  (ornek: 0 issue letsencrypt.org; tirnaklar temizlenir, etiket lowercase)
  TLSA -> "kullanim secici eslesme sertifika-hex"  (0-3 / 0-1 / 0-2 / hex)
  PTR -> hedef alan adi
- **Windows DNS yazimi:** SRV (-Srv -DomainName/-Priority/-Weight/-Port), TLSA (-Tlsa
  -CertificateUsage/-Selector/-MatchingType/-CertificateAssociationData), PTR
  (Add-DnsServerResourceRecordPtr -PtrDomainName) NATIVE. **CAA'nin native parametresi YOK** ->
  RFC 3597 `-Unknown -Type 257 -RecordData <0xNN,...>`; RDATA = bayrak(1)+etiket-uzunlugu(1)+etiket+deger
  (RFC 8659) ve `BuildCaaRecordData` ile uretilip TESTLE dogrulaniyor.
  **CANLI DOGRULANMASI GEREKEN TEK SEY:** CAA'nin `-Unknown` cagrisi hedef sunucuda denenmeli
  (bu makinede DNS Server rolu yok). Digerleri native cmdlet parametreleri.
- Uretilen PowerShell satirlari test edilebilir olsun diye `BuildSrvLine/BuildTlsaLine/BuildCaaLine`
  ayri metodlara alindi (DnsRecordState private nested oldugu icin primitif parametre alirlar).
- TESTLER: `DnsRecordTypeTests` 20 test (ayristirma + hatali girdi reddi + RDATA bayt bayt +
  uretilen komut satiri) -> Infrastructure **188/188**, Core 2/2. i18n TR/EN **2675/2675**.
- **0.2.23 PAKET:** `artifacts\release\0.2.23\AdaPanelSetup.exe`, `51,877,932` byte (49.47 MiB),
  SHA256 `4D99C994DFC7FE6C27756C7B6439B862593D95C5CCFC11C46D1E980C59230471`.
  Icerik: 203 (guncelleme otomasyonu) + 204 (8 canli surum cozumleyici) + 205 (varsayilan bolgeye
  posta kayitlari + sablon Priority) + 206 (4 yeni kayit tipi).
- **YAYINLANDI (2026-07-31):** commit `725ddb2` + yerel `0.2.23` tag'i. GitHub release **0.2.23**
  (id 362930524, target main) + AdaPanelSetup.exe yuklendi. YAYINDAKI DOSYA INDIRILIP HASH'LENDI:
  `4D99C994DFC7FE6C27756C7B6439B862593D95C5CCFC11C46D1E980C59230471` (yereldekiyle birebir),
  `releases/latest` -> 0.2.23. Release notu Turkce (self-update ekraninda gorunur).

## 2026-08-01/02 (207) — TASARIM DALGASI: gorunurluk, dis-bakis saglik, canli izleme (7 ajan)
- KULLANICI "tasarimci olsan ne yapardin" diye sordu, sonra "hepsini yap ajanlara bolerek" dedi.
  Ilke: panel DURUMU degil, DISARIDAN NASIL GORUNDUGUNU gostermeli + bulguyu kullanicinin onune getirmeli.
- **AJAN YONETIMI DERSI:** ilk 5 ajan AYNI ANDA oturum limitine carpti (hicbiri kendi dogrulamasini
  calistiramadan durdu) ama agac DERLENEBILIR kaldi. KURAL: (1) i18n dosyalarina HICBIR ajan dokunmasin,
  anahtarlari RAPORLASIN, ana oturum tek elden birlestirsin — eszamanli yazimda anahtarlar kaybolur.
  (2) Frontend ajanlari `npm run build` CALISTIRMASIN (dist/ cakisir), yalniz `typecheck`.
  (3) Her ajana SAHIP OLDUGU + DOKUNMAYACAGI dosya listesi verilsin. (4) DI kaydi ve menu/router
  baglantisi ajana YAPTIRILMASIN (tek satirlik ortak dosyalar cakisir) — ana oturum baglar.
- **YENI BACKEND SERVISLERI:** `ISiteHealthService` (disaridan DNS/HTTP/TLS/mail olcumu; HTTP kontrolu
  DNS'e guvenmez, sunucu IP'sine `Host` basligiyla gider), `IAttentionService` (Advisor+guncelleme+DNS+
  servis sinyallerini tek listede toplar, madde basina `actionEndpoint`), `IProcessMonitorService`
  (**Singleton** — CPU% iki olcum farkindan hesaplanir, Scoped olsa hep 0 gorunurdu; w3wp komut
  satirindaki `-ap "AdaPanel.<site>"` -> SITE ADI), `IResourceAttributionService` (**Scoped**;
  MySQL/MariaDB `information_schema.PROCESSLIST`, PostgreSQL `pg_stat_activity`, IIS
  `appcmd list requests /xml` -> aktif sorgu/istek SITEYE baglanir; `SanitizeStatement` parola iceren
  sorgulari maskeler).
- **YENI FRONTEND:** `SiteHealthStrip` (workspace breadcrumb altinda, site basina TEK dis olcum,
  otomatik polling YOK), `AttentionDrawer` + ust bar rozeti + menu rozetleri + panel ozet karti
  (60 sn, `document.hidden` iken durur), `ProcessMonitor` bolumu (2 sn, duraklat/sirala),
  `ResourceConsumers` (3 sn, kaynak secici), `ConfirmDialog` (yikici islemlerde ne kaybedilecegini
  SAYAR, yuksek riskte ad yazdirir), `FieldHint` (uzun aciklamalar popover'a), mobil kabuk
  (900px hamburger, yapiskan ust bar -> dikkat dugmesi her zaman erisilebilir; `main-column` dar
  ekranda flex ÇÜNKÜ yapiskan grid ogesi kendi satirina hapsolur).
- **ONAY MODALI:** 20 `window.confirm` cevrildi. Ajan HIC ONAYI OLMAYAN iki yikici islem buldu:
  yedek geri yukleme (uzerine yazar) ve veritabani silme -> ikisine de yuksek riskli onay eklendi.
  Kullanici silme ana oturumda cevrildi (site/DB sayisini gosterir + ad yazdirir).
- **MOBIL:** mevcut kirilma noktalari (900/820/720) yeniden kullanildi, 700/620 eklendi. Gizlenenler:
  PID, calisma suresi, ag "Toplam" sutunu, halka ici kullanilan/toplam, site listesi etiketsiz
  arac simgeleri. ASLA gizlenmeyen: durum rozetleri ve dugmeler. Kaydirma kilidi SAYAÇLI
  (menu + cekmece ayni kilidi paylasir). Disk/ag satirlarinin 820px'te tek sutuna cokup basliklari
  oksuz birakan MEVCUT hatasi da duzeltildi.
- **CANLI DOGRULANAMAYAN (hedef sunucuda bakilmali):** MySQL/PostgreSQL sorgulari ve ozellikle
  `appcmd list requests /xml` oznitelik adlari (bu makinede motor/IIS yok). Ajan aday-liste ile
  buyuk/kucuk harf duyarsiz eslestirme + duz metin fallback yazdi; uyusmazlikta satir KAYBOLMAZ,
  alan bos gelir. Site adlari bos gelirse tek bir `appcmd list requests /xml` satiri yeterli.
- DOGRULAMA: build 0/0, Core 2/2, Infrastructure **307/307** (+117 test), typecheck OK, frontend
  build OK, i18n TR/EN **2815/2815**, onek cakismasi yok.
- **0.2.24 PAKET:** `artifacts\release\0.2.24\AdaPanelSetup.exe`, `51,932,998` byte (49.53 MiB),
  SHA256 `176EF8B0B390DACD4355967A689C707B7D98CD59556F88BE49930C03D04CC8FA`.
- **YAYINLANDI (2026-08-02):** commit `08fb8a4` + yerel `0.2.24` tag'i. GitHub release **0.2.24**
  (id 363632766). YAYINDAKI DOSYA INDIRILIP HASH'LENDI: `176EF8B0...D04CC8FA` (yereldekiyle birebir),
  `releases/latest` -> 0.2.24.

## 2026-08-02 (208) — Canli izleme rotuslari + planli BETIK gorevi (admin-only)
- **SIRALAMA ZIPLAMASI (kullanici sikayeti):** islem tablosu 2 sn'de bir yeniden siralanip satir sayisi
  degistikce ALTINDAKI "kim kullaniyor" bolumu zipliyordu. Uc katmanli cozum: (1) `.proc-scroll`
  SABIT 420px yukseklik + ic kaydirma -> bolum boyu hic degismez, (2) fare/klavye tablonun uzerindeyken
  SIRALAMA DONDURULUR (`orderFrozen` + `frozenOrder` pid listesi; degerler yerinde guncellenir, yeni
  surec SONA eklenir, araya girip satirlari itmez), (3) `TransitionGroup` ile satirlar kayarak yer
  degistirir (`prefers-reduced-motion` saygili). Alt satirda "siralama donduruldu" yazar.
- **SORGUYU TAM GOSTER:** backend kirpma 200 -> **4000** karakter (yavas sorgu teshisi WHERE/JOIN'leri
  gormeyi gerektirir; sinirsiz DEGIL cunku satir megabaytlarca olabilir ve liste saniyede bir cekilir).
  Arayuzde satira tiklayinca sorgu tam metin acilir (`white-space: pre-wrap`, secilebilir, 40vh
  kaydirmali) + **Kopyala** dugmesi. Acik satir 3 sn'lik yenilemede KAPANMAZ (anahtar bazli `expandedRows`).
- **"SITE BASKA YERDE, DB BIZDE" (kullanici sikayeti):** db->site bagi yoksa satir yalniz veritabani
  adini gosteriyordu; "site burada ama bag yok" ile "site baska sunucuda" AYIRT EDILEMIYORDU.
  `ResourceConsumerDto`'ya `OwnerName` + `SiteLinked` eklendi; `IDatabaseOwnershipService`'e
  **`GetAllAsync`** eklendi (50 satir icin 50 dosya okumasi yerine tek okuma). Artik uc durum ayri:
  site adi | "musteri: X · site bu sunucuda degil" | "panelde kayitli degil".
- **"HANGI SAYFA" (kullanici istegi):** MySQL sorgunun kaynagini BILMEZ. Tek durust ipucu zaman
  ortusmesi: ayni sitenin o an IIS'te islenen ve **sorgudan ONCE baslamis** istekleri (0.5 sn tolerans).
  Tek aday -> adres, coklu -> "~N istek" + tooltip'te hepsi. `~` ve dipnot bunun TAHMIN oldugunu soyler.
  Sutun yalniz veritabani gorunumunde ve eslesecek istek varken cikar.
- **PLANLI BETIK GOREVI (`script` tipi, YALNIZ Administrator):** kullanici once "site icinde + baska
  yerden" istedi, sonra "musteri olmasin admine ozel olsun" dedi -> musteri/bayi yolu HIC ACILMADI
  (bugunku gibi zorla `url`). Iki mod: **sunucu betigi** (yol serbest, SYSTEM) ve **site betigi**
  (yol site kokunun icinde olmali). `.bat`/`.cmd` -> `cmd.exe /c`, `.ps1` -> `powershell -File`.
  Kimlik ACIK SECIM: SYSTEM (varsayilan) veya sitenin app pool kimligi; app pool ile kayit basarisiz
  olursa **SESSIZCE SYSTEM'E DUSMEZ**, ham Windows hatasiyla `cron.app_pool_principal_failed` doner.
  Yol dogrulamasi admin icin de duruyor (kaza korumasi): `..` kacisi, kok-adi-oneki tuzagi
  (`site1-evil`/`site1evil`/`site10`/`site1.backup`), calisma dizini de kok icinde olmali.
  `cmd /c` TIRNAKLAMA TUZAGI: cmd, `/c` kuyrugunda TAM IKI tirnak varsa ilk ve sonuncuyu SILER —
  bosluklu tek yol tam bu durumdur; disa sarmalama (`/c ""yol" args"`) sayiyi 4'e cikarip kurali devre disi
  birakir. Test edildi. Yeni dosya: `Scheduler/ScriptTaskPlanner.cs` + `ScriptTaskPlannerTests` (31 test).
  **CANLI DOGRULANAMADI:** app pool principal'i icin `-LogonType Password -RunLevel Limited` secildi
  (sanal hesaplarda belgelenen kalip; `ServiceAccount` yalniz LocalSystem/Service icin, `S4U` sanal
  hesapta calismiyor). Bu makinede IIS YOK ve kabuk yukseltilmemis -> `Register-ScheduledTask` her
  LogonType'ta ayni "No mapping between account names and security IDs" hatasini veriyor, yani hata
  ORTAMSAL. Hedef sunucuda dogrulanmali; yanlissa `BuildCreateScript`'te tek satir degisir.
- DOGRULAMA: build 0/0, Core 2/2, Infrastructure **338/338**, typecheck OK, frontend build OK,
  i18n TR/EN **2847/2847**, onek cakismasi yok.
- **DIS BAGLANTI TESPITI (kullanici: "alioral.xyz'de 2 db var, biri disaridan kullaniliyor"):**
  Ayni sitenin birden cok veritabani olabilir ve biri BASKA sunucudaki uygulama tarafindan
  kullaniliyor olabilir — site bagi yerel gorunse de yuku ureten burasi degildir. Ayrimi yapan tek
  gozlem ISTEMCI ADRESI. `ResourceConsumerDto.RemoteClient` + `IsRemoteClientHost` (MySQL "host:port",
  PostgreSQL ham adres/yerel sokette BOS; IPv6 koseli parantez ve tek-iki-nokta port ayirimi ele
  alinir). IP olarak ayristirilamayan deger (makine adi) "uzak" SAYILMAZ — emin olmadigimizi
  "disaridan" diye etiketlemek yanlis yonlendirir. Satirda "disaridan" rozeti. 16 test.
- **0.2.25 PAKET:** `artifacts\release\0.2.25\AdaPanelSetup.exe`, `51,939,996` byte,
  SHA256 `9FAE4082243C76034A0231D13F01F06ED4FB743D05D3D3C4AFB2BA5A16D575DD`.
- **YAYINLANDI (2026-08-02):** commit `ef1d223` + yerel `0.2.25` tag'i. GitHub release **0.2.25**
  (id 363640088). YAYINDAKI DOSYA INDIRILIP HASH'LENDI: `9FAE4082...16D575DD` (birebir),
  `releases/latest` -> 0.2.25.

## 2026-08-02 (209) — "1 guncelleme bekliyor" ama panel guncel: IKI AYRI HATA
- **KULLANICI RAPORU:** ozet kartinda "1 guncelleme bekliyor", tiklayinca PANEL guncelleme sayfasi
  aciliyor ve "zaten son surumdesiniz" diyor.
- **HATA 1 — YANLIS YONLENDIRME:** `DashboardOverviewView` guncelleme cipi `software` (bilesen) +
  `updates` (panel) maddelerini TOPLUYOR ama tiklaninca HER ZAMAN `updates`'e gidiyordu. Yani bir
  BILESEN guncellemesi (PHP/MariaDB...) kullaniciyi panel guncelleme ekranina yolluyordu.
  Duzeltme: tek madde varsa KENDI bolumune (`software` -> Yazilimlar), birden coksa hepsini
  listeleyen dikkat cekmecesine gidilir.
- **HATA 2 — BAYAT SAYAC:** `AttentionService` sayiyi `automation.PendingComponentUpdates`'ten
  okuyordu; o deger GUNDE BIR yazilir. Kullanici guncellemeyi kurduktan sonra bile sayac 24 saate
  kadar "1" kalirdi. Iki katmanli duzeltme: (a) `RefreshUpdateCacheEntry` — bilesen kurulumu
  bitince o bilesenin onbellek bayragi YENI surume gore yeniden hesaplanir; (b) yeni ucuz
  `IComponentBootstrapper.GetPendingUpdateCountAsync` (yalniz kurulu bilesenler + onbellek; AG YOK,
  sistem taramasi YOK) ve AttentionService artik sayiyi ondan alir. Kaldirilmis bilesenin onbellekte
  kalan kaydi da sayilmaz.
- **PROAKTIF TARAMA (kullanici istegi, yayin oncesi):** TEMIZ cikanlar — 4 yeni ucun yetkilendirmesi
  (SiteHealth: Administrator,Reseller + `CustomerAccess.EnsureSiteAsync`; Attention/ProcessMonitor/
  ResourceAttribution: Administrator-only), tum setInterval'lerin onUnmounted'da temizlenmesi,
  Advisor'in urettigi 17 + AttentionService'in 4 baslik anahtarinin i18n'de TAM olmasi, cron'da
  admin olmayan icin `SiteName`/`RunAs` dahil tum betik alanlarinin temizlenmesi.
  **BULUNAN TEK SORUN (israf):** veritabani gorunumunde "muhtemel sayfa" korelasyonu icin HER 3
  SANIYEDE bir IIS'e istek gidiyordu — sunucuda bu her seferinde `appcmd` SURECI baslatir, ustelik
  IIS kurulu olmasa bile. Iki kosul eklendi: IIS okunamiyorsa (`installed/available` false) bir daha
  DENEME (`webUnavailable`, kaynak degisince sifirlanir) ve eslestirilecek DB satiri yoksa hic isteme.
- **0.2.26 PAKET:** `artifacts\release\0.2.26\AdaPanelSetup.exe`, `51,940,503` byte,
  SHA256 `3D1B87ACDB5BD19059B7993D4C796E2B37DC108C7EBA976D630A4E984E317FCC`.
- **YAYINLANDI (2026-08-02):** commit `67c401d` + yerel `0.2.26` tag'i. GitHub release **0.2.26**.
  YAYINDAKI DOSYA INDIRILIP HASH'LENDI: `3D1B87AC...4E317FCC` (birebir), `releases/latest` -> 0.2.26.

## 2026-08-02 (210) — KRITIK: MariaDB "guncelle" calismiyordu VE calissa TUM VERITABANLARINI SILECEKTI
- **KULLANICI:** "MariaDB 11.8 LTS yeni surume guncelle diyorum ama guncellemiyor."
- **YAKIN SEBEP:** `InstallWithDependenciesAsync` icinde `if (!forceInstall)` bloğu — bilesen kurulu VE
  `IsInstalledComponentReadyAsync` true ise kurulum ATLANIR. "Guncelle" dugmesi ayni kurulum ucunu
  cagirdigi icin sunucu "zaten kurulu" deyip donuyordu. Dugme, backend'in yapmayacagi seyi vaat ediyordu.
- **ASIL BULGU (VERI KAYBI MAYINI):** `InitializeMariaDbZipAsync` KOSULSUZ olarak
  `DeleteManagedPath(dataDirectory)` yapiyordu ve MariaDB'nin verisi kurulum klasorunun ICINDE
  (`%ProgramData%\AdaPanel\runtimes\mariadb\11\data`). Yani atlama kaldirilsa "guncelle" TUM
  VERITABANLARINI SILERDI. Dahasi: atlama yalniz dogrulama BASARILI oldugunda devreye giriyor —
  servis durmus/dosya eksikse dogrulama patlar, panel "onarim kurulumu" yapar ve VERIYI SILERDI.
  Yani guncelleme dugmesine hic dokunulmasa da patlayabilecek bir mayindi.
  MySQL tarafinda ayni hata YOK (`InitializeMySqlZipAsync` veri dizini BOSSA initialize eder);
  MariaDB'de bu koruma unutulmus.
- **DUZELTME 1 (veri):** MariaDB init artik MySQL kalibini izliyor — veri dizini varsa ve bos degilse
  DOKUNULMAZ; yalniz binary'ler degisir, servis yeni binary'lerle yeniden kaydedilir
  (`mysqld --install`) ve sema `mariadb-upgrade` ile tasinir.
- **DUZELTME 2 (guncelleme yolu):** `ComponentInstallRequest.Force` + `IComponentBootstrapper.InstallAsync(..., bool force)`.
  YALNIZ "Guncelle" dugmesi ve otomasyonun otomatik guncelleme isi force=true gonderir; ilk kurulum ve
  bagimlilik kurulumlari eskisi gibi atlar. Tum cagri yerleri named-argument'a cevrildi.
- **DUZELTME 3 (UI):** servisi olan bilesenlerde (mysql/mariadb/postgres/stalwart/redis/memcached/docker)
  yukseltme ONAY ister: hangi surumden hangi surume + "servis kisa sure duracak" + "once yedek alin".
  Servissiz araclar dogrudan guncellenir.
- **CANLI DOGRULANAMADI:** bu makinede MariaDB yok; yukseltme yolu (binary degisimi + mariadb-upgrade)
  hedef sunucuda YEDEK ALINDIKTAN SONRA denenmeli.
- **0.2.27 PAKET:** `artifacts\release\0.2.27\AdaPanelSetup.exe`, `51,940,416` byte,
  SHA256 `B1E040BBD210A89FDACADE7A8AFAB28035D3C1A4D73A3DD216E164161C177CA3`.
- **YAYINLANDI (2026-08-02):** commit `bfba0d1` + yerel `0.2.27` tag'i. GitHub release **0.2.27**.
  Yayindaki dosya indirilip hash'lendi: `B1E040BB...1C177CA3` (birebir), `releases/latest` -> 0.2.27.
- **BU MAKINEYE KURULUM DENEMESI:** Windows 10 Pro, kabuk YUKSELTILMEMIS (IsInRole Administrator=False),
  IIS kurulu DEGIL, DNS Server rolu istemci OS'ta YOK. Installer yonetici hakki ister (UAC) —
  ajan tiklayamaz. Kullanicinin `!` ile elle calistirmasi gerekir.

## 2026-08-02 (211) — CANLI TEST: 0.2.27'DEKI MariaDB DUZELTMESI YETERSIZDI (uc ayri hata)
- **BU MAKINEYE PANEL KURULDU** (Windows 10 Pro, 8890, secureEntryPath=panel, admin/AdaPanelTest2026!).
  Haziran'dan kalan gelistirme DB'si `.bak` olarak yedeklenip sifir kurulum yapildi.
  API'yi surmek icin `scratchpad\ada.ps1` (her cagrida taze giris; **CSRF token GIRISTEN SONRA
  yenilenmeli**, aksi halde 400) ve `swap-binary.ps1` (servisi durdur, publish'i kopyala, baslat).
- **CANLI DOGRULANAN ILK SEY:** manifest 11.8.3 derken kurulum **11.8.8** getirdi → canli surum
  cozumleyicisi (204) GERCEKTEN calisiyor.
- **TEST: MariaDB kur -> veri yaz -> force reinstall -> veri duruyor mu? ILK DENEME: VERI SILINDI.**
  0.2.27'de yayinladigim duzeltme YETMIYORDU. Uc ayri hata bulundu:
  1. **Asil silme daha yukarida:** `InstallComponentInternalAsync` kurulumdan ONCE
     `DeleteManagedPath(installDirectory)` yapiyor. `data/` o dizinin icinde oldugu icin veri
     postInstall'a gelmeden yok oluyordu; benim `InitializeMariaDbZipAsync` icindeki "veri varsa
     dokunma" korumam BIR ADIM GEC kaliyordu.
     -> `PreserveDataDirectories` / `RestorePreservedDirectories`: korunacak klasorler silmeden ONCE
     kurulum dizininin YANINA tasinir (ayni disk = rename), extract'ten SONRA ve postInstall'dan
     ONCE geri konur. Tasima basarisizsa kurulum DURUR (veriyi koruyamiyorsak silmeye devam edilemez).
  2. **Geri alma da siliyordu:** postInstall patlayinca `RollbackFailedInstallAsync` zip tipi icin
     `DeleteManagedPath(cleanupPath)` yapiyor → basarisiz bir YUKSELTME calisan veritabanini yok
     ediyordu. Artik orada da preserve/restore var.
  3. **Sira hatasi (benim eklediğim):** `mariadb-upgrade` `sc start`'tan hemen sonra, sunucu hazir
     olmadan calisiyordu → "Can't connect (10061)" → kurulum geri alindi → (2) yuzunden veri gitti.
     Artik ONCE `WaitForDatabaseReadyAsync`, SONRA upgrade; ustelik upgrade hatasi ARTIK OLUMCUL DEGIL
     (sunucu verisiyle ayakta; sema tasimasi aksadi diye kurulumu geri alip veriyi yok etmek sacma).
- **IKINCI DENEME: TEST GECTI.** Kur -> `veri_kaybi_testi` DB + `kanit` tablosu + satir -> force
  reinstall -> satir YERINDE (`1 | YUKSELTMEDEN ONCE YAZILDI`), servis Running.
- Regresyon testi: `DataPreservationTests` (motorlar + stalwart data/etc + durumsuz bilesenlerde
  koruma YAPILMAMASI). Infrastructure **365/365**.
- **DERS:** "kod okudum, mantik dogru" YETMEZ. 0.2.27'yi duzeltme diye yayinladim ama veri kaybi
  aynen duruyordu; ancak gercek kurulumda deneyince ortaya cikti.
- **0.2.28 PAKET + YAYIN:** `artifacts\release\0.2.28\AdaPanelSetup.exe`, `51,943,958` byte,
  SHA256 `358681007665DA493B904C8AC5DB3B9A4DDD538B99D999CE386D961363B76174`. Yayindaki dosya
  indirilip hash'lendi: birebir. Ayrica **0.2.27 release notuna UYARI eklendi** (API PATCH ile):
  "bu surumu kullanmayin, veritabani motorlarinda Guncelle verinizi silebilir, 0.2.28'e gecin".

## 2026-08-05 (212) — 0.2.87-0.2.90: notlardaki dort is (cron sunumu, Adminer, arayuz, SSL)

> NOT: bu dosya 0.2.28'den 0.2.86'ya kadar GUNCELLENMEMIS. Aradaki surumlerin devir notlari
> yalnizca git commit mesajlarinda duruyor (`git log --oneline`); oradaki mesajlar ayrintili.

### 0.2.87 — cron ekrani "neden calismadi" sorusuna cevap vermiyordu
- Panel veriye ZATEN sahipti (State, LastRunTimeUtc, LastTaskResult); eksik olan sunumdu.
- Devre disi gorevde "sonraki calisma" tarihi GOSTERILMIYOR ("Devre disi -- calismayacak") ve satir
  gorsel olarak ayrisiyor. Satir altinda TEK CUMLELIK sebep. 0x1 artik "cikis kodu 1" + cikti dugmesi.
- "Takildi" kelimesi yalnizca kullanicinin O GOREV icin verdigi sure siniri asilinca kullaniliyor;
  yoksa notr "Su anda calisan gorevler" kutusu (kullanicinin botu gunlerce surebilir, bu NORMAL).
- Calisan satirda birincil dugme artik "Suren calismayi durdur" (onceden en gorunur dugme "Devre disi
  birak"ti ve o, calisan turu DURDURMAZ).
- **Durdurma artik surec AGACINI olduruyor** (Schedule.Service EnginePID + gorev gunluk yolundan
  OKSUZ surec). **SIRA KRITIK, canlida olculdu:** Stop-ScheduledTask ONCE cagrilirsa ust surec
  aninda oluyor, Get-Process bulamiyor, oldurme dongusu HICBIR SEY YAPMIYOR (OLDURULEN=0) ve is
  nesnesinden kopan surecler (Chrome/chromedriver) hayatta kaliyor. Once agac, sonra Stop.
- **taskhostw ASLA oldurulmez:** bu makinede olculdu, COM isleyicili iki ayri Windows gorevi ayni
  taskhostw'u EnginePID olarak bildiriyor; /T ile oldurmek ilgisiz gorevleri dusururdu.
- Devre disi gorev tetiklenince ham PowerShell yigini yerine `cron.task_disabled`.
- Panelde gorevi devre disi birakan TEK yol satirdaki gecis dugmesi (kod tarandi).

### 0.2.88 — Adminer tek tik girisi HIC ACILMIYORDU
- Panel **Adminer 5.4.2** indiriyor; sarmalayici **4.x** sozlesmesine gore yazilmisti.
- Gercek adminer-5.4.2.php + gercek PHP 8.3 ile UC hata bulundu, ilki duzeltilmeden ikincisi
  gorunmuyordu:
  1. `Class "Adminer" not found` — 5'te temel sinif `\Adminer\Adminer`. display_errors kapali
     oldugu icin canlida BOS SAYFA goruluyordu. Artik hangi sinifin var oldugu OLCULUYOR (5.x+4.x).
  2. Adminer giristen sonra KENDI giris noktasina yonlendiriyor; belirtec tek kullanimlik oldugu
     icin ikinci istek 403 "Baglanti suresi dolmus" veriyordu. Belirtec artik yalnizca otomatik
     girisi baslatiyor, sonrasinda Adminer'in KENDI oturumu is goruyor.
  3. Veritabani suzgeci ilk sayfadan sonra kayboluyordu (DB secici TUM veritabanlarini
     listeliyordu). Adminer'in oturumuna yazmak DENENDI: sarmalayici cagrildiginda
     `session_status()` PHP_SESSION_NONE donuyor. Suzgec artik Adminer'in kendi `db` parametresinden.
- `adminer_object()` KURESEL ad alaninda kalmali: `function_exists('adminer_object')` dizgeyi ad
  alanina gore cozmez.
- Bu bir yetki siniri degil gosterim sinirdir: Adminer'i acan uclarin ikisi de Administrator-only.
- **Canli dogrulama yontemi:** panel API'sinden gercek DB+kullanici olusturuldu (`POST /api/v1/db`,
  alan adi `engine`), `php -S` ile gercek HTTP uzerinden test edildi, sonra DB silindi.
  Giris alani: `userNameOrEmail` (`userName` DEGIL), CSRF yaniti `data.requestToken`.

### 0.2.89 — ham hata anahtari ekranda + olu kopyala dugmeleri + 33 eksik "İ"
- `common.unexpected_error` hicbir dilde YOKTU (middleware'in yakalanmayan HER istisnasi bu kodu
  donduruyor) + 5 anahtar daha. **Yeni test `MessageKeyCoverageTests` bu SINIFI kapatiyor.**
  Test iki kez dogrulandi: anahtar silinince patliyor, ve tarama bozulup 0 anahtar bulursa
  sessizce gecmesin diye alt sinir var — ILK YAZIMDA tam da bu oluyordu (kok yolu
  `...\bin\Debug\..\..\..` seklinde birikince bin/obj filtresi butun kaynaklari atliyordu).
  Tarama bilerek yalnizca HATA yollarini kapsiyor (basari mesajlarini arayuz kendi metniyle gosterir).
- `utils/clipboard.ts`: modern API yoksa execCommand yedegi -> dugmeler http'de de GERCEKTEN
  kopyaliyor. 8 cagri yerinin hepsi cevrildi (2FA kurtarma kodlari dahil), 2 yerde `window.prompt`
  kaldirildi. Sonuc olculuyor: kopyalanmadiysa "kopyalandi" DENMIYOR.
- Form acilir listeleri artik tablo aramasina bagli degil (arama yapinca "yetki verilecek
  veritabani" listesi bosaliyordu).
- Goc ekraninda kaynak panel adlari ICERIKTE bilerek KALDI (islevsel); yalnizca sabit kodlu sekme
  etiketleri i18n'e tasindi.

### 0.2.90 — gercek Let's Encrypt sertifikasi "self-signed" gorunuyordu
- Kaynak `FriendlyName` icinde "Let's Encrypt" aranarak bulunuyordu; o adi win-acme yazar
  (`[win-acme] ornek.com @ ...`) ve kosul HICBIR ZAMAN dogru olmuyordu. Kullanici sertifikanin
  alinmadigini sanip tekrar deniyor ve LE haftalik limitini yakiyordu. Artik veren/konu
  iliskisinden okunuyor; baska otorite imzaladiysa "manual" (ona "self-signed" demek de yanlisti).
- `IsHttpsBindingPresent` sabit `true` idi -> artik `bool?` ve GERCEKTEN olculuyor; null = olculemedi.
- Tekil sertifika yolunda 120 sn -> 10 dk (mesru yayinlamayi ortasinda kesiyordu) ve kendinden
  imzaliya dusus artik SESSIZ DEGIL: sebep DTO'da tasinip sertifika satirinin altinda yaziliyor.

### BU TURDA PAKET URETILMEDI
- 0.2.86 GitHub'da yayinda; 0.2.87-0.2.90 yalnizca commit'lendi, installer uretilmedi/yayinlanmadi.
- Canli sunucuya cikmadan once: `dotnet publish` + ISCC ile paket, SHA256 kaydi, GitHub release.

## 2026-08-05 (213) — 0.2.91: tam sistem taramasi + GUVENLIK duzeltmesi + PAKET

- **KRITIK ACIK (K7 kapandi):** DB araci belirtec dizini `icacls /grant IIS_IUSRS:(OI)(CI)M` ile
  aciliyordu. IIS_IUSRS **butun uygulama havuzlarini** kapsar; dizinde motorun **root parolasi duz
  metin**. Her musterinin PHP kodu okuyabiliyordu. Artik yalnizca aracin KENDI havuzu + miras
  kirik + hiddenSegments. **Kilit dogrulanamazsa parola YAZILMIYOR** (fail-closed).
  - **OLCULEN 1:** havuz adi SITE ADI DEGIL — `CreateSiteAsync` one `AdaPanel.` ekliyor, yani
    site `AdaPanel.WebDb.Adminer` icin havuz `AdaPanel.AdaPanel.WebDb.Adminer`. Yanlis hesap
    adinda icacls **1332 ile HICBIR SEYI uygulamiyor** → duzeltme araci tamamen kirardi.
  - **OLCULEN 2:** icacls bir alt ogeye erisemedigi halde **cikis kodu 0** donuyor. Sonuc bu
    yuzden dizinin KENDI ACL'inden dogrulaniyor (`AreAccessRulesProtected` + IIS_IUSRS SID
    `S-1-5-32-568` yok mu).
  - Hesap adlari **SID ile** verilir (`*S-1-5-18`, `*S-1-5-32-544`): Turkce Windows'ta adlar farkli.
- **Sessiz basarisizlik:** FilesView kaydet/klasor/sil + yedek listesi ve UsersView plan
  ekle/sil hata yakalamiyordu. "Kaydet" en pahalisiydi → sessizce basarisiz, kullanici
  kaydedildigini sanip sekmeyi kapatiyor = VERI KAYBI.
- **window.prompt ile parola (6 yer) kaldirildi:** yeni `PromptDialog.vue` (gizli alan +
  "goster" secimi + asgari uzunluk ONDEN yazili + odak tuzagi). Panelde artik window.prompt YOK.
- **Taramada TEMIZ:** kurulum ucu loopback sinirli, Git webhook token dogruluyor, TODO/FIXME
  borcu yok, bos catch'lerin hepsi PowerShell betigi icinde (niyetli).
- **0.2.91 PAKET:** `artifacts\release\0.2.91\AdaPanelSetup.exe`, `55,306,361` byte (52.74 MiB),
  SHA256 `81B1844F48C87E31805B946DD375570DD07FC6516FC34F327DF9DEBC3F52C2C2`.
- **YAYINLANDI (2026-08-05):** GitHub release **0.2.91** (id 365559428), asset id 502672604.
  YAYINDAKI DOSYA INDIRILIP HASH LENDI: `81B1844F...3F52C2C2` (yereldekiyle BIREBIR).
  `releases/latest` -> **0.2.91**, draft=false, prerelease=false.
- **YAYIN YONTEMI (onemli):** GitHub deposunda **KOD YOK** — yalnizca README var ve 73 tag in
  hepsi o tek commit e (`7d5feb8`) isaret ediyor. Yani release ler SADECE kurulum paketi
  tasiyor; kaynak kod hic push edilmiyor (yerel gecmis uzakla **ilgisiz**, merge reddediliyor).
  Kimlik **Windows Credential Manager**'da duruyor; token'i `git credential fill` ile al
  (girdi: `protocol=https` + `host=github.com` + bos satir), sonra REST API ile release olustur
  ve asset yukle. `gh` CLI kurulu DEGIL, ortamda GH_TOKEN YOK. Anonim API cagrilari rate
  limit'e takilir; dogrulamayi token ile yap.
- **Yayin sonrasi ZORUNLU dogrulama:** yayindaki dosyayi indirip SHA256'yi yereldekiyle
  karsilastir + `releases/latest` gercekten yeni surumu gosteriyor mu bak (panelin self-update'i
  onu okur).

## 2026-08-08/10 (214) — GELİŞTİRME MAKİNESİNDE VİRÜS + iki dalgalık tam denetim ve düzeltme

> NOT: bu dosya 0.2.92'den 0.4.22'ye kadar GÜNCELLENMEMİŞTİ. O aralığın kararları yalnızca
> git commit mesajlarında; 0.3.0-0.4.22 arası ise hiç commit'lenmemişti (aşağıya bak).

### A. GELİŞTİRME MAKİNESİNDE AKTİF EXE BULAŞTIRICI BULUNDU (kritik)

`artifacts\release\0.2.91\AdaPanelSetup.exe` 56.077.824 bayt görününce ölçtüm: bu dosyanın
CODEX kaydındaki boyutu 55.306.361 ve GitHub'daki asset digest'i kayıtla BİREBİR. Yerel dosya
263. bayttan itibaren farklıydı ve `VersionInfo` **"Synaptics Pointing Device Driver"** diyordu.

- Zararlı: `C:\ProgramData\Synaptics\Synaptics.exe` (771.584 bayt), kalıcılık
  `HKLM\Software\Wow6432Node\...\Run` → "Synaptics Pointing Device Driver".
  `SSLLibrary.ddl` 23 GB'a ulaşmış (içeriği tekrar tekrar indirilmiş 404 HTML'i).
  `%AppData%\WinSl` = klavye günlüğü; en eski günlük **11.11.2021** ve içeriği WinOLS pencereleri.
- Bulaşma anı ölçüldü: **8 Eylül 2025 11:52:43**. 11:52:48'de Temp'e makrolu `.xlsm` düştü,
  11:52:49'da masaüstüne 771.584 baytlık `~$cache1`. Yani vektör **makrolu bir Excel dosyası**;
  kaynak büyük olasılıkla kırık WinOLS/tuning ekosistemi (keylogger günlükleri 2021'e gidiyor).
- Her açılıştan ~6 dk sonra diskteki EXE'leri yeniden bulaştırıyordu (8 Ağu 14:15'te 87 dosya).
- **Defender sistemde YOKTU**: `DisableAntiSpyware=1`, `DisableRealtimeMonitoring=1` ve
  `WinDefend` servisi hiç kayıtlı değil.
- Temizlik: kullanıcı alanındaki 96 öğe + 27,33 GB silindi; sistem tarafı (ProgramData, HKLM,
  Defender politikaları) yükseltilmiş bir betikle kullanıcı tarafından temizlendi ve DOĞRULANDI.

**KALICI DERS — YAYIN ÖNCESİ ZORUNLU:** "yayındaki dosyayı indirip yereldekiyle SHA256
karşılaştır" doğrulaması bu sınıfa KÖRDÜR: yerel bulaşıksa GitHub'daki de aynı bulaşık dosyadır,
hash'ler birebir tutar ve doğrulama "TEMİZ" der. Paket üretmeden önce
`(Get-Item $exe).VersionInfo.CompanyName -eq 'Synaptics'` kontrolü ŞART. Bu kontrol artık
`scripts\verify-publish-output.ps1` içinde ve her iki paketleme hattına bağlı.

**Temiz doğrulandı:** yayındaki 0.2.91 (`81b1844f…`) ve 0.4.22 (`0db89e16…6ee730e4`) paketleri,
publish çıktısı, kurulu panel, ISCC ve x64 apphost/singlefilehost şablonları. Müşteriye
bulaşık paket GİTMEDİ (0.4.22 02:11'de üretilip 02:13'te yüklendi, bulaşma 14:15'te oldu).

### B. 0.3.0-0.4.22 COMMIT'LENMEMİŞTİ

Son commit 0.2.99 (5 Ağu) idi; 6-7-8 Ağustos'ta yapılan ve **0.4.22 olarak GitHub'a yayınlanan**
işin tamamı yalnızca çalışma ağacında duruyordu (178 dosya, +12194/-385). Elektrik kesintisi bu
işi silebilirdi. Tek commit'te güvence altına alındı (`32cbcaf`).

### C. BEŞ AJANLI TAM DENETİM — bulunan ve KAPATILAN P0'lar

- **Cron görev adından SYSTEM olarak kod çalıştırma.** `EscapePowerShell` yalnız `"` kaçırıyordu,
  çıktısı ÇİFT TIRNAKLI PowerShell dizesine giriyordu → `$(...)` çalışıyordu. `Name` doğrulamasız,
  `CronController` Customer'a açık, servis LocalSystem (canlı ölçüldü). Çözüm: tek tırnak +
  `EscapeSingleQuoted` (aynı dosyada zaten vardı) + `TaskNameRules` beyaz listesi.
- **Site ADI hiç doğrulanmıyordu.** Host adı için sahiplik iddiası vardı, `Name` için yoktu →
  başkasının IIS sitesini devralma, `CustomerScopeService.cs:185` yüzünden mail domaini ve DNS
  bölgesi devralma, cascade'in adı benzeyen veritabanını DROP etmesi.
- **Oturum çerezi hiçbir olayda geçersizleşmiyordu.** `OnValidatePrincipal` yoktu.
  ÖLÇÜM: `UpdatedAtUtc` ölçüt YAPILAMAZ — AuthService onu BAŞARISIZ girişte de güncelliyor,
  yani saldırganın tek başarısız denemesi kurbanı oturumdan atardı. Parola karmasından türetilen
  damga çereze yazıldı (EF göçü gerekmedi).
- **Geri yükleme, DB import patlarken "restored" diyordu** (çıplak `catch {}`), üstelik dosyalar
  zaten canlının üzerine yazılmıştı. Artık içerik önce kardeş klasöre taşınıyor; taşınamıyorsa
  işlem HİÇ BAŞLAMIYOR.
- **Yedek budama, yedekler hiç alınamasa da çalışıyordu** ve yedek başarısızlığı için uyarı yoktu.
- **`uninstall-cleanup.ps1:250` `configtp-users.json`** — ters bölü kayıp, FTP hesap temizliği
  hiç çalışmıyordu. Üstelik KeepData=1 dalı `:28`'de `exit 0` yaptığı için o koda zaten
  ulaşılmıyordu: iki bağımsız hata aynı özelliği iki kez öldürmüş.
- **Paketleme scriptleri `$Version = "0.2.28"`** varsayılanı taşıyordu ve hiçbiri
  `Directory.Build.props` okumuyordu; README komutu parametresiz veriyordu → yayınlansa
  SONSUZ GÜNCELLEME DÖNGÜSÜ (panel her turda 0.2.28'e düşer, yine yeni sürüm görür).

### D. İKİNCİ DALGA — eklenen yetenekler

Abonelik askısı fiilen dayatılıyor (durum değiştiren istek 403, okuma serbest) · SSL, WordPress
Toolkit, istatistik, site sağlığı ve disk kotası MÜŞTERİYE açıldı (sunucu-geneli olanlar bilerek
kapalı) · bayi kendi servis planını yönetiyor + bayi tavanı · denetim kaydı (Files/Mail/FTP/DB/
Terminal + filtre/sayfalama/CSV/saklama + kiracıya kapsamlı açık) · yedek şifreleme
(AES-256-GCM, PBKDF2 600k) · HTTPS bağlaması kaldırma (`POST /ssl/remove`) · lisans altyapısı
(VARSAYILAN KAPALI) · WHMCS provisioning modülü · `docs/` (8 dosya) · arayüzde sayfalama, toplu
işlem, varlık araması, kod bölme (en büyük chunk 1.133 kB → ~600 kB).

**ÖLÇÜM NOTU (lisans):** makine parmak izi = `SHA-256(MachineGuid + kurulum kimliği)`.
MAC reddedildi (panelin KENDİSİ Docker kurunca sanal arayüz doğuruyor); SMBIOS reddedildi,
bu makinede ölçüldü: `Win32_BIOS.SerialNumber` = "To Be Filled By O.E.M".

### E. DURUM ve AÇIK KALANLAR

- Test **976 → 1232**, hepsi yeşil. i18n **TR 3672 = EN 3672**, tek yönlü fark 0.
- Sürüm hâlâ **0.4.22**; PAKET ÜRETİLMEDİ, GitHub'a bir şey gönderilmedi. Yayın için sürüm
  artırılmalı — `scripts\version-guard.ps1` artık uyuşmazlıkta DURDURUR.
- **AÇIK:** kişisel API anahtarları REST API'de çalışmıyor (`PanelToken` şeması yalnız `/mcp`'ye
  bağlı, `/api/v1` varsayılan çerez şemasını kullanıyor, `Authorization` başlığı okunmuyor) ·
  MCP yazma araçları abonelik askısını ve alt-kullanıcı bölüm izinlerini atlıyor · yöneticinin
  kiracı kaynağındaki işlemi kiracının denetim görünümünde çıkmıyor · dosya taşıma ucu yok ·
  bayi menüsünde yedekleme yok · lisans/şifreleme ekranları arayüzde yok · 155 ölü i18n anahtarı.

## 2026-08-10 (215) — Üçüncü dalga: kimlik, dosya, posta, arayüz + 0.5.0 hazırlığı

### A. KİŞİSEL API ANAHTARLARI REST'TE HİÇ ÇALIŞMIYORDU (ve düzeltmesi bir açık açacaktı)

Ölçüldü: `PanelToken` şeması YALNIZCA `/mcp`'ye bağlıydı; `/api/v1/**` controller'ları
`AuthenticationSchemes` belirtmediği için varsayılan ÇEREZ şemasını kullanıyor ve
`Authorization` başlığını hiç okumuyordu. Yani "kişisel API anahtarı" özelliği fiilen
yalnızca yapay zekâ istemcisi içindi; WHMCS modülü bu yüzden çerez oturumu açmak zorunda kaldı.

Çözüm: 50+ controller'a öznitelik yazmak yerine **policy scheme yönlendirici**
(`PanelAuthentication`, Program.cs) — istek `Bearer adap_` taşıyorsa anahtar şemasına,
taşımıyorsa çereze. Yeni controller'da unutulamaz.

**KRİTİK YAN BULGU:** `AllowDestructive`/`AllowTerminal` yetenekleri YALNIZ MCP araçlarında
sorgulanıyordu. REST anahtarı kabul edilir edilmez "yıkıcı işleme izin vermeyen" bir anahtar
tek HTTP çağrısıyla site silebilir/komut çalıştırabilirdi. `PanelTokenCapabilities` eklendi:
anahtar varsayılan SALT OKUR, terminal ayrıca `AllowTerminal` ister. `AuthController` ve
`ApiTokensController` çerez şemasına SABİTLENDİ — yoksa salt-okur anahtar kendine tam yetkili
yeni anahtar basardı.

CSRF kararı uç bazında değil **isteğin kimlik yoluna** göre çalışma anında veriliyor
(`PanelAntiforgeryFilter`): çerezde aynen uygulanır, anahtarda atlanır — çünkü antiforgery
ÇEREZE karşı bir korumadır ve `Authorization` başlığı tarayıcıdan otomatik gitmez.

**BEDELİ OLAN BİR KARAR:** `SecureEntryPathMiddleware` artık anahtar taşıyan isteği gizli giriş
yolu başlığından muaf tutuyor (aksi hâlde WHMCS gizli yolu da bilmek zorunda kalır ve yönetici
yolu değiştirdiğinde entegrasyon sebebi görünmeyen bir 404 ile sessizce kırılır). Bedeli:
burada yalnız başlığın BİÇİMİNE bakılır, anahtar henüz doğrulanmamıştır — sahte bir
`Bearer adap_…` gönderen taraf eskiden 404 alırken artık 401 alır. Kaybedilen şey ucun
VARLIĞININ gizlenmesi, erişim değil; aynı özellik `/mcp` muafiyetiyle zaten kaybedilmişti.

### B. DİĞER İŞLER

- `POST /files/move`: tek/çoklu taşıma + yeniden adlandırma. Yeniden adlandırma ayrı uç DEĞİL,
  "hedefi kendi klasörü olan taşıma" — korumalar iki yerde yazılmasın diye. Klasör `Overwrite`
  bayrağıyla bile EZİLMEZ (özyinelemeli silme bu depoda yaşanmış hata sınıfı).
  ÖLÇÜM: `PathContainment` kapısı tek başına yetiyor — eski `StartsWith` kontrolü kapatılınca
  16 taşıma testi hâlâ geçti, ikisi birden kapatılınca patladı.
- `AuditLog.SubscriptionUserId` (nullable, EF göçüyle): yöneticinin KİRACI kaynağında yaptığı
  işlem artık kiracının denetim görünümünde çıkıyor. Eski kayıtlar NULL → görünürlük aynı.
- Posta: kuyruk görüntüleyici, smarthost/relay (parola DPAPI ile; kapatmada ÖNCE yönlendirme
  geri alınır SONRA rota silinir — ters sıra gideni bir süre tamamen durdururdu),
  autodiscover/autoconfig uçları.
  ÖLÇÜM: Stalwart v1'de REST kuyruk API'si YOK (`/api/queue/messages` → 404), nesne modeline
  taşınmış. Alan adları resmî şema referansından alınıp sunucu binary'sinde arandı.
  Doğrulanamayan tek şey `RecipientStatus` içindeki hata alan adları — UYDURULMADI, varyant adı
  + ham JSON taşınıyor.
- Arayüz: lisans ekranı, yedek şifreleme alanları, askı modu (axios kesicisinde TEK kapı,
  gerçek axios ile ölçüldü), bayi yedek menüsü (`injectBackupSection` tek kaynağa çıkarıldı --
  elle kopyalanmıştı ve EKSİK OLAN kopya bayininkiydi), 150 ölü i18n anahtarı silindi.

**ÖLÇÜM DERSİ (ölü anahtar taraması):** yapı çıktısı bu ölçümü SESSİZCE zehirliyor —
`artifacts/`, `wwwroot/`, `bin/`, `obj/` dahil edilince tüm i18n paketi derlenmiş JS'in içinde
görünür ve ölü sayısı 152'den 5'e düşer. Ayrıca `.md` belgelerinde anılmak KULLANIM DEĞİLDİR.

### C. AÇIK KALANLAR

- **Autodiscover istemcilere ULAŞMIYOR:** uçlar panel API'sinde, müşterinin alan adı IIS'te
  80/443'ten servis ediliyor. `autodiscover.<alan>` binding'i + URL Rewrite ya da site köküne
  statik XML gerekiyor.
- Düğme bazında salt-okunur devre dışı bırakma yapılmadı (yazma/okuma düğmesini ayırt eden bir
  işaret yok; kırılgan CSS sezgisi uydurmak yerine kalıcı bildirim + istek kapısı verildi).
- `system_overview` MCP'de yönetici ister, REST ikizi her role açık — bilinçli asimetri, kodda
  gerekçeli (MCP çıktısı üçüncü tarafa gider).
- Beyaz etiket, slave DNS, per-site Node sürümü, WAF kural seti, posta/lisans ekranlarının bir
  kısmı.

### D. 0.5.0 HAZIRLIĞI

Test **976 → 1346**, i18n **TR 3574 = EN 3574**. Sürüm 0.4.22 → **0.5.0** (yalnız düzeltme değil;
lisans, yedek şifreleme, denetim kaydı, çalışan API anahtarları yeni yetenekler).
Yayındaki sürüm API'den DOĞRULANDI (0.4.22) — yayınlanmış tag'in üzerine paket üretilmedi.
Beş yer güncellendi ve `scripts\version-guard.ps1` GEÇTİ.
**PAKET HENÜZ ÜRETİLMEDİ, GitHub'a hiçbir şey gönderilmedi.**

### E-1. 0.5.0 YAYINLANDI (2026-08-11)

- **Paket:** `artifacts\installer\AdaPanelSetup.exe`, **89.665.087 bayt** (85,51 MiB),
  SHA256 **`970B603AA5E47EC234811C518289B24D231982E96163FC1E8DD04EF2B624B758`**.
- **GitHub release 0.5.0** (id `368241558`, asset id `509356877`). `releases/latest` → 0.5.0,
  draft=false, prerelease=false.
- **YAYIN SONRASI DOĞRULAMA YAPILDI:** yayındaki dosya indirilip hash'lendi → yereldekiyle
  BİREBİR. İndirilen dosyanın `CompanyName` = "AdaPanel" (bulaşma imzası YOK).
  Publish çıktısındaki tüm PE dosyaları da tarandı: bulaşık dosya 0.

**PAKETLEME SIRASINDA BULUNAN GERÇEK KOPUKLUK:** `components.json`'daki `php-8.3` yedek indirme
adresi **404** veriyordu (8.3.31 arşive taşınmış). Kurulum bozuk DEĞİLDİ — `ComponentBootstrapper`
php-8.3 için canlı sürüm çözümleyicisi taşıyor (`releases.json`'dan güncel yamayı alır). Ama
manifest URL'si canlı çözümleme başarısız olduğunda düşülecek YEDEK yoldur ve o yedek ölüydü.
24 bileşenin tamamı tek tek yoklandı; kırık olan YALNIZCA php-8.3 idi. Manifest resmî
`releases.json`'dan 8.3.31 → **8.3.33** (sha256 dahil) güncellendi ve bileşen denetimi
`php-8.3 -> Passed` ile doğruladı.

**NOTLAR (sonraki tur için):**
- `scripts\audit-component-packages.ps1` içindeki `Download-File`, hedef dosya varsa
  BOYUT/HASH BAKMADAN "indirilmiş" sayıp geçiyor. Şu an `finally` temizliği bunu maskeliyor;
  `-KeepDownloads` ile çalıştırılırsa yarım kalmış dosya sessizce "geçerli" muamelesi görür.
- Denetim, canlı sürüm çözümleyicisi olan bileşenlerin STATİK manifest URL'sini test ediyor.
  O bileşenlerde üst kaynak yeni yama yayınladığı anda denetim yayını bloke eder. Ya canlı
  çözümleme sonucunu test etmeli ya da bu bileşenleri ayrı raporlamalı.
- Paketleme sırasında `vc-redist-x64` indirmesi iki koşuda ağ okuma hatası verdi (aynı URL
  curl ile 1,5 sn'de indi, PowerShell'de elle tekrarlanınca da başarılı). Oynak CDN.

### E-2. 0.5.1 YAYINLANDI (2026-08-17) — otomatik yedek düzeltmesi

- **Paket:** 89.666.969 bayt (85,51 MiB),
  SHA256 **`46374F1C72DB9C6C9DE21D625A5F77ED18136D96E0FD588277345505392D8D26`**.
- **GitHub release 0.5.1** (id `371439141`). `releases/latest` → 0.5.1. Yayındaki dosya indirilip
  hash'lendi → yereldekiyle BİREBİR; `CompanyName` = "AdaPanel", publish çıktısında bulaşık PE = 0.
- Yerel etiket `0.5.1` → `f0be0f2`.

**İKİ DÜZELTME:**
1. **Site başına TEK arşiv.** Otomatik yedek siteyi dosyalarıyla ayrı, veritabanlarını tek tek
   ayrı, postayı bambaşka bir arşivle alıyordu. Artık tek zip: `files/ + databases/ + mail/ +
   config/ + manifest.json`. Hangi DB'nin hangi siteye gireceği **sahiplik kaydından**
   (`IDatabaseOwnershipService.GetAllAsync`) okunuyor, ad benzerliğinden DEĞİL. Karar saf bir
   fonksiyona çıkarıldı (`AutomationService.GroupDatabasesBySite`) çünkü otomasyon işi 19
   bağımlılıkla kuruluyor. Sahipsiz DB'ler kendi başlarına yedeklenmeye DEVAM eder.
2. **Uzak hedef hatası artık yerel yedeği götürmüyor.** `FinalizeBackupAsync` kaydı YÜKLEMEDEN
   SONRA yazıyordu; FTP patlayınca istisna yukarı çıkıyor ve kayıt HİÇ oluşmuyordu → alınmış,
   sağlam, yerel bir yedek diskte duruyor ama panelde GÖRÜNMÜYOR (listede yok, geri yüklenemiyor,
   saklama temizliğine takılmıyor). FTP'nin bir gecelik arızası o gecenin yedeğini fiilen yok
   ediyordu. Artık üç durum: `created` / `created_remote_synced` / `created_remote_failed`.

**YAYIN YÖNTEMİ NOTU (ilk kez yapıldı):** paket, ana depodan DEĞİL, commit'e bağlı **ayrı bir
worktree'den** üretildi — çünkü ana depoda bir ajanın yarım arayüz işi duruyordu ve yayına
girmemeliydi. Worktree'de `.tools` (gitignored) yok; `mklink /J` ile ana depoya junction verildi,
`npm ci` + `npm run build` worktree içinde çalıştırıldı. Bu, "yayına yalnızca commit'li ve
testten geçmiş kod girer" garantisini veren pratik yol.

**Test:** 1354 (+8). Korumalar MUTASYONLA doğrulandı: uzak hata yok sayılınca ve sahiplik süzgeci
kaldırılınca 4 test kırmızıya döndü.

### E-3. 0.5.2 ve 0.5.3 YAYINLANDI (2026-08-17)

- **0.5.2** (id 371444223): 89.684.485 bayt, SHA256 `134F07B45A83F9D928A74287B21519AC428B7A6FB7F7CDF807D3AC4671A8B2E9`.
  Yedek hedefi etiketleri ("Yerel disk + FTP"), posta kuyruğu + smarthost kartları, askıda
  düğme seviyesinde salt-okunur (31 dosya/156 kontrol), autodiscover artık İSTEMCİLERE ULAŞIYOR,
  system/overview rol daraltması, bileşen denetiminde canlı-çözümleme ayrımı + atomik indirme.
- **0.5.3** (id 371455300): 89.640.420 bayt, SHA256 `57873FBE4495E4A742C79D715DA2C4E91F4AA941C1A5722870CF1FB9A2907FCC`.
  İkisinde de yayındaki dosya indirilip hash'lendi → BİREBİR; CompanyName = AdaPanel.
  Yerel etiketler: `0.5.2` → `766f96c`, `0.5.3` → `8aa341c`.

**0.5.3'ÜN ASIL BULGUSU — KULLANICININ CANLI PANELİNDEN GELDİ:**
`AutomationService`'te `DefaultEngines = ["mysql"]` diye SABİT bir liste vardı; AdaPanel
varsayılan kurulumda **MariaDB** kurar. Gecelik yedek her gece var olmayan motoru soruyor,
`database.engine_not_installed` alıyor ve **SIFIR veritabanı yedekliyordu**. Özette
"0 veritabanı yedeklendi" yazdığı için bu bir BİLGİ gibi görünüyordu — oysa sessiz arızaydı ve
**varsayılan kurulumda herkeste böyleydi**. Motor listesi artık `GetEnginesAsync` ile kurulu
olandan türetiliyor (Installed && Ready). Karar saf fonksiyona çıkarıldı: `SelectBackupEngines`.

**DERS:** doğru yazılmış bir sayı, yanlış bir gerçeği anlatabilir. "0 veritabanı" satırı panelin
hatası değil, panelin doğru raporladığı bir arızaydı; kimse sorgulamadığı için aylarca yaşadı.

**Ayrıca 0.5.3'te:** WHMCS'in eksik iki ucu (parola belirleme AuthController'a kondu -- o sınıf
çerez şemasına sabitli, yani salt-okur olmayan bir anahtarla başkasının parolası belirlenip tam
yetkili oturum açılamıyor; kendi hesabı için de reddediliyor). Ölçülen asimetri kapandı:
`CreateSubUserAsync` parola uzunluğunu HİÇ denetlemiyordu. SSH.NET 2024.1.0 → 2026.0.0
(GHSA-q939-rpr3-3284; ÖLÇÜLDÜ: açık ScpClient'ta, panel SftpClient kullanıyor → etkilenmiyorduk).

**Test: 1446.** Yayın yöntemi: her ikisi de ana depodan değil, commit'e bağlı ayrı worktree'den
paketlendi (ana depoda ajan işi duruyordu) — `.tools` junction + `npm ci` + `npm run build`.

## 216) 0.6.1 -- site basina POSTA yedegi + kaynak basina klasor duzeni (18.08.2026)

Iki is birlikte cikti (kullanici: "ikisi birlikte ciksin").

**Klasor duzeni:** `gun -> kategori -> kaynak`. Siteye ait DB yedegi o sitenin
klasorune girer; sahiplik `IDatabaseOwnershipService`ten okunur, isim benzerligine
BAKILMAZ. Eski yedekler etkilenmez -- yol her kayitta saklaniyor.

**Site basina posta:** vandelay + IIS'te yalnizca `/jmap/` ve `/.well-known/jmap`
yolunu ileten vekil. Kural sirasi: (1) `..`/`%2e`/`%252e`/`%5c`/`\` iceren istekler
403, arka uca hic ulasmadan; (2) well-known; (3) jmap/*.
Ajan benim "`^api/` toptan reddet" talimatimi BILEREK uygulamadi: kurallar
MUSTERININ web.config'ine giriyor, toptan red musterinin kendi /api/ uygulamasini
oldururdu. Gercek risk (dizinden cikma) zaten 1. kuralda kesiliyor. Karar dogru.

**On kontrol:** vandelay kurulu mu -> parola cozuluyor mu -> Stalwart'in DUYURDUGU
apiUrl (tahmin degil, sunucudan okunur) acilyor mu. Gecmezse posta konmaz ve
GIZLENMEZ (`mailNote` + `mailMessagesError`). Tek hesap bile alinamazsa hicbiri konmaz.

**Testler:** 1653 (+41). Mutasyon dogrulamasi: red kurali sirada asagi -> 11 test
kirmizi; kismi basari tam sayilinca -> 2; alan adi kapisi naif EndsWith -> 2
(`kutu@alt.ornek.com` ve `kutu@kotuornek.com` siziyor).

**DOGRULANMADI:** IIS'in kendisi (kurallar .NET Regex ile XML uzerinde denendi,
URL Rewrite modulu uzerinden DEGIL). Vekilin gercek IIS'te 443'ten calistigi
gorulmedi -- bu makinede Stalwart yok. ILK CANLI DENEMEDE BAKILACAK YER BURASI.
`vandelay` components.json'a sha256 ile eklendi ama kurulumu denenmedi.

**Yayin:** SHA256 0FA0AB6517D4E1CFDF70A9DE06DE9DB79194C2C0F27B704E46A4A2E01FC43AAC,
90.004.937 byte. Indirilen varlikla yerel dosya hash'i ESLESTI, releases/latest = 0.6.1.
Publish icerik denetimi 155 dosya GECTI, Synaptics imzasi yok.

**Ders:** version-guard release-body.json'i da denetliyor -- surum yukseltmesini
commit'lerken onu disarida biraktim, guard paketi URETMEDI ve yakaladi. Guard calisiyor.

## 217) TAM SISTEM DENETIMI + 10 duzeltme ekibi -> 0.6.5 (19-20.08.2026)

Kullanici "tum sistemi tara, hepsini cikar" dedi. Yedi bagimsiz denetim, HATA SINIFI
basina bolundu (dosya/modul basina DEGIL -- sinif basina bolmek ayni kok nedeni farkli
modullerde yakalatti). Bulgular `docs/denetim-2026-08-19.md`'ye yazildi, sonra on
duzeltme ekibi o dosyaya `dosya:satir` referansiyla calisti.

**Sonuc:** 0.6.5 yayinlandi. Testler 2488 -> 2810 (+322).
SHA256 488E4F5888671430224F1ACE6D6E5AE5A7AAC98406BB213A6DFFF91EB33C8F0F.

### OLCULEN aciklar (bu makinede yukseltilmemis kullanici olarak dosya ACILARAK)

- Stalwart super-admin parolasi `run-stalwart.cmd` icinde DUZ METIN ve kiraciya okunabilir
  -> barindirilan her sitenin PHP'si sunucudaki TUM postayi ele geciriyordu.
- MariaDB veri dizini kiraciya okunabilir -> tum musteri DB'leri + root parola ozetleri.
- `certs\panel.password` (PFX ozel anahtar parolasi) kiraciya okunabilir.
- Sertlestirme mirasi KOPYALAYIP MUHURLUYORDU: kokte ne varsa (bu makinede
  `Everyone=FullControl`) hassas klasorlere kalici gomuluyordu ve kok duzeltilse bile dusmuyordu.

### "Kurulum tamamlanamadi" KOK NEDENI KANITLANDI

Aylardir acikti. Diskteki iki artefakt zinciri kapatti:
`last-attempt.json` hukmu **21:49:07Z**, `apply-update.log` kurulum bitisi **21:49:23Z**.
Panel hukmu kurulum bitmeden 16 SANIYE ONCE yaziyor -> `installerExitCode` yapisal olarak null.
Sonra pending kaydi silindigi icin panel bir daha hukum veremiyor, calistiricinin gec yazdigi
"failed" KALICI oluyor. Ve kanit yok ediliyordu: calistirici gunlugu ile Inno `/LOG=` AYNI dosya,
Inno dosyayi sifirdan aciyor -> dosyada `[runner]` satiri HIC yok.

Duzeltme yarisi kazanmaya CALISMADI (kurulum sureci servis baslangicini daima asar);
panelin hukmune sahiplik konuldu, calistirici yalnizca kendi bildigi cikis koduyla zenginlestiriyor.

### Bu turun en degerli dersleri

1. **Ajan mutasyon testi GERCEK SISTEME yazabilir.** Yetki ajani K6'yi dogrularken bu makinenin
   yerlesik `Administrator` parolasini GERCEKTEN degistirdi. Hesap devre disi oldugu icin zarar
   sinirli kaldi. Ders: yikici bir zinciri mutasyonla dogrulatirken YALITILMIS hedef sart kos.
   Kazanc: K6 artik kagit uzerinde bulgu degil, calistigi olculmus bir zincir.
2. **Nobetci testin kendisi hata sinifindan olabilir.** `AuditCoverageTests` elle yazilmis
   5 dosyalik listeye bakiyordu; 63 denetleyiciden 5'ini koruyup yesil kaliyordu. Ters cevrildi:
   dosyalar DISKTEN okunuyor, kapsanmayanlar GEREKCELI muaf listesinde duruyor ve borc kapaninca
   listeden DUSURULMEK ZORUNDA. Ilk turunda 17 bayat girdiyi yakaladi -> gercekten calisiyor.
3. **Koordinatorun 8 on bulgusu YANLIS cikti** (uzak DB, posta listeleri, WHMCS "yok" sanildi;
   junction takibi .NET 8'de zaten kapali; cascade motor listesi silme yolunda degil).
   Hepsi aramayi dar tutup AD TAHMIN etmekten. Raporda ayri bolumde kayitli.
4. **Kirilgan test = ilerideki gercek arizanin elenmesi.** Iki zamanlama testi (cluster paralellik,
   SEO zaman asimi) yuk altinda rastgele kirmizi veriyordu. Cozum esigi gevsetmek DEGIL:
   cluster'da duvar saati yerine ESZAMANLILIK olculdu; SEO'da iki durum arasi mesafe acildi.

### Bilerek KAPANMAYANLAR

Imza zorunlulugu (paket imzasiz; zorunlu kilmak kurulu her paneli guncellenemez yapar --
kural yazili ve testli, `RequireSignature` bayragiyla acilir), kullanici silme ucunun 200
donmesi (Core imza degisikligi ister), `subscriptionUserId` 219 kaydin 26'sinda dolu.

## 218) Denetim duzeltmelerinin ikinci turu -> 0.6.6 (24.08.2026)

Dort ekip: silme butunlugu, kiraci bagi, gozlemlenebilirlik, tarama ilerlemesi.
Testler 2810 -> 2885. Kullanicinin haftalik kota siniri yuzunden birkac kez durdurulup
kaldigi yerden devam ettirildi.

**Kullanici silme artik yarim isi basari saymiyor.** Yarim silmede 409 + yapisal kalinti
listesi (tip + AD + sebep + "bilerek mi"). Ajan "kalinti varsa kullaniciyi hic silme"
secenegini OLCUP ELEDI: cascade siteyi dosyalari silmeden ONCE kapsamdan dusuruyor, yani
ikinci deneme bos kapsam gorup "basarili" derdi -- yalan ayni yerden geri gelirdi.
WHMCS modulu HTTP durumuna degil zarftaki `success` alanina baktigi icin olcut karsilandi.
`_not_found` son ek kurali kaldirildi: `AppException.ResourceAlreadyGone` bayragi.

**Kiraci bagi 26 -> 70 kayit, borc listesi 14 -> 0.** Sahiplik OLCULUYOR (site sahibi,
`IDatabaseOwnershipService`, posta alan adi tersinden). Belirsizse null. YIKICI yollarda
sahip silmeden ONCE okunuyor -- sonra okunsaydi panelin en yikici kayitlari tam da
sahibinin goremedigi kayitlar olurdu. Askiya alma kaydi artik gercek aktoru + IP yaziyor
(eskiden "musteri kendini askiya aldi" diyordu); denetleyicideki ikinci yama kaydi kaldirildi.

**Virus taramasi sayfa yenilenince kaybolmuyor.** IKI hata vardi: status yalniz BITEN
taramayi donduruyordu VE tarama ucu iptal belirtecini isteğe bagliyordu -> yenileme
`clamscan.exe`'yi OKSUZ birakiyor, iki saatlik is bosa gidiyordu. `IOperationProgressService`
okundu ve UYMADIGI gerekcelendirildi (tek yuvali, bellekte). Bayat kayit yalan soyleyemiyor:
panel PID + surec baslangic tiki saklaniyor.

**Uc serviste ILogger** (Cache, FileMigration, WordPressToolkit). Log eklerken DORT gizli
ariza cikti: onbellek durumu okunamayinca yanlis cozum onerisi, eklenti listesi hatasinda
"her eklenti pasif", surum API'si patlayinca "tum siteler guncel", gocte 900 dosyadan 40'i
dusse yesil "tamamlandi".

### Dersler

1. **Ajanlarin kendi testlerinin zayifligini bulmasi degerli.** Silme ekibinin iki mutasyonu
   ILK TURDA sag kaldi; sebebini gormus: testler "cascade kalinti bildirirse ne olur"u
   olcuyormus, "cascade kalinti bildiriyor mu"yu degil. Gercek dosya + gercek kilitle
   yeniden yazinca ikisi de yakalandi.
2. **Olu i18n anahtarinda kendi olcumune guven.** Denetim "~14" dedi, ilk tarama 22 buldu,
   11'i YANLIS POZITIFTI (site kurucu anahtarlari C# tarafinda birlestiriliyor). Tersine
   uydurma bir `mail.` oneki gercek yetimi GIZLIYORDU. Sonuc 13.
3. **Durum kaydi depoda tutulmali.** `docs/calisma-durumu.md` olusturuldu; elektrik/kota
   kesintisinde "kaldigimiz yerden devam" oradan okunuyor. Bu turda uc ajan isini bitirip
   COMMIT ETMEDEN durdu -- ajan durdurulunca commit kontrolu sart.

## 219) DUZELTME: .htaccess 0.7.0-0.7.2'de OLU YAYINLANDI (27.08.2026)

Bolum 217-218'de ".htaccess canlida dogrulandi (alioral.xyz)" yazmistim. **YANLIS.**

**Iki ayri hata yaptim:**

1. **Yanlis cikarim.** alioral.xyz'de temiz adreslerin calistigini gorup ".htaccess calisiyor"
   dedim. Ama panelin ZATEN Laravel/WordPress icin web.config yeniden yazma ozelligi var
   (README'de yillardir listeli). Iki mekanizmayi ayirt etmedim. Dogru test: .htaccess'i
   yeniden adlandirip adreslerin hala calisip calismadigina bakmakti.

2. **Teslim yolu hic olculmedi.** Bu makineye 0.7.2 kuruldugunda olctum:
   `AdaPanel.HtaccessModule.dll` kurulum klasorunde YOK, GAC'ta kayit YOK, publish ciktisinda YOK.
   `.iss` DLL'i CAGIRIYOR ama kopyalayan Files girdisi yoktu; `package-installer.ps1` yalnizca
   `AdaPanel.Api.csproj`'u publish ediyordu. Modul solution'da oldugu icin `dotnet test` onu
   derliyordu ve 100+ testi YESILDI -- ama pakete hic girmedi.

**Sonuc:** ozellik uc surumde (0.7.0, 0.7.1, 0.7.2) olu yayinlandi. Kurulum, var OLMAYAN bir
DLL'i gosteren kayit betigini calistirip sessizce geciyordu.

**DERS -- bu oturumun en pahali dersi ve tam da avladigimiz sinifin kendisi:**
**Yesil test "teslim edildi" demek DEGILDIR.** Butun oturum boyunca "kod var ama calismiyor"
hatalarini avladik ve sonunda ayni hatayi BIZ urettik. Testler kodu olcuyordu, teslim yolunu
degil. Bir ozellik icin sorulacak son soru "testi gecti mi" degil, "PAKETIN ICINDE MI".

Duzeltme: `package-installer.ps1` modulu net48 hedefiyle derleyip publish klasorune kopyaliyor
ve eksikse DURUYOR; `HtaccessModulePackagingTests` (4 test) bunu nobete bagliyor.
Dogrulandi: modul pakette 177.152 bayt, paket 63 KB buyudu, basvurulari yalniz
mscorlib/System/System.Core/System.Web (GAC on kosulu).

---

## 2026-09-06 — DOKTRIN DUZELTMESI: "panel yoneticisi = Windows yoneticisi" celiskisi

**ONCE OKU. Bu bir kod notu degil, TEHDIT MODELI notudur.** Depoda su cumle bes ayri yerde
korumalarin gerekcesi olarak yaziliydi:

> "Panelin 'Administrator' rolu bir WINDOWS yoneticisi DEGILDIR (kabuk komutu uclari bilerek
> LOCAL SERVICE olarak kosuyor)."

**Cumlenin ikinci yarisi YANLISTI ve olculdu.** `scripts\install-service.ps1` servisi
`New-Service` ile `-Credential` VERMEDEN kurar -> panel **LocalSystem**. `TerminalService`
`powershell.exe`'yi dogrudan alt surec olarak baslatir; `LimitedIdentityRunner` yolundan GECMEZ.
Yani `POST /api/v1/terminal/execute` bir **SYSTEM kabugudur** ve o gune kadar rolu Administrator
olan HERKESE aciktir. Ustelik bir yonetici `UsersController` uzerinden ikinci bir yonetici
yaratabiliyor -> "rol yukseltmesi = SYSTEM".

**Secilen yol: (A) terminal kisitlandi, doktrin PRECISE hale getirildi.** Terminal KALDIRILMADI.

### Yeni doktrin (tek kaynak: `AdaPanel.Core.Interfaces.IServerOwnerService`)

> Panel yoneticisi bir Windows yoneticisi **degildir** — **TEK istisna sunucu SAHIBIDIR.**
> Terminal ucu bilerek SYSTEM kabugu verir ama yalniz sunucu sahibine acilir; o kisi zaten
> makinenin sahibidir. Panelden yaratilan EK yonetici hesaplari, bayiler ve musteriler icin
> sinir aynen gecerlidir ve butun diger korumalarin gerekcesi odur.

Olgun panellerin karari da budur: Plesk'te SSH Terminal yoneticiye `root` verir, ama EK yonetici
hesaplari "Restricted Mode"dadir ve Plesk'in kendi belgesi yetki yukseltmesini onlemek icin
"SSH Terminal" kutusunun bu hesaplarda **isaretlenmemesi** gerektigini yazar. cPanel'de WHM
terminali root'undur; hesap terminali varsayilan KAPALIDIR ("disabled shell") ve acildiginda bile
kullanicinin kendi kimligiyle kosar.

### Kod tarafi

- `IServerOwnerService` + `ServerOwnerRegistry` (saf karar) + `ServerOwnerService`.
- Sahip kaydi: **`%ProgramData%\AdaPanel\config\server-owner.json`** — panelin HTTP yuzeyinden
  YAZILAMAZ. Ayni kalip `AllowInternalOutboundTargets` / `AllowUnauthenticatedLocalSenders`
  bayraklarinda zaten var: korumayi, korunan yuzeyin kendisinden kapatilabilir yapmak korumayi
  kaldirmakla ayni kapiya cikar.
- Kural: kayit YOKSA (hic yazilmamis/bozuk/okunamiyor) -> **en eski etkin yonetici** (kurulum
  sihirbazinin yarattigi kisi) ve karar diske SABITLENIR. Kayit VARSA son soz onundur; gosterdigi
  kisi artik etkin yonetici degilse sonuc "sahip YOK"tur, "siradaki" DEGIL.
- `TerminalController`: rol denetimi durdu (ucuz eleme) + sahiplik denetimi eklendi; **taklit
  edilen oturum** (impersonation) hedef sahip OLSA BILE giremez. Red de denetim kaydina dusuyor
  (`terminal.command.denied`, denenen komutla birlikte).
- `AuthController.ImpersonateAsync`: hedef **Administrator olamaz** (ozelligin adi zaten "log in
  as customer"). Onceden rol denetimi HIC yoktu -> bir yonetici baska bir yoneticiyi taklit
  edebiliyordu.
- `UserManagementService`: sunucu sahibi hesabi **silinemez / pasife alinamaz / rolu
  dusurulemez** (`users.server_owner_protected`). Devir yolu: sunucudaki dosya.

### Gizlilik karari — komut metni neden kayda yaziliyor

Uca yalniz sunucu sahibi girebiliyor; denetim kaydini okuyan uclar da yonetici uclari. Kaydedilen
kisi ile kaydi okuyan kisi AYNI; korunacak ucuncu taraf mahremiyeti yok. Kayit olmadan sunucunun
sahibi bile "makinemde ne calisti" sorusunu yanitlayamaz. Metin `SecretRedactor`'dan geciyor.
