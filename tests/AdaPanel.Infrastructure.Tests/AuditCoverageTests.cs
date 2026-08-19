using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Denetim kaydı KAPSAMINI kaynak üzerinden korur: durum değiştiren her ucun iz bıraktığını ve
/// hiçbir kayda parola/sır GEÇMEDİĞİNİ sabitler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden kaynak taraması:</b> burada korunan şey tek bir davranış değil, bir SINIF — "yeni bir
/// durum değiştiren uç eklendiğinde denetim kaydı yazmayı unutmak". Tek tek uçları çalıştıran
/// testler yalnız bugün var olanları korur; yarın eklenen uç sessizce izsiz kalırdı. Panelde bu
/// hata sınıfı zaten gerçekleşmişti: FilesController, MailController ve FtpController'da
/// <c>IAuditService</c> bağımlılığı bile yoktu.
/// </para>
/// <para>
/// <b>NEDEN LİSTE TERS ÇEVRİLDİ (bu turun asıl düzeltmesi):</b> testin önceki hâli ELLE YAZILMIŞ
/// beş dosyalık bir listeye bakıyordu. 63 denetleyiciden 5'ini koruyordu ve yeşil kalıyordu — yani
/// nöbetçinin kendisi, korumaya çalıştığı hata sınıfının (elle yazılan liste gerçeklikten kayar)
/// bir örneğiydi. Aynı depoda bu sınıf aynı gün üç kez daha ısırmıştı. Artık liste DİSKTEN
/// okunuyor: <c>Controllers</c> klasöründeki her dosya taranır, kapsanmayanlar <see cref="Muaflar"/>
/// içinde GEREKÇESİYLE durur. Yeni bir denetleyici eklendiğinde test onu kendiliğinden kapsar;
/// kapsanmayan bir şey varsa listede yazılı olmak zorundadır, yani kayma GÖRÜNÜR olur.
/// </para>
/// <para>
/// <b>Neden üç çağrı biçimi de sayılıyor:</b> eski tarama yalnız <c>AuditAsync(</c> arıyordu, oysa
/// depoda kaydın büyük kısmı <c>auditService.WriteAsync(</c> ile yazılıyor (70 çağrı) ve
/// DatabasesController'ın kendi <c>WriteAuditAsync(</c> sarmalayıcısı var. Tek biçime bakmak,
/// gerçekte kayıt yazan uçları "yazmıyor" sanmaya yol açardı.
/// </para>
/// <para>
/// <b>Neden servis katmanına düşülüyor:</b> panelde kaydın bir kısmı bilerek servis içinde yazılır —
/// çünkü aynı iş arka plan işçisinden de tetiklenir (bileşen kurulumu, abonelik uygulaması, yedek).
/// Bu uçlarda denetleyiciye ikinci bir kayıt eklemek, kaydı gürültüye boğardı. Bu yüzden
/// denetleyicide iz yoksa test PES ETMEZ: uçtan çağrılan servis metodunun gövdesine bakar, oradan
/// da çağrılan servislere (sınırlı derinlikte) iner. Yalnız "servis TİPİ bir yerde kayıt yazıyor"
/// demek yeterli sayılmaz — o kadar kaba bir kural, tam da bu turda kapatılan boşlukları (abonelik
/// plan değişikliği gibi) "kapsanmış" gösterip gizlerdi.
/// </para>
/// <para>
/// <b>Neden "parola" taraması:</b> parolayı kayda yazmamak, çalışma anında ölçülmesi zor ama
/// kaynakta apaçık görülen bir kuraldır. Bir çağrı yeri <c>request.Password</c>'ü denetim
/// çağrısına eklediği anda test kırılır — kayda düşüp yedeklere yayılmadan ÖNCE.
/// (İkinci savunma hattı <c>AuditSecretRedactor</c>; o da AuditSecretRedactorTests'te doğrulanıyor.)
/// </para>
/// </remarks>
public sealed class AuditCoverageTests
{
    /// <summary>
    /// Denetim kaydı yazmanın depoda kullanılan ÜÇ biçimi.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>auditService.AuditAsync(...)</c> — HttpAuditExtensions (denetleyicilerin tek yolu).</item>
    /// <item><c>auditService.WriteAsync(...)</c> / <c>WriteForSubscriptionAsync(...)</c> — servis katmanı.</item>
    /// <item><c>WriteAuditAsync(...)</c> — DatabasesController'ın modüle özgü sarmalayıcısı.</item>
    /// </list>
    /// </remarks>
    private static readonly Regex DenetimCagrisi = new(
        @"\.AuditAsync\(|(?<![.\w])WriteAuditAsync\(|\w*[Aa]udit\w*\.Write(?:For\w+)?Async\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Denetim kaydı BEKLENMEYEN uçlar ve HER BİRİNİN gerekçesi.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Anahtar: <c>DosyaAdı#MetotAdı</c>. Değer: neden bu ucun iz bırakmasının gerekmediği.
    /// </para>
    /// <para>
    /// <b>Bu liste neden "elle yazılmış liste" tuzağına düşmüyor:</b> çünkü DAHİL ETME değil HARİÇ
    /// TUTMA listesi. Kapsam diskten geldiği için yeni bir uç eklendiğinde varsayılan "kapsanmalı"dır;
    /// burası yalnızca gerekçeli istisnaları tutar. Ayrıca
    /// <see cref="Muaf_listesi_CURUMUS_girdi_icermez"/> artık var olmayan bir girdiyi hata sayar —
    /// yani liste kodla birlikte yaşamak zorundadır.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> Muaflar = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // --- POST ama DURUM DEĞİŞTİRMEYEN uçlar ---
        //
        // Gövdesi olan bir sorgu GET ile ifade edilemediği için POST seçilmiştir. Salt okunur bir
        // aramayı her çağrıda kayda yazmak, kaydı gürültüye boğup asıl değişiklikleri görünmez
        // kılardı — denetim kaydının değeri seyrekliğindedir.
        ["FilesController.cs#SearchAsync"] = "Dosya arama: yalnız okur, hiçbir şey değiştirmez.",
        ["MigrationController.cs#DiscoverPleskAsync"] = "Plesk keşfi: UZAK sunucudan domain listesi okur, bu sunucuda hiçbir şey değişmez.",
        ["MigrationController.cs#DiscoverCpanelAsync"] = "cPanel/WHM keşfi: uzak sunucudan hesap listesi okur, bu sunucuda hiçbir şey değişmez.",
        ["MigrationController.cs#ValidateAsync"] = "Göç ön-kontrolü: 'bağlanır mı' der, göçü BAŞLATMAZ.",
        ["SoftwareController.cs#CheckForUpdatesAsync"] = "Güncelleme yoklaması: upstream sürümleri okur, hiçbir bileşene dokunmaz.",
        ["MailAutodiscoverController.cs#OutlookPostAsync"] = "Outlook autodiscover: istemciye ayar XML'i döner (protokol gereği POST), sunucuda hiçbir şey değişmez.",
        ["NginxController.cs#TestAsync"] = "nginx -t: yapılandırmayı doğrular, uygulamaz.",
        ["AuthController.cs#BeginTwoFactorSetupAsync"] = "2FA kurulumunu BAŞLATIR (QR/gizli üretir); etkinleştirme ayrı uçtadır ve o kayıt yazar.",
    };

    /// <summary>
    /// Bilinen ama BU TURDA kapatılmayan boşluklar — sahibi başka bir iş kolunda.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden muaf listesinden AYRI:</b> bunlar "kayıt gerekmiyor" demek DEĞİL, "kayıt gerekiyor
    /// ama bu turda başkası kapatıyor" demektir. İkisini aynı listeye koymak, geçici bir borcu
    /// kalıcı bir gerekçeye dönüştürürdü — ve bir yıl sonra kimse ayrımı bilemezdi.
    /// </para>
    /// <para>
    /// Liste yalnız KÜÇÜLEBİLİR: bir uç kayıt yazmaya başladığında
    /// <see cref="Ertelenen_bosluklar_KAPANINCA_listeden_dusurulur"/> testi girdinin silinmesini
    /// zorunlu kılar.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> BaskaTurdaKapatilacaklar = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Yedekleme: denetim raporunun P2 başlığındaki ilk madde (geri yükleme canlı veriyi ezer).
        ["BackupController.cs#BackupSiteAsync"] = "Yedek modülü ayrı iş kolunda.",
        ["BackupController.cs#BackupDatabaseAsync"] = "Yedek modülü ayrı iş kolunda.",
        ["BackupController.cs#BackupMailAsync"] = "Yedek modülü ayrı iş kolunda.",
        ["BackupController.cs#BackupPanelAsync"] = "Yedek modülü ayrı iş kolunda.",
        ["BackupController.cs#BackupSubscriptionAsync"] = "Yedek modülü ayrı iş kolunda.",
        ["BackupController.cs#RestoreAsync"] = "GERİ YÜKLEME canlı veriyi ezer — en yüksek öncelikli boşluk, yedek iş kolunda.",
        ["BackupController.cs#RestoreItemsAsync"] = "Seçmeli geri yükleme — yedek iş kolunda.",
        ["BackupController.cs#DeleteScheduleAsync"] = "Yedek programı silme — yedek iş kolunda.",
        ["BackupController.cs#RunScheduleNowAsync"] = "Elle tetiklenen yedek turu (budama da yapar) — yedek iş kolunda.",

        // SSL: sertifika içe aktarma özel anahtar taşır.
        ["SslController.cs#IssueLetsEncryptAsync"] = "SSL iş kolunda.",
        ["SslController.cs#ImportAsync"] = "SSL iş kolunda (özel anahtar içe aktarma).",
        ["SslController.cs#RenewAllAsync"] = "SSL iş kolunda.",
        ["SslController.cs#SetHttpsRedirectAsync"] = "SSL iş kolunda.",

        // Güvenlik: yasak kaldırma ve site izolasyonu.
        ["SecurityController.cs#UpdateBruteForceAsync"] = "Güvenlik iş kolunda.",
        ["SecurityController.cs#UnbanAsync"] = "Kaba-kuvvet yasağı kaldırma — güvenlik iş kolunda.",
        ["SecurityController.cs#IsolateAsync"] = "Güvenlik iş kolunda.",
        ["SecurityController.cs#ApplyAclAsync"] = "Güvenlik iş kolunda (dosya sistemi izinleri).",
        ["SecurityController.cs#SetRateLimitAsync"] = "Güvenlik iş kolunda.",
        ["SecurityController.cs#EnableBasicRulesAsync"] = "Güvenlik iş kolunda.",

        // WordPress: login-link YÖNETİCİ OTURUMU üretir, copy-data yıkıcı aynalama yapar.
        ["WordPressController.cs#SetMaintenanceAsync"] = "WordPress iş kolunda.",
        ["WordPressController.cs#CreateLoginLinkAsync"] = "YÖNETİCİ OTURUMU üretir — WordPress iş kolunda.",
        ["WordPressController.cs#HardenAsync"] = "WordPress iş kolunda.",
        ["WordPressController.cs#CloneAsync"] = "WordPress iş kolunda.",
        ["WordPressController.cs#CopyDataAsync"] = "Yıkıcı /MIR aynalama — WordPress iş kolunda.",
        ["WordPressController.cs#ApplyUpdatesAsync"] = "WordPress iş kolunda.",

        // Panel güncellemesi: panelin kendi ikilisini değiştirir.
        ["UpdateController.cs#ApplyAsync"] = "Güncelleme iş kolunda.",
        ["UpdateController.cs#InstallAsync"] = "Güncelleme iş kolunda.",
        ["UpdateController.cs#SetGithubAsync"] = "Güncelleme iş kolunda.",

        // Diğer sahipli denetleyiciler.
        ["SitesController.cs#ImportExistingSitesAsync"] = "Site modülü ayrı iş kolunda.",
        ["AuthController.cs#RegenerateRecoveryCodesAsync"] = "2FA kurtarma kodu üretimi — kimlik iş kolunda.",
        ["LicenseController.cs#RefreshAsync"] = "Lisans iş kolunda.",
    };

    [Fact]
    public void Durum_degistiren_HER_uc_denetim_kaydi_yazar()
    {
        var kok = KaynakKoku();
        var servisler = ServisIndeksi.Olustur(kok);

        var eksik = new List<string>();
        var incelenen = 0;
        var dosyaSayisi = 0;

        foreach (var yol in DenetleyiciDosyalari(kok))
        {
            dosyaSayisi++;
            var dosyaAdi = Path.GetFileName(yol);
            var metin = File.ReadAllText(yol);

            foreach (var (imza, metotAdi, govde) in UcGovdeleri(metin))
            {
                var anahtar = $"{dosyaAdi}#{metotAdi}";
                if (Muaflar.ContainsKey(anahtar) || BaskaTurdaKapatilacaklar.ContainsKey(anahtar))
                {
                    continue;
                }

                incelenen++;

                if (servisler.KapsaniyorMu(metin, govde))
                {
                    continue;
                }

                eksik.Add($"{anahtar}  ({imza})");
            }
        }

        // ÖLÇEMEDİK ile SORUN YOK ayrımı: ayrıştırma bozulup hiçbir uç bulunamazsa test sessizce
        // geçer ve bir daha hiçbir şeyi korumazdı. Dosya sayısı da denetlenir — klasör yolu
        // değişirse tarama boşa döner ve bunu fark etmek gerekir.
        Assert.True(dosyaSayisi >= 50, $"Denetleyici taramasi bozulmus olabilir: yalnizca {dosyaSayisi} dosya bulundu.");
        Assert.True(incelenen >= 200, $"Uc taramasi bozulmus olabilir: yalnizca {incelenen} durum degistiren uc bulundu.");

        Assert.True(
            eksik.Count == 0,
            $"Denetim kaydi YAZMAYAN durum degistiren uclar var ({eksik.Count} adet; kim/ne zaman/neye/ne yapti cevapsiz kalir).\n"
            + "Ya uca denetim kaydi ekleyin ya da GEREKCESIYLE Muaflar listesine yazin:\n  "
            + string.Join("\n  ", eksik));
    }

    /// <summary>
    /// Ertelenen boşluklar KAPANDIĞINDA listeden çıkarılmalıdır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Borç listesi tek yönlü büyüyemez. Bir uç denetim kaydı yazmaya başladığı anda burada durması
    /// için sebep kalmaz; kalırsa liste, gerçekliğe uymayan bir muafiyet listesine dönüşür — yani
    /// bu turda düzeltilen hata sınıfının aynısı, bu kez borç listesinde yeniden doğar.
    /// </para>
    /// </remarks>
    [Fact]
    public void Ertelenen_bosluklar_KAPANINCA_listeden_dusurulur()
    {
        var kok = KaynakKoku();
        var servisler = ServisIndeksi.Olustur(kok);
        var kapananlar = new List<string>();

        foreach (var yol in DenetleyiciDosyalari(kok))
        {
            var dosyaAdi = Path.GetFileName(yol);
            var metin = File.ReadAllText(yol);

            foreach (var (_, metotAdi, govde) in UcGovdeleri(metin))
            {
                var anahtar = $"{dosyaAdi}#{metotAdi}";
                if (BaskaTurdaKapatilacaklar.ContainsKey(anahtar) && servisler.KapsaniyorMu(metin, govde))
                {
                    kapananlar.Add(anahtar);
                }
            }
        }

        Assert.True(
            kapananlar.Count == 0,
            "Bu uclar artik denetim kaydi YAZIYOR — BaskaTurdaKapatilacaklar listesinden CIKARIN "
            + "(borc kapandi; listede kalmasi yeni bir sessiz muafiyet uretir):\n  "
            + string.Join("\n  ", kapananlar));
    }

    /// <summary>
    /// Muaf listesi, artık var olmayan bir uca işaret ediyorsa HATA.
    /// </summary>
    /// <remarks>
    /// Bu olmadan liste tek yönlü büyür: silinen/yeniden adlandırılan uçların muafiyetleri kalır ve
    /// bir gün aynı ada sahip YENİ bir uç sessizce muaf doğar. Nöbetçiyi ısıran hata sınıfı tam
    /// olarak budur; listenin kendisi de denetlenmek zorunda.
    /// </remarks>
    [Fact]
    public void Muaf_listesi_CURUMUS_girdi_icermez()
    {
        var kok = KaynakKoku();
        var mevcut = new HashSet<string>(StringComparer.Ordinal);

        foreach (var yol in DenetleyiciDosyalari(kok))
        {
            var dosyaAdi = Path.GetFileName(yol);
            foreach (var (_, metotAdi, _) in UcGovdeleri(File.ReadAllText(yol)))
            {
                mevcut.Add($"{dosyaAdi}#{metotAdi}");
            }
        }

        var curuyenler = Muaflar.Keys
            .Concat(BaskaTurdaKapatilacaklar.Keys)
            .Where(anahtar => !mevcut.Contains(anahtar))
            .ToArray();

        Assert.True(
            curuyenler.Length == 0,
            "Muaf/erteleme listesinde artik var olmayan uclar var (silin, yoksa ayni adla eklenen yeni bir uc sessizce muaf olur):\n  "
            + string.Join("\n  ", curuyenler));
    }

    [Fact]
    public void Denetim_cagrilarina_PAROLA_gecirilmez()
    {
        var kok = KaynakKoku();
        var ihlaller = new List<string>();
        var incelenen = 0;

        // Yalnız denetleyiciler değil, kayıt yazan HER kaynak dosya taranır: depodaki çağrıların
        // çoğu servis katmanındadır ve sırrın kayda düşmesi orada da aynı derecede kalıcıdır.
        foreach (var yol in TaranacakKaynaklar(kok))
        {
            var dosyaAdi = Path.GetFileName(yol);
            var metin = File.ReadAllText(yol);

            foreach (Match eslesme in DenetimCagrisi.Matches(metin))
            {
                var acilis = metin.IndexOf('(', eslesme.Index);
                if (acilis < 0)
                {
                    continue;
                }

                incelenen++;
                var argumanlar = ArgumanListesi(metin, acilis);
                var kod = MetinDisiKod(argumanlar);

                if (kod.Contains("Password", StringComparison.OrdinalIgnoreCase)
                    || kod.Contains("Parola", StringComparison.OrdinalIgnoreCase)
                    || kod.Contains("Secret", StringComparison.OrdinalIgnoreCase))
                {
                    ihlaller.Add($"{dosyaAdi}: {Kisalt(argumanlar)}");
                }
            }
        }

        Assert.True(incelenen >= 70, $"Sir taramasi bozulmus olabilir: yalnizca {incelenen} denetim cagrisi bulundu.");

        Assert.True(
            ihlaller.Count == 0,
            "Denetim kaydina PAROLA/SIR gecirilen cagri(lar) var — kayit, korumaya calistigi sirrin ikinci deposu olur:\n  "
            + string.Join("\n  ", ihlaller));
    }

    [Fact]
    public void Terminal_ucu_CALISTIRILAN_KOMUTU_kayda_yazar()
    {
        var yol = Path.Combine(KaynakKoku(), "src", "AdaPanel.Api", "Controllers", "TerminalController.cs");
        var metin = File.ReadAllText(yol);

        var cagriBasi = metin.IndexOf("AuditAsync(", StringComparison.Ordinal);
        Assert.True(cagriBasi > 0, "TerminalController denetim kaydi yazmiyor.");

        var argumanlar = ArgumanListesi(metin, metin.IndexOf('(', cagriBasi));

        // Panel LocalSystem olarak kosar: bu uc, sunucuda SYSTEM yetkisiyle keyfi kod calistirmanin
        // ta kendisidir. Komut kaydedilmezse bir olaydan sonra "sunucuda ne yapildi" sorusu
        // CEVAPSIZ kalir — denetim kaydinin var olma sebebi tam olarak budur.
        Assert.True(
            argumanlar.Contains("request.Command", StringComparison.Ordinal),
            "Terminal denetim kaydi calistirilan KOMUTU icermiyor; yalniz cikis kodu yazmak yetmez.");
    }

    // ------------------------------------------------------------------
    // Kaynak tarama yardımcıları
    // ------------------------------------------------------------------

    /// <summary>Denetleyici dosyaları — DİSKTEN, sıralı (hata iletisi kararlı olsun diye).</summary>
    private static IEnumerable<string> DenetleyiciDosyalari(string kok) =>
        Directory
            .EnumerateFiles(Path.Combine(kok, "src", "AdaPanel.Api", "Controllers"), "*Controller.cs", SearchOption.AllDirectories)
            .OrderBy(yol => yol, StringComparer.Ordinal);

    /// <summary>Sır taramasına giren kaynaklar: denetleyiciler + altyapı servisleri.</summary>
    private static IEnumerable<string> TaranacakKaynaklar(string kok) =>
        DenetleyiciDosyalari(kok)
            .Concat(Directory
                .EnumerateFiles(Path.Combine(kok, "src", "AdaPanel.Infrastructure"), "*.cs", SearchOption.AllDirectories)
                .OrderBy(yol => yol, StringComparer.Ordinal));

    /// <summary>
    /// Dosyayı uç uç ayırır: her <c>[Http...]</c> özniteliğinden bir sonrakine (ya da sınıf sonuna)
    /// kadar olan metin o ucun gövdesi sayılır. Tam bir C# ayrıştırıcısı değil ama korumak istediği
    /// şey için yeterli ve kırılgan olmayan bir yaklaşım.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden gövde METODUN KENDİ süslü parantezleriyle sınırlanıyor, "bir sonraki özniteliğe
    /// kadar" değil:</b> ölçüldü — "bir sonraki <c>[Http...]</c>'e kadar" kuralı, dosyanın SON
    /// ucunun gövdesini dosya sonuna kadar uzatıyordu. Yani son ucun gövdesine sınıfın bütün özel
    /// yardımcı metotları da giriyor ve içlerinden biri denetim kaydı yazıyorsa o uç, HİÇ kayıt
    /// yazmasa bile "kapsanmış" sayılıyordu. Nöbetçinin her denetleyicide bir ucu sessizce muaf
    /// tutması, tam da bu testin var olma sebebine aykırı.
    /// </para>
    /// </remarks>
    private static IEnumerable<(string Imza, string MetotAdi, string Govde)> UcGovdeleri(string metin)
    {
        var http = new Regex(@"\[Http(Get|Post|Put|Delete|Patch)[^\]]*\]", RegexOptions.Compiled);
        var bildirim = new Regex(@"(?:public|private|protected|internal)[^;{}=]*?\s(?<ad>\w+)\s*\(", RegexOptions.Compiled);
        var eslesmeler = http.Matches(metin);

        for (var i = 0; i < eslesmeler.Count; i++)
        {
            var mevcut = eslesmeler[i];
            var imza = mevcut.Value;

            // Yalnız durum DEĞİŞTİREN fiiller.
            if (!imza.StartsWith("[HttpPost", StringComparison.Ordinal)
                && !imza.StartsWith("[HttpPut", StringComparison.Ordinal)
                && !imza.StartsWith("[HttpDelete", StringComparison.Ordinal)
                && !imza.StartsWith("[HttpPatch", StringComparison.Ordinal))
            {
                continue;
            }

            // Öznitelikten sonraki İLK metot bildirimi bu ucun kendisidir (arada başka öznitelikler
            // olabilir); sınır, bir sonraki uç özniteliğidir ki kaçak arama olmasın.
            var sinir = i + 1 < eslesmeler.Count ? eslesmeler[i + 1].Index : metin.Length;
            var bildirimEslesmesi = bildirim.Match(metin, mevcut.Index, sinir - mevcut.Index);
            if (!bildirimEslesmesi.Success)
            {
                yield return (imza, "(cozulemedi)", metin[mevcut.Index..sinir]);
                continue;
            }

            var son = MetotSonu(metin, metin.IndexOf('(', bildirimEslesmesi.Index));
            yield return (
                imza,
                bildirimEslesmesi.Groups["ad"].Value,
                metin[mevcut.Index..(son < 0 ? sinir : son)]);
        }
    }

    /// <summary>Parametre listesinin açılış parantezinden metot gövdesinin sonuna kadar olan indeks.</summary>
    private static int MetotSonu(string metin, int acilisIndeksi)
    {
        if (acilisIndeksi < 0)
        {
            return -1;
        }

        var kapanis = DengeliKapanis(metin, acilisIndeksi, '(', ')');
        if (kapanis < 0)
        {
            return -1;
        }

        var i = kapanis + 1;
        while (i < metin.Length && char.IsWhiteSpace(metin[i]))
        {
            i++;
        }

        if (i < metin.Length && metin[i] == '{')
        {
            var son = DengeliKapanis(metin, i, '{', '}');
            return son < 0 ? -1 : son + 1;
        }

        // İfade gövdeli uç (=> ...;)
        var noktali = metin.IndexOf(';', i);
        return noktali < 0 ? -1 : noktali + 1;
    }

    private static int DengeliKapanis(string metin, int acilis, char ac, char kapa)
    {
        var derinlik = 0;
        for (var i = acilis; i < metin.Length; i++)
        {
            if (metin[i] == ac)
            {
                derinlik++;
            }
            else if (metin[i] == kapa)
            {
                derinlik--;
                if (derinlik == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Argüman metninden DÜZ METİN kısımlarını atar, KOD kısımlarını bırakır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gerekçe: denetim kaydının insan tarafından okunan cümleleri zaten "password was changed"
    /// gibi ifadeler içerir — bunları ihlal saymak testi kullanılamaz hâle getirirdi. Tehlikeli
    /// olan, bir DEĞERİN geçirilmesidir: <c>request.Password</c>. Bu yüzden tırnak içindeki düz
    /// metin atılır ama enterpolasyon delikleri (<c>{...}</c>) KOD sayılıp korunur — sızıntının en
    /// olası biçimi tam olarak <c>$"... {request.Password}"</c>dır.
    /// </para>
    /// </remarks>
    private static string MetinDisiKod(string argumanlar)
    {
        var kod = new System.Text.StringBuilder();
        var metinIcinde = false;
        var delikDerinligi = 0;

        for (var i = 0; i < argumanlar.Length; i++)
        {
            var karakter = argumanlar[i];

            if (metinIcinde)
            {
                if (karakter == '\\')
                {
                    i++; // kaçırılmış karakter (ör. \") atlanır
                    continue;
                }

                if (karakter == '{' && delikDerinligi == 0)
                {
                    delikDerinligi = 1;
                    continue;
                }

                if (delikDerinligi > 0)
                {
                    if (karakter == '{')
                    {
                        delikDerinligi++;
                    }
                    else if (karakter == '}')
                    {
                        delikDerinligi--;
                        continue;
                    }

                    kod.Append(karakter);
                    continue;
                }

                if (karakter == '"')
                {
                    metinIcinde = false;
                }

                continue;
            }

            if (karakter == '"')
            {
                metinIcinde = true;
                continue;
            }

            // YORUMLAR DA ATILIR. Ölçüldü: BackupService'in denetim çağrısının argümanları arasında
            // "jeton/parola içermez" diye bir NEDEN yorumu var — yani kodun sırrı yazmadığını
            // AÇIKLAYAN cümle, testi "sır yazılıyor" diye kırıyordu. Bu, tam olarak yanlış yönde bir
            // teşvik: geliştiriciyi doğru olan gerekçe yorumunu SİLMEYE iter.
            if (karakter == '/' && i + 1 < argumanlar.Length)
            {
                if (argumanlar[i + 1] == '/')
                {
                    var satirSonu = argumanlar.IndexOf('\n', i);
                    if (satirSonu < 0)
                    {
                        break;
                    }

                    i = satirSonu;
                    continue;
                }

                if (argumanlar[i + 1] == '*')
                {
                    var kapanis = argumanlar.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (kapanis < 0)
                    {
                        break;
                    }

                    i = kapanis + 1;
                    continue;
                }
            }

            kod.Append(karakter);
        }

        return kod.ToString();
    }

    /// <summary>Verilen açılış parantezinden dengeli kapanışa kadar olan argüman metni.</summary>
    private static string ArgumanListesi(string metin, int acilisIndeksi)
    {
        var derinlik = 0;
        for (var i = acilisIndeksi; i < metin.Length; i++)
        {
            if (metin[i] == '(')
            {
                derinlik++;
            }
            else if (metin[i] == ')')
            {
                derinlik--;
                if (derinlik == 0)
                {
                    return metin[(acilisIndeksi + 1)..i];
                }
            }
        }

        return metin[acilisIndeksi..];
    }

    private static string Kisalt(string deger) =>
        deger.Length <= 160 ? deger : deger[..160] + "…";

    /// <summary>Depo kökünü test çıktısı klasöründen yukarı çıkarak bulur.</summary>
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

    // ------------------------------------------------------------------
    // Servis katmanı indeksi (denetleyicide iz yoksa buraya düşülür)
    // ------------------------------------------------------------------

    /// <summary>Basit kaynak ayrıştırma yardımcıları (tam bir derleyici değil, kasıtlı olarak).</summary>
    private static class KaynakAyristirma
    {
        /// <summary>Birincil kurucu parametreleri ve <c>readonly</c> alanlar: <c>değişken → tip</c>.</summary>
        public static IReadOnlyDictionary<string, string> BagimlilikTipleri(string metin)
        {
            var sonuc = new Dictionary<string, string>(StringComparer.Ordinal);

            var birincilKurucu = Regex.Match(metin, @"class\s+\w+\s*\((?<p>[^)]*)\)", RegexOptions.Singleline);
            if (birincilKurucu.Success)
            {
                Parametreleri(birincilKurucu.Groups["p"].Value, sonuc);
            }

            foreach (Match alan in Regex.Matches(metin, @"readonly\s+(?<tip>[\w.]+)\s+(?<ad>\w+)\s*[;=]"))
            {
                sonuc[alan.Groups["ad"].Value] = KisaTip(alan.Groups["tip"].Value);
            }

            return sonuc;
        }

        private static void Parametreleri(string liste, Dictionary<string, string> hedef)
        {
            foreach (var parca in liste.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eslesme = Regex.Match(parca, @"(?<tip>[\w.]+)\??\s+(?<ad>\w+)\s*$");
                if (eslesme.Success)
                {
                    hedef[eslesme.Groups["ad"].Value] = KisaTip(eslesme.Groups["tip"].Value);
                }
            }
        }

        private static string KisaTip(string tip)
        {
            var son = tip.LastIndexOf('.');
            return son < 0 ? tip : tip[(son + 1)..];
        }
    }

    /// <summary>
    /// Altyapı katmanının "hangi metot kayıt yazar" indeksi.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden metot düzeyinde, tip düzeyinde değil:</b> "bu servis bir yerde kayıt yazıyor" kuralı
    /// tek satırda yazılabilirdi ama işe yaramazdı — örneğin <c>SubscriptionEnforcementService</c>
    /// askıya almayı kaydeder, o yüzden ONA dokunan HER uç (plan değişikliği dahil) kapsanmış
    /// sayılırdı. Oysa plan değişikliğinin hiçbir izi yoktu ve bu turda kapatılan boşluk tam da
    /// oydu. Kaba kural, nöbetçiyi yeşil ama kör bırakırdı.
    /// </para>
    /// <para>
    /// <b>Neden derinlik sınırlı:</b> çağrı grafiği metin üzerinden çözülüyor; sınırsız derinlik hem
    /// yavaşlar hem de alakasız bir yoldan kayıt yazan bir metoda ulaşıp yanlış "kapsanmış" kararı
    /// verme ihtimalini büyütür. Üç seviye, panelin denetleyici → servis → yardımcı servis
    /// zincirini karşılar (ör. SoftwareController → PhpService → ComponentBootstrapper).
    /// </para>
    /// </remarks>
    private sealed class ServisIndeksi
    {
        private const int AzamiDerinlik = 3;

        private readonly Dictionary<string, List<Sinif>> siniflar = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> arayuzden = new(StringComparer.Ordinal);

        /// <summary>(sınıf, metot, kalan bütçe) → kayıt yazıyor mu. Tekrarlanan gezinmeyi toplar.</summary>
        private readonly Dictionary<string, bool> sonuclar = new(StringComparer.Ordinal);

        public static ServisIndeksi Olustur(string kok)
        {
            var indeks = new ServisIndeksi();

            foreach (var yol in Directory.EnumerateFiles(Path.Combine(kok, "src", "AdaPanel.Infrastructure"), "*.cs", SearchOption.AllDirectories))
            {
                var metin = File.ReadAllText(yol);
                var bildirim = Regex.Match(
                    metin,
                    @"(?:public|internal)\s+(?:sealed\s+|abstract\s+|partial\s+|static\s+)*class\s+(?<ad>\w+)(?<kurucu>\s*\([^)]*\))?\s*(?::\s*(?<temeller>[^\r\n{]+))?");

                if (!bildirim.Success)
                {
                    continue;
                }

                var ad = bildirim.Groups["ad"].Value;
                var sinif = new Sinif(metin, KaynakAyristirma.BagimlilikTipleri(metin));

                if (!indeks.siniflar.TryGetValue(ad, out var liste))
                {
                    liste = [];
                    indeks.siniflar[ad] = liste;
                }

                liste.Add(sinif);

                foreach (var temel in bildirim.Groups["temeller"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var temelAd = Regex.Match(temel, @"^[\w.]+").Value;
                    var son = temelAd.LastIndexOf('.');
                    if (son >= 0)
                    {
                        temelAd = temelAd[(son + 1)..];
                    }

                    if (temelAd.Length > 1 && temelAd[0] == 'I' && char.IsUpper(temelAd[1]))
                    {
                        indeks.arayuzden[temelAd] = ad;
                    }
                }
            }

            return indeks;
        }

        /// <summary>
        /// Bir ucun izi var mı: doğrudan çağrı, AYNI denetleyicideki yardımcı metot, ya da servis katmanı.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Neden aynı sınıftaki yardımcı da sayılıyor:</b> bir denetleyicide dört uç aynı kaydı
        /// yazıyorsa kaydı özel bir yardımcıya çıkarmak DOĞRU olandır (tekrarı tek kaynağa toplar).
        /// Yalnız gövdedeki düz metne bakan bir nöbetçi, bu doğru refaktörü "kayıt silinmiş" diye
        /// cezalandırır ve geliştiriciyi kopyala-yapıştıra iter.
        /// </para>
        /// </remarks>
        public bool KapsaniyorMu(string denetleyiciMetni, string govde)
        {
            var denetleyici = new Sinif(denetleyiciMetni, KaynakAyristirma.BagimlilikTipleri(denetleyiciMetni));
            return GovdedeKayitVarMi(govde, denetleyici, AzamiDerinlik, []);
        }

        /// <summary>
        /// Tek bir gövdenin (uç ya da metot) kayıt yazıp yazmadığı — üç yola da bakarak.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Neden "kalan adım" sayılıyor, artan derinlik değil, ve neden yol kümesi geri
        /// alınıyor:</b> ilk yazımda tek bir küresel "ziyaret edildi" kümesi vardı ve bu, sessiz bir
        /// YANLIŞ NEGATİF üretti. Ölçüldü: <c>SoftwareController.InstallAsync</c> önce
        /// <c>phpService</c> dalını geziyor, o dal <c>ComponentBootstrapper</c>'ın kurulum
        /// metotlarını DERİN bir noktada (bütçe bitmişken) ziyaret edilmiş işaretliyor; sonra asıl
        /// dal aynı metotlara BOL bütçeyle geldiğinde "zaten bakıldı" deyip <c>false</c> dönüyordu.
        /// Yani gerçekte kayıt yazan bir uç, yalnızca çağrıların SIRASI yüzünden "yazmıyor"
        /// görünüyordu. Küme artık yalnızca DÖNGÜ kesmek için ve yalnızca o an izlenen YOL boyunca
        /// tutuluyor (girişte eklenir, çıkışta silinir); tekrarlanan işi
        /// <see cref="sonuclar"/> önbelleği toplar.
        /// </para>
        /// </remarks>
        private bool GovdedeKayitVarMi(string govde, Sinif sahip, int kalan, HashSet<string> yol)
        {
            if (DenetimCagrisi.IsMatch(govde))
            {
                return true;
            }

            if (kalan <= 0)
            {
                return false;
            }

            // 1) Aynı sınıftaki yardımcı metotlar (ör. dört ucun paylaştığı özel kayıt yardımcısı).
            foreach (Match ic in Regex.Matches(govde, @"(?<![.\w])(?<metot>\w+Async)\s*\("))
            {
                var ad = ic.Groups["metot"].Value;
                if (!yol.Add($"(sahip)#{ad}"))
                {
                    continue;
                }

                try
                {
                    foreach (var yardimci in sahip.MetotGovdeleri(ad))
                    {
                        if (GovdedeKayitVarMi(yardimci, sahip, kalan - 1, yol))
                        {
                            return true;
                        }
                    }
                }
                finally
                {
                    yol.Remove($"(sahip)#{ad}");
                }
            }

            // 2) Enjekte edilmiş servisler.
            foreach (Match cagri in Regex.Matches(govde, @"(?<degisken>\w+)\.(?<metot>\w+)\s*\("))
            {
                if (sahip.Bagimliliklar.TryGetValue(cagri.Groups["degisken"].Value, out var tip)
                    && KayitYaziyorMu(SinifAdi(tip), cagri.Groups["metot"].Value, kalan, yol))
                {
                    return true;
                }
            }

            return false;
        }

        private string SinifAdi(string tip) => arayuzden.TryGetValue(tip, out var sinif) ? sinif : tip;

        private bool KayitYaziyorMu(string sinifAdi, string metotAdi, int kalan, HashSet<string> yol)
        {
            if (kalan <= 0 || !siniflar.TryGetValue(sinifAdi, out var parcalar))
            {
                return false;
            }

            // Önbellek anahtarına KALAN BÜTÇE de girer: aynı metot, farklı bütçeyle farklı sonuç
            // verebilir. Bütçeyi anahtardan düşürmek, yukarıda anlatılan yanlış negatifin ta kendisi.
            var anahtar = $"{sinifAdi}#{metotAdi}#{kalan}";
            if (sonuclar.TryGetValue(anahtar, out var onbellek))
            {
                return onbellek;
            }

            var dugum = $"{sinifAdi}#{metotAdi}";
            if (!yol.Add(dugum))
            {
                // Döngü: bu dal sonuç veremez. ÖNBELLEĞE YAZILMAZ — bu "false" yola özgüdür,
                // metodun kendisi hakkında bir şey söylemez.
                return false;
            }

            try
            {
                foreach (var sinif in parcalar)
                {
                    foreach (var govde in sinif.MetotGovdeleri(metotAdi))
                    {
                        if (GovdedeKayitVarMi(govde, sinif, kalan - 1, yol))
                        {
                            sonuclar[anahtar] = true;
                            return true;
                        }
                    }
                }
            }
            finally
            {
                yol.Remove(dugum);
            }

            sonuclar[anahtar] = false;
            return false;
        }

        private sealed class Sinif(string metin, IReadOnlyDictionary<string, string> bagimliliklar)
        {
            public IReadOnlyDictionary<string, string> Bagimliliklar { get; } = bagimliliklar;

            /// <summary>
            /// Verilen adlı metot TANIMLARININ gövdeleri (aşırı yüklemeler dahil).
            /// </summary>
            /// <remarks>
            /// Tanım ile ÇAĞRI şöyle ayrılır: bir tanımın parametre listesini <c>{</c> ya da
            /// <c>=&gt;</c> izler; bir çağrıyı ise <c>;</c>, <c>)</c>, <c>,</c> gibi bir şey izler.
            /// Bu ayrım olmadan metot adının geçtiği her yer gövde sanılır ve indeks anlamsızlaşırdı.
            /// </remarks>
            public IEnumerable<string> MetotGovdeleri(string ad)
            {
                foreach (Match eslesme in Regex.Matches(metin, @"(?<![.\w])" + Regex.Escape(ad) + @"\s*\("))
                {
                    var acilis = metin.IndexOf('(', eslesme.Index);
                    var kapanis = DengeliKapanis(metin, acilis, '(', ')');
                    if (kapanis < 0)
                    {
                        continue;
                    }

                    var i = kapanis + 1;
                    while (i < metin.Length && char.IsWhiteSpace(metin[i]))
                    {
                        i++;
                    }

                    if (i < metin.Length && metin[i] == '{')
                    {
                        var son = DengeliKapanis(metin, i, '{', '}');
                        if (son > 0)
                        {
                            yield return metin[i..son];
                        }
                    }
                    else if (i + 1 < metin.Length && metin[i] == '=' && metin[i + 1] == '>')
                    {
                        var noktali = metin.IndexOf(';', i);
                        yield return noktali < 0 ? metin[i..] : metin[i..noktali];
                    }
                }
            }

        }
    }
}
