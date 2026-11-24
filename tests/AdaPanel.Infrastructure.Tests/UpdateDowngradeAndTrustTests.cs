using AdaPanel.Infrastructure.SelfUpdate;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>G17: surum dusurme korumasi (saf karar) + bugunku 0.8.155 sunucusunun yeni paketi kabul etmesi.</summary>
public sealed class UpdateDowngradeAndTrustTests
{
    [Theory]
    [InlineData("0.8.155", "0.8.156")]
    [InlineData("0.8.155", "0.9.0")]
    [InlineData("0.8.155+abc123", "v0.8.156")]
    [InlineData("0.8.155", "0.8.156-preview")]
    [InlineData("0.8.9", "0.8.10")]
    public void NewerTargetIsAccepted(string installed, string target)
        => Assert.Null(UpdatePackageTrust.SurumDusurmeKusuru(installed, target));

    [Theory]
    [InlineData("0.8.155", "0.8.155")]
    [InlineData("0.8.155", "0.8.154")]
    [InlineData("0.8.155", "0.7.999")]
    [InlineData("0.8.155", "")]
    [InlineData("0.8.155", "garbage")]
    [InlineData("garbage", "0.8.156")]
    public void EqualOlderOrUnparseableTargetIsRejected(string installed, string target)
        => Assert.NotNull(UpdatePackageTrust.SurumDusurmeKusuru(installed, target));

    [Fact]
    public void CurrentlyDeployedUnsignedChainStillAcceptsNextRelease()
    {
        // Canli 0.8.155 sunucusu: imza zorunlu degil, paket imzasiz, kimlik dogru, adres izinli.
        Assert.Null(UpdatePackageTrust.SurumDusurmeKusuru("0.8.155", "0.8.156"));
        Assert.Null(UpdatePackageTrust.ImzaKusuru("NotSigned", "", imzaZorunlu: false));
        Assert.Null(UpdatePackageTrust.PaketKimligiKusuru("AdaPanel", "AdaPanel"));
        Assert.True(UpdatePackageTrust.DepoIzinli("aliorala/adapanel"));
        Assert.True(UpdatePackageTrust.IndirmeAdresiIzinli(
            "https://github.com/aliorala/adapanel/releases/download/v0.8.156/AdaPanelSetup.exe", "aliorala/adapanel"));
        // Imzali ama bozuk paket yine reddedilir.
        Assert.NotNull(UpdatePackageTrust.ImzaKusuru("HashMismatch", "CN=x", imzaZorunlu: false));
    }
}
