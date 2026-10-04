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
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FileTransferQuotaBoundaryTests
{
    private const int MiB = 1024 * 1024;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AResellerCannotMoveAnotherSubscriptionsBytesIntoAFullDestination(bool directory)
    {
        using var f = await Fixture.Create(UserRole.Reseller);
        var relative = directory ? "tree/item.bin" : "item.bin";
        var bytes = Bytes(1, 0x41);
        f.WriteSource(relative, bytes);
        f.FillDestinationUsage(MiB);

        var failure = await Record.ExceptionAsync(() => f.Move(directory ? "tree" : relative));

        AssertQuotaFailure(failure);
        Assert.Equal(bytes, File.ReadAllBytes(f.Source(relative)));
        Assert.False(File.Exists(f.Destination(relative)));
        Assert.False(Directory.Exists(f.Destination("tree")));
        Assert.Equal(MiB, f.DestinationUsageBytes());
        Assert.Equal(0, f.Audits);
        f.AssertNoStaging();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task AFullSubscriptionCanRenameOrMoveItsExistingBytes(bool rename, bool child)
    {
        using var f = await Fixture.Create(UserRole.Customer, sameOwner: true, child: child);
        var bytes = Bytes(16 * 1024, 0x42);
        f.WriteSource("item.bin", bytes);
        f.FillDestinationUsage(MiB);

        var result = await f.Move("item.bin", newName: rename ? "renamed.bin" : null, destinationIsSource: rename);

        AssertSuccessfulMove(result);
        Assert.False(File.Exists(f.Source("item.bin")));
        Assert.Equal(bytes, File.ReadAllBytes(rename ? f.Source("renamed.bin") : f.Destination("item.bin")));
        Assert.Equal(MiB, f.DestinationUsageBytes());
        Assert.Equal(1, f.Audits);
        f.AssertNoStaging();
    }

    [Fact]
    public async Task AnAdministratorRetainsTheExplicitQuotaExemptionForCrossSubscriptionMoves()
    {
        using var f = await Fixture.Create(UserRole.Administrator);
        f.WriteSource("item.bin", Bytes(1, 0x43));
        f.FillDestinationUsage(MiB);

        var result = await f.Move("item.bin");

        AssertSuccessfulMove(result);
        Assert.False(File.Exists(f.Source("item.bin")));
        Assert.Equal(new byte[] { 0x43 }, File.ReadAllBytes(f.Destination("item.bin")));
        Assert.Equal(MiB + 1L, f.DestinationUsageBytes());
        Assert.Equal(1, f.Audits);
        f.AssertNoStaging();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AResellerCanMoveBytesBetweenSubscriptionsWhenTheDestinationHasRoom(bool directory)
    {
        using var f = await Fixture.Create(UserRole.Reseller, maxDiskMb: 2);
        var relative = directory ? "tree/item.bin" : "item.bin";
        var bytes = Bytes(64 * 1024, 0x44);
        f.WriteSource(relative, bytes);
        f.FillDestinationUsage(MiB);

        var result = await f.Move(directory ? "tree" : relative);

        AssertSuccessfulMove(result);
        Assert.False(File.Exists(f.Source(relative)));
        Assert.Equal(bytes, File.ReadAllBytes(f.Destination(relative)));
        Assert.Equal(MiB + bytes.Length, f.DestinationUsageBytes());
        Assert.Equal(1, f.Audits);
        f.AssertNoStaging();
    }

    [Theory]
    [InlineData(64 * 1024)]
    [InlineData(128 * 1024)]
    public async Task CrossSubscriptionOverwriteMoveDoesNotChargeBytesAlreadyAtTheDestination(int sourceLength)
    {
        using var f = await Fixture.Create(UserRole.Reseller);
        var bytes = Bytes(sourceLength, 0x45);
        f.WriteSource("item.bin", bytes);
        f.WriteDestination("item.bin", Bytes(128 * 1024, 0x46));
        f.FillDestinationUsage(MiB);

        var result = await f.Move("item.bin", overwrite: true);

        AssertSuccessfulMove(result);
        Assert.False(File.Exists(f.Source("item.bin")));
        Assert.Equal(bytes, File.ReadAllBytes(f.Destination("item.bin")));
        Assert.Equal(MiB - 128 * 1024 + sourceLength, f.DestinationUsageBytes());
        Assert.Equal(1, f.Audits);
        f.AssertNoStaging();
    }

    [Theory]
    [InlineData(64 * 1024, 128 * 1024, 1, true)]
    [InlineData(128 * 1024, 128 * 1024, 1, true)]
    [InlineData(192 * 1024, 128 * 1024, 1, false)]
    [InlineData(192 * 1024, 128 * 1024, 2, true)]
    public async Task OverwriteCopyChargesOnlyItsActualNetGrowth(int sourceLength, int targetLength, int limit, bool allowed)
    {
        using var f = await Fixture.Create(UserRole.Customer, sameOwner: true, maxDiskMb: limit);
        var source = Bytes(sourceLength, 0x47);
        var oldTarget = Bytes(targetLength, 0x48);
        f.WriteSource("item.bin", source);
        f.WriteDestination("item.bin", oldTarget);
        f.FillDestinationUsage(MiB);
        CopyFilesResultDto? result = null;

        var failure = await Record.ExceptionAsync(async () => result = await f.Copy("item.bin", overwrite: true));

        Assert.Equal(source, File.ReadAllBytes(f.Source("item.bin")));
        if (allowed)
        {
            Assert.Null(failure);
            AssertSuccessfulCopy(Assert.IsType<CopyFilesResultDto>(result));
            Assert.Equal(source, File.ReadAllBytes(f.Destination("item.bin")));
            Assert.Equal(MiB - targetLength + sourceLength, f.DestinationUsageBytes());
            Assert.Equal(1, f.Audits);
        }
        else
        {
            AssertQuotaFailure(failure);
            Assert.Equal(oldTarget, File.ReadAllBytes(f.Destination("item.bin")));
            Assert.Equal(MiB, f.DestinationUsageBytes());
            Assert.Equal(0, f.Audits);
        }
        f.AssertNoStaging();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASourceGrowingDuringTheUsageAwaitCannotPublishMoreThanTheApprovedBytes(bool directory)
    {
        using var f = await Fixture.Create(UserRole.Reseller, maxDiskMb: 2);
        var relative = directory ? "tree/item.bin" : "item.bin";
        var original = Bytes(1024, 0x49);
        var grown = Bytes(MiB + 1, 0x4a);
        f.WriteSource(relative, original);
        f.FillDestinationUsage(MiB);
        var boundaryCalls = 0;
        var sourceWasLocked = false;
        f.BeforeNextUsage = async token =>
        {
            boundaryCalls++;
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            try { await File.WriteAllBytesAsync(f.Source(relative), grown, token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Holding the measured source open is a valid alternative to staging its bytes.
                sourceWasLocked = true;
                Assert.Equal(original, File.ReadAllBytes(f.Source(relative)));
            }
        };
        CopyFilesResultDto? result = null;

        var failure = await Record.ExceptionAsync(async () => result = await f.Copy(directory ? "tree" : relative));

        Assert.Equal(1, boundaryCalls);
        Assert.Equal(sourceWasLocked ? original.Length : grown.Length, new FileInfo(f.Source(relative)).Length);
        if (failure is null)
        {
            // An immutable original snapshot may be copied; the newer oversized bytes may not.
            AssertSuccessfulCopy(Assert.IsType<CopyFilesResultDto>(result));
            Assert.Equal(original.Length, new FileInfo(f.Destination(relative)).Length);
            Assert.Equal(original, File.ReadAllBytes(f.Destination(relative)));
            Assert.Equal(MiB + original.Length, f.DestinationUsageBytes());
            Assert.Equal(1, f.Audits);
        }
        else
        {
            AssertQuotaFailure(failure);
            Assert.False(File.Exists(f.Destination(relative)));
            Assert.False(Directory.Exists(f.Destination("tree")));
            Assert.Equal(MiB, f.DestinationUsageBytes());
            Assert.Equal(0, f.Audits);
        }
        f.AssertNoStaging();
    }

    [Fact]
    public async Task AStableDirectoryCopyWithinQuotaKeepsItsSourceAndAllSelectedBytes()
    {
        using var f = await Fixture.Create(UserRole.Reseller, maxDiskMb: 2);
        var first = Bytes(12 * 1024, 0x4b);
        var second = Bytes(13 * 1024, 0x4c);
        f.WriteSource("tree/one.bin", first);
        f.WriteSource("tree/nested/two.bin", second);
        f.FillDestinationUsage(MiB);

        AssertSuccessfulCopy(await f.Copy("tree"));

        Assert.Equal(first, File.ReadAllBytes(f.Source("tree/one.bin")));
        Assert.Equal(second, File.ReadAllBytes(f.Source("tree/nested/two.bin")));
        Assert.Equal(first, File.ReadAllBytes(f.Destination("tree/one.bin")));
        Assert.Equal(second, File.ReadAllBytes(f.Destination("tree/nested/two.bin")));
        Assert.Equal(MiB + first.Length + second.Length, f.DestinationUsageBytes());
        Assert.Equal(1, f.Audits);
        f.AssertNoStaging();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringQuotaDiscardsThePrivatePayloadAndPreservesTheTarget(bool directory)
    {
        using var f = await Fixture.Create(UserRole.Reseller, maxDiskMb: 2);
        var relative = directory ? "tree/item.bin" : "item.bin";
        var source = Bytes(2048, 0x51);
        var original = Bytes(4096, 0x52);
        f.WriteSource(relative, source);
        if (!directory) f.WriteDestination(relative, original);
        f.FillDestinationUsage(MiB);
        f.BeforeNextUsage = _ =>
        {
            f.AssertOnePrivatePayloadOutsideSites();
            f.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Copy(directory ? "tree" : relative, overwrite: !directory));

        Assert.Equal(source, File.ReadAllBytes(f.Source(relative)));
        if (directory) Assert.False(Directory.Exists(f.Destination("tree")));
        else Assert.Equal(original, File.ReadAllBytes(f.Destination(relative)));
        Assert.Equal(0, f.Audits);
        f.AssertNoStaging();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AQuotaReadFailureIsNotDowngradedToAnOrdinaryItemResult(bool directory)
    {
        using var f = await Fixture.Create(UserRole.Reseller, maxDiskMb: 2);
        var relative = directory ? "tree/item.bin" : "item.bin";
        var source = Bytes(2048, 0x53);
        var original = Bytes(4096, 0x54);
        f.WriteSource(relative, source);
        if (!directory) f.WriteDestination(relative, original);
        var failure = new IOException("Fixture quota usage is unavailable.");
        f.BeforeNextUsage = _ =>
        {
            f.AssertOnePrivatePayloadOutsideSites();
            return Task.FromException(failure);
        };

        Assert.Same(failure, await Record.ExceptionAsync(() => f.Copy(directory ? "tree" : relative, overwrite: !directory)));

        Assert.Equal(source, File.ReadAllBytes(f.Source(relative)));
        if (directory) Assert.False(Directory.Exists(f.Destination("tree")));
        else Assert.Equal(original, File.ReadAllBytes(f.Destination(relative)));
        Assert.Equal(0, f.Audits);
        f.AssertNoStaging();
    }

    [Fact]
    public async Task AQuotaRejectionOnTheNextItemPreservesTheAlreadyPublishedItem()
    {
        using var f = await Fixture.Create(UserRole.Reseller);
        var first = Bytes(MiB, 0x55);
        var second = Bytes(1, 0x56);
        f.WriteSource("first.bin", first);
        f.WriteSource("second.bin", second);

        AssertQuotaFailure(await Record.ExceptionAsync(() => f.CopyMany(["first.bin", "second.bin"])));

        Assert.Equal(first, File.ReadAllBytes(f.Source("first.bin")));
        Assert.Equal(second, File.ReadAllBytes(f.Source("second.bin")));
        Assert.Equal(first, File.ReadAllBytes(f.Destination("first.bin")));
        Assert.False(File.Exists(f.Destination("second.bin")));
        Assert.Equal(MiB, f.DestinationUsageBytes());
        Assert.Equal(0, f.Audits);
        f.AssertNoStaging();
    }

    private static void AssertQuotaFailure(Exception? failure)
    {
        var error = Assert.IsType<AppException>(failure);
        Assert.Equal("files.quota_exceeded", error.MessageKey);
        Assert.Equal(403, error.HttpStatusCode);
    }

    private static void AssertSuccessfulMove(MoveFilesResultDto result)
    {
        Assert.Equal(1, result.MovedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.True(Assert.Single(result.Items).Moved);
    }

    private static void AssertSuccessfulCopy(CopyFilesResultDto result)
    {
        Assert.Equal(1, result.CopiedCount);
        Assert.Equal(0, result.FailedCount);
        var item = Assert.Single(result.Items);
        Assert.True(item.Copied);
        Assert.Equal(string.Empty, item.ErrorKey);
    }

    private static byte[] Bytes(int count, byte value) => Enumerable.Repeat(value, count).ToArray();

    private sealed class Fixture : IDisposable
    {
        private const string RootPrefix = "adapanel-transfer-quota-";
        private const string PasswordHash = "transfer-quota-fixture-hash";
        private readonly string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), RootPrefix + Guid.NewGuid().ToString("N")));
        private readonly string sourceName = "source-" + Guid.NewGuid().ToString("N") + ".test";
        private readonly string destinationName = "destination-" + Guid.NewGuid().ToString("N") + ".test";
        private readonly Guid sourceOwner = Guid.NewGuid();
        private readonly Guid destinationOwner;
        private readonly Guid actor;
        private readonly bool sameOwner;
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        private readonly FilePublicationAuthorityTests.LocalGate gates = new();
        private readonly AdaPanelDbContext db;
        private readonly ServiceProvider services;
        private readonly CustomerScopeService scopes;
        private readonly FilesController controller;
        private readonly List<string> stagingPaths = [];
        private string SourceRoot => Path.Combine(root, "source");
        private string DestinationRoot => Path.Combine(root, "destination");
        private string VirtualSourceRoot => $"sites/{sourceName}";
        private string VirtualDestinationRoot => $"sites/{destinationName}/out";
        public int Audits { get; private set; }
        public Func<CancellationToken, Task>? BeforeNextUsage { get; set; }

        private Fixture(UserRole role, bool sameOwner, bool child)
        {
            this.sameOwner = sameOwner;
            destinationOwner = sameOwner ? sourceOwner : Guid.NewGuid();
            actor = role == UserRole.Customer && !child ? sourceOwner : Guid.NewGuid();
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(Path.Combine(DestinationRoot, "out"));
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            db.Users.Add(User(sourceOwner, "source-owner", UserRole.Customer));
            if (!sameOwner) db.Users.Add(User(destinationOwner, "destination-owner", UserRole.Customer));
            if (actor != sourceOwner) db.Users.Add(User(actor, "actor", role));
            db.SaveChanges();
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(root, "panel") }));
            var sites = Stub<IIisSiteService>((method, args) => method.Name == nameof(IIisSiteService.GetSiteAsync)
                ? Task.FromResult(ReadSite((string)args![0]!)) : throw new NotSupportedException(method.Name));
            scopes = new CustomerScopeService(store, Stub<IServicePlanService>(),
                Stub<IResellerQuotaService>((method, _) => method.Name == nameof(IResellerQuotaService.GetQuotaAsync)
                    ? Task.FromResult<ResellerQuotaDto?>(null) : throw new NotSupportedException(method.Name)), sites);
            var observedScopes = Stub<ICustomerScopeService>((method, args) => method.Name == nameof(ICustomerScopeService.GetDiskUsageMbAsync)
                ? ReadUsage((Guid)args![0]!, (CancellationToken)args[1]!) : method.Invoke(scopes, args));
            services = new ServiceCollection().AddSingleton(db).AddSingleton(new SessionRevocationStore(store))
                .AddSingleton<IIisSiteService>(sites).AddSingleton<IConcurrencyLock>(gates).BuildServiceProvider();
            var files = new FileService(sites)
            {
                TransferStagingPath = () =>
                {
                    var path = Path.Combine(root, "private-transfer-" + Guid.NewGuid().ToString("N") + ".tmp");
                    stagingPaths.Add(path);
                    return path;
                }
            };
            controller = new FilesController(files, observedScopes,
                Stub<IAuditService>((method, _) =>
                {
                    if (method.Name is not (nameof(IAuditService.WriteAsync) or nameof(IAuditService.WriteForSubscriptionAsync)))
                        throw new NotSupportedException(method.Name);
                    Audits++;
                    return Task.CompletedTask;
                }), new ConcurrentWorkGate())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        RequestServices = services,
                        User = new ClaimsPrincipal(new ClaimsIdentity([
                            new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, role.ToString()),
                            new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp(PasswordHash)),
                            new Claim(SessionRevocationStore.SessionIdClaim, SessionRevocationStore.NewSessionId())],
                            CookieAuthenticationDefaults.AuthenticationScheme))
                    }
                }
            };
        }

        public static async Task<Fixture> Create(UserRole role, bool sameOwner = false, bool child = false, int maxDiskMb = 1)
        {
            var f = new Fixture(role, sameOwner, child);
            try
            {
                await f.scopes.SetScopeAsync(f.sourceOwner, sameOwner ? [f.sourceName, f.destinationName] : [f.sourceName],
                    2, sameOwner ? maxDiskMb : 0, f.lifetime.Token);
                if (!sameOwner)
                    await f.scopes.SetScopeAsync(f.destinationOwner, [f.destinationName], 2, maxDiskMb, f.lifetime.Token);
                if (role == UserRole.Reseller)
                {
                    await f.scopes.SetOwnerAsync(f.sourceOwner, f.actor, f.lifetime.Token);
                    if (!sameOwner) await f.scopes.SetOwnerAsync(f.destinationOwner, f.actor, f.lifetime.Token);
                }
                if (child)
                {
                    await f.scopes.SetParentSubscriptionAsync(f.actor, f.sourceOwner, f.lifetime.Token);
                    await f.scopes.SetSubUserSectionsAsync(f.actor, ["files"], f.lifetime.Token);
                }
                return f;
            }
            catch { f.Dispose(); throw; }
        }

        private static PanelUser User(Guid id, string name, UserRole role) => new()
        { Id = id, UserName = name, Email = name + "@test.invalid", Role = role, IsActive = true, PasswordHash = PasswordHash };

        private SiteDto ReadSite(string name)
        {
            Assert.True(name == sourceName || name == destinationName, "Only fixture sites may be resolved.");
            var path = name == sourceName ? SourceRoot : DestinationRoot;
            return new SiteDto { Id = name == sourceName ? 71 : 72, Name = name, PhysicalPath = path,
                ApplicationRootPath = path, ApplicationPoolName = name };
        }

        private async Task<DiskUsage> ReadUsage(Guid owner, CancellationToken token)
        {
            var before = BeforeNextUsage;
            BeforeNextUsage = null;
            if (before is not null) await before(token);
            return await scopes.GetDiskUsageMbAsync(owner, token);
        }

        public string Source(string relative) => OwnedPath(SourceRoot, relative);
        public string Destination(string relative) => OwnedPath(Path.Combine(DestinationRoot, "out"), relative);
        public void WriteSource(string relative, byte[] bytes) => WriteBytes(Source(relative), bytes);
        public void WriteDestination(string relative, byte[] bytes) => WriteBytes(Destination(relative), bytes);

        private static void WriteBytes(string path, byte[] bytes)
        { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); }

        private string OwnedPath(string parent, string relative)
        {
            var path = Path.GetFullPath(Path.Combine(parent, relative.Replace('/', Path.DirectorySeparatorChar)));
            Assert.StartsWith(root + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
            return path;
        }

        public long DestinationUsageBytes() => BytesUnder(DestinationRoot) + (sameOwner ? BytesUnder(SourceRoot) : 0);
        private static long BytesUnder(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);

        public void FillDestinationUsage(long requestedTotal)
        {
            var present = DestinationUsageBytes();
            Assert.InRange(present, 0, requestedTotal);
            using var filler = new FileStream(Path.Combine(DestinationRoot, "quota-filler.bin"), FileMode.CreateNew, FileAccess.Write);
            filler.SetLength(requestedTotal - present);
        }

        public async Task<MoveFilesResultDto> Move(string relative, bool overwrite = false, string? newName = null, bool destinationIsSource = false)
        {
            var response = await controller.MoveAsync(new MoveFilesRequest { Paths = [VirtualSourceRoot + "/" + relative],
                DestinationPath = destinationIsSource ? VirtualSourceRoot : VirtualDestinationRoot,
                NewName = newName, Overwrite = overwrite }, lifetime.Token);
            return Assert.IsType<ApiResponse<MoveFilesResultDto>>(Assert.IsType<OkObjectResult>(response.Result).Value).Data!;
        }

        public async Task<CopyFilesResultDto> Copy(string relative, bool overwrite = false)
            => await CopyMany([relative], overwrite);

        public async Task<CopyFilesResultDto> CopyMany(string[] relativePaths, bool overwrite = false)
        {
            var response = await controller.CopyAsync(new CopyFilesRequest { Paths = relativePaths.Select(path => VirtualSourceRoot + "/" + path).ToArray(),
                DestinationPath = VirtualDestinationRoot, Overwrite = overwrite }, lifetime.Token);
            return Assert.IsType<ApiResponse<CopyFilesResultDto>>(Assert.IsType<OkObjectResult>(response.Result).Value).Data!;
        }

        public void AssertNoStaging()
        {
            Assert.Empty(Directory.GetFileSystemEntries(root, ".adapanel-*", SearchOption.AllDirectories));
            Assert.All(stagingPaths, path => Assert.False(File.Exists(path), "Private transfer payload remained after completion."));
        }

        public void AssertOnePrivatePayloadOutsideSites()
        {
            var path = Assert.Single(stagingPaths);
            Assert.Equal(root, Path.GetDirectoryName(path));
            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFileSystemEntries(SourceRoot, ".adapanel-*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFileSystemEntries(DestinationRoot, ".adapanel-*", SearchOption.AllDirectories));
        }

        public void Cancel() => lifetime.Cancel();

        public void Dispose()
        {
            services.Dispose(); db.Dispose(); connection.Dispose(); gates.Dispose(); lifetime.Dispose();
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            var name = Path.GetFileName(root);
            if (!string.Equals(Path.GetDirectoryName(root), temp, StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith(RootPrefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[RootPrefix.Length..], "N", out _))
                throw new InvalidOperationException("Transfer fixture cleanup must stay inside its unique owned temporary root.");
            if (!Directory.Exists(root)) return;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var path))
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidOperationException("Transfer fixture cleanup must not traverse a reparse point.");
                if (!attributes.HasFlag(FileAttributes.Directory)) continue;
                foreach (var entry in Directory.GetFileSystemEntries(path))
                {
                    var full = Path.GetFullPath(entry);
                    if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Transfer fixture cleanup escaped its owned root.");
                    pending.Push(full);
                }
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var result = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)result).Handler = handler ?? ((method, _) => throw new NotSupportedException(method.Name));
        return result;
    }

    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
