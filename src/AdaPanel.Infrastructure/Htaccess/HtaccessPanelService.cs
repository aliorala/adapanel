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
    /// <b>ÇEKİRDEK MOTOR IIS İÇİNDE YÜKLENİYOR MU?</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>BU ALAN BİR KEZ YALAN SÖYLEDİ ve tanımı o yüzden değişti.</b> Değer
    /// <c>Support == "supported"</c> ile, yani YALNIZ uygulama havuzunun CLR sürümüne bakılarak
    /// dolduruluyordu. Havuz sürümü bir ÖN KOŞULDUR, kaydın kendisi DEĞİL: modül
    /// <c>applicationHost.config</c>'e hiç kaydedilmemiş olsa bile bu alan <c>true</c> derdi.
    /// Canlıda ölçülen tablo tam olarak buydu — panel <c>engineLoadedInIis: true</c>,
    /// <c>support: supported</c>, havuz <c>Integrated</c>/<c>v4.0</c> diyordu; panelin kendi
    /// benzetimi doğru cevabı üretiyordu (<c>302</c>); gerçek HTTP isteği ise <c>404</c>
    /// dönüyordu. Motor doğruydu, TESLİM HATTI kopuktu ve panel bunu göremediği için "her şey
    /// yolunda" diyordu.
    /// </para>
    /// <para>
    /// <b>YENİ TANIM — DÖRT ÖLÇÜMÜN BİRLEŞİMİ:</b>
    /// (1) uygulama havuzu yönetilen kod çalıştırıyor ve CLR sürümü uygun (<see cref="Support"/>),
    /// (2) modül <c>system.webServer/modules</c> içinde KAYITLI ve <c>type</c>'ı bizim modülümüzü
    /// gösteriyor, (3) <c>preCondition</c> modülü isteğin dışında bırakmıyor
    /// (<see cref="HtaccessModuleRegistrationState.PreConditionExcludesUnmanagedRequests"/>),
    /// (4) modülün çözdüğü VERİ KÖKÜ panelinkiyle AYNI — yoksa modül yüklenir, koşar ve panelin
    /// yazdığı anahtar dosyasını HİÇ göremez.
    /// Dördünden biri bile OKUNAMIYORSA sonuç <c>false</c>'tur — "bilmiyorum" asla "yüklü" diye
    /// raporlanmaz; hangi ölçümün eksik olduğu <see cref="EngineBlockers"/>'ta yazılıdır.
    /// </para>
    /// <para>
    /// <b>DÖRDÜNCÜ ÖLÇÜM SONRADAN EKLENDİ</b> ve sebebi ilk üçüyle aynı sınıftan: kayıt ölçülüyordu
    /// ama iki sürecin AYNI KLASÖRE baktığı ölçülmüyordu. Ayrıntılı gerekçe ve ölçüm
    /// <c>HtaccessPanelService.VeriKokuAyrisiyor</c> üzerinde.
    /// </para>
    /// <para>
    /// <b>HÂLÂ İDDİA EDİLMEYEN:</b> panel süreci <c>w3wp</c>'nin içini göremez, yani "şu an bu
    /// havuzda yüklü nesne var" diye bir ölçüm YOKTUR. <c>true</c>, "IIS'in modülü yüklemesi için
    /// gereken her şey ölçüldü ve yerinde" demektir — bu, eski hâlinden çok daha güçlü ama yine de
    /// sonlu bir iddiadır.
    /// </para>
    /// </remarks>
    public bool EngineLoadedInIis { get; init; }

    /// <summary>
    /// Modülün IIS kaydının HAM ÖLÇÜMÜ — <see cref="EngineLoadedInIis"/>'in dayanağı.
    /// </summary>
    /// <remarks>
    /// Tek bir <c>bool</c> yetmiyor: "kayıt yok" ile "kaydı okuyamadım" ile "kayıt var ama ön
    /// koşulu yanlış" bambaşka arızalardır ve kullanıcının yapacağı iş her birinde farklıdır.
    /// Ham değerler (<c>type</c>, <c>preCondition</c>) olduğu gibi veriliyor: panel onları
    /// yorumlar ama SAKLAMAZ — yöneticinin gördüğü şey IIS'te YAZAN şeydir.
    /// </remarks>
    public HtaccessModuleRegistrationState ModuleRegistration { get; init; } = new();

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
    private readonly IHtaccessModuleRegistry _kayit;
    private readonly HtaccessSwitchStore _anahtarlar;
    private readonly ILogger<HtaccessPanelService> _gunluk;

    public HtaccessPanelService(
        IHtaccessEngine motor,
        IIisSiteService siteler,
        IAspNetService aspNet,
        IHtaccessModuleRegistry kayit,
        IOptions<PanelRuntimeOptions> secenekler,
        ILogger<HtaccessPanelService> gunluk)
        : this(motor, siteler, aspNet, kayit, secenekler, gunluk, null)
    {
    }

    /// <param name="modulVeriKokuCozucu">
    /// Modülün (w3wp içindeki net48 derlemenin) veri kökünü NASIL çözdüğü. <c>null</c> = GERÇEK
    /// çözücü (<see cref="HtaccessHostingPaths.ResolveDataRootFromEnvironment"/>); üretimde her
    /// zaman budur ve DI bu aşırı yüklemeyi hiç görmez.
    ///
    /// NEDEN ENJEKTE EDİLEBİLİR: ölçülen şey İKİ SÜRECİN MUTABAKATI ve test sürecinde ikinci süreç
    /// YOKTUR. Tek alternatif <c>ADAPANEL_DATA_ROOT</c> ortam değişkenini testten yazmaktı; ortam
    /// değişkeni SÜREÇ GENELİDİR ve xunit test sınıflarını paralel koşturur, yani testler
    /// birbirinin kökünü ezerdi. Ayrışmayı ölçen bir düzeltmenin, kendisi ölçülemez bir yolla
    /// sınanması olurdu.
    /// </param>
    internal HtaccessPanelService(
        IHtaccessEngine motor,
        IIisSiteService siteler,
        IAspNetService aspNet,
        IHtaccessModuleRegistry kayit,
        IOptions<PanelRuntimeOptions> secenekler,
        ILogger<HtaccessPanelService> gunluk,
        Func<string>? modulVeriKokuCozucu)
    {
        _motor = motor;
        _siteler = siteler;
        _aspNet = aspNet;
        _kayit = kayit;
        _gunluk = gunluk;
        _modulVeriKokuCozucu = modulVeriKokuCozucu ?? HtaccessHostingPaths.ResolveDataRootFromEnvironment;

        var veriKoku = Path.GetFullPath(Environment.ExpandEnvironmentVariables(secenekler.Value.DataRoot));
        _anahtarlar = new HtaccessSwitchStore(veriKoku);
        _veriKoku = veriKoku;
    }

    /// <summary>Panelin ANAHTAR DOSYALARINI YAZDIĞI veri kökü (<c>Panel:DataRoot</c>, genişletilmiş).</summary>
    private readonly string _veriKoku;

    /// <summary>Modülün veri kökünü çözme yolu; üretimde gerçek ortam çözücüsü.</summary>
    private readonly Func<string> _modulVeriKokuCozucu;

    /// <summary>
    /// Panel ile modül AYNI veri kökünü mü çözüyor?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>DÖRDÜNCÜ ÖLÇÜM — ve var olma sebebi ölçülmüş bir SESSİZ ARIZA.</b> Panel anahtar
    /// dosyalarını <c>Panel:DataRoot</c> altına yazar; w3wp içindeki modül ise aynı kökü
    /// <see cref="HtaccessHostingPaths.ResolveDataRootFromEnvironment"/> ile, yani
    /// <c>ADAPANEL_DATA_ROOT</c> ortam değişkeninden ya da <c>%ProgramData%\AdaPanel</c>
    /// varsayılanından çözer. Modülün panelin appsettings.json'ını okuma yolu YOKTUR.
    /// </para>
    /// <para>
    /// <b>ÖLÇÜLDÜ:</b> <c>ADAPANEL_DATA_ROOT</c>'u kuran HİÇBİR ŞEY YOK — ne installer
    /// (<c>installer\AdaPanel.iss</c>), ne kurulum betikleri, ne panelin kendisi. Bu makinede
    /// makine ve süreç düzeyinde değer BOŞ ölçüldü. Yani modül HER ZAMAN varsayılana düşer.
    /// Varsayılan kurulumda iki taraf aynı köke bakar ve özellik çalışır — kusur bu yüzden
    /// bugüne kadar görünmedi.
    /// </para>
    /// <para>
    /// <b>ARIZA:</b> <c>Panel:DataRoot</c> varsayılandan BAŞKA bir değere alınırsa (desteklenen
    /// bir ayar; ayrıca <c>Panel__DataRoot</c> ortam değişkeniyle de ezilebilir) panel anahtarı
    /// yeni köke yazar, modül eski varsayılana bakmaya devam eder. Panel "açık" der, modül
    /// anahtarı HİÇ görmez, hiçbir istekte hiçbir kural uygulanmaz. Tam olarak bu sınıfın
    /// arızası: dış sistem panelin ürettiği yolu ARAMIYOR.
    /// </para>
    /// <para>
    /// Panel bu durumu DÜZELTEMEZ (ortam değişkenini yazmak w3wp/WAS yeniden başlatmadan etkili
    /// olmaz), ama SUSMAMALI. Ölçüp engel olarak bildiriyor.
    /// </para>
    /// </remarks>
    private bool VeriKokuAyrisiyor()
    {
        try
        {
            var modulunGordugu = HtaccessHostingPaths.ExpandFull(_modulVeriKokuCozucu());

            // Windows'ta yol karşılaştırması harf DUYARSIZ; sondaki ayraç anlamsız farktır.
            return !string.Equals(
                _veriKoku.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                modulunGordugu.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Kök ÇÖZÜLEMİYORSA "ayrışıyor" DEMEYİZ: uydurma bir engel, olmayan bir arızayı
            // rapor ederdi. Ölçemediğimiz şey hakkında iddia üretmemek bu dosyanın kuralı.
            return false;
        }
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

        // KAYIT SUNUCU GENELİDİR, site başına değil: modül applicationHost.config'e BİR KEZ
        // kaydedilir (gerekçesi HtaccessModuleRegistration.RegistrationScopeRationale). Yine de
        // her site raporunda okunuyor, çünkü kullanıcı sorunu SİTE ekranında yaşıyor: "bu sitede
        // neden çalışmıyor" sorusunun cevabı sunucu geneli bir eksiklik olabilir ve onu ayrı bir
        // ekrana saklamak, tam da bu turda kapatılan "kimse sebebini söylemiyor" davranışı olurdu.
        var kayit = _kayit.Read();
        var veriKokuAyrisiyor = VeriKokuAyrisiyor();

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
            ModuleRegistration = kayit,
            // UC OLCUMUN BIRLESIMI (gerekcesi HtaccessSiteReport.EngineLoadedInIis uzerinde):
            // havuz + kayit + on kosul. Eskiden yalniz ilki vardi ve panel, modul HIC kayitli
            // olmadan da "yuklu" diyordu -- canli olcumde tam olarak bu oldu.
            // DORT OLCUMUN BIRLESIMI (gerekcesi HtaccessSiteReport.EngineLoadedInIis uzerinde):
            // havuz + kayit + on kosul + VERI KOKU MUTABAKATI. Dorduncusu olmadan panel, modulun
            // hicbir zaman bakmayacagi bir klasore anahtar yazip "yuklu" diyebiliyordu.
            EngineLoadedInIis = destek == "supported" && kayit.Confirmed && !veriKokuAyrisiyor,
            EngineBlockers = MotorEngelleri(kayit, veriKokuAyrisiyor),
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
    /// <param name="mode">
    /// <c>off</c> / <c>observe</c> / <c>enforce</c>. NULL KABUL EDER ama GEÇERLİ SAYMAZ: tip
    /// bilerek <c>string?</c>, çünkü çağıran (denetleyici) eksik gövdeyi bir varsayılana çevirip
    /// örtmek yerine olduğu gibi buraya getirmeli — kararı <see cref="ModCozumle"/> verir ve boş
    /// değeri "enfroce" yazım hatasıyla aynı yerde REDDEDER.
    /// </param>
    public async Task<HtaccessSiteReport> SetModeAsync(
        string siteName, string? mode, string? note, CancellationToken iptal)
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
    /// <param name="mode">
    /// <c>off</c> / <c>observe</c> / <c>enforce</c>. NULL KABUL EDER ama GEÇERLİ SAYMAZ; gerekçesi
    /// <see cref="SetModeAsync"/>'in aynı parametresinde yazılı. Burada bedeli daha büyüktür:
    /// söylenmemiş bir kipin "off" sayılması sunucudaki HER siteyi birden kapatırdı.
    /// </param>
    public HtaccessServerSwitchState SetServerMode(string? mode, string? note)
    {
        var kip = ModCozumle(mode);
        _anahtarlar.SetGlobal(kip, note);
        return GetServerMode();
    }

    /// <summary>Modülün IIS kaydının ÖLÇÜLMÜŞ hâli. Site adı GEREKMEZ — kayıt sunucu genelidir.</summary>
    public HtaccessModuleRegistrationState GetModuleRegistration() => _kayit.Read();

    /// <summary>
    /// Modülü <c>applicationHost.config</c>'e KAYDEDER — bozuk/eksik kaydın panelden ONARIMI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden bir onarım yolu gerekti:</b> kayıt kurulumda bir kez yazılıyor
    /// (<c>scripts/install-htaccess-module.ps1</c>) ve o adım BEST-EFFORT çağrılıyor — başarısız
    /// olursa kurulum devam eder, çünkü panel bu modül olmadan da tam çalışır. Bedeli, sunucuda
    /// hiçbir belirti vermeden ölü bir özellikti: kaydın eksik olduğunu görmenin tek yolu sunucuya
    /// girip <c>appcmd</c> çalıştırmaktı. Panel artık eksikliği ÖLÇTÜĞÜNE göre (bkz.
    /// <see cref="MotorEngelleri"/>) düzeltmesini de sunmalı; "sorunu gösterip çaresini
    /// söylememek" bu iş kolunun düzeltmeye çalıştığı davranışın bir başka hâli olurdu.
    /// </para>
    /// <para>
    /// <b>KİP DEĞİŞTİRMEZ.</b> Kayıt, modülü sunucudaki her yönetilen havuza YÜKLETİR ama
    /// anahtarlar kapalıyken modül her isteği dokunmadan geçirir (<c>HtaccessRuntimeGate</c>).
    /// Onarımın kendiliğinden açması "kolaylık" gibi görünürdü ama kurulumun bilinçli varsayılanını
    /// (kurulum hiçbir sitenin davranışını değiştirmez) sessizce çiğnerdi.
    /// </para>
    /// </remarks>
    public HtaccessModuleRegistrationState RegisterModule() => _kayit.Register();

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
            //
            // SÖYLENMEMİŞ KİP DE (null / boş dize) BURAYA DÜŞER ve aynı gerekçeyle reddedilir.
            // Uzun süre düşmüyordu: denetleyici `request?.Mode ?? "off"` yazıyor, yani gövdesi
            // eksik bir POST bu kapıya HİÇ gelmeden geçerli bir "kapat" isteğine dönüşüyordu.
            // Sunucu geneli uçta bunun bedeli, sunucudaki HER sitenin desteğinin sessizce
            // kapanması ve denetim kaydının bunu bilinçli bir karar gibi yazmasıydı.
            _ => throw new AppException("htaccess.invalid_mode",
                details: string.IsNullOrWhiteSpace(mod)
                    ? "mode=<eksik>; gecerli degerler: off, observe, enforce"
                    : $"mode='{mod}'; gecerli degerler: off, observe, enforce")
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
    /// <b>BU LİSTE BOŞ DÖNÜYORDU ve boş liste "engel yok" diye KESİN bir iddiadır.</b> O iddia
    /// ölçülmemişti: derleme zamanı engeli (motor net8 / modül net48) gerçekten kalkmıştı
    /// (<c>HtaccessNet48EngineLoadTests</c> gerçek bir CLR 4 sürecinde ölçüyor), ama bundan
    /// "çalışma zamanında da engel yok" sonucu çıkarılmıştı. Canlıda modül IIS'in istek yoluna
    /// hiç girmiyordu ve panel tek bir engel bile göstermiyordu.
    /// </para>
    /// <para>
    /// <b>Artık engeller ÖLÇÜMDEN geliyor</b> (<see cref="IHtaccessModuleRegistry.Read"/>).
    /// Anahtar olarak dönüyorlar (<c>htaccess.blocker.*</c>) ve metni arayüz çeviriyor — sunucu
    /// kullanıcı metni üretmez, aynı gerekçe <see cref="Sapmalar"/>'da da yazılı.
    /// </para>
    /// <para>
    /// <b>HAVUZ KAYNAKLI nedenler BİLEREK BURADA DEĞİL:</b> "yönetilen kod yok" ve "yanlış CLR
    /// sürümü" zaten <see cref="HtaccessSiteReport.Support"/> alanında, kendi ayrı şeridinde
    /// gösteriliyor. İkinci kez engel olarak yazmak, aynı arızayı iki yerde gösterip kullanıcıya
    /// iki ayrı sorun varmış gibi okuturdu.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> MotorEngelleri(
        HtaccessModuleRegistrationState kayit,
        bool veriKokuAyrisiyor)
    {
        var engeller = new List<string>();

        // VERİ KÖKÜ AYRIŞMASI EN ÖNE ALINDI ve kaydın okunabilirliğinden BAĞIMSIZ bildirilir:
        // kayıt kusursuz olsa bile panel ile modül farklı klasörlere bakıyorsa özellik ÖLÜDÜR.
        // Diğer engellerin altına gömülseydi, "kayıt tamam" gören yönetici burada durur ve asıl
        // sebebi hiç görmezdi. Gerekçesinin tamamı VeriKokuAyrisiyor üzerinde yazılı.
        if (veriKokuAyrisiyor)
        {
            engeller.Add("htaccess.blocker.data_root_mismatch");
        }

        if (!kayit.Readable)
        {
            // OKUNAMADI != KAYIT YOK. İkisini aynı anahtara toplamak, yöneticiye "modülü kur"
            // dedirtirdi; oysa gerçek iş "panelin IIS yapılandırmasını okuyamamasını düzelt".
            engeller.Add("htaccess.blocker.registration_unreadable");
            return engeller;
        }

        if (!kayit.Present)
        {
            engeller.Add("htaccess.blocker.module_not_registered");

            // GAC ÖLÇÜMÜ YALNIZ BURADA ANLAMLI: kayıt zaten varken derlemenin yokluğu sitelerin
            // 500 vermesi demektir ve o ayrı bir yangındır; kayıt YOKKEN ise bu bilgi onarımın
            // yapılabilir olup olmadığını söyler (uç, derleme yoksa REDDEDER).
            if (kayit.Assembly.Presence == HtaccessModuleAssemblyPresence.Missing)
            {
                engeller.Add("htaccess.blocker.module_assembly_missing");
            }

            return engeller;
        }

        if (!kayit.TypeMatches)
        {
            engeller.Add("htaccess.blocker.module_type_mismatch");
        }

        if (kayit.PreConditionExcludesUnmanagedRequests)
        {
            // AYRI BİR ENGEL, "kayıt yok"un bir alt hâli DEĞİL: kayıt yerinde, modül yükleniyor,
            // ama IIS onu yalnız yönetilen işleyicilerin isteklerinde çalıştırıyor. .htaccess'in
            // bütün amacı PHP ve statik dosyalar olduğu için özellik "kurulu" görünüp hiçbir şey
            // yapmaz — panelin ayırt etmesi gereken en sinsi hâl budur.
            engeller.Add("htaccess.blocker.precondition_managed_handler");
        }

        return engeller;
    }

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
