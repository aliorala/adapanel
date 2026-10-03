using System.Reflection;
using System.Net;
using System.Net.Sockets;
using AdaPanel.Core.Common;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Cache;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class CacheInspectionTruthTests : IDisposable
{
    private readonly SqliteConnection connection = new("Filename=:memory:");
    private readonly AdaPanelDbContext context;

    public CacheInspectionTruthTests()
    {
        connection.Open();
        context = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
    }

    [Theory]
    [InlineData(1, "", "access denied")]
    [InlineData(0, "", "")]
    [InlineData(0, "not json", "")]
    [InlineData(0, "{}", "")]
    [InlineData(0, "[]", "")]
    [InlineData(0, "{\"Status\":null}", "")]
    [InlineData(0, "{\"Status\":42}", "")]
    [InlineData(0, "{\"Status\":\"\"}", "")]
    [InlineData(0, "{\"Status\":\"Stopped\"}", "inspection reported an error")]
    public async Task Failed_or_unreadable_service_inspection_is_unknown(int exitCode, string output, string error)
    {
        Installed("redis");
        var service = Create(_ => new CommandResult { ExitCode = exitCode, StandardOutput = output, StandardError = error });
        var status = await service.GetStatusAsync();
        Assert.True(status.Redis.IsInstalled);
        Assert.Equal("Unknown", status.Redis.Status);
        Assert.False(status.Redis.IsServiceRegistered);
    }

    [Fact]
    public async Task A_failed_inspection_process_does_not_abort_the_entire_status_response()
    {
        Installed("redis");
        var service = Create(_ => throw new IOException("PowerShell could not start"));
        var status = await service.GetStatusAsync();
        Assert.Equal("Unknown", status.Redis.Status);
        Assert.Equal("NotInstalled", status.Memcached.Status);
    }

    [Theory]
    [InlineData("{\"Exists\":false}", false)]
    [InlineData("{\"Status\":\"Stopped\"}", true)]
    public async Task Measured_absence_and_a_stopped_service_remain_distinguishable(string output, bool registered)
    {
        Installed("redis");
        var status = await Create(_ => Success(output)).GetStatusAsync();
        Assert.Equal("Stopped", status.Redis.Status);
        Assert.Equal(registered, status.Redis.IsServiceRegistered);
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Stopped")]
    [InlineData("StartPending")]
    [InlineData("StopPending")]
    [InlineData("Paused")]
    public async Task Successful_service_state_reads_preserve_the_measured_state(string value)
    {
        var service = Create(_ => Success($"{{\"Status\":\"{value}\"}}"));
        var method = typeof(CacheService).GetMethod("ReadServiceStatusAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = await (Task<string?>)method.Invoke(service, ["fixture-service", CancellationToken.None])!;
        Assert.Equal(value, result);
    }

    [Theory]
    [InlineData("redis", true)]
    [InlineData("redis", false)]
    [InlineData("memcached", true)]
    [InlineData("memcached", false)]
    public async Task Unknown_inspection_prevents_start_or_stop_side_effects(string component, bool start)
    {
        Installed(component);
        var service = Create(_ => new CommandResult { ExitCode = 1, StandardError = "inspection unavailable" }, taskResult: new CommandResult { ExitCode = 1, StandardError = "inspection unavailable" });
        var error = await Assert.ThrowsAsync<AppException>(() => component == "redis"
            ? start ? service.StartRedisAsync() : service.StopRedisAsync()
            : start ? service.StartMemcachedAsync() : service.StopMemcachedAsync());
        Assert.Equal("cache.status_unavailable", error.MessageKey);
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(0, "")]
    [InlineData(0, "unreadable")]
    public async Task Unknown_task_registration_is_not_reported_as_a_stopped_cache(int exitCode, string output)
    {
        Installed("memcached");
        var status = await Create(_ => Success("{\"Exists\":false}"), new CommandResult { ExitCode = exitCode, StandardOutput = output }).GetStatusAsync();
        Assert.Equal("Unknown", status.Memcached.Status);
    }

    [Fact]
    public async Task An_explicitly_missing_service_can_still_be_reported_stopped()
    {
        Installed("redis");
        var result = await Create(_ => Success("{\"Exists\":false}")).StopRedisAsync();
        Assert.Equal("Stopped", result.Status);
        Assert.False(result.IsServiceRegistered);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated_instead_of_becoming_unknown()
    {
        using var cancelled = new CancellationTokenSource();
        var service = Create(_ => { cancelled.Cancel(); throw new OperationCanceledException(cancelled.Token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetStatusAsync(cancelled.Token));
    }

    [Theory]
    [InlineData(null, "Unknown")]
    [InlineData("unexpected protocol", "Unknown")]
    [InlineData("", "Stopped")]
    [InlineData("VERSION 1.6.8", "Running")]
    public async Task Cache_protocol_measurements_distinguish_failure_refusal_and_live_service(string? probeResult, string expected)
    {
        Installed("memcached");
        var service = Create(_ => Success("{\"Exists\":false}"), Success("true"), probeResult);
        var status = await service.GetStatusAsync();
        Assert.Equal(expected, status.Memcached.Status);
    }

    [Fact]
    public async Task A_valid_cache_protocol_response_remains_proof_of_running_when_task_inspection_fails()
    {
        Installed("memcached");
        var status = await Create(_ => Success("{\"Exists\":false}"), new CommandResult { ExitCode = 1 }, "VERSION 1.6.8").GetStatusAsync();
        Assert.Equal("Running", status.Memcached.Status);
    }

    [Fact]
    public async Task Port_probe_cancellation_is_not_reported_as_a_stopped_service()
    {
        var service = Create(_ => Success("{\"Exists\":false}"));
        var method = typeof(CacheService).GetMethod("ProbeTextAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await (Task<string?>)method.Invoke(service, [0, "version\r\n", new CancellationToken(canceled: true)])!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completion_wait_requires_a_measured_result_after_an_unknown_probe(bool expectedAlive)
    {
        var reads = 0;
        var service = Create(_ => Success("{\"Exists\":false}"), probe: (_, _, _) =>
            Task.FromResult(++reads == 1 ? null : expectedAlive ? "VERSION 1.6.8" : ""));
        var descriptor = typeof(CacheService).GetProperty("MemcachedDescriptor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service);
        var method = typeof(CacheService).GetMethod("WaitForLivenessAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var completed = await (Task<bool>)method.Invoke(service, [descriptor, expectedAlive, CancellationToken.None])!;
        Assert.True(completed);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task A_connected_peer_closing_without_a_protocol_response_is_unknown_not_connection_refused()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var peerTask = CloseAfterReceivingCommand();
        var service = Create(_ => Success("{\"Exists\":false}"));
        var method = typeof(CacheService).GetMethod("ProbeTextAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var response = await (Task<string?>)method.Invoke(service, [port, "version\r\n", deadline.Token])!;
        await peerTask;
        Assert.Null(response);

        async Task CloseAfterReceivingCommand()
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            var buffer = new byte[256];
            await peer.GetStream().ReadAsync(buffer, deadline.Token);
            peer.Client.Shutdown(SocketShutdown.Send);
        }
    }

    private CacheService Create(Func<string, CommandResult> serviceResult, CommandResult? taskResult = null, string? probeResult = "", Func<int, string, CancellationToken, Task<string?>>? probe = null)
        => new(context)
        {
            // All OS inspection and protocol responses are fixtures. Port zero is
            // retained as a second guard against reaching a real cache instance.
            MemcachedProbePort = 0,
            InspectCachePortAsync = probe ?? ((_, _, _) => Task.FromResult(probeResult)),
            InspectPowerShellAsync = (script, _) =>
            {
                Assert.DoesNotContain("Start-Service", script);
                Assert.DoesNotContain("Stop-Service", script);
                Assert.True(script.Contains("Get-Service", StringComparison.Ordinal) || script.Contains("Get-ScheduledTask", StringComparison.Ordinal));
                return Task.FromResult(script.Contains("Get-Service", StringComparison.Ordinal) ? serviceResult(script) : taskResult ?? Success("false"));
            }
        };

    private static CommandResult Success(string output) => new() { ExitCode = 0, StandardOutput = output };
    private void Installed(string component)
    {
        context.InstalledComponents.Add(new InstalledComponent
        {
            ComponentId = component, DisplayName = component, Version = "fixture", Type = ComponentType.Zip,
            InstallDir = Path.Combine(Path.GetTempPath(), "adapanel-cache-inspection-missing-" + Guid.NewGuid().ToString("N")),
            ServiceName = "AdaFixture_" + Guid.NewGuid().ToString("N"), Status = ComponentInstallStatus.Installed
        });
        context.SaveChanges();
    }
    public void Dispose() { context.Dispose(); connection.Dispose(); }
}
