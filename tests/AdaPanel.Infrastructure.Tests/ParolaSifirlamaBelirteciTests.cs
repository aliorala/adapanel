using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// PAROLA SIFIRLAMA BELİRTECİNİN DİSİPLİNİ.
/// </summary>
/// <remarks>
/// <para>
/// Bu akış bu tura kadar HİÇ test edilmemişti. Ölçülen ve burada çivilenen kurallar: belirteç
/// kriptografik rastgeledir ve dosyada yalnız SHA-256 özeti durur (<see cref="SingleUseTokenStore"/>),
/// TEK KULLANIMLIKTIR, süresi vardır ve kullanıldığında kullanıcının API anahtarları düşer.
/// </para>
/// <para>
/// <b>Düzeltilen davranış:</b> parola kuralı belirteç TÜKETİLDİKTEN sonra uygulanıyordu. Yeni
/// parolayı kısa yazan kullanıcı hem hata alıyor hem de sıfırlama bağlantısını YAKIYORDU —
/// "tekrar deneyin" diyen ekranın arkasında link ölmüştü ve yeni bir e-posta istemek gerekiyordu.
/// </para>
/// </remarks>
public sealed class ParolaSifirlamaBelirteciTests
{
    private const string YeniParola = "Yeni!Parola12345";

    [Fact]
    public async Task Gecerli_belirtecle_parola_DEGISIR()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync();
        var belirtec = await BelirtecUretAsync(duzenek);

        await duzenek.Servis.ResetPasswordAsync(belirtec, YeniParola);

        var taze = await duzenek.TazeleAsync();
        Assert.NotEqual(duzenek.IlkKarma, taze.PasswordHash);
        Assert.Contains("auth.password_reset.completed", duzenek.Denetim.Actions);
    }

    [Fact]
    public async Task Belirtec_IKINCI_kez_calismaz()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync();
        var belirtec = await BelirtecUretAsync(duzenek);

        await duzenek.Servis.ResetPasswordAsync(belirtec, YeniParola);

        var hata = await Assert.ThrowsAsync<AppException>(() =>
            duzenek.Servis.ResetPasswordAsync(belirtec, "Baska!Parola12345"));
        Assert.Equal("auth.reset_token_invalid", hata.MessageKey);
    }

    [Fact]
    public async Task Suresi_gecmis_belirtec_REDDEDILIR()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync();
        var belirtec = await BelirtecUretAsync(duzenek, TimeSpan.FromSeconds(-1));

        var hata = await Assert.ThrowsAsync<AppException>(() =>
            duzenek.Servis.ResetPasswordAsync(belirtec, YeniParola));
        Assert.Equal("auth.reset_token_invalid", hata.MessageKey);
    }

    [Fact]
    public async Task Bilinmeyen_belirtec_REDDEDILIR()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync();
        await BelirtecUretAsync(duzenek);

        var hata = await Assert.ThrowsAsync<AppException>(() =>
            duzenek.Servis.ResetPasswordAsync("uydurma-belirtec", YeniParola));
        Assert.Equal("auth.reset_token_invalid", hata.MessageKey);
    }

    /// <summary>Belirtecin KENDİSİ diske yazılmaz; yalnız özeti durur.</summary>
    [Fact]
    public async Task Belirtec_dosyada_DUZ_METIN_durmaz()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync();
        var belirtec = await BelirtecUretAsync(duzenek);

        var icerik = await File.ReadAllTextAsync(duzenek.DosyaDeposu.ResolvePath(DepoYolu));

        Assert.DoesNotContain(belirtec, icerik, StringComparison.Ordinal);
    }

    /// <summary>
    /// KISA PAROLA BELİRTECİ YAKMAZ: kural belirteç tüketilmeden ÖNCE uygulanır, kullanıcı aynı
    /// bağlantıyla tekrar deneyebilir.
    /// </summary>
    [Fact]
    public async Task Kisa_parola_belirteci_TUKETMEZ()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync();
        var belirtec = await BelirtecUretAsync(duzenek);

        var hata = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            duzenek.Servis.ResetPasswordAsync(belirtec, "kisa"));
        Assert.Equal("users.password_too_short", hata.Message);

        // AYNI bağlantı hâlâ çalışıyor olmalı.
        await duzenek.Servis.ResetPasswordAsync(belirtec, YeniParola);
        Assert.NotEqual(duzenek.IlkKarma, (await duzenek.TazeleAsync()).PasswordHash);
    }

    /// <summary>
    /// SIFIRLAMA API ANAHTARLARINI DÜŞÜRÜR: parola kurtarıldıysa hesabın ele geçirilmiş olabileceği
    /// varsayılır; anahtarlar düşmezse saldırgan erişimini sürdürür.
    /// </summary>
    [Fact]
    public async Task Sifirlama_API_anahtarlarini_dusurur()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync();
        var belirtec = await BelirtecUretAsync(duzenek);

        await duzenek.Servis.ResetPasswordAsync(belirtec, YeniParola);

        Assert.Equal(1, duzenek.AnahtarServisi.IptalCagrisi);
    }

    /// <summary>Sıfırlama giriş KİLİDİNİ de açar; yoksa kişi yeni parolayla da giremezdi.</summary>
    [Fact]
    public async Task Sifirlama_giris_KILIDINI_acar()
    {
        await using var duzenek = await GirisDuzenegi.OlusturAsync(kilitDakika: 15);
        var belirtec = await BelirtecUretAsync(duzenek);

        await duzenek.Servis.ResetPasswordAsync(belirtec, YeniParola);

        var taze = await duzenek.TazeleAsync();
        Assert.Null(taze.LockedUntilUtc);
        Assert.Equal(0, taze.FailedLoginCount);
    }

    // --- yardımcılar ---

    private const string DepoYolu = "config/password-resets.json";

    /// <summary>
    /// Belirteci ORTAK depodan üretir. E-posta gönderimi üzerinden gitmek SMTP sahtesi gerektirirdi
    /// ve ölçülmek istenen şey e-posta değil, belirtecin kendi disiplini.
    /// </summary>
    private static async Task<string> BelirtecUretAsync(GirisDuzenegi duzenek, TimeSpan? omur = null)
    {
        var (belirtec, _) = await SingleUseTokenStore.IssueAsync(
            duzenek.DosyaDeposu,
            DepoYolu,
            duzenek.Kullanici.Id,
            omur ?? TimeSpan.FromHours(1),
            CancellationToken.None);
        return belirtec;
    }
}
