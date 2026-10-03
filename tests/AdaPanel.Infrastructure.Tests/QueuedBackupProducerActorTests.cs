using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Infrastructure.Backup;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed partial class QueuedBackupActorLifecycleTests
{
    public static IEnumerable<object[]> ProducerRevocations() =>
        from operation in new[] { "site", "subscription", "database", "mail", "panel" }
        from change in new[] { "disabled", "deleted", "role", "password" }
        select new object[] { operation, change };

    [Theory]
    [MemberData(nameof(ProducerRevocations))]
    public async Task QueuedProducerRemeasuresTheActorBeforeOpeningTheBackupSource(string operation, string change)
    {
        using var f = new Fixture(stopAtProducerBoundary: true);
        await f.PrepareProducer(operation);
        var controller = f.Controller(f.Admin);
        await f.ControllerDb.Users.SingleAsync(user => user.Id == f.Admin, f.Token);
        using var source = await f.EnterSource();
        var action = Capture(() => f.InvokeProducer(controller, operation));
        await f.WaitForProducerSource(action);
        await f.Change(change, f.Admin);
        source.Dispose();
        AssertAuthorization(await action.WaitAsync(f.Token));
        await f.AssertNoProducedArchive();
        Assert.Equal(0, f.MailRuntimeReads);
    }

    public static IEnumerable<object[]> ProducerTenantRevocations() =>
        from operation in new[] { "site", "subscription" }
        from change in new[] { "section", "parent_disabled", "parent_deleted", "reparented", "detached" }
        select new object[] { operation, change };

    [Theory]
    [MemberData(nameof(ProducerTenantRevocations))]
    public async Task QueuedTenantProducerKeepsTheOriginalParentAndSection(string operation, string change)
    {
        using var f = new Fixture(stopAtProducerBoundary: true);
        await f.PrepareProducer(operation);
        using var source = await f.EnterSource();
        var action = Capture(() => f.InvokeProducer(f.Controller(f.Child), operation));
        await f.WaitForProducerSource(action);
        await f.Change(change, f.Child);
        source.Dispose();
        AssertAuthorization(await action.WaitAsync(f.Token));
        await f.AssertNoProducedArchive();
    }

    [Fact]
    public async Task SiteBackupCannotReadAReassignedSiteAfterTheSourceWait()
    {
        using var f = new Fixture(stopAtProducerBoundary: true);
        await f.PrepareProducer("site");
        using var source = await f.EnterSource();
        var action = Capture(() => f.InvokeProducer(f.Controller(f.Child), "site"));
        await f.WaitForProducerSource(action);
        await f.Change("source", f.Child);
        source.Dispose();
        AssertAuthorization(await action.WaitAsync(f.Token));
        await f.AssertNoProducedArchive();
    }

    [Theory]
    [InlineData("site")]
    [InlineData("subscription")]
    [InlineData("database")]
    [InlineData("mail")]
    [InlineData("panel")]
    public async Task AnActiveProducerPassesAuthorizationAtTheSafeFakeBoundary(string operation)
    {
        using var f = new Fixture(stopAtProducerBoundary: true);
        await f.PrepareProducer(operation);
        var error = await Assert.ThrowsAsync<AppException>(() => f.InvokeProducer(f.Controller(f.Admin), operation));
        Assert.Equal("fixture.producer_boundary", error.MessageKey);
        Assert.Equal(1, f.ProducerBoundaryAttempts);
        Assert.Empty(await f.Service.GetBackupsAsync(f.Token));
        Assert.Empty(f.Cloud.Istekler);
        Assert.False(Directory.Exists(Path.Combine(f.Area.Kok, "backups")));
    }

    [Theory]
    [InlineData("site")]
    [InlineData("subscription")]
    [InlineData("database")]
    [InlineData("panel")]
    public async Task ProducerCancellationWhileWaitingDoesNotCreateAnArchive(string operation)
    {
        using var f = new Fixture(stopAtProducerBoundary: true);
        await f.PrepareProducer(operation);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(f.Token);
        using var source = await f.EnterSource();
        var action = Capture(() => f.InvokeProducer(f.Controller(f.Admin), operation, canceled.Token));
        await f.WaitForProducerSource(action);
        canceled.Cancel();
        source.Dispose();
        Assert.IsAssignableFrom<OperationCanceledException>(await action.WaitAsync(f.Token));
        await f.AssertNoProducedArchive();
    }

    private sealed partial class Fixture
    {
        internal async Task PrepareProducer(string operation)
        {
            Record = new BackupRecordDto
            {
                Type = operation,
                Source = operation switch
                {
                    "site" => Site,
                    "subscription" => Customer.ToString("N"),
                    "database" => "postgresql:Foo",
                    "mail" => "stalwart",
                    "panel" => "panel",
                    _ => throw new InvalidOperationException(operation)
                }
            };
            await Area.KayitlariYazAsync();
        }

        internal async Task InvokeProducer(BackupController controller, string operation, CancellationToken? token = null)
        {
            var ct = token ?? Token;
            switch (operation)
            {
                case "site": await controller.BackupSiteAsync(new BackupSiteRequest { SiteName = Site }, ct); break;
                case "subscription": await controller.BackupSubscriptionAsync(new BackupSubscriptionRequest
                {
                    UserId = Customer, IncludeFiles = true, IncludeDatabases = false, IncludeMail = false, IncludeFtp = false
                }, ct); break;
                case "database": await controller.BackupDatabaseAsync(new BackupDatabaseRequest
                    { Engine = "postgresql", DatabaseName = "Foo" }, ct); break;
                case "mail": await controller.BackupMailAsync(new BackupMailRequest()); break;
                case "panel": await controller.BackupPanelAsync(new BackupPanelRequest(), ct); break;
                default: throw new InvalidOperationException(operation);
            }
        }

        internal async Task WaitForProducerSource(Task action)
        {
            while (!action.IsCompleted && References < 2) await Task.Delay(1, Token);
            Assert.False(action.IsCompleted, "The real producer did not reach the source lease wait.");
        }

        internal async Task AssertNoProducedArchive()
        {
            Assert.Equal(0, ProducerBoundaryAttempts);
            Assert.Empty(await Service.GetBackupsAsync(Token));
            Assert.Empty(Cloud.Istekler);
            Assert.Equal(0, OutboundAttempts);
            Assert.False(Directory.Exists(Path.Combine(Area.Kok, "backups")));
        }
    }
}
