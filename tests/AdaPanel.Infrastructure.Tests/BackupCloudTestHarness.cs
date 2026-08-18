using System.Collections.Concurrent;
using AdaPanel.Core.Dtos.Audit;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Bulut hedefi (Dropbox / OneDrive / Azure) testlerinin ORTAK kurulumu.
/// </summary>
/// <remarks>
/// <para>
/// Uc test dosyasi ayni sahte sunucuyu ve ayni servis kurulumunu istiyor. Ucunde ayri ayri
/// yazmak, bir tanesi duzeltilip digerleri unutuldugunda "bir testte yakalanan, obur testte
/// kacan" bir bosluk birakirdi.
/// </para>
/// <para>
/// <b>DURUSTLUK NOTU:</b> buradaki istemci SAHTEDIR. Gercek Dropbox/OneDrive/Azure hesabina
/// hicbir istek gitmez. Test edilen sey PROTOKOL KARARLARIDIR: hangi ucca, hangi yola, hangi
/// basliklarla gidiliyor ve kayda ne yaziliyor. "Gercek hesapta calisir" iddiasi BU TESTLERDEN
/// CIKARILAMAZ.
/// </para>
/// </remarks>
internal sealed class BulutAlani : IDisposable
{
    private readonly CancellationTokenSource _iptal = new(TimeSpan.FromSeconds(60));

    public BulutAlani()
    {
        Kok = Path.Combine(Path.GetTempPath(), $"adapanel-bulut-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Kok);
        DosyaDeposu = new JsonFileStore(
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Kok }));
        SiteKoku = Path.Combine(Kok, "wwwroot", "shop");
        Directory.CreateDirectory(SiteKoku);
        File.WriteAllText(Path.Combine(SiteKoku, "index.php"), "MUSTERI ICERIGI");
    }

    /// <summary>Testlerdeki sitenin adi; uzak yolda KAYNAK klasoru olarak gecer.</summary>
    public const string Site = "shop.example.com";

    public string Kok { get; }

    public string SiteKoku { get; }

    public JsonFileStore DosyaDeposu { get; }

    public SahteDenetim Denetim { get; } = new();

    public CancellationToken Iptal => _iptal.Token;

    public BackupService ServisKur(SahteBulut bulut, ISecretProtector? koruyucu = null) =>
        new(new SahteSiteServisi(SiteKoku),
            null!,
            null!,
            new SahteTercihServisi(Path.Combine(Kok, "backups")),
            null!,
            DosyaDeposu,
            new SahteIstemciFabrikasi(bulut),
            secretProtector: koruyucu ?? new SahteKoruyucu(),
            auditService: Denetim);

    public Task KayitlariYazAsync(params BackupRecordDto[] kayitlar) =>
        DosyaDeposu.WriteAsync("config/backups.json", kayitlar, Iptal);

    /// <summary>
    /// Otomasyon ayarlarini (uzak hedef + KORUNMUS parola) yazar.
    /// </summary>
    /// <remarks>
    /// Elle silme, kimligi tam da bu dosyadan okur. Testin bu dosyayi GERCEK alan adlariyla
    /// yazmasi, <see cref="BackupRemoteCredentialReader"/>'in surukleme riskini de olcer.
    /// </remarks>
    public Task UzakHedefiYazAsync(string tur, string adres, string kullanici, string korunmusParola, string bolge = "") =>
        DosyaDeposu.WriteAsync(
            "config/automation.json",
            new Dictionary<string, object>
            {
                ["backupTargetType"] = tur,
                ["backupRemoteUrl"] = adres,
                ["backupRemoteUserName"] = kullanici,
                ["backupRemoteRegion"] = bolge,
                ["backupRemotePasswordProtected"] = korunmusParola
            },
            Iptal);

    /// <summary>Kaydin YEREL yolundan gun klasorunu okur (uzak yolun ayni karardan turedigini olcmek icin).</summary>
    public string YerelGun(BackupRecordDto kayit)
    {
        var klasor = new DirectoryInfo(Path.GetDirectoryName(kayit.Path)!);
        while (klasor is not null)
        {
            if (DateTime.TryParseExact(
                    klasor.Name,
                    "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out _))
            {
                return klasor.Name;
            }

            klasor = klasor.Parent;
        }

        throw new InvalidOperationException($"Yedek yolunda gun klasoru yok: {kayit.Path}");
    }

    public void Dispose()
    {
        _iptal.Dispose();
        try
        {
            Directory.Delete(Kok, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Bir sahte HTTP istegi: neyin nereye, hangi basliklarla gittigi.</summary>
internal sealed record SahteIstek(string Method, string Url, string? Authorization, string? DropboxArg, string? ContentRange, string? Body)
{
    public string Ozet => $"{Method} {Url}";
}

/// <summary>
/// Dropbox / OneDrive / Azure uclarini taklit eden HTTP isleyicisi.
/// </summary>
/// <remarks>
/// Yanit ESLESTIRME ile verilir: (yontem, adres parcasi) -> (durum, govde). Boylece tek bir
/// akista birden fazla uc (jeton tazeleme + oturum acma + parca yukleme) ayri ayri
/// yonlendirilebilir. Eslesme yoksa varsayilan yanit doner.
/// </remarks>
internal sealed class SahteBulut
{
    private readonly List<(string Method, string Contains, System.Net.HttpStatusCode Status, string Body)> _kurallar = [];

    public ConcurrentQueue<SahteIstek> Istekler { get; } = new();

    public List<string> Ozetler => Istekler.Select(istek => istek.Ozet).ToList();

    public System.Net.HttpStatusCode VarsayilanDurum { get; set; } = System.Net.HttpStatusCode.OK;

    public string VarsayilanGovde { get; set; } = "{}";

    /// <summary>Istek atildiginda firlatilacak ag hatasi (null = hata yok).</summary>
    public Func<string, Exception?>? AgHatasi { get; set; }

    public SahteBulut Yanit(string method, string contains, System.Net.HttpStatusCode status, string body = "{}")
    {
        _kurallar.Add((method, contains, status, body));
        return this;
    }

    public (System.Net.HttpStatusCode Status, string Body) Coz(string method, string url)
    {
        foreach (var kural in _kurallar)
        {
            if (string.Equals(kural.Method, method, StringComparison.OrdinalIgnoreCase)
                && url.Contains(kural.Contains, StringComparison.OrdinalIgnoreCase))
            {
                return (kural.Status, kural.Body);
            }
        }

        return (VarsayilanDurum, VarsayilanGovde);
    }
}

internal sealed class SahteIstemciFabrikasi(SahteBulut bulut) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new SahteBulutIsleyici(bulut));
}

internal sealed class SahteBulutIsleyici(SahteBulut bulut) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string? govde = null;
        if (request.Content is not null)
        {
            // Govde GERCEKTEN okunur: tuketilmeyen bir akis, gercek sunucuda patlayan kodu
            // yesil gosterebilir.
            var bayt = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            govde = bayt.Length <= 4096 ? System.Text.Encoding.UTF8.GetString(bayt) : $"<{bayt.Length} bayt>";
        }

        var url = request.RequestUri!.ToString();

        bulut.Istekler.Enqueue(new SahteIstek(
            request.Method.Method,
            url,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("Dropbox-API-Arg", out var arg) ? string.Join(",", arg) : null,
            request.Content?.Headers.ContentRange?.ToString(),
            govde));

        if (bulut.AgHatasi?.Invoke(url) is { } hata)
        {
            throw hata;
        }

        var (durum, cevap) = bulut.Coz(request.Method.Method, url);
        return new HttpResponseMessage(durum)
        {
            RequestMessage = request,
            Content = new StringContent(cevap)
        };
    }
}

internal sealed class SahteTercihServisi(string yedekKoku) : IPanelPreferencesService
{
    public Task<PanelPreferencesDto> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new PanelPreferencesDto { DefaultBackupRoot = yedekKoku });

    public Task<PanelPreferencesDto> UpdateAsync(UpdatePanelPreferencesRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed class SahteSiteServisi(string uygulamaKoku) : IIisSiteService
{
    public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SiteDto
        {
            Name = siteName,
            PhysicalPath = uygulamaKoku,
            ApplicationRootPath = uygulamaKoku
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

/// <summary>
/// Sirlari TERSINE CEVRILEBILIR bicimde "korur". Gercek DPAPI yerine testte kullanilir; onemli
/// olan, kodun sirri KORUNMUS hâlde saklayip kullanim aninda cozdugunu olcmektir.
/// </summary>
internal sealed class SahteKoruyucu : ISecretProtector
{
    internal const string Onek = "korunmus:";

    public string Protect(string plaintext) => Onek + plaintext;

    public string Unprotect(string protectedValue) =>
        protectedValue.StartsWith(Onek, StringComparison.Ordinal)
            ? protectedValue[Onek.Length..]
            : throw new InvalidOperationException("Korunmus deger degil.");
}

/// <summary>Yazilan denetim kayitlarini toplar.</summary>
internal sealed class SahteDenetim : IAuditService
{
    public List<(string Action, string Resource, string Details)> Kayitlar { get; } = [];

    public Task WriteAsync(string action, string resource, string details, Guid? userId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        lock (Kayitlar)
        {
            Kayitlar.Add((action, resource, details));
        }

        return Task.CompletedTask;
    }

    public Task WriteForSubscriptionAsync(string action, string resource, string details, Guid? userId, Guid? subscriptionUserId, string? ipAddress, CancellationToken cancellationToken = default) =>
        WriteAsync(action, resource, details, userId, ipAddress, cancellationToken);

    public Task<IReadOnlyCollection<AuditLogDto>> GetRecentAsync(int take, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AuditLogPageDto> SearchAsync(AuditLogQuery query, AuditVisibility visibility, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public IAsyncEnumerable<AuditLogDto> StreamAsync(AuditLogQuery query, AuditVisibility visibility, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AuditVisibility> ResolveVisibilityAsync(Guid userId, UserRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AuditRetentionSettingsDto> GetRetentionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AuditRetentionSettingsDto> SetRetentionAsync(int retentionDays, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<int> PurgeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
