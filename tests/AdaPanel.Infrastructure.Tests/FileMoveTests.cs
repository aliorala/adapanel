using System.Diagnostics;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Files;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Dosya TAŞIMA / YENİDEN ADLANDIRMA ucunun davranışsal korumaları.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden bu testler:</b> taşıma, dosya yöneticisinin en yıkıcı işlemidir — bir öge YER
/// DEĞİŞTİRİR, yani eski yerinden SİLİNİR. Yanlış yapıldığında sonucu veri kaybıdır ve geri alınamaz.
/// Panelde bu uç hiç yoktu; eklerken korunması gereken sınırlar (sanal alan, klasör bağlantıları,
/// üzerine yazma, kendi içine taşıma) burada gerçek dosya sistemi üzerinde ölçülür — kaynak
/// taramasıyla değil.
/// </para>
/// </remarks>
public sealed class FileMoveTests : IDisposable
{
    private readonly string kok;
    private readonly string siteAKoku;
    private readonly string siteBKoku;
    private readonly string disariKok;
    private readonly FileService servis;

    public FileMoveTests()
    {
        kok = Path.Combine(Path.GetTempPath(), "adapanel-move-" + Guid.NewGuid().ToString("N"));
        siteAKoku = Path.Combine(kok, "a.test");
        siteBKoku = Path.Combine(kok, "b.test");
        disariKok = Path.Combine(kok, "disari");
        Directory.CreateDirectory(siteAKoku);
        Directory.CreateDirectory(siteBKoku);
        Directory.CreateDirectory(disariKok);

        servis = new FileService(new SahteSiteServisi(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["a.test"] = siteAKoku,
            ["b.test"] = siteBKoku
        }));
    }

    public void Dispose()
    {
        try
        {
            // Bağlantılar önce KALDIRILIR: özyinelemeli silme bir junction'ı takip ederse testin
            // kendisi, korumaya çalıştığı veri kaybını üretirdi.
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
    // SANAL ALAN
    // ------------------------------------------------------------------

    [Fact]
    public async Task Sanal_alan_DISINA_tasima_REDDEDILIR()
    {
        DosyaYaz(Path.Combine(siteAKoku, "rapor.txt"), "gizli");

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.MoveAsync(new MoveFilesRequest
        {
            Paths = ["sites/a.test/rapor.txt"],
            DestinationPath = "sites/a.test/../disari"
        }));

        Assert.Equal("files.path_outside_root", hata.MessageKey);

        // Reddedilmek yetmez: dosya YERİNDE kalmalı, hedefte de oluşmamalı.
        Assert.True(File.Exists(Path.Combine(siteAKoku, "rapor.txt")));
        Assert.False(File.Exists(Path.Combine(disariKok, "rapor.txt")));
    }

    [Fact]
    public async Task Kaynak_sanal_alan_DISINDAYSA_istek_REDDEDILIR()
    {
        DosyaYaz(Path.Combine(disariKok, "hedef-disi.txt"), "panel dosyasi");
        Directory.CreateDirectory(Path.Combine(siteAKoku, "gelen"));

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.MoveAsync(new MoveFilesRequest
        {
            Paths = ["sites/a.test/../disari/hedef-disi.txt"],
            DestinationPath = "sites/a.test/gelen"
        }));

        Assert.Equal("files.path_outside_root", hata.MessageKey);
        Assert.True(File.Exists(Path.Combine(disariKok, "hedef-disi.txt")));
    }

    [Fact]
    public async Task Yeni_ADIN_icine_YOL_konarak_sanal_alandan_CIKILAMAZ()
    {
        DosyaYaz(Path.Combine(siteAKoku, "rapor.txt"), "gizli");

        // Ad alanı bir yol kabul etseydi, hedef klasör denetimi tamamen anlamsızlaşırdı: istek
        // meşru bir hedef gösterip dosyayı bambaşka bir yere yazdırabilirdi.
        var hata = await Assert.ThrowsAsync<AppException>(() => servis.MoveAsync(new MoveFilesRequest
        {
            Paths = ["sites/a.test/rapor.txt"],
            DestinationPath = "sites/a.test",
            NewName = @"..\..\disari\kacak.txt"
        }));

        Assert.Equal("files.invalid_name", hata.MessageKey);
        Assert.False(File.Exists(Path.Combine(disariKok, "kacak.txt")));
        Assert.True(File.Exists(Path.Combine(siteAKoku, "rapor.txt")));
    }

    [Fact]
    public async Task Site_KOKU_tasinamaz()
    {
        Directory.CreateDirectory(Path.Combine(siteBKoku, "gelen"));

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.MoveAsync(new MoveFilesRequest
        {
            Paths = ["sites/a.test"],
            DestinationPath = "sites/b.test/gelen"
        }));

        // Site kökünü taşımak, siteyi belge kökünden koparmak demektir: site anında ölür.
        Assert.Equal("files.site_root_protected", hata.MessageKey);
        Assert.True(Directory.Exists(siteAKoku));
    }

    // ------------------------------------------------------------------
    // KLASÖR BAĞLANTISI (junction)
    // ------------------------------------------------------------------

    [Fact]
    public void Junction_TAKIP_EDILMEZ_ve_baglantinin_HEDEFI_korunur()
    {
        var gercekSite = Path.Combine(kok, "gercek-site");
        Directory.CreateDirectory(gercekSite);
        DosyaYaz(Path.Combine(gercekSite, "onemli.txt"), "musteri sitesi");

        var baglanti = Path.Combine(siteAKoku, "baglanti");
        JunctionOlustur(baglanti, gercekSite);
        Assert.True(Directory.Exists(baglanti), "Junction kurulamadi; test onkosulu saglanmadi.");
        Directory.CreateDirectory(Path.Combine(siteAKoku, "gelen"));

        var hata = Assert.ThrowsAsync<AppException>(() => servis.MoveAsync(new MoveFilesRequest
        {
            Paths = ["sites/a.test/baglanti"],
            DestinationPath = "sites/a.test/gelen"
        })).GetAwaiter().GetResult();

        // Bir bağlantıyı taşımak, hedefindeki MÜŞTERİ SİTESİNİ başka bir yere bağlamak demektir;
        // bu panelde daha önce yaşanmış bir hata sınıfıdır (özyinelemeli silmenin bağlantıyı takip
        // etmesi). Bağlantı da hedefi de olduğu gibi durmalı.
        Assert.Equal("files.path_outside_root", hata.MessageKey);
        Assert.True(File.Exists(Path.Combine(gercekSite, "onemli.txt")));
        Assert.True(Directory.Exists(baglanti));
        Assert.False(Directory.Exists(Path.Combine(siteAKoku, "gelen", "baglanti")));
    }

    // ------------------------------------------------------------------
    // ÜZERİNE YAZMA
    // ------------------------------------------------------------------

    [Fact]
    public async Task Hedefte_AYNI_AD_varsa_sessizce_uzerine_YAZILMAZ()
    {
        Directory.CreateDirectory(Path.Combine(siteAKoku, "kaynak"));
        Directory.CreateDirectory(Path.Combine(siteAKoku, "hedef"));
        DosyaYaz(Path.Combine(siteAKoku, "kaynak", "rapor.txt"), "YENI");
        DosyaYaz(Path.Combine(siteAKoku, "hedef", "rapor.txt"), "ESKI");

        var sonuc = await servis.MoveAsync(new MoveFilesRequest
        {
            Paths = ["sites/a.test/kaynak/rapor.txt"],
            DestinationPath = "sites/a.test/hedef"
        });

        Assert.Equal(0, sonuc.MovedCount);
        Assert.Equal(1, sonuc.FailedCount);
        Assert.Equal("files.move_target_exists", Assert.Single(sonuc.Items).ErrorKey);

        // İki dosya da AYNEN yerinde: ne hedef ezildi ne kaynak kayboldu.
        Assert.Equal("ESKI", File.ReadAllText(Path.Combine(siteAKoku, "hedef", "rapor.txt")));
        Assert.Equal("YENI", File.ReadAllText(Path.Combine(siteAKoku, "kaynak", "rapor.txt")));
    }

    [Fact]
    public async Task Uzerine_yazma_ACIKCA_istendiginde_yapilir()
    {
        Directory.CreateDirectory(Path.Combine(siteAKoku, "kaynak"));
        Directory.CreateDirectory(Path.Combine(siteAKoku, "hedef"));
        DosyaYaz(Path.Combine(siteAKoku, "kaynak", "rapor.txt"), "YENI");
        DosyaYaz(Path.Combine(siteAKoku, "hedef", "rapor.txt"), "ESKI");

        var sonuc = await servis.MoveAsync(new MoveFilesRequest
        {
            Paths = ["sites/a.test/kaynak/rapor.txt"],
            DestinationPath = "sites/a.test/hedef",
            Overwrite = true
        });

        // Reddetmek VARSAYILAN; yasak değil. Kullanıcı bilerek istediğinde iş yapılabilmeli.
        Assert.Equal(1, sonuc.MovedCount);
        Assert.Equal("YENI", File.ReadAllText(Path.Combine(siteAKoku, "hedef", "rapor.txt")));
        Assert.False(File.Exists(Path.Combine(siteAKoku, "kaynak", "rapor.txt")));
    }

    [Fact]
    public async Task Var_olan_KLASOR_uzerine_Overwrite_ile_bile_yazilmaz()
    {
        Directory.CreateDirectory(Path.Combine(siteAKoku, "kaynak", "veri"));
        Directory.CreateDirectory(Path.Combine(siteAKoku, "hedef", "veri"));
        DosyaYaz(Path.Combine(siteAKoku, "kaynak", "veri", "yeni.txt"), "yeni");
        DosyaYaz(Path.Combine(siteAKoku, "hedef", "veri", "eski.txt"), "eski");

        var sonuc = await servis.MoveAsync(new MoveFilesRequest
        {
            Paths = ["sites/a.test/kaynak/veri"],
            DestinationPath = "sites/a.test/hedef",
            Overwrite = true
        });

        // Klasörü ezmek, hedefin ÖZYİNELEMELİ SİLİNMESİ demektir; bu panelde tam da bu işlem bir
        // kez müşteri sitesini götürmeye ramak kalmıştı. Tek bir bayrağın arkasına konmaz.
        Assert.Equal(1, sonuc.FailedCount);
        Assert.Equal("files.move_target_exists", Assert.Single(sonuc.Items).ErrorKey);
        Assert.True(File.Exists(Path.Combine(siteAKoku, "hedef", "veri", "eski.txt")));
        Assert.True(File.Exists(Path.Combine(siteAKoku, "kaynak", "veri", "yeni.txt")));
    }

    // ------------------------------------------------------------------
    // KENDİ ALT AĞACINA TAŞIMA
    // ------------------------------------------------------------------

    [Fact]
    public async Task Klasor_KENDI_ALT_AGACINA_tasinamaz()
    {
        Directory.CreateDirectory(Path.Combine(siteAKoku, "ust", "alt"));
        DosyaYaz(Path.Combine(siteAKoku, "ust", "icerik.txt"), "veri");

        var sonuc = await servis.MoveAsync(new MoveFilesRequest
        {
            Paths = ["sites/a.test/ust"],
            DestinationPath = "sites/a.test/ust/alt"
        });

        Assert.Equal("files.move_into_itself", Assert.Single(sonuc.Items).ErrorKey);
        Assert.True(File.Exists(Path.Combine(siteAKoku, "ust", "icerik.txt")));
    }

    [Fact]
    public async Task Ogeyi_BULUNDUGU_klasore_tasimak_isi_yokmus_gibi_gostermez()
    {
        DosyaYaz(Path.Combine(siteAKoku, "rapor.txt"), "veri");

        var sonuc = await servis.MoveAsync(new MoveFilesRequest
        {
            Paths = ["sites/a.test/rapor.txt"],
            DestinationPath = "sites/a.test"
        });

        Assert.Equal("files.move_same_path", Assert.Single(sonuc.Items).ErrorKey);
        Assert.True(File.Exists(Path.Combine(siteAKoku, "rapor.txt")));
    }

    // ------------------------------------------------------------------
    // KISMİ BAŞARISIZLIK
    // ------------------------------------------------------------------

    [Fact]
    public async Task Kismi_basarisizlik_DURUSTCE_raporlanir()
    {
        Directory.CreateDirectory(Path.Combine(siteAKoku, "kaynak"));
        Directory.CreateDirectory(Path.Combine(siteAKoku, "hedef"));
        DosyaYaz(Path.Combine(siteAKoku, "kaynak", "bir.txt"), "1");
        DosyaYaz(Path.Combine(siteAKoku, "kaynak", "iki.txt"), "2");
        DosyaYaz(Path.Combine(siteAKoku, "hedef", "iki.txt"), "hedefte-var");

        var sonuc = await servis.MoveAsync(new MoveFilesRequest
        {
            Paths = ["sites/a.test/kaynak/bir.txt", "sites/a.test/kaynak/iki.txt", "sites/a.test/kaynak/yok.txt"],
            DestinationPath = "sites/a.test/hedef"
        });

        // Bir ögenin takılması diğerlerini durdurmaz, ama SESSİZCE de geçilmez: hangisinin neden
        // taşınmadığı isim isim raporlanır. "Hepsi tamam" diyen bir cevap, kullanıcıyı dosyasını
        // yanlış klasörde aramaya gönderirdi.
        Assert.Equal(1, sonuc.MovedCount);
        Assert.Equal(2, sonuc.FailedCount);
        Assert.Equal(3, sonuc.Items.Length);

        Assert.True(sonuc.Items.Single(item => item.SourcePath.EndsWith("bir.txt", StringComparison.Ordinal)).Moved);
        Assert.Equal(
            "files.move_target_exists",
            sonuc.Items.Single(item => item.SourcePath.EndsWith("iki.txt", StringComparison.Ordinal)).ErrorKey);
        Assert.Equal(
            "files.not_found",
            sonuc.Items.Single(item => item.SourcePath.EndsWith("yok.txt", StringComparison.Ordinal)).ErrorKey);

        Assert.True(File.Exists(Path.Combine(siteAKoku, "hedef", "bir.txt")));
        Assert.Equal("hedefte-var", File.ReadAllText(Path.Combine(siteAKoku, "hedef", "iki.txt")));
    }

    [Fact]
    public async Task Hedef_klasor_YOKSA_hicbir_sey_tasinmaz()
    {
        DosyaYaz(Path.Combine(siteAKoku, "rapor.txt"), "veri");

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.MoveAsync(new MoveFilesRequest
        {
            Paths = ["sites/a.test/rapor.txt"],
            DestinationPath = "sites/a.test/olmayan-klasor"
        }));

        // Hedefi kendiliğinden yaratmak, yazım hatasıyla açılan bir klasöre sekiz dosya taşıyıp
        // onları "kaybolmuş" hâle getirirdi.
        Assert.Equal("files.move_destination_missing", hata.MessageKey);
        Assert.True(File.Exists(Path.Combine(siteAKoku, "rapor.txt")));
    }

    // ------------------------------------------------------------------
    // ASIL İŞ
    // ------------------------------------------------------------------

    [Fact]
    public async Task Dosya_ve_KLASOR_baska_klasore_tasinir()
    {
        Directory.CreateDirectory(Path.Combine(siteAKoku, "kaynak", "klasor"));
        Directory.CreateDirectory(Path.Combine(siteAKoku, "hedef"));
        DosyaYaz(Path.Combine(siteAKoku, "kaynak", "rapor.txt"), "veri");
        DosyaYaz(Path.Combine(siteAKoku, "kaynak", "klasor", "ic.txt"), "ic veri");

        var sonuc = await servis.MoveAsync(new MoveFilesRequest
        {
            Paths = ["sites/a.test/kaynak/rapor.txt", "sites/a.test/kaynak/klasor"],
            DestinationPath = "sites/a.test/hedef"
        });

        Assert.Equal(2, sonuc.MovedCount);
        Assert.Equal(0, sonuc.FailedCount);
        Assert.Equal("sites/a.test/hedef/rapor.txt", sonuc.Items[0].TargetPath);

        Assert.True(File.Exists(Path.Combine(siteAKoku, "hedef", "rapor.txt")));
        Assert.Equal("ic veri", File.ReadAllText(Path.Combine(siteAKoku, "hedef", "klasor", "ic.txt")));
        Assert.False(File.Exists(Path.Combine(siteAKoku, "kaynak", "rapor.txt")));
        Assert.False(Directory.Exists(Path.Combine(siteAKoku, "kaynak", "klasor")));
    }

    [Fact]
    public async Task Yeniden_adlandirma_ayni_klasorde_calisir()
    {
        DosyaYaz(Path.Combine(siteAKoku, "eski-ad.txt"), "veri");

        var sonuc = await servis.MoveAsync(new MoveFilesRequest
        {
            Paths = ["sites/a.test/eski-ad.txt"],
            DestinationPath = "sites/a.test",
            NewName = "yeni-ad.txt"
        });

        // Yeniden adlandırma ayrı bir uç DEĞİL: hedefi kaynağın kendi klasörü olan bir taşımadır.
        Assert.Equal(1, sonuc.MovedCount);
        Assert.Equal("sites/a.test/yeni-ad.txt", Assert.Single(sonuc.Items).TargetPath);
        Assert.True(File.Exists(Path.Combine(siteAKoku, "yeni-ad.txt")));
        Assert.False(File.Exists(Path.Combine(siteAKoku, "eski-ad.txt")));
    }

    [Fact]
    public async Task COKLU_ogede_yeniden_adlandirma_reddedilir()
    {
        DosyaYaz(Path.Combine(siteAKoku, "bir.txt"), "1");
        DosyaYaz(Path.Combine(siteAKoku, "iki.txt"), "2");
        Directory.CreateDirectory(Path.Combine(siteAKoku, "hedef"));

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.MoveAsync(new MoveFilesRequest
        {
            Paths = ["sites/a.test/bir.txt", "sites/a.test/iki.txt"],
            DestinationPath = "sites/a.test/hedef",
            NewName = "tek-ad.txt"
        }));

        // İki dosyaya aynı adı vermek, birinin sessizce yok olması demektir.
        Assert.Equal("files.move_rename_single_only", hata.MessageKey);
        Assert.True(File.Exists(Path.Combine(siteAKoku, "bir.txt")));
        Assert.True(File.Exists(Path.Combine(siteAKoku, "iki.txt")));
    }

    [Fact]
    public async Task Baska_bir_SITEYE_tasima_calisir()
    {
        DosyaYaz(Path.Combine(siteAKoku, "rapor.txt"), "veri");
        Directory.CreateDirectory(Path.Combine(siteBKoku, "gelen"));

        var sonuc = await servis.MoveAsync(new MoveFilesRequest
        {
            Paths = ["sites/a.test/rapor.txt"],
            DestinationPath = "sites/b.test/gelen"
        });

        // Sanal alan SİTE BAŞINA tanımlıdır; iki uç da kendi sitesinin içindeyse taşıma meşrudur.
        // (Çağıranın iki siteye de yetkisi olduğu ayrıca denetleyicide sorulur.)
        Assert.Equal(1, sonuc.MovedCount);
        Assert.True(File.Exists(Path.Combine(siteBKoku, "gelen", "rapor.txt")));
    }

    // --- düzenek ---

    private static void DosyaYaz(string yol, string icerik)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(yol)!);
        File.WriteAllText(yol, icerik);
    }

    private static void JunctionOlustur(string link, string hedef)
    {
        var betik = $"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{hedef.Replace("'", "''")}' | Out-Null";
        var yol = Path.Combine(Path.GetTempPath(), $"ada-move-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(yol, "$ErrorActionPreference = 'Stop'\n" + betik);
        try
        {
            using var surec = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{yol}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            surec.WaitForExit(60_000);
        }
        finally
        {
            File.Delete(yol);
        }
    }

    /// <summary>Site adını fiziksel köke çeviren sahte; başka hiçbir IIS işlemi kullanılmaz.</summary>
    private sealed class SahteSiteServisi(IReadOnlyDictionary<string, string> kokler) : IIisSiteService
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
