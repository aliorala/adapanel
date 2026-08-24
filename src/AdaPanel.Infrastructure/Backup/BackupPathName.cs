using AdaPanel.Core.Common;

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
/// ve sondan boşluk ile NOKTA kırpar, sonuç boşsa sabit bir ada düşer, aşırı uzun adı keser ve
/// en son Windows'un AYRILMIŞ AYGIT ADLARINI etkisizleştirir.
/// </para>
/// <para>
/// <b>Neden aygıt adı ayrıca gerekiyor:</b> <c>GetInvalidFileNameChars</c> aygıt adlarını bilmez
/// — "CON" içinde geçersiz KARAKTER yoktur. "CON" adlı bir sitenin yedeği
/// <c>CreateDirectory</c> istisnasıyla düşüyordu (bkz. <see cref="WindowsReservedNames"/>).
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

        if (temiz.Length == 0)
        {
            return fallback;
        }

        // AYRILMIŞ AYGIT ADI son adımda etkisizleştirilir — kesme ve kırpmadan SONRA, çünkü
        // ikisi de ayrılmış olmayan bir adı ayrılmış hâle getirebilir: "console" 64 karaktere
        // kesilse değişmez ama "con.x" kırpılınca "con" kalır. Önce baksaydık, kendi ürettiğimiz
        // adı denetimsiz bırakırdık.
        //
        // Neden fallback'e DÜŞÜLMÜYOR: "CON" ve "NUL" adlı iki site aynı yedek klasörüne
        // karışırdı. Ek koymak kaynağı ayırt edilebilir bırakır.
        return WindowsReservedNames.MakeSafe(temiz);
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
