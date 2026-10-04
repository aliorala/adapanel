using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Files;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FileQuotaExactUsageTests
{
    private const long MiB = 1024 * 1024;

    [Theory]
    [InlineData(true, "")]
    [InlineData(false, "files.size_truncated_entries")]
    [InlineData(false, "files.size_truncated_time")]
    [InlineData(false, "files.size_truncated_denied")]
    public void TheKnownByteLowerBoundCannotBeRoundedOutOfTheQuota(bool measured, string reason)
    {
        var usage = new DiskUsage(0, measured, reason) { UsageBytes = 600 * 1024 };
        AssertQuota(() => FileQuotaGuard.EnsureRoom(1, usage, 500 * 1024, string.Empty));
    }

    [Fact]
    public void ExactByteCapacityIsNotRoundedUpToTheNextMebibyte()
    {
        var usage = new DiskUsage(0, true, string.Empty) { UsageBytes = 600 * 1024 };
        FileQuotaGuard.EnsureRoom(1, usage, 424 * 1024, string.Empty);
    }

    [Theory]
    [InlineData("files.size_truncated_entries")]
    [InlineData("files.size_truncated_time")]
    [InlineData("files.size_truncated_denied")]
    public void AnIncompleteUsageMeasurementRetainsTheExistingKnownLowerBoundPolicy(string reason)
    {
        var usage = new DiskUsage(0, false, reason) { UsageBytes = 600 * 1024 };
        FileQuotaGuard.EnsureRoom(1, usage, 424 * 1024, string.Empty);
    }

    [Fact]
    public void AnUnmeasuredIncomingTreeIsStillRejectedEvenWhenItsKnownBytesFit()
    {
        var usage = new DiskUsage(0, true, string.Empty) { UsageBytes = 1 };
        var error = Assert.Throws<AppException>(() =>
            FileQuotaGuard.EnsureRoom(1, usage, 1, "files.size_truncated_entries"));
        Assert.Equal("files.size_truncated_entries", error.MessageKey);
        Assert.Equal(413, error.HttpStatusCode);
    }

    [Fact]
    public void ExistingConstructorsDeconstructionAndLegacyFallbackRetainTheirContract()
    {
        var usage = new DiskUsage(1, false, "files.size_truncated_time");
        var (megabytes, measured, reason) = usage;
        Assert.Equal(1, megabytes);
        Assert.False(measured);
        Assert.Equal("files.size_truncated_time", reason);
        Assert.Null(usage.UsageBytes);
        FileQuotaGuard.EnsureRoom(1, usage, 0, string.Empty);
        AssertQuota(() => FileQuotaGuard.EnsureRoom(1, usage, 1, string.Empty));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void InvalidOptionalBytesCannotEraseTheExistingUsageBound(long exactBytes)
    {
        var usage = new DiskUsage(1, true, string.Empty) { UsageBytes = exactBytes };
        AssertQuota(() => FileQuotaGuard.EnsureRoom(1, usage, 1, string.Empty));
    }

    [Fact]
    public void AByteTotalCannotOverflowTheAdditionAndBecomeAvailableCapacity()
    {
        var usage = new DiskUsage(0, true, string.Empty) { UsageBytes = long.MaxValue };
        AssertQuota(() => FileQuotaGuard.EnsureRoom(1, usage, 1, string.Empty));
    }

    [Fact]
    public void ALegacyMebibyteTotalCannotOverflowTheUnitConversion()
    {
        var usage = new DiskUsage(long.MaxValue, true, string.Empty);
        AssertQuota(() => FileQuotaGuard.EnsureRoom(1, usage, 0, string.Empty));
    }

    [Fact]
    public void IncomingBytesCannotOverflowTheUsageComparison()
    {
        var usage = new DiskUsage(0, true, string.Empty) { UsageBytes = 64 };
        AssertQuota(() => FileQuotaGuard.EnsureRoom(1, usage, long.MaxValue, string.Empty));
    }

    [Fact]
    public void NegativeIncomingBytesDoNotHideAnAlreadyExceededQuota()
    {
        var usage = new DiskUsage(2, true, string.Empty) { UsageBytes = 2 * MiB };
        AssertQuota(() => FileQuotaGuard.EnsureRoom(1, usage, -long.MaxValue, string.Empty));
        FileQuotaGuard.EnsureRoom(1, new DiskUsage(-1, true, string.Empty), -1, string.Empty);
    }

    [Fact]
    public void UnlimitedSubscriptionsStillSkipQuotaEnforcement()
    {
        var usage = new DiskUsage(long.MaxValue, false, "files.size_truncated_time") { UsageBytes = long.MaxValue };
        FileQuotaGuard.EnsureRoom(0, usage, long.MaxValue, "files.size_truncated_entries");
    }

    private static void AssertQuota(Action action)
    {
        var error = Assert.Throws<AppException>(action);
        Assert.Equal("files.quota_exceeded", error.MessageKey);
        Assert.Equal(403, error.HttpStatusCode);
    }
}
