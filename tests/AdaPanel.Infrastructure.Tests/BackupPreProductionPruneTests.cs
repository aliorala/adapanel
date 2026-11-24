using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Automation;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.RunningJobs;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Subscriptions;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class BackupPreProductionPruneTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SameArchiveFailsInBothPhases_ReportsAttemptsInsteadOfDistinctBackups(bool subscription, bool remote)
    {
        using var fixture = new Fixture(subscription);
        await fixture.Configure();
        fixture.Prune = (_, _) => Task.FromResult(new BackupPruneResultDto
        {
            LocalDeleteFailed = remote ? 0 : 1,
            RemoteDeleteFailed = remote ? 1 : 0,
            Failures = ["same-archive.zip: delete_failed"]
        });

        var (status, details) = await fixture.Run();

        Assert.Equal(new[] { "prune", "produce", "prune" }, fixture.Events);
        Assert.Equal("completed_with_errors", status);
        Assert.Contains("same-archive.zip", details);
        if (subscription)
        {
            Assert.Contains("basarisiz silme denemeleri", details);
            Assert.Contains(remote ? "remote_delete_failed=2" : "local_delete_failed=2", details);
        }
        else
        {
            Assert.Contains(remote ? "2 uzak kopya silme denemesi basarisiz" : "2 yerel dosya silme denemesi basarisiz", details);
        }
        Assert.DoesNotContain("2 yedegin uzak kopyasi", details);
        Assert.DoesNotContain("2 yedegin yerel dosyasi", details);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrunesBeforeAllocationAndAfterPublication_ReportsFinalProtectionOnly(bool subscription)
    {
        using var fixture = new Fixture(subscription);
        await fixture.Configure();
        fixture.Prune = (phase, _) => Task.FromResult(phase == 1
            ? new BackupPruneResultDto { Removed = 3, RetainedExpired = 4, RetentionNotes = ["pre-only"] }
            : new BackupPruneResultDto { Removed = 2, RetainedExpired = 1, RetentionNotes = ["final-only"] });

        var (status, details) = await fixture.Run();

        Assert.Equal(new[] { "prune", "produce", "prune" }, fixture.Events);
        Assert.Equal("completed", status);
        Assert.Contains("5 eski yedek silindi", details);
        Assert.Contains("1 suresi dolmus yedek", details);
        Assert.Contains("final-only", details);
        Assert.DoesNotContain("pre-only", details);
        Assert.All(fixture.PruneScopes, scope => Assert.Equal(subscription ? fixture.Owner.ToString("N") : "global", scope));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PrePruneFailure_DoesNotSuppressProductionOrHideError(bool subscription, bool throws)
    {
        using var fixture = new Fixture(subscription);
        await fixture.Configure();
        fixture.Prune = (phase, _) => phase != 1 ? Task.FromResult(new BackupPruneResultDto())
            : throws ? Task.FromException<BackupPruneResultDto>(new IOException("pre-phase failure"))
            : Task.FromResult(new BackupPruneResultDto { LocalDeleteFailed = 1, Failures = ["pre-phase failure"] });

        var (status, details) = await fixture.Run();

        Assert.Equal(new[] { "prune", "produce", "prune" }, fixture.Events);
        Assert.Equal("completed_with_errors", status);
        Assert.Contains("pre-phase failure", details);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringPrePrune_StopsProductionAndPostPrune(bool subscription)
    {
        using var fixture = new Fixture(subscription);
        await fixture.Configure();
        using var canceled = new CancellationTokenSource();
        fixture.Prune = (_, _) =>
        {
            canceled.Cancel();
            return Task.FromCanceled<BackupPruneResultDto>(canceled.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Run(token: canceled.Token));

        Assert.Equal(new[] { "prune" }, fixture.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorityRevokedAtPruneBoundary_StopsBeforeDeletionAndProduction(bool subscription)
    {
        using var fixture = new Fixture(subscription);
        await fixture.Configure();
        var revoked = false;
        fixture.BeforePruneMutation = () => revoked = true;

        var failure = await Assert.ThrowsAsync<AppException>(() => fixture.Run(
            _ => revoked ? Task.FromException(new AppException("auth.revoked", 403, 403)) : Task.CompletedTask));

        Assert.Equal("auth.revoked", failure.MessageKey);
        Assert.Empty(fixture.Events);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-preprune-" + Guid.NewGuid().ToString("N"));
        private readonly bool subscription;
        private readonly JsonFileStore store;
        private readonly AutomationService automation;
        private readonly SubscriptionBackupScheduleService schedules;
        private int phases;
        public Guid Owner { get; } = Guid.NewGuid();
        public List<string> Events { get; } = [];
        public List<string> PruneScopes { get; } = [];
        public Action? BeforePruneMutation { get; set; }
        public Func<int, CancellationToken, Task<BackupPruneResultDto>> Prune { get; set; } = (_, _) => Task.FromResult(new BackupPruneResultDto());

        public Fixture(bool subscription)
        {
            this.subscription = subscription;
            Directory.CreateDirectory(root);
            store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            var jobs = new RunningJobService(store, NullLogger<RunningJobService>.Instance);
            var backups = Fake<IBackupService>((method, args) => method.Name switch
            {
                "PruneAsync" or "PruneSubscriptionAsync" => PrunePhase(method.Name, args),
                "BackupPanelAsync" or "BackupSubscriptionAsync" => Produce(method.Name, args),
                _ => throw new NotSupportedException(method.Name)
            });
            automation = new AutomationService(store, backups, null!, null!, null!, null!, null!, null!, null!,
                null!, null!, null!, null!, null!, null!, null!, new ConcurrencyLock(), null!, null!, jobs,
                NullLogger<AutomationService>.Instance);
            var scopes = Fake<ICustomerScopeService>((method, _) => method.Name == "GetScopeAsync"
                ? Task.FromResult(new CustomerScope([], 0, 0, 0, 0, 0)) : throw new NotSupportedException(method.Name));
            schedules = new SubscriptionBackupScheduleService(store, backups, scopes, null!, null!, new ConcurrencyLock(), jobs,
                NullLogger<SubscriptionBackupScheduleService>.Instance);
        }

        public async Task Configure()
        {
            await store.WriteAsync("config/automation.json", new
            {
                autoBackupEnabled = true, backupSites = false, backupDatabases = false, backupMail = false,
                backupPanelConfig = true, backupRetentionDays = 5, backupTargetType = "local", autoSslRenewEnabled = false, updateCheckEnabled = false
            });
            await store.WriteAsync("config/subscription-backups.json", new[] { new
            {
                userId = Owner.ToString("N"), enabled = true, targetType = "local", retentionDays = 5, keepCount = 2
            } });
        }

        public async Task<(string Status, string Details)> Run(Func<CancellationToken, Task>? check = null, CancellationToken token = default)
        {
            if (subscription)
            {
                var result = check is null ? await schedules.RunNowAsync(Owner, token) : await schedules.RunNowAsync(Owner, check, token);
                return (result.LastStatus, result.LastDetails);
            }
            var status = check is null ? await automation.RunBackupNowAsync(token) : await automation.RunBackupNowAsync(check, token);
            return (status.LastBackupStatus, status.LastBackupDetails);
        }

        private async Task<BackupPruneResultDto> PrunePhase(string name, object?[] args)
        {
            var token = (CancellationToken)args[^1]!;
            BeforePruneMutation?.Invoke();
            var check = args.OfType<Func<CancellationToken, Task>>().SingleOrDefault();
            if (check is not null) await check(token);
            Events.Add("prune");
            PruneScopes.Add(name == "PruneSubscriptionAsync" ? ((Guid)args[0]!).ToString("N") : "global");
            return await Prune(++phases, token);
        }

        private async Task<BackupRecordDto> Produce(string name, object?[] args)
        {
            var token = (CancellationToken)args[^1]!;
            var check = args.OfType<Func<CancellationToken, Task>>().SingleOrDefault();
            if (check is not null) await check(token);
            // Simulates allocation on the previously full volume: only the pre-pass frees space.
            Assert.True(phases > 0, "Archive allocation happened before existing eligible backups were pruned.");
            Events.Add("produce");
            return AutomationBackupOutcomeTests.Result(name == "BackupPanelAsync" ? "panel" : "subscription",
                name == "BackupPanelAsync" ? "panel" : Owner.ToString("N"), []);
        }

        public void Dispose() => Directory.Delete(root, recursive: true);
    }

    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, PostgresDatabaseIdentityConsumersTests.StrictProxy>();
        ((PostgresDatabaseIdentityConsumersTests.StrictProxy)(object)proxy).Handler = handler;
        return proxy;
    }
}
