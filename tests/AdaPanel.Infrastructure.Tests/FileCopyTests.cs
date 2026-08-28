using System.Diagnostics;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Files;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Dosya KOPYALAMA ucunun davranışsal korumaları.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı bir test dosyası:</b> kopyalama "kaynağı silmeyen taşıma" değildir. Klasör
/// kopyalamak ÖZYİNELEMELİ bir yürüyüştür ve o yürüyüşün HER adımında bağlantı (junction/symlink)
/// kararı yeniden verilir — taşımada tek bir <c>Directory.Move</c> vardır, hiç yürüyüş yoktur.
/// Bağlantıyı takip eden bir kopyalama, sanal alanın DIŞINDAKİ veriyi (panel veri klasörü, başka
/// kiracının sitesi) kiracının okuyabildiği bir klasöre çekerdi; yol çözümlemesindeki koruma
/// özyinelemenin içinden delinmiş olurdu. Bu yüzden kurallar burada gerçek dosya sistemi üzerinde
/// ölçülür — kaynak taramasıyla değil.
/// </para>
/// <para>
/// Kopyalamanın taşımadan EN GÖRÜNÜR farkı da burada kanıtlanır: kaynak YERİNDE KALIR.
/// </para>
/// </remarks>
public sealed class FileCopyTests : IDisposable
{
    private readonly string kok;
    private readonly string siteAKoku;
    private readonly string siteBKoku;
    private readonly string disariKok;
    private readonly FileService servis;

    public FileCopyTests()
    {
        kok = Path.Combine(Path.GetTempPath(), "adapanel-copy-" + Guid.NewGuid().ToString("N"));
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
    // ASIL İŞ — TAŞIMADAN FARKI
    // ------------------------------------------------------------------

    [Fact]
    public async Task Dosya_kopyalanir_ve_KAYNAK_YERINDE_kalir()
    {
        Directory.CreateDirectory(Path.Combine(siteAKoku, "kaynak"));
        Directory.CreateDirectory(Path.Combine(siteAKoku, "hedef"));
        DosyaYaz(Path.Combine(siteAKoku, "kaynak", "rapor.txt"), "veri");

        var sonuc = await servis.CopyAsync(new CopyFilesRequest
        {
            Paths = ["sites/a.test/kaynak/rapor.txt"],
            DestinationPath = "sites/a.test/hedef"
        });

        Assert.Equal(1, sonuc.CopiedCount);
        Assert.Equal(0, sonuc.FailedCount);
        Assert.Equal("sites/a.test/hedef/rapor.txt", Assert.Single(sonuc.Items).TargetPath);

        // KOPYALAMANIN TAŞIMADAN FARKI TAM OLARAK BUDUR. Kaynağı silen bir "kopyalama", kullanıcının
        // yedek almak için başvurduğu işlemin ta kendisini veri kaybına çevirirdi.
        Assert.Equal("veri", File.ReadAllText(Path.Combine(siteAKoku, "kaynak", "rapor.txt")));
        Assert.Equal("veri", File.ReadAllText(Path.Combine(siteAKoku, "hedef", "rapor.txt")));
    }

    [Fact]
    public async Task Klasor_OZYINELEMELI_kopyalanir()
    {
        Directory.CreateDirectory(Path.Combine(siteAKoku, "hedef"));
        DosyaYaz(Path.Combine(siteAKoku, "veri", "ust.txt"), "ust");
        DosyaYaz(Path.Combine(siteAKoku, "veri", "alt", "ic.txt"), "ic veri");
        DosyaYaz(Path.Combine(siteAKoku, "veri", "alt", "daha-alt", "en-ic.txt"), "en ic");

        var sonuc = await servis.CopyAsync(new CopyFilesRequest
        {
            Paths = ["sites/a.test/veri"],
            DestinationPath = "sites/a.test/hedef"
        });

        // Yalnız üst klasörü yaratıp içini boş bırakan bir "kopyalama", kullanıcıya tam kopyası
        // olduğunu söyleyip aslında hiçbir şey vermemek olurdu.
        Assert.Equal(1, sonuc.CopiedCount);
        Assert.Equal(string.Empty, Assert.Single(sonuc.Items).ErrorKey);
        Assert.Equal("ust", File.ReadAllText(Path.Combine(siteAKoku, "hedef", "veri", "ust.txt")));
        Assert.Equal("ic veri", File.ReadAllText(Path.Combine(siteAKoku, "hedef", "veri", "alt", "ic.txt")));
        Assert.Equal("en ic", File.ReadAllText(Path.Combine(siteAKoku, "hedef", "veri", "alt", "daha-alt", "en-ic.txt")));

        // Kaynak ağacı olduğu gibi durur.
        Assert.True(File.Exists(Path.Combine(siteAKoku, "veri", "alt", "daha-alt", "en-ic.txt")));
    }

    [Fact]
    public async Task Ayni_klasore_YENI_ADLA_cogaltilabilir()
    {
        DosyaYaz(Path.Combine(siteAKoku, "web.config"), "ayar");

        var sonuc = await servis.CopyAsync(new CopyFilesRequest
        {
            Paths = ["sites/a.test/web.config"],
            DestinationPath = "sites/a.test",
            NewName = "web.config.yedek"
        });

        // "Değiştirmeden önce yedeğini al" — kopyalamanın en sık kullanıldığı yer budur.
        Assert.Equal(1, sonuc.CopiedCount);
        Assert.Equal("ayar", File.ReadAllText(Path.Combine(siteAKoku, "web.config.yedek")));
        Assert.Equal("ayar", File.ReadAllText(Path.Combine(siteAKoku, "web.config")));
    }

    [Fact]
    public async Task Ogeyi_KENDI_uzerine_kopyalamak_reddedilir()
    {
        DosyaYaz(Path.Combine(siteAKoku, "rapor.txt"), "veri");

        var sonuc = await servis.CopyAsync(new CopyFilesRequest
        {
            Paths = ["sites/a.test/rapor.txt"],
            DestinationPath = "sites/a.test"
        });

        // Kaynağın kendi üstüne kopyalanması dosyayı BOŞALTIR; sessizce "tamam" demek veri kaybıdır.
        Assert.Equal("files.copy_same_path", Assert.Single(sonuc.Items).ErrorKey);
        Assert.Equal("veri", File.ReadAllText(Path.Combine(siteAKoku, "rapor.txt")));
    }

    // ------------------------------------------------------------------
    // SANAL ALAN
    // ------------------------------------------------------------------

    [Fact]
    public async Task Sanal_alan_DISINA_kopyalama_REDDEDILIR()
    {
        DosyaYaz(Path.Combine(siteAKoku, "rapor.txt"), "gizli");

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.CopyAsync(new CopyFilesRequest
        {
            Paths = ["sites/a.test/rapor.txt"],
            DestinationPath = "sites/a.test/../disari"
        }));

        Assert.Equal("files.path_outside_root", hata.MessageKey);
        Assert.True(File.Exists(Path.Combine(siteAKoku, "rapor.txt")));
        Assert.False(File.Exists(Path.Combine(disariKok, "rapor.txt")));
    }

    [Fact]
    public async Task Kaynak_sanal_alan_DISINDAYSA_istek_REDDEDILIR()
    {
        DosyaYaz(Path.Combine(disariKok, "panel-sirri.txt"), "panel dosyasi");
        Directory.CreateDirectory(Path.Combine(siteAKoku, "gelen"));

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.CopyAsync(new CopyFilesRequest
        {
            Paths = ["sites/a.test/../disari/panel-sirri.txt"],
            DestinationPath = "sites/a.test/gelen"
        }));

        // Kopyalama silmediği için "zararsız" görünür, ama izolasyon açısından TAM TERSİ: sanal alan
        // dışından kopyalamak, kiracının göremeyeceği bir dosyanın İÇERİĞİNİ okuyabildiği klasöre
        // çekmektir — doğrudan veri sızması.
        Assert.Equal("files.path_outside_root", hata.MessageKey);
        Assert.False(File.Exists(Path.Combine(siteAKoku, "gelen", "panel-sirri.txt")));
    }

    [Fact]
    public async Task Yeni_ADIN_icine_YOL_konarak_sanal_alandan_CIKILAMAZ()
    {
        DosyaYaz(Path.Combine(siteAKoku, "rapor.txt"), "gizli");

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.CopyAsync(new CopyFilesRequest
        {
            Paths = ["sites/a.test/rapor.txt"],
            DestinationPath = "sites/a.test",
            NewName = @"..\..\disari\kacak.txt"
        }));

        Assert.Equal("files.invalid_name", hata.MessageKey);
        Assert.False(File.Exists(Path.Combine(disariKok, "kacak.txt")));
    }

    [Fact]
    public async Task Site_KOKU_kopyalanamaz()
    {
        Directory.CreateDirectory(Path.Combine(siteBKoku, "gelen"));

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.CopyAsync(new CopyFilesRequest
        {
            Paths = ["sites/a.test"],
            DestinationPath = "sites/b.test/gelen"
        }));

        Assert.Equal("files.site_root_protected", hata.MessageKey);
        Assert.False(Directory.Exists(Path.Combine(siteBKoku, "gelen", "a.test")));
    }

    // ------------------------------------------------------------------
    // KLASÖR BAĞLANTISI (junction)
    // ------------------------------------------------------------------

    [Fact]
    public void Kaynagin_KENDISI_baglantiysa_istek_REDDEDILIR()
    {
        var gercekSite = Path.Combine(kok, "gercek-site");
        Directory.CreateDirectory(gercekSite);
        DosyaYaz(Path.Combine(gercekSite, "onemli.txt"), "musteri sitesi");

        var baglanti = Path.Combine(siteAKoku, "baglanti");
        JunctionOlustur(baglanti, gercekSite);
        Assert.True(Directory.Exists(baglanti), "Junction kurulamadi; test onkosulu saglanmadi.");
        Directory.CreateDirectory(Path.Combine(siteAKoku, "gelen"));

        var hata = Assert.ThrowsAsync<AppException>(() => servis.CopyAsync(new CopyFilesRequest
        {
            Paths = ["sites/a.test/baglanti"],
            DestinationPath = "sites/a.test/gelen"
        })).GetAwaiter().GetResult();

        Assert.Equal("files.path_outside_root", hata.MessageKey);
        Assert.False(Directory.Exists(Path.Combine(siteAKoku, "gelen", "baglanti")));
    }

    [Fact]
    public void Klasorun_ICINDEKI_baglanti_TAKIP_EDILMEZ_ve_RAPORLANIR()
    {
        var gercekSite = Path.Combine(kok, "gercek-site");
        Directory.CreateDirectory(gercekSite);
        DosyaYaz(Path.Combine(gercekSite, "sizan.txt"), "sanal alan disi veri");

        DosyaYaz(Path.Combine(siteAKoku, "veri", "normal.txt"), "normal");
        var baglanti = Path.Combine(siteAKoku, "veri", "baglanti");
        JunctionOlustur(baglanti, gercekSite);
        Assert.True(Directory.Exists(baglanti), "Junction kurulamadi; test onkosulu saglanmadi.");
        Directory.CreateDirectory(Path.Combine(siteAKoku, "hedef"));

        var sonuc = servis.CopyAsync(new CopyFilesRequest
        {
            Paths = ["sites/a.test/veri"],
            DestinationPath = "sites/a.test/hedef"
        }).GetAwaiter().GetResult();

        var satir = Assert.Single(sonuc.Items);

        // Kopya OLUŞTU (normal.txt geldi) ama EKSİK: bağlantı atlandı ve bu SÖYLENİR.
        Assert.True(satir.Copied);
        Assert.Equal("files.copy_link_skipped", satir.ErrorKey);
        Assert.Equal("normal", File.ReadAllText(Path.Combine(siteAKoku, "hedef", "veri", "normal.txt")));

        // ASIL KORUMA: bağlantının işaret ettiği sanal alan DIŞI içerik kiracının klasörüne
        // KOPYALANMADI. Takip edilseydi bu, dosya yöneticisi üzerinden çalışan bir veri sızmasıydı.
        Assert.False(Directory.Exists(Path.Combine(siteAKoku, "hedef", "veri", "baglanti")));
        Assert.False(File.Exists(Path.Combine(siteAKoku, "hedef", "veri", "baglanti", "sizan.txt")));
        Assert.True(File.Exists(Path.Combine(gercekSite, "sizan.txt")));
    }

    // ------------------------------------------------------------------
    // ÜZERİNE YAZMA
    // ------------------------------------------------------------------

    [Fact]
    public async Task Hedefte_AYNI_AD_varsa_sessizce_uzerine_YAZILMAZ()
    {
        DosyaYaz(Path.Combine(siteAKoku, "kaynak", "rapor.txt"), "YENI");
        DosyaYaz(Path.Combine(siteAKoku, "hedef", "rapor.txt"), "ESKI");

        var sonuc = await servis.CopyAsync(new CopyFilesRequest
        {
            Paths = ["sites/a.test/kaynak/rapor.txt"],
            DestinationPath = "sites/a.test/hedef"
        });

        Assert.Equal(0, sonuc.CopiedCount);
        Assert.Equal("files.copy_target_exists", Assert.Single(sonuc.Items).ErrorKey);
        Assert.Equal("ESKI", File.ReadAllText(Path.Combine(siteAKoku, "hedef", "rapor.txt")));
    }

    [Fact]
    public async Task Uzerine_yazma_ACIKCA_istendiginde_yapilir()
    {
        DosyaYaz(Path.Combine(siteAKoku, "kaynak", "rapor.txt"), "YENI");
        DosyaYaz(Path.Combine(siteAKoku, "hedef", "rapor.txt"), "ESKI");

        var sonuc = await servis.CopyAsync(new CopyFilesRequest
        {
            Paths = ["sites/a.test/kaynak/rapor.txt"],
            DestinationPath = "sites/a.test/hedef",
            Overwrite = true
        });

        // Reddetmek VARSAYILAN; yasak değil.
        Assert.Equal(1, sonuc.CopiedCount);
        Assert.Equal("YENI", File.ReadAllText(Path.Combine(siteAKoku, "hedef", "rapor.txt")));
        Assert.Equal("YENI", File.ReadAllText(Path.Combine(siteAKoku, "kaynak", "rapor.txt")));
    }

    [Fact]
    public async Task Var_olan_KLASOR_uzerine_Overwrite_ile_bile_yazilmaz()
    {
        DosyaYaz(Path.Combine(siteAKoku, "kaynak", "veri", "yeni.txt"), "yeni");
        DosyaYaz(Path.Combine(siteAKoku, "hedef", "veri", "eski.txt"), "eski");

        var sonuc = await servis.CopyAsync(new CopyFilesRequest
        {
            Paths = ["sites/a.test/kaynak/veri"],
            DestinationPath = "sites/a.test/hedef",
            Overwrite = true
        });

        // Klasörü ezmek, hedefin ÖZYİNELEMELİ SİLİNMESİ demektir; tek bir bayrağın arkasına konmaz.
        Assert.Equal(1, sonuc.FailedCount);
        Assert.Equal("files.copy_target_exists", Assert.Single(sonuc.Items).ErrorKey);
        Assert.True(File.Exists(Path.Combine(siteAKoku, "hedef", "veri", "eski.txt")));
        Assert.False(File.Exists(Path.Combine(siteAKoku, "hedef", "veri", "yeni.txt")));
    }

    // ------------------------------------------------------------------
    // KENDİ ALT AĞACINA KOPYALAMA
    // ------------------------------------------------------------------

    [Fact]
    public async Task Klasor_KENDI_ALT_AGACINA_kopyalanamaz()
    {
        Directory.CreateDirectory(Path.Combine(siteAKoku, "ust", "alt"));
        DosyaYaz(Path.Combine(siteAKoku, "ust", "icerik.txt"), "veri");

        var sonuc = await servis.CopyAsync(new CopyFilesRequest
        {
            Paths = ["sites/a.test/ust"],
            DestinationPath = "sites/a.test/ust/alt"
        });

        // Özyineleme, kopyalarken BÜYÜMEYE devam eden bir ağacı okur: disk dolana kadar süren
        // bir kopyalama. Taşımadaki "erişilemez hâle gelir"den daha yıkıcıdır.
        Assert.Equal("files.copy_into_itself", Assert.Single(sonuc.Items).ErrorKey);
        Assert.False(Directory.Exists(Path.Combine(siteAKoku, "ust", "alt", "ust")));
    }

    // ------------------------------------------------------------------
    // KISMİ BAŞARISIZLIK
    // ------------------------------------------------------------------

    [Fact]
    public async Task Kismi_basarisizlik_ISTISNA_atmaz_DURUSTCE_raporlanir()
    {
        DosyaYaz(Path.Combine(siteAKoku, "kaynak", "bir.txt"), "1");
        DosyaYaz(Path.Combine(siteAKoku, "kaynak", "iki.txt"), "2");
        DosyaYaz(Path.Combine(siteAKoku, "hedef", "iki.txt"), "hedefte-var");

        var sonuc = await servis.CopyAsync(new CopyFilesRequest
        {
            Paths = ["sites/a.test/kaynak/bir.txt", "sites/a.test/kaynak/iki.txt", "sites/a.test/kaynak/yok.txt"],
            DestinationPath = "sites/a.test/hedef"
        });

        Assert.Equal(1, sonuc.CopiedCount);
        Assert.Equal(2, sonuc.FailedCount);
        Assert.Equal(3, sonuc.Items.Length);

        Assert.True(sonuc.Items.Single(oge => oge.SourcePath.EndsWith("bir.txt", StringComparison.Ordinal)).Copied);
        Assert.Equal(
            "files.copy_target_exists",
            sonuc.Items.Single(oge => oge.SourcePath.EndsWith("iki.txt", StringComparison.Ordinal)).ErrorKey);
        Assert.Equal(
            "files.not_found",
            sonuc.Items.Single(oge => oge.SourcePath.EndsWith("yok.txt", StringComparison.Ordinal)).ErrorKey);

        Assert.True(File.Exists(Path.Combine(siteAKoku, "hedef", "bir.txt")));
        Assert.Equal("hedefte-var", File.ReadAllText(Path.Combine(siteAKoku, "hedef", "iki.txt")));
    }

    [Fact]
    public async Task Hedef_klasor_YOKSA_hicbir_sey_kopyalanmaz()
    {
        DosyaYaz(Path.Combine(siteAKoku, "rapor.txt"), "veri");

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.CopyAsync(new CopyFilesRequest
        {
            Paths = ["sites/a.test/rapor.txt"],
            DestinationPath = "sites/a.test/olmayan-klasor"
        }));

        Assert.Equal("files.copy_destination_missing", hata.MessageKey);
        Assert.False(Directory.Exists(Path.Combine(siteAKoku, "olmayan-klasor")));
    }

    [Fact]
    public async Task COKLU_ogede_yeniden_adlandirma_reddedilir()
    {
        DosyaYaz(Path.Combine(siteAKoku, "bir.txt"), "1");
        DosyaYaz(Path.Combine(siteAKoku, "iki.txt"), "2");
        Directory.CreateDirectory(Path.Combine(siteAKoku, "hedef"));

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.CopyAsync(new CopyFilesRequest
        {
            Paths = ["sites/a.test/bir.txt", "sites/a.test/iki.txt"],
            DestinationPath = "sites/a.test/hedef",
            NewName = "tek-ad.txt"
        }));

        // İki dosyaya aynı adı vermek, birinin diğerinin üstüne yazması demektir.
        Assert.Equal("files.copy_rename_single_only", hata.MessageKey);
        Assert.False(File.Exists(Path.Combine(siteAKoku, "hedef", "tek-ad.txt")));
    }

    [Fact]
    public async Task Kaynak_verilmeden_kopyalama_istenemez()
    {
        var hata = await Assert.ThrowsAsync<AppException>(() => servis.CopyAsync(new CopyFilesRequest
        {
            Paths = [],
            DestinationPath = "sites/a.test"
        }));

        Assert.Equal("files.copy_source_required", hata.MessageKey);
    }

    // ------------------------------------------------------------------
    // ORTAK GÜVENLİK ÖN HAZIRLIĞI — NÖBETÇİ
    // ------------------------------------------------------------------

    [Fact]
    public async Task Tasima_ve_kopyalama_AYNI_sanal_alan_kapisindan_gecer()
    {
        DosyaYaz(Path.Combine(siteAKoku, "rapor.txt"), "gizli");

        var tasimaHatasi = await Assert.ThrowsAsync<AppException>(() => servis.MoveAsync(new MoveFilesRequest
        {
            Paths = ["sites/a.test/rapor.txt"],
            DestinationPath = "sites/a.test/../disari"
        }));

        var kopyalamaHatasi = await Assert.ThrowsAsync<AppException>(() => servis.CopyAsync(new CopyFilesRequest
        {
            Paths = ["sites/a.test/rapor.txt"],
            DestinationPath = "sites/a.test/../disari"
        }));

        // Aynı istek, aynı cevap. Ayrışırlarsa iki akıştan biri gevşemiş demektir.
        Assert.Equal(tasimaHatasi.MessageKey, kopyalamaHatasi.MessageKey);
        Assert.Equal("files.path_outside_root", kopyalamaHatasi.MessageKey);
    }

    [Fact]
    public void Guvenlik_on_hazirligi_TEK_KAYNAKTAN_gelir()
    {
        var kaynak = File.ReadAllText(Path.Combine(DepoKoku(), "src", "AdaPanel.Infrastructure", "Files", "FileService.cs"));

        var tasima = GovdeAl(kaynak, "public async Task<MoveFilesResultDto> MoveAsync(");
        var kopyalama = GovdeAl(kaynak, "public async Task<CopyFilesResultDto> CopyAsync(");
        var hazirlik = GovdeAl(kaynak, "private async Task<TransferPlan> BuildTransferPlanAsync(");

        // 1) İKİSİ DE ortak hazırlığı çağırır.
        Assert.Contains("BuildTransferPlanAsync(", tasima, StringComparison.Ordinal);
        Assert.Contains("BuildTransferPlanAsync(", kopyalama, StringComparison.Ordinal);

        // 2) Denetimler ORTAK yerde yaşar. Bu liste, kaynak/hedef çözümlemesinin tamamıdır; biri
        //    buradan düşerse hem taşıma hem kopyalama aynı anda delinir ve test kızarır.
        foreach (var nobetci in new[]
                 {
                     "ResolveTargetAsync(",
                     "EnsureSiteTarget(",
                     "EnsureNotSiteRoot(",
                     "EnsureInsideSandbox(",
                     "EnsureSimpleName(",
                     "Directory.Exists(destination.PhysicalPath)"
                 })
        {
            Assert.Contains(nobetci, hazirlik, StringComparison.Ordinal);
        }

        // 3) İki akış da denetimi KENDİ İÇİNDE TEKRARLAMAZ. Tekrarlanan bir liste, birini
        //    sıkılaştırıp diğerini unutmanın yoludur — bu depoda zaten bir kez öyle oldu.
        Assert.DoesNotContain("EnsureInsideSandbox(", tasima, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureInsideSandbox(", kopyalama, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // ÖLÇÜM: WriteAsync'te EnsureInsideSandbox YOK — delik mi?
    // ------------------------------------------------------------------

    /// <summary>
    /// <b>ÖLÇÜLEN SORU:</b> <c>WriteAsync</c> yalnız <c>EnsureSiteTarget</c> + <c>EnsureNotSiteRoot</c>
    /// çağırır; <c>MoveAsync</c>/<c>CopyAsync</c> ise ayrıca <c>EnsureInsideSandbox</c> çağırır.
    /// Bu bir delik mi?
    /// <para>
    /// <b>CEVAP: hayır — <c>ResolveTargetAsync</c> sanal alan denetimini KENDİSİ yapar</b>
    /// (<c>ResolveSandboxTarget</c>: tam yol sanal alan kökü ya da kökün altında değilse
    /// <c>files.path_outside_root</c>; ayrıca yol üzerindeki bağlantılar reddedilir). Sanal alanı
    /// olan HER hedef bu kapıdan geçer, çünkü kök dışındaki tek dönüş yolu sanal köktür ve onu da
    /// <c>EnsureSiteTarget</c> eler. <c>EnsureInsideSandbox</c> bu yüzden GEREKSİZ TEKRAR, delik değil.
    /// </para>
    /// <para>
    /// Ama "okudum, öyleymiş" bir kanıt değildir: bu test kapıyı DAVRANIŞSAL olarak ölçer. Birileri
    /// <c>ResolveSandboxTarget</c>'taki denetimi kaldırırsa — ki o an <c>WriteAsync</c>, <c>DeleteAsync</c>,
    /// <c>SaveUploadAsync</c> ve <c>CreateDirectoryAsync</c>'in HEPSİ sanal alandan çıkar — burası kızarır.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WriteAsync_EnsureInsideSandbox_cagirmasa_da_sanal_alandan_CIKAMAZ()
    {
        var hata = await Assert.ThrowsAsync<AppException>(() => servis.WriteAsync(new WriteFileRequest
        {
            Path = "sites/a.test/../disari/kacak.txt",
            Content = "kacak"
        }));

        Assert.Equal("files.path_outside_root", hata.MessageKey);
        Assert.False(File.Exists(Path.Combine(disariKok, "kacak.txt")));
    }

    [Fact]
    public async Task Silme_ve_klasor_acma_da_ayni_kapidan_gecer()
    {
        DosyaYaz(Path.Combine(disariKok, "panel-sirri.txt"), "panel dosyasi");

        var silmeHatasi = await Assert.ThrowsAsync<AppException>(() => servis.DeleteAsync(new DeleteFileRequest
        {
            Path = "sites/a.test/../disari/panel-sirri.txt"
        }));

        var klasorHatasi = await Assert.ThrowsAsync<AppException>(() => servis.CreateDirectoryAsync(new CreateDirectoryRequest
        {
            Path = "sites/a.test/../disari/yeni"
        }));

        // Aynı ölçüm, EnsureInsideSandbox çağırmayan diğer uçlar için: koruma tek yerde (yol
        // çözümlemesinde) ve hepsi için geçerli.
        Assert.Equal("files.path_outside_root", silmeHatasi.MessageKey);
        Assert.Equal("files.path_outside_root", klasorHatasi.MessageKey);
        Assert.True(File.Exists(Path.Combine(disariKok, "panel-sirri.txt")));
        Assert.False(Directory.Exists(Path.Combine(disariKok, "yeni")));
    }

    // --- düzenek ---

    private static void DosyaYaz(string yol, string icerik)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(yol)!);
        File.WriteAllText(yol, icerik);
    }

    /// <summary>Bir metodun gövdesini imzasından başlayıp 4 boşluk girintili kapanış süslüsüne kadar alır.</summary>
    private static string GovdeAl(string kaynak, string imza)
    {
        var baslangic = kaynak.IndexOf(imza, StringComparison.Ordinal);
        Assert.True(baslangic >= 0, $"FileService.cs icinde bulunamadi: {imza}");

        var bitis = kaynak.IndexOf("\n    }", baslangic, StringComparison.Ordinal);
        Assert.True(bitis > baslangic, $"Metot govdesinin sonu bulunamadi: {imza}");

        return kaynak[baslangic..bitis];
    }

    private static string DepoKoku()
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            if (File.Exists(Path.Combine(klasor, "AdaPanel.sln")))
            {
                return Path.GetFullPath(klasor);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new DirectoryNotFoundException("Depo koku bulunamadi.");
    }

    private static void JunctionOlustur(string link, string hedef)
    {
        var betik = $"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{hedef.Replace("'", "''")}' | Out-Null";
        var yol = Path.Combine(Path.GetTempPath(), $"ada-copy-{Guid.NewGuid():N}.ps1");
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
