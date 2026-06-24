# AdaPanel Codex Memory

Son guncelleme: 2026-06-22

Bu dosya, baska bir kullanici veya yeni bir Codex oturumu ayni klasorde devam ederken hizli devir notu olarak okunmalidir.
Ilk okunacak dosyalar:

1. `promp.md`
2. `CODEX_MEMORY.md`

## Mevcut durum

- Proje kok dizini: `C:\Users\aLi\Desktop\AdaPanel`
- Hedef platform: Windows 10/11 ve Windows Server 2016+
- Mimari: `Api` + `Core` + `Infrastructure` + `Data`
- Frontend: Vue 3 + Vite + Pinia + Vue I18n
- Backend: ASP.NET Core 8
- Yerel .NET SDK: `C:\Users\aLi\Desktop\AdaPanel\.tools\dotnet\dotnet.exe`

## 2026-06-08 15:00 son devir (en guncel ve oncelikli)

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
