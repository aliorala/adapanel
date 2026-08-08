using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Paketleme surumunun TEK KAYNAKTAN gelmesi.
/// </summary>
/// <remarks>
/// <para>
/// PATLAMA ZINCIRI: <c>build-installer.ps1</c> ve <c>package-installer.ps1</c> ikisi de
/// <c>$Version = "0.2.28"</c> diye OLU bir varsayilan tasiyordu ve hicbiri
/// <c>Directory.Build.props</c>'u okumuyordu. README'deki komut da parametresizdi. Sonuc:
/// </para>
/// <list type="number">
/// <item>kaynak 0.4.22 iken exe 0.2.28 damgalanir,</item>
/// <item>release 0.4.23 tag'iyle yayinlanir,</item>
/// <item>kurulu paneller 'latest'i gorur, SHA dogru oldugu icin gecer, kurar,</item>
/// <item>panel 0.2.28'e DUSER, sonraki kontrolde yine 0.4.23 gorur,</item>
/// <item>her gun indirip kuran SONSUZ GUNCELLEME DONGUSU.</item>
/// </list>
/// <para>
/// Testler <c>scripts\version-guard.ps1</c>'in kendisini kosturur; kopyasini degil.
/// </para>
/// </remarks>
public sealed class ReleaseVersionGuardTests : IDisposable
{
    private readonly string calisma = Path.Combine(Path.GetTempPath(), $"ada-surum-{Guid.NewGuid():N}");

    public ReleaseVersionGuardTests() => Directory.CreateDirectory(calisma);

    public void Dispose()
    {
        try { Directory.Delete(calisma, recursive: true); } catch { /* en iyi caba */ }
    }

    /// <summary>
    /// Surum verilmezse Directory.Build.props'tan OKUNUR (olu varsayilan yok).
    /// </summary>
    [Fact]
    public void Surum_verilmezse_kaynaktan_okunur()
    {
        var depo = SahteDepoKur("0.4.22", "0.4.22", "0.4.22", "0.4.22");

        var sonuc = KapiyiCalistir(depo, requestedVersion: null);

        Assert.Equal(0, sonuc.ExitCode);
        Assert.Contains("SURUM=0.4.22", sonuc.Tumu);
    }

    /// <summary>
    /// Elle verilen surum kaynakla uyusmuyorsa DUR.
    /// </summary>
    /// <remarks>
    /// Tam olarak eski varsayilanin (0.2.28) yapacagi sey bu: sessizce yanlis damgalamak yerine
    /// paketleme durmali.
    /// </remarks>
    [Fact]
    public void Elle_verilen_yanlis_surum_paketlemeyi_durdurur()
    {
        var depo = SahteDepoKur("0.4.22", "0.4.22", "0.4.22", "0.4.22");

        var sonuc = KapiyiCalistir(depo, requestedVersion: "0.2.28");

        Assert.NotEqual(0, sonuc.ExitCode);
        Assert.Contains("Surum uyusmazligi", sonuc.Tumu);
    }

    /// <summary>
    /// GitHub release tag'i kaynaktan farkliysa DUR -- sonsuz guncelleme dongusunun tam sebebi.
    /// </summary>
    [Fact]
    public void Release_tagi_kaynaktan_farkliysa_paket_uretilmez()
    {
        var depo = SahteDepoKur(kaynak: "0.4.23", packageJson: "0.4.23", iss: "0.4.23", releaseTag: "0.4.22");

        var sonuc = KapiyiCalistir(depo, requestedVersion: null);

        Assert.NotEqual(0, sonuc.ExitCode);
        Assert.Contains("Surum tutarsizligi", sonuc.Tumu);
        Assert.Contains("release-body.json", sonuc.Tumu);
    }

    /// <summary>
    /// Arayuz surumu kaynaktan farkliysa DUR.
    /// </summary>
    [Fact]
    public void Arayuz_surumu_kaynaktan_farkliysa_paket_uretilmez()
    {
        var depo = SahteDepoKur(kaynak: "0.4.23", packageJson: "0.2.34", iss: "0.4.23", releaseTag: "0.4.23");

        var sonuc = KapiyiCalistir(depo, requestedVersion: null);

        Assert.NotEqual(0, sonuc.ExitCode);
        Assert.Contains("frontend/package.json", sonuc.Tumu);
    }

    /// <summary>
    /// Installer'daki yedek surum tanimi kaynaktan farkliysa DUR.
    /// </summary>
    /// <remarks>
    /// Scriptler <c>/DMyAppVersion</c> ile bu degeri ezer -- ama .iss ELLE derlenirse bu deger
    /// damgalanir. Stale kalan bir yedek tanim "elle derlenmis paket yanlis surum tasir"
    /// demektir.
    /// </remarks>
    [Fact]
    public void Installer_yedek_surumu_kaynaktan_farkliysa_paket_uretilmez()
    {
        var depo = SahteDepoKur(kaynak: "0.4.23", packageJson: "0.4.23", iss: "0.2.28", releaseTag: "0.4.23");

        var sonuc = KapiyiCalistir(depo, requestedVersion: null);

        Assert.NotEqual(0, sonuc.ExitCode);
        Assert.Contains("installer/AdaPanel.iss", sonuc.Tumu);
    }

    /// <summary>
    /// GERCEK DEPO: paketlemeyi belirleyen dort kaynak birbirine esit olmali.
    /// </summary>
    /// <remarks>
    /// Sahte depolarla yapilan testler mantigi dogrular; bu test DEPONUN KENDISINI dogrular.
    /// Biri kaymissa yayin hatti zaten duracaktir -- bunu paketleme aninda degil, testte
    /// ogrenmek daha ucuzdur.
    /// </remarks>
    [Fact]
    public void Gercek_deponun_surumleri_tutarli()
    {
        var sonuc = KapiyiCalistir(ScriptTestHost.RepoRoot, requestedVersion: null);

        Assert.True(
            sonuc.ExitCode == 0,
            $"Depodaki surumler tutarsiz -- yayin hatti duracak:\n{sonuc.Tumu}");
    }

    // --- duzenek ---

    private static ScriptTestHost.ScriptResult KapiyiCalistir(string repoRoot, string? requestedVersion)
    {
        var kapi = ScriptTestHost.ScriptPath("scripts", "version-guard.ps1");
        Assert.True(File.Exists(kapi), $"Surum kapisi bulunamadi: {kapi}");

        var istenen = requestedVersion is null
            ? "$null"
            : ScriptTestHost.PowerShellLiteral(requestedVersion);

        return ScriptTestHost.Run($"""
            . {ScriptTestHost.PowerShellLiteral(kapi)}
            $surum = Resolve-AdaReleaseVersion -RepoRoot {ScriptTestHost.PowerShellLiteral(repoRoot)} -RequestedVersion {istenen}
            Write-Output "SURUM=$surum"
            """);
    }

    private string SahteDepoKur(string kaynak, string packageJson, string iss, string releaseTag)
    {
        var depo = Path.Combine(calisma, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(depo, "frontend"));
        Directory.CreateDirectory(Path.Combine(depo, "installer"));

        File.WriteAllText(Path.Combine(depo, "Directory.Build.props"), $"""
            <Project>
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <Version>{kaynak}</Version>
              </PropertyGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(depo, "frontend", "package.json"),
            $$"""{ "name": "adapanel", "version": "{{packageJson}}" }""");

        File.WriteAllText(Path.Combine(depo, "frontend", "package-lock.json"),
            $$"""{ "name": "adapanel", "version": "{{packageJson}}", "lockfileVersion": 3 }""");

        File.WriteAllText(Path.Combine(depo, "installer", "AdaPanel.iss"), $"""
            #define MyAppName "AdaPanel"
            #ifndef MyAppVersion
              #define MyAppVersion "{iss}"
            #endif
            """);

        File.WriteAllText(Path.Combine(depo, "release-body.json"),
            $$"""{"tag_name": "{{releaseTag}}", "name": "{{releaseTag}}", "body": "x"}""");

        return depo;
    }
}
