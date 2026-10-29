# Tam dosya yedeği ve kapsam sayımı — 29 Ekim 2026

## Kök nedenler

- Otomasyon site arşivlerine giren veritabanlarını tekrar ayrı dökmüyordu;
  özet ise yalnız ayrı dökümleri sayıyordu. Gerçek kapsam olduğundan az görünüyordu.
- Açık SQLite dosyaları ve paylaşım kilitli dosyalar canlı kaynaktan normal ZIP
  okumasıyla alınamıyordu. Yalnız paylaşım bayrağını gevşetmek SQLite DB/WAL/SHM
  kümesinin aynı ana ait olmasını sağlamaz; kilit dosyalarını dışlamak tam yedek değildir.
- Artımlı indeks yalnız boyut ve mtime karşılaştırıyordu; içerik bu değerler
  değişmeden değişebilirdi.
- Log sarmalayıcısındaki panel URL çalıştırıcısı, dışarıdan alınacak betik sanılıp
  gereksiz eksik-betik uyarısı üretiyordu.
- Otomatik motor keşfi başarısızlığı yalnız loglanıp boş listeye dönüyordu;
  hazır olmayan kurulu motorlar da kapsam sonucunda açıkça görünmüyordu.

## Uygulama

Site/abonelik/panel dosyaları tek bir geçici VSS kümesinin köklerinden okunur.
Tarama, SHA-256 indeks ve ZIP girdileri aynı değişmez dosya görünümünü kullanır.
SQLite ana dosyası, WAL/SHM ve paylaşım kilitli dosyalar dışlanmaz. Dosyalar
arşivlenince lease bırakılır; ayrı SQL dökümleri ve uzak yükleme sırasında tutulmaz.

Windows Diskshadow `VOLATILE NOWRITERS` bağlamını kullanır. Bu dosya sistemi
düzeyinde çökme sonrası toparlanabilir görünüm sağlar; uygulama yazıcısı veya
ayrı mantıksal SQL dökümleriyle ortak işlem anı garantisi değildir. Windows Server,
Diskshadow desteği, uygun hizmet hesabı ve yerel desteklenen birim gerekir.
UNC ve reparse üzerinden kaynak kaçışı reddedilir. Üretim DI kaydı runtime
yoksa hata verir; snapshot başarısızken canlı kaynak üzerinden sessiz devam etmez.

Yardımcılar yalnız işlem sahibine açık rastgele dizinde oluşturulur. Ana PID ve
başlangıç zamanı izlenir; bırakma/iptal/hata yollarında sınırlı süreli temizlik
vardır. Başka uygulamanın snapshotlarını silen komut yoktur. Metadata sabit
`metadata.cab` adına yazılır ve yalnız oluşturulan dosyalar temizlenir.

Artımlı indeks SHA-256 taşır; eski hash içermeyen veya eksik indeks yeni tam
zincir başlatır. Gerçekten okunamayan içerik tam başarı sayılmaz. Otomasyon
doğrulanmış benzersiz veritabanlarını site/ayrı döküm ayrımıyla gösterir; yerelde
mevcut kopya sayılmış olsa bile uzak aktarım hatası hata olarak kalır. Kullanıcının
kapattığı kapsam veya seçmediği motor kendiliğinden açılmaz.

URL görevleri ortak scheduler çözümleyicisiyle tanınır; gerçek BAT/CMD/PowerShell
betikleri için arşivleme, hash, özgün yol ve geri yükleme kontrolleri korunur.

## Testler ve gerçek Windows doğrulaması

- Otomasyon fake grubu 81/81; frontend ayrıntı çevirileri 11/11 ve tür kontrolü geçti.
- Snapshot/indeks/kilit/abonelik/panel/encoding/cleanup dar grubu 57/57.
- Görev kapsama grubu 19/19. Bağımsız denetçi ilgili birleşik grubu 72/72 ve son
  encoding/cleanup grubunu 5/5 ayrıca çalıştırdı; ONAY verdi. Gruplar örtüşür.
- Eski sayım/keşif davranışı, ayrı-kopya sayımı, çeviri kaybı ve hash kontrolünün
  kaldırılması mutasyonları ilgili testleri kırdı; kaynaklar geri yüklendi.
- Gerçek sunucuda yalnız üretilmiş geçici test verileri kullanıldı. SQLite WAL
  açıkken üç kayıt ve FileShare.None ile kilitli bir dosya snapshotlandı. Sonradan
  canlı test veritabanına dördüncü kayıt eklendi: alınan kopyada üç kayıt kaldı,
  `PRAGMA integrity_check` sonucu `ok`, kilitli dosya baytları eşleşti.
- Aynı gerçek test own snapshotın bırakıldığını doğruladı; program çıkışı 0.
  Canlı site veritabanında geri yükleme veya yıkıcı işlem yapılmadı.
- Gerçek ilk deneme UTF-16 Diskshadow script uyumsuzluğunu yakaladı; script
  BOM/NUL içermeyen ASCII komutlara dönüştürüldü, Unicode yollar ortam üzerinden
  korundu. İkinci deneme örtük CAB temizleme sorununu yakaladı. İkisi de başarısız
  raporlandı; düzeltmelerden sonra tam test geçti. Bunlar fake test yerine
  sunucu uyumluluğunu gerçekten ölçmenin yakaladığı hatalardır.

Web dışı kanıtlar: `.tmp/vss-runtime-probe/live-result.json`,
`.tmp/vss-runtime-probe/upload.json`, `.tmp/backup-vss-tests.log`,
`.tmp/backup-task-wrapper-tests.log`, `.tmp/automation-backup-complete-tests.log`.
Probe dosyası FTPS ile doğrulanmış korumalı App_Data konumundan çalıştırıldı ve
sonrasında yalnız bu denemenin yüklediği dosya/boş klasör kaldırıldı.

## Değişen kaynaklar ve ajanlar

`BackupService.cs`, `BackupService.TaskScripts.cs`, `BackupChainPlanner.cs`,
`BackupChainDtos.cs`, yeni `IBackupSnapshotRuntime.cs` / `WindowsBackupSnapshotRuntime.cs`,
DI kaydı; `AutomationService.cs`, frontend `automationDetails` ve TR/EN özet anahtarı.
İlgili regresyon testleri eklendi/güncellendi.

İki `rues-gelistirici` dar dosya sahipliğiyle çalıştı; `rues-derin-akil` kilitli
dosya tasarımını salt okunur değerlendirdi, farklı `rues-denetci` bağımsız kontrol
yaptı. Aynı anda en fazla iki alt ajan çalıştı. Root gerçek Windows probe ve
görev sınıflandırma düzeltmesini yaptı.

## Dağıtım durumu

0.8.138 paketleme ve dağıtım hazırlığındadır. Gerçek sunucu probe'unun geçmesi,
uygulamanın yeni sürümünün kurulması veya tam üretim yedeğinin tamamlanmasıyla
eş tutulmaz. Yeni sürüm sonrası gerçek yedek/arşiv kontrolü ayrıca kaydedilecektir.

## Paket restore sırasında bulunan bağlantı kütüphanesi yaması

Yeni NuGet audit verisi MySqlConnector 2.6.0 için NU1902 bildirdi. Üreticinin
[GHSA-473q-m89c-ghf8 duyurusu](https://github.com/advisories/GHSA-473q-m89c-ghf8)
ve [2.6.1 sürüm notu](https://github.com/mysql-net/MySqlConnector/releases/tag/2.6.1)
yamanın 2.6.1 olduğunu doğruluyor. Yalnız paket referansı 2.6.1 olarak güncellendi;
TLS modu, CA, sertifika ve canlı veritabanı ayarları değiştirilmedi. Duyuru,
yerel/loopback bağlantılarını bu saldırıdan etkilenmeyen kapsamda tanımlar;
canlı sunucuda parola sızıntısı yaşandığına dair bir bulgu yoktur. Yama paketinin
dar SQL/DEFINER/transfer TLS/provider grubu 202/202 geçti. Force/no-cache restore
ve API Release derlemesi başarılı; son derlemede sıfır uyarı ve sıfır hata var.
API/Infrastructure/test assets kayıtları gerçek MySqlConnector/2.6.1 çözümlemesini
doğruluyor. Nihai kurulum paketi de bu kaynakla yeniden üretilecektir.
