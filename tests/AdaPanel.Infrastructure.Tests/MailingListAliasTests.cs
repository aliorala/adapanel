using System.Security.Claims;
using System.Text.Json;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Audit;
using AdaPanel.Core.Dtos.Mail;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Mail;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Dağıtım listesi EK ADRESLERİ (Stalwart <c>MailingList.aliases</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>DÜRÜSTLÜK SINIRI — neyin kanıtlandığı, neyin kanıtlanmadığı.</b> Bu makinede AYAKTA
/// STALWART YOK (ikililer kurulu, sunucu çalışmıyor — bu ayrım bir testi kırdı, bkz.
/// <see cref="KendiAlanAdiKapidanGecer"/>). Dolayısıyla burada <b>kanıtlanan</b> şeyler: alias şemasının
/// nasıl okunduğu, üretilen sieve script'lerinin içeriği ve KAPSAM (kiracı izolasyonu) kapısının
/// gerçekten kapandığı. <b>Kanıtlanamayan</b>: <c>update MailingList --file {"aliases":...}</c>
/// çağrısını Stalwart'ın kabul ettiği ve alias'a gönderilen postanın gerçekten abonelere
/// dağıtıldığı. İlk kurulumda ELLE denenmeli.
/// </para>
/// <para>
/// Şema TAHMİN EDİLMEDİ: <c>MailingList</c> alanları (<c>name</c>, <c>domainId</c>,
/// <c>description</c>, <c>aliases</c>, <c>recipients</c>, <c>memberTenantId</c>,
/// <c>emailAddress</c>) daha önce sunucudan okunmuş ve <see cref="MailListDto"/> içine yazılmıştı.
/// Bu depoda posta listesi özelliği tam olarak "nesne adı tahmin edildi" yüzünden aylarca ölüydü.
/// </para>
/// </remarks>
public sealed class MailingListAliasTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Alan adı kimliği → ad haritası; sunucuda alias'lar ADLA değil KİMLİKLE durur.</summary>
    private static readonly Dictionary<string, string> AlanAdlari = new(StringComparer.Ordinal)
    {
        ["a"] = "ornek.com",
        ["b"] = "ikinci.com"
    };

    // --- 1) Şema sözleşmesi: alias haritası nasıl okunuyor ---

    /// <summary>
    /// Alias, hesaplardakiyle AYNI gösterimdedir: sayısal anahtarlı harita,
    /// değer <c>{"name":..,"domainId":..,"enabled":true}</c>. Dizi bekleyen bir okuma her listeyi
    /// "ek adresi yok" gösterirdi — aboneleri dizi sanmanın maliyeti bu depoda zaten ödenmişti.
    /// </summary>
    [Fact]
    public void AliasHaritasiTamAdreseCevriliyor()
    {
        var liste = Parse("""
            {"emailAddress":"duyuru@ornek.com",
             "aliases":{"0":{"name":"info","domainId":"a","enabled":true}}}
            """);

        Assert.Equal(["info@ornek.com"], MailService.ReadAliasAddresses(liste, AlanAdlari));
    }

    /// <summary>
    /// Alias LİSTENİN alan adından BAŞKA bir alan adında olabilir; adres o alan adıyla kurulur.
    /// Listenin alan adı varsayılsaydı panel var olmayan bir adres gösterirdi.
    /// </summary>
    [Fact]
    public void BaskaAlanAdindakiAliasKendiAlanAdiylaGosterilir()
    {
        var liste = Parse("""
            {"emailAddress":"duyuru@ornek.com",
             "aliases":{"0":{"name":"duyuru","domainId":"b","enabled":true}}}
            """);

        Assert.Equal(["duyuru@ikinci.com"], MailService.ReadAliasAddresses(liste, AlanAdlari));
    }

    /// <summary>
    /// ÇÖZÜLEMEYEN alan adı kimliği ATLANIR — "bilinmeyen" gibi bir yer tutucu KONMAZ.
    /// </summary>
    /// <remarks>
    /// Kimlik çözülemiyorsa alias, çağıranın göremediği bir alan adına aittir. Yer tutucu koymak
    /// kiracıya komşusunun bir kaydının VARLIĞINI sızdırırdı — panelin en pahalı hata sınıfı
    /// (kapatılan çapraz-kiracı açığı) tam olarak bu türdendi.
    /// </remarks>
    [Fact]
    public void CozulemeyenAlanAdiSizdirilmadanAtlanir()
    {
        var liste = Parse("""
            {"aliases":{"0":{"name":"gizli","domainId":"BILINMEYEN","enabled":true},
                        "1":{"name":"info","domainId":"a","enabled":true}}}
            """);

        Assert.Equal(["info@ornek.com"], MailService.ReadAliasAddresses(liste, AlanAdlari));
    }

    /// <summary>
    /// DEVRE DIŞI alias da listelenir. Nesnede duruyor ve aynı adresin ikinci kez eklenmesini
    /// engelliyor; gizlemek, yöneticiye "bu adres boşta" dedirtip sonra "zaten kullanılıyor"
    /// hatası vermek olurdu.
    /// </summary>
    [Fact]
    public void DevreDisiAliasDaGorunur()
    {
        var liste = Parse("""
            {"aliases":{"0":{"name":"eski","domainId":"a","enabled":false}}}
            """);

        Assert.Equal(["eski@ornek.com"], MailService.ReadAliasAddresses(liste, AlanAdlari));
    }

    [Fact]
    public void AliasYokkenBosDoner()
    {
        Assert.Empty(MailService.ReadAliasAddresses(Parse("""{"emailAddress":"duyuru@ornek.com"}"""), AlanAdlari));
        Assert.Empty(MailService.ReadAliasAddresses(Parse("""{"aliases":{}}"""), AlanAdlari));
        Assert.Empty(MailService.ReadAliasAddresses(Parse("""{"aliases":[]}"""), AlanAdlari));
        Assert.Empty(MailService.ReadAliasAddresses(default, AlanAdlari));
    }

    /// <summary>Sonuç küçük harfli, SIRALI ve tekrarsız: ekran sırası sunucunun harita sırasına bırakılamaz.</summary>
    [Fact]
    public void SonucKucukHarfliSiraliVeTekrarsiz()
    {
        var liste = Parse("""
            {"aliases":{"0":{"name":"Zeta","domainId":"a"},
                        "1":{"name":"alfa","domainId":"a"},
                        "2":{"name":"ALFA","domainId":"a"}}}
            """);

        Assert.Equal(["alfa@ornek.com", "zeta@ornek.com"], MailService.ReadAliasAddresses(liste, AlanAdlari));
    }

    // --- 2) GÜVENLİK: ek adres, gönderim politikasını ATLATMAMALI ---

    /// <summary>
    /// Kısıtlı bir listenin EK ADRESİ de aynı kuralı almalı.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bu, özelliğin en tehlikeli tarafı ve tasarım sırasında ölçüldü: RCPT script'i zarftaki
    /// alıcıyla eşleşiyor (<c>envelope :is "to"</c>). Alias'a yazan bir göndericinin zarf alıcısı
    /// ALIAS'tır. Yalnız ana adrese kural konsaydı, "yalnız aboneler" seçilmiş KAPALI bir liste
    /// alias üzerinden HERKESE açık kalırdı — yani ek adres özelliği, kendisi bir politika
    /// atlatma yolu olurdu.
    /// </para>
    /// <para>
    /// Kanıtlanamayan (canlı sunucu yok): Stalwart'ın RCPT aşamasında alias'ı ana adrese
    /// genişletip genişletmediği. Kural her iki davranışta da doğru — genişletiyorsa alias kuralı
    /// hiç eşleşmez (zararsız), genişletmiyorsa açığı kapatan tek şey odur.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <b>Bu testin ilk sürümü YANLIŞ ŞEYİ ÖLÇÜYORDU ve mutasyon yakaladı.</b> Script üreticisine
    /// ELLE alias kuralı veriyordu, yani "alias kuralı verilirse ne olur"u doğruluyordu. "Alias
    /// kuralını hiç üretme" mutasyonu HAYATTA KALDI. Bu depoda aynı tuzağa silme ekibi de düşmüştü
    /// (CODEX_MEMORY 218, ders 1). Ölçüm noktası kural ÜRETİMİNE taşındı.
    /// </remarks>
    [Fact]
    public void EkAdresGonderimPolitikasiniAtlatamaz()
    {
        var kurallar = MailService.BuildListPolicyRules(
            "duyuru@ornek.com",
            MailListPostingPolicy.Subscribers,
            ["ali@ornek.com"],
            ["info@ornek.com", "duyurular@ikinci.com"]);

        // ÜRETİM: her ek adres kendi kuralını aldı mı?
        Assert.Equal(
            ["duyuru@ornek.com", "info@ornek.com", "duyurular@ikinci.com"],
            kurallar.Select(kural => kural.Address));

        // Ve hepsi AYNI kısıtı taşıyor — alias'ınki gevşetilmiş olamaz.
        Assert.All(kurallar, kural =>
        {
            Assert.Equal(MailListPostingPolicy.Subscribers, kural.Policy);
            Assert.Equal(["ali@ornek.com"], kural.AllowedSenders);
        });

        // UYGULAMA: üretilen kurallar script'te gerçekten üç ayrı REDDETME'ye dönüşüyor mu?
        var script = MailService.BuildListPolicyScript(kurallar);
        Assert.Contains("\"to\" \"duyuru@ornek.com\"", script, StringComparison.Ordinal);
        Assert.Contains("\"to\" \"info@ornek.com\"", script, StringComparison.Ordinal);
        Assert.Contains("\"to\" \"duyurular@ikinci.com\"", script, StringComparison.Ordinal);
        Assert.Equal(3, script.Split("550 5.7.2").Length - 1);
    }

    /// <summary>Ek adresi olmayan liste tek kural üretir — düzeltme var olan davranışı bozmamalı.</summary>
    [Fact]
    public void EkAdresiOlmayanListeTekKuralUretir()
    {
        var kurallar = MailService.BuildListPolicyRules(
            "duyuru@ornek.com", MailListPostingPolicy.Allowed, ["yonetici@ornek.com"], []);

        Assert.Equal(["duyuru@ornek.com"], kurallar.Select(kural => kural.Address));
    }

    // --- 3) List-Unsubscribe: alias'a gelen postada da başlık olmalı, ama LİSTE kimliğiyle ---

    /// <summary>
    /// Alias kuralı <c>To:</c> başlığında ALIAS'ı arar; eklediği <c>List-Id</c> ve abonelikten
    /// çıkma bağlantısı ise LİSTENİN ANA adresini gösterir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Alias atlansaydı: alias'a gelen duyuruda "abonelikten çık" bağlantısı HİÇ olmazdı, aynı
    /// listenin ana adresinden gelenlerde olurdu. Abone farkı anlayamaz, biz de şikâyeti
    /// üretemezdik — sessiz bir uyumsuzluk (RFC 8058 / gönderici itibarı).
    /// </para>
    /// <para>
    /// <c>List-Id</c> neden alias'tan türetilmiyor: o başlık ADRESİ değil LİSTEYİ tanımlar ve
    /// posta istemcileri ona göre gruplar/süzer. Alias başına ayrı kimlik, aynı listenin
    /// iletilerini kullanıcının kutusunda İKİ AYRI listeymiş gibi bölerdi.
    /// </para>
    /// </remarks>
    [Fact]
    public void EkAdreseGelenPostaDaAbonelikBasligiAlir_AmaListeKimligiyle()
    {
        // ÜRETİM (elle kural verilmiyor — bkz. EkAdresGonderimPolitikasiniAtlatamaz'daki ders).
        var kurallar = MailService.BuildListUnsubscribeRules(
            "Duyuru@Ornek.com", "https://p.test", ["info@ornek.com"]);

        Assert.Equal(["duyuru@ornek.com", "info@ornek.com"], kurallar.Select(kural => kural.ListAddress));

        // Bağlantı İKİSİNDE de listenin ana adresini gösterir; alias'a özel çıkış üretilmez.
        Assert.All(kurallar, kural =>
            Assert.Equal("https://p.test/api/v1/public/mail-lists/unsubscribe?list=duyuru%40ornek.com", kural.UnsubscribeUrl));

        // UYGULAMA.
        var script = MailService.BuildDataStageScript(new Dictionary<string, MailService.AutoResponderEntry>(), kurallar);

        // Eşleşme ALIAS'ın kendisiyle (To: başlığında liste adresi görünmez).
        Assert.Contains("\"to\" \"info@ornek.com\"", script, StringComparison.Ordinal);

        // Ama kimlik LİSTENİN: "info.ornek.com" ASLA List-Id olmamalı.
        Assert.DoesNotContain("<info.ornek.com>", script, StringComparison.Ordinal);
        Assert.Equal(2, script.Split("<duyuru.ornek.com>").Length - 1);
    }

    /// <summary>
    /// Liste adresi alias kümesinde de geçiyorsa (bozuk veri) kural İKİ KEZ yazılmaz: aynı iletiye
    /// iki <c>List-Unsubscribe</c> başlığı eklenirdi ve bazı istemciler bunu geçersiz sayar.
    /// </summary>
    [Fact]
    public void ListeAdresiAliasKumesindeDeVarsaKuralTekrarlanmaz()
    {
        var kurallar = MailService.BuildListUnsubscribeRules(
            "duyuru@ornek.com", "https://p.test", ["duyuru@ornek.com", "info@ornek.com"]);

        Assert.Equal(["duyuru@ornek.com", "info@ornek.com"], kurallar.Select(kural => kural.ListAddress));
    }

    // --- 3b) SAPMA NÖBETÇİSİ: script üreticileri alias'ları GERÇEKTEN okuyor mu? ---

    /// <summary>
    /// Kural üretimi test edilebilir dikişlere taşındı; ama o dikişleri BESLEYEN iki metot
    /// (<c>SyncListPolicyScriptAsync</c>, <c>BuildListUnsubscribeRulesAsync</c>) canlı
    /// <c>stalwart-cli</c> çağırıyor ve bu makinede koşturulamıyor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden bu kaba nöbetçi yine de değerli:</b> mutasyon testi boşluğu ÖLÇTÜ — besleyen
    /// çağrıda <c>ReadAliasAddresses(...)</c> yerine boş liste geçmek HİÇBİR testi kırmıyordu.
    /// Yani "kural üretimi doğru" ile "kural üretimine doğru veri gidiyor" ayrı sorular ve
    /// ikincisi kapsanmıyordu. Kapsanmayan yerde sessiz sapma olur: alias özelliği çalışır
    /// görünürken gönderim politikası alias'ta uygulanmaz — tam olarak kapatmaya çalıştığımız açık.
    /// </para>
    /// <para>
    /// Ölçülebilen tek şey kaynaktaki bağ. Bağ koptuğunda test kırmızıya döner ve neyin
    /// koptuğunu söyler. Canlı sunucu geldiğinde bu iki nöbetçi GERÇEK uçtan uca testle
    /// DEĞİŞTİRİLMELİDİR.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("rules.AddRange(BuildListPolicyRules(")]
    [InlineData("rules.AddRange(BuildListUnsubscribeRules(")]
    [InlineData("detail is null ? [] : ReadAliasAddresses(detail.Value, domainNamesById)));")]
    public void ScriptUreticileriAliaslariOkuyor(string beklenenBag)
    {
        var kaynak = File.ReadAllText(KaynakDosyaYolu("src/AdaPanel.Infrastructure/Mail/MailService.cs"));
        Assert.Contains(beklenenBag, kaynak, StringComparison.Ordinal);
    }

    /// <summary>Her İKİ besleyici de alias okumalı: tek bir tanesi yeterli DEĞİL.</summary>
    [Fact]
    public void HerIkiScriptDeAliasOkuyor()
    {
        var kaynak = File.ReadAllText(KaynakDosyaYolu("src/AdaPanel.Infrastructure/Mail/MailService.cs"));
        Assert.Equal(2, kaynak.Split("detail is null ? [] : ReadAliasAddresses(detail.Value, domainNamesById)));").Length - 1);
    }

    private static string KaynakDosyaYolu(string goreliYol)
    {
        var dizin = new DirectoryInfo(AppContext.BaseDirectory);
        while (dizin is not null && !File.Exists(Path.Combine(dizin.FullName, "AdaPanel.sln")))
        {
            dizin = dizin.Parent;
        }

        Assert.NotNull(dizin);
        var yol = Path.Combine(dizin!.FullName, goreliYol.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(yol), $"kaynak dosya bulunamadı: {yol}");
        return yol;
    }

    // --- 4) KİRACI İZOLASYONU: ek adres başka bir kiracının alan adına bağlanamaz ---

    /// <summary>
    /// SIZINTI YOLU: kiracı KENDİ listesine <c>duyuru@kurban.com</c> ek adresi tanımlarsa,
    /// kurbanın alan adına gelen posta kendi abone kümesine — yani kendisine — dağıtılırdı.
    /// Kapatılmış olan alias/çapraz-kiracı açığının birebir aynısı, sadece hesap yerine liste
    /// üzerinden.
    /// </summary>
    [Fact]
    public async Task BaskaKiraciyaAitAlanAdiEkAdresOlarakEklenemez()
    {
        var denetleyici = Duzenek();

        var hata = await Assert.ThrowsAsync<AppException>(() => denetleyici.AddListAliasAsync(
            new MailListAliasRequest { ListAddress = "duyuru@benim.com", Alias = "duyuru@kurban.com" },
            CancellationToken.None));

        Assert.Equal("auth.domain_forbidden", hata.MessageKey);
        Assert.Equal(403, hata.HttpStatusCode);
    }

    /// <summary>
    /// BENZEYEN ALAN ADI. Naif <c>EndsWith("benim.com")</c> ile <c>kotubenim.com</c> de eşleşirdi;
    /// denetim kullanıcının izinli alan adı KÜMESİNDE tam eşleşme aradığı için geçmez.
    /// </summary>
    [Theory]
    [InlineData("duyuru@kotubenim.com")]
    [InlineData("duyuru@xbenim.com")]
    [InlineData("duyuru@alt.benim.com")]
    public async Task BenzeyenAlanAdiEkAdresOlarakEklenemez(string ekAdres)
    {
        var denetleyici = Duzenek();

        var hata = await Assert.ThrowsAsync<AppException>(() => denetleyici.AddListAliasAsync(
            new MailListAliasRequest { ListAddress = "duyuru@benim.com", Alias = ekAdres },
            CancellationToken.None));

        Assert.Equal("auth.domain_forbidden", hata.MessageKey);
    }

    /// <summary>
    /// Ters yön: ek adres KENDİ alan adında ama LİSTE başkasının. Yalnız alias doğrulansaydı,
    /// kiracı başkasının listesine ikinci bir kapı açabilir (ya da var olanı söküp hizmetini
    /// kesebilirdi).
    /// </summary>
    [Fact]
    public async Task BaskasininListesineEkAdresBaglanamaz()
    {
        var denetleyici = Duzenek();

        var hata = await Assert.ThrowsAsync<AppException>(() => denetleyici.AddListAliasAsync(
            new MailListAliasRequest { ListAddress = "duyuru@kurban.com", Alias = "info@benim.com" },
            CancellationToken.None));

        Assert.Equal("auth.domain_forbidden", hata.MessageKey);
    }

    /// <summary>Kaldırma ucu AYNI iki kapıdan geçer — biri açık kalsa hizmet kesintisi yolu olurdu.</summary>
    [Theory]
    [InlineData("duyuru@benim.com", "duyuru@kurban.com")]
    [InlineData("duyuru@kurban.com", "info@benim.com")]
    [InlineData("duyuru@benim.com", "info@kotubenim.com")]
    public async Task EkAdresKaldirmaDaAyniDenetimdenGecer(string liste, string ekAdres)
    {
        var denetleyici = Duzenek();

        var hata = await Assert.ThrowsAsync<AppException>(() =>
            denetleyici.RemoveListAliasAsync(liste, ekAdres, CancellationToken.None));

        Assert.Equal("auth.domain_forbidden", hata.MessageKey);
    }

    /// <summary>
    /// KENDİ alan adı kapıdan GEÇER. Bu test olmasa, "hepsini reddet" diyen bozuk bir denetim de
    /// yukarıdaki testleri yeşil geçerdi — yani özelliğin hiç çalışmadığı bir dünya da yeşildi.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden "hata YOK" değil de "şu hata DEĞİL":</b> kapının ötesinde gerçek bir Stalwart
    /// çağrısı var ve bu makinede canlı sunucu yok. Kapının ötesinde ne olduğu MAKİNEYE bağlı
    /// (ilk sürüm <c>mail.lists_unsupported</c> bekliyordu ve bu geliştirme makinesinde KIRILDI:
    /// burada Stalwart ikilileri kurulu, dolayısıyla ön koşul geçiyor). Testin doğrulaması
    /// gereken tek şey KAPININ KENDİSİ: kapsam denetimi bu isteği reddetmiyor.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("duyuru@benim.com", "info@benim.com")]
    [InlineData("duyuru@benim.com", "info")]
    [InlineData("duyuru@benim.com", "  INFO  ")]
    public async Task KendiAlanAdiKapidanGecer(string liste, string ekAdres)
    {
        var denetleyici = Duzenek();

        var hata = await Record.ExceptionAsync(() => denetleyici.AddListAliasAsync(
            new MailListAliasRequest { ListAddress = liste, Alias = ekAdres },
            CancellationToken.None));

        KapsamKapisiniGectiginiDogrula(hata);
    }

    /// <summary>Yönetici kapsam denetimine takılmaz (izinli küme <c>null</c> döner).</summary>
    [Fact]
    public async Task YoneticiKapsamDenetimineTakilmaz()
    {
        var denetleyici = Duzenek(rol: "Administrator");

        var hata = await Record.ExceptionAsync(() => denetleyici.AddListAliasAsync(
            new MailListAliasRequest { ListAddress = "duyuru@kurban.com", Alias = "info@kurban.com" },
            CancellationToken.None));

        KapsamKapisiniGectiginiDogrula(hata);
    }

    /// <summary>
    /// İstek kapsam denetimini GEÇTİ mi? Kapının ötesindeki sonuç bu makinede belirsizdir
    /// (canlı Stalwart yok, kurulu ikililer var), bu yüzden ölçülen tek şey reddedilmemiş olması.
    /// </summary>
    private static void KapsamKapisiniGectiginiDogrula(Exception? hata)
    {
        if (hata is AppException uygulamaHatasi)
        {
            Assert.NotEqual("auth.domain_forbidden", uygulamaHatasi.MessageKey);
        }
    }

    /// <summary>
    /// Ek adres (alias) uçları hiçbir ALTYAPI sınıfını parametre olarak almamalı.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bu uçlar bir süre <c>[FromServices] AdaPanel.Infrastructure.Mail.MailService</c> alıyordu:
    /// API katmanı somut bir altyapı sınıfına bağlanıyor, <c>MailServiceRouter</c> devre dışı
    /// kalıyor ve "desteklenmiyor" cevabı denetleyicinin içinde statik bir
    /// <c>MailService.IsInstalled()</c> çağrısıyla üretiliyordu. Artık iki metot da
    /// <see cref="IMailService"/>'te ve cevap diğer bütün liste uçlarıyla aynı yerden geliyor.
    /// </para>
    /// <para>
    /// <b>DÜRÜSTLÜK — kapsam:</b> aynı denetleyicideki <c>public-base-url</c> uçları HÂLÂ somut
    /// servisi alıyor. O bilerek kapsam dışı bırakıldı ve bu test onu kapsamıyor; bu yüzden kural
    /// "denetleyicinin tamamı" değil, ADI VERİLEN iki eylem üzerinden yazıldı. Genel kural gibi
    /// yazılsaydı ya yanlış yere kırmızı verirdi ya da gevşetilmek zorunda kalırdı.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(nameof(MailController.AddListAliasAsync))]
    [InlineData(nameof(MailController.RemoveListAliasAsync))]
    public void Alias_Uclari_Altyapi_Sinifina_Baglanmiyor(string eylemAdi)
    {
        var eylem = typeof(MailController).GetMethod(eylemAdi)!;
        var altyapiDerlemesi = typeof(MailService).Assembly;

        var bagliOlanlar = eylem.GetParameters()
            .Where(parametre => parametre.ParameterType.Assembly == altyapiDerlemesi)
            .Select(parametre => parametre.ParameterType.Name)
            .ToArray();

        Assert.Empty(bagliOlanlar);
    }

    /// <summary>
    /// MailEnable kurulu bir sunucuda ek adres uçları <c>mail.lists_unsupported</c> der.
    /// </summary>
    /// <remarks>
    /// Kullanıcının göreceği sebep DOĞRU olmalı: "posta sunucusu kurulu değil" YANLIŞ olurdu
    /// (sunucu kurulu, sadece dağıtım listesi desteklemeyen bir sunucu). Bu cevap eskiden
    /// denetleyicideki elle kontrolden geliyordu; artık diğer liste metotlarıyla aynı yerden,
    /// aynı anahtarla geliyor — yani tek bir yerin değişmesi hepsini birden değiştirir.
    /// </remarks>
    [Fact]
    public async Task MailEnable_Yedeginde_Alias_Uclari_Desteklenmiyor_Diyor()
    {
        var mailEnable = new MailEnableMailService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MailEnableMailService>.Instance);
        var istek = new MailListAliasRequest
        {
            ListAddress = "duyuru@ornek.com",
            Alias = "info@ornek.com"
        };

        var ekleme = await Assert.ThrowsAsync<AppException>(
            () => mailEnable.AddMailListAliasAsync(istek));
        var kaldirma = await Assert.ThrowsAsync<AppException>(
            () => mailEnable.RemoveMailListAliasAsync(istek));

        Assert.Equal("mail.lists_unsupported", ekleme.MessageKey);
        Assert.Equal("mail.lists_unsupported", kaldirma.MessageKey);

        // Var olan liste uçlarıyla AYNI anahtar: ikisi ayrışırsa arayüz iki farklı metin gösterir.
        var mevcut = await Assert.ThrowsAsync<AppException>(
            () => mailEnable.DeleteMailListAsync("duyuru@ornek.com"));
        Assert.Equal(mevcut.MessageKey, ekleme.MessageKey);
    }

    // --- düzenek ---

    /// <remarks>
    /// EK ADRES UÇLARI ARTIK ARAYÜZDEN GEÇİYOR. Eskiden düzenek ikinci bir değer (somut
    /// <c>MailService</c>, bilerek <c>null</c>) döndürüyordu çünkü uçlar onu <c>[FromServices]</c>
    /// ile alıyordu. Artık <see cref="PatlayanMailServisi"/> hem enjekte edilen hem de çağrılan
    /// servistir; kapıyı geçen bir istek ona ulaşıp <c>NotSupportedException</c> alır — ki
    /// <see cref="KapsamKapisiniGectiginiDogrula"/> bunu "kapıyı geçti" olarak kabul eder.
    /// KAZANÇ, düzenek sadeliğinden ibaret değil: sonuç artık MAKİNEDEN BAĞIMSIZ. Önceki hâlde
    /// kapının ötesi <c>MailService.IsInstalled()</c>'a, yani makinede Stalwart ikililerinin
    /// kurulu olup olmamasına bağlıydı — bu, bu depoda bir testi zaten kırmıştı.
    /// </remarks>
    private static MailController Duzenek(string rol = "Customer")
    {
        var kullanici = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, rol)
            ],
            "test"));

        var denetleyici = new MailController(
            new PatlayanMailServisi(),
            new PatlayanWebmail(),
            new SahteKapsam(),
            new PatlayanSiteServisi(),
            new PatlayanDenetimKaydi())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = kullanici }
            }
        };

        return denetleyici;
    }

    /// <summary>Kiracının izinli alan adı KÜMESİ: yalnız "benim.com". Alt alan adı AYRI bir domaindir.</summary>
    private sealed class SahteKapsam : ICustomerScopeService
    {
        public Task<IReadOnlyCollection<string>> GetAllowedMailDomainsAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<string>>(["benim.com"]);

        public Task<IReadOnlyCollection<string>> GetAssignedSitesAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetParentSubscriptionAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CustomerScope> GetScopeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetScopeAsync(Guid userId, IEnumerable<string> siteNames, int maxSites, int maxDiskMb, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExtraLimitsAsync(Guid userId, int maxDatabases, int maxMailboxes, int maxScheduledTasks, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveUserAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveSiteFromAllScopesAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DiskUsage> GetDiskUsageMbAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetOwnerAsync(Guid userId, Guid? ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetOwnerAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetOwnedUserIdsAsync(Guid ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerSitesAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> GetResellerMailDomainsAsync(Guid resellerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> GetSiteOwnerUserIdAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetParentSubscriptionAsync(Guid userId, Guid? parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubUserIdsAsync(Guid parentUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetSubUserSectionsAsync(Guid userId, IReadOnlyCollection<string>? sections, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>?> GetSubUserSectionsAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPlanAsync(Guid userId, string? planId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetExpiryAsync(Guid userId, DateTime? expiresAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SuspendAsync(Guid userId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResumeAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSuspendedUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> GetSubscriptionUserIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// Hiçbir posta işi YAPMAYAN servis: bu testlerin ölçtüğü şey KAPI (kapsam denetimi), kapının
    /// ötesindeki Stalwart çağrısı değil. Kapıya takılan istek buraya HİÇ ulaşmaz; kapıyı geçen
    /// istek ulaşır ve <c>NotSupportedException</c> alır — o da "geçti" demektir.
    /// </summary>
    private sealed class PatlayanMailServisi : IMailService
    {
        public Task<MailWorkspaceDto> AddMailListAliasAsync(MailListAliasRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> RemoveMailListAliasAsync(MailListAliasRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> GetWorkspaceAsync(string? domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> ConfigureAsync(ConfigureMailRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> CreateDomainAsync(CreateMailDomainRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> DeleteDomainAsync(string domainName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> CreateAccountAsync(CreateMailAccountRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> DeleteAccountAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> SetAccountPasswordAsync(SetMailAccountPasswordRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> CreateAliasAsync(CreateMailAliasRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> DeleteAliasAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> CreateMailListAsync(CreateMailListRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> SetMailListRecipientsAsync(SetMailListRecipientsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> DeleteMailListAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<MailListenerDto>> GetListenersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> SetDomainQuotaAsync(SetMailDomainQuotaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailOutboundLimitDto> GetOutboundLimitAsync(string domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailOutboundLimitDto> SetOutboundLimitAsync(string domain, int messagesPerHour, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailWorkspaceDto> SetAccountQuotaAsync(SetMailAccountQuotaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailDnsDto> GetDnsRecordsAsync(string domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailDiagnosticsDto> GetDiagnosticsAsync(string domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailCertificateDto> GetCertificateStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailCertificateDto> IssueCertificateAsync(IssueMailCertificateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailSpamSettingsDto> GetSpamSettingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailSpamSettingsDto> UpdateSpamSettingsAsync(UpdateMailSpamSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailQueueDto> GetQueueAsync(int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailQueueMessageDto?> GetQueueMessageAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteQueueMessageAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailRelayDto> GetRelayAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailRelayDto> SetRelayAsync(SetMailRelayRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailClientSettingsDto?> GetClientSettingsAsync(string domain, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailAutoResponderDto> GetAutoResponderAsync(string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailAutoResponderDto> SetAutoResponderAsync(SetMailAutoResponderRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailCertificateDto?> SyncCertificateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> EnsureServerHostnameAsync(bool applyChanges = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PatlayanWebmail : IWebmailService
    {
        public Task<WebmailOpenDto> OpenAsync(string publicHost, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WebmailDiagnosticsDto> DiagnoseAsync(string publicHost, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SecureWebmailForDomainAsync(string domain, string contactEmail, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PatlayanSiteServisi : IIisSiteService
    {
        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Kayıt yalnız BAŞARILI işlemden sonra yazılır; bu testlerin hiçbiri oraya varmaz.</summary>
    private sealed class PatlayanDenetimKaydi : IAuditService
    {
        public Task WriteAsync(string action, string resource, string details, Guid? userId, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WriteForSubscriptionAsync(string action, string resource, string details, Guid? userId, Guid? subscriptionUserId, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<AuditLogDto>> GetRecentAsync(int take, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditLogPageDto> SearchAsync(AuditLogQuery query, AuditVisibility visibility, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<AuditLogDto> StreamAsync(AuditLogQuery query, AuditVisibility visibility, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditVisibility> ResolveVisibilityAsync(Guid userId, UserRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditRetentionSettingsDto> GetRetentionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuditRetentionSettingsDto> SetRetentionAsync(int retentionDays, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> PurgeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
