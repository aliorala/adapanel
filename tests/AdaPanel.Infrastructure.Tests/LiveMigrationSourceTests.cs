using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Infrastructure.Migration;
using AdaPanel.Infrastructure.Migration.Sources;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class LiveMigrationSourceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanel.LiveMigration." + Guid.NewGuid().ToString("N"));
    private readonly Transport transport = new();
    private readonly LiveMigrationOptions options = new();
    private const string Secret = "THIS-IS-A-CONNECTION-SECRET";
    public LiveMigrationSourceTests() => Directory.CreateDirectory(root);
    private LiveMigrationSource Service(bool allowInternal = false, IPAddress? address = null) => new(
        Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { AllowInternalOutboundTargets = allowInternal }), options, transport,
        (_, _) => Task.FromResult(new[] { address ?? IPAddress.Parse("8.8.8.8") }));
    private static LiveMigrationConnection Connection(string provider = "cpanel", string platform = "linux", string fingerprint = "SHA256:fixture") => new()
    { Provider = provider, Platform = platform, Host = "source.example.com", SshUsername = "root", Password = Secret, AcceptedSshFingerprint = fingerprint };

    [Theory]
    [InlineData("cpanel")]
    [InlineData("plesk")]
    public async Task DiscoverUsesReadonlyOfficialInventoryCommandsAndPinnedInspectedAddress(string provider)
    {
        var result = await Service().DiscoverAsync(Connection(provider));
        Assert.True(result.Supported); Assert.Empty(result.ErrorCode); Assert.Equal(2, result.Units.Count);
        Assert.Equal("8.8.8.8", transport.Address!.ToString());
        Assert.Contains(provider == "cpanel" ? "/usr/local/cpanel/bin/whmapi1 --output=json listaccts" : "/usr/sbin/plesk bin subscription --list", transport.Session.Commands);
        Assert.Empty(transport.Session.CreatedDirectories);
        Assert.All(result.Units, unit => Assert.StartsWith(provider + ":", unit.Id));
    }

    [Theory]
    [InlineData("cpanel", "alpha")]
    [InlineData("plesk", "alpha.example.com")]
    public async Task AcquireCreatesFullBackupDownloadsActualBytesHashAndCleansOnlyOwnedFiles(string provider, string name)
    {
        var result = await Service().AcquireAsync(Connection(provider), [provider + ":" + name], root);
        Assert.Empty(result.ErrorCode); Assert.True(result.ProcessCompletionConfirmed); Assert.True(result.RemoteCleanupComplete);
        var artifact = Assert.Single(result.Artifacts);
        Assert.True(artifact.Full); Assert.True(artifact.RequiresContentValidation);
        Assert.Equal(transport.Session.Content, File.ReadAllBytes(artifact.ServerGeneratedPath));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(transport.Session.Content)).ToLowerInvariant(), artifact.Sha256);
        Assert.Equal(transport.Session.Content.Length, artifact.Bytes);
        Assert.Contains("migration.live_snapshot_not_atomic", result.Warnings);
        Assert.Equal(1, transport.Session.BackupCalls);
        Assert.True(File.Exists(result.RecoveryManifestPath));
        var manifest = File.ReadAllText(result.RecoveryManifestPath);
        Assert.Contains(result.RemoteWorkspace, manifest); Assert.DoesNotContain(Secret, manifest);
        Assert.Single(transport.Session.DeletedFiles); Assert.Single(transport.Session.DeletedDirectories);
        var backup = transport.Session.Commands.Single(command => command.Contains("pkgacct ") || command.Contains("pleskbackup "));
        if (provider == "cpanel")
        {
            Assert.Contains("--dbbackup=all --skipapitokens --skipauthnlinks 'alpha'", backup);
            Assert.DoesNotContain("--incremental", backup); Assert.DoesNotContain("--split", backup); Assert.DoesNotContain("--use_backups", backup);
        }
        else
        {
            Assert.Contains("--domains-name 'alpha.example.com' -output-file", backup);
            Assert.Contains("-compression-level fastest -v", backup); Assert.DoesNotContain("--suspend", backup);
        }
        Assert.DoesNotContain("rm ", string.Join('\n', transport.Session.Commands));
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task AcquireRereadsInventoryAndRejectsRemovedOrForgedSelectionBeforeCreatingWorkspace()
    {
        var result = await Service().AcquireAsync(Connection(), ["cpanel:missing"], root);
        Assert.Equal("migration.live_selection_stale", result.ErrorCode);
        Assert.Empty(transport.Session.CreatedDirectories); Assert.Equal(0, transport.Session.BackupCalls);
    }

    [Theory]
    [InlineData("cpanel", "windows")]
    [InlineData("plesk", "windows")]
    [InlineData("unknown", "linux")]
    public async Task UnsupportedPlatformsNeverConnect(string provider, string platform)
    {
        Assert.False((await Service().DiscoverAsync(Connection(provider, platform))).Supported);
        Assert.Equal("migration.live_platform_unsupported", (await Service().AcquireAsync(Connection(provider, platform), ["x"], root)).ErrorCode);
        Assert.Equal(0, transport.ConnectCalls);
    }

    [Fact]
    public async Task InternalAddressIsRejectedBeforeAnyTransportOrCredentialUse()
    {
        var result = await Service(address: IPAddress.Loopback).DiscoverAsync(Connection());
        Assert.Equal("migration.host_forbidden", result.ErrorCode); Assert.Equal(0, transport.ConnectCalls);
    }

    [Fact]
    public async Task ExplicitInternalPolicyStillPinsTheInspectedLiteralAddress()
    {
        var result = await Service(true, IPAddress.Parse("192.168.4.5")).DiscoverAsync(Connection());
        Assert.Empty(result.ErrorCode); Assert.Equal("192.168.4.5", transport.Address!.ToString());
    }

    [Fact]
    public async Task UnknownHostKeyReturnsOnlyFingerprintAndDoesNotRunCommands()
    {
        transport.ConnectError = new AppException("migration.hostkey.untrusted", 409, 409, "SHA256:unknown-key");
        var result = await Service().DiscoverAsync(Connection(fingerprint: ""));
        Assert.Equal("migration.hostkey.untrusted", result.ErrorCode); Assert.Equal("SHA256:unknown-key", result.HostFingerprint);
        Assert.Empty(transport.Session.Commands); Assert.DoesNotContain(Secret, JsonSerializer.Serialize(result));
    }

    [Fact]
    public void SharedHostKeyGateRejectsUnknownAndAcceptsOnlyTheExactSha256Pin()
    {
        const string key = "n8/3n62XJlrV0kh7O1pyzSpWEwX+oY4VPnY0j8Ml4ZM";
        Assert.False(new RemoteHostKeyTrustGate("").Evaluate("ssh-ed25519", key, 256));
        Assert.False(new RemoteHostKeyTrustGate("SHA256:different").Evaluate("ssh-ed25519", key, 256));
        Assert.True(new RemoteHostKeyTrustGate("SHA256:" + key).Evaluate("ssh-ed25519", key, 256));
    }

    [Theory]
    [InlineData("malicious")]
    [InlineData("truncated")]
    [InlineData("nonzero")]
    public async Task FailedInventoryNeverBecomesAnEmptySuccessfulDiscovery(string mode)
    {
        transport.Session.ListingMode = mode;
        var result = await Service().DiscoverAsync(Connection());
        Assert.NotEmpty(result.ErrorCode); Assert.Empty(result.Units);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("symlink")]
    [InlineData("wrong-name")]
    [InlineData("zero")]
    [InlineData("oversize")]
    public async Task ExitZeroStillRequiresExpectedNormalNonemptyBoundedArtifact(string mode)
    {
        transport.Session.ArtifactMode = mode;
        options.MaxArtifactBytes = 1000;
        var result = await Service().AcquireAsync(Connection(), ["cpanel:alpha"], root);
        Assert.NotEmpty(result.ErrorCode); Assert.Empty(result.Artifacts); Assert.False(result.RemoteCleanupComplete);
        Assert.Empty(transport.Session.DeletedFiles);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task StreamShorterOrLongerThanMetadataCannotProduceAnArtifact(int sizeDelta)
    {
        transport.Session.SizeDelta = sizeDelta;
        var result = await Service().AcquireAsync(Connection(), ["cpanel:alpha"], root);
        Assert.NotEmpty(result.ErrorCode); Assert.Empty(result.Artifacts);
        Assert.Empty(Directory.GetFiles(root, "*.tar.gz"));
    }

    [Fact]
    public async Task SecondBackupFailurePreservesFirstArtifactAndReportsPartialState()
    {
        transport.Session.FailBackupAt = 2;
        var result = await Service().AcquireAsync(Connection(), ["cpanel:alpha", "cpanel:beta"], root);
        Assert.Single(result.Artifacts); Assert.NotEmpty(result.ErrorCode); Assert.False(result.RemoteCleanupComplete);
        Assert.True(result.ProcessCompletionConfirmed); Assert.Equal(2, result.AttemptedRemoteFiles.Count);
        Assert.Empty(transport.Session.DeletedFiles);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InterruptedOrUnknownCompletionNeverDeletesSourceWorkspace(bool cancel)
    {
        transport.Session.InterruptBackup = cancel;
        transport.Session.UnknownExit = !cancel;
        var result = await Service().AcquireAsync(Connection(), ["cpanel:alpha"], root);
        Assert.Equal(cancel, result.Cancelled); Assert.False(result.ProcessCompletionConfirmed);
        Assert.False(result.RemoteCleanupComplete); Assert.Empty(result.Artifacts);
        Assert.Empty(transport.Session.DeletedFiles); Assert.Empty(transport.Session.DeletedDirectories);
        Assert.NotEmpty(result.RemoteWorkspace); Assert.True(File.Exists(result.RecoveryManifestPath));
    }

    [Fact]
    public async Task UnknownWorkspaceEntriesAreRetainedAndCleanupIsExplicitlyIncomplete()
    {
        transport.Session.UnknownWorkspaceEntry = true;
        var result = await Service().AcquireAsync(Connection(), ["cpanel:alpha"], root);
        Assert.Single(result.Artifacts); Assert.False(result.RemoteCleanupComplete); Assert.Empty(transport.Session.DeletedFiles);
    }

    [Fact]
    public async Task RawTransportErrorsAndSecretsNeverReachResultsOrProgress()
    {
        transport.ConnectError = new Exception(Secret);
        var progress = new List<LiveMigrationProgress>();
        var result = await Service().AcquireAsync(Connection(), ["cpanel:alpha"], root, new ProgressSink(progress));
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(result) + JsonSerializer.Serialize(progress));
        Assert.NotEmpty(result.ErrorCode); Assert.Empty(result.Artifacts);
    }

    [Theory]
    [InlineData("alpha; touch /tmp/pwn")]
    [InlineData("--server")]
    [InlineData("alpha\nroot")]
    public void UserTextCannotExpandBackupCommandScope(string unit)
    {
        Assert.Throws<AppException>(() => LiveMigrationSource.BackupCommand("cpanel", unit,
            "/var/tmp/adapanel-migration-" + new string('a', 32), "/var/tmp/adapanel-migration-" + new string('a', 32) + "/source.tar"));
    }

    public void Dispose() => Directory.Delete(root, true);
    private sealed class ProgressSink(List<LiveMigrationProgress> items) : IProgress<LiveMigrationProgress>
    { public void Report(LiveMigrationProgress value) => items.Add(value); }
    private sealed class Transport : ILiveSshTransport
    {
        public readonly Session Session = new();
        public int ConnectCalls;
        public IPAddress? Address;
        public Exception? ConnectError;
        public Task<ILiveSshSession> ConnectAsync(LiveMigrationConnection connection, IPAddress inspectedAddress, CancellationToken token)
        { ConnectCalls++; Address = inspectedAddress; if (ConnectError != null) throw ConnectError; return Task.FromResult<ILiveSshSession>(Session); }
    }
    private sealed class Session : ILiveSshSession
    {
        public readonly List<string> Commands = [], CreatedDirectories = [], DeletedFiles = [], DeletedDirectories = [];
        public readonly Dictionary<string, LiveRemoteEntry> Entries = [];
        public readonly byte[] Content = Encoding.UTF8.GetBytes("actual-backup-content-fixture");
        public string ListingMode = "", ArtifactMode = "";
        public int BackupCalls, FailBackupAt, SizeDelta;
        public bool InterruptBackup, UnknownExit, UnknownWorkspaceEntry;
        public Task<LiveCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken token)
        {
            Commands.Add(command);
            if (command == "/usr/bin/id -u") return Task.FromResult(new LiveCommandResult(0, "0\n"));
            if (command.Contains("listaccts") || command.Contains("subscription --list"))
            {
                if (ListingMode == "nonzero") return Task.FromResult(new LiveCommandResult(1, Secret));
                if (ListingMode == "malicious") return Task.FromResult(new LiveCommandResult(0, "{\"metadata\":{\"result\":1},\"data\":{\"acct\":[{\"user\":\"x;cmd\"}]}}"));
                return Task.FromResult(new LiveCommandResult(0, command.Contains("listaccts")
                    ? "{\"metadata\":{\"result\":1},\"data\":{\"acct\":[{\"user\":\"alpha\"},{\"user\":\"beta\"}]}}"
                    : "alpha.example.com\nbeta.example.com\n", ListingMode == "truncated"));
            }
            if (command.Contains(" -V") || command.EndsWith(" version")) return Task.FromResult(new LiveCommandResult(0, "Product version: 18.0.75"));
            BackupCalls++;
            if (InterruptBackup) throw new OperationCanceledException();
            if (UnknownExit) return Task.FromResult(new LiveCommandResult(null, Secret));
            if (FailBackupAt == BackupCalls) return Task.FromResult(new LiveCommandResult(1, Secret));
            if (ArtifactMode != "missing")
            {
                var name = command.Contains("pkgacct") ? "cpmove-" + (BackupCalls == 1 ? "alpha" : "beta") + ".tar.gz" : "source-" + (BackupCalls - 1) + ".tar";
                if (ArtifactMode == "wrong-name") name = "unexpected.tar.gz";
                var bytes = ArtifactMode == "oversize" ? 1001 : ArtifactMode == "zero" ? 0 : Content.Length + SizeDelta;
                Entries[name] = new(name, true, false, ArtifactMode == "symlink", bytes);
                if (UnknownWorkspaceEntry) Entries["unowned"] = new("unowned", false, true, false, 0);
            }
            return Task.FromResult(new LiveCommandResult(0, ""));
        }
        public Task CreatePrivateDirectoryAsync(string path, CancellationToken token) { CreatedDirectories.Add(path); return Task.CompletedTask; }
        public Task<IReadOnlyList<LiveRemoteEntry>> ListAsync(string path, CancellationToken token) => Task.FromResult<IReadOnlyList<LiveRemoteEntry>>(Entries.Values.ToArray());
        public Task<Stream> OpenReadAsync(string path, CancellationToken token) => Task.FromResult<Stream>(new MemoryStream(Content));
        public Task DeleteFileAsync(string path, CancellationToken token) { DeletedFiles.Add(path); Entries.Remove(path[(path.LastIndexOf('/') + 1)..]); return Task.CompletedTask; }
        public Task DeleteDirectoryAsync(string path, CancellationToken token) { DeletedDirectories.Add(path); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
