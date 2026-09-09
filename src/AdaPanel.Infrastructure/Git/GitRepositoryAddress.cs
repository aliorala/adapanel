using System.Text.RegularExpressions;

namespace AdaPanel.Infrastructure.Git;

/// <summary>
/// Bir git depo adresinin BİÇİMİNİ ve KONAĞINI çözen TEK ayrıştırıcı.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN AYRI BİR DOSYA — kapatılan açık burada.</b> <c>GitDeploymentService</c> depo adresinin
/// konağını <c>Uri.TryCreate</c> ile çıkarıyordu ve scp kalıbı (<c>git@host:yol/depo.git</c>)
/// <c>Uri</c> ile AYRIŞTIRILAMAZ. Bu makinede .NET 8 ile ÖLÇÜLDÜ:
/// </para>
/// <list type="bullet">
/// <item><c>git@github.com:kullanici/depo.git</c> → <c>Uri.TryCreate(..., Absolute)</c> = <b>false</b></item>
/// <item><c>git@169.254.169.254:latest/meta-data</c> → <b>false</b></item>
/// <item><c>git@localhost:depo.git</c> → <b>false</b></item>
/// </list>
/// <para>
/// Yani adres kapısı (<c>OutboundAddressGuard</c>) o dalda ERKEN DÖNÜYORDU: scp biçiminde yazılan
/// bir adres kapıya HİÇ UĞRAMIYORDU. Koruma "zayıf" değil, <b>sıfır</b>dı. Aynı adres
/// <c>ValidateRepositoryUrl</c>'in scp beyaz listesinden geçtiği için de reddedilmiyordu.
/// </para>
/// <para>
/// <b>Neden İKİNCİ bir ayrıştırıcı değil:</b> scp kalıbının deseni bu depoda zaten VARDI
/// (<c>ValidateRepositoryUrl</c> içinde satır içi bir <c>Regex</c>). Konak çıkarımı için ikinci bir
/// desen yazmak, bu deponun defalarca ölçülmüş hata sınıfının ta kendisi olurdu: iki nüsha ayrışır,
/// biri düzeltilir, öteki sessizce açık kalır. Bu yüzden desen BURAYA taşındı ve hem BİÇİM
/// doğrulaması hem KONAK çıkarımı aynı eşleşmeyi kullanıyor — "kabul edilen biçim" ile "denetlenen
/// konak" tanım gereği aynı şeyden geliyor.
/// </para>
/// </remarks>
internal static class GitRepositoryAddress
{
    /// <summary>
    /// scp benzeri adres: <c>[kullanıcı@]konak[:port]:yol</c>.
    /// </summary>
    /// <remarks>
    /// Desen <c>ValidateRepositoryUrl</c>'den TAŞINDI, yeniden yazılmadı; karakter sınıfları
    /// birebir aynıdır (aksi hâlde bugüne kadar kabul edilen meşru bir adres sessizce reddedilirdi).
    /// Tek değişiklik adlandırılmış gruplardır — konak artık okunabiliyor.
    /// <para>
    /// Konak sınıfı köşeli parantez İÇERMEZ: <c>user@[::1]:depo.git</c> bu desenle eşleşmez ve
    /// <c>Uri</c> ile de ayrıştırılamaz (ölçüldü), yani biçim doğrulamasında reddedilir. Bu bilinçli
    /// bir sınırdır: ayrıştıramadığımız bir konağı "temiz" saymak, kapatılan açığın kendisiydi.
    /// </para>
    /// </remarks>
    internal static readonly Regex ScpLikePattern = new(
        @"^[A-Za-z0-9._-]+@(?<konak>[A-Za-z0-9._-]+)(?::[0-9]+)?:[A-Za-z0-9._/~-]+$",
        RegexOptions.Compiled);

    /// <summary>Adres scp kalıbında mı (<c>git@host:owner/repo.git</c>)?</summary>
    internal static bool IsScpLike(string url) => ScpLikePattern.IsMatch(url);

    /// <summary>
    /// Adresin KONAĞINI çıkarır. Çıkaramazsa <c>false</c> döner — ve çağıran bunu "geç" değil
    /// "REDDET" olarak okumalıdır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sıra ÖNEMLİ: önce mutlak <c>Uri</c> denenir ama YALNIZ izin verilen şemalarda
    /// (<c>http/https/ssh</c>) kabul edilir. Başka bir şema ayrıştırılabilse bile buradan çıkmaz;
    /// aksi hâlde <c>ext::…</c> gibi bir taşıyıcı yazımı "konağı yok, demek ki temiz" diye
    /// geçebilirdi. Şema tanınmazsa scp kalıbı denenir.
    /// </para>
    /// <para>
    /// <c>DnsSafeHost</c> kullanılır çünkü IPv6 literalindeki köşeli parantezleri atar, IDN adını
    /// punycode'a çevirir ve ondalık/sekizlik/onaltılık IPv4 yazımlarını noktalı dörtlüye
    /// normalleştirir — <c>OutboundAddressGuard</c> tam olarak bu biçimi bekler.
    /// </para>
    /// </remarks>
    internal static bool TryGetHost(string? url, out string host)
    {
        host = string.Empty;

        var trimmed = (url ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" or "ssh")
        {
            host = uri.DnsSafeHost;
            return host.Length > 0;
        }

        var eslesme = ScpLikePattern.Match(trimmed);
        if (!eslesme.Success)
        {
            return false;
        }

        host = eslesme.Groups["konak"].Value;
        return host.Length > 0;
    }
}
