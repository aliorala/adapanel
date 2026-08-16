using System.Xml.Linq;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>
/// Otomatik yapılandırma isteklerini panele ileten URL Rewrite (+ARR) kurallarının TEK kaynağı.
/// </summary>
/// <remarks>
/// <para>
/// Ayrı ve saf (I/O'suz) bir sınıf: kuralların İÇERİĞİ ve SIRASI, IIS olmadan test edilebilmeli.
/// Bu depoda "kod var ama hiç çalışmıyor" sınıfından arızalar tam da bu yüzden çıktı — kural
/// yazılıyordu ama yanlış yere/yanlış sıraya.
/// </para>
/// </remarks>
public static class MailAutodiscoverRewriteRules
{
    /// <summary>Panelin kendi kurallarını tanıyıp güncelleyebilmesi için sabit ad öneki.</summary>
    public const string RuleNamePrefix = "AdaPanel.MailAutoconfig.";

    /// <summary>
    /// Yayınlanan üç kural. Sıra bilinçlidir: en özgül eşleşme (koşullu olan) en sonda.
    /// </summary>
    private static readonly (string Name, string Match, string Path, string? HostCondition)[] Kurallar =
    [
        // Outlook'un İLK denediği adres. Müşterinin kendi alan adı olduğu için sertifikası
        // zaten geçerlidir — yeni sertifika/DNS gerektirmeyen tek yol budur.
        ("Outlook", @"^autodiscover/autodiscover\.xml$", "/autodiscover/autodiscover.xml", null),

        // Thunderbird/Apple Mail: alan adının kendi kökündeki .well-known yolu.
        ("Autoconfig", @"^\.well-known/autoconfig/mail/config-v1\.1\.xml$", "/.well-known/autoconfig/mail/config-v1.1.xml", null),

        // Thunderbird'ün klasik yolu YALNIZ "autoconfig.<alan>" ana adında açılır. Koşulsuz
        // bırakılsaydı, müşterinin kendi "/mail/config-v1.1.xml" dosyası ele geçirilirdi.
        ("AutoconfigHost", @"^mail/config-v1\.1\.xml$", "/mail/config-v1.1.xml", @"^autoconfig\.")
    ];

    /// <summary>Yayınlanan kural sayısı — durum ölçümü "üçü de yerinde mi" diye sorar.</summary>
    public static int ExpectedRuleCount => Kurallar.Length;

    /// <summary>İstemcilerin çağıracağı adresler (elle doğrulama ve kullanıcıya gösterim için).</summary>
    public static IReadOnlyList<string> ProbeUrls(string domain) =>
    [
        $"https://{domain}/autodiscover/autodiscover.xml",
        $"https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml",
        $"https://autoconfig.{domain}/mail/config-v1.1.xml"
    ];

    /// <summary>
    /// Kuralları belgeye (yeniden) yazar. İdempotenttir: önce panelin kendi kuralları temizlenir,
    /// kullanıcının kurallarına dokunulmaz.
    /// </summary>
    /// <param name="document">Sitenin <c>web.config</c> belgesi.</param>
    /// <param name="domain">Yayınlanan posta alan adı — panele AÇIKÇA bildirilir (aşağıya bkz.).</param>
    /// <param name="backendUrl">Panelin yerel adresi, ör. <c>https://localhost:8890</c>.</param>
    public static void Apply(XDocument document, string domain, string backendUrl)
    {
        Remove(document);

        var rules = EnsureRulesElement(document);
        var yeni = new List<object>();

        foreach (var (name, match, path, hostCondition) in Kurallar)
        {
            var kural = new XElement(
                "rule",
                new XAttribute("name", RuleNamePrefix + name),
                new XAttribute("stopProcessing", "true"),
                new XElement(
                    "match",
                    new XAttribute("url", match),
                    new XAttribute("ignoreCase", "true")));

            if (hostCondition is not null)
            {
                kural.Add(new XElement(
                    "conditions",
                    new XElement(
                        "add",
                        new XAttribute("input", "{HTTP_HOST}"),
                        new XAttribute("pattern", hostCondition))));
            }

            kural.Add(new XElement(
                "action",
                new XAttribute("type", "Rewrite"),
                // ALAN ADI AÇIKÇA YAZILIR. ARR, arka uca giderken Host başlığını varsayılan olarak
                // hedefin adıyla (localhost) değiştirir; panel ise sorulan alan adını istek ana
                // adından çözüyor. Açık parametre olmasaydı panel "localhost" görür, onu bir posta
                // alanı olarak tanımaz ve HER istek 404 dönerdi — yani kural yazılır ama özellik
                // yine ölü olurdu. (Sunucu değişkeni/preserveHostHeader ile Host'u korumak da
                // mümkündü; o, SUNUCU GENELİ bir ARR ayarını değiştirmek demek — mevcut kullanıcı
                // vekillerinin davranışını da değiştirirdi. Kural başına açık parametre daha dar.)
                new XAttribute("url", $"{backendUrl.TrimEnd('/')}{path}?domain={domain}"),
                new XAttribute("appendQueryString", "true")));

            yeni.Add(kural);
        }

        // EN BAŞA. WordPress'in "dosya değilse index.php'ye yaz" kuralı bu yolları yakalar ve
        // stopProcessing ile bitirir; sona eklenen kural WordPress kurulu sitelerde hiç çalışmazdı.
        rules.AddFirst([.. yeni]);
    }

    /// <summary>Panelin kendi kurallarını kaldırır; kaldırılan kural sayısını döner.</summary>
    public static int Remove(XDocument document)
    {
        var rules = GetRewriteElement(document)?.Element("rules");
        if (rules is null)
        {
            return 0;
        }

        var hedef = rules.Elements("rule").Where(IsOwnRule).ToList();
        hedef.Remove();
        return hedef.Count;
    }

    /// <summary>Belgede panele ait kaç kural var (durum ölçümü).</summary>
    public static int Count(XDocument document)
        => GetRewriteElement(document)?.Element("rules")?.Elements("rule").Count(IsOwnRule) ?? 0;

    /// <summary>
    /// Kural yerinde ama YANLIŞ arka uca ya da YANLIŞ alan adına bakıyor mu?
    /// </summary>
    /// <remarks>
    /// Panelin portu değiştiğinde eski kural sessizce 502 üretirdi ve "üç kural da var" ölçümü
    /// bunu yeşil gösterirdi. Hedef adresin de doğrulanması şart.
    /// </remarks>
    public static bool TargetsBackend(XDocument document, string domain, string backendUrl)
    {
        var rules = GetRewriteElement(document)?.Element("rules");
        if (rules is null)
        {
            return false;
        }

        var beklenenOnek = backendUrl.TrimEnd('/');
        var beklenenSonek = $"?domain={domain}";
        var kendi = rules.Elements("rule").Where(IsOwnRule).ToList();

        return kendi.Count == ExpectedRuleCount
            && kendi.All(rule =>
            {
                var url = (string?)rule.Element("action")?.Attribute("url") ?? string.Empty;
                return url.StartsWith(beklenenOnek, StringComparison.OrdinalIgnoreCase)
                    && url.EndsWith(beklenenSonek, StringComparison.OrdinalIgnoreCase);
            });
    }

    private static bool IsOwnRule(XElement rule)
        => ((string?)rule.Attribute("name"))?.StartsWith(RuleNamePrefix, StringComparison.OrdinalIgnoreCase) == true;

    private static XElement? GetRewriteElement(XDocument document)
        => document.Element("configuration")?.Element("system.webServer")?.Element("rewrite");

    private static XElement EnsureRulesElement(XDocument document)
    {
        var configuration = document.Element("configuration");
        if (configuration is null)
        {
            configuration = new XElement("configuration");
            document.Add(configuration);
        }

        return EnsureChild(EnsureChild(EnsureChild(configuration, "system.webServer"), "rewrite"), "rules");
    }

    private static XElement EnsureChild(XElement parent, string name)
    {
        var child = parent.Element(name);
        if (child is not null)
        {
            return child;
        }

        child = new XElement(name);
        parent.Add(child);
        return child;
    }
}
