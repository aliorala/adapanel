using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>
/// Dalga 3: posta kutusu askıya alma, alias güncelleme, kutu başına yönlendirme ve DKIM rotasyonu (Stalwart).
/// </summary>
/// <remarks>
/// Askıya alma yöntemi göç koduyla AYNIDIR (<c>SetMailboxActiveAsync</c>: permissions Merge + disabledPermissions);
/// o yol testle kanıtlıdır. Her yazma GERİ OKUNUR; okuma tutmuyorsa istisna fırlar, "tamam" denmez.
/// Yönlendirme ve DKIM aşamaları (SieveUserScript, DkimSignature stage) bu makinede canlı Stalwart olmadan
/// ölçülemedi: CLI reddederse işlem başarısız olur ve eski durum korunur.
/// </remarks>
public sealed partial class MailService
{
    internal const string ForwardScriptName = "adapanel-forward";
    internal const string DkimRotationStorePath = "config/mail-dkim-rotations.json";

    /// <summary>Eski anahtarın DNS'te kalacağı süre (önbellekteki imzaların doğrulanabilmesi için).</summary>
    internal static readonly TimeSpan DkimGracePeriod = TimeSpan.FromDays(7);

    private static readonly Regex ForwardAddressPattern = new(@"^[^\s@""'\\<>;,]+@[^\s@""'\\<>;,]+\.[^\s@""'\\<>;,]+$", RegexOptions.Compiled);

    // --- Askıya alma ---

    public async Task<MailWorkspaceDto> SetAccountActiveAsync(SetMailAccountActiveRequest request, CancellationToken cancellationToken = default)
    {
        var normalized = request.Address.Trim().ToLowerInvariant();
        var domain = ExtractDomain(normalized);
        var (cli, env) = await RequireCliAsync(cancellationToken);
        var id = await FindIdAsync("account", "emailAddress", normalized, cancellationToken)
            ?? throw new AppException("mail.account_not_found", 404, 404);

        var permissions = new Dictionary<string, object>
        {
            ["@type"] = "Merge",
            ["enabledPermissions"] = new Dictionary<string, bool>(),
            ["disabledPermissions"] = request.Active
                ? new Dictionary<string, bool>()
                : new Dictionary<string, bool> { ["authenticate"] = true, ["emailReceive"] = true, ["emailSend"] = true }
        };
        var result = await RunCliJsonAsync(cli, $"update account {Arg(id)}", JsonSerializer.Serialize(new { permissions }), env, cancellationToken);
        if (!result.Success)
        {
            throw MapMailError(result);
        }

        // GERİ OKU: yazma çıkış koduyla "tamam" dese de sunucudaki hal ölçülmeden başarı denmez.
        var detail = await GetObjectAsync(cli, "account", id, env, cancellationToken);
        var measured = detail is null ? null : MigrationAccountActive(detail.Value);
        if (measured != request.Active)
        {
            throw new AppException("mail.account_active_not_applied", 500, 500,
                $"'{normalized}': istenen etkin={request.Active}, sunucudan okunan={(measured?.ToString() ?? "olculemedi")}.");
        }

        return await GetWorkspaceAsync(domain, cancellationToken);
    }

    // --- Alias güncelleme ---

    public async Task<MailWorkspaceDto> UpdateAliasAsync(UpdateMailAliasRequest request, CancellationToken cancellationToken = default)
    {
        var normalized = request.Address.Trim().ToLowerInvariant();
        var domain = ExtractDomain(normalized);
        var localPart = normalized[..normalized.IndexOf('@')];
        var domainId = await FindIdAsync("domain", "name", domain, cancellationToken)
            ?? throw new AppException("mail.operation_failed", 404, 404, $"domain not found: {domain}");
        var (cli, env) = await RequireCliAsync(cancellationToken);

        var accounts = await QueryAsync(cli, "account", env, cancellationToken);
        if (!accounts.Measured)
        {
            throw new AppException("mail.accounts_unmeasured", 502, 502, domain);
        }

        string? ownerId = null;
        JsonElement? ownerDetail = null;
        string? entryKey = null;
        var currentEnabled = true;
        foreach (var brief in accounts.Items)
        {
            var accountId = GetField(brief, "id");
            if (string.IsNullOrWhiteSpace(accountId)) continue;
            var detail = await GetObjectAsync(cli, "account", accountId, env, cancellationToken);
            if (detail is null || !detail.Value.TryGetProperty("aliases", out var map) || map.ValueKind != JsonValueKind.Object) continue;
            foreach (var entry in map.EnumerateObject())
            {
                if (string.Equals(GetField(entry.Value, "name"), localPart, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(GetField(entry.Value, "domainId"), domainId, StringComparison.Ordinal))
                {
                    ownerId = accountId;
                    ownerDetail = detail;
                    entryKey = entry.Name;
                    currentEnabled = !entry.Value.TryGetProperty("enabled", out var flag) || flag.ValueKind != JsonValueKind.False;
                    break;
                }
            }

            if (ownerId is not null) break;
        }

        if (ownerId is null || ownerDetail is null || entryKey is null)
        {
            throw new AppException("mail.operation_failed", 404, 404, $"alias not found: {normalized}");
        }

        var desiredEnabled = request.Active ?? currentEnabled;
        var targetAddress = string.IsNullOrWhiteSpace(request.Target) ? null : NormalizeAddress(request.Target, domain);
        string? newOwnerId = null;
        if (targetAddress is not null)
        {
            newOwnerId = await FindIdAsync("account", "emailAddress", targetAddress, cancellationToken)
                ?? throw new AppException("mail.operation_failed", 404, 404, $"target account not found: {targetAddress}");
            if (string.Equals(newOwnerId, ownerId, StringComparison.Ordinal)) newOwnerId = null; // hedef değişmedi
        }

        var entryJson = $"{{\"name\":{JsonValue(localPart)},\"domainId\":{JsonValue(domainId)},\"enabled\":{(desiredEnabled ? "true" : "false")}}}";

        if (newOwnerId is null)
        {
            // Aynı hesapta yerinde güncelle (yalnız etkin bayrağı).
            var entries = new List<string>();
            foreach (var entry in ownerDetail.Value.GetProperty("aliases").EnumerateObject())
            {
                entries.Add(entry.Name == entryKey
                    ? $"{JsonValue(entry.Name)}:{entryJson}"
                    : $"{JsonValue(entry.Name)}:{entry.Value.GetRawText()}");
            }

            await UpdateAliasMapAsync(cli, env, ownerId, entries, cancellationToken);
        }
        else
        {
            // Stalwart adres tekilliğini zorlayabilir: yeni hesaba EKLEMEK için adres önce eskiden ÇIKMALI.
            // Sıra: eskiden çıkar -> yeniye ekle -> eklenemezse eski hesaptaki orijinal haritayı GERİ YAZ (alias kaybolmaz).
            var oldOriginal = ExportAliasEntries(ownerDetail.Value);
            var oldWithout = ExportAliasEntries(ownerDetail.Value, skipName: localPart, skipDomainId: domainId).Entries;
            await UpdateAliasMapAsync(cli, env, ownerId, oldWithout, cancellationToken); // başarısızsa hiçbir şey değişmedi

            try
            {
                var newDetail = await GetObjectAsync(cli, "account", newOwnerId, env, cancellationToken)
                    ?? throw new AppException("mail.operation_failed", 500, 500);
                var newEntries = ExportAliasEntries(newDetail);
                newEntries.Add($"\"{NextNumericKey(newDetail)}\":{entryJson}");
                await UpdateAliasMapAsync(cli, env, newOwnerId, newEntries, cancellationToken);
            }
            catch
            {
                try
                {
                    await UpdateAliasMapAsync(cli, env, ownerId, oldOriginal, CancellationToken.None);
                }
                catch (Exception rollback)
                {
                    logger.LogError(rollback, "Alias taşıma geri alınamadı, alias kaybolmuş olabilir: {Alias}", normalized);
                }

                throw;
            }
        }

        var workspace = await GetWorkspaceAsync(domain, cancellationToken);
        var landed = workspace.Aliases.FirstOrDefault(item => string.Equals(item.Address, normalized, StringComparison.OrdinalIgnoreCase));
        if (landed is null
            || landed.Active == !desiredEnabled
            || (targetAddress is not null && !string.Equals(landed.Target, targetAddress, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AppException("mail.alias_update_not_applied", 500, 500, normalized);
        }

        return workspace;
    }

    private async Task UpdateAliasMapAsync(string cli, IReadOnlyDictionary<string, string> env, string accountId,
        IReadOnlyList<string> entries, CancellationToken cancellationToken)
    {
        var payload = "aliases={" + string.Join(",", entries) + "}";
        var result = await RunCliAsync(cli, $"update account {Arg(accountId)} --field {CliJsonArg(payload)}", env, cancellationToken);
        if (!result.Success)
        {
            throw MapMailError(result);
        }
    }

    // --- Kutu başına yönlendirme (hesap sieve betiği: her alıcı için ayrı çalışır; başkasının postasına dokunamaz) ---

    internal static string BuildForwardScript(string target, bool keepCopy)
    {
        if (!ForwardAddressPattern.IsMatch(target)) throw new AppException("mail.forward_invalid_target", 400, 400);
        var quoted = "\"" + target.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        return keepCopy
            ? $"require [\"copy\"];\r\n# AdaPanel yonlendirme / forward (kopya kalir)\r\nredirect :copy {quoted};\r\n"
            : $"# AdaPanel yonlendirme / forward (kopya kalmaz)\r\nredirect {quoted};\r\n";
    }

    internal static MailForwardDto? ParseForwardScript(string address, string contents)
    {
        var match = Regex.Match(contents ?? string.Empty, @"^\s*redirect\s+(?<copy>:copy\s+)?""(?<to>[^""]+)""\s*;", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return match.Success
            ? new MailForwardDto { Address = address, Enabled = true, Target = match.Groups["to"].Value, KeepCopy = match.Groups["copy"].Success }
            : null;
    }

    private async Task<(string? ScriptId, string Contents)> FindForwardScriptAsync(string cli, IReadOnlyDictionary<string, string> env,
        string accountId, CancellationToken cancellationToken)
    {
        var scripts = await QueryAsync(cli, "SieveUserScript", env, cancellationToken);
        if (!scripts.Measured)
        {
            throw new AppException("mail.lookup_unmeasured", 502, 502, "SieveUserScript");
        }

        foreach (var brief in scripts.Items)
        {
            if (!string.Equals(GetField(brief, "accountId"), accountId, StringComparison.Ordinal)
                || !string.Equals(GetField(brief, "name"), ForwardScriptName, StringComparison.Ordinal))
            {
                continue;
            }

            var id = GetField(brief, "id");
            var detail = await GetObjectAsync(cli, "SieveUserScript", id, env, cancellationToken);
            return (id, detail is null ? string.Empty : GetField(detail.Value, "contents"));
        }

        return (null, string.Empty);
    }

    public async Task<MailForwardDto> GetForwardAsync(string address, CancellationToken cancellationToken = default)
    {
        var normalized = address.Trim().ToLowerInvariant();
        var (cli, env) = await RequireCliAsync(cancellationToken);
        var accountId = await FindIdAsync("account", "emailAddress", normalized, cancellationToken)
            ?? throw new AppException("mail.account_not_found", 404, 404);
        var (scriptId, contents) = await FindForwardScriptAsync(cli, env, accountId, cancellationToken);
        return (scriptId is null ? null : ParseForwardScript(normalized, contents))
            ?? new MailForwardDto { Address = normalized, Enabled = false };
    }

    public async Task<MailForwardDto> SetForwardAsync(SetMailForwardRequest request, CancellationToken cancellationToken = default)
    {
        var normalized = request.Address.Trim().ToLowerInvariant();
        var target = (request.Target ?? string.Empty).Trim().ToLowerInvariant();
        if (request.Enabled)
        {
            if (string.Equals(target, normalized, StringComparison.OrdinalIgnoreCase))
            {
                throw new AppException("mail.forward_to_self", 400, 400);
            }

            BuildForwardScript(target, request.KeepCopy); // doğrular (geçersiz adres → 400)
        }

        var (cli, env) = await RequireCliAsync(cancellationToken);
        var accountId = await FindIdAsync("account", "emailAddress", normalized, cancellationToken)
            ?? throw new AppException("mail.account_not_found", 404, 404);
        var (scriptId, _) = await FindForwardScriptAsync(cli, env, accountId, cancellationToken);

        if (!request.Enabled)
        {
            if (scriptId is not null)
            {
                var deleted = await RunCliAsync(cli, $"delete SieveUserScript --ids {Arg(scriptId)}", env, cancellationToken);
                if (!deleted.Success) throw MapMailError(deleted);
            }

            var after = await FindForwardScriptAsync(cli, env, accountId, cancellationToken);
            if (after.ScriptId is not null) throw new AppException("mail.forward_not_applied", 500, 500, normalized);
            return new MailForwardDto { Address = normalized, Enabled = false };
        }

        var payload = JsonSerializer.Serialize(new
        {
            accountId,
            name = ForwardScriptName,
            isActive = true,
            description = "AdaPanel forward",
            contents = BuildForwardScript(target, request.KeepCopy)
        });
        var write = scriptId is null
            ? await RunCliJsonAsync(cli, "create SieveUserScript", payload, env, cancellationToken)
            : await RunCliJsonAsync(cli, $"update SieveUserScript {Arg(scriptId)}", payload, env, cancellationToken);
        if (!write.Success) throw MapMailError(write);

        var measured = await FindForwardScriptAsync(cli, env, accountId, cancellationToken);
        var parsed = measured.ScriptId is null ? null : ParseForwardScript(normalized, measured.Contents);
        if (parsed is null
            || !string.Equals(parsed.Target, target, StringComparison.OrdinalIgnoreCase)
            || parsed.KeepCopy != request.KeepCopy)
        {
            throw new AppException("mail.forward_not_applied", 500, 500, normalized);
        }

        return parsed;
    }

    // --- DKIM anahtar rotasyonu ---

    internal sealed class DkimRotationEntry
    {
        public string Domain { get; set; } = string.Empty;
        public string OldSelector { get; set; } = string.Empty;
        public string NewSelector { get; set; } = string.Empty;
        public DateTime RetireAfterUtc { get; set; }
    }

    public async Task<MailDkimRotationDto> RotateDkimAsync(RotateMailDkimRequest request, CancellationToken cancellationToken = default)
    {
        var domain = request.Domain.Trim().ToLowerInvariant();
        var (cli, env) = await RequireCliAsync(cancellationToken);
        var domainId = await FindIdAsync("domain", "name", domain, cancellationToken)
            ?? throw new AppException("mail.operation_failed", 404, 404, $"domain not found: {domain}");

        var signatures = await QueryAsync(cli, "DkimSignature", env, cancellationToken);
        if (!signatures.Measured) throw new AppException("mail.dkim_unmeasured", 502, 502, domain);

        // Aktif imzalar (alan başına RSA + Ed25519 olabilir). Rotasyon aktif RSA imzayı döndürür.
        JsonElement? old = null;
        string? oldId = null;
        foreach (var brief in signatures.Items)
        {
            if (!string.Equals(GetField(brief, "domainId"), domainId, StringComparison.Ordinal)
                || !string.Equals(GetField(brief, "stage"), "active", StringComparison.OrdinalIgnoreCase)) continue;
            var id = GetField(brief, "id");
            var detail = await GetObjectAsync(cli, "DkimSignature", id, env, cancellationToken);
            if (detail is null) continue;
            if (GetField(detail.Value, "@type").Contains("Ed25519", StringComparison.OrdinalIgnoreCase)) continue;
            old = detail;
            oldId = id;
            break;
        }

        if (old is null || oldId is null)
        {
            throw new AppException("mail.dkim_no_active_key", 409, 409, domain);
        }

        var oldSelector = GetField(old.Value, "selector");
        var type = GetField(old.Value, "@type");
        var existingSelectors = signatures.Items.Select(item => GetField(item, "selector")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newSelector = $"r{DateTime.UtcNow:yyyyMMdd}";
        for (var suffix = 2; existingSelectors.Contains(newSelector); suffix++)
        {
            newSelector = $"r{DateTime.UtcNow:yyyyMMdd}{suffix}";
        }

        // 1) Yeni anahtar BEKLEMEDE oluşturulur: henüz imzalamaz, eski anahtar aktif kalır.
        var create = await RunCliJsonAsync(cli, $"create DkimSignature/{type}",
            JsonSerializer.Serialize(new { domainId, selector = newSelector, stage = "pending" }), env, cancellationToken);
        if (!create.Success) throw MapMailError(create);

        string? newId = null;
        try
        {
            newId = await FindDkimIdAsync(cli, env, domainId, newSelector, cancellationToken);
            if (newId is null)
            {
                // Kimlik okunamadı ama anahtar oluşmuş olabilir: seçiciye göre bulunup silinemezse en azından raporla.
                throw new AppException("mail.dkim_rotation_unverified", 502, 502, newSelector);
            }
            var created = await GetObjectAsync(cli, "DkimSignature", newId, env, cancellationToken);
            var publicKey = created is null ? string.Empty : GetField(created.Value, "publicKey");
            if (string.IsNullOrWhiteSpace(publicKey))
            {
                throw new AppException("mail.dkim_rotation_unverified", 502, 502, newSelector);
            }

            // 2) DNS'e yayınla. Yayınlanamıyorsa (ikincil bölge, DNS rolü yok, başka sağlayıcı) GEÇİŞ YAPILMAZ:
            //    yayınlanmamış anahtara geçmek giden postayı doğrulanamaz kılar.
            var record = new UpsertDnsRecordRequest
            {
                Type = "TXT",
                Host = $"{newSelector}._domainkey",
                Value = $"v=DKIM1; k=rsa; p={publicKey}",
                Ttl = 3600
            };
            var site = ResolveManagedIdentitySite(await iisSiteService.GetSitesAsync(cancellationToken), domain);
            MailDnsPublication.EnsureDomainZone(await dnsService.GetZoneAsync(site.Name, cancellationToken), domain);
            var zone = await dnsService.UpsertRecordsAsync(site.Name, [record], cancellationToken);
            var verdict = MailDnsPublication.Evaluate(zone, 1);
            if (!verdict.Published)
            {
                throw new AppException("mail.dkim_rotation_dns_not_published", 409, 409, verdict.StatusKey);
            }

            // 3) Geçiş: yeni anahtar aktif olur.
            var activate = await RunCliAsync(cli, $"update DkimSignature {Arg(newId)} --field stage=active", env, cancellationToken);
            if (!activate.Success) throw MapMailError(activate);
            var activated = await GetObjectAsync(cli, "DkimSignature", newId, env, cancellationToken);
            if (activated is null || !string.Equals(GetField(activated.Value, "stage"), "active", StringComparison.OrdinalIgnoreCase))
            {
                throw new AppException("mail.dkim_rotation_unverified", 502, 502, newSelector);
            }
        }
        catch
        {
            // Başarısız: eski anahtar hâlâ aktif. Beklemedeki yeni anahtarı ve DNS kaydını temizle (en iyi çaba).
            await TryAbortDkimRotationAsync(cli, env, domain, newId, newSelector, cancellationToken);
            throw;
        }

        // 4) Eski anahtar emekliye ayrılır (artık imzalamaz; DNS kaydı bekleme süresince KALIR).
        var retiring = await RunCliAsync(cli, $"update DkimSignature {Arg(oldId)} --field stage=retiring", env, cancellationToken);
        if (!retiring.Success)
        {
            logger.LogWarning("Eski DKIM anahtarı 'retiring' yapılamadı ({Domain}/{Selector}); iki anahtar da aktif kalabilir.", domain, oldSelector);
        }

        var retireAfter = DateTime.UtcNow + DkimGracePeriod;
        await fileStore.UpdateAsync(DkimRotationStorePath, new List<DkimRotationEntry>(), list =>
        {
            list.Add(new DkimRotationEntry { Domain = domain, OldSelector = oldSelector, NewSelector = newSelector, RetireAfterUtc = retireAfter });
            return list;
        }, cancellationToken);

        return new MailDkimRotationDto
        {
            Domain = domain,
            OldSelector = oldSelector,
            NewSelector = newSelector,
            RetireAfterUtc = retireAfter,
            Status = "rotated"
        };
    }

    private async Task<string?> FindDkimIdAsync(string cli, IReadOnlyDictionary<string, string> env, string domainId, string selector,
        CancellationToken cancellationToken)
    {
        var all = await QueryAsync(cli, "DkimSignature", env, cancellationToken);
        if (!all.Measured) return null;
        foreach (var item in all.Items)
        {
            if (string.Equals(GetField(item, "domainId"), domainId, StringComparison.Ordinal)
                && string.Equals(GetField(item, "selector"), selector, StringComparison.OrdinalIgnoreCase))
            {
                return GetField(item, "id");
            }
        }

        return null;
    }

    private async Task TryAbortDkimRotationAsync(string cli, IReadOnlyDictionary<string, string> env, string domain,
        string? newId, string newSelector, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(newId))
            {
                // Kimlik okunamamış olabilir ama anahtar oluşmuştur: seçiciyle yeniden ara ve temizle.
                var domainId = await FindIdAsync("domain", "name", domain, CancellationToken.None);
                if (domainId is not null) newId = await FindDkimIdAsync(cli, env, domainId, newSelector, CancellationToken.None);
            }

            if (!string.IsNullOrWhiteSpace(newId))
            {
                await RunCliAsync(cli, $"delete DkimSignature --ids {Arg(newId)}", env, CancellationToken.None);
            }

            await TryRemoveDkimDnsRecordAsync(domain, newSelector, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "DKIM rotasyonu geri alınırken temizlik tamamlanamadı: {Domain}/{Selector}", domain, newSelector);
        }
    }

    private async Task<bool> TryRemoveDkimDnsRecordAsync(string domain, string selector, CancellationToken cancellationToken)
    {
        try
        {
            var site = ResolveManagedIdentitySite(await iisSiteService.GetSitesAsync(cancellationToken), domain);
            var zone = await dnsService.GetZoneAsync(site.Name, cancellationToken);
            MailDnsPublication.EnsureDomainZone(zone, domain); // başka bölgedeki kaydı asla silme
            var host = $"{selector}._domainkey";
            var match = zone.Records.FirstOrDefault(item =>
                string.Equals(item.Type, "TXT", StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Host.TrimEnd('.'), host, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                await dnsService.DeleteRecordAsync(site.Name, match.Id, cancellationToken);
            }

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "DKIM DNS kaydı kaldırılamadı: {Domain}/{Selector}", domain, selector);
            return false;
        }
    }

    public async Task<int> FinalizeDkimRotationsAsync(CancellationToken cancellationToken = default)
    {
        var pending = await fileStore.ReadAsync(DkimRotationStorePath, new List<DkimRotationEntry>(), cancellationToken);
        var due = pending.Where(item => item.RetireAfterUtc <= DateTime.UtcNow).ToList();
        if (due.Count == 0) return 0;

        var (cli, env) = await RequireCliAsync(cancellationToken);
        var done = new List<DkimRotationEntry>();
        foreach (var entry in due)
        {
            try
            {
                var domainId = await FindIdAsync("domain", "name", entry.Domain, cancellationToken);
                if (domainId is not null)
                {
                    var oldId = await FindDkimIdAsync(cli, env, domainId, entry.OldSelector, cancellationToken);
                    if (oldId is not null)
                    {
                        // Yeni anahtar hâlâ aktif değilse eski kaldırılmaz (imzasız posta olmasın).
                        var current = await GetObjectAsync(cli, "DkimSignature",
                            await FindDkimIdAsync(cli, env, domainId, entry.NewSelector, cancellationToken) ?? string.Empty, env, cancellationToken);
                        if (current is null || !string.Equals(GetField(current.Value, "stage"), "active", StringComparison.OrdinalIgnoreCase))
                        {
                            logger.LogWarning("DKIM: yeni anahtar aktif değil, eski anahtar korunuyor: {Domain}", entry.Domain);
                            continue;
                        }

                        var removed = await RunCliAsync(cli, $"delete DkimSignature --ids {Arg(oldId)}", env, cancellationToken);
                        if (!removed.Success) continue;
                    }
                }

                if (await TryRemoveDkimDnsRecordAsync(entry.Domain, entry.OldSelector, cancellationToken))
                {
                    done.Add(entry);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "DKIM eski anahtar kaldırılamadı, yeniden denenecek: {Domain}/{Selector}", entry.Domain, entry.OldSelector);
            }
        }

        if (done.Count > 0)
        {
            await fileStore.UpdateAsync(DkimRotationStorePath, new List<DkimRotationEntry>(), list =>
            {
                list.RemoveAll(item => done.Any(d => d.Domain == item.Domain && d.OldSelector == item.OldSelector));
                return list;
            }, cancellationToken);
        }

        return done.Count;
    }
}
