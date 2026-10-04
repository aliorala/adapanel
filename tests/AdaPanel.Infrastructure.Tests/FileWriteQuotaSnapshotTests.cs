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
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FileWriteQuotaSnapshotTests
{
    private const int MiB = 1024 * 1024;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheDeductedOriginalCannotShrinkDuringQuotaAndFundAnotherFile(bool replace)
    {
        using var f = await Fixture.Create();
        var original = Encoding.UTF8.GetBytes(new string('A', 128 * 1024));
        var content = new string('B', original.Length);
        f.WriteOriginal(original);
        f.FillUsage(MiB);
        var attempted = false;
        var changed = false;
        f.BeforeUsage = async token =>
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            attempted = true;
            changed = f.TryReplaceOriginal([], replace);
            if (changed) f.WriteAdditional("concurrent.bin", new byte[original.Length]);
        };

        var failure = await Record.ExceptionAsync(() => f.Write(content));

        Assert.True(attempted);
        Assert.InRange(f.UsageBytes(), 0, MiB);
        if (changed)
        {
            Assert.NotNull(failure);
            Assert.Empty(File.ReadAllBytes(f.Target));
            Assert.Equal(0, f.Audits);
        }
        else
        {
            Assert.Null(failure);
            Assert.Equal(Encoding.UTF8.GetBytes(content), File.ReadAllBytes(f.Target));
            Assert.Equal(1, f.Audits);
        }
        f.AssertCleanAndReleased();
    }

    [Theory]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf32-be")]
    public async Task AReplacementDuringQuotaCannotBeOverwrittenUsingThePreviousFilesCodec(string codecName)
    {
        using var f = await Fixture.Create();
        var codec = Codec(codecName);
        var before = Encoded(codec, "original text");
        var accepted = Encoded(codec, "updated text!");
        var foreign = Encoding.UTF8.GetBytes(new string('F', before.Length));
        f.WriteOriginal(before);
        var attempted = false;
        var changed = false;
        f.BeforeUsage = _ =>
        {
            attempted = true;
            changed = f.TryReplaceOriginal(foreign, replace: true);
            return Task.CompletedTask;
        };

        var failure = await Record.ExceptionAsync(() => f.Write("updated text!"));

        Assert.True(attempted);
        if (changed)
        {
            Assert.NotNull(failure);
            Assert.Equal(foreign, File.ReadAllBytes(f.Target));
            Assert.Equal(0, f.Audits);
        }
        else
        {
            Assert.Null(failure);
            Assert.Equal(accepted, File.ReadAllBytes(f.Target));
            Assert.Equal(1, f.Audits);
        }
        f.AssertCleanAndReleased();
    }

    [Fact]
    public async Task TheDeductedOriginalRemainsProtectedUntilTheFinalAuthorityAwait()
    {
        using var f = await Fixture.Create();
        var content = new string('B', 128 * 1024);
        f.WriteOriginal(Encoding.UTF8.GetBytes(new string('A', content.Length)));
        f.FillUsage(MiB);
        var attempted = false;
        var changed = false;
        f.BeforeSiteRead = async () =>
        {
            if (attempted || !f.HasStaging()) return;
            await Task.Yield();
            attempted = true;
            changed = f.TryReplaceOriginal([], replace: false);
            if (changed) f.WriteAdditional("concurrent.bin", new byte[content.Length]);
        };

        var failure = await Record.ExceptionAsync(() => f.Write(content));

        Assert.True(attempted);
        Assert.InRange(f.UsageBytes(), 0, MiB);
        if (changed)
        {
            Assert.NotNull(failure);
            Assert.Empty(File.ReadAllBytes(f.Target));
            Assert.Equal(0, f.Audits);
        }
        else
        {
            Assert.Null(failure);
            Assert.Equal(Encoding.UTF8.GetBytes(content), File.ReadAllBytes(f.Target));
            Assert.Equal(1, f.Audits);
        }
        f.AssertCleanAndReleased();
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    [InlineData("utf32-le")]
    [InlineData("utf32-be")]
    public async Task AnUnchangedTargetRetainsItsEncodingAndPreamble(string codecName)
    {
        using var f = await Fixture.Create();
        var codec = Codec(codecName);
        f.WriteOriginal(Encoded(codec, "Türkçe 文本\nfirst"));
        const string content = "Türkçe 文本\nlast!";

        var result = await f.Write(content);

        Assert.Equal(Encoded(codec, content), File.ReadAllBytes(f.Target));
        Assert.Equal(content, result.Content);
        Assert.True(result.ContentLoaded);
        Assert.Empty(result.PhysicalPath);
        Assert.Equal(1, f.Audits);
        f.AssertCleanAndReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANewTargetCreatedDuringQuotaKeepsItsOwnBytes(bool failIfExists)
    {
        using var f = await Fixture.Create();
        var foreign = Encoding.UTF8.GetBytes("another request's file");
        f.BeforeUsage = _ => { f.WriteOriginal(foreign); return Task.CompletedTask; };

        var error = Assert.IsType<AppException>(await Record.ExceptionAsync(() => f.Write("accepted content", failIfExists)));

        Assert.Equal("files.already_exists", error.MessageKey);
        Assert.Equal(409, error.HttpStatusCode);
        Assert.Equal(foreign, File.ReadAllBytes(f.Target));
        Assert.Equal(0, f.Audits);
        f.AssertCleanAndReleased();
    }

    [Fact]
    public async Task ANewUtf8FileCanUseTheExactRemainingByteCapacity()
    {
        using var f = await Fixture.Create();
        const string content = "new Türkçe 文本";
        var expected = Encoding.UTF8.GetBytes(content);
        f.FillUsage(MiB - expected.Length);

        var result = await f.Write(content);

        Assert.Equal(expected, File.ReadAllBytes(f.Target));
        Assert.Equal(MiB, f.UsageBytes());
        Assert.True(result.ContentLoaded);
        Assert.Equal(1, f.Audits);
        f.AssertCleanAndReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringQuotaPublishesNothingAndReleasesTheOriginal(bool exists)
    {
        using var f = await Fixture.Create();
        var original = Encoded(Codec("utf16-be"), "keep this text");
        if (exists) f.WriteOriginal(original);
        f.BeforeUsage = _ => { f.Cancel(); return Task.CompletedTask; };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Write("cancelled text"));

        if (exists) Assert.Equal(original, File.ReadAllBytes(f.Target));
        else Assert.False(File.Exists(f.Target));
        Assert.Equal(0, f.Audits);
        f.AssertCleanAndReleased();
    }

    [Fact]
    public async Task ALockedOriginalCannotBeTreatedAsAnEmptyNewFile()
    {
        using var f = await Fixture.Create();
        var original = Encoded(Codec("utf32-le"), "locked original");
        f.WriteOriginal(original);
        Exception? failure;
        using (var held = new FileStream(f.Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            failure = await Record.ExceptionAsync(() => f.Write("new text"));

        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(original, File.ReadAllBytes(f.Target));
        Assert.Equal(0, f.UsageReads);
        Assert.Equal(0, f.Audits);
        f.AssertCleanAndReleased();
    }

    [Fact]
    public async Task AQuotaReadErrorKeepsTheOriginalBytesAndException()
    {
        using var f = await Fixture.Create();
        var original = Encoded(Codec("utf8-bom"), "original");
        f.WriteOriginal(original);
        var failure = new IOException("Fixture quota measurement failed.");
        f.BeforeUsage = _ => Task.FromException(failure);

        Assert.Same(failure, await Record.ExceptionAsync(() => f.Write("changed")));

        Assert.Equal(original, File.ReadAllBytes(f.Target));
        Assert.Equal(0, f.Audits);
        f.AssertCleanAndReleased();
    }

    [Fact]
    public async Task EncodedGrowthBeyondTheRemainingQuotaKeepsTheOriginal()
    {
        using var f = await Fixture.Create();
        var codec = Codec("utf32-be");
        var original = Encoded(codec, "A");
        f.WriteOriginal(original);
        f.FillUsage(MiB);

        var error = Assert.IsType<AppException>(await Record.ExceptionAsync(() => f.Write("AB")));

        Assert.Equal("files.quota_exceeded", error.MessageKey);
        Assert.Equal(original, File.ReadAllBytes(f.Target));
        Assert.Equal(MiB, f.UsageBytes());
        Assert.Equal(0, f.Audits);
        f.AssertCleanAndReleased();
    }

    private static Encoding Codec(string name) => name switch
    {
        "utf8" => new UTF8Encoding(false, true),
        "utf8-bom" => new UTF8Encoding(true, true),
        "utf16-le" => new UnicodeEncoding(false, true, true),
        "utf16-be" => new UnicodeEncoding(true, true, true),
        "utf32-le" => new UTF32Encoding(false, true, true),
        "utf32-be" => new UTF32Encoding(true, true, true),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static byte[] Encoded(Encoding codec, string text) => [.. codec.GetPreamble(), .. codec.GetBytes(text)];

    private sealed class Fixture : IDisposable
    {
        private const string Prefix = "adapanel-write-snapshot-";
        private const string PasswordHash = "write-quota-fixture-hash";
        private readonly string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N")));
        private readonly string name = "write-" + Guid.NewGuid().ToString("N") + ".test";
        private readonly Guid owner = Guid.NewGuid();
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        private readonly FilePublicationAuthorityTests.LocalGate gates = new();
        private readonly AdaPanelDbContext db;
        private readonly ServiceProvider services;
        private readonly CustomerScopeService scopes;
        private readonly FilesController controller;
        private string SiteRoot => Path.Combine(root, "site");
        public string Target => Path.Combine(SiteRoot, "content.txt");
        public Func<CancellationToken, Task>? BeforeUsage;
        public Func<Task>? BeforeSiteRead;
        public int Audits, UsageReads;

        private Fixture()
        {
            Directory.CreateDirectory(SiteRoot);
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            db.Users.Add(new PanelUser { Id = owner, UserName = "writer", Email = "writer@test.invalid",
                Role = UserRole.Customer, IsActive = true, PasswordHash = PasswordHash });
            db.SaveChanges();
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(root, "panel") }));
            var site = new SiteDto { Id = 51, Name = name, PhysicalPath = SiteRoot, ApplicationRootPath = SiteRoot, ApplicationPoolName = name };
            var sites = Stub<IIisSiteService>((method, args) =>
            {
                if (method.Name != nameof(IIisSiteService.GetSiteAsync)) throw new NotSupportedException(method.Name);
                Assert.Equal(name, args![0]);
                return ReadSite();
            });
            scopes = new CustomerScopeService(store, Stub<IServicePlanService>(),
                Stub<IResellerQuotaService>((method, _) => method.Name == nameof(IResellerQuotaService.GetQuotaAsync)
                    ? Task.FromResult<ResellerQuotaDto?>(null) : throw new NotSupportedException(method.Name)), sites);
            var observedScopes = Stub<ICustomerScopeService>((method, args) => method.Name == nameof(ICustomerScopeService.GetDiskUsageMbAsync)
                ? ReadUsage((Guid)args![0]!, (CancellationToken)args[1]!) : method.Invoke(scopes, args));
            services = new ServiceCollection().AddSingleton(db).AddSingleton(new SessionRevocationStore(store))
                .AddSingleton<IIisSiteService>(sites).AddSingleton<IConcurrencyLock>(gates).BuildServiceProvider();
            controller = new FilesController(new FileService(sites), observedScopes,
                Stub<IAuditService>((method, _) =>
                {
                    if (method.Name is not (nameof(IAuditService.WriteAsync) or nameof(IAuditService.WriteForSubscriptionAsync)))
                        throw new NotSupportedException(method.Name);
                    Audits++; return Task.CompletedTask;
                }), new ConcurrentWorkGate())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services,
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, owner.ToString()), new Claim(ClaimTypes.Role, nameof(UserRole.Customer)),
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

        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            try { await f.scopes.SetScopeAsync(f.owner, [f.name], 1, 1, f.lifetime.Token); return f; }
            catch { f.Dispose(); throw; }
        }

        public void WriteOriginal(byte[] bytes) => File.WriteAllBytes(Target, bytes);
        public void WriteAdditional(string name, byte[] bytes)
        {
            Assert.Equal(name, Path.GetFileName(name));
            File.WriteAllBytes(Path.Combine(SiteRoot, name), bytes);
        }

        public bool TryReplaceOriginal(byte[] bytes, bool replace)
        {
            var replacement = Path.Combine(root, "replacement.txt");
            if (replace) File.WriteAllBytes(replacement, bytes);
            try
            {
                if (replace) File.Move(replacement, Target, overwrite: true);
                else File.WriteAllBytes(Target, bytes);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }

        private async Task<DiskUsage> ReadUsage(Guid user, CancellationToken token)
        {
            UsageReads++;
            var before = BeforeUsage;
            BeforeUsage = null;
            if (before is not null) await before(token);
            return await scopes.GetDiskUsageMbAsync(user, token);
        }

        public long UsageBytes() => Directory.GetFiles(SiteRoot, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        public void FillUsage(long bytes)
        {
            var present = UsageBytes();
            Assert.InRange(present, 0, bytes);
            using var filler = new FileStream(Path.Combine(SiteRoot, "filler.bin"), FileMode.CreateNew, FileAccess.Write);
            filler.SetLength(bytes - present);
        }

        public async Task<FileWorkspaceDto> Write(string content, bool failIfExists = false)
        {
            var result = await controller.WriteAsync(new WriteFileRequest { Path = $"sites/{name}/content.txt",
                Content = content, FailIfExists = failIfExists }, lifetime.Token);
            return Assert.IsType<ApiResponse<FileWorkspaceDto>>(Assert.IsType<OkObjectResult>(result.Result).Value).Data!;
        }

        public void Cancel() => lifetime.Cancel();
        public bool HasStaging() => Directory.GetFiles(SiteRoot, ".adapanel-write-*", SearchOption.AllDirectories).Length > 0;
        public void AssertCleanAndReleased()
        {
            Assert.False(HasStaging());
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
            var basename = Path.GetFileName(root);
            if (!string.Equals(Path.GetDirectoryName(root), temp, StringComparison.OrdinalIgnoreCase) ||
                !basename.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(basename[Prefix.Length..], "N", out _))
                throw new InvalidOperationException("Write fixture cleanup must remain inside its unique owned temporary root.");
            if (!Directory.Exists(root)) return;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var path))
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidOperationException("Write fixture cleanup must not follow a reparse point.");
                if (!attributes.HasFlag(FileAttributes.Directory)) continue;
                foreach (var entry in Directory.GetFileSystemEntries(path))
                {
                    var full = Path.GetFullPath(entry);
                    if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Write fixture cleanup escaped its owned temporary root.");
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
