using System.Globalization;
using System.Net;
using System.Text;
using AdaPanel.Core.Dtos.Migration;

namespace AdaPanel.Infrastructure.Migration.Sources;

/// <summary>Strict, bounded BIND text parsing; unsupported RRs remain visible with an issue.</summary>
internal static class CpanelBindZoneReader
{
    private sealed record Token(string Text, bool Quoted);
    private sealed record Line(List<Token> Tokens, bool OwnerOmitted);
    internal static PortableMigrationDnsZone Read(string zone, string text, List<string> siteIds,
        Action<string, string, string> issue)
    {
        var records = new List<PortableMigrationDnsRecord>();
        var id = "dns:" + zone;
        var origin = zone.TrimEnd('.') + ".";
        string? previous = null;
        int? defaultTtl = null;
        try
        {
            foreach (var line in Lex(text))
            {
                var tokens = line.Tokens;
                if (tokens.Count == 0) continue;
                try
                {
                    if (tokens[0].Text.StartsWith('$'))
                    {
                        if (tokens[0].Text.Equals("$ORIGIN", StringComparison.OrdinalIgnoreCase) && tokens.Count == 2)
                            origin = Domain(tokens[1].Text, origin);
                        else if (tokens[0].Text.Equals("$TTL", StringComparison.OrdinalIgnoreCase) && tokens.Count == 2)
                            defaultTtl = Ttl(tokens[1].Text);
                        else issue("cpanel.dns.directive_unsupported", id, "The zone contains an unsupported directive; no external include was read.");
                        continue;
                    }
                    var cursor = 0;
                    var owner = line.OwnerOmitted ? previous ?? throw new FormatException() : Domain(tokens[cursor++].Text, origin);
                    previous = owner;
                    int? ttl = null;
                    while (cursor < tokens.Count)
                    {
                        var token = tokens[cursor].Text;
                        if (token.Equals("IN", StringComparison.OrdinalIgnoreCase)) { cursor++; continue; }
                        if (token.Length > 0 && char.IsAsciiDigit(token[0])) { ttl = Ttl(token); cursor++; continue; }
                        break;
                    }
                    if (cursor >= tokens.Count) throw new FormatException();
                    var type = tokens[cursor++].Text.ToUpperInvariant();
                    var data = tokens.Skip(cursor).ToArray();
                    string value;
                    var priority = 0;
                    switch (type)
                    {
                        case "A":
                        case "AAAA":
                            if (data.Length != 1 || !IPAddress.TryParse(data[0].Text, out var ip)
                                || (type == "A") != (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)) throw new FormatException();
                            value = ip.ToString(); break;
                        case "CNAME": case "NS": case "PTR":
                            if (data.Length != 1) throw new FormatException();
                            value = Domain(data[0].Text, origin); break;
                        case "MX":
                            if (data.Length != 2 || !ushort.TryParse(data[0].Text, out var mx)) throw new FormatException();
                            priority = mx; value = Domain(data[1].Text, origin); break;
                        case "TXT":
                            if (data.Length == 0) throw new FormatException();
                            value = string.Concat(data.Select(t => t.Text)); break;
                        case "SRV":
                            if (data.Length != 4 || !ushort.TryParse(data[0].Text, out var srv) || !ushort.TryParse(data[1].Text, out var weight) || !ushort.TryParse(data[2].Text, out var port)) throw new FormatException();
                            priority = srv; value = $"{weight} {port} {Domain(data[3].Text, origin)}"; break;
                        case "CAA":
                            if (data.Length != 3 || !byte.TryParse(data[0].Text, out var flags)) throw new FormatException();
                            value = $"{flags} {data[1].Text} {Quote(data[2].Text)}"; break;
                        case "SOA":
                            if (data.Length != 7 || !uint.TryParse(data[2].Text, out _)) throw new FormatException();
                            value = $"{Domain(data[0].Text, origin)} {Domain(data[1].Text, origin)} {data[2].Text} {Ttl(data[3].Text)} {Ttl(data[4].Text)} {Ttl(data[5].Text)} {Ttl(data[6].Text)}"; break;
                        case "TLSA":
                            if (data.Length != 4 || !byte.TryParse(data[0].Text, out _) || !byte.TryParse(data[1].Text, out _) || !byte.TryParse(data[2].Text, out _) || data[3].Text.Length % 2 != 0 || !data[3].Text.All(char.IsAsciiHexDigit)) throw new FormatException();
                            value = string.Join(' ', data.Select(t => t.Text)); break;
                        default:
                            value = string.Join(' ', data.Select(t => t.Quoted ? Quote(t.Text) : t.Text));
                            issue("cpanel.dns.record_unsupported", id, "An unsupported DNS record type requires manual mapping.");
                            break;
                    }
                    if (ttl is null && defaultTtl is null) issue("cpanel.dns.ttl_unknown", id, "No explicit or default TTL was available; manual mapping is required.");
                    var suffix = zone.TrimEnd('.') + ".";
                    var name = owner.Equals(suffix, StringComparison.OrdinalIgnoreCase) ? "@" : owner.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase) ? owner[..^(suffix.Length + 1)] : owner;
                    records.Add(new() { Type = type, Name = name, Value = value, Ttl = ttl ?? defaultTtl ?? 0, Priority = priority });
                }
                catch (Exception error) when (error is FormatException or OverflowException)
                { issue("cpanel.dns.record_malformed", id, "A DNS record could not be normalized; the original zone is preserved as a non-executed private source payload."); }
            }
        }
        catch (FormatException) { issue("cpanel.dns.syntax_invalid", id, "The BIND zone contains malformed quoted, escaped or multiline syntax."); }
        if (records.Count == 0) issue("cpanel.dns.records_missing", id, "The zone has no readable DNS records.");
        return new() { Id = id, Name = zone, SiteIds = siteIds, Records = records };
    }
    private static string Domain(string value, string origin)
    {
        if (value == "@") return origin;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.Contains('\\')) throw new FormatException();
        return value.EndsWith('.') ? value : value + "." + origin;
    }
    private static int Ttl(string value)
    {
        long result = 0;
        var cursor = 0;
        while (cursor < value.Length)
        {
            var start = cursor;
            while (cursor < value.Length && char.IsAsciiDigit(value[cursor])) cursor++;
            if (cursor == start || !long.TryParse(value[start..cursor], NumberStyles.None, CultureInfo.InvariantCulture, out var n)) throw new FormatException();
            var multiplier = cursor == value.Length ? 1 : char.ToLowerInvariant(value[cursor++]) switch { 'w' => 604800, 'd' => 86400, 'h' => 3600, 'm' => 60, 's' => 1, _ => throw new FormatException() };
            result = checked(result + checked(n * multiplier));
            if (result > int.MaxValue) throw new OverflowException();
        }
        if (value.Length == 0) throw new FormatException();
        return (int)result;
    }
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    private static IEnumerable<Line> Lex(string text)
    {
        var tokens = new List<Token>();
        var current = new StringBuilder();
        var inQuote = false; var quoted = false; var active = false;
        var parentheses = 0; var comment = false; var startLine = true; var omitted = false;
        void Flush()
        {
            if (!active) return;
            tokens.Add(new(current.ToString(), quoted));
            current.Clear(); quoted = false; active = false;
        }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r') continue;
            if (comment && c != '\n') continue;
            if (c == '\n' && !inQuote)
            {
                comment = false; Flush();
                if (parentheses == 0)
                {
                    if (tokens.Count > 0) yield return new(tokens, omitted);
                    tokens = []; startLine = true; omitted = false;
                }
                continue;
            }
            if (startLine)
            {
                omitted = char.IsWhiteSpace(c); startLine = false;
            }
            if (c == '\\')
            {
                if (++i >= text.Length) throw new FormatException();
                if (!inQuote) throw new FormatException();
                active = true;
                if (i + 2 < text.Length && char.IsAsciiDigit(text[i]) && char.IsAsciiDigit(text[i + 1]) && char.IsAsciiDigit(text[i + 2]))
                {
                    if (!byte.TryParse(text.AsSpan(i, 3), out var b)) throw new FormatException();
                    current.Append((char)b); i += 2;
                }
                else current.Append(text[i]);
                continue;
            }
            if (c == '"')
            {
                if (!inQuote && active) throw new FormatException();
                inQuote = !inQuote; quoted = true; active = true;
                if (!inQuote) Flush();
                continue;
            }
            if (inQuote) { if (c == '\n') throw new FormatException(); current.Append(c); continue; }
            if (c == ';') { Flush(); comment = true; continue; }
            if (c == '(') { Flush(); if (++parentheses > 1) throw new FormatException(); continue; }
            if (c == ')') { Flush(); if (--parentheses < 0) throw new FormatException(); continue; }
            if (char.IsWhiteSpace(c)) { Flush(); continue; }
            active = true; current.Append(c);
        }
        if (inQuote || parentheses != 0) throw new FormatException();
        Flush();
        if (tokens.Count != 0) yield return new(tokens, omitted);
    }
}
