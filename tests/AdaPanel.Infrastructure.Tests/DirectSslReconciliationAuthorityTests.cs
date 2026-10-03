using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Infrastructure.Ssl;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class QueuedBackupActorLifecycleTests
{
    [Theory]
    [InlineData("disabled", false)]
    [InlineData("deleted", false)]
    [InlineData("role", false)]
    [InlineData("password", false)]
    [InlineData("source", false)]
    [InlineData("section", true)]
    [InlineData("parent_disabled", true)]
    [InlineData("parent_deleted", true)]
    [InlineData("reparented", true)]
    public async Task DirectSslReconciliationCannotWriteAfterTheActorLosesAuthority(string change, bool child)
    {
        using var f = new Fixture();
        await f.PrepareRemovalReconciliation();
        var actor = child ? f.Child : f.Customer;
        var action = Capture(async () => await f.RemovalController(actor).RemoveCertificateAsync(f.RemovalRequest, f.Token));
        await f.WaitDirectRemoval(action);
        var afterBindingRemoval = await f.RemovalState();
        await f.Change(change, actor);
        f.RemovalRelease.TrySetResult();
        AssertAuthorization(await action.WaitAsync(f.Token));
        Assert.Equal(1, f.RemovalCommands);
        Assert.Equal(1, f.RemovalRedirectWrites);
        Assert.Equal(afterBindingRemoval, await f.RemovalState());
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    [Fact]
    public async Task DirectSslReconciliationHonorsCancellationEvenWhenTheProbeReturnsNormally()
    {
        using var f = new Fixture();
        await f.PrepareRemovalReconciliation();
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var action = Capture(async () => await f.RemovalController(f.Customer).RemoveCertificateAsync(f.RemovalRequest, canceled.Token));
        await f.WaitDirectRemoval(action);
        var afterBindingRemoval = await f.RemovalState();
        canceled.Cancel();
        f.RemovalRelease.TrySetResult();
        Assert.IsAssignableFrom<OperationCanceledException>(await action.WaitAsync(f.Token));
        Assert.Equal(afterBindingRemoval, await f.RemovalState());
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectSslReconciliationStillPublishesAMeasuredRemainingCertificate(bool found)
    {
        using var f = new Fixture();
        await f.PrepareRemovalReconciliation(found);
        var action = f.RemovalController(f.Customer).RemoveCertificateAsync(f.RemovalRequest, f.Token);
        await f.WaitDirectRemoval(action);
        f.RemovalRelease.TrySetResult();
        await action.WaitAsync(f.Token);
        var record = Assert.Single(await f.RemovalRecords());
        Assert.Equal(found, record.IsPresentInStore);
        Assert.NotNull(record.LastVerifiedUtc);
        Assert.Equal(found ? "remaining-renewed" : "remaining-original", record.Thumbprint);
        Assert.Single(f.Area.Denetim.Kayitlar, item => item.Action == "ssl.binding_removed");
    }

    [Fact]
    public async Task DirectSslReconciliationPreservesTheTrustedServicePath()
    {
        using var f = new Fixture();
        await f.PrepareRemovalReconciliation();
        var action = f.RemovalService.RemoveCertificateAsync(f.RemovalRequest, f.Token);
        await f.WaitDirectRemoval(action);
        f.RemovalRelease.TrySetResult();
        Assert.True((await action.WaitAsync(f.Token)).BindingRemoved);
        Assert.False(Assert.Single(await f.RemovalRecords()).IsPresentInStore);
        Assert.Empty(f.Area.Denetim.Kayitlar);
    }

    [Theory]
    [InlineData("thumbprint", false)]
    [InlineData("expiry", false)]
    [InlineData("source", false)]
    [InlineData("thumbprint", true)]
    [InlineData("expiry", true)]
    [InlineData("source", true)]
    public async Task DirectSslReconciliationCannotApplyAnOldProbeToAReplacementRecord(string change, bool found)
    {
        using var f = new Fixture();
        await f.PrepareRemovalReconciliation(found);
        var action = f.RemovalController(f.Customer).RemoveCertificateAsync(f.RemovalRequest, f.Token);
        await f.WaitDirectRemoval(action);
        await f.Area.DosyaDeposu.UpdateAsync("config/ssl.json", Array.Empty<SslCertificateDto>(), records =>
        {
            var record = Assert.Single(records);
            if (change == "thumbprint") record.Thumbprint = "concurrently-imported";
            if (change == "expiry") record.NotAfterUtc = new(2028, 2, 1, 0, 0, 0, DateTimeKind.Utc);
            if (change == "source") record.Source = "imported";
            return records;
        }, f.Token);
        var replacement = await f.RemovalState();
        f.RemovalRelease.TrySetResult();
        await action.WaitAsync(f.Token);
        Assert.Equal(replacement, await f.RemovalState());
        Assert.Single(f.Area.Denetim.Kayitlar);
    }

    [Fact]
    public async Task DirectSslReconciliationAllowsAConcurrentVerificationTimestamp()
    {
        using var f = new Fixture();
        await f.PrepareRemovalReconciliation();
        var action = f.RemovalController(f.Customer).RemoveCertificateAsync(f.RemovalRequest, f.Token);
        await f.WaitDirectRemoval(action);
        var earlier = DateTime.UtcNow.AddMinutes(-1);
        await f.Area.DosyaDeposu.UpdateAsync("config/ssl.json", Array.Empty<SslCertificateDto>(), records =>
        {
            Assert.Single(records).LastVerifiedUtc = earlier;
            return records;
        }, f.Token);
        f.RemovalRelease.TrySetResult();
        await action.WaitAsync(f.Token);
        var verified = Assert.Single(await f.RemovalRecords());
        Assert.False(verified.IsPresentInStore);
        Assert.True(verified.LastVerifiedUtc > earlier);
    }

    private sealed partial class Fixture
    {
        internal async Task PrepareRemovalReconciliation(bool found = false)
        {
            await PrepareDirectRemoval("reconcile");
            await Area.DosyaDeposu.UpdateAsync("config/ssl.json", Array.Empty<SslCertificateDto>(),
                records => [.. records, new SslCertificateDto
                {
                    SiteName = Site, HostName = "remaining." + Site, Port = 8443, Thumbprint = "remaining-original",
                    Source = "manual", IsPresentInStore = true, NotAfterUtc = new(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc)
                }], Token);
            RemovalProbe = async (record, _) =>
            {
                Assert.Equal("remaining." + Site, record.HostName);
                Assert.Equal(1, RemovalCommands);
                RemovalReached.TrySetResult();
                // The external reader may finish normally after caller cancellation.
                await RemovalRelease.Task.WaitAsync(Token);
                return found ? SslStoreProbe.Found("remaining-renewed", new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), "manual")
                    : SslStoreProbe.NotFound();
            };
        }

        internal Task<SslCertificateDto[]> RemovalRecords() =>
            Area.DosyaDeposu.ReadAsync("config/ssl.json", Array.Empty<SslCertificateDto>(), Token);
    }
}
