using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Ssl;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class QueuedBackupActorLifecycleTests
{
    public static IEnumerable<object[]> WorkspaceActors() =>
        new[] { "disabled", "deleted", "role", "password", "source", "section", "parent_disabled", "parent_deleted", "reparented" }
            .Select(change => new object[] { change, change is "section" or "parent_disabled" or "parent_deleted" or "reparented" });

    [Theory, MemberData(nameof(WorkspaceActors))]
    public async Task DirectSslWorkspaceCannotPublishAfterTheActorLosesAuthority(string change, bool child)
    {
        using var f = new Fixture(); await f.PrepareWorkspaceProbe();
        var before = await f.RemovalState(); var actor = child ? f.Child : f.Customer;
        var action = Capture(async () => await f.RemovalController(actor).GetWorkspaceAsync(Fixture.Site, f.Token));
        await f.WaitDirectRemoval(action); await f.Change(change, actor); f.RemovalRelease.TrySetResult();
        AssertAuthorization(await action.WaitAsync(f.Token));
        Assert.Equal(before, await f.RemovalState());
        Assert.Equal(0, f.RemovalCommands); Assert.Equal(0, f.RemovalRedirectWrites);
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("path")]
    [InlineData("same-snapshot")]
    public async Task DirectSslWorkspaceCannotPublishForASiteReplacedDuringItsProbe(string change)
    {
        using var f = new Fixture(); await f.PrepareWorkspaceProbe();
        var before = await f.RemovalState();
        var action = Capture(async () => await f.RemovalController(f.Customer).GetWorkspaceAsync(Fixture.Site, f.Token));
        await f.WaitDirectRemoval(action); await f.ReplaceRemovalSite(change); f.RemovalRelease.TrySetResult();
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await action.WaitAsync(f.Token)).MessageKey);
        Assert.Equal(before, await f.RemovalState());
    }

    [Theory]
    [InlineData("remove", "id")]
    [InlineData("remove", "path")]
    [InlineData("remove", "same-snapshot")]
    [InlineData("workspace", "id")]
    [InlineData("workspace", "path")]
    [InlineData("workspace", "same-snapshot")]
    public async Task DirectSslOperationsKeepTheSiteObservedBeforeTheirFirstScopeWait(string operation, string change)
    {
        using var f = new Fixture(); await f.PrepareDirectRemoval("scope");
        f.InterceptRemovalScope(async (id, token) =>
        {
            var assigned = await f.Scopes.GetAssignedSitesAsync(id, token);
            f.RemovalReached.TrySetResult(); await f.RemovalRelease.Task.WaitAsync(token); return assigned;
        });
        var before = await f.RemovalState();
        var action = Capture(async () =>
        {
            var controller = f.RemovalController(f.Customer);
            if (operation == "remove") await controller.RemoveCertificateAsync(f.RemovalRequest, f.Token);
            else await controller.GetWorkspaceAsync(Fixture.Site, f.Token);
        });
        await f.WaitDirectRemoval(action); await f.ReplaceRemovalSite(change); f.RemovalRelease.TrySetResult();
        Assert.Equal("sites.target_changed", Assert.IsType<AppException>(await action.WaitAsync(f.Token)).MessageKey);
        Assert.Equal(before, await f.RemovalState());
        Assert.Equal(0, f.RemovalCommands); Assert.Equal(0, f.RemovalRedirectWrites);
    }

    [Fact]
    public async Task DirectSslWorkspaceHonorsCancellationAfterAnIgnoringProbeReturns()
    {
        using var f = new Fixture(); await f.PrepareWorkspaceProbe();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var before = await f.RemovalState();
        var action = Capture(async () => await f.RemovalController(f.Customer).GetWorkspaceAsync(Fixture.Site, cancellation.Token));
        await f.WaitDirectRemoval(action); cancellation.Cancel(); f.RemovalRelease.TrySetResult();
        Assert.IsAssignableFrom<OperationCanceledException>(await action.WaitAsync(f.Token));
        Assert.Equal(before, await f.RemovalState());
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("customer")]
    [InlineData("child")]
    [InlineData("trusted")]
    public async Task DirectSslWorkspaceStillRefreshesTheAuthorizedCertificate(string role)
    {
        using var f = new Fixture(); await f.PrepareWorkspaceProbe();
        var action = role == "trusted"
            ? (Task)f.RemovalService.GetSiteWorkspaceAsync(Fixture.Site, f.Token)
            : f.RemovalController(role == "admin" ? f.Admin : role == "child" ? f.Child : f.Customer).GetWorkspaceAsync(Fixture.Site, f.Token);
        await f.WaitDirectRemoval(action); f.RemovalRelease.TrySetResult(); await action.WaitAsync(f.Token);
        var record = Assert.Single(await f.RemovalRecords());
        Assert.False(record.IsPresentInStore); Assert.NotNull(record.LastVerifiedUtc);
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    [Theory, MemberData(nameof(WorkspaceActors))]
    public async Task DirectSslRemovalCannotDeleteMetadataAfterAuthorityChangesInTheJsonQueue(string change, bool child)
    {
        using var f = new Fixture(); await f.PrepareMetadataApproval();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = f.HoldSslJson(entered, release); await entered.Task.WaitAsync(f.Token);
        var before = await f.RemovalState(); var actor = child ? f.Child : f.Customer;
        var action = Capture(async () => await f.RemovalController(actor).RemoveCertificateAsync(f.RemovalRequest, f.Token));
        try { await f.WaitDirectRemoval(action); await f.Change(change, actor); }
        finally { release.TrySetResult(); }
        await holder.WaitAsync(f.Token);
        AssertAuthorization(await action.WaitAsync(f.Token));
        Assert.Equal(before, await f.RemovalState()); Assert.Equal(1, f.RemovalCommands);
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectSslRemovalJsonQueuePreservesNormalCompletionAndCancellation(bool cancel)
    {
        using var f = new Fixture(); await f.PrepareMetadataApproval();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = f.HoldSslJson(entered, release); await entered.Task.WaitAsync(f.Token);
        var before = await f.RemovalState();
        var action = Capture(async () => await f.RemovalController(f.Customer).RemoveCertificateAsync(f.RemovalRequest, cancellation.Token));
        try { await f.WaitDirectRemoval(action); if (cancel) cancellation.Cancel(); }
        finally { release.TrySetResult(); }
        await holder.WaitAsync(f.Token); var error = await action.WaitAsync(f.Token);
        if (cancel)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error); Assert.Equal(before, await f.RemovalState());
        }
        else { Assert.Null(error); Assert.Empty(await f.RemovalRecords()); Assert.Single(f.Area.Denetim.Kayitlar); }
    }

    private sealed partial class Fixture
    {
        internal async Task PrepareWorkspaceProbe()
        {
            await PrepareDirectRemoval("workspace-probe");
            RemovalProbe = async (_, _) =>
            {
                RemovalReached.TrySetResult(); await RemovalRelease.Task.WaitAsync(Token); return SslStoreProbe.NotFound();
            };
        }
        internal void InterceptRemovalScope(Func<Guid, CancellationToken, Task<IReadOnlyCollection<string>>> assigned) =>
            RemovalScopeOverride = Fake<ICustomerScopeService>((method, args) => method.Name == "GetAssignedSitesAsync"
                ? assigned((Guid)args[0]!, (CancellationToken)args[1]!) : method.Invoke(Scopes, args));
        internal async Task PrepareMetadataApproval()
        {
            await PrepareDirectRemoval("metadata");
            InterceptRemovalScope(async (id, token) =>
            {
                var assigned = await Scopes.GetAssignedSitesAsync(id, token);
                if (RemovalCommands > 0) RemovalReached.TrySetResult();
                return assigned;
            });
        }
        internal Task<SslCertificateDto[]> HoldSslJson(TaskCompletionSource entered, TaskCompletionSource release) =>
            Area.DosyaDeposu.UpdateAsync("config/ssl.json", Array.Empty<SslCertificateDto>(), records => records,
                async _ => { entered.TrySetResult(); await release.Task.WaitAsync(Token); }, Token);
        internal async Task ReplaceRemovalSite(string change)
        {
            if (change == "same-snapshot")
                await SiteMutationGuard.RunMutationAsync(RemovalGates, Site, SiteMutationGuard.MutationKind.IdentityChange,
                    () => Task.FromResult(true), Token);
            else RemovalSite = new SiteDto
            {
                Id = change == "id" ? 99 : 18, Name = Site,
                PhysicalPath = change == "path" ? Target : Area.SiteKoku,
                ApplicationRootPath = change == "path" ? Target : Area.SiteKoku, ApplicationPoolName = "actor-removal-pool"
            };
        }
    }
}
