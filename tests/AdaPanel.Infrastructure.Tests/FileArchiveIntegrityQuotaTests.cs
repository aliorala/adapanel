using System.Buffers.Binary;
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

public sealed class FileArchiveIntegrityQuotaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuotaAndExtractionUseTheSameOpenedArchive(bool replace)
    {
        using var fixture = new Fixture();
        var accepted = Encoding.UTF8.GetBytes("ORIGINAL");
        fixture.WriteArchive([( "payload.txt", accepted )]);
        var replacement = fixture.MakeArchive([( "payload.txt", new byte[1024 * 1024 + 1] )]);
        var attempted = false;
        fixture.DiskUsageRead = () =>
        {
            attempted = true;
            try
            {
                if (replace)
                {
                    var next = Path.Combine(fixture.Root, "replacement.zip");
                    File.WriteAllBytes(next, replacement);
                    File.Move(next, fixture.Archive, overwrite: true);
                }
                else File.WriteAllBytes(fixture.Archive, replacement);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A read handle that excludes mutation may reject this ordinary file replacement.
            }
            return Task.CompletedTask;
        };

        await fixture.Extract();

        Assert.True(attempted);
        Assert.Equal(accepted, File.ReadAllBytes(Path.Combine(fixture.Output, "payload.txt")));
        Assert.Equal(1, fixture.Audits);
        fixture.AssertArchiveHandleReleased();
    }

    [Theory]
    [InlineData("short-length")]
    [InlineData("long-length")]
    [InlineData("crc")]
    [InlineData("compression")]
    public async Task AnInvalidEntryCannotReplaceTheExistingDestination(string corruption)
    {
        using var fixture = new Fixture();
        var content = Encoding.UTF8.GetBytes("A small ordinary archive entry.");
        var bytes = fixture.MakeArchive([( "payload.txt", content )], CompressionLevel.NoCompression);
        CorruptEntry(bytes, 0, corruption);
        File.WriteAllBytes(fixture.Archive, bytes);
        Directory.CreateDirectory(fixture.Output);
        var target = Path.Combine(fixture.Output, "payload.txt");
        File.WriteAllText(target, "EXISTING CONTENT");

        var failure = await Record.ExceptionAsync(() => fixture.Extract());

        Assert.Equal("EXISTING CONTENT", File.ReadAllText(target));
        Assert.Equal("files.invalid_archive", Assert.IsType<AppException>(failure).MessageKey);
        Assert.Equal(0, fixture.Audits);
        Assert.Equal(new[] { "payload.txt" }, Directory.GetFiles(fixture.Output).Select(path => Path.GetFileName(path)!).Order().ToArray());
        fixture.AssertArchiveHandleReleased();
    }

    [Fact]
    public async Task ACorruptLaterEntryKeepsEarlierCompletedEntriesAndTheOldDestination()
    {
        using var fixture = new Fixture();
        var bytes = fixture.MakeArchive([
            ("first.txt", Encoding.UTF8.GetBytes("FIRST")),
            ("payload.txt", Encoding.UTF8.GetBytes("SECOND"))]);
        CorruptEntry(bytes, 1, "crc");
        File.WriteAllBytes(fixture.Archive, bytes);
        Directory.CreateDirectory(fixture.Output);
        File.WriteAllText(Path.Combine(fixture.Output, "payload.txt"), "EXISTING CONTENT");

        var failure = await Record.ExceptionAsync(() => fixture.Extract());

        Assert.Equal("EXISTING CONTENT", File.ReadAllText(Path.Combine(fixture.Output, "payload.txt")));
        Assert.Equal("FIRST", File.ReadAllText(Path.Combine(fixture.Output, "first.txt")));
        Assert.Equal("files.invalid_archive", Assert.IsType<AppException>(failure).MessageKey);
        Assert.Equal(0, fixture.Audits);
        fixture.AssertArchiveHandleReleased();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnreadableCentralDirectoryDoesNotCreateTheDestination(bool trusted)
    {
        using var fixture = new Fixture();
        var valid = fixture.MakeArchive([( "payload.txt", Encoding.UTF8.GetBytes("CONTENT") )]);
        File.WriteAllBytes(fixture.Archive, valid[..^10]);

        var failure = await Record.ExceptionAsync(() => trusted
            ? fixture.Files.ExtractAsync(fixture.Request, fixture.Token)
            : fixture.Extract());

        Assert.False(Directory.Exists(fixture.Output));
        Assert.Equal("files.invalid_archive", Assert.IsType<AppException>(failure).MessageKey);
        Assert.Equal(0, fixture.Audits);
        fixture.AssertArchiveHandleReleased();
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Fastest)]
    [InlineData(CompressionLevel.Optimal)]
    public async Task ValidArchivesPreserveTheirExactBytesAndNormalOverwrite(CompressionLevel compression)
    {
        using var fixture = new Fixture();
        var content = Encoding.UTF8.GetBytes("Türkçe 文本\n" + new string('A', 8192));
        fixture.WriteArchive([( "nested/örnek.txt", content ), ("empty.txt", [])], compression);
        DateTime expectedTimestamp;
        using (var archive = ZipFile.OpenRead(fixture.Archive)) expectedTimestamp = archive.Entries[0].LastWriteTime.UtcDateTime;
        Directory.CreateDirectory(Path.Combine(fixture.Output, "nested"));
        File.WriteAllText(Path.Combine(fixture.Output, "nested", "örnek.txt"), "PREVIOUS");

        var result = await fixture.Extract();

        Assert.Equal(content, File.ReadAllBytes(Path.Combine(fixture.Output, "nested", "örnek.txt")));
        Assert.Equal(expectedTimestamp, File.GetLastWriteTimeUtc(Path.Combine(fixture.Output, "nested", "örnek.txt")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(fixture.Output, "empty.txt")));
        Assert.Equal(fixture.Request.DestinationPath, result.Path);
        Assert.Equal(string.Empty, result.PhysicalPath);
        Assert.Equal(1, fixture.Audits);
        fixture.AssertArchiveHandleReleased();
    }

    [Fact]
    public async Task AnEmptyArchiveIsAValidZeroByteOperation()
    {
        using var fixture = new Fixture();
        fixture.WriteArchive([]);
        var result = await fixture.Extract();
        Assert.True(Directory.Exists(fixture.Output));
        Assert.Empty(result.Entries);
        Assert.Equal(1, fixture.Audits);
        fixture.AssertArchiveHandleReleased();
    }

    [Fact]
    public async Task AnArchiveOverQuotaCannotReplaceExistingFiles()
    {
        using var fixture = new Fixture();
        fixture.WriteArchive([( "payload.txt", new byte[1024 * 1024 + 1] )]);
        Directory.CreateDirectory(fixture.Output);
        File.WriteAllText(Path.Combine(fixture.Output, "payload.txt"), "EXISTING CONTENT");
        var failure = await Record.ExceptionAsync(() => fixture.Extract());
        Assert.Equal("files.quota_exceeded", Assert.IsType<AppException>(failure).MessageKey);
        Assert.Equal("EXISTING CONTENT", File.ReadAllText(Path.Combine(fixture.Output, "payload.txt")));
        Assert.Equal(0, fixture.Audits);
        fixture.AssertArchiveHandleReleased();
    }

    [Fact]
    public async Task CancellationDuringQuotaMeasurementPublishesNothingAndReleasesTheArchive()
    {
        using var fixture = new Fixture();
        fixture.WriteArchive([( "payload.txt", Encoding.UTF8.GetBytes("CONTENT") )]);
        fixture.DiskUsageRead = () => { fixture.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Extract());
        Assert.False(Directory.Exists(fixture.Output));
        Assert.Equal(0, fixture.Audits);
        fixture.AssertArchiveHandleReleased();
    }

    private static void CorruptEntry(byte[] bytes, int entryIndex, string kind)
    {
        var central = -1;
        for (var i = 0; i <= bytes.Length - 46; i++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) == 0x02014b50 && entryIndex-- == 0)
            { central = i; break; }
        Assert.True(central >= 0, "The fixture must contain the requested central-directory entry.");
        var local = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central + 42, 4)));
        if (kind == "compression")
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(central + 10, 2), 99);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(local + 8, 2), 99);
        }
        else if (kind == "crc")
        {
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central + 16, 4)) ^ 1;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 16, 4), crc);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(local + 14, 4), crc);
        }
        else
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(central + 24, 4));
            length = kind == "short-length" ? length - 1 : length + 1;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 24, 4), length);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(local + 22, 4), length);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "adapanel-archive-integrity-" + Guid.NewGuid().ToString("N")));
        public string SiteRoot => Path.Combine(Root, "site");
        public string Archive => Path.Combine(SiteRoot, "package.zip");
        public string Output => Path.Combine(SiteRoot, "output");
        public FileService Files { get; }
        public ArchiveFileRequest Request { get; }
        public Func<Task>? DiskUsageRead;
        public int Audits;
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly AdaPanelDbContext db;
        private readonly ServiceProvider services;
        private readonly FilePublicationAuthorityTests.LocalGate gates = new();
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        private readonly FilesController controller;
        public CancellationToken Token => lifetime.Token;

        public Fixture()
        {
            Directory.CreateDirectory(SiteRoot);
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            var owner = Guid.NewGuid();
            db.Users.Add(new PanelUser { Id = owner, UserName = "archive-owner", Email = "archive@example.invalid",
                Role = UserRole.Customer, IsActive = true, PasswordHash = "fixture-password-hash" });
            db.SaveChanges();
            var name = "archive-" + Guid.NewGuid().ToString("N") + ".test";
            var site = new SiteDto { Name = name, Id = 13, PhysicalPath = SiteRoot, ApplicationRootPath = SiteRoot, ApplicationPoolName = name };
            var sites = Stub<IIisSiteService>((method, _) => method == nameof(IIisSiteService.GetSiteAsync)
                ? Task.FromResult(site) : throw new NotSupportedException(method));
            var scopes = Stub<ICustomerScopeService>((method, _) => method switch
            {
                nameof(ICustomerScopeService.GetAssignedSitesAsync) => Task.FromResult<IReadOnlyCollection<string>>([name]),
                nameof(ICustomerScopeService.GetParentSubscriptionAsync) => Task.FromResult<Guid?>(null),
                nameof(ICustomerScopeService.GetScopeAsync) => Task.FromResult(new CustomerScope([name], 1, 1, 0, 0, 0)),
                nameof(ICustomerScopeService.GetSiteOwnerUserIdAsync) => Task.FromResult<Guid?>(owner),
                nameof(ICustomerScopeService.GetDiskUsageMbAsync) => ReadUsage(),
                _ => throw new NotSupportedException(method)
            });
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(Root, "panel") }));
            services = new ServiceCollection().AddSingleton(db).AddSingleton(new SessionRevocationStore(store))
                .AddSingleton<IIisSiteService>(sites).AddSingleton<IConcurrencyLock>(gates).BuildServiceProvider();
            Files = new FileService(sites);
            controller = new FilesController(Files, scopes, Stub<IAuditService>((method, _) =>
            {
                if (method is not (nameof(IAuditService.WriteAsync) or nameof(IAuditService.WriteForSubscriptionAsync)))
                    throw new NotSupportedException(method);
                Audits++; return Task.CompletedTask;
            }), new ConcurrentWorkGate())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services,
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, owner.ToString()), new Claim(ClaimTypes.Role, nameof(UserRole.Customer)),
                        new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("fixture-password-hash")),
                        new Claim(SessionRevocationStore.SessionIdClaim, SessionRevocationStore.NewSessionId())], CookieAuthenticationDefaults.AuthenticationScheme)) } }
            };
            Request = new ArchiveFileRequest { Path = $"sites/{name}/package.zip", DestinationPath = $"sites/{name}/output" };
        }

        private async Task<DiskUsage> ReadUsage()
        {
            if (DiskUsageRead is { } read) await read();
            var bytes = Directory.GetFiles(SiteRoot, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
            return new DiskUsage(PanelUnits.BytesToMebibytes(bytes), true, string.Empty) { UsageBytes = bytes };
        }
        public async Task<FileWorkspaceDto> Extract()
        {
            var result = await controller.ExtractAsync(Request, Token);
            return Assert.IsType<ApiResponse<FileWorkspaceDto>>(Assert.IsType<OkObjectResult>(result.Result).Value).Data!;
        }
        public byte[] MakeArchive((string Name, byte[] Content)[] entries, CompressionLevel compression = CompressionLevel.Optimal)
        {
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var entry in entries)
                {
                    var archived = zip.CreateEntry(entry.Name, compression);
                    archived.LastWriteTime = new DateTimeOffset(2024, 2, 3, 4, 5, 6, TimeSpan.Zero);
                    using var stream = archived.Open();
                    stream.Write(entry.Content);
                }
            return output.ToArray();
        }
        public void WriteArchive((string Name, byte[] Content)[] entries, CompressionLevel compression = CompressionLevel.Optimal) =>
            File.WriteAllBytes(Archive, MakeArchive(entries, compression));
        public void AssertArchiveHandleReleased()
        { using var exclusive = new FileStream(Archive, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        public void Cancel() => lifetime.Cancel();
        public void Dispose()
        {
            services.Dispose(); db.Dispose(); connection.Dispose(); gates.Dispose(); lifetime.Dispose();
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(Root), temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(Root).StartsWith("adapanel-archive-integrity-", StringComparison.Ordinal))
                throw new InvalidOperationException("Archive fixture cleanup is outside its owned temporary root.");
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
