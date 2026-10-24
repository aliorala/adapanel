using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Claims;
using System.Security.Principal;
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
using Xunit.Abstractions;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>Actual controller, SQLite authority, service and native ACL processing. IIS is fake;
/// native commands can affect only a guarded unique temp tree and the current process SID.</summary>
// Native ACL process startup must not compete with unrelated integration tests.
[Collection("Script host timing")]
public sealed class FileRecursivePermissionBoundaryTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("R", false)]
    [InlineData("RX", false)]
    [InlineData("W", false)]
    [InlineData("M", false)]
    [InlineData("F", false)]
    [InlineData("R", true)]
    [InlineData("RX", true)]
    [InlineData("W", true)]
    [InlineData("M", true)]
    [InlineData("F", true)]
    public async Task RecursivePermissionsDoNotAlterTheDestinationOfADescendantJunction(string rights, bool nested)
    {
        await using var f = await Fixture.Create(rights, nested ? "nested" : "direct");
        await f.Run();
        f.AssertNormalTreeChanged();
        Assert.Equal(f.OutsideBefore, f.OutsideAcl());
        Assert.Single(f.Audits);
    }

    [Theory]
    [InlineData("R")]
    [InlineData("RX")]
    [InlineData("W")]
    [InlineData("M")]
    [InlineData("F")]
    public async Task NormalDirectoriesAndFilesReceiveTheRequestedRights(string rights)
    {
        await using var f = await Fixture.Create(rights);
        await f.Run();
        f.AssertNormalTreeChanged();
        Assert.Equal(f.OutsideBefore, f.OutsideAcl());
        Assert.Single(f.Audits);
    }

    [Fact]
    public async Task SelectedTargetReplacedDuringApprovalIsRejectedBeforeNativeMutation()
    {
        await using var f = await Fixture.Create("M");
        f.AfterApproval = () => f.ReplaceWithJunction(f.Selected);
        Assert.IsType<AppException>(await Record.ExceptionAsync(() => f.Run()));
        Assert.Empty(f.NativeCalls);
        Assert.Empty(f.Audits);
        Assert.Equal(f.OutsideBefore, f.OutsideAcl());
    }

    [Fact]
    public async Task DescendantReplacedDuringApprovalNeverChangesTheOutsideDestination()
    {
        await using var f = await Fixture.Create("M");
        f.AfterApproval = () => f.ReplaceWithJunction(f.Ordinary);
        // A safe implementation may exclude the newly measured link or reject the changed plan.
        // Neither choice may run a recursive native mutation into its destination.
        var failure = await Record.ExceptionAsync(() => f.Run());
        Assert.True(failure is null or AppException, failure?.ToString());
        Assert.Equal(f.OutsideBefore, f.OutsideAcl());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeTheFirstNativeEffectDoesNotPublishSuccess(bool duringApproval)
    {
        await using var f = await Fixture.Create("M");
        if (duringApproval) f.AfterApproval = () => { f.Cancel(); return Task.CompletedTask; };
        else f.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run());
        Assert.Empty(f.NativeCalls);
        Assert.Empty(f.Audits);
        Assert.Equal(f.NormalBefore, f.NormalAcl());
        Assert.Equal(f.OutsideBefore, f.OutsideAcl());
    }

    [Fact]
    public async Task AFailedNativeCommandDoesNotPublishSuccessOrAnAudit()
    {
        await using var f = await Fixture.Create("M");
        f.FailCommand = true;
        var failure = await Assert.ThrowsAsync<AppException>(() => f.Run());
        Assert.Equal("files.acl_failed", failure.MessageKey);
        Assert.Single(f.NativeCalls);
        Assert.Empty(f.Audits);
        Assert.Equal(f.NormalBefore, f.NormalAcl());
        Assert.Equal(f.OutsideBefore, f.OutsideAcl());
    }

    [Fact]
    public async Task UnprotectedOutsideDescriptorsAndExistingInheritancePolicyArePreserved()
    {
        await using var f = await Fixture.Create("M", "nested");
        f.EnableOutsideInheritance();
        var policy = f.NormalPolicy();
        var response = await f.Run();
        f.AssertNormalTreeChanged();
        Assert.Equal(f.OutsideBefore, f.OutsideAcl());
        Assert.Equal(policy, f.NormalPolicy());
        Assert.Contains("Skipped 1 link", response.Data!.PermissionsDetail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnIncompletePlanDoesNotBeginAnyPermissionMutation(bool expired)
    {
        await using var f = await Fixture.Create("M");
        f.Files.PermissionWalkBudget = () => new SafeDirectoryWalkBudget(expired ? 100 : 1, expired ? 0 : 5_000);
        Assert.Equal("files.acl_failed", (await Assert.ThrowsAsync<AppException>(() => f.Run())).MessageKey);
        Assert.Empty(f.NativeCalls); Assert.Empty(f.Audits);
        Assert.Equal(f.NormalBefore, f.NormalAcl());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationOrCancellationAfterOneEntryPreventsTheNextEntry(bool cancel)
    {
        await using var f = await Fixture.Create("M");
        f.AfterNative = async () => { if (cancel) f.Cancel(); else await f.Revoke(); };
        var error = await Record.ExceptionAsync(() => f.Run());
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Equal(401, Assert.IsType<AppException>(error).HttpStatusCode);
        Assert.Single(f.NativeCalls); Assert.Empty(f.Audits);
        f.AssertOnlyFirstEntryChanged();
        Assert.Equal(f.OutsideBefore, f.OutsideAcl());
    }

    [Fact]
    public async Task AFailureInTheSecondCommandStopsTheRestWithoutClaimingRollback()
    {
        await using var f = await Fixture.Create("M");
        f.FailAtCommand = 2;
        Assert.Equal("files.acl_failed", (await Assert.ThrowsAsync<AppException>(() => f.Run())).MessageKey);
        Assert.Equal(2, f.NativeCalls.Count); Assert.Empty(f.Audits);
        f.AssertOnlyFirstEntryChanged();
    }

    [Fact]
    public async Task APlannedChildReplacedAtItsFinalApprovalIsRejectedBeforeItsCommand()
    {
        await using var f = await Fixture.Create("M");
        f.AfterApprovalNumber = 4; // initial plan, root-before, root-after, child-before
        f.AfterApproval = () => f.ReplaceWithJunction(f.Ordinary);
        Assert.Equal("files.path_outside_root", (await Assert.ThrowsAsync<AppException>(() => f.Run())).MessageKey);
        Assert.Single(f.NativeCalls); Assert.Empty(f.Audits);
        Assert.Equal(f.OutsideBefore, f.OutsideAcl());
    }

    [Fact]
    public async Task TheExecutionBudgetAlsoBoundsTheNativeAwait()
    {
        await using var f = await Fixture.Create("M");
        // The total budget includes real SQLite/session approval before the fake transport.
        // Allow that work a generous margin; the fake then waits indefinitely for the real
        // linked deadline, and the outer bound still detects a missing native-await timeout.
        f.Files.PermissionExecutionTimeout = TimeSpan.FromSeconds(1);
        var entered = 0;
        f.Files.PermissionCommand = async (_, _, token) =>
        {
            entered++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable fake transport.");
        };
        var failure = await Assert.ThrowsAsync<AppException>(() => f.Run().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("files.acl_failed", failure.MessageKey);
        Assert.Equal(1, entered); Assert.Empty(f.Audits);
        Assert.Equal(f.NormalBefore, f.NormalAcl());
    }

    [Fact]
    public async Task OneUnmeasuredSummaryMakesTheAggregateUnmeasuredWithoutPublishingNativeText()
    {
        await using var f = await Fixture.Create("M");
        f.UnmeasuredAtCommand = 2;
        var response = await f.Run();
        f.AssertNormalTreeChanged();
        Assert.Null(response.Data!.PermissionsVerified);
        Assert.Equal("files.acl_unverified", Assert.Single(f.Audits));
        Assert.DoesNotContain("fixture-private-path", response.Data.PermissionsDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FiveHundredFilesUseTheActualGuardedPermissionFlow()
    {
        // This case launches 503 native commands; give the bulk fixture its own budget.
        // Individual command deadlines and all permission/boundary assertions stay unchanged.
        await using var f = await Fixture.Create("M", probeFiles: 500, fixtureTimeout: TimeSpan.FromMinutes(5));
        var clock = Stopwatch.StartNew();
        try { await f.Run(); }
        finally
        {
            clock.Stop();
            output.WriteLine("Actual guarded FileService ACL: 500 extra files, {0} commands, {1} ms.", f.NativeCalls.Count, clock.ElapsedMilliseconds);
        }
        f.AssertNormalTreeChanged();
        Assert.Equal(f.OutsideBefore, f.OutsideAcl());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Prefix = "adapanel-file-acl-boundary-";
        private const string Principal = "owned_acl_fixture";
        private readonly string root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource lifetime;
        private readonly CancellationTokenSource request;
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly AdaPanelDbContext db;
        private readonly ServiceProvider services;
        private readonly FilePublicationAuthorityTests.LocalGate gates = new();
        private readonly FilesController controller;
        private readonly FileService files;
        private readonly SessionRevocationStore sessions;
        private readonly string sessionId = SessionRevocationStore.NewSessionId();
        private readonly SecurityIdentifier currentSid;
        private readonly string rights, siteRoot, inside, outside, outsideFile, outsideChild, outsideNestedFile;
        private readonly Dictionary<string, byte[]> savedAccess = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> junctions = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> ordinaryPaths = [];
        private int approvals;
        internal readonly string Selected, Ordinary;
        internal string[] OutsideBefore { get; private set; } = [];
        internal string[] NormalBefore { get; private set; } = [];
        internal Func<Task>? AfterApproval;
        internal Func<Task>? AfterNative;
        internal bool FailCommand;
        internal int FailAtCommand, UnmeasuredAtCommand;
        internal int AfterApprovalNumber = 1;
        internal FileService Files => files;
        internal List<string> NativeCalls { get; } = [];
        internal List<string> Audits { get; } = [];

        private Fixture(string rights, TimeSpan? fixtureTimeout = null)
        {
            lifetime = new CancellationTokenSource(fixtureTimeout ?? TimeSpan.FromSeconds(90));
            this.rights = rights;
            using (var identity = WindowsIdentity.GetCurrent()) currentSid = identity.User!;
            Assert.NotNull(currentSid);
            request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            Directory.CreateDirectory(root); ValidateOwnedRoot(); SaveAccess(root);
            var rootAcl = ReadAccess(root);
            rootAcl.AddAccessRule(new FileSystemAccessRule(currentSid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            ApplyAccess(root, rootAcl);
            siteRoot = Path.Combine(root, "site"); Selected = Path.Combine(siteRoot, "selected");
            Ordinary = Path.Combine(Selected, "ordinary"); inside = Path.Combine(Ordinary, "inside.txt");
            outside = Path.Combine(root, "outside"); outsideFile = Path.Combine(outside, "sentinel.txt");
            outsideChild = Path.Combine(outside, "child"); outsideNestedFile = Path.Combine(outsideChild, "nested-sentinel.txt");
            Directory.CreateDirectory(Ordinary); Directory.CreateDirectory(outsideChild);
            File.WriteAllText(inside, "NORMAL"); File.WriteAllText(outsideFile, "OUTSIDE"); File.WriteAllText(outsideNestedFile, "OUTSIDE CHILD");
            ordinaryPaths.AddRange([Selected, Ordinary, inside]);
            foreach (var path in new[] { Selected, Ordinary, inside, outside, outsideFile, outsideChild, outsideNestedFile }) SaveAccess(path);

            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            var actor = Guid.NewGuid(); var siteName = "acl-" + Guid.NewGuid().ToString("N") + ".test";
            db.Users.Add(new PanelUser { Id = actor, UserName = "actor", Email = "actor@test.invalid", Role = UserRole.Administrator,
                IsActive = true, PasswordHash = "original" }); db.SaveChanges();
            var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(root, "panel") }));
            SiteDto Site() => new() { Id = 12, Name = siteName, PhysicalPath = siteRoot, ApplicationRootPath = siteRoot, ApplicationPoolName = siteName };
            var sites = Fake<IIisSiteService>((method, _) => method.Name switch
            {
                nameof(IIisSiteService.GetSiteAsync) => Task.FromResult(Site()),
                nameof(IIisSiteService.GetSitesAsync) => Task.FromResult<IReadOnlyCollection<SiteDto>>([Site()]),
                _ => throw new NotSupportedException(method.Name)
            });
            var scopes = new CustomerScopeService(store, Fake<IServicePlanService>(),
                Fake<IResellerQuotaService>((method, _) => method.Name == nameof(IResellerQuotaService.GetQuotaAsync)
                    ? Task.FromResult<ResellerQuotaDto?>(null) : throw new NotSupportedException(method.Name)), sites);
            sessions = new SessionRevocationStore(store);
            files = new FileService(sites) { PermissionCommand = NativeCommand };
            // Keep the actual controller's measured approval. This adapter only schedules a
            // filesystem change after its await, before the actual service resumes publication.
            var service = Fake<IFileService>((method, args) =>
            {
                Assert.Equal(nameof(IFileService.SetPermissionsAsync), method.Name); Assert.Equal(3, args.Length);
                var approve = (Func<SiteDto, CancellationToken, Task>)args[1]!;
                return files.SetPermissionsAsync((SetFilePermissionsRequest)args[0]!, async (site, token) =>
                {
                    await approve(site, token);
                    if (Interlocked.Increment(ref approvals) == AfterApprovalNumber && AfterApproval is not null) await AfterApproval();
                }, (CancellationToken)args[2]!);
            });
            var audit = Fake<IAuditService>((method, args) =>
            {
                Assert.True(method.Name is nameof(IAuditService.WriteAsync) or nameof(IAuditService.WriteForSubscriptionAsync));
                Audits.Add((string)args[0]!); return Task.CompletedTask;
            });
            services = new ServiceCollection().AddSingleton(db).AddSingleton(sessions)
                .AddSingleton<IIisSiteService>(sites).AddSingleton<IConcurrencyLock>(gates).BuildServiceProvider();
            controller = new FilesController(service, scopes, audit, new ConcurrentWorkGate())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services,
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new(ClaimTypes.NameIdentifier, actor.ToString()), new(ClaimTypes.Role, nameof(UserRole.Administrator)),
                        new(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("original")),
                        new(SessionRevocationStore.SessionIdClaim, sessionId)
                    ], CookieAuthenticationDefaults.AuthenticationScheme)) } }
            };
            PermissionRequest = new() { Path = $"sites/{siteName}/selected", Principal = Principal, Rights = rights };
        }
        private SetFilePermissionsRequest PermissionRequest { get; }

        internal static async Task<Fixture> Create(string rights, string? alias = null, int probeFiles = 0, TimeSpan? fixtureTimeout = null)
        {
            var f = new Fixture(rights, fixtureTimeout);
            try
            {
                for (var index = 0; index < probeFiles; index++)
                {
                    var path = Path.Combine(f.Ordinary, $"probe-{index:0000}.txt");
                    f.ValidateOwnedPath(path); File.WriteAllText(path, "PROBE");
                    f.SaveAccess(path); f.ordinaryPaths.Add(path);
                }
                if (alias is not null)
                {
                    var parent = f.Selected;
                    if (alias == "nested")
                    {
                        parent = Path.Combine(f.Selected, "nested"); Directory.CreateDirectory(parent);
                        f.SaveAccess(parent); f.ordinaryPaths.Add(parent);
                    }
                    await f.CreateJunction(Path.Combine(parent, "alias"));
                }
                // Build every path first. A new protected descriptor removes the fixture's
                // inherited current-SID FullControl; AddAccessRule alone would merge it with
                // this narrow rule and turn the positive native test into a no-op.
                foreach (var path in f.ordinaryPaths.Concat([f.outside, f.outsideFile, f.outsideChild, f.outsideNestedFile]))
                    f.SetCurrentAllow(path, FileSystemRights.ReadAttributes);
                foreach (var path in f.ordinaryPaths)
                    Assert.NotEqual(RequiredRights(rights), f.CurrentAllow(path) & RequiredRights(rights));
                f.NormalBefore = f.NormalAcl(); f.OutsideBefore = f.OutsideAcl();
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }

        internal async Task<ApiResponse<FileWorkspaceDto>> Run()
        {
            var response = await controller.SetPermissionsAsync(PermissionRequest, request.Token);
            return Assert.IsType<ApiResponse<FileWorkspaceDto>>(Assert.IsType<OkObjectResult>(response.Result).Value);
        }
        internal void Cancel() => request.Cancel();
        internal Task Revoke() => sessions.RevokeAsync(sessionId, lifetime.Token);
        internal string[] OutsideAcl() => new[] { outside, outsideFile, outsideChild, outsideNestedFile }.Select(AccessText).ToArray();
        internal string[] NormalAcl() => ordinaryPaths.Select(AccessText).ToArray();
        internal string[] NormalPolicy() => ordinaryPaths.Select(path =>
        {
            var security = ReadAccess(path);
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Where(rule => !rule.IdentityReference.Equals(currentSid) || rule.AccessControlType != AccessControlType.Allow)
                .Select(rule => $"{rule.IdentityReference.Value}:{rule.AccessControlType}:{rule.FileSystemRights}:{rule.InheritanceFlags}:{rule.PropagationFlags}:{rule.IsInherited}")
                .Order(StringComparer.Ordinal);
            return $"{security.AreAccessRulesProtected}:{string.Join("|", rules)}";
        }).ToArray();
        internal void EnableOutsideInheritance()
        {
            foreach (var path in new[] { outside, outsideFile, outsideChild, outsideNestedFile })
            {
                ValidateOwnedPath(path); var security = ReadAccess(path);
                security.SetAccessRuleProtection(false, true); ApplyAccess(path, security);
            }
            OutsideBefore = OutsideAcl();
        }
        internal void AssertOnlyFirstEntryChanged()
        {
            var current = NormalAcl();
            Assert.NotEqual(NormalBefore[0], current[0]);
            Assert.Equal(NormalBefore.Skip(1), current.Skip(1));
        }
        internal void AssertNormalTreeChanged()
        {
            Assert.NotEmpty(NativeCalls);
            Assert.NotEqual(NormalBefore, NormalAcl());
            foreach (var path in ordinaryPaths)
            {
                Assert.Equal(RequiredRights(rights), CurrentAllow(path) & RequiredRights(rights));
                // /grant without OI/CI must not add a policy for future children.
                Assert.All(ReadAccess(path).GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                    .Where(rule => rule.IdentityReference.Equals(currentSid) && rule.AccessControlType == AccessControlType.Allow),
                    rule => Assert.Equal(InheritanceFlags.None, rule.InheritanceFlags));
            }
        }
        private static FileSystemRights RequiredRights(string rights) => rights switch
        { "R" => FileSystemRights.Read, "RX" => FileSystemRights.ReadAndExecute, "W" => FileSystemRights.Write,
            "M" => FileSystemRights.Modify, "F" => FileSystemRights.FullControl, _ => throw new NotSupportedException(rights) };
        private FileSystemRights CurrentAllow(string path) => ReadAccess(path).GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Where(rule => rule.IdentityReference.Equals(currentSid) && rule.AccessControlType == AccessControlType.Allow
                && !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)).Aggregate((FileSystemRights)0, (value, rule) => value | rule.FileSystemRights);

        internal async Task ReplaceWithJunction(string path)
        {
            Assert.True(path == Selected || path == Ordinary);
            ValidateOwnedPath(path);
            // Preparation is reversible and confined to the owned parent. Do not rely on an
            // elevated Administrators membership after the target's initial narrow DACL.
            var parent = Path.GetDirectoryName(path)!; var previousParent = ReadAccess(parent).GetSecurityDescriptorBinaryForm();
            SetCurrentAllow(parent, FileSystemRights.FullControl);
            try
            {
                var detached = Path.Combine(root, "detached-" + Guid.NewGuid().ToString("N"));
                ValidateOwnedPath(detached); Directory.Move(path, detached);
                foreach (var pair in savedAccess.Where(pair => PathContainment.IsRootOrInside(path, pair.Key)).ToArray())
                    savedAccess[Path.GetFullPath(Path.Combine(detached, Path.GetRelativePath(path, pair.Key)))] = pair.Value;
                await CreateJunction(path);
            }
            finally
            {
                var security = ReadAccess(parent); security.SetSecurityDescriptorBinaryForm(previousParent, AccessControlSections.Access); ApplyAccess(parent, security);
            }
        }
        private async Task CreateJunction(string path)
        {
            ValidateOwnedPath(path); ValidateOwnedPath(outside);
            Assert.True(PathContainment.IsRootOrInside(Selected, path));
            var info = new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") };
            info.ArgumentList.Add("/c"); info.ArgumentList.Add("mklink"); info.ArgumentList.Add("/J"); info.ArgumentList.Add(path); info.ArgumentList.Add(outside);
            var result = await RunProcess(info, lifetime.Token); Assert.True(result.Success, result.StandardError);
            junctions.Add(path);
            Assert.Equal(outside, Directory.ResolveLinkTarget(path, true)!.FullName, StringComparer.OrdinalIgnoreCase);
        }
        private async Task<CommandResult> NativeCommand(string executable, string arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); ValidateOwnedRoot();
            Assert.Equal("icacls.exe", executable);
            Assert.StartsWith("\"", arguments, StringComparison.Ordinal);
            var end = arguments.IndexOf('"', 1); Assert.True(end > 1);
            var path = arguments[1..end]; Assert.Contains(path, ordinaryPaths);
            Assert.Equal($"\"{path}\" /grant \"{Principal}:{rights}\" /C /L", arguments);
            ValidateOwnedPath(path); Assert.Equal((FileAttributes)0, File.GetAttributes(path) & FileAttributes.ReparsePoint);
            foreach (var link in junctions)
                Assert.Equal(outside, Directory.ResolveLinkTarget(link, true)!.FullName, StringComparer.OrdinalIgnoreCase);
            NativeCalls.Add(arguments);
            if (FailCommand || FailAtCommand == NativeCalls.Count) return new CommandResult { ExitCode = 5, StandardError = "Fixture native failure" };
            var info = new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "icacls.exe") };
            info.ArgumentList.Add(path); info.ArgumentList.Add("/grant"); info.ArgumentList.Add("*" + currentSid.Value + ":" + rights);
            info.ArgumentList.Add("/C"); info.ArgumentList.Add("/L");
            var result = await RunProcess(info, token);
            Assert.True(result.Success, result.StandardOutput + "\n" + result.StandardError);
            if (AfterNative is not null) await AfterNative();
            if (UnmeasuredAtCommand == NativeCalls.Count)
                return new CommandResult { ExitCode = 0, StandardOutput = "Unknown output: fixture-private-path" };
            return result;
        }
        private async Task<CommandResult> RunProcess(ProcessStartInfo info, CancellationToken token)
        {
            info.UseShellExecute = false; info.CreateNoWindow = true;
            info.RedirectStandardOutput = true; info.RedirectStandardError = true;
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); bound.CancelAfter(TimeSpan.FromSeconds(15));
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Fixture process did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(bound.Token); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); throw; }
            return new CommandResult { ExitCode = process.ExitCode, StandardOutput = await stdout, StandardError = await stderr };
        }

        private void ValidateOwnedRoot()
        {
            var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(full), StringComparer.OrdinalIgnoreCase);
            Assert.StartsWith(Prefix, Path.GetFileName(full), StringComparison.Ordinal);
            Assert.Equal((FileAttributes)0, File.GetAttributes(full) & FileAttributes.ReparsePoint);
        }
        private void ValidateOwnedPath(string path)
        {
            ValidateOwnedRoot(); Assert.True(PathContainment.IsRootOrInside(root, Path.GetFullPath(path)));
            for (var current = Path.GetDirectoryName(Path.GetFullPath(path)); current is not null && PathContainment.IsRootOrInside(root, current); current = Path.GetDirectoryName(current))
                Assert.Equal((FileAttributes)0, File.GetAttributes(current) & FileAttributes.ReparsePoint);
        }
        private static FileSystemSecurity ReadAccess(string path) => Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access) : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        private static string AccessText(string path) => ReadAccess(path).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        private void SaveAccess(string path) { ValidateOwnedPath(path); savedAccess[path] = ReadAccess(path).GetSecurityDescriptorBinaryForm(); }
        private void SetCurrentAllow(string path, FileSystemRights rights)
        {
            ValidateOwnedPath(path); var directory = Directory.Exists(path);
            var rules = ReadAccess(path).GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var rule in rules.Where(rule => !rule.IdentityReference.Equals(currentSid) || rule.AccessControlType != AccessControlType.Allow))
                security.AddAccessRule(new FileSystemAccessRule(rule.IdentityReference, rule.FileSystemRights, rule.InheritanceFlags, rule.PropagationFlags, rule.AccessControlType));
            security.AddAccessRule(new FileSystemAccessRule(currentSid, rights, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            ApplyAccess(path, security);
        }
        private static void ApplyAccess(string path, FileSystemSecurity security)
        {
            if (security is DirectorySecurity directory) new DirectoryInfo(path).SetAccessControl(directory);
            else new FileInfo(path).SetAccessControl((FileSecurity)security);
        }

        public ValueTask DisposeAsync()
        {
            request.Cancel(); ValidateOwnedRoot();
            foreach (var link in junctions.OrderByDescending(path => path.Length))
            {
                ValidateOwnedPath(link); Assert.Equal(FileAttributes.ReparsePoint, File.GetAttributes(link) & FileAttributes.ReparsePoint);
                Assert.Equal(outside, Directory.ResolveLinkTarget(link, true)!.FullName, StringComparer.OrdinalIgnoreCase);
                // The fixture deliberately removed DeleteChild from this SID. Restore only
                // its owned parent's noninheritable setup access before unlinking the alias.
                SetCurrentAllow(Path.GetDirectoryName(link)!, FileSystemRights.FullControl);
                Directory.Delete(link); // Unlink only. Never recurse into the junction destination.
            }
            foreach (var pair in savedAccess.OrderByDescending(pair => pair.Key.Length))
            {
                if (!Path.Exists(pair.Key)) continue;
                ValidateOwnedPath(pair.Key); Assert.Equal((FileAttributes)0, File.GetAttributes(pair.Key) & FileAttributes.ReparsePoint);
                var security = ReadAccess(pair.Key); security.SetSecurityDescriptorBinaryForm(pair.Value, AccessControlSections.Access); ApplyAccess(pair.Key, security);
            }
            services.Dispose(); db.Dispose(); connection.Dispose(); gates.Dispose();
            ValidateOwnedRoot();
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var directory))
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    Assert.Equal((FileAttributes)0, entry.Attributes & FileAttributes.ReparsePoint);
                    if (entry is DirectoryInfo) pending.Push(entry.FullName);
                }
            Directory.Delete(root, recursive: true);
            request.Dispose(); lifetime.Dispose(); return ValueTask.CompletedTask;
        }
    }
    private static T Fake<T>(Func<MethodInfo, object?[], object?>? handler = null) where T : class
    { var result = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)result).Handler = handler ?? ((method, _) => throw new NotSupportedException(method.Name)); return result; }
    public class Proxy : DispatchProxy
    { public Func<MethodInfo, object?[], object?> Handler = null!; protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args ?? []); }
}
