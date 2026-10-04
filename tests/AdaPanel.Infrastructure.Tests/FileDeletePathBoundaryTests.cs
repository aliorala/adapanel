using System.Diagnostics;
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

public sealed class FileDeletePathBoundaryTests
{
    [Theory]
    [InlineData(false, UserRole.Customer)]
    [InlineData(true, UserRole.Customer)]
    [InlineData(false, UserRole.Administrator)]
    [InlineData(true, UserRole.Administrator)]
    public async Task AncestorReplacedDuringApprovalCannotRedirectDeletionOutsideTheSite(bool directory, UserRole role)
    {
        using var f = await Fixture.Create(directory, role);
        f.AfterApproval = async token =>
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            f.ReplaceAncestorWithOwnedJunction();
        };

        var error = await Record.ExceptionAsync(f.Delete);

        Assert.Equal(1, f.BoundaryCalls);
        f.AssertOutsidePreserved();
        f.AssertRetainedOriginal();
        Assert.Equal("files.path_outside_root", Assert.IsType<AppException>(error).MessageKey);
        Assert.Equal(0, f.Audits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALinkPresentBeforeTheRequestIsAlreadyRejected(bool directory)
    {
        using var f = await Fixture.Create(directory, UserRole.Customer);
        f.ReplaceAncestorWithOwnedJunction();

        var error = await Assert.ThrowsAsync<AppException>(f.Delete);

        Assert.Equal("files.path_outside_root", error.MessageKey);
        f.AssertOutsidePreserved();
        f.AssertRetainedOriginal();
        Assert.Equal(0, f.Audits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheSelectedEntryItselfCannotBecomeAJunctionDuringApproval(bool directory)
    {
        using var f = await Fixture.Create(directory, UserRole.Customer);
        f.AfterApproval = async token =>
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            f.ReplaceSelectedWithOwnedJunction();
        };

        var error = await Record.ExceptionAsync(f.Delete);

        Assert.Equal(1, f.BoundaryCalls);
        Assert.Equal("files.path_outside_root", Assert.IsType<AppException>(error).MessageKey);
        Assert.True(File.GetAttributes(f.Selected).HasFlag(FileAttributes.ReparsePoint));
        f.AssertOutsidePreserved();
        f.AssertRetainedSelected();
        Assert.Equal(0, f.Audits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterTheOwnedSwapPreventsAnyDeletion(bool directory)
    {
        using var f = await Fixture.Create(directory, UserRole.Customer);
        f.AfterApproval = async _ =>
        {
            await Task.Yield();
            f.ReplaceAncestorWithOwnedJunction();
            f.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(f.Delete);

        Assert.Equal(1, f.BoundaryCalls);
        f.AssertOutsidePreserved();
        f.AssertRetainedOriginal();
        Assert.Equal(0, f.Audits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryFilesAndTreesStillDeleteAndReturnAScopedWorkspace(bool directory)
    {
        using var f = await Fixture.Create(directory, UserRole.Customer);

        await f.Delete();

        Assert.False(File.Exists(f.Selected));
        Assert.False(Directory.Exists(f.Selected));
        f.AssertOutsidePreserved();
        Assert.Equal(1, f.Audits);
    }

    [Fact]
    public async Task AnAlreadyMissingOrdinaryTargetKeepsTheExistingIdempotentBehavior()
    {
        using var f = await Fixture.Create(false, UserRole.Customer);
        File.Delete(f.Selected);

        await f.Delete();

        f.AssertOutsidePreserved();
        Assert.Equal(1, f.Audits);
    }

    private sealed class Fixture : IDisposable
    {
        private const string Prefix = "adapanel-delete-boundary-";
        private const string Original = "ORIGINAL SELECTED DATA";
        private const string Foreign = "OUTSIDE FIXTURE SENTINEL";
        private readonly string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N")));
        private readonly string siteName = "delete-" + Guid.NewGuid().ToString("N") + ".test";
        private readonly Guid actor = Guid.NewGuid();
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(45));
        private readonly FilePublicationAuthorityTests.LocalGate gates = new();
        private readonly AdaPanelDbContext db;
        private readonly ServiceProvider services;
        private readonly FilesController controller;
        private readonly CustomerScopeService scopes;
        private readonly bool directory;
        private readonly UserRole role;
        private readonly List<(string Link, string Target)> junctions = [];
        private string SiteRoot => Path.Combine(root, "site");
        private string Ancestor => Path.Combine(SiteRoot, "parent");
        private string Retained => Path.Combine(root, "retained-parent");
        private string RetainedSelected => Path.Combine(root, "retained-selected");
        private string Outside => Path.Combine(root, "outside");
        private string SelectedName => directory ? "selected" : "selected.txt";
        private string SentinelRelative => directory ? Path.Combine(SelectedName, "nested", "data.txt") : SelectedName;
        public string Selected => Path.Combine(Ancestor, SelectedName);
        public int Audits { get; private set; }
        public int BoundaryCalls { get; private set; }
        public Func<CancellationToken, Task>? AfterApproval { get; set; }

        private Fixture(bool directory, UserRole role)
        {
            this.directory = directory;
            this.role = role;
            WriteOwned(Path.Combine(Ancestor, SentinelRelative), Original);
            WriteOwned(Path.Combine(Outside, SentinelRelative), Foreign);
            WriteOwned(Path.Combine(Outside, "unrelated.txt"), "UNRELATED OUTSIDE DATA");
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            db.Users.Add(new PanelUser { Id = actor, UserName = "actor", Email = "actor@test.invalid",
                IsActive = true, Role = role, PasswordHash = "delete-boundary-fixture" });
            db.SaveChanges();
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(root, "panel") }));
            var sites = Stub<IIisSiteService>((method, args) =>
            {
                if (method.Name != nameof(IIisSiteService.GetSiteAsync)) throw new NotSupportedException(method.Name);
                Assert.Equal(siteName, args![0]);
                return Task.FromResult(new SiteDto { Id = 95, Name = siteName, PhysicalPath = SiteRoot,
                    ApplicationRootPath = SiteRoot, ApplicationPoolName = siteName });
            });
            scopes = new CustomerScopeService(store, Stub<IServicePlanService>(),
                Stub<IResellerQuotaService>((method, _) => method.Name == nameof(IResellerQuotaService.GetQuotaAsync)
                    ? Task.FromResult<ResellerQuotaDto?>(null) : throw new NotSupportedException(method.Name)), sites);
            services = new ServiceCollection().AddSingleton(db).AddSingleton(new SessionRevocationStore(store))
                .AddSingleton<IIisSiteService>(sites).AddSingleton<IConcurrencyLock>(gates).BuildServiceProvider();
            var actual = new FileService(sites);
            // Keep the real controller approval and real FileService. Only insert a
            // filesystem writer at the returned async approval boundary, with no fake
            // access decision or synthetic path/owner result.
            var files = Stub<IFileService>((method, args) =>
            {
                if (method.Name != nameof(IFileService.DeleteAsync) || args!.Length != 3)
                    throw new NotSupportedException(method.Name);
                var approve = (Func<SiteDto, CancellationToken, Task>)args[1]!;
                return actual.DeleteAsync((DeleteFileRequest)args[0]!, async (site, token) =>
                {
                    await approve(site, token);
                    if (AfterApproval is { } hook)
                    {
                        AfterApproval = null;
                        BoundaryCalls++;
                        await hook(token);
                    }
                }, (CancellationToken)args[2]!);
            });
            controller = new FilesController(files, scopes,
                Stub<IAuditService>((method, args) =>
                {
                    if (method.Name is not (nameof(IAuditService.WriteAsync) or nameof(IAuditService.WriteForSubscriptionAsync)))
                        throw new NotSupportedException(method.Name);
                    Assert.Equal("files.deleted", args![0]); Audits++; return Task.CompletedTask;
                }), new ConcurrentWorkGate())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
                {
                    RequestServices = services,
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, role.ToString()),
                        new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("delete-boundary-fixture")),
                        new Claim(SessionRevocationStore.SessionIdClaim, SessionRevocationStore.NewSessionId())], CookieAuthenticationDefaults.AuthenticationScheme))
                } }
            };
        }

        public static async Task<Fixture> Create(bool directory, UserRole role)
        {
            var f = new Fixture(directory, role);
            try { await f.scopes.SetScopeAsync(f.actor, [f.siteName], 1, 0); return f; }
            catch { f.Dispose(); throw; }
        }
        public async Task Delete()
        {
            var result = await controller.DeleteAsync(new DeleteFileRequest { Path = $"sites/{siteName}/parent/{SelectedName}" }, lifetime.Token);
            var body = Assert.IsType<ApiResponse<FileWorkspaceDto>>(Assert.IsType<OkObjectResult>(result.Result).Value).Data!;
            Assert.Equal(role == UserRole.Administrator ? Ancestor : string.Empty, body.PhysicalPath);
        }
        public void Cancel() => lifetime.Cancel();
        public void ReplaceAncestorWithOwnedJunction()
        {
            ValidateOwnedRoot();
            CheckOwned(Ancestor); CheckOwned(Retained); CheckOwned(Outside);
            Assert.Empty(junctions);
            Assert.False(File.GetAttributes(Ancestor).HasFlag(FileAttributes.ReparsePoint));
            Assert.False(File.GetAttributes(Outside).HasFlag(FileAttributes.ReparsePoint));
            Directory.Move(Ancestor, Retained);
            CreateOwnedJunction(Ancestor, Outside);
        }
        public void ReplaceSelectedWithOwnedJunction()
        {
            ValidateOwnedRoot();
            CheckOwned(Selected); CheckOwned(RetainedSelected); CheckOwned(Outside);
            Assert.Empty(junctions);
            Assert.False(File.GetAttributes(Selected).HasFlag(FileAttributes.ReparsePoint));
            if (directory) Directory.Move(Selected, RetainedSelected);
            else File.Move(Selected, RetainedSelected);
            CreateOwnedJunction(Selected, directory ? Path.Combine(Outside, SelectedName) : Outside);
        }
        private void CreateOwnedJunction(string link, string target)
        {
            ValidateOwnedRoot(); CheckOwned(link); CheckOwned(target);
            Assert.False(File.Exists(link)); Assert.False(Directory.Exists(link));
            Assert.True(Directory.Exists(target));
            Assert.False(File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint));
            // These paths are generated solely from the validated fixture root and
            // fixed segments. No real account, IIS operation or external path is used.
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe", Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            })!;
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("Owned fixture junction creation timed out.");
            }
            Assert.Equal(0, process.ExitCode);
            junctions.Add((link, target));
            Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
            Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(new DirectoryInfo(link).ResolveLinkTarget(true)!.FullName), ignoreCase: true);
        }
        public void AssertOutsidePreserved()
        {
            Assert.True(File.Exists(Path.Combine(Outside, SentinelRelative)), "The approved deletion reached the outside fixture sentinel.");
            Assert.Equal(Foreign, File.ReadAllText(Path.Combine(Outside, SentinelRelative)));
            Assert.Equal("UNRELATED OUTSIDE DATA", File.ReadAllText(Path.Combine(Outside, "unrelated.txt")));
        }
        public void AssertRetainedOriginal() => Assert.Equal(Original, File.ReadAllText(Path.Combine(Retained, SentinelRelative)));
        public void AssertRetainedSelected() => Assert.Equal(Original, File.ReadAllText(directory
            ? Path.Combine(RetainedSelected, "nested", "data.txt") : RetainedSelected));
        private void WriteOwned(string path, string text)
        {
            ValidateOwnedRoot(); CheckOwned(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        private void CheckOwned(string path)
        {
            if (!Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture path escaped its owned root.");
        }
        private void ValidateOwnedRoot()
        {
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            var name = Path.GetFileName(root);
            if (!string.Equals(Path.GetDirectoryName(root), parent, StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[Prefix.Length..], "N", out _) ||
                Directory.Exists(root) && File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Fixture root must be a unique ordinary direct child of TEMP.");
        }
        public void Dispose()
        {
            services.Dispose(); db.Dispose(); connection.Dispose(); gates.Dispose(); lifetime.Dispose();
            ValidateOwnedRoot();
            foreach (var (link, target) in junctions)
            {
                CheckOwned(link); CheckOwned(target);
                if (!Directory.Exists(link)) continue;
                if (!File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidOperationException("Cleanup expected its own junction.");
                Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(new DirectoryInfo(link).ResolveLinkTarget(true)!.FullName), ignoreCase: true);
                Directory.Delete(link, recursive: false);
            }
            if (!Directory.Exists(root)) return;
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var path))
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("Unexpected fixture reparse point.");
                if (!attributes.HasFlag(FileAttributes.Directory)) continue;
                foreach (var entry in Directory.GetFileSystemEntries(path)) { CheckOwned(entry); pending.Push(entry); }
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
