using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Infrastructure.Ssl;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// SSL modulunun "niyeti gercek sanma" hatalarindan kalan ucu.
/// <para>
/// BULUNAN HATA-1: sertifikanin kaynagi <c>FriendlyName</c> icinde "Let's Encrypt" aranarak
/// bulunuyordu. O adi win-acme yazar ve bicimi "[win-acme] example.com @ ..." oldugu icin kosul
/// HICBIR ZAMAN dogru olmuyordu: gercek bir Let's Encrypt sertifikasi panelde "self-signed"
/// gorunuyor, kullanici sertifikanin alinmadigini sanip tekrar deniyor ve Let's Encrypt'in
/// haftalik limitini yakiyordu.
/// </para>
/// <para>
/// BULUNAN HATA-2: <c>IsHttpsBindingPresent</c> sabit <c>true</c> yaziliyordu, yani hic
/// olculmuyordu. Site sagligi ekrani 443 el sikismasi basarisizken bile "https binding=True"
/// diyerek kullaniciyi yanlis yone gonderiyordu.
/// </para>
/// </summary>
public sealed class SslCertificateSourceTests
{
    /// <summary>
    /// Kendinden imzalinin TANIMI: veren = konu. FriendlyName ne yazarsa yazsin sonuc degismez.
    /// </summary>
    [Fact]
    public void ASelfSignedCertificate_IsRecognisedRegardlessOfItsFriendlyName()
    {
        using var sertifika = UretSelfSigned("ornek.com");

        Assert.Equal("self-signed", WinAcmeSslService.ResolveCertificateSource(sertifika));
    }

    /// <summary>
    /// win-acme'nin yazdigi ad ("[win-acme] ...") kaynagi belirlemez; belirleyen VERENDIR.
    /// Bu test, eski FriendlyName mantiginin geri gelmesini engeller: o mantik bu sertifikaya
    /// da "self-signed" derdi, cunku adin icinde "Let's Encrypt" gecmez.
    /// </summary>
    [Fact]
    public void TheWinAcmeFriendlyName_DoesNotDecideTheSource()
    {
        using var sertifika = UretSelfSigned("ornek.com");
        // Not: FriendlyName yalnizca Windows'ta yazilabilir; testler zaten net8.0-windows hedefinde.
        sertifika.FriendlyName = "[win-acme] ornek.com @ 2026-08-05";

        // Veren = konu oldugu icin hala self-signed. Onemli olan, kararin ADA gore verilmemesi.
        Assert.Equal("self-signed", WinAcmeSslService.ResolveCertificateSource(sertifika));
    }

    /// <summary>
    /// Baska bir otorite imzaladiysa "self-signed" DENMEZ: tarayici o sertifikaya guvenebilir ve
    /// paneli "guvensiz" diye uyarmak yanlis alarm olurdu.
    /// </summary>
    [Fact]
    public void ACertificateSignedByAnAuthority_IsNotCalledSelfSigned()
    {
        using var otorite = UretOtorite("Ornek CA");
        using var sertifika = UretOtoriteyleImzali(otorite, "ornek.com");

        Assert.Equal("manual", WinAcmeSslService.ResolveCertificateSource(sertifika));
    }

    /// <summary>Let's Encrypt zinciri VEREN alanindan taninir.</summary>
    [Fact]
    public void ALetsEncryptChain_IsRecognisedFromTheIssuer()
    {
        using var otorite = UretOtorite("Let's Encrypt Authority X3");
        using var sertifika = UretOtoriteyleImzali(otorite, "ornek.com");

        Assert.Equal("letsencrypt", WinAcmeSslService.ResolveCertificateSource(sertifika));
    }

    /// <summary>
    /// "Olculemedi" ayri bir durum: yeni bir DTO hicbir sey IDDIA ETMEMELI. Eskiden bu alan
    /// sabit true yaziliyordu ve panel olcmedigi bir seyi "var" diye raporluyordu.
    /// </summary>
    [Fact]
    public void ANewCertificateRecord_DoesNotClaimAnHttpsBinding()
    {
        Assert.Null(new SslCertificateDto().IsHttpsBindingPresent);
    }

    /// <summary>
    /// Kendinden imzaliya DUSULDUGUNDE sebep tasinmali; bos bir sebep, sessiz dusus demektir.
    /// </summary>
    [Fact]
    public void AFallbackReason_CanBeCarriedOnTheRecord()
    {
        var kayit = new SslCertificateDto { Source = "self-signed", FallbackReason = "ssl.fallback.acme_failed" };

        Assert.NotEmpty(kayit.FallbackReason);
    }

    /// <summary>
    /// Tekil sertifika yolundaki sure siniri, mesru bir yayinlamayi ortasinda kesmemeli.
    /// 120 saniye COK KISAYDI: dogrulama DNS yayilmasini ve yeniden denemeleri icerebilir; sure
    /// dolunca surec olduruluyor, sessizce kendinden imzali uretiliyor ve islem BASARILI donuyordu.
    /// </summary>
    [Fact]
    public void TheSingleCertificatePath_DoesNotCutOffAnHonestIssuanceAfterTwoMinutes()
    {
        var kaynak = OkuKaynak();

        Assert.DoesNotContain("timeout: TimeSpan.FromSeconds(120)", kaynak, StringComparison.Ordinal);
    }

    private static X509Certificate2 UretSelfSigned(string host)
    {
        using var rsa = RSA.Create(2048);
        var istek = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return istek.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static X509Certificate2 UretOtorite(string ad)
    {
        using var rsa = RSA.Create(2048);
        var istek = new CertificateRequest($"CN={ad}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        istek.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return istek.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    }

    private static X509Certificate2 UretOtoriteyleImzali(X509Certificate2 otorite, string host)
    {
        using var rsa = RSA.Create(2048);
        var istek = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        istek.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        var seri = new byte[8];
        RandomNumberGenerator.Fill(seri);
        return istek.Create(otorite, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(89), seri);
    }

    private static string OkuKaynak()
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            var aday = Path.Combine(klasor, "src", "AdaPanel.Infrastructure", "Ssl", "WinAcmeSslService.cs");
            if (File.Exists(aday))
            {
                return File.ReadAllText(aday);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new FileNotFoundException("WinAcmeSslService.cs");
    }
}
