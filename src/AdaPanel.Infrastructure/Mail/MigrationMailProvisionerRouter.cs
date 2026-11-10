using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>Rechecks the ordinary readonly provider selection for every migration operation.</summary>
public sealed class MigrationMailProvisionerRouter : IMigrationMailProvisioner
{
    private readonly IMigrationMailProvisioner stalwart;
    private readonly Func<string> currentProvider;
    public MigrationMailProvisionerRouter(MailService stalwart, MailProviderSelection selection)
        : this(stalwart, () => selection.Current) { }
    internal MigrationMailProvisionerRouter(IMigrationMailProvisioner stalwart, Func<string> currentProvider)
    { this.stalwart = stalwart; this.currentProvider = currentProvider; }
    private void EnsureSupportedProvider()
    {
        string provider;
        try { provider = currentProvider(); }
        catch { throw new AppException("migration.mail_provider_unmeasured", 409, 409); }
        if (!string.Equals(provider, "stalwart", StringComparison.Ordinal))
            throw new AppException("migration.mail_target_provider_unsupported", 409, 409);
    }
    public async Task<MigrationMailSnapshot> ReadAsync(string domain, CancellationToken cancellationToken)
    {
        try { EnsureSupportedProvider(); }
        catch (AppException error) { return new() { Blockers = [error.MessageKey] }; }
        return await stalwart.ReadAsync(domain, cancellationToken);
    }
    public Task<string> CreateDomainFreshAsync(string domain, CancellationToken cancellationToken)
    { EnsureSupportedProvider(); return stalwart.CreateDomainFreshAsync(domain, cancellationToken); }
    public Task<string> CreateMailboxFreshAsync(string domainId, string address, string password, CancellationToken cancellationToken)
    { EnsureSupportedProvider(); return stalwart.CreateMailboxFreshAsync(domainId, address, password, cancellationToken); }
    public Task SetMailboxQuotaAsync(string accountId, string address, long quotaMb, CancellationToken cancellationToken)
    { EnsureSupportedProvider(); return stalwart.SetMailboxQuotaAsync(accountId, address, quotaMb, cancellationToken); }
    public Task SetMailboxActiveAsync(string accountId, string address, bool active, CancellationToken cancellationToken)
    { EnsureSupportedProvider(); return stalwart.SetMailboxActiveAsync(accountId, address, active, cancellationToken); }
    public Task SetDomainActiveAsync(string domainId, string domain, bool active, CancellationToken cancellationToken)
    { EnsureSupportedProvider(); return stalwart.SetDomainActiveAsync(domainId, domain, active, cancellationToken); }
    public Task CreateAliasFreshAsync(string domainId, string accountId, string address, bool active, CancellationToken cancellationToken)
    { EnsureSupportedProvider(); return stalwart.CreateAliasFreshAsync(domainId, accountId, address, active, cancellationToken); }
    public Task<string> CreateListFreshAsync(string domainId, CreateMailListRequest request, IReadOnlyCollection<string> aliases, CancellationToken cancellationToken)
    { EnsureSupportedProvider(); return stalwart.CreateListFreshAsync(domainId, request, aliases, cancellationToken); }
}
