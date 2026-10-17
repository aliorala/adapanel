using AdaPanel.Core.Dtos.Mail;
using DnsClient;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>
/// Mail teşhisini (MX/A/SPF/DKIM/DMARC/PTR canlı DNS kontrolü) mail sunucusundan BAĞIMSIZ çalıştırır;
/// hem Stalwart hem MailEnable servisleri aynı mantığı kullanır. Hatalar izole edilir; iptal korunur.
/// </summary>
internal static class MailDiagnosticsRunner
{
    public static async Task<MailDiagnosticsDto> RunAsync(MailDnsDto dns, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = dns.Domain.Trim().ToLowerInvariant();
        var mailHost = string.IsNullOrWhiteSpace(dns.MailHost) ? $"mail.{normalized}" : dns.MailHost.Trim().TrimEnd('.').ToLowerInvariant();
        var serverIp = dns.ServerAddress?.Trim() ?? string.Empty;
        var dkimRecords = dns.Records.Where(record => string.Equals(record.Purpose, "dkim", StringComparison.OrdinalIgnoreCase)).ToArray();
        var spfExpected = dns.Records.FirstOrDefault(record => string.Equals(record.Purpose, "spf", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
        var dmarcExpected = dns.Records.FirstOrDefault(record => string.Equals(record.Purpose, "dmarc", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;

        // 4 sn timeout, tek deneme, önbelleksiz: teşhis hızlı ve güncel olsun.
        var lookup = new LookupClient(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(4), Retries = 1, UseCache = false });
        var checks = new List<MailDiagnosticCheckDto>();

        // MX → domainin MX'i mail.<domain>'i göstermeli.
        checks.Add(await RunCheckAsync("mx", mailHost, async () =>
        {
            var response = await lookup.QueryAsync(normalized, QueryType.MX, cancellationToken: cancellationToken);
            var hosts = response.Answers.MxRecords().Select(record => record.Exchange.Value.TrimEnd('.')).ToArray();
            var ok = hosts.Any(host => host.Equals(mailHost, StringComparison.OrdinalIgnoreCase));
            return (ok, string.Join(", ", hosts));
        }));

        // A → mail.<domain> sunucu IP'sine çözülmeli.
        checks.Add(await RunCheckAsync("a", serverIp, async () =>
        {
            // Panelde sunucu adresi yoksa karşılaştıracak değer yok → "kontrol edilemedi".
            if (string.IsNullOrEmpty(serverIp))
            {
                throw new InvalidOperationException("server address not configured");
            }

            var response = await lookup.QueryAsync(mailHost, QueryType.A, cancellationToken: cancellationToken);
            var ips = response.Answers.ARecords().Select(record => record.Address.ToString()).ToArray();
            var ok = ips.Any(ip => ip.Equals(serverIp, StringComparison.OrdinalIgnoreCase));
            return (ok, string.Join(", ", ips));
        }));

        // SPF → domainin TXT'sinde v=spf1 olmalı.
        checks.Add(await RunCheckAsync("spf", spfExpected, async () =>
        {
            var values = await TxtValuesAsync(lookup, normalized, cancellationToken);
            return CheckTxt("spf", values, spfExpected);
        }));

        // DKIM → <selector>._domainkey.<domain> TXT'sinde v=DKIM1 olmalı.
        checks.Add(await RunCheckAsync("dkim", string.Join(" | ", dkimRecords.Select(r => r.Host + ": " + r.Value)), async () =>
        {
            if (dkimRecords.Length == 0) return (false, string.Empty);
            var healthy = true;
            var found = new List<string>();
            foreach (var dkim in dkimRecords)
            {
                var host = dkim.Host.Trim().TrimEnd('.');
                if (string.IsNullOrEmpty(host)) return (false, string.Empty);
                var fqdn = host.EndsWith("." + normalized, StringComparison.OrdinalIgnoreCase) ? host : $"{host}.{normalized}";
                var values = await TxtValuesAsync(lookup, fqdn, cancellationToken);
                var result = CheckTxt("dkim", values, dkim.Value);
                healthy &= result.Ok;
                found.Add(host + ": " + result.Found);
            }
            return (healthy, string.Join(" | ", found));
        }));

        // DMARC → _dmarc.<domain> TXT'sinde v=DMARC1 olmalı.
        checks.Add(await RunCheckAsync("dmarc", dmarcExpected, async () =>
        {
            var values = await TxtValuesAsync(lookup, $"_dmarc.{normalized}", cancellationToken);
            return CheckTxt("dmarc", values, dmarcExpected);
        }));

        // PTR → sunucu IP'sinin ters DNS'i mail.<domain> olmalı (sağlayıcıda ayarlanır).
        checks.Add(await RunCheckAsync("ptr", mailHost, async () =>
        {
            if (!System.Net.IPAddress.TryParse(serverIp, out var ip))
            {
                // Sunucu IP'si yok/geçersiz → karşılaştıramayız → "kontrol edilemedi".
                throw new InvalidOperationException("server address not configured");
            }

            var response = await lookup.QueryReverseAsync(ip, cancellationToken);
            var ptr = response.Answers.PtrRecords().Select(record => record.PtrDomainName.Value.TrimEnd('.')).ToArray();
            var ok = ptr.Any(host => host.Equals(mailHost, StringComparison.OrdinalIgnoreCase));
            return (ok, string.Join(", ", ptr));
        }));

        // Sunucu-yerel port dinleme kontrolü: mail servisi (Stalwart) bu portları AÇMIŞ mı?
        // 25=gelen SMTP/MX, 587=submission, 143=IMAP. Yerelde dinlenmiyorsa gelen mail/istemci çalışmaz
        // (DNS doğru olsa bile). Dış erişilebilirliği değil, sunucuda dinlemeyi gösterir.
        checks.Add(await RunCheckAsync("smtp", "127.0.0.1:25", () => ProbePortAsync(25, cancellationToken)));
        checks.Add(await RunCheckAsync("submission", "127.0.0.1:587", () => ProbePortAsync(587, cancellationToken)));
        checks.Add(await RunCheckAsync("imap", "127.0.0.1:143", () => ProbePortAsync(143, cancellationToken)));

        return new MailDiagnosticsDto
        {
            Domain = normalized,
            MailHost = mailHost,
            ServerAddress = serverIp,
            Checks = checks.ToArray()
        };
    }

    // Sorgu başarısızsa unknown; çağıranın iptali hata raporuna dönüştürülmez.
    private static async Task<MailDiagnosticCheckDto> RunCheckAsync(string key, string expected, Func<Task<(bool Ok, string Found)>> check)
    {
        try
        {
            var (ok, found) = await check();
            return new MailDiagnosticCheckDto { Key = key, Status = ok ? "ok" : "fail", Expected = expected, Found = found };
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return new MailDiagnosticCheckDto { Key = key, Status = "unknown", Expected = expected, Found = string.Empty };
        }
    }

    // Yerel TCP connect ile portun dinlenip dinlenmediğini kontrol eder (3 sn timeout). Dış
    // erişilebilirliği DEĞİL, mail servisinin sunucuda o portu açıp açmadığını gösterir.
    private static async Task<(bool Ok, string Found)> ProbePortAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new System.Net.Sockets.TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port, timeout.Token);
            return (client.Connected, $"dinleniyor (127.0.0.1:{port})");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return (false, $"dinlenmiyor (127.0.0.1:{port})");
        }
    }

    private static async Task<List<string>> TxtValuesAsync(ILookupClient lookup, string name, CancellationToken cancellationToken)
    {
        var response = await lookup.QueryAsync(name, QueryType.TXT, cancellationToken: cancellationToken);
        return response.Answers.TxtRecords().Select(record => string.Concat(record.Text)).ToList();
    }

    // DNS configuration checks, not a delivery-time SPF/DMARC or message signature verifier.
    internal static (bool Ok, string Found) CheckTxt(string kind, IEnumerable<string> values, string expected)
    {
        var records = values.Where(value => kind switch
        {
            "spf" => value.Equals("v=spf1", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("v=spf1 ", StringComparison.OrdinalIgnoreCase),
            "dmarc" => value.StartsWith("v=DMARC1;", StringComparison.Ordinal)
                || value.Equals("v=DMARC1", StringComparison.Ordinal),
            // A DKIM key record may omit v=; the key value is case sensitive.
            "dkim" => ParseTags(value)?.ContainsKey("p") == true || value.StartsWith("v=DKIM1", StringComparison.Ordinal),
            _ => false
        }).ToArray();
        var found = string.Join(" | ", records);
        if (records.Length != 1) return (false, found);
        if (kind == "spf")
            return (!string.IsNullOrWhiteSpace(expected) && string.Equals(
                NormalizeSpaces(records[0]), NormalizeSpaces(expected), StringComparison.Ordinal), found);
        var actual = ParseTags(records[0]);
        var wanted = ParseTags(expected);
        if (actual is null || wanted is null) return (false, found);
        if (kind == "dkim")
        {
            var key = RemoveSpaces(actual.GetValueOrDefault("p", ""));
            var expectedKey = RemoveSpaces(wanted.GetValueOrDefault("p", ""));
            return (key.Length > 0 && key == expectedKey
                && (!actual.TryGetValue("v", out var version) || version == "DKIM1")
                && actual.GetValueOrDefault("k", "rsa") == wanted.GetValueOrDefault("k", "rsa"), found);
        }
        return (actual.GetValueOrDefault("p") is "none" or "quarantine" or "reject"
            && wanted.All(pair => actual.GetValueOrDefault(pair.Key) == pair.Value), found);
    }

    private static Dictionary<string, string>? ParseTags(string value)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 || !tags.TryAdd(part[..separator].Trim(), part[(separator + 1)..].Trim())) return null;
        }
        return tags;
    }

    private static string NormalizeSpaces(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string RemoveSpaces(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
}
