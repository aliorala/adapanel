using System.Globalization;
using System.Security.Cryptography;

using AdaPanel.Core.Dtos.Auth;
using AdaPanel.Infrastructure.Services;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// İKİ ADIMLI DOĞRULAMA: KOD BİR KEZ ÇALIŞIR.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kapatılan açık:</b> TOTP kodu 30 saniyelik bir "adım"dan üretilir ve panel ±1 adım tolerans
/// uyguladığı için AYNI KOD 90 saniye boyunca geçerliydi. Doğrulama yalnız "geçerli mi" diye
/// sorduğundan, BİR KEZ KULLANILMIŞ kod o pencere içinde tekrar kullanılabiliyordu: kimlik avı
/// sayfasına girilen, omuz üstünden ya da ekran görüntüsünden okunan bir kodla saldırgan ikinci bir
/// oturum açabilirdi. İkinci faktörün vaadi "kod BİR KEZ işe yarasın"dır.
/// </para>
/// <para>
/// <b>TotpService'in kendisi bu turdan önce HİÇ test edilmemişti</b> (ne pencere genişliği, ne
/// kod üretimi, ne reddetme yolu). Aşağıdaki ilk blok o boşluğu da kapatıyor.
/// </para>
/// </remarks>
public sealed class TotpTekrarOynatmaTests
{
    // --- 1) TotpService: pencere ve doğrulama ---

    [Fact]
    public void Dogru_kod_kabul_edilir_ve_ADIM_donulur()
    {
        var servis = new TotpService();
        var sir = servis.GenerateSecret();
        var adim = SuAnkiAdim();

        var sonuc = servis.VerifyCode(sir, Kod(sir, adim));

        Assert.Equal(adim, sonuc);
    }

    [Fact]
    public void Yanlis_kod_REDDEDILIR()
    {
        var servis = new TotpService();
        var sir = servis.GenerateSecret();

        Assert.Null(servis.VerifyCode(sir, "000000"));
        Assert.Null(servis.VerifyCode(sir, "12345"));
        Assert.Null(servis.VerifyCode(sir, string.Empty));
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: saat kayması için ±1 adımlık tolerans KORUNMALI. Toleransı
    /// kaldırmak, sunucusuyla telefonu birkaç saniye kaymış her kullanıcıyı dışarıda bırakır.
    /// </summary>
    [Fact]
    public void Bir_onceki_ve_bir_sonraki_adimin_kodu_da_kabul_edilir()
    {
        var servis = new TotpService();
        var sir = servis.GenerateSecret();
        var adim = SuAnkiAdim();

        Assert.Equal(adim - 1, servis.VerifyCode(sir, Kod(sir, adim - 1)));
        Assert.Equal(adim + 1, servis.VerifyCode(sir, Kod(sir, adim + 1)));
    }

    /// <summary>Pencere SINIRLI: iki adım öteki kod artık geçerli değil.</summary>
    [Fact]
    public void Iki_adim_oteki_kod_REDDEDILIR()
    {
        var servis = new TotpService();
        var sir = servis.GenerateSecret();
        var adim = SuAnkiAdim();

        Assert.Null(servis.VerifyCode(sir, Kod(sir, adim - 2)));
        Assert.Null(servis.VerifyCode(sir, Kod(sir, adim + 2)));
    }

    // --- 2) Giriş akışı: tekrar oynatma ---

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: geçerli kodla giriş HÂLÂ çalışmalı. Tekrar oynatma korumasının en
    /// büyük riski, meşru kullanıcıyı da dışarıda bırakmasıdır.
    /// </summary>
    [Fact]
    public async Task Gecerli_TOTP_koduyla_giris_CALISIR()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync(ikiAdimli: true);

        var sonuc = await duzenek.Servis.LoginAsync(Istek(duzenek, Kod(duzenek.TotpSirri!, SuAnkiAdim())), "203.0.113.10");

        Assert.True(sonuc.IsAuthenticated);
    }

    /// <summary>KULLANILMIŞ KOD İKİNCİ KEZ ÇALIŞMAZ — düzeltmenin asıl amacı.</summary>
    [Fact]
    public async Task Ayni_TOTP_kodu_IKINCI_kez_calismaz()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync(ikiAdimli: true);
        var kod = Kod(duzenek.TotpSirri!, SuAnkiAdim());

        Assert.True((await duzenek.Servis.LoginAsync(Istek(duzenek, kod), "203.0.113.10")).IsAuthenticated);

        var hata = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            duzenek.Servis.LoginAsync(Istek(duzenek, kod), "203.0.113.10"));

        Assert.Equal("auth.invalid_two_factor_code", hata.Message);
    }

    /// <summary>
    /// TEKRAR OYNATMA, BAŞARISIZ DENEME SAYILIR: kilit sayacı ve IP sayacı beslenir, denetim
    /// kaydına düşer. Aksi hâlde saldırgan sınırsız deneyebilirdi.
    /// </summary>
    [Fact]
    public async Task Tekrar_oynatilan_kod_BASARISIZ_deneme_sayilir()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync(ikiAdimli: true);
        var kod = Kod(duzenek.TotpSirri!, SuAnkiAdim());

        await duzenek.Servis.LoginAsync(Istek(duzenek, kod), "203.0.113.10");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            duzenek.Servis.LoginAsync(Istek(duzenek, kod), "203.0.113.10"));

        Assert.Contains("auth.2fa.failed", duzenek.Denetim.Actions);
        Assert.Equal(1, duzenek.KabaKuvvet.Failures);
        Assert.Equal(1, (await duzenek.TazeleAsync()).FailedLoginCount);
    }

    /// <summary>
    /// DAHA ESKİ adımın kodu da düşer: yalnız "aynı kod" reddedilseydi, tolerans penceresi
    /// yüzünden hâlâ geçerli olan bir önceki adımın kodu kullanılabilir kalırdı.
    /// </summary>
    [Fact]
    public async Task Kullanilan_adimdan_ESKI_kod_da_REDDEDILIR()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync(ikiAdimli: true);
        var adim = SuAnkiAdim();

        Assert.True((await duzenek.Servis.LoginAsync(Istek(duzenek, Kod(duzenek.TotpSirri!, adim)), "203.0.113.10")).IsAuthenticated);

        var hata = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            duzenek.Servis.LoginAsync(Istek(duzenek, Kod(duzenek.TotpSirri!, adim - 1)), "203.0.113.10"));

        Assert.Equal("auth.invalid_two_factor_code", hata.Message);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETÇİSİ: BİR SONRAKİ adımın kodu hâlâ çalışır. Sayaç ileriye kapasaydı
    /// kullanıcı 30 saniye bekledikten sonra da giremezdi.
    /// </summary>
    [Fact]
    public async Task Kullanilan_adimdan_SONRAKI_kod_CALISIR()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync(ikiAdimli: true);
        var adim = SuAnkiAdim();

        await duzenek.Servis.LoginAsync(Istek(duzenek, Kod(duzenek.TotpSirri!, adim)), "203.0.113.10");

        var sonuc = await duzenek.Servis.LoginAsync(Istek(duzenek, Kod(duzenek.TotpSirri!, adim + 1)), "203.0.113.10");
        Assert.True(sonuc.IsAuthenticated);
    }

    /// <summary>
    /// KURTARMA KODLARINI YENİLEMEK TEKRAR OYNATMA SAYACINI SIFIRLAMAZ. İkisi aynı dosyada
    /// duruyor; üzerine düz yazılsaydı, kodları yenilemek az önce harcanmış bir TOTP kodunu
    /// yeniden geçerli kılardı.
    /// </summary>
    [Fact]
    public async Task Kurtarma_kodlarini_yenilemek_TOTP_sayacini_SIFIRLAMAZ()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync(ikiAdimli: true);
        var kod = Kod(duzenek.TotpSirri!, SuAnkiAdim());

        await duzenek.Servis.LoginAsync(Istek(duzenek, kod), "203.0.113.10");
        await duzenek.Servis.RegenerateRecoveryCodesAsync(duzenek.Kullanici.Id);

        var hata = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            duzenek.Servis.LoginAsync(Istek(duzenek, kod), "203.0.113.10"));

        Assert.Equal("auth.invalid_two_factor_code", hata.Message);
    }

    /// <summary>
    /// KURTARMA KODU KULLANMAK da sayacı sıfırlamaz (aynı dosya, ikinci yazma yolu).
    /// </summary>
    [Fact]
    public async Task Kurtarma_kodu_kullanmak_TOTP_sayacini_SIFIRLAMAZ()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync(ikiAdimli: true);
        var kodlar = await duzenek.Servis.RegenerateRecoveryCodesAsync(duzenek.Kullanici.Id);
        var totpKodu = Kod(duzenek.TotpSirri!, SuAnkiAdim());

        await duzenek.Servis.LoginAsync(Istek(duzenek, totpKodu), "203.0.113.10");
        Assert.True((await duzenek.Servis.LoginAsync(Istek(duzenek, kodlar.Codes.First()), "203.0.113.10")).IsAuthenticated);

        var hata = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            duzenek.Servis.LoginAsync(Istek(duzenek, totpKodu), "203.0.113.10"));

        Assert.Equal("auth.invalid_two_factor_code", hata.Message);
    }

    // --- yardımcılar ---

    private static LoginRequest Istek(GirisDuzenegi duzenek, string kod) => new()
    {
        UserNameOrEmail = duzenek.Kullanici.UserName,
        Password = GirisDuzenegi.DogruParola,
        TwoFactorCode = kod
    };

    private static long SuAnkiAdim() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

    /// <summary>
    /// RFC 6238 kodunu testin KENDİSİ üretir: doğrulayıcının kendi kodunu kendine doğrulatması
    /// hiçbir şey ölçmezdi (aynı hata iki yerde birden olurdu).
    /// </summary>
    private static string Kod(string base32Sir, long adim)
    {
        var anahtar = Base32Coz(base32Sir);
        Span<byte> zaman = stackalloc byte[8];
        var deger = adim;
        for (var i = 7; i >= 0; i--)
        {
            zaman[i] = (byte)(deger & 0xff);
            deger >>= 8;
        }

        using var hmac = new HMACSHA1(anahtar);
        var ozet = hmac.ComputeHash(zaman.ToArray());
        var kayma = ozet[^1] & 0x0f;
        var sayi = ((ozet[kayma] & 0x7f) << 24)
            | (ozet[kayma + 1] << 16)
            | (ozet[kayma + 2] << 8)
            | ozet[kayma + 3];

        return (sayi % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static byte[] Base32Coz(string girdi)
    {
        const string alfabe = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var temiz = girdi.Trim().TrimEnd('=').ToUpperInvariant();
        var cikti = new List<byte>();
        var tampon = 0;
        var bit = 0;
        foreach (var karakter in temiz)
        {
            tampon = (tampon << 5) | alfabe.IndexOf(karakter, StringComparison.Ordinal);
            bit += 5;
            if (bit >= 8)
            {
                cikti.Add((byte)((tampon >> (bit - 8)) & 0xff));
                bit -= 8;
            }
        }

        return [.. cikti];
    }
}
