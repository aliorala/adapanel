using System.Text.RegularExpressions;

namespace AdaPanel.Infrastructure.Iis;

/// <summary>
/// Panelin yönettiği IIS uygulama havuzu adının TEK KAYNAĞI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden var — ölçülmüş, sessiz bozulan bir sözleşme.</b> Havuz adı iki ayrı yerde
/// üretiliyordu ve <b>ikisi aynı adı üretmiyordu</b>:
/// </para>
/// <list type="bullet">
/// <item><c>WebAdministrationSiteService</c> (havuzu GERÇEKTEN kuran taraf) adı önce
/// temizliyordu: <c>[^A-Za-z0-9._-]</c> dışındaki her şey <c>-</c> oluyordu.</item>
/// <item><c>WafService.IsolateAppPoolAsync</c> ise site adını HAM haliyle ekliyordu.</item>
/// </list>
/// <para>
/// Site adı yalnızca kırpılıyor ve aygıt adı denetiminden geçiyor; karakter kümesi
/// <c>[A-Za-z0-9._-]</c> ile SINIRLI DEĞİL. Yani <c>Müşteri Sitesi</c> gibi bir ad için kuran
/// taraf <c>AdaPanel.M-teri-Sitesi</c>, izolasyon ise <c>AdaPanel.Müşteri Sitesi</c> diyordu.
/// Sonuç: izolasyon var olan havuzu bulamayıp <b>İKİNCİ bir havuz</b> açıyor, sitenin ACL'leri
/// eski havuzun kimliğine verilmiş olduğu için <b>boşa düşüyor</b> — ve
/// <c>MeasureIsolation</c> yalnızca ÖNEKE baktığı için panel yine "izole" diye raporluyordu.
/// </para>
/// <para>
/// <b>Bu, bu sürümde kapatılan üçüncü aynı hata.</b> FTP ev klasörü ve site yazma izni de aynı
/// kalıptı: panel bir AD ya da YOL üretip dış sisteme veriyor, o sistemin aynısını beklediğini
/// VARSAYIYOR, çağrı başarılı dönüyor ve iki taraf farklı şeyden bahsediyor. Kalıcı çözüm her
/// seferinde aynı: adı iki yerde üretme, TEK yerden oku.
/// </para>
/// </remarks>
public static partial class ManagedAppPoolName
{
    /// <summary>Panelin yönettiği havuzların öneki.</summary>
    /// <remarks>
    /// Önek bir SAHİPLİK işaretidir: panel yalnız bu önekli havuzlara dokunur, sunucuda elle
    /// kurulmuş havuzlara karışmaz.
    /// </remarks>
    public const string Prefix = "AdaPanel.";

    /// <summary>Site adından, IIS'te GERÇEKTEN kurulacak havuz adını üretir.</summary>
    public static string Build(string siteName) => Prefix + Sanitize(siteName);

    /// <summary>Bir havuz adının paneli ilgilendirip ilgilendirmediği.</summary>
    public static bool IsManaged(string? applicationPoolName)
        => applicationPoolName?.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Havuz adında kullanılamayacak karakterleri <c>-</c> ile değiştirir.
    /// </summary>
    /// <remarks>
    /// Temizleme BURADA, <see cref="Build"/> ile aynı dosyada: ayrı dursaydı biri temizleyip
    /// öteki temizlemeden ekleyebilirdi — kapatılan hata tam olarak buydu.
    /// </remarks>
    public static string Sanitize(string siteName) => AppPoolNamePattern().Replace(siteName, "-");

    [GeneratedRegex("[^A-Za-z0-9._-]+", RegexOptions.Compiled)]
    private static partial Regex AppPoolNamePattern();
}
