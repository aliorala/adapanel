using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Git;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Git;

/// <summary>
/// Dağıtım anahtarı / HTTPS jetonu, derleme sonrası komut, geçmiş + geri alma ve yapılandırmayı kaldırma.
/// </summary>
/// <remarks>
/// <para>
/// <b>SIR KURALI:</b> özel SSH anahtarı ve HTTPS jetonu yalnız <see cref="Core.Interfaces.ISecretProtector"/>
/// (DPAPI/DataProtection) ile korunmuş olarak diske yazılır. Hiçbir DTO, denetim kaydı, günlük ya da
/// istisna metni onları taşımaz; git çıktısı da (<see cref="Scrub"/>) jetonun kendisinden ve biçimlerinden
/// arındırılır. Jeton komut satırına DEĞİL, yalnız alt sürecin ortam değişkenine (<c>GIT_CONFIG_VALUE_0</c>)
/// konur; özel anahtar yalnız koşu süresince ACL'i kapalı geçici dosyada durur ve hemen sıfırlanıp silinir.
/// </para>
/// <para>
/// <b>Derleme sonrası komut:</b> serbest kabuk YOKTUR. Yalnız <see cref="PostDeployAllowList"/> kimlikleri
/// çalışır ve sitenin kendi kodunu (composer/npm betikleri) çalıştırdıkları için SYSTEM olarak değil,
/// <see cref="LimitedIdentityRunner"/> ile sınırlı kimlikle koşarlar.
/// </para>
/// </remarks>
public sealed partial class GitDeploymentService
{
    private const int MaxHistory = 30;
    private const int DefaultPostDeployTimeoutSeconds = 600;

    /// <summary>Beyaz liste: kimlik → (araç, argümanlar). Argümanlar SABİTTİR; kullanıcıdan hiçbir parça gelmez.</summary>
    internal static readonly IReadOnlyDictionary<string, (string Tool, string Arguments)> PostDeployAllowList =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["composer-install"] = ("composer", "install --no-dev --no-interaction --prefer-dist --optimize-autoloader"),
            ["npm-ci"] = ("npm", "ci"),
            ["npm-run-build"] = ("npm", "run build")
        };

    /// <summary>Test dikişi: SSH anahtar çifti üretici (özel, genel). Varsayılan ssh-keygen.</summary>
    internal Func<string, CancellationToken, Task<(string Private, string Public)>>? DeployKeyGenerator { get; init; }

    /// <summary>Test dikişi: derleme sonrası komut koşucusu (kimlik, çalışma klasörü, zaman aşımı).</summary>
    internal Func<string, string, TimeSpan, CancellationToken, Task<CommandResult>>? PostDeployRunner { get; init; }

    /// <summary>Test dikişi: alt sürece verilen ortam değişkenlerini gözler.</summary>
    internal Action<IReadOnlyDictionary<string, string>>? EnvironmentObserver { get; init; }

    private sealed class GitDeployHistoryEntry
    {
        public string Id { get; set; } = string.Empty;
        public DateTime Utc { get; set; }
        public string Trigger { get; set; } = string.Empty;
        public string PreviousCommit { get; set; } = string.Empty;
        public string NewCommit { get; set; } = string.Empty;
        public bool Success { get; set; }
        public int LocalChangeCount { get; set; }
        public List<string> LocalChanges { get; set; } = [];
        public string SavedChangesRef { get; set; } = string.Empty;
        public string PostDeploy { get; set; } = string.Empty;
    }

    // ---- Yapılandırmayı kaldırma ----

    public async Task<GitRemovalResultDto> RemoveAsync(string siteName, CancellationToken cancellationToken = default)
    {
        return await concurrencyLock.RunExclusiveAsync(DeploymentKey(siteName), () =>
        {
            var removed = new List<string>();
            var leftovers = new List<string>();
            var kept = new List<string> { "site files" };

            var config = fileStore.ResolvePath(ConfigPath(siteName));
            try
            {
                if (File.Exists(config))
                {
                    File.SetAttributes(config, FileAttributes.Normal);
                    File.Delete(config);
                    removed.Add("configuration");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Git yapılandırması silinemedi: {SiteName}", siteName);
                leftovers.Add("configuration: could not be deleted (deploy key and token are still stored)");
            }

            var gitDir = GitDirFor(siteName);
            try
            {
                if (Directory.Exists(gitDir))
                {
                    DeleteTreeNoFollow(gitDir);
                    removed.Add("panel git directory");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Panelin git dizini silinemedi: {SiteName}", siteName);
                leftovers.Add("panel git directory: could not be fully deleted");
            }

            // Eski kiracı .git kopyaları (kiracı geçmişi olabilir) BİLİNÇLİ korunur.
            var parent = Path.GetDirectoryName(gitDir);
            var baseName = Path.GetFileName(gitDir);
            baseName = baseName.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? baseName[..^4] : baseName;
            if (parent is not null && Directory.Exists(parent))
                foreach (var old in Directory.EnumerateDirectories(parent, baseName + ".tenant-old-*"))
                    kept.Add("relocated tenant .git copy: " + Path.GetFileName(old));

            return Task.FromResult(new GitRemovalResultDto
            {
                SiteName = siteName,
                Removed = removed.ToArray(),
                Leftovers = leftovers.ToArray(),
                Kept = kept.ToArray()
            });
        }, cancellationToken);
    }

    /// <summary>Ağacı siler; yeniden-ayrıştırma noktalarını TAKİP ETMEZ (yalnız bağlantıyı siler), salt-okunur bayrağı kaldırır.</summary>
    internal static void DeleteTreeNoFollow(string directory)
    {
        var info = new DirectoryInfo(directory);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            info.Delete(); // yalnız bağlantı; hedef içeriği silinmez
            return;
        }

        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo child)
            {
                DeleteTreeNoFollow(child.FullName);
            }
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
        }

        info.Delete();
    }

    // ---- SSH dağıtım anahtarı ----

    public async Task<GitDeploymentDto> GenerateDeployKeyAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var protector = RequireProtector();
        return await concurrencyLock.RunExclusiveAsync(DeploymentKey(siteName), async () =>
        {
            var path = await ResolveSitePathAsync(siteName, cancellationToken);
            var record = await LoadAsync(siteName, cancellationToken);
            var (privateKey, publicKey) = await GenerateKeyPairAsync(siteName, cancellationToken);
            publicKey = publicKey.Trim();
            var fingerprint = FingerprintOf(publicKey);
            record.DeployKeyProtected = protector.Protect(privateKey);
            record.DeployPublicKey = publicKey;
            record.DeployKeyFingerprint = fingerprint;
            record.DeployKeyCreatedUtc = DateTime.UtcNow;
            await SaveAsync(siteName, record, cancellationToken);
            return ToDto(siteName, path, record);
        }, cancellationToken);
    }

    public async Task<GitDeploymentDto> RemoveDeployKeyAsync(string siteName, CancellationToken cancellationToken = default)
    {
        return await concurrencyLock.RunExclusiveAsync(DeploymentKey(siteName), async () =>
        {
            var path = await ResolveSitePathAsync(siteName, cancellationToken);
            var record = await LoadAsync(siteName, cancellationToken);
            record.DeployKeyProtected = string.Empty;
            record.DeployPublicKey = string.Empty;
            record.DeployKeyFingerprint = string.Empty;
            record.DeployKeyCreatedUtc = null;
            await SaveAsync(siteName, record, cancellationToken);
            return ToDto(siteName, path, record);
        }, cancellationToken);
    }

    private async Task<(string Private, string Public)> GenerateKeyPairAsync(string siteName, CancellationToken cancellationToken)
    {
        var comment = "adapanel-" + Regex.Replace(siteName, "[^A-Za-z0-9._-]", "_");
        if (DeployKeyGenerator is not null) return await DeployKeyGenerator(comment, cancellationToken);

        var directory = fileStore.EnsureDirectory("git-keys/tmp");
        var keyPath = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        try
        {
            var result = await ProcessRunner.RunAsync(
                "ssh-keygen",
                $"-q -t ed25519 -N \"\" -C {QuoteArg(comment)} -f {QuoteArg(keyPath)}",
                directory,
                cancellationToken,
                timeout: TimeSpan.FromSeconds(60));
            if (!result.Success || !File.Exists(keyPath) || !File.Exists(keyPath + ".pub"))
                throw new AppException("git.deploy_key_generation_failed", 500, 500);
            return (await File.ReadAllTextAsync(keyPath, cancellationToken), await File.ReadAllTextAsync(keyPath + ".pub", cancellationToken));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new AppException("git.ssh_keygen_not_available", 409, 409);
        }
        finally
        {
            WipeFile(keyPath);
            WipeFile(keyPath + ".pub");
        }
    }

    internal static string FingerprintOf(string publicKeyLine)
    {
        var parts = publicKeyLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return string.Empty;
        try
        {
            return "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(parts[1]))).TrimEnd('=');
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    private static void WipeFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var length = new FileInfo(path).Length;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var zeros = new byte[Math.Min(length, 8192)];
                for (long written = 0; written < length; written += zeros.Length) stream.Write(zeros, 0, (int)Math.Min(zeros.Length, length - written));
                stream.Flush(true);
            }
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Silinemeyen geçici dosya dizin ACL'i (yalnız panel hesapları) ile korunur; sessiz değil: günlük.
            System.Diagnostics.Trace.TraceWarning("Git key temp file could not be wiped: " + exception.GetType().Name);
        }
    }

    // ---- HTTPS jetonu ----

    private static readonly Regex UsernamePattern = new("^[A-Za-z0-9._@-]{1,100}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<GitDeploymentDto> SetHttpsTokenAsync(string siteName, SetGitHttpsTokenRequest request, CancellationToken cancellationToken = default)
    {
        var protector = RequireProtector();
        var username = (request.Username ?? string.Empty).Trim();
        var token = request.Token ?? string.Empty;
        if (!UsernamePattern.IsMatch(username)) throw new AppException("git.invalid_token_username", 400, 400);
        if (token.Length is 0 or > 512 || token.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            throw new AppException("git.invalid_token", 400, 400);

        return await concurrencyLock.RunExclusiveAsync(DeploymentKey(siteName), async () =>
        {
            var path = await ResolveSitePathAsync(siteName, cancellationToken);
            var record = await LoadAsync(siteName, cancellationToken);
            // Düz http üzerinden jeton gönderilmez: yapılandırılmış adres https değilse reddedilir.
            if (!record.RepositoryUrl.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new AppException("git.token_requires_https", 409, 409);
            record.HttpsUsername = username;
            record.HttpsTokenProtected = protector.Protect(token);
            await SaveAsync(siteName, record, cancellationToken);
            return ToDto(siteName, path, record);
        }, cancellationToken);
    }

    public async Task<GitDeploymentDto> RemoveHttpsTokenAsync(string siteName, CancellationToken cancellationToken = default)
    {
        return await concurrencyLock.RunExclusiveAsync(DeploymentKey(siteName), async () =>
        {
            var path = await ResolveSitePathAsync(siteName, cancellationToken);
            var record = await LoadAsync(siteName, cancellationToken);
            record.HttpsUsername = string.Empty;
            record.HttpsTokenProtected = string.Empty;
            await SaveAsync(siteName, record, cancellationToken);
            return ToDto(siteName, path, record);
        }, cancellationToken);
    }

    private Core.Interfaces.ISecretProtector RequireProtector() =>
        secretProtector ?? throw new AppException("git.secret_store_unavailable", 503, 503);

    /// <summary>Dağıtım süresince kimlik doğrulama bağlamı; Dispose geçici anahtar dosyasını sıfırlayıp siler.</summary>
    private sealed class GitAuthContext : IDisposable
    {
        public Dictionary<string, string> Environment { get; } = [];
        public List<string> Secrets { get; } = [];
        public string? KeyPath { get; set; }

        public void Dispose()
        {
            if (KeyPath is not null) WipeFile(KeyPath);
        }
    }

    private GitAuthContext PrepareAuth(GitDeploymentRecord record, string url)
    {
        var context = new GitAuthContext();
        var isHttps = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var isPlainHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        var isSsh = !isHttps && !isPlainHttp;

        if (!string.IsNullOrEmpty(record.HttpsTokenProtected) && (isHttps || isPlainHttp))
        {
            if (!isHttps) throw new AppException("git.token_requires_https", 409, 409);
            var token = RequireProtector().Unprotect(record.HttpsTokenProtected);
            var username = string.IsNullOrWhiteSpace(record.HttpsUsername) ? "x-access-token" : record.HttpsUsername;
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{token}"));
            // Komut satırına DEĞİL ortama: süreç listesinde ve git remote yapılandırmasında görünmez.
            context.Environment["GIT_CONFIG_COUNT"] = "1";
            context.Environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            context.Environment["GIT_CONFIG_VALUE_0"] = "Authorization: Basic " + basic;
            context.Secrets.Add(token);
            context.Secrets.Add(basic);
        }

        if (!string.IsNullOrEmpty(record.DeployKeyProtected) && isSsh)
        {
            var privateKey = RequireProtector().Unprotect(record.DeployKeyProtected).Replace("\r\n", "\n");
            if (!privateKey.EndsWith('\n')) privateKey += "\n";
            var directory = fileStore.EnsureDirectory("git-keys/run");
            var keyPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".key");
            using (var stream = SecureTempFile.Create(keyPath))
            {
                var bytes = new UTF8Encoding(false).GetBytes(privateKey);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            context.KeyPath = keyPath;
            var knownHosts = Path.Combine(fileStore.EnsureDirectory("git-keys"), "known_hosts");
            if (keyPath.Contains('\'') || knownHosts.Contains('\'')) throw new AppException("git.unsafe_key_path", 500, 500);
            // Panelin kendi known_hosts dosyası: ilk görülen anahtar kabul edilir (accept-new), SONRA değişirse reddedilir.
            context.Environment["GIT_SSH_COMMAND"] =
                $"ssh -i '{keyPath.Replace('\\', '/')}' -o IdentitiesOnly=yes -o BatchMode=yes " +
                $"-o StrictHostKeyChecking=accept-new -o UserKnownHostsFile='{knownHosts.Replace('\\', '/')}'";
            context.Secrets.Add(privateKey.Trim());
        }

        EnvironmentObserver?.Invoke(context.Environment);
        return context;
    }

    /// <summary>Çıktıdan jetonu ve türevlerini siler (git bazen başlık/URL parçalarını yankılar).</summary>
    internal static string Scrub(string text, IEnumerable<string> secrets)
    {
        foreach (var secret in secrets.Where(value => value.Length >= 4))
            text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return SecretRedactor.Redact(text);
    }

    // ---- Derleme sonrası komut ----

    public async Task<GitDeploymentDto> SetPostDeployAsync(string siteName, SetGitPostDeployRequest request, CancellationToken cancellationToken = default)
    {
        var commands = (request.Commands ?? []).Select(value => value?.Trim() ?? string.Empty).ToArray();
        if (commands.Length > 5 || commands.Distinct(StringComparer.Ordinal).Count() != commands.Length)
            throw new AppException("git.post_deploy_invalid", 400, 400);
        // Yalnız beyaz liste kimlikleri: serbest metin hiçbir yoldan çalıştırılamaz.
        if (commands.Any(command => !PostDeployAllowList.ContainsKey(command)))
            throw new AppException("git.post_deploy_not_allowed", 400, 400);
        if (request.TimeoutSeconds is < 30 or > 1800) throw new AppException("git.post_deploy_invalid", 400, 400);

        return await concurrencyLock.RunExclusiveAsync(DeploymentKey(siteName), async () =>
        {
            var path = await ResolveSitePathAsync(siteName, cancellationToken);
            var record = await LoadAsync(siteName, cancellationToken);
            record.PostDeployCommands = commands.ToList();
            record.PostDeployTimeoutSeconds = request.TimeoutSeconds;
            await SaveAsync(siteName, record, cancellationToken);
            return ToDto(siteName, path, record);
        }, cancellationToken);
    }

    private async Task<(bool Success, string Summary, string Output)> RunPostDeployAsync(
        string path, GitDeploymentRecord record, CancellationToken cancellationToken)
    {
        var summary = new List<string>();
        var output = new StringBuilder();
        var timeout = TimeSpan.FromSeconds(record.PostDeployTimeoutSeconds is >= 30 and <= 1800
            ? record.PostDeployTimeoutSeconds : DefaultPostDeployTimeoutSeconds);
        foreach (var id in record.PostDeployCommands)
        {
            // Diskteki kayıt elle bozulmuş olabilir: çalıştırma anında da beyaz liste yeniden ölçülür.
            if (!PostDeployAllowList.ContainsKey(id))
            {
                summary.Add($"{id}: rejected (not allowed)");
                output.AppendLine($"[post-deploy] '{id}' is not an allowed command; skipped.");
                return (false, string.Join("; ", summary), output.ToString());
            }

            CommandResult result;
            try
            {
                result = await (PostDeployRunner?.Invoke(id, path, timeout, cancellationToken)
                    ?? RunAllowedPostDeployAsync(id, path, timeout, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Git post-deploy komutu çalıştırılamadı: {Command}", id);
                var reason = exception is AppException app ? app.MessageKey : exception.GetType().Name;
                summary.Add($"{id}: could not run ({reason})");
                output.AppendLine($"[post-deploy] {id}: could not run ({reason}).");
                return (false, string.Join("; ", summary), output.ToString());
            }

            var text = CombineOutput(result);
            if (text.Length > 8000) text = text[^8000..];
            output.AppendLine($"[post-deploy] {id} exit {result.ExitCode}");
            if (text.Length > 0) output.AppendLine(text);
            if (!result.Success)
            {
                summary.Add($"{id}: failed (exit {result.ExitCode})");
                return (false, string.Join("; ", summary), output.ToString());
            }

            summary.Add($"{id}: ok");
        }

        return (true, string.Join("; ", summary), output.ToString());
    }

    private async Task<CommandResult> RunAllowedPostDeployAsync(string id, string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var (tool, arguments) = PostDeployAllowList[id];
        if (dbContext is null) throw new AppException("git.post_deploy_unavailable", 503, 503);

        async Task<string?> InstallDirAsync(Func<IQueryable<InstalledComponent>, IQueryable<InstalledComponent>> filter) =>
            await filter(dbContext.InstalledComponents.AsNoTracking().Where(item => item.Status == ComponentInstallStatus.Installed))
                .Select(item => item.InstallDir).FirstOrDefaultAsync(cancellationToken);

        if (tool == "composer")
        {
            var composerDir = await InstallDirAsync(q => q.Where(item => item.ComponentId == "composer"));
            var phpDir = await InstallDirAsync(q => q.Where(item => item.ComponentId.StartsWith("php-")));
            if (composerDir is null || phpDir is null) throw new AppException("composer.components_required", 409, 409);
            var phar = Path.Combine(composerDir, "composer.phar");
            var php = Path.Combine(phpDir, "php.exe");
            if (!File.Exists(phar) || !File.Exists(php)) throw new AppException("composer.components_required", 409, 409);
            return await LimitedIdentityRunner.RunAsync(php, $"\"{phar}\" {arguments}", path, path, timeout, cancellationToken);
        }

        var nodeDir = await InstallDirAsync(q => q.Where(item => item.ComponentId == "nodejs-lts"));
        if (nodeDir is null) throw new AppException("appruntime.runtime_not_installed", 409, 409);
        var node = Path.Combine(nodeDir, "node.exe");
        var npmCli = Path.Combine(nodeDir, "node_modules", "npm", "bin", "npm-cli.js");
        if (!File.Exists(node) || !File.Exists(npmCli)) throw new AppException("appruntime.runtime_not_installed", 409, 409);
        return await LimitedIdentityRunner.RunAsync(node, $"\"{npmCli}\" {arguments}", path, path, timeout, cancellationToken);
    }

    // ---- Geçmiş ve geri alma ----

    private static GitDeployHistoryDto[] ToHistoryDtos(IEnumerable<GitDeployHistoryEntry> history) =>
        history.OrderByDescending(entry => entry.Utc).Take(15).Select(entry => new GitDeployHistoryDto
        {
            Id = entry.Id,
            DeployedAtUtc = entry.Utc,
            Trigger = entry.Trigger,
            PreviousCommit = entry.PreviousCommit,
            NewCommit = entry.NewCommit,
            Success = entry.Success,
            LocalChangeCount = entry.LocalChangeCount,
            LocalChanges = entry.LocalChanges.ToArray(),
            SavedChangesRef = entry.SavedChangesRef,
            PostDeploy = entry.PostDeploy
        }).ToArray();

    private static readonly Regex FullSha = new("^[0-9a-f]{40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<GitDeployResultDto> RollbackAsync(string siteName, string deploymentId, CancellationToken cancellationToken = default)
    {
        return await concurrencyLock.RunExclusiveAsync(DeploymentKey(siteName), async () =>
        {
            var path = await ResolveSitePathAsync(siteName, cancellationToken);
            var record = await LoadAsync(siteName, cancellationToken);
            if (string.IsNullOrWhiteSpace(record.RepositoryUrl)) throw new AppException("git.not_configured", 409, 409);
            var entry = record.History.FirstOrDefault(item => string.Equals(item.Id, deploymentId, StringComparison.Ordinal))
                ?? throw new AppException("git.deployment_not_found", 404, 404);
            // Hedef yalnız panelin kendi kaydettiği tam SHA'dır: kullanıcıdan serbest ref gelmez.
            if (!FullSha.IsMatch(entry.PreviousCommit)) throw new AppException("git.rollback_unavailable", 409, 409);
            return await RunDeployCoreAsync(siteName, path, record, "rollback", entry.PreviousCommit, cancellationToken);
        }, cancellationToken);
    }

    private async Task<string> GetFullCommitAsync(string path, string gitDir, CancellationToken cancellationToken)
    {
        var result = await ExecuteGitAsync("rev-parse HEAD", path, cancellationToken, gitDir);
        var value = result.Success ? result.StandardOutput.Trim() : string.Empty;
        return FullSha.IsMatch(value) ? value : string.Empty;
    }

    /// <summary>
    /// <c>reset --hard</c> ÖNCESİ: izlenen dosyalardaki yerel değişiklikleri kaydeder ve bir git referansına
    /// yedekler (<c>git stash create</c> çalışma ağacına dokunmaz). Yedeklenemezse dağıtım DURUR: sessizce ezmek yok.
    /// </summary>
    private async Task PreserveLocalChangesAsync(string path, string gitDir, GitDeployHistoryEntry entry, CancellationToken cancellationToken)
    {
        var status = await ExecuteGitAsync("status --porcelain --untracked-files=no", path, cancellationToken, gitDir);
        if (!status.Success)
        {
            // Durum ölçülemedi: ezilecek şeyin olup olmadığı BİLİNMİYOR. Kayıt bunu söyler; fetch zaten bozuk depoda düşer.
            entry.LocalChanges.Add("?: local changes could not be measured");
            return;
        }

        var lines = status.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        entry.LocalChangeCount = lines.Length;
        entry.LocalChanges = lines.Take(20).ToList();
        if (lines.Length == 0) return;

        var create = await ExecuteGitAsync("-c user.name=AdaPanel -c user.email=adapanel@localhost stash create \"adapanel pre-deploy\"",
            path, cancellationToken, gitDir);
        var sha = create.Success ? create.StandardOutput.Trim() : string.Empty;
        if (!FullSha.IsMatch(sha))
            throw new AppException("git.preserve_failed", 409, 409, "Local changes could not be saved before reset.");
        var reference = $"refs/adapanel/saved/{entry.Id}";
        var update = await ExecuteGitAsync($"update-ref {QuoteArg(reference)} {sha}", path, cancellationToken, gitDir);
        if (!update.Success)
            throw new AppException("git.preserve_failed", 409, 409, "Local changes could not be saved before reset.");
        entry.SavedChangesRef = reference;
    }
}
