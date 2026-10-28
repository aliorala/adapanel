using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Mail;

public sealed partial class WebmailService
{
    private static readonly SemaphoreSlim DomainPreparation = new(1, 1);
    internal Func<string, CancellationToken, Task<bool>> DomainHttpsProbe { get; init; } = ProbeDomainHttpsAsync;
    internal Func<string, CancellationToken, Task<CommandResult>> SsoAclCommand { get; init; } = ProcessRunner.RunPowerShellAsync;

    internal static string RequestedWebmailHost(string? domain)
    {
        var value = (domain ?? "").Trim().TrimEnd('.');
        if (value.StartsWith("*.", StringComparison.Ordinal) || ResolveWebmailHost(value) is not { } host)
            throw new AppException("mail.webmail_domain_invalid", 422, 422);
        return host;
    }

    public async Task<WebmailOpenDto> OpenAsync(string publicHost, WebmailOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Domain))
        {
            if (!string.IsNullOrWhiteSpace(request.Address))
                throw new AppException("mail.webmail_domain_invalid", 422, 422);
            return await OpenAsync(publicHost, cancellationToken);
        }
        var host = RequestedWebmailHost(request.Domain);
        if (providerSelection?.Current == "mailenable")
        {
            if (!string.IsNullOrWhiteSpace(request.Address))
                throw new AppException("mail.webmail_sso_unsupported", 409, 409);
            var endpoint = await mailEnableWebmail!.OpenAsync(host, cancellationToken);
            if (!Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || !uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort)
                throw new AppException("mail.webmail_https_required", 409, 409);
            return endpoint;
        }

        await DomainPreparation.WaitAsync(cancellationToken);
        try
        {
            // The existing deployment is shared. Repair must retain every previous HTTPS binding.
            await OpenAsync(publicHost, cancellationToken);
            var state = await fileStore.ReadAsync<WebmailState?>(StatePath, null, cancellationToken)
                ?? throw new AppException("mail.webmail_sso_unavailable", 409, 409);
            var site = await iisSiteService.GetSiteAsync(SiteName, cancellationToken);
            var bound = site.Bindings.Any(b => b.Protocol.Equals("https", StringComparison.OrdinalIgnoreCase)
                && b.Port == 443 && b.HostName.Equals(host, StringComparison.OrdinalIgnoreCase));
            if (!bound || !await DomainHttpsProbe(host, cancellationToken))
            {
                await EnsureDomainDnsAsync(host[8..], cancellationToken);
                if (!site.Bindings.Any(b => b.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase)
                    && b.Port == 80 && b.HostName.Equals(host, StringComparison.OrdinalIgnoreCase)))
                    await iisSiteService.AddBindingAsync(SiteName,
                        new SiteBindingRequest { HostName = host, Protocol = "http", Port = 80 }, cancellationToken);
                var contact = await fileStore.ReadAsync<AcmeContactSettings?>(AcmeContactSettings.Path, null, cancellationToken);
                if (!await sslService.TrySecureHostAsync(SiteName, host, contact?.Email ?? "", cancellationToken)
                    || !await DomainHttpsProbe(host, cancellationToken))
                    throw new AppException("mail.webmail_https_required", 409, 409);
                site = await iisSiteService.GetSiteAsync(SiteName, cancellationToken);
                if (!site.Bindings.Any(b => b.Protocol.Equals("https", StringComparison.OrdinalIgnoreCase)
                    && b.Port == 443 && b.HostName.Equals(host, StringComparison.OrdinalIgnoreCase)))
                    throw new AppException("mail.webmail_https_required", 409, 409);
            }
            // Do not publish a credential into an application readable/writable by other pools.
            if (!string.IsNullOrWhiteSpace(request.Address))
            {
                await HardenSsoRootAsync(state, site, cancellationToken);
                if (!await DomainHttpsProbe(host, cancellationToken))
                    throw new AppException("mail.webmail_https_required", 409, 409);
            }

            using (await MailCertificateLifecycle.EnterAsync(fileStore.ResolvePath(StatePath), cancellationToken))
            {
                var latest = await fileStore.ReadAsync<WebmailState?>(StatePath, null, cancellationToken)
                    ?? throw new AppException("mail.webmail_sso_unavailable", 409, 409);
                if (!latest.SecuredHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                {
                    latest.SecuredHosts.Add(host);
                    await fileStore.WriteAsync(StatePath, latest, cancellationToken);
                }
            }
            return new WebmailOpenDto { Url = $"https://{host}/" };
        }
        finally { DomainPreparation.Release(); }
    }

    private async Task EnsureDomainDnsAsync(string domain, CancellationToken token)
    {
        if (dnsService is null) return;
        var sites = await iisSiteService.GetSitesAsync(token);
        var owners = sites.Where(site => site.Bindings.Any(b => b.HostName.Equals(domain, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (owners.Length != 1) return; // External DNS requires an explicit operator action.
        var zone = await dnsService.GetZoneAsync(owners[0].Name, token);
        if (zone.IsSecondary || !zone.IsWindowsZonePresent || !zone.IsWindowsDnsAvailable) return;
        if (zone.Records.Any(r => r.Host.Equals("webmail", StringComparison.OrdinalIgnoreCase))) return;
        await dnsService.EnsureMissingRecordsAsync(owners[0].Name,
            [new UpsertDnsRecordRequest { Type = "CNAME", Host = "webmail", Value = domain + "." }], token);
    }

    private static async Task<bool> ProbeDomainHttpsAsync(string host, CancellationToken token)
    {
        try
        {
            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
            handler.ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 443), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            };
            // URI hostname still controls SNI and the normal chain/name checks.
            handler.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.Online;
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await client.GetAsync($"https://{host}/adapanel-health.php", token);
            return response.StatusCode == HttpStatusCode.OK
                && (await response.Content.ReadAsStringAsync(token)).Trim() == "AdaPanel-Webmail-OK";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { return false; }
    }

    private async Task HardenSsoRootAsync(WebmailState state, SiteDto site, CancellationToken token)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(state.EntryRoot));
        if (root is null || !site.ApplicationPoolName.Equals($"AdaPanel.{SiteName}", StringComparison.Ordinal)
            || !Path.GetFullPath(site.PhysicalPath).TrimEnd('\\', '/').Equals(
                Path.GetFullPath(state.EntryRoot).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(Path.Combine(root, "config")) || !Directory.Exists(Path.Combine(root, "program")))
            throw new AppException("mail.webmail_sso_unavailable", 409, 409);
        var result = await SsoAclCommand(BuildSsoAclScript(root), token);
        if (!result.Success || !result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Contains("ADAPANEL-WEBMAIL-ACL-OK", StringComparer.Ordinal))
            throw new AppException("mail.webmail_sso_unavailable", 409, 409);
    }

    internal static string BuildSsoAclScript(string root) => $$"""
        $ErrorActionPreference = 'Stop'
        Import-Module WebAdministration -ErrorAction Stop
        $root = [IO.Path]::GetFullPath('{{root.Replace("'", "''")}}').TrimEnd('\')
        $siteName = 'AdaPanel.Webmail'
        $poolName = 'AdaPanel.AdaPanel.Webmail'
        $pool = Get-Item -LiteralPath "IIS:\AppPools\$poolName" -ErrorAction Stop
        if ("$($pool.processModel.identityType)" -notin @('4','ApplicationPoolIdentity')) { throw 'Webmail requires its dedicated pool identity.' }
        $poolSid = ([Security.Principal.NTAccount]::new("IIS APPPOOL\$poolName")).Translate([Security.Principal.SecurityIdentifier])
        $systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
        $adminSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
        $allowed = @($poolSid.Value,$systemSid.Value,$adminSid.Value)
        $marker = Join-Path $root 'config\adapanel-private-acl-v1'
        $writeRoots = @('adapanel-data','temp','logs') | ForEach-Object { Join-Path $root $_ }
        function Writable([string]$path) {
          foreach ($w in $writeRoots) { if ($path -eq $w -or $path.StartsWith($w+'\',[StringComparison]::OrdinalIgnoreCase)) { return $true } }
          return $false
        }
        function Check-Acl([string]$path) {
          $acl = Get-Acl -LiteralPath $path -ErrorAction Stop
          foreach ($ace in $acl.Access) {
            if ($ace.AccessControlType -ne 'Allow') { continue }
            $sid = $ace.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
            if ($allowed -notcontains $sid) { throw 'Webmail ACL grants another identity access.' }
            if ($sid -eq $poolSid.Value -and -not (Writable $path) -and
                ($ace.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write) -ne 0) { throw 'Webmail code is writable by the pool.' }
          }
          if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $allowed) { throw 'Unexpected webmail owner.' }
        }
        if (-not (Test-Path -LiteralPath $marker)) {
          $entries = @((Get-Item -LiteralPath $root)) + @(Get-ChildItem -LiteralPath $root -Force -Recurse -ErrorAction Stop)
          # Existing Roundcube static-asset junctions may only reference this same owned tree.
          foreach ($entry in $entries) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
              if (-not $entry.Target) { throw 'Unresolved webmail link.' }
              foreach ($target in $entry.Target) {
                if (-not [IO.Path]::IsPathRooted($target)) { $target = Join-Path $entry.Parent.FullName $target }
                $target = [IO.Path]::GetFullPath($target)
                if (-not $target.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Webmail link leaves the owned tree.' }
              }
            }
          }
          foreach ($entry in $entries) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            if ($entry.PSIsContainer) { $acl = [Security.AccessControl.DirectorySecurity]::new(); $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit' }
            else { $acl = [Security.AccessControl.FileSecurity]::new(); $inherit = [Security.AccessControl.InheritanceFlags]::None }
            $acl.SetAccessRuleProtection($true,$false)
            $acl.SetOwner($adminSid)
            foreach ($sid in @($systemSid,$adminSid)) {
              $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl',$inherit,'None','Allow'))
            }
            $rights = if (Writable $entry.FullName) { 'Modify' } else { 'ReadAndExecute' }
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($poolSid,$rights,$inherit,'None','Allow'))
            Set-Acl -LiteralPath $entry.FullName -AclObject $acl -ErrorAction Stop
            Check-Acl $entry.FullName
          }
          [IO.File]::WriteAllText($marker,'AdaPanel private webmail ACL v1')
        }
        # On every handoff recheck all private state, including existing SQLite/session files.
        $private = @($root,(Join-Path $root 'public_html'),(Join-Path $root 'config')) + $writeRoots
        foreach ($path in $private) { Check-Acl $path }
        foreach ($path in @((Join-Path $root 'config'))+$writeRoots) {
          foreach ($entry in (Get-ChildItem -LiteralPath $path -Recurse -Force -ErrorAction Stop)) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Link in private webmail state.' }
            Check-Acl $entry.FullName
          }
        }
        Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $siteName -Filter 'system.webServer/security/authentication/anonymousAuthentication' -Name userName -Value '' -ErrorAction Stop
        $anonymous = Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $siteName -Filter 'system.webServer/security/authentication/anonymousAuthentication' -Name userName -ErrorAction Stop
        if ("$($anonymous.Value)" -ne '') { throw 'Anonymous webmail identity did not change.' }
        'ADAPANEL-WEBMAIL-ACL-OK'
        """;
}
