using System.Net;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.System;
using AdaPanel.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class SystemIpAddressMeasurementTests
{
    [Theory]
    [InlineData("initial")]
    [InlineData("partial")]
    [InlineData("foreign-cancellation")]
    public async Task AnIncompleteNativeMeasurementCannotBeReturnedAsAnEmptyOrPartialSuccess(string failure)
    {
        using var service = Service(() => failure == "initial"
            ? throw new IOException("adapter query unavailable; internal target")
            : FailingSequence(failure == "foreign-cancellation"));

        var error = await Assert.ThrowsAsync<AppException>(() => service.GetServerIpAddressesAsync());

        Assert.Equal("system.ip_addresses_unmeasured", error.MessageKey);
        Assert.Equal(503, error.HttpStatusCode);
        Assert.Empty(error.Details);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControllerDoesNotConvertAFailedMeasurementIntoASuccessEnvelope(bool partial)
    {
        using var service = Service(() => partial ? FailingSequence(false) : throw new UnauthorizedAccessException("native access denied"));
        var controller = Controller(service);
        var error = await Assert.ThrowsAsync<AppException>(() => controller.GetIpAddressesAsync(default));
        Assert.Equal("system.ip_addresses_unmeasured", error.MessageKey);
        Assert.Equal(503, error.HttpStatusCode);
    }

    [Theory]
    [InlineData("before-first")]
    [InlineData("after-first")]
    [InlineData("at-end")]
    [InlineData("throw")]
    public async Task CallerCancellationDuringNativeEnumerationNeverPublishesAddresses(string when)
    {
        using var caller = new CancellationTokenSource();
        using var service = Service(() => CanceledSequence(caller, when));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Controller(service).GetIpAddressesAsync(caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
    }

    [Fact]
    public async Task AnAlreadyCanceledRequestNeverInvokesTheNativeReader()
    {
        var calls = 0;
        using var caller = new CancellationTokenSource(); caller.Cancel();
        using var service = Service(() => { calls++; return []; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetServerIpAddressesAsync(caller.Token));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACompleteEmptyOrFilteredMeasurementIsStillASuccessfulEmptyList(bool filtered)
    {
        using var service = Service(() => filtered
            ? [Ip("127.0.0.1"), Ip("::1"), Ip("fe80::1234%7")]
            : []);
        var result = await Controller(service).GetIpAddressesAsync(default);
        var envelope = Assert.IsType<ApiResponse<IReadOnlyCollection<ServerIpAddressDto>>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(envelope.Success);
        Assert.Empty(envelope.Data!);
    }

    [Fact]
    public async Task CompleteMeasurementsPreserveUniqueAddressesAndTheirInterfaceWithoutIpv6ScopeIds()
    {
        using var service = Service(() =>
        [
            Ip("2001:db8::2%7", "public-v6"), Ip("192.0.2.2", "first"), Ip("192.0.2.2", "duplicate"),
            Ip("192.0.2.1", "other"), Ip("127.0.0.1"), Ip("fe80::1%9")
        ]);
        var result = (await service.GetServerIpAddressesAsync()).ToArray();
        Assert.Equal(["192.0.2.1", "192.0.2.2", "2001:db8::2"], result.Select(row => row.Address));
        Assert.Equal(["IPv4", "IPv4", "IPv6"], result.Select(row => row.Family));
        Assert.Equal(["other", "first", "public-v6"], result.Select(row => row.InterfaceName));
    }

    [Fact]
    public async Task RetryAfterAnIncompleteMeasurementUsesOnlyTheNewCompleteSnapshot()
    {
        var reads = 0;
        using var service = Service(() => ++reads == 1 ? FailingSequence(false) : [Ip("192.0.2.99", "recovered")]);
        await Assert.ThrowsAsync<AppException>(() => service.GetServerIpAddressesAsync());
        var row = Assert.Single(await service.GetServerIpAddressesAsync());
        Assert.Equal("192.0.2.99", row.Address);
        Assert.Equal("recovered", row.InterfaceName);
    }

    private static IEnumerable<SystemIpAddressSnapshot> FailingSequence(bool foreignCancellation)
    {
        yield return Ip("192.0.2.1");
        if (foreignCancellation) throw new OperationCanceledException("native operation ended without caller cancellation");
        throw new IOException("a later adapter failed");
    }

    private static IEnumerable<SystemIpAddressSnapshot> CanceledSequence(CancellationTokenSource caller, string when)
    {
        if (when == "before-first") caller.Cancel();
        yield return Ip("192.0.2.1");
        if (when is "after-first" or "throw") caller.Cancel();
        if (when == "throw") caller.Token.ThrowIfCancellationRequested();
        yield return Ip("::1");
        if (when == "at-end") caller.Cancel();
    }

    private static SystemIpAddressSnapshot Ip(string value, string name = "Ethernet") => new(IPAddress.Parse(value), name);
    private static SystemMonitoringService Service(Func<IEnumerable<SystemIpAddressSnapshot>> read) => new(
        NullLogger<SystemMonitoringService>.Instance, new SystemMonitoringSources
        {
            ReadCpu = () => throw new InvalidOperationException("IP query must not sample CPU"),
            ReadMemory = () => throw new InvalidOperationException("IP query must not sample memory"),
            ReadDisks = () => throw new InvalidOperationException("IP query must not enumerate disks"),
            ReadNetwork = () => throw new InvalidOperationException("IP query must not sample counters"),
            ReadIpAddresses = read
        });
    private static SystemController Controller(SystemMonitoringService service) => new(service)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Administrator")], "test"))
            }
        }
    };
}
