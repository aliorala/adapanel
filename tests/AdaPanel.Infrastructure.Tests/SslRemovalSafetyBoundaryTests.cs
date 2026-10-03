using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Infrastructure.Ssl;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class SslRemovalLifecycleTests
{
    [Fact]
    public async Task LosingTheOtherHttpsBindingCannotLeaveTheSiteRedirectingToNothing()
    {
        using var f = await Fixture.Create("before-binding-removal");
        var target = Assert.Single(f.Bindings);
        f.Bindings = [target, new IisHttpsBinding
        {
            SiteName = f.Name, BindingInformation = "*:443:other." + f.Name,
            Thumbprint = "other-certificate", CertificateReadable = true
        }];
        f.Redirect = true;
        var action = Capture(() => f.Remove());
        try
        {
            await f.Wait(action);
            f.Bindings = [target];
        }
        finally { f.Release.TrySetResult(); }

        var error = await action.WaitAsync(f.Token);
        Assert.False(f.Redirect && !f.Bindings.Any(binding => binding.HostName == f.Name),
            "The last HTTPS listener was removed while HTTP still redirects to HTTPS.");
        if (error is not null)
            Assert.Equal("ssl.binding_changed", Assert.IsType<AppException>(error).MessageKey);
        else
        {
            Assert.False(f.Redirect);
            Assert.Equal(1, f.RedirectWrites);
            Assert.Equal(1, f.BindingDeletes);
        }
    }

    [Theory]
    [InlineData("other-site")]
    [InlineData("rebound-target")]
    [InlineData("unreadable")]
    public async Task ANewCertificateUserDuringTheFinalApprovalWaitPreventsStoreDeletion(string change)
    {
        using var f = await Fixture.Create("");
        var target = Assert.Single(f.Bindings);
        var action = RemoveAtStoreApproval(f);
        try
        {
            await f.Wait(action);
            Assert.Equal(1, f.BindingDeletes);
            Assert.Empty(await f.Records());
            f.Bindings = [change == "rebound-target" ? target : new IisHttpsBinding
            {
                SiteName = "neighbor." + f.Name,
                BindingInformation = "*:443:neighbor." + f.Name,
                Thumbprint = change == "unreadable" ? "" : target.Thumbprint,
                CertificateReadable = change != "unreadable"
            }];
        }
        finally { f.Release.TrySetResult(); }

        var result = await action.WaitAsync(f.Token);
        Assert.Equal(0, f.CertificateDeletes);
        Assert.Equal("kept_in_use", result.CertificateAction);
        Assert.NotEmpty(result.CertificateUsedBy);
        Assert.Single(f.Bindings);
    }

    [Fact]
    public async Task ANewUserOfADifferentCertificateDoesNotBlockTheRequestedStoreDeletion()
    {
        using var f = await Fixture.Create("");
        var action = RemoveAtStoreApproval(f);
        try
        {
            await f.Wait(action);
            f.Bindings = [new IisHttpsBinding
            {
                SiteName = "neighbor." + f.Name,
                BindingInformation = "*:443:neighbor." + f.Name,
                Thumbprint = "different-certificate", CertificateReadable = true
            }];
        }
        finally { f.Release.TrySetResult(); }

        var result = await action.WaitAsync(f.Token);
        Assert.Equal(1, f.CertificateDeletes);
        Assert.Equal("removed", result.CertificateAction);
        Assert.Empty(result.CertificateUsedBy);
        Assert.Single(f.Bindings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("My")]
    [InlineData("{}")]
    [InlineData("{\"status\":\"removed\",\"stores\":[\"My\"],\"usedBy\":[]}")]
    [InlineData("{\"status\":\"removed\",\"bindingsMeasured\":true,\"storesMeasured\":true,\"stores\":[],\"usedBy\":[]}")]
    public async Task AnUnmeasuredNativeStoreOutcomeCannotBeReportedAsCertificateRemoval(string output)
    {
        using var f = await Fixture.Create("");
        f.CertificateOutput = output;
        var result = await f.Remove(removeFromStore: true);
        Assert.True(result.BindingRemoved);
        Assert.Equal(1, f.CertificateCommands);
        Assert.Equal("remove_failed", result.CertificateAction);
        Assert.NotEmpty(result.Detail);
    }

    [Fact]
    public async Task NativeMeasurementOfANewCertificateUserOverridesTheEarlierManagedInventory()
    {
        using var f = await Fixture.Create("");
        var target = Assert.Single(f.Bindings);
        var neighbor = new IisHttpsBinding
        {
            SiteName = "native-neighbor." + f.Name,
            BindingInformation = "*:443:native-neighbor." + f.Name,
            Thumbprint = target.Thumbprint, CertificateReadable = true
        };
        f.BeforeCertificateCommand = () => f.Bindings = [neighbor];
        f.CertificateCommandDeletes = false;
        var user = $"{neighbor.SiteName} ({neighbor.BindingInformation})";
        f.CertificateOutput = System.Text.Json.JsonSerializer.Serialize(new
        {
            status = "in_use", bindingsMeasured = true, storesMeasured = false,
            stores = Array.Empty<string>(), usedBy = new[] { user }
        });

        var result = await f.Remove(removeFromStore: true);
        Assert.Equal(1, f.CertificateCommands);
        Assert.Equal(0, f.CertificateDeletes);
        Assert.Equal("kept_in_use", result.CertificateAction);
        Assert.Equal(user, Assert.Single(result.CertificateUsedBy));
        Assert.Same(neighbor, Assert.Single(f.Bindings));
    }

    [Fact]
    public async Task AnAlreadyMissingStoreCertificateIsNotReportedAsRemovedByThisOperation()
    {
        using var f = await Fixture.Create("");
        f.CertificateCommandDeletes = false;
        f.CertificateOutput = """{"status":"not_found","bindingsMeasured":true,"storesMeasured":true,"stores":[],"usedBy":[]}""";
        var result = await f.Remove(removeFromStore: true);
        Assert.Equal("remove_failed", result.CertificateAction);
        Assert.Equal(0, f.CertificateDeletes);
        Assert.NotEmpty(result.Detail);
    }

    [Fact]
    public async Task AStoreCommandCancellationIsPropagatedAndReleasesTheSiteReservation()
    {
        using var f = await Fixture.Create("");
        var canceled = new OperationCanceledException("fixture certificate command canceled");
        f.CertificateFailure = canceled;
        Assert.Same(canceled, await Capture(() => f.Remove(removeFromStore: true)));
        Assert.Equal(0, f.CertificateDeletes);
        await f.NativeMutation();
    }

    private static Task<RemoveSslCertificateResultDto> RemoveAtStoreApproval(Fixture f)
    {
        var paused = false;
        return f.Service.RemoveCertificateAsync(new()
        {
            SiteName = f.Name, HostName = f.Name, Port = 443, RemoveCertificateFromStore = true
        }, async (_, token) =>
        {
            // Observe the real stage through its effects, not a callback invocation count.
            // The target JSON record disappears only after native removal and its readback.
            if (paused || f.BindingDeletes == 0 || (await f.Records()).Length != 0) return;
            paused = true;
            f.Entered.TrySetResult();
            await f.Release.Task.WaitAsync(token);
        }, f.Token);
    }
}
