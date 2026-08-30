using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Git;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Git;

/// <summary>
/// Site uygulama köküne Git deposundan dağıtım yapar. İlk dağıtımda <c>git clone</c>,
/// sonraki dağıtımlarda <c>git fetch</c> + <c>git reset --hard</c> kullanır. Yapılandırma
/// <c>config/git/&lt;site&gt;.json</c> dosyasında tutulur.
/// </summary>
public sealed class GitDeploymentService(
    IIisSiteService iisSiteService,
    JsonFileStore fileStore,
    IConcurrencyLock concurrencyLock,
    ILogger<GitDeploymentService> logger,
    // İSTEĞE BAĞLI: "iç ağ hedeflerine izin ver" açık kapısı. Verilmemişse KAPALI sayılır.
    Microsoft.Extensions.Options.IOptions<Options.PanelRuntimeOptions>? runtimeOptions = null,
    // İSTEĞE BAĞLI: adres çözümleyici. Testte sahte — birim testi gerçek DNS'e çıkmamalı.
    OutboundAddressGuard.OutboundHostResolver? outboundResolver = null) : IGitDeploymentService
{
    /// <summary>
    /// Depo adresinin KONAĞI panelin ulaşmaması gereken bir yere mi çözülüyor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden <see cref="ValidateRepositoryUrl"/>'den AYRI:</b> o metot saf, senkron ve BİÇİM
    /// denetler (baştaki <c>-</c>, boşluk, <c>::</c> taşıyıcısı, şema beyaz listesi) — testleri de
    /// onu öyle ölçüyor. Konak denetimi DNS gerektirir, yani asenkron ve dış dünyaya bağımlıdır;
    /// aynı metoda katmak o metodun sınanabilirliğini bozardı.
    /// </para>
    /// <para>
    /// <b>Yüzey:</b> adres <c>git clone/fetch</c>'e verilir ve panel LocalSystem olarak koşar.
    /// <c>ValidateRepositoryUrl</c> hangi ŞEMA olduğuna bakıyordu ama NEREYE gittiğine bakmıyordu;
    /// <c>http://169.254.169.254/x.git</c> onun için kusursuz bir adrestir.
    /// </para>
    /// <para>
    /// <b>KAPSAM DÜRÜSTLÜĞÜ:</b> yalnız mutlak <c>http/https/ssh</c> adreslerinin konağı ölçülür.
    /// scp kalıbı (<c>git@host:owner/repo.git</c>) <c>Uri</c> ile ayrıştırılamaz; kendi elimizle
    /// konak çıkarmak, bu dosyanın zaten uyardığı "İKİNCİ ayrıştırıcı" hatasını doğururdu. O yol
    /// bugün denetimsiz kalıyor ve raporlandı.
    /// </para>
    /// </remarks>
    private async Task EnsureRepositoryHostAllowedAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out var adres) ||
            adres.Scheme is not ("http" or "https" or "ssh"))
        {
            return;
        }

        var engellenen = await OutboundAddressGuard.FindBlockedHostAsync(
            adres.DnsSafeHost,
            outboundResolver ?? OutboundAddressGuard.SystemResolver,
            OutboundTargetPolicy.Connecting(runtimeOptions?.Value.AllowInternalOutboundTargets ?? false),
            cancellationToken);

        if (engellenen is not null)
        {
            throw new AppException("git.repository_forbidden", 403, 403, engellenen);
        }
    }

    public async Task<GitDeploymentDto> GetAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var path = await ResolveSitePathAsync(siteName, cancellationToken);
        var record = await LoadAsync(siteName, cancellationToken);
        return ToDto(siteName, path, record);
    }

    public async Task<GitDeploymentDto> ConfigureAsync(string siteName, ConfigureGitDeploymentRequest request, CancellationToken cancellationToken = default)
    {
        var path = await ResolveSitePathAsync(siteName, cancellationToken);
        ValidateRepositoryUrl(request.RepositoryUrl);
        await EnsureRepositoryHostAllowedAsync(request.RepositoryUrl, cancellationToken);

        return await concurrencyLock.RunExclusiveAsync(async () =>
        {
            var record = await LoadAsync(siteName, cancellationToken);
            record.RepositoryUrl = request.RepositoryUrl.Trim();
            record.Branch = NormalizeBranch(request.Branch);
            if (request.RegenerateSecret || string.IsNullOrWhiteSpace(record.WebhookSecret))
            {
                record.WebhookSecret = GenerateSecret();
            }

            await SaveAsync(siteName, record, cancellationToken);
            return ToDto(siteName, path, record);
        }, cancellationToken);
    }

    public async Task<GitDeployResultDto> DeployAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var path = await ResolveSitePathAsync(siteName, cancellationToken);
        var record = await LoadAsync(siteName, cancellationToken);
        if (string.IsNullOrWhiteSpace(record.RepositoryUrl))
        {
            throw new AppException("git.not_configured", 409, 409);
        }

        return await concurrencyLock.RunExclusiveAsync(() => RunDeployAsync(siteName, path, record, cancellationToken), cancellationToken);
    }

    public async Task<GitDeployResultDto> DeployViaWebhookAsync(string siteName, string token, CancellationToken cancellationToken = default)
    {
        var path = await ResolveSitePathAsync(siteName, cancellationToken);
        var record = await LoadAsync(siteName, cancellationToken);
        if (string.IsNullOrWhiteSpace(record.RepositoryUrl) || string.IsNullOrWhiteSpace(record.WebhookSecret))
        {
            throw new AppException("git.not_configured", 409, 409);
        }

        if (!FixedTimeEquals(token, record.WebhookSecret))
        {
            throw new AppException("git.webhook_unauthorized", 401, 401);
        }

        return await concurrencyLock.RunExclusiveAsync(() => RunDeployAsync(siteName, path, record, cancellationToken), cancellationToken);
    }

    private async Task<GitDeployResultDto> RunDeployAsync(string siteName, string path, GitDeploymentRecord record, CancellationToken cancellationToken)
    {
        await EnsureGitAvailableAsync(cancellationToken);
        Directory.CreateDirectory(path);

        var isRepository = Directory.Exists(Path.Combine(path, ".git"));
        CommandResult result;

        if (!isRepository)
        {
            if (Directory.EnumerateFileSystemEntries(path).Any())
            {
                // Boş olmayan dizine clone başarısız olur; mevcut içeriği koruyup init+fetch yap.
                result = await InitAndFetchAsync(path, record, cancellationToken);
            }
            else
            {
                result = await RunGitAsync(
                    $"clone --branch {QuoteArg(record.Branch)} --single-branch {QuoteArg(record.RepositoryUrl)} .",
                    path,
                    cancellationToken);
            }
        }
        else
        {
            // DİKKAT: bu üç komutun sonuçları ayrı ayrı denetlenmeli. `reset --hard origin/<dal>`
            // YEREL remote-tracking ref'ini kullanır; önceki başarılı bir fetch'ten kalmış bir
            // `origin/<dal>` varsa fetch TAMAMEN başarısız olsa bile reset 0 ile çıkar. Yalnızca
            // reset'e bakmak, süresi dolmuş bir deploy token'ında her dağıtımın "başarılı" görünmesine
            // ve sitenin günlerce eski kodda kalmasına yol açıyordu.
            var setUrl = await RunGitAsync($"remote set-url origin {QuoteArg(record.RepositoryUrl)}", path, cancellationToken);
            if (!setUrl.Success)
            {
                // Depo adresi güncellenemediyse sonraki adımlar ESKİ depodan çeker; "dağıtıldı" demek yanıltıcı olur.
                result = setUrl;
            }
            else
            {
                var fetch = await RunGitAsync($"fetch origin {QuoteArg(record.Branch)} --prune", path, cancellationToken);
                result = !fetch.Success
                    ? fetch
                    : await RunGitAsync($"reset --hard {QuoteArg($"origin/{record.Branch}")}", path, cancellationToken);
            }
        }

        var commit = await GetCurrentCommitAsync(path, cancellationToken);
        var success = result.Success;

        record.LastCommit = commit;
        record.LastDeployUtc = DateTime.UtcNow;
        record.LastDeployStatus = success ? "succeeded" : "failed";
        await SaveAsync(siteName, record, cancellationToken);

        if (!success)
        {
            logger.LogWarning("Git dağıtımı başarısız: {SiteName} -> {Error}", siteName, result.StandardError);
        }

        return new GitDeployResultDto
        {
            Success = success,
            Commit = commit,
            Output = CombineOutput(result),
            DeployedAtUtc = record.LastDeployUtc.Value,
            Deployment = ToDto(siteName, path, record)
        };
    }

    private static async Task<CommandResult> InitAndFetchAsync(string path, GitDeploymentRecord record, CancellationToken cancellationToken)
    {
        await RunGitAsync("init", path, cancellationToken);
        await RunGitAsync($"remote add origin {QuoteArg(record.RepositoryUrl)}", path, cancellationToken);
        await RunGitAsync($"fetch origin {QuoteArg(record.Branch)} --prune", path, cancellationToken);
        await RunGitAsync($"checkout -f -B {QuoteArg(record.Branch)} {QuoteArg($"origin/{record.Branch}")}", path, cancellationToken);
        return await RunGitAsync($"reset --hard {QuoteArg($"origin/{record.Branch}")}", path, cancellationToken);
    }

    private static async Task<string> GetCurrentCommitAsync(string path, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync("rev-parse --short HEAD", path, cancellationToken);
        return result.Success ? result.StandardOutput.Trim() : string.Empty;
    }

    private static async Task EnsureGitAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunGitAsync("--version", null, cancellationToken);
            if (!result.Success)
            {
                throw new AppException("git.not_available", 409, 409);
            }
        }
        catch (Exception exception) when (exception is not AppException)
        {
            throw new AppException("git.not_available", 409, 409, exception.Message);
        }
    }

    private async Task<string> ResolveSitePathAsync(string siteName, CancellationToken cancellationToken)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var path = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new AppException("git.path_not_found", 404, 404);
        }

        return path;
    }

    private async Task<GitDeploymentRecord> LoadAsync(string siteName, CancellationToken cancellationToken)
    {
        return await fileStore.ReadAsync(ConfigPath(siteName), new GitDeploymentRecord(), cancellationToken);
    }

    private async Task SaveAsync(string siteName, GitDeploymentRecord record, CancellationToken cancellationToken)
    {
        await fileStore.WriteAsync(ConfigPath(siteName), record, cancellationToken);
    }

    // AYRILMIŞ AYGIT ADI kontrolü UZANTI EKLENDİKTEN SONRA yapılır: ayrılmışlık ilk noktadan
    // önceki parçaya bakar, yani "CON" için hedef "CON.json" olur ve o da aygıttır. Kontrolsüz
    // bırakıldığında "CON" adlı bir sitenin Git ayarları KAYDEDİLEMİYORDU (JsonFileStore'un atomik
    // yazımı FileNotFoundException veriyor) — dağıtım kurulmuş görünüp hiç çalışmazdı.
    //
    // internal: bkz. SeoReportStore.Path — üretilen adın diskte açılabildiği ÖLÇÜLEBİLSİN diye.
    internal static string ConfigPath(string siteName) =>
        $"config/git/{WindowsReservedNames.MakeSafe($"{SanitizeFileName(siteName)}.json")}";

    private static GitDeploymentDto ToDto(string siteName, string path, GitDeploymentRecord record)
    {
        return new GitDeploymentDto
        {
            SiteName = siteName,
            RepositoryUrl = record.RepositoryUrl,
            Branch = string.IsNullOrWhiteSpace(record.Branch) ? "main" : record.Branch,
            Configured = !string.IsNullOrWhiteSpace(record.RepositoryUrl),
            DeployPath = path,
            LastCommit = record.LastCommit,
            LastDeployUtc = record.LastDeployUtc,
            LastDeployStatus = record.LastDeployStatus,
            WebhookPath = $"/api/v1/git/{Uri.EscapeDataString(siteName)}/webhook",
            WebhookSecret = record.WebhookSecret
        };
    }

    /// <summary>
    /// Depo adresi doğrulaması.
    /// <para>
    /// ESKİ HÂLİ SYSTEM OLARAK KOD ÇALIŞTIRMAYA AÇIKTI. Tek kural "http/https ya da içinde
    /// <c>@</c> VE <c>:</c> geçen bir şey" idi. <c>ext::sh -c "..."</c> her iki koşulu da
    /// sağlayabiliyordu ve git'in <b>uzantı taşıyıcısı</b> (transport helper) tam olarak
    /// "bu komutu çalıştır" demektir — panel LocalSystem olduğu için doğrudan SYSTEM kabuğu.
    /// Ayrıca <c>-</c> ile başlayan bir adres git için ADRES değil BAYRAKtır
    /// (<c>--upload-pack=...</c>, <c>--config=...</c>); tırnaklamak bunu KAPATMAZ, çünkü tırnağı
    /// git görmeden önce argüman ayrıştırıcısı soyar. Aynı dosyada dal adı için bu iki kontrol
    /// zaten yapılıyordu (bkz. <see cref="NormalizeBranch"/>); URL payını almamıştı.
    /// </para>
    /// </summary>
    internal static void ValidateRepositoryUrl(string url)
    {
        var trimmed = url.Trim();
        if (trimmed.Length is 0 or > 2048)
        {
            throw new AppException("git.invalid_repository", 400, 400);
        }

        // Bayrağa benzeyen adres.
        if (trimmed[0] == '-')
        {
            throw new AppException("git.invalid_repository", 400, 400);
        }

        // İç boşluk/denetim karakteri: hem ikinci bir argüman doğurur hem de meşru bir git
        // adresinde hiç bulunmaz.
        foreach (var karakter in trimmed)
        {
            if (char.IsWhiteSpace(karakter) || char.IsControl(karakter))
            {
                throw new AppException("git.invalid_repository", 400, 400);
            }
        }

        // TAŞIYICI UZANTISI (`ext::`, `transport::`). `::` yalnız ilk `/` ÖNCESİNDE aranıyor:
        // yol/sorgu kısmındaki iki nokta üst üste zararsızdır ve IPv6 gövdeli adresleri kırmayalım.
        //
        // DÜRÜSTLÜK NOTU — BU KONTROL BUGÜN GEREKSİZ. Mutasyonla ölçüldü: kapatıldığında hiçbir
        // test kırmızıya dönmüyor, çünkü aşağıdaki biçim beyaz listesi `ext::…` biçimlerinin
        // hepsini zaten reddediyor (scp kalıbında kullanıcı kısmı `:` içeremez). Bilerek
        // bırakılıyor: ileride scp kalıbı genişletilirse taşıyıcı reddi ayakta kalsın. Asıl ve
        // ÖLÇÜLEBİLİR ikinci katman bu değil, RunGitAsync'teki GIT_ALLOW_PROTOCOL'dür.
        var ilkEgikCizgi = trimmed.IndexOf('/');
        var onEk = ilkEgikCizgi < 0 ? trimmed : trimmed[..ilkEgikCizgi];
        if (onEk.Contains("::", StringComparison.Ordinal))
        {
            throw new AppException("git.invalid_repository", 400, 400);
        }

        var mutlak = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri);
        var isHttp = mutlak && uri!.Scheme is "http" or "https";
        var isSsh = mutlak && uri!.Scheme is "ssh";

        // scp benzeri: git@host:owner/repo.git. Eski "içinde @ ve : var" kontrolünün aksine
        // gerçekten BİÇİM ölçülüyor.
        var isScpLike = Regex.IsMatch(trimmed, @"^[A-Za-z0-9._-]+@[A-Za-z0-9._-]+(?::[0-9]+)?:[A-Za-z0-9._/~-]+$");

        if (!isHttp && !isSsh && !isScpLike)
        {
            throw new AppException("git.invalid_repository", 400, 400);
        }
    }

    // Dal adı git komutlarına geçiyor; boşluk/'--flag'/tırnak içeren bir ad argüman enjeksiyonuna
    // (ör. git --upload-pack=...) yol açabilir. Güvenli git dal adı kalıbına zorla (boşsa "main").
    private static string NormalizeBranch(string? value)
    {
        var branch = string.IsNullOrWhiteSpace(value) ? "main" : value.Trim();
        if (branch.Length > 200 ||
            branch.StartsWith('-') ||
            branch.Contains("..", StringComparison.Ordinal) ||
            !Regex.IsMatch(branch, "^[A-Za-z0-9][A-Za-z0-9._/-]*$"))
        {
            throw new AppException("git.invalid_branch", 400, 400);
        }

        return branch;
    }

    private static string GenerateSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private static bool FixedTimeEquals(string a, string b)
    {
        var bytesA = System.Text.Encoding.UTF8.GetBytes(a ?? string.Empty);
        var bytesB = System.Text.Encoding.UTF8.GetBytes(b ?? string.Empty);
        return bytesA.Length == bytesB.Length && CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }

    private static string CombineOutput(CommandResult result) =>
        string.Join(Environment.NewLine, new[] { result.StandardOutput, result.StandardError }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    /// <summary>
    /// git komutlarını KİMLİK İSTEMİ KAPALI çalıştırır.
    /// <para>
    /// Kimlik gerektiren bir HTTPS remote'ta git, terminal istemi ya da Git Credential Manager
    /// penceresi açmaya çalışır. Servis oturumunda bunu yanıtlayacak kimse yoktur; süreç
    /// ProcessRunner'ın 10 dakikalık tavanına kadar asılı kalır. Dağıtım GLOBAL KİLİDİN İÇİNDE
    /// çalıştığı için bu süre boyunca panelde hiç kimse giriş yapamaz, hiçbir değişiklik kaydedilemez.
    /// Bu değişkenlerle git, "kimlik doğrulama gerekli" diyerek HEMEN hata verir.
    /// </para>
    /// </summary>
    private static Task<CommandResult> RunGitAsync(string arguments, string? workingDirectory, CancellationToken cancellationToken)
        => ProcessRunner.RunAsync(
            "git",
            arguments,
            workingDirectory,
            cancellationToken,
            new Dictionary<string, string>
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GCM_INTERACTIVE"] = "never",
                ["GIT_ASKPASS"] = "echo",

                // İKİNCİ VE BAĞIMSIZ KATMAN: git'in KENDİ taşıyıcı beyaz listesi. ValidateRepositoryUrl
                // `ext::` biçimini reddediyor, ama o doğrulama yalnız ConfigureAsync'te çalışıyor —
                // diskteki config/git/<site>.json bir kez kötü bir değerle yazıldıysa DeployAsync onu
                // yeniden ölçmüyor. Bu değişken kararı git'in transport katmanına taşır: listede
                // olmayan hiçbir taşıyıcı (ext, file, ftp…) çalıştırılmaz.
                ["GIT_ALLOW_PROTOCOL"] = "http:https:ssh"
            });

    private static string QuoteArg(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private sealed class GitDeploymentRecord
    {
        public string RepositoryUrl { get; set; } = string.Empty;
        public string Branch { get; set; } = "main";
        public string WebhookSecret { get; set; } = string.Empty;
        public string LastCommit { get; set; } = string.Empty;
        public DateTime? LastDeployUtc { get; set; }
        public string LastDeployStatus { get; set; } = string.Empty;
    }
}
