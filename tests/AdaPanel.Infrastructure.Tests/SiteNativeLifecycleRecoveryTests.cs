using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

// The real public native methods run their private write/commit/fresh-read/firewall paths.
// Managers publish only on CommitChanges; every OS command and IIS availability probe is fake.
public sealed class SiteNativeLifecycleRecoveryTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("use")]
    [InlineData("deleting")]
    [InlineData("provisioning")]
    public async Task RootChangeCannotBypassAnExistingSiteLifecycleReservation(string reservation)
    {
        using var f = new Fixture();
        await f.Create();
        using var target = await SiteMutationGuard.CaptureAsync(f.Native, f.Gates, f.Name, default);
        IDisposable guard = reservation switch
        {
            "use" => await target.ReserveAsync(default),
            "deleting" => await SiteMutationGuard.BeginDeletionAsync(f.Gates, f.Name, default),
            _ => await SiteMutationGuard.WatchCreationAsync(f.Gates, f.Name, default, provisioning: true)
        };
        var commits = f.State.Commits;
        Exception? error;
        try { error = await Capture(() => f.Native.UpdatePhysicalPathAsync(f.Name, f.OtherPath)).WaitAsync(Deadline); }
        finally { guard.Dispose(); }
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(error).MessageKey);
        Assert.Equal(commits, f.State.Commits);
        Assert.Equal(f.SitePath, f.State.Current!.PhysicalPath);
        Assert.False(Directory.Exists(f.OtherPath));
        Assert.Equal(f.OtherPath, (await f.Native.UpdatePhysicalPathAsync(f.Name, f.OtherPath)).PhysicalPath);
    }

    [Fact]
    public async Task RootChangeAwayAndBackInvalidatesPreviouslyCapturedTarget()
    {
        using var f = new Fixture();
        await f.Create();
        using var old = await SiteMutationGuard.CaptureAsync(f.Native, f.Gates, f.Name, default);
        await f.Native.UpdatePhysicalPathAsync(f.Name, f.OtherPath);
        await f.Native.UpdatePhysicalPathAsync(f.Name, f.SitePath);
        var entered = false;
        var error = await Capture(() => old.RunAsync(_ => { entered = true; return Task.CompletedTask; }, default));
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(error).MessageKey);
        Assert.False(entered);
        using var fresh = await SiteMutationGuard.CaptureAsync(f.Native, f.Gates, f.Name, default);
        await fresh.RunAsync(_ => Task.CompletedTask, default);
    }

    [Fact]
    public async Task FailedRootCommitAlsoInvalidatesOldEvidenceWithoutPublishingItsPath()
    {
        using var f = new Fixture();
        await f.Create();
        using var old = await SiteMutationGuard.CaptureAsync(f.Native, f.Gates, f.Name, default);
        f.State.FailCommit = f.State.Commits + 1;
        Assert.IsType<AppException>(await Capture(() => f.Native.UpdatePhysicalPathAsync(f.Name, f.OtherPath)));
        Assert.Equal(f.SitePath, f.State.Current!.PhysicalPath);
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(
            await Capture(() => old.RunAsync(_ => Task.CompletedTask, default))).MessageKey);
    }

    [Fact]
    public async Task UnreservedRootChangeCommitsAndReturnsTheNewPath()
    {
        using var f = new Fixture();
        var original = await f.Create();
        var changed = await f.Native.UpdatePhysicalPathAsync(f.Name, f.OtherPath);
        Assert.Equal(original.Id, changed.Id);
        Assert.Equal(f.OtherPath, changed.PhysicalPath);
        Assert.Equal(f.OtherPath, (await f.Native.GetSiteAsync(f.Name)).PhysicalPath);
        Assert.True(Directory.Exists(f.OtherPath));
    }

    [Theory]
    [InlineData("delete", false)]
    [InlineData("add", false)]
    [InlineData("delete", true)]
    [InlineData("add", true)]
    public async Task FirewallExceptionAfterCommitStillRollsBackOnlyTheCreatedGeneration(string stage, bool canceled)
    {
        using var f = new Fixture();
        using var cancellation = new CancellationTokenSource(Deadline);
        Exception expected = canceled ? new OperationCanceledException(cancellation.Token) : new IOException("firewall launch failed");
        var failed = false;
        f.Command = (_, arguments, _) =>
        {
            if (!failed && arguments.Contains("firewall " + stage + " rule", StringComparison.Ordinal))
            {
                failed = true;
                Assert.NotNull(f.State.Current);
                Assert.True(f.State.Current!.Id > 0);
                if (canceled) cancellation.Cancel();
                throw expected;
            }
            return Task.FromResult(new CommandResult());
        };
        var error = await Capture(() => f.Provision(cancellation.Token)).WaitAsync(Deadline);
        Assert.Same(expected, error);
        Assert.True(failed);
        Assert.Equal(0, f.DefaultsCalls);
        Assert.Null(f.State.Current);
        Assert.False(Directory.Exists(f.SitePath));
        Assert.Equal(2, f.State.Commits);
        Assert.All(f.CommandTokens.SkipWhile(token => token != CancellationToken.None), token => Assert.False(token.IsCancellationRequested));
        // Neither cancellation nor recovery keeps the name reserved forever.
        f.Command = (_, _, _) => Task.FromResult(new CommandResult());
        Assert.True((await f.Create()).Id > 0);
    }

    [Fact]
    public async Task FirewallRecoveryPreservesAPreexistingDirectory()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.SitePath);
        var existing = Path.Combine(f.SitePath, "existing.txt");
        await File.WriteAllTextAsync(existing, "keep");
        var expected = new IOException("firewall failed");
        f.FailFirstCommand(expected);
        Assert.Same(expected, await Capture(() => f.Provision(default)));
        Assert.Null(f.State.Current);
        Assert.Equal("keep", await File.ReadAllTextAsync(existing));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("path")]
    [InlineData("same-id-native")]
    public async Task FirewallRecoveryCannotDeleteAReplacementGeneration(string replacement)
    {
        using var f = new Fixture();
        var expected = new IOException("firewall failed after replacement");
        var failed = false;
        long replacementId = 0;
        f.Command = async (_, _, _) =>
        {
            if (!failed)
            {
                failed = true;
                var originalId = f.State.Current!.Id;
                if (replacement == "same-id-native")
                {
                    await f.Create();
                    f.State.Current!.Id = originalId;
                }
                else if (replacement == "id") f.State.Current!.Id += 100;
                else
                {
                    Directory.CreateDirectory(f.OtherPath);
                    f.State.Current!.PhysicalPath = f.OtherPath;
                }
                replacementId = f.State.Current!.Id;
                await File.WriteAllTextAsync(Path.Combine(f.SitePath, "replacement.txt"), "replacement data");
                throw expected;
            }
            return new CommandResult();
        };
        Assert.Same(expected, await Capture(() => f.Provision(default)).WaitAsync(Deadline));
        Assert.NotNull(f.State.Current);
        Assert.Equal(replacementId, f.State.Current!.Id);
        Assert.Equal("replacement data", await File.ReadAllTextAsync(Path.Combine(f.SitePath, "replacement.txt")));
        Assert.Equal(replacement == "same-id-native" ? 2 : 1, f.State.Commits);
    }

    [Fact]
    public async Task FailedRollbackCommitPreservesTheSiteAndDirectoryAndOriginalFirewallError()
    {
        using var f = new Fixture();
        f.State.FailCommit = 2;
        var expected = new IOException("firewall failed");
        f.FailFirstCommand(expected);
        Assert.Same(expected, await Capture(() => f.Provision(default)));
        Assert.Equal(2, f.State.Commits);
        Assert.NotNull(f.State.Current);
        Assert.True(Directory.Exists(f.SitePath));
    }

    [Fact]
    public async Task FailedCreateCommitCannotClaimAPreexistingSiteForRollback()
    {
        using var f = new Fixture();
        var original = await f.Create();
        await File.WriteAllTextAsync(Path.Combine(f.SitePath, "original.txt"), "original");
        f.State.FailCommit = f.State.Commits + 1;
        var commandCount = f.CommandTokens.Count;
        Assert.IsType<AppException>(await Capture(() => f.Provision(default)));
        Assert.Equal(original.Id, f.State.Current!.Id);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(f.SitePath, "original.txt")));
        Assert.Equal(commandCount, f.CommandTokens.Count);
    }

    [Fact]
    public async Task ActualNativeSuccessReachesNormalProvisioningRollback()
    {
        using var f = new Fixture();
        var error = await Capture(() => f.Provision(default));
        Assert.Same(f.DefaultsFailure, error);
        Assert.Equal(1, f.DefaultsCalls);
        Assert.Null(f.State.Current);
        Assert.False(Directory.Exists(f.SitePath));
        Assert.Equal(2, f.State.Commits);
        Assert.Equal(3, f.CommandTokens.Count); // Create: delete+add; rollback: delete.
    }

    [Fact]
    public async Task PublicNativeCreateReturnsTheCommittedIdentityAndDoesNotHideFirewallExceptions()
    {
        using var f = new Fixture();
        var created = await f.Create();
        Assert.True(created.Id > 0);
        Assert.Equal(created.Id, f.State.Current!.Id);
        Assert.Equal(2, f.CommandTokens.Count);
        var expected = new IOException("firewall unavailable");
        f.FailFirstCommand(expected);
        Assert.Same(expected, await Capture(() => f.Create()));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("path")]
    [InlineData("pool")]
    [InlineData("read-failed")]
    public async Task UnmeasuredCommittedIdentityIsNeverClaimedForRollback(string missing)
    {
        using var f = new Fixture();
        f.InspectManager = manager =>
        {
            if (f.State.Commits == 0) return;
            if (missing == "read-failed") throw new IOException("fresh manager unavailable");
            var site = manager.Sites.Single();
            if (missing == "id") site.Id = 0;
            else if (missing == "path") site.PhysicalPath = "";
            else site.Applications[0].ApplicationPoolName = "";
        };
        Assert.IsType<AppException>(await Capture(() => f.Provision(default)));
        Assert.NotNull(f.State.Current);
        Assert.Equal(1, f.State.Commits);
        Assert.Equal(0, f.DefaultsCalls);
        Assert.Empty(f.CommandTokens);
        Assert.True(Directory.Exists(f.SitePath));
        f.InspectManager = null;
        Assert.True((await f.Native.GetSiteAsync(f.Name)).Id > 0);
        await f.Native.DeleteSiteAsync(f.Name); // Failed observation also releases the creation intent.
    }

    [Fact]
    public async Task AnotherNativeInvocationCannotPublishACommitToTheFailingCreation()
    {
        using var f = new Fixture();
        var expected = new IOException("original invocation failed before its write");
        var intercepted = false;
        f.EnsureManagement = async _ =>
        {
            if (intercepted) return;
            intercepted = true;
            await f.Create(); // A different call creates the same name while the original awaits preparation.
            await File.WriteAllTextAsync(Path.Combine(f.SitePath, "foreign.txt"), "foreign generation");
            throw expected;
        };
        Assert.Same(expected, await Capture(() => f.Provision(default)).WaitAsync(Deadline));
        Assert.Equal(1, f.State.Commits);
        Assert.NotNull(f.State.Current);
        Assert.Equal("foreign generation", await File.ReadAllTextAsync(Path.Combine(f.SitePath, "foreign.txt")));
        Assert.Equal(0, f.DefaultsCalls);
        using var target = await SiteMutationGuard.CaptureAsync(f.Native, f.Gates, f.Name, default);
        await target.RunAsync(_ => Task.CompletedTask, default);
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    { try { await action(); return null; } catch (Exception error) { return error; } }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanel.NativeRecovery." + Guid.NewGuid().ToString("N"));
        public string Name { get; } = "native-" + Guid.NewGuid().ToString("N") + ".test";
        public string SitePath => Path.Combine(root, "site");
        public string OtherPath => Path.Combine(root, "other");
        public IConcurrencyLock Gates { get; } = new ConcurrencyLock();
        public MemoryState State { get; } = new();
        public WebAdministrationSiteService Native { get; }
        public Action<MemoryManager>? InspectManager;
        public Func<CancellationToken, Task>? EnsureManagement;
        public int DefaultsCalls;
        public Exception DefaultsFailure { get; } = new InvalidOperationException("defaults intentionally failed before ACL commands");
        public List<CancellationToken> CommandTokens { get; } = [];
        public Func<string, string, CancellationToken, Task<CommandResult>> Command = (_, _, _) => Task.FromResult(new CommandResult());
        public Fixture()
        {
            Native = new WebAdministrationSiteService(Gates, NullLogger<WebAdministrationSiteService>.Instance,
                () => { var manager = new MemoryManager(State); InspectManager?.Invoke(manager); return manager; },
                token => { token.ThrowIfCancellationRequested(); return EnsureManagement?.Invoke(token) ?? Task.CompletedTask; },
                (file, args, token) => { CommandTokens.Add(token); return Command(file, args, token); });
        }
        public Task<SiteDto> Create() => Native.CreateSiteAsync(Request());
        private CreateSiteRequest Request() => new()
        { Name = Name, HostName = Name, PhysicalPath = SitePath, RuntimeProfile = "static", Port = 8080 };
        public void FailFirstCommand(Exception failure)
        {
            var failed = false;
            Command = (_, _, _) => { if (!failed) { failed = true; throw failure; } return Task.FromResult(new CommandResult()); };
        }
        public async Task<SiteDto> Provision(CancellationToken token)
        {
            using var services = new ServiceCollection().AddSingleton<IIisSiteService>(Native).AddSingleton(Gates)
                .AddSingleton<IComponentBootstrapper>(Fake<IComponentBootstrapper>())
                .AddSingleton<IPhpService>(Fake<IPhpService>()).AddSingleton<IAspNetService>(Fake<IAspNetService>())
                .AddSingleton<IAppRuntimeService>(Fake<IAppRuntimeService>()).AddSingleton<IWafService>(Fake<IWafService>())
                .AddSingleton<ISiteDefaultsService>(Fake<ISiteDefaultsService>((_, _) => { DefaultsCalls++; throw DefaultsFailure; }))
                .AddSingleton<Microsoft.Extensions.Logging.ILogger<SiteProvisioningService>>(NullLogger<SiteProvisioningService>.Instance)
                .BuildServiceProvider();
            return await ActivatorUtilities.CreateInstance<SiteProvisioningService>(services)
                .CreateAndProvisionAsync(Request(), null, null, token);
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    public sealed class MemoryState
    {
        public MemorySite? Current;
        public PoolCollection Pools = [];
        public int Commits;
        public int FailCommit = -1;
        public long NextId = 40;
    }
    public sealed class MemoryManager : IDisposable
    {
        private readonly MemoryState state;
        public SiteCollection Sites { get; } = [];
        public PoolCollection ApplicationPools { get; } = [];
        public MemoryManager(MemoryState state)
        {
            this.state = state;
            if (state.Current is not null) Sites.Add(state.Current.Clone());
            ApplicationPools.AddRange(state.Pools.Select(pool => pool.Clone()));
        }
        public void CommitChanges()
        {
            state.Commits++;
            if (state.Commits == state.FailCommit) throw new IOException("commit failed");
            foreach (var site in Sites) if (site.Id == 0) site.Id = ++state.NextId;
            state.Current = Sites.SingleOrDefault()?.Clone();
            state.Pools = new PoolCollection();
            state.Pools.AddRange(ApplicationPools.Select(pool => pool.Clone()));
        }
        public void Dispose() { }
    }
    public sealed class SiteCollection : List<MemorySite>
    {
        public MemorySite Add(string name, string protocol, string binding, string path)
        {
            var site = new MemorySite { Name = name, PhysicalPath = path,
                Bindings = [new Binding { Protocol = protocol, BindingInformation = binding }] };
            Add(site);
            return site;
        }
    }
    public sealed class MemorySite
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string State => "Started";
        public bool ServerAutoStart { get; set; } = true;
        public Binding[] Bindings { get; set; } = [];
        public Application[] Applications { get; } = [new()];
        public string PhysicalPath { get => Applications[0].VirtualDirectories[0].PhysicalPath; set => Applications[0].VirtualDirectories[0].PhysicalPath = value; }
        public MemorySite Clone()
        {
            var clone = new MemorySite { Id = Id, Name = Name, ServerAutoStart = ServerAutoStart, PhysicalPath = PhysicalPath,
                Bindings = Bindings.Select(binding => new Binding { Protocol = binding.Protocol, BindingInformation = binding.BindingInformation }).ToArray() };
            clone.Applications[0].ApplicationPoolName = Applications[0].ApplicationPoolName;
            return clone;
        }
    }
    public sealed class Binding
    { public string Protocol { get; set; } = "http"; public string BindingInformation { get; set; } = ""; }
    public sealed class Application
    { public string Path => "/"; public string ApplicationPoolName { get; set; } = ""; public VirtualDirectory[] VirtualDirectories { get; } = [new()]; }
    public sealed class VirtualDirectory
    { public string Path => "/"; public string PhysicalPath { get; set; } = ""; }
    public sealed class PoolCollection : List<Pool>
    { public Pool Add(string name) { var pool = new Pool { Name = name }; Add(pool); return pool; } }
    public sealed class Pool
    {
        public string Name { get; set; } = "";
        public bool AutoStart { get; set; }
        public string ManagedRuntimeVersion { get; set; } = "";
        public Pool Clone() => new() { Name = Name, AutoStart = AutoStart, ManagedRuntimeVersion = ManagedRuntimeVersion };
    }
    private static T Fake<T>(Func<MethodInfo, object?[], object?>? invoke = null) where T : class
    {
        var value = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)value).InvokeMethod = invoke ?? ((method, _) => throw new InvalidOperationException("Unexpected integration: " + method.Name));
        return value;
    }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> InvokeMethod { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => InvokeMethod(targetMethod!, args ?? []);
    }
}
