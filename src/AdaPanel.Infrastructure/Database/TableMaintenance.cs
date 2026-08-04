namespace AdaPanel.Infrastructure.Database;

/// <summary>
/// Tablo bakımının SAF (veritabanına dokunmayan) kararları: hangi motor onarılabilir, bir denetim
/// sonucu sorunlu mu, tanımlayıcı nasıl kaçırılır.
/// <para>
/// Neden ayrı sınıf: bu kararlar özelliğin doğruluk özüdür ve yanlış verildiğinde sonuç sessiz
/// olur — panel "onarıldı" der, hiçbir şey yapılmamıştır. Canlı bir MySQL sunucusu olmadan
/// kanıtlanabilmeleri gerekir.
/// </para>
/// </summary>
internal static class TableMaintenance
{
    /// <summary>
    /// <c>REPAIR TABLE</c>'ın GERÇEKTEN çalıştığı motorlar.
    /// <para>
    /// Belgelerden birebir: MySQL'de MyISAM, ARCHIVE ve CSV; MariaDB ayrıca Aria. InnoDB bu
    /// listede YOKTUR ve olmaması bu özelliğin en kritik ayrıntısıdır — çünkü InnoDB'de REPAIR
    /// hata vermez, sessizce hiçbir şey yapmadan "OK" döner.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> RepairableEngines = new(StringComparer.OrdinalIgnoreCase)
    {
        "MyISAM",
        "Aria",
        "ARCHIVE",
        "CSV"
    };

    /// <summary>
    /// <c>OPTIMIZE TABLE</c> ile yeniden inşa edilebilen motorlar. InnoDB'de bu çağrı
    /// <c>ALTER TABLE ... FORCE</c>'a eşlenir ve tabloyu gerçekten yeniden yazar.
    /// </summary>
    private static readonly HashSet<string> RebuildableEngines = new(StringComparer.OrdinalIgnoreCase)
    {
        "InnoDB"
    };

    internal static bool CanRepair(string? engine) =>
        !string.IsNullOrWhiteSpace(engine) && RepairableEngines.Contains(engine);

    internal static bool CanRebuild(string? engine) =>
        !string.IsNullOrWhiteSpace(engine) && RebuildableEngines.Contains(engine);

    /// <summary>
    /// Tanımlayıcıyı ters tırnakla kaçırır. Veritabanı ve tablo adları kullanıcıdan gelir ve
    /// sorguya metin olarak eklenir (CHECK/REPAIR TABLE parametre kabul etmez), bu yüzden
    /// kaçırma ŞART: içindeki ters tırnak ikilenir.
    /// </summary>
    internal static string Quote(string identifier) =>
        "`" + (identifier ?? string.Empty).Replace("`", "``") + "`";

    /// <summary>
    /// Denetim sonucu satırlarını tek bir duruma indirger.
    /// <para>
    /// SON SATIRA BAKMAK YETMEZ: bir tablo için birden çok satır döner ve <c>error</c> satırı
    /// ortada olup son satır <c>status: OK</c> olabilir. ISPConfig'in yaptığı hata tam olarak
    /// budur (satırın son iki karakterine bakıyor) ve InnoDB'nin işe yaramaz "OK" satırı o
    /// testten geçer.
    /// </para>
    /// <para>
    /// <c>Table is already up to date</c> bir SORUN DEĞİLDİR: motor "denetlemeye gerek yok" diyor.
    /// Bunu sorun saymak sahte alarm üretir.
    /// </para>
    /// </summary>
    internal static string Classify(IEnumerable<(string MsgType, string MsgText)> rows)
    {
        var sorunlu = false;
        var desteklenmiyor = false;

        foreach (var (tip, metin) in rows)
        {
            // "doesn't support check/repair" bir hata değil, YAPILMADI bilgisidir. Ayrı
            // sınıflandırılır ki kullanıcıya "sağlıklı" diye gösterilmesin.
            if (metin.Contains("doesn't support", StringComparison.OrdinalIgnoreCase) ||
                metin.Contains("does not support", StringComparison.OrdinalIgnoreCase))
            {
                // OPTIMIZE'ın "does not support optimize, doing recreate + analyze instead"
                // mesajı BAŞARIDIR: tablo gerçekten yeniden inşa edildi. Aynı kelimeleri
                // taşıyan iki mesajın anlamı zıttır; ayırt eden "instead" kısmıdır.
                if (metin.Contains("instead", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                desteklenmiyor = true;
                continue;
            }

            if (string.Equals(tip, "error", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tip, "warning", StringComparison.OrdinalIgnoreCase))
            {
                sorunlu = true;
            }
        }

        if (sorunlu)
        {
            return "problem";
        }

        return desteklenmiyor ? "unsupported" : "healthy";
    }
}
