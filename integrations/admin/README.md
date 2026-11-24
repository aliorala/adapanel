# AdaPanel özel yönetim ekranı

Adres: https://adapanel.host/admin/ — kullanıcı adı `admin`.
İlk giriş bilgisi yalnız yerel `C:\Users\aLi-Pc\.codex\private\adapanel-admin-giris.txt`
dosyasına yazıldı. Şifre ekrandaki **Şifre değiştir** bölümünden değiştirilebilir.
Bu dosya sonraki değişikliklerde otomatik güncellenmez. Eski erişim anahtarı
artık geçerli değildir. Eski yönetim adresleri `/admin/` adresine yönlenir.

## Güvenlik ve saklama

Kurulum tablosunda kimlik özeti, sürüm, son bildirim ve aktiflik gösterilir. Günlük sürüm bildirimi otomatik çalışır; yalnız ayrıntı paylaşımı kapatılabilir. Ayrıntıların kapatılması günlük sürüm bildirimini veya sayımı durdurmaz. Ayrıntı paylaşımı açık kayıtlarda IP ve ipwho.is sorgusu başarılıysa ülke/şehir gösterilir; eksik konum Belirlenemedi, kapalı paylaşımda eski veri yoksa Paylaşılmadı olur. Ayrıntı kapalı rapor aynı kimlikle sürüm/aktifliği günceller; önceki gerçek IP/konum/beyanlar son alındığı tarih ile geçmiş kayıt olarak korunur. Ülke/şehir ayrıca kaynak IP ve sorgu tarihini gösterir; başarısız yeni sorgu eski başarılı sonucu boşaltmaz.

- `App_Data/installation-stats/admin-auth.json`: bcrypt (cost 12) şifre özeti,
  oturum nesli ve süreli deneme sayaçları. Düz şifre saklanmaz.
- `App_Data/installation-stats/sessions/`: PHP oturumları. Web üzerinden erişilemez.
- Secure, HttpOnly, SameSite=Strict, yalnız `/admin` yolunda çalışan oturum çerezi.
- Girişte ve şifre değişiminde oturum kimliği yenilenir. Şifre değişimi diğer
  oturumları sonraki isteklerinde geçersiz kılar. Çıkış sunucudaki oturumu siler.
- 30 dakika hareketsizlik ve en çok 8 saat oturum ömrü.
- POST işlemlerinde sabit HTTPS Origin ve oturuma özel CSRF belirteci kontrolü.
- IP başına 10 dakikada 8 başarısız şifre denemesi; tüm IP'ler için aynı süre
  içinde 100 deneme sınırı. Yeni tarayıcı oturumu açmak sınırı sıfırlamaz.
- Yeni şifre en az 6 karakter, en fazla 72 UTF-8 bayt; eski şifre doğrulaması ve tekrar
  alanı gerekir. Mevcut şifre tekrar seçilemez.
- Koddan/yanıtlardan özel kayıt, parola, dosya yolu veya hata ayrıntısı sızdırılmaz.

Oturum ayarları [PHP güvenli oturum rehberi](https://www.php.net/manual/en/session.security.ini.php)
esas alınarak uygulanmıştır. Yönetim sayfaları noindex/no-store ve frame engeli taşır.

## Güncelleme

Yalnız `integrations/admin/` içindeki çalışma dosyalarını `/admin/` klasörüne
yayımlayın; README ve testleri yüklemeyin. Mevcut `admin-auth.json`, sessions,
collector config ve registry dosyalarını koruyun. İlk kurulum betiğini yeniden
çalıştırmayın. Şifre unutulursa hosting yöneticisinin web dışı auth deposunda
kontrollü sıfırlama yapması gerekir; halka açık sıfırlama adresi yoktur.

## Doğrulama

`python integrations/admin/test-http.py`

Test gerçek PHP HTTP uçlarını geçici, ayrı dosyalarla çalıştırır. Şifre değişimi,
eski şifre/oturum reddi, CSRF, Origin, çerez özellikleri, hız sınırı, çıkış ve
bozuk depoda erişimin kapanması doğrulanır. Üretim şifresini değiştirmez ve
üretime test kurulum kaydı eklemez.

21 Ekim 2026 kullanıcı talebiyle istatistikler tamamen özel hale getirildi.
Ana sayfalar sayaç istemez; eski genel summary.php ve admin-api.php HTTP 410 verir.
Günlük heartbeat.php adresi değişmedi. Mevcut registry ve collector config
dosyaları yayında birebir korundu.
