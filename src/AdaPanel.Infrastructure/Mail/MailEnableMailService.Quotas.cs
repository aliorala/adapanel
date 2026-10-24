using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;

namespace AdaPanel.Infrastructure.Mail;

public sealed partial class MailEnableMailService
{
    // This is the same allocation contract as the Stalwart adapter: limits the
    // sum of mailbox quotas created through AdaPanel, not native disk usage.
    private const string AllocationPath = "config/mailenable-domain-quotas.json";

    public async Task<MailWorkspaceDto> SetDomainQuotaAsync(SetMailDomainQuotaRequest request,
        CancellationToken cancellationToken = default)
    {
        var domain = ValidateDomain(request.Domain);
        ValidateQuota(request.MaxSizeMB);
        if (fileStore is null) throw new AppException("mail.configuration_unavailable", 503, 503);
        using var mutation = await AcquireMutationAsync(cancellationToken);
        var (workspace, measured) = await WorkspaceOkuAsync(domain, cancellationToken);
        MailQuotaMeasurement.EnsureMeasured(1, measured && workspace.AccountsMeasured,
            MailQuotaMeasurement.DomainAllocationKey, domain);
        if (!workspace.Domains.Any(d => d.Name.Equals(domain, StringComparison.OrdinalIgnoreCase)))
            throw new AppException("mail.invalid_domain", 404, 404);
        if (request.MaxSizeMB > 0)
        {
            if (workspace.Accounts.Any(a => a.MaxSizeMB <= 0))
                throw new AppException("mail.domain_quota_needs_mailbox_limit", 409, 409);
            if (workspace.Accounts.Sum(a => (long)a.MaxSizeMB) > request.MaxSizeMB)
                throw new AppException("mail.domain_quota_below_allocated", 409, 409);
        }
        await fileStore.UpdateAsync(AllocationPath, new Dictionary<string, int>(), values =>
        {
            var updated = new Dictionary<string, int>(values, StringComparer.OrdinalIgnoreCase);
            if (request.MaxSizeMB == 0) updated.Remove(domain);
            else updated[domain] = request.MaxSizeMB;
            return updated;
        }, cancellationToken);
        // Re-read persisted policy before presenting success; keep the measured inventory.
        await ApplyDomainAllocationsAsync(workspace, cancellationToken);
        if (workspace.Domains.Single(d => d.Name.Equals(domain, StringComparison.OrdinalIgnoreCase)).MaxSizeMB != request.MaxSizeMB)
            throw new AppException("mail.configuration_unavailable", 502, 502);
        return workspace;
    }

    private async Task<Dictionary<string, int>> ReadDomainAllocationsAsync(CancellationToken cancellationToken)
    {
        if (fileStore is null) return new(StringComparer.OrdinalIgnoreCase);
        var values = await fileStore.ReadAsync(AllocationPath, new Dictionary<string, int>(), cancellationToken);
        if (values.Any(p => p.Value <= 0 || p.Value > 1024 * 1024))
            throw new AppException("mail.configuration_unavailable", 502, 502);
        return new(values, StringComparer.OrdinalIgnoreCase);
    }

    private async Task ApplyDomainAllocationsAsync(MailWorkspaceDto workspace, CancellationToken cancellationToken)
    {
        var limits = await ReadDomainAllocationsAsync(cancellationToken);
        foreach (var domain in workspace.Domains)
        {
            domain.MaxSizeMB = limits.GetValueOrDefault(domain.Name);
            if (workspace.AccountsMeasured && domain.Name.Equals(workspace.SelectedDomain, StringComparison.OrdinalIgnoreCase))
            {
                domain.AccountCount = workspace.Accounts.Count;
                domain.AllocatedMB = (int)Math.Min(int.MaxValue, workspace.Accounts.Sum(a => (long)Math.Max(0, a.MaxSizeMB)));
            }
        }
    }

    private async Task EnsureDomainAllocationAsync(string domain, string address, int requestedMb, CancellationToken cancellationToken)
    {
        var limits = await ReadDomainAllocationsAsync(cancellationToken);
        if (!limits.TryGetValue(domain, out var limit)) return;
        if (requestedMb <= 0) throw new AppException("mail.domain_quota_needs_mailbox_limit", 409, 409);
        var (workspace, measured) = await WorkspaceOkuAsync(domain, cancellationToken);
        MailQuotaMeasurement.EnsureMeasured(limit, measured && workspace.AccountsMeasured,
            MailQuotaMeasurement.DomainAllocationKey, domain);
        var others = workspace.Accounts.Where(a => !a.Address.Equals(address, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (others.Any(a => a.MaxSizeMB <= 0)) throw new AppException("mail.domain_quota_needs_mailbox_limit", 409, 409);
        if (others.Sum(a => (long)a.MaxSizeMB) + requestedMb > limit)
            throw new AppException("mail.domain_quota_exceeded", 409, 409);
    }
}
