using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>Process-local, bounded, one-use grants. Only SHA-256 ticket digests are retained.</summary>
public sealed class WebmailSsoTickets
{
    private readonly object gate = new();
    private readonly Dictionary<string, Grant> grants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> attempts = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;
    public WebmailSsoTickets() : this(TimeProvider.System) { }
    internal WebmailSsoTickets(TimeProvider clock) => this.clock = clock;

    public sealed record Grant(ClaimsPrincipal Actor, string Authority, string Domain, string Address,
        string AccountId, string Host, string Origin, DateTimeOffset ExpiresAt);

    public string Issue(ClaimsPrincipal actor, string authority, string domain, string address, string accountId,
        string host, string origin, bool rateAlreadyChecked = false)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var key in grants.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToArray()) grants.Remove(key);
            if (grants.Count >= 1024) throw Limited();
            if (!rateAlreadyChecked) Reserve(actor);
            var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            grants.Add(Hash(ticket), new(new ClaimsPrincipal(actor.Identities.Select(x => new ClaimsIdentity(x))),
                authority, domain, address, accountId, host, origin, now.AddSeconds(60)));
            return ticket;
        }
    }

    public void Reserve(ClaimsPrincipal actor)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var key in attempts.Keys.ToArray())
            {
                while (attempts[key].TryPeek(out var date) && date <= now.AddMinutes(-1)) attempts[key].Dequeue();
                if (attempts[key].Count == 0) attempts.Remove(key);
            }
            if (grants.Count >= 1024 || attempts.Count >= 2048) throw Limited();
            var actorId = actor.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw Invalid();
            if (!attempts.TryGetValue(actorId, out var entries)) attempts[actorId] = entries = new();
            if (entries.Count >= 10) throw Limited();
            entries.Enqueue(now);
        }
    }

    public Grant Take(string ticket, string host, string origin)
    {
        if (ticket.Length != 64 || ticket.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')) throw Invalid();
        lock (gate)
        {
            // Even a mismatched caller burns the grant; no retry can race a successful consume.
            if (!grants.Remove(Hash(ticket), out var grant) || grant.ExpiresAt <= clock.GetUtcNow() ||
                !string.Equals(grant.Host, host, StringComparison.Ordinal) ||
                !string.Equals(grant.Origin, origin, StringComparison.Ordinal)) throw Invalid();
            return grant;
        }
    }

    /// <summary>Authenticated document handoff only. Inspection never consumes the broker grant.</summary>
    public Grant Inspect(string ticket, Guid actorId, string origin)
    {
        if (ticket.Length != 64 || ticket.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')) throw Invalid();
        lock (gate)
        {
            if (!grants.TryGetValue(Hash(ticket), out var grant) || grant.ExpiresAt <= clock.GetUtcNow() ||
                !Guid.TryParse(grant.Actor.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var owner) || owner != actorId ||
                !string.Equals(grant.Origin, origin, StringComparison.Ordinal)) throw Invalid();
            return grant;
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value)));
    private static AppException Invalid() => new("mail.webmail_sso_invalid", 403, 403);
    private static AppException Limited() => new("mail.webmail_sso_rate_limited", 429, 429);
}
