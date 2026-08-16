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
/// fazlası, süresi dolmamış olsa bile silinebilir (disk yönetimi).</item>
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
    /// <param name="keepCount">Kaynak başına korunacak en yeni geri yükleme noktası sayısı (0 = sayı limiti yok).</param>
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
        //    Sayı limiti kaynak korumasından BAĞIMSIZDIR: elde N nokta kalacağı garanti olduğu için
        //    "hiç yedek kalmama" riski yoktur.
        var sayiIleSilinen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (keepCount > 0)
        {
            foreach (var kaynak in noktalar.GroupBy(nokta => nokta.SourceKey, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var fazla in kaynak.OrderByDescending(nokta => nokta.NewestUtc).Skip(Math.Max(1, keepCount)))
                {
                    sayiIleSilinen.Add(fazla.Key);
                    silinecek.AddRange(fazla.Records);
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
