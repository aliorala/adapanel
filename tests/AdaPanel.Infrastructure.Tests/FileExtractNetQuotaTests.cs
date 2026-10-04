using System.IO.Compression;
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

public sealed class FileExtractNetQuotaTests
{
    private const int MiB = 1024 * 1024;

    [Theory]
    [InlineData(UserRole.Customer, false, 64 * 1024)]
    [InlineData(UserRole.Customer, false, 128 * 1024)]
    [InlineData(UserRole.Customer, true, 64 * 1024)]
    [InlineData(UserRole.Customer, true, 128 * 1024)]
    [InlineData(UserRole.Reseller, false, 64 * 1024)]
    [InlineData(UserRole.Reseller, false, 128 * 1024)]
    public async Task AFullSubscriptionCanReplaceAnEntryWithoutIncreasingItsUsage(UserRole role, bool child, int length)
    {
        using var f = await Fixture.Create(role, child);
        var accepted = Bytes(length, 0x31);
        f.WriteArchive(("nested/payload.bin", accepted));
        f.WriteOutput("nested/payload.bin", Bytes(128 * 1024, 0x32));
        f.FillUsage(MiB);

        var result = await f.Extract();

        Assert.Equal(accepted, File.ReadAllBytes(f.Output("nested/payload.bin")));
        Assert.Equal(MiB - 128 * 1024 + length, f.UsageBytes());
        Assert.Equal(f.VirtualOutput, result.Path);
        Assert.Empty(result.PhysicalPath);
        Assert.Equal(1, f.Audits);
        f.AssertNoStagingAndReleasedArchive();
    }

    [Fact]
    public async Task AnEntryThatReallyIncreasesAFullSubscriptionsUsageIsRejected()
    {
        using var f = await Fixture.Create(UserRole.Customer);
        var old = Bytes(128 * 1024, 0x41);
        f.WriteArchive(("payload.bin", Bytes(old.Length + 1, 0x42)));
        f.WriteOutput("payload.bin", old);
        f.FillUsage(MiB);

        AssertQuota(await Record.ExceptionAsync(() => f.Extract()));

        Assert.Equal(old, File.ReadAllBytes(f.Output("payload.bin")));
        Assert.Equal(MiB, f.UsageBytes());
        Assert.Equal(0, f.Audits);
        f.AssertNoStagingAndReleasedArchive();
    }

    [Fact]
    public async Task AnOverwriteWithEnoughRoomPublishesTheExactLargerEntry()
    {
        using var f = await Fixture.Create(UserRole.Customer, maxDiskMb: 2);
        var accepted = Bytes(192 * 1024, 0x51);
        f.WriteArchive(("payload.bin", accepted));
        f.WriteOutput("payload.bin", Bytes(128 * 1024, 0x52));
        f.FillUsage(MiB);

        await f.Extract();

        Assert.Equal(accepted, File.ReadAllBytes(f.Output("payload.bin")));
        Assert.Equal(MiB + 64 * 1024, f.UsageBytes());
        Assert.Equal(1, f.Audits);
        f.AssertNoStagingAndReleasedArchive();
    }

    [Fact]
    public async Task ANewEntryCannotBorrowTheLengthOfAnUnrelatedExistingFile()
    {
        using var f = await Fixture.Create(UserRole.Customer);
        var unrelated = Bytes(128 * 1024, 0x61);
        f.WriteArchive(("new.bin", Bytes(1, 0x62)));
        f.WriteOutput("unrelated.bin", unrelated);
        f.FillUsage(MiB);

        AssertQuota(await Record.ExceptionAsync(() => f.Extract()));

        Assert.False(File.Exists(f.Output("new.bin")));
        Assert.Equal(unrelated, File.ReadAllBytes(f.Output("unrelated.bin")));
        Assert.Equal(MiB, f.UsageBytes());
        Assert.Equal(0, f.Audits);
        f.AssertNoStagingAndReleasedArchive();
    }

    [Theory]
    [InlineData(UserRole.Administrator, 1)]
    [InlineData(UserRole.Customer, 0)]
    public async Task AdministratorAndUnlimitedSubscriptionsRetainTheirExistingExemption(UserRole role, int maxDiskMb)
    {
        using var f = await Fixture.Create(role, maxDiskMb: maxDiskMb);
        var accepted = Bytes(64 * 1024, 0x71);
        f.WriteArchive(("new.bin", accepted));
        f.FillUsage(MiB);

        await f.Extract();

        Assert.Equal(accepted, File.ReadAllBytes(f.Output("new.bin")));
        Assert.Equal(MiB + accepted.Length, f.UsageBytes());
        Assert.Equal(1, f.Audits);
        f.AssertNoStagingAndReleasedArchive();
    }

    [Fact]
    public async Task RepeatedEntriesForOneWindowsPathUseItsCurrentPublishedLength()
    {
        using var f = await Fixture.Create(UserRole.Customer);
        var last = Bytes(128 * 1024, 0x81);
        f.WriteArchive(("payload.bin", Bytes(64 * 1024, 0x82)), ("PAYLOAD.bin", last));
        f.WriteOutput("payload.bin", Bytes(last.Length, 0x83));
        f.FillUsage(MiB);

        await f.Extract();

        Assert.Equal(last, File.ReadAllBytes(f.Output("payload.bin")));
        Assert.Single(Directory.GetFiles(f.OutputRoot));
        Assert.Equal(MiB, f.UsageBytes());
        Assert.Equal(1, f.Audits);
        f.AssertNoStagingAndReleasedArchive();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheDeductedDestinationStaysUnchangedThroughQuotaAndFinalApproval(bool finalApproval)
    {
        using var f = await Fixture.Create(UserRole.Customer);
        var accepted = Bytes(64 * 1024, 0x91);
        f.WriteArchive(("payload.bin", accepted));
        f.WriteOutput("payload.bin", Bytes(128 * 1024, 0x92));
        f.FillUsage(MiB);
        var attempted = false;
        Exception? mutationFailure = null;
        Task TryChange()
        {
            if (attempted) return Task.CompletedTask;
            attempted = true;
            mutationFailure = Record.Exception(() => File.WriteAllBytes(f.Output("payload.bin"), []));
            return Task.CompletedTask;
        }
        if (finalApproval)
            f.BeforeSiteRead = () => f.HasStagedEntry() ? TryChange() : Task.CompletedTask;
        else f.BeforeUsage = _ => TryChange();

        await f.Extract();

        Assert.True(attempted);
        Assert.True(mutationFailure is IOException or UnauthorizedAccessException,
            "The quota deduction must remain backed by the original file until the final await completes.");
        Assert.Equal(accepted, File.ReadAllBytes(f.Output("payload.bin")));
        Assert.Equal(1, f.Audits);
        f.AssertOutputHandleReleased("payload.bin");
        f.AssertNoStagingAndReleasedArchive();
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("empty")]
    public async Task CancellationAtTheFirstQuotaCheckCreatesNoDestination(string shape)
    {
        using var f = await Fixture.Create(UserRole.Customer);
        if (shape == "file") f.WriteArchive(("nested/payload.bin", Bytes(1, 0xA1)));
        else if (shape == "directory") f.WriteArchive(("nested/", []));
        else f.WriteArchive();
        f.RemoveEmptyOutputDirectory();
        f.BeforeUsage = _ => { f.Cancel(); return Task.CompletedTask; };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Extract());

        Assert.False(Directory.Exists(f.OutputRoot));
        Assert.Equal(0, f.Audits);
        f.AssertNoStagingAndReleasedArchive();
    }

    [Fact]
    public async Task CancellationDuringOverwriteQuotaKeepsTheOldFileAndReleasesItsHandle()
    {
        using var f = await Fixture.Create(UserRole.Customer);
        var old = Bytes(128 * 1024, 0xB1);
        f.WriteArchive(("payload.bin", Bytes(64 * 1024, 0xB2)));
        f.WriteOutput("payload.bin", old);
        f.BeforeUsage = _ => { f.Cancel(); return Task.CompletedTask; };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Extract());

        Assert.Equal(old, File.ReadAllBytes(f.Output("payload.bin")));
        Assert.Equal(0, f.Audits);
        f.AssertOutputHandleReleased("payload.bin");
        f.AssertNoStagingAndReleasedArchive();
    }

    [Fact]
    public async Task AQuotaReadFailureKeepsTheOldFileAndTheOriginalFailure()
    {
        using var f = await Fixture.Create(UserRole.Customer);
        var old = Bytes(128 * 1024, 0xC1);
        f.WriteArchive(("payload.bin", Bytes(64 * 1024, 0xC2)));
        f.WriteOutput("payload.bin", old);
        var failure = new IOException("The fixture usage measurement could not be read.");
        f.BeforeUsage = _ => Task.FromException(failure);

        Assert.Same(failure, await Record.ExceptionAsync(() => f.Extract()));

        Assert.Equal(old, File.ReadAllBytes(f.Output("payload.bin")));
        Assert.Equal(0, f.Audits);
        f.AssertOutputHandleReleased("payload.bin");
        f.AssertNoStagingAndReleasedArchive();
    }

    [Fact]
    public async Task ATargetCreatedAfterQuotaMeasurementIsNotSilentlyOverwritten()
    {
        using var f = await Fixture.Create(UserRole.Customer);
        var foreign = Bytes(32 * 1024, 0xD1);
        f.WriteArchive(("payload.bin", Bytes(64 * 1024, 0xD2)));
        f.BeforeUsage = _ => { f.WriteOutput("payload.bin", foreign); return Task.CompletedTask; };

        var error = Assert.IsType<AppException>(await Record.ExceptionAsync(() => f.Extract()));

        Assert.Equal("files.already_exists", error.MessageKey);
        Assert.Equal(409, error.HttpStatusCode);
        Assert.Equal(foreign, File.ReadAllBytes(f.Output("payload.bin")));
        Assert.Equal(0, f.Audits);
        f.AssertOutputHandleReleased("payload.bin");
        f.AssertNoStagingAndReleasedArchive();
    }

    [Fact]
    public async Task ARejectedLaterEntryKeepsTheEarlierVerifiedPublication()
    {
        using var f = await Fixture.Create(UserRole.Customer);
        var first = Bytes(64 * 1024, 0xE1);
        f.WriteArchive(("first.bin", first), ("second.bin", Bytes(MiB + 1, 0xE2)));
        f.WriteOutput("first.bin", Bytes(128 * 1024, 0xE3));
        f.FillUsage(MiB);

        AssertQuota(await Record.ExceptionAsync(() => f.Extract()));

        Assert.Equal(first, File.ReadAllBytes(f.Output("first.bin")));
        Assert.False(File.Exists(f.Output("second.bin")));
        Assert.Equal(MiB - 64 * 1024, f.UsageBytes());
        Assert.Equal(0, f.Audits);
        f.AssertNoStagingAndReleasedArchive();
    }

    [Fact]
    public async Task TwoSubMebibyteEntriesCannotExceedTheCombinedByteQuota()
    {
        using var f = await Fixture.Create(UserRole.Customer);
        var first = Bytes(700 * 1024, 0xF1);
        f.WriteArchive(("first.bin", first), ("second.bin", Bytes(700 * 1024, 0xF2)));
        var initialBytes = f.UsageBytes();
        Assert.InRange(initialBytes, 1, MiB - first.Length);

        AssertQuota(await Record.ExceptionAsync(() => f.Extract()));

        Assert.Equal(first, File.ReadAllBytes(f.Output("first.bin")));
        Assert.False(File.Exists(f.Output("second.bin")));
        Assert.Equal(initialBytes + first.Length, f.UsageBytes());
        Assert.InRange(f.UsageBytes(), 0, MiB);
        Assert.Equal(0, f.Audits);
        f.AssertNoStagingAndReleasedArchive();
    }

    [Fact]
    public async Task ExistingFractionalUsageIsIncludedBeforeTheFirstEntry()
    {
        using var f = await Fixture.Create(UserRole.Customer);
        f.WriteArchive(("payload.bin", Bytes(500 * 1024, 0x21)));
        f.FillUsage(600 * 1024);

        AssertQuota(await Record.ExceptionAsync(() => f.Extract()));

        Assert.False(File.Exists(f.Output("payload.bin")));
        Assert.Equal(600 * 1024, f.UsageBytes());
        Assert.Equal(0, f.Audits);
        f.AssertNoStagingAndReleasedArchive();
    }

    [Fact]
    public async Task ExactByteCapacityIsAvailableWithoutRoundingUpTheExistingUsage()
    {
        using var f = await Fixture.Create(UserRole.Customer);
        var accepted = Bytes(424 * 1024, 0x22);
        f.WriteArchive(("payload.bin", accepted));
        f.FillUsage(600 * 1024);

        await f.Extract();

        Assert.Equal(accepted, File.ReadAllBytes(f.Output("payload.bin")));
        Assert.Equal(MiB, f.UsageBytes());
        Assert.Equal(1, f.Audits);
        f.AssertNoStagingAndReleasedArchive();
    }

    [Fact]
    public async Task ShrinkingThenGrowingAnAliasCanUseTheExactRemainingCapacity()
    {
        using var f = await Fixture.Create(UserRole.Customer);
        var accepted = Bytes(836 * 1024, 0x23);
        f.WriteArchive(("payload.bin", Bytes(128 * 1024, 0x24)), ("PAYLOAD.bin", accepted));
        f.WriteOutput("payload.bin", Bytes(512 * 1024, 0x25));
        f.FillUsage(700 * 1024);

        await f.Extract();

        Assert.Equal(accepted, File.ReadAllBytes(f.Output("payload.bin")));
        Assert.Single(Directory.GetFiles(f.OutputRoot));
        Assert.Equal(MiB, f.UsageBytes());
        Assert.Equal(1, f.Audits);
        f.AssertNoStagingAndReleasedArchive();
    }

    private static byte[] Bytes(int length, byte value) => Enumerable.Repeat(value, length).ToArray();

    private static void AssertQuota(Exception? failure)
    {
        var error = Assert.IsType<AppException>(failure);
        Assert.Equal("files.quota_exceeded", error.MessageKey);
        Assert.Equal(403, error.HttpStatusCode);
    }

    private sealed class Fixture : IDisposable
    {
        private const string Prefix = "adapanel-extract-netquota-";
        private const string PasswordHash = "extract-netquota-fixture-hash";
        private readonly string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N")));
        private readonly string siteName = "extract-" + Guid.NewGuid().ToString("N") + ".test";
        private readonly Guid owner = Guid.NewGuid();
        private readonly Guid actor;
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        private readonly FilePublicationAuthorityTests.LocalGate gates = new();
        private readonly AdaPanelDbContext db;
        private readonly ServiceProvider services;
        private readonly CustomerScopeService scopes;
        private readonly FilesController controller;
        private string SiteRoot => Path.Combine(root, "site");
        private string Archive => Path.Combine(SiteRoot, "package.zip");
        public string OutputRoot => Path.Combine(SiteRoot, "output");
        public string VirtualOutput => $"sites/{siteName}/output";
        public int Audits { get; private set; }
        public Func<CancellationToken, Task>? BeforeUsage { get; set; }
        public Func<Task>? BeforeSiteRead { get; set; }

        private Fixture(UserRole role, bool child)
        {
            actor = role == UserRole.Customer && !child ? owner : Guid.NewGuid();
            Directory.CreateDirectory(OutputRoot);
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            db.Users.Add(User(owner, "owner", UserRole.Customer));
            if (actor != owner) db.Users.Add(User(actor, "actor", role));
            db.SaveChanges();
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(root, "panel") }));
            var site = new SiteDto { Id = 71, Name = siteName, PhysicalPath = SiteRoot,
                ApplicationRootPath = SiteRoot, ApplicationPoolName = siteName };
            var sites = Stub<IIisSiteService>((method, args) =>
            {
                if (method.Name != nameof(IIisSiteService.GetSiteAsync)) throw new NotSupportedException(method.Name);
                Assert.Equal(siteName, Assert.IsType<string>(args![0]));
                return ReadSite();
            });
            scopes = new CustomerScopeService(store, Stub<IServicePlanService>(),
                Stub<IResellerQuotaService>((method, _) => method.Name == nameof(IResellerQuotaService.GetQuotaAsync)
                    ? Task.FromResult<ResellerQuotaDto?>(null) : throw new NotSupportedException(method.Name)), sites);
            services = new ServiceCollection().AddSingleton(db).AddSingleton(new SessionRevocationStore(store))
                .AddSingleton<IIisSiteService>(sites).AddSingleton<IConcurrencyLock>(gates).BuildServiceProvider();
            var observedScopes = Stub<ICustomerScopeService>((method, args) => method.Name == nameof(ICustomerScopeService.GetDiskUsageMbAsync)
                ? ReadUsage((Guid)args![0]!, (CancellationToken)args[1]!) : method.Invoke(scopes, args));
            controller = new FilesController(new FileService(sites), observedScopes,
                Stub<IAuditService>((method, _) =>
                {
                    if (method.Name is not (nameof(IAuditService.WriteAsync) or nameof(IAuditService.WriteForSubscriptionAsync)))
                        throw new NotSupportedException(method.Name);
                    Audits++;
                    return Task.CompletedTask;
                }), new ConcurrentWorkGate())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services,
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, role.ToString()),
                        new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp(PasswordHash)),
                        new Claim(SessionRevocationStore.SessionIdClaim, SessionRevocationStore.NewSessionId())],
                        CookieAuthenticationDefaults.AuthenticationScheme)) } }
            };

            async Task<SiteDto> ReadSite()
            {
                if (BeforeSiteRead is { } before) await before();
                return site;
            }
        }

        public static async Task<Fixture> Create(UserRole role, bool child = false, int maxDiskMb = 1)
        {
            var f = new Fixture(role, child);
            try
            {
                await f.scopes.SetScopeAsync(f.owner, [f.siteName], 1, maxDiskMb, f.lifetime.Token);
                if (role == UserRole.Reseller) await f.scopes.SetOwnerAsync(f.owner, f.actor, f.lifetime.Token);
                if (child)
                {
                    await f.scopes.SetParentSubscriptionAsync(f.actor, f.owner, f.lifetime.Token);
                    await f.scopes.SetSubUserSectionsAsync(f.actor, ["files"], f.lifetime.Token);
                }
                return f;
            }
            catch { f.Dispose(); throw; }
        }

        private static PanelUser User(Guid id, string name, UserRole role) => new()
        { Id = id, UserName = name, Email = name + "@test.invalid", Role = role, IsActive = true, PasswordHash = PasswordHash };

        public string Output(string relative)
        {
            var path = Path.GetFullPath(Path.Combine(OutputRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            Assert.StartsWith(OutputRoot + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
            return path;
        }

        public void WriteOutput(string relative, byte[] bytes)
        {
            var path = Output(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        public void WriteArchive(params (string Name, byte[] Bytes)[] entries)
        {
            using var output = new FileStream(Archive, FileMode.CreateNew, FileAccess.Write);
            using var zip = new ZipArchive(output, ZipArchiveMode.Create);
            foreach (var item in entries)
            {
                using var entry = zip.CreateEntry(item.Name, CompressionLevel.Optimal).Open();
                entry.Write(item.Bytes);
            }
        }

        public long UsageBytes() => Directory.GetFiles(SiteRoot, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);

        private async Task<DiskUsage> ReadUsage(Guid user, CancellationToken token)
        {
            if (BeforeUsage is { } before) await before(token);
            return await scopes.GetDiskUsageMbAsync(user, token);
        }

        public void Cancel() => lifetime.Cancel();
        public void RemoveEmptyOutputDirectory() => Directory.Delete(OutputRoot, recursive: false);
        public bool HasStagedEntry() => Directory.GetFiles(SiteRoot, ".adapanel-write-*", SearchOption.AllDirectories).Length > 0;

        public void AssertOutputHandleReleased(string relative)
        {
            using var file = new FileStream(Output(relative), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(file.Length > 0);
        }

        public void FillUsage(long requested)
        {
            var present = UsageBytes();
            Assert.InRange(present, 0, requested);
            using var filler = new FileStream(Path.Combine(SiteRoot, "filler.bin"), FileMode.CreateNew, FileAccess.Write);
            filler.SetLength(requested - present);
            Assert.Equal(requested, UsageBytes());
        }

        public async Task<FileWorkspaceDto> Extract()
        {
            var response = await controller.ExtractAsync(new ArchiveFileRequest { Path = $"sites/{siteName}/package.zip",
                DestinationPath = VirtualOutput }, lifetime.Token);
            return Assert.IsType<ApiResponse<FileWorkspaceDto>>(Assert.IsType<OkObjectResult>(response.Result).Value).Data!;
        }

        public void AssertNoStagingAndReleasedArchive()
        {
            Assert.Empty(Directory.GetFileSystemEntries(SiteRoot, ".adapanel-*", SearchOption.AllDirectories));
            using var archive = new FileStream(Archive, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(archive.Length > 0);
        }

        public void Dispose()
        {
            services.Dispose(); db.Dispose(); connection.Dispose(); gates.Dispose(); lifetime.Dispose();
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            var name = Path.GetFileName(root);
            if (!string.Equals(Path.GetDirectoryName(root), temp, StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[Prefix.Length..], "N", out _))
                throw new InvalidOperationException("Extraction fixture cleanup escaped its unique owned temporary root.");
            if (!Directory.Exists(root)) return;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var path))
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidOperationException("Extraction fixture cleanup must not follow a reparse point.");
                if (!attributes.HasFlag(FileAttributes.Directory)) continue;
                foreach (var entry in Directory.GetFileSystemEntries(path))
                {
                    var full = Path.GetFullPath(entry);
                    if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Extraction fixture cleanup escaped its owned temporary root.");
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
