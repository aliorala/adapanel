using System.IO.Compression;
using System.Reflection;
using System.Security.Claims;
using System.Text;
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

/// <summary>Real user-management scope reassignment runs while the file request holds its native site pin.</summary>
public sealed class FilePublicationOwnerFreshnessTests
{
    private const int MiB = 1024 * 1024;
    private static readonly string Content = new('x', 4096);

    [Theory]
    [InlineData("upload", "scope")]
    [InlineData("write", "scope")]
    [InlineData("compress", "scope")]
    [InlineData("extract", "scope")]
    [InlineData("upload", "usage")]
    [InlineData("write", "usage")]
    [InlineData("compress", "usage")]
    [InlineData("extract", "usage")]
    [InlineData("upload", "approval")]
    [InlineData("write", "approval")]
    [InlineData("compress", "approval")]
    [InlineData("extract", "approval")]
    public async Task ResellerCannotSpendAnEarlierOwnersQuotaAfterLegitimateReassignment(string route, string boundary)
    {
        using var f = await Fixture.Create(route, UserRole.Reseller);
        f.Arm(boundary, f.Reassign);

        var error = await Record.ExceptionAsync(f.Invoke);

        await f.AssertRealReassignment();
        AssertRejected(error);
        Assert.False(File.Exists(f.Output));
        Assert.Equal(MiB, f.UsageBytes());
        Assert.Equal(0, f.FileAudits);
        f.AssertClean();
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("write")]
    [InlineData("compress")]
    [InlineData("extract")]
    public async Task ReassignmentDuringAuditRejectsTheResponseWithoutPretendingToUndoTheCompletedFile(string route)
    {
        using var f = await Fixture.Create(route, UserRole.Reseller);
        f.Arm("audit", f.Reassign);

        var error = await Record.ExceptionAsync(f.Invoke);

        await f.AssertRealReassignment();
        AssertRejected(error);
        f.AssertOutput();
        Assert.Equal(1, f.FileAudits);
        f.AssertClean();
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("write")]
    [InlineData("compress")]
    [InlineData("extract")]
    public async Task CustomerWhoLosesTheSiteDuringQuotaIsAlreadyDeniedBeforePublication(string route)
    {
        using var f = await Fixture.Create(route, UserRole.Customer);
        f.Arm("usage", f.Reassign);

        var error = await Record.ExceptionAsync(f.Invoke);

        await f.AssertRealReassignment();
        AssertRejected(error);
        Assert.False(File.Exists(f.Output));
        Assert.Equal(0, f.FileAudits);
        f.AssertClean();
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("write")]
    [InlineData("compress")]
    [InlineData("extract")]
    public async Task AdministratorCanStillPublishAcrossAnAssignmentChangeWithItsQuotaExemption(string route)
    {
        using var f = await Fixture.Create(route, UserRole.Administrator);
        f.Arm("publication", f.Reassign);

        await f.Invoke();

        await f.AssertRealReassignment();
        f.AssertOutput();
        Assert.True(f.UsageBytes() > MiB);
        Assert.Equal(1, f.FileAudits);
        f.AssertClean();
    }

    [Theory]
    [InlineData("upload", UserRole.Customer)]
    [InlineData("write", UserRole.Customer)]
    [InlineData("compress", UserRole.Customer)]
    [InlineData("extract", UserRole.Customer)]
    [InlineData("upload", UserRole.Reseller)]
    [InlineData("write", UserRole.Reseller)]
    [InlineData("compress", UserRole.Reseller)]
    [InlineData("extract", UserRole.Reseller)]
    public async Task StableOwnerPreservesNormalResellerAndAdditionalLoginBehavior(string route, UserRole role)
    {
        using var f = await Fixture.Create(route, role, child: role == UserRole.Customer);

        await f.Invoke();

        f.AssertOutput();
        Assert.Equal(0, f.OwnerUpdates);
        Assert.Equal(1, f.FileAudits);
        f.AssertClean();
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("write")]
    [InlineData("compress")]
    [InlineData("extract")]
    public async Task CancellationAfterRealReassignmentKeepsTheSiteUnmodified(string route)
    {
        using var f = await Fixture.Create(route, UserRole.Reseller);
        f.Arm("usage", async token => { await f.Reassign(token); f.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(f.Invoke);

        await f.AssertRealReassignment();
        Assert.False(File.Exists(f.Output));
        Assert.Equal(0, f.FileAudits);
        f.AssertClean();
    }

    private static void AssertRejected(Exception? error)
    {
        var rejected = Assert.IsType<AppException>(error);
        Assert.True(rejected.HttpStatusCode is 403 or 409);
    }

    private sealed class Fixture : IDisposable
    {
        private const string Prefix = "adapanel-publication-owner-";
        private const string Hash = "publication-owner-fixture";
        private readonly string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N")));
        private readonly string siteName = "owner-" + Guid.NewGuid().ToString("N") + ".test";
        private readonly Guid ownerA = Guid.NewGuid(), ownerB = Guid.NewGuid(), reseller = Guid.NewGuid(), admin = Guid.NewGuid(), childId = Guid.NewGuid();
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(60));
        private readonly FilePublicationAuthorityTests.LocalGate gates = new();
        private readonly AdaPanelDbContext db;
        private readonly CustomerScopeService scopes;
        private readonly UserManagementService users;
        private readonly ServiceProvider services;
        private readonly FilesController controller;
        private readonly string route;
        private readonly List<string> payloads = [];
        private string? boundary;
        private Func<CancellationToken, Task>? change;
        private bool ownerMeasured, usageMeasured;
        private string SiteRoot => Path.Combine(root, "site");
        private string VirtualRoot => $"sites/{siteName}";
        public string Output => Path.Combine(SiteRoot, "out", route == "compress" ? "result.zip" : "result.txt");
        public int OwnerUpdates { get; private set; }
        public int FileAudits => db.AuditLogs.Count(row => row.Action == "files.uploaded" || row.Action == "files.written" ||
            row.Action == "files.compressed" || row.Action == "files.extracted");

        private Fixture(string route, UserRole role, bool child)
        {
            this.route = route;
            Directory.CreateDirectory(Path.Combine(SiteRoot, "out"));
            File.WriteAllText(Path.Combine(SiteRoot, "source.txt"), Content, new UTF8Encoding(false));
            using (var zip = ZipFile.Open(Path.Combine(SiteRoot, "input.zip"), ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(zip.CreateEntry("result.txt").Open(), new UTF8Encoding(false));
                writer.Write(Content);
            }
            using (var filler = new FileStream(Path.Combine(SiteRoot, "filler.bin"), FileMode.CreateNew, FileAccess.Write))
                filler.SetLength(MiB - UsageBytes());
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            db.Users.AddRange(User(ownerA, "a", UserRole.Customer), User(ownerB, "b", UserRole.Customer),
                User(reseller, "reseller", UserRole.Reseller), User(admin, "admin", UserRole.Administrator), User(childId, "child", UserRole.Customer));
            db.SaveChanges();
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(root, "panel") }));
            var sites = Stub<IIisSiteService>((method, args) => method.Name == nameof(IIisSiteService.GetSiteAsync)
                ? ReadSite((string)args![0]!, (CancellationToken)args[1]!) : throw new NotSupportedException(method.Name));
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
            var files = new FileService(sites) { UploadStagingPath = PrivatePayload, CompressionStagingPath = PrivatePayload };
            var actor = role == UserRole.Administrator ? admin : role == UserRole.Reseller ? reseller : child ? childId : ownerA;
            controller = new FilesController(files, observed,
                Stub<IAuditService>((method, args) => ObserveAudit(audit, method, args!)), new ConcurrentWorkGate())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
                {
                    RequestServices = services,
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, role.ToString()),
                        new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp(Hash)),
                        new Claim(SessionRevocationStore.SessionIdClaim, SessionRevocationStore.NewSessionId())], CookieAuthenticationDefaults.AuthenticationScheme))
                } }
            };
        }

        public static async Task<Fixture> Create(string route, UserRole role, bool child = false)
        {
            var f = new Fixture(route, role, child);
            try
            {
                await f.scopes.SetScopeAsync(f.ownerA, [f.siteName], 2, 8);
                await f.scopes.SetScopeAsync(f.ownerB, [], 2, 1);
                await f.scopes.SetOwnerAsync(f.ownerA, f.reseller);
                await f.scopes.SetOwnerAsync(f.ownerB, f.reseller);
                if (child)
                {
                    await f.scopes.SetParentSubscriptionAsync(f.childId, f.ownerA);
                    await f.scopes.SetSubUserSectionsAsync(f.childId, ["files"]);
                }
                return f;
            }
            catch { f.Dispose(); throw; }
        }

        private static PanelUser User(Guid id, string name, UserRole role) => new()
        { Id = id, UserName = name, Email = name + "@test.invalid", Role = role, IsActive = true, PasswordHash = Hash };
        public void Arm(string when, Func<CancellationToken, Task> action) { boundary = when; change = action; }
        private async Task Trigger(string when, CancellationToken token)
        {
            if (boundary != when || change is not { } action) return;
            change = null;
            await Task.Yield();
            await action(token);
        }
        private async Task<SiteDto> ReadSite(string name, CancellationToken token)
        {
            Assert.Equal(siteName, name);
            if (Directory.GetFiles(SiteRoot, ".adapanel-write-*", SearchOption.AllDirectories).Length != 0)
                await Trigger("publication", token);
            return new SiteDto { Id = 81, Name = siteName, PhysicalPath = SiteRoot,
                ApplicationRootPath = SiteRoot, ApplicationPoolName = siteName };
        }
        private async Task<Guid?> ReadOwner(string name, CancellationToken token)
        {
            var owner = await scopes.GetSiteOwnerUserIdAsync(name, token);
            ownerMeasured = true;
            return owner;
        }
        private async Task<CustomerScope> ReadScope(Guid owner, CancellationToken token)
        {
            var value = await scopes.GetScopeAsync(owner, token);
            if (owner == ownerA && ownerMeasured) await Trigger("scope", token);
            if (owner == reseller && usageMeasured) await Trigger("approval", token);
            return value;
        }
        private async Task<DiskUsage> ReadUsage(Guid owner, CancellationToken token)
        {
            var value = await scopes.GetDiskUsageMbAsync(owner, token);
            if (owner == ownerA) { usageMeasured = true; await Trigger("usage", token); }
            return value;
        }
        private async Task ObserveAudit(IAuditService actual, MethodInfo method, object?[] args)
        {
            await (Task)method.Invoke(actual, args)!;
            if (args[0] is string action && action.StartsWith("files.", StringComparison.Ordinal)) await Trigger("audit", lifetime.Token);
        }
        public async Task Reassign(CancellationToken token)
        {
            await Update(ownerA, [], token);
            await Update(ownerB, [siteName], token);
        }
        private async Task Update(Guid owner, string[] assigned, CancellationToken token)
        {
            var scope = await scopes.GetScopeAsync(owner, token);
            var user = await db.Users.AsNoTracking().SingleAsync(row => row.Id == owner, token);
            await users.UpdateUserAsync(owner, new UpdateUserRequest { Email = user.Email, Role = "Customer",
                OwnerId = reseller, IsActive = true, AssignedSites = assigned, MaxSites = 2, MaxDiskMb = scope.MaxDiskMb }, admin, null, token);
            OwnerUpdates++;
        }
        public async Task AssertRealReassignment()
        {
            Assert.Equal(2, OwnerUpdates);
            Assert.Equal(2, await db.AuditLogs.CountAsync(row => row.Action == "users.updated"));
            Assert.Equal(ownerB, await scopes.GetSiteOwnerUserIdAsync(siteName));
            Assert.Contains(siteName, await scopes.GetResellerSitesAsync(reseller));
            Assert.Empty(await scopes.GetAssignedSitesAsync(ownerA));
            Assert.Equal(1, (await scopes.GetScopeAsync(ownerB)).MaxDiskMb);
        }
        public async Task Invoke()
        {
            ActionResult result;
            if (route == "upload")
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Content));
                result = (await controller.UploadAsync(VirtualRoot + "/out", new FormFile(stream, 0, stream.Length, "file", "result.txt"), lifetime.Token)).Result!;
            }
            else if (route == "write")
                result = (await controller.WriteAsync(new WriteFileRequest { Path = VirtualRoot + "/out/result.txt", Content = Content }, lifetime.Token)).Result!;
            else if (route == "compress")
                result = (await controller.CompressAsync(new ArchiveFileRequest { Path = VirtualRoot + "/source.txt", DestinationPath = VirtualRoot + "/out/result.zip" }, lifetime.Token)).Result!;
            else
                result = (await controller.ExtractAsync(new ArchiveFileRequest { Path = VirtualRoot + "/input.zip", DestinationPath = VirtualRoot + "/out" }, lifetime.Token)).Result!;
            Assert.IsType<OkObjectResult>(result);
        }
        public void AssertOutput()
        {
            if (route != "compress") Assert.Equal(Content, File.ReadAllText(Output));
            else
            {
                using var zip = ZipFile.OpenRead(Output);
                using var reader = new StreamReader(Assert.Single(zip.Entries).Open());
                Assert.Equal(Content, reader.ReadToEnd());
            }
        }
        public long UsageBytes() => Directory.GetFiles(SiteRoot, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        private string PrivatePayload()
        {
            var path = Path.Combine(root, "private-" + Guid.NewGuid().ToString("N") + ".tmp");
            payloads.Add(path); return path;
        }
        public void Cancel() => lifetime.Cancel();
        public void AssertClean()
        {
            Assert.All(payloads, path => Assert.False(File.Exists(path)));
            Assert.Empty(Directory.GetFileSystemEntries(root, ".adapanel-*", SearchOption.AllDirectories));
        }
        public void Dispose()
        {
            services.Dispose(); db.Dispose(); connection.Dispose(); gates.Dispose(); lifetime.Dispose();
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            var name = Path.GetFileName(root);
            if (!string.Equals(Path.GetDirectoryName(root), temp, StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[Prefix.Length..], "N", out _))
                throw new InvalidOperationException("Cleanup must stay in the uniquely owned fixture root.");
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
                        throw new InvalidOperationException("Cleanup escaped the owned root.");
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
