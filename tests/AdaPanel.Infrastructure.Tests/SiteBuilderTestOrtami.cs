using System.Diagnostics;
using AdaPanel.Core.Dtos.Notifications;
using AdaPanel.Core.Dtos.SiteBuilder;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.SiteBuilder;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Site kurucunun GERÇEK dosya sistemiyle çalışan uçtan uca test ortamı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ortak dosyada:</b> aynı kurulum (geçici veri kökü + geçici site kökü + sahte IIS)
/// yayın güvenliği, çok sayfa ve iletişim formu testlerinin ÜÇÜNDE de lazım. Üç kopya olsaydı
/// biri güncellenip diğerleri geride kalırdı — ve geride kalan kopya, gerçekte var olmayan bir
/// dünyayı doğrulayan yeşil bir test üretirdi.
/// </para>
/// <para>
/// <b>Neden gerçek dosya sistemi:</b> bu modülün en riskli tarafı diske yazmak. Sahte bir dosya
/// katmanı, tam da korunmak istenen şeyi (kavşak, üzerine yazma, öksüz kalan dosya) taklit
/// edemez.
/// </para>
/// </remarks>
internal sealed class SiteBuilderTestOrtami : IAsyncDisposable
{
    public const string SiteAdi = "ornek.com";

    private SiteBuilderTestOrtami(
        string veriKoku,
        string siteKoku,
        JsonFileStore depo,
        SiteBuilderService servis,
        SahtePostaci postaci)
    {
        VeriKoku = veriKoku;
        SiteKoku = siteKoku;
        Depo = depo;
        Servis = servis;
        Postaci = postaci;
    }

    public string VeriKoku { get; }

    public string SiteKoku { get; }

    public JsonFileStore Depo { get; }

    public SiteBuilderService Servis { get; }

    /// <summary>Gönderilen postaları TUTAN sahte gönderici; gerçekten SMTP'ye çıkmaz.</summary>
    public SahtePostaci Postaci { get; }

    public SiteBuilderContactRateLimiter HizSiniri { get; } = new();

    public static Task<SiteBuilderTestOrtami> KurAsync()
    {
        var taban = GeciciKlasor();
        var veriKoku = Path.Combine(taban, "data");
        var siteKoku = Path.Combine(taban, "site");
        Directory.CreateDirectory(veriKoku);
        Directory.CreateDirectory(siteKoku);

        var depo = new JsonFileStore(
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = veriKoku }));

        var servis = new SiteBuilderService(
            new SiteBuilderSahteSiteServisi(siteKoku),
            depo,
            NullLogger<SiteBuilderService>.Instance);

        return Task.FromResult(new SiteBuilderTestOrtami(taban, siteKoku, depo, servis, new SahtePostaci()));
    }

    /// <summary>Panelin genel erişim adını ayarlar — iletişim formunun gönderim hedefi buradan kurulur.</summary>
    public Task PanelAnaAdiAyarlaAsync(string hostName) =>
        Depo.WriteAsync("config/panel-preferences.json", new PanelPreferencesDto { PanelHostName = hostName });

    /// <summary>Kimliksiz form ucunu bu ortamın dosyalarıyla çalıştıran servis.</summary>
    public SiteBuilderContactService FormServisi() => new(
        Depo,
        Postaci,
        HizSiniri,
        NullLogger<SiteBuilderContactService>.Instance);

    /// <summary>Gerçek bir PNG yükler (imza baytları doğru).</summary>
    public async Task<SiteBuilderImageDto> GorselYukleAsync()
    {
        var png = SiteBuilderImageFixtures.MinimalPng();
        using var akis = new MemoryStream(png);
        return await Servis.UploadImageAsync(SiteAdi, akis, png.LongLength);
    }

    public ValueTask DisposeAsync()
    {
        Temizle(Path.GetDirectoryName(SiteKoku)!);
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------ ortak yardımcılar

    public static string GeciciKlasor()
    {
        var yol = Path.Combine(Path.GetTempPath(), $"adapanel-sb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(yol);
        return yol;
    }

    public static void Temizle(string yol)
    {
        try
        {
            if (Directory.Exists(yol))
            {
                // Kavşakları İZLEMEDEN temizle: testin kendisi de yanlışlıkla hedefi silmesin.
                foreach (var dizin in Directory.GetDirectories(yol, "*", SearchOption.AllDirectories)
                             .Where(d => File.GetAttributes(d).HasFlag(FileAttributes.ReparsePoint)))
                {
                    Directory.Delete(dizin);
                }

                Directory.Delete(yol, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temizlik testin konusu değil.
        }
    }

    public static bool KavsakKur(string baglanti, string hedef)
    {
        try
        {
            using var süreç = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{baglanti}\" \"{hedef}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            süreç?.WaitForExit(10000);
            return Directory.Exists(baglanti)
                   && File.GetAttributes(baglanti).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Gönderilen postayı YAKALAYAN sahte gönderici.
/// </summary>
/// <remarks>
/// Gerçek SMTP'ye çıkmaz; testler "ne gönderildi" sorusunu (alıcı, KONU SATIRI, gövde) tam
/// olarak ölçebilsin diye her iletiyi saklar. Başlık enjeksiyonu testleri buna dayanıyor:
/// gönderim taklit edilmeseydi, konu satırının içine kaçan bir <c>\r\n</c> ancak canlı bir posta
/// sunucusuyla görülebilirdi — yani pratikte hiç görülmezdi.
/// </remarks>
internal sealed class SahtePostaci : INotificationService
{
    public sealed record Ileti(string To, string Subject, string Body);

    public List<Ileti> Gonderilenler { get; } = [];

    /// <summary>Doluysa gönderim bu istisnayla BAŞARISIZ olur (SMTP ayarsız senaryosu).</summary>
    public Exception? Ariza { get; set; }

    public Task SendEmailToAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default)
    {
        if (Ariza is not null)
        {
            throw Ariza;
        }

        Gonderilenler.Add(new Ileti(toEmail, subject, body));
        return Task.CompletedTask;
    }

    public Task<NotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<NotificationSettingsDto> UpdateSettingsAsync(UpdateNotificationSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task SendEmailAsync(string subject, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<bool> IsEmailConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(Ariza is null);

    public Task SendWebhookAsync(string eventName, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task TestAsync(TestNotificationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>Sitenin fiziksel yolunu SABİT döndüren IIS taklidi.</summary>
/// <remarks>
/// <para>
/// Hedef klasörün istekten DEĞİL IIS'ten çözüldüğü kuralı, testlerde de aynı kapıdan geçsin diye
/// burada duruyor: servis site adını verir, yolu bu taklit söyler.
/// </para>
/// <para>
/// Ad <c>SiteBuilder</c> önekli: test projesinde ad alanı düzeyinde başka bir
/// <c>SahteSiteServisi</c> zaten var (yedekleme testleri). Aynı adı kullanmak, iki modülün
/// taklidini birbirine bağlar ve birinin ihtiyacı değiştiğinde diğerini sessizce bozardı.
/// </para>
/// </remarks>
internal sealed class SiteBuilderSahteSiteServisi(string uygulamaKoku) : IIisSiteService
{
    public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SiteDto
        {
            Name = siteName,
            PhysicalPath = uygulamaKoku,
            ApplicationRootPath = uygulamaKoku,
            Bindings = [new SiteBindingDto { Protocol = "https", HostName = siteName, Port = 443, BindingInformation = "*:443:" + siteName }]
        });

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
