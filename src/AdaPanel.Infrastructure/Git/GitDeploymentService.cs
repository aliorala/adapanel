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
    ILogger<GitDeploymentService> logger) : IGitDeploymentService
{
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
                result = await ProcessRunner.RunAsync(
                    "git",
                    $"clone --branch {QuoteArg(record.Branch)} --single-branch {QuoteArg(record.RepositoryUrl)} .",
                    path,
                    cancellationToken);
            }
        }
        else
        {
            await ProcessRunner.RunAsync("git", $"remote set-url origin {QuoteArg(record.RepositoryUrl)}", path, cancellationToken);
            await ProcessRunner.RunAsync("git", $"fetch origin {QuoteArg(record.Branch)} --prune", path, cancellationToken);
            result = await ProcessRunner.RunAsync("git", $"reset --hard {QuoteArg($"origin/{record.Branch}")}", path, cancellationToken);
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
        await ProcessRunner.RunAsync("git", "init", path, cancellationToken);
        await ProcessRunner.RunAsync("git", $"remote add origin {QuoteArg(record.RepositoryUrl)}", path, cancellationToken);
        await ProcessRunner.RunAsync("git", $"fetch origin {QuoteArg(record.Branch)} --prune", path, cancellationToken);
        await ProcessRunner.RunAsync("git", $"checkout -f -B {QuoteArg(record.Branch)} {QuoteArg($"origin/{record.Branch}")}", path, cancellationToken);
        return await ProcessRunner.RunAsync("git", $"reset --hard {QuoteArg($"origin/{record.Branch}")}", path, cancellationToken);
    }

    private static async Task<string> GetCurrentCommitAsync(string path, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync("git", "rev-parse --short HEAD", path, cancellationToken);
        return result.Success ? result.StandardOutput.Trim() : string.Empty;
    }

    private static async Task EnsureGitAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("git", "--version", cancellationToken: cancellationToken);
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

    private static string ConfigPath(string siteName) => $"config/git/{SanitizeFileName(siteName)}.json";

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

    private static void ValidateRepositoryUrl(string url)
    {
        var trimmed = url.Trim();
        var isHttp = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        var isScpLike = trimmed.Contains('@') && trimmed.Contains(':'); // git@host:owner/repo.git
        if (!isHttp && !isScpLike)
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
