using System.Reflection;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>Actual SSL service/controller with temporary state and command-boundary fakes; no IIS or process runs.</summary>
public sealed class SslRedirectOutcomeTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task AFailedWriteCannotPublishSuccessEvenWhenTheReadbackMatches(bool enabled, bool matches)
    {
        using var f = new Fixture(enabled);
        f.Writes.Enqueue(Result(1, "", "write failed; password=redirect-sentinel"));
        f.Read = Result(0, matches == enabled ? "YES" : "NO");
        await AssertRejected(f, enabled);
        Assert.Equal(1, f.WriteCalls);
        Assert.Equal(0, f.InstallCalls);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("failed-status")]
    [InlineData("still-missing")]
    [InlineData("retry-exit")]
    [InlineData("retry-empty")]
    public async Task MissingRewriteMustInstallAndRetrySuccessfullyBeforePublishing(string failure)
    {
        using var f = new Fixture(true);
        f.Writes.Enqueue(Result(0, "NOREWRITE"));
        f.Writes.Enqueue(failure switch
        {
            "still-missing" => Result(0, "NOREWRITE"),
            "retry-exit" => Result(1, "", "retry failed"),
            "retry-empty" => Result(0, ""),
            _ => Result(0, "OK")
        });
        if (failure == "throw") f.InstallFailure = new IOException("installer failed");
        if (failure == "failed-status") f.InstallStatus = "Failed";
        await AssertRejected(f, true);
        Assert.Equal(1, f.InstallCalls);
        if (failure is "throw" or "failed-status") Assert.Equal(1, f.WriteCalls);
        else Assert.Equal(2, f.WriteCalls);
    }

    [Fact]
    public async Task MissingRewriteMayRecoverThroughOneMeasuredSuccessfulRetry()
    {
        using var f = new Fixture(true);
        f.Writes.Enqueue(Result(0, "NOREWRITE"));
        f.Writes.Enqueue(Result(0, "OK"));
        var response = await f.Set(true);
        Assert.True(response.Data!.HttpsRedirectMeasured);
        Assert.True(response.Data.HttpsRedirectEnabled);
        Assert.Equal(2, f.WriteCalls);
        Assert.Equal(1, f.InstallCalls);
        Assert.Single(f.Area.Denetim.Kayitlar);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AMeasuredOppositeStateCannotBeReportedAsTheRequestedChange(bool enabled)
    {
        using var f = new Fixture(!enabled);
        await AssertRejected(f, enabled);
    }

    public static IEnumerable<object[]> UnknownReadbacks() =>
        new[] { "failed", "empty", "unexpected", "conflicting", "throws" }.Select(kind => new object[] { kind });

    [Theory]
    [MemberData(nameof(UnknownReadbacks))]
    public async Task UnknownReadbackCannotClaimThatADisableSucceeded(string kind)
    {
        using var f = new Fixture(false);
        f.UnknownRead(kind);
        await AssertRejected(f, false);
    }

    [Theory]
    [MemberData(nameof(UnknownReadbacks))]
    public async Task WorkspaceKeepsUnreadableRedirectStateUnknown(string kind)
    {
        using var f = new Fixture(false);
        f.UnknownRead(kind);
        var state = await f.Service.GetSiteWorkspaceAsync(Fixture.SiteName, f.Token);
        Assert.False(state.HttpsRedirectMeasured);
        Assert.False(state.HttpsRedirectEnabled);
        Assert.Equal(0, f.WriteCalls);
        Assert.Equal(0, f.InstallCalls);
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    [Theory]
    [InlineData("YES", true)]
    [InlineData("NO", false)]
    [InlineData("  YES\r\n", true)]
    [InlineData("\r\nNO\r\n", false)]
    public async Task WorkspaceDistinguishesBothMeasuredStatesFromUnknown(string output, bool enabled)
    {
        using var f = new Fixture(enabled);
        f.Read = Result(0, output);
        var state = await f.Service.GetSiteWorkspaceAsync(Fixture.SiteName, f.Token);
        Assert.True(state.HttpsRedirectMeasured);
        Assert.Equal(enabled, state.HttpsRedirectEnabled);
        Assert.Equal(0, f.WriteCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AConfirmedChangePublishesItsActualStateAndOneAudit(bool enabled)
    {
        using var f = new Fixture(enabled);
        var response = await f.Set(enabled);
        Assert.True(response.Success);
        Assert.True(response.Data!.HttpsRedirectMeasured);
        Assert.Equal(enabled, response.Data.HttpsRedirectEnabled);
        Assert.Equal(1, f.WriteCalls);
        Assert.Equal(1, f.ReadCalls);
        Assert.Equal(0, f.InstallCalls);
        var audit = Assert.Single(f.Area.Denetim.Kayitlar);
        Assert.Equal("ssl.https_redirect_changed", audit.Action);
        Assert.Contains($"={enabled}", audit.Details);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("write")]
    [InlineData("read")]
    [InlineData("install")]
    public async Task CancellationRemainsCancellationAndDoesNotWriteASuccessAudit(string boundary)
    {
        using var f = new Fixture(true);
        using var canceled = new CancellationTokenSource();
        if (boundary == "before") canceled.Cancel();
        if (boundary == "write") f.WriteFailure = new OperationCanceledException("write canceled");
        if (boundary == "read") f.ReadFailure = new OperationCanceledException("read canceled");
        if (boundary == "install")
        {
            f.Writes.Enqueue(Result(0, "NOREWRITE"));
            f.InstallFailure = new OperationCanceledException("install canceled");
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Set(true, canceled.Token));
        Assert.Empty(f.Area.Denetim.Kayitlar);
        if (boundary == "before") Assert.Equal(0, f.WriteCalls);
        if (boundary == "install") Assert.Equal(1, f.WriteCalls);
    }

    private static async Task AssertRejected(Fixture f, bool enabled)
    {
        var error = await Assert.ThrowsAsync<AppException>(() => f.Set(enabled));
        Assert.Equal("ssl.https_redirect_failed", error.MessageKey);
        Assert.InRange(error.HttpStatusCode, 400, 599);
        Assert.DoesNotContain("redirect-sentinel", error.Details);
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    [Theory]
    [MemberData(nameof(UnknownReadbacks))]
    public async Task UnknownRedirectStateCannotRemoveTheLastHttpsBinding(string kind)
    {
        using var f = new Fixture(false);
        f.AddLastBinding();
        f.UnknownRead(kind);
        var error = await Assert.ThrowsAsync<AppException>(() => f.Remove());
        Assert.Equal("ssl.https_redirect_failed", error.MessageKey);
        Assert.Equal(0, f.BindingRemovalCalls);
        Assert.Equal(0, f.WriteCalls);
        Assert.Single(f.Bindings!);
    }

    [Fact]
    public async Task RedirectReadCancellationCannotRemoveTheLastHttpsBinding()
    {
        using var f = new Fixture(false);
        f.AddLastBinding();
        f.ReadFailure = new OperationCanceledException("redirect read canceled");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Remove());
        Assert.Equal(0, f.BindingRemovalCalls);
        Assert.Single(f.Bindings!);
    }

    [Fact]
    public async Task AMeasuredDisabledRedirectStillAllowsRemovingTheLastBinding()
    {
        using var f = new Fixture(false);
        f.AddLastBinding();
        f.AllowBindingRemoval = true;
        var result = await f.Remove();
        Assert.True(result.BindingRemoved);
        Assert.Equal("kept", result.CertificateAction);
        Assert.Equal(1, f.BindingRemovalCalls);
        Assert.Equal(0, f.WriteCalls);
        Assert.Empty(f.Bindings!);
        Assert.True(result.Workspace!.HttpsRedirectMeasured);
        Assert.False(result.Workspace.HttpsRedirectEnabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CertificateFlowsCanKeepTheBestEffortRedirectResultWithoutClaimingSuccess(bool enabled)
    {
        using var f = new Fixture(enabled);
        f.Writes.Enqueue(Result(1, "", "redirect write failed"));
        // Certificate issuance/import use this shared helper as best-effort. The explicit
        // settings operation checks its result; failure must not become a helper exception.
        var helper = typeof(WinAcmeSslService).GetMethod("ApplyHttpsRedirectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = (Task<bool>)helper.Invoke(f.Service, [Fixture.SiteName, enabled, f.Token])!;
        Assert.False(await result);
        Assert.Equal(1, f.WriteCalls);
    }

    private static CommandResult Result(int code, string output, string error = "") =>
        new() { ExitCode = code, StandardOutput = output, StandardError = error };

    private sealed class Fixture : IDisposable
    {
        internal const string SiteName = "redirect.example.test";
        internal readonly BulutAlani Area = new();
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly AdaPanelDbContext db;
        private readonly Guid administrator = Guid.NewGuid();
        private readonly IIisSiteService sites;
        private readonly List<ServiceProvider> providers = [];
        internal readonly WinAcmeSslService Service;
        internal readonly Queue<CommandResult> Writes = new();
        internal CommandResult Read;
        internal Exception? WriteFailure;
        internal Exception? ReadFailure;
        internal Exception? InstallFailure;
        internal string InstallStatus = "Installed";
        internal int WriteCalls;
        internal int ReadCalls;
        internal int InstallCalls;
        internal IReadOnlyCollection<IisHttpsBinding>? Bindings = [];
        internal int BindingRemovalCalls;
        internal bool AllowBindingRemoval;
        internal CancellationToken Token => Area.Iptal;

        internal Fixture(bool enabled)
        {
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            db.Users.Add(new PanelUser
            {
                Id = administrator, UserName = "redirect-administrator", Email = "redirect@example.test",
                PasswordHash = "redirect-fixture-hash", Role = UserRole.Administrator, IsActive = true
            });
            db.SaveChanges();
            Read = Result(0, enabled ? "YES" : "NO");
            var site = new SiteDto { Id = 1, Name = SiteName, PhysicalPath = Area.SiteKoku,
                ApplicationRootPath = Area.SiteKoku, ApplicationPoolName = "redirect-pool" };
            sites = Proxy<IIisSiteService>((method, args) =>
            {
                args.OfType<CancellationToken>().Single().ThrowIfCancellationRequested();
                return method.Name == "GetSiteAsync" ? Task.FromResult(site) : throw new NotSupportedException(method.Name);
            });
            var installer = Proxy<IComponentBootstrapper>((method, args) =>
            {
                Assert.Equal("InstallAsync", method.Name);
                Assert.Equal("url-rewrite", args[0]);
                args.OfType<CancellationToken>().Single().ThrowIfCancellationRequested();
                InstallCalls++;
                if (InstallFailure is not null) throw InstallFailure;
                return Task.FromResult(new ComponentInstallResultDto { ComponentId = "url-rewrite", Status = InstallStatus });
            });
            Service = new WinAcmeSslService(db, sites, null!, Area.DosyaDeposu, installer,
                NullLogger<WinAcmeSslService>.Instance,
                _ => Task.FromResult<IReadOnlyCollection<IisHttpsBinding>?>(Bindings))
            {
                RedirectCommand = RunCommand,
                RemoveBindingCommand = (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    BindingRemovalCalls++;
                    if (!AllowBindingRemoval) throw new InvalidOperationException("Binding removal must not run before redirect safety is measured.");
                    Bindings = [];
                    return Task.FromResult(Result(0, "OK"));
                }
            };
        }

        internal void AddLastBinding() => Bindings = [new()
        {
            SiteName = SiteName, BindingInformation = "*:443:" + SiteName,
            Thumbprint = "fixture-only", CertificateReadable = true
        }];

        internal Task<RemoveSslCertificateResultDto> Remove() => Service.RemoveCertificateAsync(
            new() { SiteName = SiteName, HostName = SiteName, Port = 443 }, Token);

        private Task<CommandResult> RunCommand(string script, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            // Distinguish actual production write/read scripts at the external command boundary.
            if (script.Contains("Clear-WebConfiguration", StringComparison.Ordinal))
            {
                WriteCalls++;
                if (WriteFailure is not null) throw WriteFailure;
                return Task.FromResult(Writes.Count > 0 ? Writes.Dequeue() : Result(0, "OK"));
            }
            ReadCalls++;
            if (ReadFailure is not null) throw ReadFailure;
            return Task.FromResult(Read);
        }

        internal void UnknownRead(string kind)
        {
            Read = kind switch
            {
                "failed" => Result(1, "", "IIS read failed"),
                "empty" => Result(0, ""),
                "unexpected" => Result(0, "YESTERDAY"),
                "conflicting" => Result(0, "YES\r\nNO"),
                _ => Result(0, "NO")
            };
            if (kind == "throws") ReadFailure = new IOException("IIS read interrupted");
        }

        internal async Task<ApiResponse<SslWorkspaceDto>> Set(bool enabled, CancellationToken? token = null)
        {
            var scopes = Proxy<ICustomerScopeService>((method, _) => method.Name == "GetSiteOwnerUserIdAsync"
                ? Task.FromResult<Guid?>(null) : throw new NotSupportedException(method.Name));
            var services = new ServiceCollection().AddSingleton(db)
                .AddSingleton<IIisSiteService>(sites).AddSingleton<ICustomerScopeService>(scopes)
                .AddSingleton<IConcurrencyLock>(new ConcurrencyLock()).BuildServiceProvider();
            providers.Add(services);
            var controller = new SslController(Service, scopes, null!, Area.Denetim)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
                {
                    RequestServices = services,
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, administrator.ToString()),
                        new Claim(ClaimTypes.Role, "Administrator"),
                        new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp("redirect-fixture-hash"))], "test"))
                } }
            };
            var result = await controller.SetHttpsRedirectAsync(new() { SiteName = SiteName, Enabled = enabled }, token ?? Token);
            return Assert.IsType<ApiResponse<SslWorkspaceDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        }

        public void Dispose()
        {
            foreach (var provider in providers) provider.Dispose();
            db.Dispose(); connection.Dispose(); Area.Dispose();
        }
    }

    private static T Proxy<T>(Func<MethodInfo, object?[], object?> invoke) where T : class
    {
        var value = DispatchProxy.Create<T, BoundaryProxy>();
        ((BoundaryProxy)(object)value).Call = invoke;
        return value;
    }

    public class BoundaryProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[], object?> Call = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args ?? []);
    }
}
