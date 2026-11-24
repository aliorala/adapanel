using System.Security.AccessControl;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MigrationMailProvisionerTests
{
    [Theory]
    [InlineData("0.16.9")]
    [InlineData("0.16.26")]
    public async Task Native_fresh_creation_uses_private_json_secret_files_and_no_dns_or_global_policy_methods(string version)
    {
        using var f = new Fixture();
        f.Component.Version = version; await f.Db.SaveChangesAsync();
        var before = await f.Service.ReadAsync("target.test", default);
        Assert.True(before.Ready); Assert.True(before.SchemaSupported); Assert.False(before.DomainExists);
        var domainId = await f.Service.CreateDomainFreshAsync("target.test", default);
        var accountId = await f.Service.CreateMailboxFreshAsync(domainId, "alice@target.test", Fixture.Password, default);
        await f.Service.SetMailboxQuotaAsync(accountId, "alice@target.test", 512, default);
        await f.Service.SetMailboxActiveAsync(accountId, "alice@target.test", false, default);
        await f.Service.CreateAliasFreshAsync(domainId, accountId, "info@target.test", false, default);
        await f.Service.SetDomainActiveAsync(domainId, "target.test", false, default);
        var actual = await f.Service.ReadAsync("target.test", default);
        Assert.True(actual.ManualManagement); Assert.False(actual.DomainActive);
        var account = Assert.Single(actual.Accounts); Assert.False(account.Active); Assert.Equal(512, account.QuotaMb);
        Assert.False(Assert.Single(actual.Aliases).Active);
        Assert.DoesNotContain(Fixture.Password, string.Join('\n', f.Commands));
        Assert.All(f.CredentialFilesProtected, value => Assert.True(value));
        Assert.All(f.Mutations, command => Assert.True(command.StartsWith("create domain") || command.StartsWith("create Account/User") || command.StartsWith("update account") || command.StartsWith("update domain")));
        Assert.Equal(new[] { "authenticate", "emailReceive", "emailSend" }, f.Account!["permissions"].GetProperty("disabledPermissions").EnumerateObject().Select(property => property.Name));
        Assert.All(f.PayloadFiles, file => Assert.False(File.Exists(file)));
    }

    [Fact]
    public async Task Strict_create_rejects_existing_domain_instead_of_reusing_it()
    {
        using var f = new Fixture(); await f.Service.CreateDomainFreshAsync("target.test", default); f.Mutations.Clear();
        Assert.Equal("migration.mail_domain_exists", (await Assert.ThrowsAsync<AppException>(() => f.Service.CreateDomainFreshAsync("target.test", default))).MessageKey);
        Assert.Empty(f.Mutations);
    }

    [Fact]
    public async Task Unknown_version_or_described_schema_is_not_reported_as_ready_supported()
    {
        using var f = new Fixture(); f.Component.Version = "1.1.0"; await f.Db.SaveChangesAsync();
        Assert.False((await f.Service.ReadAsync("target.test", default)).SchemaSupported);
        f.Component.Version = "0.16.9"; await f.Db.SaveChangesAsync(); f.BadDescribe = true;
        Assert.False((await f.Service.ReadAsync("target.test", default)).SchemaSupported); Assert.Empty(f.Mutations);
    }

    [Fact]
    public async Task Rejected_native_secret_payload_never_escapes_through_error_details()
    {
        using var f = new Fixture(); var domain = await f.Service.CreateDomainFreshAsync("target.test", default); f.RejectAccount = true;
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.CreateMailboxFreshAsync(domain, "alice@target.test", Fixture.Password, default));
        Assert.Equal("migration.mail_provisioning_failed", error.MessageKey); Assert.Empty(error.Details);
        Assert.DoesNotContain(Fixture.Password, string.Join('\n', f.Commands));
    }
    private sealed class Fixture : IDisposable
    {
        internal const string Password = "PRIVATE-MIGRATION-MAILBOX-PASSWORD";
        private readonly string root = Path.Combine(Path.GetTempPath(), "AdaPanel.MailProvisioner." + Guid.NewGuid().ToString("N"));
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        internal readonly AdaPanelDbContext Db;
        internal readonly InstalledComponent Component = new() { ComponentId = "stalwart-mail", Status = ComponentInstallStatus.Installed, Version = "0.16.9" };
        internal readonly MailService Service;
        internal readonly List<string> Commands = [], Mutations = [], PayloadFiles = [];
        internal readonly List<bool> CredentialFilesProtected = [];
        internal Dictionary<string, JsonElement>? Domain, Account;
        internal bool BadDescribe, RejectAccount;
        internal Fixture()
        {
            PortableMigrationStorage.CreatePrivateDirectory(root);
            var options = Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root });
            var files = new JsonFileStore(options); connection.Open(); Db = new(new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(connection).Options); Db.Database.EnsureCreated();
            var cliRoot = files.EnsureDirectory("fake-cli"); File.WriteAllText(Path.Combine(cliRoot, "stalwart-cli.exe"), "not-executable");
            Db.InstalledComponents.AddRange(Component, new InstalledComponent { ComponentId = "stalwart-cli", Status = ComponentInstallStatus.Installed, InstallDir = cliRoot }); Db.SaveChanges();
            var protector = new Protector(); files.WriteAsync("config/mail.json", new { adminPasswordProtected = "fixture-admin-secret" }).GetAwaiter().GetResult();
            Service = new MailService(files, protector, null!, null!, null!, Db, new StalwartRuntime(files, protector, Db, null!), NullLogger<MailService>.Instance) { CommandRunner = Execute };
        }
        private Task<CommandResult> Execute(string executable, string command, IReadOnlyDictionary<string, string> environment, CancellationToken token)
        {
            Commands.Add(command); Assert.Equal("fixture-admin-secret", environment["STALWART_PASSWORD"]);
            if (command.StartsWith("describe ")) return Result(BadDescribe ? "unknown" : "name domainId credentials permissions quotas aliases isEnabled certificateManagement dkimManagement dnsManagement recipients", raw: true);
            if (command == "query domain --json") return Result(Domain == null ? Array.Empty<object>() : new object[] { Domain });
            if (command == "query account --json") return Result(Account == null ? [] : new object[] { Account });
            if (command == "query MailingList --json" || command == "query mailinglist --json") return Result(Array.Empty<object>());
            if (command == "query NetworkListener --json") return Result(new[] { new { name = "imap", protocol = "imap", bind = new Dictionary<string, bool> { ["0.0.0.0:993"] = true } } });
            if (command.StartsWith("get SystemSettings ")) return Result(new { defaultHostname = "mail.target.test" });
            if (command.StartsWith("get domain ")) return Result(Domain!);
            if (command.StartsWith("get account ")) return Result(Account!);
            var file = Regex.Match(command, " --file (.+)$");
            if (!file.Success) throw new InvalidOperationException("Unexpected management command.");
            var path = file.Groups[1].Value.Trim('"'); PayloadFiles.Add(path); CredentialFilesProtected.Add(new FileInfo(path).GetAccessControl().AreAccessRulesProtected);
            var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))!;
            Mutations.Add(command);
            if (command.StartsWith("create domain "))
            {
                Domain = payload; Put(Domain, "id", "domain-id");
            }
            else if (command.StartsWith("create Account/User "))
            {
                if (RejectAccount) return Task.FromResult(new CommandResult { ExitCode = 1, StandardError = Password });
                Account = payload; Put(Account, "id", "account-id"); Put(Account, "@type", "User"); Put(Account, "emailAddress", "alice@target.test");
                Put(Account, "permissions", new Dictionary<string, object> { ["@type"] = "Inherit" }); Put(Account, "aliases", new { });
            }
            else if (command.StartsWith("update account ")) foreach (var field in payload) Account![field.Key] = field.Value;
            else if (command.StartsWith("update domain ")) foreach (var field in payload) Domain![field.Key] = field.Value;
            else throw new InvalidOperationException("Unexpected mutation.");
            return Result(new { });
        }
        private static void Put<T>(Dictionary<string, JsonElement> target, string key, T value) => target[key] = JsonSerializer.SerializeToElement(value);
        private static Task<CommandResult> Result(object data, bool raw = false) => Task.FromResult(new CommandResult { ExitCode = 0, StandardOutput = raw ? (string)data : JsonSerializer.Serialize(data) });
        public void Dispose() { Db.Dispose(); connection.Dispose(); PortableMigrationStorage.DeleteOwnedDirectory(root); }
        private sealed class Protector : ISecretProtector { public string Protect(string value) => value; public string Unprotect(string value) => value; }
    }
}
