using System.Xml.Linq;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>
/// Stalwart'ın JMAP ucunu IIS üzerinden yayımlayan URL Rewrite (+ARR) kurallarının TEK kaynağı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden bu vekil VAR:</b> tek bir alan adının postasını site arşivine koyabilmek için
/// <c>vandelay</c> kullanılıyor ve vandelay, JMAP oturumunun <i>reklam ettiği</i> adresi kullanmak
/// ZORUNDA (ölçüldü: <c>--url</c>'e tam oturum adresi vermek bile bunu değiştirmiyor; araç
/// "session advertises apiUrl on https://… ; vandelay must use the advertised URL" deyip reklam
/// edilen adrese gidiyor). Stalwart ise reklam adresini HER ZAMAN <c>https://&lt;ad&gt;/jmap/</c>
/// olarak üretir — port EKLENEMEZ (<c>SystemSettings</c>'te porta izin veren alan yok; denendi,
/// "Invalid hostname" ile reddediliyor). AdaPanel de Stalwart'ın HTTP portunu (8080) BİLEREK
/// yalnız loopback'te tutuyor ve 443 dinleyicisini siliyor (443 IIS'e ayrılmış). Yani reklam
/// edilen adrese ancak IIS 443'ten vekillenerek ulaşılabilir. Bu sınıf o vekildir.
/// </para>
/// <para>
/// <b>VEKİL BİR GÜVENLİK YÜZEYİDİR — ne geçer, ne geçmez:</b>
/// </para>
/// <list type="bullet">
///   <item><description>
///   <b>GEÇER (yalnız ikisi):</b> <c>/.well-known/jmap</c> (oturum keşfi) ve <c>/jmap/…</c> (JMAP
///   API, yükleme/indirme uçları dahil). Başka HİÇBİR yol arka uca gitmez.
///   </description></item>
///   <item><description>
///   <b>AÇIK RED:</b> yukarıdaki iki yola gelen ama ham adresinde dizin çıkma (<c>..</c>) ya da
///   kodlanmış biçimleri (<c>%2e</c>, <c>%252e</c>, <c>%5c</c>, ters bölü) bulunan istekler 403 ile
///   KESİLİR — arka uca hiç ulaşmaz. Bu kural, vekilin TEK gerçek kaçış yoludur: <c>/jmap/</c>
///   önekiyle girip <c>../api/…</c> ile Stalwart'ın YÖNETİM API'sine çıkmak. Reddi ayrı ve EN BAŞTA
///   bir kural olarak yazmak bilinçlidir; "zaten eşleşmez" demek, eşleşme kalıbı ileride
///   genişletildiğinde sessizce açılan bir kapı bırakırdı.
///   </description></item>
///   <item><description>
///   <b>ARKA UCA HİÇ GİTMEZ:</b> Stalwart'ın yönetim API'si (<c>/api/…</c>), web yönetim arayüzü
///   ve diğer bütün yollar. Bunlar için vekil kuralı YOKTUR; eşleşmeyen istek IIS'te sitenin kendi
///   içeriğine düşer, 8080'e değil.
///   </description></item>
/// </list>
/// <para>
/// <b>Neden <c>^api/</c> için TOPTAN bir red kuralı YAZILMADI (bilinçli):</b> bu kurallar MÜŞTERİNİN
/// sitesinin <c>web.config</c>'ine giriyor. <c>^api/</c>'yi topyekûn reddetmek, müşterinin KENDİ
/// <c>/api/</c> uygulamasını öldürürdü — vekille hiç ilgisi olmayan bir yolu kapatmak, çözdüğünden
/// büyük bir arıza üretirdi. Stalwart'ın <c>/api/</c>'si zaten bu vekilden geçmiyor; ona giden TEK
/// olası yol <c>/jmap/</c> önekinden dizin çıkmaktır ve açık red kuralı tam olarak onu kesiyor.
/// </para>
/// <para>
/// <b>Vekil KİMLİK DOĞRULAMAZ.</b> JMAP'in kendi kimlik doğrulaması (HTTP Basic / Bearer) devrede
/// kalır; bu kurallar yalnızca isteği taşır. Yedek akışı yönetici kimliğiyle bağlanır ve hedef
/// hesabı <c>--account-name</c> ile seçer (Stalwart'ın <c>impersonate</c> izni).
/// </para>
/// <para>
/// Ayrı ve saf (I/O'suz) bir sınıf: kuralların İÇERİĞİ ve SIRASI, IIS olmadan test edilebilmeli.
/// <see cref="MailAutodiscoverRewriteRules"/> ile aynı kalıp — o kalıp bu depoda "kod var ama hiç
/// çalışmıyor" sınıfından arızaları yakalamak için kuruldu.
/// </para>
/// </remarks>
public static class MailJmapProxyRules
{
    /// <summary>Panelin kendi kurallarını tanıyıp güncelleyebilmesi için sabit ad öneki.</summary>
    public const string RuleNamePrefix = "AdaPanel.MailJmap.";

    /// <summary>
    /// Stalwart'ın JMAP/yönetim ucu. LOOPBACK: 8080 dışarıya kapalı kalır (ComponentBootstrapper
    /// bunu bilerek sabitliyor); dışarıdan erişim YALNIZ bu vekilin geçirdiği iki yoldan olur.
    /// </summary>
    public const string BackendUrl = "http://127.0.0.1:8080";

    /// <summary>
    /// Ham adreste arandığında isteği 403'e düşüren kaçış belirteçleri. Hem çıplak hem tek ve ÇİFT
    /// kodlanmış biçimler: IIS bir kez çözer, arka uç bir kez daha çözebilir — tek kodlamayı
    /// kesip çift kodlamayı bırakmak, kuralı hiç yazmamakla aynı kapıyı açık bırakırdı.
    /// </summary>
    internal const string TraversalPattern = @"(\.\.|%2e|%2E|%252e|%252E|%5c|%5C|\\)";

    /// <summary>Yayınlanan kural sayısı — durum ölçümü "hepsi yerinde mi" diye sorar.</summary>
    public static int ExpectedRuleCount => 3;

    /// <summary>
    /// Vandelay'in gideceği adres. Yedek ÖN KONTROLÜ tam olarak burayı yoklar; yoklanan adresle
    /// yedeğin kullanacağı adres AYNI olmalı, yoksa "yeşil ön kontrol + patlayan yedek" olurdu.
    /// </summary>
    public static string SessionUrl(string mailHostName) => $"https://{mailHostName}/.well-known/jmap";

    /// <summary>
    /// Kuralları belgeye (yeniden) yazar. İdempotenttir: önce panelin kendi kuralları temizlenir,
    /// kullanıcının kurallarına dokunulmaz.
    /// </summary>
    /// <param name="document">Sitenin <c>web.config</c> belgesi.</param>
    public static void Apply(XDocument document)
    {
        Remove(document);

        var rules = EnsureRulesElement(document);

        // SIRA HAYATİDİR ve şu üç kural TAM BU SIRAYLA yazılır:
        //   1) RED  — kaçış denemesi arka uca ULAŞMADAN kesilsin diye EN BAŞTA.
        //   2) .well-known/jmap — oturum keşfi (tam eşleşme).
        //   3) jmap/…            — JMAP API (önek eşleşmesi).
        // Red kuralı ikinci/üçüncü sıraya konsaydı, geçiren kural stopProcessing ile önce biter ve
        // red HİÇ ÇALIŞMAZDI — yani yazılmış ama ölü bir güvenlik kuralı olurdu.
        var yeni = new List<XElement>
        {
            DenyRule(),
            RewriteRule("WellKnown", @"^\.well-known/jmap$", "/.well-known/jmap"),
            RewriteRule("Api", @"^jmap/(.*)$", "/jmap/{R:1}")
        };

        // EN BAŞA. WordPress'in "dosya değilse index.php'ye yaz" kuralı bu yolları yakalar ve
        // stopProcessing ile bitirir; sona eklenen kural WordPress kurulu sitelerde hiç çalışmazdı.
        rules.AddFirst([.. yeni]);
    }

    /// <summary>
    /// AÇIK RED: JMAP önekine gelen ama ham adresinde dizin çıkma belirteci taşıyan istek 403.
    /// </summary>
    /// <remarks>
    /// Koşul {REQUEST_URI} üzerindedir, eşleşme kalıbı üzerinde DEĞİL: <c>match url</c> IIS'in
    /// ÇÖZÜLMÜŞ yolunu görür ve çözülme sırasında <c>%2e%2e</c> zaten <c>..</c>'ye dönüşmüş ya da
    /// normalize edilmiş olabilir. Ham adresi ölçmek, kodlanmış kaçışı da yakalayan tek yoldur.
    /// </remarks>
    private static XElement DenyRule()
        => new(
            "rule",
            new XAttribute("name", RuleNamePrefix + "Deny"),
            new XAttribute("stopProcessing", "true"),
            new XElement(
                "match",
                new XAttribute("url", @"^(jmap(/.*)?|\.well-known/jmap)$"),
                new XAttribute("ignoreCase", "true")),
            new XElement(
                "conditions",
                new XElement(
                    "add",
                    new XAttribute("input", "{REQUEST_URI}"),
                    new XAttribute("pattern", TraversalPattern))),
            new XElement(
                "action",
                // CustomResponse (AbortRequest değil): 403 GÖRÜNÜR bir cevaptır ve günlüğe düşer.
                // Bağlantıyı sessizce koparmak, saldırıyı da teşhisi de görünmez yapardı.
                new XAttribute("type", "CustomResponse"),
                new XAttribute("statusCode", "403"),
                new XAttribute("statusReason", "Forbidden"),
                new XAttribute("statusDescription", "JMAP proxy: path traversal is not allowed.")));

    private static XElement RewriteRule(string name, string match, string path)
        => new(
            "rule",
            new XAttribute("name", RuleNamePrefix + name),
            new XAttribute("stopProcessing", "true"),
            new XElement(
                "match",
                new XAttribute("url", match),
                new XAttribute("ignoreCase", "true")),
            new XElement(
                "action",
                new XAttribute("type", "Rewrite"),
                new XAttribute("url", BackendUrl + path),
                // JMAP indirme ucu sorgu dizesi kullanır (?accept=…); atmak indirmeyi bozardı.
                new XAttribute("appendQueryString", "true")));

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
    /// Kurallar YERİNDE ve DOĞRU mu: üçü de var mı, geçirenler loopback arka uca mı bakıyor, ve
    /// RED kuralı hâlâ EN BAŞTA mı?
    /// </summary>
    /// <remarks>
    /// Sırayı da ölçmek şart: kurallar elle düzenlenip red kuralı aşağı kaydırılırsa sayı yine üç
    /// çıkar ve "yeşil" görünürdü — ama kaçış koruması ölmüş olurdu.
    /// </remarks>
    public static bool IsHealthy(XDocument document)
    {
        var rules = GetRewriteElement(document)?.Element("rules");
        if (rules is null)
        {
            return false;
        }

        var kendi = rules.Elements("rule").Where(IsOwnRule).ToList();
        if (kendi.Count != ExpectedRuleCount)
        {
            return false;
        }

        if (!string.Equals((string?)kendi[0].Attribute("name"), RuleNamePrefix + "Deny", StringComparison.Ordinal))
        {
            return false;
        }

        return kendi.Skip(1).All(rule =>
            ((string?)rule.Element("action")?.Attribute("url") ?? string.Empty)
                .StartsWith(BackendUrl, StringComparison.OrdinalIgnoreCase));
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
