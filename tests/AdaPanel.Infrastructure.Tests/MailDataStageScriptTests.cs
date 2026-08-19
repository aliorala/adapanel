using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// DATA aşamasının TEK sieve yuvasını iki özellik paylaşıyor: otomatik yanıt ve dağıtım listesi
/// <c>List-Unsubscribe</c> başlıkları.
/// </summary>
/// <remarks>
/// <para>
/// <c>MtaStageData.script</c> tek bir yuvadır; ikinci bir script üretilseydi yalnız sonuncusu
/// bağlanır, diğeri sunucuda ölü dururdu. Bu yüzden ikisi aynı script'e yazılır — ve bu testlerin
/// asıl işi, PAYLAŞIMIN otomatik yanıtı BOZMADIĞINI her kombinasyonda göstermek.
/// </para>
/// <para>
/// Koşulun <c>To:</c> başlığı olması bir ÖLÇÜMÜN sonucu: DATA aşamasında <c>${envelope.to}</c>
/// zaten genişlemiş geliyor (liste adresi kaybolup yerine abonelerin birleşimi geçiyor:
/// <c>ali@ornek.comveli@ornek.com</c>), bu yüzden <c>envelope :is "to"</c> hiç eşleşmiyordu.
/// </para>
/// </remarks>
public sealed class MailDataStageScriptTests
{
    private static Dictionary<string, MailService.AutoResponderEntry> Responders(
        params (string Address, bool Enabled, string Message)[] entries)
    {
        var store = new Dictionary<string, MailService.AutoResponderEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (address, enabled, message) in entries)
        {
            store[address] = new MailService.AutoResponderEntry
            {
                Enabled = enabled,
                Subject = "Ofiste degilim",
                Message = message
            };
        }

        return store;
    }

    private static IReadOnlyList<MailService.ListUnsubscribeRule> Lists(params string[] addresses) =>
        [.. addresses.Select(address => new MailService.ListUnsubscribeRule(
            address,
            $"https://panel.ornek.com/api/v1/public/mail-lists/unsubscribe?list={address}"))];

    // --- Dört kombinasyon: ikisi de kapalı / yalnız biri / ikisi birden ---

    /// <summary>
    /// İkisi de yokken GEÇERLİ ama etkisiz bir script üretilmeli: boş içerik derlenmeyebilir ve
    /// bağlama bozulurdu (otomatik yanıtta kanıtlanan yol).
    /// </summary>
    [Fact]
    public void Script_IsCommentOnly_WhenNeitherFeatureIsActive()
    {
        var script = MailService.BuildDataStageScript(Responders(), Lists());

        Assert.StartsWith("#", script);
        Assert.NotEmpty(script.Trim());
        Assert.DoesNotContain("addheader", script, StringComparison.Ordinal);
        Assert.DoesNotContain("notify", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// YALNIZ otomatik yanıt açıkken script, liste paylaşımından ÖNCEKİ hâliyle aynı işi yapmalı.
    /// Bu, paylaşımın otomatik yanıtı bozmadığının ilk kanıtı.
    /// </summary>
    [Fact]
    public void Script_KeepsTheAutoResponderIntact_WhenThereAreNoLists()
    {
        var script = MailService.BuildDataStageScript(
            Responders(("info@ornek.com", true, "Yillik izindeyim")),
            Lists());

        Assert.Contains("notify", script, StringComparison.Ordinal);
        Assert.Contains("enotify", script, StringComparison.Ordinal);
        // RFC 3834 korumaları yerinde.
        Assert.Contains("not exists [\"list-id\", \"list-unsubscribe\"]", script, StringComparison.Ordinal);
        Assert.Contains("not header :is \"precedence\"", script, StringComparison.Ordinal);
        // Liste özelliği kapalıyken editheader gereksiz yere istenmemeli.
        Assert.DoesNotContain("addheader", script, StringComparison.Ordinal);
        Assert.DoesNotContain("editheader", script, StringComparison.Ordinal);
    }

    /// <summary>YALNIZ liste varken otomatik yanıt bloğu HİÇ yazılmamalı.</summary>
    [Fact]
    public void Script_OnlyAddsHeaders_WhenThereAreNoAutoResponders()
    {
        var script = MailService.BuildDataStageScript(Responders(), Lists("duyuru@ornek.com"));

        Assert.Contains("addheader \"List-Unsubscribe\"", script, StringComparison.Ordinal);
        Assert.Contains("editheader", script, StringComparison.Ordinal);
        Assert.DoesNotContain("notify", script, StringComparison.Ordinal);
        Assert.DoesNotContain("enotify", script, StringComparison.Ordinal);
    }

    /// <summary>İKİSİ BİRDEN açıkken her iki özellik de script'te bulunmalı.</summary>
    [Fact]
    public void Script_ContainsBothFeatures_WhenBothAreActive()
    {
        var script = MailService.BuildDataStageScript(
            Responders(("info@ornek.com", true, "Yillik izindeyim")),
            Lists("duyuru@ornek.com"));

        Assert.Contains("addheader \"List-Unsubscribe\"", script, StringComparison.Ordinal);
        Assert.Contains("notify", script, StringComparison.Ordinal);
    }

    // --- Sieve dilbilgisi: require TEK ve EN ÜSTTE ---

    /// <summary>
    /// <c>require</c> yalnız bir kez ve script'in EN BAŞINDA olmalı; sieve başka yerde kabul etmez.
    /// İki blok kendi require'ını yazsaydı script hiç derlenmez, DATA aşaması komple çalışmazdı.
    /// </summary>
    [Fact]
    public void Script_HasExactlyOneRequireLine_AtTheTop()
    {
        var script = MailService.BuildDataStageScript(
            Responders(("info@ornek.com", true, "mesaj")),
            Lists("duyuru@ornek.com"));

        var lines = script.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("require [", lines[0]);
        Assert.Equal(1, lines.Count(line => line.TrimStart().StartsWith("require", StringComparison.Ordinal)));
    }

    /// <summary>Kullanılan her uzantı require edilmeli; eksik require sieve'de derleme hatasıdır.</summary>
    [Fact]
    public void Script_RequiresEveryExtensionItUses()
    {
        var script = MailService.BuildDataStageScript(
            Responders(("info@ornek.com", true, "mesaj")),
            Lists("duyuru@ornek.com"));

        var requireLine = script.Split("\r\n")[0];

        Assert.Contains("\"editheader\"", requireLine, StringComparison.Ordinal);
        Assert.Contains("\"enotify\"", requireLine, StringComparison.Ordinal);
        Assert.Contains("\"envelope\"", requireLine, StringComparison.Ordinal);
        Assert.Contains("\"variables\"", requireLine, StringComparison.Ordinal);
    }

    // --- Koşul ve sıra ---

    /// <summary>
    /// Koşul <c>To:</c> BAŞLIĞINA bakmalı. <c>envelope</c> DATA aşamasında zaten genişlemiş
    /// olduğundan (ölçüldü) liste adresi orada YOKTUR ve kural hiç eşleşmezdi.
    /// </summary>
    [Fact]
    public void ListRule_MatchesOnTheToHeader_NotTheEnvelope()
    {
        var script = MailService.BuildDataStageScript(Responders(), Lists("duyuru@ornek.com"));

        Assert.Contains("header :contains :comparator \"i;ascii-casemap\" \"to\" \"duyuru@ornek.com\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("envelope :is", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// Liste başlıkları otomatik yanıttan ÖNCE eklenmeli. Otomatik yanıt bloğu
    /// <c>not exists ["list-id","list-unsubscribe"]</c> ile korunduğu için, başlıklar önce
    /// eklendiğinde listeye gelen iletiye otomatik yanıt ÜRETİLMEZ — postanın listeye geri dönüp
    /// döngü yapmasını engelleyen doğru davranış budur (RFC 3834).
    /// </summary>
    [Fact]
    public void ListHeaders_AreAddedBeforeTheAutoResponderBlock()
    {
        var script = MailService.BuildDataStageScript(
            Responders(("info@ornek.com", true, "mesaj")),
            Lists("duyuru@ornek.com"));

        var addheader = script.IndexOf("addheader \"List-Unsubscribe\"", StringComparison.Ordinal);
        var autoReply = script.IndexOf("not exists [\"list-id\"", StringComparison.Ordinal);

        Assert.True(addheader >= 0 && autoReply >= 0);
        Assert.True(addheader < autoReply, "Liste basliklari otomatik yanittan ONCE eklenmeli.");
    }

    /// <summary>Başlık değeri RFC 2369 gereği açılı ayraç içinde olmalı.</summary>
    [Fact]
    public void UnsubscribeHeader_IsWrappedInAngleBrackets()
    {
        var script = MailService.BuildDataStageScript(Responders(), Lists("duyuru@ornek.com"));

        Assert.Contains("<https://panel.ornek.com/api/v1/public/mail-lists/unsubscribe?list=duyuru@ornek.com>", script, StringComparison.Ordinal);
    }

    /// <summary>Kapalı otomatik yanıt kayıtları script'e girmemeli.</summary>
    [Fact]
    public void DisabledAutoRespondersAreNotEmitted()
    {
        var script = MailService.BuildDataStageScript(
            Responders(("info@ornek.com", false, "mesaj")),
            Lists());

        Assert.DoesNotContain("notify", script, StringComparison.Ordinal);
    }

    [Fact]
    public void CountActiveAutoResponders_CountsOnlyEnabledOnesWithAMessage()
    {
        var store = Responders(
            ("a@ornek.com", true, "var"),
            ("b@ornek.com", false, "var"),
            ("c@ornek.com", true, ""));

        Assert.Equal(1, MailService.CountActiveAutoResponders(store));
    }

    // --- Panelin genel adresi: TAHMİN EDİLMEZ, doğrulanır ---

    /// <summary>Boş = özellik kapalı; bu meşru ve varsayılan durum.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NormalizePublicBaseUrl_TreatsEmptyAsDisabled(string? value)
    {
        Assert.Equal(string.Empty, MailService.NormalizePublicBaseUrl(value));
    }

    [Theory]
    [InlineData("https://panel.ornek.com", "https://panel.ornek.com")]
    [InlineData("https://panel.ornek.com/", "https://panel.ornek.com")]
    [InlineData("http://panel.ornek.com:8890", "http://panel.ornek.com:8890")]
    public void NormalizePublicBaseUrl_KeepsTheRootAndDropsTheTrailingSlash(string input, string expected)
    {
        Assert.Equal(expected, MailService.NormalizePublicBaseUrl(input));
    }

    /// <summary>
    /// Bozuk adres REDDEDİLİR. Kabul edilseydi, GERÇEK abonelere giden iletilere çalışmayan bir
    /// bağlantı olarak çıkardı ve bunu ancak abone tıklayınca fark ederdik.
    /// </summary>
    [Theory]
    [InlineData("panel.ornek.com")]
    [InlineData("ftp://panel.ornek.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://panel.ornek.com/altyol")]
    [InlineData("https://panel.ornek.com?x=1")]
    public void NormalizePublicBaseUrl_RejectsAnythingThatIsNotAnHttpRoot(string value)
    {
        var exception = Assert.Throws<AppException>(() => MailService.NormalizePublicBaseUrl(value));

        Assert.Equal("mail.public_base_url_invalid", exception.Message);
    }
}
