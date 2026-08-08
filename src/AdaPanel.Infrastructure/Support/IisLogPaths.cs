using System.Xml.Linq;

namespace AdaPanel.Infrastructure.Support;

/// <summary>
/// IIS'in erişim günlüklerini GERÇEKTEN nereye yazdığını çözer.
/// </summary>
/// <remarks>
/// <para>
/// Panel bu yolu üç ayrı yerde (istatistikler, günlük görüntüleyici, otomatik günlük temizliği)
/// sabit kodluyordu: <c>%SystemDrive%\inetpub\logs\LogFiles</c>. IIS'in <c>logFile.directory</c>
/// ayarı hiç okunmuyordu. Yönetici günlük klasörünü taşımışsa (yaygın: <c>D:\Logs</c>) üç özellik
/// de <b>sessizce yanlış cevap</b> veriyordu:
/// </para>
/// <list type="bullet">
/// <item>İstatistikler her zaman sıfır gösteriyordu — hata değil, "trafik yok" gibi.</item>
/// <item>Site günlükleri boş görünüyordu.</item>
/// <item>Otomatik temizlik hiçbir dosya silmiyor ama <b>"tamamlandı"</b> yazıyordu.</item>
/// </list>
/// <para>
/// Ayar <c>applicationHost.config</c>'ten XML olarak okunuyor: COM/reflection gerekmez, panelin
/// başka yerlerinde kullanılan Microsoft.Web.Administration sarmalayıcısına bağımlılık doğurmaz.
/// Okunamazsa IIS'in kendi varsayılanına düşülür — yani en kötü ihtimalle bugünkü davranış.
/// </para>
/// </remarks>
internal static class IisLogPaths
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "inetsrv",
        "config",
        "applicationHost.config");

    /// <summary>
    /// IIS kurulumunun varsayılan günlük kökü (ayar okunamadığında kullanılır).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sistem sürücüsünden kuruluyor, dizge değiştirmeyle DEĞİL.</b> Panelde bu yol üç yerde
    /// şöyle yazılıydı:
    /// </para>
    /// <code>Environment.GetFolderPath(SpecialFolder.System).Replace("System32", "inetpub\\logs\\LogFiles")</code>
    /// <para>
    /// Bu ifade ÇALIŞMIYORDU. <c>GetFolderPath</c> bu makinede ölçüldü ve <c>C:\Windows\system32</c>
    /// döndürüyor — küçük "s" ile. <see cref="string.Replace(string, string)"/> ise büyük/küçük
    /// harfe duyarlıdır, dolayısıyla değişim hiç yapılmıyor ve "günlük dizini" olarak
    /// <c>C:\Windows\system32</c> kullanılıyordu. Sonuç, yönetici hiçbir şey taşımamış olsa bile
    /// HER kurulumda yanlıştı: istatistikler hep sıfır, günlük görüntüleyici system32 içinde
    /// klasör açmaya çalışıyor, otomatik temizlik hiçbir dosya silmeyip "tamamlandı" diyordu.
    /// </para>
    /// </remarks>
    private static string DefaultRoot
    {
        get
        {
            var sistemSurucusu = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));
            return Path.Combine(
                string.IsNullOrWhiteSpace(sistemSurucusu) ? @"C:\" : sistemSurucusu,
                "inetpub",
                "logs",
                "LogFiles");
        }
    }

    /// <summary>Tüm sitelerin günlüklerinin altında toplandığı kök klasör.</summary>
    public static string ResolveRoot() => ResolveRoot(siteId: null);

    /// <summary>Belirli bir sitenin günlük klasörü (<c>&lt;kök&gt;\W3SVCn</c>).</summary>
    public static string ResolveSiteDirectory(long siteId) =>
        Path.Combine(ResolveRoot(siteId), $"W3SVC{siteId}");

    private static string ResolveRoot(long? siteId) => ResolveRootFrom(ConfigPath, siteId);

    /// <summary>
    /// Ayrıştırma mantığı — yapılandırma yolu parametre olduğu için gerçek IIS olmadan test edilir.
    /// </summary>
    internal static string ResolveRootFrom(string configPath, long? siteId)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return DefaultRoot;
            }

            var sites = XDocument.Load(configPath)
                .Root?
                .Element("system.applicationHost")?
                .Element("sites");

            if (sites is null)
            {
                return DefaultRoot;
            }

            // Sitenin KENDİ ayarı varsayılanı ezer (IIS'te de böyle).
            var siteOzel = siteId is { } id
                ? sites.Elements("site")
                    .FirstOrDefault(site => (string?)site.Attribute("id") == id.ToString())?
                    .Element("logFile")?
                    .Attribute("directory")?.Value
                : null;

            var varsayilan = sites.Element("siteDefaults")?.Element("logFile")?.Attribute("directory")?.Value;
            var secilen = Genislet(siteOzel) ?? Genislet(varsayilan);

            return secilen ?? DefaultRoot;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException or System.Security.SecurityException)
        {
            // Okuyamadık; IIS varsayılanına düşüyoruz. Bu, düzeltmeden önceki davranışın aynısı —
            // yani hiçbir kurulumu bozmaz.
            return DefaultRoot;
        }
    }

    private static string? Genislet(string? value)
    {
        var kirpilmis = (value ?? string.Empty).Trim();
        if (kirpilmis.Length == 0)
        {
            return null;
        }

        var genisletilmis = Environment.ExpandEnvironmentVariables(kirpilmis);
        return genisletilmis.Length == 0 ? null : genisletilmis;
    }
}
