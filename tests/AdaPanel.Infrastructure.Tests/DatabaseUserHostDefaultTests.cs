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
        Assert.Null(new SetDatabaseUserPasswordRequest().AllowedHost);
    }

    /// <summary>
    /// ICE AKTARMA '%' VARSAYILANINDAN CIKARILDI -- ve bu, yukaridaki kuralin ISTISNASI DEGIL,
    /// yanlis siniflandirilmis bir yol.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ice aktarma "var olan hesabi adresler" diye siniflandirilmisti, ama yaptigi sey YETKI
    /// YAZMAK: GRANT ALL. Panelin kendi kullanicilari 'localhost'ta dogar
    /// (<c>MySqlAccountHosts.SafeCreationHost</c>), dolayisiyla '%' varsayilani yetkiyi VAR
    /// OLMAYAN bir hesaba yaziyordu -- ya "kullanici bulunamadi" ile dusuyor, ya da GRANT'in
    /// hesap olusturabildigi yapilandirmalarda dunyaya acik IKINCI bir hesap doguruyordu.
    /// Ardindan kimlik kaydina da '%' yazildigi icin silme yolu DROP USER 'u'@'%' calistiriyor,
    /// gercek hesap oksuz kaliyordu.
    /// </para>
    /// <para>
    /// Geriye donuk kirmaz: hesap gercekten '%' ise motordan okunan kume de '%' doner.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheImportPathNoLongerAssumesTheEveryHostAccount()
    {
        Assert.Equal(string.Empty, new ImportDatabaseRequest().Host);
    }

    /// <summary>
    /// Ice aktarma host'u TAHMIN ETMEZ, motordan OKUR. Yukaridaki test yalnizca '%'in
    /// yoklugunu gosterir; bu, bosluk yerine dogru davranisin kondugunu gosterir.
    /// </summary>
    [Fact]
    public void TheImportPathResolvesHostsFromTheEngine()
    {
        var kaynak = KaynakKodu(Path.Combine("src", "AdaPanel.Infrastructure", "Database", "DatabaseService.cs"));
        var govde = MetotGovdesi(kaynak, "ImportDatabaseAsync");

        Assert.Contains("ResolveExistingHostsAsync(provider, profile, userName", govde, StringComparison.Ordinal);

        // Kimlik kaydina da '%' DEGIL, olculmus bir hesap yazilmali: silme yolu bu kaydi
        // kullaniyor ve yanlis host yazilirsa DROP USER yanlis hesabi hedefliyor.
        Assert.Contains("MySqlAccountHosts.IsLocal", govde, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bir metodun govdesini kaynaktan keser. Dosya genelinde arama, aranan satir BASKA bir
    /// metotta gectigi icin duzeltme geri alinsa bile yesil kalan "kor" testler uretiyor --
    /// bu dosyada zaten bir kez oldu.
    /// </summary>
    private static string MetotGovdesi(string kaynak, string metotAdi)
    {
        var basla = kaynak.IndexOf($"{metotAdi}(", StringComparison.Ordinal);
        Assert.True(basla >= 0, $"Metot bulunamadi: {metotAdi}");

        var acilis = kaynak.IndexOf('{', basla);
        Assert.True(acilis >= 0, $"Metot govdesi bulunamadi: {metotAdi}");

        var derinlik = 0;
        for (var i = acilis; i < kaynak.Length; i++)
        {
            if (kaynak[i] == '{')
            {
                derinlik++;
            }
            else if (kaynak[i] == '}')
            {
                derinlik--;
                if (derinlik == 0)
                {
                    return kaynak[acilis..(i + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Metot govdesi kapanmadi: {metotAdi}");
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
    /// <remarks>
    /// <para>
    /// <b>BU TEST KORDU ve olcerek goruldu.</b> Yalnizca <c>allowedHost: "%"</c> ariyordu; ice
    /// aktarma formu ise <c>host: "%"</c> gonderiyordu. Yani ekranda duran gercek '%' sabitini
    /// nobetci HIC gormedi -- "testin var olmasi bir sey olctugu anlamina gelmez"in birebir
    /// ornegi. Artik ALAN ADINDAN bagimsiz olarak "bir host alanina '%' SABITI atanmasi"
    /// araniyor; yeni bir form yarin baska bir ad kullansa da yakalanir.
    /// </para>
    /// <para>
    /// Desen deger olarak yalnizca SABIT '%' arar. Kullanicinin yazdigi bir degiskeni gonderen
    /// alanlar (<c>host: form.host</c>) eslesmez -- amac uzak erisimi yasaklamak degil, panelin
    /// kullanicidan habersiz "her yerden" demesini yasaklamak.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("DatabasesView.vue")]
    public void TheUserInterfaceNoLongerSendsTheWildcardHost(string ekran)
    {
        var kaynak = KaynakKodu(Path.Combine("frontend", "src", "views", ekran));

        var ihlaller = Regex.Matches(kaynak, @"\b\w*[Hh]ost\s*:\s*""%""")
            .Select(match => match.Value)
            .ToArray();

        Assert.True(
            ihlaller.Length == 0,
            $"{ekran} bir host alanina '%' SABITI gonderiyor: " + string.Join(", ", ihlaller));

        Assert.DoesNotContain("host || \"%\"", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// NOBETCININ KENDISI OLCULUR: yukaridaki desen, kapatilan iki bicimi de GERCEKTEN yakaliyor
    /// mu. Kor kalmis desenin bedeli bir kez odendi; bir daha "arama yapiyorum" demek yetmez.
    /// </summary>
    [Theory]
    [InlineData("      host: \"%\",")]
    [InlineData("      allowedHost: \"%\",")]
    [InlineData("    targetHost:\"%\"")]
    public void TheWildcardScannerActuallyMatchesTheShapesItClaimsTo(string satir)
    {
        Assert.Matches(@"\b\w*[Hh]ost\s*:\s*""%""", satir);
    }
}
