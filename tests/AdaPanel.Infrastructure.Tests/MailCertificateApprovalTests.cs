using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public static IEnumerable<object[]> IncompleteCertificateInventories()
    {
        foreach (var json in new[]
        {
            "[{}]",
            "[{\"id\":\"cert-1\"}]",
            "[{\"id\":42,\"subjectAlternativeNames\":[\"mail.example.test\"]}]",
            "[{\"id\":\"\",\"subjectAlternativeNames\":[\"mail.example.test\"]}]",
            "[{\"id\":\"cert-1\",\"subjectAlternativeNames\":null}]",
            "[{\"id\":\"cert-1\",\"subjectAlternativeNames\":\"mail.example.test\"}]",
            "[{\"id\":\"cert-1\",\"subjectAlternativeNames\":[\"mail.example.test\",null]}]",
            "[{\"id\":\"cert-1\",\"subjectAlternativeNames\":[\"mail.example.test\",42]}]",
            "[{\"id\":\"cert-1\",\"subjectAlternativeNames\":[\"mail.example.test\",\"\"]}]",
            "[{\"id\":\"cert-1\",\"subjectAlternativeNames\":{\"mail.example.test\":false}}]",
            "[{\"id\":\"cert-1\",\"subjectAlternativeNames\":{\"mail.example.test\":null}}]",
            "[{\"id\":\"cert-1\",\"subjectAlternativeNames\":[\"mail.example.test\"]},{\"id\":\"cert-1\",\"subjectAlternativeNames\":[\"other.test\"]}]",
            "[{\"id\":\"cert-1\",\"id\":\"cert-2\",\"subjectAlternativeNames\":[\"mail.example.test\"]}]",
            "[{\"id\":\"cert-1\",\"subjectAlternativeNames\":[\"other.test\"],\"subjectAlternativeNames\":[\"mail.example.test\"]}]",
            "[{\"id\":\"cert-1\",\"subjectAlternativeNames\":{\"mail.example.test\":true,\"mail.example.test\":true}}]",
            "[{\"id\":\"cert-1\",\"subjectAlternativeNames\":[\"mail.example.test\"]},{\"id\":\"other-cert\"}]"
        }) yield return [json];
    }

    [Theory]
    [MemberData(nameof(IncompleteCertificateInventories))]
    public async Task IncompleteCertificateMetadataCannotAuthorizeAProviderWrite(string inventory)
    {
        using var f = new Fixture(true);
        await f.Initialize();
        var before = await f.Bytes();
        f.QueryResult = new CommandResult { ExitCode = 0, StandardOutput = inventory };
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.SyncCertificateAsync(f.Token));
        Assert.Equal("mail.certificate_push_failed", error.MessageKey);
        Assert.Equal(new[] { "query Certificate --json" }, f.Commands);
        Assert.Equal(before, await f.Bytes());
        Assert.False(f.Written);
    }

    [Theory]
    [MemberData(nameof(IncompleteCertificateInventories))]
    public async Task IncompleteInventoryIsUnknownEvenWhenListenersAreHealthy(string inventory)
    {
        using var f = new Fixture(true);
        await f.Initialize();
        await f.EditState(state => state["thumbprint"] = f.Thumbprint);
        f.QueryResult = new CommandResult { ExitCode = 0, StandardOutput = inventory };
        var result = await f.Service.GetCertificateStatusAsync(f.Token);
        Assert.True(result.Active);
        Assert.Equal("unknown", result.ConfigurationStatus);
        Assert.Empty(result.CertificateId);
        Assert.False(f.Written);
    }

    [Theory]
    [InlineData("[]", "missing", "")]
    [InlineData("[{\"id\":\"cert-1\",\"subjectAlternativeNames\":[]}]", "missing", "")]
    [InlineData("[{\"id\":\"cert-1\",\"subjectAlternativeNames\":{}}]", "missing", "")]
    [InlineData("[{\"id\":\"cert-1\",\"subjectAlternativeNames\":[\"mail.example.test\"]}]", "ok", "cert-1")]
    [InlineData("[{\"id\":\"cert-1\",\"subjectAlternativeNames\":{\"mail.example.test\":true}}]", "ok", "cert-1")]
    public async Task CompleteInventoryRemainsMeasurable(string inventory, string status, string id)
    {
        using var f = new Fixture(true);
        await f.Initialize();
        f.QueryResult = new CommandResult { ExitCode = 0, StandardOutput = inventory };
        var result = await f.Service.GetCertificateStatusAsync(f.Token);
        Assert.Equal(status, result.ConfigurationStatus);
        Assert.Equal(id, result.CertificateId);
        Assert.False(f.Written);
    }

    [Fact]
    public async Task IncompleteReadbackDoesNotReloadChangeDefaultOrPublishAndCanBeRetried()
    {
        using var f = new Fixture(true);
        await f.Initialize();
        f.DefaultHost = f.Host;
        f.DefaultId = "";
        var before = await f.Bytes();
        f.AfterWrite = () =>
        {
            f.QueryResult = new CommandResult { ExitCode = 0, StandardOutput = "[{\"id\":\"cert-1\"}]" };
            return Task.CompletedTask;
        };
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.SyncCertificateAsync(f.Token));
        Assert.Equal("mail.certificate_push_failed", error.MessageKey);
        Assert.True(f.Written);
        Assert.DoesNotContain("update SystemSettings", f.Commands);
        Assert.DoesNotContain("create Action", f.Commands);
        Assert.Equal(before, await f.Bytes());
        f.QueryResult = null;
        f.AfterWrite = null;
        // Keep this retry focused on inventory repair; default-setting behavior has separate coverage.
        f.DefaultId = "cert-1";
        Assert.True((await f.Service.SyncCertificateAsync(f.Token))!.Active);
    }

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
        Assert.Equal(new[] { "query Certificate --json", "get SystemSettings --json", existing ? "update Certificate cert-1" : "create Certificate", "query Certificate --json", "create Action" }, f.Commands);
        using var payload = JsonDocument.Parse(f.CertificatePayload!);
        Assert.Contains("BEGIN CERTIFICATE", payload.RootElement.GetProperty("certificate").GetProperty("value").GetString());
        Assert.Equal(f.PrivateKeyPem, payload.RootElement.GetProperty("privateKey").GetProperty("secret").GetString());
        Assert.Equal("{\"@type\":\"ReloadTlsCertificates\"}", f.ReloadPayload);
        using var state = JsonDocument.Parse(await f.Bytes());
        Assert.NotEqual("previous-hash", state.RootElement.GetProperty("contentHash").GetString());
        Assert.Equal(result.NotAfterUtc, state.RootElement.GetProperty("notAfterUtc").GetDateTime());
        Assert.Equal(f.Thumbprint, state.RootElement.GetProperty("thumbprint").GetString());
        Assert.All(result.Endpoints, endpoint => Assert.Equal(f.Thumbprint, endpoint.Thumbprint));
        Assert.True((await f.Service.GetCertificateStatusAsync(f.Token)).Active);
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
        Assert.Equal(5, f.Commands.Count);
        Assert.Equal(before, await f.Bytes());
    }

    private static async Task<Exception?> Capture(Func<Task<MailCertificateDto?>> action)
    { try { await action(); return null; } catch (Exception error) { return error; } }

    [Fact]
    public async Task FailedSmtpVerificationDoesNotPublishSuccessOrRenewalHash()
    {
        using var f = new Fixture(true);
        await f.Initialize();
        f.FailTls = true;
        var before = await f.Bytes();
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.SyncCertificateAsync(f.Token));
        Assert.Equal("mail.certificate_not_activated", error.MessageKey);
        Assert.Equal(before, await f.Bytes());
        Assert.DoesNotContain("update SystemSettings", f.Commands); // explicit other-domain default preserved
    }

    [Fact]
    public async Task EmptyDefaultIsInitializedOnlyForTheServersOwnHostnameAndReadBack()
    {
        using var f = new Fixture(true);
        await f.Initialize();
        f.DefaultHost = "mail.example.test";
        f.DefaultId = "";
        Assert.NotNull(await f.Service.SyncCertificateAsync(f.Token));
        Assert.Equal("cert-1", f.DefaultId);
        Assert.Contains("update SystemSettings", f.Commands);
        Assert.Equal(2, f.Commands.Count(c => c == "get SystemSettings --json"));
    }

    [Fact]
    public async Task PemRenewalDoesNotRequireExportingAnUnrelatedWindowsStoreKey()
    {
        using var f = new Fixture(true);
        await f.Initialize();
        f.FailStoreLookup = true;
        Assert.True((await f.Service.SyncCertificateAsync(f.Token))!.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedCertificateRepairsListenersServingTheWrongCertificate(bool windowsStore)
    {
        using var f = new Fixture(true);
        await f.Initialize();
        await f.Service.SyncCertificateAsync(f.Token);
        if (windowsStore) await f.UseWindowsStore();
        f.Commands.Clear();
        f.PresentedThumbprint = "wrong-certificate";
        var result = await f.Service.SyncCertificateAsync(f.Token);
        Assert.True(result!.Active);
        Assert.Contains("update Certificate cert-1", f.Commands);
        Assert.Equal(f.Thumbprint, f.PresentedThumbprint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedCertificateCannotHideFailedSmtpValidation(bool windowsStore)
    {
        using var f = new Fixture(true);
        await f.Initialize();
        await f.Service.SyncCertificateAsync(f.Token);
        if (windowsStore) await f.UseWindowsStore();
        var before = await f.Bytes();
        f.FailTls = true;
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.SyncCertificateAsync(f.Token));
        Assert.Equal("mail.certificate_not_activated", error.MessageKey);
        Assert.Equal(before, await f.Bytes());
    }

    [Fact]
    public async Task HealthyUnchangedWindowsCertificateIsMeasuredWithoutProviderWrites()
    {
        using var f = new Fixture(true);
        await f.Initialize();
        await f.UseWindowsStore();
        var before = await f.Bytes();
        Assert.Null(await f.Service.SyncCertificateAsync(f.Token));
        Assert.Equal(new[] { 465, 587 }, f.ProbedPorts.Order());
        Assert.Empty(f.Commands);
        Assert.Equal(before, await f.Bytes());
    }

    [Fact]
    public async Task OldPemThumbprintIsRepairedWithoutRewritingAHealthyListener()
    {
        using var f = new Fixture(true);
        await f.Initialize();
        await f.Service.SyncCertificateAsync(f.Token);
        await f.EditState(state => state["thumbprint"] = "stale-thumbprint");
        f.Commands.Clear();
        Assert.Null(await f.Service.SyncCertificateAsync(f.Token));
        Assert.Empty(f.Commands);
        Assert.True((await f.Service.GetCertificateStatusAsync(f.Token)).Active);
    }

    [Fact]
    public async Task UntrustedReplacementIsRejectedBeforeItCanOverwriteTheRunningCertificate()
    {
        using var f = new Fixture(true);
        await f.Initialize();
        f.EnforceTrust = true; // This fixture uses a genuinely untrusted self-signed leaf.
        var before = await f.Bytes();
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.SyncCertificateAsync(f.Token));
        Assert.Equal("mail.certificate_invalid", error.MessageKey);
        Assert.Empty(f.Commands);
        Assert.Equal(before, await f.Bytes());
        Assert.DoesNotContain(f.PrivateKeyPem, error.ToString());
    }

    [Theory]
    [InlineData(587)]
    [InlineData(465)]
    public async Task FailedFinalStoreInspectionDoesNotPublishNewState(int failingPort)
    {
        using var f = new Fixture(true);
        await f.Initialize();
        f.StoreMaterial = true;
        f.FailFinalInspectionPort = failingPort;
        var before = await f.Bytes();
        var error = await Assert.ThrowsAsync<AppException>(() =>
            f.Service.SyncWebCertificateAsync("mail.example.test", f.Approve, f.Token));
        Assert.Equal("mail.certificate_not_activated", error.MessageKey);
        Assert.Contains("update Certificate cert-1", f.Commands);
        Assert.Equal(before, await f.Bytes());
    }

    [Theory]
    [InlineData("smtp.example.test", "smtp.example.test")]
    [InlineData("smtp.example.test", "*.example.test")]
    [InlineData("mail.example.test", "*.example.test")]
    public async Task WebCertificateSyncTargetsTheConfiguredHostAndKeepsItsDomain(string mailHost, string webHost)
    {
        using var f = new Fixture(true);
        f.Host = mailHost;
        await f.Initialize();
        await f.EditState(state => state["domain"] = "associated.example.test");
        f.StoreMaterial = true;
        await f.Service.SyncWebCertificateAsync(webHost, f.Approve, f.Token);
        Assert.Equal(mailHost, Assert.Single(f.StoreLookups));
        Assert.Contains("update Certificate cert-1", f.Commands);
        using var state = JsonDocument.Parse(await f.Bytes());
        Assert.Equal(mailHost, state.RootElement.GetProperty("hostName").GetString());
        Assert.Equal("associated.example.test", state.RootElement.GetProperty("domain").GetString());
    }

    [Theory]
    [InlineData("smtp.unrelated.test")]
    [InlineData("*.unrelated.test")]
    [InlineData("*.test")]
    public async Task UnrelatedWebCertificatesDoNotChangeTheConfiguredMailCertificate(string webHost)
    {
        using var f = new Fixture(true);
        await f.Initialize();
        f.FailStoreLookup = true;
        var before = await f.Bytes();
        await f.Service.SyncWebCertificateAsync(webHost, f.Approve, f.Token);
        Assert.Empty(f.StoreLookups);
        Assert.Empty(f.Commands);
        Assert.Equal(before, await f.Bytes());
    }

    [Fact]
    public async Task WildcardWebSyncRevalidatesAuthorityBeforeMailMutation()
    {
        using var f = new Fixture(true);
        await f.Initialize();
        f.StoreMaterial = true;
        f.PauseQuery = true;
        var before = await f.Bytes();
        var action = f.Service.SyncWebCertificateAsync("*.example.test", f.Approve, f.Token);
        await f.QueryEntered.Task.WaitAsync(f.Token);
        await f.ChangeActor("disabled");
        f.QueryRelease.TrySetResult();
        var error = await Assert.ThrowsAsync<AppException>(() => action);
        Assert.Equal("auth.unauthorized", error.MessageKey);
        Assert.Single(f.Commands);
        Assert.Equal(before, await f.Bytes());
    }

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
        internal bool Written, FailTls;
        internal bool StoreMaterial, FailStoreLookup, EnforceTrust;
        internal string Host = "mail.example.test";
        internal readonly List<string> StoreLookups = [];
        internal int FailFinalInspectionPort;
        internal string CertificatePem = "", Thumbprint = "", PresentedThumbprint = "";
        internal readonly List<int> ProbedPorts = [];
        internal string DefaultHost = "other.example.test", DefaultId = "other-cert";
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
                NullLogger<MailService>.Instance)
            {
                CertificateCommandRunner = Execute,
                StoreCertificateLookup = host =>
                {
                    StoreLookups.Add(host);
                    return FailStoreLookup ? throw new CryptographicException("Store key cannot be exported")
                        : StoreMaterial ? new MailCertificateMaterial(CertificatePem, PrivateKeyPem, Thumbprint) : null;
                },
                ValidateCertificateMaterial = (host, cert, key) => { if (EnforceTrust) MailCertificateValidation.Validate(host, cert, key); },
                TlsProbe = (_, port, expected, _) =>
                {
                    ProbedPorts.Add(port);
                    return Task.FromResult(new MailTlsEndpointDto { Port = port, Thumbprint = PresentedThumbprint,
                        Status = FailTls || (port == FailFinalInspectionPort && ProbedPorts.Count(p => p == port) > 1)
                            || (!string.IsNullOrEmpty(expected) && expected != PresentedThumbprint) ? "fail" : "ok" });
                }
            };
        }

        internal async Task Initialize()
        {
            var pem = area.DosyaDeposu.EnsureDirectory("pem");
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=" + Host, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(Host);
            request.CertificateExtensions.Add(san.Build());
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            PrivateKeyPem = key.ExportPkcs8PrivateKeyPem();
            CertificatePem = certificate.ExportCertificatePem();
            Thumbprint = PresentedThumbprint = certificate.Thumbprint;
            await File.WriteAllTextAsync(Path.Combine(pem, "mail-key.pem"), PrivateKeyPem, Token);
            await File.WriteAllTextAsync(Path.Combine(pem, "mail-crt.pem"), certificate.ExportCertificatePem(), Token);
            await area.DosyaDeposu.WriteAsync("config/mail-cert.json", new
            { domain = "example.test", hostName = Host, source = "letsencrypt", pemDirectory = pem, contentHash = "previous-hash", thumbprint = "old-thumbprint" }, Token);
            await area.DosyaDeposu.WriteAsync("config/mail.json", new
            { adminPasswordProtected = SahteKoruyucu.Onek + "fixture-password" }, Token);
            principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            { new Claim(ClaimTypes.NameIdentifier, actorId.ToString()), new Claim(ClaimTypes.Role, "Administrator") }, "fixture"));
            authority = await BackupActorAuthority.CaptureAsync(db, null!, principal, Token);
        }

        internal Task Approve(CancellationToken token) => authority.RevalidateAsync(db, null!, principal, token);
        internal async Task EditState(Action<JsonObject> edit)
        {
            var state = JsonNode.Parse(await Bytes())!.AsObject();
            edit(state);
            await area.DosyaDeposu.WriteAsync("config/mail-cert.json", state, Token);
        }
        internal async Task UseWindowsStore()
        {
            StoreMaterial = true;
            await EditState(state => { state["source"] = "windows-store"; state["thumbprint"] = Thumbprint; });
        }
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
                return QueryResult ?? new CommandResult { ExitCode = 0, StandardOutput = (existing || Written)
                    ? JsonSerializer.Serialize(new[] { new { id = "cert-1", subjectAlternativeNames = new[] { Host } } }) : "[]" };
            }
            if (command == "get SystemSettings --json")
                return new CommandResult { ExitCode = 0, StandardOutput = JsonSerializer.Serialize(new { defaultHostname = DefaultHost, defaultCertificateId = DefaultId }) };
            if (command == "update SystemSettings")
            {
                using var settings = JsonDocument.Parse(json!);
                Assert.Single(settings.RootElement.EnumerateObject());
                DefaultId = settings.RootElement.GetProperty("defaultCertificateId").GetString()!;
                return new CommandResult { ExitCode = 0 };
            }
            if (command.StartsWith("create Certificate", StringComparison.Ordinal) || command.StartsWith("update Certificate", StringComparison.Ordinal))
            {
                CertificatePayload = json;
                Written = true;
                if (AfterWrite is not null) await AfterWrite();
                return new CommandResult { ExitCode = 0 };
            }
            Assert.Equal("create Action", command);
            ReloadPayload = json;
            if (!FailReload && json!.Contains("ReloadTlsCertificates")) PresentedThumbprint = Thumbprint;
            return new CommandResult { ExitCode = FailReload ? 1 : 0 };
        }

        internal Task<byte[]> Bytes() => File.ReadAllBytesAsync(area.DosyaDeposu.ResolvePath("config/mail-cert.json"), Token);
        public void Dispose() { QueryRelease.TrySetResult(); db.Dispose(); connection.Dispose(); area.Dispose(); }
    }
}
