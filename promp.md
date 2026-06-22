# AdaPanel — Tam Proje Yapısı (Tek Belge)

> Bu **tek belge**, AdaPanel projesinin tamamını anlatır: vizyon, teknoloji, mimari, kurallar,
> ve **dosya dosya yapı** (her dosyanın görevi + içinde olması gereken tipler/metotlar).
> Kod gövdeleri yazılmaz; bu belge Codex'e verilir, kodu Codex üretir.
> İçerik bölümlere ayrılmıştır; istersen baştan sona okursun, istersen bir modülün bölümünü
> alıp Codex'e verirsin.

**İçindekiler**
- BÖLÜM A — Genel (vizyon, teknoloji, mimari, kurallar)
- BÖLÜM B — Kurulum & Bağımlılık Yöneticisi (`components.json`)
- BÖLÜM C — Tam Klasör Ağacı
- BÖLÜM D — Dosya Dosya Yapı (tüm projeler)
- BÖLÜM E — Modül / Adım Kılavuzu (25 modül: hangi dosya, ne yapılacak)
- BÖLÜM F — Codex Ana Promptu

---

# BÖLÜM A — Genel

## A.1 Özet
**AdaPanel**, tüm Windows sürümlerinde (10/11, Server 2016+) çalışan, web tabanlı bir **web hosting
kontrol panelidir**. aaPanel for Windows tarzındadır. **Tek büyük farkı:** aynı sunucuda/aynı sitede
**ASP (Classic ASP + ASP.NET) ve PHP'yi AYNI ANDA** çalıştırabilir (IIS handler mapping).
Arayüz **Türkçe + İngilizce** (i18n). Nihai çıktı **tek tıkla kurulan `AdaPanelSetup.exe`**.

## A.2 Tam Özellik Listesi
- Web sitesi (domain) yönetimi + mevcut IIS sitelerini içe aktarma
- PHP çoklu sürüm yönetimi (FastCGI)
- ASP / ASP.NET yönetimi + **ASP↔PHP eşzamanlı**
- Node.js / Python uygulama barındırma (HttpPlatformHandler)
- Reverse proxy / URL Rewrite (IIS ARR)
- Veritabanı: MySQL/MariaDB + PostgreSQL (+ opsiyonel MSSQL)
- Web veritabanı arayüzü: phpMyAdmin / Adminer
- Redis / Memcached
- Dosya yöneticisi (sandbox'lı)
- SSL/TLS: Let's Encrypt (win-acme) + manuel + **otomatik yenileme**
- FTP yönetimi
- Site güvenliği & WAF (app pool izolasyonu, ACL, rate-limit)
- Zamanlanmış görevler (Task Scheduler / cron benzeri)
- Güvenlik duvarı (Windows Firewall)
- Log görüntüleyici (panel + site bazlı IIS logları)
- Bildirim & uyarı (e-posta / webhook)
- Yedekleme & geri yükleme (yerel + uzak/bulut: FTP/S3/Drive)
- Sistem izleme (CPU/RAM/disk/ağ)
- App Store (tek tık uygulama kurulumu)
- Panel kullanıcı yönetimi & güvenlik (2FA, güvenli giriş yolu)
- Panel kendi kendini güncelleme (self-update)
- Otomatik bağımlılık kurulumu (PHP/DB/Redis vb. otomatik indir & kur)

## A.3 Teknoloji
| Katman | Teknoloji | Neden |
|---|---|---|
| Backend | **ASP.NET Core 8 (C#)**, Windows Service | IIS'i `Microsoft.Web.Administration` ile resmî API'den yönetir |
| IIS yönetimi | Microsoft.Web.Administration | appcmd parse etmeye gerek yok |
| Sistem ops. | PowerShell/CLI + PerformanceCounter | firewall, servis, metrik |
| Panel DB | **SQLite + EF Core** | kurulum gerektirmez, tek dosya |
| Frontend | **Vue 3 + Vite + Pinia + Vue I18n + Naive UI** | reaktif UI; build → `wwwroot` |
| Auth | Cookie/JWT + TOTP 2FA | aaPanel benzeri güvenli giriş |
| Yayın | self-contained, single-file (win-x64) | .NET kurulu olmasa da çalışır |
| Paketleme | **Inno Setup → tek `AdaPanelSetup.exe`** | çift tıkla kurulum |

**Tek port/tek çalıştırılabilir:** Vue SPA build edilir → `AdaPanel.Api/wwwroot`'a kopyalanır →
tek `AdaPanel.Api.exe` hem API'yi hem arayüzü sunar (varsayılan port **8888**, self-signed HTTPS).

## A.4 Mimari (katmanlar)
```
[ Vue SPA (tarayıcı) ]  --HTTPS-->  [ AdaPanel.Api (ASP.NET Core / Windows Service) ]
                                          │  REST /api/v1, ortak zarf, auth, audit
                                          ▼
                                 [ AdaPanel.Core ]  (arabirimler, modeller, DTO'lar)
                                          ▲ implementasyon
                                 [ AdaPanel.Infrastructure ]  (IIS, PHP, DB, SSL, Bootstrap...)
                                          │
                                 [ AdaPanel.Data ]  (EF Core + SQLite)
                                          │
                              [ IIS ]  .php→FastCGI · .asp→asp.dll · .aspx→.NET
```
**Clean Architecture:** Api → Core (arabirimler) ← Infrastructure (implementasyon); Data en altta.
Bağımlılık yönü her zaman içeriye (Core'a) doğru.

## A.5 Kurallar (Conventions)
- **API:** REST, `/api/v1/...`, JSON. **Ortak yanıt zarfı:** `{ success, data, message, code }`.
- **Adlandırma:** C# PascalCase; JSON/JS camelCase; i18n anahtarı `nokta.ile` (örn. `sites.create.success`).
- **Güvenlik:** Her IIS/sistem değiştiren işlem → auth + admin rolü + **audit log**. Şifreler PBKDF2/BCrypt.
  2FA TOTP. Güvenli giriş: rastgele yol + opsiyonel IP kısıtı. Tüm dosya yolu işlemleri **kök dizine sandbox**.
- **i18n:** Backend `Resources/tr.json`,`en.json`; Frontend Vue I18n `i18n/tr.json`,`en.json`. Varsayılan TR.
- **Loglama:** Serilog → dosya + panel içi görüntüleyici. Audit ayrı tablo (kim/ne/ne zaman/kaynak).
- **Test:** Core iş mantığı birim test (xUnit); IIS/DB entegrasyon testi. Her modül `tests/` ile.
- **Hata yönetimi:** Dış komut/işlem try/catch; kullanıcıya i18n `message`, ham hata loglanır (sızdırılmaz).
- **Eşzamanlılık:** IIS yazma işlemleri global `SemaphoreSlim`/named mutex ile serileştirilir.
- **Rollback:** Kritik işlem (site/SSL) başarısız olursa yarım kalanı temizler.

---

# BÖLÜM B — Kurulum & Bağımlılık Yöneticisi

## B.1 Tek Tık Kurulum (`AdaPanelSetup.exe`) — Sihirbazın yaptıkları
1. Yönetici (UAC) izniyle çalışır.
2. Dosyaları `C:\Program Files\AdaPanel\` altına kopyalar.
3. **IIS gereksinimlerini otomatik açar/denetler** (Web Server rolü + **CGI** özelliği = PHP FastCGI için).
   Eksikse `dism`/PowerShell ile etkinleştirir.
4. AdaPanel'i **Windows Service** olarak kaydeder ve başlatır.
5. Panel **portu** sorar (varsayılan 8888), **self-signed sertifika** üretir, Firewall kuralı ekler.
6. **Bootstrapper'ı çalıştırır** → "Tam (Önerilen)" modda varsayılan yığını otomatik indirip kurar.
7. Tarayıcıyı `https://localhost:8888`'e açar → ilk yönetici + güvenli giriş yolu kurulur.
8. Başlat menüsü kısayolu + temiz kaldırma (uninstall).

**Build zinciri (geliştiricide):** `frontend` → `npm run build` → `AdaPanel.Api/wwwroot`'a kopyala →
`dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true` →
`installer/AdaPanel.iss` (Inno Setup) derle → **`AdaPanelSetup.exe`**. Hepsi `scripts/build-installer.ps1`.

## B.2 Bootstrapper akışı (her bileşen için)
1. **İndir** (ilerleme çubuğu, retry) → 2. **Doğrula** (sha256) → 3. **Kur** (zip→çıkar / msi→`msiexec /qn` /
installer→sessiz arg) → 4. **postInstall** (PHP→FastCGI+php.ini; DB→servis/port) → 5. **Servis kaydet** →
6. **Parola üret** (root/superuser otomatik, şifreli sakla, panelde göster) → 7. **SQLite'a kaydet**
(panel "Yazılımlar" sayfasında listelenir).

## B.3 `components.json` (bağımlılık manifesti)
```json
{
  "components": [
    { "id": "php-8.3", "displayName": "PHP 8.3 (NTS x64)", "type": "zip",
      "downloadUrl": "https://windows.php.net/downloads/releases/php-8.3.x-nts-Win32-vs16-x64.zip",
      "sha256": "...", "installDir": "C:\\AdaPanel\\runtimes\\php\\8.3",
      "postInstall": ["registerFastCgi","writePhpIni"], "default": true },

    { "id": "mariadb-11", "displayName": "MariaDB 11.x", "type": "msi",
      "downloadUrl": "https://archive.mariadb.org/...mariadb-11.x-winx64.msi", "sha256": "...",
      "silentArgs": "/qn SERVICENAME=AdaMariaDB PORT=3306 PASSWORD=__GENERATED__",
      "serviceName": "AdaMariaDB", "default": true },

    { "id": "mysql-8", "displayName": "MySQL 8.x", "type": "msi",
      "downloadUrl": "https://dev.mysql.com/get/Downloads/MySQL-8.0/mysql-8.x-winx64.msi", "sha256": "...",
      "silentArgs": "/qn PORT=3306 PASSWORD=__GENERATED__", "serviceName": "AdaMySQL",
      "default": false, "note": "MariaDB ile alternatif; sihirbazda biri seçilir" },

    { "id": "postgresql-16", "displayName": "PostgreSQL 16", "type": "installer",
      "downloadUrl": "https://get.enterprisedb.com/postgresql/postgresql-16.x-windows-x64.exe", "sha256": "...",
      "silentArgs": "--mode unattended --superpassword __GENERATED__ --serverport 5432",
      "serviceName": "AdaPostgreSQL", "default": true },

    { "id": "composer",   "displayName": "Composer",  "type": "phar",     "default": true },
    { "id": "phpmyadmin", "displayName": "phpMyAdmin","type": "zip",      "default": true },
    { "id": "adminer",    "displayName": "Adminer",   "type": "phpFile",  "default": false },
    { "id": "redis",      "displayName": "Redis",     "type": "zip",  "serviceName": "AdaRedis",     "default": true },
    { "id": "memcached",  "displayName": "Memcached", "type": "zip",  "serviceName": "AdaMemcached", "default": false },
    { "id": "nodejs-lts", "displayName": "Node.js LTS","type": "msi", "default": false },
    { "id": "python-3",   "displayName": "Python 3.x","type": "installer","default": false },
    { "id": "win-acme",   "displayName": "win-acme (Let's Encrypt)", "type": "zip", "default": true }
  ]
}
```
`default:true` olanlar "Tam" kurulumda otomatik gelir. MSSQL/Node/Python/Memcached/Adminer varsayılan
kapalı; sihirbazda veya panelden sonradan tek tıkla kurulur. **Online installer** prensibi: bileşenler
exe'ye gömülmez, kurulumda resmî kaynaktan indirilir (bu aşamada internet gerekir).

---

# BÖLÜM C — Tam Klasör Ağacı
```
AdaPanel/
├─ AdaPanel.sln
├─ README.md
├─ components.json                 # bağımlılık manifesti (Bootstrapper okur)
├─ src/
│  ├─ AdaPanel.Core/               # arabirimler, modeller, DTO'lar (bağımlılıksız)
│  │  ├─ Common/                   # ApiResponse, AppException
│  │  ├─ Entities/                 # PanelUser, AuditLog, InstalledComponent, PanelSetting
│  │  ├─ Dtos/                     # Site, Database, Component, System, Auth DTO'ları
│  │  ├─ Enums/                    # SiteRuntime, DbEngine, ComponentType, UserRole...
│  │  └─ Interfaces/               # tüm servis arabirimleri (I*Service)
│  ├─ AdaPanel.Data/               # EF Core + SQLite
│  │  ├─ AppDbContext.cs
│  │  ├─ Configurations/           # entity yapılandırmaları
│  │  └─ Migrations/
│  ├─ AdaPanel.Infrastructure/     # arabirim implementasyonları
│  │  ├─ Iis/  Php/  AspNet/  AppRuntimes/  ReverseProxy/
│  │  ├─ Database/  WebDbClient/  Cache/
│  │  ├─ Files/  Ssl/  Ftp/  Waf/
│  │  ├─ Scheduler/  Firewall/  Logs/  Notifications/
│  │  ├─ Backup/  AppStore/  SelfUpdate/
│  │  ├─ System/  Bootstrap/  Security/
│  │  └─ DependencyInjection.cs    # AddInfrastructure() — DI kaydı
│  └─ AdaPanel.Api/                # web host + Windows Service
│     ├─ Program.cs
│     ├─ appsettings.json
│     ├─ Controllers/              # her modül için bir controller
│     ├─ Middleware/               # hata yakalama, audit, güvenli giriş
│     ├─ Auth/                     # JWT/cookie, TOTP doğrulama
│     ├─ Resources/                # tr.json, en.json (backend i18n)
│     └─ wwwroot/                  # build edilmiş Vue SPA buraya kopyalanır
├─ frontend/                       # Vue 3 + Vite kaynak
│  ├─ index.html  package.json  vite.config.ts
│  └─ src/
│     ├─ main.ts  App.vue
│     ├─ router/                   # route tanımları
│     ├─ stores/                   # Pinia (auth, ui)
│     ├─ api/                      # axios servisleri (her modül)
│     ├─ i18n/                     # tr.json, en.json
│     ├─ layouts/                  # ana layout (sidebar + topbar)
│     ├─ components/               # ortak bileşenler
│     └─ views/                    # sayfalar (modül başına)
├─ installer/
│  └─ AdaPanel.iss                 # Inno Setup scripti
├─ scripts/
│  ├─ build-installer.ps1          # frontend build → publish → installer
│  ├─ install-service.ps1
│  └─ enable-iis-features.ps1      # IIS + CGI özelliklerini açar
└─ tests/
   ├─ AdaPanel.Core.Tests/
   └─ AdaPanel.Infrastructure.Tests/
```

---

# BÖLÜM D — Dosya Dosya Yapı

> Her dosya için: **görevi** ve **içermesi gereken tipler/üyeler**. (Kod gövdesi yok; Codex yazacak.)

## D.1 AdaPanel.Core
| Dosya | Görev / İçerik |
|---|---|
| `Common/ApiResponse.cs` | Ortak yanıt zarfı `ApiResponse<T>` { `Success`, `Data`, `Message`, `Code` } + `Ok()`/`Fail()` yardımcıları |
| `Common/AppException.cs` | i18n `MessageKey` + `Code` taşıyan uygulama hatası |
| `Entities/PanelUser.cs` | Id, Username, PasswordHash, Role, Language, TwoFactorEnabled, TotpSecret, CreatedAt, LastLoginAt |
| `Entities/AuditLog.cs` | Id, Username, Action, Resource, Detail, IpAddress, CreatedAt |
| `Entities/InstalledComponent.cs` | Id, ComponentId, DisplayName, Version, InstallDir, ServiceName, Status, InstalledAt |
| `Entities/PanelSetting.cs` | Id, Key, Value (port, dil, güvenli giriş yolu vb.) |
| `Enums/Enums.cs` | `SiteRuntime`{Php,AspNet,ClassicAsp,Node,Python,Static}, `DbEngine`{MySql,MariaDb,PostgreSql,MsSql}, `ComponentType`, `ComponentStatus`, `UserRole`, `BackupTarget` |
| `Dtos/SiteDtos.cs` | `SiteDto`, `SiteBindingDto`, `CreateSiteRequest` |
| `Dtos/DatabaseDtos.cs` | `DatabaseDto`, `DbUserDto`, `CreateDatabaseRequest` |
| `Dtos/ComponentDtos.cs` | `ComponentManifestItem` (manifest şeması), `InstallProgress` |
| `Dtos/SystemDtos.cs` | `SystemMetrics`, `SystemInfo` |
| `Dtos/AuthDtos.cs` | `LoginRequest`, `LoginResult` |
| `Interfaces/*.cs` | Aşağıdaki tüm servis arabirimleri |

### Core arabirimleri (her biri ayrı dosya — `Interfaces/`)
| Arabirim | Anahtar metotlar (imza, gövde yok) |
|---|---|
| `IIisSiteService` | GetSites, GetSite, CreateSite, DeleteSite, Start/StopSite, Add/RemoveBinding, **ImportExistingSites** |
| `IPhpService` | GetInstalledVersions, InstallVersionAsync, SetSitePhpVersion, RegisterFastCgi |
| `IAspNetService` | EnableClassicAsp, SetDotNetVersion, **EnableMixedHandlers** (aynı sitede .php+.asp+.aspx) |
| `IAppRuntimeService` | HostNodeApp, HostPythonApp (HttpPlatformHandler) |
| `IReverseProxyService` | EnableProxy, RemoveProxy, AddRewriteRule (ARR + URL Rewrite) |
| `IDatabaseService` | GetDatabases, CreateDatabase, DeleteDatabase, CreateUser, GrantAccess (DbEngine parametreli) |
| `IWebDbClientService` | DeployPhpMyAdmin, DeployAdminer, GetAccessUrl |
| `ICacheService` | StartRedis, StopRedis, StartMemcached, StopMemcached, IsRunning |
| `IFileService` | List, ReadAsync, WriteAsync, Delete, CreateDirectory, SetPermissions (sandbox'lı) |
| `ISslService` | IssueLetsEncryptAsync, ImportCertificate, **RenewAllAsync** (otomatik yenileme) |
| `IFtpService` | CreateFtpUser, DeleteFtpUser, GetFtpUsers |
| `IWafService` | IsolateAppPool, ApplyFolderAcl, SetRateLimit, EnableBasicRules |
| `ISchedulerService` | CreateTask, DeleteTask, GetTasks (Task Scheduler) |
| `IFirewallService` | AllowPort, BlockPort, GetRules |
| `ILogViewerService` | TailPanelLogAsync, TailSiteLogAsync |
| `INotificationService` | SendEmailAsync, SendWebhookAsync |
| `IBackupService` | BackupSiteAsync, BackupDatabaseAsync, RestoreAsync (BackupTarget: Local/Ftp/S3/Drive) |
| `IAppStoreService` | GetTemplates, InstallAsync |
| `ISelfUpdateService` | CheckForUpdateAsync, ApplyUpdateAsync |
| `ISystemMetricsService` | GetMetrics, GetInfo |
| `IComponentBootstrapper` | GetManifest, GetDefaults, InstallAsync, **InstallDefaultsAsync** |
| `IAuthService` | LoginAsync, EnsureSeedAdminAsync, SetupTwoFactorAsync |
| `IAuditService` | LogAsync |

## D.2 AdaPanel.Data
| Dosya | Görev / İçerik |
|---|---|
| `AppDbContext.cs` | `DbSet<PanelUser>`, `<AuditLog>`, `<InstalledComponent>`, `<PanelSetting>`; SQLite bağlantısı |
| `Configurations/*.cs` | Her entity için Fluent API yapılandırması (index, kısıt) |
| `Migrations/` | `dotnet ef migrations add Init` ile üretilir; ilk açılışta otomatik `Migrate()` |

## D.3 AdaPanel.Infrastructure (implementasyonlar)
| Klasör / Dosya | Görev / Teknik not |
|---|---|
| `Iis/WebAdministrationSiteService.cs` | `IIisSiteService` impl. `ServerManager` ile Sites/AppPools/Bindings; ImportExistingSites mevcut siteleri okur |
| `Php/PhpService.cs` | `IPhpService`; Bootstrapper ile PHP indir, IIS FastCGI handler ekle, php.ini yaz |
| `AspNet/AspNetService.cs` | `IAspNetService`; Classic ASP (asp.dll) + .NET CLR; **EnableMixedHandlers** tek sitede çoklu handler |
| `AppRuntimes/AppRuntimeService.cs` | `IAppRuntimeService`; HttpPlatformHandler ile Node/Python |
| `ReverseProxy/ReverseProxyService.cs` | `IReverseProxyService`; ARR + URL Rewrite web.config |
| `Database/MySqlProvider.cs`, `PostgreSqlProvider.cs`, `MsSqlProvider.cs`, `DatabaseService.cs` | `IDatabaseService`; motor başına sağlayıcı, ortak soyutlama |
| `WebDbClient/WebDbClientService.cs` | `IWebDbClientService`; phpMyAdmin/Adminer dağıt, erişim sitesi/URL |
| `Cache/CacheService.cs` | `ICacheService`; Redis/Memcached servis kontrolü |
| `Files/FileService.cs` | `IFileService`; **kök sandbox** + path traversal engeli, ACL |
| `Ssl/WinAcmeSslService.cs` | `ISslService`; win-acme ile Let's Encrypt, manuel pfx, RenewAll |
| `Ftp/FtpService.cs` | `IFtpService`; IIS FTP veya FileZilla Server |
| `Waf/WafService.cs` | `IWafService`; app pool kimlik izolasyonu, ACL, rate-limit |
| `Scheduler/SchedulerService.cs` | `ISchedulerService`; Windows Task Scheduler (TaskService) |
| `Firewall/FirewallService.cs` | `IFirewallService`; `netsh advfirewall` / PowerShell |
| `Logs/LogViewerService.cs` | `ILogViewerService`; panel logu + IIS site logları (tail) |
| `Notifications/NotificationService.cs` | `INotificationService`; SMTP e-posta + webhook |
| `Backup/BackupService.cs` | `IBackupService`; zip + DB dump; hedef: yerel/FTP/S3/Drive |
| `AppStore/AppStoreService.cs` | `IAppStoreService`; şablon (WordPress vb.) indir+kur |
| `SelfUpdate/SelfUpdateService.cs` | `ISelfUpdateService`; sürüm kontrol + güvenli güncelleme |
| `System/SystemMetricsService.cs` | `ISystemMetricsService`; `PerformanceCounter` ile CPU/RAM/disk/ağ |
| `Bootstrap/ComponentBootstrapper.cs` | `IComponentBootstrapper`; components.json oku, indir/doğrula/kur akışı (B.2) |
| `Security/PasswordHasher.cs`, `TotpService.cs`, `SecretProtector.cs` | PBKDF2/BCrypt; TOTP; DB parolalarını şifreli saklama (DPAPI) |
| `DependencyInjection.cs` | `AddInfrastructure(IServiceCollection)` — tüm I*Service → impl kaydı + `IConcurrencyLock` (SemaphoreSlim) |

## D.4 AdaPanel.Api
| Dosya | Görev / İçerik |
|---|---|
| `Program.cs` | Host kurulumu: `UseWindowsService()`, Kestrel (8888, self-signed), Serilog, EF `Migrate()`, `AddInfrastructure()`, auth, controller, SPA fallback (wwwroot) |
| `appsettings.json` | Port, ConnectionString (SQLite), Serilog, SMTP, güvenli giriş yolu, varsayılan dil |
| `Middleware/ErrorHandlingMiddleware.cs` | Exception → ortak zarf `Fail(...)`; ham hata loglanır |
| `Middleware/AuditMiddleware.cs` | Değiştiren isteklerde `IAuditService.LogAsync` |
| `Middleware/SecureEntranceMiddleware.cs` | Rastgele giriş yolu + opsiyonel IP kısıtı |
| `Auth/JwtTokenFactory.cs`, `AuthService` kaydı | Token üretimi/doğrulama; TOTP kontrolü |
| `Resources/tr.json`, `en.json` | Backend i18n mesajları |
| `Controllers/*Controller.cs` | Her modül için bir controller (D.5) |
| `wwwroot/` | Build edilmiş Vue SPA (boş bırakılır, build doldurur) |

### D.5 Controller ↔ Endpoint haritası (hepsi `/api/v1` altında, ortak zarf döner)
| Controller | Başlıca uç noktalar |
|---|---|
| `AuthController` | `POST /auth/login`, `POST /auth/2fa/setup`, `GET /auth/me` |
| `SitesController` | `GET /sites`, `POST /sites`, `DELETE /sites/{name}`, `POST /sites/{name}/start|stop`, `POST /sites/import` |
| `PhpController` | `GET /php/versions`, `POST /php/install`, `POST /sites/{name}/php` |
| `AspController` | `POST /sites/{name}/asp/classic`, `POST /sites/{name}/dotnet`, `POST /sites/{name}/mixed` |
| `AppRuntimeController` | `POST /sites/{name}/node`, `POST /sites/{name}/python` |
| `ProxyController` | `POST /sites/{name}/proxy`, `POST /sites/{name}/rewrite`, `DELETE /sites/{name}/proxy` |
| `DatabasesController` | `GET /db?engine=`, `POST /db`, `DELETE /db/{name}`, `POST /db/users`, `POST /db/grant` |
| `WebDbController` | `POST /webdb/phpmyadmin`, `POST /webdb/adminer`, `GET /webdb/url` |
| `CacheController` | `POST /cache/redis/start|stop`, `POST /cache/memcached/start|stop`, `GET /cache/status` |
| `FilesController` | `GET /files?path=`, `GET /files/download`, `POST /files/upload`, `PUT /files`, `DELETE /files`, `POST /files/mkdir`, `POST /files/acl` |
| `SslController` | `POST /ssl/letsencrypt`, `POST /ssl/import`, `POST /ssl/renew` |
| `FtpController` | `GET /ftp/users`, `POST /ftp/users`, `DELETE /ftp/users/{u}` |
| `SecurityController` | `POST /sites/{name}/isolate`, `POST /sites/{name}/acl`, `POST /sites/{name}/ratelimit` |
| `CronController` | `GET /cron`, `POST /cron`, `DELETE /cron/{name}` |
| `FirewallController` | `GET /firewall`, `POST /firewall/allow`, `POST /firewall/block` |
| `LogsController` | `GET /logs/panel?lines=`, `GET /logs/site/{name}?lines=` |
| `NotificationsController` | `POST /notify/test`, `PUT /notify/settings` |
| `BackupController` | `POST /backup/site`, `POST /backup/db`, `POST /backup/restore`, `GET /backup` |
| `StoreController` | `GET /store`, `POST /store/install` |
| `UpdateController` | `GET /update/check`, `POST /update/apply` |
| `SystemController` | `GET /system/metrics`, `GET /system/info` |
| `SoftwareController` | `GET /software` (manifest+kurulu), `POST /software/install`, `DELETE /software/{id}` |
| `UsersController` | `GET /users`, `POST /users`, `PUT /users/{id}`, `DELETE /users/{id}` |
| `SettingsController` | `GET /settings`, `PUT /settings` (port, dil, güvenli giriş) |

## D.6 frontend (Vue 3)
| Dosya / Klasör | Görev |
|---|---|
| `package.json` | bağımlılıklar: vue, vue-router, pinia, vue-i18n, naive-ui, axios; script `build` |
| `vite.config.ts` | build çıktısı `../src/AdaPanel.Api/wwwroot`; dev proxy `/api` → 8888 |
| `src/main.ts` | Vue + router + pinia + i18n + Naive UI kurulumu |
| `src/api/*.ts` | Her modül için axios servis dosyası (sites.ts, db.ts, ...) ortak zarfı çözer |
| `src/stores/auth.ts`, `ui.ts` | token, kullanıcı, dil, tema durumu |
| `src/i18n/tr.json`, `en.json` | Arayüz çevirileri (anahtarlar D.5 modülleriyle eşleşir) |
| `src/layouts/MainLayout.vue` | Sidebar (modül menüsü) + topbar (dil/çıkış) |
| `src/router/index.ts` | Korumalı rotalar; login dışı her şey auth ister |
| `src/views/*.vue` | Sayfalar: Login, Dashboard, Sites, SiteDetail, Php, Asp, Apps, Proxy, Databases, WebDb, Cache, Files, Ssl, Ftp, Security, Cron, Firewall, Logs, Notifications, Backup, Store, Software, Update, Users, Settings |

## D.7 installer / scripts / tests
| Dosya | Görev |
|---|---|
| `installer/AdaPanel.iss` | Inno Setup: dosya kopyalama, UAC, servis kaydı, port sorma, sertifika, Bootstrapper çağrısı, uninstall |
| `scripts/build-installer.ps1` | frontend build → `dotnet publish` (self-contained single-file) → Inno Setup derle |
| `scripts/install-service.ps1` | `sc create` / `New-Service` ile servis kaydı (geliştirici/yedek) |
| `scripts/enable-iis-features.ps1` | `dism`/`Enable-WindowsOptionalFeature` ile IIS + CGI özelliklerini açar |
| `tests/AdaPanel.Core.Tests/` | iş mantığı birim testleri (xUnit) |
| `tests/AdaPanel.Infrastructure.Tests/` | IIS/DB entegrasyon testleri |

---

# BÖLÜM E — Modül / Adım Kılavuzu (25 Modül)

> Her satır bir Codex görevi olabilir. Sıra önerilen geliştirme sırasıdır; her adım **derlenebilir** kalmalı.

| # | Modül | Dokunulan başlıca dosyalar | Yapılacak |
|---|---|---|---|
| 1 | Plan | (bu belge) | Mimari, kurallar, yapı |
| 2 | İskelet | sln, 4 csproj, Program.cs, AppDbContext, DependencyInjection, appsettings | Derlenen boş çözüm; Windows Service; SQLite+EF Migrate; Serilog; SPA fallback |
| 3 | Auth & Güvenlik | AuthController, AuthService, JwtTokenFactory, TotpService, PasswordHasher, SecureEntranceMiddleware, UsersController | Login, 2FA, güvenli giriş, kullanıcı yönetimi, audit |
| 4 | Dashboard & İzleme | SystemMetricsService, SystemController, Dashboard.vue | CPU/RAM/disk/ağ + sistem bilgisi |
| 5 | Bootstrapper | ComponentBootstrapper, components.json, SoftwareController, Software.vue | indir/doğrula/kur/servis/parola/kayıt; Yazılımlar sayfası |
| 6 | IIS Site | WebAdministrationSiteService, SitesController, Sites.vue, SiteDetail.vue | site/binding/app pool CRUD + ImportExistingSites |
| 7 | PHP | PhpService, PhpController, Php.vue | çoklu PHP, FastCGI handler |
| 8 | ASP/ASP.NET + eşzamanlı | AspNetService, AspController, Asp.vue | Classic ASP + .NET; **EnableMixedHandlers** |
| 9 | Node/Python | AppRuntimeService, AppRuntimeController, Apps.vue | HttpPlatformHandler barındırma |
| 10 | Reverse Proxy | ReverseProxyService, ProxyController, Proxy.vue | ARR + URL Rewrite |
| 11 | Veritabanı | *Provider'lar, DatabaseService, DatabasesController, Databases.vue | MySQL/MariaDB/PostgreSQL(+MSSQL) yönetimi |
| 12 | Web DB Arayüzü | WebDbClientService, WebDbController, WebDb.vue | phpMyAdmin/Adminer |
| 13 | Önbellek | CacheService, CacheController, Cache.vue | Redis/Memcached |
| 14 | Dosya Yöneticisi | FileService, FilesController, Files.vue | sandbox'lı dosya işlemleri |
| 15 | SSL/TLS | WinAcmeSslService, SslController, Ssl.vue | Let's Encrypt + manuel + RenewAll |
| 16 | FTP | FtpService, FtpController, Ftp.vue | FTP kullanıcı yönetimi |
| 17 | Site Güvenliği/WAF | WafService, SecurityController, Security.vue | izolasyon, ACL, rate-limit |
| 18 | Zamanlanmış Görevler | SchedulerService, CronController, Cron.vue | Task Scheduler |
| 19 | Güvenlik Duvarı | FirewallService, FirewallController, Firewall.vue | port aç/engelle |
| 20 | Loglar | LogViewerService, LogsController, Logs.vue | panel + site logları |
| 21 | Bildirim/Uyarı | NotificationService, NotificationsController, Notifications.vue | e-posta/webhook + tetikleyiciler |
| 22 | Yedekleme | BackupService, BackupController, Backup.vue | yerel + bulut + geri yükleme |
| 23 | App Store | AppStoreService, StoreController, Store.vue | tek tık uygulama |
| 24 | Self-Update | SelfUpdateService, UpdateController, Update.vue | sürüm kontrol + güncelleme |
| 25 | i18n + Installer | Resources, i18n json'lar, AdaPanel.iss, build-installer.ps1 | çeviri tamam + tek `AdaPanelSetup.exe` |

---

# BÖLÜM F — Codex Ana Promptu

> Codex'e bir modül yazdırırken bunu baştan ver, sonra ilgili modülün (BÖLÜM E) satırını ekle.

```
Sen AdaPanel adlı bir Windows web hosting kontrol paneli geliştiriyorsun.
Aşağıdaki mimari ve kurallara KESİNLİKLE uy:

- Backend: ASP.NET Core 8 (C#), Windows Service olarak çalışır.
- IIS yönetimi: Microsoft.Web.Administration ile (appcmd parse etme).
- Web sunucusu: IIS. Aynı site/sunucuda ASP (Classic ASP + ASP.NET) ve PHP
  AYNI ANDA çalışabilmeli (IIS handler mapping).
- Panel veritabanı: SQLite + EF Core.
- Yönetilen veritabanları: MySQL/MariaDB + PostgreSQL (opsiyonel MSSQL).
- Frontend: Vue 3 + Vite + Pinia + Vue I18n + Naive UI; build → AdaPanel.Api/wwwroot
  (tek port, tek çalıştırılabilir).
- Mimari: Api / Core / Infrastructure / Data (Clean Architecture); bağımlılık Core'a doğru.
- API: REST /api/v1, ortak yanıt zarfı { success, data, message, code }.
- Dil: Türkçe + İngilizce (i18n, varsayılan Türkçe). Kod İngilizce, kullanıcı metni i18n anahtarı.
- Güvenlik: auth + admin rolü + audit log; path traversal sandbox; 2FA (TOTP);
  güvenli giriş yolu; DB parolaları otomatik üretilip şifreli saklanır.
- Bağımlılıklar components.json'dan Bootstrapper ile OTOMATİK indirilir, sha256
  doğrulanır, sessiz kurulur, Windows Service kaydedilir.
- Paketleme: self-contained single-file publish (win-x64) + Inno Setup ile tek
  AdaPanelSetup.exe; kurulum IIS+CGI özelliklerini açar, servis kaydeder, port+sertifika ayarlar.
- Kurallar: birim/entegrasyon test; ortak hata yönetimi; IIS yazma işlemlerinde
  kilitleme (SemaphoreSlim); başarısız kritik işlemde rollback.
- Her modül kendi başına derlenebilir olmalı; arabirimler AdaPanel.Core'da tanımlı,
  implementasyon AdaPanel.Infrastructure'da, DI ile kaydedilir.

Şimdi şu modülü, BÖLÜM D'deki dosya yapısına ve imzalara uygun şekilde, eksiksiz
derlenebilir kod olarak üret:
[BURAYA BÖLÜM E'DEKİ İLGİLİ MODÜL SATIRINI YAPIŞTIR]
```

---

*AdaPanel — Tam Proje Yapısı · tek belge · kod Codex tarafından bu yapıya göre üretilir.*