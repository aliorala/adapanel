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

    // ---------------------------------------------------------- net48 KARSILIGININ ESDEGERLIGI

    /// <summary>
    /// <see cref="PathContainment.PortableIsPathFullyQualified"/>, BCL'in
    /// <see cref="Path.IsPathFullyQualified(string)"/> üyesiyle AYNI cevabı vermek zorunda.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden bu test var:</b> bu dosya (<c>PathContainment.cs</c>) iki hedefte derleniyor —
    /// panel için <c>net8.0-windows</c>, IIS <c>.htaccess</c> modülü için <c>net48</c>.
    /// <c>Path.IsPathFullyQualified</c> net48'de YOK, bu yüzden orada elle yazılmış karşılık
    /// çalışıyor. Bu sınıf panelin çapraz-kiracı savunmasının tek ortak parçası olduğu için
    /// "karşılık aynıdır" cümlesi VARSAYIM OLARAK BIRAKILAMAZ: iki uygulamanın ayrıldığı tek bir
    /// girdi, IIS tarafında sessiz bir kapsam açığı demektir.
    /// </para>
    /// <para>
    /// Tablo tesadüfi değil: tam nitelikli olma kararının BÜTÜN dallarını gezer — sürücüye göreli
    /// (<c>C:x</c>), sürücüsüz kök (<c>\x</c>), UNC, aygıt yolu (<c>\\?\</c>), ters/düz eğik çizgi
    /// karışımı, sınır uzunlukları (0, 1, 2, 3 karakter) ve büyük/küçük sürücü harfi.
    /// </para>
    /// </remarks>
    [Theory]
    // --- tam nitelikli
    [InlineData(@"C:\sites\site1")]
    [InlineData(@"C:/sites/site1")]
    [InlineData(@"c:\x")]
    [InlineData(@"Z:\")]
    [InlineData(@"\\sunucu\pay\klasor")]
    [InlineData(@"//sunucu/pay")]
    [InlineData(@"\\?\C:\uzun\yol")]
    [InlineData(@"\\.\PhysicalDrive0")]
    // --- tam nitelikli DEGIL
    [InlineData("")]
    [InlineData("h")]
    [InlineData("httpdocs")]
    [InlineData(@"httpdocs\alt")]
    [InlineData(@"..\komsu")]
    [InlineData(@".\burasi")]
    [InlineData(@"\kokten-ama-surucusuz")]
    [InlineData(@"/kokten-ama-surucusuz")]
    [InlineData("C:")]
    [InlineData(@"C:x")]                 // SURUCUYE GORELI: o surucunun gecerli dizinine bagli
    [InlineData(@"C:httpdocs\alt")]
    [InlineData("1:\\x")]                // gecersiz surucu harfi
    [InlineData(@"::\x")]
    public void TASINABILIR_KARSILIK_BCL_ile_AYNI_CEVABI_VERIR(string yol)
    {
        Assert.Equal(Path.IsPathFullyQualified(yol), PathContainment.PortableIsPathFullyQualified(yol));
    }

    /// <summary>
    /// Eşdeğerlik ELLE SEÇİLMİŞ tabloyla sınırlı kalmasın: üretilen girdilerde de aynı cevap.
    /// </summary>
    /// <remarks>
    /// Elle yazılan tablo yalnızca DÜŞÜNÜLEN durumları kapsar. Asıl risk düşünülmeyen bileşimdir
    /// (örneğin <c>C:/</c> ile <c>C:\\</c> arasındaki fark, ya da iki nokta üst üstenin ikinci
    /// karakterden başka bir yerde olması). Bu yüzden ön ek/ayırıcı/sürücü parçaları çarpım
    /// hâlinde üretilip ikisi yan yana koşturuluyor.
    /// </remarks>
    [Fact]
    public void TASINABILIR_KARSILIK_URETILMIS_GIRDILERDE_de_AYRILMAZ()
    {
        string[] onekler = ["", @"\", "/", @"\\", "//", @"\\?\", "C:", "c:", @"C:\", "C:/", "1:", ":", "::"];
        string[] govdeler = ["", "x", "sites", @"sites\site1", "sites/site1", "..", ".", "?"];

        var ayrilan = new List<string>();

        foreach (var onek in onekler)
        {
            foreach (var govde in govdeler)
            {
                var yol = onek + govde;
                if (Path.IsPathFullyQualified(yol) != PathContainment.PortableIsPathFullyQualified(yol))
                {
                    ayrilan.Add(yol);
                }
            }
        }

        Assert.True(
            ayrilan.Count == 0,
            "net48 karsiligi BCL'den AYRILDI. Bu, IIS tarafinda sessiz bir kapsam farki demektir. "
            + "Ayrilan girdiler: " + string.Join(" | ", ayrilan));
    }
}
