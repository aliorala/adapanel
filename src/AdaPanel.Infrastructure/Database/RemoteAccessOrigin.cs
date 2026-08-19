using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Database;

/// <summary>Uzak erişim kipi. Varsayılan <see cref="Closed"/> — kapı kapalı doğar.</summary>
internal enum RemoteAccessMode
{
    /// <summary>Yalnız sunucunun kendisi (localhost / 127.0.0.1 / ::1).</summary>
    Closed = 0,

    /// <summary>Yalnız listelenen kaynaklar.</summary>
    Allowlist = 1,

    /// <summary>Her yer. Açık onay olmadan seçilemez.</summary>
    Anywhere = 2
}

internal enum RemoteAccessOriginKind
{
    Anywhere,
    Ipv4,
    Ipv4Network,
    Ipv4Pattern,
    Ipv6
}

/// <summary>
/// Kullanıcının girdiği "nereden bağlanılabilir" değerinin ÇÖZÜMLENMİŞ hâli.
/// </summary>
/// <remarks>
/// <para>
/// BU SINIF BİR GÜVENLİK SINIRIDIR. Değer kullanıcıdan gelir ve iki yere gider: MySQL'de bir SQL
/// dizgi sabitine (<c>'kullanici'@'&lt;host&gt;'</c>), PostgreSQL'de ise <c>pg_hba.conf</c> satırına.
/// İkisi de kaçırmayla değil, YENİDEN ÜRETİMLE korunur: ayrıştırıcı girdiden yalnızca SAYILARI
/// (oktetler, önek uzunluğu) alır ve çıktıyı bu sayılardan kendisi kurar. Girdinin hiçbir baytı
/// çıktıya kopyalanmaz — dolayısıyla tırnak, satır sonu ya da yorum karakteri geçiremez.
/// Tek istisna IPv6'dır: orada çıktı <see cref="IPAddress"/>'in kanonik biçimidir (yine girdi değil).
/// </para>
/// <para>
/// Ana bilgisayar ADI (DNS) bilerek KABUL EDİLMEZ. DNS tabanlı erişim listesi, saldırganın
/// denetleyebildiği bir çözümlemeye güvenmek demektir; ayrıca iki motorda iki ayrı sözdizimi
/// (MySQL <c>%.ornek.com</c>, pg_hba <c>.ornek.com</c>) gerektirirdi ve aynı girdi motora göre
/// farklı davranırdı.
/// </para>
/// </remarks>
internal sealed record RemoteAccessOrigin
{
    private RemoteAccessOrigin(RemoteAccessOriginKind kind, string canonical, byte[]? octets, int prefix, IPAddress? ipv6)
    {
        Kind = kind;
        Canonical = canonical;
        Octets = octets;
        Prefix = prefix;
        Ipv6 = ipv6;
    }

    public RemoteAccessOriginKind Kind { get; }

    /// <summary>Panelde saklanan/gösterilen normalleştirilmiş metin.</summary>
    public string Canonical { get; }

    private byte[]? Octets { get; }

    private int Prefix { get; }

    private IPAddress? Ipv6 { get; }

    public bool IsAnywhere => Kind == RemoteAccessOriginKind.Anywhere;

    /// <summary>"Her yer" anlamına gelen yazımlar. Kullanıcı bunlardan hangisini yazarsa yazsın aynı şeydir.</summary>
    private static readonly HashSet<string> AnywhereSpellings = new(StringComparer.OrdinalIgnoreCase)
    {
        "%", "*", "any", "anywhere", "0.0.0.0/0", "::/0"
    };

    /// <summary>
    /// Kullanıcı girdisini çözümler. Geçersizse <c>database.remote_invalid_origin</c> ile atar.
    /// </summary>
    public static RemoteAccessOrigin Parse(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > 64)
        {
            throw new AppException("database.remote_invalid_origin", 400, 400);
        }

        if (AnywhereSpellings.Contains(text))
        {
            return new RemoteAccessOrigin(RemoteAccessOriginKind.Anywhere, "%", null, 0, null);
        }

        // Sıra önemli: önce ağ (CIDR), sonra joker desen, sonra tek adres.
        return TryParseNetwork(text)
            ?? TryParsePattern(text)
            ?? TryParseSingleAddress(text)
            ?? throw new AppException("database.remote_invalid_origin", 400, 400);
    }

    /// <summary>Aynı kaynağın iki kez yazılması sorun değildir; liste tekilleştirilir ve sırası korunur.</summary>
    public static IReadOnlyList<RemoteAccessOrigin> ParseAll(IEnumerable<string>? values)
    {
        var result = new List<RemoteAccessOrigin>();
        foreach (var value in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var origin = Parse(value);
            if (result.All(item => item.Canonical != origin.Canonical))
            {
                result.Add(origin);
            }
        }

        return result;
    }

    /// <summary>
    /// MySQL/MariaDB hesabının host kısmı (<c>'kullanici'@'&lt;buraya&gt;'</c>).
    /// </summary>
    /// <remarks>
    /// CIDR öneki yalnız /8, /16, /24, /32 için karşılanabilir ve bunlar joker desene çevrilir
    /// (<c>10.0.0.0/16</c> → <c>10.0.%</c>). MySQL'in <c>adres/ağ-maskesi</c> yazımı da yalnız bu dört
    /// uzunluğu kabul eder; /26 gibi bir önek MySQL hesabında HİÇ ifade edilemez. Sessizce
    /// genişletmek (örneğin /26 yerine /24 yazmak) kullanıcının istemediği makinelere kapı açardı,
    /// bu yüzden reddedilir.
    /// </remarks>
    public string ToMySqlHost()
    {
        switch (Kind)
        {
            case RemoteAccessOriginKind.Anywhere:
                return "%";

            case RemoteAccessOriginKind.Ipv4:
                return FormatOctets(4);

            case RemoteAccessOriginKind.Ipv6:
                return Ipv6!.ToString();

            case RemoteAccessOriginKind.Ipv4Pattern:
                return FormatOctets(Prefix / 8) + ".%";

            case RemoteAccessOriginKind.Ipv4Network when Prefix == 32:
                return FormatOctets(4);

            case RemoteAccessOriginKind.Ipv4Network when Prefix is 8 or 16 or 24:
                return FormatOctets(Prefix / 8) + ".%";

            default:
                throw new AppException("database.remote_cidr_unsupported", 400, 400);
        }
    }

    /// <summary>
    /// <c>pg_hba.conf</c> adres alanı. PostgreSQL her önek uzunluğunu kabul eder.
    /// </summary>
    public string ToPostgresAddress()
    {
        return Kind switch
        {
            RemoteAccessOriginKind.Anywhere => "0.0.0.0/0",
            RemoteAccessOriginKind.Ipv4 => FormatOctets(4) + "/32",
            RemoteAccessOriginKind.Ipv6 => Ipv6!.ToString() + "/128",
            RemoteAccessOriginKind.Ipv4Pattern => FormatNetwork(),
            RemoteAccessOriginKind.Ipv4Network => FormatNetwork(),
            _ => throw new AppException("database.remote_invalid_origin", 400, 400)
        };
    }

    /// <summary>
    /// Windows Güvenlik Duvarı <c>-RemoteAddress</c> değeri.
    /// </summary>
    /// <remarks>
    /// "Her yer" burada <c>Any</c> olur; PowerShell bu anahtar kelimeyi bekler, <c>%</c> bekemez.
    /// </remarks>
    public string ToFirewallRemoteAddress()
    {
        return Kind switch
        {
            RemoteAccessOriginKind.Anywhere => "Any",
            RemoteAccessOriginKind.Ipv4 => FormatOctets(4),
            RemoteAccessOriginKind.Ipv6 => Ipv6!.ToString(),
            _ => FormatNetwork()
        };
    }

    private string FormatNetwork()
    {
        var octets = Octets!;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{octets[0]}.{octets[1]}.{octets[2]}.{octets[3]}/{Prefix}");
    }

    private string FormatOctets(int count)
    {
        var octets = Octets!;
        return string.Join('.', octets.Take(count).Select(item => item.ToString(CultureInfo.InvariantCulture)));
    }

    private static RemoteAccessOrigin? TryParseNetwork(string text)
    {
        var slash = text.IndexOf('/');
        if (slash <= 0)
        {
            return null;
        }

        var addressPart = text[..slash];
        var prefixPart = text[(slash + 1)..];
        if (!TryParseOctets(addressPart, out var octets) ||
            !int.TryParse(prefixPart, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) ||
            prefix is < 0 or > 32)
        {
            // IPv6 CIDR BİLEREK desteklenmez: MySQL hesabının host kısmında karşılığı yoktur, yani
            // aynı girdi PostgreSQL'de çalışıp MySQL'de çalışmazdı. Motora göre farklı davranan bir
            // güvenlik ayarı, kullanıcının yanlış varsayımla yaşamasına yol açar.
            return null;
        }

        // Host bitleri maskelenir: 192.168.1.55/24 yazan kullanıcı 192.168.1.0/24 kastediyor.
        // Maskelenmezse pg_hba "invalid CIDR mask" ile REDDEDER ve dosya bozulurdu.
        MaskHostBits(octets, prefix);

        // /0 yalnızca "her yer"dir; onu ayrı bir tür olarak taşımak, "her yer" onayının
        // atlanabileceği ikinci bir yol açardı.
        return prefix == 0
            ? new RemoteAccessOrigin(RemoteAccessOriginKind.Anywhere, "%", null, 0, null)
            : new RemoteAccessOrigin(
                RemoteAccessOriginKind.Ipv4Network,
                string.Create(CultureInfo.InvariantCulture, $"{octets[0]}.{octets[1]}.{octets[2]}.{octets[3]}/{prefix}"),
                octets,
                prefix,
                null);
    }

    private static RemoteAccessOrigin? TryParsePattern(string text)
    {
        if (!text.EndsWith(".%", StringComparison.Ordinal))
        {
            return null;
        }

        var head = text[..^2];
        var parts = head.Split('.');
        if (parts.Length is < 1 or > 3)
        {
            return null;
        }

        var octets = new byte[4];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!TryParseOctet(parts[index], out octets[index]))
            {
                return null;
            }
        }

        var prefix = parts.Length * 8;
        return new RemoteAccessOrigin(
            RemoteAccessOriginKind.Ipv4Pattern,
            string.Join('.', octets.Take(parts.Length)) + ".%",
            octets,
            prefix,
            null);
    }

    private static RemoteAccessOrigin? TryParseSingleAddress(string text)
    {
        if (TryParseOctets(text, out var octets))
        {
            return new RemoteAccessOrigin(
                RemoteAccessOriginKind.Ipv4,
                string.Create(CultureInfo.InvariantCulture, $"{octets[0]}.{octets[1]}.{octets[2]}.{octets[3]}"),
                octets,
                32,
                null);
        }

        // IPv6: yalnız TEK adres. Çıktı IPAddress'in kanonik yazımıdır, girdinin kendisi değil.
        if (text.Contains(':', StringComparison.Ordinal) &&
            IPAddress.TryParse(text, out var parsed) &&
            parsed.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return new RemoteAccessOrigin(RemoteAccessOriginKind.Ipv6, parsed.ToString(), null, 128, parsed);
        }

        return null;
    }

    /// <summary>
    /// Dört oktetli IPv4 ayrıştırması — <see cref="IPAddress.TryParse"/> BİLEREK kullanılmaz.
    /// </summary>
    /// <remarks>
    /// <see cref="IPAddress.TryParse"/> kısaltılmış ("10.1" → 10.0.0.1) ve sekizlik ("010.0.0.1")
    /// yazımları kabul eder. Kullanıcının ekranda gördüğü metinle motora yazılan adresin farklı
    /// olması, bir erişim listesinde beklenmedik makinelere izin vermek demektir.
    /// </remarks>
    private static bool TryParseOctets(string text, out byte[] octets)
    {
        octets = new byte[4];
        var parts = text.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        for (var index = 0; index < 4; index++)
        {
            if (!TryParseOctet(parts[index], out octets[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseOctet(string text, out byte value)
    {
        value = 0;
        if (text.Length is 0 or > 3 || !text.All(char.IsAsciiDigit))
        {
            return false;
        }

        // "010" gibi baştan sıfırlı yazımlar reddedilir (sekizlik karışıklığı).
        if (text.Length > 1 && text[0] == '0')
        {
            return false;
        }

        return byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static void MaskHostBits(byte[] octets, int prefix)
    {
        for (var index = 0; index < 4; index++)
        {
            var bitsForThisOctet = prefix - (index * 8);
            if (bitsForThisOctet >= 8)
            {
                continue;
            }

            octets[index] = bitsForThisOctet <= 0
                ? (byte)0
                : (byte)(octets[index] & (byte)(0xFF << (8 - bitsForThisOctet)));
        }
    }
}
