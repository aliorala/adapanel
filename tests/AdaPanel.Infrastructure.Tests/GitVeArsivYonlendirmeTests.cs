using System.IO.Compression;
using System.Net;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Git;
using AdaPanel.Infrastructure.Git;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// GİT DAĞITIMI ve ARŞİV İÇE AKTARMA: adres kapısının ULAŞMADIĞI iki yol.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kapatılan iki açık, ikisi de ÖLÇÜLDÜ:</b>
/// </para>
/// <list type="number">
/// <item><b>scp kalıbı HİÇ DENETLENMİYORDU.</b> <c>GitDeploymentService</c> konağı
/// <c>Uri.TryCreate</c> ile çıkarıyordu; <c>git@169.254.169.254:latest/meta-data</c> bu makinede
/// ölçüldüğü üzere <c>Uri</c> ile ayrıştırılamaz (<c>TryCreate=false</c>), yani kod erken dönüyor
/// ve adres kapıya HİÇ uğramıyordu. Koruma zayıf değil SIFIRDI.</item>
/// <item><b>YÖNLENDİRMEYİ DIŞ ARAÇ İZLİYORDU.</b> git 2.54.0.windows.1 ile gerçek loopback
/// sunucularında ölçüldü: panelin kurduğu birebir ortamda <c>git clone</c> dış sunucunun
/// <c>302</c>'sini izledi ve İÇ sunucuya gerçek keşif isteği ULAŞTI. Aynı sınıf arşiv indirmesinde
/// de vardı (<c>Invoke-WebRequest</c> 3xx'i kendi izler).</item>
/// </list>
/// <para>
/// <b>HİÇBİR TEST MAKİNEDEN ÇIKMAZ.</b> Bütün sunucular <c>127.0.0.1</c> üzerinde testin kendi
/// dinleyicisiyle kalkar; hiçbir ada gerçek DNS çözümlemesi yapılmaz (sahte çözümleyici verilir).
/// </para>
/// <para>
/// <b>Kapının simülasyonu neden POLİTİKA DEĞİŞTİREREK yapılıyor:</b> bu makinede bağlanılabilen
/// hiçbir adres engel listesinin dışında değildir. Bu yüzden BİRİNCİ bağlantı "temiz dış konak"
/// sayılır, sonrakiler denetlenir; ölçülen şey "yönlendirme adımı denetleniyor mu"dur. Aynı gerekçe
/// <see cref="OutboundRedirectGuardTests"/>'te de yazılı.
/// </para>
/// </remarks>
public sealed class GitVeArsivYonlendirmeTests
{
    // =====================================================================================
    // 1 — GİT: scp KALIBI ARTIK KAPIDAN GEÇİYOR
    // =====================================================================================

    /// <summary>
    /// ASIL İDDİA: scp kalıbında yazılmış bir adres artık adres kapısına GİRİYOR ve iç hedefte
    /// reddediliyor.
    /// </summary>
    /// <remarks>
    /// MUTASYON: <c>GitRepositoryAddress.TryGetHost</c>'taki scp dalı kaldırılırsa (ya da
    /// <c>FindBlockedRepositoryHostAsync</c> ayrıştıramadığı adresi yine "temiz" sayarsa) bu satır
    /// KIRMIZI yanar. Eski kodda hepsi <c>null</c> dönüyordu.
    /// </remarks>
    [Theory]
    [InlineData("git@169.254.169.254:latest/meta-data", "169.254.169.254")]   // bulut meta verisi
    [InlineData("git@127.0.0.1:depo.git", "127.0.0.1")]                        // geri döngü
    [InlineData("git@10.0.0.5:ekip/depo.git", "10.0.0.5")]                     // iç ağ
    [InlineData("git@192.168.1.1:22:ekip/depo.git", "192.168.1.1")]            // portlu scp yazımı
    [InlineData("git@2130706433:depo.git", "127.0.0.1")]                       // ONDALIK IPv4 yazımı
    public async Task Scp_kalibindaki_ic_hedef_ARTIK_reddedilir(string adres, string beklenenHedef)
    {
        var engellenen = await GitDeploymentService.FindBlockedRepositoryHostAsync(
            adres,
            (_, _) => throw new InvalidOperationException("DNS beklenmiyordu: adres düz IP."),
            OutboundTargetPolicy.Strict,
            CancellationToken.None);

        Assert.Equal(beklenenHedef, engellenen);
    }

    /// <summary>
    /// scp kalıbında yazılmış bir AD da çözülür ve çıkan adres denetlenir — ad tarafında bakmak
    /// yetmez, çünkü saldırgan kendi alan adının A kaydını iç bir adrese çevirebilir.
    /// </summary>
    [Fact]
    public async Task Scp_kalibindaki_ad_COZULUR_ve_cikan_adres_denetlenir()
    {
        var engellenen = await GitDeploymentService.FindBlockedRepositoryHostAsync(
            "git@kotu.ornek.test:ekip/depo.git",
            (ad, _) => Task.FromResult(
                ad == "kotu.ornek.test" ? new[] { IPAddress.Parse("169.254.169.254") } : []),
            OutboundTargetPolicy.Strict,
            CancellationToken.None);

        Assert.Equal("169.254.169.254", engellenen);
    }

    /// <summary>
    /// FAZLA-KAPATMA NÖBETÇİSİ: MEŞRU bir GitHub/GitLab adresi — scp kalıbı dâhil — GEÇMEYE devam
    /// eder. Bu testin kırmızı yanması "kiracı kendi deposunu çekemiyor" demektir.
    /// </summary>
    [Theory]
    [InlineData("git@github.com:ada/panel.git")]
    [InlineData("git@github.com:22:ada/panel.git")]
    [InlineData("https://github.com/ada/panel.git")]
    [InlineData("https://kullanici:token@github.com/ada/panel.git")]
    [InlineData("ssh://git@gitlab.ornek.com:22/ada/panel.git")]
    [InlineData("http://gitlab.ornek.com/ada/panel.git")]
    public async Task Mesru_depo_adresleri_KAPIDAN_GECER(string adres)
    {
        var engellenen = await GitDeploymentService.FindBlockedRepositoryHostAsync(
            adres,
            // Her ad genel bir adrese çözülüyor (example.com'un gerçek adresi; hiçbir sokete
            // dokunulmaz, yalnız aralık denetimi yapılır).
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }),
            OutboundTargetPolicy.Strict,
            CancellationToken.None);

        Assert.Null(engellenen);
    }

    /// <summary>
    /// AYRIŞTIRILAMAYAN ADRES ARTIK "TEMİZ" DEĞİL. Eski kod ayrıştıramadığında <c>return</c>
    /// ediyordu; yani kapının kapsamı, ayrıştırıcının yeteneğine bağlıydı.
    /// </summary>
    [Theory]
    [InlineData("ext::sh -c whoami")]
    [InlineData("user@[::1]:depo.git")]
    [InlineData("")]
    public async Task Ayristirilamayan_adres_REDDEDILIR(string adres)
    {
        var engellenen = await GitDeploymentService.FindBlockedRepositoryHostAsync(
            adres,
            (_, _) => throw new InvalidOperationException("DNS beklenmiyordu."),
            OutboundTargetPolicy.Strict,
            CancellationToken.None);

        Assert.Equal("unparsable_host", engellenen);
    }

    /// <summary>
    /// AÇIK KAPI AÇIKKEN ayrıştırılamayan adres reddedilmez: işletici iç ağ hedeflerine bilerek
    /// izin verdiyse, ayrıştıramadığımız bir adres yüzünden meşru bir kurulumu kırmayız.
    /// </summary>
    [Fact]
    public async Task Acik_kapi_ACIKKEN_ayristirilamayan_adres_reddedilmez()
    {
        var engellenen = await GitDeploymentService.FindBlockedRepositoryHostAsync(
            "git@sunucu:depo.git",
            (_, _) => throw new InvalidOperationException("DNS beklenmiyordu."),
            OutboundTargetPolicy.Connecting(allowInternalTargets: true),
            CancellationToken.None);

        Assert.Null(engellenen);
    }

    // =====================================================================================
    // 2 — GİT: ÇAĞRI YERLERİ (kaydetme VE dağıtım)
    // =====================================================================================

    /// <summary>
    /// Kaydetme ucu (Bayi'ye kapalı, yöneticiye açık) scp kalıbındaki iç hedefi REDDEDER ve
    /// diske hiçbir yapılandırma yazılmaz.
    /// </summary>
    [Fact]
    public async Task Yapilandirma_scp_kalibindaki_ic_hedefi_REDDEDER()
    {
        var kok = GeciciKok();
        var servis = GitServisi(kok, _ => new[] { IPAddress.Parse("169.254.169.254") });

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.ConfigureAsync(
            "ornek.test",
            new ConfigureGitDeploymentRequest { RepositoryUrl = "git@kotu.ornek.test:ekip/depo.git", Branch = "main" },
            CancellationToken.None));

        Assert.Equal("git.repository_forbidden", hata.MessageKey);
    }

    /// <summary>
    /// DNS REBINDING: kaydetme anında TEMİZ olan adres, DAĞITIM anında iç adrese çözülüyorsa
    /// dağıtım REDDEDİLİR ve <c>git</c> hiç çalıştırılmaz.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kapatılan pencere: kapı YALNIZ <c>ConfigureAsync</c>'te koşuyordu. Webhook ucu ANONİM
    /// olduğundan tetikleme de saldırganın elindedir — yani "kaydet, kaydı çevir, webhook'u tetikle"
    /// dizisi tamamen dışarıdan sürülebiliyordu.
    /// </para>
    /// <para>
    /// MUTASYON: <c>RunDeployAsync</c>'in başındaki <c>EnsureRepositoryHostAllowedAsync</c> çağrısı
    /// kaldırılırsa bu test KIRMIZI yanar (istisna yerine "git.not_available"/klonlama hatası gelir).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Dagitim_ANINDA_ic_adrese_cozulen_depo_REDDEDILIR()
    {
        var kok = GeciciKok();
        var icAdres = false;
        var servis = GitServisi(
            kok,
            _ => icAdres ? [IPAddress.Parse("169.254.169.254")] : [IPAddress.Parse("93.184.216.34")]);

        // 1) Kaydetme: adres o an TEMİZ.
        await servis.ConfigureAsync(
            "ornek.test",
            new ConfigureGitDeploymentRequest { RepositoryUrl = "ssh://git@degisken.ornek.test/ekip/depo.git", Branch = "main" },
            CancellationToken.None);

        // 2) Kayıt iç adrese çevriliyor.
        icAdres = true;

        var hata = await Assert.ThrowsAsync<AppException>(
            () => servis.DeployAsync("ornek.test", CancellationToken.None));

        Assert.Equal("git.repository_forbidden", hata.MessageKey);
    }

    // =====================================================================================
    // 3 — GİT: YÖNLENDİRME (gerçek git.exe, gerçek soket)
    // =====================================================================================

    /// <summary>
    /// ASIL İDDİA: <c>git</c> artık <c>302</c> ile iç ağa SAPAMAZ — iç servise TEK BİR İSTEK BİLE
    /// ULAŞMAZ.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bu test kapatılan açığı doğrudan ölçer.</b> Aynı düzenekte, panelin ESKİ komut satırıyla
    /// (yani <c>-c http.followRedirects=false</c> olmadan) iç sunucu <c>GET
    /// /ic.git/info/refs?service=git-upload-pack</c> isteğini GERÇEKTEN aldı; ölçüm bu makinede
    /// git 2.54.0.windows.1 ile yapıldı.
    /// </para>
    /// <para>
    /// MUTASYON: <see cref="GitDeploymentService.BuildGitArguments"/> ön yapılandırmayı eklemeyi
    /// bırakırsa bu test KIRMIZI yanar (iç sunucu istek sayacı 1 olur).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Git_yonlendirmeyle_ic_aga_SAPAMAZ()
    {
        using var icSunucu = SahteHttpSunucu.BaslatHam(_ => (200, null, GitIcerikTipi, GitReklami()));
        using var disSunucu = SahteHttpSunucu.Baslat(
            yol => (302, $"http://127.0.0.1:{icSunucu.Port}/ic.git{(yol.Contains("info/refs") ? "/info/refs?service=git-upload-pack" : string.Empty)}", string.Empty));

        var calismaDizini = GeciciKok();
        var sonuc = await GitDeploymentService.RunGitForTests(
            $"ls-remote \"http://127.0.0.1:{disSunucu.Port}/dis.git\"",
            calismaDizini,
            CancellationToken.None);

        Assert.False(sonuc.Success);

        // ASIL ÖLÇÜM: iç servise HİÇBİR istek ulaşmadı.
        Assert.Equal(0, icSunucu.IstekSayisi);
        Assert.Equal(1, disSunucu.IstekSayisi);
    }

    /// <summary>
    /// FAZLA-KAPATMA NÖBETÇİSİ: yönlendirme YAPMAYAN meşru bir depo, aynı komut satırıyla SORUNSUZ
    /// okunur.
    /// </summary>
    /// <remarks>
    /// Düzeneğin kendisinin çalıştığının kanıtı: yukarıdaki testin "0 istek" sonucu, git'in sahte
    /// sunucuyla hiç konuşamamasından değil KORUMADAN geliyor. Burada aynı sahte sunucu doğrudan
    /// hedeflendiğinde <c>git ls-remote</c> ref'i GERÇEKTEN listeliyor.
    /// </remarks>
    [Fact]
    public async Task Yonlendirmesiz_mesru_depo_git_ile_OKUNABILIYOR()
    {
        using var sunucu = SahteHttpSunucu.BaslatHam(_ => (200, null, GitIcerikTipi, GitReklami()));

        var sonuc = await GitDeploymentService.RunGitForTests(
            $"ls-remote \"http://127.0.0.1:{sunucu.Port}/depo.git\"",
            GeciciKok(),
            CancellationToken.None);

        Assert.True(sonuc.Success, sonuc.StandardError);
        Assert.Contains("refs/heads/main", sonuc.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// FAZLA-KAPATMA NÖBETÇİSİ + ASIL ÇÖZÜM: MEŞRU bir yönlendirme (yeniden adlandırılmış depo,
    /// <c>http→https</c> yükseltmesi) artık PANEL tarafından çözülüyor ve git NİHAİ adrese gidiyor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Yönlendirmeyi git'e kapatmak tek başına yapılsaydı bu senaryo SESSİZCE kırılırdı. Panel
    /// zinciri denetlenen istemciyle çözüp git'e zaten çözülmüş adresi verdiği için hem koruma hem
    /// meşru davranış ayakta.
    /// </para>
    /// <para>
    /// MUTASYON: <c>ResolveHttpRepositoryUrlAsync</c> özgün adresi olduğu gibi dönerse bu test
    /// KIRMIZI yanar.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Mesru_yonlendirmeyi_PANEL_cozer_ve_nihai_adresi_git_e_verir()
    {
        using var hedefSunucu = SahteHttpSunucu.BaslatHam(_ => (200, null, GitIcerikTipi, GitReklami()));
        using var eskiSunucu = SahteHttpSunucu.Baslat(
            _ => (301, $"http://127.0.0.1:{hedefSunucu.Port}/yeni.git/info/refs?service=git-upload-pack", string.Empty));

        // Açık kapı AÇIK: loopback hedefi meşru sayılsın (bu makinede bağlanılabilen hiçbir adres
        // engel listesinin dışında değil). Ölçülen şey zincirin çözülüp çözülmediğidir.
        var servis = GitServisi(
            GeciciKok(),
            _ => [IPAddress.Parse("93.184.216.34")],
            IcAgaIzinliFabrika(),
            icAgaIzin: true);

        var cozulen = await servis.ResolveHttpRepositoryUrlAsync(
            $"http://127.0.0.1:{eskiSunucu.Port}/eski.git",
            CancellationToken.None);

        Assert.Equal($"http://127.0.0.1:{hedefSunucu.Port}/yeni.git", cozulen);

        // Nihai adres git ile GERÇEKTEN okunabiliyor: zincir çözüldü, klon çalışmaya devam ediyor.
        var sonuc = await GitDeploymentService.RunGitForTests(
            $"ls-remote \"{cozulen}\"",
            GeciciKok(),
            CancellationToken.None);

        Assert.True(sonuc.Success, sonuc.StandardError);
    }

    /// <summary>
    /// Ön çözümleme sırasında zincir İÇ AĞA saparsa istek atılmaz ve kullanıcı adres yasağını
    /// görür — "ağ hatası" değil.
    /// </summary>
    [Fact]
    public async Task On_cozumleme_ic_aga_sapan_zinciri_REDDEDER()
    {
        using var icSunucu = SahteHttpSunucu.BaslatHam(_ => (200, null, GitIcerikTipi, GitReklami()));
        using var disSunucu = SahteHttpSunucu.Baslat(
            _ => (302, $"http://127.0.0.1:{icSunucu.Port}/ic.git/info/refs?service=git-upload-pack", string.Empty));

        var servis = GitServisi(
            GeciciKok(),
            _ => [IPAddress.Parse("93.184.216.34")],
            IlkBaglantiTemizFabrika());

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.ResolveHttpRepositoryUrlAsync(
            $"http://127.0.0.1:{disSunucu.Port}/dis.git",
            CancellationToken.None));

        Assert.Equal("git.repository_forbidden", hata.MessageKey);
        Assert.Equal(0, icSunucu.IstekSayisi);
    }

    /// <summary>
    /// İKİNCİ KATMAN, DAVRANIŞLA ÖLÇÜLDÜ: git'in KENDİ taşıyıcı beyaz listesi
    /// (<c>GIT_ALLOW_PROTOCOL</c>) tehlikeli taşıyıcıları GERÇEKTEN kesiyor.
    /// </summary>
    /// <remarks>
    /// <c>ext::</c> git için "bu komutu çalıştır" demektir ve panel LocalSystem koşar; <c>file://</c>
    /// ve şemasız yerel yol ise sunucudaki her dizini "depo" yapabilirdi. Üçü de burada GERÇEK
    /// <c>git.exe</c> ile ölçülüyor — kaynak taraması değil.
    /// </remarks>
    [Fact]
    public async Task Tehlikeli_tasiyicilar_git_tarafindan_REDDEDILIR()
    {
        var calismaDizini = GeciciKok();
        var isaret = Path.Combine(calismaDizini, "KOMUT-CALISTI.txt");

        var ext = await GitDeploymentService.RunGitForTests(
            $"ls-remote \"ext::cmd /c echo x > {isaret}\"",
            calismaDizini,
            CancellationToken.None);

        Assert.False(ext.Success);
        Assert.False(File.Exists(isaret));
        Assert.Contains("not allowed", ext.StandardError, StringComparison.OrdinalIgnoreCase);

        // Yerel yol (hem file:// hem şemasız) da taşıyıcı listesinin dışında.
        var yerelDepo = GeciciKok();
        await GitDeploymentService.RunGitForTests("init --bare", yerelDepo, CancellationToken.None);

        var dosya = await GitDeploymentService.RunGitForTests(
            $"ls-remote \"{yerelDepo}\"",
            calismaDizini,
            CancellationToken.None);

        Assert.False(dosya.Success);
        Assert.Contains("not allowed", dosya.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    // =====================================================================================
    // 4 — ARŞİV İÇE AKTARMA (Bayi rolüne AÇIK uç)
    // =====================================================================================

    /// <summary>
    /// KURULAN KOMUT: <c>http/https</c> arşivi ARTIK PowerShell'den İNMİYOR; <c>ftp</c> iniyor.
    /// </summary>
    /// <remarks>
    /// Bu ayrım korumanın kendisidir: <c>Invoke-WebRequest</c> 3xx'i kendi izler ve panelin adres
    /// kapısı o adımları göremez. Ölçülen dize üretimde çalıştırılan dizenin ta kendisidir.
    /// </remarks>
    [Fact]
    public void Arsiv_scripti_http_icin_indirme_ADIMI_ICERMEZ_ftp_icin_ICERIR()
    {
        var http = SiteHostingSettingsService.BuildImportScript(false, "http://ornek.test/s.zip", @"C:\t\a.zip", @"C:\t\x");
        Assert.DoesNotContain("Invoke-WebRequest", http, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Expand-Archive", http, StringComparison.Ordinal);

        var ftp = SiteHostingSettingsService.BuildImportScript(true, "ftp://ornek.test/s.zip", @"C:\t\a.zip", @"C:\t\x");
        Assert.Contains("Invoke-WebRequest", ftp, StringComparison.Ordinal);
        Assert.Contains("ftp://ornek.test/s.zip", ftp, StringComparison.Ordinal);
    }

    /// <summary>
    /// ASIL İDDİA: doğrulamayı geçmiş bir arşiv adresi, <c>302</c> ile iç ağa ATLAYAMAZ. İç servise
    /// TEK BİR İSTEK BİLE ULAŞMAZ ve kullanıcı adres yasağını görür.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uç <b>Bayi (Reseller)</b> rolüne açıktır (<c>SiteHostingSettingsController</c>:
    /// <c>[Authorize(Roles = "Administrator,Reseller")]</c>) ve indirmeyi panel LocalSystem olarak
    /// yapar — yani bu, kiracının elindeki bir SSRF'ti.
    /// </para>
    /// <para>
    /// MUTASYON: indirme yeniden <c>Invoke-WebRequest</c>'e alınırsa (ya da denetlenen istemci
    /// yerine düz bir <c>HttpClient</c> kullanılırsa) bu test KIRMIZI yanar.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Arsiv_indirmesi_yonlendirmeyle_ic_aga_SAPAMAZ()
    {
        using var icSunucu = SahteHttpSunucu.BaslatHam(_ => (200, null, "application/zip", ZipUret()));
        using var disSunucu = SahteHttpSunucu.Baslat(
            _ => (302, $"http://127.0.0.1:{icSunucu.Port}/ic.zip", string.Empty));

        var (servis, _) = ArsivServisi(IlkBaglantiTemizFabrika());

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.ImportSiteArchiveAsync(
            "ornek.test",
            $"http://arsiv.ornek.test:{disSunucu.Port}/site.zip",
            cleanFirst: false,
            CancellationToken.None));

        Assert.Equal("migration.url_forbidden", hata.MessageKey);
        Assert.Equal(0, icSunucu.IstekSayisi);
        Assert.Equal(1, disSunucu.IstekSayisi);
    }

    /// <summary>
    /// FAZLA-KAPATMA NÖBETÇİSİ: MEŞRU bir arşiv indirmesi — YÖNLENDİRMELİ olanı da — çalışmaya
    /// devam eder ve dosyalar siteye GERÇEKTEN kopyalanır.
    /// </summary>
    /// <remarks>
    /// Yönlendirmeyi kapatmak (ya da indirmeyi tümden reddetmek) bu testi kırardı; CDN ve S3 bölge
    /// yönlendirmesi yapan hedefler arşiv indirmesinin YAYGIN hâlidir.
    /// </remarks>
    [Fact]
    public async Task Mesru_arsiv_indirmesi_yonlendirmeyi_IZLER_ve_dosyalar_KOPYALANIR()
    {
        using var icSunucu = SahteHttpSunucu.BaslatHam(_ => (200, null, "application/zip", ZipUret()));
        using var disSunucu = SahteHttpSunucu.Baslat(
            _ => (302, $"http://127.0.0.1:{icSunucu.Port}/gercek.zip", string.Empty));

        var (servis, siteKoku) = ArsivServisi(IcAgaIzinliFabrika());

        var sonuc = await servis.ImportSiteArchiveAsync(
            "ornek.test",
            $"http://arsiv.ornek.test:{disSunucu.Port}/site.zip",
            cleanFirst: false,
            CancellationToken.None);

        Assert.True(sonuc.FilesCopied >= 1);
        Assert.Equal(1, icSunucu.IstekSayisi);
        Assert.Equal("merhaba", await File.ReadAllTextAsync(Path.Combine(siteKoku, "index.html")));
    }

    /// <summary>
    /// HTTP hatası "adres yasak" ile KARIŞTIRILMAZ: teşhis buna bağlı.
    /// </summary>
    [Fact]
    public async Task Arsiv_indirmesi_HTTP_hatasini_yasakla_KARISTIRMAZ()
    {
        using var sunucu = SahteHttpSunucu.Baslat(_ => (404, null, "yok"));
        var (servis, _) = ArsivServisi(IcAgaIzinliFabrika());

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.ImportSiteArchiveAsync(
            "ornek.test",
            $"http://arsiv.ornek.test:{sunucu.Port}/site.zip",
            cleanFirst: false,
            CancellationToken.None));

        Assert.Equal("migration.download_failed", hata.MessageKey);
        Assert.Contains("404", hata.Details ?? string.Empty, StringComparison.Ordinal);
    }

    // =====================================================================================
    // YARDIMCILAR
    // =====================================================================================

    private const string GitIcerikTipi = "application/x-git-upload-pack-advertisement";

    /// <summary>
    /// git'in AKILLI HTTP keşif yanıtı (pkt-line). Uzunluklar ELLE YAZILMAZ, hesaplanır: yanlış
    /// bir uzunluk git'i "aptal protokol"e düşürür ve test ölçtüğünü sandığı şeyi ölçmez.
    /// </summary>
    private static byte[] GitReklami()
    {
        const string sha = "1234567890abcdef1234567890abcdef12345678";
        var govde = new StringBuilder()
            .Append(PktSatir("# service=git-upload-pack\n"))
            .Append("0000")
            .Append(PktSatir($"{sha} refs/heads/main\0multi_ack\n"))
            .Append("0000")
            .ToString();

        return Encoding.ASCII.GetBytes(govde);

        static string PktSatir(string yuk) => (yuk.Length + 4).ToString("x4") + yuk;
    }

    /// <summary>İçinde tek bir <c>index.html</c> olan gerçek bir zip.</summary>
    private static byte[] ZipUret()
    {
        using var bellek = new MemoryStream();
        using (var arsiv = new ZipArchive(bellek, ZipArchiveMode.Create, leaveOpen: true))
        {
            var girdi = arsiv.CreateEntry("index.html");
            using var akis = girdi.Open();
            akis.Write(Encoding.UTF8.GetBytes("merhaba"));
        }

        return bellek.ToArray();
    }

    private static string GeciciKok()
    {
        var kok = Path.Combine(Path.GetTempPath(), "ada-git-arsiv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(kok);
        return kok;
    }

    private static GitDeploymentService GitServisi(
        string kok,
        Func<string, IPAddress[]> dns,
        IHttpClientFactory? fabrika = null,
        bool icAgaIzin = false)
    {
        var secenekler = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions
        {
            DataRoot = kok,
            AllowInternalOutboundTargets = icAgaIzin
        });

        return new GitDeploymentService(
            new SahteSiteServisi(kok),
            new JsonFileStore(secenekler),
            new ConcurrencyLock(),
            NullLogger<GitDeploymentService>.Instance,
            secenekler,
            (ad, _) => Task.FromResult(dns(ad)),
            fabrika);
    }

    /// <summary>
    /// Arşiv servisi + sitenin kökü. Ön kapının çözümleyicisi GENEL bir adres döner (yoksa
    /// loopback hedefi daha soket açılmadan reddedilirdi ve yönlendirme hiç ölçülemezdi);
    /// bağlantı anındaki denetim ise fabrikaya verilen politikadan gelir.
    /// </summary>
    private static (SiteHostingSettingsService Servis, string SiteKoku) ArsivServisi(IHttpClientFactory fabrika)
    {
        var veriKoku = GeciciKok();
        var siteKoku = GeciciKok();
        var secenekler = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = veriKoku });

        var servis = new SiteHostingSettingsService(
            new SahteSiteServisi(siteKoku),
            null!,
            null!,
            secenekler,
            new ConcurrencyLock(),
            new JsonFileStore(secenekler),
            fabrika,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        return (servis, siteKoku);
    }

    /// <summary>İLK bağlantıyı "temiz dış konak" sayan, sonrakileri denetleyen fabrika.</summary>
    private static IHttpClientFactory IlkBaglantiTemizFabrika()
    {
        var sayac = 0;
        return new SahteFabrika(() => Interlocked.Increment(ref sayac) == 1
            ? OutboundTargetPolicy.Connecting(allowInternalTargets: true)
            : OutboundTargetPolicy.Strict);
    }

    /// <summary>Açık kapı AÇIK: zincir sonuna kadar izlenir (fazla-kapatma nöbetçileri için).</summary>
    private static IHttpClientFactory IcAgaIzinliFabrika()
        => new SahteFabrika(() => OutboundTargetPolicy.Connecting(allowInternalTargets: true));

    /// <summary>
    /// Yalnız DENETLENEN işleyiciyi üreten fabrika. Ad çözümlemesi sahtedir: her ad loopback'e
    /// çözülür, yani test hiçbir gerçek DNS sorgusu yapmaz.
    /// </summary>
    private sealed class SahteFabrika(Func<OutboundTargetPolicy> politika) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(OutboundConnectGuard.CreateHandler(
                politika,
                (_, _) => Task.FromResult(new[] { IPAddress.Loopback })));
    }
}
