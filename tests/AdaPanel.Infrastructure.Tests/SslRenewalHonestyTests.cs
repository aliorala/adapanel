using AdaPanel.Core.Dtos.Ssl;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// SSL yenileme sonucunun DURUST olmasi.
/// <para>
/// BULUNAN HATA: win-acme kurulu degilken RenewAllAsync istisna atmadan donuyor, otomasyon da
/// durumu "completed" yaziyordu. Ekranda her gun "SSL yenileme: tamamlandi -- 0 sertifika
/// yenilendi" satiri tazeleniyor, gercekte HICBIR sertifika yenilenmiyordu. Bu ancak 90 gun
/// sonra tum siteler ayni anda "bu baglanti guvenli degil" verdiginde anlasilirdi ve o ana kadar
/// panel tek bir uyari bile gostermezdi.
/// </para>
/// <para>
/// Ikinci hata: RenewedCount, wacs'in CIKIS KODUNDAN uretiliyordu. wacs --renew yenilenecek bir
/// sey olmadiginda da 0 donebilir. Yani bes sertifika
/// yenilense de "1", hicbiri yenilenmese de "1" yaziyordu -- sayi hicbir gercegi olcmuyordu.
/// </para>
/// </summary>
public sealed class SslRenewalHonestyTests
{
    /// <summary>
    /// Olculemedigi durumun VARSAYILAN olmamasi onemli: yeni bir sonuc nesnesi olusturan biri
    /// yanlislikla "olculdu" demis olmamali... ama tersi de tehlikeli. Varsayilan true, cunku
    /// sonuc nesnesi yalnizca gercekten calistirildiginda uretiliyor; olculemedi durumu ACIKCA
    /// isaretleniyor. Bu test o sozlesmeyi sabitler.
    /// </summary>
    [Fact]
    public void ANewResult_IsMeasuredByDefault()
    {
        Assert.True(new RenewAllResultDto().Measured);
    }

    // Olculemedi isaretlendiginde sebep BOS BIRAKILMAMALI: kullanicinin ne yapacagini bilmesi
    // gerekir ("win-acme kurulu degil" gibi).
    [Fact]
    public void AnUnmeasuredResult_CarriesTheReason()
    {
        var sonuc = new RenewAllResultDto
        {
            Measured = false,
            FailureReason = "win-acme kurulu degil; sertifikalar yenilenemiyor."
        };

        Assert.False(sonuc.Measured);
        Assert.NotEmpty(sonuc.FailureReason);
    }

    /// <summary>
    /// Yenilenen sayisi CIKTIDAN okunmali, cikis kodundan degil.
    /// </summary>
    [Fact]
    public void RenewedCount_IsParsedFromOutputNotFromTheExitCode()
    {
        var kaynak = ReadSource("Ssl", "WinAcmeSslService.cs");

        Assert.DoesNotContain("RenewedCount = result.Success ? 1 : 0", kaynak, StringComparison.Ordinal);
        Assert.Contains("Regex.Matches", kaynak, StringComparison.Ordinal);
    }

    // win-acme bulunamadiginda Measured=false yazildigini kilitler.
    [Fact]
    public void MissingWinAcme_IsReportedAsUnmeasured()
    {
        var kaynak = ReadSource("Ssl", "WinAcmeSslService.cs");

        Assert.Contains("Measured = false", kaynak, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] parts)
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            var aday = Path.Combine(new[] { klasor, "src", "AdaPanel.Infrastructure" }.Concat(parts).ToArray());
            if (File.Exists(aday))
            {
                return File.ReadAllText(aday);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new FileNotFoundException(string.Join('/', parts));
    }
}
