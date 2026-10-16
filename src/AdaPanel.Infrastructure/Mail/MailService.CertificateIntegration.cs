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
        if (!hostName.StartsWith("mail.", StringComparison.OrdinalIgnoreCase) || await ResolveCliAsync(cancellationToken) is null)
            return;
        // An explicit web certificate operation for a mail hostname reuses the trusted store
        // certificate. It never issues another certificate or changes unrelated mail settings.
        using var operation = await MailCertificateLifecycle.EnterAsync(fileStore.ResolvePath(CertificateStatePath), cancellationToken);
        if (beforeMutation is not null) await beforeMutation(cancellationToken);
        var material = StoreCertificateLookup(hostName)
            ?? throw new AppException("mail.certificate_store_missing", 409, 409);
        var state = await fileStore.ReadAsync<MailCertificateState?>(CertificateStatePath, null, cancellationToken);
        if (state?.HostName == hostName && state.Thumbprint == material.Thumbprint)
        {
            var probes = await Task.WhenAll(new[] { 587, 465 }.Select(port => TlsProbe(hostName, port, material.Thumbprint, cancellationToken)));
            if (probes.All(p => p.Status == "ok")) return;
        }
        await ApplyStoreCertificateAsync(hostName[5..], hostName, material, cancellationToken, beforeMutation);
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
            var matches = records.Items.Where(item => CertificateNames(item).Contains(dto.HostName, StringComparer.OrdinalIgnoreCase)).ToArray();
            dto.CertificateId = string.Join(", ", matches.Select(item => GetField(item, "id")));
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
        await fileStore.WriteAsync(CertificateStatePath, state, token);
        var dto = new MailCertificateDto { Domain = domain, HostName = host, Source = state.Source, NotAfterUtc = state.NotAfterUtc };
        await InspectMailCertificateAsync(dto, state.Thumbprint, token);
        if (!dto.Active) throw new AppException("mail.certificate_not_activated", 502, 502, host);
        return dto;
    }

    internal static string[] CertificateNames(JsonElement item)
    {
        if (!item.TryGetProperty("subjectAlternativeNames", out var names)) return [];
        return names.ValueKind switch
        {
            JsonValueKind.Object => names.EnumerateObject().Select(p => p.Name).ToArray(),
            JsonValueKind.Array => names.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToArray(),
            _ => []
        };
    }

    internal static string? SelectCertificateId(IReadOnlyList<JsonElement> items, string host, X509Certificate2 replacement)
    {
        var matches = items.Where(item => CertificateNames(item).Contains(host, StringComparer.OrdinalIgnoreCase)).ToArray();
        // Never silently choose one of several SNI matches, or strip names from a shared certificate.
        if (matches.Length > 1 || matches.Any(item => CertificateNames(item).Any(name =>
            name.Contains('*') || !replacement.MatchesHostname(name, false, false))))
            throw new AppException("mail.certificate_selection_conflict", 409, 409, host);
        if (matches.Length == 0) return null;
        var id = GetField(matches[0], "id");
        if (string.IsNullOrWhiteSpace(id)) throw new AppException("mail.certificate_push_failed", 502, 502);
        return id;
    }
}
