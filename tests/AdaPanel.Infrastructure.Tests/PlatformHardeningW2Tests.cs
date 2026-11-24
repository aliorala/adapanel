using System.Net;
using System.Security.Cryptography;
using System.Text;
using AdaPanel.Api.Middleware;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Advisor;
using AdaPanel.Core.Dtos.Notifications;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Dtos.System;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Advisor;
using AdaPanel.Infrastructure.Automation;
using AdaPanel.Infrastructure.Notifications;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Scheduler;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>M5/M6/M8, uyarı geri çekilmesi, webhook imzası ve hata raporu kuyruğu.</summary>
public sealed class PlatformHardeningW2Tests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-w2-" + Guid.NewGuid().ToString("N"));

    public PlatformHardeningW2Tests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { /* temizlik zorunlu değil */ }
    }

    private JsonFileStore NewStore() => new(
        Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));

    // ---------- M6: zamanlama türü ----------

    [Theory]
    [InlineData("weekly")]
    [InlineData("monthly")]
    [InlineData("yearly")]
    [InlineData("")]
    [InlineData("hourlyy")]
    public void Unknown_schedule_type_is_rejected_not_turned_into_hourly(string type)
    {
        var request = new CreateScheduledTaskRequest { Name = "g", TaskType = "url", Url = "https://ornek.test/c", ScheduleType = type, Interval = 1 };
        var ex = Assert.Throws<AppException>(() => SchedulerService.BuildCreateScript(request, plan: null));
        Assert.Equal("cron.invalid_schedule_type", ex.MessageKey);
        Assert.Equal("cron.invalid_schedule_type", Assert.Throws<AppException>(() => SchedulerService.ValidateSchedule(request)).MessageKey);
    }

    [Theory]
    [InlineData("minute", 1439, true)]
    [InlineData("minute", 1440, false)]
    [InlineData("hourly", 744, true)]
    [InlineData("hourly", 745, false)]
    [InlineData("daily", 5000, true)]
    public void Interval_is_capped_per_type(string type, int interval, bool ok)
    {
        var request = new CreateScheduledTaskRequest { ScheduleType = type, Interval = interval };
        if (ok) SchedulerService.ValidateSchedule(request);
        else Assert.Equal("cron.invalid_interval", Assert.Throws<AppException>(() => SchedulerService.ValidateSchedule(request)).MessageKey);
    }

    [Theory]
    [InlineData("minutely", "-Minutes 5")]
    [InlineData("hourly", "-Hours 5")]
    public void Known_types_still_build_their_triggers(string type, string expected)
    {
        var script = SchedulerService.BuildCreateScript(
            new CreateScheduledTaskRequest { Name = "g", TaskType = "url", Url = "https://ornek.test/c", ScheduleType = type, Interval = 5 }, plan: null);
        Assert.Contains(expected, script, StringComparison.Ordinal);
    }

    // ---------- M8: izleme geçmişi kalıcılığı ----------

    private static SystemMonitoringSources Sources() => new()
    {
        ReadCpu = () => 10,
        ReadMemory = () => new(1000, 500),
        ReadDisks = () => [new("C:", "system", "NTFS", 1000, 500)],
        ReadNetwork = () => [new("nic", "Ethernet", "Ethernet", "Up", 1000, 200)]
    };

    [Fact]
    public async Task Monitoring_history_survives_restart()
    {
        using (var first = new SystemMonitoringService(NullLogger<SystemMonitoringService>.Instance, Sources(), NewStore()))
        {
            for (var i = 0; i < SystemMonitoringService.PersistEverySamples; i++) await first.RecordSampleAsync();
        }

        using var second = new SystemMonitoringService(NullLogger<SystemMonitoringService>.Instance, Sources(), NewStore());
        var history = await second.GetHistoryAsync();
        Assert.Equal(SystemMonitoringService.PersistEverySamples, history.Count);
        Assert.All(history, sample => Assert.Equal(10, sample.CpuPercent));
    }

    [Fact]
    public async Task Corrupt_history_file_does_not_break_monitoring()
    {
        var path = Path.Combine(root, "state", "monitoring-history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ not json");
        using var service = new SystemMonitoringService(NullLogger<SystemMonitoringService>.Instance, Sources(), NewStore());
        Assert.Empty(await service.GetHistoryAsync());
        await service.RecordSampleAsync();
        Assert.Single(await service.GetHistoryAsync());
    }

    // ---------- Uyarı geri çekilmesi ----------

    [Fact]
    public void Alert_backoff_grows_and_is_capped()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), AlertDeliveryFailureStore.Backoff(1));
        Assert.Equal(TimeSpan.FromMinutes(10), AlertDeliveryFailureStore.Backoff(2));
        Assert.Equal(TimeSpan.FromMinutes(20), AlertDeliveryFailureStore.Backoff(3));
        Assert.Equal(TimeSpan.FromHours(6), AlertDeliveryFailureStore.Backoff(50));
    }

    [Fact]
    public async Task Failed_alert_delivery_is_recorded_and_shown_in_attention_then_cleared()
    {
        var failures = new AlertDeliveryFailureStore(NewStore());
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await failures.RecordFailureAsync("disk:C", "disk:C (email:IOException, webhook:AppException)", now);
        await failures.RecordFailureAsync("disk:C", "disk:C (email:IOException, webhook:AppException)", now.AddMinutes(5));

        var stored = Assert.Single((await failures.ReadAsync()).Values);
        Assert.Equal(2, stored.Attempts);
        Assert.Equal(now, stored.FirstFailedUtc);
        Assert.Equal(now.AddMinutes(5).AddMinutes(10), stored.NextRetryUtc);

        var advisor = new AdvisorStub();
        var attention = new AttentionService(advisor, ComponentsStub(), null!, null!, null!, null!, null, failures);
        var item = Assert.Single((await attention.GetSummaryAsync()).Items, i => i.Id == "alert-delivery-failed");
        Assert.Equal(1, item.AffectedCount);

        await failures.ClearAsync(["disk:C"]);
        Assert.DoesNotContain((await attention.GetSummaryAsync()).Items, i => i.Id == "alert-delivery-failed");
    }

    // ---------- Webhook imza + yeniden deneme ----------

    private sealed class Protector : ISecretProtector
    {
        public string Protect(string value) => "protected:" + value;
        public string Unprotect(string value) => value[10..];
    }

    private sealed class Transport(params HttpStatusCode[] statuses) : HttpMessageHandler, IHttpClientFactory
    {
        public readonly List<(string? Signature, string Body)> Calls = [];
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Headers.TryGetValues("X-AdaPanel-Signature", out var values);
            Calls.Add((values?.SingleOrDefault(), await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(statuses[Math.Min(Calls.Count - 1, statuses.Length - 1)]);
        }
    }

    private async Task<(NotificationService Service, JsonFileStore Store)> NewNotifier(Transport transport)
    {
        var store = NewStore();
        await store.WriteAsync("config/notifications.json", new NotificationSettingsDto { WebhookUrl = "https://hooks.example.net/x" });
        var service = new NotificationService(store, transport, new Protector(),
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }))
        { WebhookRetryDelays = [TimeSpan.Zero, TimeSpan.Zero] };
        return (service, store);
    }

    [Fact]
    public async Task Webhook_is_signed_with_a_generated_persisted_secret()
    {
        var transport = new Transport(HttpStatusCode.OK);
        var (service, store) = await NewNotifier(transport);
        await service.SendWebhookAsync("adapanel.alert", "govde");

        var call = Assert.Single(transport.Calls);
        var parts = call.Signature!.Split(',');
        var timestamp = long.Parse(parts[0]["t=".Length..]);
        var secret = (await store.ReadAsync("config/notifications.json", new NotificationSettingsDto())).WebhookSecret;
        Assert.StartsWith("protected:", secret);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret["protected:".Length..]));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{call.Body}"))).ToLowerInvariant();
        Assert.Equal("v1=" + expected, parts[1]);
        // İstemci imza anahtarını ayarlar ekranında görebilmeli (alıcı doğrulasın diye).
        Assert.Equal(secret["protected:".Length..], (await service.GetSettingsAsync()).WebhookSecret);
    }

    [Fact]
    public async Task Webhook_retries_twice_on_server_errors_then_succeeds()
    {
        var transport = new Transport(HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var (service, _) = await NewNotifier(transport);
        await service.SendWebhookAsync("e", "b");
        Assert.Equal(3, transport.Calls.Count);
    }

    [Fact]
    public async Task Webhook_gives_up_after_two_retries()
    {
        var transport = new Transport(HttpStatusCode.InternalServerError);
        var (service, _) = await NewNotifier(transport);
        var ex = await Assert.ThrowsAsync<AppException>(() => service.SendWebhookAsync("e", "b"));
        Assert.Equal("notifications.webhook_failed", ex.MessageKey);
        Assert.Equal(3, transport.Calls.Count);
    }

    [Fact]
    public async Task Webhook_does_not_retry_client_errors()
    {
        var transport = new Transport(HttpStatusCode.NotFound);
        var (service, _) = await NewNotifier(transport);
        await Assert.ThrowsAsync<AppException>(() => service.SendWebhookAsync("e", "b"));
        Assert.Single(transport.Calls);
    }

    // ---------- M5: ayrı döngüler ----------

    [Fact]
    public async Task Watchdogs_keep_ticking_while_a_long_job_is_running()
    {
        var automation = new LoopProbe();
        var services = new ServiceCollection().AddScoped<IAutomationService>(_ => automation).BuildServiceProvider();
        var worker = new AutomationWorker(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AutomationWorker>.Instance)
        {
            StartupDelay = TimeSpan.Zero,
            PollInterval = TimeSpan.FromMilliseconds(20)
        };
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await automation.LongJobStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            SpinWait.SpinUntil(() => automation.WatchdogRuns >= 3, TimeSpan.FromSeconds(10));
            Assert.True(automation.WatchdogRuns >= 3, "nöbetçiler uzun işin arkasında bekledi");
            Assert.Equal(1, automation.LongJobRuns); // uzun iş üst üste binmez (tek-çalışma)
        }
        finally
        {
            automation.ReleaseLongJob.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class LoopProbe : IAutomationService
    {
        public int WatchdogRuns, LongJobRuns;
        public readonly TaskCompletionSource LongJobStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseLongJob = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RunWatchdogsAsync(CancellationToken cancellationToken = default) { Interlocked.Increment(ref WatchdogRuns); return Task.CompletedTask; }
        public async Task RunLongJobsAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref LongJobRuns);
            LongJobStarted.TrySetResult();
            await ReleaseLongJob.Task.WaitAsync(cancellationToken);
        }
        public Task RunDueJobsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Automation.AutomationStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Automation.AutomationStatusDto> UpdateSettingsAsync(AdaPanel.Core.Dtos.Automation.UpdateAutomationSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Automation.AutomationStatusDto> RunBackupNowAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Automation.AutomationStatusDto> RunSslRenewNowAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Backup.BackupRemoteTestResultDto> TestRemoteBackupAsync(AdaPanel.Core.Dtos.Backup.BackupRemoteTestRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // ---------- Hata raporu kuyruğu ----------

    private sealed class FakeQueue(bool accept) : IArkaPlanIsKuyrugu
    {
        public int Offered;
        public bool Kuyrukla(string ad, Func<IServiceProvider, CancellationToken, Task> @is) { Offered++; return accept; }
    }

    private static async Task Throw500(ApiExceptionHandlingMiddleware middleware)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        Assert.Equal(500, context.Response.StatusCode);
    }

    [Fact]
    public async Task Error_reports_go_through_the_bounded_queue_and_drops_are_counted()
    {
        RequestDelegate boom = _ => throw new AppException("x.failed", 500, 500);
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var accepting = new FakeQueue(accept: true);
        await Throw500(new ApiExceptionHandlingMiddleware(boom, NullLogger<ApiExceptionHandlingMiddleware>.Instance, scopes, accepting));
        Assert.Equal(1, accepting.Offered);

        var before = ApiExceptionHandlingMiddleware.DroppedErrorReports;
        var full = new FakeQueue(accept: false);
        await Throw500(new ApiExceptionHandlingMiddleware(boom, NullLogger<ApiExceptionHandlingMiddleware>.Instance, scopes, full));
        Assert.Equal(before + 1, ApiExceptionHandlingMiddleware.DroppedErrorReports);
    }

    // ---------- yardımcı saplamalar ----------

    private sealed class AdvisorStub : IAdvisorService
    {
        public Task<AdvisorReportDto> GetReportAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AdvisorReportDto());
    }

    private static IComponentBootstrapper ComponentsStub()
    {
        var proxy = System.Reflection.DispatchProxy.Create<IComponentBootstrapper, ZeroProxy>();
        return proxy;
    }

    public class ZeroProxy : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) => Task.FromResult(0);
    }
}
