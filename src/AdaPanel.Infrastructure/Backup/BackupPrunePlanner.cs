using AdaPanel.Core.Dtos.Backup;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// "Hangi yedek SİLİNEBİLİR" kararının TEK kaynağı.
/// </summary>
/// <remarks>
/// <para>
/// Bu karar panelin verebileceği en pahalı kararlardan biridir: yanlış bir "evet", geri
/// yüklenecek son noktayı yok eder. Bu yüzden karar dosya sistemine dokunmayan saf bir
/// fonksiyona alındı; <see cref="BackupService"/> yalnızca sonucu uygular.
/// </para>
/// <para>
/// Üç koruma birlikte çalışır:
/// </para>
/// <list type="number">
/// <item><b>Kaynak bazında koruma:</b> bir kaynağın (site/veritabanı/abonelik/posta/panel)
/// cutoff'tan sonra alınmış KULLANILABİLİR bir yedeği yoksa, o kaynağın eskilerine dokunulmaz.
/// Yenisini üretemiyorsak eski olan son savunmadır.</item>
/// <item><b>Zincir bütünlüğü:</b> artımsal zincir tek parçadır; ancak EN YENİ halkası da süresini
/// doldurduğunda budanır. Aksi hâlde 30 gün önceki tam yedeği silmek, dün alınmış artımsal
/// halkayı sessizce çöpe çevirirdi.</item>
/// <item><b>Sayı limiti (KeepCount):</b> en yeni N geri yükleme noktası her koşulda korunur;
/// fazlası, süresi dolmamış olsa bile silinebilir (disk yönetimi). Bu dal da
/// <b>KULLANILABİLİRLİK</b> yüklemine tabidir: "N nokta kaldı" ile "geri yüklenebilir nokta
/// kaldı" aynı şey değildir.</item>
/// </list>
/// </remarks>
internal static class BackupPrunePlanner
{
    /// <summary>Bir geri yükleme noktası: tek bir yedek ya da bir artımsal zincirin tamamı.</summary>
    private sealed record RestorePoint(string Key, string SourceKey, DateTime NewestUtc, List<BackupRecordDto> Records);

    /// <summary>
    /// Silinebilecek kayıtları seçer. Dönen liste HİÇBİR zincirin ortasını kırmaz: bir zincirden
    /// ya hepsi seçilir ya hiçbiri.
    /// </summary>
    /// <param name="records">Değerlendirilecek kayıtlar (çağıran zaten kapsamı daraltmış olabilir).</param>
    /// <param name="cutoffUtc">Bu tarihten eski yedekler süresini doldurmuş sayılır.</param>
    /// <param name="keepCount">
    /// Kaynak başına korunacak en yeni geri yükleme noktası sayısı (0 = sayı limiti yok).
    /// Bu bir ÜST SINIR DEĞİL ALT GARANTİDİR: kaynağın son KULLANILABİLİR noktası bu limit
    /// yüzünden silinemez.
    /// </param>
    /// <param name="kullanilabilir">Kayıt GERÇEKTEN geri yüklenebilir mi (dosya yerinde ya da off-site kopya var).</param>
    public static IReadOnlyList<BackupRecordDto> Select(
        IReadOnlyCollection<BackupRecordDto> records,
        DateTime cutoffUtc,
        int keepCount,
        Func<BackupRecordDto, bool> kullanilabilir)
    {
        if (records.Count == 0)
        {
            return [];
        }

        // 1) KAYNAK BAZINDA KORUMA: cutoff'tan sonra alınmış kullanılabilir yedeği olan kaynaklar.
        var korunanKaynaklar = records
            .Where(record => record.CreatedAtUtc >= cutoffUtc && kullanilabilir(record))
            .Select(SourceKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 2) Geri yükleme noktalarına grupla (zincir = tek nokta).
        var noktalar = records
            .GroupBy(PointKey, StringComparer.OrdinalIgnoreCase)
            .Select(grup => new RestorePoint(
                grup.Key,
                SourceKey(grup.First()),
                grup.Max(record => record.CreatedAtUtc),
                grup.ToList()))
            .ToArray();

        var silinecek = new List<BackupRecordDto>();

        // 3) SAYI LİMİTİ: kaynak başına en yeni N nokta korunur, fazlası (yaşı ne olursa olsun) gider.
        //
        //    ÖLÇÜLEN HATA: bu dal eskiden `kullanilabilir` yüklemini HİÇ çağırmıyordu; yorumu da
        //    "elde N nokta kalacağı garanti, hiç yedek kalmama riski yok" diyordu. Yanlıştı —
        //    "nokta kalması" ile "GERİ YÜKLENEBİLİR nokta kalması" aynı şey değil. Ölçüm:
        //    b1(10 gün, dosya var) + b2(5 gün, dosya var) + b3(bugün, dosyası YOK), keepCount=1
        //    → en yeni nokta b3 korunur, b1 ve b2 silinirdi ve geri yüklenebilir yedek sayısı
        //    SIFIRA düşerdi. Kaydı duran ama dosyası olmayan bir yedek, yedek DEĞİLDİR.
        //
        //    Kural: sayı limiti, kaynağın SON KULLANILABİLİR noktasını asla silemez. Limit
        //    dolmuşsa bile, henüz kullanılabilir bir nokta korunmadıysa ilk kullanılabilir nokta
        //    fazladan korunur. Her şey sağlamken davranış birebir eskisi gibidir (en yeni nokta
        //    zaten kullanılabilirdir ve limit ilk turda dolar).
        var sayiIleSilinen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (keepCount > 0)
        {
            var limit = Math.Max(1, keepCount);
            foreach (var kaynak in noktalar.GroupBy(nokta => nokta.SourceKey, StringComparer.OrdinalIgnoreCase))
            {
                var korunan = 0;
                var kullanilabilirKorundu = false;

                foreach (var nokta in kaynak.OrderByDescending(nokta => nokta.NewestUtc))
                {
                    // NOKTA kullanılabilirliği ZİNCİRİN TAMAMIDIR: bir halkası eksik olan zincir
                    // geri yüklenemez, dolayısıyla "elimizde duran nokta" sayılamaz.
                    var noktaKullanilabilir = nokta.Records.All(kullanilabilir);

                    if (korunan < limit || (!kullanilabilirKorundu && noktaKullanilabilir))
                    {
                        korunan++;
                        kullanilabilirKorundu |= noktaKullanilabilir;
                        continue;
                    }

                    sayiIleSilinen.Add(nokta.Key);
                    silinecek.AddRange(nokta.Records);
                }
            }
        }

        // 4) SÜRE LİMİTİ: noktanın EN YENİ halkası da süresini doldurmuş olmalı + kaynak korumalı olmalı.
        foreach (var nokta in noktalar)
        {
            if (sayiIleSilinen.Contains(nokta.Key))
            {
                continue; // zaten seçildi
            }

            if (nokta.NewestUtc >= cutoffUtc)
            {
                continue; // nokta hâlâ taze (zincirin son halkası dahil)
            }

            if (!korunanKaynaklar.Contains(nokta.SourceKey))
            {
                continue; // bu kaynağın GÜNCEL yedeği yok → eskisine dokunulmaz
            }

            silinecek.AddRange(nokta.Records);
        }

        return silinecek;
    }

    /// <summary>
    /// Budama korumasının "kaynak" tanımı: aynı siteyi/veritabanını/aboneliği temsil eden yedekler
    /// tek küme sayılır. Bir sitenin yedeği, BAŞKA bir sitenin eskilerini silmeye yetki vermez.
    /// </summary>
    public static string SourceKey(BackupRecordDto record) => $"{record.Type}|{record.Source}";

    /// <summary>
    /// Geri yükleme noktası anahtarı: zincire aitse ZİNCİR, değilse kaydın kendisi.
    /// Zinciri tek anahtar altında toplamak, "ya hepsi ya hiçbiri" kuralının uygulandığı yerdir.
    /// </summary>
    private static string PointKey(BackupRecordDto record) =>
        string.IsNullOrWhiteSpace(record.ChainId) ? $"id:{record.Id}" : $"chain:{record.ChainId}";
}
