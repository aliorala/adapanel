# Harici TLS Nginx için yenileme betiği

AdaPanel'in yönettiği Nginx varsayılan olarak `127.0.0.1:8088` üzerinde HTTP
ters vekilidir; web sertifikaları IIS HTTPS bağlarında kullanılır. Bu normal
kurulumda Nginx yenileme betiğini etkinleştirmek gerekmez.

Bu betik, yöneticinin ayrıca TLS sonlandıran bir Windows Nginx kurduğu ve
mevcut win-acme yenileme kaydının Nginx'in kullandığı PEM dosyalarını zaten
güncellediği kurulumlar içindir. Sertifika/anahtar aktarmaz, yenileme kaydı
oluşturmaz, servis hesabını değiştirmez ve Nginx'i yeniden başlatmaz.

Kurucu betiği `{app}\scripts\win-acme-nginx-reload.ps1` konumuna yerleştirir.
İlgili harici TLS yenileme kaydının **script installation** eklentisinde:

- **Betik yolu:** `{app}\scripts\win-acme-nginx-reload.ps1` yerine gerçek
  kurulum yolunu seçin; örneğin
  `C:\Program Files\AdaPanel\scripts\win-acme-nginx-reload.ps1`.
- **Parametreler:** aşağıdaki yolları kendi Nginx kurulumunuza uyarlayın.

```text
-ExecutablePath "C:\nginx\nginx.exe" -Prefix "C:\nginx" -ConfigurationPath "C:\nginx\conf\nginx.conf" -PidPath "C:\nginx\logs\nginx.pid"
```

Eklentiye doğrudan `.ps1` dosyasını verin. Betiği PEM dosyaları yazıldıktan
sonra çalıştırın; mevcut IIS ve diğer installation eklentilerini koruyun.
Yollar açık, yerel ve mutlak olmalıdır. `PidPath` etkin `pid` yönergesiyle
eşleşmelidir; yönerge yoksa standart Windows Nginx'in
`<prefix>/logs/nginx.pid` yolu kullanılır. PID yolunda ileri eğik çizgi kullanın;
PID değerindeki kaçışlar veya değişkenler belirsizlik nedeniyle reddedilir.
Diğer yönergelerdeki regex ve alıntı kaçışları korunur.

Betik dosyasını ve üst dizinlerini yalnız Administrators/SYSTEM yazabilmelidir;
dosya sahibi de bu iki kimlikten biri olmalıdır. Görev hesabına yalnız
okuma/çalıştırma hakkı yeterlidir. Betik kendi dosyasının sahipliğini ve yazma
izinlerini denetler; üst dizinlerin korunmasını yönetici sağlar.

Nginx master süreci ile win-acme görevi **aynı hesap SID'siyle** çalışmalıdır.
SYSTEM görevi başka kullanıcının Nginx sürecine reload göndermez; hesaplar
uyuşmuyorsa yönetici amaçlanan servis hesabını ve erişim haklarını düzenler.

Betik master PID, executable yolu ve hesap SID'sini doğrular; `nginx -t -T`
sonrasında PID ve süreç başlangıç zamanını tekrar kontrol edip `-s reload`
gönderir. Yapılandırma dökümü loglanmaz. Belirsiz PID, kimlik uyuşmazlığı,
komut hatası veya 30 saniyelik zaman aşımı sıfırdan farklı çıkış verir.
Başarı yalnız yapılandırma testi ve graceful reload sinyalinin tamamlandığını
bildirir; sunulan yeni sertifika ayrıca TLS uç noktasında doğrulanmalıdır.
