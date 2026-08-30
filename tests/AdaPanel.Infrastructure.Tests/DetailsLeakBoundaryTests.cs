using System.Text;
using System.Text.Json;
using AdaPanel.Api.Middleware;
using AdaPanel.Core.Common;
using AdaPanel.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// <see cref="AppException.Details"/> SIZINTI KANALININ kapali kaldigini sinar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden bu dosya var:</b> 0.8.7'de MariaDB root parolasi <c>Details</c> uzerinden HTTP yanit
/// govdesine donuyordu. O TEK vaka cagri yerinde (<c>BuildProcessDetails</c>) maskelendi, ama
/// KANALIN KENDISI acik kaldi: depoda ~40 ayri yer <c>Details</c> uretiyor ve bunlarin dordu
/// (<c>AppStoreService</c>, <c>DnsService</c>, <c>FtpService</c>,
/// <c>ComponentBootstrapper.BuildCommandDetails</c>) hic maskelenmiyordu. "Ayni kural kirk yerde"
/// bu depoda defalarca olculmus bir hata sinifidir: biri duzelir, oteki sessizce acik kalir.
/// </para>
/// <para>
/// <b>Bu yuzden testler cagri yerlerini degil SINIRLARI olcer.</b> Korunan uc sinir:
/// (1) <see cref="AppException"/> kurucusu -- <c>Details</c>'in uretilebilecegi TEK yer,
/// (2) HTTP yanit govdesi -- <c>ApiExceptionHandlingMiddleware</c>,
/// (3) ilerleme kaydi -- <c>OperationProgressService</c>, ki bu yol ara katmandan GECMEZ.
/// Dorduncu sinir olan denetim kaydi (<c>AuditWriter</c>) zaten korunuyordu ve
/// <c>AuditSecretRedactorTests</c> tarafindan olculuyor.
/// </para>
/// <para>
/// <b>FAZLA KAPATMA da olculur.</b> "Her seyi maskele" cozumu butun sir testlerini yesil yakar ve
/// paneli teshis edilemez hale getirir: nginx baslatma hatasi, yedek geri yukleme uyarisi ve
/// bilesen indirme 404'u <c>Details</c>'in VAR OLMA SEBEBIDIR. Nobetci testler bu bilginin HALA
/// gectigini kanitlar.
/// </para>
/// </remarks>
public sealed class DetailsLeakBoundaryTests
{
    // ------------------------------------------------------------------------------------
    // SINIR 1 -- AppException kurucusu (TEK KAYNAK)
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// OLCULMUS cagri: mariadb kurulumu <c>--password="&lt;root&gt;"</c> ile calisir ve
    /// basarisizlikta bu dizge <c>Details</c>'e konur.
    /// </summary>
    /// <remarks>
    /// Bu test BILEREK <c>BuildProcessDetails</c>'i cagirmaz: o cagri yeri kendi icinde zaten
    /// maskeliyor ve testi ondan gecirmek, kurucunun korumasini olcmek yerine ESKI yamayi olcerdi.
    /// Burada olculen sey, HERHANGI bir cagri yerinin ham dizge gondermesi durumunda kanalin
    /// yine de kapali olmasidir.
    /// </remarks>
    [Fact]
    public void Sinir1_AppException_ham_parolayi_Details_e_HIC_ALMAZ()
    {
        const string parola = "R00t-Parola-Gizli!";

        var hata = new AppException(
            "software.process_failed",
            1,
            500,
            $"File=mariadb-install-db.exe\r\nArguments=--service=\"AdaMariaDB\" --password=\"{parola}\" --port=3306");

        Assert.DoesNotContain(parola, hata.Details, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Mask, hata.Details, StringComparison.Ordinal);

        // Teshis degeri KAYBOLMAZ: hangi arac, hangi alan, hangi port gorunmeye devam eder.
        Assert.Contains("mariadb-install-db.exe", hata.Details, StringComparison.Ordinal);
        Assert.Contains("--port=3306", hata.Details, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="AppException.Gone"/> ayri bir fabrikadir ama AYNI kurucudan gecer.
    /// Ayri bir yol olsaydi maskelemeyi atlardi; bu test o varsayimi olcer.
    /// </summary>
    [Fact]
    public void Sinir1_Gone_fabrikasi_da_maskeler()
    {
        var hata = AppException.Gone("mail.mailbox_not_found", "connectionString=Server=x;Password=Gizli-1!");

        Assert.DoesNotContain("Gizli-1!", hata.Details, StringComparison.Ordinal);
    }

    /// <summary>
    /// FAZLA KAPATMA NOBETCISI. Bu test kirmizi yanarsa koruma bilgiyi de kesiyordur ve panel
    /// teshis edilemez hale gelmistir -- sir testlerinin yesil olmasi bunu telafi ETMEZ.
    /// </summary>
    [Theory]
    [InlineData("nginx: [emerg] bind() to 0.0.0.0:80 failed (10013: access permissions)")]
    [InlineData("Restore warning: database 'ada_db' already exists; 3 tables skipped.")]
    [InlineData("Url=https://downloads.mariadb.org/f/mariadb-11.4.2/winx64/mariadb.msi\r\nStatusCode=404 Not Found")]
    [InlineData("ExitCode=1\r\nStandardError=The service did not respond in time.")]
    [InlineData("Mailbox password was changed for user bob.")]
    [InlineData("Bearer token missing")]
    [InlineData("Site 'ornek.com' has 3 bindings; port 443 is already owned by 'baska.com'.")]
    public void Sinir1_MESRU_teshis_OLDUGU_GIBI_gecer(string teshis) =>
        Assert.Equal(teshis, new AppException("x", 1, 500, teshis).Details);

    // ------------------------------------------------------------------------------------
    // SINIR 2 -- HTTP yanit govdesi
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// UCTAN UCA OLCUM: istisna gercekten ara katmandan gecirilir ve YAZILAN JSON GOVDE okunur.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Neden govde okunuyor da <c>Details</c> ozelligine bakilmiyor: sizinti "ozellikte ne yaziyor"
    /// degil "istemciye ne gitti" sorusudur. 0.8.7 hatasinin tam sekli buydu -- deger dogru yerde
    /// duruyordu, sorun onun YAZILMASIYDI.
    /// </para>
    /// <para>
    /// 409 secildi (500 degil): 500'de ara katman arka planda hata raporu kuyruklar ve o gorev
    /// testin bitisiyle yarisirdi. Maskeleme durum kodundan bagimsizdir, bu yuzden olcum kaybi yok.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Sinir2_yanit_govdesinde_sir_YOK_ama_teshis_VAR()
    {
        const string parola = "Yanit-Govde-Parolasi!";
        var govde = await AraKatmandanGecirAsync(new AppException(
            "software.process_failed",
            409,
            409,
            $"nginx: [emerg] bind() failed\r\nArguments=--password=\"{parola}\" --port=8080"));

        Assert.DoesNotContain(parola, govde, StringComparison.Ordinal);

        // Yanit HALA kullaniciya bir sey soylemeli: mesaj anahtari ve mesru teshis gecmeli.
        Assert.Contains("software.process_failed", govde, StringComparison.Ordinal);
        Assert.Contains("bind() failed", govde, StringComparison.Ordinal);
        Assert.Contains("--port=8080", govde, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ara katmanin KENDI korumasi -- <c>AppException</c>'a hic ugramamis bir dize icin.
    /// </summary>
    /// <remarks>
    /// <c>WriteResponseAsync</c>'in sozlesmesi "keyfi bir dize al ve istemciye yaz"dir. Bugun tek
    /// besleyicisi <c>AppException.Details</c> (yani zaten maskeli), ama yarin bir catch bloguna
    /// eklenen ham <c>exception.Message</c> sessizce disari gecerdi. Bu testi yazmak icin
    /// <c>InvalidOperationException</c> yolu kullanilir: o yol <c>Details</c> ile HIC temas etmez.
    /// </remarks>
    [Fact]
    public async Task Sinir2_AppException_OLMAYAN_yol_da_maskelenir()
    {
        const string sir = "Baglanti-Dizgesi-Parolasi!";
        var govde = await AraKatmandanGecirAsync(
            new InvalidOperationException($"Server=127.0.0.1;Uid=root;Pwd={sir};SslMode=None"));

        Assert.DoesNotContain(sir, govde, StringComparison.Ordinal);
    }

    private static async Task<string> AraKatmandanGecirAsync(Exception firlatilan)
    {
        var saglayici = new ServiceCollection().BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = saglayici
        };
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/software/install";
        context.Response.Body = new MemoryStream();

        var araKatman = new ApiExceptionHandlingMiddleware(
            _ => throw firlatilan,
            NullLogger<ApiExceptionHandlingMiddleware>.Instance,
            saglayici.GetRequiredService<IServiceScopeFactory>());

        await araKatman.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var okuyucu = new StreamReader(context.Response.Body, Encoding.UTF8);
        var ham = await okuyucu.ReadToEndAsync();

        // Govde GERCEKTEN yazilmis olmali; bos govdede her Assert.DoesNotContain bedava gecerdi.
        Assert.False(string.IsNullOrWhiteSpace(ham), "Ara katman yanit govdesi yazmadi; olcum yapilamadi.");
        using var belge = JsonDocument.Parse(ham);
        Assert.False(belge.RootElement.GetProperty("success").GetBoolean());

        return ham;
    }

    // ------------------------------------------------------------------------------------
    // SINIR 3 -- ilerleme kaydi (ara katmandan GECMEZ)
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// <c>GET /api/v1/software/progress</c> istemciye <see cref="OperationProgressService.Snapshot"/>
    /// dondurur ve bu yol hata ara katmanina HIC ugramaz. Ara katmana konan koruma burayi
    /// KAPSAMAZ; bu yuzden ayri olculur.
    /// </summary>
    [Fact]
    public void Sinir3_ilerleme_kaydinda_sir_YOK_ama_teshis_VAR()
    {
        const string parola = "Ilerleme-Parolasi!";
        var ilerleme = new OperationProgressService();

        ilerleme.Begin("software.install");
        ilerleme.StartStep("MariaDB");
        ilerleme.CompleteStep(
            "MariaDB",
            success: false,
            $"software.process_failed\r\nArguments=--password=\"{parola}\" --port=3306");
        ilerleme.Fail($"Server=127.0.0.1;Uid=root;Pwd={parola};");

        var anlik = ilerleme.Snapshot();
        var adim = Assert.Single(anlik.Steps);

        Assert.DoesNotContain(parola, adim.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(parola, anlik.Error, StringComparison.Ordinal);

        // Kullanici hangi adimin neden dustugunu HALA gorebilmeli.
        Assert.Equal("failed", adim.Status);
        Assert.Contains("software.process_failed", adim.Detail, StringComparison.Ordinal);
        Assert.Contains("--port=3306", adim.Detail, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------
    // KAPSAM -- panelin GERCEKTEN urettigi sir bicimleri
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Her satir, kaynakta olculmus bir bicimdir; tahmin degildir.
    /// </summary>
    [Theory]
    // ComponentBootstrapper.cs: mariadb-install-db --password="<root>"
    [InlineData("--datadir=\"C:\\d\" --service=\"AdaMariaDB\" --password=\"R00t-Gizli!\" --port=3306", "R00t-Gizli!")]
    // AppStoreService.cs:397 -- WordPress yonetici parolasi
    [InlineData("\"wp.phar\" core install --admin_password=\"Wp!aabb1122\" --admin_email=\"a@b\"", "Wp!aabb1122")]
    // ComponentBootstrapper.cs:3326 -- BITISIK ad; eski desen bunu KACIRIYORDU
    [InlineData("PGPASSWORD=PgGizli1", "PgGizli1")]
    // DatabaseService.cs -- MYSQL_PWD ortam degiskeni
    [InlineData("MYSQL_PWD=MyGizli1", "MyGizli1")]
    // .NET ozellik adlari PascalCase BITISIKTIR; eski desen butun bu BICIMI kaciriyordu
    [InlineData("SmtpPassword: Smtp-Gizli-1", "Smtp-Gizli-1")]
    [InlineData("AdminPassword=Admin-Gizli-1", "Admin-Gizli-1")]
    [InlineData("WebhookToken=Wh-Gizli-1234567", "Wh-Gizli-1234567")]
    // Baglanti dizgesi
    [InlineData("Server=127.0.0.1;Port=3306;Uid=root;Pwd=Gizli-Baglanti-1", "Gizli-Baglanti-1")]
    // Adrese gomulu kimlik: Git dagitimi, yedek hedefi, PostgreSQL
    [InlineData("https://user:ghp_ABCDEFGHIJKLMNOP@github.com/o/r.git", "ghp_ABCDEFGHIJKLMNOP")]
    [InlineData("ftp://adakullanici:FtpHedefParola1@yedek.example.com/klasor", "FtpHedefParola1")]
    [InlineData("postgresql://ada:PgGizli1@127.0.0.1:5432/db", "PgGizli1")]
    // Webhook: sir YOLUN icinde
    [InlineData("https://hooks.slack.com/services/T00000000/B00000000/XXXXXXXXXXXXXXXXXXXX", "XXXXXXXXXXXXXXXXXXXX")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWx", "AbCdEfGhIjKlMnOpQrStUvWx")]
    // Stalwart TLS ozel anahtari
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEAxGIZLIKKEY123\n-----END RSA PRIVATE KEY-----", "MIIEpAIBAAKCAQEAxGIZLIKKEY123")]
    // Kimlik basligi
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.abcdefghijklmnop", "eyJhbGciOiJIUzI1NiJ9.abcdefghijklmnop")]
    public void Panelin_urettigi_sir_bicimleri_MASKELENIR(string girdi, string sizmamasiGereken)
    {
        var hata = new AppException("x", 1, 500, girdi);

        Assert.DoesNotContain(sizmamasiGereken, hata.Details, StringComparison.Ordinal);
    }

    /// <summary>
    /// KISA ama COK ANLAMLI adlar gevsek kola KONMADI: bunlar baska kelimelerin icinde gecer ve
    /// gevsek eslesme mesru teshisi maskelerdi. Bu test o kararin bozulmadigini olcer.
    /// </summary>
    [Theory]
    [InlineData("Bypass=true")]
    [InlineData("MaxPasses=3")]
    [InlineData("FTPS requirement set to True.")]
    public void Kisa_ve_cok_anlamli_adlar_FAZLA_maskelenmez(string girdi) =>
        Assert.Equal(girdi, SecretRedactor.Redact(girdi));

    // ------------------------------------------------------------------------------------
    // WEBHOOK ADRESI -- denetim kaydinda sema+konak kalir, yol gider
    // ------------------------------------------------------------------------------------

    [Fact]
    public void RedactUrl_yolu_atar_konagi_KORUR()
    {
        var sonuc = SecretRedactor.RedactUrl("https://kendi-sunucum.example.com/hooks/ADAPANEL-GIZLI-YOL?k=abc");

        Assert.DoesNotContain("ADAPANEL-GIZLI-YOL", sonuc, StringComparison.Ordinal);
        Assert.DoesNotContain("k=abc", sonuc, StringComparison.Ordinal);

        // "Alarmlar nereye gidiyordu" sorusu HALA cevaplanabilir olmali.
        Assert.Contains("kendi-sunucum.example.com", sonuc, StringComparison.Ordinal);
        Assert.StartsWith("https://", sonuc, StringComparison.Ordinal);
    }

    /// <summary>Adrese gomulu kimlik <see cref="Uri.Authority"/> ile zaten dusertir; olculur.</summary>
    [Fact]
    public void RedactUrl_gomulu_kimligi_de_atar()
    {
        var sonuc = SecretRedactor.RedactUrl("https://bot:Gizli-Bot-1@ornek.com/hook/abc");

        Assert.DoesNotContain("Gizli-Bot-1", sonuc, StringComparison.Ordinal);
        Assert.Contains("ornek.com", sonuc, StringComparison.Ordinal);
    }

    /// <summary>Bos webhook ("kaldirildi") ile dolu webhook AYIRT EDILEBILIR kalmali.</summary>
    [Fact]
    public void RedactUrl_bos_degeri_bos_birakir() =>
        Assert.Equal(string.Empty, SecretRedactor.RedactUrl("  "));

    /// <summary>Cozulemeyen dizge sessizce GECMEZ; genel maskelemeye duser.</summary>
    [Fact]
    public void RedactUrl_adres_olmayan_dizgeyi_de_maskeler() =>
        Assert.DoesNotContain(
            "Gizli-1!",
            SecretRedactor.RedactUrl("bu bir adres degil password=Gizli-1!"),
            StringComparison.Ordinal);

    /// <summary>
    /// BAGLANTI TESTI -- kuralin CAGRI YERINDE gercekten devrede oldugunu olcer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Yukaridaki testler <see cref="SecretRedactor.RedactUrl"/>'un KENDISINI sinar. Ama kural
    /// dogru olup da uygulanmazsa hicbir ise yaramaz: <c>NotificationsController</c> webhook
    /// adresini denetim kaydina DUZ yaziyordu ve o satiri Operator rolu de okuyabiliyor
    /// (<c>GET /api/v1/audit-logs/search</c> ve <c>/export</c> uclarinda rol kisiti YOK;
    /// <c>AuditService.ResolveVisibilityAsync</c> Operator'e sunucu genelinde SINIRSIZ gorunurluk
    /// verir). Cagri yeri eski hâline dondurulurse burasi kirmizi yanar.
    /// </para>
    /// <para>
    /// Neden kaynak taramasi: ucu calistirmak bir veritabani ve kimlik baglami gerektirir; korunan
    /// sey ise tek bir davranis degil "webhook adresini ham yazma" KURALIDIR. Depoda bu idiyom
    /// zaten kullaniliyor (<c>AuditCoverageTests</c>).
    /// </para>
    /// </remarks>
    [Fact]
    public void Webhook_adresi_denetim_kaydina_HAM_yazilmaz()
    {
        var yol = Path.Combine(KaynakKoku(), "src", "AdaPanel.Api", "Controllers", "NotificationsController.cs");
        Assert.True(File.Exists(yol), $"Denetlenen dosya bulunamadi: {yol}");

        var kaynak = File.ReadAllText(yol);

        // Yorum satirlari haric tutulur: gerekcede webhook adresi ORNEK olarak geciyor.
        var kod = string.Join(
            "\n",
            kaynak.Split('\n').Where(satir => !satir.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.DoesNotContain("{oncesi.WebhookUrl}", kod, StringComparison.Ordinal);
        Assert.DoesNotContain("{settings.WebhookUrl}", kod, StringComparison.Ordinal);

        // Ve maskeleme GERCEKTEN uygulanmis olmali (adres alani tumden silinerek de "gecmis"
        // gorunebilirdi -- o zaman kayit isini yapmaz).
        Assert.Contains("RedactUrl(oncesi.WebhookUrl)", kod, StringComparison.Ordinal);
        Assert.Contains("RedactUrl(settings.WebhookUrl)", kod, StringComparison.Ordinal);

        // Adres HALA kayda giriyor olmali. Bu satir olmasaydi "webhook alanini tumden kaldir"
        // cozumu de yesil yanardi ve kayit "alarmlar nereye tasindi" sorusunu cevaplayamazdi.
        Assert.Contains("webhook '{", kod, StringComparison.Ordinal);
    }

    private static string KaynakKoku()
    {
        var dizin = new DirectoryInfo(AppContext.BaseDirectory);
        while (dizin is not null && !File.Exists(Path.Combine(dizin.FullName, "AdaPanel.sln")))
        {
            dizin = dizin.Parent;
        }

        Assert.NotNull(dizin);
        return dizin!.FullName;
    }

    // ------------------------------------------------------------------------------------
    // DAYANIKLILIK
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// GERI IZLEME PATLAMASI NOBETCISI.
    /// </summary>
    /// <remarks>
    /// Anahtar deseni "onek + sir kelimesi + sonek" bicimindedir; onek sinirsiz birakilsaydi
    /// eslesmeyen uzun bir cikti (8000 karakterlik hex/base64) her baslangic noktasi icin geri
    /// izleme yapar ve O(n²) olurdu. Bu, 1 saniyelik zaman asimini gercekten tetikleyebilirdi --
    /// ve zaman asimi metnin TAMAMINI atar, yani panel teshisini kaybederdi. Onek {0,64} ile
    /// sinirlandi; bu test o sinirin gerektigini degil, KORUNDUGUNU olcer.
    /// </remarks>
    [Fact]
    public void Uzun_eslesmeyen_cikti_zaman_asimina_ugramaz()
    {
        var uzun = string.Concat(Enumerable.Repeat("a1b2c3d4e5f6-_.", 600)); // ~9000 karakter

        var basla = DateTime.UtcNow;
        var sonuc = SecretRedactor.Redact(uzun);
        var sure = DateTime.UtcNow - basla;

        // Zaman asimina ugrasaydi sonuc TAMAMEN maskeye donerdi; metin oldugu gibi durmali.
        Assert.Equal(uzun, sonuc);
        Assert.True(sure < TimeSpan.FromSeconds(1), $"Maskeleme cok yavas: {sure}");
    }

    /// <summary>
    /// Maskeleme ETKISIZDIR (idempotent). Uc sinir ust uste uygulanir; degilse metin her gecisde
    /// biraz daha bozulur ve teshis sessizce erirdi.
    /// </summary>
    [Fact]
    public void Maskeleme_etkisizdir()
    {
        const string girdi = "File=setup.exe --password=\"Gizli-1!\" --port=3306";
        var bir = SecretRedactor.Redact(girdi);

        Assert.Equal(bir, SecretRedactor.Redact(bir));
    }
}
