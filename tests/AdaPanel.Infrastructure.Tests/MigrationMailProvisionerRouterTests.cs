using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MigrationMailProvisionerRouterTests
{
    [Theory]
    [InlineData("domain")]
    [InlineData("mailbox")]
    [InlineData("quota")]
    [InlineData("mailbox-active")]
    [InlineData("domain-active")]
    [InlineData("alias")]
    [InlineData("list")]
    public async Task Every_mutation_rereads_active_provider_even_after_a_successful_Stalwart_preflight(string operation)
    {
        var inner = new Provisioner(); var provider = "stalwart"; var reads = 0;
        var router = new MigrationMailProvisionerRouter(inner, () => { reads++; return provider; });
        Assert.True((await router.ReadAsync("target.test", default)).Ready);
        provider = MailProviderSelection.Resolve("mailenable", true, true, false, true);
        var error = await Assert.ThrowsAsync<AppException>(() => Mutate(router, operation));
        Assert.Equal("migration.mail_target_provider_unsupported", error.MessageKey); Assert.Empty(error.Details);
        Assert.Equal(2, reads); Assert.Equal(1, inner.Calls); // Only the previous readonly Stalwart query ran.
    }
    [Theory]
    [InlineData("mailenable")]
    [InlineData("unknown")]
    [InlineData("ambiguous")]
    public async Task Unsupported_or_unmeasured_provider_is_closed_without_reading_stale_Stalwart_credentials(string selected)
    {
        var inner = new Provisioner();
        var router = new MigrationMailProvisionerRouter(inner, () => selected == "ambiguous" ? throw new InvalidOperationException("fixture-private-secret") : selected);
        var snapshot = await router.ReadAsync("target.test", default);
        Assert.False(snapshot.Ready); Assert.NotEmpty(snapshot.Blockers); Assert.Equal(0, inner.Calls);
        var error = await Assert.ThrowsAsync<AppException>(() => router.CreateMailboxFreshAsync("id", "a@target.test", "fixture-private-secret", default));
        Assert.DoesNotContain("fixture-private-secret", error.MessageKey + string.Join(',', error.Details));
        Assert.Equal(0, inner.Calls);
    }
    [Fact]
    public async Task Provider_is_rechecked_between_writes_and_the_same_Stalwart_instance_is_used_when_active()
    {
        var inner = new Provisioner(); var active = "stalwart";
        var router = new MigrationMailProvisionerRouter(inner, () => active);
        await router.CreateDomainFreshAsync("target.test", default); Assert.Equal(1, inner.Calls);
        active = "mailenable";
        await Assert.ThrowsAsync<AppException>(() => router.SetDomainActiveAsync("id", "target.test", false, default));
        Assert.Equal(1, inner.Calls);
    }
    private static Task Mutate(IMigrationMailProvisioner router, string operation) => operation switch
    {
        "domain" => router.CreateDomainFreshAsync("target.test", default),
        "mailbox" => router.CreateMailboxFreshAsync("id", "a@target.test", "fixture-private-secret", default),
        "quota" => router.SetMailboxQuotaAsync("id", "a@target.test", 100, default),
        "mailbox-active" => router.SetMailboxActiveAsync("id", "a@target.test", false, default),
        "domain-active" => router.SetDomainActiveAsync("id", "target.test", false, default),
        "alias" => router.CreateAliasFreshAsync("id", "id", "alias@target.test", false, default),
        "list" => router.CreateListFreshAsync("id", new() { Address = "list@target.test" }, [], default),
        _ => throw new InvalidOperationException()
    };
    private sealed class Provisioner : IMigrationMailProvisioner
    {
        internal int Calls;
        public Task<MigrationMailSnapshot> ReadAsync(string domain, CancellationToken token) { Calls++; return Task.FromResult(new MigrationMailSnapshot { Ready = true }); }
        private Task<string> Write() { Calls++; return Task.FromResult("id"); }
        public Task<string> CreateDomainFreshAsync(string domain, CancellationToken token) => Write();
        public Task<string> CreateMailboxFreshAsync(string id, string address, string password, CancellationToken token) => Write();
        public Task SetMailboxQuotaAsync(string id, string address, long quota, CancellationToken token) => Write();
        public Task SetMailboxActiveAsync(string id, string address, bool active, CancellationToken token) => Write();
        public Task SetDomainActiveAsync(string id, string domain, bool active, CancellationToken token) => Write();
        public Task CreateAliasFreshAsync(string domain, string id, string address, bool active, CancellationToken token) => Write();
        public Task<string> CreateListFreshAsync(string domain, CreateMailListRequest request, IReadOnlyCollection<string> aliases, CancellationToken token) => Write();
    }
}
