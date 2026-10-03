using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class StalwartProcessObservationTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "adapanel-stalwart");
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("stalwart.exe")]
    public void UnreadableOrRelativeImageIsUnknown(string? path)
    {
        Assert.Null(StalwartProcessProbe.BelongsToInstallation(Root, path));
        Assert.Equal(StalwartProcessState.Unknown, StalwartProcessProbe.Classify(Root, [new(71, false, path)]).State);
    }

    [Fact]
    public void SimilarSiblingInstallationCannotProveStartup()
    {
        var path = Path.Combine(Root + "-other", "stalwart.exe");
        Assert.False(StalwartProcessProbe.BelongsToInstallation(Root, path));
        Assert.Equal(StalwartProcessState.Stopped, StalwartProcessProbe.Classify(Root, [new(71, false, path)]).State);
    }

    [Fact]
    public void ExecutableAndNormalizedNestedDirectoryMatchWithoutCaseSensitivity()
    {
        var path = Path.Combine(Root.ToUpperInvariant(), "bin", "STALWART.EXE");
        Assert.True(StalwartProcessProbe.BelongsToInstallation(Root, path));
        Assert.Equal(StalwartProcessState.Running, StalwartProcessProbe.Classify(Root, [new(71, false, path)]).State);
    }

    [Fact]
    public void ExitedMatchingProcessIsNotRunning() => Assert.Equal(StalwartProcessState.Stopped,
        StalwartProcessProbe.Classify(Root, [new(71, true, Path.Combine(Root, "stalwart.exe"))]).State);

    [Fact]
    public void UnknownIdentityIsNotAConfirmedAbsence() => Assert.Equal(StalwartProcessState.Unknown,
        StalwartProcessProbe.Classify(Root, [new(71, false, null, "access denied"), new(72, false, Path.Combine(Root + "-other", "stalwart.exe"))]).State);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingLiveProcessIsPositiveEvidenceEvenWithOtherUnreadableProcesses(bool reverse)
    {
        StalwartProcessSample[] samples = [new(71, false, null), new(72, false, Path.Combine(Root, "stalwart.exe"))];
        Assert.Equal(StalwartProcessState.Running, StalwartProcessProbe.Classify(Root, reverse ? samples.Reverse() : samples).State);
    }

    [Fact]
    public void DifferentExecutableInsideTheInstallationIsNotMail() => Assert.False(
        StalwartProcessProbe.BelongsToInstallation(Root, Path.Combine(Root, "other.exe")));
}
