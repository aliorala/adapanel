# AdaPanel — Canlı Kurulum ve Doğrulama Runbook'u

> Hedef: `AdaPanelSetup.exe` ile temiz bir **Windows Server 2019+** VPS'e kurulum ve müşteri almadan önce
> her şeyin çalıştığının canlı doğrulanması. Bu dosya panelin **kaynak kodundan** çıkarılmıştır; menü
> adları üründe birebir aynı olmayabilir, akış aynıdır.
>
> İlgili sürüm: installer SHA256 `E117C96A7A8CB7FF9DF139A1F0733AC3E63E657D9F2E89C11AF5A92C5DE688A6` (49.31 MiB).

---

## 0. Önkoşullar
- [ ] Windows Server 2019 veya üstü, Administrator erişimi (RDP).
- [ ] IIS + ASP.NET/CGI özellikleri (installer `enable-iis-features.ps1` ile kurar; kurulmazsa panel servisi açılmaz — kurulum loglarına bak).
- [ ] Sunucunun **statik public IP**'si ve (mail için) **PTR/rDNS** kontrolünü sağlayıcıdan yapabiliyor olman.
- [ ] DNS yönetimi: domainlerin NS'i bu sunucuya mı, yoksa harici sağlayıcıya mı? (Mail DNS kayıtlarını nereye ekleyeceğini belirler.)

## 1. Kurulum
1. [ ] `AdaPanelSetup.exe`'yi sunucuya kopyala, **Administrator olarak** çalıştır.
2. [ ] Port seçimi: varsayılan **8890** (HTTPS). Değiştirebilirsin; servis `https://0.0.0.0:<port>`'a bağlanır, firewall kuralı otomatik açılır.
3. [ ] Kurulum biter bitmez panel tarayıcıda açılır. Açılmazsa: `https://<SUNUCU-IP>:8890/panel`
   (kendi kestiğin sertifika → tarayıcı uyarısını geç). Başlat menüsü/masaüstündeki **AdaPanel** kısayolu da bu URL'dir.
4. [ ] Panel açılmıyorsa: `services.msc` → AdaPanel servisi **Running** mı; `C:\ProgramData\AdaPanel\logs\adapanel-<tarih>.log` son satırlar.

## 2. İlk erişim + yönetici hesabı
- [ ] Kurulum sihirbazı → **admin kullanıcı** (güçlü parola) oluştur, ardından giriş yap.
- [ ] (Önerilen) Ayarlar → Güvenlik → admin için **2FA** etkinleştir.

## 3. Güvenlik sertleştirme (müşteri almadan ÖNCE)
> K1 (ProgramData ACL) ve K2 (MariaDB root parolası) zaten **otomatik** uygulanır (K2 MariaDB bileşeni kurulurken).
> Aşağıdaki script kalan sertleştirmeyi yapar; **idempotent**, tekrar çalıştırmak güvenli.

```powershell
# Kurulum klasöründe (varsayılan: C:\Program Files\AdaPanel\scripts)
# WinRM ile uzaktan yönetim KULLANMIYORSAN -HardenManagementPorts ver.
# RDP'yi kendi sabit IP'ne kısıtla (kilitlenmemek için kendi IP'ni doğru yaz!).
.\harden-adapanel.ps1 -HardenManagementPorts -RdpAllowFrom '<SENIN-SABIT-IP>'
```
- [ ] K1 — `C:\ProgramData\AdaPanel` hassas alt klasörleri yalnız SYSTEM/Administrators (tenant okuyamaz).
- [ ] K3 — paylaşımlı php.ini sertleştirildi (expose_php=Off; tehlikeli OS fonksiyonları).
- [ ] B6/B7/B8 — Web(80/443)+DNS(53) kuralları açık; phpMyAdmin public portları (8896/8897) kapalı; SMB/RPC/WinRM engellendi (opt-in).
- [ ] RDP — yalnız belirttiğin IP'den erişilebiliyor (test et: başka ağdan RDP **reddedilmeli**).

## 4. Bileşenleri kur (panel → Yazılım)
Panelden kur, sırayla doğrula:
- [ ] **PHP** (gerekli sürüm) → kurulunca FastCGI otomatik kaydolur.
- [ ] **MariaDB** → kurulumda root parolası otomatik atanır (K2). Panel DB işlemleri çalışıyor mu (bkz. §5.6).
- [ ] **phpMyAdmin / Adminer** → loopback'e bağlı, panel proxy + auth arkasında.
- [ ] **Stalwart (mail)** → 25/587/143 portları (bkz. §6).
- [ ] **Redis** (opsiyonel) + PHP'de **"Redis uzantısını etkinleştir"** (PHP ekranı butonu) — uygulaman Redis cache/session kullanacaksa.

## 5. Doğrulama checklist (canlı)
- [ ] **5.1 Site oluştur** → bir test domaini için site aç; `http://<domain>` IIS'te açılıyor mu.
- [ ] **5.2 SSL** → SSL bölümünden Let's Encrypt sertifikası al; HTTPS açılıyor mu; geçerlilik etiketi "X gün kaldı" görünüyor mu.
- [ ] **5.3 Document Root (Laravel)** → Site → Barındırma → "Belge kökü" alanına `...\public` yaz veya "\public kullan"; kök 403'ü gidiyor mu.
- [ ] **5.4 phpMyAdmin (B3)** → Veritabanları'ndan phpMyAdmin proxy açılıyor mu; **dışarıdan** `http://<IP>:8896` KAPALI mı (tarayıcıdan dene → açılmamalı).
- [ ] **5.5 DB içe aktar** → Veritabanları → "Mevcut DB'yi içe aktar" ile elle/harici bir DB'yi panele tanıt → phpMyAdmin'de o kullanıcıyla açılıyor mu.
- [ ] **5.6 K2 root** → Veritabanları yeni DB+kullanıcı oluştur → bağlanıyor mu (panel root parolasını biliyor demektir). `mysql -u root` **parolasız bağlanmamalı**.
- [ ] **5.7 Webmail (#4)** → Mail → posta kutusu yanında **Webmail** tek-tık → Roundcube açılıyor mu. Açılmazsa: PHP `pdo_sqlite/intl/openssl/mbstring` etkin mi, 8899 portu, SQLite auto-init (loglara bak).
- [ ] **5.8 Göç keşfi (#5)** → Göç → Plesk host + API anahtarı → "keşfet" → domain listesi geliyor mu. (Gelmezse manuel form yine çalışır.)
- [ ] **5.9 Kota (M1)** → Ayarlar → Kullanıcılar → servis planı oluştur → Customer kullanıcı aç, plan ata → o kullanıcıyla planın MaxSites'ından fazla site açmayı dene → **403 reddedilmeli**.

## 6. Mail DNS + teşhis (#2)
1. [ ] Mail → domaini ekle. Panel **DNS kayıtlarını** (MX / SPF / DKIM / DMARC / A) üretir.
2. [ ] Bu kayıtları domainin DNS'ine ekle (NS bu sunucuysa panel zone'a uygulayabilir; harici sağlayıcıysa elle ekle).
3. [ ] Mail → **"Kontrol"** (teşhis) → MX/A/SPF/DKIM/DMARC/PTR hepsi **yeşil** olmalı. Kırmızı olanı panel söyler:
   - PTR kırmızı → sağlayıcıdan `<IP> → mail.<domain>` ters DNS ayarı iste.
   - DKIM kırmızı → `selector._domainkey.<domain>` TXT eksik; panelin verdiği değeri ekle.
4. [ ] Gerçek gönderim testi: bir hesaptan **Gmail'e** mail at → Gmail "show original" → **SPF/DKIM/DMARC = PASS** mı.
   (Açık-röle: Stalwart varsayılan kapalı-röledir; 587 auth ister, 25 yalnız yerel domainlere kabul eder.)

## 7. Yedek + otomasyon (M2)
- [ ] Otomasyon → **otomatik yedek** aç: aralık (saat) + retention (gün) + kapsam (Siteler / Veritabanları / Mail / **Panel yapılandırması**).
- [ ] (Önerilen) **Off-site hedef**: FTP / S3 / WebDAV + kimlik → yedekler sunucu dışına da gitsin.
- [ ] Otomasyon → **"Şimdi yedekle"** → Yedek listesinde kayıt oluşuyor mu; off-site ise `created_remote_synced` mı.
- [ ] Yedek → **"Panel"** türü → panel config zip'i (adapanel.db + config) üretiliyor mu. **Bunu sunucu dışında sakla** (felaket kurtarma).
- [ ] Restore testi: bir test sitesinin yedeğinden **granüler geri yükleme** (zip içeriğine gözat → dosya seç → geri yükle) çalışıyor mu.
- [ ] Otomatik SSL yenileme açık mı (Otomasyon) + uyarılar (disk/sertifika eşiği → e-posta/webhook).

## 8. Sorun giderme
- **Panel açılmıyor**: AdaPanel servisi durumu + `C:\ProgramData\AdaPanel\logs\adapanel-<tarih>.log`. Migration/ACL hatası → log net söyler.
- **Site 500.19**: pretty-URL için URL Rewrite modülü gerekir; yoksa Site → "Pretty URL yönlendirmesi" net uyarı verir.
- **PHP "memory exhausted" / yazma hatası**: PHP ayarlarından memory_limit; Site → "Yazma izinlerini onar".
- **Mail gelmiyor/gitmiyor**: önce Mail "Kontrol" (port 25/587/143 dinleniyor mu + DNS yeşil mi).
- **Felaket kurtarma**: panel config yedeğini (adapanel.db + config) AYNI makineye geri koy. Not: DataProtection anahtarları (keys/) ve
  sertifikalar yedeğe DAHİL DEĞİL (makineye bağlı) — farklı makinede kurtarmada panel yeniden setup ister.

---
_Bu runbook kaynak koddan üretildi; üründe gördüğün davranışla çelişen bir şey olursa ürün esastır — geri bildir, güncelleyelim._
