using System.Reflection;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// MEVCUT PAROLAYI DOĞRULAYAN HER UÇ, PAROLA DENEME YÜZEYİDİR VE DAR BİR TAVANA BAĞLI OLMALI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ölçülen eksik:</b> <c>2fa/disable</c> mevcut parolayı doğruluyor ama yalnız küresel oturum
/// tavanının (600/dk) altındaydı. Yani çerezi ele geçiren biri, dakikada yüzlerce parola deneyerek
/// ikinci faktörü kaldırmayı zorlayabilirdi — ve ikinci faktörü kaldırmak, hesabı kalıcı olarak
/// devralmanın son adımıdır. <c>change-password</c> zaten dar politikaya bağlıydı; iki uç aynı işi
/// yaptığı hâlde farklı korunuyordu.
/// </para>
/// <para>
/// Nöbetçi TEK TEK uç saymıyor, KURALI yazıyor: "parolayı doğrulayan uç" listesi burada duruyor ve
/// yeni bir tanesi eklendiğinde bu dosyaya da eklenmesi gerekir. Sayıma dayalı koruma unutmayı
/// "açık" olarak cezalandırır; kurala dayalı koruma en fazla gereksiz sıkılık üretir.
/// </para>
/// </remarks>
public sealed class KimlikUclariHizSiniriTests
{
    [Theory]
    [InlineData(nameof(AuthController.ChangePasswordAsync))]
    [InlineData(nameof(AuthController.DisableTwoFactorAsync))]
    [InlineData(nameof(AuthController.SetUserPasswordAsync))]
    public void Parola_dogrulayan_uclar_DAR_politikaya_bagli(string eylemAdi)
    {
        var eylem = typeof(AuthController).GetMethod(eylemAdi, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(eylem);

        var politika = eylem!.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName;

        Assert.Equal(PanelRateLimits.PasswordChange, politika);
    }

    /// <summary>
    /// Giriş ucu (parola + iki adımlı kodun İKİSİNİ birden aldığı için 2FA kaba kuvvetinin de tek
    /// kapısı) giriş politikasına bağlı olmalı. Arayüz ikinci adımı AYRI bir uca değil, aynı
    /// isteğe koyuyor (frontend/src/stores/auth.ts) — yani 2FA denemelerinin tavanı burasıdır.
    /// </summary>
    [Fact]
    public void Giris_ucu_GIRIS_politikasina_bagli()
    {
        var eylem = typeof(AuthController).GetMethod(nameof(AuthController.LoginAsync));

        Assert.Equal(
            PanelRateLimits.Login,
            eylem!.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
    }

    /// <summary>Parola sıfırlama isteği ve tüketimi de giriş politikasının altında.</summary>
    [Theory]
    [InlineData(nameof(AuthController.ForgotPasswordAsync))]
    [InlineData(nameof(AuthController.ResetPasswordAsync))]
    public void Parola_sifirlama_uclari_sinirli(string eylemAdi)
    {
        var eylem = typeof(AuthController).GetMethod(eylemAdi);

        Assert.Equal(
            PanelRateLimits.Login,
            eylem!.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
    }
}
