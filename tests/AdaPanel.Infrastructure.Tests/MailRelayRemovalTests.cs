using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailRelayRemovalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retrying_removal_after_local_publication_failure_clears_secret_without_recreating_route(bool rejectReload)
    {
        using var fixture = new Fixture { RejectReload = rejectReload };
        using var blocker = new FileStream(fixture.Store.ResolvePath("config/mail-relay.json"),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service.SetRelayAsync(new() { Enabled = false }));
        Assert.Equal("mail.relay_cleanup_incomplete", error.MessageKey);
        Assert.Equal(503, error.HttpStatusCode);
        Assert.Equal("HResult=0x80070020; Reload=" + (rejectReload ? "NotReloaded" : "Reloaded"), error.Details);
        Assert.DoesNotContain("fixture-secret", error.Details, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Area.Kok, error.Details, StringComparison.Ordinal);
        Assert.Equal("'mx'", fixture.Expression);
        Assert.False(fixture.RoutePresent);
        var partial = await fixture.Service.GetRelayAsync();
        Assert.False(partial.Enabled);
        Assert.True(partial.HasPassword);
        Assert.True((await fixture.Store.ReadAsync<MailService.MailRelayStore?>("config/mail-relay.json", null))!.Enabled);
        blocker.Dispose();
        fixture.RejectReload = false;

        var completed = await fixture.Service.SetRelayAsync(new() { Enabled = false });
        Assert.False(completed.Enabled);
        Assert.False(completed.HasPassword);
        Assert.Equal(1, fixture.RouteUpdates);
        Assert.Equal(1, fixture.Deletions);
        Assert.Equal(2, fixture.Reloads);
        var saved = await fixture.Store.ReadAsync<MailService.MailRelayStore?>("config/mail-relay.json", null);
        Assert.False(saved!.Enabled);
        Assert.Empty(saved.PasswordProtected);
        Assert.Empty(saved.Username);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Area.Kok, "config"), "*.tmp*"));
    }

    [Fact]
    public async Task Failed_reload_is_reported_after_local_secret_cleanup_and_retry_reloads_again()
    {
        using var fixture = new Fixture { RejectReload = true };
        var error = await Assert.ThrowsAsync<AppException>(() => fixture.Service.SetRelayAsync(new() { Enabled = false }));
        Assert.Equal("mail.settings_not_activated", error.MessageKey);
        var saved = await fixture.Store.ReadAsync<MailService.MailRelayStore?>("config/mail-relay.json", null);
        Assert.False(saved!.Enabled);
        Assert.Empty(saved.PasswordProtected);
        fixture.RejectReload = false;
        Assert.False((await fixture.Service.SetRelayAsync(new() { Enabled = false })).HasPassword);
        Assert.Equal(2, fixture.Reloads);
        Assert.Equal(1, fixture.Deletions);
        Assert.Equal(1, fixture.RouteUpdates);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly BulutAlani Area = new();
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly AdaPanelDbContext db;
        internal JsonFileStore Store => Area.DosyaDeposu;
        internal readonly MailService Service;
        internal string Expression = "'adapanel-relay'";
        internal bool RoutePresent = true, RejectReload;
        internal int RouteUpdates, Deletions, Reloads;

        internal Fixture()
        {
            connection.Open();
            db = new AdaPanelDbContext(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            var cliDirectory = Store.EnsureDirectory("fake-cli");
            File.WriteAllText(Path.Combine(cliDirectory, "stalwart-cli.exe"), "not executable");
            db.InstalledComponents.Add(new InstalledComponent
            { ComponentId = "stalwart-cli", InstallDir = cliDirectory, Status = ComponentInstallStatus.Installed, Type = ComponentType.Zip });
            db.SaveChanges();
            var protector = new SahteKoruyucu();
            Store.WriteAsync("config/mail.json", new { adminPasswordProtected = protector.Protect("fixture-admin") }).GetAwaiter().GetResult();
            Store.WriteAsync("config/mail-relay.json", new MailService.MailRelayStore
            { Enabled = true, Host = "relay.test", Username = "fixture-user", PasswordProtected = protector.Protect("fixture-secret"), PreviousRouteExpression = "'mx'" }).GetAwaiter().GetResult();
            Service = new MailService(Store, protector, null!, null!, null!, db,
                new StalwartRuntime(Store, protector, db, null!), NullLogger<MailService>.Instance)
                { CommandRunner = Execute };
        }

        private Task<CommandResult> Execute(string cli, string command, IReadOnlyDictionary<string, string> env, CancellationToken token)
        {
            object output = new { };
            if (command.StartsWith("get MtaOutboundStrategy ", StringComparison.Ordinal))
                output = new { route = new { match = Array.Empty<object>(), @else = Expression } };
            else if (command == "query MtaRoute --json")
                output = RoutePresent ? new[] { new { id = "relay-id", name = "adapanel-relay" } } : [];
            else if (command.StartsWith("update MtaOutboundStrategy singleton ", StringComparison.Ordinal))
            {
                Assert.Equal("update MtaOutboundStrategy singleton --field " +
                    WindowsArgument.Quote("route={\"match\":[],\"else\":" + JsonSerializer.Serialize("'mx'") + "}"), command);
                Expression = "'mx'";
                RouteUpdates++;
            }
            else if (command.StartsWith("delete MtaRoute --ids ", StringComparison.Ordinal))
            {
                Assert.Equal("'mx'", Expression);
                Assert.True(RoutePresent);
                RoutePresent = false;
                Deletions++;
            }
            else if (command.StartsWith("create Action --file ", StringComparison.Ordinal))
            {
                Reloads++;
                if (RejectReload) return Task.FromResult(new CommandResult { ExitCode = 1 });
            }
            else throw new InvalidOperationException("Unexpected command: " + command);
            return Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = JsonSerializer.Serialize(output) });
        }

        public void Dispose()
        {
            db.Dispose();
            connection.Dispose();
            Area.Dispose();
        }
    }
}
