using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ftp;
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

public sealed class SslFtpRenewalWiringTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FtpSyncRunsWithAndWithoutAcmeAndPreservesMutationAuthority(bool installed, bool authorized)
    {
        using var f = new Fixture(installed);
        var approvals = 0;
        Task Approve(CancellationToken token) { token.ThrowIfCancellationRequested(); approvals++; return Task.CompletedTask; }
        var result = authorized ? await f.Service.RenewAllAsync(Approve, f.Area.Iptal) : await f.Service.RenewAllAsync(f.Area.Iptal);
        Assert.Equal(installed, result.Measured);
        Assert.Equal(1, f.Calls);
        Assert.Equal(authorized, f.HasApproval);
        Assert.Equal(authorized ? 2 : 0, approvals);
    }

    [Fact]
    public async Task FtpFailureIsReportedInTheRenewalResult()
    {
        using var f = new Fixture(true);
        f.Fail = true;
        var result = await f.Service.RenewAllAsync(f.Area.Iptal);
        Assert.Contains("ftp.certificate_probe_failed", result.FailureReason);
        Assert.Contains("ftp.certificate_probe_failed", result.Output);
    }

    [Fact]
    public async Task ARevokedRenewalAuthorityIsNotSwallowedAsAnEndpointWarning()
    {
        using var f = new Fixture(false);
        var calls = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RenewAllAsync(_ =>
        { if (++calls > 1) throw new UnauthorizedAccessException(); return Task.CompletedTask; }, f.Area.Iptal));
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly BulutAlani Area = new();
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly AdaPanelDbContext db;
        internal readonly WinAcmeSslService Service;
        internal int Calls;
        internal bool HasApproval, Fail;
        internal Fixture(bool installed)
        {
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            if (installed)
            {
                var directory = Area.DosyaDeposu.EnsureDirectory("fake-acme");
                File.WriteAllText(Path.Combine(directory, "wacs.exe"), "never executed");
                db.InstalledComponents.Add(new InstalledComponent { ComponentId = "win-acme", Status = ComponentInstallStatus.Installed, InstallDir = directory });
                db.SaveChanges();
            }
            var ftp = DispatchProxy.Create<IFtpService, Proxy>();
            ((Proxy)(object)ftp).Run = async args =>
            {
                Calls++;
                if (args[0] is Func<CancellationToken, Task> approval) { HasApproval = true; await approval((CancellationToken)args[1]!); }
                if (Fail) throw new AppException("ftp.certificate_probe_failed", 502, 502);
                return new FtpCertificateDto { CertificateVerified = true };
            };
            Service = new WinAcmeSslService(db, null!, null!, Area.DosyaDeposu, null!, NullLogger<WinAcmeSslService>.Instance,
                bindingReader: _ => Task.FromResult<IReadOnlyCollection<IisHttpsBinding>?>([]), ftpService: ftp)
            { RenewalCommand = (_, _, _) => Task.FromResult(new CommandResult { ExitCode = 0 }) };
        }
        public void Dispose() { db.Dispose(); connection.Dispose(); Area.Dispose(); }
    }
    public class Proxy : DispatchProxy
    {
        public Func<object?[], Task<FtpCertificateDto?>> Run { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method!.Name == "SyncCertificateAsync" ? Run(args!) : throw new InvalidOperationException(method.Name);
    }
}
