namespace AdaPanel.Infrastructure.Mail;

public sealed partial class MailEnableMailService
{
    private static readonly SemaphoreSlim MutationGate = new(1, 1);

    private static async Task<IDisposable> AcquireMutationAsync(CancellationToken token)
    {
        await MutationGate.WaitAsync(token);
        return new MutationLease();
    }

    private sealed class MutationLease : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) MutationGate.Release();
        }
    }

    internal static string DeleteEmptyDomainScript(string name) => $$"""
        $ErrorActionPreference = 'Stop'
        # Deletion of a shared/nonempty postoffice is a separate migration operation.
        # Check all dependent objects before changing either SMTP or postoffice records.
        $domain = New-Object -ComObject 'MEAOSM.Domain'
        $domain.DomainName = '{{PsLiteralRaw(name)}}'
        $domain.AccountName = ''; $domain.Status = -1
        $domain.DomainRedirectionStatus = -1; $domain.DomainRedirectionHosts = ''
        $exists = $domain.GetDomain() -eq 1
        if ($exists -and $domain.AccountName -ine '{{PsLiteralRaw(name)}}') { throw 'Domain belongs to another postoffice.' }
        $domains = New-Object -ComObject 'MEAOSM.Domain'
        $domains.AccountName = '{{PsLiteralRaw(name)}}'; $domains.DomainName = ''; $domains.Status = -1
        $domains.DomainRedirectionHosts = ''; $domains.DomainRedirectionStatus = -1
        $seen = @{}
        if ($domains.FindFirstDomain() -eq 1) {
          do {
            if ($domains.DomainName -ine '{{PsLiteralRaw(name)}}' -or $seen.ContainsKey([string]$domains.DomainName)) { throw 'Postoffice is shared or its inventory is incomplete.' }
            $seen[[string]$domains.DomainName] = $true
            $domains.AccountName = '{{PsLiteralRaw(name)}}'; $domains.DomainName = ''; $domains.Status = -1
            $domains.DomainRedirectionHosts = ''; $domains.DomainRedirectionStatus = -1
          } while ($domains.FindNextDomain() -eq 1)
        }
        $mailbox = New-Object -ComObject 'MEAOPO.Mailbox'
        $mailbox.Postoffice = '{{PsLiteralRaw(name)}}'; $mailbox.Mailbox = ''; $mailbox.Status = -1
        $mailbox.Limit = -1; $mailbox.RedirectStatus = -1; $mailbox.RedirectAddress = ''
        if ($mailbox.FindFirstMailbox() -eq 1) { throw 'Postoffice contains mailboxes.' }
        $map = New-Object -ComObject 'MEAOAM.AddressMap'
        $map.Account = '{{PsLiteralRaw(name)}}'; $map.SourceAddress = ''; $map.DestinationAddress = ''; $map.Scope = ''
        if ($map.FindFirstAddressMap() -eq 1) { throw 'Postoffice contains address mappings.' }
        $list = New-Object -ComObject 'MEAOLS.List'
        $list.AccountName = '{{PsLiteralRaw(name)}}'; $list.ListName = ''
        if ($list.FindFirstList() -eq 1) { throw 'Postoffice contains mailing lists.' }
        $group = New-Object -ComObject 'MEAOPO.Group'
        $group.Postoffice = '{{PsLiteralRaw(name)}}'; $group.GroupName = ''; $group.RecipientAddress = ''
        if ($group.FindFirstGroup() -eq 1) { throw 'Postoffice contains groups.' }
        if ($exists -and $domain.RemoveDomain() -ne 1) { throw 'SMTP domain removal failed.' }
        $verify = New-Object -ComObject 'MEAOSM.Domain'
        $verify.DomainName = '{{PsLiteralRaw(name)}}'
        if ($verify.GetDomain() -eq 1) { throw 'SMTP domain removal readback failed.' }
        $po = New-Object -ComObject 'MEAOPO.PostOffice'
        $po.Name = '{{PsLiteralRaw(name)}}'; $po.Account = ''; $po.Status = -1
        if ($po.GetPostoffice() -eq 1 -and $po.RemovePostoffice() -ne 1) { throw 'Postoffice removal failed.' }
        'OK'
        """;

    internal static string AccountCreationPreflight(string domain, string local, string full) => $$"""
        $ErrorActionPreference = 'Stop'
        $domain = New-Object -ComObject 'MEAOSM.Domain'
        $domain.DomainName = '{{PsLiteralRaw(domain)}}'
        if ($domain.GetDomain() -ne 1 -or $domain.AccountName -ine '{{PsLiteralRaw(domain)}}') { throw 'SMTP domain ownership could not be verified.' }
        $mailbox = New-Object -ComObject 'MEAOPO.Mailbox'
        $mailbox.Postoffice = '{{PsLiteralRaw(domain)}}'
        $mailbox.Mailbox = '{{PsLiteralRaw(local)}}'
        if ($mailbox.GetMailbox() -eq 1) { throw 'Mailbox already exists.' }
        $map = New-Object -ComObject 'MEAOAM.AddressMap'
        $map.SourceAddress = '[SMTP:{{PsLiteralRaw(full)}}]'
        if ($map.GetAddressMap() -eq 1) { throw 'Address already exists.' }
        $login = New-Object -ComObject 'MEAOAU.Login'
        $login.Account = '{{PsLiteralRaw(domain)}}'; $login.UserName = '{{PsLiteralRaw(full)}}'
        $login.Rights = '{{SondaNobetcisi}}'
        if ($login.GetLogin() -eq 1 -or $login.Rights -ne '{{SondaNobetcisi}}') { throw 'Login already exists.' }
        'OK'
        """;

    // Run only after the mailbox and login have both been verified absent. Never remove
    // SMTP-to-SMTP forwarding records or maps owned by a different postoffice.
    internal static string RemoveMailboxMappingsScript(string domain, string local) => $$"""
        $ErrorActionPreference = 'Stop'
        $destination = '[SF:{{PsLiteralRaw(domain)}}/{{PsLiteralRaw(local)}}]'
        $maps = New-Object -ComObject 'MEAOAM.AddressMap'
        $maps.Account = ''; $maps.SourceAddress = ''; $maps.DestinationAddress = $destination; $maps.Scope = ''
        $sources = @(); $seen = @{}
        if ($maps.FindFirstAddressMap() -eq 1) {
          do {
            $source = [string]$maps.SourceAddress
            if ($seen.ContainsKey($source) -or $sources.Count -ge 20000) { throw 'Address enumeration was incomplete.' }
            $seen[$source] = $true
            if ($maps.DestinationAddress -ine $destination -or $source -notmatch '^\[SMTP:[^\r\n\[\]]+\]$') { throw 'Unexpected mailbox mapping.' }
            if ($maps.Account -and $maps.Account -ine '{{PsLiteralRaw(domain)}}') { throw 'Mailbox mapping belongs to another postoffice.' }
            $sources += $source
            $maps.Account = ''; $maps.SourceAddress = ''; $maps.DestinationAddress = $destination; $maps.Scope = ''
          } while ($maps.FindNextAddressMap() -eq 1)
        }
        foreach ($source in $sources) {
          $map = New-Object -ComObject 'MEAOAM.AddressMap'
          $map.SourceAddress = $source
          if ($map.GetAddressMap() -eq 1) {
            if ($map.DestinationAddress -ine $destination -or ($map.Account -and $map.Account -ine '{{PsLiteralRaw(domain)}}')) { throw 'Mailbox mapping changed during removal.' }
            if ($map.RemoveAddressMap() -ne 1) { throw 'Mailbox mapping removal failed.' }
          }
        }
        $verify = New-Object -ComObject 'MEAOAM.AddressMap'
        $verify.Account = ''; $verify.SourceAddress = ''; $verify.DestinationAddress = $destination; $verify.Scope = ''
        if ($verify.FindFirstAddressMap() -eq 1) { throw 'Mailbox mapping removal was not complete.' }
        'OK'
        """;
}
