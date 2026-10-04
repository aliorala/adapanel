using System.Data.Common;
using System.Security.Claims;
using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Health;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Health;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class ServiceHealthLifecycleTests
{
    [Theory]
    [InlineData("Stopped")]
    [InlineData("Unknown")]
    [InlineData("NotInstalled")]
    [InlineData("StartPending")]
    [InlineData("StopPending")]
    [InlineData("Paused")]
    [InlineData("missing")]
    public async Task Successful_command_without_a_running_measurement_is_not_success(string status)
    {
        using var f = new Fixture();
        f.Status = status;
        var failure = await Record.ExceptionAsync(() => f.Restart());
        Assert.IsType<AppException>(failure);
        Assert.Equal(1, f.Commands);
        Assert.Empty(f.Audit.Kayitlar);
    }

    [Theory]
    [InlineData("W3SVC")]
    [InlineData("w3svc")]
    [InlineData("AdaTask")]
    public async Task Running_measurement_preserves_successful_registered_targets(string name)
    {
        using var f = new Fixture();
        var result = await f.Restart(name);
        var response = Assert.IsType<ApiResponse<ServiceHealthDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(name == "AdaTask" ? "AdaTask" : "W3SVC", response.Data!.Name);
        Assert.Equal("Running", response.Data.Status);
        Assert.Equal(1, f.Commands);
        Assert.Single(f.Audit.Kayitlar);
    }

    [Fact]
    public async Task Unknown_target_never_reaches_the_native_command()
    {
        using var f = new Fixture();
        Assert.IsType<AppException>(await Record.ExceptionAsync(() => f.Restart("ArbitraryService")));
        Assert.Equal(0, f.Commands);
        Assert.Empty(f.Audit.Kayitlar);
    }

    [Fact]
    public async Task Failed_command_never_becomes_a_successful_restart()
    {
        using var f = new Fixture { CommandResult = new() { ExitCode = 5, StandardError = "fixture access denied" } };
        Assert.IsType<AppException>(await Record.ExceptionAsync(() => f.Restart()));
        Assert.Equal(0, f.StatusReads);
        Assert.Empty(f.Audit.Kayitlar);
    }

    [Fact]
    public async Task Failed_post_restart_read_does_not_publish_success()
    {
        using var f = new Fixture { ReadFailure = new IOException("fixture service query unavailable") };
        Assert.IsType<IOException>(await Record.ExceptionAsync(() => f.Restart()));
        Assert.Empty(f.Audit.Kayitlar);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("role")]
    [InlineData("password")]
    public async Task Queued_restart_rechecks_the_original_actor_before_native_effects(string change)
    {
        using var f = new Fixture();
        f.Barrier.Enabled = true;
        var action = Record.ExceptionAsync(() => f.Restart());
        await Task.WhenAny(action, f.Barrier.Entered.Task).WaitAsync(f.Token);
        Assert.True(f.Barrier.Entered.Task.IsCompleted, "Request ended before the installed-component read boundary.");
        await f.ChangeActor(change);
        f.Barrier.Release.TrySetResult();
        var error = Assert.IsType<AppException>(await action.WaitAsync(f.Token));
        Assert.Contains(error.HttpStatusCode, new[] { 401, 403 });
        Assert.Equal(0, f.Commands);
        Assert.Empty(f.Audit.Kayitlar);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("role")]
    [InlineData("password")]
    public async Task Invalid_current_actor_cannot_use_an_old_administrator_claim(string change)
    {
        using var f = new Fixture();
        await f.ChangeActor(change);
        var error = Assert.IsType<AppException>(await Record.ExceptionAsync(() => f.Restart()));
        Assert.Contains(error.HttpStatusCode, new[] { 401, 403 });
        Assert.Equal(0, f.Commands);
        Assert.Empty(f.Audit.Kayitlar);
    }

    [Fact]
    public async Task Queued_cancellation_never_reaches_the_command()
    {
        using var f = new Fixture();
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        f.Barrier.Enabled = true;
        var action = Record.ExceptionAsync(() => f.Restart(token: canceled.Token));
        await Task.WhenAny(action, f.Barrier.Entered.Task).WaitAsync(f.Token);
        Assert.True(f.Barrier.Entered.Task.IsCompleted);
        canceled.Cancel();
        Assert.IsAssignableFrom<OperationCanceledException>(await action.WaitAsync(f.Token));
        Assert.Equal(0, f.Commands);
        Assert.Empty(f.Audit.Kayitlar);
    }

    [Fact]
    public async Task Cancellation_returned_by_the_final_measurement_is_not_success()
    {
        using var f = new Fixture();
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        f.AfterRead = canceled.Cancel;
        Assert.IsAssignableFrom<OperationCanceledException>(await Record.ExceptionAsync(() => f.Restart(token: canceled.Token)));
        Assert.Empty(f.Audit.Kayitlar);
    }

    [Fact]
    public async Task Request_changes_while_awaiting_the_command_cannot_retarget_the_audit()
    {
        using var f = new Fixture();
        var request = new RestartServiceRequest { Name = "W3SVC" };
        f.DuringCommand = () => request.Name = "DNS";
        await f.Controller.RestartAsync(request, f.Token);
        var audit = Assert.Single(f.Audit.Kayitlar);
        Assert.Equal("service:W3SVC", audit.Resource);
        Assert.DoesNotContain("'DNS'", audit.Details);
    }

    [Fact]
    public async Task Trusted_service_caller_keeps_the_measured_restart_contract()
    {
        using var f = new Fixture();
        var result = await f.Service.RestartServiceAsync("W3SVC", f.Token);
        Assert.Equal("Running", result.Status);
        Assert.Equal(1, f.Commands);
        Assert.Empty(f.Audit.Kayitlar);
    }

    [Fact]
    public async Task Failed_authority_callback_prevents_the_native_command()
    {
        using var f = new Fixture();
        var failure = new AppException("auth.unauthorized", 401, 401);
        Assert.Same(failure, await Record.ExceptionAsync(() => f.Service.RestartServiceAsync("W3SVC",
            _ => Task.FromException(failure), f.Token)));
        Assert.Equal(0, f.Commands);
    }

    [Fact]
    public async Task Authority_callback_cannot_hide_cancellation_before_the_native_command()
    {
        using var f = new Fixture();
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        Assert.IsAssignableFrom<OperationCanceledException>(await Record.ExceptionAsync(() => f.Service.RestartServiceAsync("W3SVC",
            _ => { canceled.Cancel(); return Task.CompletedTask; }, canceled.Token)));
        Assert.Equal(0, f.Commands);
    }

    [Fact]
    public async Task Health_read_cannot_hide_cancellation_at_the_last_measurement()
    {
        using var f = new Fixture();
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        f.AfterRead = canceled.Cancel;
        Assert.IsAssignableFrom<OperationCanceledException>(await Record.ExceptionAsync(() => f.Service.GetHealthAsync(canceled.Token)));
    }

    [Fact]
    public async Task Current_database_administrator_cannot_use_a_different_claimed_role()
    {
        using var f = new Fixture();
        var identity = Assert.IsType<ClaimsIdentity>(f.Controller.User.Identity);
        identity.RemoveClaim(identity.FindFirst(ClaimTypes.Role)!);
        identity.AddClaim(new Claim(ClaimTypes.Role, "Customer"));
        Assert.IsType<AppException>(await Record.ExceptionAsync(() => f.Restart()));
        Assert.Equal(0, f.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_the_last_native_probe_is_not_a_completed_measurement(bool scheduledTask)
    {
        using var canceled = new CancellationTokenSource();
        string Probe(string _) { canceled.Cancel(); return "Running"; }
        Assert.IsAssignableFrom<OperationCanceledException>(await Record.ExceptionAsync(() => WindowsServiceInspection.ReadAsync(
            ["AdaTask"], canceled.Token, scheduledTask ? _ => "NotInstalled" : Probe, Probe)));
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("foreign-service")]
    [InlineData("wildcard-service")]
    [InlineData("duplicate-service")]
    [InlineData("foreign-task")]
    [InlineData("duplicate-task")]
    public void Native_restart_rejects_unmeasured_or_ambiguous_identity_before_any_mutation(string mode)
    {
        var result = RunScript(mode);
        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("MUTATE:", result.StdOut);
    }

    [Theory]
    [InlineData("service")]
    [InlineData("task")]
    [InlineData("mixed-service")]
    [InlineData("mixed-task")]
    public void Native_restart_preserves_exact_measured_service_and_task_paths(string mode)
    {
        var result = RunScript(mode);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(mode.EndsWith("service", StringComparison.Ordinal) ? "MUTATE:SERVICE:AdaTask" : "MUTATE:START:AdaTask", result.StdOut);
        Assert.DoesNotContain(mode.EndsWith("service", StringComparison.Ordinal) ? "MUTATE:START" : "MUTATE:SERVICE", result.StdOut);
        Assert.DoesNotContain("Another", result.StdOut);
    }

    private static ScriptTestHost.ScriptResult RunScript(string mode) => ScriptTestHost.Run($$"""
        $mode = '{{mode}}'
        function Get-Service {
            param($Name, $ErrorAction)
            switch ($mode) {
                'denied' { Write-Error 'fixture access denied' -ErrorAction $ErrorAction; return }
                'foreign-service' { [pscustomobject]@{Name='AnotherService';Status='Running'}; return }
                'wildcard-service' {
                    [pscustomobject]@{Name='AdaDatabase';Status='Running'}
                    [pscustomobject]@{Name='AdaMail';Status='Running'}
                    return
                }
                'duplicate-service' {
                    [pscustomobject]@{Name='AdaTask';Status='Running'}
                    [pscustomobject]@{Name='AdaTask';Status='Running'}
                    return
                }
                'service' { [pscustomobject]@{Name='AdaTask';Status='Running'}; return }
                'mixed-service' {
                    [pscustomobject]@{Name='AnotherService';Status='Running'}
                    [pscustomobject]@{Name='AdaTask';Status='Running'}
                    return
                }
                'mixed-task' { [pscustomobject]@{Name='AnotherService';Status='Running'}; return }
            }
            return $null
        }
        function Get-ScheduledTask {
            param($TaskName, $TaskPath, $ErrorAction)
            if ($mode -in @('foreign-service','wildcard-service')) { return $null }
            if ($mode -eq 'foreign-task') { [pscustomobject]@{TaskName='AnotherTask';TaskPath='\';State='Running'}; return }
            if ($mode -eq 'mixed-task') {
                [pscustomobject]@{TaskName='AnotherTask';TaskPath='\';State='Running'}
                [pscustomobject]@{TaskName='AdaTask';TaskPath='\Another\';State='Running'}
            }
            [pscustomobject]@{TaskName='AdaTask';TaskPath='\';State='Running'}
            if ($mode -eq 'duplicate-task') { [pscustomobject]@{TaskName='AdaTask';TaskPath='\';State='Running'} }
        }
        function Restart-Service {
            param($Name, $InputObject, [switch]$Force, $ErrorAction)
            if ($InputObject) { Write-Output ('MUTATE:SERVICE:' + $InputObject.Name) }
            else { Write-Output ('MUTATE:SERVICE:' + $Name) }
        }
        function Stop-ScheduledTask { param($InputObject,$ErrorAction) Write-Output ('MUTATE:STOP:' + $InputObject.TaskName) }
        function Start-ScheduledTask { param($InputObject,$ErrorAction) Write-Output ('MUTATE:START:' + $InputObject.TaskName) }
        {{HealthService.BuildRestartScript(mode == "wildcard-service" ? "Ada*" : "AdaTask")}}
        """, 15_000);

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection keeper = new($"Data Source=health-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        private readonly AdaPanelDbContext db;
        private readonly ServiceProvider services;
        internal readonly QueryBarrier Barrier = new();
        internal readonly SahteDenetim Audit = new();
        internal readonly PanelUser Admin = new() { UserName = "health-admin", Email = "health@example.test", Role = UserRole.Administrator, PasswordHash = "original-credential" };
        internal readonly ServerHealthController Controller;
        internal readonly HealthService Service;
        internal string Status = "Running";
        internal Exception? ReadFailure;
        internal Action? DuringCommand;
        internal Action? AfterRead;
        internal int Commands;
        internal int StatusReads;
        internal CommandResult CommandResult = new() { ExitCode = 0 };
        internal CancellationToken Token => deadline.Token;

        internal Fixture()
        {
            keeper.Open();
            using (var seed = NewContext())
            {
                seed.Database.EnsureCreated();
                seed.Users.Add(Admin);
                seed.InstalledComponents.Add(new InstalledComponent { ComponentId = "health-task", ServiceName = "AdaTask", Status = ComponentInstallStatus.Installed });
                seed.SaveChanges();
            }
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(keeper.ConnectionString).AddInterceptors(Barrier).Options);
            Service = new HealthService(db)
            {
                RestartCommand = (_, _) => { Commands++; DuringCommand?.Invoke(); return Task.FromResult(CommandResult); },
                ReadStatuses = (names, _) =>
                {
                    StatusReads++;
                    if (ReadFailure is not null) throw ReadFailure;
                    AfterRead?.Invoke();
                    return Task.FromResult(Status == "missing" ? new Dictionary<string, string>() : names.ToDictionary(name => name, _ => Status, StringComparer.OrdinalIgnoreCase));
                }
            };
            services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
            Controller = new ServerHealthController(Service, null!, Audit)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        RequestServices = services,
                        User = new ClaimsPrincipal(new ClaimsIdentity([
                            new Claim(ClaimTypes.NameIdentifier, Admin.Id.ToString()),
                            new Claim(ClaimTypes.Role, "Administrator"),
                            new Claim(CookieSessionValidator.CredentialStampClaim, CookieSessionValidator.ComputeStamp(Admin.PasswordHash))
                        ], "fixture"))
                    }
                }
            };
        }

        internal Task<ActionResult<ApiResponse<ServiceHealthDto>>> Restart(string name = "W3SVC", CancellationToken? token = null) =>
            Controller.RestartAsync(new RestartServiceRequest { Name = name }, token ?? Token);

        internal async Task ChangeActor(string change)
        {
            await using var context = NewContext();
            var user = await context.Users.SingleAsync(user => user.Id == Admin.Id, Token);
            switch (change)
            {
                case "deleted": context.Users.Remove(user); break;
                case "disabled": user.IsActive = false; break;
                case "role": user.Role = UserRole.Customer; break;
                case "password": user.PasswordHash = "replacement-credential"; break;
                default: throw new ArgumentOutOfRangeException(nameof(change));
            }
            await context.SaveChangesAsync(Token);
        }

        private AdaPanelDbContext NewContext() => new(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(keeper.ConnectionString).Options);

        public void Dispose()
        {
            Barrier.Release.TrySetResult();
            services.Dispose();
            db.Dispose();
            keeper.Dispose();
            deadline.Dispose();
        }
    }

    private sealed class QueryBarrier : DbCommandInterceptor
    {
        internal bool Enabled;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("installed_components", StringComparison.OrdinalIgnoreCase))
            {
                Enabled = false;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
