namespace AdaPanel.Infrastructure.Mail;

/// <summary>
/// KİRACIDAN KİRACIYA POSTA SAHTECİLİĞİNİ kapatan kuralın TEK kaynağı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kapatılan açık (yalıtılmış bir Stalwart kopyasında GERÇEK SMTP oturumlarıyla ölçüldü):</b>
/// A müşterisi, kimlik doğrulaması OLMADAN <c>:25</c>'e bağlanıp <c>MAIL FROM</c>/<c>RCPT TO</c>
/// alanlarına B'nin alan adını yazabiliyordu ve sunucu <c>250 queued</c> diyordu. Kaynak IP
/// paylaşılan sunucu IP'si olduğunda (aynı makinedeki her kiracının PHP'si tam da oradan çıkar)
/// alıcının SPF'i <c>pass</c>, DMARC <c>pass</c> veriyor, ileti B'nin GELEN KUTUSUNA düşüyordu.
/// "Alıcının SPF'i yakalar" varsayımı paylaşımlı barındırmada GEÇERSİZDİR: panel her alan adına
/// <c>ip4:{sunucuIP}</c> yazdığı için o IP HER kiracının SPF'inde yetkilidir.
/// </para>
/// <para>
/// <b>Kök neden:</b> Stalwart'ın fabrika varsayılanı olan
/// <see cref="StalwartFactoryDefault"/> ifadesi, kimliksiz bir bağlantıya HERHANGİ BİR gönderen
/// alan adı bırakır — sunucunun KENDİ barındırdığı alan adları dahil.
/// </para>
/// <para>
/// <b>Uygulanan STANDART pratik:</b> Postfix'te
/// <c>smtpd_sender_restrictions = permit_sasl_authenticated, check_sender_access &lt;yerel alan adları&gt; → REJECT</c>,
/// Exim/cPanel'de <c>deny senders = +local_domains / !authenticated = *</c> olarak bilinen kural:
/// <b>kimliksiz bir bağlantı, sunucunun kendi yerel alan adlarından biri adına posta gönderemez.</b>
/// Postfix reçetesinin yaygın hâli listeye <c>permit_mynetworks</c>'ü de koyar (yani loopback'e
/// güvenir); paylaşımlı barındırmada açığın TAM OLARAK geldiği yer orası olduğu için burada
/// loopback ayrıcalığı YOKTUR — kiracının PHP'si de loopback'ten bağlanır.
/// </para>
/// <para>
/// <b>Neden bu yuva:</b> Stalwart'ın kendi şeması <c>MtaStageMail.isSenderAllowed</c> alanını
/// "gönderenin gönderme izni olduğunda true dönen ifade" diye tanımlar; MAIL FROM aşamasının
/// gönderen doğrulaması için ayrılmış yuva budur. Panel bugüne kadar yalnız
/// <c>MtaStageRcpt.script</c> ve <c>MtaStageData.script</c> yuvalarına yazıyordu.
/// </para>
/// <para>
/// <b><c>is_local_domain</c> uydurma değildir:</b> Stalwart'ın KENDİ varsayılanlarında geçer
/// (<c>SenderAuth.dkimSignDomain</c> = <c>is_local_domain(sender_domain) &amp;&amp; !is_empty(authenticated_as)</c>,
/// <c>MtaOutboundStrategy.route</c> = <c>is_local_domain(rcpt_domain)</c>) ve MAIL aşamasında
/// çalıştığı gerçek bir SMTP oturumuyla ölçülmüştür.
/// </para>
/// </remarks>
internal static class MailSenderPolicy
{
    /// <summary>
    /// Stalwart 0.16.8'in FABRİKA varsayılanı (şemadan ve çalışan sunucudan birebir okundu).
    /// </summary>
    /// <remarks>
    /// İki işi vardır: (1) kimliği doğrulanmış oturumlara her zaman izin ver, (2) kimliksiz
    /// oturumda gönderen alan adı <c>spam-block</c> listesindeyse reddet. Kuralımız bu iki işi
    /// AYNEN korur; yalnız üçüncü bir koşul EKLER.
    /// </remarks>
    internal const string StalwartFactoryDefault =
        "!is_empty(authenticated_as) || !key_exists('spam-block', sender_domain)";

    /// <summary>
    /// SIKI kural: kimliksiz oturum, sunucunun barındırdığı bir alan adı adına gönderemez.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fabrika varsayılanının üstüne TEK bir koşul biner: <c>!is_local_domain(sender_domain)</c>.
    /// Kimliği doğrulanmış taraf ifadenin ilk kolunda olduğu gibi durur, yani meşru kiracı
    /// gönderimi hiç etkilenmez.
    /// </para>
    /// <para>
    /// <b>Fazla kapatmayan noktalar (hepsi gerçek oturumla ölçüldü):</b> boş gönderen
    /// (<c>MAIL FROM:&lt;&gt;</c>, yani bounce/DSN) yerel alan adı SAYILMAZ ve geçer; başka bir
    /// alan adından gelen normal posta geçer; posta listesi genişletmesi ve otomatik yanıt
    /// (<c>vacation</c>) SMTP oturumu açmadan sunucunun içinde üretildiği için bu aşamadan hiç
    /// geçmez.
    /// </para>
    /// </remarks>
    internal const string Strict =
        "!is_empty(authenticated_as) || (!key_exists('spam-block', sender_domain) && !is_local_domain(sender_domain))";

    /// <summary>Yuvayla ne yapılacağı.</summary>
    internal enum SlotAction
    {
        /// <summary>İstenen değer zaten yazılı — dokunma (açılış/güncelleme idempotent olsun diye).</summary>
        None,

        /// <summary>Yuva boş ya da BİZİM bildiğimiz bir değer — yaz.</summary>
        Write,

        /// <summary>Yuvada sunucu işletmecisinin KENDİ ifadesi var — EZME, uyar.</summary>
        Foreign
    }

    /// <summary>İstenen ifadeyi üretir. Tek kaynak burasıdır; başka yerde string kurulmaz.</summary>
    /// <param name="allowUnauthenticatedLocalSenders">
    /// Sunucu işletmecisinin BİLEREK gevşetme anahtarı
    /// (<c>Panel:AllowUnauthenticatedLocalSenders</c>, varsayılan <c>false</c>).
    /// </param>
    internal static string Build(bool allowUnauthenticatedLocalSenders) =>
        allowUnauthenticatedLocalSenders ? StalwartFactoryDefault : Strict;

    /// <summary>
    /// Yuvanın mevcut değerine bakarak ne yapılacağına karar verir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden yabancı ifade EZİLMEZ:</b> bu ayar ÇALIŞAN bir posta sunucusunun kabul
    /// politikasıdır. İşletmeci buraya kendi kuralını yazmışsa (ör. bir iş ortağı ağına muafiyet),
    /// güncellemenin onu sessizce silmesi teslim edilemeyen posta üretirdi — ve teslim edilemeyen
    /// posta, açığın kendisinden daha pahalıdır. Aynı karar <c>MtaStageRcpt</c> yuvasında da
    /// yazılıdır (bkz. <c>MailService.DecideListPolicySlot</c>).
    /// </para>
    /// <para>
    /// <b>Neden fabrika varsayılanı "bizim bildiğimiz değer" sayılır:</b> mevcut kurulumların
    /// hepsinde yuvada TAM OLARAK o ifade vardır (panel bu alana hiç yazmadı). Onu yabancı
    /// saysaydık düzeltme hiçbir mevcut kuruluma ulaşmazdı.
    /// </para>
    /// <para>
    /// <b>Gevşetme anahtarı geri alınabilir olsun diye</b> <see cref="Strict"/> de bilinen değer
    /// listesindedir: işletmeci anahtarı açtığında panel fabrika varsayılanını GERİ yazar, yani
    /// açık kapı gerçekten kapıdır.
    /// </para>
    /// </remarks>
    internal static SlotAction Decide(string? current, string desired)
    {
        var value = (current ?? string.Empty).Trim();
        if (string.Equals(value, desired.Trim(), StringComparison.Ordinal))
        {
            return SlotAction.None;
        }

        var known = value.Length == 0
            || string.Equals(value, StalwartFactoryDefault, StringComparison.Ordinal)
            || string.Equals(value, Strict, StringComparison.Ordinal);

        return known ? SlotAction.Write : SlotAction.Foreign;
    }

    /// <summary>
    /// <c>stalwart-cli update MtaStageMail singleton --field &lt;bu&gt;</c> için alan değeri.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Biçim ÖLÇÜLDÜ (yalıtılmış Stalwart 0.16.8 + stalwart-cli 1.0.8): alan bir ifade NESNESİ
    /// bekler (<c>match</c> + <c>else</c>); düz string gönderilirse sunucu
    /// <c>validationFailed</c> döndürür. Değer burada üretiliyor ki testi sunucudan okunan
    /// biçimle karşılaştırabilsin.
    /// </para>
    /// <para>
    /// <b>GEVŞETİLMİŞ KODLAYICI ZORUNLU — ölçülmüş bir tuzak.</b> <c>JsonSerializer</c>'ın
    /// VARSAYILAN kodlayıcısı <c>'</c> karakterini <c>'</c>, <c>&amp;</c> karakterini
    /// <c>&</c> yapar. İfadede ikisi de var (<c>'spam-block'</c> ve <c>&amp;&amp;</c>), yani
    /// varsayılan kodlayıcı JSON'a TERS BÖLÜ sokar. Değer komut satırına
    /// <c>MailService.CliJsonArg</c> ile geçiyor ve o da MSVC kuralı gereği her ters bölüyü
    /// ikiler — sonuçta sunucuya <c>\\u0027</c> giderdi ve ifade bozulurdu. İfadede <c>"</c> ya da
    /// <c>\</c> hiç bulunmadığı için gevşetilmiş kodlayıcı çıktıyı TERS BÖLÜSÜZ üretir; tuzak
    /// böylece kapanır ve <c>CliField_NeverContainsABackslash</c> testi nöbette durur.
    /// </para>
    /// </remarks>
    internal static string BuildIsSenderAllowedField(string expression) =>
        "isSenderAllowed={\"match\":{},\"else\":"
        + System.Text.Json.JsonSerializer.Serialize(expression, RelaxedJson)
        + "}";

    private static readonly System.Text.Json.JsonSerializerOptions RelaxedJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // --- SENARYO 3: zarf doğru ama From BAŞLIĞI sahte ---

    /// <summary>
    /// Kimliği doğrulanmış gönderimde <c>From</c> BAŞLIĞININ, sunucuda barındırılan BAŞKA bir
    /// alan adını taklit etmesini engelleyen sieve bloğu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden gerekli:</b> <c>MtaStageAuth.mustMatchSender</c> yalnız ZARFI (MAIL FROM) kimliğe
    /// bağlar. Kullanıcı posta istemcisinde zarfı DEĞİL BAŞLIĞI görür. Ölçüldü: A kiracısı doğru
    /// zarfla bağlanıp <c>From: fatura@b-alanadi</c> yazdığında ileti B'nin GELEN KUTUSUNA
    /// düşüyordu — çünkü DMARC denetimi yalnız <c>local_port == 25</c> için açıktır, yani
    /// gönderim (submission) portundan gelip AYNI sunucuda teslim edilen ileti hiç denetlenmez.
    /// </para>
    /// <para>
    /// <b>Kural DAR tutuldu — fazla kapatmamak için:</b> yalnız (a) oturum kimlik doğrulamalıysa,
    /// (b) <c>From</c> başlığında bir alan adı varsa, (c) o alan adı zarf gönderenininkinden
    /// FARKLIYSA ve (d) o alan adı BU SUNUCUDA barındırılıyorsa reddedilir. Yani kiracının
    /// kendi Gmail/kurumsal adresiyle "farklı kimlikle gönder" yapması ETKİLENMEZ (yerel değil),
    /// dışarıdan gelen posta etkilenmez (kimliksiz), başlığı olmayan ileti etkilenmez.
    /// </para>
    /// <para>
    /// <b>BİLEREK KIRILAN tek akış:</b> İKİ alan adı barındıran bir kiracının, birinin kutusuyla
    /// oturum açıp DİĞERİNİN adına <c>From</c> yazması. Bu ret SESSİZ DEĞİLDİR: gönderim anında
    /// istemciye <see cref="FromHeaderRejectText"/> döner ve panelde
    /// <c>mail.senderPolicy.*</c> metinleri ne yapılacağını yazar (o alan adının kutusuyla
    /// oturum açın ya da adresi alias olarak ekleyin).
    /// </para>
    /// <para>
    /// <b>Ölçüm:</b> <c>env.authenticated_as</c> ve <c>is_local_domain()</c>'ın sieve
    /// <c>vnd.stalwart.expressions</c> içinde çalıştığı gerçek oturumla doğrulandı;
    /// <c>authenticated_as</c> (öneksiz) derlenmez.
    /// </para>
    /// </remarks>
    internal const string FromHeaderRejectText =
        "5.7.1 The From header domain is hosted on this server but is not yours. "
        + "Send from your own address, or add it as an alias to your mailbox.";

    /// <summary>Bloğun ihtiyaç duyduğu sieve uzantıları (birleşik <c>require</c> satırına girer).</summary>
    internal static readonly string[] FromHeaderExtensions = ["vnd.stalwart.expressions", "reject"];

    /// <summary>Sieve bloğunun gövdesi. Tek kaynak; <c>MailService.BuildDataStageScript</c> çağırır.</summary>
    internal static string BuildFromHeaderAlignmentBlock()
    {
        return
            "# AdaPanel: kimlikli gonderimde From basligi baska bir YEREL alan adini taklit edemez.\r\n"
            + "set \"adaHeaderDomain\" \"\";\r\n"
            + "set \"adaEnvelopeDomain\" \"\";\r\n"
            + "if address :domain :matches \"from\" \"*\" { set \"adaHeaderDomain\" \"${1}\"; }\r\n"
            + "if envelope :domain :matches \"from\" \"*\" { set \"adaEnvelopeDomain\" \"${1}\"; }\r\n"
            + "if eval \"!is_empty(env.authenticated_as) && !is_empty(adaHeaderDomain) "
            + "&& adaHeaderDomain != adaEnvelopeDomain && is_local_domain(adaHeaderDomain)\" {\r\n"
            + $"    reject \"{FromHeaderRejectText}\";\r\n"
            + "}\r\n";
    }
}
