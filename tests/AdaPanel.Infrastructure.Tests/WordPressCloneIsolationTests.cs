using System.Reflection;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.AppStore;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class WordPressCloneIsolationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-wp-isolation-" + Guid.NewGuid().ToString("N"));
    private const string Config = "<?php define('DB_NAME','source_db'); define('DB_USER','source_user'); define('DB_PASSWORD','source_password'); define('DB_HOST','127.0.0.1');";
    private readonly List<string> events = [];
    private bool autoStart;
    private bool running;
    private int rewrites;
    private string targetRoot = "";

    [Theory]
    [InlineData("missing")]
    [InlineData("changed")]
    [InlineData("source-transfer")]
    [InlineData("empty-password")]
    [InlineData("wrong-identity")]
    [InlineData("empty-identity")]
    [InlineData("mysql-missing")]
    public async Task Unverified_database_never_rewrites_or_starts(string failure)
    {
        var service = Create(failure);
        var result = await service.CloneAsync("source", Request());
        Assert.False(result.DatabaseIsolated);
        Assert.Equal(failure is "wrong-identity" or "empty-identity" or "mysql-missing", result.ConfigRewritten);
        Assert.False(result.UrlRewritten);
        Assert.False(result.SiteStarted);
        Assert.Equal(0, rewrites);
        Assert.False(running);
        Assert.False(autoStart);
    }

    [Fact]
    public async Task Success_rewrites_only_isolated_config_then_starts_and_persists_autostart()
    {
        var result = await Create().CloneAsync("source", Request());
        Assert.True(result.DatabaseIsolated);
        Assert.True(result.ConfigRewritten);
        Assert.True(result.UrlRewritten);
        Assert.True(result.SiteStarted);
        Assert.True(running);
        Assert.True(autoStart);
        Assert.Equal(new[] { "create-stopped", "copy", "transfer", "identity", "rewrite", "start", "auto-True" }, events);
        Assert.Contains("source_db", await File.ReadAllTextAsync(Path.Combine(root, "source", "wp-config.php")));
    }

    [Fact]
    public async Task Url_failure_keeps_target_stopped_even_after_reboot()
    {
        var result = await Create("url").CloneAsync("source", Request());
        Assert.True(result.DatabaseIsolated);
        Assert.False(result.UrlRewritten);
        Assert.False(result.SiteStarted);
        Assert.False(running);
        Assert.False(autoStart);
        Assert.DoesNotContain("start", events);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("persist")]
    public async Task Startup_or_persistence_failure_rolls_back_both_states(string failure)
    {
        var result = await Create(failure).CloneAsync("source", Request());
        Assert.True(result.DatabaseIsolated);
        Assert.True(result.UrlRewritten);
        Assert.False(result.SiteStarted);
        Assert.False(running);
        Assert.False(autoStart);
        Assert.Contains("auto-False", events);
        Assert.Contains("stop", events);
    }

    [Fact]
    public async Task Failed_rollback_is_an_error_not_a_stopped_claim()
    {
        await Assert.ThrowsAsync<AggregateException>(() => Create("rollback").CloneAsync("source", Request()));
        Assert.Contains("stop", events);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("nonsense", false)]
    [InlineData("6.4", true)]
    public void Unknown_or_invalid_installed_version_is_not_measured(string? installed, bool measured)
    {
        var service = Create();
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(source, "wp-includes"));
        if (installed is not null) File.WriteAllText(Path.Combine(source, "wp-includes", "version.php"), "$wp_version = '" + installed + "';");
        var method = typeof(WordPressToolkitService).GetMethod("BuildDto", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = (WordPressSiteDto)method.Invoke(service, ["source", source, "source.test", "6.9"])!;
        Assert.Equal(measured, result.VersionMeasured);
        Assert.Equal(measured, result.VersionComparisonMeasured);
        Assert.Equal(measured, result.UpdateAvailable);
    }

    private WordPressToolkitService Create(string failure = "")
    {
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "wp-config.php"), Config);
        var sites = Stub<IIisSiteService>((method, args) => method switch
        {
            "GetSiteAsync" => Task.FromResult(new SiteDto { Name = "source", PhysicalPath = source, Bindings = [new SiteBindingDto { HostName = "source.test", Protocol = "http", Port = 80 }] }),
            "GetSitesAsync" => Task.FromResult<IReadOnlyCollection<SiteDto>>([]),
            "CreateSiteAsync" => CreateSite((CreateSiteRequest)args[0]!),
            "StartSiteAsync" => Start(),
            "SetSiteAutoStartAsync" => SetAuto((bool)args[1]!),
            "StopSiteAsync" => Stop(),
            _ => throw new NotSupportedException(method)
        });
        var database = Stub<IDatabaseService>((method, args) => method switch
        {
            "GetEnginesAsync" => Task.FromResult<IReadOnlyCollection<DatabaseEngineStatusDto>>([new() { Engine = "mariadb", Installed = true }]),
            "TransferDatabaseAsync" => Transfer((DatabaseTransferRequest)args[0]!),
            _ => throw new NotSupportedException(method)
        });
        var hosting = Stub<ISiteHostingSettingsService>((method, args) =>
        {
            Assert.Equal("RunWpCliCommandAsync", method);
            var command = (string)args[1]!;
            if (command.StartsWith("db query", StringComparison.Ordinal))
            {
                Assert.Equal("db query SELECT(DATABASE()) --skip-column-names", command);
                var config = File.ReadAllText(Path.Combine(targetRoot, "wp-config.php"));
                Assert.Contains("'127.0.0.1'", config);
                Assert.DoesNotContain("source_db", config);
                Assert.False(running);
                Assert.False(autoStart);
                events.Add("identity");
                return Task.FromResult(new ComposerRunResultDto { ExitCode = failure == "mysql-missing" ? 1 : 0,
                    Output = failure == "wrong-identity" ? "source_db" : failure == "empty-identity" ? "" : "target_db\r\n" });
            }
            Assert.Contains("search-replace", command);
            var persisted = File.ReadAllText(Path.Combine(targetRoot, "wp-config.php"));
            Assert.DoesNotContain("source_db", persisted);
            Assert.Contains("target_db", persisted);
            Assert.False(running);
            Assert.False(autoStart);
            rewrites++;
            events.Add("rewrite");
            return Task.FromResult(new ComposerRunResultDto { ExitCode = failure == "url" ? 1 : 0 });
        });
        return new WordPressToolkitService(sites, database, hosting)
        {
            CloneFileCopy = (_, target, _, _) =>
            {
                events.Add("copy");
                if (failure != "missing") File.WriteAllText(Path.Combine(target, "wp-config.php"), failure == "changed" ? "<?php" : Config);
                return Task.FromResult(new CommandResult { ExitCode = 1 });
            }
        };

        Task<SiteDto> CreateSite(CreateSiteRequest request)
        {
            Assert.False(request.AutoStart);
            targetRoot = request.PhysicalPath;
            autoStart = request.AutoStart;
            events.Add("create-stopped");
            return Task.FromResult(new SiteDto { Name = request.Name, State = "Stopped" });
        }
        Task<DatabaseTransferResultDto> Transfer(DatabaseTransferRequest request)
        {
            Assert.False(autoStart);
            Assert.False(running);
            events.Add("transfer");
            return Task.FromResult(new DatabaseTransferResultDto { TargetDatabase = failure == "source-transfer" ? "source_db" : request.TargetDatabase,
                UserName = "target_user", Password = failure == "empty-password" ? "" : "target'\\password" });
        }
        Task<SiteDto> Start()
        {
            events.Add("start");
            running = true;
            if (failure == "start") throw new IOException("start failed after transition");
            return Task.FromResult(new SiteDto { State = "Started" });
        }
        Task<SiteDto> SetAuto(bool value)
        {
            events.Add("auto-" + value);
            autoStart = value;
            if (value && failure is "persist" or "rollback") throw new IOException("commit result unknown");
            if (!value && failure == "rollback") throw new IOException("rollback unavailable");
            return Task.FromResult(new SiteDto { AutoStart = value });
        }
        Task<SiteDto> Stop()
        {
            events.Add("stop");
            running = false;
            return Task.FromResult(new SiteDto { State = "Stopped" });
        }
    }

    private static CloneWordPressRequest Request() => new() { TargetSiteName = "target_db", TargetHostName = "target.test" };
    private static T Stub<T>(Func<string, object?[], object?> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T, ServiceProxy>();
        ((ServiceProxy)(object)proxy).Handler = invoke;
        return proxy;
    }
    public class ServiceProxy : DispatchProxy
    {
        public Func<string, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!.Name, args!);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
