using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Xml.Linq;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Waf;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Waf;

public sealed class WafService(
    IIisSiteService iisSiteService,
    IConcurrencyLock concurrencyLock,
    JsonFileStore fileStore) : IWafService
{
    private const string StatePath = "config/waf.json";
    private const string WebAdministrationAssemblyPath = "%windir%\\System32\\inetsrv\\Microsoft.Web.Administration.dll";
    private const string ServerManagerTypeName = "Microsoft.Web.Administration.ServerManager";

    public async Task<SiteSecurityConfigurationDto> GetSiteConfigurationAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var states = (await fileStore.ReadAsync(StatePath, Array.Empty<SecurityState>(), cancellationToken)).ToList();
        var state = states.FirstOrDefault(item => string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase));
        if (state is null)
        {
            state = new SecurityState
            {
                SiteName = site.Name,
                IsIsolated = site.ApplicationPoolName.StartsWith("AdaPanel.", StringComparison.OrdinalIgnoreCase),
                RequestsPerMinute = 0,
                Principal = $"IIS AppPool\\{site.ApplicationPoolName}"
            };
            states.Add(state);
            await fileStore.WriteAsync(StatePath, states, cancellationToken);
        }

        return new SiteSecurityConfigurationDto
        {
            SiteName = site.Name,
            ApplicationPoolName = site.ApplicationPoolName,
            PhysicalPath = site.PhysicalPath,
            IsIsolated = state.IsIsolated,
            FolderAclApplied = state.FolderAclApplied,
            BasicRulesEnabled = state.BasicRulesEnabled,
            RequestsPerMinute = state.RequestsPerMinute,
            Principal = state.Principal
        };
    }

    public async Task<SiteSecurityConfigurationDto> IsolateAppPoolAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var targetPool = $"AdaPanel.{site.Name}";

        try
        {
            // IIS KUYRUĞUNA GİRİYOR. applicationHost.config TEK bir dosyadır; iki ServerManager
            // aynı anda oku-değiştir-yaz yaparsa biri sessizce kaybolur ya da CommitChanges
            // paylaşım ihlaliyle düşer. Panelin diğer IIS yazıcıları (site, PHP, ASP.NET, reverse
            // proxy, nginx, app runtime) zaten bu kuyruktaydı — bu sınıf dışarıda kalmıştı.
            await concurrencyLock.RunExclusiveAsync(ConcurrencyKeys.Iis, () =>
            {
                using var serverManager = CreateServerManager();
                var sites = GetPropertyObject(serverManager, "Sites");
                var siteObject = FindByName(sites, site.Name) ?? throw new AppException("sites.site_not_found", 404, 404);
                var applications = GetPropertyObject(siteObject, "Applications");
                var rootApplication = Enumerate(applications).First(item => string.Equals(GetStringProperty(item, "Path"), "/", StringComparison.OrdinalIgnoreCase));
                var pools = GetPropertyObject(serverManager, "ApplicationPools");
                var pool = FindByName(pools, targetPool) ?? InvokeMethod(pools, "Add", targetPool);
                SetPropertyValue(pool, "ManagedRuntimeVersion", string.Empty);
                SetPropertyValue(pool, "AutoStart", true);
                SetPropertyValue(rootApplication, "ApplicationPoolName", targetPool);
                InvokeMethod(serverManager, "CommitChanges");
                return Task.CompletedTask;
            }, cancellationToken);
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapIisException(exception);
        }

        var durum = await SaveStateAsync(site, isIsolated: true, cancellationToken: cancellationToken);

        /*
         * KLASÖR İZNİ HAVUZU TAKİP ETMELİ.
         *
         * Klasör ACL'i bir HAVUZ KİMLİĞİNE verilir. Bu metot ise sitenin havuzunu değiştirir —
         * yani daha önce uygulanmış izin artık ESKİ kimliğe yazılıdır. Üstelik ACL uygulanırken
         * miras kırıldığı için IIS_IUSRS üzerinden yedek bir erişim de kalmaz: site 401.3/500
         * verir ve kullanıcı bunu izolasyona bağlayamaz.
         *
         * İzin daha önce uygulanmadıysa dokunulmaz — izolasyon, kullanıcının istemediği bir
         * kısıtlamayı kendiliğinden getirmemeli.
         */
        if (durum.FolderAclApplied)
        {
            // Principal boş: ApplyFolderAclAsync siteyi TAZE okur ve yeni havuz kimliğini kullanır.
            return await ApplyFolderAclAsync(
                siteName,
                new ApplyFolderAclRequest { Principal = string.Empty, Rights = string.Empty },
                cancellationToken);
        }

        return durum;
    }

    public async Task<SiteSecurityConfigurationDto> ApplyFolderAclAsync(string siteName, ApplyFolderAclRequest request, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        // Boş bırakılırsa sitenin O ANKİ havuz kimliği kullanılır — site taze okunduğu için
        // izolasyondan sonra da doğru havuza yazar.
        var principal = string.IsNullOrWhiteSpace(request.Principal)
            ? $"IIS AppPool\\{site.ApplicationPoolName}"
            : Support.AclInputValidator.EnsurePrincipal(request.Principal);

        var applicationRoot = string.IsNullOrWhiteSpace(site.ApplicationRootPath)
            ? site.PhysicalPath
            : site.ApplicationRootPath;
        // Doğrulanmadan icacls'e giden bir izin dizesi, sitenin kendi kökünü erişilemez
        // bırakabilir (ör. "R" verilirse PHP kendi klasörüne yazamaz) ya da komuta bayrak
        // sızdırabilirdi. Kural AclInputValidator'da, dosya yöneticisiyle ORTAK.
        var rights = string.IsNullOrWhiteSpace(request.Rights)
            ? "M"
            : Support.AclInputValidator.EnsureRights(request.Rights);
        var rootLiteral = applicationRoot.Replace("'", "''");
        var poolLiteral = principal.Replace("'", "''");
        var siteLiteral = site.Name.Replace("'", "''");

        // PLESK-BENZERİ TAM KİRACI İZOLASYONU: (1) statik dosyalar IUSR ile DEĞİL app pool kimliğiyle sunulsun
        // (anonymousAuthentication.userName='') → sitenin TÜM erişimi (PHP FastCGI + statik) tek kimlikten geçer.
        // (2) site kökünde ACL kalıtımını KIR: üst klasörden miras gelen IIS_IUSRS/Users GENİŞ okuması düşer —
        // aksi halde DİĞER sitelerin app pool kimlikleri (hepsi IIS_IUSRS üyesi) bu sitenin dosyalarını okuyabilirdi
        // (izolasyon yok). Yalnız BU sitenin app pool kimliği (Modify) + SYSTEM + Administrators (SID = locale
        // BAĞIMSIZ) kalır. /T ve /reset YOK → FTP kullanıcısının açık grant'i ve alt klasör kalıtımı korunur
        // (çocuklar yeni kök ACL'ini miras alır). Anonim kimlik AYARLANAMAZSA icacls'e GEÇİLMEZ (exit 2) → statik
        // içerik kırılmasın diye izolasyon uygulanmadan güvenli durulur (fail-safe).
        var script = $$"""
            $site = '{{siteLiteral}}'
            try {
              Import-Module WebAdministration -ErrorAction Stop
              Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $site -Filter 'system.webServer/security/authentication/anonymousAuthentication' -Name userName -Value '' -ErrorAction Stop
            } catch {
              Write-Error ("Anonim kimlik app pool kimligine ayarlanamadi: " + $_.Exception.Message); exit 2
            }
            $grant = '{{poolLiteral}}' + ':(OI)(CI){{rights}}'
            icacls '{{rootLiteral}}' /inheritance:r /grant:r "$grant" "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" /C /Q | Out-Null
            if ($LASTEXITCODE -ne 0) { exit 1 }
            """;
        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);

        if (!result.Success)
        {
            throw new AppException("security.acl_failed", 500, 500, result.StandardError);
        }

        return await SaveStateAsync(site, folderAclApplied: true, principal: principal, cancellationToken: cancellationToken);
    }

    public async Task<SiteSecurityConfigurationDto> SetRateLimitAsync(string siteName, SetSiteRateLimitRequest request, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var webConfigPath = Path.Combine(site.PhysicalPath, "web.config");
        Directory.CreateDirectory(site.PhysicalPath);
        var document = File.Exists(webConfigPath)
            ? XDocument.Load(webConfigPath, LoadOptions.PreserveWhitespace)
            : new XDocument(new XElement("configuration"));

        var configuration = document.Element("configuration")!;
        var systemWebServer = EnsureElement(configuration, "system.webServer");
        var security = EnsureElement(systemWebServer, "security");
        var dynamicIpSecurity = EnsureElement(security, "dynamicIpSecurity");
        dynamicIpSecurity.SetAttributeValue("denyByRequestRate", true);
        dynamicIpSecurity.SetAttributeValue("maxRequests", Math.Max(1, request.RequestsPerMinute));
        dynamicIpSecurity.SetAttributeValue("requestIntervalInMilliseconds", 60000);

        document.Save(webConfigPath);
        return await SaveStateAsync(site, requestsPerMinute: Math.Max(1, request.RequestsPerMinute), cancellationToken: cancellationToken);
    }

    public async Task<SiteSecurityConfigurationDto> EnableBasicRulesAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var webConfigPath = Path.Combine(site.PhysicalPath, "web.config");
        Directory.CreateDirectory(site.PhysicalPath);
        var document = File.Exists(webConfigPath)
            ? XDocument.Load(webConfigPath, LoadOptions.PreserveWhitespace)
            : new XDocument(new XElement("configuration"));

        var configuration = document.Element("configuration")!;
        var systemWebServer = EnsureElement(configuration, "system.webServer");
        var security = EnsureElement(systemWebServer, "security");
        var requestFiltering = EnsureElement(security, "requestFiltering");
        requestFiltering.SetAttributeValue("removeServerHeader", true);
        requestFiltering.SetAttributeValue("allowDoubleEscaping", false);

        var httpProtocol = EnsureElement(systemWebServer, "httpProtocol");
        var customHeaders = EnsureElement(httpProtocol, "customHeaders");
        EnsureHeader(customHeaders, "X-Frame-Options", "SAMEORIGIN");
        EnsureHeader(customHeaders, "X-Content-Type-Options", "nosniff");
        EnsureHeader(customHeaders, "Referrer-Policy", "same-origin");

        document.Save(webConfigPath);
        return await SaveStateAsync(site, basicRulesEnabled: true, cancellationToken: cancellationToken);
    }

    public async Task RemoveSiteAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var states = (await fileStore.ReadAsync(StatePath, Array.Empty<SecurityState>(), cancellationToken)).ToList();
        var kaldirilan = states.RemoveAll(item => string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase));

        // Kayıt yoksa dosyaya DOKUNMA: silme işlemi idempotent olmalı ve gereksiz yazma,
        // eşzamanlı bir güncellemeyi ezme riski taşır.
        if (kaldirilan > 0)
        {
            await fileStore.WriteAsync(StatePath, states, cancellationToken);
        }
    }

    private async Task<SiteSecurityConfigurationDto> SaveStateAsync(
        AdaPanel.Core.Dtos.Sites.SiteDto site,
        bool? isIsolated = null,
        bool? folderAclApplied = null,
        bool? basicRulesEnabled = null,
        int? requestsPerMinute = null,
        string? principal = null,
        CancellationToken cancellationToken = default)
    {
        var states = (await fileStore.ReadAsync(StatePath, Array.Empty<SecurityState>(), cancellationToken)).ToList();
        var index = states.FindIndex(item => string.Equals(item.SiteName, site.Name, StringComparison.OrdinalIgnoreCase));
        var state = index >= 0 ? states[index] : new SecurityState { SiteName = site.Name };

        state.IsIsolated = isIsolated ?? state.IsIsolated;
        state.FolderAclApplied = folderAclApplied ?? state.FolderAclApplied;
        state.BasicRulesEnabled = basicRulesEnabled ?? state.BasicRulesEnabled;
        state.RequestsPerMinute = requestsPerMinute ?? state.RequestsPerMinute;
        state.Principal = principal ?? state.Principal;

        if (index >= 0)
        {
            states[index] = state;
        }
        else
        {
            states.Add(state);
        }

        await fileStore.WriteAsync(StatePath, states, cancellationToken);
        return await GetSiteConfigurationAsync(site.Name, cancellationToken);
    }

    private static XElement EnsureElement(XElement parent, string name)
    {
        var element = parent.Element(name);
        if (element is null)
        {
            element = new XElement(name);
            parent.Add(element);
        }

        return element;
    }

    private static void EnsureHeader(XElement customHeaders, string name, string value)
    {
        var existing = customHeaders.Elements("add")
            .FirstOrDefault(element => string.Equals((string?)element.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            customHeaders.Add(new XElement("add",
                new XAttribute("name", name),
                new XAttribute("value", value)));
            return;
        }

        existing.SetAttributeValue("value", value);
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

        return CreateDiagnosticException("security.configuration_failed", 500, exception);
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

    private sealed class SecurityState
    {
        public string SiteName { get; set; } = string.Empty;

        public bool IsIsolated { get; set; }

        public bool FolderAclApplied { get; set; }

        public bool BasicRulesEnabled { get; set; }

        public int RequestsPerMinute { get; set; }

        public string Principal { get; set; } = string.Empty;
    }
}
