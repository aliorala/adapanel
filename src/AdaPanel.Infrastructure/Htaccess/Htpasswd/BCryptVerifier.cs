using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AdaPanel.Infrastructure.Htaccess;

namespace AdaPanel.Infrastructure.Htaccess.Htpasswd;

/// <summary>
/// bcrypt (<c>$2a$</c> / <c>$2b$</c> / <c>$2y$</c>) parola ÖZETİ DOĞRULAMASI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden kendi uygulamamız:</b> htpasswd dosyalarının bugünkü varsayılan biçimi bcrypt'tir
/// (<c>htpasswd -B</c>) ve .NET'te yerleşik bir bcrypt yoktur. Bu motor için depoya yeni bir NuGet
/// bağımlılığı EKLENMEDİ; algoritma OpenBSD'nin <c>blowfish.c</c>/<c>bcrypt.c</c> kaynağından
/// birebir yazıldı. Doğruluğu tahmine değil, YAYIMLANMIŞ test vektörlerine dayanır
/// (OpenWall bcrypt paketi + jBCrypt kümesi, <c>BCryptVerifierTests</c>).
/// </para>
/// <para>
/// <b>DOĞRULAR ve (2026-08 itibarıyla) ÜRETİR</b> — bkz. <see cref="Generate"/>. Üretim uzun süre
/// bilerek dışarıda bırakılmıştı ("panelin işi"); ölçülen sonuç, panelin özet hiç üretmemesi ve
/// korumalı dizin parolasını düz metin saklaması oldu. Doğrulayan ile üreten AYNI dosyada.
/// </para>
/// <para>
/// <b>Maliyet (cost) SINIRLIDIR.</b> <c>$2y$31$</c> tek doğrulamada dakikalarca CPU yer; kendi
/// parola dosyasını yükleyebilen bir kiracı bunu hizmet dışı bırakma silahına çevirirdi.
/// </para>
/// </remarks>
internal static class BCryptVerifier
{
    private const string B64 = "./ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const int SaltBytes = 16;
    private const int Words = 6;

    /// <summary>Doğrulamanın sonucu; başarısızlık sebebi ayrıştırılabilir olsun diye ayrı.</summary>
    internal enum Sonuc
    {
        Match,
        NoMatch,
        Malformed,
        CostTooHigh,
        UnsupportedVariant
    }

    public static Sonuc Verify(string hash, string password, int maxCost)
    {
        if (string.IsNullOrEmpty(hash) || hash.Length < 4 || hash[0] != '$' || hash[1] != '2')
        {
            return Sonuc.Malformed;
        }

        // Sürüm denetimi UZUNLUKTAN ÖNCE: "$2$05$..." biçimsel olarak kısa olsa da asıl sebep
        // desteklenmeyen SÜRÜMDÜR ve kullanıcıya bu söylenmelidir ("bozuk dosya" değil).
        var surum = hash[2];
        if (surum == '$')
        {
            // "$2$" (ilk sürüm) çok eski ve farklı anahtar uzunluğu kuralına sahip.
            return Sonuc.UnsupportedVariant;
        }

        if (surum is not ('a' or 'b' or 'y'))
        {
            // $2x$: 8-bit karakterlerde hatalı davranan, BİLEREK bozuk bırakılmış sürüm.
            // Taklit etmek yerine reddediyoruz; yanlış kabul etmek parola atlatması olurdu.
            return Sonuc.UnsupportedVariant;
        }

        if (hash.Length < 29 || hash[3] != '$' || !HtaccessPortableApi.AsciiRakamMi(hash[4]) || !HtaccessPortableApi.AsciiRakamMi(hash[5]) || hash[6] != '$')
        {
            return Sonuc.Malformed;
        }

        var maliyet = (hash[4] - '0') * 10 + (hash[5] - '0');
        if (maliyet is < 4 or > 31)
        {
            return Sonuc.Malformed;
        }

        if (maliyet > maxCost)
        {
            return Sonuc.CostTooHigh;
        }

        var govde = hash[7..];
        if (govde.Length < 22 + 31)
        {
            return Sonuc.Malformed;
        }

        var tuz = B64Decode(govde[..22], SaltBytes);
        if (tuz is null)
        {
            return Sonuc.Malformed;
        }

        var beklenen = govde[22..53];

        var anahtar = AnahtarBaytlari(password);
        var uretilen = Hesapla(tuz, anahtar, maliyet);

        return SabitZamanliEsit(uretilen, beklenen) ? Sonuc.Match : Sonuc.NoMatch;
    }

    /// <summary>
    /// Parolanın anahtar baytları: UTF-8 + sonlandırıcı NUL.
    /// </summary>
    /// <remarks>
    /// Anahtar akışı <c>Blowfish_expandstate</c> içinde YALNIZCA 18 kez (18*4 = 72 bayt) okunur;
    /// bu yüzden 72 baytın ötesi hiçbir zaman kullanılmaz. bcrypt'in meşhur "72 karakter sınırı"
    /// tam olarak buradan gelir ve ayrıca kırpmaya gerek bırakmaz.
    /// </remarks>
    private static byte[] AnahtarBaytlari(string password)
    {
        var baytlar = Encoding.UTF8.GetBytes(password);
        var uzunluk = Math.Min(baytlar.Length, 72);
        var anahtar = new byte[uzunluk + 1];
        Array.Copy(baytlar, anahtar, uzunluk);
        anahtar[uzunluk] = 0;
        return anahtar;
    }

    private static string Hesapla(byte[] tuz, byte[] anahtar, int maliyet)
    {
        var p = BlowfishInitialState.CopyP();
        var s = BlowfishInitialState.CopyS();

        ExpandState(p, s, tuz, anahtar);

        var turSayisi = 1u << maliyet;
        for (var k = 0u; k < turSayisi; k++)
        {
            Expand0State(p, s, anahtar);
            Expand0State(p, s, tuz);
        }

        // "OrpheanBeholderScryDoubt" — bcrypt'in sabit açık metni (24 bayt = 6 kelime).
        // u8 SONEKİ KULLANILMIYOR: o, ReadOnlySpan<byte> üretir ve Span tipleri net48'de yok
        // (bkz. HtaccessPortableApi). ASCII kodlaması AYNI 24 baytı verir — dizedeki her karakter
        // ASCII aralığındadır, yani UTF-8 ile birebir örtüşür.
        var acikMetin = Encoding.ASCII.GetBytes("OrpheanBeholderScryDoubt");
        var cdata = new uint[Words];
        var akisKonumu = 0;
        for (var i = 0; i < Words; i++)
        {
            cdata[i] = Stream2Word(acikMetin, ref akisKonumu);
        }

        for (var k = 0; k < 64; k++)
        {
            for (var i = 0; i < Words; i += 2)
            {
                Encipher(p, s, ref cdata[i], ref cdata[i + 1]);
            }
        }

        var ciktilar = new byte[Words * 4];
        for (var i = 0; i < Words; i++)
        {
            ciktilar[4 * i + 0] = (byte)(cdata[i] >> 24);
            ciktilar[4 * i + 1] = (byte)(cdata[i] >> 16);
            ciktilar[4 * i + 2] = (byte)(cdata[i] >> 8);
            ciktilar[4 * i + 3] = (byte)cdata[i];
        }

        // bcrypt son baytı ATAR: 24 bayttan 23'ü kodlanır (31 taban64 karakteri).
        return B64Encode(ciktilar, Words * 4 - 1);
    }

    // ------------------------------------------------------------ Blowfish çekirdeği

    private static uint F(uint[] s, uint x) =>
        ((s[(x >> 24) & 0xFF] + s[0x100 + ((x >> 16) & 0xFF)]) ^ s[0x200 + ((x >> 8) & 0xFF)])
        + s[0x300 + (x & 0xFF)];

    private static void Encipher(uint[] p, uint[] s, ref uint xl, ref uint xr)
    {
        var l = xl ^ p[0];
        var r = xr;

        for (var i = 1; i <= 16; i += 2)
        {
            r ^= F(s, l) ^ p[i];
            l ^= F(s, r) ^ p[i + 1];
        }

        xl = r ^ p[17];
        xr = l;
    }

    /// <summary>Bayt akışından döngüsel olarak 4 bayt okuyup büyük-uçlu bir kelime kurar.</summary>
    private static uint Stream2Word(byte[] veri, ref int konum)
    {
        uint gecici = 0;
        for (var i = 0; i < 4; i++, konum++)
        {
            if (konum >= veri.Length)
            {
                konum = 0;
            }

            gecici = (gecici << 8) | veri[konum];
        }

        return gecici;
    }

    private static void Expand0State(uint[] p, uint[] s, byte[] anahtar)
    {
        var j = 0;
        for (var i = 0; i < 18; i++)
        {
            p[i] ^= Stream2Word(anahtar, ref j);
        }

        uint datal = 0, datar = 0;
        for (var i = 0; i < 18; i += 2)
        {
            Encipher(p, s, ref datal, ref datar);
            p[i] = datal;
            p[i + 1] = datar;
        }

        for (var i = 0; i < 4; i++)
        {
            for (var k = 0; k < 256; k += 2)
            {
                Encipher(p, s, ref datal, ref datar);
                s[i * 256 + k] = datal;
                s[i * 256 + k + 1] = datar;
            }
        }
    }

    private static void ExpandState(uint[] p, uint[] s, byte[] veri, byte[] anahtar)
    {
        var j = 0;
        for (var i = 0; i < 18; i++)
        {
            p[i] ^= Stream2Word(anahtar, ref j);
        }

        j = 0;
        uint datal = 0, datar = 0;
        for (var i = 0; i < 18; i += 2)
        {
            datal ^= Stream2Word(veri, ref j);
            datar ^= Stream2Word(veri, ref j);
            Encipher(p, s, ref datal, ref datar);
            p[i] = datal;
            p[i + 1] = datar;
        }

        for (var i = 0; i < 4; i++)
        {
            for (var k = 0; k < 256; k += 2)
            {
                datal ^= Stream2Word(veri, ref j);
                datar ^= Stream2Word(veri, ref j);
                Encipher(p, s, ref datal, ref datar);
                s[i * 256 + k] = datal;
                s[i * 256 + k + 1] = datar;
            }
        }
    }

    // ------------------------------------------------------------ bcrypt taban64

    /// <summary>bcrypt'in KENDİ taban64 alfabesi — standart Base64 DEĞİLDİR.</summary>
    private static byte[]? B64Decode(string metin, int beklenenBayt)
    {
        var cikti = new byte[beklenenBayt];
        var yazilan = 0;
        var i = 0;

        while (yazilan < beklenenBayt && i < metin.Length)
        {
            var c1 = Char64(metin[i++]);
            var c2 = i < metin.Length ? Char64(metin[i++]) : -1;
            if (c1 < 0 || c2 < 0)
            {
                return null;
            }

            cikti[yazilan++] = (byte)((c1 << 2) | ((c2 & 0x30) >> 4));
            if (yazilan >= beklenenBayt || i >= metin.Length)
            {
                break;
            }

            var c3 = Char64(metin[i++]);
            if (c3 < 0)
            {
                return null;
            }

            cikti[yazilan++] = (byte)(((c2 & 0x0F) << 4) | ((c3 & 0x3C) >> 2));
            if (yazilan >= beklenenBayt || i >= metin.Length)
            {
                break;
            }

            var c4 = Char64(metin[i++]);
            if (c4 < 0)
            {
                return null;
            }

            cikti[yazilan++] = (byte)(((c3 & 0x03) << 6) | c4);
        }

        return yazilan == beklenenBayt ? cikti : null;
    }

    private static string B64Encode(byte[] veri, int uzunluk)
    {
        var sonuc = new StringBuilder();
        var i = 0;

        while (i < uzunluk)
        {
            int c1 = veri[i++];
            sonuc.Append(B64[(c1 >> 2) & 0x3F]);
            c1 = (c1 & 0x03) << 4;
            if (i >= uzunluk)
            {
                sonuc.Append(B64[c1 & 0x3F]);
                break;
            }

            int c2 = veri[i++];
            c1 |= (c2 >> 4) & 0x0F;
            sonuc.Append(B64[c1 & 0x3F]);
            c1 = (c2 & 0x0F) << 2;
            if (i >= uzunluk)
            {
                sonuc.Append(B64[c1 & 0x3F]);
                break;
            }

            c2 = veri[i++];
            c1 |= (c2 >> 6) & 0x03;
            sonuc.Append(B64[c1 & 0x3F]);
            sonuc.Append(B64[c2 & 0x3F]);
        }

        return sonuc.ToString();
    }

    private static int Char64(char k)
    {
        var index = B64.IndexOf(k);
        return index;
    }

    /// <summary>
    /// YENİ bir bcrypt özeti üretir (<c>$2y$</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sınıf yorumu "üretmez" diyordu ve o cümle artık DOĞRU DEĞİL — sebebi ölçüldü:</b>
    /// korumalı dizin parolası, üretilecek bir özet olmadığı için panelde
    /// <c>base64(kullanıcı:parola)</c> olarak müşterinin kendi site köküne yazılıyordu ve
    /// çözülünce düz metin çıkıyordu (bu turda canlı ölçüldü). "Üretmek panelin işi" cümlesinin
    /// bedeli, panelin hiç üretmemesi oldu. Üretim burada durur çünkü Blowfish çekirdeği
    /// (<see cref="Hesapla"/>, <see cref="B64Encode"/>) burada; ikinci bir yere yazmak, doğrulayan
    /// ile üretenin ayrı düşmesi demekti — bu sınıfta yapılabilecek en pahalı hata odur.
    /// </para>
    /// <para>
    /// <b>Tuz KRİPTOGRAFİK kaynaktan gelir</b> (<see cref="RandomNumberGenerator"/>, net48'de de
    /// var). <c>Random</c> kullanılsaydı aynı saniyede kurulan iki koruma aynı tuzu alabilir ve
    /// tuzun tek işlevi (önceden hesaplanmış tabloları kırmak) ortadan kalkardı.
    /// </para>
    /// <para>
    /// <b>Sürüm <c>$2y$</c>:</b> <see cref="Verify"/>'ın kabul ettiği üç sürümden biri ve Apache
    /// <c>htpasswd -B</c>'nin ürettiği sürüm. Ürettiğimiz özet, standart <c>htpasswd</c> ile
    /// üretilmiş bir dosyadan AYIRT EDİLEMEZ olmalı — yönetici dosyayı kendi araçlarıyla
    /// onarabilmeli.
    /// </para>
    /// </remarks>
    /// <param name="cost">
    /// 2'nin kuvveti olarak tur sayısı. <see cref="Verify"/> ile aynı sınırlar geçerlidir
    /// (4..31) ve çağıran taraf ayrıca <c>HtaccessLimits.MaxBcryptCost</c>'u aşmamalıdır —
    /// aşarsa ürettiğimiz özeti KENDİ doğrulayıcımız reddederdi.
    /// </param>
    internal static string Generate(string password, int cost)
    {
        if (cost is < 4 or > 31)
        {
            // Sessizce kırpmak, "maliyet 12 istedim" diyen çağırana 4 vermek olurdu.
            throw new ArgumentOutOfRangeException(nameof(cost), cost, "bcrypt maliyeti 4..31 aralığında olmalı.");
        }

        var tuz = new byte[SaltBytes];
        using (var rastgele = RandomNumberGenerator.Create())
        {
            rastgele.GetBytes(tuz);
        }

        var govde = Hesapla(tuz, AnahtarBaytlari(password ?? string.Empty), cost);

        // Biçim: $2y$<iki haneli maliyet>$<22 karakter tuz><31 karakter özet>.
        // Maliyet İKİ HANE olmak ZORUNDA (Verify hash[4] ve hash[5]'i okuyor); "$2y$9$..." yazmak
        // kendi doğrulayıcımızın Malformed demesine yol açardı.
        return "$2y$" + cost.ToString("00", CultureInfo.InvariantCulture) + "$" + B64Encode(tuz, SaltBytes) + govde;
    }

    /// <summary>
    /// Parola karşılaştırması SABİT ZAMANLIDIR: erken çıkış, karakter karakter deneme yaparak
    /// özet tahmin etmeye yarayan bir zamanlama kanalı açardı.
    /// </summary>
    internal static bool SabitZamanliEsit(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        var fark = 0;
        for (var i = 0; i < a.Length; i++)
        {
            fark |= a[i] ^ b[i];
        }

        return fark == 0;
    }
}
