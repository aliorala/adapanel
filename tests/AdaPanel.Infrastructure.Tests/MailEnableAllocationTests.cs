using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailEnableAllocationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-me-allocation-" + Guid.NewGuid().ToString("N"));
    private JsonFileStore Store => new(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
    private MailEnableMailService Service(int quotaMb, bool measured = true) => new(NullLogger<MailEnableMailService>.Instance, Store)
    {
        KomutKosucusu = (_, _) => Task.FromResult(measured ? JsonSerializer.Serialize(new
        {
            domains = new[] { new { name = "example.test", status = 1 } },
            accounts = new[] { new { mailbox = "one", status = 1, limit = quotaMb == 0 ? -1 : quotaMb * 1024 } },
            aliases = Array.Empty<object>()
        }) : "{}")
    };

    [Fact]
    public async Task Saved_allocation_is_enforced_before_new_mailbox_or_quota_writes()
    {
        var service = Service(100);
        var result = await service.SetDomainQuotaAsync(new() { Domain = "example.test", MaxSizeMB = 150 });
        Assert.Equal(150, Assert.Single(result.Domains).MaxSizeMB);
        Assert.Equal(100, Assert.Single(result.Domains).AllocatedMB);
        Assert.Equal(1, Assert.Single(result.Domains).AccountCount);
        var create = await Assert.ThrowsAsync<AppException>(() => service.CreateAccountAsync(new()
            { Domain = "example.test", Address = "two", Password = "not-a-live-password", MaxSizeMB = 51 }));
        Assert.Equal("mail.domain_quota_exceeded", create.MessageKey);
        var unlimited = await Assert.ThrowsAsync<AppException>(() => service.SetAccountQuotaAsync(new()
            { Address = "one@example.test", MaxSizeMB = 0 }));
        Assert.Equal("mail.domain_quota_needs_mailbox_limit", unlimited.MessageKey);
    }

    [Theory]
    [InlineData(0, true, "mail.domain_quota_needs_mailbox_limit")]
    [InlineData(200, true, "mail.domain_quota_below_allocated")]
    [InlineData(0, false, "mail.domain_quota_unmeasured")]
    public async Task Unsafe_allocation_does_not_persist(int mailboxMb, bool measured, string key)
    {
        var error = await Assert.ThrowsAsync<AppException>(() => Service(mailboxMb, measured)
            .SetDomainQuotaAsync(new() { Domain = "example.test", MaxSizeMB = 150 }));
        Assert.Equal(key, error.MessageKey);
        Assert.False(File.Exists(Store.ResolvePath("config/mailenable-domain-quotas.json")));
    }

    [Fact]
    public async Task Clearing_one_domain_limit_preserves_other_domain_policy()
    {
        await Store.WriteAsync("config/mailenable-domain-quotas.json", new Dictionary<string, int>
            { ["example.test"] = 150, ["neighbor.test"] = 700 });
        await Service(100).SetDomainQuotaAsync(new() { Domain = "example.test", MaxSizeMB = 0 });
        var saved = await Store.ReadAsync("config/mailenable-domain-quotas.json", new Dictionary<string, int>());
        Assert.False(saved.ContainsKey("example.test"));
        Assert.Equal(700, saved["neighbor.test"]);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
