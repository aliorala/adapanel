namespace AdaPanel.Core.Interfaces;

public interface ITotpService
{
    string GenerateSecret();

    string BuildProvisioningUri(string issuer, string accountName, string secret);

    /// <summary>
    /// Kodu doğrular ve EŞLEŞEN ZAMAN ADIMINI döner (geçersizse <c>null</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden <c>bool</c> değil:</b> TOTP kodu kabul penceresi boyunca (30 sn'lik adım, ±1 adım
    /// tolerans = 90 saniye) DEFALARCA geçerlidir. Yalnız "geçerli mi" sorulduğunda, bir kez
    /// kullanılmış kod tekrar kullanılabilir kalır — omuz üstünden okunan, ekran görüntüsünden
    /// alınan ya da bir kimlik avı sayfasına girilen kod, saldırganın elinde o pencere boyunca
    /// çalışmaya devam ederdi. Kodu TEK KULLANIMLIK yapmanın tek yolu hangi adımın eşleştiğini
    /// bilmek ve o adımı tüketmektir; bu yüzden adım numarası çağırana dönülür.
    /// </para>
    /// <para>
    /// Yanında bir <c>bool</c> kolaylık metodu BİLEREK eklenmedi: iki metot olsaydı yeni bir çağrı
    /// yolu kolay olanı seçer ve tekrar oynatma koruması o yolda sessizce yok olurdu.
    /// </para>
    /// </remarks>
    long? VerifyCode(string secret, string code);
}
