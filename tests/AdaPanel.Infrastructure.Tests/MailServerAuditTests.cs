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

public sealed class MailServerAuditTests
{
    [Fact]
    public async Task Overview_counts_every_domain_without_selecting_a_domain()
    {
        using var f = new Fixture();
        f.Replies["query domain --json"] = Ok("""[{"id":"d","name":"travianpanel.com"},{"id":"other","name":"other.test"},{"id":"empty","name":"empty.test"}]""");
        f.Replies["query account --json"] = Ok("""[{"@type":"Account/User","id":"a","emailAddress":"info@TRAVIANPANEL.COM"},{"@type":"Account/User","id":"b","emailAddress":"a@other.test"},{"@type":"Account/User","id":"c","emailAddress":"b@other.test"},{"@type":"Account/User","id":"d","emailAddress":"outside@travianpanel.com.evil"}]""");

        var workspace = await f.Service.GetWorkspaceAsync(null);

        Assert.Equal(new[] { 1, 2, 0 }, workspace.Domains.Select(domain => domain.AccountCount));
        Assert.All(workspace.Domains, domain => Assert.True(domain.AccountCountMeasured));
        Assert.False(workspace.AccountsMeasured); // Overview counts are not a selected-domain quota inventory.
        Assert.Empty(workspace.Accounts);
        Assert.Equal(1, f.Commands.Count(command => command == "query account --json"));
        Assert.DoesNotContain(f.Commands, command => command.StartsWith("get account "));
    }

    [Fact]
    public async Task Selecting_one_domain_preserves_counts_for_other_domains_and_reuses_details()
    {
        using var f = new Fixture();
        f.Replies["query domain --json"] = Ok("""[{"id":"d","name":"example.test"},{"id":"e","name":"other.test"}]""");
        f.Replies["query account --json"] = Ok("""[{"id":"a"},{"id":"b"}]""");
        f.Replies["get account a --json"] = Ok("""{"emailAddress":"info@example.test"}""");
        f.Replies["get account b --json"] = Ok("""{"emailAddress":"info@other.test"}""");

        var workspace = await f.Service.GetWorkspaceAsync("example.test");

        Assert.All(workspace.Domains, domain => { Assert.Equal(1, domain.AccountCount); Assert.True(domain.AccountCountMeasured); });
        Assert.Equal("info@example.test", Assert.Single(workspace.Accounts).Address);
        Assert.Equal(1, f.Commands.Count(command => command == "get account a --json"));
        Assert.Equal(1, f.Commands.Count(command => command == "get account b --json"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[{\"id\":\"unreadable\"}]")]
    public async Task Unreadable_overview_count_is_not_measured_zero(string inventory)
    {
        using var f = new Fixture();
        f.Replies["query account --json"] = Ok(inventory);
        f.Replies["get account unreadable --json"] = new() { ExitCode = 1 };
        var workspace = await f.Service.GetWorkspaceAsync(null);
        Assert.True(workspace.Configured);
        Assert.False(Assert.Single(workspace.Domains).AccountCountMeasured);
        Assert.NotEmpty(workspace.StatusMessage);
    }

    [Fact]
    public async Task Measured_empty_overview_remains_zero()
    {
        using var f = new Fixture();
        var workspace = await f.Service.GetWorkspaceAsync(null);
        Assert.True(Assert.Single(workspace.Domains).AccountCountMeasured);
        Assert.Equal(0, workspace.Domains.Single().AccountCount);
        Assert.Empty(workspace.StatusMessage);
    }

    [Theory]
    [InlineData("Account/User", "Account/User", true, 1)]
    [InlineData("User", "User", true, 1)]
    [InlineData(null, "User", true, 1)]
    [InlineData("Group", "Group", true, 0)]
    [InlineData(null, "Group", true, 0)]
    [InlineData("Future", "User", false, 0)]
    [InlineData(null, "Future", false, 0)]
    [InlineData("Account/Group", "Account/Group", true, 0)]
    [InlineData(null, "Account/Group", true, 0)]
    [InlineData("Account/Future", "Account/User", false, 0)]
    [InlineData(null, "Account/Future", false, 0)]
    [InlineData(null, null, true, 1)]
    public async Task Overview_and_selected_inventory_share_mailbox_variant_rules(string? summaryType, string? detailType, bool measured, int count)
    {
        using var f = new Fixture();
        var summaryPrefix = summaryType is null ? "" : $"\"@type\":\"{summaryType}\",";
        var detailPrefix = detailType is null ? "" : $"\"@type\":\"{detailType}\",";
        f.Replies["query account --json"] = Ok($$"""[{ {{summaryPrefix}} "id":"a","emailAddress":"info@example.test"}]""");
        f.Replies["get account a --json"] = Ok("{" + detailPrefix + "\"emailAddress\":\"info@example.test\",\"aliases\":{\"0\":{\"name\":\"alias\",\"domainId\":\"d\"}}}");

        var overview = await f.Service.GetWorkspaceAsync(null);
        Assert.Equal(count, Assert.Single(overview.Domains).AccountCount);
        Assert.Equal(measured, overview.Domains.Single().AccountCountMeasured);

        var selected = await f.Service.GetWorkspaceAsync("example.test");
        Assert.Equal(count, Assert.Single(selected.Domains).AccountCount);
        Assert.Equal(measured, selected.AccountsMeasured);
        Assert.Equal(measured, selected.Domains.Single().AccountCountMeasured);
        Assert.Equal(count, selected.Accounts.Count);
        Assert.Equal(count, selected.Aliases.Count); // Alias records never add mailboxes.
    }

    [Fact]
    public async Task Repeated_identity_or_address_does_not_inflate_counts_or_allocation_inventory()
    {
        using var f = new Fixture();
        f.Replies["query account --json"] = Ok("""[{"id":"a","emailAddress":"info@example.test"},{"id":"a","emailAddress":"info@example.test"},{"id":"b","emailAddress":"INFO@example.test"}]""");
        f.Replies["get account a --json"] = Ok("""{"emailAddress":"info@example.test"}""");
        f.Replies["get account b --json"] = Ok("""{"emailAddress":"INFO@example.test"}""");
        var overview = await f.Service.GetWorkspaceAsync(null);
        Assert.Equal(1, Assert.Single(overview.Domains).AccountCount);
        Assert.True(overview.Domains.Single().AccountCountMeasured);
        Assert.Equal(1, f.Commands.Count(command => command == "get account a --json"));
        var selected = await f.Service.GetWorkspaceAsync("example.test");
        Assert.Single(selected.Accounts);
        Assert.Equal(1, Assert.Single(selected.Domains).AccountCount);
        Assert.True(selected.AccountsMeasured);
    }

    [Fact]
    public async Task Legacy_summary_with_unreadable_variant_detail_is_not_measured_zero()
    {
        using var f = new Fixture();
        f.Replies["query account --json"] = Ok("""[{"id":"a","emailAddress":"info@example.test"}]""");
        f.Replies["get account a --json"] = new() { ExitCode = 1 };
        var overview = await f.Service.GetWorkspaceAsync(null);
        Assert.False(Assert.Single(overview.Domains).AccountCountMeasured);
        Assert.NotEmpty(overview.StatusMessage);
    }

    [Theory]
    [InlineData("[]", null, true)]
    [InlineData("[{\"id\":\"a\"}]", "{\"emailAddress\":\"a@example.test\"}", true)]
    [InlineData("[{\"id\":\"a\"}]", null, false)]
    [InlineData("[{\"emailAddress\":\"a@example.test\"}]", null, false)]
    [InlineData("[{\"id\":\"a\"}]", "{}", false)]
    public async Task Account_count_requires_all_details(string inventory, string? detail, bool measured)
    {
        using var f = new Fixture();
        f.Replies["query account --json"] = Ok(inventory);
        f.Replies["get account a --json"] = detail is null ? new() { ExitCode = 1 } : Ok(detail);
        var workspace = await f.Service.GetWorkspaceAsync("example.test");
        Assert.Equal(measured, workspace.AccountsMeasured);
        Assert.Equal(measured, Assert.Single(workspace.Domains).AccountCountMeasured);
    }

    [Theory]
    [InlineData("broken json")]
    [InlineData("[{\"id\":\"q\"},null]")]
    [InlineData("{\"id\":\"q\"}\n{truncated")]
    public async Task Broken_queue_is_not_reported_as_configured_empty(string output)
    {
        using var f = new Fixture();
        f.Replies["query QueuedMessage --json"] = Ok(output);
        Assert.False((await f.Service.GetQueueAsync(50)).Configured);
    }

    [Fact]
    public async Task Valid_empty_queue_stays_measurable()
    {
        using var f = new Fixture();
        var queue = await f.Service.GetQueueAsync(50);
        Assert.True(queue.Configured);
        Assert.Equal(0, queue.TotalCount);
    }

    [Theory]
    [InlineData("broken json")]
    [InlineData("null")]
    public async Task Broken_domain_inventory_is_not_reported_as_configured(string output)
    {
        using var f = new Fixture();
        f.Replies["query domain --json"] = Ok(output);
        Assert.False((await f.Service.GetWorkspaceAsync(null)).Configured);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_cascade_does_not_continue_to_delete_domain(bool alias)
    {
        using var f = new Fixture();
        f.Replies["query account --json"] = Ok("[{\"id\":\"a\"}]");
        f.Replies["get account a --json"] = Ok(alias
            ? "{\"emailAddress\":\"user@other.test\",\"aliases\":{\"0\":{\"name\":\"info\",\"domainId\":\"d\"}}}"
            : "{\"emailAddress\":\"user@example.test\",\"domainId\":\"d\"}");
        f.FailAccountMutation = true;
        await Assert.ThrowsAsync<AppException>(() => f.Service.DeleteDomainAsync("example.test"));
        Assert.Contains(f.Commands, command => command.StartsWith(alias ? "update account " : "delete account "));
        Assert.DoesNotContain(f.Commands, command => command.StartsWith("delete domain "));
    }

    private static CommandResult Ok(string output = "[]") => new() { ExitCode = 0, StandardOutput = output };

    [Fact]
    public async Task Queue_summary_with_size_and_date_still_loads_missing_recipients()
    {
        using var f = new Fixture();
        f.Replies["query QueuedMessage --json"] = Ok("""[{"id":"q","returnPath":"sender@example.test","size":527,"createdAt":"2026-10-16T10:00:00Z"}]""");
        f.Replies["get QueuedMessage q --json"] = Ok("""{"id":"q","size":527,"createdAt":"2026-10-16T10:00:00Z","recipients":{"target@example.test":{"status":{"@type":"TemporaryFailure"},"retryCount":8}}}""");
        var message = Assert.Single((await f.Service.GetQueueAsync(50)).Messages);
        Assert.Equal("target@example.test", Assert.Single(message.Recipients).Address);
        Assert.Equal(8, message.Recipients.Single().RetryCount);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly BulutAlani area = new();
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly AdaPanelDbContext db;
        internal readonly MailService Service;
        internal readonly Dictionary<string, CommandResult> Replies = new();
        internal readonly List<string> Commands = [];
        internal bool FailAccountMutation;

        internal Fixture()
        {
            connection.Open();
            db = new(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options);
            db.Database.EnsureCreated();
            var cliDirectory = area.DosyaDeposu.EnsureDirectory("fake-cli");
            File.WriteAllText(Path.Combine(cliDirectory, "stalwart-cli.exe"), "never executed");
            db.InstalledComponents.Add(new InstalledComponent
            { ComponentId = "stalwart-cli", InstallDir = cliDirectory, Status = ComponentInstallStatus.Installed, Type = ComponentType.Zip });
            db.SaveChanges();
            area.DosyaDeposu.WriteAsync("config/mail.json", new
            { adminPasswordProtected = SahteKoruyucu.Onek + "fixture-password" }).GetAwaiter().GetResult();
            var protector = new SahteKoruyucu();
            var runtime = new StalwartRuntime(area.DosyaDeposu, protector, db, new SahteIstemciFabrikasi(new SahteBulut()));
            Replies["query domain --json"] = Ok("[{\"id\":\"d\",\"name\":\"example.test\"}]");
            Service = new(area.DosyaDeposu, protector, null!, null!, null!, db, runtime, NullLogger<MailService>.Instance)
            {
                CommandRunner = (_, command, _, _) =>
                {
                    Commands.Add(command);
                    return Task.FromResult(FailAccountMutation && (command.StartsWith("delete account ") || command.StartsWith("update account "))
                        ? new CommandResult { ExitCode = 1, StandardError = "provider rejected change" }
                        : Replies.GetValueOrDefault(command) ?? Ok());
                }
            };
        }

        public void Dispose() { db.Dispose(); connection.Dispose(); area.Dispose(); }
    }
}
