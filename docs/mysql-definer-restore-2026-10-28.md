# MySQL/MariaDB DEFINER geri yükleme uyumluluğu

## Kök neden ve kapsam

`rsmc_alarmm@%` ve `rsmc_alarmm@localhost` farklı veritabanı hesaplarıdır. Kaynakta
`DEFINER=rsmc_alarmm@%` olan view'ı hedefte başka bir hesapla oluşturmak MariaDB'de
`SET USER` gerektirebilir. Hata 1227 yedeğin bozuk olduğunu göstermez. Önceki tablo/veri
ifadeleri zaten uygulanmış olabilir; DDL ve bütün döküm tek bir geri alınabilir işlem değildir.

Kullanıcının bildirdiği MariaDB 11.8.9 olayı bu ayrımla uyumludur. Bu çalışma canlı
veritabanını veya kullanıcının 24.616 kayıt ölçümünü yeniden çalıştırmadı.

| Akış | Kod | Sonuç |
| --- | --- | --- |
| AdaPanel tek SQL yedeği | `DatabaseService.ExportDumpAsync`, `BackupService.BackupDatabaseAsync` | Özgün sahipler varsayılan olarak korunur; yeni taşınabilir dışa aktarım seçimi vardır. |
| Site/abonelik/zamanlanmış yedeklerin SQL içeriği | `BackupService.TryAddDatabaseDumpAsync` | Özgün sahipler korunur; otomatik yedeklerin birebir sahiplik davranışı değiştirilmedi. |
| AdaPanel tek SQL geri yükleme | `BackupService.RestoreCoreAsync` → `DatabaseService.ImportDumpAsync` | Uyarlama seçimi, gerçek CLI hatası ve kalıcı kısmi sonuç. |
| Site/abonelik ZIP geri yükleme | `RestoreSiteChainAsync` / `RestoreSubscriptionChainAsync` → `ImportDatabasesFromArchiveAsync` | Seçim son halkanın SQL dökümlerine taşınır. Diğer motorlar etkilenmez. |
| Sunucudan sunucuya taşıma | `DatabaseService.TransferDatabaseAsync` | Aynı dönüştürücü ve geri yükleme koruması; hesap/yetki adımı başarısızsa tam başarı dönmez. |
| Panelde mevcut DB'yi tanıtma | `ImportDatabaseAsync` | SQL çalıştırmaz; DEFINER seçeneği bu forma eklenmedi. |
| phpMyAdmin elle SQL içe aktarma | `WebDbController` ile açılan bağımsız phpMyAdmin | AdaPanel SQL dönüştürücüsünden geçmez; panelin geri yükleme kutusu bu işleme etki etmez. |
| Arşivden seçilen dosyaları geri alma | `RestoreItemsAsync` | Dosya çıkarır; SQL çalıştırmaz. |

## Kullanıcı seçenekleri

Geri yükleme onayında ve veritabanı taşıma formunda:

**Nesne sahiplerini (DEFINER) geri yükleyen kullanıcıya uyarla**

- Varsayılan **kapalı**: özgün sahipleri korur; eski API istemcilerinin davranışı değişmez.
- Açık: beş desteklenen CREATE nesne başlığındaki açık sahip `CURRENT_USER` yapılır.
- `SQL SECURITY DEFINER` / `INVOKER` değişmez; sahip değiştiği için DEFINER bağlamındaki
  erişim yetkileri değişebilir. Trigger/event de sahibinin yetkileriyle çalışır.
- AdaPanel şu anda SQL içe aktarımını **panelin motor yönetim hesabıyla** yapar. Bu seçenek
  phpMyAdmin'de giriş yapılan uygulama hesabı anlamına gelmez; arayüz bunu açıkça belirtir.
- Hiçbir hesaba `SET USER`, `SUPER` veya başka yeni global yetki verilmez.

Veritabanı yedekleme formunda MySQL/MariaDB için ayrı bir seçenek:

**Taşınabilir SQL dışa aktarımı (DEFINER=CURRENT_USER)**

- Varsayılan **kapalı**. Yeni dışa aktarım dönüştürülür; mevcut yedeklere dokunulmaz.
- phpMyAdmin ile farklı hesapta yüklemek için kullanılabilir. phpMyAdmin'de CURRENT_USER,
  o anda phpMyAdmin bağlantısının doğrulanan veritabanı hesabını gösterir.
- Yedek kaydında `portableDefiners=true` tutulur; geri yükleme onayında bu özellik açıklanır.
- Bu kopyada özgün sahipler bulunmaz; kutuyu sonradan kapatmak onları geri getirmez.
  Bu yüzden taşınabilir çıktı özgün sahipleriyle birebir felaket kurtarma yedeği yerine geçmez.
- Taşınabilirlik yalnız sahiplikle ilgilidir: veritabanı adları/USE, collation, sunucuya özel
  SQL özellikleri, sürüm yorumlarının yürütülme koşulları ve mevcut nesneler aynen kalır.
- Mevcut yedek sıkıştırma/şifreleme davranışı korunur: indirilen dosya `.sql.zst` veya
  `.sql.zst.enc` olabilir. phpMyAdmin'e dış sarmalayıcıyı doğrudan vermek yerine önce
  yetkili araçlarla SQL içeriği açılmalıdır; bu özellik şifreleme parolasını atlatmaz.
- phpMyAdmin'in kendi dışa aktarma ayarlarındaki `remove_definer_from_definitions` seçeneği
  ayrıca kullanılabilir; belgelenen kapsamına bakılmalı, bütün beş nesneyi kapsadığı varsayılmamalıdır.

## Dönüştürücü ve sürüm uyumluluğu

`MySqlDefinerRewriter` SQL'i karakter/token akışı olarak okur. Büyük INSERT cümleleri
ve uzun metinler belleğe topluca alınmaz; token tamponu 65.536 karakter, okuma/yazma tamponları
16 KiB düzeyindedir. Dosya boyutuyla büyüyen bir SQL dizgesi veya nesne listesi tutulmaz.

Desteklenenler: CREATE VIEW, TRIGGER, PROCEDURE, FUNCTION, EVENT; OR REPLACE / ALGORITHM
başlıkları; tırnaklı/tırnaksız hesaplar; CURRENT_USER ve CURRENT_USER(); CRLF/LF; UTF-8 ve BOM;
`;`, `;;`, `$$`, `//` gibi normal DELIMITER komutları; `/*!50013 ... */` ve `/*M!...*/` yorumları;
MariaDB'nin `/*M!999999\- enable the sandbox mode */` istemci başlığı.

Sıradan yorumlar, INSERT verileri, SQL metin değerleri, tırnaklı sütun adları ve rutin
gövdelerindeki sahte CREATE/DEFINER metinleri korunur. SQL SECURITY değiştirilmez.
Yorum sarmalayıcıları ve sürüm numaraları değiştirilmez; MariaDB/MySQL bunları kendi
sürüm kurallarına göre yürütür. Örneğin MariaDB, MySQL biçimindeki 50700–99999 arası
sürüm yorumlarını çalıştırmaz; dönüştürücü bunları zorla etkinleştirmez.

Belirsiz veya desteklenmeyen sözdizimi dönüşümde **SQL çalıştırılmadan reddedilir**.
UTF-16/bozuk UTF-8/NUL, belirsiz DELIMITER, NO_BACKSLASH_ESCAPES, ANSI_QUOTES/ANSI
(bunları açan DB2/MAXDB/MSSQL/ORACLE/POSTGRESQL kipleri dahil),
kaçışlı/dinamik/encoded SQL_MODE değerleri bu kapsamdadır. Standart mysqldump SQL_MODE
kaydetme/literal değer/geri alma düzenleri test edilir. İçe aktarma öncesinde aynı motor yönetim
hesabının başlangıç SQL_MODE değeri yalnız okunur ve desteklenmiyorsa reddedilir; sunucu ayarı
değiştirilmez. MySQL'in 50700 ve üzeri sürüm yorumları veya MariaDB'ye özgü koşullu yorumlar
içinde SQL_MODE değişimi/okuması hedefe göre çalışmayabileceğinden uyarlama modunda reddedilir.
Normal 40101/50003 kip yorumları ve 50013 DEFINER yorumları desteklenir.
Bilinen kip basitçe `@saved_sql_mode=@@sql_mode` ile saklanıp geri alındığında sonraki rutin/view
desteklenir; hesaplamalı/dinamik saklama güvenilir sayılmaz. Bilinmeyen bir moda geri dönüldükten
sonra SQL ayrıştırmaya devam etmek gerekirse işlem durur. Bu, kaynak SQL'i tahminle değiştirmekten
kaçınan bilinçli bir uyumluluk sınırıdır. Özgün sahipleri koruma modu dönüştürücü kullanmaz.

SQL_MODE takibi mysqldump'ın basit oturum kipi kaydetme/geri alma düzeni içindir; genel bir SQL
yorumlayıcısı değildir. Fonksiyon çağrılarının kullanıcı değişkenlerine dolaylı yan etkileri
modellenmez. Böyle özelleştirilmiş SQL'ler için uyarlama yerine özgün sahipleri koruma veya
ayrıca incelenmiş nesne tanımlarıyla elle taşıma kullanılmalıdır.

Sözdizimi MariaDB 11.8 ailesi ve desteklenen MySQL sürümlerindeki CURRENT_USER biçimini
kullanır; bu makinede çalıştırılabilir MariaDB/MySQL test sunucusu bulunmadığından gerçek
MariaDB 11.8.9 veya MySQL motoruyla uçtan uca doğrulama yapılmadı.

## Hata, kısmi sonuç ve tekrar deneme

- mysql batch/stdin akışı ilk hatada durur; `--force` ve istemci `source` komutu kullanılmaz.
- CLI çıkış kodu, gerçek stderr ve bulunabiliyorsa hata satırındaki nesne adı gösterilir.
  Merkezî sır maskeleme devam eder. Hata ayrıntısı 8.192 karakterle sınırlıdır.
- Tek SQL geri yükleme hatası/iptali artık kaydı `restored_with_errors` yapar. Site/abonelikte
  başarısız veritabanı uyarısı kalıcıdır; geri kalan bileşenlerin sonucu ayrı kalır.
- SQL gönderilmeden hemen önce `config/mysql-restore-attempts.json` içine kalıcı niyet yazılır.
  Hata, kesinti, zaman aşımı veya belirsiz tamamlanmada bu niyet kalır; panel yeniden başlasa da
  dump'ı yeniden oynatmaz. Çıkış kodu sıfır ve ilgili son adımlar tamamlanınca kaldırılır.
- Genel SQL dosyası USE ile farklı DB'lere geçebilir. Bu nedenle AdaPanel SQL geri yüklemesinin
  belirsiz sonucu **motor genelinde** yeni tam SQL aktarımlarını engeller; hedef adı değiştirmek
  korumayı aşamaz. Bu koruma tamamlanmış tabloları silmez, INSERT'leri tekrarlamaz.
- Panelin kendi `--databases` kullanmadan ürettiği kontrollü tek DB transferinde koruma hedef
  veritabanı kapsamındadır. SQL sonrasındaki hesap/GRANT adımı da koruma altındadır.
- Yarım yüklemede önce asıl hatayı/satırı inceleyin. Bu olayda yalnız `v_incidents_full` CREATE
  tanımını CURRENT_USER ile oluşturmak doğru kurtarma yoludur; tüm dump yeniden çalıştırılmaz.
- Kalıcı niyet otomatik zaman aşımıyla unutulmaz. Elle düzeltme sonrası devam etmeden önce
  yönetici gerçek veriyi ve bu kurtarma kaydını incelemelidir; sırf engeli kaldırmak için dosyayı
  silmek güvenli bir kurtarma değildir. Yeni/import edilmemiş boş hedef ayrı bir aktarım yolu
  olarak kullanılabilir; genel motor kilidini farklı DB adıyla aşmak mümkün değildir.
- Panelde kullanıcı açıkça yeni bir DB oluşturduğunda yalnız o **yeni oluştuğu doğrulanan**
  DB'nin dar kapsamlı eski transfer kaydı kaldırılır. Otomatik DROP DATABASE/TABLE eklenmedi.

## Değişen dosya grupları

- `src/AdaPanel.Infrastructure/Database/MySqlDefinerRewriter.cs`: akış ayrıştırıcısı ve hata nesnesi bulma.
- `MySqlRestoreExecutor.cs`, `MySqlRestoreJournal.cs`, `MySqlImportSession.cs`: ortak yürütme, başlangıç kipi kontrolü, hata ve tekrar koruması.
- `DatabaseService.cs`: dump, import ve sunucudan sunucuya taşıma entegrasyonu.
- `src/AdaPanel.Infrastructure/Backup/BackupService.cs`: tek SQL, site/abonelik ve taşınabilir kayıt entegrasyonu.
- Core DatabaseDump/Transfer, BackupDatabase/RestoreBackup/BackupRecord DTO'ları ve BackupController metadata kopyası.
- Frontend backup/database API tipleri, ortak restore onayı, ConfirmDialog, BackupView ve DbTransferView.
- Türkçe/İngilizce arayüz ve API hata katalogları.
- Ayrıştırıcı/yürütme/yedek/hesap/arayüz testleri.

## Doğrulama

Son doğrulama (28 Ekim 2026):

| Kontrol | Sonuç |
| --- | --- |
| İlgili backend testleri | **249 geçti**, 0 başarısız, 0 atlanan |
| İlgili frontend testleri (10 dosya) | **345 geçti**, 0 başarısız |
| `vue-tsc --noEmit` | Geçti |
| Vite üretim derlemesi | Geçti |
| `git diff --check` | Geçti |
| Bağımsız rues-denetci incelemesi | **ONAY**, engelleyici bulgu yok |

Backend kapsamı: MySqlDefinerRewriterTests, MySqlRestoreExecutionTests,
BackupCombinedRestoreSafetyTests, BackupDatabaseInclusionTests, DatabaseAccountLifecycleTests,
BakilmayanAdaylarDogrulamaTests, DatabaseTransferTlsTests, PostgreSqlDumpArgumentTests,
BackupGranularRestoreTests ve BackupInventoryRestoreOutcomeTests.

Frontend kapsamı: useBackupRestore, BackupRestoreTransport, BackupRestoreOutcome,
DbTransferLifecycle, BackupLifecycle, BackupScheduleTransport, BackupSelectedDownload,
DatabaseMutationContext, DatabaseWorkspaceContext ve i18n/catalog testleri.

Kontroller farklı kaynak/hedef hesaplarını, aynı hesabı ve CURRENT_USER'ı; beş nesne türünü;
çalıştırılabilir yorumları; MariaDB sandbox başlığını; sahte metin/verileri; uzun INSERT'i;
DELIMITER ve oturum kipi değişimlerini; nesne/satır/1227 hatasını; değişmeyen kaynak dosyayı;
kalıcı tekrar korumasını; SQL sonrası hesap hatasını; kısmi durum ve önceki hatanın korunmasını
doğrular. Mevcut süreç koşucusu testleri gerçek bir alt sürece stdin beslemesini ve sıfır olmayan
çıkış kodunu da ölçer. Yeni SQL yürütme testleri sahte CLI sonuçları kullanır.

Çalışan roller: rues-gelistirici (ayrıştırıcı ve testler), rues-arastirmaci (akış haritası/resmî
belgeler), rues-denetci (bağımsız kontrol); ana oturum servis/UI entegrasyonu ve testleri birleştirdi.
Canlı veritabanına bağlanılmadı, canlı yedek değiştirilmedi ve global yetki verilmedi.
Gerçek MariaDB 11.8.9/MySQL ile uçtan uca test yapılmadığı sınırı geçerlidir.

## Resmî kaynaklar

- [MariaDB CREATE VIEW](https://mariadb.com/docs/server/server-usage/views/create-view)
- [MariaDB CURRENT_USER](https://mariadb.com/docs/server/reference/sql-functions/secondary-functions/information-functions/current_user)
- [MariaDB yorum sözdizimi](https://mariadb.com/docs/server/reference/sql-statements/comment-syntax)
- [MariaDB SQL_MODE](https://mariadb.com/docs/server/server-management/variables-and-modes/sql_mode)
- [MySQL stored object security](https://dev.mysql.com/doc/refman/8.0/en/stored-objects-security.html)
- [mysqldump seçenekleri](https://dev.mysql.com/doc/refman/8.4/en/mysqldump.html)
- [phpMyAdmin dışa aktarım yapılandırması](https://docs.phpmyadmin.net/en/master/config.html#cfg_Export_remove_definer_from_definitions)
