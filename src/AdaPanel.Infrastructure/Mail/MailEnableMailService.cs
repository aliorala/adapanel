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
public sealed class MailEnableMailService(ILogger<MailEnableMailService> logger) : IMailService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string Ps32Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "SysWOW64", "WindowsPowerShell", "v1.0", "powershell.exe");

    // MailEnable kurulu mu? (servis dizini + COM). Router bunu kullanır.
    public static bool IsInstalled()
    {
        var dir = Environment.ExpandEnvironmentVariables("%ProgramFiles(x86)%\\Mail Enable\\BIN\\MEInstaller.exe");
        return System.IO.File.Exists(dir);
    }

    public async Task<MailWorkspaceDto> GetWorkspaceAsync(string? domain, CancellationToken cancellationToken = default)
    {
        var selected = (domain ?? string.Empty).Trim().ToLowerInvariant();
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $domains = @()
            $po = New-Object -ComObject 'MEAOPO.PostOffice'
            $seen = @{}
            if ($po.FindFirstPostoffice() -eq 0) {
              $n = 0
              do {
                $nm = "$($po.Name)"
                # Bazı MailEnable COM sürümlerinde FindNext sonu doğru bildirmez (sonsuz
                # döngü/timeout). Tekrar eden ad gelirse enumerasyonu sonlandır.
                if ($seen.ContainsKey($nm)) { break }
                $seen[$nm] = $true
                $domains += [pscustomobject]@{ name = $nm; status = [int]$po.Status }
                $n++
              } while ($po.FindNextPostoffice() -eq 0 -and $n -lt 500)
            }
            $accounts = @(); $aliases = @()
            $sel = '{{PsLiteralRaw(selected)}}'
            if ($sel) {
              $mb = New-Object -ComObject 'MEAOPO.Mailbox'
              $mb.Postoffice = $sel
              if ($mb.FindFirstMailbox() -eq 0) {
                $n = 0; $seenMb = @{}
                do {
                  $mbn = "$($mb.Mailbox)"
                  if ($seenMb.ContainsKey($mbn)) { break }
                  $seenMb[$mbn] = $true
                  $accounts += [pscustomobject]@{ mailbox = $mbn; status = [int]$mb.Status; limit = [int]$mb.Limit }
                  $n++
                } while ($mb.FindNextMailbox() -eq 0 -and $n -lt 5000)
              }
              $am = New-Object -ComObject 'MEAOAM.AddressMap'
              if ($am.FindFirstAddressMap() -eq 0) {
                $n = 0; $seenAm = @{}
                do {
                  $src = "$($am.SourceAddress)"; $dst = "$($am.DestinationAddress)"
                  $key = "$src=>$dst"
                  if ($seenAm.ContainsKey($key)) { break }
                  $seenAm[$key] = $true
                  $isCatchAll = $src -match [regex]::Escape("[SMTP:$sel]")
                  if (($src -match [regex]::Escape("@$sel") -or $isCatchAll) -and $dst -match 'SMTP:') {
                    $aliases += [pscustomobject]@{ source = $src; destination = $dst }
                  }
                  $n++
                } while ($am.FindNextAddressMap() -eq 0 -and $n -lt 20000)
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
            return new MailWorkspaceDto
            {
                Installed = true,
                Configured = true,
                SelectedDomain = selected,
                StatusMessage = "Mail yönetim bileşenine (MailEnable COM) erişilemedi. MailEnable kurulu ve COM kayıtlı olmalı. Ayrıntı: "
                    + (string.IsNullOrWhiteSpace(exception.Details) ? exception.Message : exception.Details)
            };
        }

        var data = ParseJson(json);

        var workspace = new MailWorkspaceDto
        {
            Installed = true,
            Configured = true,
            SelectedDomain = selected
        };

        if (data is null)
        {
            return workspace;
        }

        var root = data.Value;
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

        return workspace;
    }

    // MailEnable yönetici parolası gerektirmez; "yapılandırıldı" olarak döner.
    public Task<MailWorkspaceDto> ConfigureAsync(ConfigureMailRequest request, CancellationToken cancellationToken = default)
        => GetWorkspaceAsync(null, cancellationToken);

    public async Task<MailWorkspaceDto> CreateDomainAsync(CreateMailDomainRequest request, CancellationToken cancellationToken = default)
    {
        var domain = request.DomainName.Trim().ToLowerInvariant();
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $po = New-Object -ComObject 'MEAOPO.PostOffice'
            $po.Account = '{{PsLiteralRaw(domain)}}'
            $po.Name = '{{PsLiteralRaw(domain)}}'
            $po.Status = 1
            [void]$po.AddPostoffice()
            'OK'
            """;
        await RunComAsync(script, cancellationToken);
        return await GetWorkspaceAsync(domain, cancellationToken);
    }

    public async Task<MailWorkspaceDto> DeleteDomainAsync(string domainName, CancellationToken cancellationToken = default)
    {
        var domain = domainName.Trim().ToLowerInvariant();
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $po = New-Object -ComObject 'MEAOPO.PostOffice'
            $po.Account = '{{PsLiteralRaw(domain)}}'
            $po.Name = '{{PsLiteralRaw(domain)}}'
            [void]$po.RemovePostoffice()
            'OK'
            """;
        await RunComAsync(script, cancellationToken);
        return await GetWorkspaceAsync(null, cancellationToken);
    }

    public async Task<MailWorkspaceDto> CreateAccountAsync(CreateMailAccountRequest request, CancellationToken cancellationToken = default)
    {
        var domain = request.Domain.Trim().ToLowerInvariant();
        var local = GetMailboxLocal(request.Address).ToLowerInvariant();
        var full = $"{local}@{domain}";
        var password = request.Password;

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $mb = New-Object -ComObject 'MEAOPO.Mailbox'
            $mb.Postoffice = '{{PsLiteralRaw(domain)}}'
            $mb.Mailbox = '{{PsLiteralRaw(local)}}'
            $mb.Status = 1
            $mb.Limit = -1
            $mb.RedirectAddress = ''
            $mb.RedirectStatus = 0
            [void]$mb.AddMailbox()
            $lg = New-Object -ComObject 'MEAOAU.Login'
            $lg.Account = '{{PsLiteralRaw(domain)}}'
            $lg.UserName = '{{PsLiteralRaw(full)}}'
            $lg.Password = '{{PsLiteralRaw(password)}}'
            $lg.Rights = 'USER'
            $lg.Status = 1
            [void]$lg.AddLogin()
            'OK'
            """;
        await RunComAsync(script, cancellationToken);
        return await GetWorkspaceAsync(domain, cancellationToken);
    }

    public async Task<MailWorkspaceDto> DeleteAccountAsync(string address, CancellationToken cancellationToken = default)
    {
        var (local, domain) = SplitAddress(address);
        var full = $"{local}@{domain}";
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $mb = New-Object -ComObject 'MEAOPO.Mailbox'
            $mb.Postoffice = '{{PsLiteralRaw(domain)}}'
            $mb.Mailbox = '{{PsLiteralRaw(local)}}'
            try { [void]$mb.RemoveMailbox() } catch {}
            $lg = New-Object -ComObject 'MEAOAU.Login'
            $lg.Account = '{{PsLiteralRaw(domain)}}'
            $lg.UserName = '{{PsLiteralRaw(full)}}'
            try { [void]$lg.RemoveLogin() } catch {}
            'OK'
            """;
        await RunComAsync(script, cancellationToken);
        return await GetWorkspaceAsync(domain, cancellationToken);
    }

    public async Task<MailWorkspaceDto> SetAccountPasswordAsync(SetMailAccountPasswordRequest request, CancellationToken cancellationToken = default)
    {
        var (local, domain) = SplitAddress(request.Address);
        var full = $"{local}@{domain}";
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $lg = New-Object -ComObject 'MEAOAU.Login'
            $lg.Account = '{{PsLiteralRaw(domain)}}'
            $lg.UserName = '{{PsLiteralRaw(full)}}'
            [void]$lg.GetLogin()
            $lg.Password = '{{PsLiteralRaw(request.Password)}}'
            $lg.Status = 1
            [void]$lg.EditLogin()
            'OK'
            """;
        await RunComAsync(script, cancellationToken);
        return await GetWorkspaceAsync(domain, cancellationToken);
    }

    public async Task<MailWorkspaceDto> CreateAliasAsync(CreateMailAliasRequest request, CancellationToken cancellationToken = default)
    {
        var domain = request.Domain.Trim().ToLowerInvariant();
        var aliasLocal = GetMailboxLocal(request.Address).ToLowerInvariant();
        var aliasAddress = $"{aliasLocal}@{domain}";
        var target = request.Target.Trim().ToLowerInvariant();
        // "*" yerel kısmı = catch-all (yakala-tümü). MailEnable'da kaynak çıplak alan adıdır:
        // domaine gelip eşleşmeyen TÜM adresler hedefe yönlenir (panel "catch-all" gibi).
        var isCatchAll = aliasLocal is "*" or "@" or "";
        var source = isCatchAll ? domain : aliasAddress;
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $am = New-Object -ComObject 'MEAOAM.AddressMap'
            $am.SourceAddress = '[SMTP:{{PsLiteralRaw(source)}}]'
            $am.DestinationAddress = '[SMTP:{{PsLiteralRaw(target)}}]'
            $am.Scope = 0
            $am.Status = 1
            [void]$am.AddAddressMap()
            'OK'
            """;
        await RunComAsync(script, cancellationToken);
        return await GetWorkspaceAsync(domain, cancellationToken);
    }

    public async Task<MailWorkspaceDto> DeleteAliasAsync(string address, CancellationToken cancellationToken = default)
    {
        var (local, domain) = SplitAddress(address);
        // Catch-all kaydının kaynağı çıplak alan adıdır; "*@domain" silinirken bunu eşle.
        var clean = local is "*" or "@" or "" ? domain : ExtractSmtp(address);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $am = New-Object -ComObject 'MEAOAM.AddressMap'
            $am.SourceAddress = '[SMTP:{{PsLiteralRaw(clean)}}]'
            try { [void]$am.RemoveAddressMap() } catch {}
            'OK'
            """;
        await RunComAsync(script, cancellationToken);
        return await GetWorkspaceAsync(domain, cancellationToken);
    }

    // Dağıtım listeleri yalnızca Stalwart'ta desteklenir; MailEnable yedeğinde net hata döner.
    public Task<MailWorkspaceDto> CreateMailListAsync(CreateMailListRequest request, CancellationToken cancellationToken = default)
        => throw new AppException("mail.lists_unsupported", 409, 409);

    public Task<MailWorkspaceDto> SetMailListRecipientsAsync(SetMailListRecipientsRequest request, CancellationToken cancellationToken = default)
        => throw new AppException("mail.lists_unsupported", 409, 409);

    public Task<MailWorkspaceDto> DeleteMailListAsync(string address, CancellationToken cancellationToken = default)
        => throw new AppException("mail.lists_unsupported", 409, 409);

    // MailEnable dinleyicileri bu görünümde listelenmez (Stalwart'a özel); boş döner.
    public Task<IReadOnlyCollection<MailListenerDto>> GetListenersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyCollection<MailListenerDto>>([]);

    public async Task<MailWorkspaceDto> SetAccountQuotaAsync(SetMailAccountQuotaRequest request, CancellationToken cancellationToken = default)
    {
        var (local, domain) = SplitAddress(request.Address);
        // MailEnable Limit birimi KB'dir; 0/negatif = sınırsız.
        var limitKb = request.MaxSizeMB > 0 ? request.MaxSizeMB * 1024 : -1;
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $mb = New-Object -ComObject 'MEAOPO.Mailbox'
            $mb.Postoffice = '{{PsLiteralRaw(domain)}}'
            $mb.Mailbox = '{{PsLiteralRaw(local)}}'
            [void]$mb.GetMailbox()
            $mb.Limit = {{limitKb}}
            [void]$mb.EditMailbox()
            'OK'
            """;
        await RunComAsync(script, cancellationToken);
        return await GetWorkspaceAsync(domain, cancellationToken);
    }

    // Domain seviyesinde kota MailEnable'da postoffice nesnesinde yoktur; mevcut durumu döner.
    public Task<MailWorkspaceDto> SetDomainQuotaAsync(SetMailDomainQuotaRequest request, CancellationToken cancellationToken = default)
        => GetWorkspaceAsync(request.Domain.Trim().ToLowerInvariant(), cancellationToken);

    public async Task<MailAutoResponderDto> GetAutoResponderAsync(string address, CancellationToken cancellationToken = default)
    {
        var (local, domain) = SplitAddress(address);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $mb = New-Object -ComObject 'MEAOPO.Mailbox'
            $mb.Postoffice = '{{PsLiteralRaw(domain)}}'
            $mb.Mailbox = '{{PsLiteralRaw(local)}}'
            [void]$mb.GetMailbox()
            $status = 0; $contents = ''
            try { $status = [int]$mb.GetAutoResponderStatus() } catch {}
            try { $contents = [string]$mb.GetAutoResponderContents() } catch {}
            [pscustomobject]@{ enabled = $status; message = $contents } | ConvertTo-Json -Compress
            """;
        var json = await RunComAsync(script, cancellationToken);
        var data = ParseJson(json);
        return new MailAutoResponderDto
        {
            Address = $"{local}@{domain}",
            Enabled = data is not null && GetInt(data.Value, "enabled") == 1,
            Message = data is not null ? GetStr(data.Value, "message") : string.Empty
        };
    }

    public async Task<MailAutoResponderDto> SetAutoResponderAsync(SetMailAutoResponderRequest request, CancellationToken cancellationToken = default)
    {
        var (local, domain) = SplitAddress(request.Address);
        var status = request.Enabled ? 1 : 0;
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $mb = New-Object -ComObject 'MEAOPO.Mailbox'
            $mb.Postoffice = '{{PsLiteralRaw(domain)}}'
            $mb.Mailbox = '{{PsLiteralRaw(local)}}'
            [void]$mb.GetMailbox()
            [void]$mb.SetAutoResponderContents('{{PsLiteralRaw(request.Message)}}')
            [void]$mb.SetAutoResponderStatus({{status}})
            'OK'
            """;
        await RunComAsync(script, cancellationToken);
        return await GetAutoResponderAsync(request.Address, cancellationToken);
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
        => Task.FromResult(new MailCertificateDto { Active = true, Source = "mailenable" });

    public Task<MailCertificateDto> IssueCertificateAsync(IssueMailCertificateRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new MailCertificateDto { Active = true, Source = "mailenable" });

    /// <summary>MailEnable tarafinda karsilama adi panelden yonetilmiyor.</summary>
    public Task<bool> EnsureServerHostnameAsync(bool applyChanges = true, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<MailCertificateDto?> SyncCertificateAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<MailCertificateDto?>(null);

    public Task<MailSpamSettingsDto> GetSpamSettingsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new MailSpamSettingsDto { Installed = true, Configured = true, Enabled = true });

    public Task<MailSpamSettingsDto> UpdateSpamSettingsAsync(UpdateMailSpamSettingsRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new MailSpamSettingsDto { Installed = true, Configured = true, Enabled = request.Enabled });

    // --- yardımcılar ---

    private async Task<string> RunComAsync(string script, CancellationToken cancellationToken)
    {
        var ps32 = Ps32Path;
        if (!System.IO.File.Exists(ps32))
        {
            throw new AppException("mail.not_installed", 409, 409, "32-bit PowerShell (SysWOW64) bulunamadı.");
        }

        var tempScript = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"adapanel-me-{Guid.NewGuid():N}.ps1");
        await System.IO.File.WriteAllTextAsync(tempScript, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        try
        {
            var result = await ProcessRunner.RunAsync(
                ps32,
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempScript}\"",
                cancellationToken: cancellationToken,
                timeout: TimeSpan.FromSeconds(45));

            if (!result.Success)
            {
                logger.LogWarning(
                    "MailEnable COM command failed. ExitCode={ExitCode} Error={Error}",
                    result.ExitCode,
                    result.StandardError);
                throw new AppException(
                    "mail.command_failed",
                    500,
                    500,
                    string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
            }

            return result.StandardOutput;
        }
        finally
        {
            try { System.IO.File.Delete(tempScript); } catch { /* en iyi çaba */ }
        }
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

    private static int NormalizeLimitToMb(int limitKb)
        => limitKb <= 0 ? 0 : limitKb / 1024;

    private static string GetMailboxLocal(string address)
    {
        var clean = ExtractSmtp(address);
        var at = clean.IndexOf('@');
        return at > 0 ? clean[..at] : clean;
    }

    private static (string Local, string Domain) SplitAddress(string address)
    {
        var clean = ExtractSmtp(address).ToLowerInvariant();
        var at = clean.IndexOf('@');
        return at > 0 ? (clean[..at], clean[(at + 1)..]) : (clean, string.Empty);
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
