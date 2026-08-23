using System.Security.Cryptography;
using System.Text;

namespace AdaPanel.Infrastructure.Htaccess.Htpasswd;

/// <summary>
/// Apache'nin <c>$apr1$</c> ve klasik <c>$1$</c> (md5crypt) parola özetleri.
/// </summary>
/// <remarks>
/// <para>
/// <c>htpasswd -m</c>'in ürettiği biçim budur ve internetteki <c>.htpasswd</c> dosyalarının hâlâ
/// büyük kısmı böyledir. İki biçim arasındaki TEK fark başlangıç etiketidir (<c>$apr1$</c> /
/// <c>$1$</c>); algoritma aynıdır, bu yüzden tek uygulamayla ikisi de karşılanır.
/// </para>
/// <para>
/// Algoritma Apache'nin <c>apr_md5_encode</c> kaynağından yazıldı — "1000 tur" ve sondaki tuhaf
/// bayt sıralaması dahil, hepsi belirleyicidir. Doğruluğu Apache'nin KENDİ belgelerindeki test
/// vektörüyle ölçülüyor (<c>myPassword</c> → <c>$apr1$r31.....$HqJZimcKQFAMYayBlzkrA/</c>).
/// </para>
/// <para>
/// <b>Yeni parola ÜRETMEK için kullanılmamalı:</b> MD5 tabanlıdır ve bugünün ölçüsünde zayıftır;
/// burada yalnızca ESKİ dosyaları okuyabilmek için var.
/// </para>
/// </remarks>
internal static class ApacheMd5Crypt
{
    private const string Itoa64 = "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>Apache'nin özet biçimi: <c>$apr1$TUZ$OZET</c>.</summary>
    public const string Apr1Magic = "$apr1$";

    /// <summary>Klasik Unix md5crypt: <c>$1$TUZ$OZET</c>.</summary>
    public const string Md5Magic = "$1$";

    /// <summary>
    /// Verilen parolayı, <paramref name="hash"/> içindeki tuzla yeniden özetler ve karşılaştırır.
    /// Biçim bozuksa <c>false</c> döner — istisna atmaz.
    /// </summary>
    public static bool Verify(string hash, string password)
    {
        var magic = hash.StartsWith(Apr1Magic, StringComparison.Ordinal) ? Apr1Magic
            : hash.StartsWith(Md5Magic, StringComparison.Ordinal) ? Md5Magic
            : null;
        if (magic is null)
        {
            return false;
        }

        var govde = hash[magic.Length..];
        var ayirici = govde.IndexOf('$');
        if (ayirici <= 0)
        {
            return false;
        }

        var tuz = govde[..ayirici];
        if (tuz.Length > 8)
        {
            // Apache tuzu 8 karakterle sınırlar; uzun tuz KIRPILIR, reddedilmez.
            tuz = tuz[..8];
        }

        // Karşılaştırma KANONİK biçim üzerinden yapılır: tuz kırpılmış olabileceği için
        // saklanan dizgeyle doğrudan karşılaştırmak yanlış olurdu.
        var uretilen = Crypt(password, tuz, magic);
        var beklenen = magic + tuz + "$" + govde[(ayirici + 1)..];
        return BCryptVerifier.SabitZamanliEsit(uretilen, beklenen);
    }

    /// <summary>Özeti üretir (test ve doğrulama için; panel parola YAZARKEN bunu kullanmamalı).</summary>
    public static string Crypt(string password, string salt, string magic)
    {
        var pw = Encoding.UTF8.GetBytes(password);
        var sp = Encoding.UTF8.GetBytes(salt);
        var magicBytes = Encoding.ASCII.GetBytes(magic);

        byte[] final;
        using (var ctx1 = IncrementalHash.CreateHash(HashAlgorithmName.MD5))
        {
            ctx1.AppendData(pw);
            ctx1.AppendData(sp);
            ctx1.AppendData(pw);
            final = ctx1.GetHashAndReset();
        }

        using var ctx = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        ctx.AppendData(pw);
        ctx.AppendData(magicBytes);
        ctx.AppendData(sp);

        for (var pl = pw.Length; pl > 0; pl -= 16)
        {
            ctx.AppendData(final, 0, pl > 16 ? 16 : pl);
        }

        // Apache burada "final"i SIFIRLAR; sonraki döngü bu sıfır baytı kullanır.
        Array.Clear(final);

        // "Then something really weird..." (Apache'nin kendi yorumu): parolanın uzunluğunun
        // bitleri, sıfır baytı ile parolanın ilk baytı arasında seçim yaptırır.
        for (var i = pw.Length; i != 0; i >>= 1)
        {
            if ((i & 1) != 0)
            {
                ctx.AppendData(final, 0, 1);
            }
            else
            {
                ctx.AppendData(pw, 0, 1);
            }
        }

        final = ctx.GetHashAndReset();

        // 1000 tur: MD5'i kasten yavaşlatan kısım.
        using var dongu = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        for (var i = 0; i < 1000; i++)
        {
            if ((i & 1) != 0)
            {
                dongu.AppendData(pw);
            }
            else
            {
                dongu.AppendData(final);
            }

            if (i % 3 != 0)
            {
                dongu.AppendData(sp);
            }

            if (i % 7 != 0)
            {
                dongu.AppendData(pw);
            }

            if ((i & 1) != 0)
            {
                dongu.AppendData(final);
            }
            else
            {
                dongu.AppendData(pw);
            }

            final = dongu.GetHashAndReset();
        }

        var sonuc = new StringBuilder(magic).Append(salt).Append('$');
        To64(sonuc, (uint)((final[0] << 16) | (final[6] << 8) | final[12]), 4);
        To64(sonuc, (uint)((final[1] << 16) | (final[7] << 8) | final[13]), 4);
        To64(sonuc, (uint)((final[2] << 16) | (final[8] << 8) | final[14]), 4);
        To64(sonuc, (uint)((final[3] << 16) | (final[9] << 8) | final[15]), 4);
        To64(sonuc, (uint)((final[4] << 16) | (final[10] << 8) | final[5]), 4);
        To64(sonuc, final[11], 2);

        return sonuc.ToString();
    }

    private static void To64(StringBuilder hedef, uint deger, int adet)
    {
        for (var i = 0; i < adet; i++)
        {
            hedef.Append(Itoa64[(int)(deger & 0x3F)]);
            deger >>= 6;
        }
    }
}
