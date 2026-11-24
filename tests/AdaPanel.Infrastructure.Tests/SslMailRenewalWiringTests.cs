using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SslMailRenewalWiringTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MailSyncDoesNotDependOnWinAcmeAndRetainsApproval(bool installed, bool approval)
    {
        using var f = new Fixture(installed);
        var approvals = 0;
        Task Approve(CancellationToken token) { token.ThrowIfCancellationRequested(); approvals++; return Task.CompletedTask; }
        var result = approval ? await f.Service.RenewAllAsync(Approve, f.Area.Iptal)
            : await f.Service.RenewAllAsync(f.Area.Iptal);
        Assert.Equal(installed, result.Measured);
        Assert.Equal(1, f.MailCalls);
        Assert.Equal(approval, f.MailReceivedApproval);
        Assert.Equal(approval ? 2 : 0, approvals);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CompletedMailSynchronizationAndItsResultAreCarriedToTheCaller(bool installed, bool changed)
    {
        using var f = new Fixture(installed);
        f.MailResult = changed ? new MailCertificateDto { Active = true, HostName = "mail.example.test" } : null;
        var result = await f.Service.RenewAllAsync(f.Area.Iptal);
        Assert.True(result.MailSynchronizationPerformed);
        Assert.Same(f.MailResult, result.MailCertificate);
        Assert.Equal(1, f.MailCalls);
    }

    [Fact]
    public async Task MissingWinAcmeDoesNotHideMailValidationFailure()
    {
        using var f = new Fixture(false);
        f.FailMail = true;
        var result = await f.Service.RenewAllAsync(f.Area.Iptal);
        Assert.Contains("mail.certificate_not_activated", result.FailureReason);
        Assert.Contains("mail.certificate_not_activated", result.Output);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly BulutAlani Area = new();
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly AdaPanelDbContext db;
        internal readonly WinAcmeSslService Service;
        internal int MailCalls;
        internal bool MailReceivedApproval, FailMail;
        internal MailCertificateDto? MailResult;

        internal Fixture(bool installed)
        {
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            if (installed)
            {
                var directory = Area.DosyaDeposu.EnsureDirectory("fake-acme");
                File.WriteAllText(Path.Combine(directory, "wacs.exe"), "fixture, never executed");
                db.InstalledComponents.Add(new InstalledComponent
                    { ComponentId = "win-acme", Status = ComponentInstallStatus.Installed, InstallDir = directory });
                db.SaveChanges();
            }
            var mail = DispatchProxy.Create<IMailService, Proxy>();
            ((Proxy)(object)mail).Run = SyncMail;
            Service = new WinAcmeSslService(db, null!, null!, Area.DosyaDeposu, null!,
                NullLogger<WinAcmeSslService>.Instance,
                bindingReader: _ => Task.FromResult<IReadOnlyCollection<IisHttpsBinding>?>([]), mailService: mail)
            { RenewalCommand = (_, _, _) => Task.FromResult(new CommandResult { ExitCode = 0 }) };
        }

        private async Task<MailCertificateDto?> SyncMail(object?[] args)
        {
            MailCalls++;
            if (args[0] is Func<CancellationToken, Task> approve)
            {
                MailReceivedApproval = true;
                await approve((CancellationToken)args[1]!);
            }
            if (FailMail) throw new AppException("mail.certificate_not_activated", 502, 502);
            return MailResult;
        }
        public void Dispose() { db.Dispose(); connection.Dispose(); Area.Dispose(); }
    }

    public class Proxy : DispatchProxy
    {
        public Func<object?[], Task<MailCertificateDto?>> Run { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method!.Name == "SyncCertificateAsync" ? Run(args!) : throw new InvalidOperationException(method.Name);
    }
}
