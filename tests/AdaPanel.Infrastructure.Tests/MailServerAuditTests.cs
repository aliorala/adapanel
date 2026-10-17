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
