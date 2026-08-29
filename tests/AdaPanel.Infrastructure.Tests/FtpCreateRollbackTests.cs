using System.Diagnostics;
using System.Security.Principal;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Ftp;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// YARIM KALAN FTP OLUŞTURMASI ÖKSÜZ WINDOWS HESABI BIRAKMAZ.
/// </summary>
/// <remarks>
/// <para>
/// <b>Düzeltilen hata (19.08.2026, canlı ölçüm).</b> Yetki kuralı kaldırma sessizce başarısız
/// olduğu için aynı adın yeniden oluşturulması <c>500 ftp.site_configuration_failed</c> veriyordu.
/// Ama <c>net user /add</c> ÇOKTAN çalışmıştı: <c>net user &lt;ad&gt;</c> hesabın VAR olduğunu
/// gösteriyor, <c>GET /ftp/users</c> onu LİSTELEMİYORDU (panel kaydı hatadan sonra yazılıyor).
/// Geriye parolası operatörün belirlediği, panelde görünmeyen, panelden silinemeyen bir yerel
/// hesap kalıyordu.
/// </para>
/// <para>
/// <b>Neden <see cref="FtpService.RollbackCreateAsync"/> doğrudan koşuluyor:</b> tam
/// <c>CreateFtpUserAsync</c> yolundan test etmek mümkün değil — o yol
/// <c>EnsureFtpInfrastructureAsync</c>'i çağırır ve yükseltilmiş bir makinede GERÇEK
/// "AdaPanel FTP" sitesini yeniden yapılandırıp <b>FTPSVC'yi yeniden başlatırdı</b>. Bir testin
/// canlı FTP sunucusunu yeniden başlatması kabul edilemez. Korunan davranış geri almanın
/// kendisidir ve burada gerçek bir Windows hesabıyla ölçülür.
/// </para>
/// <para>
/// <b>DÜRÜST SINIR:</b> yerel hesap açmak yükseltilmiş yetki ister. Yetki yoksa testler ölçüm
/// yapamadıklarını söyleyip çıkar — "çalıştıramadım" ile "doğru" aynı şey değildir.
/// </para>
/// </remarks>
public sealed class FtpCreateRollbackTests : IDisposable
{
    private readonly string veriKoku = Path.Combine(Path.GetTempPath(), $"ada-ftp-geri-{Guid.NewGuid():N}");

    // Ad 20 karakteri asamaz (net user siniri) ve testin kendi izini tasimali.
    private readonly string hesapAdi = "ada_gd_" + Guid.NewGuid().ToString("N")[..8];

    public FtpCreateRollbackTests() => Directory.CreateDirectory(veriKoku);

    public void Dispose()
    {
        // Test ne olursa olsun MAKİNEDE İZ BIRAKMAZ; testin kendisi öksüz hesap üretemez.
        if (HesapVar(hesapAdi))
        {
            NetUser($"user \"{hesapAdi}\" /delete");
        }

        try { Directory.Delete(veriKoku, recursive: true); } catch { /* en iyi çaba */ }
    }

    [Fact]
    public async Task Bu_cagrinin_actigi_hesap_GERI_ALINIR()
    {
        if (!YukseltilmisMi())
        {
            // Yetki yok -> yerel hesap acilamaz -> davranis OLCULEMEDI. Sessiz bir "yesil" degil,
            // bilincli bir cikis: yonetici yetkisiyle kosan makinede (CI, sunucu) kendiliginden calisir.
            return;
        }

        Assert.True(NetUser($"user \"{hesapAdi}\" \"GeriAlmaParola123!\" /add /y"), "Test önkoşulu: hesap açılamadı.");
        Assert.True(HesapVar(hesapAdi), "Test önkoşulu: hesap açıldı sayılmadı.");

        var evDizini = Path.Combine(veriKoku, "site-koku");
        Directory.CreateDirectory(evDizini);
        File.WriteAllText(Path.Combine(evDizini, "index.php"), "musteri dosyasi");

        var not = await ServisKur().RollbackCreateAsync(hesapAdi, evDizini, hesabiBuCagriActi: true);

        // ASIL İDDİA: öksüz kalmadı.
        Assert.False(HesapVar(hesapAdi), $"Hesap '{hesapAdi}' geri alınmadı — ÖKSÜZ kaldı. Geri alma notu: {not}");

        // MÜŞTERİNİN DOSYALARI DURUYOR: geri alma ACL'i geri alır, ana dizini SİLMEZ. homePath
        // müşterinin site köküdür; burada bir Remove-Item, hatayı düzeltirken veri kaybı olurdu.
        Assert.True(Directory.Exists(evDizini));
        Assert.True(File.Exists(Path.Combine(evDizini, "index.php")));
    }

    [Fact]
    public async Task Bu_cagrinin_ACMADIGI_hesap_ASLA_silinmez()
    {
        if (!YukseltilmisMi())
        {
            return;
        }

        Assert.True(NetUser($"user \"{hesapAdi}\" \"GeriAlmaParola123!\" /add /y"), "Test önkoşulu: hesap açılamadı.");

        var evDizini = Path.Combine(veriKoku, "site-koku");
        Directory.CreateDirectory(evDizini);

        // Meşru "kendi hesabımı yeniden oluştur" akışı (FtpUserNameClaim.KendiKaydi): hesap zaten
        // vardı, bu çağrı açmadı. Ara bir adım takılırsa müşterinin ÇALIŞAN hesabını silmek,
        // düzeltilen hatadan beter bir yıkım olurdu.
        await ServisKur().RollbackCreateAsync(hesapAdi, evDizini, hesabiBuCagriActi: false);

        Assert.True(HesapVar(hesapAdi), "Panelin açmadığı hesap silindi — geri alma kendi sınırını aştı.");
    }

    // --- düzenek ---

    private FtpService ServisKur() => new(
        new PatlayanSiteServisi(),
        new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = veriKoku })),
        new SahteTercihler(),
        Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = veriKoku }));

    private static bool YukseltilmisMi()
    {
        using var kimlik = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(kimlik).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool HesapVar(string ad) => NetUser($"user \"{ad}\"");

    private static bool NetUser(string argumanlar)
    {
        using var surec = Process.Start(new ProcessStartInfo
        {
            FileName = "net.exe",
            Arguments = argumanlar,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        surec.StandardOutput.ReadToEnd();
        surec.StandardError.ReadToEnd();
        surec.WaitForExit(60_000);
        return surec.ExitCode == 0;
    }

    /// <summary>Geri alma IIS site servisine HİÇ sormamalı; sorarsa test bunu görür.</summary>
    private sealed class PatlayanSiteServisi : IIisSiteService
    {
        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SahteTercihler : IPanelPreferencesService
    {
        public Task<PanelPreferencesDto> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new PanelPreferencesDto());

        public Task<PanelPreferencesDto> UpdateAsync(UpdatePanelPreferencesRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
