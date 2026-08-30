using System.Reflection;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Enums;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Veritabani kullanici adi UZUNLUK siniri: motorun soyledigi sayi, panelin uydurdugu degil.
/// </summary>
/// <remarks>
/// <para>
/// OLCULEN DURUM: panelin dogrulayicisi 64 karaktere izin veriyordu. MySQL'in siniri 32'dir ve
/// motorda SABIT KODLUDUR ("MySQL user names are up to 32 characters long" --
/// dev.mysql.com/doc/refman/8.4/en/user-names.html). 33-64 arasi bir adda: CREATE DATABASE ONCE
/// calisiyor, CREATE USER 1470 (ER_WRONG_STRING_LENGTH) ile dusuyor, geri alma olmadigi icin
/// kullaniciya jenerik 500 donerken sunucuda SAHIPSIZ bir veritabani kaliyordu.
/// </para>
/// <para>
/// 64 sayisi ayrica BES ayri yere elle yazilmisti (regex + dort istek DTO'su) ve hicbiri bir
/// motor sinirini temsil etmiyordu. Sinir artik tek kaynakta.
/// </para>
/// </remarks>
public sealed class DatabaseUserNameLimitTests
{
    /// <summary>Sayilar motorun BELGESINDEN alindi; degistirilirse burada yakalanir.</summary>
    [Theory]
    [InlineData(DbEngine.MySql, 32)]
    [InlineData(DbEngine.MariaDb, 80)]
    [InlineData(DbEngine.PostgreSql, 63)]
    [InlineData(DbEngine.MsSql, 128)]
    public void EachEngineReportsItsDocumentedLimit(DbEngine engine, int beklenen)
    {
        Assert.Equal(beklenen, DatabaseIdentifierLimits.ForEngine(engine));
    }

    /// <summary>
    /// MySQL'in siniri 32 OLMALI. Ayri bir test cunku duzeltilen hata tam olarak buydu; digerleri
    /// degisse bile bu sayinin kaymasi ayni acigin geri gelmesi demektir.
    /// </summary>
    [Fact]
    public void MySqlIsCappedAtThirtyTwo()
    {
        Assert.Equal(32, DatabaseIdentifierLimits.MySqlUserNameLength);
    }

    /// <summary>
    /// 33 karakterlik bir ad MySQL icin REDDEDILIR -- motora hic gitmeden, 400 ile ve nedenini
    /// soyleyerek. Eskiden kabul ediliyor, sonra motorda 1470 ile jenerik 500'e donusuyordu.
    /// </summary>
    [Fact]
    public void AThirtyThreeCharacterNameIsRejectedForMySql()
    {
        var hata = Assert.Throws<TargetInvocationException>(
            () => Dogrula(new string('a', 33), DbEngine.MySql));

        var app = Assert.IsType<AppException>(hata.InnerException);
        Assert.Equal("database.user_name_too_long", app.MessageKey);
        Assert.Equal(400, app.HttpStatusCode);

        // Ayrinti, kullanicinin adi kisaltirken ihtiyaci olan iki sayiyi TASIMALI.
        Assert.Contains("max=32", app.Details, StringComparison.Ordinal);
        Assert.Contains("length=33", app.Details, StringComparison.Ordinal);
    }

    /// <summary>Tam 32 karakter GECERLI: sinir "asilinca" reddeder, "yaklasinca" degil.</summary>
    [Fact]
    public void ExactlyThirtyTwoCharactersIsAcceptedForMySql()
    {
        var ad = new string('a', 32);
        Assert.Equal(ad, Dogrula(ad, DbEngine.MySql));
    }

    /// <summary>
    /// AYNI AD MariaDB'de GECERLI. Sinir motora gore olculuyor; tek bir sayiyi butun motorlara
    /// dayatmak, MariaDB'nin gercekten kabul ettigi bir adi reddetmek olurdu.
    /// </summary>
    [Fact]
    public void TheSameNameIsAcceptedOnMariaDbWhereTheEngineAllowsIt()
    {
        var ad = new string('a', 40);
        Assert.Equal(ad, Dogrula(ad, DbEngine.MariaDb));
    }

    /// <summary>MariaDB'nin 80 siniri da uygulanir: "genis" demek "sinirsiz" demek degil.</summary>
    [Fact]
    public void MariaDbStillHasALimit()
    {
        Assert.Throws<TargetInvocationException>(() => Dogrula(new string('a', 81), DbEngine.MariaDb));
    }

    /// <summary>
    /// VERITABANI, KULLANICI ADI DOGRULANMADAN OLUSTURULMAZ.
    /// </summary>
    /// <remarks>
    /// Sira meselesi bir uslup tercihi degil: <c>CreateDatabaseAsync</c> once veritabanini
    /// yaratiyordu ve kullanici adi gecersizse geri alma yoktu. Kaynakta sira olculur cunku
    /// "hangisi once calisir" sorusunun cevabi tam olarak budur.
    /// </remarks>
    [Fact]
    public void TheUserNameIsValidatedBeforeTheDatabaseIsCreated()
    {
        var govde = MetotGovdesi(ServisKaynagi(), "CreateDatabaseAsync");

        var dogrulama = govde.IndexOf("NormalizeUserName(request.UserName, dbEngine)", StringComparison.Ordinal);
        var olusturma = govde.IndexOf("provider.CreateDatabaseAsync(", StringComparison.Ordinal);

        Assert.True(dogrulama >= 0, "Kullanici adi dogrulamasi CreateDatabaseAsync icinde bulunamadi.");
        Assert.True(olusturma >= 0, "CreateDatabase cagrisi bulunamadi.");
        Assert.True(
            dogrulama < olusturma,
            "Kullanici adi, veritabani olusturulduktan SONRA dogrulaniyor: gecersiz adda sahipsiz veritabani kalir.");
    }

    /// <summary>
    /// Kullanici kurulamazsa BU CAGRIDA olusturulan veritabani GERI ALINIR. Dogrulama uzunlugu
    /// yakalar ama parola politikasi / yetki gibi baska sebepler de var; geri alma o durumlar icin.
    /// </summary>
    [Fact]
    public void AFailedUserCreationRollsBackTheDatabase()
    {
        var govde = MetotGovdesi(ServisKaynagi(), "CreateDatabaseAsync");

        Assert.Contains("provider.DeleteDatabaseAsync(profile, databaseName, cancellationToken)", govde, StringComparison.Ordinal);
    }

    /// <summary>MySQL 1470 artik jenerik 500 DEGIL, nedenini soyleyen 400.</summary>
    [Fact]
    public void MySqlErrorFourteenSeventyIsMapped()
    {
        var kaynak = Kaynak(Path.Combine("src", "AdaPanel.Infrastructure", "Database", "MySqlDatabaseProvider.cs"));

        Assert.Contains("1470 => new AppException(\"database.user_name_too_long\"", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// SINIR TEK KAYNAKTA. Istek DTO'lari kullanici adi icin elle yazilmis bir sayi TASIMAMALI --
    /// bes kopyanin bes farkli sayiya kaymasi tam olarak boyle basladi.
    /// </summary>
    [Theory]
    [InlineData("CreateDatabaseRequest.cs")]
    [InlineData("CreateDatabaseUserRequest.cs")]
    [InlineData("GrantDatabaseAccessRequest.cs")]
    [InlineData("ImportDatabaseRequest.cs")]
    public void RequestDtosDoNotHardcodeTheUserNameLimit(string dosya)
    {
        var kaynak = Kaynak(Path.Combine("src", "AdaPanel.Core", "Dtos", "Databases", dosya));

        // UserName alanindan hemen ONCEKI StringLength satiri taranir; ayni dosyadaki DIGER
        // alanlarin (Engine, Password, SiteName) sinirlari bu kuralin disinda.
        var eslesme = Regex.Match(
            kaynak,
            @"\[StringLength\((?<uzunluk>[^,)\]]+)[^\]]*\]\s*(?://[^\n]*\n\s*)*public string UserName");

        Assert.True(eslesme.Success, $"{dosya} icinde UserName icin StringLength bulunamadi.");
        Assert.Equal(
            "DatabaseIdentifierLimits.RequestCeiling",
            eslesme.Groups["uzunluk"].Value.Trim());
    }

    /// <summary>
    /// Tavan bir MOTOR siniri degil, kaba bir ust siniridir: en genis motoru karsilamali, yoksa
    /// DTO gecerli bir SQL Server adini motora hic sormadan reddederdi.
    /// </summary>
    [Fact]
    public void TheRequestCeilingCoversEveryEngine()
    {
        foreach (var engine in Enum.GetValues<DbEngine>())
        {
            Assert.True(
                DatabaseIdentifierLimits.ForEngine(engine) <= DatabaseIdentifierLimits.RequestCeiling,
                $"{engine} sinirini DTO tavani karsilamiyor.");
        }
    }

    /// <summary>
    /// Tasima kullanicisi turetimi de tek kaynaktan besleniyor: elle yazilmis "27" bayat bir
    /// kopyaydi ve sinir degistiginde sessizce yanlisa duserdi.
    /// </summary>
    [Fact]
    public void TheDerivedTransferUserStaysWithinTheMySqlLimit()
    {
        var method = typeof(AdaPanel.Infrastructure.Database.DatabaseService).GetMethod(
            "DeriveTransferUser",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var uretilen = (string)method!.Invoke(null, [new string('x', 200)])!;

        Assert.True(
            uretilen.Length <= DatabaseIdentifierLimits.MySqlUserNameLength,
            $"Turetilen tasima kullanicisi MySQL sinirini asiyor: {uretilen.Length}");
        Assert.EndsWith("_usr", uretilen, StringComparison.Ordinal);
    }

    private static string Dogrula(string ad, DbEngine engine)
    {
        var method = typeof(AdaPanel.Infrastructure.Database.DatabaseService).GetMethod(
            "NormalizeUserName",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return (string)method!.Invoke(null, [ad, engine])!;
    }

    private static string ServisKaynagi() =>
        Kaynak(Path.Combine("src", "AdaPanel.Infrastructure", "Database", "DatabaseService.cs"));

    private static string Kaynak(string goreliYol)
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
    /// Metot govdesini keser. Dosya genelinde arama yapmak, aranan satir BASKA bir metotta
    /// gectigi icin duzeltme geri alinsa bile yesil kalan "kor" testler uretir.
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
}
