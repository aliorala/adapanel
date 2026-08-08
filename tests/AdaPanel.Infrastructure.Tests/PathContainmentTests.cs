using AdaPanel.Core.Common;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// <see cref="PathContainment"/> panelin çapraz-kiracı savunmasının TEK ortak parçasıdır: belge kökü
/// (SitesController), FTP ana dizini (FtpController) ve benzeri her yerde "bu yol kiracının alanında
/// mı?" sorusunu bu sınıf cevaplar. Buradaki bir hata, tek tek üç açık demektir; bu yüzden saldırı
/// dizgeleri burada, tek yerde tutuluyor.
/// </summary>
public sealed class PathContainmentTests
{
    private const string Kok = @"C:\sites\site1";

    [Theory]
    // Ön ek eşleşmesi TUZAĞI: sadece StartsWith kullanılsaydı bunların hepsi "içeride" sayılırdı.
    [InlineData(@"C:\sites\site1-evil")]
    [InlineData(@"C:\sites\site10")]
    [InlineData(@"C:\sites\site1evil\httpdocs")]
    // Klasik dışarı çıkma
    [InlineData(@"C:\sites\site1\..\site2")]
    [InlineData(@"C:\sites\site1\..\..\Windows\System32")]
    [InlineData(@"C:\sites\site2")]
    [InlineData(@"C:\")]
    // Ayırıcı karıştırma ve fazlalık ayırıcılar normalleştirme ile kapanmalı
    [InlineData(@"C:/sites/site1/../site2")]
    [InlineData(@"C:\sites\.\site1\..\site2")]
    // Göreli yol: neye çözüleceği çalışma dizinine bağlıdır, sunucuda tahmin edilemez
    [InlineData("httpdocs")]
    [InlineData(@"..\site2")]
    // Biçimsel olarak kullanılamaz girdiler istisna değil, "hayır" üretmeli
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Kiracinin_alaninin_disindaki_yollar_reddedilir(string? aday)
    {
        Assert.False(PathContainment.IsInsideRoot(Kok, aday));
        Assert.False(PathContainment.IsRootOrInside(Kok, aday));
    }

    [Theory]
    [InlineData(@"C:\sites\site1\httpdocs")]
    [InlineData(@"C:\sites\site1\httpdocs\public")]
    [InlineData(@"C:/sites/site1/httpdocs")]
    [InlineData(@"C:\sites\site1\alt\..\httpdocs")]
    public void Kokun_altindaki_yollar_kabul_edilir(string aday)
    {
        Assert.True(PathContainment.IsInsideRoot(Kok, aday));
        Assert.True(PathContainment.IsRootOrInside(Kok, aday));
    }

    [Theory]
    [InlineData(@"C:\sites\site1")]
    [InlineData(@"C:\sites\site1\")]
    [InlineData(@"C:/sites/site1")]
    [InlineData(@"C:\SITES\SITE1")] // Windows yolları büyük/küçük harfe duyarsızdır
    public void Kokun_kendisi_IsRootOrInside_icin_gecerli_IsInsideRoot_icin_degildir(string aday)
    {
        // FTP ana dizini ve belge kökü için EN OLAĞAN seçim sitenin kökünün ta kendisidir; bunu
        // reddetmek meşru kullanımı kırar. Dosya hedefleri (betikler) içinse kök geçersizdir.
        Assert.True(PathContainment.IsRootOrInside(Kok, aday));
        Assert.False(PathContainment.IsInsideRoot(Kok, aday));
    }

    [Fact]
    public void Kok_gecersizse_hicbir_aday_gecerli_sayilmaz()
    {
        // Site kökü okunamadıysa (boş PhysicalPath) "her şeye izin ver" hâline DÜŞÜLMEMELİ.
        foreach (var gecersizKok in new string?[] { null, "", "   ", "httpdocs" })
        {
            Assert.False(PathContainment.IsInsideRoot(gecersizKok, @"C:\sites\site1\httpdocs"));
            Assert.False(PathContainment.IsRootOrInside(gecersizKok, @"C:\sites\site1\httpdocs"));
        }
    }
}
