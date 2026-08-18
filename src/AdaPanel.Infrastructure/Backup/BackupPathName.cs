namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// Kullanıcı verisinden (site adı, veritabanı adı, abonelik kimliği) YOL PARÇASI üretmenin TEK
/// kaynağı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ortak bir yardımcı:</b> aynı temizlik iki yerde ayrı ayrı yazılmıştı
/// (<c>BackupLayout.NormalizeCategory</c> ve <c>BackupService.BuildRestoreFolderName</c>). Aynı
/// kuralın iki kopyası, biri düzeltilip diğeri unutulduğunda "bir yerde engellenen, öbür yerde
/// serbest" bir dizin kaçışı demektir. Kural burada bir kez yazılır; her iki çağıran da buradan
/// geçer.
/// </para>
/// <para>
/// <b>Ne yapar:</b> dosya adında geçersiz olan HER karakteri atar (yol ayırıcıları dâhil), baştan
/// ve sondan boşluk ile NOKTA kırpar, sonuç boşsa sabit bir ada düşer, aşırı uzun adı keser.
/// </para>
/// <para>
/// <b>Neden nokta kırpılıyor:</b> ayırıcılar atıldıktan sonra <c>..\..\Windows</c> geriye
/// <c>....Windows</c> bırakır; baştaki noktalar kırpılmazsa <c>..</c> gibi bir ad üretilebilir ve
/// yol bir seviye YUKARI kayabilirdi. Ayrıca Windows sonu nokta/boşlukla biten klasör adını
/// sessizce budar — o ad diskte beklenenden başka bir klasöre denk gelirdi.
/// </para>
/// </remarks>
internal static class BackupPathName
{
    /// <summary>
    /// Tek bir yol parçasının üst sınırı. Site/veritabanı adı yola girdiği için toplam yol
    /// uzunluğu artık kullanıcı verisine bağlı; sınırsız bırakmak, uzun adlı bir sitede yedeği
    /// "yol çok uzun" hatasıyla sessizce başarısız kılardı.
    /// </summary>
    internal const int MaxLength = 64;

    internal static string Sanitize(string? value, string fallback)
    {
        var gecersizler = Path.GetInvalidFileNameChars();
        var suzulmus = new string((value ?? string.Empty)
            .Where(karakter => !gecersizler.Contains(karakter)
                // Linux'ta GetInvalidFileNameChars yalnız '\0' ve '/' döner; '\' orada geçerli bir
                // ad karakteridir. Ayırıcılar bu yüzden AYRICA elenir — kural işletim sistemine
                // göre gevşemesin.
                && karakter != Path.DirectorySeparatorChar
                && karakter != Path.AltDirectorySeparatorChar)
            .ToArray());

        var temiz = Kirp(suzulmus);
        if (temiz.Length > MaxLength)
        {
            // Kesme yeniden nokta/boşlukla biten bir ad üretebilir; ikinci kırpma bunun içindir.
            temiz = Kirp(temiz[..MaxLength]);
        }

        return temiz.Length == 0 ? fallback : temiz;
    }

    /// <summary>
    /// Baştan/sondan boşluk ve noktayı DEĞİŞMEZ HÂLE GELENE KADAR kırpar. Tek turluk
    /// <c>Trim().Trim('.')</c> yetmez: <c>"a. . "</c> ilk turdan <c>"a."</c> olarak çıkardı.
    /// </summary>
    private static string Kirp(string deger)
    {
        var onceki = deger;
        while (true)
        {
            var simdi = onceki.Trim().Trim('.');
            if (simdi.Length == onceki.Length)
            {
                return simdi;
            }

            onceki = simdi;
        }
    }
}
