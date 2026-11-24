using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Mail;

public sealed class WebmailSsoService(StalwartWebmailCredentials credentials, IMailService mail,
    JsonFileStore files, MailProviderSelection provider)
{
    internal const string LedgerPath = "config/webmail-sso-leases.json";
    public sealed record Lease(string Correlation, string AccountId, string? CredentialId, DateTimeOffset ExpiresAt,
        string Domain = "");
    public sealed record Login(string Address, string Password, string Lease, long ExpiresAt);

    public async Task<string> ResolveAsync(string domain, string address, CancellationToken token)
    {
        if (provider.Current != "stalwart") throw new AppException("mail.webmail_sso_unsupported", 409, 409);
        if (address.Length > 320 || address.Count(c => c == '@') != 1 ||
            !address.EndsWith("@" + domain, StringComparison.OrdinalIgnoreCase) || address[..address.IndexOf('@')].Length == 0)
            throw new AppException("mail.webmail_sso_invalid", 403, 403);
        var workspace = await mail.GetWorkspaceAsync(domain, token);
        if (!workspace.AccountsMeasured || workspace.Accounts.Count(x =>
            string.Equals(x.Address, address, StringComparison.OrdinalIgnoreCase) && x.Active != false) != 1)
            throw new AppException("mail.webmail_sso_invalid", 403, 403);
        return await credentials.ResolveAccountAsync(address, token);
    }

    public async Task<Login> ConsumeAsync(WebmailSsoTickets.Grant grant, Func<CancellationToken, Task> authorize,
        CancellationToken token, Func<CancellationToken, Task>? beforeReturn = null)
    {
        await authorize(token);
        if (await ResolveAsync(grant.Domain, grant.Address, token) != grant.AccountId) throw new AppException("mail.webmail_sso_invalid", 403, 403);
        await authorize(token);
        var correlation = "AdaPanel webmail SSO " + Guid.NewGuid().ToString("N");
        var expiry = DateTimeOffset.UtcNow.AddMinutes(30);
        var lease = new Lease(correlation, grant.AccountId, null, expiry, grant.Domain);
        // Persist ownership before a request can have an ambiguous created-but-timed-out result.
        await files.UpdateAsync(LedgerPath, new List<Lease>(), list =>
        {
            if (list.Count >= 1024) throw new AppException("mail.webmail_sso_rate_limited", 429, 429);
            list.Add(lease); return list;
        }, token);
        try
        {
            var credential = await credentials.CreateAsync(grant.AccountId, correlation, expiry, token);
            await files.UpdateAsync(LedgerPath, new List<Lease>(), list => list.Select(x =>
                x.Correlation == correlation ? x with { CredentialId = credential.Id } : x).ToList(), token);
            if (await ResolveAsync(grant.Domain, grant.Address, token) != grant.AccountId)
                throw new AppException("mail.webmail_sso_invalid", 403, 403);
            await authorize(token);
            if (beforeReturn is not null) await beforeReturn(token);
            return new(grant.Address, credential.Secret, correlation, expiry.ToUnixTimeSeconds());
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await RevokeAsync(correlation, cleanup.Token); } catch { /* ledger retries, server expiry remains authoritative */ }
            throw;
        }
    }

    public async Task RevokeAsync(string correlation, CancellationToken token)
    {
        var leases = await files.ReadAsync(LedgerPath, new List<Lease>(), token);
        var lease = leases.SingleOrDefault(x => x.Correlation == correlation);
        if (lease is null) return;
        var removed = await credentials.RevokeOwnedAsync(lease.AccountId, lease.CredentialId, lease.Correlation, token);
        // A timed-out create might still be in flight. Keep a pending ownership record until
        // it is observed and destroyed, or its server-enforced absolute expiry has passed.
        if (!removed && lease.CredentialId is null && lease.ExpiresAt > DateTimeOffset.UtcNow) return;
        await files.UpdateAsync(LedgerPath, new List<Lease>(), list => list.Where(x => x.Correlation != correlation).ToList(), token);
    }

    public async Task<string?> GetLeaseDomainAsync(string correlation, CancellationToken token) =>
        (await files.ReadAsync(LedgerPath, new List<Lease>(), token))
            .SingleOrDefault(lease => lease.Correlation == correlation)?.Domain;

    internal async Task CleanupAsync(CancellationToken token)
    {
        var leases = await files.ReadAsync(LedgerPath, new List<Lease>(), token);
        // Pending create records are retried immediately, including after process restart.
        var failed = false;
        foreach (var lease in leases.Where(x => x.ExpiresAt <= DateTimeOffset.UtcNow ||
            x.CredentialId is null && x.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(28)))
        {
            try { await RevokeAsync(lease.Correlation, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { failed = true; } // One unavailable account must not starve unrelated cleanup.
        }
        if (failed) throw new AppException("mail.webmail_sso_unavailable", 503, 503);
    }
}

public sealed class WebmailSsoCleanup(IServiceScopeFactory scopes, ILogger<WebmailSsoCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<WebmailSsoService>().CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("Webmail SSO credential cleanup will be retried."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
