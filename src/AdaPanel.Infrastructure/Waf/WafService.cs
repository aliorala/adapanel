using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Security.Principal;
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

    /// <summary>
    /// Panelin yönettiği uygulama havuzu adı öneki. <c>WebAdministrationSiteService</c> ile AYNI
    /// sözleşme — izolasyonun ölçütü de, site oluşturulurken açılan havuzun adı da budur.
    /// </summary>
    private const string ManagedPoolPrefix = "AdaPanel.";
    private const string WebAdministrationAssemblyPath = "%windir%\\System32\\inetsrv\\Microsoft.Web.Administration.dll";
    private const string ServerManagerTypeName = "Microsoft.Web.Administration.ServerManager";

    /// <summary>
    /// Sitenin güvenlik durumunu GERÇEKTEN ÖLÇEREK döner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eski hâlinde bu metot yalnızca <c>config/waf.json</c>'u okuyordu ve <c>IsIsolated</c>
    /// gerçeğe SADECE kayıt ilk oluşturulurken bakıyordu. Sonrasında havuz değişse de (elle
    /// <c>DefaultAppPool</c>'a alınsa da) kayıt "izole" demeye devam ediyordu — üstelik aynı
    /// DTO'nun içinde gerçek havuz adı yan yana duruyordu, yani ekran kendi kendisiyle çelişiyordu.
    /// <c>BasicRulesEnabled</c> ile <c>RequestsPerMinute</c> ise sitenin <c>web.config</c>'inde
    /// yaşıyor: o dosyayı silen/ezen her yol (FTP, dosya yöneticisi, git dağıtımı, SiteBuilder,
    /// WordPress kurulumu) bu ayarları da götürüyor ve panel bunu hiç görmüyordu.
    /// </para>
    /// <para>
    /// Kayıt tamamen atılmadı: hangi KİMLİĞE ACL verildiği (<c>Principal</c>) dosya sisteminden
    /// güvenilir biçimde geri türetilemez ve izolasyon sonrası eski kimliğin izni tam da o kayıt
    /// sayesinde takip ediliyor. Kayıt artık NİYETİ, DTO ise GERÇEĞİ taşır.
    /// </para>
    /// <para>
    /// Okuma yolunda ARTIK YAZMA YOK: eski kod kayıt yoksa dosyaya bir satır ekliyordu; salt-okunur
    /// bir sorgunun yan etkisi olarak yapılan yazma, eşzamanlı bir güncellemeyle yarışabiliyordu.
    /// </para>
    /// </remarks>
    public async Task<SiteSecurityConfigurationDto> GetSiteConfigurationAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var states = await fileStore.ReadAsync(StatePath, Array.Empty<SecurityState>(), cancellationToken);
        var state = states.FirstOrDefault(item => string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase));

        var principal = state is null || string.IsNullOrWhiteSpace(state.Principal)
            ? $"IIS AppPool\\{site.ApplicationPoolName}"
            : state.Principal;

        var applicationRoot = string.IsNullOrWhiteSpace(site.ApplicationRootPath)
            ? site.PhysicalPath
            : site.ApplicationRootPath;

        var izolasyon = MeasureIsolation(site.ApplicationPoolName);
        var acl = MeasureFolderAcl(applicationRoot, principal);
        var webConfig = MeasureWebConfig(WebConfigPath(site.PhysicalPath));

        return new SiteSecurityConfigurationDto
        {
            SiteName = site.Name,
            ApplicationPoolName = site.ApplicationPoolName,
            PhysicalPath = site.PhysicalPath,

            // ÖLÇÜM ÖNCELİKLİ; ölçülemediyse panelin son bildiği değere düşülür ve bu
            // *Measured=false ile SÖYLENİR. Ölçülemeyeni "kapalı" göstermek yanlış alarm üretir.
            IsIsolated = izolasyon ?? state?.IsIsolated ?? false,
            IsIsolatedMeasured = izolasyon.HasValue,

            FolderAclApplied = acl ?? state?.FolderAclApplied ?? false,
            FolderAclMeasured = acl.HasValue,

            BasicRulesEnabled = webConfig.BasicRules ?? state?.BasicRulesEnabled ?? false,
            BasicRulesMeasured = webConfig.BasicRules.HasValue,

            RequestsPerMinute = webConfig.RequestsPerMinute ?? state?.RequestsPerMinute ?? 0,
            RateLimitMeasured = webConfig.RequestsPerMinute.HasValue,

            Principal = principal
        };
    }

    private static string WebConfigPath(string physicalPath) => Path.Combine(physicalPath ?? string.Empty, "web.config");

    /// <summary>
    /// Site KENDİ havuzunda mı — IIS'ten taze okunan havuz adından ölçülür.
    /// </summary>
    /// <returns><c>null</c> = havuz adı okunamadı (ÖLÇÜLEMEDİ).</returns>
    /// <remarks>
    /// Ölçüt, <c>WebAdministrationSiteService</c>'in yönettiği havuz adı önekidir
    /// (<c>AdaPanel.</c>) — panelin izolasyonda oluşturduğu havuz da (<c>IsolateAppPoolAsync</c>)
    /// site oluşturulurken açılan havuz da bu öneki taşır.
    /// </remarks>
    internal static bool? MeasureIsolation(string? applicationPoolName)
    {
        if (string.IsNullOrWhiteSpace(applicationPoolName))
        {
            return null;
        }

        return applicationPoolName.StartsWith(ManagedPoolPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Klasör ACL'i GERÇEKTEN uygulanmış mı — dosya sisteminden ölçülür.
    /// </summary>
    /// <returns>
    /// <c>true</c> = miras kırılmış VE kimliğe açık (explicit) izin var (yani
    /// <see cref="ApplyFolderAclAsync"/>'in yaptığı şey yerinde duruyor).
    /// <c>false</c> = klasör okundu, bu ikisinden biri yok.
    /// <c>null</c> = ÖLÇÜLEMEDİ (ACL okunamadı, kimlik çözülemedi).
    /// </returns>
    internal static bool? MeasureFolderAcl(string? folder, string principal)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(principal))
        {
            return null;
        }

        try
        {
            var info = new DirectoryInfo(folder);
            if (!info.Exists)
            {
                // ÖLÇÜLDÜ: klasör yok → uygulanmış bir ACL de yok.
                return false;
            }

            var security = info.GetAccessControl(AccessControlSections.Access);

            // ApplyFolderAclAsync `icacls /inheritance:r` çalıştırır: mirasın kırık OLMAMASI,
            // izolasyonun geri alındığı (diğer sitelerin havuz kimlikleri tekrar okuyabildiği)
            // anlamına gelir. Yalnız "izin var mı" diye bakmak bu geri alınışı gizlerdi.
            if (!security.AreAccessRulesProtected)
            {
                return false;
            }

            foreach (var rule in security.GetAccessRules(true, false, typeof(NTAccount)).Cast<FileSystemAccessRule>())
            {
                if (rule.AccessControlType == AccessControlType.Allow &&
                    string.Equals(rule.IdentityReference.Value, principal, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // ACL okunamadı / SID çözülemedi: ÖLÇEMEDİK. "Uygulanmamış" DEMİYORUZ.
            return null;
        }
    }

    /// <summary>Sitenin <c>web.config</c>'inden okunan ölçüm. Alanlar <c>null</c> ise ÖLÇÜLEMEDİ.</summary>
    internal sealed record WebConfigMeasurement(bool? BasicRules, int? RequestsPerMinute);

    /// <summary>
    /// Temel kurallar ve hız sınırı sitenin <c>web.config</c>'inde DURUYOR MU — dosyadan ölçülür.
    /// </summary>
    /// <remarks>
    /// Dosyanın hiç OLMAMASI ölçülebilir bir gerçektir: kural da yoktur, sınır da (false / 0).
    /// Dosyanın OKUNAMAMASI (kilitli, bozuk XML) ise ölçümün yapılamadığı anlamına gelir; bu iki
    /// hâli aynı kefeye koymak, panelin bir dosya kilidini "güvenlik kuralları kaldırılmış" diye
    /// raporlamasına yol açardı.
    /// </remarks>
    internal static WebConfigMeasurement MeasureWebConfig(string webConfigPath)
    {
        try
        {
            if (!File.Exists(webConfigPath))
            {
                return new WebConfigMeasurement(false, 0);
            }

            var systemWebServer = XDocument.Load(webConfigPath)
                .Element("configuration")?
                .Element("system.webServer");

            if (systemWebServer is null)
            {
                return new WebConfigMeasurement(false, 0);
            }

            return new WebConfigMeasurement(
                MeasureBasicRules(systemWebServer),
                MeasureRateLimit(systemWebServer));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Xml.XmlException
                or ArgumentException
                or NotSupportedException)
        {
            return new WebConfigMeasurement(null, null);
        }
    }

    /// <summary>
    /// <see cref="EnableBasicRulesAsync"/>'in yazdığı HER ŞEY yerinde mi. Yarısı silinmişse cevap
    /// "hayır"dır: eksik uygulanmış bir sertleştirmeye "açık" demek, olmayan bir korumaya güvenmektir.
    /// </summary>
    private static bool MeasureBasicRules(XElement systemWebServer)
    {
        var requestFiltering = systemWebServer.Element("security")?.Element("requestFiltering");
        if (ReadBool(requestFiltering, "removeServerHeader") != true ||
            ReadBool(requestFiltering, "allowDoubleEscaping") != false)
        {
            return false;
        }

        var customHeaders = systemWebServer.Element("httpProtocol")?.Element("customHeaders");
        return HasHeader(customHeaders, "X-Frame-Options")
               && HasHeader(customHeaders, "X-Content-Type-Options")
               && HasHeader(customHeaders, "Referrer-Policy");
    }

    private static int MeasureRateLimit(XElement systemWebServer)
    {
        var dynamicIpSecurity = systemWebServer.Element("security")?.Element("dynamicIpSecurity");
        if (dynamicIpSecurity is null || ReadBool(dynamicIpSecurity, "denyByRequestRate") != true)
        {
            // Kural kapalıysa sayının bir anlamı yok: sınır YOK.
            return 0;
        }

        return int.TryParse((string?)dynamicIpSecurity.Attribute("maxRequests"), out var value) && value > 0
            ? value
            : 0;
    }

    private static bool? ReadBool(XElement? element, string attributeName)
    {
        var raw = (string?)element?.Attribute(attributeName);
        return bool.TryParse(raw, out var value) ? value : null;
    }

    private static bool HasHeader(XElement? customHeaders, string name)
        => customHeaders?.Elements("add").Any(element =>
               string.Equals((string?)element.Attribute("name"), name, StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace((string?)element.Attribute("value"))) == true;

    public async Task<SiteSecurityConfigurationDto> IsolateAppPoolAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var targetPool = ManagedPoolPrefix + site.Name;

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
        var webConfigPath = WebConfigPath(site.PhysicalPath);
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
        var webConfigPath = WebConfigPath(site.PhysicalPath);
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
