using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Audit;
using AdaPanel.Infrastructure.Ftp;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>Actual controller/service and SQLite ownership; only fixture-owned temporary junctions are native.</summary>
public sealed class FtpHomeBoundaryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ScopedCreationRejectsAnExternalHomeOrAncestorBeforeAnyAccountEffect(bool ancestor, bool existing)
    {
        await using var f = await Fixture.Create(existing: existing, ancestor: ancestor);
        await f.ReplaceHomeWithOutsideJunction();
        var before = await f.StateBytes();
        var error = await Record.ExceptionAsync(() => f.Run());
        Assert.Equal(403, Assert.IsType<AppException>(error).HttpStatusCode);
        Assert.Empty(f.Effects);
        Assert.Empty(f.OutsideEffects);
        Assert.Equal(before, await f.StateBytes());
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
        Assert.Equal("outside sentinel", await File.ReadAllTextAsync(f.Sentinel));
    }

    [Theory]
    [InlineData("lookup", false)]
    [InlineData("lookup", true)]
    [InlineData("expiry", false)]
    [InlineData("expiry", true)]
    [InlineData("acl", false)]
    [InlineData("acl", true)]
    public async Task AHomeReplacedDuringNativeAwaitCannotAcquireOutsidePermissionsOrFtpRouting(string boundary, bool ancestor)
    {
        await using var f = await Fixture.Create(existing: true, ancestor: ancestor);
        f.Pause = boundary;
        var before = await f.StateBytes();
        var action = f.Run();
        await f.WaitForBoundary(action);
        try { await f.ReplaceHomeWithOutsideJunction(); }
        finally { f.Release.TrySetResult(); }
        var error = await Record.ExceptionAsync(() => action);
        Assert.Equal(403, Assert.IsType<AppException>(error).HttpStatusCode);
        Assert.Empty(f.OutsideEffects);
        Assert.Equal(before, await f.StateBytes());
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
        Assert.True(f.AccountExists);
        Assert.Equal("outside sentinel", await File.ReadAllTextAsync(f.Sentinel));
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("acl")]
    public async Task ANewAccountIsStillRecoveredWithoutTouchingTheReplacedHome(string boundary)
    {
        await using var f = await Fixture.Create();
        f.Pause = boundary;
        var before = await f.StateBytes();
        var action = f.Run();
        await f.WaitForBoundary(action);
        Assert.True(f.AccountExists);
        try { await f.ReplaceHomeWithOutsideJunction(); }
        finally { f.Release.TrySetResult(); }
        var error = await Record.ExceptionAsync(() => action);
        Assert.Equal(403, Assert.IsType<AppException>(error).HttpStatusCode);
        Assert.Empty(f.OutsideEffects);
        Assert.False(f.AccountExists);
        Assert.Contains("delete", f.Effects);
        Assert.Equal(before, await f.StateBytes());
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
        Assert.Equal("outside sentinel", await File.ReadAllTextAsync(f.Sentinel));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinarySiteSubdirectoriesRemainUsable(bool existing)
    {
        await using var f = await Fixture.Create(existing: existing);
        var result = await f.Run();
        Assert.Equal(f.Home, result.HomePath);
        Assert.True(f.AccountExists);
        Assert.Contains("grant", f.Effects);
        Assert.Empty(f.OutsideEffects);
        Assert.Single(await f.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task AnAdministratorMayStillChooseAnExplicitOrdinaryCustomDirectory()
    {
        await using var f = await Fixture.Create(role: "Administrator");
        f.Home = f.Outside;
        var result = await f.Run();
        Assert.Equal(f.Outside, result.HomePath);
        Assert.Contains("grant", f.Effects);
        Assert.Single(await f.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task ExistingPanelInfrastructureHomeJunctionsAreNotRejectedAsRequestedHomeEscapes()
    {
        await using var f = await Fixture.Create(existing: true);
        foreach (var link in f.Service.HomeLinkPaths(f.UserName)) await f.CreateOwnedJunction(link, f.Home);
        var result = await f.Run();
        Assert.Equal(f.Home, result.HomePath);
        Assert.Empty(f.OutsideEffects);
        Assert.Single(await f.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task CancellationAtTheAccountProbePreventsAllNativeMutations()
    {
        await using var f = await Fixture.Create(existing: true);
        f.Pause = "lookup";
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var before = await f.StateBytes();
        var action = f.Run(caller.Token);
        await f.WaitForBoundary(action);
        caller.Cancel(); f.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        Assert.Empty(f.Effects);
        Assert.Equal(before, await f.StateBytes());
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-ftp-home-boundary-" + Guid.NewGuid().ToString("N"));
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        private readonly JsonFileStore store;
        private readonly ServiceProvider services;
        private readonly FtpController controller;
        private readonly CustomerScopeService scopes;
        private readonly Guid actor = Guid.NewGuid(), owner = Guid.NewGuid();
        private readonly SiteDto site;
        private readonly List<string> junctions = [];
        private int paused;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Effects = [], OutsideEffects = [];
        internal readonly string UserName = "home_" + Guid.NewGuid().ToString("N")[..8];
        internal readonly string Outside, Link, Sentinel;
        internal string Home, Pause = "";
        internal bool AccountExists;
        internal AdaPanelDbContext Db { get; }
        internal FtpService Service { get; }
        internal CancellationToken Token => deadline.Token;

        private Fixture(string role, bool existing, bool ancestor)
        {
            Directory.CreateDirectory(root);
            site = new SiteDto { Id = 241, Name = "owned-" + UserName + ".test", PhysicalPath = Path.Combine(root, "site"), ApplicationPoolName = "pool-" + UserName };
            Directory.CreateDirectory(site.PhysicalPath);
            Outside = Path.Combine(root, "other-tenant"); Directory.CreateDirectory(Outside);
            Directory.CreateDirectory(Path.Combine(Outside, "nested"));
            Sentinel = Path.Combine(Outside, "private.txt"); File.WriteAllText(Sentinel, "outside sentinel");
            Link = Path.Combine(site.PhysicalPath, "ftp-content");
            Home = ancestor ? Path.Combine(Link, "nested") : Link;
            Directory.CreateDirectory(Home);
            var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root });
            store = new JsonFileStore(options);
            connection.Open();
            Db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            Db.Database.EnsureCreated();
            Db.Users.AddRange(new PanelUser { Id = actor, UserName = "actor", Email = "actor@example.test", IsActive = true, PasswordHash = "accepted", Role = Enum.Parse<UserRole>(role) },
                new PanelUser { Id = owner, UserName = "owner", Email = "owner@example.test", IsActive = true, PasswordHash = "accepted", Role = UserRole.Customer });
            Db.SaveChanges();
            var sites = Fake<IIisSiteService>((method, _) => method.Name switch
            {
                "GetSiteAsync" => Task.FromResult(new SiteDto { Id = site.Id, Name = site.Name, PhysicalPath = site.PhysicalPath, ApplicationPoolName = site.ApplicationPoolName }),
                "GetSitesAsync" => Task.FromResult<IReadOnlyCollection<SiteDto>>([site]),
                _ => throw new NotSupportedException(method.Name)
            });
            scopes = new CustomerScopeService(store, new ServicePlanService(store), Fake<IResellerQuotaService>((method, _) =>
                method.Name == "GetQuotaAsync" ? Task.FromResult<ResellerQuotaDto?>(null) : throw new NotSupportedException(method.Name)), sites);
            AccountExists = existing;
            Service = new FtpService(sites, store, Fake<IPanelPreferencesService>((method, _) => method.Name == "GetAsync"
                ? Task.FromResult(new PanelPreferencesDto()) : throw new NotSupportedException(method.Name)), options)
            { NativeAccountStatus = Lookup, NativeCommand = Command, NativeScript = Script };
            var sessions = new SessionRevocationStore(store);
            services = new ServiceCollection().AddSingleton(Db).AddSingleton(sessions).AddSingleton<IConcurrencyLock, ConcurrencyLock>()
                .AddSingleton<ICustomerScopeService>(scopes).AddSingleton<IIisSiteService>(sites).AddSingleton<IFtpService>(Service)
                .AddSingleton<IAuditService>(new AuditService(Db, scopes, store)).BuildServiceProvider();
            controller = ActivatorUtilities.CreateInstance<FtpController>(services);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, role),
                    new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("accepted")),
                    new Claim(SessionRevocationStore.SessionIdClaim, SessionRevocationStore.NewSessionId())
                ], CookieAuthenticationDefaults.AuthenticationScheme)) } };
        }
        internal static async Task<Fixture> Create(string role = "Reseller", bool existing = false, bool ancestor = false)
        {
            var f = new Fixture(role, existing, ancestor);
            await f.scopes.SetScopeAsync(f.owner, [f.site.Name], 0, 0, f.Token);
            await f.scopes.SetOwnerAsync(f.owner, f.actor, f.Token);
            await f.store.WriteAsync("config/ftp-infra.json", new { configured = true }, f.Token);
            await f.store.WriteAsync("config/ftp-users.json", existing
                ? new[] { new { userName = f.UserName, siteName = f.site.Name, homePath = f.Home, isEnabled = true, createdAtUtc = DateTime.UtcNow.AddDays(-2) } }
                : [], f.Token);
            return f;
        }
        internal Task<byte[]> StateBytes() => File.ReadAllBytesAsync(store.ResolvePath("config/ftp-users.json"), Token);
        internal async Task<FtpUserDto> Run(CancellationToken? token = null)
        {
            var response = await controller.CreateUserAsync(new CreateFtpUserRequest
                { UserName = UserName, SiteName = site.Name, HomePath = Home, Password = "FixturePassword123!" }, token ?? Token);
            return Assert.IsType<ApiResponse<FtpUserDto>>(Assert.IsType<OkObjectResult>(response.Result).Value).Data!;
        }
        internal async Task WaitForBoundary(Task action)
        {
            if (await Task.WhenAny(Entered.Task, action).WaitAsync(Token) == action)
            { await action; throw new InvalidOperationException("Create ended before its intended fake native boundary."); }
        }
        private async Task PauseAt(string boundary)
        {
            if (Pause == boundary && Interlocked.Exchange(ref paused, 1) == 0)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(Token); }
        }
        private async Task<uint> Lookup(string name, CancellationToken token)
        { Assert.Equal(UserName, name); await PauseAt("lookup"); return AccountExists ? 0u : 2221u; }
        private async Task<CommandResult> Command(string executable, string arguments, CancellationToken token)
        {
            if (executable == "net.exe")
            {
                if (arguments.Contains("/add", StringComparison.Ordinal)) { Effects.Add("create"); AccountExists = true; }
                else if (arguments.Contains("/delete", StringComparison.Ordinal)) { Effects.Add("delete"); AccountExists = false; }
                else throw new NotSupportedException("Unexpected native account operation.");
            }
            else
            {
                Assert.Equal("icacls.exe", executable);
                var path = Regex.Match(arguments, "^\"([^\"]*)\"").Groups[1].Value;
                var effect = arguments.Contains("/remove:g", StringComparison.Ordinal) ? "remove-grant" : "grant";
                Effects.Add(effect); RecordOutside(effect, path);
                await PauseAt("acl");
            }
            return new CommandResult { ExitCode = 0, StandardOutput = "Successfully processed 1 files; Failed processing 0 files" };
        }
        private async Task<CommandResult> Script(string script, bool strict, CancellationToken token)
        {
            if (script.StartsWith("if (Get-Service FTPSVC", StringComparison.Ordinal)) return new CommandResult { ExitCode = 0, StandardOutput = "present" };
            if (script.StartsWith("if ((Get-Service FTPSVC", StringComparison.Ordinal)) return new CommandResult { ExitCode = 0, StandardOutput = "up" };
            if (script.Contains("Set-LocalUser", StringComparison.Ordinal))
            { Effects.Add("expiry"); await PauseAt("expiry"); return new CommandResult { ExitCode = 0, StandardOutput = "ADAPANEL-SURESIZ" }; }
            Assert.True(strict);
            if (script.Contains("Add-WebConfiguration", StringComparison.Ordinal))
            {
                var path = Regex.Match(script, @"\$homePath = '([^']*)'").Groups[1].Value;
                Effects.Add("configure-home"); RecordOutside("configure-home", path);
            }
            else Effects.Add("remove-home");
            return new CommandResult { ExitCode = 0 };
        }
        private void RecordOutside(string effect, string path)
        {
            var current = root;
            foreach (var segment in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, segment);
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    current = Directory.ResolveLinkTarget(current, true)!.FullName;
            }
            if (PathContainment.IsRootOrInside(Outside, current)) OutsideEffects.Add(effect);
        }
        internal async Task ReplaceHomeWithOutsideJunction()
        {
            ValidateOwnedRoot();
            Assert.True(PathContainment.IsInsideRoot(root, Link));
            Assert.Equal((FileAttributes)0, File.GetAttributes(Link) & FileAttributes.ReparsePoint);
            Directory.Delete(Link, recursive: true);
            await CreateOwnedJunction(Link, Outside);
        }
        internal async Task CreateOwnedJunction(string link, string target)
        {
            Assert.True(PathContainment.IsInsideRoot(root, link)); Assert.True(PathContainment.IsInsideRoot(root, target));
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            using var process = Process.Start(new ProcessStartInfo { FileName = "cmd.exe", Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(Token);
            Assert.True(process.ExitCode == 0, (await output) + (await error));
            junctions.Add(link);
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        }
        private void ValidateOwnedRoot()
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            Assert.Equal(tempRoot, Path.GetDirectoryName(fullRoot), StringComparer.OrdinalIgnoreCase);
            Assert.StartsWith("adapanel-ftp-home-boundary-", Path.GetFileName(fullRoot), StringComparison.Ordinal);
            Assert.Equal((FileAttributes)0, File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint);
        }
        public async ValueTask DisposeAsync()
        {
            Release.TrySetResult(); deadline.Cancel();
            ValidateOwnedRoot();
            foreach (var link in junctions.AsEnumerable().Reverse())
                if (Directory.Exists(link)) Directory.Delete(link); // Unlink only; never recurse through fixture junctions.
            services.Dispose(); await Db.DisposeAsync(); await connection.DisposeAsync(); deadline.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
    private static T Fake<T>(Func<MethodInfo, object?[], object?> action) where T : class
    { var value = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)value).Action = action; return value; }
    public class Proxy : DispatchProxy
    { public Func<MethodInfo, object?[], object?> Action = null!; protected override object? Invoke(MethodInfo? method, object?[]? args) => Action(method!, args ?? []); }
}
