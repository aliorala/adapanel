using AdaPanel.Api.Middleware;
using AdaPanel.Infrastructure.Mail;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Dağıtım listesinden SELF-SERVİS çıkma. Panelin tek kimliksiz durum değiştiren ucu olduğu için
/// testlerin ağırlığı işlevde değil, KÖTÜYE KULLANIMDA.
/// </summary>
/// <remarks>
/// Özelliğin biçimi bir ölçümün sonucudur: Stalwart'ın fan-out'u iletiyi kişiselleştirmiyor
/// (teslim edilen iletide <c>List-Unsubscribe</c> YOK, aboneye özel tek başlık teslim anında
/// eklenen <c>Delivered-To</c>). Bu yüzden RFC 8058 tek-tık bağlantısı üretilemez; bağlantı liste
/// başınadır ve bu da e-posta onayını ZORUNLU kılar — onay olmasaydı bağlantıyı gören herkes
/// BAŞKASINI listeden atabilirdi.
/// </remarks>
public sealed class MailListUnsubscribeTests
{
    private const string List = "duyuru@ornek.com";
    private const string Subscriber = "ali@ornek.com";

    private static MailListUnsubscribeService.PendingUnsubscribe Pending(
        DateTimeOffset created,
        DateTimeOffset expires,
        string list = List,
        string subscriber = Subscriber) => new()
        {
            ListAddress = list,
            Subscriber = subscriber,
            CreatedAtUtc = created,
            ExpiresAtUtc = expires
        };

    // --- 1) Belirteç: tahmin edilemez, tek kullanımlık, saklanmaz ---

    /// <summary>
    /// Belirteç kriptografik rastgele olmalı. Bu belirteç, KİMLİKSİZ bir uçta bir aboneyi listeden
    /// düşürmeye yeten TEK kanıttır; tahmin edilebilir olursa özellik bir saldırı aracına döner.
    /// </summary>
    [Fact]
    public void CreateToken_ProducesUnpredictableValues()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => MailListUnsubscribeService.CreateToken()).ToList();

        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// 256 bit entropi: base64url gösteriminde dolgusuz 43 karakter. Kısaltılırsa kaba kuvvet
    /// erişilebilir hâle gelir.
    /// </summary>
    [Fact]
    public void CreateToken_Carries256BitsOfEntropy()
    {
        var token = MailListUnsubscribeService.CreateToken();

        Assert.Equal(43, token.Length);
    }

    /// <summary>
    /// Belirteç URL'de taşınır: <c>+</c> ve <c>/</c> gibi kaçırılması gereken karakterler
    /// bulunmamalı, yoksa e-postadaki bağlantı istemciden istemciye bozulur.
    /// </summary>
    [Fact]
    public void CreateToken_IsUrlSafe()
    {
        for (var i = 0; i < 50; i++)
        {
            var token = MailListUnsubscribeService.CreateToken();

            Assert.DoesNotContain('+', token);
            Assert.DoesNotContain('/', token);
            Assert.DoesNotContain('=', token);
        }
    }

    /// <summary>
    /// Depoda belirtecin KENDİSİ değil özeti durur: yapılandırma dosyasını okuyabilen biri (yedek,
    /// hatalı ACL) aksi hâlde her aboneyi listeden düşürebilirdi.
    /// </summary>
    [Fact]
    public void HashToken_DoesNotStoreTheTokenItself()
    {
        var token = MailListUnsubscribeService.CreateToken();
        var hash = MailListUnsubscribeService.HashToken(token);

        Assert.NotEqual(token, hash);
        Assert.DoesNotContain(token, hash, StringComparison.Ordinal);
        Assert.Equal(64, hash.Length); // SHA-256, onaltılık
    }

    [Fact]
    public void HashToken_IsDeterministic()
    {
        var token = MailListUnsubscribeService.CreateToken();

        Assert.Equal(MailListUnsubscribeService.HashToken(token), MailListUnsubscribeService.HashToken(token));
    }

    [Fact]
    public void HashToken_DiffersBetweenTokens()
    {
        Assert.NotEqual(
            MailListUnsubscribeService.HashToken(MailListUnsubscribeService.CreateToken()),
            MailListUnsubscribeService.HashToken(MailListUnsubscribeService.CreateToken()));
    }

    // --- 2) Süre aşımı ---

    /// <summary>Süresi dolmuş belirteç KABUL EDİLMEMELİ (sınır dahil).</summary>
    [Fact]
    public void IsExpired_IsTrue_AtAndAfterTheExpiryInstant()
    {
        var now = DateTimeOffset.UtcNow;
        var pending = Pending(now.AddHours(-24), now);

        Assert.True(MailListUnsubscribeService.IsExpired(pending, now));
        Assert.True(MailListUnsubscribeService.IsExpired(pending, now.AddSeconds(1)));
    }

    [Fact]
    public void IsExpired_IsFalse_BeforeTheExpiryInstant()
    {
        var now = DateTimeOffset.UtcNow;
        var pending = Pending(now, now.AddHours(24));

        Assert.False(MailListUnsubscribeService.IsExpired(pending, now.AddHours(23)));
    }

    /// <summary>Ömür sonsuz olmamalı: bağlantı posta kutusunda kalıcı olarak durur.</summary>
    [Fact]
    public void TokenLifetime_IsBounded()
    {
        Assert.True(MailListUnsubscribeService.TokenLifetime > TimeSpan.Zero);
        Assert.True(MailListUnsubscribeService.TokenLifetime <= TimeSpan.FromDays(7));
    }

    /// <summary>Budama süresi dolmuşları atar, canlıları KORUR.</summary>
    [Fact]
    public void Prune_RemovesOnlyExpiredEntries()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new Dictionary<string, MailListUnsubscribeService.PendingUnsubscribe>(StringComparer.Ordinal)
        {
            ["eski"] = Pending(now.AddHours(-48), now.AddHours(-24)),
            ["taze"] = Pending(now, now.AddHours(24))
        };

        MailListUnsubscribeService.Prune(store, now);

        Assert.False(store.ContainsKey("eski"));
        Assert.True(store.ContainsKey("taze"));
    }

    // --- 3) Kötüye kullanım: onay postası spam silahına dönmemeli ---

    /// <summary>
    /// Aynı (liste, adres) için bekleme süresi dolmadan İKİNCİ posta gönderilmez. Uç kimliksiz ve
    /// e-posta gönderiyor; sınır olmasaydı saldırgan, kurbanın adresini yazıp isteği tekrarlayarak
    /// paneli spam silahına çevirebilirdi — üstelik postalar panelin kendi alan adından gider.
    /// </summary>
    [Fact]
    public void ShouldSendConfirmation_IsFalse_WithinTheCooldownForTheSameAddress()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new Dictionary<string, MailListUnsubscribeService.PendingUnsubscribe>(StringComparer.Ordinal)
        {
            ["a"] = Pending(now, now.AddHours(24))
        };

        Assert.False(MailListUnsubscribeService.ShouldSendConfirmation(store, List, Subscriber, now));
    }

    [Fact]
    public void ShouldSendConfirmation_IsTrue_AfterTheCooldownElapses()
    {
        var created = DateTimeOffset.UtcNow;
        var store = new Dictionary<string, MailListUnsubscribeService.PendingUnsubscribe>(StringComparer.Ordinal)
        {
            ["a"] = Pending(created, created.AddHours(24))
        };

        var later = created + MailListUnsubscribeService.ResendCooldown + TimeSpan.FromSeconds(1);

        Assert.True(MailListUnsubscribeService.ShouldSendConfirmation(store, List, Subscriber, later));
    }

    /// <summary>Bekleme BÜYÜK/KÜÇÜK HARFTEN bağımsız olmalı; yoksa harf değiştirerek aşılır.</summary>
    [Fact]
    public void ShouldSendConfirmation_CooldownCannotBeBypassedByChangingCase()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new Dictionary<string, MailListUnsubscribeService.PendingUnsubscribe>(StringComparer.Ordinal)
        {
            ["a"] = Pending(now, now.AddHours(24))
        };

        Assert.False(MailListUnsubscribeService.ShouldSendConfirmation(store, "DUYURU@ORNEK.COM", "ALI@ORNEK.COM", now));
    }

    /// <summary>Bekleme adres BAŞINA olmalı; başka bir abonenin isteği engellenmemeli.</summary>
    [Fact]
    public void ShouldSendConfirmation_IsTrue_ForADifferentSubscriber()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new Dictionary<string, MailListUnsubscribeService.PendingUnsubscribe>(StringComparer.Ordinal)
        {
            ["a"] = Pending(now, now.AddHours(24))
        };

        Assert.True(MailListUnsubscribeService.ShouldSendConfirmation(store, List, "veli@ornek.com", now));
    }

    /// <summary>Aynı kişi BAŞKA bir listeden de çıkabilmeli.</summary>
    [Fact]
    public void ShouldSendConfirmation_IsTrue_ForADifferentList()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new Dictionary<string, MailListUnsubscribeService.PendingUnsubscribe>(StringComparer.Ordinal)
        {
            ["a"] = Pending(now, now.AddHours(24))
        };

        Assert.True(MailListUnsubscribeService.ShouldSendConfirmation(store, "baska@ornek.com", Subscriber, now));
    }

    [Fact]
    public void ResendCooldown_IsLongEnoughToBeMeaningful()
    {
        Assert.True(MailListUnsubscribeService.ResendCooldown >= TimeSpan.FromMinutes(1));
    }

    // --- 4) Onay adresi ---

    [Fact]
    public void BuildConfirmUrl_SubstitutesThePlaceholder()
    {
        var url = MailListUnsubscribeService.BuildConfirmUrl("https://panel.ornek.com/u?token={token}", "ABC123");

        Assert.Equal("https://panel.ornek.com/u?token=ABC123", url);
    }

    /// <summary>Yer tutucu yoksa sorgu dizesi eklenir; bağlantı yine de çalışır.</summary>
    [Fact]
    public void BuildConfirmUrl_AppendsTheTokenWhenThereIsNoPlaceholder()
    {
        var url = MailListUnsubscribeService.BuildConfirmUrl("https://panel.ornek.com/u", "ABC123");

        Assert.Equal("https://panel.ornek.com/u?token=ABC123", url);
    }

    /// <summary>Belirteç URL'ye KAÇIRILARAK konur; aksi hâlde bağlantı bozulabilir.</summary>
    [Fact]
    public void BuildConfirmUrl_EscapesTheToken()
    {
        var url = MailListUnsubscribeService.BuildConfirmUrl("https://p/u?token={token}", "a b&c");

        Assert.DoesNotContain(" ", url);
        Assert.DoesNotContain("&c", url);
    }

    // --- 5) Adres normalleştirme ---

    [Theory]
    [InlineData("  Ali@Ornek.COM  ", "ali@ornek.com")]
    [InlineData("ali@ornek.com.", "ali@ornek.com")]
    [InlineData("", "")]
    public void NormalizeAddress_TrimsLowercasesAndDropsTheTrailingDot(string input, string expected)
    {
        Assert.Equal(expected, MailListUnsubscribeService.NormalizeAddress(input));
    }

    [Theory]
    [InlineData("duyuru@ornek.com", "ornek.com")]
    [InlineData("a@b@ornek.com", "ornek.com")]
    [InlineData("duyuru", "")]
    [InlineData("duyuru@", "")]
    public void DomainOf_TakesEverythingAfterTheLastAt(string address, string expected)
    {
        Assert.Equal(expected, MailListUnsubscribeService.DomainOf(address));
    }

    // --- 6) Gizli giriş yolu muafiyeti ---

    /// <summary>
    /// Muafiyet OLMAZSA uç eklenmiş ama her istekte 404 dönüyor olurdu — yani özellik var görünüp
    /// hiç çalışmazdı. Abone, panelin gizli giriş yolunu bilemez ve özel başlık gönderemez.
    /// </summary>
    [Fact]
    public void UnsubscribeEndpoint_IsExemptFromTheSecretEntryPath()
    {
        Assert.True(SecureEntryPathMiddleware.IsAlwaysPublicPath(
            new PathString("/api/v1/public/mail-lists/unsubscribe/request")));
        Assert.True(SecureEntryPathMiddleware.IsAlwaysPublicPath(
            new PathString("/api/v1/public/mail-lists/unsubscribe/confirm")));
    }

    /// <summary>
    /// Muafiyet DAR: başka bir <c>/api/v1/public…</c> ucu bu kapıdan SIZAMAZ. Segment sınırına
    /// bakılmasaydı, sonradan eklenecek herhangi bir "public" uç sessizce korumasız kalırdı.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/public")]
    [InlineData("/api/v1/public/other")]
    [InlineData("/api/v1/public/mail-listsX")]
    [InlineData("/api/v1/mail/lists")]
    [InlineData("/api/v1/mail")]
    [InlineData("/api/v1/users")]
    public void OtherApiPaths_AreNotExempt(string path)
    {
        Assert.False(SecureEntryPathMiddleware.IsAlwaysPublicPath(new PathString(path)));
    }

    /// <summary>Muafiyet yalnız bu segmente ait olmalı.</summary>
    [Fact]
    public void TheExemptionMatchesOnlyTheUnsubscribeSegment()
    {
        Assert.True(SecureEntryPathMiddleware.IsMailListUnsubscribePath(new PathString("/api/v1/public/mail-lists")));
        Assert.False(SecureEntryPathMiddleware.IsMailListUnsubscribePath(new PathString("/api/v1/public/mail-listsX")));
        Assert.False(SecureEntryPathMiddleware.IsMailListUnsubscribePath(new PathString("/api/v1/mail")));
    }

    // --- 7) Tarayıcı akışı: bağlantı GERÇEKTEN tıklanabilir olmalı ---

    private static string ReadControllerSource()
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            var aday = Path.Combine(klasor, "src", "AdaPanel.Api", "Controllers", "MailListPublicController.cs");
            if (File.Exists(aday))
            {
                return File.ReadAllText(aday);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new FileNotFoundException("MailListPublicController.cs bulunamadi.");
    }

    /// <summary>
    /// REGRESYON: onay ucu bir zamanlar YALNIZ <c>[HttpPost] [FromBody]</c> JSON kabul ediyordu.
    /// E-postadaki bağlantıya tıklayan tarayıcı GET yapar — yani akış 405 ile ÖLÜYORDU: abone
    /// tıklıyor, hiçbir şey olmuyordu. Uçların tarayıcının gerçekten yaptığı şeyi kabul etmesi şart.
    /// </summary>
    [Fact]
    public void ConfirmLink_IsReachableWithABrowserGet()
    {
        var kaynak = ReadControllerSource();

        Assert.Contains("[HttpGet(\"unsubscribe/confirm\")]", kaynak, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"unsubscribe\")]", kaynak, StringComparison.Ordinal);
    }

    /// <summary>
    /// Form gönderimleri <c>[FromForm]</c> ile bağlanmalı: tarayıcı formu
    /// <c>application/x-www-form-urlencoded</c> gönderir, <c>[FromBody]</c> JSON bunu BAĞLAMAZ.
    /// </summary>
    [Fact]
    public void FormPosts_BindFromFormNotFromJsonBody()
    {
        // Yalnız KOD sayılır: doküman yorumları eski tasarımı ANLATTIĞI için "[FromBody]" kelimesini
        // içerir; yorumu koda saymak testi yanlış yerde kırardı.
        var kod = string.Join(
            '\n',
            ReadControllerSource()
                .Split('\n')
                .Where(satir => !satir.TrimStart().StartsWith("///", StringComparison.Ordinal)));

        Assert.Contains("[FromForm]", kod, StringComparison.Ordinal);
        Assert.DoesNotContain("[FromBody]", kod, StringComparison.Ordinal);
    }

    /// <summary>
    /// GET, ASLA çıkarma yapmamalı. Posta istemcileri ve kurumsal güvenlik tarayıcıları iletideki
    /// bağlantıları ÖN-YÜKLER; GET doğrudan çıkarsaydı abone bağlantıya hiç dokunmadan, yalnızca
    /// iletiyi aldığı için listeden düşerdi. Çıkarma yalnız POST'ta olur.
    /// </summary>
    [Fact]
    public void TheGetEndpointsNeverRemoveAnyone()
    {
        var kaynak = ReadControllerSource();

        // ConfirmAsync (servisteki çıkarma çağrısı) yalnız POST eyleminde geçmeli.
        var getForm = kaynak.IndexOf("public ContentResult ConfirmForm", StringComparison.Ordinal);
        var postConfirm = kaynak.IndexOf("public async Task<ContentResult> ConfirmAsync", StringComparison.Ordinal);
        var cagri = kaynak.IndexOf("unsubscribeService.ConfirmAsync", StringComparison.Ordinal);

        Assert.True(getForm >= 0 && postConfirm >= 0 && cagri >= 0);
        Assert.True(cagri > postConfirm, "Cikarma cagrisi POST eyleminin ICINDE olmali.");
    }

    // --- 8) Sayfalar: kimliksiz uçta yansıtılmış XSS olmamalı ---

    /// <summary>
    /// Liste adresi SORGU DİZESİNDEN gelir ve sayfaya basılır. Kaçırılmasaydı kimliksiz bir uçta
    /// yansıtılmış XSS olurdu — saldırgan, kurbanı panelin alan adında betik çalıştıran bir
    /// bağlantıya çekebilirdi.
    /// </summary>
    [Fact]
    public void RequestForm_EscapesTheListAddress()
    {
        var html = MailListUnsubscribePages.RequestForm("<script>alert(1)</script>", "/x");

        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    /// <summary>Belirteç de gizli alana basılır; tırnak kaçışı formu kırıp betik enjekte edebilirdi.</summary>
    [Fact]
    public void ConfirmForm_EscapesTheToken()
    {
        var html = MailListUnsubscribePages.ConfirmForm("\"><script>alert(1)</script>", "/x");

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ResultPage_EscapesTheListAddress()
    {
        var html = MailListUnsubscribePages.Result(removed: true, listAddress: "<img src=x onerror=alert(1)>");

        Assert.DoesNotContain("<img src=x", html, StringComparison.Ordinal);
    }

    /// <summary>Başarısızlık sayfası "çıkarılmadınız" demeli; "tamam" demek yalan olurdu.</summary>
    [Fact]
    public void ResultPage_SaysPlainlyThatNothingHappened_WhenTheTokenWasInvalid()
    {
        var html = MailListUnsubscribePages.Result(removed: false, listAddress: string.Empty);

        Assert.Contains("çıkarılmadınız", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT removed", html, StringComparison.Ordinal);
    }

    /// <summary>İstek sayfası, adresin abone OLUP OLMADIĞINI ele vermemeli.</summary>
    [Fact]
    public void RequestAcceptedPage_DoesNotRevealWhetherTheAddressWasSubscribed()
    {
        var html = MailListUnsubscribePages.RequestAccepted();

        Assert.Contains("aboneyse", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("If the address is subscribed", html, StringComparison.Ordinal);
    }

    /// <summary>Bu sayfalar arama motorlarına girmemeli.</summary>
    [Fact]
    public void ThePagesAreNotIndexable()
    {
        Assert.Contains("noindex", MailListUnsubscribePages.RequestAccepted(), StringComparison.Ordinal);
    }
}
