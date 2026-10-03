using System.Reflection;
using AdaPanel.Core.Dtos.Nginx;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Bootstrap;
using AdaPanel.Infrastructure.Nginx;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class NginxLifecycleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-nginx-lifecycle-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly AdaPanelDbContext db;
    private readonly NginxService service;

    public NginxLifecycleTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "conf"));
        File.WriteAllText(Path.Combine(root, "conf", "nginx.conf"), "original custom configuration");
        File.WriteAllText(Path.Combine(root, "conf", "adapanel.json"), "{\"listenPort\":8088}");
        connection.Open();
        db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.InstalledComponents.Add(new InstalledComponent { ComponentId = "nginx", InstallDir = root, Status = ComponentInstallStatus.Installed });
        db.SaveChanges();
        var audit = DispatchProxy.Create<IAuditService, AuditProxy>();
        service = new NginxService(db, audit);
    }

    [Fact]
    public async Task A_later_configure_waits_for_the_failed_activation_and_rollback_before_publishing()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondValidated = false;
        var failed = service.ConfigureAsync(new() { ListenPort = 9088 }, null, null,
            (directory, request, token) => NginxInstallationSupport.WriteConfigurationAsync(directory, request,
                (_, _) => Task.FromResult(new CommandResult { ExitCode = 0 }), async _ =>
                {
                    entered.SetResult(); await release.Task;
                    throw new IOException("fixture reload failed");
                }, token), _ => Task.FromResult(new NginxStatusDto()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = service.ConfigureAsync(new() { ListenPort = 9089 }, null, null,
                (directory, request, token) => NginxInstallationSupport.WriteConfigurationAsync(directory, request, (_, _) =>
                {
                    Assert.Equal("original custom configuration", File.ReadAllText(Path.Combine(root, "conf", "nginx.conf")));
                    Assert.Equal("{\"listenPort\":8088}", File.ReadAllText(Path.Combine(root, "conf", "adapanel.json")));
                    secondValidated = true; return Task.FromResult(new CommandResult { ExitCode = 0 });
                }, token), _ => Task.FromResult(new NginxStatusDto { ListenPort = 9089 }));
            Assert.False(secondValidated); Assert.False(second.IsCompleted);
            release.SetResult();
            await Assert.ThrowsAsync<IOException>(() => failed);
            Assert.Equal(9089, (await second.WaitAsync(TimeSpan.FromSeconds(5))).ListenPort);
            Assert.Contains("listen 127.0.0.1:9089;", File.ReadAllText(Path.Combine(root, "conf", "nginx.conf")));
            Assert.Equal(9089, (await NginxInstallationSupport.ReadConfigurationAsync(root, default)).ListenPort);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("install")]
    [InlineData("remove")]
    public async Task Bootstrap_operations_wait_for_the_same_lifecycle_before_touching_files_or_services(string action)
    {
        using var lease = await NginxLifecycle.EnterAsync(default);
        using var cts = new CancellationTokenSource();
        var bootstrapper = new ComponentBootstrapper(null!, null!, null!, null!,
            Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(root, "unused-data-root") }),
            null!, null!, NullLogger<ComponentBootstrapper>.Instance);
        var operation = action == "install"
            ? (Task)bootstrapper.InstallComponentInternalAsync(new() { Id = "nginx" }, null, null, cts.Token)
            : bootstrapper.RemoveAsync("nginx", null, null, cts.Token);
        Assert.False(operation.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(Directory.Exists(Path.Combine(root, "unused-data-root")));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("test")]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("reload")]
    public async Task Readers_and_state_actions_wait_for_installation_without_starting_native_commands(string action)
    {
        using var lease = await NginxLifecycle.EnterAsync(default);
        using var cts = new CancellationTokenSource();
        var operation = action switch
        {
            "status" => service.GetStatusAsync(cts.Token),
            "test" => service.TestConfigurationAsync(cts.Token),
            "start" => service.StartAsync(null, null, cts.Token),
            "stop" => service.StopAsync(null, null, cts.Token),
            _ => service.ReloadAsync(null, null, cts.Token)
        };
        Assert.False(operation.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task Other_components_do_not_wait_and_a_cancelled_waiter_does_not_release_the_owner()
    {
        using var lease = await NginxLifecycle.EnterAsync(default);
        Assert.Null(await NginxLifecycle.EnterForComponentAsync("php-8.3", default));
        using var cts = new CancellationTokenSource();
        var waiter = NginxLifecycle.EnterForComponentAsync("NGINX", cts.Token);
        Assert.False(waiter.IsCompleted); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        var next = NginxLifecycle.EnterAsync(default);
        Assert.False(next.IsCompleted);
        lease.Dispose(); lease.Dispose();
        using var nextLease = await next.WaitAsync(TimeSpan.FromSeconds(5));
        using var finalCts = new CancellationTokenSource();
        var final = NginxLifecycle.EnterAsync(finalCts.Token);
        Assert.False(final.IsCompleted); finalCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => final);
    }

    public class AuditProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == nameof(IAuditService.WriteAsync) ? Task.CompletedTask : throw new NotSupportedException();
    }

    public void Dispose() { db.Dispose(); connection.Dispose(); Directory.Delete(root, true); }
}
