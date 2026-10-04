using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Dtos.Settings;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Ftp;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;
using Xunit.Abstractions;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Real icacls, restricted to a unique temporary fixture and the existing process SID.
/// Account/IIS operations are fake. This measures junction behavior instead of inferring
/// it from the icacls documentation's description of /L for symbolic links.
/// </summary>
public sealed class FtpRecursivePermissionBoundaryTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RecursivePermissionChangesLeaveADescendantJunctionDestinationUntouched(bool rollback, bool nested)
    {
        await using var f = await Fixture.Create(junction: true, nested: nested);
        var outside = f.OutsideAcl();
        await f.Run(rollback);
        if (!rollback) f.ReportGrantMeasurement(output);
        Assert.Equal(rollback ? f.ExpectedCommandCount : 1, f.NativeCommands.Count);
        f.AssertNormalTreeChanged(rollback);
        Assert.Equal(outside, f.OutsideAcl());
        Assert.Equal("outside sentinel", await File.ReadAllTextAsync(f.Sentinel, f.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryDescendantsStillReceiveTheRequestedPermissionChange(bool rollback)
    {
        await using var f = await Fixture.Create();
        var outside = f.OutsideAcl();
        await f.Run(rollback);
        if (!rollback) f.ReportGrantMeasurement(output);
        Assert.Equal(rollback ? f.ExpectedCommandCount : 1, f.NativeCommands.Count);
        f.AssertNormalTreeChanged(rollback);
        Assert.Equal(outside, f.OutsideAcl());
    }

    [Fact]
    public async Task FailedCreationRecoversOnlyTheOrdinaryHomeTree()
    {
        await using var f = await Fixture.Create(junction: true);
        f.FailHomeConfiguration = true;
        var outside = f.OutsideAcl();
        var records = await f.RecordBytes();
        var error = await Assert.ThrowsAsync<AppException>(() => f.Run(rollback: false));
        Assert.Equal("ftp.site_configuration_failed", error.MessageKey);
        Assert.Equal(2, f.NativeCommands.Count); // Only the attempted Home grant is recovered.
        Assert.Equal(outside, f.OutsideAcl());
        Assert.Equal(records, await f.RecordBytes());
        Assert.Equal("outside sentinel", await File.ReadAllTextAsync(f.Sentinel, f.Token));
    }

    [Fact]
    public async Task ACancelledRequestDoesNotLaunchAnyPermissionCommand()
    {
        await using var f = await Fixture.Create(junction: true);
        var outside = f.OutsideAcl();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.CreateAccount(cancellation.Token));
        Assert.Empty(f.NativeCommands);
        Assert.Equal(outside, f.OutsideAcl());
    }

    [Fact]
    [Trait("Category", "NativePerformanceProbe")]
    public async Task FiveHundredOrdinaryFilesMeasureActualPermissionExecutionTime()
    {
        await using var f = await Fixture.Create(ordinaryFiles: 500);
        // The fixture already contains one ordinary file. Setup and ACL restoration
        // are outside the measurement; every timed command uses the same safe whitelist.
        var outside = f.OutsideAcl();
        var elapsed = Stopwatch.StartNew();
        try { await f.Run(rollback: false); }
        finally
        {
            elapsed.Stop();
            output.WriteLine($"FTP_ACL_TIMING files=500 commands={f.NativeCommands.Count} elapsed_ms={elapsed.Elapsed.TotalMilliseconds:F0}");
        }
        Assert.Equal(502, f.ExpectedCommandCount);
        Assert.Single(f.NativeCommands); // Root inheritance supplies M; all 500 files are measured.
        f.AssertNormalTreeChanged(rollback: false);
        Assert.Equal(outside, f.OutsideAcl());
    }

    [Fact]
    public async Task AProtectedChildStillGetsANativeGrantAndPreservesTheOutsideTree()
    {
        await using var f = await Fixture.Create(junction: true);
        f.ProtectOrdinaryDirectory();
        var outside = f.OutsideAcl();
        await f.Run(rollback: false);
        Assert.Equal(2, f.NativeCommands.Count);
        f.AssertNormalTreeChanged(rollback: false);
        Assert.Equal(outside, f.OutsideAcl());
    }

    [Fact]
    public async Task ExistingInheritedFullControlNeedsNoAdditionalGrant()
    {
        await using var f = await Fixture.Create(junction: true);
        f.AddHomeAccess(FileSystemRights.FullControl);
        var before = f.AllHomeAcl();
        var outside = f.OutsideAcl();
        await f.Run(rollback: false);
        Assert.Empty(f.NativeCommands);
        Assert.Equal(before, f.AllHomeAcl());
        Assert.Equal(outside, f.OutsideAcl());
    }

    [Fact]
    public async Task FailedHomeConfigurationCleansHomeButPreservesAlreadyValidPermissions()
    {
        await using var f = await Fixture.Create(junction: true);
        f.AddHomeAccess(FileSystemRights.Modify);
        f.FailHomeConfiguration = true;
        var before = f.AllHomeAcl();
        var records = await f.RecordBytes();
        var error = await Assert.ThrowsAsync<AppException>(() => f.Run(rollback: false));
        Assert.Equal("ftp.site_configuration_failed", error.MessageKey);
        Assert.Equal(1, f.HomeCleanupCalls);
        Assert.Empty(f.NativeCommands);
        Assert.Equal(before, f.AllHomeAcl());
        Assert.Equal(records, await f.RecordBytes());
    }

    [Fact]
    public async Task FailedMixedGrantPreservesTheSkippedHomesExistingPermission()
    {
        await using var f = await Fixture.Create(junction: true);
        f.AddHomeAccess(FileSystemRights.Modify);
        f.ProtectOrdinaryDirectory();
        f.FailHomeConfiguration = true;
        var homeBefore = f.HomeAcl();
        var outside = f.OutsideAcl();
        var records = await f.RecordBytes();
        var error = await Assert.ThrowsAsync<AppException>(() => f.Run(rollback: false));
        Assert.Equal("ftp.site_configuration_failed", error.MessageKey);
        Assert.Equal(2, f.NativeCommands.Count); // Protected child grant + only its recovery.
        Assert.DoesNotContain(f.NativeCommands, command => command.StartsWith($"\"{f.Home}\" ", StringComparison.Ordinal));
        Assert.Equal(1, f.HomeCleanupCalls);
        Assert.Equal(homeBefore, f.HomeAcl());
        Assert.Equal(outside, f.OutsideAcl());
        Assert.Equal(records, await f.RecordBytes());
    }

    [Theory]
    [InlineData(false, false, false, false, false, false, false)]
    [InlineData(true, false, false, false, false, false, true)]
    [InlineData(true, true, false, false, false, false, false)]
    [InlineData(true, true, true, false, false, false, true)]
    [InlineData(true, true, false, true, false, false, true)]
    [InlineData(true, true, false, false, true, false, true)]
    [InlineData(true, true, false, false, false, true, true)]
    public void OnlyAnApplicableCompleteExactIdentityGrantCanSkip(bool directory, bool inherit,
        bool inheritOnly, bool noPropagate, bool unrelated, bool deny, bool expectedChange)
    {
        var identity = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
        security.AddAccessRule(new FileSystemAccessRule(unrelated
                ? new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) : identity,
            FileSystemRights.Modify, inherit ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
            inheritOnly ? PropagationFlags.InheritOnly : noPropagate ? PropagationFlags.NoPropagateInherit : PropagationFlags.None,
            deny ? AccessControlType.Deny : AccessControlType.Allow));
        var before = security.GetSecurityDescriptorBinaryForm();
        Assert.Equal(expectedChange, FtpService.HomePermissionChangeNeeded(security, identity, directory, grant: true));
        Assert.Equal(before, security.GetSecurityDescriptorBinaryForm());
    }

    [Fact]
    public void RecoverySkipsDenyInheritedAndUnrelatedRulesWithoutChangingThem()
    {
        var identity = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm($"D:(D;;WD;;;{identity.Value})(A;;FA;;;SY)(A;ID;FA;;;{identity.Value})");
        var before = security.GetSecurityDescriptorBinaryForm();
        Assert.False(FtpService.HomePermissionChangeNeeded(security, identity, isDirectory: false, grant: false));
        Assert.Equal(before, security.GetSecurityDescriptorBinaryForm());
        security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.Read, AccessControlType.Allow));
        Assert.True(FtpService.HomePermissionChangeNeeded(security, identity, isDirectory: false, grant: false));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Prefix = "adapanel-ftp-recursive-acl-";
        private const string UserName = "acl_fixture";
        private readonly string root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(40));
        private readonly SecurityIdentifier currentSid = CurrentProcessSid();
        private readonly Dictionary<string, byte[]> savedAccess = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> allowedAclPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly JsonFileStore store;
        private readonly FtpService service;
        private readonly string normalFile, link, outsideDirectory, outsideFile;
        private readonly string normalAccessBefore, homeAccessBefore, normalRulesBefore, homeRulesBefore;
        private readonly bool hasJunction;
        internal readonly string Home, Outside, Sentinel;
        internal readonly List<string> NativeCommands = [];
        internal bool FailHomeConfiguration;
        internal int HomeCleanupCalls;
        internal CancellationToken Token => deadline.Token;
        internal int ExpectedCommandCount => allowedAclPaths.Count;

        private Fixture(bool junction, bool nested, int ordinaryFiles)
        {
            Directory.CreateDirectory(root);
            ValidateOwnedRoot();
            SaveAccess(root);
            // Only this newly created root is changed. Its inheritable existing-user access
            // keeps the fixture usable even while /remove:g removes explicit child grants.
            AddCurrentAccess(root, FileSystemRights.FullControl);
            Home = Path.Combine(root, "home");
            Outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(Home);
            Directory.CreateDirectory(Outside);
            var normalDirectory = Path.Combine(Home, "ordinary");
            Directory.CreateDirectory(normalDirectory);
            normalFile = Path.Combine(normalDirectory, "inside.txt");
            Sentinel = Path.Combine(Outside, "sentinel.txt");
            outsideDirectory = Path.Combine(Outside, "ordinary-descendant");
            outsideFile = Path.Combine(outsideDirectory, "nested-sentinel.txt");
            Directory.CreateDirectory(outsideDirectory);
            File.WriteAllText(normalFile, "inside sentinel");
            File.WriteAllText(Sentinel, "outside sentinel");
            File.WriteAllText(outsideFile, "outside nested sentinel");
            var linkParent = nested ? Path.Combine(Home, "nested") : Home;
            Directory.CreateDirectory(linkParent);
            link = Path.Combine(linkParent, "alias");
            hasJunction = junction;
            allowedAclPaths.UnionWith([Home, normalDirectory, normalFile, linkParent]);
            foreach (var path in new[] { Home, Outside, normalDirectory, normalFile, Sentinel, outsideDirectory, outsideFile, linkParent }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                SaveAccess(path);
                AddCurrentAccess(path, FileSystemRights.ReadAndExecute);
            }
            for (var index = 1; index < ordinaryFiles; index++)
            {
                Token.ThrowIfCancellationRequested();
                var path = Path.Combine(normalDirectory, $"timing-{index:D4}.txt");
                ValidateOwnedPath(path);
                File.WriteAllText(path, "timing fixture");
                SaveAccess(path);
                AddCurrentAccess(path, FileSystemRights.ReadAndExecute);
                Assert.True(allowedAclPaths.Add(path));
            }
            // The root keeps our cleanup rights. Only this owned Home tree starts without
            // a current-SID Modify/FullControl grant, so the native grant is really needed.
            // Other principals and Outside are unchanged; child RX entries remain intact.
            var originalRules = new DirectoryInfo(Home).GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            // Build an explicit protected descriptor: preserving then removing inherited
            // ACEs in one in-memory descriptor can leave the inherited FullControl ACE.
            // Keep every other identity/type/rule, and replace only our fixture SID Allow.
            var homeSecurity = new DirectorySecurity();
            homeSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var rule in originalRules.Where(rule => !rule.IdentityReference.Equals(currentSid)
                || rule.AccessControlType != AccessControlType.Allow))
                homeSecurity.AddAccessRule(new FileSystemAccessRule(rule.IdentityReference, rule.FileSystemRights,
                    rule.InheritanceFlags, rule.PropagationFlags, rule.AccessControlType));
            homeSecurity.AddAccessRule(new FileSystemAccessRule(currentSid, FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(Home).SetAccessControl(homeSecurity);
            homeAccessBefore = AccessText(Home);
            normalAccessBefore = AccessText(normalFile);
            homeRulesBefore = DescribeCurrentRules(Home);
            normalRulesBefore = DescribeCurrentRules(normalFile);
            var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root });
            store = new JsonFileStore(options);
            var site = new SiteDto { Id = 142, Name = "fixture.test", PhysicalPath = Home, ApplicationPoolName = "fixture-pool" };
            service = new FtpService(Fake<IIisSiteService>((method, _) => method.Name switch
            {
                "GetSiteAsync" => Task.FromResult(site),
                "GetSitesAsync" => Task.FromResult<IReadOnlyCollection<SiteDto>>([site]),
                _ => throw new NotSupportedException(method.Name)
            }), store, Fake<IPanelPreferencesService>((method, _) => method.Name == "GetAsync"
                ? Task.FromResult(new PanelPreferencesDto()) : throw new NotSupportedException(method.Name)), options)
            {
                NativeAccountStatus = (name, token) =>
                { token.ThrowIfCancellationRequested(); Assert.Equal(UserName, name); return Task.FromResult(0u); },
                NativeCommand = NativeCommand,
                HomePermissionIdentity = name => { Assert.Equal(UserName, name); return currentSid; },
                NativeScript = NativeScript
            };
        }

        internal static async Task<Fixture> Create(bool junction = false, bool nested = false, int ordinaryFiles = 1)
        {
            var f = new Fixture(junction, nested, ordinaryFiles);
            try
            {
                Assert.True(FtpService.HomePermissionChangeNeeded(ReadAccess(f.Home), f.currentSid, isDirectory: true, grant: true),
                    "Owned Home must really start without an applicable Modify grant.");
                await f.store.WriteAsync("config/ftp-infra.json", new { configured = true }, f.Token);
                await f.store.WriteAsync("config/ftp-users.json", new[] { new
                {
                    userName = UserName, siteName = "fixture.test", homePath = f.Home,
                    isEnabled = true, createdAtUtc = DateTime.UtcNow.AddDays(-1)
                } }, f.Token);
                if (junction)
                {
                    f.ValidateOwnedPath(f.link);
                    f.ValidateOwnedPath(f.Outside);
                    var result = await f.RunProcess(new ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                        Arguments = $"/c mklink /J \"{f.link}\" \"{f.Outside}\""
                    }, f.Token);
                    Assert.True(result.Success, "The fixture-owned junction could not be created.");
                    Assert.Equal(f.Outside, Directory.ResolveLinkTarget(f.link, true)!.FullName, StringComparer.OrdinalIgnoreCase);
                }
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }

        internal Task<FtpUserDto> CreateAccount(CancellationToken token) => service.CreateFtpUserAsync(
            new CreateFtpUserRequest { UserName = UserName, Password = "FixturePassword123!", SiteName = "fixture.test", HomePath = Home }, token);

        internal async Task Run(bool rollback)
        {
            if (rollback)
                Assert.Equal(string.Empty, await service.RollbackCreateAsync(UserName, Home, hesabiBuCagriActi: false));
            else
                Assert.Equal(Home, (await CreateAccount(Token)).HomePath);
        }

        internal Task<byte[]> RecordBytes() => File.ReadAllBytesAsync(store.ResolvePath("config/ftp-users.json"), Token);
        internal string[] OutsideAcl() => [AccessText(Outside), AccessText(Sentinel), AccessText(outsideDirectory), AccessText(outsideFile)];
        internal string[] AllHomeAcl() => allowedAclPaths.Order(StringComparer.OrdinalIgnoreCase).Select(AccessText).ToArray();
        internal string HomeAcl() => AccessText(Home);
        internal void AddHomeAccess(FileSystemRights rights) => AddCurrentAccess(Home, rights);
        internal void ProtectOrdinaryDirectory()
        {
            var path = Path.GetDirectoryName(normalFile)!;
            ValidateOwnedPath(path);
            var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            new DirectoryInfo(path).SetAccessControl(security);
        }

        internal void ReportGrantMeasurement(ITestOutputHelper output)
        {
            // Only rights/flags for the existing process identity are logged, not the SID.
            output.WriteLine("Home before: " + homeRulesBefore);
            output.WriteLine("Home after: " + DescribeCurrentRules(Home));
            output.WriteLine("Ordinary file before: " + normalRulesBefore);
            output.WriteLine("Ordinary file after: " + DescribeCurrentRules(normalFile));
        }

        internal void AssertNormalTreeChanged(bool rollback)
        {
            if (rollback) Assert.DoesNotContain(CurrentRules(normalFile), rule => !rule.IsInherited);
            else
            {
                // (OI)(CI) grants propagate to existing files. The file's pre-existing
                // explicit RX entry need not turn into M: measure the new inherited ACE,
                // plus actual before/after changes, rather than matching inherited F.
                Assert.NotEqual(homeAccessBefore, AccessText(Home));
                Assert.Contains(CurrentRules(Home), rule => !rule.IsInherited && IsModifyGrant(rule));
                Assert.NotEqual(normalAccessBefore, AccessText(normalFile));
                Assert.Contains(CurrentRules(normalFile), rule => rule.IsInherited && IsModifyGrant(rule));
            }
        }

        private FileSystemAccessRule[] CurrentRules(string path) => ReadAccess(path)
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Where(rule => rule.IdentityReference.Equals(currentSid)
                && rule.AccessControlType == AccessControlType.Allow).ToArray();

        private static bool IsModifyGrant(FileSystemAccessRule rule) =>
            (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify &&
            (rule.FileSystemRights & (FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) == 0;

        private string DescribeCurrentRules(string path) => string.Join("; ", CurrentRules(path)
            .Select(rule => $"{(rule.IsInherited ? "inherited" : "explicit")} {rule.FileSystemRights} {rule.InheritanceFlags} {rule.PropagationFlags}"));

        private async Task<CommandResult> NativeCommand(string executable, string arguments, CancellationToken token)
        {
            // No process is launched unless this is an exact ordinary fixture-entry ACL
            // command. Only the fake account identity is replaced with the current SID.
            Assert.Equal("icacls.exe", executable);
            ValidateOwnedRoot();
            ValidateFixtureLinks();
            Assert.StartsWith("\"", arguments, StringComparison.Ordinal);
            var quoteEnd = arguments.IndexOf('"', 1);
            Assert.True(quoteEnd > 1);
            var path = arguments[1..quoteEnd];
            Assert.Contains(path, allowedAclPaths);
            ValidateOwnedPath(path);
            Assert.Equal((FileAttributes)0, File.GetAttributes(path) & FileAttributes.ReparsePoint);
            var rights = Directory.Exists(path) ? "(OI)(CI)M" : "M";
            var grant = arguments == $"\"{path}\" /grant \"{UserName}:{rights}\" /C /L";
            var remove = arguments == $"\"{path}\" /remove:g \"{UserName}\" /C /L";
            Assert.True(grant || remove, "Unexpected ACL command; no native process was launched.");
            token.ThrowIfCancellationRequested();
            var info = new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "icacls.exe") };
            info.ArgumentList.Add(path);
            info.ArgumentList.Add(grant ? "/grant" : "/remove:g");
            info.ArgumentList.Add("*" + currentSid.Value + (grant ? ":" + rights : string.Empty));
            info.ArgumentList.Add("/C"); info.ArgumentList.Add("/L");
            NativeCommands.Add(arguments);
            var result = await RunProcess(info, token);
            Assert.True(result.Success, "The fixture-only ACL command failed.");
            return result;
        }

        private Task<CommandResult> NativeScript(string script, bool strict, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (script.StartsWith("if (Get-Service FTPSVC", StringComparison.Ordinal)) return Result("present");
            if (script.StartsWith("if ((Get-Service FTPSVC", StringComparison.Ordinal)) return Result("up");
            if (script.Contains("Set-LocalUser", StringComparison.Ordinal)) return Result("ADAPANEL-SURESIZ");
            Assert.True(strict);
            if (script.Contains("foreach ($userRoot", StringComparison.Ordinal) && !script.Contains("Add-WebConfiguration", StringComparison.Ordinal)) HomeCleanupCalls++;
            var failed = FailHomeConfiguration && script.Contains("Add-WebConfiguration", StringComparison.Ordinal);
            return Task.FromResult(new CommandResult { ExitCode = failed ? 1 : 0 });
        }

        private static Task<CommandResult> Result(string output) => Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = output });

        private static SecurityIdentifier CurrentProcessSid()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User ?? throw new InvalidOperationException("The process user SID is unavailable.");
        }

        private async Task<CommandResult> RunProcess(ProcessStartInfo info, CancellationToken token)
        {
            info.UseShellExecute = false; info.CreateNoWindow = true;
            info.RedirectStandardOutput = true; info.RedirectStandardError = true;
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(token, Token);
            bound.CancelAfter(TimeSpan.FromSeconds(15));
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Fixture process did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(bound.Token); }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            return new CommandResult { ExitCode = process.ExitCode, StandardOutput = await output, StandardError = await error };
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
            ValidateOwnedRoot();
            Assert.True(PathContainment.IsRootOrInside(root, Path.GetFullPath(path)));
            for (var current = Path.GetDirectoryName(Path.GetFullPath(path)); current is not null && PathContainment.IsRootOrInside(root, current); current = Path.GetDirectoryName(current))
                Assert.Equal((FileAttributes)0, File.GetAttributes(current) & FileAttributes.ReparsePoint);
        }

        private void ValidateFixtureLinks()
        {
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var directory))
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        Assert.True(hasJunction);
                        Assert.Equal(link, entry.FullName, StringComparer.OrdinalIgnoreCase);
                        Assert.Equal(Outside, Directory.ResolveLinkTarget(link, true)!.FullName, StringComparer.OrdinalIgnoreCase);
                    }
                    else if (entry is DirectoryInfo) pending.Push(entry.FullName);
                }
        }

        private void SaveAccess(string path)
        {
            ValidateOwnedPath(path);
            savedAccess[path] = ReadAccess(path).GetSecurityDescriptorBinaryForm();
        }

        private static FileSystemSecurity ReadAccess(string path) => Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access);

        private static string AccessText(string path) => ReadAccess(path).GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        private void AddCurrentAccess(string path, FileSystemRights rights)
        {
            ValidateOwnedPath(path);
            var security = ReadAccess(path);
            security.AddAccessRule(new FileSystemAccessRule(currentSid, rights,
                Directory.Exists(path) ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
                PropagationFlags.None, AccessControlType.Allow));
            ApplyAccess(path, security);
        }

        private static void ApplyAccess(string path, FileSystemSecurity security)
        {
            if (security is DirectorySecurity directory) new DirectoryInfo(path).SetAccessControl(directory);
            else new FileInfo(path).SetAccessControl((FileSecurity)security);
        }

        public ValueTask DisposeAsync()
        {
            deadline.Cancel();
            ValidateOwnedRoot();
            if (Directory.Exists(link))
            {
                Assert.Equal(FileAttributes.ReparsePoint, File.GetAttributes(link) & FileAttributes.ReparsePoint);
                Directory.Delete(link); // Unlink only; never recurse into the destination.
            }
            foreach (var pair in savedAccess.OrderByDescending(pair => pair.Key.Length))
            {
                ValidateOwnedPath(pair.Key);
                var security = ReadAccess(pair.Key);
                security.SetSecurityDescriptorBinaryForm(pair.Value, AccessControlSections.Access);
                ApplyAccess(pair.Key, security);
            }
            ValidateOwnedRoot();
            ValidateFixtureLinks();
            Directory.Delete(root, recursive: true);
            deadline.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static T Fake<T>(Func<MethodInfo, object?[], object?> action) where T : class
    { var value = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)value).Action = action; return value; }
    public class Proxy : DispatchProxy
    { public Func<MethodInfo, object?[], object?> Action = null!; protected override object? Invoke(MethodInfo? method, object?[]? args) => Action(method!, args ?? []); }
}
