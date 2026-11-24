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

    [Theory]
    [InlineData("0.8.155.0", "0.8.155")]
    [InlineData("0.8.155", "0.8.155.0")]
    [InlineData("v0.8.155", "0.8.155")]
    [InlineData("0.8.156", "0.8.155")]
    public void SameOrOlderIsRejectedEvenWithTrailingZeroOrVPrefix(string installed, string target)
        => Assert.NotNull(UpdatePackageTrust.SurumDusurmeKusuru(installed, target));

    [Theory]
    [InlineData("0.8.156-preview", "0.8.156")]
    [InlineData("0.8.155.0", "0.8.156")]
    [InlineData("0.8.155", "v0.8.156.0")]
    public void StableOfSameCoreOverPreReleaseAndNewerCoresAreAccepted(string installed, string target)
        => Assert.Null(UpdatePackageTrust.SurumDusurmeKusuru(installed, target));

    [Theory]
    [InlineData("0.8.156", "0.8.156-preview")]
    [InlineData("0.8.156-a", "0.8.156-b")]
    public void StableToPreReleaseOfSameCoreIsRejected(string installed, string target)
        => Assert.NotNull(UpdatePackageTrust.SurumDusurmeKusuru(installed, target));

    [Theory]
    [InlineData("v0.2.27", "v0.2.29", true)]
    [InlineData("0.8.155.0", "0.8.155", false)]
    [InlineData("0.8.156-preview", "0.8.156", true)]
    public void IsNewerVersionSharesTheTrustParser(string current, string latest, bool expected)
        => Assert.Equal(expected, AdaPanel.Infrastructure.SelfUpdate.SelfUpdateService.IsNewerVersion(current, latest));

    [Theory]
    [InlineData("", "0.8.156", true)]
    [InlineData("garbage", "0.8.156", true)]
    [InlineData("0.8.156.0", "0.8.156", true)]
    [InlineData("0.8.155.0", "0.8.156", false)]
    public void PackageOwnVersionOnlyCheckedWhenReadable(string packageVersion, string expected, bool ok)
        => Assert.Equal(ok, UpdatePackageTrust.PaketSurumuKusuru(packageVersion, expected) is null);
}
