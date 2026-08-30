using System.Diagnostics;
using System.IO.Compression;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Files;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Dosya yöneticisinin ARŞİV ÇIKARMA ucunun sanal alan korumaları.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı bir test dosyası ve neden GERÇEK dosya sistemi:</b> çıkarma, panelin kiracı
/// girdisinden gelen bir yolu diske YAZDIĞI tek yerdir ki o yolu ne istek gövdesi ne de sanal yol
/// çözümleyicisi belirler — <b>arşivin içindeki girdi adı</b> belirler. Yani buradaki koruma,
/// panelin geri kalanındaki <c>ResolveTargetAsync</c> kapısının HİÇ görmediği bir değeri denetler.
/// Kaynak taramasıyla ölçülemez: "girdi adı kökün altında mı" sorusunun cevabı Windows'un yol
/// çözümlemesine (ve diskte duran bağlantılara) bağlıdır, dizge karşılaştırmasına değil.
/// </para>
/// <para>
/// <b>Bu dosya bir eksikliği kapatıyor:</b> <c>ExtractAsync</c>'in HİÇ testi yoktu (ölçüldü:
/// <c>tests/</c> altında tek bir <c>ExtractAsync</c> çağrısı yok, yalnız <c>FileQuotaTests</c>'te
/// denetleyici imzasının dizge araması var). Panel LocalSystem olarak koştuğu için bu uçtan kaçan
/// bir yazma, kiracının SYSTEM yetkisiyle disk üzerinde istediği yere yazması demektir.
/// </para>
/// </remarks>
public sealed class FileExtractTests : IDisposable
{
    private readonly string kok;
    private readonly string siteAKoku;
    private readonly string siteBKoku;
    private readonly string disariKok;
    private readonly FileService servis;

    public FileExtractTests()
    {
        kok = Path.Combine(Path.GetTempPath(), "adapanel-extract-" + Guid.NewGuid().ToString("N"));
        siteAKoku = Path.Combine(kok, "a.test");
        siteBKoku = Path.Combine(kok, "b.test");
        disariKok = Path.Combine(kok, "disari");
        Directory.CreateDirectory(siteAKoku);
        Directory.CreateDirectory(siteBKoku);
        Directory.CreateDirectory(disariKok);

        servis = new FileService(new SahteExtractSiteServisi(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["a.test"] = siteAKoku,
            ["b.test"] = siteBKoku
        }));
    }

    public void Dispose()
    {
        try
        {
            // Bağlantılar ÖNCE kaldırılır: özyinelemeli silme bir junction'ı takip ederse testin
            // kendisi, ölçmeye çalıştığı veri kaybını üretirdi. (Aynı gerekçe FileCopyTests'te.)
            foreach (var dizin in Directory.EnumerateDirectories(kok, "*", SearchOption.AllDirectories).Reverse())
            {
                var bilgi = new DirectoryInfo(dizin);
                if (bilgi.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Directory.Delete(dizin);
                }
            }

            Directory.Delete(kok, recursive: true);
        }
        catch
        {
            // Geçici klasör temizliği en iyi çaba.
        }
    }

    // ------------------------------------------------------------------
    // MEŞRU KULLANIM — koruma daraltılırken KIRILMAMASI gerekenler
    // ------------------------------------------------------------------

    [Fact]
    public async Task Sade_arsiv_ACILIR_ve_alt_klasorler_kurulur()
    {
        ArsivYaz(Path.Combine(siteAKoku, "paket.zip"), new Dictionary<string, string>
        {
            ["index.php"] = "<?php",
            ["wp-content/themes/tema/style.css"] = "body{}"
        });

        await servis.ExtractAsync(new ArchiveFileRequest
        {
            Path = "sites/a.test/paket.zip",
            DestinationPath = "sites/a.test/hedef"
        });

        Assert.Equal("<?php", File.ReadAllText(Path.Combine(siteAKoku, "hedef", "index.php")));
        Assert.Equal("body{}", File.ReadAllText(Path.Combine(siteAKoku, "hedef", "wp-content", "themes", "tema", "style.css")));
    }

    /// <summary>
    /// Türkçe harf, boşluk ve nokta içeren MEŞRU adlar çıkarılabilmeli.
    /// </summary>
    /// <remarks>
    /// Bu depoda bir korumayı daraltırken meşru kullanımı kırmak, kapatılan açıktan pahalıya
    /// mal olur: müşterinin "Şirket Belgeleri" klasörü açılamazsa panel işini yapmıyordur.
    /// Bu yüzden aşağıdaki kaçış testleriyle AYNI dosyada duruyor — daraltma ölçülür.
    /// </remarks>
    [Fact]
    public async Task Turkce_ve_bosluklu_adlar_CIKARILIR()
    {
        ArsivYaz(Path.Combine(siteAKoku, "paket.zip"), new Dictionary<string, string>
        {
            ["Şirket Belgeleri/güncel rapor.txt"] = "içerik",
            ["ıİşÇöĞü/ağ.txt"] = "veri"
        });

        await servis.ExtractAsync(new ArchiveFileRequest
        {
            Path = "sites/a.test/paket.zip",
            DestinationPath = "sites/a.test/hedef"
        });

        Assert.Equal("içerik", File.ReadAllText(Path.Combine(siteAKoku, "hedef", "Şirket Belgeleri", "güncel rapor.txt")));
        Assert.Equal("veri", File.ReadAllText(Path.Combine(siteAKoku, "hedef", "ıİşÇöĞü", "ağ.txt")));
    }

    // ------------------------------------------------------------------
    // ZIP SLIP — girdi adının KENDİSİ yol kaçışı
    // ------------------------------------------------------------------

    [Fact]
    public async Task Girdi_adindaki_UST_KLASOR_kacisi_REDDEDILIR()
    {
        ArsivYaz(Path.Combine(siteAKoku, "kotu.zip"), new Dictionary<string, string>
        {
            ["../../disari/kacti.txt"] = "kacti"
        });

        await Assert.ThrowsAsync<AppException>(() => servis.ExtractAsync(new ArchiveFileRequest
        {
            Path = "sites/a.test/kotu.zip",
            DestinationPath = "sites/a.test/hedef"
        }));

        Assert.False(File.Exists(Path.Combine(disariKok, "kacti.txt")));
    }

    [Fact]
    public async Task Girdi_adindaki_TERS_BOLU_kacisi_REDDEDILIR()
    {
        // Zip biçimi ayırıcı olarak yalnız '/' der; saldırgan arşivleri ters bölü kullanır çünkü
        // '/' arayan bir süzgeç onu görmez. Panelin kapısı ham dizgeye değil, Windows'un ÇÖZDÜĞÜ
        // tam yola bakmak zorundadır.
        ArsivYaz(Path.Combine(siteAKoku, "kotu.zip"), new Dictionary<string, string>
        {
            ["..\\..\\disari\\kacti.txt"] = "kacti"
        });

        await Assert.ThrowsAsync<AppException>(() => servis.ExtractAsync(new ArchiveFileRequest
        {
            Path = "sites/a.test/kotu.zip",
            DestinationPath = "sites/a.test/hedef"
        }));

        Assert.False(File.Exists(Path.Combine(disariKok, "kacti.txt")));
    }

    [Fact]
    public async Task Girdi_adindaki_MUTLAK_yol_REDDEDILIR()
    {
        var mutlak = Path.Combine(disariKok, "mutlak.txt");
        ArsivYaz(Path.Combine(siteAKoku, "kotu.zip"), new Dictionary<string, string>
        {
            [mutlak] = "mutlak"
        });

        await Assert.ThrowsAsync<AppException>(() => servis.ExtractAsync(new ArchiveFileRequest
        {
            Path = "sites/a.test/kotu.zip",
            DestinationPath = "sites/a.test/hedef"
        }));

        Assert.False(File.Exists(mutlak));
    }

    [Fact]
    public async Task KOMSU_KIRACIYA_yazma_REDDEDILIR()
    {
        ArsivYaz(Path.Combine(siteAKoku, "kotu.zip"), new Dictionary<string, string>
        {
            ["../b.test/shell.php"] = "<?php system($_GET[0]);"
        });

        await Assert.ThrowsAsync<AppException>(() => servis.ExtractAsync(new ArchiveFileRequest
        {
            Path = "sites/a.test/kotu.zip",
            DestinationPath = "sites/a.test/hedef"
        }));

        Assert.False(File.Exists(Path.Combine(siteBKoku, "shell.php")));
    }

    // ------------------------------------------------------------------
    // BAĞLANTI (junction/symlink) KAÇIŞI — dizge denetiminin GÖREMEDİĞİ yol
    // ------------------------------------------------------------------

    /// <summary>
    /// Kiracının kendi kökünde duran bir KAVŞAK (junction) üzerinden çıkarma, sanal alanın
    /// dışına yazmamalı.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden bu senaryo gerçek:</b> paylaşımlı Windows barındırmada kiracı kendi sitesinde KOD
    /// çalıştırır (PHP/ASP). Kavşak oluşturmak Windows'ta AYRICALIK GEREKTİRMEZ (sembolik bağın
    /// aksine): <c>mklink /J</c> sıradan bir kullanıcıyla çalışır. Yani kiracı, kendi belge
    /// kökünde <c>C:\Windows</c>'a bakan bir kavşak bırakıp panelden "çıkart" diyebilir.
    /// </para>
    /// <para>
    /// <b>Neden dizge denetimi yetmez:</b> <c>&lt;kök&gt;\bag\kotu.txt</c> yolu KÖKÜN ALTINDADIR —
    /// önek karşılaştırması geçer. Kaçış dizgede değil, DİSKTE olur: yazma <c>bag</c>'ı takip eder.
    /// Panel LocalSystem olduğu için hedefte ACL de durdurmaz. Bu, bu depodaki tekrarlayan kalıbın
    /// tam örneğidir: denetim var, ama denetlenen değeri saldırgan belirliyor.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Kok_ICINDEKI_JUNCTION_uzerinden_disari_yazilamaz()
    {
        var baglanti = Path.Combine(siteAKoku, "hedef", "bag");
        Directory.CreateDirectory(Path.Combine(siteAKoku, "hedef"));
        JunctionOlustur(baglanti, disariKok);
        Assert.True(Directory.Exists(baglanti), "Junction kurulamadi; test onkosulu saglanmadi.");

        ArsivYaz(Path.Combine(siteAKoku, "kotu.zip"), new Dictionary<string, string>
        {
            ["bag/kacti.txt"] = "kacti"
        });

        await Assert.ThrowsAsync<AppException>(() => servis.ExtractAsync(new ArchiveFileRequest
        {
            Path = "sites/a.test/kotu.zip",
            DestinationPath = "sites/a.test/hedef"
        }));

        Assert.False(
            File.Exists(Path.Combine(disariKok, "kacti.txt")),
            "Kavsak TAKIP EDILDI: arsiv girdisi sanal alanin DISINA yazildi.");
    }

    /// <summary>
    /// Kavşak, çıkarmanın HEDEF yolunun kendisi olduğunda da reddedilmeli.
    /// </summary>
    [Fact]
    public async Task HEDEF_yolun_kendisi_JUNCTION_ise_REDDEDILIR()
    {
        var baglanti = Path.Combine(siteAKoku, "bag");
        JunctionOlustur(baglanti, disariKok);
        Assert.True(Directory.Exists(baglanti), "Junction kurulamadi; test onkosulu saglanmadi.");

        ArsivYaz(Path.Combine(siteAKoku, "kotu.zip"), new Dictionary<string, string>
        {
            ["kacti.txt"] = "kacti"
        });

        await Assert.ThrowsAsync<AppException>(() => servis.ExtractAsync(new ArchiveFileRequest
        {
            Path = "sites/a.test/kotu.zip",
            DestinationPath = "sites/a.test/bag"
        }));

        Assert.False(File.Exists(Path.Combine(disariKok, "kacti.txt")));
    }

    /// <summary>
    /// Arşivin KENDİSİ bir bağlantı üzerinden gösterilemez (okuma tarafı).
    /// </summary>
    [Fact]
    public async Task ARSIV_yolu_JUNCTION_uzerinden_gosterilemez()
    {
        var disaridakiArsiv = Path.Combine(disariKok, "gizli.zip");
        ArsivYaz(disaridakiArsiv, new Dictionary<string, string> { ["x.txt"] = "x" });

        var baglanti = Path.Combine(siteAKoku, "bag");
        JunctionOlustur(baglanti, disariKok);
        Assert.True(Directory.Exists(baglanti), "Junction kurulamadi; test onkosulu saglanmadi.");

        await Assert.ThrowsAsync<AppException>(() => servis.ExtractAsync(new ArchiveFileRequest
        {
            Path = "sites/a.test/bag/gizli.zip",
            DestinationPath = "sites/a.test/hedef"
        }));
    }

    // --- düzenek ---

    private static void ArsivYaz(string yol, IReadOnlyDictionary<string, string> girdiler)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(yol)!);
        if (File.Exists(yol))
        {
            File.Delete(yol);
        }

        using var akis = File.Create(yol);
        using var arsiv = new ZipArchive(akis, ZipArchiveMode.Create);
        foreach (var (ad, icerik) in girdiler)
        {
            // CreateEntry HAM adı yazar (CreateEntryFromFile'ın aksine normalleştirmez) — kötü
            // niyetli girdi adını üretmenin tek yolu budur.
            var girdi = arsiv.CreateEntry(ad);
            using var yazici = new StreamWriter(girdi.Open());
            yazici.Write(icerik);
        }
    }

    private static void JunctionOlustur(string link, string hedef)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using var surec = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{hedef}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        surec.WaitForExit(60_000);
    }

    /// <summary>Site adını fiziksel köke çeviren sahte; başka hiçbir IIS işlemi kullanılmaz.</summary>
    private sealed class SahteExtractSiteServisi(IReadOnlyDictionary<string, string> kokler) : IIisSiteService
    {
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default)
        {
            if (!kokler.TryGetValue(siteName, out var kok))
            {
                throw new AppException("sites.not_found", 404, 404);
            }

            return Task.FromResult(new SiteDto
            {
                Name = siteName,
                PhysicalPath = kok,
                ApplicationRootPath = kok
            });
        }

        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
