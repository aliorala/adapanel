using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Infrastructure.Mail;

public sealed partial class MailEnableWebmail
{
    internal const string ManagedStatePath = "config/mailenable-webmail-certificates.json";
    private static readonly SemaphoreSlim ManagementGate = new(1, 1);
    internal sealed record StoredCertificate(string Thumbprint, DateTime NotAfterUtc, string StoreName = "My");
    internal sealed record ManagedCertificate(string Host, long SiteId, string Site, string Root, string Pool, string LoginPath,
        string Identity, int Port, string BindingInformation, string Thumbprint, string PreviousThumbprint,
        string PendingThumbprint, string Status, string Revision);
    internal Func<string, string?, StoredCertificate?> StoreCertificateLookup { get; init; } = FindCertificate;
    internal Func<string, string, CancellationToken, Task<CommandResult>> IssueCommand { get; init; } =
        (exe, args, token) => ProcessRunner.RunAsync(exe, args, cancellationToken: token, timeout: TimeSpan.FromMinutes(10));
    internal Func<string, CancellationToken, Task<CommandResult>> BindingCommand { get; init; } = ProcessRunner.RunPowerShellAsync;
    internal Func<CancellationToken, Task<string>>? ExecutableLookup { get; init; }
    internal Func<string>? ActiveProvider { get; init; }
    private readonly IConcurrencyLock webmailGates = concurrencyLock ?? new ConcurrencyLock();
    private JsonFileStore Files => fileStore ?? throw NotReady();
    private string CurrentProvider => ActiveProvider?.Invoke() ?? providerSelection?.Current ?? throw NotReady();

    public Task SecureForDomainAsync(string domain, string contactEmail, CancellationToken token = default) =>
        SecureForDomainAsync(domain, contactEmail, null, token);

    public async Task SecureForDomainAsync(string domain, string contactEmail, Func<CancellationToken, Task>? beforeMutation,
        CancellationToken token = default)
    {
        var normalized = (domain ?? "").Trim();
        if (normalized.StartsWith("*.", StringComparison.Ordinal)) normalized = normalized[2..];
        var host = NormalizeHost(normalized.StartsWith("webmail.", StringComparison.OrdinalIgnoreCase) ? normalized : "webmail." + normalized);
        await ManagementGate.WaitAsync(token);
        try
        {
            RequireProvider();
            var records = await ReadRecordsAsync(token);
            var existing = records.SingleOrDefault(r => r.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
            await ApplyAsync(host, contactEmail, existing, true, beforeMutation, token);
        }
        finally { ManagementGate.Release(); }
    }

    internal async Task<IReadOnlyList<string>> SyncManagedAsync(Func<CancellationToken, Task>? beforeMutation, CancellationToken token)
    {
        await ManagementGate.WaitAsync(token);
        try
        {
            ManagedCertificate[] records;
            try { records = await ReadRecordsAsync(token); }
            catch (Exception error) when (error is not OperationCanceledException)
            { return ["MailEnable webmail managed state is unreadable: " + SafeError(error)]; }
            if (records.Length == 0) return [];
            if (CurrentProvider != "mailenable") return ["MailEnable webmail synchronization skipped: the active provider changed."];
            var failures = new List<string>();
            var rejected = false;
            async Task Approve(CancellationToken ct)
            {
                if (beforeMutation is null) return;
                try { await beforeMutation(ct); }
                catch { rejected = true; throw; }
            }
            foreach (var record in records)
            {
                token.ThrowIfCancellationRequested();
                try { await ApplyAsync(record.Host, "", record, false, Approve, token); }
                catch (Exception error) when (!rejected && error is not OperationCanceledException)
                { failures.Add(record.Host + ": " + SafeError(error)); }
            }
            return failures;
        }
        finally { ManagementGate.Release(); }
    }

    private async Task ApplyAsync(string host, string contactEmail, ManagedCertificate? record, bool allowIssue,
        Func<CancellationToken, Task>? beforeMutation, CancellationToken token)
    {
        if (iisSiteService is null) throw NotReady();
        var selected = Choose(await ReadInventoryAsync(token), host);
        if (record is not null) AssertRecordedIdentity(record, selected);
        using var generation = await SiteMutationGuard.CaptureAsync(iisSiteService, webmailGates, selected.Site.Name, token);
        if (generation.Site.Id != selected.Site.Id || !SamePath(generation.Site.PhysicalPath, selected.Root)
            || !generation.Site.ApplicationPoolName.Equals(selected.Pool, StringComparison.OrdinalIgnoreCase)) throw Changed();
        using var reservation = await generation.ReserveAsync(token);
        var endpoints = ExactEndpoints(selected, host);
        var endpoint = endpoints.SingleOrDefault();
        if (record is not null) AssertOwnedBinding(record, endpoints);
        var port = record?.Port ?? endpoint?.Port ?? 443;
        var information = record?.BindingInformation ?? endpoint?.BindingInformation ?? $"*:{port}:{host}";
        var previous = endpoint?.Thumbprint ?? "";
        var expectedHashes = record is null ? new[] { previous } : AllowedHashes(record);
        var requireExact = false;
        var latest = selected;
        async Task ValidateCore(CancellationToken ct)
        {
            RequireProvider();
            var current = Choose(await ReadInventoryAsync(ct), host);
            AssertIdentity(selected, current);
            var measured = ExactEndpoints(current, host);
            if (requireExact)
            {
                if (measured.Length != 1 || measured[0].Port != port || !measured[0].BindingInformation.Equals(information, StringComparison.OrdinalIgnoreCase)
                    || !expectedHashes.Contains(measured[0].Thumbprint, StringComparer.OrdinalIgnoreCase)) throw Changed();
            }
            else AssertAllowed(measured, port, information, expectedHashes);
            if (beforeMutation is not null) await beforeMutation(ct);
            ct.ThrowIfCancellationRequested();
            latest = current;
        }
        Task Approve(CancellationToken ct) => generation.RunAsync(_ => ValidateCore(ct), ct);
        await Approve(token);

        var currentCertificate = endpoint is not null ? StoreCertificateLookup(host, endpoint.Thumbprint) : null;
        var certificate = StoreCertificateLookup(host, null);
        if (currentCertificate is not null && (record is null || record.Status == "confirmed")
            && (allowIssue || certificate?.Thumbprint.Equals(currentCertificate.Thumbprint, StringComparison.OrdinalIgnoreCase) == true))
        {
            await EndpointProbe(endpoint!, token);
            expectedHashes = [currentCertificate.Thumbprint]; requireExact = true;
            var adopted = NewRecord(selected, host, port, information, currentCertificate.Thumbprint, previous, "", "confirmed");
            await SaveRecordAsync(adopted, record?.Revision, Approve, token);
            return;
        }
        if (certificate is null && allowIssue)
        {
            var contact = contactEmail?.Trim() ?? "";
            if (contact.Length == 0) contact = (await Files.ReadAsync<AcmeContactSettings?>(AcmeContactSettings.Path, null, token))?.Email?.Trim() ?? "";
            if (!AcmeArgumentRules.IsCommandSafeEmail(contact)) throw Failure("A valid ACME contact email is required.");
            var executable = await ResolveExecutableAsync(token);
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) throw Failure("win-acme is not installed.");
            await Approve(token);
            var arguments = IssuanceArguments(host, contact);
            var issued = await IssueCommand(executable, arguments, token);
            token.ThrowIfCancellationRequested();
            if (!issued.Success) throw Failure("win-acme self-hosting issuance failed.");
            certificate = StoreCertificateLookup(host, null);
        }
        if (certificate is null) throw Failure("No trusted LocalMachine server certificate with a private key is available.");
        var pending = NewRecord(selected, host, port, information, record?.Thumbprint ?? "", previous, certificate.Thumbprint, "pending");
        await SaveRecordAsync(pending, record?.Revision, Approve, token);
        await generation.RunAsync(async _ =>
        {
            await ValidateCore(token);
            var nativeBinding = latest.Site.Bindings.SingleOrDefault(b => b.Protocol.Equals("https", StringComparison.OrdinalIgnoreCase)
                && b.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
            var expected = new SslBindingMutation.ExpectedBinding(latest.Site.Id, latest.Root, latest.Pool,
                nativeBinding?.Information ?? information, nativeBinding?.Thumbprint ?? "", nativeBinding?.StoreName ?? "",
                nativeBinding?.Flags ?? 0, certificate.StoreName, MustBeAbsent: nativeBinding is null);
            var applied = await BindingCommand(NativeIdentityGuard(latest.Site,
                Path.Combine(latest.Root, latest.LoginPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar))) + "\n" +
                SslBindingMutation.Script(selected.Site.Name, host, port, certificate.Thumbprint, expected), token);
            token.ThrowIfCancellationRequested();
            if (!applied.Success || applied.StandardOutput.Trim() != "OK") throw Failure("The WebMail certificate binding was not verified.");
            expectedHashes = [certificate.Thumbprint]; requireExact = true;
            await ValidateCore(token);
        }, token);
        var secured = new Endpoint(selected.Site.Name, host, port, selected.LoginPath, selected.Site.Id, certificate.Thumbprint, information);
        await EndpointProbe(secured, token);
        await SaveRecordAsync(pending with { Thumbprint=certificate.Thumbprint, PendingThumbprint="", Status="confirmed", Revision=Guid.NewGuid().ToString("N") },
            pending.Revision, Approve, token);
    }

    private static ManagedCertificate NewRecord(TargetSite target, string host, int port, string information, string thumb,
        string previous, string pending, string status) => new(host, target.Site.Id, target.Site.Name, target.Root, target.Pool,
        target.LoginPath, target.Identity, port, information, thumb, previous, pending, status, Guid.NewGuid().ToString("N"));
    private static Endpoint[] ExactEndpoints(TargetSite target, string host)
    {
        var bindings = target.Site.Bindings.Where(b => b.Protocol.Equals("https", StringComparison.OrdinalIgnoreCase) && b.Host.Equals(host, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (bindings.Length > 1) throw Failure("The exact WebMail HTTPS endpoint is ambiguous.");
        return bindings.Select(b => new Endpoint(target.Site.Name, host, b.Port, target.LoginPath, target.Site.Id, b.Thumbprint, b.Information)).ToArray();
    }
    private static void AssertIdentity(TargetSite expected, TargetSite actual)
    {
        if (expected.Site.Id != actual.Site.Id || !expected.Site.Name.Equals(actual.Site.Name, StringComparison.OrdinalIgnoreCase)
            || !SamePath(expected.Root, actual.Root) || expected.Identity != actual.Identity || expected.LoginPath != actual.LoginPath
            || !expected.Pool.Equals(actual.Pool, StringComparison.OrdinalIgnoreCase)) throw Changed();
    }
    private static void AssertRecordedIdentity(ManagedCertificate record, TargetSite actual)
    {
        if (record.SiteId != actual.Site.Id || !record.Site.Equals(actual.Site.Name, StringComparison.OrdinalIgnoreCase)
            || !SamePath(record.Root, actual.Root) || record.Identity != actual.Identity || record.LoginPath != actual.LoginPath
            || !record.Pool.Equals(actual.Pool, StringComparison.OrdinalIgnoreCase)) throw Changed();
    }
    private static string[] AllowedHashes(ManagedCertificate record) => record.Status == "pending"
        ? [record.Thumbprint, record.PreviousThumbprint, record.PendingThumbprint] : [record.Thumbprint];
    private static void AssertOwnedBinding(ManagedCertificate record, Endpoint[] endpoints) =>
        AssertAllowed(endpoints, record.Port, record.BindingInformation, AllowedHashes(record));
    private static void AssertAllowed(Endpoint[] endpoints, int port, string information, string[] hashes)
    {
        if (endpoints.Length == 0) { if (!hashes.Contains("")) throw Changed(); return; }
        if (endpoints.Length != 1 || endpoints[0].Port != port || !endpoints[0].BindingInformation.Equals(information, StringComparison.OrdinalIgnoreCase)
            || !hashes.Contains(endpoints[0].Thumbprint, StringComparer.OrdinalIgnoreCase)) throw Changed();
    }
    private static bool SamePath(string left, string right) => FullPath(left).Equals(FullPath(right), StringComparison.OrdinalIgnoreCase);
    private void RequireProvider() { if (CurrentProvider != "mailenable") throw Failure("MailEnable is no longer the active provider."); }
    private static AppException Failure(string detail) => new("mail.webmail.mailenable_not_ready", 409, 409, detail);
    private static AppException Changed() => Failure("The managed WebMail site, application or certificate changed externally.");
    private static string SafeError(Exception error) => error is AppException app ? app.MessageKey + ": " + app.Details : error.GetType().Name;
    private static string NormalizeHost(string host)
    {
        try { host = new IdnMapping().GetAscii(host.Trim().TrimEnd('.')).ToLowerInvariant(); }
        catch (ArgumentException) { throw Failure("Invalid WebMail hostname."); }
        if (!AcmeArgumentRules.IsCommandSafeHost(host) || !host.Contains('.') || host.Contains('*') || Uri.CheckHostName(host) != UriHostNameType.Dns)
            throw Failure("Invalid WebMail hostname.");
        return host;
    }
    internal static string IssuanceArguments(string host, string contact)
    {
        if (NormalizeHost(host) != host || !AcmeArgumentRules.IsCommandSafeEmail(contact)) throw Failure("Invalid ACME arguments.");
        return $"--source manual --host \"{host}\" --validation selfhosting --store certificatestore --certificatestore My --keepexisting --installation none --emailaddress \"{contact}\" --accepttos --baseuri https://acme-v02.api.letsencrypt.org/";
    }
    private async Task<string> ResolveExecutableAsync(CancellationToken token)
    {
        if (ExecutableLookup is not null) return await ExecutableLookup(token);
        if (dbContext is null) return "";
        var component = await dbContext.InstalledComponents.FirstOrDefaultAsync(c => c.ComponentId == "win-acme" && c.Status == ComponentInstallStatus.Installed, token);
        return component is not null && Directory.Exists(component.InstallDir)
            ? Directory.GetFiles(component.InstallDir, "wacs.exe", SearchOption.AllDirectories).FirstOrDefault() ?? "" : "";
    }

    private async Task<ManagedCertificate[]> ReadRecordsAsync(CancellationToken token)
    {
        var records = await Files.ReadAsync<ManagedCertificate[]?>(ManagedStatePath, null, token);
        if (records is null)
        {
            if (File.Exists(Files.ResolvePath(ManagedStatePath))) throw Failure("Managed WebMail state is unreadable.");
            return [];
        }
        ValidateRecords(records);
        return records;
    }
    private static void ValidateRecords(ManagedCertificate[] records)
    {
        if (records.Any(r => r is null || r.Host is null || NormalizeHost(r.Host) != r.Host || r.SiteId <= 0
            || string.IsNullOrWhiteSpace(r.Site) || string.IsNullOrWhiteSpace(r.Root) || string.IsNullOrWhiteSpace(r.Pool)
            || string.IsNullOrWhiteSpace(r.LoginPath) || !r.LoginPath.StartsWith('/') || string.IsNullOrWhiteSpace(r.Identity)
            || r.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(r.BindingInformation) || r.Thumbprint is null || r.PreviousThumbprint is null || r.PendingThumbprint is null
            || r.Status is not ("pending" or "confirmed") || !Guid.TryParseExact(r.Revision, "N", out _)
            || (r.Status == "confirmed" && r.Thumbprint.Length == 0) || (r.Status == "pending" && r.PendingThumbprint.Length == 0))
            || records.Select(r => r.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count() != records.Length) throw Failure("Managed WebMail state is unreadable.");
    }
    private async Task SaveRecordAsync(ManagedCertificate next, string? expectedRevision, Func<CancellationToken, Task> approve, CancellationToken token)
    {
        await approve(token);
        await Files.UpdateAsync<ManagedCertificate[]?>(ManagedStatePath, null, records =>
        {
            if (records is null && File.Exists(Files.ResolvePath(ManagedStatePath))) throw Failure("Managed WebMail state is unreadable.");
            records ??= [];
            ValidateRecords(records);
            var current = records.SingleOrDefault(r => r.Host.Equals(next.Host, StringComparison.OrdinalIgnoreCase));
            if (current?.Revision != expectedRevision) throw Changed();
            return records.Where(r => !r.Host.Equals(next.Host, StringComparison.OrdinalIgnoreCase)).Append(next).ToArray();
        }, approve, token);
    }

    private static StoredCertificate? FindCertificate(string host, string? preferredThumbprint)
    {
        var candidates = new List<(X509Certificate2 Certificate, string Store)>();
        try
        {
            foreach (var name in new[] { "My", "WebHosting" })
            {
                using var store = new X509Store(name, StoreLocation.LocalMachine);
                try { store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly); }
                catch (CryptographicException error) when (name == "WebHosting" && error.HResult is unchecked((int)0x80092004) or unchecked((int)0x80070002)) { continue; }
                candidates.AddRange(store.Certificates.Cast<X509Certificate2>().Select(c => (c, name)));
            }
            foreach (var candidate in candidates.OrderByDescending(c => c.Certificate.NotAfter))
            {
                var certificate = candidate.Certificate;
                if (preferredThumbprint is not null && !certificate.Thumbprint.Equals(preferredThumbprint, StringComparison.OrdinalIgnoreCase)) continue;
                if (ValidCertificate(certificate, host)) return new(certificate.Thumbprint, certificate.NotAfter.ToUniversalTime(), candidate.Store);
            }
            return null;
        }
        finally { foreach (var candidate in candidates) candidate.Certificate.Dispose(); }
    }
    internal static bool ValidCertificate(X509Certificate2 certificate, string host, Action<X509ChainPolicy>? configureTrust = null)
    {
        if (!certificate.HasPrivateKey || !SslCertificateSelection.MatchesHost(certificate, host)
            || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(5);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        configureTrust?.Invoke(chain.ChainPolicy);
        return chain.Build(certificate);
    }
}
