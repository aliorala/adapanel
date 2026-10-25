# Gece yedekleri ve FTP saklama incelemesi — 25 Ekim 2026

> Aşağıdaki canlı bulgular ilk incelemenin kaydıdır. Kullanıcı daha sonra eski dosyaların yine de silinmesini ve güncellemenin yayımlanmasını açıkça istedi. 0.8.121 bu doğrultuda pozitif gün sınırını kesin hale getirir: tamamen süresi dolmuş son kopyalar da silme planına alınır. Önceki korumayı sürdürme önerileri bu yeni talimatla değiştirilmiştir. Yeni davranış ve yayın kanıtları `release-0.8.121.md` dosyasındadır. Bu belge canlı dosyaların silindiği anlamına gelmez.

## Canlı ortamdan doğrulananlar

- Açık `adapanel.host` RDP oturumundaki AdaPanel incelendi. Gece programı açık, saat 03:00, saklama süresi 5 gün. Son çalışma 25.10.2026 03:12:47; sonuç eksik öğelerle tamamlandı, 2 site / 0 veritabanı ve 9 eski kayıt silindi olarak gösteriliyor.
- Son işlemde `travianpanel.com`, `travianpanel.xyz` ve bir veritabanı için disk yetersizliği; diğer iki veritabanı için `database.dump_failed` bildiriliyor. 24 Ekim günlüğünde de aynı disk yetersizliği, ayrıca posta/panel arşivi yazma hataları var. İki genel dump hatasının ayrıntısı bu günlükte yok; yalnız zaman birlikteliğine dayanarak kesin neden atanmıyor.
- İnceleme sırasında C: 74,4 GB toplam / 17,6 GB boş görünüyordu. Bu, gece başarısızlığı anındaki boş alan ölçümü değildir.
- Yedek kökü sunucuda `C:\ProgramData\AdaPanel\backups`; FTP hedefi `ev.alioral.com.tr`. Bu bilgisayardaki FileZilla Server'ın ilgili ana dizini `D:\AdaPanelDBLERİ`. Dosya okuma/yazma/silme ve klasör oluşturma/silme/listeleme izinleri yapılandırmada açık.
- Sunucunun `config/backups.json` dosyasının inceleme kopyasındaki **63 kayıt**, FTP depolama klasöründe aynı göreli yol ve boyutla bulundu. **63/63 SHA-256** kayıtla eşleşti. Toplam yaklaşık **24,97 GiB**. Bu bütünlük karşılaştırmasıdır; veritabanlarını veya siteleri canlıya geri yükleme provası yapılmadı.
- Kayıt dışındaki dört dosya eski `connection-test/adapanel-connection-test.txt` işaretleridir; her biri 69 bayt, toplam 276 bayt. Büyük, kayıt dışı FTP yedek arşivi bulunmadı.

## Eski FTP dosyalarının kalma nedeni

- 2–3 Ekim tarihli **19 kayıt**, eski biçimden geliyor: `includedDatabases` ve `includedComponents` ölçülmemiş/null. Yeni ölçülmüş kayıtla içerik eşdeğerliği kanıtlanamadığından saklama kuralı bu kayıtları koruyor. Bazı kaynakların ayrıca güncel yedeği de yok.
- 13–14 Ekim tarihli **4 kayıt**: iki VideoTools site yedeğinin daha yeni aynı kaynak yedeği bulunmuyor; iki panel yedeği yeni panel kopyalarından farklı olarak VideoTools yapılandırmasını da kapsıyor. Bunları yalnız tarihe bakarak silmek, kapsamı artık yedeklenmeyen veriyi kaldırır.
- 20 Ekim'in kalan yedekleri yaklaşık 03:20'de oluşturulmuş. 25 Ekim 03:12'deki temizlik anında tam 120 saat dolmamıştı. Ayar takvim klasörü sayısı değil, kayıt oluşturma zamanından geçen süredir.
- Son özet FTP silme hatası bildirmiyor. Kaynak kodda `Removed`, kayıtlı yerel ve varsa uzak kopya silme adımları başarıyla sonuçlanınca artıyor. Bu sayı FTP kökündeki bütün eski dosyaların silindiği anlamına gelmiyor.
- Arayüzdeki “Dosya yok” rozeti **yerel** dosya durumudur; “Uzak hedefe kopyalandı” ise geçmiş yükleme sonucudur. Bu incelemede uzak kopyalar ayrıca dosya ve SHA-256 karşılaştırmasıyla doğrulandı.

## Kaynak kodda düzeltilenler

- Site/veritabanı/panel/abonelik/posta üretiminde hata veya iptal öncesi oluşturulan, henüz kaydı yayımlanmamış dosyalar için sahiplik temelli temizlik. Başka işlemin önceden var olan dosyası veya yayımlanmış yedek silinmiyor.
- Şifreleme/sıkıştırma ayrı geçici çıktıya yazılıyor, tamamlanınca mevcut hedefin üzerine yazmadan taşınıyor. Hata/iptal artık yarım `.enc`/`.zst` bırakmıyor. İşlem yarıda kesildiğinde işletim sistemi ya da izin hatası temizliği engellerse best-effort temizliğin sınırı sürer; ani güç kesintisi için genel dosya taraması eklenmedi.
- Saklama planı artık **korunan süresi dolmuş kayıtları** da nedenleriyle bildiriyor. Silme seçim kuralları gevşetilmedi. Gece ve abonelik özetlerinde korunan sayı ve teknik neden/Türkçe açıklama yer alıyor.
- Yedek üretiminden önce de aynı güvenli saklama planının çalıştırılması; üretim sonrası temizlik korunuyor. Amaç, zaten silinebilir eski kopyaları yeni büyük ZIP yazılmadan temizlemek. Son sağlam kopya ve zincir korumaları sürüyor. Başlangıç temizliği hatası raporlanırken üretim devam edebiliyor; iptal veya yetki/program değişikliği işini durduruyor.
- İki temizlik turunda aynı dosyanın silinmesi başarısız olursa toplam, farklı yedek sayısı gibi sunulmuyor; açıkça başarısız silme denemesi olarak gösteriliyor.

## Sınırlar ve canlı durum

- Bu incelemede canlı yedek, FTP dosyası, sertifika, posta ayarı veya program ayarı silinmedi/değiştirilmedi. Eski yedeklerin kapsamını bilinmiyor yerine boş kabul eden bir geçiş yapılmadı.
- Eski 19 kaydın güvenli kaldırılması için arşiv kapsamının doğrulanması veya yönetici tarafından hangi geri dönüş noktalarının bırakılacağının açıkça seçilmesi gerekir. Koruma kuralını kaldırmak çözüm değildir.
- Yedek üretiminde süreç zorla kapatılması/güç kesintisi ve kayda giremeden yarım kalan uzak yüklemeler için ayrı kurtarma sınırları var; otomatik budama bilinen kayıtları işler. Canlı FTP envanterinde bu tür büyük kayıt dışı arşiv saptanmadı.
- Kaynak düzeltmeleri henüz canlı sunucuya kurulmuş değildir. Bu inceleme yeni sürüm yayımlandı veya canlı yedekleme yeniden başarılı oldu iddiası taşımaz.

## Canlıda tamamlanması gerekenler

- Doğrulanmış kaynak düzeltmelerini içeren sürüm kurulmalı; ardından bir tam gece yedekleme sonucunda site, veritabanı, posta ve panel sonuçları ayrı doğrulanmalı.
- İki genel `database.dump_failed` hatası için ilgili veritabanı dışa aktarma işleminin ayrıntılı hata çıktısı incelenmeli. Bu incelemede canlı veritabanlarında yeni dump/geri yükleme başlatılmadı.
- Disk ihtiyacı ve yerel kopya saklama tercihi değerlendirilmeden sırf FTP'de kopya var diye yerel dosyalar kaldırılmamalı. Ön temizlik yalnız mevcut güvenli silme kurallarına uyan kayıtlar için alan açar; tek başına yeterli boş alan garantisi vermez.
- VideoTools'un yeni programda neden aynı kapsamda yedeklenmediği kontrol edilmeli. Son uygun kopyaları silmek yerine eksik güncel kapsam tamamlanmalı; bir geri yükleme provası ayrı ortamda yapılmalı.

## Doğrulama

- Geliştirici odaklı ilk küme: 64 geçti; temizliği devre dışı bırakan mutasyonda 11/12 regresyon beklendiği gibi başarısız oldu, mutasyon geri alındı.
- İlk geniş küme: 1471 geçti / 1 FTP soket süre aşımı. Aynı 7 FTP deadline testi kaynak değiştirilmeden bağımsız koşuda geçti.
- Üretim öncesi temizlik odaklı kümesi: 93/93 geçti. Ön temizliği devre dışı bırakan mutasyonda 8/10 yeni test beklendiği gibi başarısız oldu; kaynak geri yüklendi.
- Sonraki geniş kümede 1478/1482 geçti; dört başarısızlık mevcut testlerde tek temizlik turu beklenmesiydi. Beklentiler iki tura güncellendi; yetki, iptal, program ve abonelik kapsamı kontrolleri korundu. FTP zamanlama testleri bu koşuda geçti.
- Aynı dosyanın iki kez silinememesini kapsayan dört yeni regresyon testi eski özetle 4/4 başarısız oldu; düzeltme uygulandı.
- Son odaklı küme: **555/555 geçti** (`backup-prune-attempts-final-20261025.trx`).
- Son geniş yedekleme kümesi: **1486/1486 geçti, 0 başarısız, 0 atlanan**. FTP deadline testleri dahil. Son kaynakla oluşturulan derlemeye `--no-build --no-restore` kullanıldı; sonuç `tests/AdaPanel.Infrastructure.Tests/TestResults/backup-audit-verified-20261025.trx`. `git diff --check` temiz.
- Bağımsız `rues-denetci`, dosya sahipliği, saklama açıklamaları, üretim öncesi/sonrası temizlik ve hata sayımı düzeltmesini inceledi; **ONAY**, açık engel yok. Denetçi test çalıştırmadı; testler geliştirici ve ana oturumda çalıştırıldı.
