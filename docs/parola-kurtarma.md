# Yönetici parolasını sunucudan belirleme

> **Panele giremiyorsanız `data\adapanel.db` dosyasını SİLMEYİN.** O dosya panelin bütün
> kullanıcılarını, ayarlarını ve kurulu bileşen kayıtlarını taşır. Parolayı sıfırlamanın kayıpsız
> yolu aşağıdadır.

Bu belgedeki her adım kaynak koddan doğrulanmıştır
(`src/AdaPanel.Api/Recovery/ParolaBelirlemeKomutu.cs`,
`src/AdaPanel.Infrastructure/Security/ConsolePasswordRecovery.cs`).

---

## Yordam

1. Sunucuda oturum açın (konsol ya da uzak masaüstü).
2. Komut istemini / PowerShell'i **"Yönetici olarak çalıştır"** ile açın.
3. Şunu çalıştırın:

```powershell
& "C:\Program Files\AdaPanel\AdaPanel.Api.exe" --parola-belirle
```

Komut sırayla:

* **yönetici hesaplarını listeler** (pasif olanlar ve iki adımlı doğrulaması açık olanlar
  işaretlenir),
* **hangisi olduğunu size sordurur** — tek yönetici olsa bile numara istenir; "hangi hesabın
  parolası değişti" sorusunun yanıtı varsayıma bırakılmaz,
* **yeni parolayı iki kez sorar.** Parola ekrana yazılmaz.

Panel servisinin çalışıyor olması gerekmez; komut yalnız veritabanını açar.

### Hedefi doğrudan vermek

```powershell
& "C:\Program Files\AdaPanel\AdaPanel.Api.exe" --parola-belirle yonetici
& "C:\Program Files\AdaPanel\AdaPanel.Api.exe" --parola-belirle yonetici@ornek.test
```

Kullanıcı adı da e-posta da kabul edilir. Hedef yönetici değilse komut **açıkça reddeder**.

### İki adımlı doğrulama

Parola sıfırlamak 2FA'yı **kapatmaz**. Doğrulayıcınızı da kaybettiyseniz:

```powershell
& "C:\Program Files\AdaPanel\AdaPanel.Api.exe" --parola-belirle yonetici --2fa-kapat
```

Komut kapatmadan önce ekrana uyarı basar ve `EVET` yazmanızı ister; kapatınca **kurtarma kodları da
geçersiz kılınır**. Girişten sonra iki adımlı doğrulamayı yeniden açın (**Sol menü → Hesap**).

### İngilizce eş anlamlıları

`--set-password` ve `--disable-2fa` aynı işi yapar.

---

## Komut ne yapar, ne yapmaz

| Yapar | Yapmaz |
|---|---|
| Parolayı yazar (aynı kural: en az 12 karakter) | Parolayı komut satırından ya da dosyadan almaz |
| Giriş kilidini ve hatalı deneme sayacını temizler | IP engellerini kaldırmaz (bkz. `recover-access.flag`) |
| Kullanıcının açık oturumlarını düşürür | Başka kullanıcıların oturumlarına dokunmaz |
| Kullanıcının API anahtarlarını iptal eder | Başka kullanıcıların anahtarlarına dokunmaz |
| Denetim kaydı yazar (`auth.password_set_from_console`) | Parolayı denetim kaydına **yazmaz** |
| İstenirse iki adımlı doğrulamayı kapatır | Varsayılan olarak iki adımlı doğrulamayı **korur** |
| Pasif hesabı bildirir | Pasif hesabı kendiliğinden **etkinleştirmez** |

Denetim kaydında işlemi yapan **Windows hesabı** yazar (örn. `SUNUCU\Administrator`): panel
kullanıcısı olarak yapılmış bir işlem değildir, sunucudan yapılmış bir müdahaledir.

---

## Çıkış kodları

| Kod | Anlamı |
|---|---|
| 0 | Parola belirlendi |
| 1 | Genel hata (ör. veritabanı açılamadı) |
| 2 | Komut yönetici olarak çalıştırılmadı |
| 3 | Hedef bulunamadı / hedef yönetici değil / panelde hiç yönetici yok |
| 4 | Vazgeçildi (boş seçim, parolalar uyuşmadı, parola çok kısa) |

---

## Sık karşılaşılanlar

**"Panel veritabanı bulunamadı: ..."**
Kurulum başka bir veri kökü kullanıyor olabilir. Doğru kökü verin:

```powershell
$env:Panel__DataRoot = "D:\AdaPanelVerisi"
& "C:\Program Files\AdaPanel\AdaPanel.Api.exe" --parola-belirle
```

**"Panel veritabanı açılamadı ... database is locked"**
Panel servisi uzun bir işlemin ortasında olabilir. Servisi durdurup tekrar deneyin:

```powershell
net stop AdaPanel
& "C:\Program Files\AdaPanel\AdaPanel.Api.exe" --parola-belirle
net start AdaPanel
```

**"Bu komut YÖNETİCİ olarak çalıştırılmalı."**
Veri kökü yalnız yöneticilere açıktır; komut istemini yükseltilmiş olarak açın.

**Parolayı belirledim ama giremiyorum.**
Komut "Hesap PASİF" uyarısı verdiyse giriş, parola doğru olsa da reddedilir; başka bir yönetici
hesabıyla girip kullanıcıyı etkinleştirin. IP'niz brute-force koruması tarafından engellenmiş de
olabilir — o durumda `config\recover-access.flag` dosyasını oluşturup servisi yeniden başlatın
(bkz. [Sorun giderme](sorun-giderme.md)).

---

## Neden bu yol böyle tasarlandı

* **Ağdan erişilemez.** Panelde bu iş için bir HTTP ucu **yoktur ve olmayacaktır**: ağdan
  çağrılabilen bir eşdeğeri, kimlik doğrulamayı tamamen atlayan bir arka kapı olurdu. Komut, sunucuda
  oturum açmış olmayı ve yönetici yetkisini şart koşar.
* **Yeni bir yetki vermez.** Veri kökü zaten yalnız yöneticilere açıktır; oraya erişebilen kişi
  `adapanel.db`'yi silebiliyordu. Bu komut o yetkiyi **veri kaybetmeden** kullanılabilir kılar.
* **Yalnız yönetici hesapları.** Müşteri/bayi parolası panelden (kapsam denetimiyle) değiştirilir;
  konsol yolu o denetimi atlamak için kullanılamaz.
* **Parola argüman olarak alınmaz.** Komut satırındaki bir parola süreç listesinde, kabuk geçmişinde
  ve zamanlanmış görev tanımlarında görünür.
* **İki adımlı doğrulama varsayılan olarak korunur.** Parola sıfırlarken sessizce kapatmak, ikinci
  faktörü tam da var olma sebebinin gerçekleştiği anda (parola kayboldu/ele geçirildi) işlevsiz
  kılardı. Kapatma ayrı bir istektir ve bedeli ekranda yazılıdır.
