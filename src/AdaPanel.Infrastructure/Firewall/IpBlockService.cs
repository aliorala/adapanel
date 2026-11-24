using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Firewall;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Firewall;

/// <summary>Windows Firewall'a engel kurali yazan/silen kapi. Testlerde sahtesi verilir.</summary>
public interface IIpBlockRuleWriter
{
    /// <summary>Kurali yazar ve GERI OKUR; okunamazsa ya da adres uyusmazsa basarisiz doner.</summary>
    Task<(bool Applied, string Error)> ApplyAsync(string ruleName, string network, CancellationToken cancellationToken);

    /// <summary>Kurali kaldirir ve kalkmis oldugunu GERI OKUR ("zaten yok" basaridir).</summary>
    Task<(bool Removed, string Error)> RemoveAsync(string ruleName, CancellationToken cancellationToken);
}

public sealed class PowerShellIpBlockRuleWriter : IIpBlockRuleWriter
{
    public async Task<(bool Applied, string Error)> ApplyAsync(string ruleName, string network, CancellationToken cancellationToken)
    {
        // Ad ve adres TEK TIRNAKLI literal'e girer (bkz. PowerShellQuoting). Adres ayrica IPAddress ile
        // ayristirilmis degerdir; kullanici metni dogrudan betige gitmez.
        var script = $$"""
            Remove-NetFirewallRule -DisplayName '{{PowerShellQuoting.Escape(ruleName)}}' -ErrorAction SilentlyContinue | Out-Null
            New-NetFirewallRule -DisplayName '{{PowerShellQuoting.Escape(ruleName)}}' -Direction Inbound -Action Block -RemoteAddress '{{PowerShellQuoting.Escape(network)}}' -Profile Any | Out-Null
            $r = Get-NetFirewallRule -DisplayName '{{PowerShellQuoting.Escape(ruleName)}}' -ErrorAction Stop
            $a = $r | Get-NetFirewallAddressFilter -ErrorAction Stop
            if (-not $r.Enabled -or [string]$r.Action -ne 'Block') { throw 'rule readback mismatch' }
            if (-not ($a.RemoteAddress -contains '{{PowerShellQuoting.Escape(network)}}') -and -not ($a.RemoteAddress -contains '{{PowerShellQuoting.Escape(HostForm(network))}}')) { throw 'remote address readback mismatch' }
            """;
        var result = await ProcessRunner.RunPowerShellStrictAsync(script, cancellationToken);
        return result.Success ? (true, string.Empty) : (false, FirstLine(result.StandardError));
    }

    public async Task<(bool Removed, string Error)> RemoveAsync(string ruleName, CancellationToken cancellationToken)
    {
        var script = $$"""
            Remove-NetFirewallRule -DisplayName '{{PowerShellQuoting.Escape(ruleName)}}' -ErrorAction SilentlyContinue | Out-Null
            $left = @(Get-NetFirewallRule -DisplayName '{{PowerShellQuoting.Escape(ruleName)}}' -ErrorAction SilentlyContinue)
            if ($left.Count -gt 0) { throw 'rule still present' }
            """;
        var result = await ProcessRunner.RunPowerShellStrictAsync(script, cancellationToken);
        return result.Success ? (true, string.Empty) : (false, FirstLine(result.StandardError));
    }

    /// <summary>Tek adres /32 (ya da /128) yazildiginda Windows onu adres olarak geri verir.</summary>
    private static string HostForm(string network) =>
        network.EndsWith("/32", StringComparison.Ordinal) ? network[..^3]
        : network.EndsWith("/128", StringComparison.Ordinal) ? network[..^4] : network;

    private static string FirstLine(string text) =>
        (text ?? string.Empty).Split('\n', 2)[0].Trim() is { Length: > 0 } line
            ? (line.Length > 300 ? line[..300] : line)
            : "firewall rule could not be written";
}

/// <summary>
/// IP/CIDR engel listesi + sureli engel. Durum <c>config/ip-blocklist.json</c>'da, kurallar Windows
/// Firewall'da <c>AdaPanel IpBlock &lt;id&gt;</c> adiyla durur. Kural yazilamazsa girdi "failed" olarak
/// KAYDEDILIR ve kullaniciya oyle soylenir (IP agda engellenmemistir; sessiz basari yoktur).
/// </summary>
public sealed class IpBlockService(
    JsonFileStore fileStore,
    IIpBlockRuleWriter ruleWriter,
    ILogger<IpBlockService> logger) : IIpBlockService
{
    internal const string StatePath = "config/ip-blocklist.json";
    internal const string RulePrefix = "AdaPanel IpBlock ";
    private const int MaxEntries = 500;
    private const int MaxDurationMinutes = 525600;

    /// <summary>Panelin kendi arayuz adreslerini veren kaynak; testlerde degistirilir.</summary>
    internal Func<IReadOnlyCollection<IPAddress>> LocalAddresses { get; init; } = ReadLocalAddresses;

    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    public async Task<IpBlockListDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var all = await fileStore.ReadAsync(StatePath, Array.Empty<IpBlockEntryDto>(), cancellationToken);
        return new IpBlockListDto
        {
            Entries = all.OrderByDescending(item => item.CreatedAtUtc).ToArray(),
            CountryBlockSupported = false
        };
    }

    public async Task<IpBlockEntryDto> AddAsync(AddIpBlockRequest request, string actor,
        IReadOnlyCollection<string> protectedAddresses, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(request.CountryCode))
            throw new AppException("firewall.geoip_unavailable", 501, 501);
        var network = ParseNetwork(request.Target);

        var minutes = request.DurationMinutes.GetValueOrDefault();
        if (minutes < 0 || minutes > MaxDurationMinutes) throw new AppException("firewall.ipblock_duration_invalid", 400, 400);
        var reason = (request.Reason ?? string.Empty).Trim();
        if (reason.Length > 200 || reason.Any(char.IsControl)) throw new AppException("firewall.ipblock_reason_invalid", 400, 400);

        // KILITLENME KORUMASI: engellenecek ag, panelin kendi adresini ya da yoneticinin oturum
        // adresini KAPSIYORSA hicbir sey yazilmaz. Windows'ta engel izni ezer; yonetici kendini
        // disari atarsa panelden geri acamaz (yalniz sunucu konsolu kalir).
        var protectedIps = protectedAddresses
            .Select(text => IPAddress.TryParse(text, out var ip) ? ip : null).OfType<IPAddress>()
            .Concat(LocalAddresses()).Append(IPAddress.Loopback).Append(IPAddress.IPv6Loopback).ToArray();
        var hit = protectedIps.FirstOrDefault(ip => network.Contains(ip));
        if (hit is not null)
            throw new AppException("firewall.ipblock_would_lock_out", 409, 409, $"{request.Target.Trim()} covers protected address {hit}.");

        var now = UtcNow();
        var id = Guid.NewGuid().ToString("N")[..12];
        var entry = new IpBlockEntryDto
        {
            Id = id,
            Target = network.ToString(),
            Reason = reason,
            CreatedAtUtc = now,
            ExpiresAtUtc = minutes > 0 ? now.AddMinutes(minutes) : null,
            CreatedBy = (actor ?? string.Empty).Trim(),
            RuleName = RulePrefix + id,
            RuleState = "failed",
            RuleError = "pending"
        };

        await fileStore.UpdateAsync(StatePath, Array.Empty<IpBlockEntryDto>(), existing =>
        {
            if (existing.Any(item => string.Equals(item.Target, entry.Target, StringComparison.Ordinal)))
                throw new AppException("firewall.ipblock_duplicate", 409, 409);
            if (existing.Length >= MaxEntries) throw new AppException("firewall.ipblock_limit_reached", 409, 409);
            return existing.Append(entry).ToArray();
        }, cancellationToken);

        var (applied, error) = await ruleWriter.ApplyAsync(entry.RuleName, entry.Target, cancellationToken);
        entry.RuleState = applied ? "applied" : "failed";
        entry.RuleError = applied ? string.Empty : error;
        await fileStore.UpdateAsync(StatePath, Array.Empty<IpBlockEntryDto>(), existing =>
        {
            foreach (var item in existing.Where(item => item.Id == id))
            {
                item.RuleState = entry.RuleState;
                item.RuleError = entry.RuleError;
            }
            return existing.ToArray();
        }, cancellationToken);
        if (!applied)
            logger.LogError("IP block {Target}: firewall rule could NOT be written ({Error}); the address is not blocked on the network.",
                entry.Target, error);
        return entry;
    }

    public async Task<IpBlockEntryDto> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        var all = await fileStore.ReadAsync(StatePath, Array.Empty<IpBlockEntryDto>(), cancellationToken);
        var entry = all.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal))
            ?? throw new AppException("firewall.ipblock_not_found", 404, 404);
        var (removed, error) = await ruleWriter.RemoveAsync(entry.RuleName, cancellationToken);
        // Kural KALKTIGI dogrulanmadan kayit silinmez: aksi halde panelde gorunmeyen ama aginda
        // engelli bir adres kalir (geri alinamayan, izsiz engel).
        if (!removed) throw new AppException("firewall.write_failed", 500, 500, error);
        await fileStore.UpdateAsync(StatePath, Array.Empty<IpBlockEntryDto>(),
            existing => existing.Where(item => item.Id != id).ToArray(), cancellationToken);
        return entry;
    }

    public async Task<IReadOnlyCollection<IpBlockEntryDto>> SweepExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = UtcNow();
        var all = await fileStore.ReadAsync(StatePath, Array.Empty<IpBlockEntryDto>(), cancellationToken);
        var removedEntries = new List<IpBlockEntryDto>();
        foreach (var entry in all.Where(item => item.ExpiresAtUtc is { } expires && expires <= now))
        {
            var (removed, error) = await ruleWriter.RemoveAsync(entry.RuleName, cancellationToken);
            if (!removed)
            {
                // Kayit KALIR: bir sonraki turda yeniden denenir; kural varken kaydi silmek engeli sahipsiz birakir.
                logger.LogWarning("Expired IP block {Target}: firewall rule could not be removed ({Error}); will retry.", entry.Target, error);
                continue;
            }
            removedEntries.Add(entry);
        }
        if (removedEntries.Count > 0)
        {
            var ids = removedEntries.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            await fileStore.UpdateAsync(StatePath, Array.Empty<IpBlockEntryDto>(),
                existing => existing.Where(item => !ids.Contains(item.Id)).ToArray(), cancellationToken);
        }
        return removedEntries;
    }

    internal static IpNetwork ParseNetwork(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0 || value.Length > 64 || value.Any(char.IsWhiteSpace) || value.Contains(','))
            throw new AppException("firewall.ipblock_target_invalid", 400, 400);
        var slash = value.IndexOf('/');
        var addressText = slash < 0 ? value : value[..slash];
        if (!IPAddress.TryParse(addressText, out var address) || addressText.Contains('%'))
            throw new AppException("firewall.ipblock_target_invalid", 400, 400);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = max;
        if (slash >= 0 && (!int.TryParse(value[(slash + 1)..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out prefix) || prefix < 0 || prefix > max))
            throw new AppException("firewall.ipblock_target_invalid", 400, 400);
        // Hepsini engelle (/0) bir engel listesi girdisi degildir.
        if (prefix == 0) throw new AppException("firewall.ipblock_target_invalid", 400, 400);
        // Cok genis aralik (IPv4 /8'den, IPv6 /32'den genis) toplu yanlis engel riskidir: reddedilir.
        if (prefix < (address.AddressFamily == AddressFamily.InterNetwork ? 8 : 32))
            throw new AppException("firewall.ipblock_range_too_wide", 400, 400);
        return new IpNetwork(address, prefix);
    }

    private static IReadOnlyCollection<IPAddress> ReadLocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses.Select(unicast => unicast.Address)).ToArray();
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
        {
            return [];
        }
    }
}

/// <summary>Ag adresi + onek; adresin host bitleri sifirlanir.</summary>
public readonly struct IpNetwork
{
    private readonly byte[] bytes;

    public IpNetwork(IPAddress address, int prefix)
    {
        Prefix = prefix;
        var raw = address.GetAddressBytes();
        for (var bit = prefix; bit < raw.Length * 8; bit++) raw[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        bytes = raw;
        Family = address.AddressFamily;
    }

    public int Prefix { get; }

    public AddressFamily Family { get; }

    public bool Contains(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != Family) return false;
        var other = address.GetAddressBytes();
        for (var bit = 0; bit < Prefix; bit++)
            if ((other[bit / 8] & (0x80 >> (bit % 8))) != (bytes[bit / 8] & (0x80 >> (bit % 8)))) return false;
        return true;
    }

    public override string ToString() => $"{new IPAddress(bytes)}/{Prefix}";
}
