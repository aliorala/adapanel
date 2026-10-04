using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using F = AdaPanel.Infrastructure.Tests.SubscriptionBackupMeasurementFixture;

namespace AdaPanel.Infrastructure.Tests;

public sealed class QueuedBackupReadAuthorityTests
{
    [Theory]
    [InlineData("list", "password")]
    [InlineData("list", "disabled")]
    [InlineData("list", "logout")]
    [InlineData("list", "role")]
    [InlineData("download", "password")]
    [InlineData("download", "disabled")]
    [InlineData("download", "logout")]
    [InlineData("download", "role")]
    [InlineData("entries", "password")]
    [InlineData("entries", "disabled")]
    [InlineData("entries", "logout")]
    [InlineData("entries", "role")]
    [InlineData("chain", "password")]
    [InlineData("chain", "disabled")]
    [InlineData("chain", "logout")]
    [InlineData("chain", "role")]
    public async Task AReadCannotDiscloseItsArchiveAfterTheOriginalActorIsRevoked(string route, string change)
    {
        using var fixture = await Fixture.Create(route);
        var pending = fixture.Read();
        await fixture.WaitForRead(pending);
        await fixture.ChangeActor(change);
        fixture.Resume();

        var error = await Assert.ThrowsAsync<AppException>(() => pending);
        Assert.Equal(401, error.HttpStatusCode);
        Assert.False(fixture.Published);
        await fixture.AssertArchiveUnchanged();
    }

    [Theory]
    [InlineData("entries", "site")]
    [InlineData("entries", "section")]
    [InlineData("entries", "parent")]
    [InlineData("entries", "reseller")]
    [InlineData("chain", "site")]
    [InlineData("chain", "section")]
    [InlineData("chain", "parent")]
    [InlineData("chain", "reseller")]
    public async Task ArchiveIoDoesNotCarryAnEarlierTenantOrSectionApprovalForward(string route, string change)
    {
        var role = change == "reseller" ? "Reseller" : change is "parent" or "section" ? "SubUser" : "Customer";
        using var fixture = await Fixture.Create(route, role);
        var pending = fixture.Read();
        await fixture.WaitForRead(pending);
        await fixture.ChangeScope(change);
        fixture.Resume();

        var error = await Assert.ThrowsAsync<AppException>(() => pending);
        Assert.True(error.HttpStatusCode is 401 or 403 or 404);
        Assert.False(fixture.Published);
        await fixture.AssertArchiveUnchanged();
    }

    [Fact]
    public async Task DeletingAnActorDuringArchiveReadCannotReturnItsEntries()
    {
        using var fixture = await Fixture.Create("entries");
        var pending = fixture.Read();
        await fixture.WaitForRead(pending);
        await fixture.Db.Users.Where(user => user.Id == fixture.Actor).ExecuteDeleteAsync();
        fixture.Resume();
        var error = await Assert.ThrowsAsync<AppException>(() => pending);
        Assert.Equal(401, error.HttpStatusCode);
        Assert.False(fixture.Published);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("download")]
    [InlineData("entries")]
    [InlineData("chain")]
    public async Task ACompletedReadStillHonorsCancellationBeforePublishing(string route)
    {
        using var fixture = await Fixture.Create(route);
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Read(cancellation.Token);
        await fixture.WaitForRead(pending);
        cancellation.Cancel();
        fixture.Resume();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(fixture.Published);
        await fixture.AssertArchiveUnchanged();
    }

    [Theory]
    [InlineData("list", "Customer")]
    [InlineData("list", "Reseller")]
    [InlineData("list", "Administrator")]
    [InlineData("download", "Customer")]
    [InlineData("download", "Reseller")]
    [InlineData("download", "Administrator")]
    [InlineData("entries", "Customer")]
    [InlineData("entries", "Reseller")]
    [InlineData("entries", "Administrator")]
    [InlineData("chain", "Customer")]
    [InlineData("chain", "Reseller")]
    [InlineData("chain", "Administrator")]
    public async Task AnUnchangedActorReceivesTheRealOwnedArchive(string route, string role)
    {
        using var fixture = await Fixture.Create(route, role);
        var pending = fixture.Read();
        await fixture.WaitForRead(pending);
        fixture.Resume();
        await pending;
        Assert.True(fixture.Published);
        await fixture.AssertArchiveUnchanged();
    }

    [Theory]
    [InlineData("list")]
    [InlineData("download")]
    [InlineData("entries")]
    [InlineData("chain")]
    public async Task APanelTokenPrincipalDoesNotNeedCookieOnlySessionClaims(string route)
    {
        using var fixture = await Fixture.Create(route, panelToken: true);
        var pending = fixture.Read();
        await fixture.WaitForRead(pending);
        fixture.Resume();
        await pending;
        Assert.True(fixture.Published);
        await fixture.AssertArchiveUnchanged();
    }

    private static T Data<T>(ActionResult<ApiResponse<T>> response) =>
        Assert.IsType<ApiResponse<T>>(Assert.IsType<OkObjectResult>(response.Result).Value).Data!;

    private sealed class Fixture : IDisposable
    {
        private readonly F backup = new();
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string route;
        private readonly Guid reseller = Guid.NewGuid(), admin = Guid.NewGuid(), child = Guid.NewGuid();
        private readonly string sessionId = SessionRevocationStore.NewSessionId();
        private readonly ServiceProvider services;
        private readonly SessionRevocationStore sessions;
        private BackupController controller = null!;
        private BackupRecordDto record = null!;
        private byte[] archiveBytes = [], indexBytes = [];
        private int waited;
        internal AdaPanelDbContext Db { get; }
        internal Guid Actor { get; }
        internal bool Published { get; private set; }

        private Fixture(string route, string role)
        {
            this.route = route;
            connection.Open();
            Db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            Db.Database.EnsureCreated();
            foreach (var id in new[] { F.Owner, F.Other, reseller, admin, child })
                Db.Users.Add(new PanelUser
                {
                    Id = id, UserName = id.ToString("N"), Email = id.ToString("N") + "@example.test", IsActive = true,
                    PasswordHash = "accepted-password-hash",
                    Role = id == admin ? UserRole.Administrator : id == reseller ? UserRole.Reseller : UserRole.Customer
                });
            Db.SaveChanges();
            Actor = role switch { "Administrator" => admin, "Reseller" => reseller, "SubUser" => child, _ => F.Owner };
            sessions = new SessionRevocationStore(backup.Store);
            services = new ServiceCollection().AddSingleton(Db).AddSingleton(sessions).BuildServiceProvider();
        }

        internal static async Task<Fixture> Create(string route, string role = "Customer", bool panelToken = false)
        {
            var fixture = new Fixture(route, role);
            await fixture.backup.Scopes.SetOwnerAsync(F.Owner, fixture.reseller);
            await fixture.backup.Scopes.SetParentSubscriptionAsync(fixture.child, F.Owner);
            await fixture.backup.Scopes.SetSubUserSectionsAsync(fixture.child, ["backup"]);
            await fixture.backup.Ownership.RecordAsync("mysql", "private_ledger", F.Owner, F.Site);
            fixture.record = await fixture.backup.Service.BackupSiteAsync(new BackupSiteRequest
            {
                SiteName = F.Site, IncludeFiles = true, IncludeConfig = false, Incremental = true,
                Databases = [new() { Engine = "mysql", DatabaseName = "private_ledger" }]
            });
            Assert.Equal(F.Owner.ToString("N"), fixture.record.SubscriptionUserId);
            fixture.archiveBytes = await File.ReadAllBytesAsync(fixture.record.Path);
            fixture.indexBytes = await File.ReadAllBytesAsync(fixture.backup.Store.ResolvePath("config/backups.json"));
            var proxy = DispatchProxy.Create<IBackupService, ReadProxy>();
            ((ReadProxy)(object)proxy).InvokeRead = fixture.InvokeService;
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, fixture.Actor.ToString()),
                new(ClaimTypes.Role, role == "SubUser" ? "Customer" : role)
            };
            if (!panelToken)
            {
                claims.Add(new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("accepted-password-hash")));
                claims.Add(new Claim(SessionRevocationStore.SessionIdClaim, fixture.sessionId));
            }
            fixture.controller = new BackupController(proxy, fixture.backup.Scopes, fixture.backup.Ownership, null!, null!,
                new AuditService(fixture.Db, fixture.backup.Scopes, fixture.backup.Store), fixture.Db)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
                {
                    RequestServices = fixture.services,
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims,
                        panelToken ? PanelTokenAuthenticationHandler.SchemeName : CookieAuthenticationDefaults.AuthenticationScheme))
                } }
            };
            return fixture;
        }

        private object InvokeService(MethodInfo method, object?[] args) => method.Name switch
        {
            "GetBackupsAsync" => Deliver(backup.Service.GetBackupsAsync((CancellationToken)args[0]!), route is "list" or "download"),
            "GetEntriesAsync" => Deliver(backup.Service.GetEntriesAsync((string)args[0]!, (string?)args[1], (CancellationToken)args[2]!), route == "entries"),
            "GetChainAsync" => Deliver(backup.Service.GetChainAsync((string)args[0]!, (CancellationToken)args[1]!), route == "chain"),
            _ => throw new NotSupportedException(method.Name)
        };

        private async Task<T> Deliver<T>(Task<T> actualRead, bool pause)
        {
            var result = await actualRead;
            if (pause && Interlocked.Exchange(ref waited, 1) == 0)
            {
                // Model an IO completion whose response is ready but has not yet resumed
                // the controller. No source mutation lease is held by this read wrapper.
                entered.TrySetResult();
                await released.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            return result;
        }

        internal async Task Read(CancellationToken token = default)
        {
            switch (route)
            {
                case "list": Assert.Equal(record.Id, Assert.Single(Data(await controller.GetBackupsAsync(token))).Id); break;
                case "download": Assert.Equal(record.Path, Assert.IsType<PhysicalFileResult>(await controller.DownloadAsync(record.Id, token)).FileName); break;
                case "entries": Assert.Contains(Data(await controller.GetEntriesAsync(record.Id, "databases/", token)).Entries,
                    item => item.Path == "databases/mysql-private_ledger.sql"); break;
                case "chain": Assert.Equal(record.Id, Assert.Single(Data(await controller.GetChainAsync(record.Id, token)).Links).BackupId); break;
                default: throw new InvalidOperationException(route);
            }
            Published = true;
        }

        internal async Task WaitForRead(Task pending)
        {
            var completed = await Task.WhenAny(entered.Task, pending).WaitAsync(TimeSpan.FromSeconds(10));
            if (completed == pending) { await pending; throw new InvalidOperationException("Read ended before the intended archive IO boundary."); }
            Assert.False(pending.IsCompleted);
        }
        internal void Resume() => released.TrySetResult();
        internal Task ChangeActor(string change) => change switch
        {
            "password" => Db.Users.Where(user => user.Id == Actor).ExecuteUpdateAsync(update => update.SetProperty(user => user.PasswordHash, "replacement-password-hash")),
            "disabled" => Db.Users.Where(user => user.Id == Actor).ExecuteUpdateAsync(update => update.SetProperty(user => user.IsActive, false)),
            "role" => Db.Users.Where(user => user.Id == Actor).ExecuteUpdateAsync(update => update.SetProperty(user => user.Role, UserRole.Administrator)),
            "logout" => sessions.RevokeAsync(sessionId),
            _ => throw new InvalidOperationException(change)
        };
        internal async Task ChangeScope(string change)
        {
            switch (change)
            {
                case "site":
                    await backup.Scopes.SetScopeAsync(F.Owner, [], 0, 0);
                    await backup.Scopes.SetScopeAsync(F.Other, [F.Site], 0, 0); break;
                case "section": await backup.Scopes.SetSubUserSectionsAsync(child, []); break;
                case "parent": await backup.Scopes.SetParentSubscriptionAsync(child, F.Other); break;
                case "reseller": await backup.Scopes.SetOwnerAsync(F.Owner, F.Other); break;
                default: throw new InvalidOperationException(change);
            }
        }
        internal async Task AssertArchiveUnchanged()
        {
            Assert.Equal(archiveBytes, await File.ReadAllBytesAsync(record.Path));
            Assert.Equal(indexBytes, await File.ReadAllBytesAsync(backup.Store.ResolvePath("config/backups.json")));
            Assert.Empty(await Db.AuditLogs.ToListAsync());
        }
        public void Dispose() { Resume(); services.Dispose(); Db.Dispose(); connection.Dispose(); backup.Dispose(); }
    }

    public class ReadProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object> InvokeRead { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => InvokeRead(targetMethod!, args ?? []);
    }
}
