using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>
/// Stalwart Mail Server yönetimi. Tüm işlemler <c>stalwart-cli</c> üzerinden,
/// sunucunun yönetim API'sine (http://localhost:8080) bağlanarak yapılır.
/// Yönetici parolası, panel üzerinden kurulan Stalwart bileşeninin üretilmiş
/// parolasından (veya <c>config/mail.json</c> override'ından) okunur.
/// Şema notları (v0.16, canlı sunucuda doğrulandı): hesaplar <c>Account/User</c>
/// varyantıyla oluşturulur (name=yerel kısım + domainId); parola hesabın
/// <c>credentials</c> map'inde <c>{"@type":"Password","secret":...}</c> olarak,
/// alias'lar hesabın <c>aliases</c> map'inde tutulur; ayrı bir alias nesnesi yoktur.
/// CLI çıktıları NDJSON'dur (satır başına bir nesne).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MailService(
    JsonFileStore fileStore,
    ISecretProtector secretProtector,
    IPanelPreferencesService panelPreferencesService,
    IIisSiteService iisSiteService,
    IDnsService dnsService,
    AdaPanelDbContext dbContext,
    ILogger<MailService> logger) : IMailService
{
    /// <summary>
    /// Hesap disk kotasının Stalwart şemasındaki YERİ: <c>quotas</c> haritasının <c>maxDiskQuota</c>
    /// anahtarı (byte). Tek tamsayılık bir <c>quota</c> alanı YOKTUR — o ad kullanıldığında sunucu
    /// "unknown field: quota" döndürür ve kota sessizce uygulanmamış olur.
    /// </summary>
    private const string AccountQuotaField = "quotas/maxDiskQuota";

    private const string ConfigPath = "config/mail.json";
    private const string CertificateStatePath = "config/mail-cert.json";
    // Domain kotalari Stalwart'ta yok → panel-tarafi tutulur (domain→azami toplam MB) ve tahsis toplamiyla dayatilir.
    private const string DomainQuotaPath = "config/mail-domain-quotas.json";
    private const string ServerComponentId = "stalwart-mail";
    private const string CliComponentId = "stalwart-cli";
    private const string GeneratedPasswordKey = "generatedPassword";
    private const string AdminUser = "admin";
    // localhost hem ::1 hem 127.0.0.1'i kapsar; Stalwart varsayılanları Windows'ta
    // yalnız IPv6'ya bağlanabildiğinden sabit 127.0.0.1 kullanılmaz.
    private const string ManagementUrl = "http://localhost:8080";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Stalwart mail sunucusu bu makineye (panel üzerinden) kurulmuş mu? Router kullanır.</summary>
    public static bool IsInstalled()
    {
        var root = Environment.ExpandEnvironmentVariables("%ProgramData%\\AdaPanel\\runtimes\\stalwart");
        return Directory.Exists(root) &&
               Directory.EnumerateFiles(root, "stalwart*.exe", SearchOption.AllDirectories).Any();
    }

    public async Task<MailWorkspaceDto> GetWorkspaceAsync(string? domain, CancellationToken cancellationToken = default)
    {
        var cli = await ResolveCliAsync(cancellationToken);
        if (cli is null)
        {
            return new MailWorkspaceDto { Installed = false };
        }

        var password = await GetAdminPasswordAsync(cancellationToken);
        if (password is null)
        {
            return new MailWorkspaceDto { Installed = true, Configured = false };
        }

        var env = BuildEnvironment(password);
        var probe = await RunCliAsync(cli, "query domain --json", env, cancellationToken);
        if (!probe.Success)
        {
            // CLI var ama sunucuya bağlanılamıyor (henüz başlamamış olabilir) ya da kimlik doğrulama başarısız.
            return new MailWorkspaceDto { Installed = true, Configured = false };
        }

        var quotas = await ReadDomainQuotasAsync(cancellationToken);
        var domainItems = ParseItems(probe.StandardOutput);
        var domains = domainItems
            .Select(item => new MailDomainDto
            {
                Name = GetField(item, "name"),
                Active = GetBoolField(item, "isEnabled", "active", "enabled"),
                AccountCount = 0,
                MaxSizeMB = quotas.GetValueOrDefault(GetField(item, "name").Trim().ToLowerInvariant())
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .ToArray();

        var workspace = new MailWorkspaceDto
        {
            Installed = true,
            Configured = true,
            SelectedDomain = domain ?? string.Empty,
            Domains = domains
        };

        if (!string.IsNullOrWhiteSpace(domain))
        {
            var normalized = domain.Trim().ToLowerInvariant();
            var domainId = domainItems
                .Where(item => string.Equals(GetField(item, "name"), normalized, StringComparison.OrdinalIgnoreCase))
                .Select(item => GetField(item, "id"))
                .FirstOrDefault();

            // Hesap özet listesi yalnızca emailAddress+id içerir; kota, kullanım ve
            // alias'lar için hesap detayını çekmek gerekir.
            var accounts = new List<MailAccountDto>();
            var aliases = new List<MailAliasDto>();
            foreach (var brief in await QueryAsync(cli, "account", env, cancellationToken))
            {
                var id = GetField(brief, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var detail = await GetObjectAsync(cli, "account", id, env, cancellationToken);
                if (detail is null)
                {
                    continue;
                }

                var address = GetField(detail.Value, "emailAddress", "name");
                if (address.EndsWith("@" + normalized, StringComparison.OrdinalIgnoreCase))
                {
                    accounts.Add(new MailAccountDto
                    {
                        Address = address,
                        Active = true,
                        MaxSizeMB = (int)(ReadAccountQuotaBytes(detail.Value) / (1024 * 1024)),
                        CurrentSizeMB = (int)(GetLongField(detail.Value, "usedDiskQuota") / (1024 * 1024))
                    });
                }

                // Alias'lar hedef hesabın içinde saklanır; seçili domain'e ait olanları topla.
                if (detail.Value.TryGetProperty("aliases", out var aliasMap) && aliasMap.ValueKind == JsonValueKind.Object)
                {
                    foreach (var entry in aliasMap.EnumerateObject())
                    {
                        var aliasName = GetField(entry.Value, "name");
                        var aliasDomainId = GetField(entry.Value, "domainId");
                        if (!string.IsNullOrWhiteSpace(aliasName)
                            && !string.IsNullOrWhiteSpace(domainId)
                            && string.Equals(aliasDomainId, domainId, StringComparison.Ordinal))
                        {
                            aliases.Add(new MailAliasDto
                            {
                                Address = $"{aliasName}@{normalized}",
                                Target = address,
                                Active = GetBoolField(entry.Value, "enabled")
                            });
                        }
                    }
                }
            }

            // Dağıtım listeleri (mailing-list): seçili domain'e ait olanları topla.
            var lists = new List<MailListDto>();
            foreach (var brief in await QueryAsync(cli, "mailing-list", env, cancellationToken))
            {
                var id = GetField(brief, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var listDetail = await GetObjectAsync(cli, "mailing-list", id, env, cancellationToken) ?? brief;
                var listAddress = GetField(listDetail, "emailAddress", "name");
                if (!listAddress.EndsWith("@" + normalized, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                lists.Add(new MailListDto
                {
                    Address = listAddress,
                    Recipients = ReadStringArray(listDetail, "recipients")
                });
            }

            workspace.Accounts = accounts;
            workspace.Aliases = aliases;
            workspace.Lists = lists;

            // Seçili domainin tahsis toplamını (kutu kotaları) doldur → UI "kullanılan / limit" gösterir.
            var selected = domains.FirstOrDefault(item => string.Equals(item.Name, normalized, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                selected.AllocatedMB = accounts.Sum(account => Math.Max(0, account.MaxSizeMB));
                selected.AccountCount = accounts.Count;
            }
        }

        return workspace;
    }

    public async Task<MailWorkspaceDto> ConfigureAsync(ConfigureMailRequest request, CancellationToken cancellationToken = default)
    {
        var cli = await ResolveCliAsync(cancellationToken)
            ?? throw new AppException("mail.not_installed", 409, 409);

        var env = BuildEnvironment(request.AdminPassword);
        var probe = await RunCliAsync(cli, "query domain --json", env, cancellationToken);
        if (!probe.Success)
        {
            throw new AppException("mail.invalid_admin_password", 401, 401, CombineError(probe));
        }

        await fileStore.WriteAsync(ConfigPath, new MailConfig
        {
            AdminPasswordProtected = secretProtector.Protect(request.AdminPassword)
        }, cancellationToken);

        return await GetWorkspaceAsync(null, cancellationToken);
    }

    public async Task<MailWorkspaceDto> CreateDomainAsync(CreateMailDomainRequest request, CancellationToken cancellationToken = default)
    {
        var domain = request.DomainName.Trim().ToLowerInvariant();
        // İDEMPOTENT: domain Stalwart'ta ZATEN varsa (ör. önceki kurulumdan/silinemeyen testten kalma) "create
        // domain" reddeder → mail.operation_failed → site oluşturma uyarı verirdi (canlı bulgu).
        // Profesyonel paneller gibi var olanı BAŞARI say. (Stalwart kurulu/yapılandırılı değilse FindIdAsync null
        // döner → MutateAsync gerçek mail.not_installed/not_configured'ı fırlatır = maskelenmez.)
        var existing = await FindIdAsync("domain", "name", domain, cancellationToken);
        if (existing is not null)
        {
            return await GetWorkspaceAsync(domain, cancellationToken);
        }

        var workspace = await MutateAsync(
            domain,
            $"create domain --field name={Arg(domain)} --field isEnabled=true",
            cancellationToken);

        // Kayitlar HEMEN yayinlanir; kullanicinin bir dugmeye basmasi beklenmez.
        //
        // Neden: Stalwart domain olusturulurken DKIM anahtarlarini kendisi uretir ve giden her
        // postayi o anahtarlarla IMZALAR. Karsilik gelen DNS kaydi yayinlanmazsa alici imzayi
        // dogrulayamaz -- posta imzali ama DOGRULANAMAZ gider, yani DKIM basarisiz sayilir ve
        // spam'e duser. Canli sunucuda tam olarak bu oldu: iletiler v1-rsa-*/v1-ed25519-* seciciyle
        // imzaliydi, iki TXT kaydinin ikisi de bolgede yoktu. Plesk de posta acilirken kaydi
        // kendisi yayinlar.
        await TryPublishDnsRecordsAsync(domain, cancellationToken);
        return workspace;
    }

    /// <summary>
    /// Posta DNS kayitlarini (MX, SPF, DKIM, DMARC) panelin bolgesine yazar.
    /// <para>
    /// EN IYI CABA: bolge baska bir saglayicida olabilir ya da DNS rolu kurulu olmayabilir. Boyle
    /// bir durumda posta alaninin OLUSMASI engellenmemeli -- kullanici kayitlari elle de girebilir.
    /// Sessiz kalmamak icin sonuc gunlugue yazilir.
    /// </para>
    /// </summary>
    private async Task TryPublishDnsRecordsAsync(string domain, CancellationToken cancellationToken)
    {
        try
        {
            var dns = await GetDnsRecordsAsync(domain, cancellationToken);
            var yazilan = 0;
            foreach (var record in dns.Records.Where(item => !string.IsNullOrWhiteSpace(item.Value)))
            {
                await dnsService.UpsertRecordAsync(
                    dns.Domain,
                    new UpsertDnsRecordRequest
                    {
                        Type = record.Type,
                        Host = record.Host,
                        Value = record.Value,
                        Priority = record.Priority,
                        Ttl = 3600
                    },
                    cancellationToken);
                yazilan++;
            }

            logger.LogInformation("Mail DNS records published for {Domain}: {Count} record(s).", domain, yazilan);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Mail DNS records could not be published automatically for {Domain}. The zone is probably hosted elsewhere; the records must be added manually.",
                domain);
        }
    }

    public async Task<MailWorkspaceDto> DeleteDomainAsync(string domainName, CancellationToken cancellationToken = default)
    {
        var domain = domainName.Trim().ToLowerInvariant();
        var (cli, env) = await RequireCliAsync(cancellationToken);

        var domainId = await FindIdAsync("domain", "name", domain, cancellationToken);
        if (domainId is null)
        {
            // Domain Stalwart'ta yok; idempotent dön (UI'de kaldıysa tazelenmiş liste temizler).
            return await GetWorkspaceAsync(null, cancellationToken);
        }

        // profesyonel paneller gibi CASCADE: domaine bağlı TÜM üyeler (hesap + grup + liste) domainle birlikte
        // gider. Stalwart, ÜYESİ olan bir domaini silmeyi REDDEDER → eskiden yalnız hesaplar siliniyordu; domainde
        // grup/liste/öksüz üye kalınca "delete domain" patlıyor ve domain UI'de takılı kalıyordu (mail.operation_failed).
        // QueryAsync bilinmeyen/desteklenmeyen tipte [] döner (fırlatmaz) → grup/liste güvenle denenir. account
        // turunda ayrıca BAŞKA domaindeki hesaplarda bu domaine işaret eden alias girdileri de temizlenir.
        foreach (var principalType in new[] { "list", "group", "account" })
        {
            foreach (var brief in await QueryAsync(cli, principalType, env, cancellationToken))
            {
                var memberId = GetField(brief, "id");
                if (string.IsNullOrWhiteSpace(memberId))
                {
                    continue;
                }

                var detail = await GetObjectAsync(cli, principalType, memberId, env, cancellationToken);
                var memberDomainId = detail is null ? null : GetField(detail.Value, "domainId");
                var memberEmail = detail is null ? GetField(brief, "emailAddress") : GetField(detail.Value, "emailAddress");
                var belongsToDomain =
                    string.Equals(memberDomainId, domainId, StringComparison.Ordinal) ||
                    (memberEmail?.EndsWith("@" + domain, StringComparison.OrdinalIgnoreCase) ?? false);

                if (belongsToDomain)
                {
                    await RunCliAsync(cli, $"delete {principalType} --ids {Arg(memberId)}", env, cancellationToken);
                    continue;
                }

                // Başka domaindeki HESAPTA bu domaine ait alias varsa alias map'ten çıkar (yalnız account).
                if (principalType == "account" && detail is not null)
                {
                    var (entries, removed) = StripAliasesOfDomain(detail.Value, domainId);
                    if (removed)
                    {
                        var aliasPayload = "aliases={" + string.Join(",", entries) + "}";
                        await RunCliAsync(cli, $"update account {Arg(memberId)} --field {CliJsonArg(aliasPayload)}", env, cancellationToken);
                    }
                }
            }
        }

        // Üyeler temizlendi → domaini sil. Hâlâ reddederse gerçek Stalwart çıktısı Details'te taşınır (cascade gösterir).
        var result = await RunCliAsync(cli, $"delete domain --ids {Arg(domainId)}", env, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("mail.operation_failed", 500, 500, CombineError(result));
        }

        return await GetWorkspaceAsync(null, cancellationToken);
    }

    // Bir hesabın alias map'inden verilen domainId'ye ait tüm girdileri çıkarır.
    private static (List<string> Entries, bool Removed) StripAliasesOfDomain(JsonElement account, string domainId)
    {
        var entries = new List<string>();
        var removed = false;
        if (account.TryGetProperty("aliases", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in map.EnumerateObject())
            {
                if (string.Equals(GetField(entry.Value, "domainId"), domainId, StringComparison.Ordinal))
                {
                    removed = true;
                    continue;
                }

                entries.Add($"{JsonValue(entry.Name)}:{entry.Value.GetRawText()}");
            }
        }

        return (entries, removed);
    }

    public async Task<MailWorkspaceDto> CreateAccountAsync(CreateMailAccountRequest request, CancellationToken cancellationToken = default)
    {
        var domain = request.Domain.Trim().ToLowerInvariant();
        var address = NormalizeAddress(request.Address, domain);
        var localPart = address[..address.IndexOf('@')];
        var (cli, env) = await RequireCliAsync(cancellationToken);
        var domainId = await FindIdAsync("domain", "name", domain, cancellationToken)
            ?? throw new AppException("mail.operation_failed", 404, 404, $"domain not found: {domain}");

        // panel-tarafı domain kotası denetimi: yeni kutu domain toplamını aşarsa reddet (Plesk gibi).
        await EnsureDomainAllocationAsync(domain, address, request.MaxSizeMB, cancellationToken);

        // Hesap KOTASIZ oluşturulur: canlı testte quota'sız `create Account/User` çalışıyor,
        // payload'a "quotas" eklenince Stalwart 0.16.9 reddediyordu. Ayrıca JSON --json yerine
        // --file ile geçirilir (Windows komut satırı tırnak bozulmasına karşı sigorta).
        var payload =
            $"{{\"name\":{JsonValue(localPart)},\"domainId\":{JsonValue(domainId)}," +
            $"\"credentials\":{{\"1\":{{\"@type\":\"Password\",\"secret\":{JsonValue(request.Password)}}}}}}}";
        var result = await RunCliJsonAsync(cli, "create Account/User", payload, env, cancellationToken);
        if (!result.Success)
        {
            throw MapMailError(result);
        }

        // Kota hesap oluştuktan SONRA ayrı bir `update` ile uygulanır (yaratma payload'ına "quotas"
        // eklenince Stalwart 0.16.x reddediyor). Başarısızlık YUTULMAZ, günlüğe yazılır: burada
        // sessizce yutmak, yöneticinin "512 MB kota koydum" sanıp kutunun sınırsız kalmasına yol
        // açıyordu — canlı testte tam olarak bu yaşandı (yanlış alan adı yüzünden her çağrı
        // "unknown field: quota" ile düşüyor, panel yine de "tamam" diyordu).
        if (request.MaxSizeMB > 0)
        {
            var accountId = await FindIdAsync("account", "emailAddress", address, cancellationToken);
            if (accountId is not null)
            {
                var bytes = (long)request.MaxSizeMB * 1024 * 1024;
                var quotaResult = await RunCliAsync(
                    cli,
                    $"update account {Arg(accountId)} --field {AccountQuotaField}={bytes}",
                    env,
                    cancellationToken);
                if (!quotaResult.Success)
                {
                    logger.LogWarning(
                        "Posta kutusu kotası uygulanamadı ({Address}, {SizeMB} MB): {Error}",
                        address,
                        request.MaxSizeMB,
                        string.IsNullOrWhiteSpace(quotaResult.StandardError) ? quotaResult.StandardOutput : quotaResult.StandardError);
                }
            }
        }

        return await GetWorkspaceAsync(domain, cancellationToken);
    }

    public async Task<MailWorkspaceDto> DeleteAccountAsync(string address, CancellationToken cancellationToken = default)
    {
        var normalized = address.Trim().ToLowerInvariant();
        var domain = ExtractDomain(normalized);
        var id = await FindIdAsync("account", "emailAddress", normalized, cancellationToken) ?? normalized;
        return await MutateAsync(domain, $"delete account --ids {Arg(id)}", cancellationToken);
    }

    public async Task<MailWorkspaceDto> SetAccountPasswordAsync(SetMailAccountPasswordRequest request, CancellationToken cancellationToken = default)
    {
        var normalized = request.Address.Trim().ToLowerInvariant();
        var domain = ExtractDomain(normalized);
        var id = await FindIdAsync("account", "emailAddress", normalized, cancellationToken) ?? normalized;
        // credentials map'i komple değiştirilir; tek Password kimliği panel modelidir.
        var payload = $"credentials={{\"1\":{{\"@type\":\"Password\",\"secret\":{JsonValue(request.Password)}}}}}";
        return await MutateAsync(domain, $"update account {Arg(id)} --field {CliJsonArg(payload)}", cancellationToken);
    }

    public async Task<MailWorkspaceDto> CreateAliasAsync(CreateMailAliasRequest request, CancellationToken cancellationToken = default)
    {
        var domain = request.Domain.Trim().ToLowerInvariant();
        var address = NormalizeAddress(request.Address, domain);
        var localPart = address[..address.IndexOf('@')];
        var target = NormalizeAddress(request.Target, domain);

        var domainId = await FindIdAsync("domain", "name", domain, cancellationToken)
            ?? throw new AppException("mail.operation_failed", 404, 404, $"domain not found: {domain}");
        var accountId = await FindIdAsync("account", "emailAddress", target, cancellationToken)
            ?? throw new AppException("mail.operation_failed", 404, 404, $"target account not found: {target}");

        var (cli, env) = await RequireCliAsync(cancellationToken);
        var detail = await GetObjectAsync(cli, "account", accountId, env, cancellationToken)
            ?? throw new AppException("mail.operation_failed", 500, 500);

        // Map anahtarları sayısal olmalı ("new" gibi anahtarlar invalidPatch verir).
        var entries = ExportAliasEntries(detail);
        entries.Add($"\"{NextNumericKey(detail)}\":{{\"name\":{JsonValue(localPart)},\"domainId\":{JsonValue(domainId)},\"enabled\":true}}");
        var payload = "aliases={" + string.Join(",", entries) + "}";
        return await MutateAsync(domain, $"update account {Arg(accountId)} --field {CliJsonArg(payload)}", cancellationToken);
    }

    public async Task<MailWorkspaceDto> DeleteAliasAsync(string address, CancellationToken cancellationToken = default)
    {
        var normalized = address.Trim().ToLowerInvariant();
        var domain = ExtractDomain(normalized);
        var localPart = normalized[..normalized.IndexOf('@')];
        var domainId = await FindIdAsync("domain", "name", domain, cancellationToken)
            ?? throw new AppException("mail.operation_failed", 404, 404, $"domain not found: {domain}");

        var (cli, env) = await RequireCliAsync(cancellationToken);

        // Alias'lar hesapların içinde tutulur; alias'ı barındıran hesabı bul ve map'ten çıkar.
        foreach (var brief in await QueryAsync(cli, "account", env, cancellationToken))
        {
            var accountId = GetField(brief, "id");
            if (string.IsNullOrWhiteSpace(accountId))
            {
                continue;
            }

            var detail = await GetObjectAsync(cli, "account", accountId, env, cancellationToken);
            if (detail is null)
            {
                continue;
            }

            var entries = ExportAliasEntries(detail.Value, skipName: localPart, skipDomainId: domainId);
            if (entries.Removed)
            {
                var payload = "aliases={" + string.Join(",", entries.Entries) + "}";
                return await MutateAsync(domain, $"update account {Arg(accountId)} --field {CliJsonArg(payload)}", cancellationToken);
            }
        }

        throw new AppException("mail.operation_failed", 404, 404, $"alias not found: {normalized}");
    }

    // --- Dağıtım listeleri (Stalwart mailing-list principal) ---

    public async Task<MailWorkspaceDto> CreateMailListAsync(CreateMailListRequest request, CancellationToken cancellationToken = default)
    {
        var domain = request.Domain.Trim().ToLowerInvariant();
        var address = NormalizeAddress(request.Address, domain);
        var localPart = address[..address.IndexOf('@')];
        var (cli, env) = await RequireCliAsync(cancellationToken);
        var domainId = await FindIdAsync("domain", "name", domain, cancellationToken)
            ?? throw new AppException("mail.operation_failed", 404, 404, $"domain not found: {domain}");

        // recipients EmailAddress[]; JSON --file ile geçirilir (komut satırı tırnak bozulmasına karşı).
        var payload =
            $"{{\"name\":{JsonValue(localPart)},\"domainId\":{JsonValue(domainId)}," +
            $"\"recipients\":{BuildRecipientsJson(request.Recipients, domain)}}}";
        var result = await RunCliJsonAsync(cli, "create mailing-list", payload, env, cancellationToken);
        if (!result.Success)
        {
            throw MapMailError(result);
        }

        return await GetWorkspaceAsync(domain, cancellationToken);
    }

    public async Task<MailWorkspaceDto> SetMailListRecipientsAsync(SetMailListRecipientsRequest request, CancellationToken cancellationToken = default)
    {
        var normalized = request.Address.Trim().ToLowerInvariant();
        var domain = ExtractDomain(normalized);
        var id = await FindIdAsync("mailing-list", "emailAddress", normalized, cancellationToken)
            ?? throw new AppException("mail.operation_failed", 404, 404, $"list not found: {normalized}");
        var payload = "recipients=" + BuildRecipientsJson(request.Recipients, domain);
        return await MutateAsync(domain, $"update mailing-list {Arg(id)} --field {CliJsonArg(payload)}", cancellationToken);
    }

    public async Task<MailWorkspaceDto> DeleteMailListAsync(string address, CancellationToken cancellationToken = default)
    {
        var normalized = address.Trim().ToLowerInvariant();
        var domain = ExtractDomain(normalized);
        var id = await FindIdAsync("mailing-list", "emailAddress", normalized, cancellationToken) ?? normalized;
        return await MutateAsync(domain, $"delete mailing-list --ids {Arg(id)}", cancellationToken);
    }

    public async Task<IReadOnlyCollection<MailListenerDto>> GetListenersAsync(CancellationToken cancellationToken = default)
    {
        var cli = await ResolveCliAsync(cancellationToken);
        var password = await GetAdminPasswordAsync(cancellationToken);
        if (cli is null || password is null)
        {
            return [];
        }

        var env = BuildEnvironment(password);
        var result = await RunCliAsync(cli, "query NetworkListener --json", env, cancellationToken);
        if (!result.Success)
        {
            return [];
        }

        var listeners = new List<MailListenerDto>();
        foreach (var item in ParseItems(result.StandardOutput))
        {
            var binds = new List<string>();
            if (item.TryGetProperty("bind", out var bind) && bind.ValueKind == JsonValueKind.Object)
            {
                binds.AddRange(bind.EnumerateObject().Select(property => property.Name));
            }

            listeners.Add(new MailListenerDto
            {
                Name = GetField(item, "name", "id"),
                Protocol = GetField(item, "protocol"),
                Binds = binds
            });
        }

        return listeners
            .OrderBy(listener => listener.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Alıcı adreslerini normalize edip JSON dizisine çevirir (boşları atar).
    private static string BuildRecipientsJson(IEnumerable<string> recipients, string domain)
    {
        var items = recipients
            .Select(value => (value ?? string.Empty).Trim())
            .Where(value => value.Length > 0)
            .Select(value => JsonValue(NormalizeAddress(value, domain)));
        return "[" + string.Join(",", items) + "]";
    }

    private static IReadOnlyCollection<string> ReadStringArray(JsonElement element, string field)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(field, out var array)
            && array.ValueKind == JsonValueKind.Array)
        {
            return array.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray();
        }

        return [];
    }

    public async Task<MailWorkspaceDto> SetAccountQuotaAsync(SetMailAccountQuotaRequest request, CancellationToken cancellationToken = default)
    {
        var normalized = request.Address.Trim().ToLowerInvariant();
        var domain = ExtractDomain(normalized);
        // panel-tarafı domain kotası denetimi: bu kutunun yeni kotası domain toplamını aşarsa reddet.
        await EnsureDomainAllocationAsync(domain, normalized, Math.Max(0, request.MaxSizeMB), cancellationToken);
        var id = await FindIdAsync("account", "emailAddress", normalized, cancellationToken) ?? normalized;
        // Stalwart'ta hesap kotası `quotas` HARİTASININ maxDiskQuota anahtarıdır (byte); tek tamsayılık
        // bir `quota` alanı YOKTUR. Şemadan doğrulandı ve canlı olarak ölçüldü: `--field quota=<byte>`
        // sunucudan "unknown field: quota" alıyor, `--field quotas/maxDiskQuota=<byte>` uygulanıyor.
        // Harita alanına JSON pointer yolu ile yazılır. 0 = sınırsız.
        var bytes = (long)Math.Max(0, request.MaxSizeMB) * 1024 * 1024;
        return await MutateAsync(domain, $"update account {Arg(id)} --field {AccountQuotaField}={bytes}", cancellationToken);
    }

    // --- Otomatik yanıt (Plesk autoresponder paritesi) ---
    // Stalwart'ta kullanıcı-sieve "vacation" TRUSTED bağlamda ÇALIŞMAZ (canlı doğrulandı:
    // sieve.not-supported/DuplicateId). Kanıtlanan yol: tek SISTEM sieve script'i (SieveSystemScript
    // "adapanel-autoreply") DATA aşamasına bağlanır (MtaStageData.script) ve enotify/mailto ile yanıt
    // üretir; RFC 3834 döngü korumaları (Auto-Submitted/Precedence/List-Id/boş-gönderen) script içindedir.
    // Ayarlar config/mail-autoresponders.json'da tutulur; değişiklik ReloadSettings ile ANINDA etkinleşir.
    // Bilinen sınırlar: gönderen-başına frekans kısıtı yok (duplicate trusted'da desteklenmez) ve
    // yanıtın From başlığı Stalwart'ın bildirim varsayılanıdır (mailto from parametresi RFC 5436 gereği yok sayılır).
    private const string AutoResponderStorePath = "config/mail-autoresponders.json";
    private const string AutoResponderScriptName = "adapanel-autoreply";

    private sealed class AutoResponderEntry
    {
        public bool Enabled { get; set; }
        public string Subject { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public async Task<MailAutoResponderDto> GetAutoResponderAsync(string address, CancellationToken cancellationToken = default)
    {
        var normalized = address.Trim().ToLowerInvariant();
        var store = await ReadAutoRespondersAsync(cancellationToken);
        return store.TryGetValue(normalized, out var entry)
            ? new MailAutoResponderDto { Address = normalized, Enabled = entry.Enabled, Subject = entry.Subject, Message = entry.Message }
            : new MailAutoResponderDto { Address = normalized, Enabled = false };
    }

    public async Task<MailAutoResponderDto> SetAutoResponderAsync(SetMailAutoResponderRequest request, CancellationToken cancellationToken = default)
    {
        var normalized = request.Address.Trim().ToLowerInvariant();
        if (request.Enabled && string.IsNullOrWhiteSpace(request.Message))
        {
            throw new AppException("mail.autoresponder_message_required", 400, 400);
        }

        // Hesap gerçekten var olmalı (hayalet adrese kural yazılmasın).
        _ = await FindIdAsync("account", "emailAddress", normalized, cancellationToken)
            ?? throw new AppException("mail.account_not_found", 404, 404);

        var store = await ReadAutoRespondersAsync(cancellationToken);
        if (request.Enabled)
        {
            store[normalized] = new AutoResponderEntry
            {
                Enabled = true,
                Subject = request.Subject.Trim(),
                Message = request.Message.Trim()
            };
        }
        else
        {
            store.Remove(normalized);
        }

        await fileStore.WriteAsync(AutoResponderStorePath, store, cancellationToken);
        await SyncAutoResponderScriptAsync(store, cancellationToken);

        return await GetAutoResponderAsync(normalized, cancellationToken);
    }

    private async Task<Dictionary<string, AutoResponderEntry>> ReadAutoRespondersAsync(CancellationToken cancellationToken)
    {
        var stored = await fileStore.ReadAsync<Dictionary<string, AutoResponderEntry>>(AutoResponderStorePath, new(), cancellationToken);
        return stored is null
            ? new Dictionary<string, AutoResponderEntry>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, AutoResponderEntry>(stored, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Mağazadaki TÜM aktif kayıtlardan tek sistem script'ini üretir, upsert eder, DATA aşamasına bağlar ve ayarları yeniden yükletir.</summary>
    private async Task SyncAutoResponderScriptAsync(Dictionary<string, AutoResponderEntry> store, CancellationToken cancellationToken)
    {
        var (cli, env) = await RequireCliAsync(cancellationToken);
        var contents = BuildAutoResponderScript(store);

        var payload = JsonSerializer.Serialize(new
        {
            name = AutoResponderScriptName,
            isActive = true,
            description = "AdaPanel otomatik yanit (panelden uretilir; elle duzenlemeyin)",
            contents
        });

        var scriptId = await FindIdAsync("SieveSystemScript", "name", AutoResponderScriptName, cancellationToken);
        var upsert = scriptId is null
            ? await RunCliJsonAsync(cli, "create SieveSystemScript", payload, env, cancellationToken)
            : await RunCliJsonAsync(cli, $"update SieveSystemScript {Arg(scriptId)}", payload, env, cancellationToken);
        if (!upsert.Success)
        {
            throw new AppException("mail.autoresponder_script_failed", 502, 502, upsert.StandardError.Trim());
        }

        // DATA aşamasına bağla (boş/false ise). Yönetici elle başka script bağladıysa DOKUNMA ve açıkça bildir.
        var stage = await GetObjectAsync(cli, "MtaStageData", "singleton", env, cancellationToken);
        var currentScript = stage is { } stageValue
            && stageValue.TryGetProperty("script", out var scriptExpr)
            && scriptExpr.TryGetProperty("else", out var elseValue)
                ? elseValue.GetString() ?? string.Empty
                : string.Empty;

        var ourExpression = $"'{AutoResponderScriptName}'";
        if (string.IsNullOrWhiteSpace(currentScript) || currentScript is "false" or "0")
        {
            var bind = await RunCliAsync(
                cli,
                $"update MtaStageData singleton --field {CliJsonArg($"script={{\"match\":{{}},\"else\":\"{ourExpression}\"}}")}",
                env,
                cancellationToken);
            if (!bind.Success)
            {
                throw new AppException("mail.autoresponder_script_failed", 502, 502, bind.StandardError.Trim());
            }
        }
        else if (!string.Equals(currentScript, ourExpression, StringComparison.Ordinal))
        {
            throw new AppException("mail.autoresponder_conflict", 409, 409, $"MtaStageData.script={currentScript}");
        }

        // Değişiklik yeniden başlatma OLMADAN etkinleşir (canlı doğrulandı: ReloadSettings yeterli).
        _ = await RunCliJsonAsync(cli, "create Action", "{\"@type\":\"ReloadSettings\"}", env, cancellationToken);
    }

    /// <summary>
    /// Sistem sieve script içeriği. Aktif kayıt yoksa yorum-satırı script (derlenir, hiçbir şey yapmaz;
    /// canlı doğrulandı). RFC 3834: listelere/otomatik iletilere/boş-göndericiye asla yanıt verilmez.
    /// </summary>
    private static string BuildAutoResponderScript(Dictionary<string, AutoResponderEntry> store)
    {
        var active = store
            .Where(pair => pair.Value.Enabled && !string.IsNullOrWhiteSpace(pair.Value.Message))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();

        if (active.Count == 0)
        {
            return "# AdaPanel: aktif otomatik yanit yok / no active auto-replies\r\n";
        }

        var builder = new StringBuilder();
        builder.AppendLine("require [\"envelope\", \"variables\", \"enotify\"];");
        builder.AppendLine("# AdaPanel tarafindan uretilir; elle duzenlemeyin. / Generated by AdaPanel; do not edit.");
        builder.AppendLine("if allof(");
        builder.AppendLine("    not exists [\"list-id\", \"list-unsubscribe\"],");
        builder.AppendLine("    not header :is \"precedence\" [\"bulk\", \"list\", \"junk\"],");
        builder.AppendLine("    not header :matches \"auto-submitted\" [\"auto-*\", \"yes\"]");
        builder.AppendLine(") {");
        builder.AppendLine("    set \"sender\" \"\";");
        builder.AppendLine("    if envelope :all :matches \"from\" \"*\" {");
        builder.AppendLine("        set \"sender\" \"${1}\";");
        builder.AppendLine("    }");
        builder.AppendLine("    if not string :is \"${sender}\" \"\" {");

        foreach (var (address, entry) in active)
        {
            var sieveAddress = SieveString(address);
            var subject = string.IsNullOrWhiteSpace(entry.Subject) ? "Otomatik yanit / Auto-reply" : entry.Subject;
            var uri = $"mailto:${{sender}}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(NormalizeNewlines(entry.Message))}";
            builder.AppendLine($"        if allof(envelope :all :is \"to\" \"{sieveAddress}\", not string :is \"${{sender}}\" \"{sieveAddress}\") {{");
            builder.AppendLine($"            notify \"{SieveString(uri)}\";");
            builder.AppendLine("        }");
        }

        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string SieveString(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n").Replace('\r', '\n');

    // panel-tarafı domain kotası: Stalwart domain kotası tutmaz; limit mail-domain-quotas.json'da saklanır ve
    // yeni kutu/kota işlemlerinde tahsis toplamıyla dayatılır. 0 = sınırsız (kayıttan silinir).
    public async Task<MailWorkspaceDto> SetDomainQuotaAsync(SetMailDomainQuotaRequest request, CancellationToken cancellationToken = default)
    {
        var domain = request.Domain.Trim().ToLowerInvariant();
        var limit = Math.Max(0, request.MaxSizeMB);

        // Limit, halihazırda tahsis edilenden küçük olamaz (aksi halde mevcut kutular kotayı aşmış olurdu).
        if (limit > 0)
        {
            var allocated = await GetDomainAllocatedMbAsync(domain, cancellationToken);
            if (allocated > limit)
            {
                throw new AppException("mail.domain_quota_below_allocated", 409, 409, $"allocated={allocated}MB");
            }
        }

        var quotas = await ReadDomainQuotasAsync(cancellationToken);
        if (limit == 0)
        {
            quotas.Remove(domain);
        }
        else
        {
            quotas[domain] = limit;
        }
        await fileStore.WriteAsync(DomainQuotaPath, quotas, cancellationToken);

        return await GetWorkspaceAsync(domain, cancellationToken);
    }

    // Domain kota haritası (domain→MB, büyük/küçük harf duyarsız). Boş = kota yok.
    private async Task<Dictionary<string, int>> ReadDomainQuotasAsync(CancellationToken cancellationToken)
    {
        var stored = await fileStore.ReadAsync<Dictionary<string, int>>(DomainQuotaPath, new(), cancellationToken);
        return stored is null
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(stored, StringComparer.OrdinalIgnoreCase);
    }

    // Bir domaindeki posta kutularına tahsis edilen toplam kota (MB). Sınırsız (0) kutular 0 sayılır.
    private async Task<int> GetDomainAllocatedMbAsync(string domain, CancellationToken cancellationToken)
    {
        var workspace = await GetWorkspaceAsync(domain, cancellationToken);
        return workspace.Accounts?.Sum(account => Math.Max(0, account.MaxSizeMB)) ?? 0;
    }

    // Yeni kutu/kota işlemi domain kotasını aşıyor mu? Aşıyorsa AppException fırlatır. excludeAddress: güncellenen kutu
    // (kendi eski tahsisi düşülür). requestedMb: yeni kota (0 = sınırsız → domain kotalıysa reddedilir).
    private async Task EnsureDomainAllocationAsync(string domain, string excludeAddress, int requestedMb, CancellationToken cancellationToken)
    {
        var quotas = await ReadDomainQuotasAsync(cancellationToken);
        if (!quotas.TryGetValue(domain, out var limit) || limit <= 0)
        {
            return; // domain kotası yok
        }

        if (requestedMb <= 0)
        {
            // Domain kotası varken sınırsız kutu, kotayı anlamsız kılar.
            throw new AppException("mail.domain_quota_needs_mailbox_limit", 409, 409);
        }

        var workspace = await GetWorkspaceAsync(domain, cancellationToken);
        var others = (workspace.Accounts ?? [])
            .Where(account => !string.Equals(account.Address, excludeAddress, StringComparison.OrdinalIgnoreCase))
            .Sum(account => Math.Max(0, account.MaxSizeMB));
        if (others + requestedMb > limit)
        {
            throw new AppException("mail.domain_quota_exceeded", 409, 409, $"limit={limit}MB used={others}MB add={requestedMb}MB");
        }
    }

    /// <summary>
    /// Domain için dış DNS sağlayıcısına eklenmesi gereken kayıtları üretir.
    /// DKIM public anahtarları Stalwart'tan (DkimSignature nesneleri) okunur;
    /// publicKey alanı zaten DNS'e uygun çıplak base64 değeridir.
    /// </summary>
    public async Task<MailDnsDto> GetDnsRecordsAsync(string domain, CancellationToken cancellationToken = default)
    {
        var normalized = domain.Trim().ToLowerInvariant();
        var (cli, env) = await RequireCliAsync(cancellationToken);
        var domainId = await FindIdAsync("domain", "name", normalized, cancellationToken)
            ?? throw new AppException("mail.operation_failed", 404, 404, $"domain not found: {normalized}");

        var preferences = await panelPreferencesService.GetAsync(cancellationToken);
        var serverAddress = preferences.ServerAddress?.Trim() ?? string.Empty;
        var mailHost = $"mail.{normalized}";

        var records = new List<MailDnsRecordDto>
        {
            new()
            {
                Type = "A",
                Host = "mail",
                Value = serverAddress,
                Purpose = "a"
            },
            new()
            {
                Type = "MX",
                Host = "@",
                Value = mailHost,
                Priority = 10,
                Purpose = "mx"
            },
            new()
            {
                Type = "TXT",
                Host = "@",
                Value = BuildSpfValue(serverAddress),
                Purpose = "spf"
            }
        };

        // Stalwart domain oluşturulurken RSA + Ed25519 DKIM anahtarlarını otomatik üretir.
        foreach (var brief in await QueryAsync(cli, "DkimSignature", env, cancellationToken))
        {
            if (!string.Equals(GetField(brief, "domainId"), domainId, StringComparison.Ordinal)
                || !string.Equals(GetField(brief, "stage"), "active", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = GetField(brief, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var detail = await GetObjectAsync(cli, "DkimSignature", id, env, cancellationToken);
            if (detail is null)
            {
                continue;
            }

            var selector = GetField(detail.Value, "selector");
            var publicKey = GetField(detail.Value, "publicKey");
            if (string.IsNullOrWhiteSpace(selector) || string.IsNullOrWhiteSpace(publicKey))
            {
                continue;
            }

            var algorithm = GetField(detail.Value, "@type").Contains("Ed25519", StringComparison.OrdinalIgnoreCase)
                ? "ed25519"
                : "rsa";
            records.Add(new MailDnsRecordDto
            {
                Type = "TXT",
                Host = $"{selector}._domainkey",
                Value = $"v=DKIM1; k={algorithm}; p={publicKey}",
                Purpose = "dkim"
            });
        }

        records.Add(new MailDnsRecordDto
        {
            Type = "TXT",
            Host = "_dmarc",
            Value = $"v=DMARC1; p=quarantine; rua=mailto:postmaster@{normalized}",
            Purpose = "dmarc"
        });

        return new MailDnsDto
        {
            Domain = normalized,
            MailHost = mailHost,
            ServerAddress = serverAddress,
            Records = records.ToArray()
        };
    }

    private static string BuildSpfValue(string serverAddress)
    {
        // Sunucu adresi IPv4 ise SPF'e açıkça eklenir; aksi halde mx+a mekanizmaları yeterli.
        return System.Net.IPAddress.TryParse(serverAddress, out var ip)
            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? $"v=spf1 mx a ip4:{serverAddress} ~all"
            : "v=spf1 mx a ~all";
    }

    public async Task<MailDiagnosticsDto> GetDiagnosticsAsync(string domain, CancellationToken cancellationToken = default)
        => await MailDiagnosticsRunner.RunAsync(await GetDnsRecordsAsync(domain, cancellationToken), cancellationToken);

    public async Task<MailCertificateDto> GetCertificateStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = await fileStore.ReadAsync<MailCertificateState?>(CertificateStatePath, null, cancellationToken);
        var savedContact = await fileStore.ReadAsync<Support.AcmeContactSettings?>(Support.AcmeContactSettings.Path, null, cancellationToken);
        var suggestedEmail = savedContact?.Email?.Trim() ?? string.Empty;
        return state is null
            ? new MailCertificateDto { Active = false, SuggestedContactEmail = suggestedEmail }
            : new MailCertificateDto
            {
                Active = true,
                Domain = state.Domain,
                HostName = state.HostName,
                Source = state.Source,
                NotAfterUtc = state.NotAfterUtc,
                SuggestedContactEmail = suggestedEmail
            };
    }

    /// <summary>
    /// Mail hostname'i için sertifika alır ve Stalwart'a yükler:
    /// 1) Domain panelde IIS sitesi olarak varsa win-acme (pemfiles store) ile Let's Encrypt,
    /// 2) aksi halde / başarısızlıkta hostname'e özel self-signed sertifika.
    /// Yükleme sonrası Stalwart "ReloadTlsCertificates" aksiyonuyla canlı devreye alınır.
    /// </summary>
    public async Task<MailCertificateDto> IssueCertificateAsync(IssueMailCertificateRequest request, CancellationToken cancellationToken = default)
    {
        var domain = request.Domain.Trim().ToLowerInvariant();
        var hostName = string.IsNullOrWhiteSpace(request.HostName)
            ? $"mail.{domain}"
            : request.HostName.Trim().ToLowerInvariant();

        var pemDirectory = fileStore.EnsureDirectory($"mail-certs/{hostName}");
        var source = "self-signed";

        // İletişim e-postası boşsa, daha önce (site SSL'i ya da mail sertifikası alınırken)
        // kaydedilmiş paylaşılan e-postayı kullan → kullanıcı tekrar girmek zorunda kalmaz.
        var contactEmail = request.ContactEmail?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(contactEmail))
        {
            var saved = await fileStore.ReadAsync<Support.AcmeContactSettings?>(Support.AcmeContactSettings.Path, null, cancellationToken);
            contactEmail = saved?.Email?.Trim() ?? string.Empty;
        }

        var issued = await TryIssueWithWinAcmeAsync(domain, hostName, contactEmail, request.UseStaging, pemDirectory, cancellationToken);
        if (issued)
        {
            source = "letsencrypt";
            if (!string.IsNullOrWhiteSpace(contactEmail))
            {
                await fileStore.WriteAsync(
                    Support.AcmeContactSettings.Path,
                    new Support.AcmeContactSettings { Email = contactEmail },
                    cancellationToken);
            }
        }
        else
        {
            WriteSelfSignedPem(hostName, pemDirectory);
        }

        var (certificatePem, privateKeyPem) = ReadPemPair(pemDirectory);
        var notAfter = TryGetNotAfterUtc(certificatePem);
        await PushCertificateToStalwartAsync(hostName, certificatePem, privateKeyPem, cancellationToken);

        var state = new MailCertificateState
        {
            Domain = domain,
            HostName = hostName,
            Source = source,
            PemDirectory = pemDirectory,
            ContentHash = ComputeHash(certificatePem + privateKeyPem),
            NotAfterUtc = notAfter
        };
        await fileStore.WriteAsync(CertificateStatePath, state, cancellationToken);

        return new MailCertificateDto
        {
            Active = true,
            Domain = domain,
            HostName = hostName,
            Source = source,
            NotAfterUtc = notAfter
        };
    }

    public async Task<MailCertificateDto?> SyncCertificateAsync(CancellationToken cancellationToken = default)
    {
        var state = await fileStore.ReadAsync<MailCertificateState?>(CertificateStatePath, null, cancellationToken);
        if (state is null
            || !string.Equals(state.Source, "letsencrypt", StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(state.PemDirectory))
        {
            return null;
        }

        var (certificatePem, privateKeyPem) = ReadPemPair(state.PemDirectory);
        var hash = ComputeHash(certificatePem + privateKeyPem);
        if (string.Equals(hash, state.ContentHash, StringComparison.Ordinal))
        {
            return null;
        }

        // win-acme zamanlanmış görevi sertifikayı yenilemiş; Stalwart'a yeniden bas.
        await PushCertificateToStalwartAsync(state.HostName, certificatePem, privateKeyPem, cancellationToken);
        state.ContentHash = hash;
        state.NotAfterUtc = TryGetNotAfterUtc(certificatePem);
        await fileStore.WriteAsync(CertificateStatePath, state, cancellationToken);

        return new MailCertificateDto
        {
            Active = true,
            Domain = state.Domain,
            HostName = state.HostName,
            Source = state.Source,
            NotAfterUtc = state.NotAfterUtc
        };
    }

    public async Task<bool> EnsureServerHostnameAsync(bool applyChanges = true, CancellationToken cancellationToken = default)
    {
        var state = await fileStore.ReadAsync<MailCertificateState?>(CertificateStatePath, null, cancellationToken);
        var hostName = state?.HostName?.Trim().ToLowerInvariant();

        var (cli, env) = await RequireCliAsync(cancellationToken);
        var settings = await GetObjectAsync(cli, "SystemSettings", "singleton", env, cancellationToken);
        if (settings is null)
        {
            return false;
        }

        var currentHost = GetField(settings.Value, "defaultHostname") ?? string.Empty;
        var currentDomainId = GetField(settings.Value, "defaultDomainId") ?? string.Empty;

        // SARKAN REFERANS ONARIMI.
        //
        // defaultDomainId silinmis bir alan adina isaret ediyor olabilir (ilk kurulan alan
        // silindiginde oluyor). Guncelleme TUM nesneyi yeniden dogruladigi icin bu sarkan referans
        // SystemSettings uzerindeki HER degisikligi engeller ve hata mesaji ("defaultDomainId:
        // required") sebebi hic anlatmaz. Bu yuzden once gecerli bir alana onariliyor.
        var domainIds = (await QueryAsync(cli, "domain", env, cancellationToken))
            .Select(item => GetField(item, "id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();

        if (domainIds.Count == 0)
        {
            return false;
        }

        var domainId = domainIds.Contains(currentDomainId, StringComparer.Ordinal)
            ? currentDomainId
            : domainIds[0]!;

        // Sertifika panelden alinmamissa (kendinden imzali kurulumlar, elle kurulum) hedef adi
        // SUNUCUNUN KENDI varsayilan alanindan turet: "mail.<alan>". Boylece ozellik yalnizca
        // sertifikayi panelden alanlarda degil, HER kurulumda calisir. Makine adini tahmin etmek
        // yerine yapilandirmadan turetmek, yanlis bir adi kalicilastirma riskini de ortadan kaldirir.
        if (string.IsNullOrWhiteSpace(hostName))
        {
            // YALNIZ SUNUCUNUN KENDI varsayilan alani kullanilir; "sorgudan gelen ilk alan" DEGIL.
            //
            // Sorgu sirasi garantili degildir: sarkan bir defaultDomainId varsa ilk sirada BASKA
            // BIR MUSTERININ alan adi gelebilir ve sunucu kendini onun adiyla tanitir. Bu hem
            // yanlis hem de PTR ile tutmayacagi icin teslimati bozar. Referans gecerli degilse
            // karsilama adi ayarlanmaz -- yanlis bir adi kalicilastirmaktansa dokunmamak dogrudur.
            if (!domainIds.Contains(currentDomainId, StringComparer.Ordinal))
            {
                logger.LogInformation(
                    "Posta karsilama adi turetilemedi: varsayilan alan referansi gecersiz. " +
                    "Posta sertifikasi alindiginda ad kendiliginden ayarlanacak.");
                return false;
            }

            var domain = await GetObjectAsync(cli, "domain", domainId, env, cancellationToken);
            var domainName = domain is null ? null : GetField(domain.Value, "name");
            if (string.IsNullOrWhiteSpace(domainName))
            {
                return false;
            }

            hostName = $"mail.{domainName.Trim().ToLowerInvariant()}";
        }

        if (string.Equals(currentHost, hostName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(domainId, currentDomainId, StringComparison.Ordinal))
        {
            return false;
        }

        // DENETIM MODU: degisiklik GEREKTIGINI bildirir ama hicbir sey yazmaz.
        //
        // Bu sart: eksikligi olcmeyen bir denetim, onarma dugmesinin hic cikmamasina ve sorunun
        // kalici olmasina yol aciyordu -- karsilama adi yanlisti, onarma bunu duzeltebiliyordu,
        // ama panel eksigi gormedigi icin kullaniciya hic soylemedi.
        if (!applyChanges)
        {
            return true;
        }

        // Yamada "id" alani BULUNMAMALI: sunucu onu "invalidPatch" ile reddediyor.
        var payload = $"{{\"defaultHostname\":{JsonValue(hostName)},\"defaultDomainId\":{JsonValue(domainId)}}}";
        var result = await RunCliJsonAsync(cli, "update SystemSettings", payload, env, cancellationToken);
        if (!result.Success)
        {
            logger.LogWarning("Posta sunucusunun karsilama adi ayarlanamadi: {Error}", CombineError(result));
            return false;
        }

        // AYARLARI YENIDEN OKUT.
        //
        // Stalwart ayarlari BELLEKTE tutar; yazmak tek basina karsilamayi degistirmez. Canli
        // sunucuda ad dogru yazildigi halde karsilama "vm-185-7-242-225" kalmaya devam etmesinin
        // sebebi buydu -- eksik olan tek sey bu cagriydi.
        //
        // Yerelde olculdu: defaultHostname degistirilip ReloadSettings gonderildiginde karsilama
        // aninda yeni adi gosteriyor.
        await RunCliJsonAsync(cli, "create Action", "{\"@type\":\"ReloadSettings\"}", env, cancellationToken);

        logger.LogInformation("Posta sunucusunun karsilama adi '{HostName}' olarak ayarlandi.", hostName);
        return true;
    }

    private async Task<bool> TryIssueWithWinAcmeAsync(
        string domain,
        string hostName,
        string contactEmail,
        bool useStaging,
        string pemDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(contactEmail))
        {
            return false;
        }

        var wacs = await ResolveComponentExecutableAsync("win-acme", "wacs.exe", cancellationToken);
        if (wacs is null)
        {
            return false;
        }

        Core.Dtos.Sites.SiteDto site;
        try
        {
            site = await iisSiteService.GetSiteAsync(domain, cancellationToken);
        }
        catch
        {
            // Domain panelde site olarak yönetilmiyorsa HTTP-01 doğrulaması yapılamaz.
            return false;
        }

        // HTTP-01 doğrulamasının mail hostname'ine ulaşabilmesi için sitede 80 portuna
        // hostname bağlaması bulunmalı; yoksa eklenir (varsa AddBinding sessizce çakışma döner).
        try
        {
            await iisSiteService.AddBindingAsync(site.Name, new Core.Dtos.Sites.SiteBindingRequest
            {
                Protocol = "http",
                HostName = hostName,
                IpAddress = "*",
                Port = 80
            }, cancellationToken);
        }
        catch
        {
            // Bağlama zaten varsa ya da eklenemediyse win-acme yine de denenir.
        }

        var baseUri = useStaging
            ? "https://acme-staging-v02.api.letsencrypt.org/"
            : "https://acme-v02.api.letsencrypt.org/";
        var args =
            $"--target iis --siteid {site.Id} --host {hostName} --installation none " +
            $"--store pemfiles --pemfilespath \"{pemDirectory}\" " +
            $"--emailaddress {contactEmail} --accepttos --baseuri {baseUri}";
        var result = await ProcessRunner.RunAsync(wacs, args, cancellationToken: cancellationToken);

        return result.Success && Directory.EnumerateFiles(pemDirectory, "*-key.pem").Any();
    }

    private static void WriteSelfSignedPem(string hostName, string pemDirectory)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            $"CN={hostName}",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var sanBuilder = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(hostName);
        request.CertificateExtensions.Add(sanBuilder.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));

        foreach (var stale in Directory.EnumerateFiles(pemDirectory, "*.pem"))
        {
            File.Delete(stale);
        }

        File.WriteAllText(Path.Combine(pemDirectory, $"{hostName}-crt.pem"), certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(pemDirectory, $"{hostName}-key.pem"), rsa.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>
    /// pemfiles dizininden anahtar + tam zincir okur. win-acme sürümüne göre dosya adları
    /// değişebildiğinden desenle aranır: *-key.pem anahtar, *-crt.pem yaprak, *-chain.pem ara zincir.
    /// </summary>
    private static (string CertificatePem, string PrivateKeyPem) ReadPemPair(string pemDirectory)
    {
        var keyFile = Directory.EnumerateFiles(pemDirectory, "*-key.pem")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
            ?? throw new AppException("mail.certificate_files_missing", 500, 500, pemDirectory);

        // SIRA ÖNEMLİ: yaprak önce, ara sertifikalar sonra. TLS bunu şart koşar.
        //
        // Neden dosyaları birleştirip TEKİLLEŞTİRİYORUZ: win-acme sürümüne göre -chain.pem bazen
        // yalnız ara sertifikaları, bazen yaprak DAHİL tam zinciri içerir. Önceki kod "yaprak
        // yinelenmesin" diye bakıp zinciri içinde yaprağı görünce zincirin TAMAMINI atıyordu —
        // ara sertifika da gidiyordu. Sonuç: sunucu yalnız yaprağı sunuyor, OpenSSL tabanlı
        // istemciler (PHP, Python, çoğu posta istemcisi) zinciri kuramayıp bağlantıyı kesiyordu.
        // Windows istemcileri eksik ara sertifikayı kendi indirdiği için sorun uzun süre GÖRÜNMEZ
        // kalır; bu yüzden metinsel karşılaştırma değil, blok bazında tekilleştirme yapılıyor.
        var blocks = new List<string>();

        void Topla(string? file)
        {
            if (file is null)
            {
                return;
            }

            foreach (var block in ExtractCertificateBlocks(File.ReadAllText(file)))
            {
                if (!blocks.Contains(block, StringComparer.Ordinal))
                {
                    blocks.Add(block);
                }
            }
        }

        string? Sonuncu(string pattern) => Directory.EnumerateFiles(pemDirectory, pattern)
            .Where(file => !file.EndsWith("-chain-only.pem", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        Topla(Sonuncu("*-crt.pem"));
        Topla(Sonuncu("*-chain.pem"));
        // Yalnız ara sertifikaları içeren dosya: -chain.pem hiç üretilmediyse tek zincir kaynağıdır.
        Topla(Directory.EnumerateFiles(pemDirectory, "*-chain-only.pem")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault());

        if (blocks.Count == 0)
        {
            throw new AppException("mail.certificate_files_missing", 500, 500, pemDirectory);
        }

        return (string.Join("\n", blocks) + "\n", File.ReadAllText(keyFile));
    }

    /// <summary>
    /// Bir PEM metnindeki sertifika bloklarını sırayla çıkarır ve satır sonlarını normalleştirir.
    /// <para>
    /// Normalleştirme tekilleştirme için şart: aynı sertifika bir dosyada CRLF, diğerinde LF ile
    /// yazılmış olabilir ve ham metin karşılaştırması onları FARKLI sanar — o zaman yaprak zincire
    /// ikinci kez eklenir ve bazı istemciler bunu bozuk zincir sayar.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> ExtractCertificateBlocks(string pem)
    {
        const string begin = "-----BEGIN CERTIFICATE-----";
        const string end = "-----END CERTIFICATE-----";

        var blocks = new List<string>();
        var index = 0;
        while (true)
        {
            var start = pem.IndexOf(begin, index, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var stop = pem.IndexOf(end, start, StringComparison.Ordinal);
            if (stop < 0)
            {
                break;
            }

            var raw = pem[start..(stop + end.Length)];
            var lines = raw
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0);

            blocks.Add(string.Join("\n", lines));
            index = stop + end.Length;
        }

        return blocks;
    }

    private async Task PushCertificateToStalwartAsync(string hostName, string certificatePem, string privateKeyPem, CancellationToken cancellationToken)
    {
        var (cli, env) = await RequireCliAsync(cancellationToken);

        var payload =
            $"{{\"certificate\":{{\"@type\":\"Text\",\"value\":{JsonValue(certificatePem)}}}," +
            $"\"privateKey\":{{\"@type\":\"Text\",\"secret\":{JsonValue(privateKeyPem)}}}}}";

        // Aynı hostname için mevcut sertifika varsa güncelle, yoksa oluştur.
        string? existingId = null;
        foreach (var item in await QueryAsync(cli, "Certificate", env, cancellationToken))
        {
            if (item.TryGetProperty("subjectAlternativeNames", out var sans)
                && sans.ValueKind == JsonValueKind.Object
                && sans.EnumerateObject().Any(san => string.Equals(san.Name, hostName, StringComparison.OrdinalIgnoreCase)))
            {
                existingId = GetField(item, "id");
                break;
            }
        }

        // Sertifika PEM'i çok satırlı; inline --json argümanı Windows'ta bozulur. JSON --file
        // ile geçirilir (account create ile aynı sağlam yol).
        var result = existingId is null
            ? await RunCliJsonAsync(cli, "create Certificate", payload, env, cancellationToken)
            : await RunCliJsonAsync(cli, $"update Certificate {Arg(existingId)}", payload, env, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("mail.certificate_push_failed", 500, 500, CombineError(result));
        }

        // Sertifikaları yeniden başlatma olmadan canlı yükle (tagged-union → @type JSON ile).
        await RunCliJsonAsync(cli, "create Action", "{\"@type\":\"ReloadTlsCertificates\"}", env, cancellationToken);
    }

    private async Task<string?> ResolveComponentExecutableAsync(string componentId, string executableName, CancellationToken cancellationToken)
    {
        var component = await dbContext.InstalledComponents
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.ComponentId == componentId && item.Status == ComponentInstallStatus.Installed, cancellationToken);

        if (component is null || string.IsNullOrWhiteSpace(component.InstallDir) || !Directory.Exists(component.InstallDir))
        {
            return null;
        }

        return Directory.EnumerateFiles(component.InstallDir, executableName, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static DateTime? TryGetNotAfterUtc(string certificatePem)
    {
        try
        {
            using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(certificatePem);
            return certificate.NotAfter.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private static string ComputeHash(string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

    private sealed class MailCertificateState
    {
        public string Domain { get; set; } = string.Empty;

        public string HostName { get; set; } = string.Empty;

        public string Source { get; set; } = string.Empty;

        public string PemDirectory { get; set; } = string.Empty;

        public string ContentHash { get; set; } = string.Empty;

        public DateTime? NotAfterUtc { get; set; }
    }

    /// <summary>
    /// Stalwart global spam ayarları (SpamSettings singleton): işaretleme/ret/silme
    /// eşikleri, greylisting süresi ve güven kuralları. SPF/DKIM/DMARC doğrulaması
    /// Stalwart tarafından otomatik yapılır; burada yönetilmez.
    /// </summary>
    public async Task<MailSpamSettingsDto> GetSpamSettingsAsync(CancellationToken cancellationToken = default)
    {
        var cli = await ResolveCliAsync(cancellationToken);
        if (cli is null)
        {
            return new MailSpamSettingsDto { Installed = false };
        }

        var password = await GetAdminPasswordAsync(cancellationToken);
        if (password is null)
        {
            return new MailSpamSettingsDto { Installed = true, Configured = false };
        }

        var result = await RunCliAsync(cli, "get SpamSettings --json", BuildEnvironment(password), cancellationToken);
        if (!result.Success)
        {
            return new MailSpamSettingsDto { Installed = true, Configured = false };
        }

        var items = ParseItems(result.StandardOutput);
        if (items.Count == 0)
        {
            return new MailSpamSettingsDto { Installed = true, Configured = false };
        }

        var settings = items[0];
        // greylistFor milisaniye cinsindendir; null = greylist kapalı (canlı doğrulandı).
        var greylistMs = GetLongField(settings, "greylistFor");
        return new MailSpamSettingsDto
        {
            Installed = true,
            Configured = true,
            Enabled = GetBoolField(settings, "enable"),
            ScoreSpam = GetDoubleField(settings, "scoreSpam"),
            ScoreReject = GetDoubleField(settings, "scoreReject"),
            ScoreDiscard = GetDoubleField(settings, "scoreDiscard"),
            GreylistEnabled = greylistMs > 0,
            GreylistMinutes = greylistMs > 0 ? (int)(greylistMs / 60000) : 30,
            TrustContacts = GetBoolField(settings, "trustContacts"),
            TrustReplies = GetBoolField(settings, "trustReplies")
        };
    }

    public async Task<MailSpamSettingsDto> UpdateSpamSettingsAsync(UpdateMailSpamSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var (cli, env) = await RequireCliAsync(cancellationToken);

        var greylistValue = request.GreylistEnabled
            ? ((long)Math.Clamp(request.GreylistMinutes, 1, 7 * 24 * 60) * 60000).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "null";
        var arguments =
            "update SpamSettings" +
            $" --field enable={(request.Enabled ? "true" : "false")}" +
            $" --field scoreSpam={FormatScore(request.ScoreSpam)}" +
            $" --field scoreReject={FormatScore(request.ScoreReject)}" +
            $" --field scoreDiscard={FormatScore(request.ScoreDiscard)}" +
            $" --field greylistFor={greylistValue}" +
            $" --field trustContacts={(request.TrustContacts ? "true" : "false")}" +
            $" --field trustReplies={(request.TrustReplies ? "true" : "false")}";

        var result = await RunCliAsync(cli, arguments, env, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("mail.operation_failed", 500, 500, CombineError(result));
        }

        return await GetSpamSettingsAsync(cancellationToken);
    }

    private static string FormatScore(double value) =>
        Math.Clamp(value, 0, 1000).ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture);

    private static double GetDoubleField(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
            {
                return value.GetDouble();
            }
        }

        return 0;
    }

    private async Task<MailWorkspaceDto> MutateAsync(string? domain, string arguments, CancellationToken cancellationToken)
    {
        var cli = await ResolveCliAsync(cancellationToken)
            ?? throw new AppException("mail.not_installed", 409, 409);
        var password = await GetAdminPasswordAsync(cancellationToken)
            ?? throw new AppException("mail.not_configured", 409, 409);

        var result = await RunCliAsync(cli, arguments, BuildEnvironment(password), cancellationToken);
        if (!result.Success)
        {
            throw MapMailError(result);
        }

        return await GetWorkspaceAsync(domain, cancellationToken);
    }

    private async Task<IReadOnlyList<JsonElement>> QueryAsync(string cli, string type, IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken)
    {
        var result = await RunCliAsync(cli, $"query {type} --json", env, cancellationToken);
        return result.Success ? ParseItems(result.StandardOutput) : [];
    }

    private async Task<JsonElement?> GetObjectAsync(string cli, string type, string id, IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken)
    {
        var result = await RunCliAsync(cli, $"get {type} {Arg(id)} --json", env, cancellationToken);
        if (!result.Success)
        {
            return null;
        }

        var items = ParseItems(result.StandardOutput);
        return items.Count > 0 ? items[0] : null;
    }

    private async Task<(string Cli, Dictionary<string, string> Environment)> RequireCliAsync(CancellationToken cancellationToken)
    {
        var cli = await ResolveCliAsync(cancellationToken)
            ?? throw new AppException("mail.not_installed", 409, 409);
        var password = await GetAdminPasswordAsync(cancellationToken)
            ?? throw new AppException("mail.not_configured", 409, 409);
        return (cli, BuildEnvironment(password));
    }

    // Hesabın mevcut alias map girdilerini "anahtar":{...} parçaları olarak dışa aktarır.
    private static List<string> ExportAliasEntries(JsonElement account)
    {
        var entries = new List<string>();
        if (account.TryGetProperty("aliases", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in map.EnumerateObject())
            {
                entries.Add($"{JsonValue(entry.Name)}:{entry.Value.GetRawText()}");
            }
        }

        return entries;
    }

    // aliases map'ine eklenecek yeni girdi için kullanılmayan sayısal anahtar üretir.
    private static int NextNumericKey(JsonElement account)
    {
        var next = 0;
        if (account.TryGetProperty("aliases", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in map.EnumerateObject())
            {
                if (int.TryParse(entry.Name, out var key) && key >= next)
                {
                    next = key + 1;
                }
            }
        }

        return next;
    }

    private static (List<string> Entries, bool Removed) ExportAliasEntries(JsonElement account, string skipName, string skipDomainId)
    {
        var entries = new List<string>();
        var removed = false;
        if (account.TryGetProperty("aliases", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in map.EnumerateObject())
            {
                if (string.Equals(GetField(entry.Value, "name"), skipName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(GetField(entry.Value, "domainId"), skipDomainId, StringComparison.Ordinal))
                {
                    removed = true;
                    continue;
                }

                entries.Add($"{JsonValue(entry.Name)}:{entry.Value.GetRawText()}");
            }
        }

        return (entries, removed);
    }

    private async Task<string?> FindIdAsync(string type, string field, string value, CancellationToken cancellationToken)
    {
        var cli = await ResolveCliAsync(cancellationToken);
        var password = await GetAdminPasswordAsync(cancellationToken);
        if (cli is null || password is null)
        {
            return null;
        }

        var items = await QueryAsync(cli, type, BuildEnvironment(password), cancellationToken);
        foreach (var item in items)
        {
            if (string.Equals(GetField(item, field), value, StringComparison.OrdinalIgnoreCase))
            {
                var id = GetField(item, "id");
                return string.IsNullOrWhiteSpace(id) ? null : id;
            }
        }

        return null;
    }

    // stalwart-cli her zaman bir TIMEOUT ile çalıştırılır: Stalwart yarı-ayaktaysa (süreç var
    // ama 8080 dinlemiyor) CLI sonsuza kadar asılabilir ve mail isteğini (dolayısıyla mail
    // sayfasını) süresiz boş/spinner'da bırakabilir. 30 sn sonra başarısız döner → panel "mail
    // sunucusuna bağlanılamadı" durumuna geçer, asılı kalmaz.
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(30);

    private static async Task<CommandResult> RunCliAsync(string cli, string arguments, IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken)
    {
        return await ProcessRunner.RunAsync(cli, arguments, cancellationToken: cancellationToken, environmentVariables: env, timeout: CliTimeout);
    }

    // Karmaşık JSON payload'u geçici dosyaya yazıp stalwart-cli'ye --file ile verir. Komut
    // satırında inline JSON (--json "<escaped>") Windows CommandLineToArgvW kurallarıyla
    // bozulabildiğinden create/update gibi tam-payload işlemlerinde bu yol kullanılır.
    private static async Task<CommandResult> RunCliJsonAsync(string cli, string command, string json, IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ada-stalwart-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tempFile, json, cancellationToken);
        try
        {
            return await RunCliAsync(cli, $"{command} --file {Arg(tempFile)}", env, cancellationToken);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* en iyi çaba */ }
        }
    }

    private static Dictionary<string, string> BuildEnvironment(string password) => new()
    {
        ["STALWART_URL"] = ManagementUrl,
        ["STALWART_USER"] = AdminUser,
        ["STALWART_PASSWORD"] = password
    };

    private async Task<string?> ResolveCliAsync(CancellationToken cancellationToken)
    {
        // 1) Panel üzerinden kurulan bileşenin dizini.
        var component = await dbContext.InstalledComponents
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.ComponentId == CliComponentId && item.Status == ComponentInstallStatus.Installed, cancellationToken);

        if (component is not null && !string.IsNullOrWhiteSpace(component.InstallDir) && Directory.Exists(component.InstallDir))
        {
            var found = Directory.EnumerateFiles(component.InstallDir, "stalwart-cli.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (found is not null)
            {
                return found;
            }
        }

        // 2) Varsayılan kurulum kökü.
        var defaultRoot = Environment.ExpandEnvironmentVariables("%ProgramData%\\AdaPanel\\runtimes\\stalwart-cli");
        if (Directory.Exists(defaultRoot))
        {
            var found = Directory.EnumerateFiles(defaultRoot, "stalwart-cli.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (found is not null)
            {
                return found;
            }
        }

        // 3) PATH üzerinden.
        try
        {
            var probe = await ProcessRunner.RunAsync("stalwart-cli", "--version", cancellationToken: cancellationToken);
            if (probe.Success || probe.ExitCode == 0)
            {
                return "stalwart-cli";
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // bulunamadı
        }

        return null;
    }

    private async Task<string?> GetAdminPasswordAsync(CancellationToken cancellationToken)
    {
        var config = await fileStore.ReadAsync<MailConfig?>(ConfigPath, null, cancellationToken);
        if (config is not null && !string.IsNullOrWhiteSpace(config.AdminPasswordProtected))
        {
            try
            {
                return secretProtector.Unprotect(config.AdminPasswordProtected);
            }
            catch
            {
                // bileşen parolasına düş
            }
        }

        var component = await dbContext.InstalledComponents
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.ComponentId == ServerComponentId && item.Status == ComponentInstallStatus.Installed, cancellationToken);

        if (component is null || string.IsNullOrWhiteSpace(component.ProtectedConfigurationJson))
        {
            return null;
        }

        try
        {
            var payload = secretProtector.Unprotect(component.ProtectedConfigurationJson);
            var configuration = JsonSerializer.Deserialize<Dictionary<string, string>>(payload, SerializerOptions);
            return configuration is not null && configuration.TryGetValue(GeneratedPasswordKey, out var generated) ? generated : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<JsonElement> ParseItems(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var trimmed = json.TrimStart();

        // stalwart-cli --json çıktısı NDJSON'dur: satır başına bir JSON nesnesi.
        if (trimmed.StartsWith('{'))
        {
            var items = new List<JsonElement>();
            foreach (var line in json.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!line.StartsWith('{'))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    items.Add(document.RootElement.Clone());
                }
                catch (JsonException)
                {
                    // bozuk satırı atla
                }
            }

            // Tek satırlık {items:[...]} sarmalayıcı biçimine de dayanıklı kal.
            if (items.Count == 1 && items[0].ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "items", "data", "records", "results" })
                {
                    if (items[0].TryGetProperty(key, out var inner) && inner.ValueKind == JsonValueKind.Array)
                    {
                        return inner.EnumerateArray().Select(element => element.Clone()).ToArray();
                    }
                }
            }

            return items;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Select(element => element.Clone()).ToArray()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string GetField(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value))
            {
                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString() ?? string.Empty,
                    JsonValueKind.Number => value.ToString(),
                    _ => string.Empty
                };
            }
        }

        return string.Empty;
    }

    private static bool GetBoolField(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value))
            {
                return value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => !string.Equals(value.GetString(), "false", StringComparison.OrdinalIgnoreCase),
                    _ => true
                };
            }
        }

        return true;
    }

    /// <summary>
    /// Hesabın disk kotasını (byte) okur. Kota, üst düzey bir alan değil <c>quotas</c> haritasının
    /// içindedir; üst düzeyde <c>quota</c> aramak her hesabı "kotasız" gösteriyordu.
    /// </summary>
    internal static long ReadAccountQuotaBytes(JsonElement account) =>
        account.ValueKind == JsonValueKind.Object &&
        account.TryGetProperty("quotas", out var quotas) &&
        quotas.ValueKind == JsonValueKind.Object
            ? GetLongField(quotas, "maxDiskQuota")
            : 0;

    private static long GetLongField(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    // Değeri JSON string/number literal'ine çevirir (tırnaklama + kaçışlar dahil).
    private static string JsonValue(string value) => JsonSerializer.Serialize(value);

    // Komut satırında JSON taşıyan tek argüman: iç tırnaklar MSVC kuralıyla kaçırılır.
    private static string CliJsonArg(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string NormalizeAddress(string address, string domain)
    {
        var trimmed = address.Trim().ToLowerInvariant();
        return trimmed.Contains('@') ? trimmed : $"{trimmed}@{domain}";
    }

    private static string ExtractDomain(string address)
    {
        var index = address.IndexOf('@');
        if (index < 0 || index == address.Length - 1)
        {
            throw new AppException("mail.invalid_address", 400, 400);
        }

        return address[(index + 1)..];
    }

    private static string CombineError(CommandResult result) =>
        string.Join(Environment.NewLine, new[] { result.StandardError, result.StandardOutput }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    // Stalwart CLI hatasını anlamlı bir mesaj anahtarına çevirir. En sık karşılaşılan durum
    // ZAYIF PAROLA: Stalwart varsayılanı min 8 karakter + zxcvbn güç seviyesi 3 ister; "123123"
    // gibi kısa/yaygın parolalar reddedilir. Kullanıcıya generic "tamamlanamadı" yerine net sebep
    // gösterilir (gerçek CLI çıktısı yine Details'te taşınır).
    private static AppException MapMailError(CommandResult result, int defaultStatus = 500)
    {
        var detail = CombineError(result);
        var lower = detail.ToLowerInvariant();
        if (lower.Contains("password") &&
            (lower.Contains("weak") || lower.Contains("strength") || lower.Contains("short") ||
             lower.Contains("length") || lower.Contains("guessable") || lower.Contains("common")))
        {
            return new AppException("mail.weak_password", 422, 422, detail);
        }

        return new AppException("mail.operation_failed", defaultStatus, defaultStatus, detail);
    }

    // Komut satırı argümanı: boşluk/özel karakter içeriyorsa çift tırnakla.
    private static string Arg(string value)
    {
        var escaped = value.Replace("\"", "\\\"");
        return escaped.Any(char.IsWhiteSpace) || escaped.Length == 0 ? $"\"{escaped}\"" : escaped;
    }

    private sealed class MailConfig
    {
        public string AdminPasswordProtected { get; set; } = string.Empty;
    }
}
