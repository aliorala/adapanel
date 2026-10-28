using System.Net;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Mail;

public sealed partial class MailService
{
    internal const string ServerIdentityPath = "config/mail-server-identity.json";
    internal sealed class ServerIdentityPin
    {
        public string Domain { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
        public string ContactEmail { get; set; } = string.Empty;
    }

    internal Func<string, CancellationToken, Task<bool>> IdentityGreetingProbe { get; init; } = (host, token) => SmtpGreetingProbe.MatchesAsync(host, token);
    internal Func<string, string, CancellationToken, Task<string[]>> IdentityDnsQuery { get; init; } = QueryIdentityDnsAsync;

    private Task<ServerIdentityPin?> ReadServerIdentityPinAsync(CancellationToken token) =>
        fileStore.ReadAsync<ServerIdentityPin?>(ServerIdentityPath, null, token);

    internal static (string Domain, string Host) ValidateServerIdentity(SetMailServerIdentityRequest request)
    {
        var domain = request.Domain?.Trim().ToLowerInvariant() ?? string.Empty;
        var host = request.HostName?.Trim().ToLowerInvariant() ?? string.Empty;
        static bool Fqdn(string value) => value.Length is > 3 and <= 253 && value.Contains('.')
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.')
            && value.Split('.').All(label => label.Length is > 0 and <= 63 && label[0] != '-' && label[^1] != '-')
            && Uri.CheckHostName(value) == UriHostNameType.Dns;
        if (!Fqdn(domain) || !Fqdn(host) || !host.EndsWith('.' + domain, StringComparison.Ordinal)
            || (!string.IsNullOrWhiteSpace(request.ContactEmail) && !Ssl.AcmeArgumentRules.IsCommandSafeEmail(request.ContactEmail)))
            throw new AppException("mail.invalid_domain", 400, 400);
        return (domain, host);
    }

    private async Task<bool> EnsurePinnedIdentityAsync(ServerIdentityPin pin, bool applyChanges, CancellationToken token)
    {
        var (cli, env) = await RequireCliAsync(token);
        var settings = await ReadIdentitySettingsAsync(cli, env, token);
        var current = GetField(settings, "defaultHostname");
        var currentDomainId = GetField(settings, "defaultDomainId");
        var domains = await ReadIdentityDomainIdsAsync(cli, env, token);
        if (domains.Count == 0) throw new IOException("Pinned mail identity has no valid default mail domain.");
        var domainId = domains.Contains(currentDomainId, StringComparer.Ordinal) ? currentDomainId : domains[0];
        var matches = current.Equals(pin.HostName, StringComparison.OrdinalIgnoreCase)
            && domainId == currentDomainId && await IdentityGreetingProbe(pin.HostName, token);
        if (matches) return false;
        if (!applyChanges) return true;
        // Repair only the server's explicit hostname. Domain inventory order, PTR and another
        // site's certificate issuance cannot become a replacement identity.
        var write = await RunCertificateCommandAsync(cli, "update SystemSettings",
            JsonSerializer.Serialize(new { defaultHostname = pin.HostName, defaultDomainId = domainId }), env, token);
        if (!write.Success) throw new IOException("Pinned mail identity could not be restored.");
        var reload = await RunCertificateCommandAsync(cli, "create Action", "{\"@type\":\"ReloadSettings\"}", env, token);
        var confirmed = await ReadIdentitySettingsAsync(cli, env, token);
        if (!reload.Success || !GetField(confirmed, "defaultHostname").Equals(pin.HostName, StringComparison.OrdinalIgnoreCase)
            || GetField(confirmed, "defaultDomainId") != domainId
            || !await IdentityGreetingProbe(pin.HostName, token))
            throw new IOException("Pinned mail identity activation could not be verified.");
        return true;
    }

    public async Task<MailServerIdentityDto> GetServerIdentityAsync(CancellationToken cancellationToken = default)
    {
        using var operation = await MailCertificateLifecycle.EnterAsync(fileStore.ResolvePath(CertificateStatePath), cancellationToken);
        return await ReadIdentityStatusAsync(cancellationToken);
    }

    private async Task<MailServerIdentityDto> ReadIdentityStatusAsync(CancellationToken token)
    {
        var pin = await ReadServerIdentityPinAsync(token);
        var contact = await fileStore.ReadAsync<AcmeContactSettings?>(AcmeContactSettings.Path, null, token);
        var dto = new MailServerIdentityDto { Supported = true, Pinned = pin is not null,
            ConfiguredHostName = pin?.HostName ?? string.Empty, Domain = pin?.Domain ?? string.Empty,
            ContactEmail = !string.IsNullOrWhiteSpace(pin?.ContactEmail) ? pin.ContactEmail : contact?.Email ?? string.Empty };
        var (cli, env) = await RequireCliAsync(token);
        var settings = await ReadIdentitySettingsAsync(cli, env, token);
        dto.CurrentHostName = GetField(settings, "defaultHostname");
        dto.Applied = pin is not null && dto.CurrentHostName.Equals(pin.HostName, StringComparison.OrdinalIgnoreCase)
            && await IdentityGreetingProbe(pin.HostName, token);

        return dto;
    }

    public async Task<MailServerIdentityDto> SetServerIdentityAsync(SetMailServerIdentityRequest request,
        CancellationToken cancellationToken = default)
    {
        // All validation precedes provider writes and certificate issuance.
        var (domain, host) = ValidateServerIdentity(request);
        using var operation = await MailCertificateLifecycle.EnterAsync(fileStore.ResolvePath(CertificateStatePath), cancellationToken);
        var sites = await iisSiteService.GetSitesAsync(cancellationToken);
        var site = sites.SingleOrDefault(s => s.Name.Equals(domain, StringComparison.OrdinalIgnoreCase))
            ?? sites.SingleOrDefault(s => s.Bindings.Any(b => b.HostName.Equals(domain, StringComparison.OrdinalIgnoreCase)))
            ?? throw new AppException("mail.invalid_domain", 400, 400);
        var preferences = await panelPreferencesService.GetAsync(cancellationToken);
        var address = preferences.ServerAddress?.Trim() ?? string.Empty;
        if (!IsPublicIdentityAddress(address)) throw new AppException("mail.server_address_required", 400, 400);
        var dto = await ReadIdentityStatusAsync(cancellationToken);
        // Applied describes this request; a failed new request may retain a healthy previous pin.
        dto.Applied = false;

        void Step(string key, string status, string message) => dto.Steps.Add(new() { Key = key, Status = status, Message = message });
        var stage = "dns";
        try
        {
            if (!await IdentityAddressMatchesAsync(host, address, cancellationToken))
            {
                var zone = await dnsService.GetZoneAsync(site.Name, cancellationToken);
                var nameServers = await IdentityDnsQuery(domain, "NS", cancellationToken);
                var delegatedHere = false;
                foreach (var ns in nameServers.Take(8))
                    delegatedHere |= await IdentityAddressMatchesAsync(ns, address, cancellationToken);
                if (!delegatedHere || zone.IsSecondary || zone.ModeConfigurationConflict
                    || !zone.IsWindowsDnsAvailable || !zone.IsWindowsZonePresent
                    || !zone.WindowsZoneType.Equals("Primary", StringComparison.OrdinalIgnoreCase)
                    || !(host.EndsWith('.' + zone.ZoneName, StringComparison.OrdinalIgnoreCase)))
                {
                    Step("dns", "action-required", $"Yetkili DNS sağlayıcınızda {host} A {address} kaydını ekleyin (TTL 3600). Yayınlandıktan sonra tekrar uygulayın; aktif posta kimliği korunmuştur.");
                    return dto;
                }
                var label = host[..^(zone.ZoneName.Length + 1)];
                var matches = zone.Records.Where(r => r.Type.Equals("A", StringComparison.OrdinalIgnoreCase)
                    && r.Host.Equals(label, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length > 1 || zone.Records.Any(r => r.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase)
                    && r.Host.Equals(label, StringComparison.OrdinalIgnoreCase)))
                {
                    Step("dns", "action-required", $"{host} için çakışan A/CNAME kayıtlarını DNS ekranından düzeltin. Aktif kimlik korunmuştur.");
                    return dto;
                }
                var record = matches.SingleOrDefault();
                if (record is null || !IPAddress.TryParse(record.Value, out var existing) || !existing.Equals(IPAddress.Parse(address)))
                {
                    await dnsService.UpsertRecordAsync(site.Name, new UpsertDnsRecordRequest
                    { Id = record?.Id, Type = "A", Host = label, Value = address, Ttl = record?.Ttl ?? 3600 }, cancellationToken);
                    Step("dns-preparation", "ok", $"Yalnız {host} A kaydı {address} olarak hazırlandı. Diğer posta kayıtları korundu.");
                }
                var published = await dnsService.GetPublishedRecordKeysAsync(site.Name, cancellationToken);
                if (published is null || !published.Contains("A|" + label, StringComparer.OrdinalIgnoreCase)
                    || !await IdentityAddressMatchesAsync(host, address, cancellationToken))
                {
                    Step("dns", "action-required", $"{host} A {address} kaydının genel DNS yayını henüz doğrulanamadı. Hazırlanan kayıt kalır; aktif kimlik korunmuştur. Yayından sonra tekrar uygulayın.");
                    return dto;
                }
            }
            Step("dns", "ok", $"{host} genel A kaydı {address} adresini doğruluyor.");
            stage = "certificate";
            var certificate = await IssueCertificateCoreAsync(new IssueMailCertificateRequest
            { Domain = domain, HostName = host, ContactEmail = request.ContactEmail }, cancellationToken, persistContact: false);
            if (!certificate.Active) throw new IOException("Mail certificate is not active.");
            Step("certificate", "ok", "Güvenilir sertifika hazırlandı ve SMTP TLS üzerinde doğrulandı; mevcut alanların SNI sertifikaları korundu.");
            stage = "identity";
            var (cli, env) = await RequireCliAsync(cancellationToken);
            var previous = await ReadIdentitySettingsAsync(cli, env, cancellationToken);
            var oldHost = GetField(previous, "defaultHostname");
            var oldCertificate = GetField(previous, "defaultCertificateId");
            var inventory = MailQueryResult.Olc(await RunCertificateCommandAsync(cli, "query Certificate --json", null, env, cancellationToken));
            if (!inventory.Measured) throw new IOException("Certificate inventory could not be verified.");
            var targetCertificates = ReadCertificateInventory(inventory.Items).Where(c => c.Names.Any(n => CertificateNameMatches(n, host))).ToArray();
            if (targetCertificates.Length != 1) throw new IOException("Certificate selection is ambiguous.");
            try
            {
                await WriteIdentitySettingsAsync(cli, env, host, targetCertificates[0].Id, cancellationToken);
                var confirmed = await ReadIdentitySettingsAsync(cli, env, cancellationToken);
                if (!GetField(confirmed, "defaultHostname").Equals(host, StringComparison.OrdinalIgnoreCase)
                    || GetField(confirmed, "defaultCertificateId") != targetCertificates[0].Id
                    || !await IdentityGreetingProbe(host, cancellationToken))
                    throw new IOException("SMTP greeting or saved identity did not match.");
                var contact = request.ContactEmail?.Trim() ?? string.Empty;
                if (contact.Length == 0) contact = dto.ContactEmail;
                await SaveServerIdentitySelectionAsync(new ServerIdentityPin { Domain = domain, HostName = host, ContactEmail = contact }, cancellationToken);
                dto.ContactEmail = contact;
            }
            catch
            {
                // Request cancellation must not interrupt recovery of the previous active identity.
                using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                try
                {
                    await WriteIdentitySettingsAsync(cli, env, oldHost, oldCertificate, recovery.Token);
                    var restored = await ReadIdentitySettingsAsync(cli, env, recovery.Token);
                    var verified = GetField(restored, "defaultHostname") == oldHost
                        && GetField(restored, "defaultCertificateId") == oldCertificate
                        && await IdentityGreetingProbe(oldHost, recovery.Token);
                    Step("rollback", verified ? "ok" : "fail", verified ? "Önceki aktif kimlik geri yüklendi ve doğrulandı." : "Önceki aktif kimliğe dönüş doğrulanamadı; posta servisini kontrol edin.");
                }
                catch (Exception error)
                {
                    Step("rollback", "fail", "Önceki aktif kimliğe dönüş doğrulanamadı; posta servisini kontrol edin.");
                    logger.LogError("Mail identity rollback failed: {ErrorType}", error.GetType().Name);
                }
                throw;
            }
            dto.ConfiguredHostName = dto.CurrentHostName = host;
            dto.Domain = domain;
            dto.Pinned = dto.Applied = true;
            Step("identity", "ok", "Sunucu adı kaydedildi; yeniden yükleme, ayar okuması ve gerçek SMTP karşılaması doğrulandı.");

            return dto;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning("Mail identity operation failed at {Stage}: {ErrorType}", stage, error.GetType().Name);
            Step(stage, "fail", "İşlem doğrulanamadı. Önceki kalıcı tercih korundu; DNS ve sertifika hazırlıkları yapılmış olabilir.");
            // Report the provider's actual configured value, including an incomplete rollback.
            try
            {
                var (cli, env) = await RequireCliAsync(cancellationToken);
                dto.CurrentHostName = GetField(await ReadIdentitySettingsAsync(cli, env, cancellationToken), "defaultHostname");
            }
            catch { dto.CurrentHostName = string.Empty; }
            return dto;
        }
    }

    private async Task<JsonElement> ReadIdentitySettingsAsync(string cli, IReadOnlyDictionary<string, string> env, CancellationToken token)
    {
        var query = MailQueryResult.Olc(await RunCertificateCommandAsync(cli, "get SystemSettings --json", null, env, token));
        _ = ReadCertificateSettings(query, "mail.server_identity_read_failed");
        return query.Items[0];
    }

    private async Task WriteIdentitySettingsAsync(string cli, IReadOnlyDictionary<string, string> env, string host, string certificate, CancellationToken token)
    {
        var settings = await ReadIdentitySettingsAsync(cli, env, token);
        var domains = await ReadIdentityDomainIdsAsync(cli, env, token);
        if (domains.Count == 0) throw new IOException("Mail identity requires a valid default mail domain.");
        var currentDomainId = GetField(settings, "defaultDomainId");
        var domainId = domains.Contains(currentDomainId, StringComparer.Ordinal) ? currentDomainId : domains[0];
        var result = await RunCertificateCommandAsync(cli, "update SystemSettings", JsonSerializer.Serialize(new
        { defaultHostname = host, defaultCertificateId = certificate, defaultDomainId = domainId }), env, token);
        if (!result.Success) throw new IOException("Mail identity could not be saved.");
        var reload = await RunCertificateCommandAsync(cli, "create Action", "{\"@type\":\"ReloadSettings\"}", env, token);
        if (!reload.Success) throw new IOException("Mail identity could not be reloaded.");
    }

    private async Task<List<string>> ReadIdentityDomainIdsAsync(string cli, IReadOnlyDictionary<string, string> env, CancellationToken token)
    {
        var query = CertificateCommandRunner is null ? await QueryAsync(cli, "domain", env, token)
            : MailQueryResult.Olc(await RunCertificateCommandAsync(cli, "query domain --json", null, env, token));
        if (!query.Measured) throw new IOException("SMTP hostname inspection could not read the mail domain inventory.");
        var ids = query.Items.Select(item => GetField(item, "id")).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        if (ids.Count != query.Items.Count || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw new IOException("SMTP hostname inspection received an incomplete mail domain inventory.");
        return ids;
    }

    private async Task SaveServerIdentitySelectionAsync(ServerIdentityPin selection, CancellationToken token)
    {
        var previousContact = await fileStore.ReadAsync<System.Text.Json.Nodes.JsonObject?>(AcmeContactSettings.Path, null, token);
        try
        {
            if (!string.IsNullOrWhiteSpace(selection.ContactEmail))
                await fileStore.WriteAsync(AcmeContactSettings.Path, new AcmeContactSettings { Email = selection.ContactEmail }, token);
            await fileStore.WriteAsync(ServerIdentityPath, selection, token);
        }
        catch
        {
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (previousContact is not null) await fileStore.WriteAsync(AcmeContactSettings.Path, previousContact, recovery.Token);
            else File.Delete(fileStore.ResolvePath(AcmeContactSettings.Path));
            throw;
        }
    }

    private async Task<bool> IdentityAddressMatchesAsync(string host, string address, CancellationToken token)
    {
        var values = await IdentityDnsQuery(host, "A", token);
        return values.Length > 0 && values.All(value => IPAddress.TryParse(value, out var ip) && ip.Equals(IPAddress.Parse(address)));
    }


    internal static bool IsPublicIdentityAddress(string address)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var bytes = ip.GetAddressBytes();
        return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224
            && !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            && !(bytes[0] == 192 && bytes[1] == 168) && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
    }

    private static async Task<string[]> QueryIdentityDnsAsync(string name, string type, CancellationToken token)
    {
        // Both inputs are constrained before reaching PowerShell; NS answers are validated again.
        if (Uri.CheckHostName(name.TrimEnd('.')) != UriHostNameType.Dns || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.'))
            || type is not ("A" or "NS")) return [];
        var property = type == "A" ? "IPAddress" : "NameHost";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var result = await ProcessRunner.RunPowerShellAsync(
                $"Resolve-DnsName -Name '{name}' -Type {type} -Server 1.1.1.1 -DnsOnly -ErrorAction Stop | Where-Object Type -eq '{type}' | Select-Object -ExpandProperty {property}", deadline.Token);
            return result.Success ? result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().TrimEnd('.')).ToArray() : [];
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return []; }
    }
}
