using System.Security.Claims;
using System.Text.Json;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.System;
using AdaPanel.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SystemMonitoringMeasurementTests
{
    public static IEnumerable<object[]> InvalidCpuValues() => new[]
    { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d, 101d }.Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(InvalidCpuValues))]
    public async Task InvalidCpuSamplesAreUnknownAndStillProduceSerializableOverview(double cpu)
    {
        using var f = new Fixture { Cpu = () => cpu };
        var overview = await f.Service.GetOverviewAsync();
        Assert.False(overview.CpuMeasured);
        Assert.Equal(0, overview.CpuUsagePercent);
        Assert.True(overview.MemoryMeasured);
        Assert.NotEmpty(JsonSerializer.Serialize(overview));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("failure")]
    public async Task UnavailableCpuDoesNotDiscardIndependentMemoryAndDiskMeasurements(string reason)
    {
        using var f = new Fixture { Cpu = () => reason == "missing" ? null : throw new IOException("counter unavailable") };
        var overview = await f.Service.GetOverviewAsync();
        Assert.False(overview.CpuMeasured);
        Assert.Equal(0, overview.CpuUsagePercent);
        Assert.True(overview.MemoryMeasured);
        Assert.True(overview.DisksMeasured);
        Assert.True(Assert.Single(overview.Disks).Measured);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(25.125d)]
    [InlineData(100d)]
    public async Task ValidCpuSamplesIncludingRealZeroRemainMeasured(double cpu)
    {
        using var f = new Fixture { Cpu = () => cpu };
        var overview = await f.Service.GetOverviewAsync();
        Assert.True(overview.CpuMeasured);
        Assert.Equal(Math.Round(cpu, 2), overview.CpuUsagePercent);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("available-exceeds-total")]
    [InlineData("overflow")]
    [InlineData("failure")]
    public async Task InvalidMemoryIsUnknownWithoutHidingOtherMetrics(string reason)
    {
        using var f = new Fixture { Memory = () => reason switch
        {
            "zero" => new(0, 0), "available-exceeds-total" => new(100, 101),
            "overflow" => new(ulong.MaxValue, 1), _ => throw new IOException("memory query unavailable")
        } };
        var overview = await f.Service.GetOverviewAsync();
        Assert.False(overview.MemoryMeasured);
        Assert.Equal(0, overview.TotalMemoryBytes);
        Assert.Equal(0, overview.AvailableMemoryBytes);
        Assert.Equal(0, overview.UsedMemoryBytes);
        Assert.Equal(0, overview.MemoryUsagePercent);
        Assert.True(overview.CpuMeasured);
        Assert.True(overview.DisksMeasured);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(500L)]
    [InlineData(1000L)]
    public async Task ValidMemoryRetainsItsCapacityAndPercent(long available)
    {
        using var f = new Fixture { Memory = () => new(1000, (ulong)available) };
        var overview = await f.Service.GetOverviewAsync();
        Assert.True(overview.MemoryMeasured);
        Assert.Equal(1000, overview.TotalMemoryBytes);
        Assert.Equal(available, overview.AvailableMemoryBytes);
        Assert.Equal(1000 - available, overview.UsedMemoryBytes);
        Assert.Equal((1000 - available) / 10d, overview.MemoryUsagePercent);
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(-1L, 0L)]
    [InlineData(100L, -1L)]
    [InlineData(100L, 101L)]
    public async Task OneInvalidDiskDoesNotMasqueradeAsEmptyOrHideAValidDisk(long total, long free)
    {
        using var f = new Fixture { Disks = () => [new("bad", "bad", "NTFS", total, free), new("good", "good", "NTFS", 1000, 100)] };
        var overview = await f.Service.GetOverviewAsync();
        Assert.False(overview.DisksMeasured);
        var bad = Assert.Single(overview.Disks, disk => disk.Name == "bad");
        Assert.False(bad.Measured);
        Assert.Equal(0, bad.TotalBytes); Assert.Equal(0, bad.FreeBytes); Assert.Equal(0, bad.UsagePercent);
        var good = Assert.Single(overview.Disks, disk => disk.Name == "good");
        Assert.True(good.Measured); Assert.Equal(90, good.UsagePercent);
    }

    [Fact]
    public async Task DiskEnumerationFailureReturnsExplicitUnknownAndKeepsCpuAndMemory()
    {
        using var f = new Fixture { Disks = () => throw new UnauthorizedAccessException("disk query unavailable") };
        var overview = await f.Service.GetOverviewAsync();
        Assert.False(overview.DisksMeasured); Assert.Empty(overview.Disks);
        Assert.True(overview.CpuMeasured); Assert.True(overview.MemoryMeasured);
    }

    [Fact]
    public async Task FirstNetworkSnapshotHasCountersButNoMeasuredRateThenZeroTrafficIsMeasured()
    {
        using var f = new Fixture();
        var first = await f.Service.GetOverviewAsync();
        Assert.False(first.NetworkMeasured);
        var firstNic = Assert.Single(first.NetworkInterfaces);
        Assert.False(firstNic.RatesMeasured); Assert.Equal(1000, firstNic.TotalBytesReceived);
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        var second = await f.Service.GetOverviewAsync();
        Assert.True(second.NetworkMeasured);
        Assert.True(Assert.Single(second.NetworkInterfaces).RatesMeasured);
        Assert.Equal(0, Assert.Single(second.NetworkInterfaces).BytesReceivedPerSecond);
    }

    [Fact]
    public async Task RateUsesMonotonicElapsedTimeWhenWallClockMovesBackwards()
    {
        using var f = new Fixture();
        await f.Service.GetOverviewAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(10)); f.Clock.ShiftUtc(TimeSpan.FromHours(-1));
        f.Received = 2000; f.Sent = 1200;
        var nic = Assert.Single((await f.Service.GetOverviewAsync()).NetworkInterfaces);
        Assert.Equal(100, nic.BytesReceivedPerSecond);
        Assert.Equal(100, nic.BytesSentPerSecond);
        Assert.True(nic.RatesMeasured);
    }

    [Fact]
    public async Task AnOlderRequestCannotMoveTheNetworkBaselineBackwards()
    {
        using var f = new Fixture();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reads = 0;
        f.Memory = () =>
        {
            if (Interlocked.Increment(ref reads) == 1) { entered.Set(); release.Wait(deadline.Token); }
            return new(1000, 500);
        };
        var old = Task.Run(() => f.Service.GetOverviewAsync(deadline.Token), deadline.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10), deadline.Token));
            f.Clock.Advance(TimeSpan.FromSeconds(10));
            await f.Service.GetOverviewAsync(deadline.Token);
            f.Clock.Advance(TimeSpan.FromSeconds(10)); f.Received = 2000;
            release.Set();
            var nic = Assert.Single((await old.WaitAsync(deadline.Token)).NetworkInterfaces);
            Assert.Equal(100, nic.BytesReceivedPerSecond);
            Assert.True(nic.RatesMeasured);
        }
        finally { release.Set(); await Record.ExceptionAsync(() => old); }
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("removed")]
    [InlineData("invalid")]
    public async Task CounterDiscontinuityRequiresANewBaseline(string change)
    {
        using var f = new Fixture();
        await f.Service.GetOverviewAsync(); f.Clock.Advance(TimeSpan.FromSeconds(10));
        if (change == "removed") { f.Network = () => []; await f.Service.GetOverviewAsync(); f.Network = null; }
        else if (change == "invalid") { f.Received = -1; await f.Service.GetOverviewAsync(); }
        else { f.Received = 10; }
        f.Clock.Advance(TimeSpan.FromSeconds(10));
        if (change != "reset") f.Received = 2000;
        var current = await f.Service.GetOverviewAsync();
        Assert.False(current.NetworkMeasured);
        Assert.False(Assert.Single(current.NetworkInterfaces).RatesMeasured);
        Assert.Equal(0, Assert.Single(current.NetworkInterfaces).BytesReceivedPerSecond);
        f.Clock.Advance(TimeSpan.FromSeconds(10)); f.Received += 100;
        Assert.Equal(10, Assert.Single((await f.Service.GetOverviewAsync()).NetworkInterfaces).BytesReceivedPerSecond);
    }

    [Fact]
    public async Task FailedNetworkEnumerationIsUnknownWithoutLosingOtherMetrics()
    {
        using var f = new Fixture { Network = () => throw new IOException("NIC query unavailable") };
        var overview = await f.Service.GetOverviewAsync();
        Assert.False(overview.NetworkMeasured); Assert.Empty(overview.NetworkInterfaces);
        Assert.True(overview.CpuMeasured); Assert.True(overview.MemoryMeasured);
    }

    [Fact]
    public async Task AMeasuredEmptyNetworkListHasAMeasuredZeroAggregate()
    {
        using var f = new Fixture { Network = () => [] };
        await f.Service.RecordSampleAsync();
        var sample = Assert.Single(await f.Service.GetHistoryAsync());
        Assert.True(sample.NetworkMeasured);
        Assert.Equal(0, sample.NetworkDownBytesPerSec); Assert.Equal(0, sample.NetworkUpBytesPerSec);
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("memory")]
    [InlineData("disks")]
    [InlineData("network")]
    public async Task CancellationInsideAMetricReadCannotPublishAHistorySample(string metric)
    {
        using var f = new Fixture(); using var caller = new CancellationTokenSource();
        if (metric == "cpu") f.Cpu = () => { caller.Cancel(); return 10; };
        if (metric == "memory") f.Memory = () => { caller.Cancel(); return new(1000, 500); };
        if (metric == "disks") f.Disks = () => { caller.Cancel(); return []; };
        if (metric == "network") f.Network = () => { caller.Cancel(); return []; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.RecordSampleAsync(caller.Token));
        Assert.Empty(await f.Service.GetHistoryAsync());
    }

    [Fact]
    public async Task HistoryCopiesMeasurementFlagsAndTheObservedTimestamp()
    {
        using var f = new Fixture();
        await f.Service.RecordSampleAsync();
        var first = Assert.Single(await f.Service.GetHistoryAsync());
        Assert.Equal(f.Clock.GetUtcNow().UtcDateTime, first.TimestampUtc);
        Assert.True(first.CpuMeasured); Assert.True(first.MemoryMeasured); Assert.False(first.NetworkMeasured);
        f.Clock.Advance(TimeSpan.FromSeconds(10)); f.Cpu = () => null;
        await f.Service.RecordSampleAsync();
        var last = (await f.Service.GetHistoryAsync()).Last();
        Assert.False(last.CpuMeasured); Assert.True(last.MemoryMeasured); Assert.True(last.NetworkMeasured);
    }

    [Fact]
    public async Task EditingAReturnedHistoryDtoCannotRewriteTheServicesSavedSample()
    {
        using var f = new Fixture();
        await f.Service.RecordSampleAsync();
        var returned = Assert.Single(await f.Service.GetHistoryAsync());
        var time = returned.TimestampUtc; returned.CpuPercent = 999; returned.MemoryPercent = 999; returned.TimestampUtc = DateTime.MinValue;
        var stored = Assert.Single(await f.Service.GetHistoryAsync());
        Assert.Equal(25, stored.CpuPercent); Assert.Equal(50, stored.MemoryPercent); Assert.Equal(time, stored.TimestampUtc);
    }

    [Fact]
    public async Task ControllerCarriesUnknownMetadataAndStillRedactsTenantMetrics()
    {
        using var f = new Fixture { Cpu = () => double.NaN };
        var controller = new SystemController(f.Service) { ControllerContext = new ControllerContext
        { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Administrator")], "test")) } } };
        var admin = Assert.IsType<ApiResponse<SystemOverviewDto>>(Assert.IsType<OkObjectResult>((await controller.GetOverviewAsync(default)).Result).Value).Data!;
        Assert.False(admin.CpuMeasured); Assert.True(admin.MemoryMeasured);
        Assert.NotEmpty(JsonSerializer.Serialize(admin));
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Customer")], "test"));
        var tenant = Assert.IsType<ApiResponse<SystemOverviewDto>>(Assert.IsType<OkObjectResult>((await controller.GetOverviewAsync(default)).Result).Value).Data!;
        Assert.True(tenant.IsRestricted); Assert.False(tenant.CpuMeasured); Assert.False(tenant.MemoryMeasured);
        Assert.Empty(tenant.Disks); Assert.Empty(tenant.NetworkInterfaces); Assert.Equal(0, tenant.TotalMemoryBytes);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly ManualClock Clock = new();
        internal Func<double?> Cpu = () => 25;
        internal Func<SystemMemorySnapshot> Memory = () => new(1000, 500);
        internal Func<IReadOnlyCollection<SystemDiskSnapshot>> Disks = () => [new("C:", "system", "NTFS", 1000, 500)];
        internal Func<IReadOnlyCollection<SystemNetworkSnapshot>>? Network;
        internal long Received = 1000, Sent = 200;
        private SystemMonitoringService? service;
        internal SystemMonitoringService Service => service ??= new SystemMonitoringService(NullLogger<SystemMonitoringService>.Instance,
            new SystemMonitoringSources
            {
                Clock = Clock, ReadCpu = () => Cpu(), ReadMemory = () => Memory(), ReadDisks = () => Disks(),
                ReadNetwork = () => Network?.Invoke() ?? [new("nic", "Ethernet", "Ethernet", "Up", Received, Sent)]
            });
        public void Dispose() => service?.Dispose();
    }
    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        private long utcShift;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            .AddTicks(Interlocked.Read(ref ticks) + Interlocked.Read(ref utcShift));
        internal void Advance(TimeSpan value) => Interlocked.Add(ref ticks, value.Ticks);
        internal void ShiftUtc(TimeSpan value) => Interlocked.Add(ref utcShift, value.Ticks);
    }
}
