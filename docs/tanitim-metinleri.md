# Tanıtım metinleri — kopyala/yapıştır

**Kullanım:** aşağıdaki metinleri kendi hesabınla gönder. Ben senin adına forum/topluluk
hesabı açamam ve oralara gönderi atamam — kimlik bilgilerin bende yok ve bu tür gönderiler
geri alınması zor, dışarıya açık işler.

**Doğru açı — özellik listesi DEĞİL, tek bir somut şey:**
> *"Windows/IIS'te `.htaccess` çalıştıran bir panel yazdık. Plesk for Windows bunu yapmıyor."*

Bu, o toplulukların gerçekten ilgilendiği cinsten bir cümle. Uzun özellik listesiyle
başlamak, aynı gönderiyi reklam gibi gösterir.

**Dürüstlük sınırı (bunu bozma):** `.htaccess` desteği canlı bir sunucuda doğrulandı
(24.08.2026, gerçek IIS + Laravel). Diğer birçok özellik yalnız test düzeyinde doğrulandı;
metinlerde onlar için "çalışıyor" iddiası yok, bilerek. Sorulursa dürüstçe söyle — bu tür
topluluklarda güven, abartıdan çok daha değerli.

**İndirme adresi:** https://github.com/aliorala/adapanel/releases

---

## 1. AlternativeTo — Plesk / cPanel alternatifi kaydı

En yüksek getirili yer: insanlar oraya zaten "Plesk alternatifi" diye giriyor.
`alternativeto.net` → hesap aç → "Submit App".

**Name:** AdaPanel
**Tagline:** Hosting control panel for Windows Server + IIS
**Platform:** Windows / Self-Hosted
**Alternative to:** Plesk, cPanel, CyberPanel, ISPConfig
**Licence:** (ücretsiz mi, ticari mi — sen karar ver)

**Description:**
> AdaPanel is a hosting control panel built specifically for Windows Server + IIS.
> One installer sets up IIS, PHP, mail, database, firewall rules and HTTPS, then gives you a
> single interface for websites, e-mail, DNS, SSL, FTP, databases, backups and Docker — with
> Administrator / Reseller / Customer multi-tenancy.
>
> Unlike Plesk for Windows, AdaPanel runs `.htaccess` files on IIS: WordPress permalinks,
> Laravel/Symfony front controllers, HTTPS forcing and directory protection work without
> hand-writing IIS rules. The engine is built in — no paid third-party module.
>
> Interface is available in English and Turkish.

---

## 2. Reddit — r/sysadmin, r/webhosting, r/dotnet

**Kurallar önemli:** çoğu subreddit'te doğrudan ürün tanıtımı yasak ya da belirli günlere
kısıtlı. Önce o subreddit'in kurallarını OKU. `r/webhosting` genelde daha toleranslı;
`r/sysadmin` "Thickheaded Thursday" / kendi tanıtım kuralları var.

**Başlık:**
> I built a hosting control panel for Windows/IIS that actually runs .htaccess

**Gövde:**
> I've been building a hosting control panel for Windows Server + IIS, mostly because the
> options there are thin compared to Linux.
>
> The thing I want feedback on: Plesk for Windows can't run `.htaccess` — there's no Apache,
> so it tells you to hand-write IIS rewrite rules. I wrote an engine that reads `.htaccess`
> at request time and applies it on IIS. WordPress permalinks and Laravel front controllers
> work without touching web.config.
>
> Design notes, since this is the part that could go badly wrong: `.htaccess` is written by
> the customer, so every regex has a hard timeout and every file has a rule/time budget — a
> pathological pattern can't pin the CPU and take the server down. The module fails open: if
> anything at all goes wrong the request passes through untouched. It ships disabled by default,
> there's an observe-only mode, and one server-wide switch kills it instantly without touching
> individual sites.
>
> It's also multi-tenant (admin/reseller/customer), does mail (Stalwart), DNS, SSL via
> Let's Encrypt, backups to FTP/S3/WebDAV/Dropbox/OneDrive/Azure, Docker and a WHMCS module.
>
> Honest status: the `.htaccess` engine is verified working on a live server. A lot of the rest
> is covered by tests but hasn't had wide production exposure yet — I'd rather say that than
> oversell it. Free to download, feedback welcome:
> https://github.com/aliorala/adapanel/releases

---

## 3. WebHostingTalk — hosting sektörünün forumu

Bölüm: *Web Hosting Software / Control Panels*. Burada okuyucular sektör profesyonelleri;
teknik ayrıntıyı severler, pazarlama dilinden hoşlanmazlar.

**Başlık:**
> [Free] AdaPanel — Windows/IIS control panel with working .htaccess support

**Gövde:** Reddit metnini kullan, ama şunu ekle:
> If you host Windows and you've ever had to convert a customer's `.htaccess` into IIS rewrite
> rules by hand, that's the problem this solves. The panel shows you which directives are
> supported, which aren't and why — it doesn't silently ignore lines.

---

## 4. Hacker News — Show HN

**Başlık:**
> Show HN: A Windows/IIS hosting panel that runs .htaccess

Gövdeyi kısa tut (HN uzun tanıtımı sevmez). Reddit metninin ilk üç paragrafı yeterli.
İlk yorumda teknik ayrıntıyı sen ver: neden fail-open, neden regex zaman aşımı, neden
varsayılan kapalı. HN'de değer verilen şey tam olarak bu tür kararların gerekçesi.

---

## 5. Product Hunt

Görsel gerektirir — panelden 4-5 ekran görüntüsü (siteler, `.htaccess` kartı, yedekleme,
posta). Ekran görüntüleri olmadan gönderme, boşa harcanmış olur.

**Tagline:** Windows hosting, from one panel
**Description:** AlternativeTo metnini kullan.

---

## 6. Türkçe kanallar

- **Webmaster forumları** (webmaster.org, wmaraci) — "Windows sunucuda Plesk yerine" açısı
- **Donanım Haber / Technopat** sunucu bölümleri
- **LinkedIn** — kendi hesabından, teknik anlatım olarak (reklam değil)

**Türkçe metin:**
> Windows Server + IIS için bir barındırma kontrol paneli yazdım.
>
> Çıkış noktam şuydu: Plesk'in Windows sürümünde `.htaccess` çalışmıyor, çünkü Apache yok —
> Plesk size IIS kurallarını elle yazmanızı söylüyor. AdaPanel `.htaccess` dosyalarını IIS
> üzerinde çalıştırıyor: WordPress kalıcı bağlantıları, Laravel ön denetleyicisi, HTTPS
> zorlama olduğu gibi çalışıyor.
>
> Riskli tarafını da anlatayım, çünkü asıl iş orada: `.htaccess`'i müşteri yazıyor. Kötü bir
> düzenli ifade tek istekte CPU'yu kilitleyip sunucudaki bütün siteleri düşürebilir. Bu yüzden
> her desenin zaman aşımı, her dosyanın kural ve süre bütçesi var. Modül hata durumunda isteği
> bozmuyor, olduğu gibi geçiriyor. Varsayılan kapalı geliyor, "yalnız gözlem" kipi var ve
> sunucu genelinde tek anahtarla anında kapatılabiliyor.
>
> Ayrıca: çok kiracılı (yönetici/bayi/müşteri), posta, DNS, Let's Encrypt, yedekleme
> (FTP/S3/WebDAV/Dropbox/OneDrive/Azure), Docker ve WHMCS modülü var. Arayüz Türkçe ve İngilizce.
>
> Dürüst durum: `.htaccess` motoru canlı sunucuda doğrulandı. Geri kalanın büyük kısmı testlerle
> kapsanıyor ama henüz geniş üretim kullanımı görmedi — abartmak yerine böyle söylemeyi tercih
> ediyorum. Ücretsiz indirilebilir: https://github.com/aliorala/adapanel/releases

---

## Gönderim sırası önerisi

1. **AlternativeTo** — kalıcı, arama trafiği getirir, tanıtım kuralı yok
2. **WebHostingTalk** — hedef kitle birebir
3. **r/webhosting** — kurallarını okuduktan sonra
4. **Show HN** — bir kez gönderilir, iyi bir güne denk getir (hafta içi sabah ABD saati)
5. **Product Hunt** — ekran görüntüleri hazır olunca
6. Türkçe kanallar — istediğin zaman

## Gönderdikten sonra

Gelen ilk geri bildirim, bu oturumda yazdığımız her şeyden değerli olacak. Özellikle
`.htaccess` motorunda desteklenmeyen direktif bildirimleri gelirse, panel zaten hangi satırın
neden çalışmadığını söylüyor — o raporları bana getir, kapsamı ona göre genişletiriz.
