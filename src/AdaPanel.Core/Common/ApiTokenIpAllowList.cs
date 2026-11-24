using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace AdaPanel.Core.Common;

/// <summary>
/// API anahtarı IP/CIDR izin listesi: ayrıştırma, normalleştirme ve eşleştirme TEK yerde.
/// </summary>
public static class ApiTokenIpAllowList
{
    public const int MaxEntries = 32;

    public const int MaxLength = 1536; // 32 x ("ffff:...:ffff/128" = 43) + ayraçlar

    /// <summary>
    /// Girdiyi (virgül/boşluk/satır ayraçlı) doğrular ve normalleştirir. Geçersizse null,
    /// boşsa "" döner. Çıktı: "203.0.113.5,198.51.100.0/24,2001:db8::/32".
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var parts = input.Split([',', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > MaxEntries)
        {
            return null;
        }

        var result = new List<string>();
        foreach (var part in parts)
        {
            if (!TryParseEntry(part, out var network, out var prefix))
            {
                return null;
            }

            var maxPrefix = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            var text = prefix == maxPrefix ? network.ToString() : network + "/" + prefix;
            if (!result.Contains(text, StringComparer.Ordinal))
            {
                result.Add(text);
            }
        }

        var joined = string.Join(',', result);
        return joined.Length > MaxLength ? null : joined;
    }

    /// <summary>Liste boşsa (kısıt yok) true; doluysa adres bir girdiyle eşleşiyorsa true.</summary>
    public static bool IsAllowed(string? allowedIps, IPAddress? remote)
    {
        if (string.IsNullOrWhiteSpace(allowedIps))
        {
            return true;
        }

        // Adres bilinmiyorsa kısıtlı anahtar REDDEDİLİR (güvenli taraf).
        if (remote is null)
        {
            return false;
        }

        if (remote.IsIPv4MappedToIPv6)
        {
            remote = remote.MapToIPv4();
        }

        foreach (var part in allowedIps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseEntry(part, out var network, out var prefix) && Contains(network, prefix, remote))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseEntry(string entry, out IPAddress network, out int prefix)
    {
        network = IPAddress.None;
        prefix = 0;
        var slash = entry.IndexOf('/');
        var addressText = slash < 0 ? entry : entry[..slash];
        if (!IPAddress.TryParse(addressText, out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var maxPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        prefix = maxPrefix;
        if (slash >= 0 && (!int.TryParse(entry[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefix)
                           || prefix < 0 || prefix > maxPrefix))
        {
            return false;
        }

        // Ağ adresine indir: "10.1.2.3/8" -> "10.0.0.0/8".
        var bytes = address.GetAddressBytes();
        for (var bit = prefix; bit < bytes.Length * 8; bit++)
        {
            bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        }

        network = new IPAddress(bytes);
        return true;
    }

    private static bool Contains(IPAddress network, int prefix, IPAddress candidate)
    {
        if (network.AddressFamily != candidate.AddressFamily)
        {
            return false;
        }

        var a = network.GetAddressBytes();
        var b = candidate.GetAddressBytes();
        for (var bit = 0; bit < prefix; bit++)
        {
            var mask = 0x80 >> (bit % 8);
            if ((a[bit / 8] & mask) != (b[bit / 8] & mask))
            {
                return false;
            }
        }

        return true;
    }
}
