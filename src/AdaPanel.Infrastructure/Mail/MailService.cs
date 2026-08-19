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
    // Araç YERİ + yönetici kimliği artık TEK kaynaktan gelir (bkz. StalwartRuntime): aynı çözümleme
    // vandelay için de gerekiyor ve iki kopya, parolanın yeri değiştiğinde birinin sessizce eski
    // yere bakmaya devam etmesi demekti.
    StalwartRuntime runtime,
    ILogger<MailService> logger) : IMailService
{
    /// <summary>
    /// Hesap disk kotasının Stalwart şemasındaki YERİ: <c>quotas</c> haritasının <c>maxDiskQuota</c>
    /// anahtarı (byte). Tek tamsayılık bir <c>quota</c> alanı YOKTUR — o ad kullanıldığında sunucu
    /// "unknown field: quota" döndürür ve kota sessizce uygulanmamış olur.
    /// </summary>
    private const string AccountQuotaField = "quotas/maxDiskQuota";

    /// <summary>
    /// Dağıtım listesi nesnesinin Stalwart şemasındaki ADI. <c>mailing-list</c> DEĞİLDİR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ölçüldü (Stalwart 0.16.8 + stalwart-cli 1.0.8):</b> <c>query mailing-list</c>,
    /// <c>create mailing-list</c> ve <c>delete mailing-list</c> çağrılarının HEPSİ
    /// <c>error: unknown object: mailing-list</c> ile düşüyor. CLI nesne adını büyük/küçük harf
    /// duyarsız çözüyor (<c>mailinglist</c> çalışıyor) ama TİRELİ biçimi hiç tanımıyor. Dokümandan
    /// kopyalanan tireli ad yüzünden panelin dağıtım listesi özelliği baştan sona ölüydü: liste
    /// oluşturma "işlem tamamlanamadı" veriyor, var olan listeler ekranda HİÇ görünmüyordu.
    /// </para>
    /// <para>
    /// Ad bir kez burada durur; oluşturma/okuma/güncelleme/silme aynı sabiti kullanır. Dört ayrı
    /// yere yazılsaydı biri düzeltilip diğeri unutulduğunda liste "oluşuyor ama görünmüyor"
    /// hâline gelirdi — bu depoda tam olarak bu sınıftan arızalar yaşandı.
    /// </para>
    /// </remarks>
    internal const string MailingListObject = "MailingList";

    /// <summary>
    /// Alan adı silinirken ÖNCE temizlenmesi gereken üye türleri.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden burada ve neden yalnız iki tür:</b> Stalwart üyesi olan bir domaini silmeyi
    /// REDDEDER (<c>objectIsLinked</c>). Bu liste eskiden <c>list</c>, <c>group</c>, <c>account</c>
    /// idi ve ilk ikisi ŞEMADA YOK: <c>query list</c> ve <c>query group</c> çağrılarının ikisi de
    /// <c>unknown object</c> ile düşüyor. <see cref="QueryAsync"/> başarısız sorguda BOŞ döndüğü
    /// (fırlatmadığı) için bu sessizce "bu türden üye yok" sayılıyordu.
    /// </para>
    /// <para>
    /// Ölçülen sonuç: dağıtım listesi olan bir alan adı SİLİNEMİYORDU. Hesaplar temizleniyor,
    /// listeler duruyor, <c>delete domain</c> şu cevabı veriyordu:
    /// <c>objectIsLinked ... Linked by: MailingList#b, MailingList#c</c> → panel
    /// <c>mail.operation_failed</c> gösterip alan adını ekranda takılı bırakıyordu.
    /// </para>
    /// <para>
    /// <c>group</c> ayrıca GEREKSİZDİ: gruplar <c>Account</c> nesnesinin bir varyantıdır
    /// (<c>Account/User</c> ve <c>Account/Group</c>) ve <c>query account</c> ikisini de döndürür
    /// (ölçüldü).
    /// </para>
    /// <para>
    /// <b><c>DkimSignature</c> neden listede:</b> üyeler temizlendikten SONRA bile silme
    /// reddediliyordu — <c>objectIsLinked ... Linked by: DkimSignature#..., DkimSignature#...</c>.
    /// Stalwart, alan adı oluşturulurken DKIM anahtarlarını KENDİSİ üretir; dolayısıyla her alan
    /// adında bu bağ vardır ve temizlenmediği sürece HİÇBİR alan adı silinemez. Anahtarlar alan
    /// adına aittir ve o gidince işe yaramaz; karşılık gelen DNS kayıtları da silme akışında
    /// zaten kaldırılıyor.
    /// </para>
    /// </remarks>
    internal static readonly string[] DomainCascadeTypes = [MailingListObject, "account", "DkimSignature"];

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
                if (BelongsToDomain(address, normalized))
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

            // Dağıtım listeleri: seçili domain'e ait olanları topla.
            //
            // Özet kayıt (query) yalnız description/emailAddress/id taşır — abone kümesi YOKTUR
            // (ölçüldü). Bu yüzden her liste için detay çekilir; detay okunamazsa liste ATLANIR:
            // özete düşüp "aboneleri boş" göstermek, yöneticiye listesi boşalmış gibi görünür ve
            // üzerine "kaydet" derse gerçekten boşaltır.
            var lists = new List<MailListDto>();
            // Politika deposu yalnız gerçekten liste varsa okunur (dosya çoğu kurulumda hiç yok).
            Dictionary<string, ListPolicyEntry>? listPolicies = null;
            foreach (var brief in await QueryAsync(cli, MailingListObject, env, cancellationToken))
            {
                var id = GetField(brief, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var listDetail = await GetObjectAsync(cli, MailingListObject, id, env, cancellationToken);
                if (listDetail is null)
                {
                    continue;
                }

                var listAddress = GetField(listDetail.Value, "emailAddress");
                if (!BelongsToDomain(listAddress, normalized))
                {
                    continue;
                }

                listPolicies ??= await ReadListPoliciesAsync(cancellationToken);
                var policy = listPolicies.TryGetValue(listAddress, out var stored) ? stored : new ListPolicyEntry();

                lists.Add(new MailListDto
                {
                    Address = listAddress,
                    Description = GetField(listDetail.Value, "description"),
                    Recipients = ReadListRecipients(listDetail.Value),
                    PostingPolicy = MailListPostingPolicy.Normalize(policy.Policy),
                    AllowedSenders = policy.AllowedSenders
                });
            }

            lists.Sort((left, right) => string.CompareOrdinal(left.Address, right.Address));

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

            // TEK senkron: kayitlari tek tek yazmak her seferinde bolgeyi bastan yaziyor ve
            // uzun DKIM TXT'sinin bolge kilidi bir sonraki yazmayi dusuruyordu.
            var yazilacak = dns.Records
                .Where(item => !string.IsNullOrWhiteSpace(item.Value))
                .Select(record => new UpsertDnsRecordRequest
                {
                    Type = record.Type,
                    Host = record.Host,
                    Value = record.Value,
                    Priority = record.Priority,
                    Ttl = 3600
                })
                .ToList();

            if (yazilacak.Count > 0)
            {
                await dnsService.UpsertRecordsAsync(dns.Domain, yazilacak, cancellationToken);
            }

            logger.LogInformation("Mail DNS records published for {Domain}: {Count} record(s).", domain, yazilacak.Count);
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
        // gider. Stalwart, ÜYESİ olan bir domaini silmeyi REDDEDER; üye kalırsa "delete domain" patlar ve domain
        // UI'de takılı kalır. account turunda ayrıca BAŞKA domaindeki hesaplarda bu domaine işaret eden alias
        // girdileri de temizlenir.
        foreach (var principalType in DomainCascadeTypes)
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
                    BelongsToDomain(memberEmail, domain);

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

    // --- Dağıtım listeleri (Stalwart MailingList principal) ---

    public async Task<MailWorkspaceDto> CreateMailListAsync(CreateMailListRequest request, CancellationToken cancellationToken = default)
    {
        var domain = request.Domain.Trim().ToLowerInvariant();
        var address = NormalizeAddress(request.Address, domain);
        var localPart = address[..address.IndexOf('@')];
        var (cli, env) = await RequireCliAsync(cancellationToken);
        var domainId = await FindIdAsync("domain", "name", domain, cancellationToken)
            ?? throw new AppException("mail.operation_failed", 404, 404, $"domain not found: {domain}");

        // Payload --file ile geçirilir (komut satırı tırnak bozulmasına karşı; create/update için kural).
        var payload =
            $"{{\"name\":{JsonValue(localPart)},\"domainId\":{JsonValue(domainId)}," +
            $"\"description\":{JsonValue(request.Description.Trim())}," +
            $"\"recipients\":{BuildRecipientsMapJson(request.Recipients, domain)}}}";
        var result = await RunCliJsonAsync(cli, $"create {MailingListObject}", payload, env, cancellationToken);
        if (!result.Success)
        {
            throw MapMailError(result);
        }

        await StoreListPolicyAsync(address, request.PostingPolicy, request.AllowedSenders, domain, cancellationToken);
        await SyncListPolicyScriptAsync(cancellationToken);

        return await GetWorkspaceAsync(domain, cancellationToken);
    }

    /// <summary>
    /// Aboneleri toplu değiştirir ve/veya tek tek ekler/çıkarır (bkz.
    /// <see cref="SetMailListRecipientsRequest"/>).
    /// </summary>
    /// <remarks>
    /// Sunucuda "abone ekle" diye bir işlem YOKTUR; <c>update</c> kümeyi komple değiştirir. Fark
    /// uygulanacaksa güncel küme ÖNCE sunucudan okunur — panelin elindeki (ekran tazelenmemiş
    /// olabilir) kopyaya güvenilirse, o arada başka bir yönetici tarafından eklenen aboneler
    /// sessizce silinir.
    /// </remarks>
    public async Task<MailWorkspaceDto> SetMailListRecipientsAsync(SetMailListRecipientsRequest request, CancellationToken cancellationToken = default)
    {
        var normalized = request.Address.Trim().ToLowerInvariant();
        var domain = ExtractDomain(normalized);
        var (cli, env) = await RequireCliAsync(cancellationToken);
        var id = await FindIdAsync(MailingListObject, "emailAddress", normalized, cancellationToken)
            ?? throw new AppException("mail.list_not_found", 404, 404, $"list not found: {normalized}");

        var current = await GetObjectAsync(cli, MailingListObject, id, env, cancellationToken)
            ?? throw new AppException("mail.list_not_found", 404, 404, $"list not readable: {normalized}");

        var target = ResolveRecipients(
            ReadListRecipients(current),
            request.Recipients,
            request.Add,
            request.Remove,
            domain);

        var payload = $"{{\"recipients\":{BuildRecipientsMapJson(target, domain)}}}";
        var result = await RunCliJsonAsync(cli, $"update {MailingListObject} {Arg(id)}", payload, env, cancellationToken);
        if (!result.Success)
        {
            throw MapMailError(result);
        }

        await StoreListPolicyAsync(normalized, request.PostingPolicy, request.AllowedSenders, domain, cancellationToken);

        // Abone kümesi değişmiş olabilir; `subscribers` kipinde izinli gönderenler ondan türediği
        // için script HER abone değişikliğinde yeniden üretilmelidir.
        await SyncListPolicyScriptAsync(cancellationToken);

        return await GetWorkspaceAsync(domain, cancellationToken);
    }

    public async Task<MailWorkspaceDto> DeleteMailListAsync(string address, CancellationToken cancellationToken = default)
    {
        var normalized = address.Trim().ToLowerInvariant();
        var domain = ExtractDomain(normalized);
        var id = await FindIdAsync(MailingListObject, "emailAddress", normalized, cancellationToken)
            ?? throw new AppException("mail.list_not_found", 404, 404, $"list not found: {normalized}");
        var workspace = await MutateAsync(domain, $"delete {MailingListObject} --ids {Arg(id)}", cancellationToken);

        // Politika kaydı da gider: liste silindikten sonra script'te ARTIK OLMAYAN bir adrese
        // kural kalsaydı, aynı adres yeniden açıldığında eski (belki çok dar) politika sessizce
        // geri gelirdi.
        var store = await ReadListPoliciesAsync(cancellationToken);
        if (store.Remove(normalized))
        {
            await fileStore.WriteAsync(ListPolicyStorePath, store, cancellationToken);
        }

        await SyncListPolicyScriptAsync(cancellationToken);
        return workspace;
    }

    private async Task<Dictionary<string, ListPolicyEntry>> ReadListPoliciesAsync(CancellationToken cancellationToken)
    {
        var stored = await fileStore.ReadAsync<Dictionary<string, ListPolicyEntry>>(ListPolicyStorePath, new(), cancellationToken);
        return stored is null
            ? new Dictionary<string, ListPolicyEntry>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ListPolicyEntry>(stored, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Saklı politikaları SUNUCUDAKİ güncel listelerle birleştirip tek sistem script'ini üretir,
    /// upsert eder, RCPT aşamasına bağlar ve ayarları yeniden yükletir.
    /// </summary>
    /// <remarks>
    /// Abone kümesi sunucudan OKUNUR (panelin elindeki kopyadan değil): <c>subscribers</c> kipinde
    /// izinli gönderenler abonelerden türediği için, bayat bir kopya meşru bir aboneyi kendi
    /// listesinden kilitleyebilirdi.
    /// </remarks>
    private async Task SyncListPolicyScriptAsync(CancellationToken cancellationToken)
    {
        var (cli, env) = await RequireCliAsync(cancellationToken);
        var store = await ReadListPoliciesAsync(cancellationToken);

        var rules = new List<ListPolicyRule>();
        foreach (var brief in await QueryAsync(cli, MailingListObject, env, cancellationToken))
        {
            var listAddress = GetField(brief, "emailAddress");
            if (string.IsNullOrWhiteSpace(listAddress) || !store.TryGetValue(listAddress, out var entry))
            {
                continue;
            }

            var policy = MailListPostingPolicy.Normalize(entry.Policy);
            if (policy == MailListPostingPolicy.Anyone)
            {
                continue;
            }

            var id = GetField(brief, "id");
            var subscribers = string.IsNullOrWhiteSpace(id)
                ? []
                : ReadListRecipients(await GetObjectAsync(cli, MailingListObject, id, env, cancellationToken) ?? default);

            rules.Add(new ListPolicyRule(
                listAddress.ToLowerInvariant(),
                policy,
                ResolveAllowedSenders(policy, subscribers, entry.AllowedSenders)));
        }

        var payload = JsonSerializer.Serialize(new
        {
            name = ListPolicyScriptName,
            isActive = true,
            description = "AdaPanel liste gonderim politikasi (panelden uretilir; elle duzenlemeyin)",
            contents = BuildListPolicyScript(rules)
        });

        var scriptId = await FindIdAsync("SieveSystemScript", "name", ListPolicyScriptName, cancellationToken);
        var upsert = scriptId is null
            ? await RunCliJsonAsync(cli, "create SieveSystemScript", payload, env, cancellationToken)
            : await RunCliJsonAsync(cli, $"update SieveSystemScript {Arg(scriptId)}", payload, env, cancellationToken);
        if (!upsert.Success)
        {
            throw new AppException("mail.list_policy_failed", 502, 502, CombineError(upsert));
        }

        // RCPT aşamasına bağla. Yönetici elle BAŞKA bir script bağladıysa DOKUNMA ve açıkça bildir —
        // burada sessizce üzerine yazmak, yöneticinin kendi teslimat kuralını (ör. greylisting)
        // haberi olmadan kaldırmak olurdu.
        var stage = await GetObjectAsync(cli, "MtaStageRcpt", "singleton", env, cancellationToken);
        var currentScript = stage is { } stageValue
            && stageValue.TryGetProperty("script", out var scriptExpr)
            && scriptExpr.TryGetProperty("else", out var elseValue)
                ? elseValue.GetString() ?? string.Empty
                : string.Empty;

        var ourExpression = $"'{ListPolicyScriptName}'";
        if (string.IsNullOrWhiteSpace(currentScript) || currentScript is "false" or "0")
        {
            var bind = await RunCliAsync(
                cli,
                $"update MtaStageRcpt singleton --field {CliJsonArg($"script={{\"match\":{{}},\"else\":\"{ourExpression}\"}}")}",
                env,
                cancellationToken);
            if (!bind.Success)
            {
                throw new AppException("mail.list_policy_failed", 502, 502, CombineError(bind));
            }
        }
        else if (!string.Equals(currentScript, ourExpression, StringComparison.Ordinal))
        {
            throw new AppException("mail.list_policy_conflict", 409, 409, $"MtaStageRcpt.script={currentScript}");
        }

        // Yeniden başlatma OLMADAN etkinleşir (otomatik yanıtta kanıtlandı: ReloadSettings yeterli).
        _ = await RunCliJsonAsync(cli, "create Action", "{\"@type\":\"ReloadSettings\"}", env, cancellationToken);
    }

    /// <summary>Politika kaydını günceller (kip ve/veya izinli gönderenler); <c>null</c> alanlar korunur.</summary>
    private async Task StoreListPolicyAsync(
        string address,
        string? policy,
        IEnumerable<string>? allowedSenders,
        string domain,
        CancellationToken cancellationToken)
    {
        if (policy is null && allowedSenders is null)
        {
            return;
        }

        if (policy is not null && !MailListPostingPolicy.IsKnown(policy))
        {
            throw new AppException("mail.list_policy_invalid", 400, 400, policy);
        }

        var store = await ReadListPoliciesAsync(cancellationToken);
        var entry = store.TryGetValue(address, out var existing) ? existing : new ListPolicyEntry();

        entry.Policy = MailListPostingPolicy.Normalize(policy ?? entry.Policy);
        if (allowedSenders is not null)
        {
            // İzinli gönderenler de doğrulanır: script'e teslim edilemez bir adres yazmak, o satırı
            // hiçbir zaman eşleşmeyen ölü bir kurala çevirir.
            entry.AllowedSenders = [.. NormalizeRecipients(allowedSenders, domain)];
        }

        store[address] = entry;
        await fileStore.WriteAsync(ListPolicyStorePath, store, cancellationToken);
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

    // --- Dağıtım listesi gönderim politikası (Plesk "kimler gönderebilir" paritesi) ---
    //
    // Stalwart'ın MailingList nesnesinde böyle bir alan YOK (şema sunucudan okundu). Ama kural,
    // sunucunun KENDİ eklenti noktasıyla uygulanabiliyor: RCPT aşamasına bağlanan tek bir SİSTEM
    // sieve script'i. Canlı doğrulandı (Stalwart 0.16.8):
    //   - abone, kimliği doğrulanmış  -> RCPT 250 2.1.5 OK
    //   - kimliksiz dış gönderici     -> RCPT 550 5.7.2 (reddedildi)
    //   - normal posta kutusu         -> etkilenmiyor
    // Panelin otomatik yanıt özelliği AYNI deseni DATA aşamasında kullanıyor; burada DATA yuvası
    // değil, boştaki RCPT yuvası kullanılır — ikisi çakışmaz.
    //
    // Politika RCPT aşamasında uygulanır çünkü reddin ucuz ve DÜRÜST olduğu yer orasıdır:
    // gönderici iletinin gövdesini hiç yollamadan "yazamazsın" cevabını alır. DATA aşamasında
    // reddetseydik ileti kabul edilmiş gibi görünüp sonra sessizce düşerdi.
    private const string ListPolicyStorePath = "config/mail-list-policies.json";
    private const string ListPolicyScriptName = "adapanel-listpolicy";

    internal sealed class ListPolicyEntry
    {
        public string Policy { get; set; } = MailListPostingPolicy.Anyone;

        public string[] AllowedSenders { get; set; } = [];
    }

    /// <summary>Script üretimi için tek bir listenin ETKİN kuralı.</summary>
    internal sealed record ListPolicyRule(string Address, string Policy, IReadOnlyList<string> AllowedSenders);

    /// <summary>
    /// Bütün listelerin kurallarından TEK sistem sieve script'i üretir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden tek script:</b> RCPT aşamasında bağlanacak tek bir yuva var
    /// (<c>MtaStageRcpt.script</c>). Liste başına script üretilseydi yalnız sonuncusu bağlanır,
    /// diğerleri sunucuda ölü dururdu.
    /// </para>
    /// <para>
    /// <b>Kural yoksa yorum-satırı script:</b> boş içerik derlenmeyebilir; otomatik yanıtta
    /// kanıtlanan yol, hiçbir şey yapmayan bir yorum script'i üretmektir. Böylece "politika yok"
    /// durumu da geçerli bir script'tir ve bağlama bozulmaz.
    /// </para>
    /// <para>
    /// <b>Adres karşılaştırması küçük harfe indirgenir</b> çünkü envelope değerleri istemciden
    /// gelir; <c>Ali@Ornek.com</c> ile giriş yapan abone aksi hâlde kendi listesine yazamazdı.
    /// </para>
    /// </remarks>
    internal static string BuildListPolicyScript(IEnumerable<ListPolicyRule> rules)
    {
        var active = rules
            .Where(rule => rule.Policy != MailListPostingPolicy.Anyone)
            .OrderBy(rule => rule.Address, StringComparer.Ordinal)
            .ToList();

        if (active.Count == 0)
        {
            return "# AdaPanel: liste gonderim politikasi yok / no mailing list posting policy\r\n";
        }

        var builder = new StringBuilder();
        builder.Append("require [\"variables\", \"envelope\", \"reject\"];\r\n");
        builder.Append("# AdaPanel tarafindan uretilir; elle duzenlemeyin. / Generated by AdaPanel; do not edit.\r\n");

        foreach (var rule in active)
        {
            builder.Append($"if envelope :is :comparator \"i;ascii-casemap\" \"to\" {SieveQuoted(rule.Address)} {{\r\n");

            // İzinli gönderen YOKSA (ör. abonesiz liste + subscribers kipi) kural "hiç kimse"ye
            // döner. Bu bilinçli: aksi hâlde boş bir izin listesi sessizce "herkes"e açılırdı.
            if (rule.AllowedSenders.Count == 0)
            {
                builder.Append($"    reject {SieveQuoted(BuildPolicyRejectReason(rule.Policy))};\r\n");
            }
            else
            {
                builder.Append("    if not anyof (\r\n");
                var tests = rule.AllowedSenders
                    .Select(sender => $"        string :is :comparator \"i;ascii-casemap\" \"${{env.authenticated_as}}\" {SieveQuoted(sender)}");
                builder.Append(string.Join(",\r\n", tests));
                builder.Append("\r\n    ) {\r\n");
                builder.Append($"        reject {SieveQuoted(BuildPolicyRejectReason(rule.Policy))};\r\n");
                builder.Append("    }\r\n");
            }

            builder.Append("}\r\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Red mesajı. SMTP kodu 550 5.7.2 ("mailing list ... not allowed") ve sebep AÇIK yazılır:
    /// gönderen postasının nereye gittiğini bilmeden sessizce kaybolmasındansa neden
    /// reddedildiğini görmeli.
    /// </summary>
    internal static string BuildPolicyRejectReason(string policy) => policy switch
    {
        MailListPostingPolicy.Subscribers =>
            "550 5.7.2 Only authenticated subscribers may post to this mailing list.",
        _ => "550 5.7.2 You are not authorized to post to this mailing list."
    };

    /// <summary>
    /// TIRNAKLI sieve string literal'i. Kaçırma kuralı <see cref="SieveString"/>'den gelir —
    /// iki ayrı kopya yazılsaydı biri düzeltildiğinde diğeri sessizce kaçırmayı atlardı.
    /// </summary>
    internal static string SieveQuoted(string value) => "\"" + SieveString(value) + "\"";

    /// <summary>
    /// Bir listenin ETKİN izinli gönderen kümesini hesaplar.
    /// </summary>
    /// <remarks>
    /// <c>subscribers</c> kipinde küme ABONELERDEN türetilir; bu yüzden abone listesi her
    /// değiştiğinde script yeniden üretilmelidir. Liste yöneticileri (<c>allowed</c> kipinin
    /// adresleri) <b>her iki</b> kipte de gönderebilir: kendi listesine duyuru yazamayan bir
    /// yönetici, özelliği kullanılamaz kılardı.
    /// </remarks>
    internal static IReadOnlyList<string> ResolveAllowedSenders(
        string policy,
        IEnumerable<string> subscribers,
        IEnumerable<string> allowedSenders)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var sender in allowedSenders ?? [])
        {
            var trimmed = (sender ?? string.Empty).Trim().ToLowerInvariant();
            if (trimmed.Length > 0)
            {
                result.Add(trimmed);
            }
        }

        if (policy == MailListPostingPolicy.Subscribers)
        {
            foreach (var subscriber in subscribers ?? [])
            {
                var trimmed = (subscriber ?? string.Empty).Trim().ToLowerInvariant();
                if (trimmed.Length > 0)
                {
                    result.Add(trimmed);
                }
            }
        }

        return [.. result];
    }

    /// <summary>
    /// Bir posta adresi SEÇİLİ alan adına mı ait? (Hesap ve dağıtım listesi süzgeçlerinin TEK kuralı.)
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden düz <c>EndsWith</c> değil:</b> bu süzgeç, kiracının ekranında hangi kutuların ve
    /// listelerin görüneceğine karar verir — yani bir GÖRÜNÜRLÜK KAPISIDIR. Alan adı çıplak
    /// <c>EndsWith(domain)</c> ile karşılaştırılsaydı <c>ornek.com</c> seçen kiracı,
    /// <c>duyuru@kotuornek.com</c> ve <c>duyuru@alt.ornek.com</c> adreslerini de kendi
    /// listesindeymiş gibi görürdü: birincisi BAŞKASININ alan adı, ikincisi ayrı bir mail
    /// domaini. İkisi de adres/abone sızıntısıdır.
    /// </para>
    /// <para>
    /// Kural bu yüzden SON "@"den sonrasının TAM eşitliğidir; ek etiket ya da uzayan ad kabul
    /// edilmez. Alt alan adı ayrı bir mail domainidir ve kendi seçildiğinde listelenir.
    /// </para>
    /// </remarks>
    internal static bool BelongsToDomain(string? address, string? domain)
    {
        var normalizedDomain = DomainNames.Normalize(domain);
        if (normalizedDomain.Length == 0)
        {
            return false;
        }

        var value = (address ?? string.Empty).Trim();
        var at = value.LastIndexOf('@');
        if (at < 0 || at == value.Length - 1)
        {
            return false;
        }

        return string.Equals(DomainNames.Normalize(value[(at + 1)..]), normalizedDomain, StringComparison.Ordinal);
    }

    /// <summary>
    /// Abone adreslerini Stalwart'ın beklediği KÜME (harita) gösterimine çevirir:
    /// <c>{"ali@ornek.com":true,"veli@ornek.com":true}</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden dizi değil:</b> şemada alan <c>set&lt;emailAddress&gt;</c> ve tel üzerinde
    /// haritadır. Dizi gönderildiğinde sunucu <c>invalidPatch / Invalid value for object property:
    /// recipients</c> döndürüyor (canlı ölçüldü) — panel bu yüzden hiçbir liste
    /// oluşturamıyor/güncelleyemiyordu.
    /// </para>
    /// <para>
    /// <b>Adres doğrulaması burada yapılır çünkü sunucu YAPMIYOR:</b> ölçüldü — Stalwart
    /// <c>recipients</c> kümesine <c>bu-adres-degil</c> gibi "@" bile içermeyen bir değeri
    /// itirazsız kabul ediyor. Doğrulanmazsa liste, kimseye ulaşmayan bir aboneyle sessizce
    /// yaşamaya devam eder; yönetici o kişinin duyuruları aldığını sanır.
    /// </para>
    /// </remarks>
    internal static string BuildRecipientsMapJson(IEnumerable<string> recipients, string domain)
    {
        var entries = NormalizeRecipients(recipients, domain)
            .Select(value => $"{JsonValue(value)}:true");
        return "{" + string.Join(",", entries) + "}";
    }

    /// <summary>
    /// Abone adreslerini kırpar, küçük harfe indirir, yerel kısımları alan adıyla tamamlar,
    /// doğrular ve TEKRARLARI ayıklar (sıralı döner).
    /// </summary>
    internal static IReadOnlyList<string> NormalizeRecipients(IEnumerable<string> recipients, string domain)
    {
        var unique = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in recipients ?? [])
        {
            var trimmed = (raw ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var address = NormalizeAddress(trimmed, domain);
            if (!IsValidEmailAddress(address))
            {
                throw new AppException("mail.invalid_recipient", 400, 400, address);
            }

            unique.Add(address);
        }

        return [.. unique];
    }

    /// <summary>
    /// Toplu değiştirme ve tek-abone farklarını TEK yerde birleştirir: önce (istenmişse) küme
    /// komple değiştirilir, sonra eklenenler eklenir, en son çıkarılanlar çıkarılır.
    /// </summary>
    /// <remarks>
    /// <b>Çıkarma en sonda:</b> aynı adres hem <paramref name="add"/> hem
    /// <paramref name="remove"/> içinde geçerse sonuç "çıkarıldı" olur. Bir aboneyi listeden
    /// düşürmek isteyip yanlışlıkla iki kutuyu da işaretleyen yönetici için güvenli olan yön
    /// budur: postanın yanlışlıkla dağıtılmaya devam etmesi, yanlışlıkla durmasından daha zor
    /// fark edilir ve geri alınamaz.
    /// </remarks>
    internal static IReadOnlyList<string> ResolveRecipients(
        IEnumerable<string> current,
        IEnumerable<string>? replacement,
        IEnumerable<string> add,
        IEnumerable<string> remove,
        string domain)
    {
        var result = new SortedSet<string>(
            NormalizeRecipients(replacement ?? current, domain),
            StringComparer.Ordinal);

        result.UnionWith(NormalizeRecipients(add, domain));
        result.ExceptWith(NormalizeRecipients(remove, domain));
        return [.. result];
    }

    /// <summary>
    /// Sunucudan gelen abone kümesini okur. Şemada <c>set&lt;emailAddress&gt;</c> = JSON HARİTA
    /// (<c>{"ali@ornek.com":true}</c>) — dizi bekleyen okuma her listeyi "abonesiz" gösteriyordu.
    /// </summary>
    /// <remarks>
    /// Dizi biçimi de kabul edilir: Stalwart dokümanının farklı sürümleri iki gösterimi de
    /// örnekliyor. Okuma tarafında hoşgörülü olmak bedava; YAZMA tarafı ölçülen tek doğru
    /// biçimde (harita) kalır.
    /// </remarks>
    internal static IReadOnlyCollection<string> ReadListRecipients(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("recipients", out var value))
        {
            return [];
        }

        var addresses = value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject()
                // Kümeden çıkarılmış girdi false ile işaretlenmiş olabilir; onu abone SAYMAYIZ.
                .Where(entry => entry.Value.ValueKind != JsonValueKind.False)
                .Select(entry => entry.Name),
            JsonValueKind.Array => value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!),
            _ => []
        };

        return addresses
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .OrderBy(address => address, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Adres tek "@" içeriyor, iki yanı da dolu ve alan adı en az bir nokta taşıyor mu?
    /// </summary>
    /// <remarks>
    /// Kasten dar tutuldu: amaç RFC 5322'yi yeniden yazmak değil, sunucunun sessizce kabul ettiği
    /// AÇIKÇA teslim edilemez değerleri (boşluklu metin, "@" yok, alan adı yok) durdurmak.
    /// </remarks>
    internal static bool IsValidEmailAddress(string address)
    {
        var at = address.IndexOf('@');
        if (at <= 0 || at != address.LastIndexOf('@') || at == address.Length - 1)
        {
            return false;
        }

        var domain = address[(at + 1)..];
        return !address.Any(char.IsWhiteSpace)
            && domain.Contains('.')
            && !domain.StartsWith('.')
            && !domain.EndsWith('.');
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
        // ONCE TERS DNS (PTR): karsilama adi sertifikayla degil, PTR ILE tutmali.
        //
        // Teslim edilebilirligin aradigi sey "ileri-dogrulanmis ters DNS"tir: sunucunun kendini
        // tanittigi ad, IP'nin PTR kaydiyla ayni olmali ve o ad da ayni IP'ye cozulmeli. Sertifika
        // adi bundan FARKLI olabilir ve olmasi da normaldir -- sertifika istemcilerin baglandigi
        // adi kapsar, PTR ise sunucunun kimligini dogrular.
        //
        // Eskiden ad yalnizca sertifikadan turetiliyordu. Sunucunun PTR'si baska bir ad ise
        // (yaygin durum: saglayici sunucuya kendi ana adini vermistir) karsilama PTR ile tutmuyor
        // ve panel, kullaniciyi PTR'sini degistirmeye zorlamis oluyordu. Oysa dogrusu, zaten
        // dogrulanmis olan adi kullanmak.
        var hostName = await ResolveVerifiedReverseNameAsync(cancellationToken)
                       ?? state?.HostName?.Trim().ToLowerInvariant();

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

        // AYARIN YAZILI DEGERI YETMEZ -- SUNUCUNUN GERCEKTEN NE DEDIGINE BAKILIR.
        //
        // Ayar dogru yazilmis ama Stalwart onu henuz okumamis olabilir (ReloadSettings gitmemis,
        // yazma yarim kalmis, servis yeniden baslamis). O durumda panel "esit, yapacak is yok"
        // deyip susuyor, karsilama ise hala makine adini gosteriyordu -- canlida tam olarak bu
        // oldu: ayar yerindeydi, uyari cikmadi, sorun surdu.
        //
        // Bu, oturum boyunca avladigimiz hatanin ta kendisi: panelin NIYETI ile SUNUCUNUN HALI
        // ayni sey degildir. Dogruyu ogrenmek icin kaynaga sormak gerekir.
        var karsilamaDogru = await SmtpGreetingMatchesAsync(hostName, cancellationToken);

        if (karsilamaDogru
            && string.Equals(currentHost, hostName, StringComparison.OrdinalIgnoreCase)
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

        // Ayar zaten dogruysa yeniden yazmaya gerek yok; eksik olan tek sey yeniden okutmaktir.
        if (string.Equals(currentHost, hostName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(domainId, currentDomainId, StringComparison.Ordinal))
        {
            await RunCliJsonAsync(cli, "create Action", "{\"@type\":\"ReloadSettings\"}", env, cancellationToken);
            logger.LogInformation("Posta sunucusu ayarlari yeniden okutuldu; karsilama adi '{HostName}'.", hostName);
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

    /// <summary>
    /// Posta sunucusunun CANLI SMTP karsilamasini okur ve beklenen adi icerip icermedigini soyler.
    /// Olcum yapilamazsa true doner (bilmiyorsak "bozuk" demeyiz).
    /// <para>
    /// Neden gerekli: ayarin yazili degeri sunucunun o an ne dedigini GARANTI ETMEZ. Ayar dogru
    /// olup Stalwart henuz okumamis olabilir; o durumda panel susar ve sorun kalici olur.
    /// </para>
    /// </summary>
    private async Task<bool> SmtpGreetingMatchesAsync(string hostName, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connect = client.ConnectAsync("127.0.0.1", 25, cancellationToken).AsTask();
            if (await Task.WhenAny(connect, Task.Delay(3000, cancellationToken)) != connect)
            {
                return true;
            }

            await connect;
            using var reader = new StreamReader(client.GetStream());
            var readLine = reader.ReadLineAsync(cancellationToken).AsTask();
            if (await Task.WhenAny(readLine, Task.Delay(3000, cancellationToken)) != readLine)
            {
                return true;
            }

            var banner = await readLine ?? string.Empty;
            return banner.Contains(hostName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "SMTP karsilamasi okunamadi; ayarin yazili degerine guvenilecek.");
            return true;
        }
    }

    /// <summary>
    /// Sunucunun IP'sinin PTR kaydini okur ve o adin AYNI IP'ye cozuldugunu dogrular.
    /// Dogrulanamazsa null doner.
    /// <para>
    /// Neden dogrulama sart: yalnizca PTR'ye guvenmek yeterli degildir. Ad ileri yonde baska bir
    /// yere cozuluyorsa (ya da hic cozulmuyorsa) alici sunucular bunu gecersiz sayar; boyle bir
    /// adi karsilamaya yazmak durumu iyilestirmez, kotulestirir.
    /// </para>
    /// </summary>
    private async Task<string?> ResolveVerifiedReverseNameAsync(CancellationToken cancellationToken)
    {
        try
        {
            var preferences = await panelPreferencesService.GetAsync(cancellationToken);
            var address = preferences.ServerAddress?.Trim() ?? string.Empty;
            if (!System.Net.IPAddress.TryParse(address, out var ip))
            {
                return null;
            }

            var ptr = await System.Net.Dns.GetHostEntryAsync(address);
            var name = ptr.HostName?.Trim().TrimEnd('.').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(name) || !name.Contains('.'))
            {
                return null;
            }

            // ILERI DOGRULAMA: ad ayni IP'ye cozulmeli.
            var forward = await System.Net.Dns.GetHostAddressesAsync(name);
            return forward.Any(candidate => candidate.Equals(ip)) ? name : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Ters DNS adi dogrulanamadi; karsilama adi sertifikadan turetilecek.");
            return null;
        }
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

    private Task<string?> ResolveCliAsync(CancellationToken cancellationToken)
        => runtime.ResolveExecutableAsync(
            CliComponentId,
            "stalwart-cli.exe",
            "%ProgramData%\\AdaPanel\\runtimes\\stalwart-cli",
            cancellationToken);

    private Task<string?> GetAdminPasswordAsync(CancellationToken cancellationToken)
        => runtime.GetAdminPasswordAsync(cancellationToken);

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

        // ADRES ZATEN KULLANIMDA. Stalwart bunu `primaryKeyViolation` + `Properties: email` ile
        // bildirir (ölçüldü: aynı adresle ikinci bir dağıtım listesi açmaya çalışırken). Generic
        // "işlem tamamlanamadı" gösterildiğinde kullanıcı adresin dolu olduğunu ANLAMIYOR ve aynı
        // adı denemeye devam ediyor; oysa yapması gereken tek şey başka bir yerel kısım seçmek.
        if (lower.Contains("primarykeyviolation"))
        {
            return new AppException("mail.address_in_use", 409, 409, detail);
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

    // --- Giden posta limiti (MtaOutboundThrottle) ---

    /// <summary>
    /// Panelin kurduğu throttle kurallarının açıklamasındaki ÖNEK.
    /// </summary>
    /// <remarks>
    /// Kendi kuralımızı bu önekle tanıyoruz: yöneticinin elle eklediği kurallara asla dokunmayız
    /// ve güncellerken kendi kuralımızı bulup değiştiririz (her seferinde yenisini eklemek yerine).
    /// </remarks>
    private const string OutboundThrottlePrefix = "AdaPanel outbound limit: ";

    /// <inheritdoc />
    public async Task<MailOutboundLimitDto> GetOutboundLimitAsync(string domain, CancellationToken cancellationToken = default)
    {
        var normalized = (domain ?? string.Empty).Trim().ToLowerInvariant();
        var (cli, env) = await RequireCliAsync(cancellationToken);

        var kural = await BulOutboundThrottleAsync(cli, env, normalized, cancellationToken);
        return new MailOutboundLimitDto
        {
            Domain = normalized,
            MessagesPerHour = kural?.Count ?? 0,
            Configured = kural is not null
        };
    }

    /// <inheritdoc />
    public async Task<MailOutboundLimitDto> SetOutboundLimitAsync(string domain, int messagesPerHour, CancellationToken cancellationToken = default)
    {
        var normalized = (domain ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            throw new AppException("mail.domain_required", 400, 400);
        }

        var (cli, env) = await RequireCliAsync(cancellationToken);
        var mevcut = await BulOutboundThrottleAsync(cli, env, normalized, cancellationToken);

        // 0 = limiti KALDIR. Kuralı "count=0" ile bırakmak, sıfır ileti gönderilebilir anlamına
        // gelirdi — yani limiti kaldırmak isterken alan adını tamamen susturmak.
        if (messagesPerHour <= 0)
        {
            if (mevcut is not null)
            {
                var sil = await RunCliAsync(cli, $"delete MtaOutboundThrottle {Arg(mevcut.Value.Id)}", env, cancellationToken);
                if (!sil.Success)
                {
                    throw new AppException("mail.outbound_limit_failed", 500, 500, sil.StandardError);
                }
            }

            return new MailOutboundLimitDto { Domain = normalized, MessagesPerHour = 0, Configured = false };
        }

        // Şema canlı sunucudan ÖLÇÜLEREK doğrulandı:
        //   key   → harita: {"senderDomain": true}   (liste DEĞİL)
        //   rate  → {"count": N, "period": <milisaniye>}
        //   match → {"match": {}, "else": "<ifade>"}
        // Tahminle yazılsaydı sunucu alanı sessizce yok sayar ve limit hiç kurulmamış olurdu;
        // bu depoda kota alanında tam olarak öyle bir hata yaşandı.
        var aciklama = OutboundThrottlePrefix + normalized;
        var alanlar =
            $"--field {Arg("description=" + aciklama)} " +
            "--field enable=true " +
            $"--field {CliJsonArg("key={\"senderDomain\":true}")} " +
            $"--field {CliJsonArg($"rate={{\"count\":{messagesPerHour},\"period\":3600000}}")} " +
            $"--field {CliJsonArg($"match={{\"match\":{{}},\"else\":\"sender_domain = '{normalized.Replace("'", "''")}'\"}}")}";

        var komut = mevcut is null
            ? $"create MtaOutboundThrottle {alanlar}"
            : $"update MtaOutboundThrottle {Arg(mevcut.Value.Id)} {alanlar}";

        var sonuc = await RunCliAsync(cli, komut, env, cancellationToken);
        if (!sonuc.Success)
        {
            throw new AppException("mail.outbound_limit_failed", 500, 500, sonuc.StandardError);
        }

        // DOĞRULA: komut başarılı çıksa da alan sunucuda tutulmamış olabilir. Kuralı geri okuyup
        // istenen değeri gerçekten görüyor muyuz diye bakıyoruz — "uyguladım" demeden önce.
        var dogrulama = await BulOutboundThrottleAsync(cli, env, normalized, cancellationToken);
        if (dogrulama is null || dogrulama.Value.Count != messagesPerHour)
        {
            throw new AppException("mail.outbound_limit_unverified", 500, 500);
        }

        return new MailOutboundLimitDto { Domain = normalized, MessagesPerHour = messagesPerHour, Configured = true };
    }

    /// <summary>Bu alan adına ait, PANELİN kurduğu throttle kuralını bulur.</summary>
    private async Task<(string Id, int Count)?> BulOutboundThrottleAsync(
        string cli,
        IReadOnlyDictionary<string, string> env,
        string domain,
        CancellationToken cancellationToken)
    {
        var liste = await RunCliAsync(cli, "query MtaOutboundThrottle --json", env, cancellationToken);
        if (!liste.Success)
        {
            return null;
        }

        var aranan = OutboundThrottlePrefix + domain;
        foreach (var satir in ParseItems(liste.StandardOutput))
        {
            if (!string.Equals(GetField(satir, "description")?.Trim(), aranan, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = GetField(satir, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            // Liste yanıtı yalnız özet döner; sayıyı öğrenmek için nesnenin tamamı okunur.
            var detay = await GetObjectAsync(cli, "MtaOutboundThrottle", id, env, cancellationToken);
            var count = 0;
            if (detay is { } nesne &&
                nesne.TryGetProperty("rate", out var rate) &&
                rate.TryGetProperty("count", out var sayi) &&
                sayi.TryGetInt32(out var deger))
            {
                count = deger;
            }

            return (id, count);
        }

        return null;
    }

    // --- Giden posta kuyruğu (QueuedMessage) ---

    /// <summary>Kuyruk listesinde varsayılan olarak gösterilen ileti sayısı.</summary>
    private const int VarsayilanKuyrukLimiti = 100;

    /// <summary>
    /// Kuyruk listesinin sert üst sınırı.
    /// </summary>
    /// <remarks>
    /// Sınır KEYFİ DEĞİL, maliyetten geliyor: liste yanıtı özet dönerse ileti başına ayrı bir CLI
    /// süreci açılır. Sınırsız bir "hepsini getir", kuyruğu on binlerce iletiye çıkmış (yani tam da
    /// bakılması gereken) bir sunucuda mail sayfasını süresiz kilitlerdi.
    /// </remarks>
    private const int AzamiKuyrukLimiti = 500;

    /// <inheritdoc />
    public async Task<MailQueueDto> GetQueueAsync(int limit, CancellationToken cancellationToken = default)
    {
        var cli = await ResolveCliAsync(cancellationToken);
        if (cli is null)
        {
            return new MailQueueDto { Installed = false };
        }

        var password = await GetAdminPasswordAsync(cancellationToken);
        if (password is null)
        {
            return new MailQueueDto { Installed = true, Configured = false };
        }

        var env = BuildEnvironment(password);
        var liste = await RunCliAsync(cli, "query QueuedMessage --json", env, cancellationToken);
        if (!liste.Success)
        {
            // "Kuyruk boş" DEĞİL: sunucuya bağlanılamadı. İkisini aynı görüntüye indirmek,
            // posta sunucusu çökmüşken yöneticiye "her şey yolunda" göstermek olurdu.
            return new MailQueueDto { Installed = true, Configured = false };
        }

        var kayitlar = ParseItems(liste.StandardOutput);
        var sinir = Math.Clamp(limit <= 0 ? VarsayilanKuyrukLimiti : limit, 1, AzamiKuyrukLimiti);

        var iletiler = new List<MailQueueMessageDto>();
        foreach (var kayit in kayitlar.Take(sinir))
        {
            var ileti = ParseQueueMessage(kayit);

            // Liste yanıtı ÖZET dönebilir (hesap listesinde de öyle: yalnız id + bir iki alan).
            // Zarf bilgisi gelmediyse iletinin tamamı okunur; yoksa ekranda kimlikten başka bir şey
            // olmaz ve "hangi ileti takıldı" sorusu yine cevapsız kalır.
            if (KuyrukOzetiEksik(ileti) && !string.IsNullOrWhiteSpace(ileti.Id))
            {
                var detay = await GetObjectAsync(cli, "QueuedMessage", ileti.Id, env, cancellationToken);
                if (detay is not null)
                {
                    ileti = ParseQueueMessage(detay.Value, ileti.Id);
                }
            }

            iletiler.Add(ileti);
        }

        return new MailQueueDto
        {
            Installed = true,
            Configured = true,
            TotalCount = kayitlar.Count,
            // En yeni ileti en üstte: operatörün aradığı şey "az önce gönderdiğim posta nerede".
            Messages = iletiler.OrderByDescending(item => item.CreatedAtUtc ?? DateTimeOffset.MinValue).ToArray()
        };
    }

    /// <inheritdoc />
    public async Task<MailQueueMessageDto?> GetQueueMessageAsync(string id, CancellationToken cancellationToken = default)
    {
        var kimlik = (id ?? string.Empty).Trim();
        if (kimlik.Length == 0)
        {
            return null;
        }

        var (cli, env) = await RequireCliAsync(cancellationToken);
        var detay = await GetObjectAsync(cli, "QueuedMessage", kimlik, env, cancellationToken);
        return detay is null ? null : ParseQueueMessage(detay.Value, kimlik);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteQueueMessageAsync(string id, CancellationToken cancellationToken = default)
    {
        var kimlik = (id ?? string.Empty).Trim();
        if (kimlik.Length == 0)
        {
            return false;
        }

        var (cli, env) = await RequireCliAsync(cancellationToken);

        // Zaten yoksa "sildim" demeyiz: çağıran 404 görmeli, yoksa var olmayan bir kimliği silen
        // her istek başarıyla dönerdi ve denetim kaydı olmayan bir işlemi kaydederdik.
        if (await GetObjectAsync(cli, "QueuedMessage", kimlik, env, cancellationToken) is null)
        {
            return false;
        }

        var sonuc = await RunCliAsync(cli, $"delete QueuedMessage --ids {Arg(kimlik)}", env, cancellationToken);
        if (!sonuc.Success)
        {
            throw new AppException("mail.queue_delete_failed", 502, 502, CombineError(sonuc));
        }

        // DOĞRULA: komut sıfırla dönse bile nesne yerinde kalmış olabilir. "Sildim" demeden önce
        // gerçekten gitmiş mi diye bakılır — bu depoda kota alanında tam tersi yaşandı.
        if (await GetObjectAsync(cli, "QueuedMessage", kimlik, env, cancellationToken) is not null)
        {
            throw new AppException("mail.queue_delete_unverified", 502, 502);
        }

        return true;
    }

    /// <summary>Zarf bilgisi gelmemiş (yalnız kimlik taşıyan) bir özet mi?</summary>
    private static bool KuyrukOzetiEksik(MailQueueMessageDto ileti) =>
        ileti.Recipients.Count == 0 && ileti.CreatedAtUtc is null && ileti.SizeBytes == 0;

    /// <summary>
    /// Stalwart <c>QueuedMessage</c> nesnesini panel görünümüne çevirir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Alan adları şema referansından alındı: <c>createdAt</c>, <c>nextRetry</c>,
    /// <c>returnPath</c>, <c>size</c>, <c>receivedFromIp</c> ve <c>recipients</c> (anahtarı
    /// e-posta adresi olan HARİTA). Alıcı nesnesinde <c>retryCount</c>, <c>retryDue</c>,
    /// <c>queueName</c> ve <c>status</c> bulunur.
    /// </para>
    /// <para>
    /// <c>status</c>'ün İÇİ bilerek ayrıştırılmıyor: varyantın içindeki hata alanlarının adları
    /// doğrulanamadı. Uydurma bir addan okumak, hata sebebini her zaman BOŞ gösterirdi — özellik
    /// var görünür ama hiç çalışmazdı. Bunun yerine varyant adı + ham metin taşınıyor.
    /// </para>
    /// </remarks>
    internal static MailQueueMessageDto ParseQueueMessage(JsonElement message, string? fallbackId = null)
    {
        var kimlik = GetField(message, "id");
        var alicilar = new List<MailQueueRecipientDto>();

        if (message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("recipients", out var harita)
            && harita.ValueKind == JsonValueKind.Object)
        {
            foreach (var girdi in harita.EnumerateObject())
            {
                var (durum, durumMetni) = ReadRecipientStatus(girdi.Value);
                alicilar.Add(new MailQueueRecipientDto
                {
                    Address = girdi.Name,
                    Status = durum,
                    StatusDetail = durumMetni,
                    RetryCount = (int)GetLongField(girdi.Value, "retryCount"),
                    RetryDueUtc = ParseUtc(girdi.Value, "retryDue"),
                    QueueName = GetField(girdi.Value, "queueName")
                });
            }
        }

        return new MailQueueMessageDto
        {
            Id = string.IsNullOrWhiteSpace(kimlik) ? (fallbackId ?? string.Empty) : kimlik,
            ReturnPath = GetField(message, "returnPath"),
            CreatedAtUtc = ParseUtc(message, "createdAt"),
            NextRetryUtc = ParseUtc(message, "nextRetry"),
            SizeBytes = GetLongField(message, "size"),
            ReceivedFromIp = GetField(message, "receivedFromIp"),
            Recipients = alicilar
        };
    }

    /// <summary>Alıcı durumunu (varyant adı, ham metin) olarak okur; string ya da nesne olabilir.</summary>
    private static (string Ad, string Metin) ReadRecipientStatus(JsonElement recipient)
    {
        if (recipient.ValueKind != JsonValueKind.Object || !recipient.TryGetProperty("status", out var durum))
        {
            return (string.Empty, string.Empty);
        }

        return durum.ValueKind switch
        {
            JsonValueKind.String => (durum.GetString() ?? string.Empty, durum.GetString() ?? string.Empty),
            JsonValueKind.Object => (GetField(durum, "@type", "type"), durum.GetRawText()),
            _ => (string.Empty, durum.GetRawText())
        };
    }

    /// <summary>ISO-8601 tarih alanını okur; okunamıyorsa null (tarihi uydurmaktansa göstermemek doğru).</summary>
    private static DateTimeOffset? ParseUtc(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    value.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    // --- Giden posta rölesi / smarthost (MtaRoute + MtaOutboundStrategy) ---

    /// <summary>Panelin kurduğu röle rotasının adı. Yöneticinin elle eklediği rotalara dokunulmaz.</summary>
    private const string RelayRouteName = "adapanel-relay";

    /// <summary>Yönlendirme ifadesinde rota adı TIRNAK İÇİNDE geçer (ifade dili bir string bekler).</summary>
    private const string RelayRouteExpression = "'" + RelayRouteName + "'";

    /// <summary>Stalwart'ın belgelenmiş varsayılan yönlendirmesi: MX çözümlemesiyle doğrudan teslim.</summary>
    private const string DefaultRouteExpression = "'mx'";

    private const string RelayStorePath = "config/mail-relay.json";

    /// <summary>
    /// Rölenin panel tarafındaki kaydı. PAROLA BURADA DA DÜZ DEĞİLDİR: DPAPI ile korunmuş
    /// metin saklanır (<see cref="ISecretProtector"/>), çünkü bu dosya yedeklere girer ve
    /// yedek, panelin kendisinden daha çok elden ele dolaşır.
    /// </summary>
    internal sealed class MailRelayStore
    {
        public bool Enabled { get; set; }

        public string Host { get; set; } = string.Empty;

        public int Port { get; set; }

        public string Protocol { get; set; } = "smtp";

        public bool ImplicitTls { get; set; }

        public bool AllowInvalidCerts { get; set; }

        public string Username { get; set; } = string.Empty;

        /// <summary>DPAPI ile korunmuş parola. Hiçbir DTO'ya, günlüğe ya da denetim kaydına geçmez.</summary>
        public string PasswordProtected { get; set; } = string.Empty;

        /// <summary>
        /// Röle açılmadan ÖNCEKİ yönlendirme ifadesi. Kapatınca buraya dönülür; yoksa yöneticinin
        /// kendi kurduğu yönlendirme, rölenin kapatılmasıyla sessizce varsayılana ezilirdi.
        /// </summary>
        public string PreviousRouteExpression { get; set; } = string.Empty;
    }

    /// <inheritdoc />
    public async Task<MailRelayDto> GetRelayAsync(CancellationToken cancellationToken = default)
    {
        var cli = await ResolveCliAsync(cancellationToken);
        if (cli is null)
        {
            return new MailRelayDto { Installed = false };
        }

        var password = await GetAdminPasswordAsync(cancellationToken);
        if (password is null)
        {
            return new MailRelayDto { Installed = true, Configured = false };
        }

        var env = BuildEnvironment(password);
        var kayit = await fileStore.ReadAsync<MailRelayStore?>(RelayStorePath, null, cancellationToken);
        var parolaVar = !string.IsNullOrWhiteSpace(kayit?.PasswordProtected);

        var strateji = await GetObjectAsync(cli, "MtaOutboundStrategy", "singleton", env, cancellationToken);
        if (strateji is null)
        {
            return new MailRelayDto { Installed = true, Configured = false, HasPassword = parolaVar };
        }

        // ETKİN Mİ sorusunun cevabı rotanın VARLIĞI değil, giden yönlendirmenin gerçekten onu
        // göstermesidir. Rota tanımlı ama yönlendirme hâlâ MX ise posta röleye HİÇ uğramaz;
        // paneli "röle açık" göstermek, tam da teşhis edilmek istenen sorunu gizlerdi.
        var etkin = string.Equals(ReadRouteElse(strateji.Value), RelayRouteExpression, StringComparison.Ordinal);

        var rotaId = await FindIdAsync("MtaRoute", "name", RelayRouteName, cancellationToken);
        var rota = rotaId is null ? null : await GetObjectAsync(cli, "MtaRoute", rotaId, env, cancellationToken);

        var dto = rota is null
            ? new MailRelayDto { Enabled = etkin }
            : ParseRelayRoute(rota.Value, etkin);

        dto.Installed = true;
        dto.Configured = true;
        dto.HasPassword = parolaVar;
        return dto;
    }

    /// <inheritdoc />
    public async Task<MailRelayDto> SetRelayAsync(SetMailRelayRequest request, CancellationToken cancellationToken = default)
    {
        var (cli, env) = await RequireCliAsync(cancellationToken);

        if (request.Enabled && string.IsNullOrWhiteSpace(request.Host))
        {
            throw new AppException("mail.relay_host_required", 400, 400);
        }

        var mevcutKayit = await fileStore.ReadAsync<MailRelayStore?>(RelayStorePath, null, cancellationToken);
        var yeniKayit = MergeRelayStore(mevcutKayit, request, secretProtector);

        var strateji = await GetObjectAsync(cli, "MtaOutboundStrategy", "singleton", env, cancellationToken)
            ?? throw new AppException("mail.relay_strategy_unavailable", 502, 502);
        var mevcutIfade = ReadRouteElse(strateji);
        var mevcutEslesmeler = ReadRouteMatchRaw(strateji);
        var rotaId = await FindIdAsync("MtaRoute", "name", RelayRouteName, cancellationToken);

        if (!request.Enabled)
        {
            // SIRA ÖNEMLİ: önce yönlendirme geri alınır, sonra rota silinir. Ters sırada,
            // yönlendirme bir an için var olmayan bir rotayı gösterir ve o aralıkta giden
            // postanın tamamı teslim edilemez.
            var geriIfade = string.IsNullOrWhiteSpace(mevcutKayit?.PreviousRouteExpression)
                ? DefaultRouteExpression
                : mevcutKayit!.PreviousRouteExpression;

            if (string.Equals(mevcutIfade, RelayRouteExpression, StringComparison.Ordinal))
            {
                await UpdateRouteExpressionAsync(cli, env, mevcutEslesmeler, geriIfade, cancellationToken);
            }

            if (rotaId is not null)
            {
                // Rota SİLİNİR (devre dışı bırakılmaz): kapatılmış bir rölenin kimlik bilgilerini
                // sunucuda bırakmak, kullanılmayan bir sırrı süresiz saklamak olurdu.
                var sil = await RunCliAsync(cli, $"delete MtaRoute --ids {Arg(rotaId)}", env, cancellationToken);
                if (!sil.Success)
                {
                    throw new AppException("mail.relay_failed", 502, 502, CombineError(sil));
                }
            }

            await ReloadMailSettingsAsync(cli, env, cancellationToken);
            await fileStore.WriteAsync(RelayStorePath, yeniKayit, cancellationToken);

            var kapali = await GetRelayAsync(cancellationToken);
            if (kapali.Enabled)
            {
                throw new AppException("mail.relay_unverified", 502, 502);
            }

            return kapali;
        }

        var parola = ResolveRelayPassword(yeniKayit, secretProtector);
        if (!string.IsNullOrWhiteSpace(yeniKayit.Username) && string.IsNullOrWhiteSpace(parola))
        {
            throw new AppException("mail.relay_password_required", 400, 400);
        }

        // `name` şemada oluşturulduktan sonra değiştirilemez; güncellemede gönderilmez.
        var payload = BuildRelayRouteJson(yeniKayit, parola, includeName: rotaId is null);
        var upsert = rotaId is null
            ? await RunCliJsonAsync(cli, "create MtaRoute", payload, env, cancellationToken)
            : await RunCliJsonAsync(cli, $"update MtaRoute {Arg(rotaId)}", payload, env, cancellationToken);

        if (!upsert.Success)
        {
            throw new AppException("mail.relay_failed", 502, 502, CombineError(upsert));
        }

        yeniKayit.PreviousRouteExpression = string.Equals(mevcutIfade, RelayRouteExpression, StringComparison.Ordinal)
            ? (mevcutKayit?.PreviousRouteExpression ?? DefaultRouteExpression)
            : (string.IsNullOrWhiteSpace(mevcutIfade) ? DefaultRouteExpression : mevcutIfade);

        await UpdateRouteExpressionAsync(cli, env, mevcutEslesmeler, RelayRouteExpression, cancellationToken);
        await ReloadMailSettingsAsync(cli, env, cancellationToken);
        await fileStore.WriteAsync(RelayStorePath, yeniKayit, cancellationToken);

        // "KAYDEDİLDİ" DEMEK YETMEZ. Ayarı geri okuyup sunucunun gerçekten röleye yönlendirdiğini
        // ölçüyoruz; aksi halde 25/tcp'si kapalı bir sunucuda kullanıcı röleyi kurduğunu sanır ve
        // posta yine hiç gitmez — yani özellik, çözdüğünü iddia ettiği sorunu çözmemiş olur.
        var dogrulama = await GetRelayAsync(cancellationToken);
        if (!dogrulama.Enabled
            || !string.Equals(dogrulama.Host, yeniKayit.Host, StringComparison.OrdinalIgnoreCase)
            || dogrulama.Port != yeniKayit.Port)
        {
            throw new AppException("mail.relay_unverified", 502, 502);
        }

        return dogrulama;
    }

    /// <summary>İstek + mevcut kayıttan yeni röle kaydını üretir. PAROLA ŞİFRELENEREK konur.</summary>
    /// <remarks>
    /// Ayrı ve saf bir metot: parolanın düz metin saklanmadığı ancak burada ölçülebilir. Kural
    /// akışın içine gömülseydi, doğrulaması için canlı bir posta sunucusu gerekirdi ve pratikte
    /// hiç test edilmezdi.
    /// </remarks>
    internal static MailRelayStore MergeRelayStore(MailRelayStore? mevcut, SetMailRelayRequest istek, ISecretProtector koruyucu)
    {
        if (!istek.Enabled)
        {
            // Kapatınca saklı parola da SİLİNİR: kullanılmayan bir sırrı tutmanın tek etkisi,
            // sızma yüzeyini büyütmektir.
            return new MailRelayStore { Enabled = false, PreviousRouteExpression = mevcut?.PreviousRouteExpression ?? string.Empty };
        }

        var korunmus = string.IsNullOrEmpty(istek.Password)
            ? (mevcut?.PasswordProtected ?? string.Empty)
            : koruyucu.Protect(istek.Password);

        return new MailRelayStore
        {
            Enabled = true,
            Host = (istek.Host ?? string.Empty).Trim().ToLowerInvariant(),
            Port = Math.Clamp(istek.Port, 1, 65535),
            Protocol = string.Equals((istek.Protocol ?? string.Empty).Trim(), "lmtp", StringComparison.OrdinalIgnoreCase) ? "lmtp" : "smtp",
            ImplicitTls = istek.ImplicitTls,
            AllowInvalidCerts = istek.AllowInvalidCerts,
            Username = (istek.Username ?? string.Empty).Trim(),
            PasswordProtected = korunmus,
            PreviousRouteExpression = mevcut?.PreviousRouteExpression ?? string.Empty
        };
    }

    /// <summary>Saklı parolayı çözer; çözülemiyorsa null (bozuk bir sırla sunucuya yazmayız).</summary>
    internal static string? ResolveRelayPassword(MailRelayStore kayit, ISecretProtector koruyucu)
    {
        if (string.IsNullOrWhiteSpace(kayit.PasswordProtected))
        {
            return null;
        }

        try
        {
            return koruyucu.Unprotect(kayit.PasswordProtected);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Stalwart <c>MtaRoute</c> (Relay varyantı) gövdesi.
    /// </summary>
    /// <remarks>
    /// Alan adları şema referansından: <c>address</c>, <c>port</c>, <c>protocol</c>,
    /// <c>implicitTls</c>, <c>allowInvalidCerts</c>, <c>authUsername</c> ve <c>authSecret</c>.
    /// <c>authSecret</c> ZORUNLUDUR; kimlik doğrulama kullanılmıyorsa <c>{"@type":"None"}</c>
    /// verilir — alanı hiç göndermemek nesneyi geçersiz kılar.
    /// </remarks>
    internal static string BuildRelayRouteJson(MailRelayStore kayit, string? duzParola, bool includeName)
    {
        var alanlar = new List<string>
        {
            "\"@type\":\"Relay\""
        };

        if (includeName)
        {
            alanlar.Add($"\"name\":{JsonValue(RelayRouteName)}");
        }

        alanlar.Add($"\"address\":{JsonValue(kayit.Host)}");
        alanlar.Add($"\"port\":{kayit.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        alanlar.Add($"\"protocol\":{JsonValue(kayit.Protocol)}");
        alanlar.Add($"\"implicitTls\":{(kayit.ImplicitTls ? "true" : "false")}");
        alanlar.Add($"\"allowInvalidCerts\":{(kayit.AllowInvalidCerts ? "true" : "false")}");

        if (!string.IsNullOrWhiteSpace(kayit.Username))
        {
            alanlar.Add($"\"authUsername\":{JsonValue(kayit.Username)}");
        }

        alanlar.Add(string.IsNullOrEmpty(duzParola)
            ? "\"authSecret\":{\"@type\":\"None\"}"
            : $"\"authSecret\":{{\"@type\":\"Value\",\"secret\":{JsonValue(duzParola)}}}");

        return "{" + string.Join(",", alanlar) + "}";
    }

    /// <summary>Canlı <c>MtaRoute</c> nesnesini panel görünümüne çevirir (parola sunucudan da dönmez).</summary>
    internal static MailRelayDto ParseRelayRoute(JsonElement rota, bool etkin) => new()
    {
        Enabled = etkin,
        Host = GetField(rota, "address"),
        Port = (int)GetLongField(rota, "port"),
        Protocol = GetField(rota, "protocol"),
        ImplicitTls = GetBoolOr(rota, "implicitTls", false),
        AllowInvalidCerts = GetBoolOr(rota, "allowInvalidCerts", false),
        Username = GetField(rota, "authUsername")
    };

    /// <summary>
    /// Yönlendirme ifadesini günceller: yöneticinin kendi <c>match</c> kuralları KORUNUR, yalnız
    /// <c>else</c> dalı değiştirilir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Neden yalnız <c>else</c>: şemadaki varsayılan <c>{"else":"'mx'"}</c>'tir ve varsayılan
    /// kurulumda yerel posta kutuları sorunsuz teslim edilir — yani YEREL teslim bu ifadeye bağlı
    /// değildir. Dolayısıyla <c>else</c>'i röleyle değiştirmek tam olarak "dışarı giden postayı
    /// röleye ver" demektir. <c>match</c>'e dokunmak ise yöneticinin kendi kurallarını silerdi.
    /// </para>
    /// <para>
    /// <c>match</c> sunucudan geldiği HÂLİYLE geri yazılır; biçimini yeniden üretmeye çalışmak,
    /// doğrulanmamış bir şema varsayımı olurdu.
    /// </para>
    /// </remarks>
    private async Task UpdateRouteExpressionAsync(
        string cli,
        IReadOnlyDictionary<string, string> env,
        string eslesmelerHam,
        string elseIfadesi,
        CancellationToken cancellationToken)
    {
        var ifade = $"route={{\"match\":{eslesmelerHam},\"else\":{JsonValue(elseIfadesi)}}}";
        var sonuc = await RunCliAsync(
            cli,
            $"update MtaOutboundStrategy singleton --field {CliJsonArg(ifade)}",
            env,
            cancellationToken);

        if (!sonuc.Success)
        {
            throw new AppException("mail.relay_failed", 502, 502, CombineError(sonuc));
        }
    }

    /// <summary>Yönlendirme ifadesinin <c>else</c> dalı (ör. <c>'mx'</c>).</summary>
    internal static string ReadRouteElse(JsonElement strateji) =>
        strateji.ValueKind == JsonValueKind.Object
        && strateji.TryGetProperty("route", out var rota)
        && rota.ValueKind == JsonValueKind.Object
        && rota.TryGetProperty("else", out var elseDegeri)
        && elseDegeri.ValueKind == JsonValueKind.String
            ? elseDegeri.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>Yönlendirme ifadesinin <c>match</c> dalının HAM JSON metni; yoksa boş liste.</summary>
    internal static string ReadRouteMatchRaw(JsonElement strateji) =>
        strateji.ValueKind == JsonValueKind.Object
        && strateji.TryGetProperty("route", out var rota)
        && rota.ValueKind == JsonValueKind.Object
        && rota.TryGetProperty("match", out var eslesme)
        && eslesme.ValueKind is JsonValueKind.Array or JsonValueKind.Object
            ? eslesme.GetRawText()
            : "[]";

    /// <summary>Ayar değişikliğini yeniden başlatmadan devreye alır.</summary>
    private static async Task ReloadMailSettingsAsync(string cli, IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken)
        => _ = await RunCliJsonAsync(cli, "create Action", "{\"@type\":\"ReloadSettings\"}", env, cancellationToken);

    /// <summary>
    /// Bool alanı okur; alan YOKSA verilen varsayılan döner.
    /// </summary>
    /// <remarks>
    /// <see cref="GetBoolField"/> alan bulunmadığında <c>true</c> döner (etkinlik alanları için
    /// doğru varsayım). Röle bayrakları için o varsayım tehlikelidir: sunucu <c>implicitTls</c>
    /// göndermediğinde panel "doğrudan TLS açık" gösterir ve yönetici yanlış portu ayarlar.
    /// </remarks>
    private static bool GetBoolOr(JsonElement element, string name, bool varsayilan)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return varsayilan;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            _ => varsayilan
        };
    }

    // --- Posta istemcisi ayarları (autodiscover/autoconfig kaynağı) ---

    /// <inheritdoc />
    public async Task<MailClientSettingsDto?> GetClientSettingsAsync(string domain, CancellationToken cancellationToken = default)
    {
        var normalized = (domain ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return null;
        }

        var cli = await ResolveCliAsync(cancellationToken);
        var password = cli is null ? null : await GetAdminPasswordAsync(cancellationToken);
        if (cli is null || password is null)
        {
            return null;
        }

        var env = BuildEnvironment(password);

        // PANELDE TANIMLI MI? Bu uçlar kimlik doğrulamasız çağrılır (istemciler öyle çağırır).
        // Tanımsız alan adına cevap vermek, sunucuyu "burada hangi alanlar barınıyor" sorusunun
        // ücretsiz tarayıcısına çevirirdi.
        if (await FindIdAsync("domain", "name", normalized, cancellationToken) is null)
        {
            return null;
        }

        var hostName = await ResolveClientHostNameAsync(cli, env, normalized, cancellationToken);
        if (string.IsNullOrWhiteSpace(hostName))
        {
            return null;
        }

        var sunucular = new List<MailClientServerDto>();
        foreach (var dinleyici in await GetListenersAsync(cancellationToken))
        {
            var protokol = NormalizeClientProtocol(dinleyici.Protocol);
            if (protokol is null)
            {
                continue;
            }

            foreach (var port in ListenerPorts(dinleyici.Binds))
            {
                // SMTP 25 istemciye VERİLMEZ: RFC 6409 gereği ileti gönderimi (submission) 25'ten
                // değil, kimlik doğrulamalı gönderim portundan yapılır. 25'i yayınlamak, istemciyi
                // kimlik doğrulamayan MTA portuna yönlendirmek olurdu.
                if (protokol == "smtp" && port == 25)
                {
                    continue;
                }

                if (sunucular.Any(item => item.Protocol == protokol && item.Port == port))
                {
                    continue;
                }

                sunucular.Add(new MailClientServerDto
                {
                    Protocol = protokol,
                    Host = hostName,
                    Port = port,
                    SocketType = SocketTypeForPort(port)
                });
            }
        }

        // Ölçemediysek YAYINLAMAYIZ. Uydurma bir port listesi, kullanıcıyı elle kurmaktan daha
        // kötü bir yere düşürür: ayarlar "otomatik geldi" ama çalışmaz.
        if (sunucular.Count == 0)
        {
            return null;
        }

        return new MailClientSettingsDto
        {
            Domain = normalized,
            HostName = hostName,
            Servers = sunucular.OrderBy(item => item.Protocol, StringComparer.Ordinal).ThenBy(item => item.Port).ToArray()
        };
    }

    /// <summary>
    /// İstemcinin bağlanacağı sunucu adı.
    /// </summary>
    /// <remarks>
    /// Sıra kasıtlı: (1) panelin aldığı TLS sertifikasının adı — istemci sertifikayı bu ada göre
    /// doğrular, (2) sunucunun SMTP karşılama adı, (3) panelin DNS panelinin zaten yayınladığı
    /// <c>mail.&lt;alan&gt;</c>. Üçü de yapılandırmadan gelir; makine adı gibi tahminler kullanılmaz.
    /// </remarks>
    private async Task<string> ResolveClientHostNameAsync(
        string cli,
        IReadOnlyDictionary<string, string> env,
        string domain,
        CancellationToken cancellationToken)
    {
        var sertifika = await fileStore.ReadAsync<MailCertificateState?>(CertificateStatePath, null, cancellationToken);
        if (!string.IsNullOrWhiteSpace(sertifika?.HostName))
        {
            return sertifika!.HostName.Trim().ToLowerInvariant();
        }

        var ayarlar = await GetObjectAsync(cli, "SystemSettings", "singleton", env, cancellationToken);
        var karsilama = ayarlar is null ? string.Empty : GetField(ayarlar.Value, "defaultHostname");
        return string.IsNullOrWhiteSpace(karsilama)
            ? $"mail.{domain}"
            : karsilama.Trim().ToLowerInvariant();
    }

    /// <summary>Dinleyici protokolünü istemci protokolüne eşler; istemciye ait olmayanlar null.</summary>
    private static string? NormalizeClientProtocol(string? protocol)
    {
        var deger = (protocol ?? string.Empty).Trim().ToLowerInvariant();
        if (deger.Contains("imap", StringComparison.Ordinal))
        {
            return "imap";
        }

        if (deger.Contains("pop", StringComparison.Ordinal))
        {
            return "pop3";
        }

        // "smtp" ve "submission" aynı protokoldür; ikisi de istemci gönderimi içindir.
        return deger.Contains("smtp", StringComparison.Ordinal) || deger.Contains("submission", StringComparison.Ordinal)
            ? "smtp"
            : null;
    }

    /// <summary>"0.0.0.0:143" / "[::]:993" gibi bağlama dizgilerinden port numaralarını çıkarır.</summary>
    private static IEnumerable<int> ListenerPorts(IEnumerable<string> binds)
    {
        foreach (var bind in binds ?? [])
        {
            var deger = (bind ?? string.Empty).Trim();
            var index = deger.LastIndexOf(':');
            if (index < 0 || index == deger.Length - 1)
            {
                continue;
            }

            if (int.TryParse(deger[(index + 1)..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var port)
                && port is > 0 and <= 65535)
            {
                yield return port;
            }
        }
    }

    /// <summary>
    /// Portun şifreleme biçimi.
    /// </summary>
    /// <remarks>
    /// IANA'nın örtük-TLS portları (465 submissions, 993 imaps, 995 pop3s) doğrudan TLS ile
    /// açılır; kalanlar STARTTLS ile yükseltilir. Bu tablo bir tahmin değil, protokollerin
    /// kendi tanımıdır — PORTLARIN KENDİSİ sunucunun gerçek dinleyicilerinden okunur, burada
    /// yalnız o portun standart anlamı uygulanır. (Dinleyici nesnesinde TLS'i doğrudan bildiren
    /// bir alan bu panelde doğrulanamadı; doğrulanınca buranın yerini alabilir.)
    /// </remarks>
    private static string SocketTypeForPort(int port) => port is 465 or 993 or 995 ? "SSL" : "STARTTLS";
}
