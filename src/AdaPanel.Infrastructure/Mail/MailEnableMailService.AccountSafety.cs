namespace AdaPanel.Infrastructure.Mail;

public sealed partial class MailEnableMailService
{
    // This adapter uses domain == postoffice. Refuse shared/foreign mappings before
    // touching mailbox, login or routing records in the assumed postoffice.
    internal static string DomainOwnershipGuard(string domain) => $$"""
        $ErrorActionPreference = 'Stop'
        $ownedDomain = New-Object -ComObject 'MEAOSM.Domain'
        $ownedDomain.DomainName = '{{PsLiteralRaw(domain)}}'
        if ($ownedDomain.GetDomain() -ne 1 -or $ownedDomain.AccountName -ine '{{PsLiteralRaw(domain)}}') { throw 'SMTP domain ownership could not be verified.' }

        """ + "\n";
}
