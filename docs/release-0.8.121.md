# AdaPanel 0.8.121 — Yedek saklama süresi ve FTP temizliği

25 Ekim 2026 09:11 (Türkiye) tarihinde yayımlandı: https://github.com/aliorala/adapanel/releases/tag/0.8.121

## Davranış

Kullanıcının eski dosyaların yine de silinmesi talimatıyla pozitif saklama günü kesin yaş sınırıdır. Tamamı süre dışına çıkmış geri yükleme noktaları, yeni bir kopya bulunmasa, eski kapsam bilgileri bilinmese veya adet sınırının içinde kalsa da seçilir. Yerel ve kayıtlı uzak kopyalar mevcut silme akışından geçer. Başarısız uzak silme başarı sayılmaz; kayıt sonraki deneme için kalır.

Artımlı zincirin yaşı en yeni üyeye göre belirlenir; süre içindeki bir üyeyi geri yüklemek için gereken eski üyeler yaş nedeniyle ayrı silinmez. 0 gün yaş temizliğini kapatır; yalnız adet bazlı seçimde önceki korumalar sürer. Gün sınırının adet sınırına önceliği TR/EN ayar açıklamalarında belirtilir.

Önceki incelemede hazırlanan üretici dosya sahipliği/yarım dosya temizliği, şifreleme/sıkıştırma staging çıktıları ve üretim öncesi/sonrası budama da bu sürümdedir. Tekrarlanan silme hataları ayrı yedeklermiş gibi raporlanmaz.

Bu sürüm, FTP'deki ilgisiz dosyalar veya kayıt dışı bağlantı testi işaretleri için toplu silme yapmaz. Sunucuya kurulum ve canlı eski arşivlerin silinmesi, GitHub'da kurulum paketinin yayımlanmasından ayrı işlemlerdir.

## Doğrulama

- Geliştirici odaklı küme: 596/596 geçti. Yeni yaş sınırı testlerinin eski politikayla 8'i başarısız, 3'ü başarılıydı; yeni politikayla 11/11 geçti.
- `rues-denetci` son kaynağa ONAY verdi: aktif zincir, silme sırası, FTP hata kaydı, abonelik kapsamı, arayüz ve bildirim açıklamaları incelendi. Denetçi test çalıştırmadı.
- Frontend: 141 dosya / 2707 test geçti; tür kontrolü ve üretim derlemesi başarılı. Sürüm notu ayrıştırıcı kontrolü ve 12/12 mahremiyet kapısı testi geçti.
- Geniş Release koşusunda Core 2/2, Infrastructure 10321 geçti / 11 eski saklama beklentisi başarısız oldu. Bu 11 beklenti, kimlik/bütünlük/zincir korumalarını yalnız adet budamasında sınamaya taşındı; yaş sınırı ayrı doğrulanır. Üretim kodu bu tekrar sırasında değiştirilmedi. Son Release yedekleme grubu **1497/1497 geçti**, 0 başarısız/atlanan (`release-0.8.121-backup-final.trx`). Geniş kümenin tamamı ikinci kez koşulmadı. Canlı Windows hesapları oluşturan iki `FtpCreateRollbackTests` testi yayın prosedürüne uygun olarak kapsam dışı tutuldu.
- Frontend çıktısındaki 154 dosyanın API wwwroot kopyalarıyla SHA-256 değerleri eşleşti. Bileşen paketi denetimi 22 Passed / standart kapsam dışı 3 büyük paket Skipped; başarısız paket yok.
- Kaynak commit'i: `40ef5ac`. Kaynak geçmişi uzak depoya gönderilmedi; mevcut yayın yöntemiyle kurulum paketi ve notlar yayımlanır.
- Paket: 0.8.121, 91.133.143 bayt. Publish envanterinde 172 dosya doğrulandı; installer sürüm/içerik denetimi geçti. Bileşen denetimi bu tur ayrı çalıştığı için yalnız paketlemedeki tekrarı atlandı.
- SHA256: `F6B14F3EDD5603947FC805F475DA9F8655FAB47E952E29A9C1FB0452D28B3B99`.
- Kimliksiz GitHub API'sinde `latest=0.8.121`, taslak/ön sürüm kapalı, tek `uploaded` kurulum paketi doğrulandı. Ad ve Türkçe notlar kaynakla birebir; 91.133.143 bayt ve GitHub SHA-256 digest'i yerel paketle eşleşiyor. Kanıt: `.tmp/backup-audit-20261025/release121-public-proof.json`.
- Yayın betiği başarıyla tamamlandı; yayımlanan paket yeniden indirildi ve SHA-256 yerel dosyayla birebir eşleşti (`publish-121.log`). Canlı sunucuya kurulum veya canlı FTP dosyalarının silinmesi bu turda yapılmadı; yeni politika güncelleme kurulduktan sonraki yedekleme/temizlik çalışmasında uygulanır.
