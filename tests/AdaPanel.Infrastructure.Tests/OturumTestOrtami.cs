using System.Security.Claims;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Çerez doğrulayıcısını çalıştıran testlerin ORTAK kurulum parçaları.
/// </summary>
/// <remarks>
/// <para>
/// Panelde çerez doğrulaması üç ayrı test dosyasından çağrılıyor (oturum doğrulama, konsoldan
/// parola belirleme, yöneticinin başkasının parolasını belirlemesi) ve üçü de kendi
/// <c>CookieValidatePrincipalContext</c>'ini kuruyor. Doğrulayıcıya yeni bir ÖN KOŞUL eklendiğinde
/// (oturum kimliği talebi, kara liste servisi) üç kopyanın üçünü de güncellemek gerekiyor; biri
/// atlanırsa o dosyadaki testler "reddedildi" der ve yanlış sebeple yeşil/kırmızı olur. Ön koşulun
/// TEK tanımı burada.
/// </para>
/// </remarks>
internal static class OturumTestOrtami
{
    /// <summary>
    /// Çereze yazılan oturum kimliği talebi. Doğrulayıcı kimliği olmayan çerezi REDDEDER, yani bu
    /// talep olmadan hiçbir test geçerli bir oturumu ölçemez.
    /// </summary>
    public static Claim OturumKimligiTalebi(string? deger = null) =>
        new(SessionRevocationStore.SessionIdClaim, deger ?? SessionRevocationStore.NewSessionId());

    /// <summary>
    /// İçi BOŞ bir kara liste. Diskte dosya OLUŞTURMAZ: yalnız okuma yapılır ve dosya yoksa boş
    /// liste dönülür (bkz. <see cref="JsonFileStore.ReadAsync"/>), yani testler birbirinin durumunu
    /// da göremez.
    /// </summary>
    public static SessionRevocationStore BosKaraListe() => KaraListe(
        Path.Combine(Path.GetTempPath(), "adapanel-oturum-testi-okuma"));

    /// <summary>Verilen veri kökünü kullanan kara liste (çıkış YAZAN testler için).</summary>
    public static SessionRevocationStore KaraListe(string dataRoot) =>
        new(new JsonFileStore(
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot })));
}
