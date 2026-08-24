using System.Security.Cryptography;
using System.Text;
using AdaPanel.Core.Dtos.Htaccess;
using AdaPanel.Infrastructure.Htaccess;

namespace AdaPanel.Infrastructure.Htaccess.Htpasswd;

/// <summary>
/// Apache <c>.htpasswd</c> dosyasının okunması ve parola doğrulaması.
/// </summary>
/// <remarks>
/// <para>
/// <b>DESTEKLENEN biçimler:</b> bcrypt (<c>$2a$/$2b$/$2y$</c>), <c>$apr1$</c> (Apache MD5),
/// <c>$1$</c> (md5crypt), <c>{SHA}</c> (base64 SHA-1), <c>{SSHA}</c> (tuzlu SHA-1) ve
/// Apache'nin Windows'ta izin verdiği DÜZ METİN.
/// </para>
/// <para>
/// <b>DESTEKLENMEYEN biçimler bulgu üretir:</b> klasik DES <c>crypt()</c> (Unix'e özgü, .NET'te
/// karşılığı yok), <c>$5$</c>/<c>$6$</c> (SHA-256/512 crypt), <c>$2x$</c> (bilerek bozuk bırakılmış
/// bcrypt sürümü). Bunlar için doğrulama BAŞARISIZ döner — "sessizce kabul et" seçeneği bir parola
/// atlatmasıdır, "sessizce reddet" ise kullanıcının neden giremediğini bilememesidir; bu yüzden
/// reddedilir VE sebebi yazılır.
/// </para>
/// </remarks>
internal static class HtpasswdFile
{
    /// <summary>Doğrulama sonucu.</summary>
    internal sealed class Sonuc
    {
        public bool Succeeded { get; init; }
        public string FailureCode { get; init; } = string.Empty;
        public string FailureReason { get; init; } = string.Empty;
        public List<HtaccessDiagnostic> Diagnostics { get; } = [];
    }

    public static Sonuc Verify(
        IHtaccessFileSystem dosyaSistemi,
        string dosyaYolu,
        string kullanici,
        string parola,
        HtaccessLimits sinirlar)
    {
        var sonuc = new Sonuc { Succeeded = false };

        if (string.IsNullOrEmpty(dosyaYolu))
        {
            return Basarisiz(HtaccessDiagnosticCodes.AuthIncomplete,
                "AuthUserFile tanımlı değil ya da site kökünün dışında olduğu için reddedildi.", dosyaYolu, 0);
        }

        if (!dosyaSistemi.TryReadText(dosyaYolu, sinirlar.MaxAuthUserFileBytes, out var icerik, out var hata))
        {
            return Basarisiz(HtaccessDiagnosticCodes.AuthFileUnreadable,
                $"Parola dosyası okunamadı ({hata}).", dosyaYolu, 0);
        }

        var satirNo = 0;
        foreach (var hamSatir in icerik.Split('\n'))
        {
            satirNo++;
            var satir = hamSatir.Trim('\r', ' ', '\t');
            if (satir.Length == 0 || satir[0] == '#')
            {
                continue;
            }

            var ikiNokta = satir.IndexOf(':');
            if (ikiNokta <= 0)
            {
                continue;
            }

            var ad = satir[..ikiNokta];
            if (!string.Equals(ad, kullanici, StringComparison.Ordinal))
            {
                continue;
            }

            var ozet = satir[(ikiNokta + 1)..];
            var (gecti, kod, sebep) = OzetiDogrula(ozet, parola, sinirlar);
            if (gecti)
            {
                return new Sonuc { Succeeded = true };
            }

            return Basarisiz(kod, sebep, dosyaYolu, satirNo);
        }

        // Kullanıcı yoksa da "parola yanlış" denir: kullanıcı adı SIZDIRILMAZ.
        return Basarisiz(HtaccessDiagnosticCodes.AuthIncomplete, "kullanıcı adı ya da parola hatalı", dosyaYolu, 0);

        Sonuc Basarisiz(string kod, string mesaj, string dosya, int satir)
        {
            var yeni = new Sonuc { Succeeded = false, FailureCode = kod, FailureReason = mesaj };
            yeni.Diagnostics.Add(HtaccessParser.Bulgu(
                kod == HtaccessDiagnosticCodes.AuthIncomplete ? HtaccessDiagnosticLevel.Info : HtaccessDiagnosticLevel.Error,
                kod, dosya, satir, "AuthUserFile", mesaj));
            return yeni;
        }
    }

    /// <summary>Tek bir özet dizgesini doğrular.</summary>
    internal static (bool Gecti, string Kod, string Sebep) OzetiDogrula(
        string ozet, string parola, HtaccessLimits sinirlar)
    {
        if (ozet.StartsWith("$2", StringComparison.Ordinal))
        {
            var bcrypt = BCryptVerifier.Verify(ozet, parola, sinirlar.MaxBcryptCost);
            return bcrypt switch
            {
                BCryptVerifier.Sonuc.Match => (true, string.Empty, string.Empty),
                BCryptVerifier.Sonuc.NoMatch => (false, HtaccessDiagnosticCodes.AuthIncomplete, "kullanıcı adı ya da parola hatalı"),
                BCryptVerifier.Sonuc.CostTooHigh => (false, HtaccessDiagnosticCodes.UnsupportedHashScheme,
                    $"bcrypt maliyeti {sinirlar.MaxBcryptCost} sınırının üzerinde; tek doğrulama sunucuyu kilitleyeceği için REDDEDİLDİ. Parolayı daha düşük maliyetle yeniden üretin."),
                BCryptVerifier.Sonuc.UnsupportedVariant => (false, HtaccessDiagnosticCodes.UnsupportedHashScheme,
                    "bcrypt'in bu sürümü ($2$ / $2x$) desteklenmiyor; $2y$ ya da $2b$ ile yeniden üretin."),
                _ => (false, HtaccessDiagnosticCodes.UnsupportedHashScheme, "bcrypt özeti biçimsel olarak bozuk.")
            };
        }

        if (ozet.StartsWith(ApacheMd5Crypt.Apr1Magic, StringComparison.Ordinal) ||
            ozet.StartsWith(ApacheMd5Crypt.Md5Magic, StringComparison.Ordinal))
        {
            return ApacheMd5Crypt.Verify(ozet, parola)
                ? (true, string.Empty, string.Empty)
                : (false, HtaccessDiagnosticCodes.AuthIncomplete, "kullanıcı adı ya da parola hatalı");
        }

        if (ozet.StartsWith("{SSHA}", StringComparison.Ordinal))
        {
            return SaltedSha1(ozet[6..], parola)
                ? (true, string.Empty, string.Empty)
                : (false, HtaccessDiagnosticCodes.AuthIncomplete, "kullanıcı adı ya da parola hatalı");
        }

        if (ozet.StartsWith("{SHA}", StringComparison.Ordinal))
        {
            return Sha1(ozet[5..], parola)
                ? (true, string.Empty, string.Empty)
                : (false, HtaccessDiagnosticCodes.AuthIncomplete, "kullanıcı adı ya da parola hatalı");
        }

        if (ozet.StartsWith("$5$", StringComparison.Ordinal) || ozet.StartsWith("$6$", StringComparison.Ordinal))
        {
            return (false, HtaccessDiagnosticCodes.UnsupportedHashScheme,
                "SHA-256/SHA-512 crypt ($5$ / $6$) desteklenmiyor. Parolayı 'htpasswd -B' ile bcrypt olarak yeniden üretin.");
        }

        if (ozet.StartsWith("$", StringComparison.Ordinal))
        {
            return (false, HtaccessDiagnosticCodes.UnsupportedHashScheme,
                $"'{ozet.Split('$').ElementAtOrDefault(1)}' biçimindeki parola özeti tanınmıyor. Parolayı 'htpasswd -B' ile yeniden üretin.");
        }

        if (ozet.Length == 13 && ozet.All(k => HtaccessPortableApi.AsciiHarfVeyaRakamMi(k) || k is '.' or '/'))
        {
            return (false, HtaccessDiagnosticCodes.UnsupportedHashScheme,
                "Klasik Unix DES crypt() özeti desteklenmiyor (.NET'te karşılığı yok). Parolayı 'htpasswd -B' ile bcrypt olarak yeniden üretin.");
        }

        // Apache düz metin parolaları YALNIZCA Windows/NetWare/BeOS'ta kabul eder; burası Windows.
        return BCryptVerifier.SabitZamanliEsit(ozet, parola)
            ? (true, string.Empty, string.Empty)
            : (false, HtaccessDiagnosticCodes.AuthIncomplete, "kullanıcı adı ya da parola hatalı");
    }

    private static bool Sha1(string base64, string parola)
    {
        if (!TryBase64(base64, out var beklenen) || beklenen.Length != 20)
        {
            return false;
        }

        var uretilen = HtaccessPortableApi.Sha1(Encoding.UTF8.GetBytes(parola));
        return HtaccessPortableApi.SabitSuredeEsit(uretilen, beklenen);
    }

    private static bool SaltedSha1(string base64, string parola)
    {
        if (!TryBase64(base64, out var govde) || govde.Length <= 20)
        {
            return false;
        }

        // DIZI DILIMI (govde[..20]) YAZILMIYOR: dizi dilimleri derleyicinin
        // RuntimeHelpers.GetSubArray uyesini cagirmasini gerektirir ve o uye net48'de YOKTUR --
        // ayrica BCL tipine uye eklenemedigi icin telafi de edilemez (dize dilimleri farkli,
        // onlar dogrudan Substring'e cevriliyor ve iki hedefte de calisiyor).
        // Array.Copy AYNI baytlari verir.
        var beklenen = new byte[20];
        Array.Copy(govde, 0, beklenen, 0, 20);

        var tuzUzunlugu = govde.Length - 20;
        var parolaBaytlari = Encoding.UTF8.GetBytes(parola);

        // {SSHA} tanimi: SHA1(parola || tuz). Tuz, ozetin ARKASINDA saklanir.
        var girdi = new byte[parolaBaytlari.Length + tuzUzunlugu];
        Array.Copy(parolaBaytlari, 0, girdi, 0, parolaBaytlari.Length);
        Array.Copy(govde, 20, girdi, parolaBaytlari.Length, tuzUzunlugu);

        var uretilen = HtaccessPortableApi.Sha1(girdi);
        return HtaccessPortableApi.SabitSuredeEsit(uretilen, beklenen);
    }

    private static bool TryBase64(string metin, out byte[] baytlar)
    {
        try
        {
            baytlar = Convert.FromBase64String(metin);
            return true;
        }
        catch (FormatException)
        {
            baytlar = [];
            return false;
        }
    }
}
