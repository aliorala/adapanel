using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Asp;
using AdaPanel.Core.Dtos.Htaccess;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Htaccess;
using AdaPanel.Infrastructure.Htaccess.Hosting;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// PANEL YÜZÜ testleri.
/// </summary>
/// <remarks>
/// <para>
/// Bu testlerin ortak konusu tek bir cümle: <b>panel YALAN SÖYLEMEZ.</b> Bugünkü şikâyet
/// ".htaccess duruyor ama hiçbir şey olmuyor ve kimse sebebini söylemiyor" idi; buradaki her
/// test o cümlenin bir parçasının ölçülmesidir — dosya var mı, kip ne, destek var mı, sınır ne.
/// </para>
/// <para>
/// <b>Gerçek disk kullanılıyor</b> (<c>HtaccessTestSite</c> ile aynı gerekçe): dizin
/// basamaklandırması, kapsama denetimi ve dosya damgaları ancak gerçek yollarla anlamlı biçimde
/// ölçülebilir. Bu depoda "sahte dünyada yeşil, gerçek dünyada ölü" kodun bedeli defalarca ödendi.
/// </para>
/// </remarks>
public sealed class HtaccessPanelServiceTests : IDisposable
{
    private readonly string _siteKoku;
    private readonly string _veriKoku;

    public HtaccessPanelServiceTests()
    {
        var taban = Path.Combine(Path.GetTempPath(), $"adapanel-htaccess-panel-{Guid.NewGuid():N}");
        _siteKoku = Path.Combine(taban, "site");
        _veriKoku = Path.Combine(taban, "data");
        Directory.CreateDirectory(_siteKoku);
        Directory.CreateDirectory(_veriKoku);
    }

    // ---------------------------------------------------------------- sahteler

    private sealed class SahteSiteServisi(SiteDto site) : IIisSiteService
    {
        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<SiteDto>>([site]);

        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default)
            => Task.FromResult(site);

        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class SahteAspServisi(string? managedRuntimeVersion, Exception? hata = null) : IAspNetService
    {
        public Task<AspSiteConfigurationDto> GetSiteConfigurationAsync(string siteName, CancellationToken cancellationToken = default)
            => hata is not null
                ? Task.FromException<AspSiteConfigurationDto>(hata)
                : Task.FromResult(new AspSiteConfigurationDto
                {
                    SiteName = siteName,
                    ApplicationPoolName = siteName,
                    ManagedRuntimeVersion = managedRuntimeVersion ?? string.Empty
                });

        public Task<AspSiteConfigurationDto> SetClassicAspAsync(string siteName, UpdateClassicAspRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AspSiteConfigurationDto> SetDotNetVersionAsync(string siteName, UpdateDotNetRuntimeRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AspSiteConfigurationDto> EnableMixedHandlersAsync(string siteName, EnableMixedHandlersRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private HtaccessPanelService Servis(string? havuzSurumu = "v4.0", Exception? havuzHatasi = null)
    {
        var site = new SiteDto
        {
            Id = 1,
            Name = "ornek.com",
            State = "Started",
            PhysicalPath = _siteKoku,
            ApplicationPoolName = "ornek.com",
            Bindings = [new SiteBindingDto { Protocol = "http", HostName = "ornek.com", Port = 80, IpAddress = "*" }]
        };

        return new HtaccessPanelService(
            new HtaccessEngine(),
            new SahteSiteServisi(site),
            new SahteAspServisi(havuzSurumu, havuzHatasi),
            // TAM NITELIKLI: "Options" adi AdaPanel.Infrastructure.Options AD ALANINA cozuluyor,
            // Microsoft.Extensions.Options.Options SINIFINA degil.
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = _veriKoku }),
            NullLogger<HtaccessPanelService>.Instance);
    }

    private string Yaz(string goreliYol, string icerik)
    {
        var tam = Path.Combine(_siteKoku, goreliYol.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(tam)!);
        File.WriteAllText(tam, icerik);
        return tam;
    }

    // ---------------------------------------------------------------- ASIL ŞİKÂYET

    /// <summary>
    /// Bugünkü şikâyetin TAM KARŞILIĞI: dosya var, destek kapalı. Panel ikisini de söylemeli.
    /// </summary>
    [Fact]
    public async Task DOSYA_VAR_ama_DESTEK_KAPALI_durumu_RAPORDA_GORUNUR()
    {
        Yaz(".htaccess", "RewriteEngine On\nRewriteRule ^eski$ /yeni [R=301,L]\n");

        var rapor = await Servis().GetReportAsync("ornek.com", CancellationToken.None);

        // Bu ucu birlesim panelin tek isi: "dosya duruyor" ve "kapali" ayni ekranda gorulmeli.
        Assert.True(rapor.HasFiles);
        Assert.True(rapor.HasRootFile);
        Assert.Equal("off", rapor.Mode);
        Assert.Equal("off", rapor.ServerMode);
    }

    /// <summary>
    /// KÖKTE dosya YOKKEN alt dizindeki <c>.htaccess</c> canlıda UYGULANMAZ.
    /// </summary>
    /// <remarks>
    /// Barındırma katmanı yalnız belge kökündeki dosyaya bakıyor (<c>HtaccessHttpModule.Apply</c>
    /// + <c>HtaccessFileCache</c> yokluk önbelleği). Panel bu iki durumu AYIRMAZSA kullanıcı
    /// <c>/blog/.htaccess</c>'ini yazar, panel "dosya var" der ve hiçbir şey olmaz — düzeltmeye
    /// çalıştığımız şikâyetin ikinci hâli.
    /// </remarks>
    [Fact]
    public async Task KOKTE_DOSYA_YOKSA_rapor_bunu_AYRICA_soyler()
    {
        Yaz("blog/.htaccess", "RewriteEngine On\n");

        var rapor = await Servis().GetReportAsync("ornek.com", CancellationToken.None);

        Assert.True(rapor.HasFiles);
        Assert.False(rapor.HasRootFile);
        Assert.Single(rapor.Files);
        Assert.Equal("blog", rapor.Files[0].RelativeDirectory);
        Assert.Equal("/blog/", rapor.Files[0].VirtualPath);
    }

    // ---------------------------------------------------------------- DÜRÜST SINIRLAR

    /// <summary>
    /// Node/Python sitelerinde havuz "yönetilen kod yok"a çevriliyor ve modül HİÇ YÜKLENMEZ.
    /// </summary>
    /// <remarks>
    /// <c>AppRuntimeService.EnsureApplicationPoolUsesNoManagedCode</c> havuzun
    /// <c>ManagedRuntimeVersion</c>'ını boşaltıyor. Modülün kayıt ön koşulu
    /// <c>runtimeVersionv4.0</c> olduğu için IIS girdiyi ATLAR. Panel bu siteleri "açık"
    /// gösterirse kullanıcı düğmeye basar ve hiçbir şey olmaz.
    /// </remarks>
    [Fact]
    public async Task YONETILEN_KOD_YOK_havuzunda_destek_NO_MANAGED_CODE_raporlanir()
    {
        var rapor = await Servis(havuzSurumu: string.Empty).GetReportAsync("ornek.com", CancellationToken.None);

        Assert.Equal("no_managed_code", rapor.Support);
        Assert.Equal(string.Empty, rapor.ManagedRuntimeVersion);
    }

    [Fact]
    public async Task V2_havuzunda_destek_RUNTIME_VERSION_raporlanir()
    {
        var rapor = await Servis(havuzSurumu: "v2.0").GetReportAsync("ornek.com", CancellationToken.None);

        Assert.Equal("runtime_version", rapor.Support);
    }

    [Fact]
    public async Task V4_havuzunda_destek_SUPPORTED_raporlanir()
    {
        var rapor = await Servis(havuzSurumu: "v4.0").GetReportAsync("ornek.com", CancellationToken.None);

        Assert.Equal("supported", rapor.Support);
    }

    /// <summary>
    /// Havuz OKUNAMAZSA "destekleniyor" DEĞİL, "bilinmiyor" denir.
    /// </summary>
    /// <remarks>
    /// Ölçemediğimizde iyimser davranmak, tam da bu iş kolunun kapatmaya çalıştığı sessiz
    /// arızayı üretirdi: panel yeşil, site ölü.
    /// </remarks>
    [Fact]
    public async Task HAVUZ_OKUNAMAZSA_destek_BILINMIYOR_olur()
    {
        var rapor = await Servis(havuzHatasi: new InvalidOperationException("IIS yok"))
            .GetReportAsync("ornek.com", CancellationToken.None);

        Assert.Equal("unknown", rapor.Support);
    }

    /// <summary>
    /// Motorun IIS içinde çalışmasını engelleyen bir neden KALMADI — ve panel bunu ölçüme
    /// dayandırarak söylüyor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bu test eskiden tersini ölçüyordu ("motor yüklü DEĞİL, gerekçesi şu"). Gerekçe gerçekti:
    /// motor <c>net8.0-windows</c>, IIS modülü <c>net48</c> olmak zorundaydı ve bağlantı fiziksel
    /// olarak yapılamıyordu. Engel kalktı (motorun kaynağı artık modül projesinde de derleniyor)
    /// ve panelin "hiçbir şey olmayacak" şeridi bunu göstermeyi BIRAKMALI — yoksa çalışan bir
    /// özelliği bozuk gösterirdi.
    /// </para>
    /// <para>
    /// <b>Bu test motorun net48'de yüklendiğini KANITLAMAZ</b>, panelin ne rapor ettiğini ölçer.
    /// Yükleme kanıtı ayrı ve gerçek bir CLR 4 sürecinde: <c>HtaccessNet48EngineLoadTests</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task MOTOR_ENGELI_KALMADI_ve_panel_bunu_ENGEL_OLARAK_GOSTERMIYOR()
    {
        var rapor = await Servis().GetReportAsync("ornek.com", CancellationToken.None);

        Assert.Empty(rapor.EngineBlockers);

        // Havuz "v4.0" -> destekleniyor -> panel motorun yuklenebilecegini soyluyor.
        Assert.True(rapor.EngineLoadedInIis);
    }

    /// <summary>
    /// Havuz yönetilen kod çalıştırmıyorsa motor IIS'te YÜKLENEMEZ ve panel bunu <c>true</c>
    /// göstermez.
    /// </summary>
    /// <remarks>
    /// Ters yön ölçülmezse <c>EngineLoadedInIis</c> sabit <c>true</c> yazılmış olabilirdi — yani
    /// eski yalanın (sabit <c>false</c>) simetriği. Yönetilen kod olmayan bir havuzda modülün
    /// yüklenmesi mümkün değildir; bu durumda <c>false</c> TAHMİN değil, kesin bilgidir.
    /// </remarks>
    [Theory]
    [InlineData("")]      // no_managed_code
    [InlineData("v2.0")]  // runtime_version
    public async Task HAVUZ_MODULU_YUKLEYEMEZSE_motor_YUKLU_SAYILMAZ(string havuzSurumu)
    {
        var rapor = await Servis(havuzSurumu: havuzSurumu).GetReportAsync("ornek.com", CancellationToken.None);

        Assert.False(rapor.EngineLoadedInIis);
    }

    /// <summary>
    /// Havuz OKUNAMADIYSA motor "yüklü" sayılmaz.
    /// </summary>
    /// <remarks>
    /// Bilmemek, iyi haber değildir. Okunamayan bir havuzda <c>true</c> demek, panelin en çok
    /// güvenilmesi gereken satırında tahmin yürütmek olurdu.
    /// </remarks>
    [Fact]
    public async Task HAVUZ_OKUNAMAZSA_motor_YUKLU_SAYILMAZ()
    {
        var rapor = await Servis(havuzHatasi: new InvalidOperationException("IIS yok"))
            .GetReportAsync("ornek.com", CancellationToken.None);

        Assert.False(rapor.EngineLoadedInIis);
    }

    /// <summary>
    /// Motorun Apache'den bilinçli sapmaları raporda YER ALIR — özellikle erişim denetimi sırası.
    /// </summary>
    [Fact]
    public async Task BILINCLI_SAPMALAR_raporda_yer_alir()
    {
        var rapor = await Servis().GetReportAsync("ornek.com", CancellationToken.None);

        Assert.Contains("htaccess.deviation.access_before_rewrite", rapor.Deviations);
        Assert.True(rapor.Deviations.Count >= 6, "Sapma listesi kismen kaybolmus olabilir.");
    }

    // ---------------------------------------------------------------- KİP

    [Theory]
    [InlineData("observe")]
    [InlineData("enforce")]
    [InlineData("off")]
    public async Task KIP_YAZILIR_ve_GERI_OKUNUR(string kip)
    {
        var servis = Servis();

        var rapor = await servis.SetModeAsync("ornek.com", kip, "test", CancellationToken.None);
        Assert.Equal(kip, rapor.Mode);

        // Yeni bir servis ornegi ayni cevabi vermeli: durum BELLEKTE degil DISKTE.
        var tekrar = await Servis().GetReportAsync("ornek.com", CancellationToken.None);
        Assert.Equal(kip, tekrar.Mode);
    }

    /// <summary>
    /// TANINMAYAN kip REDDEDİLİR, sessizce "off"a düşmez.
    /// </summary>
    /// <remarks>
    /// "enfroce" yazım hatası sessizce kapatma sayılsaydı kullanıcı uyguladığını sanıp kapalı bir
    /// siteyle kalırdı — ve panel ona "kaydedildi" demiş olurdu.
    /// </remarks>
    [Fact]
    public async Task TANINMAYAN_KIP_REDDEDILIR()
    {
        var hata = await Assert.ThrowsAsync<AppException>(
            () => Servis().SetModeAsync("ornek.com", "enfroce", null, CancellationToken.None));

        Assert.Equal("htaccess.invalid_mode", hata.MessageKey);
    }

    /// <summary>
    /// Site kipini açmak SUNUCU GENELİ anahtara DOKUNMAZ.
    /// </summary>
    /// <remarks>
    /// Dokunsaydı acil durum kapatma düğmesi işlevsiz kalırdı: yönetici sunucuyu kapatır, ertesi
    /// gün bir kiracı kendi sitesini açar ve sunucu geneli koruma sessizce geri gelirdi.
    /// </remarks>
    [Fact]
    public async Task SITE_KIPI_SUNUCU_GENELI_ANAHTARI_ACMAZ()
    {
        var servis = Servis();

        var rapor = await servis.SetModeAsync("ornek.com", "enforce", null, CancellationToken.None);

        Assert.Equal("enforce", rapor.Mode);
        Assert.Equal("off", rapor.ServerMode);
    }

    [Fact]
    public async Task SUNUCU_GENELI_KIP_ayrica_yazilir_ve_RAPORDA_gorunur()
    {
        // Anahtar BILEREK depo uzerinden yaziliyor (panelin SetServerMode ucu degil): olculen sey,
        // SITE raporunun genel anahtari okuyup gosterebildigi. Ayni yol servis ucundan yazildiginda
        // da calisiyor -- o HtaccessServerModeTests'te uctan uca olculuyor.
        new HtaccessSwitchStore(_veriKoku).SetGlobal(HtaccessMode.Observe, "kademeli acilis");

        var rapor = await Servis().GetReportAsync("ornek.com", CancellationToken.None);

        Assert.Equal("observe", rapor.ServerMode);
    }

    // ---------------------------------------------------------------- SUNUCU GENELİ ANAHTAR

    /// <summary>
    /// Sunucu geneli anahtar PANELDEN yazılır ve MODÜLÜN OKUDUĞU biçimde diske düşer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Biçim ELLE İDDİA EDİLMİYOR: yazan taraf panel servisi, okuyan taraf modülün kendi kapısı
    /// (<c>HtaccessRuntimeGate</c>). Biçim ya da yol uyuşmazsa panel "açtım" der, modül hiç görmez —
    /// bu iş kolunun en pahalı sessiz arızası tam olarak budur.
    /// </para>
    /// <para>
    /// Site anahtarı ayrıca açılıyor, çünkü kapı etkin modu iki anahtarın EN DÜŞÜĞÜ olarak
    /// hesaplar; site kapalıyken genel anahtarın etkisi kapıda görünmezdi.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("observe")]
    [InlineData("enforce")]
    public void SUNUCU_GENELI_ANAHTAR_PANELDEN_YAZILIR_ve_MODUL_OKUR(string kip)
    {
        // HtaccessMode INTERNAL: xUnit yalniz public metotlari kesfettigi icin beklenen deger
        // parametre olarak degil burada cozuluyor.
        var beklenen = kip == "observe" ? HtaccessMode.Observe : HtaccessMode.Enforce;
        new HtaccessSwitchStore(_veriKoku).SetSite("ornek.com", HtaccessMode.Enforce, null);

        var durum = Servis().SetServerMode(kip, "yonetici acti");

        Assert.Equal(kip, durum.Mode);
        Assert.True(durum.SwitchFileExists);
        Assert.Equal(HtaccessHostingPaths.GlobalSwitchPath(_veriKoku), durum.SwitchFilePath);

        // MODULUN GERCEK OKUYUCUSU. TTL sifir -> onbellek yok, taze okuma.
        Assert.Equal(beklenen, new HtaccessRuntimeGate(_veriKoku, TimeSpan.Zero).Evaluate("ornek.com"));

        // Yeni servis ornegi ayni cevabi vermeli: durum BELLEKTE degil DISKTE.
        Assert.Equal(kip, Servis().GetServerMode().Mode);
    }

    /// <summary>
    /// <c>off</c> anahtarı yazmaz, dosyayı SİLER — ve site ayarları KORUNUR.
    /// </summary>
    /// <remarks>
    /// Silmek bilinçli: silinmiş dosya, ayrıştırıcı ne kadar bozulursa bozulsun KAPALI demektir.
    /// Site anahtarlarına dokunulmaması ise kademeli açılışın karşılığı — yönetici sunucuyu
    /// kapatıp açtığında kiracıların tek tek ayarları geri gelmeli, sıfırdan kurulmamalı.
    /// </remarks>
    [Fact]
    public void SUNUCU_GENELI_KAPATMA_DOSYAYI_SILER_ve_SITE_AYARI_KORUNUR()
    {
        var servis = Servis();
        new HtaccessSwitchStore(_veriKoku).SetSite("ornek.com", HtaccessMode.Enforce, null);
        servis.SetServerMode("enforce", null);
        Assert.Equal(HtaccessMode.Enforce, new HtaccessRuntimeGate(_veriKoku, TimeSpan.Zero).Evaluate("ornek.com"));

        var durum = servis.SetServerMode("off", null);

        Assert.Equal("off", durum.Mode);
        Assert.False(durum.SwitchFileExists);
        Assert.False(File.Exists(HtaccessHostingPaths.GlobalSwitchPath(_veriKoku)));
        Assert.Equal(HtaccessMode.Off, new HtaccessRuntimeGate(_veriKoku, TimeSpan.Zero).Evaluate("ornek.com"));

        // Site anahtari SILINMEDI: genel anahtar geri acilinca eski ayar yerinde.
        servis.SetServerMode("enforce", null);
        Assert.Equal(HtaccessMode.Enforce, new HtaccessRuntimeGate(_veriKoku, TimeSpan.Zero).Evaluate("ornek.com"));
    }

    /// <summary>
    /// TANINMAYAN kip REDDEDİLİR ve mevcut anahtara DOKUNULMAZ.
    /// </summary>
    /// <remarks>
    /// Sessizce "off"a düşseydi, "enfroce" yazan bir yönetici sunucuyu açtığını sanıp KAPATMIŞ
    /// olurdu — hem de panel ona "kaydedildi" demiş olarak.
    /// </remarks>
    [Fact]
    public void SUNUCU_GENELI_TANINMAYAN_KIP_REDDEDILIR_ve_ANAHTARA_DOKUNULMAZ()
    {
        var servis = Servis();
        servis.SetServerMode("observe", null);
        var oncekiIcerik = File.ReadAllText(HtaccessHostingPaths.GlobalSwitchPath(_veriKoku));

        var hata = Assert.Throws<AppException>(() => servis.SetServerMode("enfroce", null));

        Assert.Equal("htaccess.invalid_mode", hata.MessageKey);
        Assert.Equal(oncekiIcerik, File.ReadAllText(HtaccessHostingPaths.GlobalSwitchPath(_veriKoku)));
        Assert.Equal("observe", Servis().GetServerMode().Mode);
    }

    // ---------------------------------------------------------------- İNCELEME

    /// <summary>
    /// Desteklenmeyen direktifin GEREKÇESİ motorun kataloğundan gelir; panel metni uydurmaz.
    /// </summary>
    [Fact]
    public async Task DESTEKLENMEYEN_DIREKTIF_gerekcesiyle_bildirilir()
    {
        Yaz(".htaccess", "RewriteEngine On\nphp_value upload_max_filesize 64M\n");

        var inceleme = await Servis().InspectAsync("ornek.com", null, CancellationToken.None);

        var bulgu = Assert.Single(
            inceleme.Unsupported.Where(t => t.Directive.Equals("php_value", StringComparison.OrdinalIgnoreCase)));

        Assert.Equal(HtaccessDiagnosticCodes.UnsupportedDirective, bulgu.Code);
        // Metin motorun katalogundan geliyor: "sebep" bos olamaz, yoksa panel "desteklenmiyor"
        // demekle yetinir ve kullanicinin ne yapacagi soylenmemis olur.
        Assert.False(string.IsNullOrWhiteSpace(bulgu.Message));
        Assert.Contains("PHP", bulgu.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ALT_DIZIN_incelemesi_BASAMAKLANDIRMAYI_okur()
    {
        Yaz(".htaccess", "RewriteEngine On\n");
        Yaz("blog/.htaccess", "DirectoryIndex blog.php\n");

        var inceleme = await Servis().InspectAsync("ornek.com", "blog", CancellationToken.None);

        Assert.Equal("/blog/", inceleme.VirtualPath);
        Assert.Equal(2, inceleme.Files.Count);
        Assert.Contains("blog.php", inceleme.Effective.DirectoryIndex);
    }

    /// <summary>
    /// Site kökünün DIŞINA çıkan dizin isteği AÇIKÇA reddedilir.
    /// </summary>
    /// <remarks>
    /// Motorun kendi kapsama denetimi de var, ama o BOŞ rapor döndürürdü ve boş rapor "orada bir
    /// şey yok" gibi okunur. İki savunma katmanı birbirine güvenmez; burada reddetmek kullanıcıya
    /// "reddedildi" demenin tek yolu.
    /// </remarks>
    [Theory]
    [InlineData("../komsu-site")]
    [InlineData("..\\komsu-site")]
    [InlineData("blog/../../disari")]
    public async Task SITE_KOKUNUN_DISINA_cikan_dizin_REDDEDILIR(string dizin)
    {
        var hata = await Assert.ThrowsAsync<AppException>(
            () => Servis().InspectAsync("ornek.com", dizin, CancellationToken.None));

        Assert.Equal("htaccess.directory_outside_root", hata.MessageKey);
    }

    // ---------------------------------------------------------------- DOĞRULAMA

    [Fact]
    public void GECERLI_METIN_olumcul_bulgu_URETMEZ()
    {
        var sonuc = Servis().Validate("RewriteEngine On\nRewriteRule ^a$ /b [L]\n", "/");

        Assert.True(sonuc.Usable);
        Assert.DoesNotContain(sonuc.Diagnostics, t => t.Level == HtaccessDiagnosticLevel.Fatal);
    }

    [Fact]
    public void KAPANMAYAN_BLOK_olumcul_bulgu_URETIR()
    {
        var sonuc = Servis().Validate("<IfModule mod_rewrite.c>\nRewriteEngine On\n", "/");

        Assert.False(sonuc.Usable);
        Assert.Contains(sonuc.Diagnostics, t => t.Code == HtaccessDiagnosticCodes.UnclosedSection);
    }

    [Fact]
    public void DOGRULAMA_DISKTE_HICBIR_SEY_OKUMAZ_ve_YAZMAZ()
    {
        // Kaydetmeden ONCE dogrulama sozu: diske dokunulsaydi kullanicinin mevcut dosyasi
        // taslakla karisirdi.
        var oncekiler = Directory.GetFileSystemEntries(_siteKoku, "*", SearchOption.AllDirectories);

        Servis().Validate("RewriteEngine On\n", "/");

        Assert.Equal(oncekiler, Directory.GetFileSystemEntries(_siteKoku, "*", SearchOption.AllDirectories));
    }

    // ---------------------------------------------------------------- BENZETİM

    [Fact]
    public async Task BENZETIM_gercek_dosyalara_gore_YENIDEN_YAZMAYI_bildirir()
    {
        Yaz(".htaccess", "RewriteEngine On\nRewriteRule ^urun/([0-9]+)$ /index.php?id=$1 [L]\n");

        var sonuc = await Servis().SimulateAsync("ornek.com", "/urun/5", "GET", false, CancellationToken.None);

        Assert.Equal("rewrite", sonuc.Action);
        Assert.Equal("/index.php?id=5", sonuc.Target);
    }

    [Fact]
    public async Task BENZETIM_YONLENDIRMEYI_durum_koduyla_bildirir()
    {
        Yaz(".htaccess", "RewriteEngine On\nRewriteRule ^eski$ https://ornek.com/yeni [R=301,L]\n");

        var sonuc = await Servis().SimulateAsync("ornek.com", "/eski", "GET", false, CancellationToken.None);

        Assert.Equal("redirect", sonuc.Action);
        Assert.Equal(301, sonuc.StatusCode);
    }

    [Fact]
    public async Task BENZETIM_KURAL_ESLESMEZSE_hicbir_sey_demez()
    {
        Yaz(".htaccess", "RewriteEngine On\nRewriteRule ^urun/([0-9]+)$ /index.php?id=$1 [L]\n");

        var sonuc = await Servis().SimulateAsync("ornek.com", "/hakkimizda", "GET", false, CancellationToken.None);

        Assert.Equal("none", sonuc.Action);
    }

    /// <summary>
    /// Benzetimde SORGU DİZESİ ayrıştırılır: <c>/a?b=1</c> yolu <c>/a</c>, sorgusu <c>b=1</c>.
    /// </summary>
    [Fact]
    public async Task BENZETIM_sorgu_dizesini_YOLDAN_AYIRIR()
    {
        Yaz(".htaccess", "RewriteEngine On\nRewriteCond %{QUERY_STRING} ^p=1$\nRewriteRule ^ara$ /sonuc.php [L]\n");

        var sonuc = await Servis().SimulateAsync("ornek.com", "/ara?p=1", "GET", false, CancellationToken.None);

        Assert.Equal("/ara", sonuc.RequestPath);
        Assert.Equal("rewrite", sonuc.Action);
    }

    public void Dispose()
    {
        try
        {
            var taban = Directory.GetParent(_siteKoku)?.FullName;
            if (taban is not null && Directory.Exists(taban))
            {
                Directory.Delete(taban, recursive: true);
            }
        }
        catch (IOException)
        {
            // Geçici klasörün silinememesi testin sonucunu değiştirmez.
        }
    }
}
