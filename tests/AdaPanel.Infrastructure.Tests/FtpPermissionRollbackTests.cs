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

namespace AdaPanel.Infrastructure.Tests;

/// <summary>Real ACL changes only inside a guarded unique temp root, for the current SID.
/// SAM, passwords, FTP links and IIS are instance fakes. No live account is created.</summary>
public sealed class FtpPermissionRollbackTests
{
    [Theory]
    [InlineData("home", false)]
    [InlineData("home", true)]
    [InlineData("file", false)]
    [InlineData("file", true)]
    [InlineData("directory", false)]
    [InlineData("directory", true)]
    public async Task FailurePreservesTheExistingAccountsPriorPermissions(string target, bool cancel)
    {
        await using var f = await Fixture.Create(target);
        var before = f.TargetAcl();
        var records = await f.RecordBytes();
        f.CancelAtConfiguration = cancel;
        await f.ExpectFailure(cancel);
        Assert.Equal(before, f.TargetAcl());
        Assert.Equal(records, await f.RecordBytes());
        Assert.Equal(1, f.HomeCleanupCalls);
        Assert.Equal(f.OutsideBefore, f.OutsideAcl());
    }

    [Theory]
    [InlineData("home")]
    [InlineData("file")]
    public async Task ConcurrentUnrelatedCurrentSidBitsSurviveRecovery(string target)
    {
        await using var f = await Fixture.Create(target);
        f.AtConfiguration = () => { f.AddTargetAccess(f.CurrentSid, FileSystemRights.ChangePermissions, AccessControlType.Allow); return Task.CompletedTask; };
        await f.ExpectFailure();
        var rights = f.ExplicitCurrentAllow();
        Assert.Equal(FileSystemRights.ReadAndExecute, rights & FileSystemRights.ReadAndExecute);
        Assert.Equal(FileSystemRights.ChangePermissions, rights & FileSystemRights.ChangePermissions);
        Assert.Equal((FileSystemRights)0, rights & FileSystemRights.WriteData);
        Assert.Equal(f.OutsideBefore, f.OutsideAcl());
    }

    [Fact]
    public async Task ConcurrentOtherIdentityAndDenyRulesAreNotReplacedByAnOldWholeDescriptor()
    {
        await using var f = await Fixture.Create("file");
        var originalRules = f.UnrelatedRules();
        string[]? concurrentRules = null;
        f.AtConfiguration = () =>
        {
            f.ChangeExistingSystemRule();
            f.AddTargetAccess(f.CurrentSid, FileSystemRights.Delete, AccessControlType.Deny);
            concurrentRules = f.UnrelatedRules();
            Assert.NotEqual(originalRules, concurrentRules);
            return Task.CompletedTask;
        };
        await f.ExpectFailure();
        Assert.Equal(concurrentRules, f.UnrelatedRules());
        Assert.Equal(FileSystemRights.ReadAndExecute, f.ExplicitCurrentAllow() & FileSystemRights.ReadAndExecute);
        Assert.Equal((FileSystemRights)0, f.ExplicitCurrentAllow() & FileSystemRights.WriteData);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    public async Task SamePathReplacementDoesNotReceiveTheOldObjectsRecovery(string target)
    {
        await using var f = await Fixture.Create(target);
        string? replacementAcl = null;
        f.AtConfiguration = () => { f.ReplaceTarget(); replacementAcl = f.TargetAcl(); return Task.CompletedTask; };
        await f.ExpectFailure();
        Assert.Equal(replacementAcl, f.TargetAcl());
        Assert.Equal("replacement generation", File.ReadAllText(f.ReplacementSentinel));
        Assert.Equal(1, f.HomeCleanupCalls);
        Assert.Equal(f.OutsideBefore, f.OutsideAcl());
    }

    [Fact]
    public async Task GoneAttemptedFileDoesNotPreventOwnedHomeCleanup()
    {
        await using var f = await Fixture.Create("file");
        f.AtConfiguration = () => { f.RemoveTarget(); return Task.CompletedTask; };
        await f.ExpectFailure();
        Assert.False(File.Exists(f.Target));
        Assert.Equal(1, f.HomeCleanupCalls);
        Assert.Equal(f.OutsideBefore, f.OutsideAcl());
    }

    [Fact]
    public async Task JunctionReplacementNeverReceivesRecovery()
    {
        await using var f = await Fixture.Create("directory");
        f.AtConfiguration = f.ReplaceTargetWithJunction;
        await f.ExpectFailure();
        Assert.Equal(f.OutsideBefore, f.OutsideAcl());
        Assert.Equal(1, f.HomeCleanupCalls);
    }

    [Theory]
    [InlineData("home")]
    [InlineData("file")]
    [InlineData("directory")]
    public async Task NormalCreationKeepsItsRequestedGrant(string target)
    {
        await using var f = await Fixture.Create(target);
        f.FailConfiguration = false;
        var result = await f.CreateAccount();
        Assert.Equal(f.Home, result.HomePath);
        Assert.Equal(FileSystemRights.Modify, f.ExplicitCurrentAllow() & FileSystemRights.Modify);
        Assert.Equal(0, f.HomeCleanupCalls);
        Assert.Equal(f.OutsideBefore, f.OutsideAcl());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Prefix = "adapanel-ftp-rollback-";
        private const string UserName = "rollback_fixture";
        private readonly string root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(40));
        private readonly CancellationTokenSource request;
        private readonly Dictionary<string, byte[]> savedAccess = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> allowedAclPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly JsonFileStore store;
        private readonly FtpService service;
        private readonly string ordinary, inside, outside, outsideFile, detached;
        private bool targetIsJunction;
        internal readonly SecurityIdentifier CurrentSid;
        internal readonly string Home, Target;
        internal readonly string[] OutsideBefore;
        internal bool FailConfiguration = true, CancelAtConfiguration;
        internal int HomeCleanupCalls;
        internal Func<Task>? AtConfiguration;
        internal string ReplacementSentinel => Target == inside ? Target : Path.Combine(Target, "replacement.txt");

        private Fixture(string target)
        {
            using (var identity = WindowsIdentity.GetCurrent()) CurrentSid = identity.User!;
            Assert.NotNull(CurrentSid);
            request = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            Directory.CreateDirectory(root);
            ValidateOwnedRoot();
            SaveAccess(root);
            AddAccess(root, CurrentSid, FileSystemRights.FullControl, AccessControlType.Allow);
            Home = Path.Combine(root, "home"); ordinary = Path.Combine(Home, "ordinary");
            inside = Path.Combine(ordinary, "original.txt");
            outside = Path.Combine(root, "outside"); outsideFile = Path.Combine(outside, "sentinel.txt");
            detached = Path.Combine(root, "detached-original");
            Directory.CreateDirectory(ordinary); Directory.CreateDirectory(outside);
            File.WriteAllText(inside, "original generation"); File.WriteAllText(outsideFile, "outside sentinel");
            foreach (var path in new[] { Home, ordinary, inside, outside, outsideFile }) SaveAccess(path);
            Target = target switch { "home" => Home, "directory" => ordinary, "file" => inside, _ => throw new ArgumentOutOfRangeException(nameof(target)) };
            SetCurrentAllow(Home, target == "home" ? FileSystemRights.ReadAndExecute : FileSystemRights.Modify);
            if (target != "home") SetCurrentAllow(Target, FileSystemRights.ReadAndExecute);
            allowedAclPaths.UnionWith([Home, ordinary, inside]);
            OutsideBefore = OutsideAcl();
            var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root });
            store = new JsonFileStore(options);
            var site = new SiteDto { Id = 143, Name = "rollback.test", PhysicalPath = Home, ApplicationPoolName = "rollback-pool" };
            service = new FtpService(Fake<IIisSiteService>((method, _) => method.Name switch
            {
                "GetSiteAsync" => Task.FromResult(site),
                "GetSitesAsync" => Task.FromResult<IReadOnlyCollection<SiteDto>>([site]),
                _ => throw new NotSupportedException(method.Name)
            }), store, Fake<IPanelPreferencesService>((method, _) => method.Name == "GetAsync"
                ? Task.FromResult(new PanelPreferencesDto()) : throw new NotSupportedException(method.Name)), options)
            {
                NativeAccountStatus = (name, token) => { token.ThrowIfCancellationRequested(); Assert.Equal(UserName, name); return Task.FromResult(0u); },
                HomePermissionIdentity = name => { Assert.Equal(UserName, name); return CurrentSid; },
                NativeCommand = NativeCommand,
                NativeScript = NativeScript
            };
        }

        internal static async Task<Fixture> Create(string target)
        {
            var f = new Fixture(target);
            try
            {
                Assert.True(FtpService.HomePermissionChangeNeeded(ReadAccess(f.Target), f.CurrentSid, Directory.Exists(f.Target), grant: true));
                await f.store.WriteAsync("config/ftp-infra.json", new { configured = true }, f.deadline.Token);
                await f.store.WriteAsync("config/ftp-users.json", new[] { new
                { userName = UserName, siteName = "rollback.test", homePath = f.Home, isEnabled = true, createdAtUtc = DateTime.UtcNow.AddDays(-1) } }, f.deadline.Token);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }

        internal Task<FtpUserDto> CreateAccount() => service.CreateFtpUserAsync(new CreateFtpUserRequest
        { UserName = UserName, Password = "FixturePassword123!", SiteName = "rollback.test", HomePath = Home }, request.Token);
        internal async Task ExpectFailure(bool cancel = false)
        {
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateAccount());
            else Assert.Equal("ftp.site_configuration_failed", (await Assert.ThrowsAsync<AppException>(() => CreateAccount())).MessageKey);
        }
        internal Task<byte[]> RecordBytes() => File.ReadAllBytesAsync(store.ResolvePath("config/ftp-users.json"), deadline.Token);
        internal string TargetAcl() => AccessText(Target);
        internal string[] OutsideAcl() => [AccessText(outside), AccessText(outsideFile)];
        internal FileSystemRights ExplicitCurrentAllow() => Rules(Target).Where(rule => rule.IdentityReference.Equals(CurrentSid)
            && rule.AccessControlType == AccessControlType.Allow && !rule.IsInherited).Aggregate((FileSystemRights)0, (rights, rule) => rights | rule.FileSystemRights);
        internal string[] UnrelatedRules() => Rules(Target).Where(rule => !rule.IdentityReference.Equals(CurrentSid) || rule.AccessControlType != AccessControlType.Allow)
            .Select(rule => $"{rule.IdentityReference.Value}:{rule.AccessControlType}:{rule.FileSystemRights}:{rule.InheritanceFlags}:{rule.PropagationFlags}:{rule.IsInherited}").Order(StringComparer.Ordinal).ToArray();
        internal void AddTargetAccess(SecurityIdentifier identity, FileSystemRights rights, AccessControlType type) => AddAccess(Target, identity, rights, type);
        internal void ChangeExistingSystemRule()
        {
            ValidateOwnedPath(Target);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var security = ReadAccess(Target);
            var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Where(rule => rule.IdentityReference.Equals(system) && rule.AccessControlType == AccessControlType.Allow).ToArray();
            Assert.NotEmpty(rules);
            foreach (var rule in rules) security.RemoveAccessRuleSpecific(rule);
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.ReadAttributes, AccessControlType.Allow));
            ApplyAccess(Target, security);
        }

        internal void RemoveTarget() { ValidateOwnedPath(Target); Assert.Equal(inside, Target); File.Delete(Target); }
        private void MoveTargetAside()
        {
            ValidateOwnedPath(Target); ValidateOwnedPath(detached);
            Assert.NotEqual(Home, Target);
            var directory = Directory.Exists(Target);
            if (directory) Directory.Move(Target, detached); else File.Move(Target, detached);
            foreach (var pair in savedAccess.Where(pair => PathContainment.IsRootOrInside(Target, pair.Key)).ToArray())
                savedAccess[Path.GetFullPath(Path.Combine(detached, Path.GetRelativePath(Target, pair.Key)))] = pair.Value;
        }
        internal void ReplaceTarget()
        {
            MoveTargetAside();
            if (Target == inside) File.WriteAllText(Target, "replacement generation");
            else { Directory.CreateDirectory(Target); File.WriteAllText(ReplacementSentinel, "replacement generation"); SaveAccess(ReplacementSentinel); }
            SetCurrentAllow(Target, FileSystemRights.ReadAndExecute);
        }
        internal async Task ReplaceTargetWithJunction()
        {
            Assert.Equal(ordinary, Target);
            MoveTargetAside(); ValidateOwnedPath(Target); ValidateOwnedPath(outside);
            var info = new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") };
            info.ArgumentList.Add("/c"); info.ArgumentList.Add("mklink"); info.ArgumentList.Add("/J"); info.ArgumentList.Add(Target); info.ArgumentList.Add(outside);
            Assert.True((await RunProcess(info, deadline.Token)).Success);
            targetIsJunction = true;
            Assert.Equal(outside, Directory.ResolveLinkTarget(Target, true)!.FullName, StringComparer.OrdinalIgnoreCase);
        }

        private async Task<CommandResult> NativeCommand(string executable, string arguments, CancellationToken token)
        {
            Assert.Equal("icacls.exe", executable); ValidateOwnedRoot();
            Assert.StartsWith("\"", arguments, StringComparison.Ordinal);
            var end = arguments.IndexOf('"', 1); Assert.True(end > 1);
            var path = arguments[1..end]; Assert.Contains(path, allowedAclPaths);
            ValidateOwnedPath(path);
            Assert.Equal((FileAttributes)0, File.GetAttributes(path) & FileAttributes.ReparsePoint);
            var rights = Directory.Exists(path) ? "(OI)(CI)M" : "M";
            var grant = arguments == $"\"{path}\" /grant \"{UserName}:{rights}\" /C /L";
            var remove = arguments == $"\"{path}\" /remove:g \"{UserName}\" /C /L";
            Assert.True(grant || remove, "Only the exact nonrecursive fixture ACL command is permitted.");
            var info = new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "icacls.exe") };
            info.ArgumentList.Add(path); info.ArgumentList.Add(grant ? "/grant" : "/remove:g");
            info.ArgumentList.Add("*" + CurrentSid.Value + (grant ? ":" + rights : string.Empty));
            info.ArgumentList.Add("/C"); info.ArgumentList.Add("/L");
            var result = await RunProcess(info, token); Assert.True(result.Success); return result;
        }

        private async Task<CommandResult> NativeScript(string script, bool strict, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (script.StartsWith("if (Get-Service FTPSVC", StringComparison.Ordinal)) return Result("present");
            if (script.StartsWith("if ((Get-Service FTPSVC", StringComparison.Ordinal)) return Result("up");
            if (script.Contains("Set-LocalUser", StringComparison.Ordinal)) return Result("ADAPANEL-SURESIZ");
            Assert.True(strict);
            if (script.Contains("Add-WebConfiguration", StringComparison.Ordinal))
            {
                if (AtConfiguration is not null) await AtConfiguration();
                if (CancelAtConfiguration) { request.Cancel(); token.ThrowIfCancellationRequested(); }
                return new CommandResult { ExitCode = FailConfiguration ? 1 : 0 };
            }
            Assert.Contains("foreach ($userRoot", script, StringComparison.Ordinal);
            HomeCleanupCalls++;
            return Result(string.Empty);
        }
        private static CommandResult Result(string output) => new() { ExitCode = 0, StandardOutput = output };

        private async Task<CommandResult> RunProcess(ProcessStartInfo info, CancellationToken token)
        {
            info.UseShellExecute = false; info.CreateNoWindow = true;
            info.RedirectStandardOutput = true; info.RedirectStandardError = true;
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token); bound.CancelAfter(TimeSpan.FromSeconds(15));
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
        private static FileSystemAccessRule[] Rules(string path) => ReadAccess(path).GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        private static string AccessText(string path) => ReadAccess(path).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        private void SaveAccess(string path) { ValidateOwnedPath(path); savedAccess[path] = ReadAccess(path).GetSecurityDescriptorBinaryForm(); }
        private void AddAccess(string path, SecurityIdentifier identity, FileSystemRights rights, AccessControlType type)
        {
            ValidateOwnedPath(path); var security = ReadAccess(path);
            security.AddAccessRule(new FileSystemAccessRule(identity, rights, Directory.Exists(path) ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None, PropagationFlags.None, type));
            ApplyAccess(path, security);
        }
        private void SetCurrentAllow(string path, FileSystemRights rights)
        {
            ValidateOwnedPath(path); var directory = Directory.Exists(path);
            var rules = Rules(path);
            FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var rule in rules.Where(rule => !rule.IdentityReference.Equals(CurrentSid) || rule.AccessControlType != AccessControlType.Allow))
                security.AddAccessRule(new FileSystemAccessRule(rule.IdentityReference, rule.FileSystemRights, rule.InheritanceFlags, rule.PropagationFlags, rule.AccessControlType));
            security.AddAccessRule(new FileSystemAccessRule(CurrentSid, rights, directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
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
            if (targetIsJunction)
            {
                Assert.Equal(FileAttributes.ReparsePoint, File.GetAttributes(Target) & FileAttributes.ReparsePoint);
                Assert.Equal(outside, Directory.ResolveLinkTarget(Target, true)!.FullName, StringComparer.OrdinalIgnoreCase);
                Directory.Delete(Target); // Unlink only, never recurse into its destination.
            }
            foreach (var pair in savedAccess.OrderByDescending(pair => pair.Key.Length))
            {
                if (!Path.Exists(pair.Key)) continue;
                ValidateOwnedPath(pair.Key); Assert.Equal((FileAttributes)0, File.GetAttributes(pair.Key) & FileAttributes.ReparsePoint);
                var security = ReadAccess(pair.Key); security.SetSecurityDescriptorBinaryForm(pair.Value, AccessControlSections.Access); ApplyAccess(pair.Key, security);
            }
            ValidateOwnedRoot();
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var directory))
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    Assert.Equal((FileAttributes)0, entry.Attributes & FileAttributes.ReparsePoint);
                    if (entry is DirectoryInfo) pending.Push(entry.FullName);
                }
            Directory.Delete(root, recursive: true);
            request.Dispose(); deadline.Dispose(); return ValueTask.CompletedTask;
        }
    }

    private static T Fake<T>(Func<MethodInfo, object?[], object?> action) where T : class
    { var value = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)value).Action = action; return value; }
    public class Proxy : DispatchProxy
    { public Func<MethodInfo, object?[], object?> Action = null!; protected override object? Invoke(MethodInfo? method, object?[]? args) => Action(method!, args ?? []); }
}
