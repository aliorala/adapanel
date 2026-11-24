using System.Security.Cryptography;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.Dns;

/// <summary>
/// İki aşamalı BIND içe aktarma: <see cref="Preview"/> hiçbir şey yazmaz; <see cref="ApplyAsync"/> yalnız
/// önizlenen metni (özet eşleşmesiyle) ve mevcut tek-kayıt yazma yolu üzerinden uygular.
/// </summary>
/// <remarks>
/// <para>
/// <b>Yazma yolu:</b> her kayıt <see cref="IDnsService.UpsertRecordAsync(string, UpsertDnsRecordRequest,
/// Func{IReadOnlyList{string}, CancellationToken, Task}, CancellationToken)"/> ile yazılır — bölge kilidi,
/// çağıranın kapsam onayı (paylaşımlı bölgede başkasının sitesine dokunma) ve yayın doğrulamaları orada.
/// Toplu bir "bölgeyi değiştir" kapısı AÇILMADI.
/// </para>
/// <para>
/// <b>Panel-yönetilen çakışma:</b> panelde "yönetilen" bayrağı yalnız şablondan tohumlanan kayıtlarda
/// durur; posta yayını (<c>UpsertRecordsAsync</c>) kayıtları yönetilmeyen olarak yazar. Bu yüzden
/// çakışma iki kuraldan biriyle işaretlenir: (a) aynı tür+konakta yönetilen kayıt var, (b) posta
/// kaydı (MX, SPF, DKIM, DMARC, posta konakları) aynı tür+konakta FARKLI değerli bir kaydı ezer ya da
/// yanına eklenir. Onay verilmedikçe bu satırlar ATLANIR ve raporlanır.
/// </para>
/// <para>İçe aktarma ASLA silmez: dosyada olmayan mevcut kayıtlar yerinde kalır.</para>
/// </remarks>
public static class DnsZoneImport
{
    private static readonly HashSet<string> MailHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "mail", "webmail", "smtp", "imap", "pop", "pop3", "autoconfig", "autodiscover"
    };

    public static string ComputeHash(string zoneName, string? zoneText)
    {
        var normalized = (zoneText ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(zoneName.Trim().TrimEnd('.').ToLowerInvariant() + "\n" + normalized);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static DnsZoneImportPreviewDto Preview(SiteDnsZoneDto zone, string? zoneText)
    {
        var parsed = DnsZoneFile.Parse(zoneText, zone.ZoneName);
        var items = parsed.Errors.Count == 0 ? Plan(zone, parsed.Records) : [];
        var errors = parsed.Errors.ToList();
        if (zone.IsSecondary)
        {
            errors.Add(new DnsZoneImportIssueDto { Line = 0, Code = "dns.zone_is_secondary" });
        }

        return new DnsZoneImportPreviewDto
        {
            ZoneName = zone.ZoneName,
            PreviewHash = ComputeHash(zone.ZoneName, zoneText),
            Items = items,
            AddCount = items.Count(item => item.Action == "add"),
            ChangeCount = items.Count(item => item.Action == "change"),
            UnchangedCount = items.Count(item => item.Action == "unchanged"),
            ManagedConflictCount = items.Count(item => item.ConflictsWithManaged),
            Ignored = [.. parsed.Ignored],
            Errors = [.. errors],
            IsSecondary = zone.IsSecondary,
            CanApply = errors.Count == 0 && items.Length > 0
        };
    }

    public static DnsZoneImportItemDto[] Plan(SiteDnsZoneDto zone, IReadOnlyList<DnsZoneFile.ParsedRecord> imported)
    {
        var existing = zone.Records.ToList();
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new DnsZoneImportItemDto?[imported.Count];

        // 1) Birebir aynı kayıt (tür, konak, değer, öncelik) → değişmedi (yalnız TTL farkı = değişiklik).
        for (var i = 0; i < imported.Count; i++)
        {
            var record = imported[i];
            var match = existing.FirstOrDefault(e => !consumed.Contains(e.Id) && SameIdentity(e, record));
            if (match is null) continue;
            consumed.Add(match.Id);
            items[i] = Item(match.Ttl == record.Ttl ? "unchanged" : "change", record, match);
        }

        // 2) Kalanlar: aynı tür+konakta tek mevcut ↔ tek içeri alınan = değişiklik; aksi hâlde ekleme.
        for (var i = 0; i < imported.Count; i++)
        {
            if (items[i] is not null) continue;
            var record = imported[i];
            var sameSlot = existing.Where(e => !consumed.Contains(e.Id) && SameSlot(e, record)).ToList();
            var pendingInSlot = Enumerable.Range(0, imported.Count)
                .Count(j => items[j] is null && SameSlot(imported[j], record));
            if (sameSlot.Count == 1 && pendingInSlot == 1)
            {
                consumed.Add(sameSlot[0].Id);
                items[i] = Item("change", record, sameSlot[0]);
            }
            else
            {
                items[i] = Item("add", record, null);
            }
        }

        foreach (var item in items)
        {
            if (item is null || item.Action == "unchanged") continue;
            var record = new DnsZoneFile.ParsedRecord(0, item.Type, item.Host, item.Value, item.Ttl, item.Priority);
            item.ConflictsWithManaged = item.ConflictsWithManaged || zone.Records.Any(e =>
                SameSlot(e, record) && !SameIdentity(e, record) && (e.IsManaged || item.IsMailRelated));
        }

        return [.. items.Select(item => item!)];
    }

    private static DnsZoneImportItemDto Item(string action, DnsZoneFile.ParsedRecord record, SiteDnsRecordDto? existing) => new()
    {
        Action = action,
        Type = record.Type,
        Host = record.Host,
        Value = record.Value,
        Ttl = record.Ttl,
        Priority = record.Priority,
        ExistingId = action == "add" ? null : existing?.Id,
        ExistingValue = action == "change" ? existing?.Value : null,
        ExistingTtl = action == "change" ? existing?.Ttl : null,
        IsMailRelated = IsMailRelated(record.Type, record.Host, record.Value),
        // Birebir değişmeyen kayıt kimseyi ezmez; yönetilen bir kaydın TTL'ini değiştirmek ise ezmektir.
        ConflictsWithManaged = action == "change" && existing is { IsManaged: true }
    };

    private static bool SameIdentity(SiteDnsRecordDto e, DnsZoneFile.ParsedRecord r) =>
        string.Equals(e.Type, r.Type, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(e.Host, r.Host, StringComparison.OrdinalIgnoreCase) &&
        (r.Type == "TXT"
            ? string.Equals(e.Value, r.Value, StringComparison.Ordinal)
            : string.Equals(e.Value, r.Value, StringComparison.OrdinalIgnoreCase)) &&
        (r.Type is not ("MX" or "SRV") || e.Priority == r.Priority);

    private static bool SameSlot(SiteDnsRecordDto e, DnsZoneFile.ParsedRecord r) =>
        string.Equals(e.Type, r.Type, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(e.Host, r.Host, StringComparison.OrdinalIgnoreCase) &&
        (r.Type != "TXT" || (TxtKind(e.Value) == TxtKind(r.Value) && TxtKind(r.Value) != "other"));

    private static bool SameSlot(DnsZoneFile.ParsedRecord a, DnsZoneFile.ParsedRecord b) =>
        a.Type == b.Type && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) &&
        (a.Type != "TXT" || (TxtKind(a.Value) == TxtKind(b.Value) && TxtKind(a.Value) != "other"));

    /// <summary>Tek örnekli politika TXT'leri: SPF, DMARC, DKIM. Diğer TXT'ler birbirini ezmez.</summary>
    private static string TxtKind(string value)
    {
        var text = value.TrimStart();
        if (text.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)) return "spf";
        if (text.StartsWith("v=dmarc1", StringComparison.OrdinalIgnoreCase)) return "dmarc";
        if (text.StartsWith("v=dkim1", StringComparison.OrdinalIgnoreCase)) return "dkim";
        return "other";
    }

    public static bool IsMailRelated(string type, string host, string value)
    {
        var h = host.ToLowerInvariant();
        return type.ToUpperInvariant() switch
        {
            "MX" => true,
            "TXT" => h == "_dmarc" || h.EndsWith("._domainkey", StringComparison.Ordinal) || TxtKind(value) is "spf" or "dmarc" or "dkim",
            "A" or "AAAA" or "CNAME" => MailHosts.Contains(h),
            "SRV" => h.StartsWith("_imap", StringComparison.Ordinal) || h.StartsWith("_pop3", StringComparison.Ordinal) ||
                     h.StartsWith("_submission", StringComparison.Ordinal) || h.StartsWith("_autodiscover", StringComparison.Ordinal),
            _ => false
        };
    }

    /// <summary>
    /// Önizlenen metni uygular. İlk hatada durur (hata büyük olasılıkla sistemik: ikincil bölge, kilit,
    /// yayın hatası) ve ne yazıldığını, ne yazılamadığını, ne denenmediğini AÇIKÇA döner.
    /// </summary>
    public static async Task<DnsZoneImportResultDto> ApplyAsync(
        IDnsService dnsService,
        string siteName,
        DnsZoneImportApplyRequest request,
        Func<IReadOnlyList<string>, CancellationToken, Task> approval,
        CancellationToken cancellationToken)
    {
        var text = request.ZoneText ?? string.Empty;
        if (text.Length > DnsZoneFile.MaxTextChars)
        {
            throw new AppException("dns.import_too_large", 400, 400);
        }

        var zone = await dnsService.GetZoneAsync(siteName, cancellationToken);
        var expected = ComputeHash(zone.ZoneName, text);
        if (!string.Equals(expected, request.PreviewHash?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            // Önizlenenden farklı bir metin uygulanmaz (önizleme ile uygulama arasında metin değişmiş).
            throw new AppException("dns.import_hash_mismatch", 409, 409);
        }

        var preview = Preview(zone, text);
        if (!preview.CanApply)
        {
            var first = preview.Errors.FirstOrDefault();
            throw new AppException(
                first?.Code == "dns.zone_is_secondary" ? "dns.zone_is_secondary" : "dns.import_invalid",
                first?.Code == "dns.zone_is_secondary" ? 409 : 400,
                first?.Code == "dns.zone_is_secondary" ? 409 : 400,
                first is null ? string.Empty : $"line {first.Line}: {first.Code}");
        }

        var result = new DnsZoneImportResultDto { ZoneName = zone.ZoneName };
        var skipped = new List<DnsZoneImportItemDto>();
        var failed = new List<DnsZoneImportFailureDto>();
        var work = new List<DnsZoneImportItemDto>();
        foreach (var item in preview.Items)
        {
            if (item.Action == "unchanged") { result.Unchanged++; continue; }
            if (item.ConflictsWithManaged && !request.ConfirmOverwriteManaged) { skipped.Add(item); continue; }
            work.Add(item);
        }

        SiteDnsZoneDto? last = null;
        for (var index = 0; index < work.Count; index++)
        {
            var item = work[index];
            try
            {
                last = await dnsService.UpsertRecordAsync(siteName, new UpsertDnsRecordRequest
                {
                    Id = item.Action == "change" ? item.ExistingId : null,
                    Type = item.Type,
                    Host = item.Host,
                    Value = item.Value,
                    Ttl = item.Ttl,
                    Priority = item.Priority
                }, approval, cancellationToken);
                if (item.Action == "change") result.Changed++; else result.Added++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (result.Added + result.Changed == 0 && exception is AppException)
                {
                    // Hiçbir şey yazılmadan reddedildi (ör. kapsam dışı paylaşımlı bölge): olduğu gibi yüzeye çık.
                    throw;
                }

                failed.Add(new DnsZoneImportFailureDto
                {
                    Type = item.Type,
                    Host = item.Host,
                    Error = exception is AppException app ? app.MessageKey : "dns.sync_failed"
                });
                result.NotAttempted = work.Count - index - 1;
                break;
            }
        }

        result.SkippedManaged = [.. skipped];
        result.Failed = [.. failed];
        result.IsPartial = failed.Count > 0 || skipped.Count > 0;
        result.Zone = last;
        return result;
    }
}
