using System.Runtime.Versioning;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>
/// IMailService dağıtıcısı. BİRİNCİL = Stalwart (modern, DKIM/SPF/DMARC yerleşik, CLI yönetimi);
/// kuruluysa her zaman o kullanılır. MailEnable yalnızca YEDEK: Stalwart yokken ama MailEnable
/// kuruluyken devreye girer. İkisi aynı anda çalışmaz (kurulum sırasında çakışma engellenir).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MailServiceRouter(
    MailService stalwartMailService,
    MailEnableMailService mailEnableMailService) : IMailService
{
    private IMailService Active => MailService.IsInstalled()
        ? stalwartMailService
        : MailEnableMailService.IsInstalled()
            ? mailEnableMailService
            : stalwartMailService;

    public Task<MailDnsDto> GetDnsRecordsAsync(string domain, CancellationToken cancellationToken = default)
        => Active.GetDnsRecordsAsync(domain, cancellationToken);

    public Task<MailDiagnosticsDto> GetDiagnosticsAsync(string domain, CancellationToken cancellationToken = default)
        => Active.GetDiagnosticsAsync(domain, cancellationToken);

    public Task<MailCertificateDto> GetCertificateStatusAsync(CancellationToken cancellationToken = default)
        => Active.GetCertificateStatusAsync(cancellationToken);

    public Task<MailCertificateDto> IssueCertificateAsync(IssueMailCertificateRequest request, CancellationToken cancellationToken = default)
        => Active.IssueCertificateAsync(request, cancellationToken);

    public Task<MailCertificateDto?> SyncCertificateAsync(CancellationToken cancellationToken = default)
        => Active.SyncCertificateAsync(cancellationToken);

    public Task<bool> EnsureServerHostnameAsync(bool applyChanges = true, CancellationToken cancellationToken = default)
        => Active.EnsureServerHostnameAsync(applyChanges, cancellationToken);

    public Task<MailWorkspaceDto> GetWorkspaceAsync(string? domain, CancellationToken cancellationToken = default)
        => Active.GetWorkspaceAsync(domain, cancellationToken);

    public Task<MailWorkspaceDto> ConfigureAsync(ConfigureMailRequest request, CancellationToken cancellationToken = default)
        => Active.ConfigureAsync(request, cancellationToken);

    public Task<MailWorkspaceDto> CreateDomainAsync(CreateMailDomainRequest request, CancellationToken cancellationToken = default)
        => Active.CreateDomainAsync(request, cancellationToken);

    public Task<MailWorkspaceDto> DeleteDomainAsync(string domainName, CancellationToken cancellationToken = default)
        => Active.DeleteDomainAsync(domainName, cancellationToken);

    public Task<MailWorkspaceDto> CreateAccountAsync(CreateMailAccountRequest request, CancellationToken cancellationToken = default)
        => Active.CreateAccountAsync(request, cancellationToken);

    public Task<MailWorkspaceDto> DeleteAccountAsync(string address, CancellationToken cancellationToken = default)
        => Active.DeleteAccountAsync(address, cancellationToken);

    public Task<MailWorkspaceDto> SetAccountPasswordAsync(SetMailAccountPasswordRequest request, CancellationToken cancellationToken = default)
        => Active.SetAccountPasswordAsync(request, cancellationToken);

    public Task<MailWorkspaceDto> CreateAliasAsync(CreateMailAliasRequest request, CancellationToken cancellationToken = default)
        => Active.CreateAliasAsync(request, cancellationToken);

    public Task<MailWorkspaceDto> DeleteAliasAsync(string address, CancellationToken cancellationToken = default)
        => Active.DeleteAliasAsync(address, cancellationToken);

    public Task<MailWorkspaceDto> CreateMailListAsync(CreateMailListRequest request, CancellationToken cancellationToken = default)
        => Active.CreateMailListAsync(request, cancellationToken);

    public Task<MailWorkspaceDto> SetMailListRecipientsAsync(SetMailListRecipientsRequest request, CancellationToken cancellationToken = default)
        => Active.SetMailListRecipientsAsync(request, cancellationToken);

    public Task<MailWorkspaceDto> DeleteMailListAsync(string address, CancellationToken cancellationToken = default)
        => Active.DeleteMailListAsync(address, cancellationToken);

    public Task<IReadOnlyCollection<MailListenerDto>> GetListenersAsync(CancellationToken cancellationToken = default)
        => Active.GetListenersAsync(cancellationToken);

    public Task<MailWorkspaceDto> SetDomainQuotaAsync(SetMailDomainQuotaRequest request, CancellationToken cancellationToken = default)
        => Active.SetDomainQuotaAsync(request, cancellationToken);

    public Task<MailOutboundLimitDto> GetOutboundLimitAsync(string domain, CancellationToken cancellationToken = default)
        => Active.GetOutboundLimitAsync(domain, cancellationToken);

    public Task<MailOutboundLimitDto> SetOutboundLimitAsync(string domain, int messagesPerHour, CancellationToken cancellationToken = default)
        => Active.SetOutboundLimitAsync(domain, messagesPerHour, cancellationToken);

    public Task<MailWorkspaceDto> SetAccountQuotaAsync(SetMailAccountQuotaRequest request, CancellationToken cancellationToken = default)
        => Active.SetAccountQuotaAsync(request, cancellationToken);

    public Task<MailAutoResponderDto> GetAutoResponderAsync(string address, CancellationToken cancellationToken = default)
        => Active.GetAutoResponderAsync(address, cancellationToken);

    public Task<MailAutoResponderDto> SetAutoResponderAsync(SetMailAutoResponderRequest request, CancellationToken cancellationToken = default)
        => Active.SetAutoResponderAsync(request, cancellationToken);

    public Task<MailSpamSettingsDto> GetSpamSettingsAsync(CancellationToken cancellationToken = default)
        => Active.GetSpamSettingsAsync(cancellationToken);

    public Task<MailSpamSettingsDto> UpdateSpamSettingsAsync(UpdateMailSpamSettingsRequest request, CancellationToken cancellationToken = default)
        => Active.UpdateSpamSettingsAsync(request, cancellationToken);

    public Task<MailQueueDto> GetQueueAsync(int limit, CancellationToken cancellationToken = default)
        => Active.GetQueueAsync(limit, cancellationToken);

    public Task<MailQueueMessageDto?> GetQueueMessageAsync(string id, CancellationToken cancellationToken = default)
        => Active.GetQueueMessageAsync(id, cancellationToken);

    public Task<bool> DeleteQueueMessageAsync(string id, CancellationToken cancellationToken = default)
        => Active.DeleteQueueMessageAsync(id, cancellationToken);

    public Task<MailRelayDto> GetRelayAsync(CancellationToken cancellationToken = default)
        => Active.GetRelayAsync(cancellationToken);

    public Task<MailRelayDto> SetRelayAsync(SetMailRelayRequest request, CancellationToken cancellationToken = default)
        => Active.SetRelayAsync(request, cancellationToken);

    public Task<MailClientSettingsDto?> GetClientSettingsAsync(string domain, CancellationToken cancellationToken = default)
        => Active.GetClientSettingsAsync(domain, cancellationToken);
}
