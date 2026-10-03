using AdaPanel.Api.Controllers;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Automation;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Ssl;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class QueuedBackupActorLifecycleTests
{
    public static IEnumerable<object[]> ManualSslRevocations() =>
        from wait in new[] { "global", "ssl", "mail" }
        from change in new[] { "disabled", "deleted", "role", "password" }
        select new object[] { wait, change };

    [Theory]
    [MemberData(nameof(ManualSslRevocations))]
    public async Task ManualSslRechecksAuthorityAtEveryExternalMutation(string wait, string change)
    {
        using var f = new Fixture();
        await f.PrepareSsl(wait);
        await using var held = wait == "global" ? await f.SslGate.HoldAsync(ConcurrencyKeys.Automation, f.Token) : null;
        var action = Capture(() => f.InvokeSsl());
        await f.WaitSsl(action, wait);
        await f.Change(change, f.Admin);
        var state = await f.SslStateBytes();
        if (held is not null) await held.DisposeAsync();
        f.SslRelease.TrySetResult();
        AssertAuthorization(await action.WaitAsync(f.Token));
        Assert.Equal(wait == "mail" ? 1 : 0, f.SslCommands);
        Assert.Equal(0, f.SslMailCommands);
        Assert.Equal(state, await f.SslStateBytes());
    }

    [Theory]
    [InlineData("global")]
    [InlineData("ssl")]
    [InlineData("mail")]
    public async Task ManualSslCancellationDoesNotPublishOrStartTheNextMutation(string wait)
    {
        using var f = new Fixture();
        await f.PrepareSsl(wait);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        await using var held = wait == "global" ? await f.SslGate.HoldAsync(ConcurrencyKeys.Automation, f.Token) : null;
        var action = Capture(() => f.InvokeSsl(cancellation.Token));
        await f.WaitSsl(action, wait);
        var state = await f.SslStateBytes();
        cancellation.Cancel();
        Assert.IsAssignableFrom<OperationCanceledException>(await action.WaitAsync(f.Token));
        Assert.Equal(wait == "mail" ? 1 : 0, f.SslCommands);
        Assert.Equal(0, f.SslMailCommands);
        Assert.Equal(state, await f.SslStateBytes());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualSslPreservesSuccessfulManualAndTrustedRenewal(bool trusted)
    {
        using var f = new Fixture();
        await f.PrepareSsl("");
        if (trusted) await f.SslAutomation.RunSslRenewNowAsync(f.Token);
        else await f.InvokeSsl();
        Assert.Equal(1, f.SslCommands);
        Assert.Equal(1, f.SslMailCommands);
        Assert.Equal("completed", (await f.SslAutomation.GetStatusAsync(f.Token)).LastSslStatus);
    }

    [Fact]
    public async Task ManualSslDoesNotConvertMailProviderCancellationToSuccess()
    {
        using var f = new Fixture();
        await f.PrepareSsl("mail-canceled");
        var state = await f.SslStateBytes();
        Assert.IsAssignableFrom<OperationCanceledException>(await Capture(() => f.InvokeSsl()));
        Assert.Equal(state, await f.SslStateBytes());
    }

    [Fact]
    public async Task ManualSslKeepsTheMeasuredProviderFailureContract()
    {
        using var f = new Fixture();
        await f.PrepareSsl("ssl-failed");
        await f.InvokeSsl();
        var state = await f.SslAutomation.GetStatusAsync(f.Token);
        Assert.Equal("failed", state.LastSslStatus);
        Assert.Contains("fixture-provider-failed", state.LastSslDetails);
    }

    private sealed partial class Fixture
    {
        internal AutomationService SslAutomation = null!;
        internal readonly ObservedManualGate SslGate = new();
        internal readonly TaskCompletionSource SslReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource SslRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int SslCommands;
        internal int SslMailCommands;
        internal WinAcmeSslService SslNativeService = null!;
        internal CommandResult? SslNativeResult;
        private string sslWait = "";

        internal async Task PrepareSsl(string wait, ISslService? nativeSsl = null)
        {
            sslWait = wait;
            var ssl = nativeSsl ?? Fake<ISslService>((method, args) => method.Name == "RenewAllAsync"
                ? RunSslProvider(args) : throw new NotSupportedException(method.Name));
            var mail = Fake<IMailService>((method, args) => method.Name == "SyncCertificateAsync"
                ? RunSslMailProvider(args) : throw new NotSupportedException(method.Name));
            SslAutomation = new AutomationService(Area.DosyaDeposu, Service, ssl, mail,
                null!, null!, null!, null!, null!, null!, new SahteKoruyucu(),
                null!, null!, null!, null!, null!, SslGate, null!, null!, Jobs, SslOutcomeLog);
            await Area.DosyaDeposu.WriteAsync("config/automation.json", new
            {
                autoBackupEnabled = false, autoSslRenewEnabled = true,
                sslRenewIntervalHours = 24, lastSslStatus = "never", lastSslDetails = "untouched"
            }, Token);
        }

        private async Task ApproveSslProvider(string boundary, object?[] args)
        {
            var token = args.OfType<CancellationToken>().Single();
            if (sslWait == boundary)
            {
                SslReached.TrySetResult();
                await SslRelease.Task.WaitAsync(token);
            }
            token.ThrowIfCancellationRequested();
            foreach (var approve in args.OfType<Func<CancellationToken, Task>>()) await approve(token);
        }

        private async Task<RenewAllResultDto> RunSslProvider(object?[] args)
        {
            await ApproveSslProvider("ssl", args);
            if (sslWait == "ssl-failed") throw new InvalidOperationException("fixture-provider-failed");
            if (SslRenewalFailure is not null) throw SslRenewalFailure;
            SslCommands++;
            return SslOutcome ?? new RenewAllResultDto { Measured = true, BindingsMeasured = true, RenewedCount = 2 };
        }

        private async Task<MailCertificateDto?> RunSslMailProvider(object?[] args)
        {
            await ApproveSslProvider("mail", args);
            if (sslWait == "mail-canceled") throw new OperationCanceledException("mail provider canceled");
            if (SslMailFailure is not null) throw SslMailFailure;
            SslMailCommands++;
            return SslMailResult;
        }

        internal async Task InvokeSsl(CancellationToken? token = null)
        {
            var controller = Controller(Admin);
            var automation = ActivatorUtilities.CreateInstance<AutomationController>(
                controller.HttpContext.RequestServices, SslAutomation, Area.Denetim);
            automation.ControllerContext = controller.ControllerContext;
            await automation.RunSslAsync(token ?? Token);
        }

        internal async Task WaitSsl(Task action, string wait)
        {
            var reached = wait == "global" ? SslGate.Attempted.Task : SslReached.Task;
            await Task.WhenAny(reached, action).WaitAsync(Token);
            Assert.True(reached.IsCompleted, "SSL request failed before reaching its controlled wait.");
            Assert.False(action.IsCompleted);
        }

        internal Task<byte[]> SslStateBytes() =>
            File.ReadAllBytesAsync(Area.DosyaDeposu.ResolvePath("config/automation.json"), Token);

        internal async Task PrepareNativeSsl(string change, bool installed)
        {
            var directory = Area.DosyaDeposu.EnsureDirectory("fake-winacme");
            await File.WriteAllTextAsync(Path.Combine(directory, "wacs.exe"), "Never executed", Token);
            if (installed)
            {
                var seed = Context();
                seed.InstalledComponents.Add(new InstalledComponent
                {
                    ComponentId = "win-acme", InstallDir = directory, Status = ComponentInstallStatus.Installed
                });
                await seed.SaveChangesAsync(Token);
            }
            var interception = new SslLookupBarrier(SslReached, SslRelease);
            var database = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>()
                .UseSqlite(keeper.ConnectionString).AddInterceptors(interception).Options);
            contexts.Add(database);
            var service = new WinAcmeSslService(database, null!, null!, Area.DosyaDeposu, null!,
                NullLogger<WinAcmeSslService>.Instance,
                _ => Task.FromResult<IReadOnlyCollection<IisHttpsBinding>?>([]))
            {
                RenewalCommand = (_, _, _) =>
                {
                    SslCommands++;
                    return Task.FromResult(SslNativeResult ?? new CommandResult { ExitCode = 0, StandardOutput = "Renewal succeeded" });
                }
            };
            SslNativeService = service;
            await Area.DosyaDeposu.WriteAsync("config/ssl.json", Array.Empty<SslCertificateDto>(), Token);
            await PrepareSsl(change, service);
        }
    }

    [Theory]
    [InlineData("disabled", true)]
    [InlineData("deleted", true)]
    [InlineData("role", true)]
    [InlineData("password", true)]
    [InlineData("disabled", false)]
    public async Task ManualSslNativeLookupCannotOutliveTheActor(string change, bool installed)
    {
        using var f = new Fixture();
        await f.PrepareNativeSsl(change, installed);
        var status = await f.SslStateBytes();
        var certificates = await File.ReadAllBytesAsync(f.Area.DosyaDeposu.ResolvePath("config/ssl.json"), f.Token);
        var action = Capture(() => f.InvokeSsl());
        await f.WaitSsl(action, "native");
        await f.Change(change, f.Admin);
        f.SslRelease.TrySetResult();
        AssertAuthorization(await action.WaitAsync(f.Token));
        Assert.Equal(0, f.SslCommands);
        Assert.Equal(0, f.SslMailCommands);
        Assert.Equal(status, await f.SslStateBytes());
        Assert.Equal(certificates, await File.ReadAllBytesAsync(f.Area.DosyaDeposu.ResolvePath("config/ssl.json"), f.Token));
    }

    [Fact]
    public async Task ManualSslNativeLookupStillRunsForAnUnchangedActor()
    {
        using var f = new Fixture();
        await f.PrepareNativeSsl("", true);
        var action = Capture(() => f.InvokeSsl());
        await f.WaitSsl(action, "native");
        f.SslRelease.TrySetResult();
        Assert.Null(await action.WaitAsync(f.Token));
        Assert.Equal(1, f.SslCommands);
        Assert.Equal(1, f.SslMailCommands);
        Assert.Equal("completed", (await f.SslAutomation.GetStatusAsync(f.Token)).LastSslStatus);
    }

    private sealed class SslLookupBarrier(TaskCompletionSource reached, TaskCompletionSource release) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("installed_components", StringComparison.Ordinal))
            {
                reached.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
