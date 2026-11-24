using AdaPanel.Core.Dtos.Mail;

namespace AdaPanel.Core.Interfaces;

/// <summary>Migration-only strict creation. Never invokes DNS publication or global policy synchronization.</summary>
public interface IMigrationMailProvisioner
{
    Task<MigrationMailSnapshot> ReadAsync(string domain, CancellationToken cancellationToken);
    Task<string> CreateDomainFreshAsync(string domain, CancellationToken cancellationToken);
    Task<string> CreateMailboxFreshAsync(string domainId, string address, string password, CancellationToken cancellationToken);
    Task SetMailboxQuotaAsync(string accountId, string address, long quotaMb, CancellationToken cancellationToken);
    Task SetMailboxActiveAsync(string accountId, string address, bool active, CancellationToken cancellationToken);
    Task SetDomainActiveAsync(string domainId, string domain, bool active, CancellationToken cancellationToken);
    Task CreateAliasFreshAsync(string domainId, string accountId, string address, bool active, CancellationToken cancellationToken);
    Task<string> CreateListFreshAsync(string domainId, CreateMailListRequest request, IReadOnlyCollection<string> aliases, CancellationToken cancellationToken);
}

public sealed class MigrationMailSnapshot
{
    public bool Ready { get; init; }
    public bool SchemaSupported { get; init; }
    public string Version { get; init; } = "";
    public bool DomainExists { get; init; }
    public string DomainId { get; init; } = "";
    public bool? DomainActive { get; init; }
    public bool ManualManagement { get; init; }
    public MigrationMailEndpoint? Endpoint { get; init; }
    public IReadOnlyList<MigrationMailAccountState> Accounts { get; init; } = [];
    public IReadOnlyList<MailAliasDto> Aliases { get; init; } = [];
    public IReadOnlyList<MailListDto> Lists { get; init; } = [];
    public IReadOnlyList<string> Blockers { get; init; } = [];
}
public sealed record MigrationMailEndpoint(string Host, int Port, bool StartTls);
public sealed record MigrationMailAccountState(string Id, string Address, long? QuotaMb, bool? Active);
