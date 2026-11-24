using System.Security.Claims;
using System.Reflection;
using System.Xml.Linq;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.AppRuntimes;
using AdaPanel.Core.Dtos.Docker;
using AdaPanel.Core.Dtos.Git;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.AppRuntimes;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>Dalga 3 / Node-Python barındırma: durdur, başlat, yeniden başlat, kaldır, günlük + site silme akışı.</summary>
public sealed class W3AppRuntimeControlTests : IDisposable
{
    private const string HostedConfig =
        "<configuration><appSettings><add key=\"keep\" value=\"1\" /></appSettings><system.webServer><handlers>" +
        "<add name=\"AdaPanel.HttpPlatform\" path=\"*\" verb=\"*\" modules=\"httpPlatformHandler\" resourceType=\"Unspecified\" />" +
        "</handlers><httpPlatform processPath=\"C:\\runtime\\node.exe\" arguments=\"&quot;server.js&quot;\" workingDirectory=\".\" />" +
        "</system.webServer></configuration>";

    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-w3-app-" + Guid.NewGuid().ToString("N"));
    private readonly string siteName = "w3app-" + Guid.NewGuid().ToString("N") + ".test";
    private readonly List<FakePool> pools = [new() { Name = "w3pool" }];

    private string SitePath => Path.Combine(root, "site");
    private string WebConfig => Path.Combine(SitePath, "web.config");
    private string LogsPath => Path.Combine(SitePath, "App_Data", "AdaPanel", "logs");

    public W3AppRuntimeControlTests() => Directory.CreateDirectory(SitePath);

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private AppRuntimeService Service(string config = HostedConfig)
    {
        File.WriteAllText(WebConfig, config);
        var iis = DispatchProxy.Create<IIisSiteService, PostgresDatabaseIdentityConsumersTests.StrictProxy>();
        ((PostgresDatabaseIdentityConsumersTests.StrictProxy)(object)iis).Handler = (method, _) => method.Name == "GetSiteAsync"
            ? Task.FromResult(new SiteDto { Id = 7, Name = siteName, PhysicalPath = SitePath, ApplicationPoolName = "w3pool" })
            : throw new NotSupportedException(method.Name);
        return new AppRuntimeService(null!, iis, null!, new ConcurrencyLock(), NullLogger<AppRuntimeService>.Instance)
        { ManagerFactory = () => new FakeManager(pools) };
    }

    private static readonly Func<SiteDto, CancellationToken, Task> NoGate = (_, _) => Task.CompletedTask;

    [Fact]
    public async Task StopStartRestartDriveTheSitePoolAndReportTheMeasuredState()
    {
        var service = Service();
        Assert.Equal("Stopped", (await service.StopAppAsync(siteName, NoGate)).PoolState);
        Assert.Equal("Stopped", pools[0].State);
        Assert.Equal("Started", (await service.StartAppAsync(siteName, NoGate)).PoolState);
        Assert.Equal("Started", (await service.RestartAppAsync(siteName, NoGate)).PoolState);
        Assert.Equal(1, pools[0].Recycles);
        pools[0].State = "Stopped";
        Assert.Equal("Started", (await service.RestartAppAsync(siteName, NoGate)).PoolState);
    }

    [Fact]
    public async Task ControlOnASiteWithoutHostedAppIsRejected()
    {
        var service = Service("<configuration />");
        var error = await Assert.ThrowsAsync<AppException>(() => service.StopAppAsync(siteName, NoGate));
        Assert.Equal("appruntime.not_hosted", error.MessageKey);
        Assert.Equal("Started", pools[0].State);
    }

    [Fact]
    public async Task AStopThatDoesNotTakeEffectIsNeverReportedAsSuccess()
    {
        var service = Service();
        pools[0].IgnoreStop = true;
        var error = await Assert.ThrowsAsync<AppException>(() => service.StopAppAsync(siteName, NoGate));
        Assert.Equal("appruntime.control_failed", error.MessageKey);
    }

    [Fact]
    public async Task ControlRunsTheAuthorityCallbackBeforeTouchingThePool()
    {
        var service = Service();
        var error = await Assert.ThrowsAsync<AppException>(() =>
            service.StopAppAsync(siteName, (_, _) => throw new AppException("auth.forbidden", 403, 403)));
        Assert.Equal(403, error.HttpStatusCode);
        Assert.Equal("Started", pools[0].State);
    }

    [Fact]
    public async Task RemoveDeletesOnlyThePanelEntriesAndThePanelLogFiles()
    {
        var service = Service();
        Directory.CreateDirectory(LogsPath);
        File.WriteAllText(Path.Combine(LogsPath, "httpplatform-stdout_1_20260101.log"), "x");
        File.WriteAllText(Path.Combine(LogsPath, "user-notes.txt"), "keep me");
        var result = await service.RemoveAppAsync(siteName, deleteLogs: true, NoGate);

        Assert.Empty(result.Leftovers);
        Assert.Contains("web.config", result.Removed);
        Assert.Contains("process", result.Removed);
        var document = XDocument.Load(WebConfig);
        Assert.Null(document.Descendants("httpPlatform").FirstOrDefault());
        Assert.Empty(document.Descendants("add").Where(e => (string?)e.Attribute("name") == "AdaPanel.HttpPlatform"));
        Assert.Equal("1", (string?)document.Descendants("add").Single(e => (string?)e.Attribute("key") == "keep").Attribute("value"));
        Assert.False(File.Exists(Path.Combine(LogsPath, "httpplatform-stdout_1_20260101.log")));
        Assert.True(File.Exists(Path.Combine(LogsPath, "user-notes.txt")));
        Assert.Equal("none", result.Configuration.RuntimeType);
        Assert.Equal(1, pools[0].Recycles);
    }

    [Fact]
    public async Task RemoveReportsALeftoverWhenTheProcessCannotBeRecycled()
    {
        var service = Service();
        pools[0].FailRecycle = true;
        var result = await service.RemoveAppAsync(siteName, deleteLogs: false, NoGate);
        Assert.Contains(result.Leftovers, line => line.StartsWith("process:", StringComparison.Ordinal));
        Assert.Contains("web.config", result.Removed);
    }

    [Fact]
    public async Task RemoveOnASiteWithoutHostedAppIsRejected()
    {
        var service = Service("<configuration />");
        var error = await Assert.ThrowsAsync<AppException>(() => service.RemoveAppAsync(siteName, true, NoGate));
        Assert.Equal("appruntime.not_hosted", error.MessageKey);
    }

    [Fact]
    public async Task LogsReturnTheTailOfTheNewestPanelLogFile()
    {
        var service = Service();
        Directory.CreateDirectory(LogsPath);
        File.WriteAllLines(Path.Combine(LogsPath, "httpplatform-stdout_9.log"), Enumerable.Range(0, 500).Select(i => "line " + i));
        File.WriteAllText(Path.Combine(LogsPath, "other.log"), "must not be read");
        var logs = await service.GetLogsAsync(siteName, 10);
        Assert.Equal("httpplatform-stdout_9.log", logs.FileName);
        Assert.EndsWith("line 499", logs.Output);
        Assert.DoesNotContain("line 100", logs.Output);
        Assert.True(logs.Truncated);
        Assert.DoesNotContain("must not be read", logs.Output);
    }

    [Fact]
    public async Task LogsOfASiteWithoutLogFolderAreEmptyNotAnError()
    {
        var logs = await Service().GetLogsAsync(siteName, 100);
        Assert.Equal(string.Empty, logs.Output);
        Assert.Equal(string.Empty, logs.FileName);
    }

    [Fact]
    public async Task RemoveEndpointDemandsTheSiteNameBeforeAnythingElseHappens()
    {
        var controller = new AppRuntimeController(null!, null!, new RecordingAuditService());
        var error = await Assert.ThrowsAsync<AppException>(() =>
            controller.RemoveAppAsync(siteName, new RemoveAppRuntimeRequest { ConfirmSiteName = "wrong" }, CancellationToken.None));
        Assert.Equal("appruntime.confirmation_required", error.MessageKey);
        Assert.Equal(400, error.HttpStatusCode);
    }

    // ---- Site silme akışı: dürüst kalıntı raporu ----

    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, PostgresDatabaseIdentityConsumersTests.StrictProxy>();
        ((PostgresDatabaseIdentityConsumersTests.StrictProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    /// <summary>Task&lt;boş koleksiyon&gt; döndürür (hangi koleksiyon tipi olursa olsun).</summary>
    private static object Empty(MethodInfo method)
    {
        var result = method.ReturnType.GetGenericArguments()[0];
        var value = result.IsArray ? Array.CreateInstance(result.GetElementType()!, 0)
            : result.IsGenericType ? Array.CreateInstance(result.GetGenericArguments()[0], 0) : null;
        return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result).Invoke(null, [value])!;
    }

    private static SiteCascadeDeletionService Cascade(string name, IAppRuntimeService? app, IDockerService? docker, IGitDeploymentService? git)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions
        {
            DataRoot = Path.Combine(Path.GetTempPath(), "adapanel-w3-cascade-" + Guid.NewGuid().ToString("N"))
        });
        return new SiteCascadeDeletionService(
            Fake<IIisSiteService>((method, _) => method.Name switch
            {
                "GetSiteAsync" => Task.FromResult(new SiteDto { Name = name }),
                "DeleteSiteAsync" => Task.CompletedTask,
                _ => throw new NotSupportedException(method.Name)
            }), null!,
            Fake<IFtpService>((_, _) => Task.FromResult<IReadOnlyCollection<AdaPanel.Core.Dtos.Ftp.FtpUserDto>>([])),
            Fake<IMailService>((_, _) => Task.FromResult(new AdaPanel.Core.Dtos.Mail.MailWorkspaceDto())),
            Fake<IDatabaseService>((_, _) => throw new NotSupportedException()), Fake<ISiteDatabaseLinkService>((method, _) => Empty(method)),
            Fake<IDatabaseOwnershipService>((method, _) => Empty(method)),
            Fake<IReverseProxyService>((_, _) => Task.FromResult(new AdaPanel.Core.Dtos.Proxy.ReverseProxySiteConfigurationDto())),
            Fake<IDiskQuotaService>((_, _) => Task.FromResult(new AdaPanel.Core.Dtos.Sites.SiteDiskQuotaDto())),
            Fake<ICustomerScopeService>((_, _) => Task.CompletedTask),
            Fake<ISslService>((_, _) => Task.CompletedTask),
            Fake<ISiteHostingSettingsService>((_, _) => Task.CompletedTask),
            Fake<IWafService>((_, _) => Task.CompletedTask),
            Fake<IDirectoryProtectionService>((_, _) => Task.CompletedTask),
            null!, null!, options, new JsonFileStore(options), NullLogger<SiteCascadeDeletionService>.Instance, new ConcurrencyLock(),
            appRuntimeService: app, dockerService: docker, gitDeploymentService: git);
    }

    [Fact]
    public async Task CascadeRemovesAppContainersAndGitStateAndReportsEveryLeftoverHonestly()
    {
        var name = "w3cascade-" + Guid.NewGuid().ToString("N") + ".test";
        var app = Fake<IAppRuntimeService>((method, _) => method.Name switch
        {
            "GetSiteConfigurationAsync" => Task.FromResult(new AppRuntimeSiteConfigurationDto { SiteName = name, RuntimeType = "node", IsEnabled = true }),
            "RemoveAppForSiteDeletionAsync" => Task.FromResult(new AppRuntimeRemovalResultDto { SiteName = name, Leftovers = ["process: still running"] }),
            _ => throw new NotSupportedException(method.Name)
        });
        var docker = Fake<IDockerService>((method, _) => method.Name == "RemoveSiteContainersAsync"
            ? Task.FromResult(new DockerSiteCleanupResult { Installed = true, DaemonRunning = true, Removed = ["web-1"], Failed = ["db-1: in use"] })
            : throw new NotSupportedException(method.Name));
        var git = Fake<IGitDeploymentService>((method, _) => method.Name == "RemoveAsync"
            ? Task.FromResult(new GitRemovalResultDto { SiteName = name, Leftovers = ["panel git directory: could not be fully deleted"] })
            : throw new NotSupportedException(method.Name));

        var result = await Cascade(name, app, docker, git).DeleteAsync(name, deleteFiles: false, deleteDatabases: false);

        Assert.Contains("Docker konteyneri (web-1)", result.Removed);
        Assert.Contains(result.Leftovers, item => item.Name == "docker:db-1");
        Assert.Contains(result.Leftovers, item => item.Name == $"apphost:{name}");
        Assert.Contains(result.Leftovers, item => item.Name == $"git:{name}");
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task CascadeCallsTheDeletionPathNotTheGuardedRemoveWhichWouldRefuseADeletingSite()
    {
        var name = "w3cascade-" + Guid.NewGuid().ToString("N") + ".test";
        var calls = new List<string>();
        var app = Fake<IAppRuntimeService>((method, _) =>
        {
            calls.Add(method.Name);
            return method.Name switch
            {
                "GetSiteConfigurationAsync" => Task.FromResult(new AppRuntimeSiteConfigurationDto { SiteName = name, RuntimeType = "python", IsEnabled = true }),
                "RemoveAppForSiteDeletionAsync" => Task.FromResult(new AppRuntimeRemovalResultDto { SiteName = name }),
                _ => throw new NotSupportedException(method.Name)
            };
        });
        var result = await Cascade(name, app, null, null).DeleteAsync(name, deleteFiles: false, deleteDatabases: false);
        Assert.Contains("RemoveAppForSiteDeletionAsync", calls);
        Assert.DoesNotContain("RemoveAppAsync", calls);
        Assert.Contains("Node/Python uygulaması", result.Removed);
    }

    [Fact]
    public async Task CascadeDoesNotClaimDockerIsCleanWhenTheEngineIsInstalledButDown()
    {
        var name = "w3cascade-" + Guid.NewGuid().ToString("N") + ".test";
        var docker = Fake<IDockerService>((method, _) => method.Name == "RemoveSiteContainersAsync"
            ? Task.FromResult(new DockerSiteCleanupResult { Installed = true, DaemonRunning = false })
            : throw new NotSupportedException(method.Name));
        var result = await Cascade(name, null, docker, null).DeleteAsync(name, deleteFiles: false, deleteDatabases: false);
        Assert.Contains(result.Leftovers, item => item.Name == $"docker:{name}");
    }

    public sealed class FakeManager(List<FakePool> pools) : IDisposable
    {
        public List<FakePool> ApplicationPools { get; } = pools;
        public void Dispose() { }
    }

    public sealed class FakePool
    {
        public string Name { get; set; } = string.Empty;
        public string State { get; set; } = "Started";
        public string ManagedRuntimeVersion { get; set; } = string.Empty;
        public bool IgnoreStop { get; set; }
        public bool FailRecycle { get; set; }
        public int Recycles { get; set; }
        public void Start() => State = "Started";
        public void Stop() { if (!IgnoreStop) State = "Stopped"; }
        public void Recycle() { if (FailRecycle) throw new IOException("fixture recycle failed"); Recycles++; }
    }
}
