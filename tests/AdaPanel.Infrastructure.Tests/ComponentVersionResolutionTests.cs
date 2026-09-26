using System.Text.Json;
using AdaPanel.Infrastructure.Bootstrap;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Canlı sürüm çözümleyicilerinin saf mantığı. Ağ çağrıları burada test edilmez; test edilen şey
/// upstream listelerinden DOĞRU sürümün seçilmesi — yanlış seçim bozuk indirme URL'i veya sessiz
/// dal atlaması (8.3 → 8.4, 3.12 → 3.14) demektir.
/// </summary>
public sealed class ComponentVersionResolutionTests
{
    [Theory]
    [InlineData("8.3.31", 2, "8.3")]
    [InlineData("11.8.3", 2, "11.8")]
    [InlineData("16.14", 1, "16")]
    [InlineData("1.31.0", 2, "1.31")]
    [InlineData("latest", 2, "")]
    [InlineData("", 2, "")]
    public void VersionSeries_TakesTheRequestedNumberOfSegments(string version, int parts, string expected)
    {
        Assert.Equal(expected, ComponentBootstrapper.VersionSeries(version, parts));
    }

    [Theory]
    [InlineData("8.3.33", "8.3.31", 1)]
    [InlineData("8.3.31", "8.3.33", -1)]
    [InlineData("8.3.31", "8.3.31", 0)]
    [InlineData("11.8.10", "11.8.9", 1)]  // sayısal karşılaştırma (metin olarak "10" < "9" olurdu)
    [InlineData("1.31.3", "1.31.10", -1)]
    public void CompareVersions_ComparesNumerically(string left, string right, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(ComponentBootstrapper.CompareVersions(left, right)));
    }

    // nginx indirme sayfası mainline (1.31.x) ve stable (1.30.x) dallarını birlikte listeler.
    [Fact]
    public void SelectNewerInSeries_StaysOnTheInstalledBranch()
    {
        var result = ComponentBootstrapper.SelectNewerInSeries(
            ["1.31.3", "1.31.1", "1.30.4", "1.28.3", "1.31.0"],
            "1.31.0",
            2);

        Assert.Equal(["1.31.3", "1.31.1"], result);
    }

    // Python dizin listesi 3.12, 3.14, 3.15 dizinlerini yan yana tutar; 3.12 kurulu olan sunucu
    // sessizce 3.15'e atlamamalı.
    [Fact]
    public void SelectNewerInSeries_DoesNotJumpToANewerMinorSeries()
    {
        var result = ComponentBootstrapper.SelectNewerInSeries(
            ["3.15.0", "3.14.6", "3.12.13", "3.12.10", "3.12.4"],
            "3.12.4",
            2);

        Assert.Equal(["3.12.13", "3.12.10"], result);
    }

    [Fact]
    public void SelectNewerInSeries_IsEmpty_WhenTheInstalledVersionIsAlreadyTheNewest()
    {
        Assert.Empty(ComponentBootstrapper.SelectNewerInSeries(["1.31.3", "1.30.4"], "1.31.3", 2));
    }

    [Fact]
    public void SelectNewerInSeries_IsEmpty_ForAnUnparsableInstalledVersion()
    {
        Assert.Empty(ComponentBootstrapper.SelectNewerInSeries(["1.31.3"], "latest", 2));
    }

    // Checksum muafiyeti YALNIZCA sha256 yayınlamayan resmi HTTPS dağıtım sunucuları için.
    [Theory]
    [InlineData("https://cdn.mysql.com/Downloads/MySQL-8.4/mysql-8.4.11-winx64.zip", true)]
    [InlineData("https://get.enterprisedb.com/postgresql/postgresql-16.14-1-windows-x64.exe", true)]
    [InlineData("https://nginx.org/download/nginx-1.31.3.zip", true)]
    [InlineData("https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.exe", true)]
    [InlineData("https://ftp.postgresql.org/pub/pgadmin/pgadmin4/v9.16/windows/pgadmin4-9.16-x64.exe", true)]
    [InlineData("http://cdn.mysql.com/Downloads/MySQL-8.4/mysql-8.4.11-winx64.zip", false)]  // HTTPS şart
    [InlineData("https://cdn.mysql.com.evil.example/x.zip", false)]                          // sahte alt alan adı
    [InlineData("https://example.com/mysql-8.4.11-winx64.zip", false)]
    public void IsTrustedNoChecksumHost_OnlyAcceptsOfficialHttpsDistributionHosts(string url, bool expected)
    {
        Assert.Equal(expected, ComponentBootstrapper.IsTrustedNoChecksumHostForTests(url));
    }

    // Only resolvers that actually retrieve a version can establish freshness.
    [Theory]
    [InlineData("php-8.3")]
    [InlineData("mariadb-11")]
    [InlineData("mysql-8")]
    [InlineData("postgresql-16")]
    [InlineData("pgadmin4")]
    [InlineData("nginx")]
    [InlineData("python-3")]
    [InlineData("redis")]
    public void SupportsVersionCheck_NowCoversThePreviouslyPinnedComponents(string componentId)
    {
        Assert.True(ComponentBootstrapper.SupportsVersionCheck(componentId));
    }

    // --- Gerçek upstream cevaplarının kısaltılmış kopyalarıyla ayrıştırma testleri ---
    // (2026-07-31'de windows.php.net ve downloads.mariadb.org'dan alınan gerçek yapı.)

    private const string PhpBranchJson =
        """
        {
          "version": "8.3.33",
          "ts-vs16-x64": { "zip": { "path": "php-8.3.33-Win32-vs16-x64.zip", "sha256": "1111111111111111111111111111111111111111111111111111111111111111" } },
          "nts-vs16-x64": { "zip": { "path": "php-8.3.33-nts-Win32-vs16-x64.zip", "sha256": "534399107056313246f424adbbb7937337e40fbbf6aa7bc26287ba9cfd2e4a2a" } },
          "nts-vs17-x64": { "zip": { "path": "php-8.3.33-nts-Win32-vs17-x64.zip", "sha256": "2222222222222222222222222222222222222222222222222222222222222222" } }
        }
        """;

    [Fact]
    public void ParsePhpBranch_KeepsTheInstalledCompilerFlavourAndReturnsItsChecksum()
    {
        using var document = JsonDocument.Parse(PhpBranchJson);
        var result = ComponentBootstrapper.ParsePhpBranch(
            document.RootElement,
            "https://downloads.php.net/~windows/releases/php-8.3.31-nts-Win32-vs16-x64.zip");

        Assert.NotNull(result);
        Assert.Equal("8.3.33", result!.Value.Version);
        Assert.Equal("php-8.3.33-nts-Win32-vs16-x64.zip", result.Value.FileName);
        Assert.Equal("534399107056313246f424adbbb7937337e40fbbf6aa7bc26287ba9cfd2e4a2a", result.Value.Sha256);
    }

    // Thread-safe (ts) paket ASLA seçilmemeli: panel PHP'yi FastCGI ile nts olarak çalıştırır.
    [Fact]
    public void ParsePhpBranch_NeverSelectsAThreadSafeBuild()
    {
        using var document = JsonDocument.Parse(PhpBranchJson);
        var result = ComponentBootstrapper.ParsePhpBranch(document.RootElement, "https://example.test/php.zip");

        Assert.NotNull(result);
        Assert.Contains("-nts-", result!.Value.FileName);
    }

    [Fact]
    public void ParsePhpBranch_ReturnsNull_WhenTheBranchHasNoWindowsZip()
    {
        using var document = JsonDocument.Parse("""{ "version": "8.3.33" }""");
        Assert.Null(ComponentBootstrapper.ParsePhpBranch(document.RootElement, string.Empty));
    }

    private const string MariaDbReleasesJson =
        """
        {
          "releases": {
            "11.8.3": { "files": [ { "file_name": "mariadb-11.8.3-winx64.zip", "checksum": { "sha256sum": "aaaa" } } ] },
            "11.8.8": { "files": [
              { "file_name": "mariadb-11.8.8-winx64.msi", "checksum": { "sha256sum": "bbbb" } },
              { "file_name": "mariadb-11.8.8-winx64.zip", "checksum": { "sha256sum": "20871a79964e1819ddaad9247b676b9d08c958c345e5e3d4748242b2b2965ff1" } }
            ] },
            "11.8.5": { "files": [ { "file_name": "mariadb-11.8.5-winx64.zip", "checksum": { "sha256sum": "cccc" } } ] }
          }
        }
        """;

    [Fact]
    public void ParseMariaDbReleases_PicksTheNewestReleaseAndItsWindowsZip()
    {
        using var document = JsonDocument.Parse(MariaDbReleasesJson);
        var result = ComponentBootstrapper.ParseMariaDbReleases(document.RootElement, "11.8.3");

        Assert.NotNull(result);
        Assert.Equal("11.8.8", result!.Value.Version);
        Assert.Equal("mariadb-11.8.8-winx64.zip", result.Value.FileName);
        Assert.Equal("20871a79964e1819ddaad9247b676b9d08c958c345e5e3d4748242b2b2965ff1", result.Value.Sha256);
    }

    [Fact]
    public void ParseMariaDbReleases_ReturnsNull_WhenNothingIsNewerThanTheInstalledVersion()
    {
        using var document = JsonDocument.Parse(MariaDbReleasesJson);
        Assert.Null(ComponentBootstrapper.ParseMariaDbReleases(document.RootElement, "11.8.8"));
    }
}
