using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Files;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Users;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Files;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Site ownership is changed through the real user-management writer, including its
/// subscription lease and JSON publication. No fake changes a returned owner value.
/// The reseller remains authorized for both sites; only the quota owner changes.
/// </summary>
public sealed class FileTransferOwnerFreshnessTests
{
    private const int MiB = 1024 * 1024;
    private static readonly byte[] Content = Enumerable.Repeat((byte)0x6b, 4096).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameSubscriptionMoveExemptionCannotUseThePreviousSourceOwner(bool directory)
    {
        using var f = await Fixture.Create(sameOwner: true);
        var relative = directory ? "tree/item.bin" : "item.bin";
        f.WriteSource(relative);
        f.FillDestination(MiB);
        f.AfterSourceOwnerRead = token => f.ReassignSource(token);

        var error = await Record.ExceptionAsync(() => f.Transfer("move", directory ? "tree" : relative));

        await f.AssertReassignmentWasReal(sourceChanged: true);
        AssertRejected(error);
        Assert.Equal(Content, File.ReadAllBytes(f.Source(relative)));
        Assert.False(File.Exists(f.Destination(relative)));
        Assert.Equal(MiB, f.DestinationBytes());
        Assert.Equal(0, f.FileAuditCount);
        f.AssertNoStaging();
    }

    [Theory]
    [InlineData("copy", "owner")]
    [InlineData("copy", "scope")]
    [InlineData("copy", "usage")]
    [InlineData("move", "owner")]
    [InlineData("move", "scope")]
    [InlineData("move", "usage")]
    public async Task ATransferCannotSpendThePreviousDestinationOwnersQuota(string operation, string boundary)
    {
        using var f = await Fixture.Create();
        f.WriteSource("item.bin");
        f.FillDestination(MiB);
        f.ArmDestinationChange(operation, boundary);

        var error = await Record.ExceptionAsync(() => f.Transfer(operation, "item.bin"));

        await f.AssertReassignmentWasReal(sourceChanged: false);
        AssertRejected(error);
        Assert.Equal(Content, File.ReadAllBytes(f.Source("item.bin")));
        Assert.False(File.Exists(f.Destination("item.bin")));
        Assert.Equal(MiB, f.DestinationBytes());
        Assert.Equal(0, f.FileAuditCount);
        f.AssertNoStaging();
    }

    [Theory]
    [InlineData("move", true)]
    [InlineData("move", false)]
    [InlineData("copy", true)]
    [InlineData("copy", false)]
    public async Task StableSubscriptionOwnersKeepNormalTransferBehavior(string operation, bool sameOwner)
    {
        using var f = await Fixture.Create(sameOwner: sameOwner);
        f.WriteSource("item.bin");
        // A full same-subscription move has zero net growth. A copy still needs room.
        f.FillDestination(operation == "move" && sameOwner ? MiB - Content.Length : 32 * 1024);

        Assert.True(await f.Transfer(operation, "item.bin"));

        Assert.Equal(Content, File.ReadAllBytes(f.Destination("item.bin")));
        Assert.Equal(operation == "copy", File.Exists(f.Source("item.bin")));
        Assert.Equal(1, f.FileAuditCount);
        Assert.Equal(0, f.OwnerUpdates);
        f.AssertNoStaging();
    }

    [Fact]
    public async Task AdministratorQuotaExemptionStillAllowsAnExplicitOverQuotaCopy()
    {
        using var f = await Fixture.Create(administrator: true);
        f.WriteSource("item.bin");
        f.FillDestination(9 * MiB);

        Assert.True(await f.Transfer("copy", "item.bin"));

        Assert.Equal(Content, File.ReadAllBytes(f.Source("item.bin")));
        Assert.Equal(Content, File.ReadAllBytes(f.Destination("item.bin")));
        Assert.Equal(1, f.FileAuditCount);
        f.AssertNoStaging();
    }

    [Fact]
    public async Task CancellationAfterTheRealOwnerChangeDoesNotPublishOrLeaveAPayload()
    {
        using var f = await Fixture.Create();
        f.WriteSource("item.bin");
        f.FillDestination(MiB);
        f.AfterQuotaUsage = async token => { await f.ReassignDestination(token); f.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Transfer("copy", "item.bin"));

        await f.AssertReassignmentWasReal(sourceChanged: false);
        Assert.Equal(Content, File.ReadAllBytes(f.Source("item.bin")));
        Assert.False(File.Exists(f.Destination("item.bin")));
        Assert.Equal(0, f.FileAuditCount);
        f.AssertNoStaging();
    }

    [Theory]
    [InlineData("move")]
    [InlineData("copy")]
    public async Task OwnershipIsCheckedAgainAfterTheFinalActorApprovalAwait(string operation)
    {
        using var f = await Fixture.Create();
        f.WriteSource("item.bin");
        f.FillDestination(MiB);
        f.AfterFinalActorApproval = f.ReassignDestination;

        AssertRejected(await Record.ExceptionAsync(() => f.Transfer(operation, "item.bin")));

        await f.AssertReassignmentWasReal(sourceChanged: false);
        Assert.Equal(Content, File.ReadAllBytes(f.Source("item.bin")));
        Assert.False(File.Exists(f.Destination("item.bin")));
        Assert.Equal(0, f.FileAuditCount);
        f.AssertNoStaging();
    }

    [Theory]
    [InlineData("move")]
    [InlineData("copy")]
    public async Task AnOwnerChangeDuringAuditPreventsAStaleSuccessButDoesNotUndoCompletedFiles(string operation)
    {
        using var f = await Fixture.Create();
        f.WriteSource("item.bin");
        f.FillDestination(MiB);
        f.AfterFileAudit = f.ReassignDestination;

        AssertRejected(await Record.ExceptionAsync(() => f.Transfer(operation, "item.bin")));

        await f.AssertReassignmentWasReal(sourceChanged: false);
        Assert.Equal(Content, File.ReadAllBytes(f.Destination("item.bin")));
        Assert.Equal(operation == "copy", File.Exists(f.Source("item.bin")));
        Assert.Equal(1, f.FileAuditCount);
        f.AssertNoStaging();
    }

    private static void AssertRejected(Exception? error)
    {
        // Either fresh quota evaluation or an explicit stale-owner conflict is safe;
        // successful publication against the old owner's allowance is not.
        var rejected = Assert.IsType<AppException>(error);
        Assert.True(rejected.HttpStatusCode is 403 or 409);
    }

    private sealed class Fixture : IDisposable
    {
        private const string Prefix = "adapanel-transfer-owner-";
        private const string PasswordHash = "owner-freshness-fixture";
        private readonly string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N")));
        private readonly string sourceName = "source-" + Guid.NewGuid().ToString("N") + ".test";
        private readonly string targetName = "target-" + Guid.NewGuid().ToString("N") + ".test";
        private readonly Guid ownerA = Guid.NewGuid(), ownerB = Guid.NewGuid(), ownerC = Guid.NewGuid();
        private readonly Guid reseller = Guid.NewGuid(), administrator = Guid.NewGuid();
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        private readonly FilePublicationAuthorityTests.LocalGate gates = new();
        private readonly AdaPanelDbContext db;
        private readonly CustomerScopeService scopes;
        private readonly UserManagementService users;
        private readonly FilesController controller;
        private readonly ServiceProvider services;
        private readonly List<string> payloadPaths = [];
        private readonly bool sameOwner;
        private int destinationOwnerReads, changeAfterDestinationRead;
        private bool quotaUsageRead;
        private string SourceRoot => Path.Combine(root, "source");
        private string TargetRoot => Path.Combine(root, "target");
        public int OwnerUpdates { get; private set; }
        public int FileAuditCount => db.AuditLogs.Count(log => log.Action == "files.moved" || log.Action == "files.copied");
        public Func<CancellationToken, Task>? AfterSourceOwnerRead { get; set; }
        public Func<CancellationToken, Task>? AfterQuotaUsage { get; set; }
        public Func<CancellationToken, Task>? AfterFinalActorApproval { get; set; }
        public Func<CancellationToken, Task>? AfterFileAudit { get; set; }
        private Func<CancellationToken, Task>? afterDestinationOwnerRead, afterQuotaScope;

        private Fixture(bool sameOwner, bool administratorActor)
        {
            this.sameOwner = sameOwner;
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(Path.Combine(TargetRoot, "out"));
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            db.Users.AddRange(User(ownerA, "a", UserRole.Customer), User(ownerB, "b", UserRole.Customer),
                User(ownerC, "c", UserRole.Customer), User(reseller, "reseller", UserRole.Reseller),
                User(administrator, "admin", UserRole.Administrator));
            db.SaveChanges();
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(root, "panel") }));
            var sites = Stub<IIisSiteService>((method, args) => method.Name == nameof(IIisSiteService.GetSiteAsync)
                ? Task.FromResult(ReadSite((string)args![0]!)) : throw new NotSupportedException(method.Name));
            scopes = new CustomerScopeService(store, Stub<IServicePlanService>(),
                Stub<IResellerQuotaService>((method, _) => method.Name == nameof(IResellerQuotaService.GetQuotaAsync)
                    ? Task.FromResult<ResellerQuotaDto?>(null) : throw new NotSupportedException(method.Name)), sites);
            var audit = new AuditService(db, scopes, store);
            users = new UserManagementService(db, new PasswordHasher<PanelUser>(), scopes,
                Stub<INotificationService>(), Stub<IPanelPreferencesService>(), Stub<IPanelSecurityService>(),
                NullLogger<UserManagementService>.Instance, audit, sites,
                Stub<IDatabaseOwnershipService>((method, _) => method.Name == nameof(IDatabaseOwnershipService.CountForOwnerAsync)
                    ? Task.FromResult(0) : throw new NotSupportedException(method.Name)),
                Stub<ISchedulerService>(), Stub<ISiteCascadeDeletionService>(), Stub<IApiTokenService>(), Stub<IServerOwnerService>());
            var observed = Stub<ICustomerScopeService>((method, args) => method.Name switch
            {
                nameof(ICustomerScopeService.GetSiteOwnerUserIdAsync) => ReadOwner((string)args![0]!, (CancellationToken)args[1]!),
                nameof(ICustomerScopeService.GetScopeAsync) => ReadScope((Guid)args![0]!, (CancellationToken)args[1]!),
                nameof(ICustomerScopeService.GetDiskUsageMbAsync) => ReadUsage((Guid)args![0]!, (CancellationToken)args[1]!),
                _ => method.Invoke(scopes, args)
            });
            services = new ServiceCollection().AddSingleton(db).AddSingleton(new SessionRevocationStore(store))
                .AddSingleton<IIisSiteService>(sites).AddSingleton<IConcurrencyLock>(gates).BuildServiceProvider();
            var files = new FileService(sites)
            {
                TransferStagingPath = () =>
                {
                    var path = Path.Combine(root, "private-" + Guid.NewGuid().ToString("N") + ".tmp");
                    payloadPaths.Add(path);
                    return path;
                }
            };
            var actor = administratorActor ? administrator : reseller;
            var role = administratorActor ? UserRole.Administrator : UserRole.Reseller;
            var observedAudit = Stub<IAuditService>((method, args) => ObserveFileAudit(audit, method, args!));
            controller = new FilesController(files, observed, observedAudit, new ConcurrentWorkGate())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
                {
                    RequestServices = services,
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, role.ToString()),
                        new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp(PasswordHash)),
                        new Claim(SessionRevocationStore.SessionIdClaim, SessionRevocationStore.NewSessionId())],
                        CookieAuthenticationDefaults.AuthenticationScheme))
                } }
            };
        }

        public static async Task<Fixture> Create(bool sameOwner = false, bool administrator = false)
        {
            var f = new Fixture(sameOwner, administrator);
            try
            {
                await f.scopes.SetScopeAsync(f.ownerA, sameOwner ? [f.sourceName, f.targetName] : [f.sourceName], 3, sameOwner ? 1 : 0);
                await f.scopes.SetScopeAsync(f.ownerB, sameOwner ? [] : [f.targetName], 3, 8);
                await f.scopes.SetScopeAsync(f.ownerC, [], 3, 1);
                foreach (var owner in new[] { f.ownerA, f.ownerB, f.ownerC }) await f.scopes.SetOwnerAsync(owner, f.reseller);
                return f;
            }
            catch { f.Dispose(); throw; }
        }

        private static PanelUser User(Guid id, string name, UserRole role) => new()
        { Id = id, UserName = name, Email = name + "@test.invalid", Role = role, IsActive = true, PasswordHash = PasswordHash };

        private SiteDto ReadSite(string name)
        {
            Assert.True(name == sourceName || name == targetName);
            var path = name == sourceName ? SourceRoot : TargetRoot;
            return new SiteDto { Id = name == sourceName ? 61 : 62, Name = name, PhysicalPath = path,
                ApplicationRootPath = path, ApplicationPoolName = name };
        }

        public void ArmDestinationChange(string operation, string boundary)
        {
            if (boundary == "owner")
            {
                // Move first compares owners, then EnsureDiskQuota resolves the target again.
                // Pause the latter real read, not a harmless earlier read that is refreshed.
                changeAfterDestinationRead = operation == "move" ? 2 : 1;
                afterDestinationOwnerRead = ReassignDestination;
            }
            else if (boundary == "scope") afterQuotaScope = ReassignDestination;
            else AfterQuotaUsage = ReassignDestination;
        }

        private async Task<Guid?> ReadOwner(string site, CancellationToken token)
        {
            var value = await scopes.GetSiteOwnerUserIdAsync(site, token);
            Func<CancellationToken, Task>? hook = null;
            if (site == sourceName) { hook = AfterSourceOwnerRead; AfterSourceOwnerRead = null; }
            if (site == targetName && ++destinationOwnerReads == changeAfterDestinationRead)
            { hook = afterDestinationOwnerRead; afterDestinationOwnerRead = null; }
            if (hook is not null) { await Task.Yield(); await hook(token); }
            return value;
        }

        private async Task<CustomerScope> ReadScope(Guid owner, CancellationToken token)
        {
            var value = await scopes.GetScopeAsync(owner, token);
            if (owner == ownerB && afterQuotaScope is { } hook)
            { afterQuotaScope = null; await Task.Yield(); await hook(token); }
            if (owner == reseller && quotaUsageRead && AfterFinalActorApproval is { } finalHook)
            { AfterFinalActorApproval = null; await Task.Yield(); await finalHook(token); }
            return value;
        }

        private async Task<DiskUsage> ReadUsage(Guid owner, CancellationToken token)
        {
            var value = await scopes.GetDiskUsageMbAsync(owner, token);
            if (owner == ownerB && AfterQuotaUsage is { } hook)
            { AfterQuotaUsage = null; await Task.Yield(); await hook(token); }
            if (owner == ownerB) quotaUsageRead = true;
            return value;
        }

        private async Task ObserveFileAudit(IAuditService actual, MethodInfo method, object?[] args)
        {
            await (Task)method.Invoke(actual, args)!;
            if (args[0] is string action && (action is "files.moved" or "files.copied") && AfterFileAudit is { } hook)
            {
                AfterFileAudit = null;
                await Task.Yield();
                await hook(lifetime.Token);
            }
        }

        public async Task ReassignSource(CancellationToken token)
        {
            await UpdateScope(ownerA, [targetName], token);
            await UpdateScope(ownerB, [sourceName], token);
        }

        public async Task ReassignDestination(CancellationToken token)
        {
            await UpdateScope(sameOwner ? ownerA : ownerB, sameOwner ? [sourceName] : [], token);
            await UpdateScope(ownerC, [targetName], token);
        }

        private async Task UpdateScope(Guid owner, string[] assigned, CancellationToken token)
        {
            var existing = await scopes.GetScopeAsync(owner, token);
            var user = await db.Users.AsNoTracking().SingleAsync(item => item.Id == owner, token);
            // This is the real /users/{id} service path, not a direct JSON replacement.
            // Its subscription lease may run while the file operation owns site use pins.
            await users.UpdateUserAsync(owner, new UpdateUserRequest { Email = user.Email, Role = "Customer",
                OwnerId = reseller, IsActive = true, AssignedSites = assigned, MaxSites = 3,
                MaxDiskMb = existing.MaxDiskMb }, administrator, null, token);
            OwnerUpdates++;
        }

        public async Task AssertReassignmentWasReal(bool sourceChanged)
        {
            Assert.Equal(2, OwnerUpdates);
            Assert.Equal(2, await db.AuditLogs.CountAsync(log => log.Action == "users.updated"));
            Assert.Equal(sourceChanged ? ownerB : ownerA, await scopes.GetSiteOwnerUserIdAsync(sourceName));
            Assert.Equal(sourceChanged ? ownerA : ownerC, await scopes.GetSiteOwnerUserIdAsync(targetName));
            var accessible = await scopes.GetResellerSitesAsync(reseller);
            Assert.Contains(sourceName, accessible);
            Assert.Contains(targetName, accessible);
            Assert.Equal(1, (await scopes.GetScopeAsync(sourceChanged ? ownerA : ownerC)).MaxDiskMb);
            Assert.Equal(1, (await scopes.GetDiskUsageMbAsync(sourceChanged ? ownerA : ownerC)).UsageMb);
        }

        public string Source(string relative) => OwnedPath(SourceRoot, relative);
        public string Destination(string relative) => OwnedPath(Path.Combine(TargetRoot, "out"), relative);
        private string OwnedPath(string parent, string relative)
        {
            var path = Path.GetFullPath(Path.Combine(parent, relative.Replace('/', Path.DirectorySeparatorChar)));
            Assert.StartsWith(root + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
            return path;
        }
        public void WriteSource(string relative)
        { var path = Source(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, Content); }
        public void FillDestination(long bytes)
        { using var output = new FileStream(Path.Combine(TargetRoot, "filler.bin"), FileMode.CreateNew, FileAccess.Write); output.SetLength(bytes); }
        public long DestinationBytes() => Directory.GetFiles(TargetRoot, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);

        public async Task<bool> Transfer(string operation, string relative)
        {
            var paths = new[] { $"sites/{sourceName}/{relative}" };
            var destination = $"sites/{targetName}/out";
            if (operation == "move")
            {
                var response = await controller.MoveAsync(new MoveFilesRequest { Paths = paths, DestinationPath = destination }, lifetime.Token);
                var result = Assert.IsType<ApiResponse<MoveFilesResultDto>>(Assert.IsType<OkObjectResult>(response.Result).Value).Data!;
                return result.MovedCount == 1 && result.FailedCount == 0;
            }
            var copied = await controller.CopyAsync(new CopyFilesRequest { Paths = paths, DestinationPath = destination }, lifetime.Token);
            var copy = Assert.IsType<ApiResponse<CopyFilesResultDto>>(Assert.IsType<OkObjectResult>(copied.Result).Value).Data!;
            return copy.CopiedCount == 1 && copy.FailedCount == 0;
        }

        public void Cancel() => lifetime.Cancel();
        public void AssertNoStaging()
        {
            Assert.All(payloadPaths, path => Assert.False(File.Exists(path)));
            Assert.Empty(Directory.GetFileSystemEntries(root, ".adapanel-*", SearchOption.AllDirectories));
        }

        public void Dispose()
        {
            services.Dispose(); db.Dispose(); connection.Dispose(); gates.Dispose(); lifetime.Dispose();
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            var name = Path.GetFileName(root);
            if (!string.Equals(Path.GetDirectoryName(root), temp, StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[Prefix.Length..], "N", out _))
                throw new InvalidOperationException("Cleanup must stay inside the uniquely owned fixture root.");
            if (!Directory.Exists(root)) return;
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var path))
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("Unexpected fixture reparse point.");
                if (!attributes.HasFlag(FileAttributes.Directory)) continue;
                foreach (var entry in Directory.GetFileSystemEntries(path))
                {
                    var full = Path.GetFullPath(entry);
                    if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Cleanup escaped the owned fixture root.");
                    pending.Push(full);
                }
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var proxy = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)proxy).Handler = handler ?? ((method, _) => throw new NotSupportedException(method.Name));
        return proxy;
    }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
