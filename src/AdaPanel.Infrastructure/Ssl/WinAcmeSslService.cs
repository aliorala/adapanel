using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Ssl;

public sealed class WinAcmeSslService(
    AdaPanelDbContext dbContext,
    IIisSiteService iisSiteService,
    JsonFileStore fileStore,
    IComponentBootstrapper componentBootstrapper,
    ILogger<WinAcmeSslService> logger) : ISslService
{
    private const string StatePath = "config/ssl.json";
    private const string WebAdministrationAssemblyPath = "%windir%\\System32\\inetsrv\\Microsoft.Web.Administration.dll";
    private const string ServerManagerTypeName = "Microsoft.Web.Administration.ServerManager";

    public async Task<SslWorkspaceDto> GetSiteWorkspaceAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var states = await fileStore.ReadAsync(StatePath, Array.Empty<SslCertificateDto>(), cancellationToken);

        return new SslWorkspaceDto
        {
            SiteName = site.Name,
            HostNames = site.Bindings
                .Where(binding => !string.IsNullOrWhiteSpace(binding.HostName))
                .Select(binding => binding.HostName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Certificates = states
                .Where(item => string.Equals(item.SiteName, site.Name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.NotAfterUtc)
                .ToArray(),
            HttpsRedirectEnabled = await IsHttpsRedirectEnabledAsync(site.Name, cancellationToken)
        };
    }

    public async Task<SslCertificateDto> IssueLetsEncryptAsync(IssueLetsEncryptRequest request, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(request.SiteName, cancellationToken);
        var hostName = ResolveHostName(site, request.HostName);
        var certificate = await TryIssueWithWinAcmeAsync(site, hostName, request.ContactEmail, request.UseStaging, cancellationToken)
            ?? CreateSelfSignedCertificate(hostName, site.Name);

        // İletişim e-postasını panel geneline kaydet ki mail sertifikası otomatik alınırken
        // (MailService) tekrar sorulmasın. Best-effort.
        if (!string.IsNullOrWhiteSpace(request.ContactEmail))
        {
            await fileStore.WriteAsync(
                Support.AcmeContactSettings.Path,
                new Support.AcmeContactSettings { Email = request.ContactEmail.Trim() },
                cancellationToken);
        }

        PersistCertificate(certificate);
        await EnsureHttpsBindingAsync(site.Name, hostName, 443, certificate, cancellationToken);

        // profesyonel paneller gibi: SSL kurulunca HTTP→HTTPS yönlendirmesi otomatik açılır (best-effort).
        await ApplyHttpsRedirectAsync(site.Name, true, cancellationToken);

        var dto = ToDto(site.Name, hostName, certificate, certificate.FriendlyName?.Contains("Let's Encrypt", StringComparison.OrdinalIgnoreCase) == true
            ? "letsencrypt"
            : "self-signed");

        await UpsertAsync(dto, cancellationToken);
        return dto;
    }

    // Domainsiz önizleme (nip.io): HIZLI self-signed HTTPS bağlaması — ACME beklemez, redirect'i değiştirmez.
    public async Task EnsurePreviewCertificateAsync(string siteName, string hostName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var certificate = CreateSelfSignedCertificate(hostName, site.Name);
        PersistCertificate(certificate);
        await EnsureHttpsBindingAsync(site.Name, hostName, 443, certificate, cancellationToken);
    }

    public async Task<SslCertificateDto> ImportCertificateAsync(ImportCertificateRequest request, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(request.SiteName, cancellationToken);
        var hostName = ResolveHostName(site, request.HostName);
        if (string.IsNullOrWhiteSpace(request.Base64Content))
        {
            throw new AppException("ssl.invalid_certificate_payload", 400, 400);
        }

        var raw = Convert.FromBase64String(request.Base64Content);
        var certificate = new X509Certificate2(
            raw,
            request.Password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet);

        PersistCertificate(certificate);
        await EnsureHttpsBindingAsync(site.Name, hostName, request.Port <= 0 ? 443 : request.Port, certificate, cancellationToken);
        await ApplyHttpsRedirectAsync(site.Name, true, cancellationToken);

        var dto = ToDto(site.Name, hostName, certificate, "manual");
        await UpsertAsync(dto, cancellationToken);
        return dto;
    }

    public async Task<RenewAllResultDto> RenewAllAsync(CancellationToken cancellationToken = default)
    {
        var wacs = await ResolveWinAcmeExecutableAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(wacs))
        {
            return new RenewAllResultDto
            {
                RenewedCount = 0,
                Output = "win-acme kurulu degil."
            };
        }

        var result = await ProcessRunner.RunAsync(wacs, "--renew --baseuri https://acme-v02.api.letsencrypt.org/", cancellationToken: cancellationToken);
        return new RenewAllResultDto
        {
            RenewedCount = result.Success ? 1 : 0,
            Output = string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput
        };
    }

    public async Task<SslWorkspaceDto> SetHttpsRedirectAsync(SetHttpsRedirectRequest request, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(request.SiteName, cancellationToken);
        await ApplyHttpsRedirectAsync(site.Name, request.Enabled, cancellationToken);
        return await GetSiteWorkspaceAsync(site.Name, cancellationToken);
    }

    // HTTP→HTTPS kalıcı (301) yönlendirme kuralını (URL Rewrite) ekler/kaldırır. ACME HTTP-01
    // challenge yolu (/.well-known/acme-challenge/) HARİÇ tutulur ki Let's Encrypt yenilemesi
    // bozulmasın. URL Rewrite modülü yoksa sessizce atlanır (SSL yine çalışır). Best-effort.
    private async Task ApplyHttpsRedirectAsync(string siteName, bool enabled, CancellationToken cancellationToken)
    {
        var site = siteName.Replace("'", "''");
        var script = enabled
            ? $$"""
                Import-Module WebAdministration -ErrorAction Stop
                if (-not (Get-WebGlobalModule | Where-Object { $_.Name -eq 'RewriteModule' })) { 'NOREWRITE'; return }
                $site = '{{site}}'; $f = 'system.webServer/rewrite/rules'; $name = 'AdaPanel-ForceHTTPS'
                Clear-WebConfiguration -pspath "IIS:\Sites\$site" -filter "$f/rule[@name='$name']" -ErrorAction SilentlyContinue
                Add-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter $f -name '.' -value @{name=$name; stopProcessing='True'}
                $r = "$f/rule[@name='$name']"
                Set-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter "$r/match" -name 'url' -value '(.*)'
                Add-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter "$r/conditions" -name '.' -value @{input='{HTTPS}'; pattern='off'}
                Add-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter "$r/conditions" -name '.' -value @{input='{REQUEST_URI}'; pattern='^/\.well-known/acme-challenge/'; negate='True'}
                Set-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter "$r/action" -name 'type' -value 'Redirect'
                Set-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter "$r/action" -name 'url' -value 'https://{HTTP_HOST}/{R:1}'
                Set-WebConfigurationProperty -pspath "IIS:\Sites\$site" -filter "$r/action" -name 'redirectType' -value 'Permanent'
                'OK'
                """
            : $$"""
                Import-Module WebAdministration -ErrorAction Stop
                $site = '{{site}}'
                Clear-WebConfiguration -pspath "IIS:\Sites\$site" -filter "system.webServer/rewrite/rules/rule[@name='AdaPanel-ForceHTTPS']" -ErrorAction SilentlyContinue
                'OK'
                """;

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);

        // URL Rewrite modülü yoksa panel bileşeni olarak OTOMATİK kur, sonra kuralı tekrar uygula.
        // (url-rewrite components.json'da default:true; ama kullanıcı ilk kurulumda önerilenleri
        // atlamışsa bile yönlendirme çalışsın.)
        if (enabled && result.Success && result.StandardOutput.Contains("NOREWRITE", StringComparison.Ordinal))
        {
            logger.LogInformation("URL Rewrite modülü kurulu değil; {Site} için otomatik kuruluyor (url-rewrite).", siteName);
            try
            {
                await componentBootstrapper.InstallAsync("url-rewrite", null, null, cancellationToken);
                result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "url-rewrite otomatik kurulumu başarısız ({Site}).", siteName);
            }
        }

        if (!result.Success)
        {
            logger.LogWarning("HTTP->HTTPS yönlendirme {State} ayarlanamadı (site {Site}): {Error}",
                enabled ? "açma" : "kapatma", siteName, result.StandardError);
        }
        else if (result.StandardOutput.Contains("NOREWRITE", StringComparison.Ordinal))
        {
            logger.LogWarning("URL Rewrite modülü kurulu değil ve otomatik kurulamadı; {Site} için HTTP->HTTPS yönlendirme uygulanamadı.", siteName);
        }
    }

    private async Task<bool> IsHttpsRedirectEnabledAsync(string siteName, CancellationToken cancellationToken)
    {
        var site = siteName.Replace("'", "''");
        var script = $$"""
            Import-Module WebAdministration -ErrorAction SilentlyContinue
            $site = '{{site}}'
            $rule = Get-WebConfiguration -pspath "IIS:\Sites\$site" -filter "system.webServer/rewrite/rules/rule[@name='AdaPanel-ForceHTTPS']" -ErrorAction SilentlyContinue
            if ($rule) { 'YES' } else { 'NO' }
            """;
        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        return result.Success && result.StandardOutput.Contains("YES", StringComparison.Ordinal);
    }

    public async Task<bool> TrySecureHostAsync(string siteName, string hostName, string contactEmail, CancellationToken cancellationToken = default)
    {
        try
        {
            var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);

            // 1) host'u kapsayan geçerli bir sertifika zaten varsa (ör. yüklenen wildcard) onu bağla.
            var existing = FindBestCertificate(hostName);
            if (existing is not null && existing.NotAfter > DateTime.Now)
            {
                await EnsureHttpsBindingAsync(site.Name, hostName, 443, existing, cancellationToken);
                return true;
            }

            // 2) yoksa Let's Encrypt dene. ÖNCE host DNS'te çözülüyor mu bak — çözülmüyorsa win-acme
            // ~120 sn takılır; hızlıca atla ki paylaşımlı site cert isteğini yavaşlatmasın.
            try
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(hostName, cancellationToken);
                if (addresses.Length == 0)
                {
                    return false;
                }
            }
            catch
            {
                return false; // host çözülmüyor → Let's Encrypt denemeye gerek yok
            }

            var issued = await TryIssueWithWinAcmeAsync(site, hostName, contactEmail, false, cancellationToken);
            if (issued is null)
            {
                return false;
            }

            PersistCertificate(issued);
            await EnsureHttpsBindingAsync(site.Name, hostName, 443, issued, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Host '{Host}' güvenli (HTTPS) yapılamadı (best-effort).", hostName);
            return false;
        }
    }

    private async Task<X509Certificate2?> TryIssueWithWinAcmeAsync(
        AdaPanel.Core.Dtos.Sites.SiteDto site,
        string hostName,
        string contactEmail,
        bool useStaging,
        CancellationToken cancellationToken)
    {
        var wacs = await ResolveWinAcmeExecutableAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(wacs) || string.IsNullOrWhiteSpace(contactEmail))
        {
            return null;
        }

        var baseUri = useStaging
            ? "https://acme-staging-v02.api.letsencrypt.org/"
            : "https://acme-v02.api.letsencrypt.org/";

        var before = FindBestCertificate(hostName);
        var args = $"--target iis --siteid {site.Id} --host {hostName} --installation iis --emailaddress {contactEmail} --accepttos --baseuri {baseUri}";
        // HTTP-01 doğrulaması domain sunucuya işaret etmiyorsa/80 kapalıysa win-acme uzun
        // süre takılabilir. 120 sn sonra iptal et ki istek sonsuza kadar dönmesin (self-signed'a düşer).
        var result = await ProcessRunner.RunAsync(wacs, args, cancellationToken: cancellationToken, timeout: TimeSpan.FromSeconds(120));
        if (!result.Success)
        {
            return null;
        }

        var after = FindBestCertificate(hostName);
        return after?.Thumbprint != before?.Thumbprint ? after : before;
    }

    private async Task<string> ResolveWinAcmeExecutableAsync(CancellationToken cancellationToken)
    {
        var component = await dbContext.InstalledComponents
            .FirstOrDefaultAsync(item => item.ComponentId == "win-acme" && item.Status == ComponentInstallStatus.Installed, cancellationToken);

        if (component is null || string.IsNullOrWhiteSpace(component.InstallDir) || !Directory.Exists(component.InstallDir))
        {
            return string.Empty;
        }

        return Directory.GetFiles(component.InstallDir, "wacs.exe", SearchOption.AllDirectories).FirstOrDefault() ?? string.Empty;
    }

    private static string ResolveHostName(AdaPanel.Core.Dtos.Sites.SiteDto site, string? requestedHostName)
    {
        var hostName = string.IsNullOrWhiteSpace(requestedHostName)
            ? site.Bindings.FirstOrDefault(binding => !string.IsNullOrWhiteSpace(binding.HostName))?.HostName
            : requestedHostName.Trim();

        if (string.IsNullOrWhiteSpace(hostName))
        {
            throw new AppException("ssl.host_name_required", 400, 400);
        }

        return hostName;
    }

    private static X509Certificate2 CreateSelfSignedCertificate(string hostName, string siteName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={hostName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(hostName);
        request.CertificateExtensions.Add(sanBuilder.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        var export = certificate.Export(X509ContentType.Pfx);
        var imported = new X509Certificate2(export, string.Empty, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet);
        imported.FriendlyName = $"AdaPanel SelfSigned {siteName}";
        return imported;
    }

    // IIS/HTTP.sys YALNIZCA LocalMachine\My deposundaki sertifikayı bağlayabilir. Bu yüzden
    // CurrentUser'a düşmeyiz (binding "bağlanamadı" verirdi); LocalMachine başarısızsa hatayı
    // açıkça yükseltiriz. Aynı parmak izi zaten varsa tekrar eklemeyiz ki özel anahtarlı
    // kopyanın üzerine anahtarsız kopya yazılmasın.
    private static void PersistCertificate(X509Certificate2 certificate)
    {
        using var machineStore = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        machineStore.Open(OpenFlags.ReadWrite);

        var alreadyPresent = !string.IsNullOrWhiteSpace(certificate.Thumbprint) &&
            machineStore.Certificates
                .Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false)
                .Count > 0;

        if (!alreadyPresent)
        {
            machineStore.Add(certificate);
        }
    }

    private static X509Certificate2? FindBestCertificate(string hostName)
    {
        // win-acme, IIS kurulumunda LE sertifikasını LocalMachine\WebHosting deposuna koyar
        // (My DEĞİL). Önce WebHosting taranır; bulunamazsa My. Özel anahtarlı (bağlanabilir)
        // sertifika tercih edilir, yoksa son çare anahtarsız eşleşme.
        var stores = new (string Name, StoreLocation Location)[]
        {
            ("WebHosting", StoreLocation.LocalMachine),
            ("My", StoreLocation.LocalMachine),
            ("My", StoreLocation.CurrentUser)
        };

        X509Certificate2? fallback = null;
        foreach (var (name, location) in stores)
        {
            X509Store store;
            try
            {
                store = new X509Store(name, location);
                store.Open(OpenFlags.ReadOnly);
            }
            catch
            {
                continue;
            }

            using (store)
            {
                var matches = store.Certificates
                    .OfType<X509Certificate2>()
                    .Where(cert => cert.Subject.Contains($"CN={hostName}", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(cert => cert.NotAfter)
                    .ToList();

                var withKey = matches.FirstOrDefault(cert => cert.HasPrivateKey);
                if (withKey is not null)
                {
                    return withKey;
                }

                fallback ??= matches.FirstOrDefault();
            }
        }

        return fallback;
    }

    // IIS HTTPS binding'i artık PowerShell WebAdministration ile yapılır (New-WebBinding -SslFlags 1
    // + AddSslCertificate). Microsoft.Web.Administration reflection bazı IIS sürümlerinde SNI
    // sertifikası bağlamayı "bağlanamadı" ile reddediyordu; PowerShell yöntemi güvenilir çalışır.
    private async Task EnsureHttpsBindingAsync(string siteName, string hostName, int port, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        var thumbprint = certificate.Thumbprint ?? string.Empty;
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            throw new AppException("ssl.binding_failed", 500, 500, "Sertifika parmak izi (thumbprint) yok.");
        }

        var site = siteName.Replace("'", "''");
        var dn = (hostName ?? string.Empty).Replace("'", "''");
        var thumb = thumbprint.Replace("'", "''");

        var script = $$"""
            Import-Module WebAdministration -ErrorAction Stop
            $ErrorActionPreference = 'Stop'
            $site = '{{site}}'; $dn = '{{dn}}'; $port = {{port}}; $thumb = '{{thumb}}'
            Get-WebBinding -Name $site -Protocol https -Port $port -HostHeader $dn -ErrorAction SilentlyContinue |
              Remove-WebBinding -ErrorAction SilentlyContinue
            if ($dn) {
              New-WebBinding -Name $site -Protocol https -Port $port -HostHeader $dn -SslFlags 1
              $b = Get-WebBinding -Name $site -Protocol https -Port $port -HostHeader $dn
            } else {
              New-WebBinding -Name $site -Protocol https -Port $port
              $b = Get-WebBinding -Name $site -Protocol https -Port $port
            }
            # Sertifika hangi depodaysa ORADAN bağla. win-acme LE sertifikasını WebHosting'e koyar;
            # self-signed/import ise My'da olur. Yanlış depo "güvenli değil" / boş binding'e yol açar.
            $store = 'My'
            if (-not (Test-Path "Cert:\LocalMachine\My\$thumb") -and (Test-Path "Cert:\LocalMachine\WebHosting\$thumb")) {
              $store = 'WebHosting'
            }
            $b.AddSslCertificate($thumb, $store)
            'OK'
            """;

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException(
                "ssl.binding_failed",
                500,
                500,
                string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
        }
    }

    public async Task RemoveSiteAsync(string siteName, CancellationToken cancellationToken = default)
    {
        await fileStore.UpdateAsync(StatePath, Array.Empty<SslCertificateDto>(), existing => existing
            .Where(item => !string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase))
            .ToArray(), cancellationToken);
    }

    private async Task UpsertAsync(SslCertificateDto dto, CancellationToken cancellationToken)
    {
        // Atomik oku-değiştir-yaz (eşzamanlı SSL işlemlerinde kayıp güncelleme olmasın).
        await fileStore.UpdateAsync(StatePath, Array.Empty<SslCertificateDto>(), existing =>
        {
            var states = existing.ToList();
            var index = states.FindIndex(item =>
                string.Equals(item.SiteName, dto.SiteName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.HostName, dto.HostName, StringComparison.OrdinalIgnoreCase) &&
                item.Port == dto.Port);

            if (index >= 0)
            {
                states[index] = dto;
            }
            else
            {
                states.Add(dto);
            }

            return states.ToArray();
        }, cancellationToken);
    }

    private static SslCertificateDto ToDto(string siteName, string hostName, X509Certificate2 certificate, string source)
    {
        return new SslCertificateDto
        {
            SiteName = siteName,
            HostName = hostName,
            Thumbprint = certificate.Thumbprint ?? string.Empty,
            FriendlyName = certificate.FriendlyName ?? string.Empty,
            Subject = certificate.Subject,
            NotAfterUtc = certificate.NotAfter.ToUniversalTime(),
            Source = source,
            IsHttpsBindingPresent = true,
            Port = 443
        };
    }

    private static IDisposable CreateServerManager()
    {
        var assemblyPath = Environment.ExpandEnvironmentVariables(WebAdministrationAssemblyPath);
        if (!File.Exists(assemblyPath))
        {
            throw new AppException("sites.iis_not_available", 503, 503);
        }

        var assembly = Assembly.LoadFrom(assemblyPath);
        var serverManagerType = assembly.GetType(ServerManagerTypeName)
            ?? throw new AppException("sites.iis_not_available", 503, 503);

        return Activator.CreateInstance(serverManagerType) as IDisposable
            ?? throw new AppException("sites.iis_not_available", 503, 503);
    }

    private static object GetPropertyObject(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName)
            ?? throw new MissingMemberException(target.GetType().FullName, propertyName);

        try
        {
            return property.GetValue(target)
                ?? throw new AppException("sites.iis_not_available", 503, 503);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static object? FindByName(object collection, string name)
    {
        return Enumerate(collection).FirstOrDefault(item =>
            string.Equals(GetStringProperty(item, "Name"), name, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<object> Enumerate(object collection)
    {
        return collection is System.Collections.IEnumerable enumerable
            ? enumerable.Cast<object>()
            : [];
    }

    private static string GetStringProperty(object target, string propertyName)
    {
        return GetPropertyObject(target, propertyName).ToString() ?? string.Empty;
    }

    private static void SetPropertyValue(object target, string propertyName, object value)
    {
        var property = target.GetType().GetProperty(propertyName)
            ?? throw new MissingMemberException(target.GetType().FullName, propertyName);

        try
        {
            property.SetValue(target, value);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
        }
    }

    private static object InvokeMethod(object target, string methodName, params object[] arguments)
    {
        try
        {
            return target.GetType().InvokeMember(
                methodName,
                BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                null,
                target,
                arguments)
                ?? throw new InvalidOperationException($"IIS method '{methodName}' returned no result.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static AppException MapIisException(Exception exception)
    {
        // Dile bağımsız (HRESULT + İngilizce) sınıflandırma — Türkçe Windows'ta da doğru çalışır.
        var message = Support.Win32ErrorHelper.Flatten(exception);
        if (Support.Win32ErrorHelper.IsAccessDenied(message))
        {
            return CreateDiagnosticException("sites.iis_permission_denied", 403, exception);
        }

        if (Support.Win32ErrorHelper.IsInUse(message))
        {
            return CreateDiagnosticException("sites.iis_configuration_locked", 409, exception);
        }

        if (message.Contains("section", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("applicationHost.config", StringComparison.OrdinalIgnoreCase))
        {
            return CreateDiagnosticException("sites.iis_install_required", 503, exception);
        }

        return CreateDiagnosticException("ssl.binding_failed", 500, exception);
    }

    private static AppException CreateDiagnosticException(string messageKey, int statusCode, Exception exception)
    {
        return new AppException(messageKey, statusCode, statusCode, BuildDiagnosticDetails(exception));
    }

    private static string BuildDiagnosticDetails(Exception exception)
    {
        var details = exception.ToString();
        const int maxLength = 8000;
        return details.Length <= maxLength
            ? details
            : details[..maxLength] + "\r\n... (diagnostic output truncated)";
    }

    private static string FlattenExceptionMessage(Exception exception)
    {
        var builder = new StringBuilder(exception.Message);
        var current = exception.InnerException;
        while (current is not null)
        {
            builder.Append(' ').Append(current.Message);
            current = current.InnerException;
        }

        return builder.ToString();
    }
}
