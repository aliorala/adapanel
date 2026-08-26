using System.Text;
using System.Text.RegularExpressions;

namespace AdaPanel.Infrastructure.SiteBuilder;

/// <summary>Ziyaretçinin doldurduğu ham form alanları.</summary>
public sealed record SiteBuilderContactSubmission(
    string? Name,
    string? Email,
    string? Subject,
    string? Message,
    string? Honeypot);

/// <summary>Formun sonucu. Her değer ziyaretçiye AYRI bir cevap sayfası verir.</summary>
public enum SiteBuilderContactOutcome
{
    /// <summary>Posta gerçekten gönderildi.</summary>
    Sent,

    /// <summary>Bu site için form yok / kapalı / alıcı adresi geçersiz.</summary>
    NotAvailable,

    /// <summary>Alanlar eksik ya da biçimsiz.</summary>
    Invalid,

    /// <summary>Hız sınırı ya da gönderen bekleme süresi.</summary>
    RateLimited,

    /// <summary>Bal küpü doldurulmuş — posta GÖNDERİLMEDİ.</summary>
    Trapped,

    /// <summary>Gönderim denendi ve BAŞARISIZ oldu. Ziyaretçiye açıkça söylenir.</summary>
    SendFailed
}

/// <summary>
/// İletişim formunun SAF çekirdeği: doğrulama, kaçırma ve postanın metni.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden saf ve neden ayrı sınıf:</b> burada korunan şey, ziyaretçinin yazdığı metnin bir
/// E-POSTA İLETİSİNE girmesi. Bu, kurucudaki HTML kaçırmasının kardeşi olan ikinci bir enjeksiyon
/// yüzeyi: başlık satırına kaçan bir <c>\r\n</c>, iletiye kendi <c>Bcc:</c> satırını ekleyebilir
/// ve uç, tek bir POST ile yüzlerce adrese posta atan bir silaha dönüşür. Saf bir sınıf, bu
/// sınırların her birini tek satırlık testlerle ve MUTASYONLA doğrulanabilir kılar.
/// </para>
/// <para>
/// <b>Neden MailKit'e güvenilmiyor:</b> gönderim katmanı bugün başlıkları kendisi kodluyor
/// olabilir. Ama "kütüphane hallediyordur" bir ölçüm değil, bir varsayımdır ve gönderim katmanı
/// bu modülün sahipliğinde değil. Kaçırma, metnin ÜRETİLDİĞİ yerde yapılır.
/// </para>
/// </remarks>
public static partial class SiteBuilderContactForm
{
    public const int MaxNameLength = 120;
    public const int MaxEmailLength = 160;
    public const int MaxSubjectLength = 160;
    public const int MaxMessageLength = 4000;

    /// <summary>
    /// Bal küpü alanının adı. Adı "website": botların doldurmaya en meraklı olduğu alan adlarından.
    /// </summary>
    /// <remarks>
    /// Ad "honeypot"/"trap" gibi bir şey OLSAYDI, kaynağı okuyan bir bot alanı atlamayı öğrenirdi.
    /// Tuzağın işe yaraması, tuzak gibi görünmemesine bağlı.
    /// </remarks>
    public const string HoneypotField = "website";

    /// <summary>Doğrulanmış ve kaçırılmış gönderim.</summary>
    public sealed record Validated(string Name, string Email, string Subject, string Message);

    /// <summary>Doğrulama sonucu: ya geçerli bir gönderim ya da sebebiyle bir ret.</summary>
    public sealed record ValidationResult(SiteBuilderContactOutcome Outcome, Validated? Value)
    {
        public static ValidationResult Reject(SiteBuilderContactOutcome outcome) => new(outcome, null);
    }

    // Kurucunun sayfada kullandığı adres kalıbının AYNISI. İki ayrı kalıp olsaydı, site sahibinin
    // sayfada gösterilebilen bir adresi form alıcısı olarak reddedilebilir (ya da tersi) olurdu.
    private static readonly Regex EmailPattern = new(
        @"^[A-Za-z0-9._%+\-]{1,64}@[A-Za-z0-9\-]{1,63}(\.[A-Za-z0-9\-]{1,63})+$",
        RegexOptions.Compiled);

    /// <summary>Bir adres, form ALICISI ya da gönderen olarak kullanılabilir mi.</summary>
    public static bool IsUsableAddress(string? email)
    {
        var value = (email ?? string.Empty).Trim();
        return value.Length is > 0 and <= MaxEmailLength && EmailPattern.IsMatch(value);
    }

    /// <summary>
    /// Ham gönderimi doğrular ve kaçırır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Uzun metin KIRPILMAZ, REDDEDİLİR.</b> Kırpıp "gönderildi" demek, ziyaretçinin yazdığı
    /// mesajın yarısını sessizce atmak olurdu; ziyaretçi eksik gittiğini hiç öğrenemezdi. Sınırı
    /// aşan gönderim, sebebi söylenerek geri çevrilir.
    /// </para>
    /// <para>
    /// <b>Bal küpü DİĞER denetimlerden ÖNCE bakılır:</b> tuzağa düşen bir istekte alanların
    /// biçimini ölçmenin anlamı yok ve ölçmemek, bota "hangi alan yanlıştı" bilgisini de vermez.
    /// </para>
    /// </remarks>
    public static ValidationResult Validate(SiteBuilderContactSubmission submission)
    {
        if (!string.IsNullOrWhiteSpace(submission.Honeypot))
        {
            return ValidationResult.Reject(SiteBuilderContactOutcome.Trapped);
        }

        var name = (submission.Name ?? string.Empty).Trim();
        var email = (submission.Email ?? string.Empty).Trim();
        var subject = (submission.Subject ?? string.Empty).Trim();
        var message = NormalizeNewLines(submission.Message ?? string.Empty).Trim();

        if (name.Length == 0 || name.Length > MaxNameLength
            || subject.Length > MaxSubjectLength
            || message.Length == 0 || message.Length > MaxMessageLength
            || !IsUsableAddress(email))
        {
            return ValidationResult.Reject(SiteBuilderContactOutcome.Invalid);
        }

        return new ValidationResult(
            SiteBuilderContactOutcome.Sent,
            new Validated(HeaderSafe(name), email, HeaderSafe(subject), message));
    }

    /// <summary>
    /// BAŞLIK SATIRINA girecek metni tek satıra indirger.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Kapatılan saldırı:</b> RFC 5322'de başlıklar satır sonuyla ayrılır. Ziyaretçi konu
    /// alanına <c>Merhaba\r\nBcc: kurban@ornek.com</c> yazabilirse, ileti o adrese de gider —
    /// yani uç, tek bir POST'la istenen adrese posta atan bir SPAM SİLAHINA dönüşür. Kapatma,
    /// "satır sonu karakterini kaldır" kadar basit ve tam da bu yüzden atlanması kolay.
    /// </para>
    /// <para>
    /// <b>Neden yalnız <c>\r\n</c> değil:</b> tek başına <c>\r</c>, tek başına <c>\n</c>, dikey
    /// sekme, form besleme, Unicode <c>NEL</c> (U+0085) ve satır/paragraf ayırıcıları (U+2028/9)
    /// de bazı ayrıştırıcılarda satır sonu sayılır. Kural karakter karakter yazılmaz;
    /// <b>tanınmayan her denetim karakteri atılır</b> — kapalı bir izin listesi, açık bir
    /// yasak listesinden daima daha dayanıklıdır.
    /// </para>
    /// </remarks>
    public static string HeaderSafe(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            // U+2028/U+2029 denetim karakteri SAYILMAZ (ayirici sinifindadir) ama bazi
            // ayristiricilar satir sonu kabul eder; bu yuzden ayrica sayiliyorlar.
            if (char.IsControl(character) || character == (char)0x2028 || character == (char)0x2029)
            {
                // Denetim karakteri BOŞLUĞA çevrilir, silinmez: silinseydi "Merhaba\nBcc:" gibi bir
                // değer "MerhabaBcc:" olur ve iki ayrı kelime birleşip okunmaz hâle gelirdi.
                builder.Append(' ');
                continue;
            }

            builder.Append(character);
        }

        // Ardışık boşluklar tek boşluğa iner: 200 satır sonu içeren bir konu, aksi hâlde 200
        // boşluktan ibaret görünürdü.
        return WhitespaceRuns().Replace(builder.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRuns();

    private static string NormalizeNewLines(string value) =>
        value.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>
    /// İletinin KONU satırı: site adı + ziyaretçinin konusu.
    /// </summary>
    /// <remarks>
    /// Site adı önde, çünkü aynı posta kutusuna birden çok siteden form gelebilir ve alıcı hangi
    /// siteden geldiğini konu satırından görmelidir. Site adı da kaçırılır: panelden geliyor ama
    /// başlık kaçırmasının doğruluğu, değerin nereden geldiğini hatırlamaya bırakılamaz.
    /// </remarks>
    public static string BuildSubject(string siteName, Validated submission)
    {
        var site = HeaderSafe(siteName);

        // Konu boşsa uydurulmaz, ETİKETLENİR: alıcı posta kutusunda "konusuz" bir satır yerine
        // iletinin nereden geldiğini görür. Etiket iki dilde, çünkü alıcı sitenin dilini değil
        // kendi panelinin dilini kullanıyor olabilir.
        var subject = submission.Subject.Length > 0 ? submission.Subject : "İletişim formu / Contact form";
        var combined = site.Length > 0 ? $"[{site}] {subject}" : subject;

        // MIME başlığı katlanır ama sınırsız uzunluk yine de bazı sunucularda reddedilir.
        return combined.Length <= 250 ? combined : combined[..250];
    }

    /// <summary>
    /// İletinin DÜZ METİN gövdesi.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden düz metin:</b> HTML gövde, ziyaretçinin yazdığı metnin alıcının posta istemcisinde
    /// işaretleme olarak yorumlanması demektir — yani kurucudaki depolanmış XSS sorusunun aynısı,
    /// bu kez posta istemcisinde. Düz metinde böyle bir soru YOKTUR; gövdeyi ayrıca kaçırmak
    /// gerekmez ve "kaçırmayı unuttuk mu" endişesi ortadan kalkar.
    /// </para>
    /// <para>
    /// <b>Neden ziyaretçinin adresi GÖVDEDE, <c>Reply-To</c> başlığında değil:</b> ziyaretçinin
    /// yazdığı hiçbir değer başlık ALANINA konmuyor. Konu satırı zaten kaçırılıyor; adresi de
    /// başlığa koymak, kaçırmaya güvenen ikinci bir yüzey açardı. Alıcı adresi gövdeden okuyup
    /// yanıtlar — bir tık fazla, sıfır risk.
    /// </para>
    /// <para>
    /// <b>Neden "gönderen doğrulanmadı" satırı var:</b> ziyaretçi adresini kanıtlamıyor. Alıcının
    /// bu adrese güvenip iş yapması, kurucunun yaratacağı en sessiz zarardan biri olurdu.
    /// </para>
    /// </remarks>
    public static string BuildBody(string siteName, Validated submission, string? visitorIp)
    {
        var builder = new StringBuilder();
        builder.Append("Site: ").Append(HeaderSafe(siteName)).Append('\n');
        builder.Append("Gönderen / From: ").Append(submission.Name).Append('\n');
        builder.Append("E-posta / E-mail: ").Append(submission.Email).Append('\n');

        if (submission.Subject.Length > 0)
        {
            builder.Append("Konu / Subject: ").Append(submission.Subject).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(visitorIp))
        {
            builder.Append("IP: ").Append(HeaderSafe(visitorIp)).Append('\n');
        }

        builder.Append('\n');
        builder.Append("--- Mesaj / Message ---\n");
        builder.Append(submission.Message).Append('\n');
        builder.Append("--- Mesaj sonu / End of message ---\n\n");
        builder.Append(
            "Bu ileti AdaPanel site kurucunun iletişim formundan gönderildi. Gönderenin adresi "
            + "DOĞRULANMAMIŞTIR; yanıtlamadan önce dikkate alın.\n"
            + "Sent by the AdaPanel site builder contact form. The sender address is NOT verified.\n");

        return builder.ToString();
    }
}
