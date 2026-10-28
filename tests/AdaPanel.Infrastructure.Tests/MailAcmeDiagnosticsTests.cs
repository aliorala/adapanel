using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailAcmeDiagnosticsTests
{
    [Fact]
    public async Task TechnicalIisSiteIsResolvedByBindingAndItsRealIdReachesWinAcme()
    {
        using var f = new Fixture();
        Assert.True(await f.Service.TryIssueWithWinAcmeAsync("example.test", "mail.example.test", "contact@example.test", false, f.Pem, default));
        Assert.Equal("web-example.test", f.BindingSite);
        Assert.Contains("--siteid 47 ", f.Arguments);
        Assert.Contains("--host \"mail.example.test\"", f.Arguments);
        Assert.Equal(1, f.Executions);
    }

    [Theory]
    [InlineData("duplicate-domain")]
    [InlineData("duplicate-host")]
    [InlineData("other-site-host")]
    [InlineData("missing-binding-readback")]
    public async Task AmbiguousOrUnverifiedHttpBindingNeverStartsWinAcme(string failure)
    {
        using var f = new Fixture { Failure = failure };
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.TryIssueWithWinAcmeAsync("example.test", "mail.example.test", "contact@example.test", false, f.Pem, default));
        Assert.Equal(0, f.Executions);
        Assert.NotEmpty(error.Details);
    }

    [Fact]
    public void ExactNameFallbackDoesNotSelectAnUnrelatedSiteAndDuplicateAssociationFailsClosed()
    {
        Assert.Equal("example.test", MailService.ResolveManagedIdentitySite([new() { Name = "example.test" }], "example.test").Name);
        Assert.Throws<AppException>(() => MailService.ResolveManagedIdentitySite([new() { Name = "unrelated.test" }], "example.test"));
        Assert.Throws<AppException>(() => MailService.ResolveManagedIdentitySite([
            new() { Name = "example.test" }, new() { Name = "technical", Bindings = [Binding("example.test")] }
        ], "example.test"));
    }

    [Fact]
    public async Task IssuerExitCodeAndSanitizedStdoutStderrSurviveFailure()
    {
        using var f = new Fixture { Failure = "process" };
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.TryIssueWithWinAcmeAsync("example.test", "mail.example.test", "contact@example.test", false, f.Pem, default));
        Assert.Equal("mail.certificate_acme_failed", error.MessageKey);
        Assert.Contains("exit code: 23", error.Details);
        Assert.Contains("HTTP-01 returned 404", error.Details);
        Assert.Contains("stderr", error.Details);
        Assert.Contains("stdout", error.Details);
        Assert.DoesNotContain("PASSWORD-FIXTURE", error.Details);
        Assert.DoesNotContain("TOKEN-FIXTURE", error.Details);
        Assert.DoesNotContain("PRIVATE-PAYLOAD", error.Details);
        Assert.DoesNotContain("CHALLENGE-FIXTURE", error.Details);
    }

    [Fact]
    public void OutputTailKeepsFinalCauseAfterLongPrologueAndRedactsBeforeTruncation()
    {
        var detail = MailService.WinAcmeFailureDetails(new()
        {
            ExitCode = 1,
            StandardOutput = new string('x', 5000) + "\n-----BEGIN PRIVATE KEY-----\n" + new string('S', 4000)
                + "\n-----END PRIVATE KEY-----\nACME validation failed: connection refused"
        });
        Assert.Contains("connection refused", detail);
        Assert.DoesNotContain(new string('S', 30), detail);
        Assert.True(detail.Length <= SecretRedactor.DetailsMaxLength);
    }

    [Fact]
    public async Task SuccessfulProcessWithoutPemReportsConcreteMissingArtifact()
    {
        using var f = new Fixture { Failure = "missing-pem" };
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.TryIssueWithWinAcmeAsync("example.test", "mail.example.test", "contact@example.test", false, f.Pem, default));
        Assert.Contains("PEM private-key file was not produced", error.Details);
    }

    private static SiteBindingDto Binding(string host) => new() { Protocol = "http", Port = 80, HostName = host };

    private sealed class Fixture : IDisposable
    {
        private readonly BulutAlani area = new();
        private readonly AdaPanelDbContext db = new(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite("Data Source=:memory:").Options);
        internal string? Failure, BindingSite, Arguments;
        internal int Executions;
        private bool bound;
        internal MailService Service { get; }
        internal string Pem { get; }
        internal Fixture()
        {
            db.Database.OpenConnection();
            db.Database.EnsureCreated();
            var executable = area.DosyaDeposu.EnsureDirectory("fake-wacs");
            File.WriteAllText(Path.Combine(executable, "wacs.exe"), "fixture only; never executed");
            db.InstalledComponents.Add(new() { ComponentId = "win-acme", InstallDir = executable, Status = ComponentInstallStatus.Installed, Type = ComponentType.Zip });
            db.SaveChanges();
            Pem = area.DosyaDeposu.EnsureDirectory("pem");
            var iis = MailServerIdentityTests.IdentityProxy.Make<AdaPanel.Core.Interfaces.IIisSiteService>((method, args) =>
            {
                if (method == "GetSitesAsync")
                {
                    var bindings = new List<SiteBindingDto> { Binding("example.test") };
                    if (bound && Failure != "missing-binding-readback" || Failure == "duplicate-host") bindings.Add(Binding("mail.example.test"));
                    var sites = new List<SiteDto> { new() { Id = 47, Name = "web-example.test", Bindings = bindings } };
                    if (Failure == "duplicate-domain") sites.Add(new() { Id = 48, Name = "second", Bindings = [Binding("example.test")] });
                    if (Failure is "duplicate-host" or "other-site-host") sites.Add(new() { Id = 49, Name = "other", Bindings = [Binding("mail.example.test")] });
                    return Task.FromResult<IReadOnlyCollection<SiteDto>>(sites);
                }
                if (method == "AddBindingAsync")
                {
                    BindingSite = (string)args![0]!;
                    Assert.Equal("web-example.test", BindingSite);
                    bound = true;
                    return Task.FromResult(new SiteDto());
                }
                throw new InvalidOperationException("Unexpected site lookup: " + method);
            });
            var protector = new SahteKoruyucu();
            Service = new(area.DosyaDeposu, protector, null!, iis, null!, db,
                new StalwartRuntime(area.DosyaDeposu, protector, db, new SahteIstemciFabrikasi(new SahteBulut())), NullLogger<MailService>.Instance)
            {
                AcmeProcessRunner = (_, args, _) =>
                {
                    Arguments = args; Executions++;
                    if (Failure == "process") return Task.FromResult(new CommandResult
                    {
                        ExitCode = 23,
                        StandardOutput = "HTTP-01 returned 404 at http://mail.example.test/.well-known/acme-challenge/CHALLENGE-FIXTURE token=TOKEN-FIXTURE",
                        StandardError = "password=PASSWORD-FIXTURE\n-----BEGIN PRIVATE KEY-----\nPRIVATE-PAYLOAD\n-----END PRIVATE KEY-----"
                    });
                    if (Failure != "missing-pem") File.WriteAllText(Path.Combine(Pem, "fixture-key.pem"), "not a real key");
                    return Task.FromResult(new CommandResult { ExitCode = 0 });
                }
            };
        }
        public void Dispose() { db.Dispose(); area.Dispose(); }
    }
}
