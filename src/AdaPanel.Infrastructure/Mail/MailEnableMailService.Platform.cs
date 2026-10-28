using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;

namespace AdaPanel.Infrastructure.Mail;

public sealed partial class MailEnableMailService
{
    internal const string PlatformHeader = """
        $ErrorActionPreference = 'Stop'
        Add-PSSnapin MailEnable.Provision.Command -ErrorAction Stop
        function Read-Setting([string]$name) {
          $items = @(Get-MailEnablePlatform -Setting $name -ErrorAction Stop)
          if ($items.Count -ne 1) { throw 'MailEnable setting could not be measured.' }
          $item = $items[0]
          if ($item -is [string] -or $item -is [ValueType]) { return [string]$item }
          if ($null -ne $item.PSObject.Properties['Value']) { return [string]$item.Value }
          if ($null -ne $item.PSObject.Properties['SettingValue']) { return [string]$item.SettingValue }
          throw 'Unrecognized MailEnable setting response.'
        }
        """;

    private async Task<Dictionary<string, string>> ReadPlatformAsync(string[] settings, CancellationToken token)
    {
        var assignments = string.Join("\n", settings.Select(name => $"$data['{PsLiteralRaw(name)}'] = Read-Setting '{PsLiteralRaw(name)}'"));
        var json = await RunComAsync(PlatformHeader + "\n$data = @{}\n" + assignments + "\n$data | ConvertTo-Json -Compress", token);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json.Trim(), JsonOptions)
            ?? throw new AppException("mail.settings_unmeasured", 502, 502);
    }

    internal async Task<MailListenerSnapshotDto> ReadListenersAsync(CancellationToken token)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $services = @(Get-CimInstance Win32_Service -Filter "Name='MESMTPC' OR Name='MEIMAPS' OR Name='MEPOPS'" -ErrorAction Stop)
            $listeners = @()
            foreach ($service in $services) {
              if ($service.State -ne 'Running' -or $service.ProcessId -le 0) { continue }
              $protocol = switch ($service.Name) { 'MESMTPC' { 'smtp' } 'MEIMAPS' { 'imap' } 'MEPOPS' { 'pop3' } }
              $sockets = @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object OwningProcess -eq $service.ProcessId)
              foreach ($socket in $sockets) {
                $bind = if ($socket.LocalAddress.Contains(':')) { "[$($socket.LocalAddress)]:$($socket.LocalPort)" } else { "$($socket.LocalAddress):$($socket.LocalPort)" }
                $listeners += [pscustomobject]@{ name = $service.Name; protocol = $protocol; binds = @($bind) }
              }
            }
            [pscustomobject]@{ installed = ($services.Count -gt 0); measured = $true; listeners = @($listeners) } | ConvertTo-Json -Depth 5 -Compress
            """;
        return JsonSerializer.Deserialize<MailListenerSnapshotDto>(await RunComAsync(script, token), JsonOptions)
            ?? throw new AppException("mail.settings_unmeasured", 502, 502);
    }

    internal Func<string, int, string?, CancellationToken, Task<MailTlsEndpointDto>> EndpointProbe { get; init; } = MailTlsProbe.ProbeAsync;

    public Task<MailCertificateDto> GetCertificateStatusForDomainAsync(string domain, CancellationToken cancellationToken = default)
        => InspectCertificateAsync(cancellationToken, ValidateDomain(domain));

    internal async Task<MailCertificateDto> InspectCertificateAsync(CancellationToken token, string? selectedDomain = null)
    {
        var settings = await ReadPlatformAsync(["smtpLocalDomainName", "smtpHostName", "sysDefaultCertificate", "sysSNI"], token);
        var host = ResolveGreeting(settings.GetValueOrDefault("smtpHostName", ""), settings.GetValueOrDefault("smtpLocalDomainName", ""));
        var managed = fileStore is null ? [] : await fileStore.ReadAsync<ManagedCertificate[]>(MailEnableCertificateStatePath, [], token);
        var tracked = selectedDomain is null
            ? managed.LastOrDefault(item => item.HostName.Equals(host, StringComparison.OrdinalIgnoreCase)) ?? managed.LastOrDefault()
            : managed.FirstOrDefault(item => item.Domain.Equals(selectedDomain, StringComparison.OrdinalIgnoreCase)
                && item.HostName.Equals("mail." + selectedDomain, StringComparison.OrdinalIgnoreCase))
                ?? managed.FirstOrDefault(item => item.Domain.Equals(selectedDomain, StringComparison.OrdinalIgnoreCase));
        if (selectedDomain is not null) host = tracked?.HostName ?? "mail." + selectedDomain;
        else if (tracked is not null) host = tracked.HostName;
        if (host is null) throw new AppException("mail.settings_unmeasured", 502, 502);
        var probes = await Task.WhenAll(new[] { 587, 465, 443 }.Select(port => EndpointProbe(host, port, port == 443 ? null : tracked?.Thumbprint, token)));
        var smtp = probes.Where(p => p.Port != 443).ToArray();
        return new()
        {
            HostName = host, Domain = tracked?.Domain ?? selectedDomain ?? "", CertificateId = tracked?.Thumbprint ?? "",
            Source = "mailenable-windows-store", Active = smtp.Length == 2 && smtp.All(p => p.Status == "ok"),
            DefaultCertificateId = settings.GetValueOrDefault("sysDefaultCertificate", ""),
            ConfigurationStatus = "measured", Endpoints = smtp, WebCertificate = probes.Single(p => p.Port == 443),
            NotAfterUtc = smtp.Where(p => p.NotAfterUtc.HasValue).Select(p => p.NotAfterUtc).Min()
        };
    }

    internal static string? ResolveGreeting(string host, string domain)
    {
        host = host.Trim().TrimEnd('.'); domain = domain.Trim().TrimEnd('.');
        if (!host.Contains('.') && domain.Length > 0) host = host.Length > 0 ? host + "." + domain : domain;
        return host.Contains('.') && Uri.CheckHostName(host) == UriHostNameType.Dns && !host.Contains('*') ? host : null;
    }

    internal Func<string, int, string, bool, CancellationToken, Task<MailTlsEndpointDto>> ClientProbe { get; init; } = MailTlsProbe.ProbeClientAsync;

    internal async Task<MailClientSettingsDto?> ReadClientSettingsAsync(string domain, CancellationToken token)
    {
        var workspace = await GetWorkspaceAsync(null, token);
        if (!workspace.Configured || !workspace.Domains.Any(d => d.Name.Equals(domain, StringComparison.OrdinalIgnoreCase) && d.Active == true)) return null;
        var observed = await GetListenersAsync(token);
        if (!observed.Measured) return null;
        var keys = new List<string> { "smtpHostName", "smtpLocalDomainName", "smtpAdditionalPorts" };
        foreach (var prefix in new[] { "smtp", "imap", "pop" })
            keys.AddRange(new[] { "ListenPort", "ListenPortSSLEnabled", "AlternatePortEnabled", "AlternatePort", "AlternatePortSSLEnabled" }.Select(suffix => prefix + suffix));
        var settings = await ReadPlatformAsync(keys.ToArray(), token);
        var host = ResolveGreeting(settings.GetValueOrDefault("smtpHostName", ""), settings.GetValueOrDefault("smtpLocalDomainName", ""));
        if (host is null) return null;
        var candidates = ClientCandidates(settings, host);
        var measured = await Task.WhenAll(candidates.Where(candidate => observed.Listeners.Any(l =>
            l.Protocol == candidate.Protocol && l.Binds.Any(b => b.EndsWith(":" + candidate.Port, StringComparison.Ordinal))))
            .Select(async candidate =>
        {
            var probe = await ClientProbe(host, candidate.Port, candidate.Protocol, candidate.SocketType == "STARTTLS", token);
            return probe.Status == "ok" ? candidate : null;
        }));
        var verified = measured.OfType<MailClientServerDto>().ToArray();
        return verified.Any(s => s.Protocol == "smtp") && verified.Any(s => s.Protocol is "imap" or "pop3")
            ? new MailClientSettingsDto { Domain = domain, HostName = host, Servers = verified } : null;
    }

    internal static IReadOnlyList<MailClientServerDto> ClientCandidates(IReadOnlyDictionary<string, string> settings, string host)
    {
        var result = new List<MailClientServerDto>();
        void Add(string protocol, string? portText, string? ssl)
        {
            if (!int.TryParse(portText, out var port) || port is < 1 or > 65535 || ssl is not ("0" or "1")) return;
            // TCP 25 is server-to-server delivery, not the client submission configuration.
            if (protocol == "smtp" && port == 25) return;
            if (!result.Any(s => s.Protocol == protocol && s.Port == port))
                result.Add(new() { Protocol = protocol, Host = host, Port = port, SocketType = ssl == "1" ? "SSL" : "STARTTLS" });
        }
        foreach (var prefix in new[] { "smtp", "imap", "pop" })
        {
            var protocol = prefix == "pop" ? "pop3" : prefix;
            Add(protocol, settings.GetValueOrDefault(prefix + "ListenPort"), settings.GetValueOrDefault(prefix + "ListenPortSSLEnabled"));
            if (settings.GetValueOrDefault(prefix + "AlternatePortEnabled") == "1")
                Add(protocol, settings.GetValueOrDefault(prefix + "AlternatePort"), settings.GetValueOrDefault(prefix + "AlternatePortSSLEnabled"));
        }
        foreach (var record in settings.GetValueOrDefault("smtpAdditionalPorts", "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Split(',');
            if (fields.Length == 4 && fields[1] == "1" && fields[3] is "0" or "2") Add("smtp", fields[0], fields[2]);
        }
        return result;
    }
}
