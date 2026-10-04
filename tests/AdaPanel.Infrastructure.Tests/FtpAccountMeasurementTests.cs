using System.Reflection;
using System.Security.Claims;
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

/// <summary>Actual FTP controller/service with SQLite, JSON and fake local-account transports.</summary>
public sealed class FtpAccountMeasurementTests
{
    [Theory]
    [InlineData("list", 2u)]
    [InlineData("list", 5u)]
    [InlineData("list", 1722u)]
    [InlineData("create", 2u)]
    [InlineData("create", 5u)]
    [InlineData("create", 1722u)]
    [InlineData("password", 2u)]
    [InlineData("password", 5u)]
    [InlineData("password", 1722u)]
    [InlineData("delete", 2u)]
    [InlineData("delete", 5u)]
    [InlineData("delete", 1722u)]
    public async Task AnUnmeasuredAccountIsNeverAbsentAndCannotAuthorizeAMutation(string route, uint status)
    {
        using var f = await Fixture.Create();
        var original = await f.RecordBytes();
        f.Status = status;
        var error = Assert.IsType<AppException>(await Record.ExceptionAsync(() => f.Run(route)));
        Assert.Equal("ftp.user_status_unknown", error.MessageKey);
        Assert.Equal(503, error.HttpStatusCode);
        Assert.Equal(original, await f.RecordBytes());
        Assert.Empty(f.Effects);
        Assert.True(f.AccountExists);
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task FailedDeleteAndUnmeasuredVerificationKeepTheManagedRecord()
    {
        using var f = await Fixture.Create();
        f.FailDelete = true;
        var original = await f.RecordBytes();
        var error = Assert.IsType<AppException>(await Record.ExceptionAsync(() => f.Run("delete")));
        Assert.Equal("ftp.user_status_unknown", error.MessageKey);
        Assert.Equal(503, error.HttpStatusCode);
        Assert.Contains("delete", f.Effects);
        Assert.True(f.AccountExists);
        Assert.Equal(original, await f.RecordBytes());
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task TrustedDeleteAlsoMeasuresBeforeRemovingExistingAccess()
    {
        using var f = await Fixture.Create();
        var original = await f.RecordBytes();
        f.Status = 5;
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.DeleteFtpUserAsync("fixture_ftp", f.Token));
        Assert.Equal("ftp.user_status_unknown", error.MessageKey);
        Assert.Empty(f.Effects);
        Assert.Equal(original, await f.RecordBytes());
    }

    [Fact]
    public void NativeBuffersAreReleasedOnBothSuccessAndFailure()
    {
        foreach (var status in new[] { 0u, 5u, 2221u })
        {
            var released = 0;
            var owned = new IntPtr(17);
            uint Query(string? server, string name, uint level, out IntPtr buffer)
            {
                Assert.Null(server); Assert.Equal("fixture_ftp", name); Assert.Equal(0u, level);
                buffer = owned; return status;
            }
            var actual = FtpLocalAccountStatus.Read("fixture_ftp", default, Query,
                buffer => { Assert.Equal(owned, buffer); released++; return 0; });
            Assert.Equal(status, actual);
            Assert.Equal(1, released);
        }
    }

    [Fact]
    public void NativeCancellationDoesNotLeakAnAlreadyReturnedBuffer()
    {
        using var caller = new CancellationTokenSource();
        var queried = 0;
        var released = 0;
        uint Query(string? server, string name, uint level, out IntPtr buffer)
        { queried++; buffer = new IntPtr(17); caller.Cancel(); return 0; }
        Assert.ThrowsAny<OperationCanceledException>(() => FtpLocalAccountStatus.Read("fixture_ftp", caller.Token, Query,
            _ => { released++; return 0; }));
        Assert.Equal(1, queried); Assert.Equal(1, released);
        // Already canceled calls never enter the native API.
        Assert.ThrowsAny<OperationCanceledException>(() => FtpLocalAccountStatus.Read("fixture_ftp", caller.Token, Query,
            _ => { released++; return 0; }));
        Assert.Equal(1, queried); Assert.Equal(1, released);
    }

    [Fact]
    public void NullNativeBuffersAreNeverFreedOrAcceptedAsSuccessfulMeasurements()
    {
        uint MissingBuffer(string? server, string name, uint level, out IntPtr buffer)
        { buffer = IntPtr.Zero; return 0; }
        Assert.Equal(13u, FtpLocalAccountStatus.Read("fixture_ftp", default, MissingBuffer,
            _ => throw new InvalidOperationException("No buffer is owned.")));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("create")]
    [InlineData("password")]
    [InlineData("delete")]
    public async Task CancellationAfterNativeReadDoesNotBecomeAbsence(string route)
    {
        using var f = await Fixture.Create();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var original = await f.RecordBytes();
        f.AfterLookup = () => { caller.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run(route, caller.Token));
        Assert.Empty(f.Effects);
        Assert.Equal(original, await f.RecordBytes());
        Assert.Empty(await f.Db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PresentEnabledAndDisabledAccountsBothRemainPresent(bool enabled)
    {
        using var f = await Fixture.Create(enabled: enabled);
        var row = Assert.Single(Assert.IsType<ApiResponse<IReadOnlyCollection<FtpUserDto>>>(await f.Run("list")).Data!);
        Assert.True(row.ExistsOnSystem);
        Assert.Equal(enabled, row.IsEnabled);
        Assert.Empty(f.Effects);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("delete")]
    public async Task AMeasuredMissingAccountRetainsTheLegitimateAbsentBehavior(string route)
    {
        using var f = await Fixture.Create();
        f.AccountExists = false;
        var result = await f.Run(route);
        if (route == "list")
        {
            Assert.False(Assert.Single(Assert.IsType<ApiResponse<IReadOnlyCollection<FtpUserDto>>>(result).Data!).ExistsOnSystem);
            Assert.Empty(f.Effects);
        }
        else
        {
            Assert.DoesNotContain("delete", f.Effects);
            Assert.Empty(await f.Service.GetFtpUsersAsync(f.Token));
            Assert.Single(await f.Db.AuditLogs.ToListAsync());
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("password")]
    [InlineData("delete")]
    public async Task MeasuredNormalMutationPathsRemainAvailable(string route)
    {
        using var f = await Fixture.Create();
        if (route == "create") { f.AccountExists = false; await f.ClearRecord(); }
        await f.Run(route);
        Assert.Contains(route, f.Effects);
        Assert.Equal(route != "delete", f.AccountExists);
        Assert.Single(await f.Db.AuditLogs.ToListAsync());
    }

    private sealed class Fixture : IDisposable
    {
        private const string UserName = "fixture_ftp", SiteName = "fixture.test";
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-ftp-measure-" + Guid.NewGuid().ToString("N"));
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        private readonly JsonFileStore store;
        private readonly ServiceProvider services;
        private readonly FtpController controller;
        private bool deleteAttempted;
        internal AdaPanelDbContext Db { get; }
        internal FtpService Service { get; }
        internal uint? Status;
        internal bool AccountExists = true, FailDelete;
        internal readonly List<string> Effects = [];
        internal Func<Task>? AfterLookup;
        internal CancellationToken Token => lifetime.Token;

        private Fixture()
        {
            Directory.CreateDirectory(root);
            var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root });
            store = new JsonFileStore(options);
            connection.Open();
            Db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            Db.Database.EnsureCreated();
            var actor = new PanelUser { Id = Guid.NewGuid(), UserName = "admin", Email = "admin@example.test", PasswordHash = "accepted-account-hash", Role = UserRole.Administrator, IsActive = true };
            Db.Users.Add(actor); Db.SaveChanges();
            var site = new SiteDto { Id = 31, Name = SiteName, PhysicalPath = Path.Combine(root, "site"), ApplicationPoolName = "fixture-pool" };
            Directory.CreateDirectory(site.PhysicalPath);
            var sites = Fake<IIisSiteService>((method, _) => method.Name switch
            {
                "GetSiteAsync" => Task.FromResult(site),
                "GetSitesAsync" => Task.FromResult<IReadOnlyCollection<SiteDto>>([site]),
                _ => throw new NotSupportedException(method.Name)
            });
            var scopes = new CustomerScopeService(store, new ServicePlanService(store),
                Fake<IResellerQuotaService>((method, _) => method.Name == "GetQuotaAsync" ? Task.FromResult<ResellerQuotaDto?>(null) : throw new NotSupportedException(method.Name)), sites);
            Service = new FtpService(sites, store,
                Fake<IPanelPreferencesService>((method, _) => method.Name == "GetAsync" ? Task.FromResult(new PanelPreferencesDto()) : throw new NotSupportedException(method.Name)), options)
            { NativeCommand = Command, NativeScript = Script, NativeAccountStatus = Lookup };
            services = new ServiceCollection().AddSingleton(Db).AddSingleton(new SessionRevocationStore(store))
                .AddSingleton<IConcurrencyLock, ConcurrencyLock>().AddSingleton<IFtpService>(Service)
                .AddSingleton<ICustomerScopeService>(scopes).AddSingleton<IIisSiteService>(sites)
                .AddSingleton<IAuditService>(new AuditService(Db, scopes, store)).BuildServiceProvider();
            controller = ActivatorUtilities.CreateInstance<FtpController>(services);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString()), new Claim(ClaimTypes.Role, "Administrator"),
                    new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp(actor.PasswordHash)),
                    new Claim(SessionRevocationStore.SessionIdClaim, SessionRevocationStore.NewSessionId())
                ], CookieAuthenticationDefaults.AuthenticationScheme)) } };
        }

        internal static async Task<Fixture> Create(bool enabled = true)
        {
            var f = new Fixture();
            await f.store.WriteAsync("config/ftp-users.json", new[] { new { userName = UserName, siteName = SiteName,
                homePath = Path.Combine(f.root, "site"), createdAtUtc = DateTime.UtcNow, isEnabled = enabled } }, f.Token);
            await f.store.WriteAsync("config/ftp-infra.json", new { configured = true }, f.Token);
            return f;
        }
        internal Task<byte[]> RecordBytes() => File.ReadAllBytesAsync(store.ResolvePath("config/ftp-users.json"), Token);
        internal Task ClearRecord() => store.WriteAsync("config/ftp-users.json", Array.Empty<object>(), Token);
        internal async Task<object> Run(string route, CancellationToken? token = null)
        {
            var ct = token ?? Token;
            IActionResult? result = route switch
            {
                "list" => (await controller.GetUsersAsync(ct)).Result,
                "create" => (await controller.CreateUserAsync(new CreateFtpUserRequest { UserName = UserName, SiteName = SiteName, Password = "FixturePassword123!" }, ct)).Result,
                "password" => (await controller.SetPasswordAsync(new SetFtpPasswordRequest { UserName = UserName, Password = "FixturePassword123!" }, ct)).Result,
                "delete" => (await controller.DeleteUserAsync(UserName, ct)).Result,
                _ => throw new InvalidOperationException(route)
            };
            return Assert.IsType<OkObjectResult>(result).Value!;
        }
        private async Task<uint> Lookup(string userName, CancellationToken token)
        {
            Assert.Equal(UserName, userName);
            var status = deleteAttempted && FailDelete ? 5u : Status ?? (AccountExists ? 0u : 2221u);
            if (AfterLookup is not null) await AfterLookup();
            return status;
        }
        private async Task<CommandResult> Command(string executable, string arguments, CancellationToken token)
        {
            if (executable == "net.exe" && arguments == $"user \"{UserName}\"")
            {
                // Legacy net.exe maps multiple native errors to the same process status. A
                // localized message is intentionally not an authority for declaring absence.
                var status = await Lookup(UserName, token);
                return new CommandResult { ExitCode = status == 2221 ? 2 : checked((int)status), StandardError = status == 0 ? "" : "Local account query could not complete." };
            }
            if (executable == "net.exe" && arguments.Contains("/add", StringComparison.Ordinal))
            {
                Effects.Add("create");
                if (AccountExists) return new CommandResult { ExitCode = 2, StandardError = "Account already exists." };
                AccountExists = true;
            }
            else if (executable == "net.exe" && arguments.Contains("/delete", StringComparison.Ordinal))
            {
                Effects.Add("delete"); deleteAttempted = true;
                if (FailDelete) return new CommandResult { ExitCode = 5 };
                AccountExists = false;
            }
            else if (executable == "net.exe") Effects.Add("password");
            else Effects.Add("acl");
            return new CommandResult { ExitCode = 0 };
        }
        private Task<CommandResult> Script(string script, bool strict, CancellationToken token)
        {
            if (script.StartsWith("if (Get-Service FTPSVC", StringComparison.Ordinal)) return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "present" });
            if (script.StartsWith("if ((Get-Service FTPSVC", StringComparison.Ordinal)) return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = "up" });
            Effects.Add(strict ? "authorization" : "credential-or-delete-fallback");
            return Task.FromResult(new CommandResult { ExitCode = script.Contains("Remove-LocalUser", StringComparison.Ordinal) && FailDelete ? 5 : 0, StandardOutput = "ADAPANEL-SURESIZ" });
        }
        public void Dispose()
        {
            lifetime.Cancel(); services.Dispose(); Db.Dispose(); connection.Dispose(); lifetime.Dispose();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
    private static T Fake<T>(Func<MethodInfo, object?[], object?> action) where T : class
    { var result = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)result).Action = action; return result; }
    public class Proxy : DispatchProxy
    { public Func<MethodInfo, object?[], object?> Action = null!; protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Action(targetMethod!, args ?? []); }
}
