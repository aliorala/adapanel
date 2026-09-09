# Çalışma durumu — canlı kayıt

Bu dosya, oturum kapandığında veya başka bir bot devraldığında güncel sonucu gösterir. Aşağıdaki eski sürüm bölümleri tarihsel kayıttır.

Son güncelleme: **09.09.2026 — 0.8.37 canlı, ek düzeltmeler tamamlandı.** Kaynak commit'i `2c96972`. [Ayrıntılı devam ve kanıtlar](kalan-sorunlar-2026-09-09.md).

## 0.8.37 ek düzeltmeler

- Uzak aktarım iptalinde tamamlanmış yerel yedeğin kaybolan panel kaydı düzeltildi. Hash aktarımdan önce ölçülüyor; kayıt/yan indeks/içerik uyarıları yazıldıktan sonra iptal çağırana iletiliyor. Site, abonelik ve posta uyarıları ilk kayda dahil.
- Kaynak izleme ekranında eski yanıtın yeni isteğin kilidini kaldırıp üst üste sorgu başlatması düzeltildi. Gerçek Vue yaşam döngüsünde eski isteğin hem geç başarısı hem geç hatası test edildi.
- **5.262 .NET + 60 ön yüz testi**, typecheck ve build başarılı. FTP iptalinden sonra aynı arşivin aynı kayıtla yeniden gönderilmesi yerel TCP/FTP düzeneğinde doğrulandı. Canlıda yapay kesinti veya geri yükleme yapılmadı.
- Kompakt ana sayfa canlı masaüstünde gözden geçirildi; CPU/bellek/disk yazılarında taşma görülmedi. Yedekleme korunuyor; şifreleme kapalı kalıyor.
- **Canlı doğrulama:** mevcut/son sürüm 0.8.37; AdaPanel hizmeti Running/Automatic. Son kurulum succeeded/exitCode=0; Inno başarı 21:14:11.301, servis ayarı Restore ve Log closed 21:14:18.707 (sunucu yerel zamanı). Paket 90.762.779 bayt, SHA256 `521A8A418520C4A4C7433A08A76896BB004FFEC84C61F29C7A5385F7853E3A03`; yayınlanan dosya tekrar indirilip doğrulandı. 149 ön yüz dosyası eşleşti; 22 bileşen geçti, 3 büyük paket mevcut istisna ile atlandı. Kaynak/history/tag push edilmedi.
- **Canlı IIS eksikliği de düzeltildi:** `Web-Request-Monitor` kuruldu; Installed=false ve appcmd 80004001 durumu giderildi. Kurulum Success=true, Installed=true; IIS canlı görünümü artık “0 aktif / Şu an işlenen istek yok” gösteriyor. Sunucu yeniden başlatılmadı, AdaPanel/W3SVC/WAS çalışıyor. Bu sunucuda rol bileşeni etkinleştirildi; kurulum paketinin isteğe bağlı özellik listesine değişiklik yapılmadı.

## Önceki 0.8.36 sonucu

- Yedekleme modülü korunuyor; şifreleme kullanıcının açık tercihiyle kapalı. Kompakt görünüm korunuyor; bekleyen onay yok.
- Uygun başarısız FTP kaydını aynı arşivle yeniden gönderme eklendi. Eski iptvhub kaydında oluşturma anının hash'i yok; bu kayıt güvenli biçimde reddedildi ve yerel arşivi/geçmişi korundu. Eski dosyayı yeniden gönderilmiş saymayın.
- Yönetici artık yeni veritabanı yedeğini kayıtlı FTP hedefine, parolayı yeniden girmeden ve hedefi onaylayarak alabiliyor. Canlıda yalnız iptvhub için iki yeni kopya başarıyla yerel + FTP olarak alındı. Son kayıt `c3de560c02774c9aac73cbcdc0a74cfc`, önceki yeni kayıt `d6eb5f54008e4c6fb00cbf5367c422c7`; ikisi de remoteState=present. Son dosyanın kayıtlı/ölçülen SHA256'sı eşleşti.
- Onaydaki yanlış Sil etiketi, uyarıyı eksik içerik gibi gösteren bildirim, boş dosya adı ve yeni yedeği gizleyen eski filtre düzeltildi. Eski filtre + kapalı gün ile canlı akış kontrolü geçti; yeni kayıt görünür ve mesaj doğru.
- **62 kayıt doğrulandı: 0 yerel dosya yok, 18 kaydın yereli geçmiş silme işleminde kaldırılmış, 18 uzak kopya yok, 0 arşiv açılamadı, 0 kayıt ölçülemedi.** Eski 18 kaydın kayıp kopyaları geri oluşturulmadı ve geçmiş silinmedi. FTP yetim taraması desteklenmiyor. Gerçek geri yükleme yapılmadı.
- Son tam **5.260 .NET testi**, 67 ilgili kontrol ve ön yüz **58 test** başarılı. Son tam arka uç koşusu 0.8.35'tedir; ön yüz ağırlıklı 0.8.36'da typecheck/58 test/build tekrar geçti. Atlanan test/derleme uyarısı yok.
- **0.8.36 paket:** 90.763.324 bayt, SHA256 `6CED764AD5662C470FFD6C2D7016FA6B197B31D2D23CC7E193597575ED6F57AB`. Yayın ve canlı indirme doğrulandı; 149 ön yüz dosyası eşleşti, 22 bileşen geçti/3 büyük paket istisnası, içerik denetimleri geçti. Son manifestler bu sürümündür.
- Hizmet Running/Automatic; son kurulum succeeded, installerExitCode=0, isFinished=true/isFailed=false; Inno Restore ve Log closed 19:15:45.711. Kaynak geçmişi açık GitHub deposuna gönderilmedi.

## Önceki kayıt — 0.8.33 başarısız FTP aktarımını yeniden gönderme

- Yöneticiye mevcut yerel arşivi kayıtlı FTP hedefine yeniden gönderme işlemi eklendi. Hedef onaylanır; yer/başlangıç günü/boyut/hash kontrol edilir; yerel dosya ve diğer uyarılar korunur. Aynı kayda yazılır, çoğaltılmaz. API rolü ve aktör/kaynak sahibi denetim izi test edildi.
- Eski 18 kayıt, önceki silme işleminde yereli kaldırılmış `deleted_remote_failed` kayıtlarıdır. “0 yerel dosya yok” sonucu bunların yerel kopyası bulunduğu anlamına gelmez; bu durum artık ayrı sayaçla gösterilir. Geçmiş kayıtlar silinmedi.
- Paket 90.763.158 bayt, SHA256 `CC925CDA64D3F543D22A1CA81B0AFD68CBA241F3FA0E044272E5382995D394B7`. Son manifestler 0.8.33'e aittir. Yayın/canlı ve gerçek yeniden gönderme sonucu henüz bu satırda doğrulanmış sayılmaz.

## Önceki kayıt — 0.8.32

Kaynak commit'leri: `01ce3c9`, `c555ac4`.
Son tam .NET koşusu (0.8.32): **5.242 başarılı**, atlanan yok; derlemede **0 uyarı / 0 hata**. 73 ilgili kontrol geçti. 0.8.31'deki **58 ön yüz testi** başarılı; 0.8.32'de yalnız sürüm bilgisi değişen ön yüz yeniden derlendi. Typecheck/build başarılı.

### Kalan servis ve FTP sorunları düzeltildi, 0.8.32 canlıda

- Kullanıcı kalan sorunların düzeltilmesini istedi. Yedekleme modülü ve kompakt görünüm korunuyor.
- Kurulumda servis başlangıç ayarı saklanıyor, dosya değiştirme sırasında yeniden başlama engelleniyor, iptal/hata/başarıda ayar geri geliyor. Yalnız tam yolu eşleşen uygulama süreci kapatılır. Durdurma ve dosya kilidi doğrulanmadan dosyalar değiştirilmez.
- FTP geçici hatalarına sınırlı tekrar, asenkron zaman aşımı/iptal ve eski yedeklerde üst klasör yokluğunun kanıtlanması eklendi. Kalıcı yetki hataları “dosya yok” sayılmaz. Test uyarıları da düzeltildi.
- Canlı kurulum tek denemede `succeeded`, çıkış kodu 0; panel 0.8.31 ve servis Running/Automatic. Inno günlüğünde Stop/Restore başarılı. Yeni sürümde kayıtlı FTP hedefine gerçek test dosyası aktarımı geçti.
- Toplu doğrulama sırasında görülen geçici 421 soket hataları için 0.8.32'de listeleme/silme/boş dizin kaldırmaya da sınırlı tekrar eklendi. 0.8.32 canlı kurulumu da başarılı; panel güncel ve servis Running/Automatic. Son kayıtta installerExitCode=null (0 diye yorumlanmaz), succeeded/isFinished=true/isFailed=false. Inno günlüğü başarılı Stop/Restore ve kurulum tamamlanması sonrası 17:58:36'da kapandı.
- **Son canlı doğrulama: 60 kayıt; 0 yerel dosya yok, 18 uzak kopya yok, 0 arşiv açılamadı, 0 kayıt ölçülemedi.** Önceki 4 geçici erişim hatası bu koşuda yok. Eski uzak kopyaların yokluğu kanıtlandı; kayıtlar silinmedi, eksik uzak kopyalar yeniden oluşturulmadı. FTP yetim taraması desteklenmediğinden yapılmadı.
- Eski başarısız veritabanı aktarımının yerel arşivi korunuyor; bağlantı testi o arşivin yeniden aktarılması değildir. Önceki gece işinin hata özeti tarihsel kayıttır. Kullanıcı yedek şifrelemesi için **“Şimdilik kapalı kalsın”** dedi; ayar bu açık tercihle kapalı bırakıldı, bekleyen onay yok. Gerçek geri yükleme yapılmadı.
- [Ayrıntılı düzeltme, test ve canlı yayın kaydı](kalan-sorunlar-2026-09-09.md). Kanıtlar `.tmp/remaining-fixes/`; mevcut manifestler 0.8.32 paketini gösterir. Kaynak geçmişi açık GitHub deposuna gönderilmedi.

## Önceki kayıt — 0.8.29 hata düzeltmeleri ve 0.8.30 kompakt görünüm

- **Yedekleme kalacak.** Kullanıcı kaldırma isteğini başka projeyle karıştırdığını söyleyerek geri çekti. Modülün veya bağlantılarının kaldırılmasına devam etmeyin.
- 0.8.29 kaynak commit'i `68180e6`, yayın/doğrulama kaydı `56a86d4`. Oturum izolasyonu, yedek geri yükleme onayları ve hata raporu, dosya düzenleyicide kaydedilmemiş değişiklik koruması, site seçimi, rol kontrolleri, URL/geri-ileri gezinmesi, kurulum takibi ve dil paketlerinin yüklenmesi düzeltildi. Süreç testi zaman aşımı ve boş SSL çıktısı da ele alındı. Ayrıntı ve test kanıtları: [düzeltme kaydı](duzeltmeler-2026-09-09.md).
- 0.8.30 kaynak commit'i `03cbea2`, canlı görünüm kaydı `9577018`. Kompakt masaüstü yazı tabanı 14 → 13 px; kart, menü ve özet boşlukları küçültüldü. CPU/bellek/disk halka metinleri ortalandı, uzun açıklamalar satır kırıyor. Mobil başlıklar halka üstünde, yüzde içeride. Rahat görünüm ve mobil yazı tabanı 14 px kaldı. Ana dosyalar: `frontend/src/styles/base.css`, `frontend/src/views/DashboardOverviewView.vue`.
- 320/390/1024/1486 px genişlikler, iki dil/tema/yoğunluk seçeneği ve uzun kaynak değerleriyle görsel ölçüm yapıldı. Son durumda yatay sayfa taşması ve halka içindeki metinlerde taşma yok. Canlı sayfa yenilendi; arayüz/sunucu 0.8.30, kök yazı 13 px ve gerçek halka metinleri doğrulandı. IIS/WAS/FTP sağlık durumu çalışıyor. Ayrıntılar: [kompakt görünüm ve yayın kaydı](kompakt-gorunum-2026-09-09.md).
- Kullanıcının mevcut canlı yayın izniyle [0.8.30](https://github.com/aliorala/adapanel/releases/tag/0.8.30) yayımlandı ve kuruldu. Kurulum dosyası 90.746.342 bayt; SHA256 `10C78CA89291F580C24CEA891E0425BC5B60F48E83F603DA8EC123636190B0AA`. Yeniden indirilen yayın paketi ve canlı sunucunun paketi doğrulandı. 149 ön yüz dosyası publish ile eşleşti; 22 bileşen denetimi geçti, 3 büyük paket betiğin istisnasıyla atlandı.
- Ön yüz derleme/eşitlemesini **bitirdikten sonra** paketleyin; birlikte başlatmak eski statik dosya listesiyle paketlemeyi bozdu. Başarısız paketleme/indirme denemeleri yayımlanmadı; son zorunlu denetimler geçti. Kaynak geçmişi yerelde; açık GitHub deposuna yalnız mevcut yayın yöntemiyle paket/sürüm notları gönderildi. Kaynak dalını açık depoya push etmeyin.
- **Açık sınırlar:** 0.8.29 ilk kurulumunda hizmet kurtarmasının dosyayı yeniden kilitlemesi kod 5 ile geri almaya yol açtı; sonra sunucu kaynaklı deneme başarılı oldu. Bu yarışa kalıcı düzeltme yapılmadı. Önceden oluşmuş bir veritabanı yedeğinin uzak FTP aktarımı `421 Service not available` ile başarısız; yerel kopya var. Eski şifreleme uyarıları ve test analiz uyarıları çözülmüş sayılmamalı. Gerçek yedek geri yükleme testi yapılmadı.
- Genel devir: [CODEX_MEMORY.md](../CODEX_MEMORY.md). Geçici önizleme dosyaları ve geliştirme sunucusu kaldırıldı. Güncel manifestler 0.8.30 paketine aittir.

---

## Önceki kayıt — Git dağıtımı + arşiv içe aktarma

Bu bölüm önceki oturumun tarihsel kaydıdır. O oturumdaki “commit atılmadı” durumu güncel değildir; ilgili düzeltmeler `942dfe9` ile commit'lendi. Aşağıdaki karar/bulgu ayrıntıları bağlam olarak korunmuştur; en güncel yayın ve test durumu yukarıdadır.

efade94 cron `url` görevindeki yönlendirme SSRF'ini kapatmış, aynı sınıftan **iki noktayı ölçüp
açık bırakmıştı**. Bu tur ikisini de kapattı. Kalıp aynıydı: *adres kapıdan geçiyor ama isteği DIŞ
BİR ARAÇ atıyor ve yönlendirmeyi kendi izliyor.*

### İKİ İDDİA DA ÖLÇÜLDÜ — biri doğrulandı, biri ÇÖKTÜ

| Önceki oturumun iddiası | Ölçüm | Sonuç |
|---|---|---|
| "scp kalıbı `Uri` ile ayrıştırılamadığı için HİÇ denetlenmiyor" | .NET 8: `Uri.TryCreate("git@169.254.169.254:latest/meta-data", Absolute)` = **false**; kod o dalda `return` ediyordu | **DOĞRU** — koruma zayıf değil **SIFIR**dı |
| "`git.exe` yönlendirmeyi kendi izler" | git 2.54.0.windows.1 + gerçek loopback: `clone` → dış sunucu `302` → **iç sunucuya GERÇEK keşif isteği ULAŞTI** (`GET /ic.git/info/refs?service=git-upload-pack`) | **DOĞRU** |
| "`Invoke-WebRequest`'ten vazgeçmek `ftp://` desteğini düşürür" | PowerShell **5.1**'de `Invoke-WebRequest ftp://…` GERÇEKTEN çalışıyor (`FtpWebRequest` üretip bağlanıyor; bu makinede `530` yanıtı alındı). Ayrıca `ftp://` **kodda açıkça beyaz listede** ve arayüz onu VAAT EDİYOR (`panel.migration.urlHint`: "http/https/ftp ile erişilebilen bir .zip") | **AYAKTA** — ama gerekçe indirmenin TAMAMINI değil yalnız `ftp` dalını tutuyor |
| "`-MaximumRedirection 0` GitHub indirmelerini kırar" | Doğru ama KONU DIŞI: yönlendirmeyi kapatmak gerekmiyor, **denetlemek** gerekiyor | Çare bu değildi |

`git.exe`'nin `http.followRedirects` varsayılanı yapılandırılmamış (git'in gömülü varsayılanı
`initial`) — yani ilk keşif isteği yönlendirmeyi İZLİYOR. `-c http.followRedirects=false` ile aynı
düzenekte iç sunucunun istek sayısı **0** oldu.

**İkinci katman ÇALIŞIYOR (davranışla ölçüldü, artık kaynak taramasıyla değil):**
`GIT_ALLOW_PROTOCOL=http:https:ssh` sayesinde `ext::cmd /c …` → `fatal: transport 'ext' not allowed`
(işaret dosyası OLUŞMADI), `file://…` ve şemasız `C:\…` yolu → `fatal: transport 'file' not allowed`.

### NOKTA 2 — `GitDeploymentService` (öncelik: koruma sıfırdı)

* Yeni `src/AdaPanel.Infrastructure/Git/GitRepositoryAddress.cs`: **TEK** ayrıştırıcı. scp deseni
  `ValidateRepositoryUrl`'den buraya TAŞINDI (kopyalanmadı) — "kabul edilen biçim" ile "denetlenen
  konak" artık tanım gereği aynı eşleşmeden geliyor.
* Ayrıştırılamayan adres artık **"temiz" değil REDDEDİLİYOR** (`unparsable_host`). Açık kapı
  açıkken bu red uygulanmaz (meşru iç kurulumu kırmamak için).
* Kapı artık **dağıtım anında da** çalışıyor, yalnız kaydetmede değil: diskteki eski/bozuk
  yapılandırma ve DNS rebinding penceresi kapandı. Webhook ucu ANONİM olduğundan tetikleme zaten
  saldırganın elindeydi.
* Yönlendirme: `git`'e **`-c http.followRedirects=false`** veriliyor (komut satırı en yüksek
  öncelik; kiracının düzenleyebildiği bir dosya bunu YENEMEZ). **Meşru yönlendirme KIRILMADI**,
  çünkü zinciri artık PANEL çözüyor — `ResolveHttpRepositoryUrlAsync` keşif adresini
  `OutboundConnectGuard.GuardedClientName` istemcisiyle çeker, **her adım** `ConnectCallback`'ten
  geçer, git'e zaten çözülmüş NİHAİ adres verilir. Ağ arızasında adres DEĞİŞTİRİLMEZ (özgün adres
  kullanılır); ön çözümleme bir kolaylıktır, koruma değil.

### NOKTA 1 — `SiteHostingSettingsService.ImportSiteArchiveAsync` (uç **Bayi**'ye açık)

* `http/https` indirmesi PowerShell'den ALINDI → `OutboundConnectGuard.GuardedClientName`
  istemcisiyle akışa (stream) iniyor. Yönlendirme KAPATILMADI, **her adımı denetleniyor**; DNS
  rebinding de kapandı (denetlenen adres = bağlanılan adres).
* `ftp://` **bilinçli olarak** PowerShell'de kaldı: ölçüm gerekçeyi ayakta tuttu ve arayüz bu
  desteği kullanıcıya vaat ediyor. **KALAN SINIR AÇIKÇA YAZILI:** ftp yolunda yalnız ön kapı var.
  FTP'de yönlendirme mekanizması YOKTUR (yani kapatılan açık orada zaten yok); kalan artık risk
  DNS rebinding ve kötü bir FTP sunucusunun PASV yanıtıyla veri kanalını başka adrese göstermesidir.
* PowerShell script'i artık `BuildImportScript(ftp, …)` ile kuruluyor — "http artık PowerShell'den
  inmiyor" iddiası **kurulan komut satırından** ölçülebiliyor.

### Mutasyon (her koruma için: kaldır → KIRMIZI → geri koy; hepsi geri alındı, `diff` ile doğrulandı)

| Kaldırılan koruma | Kırmızı test |
|---|---|
| scp konak çıkarımı → eski `Uri`-only gövde | **10** |
| `RunDeployAsync` başındaki dağıtım-anı kapısı | **1** (`Dagitim_ANINDA_ic_adrese_cozulen_depo_REDDEDILIR`) |
| `BuildGitArguments` → `http.followRedirects=false` atıldı | **1** (`Git_yonlendirmeyle_ic_aga_SAPAMAZ`) |
| `ResolveHttpRepositoryUrlAsync` → özgün adresi dönüyor | **2** |
| Arşiv indirmesi yeniden `Invoke-WebRequest`'e alındı | **3** |

**Fazla-kapatma nöbetçileri mutasyon altında YEŞİL kaldı** — yani korumanın kopyası değiller:
`Mesru_depo_adresleri_KAPIDAN_GECER` (GitHub/GitLab, scp dâhil 6 biçim),
`Yonlendirmesiz_mesru_depo_git_ile_OKUNABILIYOR` (gerçek `git ls-remote`, çıkış 0),
`Mesru_yonlendirmeyi_PANEL_cozer_ve_nihai_adresi_git_e_verir`,
`Mesru_arsiv_indirmesi_yonlendirmeyi_IZLER_ve_dosyalar_KOPYALANIR` (302 → gerçek zip → siteye
kopyalandı), `Arsiv_scripti_http_icin_indirme_ADIMI_ICERMEZ_ftp_icin_ICERIR`.

### Dokunulan dosyalar

* `src/AdaPanel.Infrastructure/Git/GitRepositoryAddress.cs` (yeni)
* `src/AdaPanel.Infrastructure/Git/GitDeploymentService.cs`
* `src/AdaPanel.Infrastructure/Iis/SiteHostingSettingsService.cs`
* `tests/AdaPanel.Infrastructure.Tests/GitVeArsivYonlendirmeTests.cs` (yeni, 27 test)
* `tests/AdaPanel.Infrastructure.Tests/SahteHttpSunucu.cs` (yeni — loopback sunucusu
  `OutboundRedirectGuardTests`'ten TEK KAYNAĞA çıkarıldı, ikinci nüsha üretilmedi)

### KAPATILMAYANLAR (dürüstçe)

* **`GitController` sahip denetimi yapmıyor.** `[Authorize(Roles = "Administrator")]` — yani Bayi'ye
  KAPALI (önceki oturumun "Bayi'ye açık" notu bu uç için YANLIŞ; ölçüldü). Ama diğer site uçlarının
  aksine `CustomerAccess.EnsureSiteAsync` çağrılmıyor: sahip olmayan bir yönetici BAŞKASININ
  sitesine dağıtım yapılandırabilir. Bu turun konusu değildi, ayrı iş.
* **Arşiv `ftp://` yolunda DNS rebinding + PASV veri kanalı** açık (yukarıda gerekçesiyle yazılı).
* **`ImportSiteArchiveAsync` ön kapısı `Panel:AllowInternalOutboundTargets` açık kapısını
  YOKSAYIYOR** (`OutboundTargetPolicy.Strict` sabit). Bu daha SIKI bir davranış, güvenlik açığı
  değil — ama panelin geri kalanıyla tutarsız: iç ağdaki bir NAS'tan arşiv indirmek, işletici kapıyı
  açsa bile mümkün değil. Bilerek dokunulmadı.
* Ölçüm için kullanılan tek kullanımlık .NET projesi scratchpad'de duruyor
  (`…/scratchpad/gitolcum/`). Silinmedi; temizlenmesi gerekiyorsa elle yapılmalı.

---

## SON İŞ — CRON `url` GÖREVİ ARTIK curl İLE KOŞMUYOR: YÖNLENDİRME SSRF'İ KAPANDI (commit ATILMADI)

**ÖLÇÜLDÜ, tahmin değil.** Kurulan komut satırı okundu ve curl 8.19.0 ile gerçek bir loopback
sunucusuna karşı çalıştırıldı. Eski komut birebir şuydu:

```
curl.exe -fsSL --proto "=http,https" --proto-redir "=http,https" --max-redirs 5 --max-time 300 --retry 2 "<adres>"
```

`-L` VARDI → yönlendirmeyi curl'ün kendisi izliyordu ve **curl'de adres engel listesi YOKTUR**.
Ölçüm: bu birebir argümanlarla çağrılan curl, `302 Location:` ile gösterilen İÇ adrese gitti ve iç
gövdeyi yazdı — **çıkış kodu 0**. `OutboundAddressGuard` yalnız görev KURULURKEN ilk adresi
görüyordu; yönlendirme arkasından geçiyordu. Görev SYSTEM olarak koşuyor.

### Neden curl bayrağıyla kapatılamadı (üçü de ölçüldü)

| Aday | Ölçüm | Karar |
|---|---|---|
| `-L`'yi at | 302 alan curl **çıkış kodu 0 + BOŞ çıktı** veriyor → `http→https`/`www` yönlendirmeli her meşru cron "başarılı" görünüp hiç çalışmaz | **RED** (fazla kapatma) |
| `--proto-redir "=http,https"` | `file://` yönlendirmesini GERÇEKTEN kesiyor | Yararlı ama yetersiz — `http→http` ile iç ağa sapmayı görmez |
| `--resolve` | yalnız YAZILAN adı sabitler; yönlendirme BAŞKA bir ada gider, o sabitlenmemiştir | **RED** (yönlendirmeye çare değil) |

### SEÇİLEN ÇÖZÜM: görevi panelin DENETLENEN taşıyıcısına taşımak

`curl.exe` → **`AdaPanel.Api.exe --cron-url "<adres>"`**

* Yeni: `src/AdaPanel.Infrastructure/Scheduler/UrlTaskRunner.cs` (fiil sabiti + komut kurma +
  çekme), `src/AdaPanel.Api/Scheduler/CronUrlKomutu.cs` (CLI karşılayıcı, `Program.cs`'te tepsi ve
  parola kurtarma komutlarıyla aynı yerde — web sunucusu kurulmadan ÖNCE `Environment.Exit`).
* Kural TEKRAR YAZILMADI: `OutboundConnectGuard.CreateHandler` + `OutboundAddressGuard.InspectAsync`
  çağrılıyor. `ConnectCallback` **HER BAĞLANTIDA** çalışır → yönlendirmenin her adımı denetlenir.
* **İKİ açık birden kapandı:** (1) yönlendirme — üstelik yönlendirmeyi KAPATMADAN, yani meşru
  `http→https`/`www` cron'ları çalışmaya devam ediyor; (2) **DNS rebinding** — denetim artık ÇALIŞMA
  ANINDA yapılıyor ve ad BİR KEZ çözülüp soket TAM O ADRESLERE açılıyor.
* curl davranış paritesi korundu: çıkış kodları curl'ünkilerle aynı (0/2/6/7/22/28/47) çünkü
  kullanıcıların izleme betikleri bunlara bakıyor; `-f` (400+ → hata, gövde yazılmaz), `--max-time
  300`, `--retry 2`, `--max-redirs 5` karşılıkları var. Adres yasağı için curl'ün ÜRETEMEYECEĞİ
  ayrık bir kod: **93**.
* **GERİYE UYUMLULUK:** eski `curl.exe` görevleri hâlâ `url` tipinde tanınıyor
  (`UrlTaskRunner.IsUrlAction`). Tanınmasaydı listede "command"a düşer, URL sütunu boşalır ve
  kullanıcı kaydettiğinde görev ADRESİNİ KAYBEDERDİ. Eski görev düzenlenip kaydedilince yeni
  biçime taşınır.

### GERÇEK BİNARY ile ölçüm (birim testi değil)

| Senaryo | Çıkış kodu | Sonuç |
|---|---|---|
| Varsayılan politika, hedef `127.0.0.1` | **93** | soket HİÇ açılmadı |
| Açık kapı AÇIK, düz adres | **0** | `DIS-SERVIS-CEVABI` — meşru görev çalışıyor |
| Açık kapı AÇIK, 302 yönlendirme | **0** | zincir sonuna kadar izlendi |
| `file:///C:/Windows/win.ini` | **2** | şema reddedildi |

### Mutasyon (her koruma için: kaldır → KIRMIZI → geri koy)

| Kaldırılan | Kırmızı |
|---|---|
| `OutboundConnectGuard.ConnectAsync` → `BlockedTarget` dalı | **16 test** (ikisi yeni: `Url_gorevi_yonlendirmeyle_ic_aga_SAPAMAZ`, `Url_gorevi_CALISMA_ANINDA_denetlenir_soket_hic_acilmaz`) |
| `ResolveAction` url dalı curl'e geri alındı | **2 test** |
| `IsUrlAction` içindeki curl tanıma (geriye uyumluluk) | **2 test** |

### AYNI SINIF — İKİSİ DE ARTIK KAPANDI (09.09.2026; ayrıntı en üstteki bölümde)

Kalıp: *adres kurulurken `OutboundAddressGuard`'dan geçiyor ama isteği DIŞ BİR ARAÇ atıyor ve o araç
yönlendirmeyi kendi izliyor.*

1. ~~**`SiteHostingSettingsService.ImportSiteArchiveAsync`**~~ → **KAPANDI.** `http/https` indirmesi
   denetlenen `HttpClient`'a taşındı. Aşağıdaki "`ftp://` desteğini düşürür" gerekçesi ÖLÇÜLDÜ ve
   yalnız `ftp` dalı için ayakta kaldı; o dal bilerek PowerShell'de bırakıldı, kalan sınır yazıldı.
2. ~~**`GitDeploymentService`**~~ → **KAPANDI.** scp kalıbı artık kapıdan geçiyor (`Uri` ile
   ayrıştırılamadığı ÖLÇÜLDÜ, iddia doğruydu), kapı dağıtım anında da çalışıyor ve yönlendirmeyi
   git değil PANEL çözüyor.

Guard'ın çağrıldığı ve isteği de PANELİN attığı (yani sağlam) yollar: webhook + SMTP
(`NotificationService`), yedek hedefi (`BackupService`), göç (`SiteMigrationService`,
`FileMigrationService`), küme (`ClusterHttpClientFactory`), ve artık cron `url`.
Guard'ın çağrılmadığı ama adresin KULLANICIDAN GELMEDİĞİ (yani yüzey olmayan) yollar: bileşen
indirme, kendini güncelleme, mağaza, PHP redis/wp-cli indirmesi, lisans, Stalwart, webmail, sağlık
kontrolü, phpMyAdmin vekili.

---

## SON İŞ — DÖRDÜNCÜ YOL: CRON GÖREVİ ARTIK HERKESE SYSTEM VERMİYOR (commit ATILMADI)

552b028 terminali, 7b71f0e ise geri yükleme / mağaza kurulumu / site fiziksel yolunu kapattı.
**Dördüncü yol açıktı ve o açıkken diğer üçü kâğıt üstündeydi.**

| Yol | ÖLÇÜLEN durum | Durum |
|---|---|---|
| `POST /cron` (yönetici dalı) | tip zorlanmıyordu: `taskType="command"` → komut doğrudan `New-ScheduledTaskAction`'a, principal `-UserId 'SYSTEM' -RunLevel Highest` (`SchedulerService.BuildCreateScript`). Sahip OLMAYAN yönetici tek istekle SYSTEM'de kod çalıştırıyordu | **KAPANDI** |
| `POST /sites/{ad}/hosting/queue/start` · `scheduler/start` | uç `[Authorize(Roles = "Administrator,Reseller")]` — yani **KİRACI (bayi)** tetikliyor; panel `php.exe artisan …` görevini **SYSTEM** olarak kuruyordu. Çalışan kod sitenin KENDİ kodu ve kiracı onu FTP/Git ile zaten yazabiliyor | **KAPANDI** (bu turda ölçüldü, aranan şey değildi) |
| Mağaza kurulumunun Laravel zamanlayıcısı (`AppStoreService`) | aynı desen | **KAPANDI** |
| `PUT /panel-settings` → `DefaultBackupRoot` | hiç doğrulanmıyordu; her yedeğin yazılacağı kök HTTP gövdesinden seçilebiliyordu (arşiv tamperlemenin kalan yolu) | **KAPANDI** |

### SEÇİLEN YOL: (A) KİMLİĞİ DÜŞÜR — yetkiyi daraltmak DEĞİL

Depoda sınırlı kimlikle görev çalıştırma altyapısı **zaten vardı ve bu makinede canlı
doğrulanmıştı** (`ScriptTaskPlanner.LimitedUserId` = `NT AUTHORITY\LOCAL SERVICE`,
`LimitedIdentityRunner`, `SchedulerService.GrantLimitedIdentityAccessAsync`). İkinci bir mekanizma
İCAT EDİLMEDİ. Sonuç: sahip olmayan yönetici aynı görevleri kurmaya devam eder, yalnız görev
SYSTEM yerine sınırlı kimlikle koşar. (B) yolu (tipleri yalnız sahibe açmak) seçilseydi yönetici
yedek/bakım/PHP görevlerini tamamen kaybederdi.

* Yeni tip: `Core/Dtos/Scheduler/ScheduledTaskPrivilege.cs` (`Full` / `Limited`).
* `ISchedulerService.CreateTaskAsync` parametresi **ZORUNLU, varsayılansız** — yeni bir çağıran
  eklenirse ceza "derleme hatası" olsun, "sessiz açık" değil. (Beş çağrı yeri de gözden geçirildi.)
* Karar TEK YERDE: `SchedulerService.RequiresLimitedIdentity(tip, runAs, yetki)`. İki sebep orada
  birleşti: kullanıcının SEÇTİĞİ sınırlı kimlik (script) + çağıranın yetkisinin ZORLADIĞI kimlik
  (command/php/script).
* `url` KAPSAM DIŞI ve bu fazla kapatmama kararı: program sabit (`curl.exe`), argümanları panel
  kurar, adres `OutboundAddressGuard`'dan geçer — keyfi kod değil. Müşteri/bayi görevleri yalnız bu
  tipte, yani **kiracının hiçbir görevi kırılmadı**.
* Sınırlı görev bir SİTEYE aitse site kökü açılır (`GrantLimitedIdentityAccessAsync` — aynı izni
  `LimitedIdentityRunner` composer/npm/artisan için zaten veriyor). İzin verilecek klasör **IIS'ten
  çözülür**, istekteki serbest bir yoldan alınmaz.
* Denetim kaydı artık kimliği **GERİ OKUNAN** görevden yazar (`task.RunAs`), istekteki "system"
  dizesinden değil — ve artık her tipte yazar.

### `IsBareInteractiveShell` → `IsNonTerminatingShell` (karar)

**Bu bir güvenlik sınırı DEĞİL ve öyle kullanılmamalı.** `cmd.exe /c <herhangi bir şey>` bu
denetimden geçer, geçmesi de DOĞRUDUR: sorduğu soru "bu komut tehlikeli mi" değil, "bu görev
bitecek mi". Eski adı ve yeri yanlış bir güvenlik hissi veriyordu; yetki sınırı komut süzgecinde
değil görevin KİMLİĞİNDE (yukarıdaki karar). Denetim kendi işini de tam yapmıyordu: bitmeyen
kabuğun asıl biçimleri argümanlıdır — `cmd.exe /k …` ve `powershell.exe -NoExit …` komuttan sonra
kabuğu AÇIK bırakır. İkisi eklendi; bayrak yalnız KABUĞUN kendi bölümünde aranır
(`cmd /c xcopy … /K` meşrudur ve kabul edilir).

### `DefaultBackupRoot` — yeni kural yazılmadı

Karar `PanelWriteTargetGuard`'a (site yolu / geri yükleme hedefi / mağaza yolu ile AYNI kapı)
soruluyor. Tek fark, panelin KENDİ varsayılanı olan `<veri kökü>\backups` ağacının açıkça kabul
edilmesi: genel kural panelin veri kökünü (haklı olarak) reddeder ve bu carve-out olmasaydı
hiçbir şeyi değiştirmeden Ayarlar formunu kaydeden her yönetici kilitlenirdi. Ayrı disk hedefleri
(`D:\yedekler`) zaten kabul.

### MUTASYON (dokuzu da yapıldı, kırmızı GÖRÜLDÜ, geri konuldu)

| mutasyon | kırmızı test |
|---|---|
| Sahiplik denetimi kaldırıldı (herkes `Full`) | **3** |
| Taklit oturumu denetimi kaldırıldı | 1 |
| `RequiresLimitedIdentity`'de yetki dalı kaldırıldı | 2 |
| Bitmeyen kabuk denetimi eski hâline (yalnız argümansız) | 5 |
| `EnsureBackupRootIsWritable` çağrısı kaldırıldı | 3 |
| Kiracının Laravel görevi yine `Full` | 2 |
| **FAZLA KAPATMA:** herkes `Limited` (sahip dahil) | 3 |
| **FAZLA KAPATMA:** `url` de düşürülüyor (kiracı görevi) | 1 |
| **FAZLA KAPATMA:** yedek kökü carve-out'u kaldırıldı (her şey ret) | 2 |

### Testler

Yeni: `CronTaskIdentityTests.cs` (24) — gerçek `CronController` çalıştırılır, zamanlayıcıya
GERÇEKTEN ne gittiği ölçülür; kimlik kararı Windows'a yazılacak PowerShell betiğinden okunur.
Yeni: `LaravelToolkitTaskIdentityTests.cs` (4) — gerçek `SiteHostingSettingsService`.
Yeni: `PanelBackupRootGuardTests.cs` (6) — gerçek `PanelPreferencesService` + gerçek dosya deposu.

Fazla kapatma nöbetçileri: sunucu sahibi HÂLÂ SYSTEM görevi kurabiliyor (3 tip) · sahip olmayan
yöneticinin görevi REDDEDİLMİYOR, alanları temizlenmiyor · kiracının `url` görevi aynen çalışıyor ·
kullanıcının kendi seçtiği sınırlı kimlik kuralı değişmedi · `cmd /c xcopy /K` hâlâ kabul ·
panelin kendi yedek klasörü ve ayrı disk hedefleri hâlâ kabul.

**5165 test yeşil / 0 kırmızı** (Core 2 + Infra 5163; baseline 5131 + 34 yeni).
Frontend: typecheck/build/`npm test` (42) temiz, i18n TR/EN 5228 = 5228.

Değişen dosyalar: `src/AdaPanel.Core/Dtos/Scheduler/ScheduledTaskPrivilege.cs` (yeni),
`src/AdaPanel.Core/Interfaces/ISchedulerService.cs`,
`src/AdaPanel.Api/Controllers/CronController.cs`,
`src/AdaPanel.Infrastructure/Scheduler/SchedulerService.cs`,
`src/AdaPanel.Infrastructure/Iis/SiteHostingSettingsService.cs`,
`src/AdaPanel.Infrastructure/AppStore/AppStoreService.cs`,
`src/AdaPanel.Infrastructure/Services/PanelPreferencesService.cs`,
`frontend/src/i18n/tr.json`/`en.json` (+1 anahtar, `cron.bare_shell` metni güncellendi),
üç yeni test dosyası + altı test dosyasında imza/uyum düzeltmesi.

### AÇIK KALAN (dürüstçe)

* **LOCAL SERVICE sıfır yetki DEĞİLDİR.** `SeImpersonatePrivilege` taşıyan hesaplarda bilinen bir
  jeton taklidi ailesi (Potato/PrintSpoofer) vardır; görev jetonunun bu ayrıcalığı taşıyıp
  taşımadığı **bu turda ÖLÇÜLEMEDİ** (gerçek zamanlanmış görev oluşturmak yasaktı). Bu, panelin
  kiracıya composer/npm için ZATEN verdiği sınırlı kimliğin ta kendisidir — yani yeni bir zayıflık
  değil; ama "SYSTEM'den indi" demek "artık yükselemez" demek DEĞİL. Sınırı gerçekten kaldırmak
  ayrı bir iş: panelin sınırlı kimliğini (her yerde) ayrıcalıksız yerel bir hesaba taşımak.
* **Site kökü izni (`GrantLimitedIdentityAccessAsync`) canlıda ölçülmedi** — `icacls` çağrısı
  PowerShell gerektiriyor; birim testi bu yolu çalıştırmıyor. Kullanılan kod yolu, betik
  görevlerinde zaten kullanılan ve canlı doğrulanmış olan yolun aynısı.
* **Sunucu betiği modunda (site seçilmemiş) sınırlı görev izin ALMAZ** ve bu bilinçli: izin
  verilecek klasör yöneticinin yazdığı keyfi bir yoldur. Betik varsayılan ACL'li bir klasördeyse
  çalışır, kilitli bir klasördeyse görev hata koduyla düşer — sessizce değil, panel son sonucu ve
  çıktıyı gösteriyor.
* **Var olan görevler dönüştürülmedi.** Bu koruma YENİ kayıtlarda çalışır; sahip olmayan bir
  yöneticinin daha önce kurduğu SYSTEM görevi yerinde durur. Toplu dönüştürme
  (`RepairTaskSettings` benzeri bir "kimlikleri düşür" turu) ayrı iş olarak raporlandı.
* **`<veri kökü>\yedekler` gibi özel adlar reddedilir** (yalnız `backups` ağacı kabul). Bilinçli:
  ikinci bir "hassas alt klasör" listesi yazmamak için. Hata mesajı ne yapılacağını söylüyor.

---

## ÖNCEKİ İŞ — SAHİPLİK SINIRINI DELEN ÜÇ YAZMA YOLU KAPATILDI (commit ATILMADI)

552b028 terminali sunucu sahibine kısıtladı. O korumanın **tek dayanağı**
`config/server-owner.json` dosyasının panelin HTTP yüzeyinden **yazılamaması**. Üç uç bu dayanağı
çürütüyordu — üçü de `Administrator` rolüne açık, panel `LocalSystem`:

| Yol | Ölçülen durum | Durum |
|---|---|---|
| `POST /backup/restore` · `restore-items` | `TargetPath` MUTLAK ve DENETİMSİZ (`BackupService.cs:3013`, `3200`) → arşiv panelin veri köküne açılıp sahiplik kaydı ezilebiliyordu | **KAPANDI** |
| Panel yedeğindeki `tasks/*.xml` | XML'in İÇERİĞİ hiç denetlenmeden `Register-ScheduledTask -Xml`'e veriliyordu → arşive konan tek XML SYSTEM'de komut kurar | **KAPANDI** |
| `POST /store/install` | `targetPath` mutlak olabiliyordu, site kökü denetimi yoktu; `cleanInstall` hedefi boşaltıyor | **KAPANDI** |
| `POST /sites` **ve** `PUT /sites/{ad}/document-root` | yönetici dalında fiziksel yol kısıtı YOKTU; `PhysicalPath = C:\` → dosya yöneticisinin sanal alanı TÜM DİSK | **KAPANDI (ikisi de)** |

**KARAR — sınır neden "sistem alanı", "site kökü" değil.** Yöneticinin ayrı diske site kurması
(`D:\siteler\musteri.com`), yedeği kendi klasörüne açması (`E:\geri-yukleme`) MEŞRUDUR; site köküne
hapsetmek özelliği öldürürdü. Reddedilen şey Windows / Program Files / ProgramData / **panelin veri
kökü** / sürücü kökü / UNC ve ortak kapsayıcının (`C:\inetpub\wwwroot`, `C:\Users`) KENDİSİ.

**TEK KAYNAK.** Yeni `Infrastructure/Security/PanelWriteTargetGuard.cs`, sistem yolu listesini
`SiteDeletionPathGuard.SystemForbiddenSubtrees/Containers`'tan OKUR — ikinci bir liste kopyası
yazılmadı (o listeler zaten ölçülmüştü: eski kara liste `C:\inetpub`, `C:\Users`, `C:\ProgramData`
ve UNC'yi GEÇİRİYORDU). `SiteDocumentRootScope` KULLANILMADI ve sebebi yazılı: o sınır KİRACI
kapsamıdır, yöneticinin kapsamı yoktur.

**GÖREV XML'İ — sahip/sahip olmayan ayrımı.** Kayıt artık YALNIZ sunucu sahibinin geri
yüklemesinde yapılır. "SYSTEM principal'lı XML'i reddet" REDDEDİLDİ: panelin KENDİ görevleri
bilerek `UserId 'SYSTEM' … RunLevel Highest` ile kurulur, o kural meşru bir yedeğin BÜTÜN
görevlerini (gecelik yedek dahil) düşürürdü. Sahip olmayanda iş sessiz değil: görev başına
`backup.restore_task_owner_required` uyarısı + durum `restored_with_errors` + XML çıkarma
klasöründe DURUYOR. Sahiplik `IServerOwnerService`'ten okunur; bağımlılık yoksa sonuç **"sahip
değil"** (fail-closed).

**Dokunulan yerler:** YENİ `src/AdaPanel.Infrastructure/Security/PanelWriteTargetGuard.cs` ·
`Backup/BackupService.cs` (`IstenenHedefiDenetle`, `CaginSahibiMiAsync`, `GorevKaydetAsync`,
`TryRestoreScheduledTasksAsync` artık `internal` + kaydedici temsilcisi alıyor) ·
`AppStore/AppStoreService.cs` (`ResolveDestinationAsync`) · `Api/Controllers/SitesController.cs`
(`EnsureAdminPathIsWritable`, iki uçta) · `Iis/SiteDeletionPathGuard.cs` (dört yardımcı `internal`).
i18n: 4 yeni anahtar, tr/en **5227/5227**.

**Testler:** YENİ `PanelWriteTargetGuardTests` (21) + `SunucuSahipligiYazmaSiniriTests` (20) — hepsi
DAVRANIŞ testi, kaynak taramıyor. Mevcut 5 test kırmızıya düşmüştü: geri yükleme hedefini
KOLAYLIK olsun diye panelin veri kökü altında seçiyorlardı (üretim UI'si hedefi hep boş gönderir);
hedefleri veri kökünün dışına alındı, ölçtükleri şey değişmedi.

**MUTASYON (8 koruma, tek tek kaldırıldı → KIRMIZI → geri kondu):**
sistem alt-ağacı denetimi **9** · `RestoreAsync` hedefi **4** · `RestoreItemsAsync` hedefi **1** ·
görev XML sahiplik kapısı **2** · mağaza hedefi **2** · belge kökü (yönetici) **2** · site
oluşturma (yönetici) **1** · sahiplik varsayılanı fail-open yapıldı **1**.

### KAPATILMAYAN — DÖRDÜNCÜ YOL: `POST /cron` (aynı sınıf, AÇIK)

`CronController.CreateTaskAsync` yönetici dalında `TaskType`'ı zorlamıyor. `taskType="command"` ile
`Command`/`Arguments` **doğrudan** `New-ScheduledTaskAction`'a geçiyor ve
`SchedulerService.BuildCreateScript` principal'i `UserId 'SYSTEM' -RunLevel Highest` yapıyor. Tek
süzgeç `IsBareInteractiveShell` ve o yalnızca **argümansız** `cmd.exe`/`powershell.exe`'yi
reddediyor — `cmd.exe` + `/c ...` GEÇİYOR.

**Sonuç:** sahip olmayan bir panel yöneticisi hâlâ tek bir cron isteğiyle SYSTEM olarak komut
çalıştırabiliyor; yani terminal sınırı ve yukarıdaki üç kapı bu yol açıkken de kâğıt üstünde
kalıyor. **Kapatılmadı** çünkü karar ürün seviyesinde: "yönetici cron'u sahibe mi kısıtlanacak,
yoksa `command`/`php`/`script` tipleri mi sahibe özel olacak" sorusu, meşru yönetici kullanımını
doğrudan etkiliyor (Plesk'te ek yöneticiler Restricted Mode'dadır — aynı yönde bir emsal var).
**Bir sonraki iş bu olmalı.**

---

## ÖNCEKİ İŞ — TERMİNAL = SYSTEM KABUĞU; artık YALNIZ SUNUCU SAHİBİNE açık + DOKTRİN DÜZELTİLDİ (commit ATILMADI)

**ÖLÇÜLEN ÇELİŞKİ.** Depoda altı ayrı yerde koruma gerekçesi olarak şu cümle yazılıydı:
"Panelin 'Administrator' rolü bir WINDOWS yöneticisi DEĞİLDİR (kabuk komutu uçları bilerek
LOCAL SERVICE olarak koşuyor)". **İkinci yarısı yanlıştı:**

| Ölçüm | Sonuç |
|---|---|
| `scripts\install-service.ps1` -> `New-Service` (`-Credential` YOK) | panel servisi **LocalSystem** |
| `TerminalService.ExecuteAsync` | `powershell.exe` doğrudan alt süreç; `LimitedIdentityRunner` yolundan GEÇMİYOR |
| `TerminalController` yetkisi (eski) | `[Authorize(Roles = "Administrator")]` — rolü Administrator olan **herkes** |
| Kaç yönetici olabilir? | Sınırsız; bir yönetici `UsersController`'dan ikinci bir yönetici yaratabiliyor (BAYİ yaratamaz: rol Customer'a zorlanıyor, `UserManagementService.cs:120`) |
| `AuthController.ImpersonateAsync` hedefi (eski) | rol denetimi **HİÇ YOKTU** -> yönetici, yöneticiyi taklit edebiliyordu |
| MCP tarafı | `ITerminalService` bilerek enjekte edilmiyor -> tek kapı REST; API anahtarı ayrıca `AllowTerminal` istiyor |

**Doktrinin gerekçe olarak kullanıldığı YERLERİN TAM LİSTESİ (6 koruma, 3'ü değil):**
`UpdatePackageTrust.cs` (güncelleme kaynağı beyaz listesi — ana metin) · `SelfUpdateService.cs`
(`SetGithubRepoAsync`) · `UpdateController.cs` (gövdeden gelen url/sha yok sayıldı) ·
`DataRootHardeningService.cs` (ProgramData ACL) · `OutboundAddressGuard.cs` (SSRF bayrağı
arayüzde değil) · `SchedulerService.cs` (cron URL hedefi denetimi). Ayrıca üç test dosyası
(`UpdatePackageTrustTests`, `SelfUpdateServiceTests`, `ArgumentInjectionTests`) ve
`docs/denetim-2026-08-19.md`.

**KIYAS (kaynaklı).** Plesk: SSH Terminal eklentisi yöneticiye `root` verir; **ek** yönetici
hesapları "Restricted Mode"dadır ve Plesk'in kendi belgesi, yetki yükseltmesini önlemek için
"SSH Terminal" kutusunun bu hesaplarda **işaretlenmemesi** gerektiğini yazar. Abonelik sahipleri
terminale girebilir ama **kendi sistem kullanıcısı** olarak, root değil. cPanel: WHM terminali
root'undur; hesap terminali varsayılan **kapalıdır** ("disabled shell") ve açılsa bile kullanıcının
kendi kimliğiyle koşar. Yani olgun panellerin kararı: **tam yetkili terminal = sunucu sahibi; alt
hesaplar ya hiç ya da düşük yetkiyle.**
Kaynaklar: docs.plesk.com "Using Additional Administrator Accounts" ve "Accessing the Command Line
from the Plesk Interface"; WHM "Manage Shell Access" + cPanel Feature Manager belgeleri.

**SEÇİLEN YOL: (A) — terminal kısıtlandı, doktrin PRECISE hâle getirildi.** Terminal KALDIRILMADI.
(B) reddedildi: doktrin, terminale erişemeyen bütün yöneticiler/bayiler/müşteriler için GERÇEK;
onu tümden bırakmak altı korumanın gerekçesini boşa çıkarırdı. Doğru düzeltme cümleyi DARALTMAKTI:

> Panel yöneticisi bir Windows yöneticisi **değildir** — **TEK istisna sunucu SAHİBİDİR.**

**Dokunulan yerler (kod):**
- YENİ `src/AdaPanel.Core/Interfaces/IServerOwnerService.cs` — doktrinin TEK kaynağı.
- YENİ `src/AdaPanel.Infrastructure/Security/ServerOwnerRegistry.cs` — saf karar (dosya/DB'siz
  sınanabilir) + disk biçimi.
- YENİ `src/AdaPanel.Infrastructure/Security/ServerOwnerService.cs` — kararı SABİTLER.
- `Api/Controllers/TerminalController.cs` — sahiplik denetimi + taklit oturumu reddi + reddin
  denetim kaydı (`terminal.command.denied`, denenen komutla birlikte).
- `Api/Controllers/AuthController.cs` — **yönetici taklit edilemez** (`auth.cannot_impersonate_administrator`).
- `Infrastructure/Services/UserManagementService.cs` — sunucu sahibi **silinemez / pasife alınamaz /
  rolü düşürülemez** (`users.server_owner_protected`).
- `Infrastructure/Services/AuthService.cs` + `Core/Dtos/Auth/CurrentSessionDto.cs` — oturumda
  `isServerOwner` (YALNIZ UX; dayatma uçta).
- `frontend/src/views/ServerToolsView.vue` + `stores/auth.ts` — Terminal sekmesi yalnız sahibe.
- i18n TR/EN +4/+4 (`terminal.owner_only`, `users.server_owner_protected`,
  `auth.cannot_impersonate_administrator`, `panel.activity.action.terminal.command.denied`);
  API `Resources` TR/EN +1/+1. Parite: frontend **5223/5223**, Api Resources **264/264**.

**SAHİPLİK NASIL BELİRLENİYOR (rol yükseltmesiyle ele geçirilemez):**
1. `%ProgramData%\AdaPanel\config\server-owner.json` — panelin HTTP yüzeyinden **yazılamaz**
   (aynı kalıp `AllowInternalOutboundTargets` ve `AllowUnauthenticatedLocalSenders`'ta zaten var:
   korumayı korunan yüzeyin kendisinden kapatılabilir yapmak, korumayı kaldırmakla aynı şey).
2. Kayıt YOKSA (hiç yazılmamış / bozuk / okunamıyor) -> **en eski etkin yönetici** = kurulum
   sihirbazının yarattığı kişi; karar diske **sabitlenir**. Sonradan yaratılan her yöneticinin
   `CreatedAtUtc`'si daha büyüktür, yani yeni hesap açmak sahipliği vermez.
3. Kayıt VARSA son söz onundur: gösterdiği kişi artık etkin yönetici değilse sonuç **"sahip yok"**,
   "sıradaki" DEĞİL. Bu ayrımı bir test yakaladı — geri düşüş burada da geçerli olsaydı sahibi
   pasife almak sahipliği devralmanın yoluydu.

**GİZLİLİK KARARI — komut metni neden kayda yazılıyor:** uca yalnız sunucu sahibi girebiliyor,
denetim kaydını okuyan uçlar da yönetici uçları; kaydedilen kişi ile okuyan kişi AYNI, korunacak
üçüncü taraf mahremiyeti YOK. Kayıt olmadan sahibi bile "makinemde ne çalıştı" sorusunu
yanıtlayamaz. Metin `SecretRedactor`'dan geçiyor (anahtar=değer sırlar maskeli, 2000 karakter).
Reddedilen deneme de yazılıyor: reddedilen komut niyetin kendisidir.

**Doğrulama:** `dotnet test AdaPanel.sln -c Release` -> **5090 yeşil / 0 kırmızı** (20 yeni test).
typecheck temiz, `npm run build` temiz, `npm test` 42 yeşil.

**Mutasyon (koruma kaldırıldı -> KIRMIZI görüldü -> geri kondu; hepsi geri alındı, 20/20 yeşil):**
terminal sahiplik denetimi kaldırıldı -> **3 kırmızı** ·
taklit oturumu reddi kaldırıldı -> **1 kırmızı** ·
"yöneticiyi taklit etme" yasağı kaldırıldı -> **1 kırmızı** ·
sahip SİLİNEMEZ koruması kaldırıldı -> **1 kırmızı** ·
sahip PASİFE ALINAMAZ koruması kaldırıldı -> **1 kırmızı** ·
sabit kayıt geçersizse sıradakine düşsün (eski hatalı kural) -> **2 kırmızı** ·
geri düşüş EN YENİ yöneticiye -> **8 kırmızı**.

**Fazla-kapatma nöbetçileri (özelliği öldürmediğinin kanıtı):** sunucu sahibi terminale GİREBİLİR ·
bozuk sahiplik kaydı sahibi dışarıda BIRAKMAZ · kayıt hiç yokken sahip yine girebilir ·
sahip OLMAYAN bir yönetici hâlâ pasife alınabilir · müşteri hesabı HÂLÂ taklit edilebilir.

**KAPATILAMAYANLAR — panel yöneticisinin SYSTEM'e çıkabildiği DİĞER yollar (ölçüldü, AÇIK):**
1. `POST /api/v1/store/install` — `targetPath` KEYFİ mutlak yol; site kökü denetimi YOK
   (`AppStoreService.cs:1519-1522`). `cleanInstall=true` ayrıca hedef klasörü boşaltıyor
   (`:1551-1568`). **En ağır kalan.**
2. `POST /api/v1/sites` / `PUT .../document-root` — admin dalında fiziksel yol kısıtı YOK
   (`SitesController.cs:521`; `:362` yalnız `!IsAdmin` dalında). `PhysicalPath = C:\` olan bir site,
   dosya yöneticisini ve `/files/acl`'i (icacls) SYSTEM olarak tüm diske açar.
3. `POST /api/v1/backup/restore` — görev XML'i `Register-ScheduledTask -Xml` ile kaydediliyor;
   zip-slip ve 1 MiB tavanı VAR ama **XML içeriği** (Principal `S-1-5-18`, Action) denetlenmiyor
   (`BackupService.cs:2387`). Ayrıca panel yedeği `adapanel.db` + `config/*` geri yüklüyor ->
   **`server-owner.json` bu yoldan değiştirilebilir**; sahiplik korumasının bilinen sınırı budur.
4. `RestoreMailAsync` -> `ZipFile.ExtractToDirectory` (`BackupService.cs:2696`) — deponun kendi
   kavşak-farkında çıkarıcısını ATLIYOR (aynı dosya `:6322` bunun neden yanlış olduğunu yazıyor).
5. `CacheService.cs:139,259,550,745` — ÇİFT tırnaklı PowerShell dizesine değer gömme. Bugün değer
   HTTP'den gelmiyor (manifest / `InstalledComponent.ServiceName`), yani sömürülemez; ama koruma
   kural değil TESADÜF (aynı hata `SchedulerService`'te ölçülüp kapatılmıştı).
6. Tasarım gereği SYSTEM kalanlar (sertleştirilmiş, meşru): cron `command`/`php` görevleri
   (SYSTEM/Highest), `update/install`, `software/install`, `docker run`, `/dbtools/**` SQL yüzeyi.
7. Terminalin KENDİSİ komut doğrulaması yapmıyor — bilerek: sahibi için amacın ta kendisi.
8. Testlerin `%TEMP%` altında bıraktığı `adapanel-sahiplik-*` klasörleri SİLİNMEDİ (ajan silme
   yasağı). İşletim sistemi temizliğine bırakıldı.

---

## ÖNCEKİ İŞ — `password/forgot` ZAMANLAMA SIZINTISI kapandı (commit ATILMADI)

**ÖLÇÜLDÜ (bu makinede, gerçek kod + gerçek ağ yığını):**

| Yanıt yolunda yapılan iş | Süre |
|---|---|
| Belirteç üretimi (`SingleUseTokenStore.IssueAsync`, dosya kilidi + yazma) | ortanca **0,72 ms** |
| Ayar dosyası okuması (`JsonFileStore.ReadAsync`, tanesi) | ortanca **0,17 ms** |
| **SMTP** — MailKit `ConnectAsync`, KAPALI porta (127.0.0.1:2525) | **2066 ms** |
| **SMTP** — karadelik IP'ye (198.51.100.7:587) | **10 sn**'de hâlâ bekliyordu (tavan) |
| **SMTP** — çözülemeyen ad | 86 ms |
| MailKit varsayılan `Timeout` | **120000 ms** |

**Farkın KAYNAĞI SMTP'dir.** `RequestPasswordResetAsync` adres KAYITLIYSA SMTP ayarını okuyor,
belirteç üretiyor, gerçek bir SMTP bağlantısı açıyor ve denetim kaydı yazıyordu; kayıtlı DEĞİLSE
hiçbirini yapmadan dönüyordu. Gövde ikisinde de aynıydı, süre değildi — yani panel, hiçbir parola
bilmeyen birine "bu e-posta kayıtlı mı" sorusunu YANIT SÜRESİYLE cevaplıyordu.

**Neden sabit gecikme SEÇİLMEDİ:** yukarıdaki sayılar doğru gecikmeyi seçmeyi imkânsız kılıyor
(2 sn seçilse karadelik senaryosunda fark yine sızar, 120 sn seçilse panel kullanılamaz olur).
İşin TAMAMI — adres araması dahil — yanıt yolundan çıkarıldı; yanıt yolunda DAL KALMADI.

**Dokunulan yerler:**
- YENİ `src/AdaPanel.Infrastructure/Support/ArkaPlanIsKuyrugu.cs` — `IArkaPlanIsKuyrugu` +
  sınırlı (512) kanal + `ArkaPlanIsKuyruguWorker` (`BackgroundService`, tek tüketici, iş başına
  3 dk tavan). Kanal kipi `Wait`; `DropWrite` denendi ve nöbetçi test yakaladı (o kipte `TryWrite`
  işi SESSİZCE atıp `true` döner).
- YENİ `src/AdaPanel.Infrastructure/Services/ParolaSifirlamaPostasi.cs` — işin tamamı; üç sonuç da
  denetim kaydına düşer (`auth.password_reset.requested` / `.mail_failed`).
- `Services/AuthService.cs` — `RequestPasswordResetAsync` artık yalnız normalize edip kuyruğa yazar;
  `INotificationService` ve `IPanelSecurityService` bağımlılıkları düştü.
- `Api/Controllers/MailListPublicController.cs` — AYNI SINIF: abonelikten çıkma isteğinde abone
  olan/olmayan adres SMTP + IIS derin araması yüzünden ayırt edilebiliyordu; iş kuyruğa alındı.
- i18n TR/EN: `panel.activity.action.auth.password_reset.mail_failed` (+1/+1).

**Doğrulama:** `dotnet test AdaPanel.sln -c Release` → **5070 yeşil / 0 kırmızı** (17 yeni test).
typecheck temiz, `npm run build` temiz, `npm test` 42 yeşil.

**Mutasyon (koruma kaldırıldı → kırmızı görüldü → geri kondu):**
yanıt yoluna "kullanıcı var mı" dalı geri kondu → **3 kırmızı** ·
gönderim hatası sessizce yutuldu → **1 kırmızı** ·
SMTP ayarsız kaydı kaldırıldı → **1 kırmızı** ·
kanal kipi `DropWrite` → **1 kırmızı**. Hepsi geri alındı, 5070/5070 yeşil.

**KAPATILAMAYANLAR (açık, gerekçeli):**
1. `login` — kullanıcı VARSA ek bir `SaveChangesAsync` (başarısız sayaç/kilit) çalışıyor, YOKSA
   çalışmıyor. Kalan fark sub-ms mertebesinde ve iki dalda da çalışan PBKDF2 doğrulamasının
   (on ms'ler) gürültüsünün altında; kapatmanın tek yolu veritabanına sahte yazmak olurdu.
2. `UserManagementService.CreateUserAsync` — `users.duplicate_username` / `users.duplicate_email`
   YANIT GÖVDESİNDE dönüyor ve kontrol KÜRESEL. Bayi, başka bayinin müşterisinin adresini
   sınayabilir. Yapısal: kullanıcı adı/e-posta panel genelinde tekil olmak zorunda (giriş bu ikisiyle
   yapılıyor). Plesk'te de aynıdır. Kimliksiz DEĞİL (yönetici/bayi yetkisi gerekir).

---

## ÖNCEKİ İŞ — Göç yolunda uzak sunucu kimliği artık DOĞRULANIYOR (kapandı, commit ATILMADI)

**Ölçülen açık (üç nokta, üçü de kullanıcının kimlik bilgisini taşıyordu):**

| Nokta | Eski hâl | Taşıdığı sır |
|---|---|---|
| Plesk/cPanel keşfi | `OutboundConnectGuard.CreateHandler(verifyTlsCertificate: false)` | `X-API-Key` / Basic / `whm root:TOKEN` |
| FTPS dosya çekme | `FtpConfig.ValidateAnyCertificate = true` | FTP kullanıcı adı + parolası |
| SFTP dosya çekme | `HostKeyReceived`'e HİÇ abone yok → SSH.NET'te `CanTrust` varsayılanı `true` (ölçüldü) | SSH parolası |

Gevşetme KASITLIYDI ve gerekçesi geçerliydi (kaynak sunucular neredeyse hep kendinden imzalı
sertifika taşır). Bu yüzden "sadece açmak" çözüm değildi — göç kullanılamaz hâle gelirdi.

**Kopyalanan kalıp: `ssh`ın host key onayı** (Plesk Migrator da Linux göçünde parmak izinin
önceden teyidini ister). Varsayılan DOĞRULA → reddederken sebebi + parmak izini GÖSTER → yönetici
başka kanaldan teyit edip AÇIKÇA kabul et → kabul o TEK parmak izine sabitlensin → kayda geçsin.
**Küresel "hep kapat" anahtarı YOK**; `CreateHandler`'daki `bool verifyTlsCertificate` KALDIRILDI,
yerine karar verici (`RemoteCertificateValidationCallback?`) geldi — verilmezse .NET'in tam
doğrulaması çalışır.

**Dokunulan yerler:**
- YENİ `src/AdaPanel.Infrastructure/Migration/RemoteHostTrust.cs` — `TrustFingerprint`,
  `RemoteTrustFailure`, `RemoteCertificateTrustGate`, `RemoteHostKeyTrustGate` (tek kaynak).
- `Support/OutboundConnectGuard.cs` — boole kapatma anahtarı kaldırıldı.
- `Migration/SiteMigrationService.cs`, `Migration/FileMigrationService.cs` — üç uç da kapıya bağlandı.
- DTO'lara açık onay alanı: `PleskDiscoverRequest`/`CpanelDiscoverRequest.AcceptCertificateFingerprint`,
  `MigrateSiteRequest.AcceptFileHostFingerprint`, `RemoteFileMigrationRequest.AcceptHostFingerprint`.
- `Api/Controllers/MigrationController.cs` — kabul edilen istisna DENETİM KAYDINA yazılıyor
  (`migration.discover.trust_exception` + göç kaydına ek satır).
- Frontend: YENİ `components/RemoteTrustPrompt.vue` + `utils/remoteTrust.ts`, `views/MigrationView.vue`,
  `api/migration.ts`. i18n TR/EN +24/+24 → **5208 = 5208**.

**Doğrulama:** `dotnet test AdaPanel.sln -c Release` → **5020 yeşil / 0 kırmızı** (Core 2 + Infra 5018;
16 yeni test). typecheck temiz, `npm run build` temiz, `npm test` 42 yeşil.

**Mutasyon (koruma kaldırıldı → kırmızı görüldü → geri kondu), 16 testin kaçı düştü:**
`Evaluate` başına `return true` → **9** · FTPS `ValidateAnyCertificate = true` → **1** ·
SFTP `HostKeyReceived` aboneliği silindi → **3** · taşıma katmanı karar vericiyi yok saydı → **3** ·
keşif kapıyı bağlamadı → **3**. Beşi de kırmızı; geri alındıktan sonra 16/16 yeşil.

**KAPATILMADI (bilerek, raporda yazılı):**
- Toplu göçte (`runBulk`) parmak izi adım detayında GÖRÜNÜYOR ama "kabul et" düğmesi YOK; tek site
  sihirbazından kabul edilip toplu göç tekrar çalıştırılır.
- Kabul edilen parmak izi KALICI DEĞİL (`known_hosts` deposu yok) — her göç işinde yeniden onaylanır.
- `FtpEncryptionMode.Auto` (fırsatçı FTPS) duruyor: araya giren `AUTH TLS` sunmayarak düz FTP'ye
  düşürebilir. Ayrı iş; `ftps` seçildiğinde `Explicit` zorunlu kılınmalı.
- DB aktarımı `mysqldump`/`mysql` CLI ile yapılıyor ve `--ssl-mode` verilmiyor — bu sınıfın
  DÖRDÜNCÜ noktası, bu turda kapsam dışı bırakıldı.

Commit ATILMADI, installer üretilmedi, canlı panele dokunulmadı.

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
> **SONRADAN DUZELTILDI — bu paragrafin son cumlesi YANLISTI.** Yedegin dayandigi
> "`ServerAddress` bos kalabilir" varsayimi olculdu ve cokdu: `PanelPreferencesService.GetAsync`
> o alani asla bos birakmiyor. Yani yedek hic calismiyordu, yalnizca istismar yuzeyi olarak
> duruyordu ve KALDIRILDI (ayni tur `AuthService.CreateSsoLinkAsync`'teki ikizini de kapatti).
> Tam olcum ve mutasyon sonuclari icin dosyanin sonundaki
> "`AllowedHosts: \"*\"` — OLCULDU, DOGRU YERDEN KAPATILDI" bolumune bak.

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

---

## Denetim turu: "panel yapamadigi seye BASARDIM diyor" (04.09.2026, devam)

Onceki ajan kota yuzunden durdurulmustu; iki bulgusu (`c746edf`, `4f9a1fc`) ve askidaki ucuncusu
(`35d3671`) kapaliydi. Bu tur, brief'in "HIC gelinemedi" dedigi yuzeylerde devam etti:
ACL/izin uygulama, IIS yazma yollari, bilesen kurulumu, kota uygulama, posta, FTP, SSL, kismi
basari raporlamasi.

### KAPATILAN DORT BULGU

| # | Yer | Dis sistem ne yapmiyor | Panel ne diyordu |
|---|---|---|---|
| 1 | `SiteProvisioningService.RepairPermissionsAsync` + `SitesController:430` + `SiteHostingView.vue:797` | `icacls` hesap adi cozulemeyince 1332 ile **hicbir sey uygulamaz**; `/T /C` kismi basarida da **0 doner** | "Yazma izinleri onarildi" (KOSULSUZ; ucun basarisiz DONMESI imkansizdi) |
| 2 | `UserManagementService.ApplySuspensionStateAsync` | Site tek tek durdurulur, hata yutulur | Kayit kosulsuz `scope.Sites.Count` yaziyordu -- `35d3671`'in KARDESI, atlanmisti |
| 3 | `DatabasesController.CheckAndRepairAsync:85` | Tablo yok / motor REPAIR desteklemiyor / onarim sonrasi HALA bozuk | "Tables repaired: \<istenen adlar\>" |
| 4 | `SitesController.ImportExistingSitesAsync` | `EnsureDefaultsAsync` sonucu `_ =` ile atiliyordu (dns, **security**=kiraci yalitimi, ssl, ftp...) | "N site imported **and defaults applied**" |

**KAPATMA BICIMI.** (1) GERI OKUMA: yeni `FolderWriteAccessProbe` DACL'i **diskten** okur, uc
degerli cevap verir (`granted` / `not_granted` / `unmeasured`); uc `SitePermissionRepairResultDto`
donuyor, arayuz uc durumu AYRI gosteriyor. (2)(3)(4) ADIYLA RAPORLAMA + TEK KAYNAK: cumleler
`SiteSuspensionReport`, `DatabaseRepairAudit`, `SiteImportAudit` icine cikarildi
(`SubscriptionEnforcementService`'in iki yolu da artik oradan besleniyor).

**Yeni dosyalar:** `src/AdaPanel.Infrastructure/Iis/FolderWriteAccessProbe.cs`,
`src/AdaPanel.Infrastructure/Support/SiteSuspensionReport.cs`,
`src/AdaPanel.Core/Dtos/Sites/SitePermissionRepairResultDto.cs`,
`src/AdaPanel.Core/Dtos/Sites/SiteImportAudit.cs`,
`src/AdaPanel.Core/Dtos/Databases/DatabaseRepairAudit.cs` + 4 test dosyasi (24 test).
**Yeni i18n (tr+en):** `panel.hosting.permissionsNotRepaired`, `panel.hosting.permissionsUnverified`,
`panel.activity.action.sites.permissions.repair_failed`, `...repair_unverified`.

**MUTASYON KANITI (6 mutasyon, 6'si KIRMIZI).** Olcumu bozan 4 mutasyon (probe hep "granted",
esleme sabit "granted", aski kaydi ISTENEN sayiya donus, devralma kaydi kosulsuz "applied")
ilgili testleri kirmizi yakti. **FAZLA KAPATMA nobetcileri (2):** probe hep "not_granted"
yapilinca 2 test kirmizi; aski cumlesi HEP uyari yazinca 3 test kirmizi (elle + otomatik yollarin
ikisi birden). Basarili durumda cumlelerin SADE kaldigini olcen nobetciler her dosyada var.

### SONRAKI TUR ICIN: OLCULDU, KAPATILMADI (tekrar aranmasin, DOGRULANMASI gerekir)

Uc kesif ajani 40+ aday isaretledi. Asagidakiler dosya:satir ile isaret edildi ama **bu turda
ne dogrulandi ne kapatildi** -- once OLCULMELI:

* **Posta (Stalwart) -- en yuklu yuzey.** `MailService` icindeki `ReloadSettings` cagrilari
  `_ =` ile atiliyor (`:1087`, `:1155`, `:1868`, `:2590`, `:2612`, `:2918`): ayar YAZILIYOR ama
  Stalwart bellekte tuttugu icin ETKINLESMEMIS olabilir. `QueryAsync:3080` CLI hatasini bos liste
  yapiyor -> "bu alanda hic kutu yok" gorunumu. `SetAccountQuotaAsync:1692` kardeslerindeki
  `*_unverified` korumasindan yoksun. `MailEnableMailService` COM cagrilarinda `[void]` ve
  `catch {}` (silme/parola/alias). `MailController:211` istenen MB'yi yaziyor.
* **FTP.** `SetRequireSslAsync:289` ISTENEN bayragi donuyor; betikteki geri okuma yalniz
  `Write-Warning`. `EnsurePasswordNeverExpiresAsync` basarisizliginda hesap yine "olusturuldu".
* **SSL.** `EnsureHttpsBindingAsync:1180` sertifika deposunu TAHMIN edip (`My`) geri okumuyor;
  `MeasureHttpsBinding:998` baglamaya bakiyor, `certificateHash`'e bakmiyor.
* **Bilesen kurulumu.** `ComponentBootstrapper:811` **istenen** surumu kaydediyor; `--version`
  cikisi (`:3152`) okunup ATILIYOR. roundcube/clamav/vandelay icin dogrulama YOK (`:2979`).
* **Guvenlik duvari.** `FirewallService:386` `Enabled=true` UYDURUYOR, non-strict runner kullaniyor;
  `ClearManagedBlockRulesAsync:330` BULUNAN kural sayisini KALDIRILAN diye yaziyor (erisim
  kurtarma kolu).
* **Zamanlayici.** `DeleteTaskAsync` / `SetTaskEnabledAsync` geri okumuyor (`CreateTaskAsync`
  okuyor). `StopTaskAsync:656` `taskkill` cikis kodunu atip DENEME sayisini "oldurulen" yaziyor.
* **IIS.** `SiteAliasService:110` baglama kaldirilamasa da kaydi siliyor (alias arayuzden kayboluyor,
  IIS'te kaliyor). `AspNetService:377` `AddAt` basarisizliginda handler'i SONA ekliyor -> `.php`
  404.3. `SiteHostingSettingsService` web.config'i geri okumuyor (500.19 riski).
* **Veritabani.** MySQL `--execute="source ..."` istemci tarafi; PostgreSQL kardesindeki
  `ON_ERROR_STOP` karsiligi YOK. `HardenAdminAccountsAsync` DROP'lari yutunca kayit "temiz" diyor.

### TEMIZ CIKANLAR (bu turda okundu, sinif YOK)
`WafService.ApplyFolderAclAsync` ($LASTEXITCODE + geri okuma), `DirectoryProtectionService`
(uc degerli), `SchedulerService.CreateTaskAsync` (`cron.write_unverified`),
`DiskQuotaService` (strict runner + geri okuma), `FileQuotaGuard`, `MySqlMaintenanceRunner`
(onarimdan SONRA yeniden CHECK), `StalwartAccountArchiver` (basarisiz hesaplari ADIYLA yaziyor),
`FtpService` silme/rollback/`ProbeHome`, `NginxInstallationSupport.RemoveAsync`,
`RemoteDbAccessService` firewall kismi, `MailController` posta listesi abone sayisi (`4f9a1fc`).

### OLCULEMEYENLER
Bu makinede IIS uygulama havuzu, ayakta Stalwart, MariaDB ve Windows DNS **yok**. Bu yuzden:
`FolderWriteAccessProbe`'un `granted` dali GERCEK bir havuz kimligiyle degil, gercek NTFS DACL'i
uzerinde `BUILTIN\Users` SID'iyle olculdu (NTFS davranisi ayni; SID cozumlemesi degil).
`icacls`'in kismi basarida 0 dondurmesi bu turda YENIDEN olculmedi -- deponun onceki olcumune
dayanildi (`WebDbClientService.cs:591`).

---

## EN AGIR DORT ADAY DOGRULANDI VE KAPATILDI (04.09.2026, ikinci tur) -- commit ATILMADI

Onceki turun "OLCULDU, KAPATILMADI" listesindeki dort en agir aday dogrulandi. Canli panele
(8890) DOKUNULMADI; hicbir sey silinmedi; guvenlik duvarinda GERCEK kural olusturulmadi.

### 1) STALWART `ReloadSettings` SONUCU ATILIYORDU -- **ACIK**, kapatildi

Stalwart ayarlari BELLEKTE tutar: ayari yazmak onu ETKIN yapmaz, `ReloadSettings` yapar. Bu
cagrinin sonucu **sekiz** yerde `_ = await ...` ile atiliyordu: liste politikasi (`:1087`),
gonderen politikasi (`:1155`), DATA asamasi script'i / otomatik yanit (`:1868`), karsilama adi
(`:2590`, `:2612`), TLS sertifikasi (`ReloadTlsCertificates`, `:2918`), role (`:3989`, `:4023`).
Reload dustugunde ayar diskte kalir, sunucu eski ayarla calisir, panel "kaydedildi" der.
**Ayni semptom karsilama adinda CANLI yasanmisti** ve kodun kendi yorumu onu anlatiyordu.

**KAPATMA BICIMI -- TEK KAYNAK.** Sekiz cagri iki metoda indirildi:
`ReloadMailSettingsAsync` (sonucu `MailReloadOutcome` olarak DONDURUR) ve
`RequireMailSettingsReloadAsync` (kullaniciya "yapildi" diyecek yollar icin; yuklenemezse
`mail.settings_not_activated` + HANGI AYAR oldugu `Details`'te). Arka plan yollari
(`MailSenderPolicyWorker`, `EnsureServerHostnameAsync`) firlatmaz ama SUSMAZ: gonderen
politikasi artik `Applied` degil `WrittenNotActivated` doner ve isci "sahtecilik korumasi
yururlukte DEGIL" diye uyarir. Kardes yol (`TryApplySenderPolicyAsync`) ayni anda duzeltildi.
Sertifika yolu `mail.certificate_not_activated` firlatir.

### 2) `QueryAsync` HATAYI "HIC YOK"A CEVIRIYORDU -- **ACIK**, kapatildi (en agir sonuc)

`result.Success ? ParseItems(...) : []`. Cagiranlar bos liste ile hatayi ayirt edemiyordu.
**Iki yerde KOTAYI SESSIZCE DEVRE DISI BIRAKIYORDU:**

| Yer | Ne oluyordu |
|---|---|
| `MailController.EnsureMailboxQuotaAsync:918` | `workspace.Accounts?.Count ?? 0` -- sorgu duserse sayi 0; `MaxMailboxes` kotasi DOLU olan kiraci sinirsiz kutu acar |
| `MailService.GetDomainAllocatedMbAsync` / `EnsureDomainAllocationAsync` | tahsis toplami 0 cikar; alan adi disk kotasi hic dayatilmaz. Ayrica `SetDomainQuotaAsync` 50 GB tahsisli alana 100 MB limit koymaya izin verirdi |

Ucuncusu: `PushCertificateToStalwartAsync` sertifika listesini soramadiginda "yok" sayip ayni ad
icin IKINCI bir sertifika olusturuyordu. Dorduncusu ve en sinsisi:
`SyncListPolicyScriptAsync` kural kumesini SIFIRDAN kurup script'i EZIYOR; sorgu duserse kume
bos cikar, yuva zaten bizimse SIFIR KURALLIK script yazilir -- yani **"yalniz aboneler
gonderebilir" korumasi butun listelerden birden kalkar**, ustelik panelin kendi politika deposu
dopdoluyken. Aynisi `BuildListUnsubscribeRulesAsync` icin gecerli (List-Unsubscribe basligi
sessizce silinir; o baslik icin 0.8.14'te ayrica duzeltme yapilmisti).

**KAPATMA BICIMI.** `QueryAsync` artik `MailQueryResult(Measured, Items)` doner; ayrim tek yerde
(`MailQueryResult.Olc`). 12 cagri yerinin HEPSI guncellendi. `FindIdAsync` de artik `null`'i
yalniz "YOK" icin kullanir; sorulamadiysa `mail.lookup_unmeasured` firlatir (once `null`, butun
cagiranlarca "yok" diye okunuyordu: alan adi silme IDEMPOTENT BASARI donuyordu, yeni kutunun
kotasi sessizce hic uygulanmiyordu). Kota karari TEK KAYNAKTA: yeni
`Core/Dtos/Mail/MailQuotaMeasurement.cs` -- iki dayatma noktasi da oradan gecer.
`MailWorkspaceDto.AccountsMeasured` varsayilani **false**'tur (fail-safe).

### 3) BILESEN KURULUMU ISTENEN SURUMU KAYDEDIYORDU -- **ACIK**, kapatildi

`installedComponent.Version = component.Version` (manifest). Kurulan surum HIC geri okunmuyordu;
ustelik `ValidateComponentRuntimeAsync` ZATEN `--version` kosturuyor ve ciktisini ATIYORDU.
Zincirleme bedeli: `RefreshUpdateCacheEntry` o yanlis degerden beslenir, yamasi cikmis surum
"guncel" gorunur. Bu depoda birkac bilesenin indirme adresi surum numarasi URL'e dizilerek
TAHMIN ediliyor (MySQL/PostgreSQL/pgAdmin), yani sapma varsayimsal degil.

**KAPATMA BICIMI.** Yeni `Bootstrap/ComponentVersionProbe.cs`: `--version` ciktisindan surum
ayiklanir, `Decide(istenen, olculen)` uc degerli karar verir. Kaydedilen artik OLCULEN surumdur;
olculemediyse istenen gosterilir ama denetim kaydi "REQUESTED ... could not be measured" der.
Olculen istenenle ayniysa cumle SADE kalir. PHP dogrulamasi surum degil eklenti listesi bastigi
icin bilerek "olculemedi" sayilir (sahte olcum uretmemek icin).

### 4) GUVENLIK DUVARINDA `Enabled=true` UYDURMASI -- **ACIK**, kapatildi

**BU MAKINEDE OLCULDU:** cok ifadeli bir script'te `New-NetFirewallRule` sonlandirici olmayan
hata verdiginde script akmaya devam eder ve `powershell.exe` **0** ile cikar (gecersiz `-Action`
ile denendi: cmdlet hata yazdi, sonraki satir kostu, cikis kodu 0). Yani `result.Success` "kural
kuruldu" DEMIYORDU; `UpsertRuleAsync` sonra ISTENEN degerleri ve `Enabled = true` degerini
UYDURUYORDU. Denetim kaydi da ("enabled {rule.Enabled}") o uydurmadan besleniyordu -- RDP'yi
engellemek icin dugmeye basan yonetici "engellendi" yazisini gorup sunucuyu acik birakabilirdi.

Ikinci bulgu ayni dosyada: **`ClearManagedBlockRulesAsync` BULUNAN kural sayisini KALDIRILAN
diye yaziyordu** (`$kurallar.Count` silmeden ONCE okunuyor, silme `-ErrorAction SilentlyContinue`).
Bu, panele ulasamayan yoneticinin sunucu konsolundan cektigi TEK KURTARMA KOLU
(`recover-access.flag`); yalan soyledigi anda yonetici guvenlik duvarini eleyip baska yerde arar.
Ucuncu bulgu: **`GetRulesAsync` okunamadiginda BOS LISTE gosteriyordu** -- "kural yok" ile
"okuyamadim" ayni ekrandi.

**KAPATMA BICIMI.** Yazma KATI kosucuya alindi (`RunPowerShellStrictAsync` -- deponun zaten
yazili dogru kalibi). Sonra kural GERI OKUNUR: yeni `ReadManagedRulesAsync` once OKUNABILIRLIK
KAPISINDAN gecer (`Get-NetFirewallRule -ErrorAction Stop`) ve ancak gecerse `ADAPANEL-DUVAR=OKUNDU`
damgasini basar. Uc durum ayri: kural okundu / kural YOK (`firewall.rule_not_applied`) /
okunamadi (`FirewallRuleDto.Verified = false`, `Enabled` uydurulmaz). Temizleme kolu artik
`FirewallBlockCleanupReport` doner: KALDIRILAMAYAN kurallari ADIYLA yazar, okunamadiysa
"OLCULEMEDI" der. Arayuz sabit yesil "kaydedildi" yerine `panel.firewall.savedUnverified`
gosterir.

### MUTASYON KANITI (15 mutasyon, 15'i KIRMIZI)
Korumalari kaldiran 9 mutasyon (reload hep etkin, sorgu hatasi=bos liste [ESKI DAVRANIS], kota
kapisi hep gecirir, workspace varsayilani "olculdu", surum hep istenen [ESKI DAVRANIS], surum
ayiklayici hep null, kati kosucu=gevsek kosucu, duvar okunamadiginda "kural yoktu", damga
kosulsuz basilir) ilgili testleri kirmizi yakti.
**FAZLA KAPATMA NOBETCILERI (6):** reload hep "etkinlesmedi", her sorgu "olculemedi", kota kapisi
hep reddeder, surum cumlesi hep uyarir, rapor hep "kaldirilamayan" yazar, her kural
"dogrulanmamis" sayilir -- altisi da kirmizi. Yani her sey yolundayken panel HALA sade "yapildi"
diyor.

### OLCULEMEYENLER (durustce)
* Bu makinede **ayakta Stalwart YOK**. Stalwart CLI'nin gercekte ne dondugu, `ReloadSettings`
  cagrisinin ayari gercekten etkinlestirdigi ve sunucunun sonrasindaki davranisi OLCULEMEDI.
  Olculen sey panelin surec sonucunu NASIL OKUDUGUDUR ve bu, gercek `powershell.exe` surecleriyle
  uretilen GERCEK `CommandResult` nesneleri uzerinden olculdu (elle yazilmis sahte sonuclarla
  degil).
* Guvenlik duvari: surec YUKSELTILMEMIS oldugu icin `Get-NetFirewallRule` gercekten
  "Erisim engellendi" veriyor -- bu, **okunabilirlik kapisinin GERCEKTEN olculdugu** anlamina
  gelir. Kuralin OKUNDU dali (var/etkin/port) bu makinede gercek bir kuralla olculemedi;
  guvenlik duvarinda kural olusturulmadi (silinemeyecegi icin bilerek).
* Bilesen surumu: kurulu Stalwart/Node/Python olmadigi icin olcum, depodaki GERCEK
  `.tools\dotnet\dotnet.exe --version` ciktisi uzerinden yapildi (ayni ayiklayici, gercek surec).

### KIRILAN DAVRANISLAR (bilerek)
1. Posta CLI sorgusu duserse artik SESSIZ gecilmiyor: alan adi silme, alias silme, DKIM kaydi
   uretme, liste politikasi yazma ve kimlik arama HATA firlatiyor. Once bu yollar "yok" sayip
   devam ediyordu. Islem GERI ALINABILIR bicimde reddediliyor (yeniden denenebilir).
2. `EnsureServerHostnameAsync` reload dustugunde artik `false` doner (once `true`).
3. `GetRulesAsync` guvenlik duvari okunamadiginda BOS LISTE degil `firewall.read_failed` doner.
4. `IFirewallService.ClearManagedBlockRulesAsync` donus tipi `int` -> `FirewallBlockCleanupReport`.
5. Kurulan bilesenin kaydedilen surumu artik OLCULEN surum; olcum istenenden farkliysa panelde
   farkli bir numara gorunur (dogrusu budur).

### YENI DOSYALAR
`src/AdaPanel.Core/Dtos/Mail/MailQuotaMeasurement.cs`,
`src/AdaPanel.Core/Dtos/Firewall/FirewallBlockCleanupReport.cs`,
`src/AdaPanel.Infrastructure/Bootstrap/ComponentVersionProbe.cs`,
`tests/AdaPanel.Infrastructure.Tests/OlculemedigiSoylenmeyenIddialarTests.cs`,
`tests/AdaPanel.Infrastructure.Tests/PostaAyariEtKINLESTI_MI_Tests.cs` (toplam 34 test).
**Yeni i18n (tr+en):** `firewall.rule_not_applied`, `panel.firewall.savedUnverified`,
`mail.settings_not_activated`, `mail.certificate_not_activated`, `mail.lookup_unmeasured`,
`mail.accounts_unmeasured`, `mail.domain_members_unmeasured`, `mail.list_policy_unmeasured`,
`mail.dkim_unmeasured`, `mail.mailbox_quota_unmeasured`, `mail.domain_quota_unmeasured`.

### BU TURDA BAKILMAYANLAR (onceki listede duruyor, DOGRULANMADI)
FTP (`SetRequireSslAsync:289`, `EnsurePasswordNeverExpiresAsync`), SSL
(`EnsureHttpsBindingAsync:1180`, `MeasureHttpsBinding:998`), Zamanlayici (`DeleteTaskAsync`,
`SetTaskEnabledAsync`, `StopTaskAsync:656`), IIS (`SiteAliasService:110`, `AspNetService:377`,
`SiteHostingSettingsService`), Veritabani (MySQL `--execute` ile `source`,
`HardenAdminAccountsAsync`), `MailEnableMailService` COM `[void]` / bos `catch`,
`SetAccountQuotaAsync:1692` kardeslerindeki dogrulama korumasi,
`ComponentBootstrapper:2979` (roundcube/clamav/vandelay dogrulamasi YOK).

---

## "BAKILMADI" LISTESINDEKI ALTI ADAY DOGRULANDI (05.09.2026, ucuncu tur) -- commit ATILMADI

Onceki turun ACIKCA "ne dogrulandi ne kapatildi" diye biraktigi adaylarin HEPSI dosya:satir ile
dogrulandi. **Altisi da ACIKTI.** Onu kapatildi; ucu olculdu ama kapatilMADI (asagida ayri
baslikta, adiyla).

Canli panele (8890) DOKUNULMADI; gercek IIS sitesi / FTP hesabi / sertifika / zamanlanmis gorev
OLUSTURULMADI ya da SILINMEDI; hicbir sey silinmedi.

### 1) FTP -- `SetRequireSslAsync` ISTENEN bayragi donduruyordu -- **ACIK**, kapatildi

`FtpService.cs:289` istenen `bool`'u geri veriyordu; `FtpController.cs:78` de ondan beslenip
kullaniciya `panel.ftp.tlsRequired` = **"FTPS artik zorunlu; duz FTP baglantilari reddedilecek"**
diyordu. Bu bir GUVENLIK IDDIASIDIR ve IIS'e hic bakilmadan yaziliyordu.

Altyapi betigi GEVSEK kosucuyla calisiyor (`RunPowerShellAsync`): `Set-ItemProperty` sonlandirici
olmayan hata verdiginde betik akar ve `powershell.exe` 0 ile cikar. Betigin ICINDE geri okuma
ZATEN VARDI (`:809`) ama yalniz `Write-Warning` yaziyordu ve C# tarafi ciktiya HIC bakmiyordu --
**olcum yapilip ATILIYORDU**, bu deponun tekrarlanan hata sinifi.

Bedeli iki yonludur: acma duserse butun musteri parolalari duz metin akmaya devam eder ve panel
"reddedilecek" der; kapatma duserse site SslRequire'da kalir, istemciler "534 Policy requires
SSL" ile kilitlenir ve panel "istege bagli" der.

**KAPATMA BICIMI.** Yeni `Core/Dtos/Ftp/FtpTlsMeasurement.cs` (uc degerli). Betik artik
`ADAPANEL-FTP-TLS=<control>|<data>|<cert>` damgasi basiyor; **dataChannelPolicy ve serverCertHash
de AYRI olculuyor** (controlChannelPolicy=1 iken sertifika yoksa IIS AUTH TLS'i reddeder -- duz
baglanti da yasak, TLS de imkansiz: FTP TUMDEN kapanir; ve sertifika baglama betikte `catch { }`
icinde). Taninmayan/bos deger "uygulanmadi" DEGIL "olculemedi" sayilir: betigin kendi 2026-06-21
notu, eski bir throw'un calisan FTP'yi yanlis alarmla bloke ettigini yaziyor.

### 2) FTP -- `EnsurePasswordNeverExpiresAsync` sonucu ATILIYORDU -- **ACIK**, kapatildi

Metot bayragi ZATEN geri okuyordu ama sonuc yalniz `LogWarning`'e gidiyordu; olusturma yolu her
halukarda sade "hesap olusturuldu" donuyordu. Hesap gercekten olusuyor -- yalan orada degil,
**soylenmeyende**: makinenin yas siniri islerse FTP kirk iki gun sonra SESSIZCE olur. Artik
`FtpUserDto.PasswordNeverExpires` (uc degerli) tasiniyor; denetim kaydi ve uc mesaji kismi
basariyi ADIYLA yaziyor. Basarili durumda cumle SADE kaliyor.

### 3) SSL -- baglama olculuyordu, SERTIFIKA olculmuyordu -- **ACIK**, kapatildi

`MeasureHttpsBinding:998` yalniz `Protocol == "https"` ve host adina bakiyordu; **`SiteBindingDto`'da
sertifika alani ZATEN YOKTU**, yani olcum yapisal olarak sertifikayi goremiyordu.
`EnsureHttpsBindingAsync:1180` ise baglamayi ONCE kurar (`New-WebBinding`), sertifikayi SONRA
ilistirir (`AddSslCertificate`). Bu siralama tek basina sunu dogurur: ikinci adim duserse geriye
**sertifikasiz bir HTTPS baglamasi** kalir -- tarayici baglantiyi kapatir, site 443'te oludur --
ve panel "HTTPS baglamasi var" deyip ssl.json'a oyle yazar. Ayrica betik sertifika deposunu `My`
diye VARSAYAR ve bunu geri okumaz.

**KAPATMA BICIMI -- TEK KAYNAK.** Depo ZATEN sertifika-farkindalikli bir okuyucuya sahipti:
`ReadHttpsBindingsAsync` (certificateHash + certificateStoreName). KALDIRMA yolu onu dogru
kullaniyor, KURMA yolu kullanmiyordu. Yeni `Ssl/SslBindingVerification.cs` (IIS'e dokunmayan saf
karar mantigi, `SslRemovalPlan` ile ayni gerekce) o okuyucudan beslenir. Okuyucu betigine
`certificateReadable` eklendi: "sertifika YOK" ile "sertifikaya BAKAMADIM" ayni bos parmak izine
dusuyordu. `SslCertificateDto.IsCertificateBound` (uc degerli) + `BindingDetail` eklendi; arayuz
ve denetim kaydi uc durumu ayri gosteriyor.

### 4) ZAMANLAYICI -- taskkill cikis kodu atiliyor, DENEME sayisi "oldurulen" yaziliyordu -- **ACIK**, kapatildi

`BuildStopScript:668-669`: `taskkill.exe ... | Out-Null` sonrasinda `$oldurulen++` **KOSULSUZ**
calisiyordu. `taskkill /F` erisim reddinde 1 ile doner ve surec YASAMAYA DEVAM EDER; panel yine
de arayuzde `panel.tasks.stopDone` = "N surec agaci sonlandirildi" diyordu. Kullanicinin canli
sunucusunda yasanan Chrome/Selenium zinciri tam olarak bu satirla "durduruldu" gorunuyordu.

Artik IKI KANIT birlikte araniyor: cikis kodu 0 **VE** surecin yeniden soruldugunda BULUNAMAMASI
(cikis kodu 0 olsa bile taskkill "termination signal sent" deyip cikabilir). Yeni damgalar
`ADAPANEL-DENENEN` ve `ADAPANEL-HAYATTA`; `StopScheduledTaskResultDto` artik `AttemptedProcesses`
+ `SurvivingProcesses` (ADIYLA, ad(pid)) + `AllKilled` tasiyor.

### 5) ZAMANLAYICI -- DeleteTaskAsync / SetTaskEnabledAsync geri okumuyordu -- **ACIK**, kapatildi

Kardesi `CreateTaskAsync` bu geri okumayi ZATEN yapiyordu (`cron.write_unverified`). Ayni dosyada
bir tarafin kapali obur tarafin acik olmasi, korumanin kural degil TESADUF oldugunu gosterir.
Silme yolu uc yerden cagriliyor ve ucu de "gitti" varsayiyordu (uc kaydi,
`RemoveTasksUnderRootAsync` sayaci, `SiteHostingSettingsService`'in `Running = false` iddiasi).
`SetTaskEnabledAsync`'te olcum zaten elde edilip `| Out-Null` ile atiliyordu:
`Enable-/Disable-ScheduledTask` SONUCTAKI gorev nesnesini dondurur ve `State` tam da sorulan
seydir. Yeni anahtarlar: `cron.delete_failed`, `cron.delete_unverified`, `cron.state_not_applied`,
`cron.state_unverified`.

### 6) IIS -- takma ad kaydi, baglama kaldirilamasa da siliniyordu -- **ACIK**, kapatildi

`SiteAliasService.cs:122-144`: baglama kaldirma hatasi yutuluyor (yalniz `LogWarning`) ve kayit
**her halukarda** siliniyordu. Metodun KENDI yorumu (`:107-109`) tam da bu felaketi anlatiyor:
"yalniz kaydi silmek alan adini IIS'te bagli birakirdi" -- ve kod sessizce onu yapiyordu.
Gorunmezligi yapisal: `GetAliasesAsync:43` kaydi IIS baglamalarina gore SUZER, tersini yapmaz;
kayit gidince oksuz baglamayi gosterecek hicbir ekran kalmaz. Kiraci izolasyonu acisindan ayrica
agir: ad hala eski sitede bagli oldugu icin asil sahibi onu alamaz ("bu ad kullanimda").

**KAPATMA BICIMI.** `taze` zaten okunuyordu ama kayit ONDAN ONCE siliniyor, olcum de yalniz
yonlendirme kuralini yazmak icin kullaniliyordu -- cevap elimizdeydi, sorulmuyordu. Sira
degistirildi: geri okuma ONCE, kayit silme SONRA. Baglama hala duruyorsa
`alias.remove_binding_failed` firlatilir ve **kayit KALIR** (islem yeniden denenebilir); site
okunamazsa `alias.remove_unverified` -- kendi olcum hatamizi geri alinamaz bir "kayboldu"ya
cevirmeyiz. Kardes yol `WinAcmeSslService.RemoveCertificateAsync:1272` ayni kurali zaten
uyguluyordu.

### 7) VERITABANI -- MySQL geri yukleme istemci tarafi `source` kullaniyordu -- **ACIK**, kapatildi

`DatabaseService.cs:907` ve `:712`: `--execute="source <dosya>"`. `source` mysql istemcisinin
KENDI komutudur, SQL degildir: dosyayi istemci satir satir okur ve icindeki bir ifade patladiginda
akisa devam eder. `ImportDumpAsync` yalniz `result.Success`'a baktigi icin YARIM geri yuklenmis
bir veritabani "geri yukleme tamam" olarak raporlanabiliyordu -- ve yonetici o cumleye guvenip
elindeki yedegi siler. PostgreSQL kardesi bu tuzagi `ON_ERROR_STOP=1` ile kapatmis, gerekcesini
yazmis ve TESTLE sabitlemisti (`PostgreSqlDumpArgumentTests.cs:34`); MySQL tarafinda ne bayrak,
ne gerekce, ne test vardi.

**KAPATMA BICIMI.** `ProcessRunner.RunAsync`'e istege bagli `standardInputFilePath` eklendi
(girdi, cikti okuyuculari BASLADIKTAN SONRA beslenir -- ters sira klasik kilitlenmedir) ve iki
MySQL yukleme yolu da TOPLU (batch) moda alindi. Argumanlar `BuildMySqlImportArguments` ile tek
kaynaga cikarildi ve testle sabitlendi (`source` geri sizarsa ya da `--force` eklenirse kirmizi).

### 8) VERITABANI -- HardenAdminAccountsAsync DROP'lari yutunca kayit "temiz" diyordu -- **ACIK**, kapatildi

`MySqlDatabaseProvider.cs:327-340`: `DROP USER` hatalari `catch (MySqlException) { }` ile
yutuluyor ve YALNIZ basarililar sayiliyordu. `DatabasesController.cs:153` de
`removed.Count == 0 ? "Hardening: no insecure admin accounts found." : ...` yaziyordu. Yani
**uc guvensiz hesap BULUNUP ucunun de silinmesi basarisiz oldugunda kayit "guvensiz yonetici
hesabi bulunamadi" diyordu** -- kodun BIR IFADE ONCE SELECT ile saydigi hesaplar hakkinda.
Panelin bilmedigini degil, BILDIGININ TERSINI soylemesi. root@'%' parolasiz kalmasi bu depoda
bilinen kritik acik; onu kapatmak icin dugmeye basan yonetici "temiz" yazisini gorup baska yere
bakar. (Ek bulgu: `FLUSH PRIVILEGES` yalniz `removed.Count > 0` iken kosuyor, yani tam da hepsi
basarisiz olan durumda ATLANIYOR.)

**KAPATMA BICIMI.** Yeni `Core/Dtos/Databases/DatabaseHardeningReport.cs` (Applied / Failed /
Unverified / Measured; `Clean` uc degerli, `Measured` varsayilani **false** = fail-safe).
Sertlestirmeden SONRA guvensiz hesap listesi AYNI sorguyla (tek kaynak:
`GuvensizHesaplariOkuAsync`) yeniden okunur: hala duran her hesap, DROP hata vermemis olsa bile
basarisizdir. PostgreSQL kardesi de ayni rapora gecirildi (ortak erisimi acik kalan veritabani
artik ADIYLA raporlaniyor).

### 9) POSTA -- SetAccountQuotaAsync kardeslerindeki dogrulamadan yoksundu -- **ACIK**, kapatildi

`MailService.cs:1752`: `MutateAsync` yalniz CLI cikis durumuna bakip TAZE workspace donduruyor --
ve o workspace'teki `MaxSizeMB` sunucudan OKUNMUS degerdir (`:222`). Yani cevap donen nesnenin
ICINDEYDI ve hic sorulmuyordu; `MailController.cs:632` de ISTENEN degeri "Mailbox quota set to
{N} MB" diye kaydediyordu. Kardes yol `SetOutboundLimitAsync:3799` bu dogrulamayi zaten yapiyor
(`mail.outbound_limit_unverified`). Yeni anahtarlar: `mail.mailbox_quota_not_applied`,
`mail.mailbox_quota_unverified`. `AccountsMeasured` false ise "uygulanmadi" DENMEZ.

### 10) BILESEN KURULUMU -- roundcube/clamav/vandelay HICBIR kapidan gecmiyordu -- **ACIK**, kapatildi

`ComponentBootstrapper.cs:3024` (`_ => string.Empty`), `:3193` (`_ => null`) ve `serviceName: ""`
yuzunden servis/port denetiminden erken donus: bu uc bilesende kurulumun tek kaniti arsivin
SHA-256'si ve "acma patlamadi" idi. Yanlis kok klasorle acilmis, yarim acilmis ya da bos bir arsiv
"kuruldu" olarak kaydediliyordu. Bedeli sessiz: vandelay yoksa **site yedekleri posta ILETILERINI
icermez**, clamav yoksa tarama hic kosmaz, roundcube yoksa webmail acilmaz.

Aranan dosya adlari UYDURULMADI, deponun KENDI tuketicilerinden alindi:
`StalwartAccountArchiver.cs:42` (vandelay.exe), `AntivirusService.cs:101` (clamscan.exe),
roundcube'un kendi giris noktasi index.php. Ayrica `FindInstalledFile` bulamayinca BOS doner ve
bos dize `File.Exists` kapisini ATLATIYORDU -- "dosya yok" ile "dogrulama tanimli degil" ayni
goruntuye dusuyordu; bu uc bilesen icin ayri bir kapi eklendi.

### MUTASYON KANITI (22 mutasyon, 22'si KIRMIZI -- ikisi ancak TESTI DUZELTTIKTEN SONRA)

Eski davranisi geri getiren 14 mutasyon (FTP TLS hep "yururlukte"; FTP TLS sertifikasi hic
olculmez; SSL sertifikasi hep bagli; okunamayan sertifika "yok" sayilir; taskkill sonucu
denetlenmez; hayatta kalan surec olsa da "hepsi olduruldu"; sertlestirme olculmeden "temiz";
MySQL yine `source`; kosucu standart girdiyi beslemez; takma ad kaydi geri okumadan once silinir;
posta kotasi geri okunmaz; uc bilesen hic dogrulanmaz; bos isaret kapiyi atlatir) ilgili testleri
kirmizi yakti.

**FAZLA KAPATMA NOBETCILERI (8):** FTP TLS hep "uygulanmadi"; SSL sertifikasi hep bagli DEGIL;
hicbir surec olduruldu sayilmaz; sertlestirme hep "eksik"; posta kotasi hep "uygulanmadi"; takma
ad silme hep "baglama duruyor"; takma ad silme hep "okunamadi" -- sekizi de kirmizi. Yani her sey
yolundayken panel HALA sade "yapildi" diyor.

**KENDI TESTIMIN IKI KACAGI OLCULDU VE KAPATILDI (durustluk kaydi).** Ilk turda "taskkill sonucu
denetlenmez" mutasyonu YESIL KALDI: kaynak taramasi `$LASTEXITCODE`'un VARLIGINA bakiyordu ve
kosulun YANINA kosulsuz bir artirma eklenince gecip gidiyordu. Sinama, artirma NOKTASININ
TEKLIGINI olcecek sekilde degistirildi ve karar kurali GERCEK powershell'de dort kombinasyonla
ayrica olculdu. Ikinci kacak: takma ad silmenin fazla-kapatma nobetcisi de kaynak taramasi oldugu
icin "hep basarisiz" mutasyonundan gecti; yerine sahte bir `IIisSiteService` ile GERCEK
`RemoveAliasAsync` kosturan davranis testi yazildi.

### OLCULEMEYENLER (durustce)

* Bu makinede kurulu **IIS FTP sitesi, IIS uygulama havuzu, ayakta MariaDB/MySQL ve Gorev
  Zamanlayici'da panel gorevi YOK**; surec de YUKSELTILMEMIS. Bu yuzden OLCULMEDI: IIS'in
  `controlChannelPolicy` yazimini gercekte kabul edip etmedigi; `AddSslCertificate`'in gercek
  davranisi; `taskkill`'in gercek bir gorev agacindaki cikis kodu; ve **mysql.exe'nin bozuk bir
  dokum karsisinda gercekte hangi cikis kodunu verdigi**.
* Buna karsilik BU MAKINEDE OLCULDU: (a) FTP geri okuma blogu gercek powershell.exe'de
  ayristirildi ve IIS yokken `ADAPANEL-FTP-TLS=||` basiyor -- yani "olculemedi"ye dusuyor,
  "uygulanmadi"ya degil (fail-safe dogrulandi); (b) durdurma betiginin TAMAMI gercek powershell'de
  kosturuldu ve damgalarini basiyor (hicbir surec oldurulmedi, hicbir gorev degistirilmedi);
  (c) `ProcessRunner`'in standart girdiyi GERCEKTEN besledigi ve besleyen bir surecin sifir
  olmayan cikis kodunun `CommandResult`'a dogru yansidigi -- MySQL duzeltmesinin dayandigi
  mekanizma tam olarak budur.

### KIRILAN DAVRANISLAR (bilerek)

1. `IFtpService.SetRequireSslAsync` donus tipi `bool` -> `FtpTlsMeasurement`.
2. `IDatabaseService.HardenAdminAccountsAsync` donus tipi `IReadOnlyList<string>` ->
   `DatabaseHardeningReport`; `POST /db/harden` govdesi degisti.
3. `SchedulerService.DeleteTaskAsync` gorev hala duruyorsa artik HATA firlatiyor (once sessizce
   basarili donuyordu); `SetTaskEnabledAsync` de geri okunan durum istenenle uyusmazsa firlatiyor.
4. `SiteAliasService.RemoveAliasAsync` baglama kalkmadiysa artik HATA firlatiyor ve kaydi
   SILMIYOR (once sessizce siliyordu).
5. `MailService.SetAccountQuotaAsync` kota geri okumada tutmazsa HATA firlatiyor.
6. roundcube/clamav/vandelay kurulumu, beklenen dosya bulunamazsa artik BASARISIZ sayiliyor.
7. MySQL geri yukleme/tasima artik dokumu standart girdiden veriyor (`source` yok).

### YENI DOSYALAR

`src/AdaPanel.Core/Dtos/Ftp/FtpTlsMeasurement.cs`,
`src/AdaPanel.Core/Dtos/Databases/DatabaseHardeningReport.cs`,
`src/AdaPanel.Infrastructure/Ssl/SslBindingVerification.cs`,
`tests/AdaPanel.Infrastructure.Tests/BakilmayanAdaylarDogrulamaTests.cs` (44 test).

**Yeni i18n (tr+en):** `panel.ftp.tlsNotApplied`, `panel.ftp.tlsUnverified`, `ftp.tls_not_applied`,
`ftp.tls_unverified`, `ftp.user_created_password_expires`, `ftp.user_created_expiry_unverified`,
`panel.ssl.certificateNotBound`, `panel.ssl.certificateUnverified`,
`panel.ssl.certificateNotBoundNotice`, `panel.ssl.certificateUnverifiedNotice`,
`panel.tasks.stopProcessesSurvived`, `cron.stop_processes_survived`, `cron.delete_failed`,
`cron.delete_unverified`, `cron.state_not_applied`, `cron.state_unverified`,
`panel.database.hardenedIncomplete`, `panel.database.hardenedUnverified`,
`database.harden_incomplete`, `database.harden_unverified`, `alias.remove_binding_failed`,
`alias.remove_unverified`, `mail.mailbox_quota_not_applied`, `mail.mailbox_quota_unverified`.

**4771 test yesil** (baseline 4727 + 44). `scripts/devam-durumu.ps1`: SAGLAM.

### BU TURDA OLCULDU AMA KAPATILMADI (dogrulanmis ACIK; sonraki tur BURADAN baslasin)

Asagidakiler bu turda dosya:satir ile DOGRULANDI ve ACIK oldugu goruldu -- ama zaman yetmedigi
icin KAPATILMADI. Bunlar "aday" degil, **olculmus acik**tir:

* **`AspNetService.cs:379-380` -- AddAt duserse handler SONA ekleniyor.**
  `try { InvokeMethod(handlers, "AddAt", 0, handler); } catch { InvokeMethod(handlers, "Add", handler); }`
  Bir ustundeki YORUM (`:377-378`) tam da bunun `.php` dosyalarini 404.3 yaptigini yaziyor; kod bir
  satir sonra o sonucu FALLBACK olarak uyguluyor. `catch` ciplak. Ayni desen iki yerde daha:
  `AspNetService.cs:406-407` (`*.asp`) ve `PhpService.cs:1170-1179` (yorumu "calismasini garantiye
  al" diyor, oysa SONA eklemek 404.3'u URETEN siralamadir). Panel `AspView.vue:334` uzerinden
  `aspnet.mixed_handlers_updated` = "Mixed ASP.NET and PHP handlers were enabled" diyor. DTO
  handler SIRASINI hic tasimiyor (`:236` yalniz varlik) ve ustelik `CommitChanges`'ten (`:191`)
  ONCE uretiliyor -- yani bellekteki nesne grafigini anlatiyor, diske ineni degil.
  **Kapatma yonu:** commit'ten SONRA taze bir okumayla handler'in `path="*"` handler'indan once
  gelip gelmedigini olcup DTO'ya uc degerli yazmak.
* **`SiteHostingSettingsService.cs:1522` -- `document.Save(webConfigPath)` metodun SON ifadesi.**
  Geri okuma, XML yeniden ayristirma, IIS tarafi dogrulama ya da HTTP yoklamasi YOK. `Validate`
  (`:1101`) ISTEGI denetliyor, uretilen belgeyi degil. Bozuk bir web.config sitenin TAMAMINA
  500.19 verir ve dosyanin kendi yorumlari (`:1331`, `:1397`, `:1444`) tam da bu sinifi anlatiyor
  -- ama korumalarin hepsi ONLEYICI, hicbiri DENETLEYICI. Uc `sites.hosting_settings.updated`
  yazip arayuz `panel.hosting.saved` gosteriyor; denetim kaydindaki sayilar servisin BELLEKTE
  kurdugu DTO'dan geliyor.
* **`MailEnableMailService.cs` -- COM cagrilarinda `[void]` ve bos `catch`.** Ve **CANLI**:
  `ServiceCollectionExtensions.cs:172-173` + `MailServiceRouter.cs:17-21`, Stalwart yoksa ve
  MailEnable varsa BU sinif calisiyor (bu dev makinesinde ikisi de yok, bu yuzden calisma zamani
  davranisi olculemedi). MailEnable COM metotlari istisna degil TAMSAYI DURUM KODU doner;
  `$ErrorActionPreference='Stop'` bunu YAKALAMAZ, her donus `[void]` ile atiliyor ve betik kosulsuz
  `'OK'` basiyor. En agiri `DeleteAccountAsync:236-249`:
  `try { [void]$mb.RemoveMailbox() } catch {}` + `try { [void]$lg.RemoveLogin() } catch {}`
  -> kutu durabilir, giris hala calisabilir, panel HTTP 200 doner. Ayni desen `:180`, `:195`,
  `:218`, `:225`, `:241`, `:245`, `:261`, `:264`, `:288`, `:304`, `:373`, `:375`. Ayrica UYDURULAN
  degerler: `:472` `GetCertificateStatusAsync` sabit `Active = true`; `:475` `IssueCertificateAsync`
  HIC issuance yapmadan `Active = true`; `:487` `UpdateSpamSettingsAsync` istegi yankiliyor;
  `:383` `SetDomainQuotaAsync` hicbir kota uygulamiyor ama uc "Domain mail quota set to N MB"
  yaziyor. `GetAutoResponderAsync:416-417` okuma hatasini "kapali/bos mesaj"a ceviriyor (hata ile
  bos sonuc ayni goruntu).

### BU TURDA KESFEDILEN, KAYITTA OLMAYAN AYNI SINIFTAN ADAYLAR (dogrulandi, kapatilmadi)

* **`SchedulerService.AddPythonToMachinePathAsync:549-570` + `RuntimeLocator.cs:279-281`.**
  `Set-ItemProperty -Path $adaAnahtar -Name Path` cagrisinda `-ErrorAction Stop` YOK, kosucu gevsek,
  geri okuma YOK; basilan `ADAPANEL-DEGISIKLIK=` sayisi EKLENMESI ISTENEN klasor sayisidir. Uc
  (`CronController.cs:187`) "Python added to machine PATH (N folder(s))" yaziyor. Bu MAKINE GENELI
  bir yazmadir (HKLM ortam degiskeni).
* **`SiteAliasService.YonlendirmeKuraliniYaz:225-232`** -- web.config rewrite kurali yazimi tumuyle
  log-only bir `catch` icinde; "birincile yonlendir" kaydedildi gorunur, kural hic yazilmamis
  olabilir. (Bu turda takma ad SILME yolu kapatildi, bu kural yazimi KAPATILMADI.)
* **`SiteHostingSettingsService.cs:431` ve `:531`** -- kuyruk iscisi icin `Running = false`
  iddiasi OLCULMEDEN yaziliyor. (`RemoveTasksUnderRootAsync:313` sayaci ise `DeleteTaskAsync`
  artik firlattigi icin dolayli olarak duzeldi.)

---

## TUR: "OLCULDU AMA KAPATILMADI" LISTESI KAPATILDI (6/6 aday)

Onceki turun bu dosyanin sonuna dosya:satir ile yazdigi ALTI acigin HEPSI kapatildi. Aranan
sinifin bu turdaki iki alt bicimi de bulundu: (a) sonuc HIC okunmuyor, (b) **olcum yapiliyor ama
sonucuna BAKILMIYOR**. (b) bu turun BASKIN bicimiydi: `MailEnableMailService`'in butun mutasyon
metotlari `await RunComAsync(...)` deyip ardindan `return await GetWorkspaceAsync(...)` yapiyordu
-- yani cevap ELDEYDI ve sorulmuyordu.

### 1. `AspNetService.cs:379/:406` + `PhpService.cs:1170` -- AddAt duserse handler SONA ekleniyor

**ACIKTI.** Uc yerde ayni dort satir: `try { AddAt(0, handler) } catch { Add(handler) }`. Bir
ustlerindeki YORUM sona eklemenin `.php` dosyalarini 404.3 yaptigini yaziyordu; kod bir satir
sonra onu FALLBACK olarak uyguluyordu. DTO handler SIRASINI hic tasimiyordu.

**KAPATMA.** `HandlerOrderMeasurement` (Core/Dtos/Asp) + `IisConfigurationAccess.InsertHandlerFirst`
(TEK KAYNAK; uc kopya kaldirildi) + `AspNetService.ExecuteWriteAsync` icin `afterCommit` kancasi:
commit'ten SONRA **TAZE bir ServerManager** ile handler listesi okunur ve handler'in `path="*"`
kaydindan ONCE olup olmadigi OLCULUR. Fallback duruyor (hic yazmamaktan iyidir) ama artik
ISARETLENIYOR. `PhpService.SetSitePhpVersionAsync` de ayni olcumu tasiyor. Uc/arayuz sade
"etkinlestirildi" demiyor; AspView kalici bir sira rozeti gosteriyor.

### 2. `SiteHostingSettingsService.cs:1522` -- `document.Save` son ifade

**ACIKTI.** Geri okuma, yeniden ayristirma, IIS tarafi dogrulama YOK.

**KAPATMA.** `WebConfigWriteMeasurement` + `KaydetVeGeriOku`: yazma sonrasi dosya diskten TEKRAR
okunup `XNode.DeepEquals` ile karsilastirilir; tutmuyorsa **ONCEKI ICERIK BAYT BAYT GERI KONUR**
(silme yok; dosya daha once yoksa yerine gecerli bos `<configuration/>` birakilir). Ikinci kapi:
`IisWebConfigOkuyabiliyorMu` -- IIS belgeyi cozumleyebiliyor mu (500.19 sinifi). Dogrulanmayan
yazma artik "kaydedildi" DEMIYOR, firlatiyor.

**BU TURDA BULUNAN EK ACIK (ayni metotta):** metodun ILK ifadesi olan `XDocument.Load` da
korumasizdi -- kilitli/bozuk bir web.config'te ham IOException/XmlException disari cikiyordu, ve
`document.Element("configuration")!` kok eleman farkliysa NRE atardi. Ikisi de kapatildi: mevcut
belge okunamiyorsa **HIC YAZILMAZ** (kullanicinin dosyasini "duzeltmek" icin ustune yazmak panelin
yapabilecegi en pahali sey).

### 3. `MailEnableMailService.cs` -- COM `[void]` + bos `catch` + UYDURULAN degerler

**ACIKTI, ve DI'da CANLI.**

**KAPATMA -- iki koldan.**

* **Uydurulan degerler** (COM'a hic dokunmayan, tamamen olculebilir metotlar):
  `GetCertificateStatusAsync` sabit `Active=true` -> `false` + `mailenable-unmeasured`;
  `IssueCertificateAsync` hic issuance yapmadan `Active=true` -> 409 firlatiyor;
  `SetDomainQuotaAsync` hicbir kota uygulamadan calisma alani donuyordu -> 409;
  `UpdateSpamSettingsAsync` istegi yankiliyordu -> 409; `GetSpamSettingsAsync` ucu de sabit `true`
  -> `Configured=false, Enabled=false`.
* **Sonucu okumayan mutasyonlar:** durum kodu konvansiyonu TAHMIN EDILMEDI (bu makinede MailEnable
  YOK, konvansiyon OLCULEMEZ ve yanlis tahmin CANLI sunucuda sahte hata uretirdi). Bunun yerine
  ZATEN YAPILAN geri okumanin sonucuna BAKILIYOR: `WorkspaceOkuAsync` artik `(Workspace, Olculdu)`
  donuyor ve `KapiAcVarlik` uc degerli karar veriyor (olculdu+tutuyor / olculdu+tutmuyor /
  OLCULEMEDI). Kapsanan: domain olustur/sil, hesap olustur/sil, takma ad olustur/sil, kutu kotasi,
  otomatik yanit. `GetWorkspaceAsync`'in "ayristirilamayan cikti = bos liste" dali da kapandi.
  `SetAccountPasswordAsync` DOGRULANAMAZ (parola geri okunamaz) ve bu artik StatusMessage'da
  ACIKCA yaziyor.

### 4. `AddPythonToMachinePathAsync` -- MAKINE GENELI yazma, olcumsuz

**ACIKTI.** `Set-ItemProperty` cagrisinda `-ErrorAction Stop` YOK, geri okuma YOK, raporlanan sayi
EKLENMESI ISTENEN sayiydi.

**KAPATMA.** `MachinePathMeasurement` + `RuntimeLocator.MakinePathDogrulamaParcasi` (TEK KAYNAK:
ayni parca hem ekleme betiginin sonunda hem de yalniz-okuyan `BuildMachinePathProbeScript`'te
kosar). Betik yazmadan sonra PATH'i TAZE okuyup `ADAPANEL-PATH-HEDEF` ve
`ADAPANEL-PATH-DOGRULANDI` basiyor; damga YOKSA sonuc "sifir" degil OLCULEMEDI.

### 5. `SiteAliasService.YonlendirmeKuraliniYaz` -- log-only catch

**ACIKTI.** `belge.Save` son ifade, butun govde yalniz-gunluge-yazan bir catch icinde; donus
degeri yoktu.

**KAPATMA.** `AliasRedirectRuleMeasurement` + `KuralDiskteVarMi` geri okumasi. FIRLATMIYOR (kural
yazilamasa da takma ad CALISIR, baglama duruyor) ama sonuc `SiteAliasWorkspaceDto`'ya tasiniyor;
panel "yonlendirme acik" demeden once ona bakiyor.

### 6. `SiteHostingSettingsService.cs:431` ve `:531` -- `Running` olculmeden yaziliyor

**ACIKTI, ve dusunuldugunden genis.** `Running = false` iddiasi olcumsuzdu VE silme hatasi "gorev
zaten yok" varsayilarak yutuluyordu -- silme GERCEKTEN basarisiz oldugunda da panel "durduruldu"
diyordu. **Kardes BASLATMA yollari (`:397`, `:600`) ayni sekilde `Running = true` diyordu**;
onlar da kapatildi (TEK KAYNAK: `DurumuOlcAsync` iki yonde de kullaniliyor).

---

### MUTASYON KANITI (37 mutasyon)

**Ilk kosumda 35 kirmizi, 2 YESIL kaldi; ikisi de duzeltildi ve bu kayda gecirilmeyi hak ediyor.**

* **M7 "geri okuma YOK" YESIL KALDI.** web.config karsilastirmasini HICBIR test kosturmuyordu:
  yarim bir yazimi gercek bir dosya sisteminde kasten uretmek mumkun degil. `UpdateWebConfig`
  metoduna `geriOkuyucu` OLCULEBILIRLIK kancasi eklendi (yazma ve GERI ALMA hala gercek dosya
  sisteminde); artik kirmizi.
* **M28 "-ErrorAction Stop kaldirildi" YESIL KALDI** -- cunku sinama KAYNAK TARAMASIYDI
  (`Assert.Contains("-ErrorAction Stop", betik)`) ve dosyanin BASKA bir satirindaki ayni metin
  korumayi gizliyordu. **Bu oturumda kaynak tarayan testin YEDINCI yanilmasi.** Yerine iki sey
  kondu: (a) GERCEK powershell.exe'de olculen hata yayilimi, (b) SATIR BAZLI kapi -- her
  `Set-ItemProperty` ve `Get-ItemProperty` satiri KENDI `-ErrorAction Stop` korumasini tasimali.

**FAZLA KAPATMA NOBETCILERI (9):** handler sirasi hep "uyari"; hicbir handler sirali sayilmaz;
hicbir web.config dogrulanmis sayilmaz; web.config uyarisi hep dolu; posta kapisi HER ZAMAN
reddeder; PATH hicbir zaman uygulanmadi; PATH her zaman uygulandi; kural olcumu hic uygulanmadi;
isci durumu hic olculmedi -- dokuzu da kirmizi. Yani her sey yolundayken panel HALA sade "yapildi"
diyor.

### BU MAKINEDE GERCEKTEN OLCULENLER

* **IIS KURULU** (`%windir%\System32\inetsrv\Microsoft.Web.Administration.dll` VAR) ama surec
  YUKSELTILMEMIS. `IisWebConfigOkuyabiliyorMu` gercekten kosturuldu: hem bilinmeyen bir site adi
  hem "Default Web Site" icin **NULL (olculemedi)** donuyor -- yani fail-safe DOGRULANDI, erisim
  reddi "web.config bozuk" sayilmiyor. Hicbir site olusturulmadi/degistirilmedi.
* **`-ErrorAction Stop` landmine'i GERCEK powershell.exe'de olculdu:** var olmayan bir kayit
  yoluna `Set-ItemProperty` + ardindan baska bir ifade -> betik AKMAYA DEVAM ETTI ve surec
  **SIFIR** ile cikti; `-ErrorAction Stop` ile cikis kodu SIFIR DEGIL ve betik akmadi. (Tek
  ifadelik bir olcum YANILTIR: onda gevsek cagri da 1 doner -- bu da olculdu.)
* **Makine PATH sondasi gercek HKLM PATH'i uzerinde kosturuldu** (YALNIZ OKUYARAK): Windows
  dizini bulundu (1/1), var olmayan klasor hedefe SAYILMADI (0/0), diskte var ama PATH'te olmayan
  klasor EKSIK olculdu (0/1), karisik durum 1/2.
* **web.config yazma/geri okuma/GERI ALMA gercek dosyalarla olculdu:** IIS reddinde onceki icerik
  BAYT BAYT geri kondu; dosya yokken geri alma SILMEDI, gecerli bos belge birakti; kilitli dosyada
  yazma dustu ve onceki icerik bozulmadi; bozuk ya da yabanci kokli belgenin USTUNE YAZILMADI.
* **Ekleme betigi gercek PowerShell AYRISTIRICISINDAN gecirildi** (kosturulmadan): 0 hata.

### OLCULEMEYENLER (durustce)

* **MailEnable BU MAKINEDE YOK** (32-bit COM: MEAOPO/MEAOAU/MEAOAM). COM metotlarinin gercek
  davranisi -- ozellikle **durum kodu konvansiyonu** ve `RemoveLogin`'in eksik bir giriste ne
  yaptigi -- OLCULMEDI. Kapatma tam da bu yuzden konvansiyona degil GERI OKUMAYA dayandirildi.
  Ayrica: `MEAOAU.Login` kaydinin hayatta kalip kalmadigi calisma alani okumasinda **GORUNMEZ**
  (kutular listeleniyor, giris kayitlari degil) -- bu, account_delete ayrintisina ACIKCA yazildi.
* **HKLM PATH'e GERCEK yazma OLCULMEDI** (surec yukseltilmemis; makine geneli PATH'i degistirmek
  bu turun kapsami disinda). Olculen sey, panelin sonucu NASIL OKUDUGUDUR.
* **Gercek bir IIS handler koleksiyonunun `AddAt(0, ...)` cagrisini hangi kosulda reddettigi
  OLCULMEDI.** Yansima yardimcilarinin GERCEK kodu, IIS'in yerine gecen bir koleksiyon uzerinde
  kosturuldu.
* **IIS sondasinin `true` (kabul) ve `false` (ret) dallari OLCULMEDI:** kabul dali yukseltilmis
  surec, ret dali GERCEKTEN bozuk bir web.config'e sahip GERCEK bir IIS sitesi gerektirir.
* **Gorev Zamanlayici'da panel gorevi YOK:** gercek bir schtasks silme/olusturma turu OLCULMEDI.

### KIRILAN DAVRANISLAR (bilerek)

1. `ISchedulerService.AddPythonToMachinePathAsync` donus tipi `Task<int>` ->
   `Task<MachinePathMeasurement>`; `POST /cron/environment/python-path` GOVDESI DEGISTI (`added`
   duruyor, yaninda `verified`, `expected`, `measured`, `applied`, `detail` geldi).
2. `SiteHostingSettingsService.UpdateWebConfig` donus tipi `void` -> `WebConfigWriteMeasurement`;
   `PUT /sites/{ad}/hosting-settings` dogrulanamayan yazimda artik **FIRLATIYOR** (once sessizce
   "kaydedildi" diyordu).
3. `MailEnableMailService`: `SetDomainQuotaAsync`, `IssueCertificateAsync`,
   `UpdateSpamSettingsAsync` artik 409 FIRLATIYOR (once sessizce basarili donuyorlardi).
   `GetCertificateStatusAsync` ve `GetSpamSettingsAsync` artik `false` donuyor. Mutasyon metotlari
   dogrulanamazsa FIRLATIYOR.
4. `GetAutoResponderAsync` cikti ayristirilamazsa FIRLATIYOR (once "kapali/bos mesaj" donuyordu);
   `SetAutoResponderAsync` geri okunan durum istenenle uyusmazsa FIRLATIYOR.
5. `AspSiteConfigurationDto`, `SitePhpAssignmentDto`, `SiteHostingSettingsDto`,
   `SiteAliasWorkspaceDto`, `QueueWorkerStatusDto` yeni olcum alanlari tasiyor (EKLEME; mevcut
   alanlar degismedi).

### YENI DOSYALAR

`src/AdaPanel.Core/Dtos/Asp/HandlerOrderMeasurement.cs`,
`src/AdaPanel.Core/Dtos/Sites/WebConfigWriteMeasurement.cs`,
`src/AdaPanel.Core/Dtos/Sites/AliasRedirectRuleMeasurement.cs`,
`src/AdaPanel.Core/Dtos/Scheduler/MachinePathMeasurement.cs`,
`tests/AdaPanel.Infrastructure.Tests/HandlerSirasiOlcumTests.cs` (11),
`tests/AdaPanel.Infrastructure.Tests/WebConfigYaziGeriOkuTests.cs` (16),
`tests/AdaPanel.Infrastructure.Tests/GeciciProbeOlcumu.cs` -> icinde `IisSondasiFailSafeTests` (2),
`tests/AdaPanel.Infrastructure.Tests/MailEnableDurustlukTests.cs` (10),
`tests/AdaPanel.Infrastructure.Tests/MakinePathOlcumTests.cs` (9),
`tests/AdaPanel.Infrastructure.Tests/TakmaAdKuraliVeIsciDurumuTests.cs` (10).

**Yeni i18n (tr+en, 29 anahtar):** `aspnet.handler_shadowed`, `aspnet.handler_order_unverified`,
`aspnet.handler_missing`, `panel.aspnet.handlerShadowed`, `panel.aspnet.handlerOrderUnverified`,
`panel.aspnet.handlerMissing`, `sites.hosting_settings_write_failed`,
`sites.hosting_settings_unverified`, `sites.hosting_settings_rejected_by_iis`,
`sites.hosting_settings_iis_unverified`, `panel.hosting.savedIisUnverified`,
`mail.domain_create_not_applied`, `mail.domain_create_unverified`,
`mail.domain_delete_not_applied`, `mail.domain_delete_unverified`,
`mail.account_create_not_applied`, `mail.account_create_unverified`,
`mail.account_delete_not_applied`, `mail.account_delete_unverified`,
`mail.alias_create_not_applied`, `mail.alias_create_unverified`,
`mail.alias_delete_not_applied`, `mail.alias_delete_unverified`,
`mail.autoresponder_unverified`, `mail.autoresponder_not_applied`,
`mail.domain_quota_not_supported`, `mail.certificate_issue_not_supported`,
`mail.spam_settings_not_supported`, `cron.python_path_unverified`,
`cron.python_path_incomplete`, `alias.redirect_rule_not_applied`,
`alias.redirect_rule_unverified`, `sites.queue_worker_state_unverified`.

**4827 test yesil** (baseline 4771 + 56). `scripts/devam-durumu.ps1`: SAGLAM.

### BU TURDA BAKILMAYAN (sonraki tur icin)

* `MailEnableMailService.SetAccountPasswordAsync` -- parola geri okunamadigi icin sonuc HALA
  dogrulanmiyor; simdilik yalnizca StatusMessage ile ACIKCA soyleniyor. Gercek kapatma, MailEnable
  kurulu bir makinede `GetLogin` davranisinin OLCULMESINI gerektirir.
* `MailEnableMailService.DeleteAccountAsync` -- posta kutusunun gittigi olculuyor ama
  **`MEAOAU.Login` kaydi olculMUYOR** (calisma alani okumasinda gorunmuyor). Kutu silinip giris
  hayatta kalirsa panel bunu goremez.
* `MailEnableMailService`'in liste/kuyruk/relay/dinleyici metotlari (`GetListenersAsync`,
  `GetQueueAsync`, `GetRelayAsync`, `SetRelayAsync`, `GetOutboundLimitAsync`,
  `SetOutboundLimitAsync`) bu turda INCELENMEDI.

---

## TUR: "BAKILMADI" LISTESINDEKI UC ADAY KAPATILDI (05.09.2026, dorduncu tur) -- commit ATILMADI

Onceki turun durum kaydina "BAKILMADI" diye yazdigi uc maddenin **UCU DE ACIK CIKTI**.

### ADAY 1 -- `SetAccountPasswordAsync`: ACIK, KAPANDI

Onceki tur "parola geri okunamaz, dogrulanamaz" demis ve yalniz `StatusMessage`'a bir cumle
koymustu. Iki sey bulundu:

1. **Cumle SESSIZCE DUSEBILIYORDU.** `StatusMessage` YALNIZ bos oldugunda yaziliyordu; calisma
   alani okumasi kendi hata mesajini birakmissa (COM'a erisilemedigi durum -- yani en kotu
   durum) kullanici parola uyarisini HIC gormuyordu.
2. **"Dogrulanamaz" DOGRU DEGILDI.** MEAOAU.Login'in DOGRULANMIS uye listesinde (CODEX_MEMORY,
   gercek sunucudan `Get-Member` ciktisi) `Password` OKUNABILIR bir ozelliktir. Geri okuma
   MUMKUN; olculemeyen sey, `GetLogin`'in eksik kayitta ne yaptigiydi.

**KAPATMA -- KENDINI KALIBRE EDEN SONDA.** Durum kodu konvansiyonu TAHMIN EDILMEDI; sondanin
AYIRT EDIP EDEMEDIGI CALISMA ANINDA OLCULUYOR. Ayni sonda uc kez kosar: (a) islemden ONCE var
olan kayit uzerinde -- POZITIF KONTROL, (b) var olmasi IMKANSIZ bir kullanici adi uzerinde --
NEGATIF KONTROL, (c) islemden SONRA. Sonda, MailEnable'in asla uretmeyecegi bir NOBETCI degeri
`Rights`'a yazip `GetLogin` cagirir ve nobetcinin degisip degismedigine bakar.

* ONCE bos **VEYA** KONTROL dolu -> sonda ayirt edemiyor -> **OLCULEMEDI** (ne basarili ne
  basarisiz). Konvansiyon ne olursa olsun bu dal SAHTE HATA uretmez.
* ONCE dolu **ve** KONTROL bos -> sonda KALIBRE; SONRA'nin dolu/bos olmasi dogrudan cevaptir.

Parola karari: kayit YOK -> **UYGULANMADI** (firlatir, `mail.account_password_not_applied`) ·
kayit VAR + parola birebir esit -> **DOGRULANDI** (uyari YOK: `StatusMessage` panelde HATA seridi
cizer, olculmus basariya mesaj yazmak sahte alarm olurdu) · kayit VAR + esit degil ->
**GERI OKUNAMADI** (sunucu parolayi ozetliyor olabilir; FIRLATMAZ, cumle yazar) · kalibre
edilemedi -> **OLCULEMEDI** (cumle yazar). **PAROLA CIKTIYA YAZILMAZ:** esitlik PowerShell
tarafinda olculur, disari yalniz mantiksal deger cikar (sinamayla olculuyor).

### ADAY 2 -- `DeleteAccountAsync`, `MEAOAU.Login` kaydi: ACIK, KAPANDI

Kutunun gittigi olculuyordu, GIRIS KAYDI olculmuyordu. **Bu, silinen musterinin POSTAYA
ERISIMININ SURMESI demekti** (kutu yok, giris var -> IMAP/SMTP oturumu hala acilabilir).

Onceki tur "giris kayitlari calisma alani okumasinda gorunmez" demisti; **NEDENI de olculdu:**
MEAOAU.Login'in dogrulanmis uye listesinde `FindFirst/FindNext` **YOKTUR** (kardes nesnelerin
-- PostOffice/Mailbox/AddressMap -- hepsinde var). Yani giris kayitlari LISTELENEMEZ; tek yol
tekil sondadir.

**KAPATMA:** ayni kalibre sonda. `Var` -> `mail.account_delete_login_survived` (ayrinti:
"giris kaydi durdugu surece silinen hesap POSTAYA ERISEBILIR") · `Olculemedi` ->
`mail.account_delete_login_unverified` · `Yok` -> kapi acilir, panel sade "silindi" der.
Ayrica **iki BOS `catch` KALDIRILDI**: `RemoveMailbox`/`RemoveLogin` hatalari artik yutulmuyor,
hata metni ayrintiya tasiniyor (once tamamen kayboluyordu).

### ADAY 3 -- liste/kuyruk/role/dinleyici: IKISI ACIK, GERISI TEMIZ

* **ACIK: `GetListenersAsync` BOS KOLEKSIYON donuyordu** ve arayuz bos listeyi
  `panel.mail.noListeners` -- "dinleyici yok" -- diye yaziyordu. Hicbir sey olculmeden verilen
  bu cevap, 25/587/143'u GERCEKTEN dinleyen bir sunucuda yoneticiye "posta hic dinlenmiyor"
  gosteriyordu. **AYNI SINIF DAHA GENIS CIKTI: BIRINCIL yol olan Stalwart `MailService`
  de ayni seyi yapiyordu** -- CLI bulunamazsa, yonetici parolasi okunamazsa ya da sorgu
  duserse `return []`. Kapatma: `MailListenerSnapshotDto` (`Installed`/`Measured`/`Listeners`)
  -- kuyruk ve rolede ZATEN VAR OLAN desen (`Configured`) dinleyicilerde eksikti.
  `MailService.GetClientSettingsAsync` artik dinleyiciler OLCULEMEDIYSE `null` doner (uydurma
  port yayinlamaz).
* **ACIK: `GetQueueMessageAsync` `null` donuyordu** -> uc bunu 404 "ileti bulunamadi" yapiyordu:
  hic bakilmamis bir kuyruk icin "boyle bir ileti yok". Kardes `DeleteQueueMessageAsync` ayni
  durumda ZATEN `mail.queue_not_supported` firlatiyordu -- iki uc ayni gercek icin iki ayri sey
  soyluyordu. Artik ikisi de ayni seyi der.
* **TEMIZ (nobetci kondu):** dagitim listeleri (409 `mail.lists_unsupported`), `GetQueueAsync`
  (`Configured=false`), `GetRelayAsync` (`Configured=false`), `SetRelayAsync` (400),
  `GetOutboundLimitAsync` (`Configured=false` -- arayuz de bunu onurlandiriyor: MailView
  "limit kurulu degil" uyarisi cizer), `SetOutboundLimitAsync` (400),
  `GetClientSettingsAsync` (null -> autodiscover 404).

### MUTASYON KANITI (19 mutasyon, 19'u da KIRMIZI)

Kalibrasyonun her bacagi ayri ayri: negatif kontrol kaldirildi (2 kirmizi) · pozitif kontrol
kaldirildi (1) · ayristirma denetimi kaldirildi (1) · var/yok ters cevrildi (8) ·
`SondaDoldu`'nun hata denetimi kaldirildi (1) · `RemoveLogin` hatasi yeniden yutuldu (1) ·
negatif kontrol GERCEK kullaniciyla yapildi (5) · parola karari bozuldu (M8/M9/M10/M16/M19,
2'ser) · **parola ciktiya sizdirildi (1)** · uc kapinin ucu de kaldirildi (1'er) · parola
uyarisi mevcut mesaj varken dusuruldu -- yani ESKI HATA geri kondu (1) · dinleyiciler
"olculdu" sayildi (1) · kuyruk iletisi yine `null` dondu (1).

NOT -- OLCUM ARACININ KENDISI YANILDI: ilk kosumda M7 "YESIL KALDI" gorundu; sebep mutasyonun
HIC UYGULANMAMIS olmasiydi (cok satirli desen, dosya CRLF; ayrica `grep -qF` cok satirli
kalibi VEYA gibi degerlendiriyor). Uygulanma denetimi `cmp` ile degistirilince M7 kirmizi
oldu (5 sinama). Bir mutasyonun yesil kalmasi once ARACIN dogrulanmasini gerektirir.

**FAZLA KAPATMA NOBETCILERI (7):** her sey yolundayken silme kapisi ACILIR · parola
DOGRULANDI'da SAHTE UYARI yazilmaz · "dogrulanamadi" FIRLATMAZ (parola yazilmis olabilir) ·
ozetleyen sunucuda "uygulanmadi" DENMEZ · kuyruk/role/giden limiti hala "yapilandirilmadi"
der · dagitim listeleri hala 409 · olculmus bos dinleyici listesi hala "yok" diyebilir.

### BU MAKINEDE GERCEKTEN OLCULENLER

* **URETIM BETIGININ KENDISI GERCEK `powershell.exe`'de KOSTURULDU.** `SilmeBetigi` ve
  `ParolaBetigi` internal yapildi; sinama bu metinlerin KOPYASINI degil KENDISINI kosturur.
  COM nesnesinin yerine, betikteki OLCULEBILIRLIK KANCASI (`$MeGirisFabrikasi`/
  `$MeKutuFabrikasi` -- uretimde tanimsizdir, `-File` taze kapsamda kosar ve gercek COM olusur)
  uzerinden bir stand-in nesne verilir. **Kaynak taramasi YOK.**
* Uc ayri sunucu davranisi gercekten kosturuldu: `GetLogin` eksik kayitta (a) sessizce
  basarisiz, (b) FIRLATIR, (c) HER DURUMDA doldurur. Sonda (a) ve (b)'de DOGRU olcuyor,
  (c)'de OLCULEMEDI diyor -- konvansiyon tahmin edilmeden.
* `RemoveLogin` hicbir sey yapmayinca / firlatinca kaydin DURDUGU olculdu.
* Parolanin sonda ciktisinda GORUNMEDIGI olculdu.
* MailEnable COM'u olmayan bu makinede `DeleteAccountAsync`/`SetAccountPasswordAsync`
  GERCEKTEN cagrildi: ikisi de FIRLATIYOR (`mail.command_failed`), sessizce basarili DONMUYOR.

### OLCULEMEYENLER (durustce)

* **MailEnable BU MAKINEDE HALA YOK.** COM durum kodu konvansiyonu, `GetLogin`'in eksik
  kayitta gercek davranisi ve `Password`'un gercekten duz metin donup donmedigi OLCULMEDI.
  Kapatma tam da bu yuzden konvansiyona degil KALIBRASYONA dayandirildi.
* **Stalwart `MailService.GetListenersAsync`'in olculemeyen dali KOSTURULMADI** (Stalwart CLI
  bu makinede yok). Yapisal olarak `Measured` yalniz basari dalinda `true` yapiliyor.
* ~~**`DeleteAccountAsync`/`SetAccountPasswordAsync` govdesinin kapilari CAGIRDIGI** COM olmadan
  kosturulamadi~~ -> **KAPANDI (06.09.2026, govde olcum dikisi):** bkz. asagidaki
  "GOVDE OLCUM DIKISI" bolumu. Govdeler artik PowerShell/MailEnable olmadan kosturuluyor ve
  kapilari cagirdiklari olculuyor.
* **Arayuzun dinleyici uyarisini cizdigi** gercek tarayicida olculmedi (typecheck + build temiz).

### KIRILAN DAVRANISLAR (bilerek)

1. `IMailService.GetListenersAsync` donus tipi `Task<IReadOnlyCollection<MailListenerDto>>` ->
   `Task<MailListenerSnapshotDto>`; **`GET /mail/listeners` GOVDESI DEGISTI** (dizi yerine
   `{installed, measured, listeners}`). Frontend (`api/mail.ts`, `MailView.vue`) guncellendi.
2. `MailEnableMailService.GetQueueMessageAsync` artik 400 FIRLATIYOR (once `null` -> 404).
3. `DeleteAccountAsync` giris kaydi dogrulanamaz/duruyorsa FIRLATIYOR (once HTTP 200).
4. `SetAccountPasswordAsync` parola UYGULANMADIYSA FIRLATIYOR; dogrulandiginda artik uyari
   GOSTERMIYOR (once her durumda uyari yaziyordu).
5. `MailService.GetClientSettingsAsync` dinleyiciler olculemediyse `null` doner.
6. `panel.mail.noListeners` metni duzeltildi: "Dinleyici bilgisi alinamadi" (yanlis) ->
   "Olculdu: bu sunucuda tanimli dinleyici yok".

### YENI DOSYALAR / ANAHTARLAR

`tests/AdaPanel.Infrastructure.Tests/MailEnableGirisKaydiSondasiTests.cs` (21 sinama);
`MailListenerSnapshotDto` (`Dtos/Mail/MailListenerDto.cs` icinde).
**Yeni i18n (tr+en, 4 anahtar):** `mail.account_delete_login_survived`,
`mail.account_delete_login_unverified`, `mail.account_password_not_applied`,
`panel.mail.listenersUnmeasured`.

**4853 test yesil** (baseline 4827 + 26). `scripts/devam-durumu.ps1`: SAGLAM.

### KARDES ACIK -- `CreateAccountAsync`, `MEAOAU.Login` kaydi: ACIK, KAPANDI (sonraki tur)

Olusturma HEM kutuyu HEM giris kaydini yaziyordu, ama yalniz KUTU dogrulaniyordu
(`HesapVarMi`). `AddLogin()` sessizce basarisiz olursa panel "hesap acildi" der, kutu listede
GORUNUR, **musteri IMAP/SMTP'ye GIRIS YAPAMAZ**. Silmenin kardesi.

**KALIBRASYON SILMENIN AYNISI DEGIL -- kopyalanamaz.** Silmede `once` POZITIF KONTROLDUR
(kayit islemden once VARDIR). Olusturmada kayit islemden once ZATEN YOKTUR: **pozitif kontrol
KURULAMAZ.** Karar bu yuzden asimetrik:

* KONTROL (imkansiz ad) DOLU -> sonda kor -> **OLCULEMEDI**.
* ONCE DOLU -> kayit islemden once de vardi (yarim silinmis hesabin OKSUZ giris kaydi);
  "sonra dolu" BIZIM yazdigimizi gostermez, kayit ESKI PAROLAYLA duruyor olabilir ->
  **OLCULEMEDI**.
* KONTROL BOS **ve** SONRA DOLU -> sonda ayni kosuda hem ayirt ediyor hem kaydi goruyor ->
  **VAR** (tek olumlu dal; kapi acilir, panel sade "olusturuldu" der).
* KONTROL BOS **ve** SONRA BOS -> **"OLUSTURULMADI" DENMEZ.** Kontrolun bos olmasi sondanin
  FAZLADAN doldurmadigini gosterir, VAR OLANI gorebildigini KANITLAMAZ -> **OLCULEMEDI**.
  Aksi halde `GetLogin`'i geri yazmayan CALISAN bir kurulumda her hesap acilisinda SAHTE HATA
  uretirdik. `OlusturmaSondasiniYorumla` bu nedenle **`Yok` DONDURMEZ** (16 birlesimin tamami
  sinanıyor).

**KAPATMA:** `OlusturmaBetigi` -- sonda parcalari (`FabrikaBasligi`, `GirisSondasiBetigi`,
`SondaCiktisi`) SILMEDEKININ AYNISI, ikinci nusha YOK. `[void]$mb.AddMailbox()` /
`[void]$lg.AddLogin()` try/catch ile olculuyor; yutulan hatalar `AddMailbox:`/`AddLogin:`
etiketiyle ayrintiya tasiniyor (`SilmeHatalari` govdesi `SondaHatalari`'ya cikarildi, etiketler
cagri yerinden geliyor). `Olculemedi` -> `mail.account_create_login_unverified` (yeni i18n,
tr+en). Sinamalar URETIM BETIGININ KENDISINI gercek `powershell.exe`'de kosturuyor
(`$MeGirisFabrikasi`/`$MeKutuFabrikasi` kancasi); kaynak taramasi YOK.

**MUTASYON (6/6 KIRMIZI):** bos dal `Yok` yapildi (6 sinama kirmizi) · `onceDoldu` ayagi
dusuruldu (3) · "her durumda OLCULEMEDI de" cozumu (3 -- fazla kapatma nobetcisi tutuyor) ·
kapi `Olculemedi`de sessizce acildi (2) · `AddLogin` try/catch'i kaldirildi (1) · negatif
kontrol GERCEK kullaniciyla yapildi (2).

**OLCULEMEYEN:** `CreateAccountAsync` GOVDESININ kapiyi cagirdigi COM olmadan kosturulamadi
(MailEnable bu makinede yok; silme/parola yolunda da ayni bosluk var). Kapinin ve karar
kuralinin KENDISI dogrudan sinaniyor.
-> **Silme/parola icin KAPANDI (06.09.2026)**, bkz. "GOVDE OLCUM DIKISI". `CreateAccountAsync`
govdesi ayni dikisle kosturulabilir durumda ama HENUZ SINANMADI (acik madde).

### BU TURDA BAKILMAYAN (sonraki tur icin)
* `GetDnsRecordsAsync` MailEnable tarafinda SABIT kayit uretiyor (MX/SPF/DMARC); sunucunun
  gercek yapilandirmasindan okunmuyor.
* `EnsureServerHostnameAsync` her zaman `false`; MailEnable karsilama adi hic olculmuyor.

## CANLI DENEMEDE BULUNANLAR (0.8.20 kurulumu, 05.09.2026)

Panel bu makineye kuruldu (0.8.9 -> 0.8.20) ve arayuzden denendi. Asagidakiler GERCEK
SISTEMDE olculdu; ikisi yeni bulgu.

### DOGRULANANLAR (canlida ilk kez)
* Izinleri Onar: denetim kaydi "Modify yerinde (DACL'den okundu)" yaziyor ve YUKSELTILMIS
  icacls ile bagimsiz teyit edildi -> `IIS APPPOOL\AdaPanel.bayi-denetim.test:(OI)(CI)(M)`.
  Kod -> canli sistem -> panelin iddiasi -> bagimsiz olcum, dordu tutuyor.
* runtimes klasorune yazma ENGELLENDI (0.8.8); Everyone gitmis, Users/IIS_IUSRS yalniz RX.
* logs ve stalwart/stalwart-cli klasorleri ACL ile kilitli (erisim reddi).
* Guvenlik basliklari gonderiliyor, HSTS YOK (0.8.7 -- kasitli).
* Gizli giris yolu: baslıksiz API istegi 404, ucun varligini ele vermiyor.
* DNS yayini durustlugu (0.8.11): site panelinde "Windows DNS'e yayinlanamadi" diyor.
* Oturum dusurme (0.8.10): yukseltmede oturum dustu, yeniden giris istedi.
* Guvenlik duvari listesi GERCEKLE BIREBIR uyusuyor (16 kural, Enabled degerleri dogru).
* Posta ekrani: bos liste gostermiyor, "baglanilamadi" diyor ve ne yapilacagini yaziyor.
* Veritabani ekrani: "Kurulu degil" diyor, "0 veritabani" DEMIYOR.

### YENI BULGU 1 -- guvenlik duvari kurali CIFT
`AdaPanel Site AdaPanel.WebDb.PhpMyAdmin TCP 8896` Windows'ta IKI KEZ kayitli (yukseltilmis
Get-NetFirewallRule ile dogrulandi). Bellekteki B2 notu "kurallar idempotent degil, 9 kopya
olusuyordu, DUZELTILDI" diyor. Ya duzeltmeden onceki artik, ya da o yol hâlâ tam idempotent
degil. ONCE HANGISI oldugu olculmeli: kural yazma yolu "once sil sonra ekle" yapiyor mu?

### YENI BULGU 2 -- silinen sitenin kurali GERIDE KALDI
`AdaPanel Site alioral.xyz TCP 80` duruyor ama o site artik YOK. Site silinirken guvenlik
duvari kuralinin kaldirilmadigi anlamina gelebilir -- yani "site silindi" denip kuralin
kalmasi, bu turun kapattigi sinifin bir baska ornegi olur. Silme yolunda kural kaldirma
CAGRILIYOR MU, olculmeli.

### KUCUK -- posta ekrani var olmayan gorevi tarif ediyor
Baglanti kurulamayinca "AdaStalwart zamanlanmis gorevini yeniden baslatin" diyor, ama o
gorev bu makinede YOK (Stalwart kurulu ama servis/gorev yok). Tavsiye, var olmayan bir seye
isaret ediyor.

### KENDI OLCUM ARACIMDA UC KEZ AYNI HATA
`-ErrorAction SilentlyContinue` ve sessiz `Get-ChildItem -Recurse`, ERISIM REDDINI yutup
sonucu "hic yok"a cevirdi: guvenlik duvari kurallari (0 sanildi), stalwart ikilileri
(yok sanildi), stalwart klasorleri (bos sanildi). Ucunde de gercek sebep erisim reddiydi.
Bu, tam da bu turda panelde kapatilan sinifin (sorgu hatasi = bos sonuc) arac tarafindaki
karsiligidir. Ders: dogrulama araci da uc degerli olmali -- var / yok / OKUYAMADIM.

## GOVDE OLCUM DIKISI -- "koruma yazildi ama GOVDE cagiriyor mu?" (06.09.2026) -- commit ATILMADI

**OLCULEN BOSLUK.** `MailEnableGirisKaydiSondasiTests` sondayi (gercek `powershell.exe`'de) ve
karar kurallarini (saf fonksiyon olarak) kapliyordu; **GOVDENIN o kurallari cagirdigi
HICBIR YERDE olculmuyordu**. `DeleteAccountAsync` icinden `GirisKapisiniAc(...)` satiri
silinseydi 36 sinamanin TAMAMI YESIL kalirdi -- yani koruma "yazilmis ama cagrilmiyor"
olabilirdi. Bu, bu depoda defalarca yakalanan sinifin ta kendisi.

### DIKIS (en az degisiklik)

PowerShell'e cikan TEK nokta olculdu: `MailEnableMailService.RunComAsync`. Butun mutasyon
metotlari (silme/olusturma/parola/kota/alias/domain + calisma alani okumasi) oradan gecer.
Eklenen tek sey:

* `internal Func<string, CancellationToken, Task<string>>? KomutKosucusu { get; init; }`
* `RunComAsync`'in ilk satirinda: `null` degilse cagrilir, degilse eskisi gibi 32-bit
  `powershell.exe` kosar. **Uretimde `null`; hicbir davranis degismedi.** Yeni mimari YOK.

**BETIK SAHTELESTIRILMEDI.** Sinama, GOVDENIN URETTIGI betigi (kopyasini degil) gercek
`powershell.exe`'de, mevcut `SahteCom` COM stand-in'i onekiyle kosturur ve ciktisini govdeye
geri verir; ikinci cagriyi (calisma alani okumasi) kontrollu bir JSON ile karsilar. Cagrilar
SIRAYLA ayirt edilir -- betik metnine BAKILMAZ. Kaynak taramasi YOK.

### YENI SINAMALAR (10, hepsi ayni dosyada -- bolum 6)

Silme govdesi (6): dogrulama gecince calisma alani DONER (fazla kapatma nobetcisi) · giris
kaydi DURUYORSA `mail.account_delete_login_survived` · sonda ayirt edemeyince
`mail.account_delete_login_unverified` · posta kutusu DURUYORSA `mail.account_delete_not_applied`
· calisma alani OKUNAMAYINCA `mail.account_delete_unverified` · yutulan `RemoveLogin` hatasi
AYRINTIYA tasiniyor.

Parola govdesi (4): dogrulaninca uyari YAZMAZ (fazla kapatma nobetcisi) · kayit islemden sonra
YOKSA `mail.account_password_not_applied` **ve calisma alani okumasi HIC yapilmiyor** (tek
PowerShell cagrisi) · ozetleyen sunucuda FIRLATMAZ ama "DOGRULANMADI" yazar · sonda ayirt
edemeyince sessiz "tamam" DEMEZ, "DOGRULANAMADI" yazar.

### MUTASYON KANITI (3 mutasyon, 8 sinama KIRMIZI)

| Mutasyon | Kirmizi |
|---|---|
| M1: `DeleteAccountAsync` govdesinden `GirisKapisiniAc(...)` KALDIRILDI | **3** |
| M2: `DeleteAccountAsync` govdesinden `KapiAcVarlik(...)` KALDIRILDI | **2** |
| M3+M4: `SetAccountPasswordAsync` govdesinden `ParolaKapisiniAc` + `ParolaNotunuYaz` KALDIRILDI | **3** |

Ucu de ESKI HALINE GETIRILDI (`git diff` yalniz EKLEME gosteriyor, 0 silme).

### `SetAccountPasswordAsync` HAKKINDA DURUST BULGU

**Yeni bir dogrulama ICAT EDILMEDI ve edilmemeli.** Parola sondasinin gorebildigi tek sey
"giris kaydi islemden SONRA hala duruyor mu"dur; "parola DEGISTI mi" GORULEMEZ. Sunucu
parolayi ozetleyerek sakliyorsa `Password` geri okundugunda yazilanla ESLESMEZ -- buna
"uygulanmadi" demek CALISAN kurulumlarda SAHTE HATA uretirdi. Mevcut kural bunu zaten dogru
yapiyor:

* kayit SONRA YOK (sonda ONCE ayni kaydi BULMUSTU) -> **UYGULANMADI**, firlatir. Tek saglam
  olumsuz iddia budur ve "parola yanlis yazildi" degil "kayit YOK" demektir.
* kayit VAR + birebir esit -> **DOGRULANDI** (tek olumlu iddia).
* kayit VAR + esit degil -> **GERI OKUNAMADI**, FIRLATMAZ.
* kalibre edilemedi -> **OLCULEMEDI**, FIRLATMAZ.

Yani parola yolunda kapatilabilir tek bosluk "govde bu kurali cagiriyor mu"ydu; o kapandi.
**Kalan gercek bosluk (kapatilamaz):** yeni parolayla GERCEKTEN oturum acilabildigi
olculmuyor. Bunun tek durust yolu panelin IMAP/SMTP'ye kendi giris denemesi yapmasidir --
ayri bir is ve ayri bir risk (kilitlenme sayaci, brute-force korumasi tetiklenmesi).

### ACIK KALAN

* `CreateAccountAsync` govdesi ayni dikisle kosturulabilir ama HENUZ SINANMADI (silmenin
  kardesi; ayni desen dogrudan uygulanir).
* Kota / alias / domain govdeleri de ayni dikisi kullanabilir; onlarin `KapiAcVarlik`
  cagrilari da govde duzeyinde olculmus DEGIL.
* MailEnable bu makinede HALA YOK: COM durum kodu konvansiyonu, `GetLogin`'in eksik kayitta
  gercek davranisi ve `Password`'un duz metin donup donmedigi olculmedi (kapatma bu yuzden
  konvansiyona degil KALIBRASYONA dayaniyor).

**4891 test yesil / 0 kirmizi** (Core 2 + Infrastructure 4889; baseline 4879 + 10 yeni).
Degisen dosyalar: `src/AdaPanel.Infrastructure/Mail/MailEnableMailService.cs`,
`tests/AdaPanel.Infrastructure.Tests/MailEnableGirisKaydiSondasiTests.cs`.

---

## `AllowedHosts: "*"` — ÖLÇÜLDÜ, DOĞRU YERDEN KAPATILDI (ayar KASITEN gevşek bırakıldı)

**Bulgunun kendisi bir açık DEĞİL.** Panel gelen her `Host` başlığını kabul ediyor
(`appsettings.json` → `AllowedHosts: "*"`, boru hattında `UseHostFiltering` yok). Bu tek başına
zararsızdır; zarar, o başlığın güvenlik-etkili bir KARARA girmesiyle oluşur. Bu yüzden önce
başlığın nereye ulaştığı ölçüldü.

### Host başlığı gerçekte nereye ulaşıyor (kaynaktan okundu)

| Kullanım noktası | Ulaştığı yer | Etki |
|---|---|---|
| `AuthService.ResetPasswordAsync` (parola sıfırlama postası) | `PanelAddress.BuildEntryUrl` — Host HİÇ verilmiyor | **etki yok** |
| `UserManagementService` (hoş geldin postası) | aynı, Host verilmiyor | **etki yok** |
| `MailListPublicController` onay bağlantısı | `ServerAddress` boşsa Host yedeği | **GERÇEK: kimliksiz uç + panelin kendi alan adından gerçek aboneye giden posta** |
| `AuthController` → `AuthService.CreateSsoLinkAsync` | `ServerAddress` boşsa Host yedeği | **GERÇEK: bağlantı TEK KULLANIMLIK GİRİŞ belirteci taşıyor** |
| `MailController` webmail aç/tanıla | yalnız ÇAĞIRANA dönen URL; kalıcı durum (`WebmailState`) Host içermez | etki yok (kendi Host'unu bozan kendine zarar verir) |
| `MailAutodiscoverController` | Host → alan adı; yanıt yalnız panelde TANIMLI alanlar için, sırsız | etki yok (aynı bilgi `emailaddress` alanıyla da alınır) |
| `DbToolsProxyController` `X-Forwarded-Host` | phpMyAdmin mutlak URL'i; uç kimlik doğrulamalı, tarayıcı Host'u CSRF ile değiştirilemez | etki yok |
| Çerez `Domain` / CORS / `UseForwardedHeaders` | hiçbiri yok / bilerek kapalı | etki yok |

### Kök neden (yamanın altındaki asıl hata)

`PanelAddress.BuildBaseUrl` bir `fallbackHost` parametresi taşıyordu: "panel adresi ayarlı
değilse isteğin `Host` başlığını kullan". Gerekçesi "adres ayarlanmamış kurulumda bağlantı
`<sunucu-adresi>` yer tutucusuyla ölü gider"di. **Gerekçe ölçüldü ve ÇÖKTÜ:**
`PanelPreferencesService.GetAsync` bu alanı asla boş bırakmıyor — boşsa `ResolveServerAddress()`
makinenin ilk loopback-olmayan IPv4 adresini, o da yoksa `Environment.MachineName`'i yazıyor.
Yani yedek yolu üretimde HİÇ çalışmıyordu; duran tek şey istismar yüzeyiydi.

### Seçilen strateji: (a) Host'u karara sokma — `AllowedHosts` DEĞİŞTİRİLMEDİ

`fallbackHost` parametresi tipten kaldırıldı; `MailListPublicController` ve
`AuthService.CreateSsoLinkAsync` artık adresi yalnız ayardan kuruyor (`CreateSsoLinkAsync`
imzasından `requestHost` da düştü — açığın geri gelmesi artık DERLEME hatası).

**`AllowedHosts` neden kısılmadı:** panele meşru olarak `localhost`, `127.0.0.1`, makinenin IP'si,
makine adı, kurulumda verilen panel alan adı ve müşterinin SONRADAN eklediği alan adı üzerinden
erişiliyor. Bu küme çalışma zamanında değişir; statik beyaz listeye çevrilseydi listede olmayan
her adres 400 alırdı — müşteri kendi panelinden kilitlenirdi. Bu, kapatılan açıktan daha kötü bir
arıza olurdu. Kapatılan şey isteğin KABULÜ değil, başlığın KARARA girmesidir.

### Mutasyon (koruma kaldırıldı, kırmızı görüldü, geri kondu)

| Mutasyon | Kırmızıya düşen test |
|---|---|
| A — onay bağlantısı yine `Request.Host`'tan kurulsun | **3** |
| B — `ResolveServerAddress()` boş dönsün (yedeğin dayandığı varsayım) | **2** |
| C — panel adresine port yazılmasın (fazla kapatma nöbetçisi) | **9** |

### Testler

Yeni: `tests/AdaPanel.Infrastructure.Tests/PanelAdresiHostBasligiTests.cs` (11 test) — kaldırılan
yedeğin yerine geçen güvence (adres kaynağı asla boş dönmez) + fazla kapatma nöbetçileri
(`localhost`, `127.0.0.1`, sunucu IP'si, makine adı, panel alan adı, 8890 portu, 443'te portsuz,
gizli giriş yolu). Güncellenen: `PostaBaglantiAdresiTests` ve `AdminPasswordAndSsoTests` — ikisinde
de "adres boşsa Host kullanılır" iddiası, "Host HİÇ giremez" iddiasıyla değiştirildi.

**4902 test yeşil / 0 kırmızı** (Core 2 + Infrastructure 4900; baseline 4891 + 11 yeni).
Değişen dosyalar: `src/AdaPanel.Infrastructure/Support/PanelAddress.cs`,
`src/AdaPanel.Api/Controllers/MailListPublicController.cs`,
`src/AdaPanel.Api/Controllers/AuthController.cs`,
`src/AdaPanel.Infrastructure/Services/AuthService.cs`,
`src/AdaPanel.Core/Interfaces/IAuthService.cs`, üç test dosyası.

### AÇIK KALAN (bu turda kapatılmadı, ölçüldü)

* `ServerAddress` otomatik çözüldüğünde makinenin İÇ IP'si olabilir (ör. `10.0.0.5`); NAT
  arkasındaki bir sunucuda e-posta bağlantıları dışarıdan açılmaz. Bu Host'la ilgili DEĞİL —
  kurulum sihirbazının panel adresini sorması/doğrulaması gereken ayrı bir eksik.
* `PanelAddress.BuildBaseUrl` adresi biçimsel doğrulamıyor: yönetici `ServerAddress` alanına
  `panel.ornek.com:8890` yazarsa `https://panel.ornek.com:8890:8888` çıkar; IPv6 (`::1`) köşeli
  paranteze alınmaz. İkisi de YÖNETİCİ girdisidir (saldırgan girdisi değil), o yüzden bu turun
  kapsamına alınmadı.

---

## Panel adresi İÇ AĞ adresi olduğunda e-posta bağlantıları ölüydü — ÖLÇÜLDÜ, UYARIYLA KAPATILDI

Bir önceki turun "açık kalan" maddesi. Sorunun sınıfı bu oturumun ana temasıyla aynı:
**panel okumadan başarı bildiriyordu.**

### Ölçülen davranış (kaynaktan + makineden)

`PanelPreferencesService.GetAsync` panel adresini asla boş bırakmıyor: boşsa
`ResolveServerAddress()` makinenin ilk loopback-olmayan IPv4 adresini, o da yoksa
`Environment.MachineName`'i yazıyor. Bu geliştirme makinesinde aday IPv4 adreslerinin **tamamı
özel aralıkta** ve sıralamada ilk sırada bir **sanal ağ adaptörünün** adresi var — yani otomatik
çözülen değer yalnızca "iç adres" değil, çoğu zaman makinenin gerçek LAN adresi bile değil.

Bu değeri **müşteriye giden** bağlantılar taşıyor:

| Kullanım | Nereye gidiyor |
|---|---|
| `AuthService.ResetPasswordAsync` | parola sıfırlama postası → müşterinin kutusu |
| `UserManagementService` (hoş geldin postası) | yeni müşteriye giriş bağlantısı |
| `MailListPublicController` onay bağlantısı | aboneye giden abonelikten çıkma postası |
| `AuthService.CreateSsoLinkAsync` | yöneticinin müşteriye ilettiği tek kullanımlık giriş |

NAT arkasındaki bir sunucuda müşteri bu bağlantıyı **açamaz**; panel "gönderildi" der ve hiçbir
şey fark etmez.

Aynı alanın **yalnız yerelde/idari olarak** kullanıldığı yerler bundan ayrıdır ve
dokunulmadı: `DnsService` (varsayılan A kaydı IP'si), `FtpService`/`RemoteDbAccessService`
(bağlantı bilgisi gösterimi), `MailService`/`MailDnsPublication` (posta DNS'i),
`SiteHealthService`, `PanelPublicAccessService`.

**Kurulum sihirbazı panel adresini SORMUYOR** (ölçüldü: `SetupWizardView.vue` `setupForm`
alanları = kullanıcı adı, e-posta, parola, gizli giriş yolu, dil, HTTPS portu, önerilen
bileşenler). Alan yalnız **Ayarlar > Genel > Sunucu adresi**'nde var ve boş bırakılırsa sunucu
otomatik dolduruyor.

### Seçilen strateji: ENGELLEME YOK, GÖRÜNÜR UYARI

`PanelAddress.IsExternallyReachable(...)` adresi **biçimsel** olarak sınıflandırır (DNS sorgusu
yok, bağlantı denemesi yok — ayar okuma yolu her istekte çağrılıyor, ağa bağlanamaz): özel
aralıklar (10/8, 172.16/12, 192.168/16), loopback, bağlantı-yerel (169.254/16), taşıyıcı NAT
(100.64/10), IPv6 loopback/link-local/unique-local, **tek etiketli makine adı** ve genel DNS'e
delege edilmeyen son ekler (`localhost`, `.local`, `.home.arpa`, `.internal`, `.test`,
`.invalid`, `.example`) → "dışarıdan açılmaz".

Sonuç `PanelPreferencesDto.ServerAddressExternallyReachable` alanına yazılır (TÜRETİLMİŞ: her
okumada yeniden hesaplanır, dosyadaki eski değere güvenilmez) ve **iki yerde yöneticiye
gösterilir**:

1. **Ayarlar > Genel > Sunucu adresi** alanının hemen altında satır içi uyarı
   (`panel.settings.serverAddressNotPublic`).
2. **Danışman** ekranında `panel-address-not-public` maddesi (severity `warning`, "Düzelt"
   düğmesi Ayarlar'a götürür, detay olarak sorunlu adresin kendisi yazılır).

**Neden engelleme değil:** iç ağa kurulmuş meşru bir panelde (şirket içi hosting, geliştirme
kurulumu) `192.168.x.x` DOĞRU adrestir ve bağlantı orada çalışır. Gönderimi kesmek çalışan bir
kurulumu kırardı — kapatılan sorundan kötü. Plesk de aynı yerde durur: hostname'i doğrular, NAT
arkası için ayrı bir "public IP" alanı sunar, ama posta göndermeyi engellemez.

**Neden kurulum sihirbazına alan eklenmedi:** kurulum anında DNS çoğu zaman henüz hazır
değildir; alan ya boş bırakılır (aynı yere döneriz) ya da yanlış doldurulur, üstelik sonradan da
yanlışlaşabilir. Uyarı, adres ne zaman ve nasıl ayarlanırsa ayarlansın çalışır. Ayarlar
formundaki alan zaten mevcuttu; eksik olan, panelin değeri **okuması** idi.

### Mutasyon (koruma kaldırıldı, kırmızı görüldü, geri kondu)

| Mutasyon | Kırmızıya düşen test |
|---|---|
| A — `GetAsync` bayrağı hiç hesaplamasın | **3** |
| B — `IsExternallyReachable` daima `true` (uyarı hiç çıkmaz) | **26** |
| C — Danışman maddesi üretilmesin | **1** |
| D — `IsExternallyReachable` daima `false` (FAZLA KAPATMA) | **16** |

D özellikle önemli: fazla kapatma nöbetçisi gerçekten ateş ediyor — `panel.ornek.com`,
`185.7.242.225`, `172.32.0.1`, `192.169.0.1` gibi GERÇEK adresler uyarı üretirse 16 test kırmızı.

### Testler

Yeni: `tests/AdaPanel.Infrastructure.Tests/PanelAdresiDisaridanErisimTests.cs` (43 test) —
sınıflandırma tablosu (iç/dış, sınır komşuları dahil: `172.15`/`172.32`, `192.169`, `100.63`,
`100.128`, `169.253`), tercih katmanının bayrağı üretmesi, kaydetme yanıtının uyarıyı
güncellemesi, ve **gerçek `AdvisorService`** üzerinden uyarının rapora girmesi/girmemesi. Ek:
`PostaBaglantiAdresiTests` içine fazla kapatma nöbetçisi — iç adresli kurulumda abonelikten
çıkma postası **yine gönderilir** ve bağlantı o adresi taşır (1 test).

**4946 test yeşil / 0 kırmızı** (Core 2 + Infrastructure 4944; baseline 4902 + 44 yeni).
Frontend: typecheck/build/`npm test` (42) temiz, i18n TR/EN 5182 = 5182.

Değişen dosyalar: `src/AdaPanel.Infrastructure/Support/PanelAddress.cs`,
`src/AdaPanel.Core/Dtos/Settings/PanelPreferencesDto.cs`,
`src/AdaPanel.Infrastructure/Services/PanelPreferencesService.cs`,
`src/AdaPanel.Infrastructure/Advisor/AdvisorService.cs`,
`frontend/src/api/panelSettings.ts`, `frontend/src/views/SetupWizardView.vue`,
`frontend/src/i18n/tr.json`, `frontend/src/i18n/en.json`, iki test dosyası.

### AÇIK KALAN (ölçüldü, kapsam dışı bırakıldı)

* Sınıflandırma **biçimseldir**: `panel.ornek.com` için DNS kaydı olmasa da "dışarıdan
  açılabilir" sayılır. Gerçek doğrulama (adı çözüp panelin IP'siyle karşılaştırmak, dışarıdan
  bağlanmayı denemek) ağ gerektirir; ayar okuma yolu her istekte çağrıldığı için oraya
  konamaz. Doğru yeri, zaten ağa çıkan `PanelPublicAccessService` (panel erişimi ekranı) olurdu.
* Uyarı **kapatılamıyor**: iç ağda bilerek çalışan bir kurulumda Danışman'da kalıcı bir uyarı
  maddesi durur. Danışman'da madde gizleme mekanizması hiç yok; eklemek ayrı bir iştir.
* `PanelAddress.BuildBaseUrl` hâlâ biçim doğrulamıyor (`panel.ornek.com:8890` yazılırsa
  `https://panel.ornek.com:8890:8888`; IPv6 köşeli paranteze alınmaz). Önceki turdan devreden
  madde; yönetici girdisidir, bu turda da kapsama alınmadı.
* Kurulum sihirbazı panel adresini hâlâ sormuyor (yukarıda gerekçesi yazılı).

---

## Panel adresi BİÇİMSEL doğrulama + normalleştirme (2026-09-06)

Önceki turdan iki kez devreden madde kapatıldı: `PanelAddress.BuildBaseUrl` ayar değerini
`https://` ile portun arasına ÇİĞ yapıştırıyordu.

### ÖLÇÜM (düzeltmeden ÖNCE alınan gerçek çıktılar, `httpsPort = 8888`)

| girdi | üretilen URL | durum |
|---|---|---|
| `panel.ornek.com:8890` | `https://panel.ornek.com:8890:8888` | BOZUK (iki port) |
| `https://panel.ornek.com` | `https://https://panel.ornek.com:8888` | BOZUK (iki şema) |
| `http://panel.ornek.com:8890` | `https://http://panel.ornek.com:8890:8888` | BOZUK |
| `fd00::1` | `https://fd00::1:8888` | BOZUK (IPv6 parantezsiz) |
| `2001:db8::1` | `https://2001:db8::1:8888` | BOZUK |
| `panel.ornek.com/` | `https://panel.ornek.com/:8888` | BOZUK (port yolun ardında) |
| `panel.ornek.com/yol` | `https://panel.ornek.com/yol:8888` | BOZUK |
| `PANEL.ORNEK.COM` | `https://PANEL.ORNEK.COM:8888` | çalışır ama kanonik değil |

Devir notundaki üç iddianın ÜÇÜ DE doğrulandı; ek olarak yol/sorgu ve `http://` şeması da aynı
sınıfta çıktı. Kaydetme yolunda hiçbir doğrulama yoktu (`UpdatePanelPreferencesRequest.ServerAddress`
üzerinde yalnız `[StringLength(255)]`), frontend'de de yok (düz `<input>`).

### Düzeltme İKİ KATMANDA (ikisi de gerekli)

* **Kaydetme** — `PanelPreferencesService.UpdateAsync` → `NormalizeServerAddress`. Yeni girdiyi
  kanonikleştirir ve yöneticiye ANINDA hata döndürür.
* **Üretme** — `PanelAddress.BuildBaseUrl` → `TryParseAddress`. Diskte ZATEN duran bozuk değeri de
  kurtarır (kaydetme doğrulaması eski kayıtlara dokunmaz; yönetici formu bir daha hiç açmayabilir).

### Normalleştirme kuralları ve gerekçeleri

1. Şema (`http://`/`https://`) SÖKÜLÜR ve yok sayılır. Şemadan port ÇIKARIMI yapılmaz (`https://`
   yazmak masum bir alışkanlık; ondan 443 çıkarmak 8888'de dinleyen çalışan kurulumu öldürürdü).
2. Yol/sorgu/parça sökülür — burada üretilen KÖK adres, yolu `BuildEntryUrl` ekler.
3. Küçültme `ToLowerInvariant` ile (Türkçe kültürde `ToLower` `IZMIR.COM` → `ızmır.com` yapardı).
4. Unicode alan adı REDDEDİLMEZ, punycode'a çevrilir (`örnek.com.tr` → `xn--rnek-4qa.com.tr`;
   punycode değeri ÖLÇÜLDÜ, tahmin edilmedi). Gerekçe: posta gövdesindeki `href` içinde çiğ
   Unicode host'u her istemci doğru kodlamaz.
5. IPv6 parantezli/parantezsiz kabul edilir; URL'ye yazılırken köşeli paranteze alınır.
6. **PORT KARARI:** `ServerAddress` kayda SAF HOST olarak yazılır (port yok). Gerekçe: bu alanı
   yalnız URL üretimi okumuyor — varsayılan DNS A kaydı (`DnsService`), FTP istemcisine gösterilen
   sunucu adresi, posta DNS yayını ve uzak veritabanı host'u da aynı alandan okuyor; içine `:8890`
   yazmak onları bozar. Adresteki port ayardaki `PreferredHttpsPort` ile AYNIYSA bilgi kaybı
   olmadan elenir (en sık gerçek durum). FARKLIYSA taraf sessizce seçilmez, hata döner
   (`settings.server_address_port_conflict`) — panel iki porttan hangisinin dışarıdan açıldığını
   bilemez ve yanlış seçim hiçbir yerde raporlanmayan ölü bağlantı üretir.
   ÜRETME yolunda (eski kayıt) ise adresteki port KAZANIR: yönetici o değeri panele girebildiği
   tarayıcı çubuğundan kopyalar, dışarıdan çalıştığı kanıtlı tek değer odur.
7. Ayrıştırılamayan değer için host UYDURULMAZ: `UnknownHostPlaceholder` (mevcut "ölü ama görünür
   bağlantı" doktrini).

`IsExternallyReachable` de aynı ayrıştırıcıyı kullanır — iki kopya kural bir gün ayrışsaydı
"uyarı yok ama bağlantı ölü" (ya da tersi) olurdu.

`AcmeArgumentRules.IsCommandSafeHost` BİLEREK yeniden kullanılmadı: o kural `*.ornek.com` joker
önekini kabul eder (sertifika adı olarak meşru, panel adresi olarak anlamsız) ve port/şema/yol/
IPv6 parantezi hakkında hiçbir şey bilmez.

### MUTASYON (üçü de yapıldı, kırmızı GÖRÜLDÜ, geri konuldu)

| mutasyon | kırmızı test |
|---|---|
| Üretme katmanı: normalleştirmeyi atla, eski çiğ davranış | **19** |
| Kaydetme katmanı: doğrulama/normalleştirmeyi kaldır | **8** |
| FAZLA KAPATMA: DNS kuralını daralt (nokta zorunlu, `-`/`_` yasak) | **12** |

Üçüncüsü kritik: fazla kapatma nöbetçileri gerçekten ateş ediyor — tek etiketli makine adı
(`WIN-SUNUCU01`, otomatik çözülen değer), alt çizgili makine adı, tireli alan adları reddedilirse
12 test kırmızı olur.

### Testler

Yeni: `tests/AdaPanel.Infrastructure.Tests/PanelAdresiBicimNormallestirmeTests.cs` (58 test).
Fazla kapatma nöbetçileri: düz alan adı, derin alt alan adı, `.com.tr`/`.travel`, tireli ad,
punycode `xn--`, Türkçe harfli ad, IPv4 (iç + genel), IPv6 (yerel + genel), tek etiketli makine
adı, alt çizgili makine adı, `localhost`, sondaki kök noktası — hepsi `Uri.TryCreate` ile geçerli
mutlak https URL üretmeli. Ayrıca BOŞ adres reddedilmemeli (aksi hâlde adresi ayarlamamış yönetici
Ayarlar formunu hiç kaydedemezdi).
Güncellenen: `PanelAdresiHostBasligiTests.Mesru_panel_adresleri_ACILABILIR_adres_uretir` artık
küçük harfli host bekliyor (host adları DNS'te de URL'de de büyük/küçük harf duyarsız).

**5004 test yeşil / 0 kırmızı** (Core 2 + Infrastructure 5002; baseline 4946 + 58 yeni).
Frontend: typecheck/build/`npm test` (42) temiz, i18n TR/EN 5184 = 5184.

Değişen dosyalar: `src/AdaPanel.Infrastructure/Support/PanelAddress.cs`,
`src/AdaPanel.Infrastructure/Services/PanelPreferencesService.cs`,
`frontend/src/i18n/tr.json`, `frontend/src/i18n/en.json`, iki test dosyası.

### AÇIK KALAN (kapsam dışı, bilerek)

* **Dış port ≠ dinleme portu modellenmiyor.** Ters vekil dışarıda 8890, panel içeride 8888
  dinliyorsa panelin bunu ifade edecek alanı yok; bu durumda kaydetme yolu hata döndürür ve
  yönetici adresi portsuz yazmak zorunda kalır (üretilen bağlantı iç portu taşır). Doğru çözüm
  ayrı bir "dış port" alanı (DTO + form + i18n + göç); bu tur kapsamına alınmadı.
* **Frontend'de anlık doğrulama yok.** Hata sunucudan döner ve banner'da görünür
  (`settings.server_address_invalid` / `settings.server_address_port_conflict` TR+EN eklendi);
  alanın kendisi yazarken kırmızıya dönmüyor.
* **`GetAsync` normalleştirmiyor** (yalnız `Trim`). Bilerek: normalleştirseydi eski kayıttaki
  portu düşürür ve `BuildBaseUrl`'ün kurtarma dalı ölürdü. Sonuç: diskte `host:8890` yazan eski
  bir kurulumda DNS/FTP/posta tüketicileri o değeri hâlâ ham görüyor (ÖNCEKİ durumla aynı,
  regresyon yok) — yönetici Ayarlar'ı bir kez kaydedince temizlenir.
* **`UpdatePanelPreferencesRequest.ServerAddress` üzerinde hâlâ DataAnnotations yok**; doğrulama
  servis katmanında. Kasıtlı: kural port bağımlı (`PreferredHttpsPort` ile karşılaştırma) ve
  attribute tek alanı görebilir.


---

## 2026-09-06 — Göçte ŞİFRELEME: FTPS düşürme saldırısı + DB aktarımında TLS

Bir önceki tur uzak sunucunun KİMLİĞİNİ doğrulanır hâle getirmişti (`68eb395`). Aynı sınıftan iki
nokta ölçülmüş ama kapsam dışı bırakılmıştı; bu tur ikisini de kapattı.

### 1) FTPS DÜŞÜRME — iddia DOĞRULANDI, telden kanıtlandı

`FtpConfig.EncryptionMode = FtpEncryptionMode.Auto` idi. Depoya eklenen sahte FTP sunucusuyla
ÖLÇÜLDÜ: sunucu `AUTH TLS`e "500" dediğinde FluentFTP bağlantıyı KESMİYOR, aynı bağlantıda düz
FTP'ye düşüp kimlik bilgisini şifresiz gönderiyordu. Mutasyon turunda sunucunun düz-metin kaydı
aynen şuydu:

```
["AUTH TLS", "USER gocKullanicisi", "PASS CokGizliFtpParolasi123!", "FEAT", "SYST", ...]
```

Yani araya giren aktif biri tek bir yanıtı bastırarak FTP parolasını açık metin okuyabilirdi ve
bir önceki turda eklenen sertifika doğrulaması HİÇ devreye girmiyordu (TLS el sıkışması hiç
başlamadığı için sorulacak sertifika yoktu).

**Çözüm:** `ftps` seçildiğinde `Explicit` ZORUNLU. `DataConnectionEncryption = secure` açıkça
yazıldı (komut kanalı TLS olup veri kanalı düz kalırsa dosya İÇERİĞİ açık gider; FluentFTP
varsayılanı bugün `true` ama bu bir güvenlik kararı, kütüphane varsayılanına bırakılamaz).

**KULLANICININ TERCİHİNE DOKUNULMADI.** Arayüzde üç seçenek var (SFTP / FTPS / FTP) ve `ftp`
BİLİNÇLİ bir tercihtir — kullanıcı TLS desteklemeyen kendi eski sunucusundan çekiyor olabilir.
O yol `None` ile açık; kapatılan şey `ftps` SEÇİLİYKEN sessizce düz FTP'ye düşmekti.

**Fazla kapatma önlemi (990):** port 990 ise `Implicit`. Örtük FTPS'te sunucu `AUTH TLS` beklemez;
`Explicit` dayatmak o göçleri kırardı. Örtük kipte düşürme MÜMKÜN DEĞİL (el sıkışma ilk iştir).

### 2) DB AKTARIMINDA ŞİFRELEME — iddia DOĞRULANDI

`mysqldump`/`mysql` komut satırında hiç `--ssl-mode` yoktu (`DatabaseService.TransferDatabaseAsync`).
İstemci varsayılanı `PREFERRED`: sunucu TLS sunmuyorsa şifresiz devam eder. Kaynak host kullanıcı
girdisidir ve UZAK olabilir → DB parolası + tüm tablolar açık metin akabilirdi.

**Ölçülen ayrım (görevin istediği):** hedef tarafın konağı `DatabaseProfiles`te sabit `127.0.0.1`
— yani import HER ZAMAN loopback. Loopback'te araya girecek ağ yok, zorlamak yalnız kırar.

**Çözüm — `MySqlTransferTls` (tek kaynak):**
* yerel/loopback kaynak (`localhost`, `127.x`, `::1`, `[::1]`, `::ffff:127.0.0.1`, `.`) → zorlama YOK
* uzak kaynak + tercih belirtilmemiş → `--ssl-mode=REQUIRED`
* kullanıcı "Şifrelemesiz" dediyse → zorlama YOK (bilinçli tercih)
* tanınmayan/boş değer → `auto` gibi ele alınır, sessizce şifresize DÜŞMEZ
* `DISABLED` açıkça YAZILMAZ: `--ssl-mode` tanımayan eski istemcilerde (MariaDB 11.4 öncesi)
  şifreleme İSTEMEYEN kullanıcının yolunu, ona hiçbir şey kazandırmayan bir bayrak kırardı

**Başarısızlığın SEBEBİ söyleniyor:** TLS kaynaklı hatalar (`SSL connection error`,
`unknown variable 'ssl-mode'`, `insecure transport`) ayrı anahtara çevrildi —
`database.transfer_source_tls_failed` — ve mesaj kullanıcıya "Şifrelemesiz seçip tekrar deneyin,
o durumda veriler açık gider" diyor. Sessizce şifresize düşme YOK.

**DÜRÜST SINIR (kodda da yazılı):** `REQUIRED` şifrelemeyi zorunlu kılar ama sunucu KİMLİĞİNİ
doğrulamaz (onun için `VERIFY_CA` + CA dosyası gerekir; göç edilen eski sunucularda CA yok).
Pasif dinleme kapanır, aktif MITM ayrı bir iştir.

### Arayüz (Vue 3)

* **Göç sihirbazı** (sol menü → Göç → "Veritabanını aktar" kutusu, kaynak parolasının hemen
  altında): "Bağlantı şifrelemesi" seçicisi — Otomatik / Zorunlu / Şifrelemesiz.
* **Veritabanları → Taşıma** (`DbTransferView`, kaynak parolasının hemen altında): aynı seçici +
  ne anlama geldiğini yazan ipucu.
* FTPS için arayüz değişikliği GEREKMEDİ: protokol seçimi (SFTP/FTPS/FTP) zaten vardı.

### MUTASYON (altısı da yapıldı, kırmızı GÖRÜLDÜ, geri konuldu)

| mutasyon | kırmızı test |
|---|---|
| **`Explicit` → tekrar `Auto`** (asıl açık) | **2** (düz metinde `PASS <parola>` görüldü) |
| `DataConnectionEncryption` → `false` | 1 |
| 990 dalı kapalı (her FTPS `Explicit`) — FAZLA KAPATMA | 1 |
| Her FTPS portu `Implicit` sayılıyor — FAZLA KAPATMA | 3 |
| `Coz`: uzak konakta da şifreleme kapalı | 7 |
| `YerelKonak` hep `false` (loopback'te de zorla) — FAZLA KAPATMA | 9 |
| Bayrak veritabanı adından SONRA | 1 |
| Göç yolunda `SourceTlsMode` aktarılmıyor (zincir kopması) | 1 |

### Testler

Yeni: `tests/AdaPanel.Infrastructure.Tests/MigrationFtpsDowngradeTests.cs` (6) —
KAYNAK TARAMAZ: ölçüm noktası gerçek bir TCP soketinin sunucu ucudur, komutlar TLS ÖNCESİ ve
TLS SONRASI ayrı kaydedilir ("parola geldi" değil, "parola ŞİFRESİZ mi geldi").
Yeni: `tests/AdaPanel.Infrastructure.Tests/DatabaseTransferTlsTests.cs` (27) — kurulan KOMUT
SATIRINDAN ve gerçek `SiteMigrationService.MigrateSiteAsync` çağrısından ölçer.

Fazla kapatma nöbetçileri: kullanıcı `ftp` seçince göç ÇALIŞIR · geçerli FTPS sunucusuna bağlantı
kurulur ve parola TLS'in içinden gider · örtük FTPS (990) çalışır · loopback DB göçü kırılmaz
(8 yazım) · kullanıcı "Şifrelemesiz" deyince uzakta da zorlanmaz · ilgisiz hata TLS hatası
sayılmaz (adında `ssl` geçen DB dahil).

**5053 test yeşil / 0 kırmızı** (Core 2 + Infrastructure 5051; baseline 5020 + 33 yeni).
Frontend: typecheck/build/`npm test` (42) temiz, i18n TR/EN 5218 = 5218.

Değişen dosyalar: `src/AdaPanel.Infrastructure/Migration/FileMigrationService.cs`,
`src/AdaPanel.Infrastructure/Migration/SiteMigrationService.cs`,
`src/AdaPanel.Infrastructure/Database/DatabaseService.cs`,
`src/AdaPanel.Infrastructure/Database/MySqlTransferTls.cs` (yeni),
`src/AdaPanel.Core/Dtos/Databases/DatabaseTransferRequest.cs`,
`src/AdaPanel.Core/Dtos/Migration/MigrateSiteRequest.cs`,
`frontend/src/views/MigrationView.vue`, `frontend/src/views/DbTransferView.vue`,
`frontend/src/api/migration.ts`, `frontend/src/api/databases.ts`,
`frontend/src/i18n/tr.json`, `frontend/src/i18n/en.json`, iki yeni test dosyası.

### AÇIK KALAN (kapsam dışı, bilerek)

* **Sunucu kimliği DB tarafında doğrulanmıyor.** `--ssl-mode=REQUIRED` pasif dinlemeyi kapatır;
  `VERIFY_CA`/`VERIFY_IDENTITY` için CA dosyası alanı (DTO + form + i18n) gerekir.
* **`--ssl-mode` tanımayan eski istemci ölçülemedi.** Bu makinede MariaDB binary'leri yok
  (`%ProgramData%\AdaPanel\runtimes\mariadb\11` altinda yalniz `data` var). Panelin kurduğu
  sürüm 11.8 LTS ve destekliyor; sistemde tespit edilmiş ESKİ bir istemcide taşıma
  "unknown variable" ile düşer — ama sessizce değil: `database.transfer_source_tls_failed`
  kullanıcıya ne yapacağını söyler.
* **Frontend payload zincirinin otomatik testi yok** (depoda bileşen testi altyapısı yok, yalnız
  4 saf util/store test dosyası var). `DbTransferView`de `sourceTlsMode` satırı silinirse sunucu
  "auto" sayar — yani başarısızlık modu GÜVENLİ tarafta (kullanıcının "Şifrelemesiz" tercihi
  düşer, korunma düşmez).
* **FTPS veri kanalı el sıkışması ölçülmedi** (sahte sunucu PASV/veri kanalı konuşmuyor);
  `DataConnectionEncryption` istemci nesnesinin gerçek durumundan ölçülüyor.

## 2026-09-09 — SSRF kardeşlerinin ARTIĞI: üçü de ÖLÇÜLDÜ, ikisi bulgu DEĞİL

`efade94` (cron `url`) ve `942dfe9` (git + arşiv) kapandıktan sonra geriye üç madde
kalmıştı. Ajansız, elle ölçüldü. **İkisi gerçek bir açık değil; üçüncüsü karar.**

### 1. `GitController` kapsam denetimi — BULGU DEĞİL, ölçüldü

İddia: "`CustomerAccess.EnsureSiteAsync` çağrılmıyor; sahip olmayan yönetici
başkasının sitesine dağıtım yapılandırabiliyor."

`CustomerAccess.EnsureSiteAsync` (`src/AdaPanel.Api/Security/CustomerAccess.cs:125`)
okundu: `GetEffectiveSitesAsync` yalnız `Reseller` ve `Customer` için liste döndürüyor,
diğer roller için `null`. `null` gelince fonksiyon **hemen dönüyor** (`:132-135`).

`GitController` `[Authorize(Roles = "Administrator")]`. Yani çağrıyı eklemek
**hiçbir şey ölçmez, hiçbir şey engellemez** — koşulsuz `return` eden bir satır olur.
Administrator panelin her yerinde küresel kapsamlıdır (sites, dns, backup, files);
Git'i tek başına sahibe kısıtlamak, aynı yöneticinin dosya yöneticisiyle aynı şeyi
yapabildiği yerde tiyatro olurdu.

**KARAR: değişiklik yok.** Sahte bir koruma eklenmedi.

### 2. Arşiv `ftp://` yolunda DNS rebinding — bilinen sınır, kodda YAZILI

`SiteHostingSettingsService.cs:908-917` kalan sınırı zaten açıkça yazıyor: `ftp`
yolunda yalnız ön kapı çalışıyor (çözümleme + adres denetimi), bağlanma anı
denetlenmiyor. `Invoke-WebRequest ftp://` desteği ölçülmüş bir gerçek (PS 5.1'de
`FtpWebRequest` üretiyor) ve arayüz `panel.migration.urlHint` ile kullanıcıya vaat
ediyor. **Yeni bir şey eklenmedi**; sınır zaten dürüstçe belgeli.

### 3. İçe aktarmanın sabit `Strict`'i — TUTARSIZ ama AÇIK DEĞİL, KARAR BEKLİYOR

İddia: "`ImportSiteArchiveAsync` ön kapısı `Panel:AllowInternalOutboundTargets`
açık kapısını yoksayıyor (sabit `Strict`) — panelin geri kalanıyla tutarsız."

**Hipotezim ölçülüp ÇÜRÜDÜ.** "Kiracıya açık olduğu için Strict" diye düşünmüştüm.
Ama `OutboundTargetPolicy`'nin kendi belgesi (`OutboundAddressGuard.cs:355-362`)
bayrağın **kiracıya açık uçlarda da geçerli olduğunu** yazıyor ve örnek olarak
"yedek hedefi, cron adresi, git deposu" diyor — üçü de `Connecting(flag)` kullanıyor:

| Yol | Politika | Kiracıya açık mı |
|---|---|---|
| `BackupService.cs:140` | `Connecting(flag)` | evet |
| `SchedulerService.cs:1177` | `Connecting(flag)` | evet |
| `GitDeploymentService.cs:59` | `Connecting(flag)` | hayır (admin) |
| `SiteMigrationService`, `FileMigrationService`, `NotificationService` | `Connecting(flag)` | — |
| `ClusterAddressGuard`, `ClusterHttpClientFactory` | `Strict` | hayır |
| **`SiteHostingSettingsService.cs:766` (arşiv içe aktarma)** | **`Strict`** | **evet (`Administrator,Reseller`)** |

Yani depo kuralı "kiracıya açıksa Strict" DEĞİL. İçe aktarmanın sabit `Strict`'i
gerçekten tutarsız — ama **daha sıkı** yönde, yani bir açık değil.

**KARAR SAHİBİNE:** iki yol var ve ikisi de savunulabilir.
- **(a) Tutarlılık:** `Connecting(flag)` yap. Varsayılan yapılandırmada HİÇBİR fark
  yok (bayrak varsayılan kapalı); yalnız kapıyı BİLEREK açmış işletici etkilenir ve
  o zaten yedek/cron/git için aynı kararı vermiş.
- **(b) Sıkı kal:** `Strict` kalsın, ama gerekçesi koda yazılsın ki bir sonraki okuyan
  "tutarsızlık" diye GEVŞETMESİN.

Uydurma bir gerekçe yazılmadı: (b) için ölçülmüş bir sebep BULUNAMADI. Bu yüzden
kod DEĞİŞTİRİLMEDİ — gevşetmek de, sahte gerekçeyle sabitlemek de sahibinin kararı.
