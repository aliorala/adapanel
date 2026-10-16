using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailCertificateLifecycleTests
{
    public static IEnumerable<object[]> SyncIssueCases() =>
        from stage in new[] { "query", "write", "reload" }
        from sameHost in new[] { false, true }
        select new object[] { stage, sameHost };

    [Theory]
    [MemberData(nameof(SyncIssueCases))]
    public async Task AQueuedIssueCannotBeOverwrittenByTheOlderSync(string stage, bool sameHost)
    {
        using var f = new Fixture();
        await f.Initialize();
        f.Pause("first", stage);
        var first = f.First.SyncCertificateAsync(f.Token);
        await f.Entered.Task.WaitAsync(f.Token);
        var host = sameHost ? Fixture.OldHost : "mail.new.example.test";
        var second = f.Second.IssueCertificateAsync(Request(host), f.Token);
        await f.CompleteInOrder(first, second);
        var state = await f.State();
        Assert.Equal(host, state.HostName);
        Assert.Equal("letsencrypt", state.Source);
        Assert.Equal(f.Store.ResolvePath($"mail-certs/{host}"), state.PemDirectory);
        Assert.Equal(f.LoadedHash, state.ContentHash);
        Assert.Equal(f.PemHash(state.PemDirectory), state.ContentHash);
        Assert.Equal(new[] { "first", "first", "first", "second", "second", "second" }, f.Commands.Select(item => item.Owner));
        Assert.Equal(0, f.References);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("write")]
    [InlineData("reload")]
    public async Task AQueuedSyncReadsTheCompletedIssueInsteadOfItsOldState(string stage)
    {
        using var f = new Fixture();
        await f.Initialize();
        f.Pause("first", stage);
        var first = f.First.IssueCertificateAsync(Request(Fixture.OldHost), f.Token);
        await f.Entered.Task.WaitAsync(f.Token);
        var second = f.Second.SyncCertificateAsync(f.Token);
        await f.CompleteInOrder(first, second);
        Assert.Null(await second);
        Assert.Equal(new[] { "first", "first", "first" }, f.Commands.Select(item => item.Owner));
        var state = await f.State();
        Assert.Equal("letsencrypt", state.Source);
        Assert.Equal(f.LoadedHash, state.ContentHash);
        Assert.Equal(f.PemHash(state.PemDirectory), state.ContentHash);
    }

    [Theory]
    [InlineData("query", false)]
    [InlineData("write", false)]
    [InlineData("reload", false)]
    [InlineData("query", true)]
    [InlineData("write", true)]
    [InlineData("reload", true)]
    public async Task TwoSyncsPublishTheNewestPemWithoutDuplicateOrInterleavedPushes(string stage, bool renewed)
    {
        using var f = new Fixture();
        await f.Initialize();
        f.Pause("first", stage);
        var first = f.First.SyncCertificateAsync(f.Token);
        await f.Entered.Task.WaitAsync(f.Token);
        if (renewed) f.WritePem(Fixture.OldHost, f.InitialPemDirectory);
        var second = f.Second.SyncCertificateAsync(f.Token);
        await f.CompleteInOrder(first, second);
        Assert.Equal(renewed, await second is not null);
        var state = await f.State();
        Assert.Equal(f.PemHash(f.InitialPemDirectory), state.ContentHash);
        Assert.Equal(f.LoadedHash, state.ContentHash);
        var expected = renewed ? new[] { "first", "first", "first", "second", "second", "second" }
            : new[] { "first", "first", "first" };
        Assert.Equal(expected, f.Commands.Select(item => item.Owner));
        Assert.Equal(0, f.References);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("write")]
    [InlineData("reload")]
    public async Task TwoIssuesKeepTheLastRequestedHostAndCertificateTogether(string stage)
    {
        using var f = new Fixture();
        await f.Initialize();
        f.Pause("first", stage);
        var first = f.First.IssueCertificateAsync(Request("mail.first.example.test"), f.Token);
        await f.Entered.Task.WaitAsync(f.Token);
        var second = f.Second.IssueCertificateAsync(Request("mail.second.example.test"), f.Token);
        await f.CompleteInOrder(first, second);
        var state = await f.State();
        Assert.Equal("mail.second.example.test", state.HostName);
        Assert.Equal(f.LoadedHash, state.ContentHash);
        Assert.Equal(f.PemHash(state.PemDirectory), state.ContentHash);
        Assert.Equal(new[] { "first", "first", "first", "second", "second", "second" }, f.Commands.Select(item => item.Owner));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AQueuedCanceledOperationDoesNotGeneratePemOrPush(bool issue)
    {
        using var f = new Fixture();
        await f.Initialize();
        f.Pause("first", "query");
        var first = f.First.SyncCertificateAsync(f.Token);
        await f.Entered.Task.WaitAsync(f.Token);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var second = Capture(async () =>
        {
            if (issue) await f.Second.IssueCertificateAsync(Request("mail.canceled.example.test"), canceled.Token);
            else await f.Second.SyncCertificateAsync(canceled.Token);
        });
        if (f.References < 2) await second.WaitAsync(f.Token);
        canceled.Cancel();
        f.Release.TrySetResult();
        await first.WaitAsync(f.Token);
        Assert.IsAssignableFrom<OperationCanceledException>(await second.WaitAsync(f.Token));
        Assert.All(f.Commands, command => Assert.Equal("first", command.Owner));
        Assert.False(Directory.Exists(f.Store.ResolvePath("mail-certs/mail.canceled.example.test")));
        Assert.Equal(0, f.References);
        Assert.Null(await f.Second.SyncCertificateAsync(f.Token));
    }

    [Fact]
    public async Task AQueuedSyncRevalidatesApprovalAfterWaitingForIssue()
    {
        using var f = new Fixture();
        await f.Initialize();
        f.Pause("first", "query");
        var first = f.First.IssueCertificateAsync(Request("mail.new.example.test"), f.Token);
        await f.Entered.Task.WaitAsync(f.Token);
        var deny = false;
        var rejected = new AppException("fixture.actor_changed", 403, 403);
        Task Approve(CancellationToken _) => deny ? Task.FromException(rejected) : Task.CompletedTask;
        var second = Capture(async () => await f.Second.SyncCertificateAsync(Approve, f.Token));
        if (f.References < 2) await second.WaitAsync(f.Token);
        deny = true;
        f.Release.TrySetResult();
        await first.WaitAsync(f.Token);
        Assert.Same(rejected, await second.WaitAsync(f.Token));
        Assert.All(f.Commands, command => Assert.Equal("first", command.Owner));
        Assert.Equal("mail.new.example.test", (await f.State()).HostName);
    }

    [Fact]
    public async Task CancellationAtAcquisitionDoesNotReturnAnOwnedLeaseOrLeakItsGate()
    {
        using var f = new Fixture();
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        IDisposable? returned = null;
        Exception? error;
        try
        {
            error = await Capture(async () => returned = await MailCertificateLifecycle.EnterAsync(
                f.StatePath, canceled.Token, canceled.Cancel));
        }
        finally { returned?.Dispose(); }
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Null(returned);
        Assert.Equal(0, f.References);
        using (await MailCertificateLifecycle.EnterAsync(f.StatePath, f.Token)) Assert.Equal(1, f.References);
        Assert.Equal(0, f.References);
    }

    [Fact]
    public async Task IssueCanceledAfterAcquisitionBeforeItsContinuationDoesNotWritePem()
    {
        using var f = new Fixture();
        await f.Initialize();
        var before = await File.ReadAllBytesAsync(f.StatePath, f.Token);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        using var holder = await MailCertificateLifecycle.EnterAsync(f.StatePath, f.Token);
        var context = new HeldContinuationContext();
        var previous = SynchronizationContext.Current;
        Task<MailCertificateDto> operation;
        SynchronizationContext.SetSynchronizationContext(context);
        try { operation = f.Second.IssueCertificateAsync(Request("mail.canceled.example.test"), canceled.Token); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        try
        {
            Assert.Equal(2, f.References);
            holder.Dispose();
            await context.Posted.Task.WaitAsync(f.Token);
            // EnterAsync already completed; the caller's await continuation has not executed.
            // This is independent of xUnit load and of SemaphoreSlim's cancellation scheduling.
            canceled.Cancel();
            context.Release();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(f.Token));
            Assert.Empty(f.Commands);
            Assert.False(Directory.Exists(f.Store.ResolvePath("mail-certs/mail.canceled.example.test")));
            Assert.Equal(before, await File.ReadAllBytesAsync(f.StatePath, f.Token));
            Assert.Equal(0, f.References);
        }
        finally
        {
            holder.Dispose();
            context.Release();
            try { await operation.WaitAsync(f.Token); }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        }
    }

    public static IEnumerable<object[]> Failures() =>
        from issue in new[] { false, true }
        from stage in new[] { "query", "write", "reload" }
        select new object[] { issue, stage };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AProviderFailureDoesNotPublishAndReleasesTheOperationForRetry(bool issue, string stage)
    {
        using var f = new Fixture();
        await f.Initialize();
        var before = await File.ReadAllBytesAsync(f.StatePath, f.Token);
        f.FailedStage = stage;
        await Assert.ThrowsAsync<AppException>(async () =>
        {
            if (issue) await f.First.IssueCertificateAsync(Request(Fixture.OldHost), f.Token);
            else await f.First.SyncCertificateAsync(f.Token);
        });
        Assert.Equal(before, await File.ReadAllBytesAsync(f.StatePath, f.Token));
        Assert.Equal(0, f.References);
        f.FailedStage = null;
        if (issue) await f.Second.IssueCertificateAsync(Request(Fixture.OldHost), f.Token);
        else await f.Second.SyncCertificateAsync(f.Token);
        var state = await f.State();
        Assert.Equal(f.LoadedHash, state.ContentHash);
        Assert.Equal(f.PemHash(state.PemDirectory), state.ContentHash);
        Assert.Equal(0, f.References);
    }

    [Fact]
    public async Task DifferentPanelStoresDoNotBlockEachOthersCertificates()
    {
        using var firstStore = new Fixture();
        using var otherStore = new Fixture();
        await firstStore.Initialize();
        await otherStore.Initialize();
        firstStore.Pause("first", "query");
        var held = firstStore.First.SyncCertificateAsync(firstStore.Token);
        await firstStore.Entered.Task.WaitAsync(firstStore.Token);
        try
        {
            await otherStore.First.IssueCertificateAsync(Request("mail.other.example.test"), otherStore.Token);
            Assert.Equal("mail.other.example.test", (await otherStore.State()).HostName);
            Assert.False(held.IsCompleted);
        }
        finally { firstStore.Release.TrySetResult(); await held.WaitAsync(firstStore.Token); }
    }

    private static IssueMailCertificateRequest Request(string host) => new() { Domain = "example.test", HostName = host };
    private static async Task<Exception?> Capture(Func<Task> action)
    { try { await action(); return null; } catch (Exception error) { return error; } }

    private sealed class HeldContinuationContext : SynchronizationContext
    {
        private readonly object sync = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> pending = new();
        private bool released;
        internal readonly TaskCompletionSource Posted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (sync)
            {
                if (!released)
                {
                    pending.Enqueue((callback, state));
                    Posted.TrySetResult();
                    return;
                }
            }
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        internal void Release()
        {
            (SendOrPostCallback Callback, object? State)[] ready;
            lock (sync)
            {
                released = true;
                ready = pending.ToArray();
                pending.Clear();
            }
            foreach (var item in ready) ThreadPool.QueueUserWorkItem(_ => item.Callback(item.State));
        }
    }

    private sealed class StateRecord
    {
        public string Domain { get; set; } = "example.test";
        public string HostName { get; set; } = OldHost;
        public string Source { get; set; } = "letsencrypt";
        public string PemDirectory { get; set; } = "";
        public string ContentHash { get; set; } = "previous-hash";
        private const string OldHost = Fixture.OldHost;
    }

    private sealed class Fixture : IDisposable
    {
        internal const string OldHost = "mail.old.example.test";
        private readonly BulutAlani area = new();
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly List<AdaPanelDbContext> contexts = [];
        private readonly string cliDirectory;
        private string? pausedOwner, pausedStage, writtenHash;
        private bool readback;
        private readonly Dictionary<string, object> certificates = [];
        internal string? LoadedHash, FailedStage;
        internal readonly MailService First, Second;
        internal readonly List<(string Owner, string Stage)> Commands = [];
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal JsonFileStore Store => area.DosyaDeposu;
        internal string StatePath => Store.ResolvePath("config/mail-cert.json");
        internal string InitialPemDirectory => Store.ResolvePath($"mail-certs/{OldHost}");
        internal CancellationToken Token => area.Iptal;
        internal int References => MailCertificateLifecycle.ReferencesForPath(StatePath);

        internal Fixture()
        {
            connection.Open();
            cliDirectory = Store.EnsureDirectory("fake-cli");
            File.WriteAllText(Path.Combine(cliDirectory, "stalwart-cli.exe"), "not an executable");
            using (var seed = NewContext())
            {
                seed.Database.EnsureCreated();
                seed.InstalledComponents.Add(new InstalledComponent
                { ComponentId = "stalwart-cli", InstallDir = cliDirectory, Status = ComponentInstallStatus.Installed, Type = ComponentType.Zip });
                seed.SaveChanges();
            }
            First = Service("first", Store);
            // Separate scoped service, context and store objects still address the same state file.
            Second = Service("second", new JsonFileStore(Microsoft.Extensions.Options.Options.Create(
                new PanelRuntimeOptions { DataRoot = Path.Combine(area.Kok, ".") })));
        }

        private AdaPanelDbContext NewContext() => new(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
        private MailService Service(string owner, JsonFileStore store)
        {
            var db = NewContext();
            contexts.Add(db);
            var protector = new SahteKoruyucu();
            var runtime = new StalwartRuntime(store, protector, db, new SahteIstemciFabrikasi(new SahteBulut()));
            return new MailService(store, protector, null!, null!, null!, db, runtime, NullLogger<MailService>.Instance)
            {
                CertificateCommandRunner = (cli, command, json, env, token) => Execute(owner, cli, command, json, env, token),
                StoreCertificateLookup = _ => null,
                CertificateIssuer = (_, host, _, _, directory, _) => { WritePem(host, directory); return Task.FromResult(true); },
                TlsProbe = (_, port, _, _) => Task.FromResult(new MailTlsEndpointDto { Port = port, Status = "ok" })
            };
        }

        internal async Task Initialize()
        {
            WritePem(OldHost, InitialPemDirectory);
            await Store.WriteAsync("config/mail-cert.json", new StateRecord { PemDirectory = InitialPemDirectory }, Token);
            await Store.WriteAsync("config/mail.json", new { adminPasswordProtected = SahteKoruyucu.Onek + "fixture-password" }, Token);
        }

        internal void WritePem(string host, string directory)
        {
            Directory.CreateDirectory(directory);
            using var key = RSA.Create(2048);
            var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(host);
            request.CertificateExtensions.Add(san.Build());
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            File.WriteAllText(Path.Combine(directory, $"{host}-crt.pem"), cert.ExportCertificatePem());
            File.WriteAllText(Path.Combine(directory, $"{host}-key.pem"), key.ExportPkcs8PrivateKeyPem());
        }

        internal void Pause(string owner, string stage) { pausedOwner = owner; pausedStage = stage; }

        internal async Task CompleteInOrder(Task first, Task second)
        {
            try
            {
                // Observe the real queue, not elapsed time. Before the fix neither public path rents
                // the lifecycle, so finish the competing request to reproduce the stale continuation.
                if (References < 2) await second.WaitAsync(Token);
            }
            finally { Release.TrySetResult(); }
            await Task.WhenAll(first, second).WaitAsync(Token);
        }

        private async Task<CommandResult> Execute(string owner, string cli, string command, string? json,
            IReadOnlyDictionary<string, string> env, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(Path.Combine(cliDirectory, "stalwart-cli.exe"), cli);
            Assert.Equal("fixture-password", env["STALWART_PASSWORD"]);
            if (command == "get SystemSettings --json")
                return new CommandResult { ExitCode = 0, StandardOutput = "{\"defaultHostname\":\"other.test\",\"defaultCertificateId\":\"other-cert\"}" };
            if (command == "query Certificate --json" && readback)
            {
                readback = false;
                return new CommandResult { ExitCode = 0, StandardOutput = JsonSerializer.Serialize(certificates.Values) };
            }
            var stage = command == "query Certificate --json" ? "query" : command == "create Action" ? "reload" : "write";
            Commands.Add((owner, stage));
            if (owner == pausedOwner && stage == pausedStage)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
            if (stage == FailedStage) return new CommandResult { ExitCode = 1, StandardError = "fixture provider failure" };
            if (stage == "query") return new CommandResult { ExitCode = 0, StandardOutput = JsonSerializer.Serialize(certificates.Values) };
            if (stage == "write")
            {
                Assert.True(command == "create Certificate" || command.StartsWith("update Certificate "));
                using var payload = JsonDocument.Parse(json!);
                var cert = payload.RootElement.GetProperty("certificate").GetProperty("value").GetString()!;
                var key = payload.RootElement.GetProperty("privateKey").GetProperty("secret").GetString()!;
                using var parsed = X509Certificate2.CreateFromPem(cert, key); // a real matching PEM pair
                var host = parsed.GetNameInfo(X509NameType.DnsName, false);
                certificates[host] = new { id = host, subjectAlternativeNames = new Dictionary<string, bool> { [host] = true } };
                readback = true;
                writtenHash = Hash(cert + key);
            }
            else
            {
                Assert.Equal("{\"@type\":\"ReloadTlsCertificates\"}", json);
                LoadedHash = writtenHash;
            }
            return new CommandResult { ExitCode = 0 };
        }

        internal string PemHash(string directory)
        {
            var cert = File.ReadAllText(Directory.GetFiles(directory, "*-crt.pem").Single()).Replace("\r\n", "\n").Trim() + "\n";
            var key = File.ReadAllText(Directory.GetFiles(directory, "*-key.pem").Single());
            return Hash(cert + key);
        }
        private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        internal Task<StateRecord> State() => Store.ReadAsync("config/mail-cert.json", new StateRecord(), Token);
        public void Dispose()
        {
            Release.TrySetResult();
            foreach (var db in contexts) db.Dispose();
            connection.Dispose();
            area.Dispose();
        }
    }
}
