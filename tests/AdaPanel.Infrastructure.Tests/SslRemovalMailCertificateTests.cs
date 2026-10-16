using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class SslRemovalLifecycleTests
{
    [Theory]
    [InlineData("{\"source\":\"windows-store\",\"thumbprint\":\"FIXTURE-CERTIFICATE\"}", true)]
    [InlineData("{\"source\":\"windows-store\",\"thumbprint\":\"another-certificate\"}", false)]
    [InlineData("{\"source\":\"letsencrypt\",\"thumbprint\":\"fixture-certificate\"}", false)]
    [InlineData("{\"source\":\"windows-store\"}", true)]
    [InlineData("{\"source\":\"unknown-provider\"}", true)]
    [InlineData("{broken", true)]
    [InlineData("null", true)]
    [InlineData("{\"source\":\"letsencrypt\",\"Source\":\"windows-store\",\"thumbprint\":\"fixture-certificate\"}", true)]
    [InlineData("{\"source\":\"windows-store\",\"thumbprint\":\"other\",\"thumbprint\":\"fixture-certificate\"}", true)]
    public async Task WebRemovalKeepsStoreCertificatesNeededByMailOrWithUnknownUsage(string state, bool protect)
    {
        using var f = await Fixture.Create("");
        var path = f.Area.DosyaDeposu.ResolvePath(MailCertificateStoreReference.StatePath);
        await File.WriteAllTextAsync(path, state, f.Token);
        var result = await f.Remove(removeFromStore: true);
        Assert.True(result.BindingRemoved);
        Assert.Equal(protect ? "kept_in_use" : "removed", result.CertificateAction);
        Assert.Equal(protect ? 0 : 1, f.CertificateCommands);
        Assert.Equal(state, await File.ReadAllTextAsync(path, f.Token));
        Assert.Equal(0, MailCertificateLifecycle.ReferencesForPath(path));
    }

    [Fact]
    public async Task LockedMailReferenceCannotBeAssumedUnused()
    {
        using var f = await Fixture.Create("");
        var path = f.Area.DosyaDeposu.ResolvePath(MailCertificateStoreReference.StatePath);
        await File.WriteAllTextAsync(path, "{}", f.Token);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await f.Remove(removeFromStore: true);
        Assert.Equal("kept_in_use", result.CertificateAction);
        Assert.Contains("unknown", result.Detail);
        Assert.Equal(0, f.CertificateCommands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreDeletionWaitsForMailPublicationWithoutHoldingTheIisGate(bool cancel)
    {
        using var f = await Fixture.Create("");
        var path = f.Area.DosyaDeposu.ResolvePath(MailCertificateStoreReference.StatePath);
        using var held = await MailCertificateLifecycle.EnterAsync(path, f.Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        var action = f.Remove(cancellation.Token, removeFromStore: true);
        try
        {
            while (MailCertificateLifecycle.ReferencesForPath(path) < 2)
            {
                if (action.IsCompleted) await action;
                await Task.Delay(10, f.Token);
            }
            Assert.False(f.Gates.Held);
            Assert.Equal(0, f.CertificateCommands);
            if (cancel) cancellation.Cancel();
            else await f.Area.DosyaDeposu.WriteAsync(MailCertificateStoreReference.StatePath,
                new { source = "windows-store", thumbprint = "fixture-certificate" }, f.Token);
        }
        finally { held.Dispose(); }
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        else Assert.Equal("kept_in_use", (await action).CertificateAction);
        Assert.Equal(0, f.CertificateCommands);
        Assert.Equal(0, MailCertificateLifecycle.ReferencesForPath(path));
    }
}
