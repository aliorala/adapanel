using System.Diagnostics;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Files;
using AdaPanel.Infrastructure.Iis;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// G2/G3/G7/G8: kiracının kurduğu junction, SYSTEM olarak çalışan panelin diskte başka yere
/// dokunmasına yol açmamalı. Testler gerçek junction kurar; hepsi geçici klasörde.
/// </summary>
public sealed class ReparseRootGuardTests : IDisposable
{
    private readonly string _kok = Path.Combine(Path.GetTempPath(), "adapanel-reparse-" + Guid.NewGuid().ToString("N"));
    private readonly string _site;
    private readonly string _disari;

    public ReparseRootGuardTests()
    {
        _site = Path.Combine(_kok, "site");
        _disari = Path.Combine(_kok, "disari");
        Directory.CreateDirectory(_site);
        Directory.CreateDirectory(_disari);
        File.WriteAllText(Path.Combine(_disari, "gizli.txt"), "SIR");
    }

    public void Dispose()
    {
        try
        {
            foreach (var dizin in Directory.EnumerateDirectories(_kok, "*", SearchOption.AllDirectories).Reverse())
            {
                if (new DirectoryInfo(dizin).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Directory.Delete(dizin);
                }
            }

            Directory.Delete(_kok, recursive: true);
        }
        catch
        {
            // en iyi çaba
        }
    }

    private static void Junction(string link, string hedef)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /c mklink /J \"{link}\" \"{hedef}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        Assert.True(process.WaitForExit(10_000));
        Assert.Equal(0, process.ExitCode);
    }

    private string Dangling()
    {
        var hedef = Path.Combine(_kok, "gecici-hedef");
        Directory.CreateDirectory(hedef);
        var link = Path.Combine(_site, "dangling");
        Junction(link, hedef);
        Directory.Delete(hedef);
        return link;
    }

    // ---- ReparseGuard (G8)

    [Fact]
    public void Dangling_junction_tespit_edilir_ve_olmayan_yol_baglanti_degildir()
    {
        var link = Dangling();

        Assert.True(ReparseGuard.IsReparsePoint(link));
        Assert.True(ReparseGuard.HasReparsePoint(_site, Path.Combine(link, "alt", "dosya.txt")));
        Assert.False(ReparseGuard.IsReparsePoint(Path.Combine(_site, "yok")));
        Assert.False(ReparseGuard.HasReparsePoint(_site, Path.Combine(_site, "yok", "dosya.txt")));
    }

    [Fact]
    public void Kokun_kendisi_baglantiysa_bulunur()
    {
        var x = Path.Combine(_site, "x");
        Junction(x, _disari);

        Assert.Equal(x, ReparseGuard.FindReparsePoint(x, Path.Combine(x, "gizli.txt"), includeRoot: true));
        Assert.Null(ReparseGuard.FindReparsePoint(x, Path.Combine(x, "gizli.txt"), includeRoot: false));
    }

    // ---- Belge kökü kabulü (G2)

    [Fact]
    public void BelgeKoku_junction_kabul_edilmez_normal_klasor_edilir()
    {
        var x = Path.Combine(_site, "x");
        Junction(x, _disari);
        var duz = Path.Combine(_site, "public");
        Directory.CreateDirectory(duz);

        Assert.False(SiteDocumentRootScope.IsAllowed(x, _site, null));
        Assert.False(SiteDocumentRootScope.IsAllowed(Path.Combine(x, "icerik"), _site, null));
        Assert.False(SiteDocumentRootScope.IsAllowed(Dangling(), _site, null));
        Assert.True(SiteDocumentRootScope.IsAllowed(duz, _site, null));
        Assert.True(SiteDocumentRootScope.IsAllowed(_site, _site, null));
    }

    // ---- FileService sandbox kökü (G2)

    [Fact]
    public async Task FileService_sandbox_koku_junction_ise_okuma_ve_indirme_reddedilir()
    {
        var x = Path.Combine(_site, "x");
        Junction(x, _disari);
        var servis = new FileService(new SahteSiteServisi(x));

        var liste = await Assert.ThrowsAsync<AppException>(() => servis.GetWorkspaceAsync("sites/a.test"));
        var indir = await Assert.ThrowsAsync<AppException>(() =>
            servis.OpenDownloadAsync("sites/a.test/gizli.txt", (_, _) => Task.CompletedTask, CancellationToken.None));

        Assert.Equal("files.path_outside_root", liste.MessageKey);
        Assert.Equal("files.path_outside_root", indir.MessageKey);
    }

    [Fact]
    public async Task FileService_sandbox_koku_junction_ise_yazma_silme_reddedilir()
    {
        var x = Path.Combine(_site, "x");
        Junction(x, _disari);
        var servis = new FileService(new SahteSiteServisi(x));

        await Assert.ThrowsAsync<AppException>(() => servis.CalculateSizeAsync("sites/a.test/gizli.txt"));
        Assert.True(File.Exists(Path.Combine(_disari, "gizli.txt")));
        Assert.Equal("SIR", File.ReadAllText(Path.Combine(_disari, "gizli.txt")));
    }

    [Fact]
    public async Task FileService_dangling_junction_alt_yolu_reddedilir()
    {
        var link = Dangling();
        var servis = new FileService(new SahteSiteServisi(_site));

        var hata = await Assert.ThrowsAsync<AppException>(() =>
            servis.GetWorkspaceAsync("sites/a.test/dangling"));

        Assert.Equal("files.path_outside_root", hata.MessageKey);
        Assert.True(Directory.Exists(_kok));
        Assert.NotNull(link);
    }

    // ---- Yayıncı (G3)

    [Fact]
    public void Yayinci_junction_kokte_hicbir_sey_silmez_yazmaz()
    {
        File.WriteAllText(Path.Combine(_disari, "index.php"), "<?php");
        File.WriteAllText(Path.Combine(_disari, "index.html"), "KORU");
        var x = Path.Combine(_site, "x");
        Junction(x, _disari);

        Assert.Throws<AppException>(() => WebAppPublisher.EnsurePhpEntry(x));
        Assert.False(WebAppPublisher.TryWriteFrontControllerRewrite(x, moduleAvailable: true));
        Assert.Throws<AppException>(() => WebAppPublisher.SetMaxAllowedContentLength(x, 1024));

        Assert.True(File.Exists(Path.Combine(_disari, "index.html")));
        Assert.False(File.Exists(Path.Combine(_disari, "web.config")));
    }

    [Fact]
    public void Yayinci_normal_kokte_calismaya_devam_eder()
    {
        var kok = Path.Combine(_site, "public");
        Directory.CreateDirectory(kok);
        File.WriteAllText(Path.Combine(kok, "index.php"), "<?php");
        File.WriteAllText(Path.Combine(kok, "index.html"), "x");

        WebAppPublisher.EnsurePhpEntry(kok);

        Assert.False(File.Exists(Path.Combine(kok, "index.html")));
        Assert.True(File.Exists(Path.Combine(kok, "web.config")));
    }

    // ---- Tutamaç son yolu (G7)

    [Fact]
    public void Tutamac_son_yolu_sandbox_disindaysa_reddedilir()
    {
        var icerde = Path.Combine(_site, "a.txt");
        File.WriteAllText(icerde, "a");
        var disarda = Path.Combine(_disari, "gizli.txt");

        using (var ok = new FileStream(icerde, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            FinalPathGuard.EnsureInside(ok, _site);
        }

        using var kotu = new FileStream(disarda, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hata = Assert.Throws<AppException>(() => FinalPathGuard.EnsureInside(kotu, _site));
        Assert.Equal("files.path_outside_root", hata.MessageKey);
    }

    [Fact]
    public void Tutamac_junction_uzerinden_acilsa_bile_gercek_yolla_reddedilir()
    {
        var x = Path.Combine(_site, "x");
        Junction(x, _disari);

        // Yol denetiminden SONRA bağlantının araya girdiği yarış: akış junction üzerinden açılır.
        using var akis = new FileStream(Path.Combine(x, "gizli.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Throws<AppException>(() => FinalPathGuard.EnsureInside(akis, _site));
    }
}
