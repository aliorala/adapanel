using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AdaPanel.Infrastructure.Htaccess;

/// <summary>
/// <c>.htaccess</c> içindeki desenlerin TEK derleme ve TEK çalıştırma noktası.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN TEK NOKTA (pazarlık dışı):</b> desenleri MÜŞTERİ yazar. <c>(a+)+$</c> gibi patolojik
/// bir desen, uzun bir girdiyle karşılaştığında geri-izlemeli (backtracking) motoru üstel süreye
/// sokar; zaman aşımı olmadan tek istek bir CPU çekirdeğini süresiz kilitler ve sunucudaki BÜTÜN
/// siteler düşer. Bu yüzden <see cref="Regex"/> nesnesi <b>yalnızca burada</b> kurulur ve
/// <c>matchTimeout</c> geçmek zorunludur — çağıran unutamaz.
/// </para>
/// <para>
/// <b>Neden <see cref="RegexOptions.Compiled"/> YOK:</b> derleme her yeni desen için IL üretir ve
/// üretilen kod süreç boyunca boşaltılamaz. Desenleri müşteri yazdığı için bu, sınırsız büyüyen
/// bir bellek yüzeyidir. Yorumlanmış <see cref="Regex"/> nesnesinin ÖNBELLEKLENMESİ, derlemenin
/// getirisinin büyük kısmını sınırsız risk almadan verir.
/// </para>
/// <para>
/// <b>Neden <see cref="RegexOptions.NonBacktracking"/> YOK:</b> ReDoS'u yapısal olarak çözerdi ama
/// geri referansları ve ileri/geri bakışları DESTEKLEMEZ, bazı yakalama durumlarında da geri-izlemeli
/// motordan farklı sonuç verir. Gerçek <c>.htaccess</c> dosyalarında bu yapılar geçer; sessizce
/// farklı davranmaktansa (bu deponun en pahalı hata sınıfı) zaman aşımıyla GÜRÜLTÜLÜ durmak
/// tercih edildi.
/// </para>
/// </remarks>
internal sealed class HtaccessRegexCache
{
    private readonly ConcurrentDictionary<string, Regex> _onbellek = new(StringComparer.Ordinal);
    private readonly int _enFazlaDesen;

    public HtaccessRegexCache(int enFazlaDesen)
    {
        // 0 ya da negatif bir sınır "sınırsız" değil, "önbellekleme yok" demektir: sınırsız
        // seçeneği bilerek YOKTUR.
        _enFazlaDesen = enFazlaDesen;
    }

    /// <summary>Şu an önbellekte tutulan desen sayısı (test ve teşhis için).</summary>
    public int Count => _onbellek.Count;

    /// <summary>
    /// Deseni derler (ya da önbellekten verir). Derlenemezse <c>null</c> döner ve sebebi
    /// <paramref name="hata"/>'ya yazar — İSTİSNA SIZMAZ.
    /// </summary>
    public Regex? TryGet(string pattern, bool ignoreCase, TimeSpan timeout, out string? hata)
    {
        hata = null;
        if (pattern is null)
        {
            hata = "desen boş";
            return null;
        }

        var anahtar = $"{(ignoreCase ? "i" : "s")}|{timeout.Ticks}|{pattern}";
        if (_onbellek.TryGetValue(anahtar, out var hazir))
        {
            return hazir;
        }

        Regex derlenmis;
        try
        {
            var secenekler = RegexOptions.CultureInvariant;
            if (ignoreCase)
            {
                secenekler |= RegexOptions.IgnoreCase;
            }

            derlenmis = new Regex(pattern, secenekler, timeout);
        }
        catch (ArgumentException istisna)
        {
            // Bozuk desen kullanıcı hatasıdır: kural devre dışı kalır, panel sebebi görür.
            hata = istisna.Message;
            return null;
        }

        if (_enFazlaDesen > 0)
        {
            // Sınıra gelince önbellek TEMİZLENİR. LRU yerine bu seçildi çünkü site başına desen
            // sayısı onlarcadır; sınır yalnızca düşmanca bir dosyanın belleği şişirmesini keser
            // ve temizlik kendi kendini onarır.
            if (_onbellek.Count >= _enFazlaDesen)
            {
                _onbellek.Clear();
            }

            _onbellek.TryAdd(anahtar, derlenmis);
        }

        return derlenmis;
    }

    /// <summary>
    /// Eşleştirmeyi ZAMAN AŞIMINI YAKALAYARAK yapar. Zaman aşımında <c>null</c> döner;
    /// çağıran bunu "eşleşmedi" ile karıştırmasın diye <see cref="Match"/> yerine
    /// <c>Match?</c> döndürülür.
    /// </summary>
    public static Match? TryMatch(Regex regex, string input, out bool zamanAsimi)
    {
        zamanAsimi = false;
        try
        {
            return regex.Match(input);
        }
        catch (RegexMatchTimeoutException)
        {
            zamanAsimi = true;
            return null;
        }
    }

    /// <summary>
    /// Desende ÜSTEL geri-izleme riski taşıyan bir yapı var mı? İç içe niceleyici
    /// (<c>(a+)+</c>, <c>(a*)*</c>, <c>(a+)*</c>) klasik ReDoS kalıbıdır.
    /// </summary>
    /// <remarks>
    /// Bu bir SEZGİDİR, kanıt değil: yanlış pozitif verebilir, bazı riskli desenleri de kaçırır.
    /// Bu yüzden kuralı DEVRE DIŞI BIRAKMAZ, yalnızca panelde uyarı üretir. Gerçek savunma
    /// <c>matchTimeout</c>'tur; sezgi kullanıcının deseni düzeltmesi içindir.
    /// </remarks>
    public static bool LooksCatastrophic(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != '(' || Kacisli(pattern, i))
            {
                continue;
            }

            var kapanis = GrupSonu(pattern, i);
            if (kapanis < 0 || kapanis + 1 >= pattern.Length)
            {
                continue;
            }

            var disNiceleyici = pattern[kapanis + 1];
            if (disNiceleyici is not ('+' or '*' or '{'))
            {
                continue;
            }

            // Grubun İÇİNDE de niceleyici varsa iç içe niceleyici kalıbı oluşur.
            var govde = pattern[(i + 1)..kapanis];
            // IndexOf, Contains('|') DEGIL: string.Contains(char) net48'de YOK. Orada cagri sessizce
            // LINQ'in Enumerable.Contains'ine duserdi -- sonuc ayni, ama bu yol istek basina kosuyor
            // ve numaralandirici tahsis ederdi. Tek yazim, iki hedefte de ayni ve tahsissiz.
            if (govde.Any(k => k is '+' or '*') || govde.IndexOf('|') >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static int GrupSonu(string pattern, int acilis)
    {
        var derinlik = 0;
        for (var i = acilis; i < pattern.Length; i++)
        {
            if (Kacisli(pattern, i))
            {
                continue;
            }

            if (pattern[i] == '(')
            {
                derinlik++;
            }
            else if (pattern[i] == ')')
            {
                derinlik--;
                if (derinlik == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static bool Kacisli(string pattern, int index)
    {
        var tersBolu = 0;
        for (var i = index - 1; i >= 0 && pattern[i] == '\\'; i--)
        {
            tersBolu++;
        }

        return tersBolu % 2 == 1;
    }
}
