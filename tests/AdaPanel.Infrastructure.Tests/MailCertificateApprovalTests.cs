using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailCertificateApprovalTests
{
    public static IEnumerable<object[]> Revocations() =>
        from existing in new[] { false, true }
        from change in new[] { "disabled", "deleted", "role", "password" }
        select new object[] { existing, change };

    [Theory]
    [MemberData(nameof(Revocations))]
    public async Task SyncRevalidatesActorAfterTheCertificateInventoryWait(bool existing, string change)
    {
        using var f = new Fixture(existing);
        await f.Initialize();
        f.PauseQuery = true;
        var before = await f.Bytes();
        var action = Capture(() => ((IMailService)f.Service).SyncCertificateAsync(f.Approve, f.Token));
        await f.QueryEntered.Task.WaitAsync(f.Token);
        await f.ChangeActor(change);
        f.QueryRelease.TrySetResult();
        var error = Assert.IsType<AppException>(await action.WaitAsync(f.Token));
        Assert.Equal("auth.unauthorized", error.MessageKey);
        Assert.Equal(new[] { "query Certificate --json" }, f.Commands);
        Assert.Equal(before, await f.Bytes());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncPreservesTheOriginalApprovalExceptionAfterTheQuery(bool canceled)
    {
        using var f = new Fixture(existing: true);
        await f.Initialize();
        f.PauseQuery = true;
        var rejection = canceled ? (Exception)new OperationCanceledException("approval interrupted")
            : new AppException("fixture.approval_rejected", 403, 403);
        var deny = false;
        Task Check(CancellationToken _) => deny ? Task.FromException(rejection) : Task.CompletedTask;
        var before = await f.Bytes();
        var action = Capture(() => ((IMailService)f.Service).SyncCertificateAsync(Check, f.Token));
        await f.QueryEntered.Task.WaitAsync(f.Token);
        deny = true;
        f.QueryRelease.TrySetResult();
        Assert.Same(rejection, await action.WaitAsync(f.Token));
        Assert.Single(f.Commands);
        Assert.Equal(before, await f.Bytes());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SyncWritesReloadsAndPublishesTheMeasuredCertificate(bool existing, bool trusted)
    {
        using var f = new Fixture(existing);
        await f.Initialize();
        var result = trusted ? await f.Service.SyncCertificateAsync(f.Token)
            : await ((IMailService)f.Service).SyncCertificateAsync(f.Approve, f.Token);
        Assert.NotNull(result);
        Assert.True(result.Active);
        Assert.Equal("mail.example.test", result.HostName);
        Assert.NotNull(result.NotAfterUtc);
        Assert.Equal(new[] { "query Certificate --json", existing ? "update Certificate cert-1" : "create Certificate", "create Action" }, f.Commands);
        using var payload = JsonDocument.Parse(f.CertificatePayload!);
        Assert.Contains("BEGIN CERTIFICATE", payload.RootElement.GetProperty("certificate").GetProperty("value").GetString());
        Assert.Equal(f.PrivateKeyPem, payload.RootElement.GetProperty("privateKey").GetProperty("secret").GetString());
        Assert.Equal("{\"@type\":\"ReloadTlsCertificates\"}", f.ReloadPayload);
        using var state = JsonDocument.Parse(await f.Bytes());
        Assert.NotEqual("previous-hash", state.RootElement.GetProperty("contentHash").GetString());
        Assert.Equal(result.NotAfterUtc, state.RootElement.GetProperty("notAfterUtc").GetDateTime());
        f.Commands.Clear();
        Assert.Null(await f.Service.SyncCertificateAsync(f.Token));
        Assert.Empty(f.Commands); // the actual persisted hash makes the next sync a no-op
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnApprovedWriteFinishesReloadAndPublicationEvenIfActorChangesAfterWrite(bool existing)
    {
        using var f = new Fixture(existing);
        await f.Initialize();
        f.AfterWrite = () => f.ChangeActor("disabled");
        var result = await ((IMailService)f.Service).SyncCertificateAsync(f.Approve, f.Token);
        Assert.NotNull(result);
        Assert.Equal("create Action", f.Commands.Last());
        using var state = JsonDocument.Parse(await f.Bytes());
        Assert.NotEqual("previous-hash", state.RootElement.GetProperty("contentHash").GetString());
    }

    [Fact]
    public async Task AnInitiallyRejectedActorDoesNotQueryOrMutateTheMailServer()
    {
        using var f = new Fixture(existing: false);
        await f.Initialize();
        var before = await f.Bytes();
        await f.ChangeActor("disabled");
        await Assert.ThrowsAsync<AppException>(() => ((IMailService)f.Service).SyncCertificateAsync(f.Approve, f.Token));
        Assert.Empty(f.Commands);
        Assert.Equal(before, await f.Bytes());
    }

    [Fact]
    public async Task CanceledInventoryWaitDoesNotWriteReloadOrPublish()
    {
        using var f = new Fixture(existing: true);
        await f.Initialize();
        f.PauseQuery = true;
        var before = await f.Bytes();
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var action = ((IMailService)f.Service).SyncCertificateAsync(f.Approve, canceled.Token);
        await f.QueryEntered.Task.WaitAsync(f.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action.WaitAsync(f.Token));
        Assert.Single(f.Commands);
        Assert.Equal(before, await f.Bytes());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnmeasuredCertificateQueryNeverCreatesADuplicate(bool malformed)
    {
        using var f = new Fixture(existing: true);
        await f.Initialize();
        f.QueryResult = malformed ? new CommandResult { ExitCode = 0, StandardOutput = "{broken" }
            : new CommandResult { ExitCode = 1, StandardError = "query failed" };
        var before = await f.Bytes();
        var error = await Assert.ThrowsAsync<AppException>(() => ((IMailService)f.Service).SyncCertificateAsync(f.Approve, f.Token));
        Assert.Equal("mail.certificate_push_failed", error.MessageKey);
        Assert.Single(f.Commands);
        Assert.Equal(before, await f.Bytes());
    }

    [Fact]
    public async Task FailedReloadDoesNotPublishTheNewCertificateHash()
    {
        using var f = new Fixture(existing: true);
        await f.Initialize();
        f.FailReload = true;
        var before = await f.Bytes();
        var error = await Assert.ThrowsAsync<AppException>(() => ((IMailService)f.Service).SyncCertificateAsync(f.Approve, f.Token));
        Assert.Equal("mail.certificate_not_activated", error.MessageKey);
        Assert.Equal(3, f.Commands.Count);
        Assert.Equal(before, await f.Bytes());
    }

    private static async Task<Exception?> Capture(Func<Task<MailCertificateDto?>> action)
    { try { await action(); return null; } catch (Exception error) { return error; } }

    private sealed class Fixture : IDisposable
    {
        private readonly BulutAlani area = new();
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly AdaPanelDbContext db;
        private readonly bool existing;
        private readonly Guid actorId = Guid.NewGuid();
        private BackupActorAuthority authority = null!;
        private ClaimsPrincipal principal = null!;
        private readonly string cliDirectory;
        internal readonly MailService Service;
        internal readonly List<string> Commands = [];
        internal readonly TaskCompletionSource QueryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource QueryRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool PauseQuery, FailReload;
        internal string PrivateKeyPem = "";
        internal string? CertificatePayload, ReloadPayload;
        internal Func<Task>? AfterWrite;
        internal CommandResult? QueryResult;
        internal CancellationToken Token => area.Iptal;

        internal Fixture(bool existing)
        {
            this.existing = existing;
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            cliDirectory = area.DosyaDeposu.EnsureDirectory("fake-cli");
            // Runtime resolves the registered file; only the certificate command delegate runs it.
            File.WriteAllText(Path.Combine(cliDirectory, "stalwart-cli.exe"), "not an executable");
            db.InstalledComponents.Add(new InstalledComponent
            { ComponentId = "stalwart-cli", InstallDir = cliDirectory, Status = ComponentInstallStatus.Installed, Type = ComponentType.Zip });
            db.Users.Add(new PanelUser
            { Id = actorId, UserName = "mail-admin", Email = "admin@example.test", IsActive = true, Role = UserRole.Administrator, PasswordHash = "initial-hash" });
            db.SaveChanges();
            var protector = new SahteKoruyucu();
            var runtime = new StalwartRuntime(area.DosyaDeposu, protector, db, new SahteIstemciFabrikasi(new SahteBulut()));
            Service = new MailService(area.DosyaDeposu, protector, null!, null!, null!, db, runtime,
                NullLogger<MailService>.Instance) { CertificateCommandRunner = Execute };
        }

        internal async Task Initialize()
        {
            var pem = area.DosyaDeposu.EnsureDirectory("pem");
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=mail.example.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            PrivateKeyPem = key.ExportPkcs8PrivateKeyPem();
            await File.WriteAllTextAsync(Path.Combine(pem, "mail-key.pem"), PrivateKeyPem, Token);
            await File.WriteAllTextAsync(Path.Combine(pem, "mail-crt.pem"), certificate.ExportCertificatePem(), Token);
            await area.DosyaDeposu.WriteAsync("config/mail-cert.json", new
            { domain = "example.test", hostName = "mail.example.test", source = "letsencrypt", pemDirectory = pem, contentHash = "previous-hash" }, Token);
            await area.DosyaDeposu.WriteAsync("config/mail.json", new
            { adminPasswordProtected = SahteKoruyucu.Onek + "fixture-password" }, Token);
            principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            { new Claim(ClaimTypes.NameIdentifier, actorId.ToString()), new Claim(ClaimTypes.Role, "Administrator") }, "fixture"));
            authority = await BackupActorAuthority.CaptureAsync(db, null!, principal, Token);
        }

        internal Task Approve(CancellationToken token) => authority.RevalidateAsync(db, null!, principal, token);
        internal async Task ChangeActor(string change)
        {
            var user = await db.Users.SingleAsync(item => item.Id == actorId, Token);
            switch (change)
            {
                case "disabled": user.IsActive = false; break;
                case "deleted": db.Users.Remove(user); break;
                case "role": user.Role = UserRole.Customer; break;
                case "password": user.PasswordHash = "changed-hash"; break;
                default: throw new InvalidOperationException(change);
            }
            await db.SaveChangesAsync(Token);
        }

        private async Task<CommandResult> Execute(string cli, string command, string? json,
            IReadOnlyDictionary<string, string> environment, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(Path.Combine(cliDirectory, "stalwart-cli.exe"), cli);
            Assert.Equal("fixture-password", environment["STALWART_PASSWORD"]);
            Commands.Add(command);
            if (command == "query Certificate --json")
            {
                QueryEntered.TrySetResult();
                if (PauseQuery) await QueryRelease.Task.WaitAsync(token);
                return QueryResult ?? new CommandResult { ExitCode = 0, StandardOutput = existing
                    ? "[{\"id\":\"cert-1\",\"subjectAlternativeNames\":{\"mail.example.test\":{}}}]" : "[]" };
            }
            if (command.StartsWith("create Certificate", StringComparison.Ordinal) || command.StartsWith("update Certificate", StringComparison.Ordinal))
            {
                CertificatePayload = json;
                if (AfterWrite is not null) await AfterWrite();
                return new CommandResult { ExitCode = 0 };
            }
            Assert.Equal("create Action", command);
            ReloadPayload = json;
            return new CommandResult { ExitCode = FailReload ? 1 : 0 };
        }

        internal Task<byte[]> Bytes() => File.ReadAllBytesAsync(area.DosyaDeposu.ResolvePath("config/mail-cert.json"), Token);
        public void Dispose() { QueryRelease.TrySetResult(); db.Dispose(); connection.Dispose(); area.Dispose(); }
    }
}
