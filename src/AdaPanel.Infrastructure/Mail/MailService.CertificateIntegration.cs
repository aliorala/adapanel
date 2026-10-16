using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;

namespace AdaPanel.Infrastructure.Mail;

public sealed partial class MailService
{
    internal Func<string, MailCertificateMaterial?> StoreCertificateLookup { get; init; } = MailCertificateStore.Find;
    internal Action<string, string, string> ValidateCertificateMaterial { get; init; } = MailCertificateValidation.Validate;
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
        var state = await fileStore.ReadAsync<MailCertificateState?>(CertificateStatePath, null, cancellationToken);
        var trackedHost = state is not null && CertificateNameMatches(hostName, state.HostName);
        if (!trackedHost && !hostName.StartsWith("mail.", StringComparison.OrdinalIgnoreCase)) return;
        // A wildcard operation targets only the already configured mail host it covers.
        // Custom names such as smtp.example.test keep their saved domain association.
        var domain = trackedHost ? state!.Domain : hostName[5..];
        hostName = trackedHost ? state!.HostName : hostName.Trim().ToLowerInvariant();
        var material = StoreCertificateLookup(hostName)
            ?? throw new AppException("mail.certificate_store_missing", 409, 409);
        if (state?.HostName == hostName && state.Thumbprint == material.Thumbprint)
        {
            var probes = await Task.WhenAll(new[] { 587, 465 }.Select(port => TlsProbe(hostName, port, material.Thumbprint, cancellationToken)));
            if (probes.All(p => p.Status == "ok")) return;
        }
        await ApplyStoreCertificateAsync(domain, hostName, material, cancellationToken, beforeMutation);
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
            if (!records.Measured || !settings.Measured || settings.Items.Count != 1) return;
            var matches = ReadCertificateInventory(records.Items)
                .Where(item => item.Names.Any(name => CertificateNameMatches(name, dto.HostName))).ToArray();
            dto.CertificateId = string.Join(", ", matches.Select(item => item.Id));
            dto.DefaultCertificateId = GetField(settings.Items[0], "defaultCertificateId");
            dto.ConfigurationStatus = matches.Length == 1 ? "ok" : matches.Length == 0 ? "missing" : "ambiguous";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is AppException or IOException or System.ComponentModel.Win32Exception)
        { dto.ConfigurationStatus = "unknown"; }
    }

    private async Task<MailCertificateDto> ApplyStoreCertificateAsync(string domain, string host,
        MailCertificateMaterial material, CancellationToken token, Func<CancellationToken, Task>? beforeMutation = null)
    {
        var previous = await fileStore.ReadAsync<MailCertificateState?>(CertificateStatePath, null, token);
        if (previous?.HostName == host && previous.Thumbprint == material.Thumbprint)
        {
            var current = new MailCertificateDto { Domain = domain, HostName = host, Source = previous.Source, NotAfterUtc = previous.NotAfterUtc };
            await InspectMailCertificateAsync(current, material.Thumbprint, token);
            if (current.Active) return current;
        }
        await PushCertificateToStalwartAsync(host, material.CertificatePem, material.PrivateKeyPem, token, beforeMutation);
        var state = new MailCertificateState
        {
            Domain = domain, HostName = host, Source = "windows-store", Thumbprint = material.Thumbprint,
            ContentHash = ComputeHash(material.CertificatePem + material.PrivateKeyPem),
            NotAfterUtc = TryGetNotAfterUtc(material.CertificatePem)
        };
        var dto = new MailCertificateDto { Domain = domain, HostName = host, Source = state.Source, NotAfterUtc = state.NotAfterUtc };
        await InspectMailCertificateAsync(dto, state.Thumbprint, token);
        if (!dto.Active) throw new AppException("mail.certificate_not_activated", 502, 502, host);
        await fileStore.WriteAsync(CertificateStatePath, state, token);
        return dto;
    }

    private sealed record CertificateInventoryRecord(string Id, string[] Names);

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
