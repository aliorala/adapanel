# AdaPanel

AdaPanel, Windows 10/11 ve Windows Server 2016+ için ASP.NET Core 8 ve Vue 3 ile geliştirilen,
Plesk benzeri bir web hosting kontrol panelidir. IIS üzerinde site, mail, veritabanı, SSL, DNS, FTP
ve daha fazlasını tek panelden yönetir.

## Özellikler

- IIS site, binding, DNS, hosting, SSL, WAF ve FTP yönetimi
- Aynı sitede PHP, Classic ASP ve ASP.NET karma profili
- Node.js ve Python için otomatik HttpPlatformHandler kurulumu
- IIS URL Rewrite ve ARR ile reverse proxy
- MariaDB, MySQL, PostgreSQL ve yerel MSSQL yönetimi
- phpMyAdmin (her DB için izole, otomatik giriş), Adminer ve pgAdmin
- Stalwart mail sunucusu: domain, posta kutusu, alias, dağıtım listesi, otomatik TLS, DNS (SPF/DKIM/
  DMARC), mail teşhis kartı ve tek tık Roundcube webmail
- Tek tık Laravel/WordPress kurulumu
- Redis, Memcached, dosya yöneticisi, loglar, terminal ve planlı görevler
- Yerel/uzak yedekleme, firewall, bildirimler, App Store ve SHA256 doğrulamalı otomatik self-update
- Çok kiracılı: müşteri/bayi kapsamı, kaynak kotaları, izolasyon
- Türkçe/İngilizce arayüz, 2FA, güvenli giriş yolu ve audit kayıtları

Site oluşturulduğunda dosya/log/yedek alanları, DNS kayıtları, hosting ayarları, ACL/WAF, SSL çalışma
alanı, FTP altyapısı ve seçilen runtime için gerekli varsayılanlar otomatik hazırlanır.

## Sistem gereksinimleri

- Windows 10/11 veya Windows Server 2016+
- Yönetici (Administrator) hakları — kurulum IIS özelliklerini etkinleştirir ve servis oluşturur
- IIS (panel gerekli özellikleri otomatik açar); .NET runtime gömülü gelir (self-contained)

## Kurulum (production)

1. `artifacts\installer\AdaPanelSetup.exe` dosyasını **yönetici olarak** çalıştırın.
2. Kurulum; AdaPanel Windows servisini, güvenlik duvarı kurallarını (panel + mail portları:
   25/143/465/587/993/995) ve panel HTTPS sertifikasını otomatik hazırlar.
3. Tarayıcıdan panele bağlanın: `https://<sunucu>:8888/<güvenli-yol>`
   - İlk kurulum sihirbazı **yalnızca sunucunun kendisinden (localhost)** açılabilir (güvenlik).
4. İlk açılışta yönetici hesabını, dili ve güvenli giriş yolunu belirleyin.

> Güvenli giriş yolu (SecureEntryPath) ilk kurulumda rastgele üretilir; panel bu yol dışından erişime
> kapalıdır. Yolu not edin.

## İlk site + mail hızlı başlangıç

1. **Web Siteleri → Yeni site** (runtime: static/PHP/ASP.NET/Node/Python otomatik hazırlanır).
2. **SSL → Let's Encrypt** ile sertifika al (HTTP→HTTPS yönlendirmesi otomatik açılır).
3. **Posta → domain ekle → posta kutusu oluştur**, ardından **DNS** sekmesindeki MX/SPF/DKIM/DMARC
   kayıtlarını alan adınızın DNS'ine ekleyin; sağlayıcıda **PTR/rDNS = mail.<domain>** ayarlayın.
4. **Posta → Yönet → Mail teşhis → Kontrol et** ile tüm kayıtların yeşil olduğunu doğrulayın.

## Portlar

| Port | Amaç |
|------|------|
| 8888 (varsayılan) | Panel (HTTPS) |
| 80 / 443 | IIS web siteleri |
| 25 | SMTP/MX (gelen + sunucular arası) |
| 587 / 465 | Submission / SMTPS (giden) |
| 143 / 993 | IMAP / IMAPS |
| 8896 / 8897 / 8899 | phpMyAdmin / Adminer / Webmail (panel araçları) |

## Güvenlik varsayılanları

- Parolalar PBKDF2 ile hash'lenir; 5 hatalı denemede hesap kilidi (15 dk) + IP brute-force koruması
- HTTPS zorunlu (HTTP fallback varsayılan kapalı), HttpOnly + SameSite=Strict çerezler
- CSRF (antiforgery) tüm yazma uçlarında zorunlu; CSP/X-Frame-Options/nosniff başlıkları
- Tüm yönetim uçları kimlik+rol doğrulamalı; müşteri/bayi yalnız kendi kapsamını görür
- İsteğe bağlı 2FA (TOTP), audit log
- İlk kurulum yalnız localhost'tan; admin oluştuktan sonra tekrar çalışmaz

## Geliştirme

```powershell
npm --prefix frontend run typecheck
npm --prefix frontend run build
.\.tools\dotnet\dotnet.exe test AdaPanel.sln -c Release
.\scripts\build-installer.ps1
```

Çıktı: `artifacts\installer\AdaPanelSetup.exe`

## Mimari

- **AdaPanel.Api** — ASP.NET Core 8 API + Vue 3 SPA (wwwroot) tek pakette
- **AdaPanel.Core** — DTO, arayüz, enum (domain sözleşmeleri)
- **AdaPanel.Infrastructure** — IIS/mail/DB/SSL/DNS servisleri (Windows entegrasyonu)
- **AdaPanel.Data** — EF Core (SQLite, `%ProgramData%\AdaPanel\data\adapanel.db`)
- Panel durumu JSON dosyalarında atomik (geçici dosya + atomik taşıma) saklanır

## Yedekleme

Veri kökü `%ProgramData%\AdaPanel` (DB + config + state). Panel içi **Yedekleme** modülü yerel/uzak
(S3 uyumlu) hedefe yedek alır. Güncellemeden önce bu klasörü yedeklemeniz önerilir.

## Geliştirici notları

- Şema: DB SQLite + `EnsureCreated` ile oluşturulur (gömülü panel için yaygın desen). İleride şema
  DEĞİŞİKLİĞİ (yeni kolon/tablo) eklenecekse, mevcut kurulumların güncellenebilmesi için EF Core
  migration'a geçiş gerekir (InitialCreate üretimi + baseline + `MigrateAsync`). Taze kurulumlarda
  EnsureCreated tam şemayı oluşturur.
- Yeni geliştirme oturumunda önce `CODEX_MEMORY.md` okunmalıdır.
