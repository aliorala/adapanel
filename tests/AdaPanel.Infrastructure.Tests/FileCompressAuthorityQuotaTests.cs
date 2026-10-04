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

public sealed class FileCompressAuthorityQuotaTests
{
    [Theory]
    [InlineData(UserRole.Customer)]
    [InlineData(UserRole.Reseller)]
    [InlineData(UserRole.Administrator)]
    public async Task ACompletedArchiveHasTheExpectedScopedWorkspace(UserRole role)
    {
        using var fixture = new Fixture(role);
        var result = await fixture.Compress();
        Assert.Equal(role == UserRole.Administrator ? fixture.SiteRoot : string.Empty, result.PhysicalPath);
        Assert.Equal(fixture.VirtualRoot, result.Path);
        Assert.Equal("SOURCE CONTENT", fixture.ReadArchiveContent());
        Assert.Equal(1, fixture.Audits);
        fixture.AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("password")]
    [InlineData("inactive")]
    [InlineData("scope")]
    [InlineData("cancel")]
    public async Task AuthorityChangedDuringAuditCannotPublishASuccessResponse(string change)
    {
        using var fixture = new Fixture();
        fixture.BeforeAuditCompletes = () => fixture.ChangeAuthority(change);
        var failure = await Record.ExceptionAsync(() => fixture.Compress());
        AssertRejection(failure, change);
        Assert.Equal("SOURCE CONTENT", fixture.ReadArchiveContent());
        fixture.AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("password")]
    [InlineData("scope")]
    [InlineData("generation")]
    public async Task FinalWorkspaceResolutionStillRequiresTheAcceptedAuthorityAndSite(string change)
    {
        using var fixture = new Fixture();
        fixture.BeforePublishedSiteRead = () => fixture.ChangeAuthority(change);
        var failure = await Record.ExceptionAsync(() => fixture.Compress());
        AssertRejection(failure, change);
        Assert.Equal("SOURCE CONTENT", fixture.ReadArchiveContent());
        Assert.Equal(0, fixture.Audits);
        fixture.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task HeavyWorkUsesThePrincipalCapturedBeforeScopeWaits()
    {
        using var fixture = new Fixture();
        using var first = fixture.Work.Enter($"files.heavy:{fixture.Actor:N}", 2, "files.too_many_operations");
        using var second = fixture.Work.Enter($"files.heavy:{fixture.Actor:N}", 2, "files.too_many_operations");
        fixture.BeforeScopeRead = () => fixture.Controller.HttpContext.User = Fixture.Principal(Guid.NewGuid(), UserRole.Customer);
        var failure = await Record.ExceptionAsync(() => fixture.Compress());
        Assert.Equal("files.too_many_operations", Assert.IsType<AppException>(failure).MessageKey);
        Assert.False(File.Exists(fixture.Archive));
        Assert.Equal(0, fixture.Audits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheActualArchiveSizeIsCheckedBeforeNewOrOverwritePublication(bool existing)
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Source, RandomBytes(2 * 1024 * 1024));
        var before = existing ? fixture.WriteExistingArchive("OLD ARCHIVE") : null;
        fixture.Request.Overwrite = existing;
        var failure = await Record.ExceptionAsync(() => fixture.Compress());
        Assert.Equal("files.quota_exceeded", Assert.IsType<AppException>(failure).MessageKey);
        if (existing) Assert.Equal(before, File.ReadAllBytes(fixture.Archive));
        else Assert.False(File.Exists(fixture.Archive));
        Assert.Equal(0, fixture.Audits);
        fixture.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task ACompressibleSourceUsesItsActualSmallArchiveSize()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Source, new byte[2 * 1024 * 1024]);
        await fixture.Compress();
        Assert.InRange(new FileInfo(fixture.Archive).Length, 1, 1024 * 1024);
        using var zip = ZipFile.OpenRead(fixture.Archive);
        Assert.Equal(2 * 1024 * 1024, Assert.Single(zip.Entries).Length);
    }

    [Fact]
    public async Task OverwritingWithASmallerArchiveUsesZeroNetGrowthAtTheQuotaLimit()
    {
        using var fixture = new Fixture();
        fixture.WriteExistingArchive(new string('X', 16000));
        fixture.UsageMb = 1;
        fixture.Request.Overwrite = true;
        await fixture.Compress();
        Assert.Equal("SOURCE CONTENT", fixture.ReadArchiveContent());
        fixture.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task ThePrivateStagedArchiveIsNotCountedTwiceBySiteUsageMeasurement()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Source, RandomBytes(1400 * 1024));
        fixture.WriteExistingArchive(RandomBytes(600 * 1024));
        fixture.Request.Overwrite = true;
        fixture.MaxDiskMb = 3;
        fixture.MeasureActualUsage = true;
        await fixture.Compress();
        Assert.InRange(new FileInfo(fixture.Archive).Length, 1400 * 1024, 1500 * 1024);
        Assert.True(fixture.UsageReads > 0);
        Assert.False(fixture.UsageSawSiteTemporaryArchive);
        fixture.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task CancellationAtTheQuotaBoundaryCleansStagingAndPreservesTheOldArchive()
    {
        using var fixture = new Fixture();
        var before = fixture.WriteExistingArchive("OLD ARCHIVE");
        fixture.Request.Overwrite = true;
        fixture.BeforeUsage = () => { fixture.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Compress());
        Assert.Equal(before, File.ReadAllBytes(fixture.Archive));
        Assert.Equal(0, fixture.Audits);
        fixture.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task DefaultCompressionPreservesAnExistingArchive()
    {
        using var fixture = new Fixture();
        var before = fixture.WriteExistingArchive("OLD ARCHIVE");
        var failure = await Record.ExceptionAsync(() => fixture.Compress());
        Assert.Equal(before, File.ReadAllBytes(fixture.Archive));
        Assert.Equal("files.already_exists", Assert.IsType<AppException>(failure).MessageKey);
        Assert.Equal(0, fixture.Audits);
        fixture.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task ATargetCreatedWhileCompressingWinsWhenOverwriteWasNotApproved()
    {
        using var fixture = new Fixture();
        fixture.OnSourceOpen = () => fixture.WriteExistingArchive("NEWLY CREATED");
        var failure = await Record.ExceptionAsync(() => fixture.Compress());
        Assert.Equal("NEWLY CREATED", fixture.ReadArchiveContent());
        Assert.Equal("files.already_exists", Assert.IsType<AppException>(failure).MessageKey);
        Assert.Equal(0, fixture.Audits);
        fixture.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task ExplicitOverwriteKeepsTheExistingSuccessfulBehavior()
    {
        using var fixture = new Fixture();
        fixture.WriteExistingArchive("OLD ARCHIVE");
        fixture.Request.Overwrite = true;
        await fixture.Compress();
        Assert.Equal("SOURCE CONTENT", fixture.ReadArchiveContent());
        Assert.Equal(1, fixture.Audits);
        fixture.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task ChangingTheRequestObjectCannotGrantOverwriteAfterAcceptance()
    {
        using var fixture = new Fixture();
        var before = fixture.WriteExistingArchive("OLD ARCHIVE");
        fixture.BeforeScopeRead = () => fixture.Request.Overwrite = true;
        var failure = await Record.ExceptionAsync(() => fixture.Compress());
        Assert.Equal(before, File.ReadAllBytes(fixture.Archive));
        Assert.Equal("files.already_exists", Assert.IsType<AppException>(failure).MessageKey);
        Assert.Equal(0, fixture.Audits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheDeductedOriginalCannotBeChangedDuringQuotaMeasurement(bool delete)
    {
        using var fixture = new Fixture();
        var original = fixture.WriteExistingArchive("OLD ARCHIVE");
        fixture.Request.Overwrite = true;
        fixture.BeforeUsage = () =>
        {
            var error = Record.Exception(() =>
            {
                if (delete) File.Delete(fixture.Archive);
                else File.WriteAllText(fixture.Archive, "CHANGED DURING QUOTA");
            });
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Original was mutable.");
            Assert.Equal(original, File.ReadAllBytes(fixture.Archive));
            return Task.CompletedTask;
        };
        await fixture.Compress();
        Assert.Equal(1, fixture.UsageReads);
        Assert.Equal("SOURCE CONTENT", fixture.ReadArchiveContent());
        fixture.AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APreviouslyAbsentTargetCreatedDuringQuotaIsPreserved(bool overwrite)
    {
        using var fixture = new Fixture();
        fixture.Request.Overwrite = overwrite;
        fixture.BeforeUsage = () => { fixture.WriteExistingArchive("NEWLY CREATED"); return Task.CompletedTask; };
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Compress());
        Assert.Equal("files.already_exists", error.MessageKey);
        Assert.Equal("NEWLY CREATED", fixture.ReadArchiveContent());
        Assert.Equal(0, fixture.Audits);
        fixture.AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("scope")]
    public async Task QuotaWaitDoesNotOutlivePublicationAuthority(string change)
    {
        using var fixture = new Fixture();
        var original = fixture.WriteExistingArchive("OLD ARCHIVE");
        fixture.Request.Overwrite = true;
        fixture.BeforeUsage = () => fixture.ChangeAuthority(change);
        var error = await Record.ExceptionAsync(() => fixture.Compress());
        AssertRejection(error, change);
        Assert.Equal(original, File.ReadAllBytes(fixture.Archive));
        Assert.Equal(0, fixture.Audits);
        fixture.AssertNoTemporaryFiles();
    }

    private static void AssertRejection(Exception? failure, string change)
    {
        if (change == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else Assert.Equal(change == "scope" ? 403 : change == "generation" ? 409 : 401,
            Assert.IsType<AppException>(failure).HttpStatusCode);
    }

    private static byte[] RandomBytes(int count)
    { var bytes = new byte[count]; new Random(17431).NextBytes(bytes); return bytes; }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "adapanel-compress-authority-" + Guid.NewGuid().ToString("N")));
        public string SiteRoot => Path.Combine(Root, "site");
        public string Source => Path.Combine(SiteRoot, "source.txt");
        public string Archive => Path.Combine(SiteRoot, "source.txt.zip");
        public string VirtualRoot { get; }
        public Guid Actor { get; } = Guid.NewGuid();
        public FileService Files { get; }
        public FilesController Controller { get; }
        public ConcurrentWorkGate Work { get; } = new();
        public ArchiveFileRequest Request { get; }
        public Func<Task>? BeforeAuditCompletes, BeforePublishedSiteRead, BeforeUsage;
        public Action? BeforeScopeRead, OnSourceOpen;
        public int Audits, UsageReads, MaxDiskMb = 1;
        public long UsageMb;
        public bool MeasureActualUsage, UsageSawSiteTemporaryArchive;
        private bool allowed = true;
        private int publishedRead, scopeRead;
        private string sitePath;
        private readonly string sid = SessionRevocationStore.NewSessionId();
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly AdaPanelDbContext db;
        private readonly SessionRevocationStore sessions;
        private readonly ServiceProvider services;
        private readonly FilePublicationAuthorityTests.LocalGate gates = new();
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));

        public Fixture(UserRole role = UserRole.Customer)
        {
            sitePath = SiteRoot;
            Directory.CreateDirectory(SiteRoot);
            File.WriteAllText(Source, "SOURCE CONTENT");
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            db.Users.Add(new PanelUser { Id = Actor, UserName = "compress-owner", Email = "compress@example.invalid",
                Role = role, IsActive = true, PasswordHash = "fixture-password-hash" });
            db.SaveChanges();
            var name = "compress-" + Guid.NewGuid().ToString("N") + ".test";
            VirtualRoot = $"sites/{name}";
            var sites = Stub<IIisSiteService>((method, _) => method == nameof(IIisSiteService.GetSiteAsync)
                ? ReadSite() : throw new NotSupportedException(method));
            var scopes = Stub<ICustomerScopeService>((method, _) => method switch
            {
                nameof(ICustomerScopeService.GetAssignedSitesAsync) or nameof(ICustomerScopeService.GetResellerSitesAsync)
                    => Task.FromResult<IReadOnlyCollection<string>>(allowed ? [name] : []),
                nameof(ICustomerScopeService.GetParentSubscriptionAsync) => Task.FromResult<Guid?>(null),
                nameof(ICustomerScopeService.GetScopeAsync) => ReadScope(),
                nameof(ICustomerScopeService.GetSiteOwnerUserIdAsync) => Task.FromResult<Guid?>(Actor),
                nameof(ICustomerScopeService.GetDiskUsageMbAsync) => ReadUsage(),
                _ => throw new NotSupportedException(method)
            });
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(Root, "panel") }));
            sessions = new SessionRevocationStore(store);
            services = new ServiceCollection().AddSingleton(db).AddSingleton(sessions)
                .AddSingleton<IIisSiteService>(sites).AddSingleton<IConcurrencyLock>(gates).BuildServiceProvider();
            Files = new FileService(sites) { CompressionStagingPath = () => Path.Combine(Root, "private-stage.zip") };
            Files.OpenCompressionFile = path =>
            {
                OnSourceOpen?.Invoke();
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            };
            Controller = new FilesController(Files, scopes, Stub<IAuditService>((method, _) =>
                method is nameof(IAuditService.WriteAsync) or nameof(IAuditService.WriteForSubscriptionAsync)
                    ? Audit() : throw new NotSupportedException(method)), Work)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services,
                    User = Principal(Actor, role, sid) } }
            };
            Request = new ArchiveFileRequest { Path = VirtualRoot + "/source.txt" };

            async Task<SiteDto> ReadSite()
            {
                if (BeforePublishedSiteRead is { } before && File.Exists(Archive) && Interlocked.Exchange(ref publishedRead, 1) == 0)
                    await before();
                return new SiteDto { Name = name, Id = 24, PhysicalPath = sitePath, ApplicationRootPath = sitePath, ApplicationPoolName = name };
            }
            Task<CustomerScope> ReadScope()
            {
                if (Interlocked.Exchange(ref scopeRead, 1) == 0) BeforeScopeRead?.Invoke();
                return Task.FromResult(new CustomerScope([name], 1, MaxDiskMb, 0, 0, 0));
            }
        }

        public static ClaimsPrincipal Principal(Guid actor, UserRole role, string? session = null) =>
            new(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, role.ToString()),
                new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("fixture-password-hash")),
                new Claim(SessionRevocationStore.SessionIdClaim, session ?? SessionRevocationStore.NewSessionId())], CookieAuthenticationDefaults.AuthenticationScheme));

        private async Task Audit()
        { Audits++; if (BeforeAuditCompletes is { } before) await before(); }
        private async Task<DiskUsage> ReadUsage()
        {
            UsageReads++;
            UsageSawSiteTemporaryArchive |= Directory.GetFiles(SiteRoot, ".adapanel-*", SearchOption.AllDirectories).Length != 0;
            if (BeforeUsage is { } before) await before();
            var usage = MeasureActualUsage ? Directory.GetFiles(SiteRoot, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) / (1024 * 1024) : UsageMb;
            return new DiskUsage(usage, true, string.Empty);
        }
        public async Task ChangeAuthority(string change)
        {
            if (change == "logout") await sessions.RevokeAsync(sid, lifetime.Token);
            else if (change == "password") await db.Users.Where(user => user.Id == Actor).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.PasswordHash, "changed"));
            else if (change == "inactive") await db.Users.Where(user => user.Id == Actor).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsActive, false));
            else if (change == "scope") allowed = false;
            else if (change == "cancel") lifetime.Cancel();
            else if (change == "generation")
            {
                sitePath = Path.Combine(Root, "new-site");
                Directory.CreateDirectory(sitePath);
                File.WriteAllText(Path.Combine(sitePath, "foreign.txt"), "FOREIGN");
            }
            else throw new ArgumentOutOfRangeException(nameof(change));
        }
        public async Task<FileWorkspaceDto> Compress()
        {
            var result = await Controller.CompressAsync(Request, lifetime.Token);
            return Assert.IsType<ApiResponse<FileWorkspaceDto>>(Assert.IsType<OkObjectResult>(result.Result).Value).Data!;
        }
        public byte[] WriteExistingArchive(string content) => WriteExistingArchive(Encoding.UTF8.GetBytes(content));
        public byte[] WriteExistingArchive(byte[] content)
        {
            using (var output = new FileStream(Archive, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            using (var entry = archive.CreateEntry("source.txt").Open()) entry.Write(content);
            return File.ReadAllBytes(Archive);
        }
        public string ReadArchiveContent()
        {
            using var archive = ZipFile.OpenRead(Archive);
            using var stream = Assert.Single(archive.Entries).Open();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        public void AssertNoTemporaryFiles()
        {
            Assert.False(File.Exists(Path.Combine(Root, "private-stage.zip")));
            Assert.Empty(Directory.GetFiles(SiteRoot, ".adapanel-*", SearchOption.AllDirectories));
        }
        public void Cancel() => lifetime.Cancel();
        public void Dispose()
        {
            services.Dispose(); db.Dispose(); connection.Dispose(); gates.Dispose(); lifetime.Dispose();
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(Root), temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(Root).StartsWith("adapanel-compress-authority-", StringComparison.Ordinal))
                throw new InvalidOperationException("Compression fixture cleanup is outside its owned temporary root.");
            Directory.Delete(Root, recursive: true);
        }
    }
    private static T Stub<T>(Func<string, object?[]?, object?> handler) where T : class
    { var proxy = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)proxy).Handler = handler; return proxy; }
    public class Proxy : DispatchProxy
    {
        public Func<string, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!.Name, args);
    }
}
