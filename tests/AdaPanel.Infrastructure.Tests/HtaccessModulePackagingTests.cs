using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// `.htaccess` IIS modülü PAKETE GİRMELİ.
///
/// NEDEN VAR — ölçülmüş, yaşanmış hata: modül `AdaPanel.sln` içinde olduğu için `dotnet test`
/// onu derliyordu ve modülün 100'den fazla testi YEŞİLDİ. Ama teslim yolu farklıydı:
/// `package-installer.ps1` yalnızca `AdaPanel.Api.csproj`'u publish ediyor ve `.iss` publish
/// klasörünü kopyalıyor. Modül hiçbirine girmiyordu.
///
/// Sonuç: 0.7.0, 0.7.1 ve 0.7.2 paketlerinde `.htaccess` özelliği HİÇ YOKTU. Kurulum,
/// var OLMAYAN bir DLL'i işaret eden bir kayıt betiği çalıştırıyordu ve sessizce geçiyordu.
/// Kurulu panelde ölçüldü: `AdaPanel.HtaccessModule.dll` yok, GAC'ta kayıt yok.
///
/// DERS: yeşil test "teslim edildi" demek DEĞİLDİR. Bir özelliğin paketin içinde olduğu
/// AYRICA ölçülmelidir — bu dosya onu ölçer.
/// </summary>
public class HtaccessModulePackagingTests
{
    [Fact]
    public void Paketleme_betigi_modulu_DERLER_ve_publish_klasorune_KOPYALAR()
    {
        var betik = File.ReadAllText(Path.Combine(KaynakKoku(), "scripts", "package-installer.ps1"));

        Assert.Contains("AdaPanel.HtaccessModule.csproj", betik);
        Assert.Contains("AdaPanel.HtaccessModule.dll", betik);
        // Derlemek yetmez: publish klasörüne KOPYALANMALI, çünkü .iss oradan alıyor.
        Assert.Contains("Copy-Item $moduleDll", betik);
    }

    /// <summary>
    /// Kopyalama sessizce başarısız olursa paket yine üretilirdi ve hata GERİ GELİRDİ.
    /// Betik, dosyanın publish klasöründe gerçekten bulunduğunu doğrulayıp aksi hâlde DURMALI.
    /// </summary>
    [Fact]
    public void Paketleme_modul_eksikse_DURUR()
    {
        var betik = File.ReadAllText(Path.Combine(KaynakKoku(), "scripts", "package-installer.ps1"));

        Assert.Contains("throw \".htaccess modulu uretilmedi", betik);
        Assert.Contains("throw \".htaccess modulu publish klasorune kopyalanamadi.\"", betik);
    }

    /// <summary>
    /// Kurulum, modülü <c>{app}</c> altına koymalı — kayıt betiği onu orada arıyor
    /// (<c>-ModulePath "{app}\AdaPanel.HtaccessModule.dll"</c>). Bugün bu, publish klasörünü
    /// olduğu gibi kopyalayan satırla sağlanıyor; o satır kaldırılırsa modül yine kaybolur.
    /// </summary>
    [Fact]
    public void Installer_publish_klasorunun_TAMAMINI_kopyalar()
    {
        var iss = File.ReadAllText(Path.Combine(KaynakKoku(), "installer", "AdaPanel.iss"));

        Assert.Contains("Source: \"{#PublishDir}\\*\"", iss);
        Assert.Contains("recursesubdirs", iss);
        // Kayıt betiği bu yolu bekliyor; ikisi birbirinden ayrılırsa özellik yine ölür.
        Assert.Contains("{app}\\AdaPanel.HtaccessModule.dll", iss);
    }

    /// <summary>Modül projesi çözümde olmalı, yoksa hiç derlenmez ve testleri de koşmaz.</summary>
    [Fact]
    public void Modul_projesi_cozumde()
    {
        var sln = File.ReadAllText(Path.Combine(KaynakKoku(), "AdaPanel.sln"));
        Assert.Contains("AdaPanel.HtaccessModule", sln);
    }

    private static string KaynakKoku()
    {
        var kok = AppContext.BaseDirectory;
        while (kok is not null && !File.Exists(Path.Combine(kok, "AdaPanel.sln")))
        {
            kok = Path.GetDirectoryName(kok);
        }

        Assert.NotNull(kok);
        return kok!;
    }
}
