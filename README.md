<div align="center">

# AdaPanel

### Windows hosting, from one panel. · Windows barındırma, tek panelden.

A modern, professional hosting control panel for **Windows Server + IIS** — everything Plesk does, built for Windows, clean and fast.
**Windows Server + IIS** için modern, profesyonel barındırma kontrol paneli — Plesk'in yaptığı her şey, Windows'a özel, sade ve hızlı.

![Platform](https://img.shields.io/badge/Windows%20Server-2016%2B-0078D6)
![Backend](https://img.shields.io/badge/.NET-8-512BD4)
![Frontend](https://img.shields.io/badge/Vue-3-42b883)
![UI](https://img.shields.io/badge/UI-English%20%C2%B7%20T%C3%BCrk%C3%A7e-2ea44f)
![Installer](https://img.shields.io/badge/Single-installer-orange)

[**⬇ Download / İndir**](https://github.com/aliorala/adapanel/releases/latest) · [Website](https://aliorala.github.io/adapanel/)

</div>

---

## 🇬🇧 English

AdaPanel is an all‑in‑one hosting control panel for **Windows Server + IIS**. A single installer sets up everything (IIS, PHP, mail, database, panel service, firewall rules, HTTPS) and gives you one modern interface to manage websites, e‑mail, databases, DNS, SSL, FTP, backups and more — with full **Administrator / Reseller / Customer** multi‑tenancy.

### ✨ Features

**Websites & runtimes**
- Create / start / stop / delete IIS sites, with **one‑flow provisioning** (site + database + mail + FTP together)
- **PHP** — multiple versions, per‑site & global settings, `open_basedir` isolation, Redis extension
- **Node.js & Python** apps, plus Static / ASP.NET / Classic ASP (mixed profile on one site)
- Pretty‑URL rewrite for **Laravel & WordPress**, document root, app pools, custom bindings, reverse proxy (URL Rewrite + ARR)
- **Domain‑less site preview** before DNS is pointed (nip.io + automatic certificate)

**Databases**
- **MariaDB / MySQL**, **PostgreSQL**, local **MSSQL**
- Create databases & users, manage grants, **phpMyAdmin / Adminer / pgAdmin** (authenticated reverse‑proxy)
- Server‑side **transfer & import** (mysqldump), link databases to sites, cascade delete

**Mail (Stalwart)**
- Domains, mailboxes, aliases, mailing lists, **per‑mailbox & per‑domain quotas**
- One‑click **Roundcube webmail**, vacation / auto‑reply (ManageSieve)
- **SPF / DKIM / DMARC** auto‑records, spam filtering, rDNS check, live mail diagnostics
- Automatic **Let's Encrypt** for the mail host

**DNS & SSL**
- DNS zones & records, server‑wide DNS templates
- **Let's Encrypt** auto‑issue + auto‑renew, certificate import, forced HTTPS

**WordPress Toolkit**
- One‑click **turnkey install** (database + user + wp‑config + admin account created for you)
- One‑click **admin login (SSO)**, core / plugin / theme updates + auto‑updates
- **Security hardening** + status panel, maintenance mode
- Plugin & theme management (install, activate/deactivate, update, delete)
- Search‑replace, admin password reset, **clone / staging**, **copy data (staging → production)**

**Backups**
- **Scheduled automatic backups** — all sites + databases + mail + panel config
- **Retention** with automatic old‑backup cleanup — **local and off‑site**
- Off‑site targets: **FTP, S3** (incl. MinIO / R2 / Wasabi), **WebDAV**
- Full **and granular** restore (browse and restore individual files)

**Migration**
- One‑flow site migration (create site + pull files + transfer database)
- **Plesk REST auto‑discovery**, cPanel discovery

**Security & multi‑tenancy**
- Firewall, brute‑force protection, WAF, rate limiting, site isolation, protected directories
- **Two‑factor authentication (TOTP)**, hardened data ACLs, secured MariaDB root, per‑site `php.ini` hardening, audit log
- Roles: **Administrator / Reseller / Customer** with resource ownership
- **Service plans** with quotas (sites, databases, mailboxes, disk) + customer self‑service

**Apps & operations**
- App store, **Docker** management, **Git** deploy with webhooks, **cron / scheduled tasks**
- Composer / Artisan / npm / WP‑CLI console, Redis & Memcached
- **File manager** (upload, compress/extract, permissions), **FTP / FTPS** accounts
- **Monitoring** (CPU / RAM / network history), server health, web statistics, logs, terminal
- Notifications & alerts, **one‑click self‑update** from GitHub Releases (SHA‑256 verified)
- Fully **bilingual UI** (English / Türkçe)

### 🖥️ Requirements
- Windows Server 2016+ (or Windows 10/11), **Administrator** rights
- IIS (the installer enables the required features automatically); the .NET runtime ships **embedded** (self‑contained)

### 🚀 Install
1. Download **`AdaPanelSetup.exe`** from the [latest release](https://github.com/aliorala/adapanel/releases/latest) and **run it as Administrator**.
2. It installs the AdaPanel Windows service, firewall rules (panel + mail ports) and the panel HTTPS certificate automatically.
3. Open the panel at **`https://<server>:8888/<secure-path>`**. The **first‑run setup opens only from the server itself (localhost)** for security.
4. Create the admin account, choose the language and the secure entry path.

> The secure entry path is randomly generated on first run and the panel is closed to access outside that path — note it down.

### 🔄 Updates
Open **Update** in the panel — it checks GitHub Releases and installs the newest version in one click, verifying the SHA‑256.

### 🔌 Ports
| Port | Purpose |
|------|---------|
| 8888 (default) | Panel (HTTPS) |
| 80 / 443 | IIS websites |
| 25 | SMTP / MX (inbound + server‑to‑server) |
| 587 / 465 | Submission / SMTPS (outbound) |
| 143 / 993 | IMAP / IMAPS |
| 8896 / 8897 / 8899 | phpMyAdmin / Adminer / Webmail (panel tools) |

### 🧩 Built with
ASP.NET Core 8 (Kestrel, Windows Service) · Vue 3 SPA · MariaDB/MySQL · Stalwart Mail · IIS FastCGI PHP · EF Core (SQLite).

---

## 🇹🇷 Türkçe

AdaPanel; Windows 10/11 ve Windows Server 2016+ için **ASP.NET Core 8** ve **Vue 3** ile geliştirilen, Plesk benzeri bir barındırma kontrol panelidir. Tek kurulum dosyası her şeyi (IIS, PHP, e‑posta, veritabanı, panel servisi, güvenlik duvarı, HTTPS) hazırlar; siteleri, e‑postayı, veritabanlarını, DNS'i, SSL'i, FTP'yi ve yedekleri tek modern arayüzden yönetirsiniz — tam **Yönetici / Bayi / Müşteri** çok‑kiracılı yapısıyla.

### ✨ Özellikler

**Web siteleri & çalışma ortamları**
- IIS sitesi oluştur / başlat / durdur / sil, **tek akışta hazırlama** (site + veritabanı + e‑posta + FTP birlikte)
- **PHP** — çoklu sürüm, site‑bazlı & genel ayarlar, `open_basedir` izolasyonu, Redis
- **Node.js & Python** uygulamaları, ayrıca Statik / ASP.NET / Klasik ASP (aynı sitede karma profil)
- **Laravel & WordPress** için temiz‑URL yönlendirme, döküman kökü, uygulama havuzları, özel bağlamalar, reverse proxy (URL Rewrite + ARR)
- DNS yönlendirilmeden **domainsiz site önizleme** (nip.io + otomatik sertifika)

**Veritabanları**
- **MariaDB / MySQL**, **PostgreSQL**, yerel **MSSQL**
- Veritabanı & kullanıcı oluşturma, yetki yönetimi, **phpMyAdmin / Adminer / pgAdmin** (kimlik doğrulamalı reverse‑proxy)
- Sunucu taraflı **taşıma & içe aktarma** (mysqldump), veritabanını siteye bağlama, cascade silme

**E‑posta (Stalwart)**
- Alan adları, posta kutuları, takma adlar, dağıtım listeleri, **kutu‑başına & domain‑başına kota**
- Tek tık **Roundcube webmail**, tatil / otomatik yanıt (ManageSieve)
- **SPF / DKIM / DMARC** otomatik kayıtları, spam filtresi, rDNS kontrolü, canlı mail tanılama
- Posta sunucusu için otomatik **Let's Encrypt**

**DNS & SSL**
- DNS bölgeleri & kayıtları, sunucu‑geneli DNS şablonları
- **Let's Encrypt** otomatik alma + otomatik yenileme, sertifika içe aktarma, zorunlu HTTPS

**WordPress Araç Seti**
- Tek tık **anahtar‑teslim kurulum** (veritabanı + kullanıcı + wp‑config + admin hesabı oluşturulur)
- Tek tık **yönetici girişi (SSO)**, çekirdek / eklenti / tema güncelleme + otomatik güncelleme
- **Güvenlik sıkılaştırma** + durum paneli, bakım modu
- Eklenti & tema yönetimi (kur, aç/kapat, güncelle, sil)
- Ara‑değiştir, yönetici parolası sıfırlama, **klon / staging**, **veri kopyalama (staging → canlı)**

**Yedekleme**
- **Zamanlanmış otomatik yedek** — tüm siteler + veritabanları + e‑posta + panel ayarları
- Eski yedekleri otomatik silen **saklama (retention)** — **yerel ve off‑site**
- Off‑site hedefler: **FTP, S3** (MinIO / R2 / Wasabi dahil), **WebDAV**
- Tam **ve granüler** geri yükleme (tek tek dosya seçip geri yükleme)

**Göç (Migration)**
- Tek akışta site göçü (site oluştur + dosyaları çek + veritabanını taşı)
- **Plesk REST otomatik keşif**, cPanel keşfi

**Güvenlik & çok‑kiracılılık**
- Güvenlik duvarı, kaba‑kuvvet koruması, WAF, hız sınırlama, site izolasyonu, korumalı dizinler
- **İki faktörlü doğrulama (TOTP)**, sıkılaştırılmış veri ACL'leri, güvenli MariaDB root, site‑bazlı `php.ini` sıkılaştırma, audit log
- Roller: **Yönetici / Bayi / Müşteri** ve kaynak sahipliği
- Kotalı **servis planları** (site, veritabanı, posta kutusu, disk) + müşteri self‑servis

**Uygulamalar & operasyon**
- Uygulama mağazası, **Docker** yönetimi, webhook'lu **Git** dağıtımı, **cron / zamanlanmış görevler**
- Composer / Artisan / npm / WP‑CLI konsolu, Redis & Memcached
- **Dosya yöneticisi** (yükle, sıkıştır/aç, izinler), **FTP / FTPS** hesapları
- **İzleme** (CPU / RAM / ağ geçmişi), sunucu sağlığı, web istatistikleri, loglar, terminal
- Bildirimler & uyarılar, GitHub Releases'ten **tek tık otomatik güncelleme** (SHA‑256 doğrulamalı)
- Tamamen **çift dilli arayüz** (İngilizce / Türkçe)

### 🖥️ Sistem gereksinimleri
- Windows 10/11 veya Windows Server 2016+, **Yönetici** hakları (kurulum IIS özelliklerini açar ve servis oluşturur)
- IIS (panel gerekli özellikleri otomatik açar); .NET runtime **gömülü** gelir (self‑contained)

### 🚀 Kurulum
1. [Son sürümden](https://github.com/aliorala/adapanel/releases/latest) **`AdaPanelSetup.exe`** dosyasını indirin ve **yönetici olarak çalıştırın**.
2. Kurulum; AdaPanel Windows servisini, güvenlik duvarı kurallarını (panel + mail portları) ve panel HTTPS sertifikasını otomatik hazırlar.
3. Panele bağlanın: **`https://<sunucu>:8888/<güvenli-yol>`**. İlk kurulum sihirbazı **yalnızca sunucunun kendisinden (localhost)** açılabilir (güvenlik).
4. İlk açılışta yönetici hesabını, dili ve güvenli giriş yolunu belirleyin.

> Güvenli giriş yolu ilk kurulumda rastgele üretilir; panel bu yol dışından erişime kapalıdır. Yolu not edin.

### 🚦 İlk site + mail (hızlı başlangıç)
1. **Web Siteleri → Yeni site** (runtime otomatik hazırlanır).
2. **SSL → Let's Encrypt** ile sertifika al (HTTP→HTTPS yönlendirmesi otomatik açılır).
3. **Posta → domain ekle → posta kutusu oluştur**, ardından **DNS** sekmesindeki MX/SPF/DKIM/DMARC kayıtlarını ekleyin; sağlayıcıda **PTR/rDNS = mail.\<domain\>** ayarlayın.
4. **Posta → Yönet → Mail teşhis → Kontrol et** ile tüm kayıtların yeşil olduğunu doğrulayın.

### 🔌 Portlar
| Port | Amaç |
|------|------|
| 8888 (varsayılan) | Panel (HTTPS) |
| 80 / 443 | IIS web siteleri |
| 25 | SMTP/MX (gelen + sunucular arası) |
| 587 / 465 | Submission / SMTPS (giden) |
| 143 / 993 | IMAP / IMAPS |
| 8896 / 8897 / 8899 | phpMyAdmin / Adminer / Webmail (panel araçları) |

### 🔒 Güvenlik varsayılanları
- Parolalar PBKDF2 ile hash'lenir; 5 hatalı denemede hesap kilidi (15 dk) + IP kaba‑kuvvet koruması
- HTTPS zorunlu (HTTP fallback varsayılan kapalı), HttpOnly + SameSite=Strict çerezler
- CSRF (antiforgery) tüm yazma uçlarında zorunlu; CSP / X‑Frame‑Options / nosniff başlıkları
- Tüm yönetim uçları kimlik + rol doğrulamalı; müşteri/bayi yalnız kendi kapsamını görür
- İsteğe bağlı 2FA (TOTP), audit log; ilk kurulum yalnız localhost'tan

### 🧱 Mimari
- **AdaPanel.Api** — ASP.NET Core 8 API + Vue 3 SPA (wwwroot) tek pakette
- **AdaPanel.Core** — DTO, arayüz, enum (domain sözleşmeleri)
- **AdaPanel.Infrastructure** — IIS / mail / DB / SSL / DNS servisleri (Windows entegrasyonu)
- **AdaPanel.Data** — EF Core (SQLite, `%ProgramData%\AdaPanel\data\adapanel.db`)
- Panel durumu JSON dosyalarında atomik saklanır. Veri kökü: `%ProgramData%\AdaPanel`

### 🛠️ Geliştirme
```powershell
npm --prefix frontend run typecheck
npm --prefix frontend run build
.\.tools\dotnet\dotnet.exe test AdaPanel.sln -c Release
.\scripts\build-installer.ps1   # çıktı: artifacts\installer\AdaPanelSetup.exe
```

---

<div align="center">

© 2026 AdaPanel · Tüm hakları saklıdır / All rights reserved

</div>
