using System.Text.Json;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Backup;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Mail;

/// <inheritdoc cref="IMailAccountArchiver" />
/// <remarks>
/// <para>
/// <b>Araç: <c>vandelay</c> (Stalwart'ın resmî içe/dışa aktarıcısı).</b> Hesap başına, kendi kendine
/// yeten tek bir SQLite arşivi üretir; sunucu ÇALIŞIRKEN çalışır ve geri yükleme YALNIZ hedef
/// hesaba yazar. Elenen yollar (ölçüldü, tahmin değil):
/// </para>
/// <list type="bullet">
///   <item><description>
///   <c>stalwart --export</c>: alan adı başına DEĞİL — deponun TAMAMINI ham anahtar-değer dökümü
///   olarak yazar (<c>subspace_a…subspace_x</c>). Tek müşteriyi ayırmak mümkün değil.
///   </description></item>
///   <item><description>
///   <c>stalwart-cli</c>: şemasında İLETİ yok; yalnız yapılandırma nesneleri (Account, Domain…).
///   </description></item>
///   <item><description>
///   Hesap başına app-password üretip IMAP: sunucu reddediyor —
///   <c>Secondary credentials cannot be set directly</c>. Mevcut parolayı geri yazmak da imkânsız
///   (sunucu <c>secret</c> alanını maskeli döner). Yani müşteri parolasını BOZMADAN bu yol kapalı.
///   </description></item>
/// </list>
/// <para>
/// <b>Kimlik:</b> panel müşteri parolalarını bilmez ve öğrenmesi de gerekmez — bağlantı YÖNETİCİ
/// kimliğiyle kurulur, hedef hesap <c>--account-name</c> ile seçilir (Stalwart <c>impersonate</c>).
/// Parola <c>VANDELAY_PASSWORD</c> ORTAM DEĞİŞKENİYLE geçirilir, komut satırıyla DEĞİL: komut satırı
/// süreç tablosunda ve zamanlanmış görev günlüklerinde görünür.
/// </para>
/// </remarks>
internal sealed class StalwartAccountArchiver : IMailAccountArchiver
{
    /// <summary>Aracın bileşen kimliği ve varsayılan kurulum kökü.</summary>
    private const string ToolComponentId = "vandelay";
    private const string ToolExecutable = "vandelay.exe";
    private const string ToolDefaultRoot = "%ProgramData%\\AdaPanel\\runtimes\\vandelay";

    /// <summary>Stalwart'ın loopback ucu — reklam edilen adresi BURADAN okuruz.</summary>
    private const string LocalSessionUrl = "http://localhost:8080/.well-known/jmap";

    /// <summary>
    /// Tek hesap için üst sınır. Sonsuz bekleme kabul edilemez: bu akış gecelik yedeğin içinde,
    /// global kilidin altında çalışıyor.
    /// </summary>
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(30);

    private readonly StalwartRuntime runtime;
    private readonly IMailService mailService;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILogger<StalwartAccountArchiver>? logger;

    /// <summary>Süreç çalıştırma SEAM'i — testler gerçek vandelay olmadan akışı ölçebilsin diye.</summary>
    internal Func<string, string, IReadOnlyDictionary<string, string>, CancellationToken, Task<CommandResult>> RunTool { get; set; }

    /// <summary>Aracın YERİ için seam (testte sahte bir yol verilir).</summary>
    internal Func<CancellationToken, Task<string?>> ResolveTool { get; set; }

    public StalwartAccountArchiver(
        StalwartRuntime runtime,
        IMailService mailService,
        IHttpClientFactory httpClientFactory,
        ILogger<StalwartAccountArchiver>? logger = null)
    {
        this.runtime = runtime;
        this.mailService = mailService;
        this.httpClientFactory = httpClientFactory;
        this.logger = logger;

        RunTool = (path, args, env, ct) =>
            ProcessRunner.RunAsync(path, args, cancellationToken: ct, environmentVariables: env, timeout: ToolTimeout);
        ResolveTool = ct => runtime.ResolveExecutableAsync(ToolComponentId, ToolExecutable, ToolDefaultRoot, ct);
    }

    // =====================================================================================
    // ÖN KONTROL
    // =====================================================================================

    /// <inheritdoc />
    public async Task<MailArchivePreflightDto> CheckAsync(CancellationToken cancellationToken = default)
    {
        var tool = await ResolveTool(cancellationToken);
        if (string.IsNullOrWhiteSpace(tool))
        {
            return Fail(string.Empty, string.Empty,
                "vandelay aracı kurulu değil; posta iletileri arşivlenemez. Bileşenler ekranından 'vandelay' kurulmalı.");
        }

        var password = await runtime.GetAdminPasswordAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(password))
        {
            return Fail(string.Empty, tool,
                "Stalwart yönetici parolası çözülemedi; posta iletileri arşivlenemez.");
        }

        // REKLAM EDİLEN ADRESİ SUNUCUNUN KENDİSİNDEN OKU — tahmin etme.
        //
        // vandelay, JMAP oturumunun reklam ettiği adrese gitmek ZORUNDA (ölçüldü: --url'e tam
        // oturum adresi vermek bile bunu değiştirmiyor). Bu yüzden ön kontrol, panelin varsaydığı
        // bir adresi değil, sunucunun GERÇEKTEN duyurduğu adresi yoklar. Aksi hâlde "yeşil ön
        // kontrol + patlayan yedek" üretirdik.
        string advertised;
        try
        {
            advertised = await runtime.GetAdvertisedApiUrlAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            return Fail(string.Empty, tool,
                "Stalwart JMAP oturumu yerel uçtan okunamadı: " + exception.Message);
        }

        if (string.IsNullOrWhiteSpace(advertised))
        {
            return Fail(string.Empty, tool,
                "Stalwart JMAP oturumu bir apiUrl duyurmadı; vandelay'in gideceği adres belirlenemedi.");
        }

        // Duyurulan adres GERÇEKTEN ulaşılabiliyor mu? Bu, IIS'teki JMAP vekilinin ayakta olup
        // olmadığının tek dürüst ölçümüdür (8080 loopback'te kaldığı için 443'ten vekillenmeli).
        var sessionUrl = StalwartRuntime.SessionUrlOf(advertised);
        var (reachable, detail) = await ProbeAsync(sessionUrl, password, cancellationToken);
        if (!reachable)
        {
            return Fail(sessionUrl, tool,
                $"Stalwart'ın duyurduğu JMAP adresi ({sessionUrl}) bu sunucudan ulaşılamıyor: {detail}. " +
                "IIS üzerinde JMAP vekili yayınlanmamış olabilir; posta iletileri arşive KONMAYACAK.");
        }

        return new MailArchivePreflightDto
        {
            IsReady = true,
            SessionUrl = sessionUrl,
            ToolPath = tool,
            Reason = string.Empty
        };
    }

    private static MailArchivePreflightDto Fail(string sessionUrl, string tool, string reason)
        => new() { IsReady = false, Reason = reason, SessionUrl = sessionUrl, ToolPath = tool };

    /// <summary>
    /// Adres ulaşılabiliyor mu? HTTP KODU ÖNEMLİ DEĞİL — 401 bile "ulaşıldı" demektir; ölçtüğümüz
    /// şey ağ/vekil yolunun açık olup olmadığı. Ad çözülemezse ya da bağlantı kurulamazsa hayır.
    /// </summary>
    private async Task<(bool Reachable, string Detail)> ProbeAsync(string url, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return (false, "adres çözülemedi");
        }

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = StalwartRuntime.BasicHeader(password);
            using var response = await client.SendAsync(request, cancellationToken);
            return ((int)response.StatusCode < 500, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
    }

    // =====================================================================================
    // ARŞİVLEME
    // =====================================================================================

    /// <inheritdoc />
    public async Task<MailArchiveResultDto> ArchiveDomainAsync(
        string domain,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var alan = (domain ?? string.Empty).Trim().ToLowerInvariant();
        var preflight = await CheckAsync(cancellationToken);
        if (!preflight.IsReady)
        {
            return new MailArchiveResultDto { Failures = [preflight.Reason] };
        }

        var password = await runtime.GetAdminPasswordAsync(cancellationToken) ?? string.Empty;
        var accounts = await ListAccountsAsync(alan, cancellationToken);
        if (accounts.Count == 0)
        {
            // Hesabı olmayan alan adı bir HATA DEĞİLDİR ama "eksiksiz arşivlendi" de değildir:
            // IsComplete false kalır ve arşiv "posta dahil" demez.
            return new MailArchiveResultDto();
        }

        Directory.CreateDirectory(outputDirectory);

        var basarili = new List<MailAccountArchiveDto>();
        var hatalar = new List<string>();

        foreach (var address in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dosya = Path.Combine(outputDirectory, BackupPathName.Sanitize(address, "hesap") + ".sqlite");
            var args =
                $"import jmap --url {Arg(preflight.SessionUrl)} " +
                $"--auth-basic {Arg(StalwartRuntime.AdminUser)} " +
                $"--account-name {Arg(address)} --quiet {Arg(dosya)}";

            var result = await RunTool(preflight.ToolPath, args, BuildEnvironment(password), cancellationToken);
            if (result.ExitCode == 0 && File.Exists(dosya))
            {
                basarili.Add(new MailAccountArchiveDto { Address = address, FilePath = dosya });
                continue;
            }

            // HATA YUTULMAZ. Tek bir hesap düşse bile alan adı "eksiksiz" sayılmaz; sessiz bir
            // catch, kullanıcının olmayan bir yedeğe güvenmesi demekti.
            var teshis = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            hatalar.Add($"{address}: {Kisalt(teshis)}");
            logger?.LogWarning("Posta hesabı arşivlenemedi ({Address}): {Error}", address, teshis);
        }

        return new MailArchiveResultDto { Accounts = basarili, Failures = hatalar };
    }

    // =====================================================================================
    // GERİ YÜKLEME
    // =====================================================================================

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> RestoreDomainAsync(
        string domain,
        string archiveDirectory,
        CancellationToken cancellationToken = default)
    {
        var alan = (domain ?? string.Empty).Trim().ToLowerInvariant();
        if (!Directory.Exists(archiveDirectory))
        {
            return ["Posta arşivi klasörü yok: " + archiveDirectory];
        }

        var preflight = await CheckAsync(cancellationToken);
        if (!preflight.IsReady)
        {
            return [preflight.Reason];
        }

        var password = await runtime.GetAdminPasswordAsync(cancellationToken) ?? string.Empty;
        var hatalar = new List<string>();

        foreach (var dosya in Directory.EnumerateFiles(archiveDirectory, "*.sqlite"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Hedef hesap DOSYA ADINDAN gelir; arşiv hangi hesabınsa oraya yazılır.
            var address = Path.GetFileNameWithoutExtension(dosya);
            if (!BelongsToDomain(address, alan))
            {
                hatalar.Add($"{address}: '{alan}' alan adına ait değil, atlandı.");
                continue;
            }

            var args =
                $"export --url {Arg(preflight.SessionUrl)} " +
                $"--auth-basic {Arg(StalwartRuntime.AdminUser)} " +
                $"--account-name {Arg(address)} --quiet {Arg(dosya)}";

            var result = await RunTool(preflight.ToolPath, args, BuildEnvironment(password), cancellationToken);
            if (result.ExitCode != 0)
            {
                var teshis = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
                hatalar.Add($"{address}: {Kisalt(teshis)}");
                logger?.LogWarning("Posta hesabı geri yüklenemedi ({Address}): {Error}", address, teshis);
            }
        }

        return hatalar;
    }

    // =====================================================================================
    // Yardımcılar
    // =====================================================================================

    /// <summary>
    /// Bu hesap arşivi GERÇEKTEN bu alan adına mı ait? Geri yüklemenin izolasyon KAPISI budur.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Geri yükleme TEK alan adını yazmalı. Arşive (elle düzenlenmiş bir zip'le ya da bir hatayla)
    /// yabancı bir hesap dosyası karışmışsa onu sunucuya uygulamak, dokunulmaması gereken BAŞKA bir
    /// müşterinin posta kutusunu ezmek olurdu.
    /// </para>
    /// <para>
    /// Kontrol büyük/küçük harf duyarsızdır ve <b>tam</b> alan adı eşleşmesi arar: "@alan" son eki
    /// yeterli DEĞİLDİR — <c>kutu@kotu-ornek.com</c> adresi <c>ornek.com</c> ile bitmediği için
    /// zaten düşer, ama <c>kutu@alt.ornek.com</c> gibi bir ALT alan da <c>ornek.com</c> sayılmamalı.
    /// </para>
    /// </remarks>
    internal static bool BelongsToDomain(string address, string domain)
    {
        var adres = (address ?? string.Empty).Trim();
        var alan = (domain ?? string.Empty).Trim();
        if (adres.Length == 0 || alan.Length == 0)
        {
            return false;
        }

        var at = adres.LastIndexOf('@');
        if (at <= 0 || at == adres.Length - 1)
        {
            return false;
        }

        return string.Equals(adres[(at + 1)..], alan, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<string>> ListAccountsAsync(string domain, CancellationToken cancellationToken)
    {
        try
        {
            var workspace = await mailService.GetWorkspaceAsync(domain, cancellationToken);
            // Alan adı süzgeci ARŞİVLEMEDE de aynı kapıdan geçer: çalışma alanı beklenmedik bir
            // hesap döndürürse onu bu alanın yedeğine koymak, başka bir müşterinin postasını bu
            // sitenin arşivine sızdırmak olurdu.
            return [.. workspace.Accounts
                .Select(account => (account.Address ?? string.Empty).Trim().ToLowerInvariant())
                .Where(address => BelongsToDomain(address, domain))];
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Posta hesapları listelenemedi: {Domain}", domain);
            return [];
        }
    }

    /// <summary>
    /// Parola ORTAM DEĞİŞKENİYLE geçer. Komut satırına yazmak, parolayı süreç tablosuna ve
    /// zamanlanmış görev günlüklerine düşürürdü — vandelay bu yüzden bu değişkeni öneriyor.
    /// </summary>
    private static Dictionary<string, string> BuildEnvironment(string password) => new()
    {
        ["VANDELAY_PASSWORD"] = password
    };

    private static string Arg(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static string Kisalt(string value)
    {
        var temiz = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        return temiz.Length <= 300 ? temiz : temiz[..300] + "…";
    }
}
