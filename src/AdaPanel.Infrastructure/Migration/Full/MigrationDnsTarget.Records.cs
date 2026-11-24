using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Infrastructure.Dns;

namespace AdaPanel.Infrastructure.Migration.Full;

public sealed partial class MigrationDnsTarget
{
    private static Prepared Prepare(MigrationPlanItemDto item, PortableMigrationManifest manifest, MigrationPlanRequest selection)
    {
        var source = manifest.DnsZones.Single(zone => zone.Id == item.SourceId);
        var zone = MigrationDnsRecords.Zone(item.Target.TargetName);
        if (zone != MigrationDnsRecords.Zone(source.Name)) throw Error("zone_rename_unsupported");
        var selected = manifest.Sites.Where(site => selection.AllSites || selection.SiteIds.Contains(MigrationResourceKey.Create(item.PackageId, site.Id), StringComparer.Ordinal)).ToArray();
        var related = selected.Where(site => source.SiteIds.Contains(site.Id, StringComparer.Ordinal)).ToArray();
        var key = item.Target.LinkedSiteId;
        if (key.Length == 0)
        {
            var primary = related.Where(site => site.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var candidates = primary.Length == 1 ? primary : related;
            if (candidates.Length != 1) throw Error("site_mapping_required");
            key = MigrationResourceKey.Create(item.PackageId, candidates[0].Id);
        }
        var linked = related.SingleOrDefault(site => MigrationResourceKey.Create(item.PackageId, site.Id) == key) ?? throw Error("site_not_selected");
        var mapping = selection.Mappings.SingleOrDefault(candidate => candidate.ResourceId == key);
        var name = string.IsNullOrWhiteSpace(mapping?.TargetName) ? linked.Name : mapping.TargetName;
        var path = string.IsNullOrWhiteSpace(mapping?.TargetPath) ? Path.Combine(selection.TargetBasePath, name) : mapping.TargetPath;
        if (string.IsNullOrWhiteSpace(name) || !Path.IsPathFullyQualified(path)) throw Error("site_mapping_invalid");
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in selection.AddressMappings)
        {
            var fromIp = IPAddress.TryParse(pair.Key, out var from); var toIp = IPAddress.TryParse(pair.Value, out var to);
            if (fromIp != toIp || fromIp && (from!.AddressFamily != to!.AddressFamily || from.ScopeIdSafe() != 0 || to.ScopeIdSafe() != 0)) throw Error("address_mapping_invalid");
            var old = fromIp ? from!.ToString() : MigrationDnsRecords.Domain(pair.Key);
            var target = toIp ? to!.ToString() : MigrationDnsRecords.Domain(pair.Value);
            if (!replacements.TryAdd(old, target)) throw Error("address_mapping_duplicate");
        }
        string Mapped(string value) => replacements.TryGetValue(value, out var replacement) ? replacement : value;
        var actions = new HashSet<string>(StringComparer.Ordinal) { "migration.dns_target_authorities_and_registrar_cutover_required" };
        var records = new List<MigrationDnsRecord>();
        foreach (var record in source.Records)
        {
            var type = record.Type.ToUpperInvariant();
            if (type == "SOA" || type == "NS" && MigrationDnsRecords.Owner(record.Name, zone) == "@") { actions.Add("migration.dns_source_authorities_not_copied"); continue; }
            if (type is "DNSKEY" or "DS" or "RRSIG" or "NSEC" or "NSEC3" or "NSEC3PARAM")
            { actions.Add("migration.dns_dnssec_keys_and_ds_require_review"); continue; }
            var value = MigrationDnsRecords.Normalize(new() { Type = type, Name = record.Name, Value = record.Value, Ttl = record.Ttl, Priority = record.Priority }, zone);
            var converted = value.Value;
            if (type is "A" or "AAAA" or "CNAME" or "MX" or "PTR" or "NS") converted = Mapped(converted);
            else if (type == "SRV") { var pieces = converted.Split(' '); converted = pieces[0] + " " + pieces[1] + " " + Mapped(pieces[2]); }
            else if (type == "TXT") converted = MapSpf(converted, replacements, actions);
            if (value.Name.Split('.').Any(label => label.Equals("_domainkey", StringComparison.OrdinalIgnoreCase)))
                actions.Add("migration.dns_dkim_public_records_copied_signing_unverified");
            records.Add(MigrationDnsRecords.Normalize(new() { Type = type, Name = value.Name, Value = converted, Ttl = value.Ttl, Priority = value.Priority }, zone));
        }
        var validated = MigrationDnsRecords.Validate(records, zone);
        DnsService.EnsureMigrationManagementCompatible(validated, zone);
        return new(name, Path.GetFullPath(path), zone, validated, actions.Order(StringComparer.Ordinal).ToArray());
    }
    private static string MapSpf(string value, IReadOnlyDictionary<string, string> mappings, HashSet<string> actions)
    {
        var tokens = Regex.Matches(value, @"\s+|\S+", RegexOptions.CultureInvariant).Select(match => match.Value).ToArray();
        var first = Array.FindIndex(tokens, token => !string.IsNullOrWhiteSpace(token));
        if (first < 0 || !tokens[first].Equals("v=spf1", StringComparison.OrdinalIgnoreCase) || mappings.Count == 0) return value;
        var changed = tokens.ToArray();
        for (var index = first + 1; index < tokens.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(tokens[index])) continue;
            var original = tokens[index]; var qualifier = "+-~?".Contains(original[0]) ? original[..1] : "";
            var term = original[qualifier.Length..];
            if (term.Length == 0 || term.Contains('%')) return Unmapped();
            var separator = term.IndexOfAny([':', '=']);
            var kind = (separator < 0 ? term : term[..separator]).ToLowerInvariant();
            var body = separator < 0 ? "" : term[(separator + 1)..];
            if (kind == "all" && separator < 0) continue;
            if (kind is "ip4" or "ip6")
            {
                if (separator < 0 || term[separator] != ':') return Unmapped();
                var parts = body.Split('/');
                if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address) || address.AddressFamily != (kind == "ip4" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6)) return Unmapped();
                var maximum = kind == "ip4" ? 32 : 128;
                if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) || prefix < 0 || prefix > maximum)) return Unmapped();
                if (mappings.TryGetValue(address.ToString(), out var mapped))
                {
                    changed[index] = qualifier + term[..(separator + 1)] + mapped + (parts.Length == 2 ? "/" + parts[1] : "");
                    if (parts.Length == 2 && parts[1] != maximum.ToString(CultureInfo.InvariantCulture)) actions.Add("migration.dns_spf_cidr_mapping_requires_review");
                }
                continue;
            }
            if (kind is "a" or "mx" or "ptr" or "include" or "exists" or "redirect" or "exp")
            {
                if (separator < 0) { if (kind is "a" or "mx" or "ptr") continue; return Unmapped(); }
                if ((kind is "redirect" or "exp") != (term[separator] == '=') || (kind is "redirect" or "exp") && qualifier.Length > 0) return Unmapped();
                var slash = body.IndexOf('/'); var domain = slash < 0 ? body : body[..slash]; var suffix = slash < 0 ? "" : body[slash..];
                if (suffix.Length > 0 && (kind is not ("a" or "mx") || !Regex.IsMatch(suffix, @"\A(?:/(?:[0-9]|[12][0-9]|3[0-2]))?(?://(?:[0-9]|[1-9][0-9]|1[01][0-9]|12[0-8]))?\z", RegexOptions.CultureInvariant))) return Unmapped();
                string normalized; try { normalized = MigrationDnsRecords.Domain(domain); } catch (AppException) { return Unmapped(); }
                if (mappings.TryGetValue(normalized, out var mapped)) changed[index] = qualifier + term[..(separator + 1)] + mapped + suffix;
                continue;
            }
            return Unmapped();
        }
        return string.Concat(changed);
        string Unmapped() { actions.Add("migration.dns_spf_mapping_requires_review"); return value; }
    }
}

internal static class MigrationDnsRecords
{
    internal static long ScopeIdSafe(this IPAddress ip) => ip.AddressFamily == AddressFamily.InterNetworkV6 ? ip.ScopeId : 0;
    internal static string Zone(string value) { var domain = Domain(value); if (!domain.Contains('.')) throw Invalid(); return domain; }
    internal static string Domain(string value)
    {
        value = value.Trim().TrimEnd('.').ToLowerInvariant();
        if (value.Length is 0 or > 253 || value.Split('.').Any(label => !Regex.IsMatch(label, @"\A[a-z0-9_](?:[a-z0-9_-]{0,61}[a-z0-9_])?\z", RegexOptions.CultureInvariant))) throw Invalid();
        return value;
    }
    internal static string Owner(string name, string zone)
    {
        if (name is "" or "@" || name.TrimEnd('.').Equals(zone, StringComparison.OrdinalIgnoreCase)) return "@";
        var absolute = name.EndsWith('.'); name = name.TrimEnd('.').ToLowerInvariant();
        if (name.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase)) name = name[..^(zone.Length + 1)];
        else if (absolute) throw Invalid();
        var labels = name.Split('.');
        if (labels.Length == 0 || labels.Where((_, index) => !(index == 0 && labels[0] == "*")).Any(label => !Regex.IsMatch(label, @"\A[a-z0-9_](?:[a-z0-9_-]{0,61}[a-z0-9_])?\z", RegexOptions.CultureInvariant)) || name.Length + zone.Length > 252) throw Invalid();
        return name;
    }
    internal static MigrationDnsRecord Normalize(MigrationDnsRecord source, string zone)
    {
        var type = source.Type.ToUpperInvariant(); var name = Owner(source.Name, zone); var value = source.Value;
        if (source.Ttl < 0 || source.Priority is < 0 or > 65535 || type is not ("A" or "AAAA" or "CNAME" or "MX" or "TXT" or "SRV" or "PTR" or "CAA" or "TLSA" or "NS") || type is not ("MX" or "SRV") && source.Priority != 0 || type == "NS" && name == "@") throw Invalid();
        switch (type)
        {
            case "A": case "AAAA":
                if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != (type == "A" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6) || ip.ScopeIdSafe() != 0) throw Invalid(); value = ip.ToString(); break;
            case "CNAME": if (name == "@") throw Invalid(); value = Domain(value); break;
            case "MX": case "PTR": case "NS": value = Domain(value); break;
            case "TXT":
                var bytes = new UTF8Encoding(false, true).GetByteCount(value);
                if (bytes + Math.Max(1, (bytes + 254) / 255) > 65535) throw Invalid(); break;
            case "SRV":
                var pieces = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (pieces.Length != 3 || !ushort.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out var weight) || !ushort.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port)) throw Invalid();
                value = weight + " " + port + " " + (pieces[2] == "." ? "." : Domain(pieces[2])); break;
            case "CAA": var caa = DnsService.ParseCaaValue(value); value = $"{caa.Flags} {caa.Tag} {caa.Value}"; break;
            case "TLSA": var tlsa = DnsService.ParseTlsaValue(value); value = $"{tlsa.Usage} {tlsa.Selector} {tlsa.MatchingType} {tlsa.Data}"; break;
        }
        return new() { Type = type, Name = name, Value = value, Priority = source.Priority, Ttl = source.Ttl };
    }
    internal static MigrationDnsRecord[] Validate(IEnumerable<MigrationDnsRecord> source, string zone)
    {
        var records = source.Select(record => Normalize(record, zone)).ToArray();
        if (records.Length > 10000 || records.GroupBy(Key, StringComparer.Ordinal).Any(group => group.Count() > 1)) throw Invalid();
        foreach (var owner in records.GroupBy(record => record.Name, StringComparer.OrdinalIgnoreCase))
            if (owner.Any(record => record.Type == "CNAME") && owner.Count() != 1) throw Invalid();
        if (records.GroupBy(record => (record.Name, record.Type)).Any(group => group.Select(record => record.Ttl).Distinct().Count() > 1)) throw Invalid();
        return records;
    }
    internal static string Key(MigrationDnsRecord record) => System.Text.Json.JsonSerializer.Serialize(new object[] { record.Type, record.Name, record.Value, record.Ttl, record.Priority });
    internal static string TextRepresentation(MigrationDnsRecord record, string zone)
    {
        var value = record.Value;
        if (record.Type == "TXT") value = QuotedUtf8(value);
        else if (record.Type is "CNAME" or "MX" or "PTR" or "NS") value += ".";
        else if (record.Type == "SRV" && !value.EndsWith(" .", StringComparison.Ordinal)) value += ".";
        else if (record.Type == "CAA") { var caa = DnsService.ParseCaaValue(value); value = $"{caa.Flags} {caa.Tag} {QuotedUtf8(caa.Value)}"; }
        if (record.Type is "MX" or "SRV") value = record.Priority.ToString(CultureInfo.InvariantCulture) + " " + value;
        return (record.Name == "@" ? zone : record.Name + "." + zone) + ". " + record.Ttl.ToString(CultureInfo.InvariantCulture) + " IN " + record.Type + " " + value;
    }
    private static string QuotedUtf8(string text)
    {
        var bytes = new UTF8Encoding(false, true).GetBytes(text); var result = new StringBuilder();
        for (var index = 0; index < Math.Max(1, bytes.Length); index += 255)
        {
            if (index > 0) result.Append(' '); result.Append('"');
            foreach (var octet in bytes.Skip(index).Take(255)) result.Append('\\').Append(octet.ToString("D3", CultureInfo.InvariantCulture));
            result.Append('"');
        }
        return result.ToString();
    }
    private static AppException Invalid() => new("migration.dns_record_invalid", 409, 409);
}
