using AdaPanel.Infrastructure.Monitoring;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// "Bu paylaşılan süreci kim kullanıyor?" servisinin SAF parçaları: appcmd çıktı ayrıştırıcıları,
/// veritabanı adı → site eşlemesi ve sorgu maskeleme. Motora/IIS'e bağlanan kısım makineye bağlıdır
/// (burada çalıştırılamaz); ama yanlış ayrıştırma ya da MASKELENMEMİŞ bir parola doğrudan kullanıcıya
/// yansır, bu yüzden bu üç parça gerçek çıktı örnekleriyle test edilir.
/// </summary>
public sealed class ResourceAttributionServiceTests
{
    // -------------------------------------------------------------------------------------------
    // Sorgu maskeleme (GİZLİLİK) — girdiler gerçek SHOW FULL PROCESSLIST "Info" sütunu değerleridir.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void SanitizeStatement_KeepsOrdinaryQueryAndCollapsesWhitespace()
    {
        var (text, redacted) = ResourceAttributionService.SanitizeStatement(
            "SELECT *\r\n  FROM wp_posts\r\n WHERE post_status = 'publish'\n ORDER BY post_date DESC");

        Assert.False(redacted);
        Assert.Equal("SELECT * FROM wp_posts WHERE post_status = 'publish' ORDER BY post_date DESC", text);
    }

    [Fact]
    public void SanitizeStatement_MasksIdentifiedByPassword()
    {
        // MySQL kullanıcı oluşturma sorgusu PROCESSLIST'te DÜZ METİN parola taşır; yönetici ekranında
        // bile olsa başka bir müşterinin parolası gösterilmemeli.
        var (text, redacted) = ResourceAttributionService.SanitizeStatement(
            "CREATE USER 'shop_user'@'%' IDENTIFIED BY 'Sup3rSecret!'");

        Assert.True(redacted);
        Assert.DoesNotContain("Sup3rSecret", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE USER", text, StringComparison.Ordinal);
        Assert.Contains("'***'", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SET PASSWORD FOR 'root'@'localhost' = PASSWORD('hunter2')", "hunter2")]
    [InlineData("ALTER USER 'app'@'%' IDENTIFIED WITH mysql_native_password BY 'p4ssw0rd'", "p4ssw0rd")]
    [InlineData("ALTER ROLE shop_user WITH PASSWORD 'pgSecret123'", "pgSecret123")]
    public void SanitizeStatement_MasksAllPasswordShapes(string statement, string secret)
    {
        var (text, redacted) = ResourceAttributionService.SanitizeStatement(statement);

        Assert.True(redacted);
        Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeStatement_CutsDanglingQuoteSoHalfPasswordNeverLeaks()
    {
        // Motor sorguyu kendisi kırpmış olabilir → kapanmamış tırnaktan sonrası parolanın yarısıdır.
        var (text, redacted) = ResourceAttributionService.SanitizeStatement(
            "CREATE USER 'a'@'%' IDENTIFIED BY 'HalfOfThePasswo");

        Assert.True(redacted);
        Assert.DoesNotContain("HalfOfThePasswo", text, StringComparison.Ordinal);
        Assert.EndsWith("'***'", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeStatement_TruncatesLongQueries()
    {
        var statement = "SELECT " + new string('x', 500);

        var (text, _) = ResourceAttributionService.SanitizeStatement(statement, maxLength: 200);

        Assert.Equal(201, text.Length); // 200 karakter + tek karakterlik "…" göstergesi
        Assert.EndsWith("…", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SanitizeStatement_ReturnsEmptyForNoStatement(string? statement)
    {
        var (text, redacted) = ResourceAttributionService.SanitizeStatement(statement);

        Assert.Equal(string.Empty, text);
        Assert.False(redacted);
    }

    // -------------------------------------------------------------------------------------------
    // Veritabanı adı → SİTE (ekranın asıl değeri)
    // -------------------------------------------------------------------------------------------

    private static Dictionary<string, string> SiteMap() => new(StringComparer.OrdinalIgnoreCase)
    {
        [ResourceAttributionService.SiteMapKey("mariadb", "shop_db")] = "alioral.xyz",
        [ResourceAttributionService.SiteMapKey("postgresql", "crm")] = "crm.example.com"
    };

    [Fact]
    public void ResolveSiteForDatabase_MatchesRecordedLinkCaseInsensitively()
    {
        Assert.Equal(
            "alioral.xyz",
            ResourceAttributionService.ResolveSiteForDatabase(SiteMap(), "mariadb", "SHOP_DB"));

        Assert.Equal(
            "crm.example.com",
            ResourceAttributionService.ResolveSiteForDatabase(SiteMap(), "postgresql", "crm"));
    }

    [Fact]
    public void ResolveSiteForDatabase_FallsBackToSiblingMySqlEngineKey()
    {
        // Bağ "mariadb" olarak kaydedilmişken kullanıcı "mysql" kaynağını izliyor olabilir; ikisi aynı
        // protokolü konuşur, bu yüzden kardeş anahtar da denenir.
        Assert.Equal(
            "alioral.xyz",
            ResourceAttributionService.ResolveSiteForDatabase(SiteMap(), "mysql", "shop_db"));
    }

    [Theory]
    [InlineData("mariadb", "bilinmeyen_db")]
    [InlineData("postgresql", "shop_db")] // farklı motorda aynı ad → eşleşme YOK (tahmin yapılmaz)
    [InlineData("mariadb", "")]
    [InlineData("mariadb", null)]
    public void ResolveSiteForDatabase_ReturnsNullWhenNotRecorded(string engine, string? databaseName)
    {
        Assert.Null(ResourceAttributionService.ResolveSiteForDatabase(SiteMap(), engine, databaseName));
    }

    [Theory]
    [InlineData("AdaPanel.alioral.xyz", "alioral.xyz")]
    [InlineData("adapanel.SHOP.com", "SHOP.com")]
    [InlineData("DefaultAppPool", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ResolveSiteNameFromPool_StripsManagedPrefixOnly(string? appPool, string? expected)
    {
        Assert.Equal(expected, ResourceAttributionService.ResolveSiteNameFromPool(appPool));
    }

    // -------------------------------------------------------------------------------------------
    // appcmd list requests — iki gerçek çıktı biçimi
    // -------------------------------------------------------------------------------------------

    private const string AppcmdXmlOutput =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <appcmd>
        <REQUEST REQUEST.NAME="1300000080000045" REQUEST.ID="1300000080000045" SITE.NAME="alioral.xyz" APPPOOL.NAME="AdaPanel.alioral.xyz" verb="GET" url="/wp-admin/admin-ajax.php?action=heartbeat" timeElapsed="12480" clientIp="88.230.11.4" pipelineState="ExecuteRequestHandler" currentModule="FastCgiModule" />
        <REQUEST REQUEST.NAME="1300000080000046" REQUEST.ID="1300000080000046" SITE.NAME="Default Web Site" verb="POST" url="/api/upload" timeElapsed="450" clientIp="::1" pipelineState="ExecuteRequestHandler" currentModule="ManagedPipelineHandler" />
        </appcmd>
        """;

    private const string AppcmdTextOutput =
        """
        REQUEST "1300000080000045" (url:GET /default.aspx, time:1250 msec, client:localhost, stage:ExecuteRequestHandler, module:ManagedPipelineHandler)
        REQUEST "1300000080000046" (url:POST /shop/checkout?ref=a,b, time:8730 msec, client:88.230.11.4, stage:ExecuteRequestHandler, module:FastCgiModule)
        """;

    [Fact]
    public void ParseAppcmdRequests_ReadsXmlFormWithSiteName()
    {
        var rows = ResourceAttributionService.ParseAppcmdRequests(AppcmdXmlOutput);

        Assert.Equal(2, rows.Count);

        var first = rows[0];
        Assert.Equal("1300000080000045", first.RequestId);
        Assert.Equal("alioral.xyz", first.SiteName);
        Assert.Equal("AdaPanel.alioral.xyz", first.AppPoolName);
        Assert.Equal("GET", first.Verb);
        Assert.Equal("/wp-admin/admin-ajax.php?action=heartbeat", first.Url);
        Assert.Equal(12.48d, first.DurationSeconds);
        Assert.Equal("88.230.11.4", first.ClientHost);
        Assert.Equal("ExecuteRequestHandler", first.Stage);
        Assert.Equal("FastCgiModule", first.Module);

        Assert.Equal("Default Web Site", rows[1].SiteName);
        Assert.Equal(0.45d, rows[1].DurationSeconds);
    }

    [Fact]
    public void ParseAppcmdRequests_ReadsTextForm()
    {
        var rows = ResourceAttributionService.ParseAppcmdRequests(AppcmdTextOutput);

        Assert.Equal(2, rows.Count);

        Assert.Equal("1300000080000045", rows[0].RequestId);
        Assert.Equal("GET", rows[0].Verb);
        Assert.Equal("/default.aspx", rows[0].Url);
        Assert.Equal(1.25d, rows[0].DurationSeconds);
        Assert.Equal("localhost", rows[0].ClientHost);
        // Metin biçiminde site adı YOKTUR; uydurmuyoruz.
        Assert.Null(rows[0].SiteName);

        // Sorgu dizesindeki virgül alan sınırı sanılmamalı.
        Assert.Equal("POST", rows[1].Verb);
        Assert.Equal("/shop/checkout?ref=a,b", rows[1].Url);
        Assert.Equal(8.73d, rows[1].DurationSeconds);
        Assert.Equal("FastCgiModule", rows[1].Module);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bilgi: Sıfır nesne bulundu.")]
    [InlineData("<appcmd></appcmd>")]
    public void ParseAppcmdRequests_ReturnsEmptyForNoRequests(string? output)
    {
        Assert.Empty(ResourceAttributionService.ParseAppcmdRequests(output));
    }

    [Fact]
    public void ParseAppcmdRequests_ReturnsEmptyForBrokenXmlInsteadOfThrowing()
    {
        // appcmd çıktısı kesilirse ekran hata vermemeli, sadece boş kalmalı.
        Assert.Empty(ResourceAttributionService.ParseAppcmdRequests("<appcmd><REQUEST url=\"/a\""));
    }

    [Fact]
    public void LooksLikeXml_DistinguishesFormats()
    {
        Assert.True(ResourceAttributionService.LooksLikeXml(AppcmdXmlOutput));
        Assert.False(ResourceAttributionService.LooksLikeXml(AppcmdTextOutput));
        Assert.False(ResourceAttributionService.LooksLikeXml(null));
    }
}
