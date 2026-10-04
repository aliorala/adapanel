using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
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

public sealed class FileUploadQuotaSnapshotTests
{
    private const int KiB = 1024;
    private const int MiB = 1024 * KiB;

    [Theory]
    [InlineData(UserRole.Customer, "renamed")]
    [InlineData(UserRole.Customer, "replaced")]
    [InlineData(UserRole.Customer, "shrunk")]
    [InlineData(UserRole.Reseller, "renamed")]
    [InlineData(UserRole.Reseller, "replaced")]
    [InlineData(UserRole.Reseller, "shrunk")]
    public async Task QuotaCannotDeductThePreviousTargetAfterItsBytesWereKeptElsewhere(UserRole role, string change)
    {
        using var f = await Fixture.Create(role);
        var original = Bytes(512 * KiB, 0x41);
        var incoming = Bytes(original.Length, 0x42);
        f.WriteTarget(original);
        f.FillUsage(MiB - 128 * KiB);
        var reached = 0;
        var protectedTarget = false;
        byte[]? targetAfterChange = null;
        f.BeforeUsage = async token =>
        {
            reached++;
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            protectedTarget = !f.TryPreserveOldBytesAndChangeTarget(change, original);
            targetAfterChange = File.Exists(f.Target) ? File.ReadAllBytes(f.Target) : null;
            Assert.True(f.UsageBytes() <= MiB, "The competing change itself must stay within quota.");
        };
        using var body = new MemoryStream(incoming);

        var failure = await Record.ExceptionAsync(() => f.Upload(new FormFile(body, 0, incoming.Length, "file", "target.bin")));

        Assert.Equal(1, reached);
        if (protectedTarget)
        {
            // Holding the deducted target open is a valid solution: the competing writer
            // cannot change it, and this upload can use its still-correct overwrite delta.
            Assert.Null(failure);
            Assert.Equal(incoming, File.ReadAllBytes(f.Target));
            Assert.Single(f.Audits);
        }
        else
        {
            var rejected = Assert.IsType<AppException>(failure);
            Assert.True(rejected.HttpStatusCode is 403 or 409);
            if (targetAfterChange is null) Assert.False(File.Exists(f.Target));
            else Assert.Equal(targetAfterChange, File.ReadAllBytes(f.Target));
            Assert.Empty(f.Audits);
        }
        Assert.True(f.UsageBytes() <= MiB, "Old destination bytes must not become an allowance for a different destination.");
        f.AssertCleanAndTargetReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyEofFromAnActualFormFileDoesNotPublishTruncatedContent(bool existing)
    {
        using var f = await Fixture.Create(UserRole.Customer);
        var original = Bytes(1024, 0x43);
        if (existing) f.WriteTarget(original);
        using var body = new MemoryStream(Bytes(128, 0x44));
        // FormFile bounds reads above Length, but an underlying early EOF can still be short.
        var form = new FormFile(body, 0, 256, "file", "target.bin");

        Assert.NotNull(await Record.ExceptionAsync(() => f.Upload(form)));

        if (existing) Assert.Equal(original, File.ReadAllBytes(f.Target));
        else Assert.False(File.Exists(f.Target));
        Assert.Empty(f.Audits);
        f.AssertCleanAndTargetReleased();
    }

    [Fact]
    public async Task ACustomFormFileCannotPublishMoreBytesThanItsApprovedLength()
    {
        using var f = await Fixture.Create(UserRole.Customer);
        f.FillUsage(MiB - 128);
        using var input = new GeneratedStream(4096);
        var form = new ContractUpload(input, reportedLength: 128);

        Assert.NotNull(await Record.ExceptionAsync(() => f.Upload(form)));

        Assert.False(File.Exists(f.Target));
        Assert.Empty(f.Audits);
        Assert.True(input.Disposed);
        f.AssertCleanAndTargetReleased();
    }

    [Fact]
    public async Task ActualUploadByteLimitAlsoAppliesToACustomUnboundedFormStream()
    {
        using var f = await Fixture.Create(UserRole.Administrator);
        f.WriteTarget(Bytes(32, 0x45));
        var original = File.ReadAllBytes(f.Target);
        // Contract hardening, not a normal multipart-parser bypass: FormFile normally caps
        // its own read stream. This non-seekable implementation exercises the service limit.
        using var input = new GeneratedStream(PanelServerLimits.FileUploadMaxBytes + 1);

        Assert.NotNull(await Record.ExceptionAsync(() => f.Upload(new ContractUpload(input, 1))));

        Assert.Equal(original, File.ReadAllBytes(f.Target));
        Assert.Empty(f.Audits);
        Assert.True(input.Disposed);
        f.AssertCleanAndTargetReleased();
    }

    [Fact]
    public async Task ADeclaredUploadAboveTheLimitDoesNotOpenOrPublishItsStream()
    {
        using var f = await Fixture.Create(UserRole.Administrator);
        using var input = new GeneratedStream(16);
        var form = new ContractUpload(input, PanelServerLimits.FileUploadMaxBytes + 1);

        Assert.NotNull(await Record.ExceptionAsync(() => f.Upload(form)));

        Assert.Equal(0, form.OpenCount);
        Assert.False(File.Exists(f.Target));
        Assert.Empty(f.Audits);
        f.AssertCleanAndTargetReleased();
    }

    [Fact]
    public async Task ActualFormFileKeepsBytesOutsideItsDeclaredSliceOutOfTheUpload()
    {
        using var f = await Fixture.Create(UserRole.Customer);
        using var body = new MemoryStream(Bytes(512, 0x46));

        await f.Upload(new FormFile(body, 0, 128, "file", "target.bin"));

        Assert.Equal(Bytes(128, 0x46), File.ReadAllBytes(f.Target));
        Assert.Single(f.Audits);
        f.AssertCleanAndTargetReleased();
    }

    [Theory]
    [InlineData(UserRole.Customer, false, 128 * KiB)]
    [InlineData(UserRole.Customer, true, 256 * KiB)]
    [InlineData(UserRole.Reseller, false, 256 * KiB)]
    public async Task AFullSubscriptionCanKeepOrReduceAnUnchangedTarget(UserRole role, bool child, int length)
    {
        using var f = await Fixture.Create(role, child);
        f.WriteTarget(Bytes(256 * KiB, 0x47));
        f.FillUsage(MiB);
        var incoming = Bytes(length, 0x48);
        using var body = new MemoryStream(incoming);

        await f.Upload(new FormFile(body, 0, incoming.Length, "file", "target.bin"));

        Assert.Equal(incoming, File.ReadAllBytes(f.Target));
        Assert.Equal(MiB - 256 * KiB + incoming.Length, f.UsageBytes());
        Assert.Single(f.Audits);
        f.AssertCleanAndTargetReleased();
    }

    [Theory]
    [InlineData(4096, true)]
    [InlineData(4097, false)]
    public async Task NewUploadsRespectTheExactRemainingByteAllowance(int length, bool allowed)
    {
        using var f = await Fixture.Create(UserRole.Customer);
        f.FillUsage(MiB - 4096);
        var incoming = Bytes(length, 0x49);
        using var body = new MemoryStream(incoming);

        var failure = await Record.ExceptionAsync(() => f.Upload(new FormFile(body, 0, length, "file", "target.bin")));

        if (allowed)
        {
            Assert.Null(failure);
            Assert.Equal(incoming, File.ReadAllBytes(f.Target));
            Assert.Equal(MiB, f.UsageBytes());
            Assert.Single(f.Audits);
        }
        else
        {
            var quota = Assert.IsType<AppException>(failure);
            Assert.Equal("files.quota_exceeded", quota.MessageKey);
            Assert.False(File.Exists(f.Target));
            Assert.Empty(f.Audits);
        }
        f.AssertCleanAndTargetReleased();
    }

    [Fact]
    public async Task AdministratorStillHasTheSubscriptionQuotaExemption()
    {
        using var f = await Fixture.Create(UserRole.Administrator);
        f.FillUsage(2 * MiB);
        using var body = new MemoryStream(Bytes(1024, 0x50));

        await f.Upload(new FormFile(body, 0, body.Length, "file", "target.bin"));

        Assert.Equal(2 * MiB + 1024, f.UsageBytes());
        Assert.Single(f.Audits);
        f.AssertCleanAndTargetReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedStreamPreservesTheExistingFileAndDoesNotAuditSuccess(bool cancellation)
    {
        using var f = await Fixture.Create(UserRole.Customer);
        var original = Bytes(4096, 0x51);
        f.WriteTarget(original);
        using var body = new InterruptedStream(Bytes(2048, 0x52), cancellation ? f.Cancel : null);

        var failure = await Record.ExceptionAsync(() => f.Upload(new FormFile(body, 0, body.Length, "file", "target.bin")));

        if (cancellation) Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else Assert.IsAssignableFrom<IOException>(failure);
        Assert.Equal(original, File.ReadAllBytes(f.Target));
        Assert.Empty(f.Audits);
        f.AssertCleanAndTargetReleased();
    }

    [Fact]
    public async Task CancellationDuringQuotaLeavesThePreviousTargetUntouched()
    {
        using var f = await Fixture.Create(UserRole.Customer);
        var original = Bytes(2048, 0x53);
        f.WriteTarget(original);
        f.BeforeUsage = _ => { f.Cancel(); return Task.CompletedTask; };
        using var body = new MemoryStream(Bytes(1024, 0x54));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Upload(new FormFile(body, 0, body.Length, "file", "target.bin")));

        Assert.Equal(original, File.ReadAllBytes(f.Target));
        Assert.Empty(f.Audits);
        f.AssertCleanAndTargetReleased();
    }

    [Fact]
    public async Task AFailedQuotaReadIsNotReportedAsAnUploadSuccess()
    {
        using var f = await Fixture.Create(UserRole.Customer);
        var original = Bytes(2048, 0x55);
        f.WriteTarget(original);
        var expected = new IOException("Fixture quota read failed.");
        f.BeforeUsage = _ => Task.FromException(expected);
        using var body = new MemoryStream(Bytes(1024, 0x56));

        Assert.Same(expected, await Record.ExceptionAsync(() => f.Upload(new FormFile(body, 0, body.Length, "file", "target.bin"))));

        Assert.Equal(original, File.ReadAllBytes(f.Target));
        Assert.Empty(f.Audits);
        f.AssertCleanAndTargetReleased();
    }

    [Fact]
    public async Task AnAlreadyCanceledUploadDoesNotOpenItsInput()
    {
        using var f = await Fixture.Create(UserRole.Customer);
        f.Cancel();
        using var input = new GeneratedStream(16);
        var form = new ContractUpload(input, 16);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Upload(form));

        Assert.Equal(0, form.OpenCount);
        Assert.False(File.Exists(f.Target));
        Assert.Empty(f.Audits);
        f.AssertCleanAndTargetReleased();
    }

    [Theory]
    [InlineData("replaced")]
    [InlineData("shrunk")]
    public async Task TheDeductedTargetStaysProtectedDuringTheFinalPublicationApproval(string change)
    {
        using var f = await Fixture.Create(UserRole.Customer);
        var original = Bytes(512 * KiB, 0x61);
        var incoming = Bytes(original.Length, 0x62);
        f.WriteTarget(original);
        f.FillUsage(MiB);
        var reached = 0;
        f.BeforePublishedFileApproval = async token =>
        {
            reached++;
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            Assert.False(f.TryPreserveOldBytesAndChangeTarget(change, original));
        };
        using var body = new MemoryStream(incoming);

        await f.Upload(new FormFile(body, 0, body.Length, "file", "target.bin"));

        Assert.Equal(1, reached);
        Assert.Equal(incoming, File.ReadAllBytes(f.Target));
        Assert.Equal(MiB, f.UsageBytes());
        Assert.Single(f.Audits);
        f.AssertCleanAndTargetReleased();
    }

    [Theory]
    [InlineData(UserRole.Customer)]
    [InlineData(UserRole.Reseller)]
    public async Task AnInitiallyMissingTargetCreatedDuringQuotaIsNotOverwritten(UserRole role)
    {
        using var f = await Fixture.Create(role);
        var competing = Bytes(512, 0x63);
        f.BeforeUsage = async token =>
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            f.AssertStagedPayload(1024);
            f.WriteTarget(competing);
        };
        using var body = new MemoryStream(Bytes(1024, 0x64));

        var failure = await Assert.ThrowsAsync<AppException>(() =>
            f.Upload(new FormFile(body, 0, body.Length, "file", "target.bin")));

        Assert.Equal("files.already_exists", failure.MessageKey);
        Assert.Equal(409, failure.HttpStatusCode);
        Assert.Equal(competing, File.ReadAllBytes(f.Target));
        Assert.Empty(f.Audits);
        f.AssertCleanAndTargetReleased();
    }

    private static byte[] Bytes(int length, byte value) => Enumerable.Repeat(value, length).ToArray();

    private sealed class Fixture : IDisposable
    {
        private const string Prefix = "adapanel-upload-quota-";
        private const string PasswordHash = "upload-quota-fixture-hash";
        private readonly string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N")));
        private readonly string siteName = "upload-" + Guid.NewGuid().ToString("N") + ".test";
        private readonly Guid owner = Guid.NewGuid();
        private readonly Guid actor;
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(60));
        private readonly FilePublicationAuthorityTests.LocalGate gates = new();
        private readonly AdaPanelDbContext db;
        private readonly ServiceProvider services;
        private readonly CustomerScopeService scopes;
        private readonly FilesController controller;
        private readonly List<string> stagingPaths = [];
        private string SiteRoot => Path.Combine(root, "site");
        public string Target => Path.Combine(SiteRoot, "target.bin");
        public List<string> Audits { get; } = [];
        public Func<CancellationToken, Task>? BeforeUsage { get; set; }
        public Func<CancellationToken, Task>? BeforePublishedFileApproval { get; set; }

        private Fixture(UserRole role, bool child)
        {
            actor = role == UserRole.Customer && !child ? owner : Guid.NewGuid();
            Directory.CreateDirectory(SiteRoot);
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            db.Users.Add(User(owner, "owner", UserRole.Customer));
            if (actor != owner) db.Users.Add(User(actor, "actor", role));
            db.SaveChanges();
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(root, "panel") }));
            var sites = Stub<IIisSiteService>((method, args) =>
            {
                if (method.Name != nameof(IIisSiteService.GetSiteAsync)) throw new NotSupportedException(method.Name);
                Assert.Equal(siteName, Assert.IsType<string>(args![0]));
                return ReadSite((CancellationToken)args[1]!);
            });
            scopes = new CustomerScopeService(store, Stub<IServicePlanService>(),
                Stub<IResellerQuotaService>((method, _) => method.Name == nameof(IResellerQuotaService.GetQuotaAsync)
                    ? Task.FromResult<ResellerQuotaDto?>(null) : throw new NotSupportedException(method.Name)), sites);
            services = new ServiceCollection().AddSingleton(db).AddSingleton(new SessionRevocationStore(store))
                .AddSingleton<IIisSiteService>(sites).AddSingleton<IConcurrencyLock>(gates).BuildServiceProvider();
            var observed = Stub<ICustomerScopeService>((method, args) => method.Name == nameof(ICustomerScopeService.GetDiskUsageMbAsync)
                ? ReadUsage((Guid)args![0]!, (CancellationToken)args[1]!) : method.Invoke(scopes, args));
            var files = new FileService(sites)
            {
                UploadStagingPath = () =>
                {
                    var path = Path.Combine(root, ".adapanel-upload-" + Guid.NewGuid().ToString("N") + ".tmp");
                    stagingPaths.Add(path);
                    return path;
                }
            };
            controller = new FilesController(files, observed,
                Stub<IAuditService>((method, args) =>
                {
                    if (method.Name is not (nameof(IAuditService.WriteAsync) or nameof(IAuditService.WriteForSubscriptionAsync)))
                        throw new NotSupportedException(method.Name);
                    Assert.Equal("files.uploaded", args![0]);
                    Audits.Add((string)args[2]!);
                    return Task.CompletedTask;
                }), new ConcurrentWorkGate())
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

        public static async Task<Fixture> Create(UserRole role, bool child = false)
        {
            var f = new Fixture(role, child);
            try
            {
                await f.scopes.SetScopeAsync(f.owner, [f.siteName], 1, 1, f.lifetime.Token);
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

        private async Task<SiteDto> ReadSite(CancellationToken token)
        {
            if (BeforePublishedFileApproval is { } hook &&
                Directory.GetFiles(SiteRoot, ".adapanel-write-*").Length != 0)
            {
                BeforePublishedFileApproval = null;
                await hook(token);
            }
            return new SiteDto { Id = 49, Name = siteName, PhysicalPath = SiteRoot,
                ApplicationRootPath = SiteRoot, ApplicationPoolName = siteName };
        }

        private async Task<DiskUsage> ReadUsage(Guid user, CancellationToken token)
        {
            var hook = BeforeUsage;
            BeforeUsage = null;
            if (hook is not null) await hook(token);
            return await scopes.GetDiskUsageMbAsync(user, token);
        }

        public void WriteTarget(byte[] bytes) => File.WriteAllBytes(Target, bytes);
        public long UsageBytes() => Directory.GetFiles(SiteRoot, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        public void FillUsage(long desired)
        {
            var present = UsageBytes();
            Assert.InRange(present, 0, desired);
            using var filler = new FileStream(Path.Combine(SiteRoot, "filler.bin"), FileMode.CreateNew, FileAccess.Write);
            filler.SetLength(desired - present);
        }

        public bool TryPreserveOldBytesAndChangeTarget(string change, byte[] original)
        {
            var kept = Path.Combine(SiteRoot, "kept.bin");
            try
            {
                if (change == "shrunk")
                {
                    // Acquire write access before creating the retained copy. A protected
                    // target must leave both paths untouched when the writer is refused.
                    using var target = new FileStream(Target, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                    File.WriteAllBytes(kept, original[(128 * KiB)..]);
                    target.SetLength(128 * KiB);
                    target.Position = 0;
                    target.Write(Bytes(128 * KiB, 0x57));
                }
                else
                {
                    File.Move(Target, kept);
                    if (change == "replaced") File.WriteAllBytes(Target, Bytes(128 * KiB, 0x58));
                }
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Assert.False(File.Exists(kept));
                Assert.Equal(original, File.ReadAllBytes(Target));
                return false;
            }
        }

        public async Task Upload(IFormFile file)
        {
            var response = await controller.UploadAsync($"sites/{siteName}", file, lifetime.Token);
            var envelope = Assert.IsType<ApiResponse<object>>(Assert.IsType<OkObjectResult>(response.Result).Value);
            Assert.NotNull(envelope.Data);
        }
        public void Cancel() => lifetime.Cancel();
        public void AssertStagedPayload(long expectedBytes)
        {
            var path = Assert.Single(stagingPaths);
            Assert.Equal(root, Path.GetDirectoryName(path));
            Assert.Equal(expectedBytes, new FileInfo(path).Length);
            Assert.Empty(Directory.GetFiles(SiteRoot, ".adapanel-*"));
        }
        public void AssertCleanAndTargetReleased()
        {
            Assert.All(stagingPaths, path => Assert.False(File.Exists(path)));
            Assert.Empty(Directory.GetFileSystemEntries(root, ".adapanel-*", SearchOption.AllDirectories));
            if (File.Exists(Target))
            {
                using var target = new FileStream(Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.True(target.CanWrite);
            }
        }
        public void Dispose()
        {
            services.Dispose(); db.Dispose(); connection.Dispose(); gates.Dispose(); lifetime.Dispose();
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            var name = Path.GetFileName(root);
            if (!string.Equals(Path.GetDirectoryName(root), temp, StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[Prefix.Length..], "N", out _))
                throw new InvalidOperationException("Upload fixture cleanup must stay inside its uniquely owned temporary root.");
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

    private sealed class ContractUpload(Stream input, long reportedLength) : IFormFile
    {
        public string ContentType => "application/octet-stream";
        public string ContentDisposition => "form-data";
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public long Length => reportedLength;
        public string Name => "file";
        public string FileName => "target.bin";
        public int OpenCount { get; private set; }
        public Stream OpenReadStream() { OpenCount++; return input; }
        public void CopyTo(Stream target) => input.CopyTo(target);
        public Task CopyToAsync(Stream target, CancellationToken token = default) => input.CopyToAsync(target, token);
    }

    private sealed class GeneratedStream(long length) : Stream
    {
        private long position;
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, length - position);
            buffer.AsSpan(offset, read).Fill(0x59);
            position += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var read = (int)Math.Min(buffer.Length, length - position);
            buffer.Span[..read].Fill(0x59);
            position += read;
            return ValueTask.FromResult(read);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class InterruptedStream(byte[] bytes, Action? cancel) : MemoryStream(bytes)
    {
        private bool read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!read) { read = true; return base.ReadAsync(buffer[..Math.Min(64, buffer.Length)], token); }
            if (cancel is not null) { cancel(); token.ThrowIfCancellationRequested(); }
            throw new IOException("Fixture upload stream ended with an IO error.");
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
