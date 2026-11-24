using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Ftp;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>Real service and JSON lifecycle; links/native accounts are instance-local memory models.</summary>
public sealed class FtpHomeRepairLifecycleTests
{
    [Fact]
    public async Task DeletionAfterTheRepairSnapshotCannotResurrectItsLinks()
    {
        await using var f = await Fixture.Create();
        f.PauseSnapshot = true;
        var repair = f.Service.HealHomeLinksAsync(f.Token);
        await f.At(f.SnapshotEntered.Task, repair);
        try { await f.Service.DeleteFtpUserAsync(f.Name, f.Token); }
        finally { f.ReleaseSnapshot(); }
        Assert.Equal(0, await repair);
        Assert.Empty(await f.Records());
        Assert.False(f.AccountExists);
        Assert.All(f.Paths(), path => Assert.False(Directory.Exists(path)));
        Assert.Empty(f.RepairedTargets);
        Assert.Equal(0, FtpAccountLifecycle.References(f.Name));
    }

    [Fact]
    public async Task ReusingADeletedNameForAnotherSiteCannotReceiveTheOldRepairTarget()
    {
        await using var f = await Fixture.Create();
        f.PauseSnapshot = true;
        var repair = f.Service.HealHomeLinksAsync(f.Token);
        await f.At(f.SnapshotEntered.Task, repair);
        Task<FtpUserDto>? create = null;
        try
        {
            await f.Service.DeleteFtpUserAsync(f.Name, f.Token);
            f.PauseCreation = true;
            create = f.CreateAccount(f.SiteB.Name);
            await f.At(f.CreationEntered.Task, create);
            f.ReleaseSnapshot();
            Assert.Equal(0, await repair);
            Assert.Empty(f.RepairedTargets);
        }
        finally
        {
            f.ReleaseSnapshot(); f.ReleaseCreation();
            await Record.ExceptionAsync(() => repair);
            if (create is not null) await Record.ExceptionAsync(() => create);
        }
        var user = await create!;
        Assert.Equal(f.SiteB.Name, user.SiteName);
        Assert.Equal(f.SiteB.PhysicalPath, Assert.Single(await f.Records()).HomePath);
        Assert.All(f.Paths(), path => Assert.Equal(f.SiteB.PhysicalPath, f.Targets[path]));
        Assert.Equal(0, FtpAccountLifecycle.References(f.Name));
    }

    [Theory]
    [InlineData("site")]
    [InlineData("generation")]
    public async Task AChangedDurableAccountCannotBeRepairedFromItsOldSnapshot(string change)
    {
        await using var f = await Fixture.Create();
        f.PauseSnapshot = true;
        var repair = f.Service.HealHomeLinksAsync(f.Token);
        await f.At(f.SnapshotEntered.Task, repair);
        try
        {
            var current = Assert.Single(await f.Records());
            if (change == "site") { current.SiteName = f.SiteB.Name; current.HomePath = f.SiteB.PhysicalPath; }
            else current.CreatedAtUtc = current.CreatedAtUtc.AddHours(1);
            await f.Save(current);
        }
        finally { f.ReleaseSnapshot(); }
        Assert.Equal(0, await repair);
        Assert.Empty(f.RepairedTargets);
        Assert.False(Directory.Exists(f.Paths()[1]));
    }

    [Fact]
    public async Task ARepairSkipsABusyAccountAndStillHealsTheNextAccount()
    {
        await using var f = await Fixture.Create();
        var next = f.Name + "b";
        await f.AddAccount(next);
        using var active = await FtpAccountLifecycle.EnterAsync(f.Name.ToUpperInvariant(), f.Token);
        Assert.Equal(1, await f.Service.HealHomeLinksAsync(f.Token).WaitAsync(f.Token));
        Assert.False(Directory.Exists(f.Paths()[1]));
        Assert.True(Directory.Exists(f.Paths(next)[1]));
        Assert.Equal(1, FtpAccountLifecycle.References(f.Name));
        Assert.Equal(0, FtpAccountLifecycle.References(next));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("target")]
    public async Task FinalApprovalCannotPublishAnAccountOrTargetSnapshotThatChangedDuringItsWait(string change)
    {
        await using var f = await Fixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repair = f.Service.HealHomeLinksAsync(f.Token, async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        await f.At(entered.Task, repair);
        try
        {
            Assert.Equal(1, FtpAccountLifecycle.References(f.Name));
            if (change == "account")
            {
                var row = Assert.Single(await f.Records());
                row.SiteName = f.SiteB.Name; row.HomePath = f.SiteB.PhysicalPath;
                await f.Save(row);
            }
            else f.Targets[f.Paths()[0]] = f.SiteB.PhysicalPath;
        }
        finally { release.TrySetResult(); }
        Assert.Equal(0, await repair);
        Assert.Empty(f.RepairedTargets);
        Assert.False(Directory.Exists(f.Paths()[1]));
        Assert.Equal(0, FtpAccountLifecycle.References(f.Name));
    }

    [Fact]
    public async Task NativeRepairHoldsTheAccountUntilDeleteCanRemoveAllOfItsLinks()
    {
        await using var f = await Fixture.Create();
        f.PauseRepair = true;
        var repair = f.Service.HealHomeLinksAsync(f.Token);
        await f.At(f.RepairEntered.Task, repair);
        Task? deletion = null;
        try
        {
            Assert.Equal(1, FtpAccountLifecycle.References(f.Name));
            deletion = f.Service.DeleteFtpUserAsync(f.Name, f.Token);
            await f.Until(() => FtpAccountLifecycle.References(f.Name) >= 2 || deletion.IsCompleted);
            Assert.False(deletion.IsCompleted);
        }
        finally
        {
            f.ReleaseRepair();
            await Record.ExceptionAsync(() => repair);
            if (deletion is not null) await Record.ExceptionAsync(() => deletion);
        }
        Assert.Equal(1, await repair);
        await deletion!;
        Assert.All(f.Paths(), path => Assert.False(Directory.Exists(path)));
        Assert.Empty(await f.Records());
        Assert.Equal(0, FtpAccountLifecycle.References(f.Name));
    }

    [Theory]
    [InlineData("before")]
    [InlineData("snapshot")]
    [InlineData("native")]
    public async Task CancellationIsObservedAndReleasesAnyRepairLease(string stage)
    {
        await using var f = await Fixture.Create();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        f.PauseSnapshot = stage == "snapshot"; f.PauseRepair = stage == "native";
        if (stage == "before") caller.Cancel();
        var repair = f.Service.HealHomeLinksAsync(caller.Token);
        if (stage != "before")
        {
            await f.At(stage == "snapshot" ? f.SnapshotEntered.Task : f.RepairEntered.Task, repair);
            caller.Cancel(); f.ReleaseSnapshot(); f.ReleaseRepair();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repair);
        if (stage != "native") Assert.Empty(f.RepairedTargets);
        Assert.Equal(0, FtpAccountLifecycle.References(f.Name));
        // Cancellation does not poison the per-account gate for the next legitimate request.
        using var next = await FtpAccountLifecycle.EnterAsync(f.Name, f.Token);
    }

    [Fact]
    public async Task ExistingSinglePathAccountsStillReceiveTheMissingLink()
    {
        await using var f = await Fixture.Create();
        Assert.Equal(1, await f.Service.HealHomeLinksAsync(f.Token));
        Assert.Equal(f.SiteA.PhysicalPath, Assert.Single(f.RepairedTargets));
        Assert.All(f.Paths(), path => Assert.Equal(f.SiteA.PhysicalPath, f.Targets[path]));
        Assert.Equal(0, FtpAccountLifecycle.References(f.Name));
    }

    [Fact]
    public async Task RecreatingAnExistingAccountDoesNotDeadlockInsideInfrastructureHealing()
    {
        await using var f = await Fixture.Create();
        var user = await f.CreateAccount(f.SiteA.Name).WaitAsync(f.Token);
        Assert.Equal(f.Name, user.UserName);
        Assert.All(f.Paths(), path => Assert.Equal(f.SiteA.PhysicalPath, f.Targets[path]));
        Assert.Equal(0, FtpAccountLifecycle.References(f.Name));
    }

    [Theory]
    [InlineData(false, "1|1|AABBCC")]
    [InlineData(true, "1|1|yok")]
    public async Task AMeasuredBrokenInfrastructureCannotCreateALocalAccountOrPublishAnAccount(bool required, string measurement)
    {
        await using var f = await Fixture.Create();
        f.AccountExists = false;
        await f.ConfigureInfrastructure(required, measurement);
        var before = await f.Records();
        var error = await Assert.ThrowsAsync<AppException>(() => f.CreateAccount(f.SiteA.Name));
        Assert.Equal("ftp.site_configuration_failed", error.MessageKey);
        Assert.False(f.AccountExists);
        Assert.Equal(before.Single().CreatedAtUtc, (await f.Records()).Single().CreatedAtUtc);
        Assert.Equal(0, FtpAccountLifecycle.References(f.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeFailureOrAuthorityRejectionDoesNotLeakTheAccountLease(bool authority)
    {
        await using var f = await Fixture.Create();
        if (authority)
            await Assert.ThrowsAsync<AppException>(() => f.Service.HealHomeLinksAsync(f.Token,
                _ => throw new AppException("auth.unauthorized", 401, 401)));
        else
        {
            f.FailRepair = true;
            Assert.Equal(0, await f.Service.HealHomeLinksAsync(f.Token));
        }
        Assert.Equal(0, FtpAccountLifecycle.References(f.Name));
        using var next = await FtpAccountLifecycle.EnterAsync(f.Name, f.Token);
        Assert.False(Directory.Exists(f.Paths()[1]));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-ftp-repair-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        private readonly JsonFileStore store;
        private readonly TaskCompletionSource snapshotRelease = NewSignal(), repairRelease = NewSignal(), creationRelease = NewSignal();
        internal readonly TaskCompletionSource SnapshotEntered = NewSignal(), RepairEntered = NewSignal(), CreationEntered = NewSignal();
        private int snapshotCount;
        internal readonly string Name = "heal_" + Guid.NewGuid().ToString("N")[..8];
        internal readonly ConcurrentDictionary<string, string> Targets = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> RepairedTargets = [];
        internal readonly SiteDto SiteA, SiteB;
        internal FtpService Service { get; }
        internal bool PauseSnapshot, PauseRepair, PauseCreation, FailRepair;
        internal bool AccountExists = true;
        private string? infrastructureMeasurement;
        internal CancellationToken Token => lifetime.Token;

        private Fixture()
        {
            Directory.CreateDirectory(root);
            SiteA = new() { Id = 51, Name = "a-" + Name, PhysicalPath = Path.Combine(root, "site-a"), ApplicationPoolName = "pool-a" };
            SiteB = new() { Id = 52, Name = "b-" + Name, PhysicalPath = Path.Combine(root, "site-b"), ApplicationPoolName = "pool-b" };
            Directory.CreateDirectory(SiteA.PhysicalPath); Directory.CreateDirectory(SiteB.PhysicalPath);
            var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root });
            store = new JsonFileStore(options);
            var sites = Fake<IIisSiteService>((method, args) => method.Name switch
            {
                "GetSiteAsync" => Task.FromResult((string)args[0]! == SiteA.Name ? SiteA : SiteB),
                "GetSitesAsync" => Task.FromResult<IReadOnlyCollection<SiteDto>>([SiteA, SiteB]),
                _ => throw new NotSupportedException(method.Name)
            });
            Service = new FtpService(sites, store, Fake<IPanelPreferencesService>((method, _) => method.Name == "GetAsync"
                ? Task.FromResult(new PanelPreferencesDto()) : throw new NotSupportedException(method.Name)), options)
            {
                NativeAccountStatus = (_, _) => Task.FromResult(AccountExists ? 0u : 2221u),
                NativeCommand = Command, NativeScript = Script,
                HomeTargetResolver = path => Targets.GetValueOrDefault(path, path),
                HomeRepairSnapshotReady = async (name, _) =>
                {
                    if (PauseSnapshot && name == Name && Interlocked.Increment(ref snapshotCount) == 1)
                    { SnapshotEntered.TrySetResult(); await snapshotRelease.Task.WaitAsync(Token); }
                }
            };
        }
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture();
            await f.AddAccount(f.Name);
            await f.store.WriteAsync("config/ftp-infra.json", new { configured = true }, f.Token);
            return f;
        }
        internal string[] Paths(string? name = null) => Service.HomeLinkPaths(name ?? Name).ToArray();
        internal async Task AddAccount(string name)
        {
            await store.UpdateAsync("config/ftp-users.json", Array.Empty<Row>(), rows => [.. rows,
                new Row { UserName = name, SiteName = SiteA.Name, HomePath = SiteA.PhysicalPath, CreatedAtUtc = DateTime.UtcNow, IsEnabled = true }], Token);
            CreateLink(Paths(name)[0], SiteA.PhysicalPath);
        }
        internal Task<Row[]> Records() => store.ReadAsync("config/ftp-users.json", Array.Empty<Row>(), Token);
        internal async Task ConfigureInfrastructure(bool required, string measurement)
        {
            infrastructureMeasurement = measurement;
            await store.WriteAsync("config/ftp-infra.json", new { configured = false }, Token);
            await store.WriteAsync("config/ftp-tls.json", new { requireSsl = required }, Token);
        }
        internal Task Save(Row row) => store.WriteAsync("config/ftp-users.json", new[] { row }, Token);
        internal Task<FtpUserDto> CreateAccount(string site) => Service.CreateFtpUserAsync(
            new CreateFtpUserRequest { UserName = Name, SiteName = site, Password = "FixturePassword123!" }, Token);
        internal async Task At(Task entered, Task action)
        {
            if (await Task.WhenAny(entered, action).WaitAsync(Token) == action)
            { await action; throw new InvalidOperationException("Operation completed before the intended fake native boundary."); }
        }
        internal async Task Until(Func<bool> condition)
        { while (!condition()) await Task.Delay(10, Token); }
        internal void ReleaseSnapshot() => snapshotRelease.TrySetResult();
        internal void ReleaseRepair() => repairRelease.TrySetResult();
        internal void ReleaseCreation() => creationRelease.TrySetResult();
        private void CreateLink(string path, string target)
        {
            // Plain temporary directories stand in for junctions; the service's read-only
            // resolver seam supplies their target. Never create a real OS junction or account.
            Directory.CreateDirectory(path); Targets[path] = target;
        }
        private void RemoveLinks()
        {
            foreach (var path in Paths())
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                Targets.TryRemove(path, out _);
            }
        }
        private async Task<CommandResult> Command(string executable, string arguments, CancellationToken token)
        {
            if (executable == "cmd.exe")
            {
                Assert.StartsWith("/c mklink /J ", arguments);
                var values = Regex.Matches(arguments, "\"([^\"]*)\"").Select(match => match.Groups[1].Value).ToArray();
                Assert.Equal(2, values.Length);
                if (PauseRepair) { RepairEntered.TrySetResult(); await repairRelease.Task.WaitAsync(Token); }
                if (FailRepair) return new CommandResult { ExitCode = 1 };
                if (Directory.Exists(values[0])) return new CommandResult { ExitCode = 1 };
                CreateLink(values[0], values[1]); RepairedTargets.Add(values[1]);
            }
            else if (executable == "net.exe")
            {
                if (arguments.Contains("/delete", StringComparison.Ordinal)) AccountExists = false;
                else if (arguments.Contains("/add", StringComparison.Ordinal)) AccountExists = true;
                else throw new InvalidOperationException("No native account queries are allowed.");
            }
            else Assert.Equal("icacls.exe", executable);
            return new CommandResult { ExitCode = 0 };
        }
        private async Task<CommandResult> Script(string script, bool strict, CancellationToken token)
        {
            if (script.Contains("Restart-Service FTPSVC", StringComparison.Ordinal))
                return new CommandResult { ExitCode = 0, StandardOutput = "ADAPANEL-FTP-TLS=" + infrastructureMeasurement };
            if (script.StartsWith("if (Get-Service FTPSVC", StringComparison.Ordinal)) return new CommandResult { ExitCode = 0, StandardOutput = "present" };
            if (script.StartsWith("if ((Get-Service FTPSVC", StringComparison.Ordinal)) return new CommandResult { ExitCode = 0, StandardOutput = "up" };
            if (script.Contains("Set-LocalUser", StringComparison.Ordinal)) return new CommandResult { ExitCode = 0, StandardOutput = "ADAPANEL-SURESIZ" };
            Assert.True(strict);
            RemoveLinks();
            if (script.Contains("Add-WebConfiguration", StringComparison.Ordinal))
            {
                var target = Regex.Match(script, @"\$homePath = '([^']*)'").Groups[1].Value;
                Assert.Contains(target, new[] { SiteA.PhysicalPath, SiteB.PhysicalPath });
                CreateLink(Paths()[0], target);
                if (PauseCreation) { CreationEntered.TrySetResult(); await creationRelease.Task.WaitAsync(Token); }
                if (Directory.Exists(Paths()[1])) return new CommandResult { ExitCode = 1, StandardError = "Native junction destination already exists." };
                CreateLink(Paths()[1], target);
            }
            return new CommandResult { ExitCode = 0 };
        }
        public ValueTask DisposeAsync()
        {
            ReleaseSnapshot(); ReleaseRepair(); ReleaseCreation(); lifetime.Cancel(); lifetime.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            return ValueTask.CompletedTask;
        }
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed class Row
    {
        public string UserName { get; set; } = "";
        public string SiteName { get; set; } = "";
        public string HomePath { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public bool IsEnabled { get; set; }
    }
    private static T Fake<T>(Func<MethodInfo, object?[], object?> action) where T : class
    { var value = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)value).Action = action; return value; }
    public class Proxy : DispatchProxy
    { public Func<MethodInfo, object?[], object?> Action = null!; protected override object? Invoke(MethodInfo? method, object?[]? args) => Action(method!, args ?? []); }
}
