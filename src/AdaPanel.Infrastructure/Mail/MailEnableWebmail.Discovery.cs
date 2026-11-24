using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Mail;

public sealed partial class MailEnableWebmail
{
    internal sealed record NativeDirectory(string Path, string PhysicalPath);
    internal sealed record NativeApplication(string Path, string Pool, NativeDirectory[] Directories);
    internal sealed record NativeBinding(string Protocol, string Host, int Port, string Information, string Thumbprint,
        [property: System.Text.Json.Serialization.JsonRequired] int Flags, [property: System.Text.Json.Serialization.JsonRequired] string StoreName);
    internal sealed record NativeSite(long Id, string Name, string State, NativeApplication[] Applications, NativeBinding[] Bindings);
    private sealed record TargetSite(NativeSite Site, string Root, string Pool, string LoginPath, string Identity);
    internal const string DiscoveryScript = """
        $ErrorActionPreference='Stop'
        Add-Type -Path "$env:windir\System32\inetsrv\Microsoft.Web.Administration.dll" -ErrorAction Stop
        $manager=New-Object Microsoft.Web.Administration.ServerManager
        try {
          $sites=@()
          foreach ($site in $manager.Sites) {
            $apps=@()
            foreach ($app in $site.Applications) {
              $directories=@()
              foreach ($directory in $app.VirtualDirectories) {
                $directories += [pscustomobject]@{path=[string]$directory.Path;physicalPath=[Environment]::ExpandEnvironmentVariables([string]$directory.PhysicalPath)}
              }
              $apps += [pscustomobject]@{path=[string]$app.Path;pool=[string]$app.ApplicationPoolName;directories=@($directories)}
            }
            $bindings=@()
            foreach ($binding in $site.Bindings) {
              $thumb=''; $flags=0; $store=''
              if ($binding.Protocol -eq 'https') {
                $hash=$binding.CertificateHash
                if ($hash -is [byte[]]) { $thumb=[BitConverter]::ToString($hash).Replace('-','') }
                elseif ($hash -is [string]) { $thumb=$hash.Trim() }
                elseif ($null -ne $hash) { throw 'Certificate hash is unreadable.' }
                if ($null -eq $binding.SslFlags) { throw 'SSL flags are unreadable.' }
                $flags=[int]$binding.SslFlags
                if ($null -eq $binding.CertificateStoreName) { throw 'Certificate store is unreadable.' }
                $store=[string]$binding.CertificateStoreName
              }
              $bindings += [pscustomobject]@{protocol=[string]$binding.Protocol;host=[string]$binding.Host;port=[int]$binding.EndPoint.Port;information=[string]$binding.BindingInformation;thumbprint=$thumb;flags=$flags;storeName=$store}
            }
            $sites += [pscustomobject]@{id=[long]$site.Id;name=[string]$site.Name;state=[string]$site.State;applications=@($apps);bindings=@($bindings)}
          }
          ConvertTo-Json -InputObject @($sites) -Depth 8 -Compress
        } finally { $manager.Dispose() }
        """;

    internal static string NativeIdentityGuard(NativeSite expected, string loginFile)
    {
        var data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { site = expected, loginFile },
            new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        return $$"""
            $ErrorActionPreference='Stop'
            $webmailExpected=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{data}}')) | ConvertFrom-Json
            $webmailJson = & {
            {{DiscoveryScript}}
            }
            $webmailSites=@($webmailJson | ConvertFrom-Json)
            $webmailActual=@($webmailSites | Where-Object { $_.id -eq $webmailExpected.site.id -and $_.name -ieq $webmailExpected.site.name })
            if ($webmailActual.Count -ne 1 -or $webmailActual[0].state -cne 'Started') { throw 'WebMail site changed.' }
            function WebMail-FullPath([string]$value) {
              return [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($value)).TrimEnd([IO.Path]::DirectorySeparatorChar)
            }
            function WebMail-AssertRealPath([string]$value) {
              $item=Get-Item -LiteralPath $value -Force -ErrorAction Stop
              while ($null -ne $item) {
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'WebMail path became a reparse point.' }
                if ($item -is [IO.FileInfo]) { $item=$item.Directory } else { $item=$item.Parent }
              }
            }
            $webmailApps=@($webmailActual[0].applications)
            if ($webmailApps.Count -ne @($webmailExpected.site.applications).Count) { throw 'WebMail applications changed.' }
            foreach ($expectedApp in $webmailExpected.site.applications) {
              $actualApp=@($webmailApps | Where-Object { $_.path -ieq $expectedApp.path })
              if ($actualApp.Count -ne 1 -or $actualApp[0].pool -ine $expectedApp.pool -or
                  @($actualApp[0].directories).Count -ne @($expectedApp.directories).Count) { throw 'WebMail application changed.' }
              foreach ($expectedDirectory in $expectedApp.directories) {
                $actualDirectory=@($actualApp[0].directories | Where-Object { $_.path -ieq $expectedDirectory.path })
                if ($actualDirectory.Count -ne 1 -or (WebMail-FullPath $actualDirectory[0].physicalPath) -ine
                    (WebMail-FullPath $expectedDirectory.physicalPath)) { throw 'WebMail directory changed.' }
                WebMail-AssertRealPath (WebMail-FullPath $actualDirectory[0].physicalPath)
              }
            }
            if (-not [IO.File]::Exists($webmailExpected.loginFile)) { throw 'WebMail login disappeared.' }
            WebMail-AssertRealPath $webmailExpected.loginFile
            # WEBMAIL_IDENTITY_GUARD_END
            """;
    }

    private async Task<NativeSite[]> ReadInventoryAsync(CancellationToken token)
    {
        var installation = InstallationRoot();
        if (string.IsNullOrWhiteSpace(installation) || !Directory.Exists(installation)) throw NotReady();
        var result = await DiscoveryCommand(DiscoveryScript, token);
        token.ThrowIfCancellationRequested();
        if (!result.Success) throw NotReady();
        try
        {
            var sites = JsonSerializer.Deserialize<NativeSite[]>(result.StandardOutput, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (sites is null || sites.Any(s => s.Id <= 0 || string.IsNullOrWhiteSpace(s.Name) || string.IsNullOrWhiteSpace(s.State)
                || s.Applications is null || s.Bindings is null || s.Applications.Any(a => a.Path is null || !a.Path.StartsWith('/')
                    || string.IsNullOrWhiteSpace(a.Pool) || a.Directories is null || a.Directories.Any(d => d.Path is null || !d.Path.StartsWith('/') || string.IsNullOrWhiteSpace(d.PhysicalPath)))
                || s.Bindings.Any(b => b.Protocol is null || b.Host is null || b.Information is null || b.Thumbprint is null || b.StoreName is null
                    || b.Thumbprint.Length > 0 && string.IsNullOrWhiteSpace(b.StoreName) || b.Port is < 1 or > 65535
                    || b.Flags < 0 || b.Thumbprint.Length > 0 && !Regex.IsMatch(b.Thumbprint, "\\A[0-9A-Fa-f]{40}\\z")))
                || sites.Select(s => s.Id).Distinct().Count() != sites.Length
                || sites.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sites.Length) throw NotReady();
            return sites;
        }
        catch (JsonException) { throw NotReady(); }
    }

    private IEnumerable<Endpoint> TrustedEndpoints(NativeSite site)
    {
        foreach (var app in site.Applications)
        foreach (var directory in app.Directories)
        {
            if (!TrustedRoot(directory.PhysicalPath)) continue;
            var login = LoginPath(directory.PhysicalPath, app.Path, directory.Path);
            if (login is null) continue;
            foreach (var binding in site.Bindings.Where(b => b.Protocol == "https" && b.Host.Length > 0 && b.Thumbprint.Length > 0))
                yield return new(site.Name, binding.Host, binding.Port, login, site.Id, binding.Thumbprint, binding.Information);
        }
    }

    private TargetSite? Dedicated(NativeSite site)
    {
        if (site.State != "Started" || site.Applications.Length == 0 || site.Applications.Any(a => a.Directories.Length == 0)
            || site.Applications.SelectMany(a => a.Directories).Any(d => !TrustedRoot(d.PhysicalPath))) return null;
        var apps = site.Applications.Where(a => a.Path == "/").ToArray();
        if (apps.Length != 1) return null;
        var roots = apps[0].Directories.Where(d => d.Path == "/").ToArray();
        if (roots.Length != 1) return null;
        var login = LoginPath(roots[0].PhysicalPath, "/", "/");
        if (login is null) return null;
        var identity = JsonSerializer.Serialize(site.Applications.OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase).Select(a => new
        {
            path=a.Path.ToLowerInvariant(), pool=a.Pool.ToLowerInvariant(),
            directories=a.Directories.OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase).Select(d => new { path=d.Path.ToLowerInvariant(), root=FullPath(d.PhysicalPath).ToLowerInvariant() })
        }));
        return new(site, FullPath(roots[0].PhysicalPath), apps[0].Pool, login, identity);
    }

    private TargetSite Choose(NativeSite[] sites, string host)
    {
        var candidates = sites.Select(Dedicated).Where(s => s is not null).Cast<TargetSite>().ToArray();
        var exact = candidates.Where(s => s.Site.Bindings.Any(b => b.Host.Equals(host, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (exact.Length == 1) return exact[0];
        if (exact.Length == 0 && candidates.Length == 1) return candidates[0];
        throw new AppException(candidates.Length == 0 ? "mail.webmail.mailenable_not_ready" : "mail.webmail.mailenable_ambiguous", 409, 409);
    }

    private bool TrustedRoot(string path)
    {
        var installation = InstallationRoot();
        if (installation is null || !Path.IsPathFullyQualified(path)) return false;
        var full = FullPath(path);
        if (!Directory.Exists(full)) return false;
        if (!new[] { "BIN", "BIN64" }.Select(bin => FullPath(Path.Combine(installation, bin, "NETWebMail")))
            .Any(root => full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return false;
        for (var directory = new DirectoryInfo(full); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    private static string? LoginPath(string root, string app, string directory)
    {
        foreach (var relative in new[] { "Mondo/lang/sys/login.aspx", "lang/sys/login.aspx" })
        {
            var file = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file)) continue;
            // A link in the login path must not escape the verified WebMail directory either.
            for (FileSystemInfo? item = new FileInfo(file); item is not null; item = item is FileInfo f ? f.Directory : ((DirectoryInfo)item).Parent)
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            return "/" + string.Join('/', new[] { app.Trim('/'), directory.Trim('/'), relative }.Where(s => s.Length > 0));
        }
        return null;
    }
    private static string FullPath(string path) => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)).TrimEnd(Path.DirectorySeparatorChar);
    private static AppException NotReady() => new("mail.webmail.mailenable_not_ready", 409, 409);
}
