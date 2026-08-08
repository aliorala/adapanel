using System.ComponentModel.DataAnnotations;
using System.Reflection;
using AdaPanel.Core.Dtos.Auth;
using AdaPanel.Infrastructure.Services;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// 2FA KURTARMA KODLARI MODEL DOGRULAMASINA TAKILIYORDU -- ozellik fiilen oluydu.
/// </summary>
/// <remarks>
/// <para>
/// <c>LoginRequest.TwoFactorCode</c> uzerindeki desen <c>^[0-9]{6,16}$</c> idi: yalnizca rakam.
/// Oysa <c>AuthService.GenerateRecoveryCode</c> kodlari
/// <c>ABCDEFGHJKLMNPQRSTUVWXYZ23456789</c> alfabesinden <c>XXXXX-XXXXX</c> biciminde uretir --
/// yani neredeyse her zaman harf, her zaman tire icerir.
/// </para>
/// <para>
/// Sonuc: kurtarma kodu istegi modele takiliyor, 400 <c>validation.failed</c> donuyor ve
/// <c>TryConsumeRecoveryCodeAsync</c> HIC CAGRILMIYORDU. Kimlik dogrulayicisini kaybeden musteri
/// panele giremiyordu -- kurtarma kodlari tam da bunun icin var.
/// </para>
/// <para>
/// Bu testler kodu TAHMIN ETMEZ: <c>GenerateRecoveryCode</c> yansima ile CAGRILIR ve uretilen
/// gercek kodlar dogrulanir.
/// </para>
/// </remarks>
public sealed class TwoFactorRecoveryCodeValidationTests
{
    /// <summary>
    /// Uretilen GERCEK kurtarma kodlari model dogrulamasindan gecmeli.
    /// </summary>
    /// <remarks>
    /// Tek bir ornekle yetinilmez: alfabe rastgele, uzunluk sabit. 200 kod uretilir; biri bile
    /// reddedilirse o musteri panele giremez demektir.
    /// </remarks>
    [Fact]
    public void Uretilen_kurtarma_kodlari_model_dogrulamasindan_gecer()
    {
        for (var deneme = 0; deneme < 200; deneme++)
        {
            var kod = KurtarmaKoduUret();

            var hatalar = Dogrula(kod);

            Assert.True(
                hatalar.Count == 0,
                $"Kurtarma kodu '{kod}' model dogrulamasina takildi: {string.Join(" | ", hatalar)}");
        }
    }

    /// <summary>
    /// Uretilen kodun BICIMI olculur: desenin sinirlari tahminle degil, olcumle konur.
    /// </summary>
    [Fact]
    public void Kurtarma_kodunun_bicimi_olculdu()
    {
        var kod = KurtarmaKoduUret();

        // XXXXX-XXXXX => 11 karakter, ortada tire.
        Assert.Equal(11, kod.Length);
        Assert.Equal('-', kod[5]);
        Assert.All(kod.Where(k => k != '-'), k => Assert.Contains(k, "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"));

        // Tiresiz yazan kullanici da gecmeli (AuthService.NormalizeRecoveryCode zaten harf/rakam
        // disini atiyor) -- 10 karakter de 6..16 araligindadir.
        Assert.Empty(Dogrula(kod.Replace("-", string.Empty)));
    }

    /// <summary>
    /// TOTP kodlari (6 hane) kirilmadan gecmeye devam etmeli.
    /// </summary>
    /// <remarks>
    /// Deseni genisletirken asil isi bozmamak gerekir; bu test onu sabitler.
    /// </remarks>
    [Fact]
    public void Totp_kodu_gecmeye_devam_eder()
    {
        Assert.Empty(Dogrula("123456"));
        Assert.Empty(Dogrula("0000000000000000"));
    }

    /// <summary>
    /// Desen GENISLETILDI ama SERBEST BIRAKILMADI.
    /// </summary>
    /// <remarks>
    /// "Her seyi kabul et" bir duzeltme degil, dogrulamayi kaldirmak olurdu. Bosluk, isaret ve
    /// sinir disi uzunluklar hala reddedilmeli.
    /// </remarks>
    [Theory]
    [InlineData("12345")]                    // cok kisa
    [InlineData("12345678901234567")]        // cok uzun (17)
    [InlineData("ABCDE FGHIJ")]              // bosluk
    [InlineData("ABCDE_FGHIJ")]              // alt cizgi
    [InlineData("<script>x</script>")]       // isaret
    [InlineData("ABCDE'FGHIJ")]              // tirnak
    public void Gecersiz_bicimler_hala_reddedilir(string kod)
    {
        Assert.NotEmpty(Dogrula(kod));
    }

    // --- duzenek ---

    private static string KurtarmaKoduUret()
    {
        var metot = typeof(AuthService).GetMethod(
            "GenerateRecoveryCode",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(metot);
        var kod = metot!.Invoke(null, null) as string;
        Assert.False(string.IsNullOrWhiteSpace(kod), "GenerateRecoveryCode bos kod uretti.");
        return kod!;
    }

    private static List<string> Dogrula(string twoFactorCode)
    {
        var istek = new LoginRequest
        {
            UserNameOrEmail = "admin",
            Password = "GecerliParola123!",
            TwoFactorCode = twoFactorCode
        };

        var sonuclar = new List<ValidationResult>();
        Validator.TryValidateObject(istek, new ValidationContext(istek), sonuclar, validateAllProperties: true);

        return sonuclar
            .Where(sonuc => sonuc.MemberNames.Contains(nameof(LoginRequest.TwoFactorCode)))
            .Select(sonuc => sonuc.ErrorMessage ?? "?")
            .ToList();
    }
}
