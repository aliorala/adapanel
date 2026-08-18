using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Dağıtım listesinin (Stalwart <c>MailingList</c>) panel ile sunucu arasındaki SÖZLEŞMESİ.
/// </summary>
/// <remarks>
/// <para>
/// Buradaki her beklenti CANLI SUNUCUDA ölçüldü (Stalwart 0.16.8 + stalwart-cli 1.0.8, yerel
/// yalıtılmış örnek). Özellik "yazılmıştı" ama üç ayrı noktada sunucunun gerçek şemasıyla
/// uyuşmadığı için baştan sona ölüydü ve bu, panelde hiçbir hata olarak görünmüyordu:
/// </para>
/// <list type="number">
/// <item>Nesne adı <c>mailing-list</c> gönderiliyordu → <c>unknown object: mailing-list</c>.</item>
/// <item>Aboneler DİZİ gönderiliyordu → <c>invalidPatch ... recipients</c>.</item>
/// <item>Aboneler DİZİ olarak okunuyordu → her liste "abonesiz" görünürdü.</item>
/// </list>
/// <para>
/// Testler bu üç noktayı ayrı ayrı çiviler: biri geri gelirse hangisinin kaydığı doğrudan
/// okunabilsin.
/// </para>
/// </remarks>
public sealed class MailingListContractTests
{
    private const string Domain = "ornek.com";

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // --- 1) Nesne adı ---

    /// <summary>
    /// Sunucu nesne adını tireli biçimde TANIMIYOR. Sabit hem oluşturma hem sorgulama hem silme
    /// yolunda kullanıldığı için tek bir doğrulama bütün yolları korur.
    /// </summary>
    [Fact]
    public void MailingListObject_IsTheSchemaName_NotTheHyphenatedDocumentationName()
    {
        Assert.Equal("MailingList", MailService.MailingListObject);
        Assert.DoesNotContain('-', MailService.MailingListObject);
    }

    // --- 2) Yazma biçimi: KÜME (harita) ---

    /// <summary>
    /// <c>recipients</c> şemada <c>set&lt;emailAddress&gt;</c>: tel üzerinde harita. Dizi
    /// gönderildiğinde sunucu <c>invalidPatch</c> döndürüyor.
    /// </summary>
    [Fact]
    public void BuildRecipientsMapJson_EmitsASetMap_NotAnArray()
    {
        var json = MailService.BuildRecipientsMapJson(["ali@ornek.com", "veli@ornek.com"], Domain);

        Assert.StartsWith("{", json);
        Assert.EndsWith("}", json);
        Assert.DoesNotContain("[", json);

        // Sunucunun kabul ettiği tam biçim: her adres bir anahtar, değeri true.
        var parsed = Parse(json);
        Assert.Equal(JsonValueKind.Object, parsed.ValueKind);
        Assert.True(parsed.GetProperty("ali@ornek.com").GetBoolean());
        Assert.True(parsed.GetProperty("veli@ornek.com").GetBoolean());
    }

    [Fact]
    public void BuildRecipientsMapJson_EmitsAnEmptyMap_WhenThereAreNoSubscribers()
    {
        Assert.Equal("{}", MailService.BuildRecipientsMapJson([], Domain));
    }

    /// <summary>Yerel kısım ("ali") liste alan adıyla tamamlanır; sunucuya yarım adres gitmez.</summary>
    [Fact]
    public void NormalizeRecipients_CompletesLocalPartsWithTheListDomain()
    {
        Assert.Equal(["ali@ornek.com"], MailService.NormalizeRecipients(["ali"], Domain));
    }

    /// <summary>Dış aboneler (Plesk'te de serbest) olduğu gibi korunur.</summary>
    [Fact]
    public void NormalizeRecipients_KeepsExternalSubscribers()
    {
        Assert.Equal(["disarisi@gmail.com"], MailService.NormalizeRecipients(["disarisi@gmail.com"], Domain));
    }

    /// <summary>
    /// Büyük/küçük harf ve boşluk farkı AYNI aboneyi iki kez listeye sokmamalı: küme gösteriminde
    /// iki anahtar birbirini ezmez, kişi duyuruyu iki kez alır.
    /// </summary>
    [Fact]
    public void NormalizeRecipients_DeduplicatesIgnoringCaseAndWhitespace()
    {
        var result = MailService.NormalizeRecipients([" Ali@Ornek.com ", "ali@ornek.com", "ALI@ORNEK.COM"], Domain);

        Assert.Equal(["ali@ornek.com"], result);
    }

    [Fact]
    public void NormalizeRecipients_DropsBlankEntries()
    {
        Assert.Empty(MailService.NormalizeRecipients(["", "   ", "\t"], Domain));
    }

    /// <summary>
    /// Sunucu adres SÖZDİZİMİNİ doğrulamıyor (ölçüldü: <c>recipients</c> kümesine "@" bile
    /// içermeyen <c>bu-adres-degil</c> değeri itirazsız kaydediliyor). Doğrulama panelde
    /// yapılmazsa liste, kimseye ulaşmayan bir aboneyle sessizce yaşar; yönetici o kişinin
    /// duyuruları aldığını sanır.
    /// </summary>
    [Theory]
    [InlineData("@ornek.com")]
    [InlineData("ali@")]
    [InlineData("ali@@ornek.com")]
    [InlineData("ali@ornek")]
    [InlineData("ali veli@ornek.com")]
    [InlineData("ali@.ornek.com")]
    [InlineData("ali@ornek.com.")]
    public void NormalizeRecipients_RejectsAddressesTheServerWouldSilentlyAccept(string address)
    {
        var exception = Assert.Throws<AppException>(() => MailService.NormalizeRecipients([address], Domain));

        Assert.Equal("mail.invalid_recipient", exception.Message);
    }

    /// <summary>
    /// Sunucunun sessizce kabul ettiği ÇIPLAK belirteç ("@" yok) sunucuya HİÇ ulaşmaz: panelin her
    /// yerinde olduğu gibi yerel kısım sayılır ve listenin alan adıyla tamamlanır. Yani bu sınıf
    /// hata sunucuya bırakılmadan panelde kapanır.
    /// </summary>
    [Fact]
    public void NormalizeRecipients_NeverSendsABareTokenToTheServer()
    {
        Assert.Equal(["bu-adres-degil@ornek.com"], MailService.NormalizeRecipients(["bu-adres-degil"], Domain));
    }

    // --- 3) Okuma biçimi ---

    /// <summary>Sunucudan gelen gerçek gövde: <c>recipients</c> bir HARİTA.</summary>
    [Fact]
    public void ReadListRecipients_ReadsTheKeysOfTheSetMap()
    {
        var detail = Parse("""
            {"name":"duyuru","domainId":"b","description":"Duyurular","aliases":{},
             "recipients":{"veli@ornek.com":true,"ali@ornek.com":true},
             "emailAddress":"duyuru@ornek.com","id":"b"}
            """);

        Assert.Equal(["ali@ornek.com", "veli@ornek.com"], MailService.ReadListRecipients(detail));
    }

    /// <summary>
    /// ESKİ hatalı okuma: dizi bekleniyordu. Harita geldiğinde boş dönüyordu — panel her listeyi
    /// "abonesiz" gösteriyordu. Bu davranış geri gelmemeli.
    /// </summary>
    [Fact]
    public void ReadListRecipients_IsNotEmpty_ForTheMapShapeTheServerActuallyReturns()
    {
        var detail = Parse("""{"recipients":{"ali@ornek.com":true}}""");

        Assert.NotEmpty(MailService.ReadListRecipients(detail));
    }

    /// <summary>Dizi gösterimi de okunur (doküman sürümleri arasında iki biçim de örnekleniyor).</summary>
    [Fact]
    public void ReadListRecipients_AlsoAcceptsTheArrayShape()
    {
        var detail = Parse("""{"recipients":["ali@ornek.com","veli@ornek.com"]}""");

        Assert.Equal(["ali@ornek.com", "veli@ornek.com"], MailService.ReadListRecipients(detail));
    }

    /// <summary>Kümeden düşürülmüş (false) girdi abone SAYILMAZ.</summary>
    [Fact]
    public void ReadListRecipients_IgnoresEntriesMarkedFalse()
    {
        var detail = Parse("""{"recipients":{"ali@ornek.com":true,"eski@ornek.com":false}}""");

        Assert.Equal(["ali@ornek.com"], MailService.ReadListRecipients(detail));
    }

    [Theory]
    [InlineData("""{"emailAddress":"duyuru@ornek.com"}""")]
    [InlineData("""{"recipients":null}""")]
    [InlineData("""{"recipients":{}}""")]
    public void ReadListRecipients_IsEmpty_WhenThereAreNoSubscribers(string json)
    {
        Assert.Empty(MailService.ReadListRecipients(Parse(json)));
    }

    // --- 4) Abone ekleme/çıkarma (fark uygulama) ---

    /// <summary>
    /// Tek abone ekleme: sunucudaki küme KORUNUR. Kaybolursa "bir kişi ekledim, diğerleri gitti"
    /// olur — sunucuda kümeyi komple değiştiren tek bir çağrı olduğu için gerçek risk budur.
    /// </summary>
    [Fact]
    public void ResolveRecipients_AddsToTheExistingSubscribers()
    {
        var result = MailService.ResolveRecipients(
            ["ali@ornek.com"],
            replacement: null,
            add: ["veli@ornek.com"],
            remove: [],
            Domain);

        Assert.Equal(["ali@ornek.com", "veli@ornek.com"], result);
    }

    /// <summary>Abonelikten çıkarma: yalnız istenen adres düşer, diğerleri kalır.</summary>
    [Fact]
    public void ResolveRecipients_RemovesOnlyTheRequestedSubscriber()
    {
        var result = MailService.ResolveRecipients(
            ["ali@ornek.com", "veli@ornek.com"],
            replacement: null,
            add: [],
            remove: ["veli@ornek.com"],
            Domain);

        Assert.Equal(["ali@ornek.com"], result);
    }

    /// <summary>Çıkarma büyük/küçük harften bağımsızdır; aksi hâlde "çıkardım ama gitmedi" olur.</summary>
    [Fact]
    public void ResolveRecipients_RemovesRegardlessOfCase()
    {
        var result = MailService.ResolveRecipients(
            ["ali@ornek.com"],
            replacement: null,
            add: [],
            remove: ["ALI@Ornek.COM"],
            Domain);

        Assert.Empty(result);
    }

    /// <summary>
    /// Toplu değiştirme: <c>recipients</c> verildiğinde küme O LİSTEYE eşitlenir (eskisi korunmaz).
    /// </summary>
    [Fact]
    public void ResolveRecipients_ReplacesTheWholeSet_WhenRecipientsIsProvided()
    {
        var result = MailService.ResolveRecipients(
            ["eski@ornek.com"],
            replacement: ["yeni@ornek.com"],
            add: [],
            remove: [],
            Domain);

        Assert.Equal(["yeni@ornek.com"], result);
    }

    /// <summary>
    /// BOŞ dizi ile <c>null</c> ayrı anlamdadır: boş dizi "hepsini sil" (meşru istek),
    /// <c>null</c> "toplu değiştirme isteme". İkisi karışırsa ya liste boşaltılamaz ya da tek
    /// abone eklemek isteyen yönetici listeyi kazara siler.
    /// </summary>
    [Fact]
    public void ResolveRecipients_ClearsEveryone_WhenRecipientsIsAnEmptyList()
    {
        var result = MailService.ResolveRecipients(
            ["ali@ornek.com", "veli@ornek.com"],
            replacement: [],
            add: [],
            remove: [],
            Domain);

        Assert.Empty(result);
    }

    [Fact]
    public void ResolveRecipients_KeepsEveryone_WhenNothingIsRequested()
    {
        var result = MailService.ResolveRecipients(
            ["ali@ornek.com", "veli@ornek.com"],
            replacement: null,
            add: [],
            remove: [],
            Domain);

        Assert.Equal(["ali@ornek.com", "veli@ornek.com"], result);
    }

    /// <summary>Aynı adres hem eklenip hem çıkarılırsa güvenli yön "çıkarıldı"dır.</summary>
    [Fact]
    public void ResolveRecipients_PrefersRemoval_WhenAnAddressIsBothAddedAndRemoved()
    {
        var result = MailService.ResolveRecipients(
            [],
            replacement: null,
            add: ["ali@ornek.com"],
            remove: ["ali@ornek.com"],
            Domain);

        Assert.Empty(result);
    }

    /// <summary>Var olan aboneyi yeniden eklemek listeyi ikizlemez.</summary>
    [Fact]
    public void ResolveRecipients_DoesNotDuplicateAnExistingSubscriber()
    {
        var result = MailService.ResolveRecipients(
            ["ali@ornek.com"],
            replacement: null,
            add: ["ALI@ORNEK.COM"],
            remove: [],
            Domain);

        Assert.Equal(["ali@ornek.com"], result);
    }

    /// <summary>Geçersiz adres, fark uygulama yolunda da reddedilir (tek doğrulama noktası).</summary>
    [Fact]
    public void ResolveRecipients_RejectsAnInvalidAddedAddress()
    {
        var exception = Assert.Throws<AppException>(() => MailService.ResolveRecipients(
            ["ali@ornek.com"],
            replacement: null,
            add: ["ali veli@ornek.com"],
            remove: [],
            Domain));

        Assert.Equal("mail.invalid_recipient", exception.Message);
    }

    // --- 5) Alan adı görünürlük kapısı ---

    /// <summary>Seçili alan adının kendi listesi/kutusu görünür.</summary>
    [Theory]
    [InlineData("duyuru@ornek.com")]
    [InlineData("DUYURU@ORNEK.COM")]
    [InlineData("  duyuru@ornek.com  ")]
    [InlineData("duyuru@ornek.com.")]
    public void BelongsToDomain_AcceptsTheSelectedDomain(string address)
    {
        Assert.True(MailService.BelongsToDomain(address, Domain));
    }

    /// <summary>
    /// SIZINTI 1 — BAŞKASININ ALAN ADI. Çıplak <c>EndsWith("ornek.com")</c> ile
    /// <c>kotuornek.com</c> de eşleşir; kiracı, başka bir müşterinin dağıtım listesini ve
    /// abonelerini kendi ekranında görürdü.
    /// </summary>
    [Theory]
    [InlineData("duyuru@kotuornek.com")]
    [InlineData("kutu@kotuornek.com")]
    [InlineData("duyuru@xornek.com")]
    public void BelongsToDomain_RejectsALookalikeDomain(string address)
    {
        Assert.False(MailService.BelongsToDomain(address, Domain));
    }

    /// <summary>
    /// SIZINTI 2 — ALT ALAN ADI. <c>alt.ornek.com</c> AYRI bir mail domainidir; <c>ornek.com</c>
    /// seçiliyken onun kutuları/listeleri listelenmemeli. Nokta sınırına bakan gevşek bir kural
    /// da (ör. <c>EndsWith(".ornek.com")</c>) bunu içeri alırdı.
    /// </summary>
    [Theory]
    [InlineData("kutu@alt.ornek.com")]
    [InlineData("duyuru@alt.ornek.com")]
    [InlineData("duyuru@a.b.ornek.com")]
    public void BelongsToDomain_RejectsASubdomainOfTheSelectedDomain(string address)
    {
        Assert.False(MailService.BelongsToDomain(address, Domain));
    }

    /// <summary>Üst alan adı da eşleşmez (seçili olan alt alan adıyken tersi yön).</summary>
    [Fact]
    public void BelongsToDomain_RejectsTheParentDomain()
    {
        Assert.False(MailService.BelongsToDomain("duyuru@ornek.com", "alt.ornek.com"));
    }

    /// <summary>Yerel kısımdaki "@" ya da alan adı olmayan değerler kapıdan geçmez.</summary>
    [Theory]
    [InlineData("duyuru", Domain)]
    [InlineData("duyuru@", Domain)]
    [InlineData("", Domain)]
    [InlineData("duyuru@ornek.com", "")]
    [InlineData("duyuru@ornek.com", null)]
    public void BelongsToDomain_RejectsMalformedInput(string? address, string? domain)
    {
        Assert.False(MailService.BelongsToDomain(address, domain));
    }

    // --- 6) Adres doğrulayıcı ---

    [Theory]
    [InlineData("ali@ornek.com", true)]
    [InlineData("ali.veli+etiket@alt.ornek.com", true)]
    [InlineData("ali@ornek", false)]
    [InlineData("aliornek.com", false)]
    [InlineData("", false)]
    public void IsValidEmailAddress_AcceptsDeliverableAddressesOnly(string address, bool expected)
    {
        Assert.Equal(expected, MailService.IsValidEmailAddress(address));
    }
}
