using System.Globalization;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Infrastructure.Dns;

/// <summary>
/// BIND bölge dosyası (RFC 1035 ana dosya biçimi) dışa/içe aktarma — SAF fonksiyonlar, hiçbir şey yazmaz.
/// </summary>
/// <remarks>
/// <para>
/// İçe aktarma BİLEREK dar: kayıt türü beyaz listesi (panelin kendi desteklediği türler), $INCLUDE ve
/// $GENERATE yok, bölge dışı adlar reddedilir, metin ve kayıt sayısı sınırlı. Her kayıt panelin TEK
/// doğrulamasından (<see cref="DnsService.NormalizeForImport"/>) geçer; elle kayıt yolundan daha gevşek
/// bir kapı açılmaz.
/// </para>
/// <para>
/// Hata toplanır, ilk hatada durulmaz: kullanıcı tek turda tüm satır hatalarını görsün. Ama hata varsa
/// UYGULANAMAZ (kısmi dosya sessizce yarım içe aktarılmaz).
/// </para>
/// </remarks>
public static class DnsZoneFile
{
    public const int MaxTextChars = 262_144;
    public const int MaxRecords = 500;

    private static readonly string[] AllowedTypes = ["A", "AAAA", "CNAME", "MX", "TXT", "NS", "SRV", "CAA", "TLSA", "PTR"];

    public sealed record ParsedRecord(int Line, string Type, string Host, string Value, int Ttl, int? Priority);

    public sealed class ParseResult
    {
        public List<ParsedRecord> Records { get; } = [];

        public List<DnsZoneImportIssueDto> Errors { get; } = [];

        public List<DnsZoneImportIssueDto> Ignored { get; } = [];
    }

    // ------------------------------------------------------------------ DIŞA AKTARIM

    public static DnsZoneExportDto Export(SiteDnsZoneDto zone, DateTime? nowUtc = null)
    {
        var origin = zone.ZoneName.Trim().TrimEnd('.').ToLowerInvariant();
        var now = nowUtc ?? DateTime.UtcNow;
        var ns = zone.NameServers
            .Concat(zone.Records.Where(r => IsApexNs(r)).Select(r => r.Value))
            .Select(value => value.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var text = new StringBuilder();
        text.Append("; AdaPanel BIND zone export: ").Append(origin).Append('\n');
        text.Append("$ORIGIN ").Append(origin).Append(".\n");
        text.Append("$TTL 3600\n");
        var primary = ns.Length > 0 ? ns[0] : $"ns1.{origin}";
        text.Append("@\tIN\tSOA\t").Append(primary).Append(". hostmaster.").Append(origin).Append(". ( ")
            .Append(now.ToString("yyyyMMddHH", CultureInfo.InvariantCulture))
            .Append(" 3600 600 1209600 3600 )\n");
        foreach (var server in ns)
        {
            text.Append("@\t3600\tIN\tNS\t").Append(server).Append(".\n");
        }

        foreach (var record in zone.Records.Where(r => !IsApexNs(r)))
        {
            var type = record.Type.ToUpperInvariant();
            var host = string.IsNullOrWhiteSpace(record.Host) ? "@" : record.Host.Trim();
            text.Append(host).Append('\t').Append(Math.Max(record.Ttl, 0).ToString(CultureInfo.InvariantCulture))
                .Append("\tIN\t").Append(type).Append('\t').Append(FormatRdata(record)).Append('\n');
        }

        return new DnsZoneExportDto { ZoneName = origin, FileName = $"{origin}.zone", ZoneText = text.ToString() };
    }

    private static bool IsApexNs(SiteDnsRecordDto record) =>
        string.Equals(record.Type, "NS", StringComparison.OrdinalIgnoreCase) && record.Host.Trim() == "@";

    private static string Absolute(string name) => name.Trim().TrimEnd('.') + ".";

    private static string FormatRdata(SiteDnsRecordDto record)
    {
        var value = record.Value ?? string.Empty;
        switch (record.Type.ToUpperInvariant())
        {
            case "CNAME":
            case "NS":
            case "PTR":
                return Absolute(value);
            case "MX":
                return $"{record.Priority ?? 10} {Absolute(value)}";
            case "SRV":
            {
                var parts = value.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length == 3
                    ? $"{record.Priority ?? 0} {parts[0]} {parts[1]} {Absolute(parts[2])}"
                    : QuoteTxt(value);
            }
            case "CAA":
            {
                var parts = value.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length == 3
                    ? $"{parts[0]} {parts[1]} {QuoteChunk(Encoding.UTF8.GetBytes(parts[2].Trim().Trim('"')))}"
                    : QuoteTxt(value);
            }
            case "TXT":
                return QuoteTxt(value);
            default:
                return value;
        }
    }

    /// <summary>TXT: 255 BAYTLIK parçalara bölünür (RFC 1035 karakter-dizesi sınırı), her parça tırnaklı.</summary>
    internal static string QuoteTxt(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (bytes.Length == 0)
        {
            return "\"\"";
        }

        var chunks = new List<string>();
        for (var offset = 0; offset < bytes.Length; offset += 255)
        {
            chunks.Add(QuoteChunk(bytes.AsSpan(offset, Math.Min(255, bytes.Length - offset)).ToArray()));
        }

        return string.Join(' ', chunks);
    }

    private static string QuoteChunk(byte[] bytes)
    {
        var builder = new StringBuilder("\"");
        foreach (var b in bytes)
        {
            if (b == (byte)'"' || b == (byte)'\\')
            {
                builder.Append('\\').Append((char)b);
            }
            else if (b is < 0x20 or >= 0x7F)
            {
                // Yazdırılamayan/ASCII dışı baytlar \DDD (ondalık) — dosya güvenle taşınır, tekrar okununca aynı baytlar.
                builder.Append('\\').Append(((int)b).ToString("000", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append((char)b);
            }
        }

        return builder.Append('"').ToString();
    }

    // ------------------------------------------------------------------ İÇE AKTARIM: AYRIŞTIRMA

    private sealed record Token(byte[] Raw, bool Quoted)
    {
        public string Text => Encoding.UTF8.GetString(Raw);
    }

    private sealed class Entry
    {
        public int Line;
        public bool LeadingSpace;
        public List<Token> Tokens { get; } = [];
    }

    public static ParseResult Parse(string? text, string zoneName)
    {
        var result = new ParseResult();
        var zone = zoneName.Trim().TrimEnd('.').ToLowerInvariant();
        text ??= string.Empty;

        if (text.Length > MaxTextChars)
        {
            result.Errors.Add(Issue(1, "dns.import_too_large", MaxTextChars.ToString(CultureInfo.InvariantCulture)));
            return result;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            result.Errors.Add(Issue(1, "dns.import_empty", string.Empty));
            return result;
        }

        var entries = Tokenize(text, result);
        var origin = zone;
        var defaultTtl = 3600;
        string? lastOwner = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var first = entry.Tokens[0];
            if (!entry.LeadingSpace && !first.Quoted && first.Text.StartsWith('$'))
            {
                HandleDirective(entry, zone, ref origin, ref defaultTtl, result);
                continue;
            }

            string owner;
            var index = 0;
            if (entry.LeadingSpace)
            {
                if (lastOwner is null)
                {
                    result.Errors.Add(Issue(entry.Line, "dns.import_no_owner", string.Empty));
                    continue;
                }

                owner = lastOwner;
            }
            else
            {
                owner = Qualify(first.Text, origin);
                index = 1;
            }

            lastOwner = owner;
            int? ttl = null;
            while (index < entry.Tokens.Count)
            {
                var token = entry.Tokens[index];
                if (token.Quoted) break;
                var word = token.Text;
                if (TryParseTtl(word, out var parsedTtl)) { ttl = parsedTtl; index++; continue; }
                if (string.Equals(word, "IN", StringComparison.OrdinalIgnoreCase)) { index++; continue; }
                if (word.ToUpperInvariant() is "CH" or "HS" or "CS" or "CLASS3" or "ANY")
                {
                    result.Errors.Add(Issue(entry.Line, "dns.import_class_not_allowed", word));
                    index = int.MaxValue;
                }

                break;
            }

            if (index == int.MaxValue) continue;
            if (index >= entry.Tokens.Count)
            {
                result.Errors.Add(Issue(entry.Line, "dns.import_malformed", string.Empty));
                continue;
            }

            var type = entry.Tokens[index].Text.ToUpperInvariant();
            var rdata = entry.Tokens.Skip(index + 1).ToList();

            var host = HostFromFqdn(owner, zone);
            if (host is null)
            {
                // Bölge dışı ad: başka bir alan adının kaydını bu bölgeye yazmak sessizce kabul edilmez.
                result.Errors.Add(Issue(entry.Line, "dns.import_out_of_zone", owner));
                continue;
            }

            if (type == "SOA")
            {
                result.Ignored.Add(Issue(entry.Line, "dns.import_ignored_soa", string.Empty));
                continue;
            }

            if (!AllowedTypes.Contains(type, StringComparer.Ordinal))
            {
                result.Errors.Add(Issue(entry.Line, "dns.import_type_not_allowed", type));
                continue;
            }

            if (type == "NS" && host == "@")
            {
                // Bölge köküne ait NS kayıtları bölge ayarlarının (ad sunucuları) işidir, kayıt listesinin değil.
                result.Ignored.Add(Issue(entry.Line, "dns.import_ignored_apex_ns", string.Empty));
                continue;
            }

            var request = BuildRequest(entry, type, host, rdata, ttl ?? defaultTtl, origin, result);
            if (request is null) continue;

            UpsertDnsRecordRequest normalized;
            try
            {
                normalized = DnsService.NormalizeForImport(request, zone);
            }
            catch (AppException exception)
            {
                result.Errors.Add(Issue(entry.Line, exception.MessageKey, $"{type} {host}"));
                continue;
            }

            var key = $"{normalized.Type}|{normalized.Host.ToLowerInvariant()}|{normalized.Value}|{normalized.Priority}";
            if (!seen.Add(key))
            {
                result.Ignored.Add(Issue(entry.Line, "dns.import_duplicate", $"{normalized.Type} {normalized.Host}"));
                continue;
            }

            if (result.Records.Count >= MaxRecords)
            {
                result.Errors.Add(Issue(entry.Line, "dns.import_too_many", MaxRecords.ToString(CultureInfo.InvariantCulture)));
                break;
            }

            result.Records.Add(new ParsedRecord(entry.Line, normalized.Type, normalized.Host, normalized.Value, normalized.Ttl, normalized.Priority));
        }

        if (result.Errors.Count == 0 && result.Records.Count == 0)
        {
            result.Errors.Add(Issue(1, "dns.import_no_records", string.Empty));
        }

        return result;
    }

    private static void HandleDirective(Entry entry, string zone, ref string origin, ref int defaultTtl, ParseResult result)
    {
        var name = entry.Tokens[0].Text.ToUpperInvariant();
        switch (name)
        {
            case "$ORIGIN":
            {
                if (entry.Tokens.Count != 2)
                {
                    result.Errors.Add(Issue(entry.Line, "dns.import_malformed", name));
                    return;
                }

                var next = Qualify(entry.Tokens[1].Text, origin);
                if (HostFromFqdn(next, zone) is null)
                {
                    result.Errors.Add(Issue(entry.Line, "dns.import_out_of_zone", next));
                    return;
                }

                origin = next;
                return;
            }
            case "$TTL":
            {
                if (entry.Tokens.Count != 2 || !TryParseTtl(entry.Tokens[1].Text, out var ttl))
                {
                    result.Errors.Add(Issue(entry.Line, "dns.import_malformed", name));
                    return;
                }

                defaultTtl = ttl;
                return;
            }
            default:
                // $INCLUDE (dosya okuma) ve $GENERATE (toplu üretim) dahil her bilinmeyen yönerge reddedilir.
                result.Errors.Add(Issue(entry.Line, "dns.import_directive_forbidden", name));
                return;
        }
    }

    private static UpsertDnsRecordRequest? BuildRequest(
        Entry entry, string type, string host, List<Token> rdata, int ttl, string origin, ParseResult result)
    {
        void Fail() => result.Errors.Add(Issue(entry.Line, "dns.import_malformed", $"{type} {host}"));

        string value;
        int? priority = null;
        switch (type)
        {
            case "A":
            case "AAAA":
                if (rdata.Count != 1) { Fail(); return null; }
                value = rdata[0].Text;
                break;
            case "CNAME":
            case "NS":
            case "PTR":
                if (rdata.Count != 1) { Fail(); return null; }
                value = Qualify(rdata[0].Text, origin);
                break;
            case "MX":
                if (rdata.Count != 2 || !TryParseUShort(rdata[0].Text, out var mxPriority)) { Fail(); return null; }
                priority = mxPriority;
                value = Qualify(rdata[1].Text, origin);
                break;
            case "SRV":
                if (rdata.Count != 4 || !TryParseUShort(rdata[0].Text, out var srvPriority)) { Fail(); return null; }
                priority = srvPriority;
                value = $"{rdata[1].Text} {rdata[2].Text} {Qualify(rdata[3].Text, origin)}";
                break;
            case "CAA":
                if (rdata.Count != 3) { Fail(); return null; }
                value = $"{rdata[0].Text} {rdata[1].Text} {rdata[2].Text}";
                break;
            case "TLSA":
                if (rdata.Count < 4) { Fail(); return null; }
                value = $"{rdata[0].Text} {rdata[1].Text} {rdata[2].Text} {string.Concat(rdata.Skip(3).Select(t => t.Text))}";
                break;
            case "TXT":
                if (rdata.Count == 0) { Fail(); return null; }
                // BIND: bir TXT'nin birden çok karakter-dizesi ARAYA BOŞLUK KONMADAN birleştirilir.
                value = Encoding.UTF8.GetString(rdata.SelectMany(t => t.Raw).ToArray());
                break;
            default:
                Fail();
                return null;
        }

        return new UpsertDnsRecordRequest { Type = type, Host = host, Value = value, Ttl = ttl, Priority = priority };
    }

    private static string Qualify(string token, string origin)
    {
        var text = token.Trim();
        if (text == "@") return origin;
        if (text.EndsWith('.')) return text.TrimEnd('.').ToLowerInvariant();
        return (text + "." + origin).ToLowerInvariant();
    }

    private static string? HostFromFqdn(string fqdn, string zone)
    {
        if (string.Equals(fqdn, zone, StringComparison.OrdinalIgnoreCase)) return "@";
        var suffix = "." + zone;
        return fqdn.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && fqdn.Length > suffix.Length
            ? fqdn[..^suffix.Length]
            : null;
    }

    private static bool TryParseUShort(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value is >= 0 and <= 65535;

    /// <summary>BIND süre biçimi: 3600 ya da 1h30m (s, m, h, d, w).</summary>
    internal static bool TryParseTtl(string text, out int seconds)
    {
        seconds = 0;
        if (text.Length == 0 || !char.IsAsciiDigit(text[0])) return false;
        long total = 0;
        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
            if (i == start || i - start > 10) return false;
            var number = long.Parse(text.AsSpan(start, i - start), CultureInfo.InvariantCulture);
            long multiplier = 1;
            if (i < text.Length)
            {
                multiplier = char.ToLowerInvariant(text[i]) switch
                {
                    's' => 1, 'm' => 60, 'h' => 3600, 'd' => 86400, 'w' => 604800, _ => 0
                };
                if (multiplier == 0) return false;
                i++;
            }

            total += number * multiplier;
            if (total > int.MaxValue) return false;
        }

        seconds = (int)total;
        return true;
    }

    private static DnsZoneImportIssueDto Issue(int line, string code, string detail) =>
        new() { Line = line, Code = code, Detail = detail };

    private static List<Entry> Tokenize(string text, ParseResult result)
    {
        var entries = new List<Entry>();
        var line = 1;
        var depth = 0;
        var i = 0;
        Entry? current = null;
        var buffer = new List<byte>();
        var inToken = false;

        void Flush()
        {
            if (!inToken) return;
            current!.Tokens.Add(new Token(buffer.ToArray(), false));
            buffer.Clear();
            inToken = false;
        }

        void EndEntry()
        {
            Flush();
            if (current is { Tokens.Count: > 0 }) entries.Add(current);
            current = null;
        }

        void AppendChar(char c)
        {
            Span<char> one = [c];
            // Vekil çift (surrogate) tek tek eklenemez; çağıran çiftleri birlikte verir.
            buffer.AddRange(Encoding.UTF8.GetBytes(one.ToArray()));
        }

        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\r') { i++; continue; }
            if (c == '\n')
            {
                line++;
                i++;
                Flush();
                if (depth == 0) { EndEntry(); }
                continue;
            }

            if (current is null)
            {
                current = new Entry { Line = line, LeadingSpace = c is ' ' or '\t' };
            }

            if (c == ';')
            {
                Flush();
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }

            if (c is ' ' or '\t') { Flush(); i++; continue; }
            if (c == '(') { Flush(); depth++; i++; continue; }
            if (c == ')')
            {
                Flush();
                if (depth == 0) result.Errors.Add(Issue(line, "dns.import_malformed", ")"));
                else depth--;
                i++;
                continue;
            }

            if (c == '"')
            {
                Flush();
                i++;
                var quoted = new List<byte>();
                var closed = false;
                while (i < text.Length)
                {
                    var q = text[i];
                    if (q == '\n' || q == '\r') break;
                    if (q == '"') { closed = true; i++; break; }
                    if (q == '\\' && i + 1 < text.Length)
                    {
                        var next = text[i + 1];
                        if (char.IsAsciiDigit(next) && i + 3 < text.Length &&
                            char.IsAsciiDigit(text[i + 2]) && char.IsAsciiDigit(text[i + 3]) &&
                            int.Parse(text.AsSpan(i + 1, 3), CultureInfo.InvariantCulture) <= 255)
                        {
                            quoted.Add((byte)int.Parse(text.AsSpan(i + 1, 3), CultureInfo.InvariantCulture));
                            i += 4;
                            continue;
                        }

                        quoted.AddRange(Encoding.UTF8.GetBytes(next.ToString()));
                        i += 2;
                        continue;
                    }

                    if (char.IsControl(q) && q != '\t')
                    {
                        result.Errors.Add(Issue(line, "dns.invalid_record_value", string.Empty));
                    }

                    if (char.IsHighSurrogate(q) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        quoted.AddRange(Encoding.UTF8.GetBytes(text.Substring(i, 2)));
                        i += 2;
                        continue;
                    }

                    quoted.AddRange(Encoding.UTF8.GetBytes(q.ToString()));
                    i++;
                }

                if (!closed) result.Errors.Add(Issue(line, "dns.import_unterminated_quote", string.Empty));
                current.Tokens.Add(new Token(quoted.ToArray(), true));
                continue;
            }

            // Tırnaksız belirteç.
            inToken = true;
            if (c == '\\' && i + 1 < text.Length && text[i + 1] != '\n' && text[i + 1] != '\r')
            {
                var next = text[i + 1];
                if (char.IsAsciiDigit(next) && i + 3 < text.Length && char.IsAsciiDigit(text[i + 2]) && char.IsAsciiDigit(text[i + 3]) &&
                    int.Parse(text.AsSpan(i + 1, 3), CultureInfo.InvariantCulture) <= 255)
                {
                    buffer.Add((byte)int.Parse(text.AsSpan(i + 1, 3), CultureInfo.InvariantCulture));
                    i += 4;
                    continue;
                }

                AppendChar(next);
                i += 2;
                continue;
            }

            if (char.IsControl(c))
            {
                result.Errors.Add(Issue(line, "dns.invalid_record_value", string.Empty));
                i++;
                continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                buffer.AddRange(Encoding.UTF8.GetBytes(text.Substring(i, 2)));
                i += 2;
                continue;
            }

            AppendChar(c);
            i++;
        }

        if (depth != 0) result.Errors.Add(Issue(line, "dns.import_malformed", "("));
        EndEntry();
        return entries;
    }
}
