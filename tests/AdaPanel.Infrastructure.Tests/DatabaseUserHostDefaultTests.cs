using System.Text.RegularExpressions;
using AdaPanel.Core.Dtos.Databases;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// YENI veritabani kullanicilarinin varsayilan erisim genisligi.
/// <para>
/// OLCULEN DURUM: panel her kullaniciyi kullanici@'%' olarak aciyordu -- veritabani olustururken
/// de, kullanici eklerken de, tasimada da. Yani her musteri hesabi kimlik dogrulama acisindan
/// dunyaya acikti ve tek engel guvenlik duvariydi. Bir guvenlik sinirini tek bir guvenlik duvari
/// kuralina baglamak, o kural bir kez acildiginda SUNUCUDAKI BUTUN musteri hesaplarinin ayni anda
/// disaridan denenebilir olmasi demektir.
/// </para>
/// <para>
/// Bu testler varsayilanin kapali dogdugunu ve GERIYE DONUK kirmadigini korur.
/// </para>
/// </summary>
public sealed class DatabaseUserHostDefaultTests
{
    private static string KaynakKodu(string goreliYol)
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            if (File.Exists(Path.Combine(klasor, "AdaPanel.sln")))
            {
                return File.ReadAllText(Path.Combine(Path.GetFullPath(klasor), goreliYol));
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new DirectoryNotFoundException("Depo koku bulunamadi.");
    }

    /// <summary>
    /// Istek varsayilani artik '%' DEGIL. Bos birakildiginda sunucu guvenli (yerel) varsayilani
    /// uygular; istemci bir sey soylemeyi unutursa kapi acilmaz.
    /// </summary>
    [Fact]
    public void CreateUserRequest_NoLongerDefaultsToEveryHost()
    {
        Assert.Equal(string.Empty, new CreateDatabaseUserRequest().AllowedHost);
    }

    /// <summary>
    /// GRANT istegi de '%' varsayilanindan cikarildi. Eski varsayilan iki sekilde zarar
    /// veriyordu: (1) hesap yerelle sinirliyken yetki 'u'@'%'e yazilmaya calisilip "kullanici
    /// bulunamadi" ile dusuyordu; (2) GRANT'in hesap olusturabildigi yapilandirmalarda panelin az
    /// once yerelle sinirladigi kullanici icin SESSIZCE her yerden gecerli yeni bir hesap
    /// doguyordu.
    /// </summary>
    [Fact]
    public void GrantRequest_NoLongerDefaultsToEveryHost()
    {
        Assert.Equal(string.Empty, new GrantDatabaseAccessRequest().AllowedHost);
    }

    /// <summary>
    /// VAR OLAN hesabi ADRESLEYEN yollarin varsayilani '%' KALMALI. Bunlar hesap yaratmaz,
    /// mevcudu bulur; degistirmek eski kurulumlardaki 'u'@'%' hesaplarinin parolasinin
    /// degistirilememesine / silinememesine yol acardi.
    /// </summary>
    [Fact]
    public void RequestsThatAddressAnExistingAccount_KeepTheLegacyDefault()
    {
        Assert.Equal("%", new ImportDatabaseRequest().Host);
        Assert.Null(new SetDatabaseUserPasswordRequest().AllowedHost);
    }

    /// <summary>
    /// OLUSTURMA yollarinin hicbiri artik '%' SABITI kullanmiyor.
    /// <para>
    /// Kaynak taramasiyla korunur cunku acik tek bir satirdi ve uc ayri yerde tekrarliyordu
    /// (CreateDatabaseAsync, CreateUserAsync, TransferDatabaseAsync). Biri geri gelirse burada
    /// yakalanir.
    /// </para>
    /// </summary>
    [Fact]
    public void NoCreationPathHardcodesTheEveryHostWildcard()
    {
        var kaynak = KaynakKodu(Path.Combine("src", "AdaPanel.Infrastructure", "Database", "DatabaseService.cs"));

        // 'const string anyHost = "%"' kalibi ve dogrudan "%" atamasi olusturma yollarinda olmamali.
        var kalintilar = Regex.Matches(kaynak, @"(?:anyHost|allowedHost|newUserHost|transferHost)\s*=\s*""%""")
            .Select(match => match.Value)
            .ToArray();

        Assert.True(
            kalintilar.Length == 0,
            "Olusturma yolunda '%' sabiti geri gelmis: " + string.Join(", ", kalintilar));
    }

    /// <summary>
    /// Guvenli varsayilan GERCEKTEN kullaniliyor: olusturma yollari NewUserHost'tan geciyor.
    /// Yukaridaki test yalnizca '%'in yoklugunu gosterir; bu, dogru degerin konuldugunu gosterir.
    /// </summary>
    [Fact]
    public void CreationPathsUseTheSafeDefaultHelper()
    {
        var kaynak = KaynakKodu(Path.Combine("src", "AdaPanel.Infrastructure", "Database", "DatabaseService.cs"));

        Assert.Contains("NewUserHost(dbEngine)", kaynak, StringComparison.Ordinal);
        Assert.Contains("NewUserHost(engine)", kaynak, StringComparison.Ordinal);
        Assert.Contains("MySqlAccountHosts.SafeCreationHost", kaynak, StringComparison.Ordinal);
    }

    /// <summary>Guvenli varsayilan yerel olmali (yoksa "guvenli" demek anlamsiz).</summary>
    [Fact]
    public void TheSafeCreationHostIsLocal()
    {
        Assert.True(AdaPanel.Infrastructure.Database.MySqlAccountHosts.IsLocal(
            AdaPanel.Infrastructure.Database.MySqlAccountHosts.SafeCreationHost));
    }

    /// <summary>
    /// Arayuz de artik '%' GONDERMIYOR. Sunucu varsayilani duzeltilse bile istemci acikca '%'
    /// gonderseydi degisiklik hicbir ise yaramazdi.
    /// </summary>
    [Fact]
    public void TheUserInterfaceNoLongerSendsTheWildcardHost()
    {
        var kaynak = KaynakKodu(Path.Combine("frontend", "src", "views", "DatabasesView.vue"));

        Assert.DoesNotContain("allowedHost: \"%\"", kaynak, StringComparison.Ordinal);
        Assert.DoesNotContain("host || \"%\"", kaynak, StringComparison.Ordinal);
    }
}
