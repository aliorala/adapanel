using System.Runtime.Versioning;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Htaccess;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Htaccess.Hosting;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Htaccess;

/// <summary>Bir sitede bulunan tek bir <c>.htaccess</c> dosyasının panel özeti.</summary>
public sealed class HtaccessFileSummary
{
    /// <summary>Site köküne göre dizin (<c>""</c> kök, <c>"blog"</c>, <c>"wp-admin"</c>).</summary>
    public string RelativeDirectory { get; init; } = string.Empty;

    /// <summary>URL karşılığı (<c>/</c>, <c>/blog/</c>).</summary>
    public string VirtualPath { get; init; } = "/";

    public long SizeBytes { get; init; }

    public DateTime LastWriteUtc { get; init; }
}

/// <summary>
/// Panelin bir siteye "<c>.htaccess</c> gerçekte NE yapıyor" sorusuna verdiği TAM cevap.
/// </summary>
/// <remarks>
/// Bu tipin var olma sebebi tek bir şikâyettir: <b>"dosya duruyor, hiçbir şey olmuyor, kimse
/// sebebini söylemiyor."</b> Bu yüzden her alan bir SORUYU cevaplıyor ve hiçbiri "iyi görünsün"
/// diye doldurulmuyor — ölçülemeyen alan boş/false kalır ve arayüz onu "bilinmiyor" diye gösterir.
/// </remarks>
public sealed class HtaccessSiteReport
{
    public string SiteName { get; init; } = string.Empty;

    public string DocumentRoot { get; init; } = string.Empty;

    /// <summary>Bu sitenin anahtarı: <c>off</c> / <c>observe</c> / <c>enforce</c>.</summary>
    public string Mode { get; init; } = "off";

    /// <summary>
    /// Sunucu geneli anahtar. Site açık olsa bile bu <c>off</c> ise HİÇBİR ŞEY olmaz —
    /// arayüzün en çok karıştırılan noktası burasıdır.
    /// </summary>
    public string ServerMode { get; init; } = "off";

    /// <summary>
    /// Sitede <c>.htaccess</c> dosyası VAR mı? <see cref="Mode"/> <c>off</c> iken bu <c>true</c>
    /// ise panel bunu AÇIKÇA söylemek zorundadır.
    /// </summary>
    public bool HasFiles => Files.Count > 0;

    /// <summary>
    /// BELGE KÖKÜNDE <c>.htaccess</c> var mı? Ayrı bir alan çünkü barındırma katmanı yalnız kök
    /// dosyaya bakıyor: kökte dosya yoksa alt dizinlerdekiler de UYGULANMAZ
    /// (bkz. <c>HtaccessEngineBridge.Prepare</c>).
    /// </summary>
    public bool HasRootFile { get; init; }

    public IReadOnlyList<HtaccessFileSummary> Files { get; init; } = [];

    /// <summary>Kök dizin için motorun raporu: ne çalışıyor, ne çalışmıyor, neden.</summary>
    public HtaccessInspectionResult Inspection { get; init; } = new();

    /// <summary>Uygulama havuzu — desteğin belirleyicisi.</summary>
    public string ApplicationPoolName { get; init; } = string.Empty;

    /// <summary>Havuzun yönetilen CLR sürümü; boş dize "yönetilen kod yok" demektir.</summary>
    public string ManagedRuntimeVersion { get; init; } = string.Empty;

    /// <summary>
    /// <c>supported</c> / <c>no_managed_code</c> / <c>runtime_version</c> / <c>unknown</c>.
    /// <c>unknown</c> = havuz OKUNAMADI; "destekleniyor" diye göstermek yalan olurdu.
    /// </summary>
    public string Support { get; init; } = "unknown";

    /// <summary>
    /// <b>ÇEKİRDEK MOTOR IIS İÇİNDE YÜKLENEBİLİR Mİ?</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uzun süre her koşulda <c>false</c>'tu: motor net8'di, IIS modülü net48 olmak zorundaydı ve
    /// bağlantı yapılamıyordu (bkz. <see cref="EngineBlockers"/>). O engel kalktı; motor artık
    /// modül derlemesinin İÇİNDE ve modülün yüklendiği her uygulamada kayıt yerine oturuyor.
    /// </para>
    /// <para>
    /// <b>NE KADARINI SÖYLEYEBİLDİĞİMİZ:</b> panel süreci <c>w3wp</c>'nin içini göremez, yani
    /// "şu anda yüklü" diye bir ölçüm YOKTUR. Değer, panelin dışarıdan ölçebildiği tek zorunlu ön
    /// koşuldan gelir: uygulama havuzu yönetilen kod çalıştırıyor mu (<see cref="Support"/>).
    /// Havuz yönetilen kod çalıştırmıyorsa ya da yanlış CLR sürümündeyse modül HİÇ yüklenmez —
    /// bu durumlarda <c>false</c> KESİN bilgidir. Havuz uygunsa değer <c>true</c>'dur ve "modül
    /// kurulu ve kayıtlıysa motor çalışır" anlamına gelir; kurulumun kendisi ayrı bir konudur ve
    /// panel onu buradan iddia etmez.
    /// </para>
    /// </remarks>
    public bool EngineLoadedInIis { get; init; }

    /// <summary>
    /// Motorun IIS içinde çalışmasını engelleyen ÖLÇÜLMÜŞ nedenler. Boş liste "engel yok" demektir.
    /// </summary>
    public IReadOnlyList<string> EngineBlockers { get; init; } = [];

    /// <summary>
    /// Motorun Apache'den BİLİNÇLİ sapmaları. Uydurulmuyor: motorun kendi belgelenmiş listesinden
    /// geliyor (<c>HtaccessEngine</c> sınıf yorumu) ve arayüz olduğu gibi gösteriyor.
    /// </summary>
    public IReadOnlyList<string> Deviations { get; init; } = [];
}

/// <summary>
/// SUNUCU GENELİ <c>.htaccess</c> anahtarının panel görünümü.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı bir tip, <see cref="HtaccessSiteReport"/> içindeki <c>ServerMode</c> değil:</b> o
/// alan bir SİTENİN raporunun parçasıdır ve site adı olmadan üretilemez. Genel anahtarı okumak ya
/// da çevirmek için bir siteye ihtiyaç yoktur — üstelik yönetici anahtarı, hiç sitesi olmayan bir
/// sunucuda da (kurulumdan hemen sonra) görebilmelidir.
/// </para>
/// <para>
/// <b>Dosya yolu neden dönüyor:</b> bu anahtar bir ACİL DURUM aracıdır. Panelin kendisi
/// erişilemez hâle geldiğinde yöneticinin tek çıkışı dosyayı silmektir; yolu ekranda göstermek,
/// o anda arayıp bulma işini ortadan kaldırır. Uç yalnız yöneticiye açık olduğu için yol sızıntısı
/// değildir (kiracıya gösterilseydi olurdu).
/// </para>
/// </remarks>
public sealed class HtaccessServerSwitchState
{
    /// <summary><c>off</c> / <c>observe</c> / <c>enforce</c>.</summary>
    public string Mode { get; init; } = "off";

    /// <summary>
    /// Anahtar dosyası diskte VAR mı. <c>off</c> iken dosya SİLİNİR, dolayısıyla bu alan
    /// "hiç açılmamış / kapatılmış" ile "açık" arasındaki farkı değil, kapatmanın gerçekten
    /// diske yansıdığını gösterir.
    /// </summary>
    public bool SwitchFileExists { get; init; }

    /// <summary>Anahtar dosyasının tam yolu (kurtarma için).</summary>
    public string SwitchFilePath { get; init; } = string.Empty;
}

/// <summary>Bir URL'in <c>.htaccess</c> altında ne olacağının panel benzetimi.</summary>
public sealed class HtaccessSimulationResult
{
    public string RequestPath { get; init; } = string.Empty;

    /// <summary><c>none</c> / <c>rewrite</c> / <c>redirect</c> / <c>deny</c>.</summary>
    public string Action { get; init; } = "none";

    public string Target { get; init; } = string.Empty;

    public int StatusCode { get; init; }

    /// <summary>Motorun ham sonucu — bulgular ve bütçe raporu dahil.</summary>
    public HtaccessEvaluationResult Detail { get; init; } = new();
}

/// <summary>
/// PANELİN <c>.htaccess</c> YÜZÜ — çekirdek motor ile barındırma anahtarlarını tek yerde birleştirir.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı bir servis, denetleyicide birleştirme değil:</b> "bu sitede .htaccess ne yapıyor"
/// sorusunun cevabı ÜÇ ayrı kaynaktan geliyor — motor (Inspect/Validate/Evaluate), anahtar
/// dosyaları (HtaccessSwitchStore) ve IIS havuzu (ClassifySupport). Üçünü denetleyicide
/// birleştirmek, aynı birleştirmeyi ileride ikinci bir çağıranın (uyarı motoru, sağlık taraması)
/// KOPYALAMASI demekti; bu depoda o hata sınıfının bedeli defalarca ödendi.
/// </para>
/// <para>
/// <b>Motor DIŞARIDAN geliyor ve TEKİLDİR (singleton).</b> Bu bir tercih değil, motorun kendi
/// sözleşmesi: <c>HtaccessEngine</c> sınıf yorumu "iş parçacığı güvenlidir ve tekil olarak
/// kaydedilmek üzere tasarlandı" diyor. Servis içinde <c>new</c> ile kurulsaydı motor istek
/// başına doğar, ayrıştırma ve desen önbellekleri her panel açılışında sıfırlanır — yani
/// motorun en pahalı işi (düzenli ifade derlemesi) her seferinde yeniden yapılırdı.
/// </para>
/// <para>
/// <b>HİÇBİR ÜYE İSTİSNA FIRLATMAZ diye bir söz VERİLMİYOR</b> — ve bu bilinçli. Motorun sözü
/// istek yolu içindir (site bozulmasın). Panel yolunda sessizce boş rapor dönmek, tam da
/// düzeltmeye çalıştığımız "hiçbir şey olmuyor, kimse sebebini söylemiyor" davranışı olurdu:
/// panel bir şeyi okuyamadıysa GÜRÜLTÜLÜ hata vermeli. Yalnız İKİNCİL okumalar (havuz sürümü,
/// dosya damgası) yutuluyor ve sonuçta "bilinmiyor" olarak görünüyor.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class HtaccessPanelService
{
    /// <summary>
    /// Dosya taramasının ÜST SINIRLARI. Neden gerekli: site kökü müşterinin dosya alanıdır ve
    /// <c>node_modules</c> gibi yüz binlerce klasör içerebilir. Sınırsız bir tarama, panelin bir
    /// sayfasını sunucuyu dizine boğan bir işe çevirirdi.
    /// </summary>
    private const int MaxTaranacakDerinlik = 8;

    private const int MaxDosyaSayisi = 200;

    private readonly IHtaccessEngine _motor;
    private readonly IIisSiteService _siteler;
    private readonly IAspNetService _aspNet;
    private readonly HtaccessSwitchStore _anahtarlar;
    private readonly ILogger<HtaccessPanelService> _gunluk;

    public HtaccessPanelService(
        IHtaccessEngine motor,
        IIisSiteService siteler,
        IAspNetService aspNet,
        IOptions<PanelRuntimeOptions> secenekler,
        ILogger<HtaccessPanelService> gunluk)
    {
        _motor = motor;
        _siteler = siteler;
        _aspNet = aspNet;
        _gunluk = gunluk;

        var veriKoku = Path.GetFullPath(Environment.ExpandEnvironmentVariables(secenekler.Value.DataRoot));
        _anahtarlar = new HtaccessSwitchStore(veriKoku);
    }

    /// <summary>Sitenin tam raporu.</summary>
    public async Task<HtaccessSiteReport> GetReportAsync(string siteName, CancellationToken iptal)
    {
        var site = await _siteler.GetSiteAsync(siteName, iptal);
        var kok = PathContainment.Normalize(site.PhysicalPath);

        var anahtar = _anahtarlar.ReadSite(site.Name);
        var genel = _anahtarlar.ReadGlobal();

        var dosyalar = kok is null ? [] : DosyalariBul(kok);
        var inceleme = kok is null ? new HtaccessInspectionResult() : _motor.Inspect(kok);

        var (havuzSurumu, destek) = await DestegiCozAsync(site.Name, iptal);

        return new HtaccessSiteReport
        {
            SiteName = site.Name,
            DocumentRoot = kok ?? site.PhysicalPath,
            Mode = ModAdi(anahtar.Mode),
            ServerMode = ModAdi(genel.Mode),
            HasRootFile = dosyalar.Any(d => d.RelativeDirectory.Length == 0),
            Files = dosyalar,
            Inspection = inceleme,
            ApplicationPoolName = site.ApplicationPoolName,
            ManagedRuntimeVersion = havuzSurumu,
            Support = destek,
            // ARTIK SABIT false DEGIL. Derleme zamani engeli kalktigi icin (bkz. MotorEngelleri)
            // motor, modulun yuklendigi her uygulamada kayit yerine oturuyor. Panelin disaridan
            // olcebildigi TEK on kosul havuzun yonetilen kod calistirip calistirmadigidir:
            // "no_managed_code" ya da yanlis CLR surumunde modul HIC yuklenmez, dolayisiyla motor
            // da yuklenmez. "unknown" da false uretir -- havuzu okuyamadiysak "calisiyor" demek
            // yalan olurdu (bkz. DestegiCozAsync).
            EngineLoadedInIis = destek == "supported",
            EngineBlockers = MotorEngelleri(),
            Deviations = Sapmalar()
        };
    }

    /// <summary>
    /// Sitenin (ya da bir alt dizininin) <c>.htaccess</c> raporunu üretir.
    /// </summary>
    /// <remarks>
    /// <paramref name="relativeDirectory"/> motora OLDUĞU GİBİ verilmiyor; önce site kökünün
    /// altında olduğu ÖLÇÜLÜYOR. Motorun kendi kapsama denetimi de var (basamaklandırma
    /// <c>..</c>'yı reddeder) ama iki katman birbirine güvenmez: burada reddetmek, kullanıcının
    /// <c>../../komsu-site</c> yazdığında "reddedildi" diye AÇIK bir hata almasını sağlar; motora
    /// bırakılsaydı boş bir rapor dönerdi ve boş rapor "orada bir şey yok" gibi okunurdu.
    /// </remarks>
    public async Task<HtaccessInspectionResult> InspectAsync(
        string siteName, string? relativeDirectory, CancellationToken iptal)
    {
        var site = await _siteler.GetSiteAsync(siteName, iptal);
        var kok = PathContainment.Normalize(site.PhysicalPath)
                  ?? throw new AppException("htaccess.document_root_invalid",
                      details: $"'{siteName}' sitesinin belge kökü ('{site.PhysicalPath}') geçerli bir mutlak yol değil.");

        var goreli = TemizGoreliDizin(relativeDirectory);
        if (goreli.Length > 0)
        {
            var hedef = Path.Combine(kok, goreli.Replace('/', Path.DirectorySeparatorChar));
            if (!PathContainment.IsRootOrInside(kok, hedef))
            {
                throw new AppException("htaccess.directory_outside_root",
                    details: $"'{relativeDirectory}' site kökünün dışına çıkıyor; inceleme REDDEDİLDİ.");
            }
        }

        return _motor.Inspect(kok, goreli);
    }

    /// <summary>
    /// Kullanıcının panelde yazdığı metni KAYDETMEDEN ÖNCE doğrular. Diskte hiçbir şey okumaz/yazmaz.
    /// </summary>
    public HtaccessParseResult Validate(string? content, string? virtualPath)
    {
        var yol = string.IsNullOrWhiteSpace(virtualPath) ? "/" : virtualPath!.Trim();
        return _motor.Validate(content ?? string.Empty, yol);
    }

    /// <summary>
    /// Bir URL'i sitenin GERÇEK <c>.htaccess</c> dosyalarına karşı çalıştırır ve ne olacağını söyler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden bu var:</b> "kural yazdım, çalışıyor mu?" sorusunun tek dürüst cevabı ölçmektir.
    /// Kural listesini göstermek yetmez — <c>RewriteCond</c> zincirleri, <c>[L]</c> bayrağı ve
    /// basamaklandırma yüzünden görünen kural ile ETKİN kural farklı olabilir.
    /// </para>
    /// <para>
    /// <b>Modülün kullandığı KÖPRÜNÜN AYNISI çalışıyor</b> (<see cref="HtaccessEngineBridge"/>):
    /// benzetim ile canlının farklı kod yollarından geçmesi, benzetimi bir yalan makinesine
    /// çevirirdi. Fark yalnız girdide: burada istek panelden geliyor, orada IIS'ten.
    /// </para>
    /// </remarks>
    public async Task<HtaccessSimulationResult> SimulateAsync(
        string siteName, string? requestPath, string? method, bool https, CancellationToken iptal)
    {
        var site = await _siteler.GetSiteAsync(siteName, iptal);
        var kok = PathContainment.Normalize(site.PhysicalPath)
                  ?? throw new AppException("htaccess.document_root_invalid",
                      details: $"'{siteName}' sitesinin belge kökü ('{site.PhysicalPath}') geçerli bir mutlak yol değil.");

        var yol = string.IsNullOrWhiteSpace(requestPath) ? "/" : requestPath!.Trim();
        var soru = yol.IndexOf('?');
        var sorgu = soru >= 0 ? yol[(soru + 1)..] : string.Empty;
        if (soru >= 0)
        {
            yol = yol[..soru];
        }

        if (!yol.StartsWith('/'))
        {
            yol = "/" + yol;
        }

        var host = site.Bindings.FirstOrDefault(b => !string.IsNullOrWhiteSpace(b.HostName))?.HostName ?? site.Name;

        var kopru = new HtaccessEngineBridge(_motor);
        var karar = kopru.Decide(kok, kok, yol, sorgu, method, "203.0.113.10", https, host);

        // Ayrıntı için motorun HAM sonucu da alınıyor: köprü kararı üçe indiriyor (uygulanabilir
        // olan bu), ama panelde bulguları ve bütçeyi göstermek gerekiyor.
        var ham = _motor.Evaluate(new HtaccessRequestContext
        {
            SiteRootPath = kok,
            RequestPath = yol,
            QueryString = sorgu,
            Method = string.IsNullOrWhiteSpace(method) ? "GET" : method!.Trim().ToUpperInvariant(),
            HttpHost = host,
            ServerName = host,
            ServerPort = https ? 443 : 80,
            IsHttps = https,
            RemoteAddress = "203.0.113.10"
        });

        return new HtaccessSimulationResult
        {
            RequestPath = yol,
            Action = karar.Kind switch
            {
                HtaccessBridgeActionKind.Rewrite => "rewrite",
                HtaccessBridgeActionKind.Redirect => "redirect",
                HtaccessBridgeActionKind.Deny => "deny",
                _ => "none"
            },
            Target = karar.Target ?? string.Empty,
            StatusCode = karar.StatusCode,
            Detail = ham
        };
    }

    /// <summary>
    /// Sitenin kipini değiştirir (kapalı / gözlem / uygula).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bu bir GÜVENLİK SINIRI değişikliğidir</b> ve denetim kaydı denetleyicide yazılıyor.
    /// Burada yalnız yazma yapılıyor; kaydı servise almak, aynı işi tetikleyen ileriki bir arka
    /// plan çağrısında aktörün (kim yaptı) kaybolmasına yol açardı.
    /// </para>
    /// <para>
    /// <b>Sunucu geneli anahtara DOKUNULMUYOR.</b> Site açılırken genel anahtarı da otomatik açmak
    /// "kolaylık" gibi görünürdü ama acil durum kapatma düğmesini işlevsiz kılardı: yönetici
    /// sunucuyu kapatır, ertesi gün bir kiracı kendi sitesini açar ve sunucu geneli koruma
    /// sessizce geri gelirdi. Genel anahtar YALNIZ yöneticinin ayrı bir kararıdır.
    /// </para>
    /// </remarks>
    public async Task<HtaccessSiteReport> SetModeAsync(
        string siteName, string mode, string? note, CancellationToken iptal)
    {
        var site = await _siteler.GetSiteAsync(siteName, iptal);
        var kip = ModCozumle(mode);

        _anahtarlar.SetSite(site.Name, kip, note);

        // Panel dosyaya dokunmuş olabilir (kullanıcı aynı ekranda .htaccess'ini düzenledi);
        // önbelleği düşürmek, raporun ANINDA güncel olmasını garanti eder.
        _motor.InvalidateCache(site.PhysicalPath);

        return await GetReportAsync(site.Name, iptal);
    }

    /// <summary>
    /// Sunucu geneli anahtarın durumu. Site adı GEREKMEZ.
    /// </summary>
    /// <remarks>
    /// Okuma başarısızsa <see cref="HtaccessSwitchStore.ReadGlobal"/> <c>off</c> döner ve burada da
    /// öyle raporlanır — panelin "açık" deyip modülün kapalı olması, tersinden çok daha tehlikeli
    /// bir yalandır (aynı gerekçe deponun kendi yorumunda da yazılı).
    /// </remarks>
    public HtaccessServerSwitchState GetServerMode()
    {
        var genel = _anahtarlar.ReadGlobal();
        return new HtaccessServerSwitchState
        {
            Mode = ModAdi(genel.Mode),
            SwitchFileExists = genel.FileExists,
            SwitchFilePath = genel.Path
        };
    }

    /// <summary>
    /// SUNUCU GENELİ anahtarı çevirir: kapalı / gözlem / uygula.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bu, özelliği AÇAN tek düğmedir.</b> Site kipi tek başına hiçbir şey yapmaz: kapı etkin
    /// modu iki anahtarın EN DÜŞÜĞÜ olarak hesaplar (<c>HtaccessRuntimeGate.Evaluate</c>), yani
    /// genel anahtar <c>off</c> iken sunucudaki hiçbir site kendini açamaz. Panelde bu ucun
    /// olmaması, özelliğin ancak <c>%ProgramData%\AdaPanel\htaccess\module.switch</c> dosyası ELLE
    /// oluşturularak kullanılabilmesi demekti.
    /// </para>
    /// <para>
    /// <b>Yazma ATOMİKTİR</b> ve bu bir tercih değil zorunluluk: modül dosyayı her TTL'de okur.
    /// Doğrudan üzerine yazılsaydı okuma yazımın ortasına denk geldiğinde YARIM içerik okunurdu
    /// (bkz. <c>HtaccessSwitchStore.WriteAtomic</c> — geçici dosya + <c>File.Move(overwrite)</c>).
    /// Yarım dosyanın "sadece kapatır" olduğu VARSAYILMIYOR, satır sırasıyla garanti altına alındı:
    /// <c>HtaccessSwitchFile.Render</c> önce <c>mode</c>, sonra <c>enabled</c> yazar; kesilen her
    /// önek <c>enabled</c> satırını içermez ve kapı KAPALI okur.
    /// </para>
    /// <para>
    /// <b>ERİŞİM İZİNLERİNE DOKUNULMAZ.</b> <c>htaccess</c> klasörü
    /// <c>DataRootHardeningService.SensitiveDirectories</c> listesinde DEĞİLDİR (liste okundu:
    /// data, keys, certs, config, backups, mail-certs) — kendi sertleştirmesi ayrıdır
    /// (<c>HtaccessSwitchStore.HardenAcl</c>: IIS_IUSRS okur, YAZAMAZ) ve kurulumda BİR KEZ
    /// uygulanır. Her anahtar çevirmede yeniden uygulamak, bu deponun ölçtüğü iki zararı geri
    /// getirirdi: yöneticinin bilinçli izin düzenlemesi ilk açmada silinir, ACL uygulaması
    /// başarısız olduğunda anahtarı yazmak da imkânsızlaşırdı. Yeni dosya, geçici dosya AYNI
    /// klasörde oluşturulup taşındığı için klasörün DACL'ini olduğu gibi devralır.
    /// </para>
    /// </remarks>
    public HtaccessServerSwitchState SetServerMode(string mode, string? note)
    {
        var kip = ModCozumle(mode);
        _anahtarlar.SetGlobal(kip, note);
        return GetServerMode();
    }

    // ---------------------------------------------------------------- iç yardımcılar

    private static string ModAdi(HtaccessMode mod) => mod switch
    {
        HtaccessMode.Observe => "observe",
        HtaccessMode.Enforce => "enforce",
        _ => "off"
    };

    private static HtaccessMode ModCozumle(string? mod)
    {
        var temiz = (mod ?? string.Empty).Trim().ToLowerInvariant();
        return temiz switch
        {
            "off" => HtaccessMode.Off,
            "observe" => HtaccessMode.Observe,
            "enforce" => HtaccessMode.Enforce,
            // TANINMAYAN KİP REDDEDİLİR, "off"a düşmez. Kapatma sessiz bir başarı gibi görünür;
            // kullanıcı "enfroce" yazdığında uyguladığını sanıp kapalı bir siteyle kalırdı.
            _ => throw new AppException("htaccess.invalid_mode",
                details: $"Geçersiz kip: '{mod}'. Geçerli değerler: off, observe, enforce.")
        };
    }

    private static string TemizGoreliDizin(string? deger)
        => (deger ?? string.Empty).Replace('\\', '/').Trim('/').Trim();

    /// <summary>
    /// Havuzun yönetilen CLR sürümünü okur ve desteği sınıflandırır.
    /// </summary>
    /// <remarks>
    /// Okuma BAŞARISIZ olursa <c>unknown</c> döner — <c>supported</c> DEĞİL. Bu bir kolaylık
    /// değil, bu iş kolunun bütün amacı: Node/Python siteleri için havuz "yönetilen kod yok"a
    /// çevriliyor (<c>AppRuntimeService</c>) ve o havuzlarda modül HİÇ YÜKLENMEZ. Ölçemediğimizde
    /// "destekleniyor" demek, tam olarak bu sessiz arızayı üretirdi.
    /// </remarks>
    private async Task<(string Surum, string Destek)> DestegiCozAsync(string siteName, CancellationToken iptal)
    {
        try
        {
            var yapilandirma = await _aspNet.GetSiteConfigurationAsync(siteName, iptal);
            var destek = HtaccessModuleRegistration.ClassifySupport(yapilandirma.ManagedRuntimeVersion);
            return (yapilandirma.ManagedRuntimeVersion, destek switch
            {
                HtaccessSupport.Supported => "supported",
                HtaccessSupport.UnsupportedNoManagedCode => "no_managed_code",
                HtaccessSupport.UnsupportedRuntimeVersion => "runtime_version",
                _ => "unknown"
            });
        }
        catch (Exception istisna)
        {
            _gunluk.LogWarning(istisna,
                "htaccess: {SiteName} sitesinin uygulama havuzu okunamadi; destek durumu BILINMIYOR olarak raporlaniyor.",
                siteName);
            return (string.Empty, "unknown");
        }
    }

    /// <summary>
    /// Site kökü altındaki bütün <c>.htaccess</c> dosyaları, kökten içeriye doğru.
    /// </summary>
    /// <remarks>
    /// <c>Directory.EnumerateFiles(..., AllDirectories)</c> KULLANILMIYOR: tek bir erişilemez alt
    /// klasör bütün numaralandırmayı istisnayla düşürür ve kullanıcı hiç dosya göremezdi. Elle
    /// yürüyüş, erişilemeyen dalı ATLAYIP kalanı gösterir — eksik liste, boş listeden iyidir ve
    /// eksiklik sınırlarla (derinlik/sayı) görünür kalır.
    /// </remarks>
    private List<HtaccessFileSummary> DosyalariBul(string kok)
    {
        var sonuc = new List<HtaccessFileSummary>();
        var kuyruk = new Queue<(string Yol, string Goreli, int Derinlik)>();
        kuyruk.Enqueue((kok, string.Empty, 0));

        while (kuyruk.Count > 0 && sonuc.Count < MaxDosyaSayisi)
        {
            var (yol, goreli, derinlik) = kuyruk.Dequeue();

            try
            {
                var dosya = Path.Combine(yol, HtaccessCascadeLoader.FileName);
                if (File.Exists(dosya))
                {
                    var bilgi = new FileInfo(dosya);
                    sonuc.Add(new HtaccessFileSummary
                    {
                        RelativeDirectory = goreli,
                        VirtualPath = goreli.Length == 0 ? "/" : "/" + goreli + "/",
                        SizeBytes = bilgi.Length,
                        LastWriteUtc = bilgi.LastWriteTimeUtc
                    });
                }

                if (derinlik >= MaxTaranacakDerinlik)
                {
                    continue;
                }

                foreach (var altDizin in Directory.EnumerateDirectories(yol))
                {
                    var ad = Path.GetFileName(altDizin);
                    if (string.IsNullOrEmpty(ad))
                    {
                        continue;
                    }

                    kuyruk.Enqueue((altDizin, goreli.Length == 0 ? ad : goreli + "/" + ad, derinlik + 1));
                }
            }
            catch (Exception istisna) when (istisna is IOException or UnauthorizedAccessException)
            {
                // Erişilemeyen dal ATLANIR; kalan liste yine de gösterilir.
                _gunluk.LogDebug(istisna, "htaccess: {Yol} taranamadi, atlandi.", yol);
            }
        }

        return sonuc.OrderBy(d => d.RelativeDirectory, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Motorun IIS içinde çalışmasını engelleyen ÖLÇÜLMÜŞ nedenler. Boş liste "engel yok" demektir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Liste ARTIK BOŞ ve bu bir ölçümün sonucu.</b> Buraya kadar tek bir engel yazılıydı
    /// (<c>htaccess.blocker.engine_not_net48</c>): motor <c>net8.0-windows</c>, IIS modülü ise
    /// <c>net48</c> olmak zorundaydı ve bir net8 derlemesi CLR 4 içinde yüklenemez. Motorun dokuz
    /// dosyasındaki yirmi beş satır yalnız .NET (Core) API'lerine dayandığı için bağlantı
    /// FİZİKSEL OLARAK yapılamıyordu.
    /// </para>
    /// <para>
    /// O satırlar taşınabilir hâle getirildi (bkz. <c>HtaccessPortableApi</c>) ve motorun kaynağı
    /// modül projesinde de derleniyor. Engelin kalktığı VARSAYILMIYOR, ölçüldü: motorun net48
    /// çıktısı ayrı bir CLR 4 sürecinde yükleniyor ve bir <c>RewriteRule</c> sonucu değiştiriyor
    /// (<c>HtaccessNet48EngineLoadTests</c>).
    /// </para>
    /// <para>
    /// <b>Yeni engel eklenecekse ÖLÇÜLMÜŞ olmalı.</b> Bu liste panelin "hiçbir şey olmayacak"
    /// şeridini besliyor; tahmini bir engel yazmak, çalışan bir özelliği bozuk göstermek olurdu.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> MotorEngelleri() => [];

    /// <summary>
    /// Motorun Apache'den BİLİNÇLİ sapmaları — anahtar olarak döner, metni arayüz çevirir.
    /// </summary>
    /// <remarks>
    /// Metin BURADA üretilmiyor: sapmaların tam anlatımı <c>HtaccessEngine</c> sınıf yorumunda
    /// yazılı ve arayüz i18n anahtarıyla gösteriyor. İki yerde iki farklı anlatım olsaydı biri
    /// güncellenir öbürü kalırdı.
    /// </remarks>
    private static IReadOnlyList<string> Sapmalar() =>
    [
        "htaccess.deviation.access_before_rewrite",
        "htaccess.deviation.directory_section",
        "htaccess.deviation.fatal_file_skipped",
        "htaccess.deviation.authuserfile_inside_root",
        "htaccess.deviation.directory_request_no_filename",
        "htaccess.deviation.regex_timeout_stops_all"
    ];
}
