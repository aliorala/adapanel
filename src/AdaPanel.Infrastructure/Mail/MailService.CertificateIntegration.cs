using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Mail;

public sealed partial class MailService
{
    internal Func<string, MailCertificateMaterial?> StoreCertificateLookup { get; init; } = MailCertificateStore.Find;
    internal Func<string, string, string, string> ValidateCertificateMaterial { get; init; } = MailCertificateValidation.Validate;
    internal Func<string, int, string?, CancellationToken, Task<MailTlsEndpointDto>> TlsProbe { get; init; } = MailTlsProbe.ProbeAsync;
    internal Func<string, string, string, bool, string, CancellationToken, Task<bool>>? CertificateIssuer { get; init; }

    private async Task<bool> MailListenersMatchAsync(string host, string expected, CancellationToken token)
    {
        var probes = await Task.WhenAll(new[] { 587, 465 }.Select(port => TlsProbe(host, port, expected, token)));
        return probes.All(probe => probe.Status == "ok");
    }

    public async Task SyncWebCertificateAsync(string hostName, Func<CancellationToken, Task>? beforeMutation = null,
        CancellationToken cancellationToken = default)
    {
        if (await ResolveCliAsync(cancellationToken) is null) return;
        // An explicit web certificate operation for a mail hostname reuses the trusted store
        // certificate. It never issues another certificate or changes unrelated mail settings.
        using var operation = await MailCertificateLifecycle.EnterAsync(fileStore.ResolvePath(CertificateStatePath), cancellationToken);
        if (beforeMutation is not null) await beforeMutation(cancellationToken);
        var states = await ReadCertificateStatesAsync(cancellationToken);
        var tracked = states.Where(state => CertificateNameMatches(hostName, state.HostName)).ToArray();
        if (tracked.Length == 0 && !hostName.StartsWith("mail.", StringComparison.OrdinalIgnoreCase)) return;
        if (tracked.Length == 0)
            tracked = [new MailCertificateState { Domain = hostName[5..], HostName = hostName.Trim().ToLowerInvariant() }];
        List<Exception> failures = [];
        foreach (var state in tracked)
        {
            try
            {
        // A wildcard operation targets only the already configured mail host it covers.
        // Custom names such as smtp.example.test keep their saved domain association.
        var domain = state.Domain;
        var targetHost = state.HostName;
        var material = StoreCertificateLookup(targetHost)
            ?? throw new AppException("mail.certificate_store_missing", 409, 409);
        if (state.Thumbprint == material.Thumbprint)
        {
            var probes = await Task.WhenAll(new[] { 587, 465 }.Select(port => TlsProbe(targetHost, port, material.Thumbprint, cancellationToken)));
            if (probes.All(p => p.Status == "ok")) continue;
        }
        await ApplyStoreCertificateAsync(domain, targetHost, material, cancellationToken, beforeMutation);
            }
            catch (OperationCanceledException) { throw; }
            catch (AppException error) when (error.HttpStatusCode == 403) { throw; }
            catch (Exception error)
            {
                failures.Add(error);
                logger.LogWarning("Mail certificate synchronization failed for {MailHost}: {ErrorType}", state.HostName, error.GetType().Name);
            }
        }
        ThrowCertificateFailures(failures);
    }

    private async Task InspectMailCertificateAsync(MailCertificateDto dto, string? expected, CancellationToken token)
    {
        var probes = await Task.WhenAll(new[] { 587, 465, 443 }.Select(port =>
            TlsProbe(dto.HostName, port, port == 443 ? null : expected, token)));
        dto.Endpoints = probes.Where(p => p.Port != 443).ToArray();
        dto.WebCertificate = probes.Single(p => p.Port == 443);
        dto.Active = dto.Endpoints.All(p => p.Status == "ok");
        // Registry status is separate from the certificate actually presented on SMTP.
        try
        {
            var (cli, env) = await RequireCliAsync(token);
            var records = MailQueryResult.Olc(await RunCertificateCommandAsync(cli, "query Certificate --json", null, env, token));
            var settings = MailQueryResult.Olc(await RunCertificateCommandAsync(cli, "get SystemSettings --json", null, env, token));
            if (!records.Measured) return;
            var selection = ReadCertificateSettings(settings);
            var matches = ReadCertificateInventory(records.Items)
                .Where(item => item.Names.Any(name => CertificateNameMatches(name, dto.HostName))).ToArray();
            dto.CertificateId = string.Join(", ", matches.Select(item => item.Id));
            dto.DefaultCertificateId = selection.DefaultCertificateId;
            dto.ConfigurationStatus = matches.Length == 1 ? "ok" : matches.Length == 0 ? "missing" : "ambiguous";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is AppException or IOException or System.ComponentModel.Win32Exception)
        { dto.ConfigurationStatus = "unknown"; }
    }

    private async Task<MailCertificateDto> ApplyStoreCertificateAsync(string domain, string host,
        MailCertificateMaterial material, CancellationToken token, Func<CancellationToken, Task>? beforeMutation = null)
    {
        var previous = (await ReadCertificateStatesAsync(token)).SingleOrDefault(state => state.HostName.Equals(host, StringComparison.OrdinalIgnoreCase));
        if (previous?.HostName == host && previous.Thumbprint == material.Thumbprint)
        {
            var current = new MailCertificateDto { Domain = domain, HostName = host, Source = previous.Source, NotAfterUtc = previous.NotAfterUtc };
            await InspectMailCertificateAsync(current, material.Thumbprint, token);
            if (current.Active) return current;
        }
        var publication = await PushCertificateToStalwartAsync(host, material.CertificatePem, material.PrivateKeyPem, token, beforeMutation);
        var state = new MailCertificateState
        {
            Domain = domain, HostName = host, Source = "windows-store", Thumbprint = material.Thumbprint,
            ContentHash = ComputeHash(material.CertificatePem + material.PrivateKeyPem),
            NotAfterUtc = TryGetNotAfterUtc(material.CertificatePem)
        };
        var dto = new MailCertificateDto { Domain = domain, HostName = host, Source = state.Source, NotAfterUtc = state.NotAfterUtc };
        await InspectMailCertificateAsync(dto, state.Thumbprint, token);
        if (!dto.Active) throw new AppException("mail.certificate_not_activated", 502, 502, host);
        await MailCertificateRegistry.UpsertAsync(fileStore, state, token, publication.InitialPrimary);
        return dto;
    }

    private async Task<string?> ResolveInitialCertificatePrimaryAsync(CertificateSettings settings, string targetHost, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace((await MailCertificateRegistry.ReadAsync(fileStore, token)).PrimaryHostName)) return null;
        var peer = await ReadManagedWebmailPeerAsync(token);
        var pin = await ReadServerIdentityPinAsync(token);
        var explicitHost = peer ?? (string.IsNullOrWhiteSpace(pin?.HostName) ? null : pin.HostName);
        var candidate = (explicitHost ?? settings.DefaultHostname).Trim().ToLowerInvariant();
        if (Uri.CheckHostName(candidate) != UriHostNameType.Dns || !candidate.Contains('.') || candidate.Contains('*'))
        {
            if (explicitHost is not null) throw new AppException("mail.certificate_not_activated", 409, 409);
            return null; // Fresh native placeholder: the first successful trusted certificate may initialize the primary.
        }
        // Renewal of this same explicit name is safe even if its previous certificate expired.
        if (candidate.Equals(targetHost, StringComparison.OrdinalIgnoreCase)) return candidate;
        var probes = await Task.WhenAll(new[] { 587, 465 }.Select(port => TlsProbe(candidate, port, null, token)));
        if (probes.All(p => p.Status == "ok")) return candidate;
        if (explicitHost is not null)
            throw new AppException("mail.certificate_not_activated", 409, 409,
                "The saved mail client identity could not be validated; it was not replaced.");
        return null; // An untrusted native bootstrap certificate is not an established client identity.
    }

    private async Task<string?> ReadManagedWebmailPeerAsync(CancellationToken token)
    {
        var state = await fileStore.ReadAsync<System.Text.Json.Nodes.JsonObject?>("config/webmail.json", null, token);
        if (state is null) return null;
        var entry = MailCertificateRegistry.Field(state, "entryRoot");
        if (string.IsNullOrWhiteSpace(entry)) return null;
        if (!Path.IsPathFullyQualified(entry) || Path.GetDirectoryName(entry) is not { } root)
            throw new AppException("mail.certificate_not_activated", 409, 409);
        var path = Path.Combine(root, "config", "config.inc.php");
        if (!File.Exists(path)) return null;
        using var lease = await MailCertificateLifecycle.EnterAsync(path, token);
        var config = await File.ReadAllTextAsync(path, token);
        if (!config.Contains(WebmailTlsConfiguration.Marker, StringComparison.OrdinalIgnoreCase)) return null;
        var peers = Regex.Matches(config, @"'peer_name'\s*=>\s*'(?<host>[A-Za-z0-9.-]+)'")
            .Select(match => match.Groups["host"].Value.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        if (peers.Length > 1 || peers.Length == 0 && config.Contains("peer_name", StringComparison.Ordinal))
            throw new AppException("mail.certificate_not_activated", 409, 409);
        return peers.SingleOrDefault();
    }

    private async Task<MailCertificateState[]> ReadCertificateStatesAsync(CancellationToken token) =>
        (await MailCertificateRegistry.ReadAsync(fileStore, token)).Records
            .Select(MailCertificateRegistry.Deserialize<MailCertificateState>).ToArray();

    private static void ThrowCertificateFailures(List<Exception> failures)
    {
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Multiple mail certificates could not be synchronized; successful hosts were preserved.", failures);
    }

    private sealed record CertificateInventoryRecord(string Id, string[] Names);

    private sealed record CertificateSettings(string DefaultHostname, string DefaultCertificateId);

    private static CertificateSettings ReadCertificateSettings(MailQueryResult query,
        string errorKey = "mail.certificate_push_failed")
    {
        AppException InvalidSettings() => new(errorKey, 502, 502,
            "SystemSettings certificate selection is incomplete or invalid.");
        if (!query.Measured || query.Items.Count != 1) throw InvalidSettings();
        var item = query.Items[0];
        if (item.ValueKind != JsonValueKind.Object
            || item.EnumerateObject().Count(p => p.NameEquals("defaultHostname")) != 1
            || item.EnumerateObject().Count(p => p.NameEquals("defaultCertificateId")) > 1)
            throw InvalidSettings();
        var host = item.GetProperty("defaultHostname");
        if (host.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(host.GetString()))
            throw InvalidSettings();
        // The optional certificate ID can be omitted/null; preserve legacy empty strings.
        // A malformed value must never be converted to "no default" and overwritten.
        var id = "";
        if (item.TryGetProperty("defaultCertificateId", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind != JsonValueKind.String) throw InvalidSettings();
            id = value.GetString()!;
            if (id.Length > 0 && string.IsNullOrWhiteSpace(id)) throw InvalidSettings();
        }
        return new CertificateSettings(host.GetString()!, id);
    }

    private static CertificateInventoryRecord[] ReadCertificateInventory(IReadOnlyList<JsonElement> items)
    {
        static AppException InvalidMetadata() => new("mail.certificate_push_failed", 502, 502,
            "Certificate inventory metadata is incomplete or invalid.");

        var result = new List<CertificateInventoryRecord>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        // Validate the entire inventory before selecting a target. An unreadable unrelated
        // record can hide a shared SAN and must not authorize creating or replacing a certificate.
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object
                || item.EnumerateObject().Count(p => p.NameEquals("id")) != 1
                || item.EnumerateObject().Count(p => p.NameEquals("subjectAlternativeNames")) != 1)
                throw InvalidMetadata();
            var idValue = item.GetProperty("id");
            if (idValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(idValue.GetString())
                || !ids.Add(idValue.GetString()!)) throw InvalidMetadata();
            var names = item.GetProperty("subjectAlternativeNames");
            string[] parsedNames;
            if (names.ValueKind == JsonValueKind.Object)
            {
                // Stalwart Set<String> uses { "name": true }; retain legacy array support below.
                var members = names.EnumerateObject().ToArray();
                if (members.Any(p => string.IsNullOrWhiteSpace(p.Name) || p.Value.ValueKind != JsonValueKind.True)
                    || members.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != members.Length)
                    throw InvalidMetadata();
                parsedNames = members.Select(p => p.Name).ToArray();
            }
            else if (names.ValueKind == JsonValueKind.Array)
            {
                var members = names.EnumerateArray().ToArray();
                if (members.Any(p => p.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(p.GetString())))
                    throw InvalidMetadata();
                parsedNames = members.Select(p => p.GetString()!).ToArray();
            }
            else throw InvalidMetadata();
            result.Add(new CertificateInventoryRecord(idValue.GetString()!, parsedNames));
        }
        return result.ToArray();
    }

    internal static string? SelectCertificateId(IReadOnlyList<JsonElement> items, string host, X509Certificate2 replacement)
    {
        var replacementNames = replacement.Extensions.OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(extension => extension.EnumerateDnsNames()).ToArray();
        var records = ReadCertificateInventory(items);
        var matches = records.Where(item => item.Names.Any(name => CertificateNameMatches(name, host))).ToArray();
        // Never silently choose one of several SNI matches, or strip names from a shared certificate.
        if (matches.Length > 1 || matches.Any(item => item.Names.Any(name =>
            name.Contains('*') ? !replacementNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                : !replacement.MatchesHostname(name, true, false))))
            throw new AppException("mail.certificate_selection_conflict", 409, 409, host);
        // Additional SANs must not collide with another domain's existing certificate.
        if (records.Except(matches).Any(item => item.Names.Any(name => replacementNames.Any(candidate =>
            CertificateNameMatches(name, candidate) || CertificateNameMatches(candidate, name)))))
            throw new AppException("mail.certificate_selection_conflict", 409, 409, host);
        if (matches.Length == 0) return null;
        return matches[0].Id;
    }

    internal static bool CertificateNameMatches(string name, string host)
    {
        if (string.Equals(name, host, StringComparison.OrdinalIgnoreCase)) return true;
        // A DNS wildcard covers exactly one leftmost label, never the apex or nested names.
        return name.StartsWith("*.", StringComparison.Ordinal) && !host.Contains('*')
            && host.IndexOf('.') > 0
            && string.Equals(name[1..], host[host.IndexOf('.')..], StringComparison.OrdinalIgnoreCase);
    }
}
