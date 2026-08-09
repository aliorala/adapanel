# Lisans ve aktivasyon

Bu belge iki kişiye hitap eder:

* **Panelin sahibine** — lisansı nasıl açıp kapatacağını, geçersiz lisansta ne olacağını anlatır.
* **Lisans sunucusunu yazacak geliştiriciye** — panelin gönderdiği ve beklediği tam sözleşmeyi verir.

---

## 1. Varsayılan: lisanslama KAPALI

`Panel:License:VerificationUrl` boş olduğu sürece:

* Panel **hiçbir yere istek atmaz**.
* Hiçbir işlem kısıtlanmaz.
* Lisans ekranı "yapılandırılmamış" der.

Bu bilinçli bir karardır: paneli kendi sunucusuna kuran biri, biz bir lisans sunucusu yayına almadan
önce hiçbir kısıtla ve hiçbir dış bağlantıyla karşılaşmamalıdır.

## 2. Yapılandırma

`C:\ProgramData\AdaPanel\config\runtime-settings.json` dosyasına (yoksa oluşturun):

```json
{
  "Panel": {
    "License": {
      "VerificationUrl": "https://lisans.ornek.com/api/verify",
      "ProductCode": "adapanel",
      "OfflineGraceDays": 14,
      "VerifyIntervalHours": 12,
      "TimeoutSeconds": 10
    }
  }
}
```

| Ayar | Varsayılan | Anlamı |
|---|---|---|
| `VerificationUrl` | *(boş)* | Doğrulama ucunun tam adresi (POST). **Boş = lisanslama kapalı.** |
| `ProductCode` | `adapanel` | Aynı lisans sunucusu birden çok ürüne hizmet verebilsin diye |
| `OfflineGraceDays` | `14` | Sunucuya ulaşılamadığında kısıtsız çalışılacak gün sayısı |
| `VerifyIntervalHours` | `12` | Başarılı doğrulamanın "taze" sayıldığı süre |
| `TimeoutSeconds` | `10` | Tek doğrulama isteğinin zaman aşımı |

`runtime-settings.json` `reloadOnChange: true` ile okunur (`Program.cs`), yani dosya değişince
ayar yeniden yüklenir; yine de servisi yeniden başlatmak en garantili yoldur.

## 3. Panel uçları

| Uç | Yetki | Ne yapar |
|---|---|---|
| `GET /api/v1/license` | Administrator | Güncel durum. Doğrulama aralığı dolduysa arka planda yeniden dener; sunucuya ulaşılamazsa yine **başarılı** döner |
| `POST /api/v1/license/activate` | Administrator | `{ "licenseKey": "..." }` — anahtarı şifreli saklar ve hemen doğrular |
| `POST /api/v1/license/refresh` | Administrator | Aralığı beklemeden sunucuya sorar |
| `DELETE /api/v1/license` | Administrator | Saklı anahtarı siler (makine değiştirme / iade) |

Yanıt gövdesi panelin standart zarfıdır: `{ success, data, message, code, details }`.
`data` alanı `LicenseStatusDto`'dur:

```json
{
  "state": "Active",
  "messageKey": "license.active",
  "isConfigured": true,
  "restrictsNewResources": false,
  "hasKey": true,
  "keySuffix": "9F2A",
  "sku": "pro",
  "maxSubscriptions": 50,
  "expiresAtUtc": "2027-01-01T00:00:00Z",
  "lastVerifiedAtUtc": "2026-08-09T07:12:00Z",
  "offlineGraceEndsAtUtc": "2026-08-23T07:12:00Z",
  "fingerprint": "9c1f…64 karakter…",
  "fingerprintSource": "machineguid+installid",
  "lastError": ""
}
```

Lisans anahtarının **kendisi hiçbir yanıtta dönmez** — yalnız son dört karakteri (`keySuffix`).
Denetim kaydına da yalnız son dört karakter yazılır.

## 4. Doğrulama sözleşmesi (lisans sunucusunu yazacak kişi için)

### İstek

```
POST <VerificationUrl>
Content-Type: application/json
```

```json
{
  "product": "adapanel",
  "licenseKey": "ADA-XXXX-XXXX-XXXX",
  "fingerprint": "9c1f… (SHA-256, 64 karakter onaltılık)",
  "panelVersion": "0.4.22"
}
```

**Gönderilen alanlar bunlardan ibarettir.** Site listesi, alan adı, müşteri sayısı, e-posta, IP
adresi, donanım envanteri **gönderilmez**. Parmak izi ham makine kimliğinin kendisi değil, geri
döndürülemez özetidir (bu, `MachineFingerprintTests` ile teste bağlanmıştır).

Alan eklemek isteyen: önce **bu belgeyi** güncellesin. Belgeyle kod arasındaki fark, gizlilik
vaadinin sessizce bozulması demektir.

### Yanıt

```
HTTP 200
Content-Type: application/json
```

```json
{
  "status": "valid",
  "sku": "pro",
  "maxSubscriptions": 50,
  "expiresAtUtc": "2027-01-01T00:00:00Z",
  "message": ""
}
```

| Alan | Zorunlu | Anlamı |
|---|---|---|
| `status` | evet | `valid` \| `invalid` \| `expired` |
| `sku` | hayır | Ürün kademesi; panelde yalnız gösterilir |
| `maxSubscriptions` | hayır | En fazla abonelik. **0 ya da alan yok = sınırsız** |
| `expiresAtUtc` | hayır | Bitiş tarihi (UTC). `null` = süresiz |
| `message` | hayır | Serbest metin; panelde teşhis alanında görünür |

### Sunucu yazarken kaçınılması gereken tuzaklar

**Yalnızca `HTTP 200` + geçerli gövde bir KARARDIR.** Bunun dışındaki her şey — ağ hatası, zaman
aşımı, TLS hatası, `4xx`, `5xx`, bozuk JSON, **tanınmayan `status` değeri** — panel tarafından
"ulaşılamadı" sayılır ve çevrimdışı toleransa düşülür.

Bu kasıtlıdır:

* Yanlış yola dağıtılmış bir sunucu `404` döndürdüğünde bu **"geçersiz lisans" sayılmaz**. Aksi
  hâlde sizin bir dağıtım hatanız bütün müşterilerin panelini kısıtlardı.
* `status` alanına ileride yeni bir değer eklerseniz **eski paneller müşteriyi kilitlemez**;
  toleransa düşer.

Bir lisansı gerçekten iptal etmek istiyorsanız `200` + `{"status":"invalid"}` dönmelisiniz.

### Aynı anahtar birden çok makinede

Panel bunu **denetlemez** — koltuk sayımı sunucunun işidir. Sunucu, `licenseKey` + `fingerprint`
çiftlerini kendi tarafında tutup ikinci bir parmak izi için `invalid` dönebilir.

## 5. Makine parmak izi

Parmak izi = `SHA-256("adapanel-fingerprint-v1|machine=<MachineGuid>|install=<kurulum-kimliği>")`,
64 karakter onaltılık.

### Neden bu iki girdi

* **Windows MachineGuid** (`HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid`) — Windows kurulumu
  sırasında bir kez üretilir. Yeniden başlatma, donanım değişikliği, NIC ekleme/çıkarma, sürücü
  güncellemesi, alan adına katılma ve makine adı değişikliği onu **değiştirmez**. Yalnız işletim
  sistemi yeniden kurulunca (ya da sysprep ile) değişir — bu zaten "yeni kurulum"dur.
* **Kurulum kimliği** — panelin bir kez ürettiği ve `config\license\install-id` dosyasında
  sakladığı değer. Sysprep'lenmemiş bir görüntüden çoğaltılan sunucularda MachineGuid aynı
  kalabilir; bu değer iki kurulumu ayırır.

### Neden başka şeyler KULLANILMIYOR

* **MAC adresi:** bir barındırma sunucusunda ağ arayüzü kümesi sürekli değişir. Panelin **kendisi**
  Docker Engine kurunca sanal anahtar/NAT arayüzleri doğar; Hyper-V rolü sanal anahtar ekler; VPN
  istemcisi TAP arayüzü ekler; NIC teaming arayüzleri birleştirir. Enumerasyon sırası da garanti
  değildir. MAC'e dayanan parmak izi bunların herhangi birinde değişir ve müşterinin lisansı
  "başka makineye taşınmış" görünürdü.
* **Disk seri numarası:** disk değişimi ve sanal disk taşımada değişir.
* **SMBIOS/BIOS değerleri:** bu geliştirme makinesinde ölçüldü —
  `Win32_BIOS.SerialNumber` = `"To Be Filled By O.E.M"` (aynı modeldeki **bütün** makinelerde aynı),
  `Win32_ComputerSystemProduct.UUID` = `1273EE80-F197-3B91-0000-000000000000` (yarısı
  doldurulmamış). Üstelik sanal makine bir konaktan diğerine taşınınca SMBIOS UUID değişebilir.
  Girdiye eklenseydi parmak izi canlı göç sırasında sessizce kayardı.

Girdi sayısını azaltmak burada doğrudan kararlılık demektir.

### Parmak izi ne zaman değişir?

| Olay | Değişir mi? |
|---|---|
| Sunucuyu yeniden başlatma | Hayır |
| Panel güncellemesi | Hayır |
| Donanım / NIC / disk değişimi | Hayır |
| Docker, Hyper-V, VPN kurulumu | Hayır |
| Makine adı / alan adı değişikliği | Hayır |
| `C:\ProgramData\AdaPanel` silinip panelin sıfırdan kurulması | **Evet** (kurulum kimliği yeniden üretilir) |
| Windows'un yeniden kurulması / sysprep | **Evet** |

Parmak izini panelden okumak için: `GET /api/v1/license` → `data.fingerprint`.

## 6. Lisans geçersizken ne olur?

**Mevcut hiçbir şey durmaz.** Siteler yayında kalır, posta gelir gider, veritabanları çalışır,
yedek alınıp geri yüklenebilir, dosyalara erişilir, müşteriler panele girebilir.

Kısıtlanan **tek** şey **yeni kaynak açmaktır**: yeni abonelik, yeni site, yeni veritabanı, yeni
posta kutusu. Kısıtlanan istek `HTTP 402` ve nedenini söyleyen bir mesaj anahtarı döner.

Bu bir ürün kararıdır ve **teste bağlanmıştır**: `LicenseEnforcementTests` içindeki
`Lisans_kodu_site_durdurma_veya_askiya_alma_YAPMAZ` testi, lisans kaynak klasöründe site
durdurma/askıya alma/silme çağrısı geçmediğini tarar. Biri gelecekte "geçersiz lisansta siteleri
durdur" eklemek isterse test kırılır.

## 7. Durumlar

| Durum | Ne zaman | Yeni kaynak |
|---|---|---|
| `NotConfigured` | `VerificationUrl` boş | **Serbest** |
| `Active` | Sunucu "geçerli" dedi ve doğrulama taze | **Serbest** |
| `Grace` | Sunucuya ulaşılamıyor ama tolerans dolmadı | **Serbest** |
| `GraceExpired` | Tolerans doldu (varsayılan 14 gün) | Kısıtlı |
| `Expired` | Sunucu "süresi dolmuş" dedi ya da bilinen bitiş tarihi geçti | Kısıtlı |
| `Invalid` | Sunucu anahtarı reddetti | Kısıtlı |
| `Unlicensed` | Lisanslama açık ama anahtar girilmemiş | Kısıtlı |

Tolerans sayacı **son başarılı temastan** başlar; hiç temas olmadıysa **anahtarın panele girildiği
andan**. Yani kurulum anında ağ yoksa müşteri lisansını girip 14 gün çalışmaya devam edebilir.

Ağ hatası **bir karar değildir**: önceki "geçerli" kararı ulaşılamama yüzünden asla "geçersiz"e
dönmez.

## 8. Anahtar nasıl saklanır?

`config\license\state.json` içinde, **şifreli**. Şifreleme projedeki kimlik kasası kalıbıyla
aynıdır (`DatabaseCredentialStore` ile birebir): `ISecretProtector` → ASP.NET Core DataProtection
→ anahtarlar `C:\ProgramData\AdaPanel\keys` klasöründe **DPAPI (LocalMachine)** ile korunur.

Sonuçları:

* Durum dosyasını kopyalayan biri anahtarı **okuyamaz**.
* `keys` klasörü silinirse anahtar **çözülemez**; panel bunu bir hata olarak değil "anahtar yok"
  olarak yorumlar ve lisans ekranı açılmaya devam eder — kullanıcı anahtarı yeniden girer.
* Durum dosyasını **başka bir makineye kopyalamak işe yaramaz** (DPAPI makineye bağlıdır).

Bu davranış `Anahtar_diske_DUZ_METIN_yazilmaz` testiyle doğrulanır.

## 9. Panele bağlama (geliştirici notu)

Lisans servisi bu sürümde **DI'a kayıtlı değildir**; kayıt satırları ve kaynak oluşturma
uçlarına takılacak kapı çağrıları ayrı bir teslim notunda tutulmaktadır
(`scratchpad/lisans-baglanti.md`). Bağlama yapılmadan `POST /api/v1/license/activate` çalışmaz.
