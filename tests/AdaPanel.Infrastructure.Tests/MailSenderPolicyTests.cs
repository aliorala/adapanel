using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Notifications;
using MailKit.Net.Smtp;
using MailKit.Security;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// KİRACIDAN KİRACIYA POSTA SAHTECİLİĞİ kuralının sözleşmesi.
/// </summary>
/// <remarks>
/// <para>
/// Kapatılan açık, yalıtılmış bir Stalwart 0.16.8 kopyasında GERÇEK SMTP oturumlarıyla ölçüldü:
/// kimlik doğrulaması olmayan bir bağlantı, sunucunun kendi barındırdığı bir alan adı adına
/// posta atabiliyor ve kaynak IP paylaşılan sunucu IP'si olduğunda SPF/DMARC <c>pass</c> vererek
/// kurbanın GELEN KUTUSUNA düşüyordu.
/// </para>
/// <para>
/// <b>Bu dosyadaki testlerin YARISI FAZLA KAPATMA nöbetçisidir.</b> Yalnız "sahtecilik
/// engellendi" ölçen bir küme, posta sunucusunu tümden kilitleyen bir düzeltmeyi de yeşil
/// gösterirdi — teslim edilemeyen posta, açığın kendisinden daha pahalıdır.
/// </para>
/// </remarks>
public sealed class MailSenderPolicyTests
{
    // --- Kuralın kendisi ---

    /// <summary>
    /// FAZLA KAPATMA NÖBETİ: kimliği doğrulanmış oturum ifadenin İLK kolunda durmalı.
    /// Bu koşul düşerse her kiracının kendi gönderimi kırılır.
    /// </summary>
    [Fact]
    public void Strict_StillAllowsEveryAuthenticatedSession()
    {
        Assert.StartsWith("!is_empty(authenticated_as) ||", MailSenderPolicy.Strict, StringComparison.Ordinal);
    }

    /// <summary>Asıl kapatma: kimliksiz oturum YEREL bir alan adı adına gönderemez.</summary>
    [Fact]
    public void Strict_RejectsLocalSenderDomainsFromUnauthenticatedSessions()
    {
        Assert.Contains("!is_local_domain(sender_domain)", MailSenderPolicy.Strict, StringComparison.Ordinal);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETİ: Stalwart'ın fabrika davranışı (spam-block listesi) KORUNMALI.
    /// Kural onu ezseydi, işletmecinin engellediği alan adları sessizce serbest kalırdı.
    /// </summary>
    [Fact]
    public void Strict_KeepsTheFactorySpamBlockCondition()
    {
        Assert.Contains("key_exists('spam-block', sender_domain)", MailSenderPolicy.Strict, StringComparison.Ordinal);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETİ: kural, fabrika varsayılanının üstüne TEK bir koşul ekler.
    /// Sıfırdan yazılmış bir ifade, ölçülmemiş bir davranış demek olurdu.
    /// </summary>
    [Fact]
    public void Strict_IsTheFactoryDefaultPlusExactlyOneCondition()
    {
        Assert.Equal(
            "!is_empty(authenticated_as) || (!key_exists('spam-block', sender_domain) && !is_local_domain(sender_domain))",
            MailSenderPolicy.Strict);
    }

    [Fact]
    public void Build_IsStrictByDefault()
    {
        Assert.Equal(MailSenderPolicy.Strict, MailSenderPolicy.Build(allowUnauthenticatedLocalSenders: false));
    }

    /// <summary>
    /// AÇIK KAPI GERÇEKTEN KAPI: işletmeci bayrağı açtığında panel Stalwart'ın FABRİKA
    /// varsayılanını geri yazar. Bayrak yalnız "yazmayı bırak" anlamına gelseydi, bir kez
    /// sıkılaştırılmış sunucu geri alınamazdı.
    /// </summary>
    [Fact]
    public void Build_RestoresTheFactoryDefault_WhenTheOperatorOpensTheDoor()
    {
        Assert.Equal(
            MailSenderPolicy.StalwartFactoryDefault,
            MailSenderPolicy.Build(allowUnauthenticatedLocalSenders: true));
    }

    // --- Yuva kararı: mevcut kurulumları BOZMADAN uygulama ---

    [Fact]
    public void Decide_DoesNothing_WhenTheRuleIsAlreadyInPlace()
    {
        Assert.Equal(
            MailSenderPolicy.SlotAction.None,
            MailSenderPolicy.Decide(MailSenderPolicy.Strict, MailSenderPolicy.Strict));
    }

    /// <summary>
    /// MEVCUT KURULUMLAR: yuvada Stalwart'ın fabrika varsayılanı vardır (panel bu alana hiç
    /// yazmadı). Fabrika varsayılanı "yabancı" sayılsaydı düzeltme HİÇBİR mevcut kuruluma ulaşmazdı.
    /// </summary>
    [Fact]
    public void Decide_Writes_WhenTheSlotHoldsTheStalwartFactoryDefault()
    {
        Assert.Equal(
            MailSenderPolicy.SlotAction.Write,
            MailSenderPolicy.Decide(MailSenderPolicy.StalwartFactoryDefault, MailSenderPolicy.Strict));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Decide_Writes_WhenTheSlotIsEmpty(string? current)
    {
        Assert.Equal(MailSenderPolicy.SlotAction.Write, MailSenderPolicy.Decide(current, MailSenderPolicy.Strict));
    }

    /// <summary>Açık kapı iki yönde de çalışmalı: sıkı ifadenin üstüne fabrika varsayılanı yazılabilir.</summary>
    [Fact]
    public void Decide_Writes_WhenTheDoorIsOpenedOnAnAlreadyHardenedServer()
    {
        Assert.Equal(
            MailSenderPolicy.SlotAction.Write,
            MailSenderPolicy.Decide(MailSenderPolicy.Strict, MailSenderPolicy.StalwartFactoryDefault));
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETİ — bu dosyadaki en pahalı test.
    /// İşletmeci yuvaya KENDİ kabul politikasını yazmışsa (iş ortağı ağına muafiyet, özel bir
    /// engelleme listesi) güncelleme onu EZEMEZ. Ezseydi, çalışan bir posta sunucusunun kabul
    /// politikası sessizce değişir ve meşru posta reddedilmeye başlardı.
    /// </summary>
    [Theory]
    [InlineData("!is_empty(authenticated_as) || key_exists('partners', remote_ip)")]
    [InlineData("true")]
    [InlineData("local_port != 25")]
    public void Decide_NeverOverwrites_AnOperatorsOwnExpression(string current)
    {
        Assert.Equal(
            MailSenderPolicy.SlotAction.Foreign,
            MailSenderPolicy.Decide(current, MailSenderPolicy.Strict));
    }

    /// <summary>Sunucu ifadeyi boşluklu döndürürse gereksiz yazma turu üretilmemeli (idempotentlik).</summary>
    [Fact]
    public void Decide_IgnoresSurroundingWhitespace()
    {
        Assert.Equal(
            MailSenderPolicy.SlotAction.None,
            MailSenderPolicy.Decide("  " + MailSenderPolicy.Strict + "  ", MailSenderPolicy.Strict));
    }

    /// <summary>
    /// Sunucuya giden alan değeri, YALITILMIŞ Stalwart'ta çalıştığı ÖLÇÜLEN biçimin AYNISI
    /// olmalı. Düz string gönderilirse (ifade nesnesi yerine) güncelleme sessizce geçmez ve
    /// panel "uyguladım" der.
    /// </summary>
    [Fact]
    public void CliField_MatchesTheShapeMeasuredAgainstARealServer()
    {
        Assert.Equal(
            "isSenderAllowed={\"match\":{},\"else\":\"" + MailSenderPolicy.Strict + "\"}",
            MailSenderPolicy.BuildIsSenderAllowedField(MailSenderPolicy.Strict));
    }

    /// <summary>
    /// ÖLÇÜLMÜŞ TUZAK: değer komut satırına <c>CliJsonArg</c> ile geçiyor ve o, MSVC kuralı
    /// gereği her ters bölüyü İKİLER. <c>JsonSerializer</c>'ın varsayılan kodlayıcısı ifadedeki
    /// <c>'</c> ve <c>&amp;</c> karakterlerini <c>'</c>/<c>&</c> yapıyordu; ikilenince
    /// sunucuya <c>\\u0027</c> gidiyor ve güncelleme <c>validationFailed</c> ile düşüyordu
    /// (yalıtılmış sunucuda görüldü). Tek ters bölü bile çıkarsa bu test kırmızı yanmalı.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CliField_NeverContainsABackslash(bool allowUnauthenticatedLocalSenders)
    {
        var field = MailSenderPolicy.BuildIsSenderAllowedField(
            MailSenderPolicy.Build(allowUnauthenticatedLocalSenders));

        Assert.DoesNotContain('\\', field);
    }

    // --- Senaryo 3: From BAŞLIĞI hizalaması ---

    /// <summary>
    /// ÖLÇÜLDÜ: sieve ifadesi içinde <c>authenticated_as</c> (öneksiz) DERLENMİYOR
    /// ("Invalid variable or function name"), <c>env.authenticated_as</c> derleniyor. Bu test
    /// öneki koruyor: düşerse script derlenmez ve DATA aşaması komple ölür.
    /// </summary>
    [Fact]
    public void FromHeaderBlock_UsesThePrefixedSessionVariable()
    {
        var block = MailSenderPolicy.BuildFromHeaderAlignmentBlock();

        Assert.Contains("env.authenticated_as", block, StringComparison.Ordinal);
    }

    /// <summary>Kural YALNIZ kimliği doğrulanmış oturumda çalışır — dışarıdan gelen posta etkilenmez.</summary>
    [Fact]
    public void FromHeaderBlock_OnlyAppliesToAuthenticatedSessions()
    {
        Assert.Contains("!is_empty(env.authenticated_as)", MailSenderPolicy.BuildFromHeaderAlignmentBlock(), StringComparison.Ordinal);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETİ: kural yalnız BU SUNUCUDA barındırılan alan adları için çalışır.
    /// <c>is_local_domain</c> koşulu düşerse, kiracının kendi Gmail/kurumsal adresiyle
    /// "farklı kimlikle gönder" yapması da kırılırdı.
    /// </summary>
    [Fact]
    public void FromHeaderBlock_OnlyProtectsDomainsHostedHere()
    {
        Assert.Contains("is_local_domain(adaHeaderDomain)", MailSenderPolicy.BuildFromHeaderAlignmentBlock(), StringComparison.Ordinal);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETİ: <c>From</c> başlığı ZARFLA aynı alan adındaysa hiçbir şey olmaz.
    /// Karşılaştırma düşerse kiracının KENDİ adına gönderimi de reddedilirdi.
    /// </summary>
    [Fact]
    public void FromHeaderBlock_AllowsAFromHeaderThatMatchesTheEnvelope()
    {
        Assert.Contains("adaHeaderDomain != adaEnvelopeDomain", MailSenderPolicy.BuildFromHeaderAlignmentBlock(), StringComparison.Ordinal);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETİ: <c>From</c> başlığı olmayan (ya da alan adı çıkarılamayan) ileti
    /// reddedilmemeli.
    /// </summary>
    [Fact]
    public void FromHeaderBlock_IgnoresMessagesWithoutAFromDomain()
    {
        Assert.Contains("!is_empty(adaHeaderDomain)", MailSenderPolicy.BuildFromHeaderAlignmentBlock(), StringComparison.Ordinal);
    }

    /// <summary>
    /// KIRDIĞIMIZ AKIŞ SESSİZ ÖLMEZ: ret metni kullanıcıya NE YAPACAĞINI söylemeli.
    /// (İki alan adı barındıran kiracı, diğerinin adına From yazarsa burada durur.)
    /// </summary>
    [Fact]
    public void FromHeaderRejectText_TellsTheUserWhatToDo()
    {
        Assert.Contains("5.7.1", MailSenderPolicy.FromHeaderRejectText, StringComparison.Ordinal);
        Assert.Contains("your own address", MailSenderPolicy.FromHeaderRejectText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alias", MailSenderPolicy.FromHeaderRejectText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ÖLÇÜLDÜ: <c>eval</c> ve <c>reject</c> için bu iki uzantı <c>require</c> satırında OLMAK
    /// ZORUNDA; yoksa sunucu script'i derlemez ve tek DATA yuvası boş kalır.
    /// </summary>
    [Fact]
    public void FromHeaderBlock_DeclaresTheExtensionsItNeeds()
    {
        Assert.Contains("vnd.stalwart.expressions", MailSenderPolicy.FromHeaderExtensions);
        Assert.Contains("reject", MailSenderPolicy.FromHeaderExtensions);
    }

    // --- DATA aşaması script'iyle birleşme ---

    private static Dictionary<string, MailService.AutoResponderEntry> NoResponders() => new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, MailService.AutoResponderEntry> OneResponder() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["info@ornek.com"] = new() { Enabled = true, Subject = "Izindeyim", Message = "Donunce yazarim" }
        };

    private static IReadOnlyList<MailService.ListUnsubscribeRule> OneList() =>
        [new("duyuru@ornek.com", "https://panel.ornek.com/api/v1/public/mail-lists/unsubscribe?list=duyuru%40ornek.com")];

    /// <summary>
    /// Kural açıkken, kullanıcının HİÇBİR özelliği açık olmasa bile gerçek bir script üretilir.
    /// Aksi hâlde koruma yalnız otomatik yanıt/liste kullanan kurulumlarda var olurdu.
    /// </summary>
    [Fact]
    public void DataStageScript_IsNotEmpty_WhenOnlyTheAntiSpoofingRuleIsActive()
    {
        var script = MailService.BuildDataStageScript(NoResponders(), [], blockCrossTenantFromHeader: true);

        Assert.Contains("is_local_domain(adaHeaderDomain)", script, StringComparison.Ordinal);
        Assert.Contains("reject", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// MEVCUT KURULUM REGRESYON NÖBETİ: bayrak kapalıyken çıktı, bu değişiklikten ÖNCEKİYLE
    /// bayt bayt aynı olmalı. Bayrak kapalı yol, işletmecinin açık kapıyı kullandığı yoldur.
    /// </summary>
    [Fact]
    public void DataStageScript_IsUnchanged_WhenTheRuleIsOff()
    {
        Assert.Equal(
            "# AdaPanel: DATA asamasinda kural yok / no DATA stage rules\r\n",
            MailService.BuildDataStageScript(NoResponders(), [], blockCrossTenantFromHeader: false));
    }

    /// <summary>
    /// <c>require</c> TEK satır ve EN ÜSTTE kalmalı — üç blok kendi require'ını yazsaydı script
    /// hiç derlenmez, DATA aşaması komple çalışmazdı.
    /// </summary>
    [Fact]
    public void DataStageScript_StillHasExactlyOneRequireLineAtTheTop()
    {
        var script = MailService.BuildDataStageScript(OneResponder(), OneList(), blockCrossTenantFromHeader: true);

        var lines = script.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("require [", lines[0], StringComparison.Ordinal);
        Assert.Single(lines, line => line.StartsWith("require [", StringComparison.Ordinal));
        Assert.Contains("\"vnd.stalwart.expressions\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"reject\"", lines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETİ (sieve dilbilgisi): liste özelliği kapalıyken <c>editheader</c>,
    /// otomatik yanıt kapalıyken <c>enotify</c> İSTENMEMELİ. Gereksiz uzantı istemek, o uzantıyı
    /// desteklemeyen bir yapılandırmada script'i tümden derlenemez yapar.
    /// </summary>
    [Fact]
    public void DataStageScript_DoesNotRequireExtensionsItDoesNotUse()
    {
        var script = MailService.BuildDataStageScript(NoResponders(), [], blockCrossTenantFromHeader: true);

        var firstLine = script.Split("\r\n")[0];
        Assert.DoesNotContain("editheader", firstLine, StringComparison.Ordinal);
        Assert.DoesNotContain("enotify", firstLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sahtecilik bloğu otomatik yanıt bloğundan ÖNCE gelmeli: reddedilecek bir iletiye otomatik
    /// yanıt üretilseydi, yanıt SAHTE From adresine giderdi.
    /// </summary>
    [Fact]
    public void DataStageScript_RejectsBeforeItAutoReplies()
    {
        var script = MailService.BuildDataStageScript(OneResponder(), OneList(), blockCrossTenantFromHeader: true);

        var reject = script.IndexOf("adaHeaderDomain", StringComparison.Ordinal);
        // "notify" DEĞİL "notify \"mailto:": require satırındaki `enotify` uzantı adı da
        // "notify" içerir ve naif arama her zaman require satırını bulurdu (ilk yazımda oldu).
        var notify = script.IndexOf("notify \"mailto:", StringComparison.Ordinal);
        Assert.True(reject >= 0 && notify >= 0);
        Assert.True(reject < notify, "Sahtecilik bloğu otomatik yanıttan önce çalışmalı.");
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETİ: yeni blok, paylaşılan yuvadaki DİĞER iki özelliği bozmamalı.
    /// </summary>
    [Fact]
    public void DataStageScript_KeepsTheOtherTwoFeaturesIntact()
    {
        var script = MailService.BuildDataStageScript(OneResponder(), OneList(), blockCrossTenantFromHeader: true);

        Assert.Contains("addheader \"List-Unsubscribe\"", script, StringComparison.Ordinal);
        Assert.Contains("addheader \"List-Id\"", script, StringComparison.Ordinal);
        Assert.Contains("notify", script, StringComparison.Ordinal);
        Assert.Contains("not exists [\"list-id\", \"list-unsubscribe\"]", script, StringComparison.Ordinal);
    }

    // --- Panelin KENDİ gönderimi sessizce ölmesin ---

    /// <summary>
    /// Panelin SMTP'si "localhost:25, kimlik yok, gönderen kendi alan adımız" diye ayarlanmışsa
    /// sunucu artık gönderen adresini reddeder. Parola sıfırlama akışı hatayı bilerek yuttuğu
    /// için, bu red ANLAMLI bir anahtara çevrilmezse arıza hiçbir yerde görünmez.
    /// </summary>
    [Fact]
    public void SenderRejection_IsRecognised_FromTheMailKitErrorCode()
    {
        var exception = new SmtpCommandException(
            SmtpErrorCode.SenderNotAccepted,
            SmtpStatusCode.MailboxNameNotAllowed,
            "5.7.1 Sender address not allowed.");

        Assert.True(NotificationService.IsSenderRejected(exception));
    }

    /// <summary>Sunucu reddi başka bir aşamada verirse gelişmiş durum kodundan yakalanır.</summary>
    [Fact]
    public void SenderRejection_IsRecognised_FromTheEnhancedStatusCode()
    {
        var exception = new SmtpCommandException(
            SmtpErrorCode.MessageNotAccepted,
            SmtpStatusCode.MailboxNameNotAllowed,
            "5.7.1 Sender address not allowed.");

        Assert.True(NotificationService.IsSenderRejected(exception));
    }

    /// <summary>
    /// FAZLA KAPATMA NÖBETİ: ALAKASIZ bir SMTP hatası gönderen reddi sayılmamalı — sayılsaydı
    /// panel kullanıcıya yanlış çözüm önerir, gerçek sebep (kota, alıcı yok) gizlenirdi.
    /// </summary>
    [Fact]
    public void SenderRejection_DoesNotSwallowUnrelatedSmtpFailures()
    {
        var exception = new SmtpCommandException(
            SmtpErrorCode.RecipientNotAccepted,
            SmtpStatusCode.MailboxUnavailable,
            "5.1.1 Recipient not found.");

        Assert.False(NotificationService.IsSenderRejected(exception));
    }
}
