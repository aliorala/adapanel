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
    OutboundAddressGuard.OutboundHostResolver? outboundResolver = null,
    // İSTEĞE BAĞLI: DENETLENEN giden istemci (yönlendirme zincirini panel çözsün diye). Verilmezse
    // ön çözümleme yapılmaz; git yine yönlendirmesiz koşar (bkz. RunGitAsync).
    IHttpClientFactory? httpClientFactory = null) : IGitDeploymentService
{
    internal Func<string, string?, CancellationToken, Task<CommandResult>>? CommandRunner { get; init; }
    private string DeploymentKey(string siteName) => "git:" + fileStore.ResolvePath(ConfigPath(siteName)).ToUpperInvariant();
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
    /// <b>KAPATILAN AÇIK — scp kalıbı KAPIYA HİÇ UĞRAMIYORDU.</b> Burada eskiden doğrudan
    /// <c>Uri.TryCreate</c> vardı ve ayrıştıramadığı adreste <c>return</c> ediyordu. Ölçüldü:
    /// <c>git@169.254.169.254:latest/meta-data</c> <c>Uri</c> ile ayrıştırılamaz, yani kapı o
    /// adresi HİÇ GÖRMÜYORDU — koruma zayıf değil SIFIRDI. Konak çıkarımı artık
    /// <see cref="GitRepositoryAddress.TryGetHost"/>'ta ve o, biçim beyaz listesiyle AYNI deseni
    /// kullanıyor. Ayrıştırılamayan adres artık "temiz" değil <b>REDDEDİLİR</b>: "anlamadım"
    /// bir izin gerekçesi değildir.
    /// </para>
    /// </remarks>
    private async Task EnsureRepositoryHostAllowedAsync(string url, CancellationToken cancellationToken)
    {
        var engellenen = await FindBlockedRepositoryHostAsync(
            url,
            outboundResolver ?? OutboundAddressGuard.SystemResolver,
            OutboundTargetPolicy.Connecting(runtimeOptions?.Value.AllowInternalOutboundTargets ?? false),
            cancellationToken);

        if (engellenen is not null)
        {
            throw new AppException("git.repository_forbidden", 403, 403, engellenen);
        }
    }

    /// <summary>
    /// Kapı gövdesi; engelli hedefi (ya da <c>unparsable_host</c>) döner, temizse <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <b>internal ve static:</b> gövde böylece sahte bir çözümleyiciyle, IIS'e/diske hiç
    /// dokunmadan ölçülebiliyor. Ölçülen şey üretimdeki gövdenin ta kendisidir — çağrı yerleri
    /// (kaydetme VE dağıtım) bunu çağırır.
    /// </remarks>
    internal static async Task<string?> FindBlockedRepositoryHostAsync(
        string url,
        OutboundAddressGuard.OutboundHostResolver resolver,
        OutboundTargetPolicy policy,
        CancellationToken cancellationToken)
    {
        if (!GitRepositoryAddress.TryGetHost(url, out var konak))
        {
            // AÇIK KAPI BURADA DA GEÇERLİ: işletici iç ağ hedeflerine bilerek izin verdiyse,
            // ayrıştıramadığımız bir adres yüzünden meşru bir kurulumu kırmayız — ama kapı
            // kapalıyken "anlamadım" mutlaka REDDETTİR (eski kod burada GEÇİYORDU).
            return policy.AllowInternalTargets ? null : "unparsable_host";
        }

        return await OutboundAddressGuard.FindBlockedHostAsync(konak, resolver, policy, cancellationToken);
    }

    public async Task<GitDeploymentDto> GetAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var path = await ResolveSitePathAsync(siteName, cancellationToken);
        var record = await LoadAsync(siteName, cancellationToken);
        return ToDto(siteName, path, record);
    }

    public async Task<GitDeploymentDto> ConfigureAsync(string siteName, ConfigureGitDeploymentRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRepositoryUrl(request.RepositoryUrl);
        await EnsureRepositoryHostAllowedAsync(request.RepositoryUrl, cancellationToken);

        return await concurrencyLock.RunExclusiveAsync(DeploymentKey(siteName), async () =>
        {
            var path = await ResolveSitePathAsync(siteName, cancellationToken);
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
        return await concurrencyLock.RunExclusiveAsync(DeploymentKey(siteName), async () =>
        {
            var path = await ResolveSitePathAsync(siteName, cancellationToken);
            var record = await LoadAsync(siteName, cancellationToken);
            if (string.IsNullOrWhiteSpace(record.RepositoryUrl)) throw new AppException("git.not_configured", 409, 409);
            return await RunDeployAsync(siteName, path, record, cancellationToken);
        }, cancellationToken);
    }

    public async Task<GitDeployResultDto> DeployViaWebhookAsync(string siteName, string token, CancellationToken cancellationToken = default)
    {
        return await concurrencyLock.RunExclusiveAsync(DeploymentKey(siteName), async () =>
        {
            var path = await ResolveSitePathAsync(siteName, cancellationToken);
            var record = await LoadAsync(siteName, cancellationToken);
            if (string.IsNullOrWhiteSpace(record.RepositoryUrl) || string.IsNullOrWhiteSpace(record.WebhookSecret))
                throw new AppException("git.not_configured", 409, 409);
            if (!FixedTimeEquals(token, record.WebhookSecret)) throw new AppException("git.webhook_unauthorized", 401, 401);
            return await RunDeployAsync(siteName, path, record, cancellationToken);
        }, cancellationToken);
    }

    private async Task<GitDeployResultDto> RunDeployAsync(string siteName, string path, GitDeploymentRecord record, CancellationToken cancellationToken)
    {
        // DENETİM ARTIK DAĞITIM ANINDA DA YAPILIYOR.
        //
        // Eskiden kapı YALNIZ ConfigureAsync'te çalışıyordu. İki sonucu ölçülebilir biçimde
        // yanlıştı: (1) diskteki config/git/<site>.json bir kez kötü bir değerle yazıldıysa
        // (eski sürüm, elle düzenleme, yedekten geri dönüş) dağıtım onu bir daha ölçmüyordu;
        // (2) kaydetme ile dağıtım arasında A kaydı değişebilir — saldırgan depoyu meşru bir
        // adresle kurup kaydı sonradan 169.254.169.254'e çevirebilirdi (DNS rebinding). Webhook
        // ucu ANONİM olduğu için ikinci senaryonun tetiklenmesi de saldırganın elindedir.
        await EnsureRepositoryHostAllowedAsync(record.RepositoryUrl, cancellationToken);

        // YÖNLENDİRME ZİNCİRİNİ PANEL ÇÖZER, git değil (gerekçe: ResolveHttpRepositoryUrlAsync).
        var adres = await ResolveHttpRepositoryUrlAsync(record.RepositoryUrl, cancellationToken);

        await EnsureGitAvailableAsync(cancellationToken);
        Directory.CreateDirectory(path);

        // G6 (tur 2): git dizini KİRACININ yazabildiği site kökünde DEĞİL, panele ait yerdedir
        // (<DataRoot>\git\<site>.git, SYSTEM+Administrators). Kiracının .git/config'i (filter.*.smudge,
        // core.worktree, http.cookieFile ...) ve .gitattributes sürücüleri artık SYSTEM'e ulaşamaz.
        var gitDir = GitDirFor(siteName);
        var locationWarning = await EnsureRepositoryLocationAsync(path, gitDir, cancellationToken);

        var isRepository = File.Exists(Path.Combine(gitDir, "HEAD"));
        CommandResult result;

        if (!isRepository)
        {
            // Boş ya da dolu dizin: aynı yol (init + fetch + checkout). `clone` site köküne .git yazardı.
            result = await InitAndFetchAsync(path, gitDir, adres, record.Branch, cancellationToken);
        }
        else
        {
            // DİKKAT: bu üç komutun sonuçları ayrı ayrı denetlenmeli. `reset --hard origin/<dal>`
            // YEREL remote-tracking ref'ini kullanır; önceki başarılı bir fetch'ten kalmış bir
            // `origin/<dal>` varsa fetch TAMAMEN başarısız olsa bile reset 0 ile çıkar. Yalnızca
            // reset'e bakmak, süresi dolmuş bir deploy token'ında her dağıtımın "başarılı" görünmesine
            // ve sitenin günlerce eski kodda kalmasına yol açıyordu.
            var setUrl = await ExecuteGitAsync($"remote set-url origin {QuoteArg(adres)}", path, cancellationToken, gitDir);
            if (!setUrl.Success)
            {
                // Depo adresi güncellenemediyse sonraki adımlar ESKİ depodan çeker; "dağıtıldı" demek yanıltıcı olur.
                result = setUrl;
            }
            else
            {
                var fetch = await ExecuteGitAsync($"fetch origin {QuoteArg(record.Branch)} --prune", path, cancellationToken, gitDir);
                result = !fetch.Success
                    ? fetch
                    : await ExecuteGitAsync($"reset --hard {QuoteArg($"origin/{record.Branch}")}", path, cancellationToken, gitDir);
            }
        }

        var commit = await GetCurrentCommitAsync(path, gitDir, cancellationToken);
        var success = result.Success;

        record.LastCommit = commit;
        record.LastDeployUtc = DateTime.UtcNow;
        record.LastDeployStatus = success ? "succeeded" : "failed";
        using (var publication = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            await fileStore.UpdateAsync(ConfigPath(siteName), record, current =>
            {
                current.LastCommit = record.LastCommit;
                current.LastDeployUtc = record.LastDeployUtc;
                current.LastDeployStatus = record.LastDeployStatus;
                return current;
            }, publication.Token);

        if (!success)
        {
            logger.LogWarning("Git dağıtımı başarısız: {SiteName} -> {Error}", siteName, result.StandardError);
        }

        return new GitDeployResultDto
        {
            Success = success,
            Commit = commit,
            Output = string.Join(Environment.NewLine, new[] { CombineOutput(result), locationWarning ?? string.Empty }.Where(value => !string.IsNullOrWhiteSpace(value))),
            DeployedAtUtc = record.LastDeployUtc.Value,
            Deployment = ToDto(siteName, path, record)
        };
    }

    private async Task<CommandResult> InitAndFetchAsync(string path, string gitDir, string adres, string branch, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(gitDir)!);

        // init: --work-tree VERİLMEZ (verilirse git core.worktree yazar; izin listesi onu reddeder).
        var init = await ExecuteGitAsync("init", path, cancellationToken, gitDir, useWorkTree: false);
        if (!init.Success) return init;

        string[] stages = [$"remote add origin {QuoteArg(adres)}",
            $"fetch origin {QuoteArg(branch)} --prune", $"checkout -f -B {QuoteArg(branch)} {QuoteArg($"origin/{branch}")}"];
        foreach (var stage in stages)
        {
            var result = await ExecuteGitAsync(stage, path, cancellationToken, gitDir);
            if (!result.Success) return result;
        }
        return await ExecuteGitAsync($"reset --hard {QuoteArg($"origin/{branch}")}", path, cancellationToken, gitDir);
    }

    private async Task<string> GetCurrentCommitAsync(string path, string gitDir, CancellationToken cancellationToken)
    {
        var result = await ExecuteGitAsync("rev-parse --short HEAD", path, cancellationToken, gitDir);
        return result.Success ? result.StandardOutput.Trim() : string.Empty;
    }

    /// <summary>Sitenin git dizini: kiracı kökünde değil, panelin veri kökünde.</summary>
    internal string GitDirFor(string siteName) =>
        fileStore.ResolvePath($"git/{WindowsReservedNames.MakeSafe($"{SanitizeFileName(siteName)}.git")}");

    /// <summary>
    /// Dağıtımdan ÖNCE. (1) Kiracı kökündeki <c>.git</c> KULLANILMAZ ve KOPYALANMAZ (commondir /
    /// objects/info/alternates ile panel deposunu kiracı klasörüne bağlatıp SYSTEM'de sürücü
    /// çalıştırmak mümkündü); web kökünden panel klasörüne TAŞINIR (silme yok) çünkü içindeki remote URL
    /// jeton taşıyabilir ve web kökünde okunabilir. Panel git dizini her zaman sıfırdan kurulur.
    /// (2) Panel git dizinindeki commondir/gitdir/alternates ve izin dışı config reddedilir.
    /// Dönen metin (varsa) dağıtım çıktısına eklenecek uyarıdır.
    /// </summary>
    internal async Task<string?> EnsureRepositoryLocationAsync(string path, string gitDir, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        string? warning = null;
        var tenantGit = Path.Combine(path, ".git");

        // gitfile ya da reparse point: başka bir yere bağlayabilir -> reddet.
        if (File.Exists(tenantGit))
        {
            throw new AppException("git.unsafe_repository", 409, 409, "'.git' is a file (gitfile).");
        }

        if (Directory.Exists(tenantGit))
        {
            if ((File.GetAttributes(tenantGit) & FileAttributes.ReparsePoint) != 0)
            {
                throw new AppException("git.unsafe_repository", 409, 409, "'.git' is a reparse point.");
            }

            warning = RelocateTenantGitDirectory(tenantGit, gitDir);
        }

        if (Directory.Exists(gitDir))
        {
            foreach (var forbidden in new[] { "commondir", "gitdir", Path.Combine("objects", "info", "alternates"), Path.Combine("objects", "info", "http-alternates") })
            {
                if (File.Exists(Path.Combine(gitDir, forbidden)) || Directory.Exists(Path.Combine(gitDir, forbidden)))
                {
                    throw new AppException("git.unsafe_repository", 409, 409, $"'{forbidden}' is present in the repository directory.");
                }
            }

            ValidateGitDirectoryConfig(gitDir);
        }

        return warning;
    }

    private static void ValidateGitDirectoryConfig(string gitDir)
    {
        var config = Path.Combine(gitDir, "config");
        var text = File.Exists(config) ? File.ReadAllText(config) : string.Empty;
        var violation = GitConfigAllowList.FindViolation(text);
        if (violation is not null)
        {
            throw new AppException("git.unsafe_config", 409, 409, violation);
        }
    }

    private static readonly Regex UserInfoInUrl = new(@"(?<=://)[^/@\s""]+@", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Eski kiracı <c>.git</c>'ini web kökünden <c>&lt;DataRoot&gt;\git\&lt;site&gt;.tenant-old-&lt;zaman&gt;</c> altına taşır.
    /// Aynı birimde <see cref="Directory.Move"/>; farklı birimde kopyala, doğrula, sonra kaynağı sil.
    /// Taşınamazsa kalan config'teki remote URL'lerin userinfo (jeton/parola) kısmı silinir ve uyarı döner.
    /// </summary>
    private string? RelocateTenantGitDirectory(string tenantGit, string gitDir)
    {
        var parent = Path.GetDirectoryName(gitDir)!;
        var name = Path.GetFileName(gitDir);
        var baseName = name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        var target = Path.Combine(parent, $"{baseName}.tenant-old-{DateTime.UtcNow:yyyyMMddHHmmssfff}");

        try
        {
            Directory.CreateDirectory(parent);
            var sameVolume = string.Equals(Path.GetPathRoot(Path.GetFullPath(tenantGit)), Path.GetPathRoot(Path.GetFullPath(target)), StringComparison.OrdinalIgnoreCase);
            if (sameVolume)
            {
                Directory.Move(tenantGit, target);
            }
            else
            {
                CopyTreeRejectingReparsePoints(tenantGit, target);
                if (CountFiles(tenantGit) != CountFiles(target))
                {
                    throw new IOException("Copied tree does not match the source.");
                }

                foreach (var file in Directory.EnumerateFiles(tenantGit, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(tenantGit, true);
            }

            logger.LogInformation("Kiracı .git web kökünden taşındı: {Source} -> {Target}", tenantGit, target);
            return null;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Kiracı .git web kökünden taşınamadı: {Path}", tenantGit);
            var sanitized = SanitizeRemoteUrls(Path.Combine(tenantGit, "config"));
            return "WARNING: the old .git directory could not be moved out of the web root"
                + (sanitized ? "; credentials in its remote URLs were removed" : "; its remote URLs could NOT be sanitized")
                + ". Remove or protect the directory manually.";
        }
    }

    private static int CountFiles(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count();

    private static bool SanitizeRemoteUrls(string configPath)
    {
        try
        {
            if (!File.Exists(configPath)) return true;
            var text = File.ReadAllText(configPath);
            var cleaned = UserInfoInUrl.Replace(text, string.Empty);
            if (!ReferenceEquals(cleaned, text) && cleaned != text)
            {
                File.SetAttributes(configPath, FileAttributes.Normal);
                File.WriteAllText(configPath, cleaned);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyTreeRejectingReparsePoints(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new AppException("git.unsafe_repository", 409, 409, $"'{entry.Name}' is a reparse point.");
            }

            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo directory)
            {
                CopyTreeRejectingReparsePoints(directory.FullName, target);
            }
            else
            {
                File.Copy(entry.FullName, target, overwrite: false);
            }
        }
    }

    private async Task EnsureGitAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await ExecuteGitAsync("--version", null, cancellationToken);
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
            WebhookSecret = record.WebhookSecret,
            // Arayüz jetonu artık BAŞLIKLA göstermeli (sorgu dizesi erişim günlüklerine yazılır).
            // Adı sunucudan taşımak zorunlu: arayüz sabit yazsaydı, ad değiştiğinde panel
            // kullanıcıya sunucunun okumadığı bir başlık gösterirdi. Gerekçe GitWebhookToken'da.
            WebhookHeaderName = AdaPanel.Core.Common.GitWebhookToken.HeaderName
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
        //
        // DESEN ARTIK BURADA DEĞİL (GitRepositoryAddress.ScpLikePattern): adres kapısı da konağı
        // AYNI eşleşmeden çıkarıyor. İki nüsha olsaydı "kabul edilen biçim" ile "denetlenen konak"
        // ayrışabilirdi — kapatılan açık tam olarak o ayrışmanın sonucuydu.
        var isScpLike = GitRepositoryAddress.IsScpLike(trimmed);

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
    private Task<CommandResult> ExecuteGitAsync(string arguments, string? workingDirectory, CancellationToken token, string? gitDir = null, bool useWorkTree = true) =>
        CommandRunner?.Invoke(arguments, workingDirectory, token) ?? RunGitAsync(arguments, workingDirectory, token, gitDir, useWorkTree);

    private static Task<CommandResult> RunGitAsync(string arguments, string? workingDirectory, CancellationToken cancellationToken, string? gitDir = null, bool useWorkTree = true)
        => ProcessRunner.RunAsync(
            "git",
            BuildGitArguments(gitDir is null
                ? arguments
                : $"--git-dir={QuoteArg(gitDir)} " + (useWorkTree && workingDirectory is not null ? $"--work-tree={QuoteArg(workingDirectory)} " : string.Empty) + arguments),
            workingDirectory,
            cancellationToken,
            new Dictionary<string, string>
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GCM_INTERACTIVE"] = "never",
                ["GIT_ASKPASS"] = "echo",

                // G6: git SYSTEM olarak, kiracının YAZABİLDİĞİ dizinde koşar; .git/config'i kiracı
                // düzenleyebilir. Kullanıcı/global yapılandırma okunmaz (SYSTEM profili zaten kiracının
                // değil; yine de deterministik). Sistem yapılandırması BİLEREK açık bırakıldı: yalnız
                // yönetici yazabilir ve Git for Windows'ta TLS arka ucu/CA yolu orada durur
                // (GIT_CONFIG_NOSYSTEM=1 https depolarını sessizce kırabilir).
                ["GIT_CONFIG_GLOBAL"] = "NUL",

                // İKİNCİ VE BAĞIMSIZ KATMAN: git'in KENDİ taşıyıcı beyaz listesi. ValidateRepositoryUrl
                // `ext::` biçimini reddediyor, ama o doğrulama yalnız ConfigureAsync'te çalışıyor —
                // diskteki config/git/<site>.json bir kez kötü bir değerle yazıldıysa DeployAsync onu
                // yeniden ölçmüyor. Bu değişken kararı git'in transport katmanına taşır: listede
                // olmayan hiçbir taşıyıcı (ext, file, ftp…) çalıştırılmaz.
                ["GIT_ALLOW_PROTOCOL"] = "http:https:ssh"
            });

    /// <summary>
    /// Her <c>git</c> çağrısının ÖNÜNE konan yapılandırma: <b>yönlendirme İZLENMEZ</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ÖLÇÜLDÜ (git 2.54.0.windows.1, gerçek loopback sunucuları):</b> panelin kurduğu birebir
    /// ortamda <c>git clone http://dis/dis.git</c> çağrıldı; dış sunucu
    /// <c>302 Location: http://ic/ic.git/info/refs?service=git-upload-pack</c> döndü. Sonuç: <b>iç
    /// sunucuya GERÇEK git keşif isteği ULAŞTI</b> (<c>GET /ic.git/info/refs?service=git-upload-pack</c>),
    /// git <c>warning: redirecting to …</c> yazdı. Yani adres kapısı yalnız İLK adresi görüyordu —
    /// <c>curl</c> yolunda kapatılan açığın (efade94) git'teki birebir eşi. Aynı düzenekte
    /// <c>-c http.followRedirects=false</c> ile iç sunucunun istek sayısı <b>0</b> oldu.
    /// </para>
    /// <para>
    /// <b>MEŞRU YÖNLENDİRME KIRILMADI</b> — çünkü yönlendirmeyi artık PANEL çözüyor
    /// (<see cref="ResolveHttpRepositoryUrlAsync"/>) ve zincirin HER ADIMI
    /// <see cref="OutboundConnectGuard"/>'dan geçiyor. git'e zaten çözülmüş SON adres veriliyor,
    /// yani izleyecek bir yönlendirme kalmıyor. Bayrağı tek başına atmak (panel çözümlemesi
    /// olmadan) yeniden adlandırılmış GitHub depolarını ve <c>http→https</c> yükseltmesi yapan
    /// kurulumları sessizce kırardı.
    /// </para>
    /// <para>
    /// <c>-c</c> git'in KENDİ yapılandırmasını geçersiz kılar; sunucudaki <c>--global</c> ya da
    /// depo içi <c>.git/config</c> bunu YENEMEZ (komut satırı en yüksek önceliktir). Kararı
    /// ortam/dosya yapılandırmasına bırakmak, korumayı kiracının düzenleyebildiği bir dosyaya
    /// bağlamak olurdu.
    /// </para>
    /// </remarks>
    internal static string BuildGitArguments(string arguments) =>
        "-c http.followRedirects=false " + HardeningArguments + arguments;

    /// <summary>
    /// G6: kiracının <c>.git/config</c> dosyasıyla kod çalıştırmasını kapatan komut satırı geçersiz
    /// kılmaları (komut satırı depo içi yapılandırmadan yüksek önceliklidir). fsmonitor/hook/ssh/askpass
    /// yürütücüleri etkisiz; <c>file</c> taşıyıcısı kapalı. Not: depodaki <c>filter.*</c> sürücüleri
    /// bu yolla kapatılamaz (bakiye).
    /// </summary>
    internal const string HardeningArguments =
        "-c core.fsmonitor=false -c core.hooksPath=NUL -c core.sshCommand=ssh " +
        "-c protocol.file.allow=never -c core.askPass=echo -c credential.helper= " +
        "-c core.symlinks=false -c core.protectNTFS=true -c http.cookieFile= -c http.saveCookies=false ";

    /// <summary>
    /// Testlerin ÜRETİMDEKİ git çağrısını (aynı ortam değişkenleri + aynı ön yapılandırma ile)
    /// gerçekten koşturabilmesi için. Kaynak taraması yerine DAVRANIŞ ölçülsün diye var.
    /// </summary>
    internal static Task<CommandResult> RunGitForTests(string arguments, string? workingDirectory, CancellationToken cancellationToken, string? gitDir = null, bool useWorkTree = true)
        => RunGitAsync(arguments, workingDirectory, cancellationToken, gitDir, useWorkTree);

    /// <summary>
    /// <c>http/https</c> depolarında YÖNLENDİRME ZİNCİRİNİ panelin DENETLENEN istemcisiyle çözer ve
    /// git'e verilecek NİHAİ adresi döner. Başka şemalarda (ssh, scp) adres olduğu gibi döner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Kalıp icat edilmedi:</b> efade94'te cron adresi için yapılanın aynısı — isteği panelin
    /// korumalı <c>HttpClient</c>'ından geçir, çünkü <see cref="OutboundConnectGuard"/>'ın
    /// <c>ConnectCallback</c>'i HER BAĞLANTIDA, yani her yönlendirme adımında çalışır. Kural
    /// burada TEKRAR YAZILMAZ.
    /// </para>
    /// <para>
    /// Sorulan adres git'in kendi keşif adresidir (<c>&lt;depo&gt;/info/refs?service=git-upload-pack</c>);
    /// böylece yeniden adlandırılmış depoların ve <c>http→https</c> yükseltmelerinin ürettiği
    /// yönlendirme, git'in göreceğiyle AYNI istekte ölçülür.
    /// </para>
    /// <para>
    /// <b>Ağ arızasında adres DEĞİŞTİRİLMEZ</b> (özgün adres döner): ön çözümleme bir KOLAYLIKtır,
    /// koruma değil. Korumayı <c>http.followRedirects=false</c> + kapı sağlıyor; ön çözümleme
    /// yalnız meşru yönlendirmelerin çalışmaya devam etmesini sağlıyor. Vekil arkasındaki bir
    /// sunucuda panelin denetlenen istemcisi (<c>UseProxy=false</c>) sorguyu yapamayabilir; o
    /// durumda git özgün adresle koşar ve kendi hatasını verir — sessizce yanlış bir adrese
    /// gitmez.
    /// </para>
    /// </remarks>
    internal async Task<string> ResolveHttpRepositoryUrlAsync(string url, CancellationToken cancellationToken)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (httpClientFactory is null ||
            !Uri.TryCreate(trimmed, UriKind.Absolute, out var adres) ||
            adres.Scheme is not ("http" or "https"))
        {
            return trimmed;
        }

        var kesif = new Uri(trimmed.TrimEnd('/') + "/info/refs?service=git-upload-pack");

        try
        {
            var istemci = httpClientFactory.CreateClient(OutboundConnectGuard.GuardedClientName);
            using var yanit = await istemci.GetAsync(kesif, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            var son = yanit.RequestMessage?.RequestUri;
            if (son is null)
            {
                return trimmed;
            }

            // Nihai adresten keşif son ekini SOY. Sunucu beklenmedik bir yola indiyse (son ek yok)
            // özgün adres korunur: tahmin ederek adres uydurmak, git'i bizim ölçmediğimiz bir yere
            // göndermek olurdu.
            const string sonEk = "/info/refs";
            if (!son.AbsolutePath.EndsWith(sonEk, StringComparison.Ordinal))
            {
                return trimmed;
            }

            var govde = new UriBuilder(son)
            {
                Path = son.AbsolutePath[..^sonEk.Length],
                Query = string.Empty,
                Fragment = string.Empty
            }.Uri.ToString();

            // Yönlendirme YOKSA özgün metni koru (UriBuilder normalleştirmesi adresi gereksiz yere
            // değiştirmesin; kullanıcının kaydettiği adres ekranlarda aynen görünüyor).
            if (string.Equals(govde.TrimEnd('/'), trimmed.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            // YÖNLENDİRME SONUNDAKİ KONAK DA KAPIDAN GEÇER. Bağlantı anında zaten denetlendi; bu
            // ikinci ölçüm git'in AZ SONRA kendi soketini açacak olmasındandır (kayıt o aralıkta
            // değişebilir) ve kullanıcıya "ağ hatası" değil adres yasağı hatası verir.
            await EnsureRepositoryHostAllowedAsync(govde, cancellationToken);
            return govde;
        }
        catch (Exception exception) when (OutboundBlockedException.Cikar(exception) is { } engellenen)
        {
            throw new AppException("git.repository_forbidden", 403, 403, engellenen.BlockedTarget);
        }
        catch (Exception exception) when (exception is not AppException && exception is not OperationCanceledException)
        {
            // Ağ/protokol arızası: adres değiştirilmez, git kendi hatasını versin.
            logger.LogDebug(exception, "Git depo adresi ön çözümlemesi yapılamadı: {Url}", trimmed);
            return trimmed;
        }
    }

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
