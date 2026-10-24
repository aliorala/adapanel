using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>
/// MailEnable (Standard) e-posta yönetimi. MailEnable 32-bit yerel COM yönetim nesneleri
/// (MEAOPO.PostOffice, MEAOPO.Mailbox, MEAOAU.Login, MEAOAM.AddressMap) üzerinden, 32-bit
/// PowerShell ile çalışır. profesyonel panel benzeri: domain (postoffice), posta kutusu + login (parola),
/// alias (address map). Yönetici parolası gerekmez (yerel makine COM yönetimi).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class MailEnableMailService(
    ILogger<MailEnableMailService> logger,
    JsonFileStore? fileStore = null,
    MailService? certificateIssuerService = null) : IMailService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// <b>GOVDE OLCUM DIKISI.</b> Bu sinifta PowerShell'e cikan TEK nokta
    /// <see cref="RunComAsync"/>'dir; burasi degistirilebilir olunca mutasyon metotlarinin
    /// GOVDESI (yalniz karar fonksiyonlari degil) MailEnable/COM olmadan olculebilir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>NEDEN GEREKTI.</b> Sonda ve karar fonksiyonlari (<see cref="GirisSondasiniYorumla"/>,
    /// <see cref="GirisKapisiniAc"/> ...) sinamalarla kaplidiy di, ama GOVDENIN onlari GERCEKTEN
    /// CAGIRDIGI hicbir yerde olculmuyordu: biri <see cref="DeleteAccountAsync"/> icinden
    /// dogrulama cagrisini silse TEK BIR SINAMA BILE KIRMIZI OLMAZDI. Bu, depoda defalarca
    /// yakalanan "koruma yazildi ama kardes yol cagirmiyor" hata sinifinin ta kendisidir.
    /// </para>
    /// <para>
    /// Uretimde <c>null</c>'dir ve hicbir davranis degismez. Sinama, gercek
    /// <c>powershell.exe</c>'de GOVDENIN URETTIGI betigi kosturup ciktisini geri verebilir --
    /// yani dikis, olcumu SAHTELESTIRMEZ, yalnizca COM'un yerine stand-in koyar.
    /// </para>
    /// </remarks>
    internal Func<string, CancellationToken, Task<string>>? KomutKosucusu { get; init; }

    private static string Ps32Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "SysWOW64", "WindowsPowerShell", "v1.0", "powershell.exe");

    // A registered SMTP service and its installation files must agree; leftover files
    // alone must not select a provider after MailEnable has been uninstalled.
    public static bool IsInstalled() => MailEnableInstallation.DiscoverRoot() is not null;

    public async Task<MailWorkspaceDto> GetWorkspaceAsync(string? domain, CancellationToken cancellationToken = default)
    {
        var (workspace, _) = await WorkspaceOkuAsync(domain, cancellationToken);
        if (workspace.Configured && !string.IsNullOrWhiteSpace(workspace.SelectedDomain))
        {
            workspace.Lists = await ReadNativeListsAsync(workspace.SelectedDomain, cancellationToken);
            workspace.ListsMeasured = true;
        }
        return workspace;
    }

    /// <summary>
    /// Calisma alanini okur ve OKUNABILDIGINI de soyler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>NEDEN IKI DEGER.</b> <see cref="GetWorkspaceAsync"/> COM'a erisemedigi ya da ciktiyi
    /// ayristiramadigi durumda BOS listelerle donuyordu -- yani "soramadim" ile "hic yok" ayni
    /// goruntuye dusuyordu. Mutasyon metotlari (olustur/sil) artik sonucu bu okumayla
    /// DOGRULADIGI icin fark hayati: olculemeyen bir sonuc "basarili" da "basarisiz" da
    /// SAYILMAZ.
    /// </para>
    /// </remarks>
    private async Task<(MailWorkspaceDto Workspace, bool Olculdu)> WorkspaceOkuAsync(
        string? domain,
        CancellationToken cancellationToken)
    {
        var olculdu = true;
        var selected = string.IsNullOrWhiteSpace(domain) ? string.Empty : ValidateDomain(domain);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $domains = @()
            $po = New-Object -ComObject 'MEAOPO.PostOffice'
            $seen = @{}
            $po.Name = ''; $po.Account = ''; $po.Status = -1
            if ($po.FindFirstPostoffice() -eq 1) {
              $n = 0
              do {
                $nm = "$($po.Name)"
                # Bazı MailEnable COM sürümlerinde FindNext sonu doğru bildirmez (sonsuz
                # döngü/timeout). Tekrar eden ad gelirse enumerasyonu sonlandır.
                if ($seen.ContainsKey($nm)) { throw 'Postoffice enumeration repeated a record.' }
                $seen[$nm] = $true
                $domains += [pscustomobject]@{ name = $nm; status = [int]$po.Status }
                $n++
                $po.Name = ''; $po.Account = ''; $po.Status = -1
              } while ($po.FindNextPostoffice() -eq 1 -and $n -lt 500)
              if ($n -ge 500) { throw 'Postoffice enumeration exceeded safety limit.' }
            }
            $accounts = @(); $aliases = @()
            $sel = '{{PsLiteralRaw(selected)}}'
            if ($sel) {
              $mb = New-Object -ComObject 'MEAOPO.Mailbox'
              $mb.Postoffice = $sel
              $mb.Mailbox = ''; $mb.Status = -1; $mb.Limit = -1; $mb.RedirectStatus = -1; $mb.RedirectAddress = ''
              if ($mb.FindFirstMailbox() -eq 1) {
                $n = 0; $seenMb = @{}
                do {
                  $mbn = "$($mb.Mailbox)"
                  if ($seenMb.ContainsKey($mbn)) { throw 'Mailbox enumeration repeated a record.' }
                  $seenMb[$mbn] = $true
                  $accounts += [pscustomobject]@{ mailbox = $mbn; status = [int]$mb.Status; limit = [int]$mb.Limit }
                  $n++
                  $mb.Postoffice = $sel
                  $mb.Mailbox = ''; $mb.Status = -1; $mb.Limit = -1; $mb.RedirectStatus = -1; $mb.RedirectAddress = ''
                } while ($mb.FindNextMailbox() -eq 1 -and $n -lt 5000)
                if ($n -ge 5000) { throw 'Mailbox enumeration exceeded safety limit.' }
              }
              $am = New-Object -ComObject 'MEAOAM.AddressMap'
              $am.Account = ''; $am.SourceAddress = ''; $am.DestinationAddress = ''; $am.Scope = ''
              if ($am.FindFirstAddressMap() -eq 1) {
                $n = 0; $seenAm = @{}
                do {
                  $src = "$($am.SourceAddress)"; $dst = "$($am.DestinationAddress)"
                  $key = "$src=>$dst"
                  if ($seenAm.ContainsKey($key)) { throw 'Address enumeration repeated a record.' }
                  $seenAm[$key] = $true
                  $isCatchAll = $src -ieq "[SMTP:$sel]" -or $src -ieq "[SMTP:*@$sel]"
                  if (($src.EndsWith("@$sel]", [StringComparison]::OrdinalIgnoreCase) -or $isCatchAll) -and $dst -match '^\[SMTP:') {
                    $aliases += [pscustomobject]@{ source = $src; destination = $dst }
                  }
                  $n++
                  $am.Account = ''; $am.SourceAddress = ''; $am.DestinationAddress = ''; $am.Scope = ''
                } while ($am.FindNextAddressMap() -eq 1 -and $n -lt 20000)
                if ($n -ge 20000) { throw 'Address enumeration exceeded safety limit.' }
              }
            }
            [pscustomobject]@{ domains = $domains; accounts = $accounts; aliases = $aliases } | ConvertTo-Json -Depth 5 -Compress
            """;

        // COM erişilemezse (MEAOPO kayıtlı değil / MailEnable yönetim bileşeni eksik) Refresh'i
        // hard 500 ile patlatma: ekran yüklensin, gerçek sebebi StatusMessage'da göster.
        string json;
        try
        {
            json = await RunComAsync(script, cancellationToken);
        }
        catch (AppException exception)
        {
            logger.LogWarning(exception, "MailEnable workspace okunamadı; zarif degradasyon.");
            return (new MailWorkspaceDto
            {
                Installed = true,
                Configured = false,
                SelectedDomain = selected,
                StatusMessage = "Mail yönetim bileşenine (MailEnable COM) erişilemedi. MailEnable kurulu ve COM kayıtlı olmalı. Ayrıntı: "
                    + (string.IsNullOrWhiteSpace(exception.Details) ? exception.Message : exception.Details)
            }, false);
        }

        var data = ParseJson(json);

        var workspace = new MailWorkspaceDto
        {
            Installed = true,
            Configured = true,
            Provider = "mailenable",
            ListsMeasured = false,
            SelectedDomain = selected
        };

        if (data is null)
        {
            workspace.Configured = false;
            // SORGU HATASI ile BOS SONUC AYNI GORUNTUYE DUSUYORDU. Ayristirilamayan bir cikti
            // "hic posta kutusu yok" DEGILDIR; bunu soylemek, kotayi dolduran bir kiraciya
            // sinirsiz kutu actiran hata sinifidir (bkz. ef2fda8).
            workspace.StatusMessage = "Mail yonetim bileseninin ciktisi AYRISTIRILAMADI: liste OLCULEMEDI (bos degil).";
            olculdu = false;
            return (workspace, olculdu);
        }

        var root = data.Value;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("domains", out var domainsJson) || domainsJson.ValueKind != JsonValueKind.Array ||
            (!string.IsNullOrWhiteSpace(selected) &&
             (!root.TryGetProperty("accounts", out var accountsJson) || accountsJson.ValueKind != JsonValueKind.Array ||
              !root.TryGetProperty("aliases", out var aliasesJson) || aliasesJson.ValueKind != JsonValueKind.Array)))
        {
            workspace.Configured = false;
            workspace.StatusMessage = "MailEnable inventory is incomplete; mailbox counts were not measured.";
            return (workspace, false);
        }
        workspace.Domains = ReadArray(root, "domains")
            .Select(item => new MailDomainDto
            {
                Name = GetStr(item, "name"),
                Active = GetInt(item, "status") == 1
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .ToArray();

        if (!string.IsNullOrWhiteSpace(selected))
        {
            workspace.AccountsMeasured = true;
            workspace.Accounts = ReadArray(root, "accounts")
                .Select(item => new MailAccountDto
                {
                    Address = $"{GetStr(item, "mailbox")}@{selected}",
                    Active = GetInt(item, "status") == 1,
                    MaxSizeMB = NormalizeLimitToMb(GetInt(item, "limit"))
                })
                .Where(item => item.Address.StartsWith("@", StringComparison.Ordinal) == false &&
                               !string.IsNullOrWhiteSpace(GetMailboxLocal(item.Address)))
                .ToArray();

            workspace.Aliases = ReadArray(root, "aliases")
                .Select(item =>
                {
                    var src = ExtractSmtp(GetStr(item, "source"));
                    // Çıplak alan adı = catch-all; UI'da "*@domain" olarak göster.
                    var address = string.Equals(src, selected, StringComparison.OrdinalIgnoreCase)
                        ? $"*@{selected}"
                        : src;
                    return new MailAliasDto
                    {
                        Address = address,
                        Target = ExtractSmtp(GetStr(item, "destination")),
                        Active = true
                    };
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Address) && !string.IsNullOrWhiteSpace(item.Target))
                .ToArray();
        }

        await ApplyDomainAllocationsAsync(workspace, cancellationToken);
        return (workspace, olculdu);
    }

    // MailEnable yönetici parolası gerektirmez; "yapılandırıldı" olarak döner.
    public Task<MailWorkspaceDto> ConfigureAsync(ConfigureMailRequest request, CancellationToken cancellationToken = default)
        => GetWorkspaceAsync(null, cancellationToken);

    public async Task<MailWorkspaceDto> CreateDomainAsync(CreateMailDomainRequest request, CancellationToken cancellationToken = default)
    {
        using var mutation = await AcquireMutationAsync(cancellationToken);
        var domain = ValidateDomain(request.DomainName);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $existingDomain = New-Object -ComObject 'MEAOSM.Domain'
            $existingDomain.DomainName = '{{PsLiteralRaw(domain)}}'
            if ($existingDomain.GetDomain() -eq 1 -and $existingDomain.AccountName -ine '{{PsLiteralRaw(domain)}}') { throw 'Domain belongs to another postoffice.' }
            $po = New-Object -ComObject 'MEAOPO.PostOffice'
            $po.Account = '{{PsLiteralRaw(domain)}}'
            $po.Name = '{{PsLiteralRaw(domain)}}'
            $po.Status = -1
            if ($po.GetPostoffice() -ne 1) {
              $po.Status = 1
              if ($po.AddPostoffice() -ne 1) { throw 'Postoffice creation failed.' }
            }
            $smtpDomain = New-Object -ComObject 'MEAOSM.Domain'
            $smtpDomain.DomainName = '{{PsLiteralRaw(domain)}}'
            if ($smtpDomain.GetDomain() -eq 1) {
              if ($smtpDomain.AccountName -ine '{{PsLiteralRaw(domain)}}') { throw 'Domain belongs to another postoffice.' }
            } else {
              $smtpDomain.AccountName = '{{PsLiteralRaw(domain)}}'
              $smtpDomain.Status = 1
              $smtpDomain.DomainRedirectionStatus = 0
              $smtpDomain.DomainRedirectionHosts = ''
              if ($smtpDomain.AddDomain() -ne 1) { throw 'SMTP domain creation failed.' }
            }
            $smtpDomain = New-Object -ComObject 'MEAOSM.Domain'
            $smtpDomain.DomainName = '{{PsLiteralRaw(domain)}}'
            if ($smtpDomain.GetDomain() -ne 1 -or $smtpDomain.AccountName -ine '{{PsLiteralRaw(domain)}}') { throw 'SMTP domain readback failed.' }
            'OK'
            """;
        await RunComAsync(script, cancellationToken);

        // GERI OKUMA: alan adi GERCEKTEN listede mi? `AddPostoffice`'in tamsayi durum kodu
        // `[void]` ile atiliyordu; tek kanit budur.
        var (workspace, olculdu) = await WorkspaceOkuAsync(domain, cancellationToken);
        KapiAcVarlik(
            olculdu,
            workspace.Domains.Any(item => string.Equals(item.Name, domain, StringComparison.OrdinalIgnoreCase)),
            olmali: true,
            "mail.domain_create_unverified",
            "mail.domain_create_not_applied",
            domain);
        return workspace;
    }

    public async Task<MailWorkspaceDto> DeleteDomainAsync(string domainName, CancellationToken cancellationToken = default)
    {
        using var mutation = await AcquireMutationAsync(cancellationToken);
        var domain = ValidateDomain(domainName);
        var (before, measured) = await WorkspaceOkuAsync(domain, cancellationToken);
        if (!measured) throw new AppException("mail.domain_delete_unverified", 502, 502);
        if (before.Accounts.Count != 0 || before.Aliases.Count != 0)
            throw new AppException("mail.domain_not_empty", 409, 409);
        await RunVerifiedCommandAsync(DeleteEmptyDomainScript(domain), cancellationToken);

        var (workspace, olculdu) = await WorkspaceOkuAsync(null, cancellationToken);
        KapiAcVarlik(
            olculdu,
            workspace.Domains.Any(item => string.Equals(item.Name, domain, StringComparison.OrdinalIgnoreCase)),
            olmali: false,
            "mail.domain_delete_unverified",
            "mail.domain_delete_not_applied",
            domain);
        return workspace;
    }

    public async Task<MailWorkspaceDto> CreateAccountAsync(CreateMailAccountRequest request, CancellationToken cancellationToken = default)
    {
        using var mutation = await AcquireMutationAsync(cancellationToken);
        ValidateQuota(request.MaxSizeMB);
        var domain = ValidateDomain(request.Domain);
        var local = ValidateLocalAddress(request.Address, domain, false);
        var full = $"{local}@{domain}";

        await EnsureDomainAllocationAsync(domain, full, request.MaxSizeMB, cancellationToken);
        await RunVerifiedCommandAsync(AccountCreationPreflight(domain, local, full), cancellationToken);

        // SILMENIN KARDESI: burada da IKI nesne yazilir (MEAOPO.Mailbox + MEAOAU.Login) ama
        // eskiden yalniz KUTU dogrulaniyordu. `AddLogin()` sessizce basarisiz olursa (COM
        // metotlari istisna degil TAMSAYI durum kodu doner; donus `[void]` ile atiliyordu)
        // panel "hesap acildi" der, kutu listede GORUNUR -- ama musteri IMAP/SMTP'ye GIRIS
        // YAPAMAZ. Kutunun varligi giris kaydinin varligini KANITLAMAZ; ayri olculur.
        var json = await RunComAsync(OlusturmaBetigi(domain, local, full, request.Password), cancellationToken);
        var sonda = SondayiOku(json);
        var hatalar = OlusturmaHatalari(sonda);

        var (workspace, olculdu) = await WorkspaceOkuAsync(domain, cancellationToken);
        KapiAcVarlik(
            olculdu,
            HesapVarMi(workspace, full),
            olmali: true,
            "mail.account_create_unverified",
            "mail.account_create_not_applied",
            $"{full} (posta kutusu){hatalar}");

        OlusturmaGirisKapisiniAc(
            OlusturmaSondasiniYorumla(sonda.Ayristirildi, sonda.OnceDoldu, sonda.KontrolDoldu, sonda.SonraDoldu),
            full,
            hatalar);

        // A mailbox and login alone do not route incoming SMTP mail to the mailbox.
        await RunComAsync(MailboxMappingScript(domain, local, full, request.MaxSizeMB), cancellationToken);
        (workspace, olculdu) = await WorkspaceOkuAsync(domain, cancellationToken);
        if (!olculdu || !workspace.Accounts.Any(a => a.Address.Equals(full, StringComparison.OrdinalIgnoreCase)
            && a.MaxSizeMB == request.MaxSizeMB))
            throw new AppException("mail.account_quota_not_applied", 502, 502);
        return workspace;
    }

    internal static string MailboxMappingScript(string domain, string local, string address, int quotaMb)
    {
        ValidateQuota(quotaMb);
        var quota = quotaMb == 0 ? -1 : checked(quotaMb * 1024);
        return $$"""
            $ErrorActionPreference = 'Stop'
            $map = New-Object -ComObject 'MEAOAM.AddressMap'
            $map.SourceAddress = '[SMTP:{{PsLiteralRaw(address)}}]'
            $mapExists = $map.GetAddressMap() -eq 1
            if ($mapExists) {
              if ($map.DestinationAddress -ine '[SF:{{PsLiteralRaw(domain)}}/{{PsLiteralRaw(local)}}]') { throw 'Address is assigned to another destination.' }
            }
            $mb = New-Object -ComObject 'MEAOPO.Mailbox'
            $mb.Postoffice = '{{PsLiteralRaw(domain)}}'
            $mb.Mailbox = '{{PsLiteralRaw(local)}}'
            if ($mb.GetMailbox() -ne 1) { throw 'Mailbox readback failed.' }
            $mb.Limit = {{quota}}
            if ($mb.EditMailbox($mb.Postoffice, $mb.Mailbox, $mb.RedirectAddress, $mb.RedirectStatus, $mb.Status, $mb.Limit, $mb.Size) -ne 1) { throw 'Mailbox quota was not applied.' }
            if (-not $mapExists) {
              $map.Account = '{{PsLiteralRaw(domain)}}'
              $map.SourceAddress = '[SMTP:{{PsLiteralRaw(address)}}]'
              $map.DestinationAddress = '[SF:{{PsLiteralRaw(domain)}}/{{PsLiteralRaw(local)}}]'
              $map.Scope = '0'
              if ($map.AddAddressMap() -ne 1) { throw 'Mailbox address mapping failed.' }
            }
            $verify = New-Object -ComObject 'MEAOAM.AddressMap'
            $verify.SourceAddress = '[SMTP:{{PsLiteralRaw(address)}}]'
            if ($verify.GetAddressMap() -ne 1 -or $verify.DestinationAddress -ine '[SF:{{PsLiteralRaw(domain)}}/{{PsLiteralRaw(local)}}]') { throw 'Mailbox address mapping readback failed.' }
            'OK'
            """;
    }

    public async Task<MailWorkspaceDto> DeleteAccountAsync(string address, CancellationToken cancellationToken = default)
    {
        using var mutation = await AcquireMutationAsync(cancellationToken);
        var (local, domain) = SplitAddress(address);
        var full = $"{local}@{domain}";
        var json = await RunComAsync(DomainOwnershipGuard(domain) + SilmeBetigi(domain, local, full), cancellationToken);
        var sonda = SondayiOku(json);

        // BU SINIFIN EN AGIR NOKTASIYDI: iki BOS catch, sonra kosulsuz 'OK'. Kutu silinmeyip
        // giris de duruyor olabilirdi ve panel HTTP 200 donerdi. Kutunun gittigi bir onceki
        // turda olculdu; GIRIS KAYDI o turda OLCULMUYORDU ve tam da o, silinen musterinin
        // POSTAYA ERISIMININ SURMESI demekti (kutu yok, giris var -> IMAP/SMTP oturumu hala
        // acilabilir).
        var (workspace, olculdu) = await WorkspaceOkuAsync(domain, cancellationToken);
        KapiAcVarlik(
            olculdu,
            HesapVarMi(workspace, full),
            olmali: false,
            "mail.account_delete_unverified",
            "mail.account_delete_not_applied",
            $"{full} (posta kutusu){SilmeHatalari(sonda)}");

        GirisKapisiniAc(
            GirisSondasiniYorumla(sonda.Ayristirildi, sonda.OnceDoldu, sonda.KontrolDoldu, sonda.SonraDoldu),
            full,
            SilmeHatalari(sonda));

        await RunVerifiedCommandAsync(RemoveMailboxMappingsScript(domain, local), cancellationToken);
        return workspace;
    }

    public async Task<MailWorkspaceDto> SetAccountPasswordAsync(SetMailAccountPasswordRequest request, CancellationToken cancellationToken = default)
    {
        using var mutation = await AcquireMutationAsync(cancellationToken);
        var (local, domain) = SplitAddress(request.Address);
        var full = $"{local}@{domain}";
        var json = await RunComAsync(DomainOwnershipGuard(domain) + ParolaBetigi(domain, full, request.Password), cancellationToken);
        var sonda = SondayiOku(json);
        var kayit = GirisSondasiniYorumla(sonda.Ayristirildi, sonda.OnceDoldu, sonda.KontrolDoldu, sonda.SonraDoldu);
        var parola = ParolaSonucunuYorumla(kayit, sonda.Eslesme);
        ParolaKapisiniAc(parola, full);

        var (workspace, _) = await WorkspaceOkuAsync(domain, cancellationToken);
        ParolaNotunuYaz(workspace, parola);
        return workspace;
    }

    public async Task<MailWorkspaceDto> CreateAliasAsync(CreateMailAliasRequest request, CancellationToken cancellationToken = default)
    {
        using var mutation = await AcquireMutationAsync(cancellationToken);
        var domain = ValidateDomain(request.Domain);
        var aliasLocal = ValidateLocalAddress(request.Address, domain, true);
        var aliasAddress = $"{aliasLocal}@{domain}";
        var (targetLocal, targetDomain) = SplitAddress(request.Target);
        var target = $"{targetLocal}@{targetDomain}";
        if (aliasAddress.Equals(target, StringComparison.OrdinalIgnoreCase))
            throw new AppException("mail.alias_self_reference", 400, 400);
        // "*" yerel kısmı = catch-all (yakala-tümü). Kaynak [SMTP:*@domain] biçimindedir:
        // domaine gelip eşleşmeyen TÜM adresler hedefe yönlenir (panel "catch-all" gibi).
        var isCatchAll = aliasLocal is "*" or "@" or "";
        var source = isCatchAll ? $"*@{domain}" : aliasAddress;
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $am = New-Object -ComObject 'MEAOAM.AddressMap'
            $am.SourceAddress = '[SMTP:{{PsLiteralRaw(source)}}]'
            if ($am.GetAddressMap() -eq 1) { throw 'Address already exists.' }
            $am.Account = '{{PsLiteralRaw(domain)}}'
            $am.DestinationAddress = '[SMTP:{{PsLiteralRaw(target)}}]'
            $am.Scope = 0
            if ($am.AddAddressMap() -ne 1) { throw 'Alias creation failed.' }
            'OK'
            """;
        await RunComAsync(DomainOwnershipGuard(domain) + script, cancellationToken);

        var (workspace, olculdu) = await WorkspaceOkuAsync(domain, cancellationToken);
        var beklenenAdres = isCatchAll ? $"*@{domain}" : aliasAddress;
        KapiAcVarlik(
            olculdu,
            workspace.Aliases.Any(item => string.Equals(item.Address, beklenenAdres, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Target, target, StringComparison.OrdinalIgnoreCase)),
            olmali: true,
            "mail.alias_create_unverified",
            "mail.alias_create_not_applied",
            beklenenAdres);
        return workspace;
    }

    public async Task<MailWorkspaceDto> DeleteAliasAsync(string address, CancellationToken cancellationToken = default)
    {
        using var mutation = await AcquireMutationAsync(cancellationToken);
        var (local, domain) = SplitAddress(address, allowCatchAll: true);
        // Remove the documented wildcard map and any legacy bare-domain alias.
        var catchAll = local is "*" or "@" or "";
        var clean = catchAll ? $"*@{domain}" : ExtractSmtp(address);
        var script = DeleteAliasScript(clean, domain);
        await RunVerifiedCommandAsync(DomainOwnershipGuard(domain) + script, cancellationToken);
        if (catchAll) await RunVerifiedCommandAsync(DomainOwnershipGuard(domain) + DeleteAliasScript(domain, domain), cancellationToken);

        // Read back the alias inventory after the command-level ownership check.
        var (workspace, olculdu) = await WorkspaceOkuAsync(domain, cancellationToken);
        var silinenAdres = string.Equals(clean, domain, StringComparison.OrdinalIgnoreCase)
            ? $"*@{domain}"
            : clean;
        KapiAcVarlik(
            olculdu,
            workspace.Aliases.Any(item => string.Equals(item.Address, silinenAdres, StringComparison.OrdinalIgnoreCase)),
            olmali: false,
            "mail.alias_delete_unverified",
            "mail.alias_delete_not_applied",
            silinenAdres);
        return workspace;
    }

    public Task<MailListenerSnapshotDto> GetListenersAsync(CancellationToken cancellationToken = default)
        => ReadListenersAsync(cancellationToken);

    public Task<MailQueueDto> GetQueueAsync(int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new MailEnableQueue(QueueRoot).Read(limit));
    }

    public Task<MailQueueMessageDto?> GetQueueMessageAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new MailEnableQueue(QueueRoot).ReadOne(id));
    }

    public Task<bool> DeleteQueueMessageAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new MailEnableQueue(QueueRoot).Delete(id));
    }

    internal Func<string?> QueueRoot { get; init; } = MailEnableQueue.InstalledRoot;

    /// <summary>
    /// İstemci ayarları ölçülemiyor → null. Autodiscover ucu bu durumda 404 döner; uydurma
    /// port/ad yayınlamak, istemciyi çalışmayan bir yapılandırmaya kilitlerdi.
    /// </summary>
    public Task<MailClientSettingsDto?> GetClientSettingsAsync(string domain, CancellationToken cancellationToken = default)
        => ReadClientSettingsAsync(domain, cancellationToken);

    public async Task<MailWorkspaceDto> SetAccountQuotaAsync(SetMailAccountQuotaRequest request, CancellationToken cancellationToken = default)
    {
        using var mutation = await AcquireMutationAsync(cancellationToken);
        ValidateQuota(request.MaxSizeMB);
        var (local, domain) = SplitAddress(request.Address);
        await EnsureDomainAllocationAsync(domain, $"{local}@{domain}", request.MaxSizeMB, cancellationToken);
        // MailEnable Limit birimi KB'dir; 0/negatif = sınırsız.
        var limitKb = request.MaxSizeMB > 0 ? request.MaxSizeMB * 1024 : -1;
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $mb = New-Object -ComObject 'MEAOPO.Mailbox'
            $mb.Postoffice = '{{PsLiteralRaw(domain)}}'
            $mb.Mailbox = '{{PsLiteralRaw(local)}}'
            if ($mb.GetMailbox() -ne 1) { throw 'Mailbox could not be read.' }
            $mb.Limit = {{limitKb}}
            if ($mb.EditMailbox($mb.Postoffice, $mb.Mailbox, $mb.RedirectAddress, $mb.RedirectStatus, $mb.Status, $mb.Limit, $mb.Size) -ne 1) { throw 'Mailbox update failed.' }
            'OK'
            """;
        await RunComAsync(DomainOwnershipGuard(domain) + script, cancellationToken);

        // KOTA GERI OKUNUR. Kardes sinifta (MailService) bu kapi 0.4.x'te kapatilmisti;
        // MailEnable yedeginde acik kalmisti.
        var adres = $"{local}@{domain}";
        var (workspace, olculdu) = await WorkspaceOkuAsync(domain, cancellationToken);
        var beklenenMb = request.MaxSizeMB > 0 ? request.MaxSizeMB : 0;
        var hesap = workspace.Accounts.FirstOrDefault(item =>
            string.Equals(item.Address, adres, StringComparison.OrdinalIgnoreCase));
        KapiAcVarlik(
            olculdu,
            hesap is not null && hesap.MaxSizeMB == beklenenMb,
            olmali: true,
            "mail.mailbox_quota_unverified",
            "mail.mailbox_quota_not_applied",
            $"{adres}: istenen {beklenenMb} MB, olculen {(hesap is null ? "hesap yok" : hesap.MaxSizeMB + " MB")}");
        return workspace;
    }

    /// <summary>
    /// MailEnable'da giden posta limiti panel üzerinden kurulamıyor: durum "limitsiz" DEĞİL,
    /// "yapılandırılmadı" olarak bildiriliyor. İkisini karıştırmak, sınırsız sanılan bir sunucuda
    /// tek müşterinin IP'yi kara listeye düşürmesine yol açardı.
    /// </summary>
    public Task<MailOutboundLimitDto> GetOutboundLimitAsync(string domain, CancellationToken cancellationToken = default)
        => Task.FromResult(new MailOutboundLimitDto
        {
            Domain = (domain ?? string.Empty).Trim().ToLowerInvariant(),
            MessagesPerHour = 0,
            Configured = false
        });

    /// <summary>
    /// Desteklenmiyor — ve BAŞARILI DÖNMÜYOR. Sessizce "tamam" demek, yöneticinin limit koyduğunu
    /// sanmasına ve gerçekte hiçbir sınır olmamasına yol açardı.
    /// </summary>
    public Task<MailOutboundLimitDto> SetOutboundLimitAsync(string domain, int messagesPerHour, CancellationToken cancellationToken = default)
        => throw new AppException("mail.outbound_limit_not_supported", 400, 400);

    public async Task<MailAutoResponderDto> GetAutoResponderAsync(string address, CancellationToken cancellationToken = default)
    {
        var (local, domain) = SplitAddress(address);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $mb = New-Object -ComObject 'MEAOPO.Mailbox'
            $mb.Postoffice = '{{PsLiteralRaw(domain)}}'
            $mb.Mailbox = '{{PsLiteralRaw(local)}}'
            if ($mb.GetMailbox() -ne 1) { throw 'Mailbox could not be read.' }
            $status = [int]$mb.GetAutoResponderStatus()
            $contents = [string]$mb.GetAutoResponderContents()
            [pscustomobject]@{ enabled = $status; message = $contents } | ConvertTo-Json -Compress
            """;
        var json = await RunComAsync(script, cancellationToken);
        var data = ParseJson(json);
        if (data is null || data.Value.ValueKind != JsonValueKind.Object ||
            !data.Value.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.Number || !enabled.TryGetInt32(out var state) || state is not (0 or 1) ||
            !data.Value.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String)
        {
            // SORGU HATASI ile BOS SONUC AYNI GORUNTUYE DUSUYORDU: ayristirilamayan cikti
            // "otomatik yanit kapali, mesaj bos" olarak gosteriliyordu. Kullanici tatilde
            // oldugunu sanan bir kutuyu "kapali" gorup dogru sanirdi.
            throw new AppException(
                "mail.autoresponder_unverified",
                500,
                500,
                $"{local}@{domain}: otomatik yanit durumu OKUNAMADI (cikti ayristirilamadi).");
        }

        return new MailAutoResponderDto
        {
            Address = $"{local}@{domain}",
            Enabled = GetInt(data.Value, "enabled") == 1,
            Message = GetStr(data.Value, "message")
        };
    }

    public async Task<MailAutoResponderDto> SetAutoResponderAsync(SetMailAutoResponderRequest request, CancellationToken cancellationToken = default)
    {
        using var mutation = await AcquireMutationAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.Subject))
            throw new AppException("mail.autoresponder_subject_not_supported", 409, 409);
        var (local, domain) = SplitAddress(request.Address);
        var status = request.Enabled ? 1 : 0;
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $mb = New-Object -ComObject 'MEAOPO.Mailbox'
            $mb.Postoffice = '{{PsLiteralRaw(domain)}}'
            $mb.Mailbox = '{{PsLiteralRaw(local)}}'
            if ($mb.GetMailbox() -ne 1) { throw 'Mailbox could not be read.' }
            [void]$mb.SetAutoResponderContents('{{PsLiteralRaw(request.Message)}}')
            [void]$mb.SetAutoResponderStatus({{status}})
            'OK'
            """;
        await RunComAsync(DomainOwnershipGuard(domain) + script, cancellationToken);

        // GERI OKUMA ZATEN VARDI, SONUCUNA BAKILMIYORDU. Artik bakiliyor.
        var olculen = await GetAutoResponderAsync(request.Address, cancellationToken);
        if (olculen.Enabled != request.Enabled || !string.Equals(olculen.Message, request.Message, StringComparison.Ordinal))
        {
            throw new AppException(
                "mail.autoresponder_not_applied",
                500,
                500,
                $"{request.Address}: istenen {(request.Enabled ? "acik" : "kapali")}, olculen {(olculen.Enabled ? "acik" : "kapali")}.");
        }

        return olculen;
    }

    // --- DNS / sertifika / spam: MailEnable kendi yönetir; panelde temel/uyumlu yanıt döner ---

    public async Task<MailDiagnosticsDto> GetDiagnosticsAsync(string domain, CancellationToken cancellationToken = default)
        => await MailDiagnosticsRunner.RunAsync(await GetDnsRecordsAsync(domain, cancellationToken), cancellationToken);

    public Task<MailDnsDto> GetDnsRecordsAsync(string domain, CancellationToken cancellationToken = default)
    {
        var normalized = (domain ?? string.Empty).Trim().ToLowerInvariant();
        var mailHost = string.IsNullOrWhiteSpace(normalized) ? "mail" : $"mail.{normalized}";
        // Temel MX/SPF/DMARC; DKIM MailEnable yönetiminden (selector) gelir, iterasyon-2.
        var dto = new MailDnsDto
        {
            Domain = normalized,
            MailHost = mailHost,
            Records =
            [
                new MailDnsRecordDto { Type = "MX", Host = "@", Value = mailHost, Priority = 10, Purpose = "mx" },
                new MailDnsRecordDto { Type = "TXT", Host = "@", Value = "v=spf1 a mx ~all", Purpose = "spf" },
                new MailDnsRecordDto { Type = "TXT", Host = "_dmarc", Value = "v=DMARC1; p=none", Purpose = "dmarc" }
            ]
        };
        return Task.FromResult(dto);
    }

    public Task<MailCertificateDto> GetCertificateStatusAsync(CancellationToken cancellationToken = default)
        => InspectCertificateAsync(cancellationToken);

    /// <summary>MailEnable tarafinda karsilama adi panelden yonetilmiyor.</summary>
    public Task<bool> EnsureServerHostnameAsync(bool applyChanges = true, CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>
    /// Spam ayarlari MailEnable tarafinda panelden OKUNMUYOR: <c>Configured=false</c>.
    /// </summary>
    /// <remarks>
    /// ONCEKI HALI ucu de sabit <c>true</c> idi. "Yapilandirildi + acik" cumlesi hicbir olcume
    /// dayanmiyordu; spam filtresi hic kurulmamis bir sunucu panelde korumali gorunuyordu.
    /// </remarks>
    public Task<MailSpamSettingsDto> GetSpamSettingsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new MailSpamSettingsDto { Installed = true, Configured = false, Enabled = false });

    /// <summary>
    /// MailEnable yedeginde spam ayari panelden YAZILMIYOR -- ve panel artik bunu SOYLUYOR.
    /// </summary>
    /// <remarks>
    /// ONCEKI HALI istegi YANKILIYORDU: kullanici "spam filtresi acik" diye kaydediyor, panel
    /// istenen degeri geri gosteriyor, sunucuda hicbir sey degismiyordu.
    /// </remarks>
    public Task<MailSpamSettingsDto> UpdateSpamSettingsAsync(UpdateMailSpamSettingsRequest request, CancellationToken cancellationToken = default)
        => throw new AppException(
            "mail.spam_settings_not_supported",
            409,
            409,
            "MailEnable yedeginde spam ayarlari panelden yazilamiyor; MailEnable yonetim konsolunu kullanin.");


    // =====================================================================================
    // GIRIS KAYDI SONDASI -- KENDINI KALIBRE EDEN OLCUM (TEK KAYNAK)
    //
    // PROBLEM: MEAOAU.Login'in `GetLogin`/`RemoveLogin`/`EditLogin` DURUM KODU KONVANSIYONU bu
    // makinede OLCULEMEZ (MailEnable kurulu degil) ve YANLIS TAHMIN CANLIDA SAHTE HATA URETIR.
    // Ayrica giris kayitlari calisma alani okumasinda GORUNMEZ: MEAOAU.Login'in DOGRULANMIS
    // uye listesinde (CODEX_MEMORY, gercek sunucudan Get-Member ciktisi) FindFirst/FindNext
    // YOKTUR -- yani giris kayitlari LISTELENEMEZ. Kardes nesnelerin (PostOffice/Mailbox/
    // AddressMap) hepsinde FindFirst/FindNext var, Login'de yok; bu bir varsayim degil,
    // depodaki olcum kaydinin soyledigi sey.
    //
    // COZUM: konvansiyonu TAHMIN ETMEK yerine CALISMA ANINDA OLCMEK. Ayni sonda UC KEZ kosar:
    //   1) ONCE    -- islemden onceki kayit (POZITIF KONTROL: sonda VAR olani gorebiliyor mu?)
    //   2) SONRA   -- islemden sonraki kayit (asil soru)
    //   3) KONTROL -- var olmasi IMKANSIZ bir kullanici adi (NEGATIF KONTROL: sonda YOK olani
    //                 yok diye gorebiliyor mu?)
    // Sonda, YAZMADIGIMIZ bir alani (Rights) once bir NOBETCI DEGERLE doldurup `GetLogin`
    // cagirir ve nobetci degerin degisip degismedigine bakar. "Degisti" = kayit okundu.
    //
    // KARAR:
    //   * ONCE bos VEYA KONTROL dolu  -> sonda AYIRT EDEMIYOR -> OLCULEMEDI (ne basarili ne
    //     basarisiz). Sunucunun konvansiyonu ne olursa olsun bu dal SAHTE HATA uretmez, cunku
    //     iddia degil "olcemedim" der.
    //   * ONCE dolu ve KONTROL bos    -> sonda KALIBRE. Artik SONRA'nin dolu/bos olmasi
    //     dogrudan kaydin var/yok olmasidir.
    //
    // DIKKAT -- OLUSTURMADA KALIBRASYON BASKADIR: orada kayit islemden once ZATEN YOKTUR, yani
    // POZITIF KONTROL KURULAMAZ ve yukaridaki kural OLDUGU GIBI KOPYALANAMAZ. Olusturmanin
    // kurali ve neden "olusturulmadi" diyemedigimiz ayrica yazildi: OlusturmaSondasiniYorumla.
    // =====================================================================================

    /// <summary>
    /// COM nesnelerini URETEN yer. <b>OLCULEBILIRLIK KANCASI:</b> uretimde bu degiskenler
    /// tanimsizdir (script <c>-File</c> ile TAZE bir kapsamda kosar) ve gercek COM nesnesi
    /// olusur; sinama, betigi nokta-kaynak ederek KENDI nesnesini verebilir ve boylece
    /// betigin GERCEK metni GERCEK <c>powershell.exe</c>'de kosturulur. Kaynak taramasi degil.
    /// </summary>
    internal const string FabrikaBasligi = """
        if (-not $MeGirisFabrikasi) { $MeGirisFabrikasi = { New-Object -ComObject 'MEAOAU.Login' } }
        if (-not $MeKutuFabrikasi) { $MeKutuFabrikasi = { New-Object -ComObject 'MEAOPO.Mailbox' } }
        """;

    /// <summary>Sondanin YAZDIGI, MailEnable'in asla uretmeyecegi nobetci deger.</summary>
    internal const string SondaNobetcisi = "__adapanel_sonda_dokunulmadi__";

    /// <summary>Uc sondanin ve mutasyon hatalarinin TEK cikti bicimi.</summary>
    internal const string SondaCiktisi = """
        [pscustomobject]@{
          kutuHata = "$kutuHata"
          girisHata = "$girisHata"
          onceHata = "$onceHata"
          onceHaklar = "$onceHaklar"
          sonraHata = "$sonraHata"
          sonraHaklar = "$sonraHaklar"
          sonraEslesme = $sonraEslesme
          kontrolHata = "$kontrolHata"
          kontrolHaklar = "$kontrolHaklar"
        } | ConvertTo-Json -Compress
        """;

    /// <summary>
    /// Hesap silme betiginin TAM metni. Sinama bu metnin KENDISINI kosturur (kopyasini degil):
    /// betik degisirse sinama da onunla degisir, boylece "olcum kodu ile uretim kodu ayristi"
    /// hatasi olanaksizlasir.
    /// </summary>
    internal static string SilmeBetigi(string domain, string local, string full)
        => $$"""
            $ErrorActionPreference = 'Stop'
            {{FabrikaBasligi}}
            {{GirisSondasiBetigi("once", domain, full, null)}}
            $kutuHata = ''
            $mb = & $MeKutuFabrikasi
            $mb.Postoffice = '{{PsLiteralRaw(domain)}}'
            $mb.Mailbox = '{{PsLiteralRaw(local)}}'
            try { [void]$mb.RemoveMailbox() } catch { $kutuHata = "$($_.Exception.GetType().Name)" }
            $girisHata = ''
            $lg = & $MeGirisFabrikasi
            $lg.Account = '{{PsLiteralRaw(domain)}}'
            $lg.UserName = '{{PsLiteralRaw(full)}}'
            try { [void]$lg.RemoveLogin() } catch { $girisHata = "$($_.Exception.GetType().Name)" }
            {{GirisSondasiBetigi("sonra", domain, full, null)}}
            {{GirisSondasiBetigi("kontrol", domain, KontrolKullanicisi(domain), null)}}
            {{SondaCiktisi}}
            """;

    /// <summary>
    /// Hesap olusturma betiginin TAM metni; sonda parcalari SILMEDEKININ AYNISIDIR (tek kaynak).
    /// <b>PAROLA CIKTIYA YAZILMAZ:</b> sonda yalnizca <c>Rights</c> nobetcisini geri okur.
    /// </summary>
    /// <remarks>
    /// Eskiden hem <c>AddMailbox()</c> hem <c>AddLogin()</c> <c>[void]</c> ile cagriliyor, donus
    /// degeri atiliyordu. Artik ikisi de yakalanir ve sonuc GERI OKUNUR. <c>once</c> sondasi
    /// islemden ONCE kosar: burada POZITIF KONTROL degil, IKINCI BIR NEGATIF KONTROLDUR
    /// (bkz. <see cref="OlusturmaSondasiniYorumla"/>).
    /// </remarks>
    internal static string OlusturmaBetigi(string domain, string local, string full, string parola)
        => $$"""
            $ErrorActionPreference = 'Stop'
            {{FabrikaBasligi}}
            {{GirisSondasiBetigi("once", domain, full, null)}}
            $kutuHata = ''
            $mb = & $MeKutuFabrikasi
            $mb.Postoffice = '{{PsLiteralRaw(domain)}}'
            $mb.Mailbox = '{{PsLiteralRaw(local)}}'
            $mb.Status = 1
            $mb.Limit = -1
            $mb.RedirectAddress = ''
            $mb.RedirectStatus = 0
            try { [void]$mb.AddMailbox() } catch { $kutuHata = "$($_.Exception.GetType().Name)" }
            $girisHata = ''
            $lg = & $MeGirisFabrikasi
            $lg.Account = '{{PsLiteralRaw(domain)}}'
            $lg.UserName = '{{PsLiteralRaw(full)}}'
            $lg.Password = '{{PsLiteralRaw(parola)}}'
            $lg.Rights = 'USER'
            $lg.Status = 1
            try { [void]$lg.AddLogin() } catch { $girisHata = "$($_.Exception.GetType().Name)" }
            {{GirisSondasiBetigi("sonra", domain, full, null)}}
            {{GirisSondasiBetigi("kontrol", domain, KontrolKullanicisi(domain), null)}}
            {{SondaCiktisi}}
            """;

    /// <summary>
    /// Parola degistirme betiginin TAM metni. <b>PAROLA CIKTIYA YAZILMAZ:</b> esitlik
    /// PowerShell tarafinda olculur, disari yalnizca mantiksal deger cikar.
    /// </summary>
    internal static string ParolaBetigi(string domain, string full, string parola)
        => $$"""
            $ErrorActionPreference = 'Stop'
            {{FabrikaBasligi}}
            $kutuHata = ''
            $girisHata = ''
            {{GirisSondasiBetigi("once", domain, full, null)}}
            $lg = & $MeGirisFabrikasi
            $lg.Account = '{{PsLiteralRaw(domain)}}'
            $lg.UserName = '{{PsLiteralRaw(full)}}'
            [void]$lg.GetLogin()
            [void]$lg.EditLogin($lg.UserName, $lg.Status, '{{PsLiteralRaw(parola)}}', $lg.Account, $lg.Description, $lg.LoginAttempts, $lg.LastAttempt, $lg.LastSuccessfulLogin, $lg.Rights)
            {{GirisSondasiBetigi("sonra", domain, full, parola)}}
            {{GirisSondasiBetigi("kontrol", domain, KontrolKullanicisi(domain), null)}}
            {{SondaCiktisi}}
            """;

    /// <summary>Var olmasi IMKANSIZ kullanici adi (negatif kontrol). Yalnizca OKUNUR.</summary>
    internal static string KontrolKullanicisi(string domain)
        => $"adapanel-olmayan-{Guid.NewGuid():N}@{domain}";

    /// <summary>
    /// Tek bir giris kaydi sondasi. <paramref name="beklenenParola"/> verilirse ayrica
    /// parolanin GERI OKUNAN degerle esitligi olculur -- ama parolanin KENDISI ciktiya
    /// YAZILMAZ, yalnizca bir mantiksal deger doner (sir gunluge/StatusMessage'a dusmesin).
    /// </summary>
    internal static string GirisSondasiBetigi(string onek, string domain, string kullanici, string? beklenenParola)
    {
        var parolaOlcumu = beklenenParola is null
            ? string.Empty
            : $$"""

                if (-not ${{onek}}Hata) {
                  try { ${{onek}}Eslesme = ("$(${{onek}}Nesne.Password)" -ceq '{{PsLiteralRaw(beklenenParola)}}') } catch { ${{onek}}Hata = "parola:$($_.Exception.GetType().Name)" }
                }
                """;

        return $$"""
            ${{onek}}Hata = ''
            ${{onek}}Haklar = '{{SondaNobetcisi}}'
            ${{onek}}Eslesme = $false
            ${{onek}}Nesne = & $MeGirisFabrikasi
            try {
              ${{onek}}Nesne.Account = '{{PsLiteralRaw(domain)}}'
              ${{onek}}Nesne.UserName = '{{PsLiteralRaw(kullanici)}}'
              ${{onek}}Nesne.Rights = '{{SondaNobetcisi}}'
            } catch { ${{onek}}Hata = "atama:$($_.Exception.GetType().Name)" }
            if (-not ${{onek}}Hata) {
              try { [void]${{onek}}Nesne.GetLogin() } catch { ${{onek}}Hata = "getlogin:$($_.Exception.GetType().Name)" }
            }
            if (-not ${{onek}}Hata) {
              try { ${{onek}}Haklar = "$(${{onek}}Nesne.Rights)" } catch { ${{onek}}Hata = "oku:$($_.Exception.GetType().Name)" }
            }{{parolaOlcumu}}
            """;
    }

    /// <summary>Tek bir sonda kaydi OKUDU mu? (nobetci degistiyse okudu.)</summary>
    internal static bool SondaDoldu(string hata, string haklar)
        => string.IsNullOrEmpty(hata) && !string.Equals(haklar, SondaNobetcisi, StringComparison.Ordinal);

    internal enum GirisKaydiOlcumu
    {
        /// <summary>Sonda var/yok ayrimini yapamadi: ne "silindi" ne "duruyor" denebilir.</summary>
        Olculemedi,

        /// <summary>Kalibre sonda kaydi BULAMADI.</summary>
        Yok,

        /// <summary>Kalibre sonda kaydi BULDU.</summary>
        Var
    }

    internal enum ParolaOlcumu
    {
        Olculemedi,

        /// <summary>Kayit geri okundu ve parola ISTENEN degerle birebir esit.</summary>
        Dogrulandi,

        /// <summary>Kayit geri okundu ama saklanan deger istenenle esit degil (ozet/maskeli olabilir).</summary>
        GeriOkunamadi,

        /// <summary>Kalibre sonda, islemden SONRA kaydi bulamadi: parola yazilmadi.</summary>
        Uygulanmadi
    }

    /// <summary>UC DEGERLI karar: once/kontrol sondasi KALIBRASYONDUR, sonra sondasi CEVAPTIR.</summary>
    internal static GirisKaydiOlcumu GirisSondasiniYorumla(bool ayristirildi, bool onceDoldu, bool kontrolDoldu, bool sonraDoldu)
    {
        if (!ayristirildi || !onceDoldu || kontrolDoldu)
        {
            return GirisKaydiOlcumu.Olculemedi;
        }

        return sonraDoldu ? GirisKaydiOlcumu.Var : GirisKaydiOlcumu.Yok;
    }

    /// <summary>
    /// OLUSTURMANIN KARARI. <b>SILMEDEKI KURAL BURAYA KOPYALANAMAZ</b> -- kalibrasyon farklidir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Silmede sonda UC ayakli kalibre edilir: <c>once</c> POZITIF KONTROLDUR (kayit islemden
    /// once VARDIR; sonda onu goruyorsa "var olani gorebiliyor" KANITLANMIS olur), <c>kontrol</c>
    /// negatif kontroldur. Olusturmada <b>POZITIF KONTROL YOKTUR</b>: kayit islemden once zaten
    /// yoktur, yani sondanin VAR OLAN bir kaydi gorebildigini gosterecek hicbir ayak yoktur.
    /// Dolayisiyla asimetrik bir karar cikar:
    /// </para>
    /// <list type="bullet">
    /// <item><b>KONTROL DOLU</b> -> sonda var olmayan kaydi da dolduruyor (kor sonda);
    /// <c>sonra</c>'nin dolulugu hicbir sey kanitlamaz -> OLCULEMEDI.</item>
    /// <item><b>ONCE DOLU</b> -> kayit islemden ONCE de vardi (orn. yarim silinmis bir hesabin
    /// artik giris kaydi). <c>sonra</c>'nin dolu olmasi bizim <c>AddLogin</c>'imizin yazdigini
    /// GOSTERMEZ: kayit ESKI PAROLAYLA duruyor olabilir -> OLCULEMEDI.</item>
    /// <item><b>KONTROL BOS + SONRA DOLU</b> -> sonda ayni kosuda hem ayirt ediyor (imkansiz ad
    /// BOS) hem de kaydi goruyor (yeni ad DOLU) -> VAR. Tek olumlu dal budur.</item>
    /// <item><b>KONTROL BOS + SONRA BOS</b> -> <b>"OLUSTURULMADI" DENMEZ.</b> Kontrolun bos
    /// cikmasi sondanin FAZLADAN doldurmadigini gosterir; VAR OLANI gorebildigini KANITLAMAZ.
    /// Sonda bu sunucuda hep bos donuyor olabilir (orn. MailEnable <c>Rights</c>'i geri
    /// yazmiyordur). Bu dala "basarisiz" demek CALISAN bir kurulumda SAHTE HATA uretirdi
    /// -> OLCULEMEDI.</item>
    /// </list>
    /// <para>
    /// Sonuc: bu fonksiyon <see cref="GirisKaydiOlcumu.Yok"/> DONDURMEZ. Olusturmada "giris
    /// kaydi YOK" iddiasini destekleyecek bir olcum yoktur; en kotu durum "olcemedim"dir.
    /// </para>
    /// </remarks>
    internal static GirisKaydiOlcumu OlusturmaSondasiniYorumla(bool ayristirildi, bool onceDoldu, bool kontrolDoldu, bool sonraDoldu)
    {
        if (!ayristirildi || kontrolDoldu || onceDoldu)
        {
            return GirisKaydiOlcumu.Olculemedi;
        }

        return sonraDoldu ? GirisKaydiOlcumu.Var : GirisKaydiOlcumu.Olculemedi;
    }

    /// <summary>
    /// Parola sonucu. <b>ESLESMEME HATA DEGILDIR:</b> MailEnable parolayi ozetleyerek saklarsa
    /// geri okunan deger istenene esit olmaz; buna "uygulanmadi" demek SAHTE HATA olurdu.
    /// O yuzden yalnizca birebir esitlik DOGRULAMA sayilir, gerisi "dogrulanamadi".
    /// </summary>
    internal static ParolaOlcumu ParolaSonucunuYorumla(GirisKaydiOlcumu kayit, bool eslesme)
        => kayit switch
        {
            GirisKaydiOlcumu.Olculemedi => ParolaOlcumu.Olculemedi,
            GirisKaydiOlcumu.Yok => ParolaOlcumu.Uygulanmadi,
            _ => eslesme ? ParolaOlcumu.Dogrulandi : ParolaOlcumu.GeriOkunamadi
        };

    /// <summary>
    /// Kullaniciya gosterilecek cumle. DOGRULANDI ise BOS doner: <c>StatusMessage</c> panelde
    /// HATA seridi olarak cizilir (MailView.vue), dolayisiyla olculmus bir basariya mesaj
    /// yazmak sahte alarm olurdu.
    /// </summary>
    internal static string ParolaDurumCumlesi(ParolaOlcumu olcum)
        => olcum switch
        {
            ParolaOlcumu.Dogrulandi => string.Empty,
            ParolaOlcumu.GeriOkunamadi =>
                "Parola yazildi ve giris kaydi geri okundu, ama saklanan deger yazilanla ESLESMIYOR "
                + "(MailEnable parolayi duz metin dondurmuyor olabilir): sonuc DOGRULANMADI. Yeni parolayla bir kez oturum acarak sinayin.",
            _ =>
                "Parola yazildi ama sonuc DOGRULANAMADI: giris kaydi sondasi bu sunucuda var/yok ayrimini yapamadi. "
                + "Yeni parolayla bir kez oturum acarak sinayin."
        };

    /// <summary>
    /// GIRIS KAYDI KAPISI (TEK KAYNAK). Ucu de ayri cumledir: gitti / duruyor / olculemedi.
    /// </summary>
    /// <remarks>
    /// <b>ANAHTARLAR ELDE KURULMAZ</b> -- depodaki i18n kullanim kapisi yalnizca kaynakta
    /// HARFI HARFINE gecen anahtarlari gorur (bkz. <see cref="KapiAcVarlik"/> aciklamasi).
    /// </remarks>
    internal static void GirisKapisiniAc(GirisKaydiOlcumu olcum, string adres, string silmeHatalari)
    {
        if (olcum == GirisKaydiOlcumu.Var)
        {
            throw new AppException(
                "mail.account_delete_login_survived",
                500,
                500,
                $"{adres}: posta kutusu silindi AMA MEAOAU.Login kaydi SILINMEDI (sonda kalibre edildi ve kaydi HALA BULUYOR). "
                + $"Giris kaydi durdugu surece silinen hesap POSTAYA ERISEBILIR; MailEnable yonetiminden giris kaydini silin.{silmeHatalari}");
        }

        if (olcum == GirisKaydiOlcumu.Olculemedi)
        {
            throw new AppException(
                "mail.account_delete_login_unverified",
                500,
                500,
                $"{adres}: posta kutusunun gittigi olculdu, MEAOAU.Login kaydinin gittigi OLCULEMEDI "
                + $"(sonda bu sunucuda var/yok ayrimini yapamadi). Giris kaydini MailEnable yonetiminden dogrulayin.{silmeHatalari}");
        }
    }

    /// <summary>
    /// OLUSTURMA GIRIS KAYDI KAPISI. IKI cumle vardir, uc degil: olculdu-var / OLCULEMEDI.
    /// "Giris kaydi olusmadi" cumlesi YOKTUR, cunku onu destekleyen bir olcum yoktur
    /// (bkz. <see cref="OlusturmaSondasiniYorumla"/>).
    /// </summary>
    /// <remarks>
    /// <b>ANAHTARLAR ELDE KURULMAZ</b> -- i18n kullanim kapisi yalnizca kaynakta HARFI HARFINE
    /// gecen anahtarlari gorur.
    /// </remarks>
    internal static void OlusturmaGirisKapisiniAc(GirisKaydiOlcumu olcum, string adres, string olusturmaHatalari)
    {
        if (olcum == GirisKaydiOlcumu.Var)
        {
            // OLCULDU: kayit yazildi. Panel sade "olusturuldu" der; sahte uyari uretmez.
            return;
        }

        throw new AppException(
            "mail.account_create_login_unverified",
            500,
            500,
            $"{adres}: posta kutusu olusturuldu, ama MEAOAU.Login kaydinin yazildigi OLCULEMEDI "
            + $"(sonda bu sunucuda kaydi ayirt edemedi). Giris kaydi yoksa hesap listede GORUNUR ama IMAP/SMTP'ye GIRIS YAPILAMAZ; "
            + $"parolayla bir kez oturum acarak sinayin.{olusturmaHatalari}");
    }

    /// <summary>
    /// PAROLA KAPISI. YALNIZ "uygulanmadi" firlatir: "dogrulanamadi" bir HATA DEGILDIR
    /// (parola yazilmis olabilir), o yuzden kullaniciya cumle olarak soylenir.
    /// </summary>
    internal static void ParolaKapisiniAc(ParolaOlcumu olcum, string adres)
    {
        if (olcum == ParolaOlcumu.Uygulanmadi)
        {
            // KALIBRE SONDA "KAYIT YOK" DIYOR: `EditLogin` bir sey yazmadi (ya da kaydi sildi).
            // Bu bir tahmin degil OLCUMDUR: ayni sonda islemden ONCE ayni kaydi BULMUSTU.
            throw new AppException(
                "mail.account_password_not_applied",
                500,
                500,
                $"{adres}: parola yazildi ama giris kaydi islemden SONRA BULUNAMADI (sonda islemden once ayni kaydi buluyordu). Parola UYGULANMADI.");
        }
    }

    /// <summary>
    /// Parola sonucunu calisma alani mesajina yazar.
    /// </summary>
    /// <remarks>
    /// ONCEKI HALI mesaji YALNIZ <c>StatusMessage</c> bos oldugunda yaziyordu: calisma alani
    /// okumasi kendi hata mesajini birakmissa "dogrulanmadi" uyarisi SESSIZCE DUSUYORDU --
    /// yani en kotu durumda (COM'a erisilemiyor) kullanici parola uyarisini HIC gormuyordu.
    /// </remarks>
    internal static void ParolaNotunuYaz(MailWorkspaceDto workspace, ParolaOlcumu olcum)
    {
        var not = ParolaDurumCumlesi(olcum);
        if (string.IsNullOrWhiteSpace(not))
        {
            return;
        }

        workspace.StatusMessage = string.IsNullOrWhiteSpace(workspace.StatusMessage)
            ? not
            : workspace.StatusMessage + " " + not;
    }

    /// <summary>
    /// Yutulan COM hatalari; bos degilse ayrintiya EKLENIR (once tamamen kayboluyordu).
    /// Etiketler cagri yerinden gelir: silmede Remove*, olusturmada Add*.
    /// </summary>
    internal static string SondaHatalari(GirisSondaOkumasi sonda, string kutuEtiketi, string girisEtiketi)
    {
        var parcalar = new List<string>();
        if (!string.IsNullOrWhiteSpace(sonda.KutuHata))
        {
            parcalar.Add(kutuEtiketi + ": " + sonda.KutuHata.Trim());
        }

        if (!string.IsNullOrWhiteSpace(sonda.GirisHata))
        {
            parcalar.Add(girisEtiketi + ": " + sonda.GirisHata.Trim());
        }

        return parcalar.Count == 0 ? string.Empty : " [" + string.Join("; ", parcalar) + "]";
    }

    /// <summary>Yutulan SILME hatalari.</summary>
    internal static string SilmeHatalari(GirisSondaOkumasi sonda)
        => SondaHatalari(sonda, "RemoveMailbox", "RemoveLogin");

    /// <summary>Yutulan OLUSTURMA hatalari.</summary>
    internal static string OlusturmaHatalari(GirisSondaOkumasi sonda)
        => SondaHatalari(sonda, "AddMailbox", "AddLogin");

    /// <summary>Sonda ciktisinin ayristirilmis hali.</summary>
    internal readonly record struct GirisSondaOkumasi(
        bool Ayristirildi,
        bool OnceDoldu,
        bool KontrolDoldu,
        bool SonraDoldu,
        bool Eslesme,
        string KutuHata,
        string GirisHata);

    /// <summary>
    /// Sonda ciktisini okur. AYRISTIRILAMAYAN cikti "her sey yolunda" DEGILDIR:
    /// <see cref="GirisSondaOkumasi.Ayristirildi"/> <c>false</c> doner ve karar OLCULEMEDI olur.
    /// </summary>
    internal static GirisSondaOkumasi SondayiOku(string json)
    {
        var data = ParseJson(json);
        if (data is null)
        {
            return new GirisSondaOkumasi(false, false, false, false, false, string.Empty, string.Empty);
        }

        var root = data.Value;
        return new GirisSondaOkumasi(
            true,
            SondaDoldu(GetStr(root, "onceHata"), GetStr(root, "onceHaklar")),
            SondaDoldu(GetStr(root, "kontrolHata"), GetStr(root, "kontrolHaklar")),
            SondaDoldu(GetStr(root, "sonraHata"), GetStr(root, "sonraHaklar")),
            GetBool(root, "sonraEslesme"),
            GetStr(root, "kutuHata"),
            GetStr(root, "girisHata"));
    }

    // =====================================================================================
    // SONUCU DOGRULAYAN KAPILAR -- TEK KAYNAK
    //
    // OLCULEN HATA: bu sinifin butun mutasyon metotlari `await RunComAsync(...)` deyip
    // ardindan `return await GetWorkspaceAsync(...)` yapiyordu. Yani OLCUM ZATEN YAPILIYOR,
    // sonucuna BAKILMIYORDU. MailEnable COM metotlari istisna DEGIL TAMSAYI DURUM KODU doner;
    // `$ErrorActionPreference='Stop'` bunu YAKALAMAZ ve her donus `[void]` ile atiliyordu.
    // En agiri DeleteAccountAsync'ti: `try { [void]$mb.RemoveMailbox() } catch {}` +
    // `try { [void]$lg.RemoveLogin() } catch {}` -> kutu durabilir, giris hala calisabilir,
    // panel HTTP 200 donerdi.
    //
    // KAPATMA: durum kodu konvansiyonunu TAHMIN ETMEK yerine (bu makinede MailEnable YOK,
    // konvansiyon OLCULEMEZ) SONUCUN KENDISI geri okunur. Uc deger: olculdu+bekleneni tutuyor /
    // olculdu+tutmuyor / OLCULEMEDI. Ucu de ayri cumledir.
    // =====================================================================================

    /// <summary>Calisma alaninda bu adres BULUNUYOR mu? (adres karsilastirmasi tek yerde.)</summary>
    internal static bool HesapVarMi(MailWorkspaceDto workspace, string adres)
        => workspace.Accounts.Any(item => string.Equals(item.Address, adres, StringComparison.OrdinalIgnoreCase));

    /// <remarks>
    /// <b>ANAHTARLAR ELDE KURULMAZ.</b> Ilk yazimda anahtarlar <c>$"mail.{tip}_unverified"</c>
    /// gibi uretiliyordu ve depodaki i18n kullanim kapisi (<c>Kullanilmayan_Ceviri_Anahtari_Kalmadi</c>)
    /// bunlari YETIM sayarak kirmizi yandi -- hakli olarak: kaynakta ARANAMAYAN bir anahtar,
    /// sonradan sessizce olu kalabilir. Anahtarlar artik cagri yerlerinde HARFI HARFINE yazili.
    /// </remarks>
    internal static void KapiAcVarlik(
        bool olculdu,
        bool bulundu,
        bool olmali,
        string olculemediAnahtari,
        string uygulanmadiAnahtari,
        string ayrinti)
    {
        if (!olculdu)
        {
            // OLCULEMEDI: "basarili" DA "basarisiz" DA degil. Panel "yapildi" DEMEZ.
            throw new AppException(olculemediAnahtari, 500, 500, ayrinti);
        }

        if (bulundu != olmali)
        {
            throw new AppException(uygulanmadiAnahtari, 500, 500, ayrinti);
        }
    }

    // --- yardımcılar ---

    internal async Task<string> RunComAsync(string script, CancellationToken cancellationToken)
    {
        // GOVDE OLCUM DIKISI (uretimde null; bkz. KomutKosucusu).
        if (KomutKosucusu is not null)
        {
            return await KomutKosucusu(script, cancellationToken);
        }

        var ps32 = Ps32Path;
        if (!System.IO.File.Exists(ps32))
        {
            throw new AppException("mail.not_installed", 409, 409, "32-bit PowerShell (SysWOW64) bulunamadı.");
        }

        // SIR SIZINTISI: bu script POSTA HESABI PAROLASINI düz metin taşır ($lg.Password = '...').
        // C:\Windows\TEMP'in varsayılan DACL'i yeni dosyalara IIS_IUSRS:ReadData miras ettiriyor
        // (ölçüldü) ve aynı ACE dizini listelemeye de yetiyor → kiracı GUID'i tahmin etmeden
        // dosyayı bulup okuyabiliyordu. Silme (finally) bir temizliktir, erişim denetimi değildir:
        // süreç ortada ölürse dosya kalır. Dosya AÇILIRKEN kilitlenir.
        var tempScript = SecureTempFile.BuildPath("adapanel-me", ".ps1");
        // Windows PowerShell 5.1 otherwise decodes the script with the ANSI code page,
        // corrupting non-ASCII mailbox passwords before the COM call.
        await SecureTempFile.WriteAsync(tempScript, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
        try
        {
            var result = await ProcessRunner.RunAsync(
                ps32,
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempScript}\"",
                cancellationToken: cancellationToken,
                timeout: TimeSpan.FromSeconds(45));

            if (!result.Success)
            {
                // PowerShell stderr can include source lines containing credentials.
                logger.LogWarning("MailEnable command failed. ExitCode={ExitCode}", result.ExitCode);
                throw new AppException(
                    "mail.command_failed",
                    500,
                    500,
                    "MailEnable command failed. Script output is withheld because it may contain credentials.");
            }

            return result.StandardOutput;
        }
        finally
        {
            try { System.IO.File.Delete(tempScript); } catch { /* en iyi çaba */ }
        }
    }

    private async Task RunVerifiedCommandAsync(string script, CancellationToken cancellationToken)
    {
        var output = await RunComAsync(script, cancellationToken);
        if (!string.Equals(output.Trim(), "OK", StringComparison.Ordinal))
            throw new AppException("mail.command_failed", 502, 502, "MailEnable did not confirm the operation.");
    }

    private static JsonElement? ParseJson(string output)
    {
        var trimmed = (output ?? string.Empty).Trim();
        var start = trimmed.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        trimmed = trimmed[start..];
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<JsonElement> ReadArray(JsonElement root, string name)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(name, out var arr))
        {
            if (arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    yield return item;
                }
            }
            else if (arr.ValueKind == JsonValueKind.Object)
            {
                // ConvertTo-Json tek öğeyi dizi yapmaz; tekil nesneyi de döndür.
                yield return arr;
            }
        }
    }

    private static string GetStr(JsonElement item, string name)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt(JsonElement item, string name)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : 0;

    private static bool GetBool(JsonElement item, string name)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int NormalizeLimitToMb(int limitKb)
        => limitKb <= 0 ? 0 : limitKb / 1024;

    private static void ValidateQuota(int quotaMb)
    {
        if (quotaMb is < 0 or > 1024 * 1024) throw new AppException("mail.invalid_quota", 400, 400);
    }

    internal static string DeleteAliasScript(string source, string domain) => $$"""
        $ErrorActionPreference = 'Stop'
        $am = New-Object -ComObject 'MEAOAM.AddressMap'
        $am.SourceAddress = '[SMTP:{{PsLiteralRaw(source)}}]'
        if ($am.GetAddressMap() -eq 1) {
          if ($am.DestinationAddress -notmatch '^\[SMTP:[^\r\n\[\]]+\]$') { throw 'Address is not an alias.' }
          if ($am.Account -and $am.Account -ine '{{PsLiteralRaw(domain)}}') { throw 'Address belongs to another postoffice.' }
          if ($am.RemoveAddressMap() -ne 1) { throw 'Alias removal failed.' }
        }
        $verify = New-Object -ComObject 'MEAOAM.AddressMap'
        $verify.SourceAddress = '[SMTP:{{PsLiteralRaw(source)}}]'
        if ($verify.GetAddressMap() -eq 1) { throw 'Alias removal readback failed.' }
        'OK'
        """;

    private static string GetMailboxLocal(string address)
    {
        var clean = ExtractSmtp(address);
        var at = clean.IndexOf('@');
        return at > 0 ? clean[..at] : clean;
    }

    private static (string Local, string Domain) SplitAddress(string address, bool allowCatchAll = false)
    {
        var clean = ExtractSmtp(address).ToLowerInvariant();
        var at = clean.LastIndexOf('@');
        if (at <= 0) throw new AppException("mail.invalid_address", 400, 400);
        var domain = ValidateDomain(clean[(at + 1)..]);
        return (ValidateLocalAddress(clean, domain, allowCatchAll), domain);
    }

    internal static string ValidateDomain(string domain)
    {
        var normalized = DomainNames.Normalize(domain);
        try { normalized = new System.Globalization.IdnMapping().GetAscii(normalized); }
        catch (ArgumentException) { throw new AppException("mail.invalid_domain", 400, 400); }
        if (normalized.Length > 253 || !normalized.Contains('.') || normalized.Split('.').Any(label =>
            label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' ||
            label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new AppException("mail.invalid_domain", 400, 400);
        return normalized;
    }

    internal static string ValidateLocalAddress(string address, string domain, bool allowCatchAll)
    {
        var clean = ExtractSmtp(address).ToLowerInvariant();
        if (allowCatchAll && clean is "*" or "@") return "*";
        var at = clean.LastIndexOf('@');
        if (at >= 0)
        {
            if (!ValidateDomain(clean[(at + 1)..]).Equals(domain, StringComparison.OrdinalIgnoreCase))
                throw new AppException("mail.invalid_address", 400, 400);
            clean = clean[..at];
        }
        if (allowCatchAll && clean == "*") return clean;
        if (clean.Length is 0 or > 64 || clean.StartsWith('.') || clean.EndsWith('.') || clean.Contains("..") ||
            clean.Any(c => !char.IsAsciiLetterOrDigit(c) && !".!#$%&'+-=^_`{|}~".Contains(c)))
            throw new AppException("mail.invalid_address", 400, 400);
        return clean;
    }

    private static string ExtractSmtp(string value)
    {
        var text = (value ?? string.Empty).Trim();
        // "[SMTP:user@domain]" veya "[SF:po/mbx]" -> içerideki adresi çıkar.
        var colon = text.IndexOf(':');
        if (text.StartsWith("[", StringComparison.Ordinal) && text.EndsWith("]", StringComparison.Ordinal) && colon > 0)
        {
            text = text[(colon + 1)..^1];
        }

        return text.Trim();
    }

    // PowerShell tek-tırnak literal içeriği (script tek-tırnakla sarıldığı için ' -> '').
    private static string PsLiteralRaw(string value)
        => (value ?? string.Empty).Replace("'", "''");
}
