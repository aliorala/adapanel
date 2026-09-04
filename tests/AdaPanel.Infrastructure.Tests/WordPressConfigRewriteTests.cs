using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.AppStore;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// "Veri Kopyala"da SESSİZCE YANLIŞ VERİTABANINA BAĞLANMA — kök nedenin kapatıldığının kanıtı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hata neydi:</b> yıkıcı <c>robocopy /MIR</c> aynalaması kaynağın <c>wp-config.php</c>'sini
/// hedefe getiriyor, panel sonra hedefin kendi DB bilgilerini o dosyaya geri yazıyordu. Geri yazım
/// düz bir <c>Regex.Replace</c>'ti: aranan <c>define('DB_NAME', …)</c> kalıbı yoksa
/// <b>içeriği değiştirmeden dönüyor ve kimseye söylemiyordu</b>. Sonuç, canlı hedef sitenin
/// staging'in veritabanına bağlanmaya devam etmesiydi — üstelik geri dönüşsüz aynalamadan SONRA.
/// </para>
/// <para>
/// <b>Bu dosya üç ayrı yüklemi doğrular:</b> (1) değiştirici artık "değiştiremedim" diyor,
/// (2) yazılamayacak yapılandırma YIKICI ADIMDAN ÖNCE yakalanıp iş DURDURULUYOR,
/// (3) durdurma kullanıcıya ULAŞAN bir kanaldan (istisna + çevrilebilir anahtar) bildiriliyor.
/// </para>
/// <para>
/// <b>Girdi biçimleri uydurulmadı.</b> WordPress'in kendi <c>wp-config-sample.php</c>'si
/// (<c>define( 'DB_NAME', 'database_name_here' );</c> — paren içinde boşluklu), resmî Docker
/// imajının <c>wp-config-docker.php</c>'si (<c>define( 'DB_NAME', getenv_docker(
/// 'WORDPRESS_DB_NAME', 'wordpress') );</c>) ve belgelenmiş "local-config" kalıbı
/// (<c>require</c> + <c>defined('DB_NAME') or define(...)</c>) kaynaklarından alındı.
/// </para>
/// </remarks>
public sealed class WordPressConfigRewriteTests : IDisposable
{
    private readonly List<string> _temizlenecek = [];

    // ================================================================================
    // 1) DEĞİŞTİRİCİ ARTIK "DEĞİŞTİREMEDİM" DİYOR
    // ================================================================================

    /// <summary>
    /// Gerçek dünyada karşılaşılan YAZILABİLİR biçimler — düzeltme çalışan hâli bozmamalı.
    /// </summary>
    /// <remarks>
    /// Bu olumlu kontrol olmasaydı "her şeye hayır de" diyen bir değiştirici de testleri geçerdi;
    /// özelliği kapatmak, düzeltmenin en sık görülen sahte biçimidir.
    /// </remarks>
    [Theory]
    // WordPress'in kendi wp-config-sample.php'si: paren içinde boşluklu, tek tırnak.
    [InlineData("define( 'DB_NAME', 'database_name_here' );")]
    // Elle yazılmış en yaygın biçim: boşluksuz.
    [InlineData("define('DB_NAME','eski_db');")]
    // Çift tırnak.
    [InlineData("define(\"DB_NAME\", \"eski_db\");")]
    // Karışık boşluk + sekme.
    [InlineData("define\t(  'DB_NAME'  ,\t'eski_db'  ) ;")]
    // Çok satırlı çağrı (biçimlendiriciler üretir).
    [InlineData("define(\n    'DB_NAME',\n    'eski_db'\n);")]
    // Değerde kaçışlı tırnak — eski kalıp bunu HİÇ eşleştiremiyordu.
    [InlineData("define('DB_NAME', 'es\\'ki_db');")]
    public void Yazilabilir_biciimlerde_deger_GERCEKTEN_degisiyor(string satir)
    {
        var icerik = "<?php\n" + satir + "\n";

        Assert.True(WordPressToolkitService.TryReplaceDefine(icerik, "DB_NAME", "hedef_db", out var sonuc, out var durum));
        Assert.Equal(WordPressToolkitService.DefineYazimDurumu.Yazildi, durum);

        // İÇERİK GERÇEKTEN DEĞİŞMİŞ OLMALI: "true döndü" yetmez, dosyaya yazılan şey önemlidir.
        Assert.NotEqual(icerik, sonuc);
        Assert.Contains("hedef_db", sonuc, StringComparison.Ordinal);
        Assert.DoesNotContain("eski_db", sonuc, StringComparison.Ordinal);
        Assert.DoesNotContain("database_name_here", sonuc, StringComparison.Ordinal);
    }

    /// <summary>
    /// Yazılamayan gerçek biçimler: eskiden hepsi SESSİZCE özdeş içerik döndürüyordu.
    /// </summary>
    [Theory]
    // Resmî Docker WordPress imajının wp-config-docker.php'si.
    [InlineData("define( 'DB_NAME', getenv_docker('WORDPRESS_DB_NAME', 'wordpress') );")]
    // Yalın getenv().
    [InlineData("define('DB_NAME', getenv('DB_NAME'));")]
    // Süper küresel diziler.
    [InlineData("define('DB_NAME', $_ENV['DB_NAME']);")]
    [InlineData("define('DB_NAME', $_SERVER['DB_NAME']);")]
    // Değer başka bir sabitten geliyor.
    [InlineData("define('DB_NAME', WP_DB_NAME);")]
    // Değişken.
    [InlineData("define('DB_NAME', $dbname);")]
    // wp-config bağlantı bilgisini HİÇ tanımlamıyor: require ile başka dosyadan geliyor.
    [InlineData("require_once __DIR__ . '/db-config.php';")]
    public void Duz_metin_define_yoksa_KALIP_YOK_denir_ve_icerik_DEGISMEZ(string satir)
    {
        var icerik = "<?php\n" + satir + "\n";

        Assert.False(WordPressToolkitService.TryReplaceDefine(icerik, "DB_NAME", "hedef_db", out var sonuc, out var durum));
        Assert.Equal(WordPressToolkitService.DefineYazimDurumu.KalipYok, durum);

        // Başarısızlıkta dosya YARIM YAZILMAZ.
        Assert.Equal(icerik, sonuc);
        Assert.DoesNotContain("hedef_db", sonuc, StringComparison.Ordinal);
    }

    /// <summary>
    /// YALNIZ YORUMDAKİ bir define "yazabildim" saydırmamalı — bu, sessiz hatanın en sinsi biçimi.
    /// </summary>
    /// <remarks>
    /// Kullanıcılar eski ayarlarını silmek yerine yorum satırına alır. Eski kod o satırı
    /// değiştiriyor, "oldu" sayıyor ve gerçek yapılandırma (aşağıdaki getenv) dokunulmadan
    /// kalıyordu: hedef, kaynağın veritabanına bağlanmaya devam ederdi.
    /// </remarks>
    [Theory]
    [InlineData("// define('DB_NAME', 'eski_db');")]
    [InlineData("#define('DB_NAME', 'eski_db');")]
    [InlineData("/* define('DB_NAME', 'eski_db'); */")]
    [InlineData("/**\n * Ornek: define('DB_NAME', 'eski_db');\n */")]
    public void Yalniz_YORUMDAKI_define_yazildi_SAYILMAZ(string yorum)
    {
        var icerik = "<?php\n" + yorum + "\ndefine('DB_NAME', getenv('DB_NAME'));\n";

        Assert.False(WordPressToolkitService.TryReplaceDefine(icerik, "DB_NAME", "hedef_db", out var sonuc, out var durum));
        Assert.Equal(WordPressToolkitService.DefineYazimDurumu.YalnizYorumda, durum);
        Assert.Equal(icerik, sonuc);
    }

    [Fact]
    public void Yorumdaki_ESKI_define_gercegin_yaninda_duruyorsa_GERCEK_olan_degisir()
    {
        // Olumlu kontrol: yorum atlama, çalışan yapılandırmayı bozmamalı.
        var icerik = "<?php\n// define('DB_NAME', 'cok_eski');\ndefine('DB_NAME', 'gercek_db');\n";

        Assert.True(WordPressToolkitService.TryReplaceDefine(icerik, "DB_NAME", "hedef_db", out var sonuc, out var durum));
        Assert.Equal(WordPressToolkitService.DefineYazimDurumu.Yazildi, durum);
        Assert.Contains("'hedef_db'", sonuc, StringComparison.Ordinal);

        // Yorum OLDUĞU GİBİ kalır: kullanıcının notunu değiştirmek bizim işimiz değil.
        Assert.Contains("// define('DB_NAME', 'cok_eski');", sonuc, StringComparison.Ordinal);
    }

    [Fact]
    public void Ayni_satirdaki_bir_URL_define_i_YORUM_sandirmaz()
    {
        // "Satırda // var mı" diye bakan basit bir denetim buradaki `http://` yüzünden gerçek
        // define'ı yorum sanar ve çalışan bir kopyalamayı reddederdi.
        var icerik = "<?php\ndefine('WP_HOME','http://ornek.com'); define('DB_NAME','gercek_db');\n";

        Assert.True(WordPressToolkitService.TryReplaceDefine(icerik, "DB_NAME", "hedef_db", out var sonuc, out var durum));
        Assert.Equal(WordPressToolkitService.DefineYazimDurumu.Yazildi, durum);
        Assert.Contains("'hedef_db'", sonuc, StringComparison.Ordinal);
        Assert.Contains("http://ornek.com", sonuc, StringComparison.Ordinal);
    }

    /// <summary>
    /// Belgelenmiş "local-config" kalıbı: gerçek değer <c>require</c> edilen dosyadan gelir,
    /// wp-config'teki define yalnızca VARSAYILANDIR ve değiştirilmesi hiçbir şeyi etkilemez.
    /// </summary>
    [Theory]
    [InlineData("defined('DB_NAME') or define('DB_NAME', 'varsayilan');")]
    [InlineData("if ( ! defined( 'DB_NAME' ) ) {\n\tdefine( 'DB_NAME', 'varsayilan' );\n}")]
    [InlineData("if (!defined(\"DB_NAME\")) define(\"DB_NAME\", \"varsayilan\");")]
    public void Kosullu_tanim_yazildi_SAYILMAZ(string kalip)
    {
        var icerik = "<?php\nrequire_once __DIR__ . '/wp-config-local.php';\n" + kalip + "\n";

        Assert.False(WordPressToolkitService.TryReplaceDefine(icerik, "DB_NAME", "hedef_db", out var sonuc, out var durum));
        Assert.Equal(WordPressToolkitService.DefineYazimDurumu.KosulluTanim, durum);
        Assert.Equal(icerik, sonuc);
    }

    [Fact]
    public void Uzaktaki_alakasiz_bir_defined_cagrisi_KOSULLU_saydirmaz()
    {
        // Pencere dar tutulmasaydı dosyanın başındaki her `defined()` çağrısı bütün define'ları
        // "koşullu" yapar, çalışan kopyalamalar reddedilirdi.
        var icerik = "<?php\nif ( ! defined( 'DB_NAME' ) ) { /* not */ }\n"
            + new string('\n', 200)
            + "define('DB_NAME', 'gercek_db');\n";

        Assert.True(WordPressToolkitService.TryReplaceDefine(icerik, "DB_NAME", "hedef_db", out _, out var durum));
        Assert.Equal(WordPressToolkitService.DefineYazimDurumu.Yazildi, durum);
    }

    /// <summary>Değerdeki PHP kaçışları GİDİŞ-DÖNÜŞ bozulmamalı.</summary>
    /// <remarks>
    /// Veri kopyalamada hedefin parolası önce OKUNUR sonra geri YAZILIR. Kaçış kuralı iki yönde
    /// tutmuyorsa parola sessizce bozulur ve canlı site veritabanına bağlanamaz — bu da tam olarak
    /// düzeltmeye çalıştığımız hata sınıfı.
    /// </remarks>
    [Theory]
    [InlineData("basit123")]
    [InlineData("tirnak'li")]
    [InlineData("ters\\bolu")]
    [InlineData("dolar$isareti")]           // eski kod bunu Regex yerine-koyma dili için kaçırmak zorundaydı
    [InlineData("hepsi'bir\\arada$3")]
    public void Parola_YAZILIP_geri_OKUNDUGUNDA_aynidir(string parola)
    {
        var icerik = "<?php\ndefine('DB_PASSWORD', 'ilk_deger');\n";

        Assert.True(WordPressToolkitService.TryReplaceDefine(icerik, "DB_PASSWORD", parola, out var yazilmis, out _));

        // İkinci tur: yazdığımız dosyadan aynı değeri geri okuyabilmeliyiz (kopyalamanın yaptığı iş).
        Assert.True(WordPressToolkitService.TryReplaceDefine(yazilmis, "DB_PASSWORD", "sonraki", out _, out var durum));
        Assert.Equal(WordPressToolkitService.DefineYazimDurumu.Yazildi, durum);

        // Ve OKUMA yolu da tam olarak aynı değeri vermeli — asıl gidiş-dönüş iddiası bu.
        Assert.True(WordPressToolkitService.TryReadDefine(yazilmis, "DB_PASSWORD", out var geriOkunan));
        Assert.Equal(parola, geriOkunan);
    }

    [Fact]
    public void Bos_parola_BULUNAMADI_ile_karistirilmaz()
    {
        // Parolasız yerel kullanıcı MEŞRUDUR. "Boş" ile "yok" aynı sayılırsa panel ya çalışan bir
        // kopyalamayı reddeder ya da bulunamayan bir parolayı boş diye geri yazıp siteyi kırar.
        var bosVar = "<?php\ndefine('DB_PASSWORD', '');\n";
        var hicYok = "<?php\ndefine('DB_NAME', 'x');\n";

        Assert.True(WordPressToolkitService.TryReplaceDefine(bosVar, "DB_PASSWORD", "yeni", out _, out _));
        Assert.False(WordPressToolkitService.TryReplaceDefine(hicYok, "DB_PASSWORD", "yeni", out _, out var durum));
        Assert.Equal(WordPressToolkitService.DefineYazimDurumu.KalipYok, durum);
    }

    // ================================================================================
    // 2) ÖN DENETİM: dört anahtarın HEPSİ zorunlu
    // ================================================================================

    [Fact]
    public void Tam_wp_config_te_yazilamayan_YOKTUR()
    {
        Assert.Empty(WordPressToolkitService.DbDefineYazilamayanlar(OrnekWpConfig("kaynak_db", "kaynak_kullanici")));
    }

    [Fact]
    public void Tek_bir_anahtar_yazilamiyorsa_bile_ON_DENETIM_bunu_bildirir()
    {
        // Dördü de zorunlu: yalnız DB_HOST kaynağınkinde kalırsa bağlantı yine yanlış sunucuya gider.
        var icerik = OrnekWpConfig("kaynak_db", "kaynak_kullanici")
            .Replace("define( 'DB_HOST', 'localhost' );", "define( 'DB_HOST', getenv('DB_HOST') );", StringComparison.Ordinal);

        var engeller = WordPressToolkitService.DbDefineYazilamayanlar(icerik);

        Assert.Single(engeller);
        Assert.Contains("DB_HOST", engeller[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Docker_imajinin_wp_config_i_DORT_anahtarda_da_yazilamaz_bulunur()
    {
        var engeller = WordPressToolkitService.DbDefineYazilamayanlar(DockerWpConfig());

        Assert.Equal(4, engeller.Count);
        Assert.All(
            WordPressToolkitService.DbBaglantiAnahtarlari,
            anahtar => Assert.Contains(engeller, engel => engel.StartsWith(anahtar + "=", StringComparison.Ordinal)));
    }

    // ================================================================================
    // 3) EN ÖNEMLİSİ: kullanıcı GERÇEKTEN uyarılıyor mu, ve yıkım GERÇEKTEN önleniyor mu
    // ================================================================================

    /// <summary>
    /// Geri yazım yapılamayacaksa <b>yıkıcı aynalama HİÇ ÇALIŞMAZ</b> ve kullanıcı çevrilebilir bir
    /// anahtarla uyarılır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bu, dosyanın en değerli yüklemi. Dizge araması DEĞİL: gerçek dizinler, gerçek
    /// <c>wp-config.php</c> dosyaları ve gerçek <c>CopyDataAsync</c> çağrısı. Hedefin dosyalarının
    /// DOKUNULMADIĞI ayrıca ölçülüyor — "hata verdi" ile "hiçbir şey bozulmadı" farklı iddialardır
    /// ve burada değerli olan ikincisidir.
    /// </para>
    /// <para>
    /// MUTASYON: ön denetim <c>if</c>'i kaldırılıp aynalamadan sonraya alındığında bu test kırmızı
    /// olur — hedefin ezilmiş dosyaları elde kalır.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Geri_yazim_yapilamayacaksa_AYNALAMA_HIC_CALISMAZ_ve_kullanici_UYARILIR()
    {
        var (kaynakKok, hedefKok) = IkiSiteKur(
            kaynakConfig: DockerWpConfig(),                                   // geri yazılamaz
            hedefConfig: OrnekWpConfig("hedef_db", "hedef_kullanici"));

        // Hedefte YALNIZ hedefte olan bir dosya: /MIR çalışsaydı SİLİNİRDİ.
        var hedeftekiOzelDosya = Path.Combine(hedefKok, "yalniz-hedefte.txt");
        await File.WriteAllTextAsync(hedeftekiOzelDosya, "canli musteri verisi");

        var servis = ServisKur(kaynakKok, hedefKok);

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.CopyDataAsync(
            "kaynak-site",
            new CopyWordPressDataRequest { TargetSiteName = "hedef-site", Files = true, Database = false },
            CancellationToken.None));

        // (a) Kullanıcıya ULAŞAN kanal: istisna + çevrilebilir anahtar. Panelin kopyalama ekranı
        //     başarı yanıtındaki mesajı okumaz, yalnız istisna metnini gösterir.
        Assert.Equal("wp.copy_config_not_rewritable", hata.MessageKey);
        Assert.Equal(409, hata.HttpStatusCode);

        // (b) SEBEP de söyleniyor: hangi anahtarlar yazılamıyor. Ayrıntı kullanıcıya gösteriliyor.
        Assert.Contains("DB_NAME", hata.Details, StringComparison.Ordinal);

        // (c) VE HİÇBİR ŞEY BOZULMADI — asıl iddia bu.
        Assert.True(File.Exists(hedeftekiOzelDosya), "Yikici aynalama CALISMIS: hedefin kendi dosyasi silinmis.");
        Assert.Equal("canli musteri verisi", await File.ReadAllTextAsync(hedeftekiOzelDosya));

        // (d) Hedefin wp-config'i hâlâ KENDİ veritabanını gösteriyor.
        var hedefConfigSonrasi = await File.ReadAllTextAsync(Path.Combine(hedefKok, "wp-config.php"));
        Assert.Contains("hedef_db", hedefConfigSonrasi, StringComparison.Ordinal);
        Assert.DoesNotContain("WORDPRESS_DB_NAME", hedefConfigSonrasi, StringComparison.Ordinal);

        // (e) Yedek klasörü bile açılmamış: iş, yıkıcı bölüme girmeden önce kesildi.
        var kardesler = Directory.GetDirectories(Path.GetDirectoryName(hedefKok.TrimEnd('\\'))!);
        Assert.DoesNotContain(kardesler, klasor => klasor.Contains("_kopyala_yedek_", StringComparison.Ordinal));
    }

    /// <summary>
    /// Hedefin KENDİ wp-config'i okunamıyorsa da yıkıcı adım çalışmaz.
    /// </summary>
    /// <remarks>
    /// Eski kod bu denetimi yalnız <c>DB_NAME</c> için ve yalnız "veritabanı da kopyalanacaksa"
    /// yolunda yapıyordu; yalnız dosya kopyalayan bir işte okunamayan parola
    /// <c>define('DB_PASSWORD','')</c> olarak geri yazılıp canlı siteyi açılamaz hâle getirirdi.
    /// </remarks>
    [Fact]
    public async Task Hedefin_kendi_DB_bilgileri_okunamiyorsa_AYNALAMA_HIC_CALISMAZ()
    {
        var (kaynakKok, hedefKok) = IkiSiteKur(
            kaynakConfig: OrnekWpConfig("kaynak_db", "kaynak_kullanici"),
            hedefConfig: "<?php\ndefine('DB_NAME', 'hedef_db');\ndefine('DB_USER', 'hedef_kullanici');\n");  // DB_PASSWORD YOK

        var hedeftekiOzelDosya = Path.Combine(hedefKok, "yalniz-hedefte.txt");
        await File.WriteAllTextAsync(hedeftekiOzelDosya, "canli musteri verisi");

        var servis = ServisKur(kaynakKok, hedefKok);

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.CopyDataAsync(
            "kaynak-site",
            new CopyWordPressDataRequest { TargetSiteName = "hedef-site", Files = true, Database = false },
            CancellationToken.None));

        Assert.Equal("wp.copy_target_config_unreadable", hata.MessageKey);
        Assert.Contains("DB_PASSWORD", hata.Details, StringComparison.Ordinal);
        Assert.True(File.Exists(hedeftekiOzelDosya), "Yikici aynalama CALISMIS.");
    }

    /// <summary>
    /// OLUMLU KONTROL — yazılabilir yapılandırmada kopyalama GERÇEKTEN yapılır ve hedef KENDİ
    /// veritabanında kalır.
    /// </summary>
    /// <remarks>
    /// Bu test olmasaydı "her kopyalamayı reddet" diyen bir düzeltme de yeşil kalırdı. Gerçek
    /// <c>robocopy</c> çalıştırılıyor: aynalamanın kaynağın wp-config'ini gerçekten getirdiği ve
    /// geri yazımın onu gerçekten düzelttiği ölçülüyor — zincirin tamamı.
    /// </remarks>
    [Fact]
    public async Task Yazilabilir_yapilandirmada_kopyalama_YAPILIR_ve_hedef_KENDI_veritabaninda_kalir()
    {
        var (kaynakKok, hedefKok) = IkiSiteKur(
            kaynakConfig: OrnekWpConfig("kaynak_db", "kaynak_kullanici"),
            hedefConfig: OrnekWpConfig("hedef_db", "hedef_kullanici"));

        await File.WriteAllTextAsync(Path.Combine(kaynakKok, "index.php"), "<?php // kaynaktan gelen");

        var servis = ServisKur(kaynakKok, hedefKok);

        var sonuc = await servis.CopyDataAsync(
            "kaynak-site",
            new CopyWordPressDataRequest { TargetSiteName = "hedef-site", Files = true, Database = false },
            CancellationToken.None);

        Assert.True(sonuc.CopiedFiles);
        Assert.True(sonuc.ConfigRewritten);

        // Dosyalar gerçekten aynalandı.
        Assert.True(File.Exists(Path.Combine(hedefKok, "index.php")));

        // KRİTİK: hedefin wp-config'i KENDİ veritabanını gösteriyor, kaynağınkini değil.
        var hedefConfig = await File.ReadAllTextAsync(Path.Combine(hedefKok, "wp-config.php"));
        Assert.Contains("'hedef_db'", hedefConfig, StringComparison.Ordinal);
        Assert.Contains("'hedef_kullanici'", hedefConfig, StringComparison.Ordinal);
        Assert.DoesNotContain("kaynak_db", hedefConfig, StringComparison.Ordinal);
        Assert.DoesNotContain("kaynak_kullanici", hedefConfig, StringComparison.Ordinal);

        // Geri dönüş yedeği duruyor ve hedefin ÖNCEKİ hâlini taşıyor.
        Assert.True(Directory.Exists(sonuc.BackupPath));
        var yedekConfig = await File.ReadAllTextAsync(Path.Combine(sonuc.BackupPath, "target-files", "wp-config.php"));
        Assert.Contains("'hedef_db'", yedekConfig, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ön denetim geçtiği hâlde geri yazım yine de olmazsa, hedef KAYNAĞIN veritabanına
    /// BIRAKILMAZ: hedefin kendi <c>wp-config.php</c>'si yedekten geri konur.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bu, kuşak-kemer yolu ve <b>ölü kod değil</b> — burada gerçekten koşturuluyor. Senaryo
    /// uydurma değil, o yolun var olma sebebinin ta kendisi: kaynağın <c>wp-config.php</c>'si ÖN
    /// DENETİMDEN SONRA, aynalamadan önce değişiyor. Yarış, veritabanı motoru sorgusuna
    /// bağlanarak birebir o aralıkta tetikleniyor.
    /// </para>
    /// <para>
    /// Ölçülen iddia: <b>bağlantı doğru kalır</b>. Kaynağın wp-config ayarları taşınmamıştır ve
    /// sonuç bunu <c>ConfigRewritten=false</c> ile söyler; ama canlı hedef site hiçbir koşulda
    /// staging'in veritabanına bağlanmaz.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task On_denetimden_SONRA_kaynak_degisirse_hedefin_KENDI_config_i_yedekten_geri_konur()
    {
        var (kaynakKok, hedefKok) = IkiSiteKur(
            kaynakConfig: OrnekWpConfig("kaynak_db", "kaynak_kullanici"),      // ön denetim GEÇER
            hedefConfig: OrnekWpConfig("hedef_db", "hedef_kullanici"));

        var kaynakConfigYolu = Path.Combine(kaynakKok, "wp-config.php");

        // Motor sorgusu ön denetim ile yıkıcı adım ARASINDA yapılıyor: yarışı tam oraya yerleştirir.
        var yarisliDb = new YarisliVeritabani(() => File.WriteAllText(kaynakConfigYolu, DockerWpConfig()));
        var servis = new WordPressToolkitService(new SahteSiteler(kaynakKok, hedefKok), yarisliDb, new SahteHostingSessiz());

        var sonuc = await servis.CopyDataAsync(
            "kaynak-site",
            new CopyWordPressDataRequest { TargetSiteName = "hedef-site", Files = true, Database = false },
            CancellationToken.None);

        Assert.True(yarisliDb.Tetiklendi, "Yaris kurulamadi: motor sorgusu hic yapilmamis, test yanlis kurulmus.");

        // (a) Kısmi başarı SÖYLENİYOR.
        Assert.False(sonuc.ConfigRewritten);

        // (b) ASIL İDDİA: hedef KENDİ veritabanında kaldı; kaynağın/staging'in DB'sine kaymadı.
        var hedefConfig = await File.ReadAllTextAsync(Path.Combine(hedefKok, "wp-config.php"));
        Assert.Contains("'hedef_db'", hedefConfig, StringComparison.Ordinal);
        Assert.Contains("'hedef_kullanici'", hedefConfig, StringComparison.Ordinal);
        Assert.DoesNotContain("WORDPRESS_DB_NAME", hedefConfig, StringComparison.Ordinal);
        Assert.DoesNotContain("getenv_docker", hedefConfig, StringComparison.Ordinal);
    }

    /// <summary>
    /// Klonlama da aynı sınıftan: yazılamayan kaynakta HİÇBİR KAYNAK OLUŞTURULMADAN durulur.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Klon yıkıcı değil ama sonucu daha ağır: yeni DB bilgileri yazılamazsa klon KAYNAĞIN — yani
    /// canlının — veritabanına bağlanır ve "staging" sanılan yerde yapılan her işlem üretim verisini
    /// değiştirir. Durmanın bedeli sıfır: site de veritabanı da henüz yok.
    /// </para>
    /// <para>
    /// <b>Girdi bilerek "local-config" kalıbı, Docker biçimi DEĞİL.</b> Ölçüldü: Docker biçiminde
    /// <c>DB_NAME</c> hiç OKUNAMADIĞI için klonlama zaten daha eski bir denetime
    /// (<c>wp.clone_config_unreadable</c>) takılıyor — yani o durum korumasız değildi. Buradaki kalıp
    /// aradaki gerçek boşluk: değer OKUNUYOR (<c>'varsayilan'</c>) ama YAZILMASI hiçbir işe yaramıyor,
    /// çünkü asıl değer <c>require</c> edilen dosyadan geliyor. Eski kod bu durumda sessizce
    /// "yazdım" sayıyordu.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Klonlama_kaynak_wp_config_yazilamiyorsa_HIC_BASLAMAZ()
    {
        var (kaynakKok, _) = IkiSiteKur(kaynakConfig: YerelConfigKalibi(), hedefConfig: OrnekWpConfig("hedef_db", "hedef_kullanici"));
        var siteler = new SahteSiteler(kaynakKok);
        var servis = new WordPressToolkitService(siteler, new PatlayanVeritabani(), new SahteHostingSessiz());

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.CloneAsync(
            "kaynak-site",
            new CloneWordPressRequest { TargetSiteName = "klon-site", TargetHostName = "klon.ornek.com" },
            CancellationToken.None));

        Assert.Equal("wp.clone_config_not_rewritable", hata.MessageKey);
        Assert.Equal(409, hata.HttpStatusCode);

        // HİÇBİR KAYNAK OLUŞTURULMADI: site açma çağrısı hiç yapılmadı.
        Assert.Equal(0, siteler.OlusturmaSayisi);
    }

    // ================================================================================
    // 4) BİLGİNİN KULLANICIYA GİDEN YOLU
    // ================================================================================

    [Fact]
    public void Sonuc_DTO_larinda_ConfigRewritten_VARDIR_ve_varsayilani_dogrudur()
    {
        // Varsayılan true: geri yazım GEREKMEYEN/başarılı durumlar dürüstçe "sorun yok" demeli.
        Assert.True(new WordPressCopyDataResultDto().ConfigRewritten);
        Assert.True(new WordPressCloneResultDto().ConfigRewritten);
    }

    [Fact]
    public void Denetleyici_ConfigRewritten_i_OKUYUP_farkli_anahtar_donduruyor()
    {
        // Bayrağın DTO'da durması yetmez; denetleyicinin onu okuyup kullanıcıya BAŞKA bir şey
        // söylemesi gerekir — aksi hâlde bilgi toplanır ve hiç kimseye ulaşmaz. (Aynı gerekçe:
        // WordPressCopyHonestyTests'teki UrlRewritten denetimi.)
        var kaynak = File.ReadAllText(Path.Combine(DepoKoku(), "src", "AdaPanel.Api", "Controllers", "WordPressController.cs"));

        Assert.Contains("result.ConfigRewritten", kaynak, StringComparison.Ordinal);
        Assert.Contains("wp.copy_config_not_merged", kaynak, StringComparison.Ordinal);
        Assert.Contains("wp.clone_config_not_written", kaynak, StringComparison.Ordinal);
    }

    [Fact]
    public void Yeni_mesaj_anahtarlarinin_TR_ve_EN_karsiligi_VARDIR()
    {
        // Çevirisi olmayan anahtar kullanıcıya HAM ANAHTAR olarak görünür ("wp.copy_config_not_rewritable"),
        // yani "açıkça söylendi" iddiası çöker.
        var tr = File.ReadAllText(Path.Combine(DepoKoku(), "frontend", "src", "i18n", "tr.json"));
        var en = File.ReadAllText(Path.Combine(DepoKoku(), "frontend", "src", "i18n", "en.json"));

        foreach (var anahtar in new[]
                 {
                     "wp.copy_config_not_rewritable",
                     "wp.copy_target_config_unreadable",
                     "wp.copy_config_not_merged",
                     "wp.clone_config_not_rewritable",
                     "wp.clone_config_not_written"
                 })
        {
            Assert.Contains($"\"{anahtar}\":", tr, StringComparison.Ordinal);
            Assert.Contains($"\"{anahtar}\":", en, StringComparison.Ordinal);
        }
    }

    // ================================================================================
    // düzenek
    // ================================================================================

    /// <summary>WordPress'in kendi <c>wp-config-sample.php</c>'sinin biçimi (boşluklu paren).</summary>
    private static string OrnekWpConfig(string db, string kullanici) => $"""
        <?php
        /** The name of the database for WordPress */
        define( 'DB_NAME', '{db}' );

        /** Database username */
        define( 'DB_USER', '{kullanici}' );

        /** Database password */
        define( 'DB_PASSWORD', 'gizli-parola' );

        /** Database hostname */
        define( 'DB_HOST', 'localhost' );

        /** Database charset */
        define( 'DB_CHARSET', 'utf8mb4' );

        $table_prefix = 'wp_';
        require_once ABSPATH . 'wp-settings.php';
        """;

    /// <summary>
    /// Belgelenmiş "local-config" kalıbı: değerler <c>require</c> edilen dosyadan gelir,
    /// wp-config'teki define'lar yalnızca varsayılandır.
    /// </summary>
    /// <remarks>
    /// Bu biçimin tehlikesi, <b>okunabilir ama yazılamaz</b> olmasıdır: <c>ReadDefine</c> pekâlâ
    /// <c>'varsayilan'</c> döner, dolayısıyla "okunamadı" denetimleri devreye girmez; oysa yazma
    /// hiçbir işe yaramaz.
    /// </remarks>
    private static string YerelConfigKalibi() => """
        <?php
        require_once __DIR__ . '/wp-config-local.php';

        defined('DB_NAME') or define('DB_NAME', 'varsayilan_db');
        defined('DB_USER') or define('DB_USER', 'varsayilan_kullanici');
        defined('DB_PASSWORD') or define('DB_PASSWORD', 'varsayilan_parola');
        defined('DB_HOST') or define('DB_HOST', 'localhost');

        $table_prefix = 'wp_';
        require_once ABSPATH . 'wp-settings.php';
        """;

    /// <summary>Resmî Docker WordPress imajının <c>wp-config-docker.php</c> biçimi.</summary>
    private static string DockerWpConfig() => """
        <?php
        function getenv_docker($env, $default) {
            return getenv($env) ?: $default;
        }

        define( 'DB_NAME', getenv_docker('WORDPRESS_DB_NAME', 'wordpress') );
        define( 'DB_USER', getenv_docker('WORDPRESS_DB_USER', 'example username') );
        define( 'DB_PASSWORD', getenv_docker('WORDPRESS_DB_PASSWORD', 'example password') );
        define( 'DB_HOST', getenv_docker('WORDPRESS_DB_HOST', 'mysql') );

        $table_prefix = 'wp_';
        require_once ABSPATH . 'wp-settings.php';
        """;

    private (string KaynakKok, string HedefKok) IkiSiteKur(string kaynakConfig, string hedefConfig)
    {
        // GEÇİCİ dizin — mutasyon denemeleri gerçek bir siteye dokunmasın diye. Bu yordamda
        // `robocopy /MIR` GERÇEKTEN çalışabilir; kapsamı buranın dışına çıkmamalı.
        var kok = Path.Combine(Path.GetTempPath(), "adapanel-wpcfg-" + Guid.NewGuid().ToString("N"));
        var kaynakKok = Path.Combine(kok, "kaynak");
        var hedefKok = Path.Combine(kok, "hedef");
        Directory.CreateDirectory(kaynakKok);
        Directory.CreateDirectory(hedefKok);
        _temizlenecek.Add(kok);

        File.WriteAllText(Path.Combine(kaynakKok, "wp-config.php"), kaynakConfig);
        File.WriteAllText(Path.Combine(hedefKok, "wp-config.php"), hedefConfig);
        return (kaynakKok, hedefKok);
    }

    private static WordPressToolkitService ServisKur(string kaynakKok, string hedefKok)
        => new(new SahteSiteler(kaynakKok, hedefKok), new PatlayanVeritabani(), new SahteHostingSessiz());

    public void Dispose()
    {
        foreach (var klasor in _temizlenecek)
        {
            try
            {
                if (Directory.Exists(klasor))
                {
                    Directory.Delete(klasor, recursive: true);
                }
            }
            catch (IOException)
            {
                // Temizlik başarısızlığı testin sonucunu değiştirmez.
            }
        }
    }

    private sealed class SahteSiteler(string kaynakKok, string? hedefKok = null) : IIisSiteService
    {
        public int OlusturmaSayisi { get; private set; }

        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default)
        {
            var kok = siteName.Contains("hedef", StringComparison.OrdinalIgnoreCase) ? hedefKok ?? kaynakKok : kaynakKok;
            return Task.FromResult(new SiteDto
            {
                Name = siteName,
                PhysicalPath = kok,
                ApplicationRootPath = kok,
                Bindings = [new SiteBindingDto { Protocol = "http", HostName = siteName + ".ornek.com", Port = 80 }]
            });
        }

        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<SiteDto>>([]);

        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default)
        {
            OlusturmaSayisi++;
            return Task.FromResult(new SiteDto { Name = request.Name, PhysicalPath = request.PhysicalPath });
        }

        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// Motor listesi BOŞ döner (varsayılana düşülür); veritabanına dokunan her çağrı PATLAR.
    /// </summary>
    /// <remarks>
    /// Bilerek: bu testler yalnız <c>Files</c> yolunu ölçüyor. Veritabanı yoluna kayan bir düzeltme
    /// sessizce geçmesin, HEMEN kırılsın.
    /// </remarks>
    private sealed class PatlayanVeritabani : IDatabaseService
    {
        public Task<IReadOnlyCollection<DatabaseEngineStatusDto>> GetEnginesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<DatabaseEngineStatusDto>>([]);

        public Task<DatabaseEngineStatusDto> RegisterCredentialsAsync(RegisterDatabaseCredentialsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> GetWorkspaceAsync(string engine, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> CreateDatabaseAsync(CreateDatabaseRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> ImportDatabaseAsync(ImportDatabaseRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteDatabaseAsync(string engine, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> CreateUserAsync(CreateDatabaseUserRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteUserAsync(string engine, string userName, string host, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> SetUserPasswordAsync(SetDatabaseUserPasswordRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> GrantAccessAsync(GrantDatabaseAccessRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseDumpResultDto> ExportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseDumpResultDto> ImportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseTransferResultDto> TransferDatabaseAsync(DatabaseTransferRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseDetailDto> GetDetailAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseCheckResultDto> CheckAndRepairAsync(string engine, string databaseName, IReadOnlyCollection<string> repairTables, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Databases.DatabaseHardeningReport> HardenAdminAccountsAsync(string engine, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// <see cref="PatlayanVeritabani"/> gibidir, ama motor sorgusunda verilen eylemi çalıştırır.
    /// </summary>
    /// <remarks>
    /// Motor sorgusu, ön denetim ile yıkıcı aynalama ARASINDAKİ tek dış çağrıdır; yarışı oraya
    /// bağlamak, "kaynak iş sırasında değişti" durumunu zamanlamaya güvenmeden BELİRLENİMCİ olarak
    /// üretir.
    /// </remarks>
    private sealed class YarisliVeritabani(Action eylem) : IDatabaseService
    {
        public bool Tetiklendi { get; private set; }

        public Task<IReadOnlyCollection<DatabaseEngineStatusDto>> GetEnginesAsync(CancellationToken cancellationToken = default)
        {
            Tetiklendi = true;
            eylem();
            return Task.FromResult<IReadOnlyCollection<DatabaseEngineStatusDto>>([]);
        }

        public Task<DatabaseEngineStatusDto> RegisterCredentialsAsync(RegisterDatabaseCredentialsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> GetWorkspaceAsync(string engine, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> CreateDatabaseAsync(CreateDatabaseRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> ImportDatabaseAsync(ImportDatabaseRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteDatabaseAsync(string engine, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> CreateUserAsync(CreateDatabaseUserRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteUserAsync(string engine, string userName, string host, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> SetUserPasswordAsync(SetDatabaseUserPasswordRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseWorkspaceDto> GrantAccessAsync(GrantDatabaseAccessRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseDumpResultDto> ExportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseDumpResultDto> ImportDumpAsync(DatabaseDumpRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseTransferResultDto> TransferDatabaseAsync(DatabaseTransferRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseDetailDto> GetDetailAsync(string engine, string databaseName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatabaseCheckResultDto> CheckAndRepairAsync(string engine, string databaseName, IReadOnlyCollection<string> repairTables, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdaPanel.Core.Dtos.Databases.DatabaseHardeningReport> HardenAdminAccountsAsync(string engine, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>wp-cli hep başarılı: adres değiştirme bu testlerin konusu değil.</summary>
    private sealed class SahteHostingSessiz : ISiteHostingSettingsService
    {
        public Task<ComposerRunResultDto> RunWpCliCommandAsync(string siteName, string command, CancellationToken cancellationToken = default)
            => Task.FromResult(new ComposerRunResultDto { ExitCode = 0 });

        public Task<SiteHostingSettingsDto> GetAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteHostingSettingsDto> UpdateAsync(string siteName, UpdateSiteHostingSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ComposerRunResultDto> RunComposerInstallAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ComposerRunResultDto> RunComposerCommandAsync(string siteName, string command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ComposerRunResultDto> RunArtisanCommandAsync(string siteName, string command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ComposerRunResultDto> RunNodeCommandAsync(string siteName, string command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SiteImportResultDto> ImportSiteArchiveAsync(string siteName, string archiveUrl, bool cleanFirst, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> GetQueueWorkerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> StartQueueWorkerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> StopQueueWorkerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> GetLaravelSchedulerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> StartLaravelSchedulerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QueueWorkerStatusDto> StopLaravelSchedulerAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static string DepoKoku()
    {
        var klasor = Path.GetFullPath(AppContext.BaseDirectory);
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            if (File.Exists(Path.Combine(klasor, "AdaPanel.sln")))
            {
                return klasor;
            }

            klasor = Path.GetFullPath(Path.Combine(klasor, ".."));
        }

        throw new DirectoryNotFoundException("Depo koku bulunamadi.");
    }
}
