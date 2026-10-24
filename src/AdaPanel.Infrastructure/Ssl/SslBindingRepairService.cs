using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.DataProtection;

namespace AdaPanel.Infrastructure.Ssl;

/// <summary>Explicit, short-lived repair plans. Never issues, imports or selects a global "best" certificate.</summary>
public sealed class SslBindingRepairService(JsonFileStore files, IIisSiteService sites,
    IConcurrencyLock gates, IDataProtectionProvider protection)
{
    private const string StatePath = "config/ssl.json";
    private readonly IDataProtector protector = protection.CreateProtector("AdaPanel.SslBindingRepair.v1");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal Func<string, CancellationToken, Task<CommandResult>> Command { get; init; } = ProcessRunner.RunPowerShellAsync;
    internal Func<string, string, string, CertificateEvidence?> CertificateReader { get; init; } = ReadCertificate;
    internal Func<BindingSnapshot, string, CancellationToken, Task<bool>> Probe { get; init; } = ProbeAsync;

    public async Task<SslRepairPlan> PlanAsync(SslRepairTarget request, string actor,
        Func<SiteDto, CancellationToken, Task> authorize, CancellationToken token)
    {
        ValidateTarget(request);
        using var target = await SiteMutationGuard.CaptureAsync(sites, gates, request.SiteName, token);
        await authorize(target.Site, token);
        var record = await ReadRecordAsync(request, token);
        var inventory = await ReadInventoryAsync(token);
        var options = new List<SslRepairOption>();
        foreach (var binding in inventory.Where(b => Same(b.SiteName, request.SiteName) && b.Port == request.Port))
        {
            // A wildcard record is not a wildcard mutation: each concrete SNI binding gets its own plan.
            if (!request.HostName.StartsWith("*.", StringComparison.Ordinal) && !Same(binding.HostName, request.HostName)) continue;
            if (string.IsNullOrWhiteSpace(binding.HostName) || binding.HostName.Contains('*')) continue;
            var candidate = CertificateReader(record.Thumbprint, binding.HostName, Same(record.Thumbprint, binding.Thumbprint) ? binding.StoreName : "");
            if (candidate is null || !candidate.MatchesHost) continue;
            var current = CertificateReader(binding.Thumbprint, binding.HostName, binding.StoreName);
            var blocked = BlockReason(binding, inventory, candidate, current);
            var plan = new ProtectedPlan(actor, DateTime.UtcNow.AddMinutes(5), request, record.Thumbprint,
                target.Site, binding, candidate.StoreName);
            options.Add(new(binding.HostName, binding.Information, current?.Summary,
                candidate.Summary, blocked.Length == 0 ? protector.Protect(JsonSerializer.Serialize(plan, Json)) : "", blocked));
        }
        await target.RunAsync(current => authorize(current, token), token);
        return new(request.SiteName, request.HostName, options);
    }

    public async Task<SslRepairResult> ApplyAsync(string planToken, string actor,
        Func<SiteDto, CancellationToken, Task> authorize, CancellationToken token)
    {
        ProtectedPlan plan;
        try
        {
            if (string.IsNullOrEmpty(planToken) || planToken.Length > 32768) throw Changed();
            plan = JsonSerializer.Deserialize<ProtectedPlan>(protector.Unprotect(planToken), Json) ?? throw Changed();
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException) { throw Changed(); }
        if (plan.Actor != actor || plan.ExpiresUtc <= DateTime.UtcNow) throw Changed();
        ValidateTarget(plan.Target);
        using var target = await SiteMutationGuard.CaptureAsync(sites, gates, plan.Target.SiteName, token);
        target.ValidateSnapshot(plan.Site);
        await authorize(target.Site, token);
        // Chain/revocation lookup is outside the global IIS gate. Revalidated again just before binding.
        var candidate = CertificateReader(plan.ExpectedThumbprint, plan.Binding.HostName, plan.CandidateStore);
        var old = CertificateReader(plan.Binding.Thumbprint, plan.Binding.HostName, plan.Binding.StoreName);
        if (candidate is null || !candidate.Summary.Valid || !Same(candidate.StoreName, plan.CandidateStore)) throw Changed();
        await target.RunAsync(async current =>
        {
            await authorize(current, token);
            var record = await ReadRecordAsync(plan.Target, token);
            if (!Same(record.Thumbprint, plan.ExpectedThumbprint)) throw Changed();
            var inventory = await ReadInventoryAsync(token);
            var exact = inventory.Where(b => Same(b.SiteName, plan.Binding.SiteName) && Same(b.Information, plan.Binding.Information)).ToArray();
            if (exact.Length != 1 || !SameSnapshot(exact[0], plan.Binding) || BlockReason(exact[0], inventory, candidate, old).Length != 0) throw Changed();
            token.ThrowIfCancellationRequested();
            await authorize(current, token);
            if (plan.ExpiresUtc <= DateTime.UtcNow || candidate.Summary.NotAfterUtc <= DateTime.UtcNow) throw Changed();
            var expected = new SslBindingMutation.ExpectedBinding(plan.Site.Id, plan.Site.PhysicalPath,
                plan.Site.ApplicationPoolName, plan.Binding.Information, plan.Binding.Thumbprint, plan.Binding.StoreName, plan.Binding.Flags, plan.CandidateStore);
            var result = await Command(SslBindingMutation.Script(plan.Target.SiteName, plan.Binding.HostName,
                plan.Binding.Port, plan.ExpectedThumbprint, expected), token);
            if (!result.Success || result.StandardOutput.Trim() != "OK") throw new AppException("ssl.repair.apply_failed", 502, 502);
        }, token);

        // Verify the actual local IIS TLS endpoint with SNI, normal chain/name/time validation, and the exact hash.
        var tlsVerified = await Probe(plan.Binding, plan.ExpectedThumbprint, token);
        var measured = await ReadInventoryAsync(token);
        var coveredHosts = measured.Where(b => Same(b.SiteName, plan.Target.SiteName) && b.Port == plan.Target.Port
                && !string.IsNullOrWhiteSpace(b.HostName) && !b.HostName.Contains('*'))
            .Select(b => b.HostName).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(host => CertificateReader(plan.ExpectedThumbprint, host, plan.CandidateStore)?.MatchesHost == true)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bindingVerified = false;
        await target.RunAsync(async current =>
        {
            await authorize(current, token);
            var fresh = await ReadInventoryAsync(token);
            if (fresh.Count != measured.Count || measured.Any(b => !fresh.Any(other => SameSnapshot(b, other)))) return;
            var exact = fresh.Where(b => Same(b.SiteName, plan.Binding.SiteName) && Same(b.Information, plan.Binding.Information)).ToArray();
            bindingVerified = exact.Length == 1 && Same(exact[0].Thumbprint, plan.ExpectedThumbprint)
                && exact[0].Flags == plan.Binding.Flags && Same(exact[0].StoreName, plan.CandidateStore);
            if (!bindingVerified) return;
            await files.UpdateAsync<SslCertificateDto[]?>(StatePath, null, records =>
            {
                if (records is null) throw Changed();
                var record = FindRecord(records, plan.Target);
                if (!Same(record.Thumbprint, plan.ExpectedThumbprint)) throw Changed();
                // Wildcard/apex records for this certificate share measurements, not a blind success flag.
                foreach (var affected in records.Where(r => Same(r.SiteName, plan.Target.SiteName)
                    && (r.Port > 0 ? r.Port : 443) == plan.Target.Port && Same(r.Thumbprint, plan.ExpectedThumbprint)))
                {
                    var bindings = fresh.Where(b => Same(b.SiteName, affected.SiteName) && b.Port == plan.Target.Port
                        && (affected.HostName.StartsWith("*.", StringComparison.Ordinal)
                            ? coveredHosts.Contains(b.HostName) : Same(b.HostName, affected.HostName))).ToArray();
                    affected.IsHttpsBindingPresent = bindings.Length > 0;
                    affected.IsCertificateBound = bindings.Length > 0 && bindings.All(b => Same(b.Thumbprint, plan.ExpectedThumbprint));
                    affected.BindingCheckedAtUtc = DateTime.UtcNow;
                    affected.BindingDetail = affected.IsCertificateBound == true ? "" : "Certificate differs on one or more covered IIS bindings.";
                }
                return records;
            }, ct => authorize(current, ct), token);
        }, token);
        return new(bindingVerified, bindingVerified && tlsVerified,
            !bindingVerified ? "ssl.repair.readback_failed" : tlsVerified ? "ssl.repair.verified" : "ssl.repair.tls_failed",
            plan.Target.SiteName, plan.Binding.Information, plan.Binding.Thumbprint, plan.ExpectedThumbprint, plan.CandidateStore);
    }

    internal sealed record ProtectedPlan(string Actor, DateTime ExpiresUtc, SslRepairTarget Target,
        string ExpectedThumbprint, SiteDto Site, BindingSnapshot Binding, string CandidateStore);
    internal sealed record BindingSnapshot(string SiteName, string HostName, string Information, string Ip,
        int Port, string Thumbprint, string StoreName, int Flags);
    internal sealed record CertificateEvidence(SslRepairCertificate Summary, string StoreName, bool MatchesHost, bool HasPrivateKey);

    internal static string BlockReason(BindingSnapshot binding, IReadOnlyList<BindingSnapshot> inventory,
        CertificateEvidence candidate, CertificateEvidence? current)
    {
        if (!candidate.Summary.Valid || !candidate.MatchesHost || !candidate.HasPrivateKey) return "ssl.repair.invalid_candidate";
        if (current is null || !current.HasPrivateKey || !Same(current.StoreName, binding.StoreName)) return "ssl.repair.no_rollback";
        if (binding.Flags < 0 || (binding.Flags & 2) != 0 || (binding.Flags & 1) == 0 || string.IsNullOrWhiteSpace(binding.HostName)) return "ssl.repair.unsupported_binding";
        // Reject duplicate host/port across IPs too; one SNI HTTP.sys key can be shared by those bindings.
        if (inventory.Count(b => b.Port == binding.Port && Same(b.HostName, binding.HostName)) != 1) return "ssl.repair.shared_binding";
        return "";
    }

    private async Task<SslCertificateDto> ReadRecordAsync(SslRepairTarget target, CancellationToken token) =>
        FindRecord(await files.ReadAsync<SslCertificateDto[]?>(StatePath, null, token) ?? throw Changed(), target);
    private static SslCertificateDto FindRecord(IEnumerable<SslCertificateDto> records, SslRepairTarget target)
    {
        var found = records.Where(r => Same(r.SiteName, target.SiteName) && Same(r.HostName, target.HostName)
            && (r.Port > 0 ? r.Port : 443) == target.Port).ToArray();
        if (found.Length != 1 || !Regex.IsMatch(found[0].Thumbprint, "\\A[0-9a-fA-F]{40}\\z")) throw Changed();
        return found[0];
    }
    private static void ValidateTarget(SslRepairTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.SiteName) || string.IsNullOrWhiteSpace(target.HostName) || target.Port is < 1 or > 65535)
            throw new AppException("ssl.repair.invalid_target", 400, 400);
    }
    private static AppException Changed() => new("ssl.repair.plan_changed", 409, 409);
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    internal static bool SameSnapshot(BindingSnapshot a, BindingSnapshot b) => Same(a.SiteName, b.SiteName)
        && Same(a.HostName, b.HostName) && Same(a.Information, b.Information) && Same(a.Thumbprint, b.Thumbprint)
        && Same(a.StoreName, b.StoreName) && a.Flags == b.Flags && a.Port == b.Port && Same(a.Ip, b.Ip);

    private static CertificateEvidence? ReadCertificate(string thumbprint, string host, string exactStore)
    {
        if (!Regex.IsMatch(thumbprint, "\\A[0-9a-fA-F]{40}\\z")) return null;
        if (Same(exactStore, "My")) exactStore = "My";
        else if (Same(exactStore, "WebHosting")) exactStore = "WebHosting";
        else if (exactStore.Length > 0) return null;
        foreach (var storeName in exactStore.Length > 0 ? new[] { exactStore } : new[] { "My", "WebHosting" })
        {
            using var store = new X509Store(storeName, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var all = store.Certificates;
            try
            {
                var certificate = all.Cast<X509Certificate2>().FirstOrDefault(c => Same(c.Thumbprint, thumbprint) && c.HasPrivateKey);
                if (certificate is null) continue;
                var matches = certificate.MatchesHostname(host, allowWildcards: true, allowCommonName: false);
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);
                chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
                var valid = matches && certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow
                    && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow && chain.Build(certificate);
                return new(new(certificate.Thumbprint, certificate.Subject, certificate.NotAfter.ToUniversalTime(), valid), storeName, matches, certificate.HasPrivateKey);
            }
            finally { foreach (var certificate in all) certificate.Dispose(); }
        }
        return null;
    }

    private async Task<IReadOnlyList<BindingSnapshot>> ReadInventoryAsync(CancellationToken token)
    {
        var result = await Command(InventoryScript, token);
        if (!result.Success) throw new AppException("ssl.repair.inventory_failed", 502, 502);
        try { return JsonSerializer.Deserialize<BindingSnapshot[]>(result.StandardOutput, Json) ?? throw Changed(); }
        catch (JsonException) { throw new AppException("ssl.repair.inventory_failed", 502, 502); }
    }
    internal const string InventoryScript = """
        Import-Module WebAdministration -ErrorAction Stop
        $ErrorActionPreference='Stop'
        $rows=@(foreach($s in @(Get-Website -ErrorAction Stop)) {
          foreach($b in @($s.Bindings.Collection)) {
            if($b.protocol -ne 'https'){continue}
            if([string]$b.bindingInformation -notmatch '^(.+):(\d+):([^:]*)$'){throw 'Invalid binding'}
            $ip=$matches[1]; $port=[int]$matches[2]; $hostName=$matches[3]
            $hash=$b.certificateHash
            if($hash -is [byte[]]){$hash=[BitConverter]::ToString($hash).Replace('-','')}
            if($hash -isnot [string] -or $hash -notmatch '^[0-9a-fA-F]{40}$' -or $null -eq $b.sslFlags){throw 'Unreadable binding'}
            [pscustomobject]@{siteName=[string]$s.Name; hostName=$hostName; information=[string]$b.bindingInformation; ip=$ip;
              port=$port; thumbprint=$hash; storeName=[string]$b.certificateStoreName; flags=[int]$b.sslFlags}
          }
        })
        ConvertTo-Json -InputObject $rows -Depth 4 -Compress
        """;

    internal static async Task<bool> ProbeAsync(BindingSnapshot binding, string thumbprint, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var ip = binding.Ip is "*" or "0.0.0.0" ? IPAddress.Loopback : IPAddress.Parse(binding.Ip.Trim('[', ']'));
            if (ip.Equals(IPAddress.IPv6Any)) ip = IPAddress.IPv6Loopback;
            if (!IPAddress.IsLoopback(ip) && !NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(a => a.Address.Equals(ip))) return false;
            using var client = new TcpClient(ip.AddressFamily);
            await client.ConnectAsync(ip, binding.Port, deadline.Token);
            using var stream = new SslStream(client.GetStream(), false, (_, cert, _, errors) =>
                errors == SslPolicyErrors.None && cert is not null && Same(cert.GetCertHashString(), thumbprint));
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                TargetHost = binding.HostName, CertificateRevocationCheckMode = X509RevocationMode.Online
            }, deadline.Token);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return false; }
    }
}
