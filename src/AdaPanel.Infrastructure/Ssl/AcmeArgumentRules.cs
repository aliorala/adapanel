namespace AdaPanel.Infrastructure.Ssl;

/// <summary>
/// win-acme (<c>wacs.exe</c>) KOMUT SATIRINA giren kullanıcı değerlerinin TEK doğruluk kaynağı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden bir güvenlik sınırı:</b> host adı ve iletişim e-postası, <c>--host</c> ve
/// <c>--emailaddress</c> argümanlarına ÇIPLAK gömülüyordu ve panel servisi LocalSystem olarak
/// koşuyor. <c>ProcessRunner.RunAsync</c> kabuk kullanmaz (<c>UseShellExecute = false</c>), yani
/// <c>;</c> <c>|</c> <c>&amp;</c> <c>$(...)</c> burada ANLAMSIZDIR — ama BOŞLUK anlamlıdır: hedef
/// süreç argüman dizesini <c>CommandLineToArgvW</c> ile böler. Değerin içine boşluk koyabilen
/// taraf, wacs.exe'ye KENDİ ARGÜMANINI ekler.
/// </para>
/// <para>
/// Bu bir "gürültü" değil, doğrudan kod çalıştırmadır: win-acme'nin <c>--validation script
/// --dnsscript &lt;yol&gt;</c> ve <c>--installation script --script &lt;yol&gt;</c> seçenekleri
/// tanımı gereği harici bir program çalıştırır. Yani host alanına
/// <c>ornek.com --validation script --dnsscript C:\...\yuk.bat</c> yazan bir müşteri, o dosyayı
/// SYSTEM olarak koşturur. Bu, panel yöneticisinin bile aşamaması gereken sınırdır
/// (bkz. <c>UpdatePackageTrust</c>).
/// </para>
/// <para>
/// Kural kasten DAR ama IDN'i kırmıyor: Unicode harf/rakam serbest, çünkü panelde Türkçe harfli
/// alan adlarıyla kurulmuş IIS bağlamaları meşrudur ve bunlar kendinden imzalı sertifika yoluna da
/// giriyor. Kırılan tek şey, bir alan adında ZATEN geçerli olmayan karakterler.
/// </para>
/// </remarks>
internal static class AcmeArgumentRules
{
    /// <summary>
    /// Host adı komut satırına konabilir mi? Joker önek (<c>*.</c>) serbesttir — joker sertifika
    /// yolu bu biçimi kendisi üretir.
    /// </summary>
    public static bool IsCommandSafeHost(string? value)
    {
        var deger = (value ?? string.Empty).Trim();

        // 253: DNS adının tam uzunluk sınırı. Üstü zaten geçersizdir, argüman dizesini şişirmesin.
        if (deger.Length is 0 or > 253)
        {
            return false;
        }

        // BAŞTA TİRE OLAMAZ. Kaçış ya da tırnak burada işe yaramaz: "-host" gibi bir değer,
        // tırnaklı bile olsa argüman ayrıştırıcısı için hâlâ bir SEÇENEKtir.
        if (deger[0] == '-')
        {
            return false;
        }

        if (deger.StartsWith("*.", StringComparison.Ordinal))
        {
            deger = deger[2..];
            if (deger.Length == 0 || deger[0] == '-')
            {
                return false;
            }
        }

        // Nokta ARDIŞIK olamaz ve sonda tek başına duramaz: "a..b" ya da "a." geçerli bir ad değil.
        if (deger.Contains("..", StringComparison.Ordinal) || deger[^1] == '.')
        {
            return false;
        }

        foreach (var karakter in deger)
        {
            // char.IsLetterOrDigit Unicode'dur (IDN korunur). BOŞLUK, tırnak, tire dışı noktalama
            // ve denetim karakterlerinin hiçbiri bu kümede yok.
            if (!char.IsLetterOrDigit(karakter) && karakter is not ('.' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// İletişim e-postası komut satırına konabilir mi? Tam RFC 5322 doğrulaması DEĞİL — amaç
    /// "geçerli mi" değil, "argüman sınırını kırabilir mi". Yerel kısımda tırnaklı/boşluklu biçim
    /// (RFC'de teknik olarak geçerli) BİLEREK reddediliyor: Let's Encrypt zaten kabul etmez.
    /// </summary>
    public static bool IsCommandSafeEmail(string? value)
    {
        var deger = (value ?? string.Empty).Trim();
        if (deger.Length is 0 or > 254)
        {
            return false;
        }

        if (deger[0] == '-')
        {
            return false;
        }

        var at = deger.IndexOf('@');
        if (at <= 0 || at != deger.LastIndexOf('@') || at == deger.Length - 1)
        {
            return false;
        }

        foreach (var karakter in deger[..at])
        {
            if (!char.IsLetterOrDigit(karakter) && karakter is not ('.' or '-' or '_' or '+'))
            {
                return false;
            }
        }

        // Alan adı kısmı host kuralının aynısından geçer, ama joker orada anlamsız.
        var alan = deger[(at + 1)..];
        return !alan.StartsWith("*.", StringComparison.Ordinal)
            && alan.Contains('.', StringComparison.Ordinal)
            && IsCommandSafeHost(alan);
    }
}
