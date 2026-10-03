using AdaPanel.Api.Controllers;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class QueuedBackupActorLifecycleTests
{
    [Theory]
    [InlineData("disabled", true)]
    [InlineData("deleted", true)]
    [InlineData("role", true)]
    [InlineData("password", true)]
    [InlineData("disabled", false)]
    public async Task DirectSslRenewalRechecksTheActorAfterTheRealExecutableLookup(string change, bool installed)
    {
        using var f = new Fixture();
        await f.PrepareNativeSsl("", installed);
        var controller = f.DirectSslController(f.Admin);
        var before = await File.ReadAllBytesAsync(f.Area.DosyaDeposu.ResolvePath("config/ssl.json"), f.Token);
        var action = Capture(async () => await controller.RenewAllAsync(f.Token));
        await f.WaitSsl(action, "native");
        await f.Change(change, f.Admin);
        f.SslRelease.TrySetResult();
        AssertAuthorization(await action.WaitAsync(f.Token));
        Assert.Equal(0, f.SslCommands);
        Assert.Equal(before, await File.ReadAllBytesAsync(f.Area.DosyaDeposu.ResolvePath("config/ssl.json"), f.Token));
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("role")]
    [InlineData("password")]
    public async Task DirectSslRenewalRejectsAnAlreadyInvalidActorBeforeProviderWork(string change)
    {
        using var f = new Fixture();
        await f.PrepareNativeSsl("", true);
        var controller = f.DirectSslController(f.Admin);
        await f.Change(change, f.Admin);
        f.SslRelease.TrySetResult();
        AssertAuthorization(await Capture(async () => await controller.RenewAllAsync(f.Token)));
        Assert.False(f.SslReached.Task.IsCompleted);
        Assert.Equal(0, f.SslCommands);
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    [Fact]
    public async Task DirectSslRenewalRejectsANonAdministratorAtItsHandlerBoundary()
    {
        using var f = new Fixture();
        await f.PrepareNativeSsl("", true);
        f.SslRelease.TrySetResult();
        AssertAuthorization(await Capture(async () => await f.DirectSslController(f.Customer).RenewAllAsync(f.Token)));
        Assert.Equal(0, f.SslCommands);
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    [Fact]
    public async Task DirectSslRenewalCancellationAtTheNativeLookupHasNoCommandOrAudit()
    {
        using var f = new Fixture();
        await f.PrepareNativeSsl("", true);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var action = Capture(async () => await f.DirectSslController(f.Admin).RenewAllAsync(canceled.Token));
        await f.WaitSsl(action, "native");
        canceled.Cancel();
        Assert.IsAssignableFrom<OperationCanceledException>(await action.WaitAsync(f.Token));
        Assert.Equal(0, f.SslCommands);
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectSslRenewalPreservesMeasuredAndUnavailableProviderResults(bool installed)
    {
        using var f = new Fixture();
        await f.PrepareNativeSsl("", installed);
        var task = f.DirectSslController(f.Admin).RenewAllAsync(f.Token);
        await f.WaitSsl(task, "native");
        f.SslRelease.TrySetResult();
        var response = Assert.IsType<OkObjectResult>((await task.WaitAsync(f.Token)).Result);
        var envelope = Assert.IsType<ApiResponse<RenewAllResultDto>>(response.Value);
        Assert.Equal(installed, envelope.Data!.Measured);
        Assert.Equal(installed ? 1 : 0, f.SslCommands);
        Assert.Single(f.Area.Denetim.Kayitlar, item => item.Action == "ssl.renew_all");
    }

    [Fact]
    public async Task DirectSslRenewalTrustedNativePathDoesNotRequireAnHttpActor()
    {
        using var f = new Fixture();
        await f.PrepareNativeSsl("", true);
        var task = f.SslNativeService.RenewAllAsync(f.Token);
        await f.WaitSsl(task, "native");
        f.SslRelease.TrySetResult();
        Assert.True((await task.WaitAsync(f.Token)).Measured);
        Assert.Equal(1, f.SslCommands);
    }

    private sealed partial class Fixture
    {
        internal SslController DirectSslController(Guid actor) => new(SslNativeService, Scopes,
            Fake<IWebmailService>((method, _) => throw new NotSupportedException(method.Name)), Area.Denetim)
        { ControllerContext = Controller(actor).ControllerContext };
    }
}
