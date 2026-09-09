using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Files;
using AdaPanel.Infrastructure.Security;
using AdaPanel.Infrastructure.SiteBuilder;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Backup;

public sealed partial class BackupService(
    IIisSiteService iisSiteService,
    IDatabaseService databaseService,
    IMailService mailService,
    IPanelPreferencesService preferencesService,
    IFtpService ftpService,
    JsonFileStore fileStore,
    IHttpClientFactory httpClientFactory,
    // İSTEĞE BAĞLI: şifreleme kasası. Verilmezse (dar kapsamlı testler) panelde kayıtlı parola
    // yok sayılır; istekle gelen parola yine çalışır.
    BackupEncryptionStore? encryptionStore = null,
    // İSTEĞE BAĞLI: dar kapsamlı testler bu servisi doğrudan kuruyor; günlükçüyü zorunlu yapmak
    // onlarca test kurulumunu kırardı. Üretimde DI her zaman verir.
    ILogger<BackupService>? logger = null,
    // İSTEĞE BAĞLI: ABONELİK yedeği için gerekenler. Yeni bağımlılıklar SONA ve varsayılanlı
    // eklendi — mevcut dar kapsamlı testler (site/şifreleme/budama) olduğu gibi derlenmeye devam
    // etsin diye. Bunlar verilmemişse abonelik yedeği AÇIKÇA reddedilir (sessizce eksik yedek
    // üretmek yerine).
    ICustomerScopeService? customerScopes = null,
    IDatabaseOwnershipService? databaseOwnership = null,
    // İSTEĞE BAĞLI: site yedeğine GERÇEK posta (ileti) koyan arşivleyici. Verilmemişse site yedeği
    // eskisi gibi yalnız hesap/alias ENVANTERİ taşır ve bunu manifestte AÇIKÇA söyler — sessizce
    // "posta dahil" demez.
    IMailAccountArchiver? mailArchiver = null,
    // İSTEĞE BAĞLI: kayıtlı uzak hedef parolasının çözücüsü. ELLE SİLMENİN uzak kopyayı da
    // silebilmesi için gerekir (kimlik, otomatik temizliğin okuduğu AYNI dosyadan gelir).
    // Verilmemişse uzak silme kimliksiz denenir ve başarısızlık kullanıcıya AÇIKÇA bildirilir.
    ISecretProtector? secretProtector = null,
    // İSTEĞE BAĞLI: yedeğin "dosyası kayıp" işaretlenmesi denetim kaydına düşsün diye. Yedeğin
    // kendiliğinden buharlaştığı hissi, izi olmayan bir değişiklikten doğar.
    IAuditService? auditService = null,
    // İSTEĞE BAĞLI: dosya taramasının ÖGE tavanı. Üretimde HİÇ verilmez (DI varsayılanı kullanır);
    // yalnızca testin bütçe korumasını GERÇEKTEN doldurabilmesi için var — üretim değeriyle bu,
    // testte iki milyon dosya yaratmak demekti, ki o da diski yorup hiçbir ek şey kanıtlamazdı.
    // Aynı seam kota/dosya boyutu yolunda da açıldı (CalculateSizeAsync'in internal aşırı yüklemesi)
    // ve gerekçesi orada yazılı. Dışarıya bir UÇ AÇMAZ: yeni bir HTTP yüzeyi yok, yalnız DI kurulumu
    // bunu görebilir.
    int? scanEntryBudget = null,
    // İSTEĞE BAĞLI: "iç ağ hedeflerine izin ver" açık kapısını taşıyan seçenekler. Verilmemişse
    // (dar kapsamlı testler) açık kapı KAPALI sayılır — güvenli taraf varsayılan olsun diye.
    Microsoft.Extensions.Options.IOptions<Options.PanelRuntimeOptions>? runtimeOptions = null,
    // İSTEĞE BAĞLI: adres çözümleyici. Üretimde gerçek DNS; testte sahte, çünkü bir birim testi
    // GERÇEKTEN 169.254.169.254'e çözümleme yapmamalı (ve yapamaz da).
    OutboundAddressGuard.OutboundHostResolver? outboundResolver = null,
    // ===================================================================================
    // İSTEĞE BAĞLI AMA VARSAYILANI GÜVENLİ TARAF: "bu isteği yapan sunucu SAHİBİ mi?"
    //
    // Yalnızca panel yedeğinden ZAMANLANMIŞ GÖREV kaydetmek için okunur (arşivden gelen ham XML
    // = SYSTEM'de kod çalıştırma yetkisi; gerekçe TryRestoreScheduledTasksAsync'te).
    //
    // İkisi de verilmezse (dar kapsamlı testler, arka plan çağrıları) sonuç "SAHİP DEĞİL"dir.
    // Bu bilinçli: eksik bir bağımlılığın yetkiyi AÇMASI, korumayı DI kurulumunun bir yan
    // etkisine bağlamak olurdu. Kapalı taraf en kötü ihtimalle görevleri uyarıyla atlar; açık
    // taraf sessizce SYSTEM verirdi.
    // ===================================================================================
    IServerOwnerService? serverOwnerService = null,
    Microsoft.AspNetCore.Http.IHttpContextAccessor? httpContextAccessor = null) : IBackupService
{
    /// <summary>İsteği yapan kullanıcı bu sunucunun SAHİBİ mi?</summary>
    /// <remarks>
    /// Kimlik istekten okunur, gövdeden DEĞİL: bu deponun kapanmış hata sınıfı tam olarak
    /// "kapsam denetimi doğru ama denetlenen değeri isteğin kendisi seçiyor" idi. Sahiplik kararı
    /// da <see cref="IServerOwnerService"/>'e bırakılır — panelin HTTP yüzeyinden yazılamayan
    /// <c>config/server-owner.json</c> kaydını okuyan TEK yer orasıdır.
    /// </remarks>
    private async Task<bool> CaginSahibiMiAsync(CancellationToken cancellationToken)
    {
        if (serverOwnerService is null || httpContextAccessor?.HttpContext is not { } context)
        {
            return false;
        }

        var ham = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(ham, out var kullanici)
               && await serverOwnerService.IsServerOwnerAsync(kullanici, cancellationToken);
    }

    /// <summary>
    /// Uzak yedek hedefinin adresi panelin ULAŞMAMASI gereken bir yere mi bakıyor; bakıyorsa
    /// <c>backup.remote_target_forbidden</c> ile reddeder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Yüzey neden var:</b> <c>RemoteUrl</c> HTTP istek gövdesinden gelir ve panel o adrese
    /// SUNUCUNUN ağından, üstelik kullanıcının verdiği kimlikle bağlanır. Denetimsiz bırakılırsa
    /// <c>targetType=http</c> + <c>RemoteUrl=http://127.0.0.1:9200/…</c> panelin iç servislerine
    /// PUT/DELETE/HEAD atan bir vekile döner; <c>webdav</c> ayrıca PROPFIND ve MKCOL verir.
    /// </para>
    /// <para>
    /// <b>EN KESKİN UÇ "bağlantıyı test et"tir</b> (<see cref="TestRemoteTargetAsync"/>): verilen
    /// adrese HEMEN bir dosya yükler, yani tek istekle kör olmayan bir sonda sağlar. Bu yüzden
    /// denetim iki çağrı yerine de bağlandı — yalnız yedek alma yoluna bağlansaydı test ucu
    /// açık kalırdı ve o uç daha ucuz sömürülür.
    /// </para>
    /// <para>
    /// <b>OAUTH HEDEFLERİ (dropbox/onedrive) BİLEREK DIŞARIDA.</b> Onlarda <c>Uri.Host</c> bir ağ
    /// konağı DEĞİL, kullanıcının klasör yoludur (<c>dropbox:///yedekler</c>); gerçek uç noktalar
    /// koda gömülü sabit adreslerdir. Bunları çözümlemeye kalkmak hiçbir şey ölçmez, yalnız
    /// çalışan bir hedefi "çözülemedi" diye reddederdi. Aynı gerekçeyle <c>local</c> de dışarıda.
    /// </para>
    /// <para>
    /// <b>DÜRÜST SINIR — YÖNLENDİRME İZLENİYOR.</b> Ölçüldü: <c>http</c>/<c>webdav</c>/<c>s3</c>/
    /// <c>azure</c> yüklemeleri <c>httpClientFactory.CreateClient()</c> ile VARSAYILAN istemciyi
    /// alır ve <c>ServiceCollectionExtensions</c> içinde varsayılan istemciye handler
    /// yapılandırılmamıştır — yani <c>AllowAutoRedirect</c> .NET varsayılanı olan <c>true</c>'dur
    /// (adlı <c>AdaPanel.LocalHealth</c> ve <c>DbToolsProxy</c> istemcileri açıkça <c>false</c>
    /// yapar, varsayılan yapmaz). Dolayısıyla dış bir adres <c>302 Location:
    /// http://169.254.169.254/</c> ile iç ağa atlarsa bu kapı GÖRMEZ. Varsayılan istemciyi
    /// yönlendirmesiz yapmak paneldeki her <c>CreateClient()</c> çağrısını etkilerdi (bileşen
    /// indirme, GitHub sürüm çözümleme — hepsi yönlendirmeye GÜVENİYOR), o yüzden burada
    /// yapılmadı ve AÇIKÇA rapor edildi.
    /// </para>
    /// </remarks>
    private async Task EnsureRemoteTargetAllowedAsync(BackupTarget target, CancellationToken cancellationToken)
    {
        if (target.Uri is null || target.Type == "local" || IsOAuthTarget(target.Type))
        {
            return;
        }

        // DnsSafeHost: IPv6 literalinde köşeli parantezleri atar, IDN adı punycode'a çevirir ve
        // ondalık/sekizlik/onaltılık IPv4 yazımlarını noktalı dörtlüye normalleştirir (ölçüldü).
        var engellenen = await OutboundAddressGuard.FindBlockedHostAsync(
            target.Uri.DnsSafeHost,
            outboundResolver ?? OutboundAddressGuard.SystemResolver,
            OutboundTargetPolicy.Connecting(runtimeOptions?.Value.AllowInternalOutboundTargets ?? false),
            cancellationToken);

        if (engellenen is not null)
        {
            throw new AppException("backup.remote_target_forbidden", 403, 403, engellenen);
        }
    }

    /// <summary>
    /// Uzak hedefe giden HER isteğin istemcisi: bağlantı ANINDA adres denetimi yapan
    /// <see cref="OutboundConnectGuard.GuardedClientName"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>YUKARIDAKİ KAPININ DÜRÜST SINIRI BURADA KAPANDI.</b> Ölçülmüştü: bu yollar
    /// <c>CreateClient()</c> ile VARSAYILAN istemciyi alıyordu ve orada <c>AllowAutoRedirect</c>
    /// .NET varsayılanı olan <c>true</c>'dur — yani doğrulamayı geçmiş dış bir adres
    /// <c>302 Location: http://169.254.169.254/</c> ile iç ağa atlayabiliyordu. Denetlenen istemci
    /// yönlendirmeyi İZLEMEYE DEVAM EDER (S3 bölge yönlendirmesi, <c>http→https</c> gibi meşru
    /// zincirler kırılmasın diye) ama zincirin HER ADIMI soket açılmadan önce denetlenir.
    /// </para>
    /// <para>
    /// <b>Neden bulut (dropbox/onedrive) çağrıları da bu istemciyi kullanıyor:</b> onların uç
    /// noktaları koda gömülü GENEL adreslerdir, yani denetimden etkilenmezler. Ayrı bir istemci
    /// kullansalardı "hangi çağrı hangi istemcide" sorusu ikinci bir karar noktası doğururdu;
    /// tek istemci, ölçülmesi gereken tek davranış demektir.
    /// </para>
    /// </remarks>
    private HttpClient CreateRemoteHttpClient()
        => httpClientFactory.CreateClient(OutboundConnectGuard.GuardedClientName);

    /// <summary>
    /// Uzak hedef kimliklerinin TEK kaynağı (otomasyon ayarları). Elle silme ile otomatik
    /// budamanın aynı kimlikleri kullanmasını sağlar; ikinci bir yapılandırma yolu açılmadı.
    /// </summary>
    private BackupRemoteCredentialReader RemoteCredentials => new(fileStore, secretProtector);

    /// <summary>
    /// FTP komutlarının TEK yüzeyi. Üretimde <see cref="FtpWebRequestCommands"/>; testte sahte.
    /// </summary>
    /// <remarks>
    /// <b>Neden kurucu parametresi DEĞİL:</b> <see cref="IBackupFtpCommands"/> <c>internal</c>'dır
    /// (dışarıya yeni bir genel yüzey açmamak için) ve genel bir kurucunun parametresi olamaz.
    /// Ölçüm ihtiyacı gerçek: 550'nin "dosya yok" mu "izin yok" mu olduğu kararı ancak GERÇEK bir
    /// FTP sunucusuna bağlanmadan denenebilirse korunabilir.
    /// </remarks>
    internal IBackupFtpCommands FtpCommands { get; init; } = new FtpWebRequestCommands();

    private const string IndexPath = "config/backups.json";

    /// <summary>
    /// "Yerelden silindi, uzak kopyası hedefte kaldı" durumu. Dize TEK yerde: budama, elle silme,
    /// budama koruması ve listeye çıkan alan aynı sabiti okur — biri değişip diğeri unutulursa
    /// işaretli kayıt sessizce görünmez olurdu.
    /// </summary>
    private const string RemoteDeleteFailedStatus = "deleted_remote_failed";

    /// <summary>
    /// Yedek dosya taramasının ÖGE üst sınırı (dosya + klasör).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dosya yöneticisinin/kotanın bütçesinden (200.000) BİLEREK bir kat daha büyük: o ikisi bir
    /// HTTP isteğinin içinde çalışır ve kullanıcı ekranın başında BEKLER; yedek ise arka planda
    /// çalışan bir toplu iştir. 200.000 burada gerçek ve meşru bir siteyi ("node_modules"lu bir
    /// uygulama tek başına yüz binlerce öge tutabilir) yarıda keserdi — yani koruma, korumak
    /// istediği veriyi kaybettirirdi.
    /// </para>
    /// <para>
    /// Yine de SONSUZ DEĞİL: bir kavşak zaten <see cref="SafeDirectoryWalk"/> tarafından
    /// atlanıyor, bu tavan ise kavşaksız da olsa (milyonlarca küçük dosya üreten bir kiracı)
    /// gecelik işin sonsuza kadar sürmesini engelleyen İKİNCİ settir.
    /// </para>
    /// </remarks>
    private const int TaramaOgeButcesi = 2_000_000;

    /// <summary>
    /// Yedek dosya taramasının SÜRE üst sınırı.
    /// </summary>
    /// <remarks>
    /// Yalnız TARAMAYI (dizin okuma) sınırlar; arşive yazma bu bütçenin dışındadır. Otomatik
    /// yedekleme genel kilidin içinde çalıştığı için (bkz. <c>RemoteUploadTimeout</c> gerekçesi)
    /// süresiz bir dizin taraması, paneli o süre boyunca kilitli tutardı.
    /// </remarks>
    private const int TaramaSureButcesiMs = 10 * 60 * 1000;

    /// <summary>Bir yedek işinin TAMAMI için tek tarama bütçesi üretir.</summary>
    private SafeDirectoryWalkBudget YeniTaramaButcesi()
        => new(scanEntryBudget ?? TaramaOgeButcesi, TaramaSureButcesiMs);

    /// <summary>
    /// Uzak hedefe yükleme için ÜST SINIR. Sonsuz bekleme kabul edilemez: yükleme, otomatik
    /// yedeklemede global kilidin içinde çalışıyor ve aynı kilidi giriş akışı da alıyor.
    /// </summary>
    private static readonly TimeSpan RemoteUploadTimeout = TimeSpan.FromHours(2);

    /// <summary>Veri akışının DURMASINA tahammül süresi (yalnız FTP; HttpClient'ta ayrı ayarı yok).</summary>
    private static readonly TimeSpan RemoteUploadStallTimeout = TimeSpan.FromMinutes(5);

    private static readonly System.Text.Json.JsonSerializerOptions BackupJsonOptions =
        new() { WriteIndented = true };

    /// <summary>
    /// Kayıtlı yedekler. Her kaydın YEREL dosyası listeleme anında ÖLÇÜLÜR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden yerel ölçüm burada:</b> liste eskiden JSON'u okuyup olduğu gibi dönüyordu; diskten
    /// elle silinmiş bir yedek panelde "geri yüklenebilir" görünmeye devam ediyordu ve bu ancak
    /// geri yükleme denemesinde — felaket anında — anlaşılıyordu. Ölçümün maliyeti kayıt başına
    /// bir <c>File.Exists</c> çağrısıdır (dizin girdisi okuması, ağ yok); yüzlerce kayıtta bile
    /// milisaniyeler mertebesindedir. Bu yüzden her listelemede yapılır.
    /// </para>
    /// <para>
    /// <b>Neden UZAK kopya burada ölçülmüyor:</b> uzak ölçüm kayıt başına bir AĞ isteğidir. Her
    /// listelemede yapmak paneli yavaşlatır ve hedefin hız sınırına takar. Uzak ölçüm
    /// <see cref="VerifyBackupsAsync"/> ile yapılır; sonucu kayıtta saklanır ve burada olduğu
    /// gibi taşınır.
    /// </para>
    /// <para>
    /// <b>Neden kayıt SİLİNMİYOR:</b> "dosya yok" demek yedeği yok saymaya yeter, kaydı silmeye
    /// yetmez. Sessizce silinen bir kayıt, yedeğin kendiliğinden buharlaştığı hissini verir;
    /// kullanıcı ne olduğunu asla öğrenemez. Kayıt İŞARETLENİR, kararı kullanıcı verir.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyCollection<BackupRecordDto>> GetBackupsAsync(CancellationToken cancellationToken = default)
    {
        var records = await fileStore.ReadAsync(IndexPath, Array.Empty<BackupRecordDto>(), cancellationToken);
        foreach (var record in records)
        {
            record.LocalMissing = IsLocalFileMissing(record);
            // Durum dizesini arayüzün ELLE karşılaştırmasını beklemek, o dizeyi iki yerde
            // yaşatmak demekti (ve bugüne kadar hiçbir ekran onu karşılaştırmadı: işaretli kayıt
            // görünmez kaldı). Türetme TEK yerde, burada.
            record.RemoteDeleteFailed = string.Equals(
                record.Status, RemoteDeleteFailedStatus, StringComparison.Ordinal);
            ApplyCopyPresence(record);
        }

        return records
            .OrderByDescending(record => record.CreatedAtUtc)
            .ToArray();
    }

    /// <summary>
    /// İki kopyanın (yerel + uzak) durumunu kayda işler: neyin ölçüldüğü, neyin ölçülmediği.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>MALİYET KARARI — burada AĞ İSTEĞİ YOKTUR.</b> Yerel varlık her listelemede ölçülür
    /// (kayıt başına bir dizin girdisi okuması, milisaniyeler); uzak varlık ise kayıt başına BİR
    /// AĞ İSTEĞİDİR. Yüz kayıtlı bir panelde her liste çizilişinde yüz FTP/HTTP isteği atmak
    /// listeyi kilitler, hedefin hız sınırına takılır ve bu depoda daha önce ölçülmüş bir hizmet
    /// reddi biçimidir. Bu yüzden uzak ölçüm İSTEK ÜZERİNEDİR (<see cref="VerifyBackupsAsync"/>,
    /// kullanıcının bilerek bastığı "Doğrula" düğmesi) ve sonucu kayıtta ÖNBELLEKLENİR
    /// (<c>RemoteState</c> + <c>RemoteVerifiedUtc</c>). Liste, ölçümün NE ZAMAN yapıldığını da
    /// taşır ki kullanıcı bayat bir ölçüme kesinmiş gibi bakmasın.
    /// </para>
    /// <para>
    /// <b>Eski kayıtlarda geriye dönük uyum:</b> <c>RemoteState</c> boş gelirse (bu alan
    /// eklenmeden önce yazılmış kayıtlar) daha önce ölçülmüş <c>RemoteMissing</c>/
    /// <c>RemoteVerifiedUtc</c> ikilisinden türetilir; hiç ölçülmemişse <c>unknown</c> kalır —
    /// "duruyor" DENMEZ.
    /// </para>
    /// </remarks>
    private static void ApplyCopyPresence(BackupRecordDto record)
    {
        var uzakVarMi = !string.IsNullOrWhiteSpace(record.RemoteUri);

        record.RemoteState = !uzakVarMi
            ? BackupPresence.None
            : record.RemoteState switch
            {
                BackupPresence.Present or BackupPresence.Missing => record.RemoteState,
                // Alan eklenmeden önce yazılmış kayıt: ölçüm YAPILMIŞSA ondan türet.
                _ when record.RemoteVerifiedUtc is not null =>
                    record.RemoteMissing ? BackupPresence.Missing : BackupPresence.Present,
                _ => BackupPresence.Unknown
            };

        // YALNIZ KESİN ölçümde uyarı: "ölçemedim" hâlinde off-site kopyayı kayıp ilan etmek,
        // ağ arızasını felaket haberine çevirirdi.
        record.OffsiteCopyLost =
            record.RemoteState == BackupPresence.Missing && !record.LocalMissing
            && !string.IsNullOrWhiteSpace(record.Path);

        // "Artık hiç kopya yok": yerel yok VE uzak ya hiç yoktu ya da ölçülüp yok bulundu.
        record.AllCopiesMissing =
            (record.LocalMissing || string.IsNullOrWhiteSpace(record.Path))
            && record.RemoteState is BackupPresence.None or BackupPresence.Missing;
    }

    /// <summary>
    /// Kaydın YEREL dosyası gerçekten yok mu? Yolu hiç olmayan (yalnız uzak) kayıtlar "kayıp"
    /// SAYILMAZ — onların yereli hiç olmadı, silinmedi.
    /// </summary>
    private static bool IsLocalFileMissing(BackupRecordDto record) =>
        !string.IsNullOrWhiteSpace(record.Path)
        && !File.Exists(record.Path)
        && !Directory.Exists(record.Path);

    /// <summary>
    /// Denetim kaydı yazar (servis verilmemişse sessizce geçer; dar kapsamlı testler).
    /// </summary>
    /// <param name="subscriptionUserId">
    /// Kaydın KONUSU olan aboneliğin sahibi (bilinmiyorsa <c>null</c>).
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Neden konu ZORUNLU parametre:</b> bu servis kaydı <c>userId: null</c> ile yazıyordu ve
    /// <c>SubscriptionUserId</c>'yi de doldurmuyordu. İkisi de boş olan kayıt
    /// (<c>AuditLogQueryEngine.cs:62-67</c>) kapsamlı role — yani KİRACIYA — HİÇ gösterilmez:
    /// müşterinin yedeği "kayıp" işaretlendiğinde bunun izi kendisine görünmüyordu. Konuyu
    /// doldurmak, kaydı sahibinin ekranına taşıyan tek şeydir.
    /// </para>
    /// <para>
    /// <b>Eylemi YAPAN neden hâlâ boş:</b> bu yollar (uzlaştırma, doğrulama sonrası işaretleme)
    /// arka plandaki otomasyondan da çalışır ve servisin HTTP kimliği yoktur. Var olmayan bir
    /// aktör uydurmak yerine boş bırakılır; kaydın kime ait olduğu KONU sütunundan okunur.
    /// Uçtan gelen (kullanıcının bastığı) işlemler denetim kaydını <c>BackupController</c>'da,
    /// gerçek kimlikle yazar.
    /// </para>
    /// </remarks>
    private async Task WriteAuditAsync(
        string action,
        string resource,
        string details,
        Guid? subscriptionUserId,
        CancellationToken cancellationToken)
    {
        if (auditService is null)
        {
            return;
        }

        try
        {
            await auditService.WriteForSubscriptionAsync(
                action,
                resource,
                details,
                null,
                subscriptionUserId,
                null,
                cancellationToken);
        }
        catch (Exception exception)
        {
            // Denetim kaydı yazılamaması ASIL işi (silme/doğrulama) durdurmamalı.
            logger?.LogWarning(exception, "Yedek denetim kaydi yazilamadi: {Action}", action);
        }
    }

    /// <summary>
    /// Bir yedek kaydının KONUSU olan abonelik sahibi: abonelik yedeğinde kaydın kendi alanı,
    /// site yedeğinde sitenin atandığı müşteri. Ölçülemezse <c>null</c> (tahmin edilmez).
    /// </summary>
    private async Task<Guid?> ResolveAuditSubjectAsync(BackupRecordDto record, CancellationToken cancellationToken)
    {
        if (Guid.TryParseExact(record.SubscriptionUserId, "N", out var abonelik))
        {
            return abonelik;
        }

        if (customerScopes is null || !string.Equals(record.Type, "site", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return await customerScopes.GetSiteOwnerUserIdAsync(record.Source, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Sahiplik okunamadıysa kayıt yine yazılır, yalnızca konusu boş kalır: denetim
            // kaydını hiç yazmamak, eksik yazmaktan kötüdür.
            logger?.LogWarning(exception, "Yedek denetim kaydi icin site sahibi okunamadi: {Site}", record.Source);
            return null;
        }
    }

    // =====================================================================================
    // ŞİFRELEME (tek kaynak). Yedekler FTP/S3/WebDAV gibi ÜÇÜNCÜ TARAF depolara gidiyor;
    // şifresiz bir arşiv müşterinin veritabanı dökümünü ve postasını o depoya erişen herkese
    // açar. Biçim ve kripto BackupArchiveCipher'da; burada yalnızca "ne zaman, hangi parolayla"
    // kararı verilir. Şifreleme ile yükleme/kayıt akışı tek yerde birleştirilmiştir
    // (FinalizeBackupAsync): dört yedek türünün her birinde ayrı ayrı yazılsaydı, biri
    // unutulduğunda o tür sessizce şifresiz kalırdı.
    // =====================================================================================

    /// <summary>
    /// Kullanılacak şifreleme parolası: önce istekteki, yoksa panelde KAYITLI olan. İkisi de yoksa
    /// boş döner = yedek şifresiz alınır. Kayıtlı parola, zamanlanmış (kimse ekranda değilken
    /// çalışan) yedeklerin şifrelenebilmesinin tek yoludur.
    /// </summary>
    private async Task<string> ResolveEncryptionPasswordAsync(string? requestPassword, CancellationToken cancellationToken)
    {
        var password = (requestPassword ?? string.Empty).Trim();
        if (password.Length > 0)
        {
            return password;
        }

        return encryptionStore is null ? string.Empty : await encryptionStore.GetPasswordAsync(cancellationToken);
    }

    /// <summary>
    /// Üretilmiş yedek dosyasını (gerekiyorsa) şifreler. Şifreleme başarısızsa hem yarım şifreli
    /// dosya hem de DÜZ METİN kopya silinir ve hata yükselir: kullanıcı "şifreli yedek" istediyse
    /// sessizce şifresiz bir dosya bırakmak, korumayı yalan söylemek olurdu.
    /// </summary>
    private static async Task<(string Path, string Name, bool Encrypted)> ProtectArchiveAsync(
        string localPath,
        string fileName,
        string password,
        CancellationToken cancellationToken)
    {
        if (password.Length == 0)
        {
            return (localPath, fileName, false);
        }

        var encryptedPath = localPath + BackupArchiveCipher.EncryptedExtension;
        try
        {
            await BackupArchiveCipher.EncryptFileAsync(localPath, encryptedPath, password, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryDeleteFile(encryptedPath);
            TryDeleteFile(localPath);
            throw new AppException("backup.encryption_failed", 500, 500, exception.Message);
        }

        // Düz metin kopya diskte KALMAZ; kalsaydı şifreleme yalnızca uzak kopyayı korur, sunucuda
        // duran asıl dosya korumasız olurdu.
        TryDeleteFile(localPath);
        return (encryptedPath, fileName + BackupArchiveCipher.EncryptedExtension, true);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Temizlik best-effort.
        }
    }

    /// <summary>
    /// Üretilmiş yedek dosyasını (gerekiyorsa) sıkıştırır. Hangi biçimin sıkıştırılacağı kararı
    /// <see cref="BackupDumpCompression.ShouldCompress"/>'tedir — burada "ne zaman" değil yalnızca
    /// "nasıl" vardır.
    /// </summary>
    /// <remarks>
    /// <b>Sıkıştırma başarısızsa YEDEK ÖLMEZ:</b> düz dosya olduğu gibi korunur, kayda görünür bir
    /// uyarı düşer. Bu, <see cref="ProtectArchiveAsync"/>'in tam TERSİ davranıştır ve fark bilinçlidir:
    /// şifreleme bir GÜVENLİK SÖZÜDÜR, tutulamıyorsa sessizce şifresiz dosya bırakmak yalan olur;
    /// sıkıştırma ise yalnızca bir BOYUT iyileştirmesidir — disk dolduğu için gzip yazılamadı diye
    /// o gecenin yedeğini hiç almamak, korumasız değil YEDEKSİZ bırakmak olurdu. Geri yükleme her
    /// iki durumda da çalışır: biçim kararı dosya başlığından verilir.
    /// </remarks>
    private async Task<(string Path, string Name, bool Compressed)> CompressArchiveAsync(
        string localPath,
        string fileName,
        string format,
        List<BackupRestoreWarningDto> warnings,
        CancellationToken cancellationToken)
    {
        if (!BackupDumpCompression.ShouldCompress(format))
        {
            return (localPath, fileName, false);
        }

        var compressedPath = localPath + BackupDumpCompression.CompressedExtension;
        try
        {
            await BackupDumpCompression.CompressFileAsync(localPath, compressedPath, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryDeleteFile(compressedPath);
            logger?.LogWarning(exception, "Yedek sikistirilamadi, duz dosya korundu: {File}", fileName);
            warnings.Add(new BackupRestoreWarningDto
            {
                MessageKey = "backup.compression_failed",
                Target = format,
                Detail = exception.Message
            });
            return (localPath, fileName, false);
        }

        // Düz kopya diskte KALMAZ; kalsaydı kazanç sıfır olurdu (iki dosya birden dururdu).
        TryDeleteFile(localPath);
        return (compressedPath, fileName + BackupDumpCompression.CompressedExtension, true);
    }

    /// <summary>
    /// Her yedek akışının ORTAK son adımı: sıkıştır → şifrele → uzak hedefe yükle → kaydı oluştur
    /// ve listeye ekle. (Sıkıştırmanın şifrelemeden ÖNCE gelmesi zorunludur; gerekçe aşağıda.)
    /// </summary>
    /// <param name="chain">
    /// Artımsal zincir damgası (zincirsiz yedeklerde <c>null</c>). Damganın kayda yazılması TEK
    /// yerde olur: dört yedek akışının her birinde ayrı ayrı yazılsaydı, biri unutulduğunda o tür
    /// zincire ait olduğunu bilmeyen — ve bu yüzden budamada zinciri kıran — kayıtlar üretirdi.
    /// </param>
    /// <param name="fileIndex">
    /// Arşivin dosya indeksi. Diskteki yan kayıt (<c>backups/index/&lt;id&gt;.json</c>) bir sonraki
    /// artımsal halkanın karşılaştırma temelidir; ŞİFRELİ arşivde indeksi arşivden okumak bütün
    /// dosyayı çözmeyi gerektirdiği için yan kayıt olmadan artımsal fiilen çalışmazdı.
    /// </param>
    /// <param name="placement">
    /// Yedeğin YERİ (gün + kategori). ZORUNLU tutuldu: isteğe bağlı olsaydı yeni bir yedek türü
    /// eklendiğinde parametreyi vermemek DERLENİR ve o tür sessizce uzak hedefin köküne düz
    /// yüklenirdi — düzeltilen hatanın ta kendisi. Şimdi vermeyi unutmak derleme hatasıdır.
    /// </param>
    /// <param name="skipped">
    /// Arşive GİREMEYEN kaynak dosyalar. <paramref name="placement"/> ile aynı gerekçeyle ZORUNLU:
    /// isteğe bağlı olsaydı yeni bir yedek türü eklendiğinde vermeyi unutmak DERLENİR ve o tür
    /// atladığı dosyaları sessizce yutmaya devam ederdi. Dosya taraması olmayan türlerde
    /// (veritabanı dökümü, posta deposu) açıkça <c>null</c> geçilir — "burada taranan dosya yok"
    /// da bir karardır ve görünür olmalıdır.
    /// </param>
    private async Task<BackupRecordDto> FinalizeBackupAsync(
        string localPath,
        string fileName,
        string type,
        string source,
        string format,
        BackupTargetRequest request,
        BackupPlacement placement,
        BackupSkipLog? skipped,
        CancellationToken cancellationToken,
        ChainStamp? chain = null,
        BackupFileIndex? fileIndex = null,
        string subscriptionUserId = "",
        int itemCount = 0,
        IReadOnlyCollection<BackupRestoreWarningDto>? contentWarnings = null)
    {
        // SIRA KİLİTLİ: ÖNCE SIKIŞTIR, SONRA ŞİFRELE.
        //
        // Şifrelenmiş veri rastgeleye yakındır ve SIKIŞMAZ; ters sırada dosya küçülmez, yalnızca
        // CPU harcanırdı. Bu yüzden sıkıştırma tam burada — ProtectArchiveAsync'ten ÖNCE — durur ve
        // sıra bir testle korunur (BackupDumpCompressionTests).
        //
        // İkisi de FinalizeBackupAsync'te toplanmıştır: dört yedek akışının her birinde ayrı ayrı
        // yazılsaydı, biri unutulduğunda o tür sessizce ham (ve/veya şifresiz) kalırdı.
        var warnings = new List<BackupRestoreWarningDto>();

        // BÜTÜNLÜK TAM BURADA, SARMALAYICILARDAN ÖNCE ÖLÇÜLÜR.
        //
        // Sırası zorunlu: bir sonraki satır dosyayı sıkıştırıp ARDINDAN şifreler ve ham kopyayı
        // siler. Ölçüm sonraya bırakılsaydı elimizde yalnız parolayla açılabilen bir sarmalayıcı
        // kalırdı; yani "yedeğin içinde bir şey var mı" sorusu bir daha ucuz sorulamazdı.
        //
        // ÖLÇÜLEN HATA: bu satırların olmadığı hâlde boyut yalnızca KAYDA YAZILIYOR, hiç
        // DENETLENMİYORDU. Sıfır baytlık ya da tek girdisi olmayan bir arşiv "created" damgasıyla
        // listeye giriyordu. Neyin ölçüldüğü ve NEDEN bu kadarının ölçüldüğü BackupArchiveProbe'ta
        // yazılı (kısaca: merkezî dizin okunur, tek bayt açılmaz).
        var butunluk = BackupArchiveProbe.Measure(localPath, format);

        var (compressedPath, compressedName, compressed) =
            await CompressArchiveAsync(localPath, fileName, format, warnings, cancellationToken);

        var password = await ResolveEncryptionPasswordAsync(request.EncryptionPassword, cancellationToken);
        var (finalPath, finalName, encrypted) = await ProtectArchiveAsync(compressedPath, compressedName, password, cancellationToken);

        var target = ResolveTarget(request.TargetType, request.RemoteUrl, request.RemoteUserName, request.RemotePassword, request.RemoteRegion);
        await EnsureRemoteTargetAllowedAsync(target, cancellationToken);

        // Hash the finished archive before starting the remote operation. Once transfer begins,
        // request cancellation must not prevent journaling this completed local copy.
        var contentHash = await BackupContentHash.ComputeAsync(finalPath, cancellationToken);

        // YEDEK HER ZAMAN ÖNCE YEREL DİSKE ALINIR; uzak hedef bunun YERİNE değil ÜSTÜNE gelir.
        //
        // Uzak yükleme hatası ESKİDEN İSTİSNA OLARAK YUKARI ÇIKIYORDU ve kayıt bu satırdan sonra
        // yazıldığı için hiç oluşmuyordu. Sonuç: alınmış, sağlam, YEREL bir yedek diskte duruyor
        // ama panelde GÖRÜNMÜYOR — listede yok, geri yüklenemiyor, saklama temizliğine de
        // takılmadığı için sonsuza kadar yer kaplıyor. FTP'nin bir gecelik arızası, o gecenin
        // yedeğini fiilen yok ediyordu.
        //
        // Artık yerel kopya HER KOŞULDA kaydedilir; uzak kopya başarısızsa bu, kaydın durumunda
        // ve uyarısında AÇIKÇA söylenir. Bir hedefin başarısızlığı diğerini götürmez.
        var remoteUri = string.Empty;
        string? remoteFailure = null;
        OperationCanceledException? remoteCancellation = null;
        if (target.Type != "local")
        {
            try
            {
                remoteUri = await UploadToRemoteAsync(finalPath, finalName, target, placement, cancellationToken);
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
            {
                remoteCancellation = exception;
                remoteFailure = DescribeRemoteFailure(exception);
            }
            catch (Exception exception)
            {
                remoteFailure = DescribeRemoteFailure(exception);
                logger?.LogWarning(
                    exception,
                    "Yedek uzak hedefe gönderilemedi ({Target}); yerel kopya korundu: {File}",
                    target.Type,
                    finalName);
            }
        }

        var record = new BackupRecordDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = finalName,
            Type = type,
            Source = source,
            Path = finalPath,
            // Biçim ANLAMSAL kalır ("zip"/"sql"/"bak"): şifreleme ve sıkıştırma birer SARMALAYICIDIR,
            // arşivin türünü değiştirmez. Geri yükleme yolları bu alana bakmaya devam eder
            // (sarmalayıcılar dosya BAŞLIĞINDAN tanınır, bu etiketten değil).
            Format = format,
            CreatedAtUtc = DateTime.UtcNow,
            Size = new FileInfo(finalPath).Length,
            Status = DescribeDestinationStatus(remoteUri, remoteFailure),
            TargetType = target.Type,
            RemoteUri = remoteUri,
            Encrypted = encrypted,
            Compressed = compressed,
            ChainId = chain?.ChainId ?? string.Empty,
            ChainIndex = chain?.ChainIndex ?? 0,
            BackupMode = chain?.Mode ?? BackupChainPlanner.ModeFull,
            BaseBackupId = chain?.BaseBackupId ?? string.Empty,
            SubscriptionUserId = subscriptionUserId,
            ItemCount = itemCount,
            IntegrityState = butunluk.State,

            // İÇERİK DAMGASI, `butunluk`tan FARKLI bir soruyu cevaplar: o "arşiv boş/bozuk mu",
            // bu "arşiv hâlâ BİZİM yazdığımız dosya mı". Kurcalanmış bir zip yapısal olarak
            // kusursuzdur, yani yukarıdaki ölçümden `sound` alır.
            //
            // SIRA ZORUNLU: damga `finalPath` üzerinden, yani sıkıştırma ve şifreleme UYGULANDIKTAN
            // sonra alınır. Ham dosyayı damgalamak, tam da kurcalanmaya açık olan katmanı ölçüm
            // dışında bırakırdı. Hesaplanamazsa boş kalır ve yedek yine geçerlidir (gerekçe
            // BackupContentHash.ComputeAsync'te yazılı).
            ContentHash = contentHash
        };

        // İNDEKS YAN KAYDI kayıttan ÖNCE yazılır: kayıt listede görünüp indeksi olmayan bir halka,
        // bir sonraki turda "temel okunamadı" diye zorunlu tam yedeğe düşürür (zararsız ama
        // gereksiz). Yazamazsak da yedek geçerlidir; yalnızca sonraki tur tam yedek olur.
        if (fileIndex is not null)
        {
            try
            {
                await fileStore.WriteAsync(IndexSidecarPath(record.Id), fileIndex, CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(exception, "Yedek dosya indeksi diske yazilamadi: {File}", finalName);
            }
        }

        // Sıkıştırma denendi ama başarısız olduysa (disk dolu vb.) SESSİZ KALINMAZ: yedek geçerlidir
        // ama beklenenden ~4 kat büyüktür ve sebebi ancak burada görünür.
        record.Warnings.AddRange(warnings);

        // Uzak kopya alınamadıysa SEBEBİ kayda taşınır. "Uzak hedefe gönderilemedi" demeden
        // sessizce yerel yedekle yetinmek, kullanıcının off-site kopyası olduğunu sanmasına yol
        // açar — felaket anında öğrenilecek en kötü şey budur.
        if (remoteFailure is not null)
        {
            record.Warnings.Add(new BackupRestoreWarningDto
            {
                MessageKey = "backup.remote_upload_failed",
                Target = target.Type,
                Detail = remoteFailure
            });
        }

        // Şifresiz yedek üçüncü taraf depoya çıktıysa bunu SÖYLERİZ. Sessiz kalmak, müşteri
        // verisinin S3/FTP'de düz metin durduğunu kimsenin fark etmemesi demekti (KVKK/GDPR).
        if (!encrypted && target.Type != "local")
        {
            record.Warnings.Add(new BackupRestoreWarningDto
            {
                MessageKey = "backup.remote_unencrypted",
                Target = target.Type,
                Detail = finalName
            });
        }

        // ARŞİVE GİREMEYEN DOSYALAR: "created" damgasını hak etmeyen tek durum budur. Kilitli
        // ya da erişilemeyen dosya atlanabilir (tek bir dosya yüzünden o gecenin yedeğini hiç
        // almamak daha kötü olurdu) ama ATLANDIĞI SÖYLENMEK ZORUNDA — hangi dosyalar olduğu da
        // dahil. Kullanıcı eksik bir arşivi "başarılı" sanıp ona güvenmemeli.
        ApplySkippedFiles(record, skipped, fileName);

        // BOŞ/AÇILAMAYAN ARŞİV "BAŞARILI" SAYILMAZ. Atlanan dosyalarla aynı kanal ve aynı
        // gerekçe: panelde yedeğin yanında duran tek özet Status'tür, "created" bırakmak bu
        // arşivi sağlamlardan AYIRT EDİLEMEZ yapardı.
        ApplyIntegrity(record, butunluk, fileName);

        ApplyContentWarnings(record, contentWarnings ?? []);
        await AppendRecordAsync(record, CancellationToken.None);
        // Preserve cancellation for the caller, after persisting both the archive and warnings.
        if (remoteCancellation is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(remoteCancellation).Throw();
        return record;
    }

    /// <summary>
    /// Arşive giremeyen dosyaları KAYDA taşır: her biri bir uyarı, durum
    /// <c>created_with_errors</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Durum neden değişiyor:</b> panelde yedeğin yanında duran tek özet Status'tür; uyarı
    /// listesi ancak kayıt açıldığında görülür. "created" bırakılsaydı eksik arşiv listede sağlam
    /// olanlardan AYIRT EDİLEMEZDİ. Mekanizma yeni değil — abonelik yedeği eksik öğe için bunu
    /// zaten yapıyordu; buradaki hata, dosya atlayan üç yardımcının o mekanizmaya HİÇ
    /// ulaşamamasıydı.
    /// </para>
    /// <para>
    /// <b>Neden <c>created_remote_failed</c> ezilmiyor:</b> off-site kopyanın olmadığını söylemek
    /// en az eksik dosya kadar önemlidir. O durum kalır, eksik dosyalar uyarı listesinde durur —
    /// ikisi de kaybolmaz.
    /// </para>
    /// </remarks>
    private void ApplySkippedFiles(BackupRecordDto record, BackupSkipLog? skipped, string fileName)
    {
        if (skipped is null || !skipped.Any)
        {
            return;
        }

        // Günlükçü: yardımcılar static olduğu için buraya kadar ulaşamıyordu; artık sunucu
        // günlüğünde de tek satırda görünür.
        logger?.LogWarning(
            "Yedek eksik alindi ({File}): {Ozet}",
            fileName,
            skipped.Describe());

        foreach (var atlanan in skipped.Detailed)
        {
            record.Warnings.Add(new BackupRestoreWarningDto
            {
                MessageKey = "backup.source_file_unreadable",
                Target = atlanan.EntryName,
                Detail = atlanan.Reason
            });
        }

        // TARAMASI YARIDA KESİLEN AĞAÇLAR. Atlanan dosyalardan daha ağır bir kayıptır: atlanan
        // dosyada "hangi dosya" bilinir, burada ise o ağacın gezilmemiş kısmında NE OLDUĞU hiç
        // ölçülmedi. Kullanıcı arşivin bu köşesine güvenmemeli.
        //
        // ANAHTAR NEDEN YÜRÜYÜŞÜN KENDİ SEBEP ANAHTARI ("files.size_truncated_*"), yeni bir
        // "backup.*" anahtarı değil: bu depoda çevirisi olmayan bir anahtar, kullanıcının ekranda
        // HAM ANAHTARI görmesi demektir (MessageKeyCoverageTests tam bunu kapatıyor). Yürüyüşün
        // sebep anahtarları hem tr hem en'de ZATEN çevrilidir ve olan biteni doğru anlatır ("öge
        // sayısı bütçesi doldu; sayım durduruldu").
        //
        // DETAIL NEDEN CÜMLE DEĞİL, ETİKET-DEĞER: `Detail` bu depoda ÇEVRİLMEYEN alandır — arayüz
        // onu çevirinin hemen altında OLDUĞU GİBİ basar (BackupView.vue: `{{ warning.detail }}`,
        // `t()`'den geçmez) ve alanın kendi belgesi de bunu söyler ("Teknik ayrıntı. Çevrilmez;
        // teşhis içindir."). Buraya Türkçe bir anlatı cümlesi yazıldığında İNGİLİZCE panelde
        // İngilizce anahtarın altında Türkçe cümle görünüyordu — yarı çevrilmiş bir ekran.
        // Çözüm bir anahtar EKLEMEK değil (i18n'de karşılığı olmayan anahtar daha kötüsünü, ham
        // anahtarı doğurur), alanı sözleşmesine geri çekmek: dilden bağımsız, makinece okunabilir
        // teknik satır. Anlatının taşıdığı iki olgu KAYBOLMUYOR — "tarama kesildi" ve "arşiv
        // eksik" etiket olarak duruyor; nerede kesildiği zaten Target'ta.
        foreach (var kesilen in skipped.TruncatedScans)
        {
            record.Warnings.Add(new BackupRestoreWarningDto
            {
                MessageKey = kesilen.ReasonKey,
                Target = kesilen.Root,
                Detail = $"scan=truncated; reason={kesilen.ReasonKey}; archive=incomplete; restore=partial"
            });
        }

        // HİÇ BULUNAMAYAN KÖKLER. Kesilen taramadan da ağır: orada ağacın bir kısmı gezilmişti,
        // burada ağacın KENDİSİ yok — o kaynağın hiçbir dosyası arşive girmedi.
        //
        // Anahtar YENİ ve gerekli: mevcut anahtarların hiçbiri bunu anlatmıyor
        // ("source_file_unreadable" tek bir dosyayı, "size_truncated_*" yarıda kesilmiş bir
        // yürüyüşü anlatır). Çevirisi tr/en'de VARDIR; olmasaydı kullanıcı ekranda ham anahtarı
        // görürdü (MessageKeyCoverageTests bunu kapatıyor).
        foreach (var eksikKok in skipped.MissingRoots)
        {
            record.Warnings.Add(new BackupRestoreWarningDto
            {
                MessageKey = "backup.source_root_missing",
                Target = eksikKok,
                Detail = "root=missing; scanned=0; archive=incomplete; restore=partial"
            });
        }

        // Ayrıntısı taşınmayanlar SAYIYLA taşınır: "kaç dosya eksik" bilgisi hiçbir koşulda
        // kaybolmamalı (bozuk bir ACL yüz binlerce dosyayı birden atlatabilir).
        //
        // Detail yine ETİKET-DEĞER (gerekçesi bir üstteki blokta): bu satırın taşıdığı şey zaten
        // saf sayıdır, Türkçe cümleye sarılması ona hiçbir şey katmıyor ama İngilizce panelde
        // yarı çevrilmiş bir satır üretiyordu.
        if (skipped.Truncated > 0)
        {
            record.Warnings.Add(new BackupRestoreWarningDto
            {
                MessageKey = "backup.source_files_unreadable_more",
                Target = skipped.Count.ToString(),
                Detail = $"skipped={skipped.Count}; listed={skipped.Detailed.Count}; unlisted={skipped.Truncated}"
            });
        }

        if (record.Status is "created" or "created_remote_synced")
        {
            record.Status = "created_with_errors";
        }
    }

    /// <summary>
    /// Arşiv ölçümünü KAYDA taşır: bozuksa bir uyarı, durum <c>created_with_errors</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden istisna atılmıyor:</b> dosya diskte duruyor ve içinde bir şey olabilir (ölçüm
    /// "girdisi yok" der, "hiçbir şey yok" demez). İstisna atıp kaydı hiç yazmamak, aynı hata
    /// sınıfının bu depoda bir kez P0 olarak kapatılmış hâlidir: gece işi sessizce hiçbir şey
    /// üretmez, kullanıcı bunu felaket anında öğrenir. Doğru cevap sessizlik değil DAMGADIR.
    /// </para>
    /// <para>
    /// <b>Neden <c>created_remote_failed</c> ezilmiyor:</b> off-site kopyanın olmadığını söylemek
    /// en az bozuk arşiv kadar önemlidir. İkisi de kalır — biri durumda, diğeri uyarı listesinde
    /// ve <see cref="BackupRecordDto.IntegrityState"/> alanında.
    /// </para>
    /// </remarks>
    private void ApplyIntegrity(BackupRecordDto record, BackupArchiveProbe.Result olcum, string fileName)
    {
        if (!olcum.Unsound)
        {
            return;
        }

        logger?.LogWarning("Yedek arsivi olculdu ve saglam degil ({File}): {Ayrinti}", fileName, olcum.Detail);

        record.Warnings.Add(new BackupRestoreWarningDto
        {
            MessageKey = "backup.archive_unsound",
            Target = fileName,
            // ETİKET-DEĞER, CÜMLE DEĞİL: Detail çevrilmeyen alandır (gerekçesi ApplySkippedFiles'ta).
            Detail = olcum.Detail
        });

        if (record.Status is "created" or "created_remote_synced")
        {
            record.Status = "created_with_errors";
        }
    }

    /// <summary>
    /// Yedeğin hangi hedeflere ULAŞTIĞINI anlatan durum.
    /// </summary>
    /// <remarks>
    /// Üç durum vardır ve üçü de GERÇEK bir sonucu anlatır:
    /// <list type="bullet">
    /// <item><c>created</c> — yalnız yerel disk istendi, alındı.</item>
    /// <item><c>created_remote_synced</c> — yerel disk + uzak hedef, İKİSİ de tamam.</item>
    /// <item><c>created_remote_failed</c> — yerel disk tamam, uzak hedef başarısız.
    /// Yedek VARDIR ama off-site kopyası YOKTUR; ikisini aynı kefeye koymak, kullanıcının
    /// olmayan bir off-site kopyaya güvenmesi demek olurdu.</item>
    /// </list>
    /// </remarks>
    /// <summary>
    /// Site dışı arşivlerin çıkarılacağı klasör adı: tür + kaynak + zaman damgası.
    /// </summary>
    /// <remarks>
    /// Kaynak alanı kullanıcı verisidir (site adı, abonelik kimliği) ve doğrudan yola girerse
    /// dizin kaçışına açık olur; bu yüzden temizlik <see cref="BackupPathName"/> üzerinden yapılır
    /// — yedek klasörü (<see cref="BackupLayout"/>) ile AYNI kural, tek kaynaktan. İkisi ayrı
    /// yazıldığı sürece biri düzeltilip diğeri unutulabilirdi. Zaman damgası, aynı yedekten ikinci
    /// kez seçerek geri yüklemenin öncekinin üzerine yazmasını engeller.
    /// </remarks>
    internal static string BuildRestoreFolderName(BackupRecordDto record)
    {
        var tur = BackupPathName.Sanitize(record.Type, "yedek");
        var kaynak = BackupPathName.Sanitize(record.Source, "yedek");
        return $"{tur}-{kaynak}-{DateTime.UtcNow:yyyyMMddHHmmss}";
    }

    /// <summary>
    /// Uzak yükleme hatasının KAYDA yazılacak hâli.
    /// </summary>
    /// <remarks>
    /// <see cref="AppException"/>'ın <c>Message</c>'ı yalnızca çeviri ANAHTARIDIR
    /// ("backup.remote_upload_failed"); sunucunun gerçekten ne dediği (<c>550 Permission denied</c>,
    /// hangi klasör, hangi adres) <c>Details</c> içinde durur. Kayda yalnız anahtarı yazmak,
    /// kullanıcıya "gönderilemedi" deyip SEBEBİ yutmaktı — hatayı görüp de nedenini asla
    /// öğrenememek, düzeltilemeyen bir arıza demektir.
    /// </remarks>
    internal static string DescribeRemoteFailure(Exception exception) =>
        exception is AppException app && !string.IsNullOrWhiteSpace(app.Details)
            ? $"{app.MessageKey}: {app.Details}"
            : exception.Message;

    internal static string DescribeDestinationStatus(string? remoteUri, string? remoteFailure)
    {
        if (!string.IsNullOrWhiteSpace(remoteFailure))
        {
            return "created_remote_failed";
        }

        return string.IsNullOrWhiteSpace(remoteUri) ? "created" : "created_remote_synced";
    }

    /// <summary>
    /// Yedeğin OKUNABİLİR hâli: sarmalayıcısı yoksa dosyanın kendisi; şifreli ve/veya sıkıştırılmış
    /// ise çözülmüş/açılmış GEÇİCİ kopya. Dispose edildiğinde geçici kopyaların HEPSİ silinir.
    /// </summary>
    private sealed class BackupContent(string filePath, IReadOnlyList<string> temporaryPaths) : IDisposable
    {
        public string FilePath { get; } = filePath;

        public void Dispose()
        {
            // Sarmalayıcı başına bir geçici dosya olabilir (çözülmüş kopya + açılmış kopya);
            // hepsi silinir, yoksa çözülmüş DÜZ METİN müşteri verisi %TEMP%'te kalırdı.
            foreach (var temporaryPath in temporaryPaths)
            {
                TryDeleteFile(temporaryPath);
            }
        }
    }

    /// <summary>
    /// Kaydın arşivini okumaya hazırlar: gerekiyorsa ÇÖZER, gerekiyorsa AÇAR.
    /// <para>
    /// Sarmalayıcıların varlığı DOSYANIN BAŞLIĞINDAN okunur, kayıttaki bayraklardan değil: eski
    /// (şifresiz ve sıkıştırmasız) yedekler çalışmaya devam etmeli, dosyası elle taşınmış/kaydı
    /// bozulmuş bir yedek de doğru tanınmalıdır. Etikete güvenip başlığa bakmamak, "yedek var
    /// görünüyor ama geri gelmiyor" sınıfının ta kendisidir.
    /// </para>
    /// <para>
    /// <b>Sıra, yazma sırasının TERSİDİR:</b> yazarken sıkıştır→şifrele, okurken çöz→aç. Bütün
    /// okuma yolları (geri yükleme, gözatma, ayrıntılı geri yükleme, zincir halkaları) tek elden
    /// buradan geçer; ayrı ayrı yazılsaydı biri unutulduğunda o yol sıkıştırılmış baytları ham SQL
    /// sanıp veritabanına aktarmaya kalkardı.
    /// </para>
    /// </summary>
    private async Task<BackupContent> OpenContentAsync(BackupRecordDto record, string? password, CancellationToken cancellationToken)
    {
        var currentPath = record.Path;
        var temporaryPaths = new List<string>();

        // DOSYA GERÇEKTEN DURUYOR MU? Listede görünüp diskte olmayan bir yedeği açmaya çalışmak
        // eskiden ham bir FileNotFoundException'a düşüyordu; kullanıcı felaket anında anlaşılmaz
        // bir yığın izi görüyordu. Sebep AÇIKÇA söylenir: dosya elle silinmiş/taşınmıştır.
        if (IsLocalFileMissing(record))
        {
            throw new AppException(
                "backup.file_missing",
                410,
                410,
                $"{record.Name} -> {record.Path}");
        }

        // İÇERİK DAMGASI BURADA DOĞRULANIR ÇÜNKÜ BURASI TEK BOĞAZDIR: geri yükleme, seçmeli geri
        // yükleme, posta geri yükleme ve zincir halkalarının hepsi bu fonksiyondan geçer. Doğrulamayı
        // çağrı yerlerine dağıtmak, bu depoda defalarca ölçülen hatayı üretirdi — biri düzeltilir,
        // kardeşi sessizce açık kalır.
        //
        // YALNIZ "EŞLEŞMEDİ" REDDEDİLİR. Damgası olmayan eski yedek ve okunamayan dosya
        // reddedilmez; gerekçeleri BackupContentHash.VerifyAsync'te dal dal yazılı. Felaket anında
        // "ölçemedim" yüzünden geri yüklemeyi engellemek, kapatılan riskten kötüdür.
        var damga = await BackupContentHash.VerifyAsync(record.ContentHash, currentPath, cancellationToken);
        if (damga == BackupIntegrityCheck.Mismatch)
        {
            // 409: istek de dosya da geçerli, ÇELİŞKİ var. Ayrıntıya iki damga da yazılır —
            // yönetici "kurcalanmış" iddiasını kendi doğrulayabilmeli, bize inanmak zorunda
            // kalmamalı.
            throw new AppException(
                "backup.integrity_mismatch",
                409,
                409,
                $"{record.Name}; expected={record.ContentHash}");
        }

        try
        {
            if (BackupArchiveCipher.IsEncryptedFile(currentPath))
            {
                var parola = await ResolveEncryptionPasswordAsync(password, cancellationToken);
                if (parola.Length == 0)
                {
                    // "Bozuk dosya" DEMEYİZ: dosya sağlam, parola eksik. Kullanıcı ne yapacağını bilmeli.
                    throw new AppException("backup.password_required", 400, 400);
                }

                var decryptedPath = Path.Combine(Path.GetTempPath(), $"adabk-open-{Guid.NewGuid():N}.tmp");
                temporaryPaths.Add(decryptedPath);
                await BackupArchiveCipher.DecryptFileAsync(currentPath, decryptedPath, parola, cancellationToken);
                currentPath = decryptedPath;
            }

            // SIKIŞTIRILMIŞ DÖKÜM GERİ YÜKLENİRKEN ÖNCE AÇILMALI: ImportDumpAsync gerçek bir .sql
            // DOSYASI bekler (mysql istemcisine "source <yol>" olarak verilir). Bu adım atlanırsa
            // özellik "yedek küçüldü" der ama geri yükleme çalışmaz — yedek fiilen yok olur.
            if (BackupDumpCompression.IsCompressedFile(currentPath))
            {
                var decompressedPath = Path.Combine(Path.GetTempPath(), $"adabk-open-{Guid.NewGuid():N}.{SafeTempExtension(record.Format)}");
                temporaryPaths.Add(decompressedPath);
                await BackupDumpCompression.DecompressFileAsync(currentPath, decompressedPath, cancellationToken);
                currentPath = decompressedPath;
            }

            return new BackupContent(currentPath, temporaryPaths);
        }
        catch
        {
            // Yarım kalan geçici kopyalar %TEMP%'te ÇÖZÜLMÜŞ müşteri verisi olarak kalmaz.
            foreach (var temporaryPath in temporaryPaths)
            {
                TryDeleteFile(temporaryPath);
            }

            throw;
        }
    }

    /// <summary>
    /// Açılmış geçici kopyanın uzantısı. Kayıttaki biçim etiketi kullanıcı verisi sayılır: yola
    /// giren her şey temizlenir, tanınmayan/boş değer <c>tmp</c>'ye düşer.
    /// </summary>
    private static string SafeTempExtension(string? format)
    {
        var temiz = new string((format ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
        return temiz.Length is > 0 and <= 8 ? temiz.ToLowerInvariant() : "tmp";
    }

    public async Task<BackupEncryptionSettingsDto> GetEncryptionSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (encryptionStore is null)
        {
            return new BackupEncryptionSettingsDto { Algorithm = EncryptionAlgorithmSummary };
        }

        var state = await encryptionStore.GetStateAsync(cancellationToken);
        var password = await encryptionStore.GetPasswordAsync(cancellationToken);
        return new BackupEncryptionSettingsDto
        {
            Enabled = state.Enabled,
            // ÖLÇÜLMÜŞ değer: kayıt var ama çözülemiyorsa (panel taşınmış, DataProtection anahtarları
            // gitmiş) "parola hazır" demek, zamanlanmış yedeklerin sessizce şifresiz alınmasıydı.
            PasswordUsable = password.Length > 0,
            UpdatedAtUtc = state.UpdatedAtUtc,
            Algorithm = EncryptionAlgorithmSummary
        };
    }

    public async Task<BackupEncryptionSettingsDto> SetEncryptionSettingsAsync(SetBackupEncryptionRequest request, CancellationToken cancellationToken = default)
    {
        if (encryptionStore is null)
        {
            throw new AppException("backup.encryption_unavailable", 503, 503);
        }

        await encryptionStore.SetPasswordAsync(request.Password, cancellationToken);
        return await GetEncryptionSettingsAsync(cancellationToken);
    }

    private static readonly string EncryptionAlgorithmSummary =
        $"AES-256-GCM / PBKDF2-SHA256 ({BackupArchiveCipher.Pbkdf2Iterations:N0} tur)";

    /// <summary>
    /// Tek bir sitenin tam yedeği: <c>files/</c> + <c>config/</c> + <c>databases/</c> + <c>mail/</c>
    /// tek bir zip içinde.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Arşiv siteye ait HER ŞEYİ bir arada tutar</b> — istekte seçilen veritabanlarının dökümü
    /// de aynı zip'in <c>databases/</c> klasörüne girer; ayrı bir dosya aramak gerekmez.
    /// </para>
    /// <para>
    /// <b>POSTA İKİ AYRI ŞEY OLARAK DURUR:</b>
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///   <c>mail/&lt;alan&gt;.json</c> — hesap/alias/yönlendirme ENVANTERİ. İleti İÇERMEZ.
    ///   </description></item>
    ///   <item><description>
    ///   <c>mail-messages/&lt;alan&gt;/&lt;adres&gt;.sqlite</c> — GERÇEK posta kutuları (iletiler,
    ///   klasörler, kimlikler). Hesap başına, Stalwart'ın resmî aracı <c>vandelay</c> ile, sunucu
    ///   ÇALIŞIRKEN alınır ve YALNIZ bu alan adına geri yüklenir.
    ///   </description></item>
    /// </list>
    /// <para>
    /// İletiler alınamazsa (vandelay kurulu değil, JMAP ucu ulaşılamaz, bir hesap düştü) arşiv
    /// "posta dahil" DEMEZ: <c>mail-messages</c> parçası eklenmez ve <c>manifest.json</c> hem
    /// uyarıyı hem NEDENİNİ taşır. Bu ayrım hayatidir — geri yüklenemeyecek bir arşive güvenip
    /// bunu ancak felaket anında öğrenmek, hiç yedek almamaktan kötüdür.
    /// </para>
    /// <para>
    /// Sunucu geneli posta yedeği (<see cref="BackupMailAsync"/>) KALDIRILMADI ve kaldırılmamalı:
    /// o, Stalwart deposunun tamamını (yapılandırma, kuyruk, bütün alan adları) kapsayan FELAKET
    /// KURTARMA yedeğidir ve servisi durdurur. Buradaki ise tek siteyi ayağa kaldırmak içindir.
    /// </para>
    /// </remarks>
    public async Task<BackupRecordDto> BackupSiteAsync(BackupSiteRequest request, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(request.SiteName, cancellationToken);
        var preferences = await preferencesService.GetAsync(cancellationToken);
        // YER kararı bir kez verilir; hem yerel klasör hem UZAK yol bundan türetilir.
        //
        // Kaynak seviyesi SİTE ADIDIR ve bilinçli olarak zincirin kaynak kimliğiyle (aşağıdaki
        // ResolveChainPlanAsync çağrısının ikinci parametresi) AYNI değerdir: böylece bir zincirin
        // bütün halkaları aynı gün içinde aynı klasörde toplanır, elle bakan biri de halkaları
        // dosya adı ayıklamadan görür.
        var placement = BackupLayout.Ensure(preferences.DefaultBackupRoot, "sites", site.Name);
        var backupsDirectory = placement.LocalDirectory;

        // ZİNCİR KARARI ARŞİVDEN ÖNCE VERİLİR: hangi dosyaların yazılacağı buna bağlı.
        var plan = await ResolveChainPlanAsync("site", site.Name, request, cancellationToken);
        var artimsal = plan.Stamp is { } damga && damga.Mode == BackupChainPlanner.ModeIncremental;

        // DOSYA ADI DA aynı temizlikten geçmiş değeri kullanır (placement.Source): klasörü
        // korurken dosya adını ham bırakmak kapıyı açık bırakmaktı — Path.Combine, ayırıcı içeren
        // bir dosya adını yol olarak kabul eder ve yedek hedef klasörün DIŞINA yazılırdı.
        var fileName = $"{placement.Source}-{DateTime.UtcNow:yyyyMMddHHmmss}{(artimsal ? "-inc" : string.Empty)}.zip";
        var targetPath = Path.Combine(backupsDirectory, fileName);

        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        var applicationRoot = string.IsNullOrWhiteSpace(site.ApplicationRootPath)
            ? site.PhysicalPath
            : site.ApplicationRootPath;

        // Dosya listesi bir kez taranır: hem indeksin hem de arşive girecek kümenin kaynağı budur.
        // ATLANANLAR tek bir defterde toplanır (tarama + arşivleme aşamaları ayrı ayrı atlayabilir).
        var atlananlar = new BackupSkipLog();
        var taranan = request.IncludeFiles
            ? ScanFiles(
                applicationRoot,
                "files",
                atlananlar,
                YeniTaramaButcesi(),
                cancellationToken)
            : [];
        var indeks = BuildIndex(plan.Stamp, taranan);
        var fark = BackupChainPlanner.Diff(plan.PreviousIndex, indeks);
        var yazilacak = fark.Changed.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // profesyonel panel benzeri tam-site yedeği: tek .zip içinde files/ + config/ + databases/ + mail/.
        var includedParts = new List<string>();
        // İÇERİK UYARILARI (arşive giremeyen MANTIKSAL öğeler; atlanan DOSYALARdan ayrı defter).
        // Abonelik yedeğinde zaten vardı, site yedeğinde YOKTU: dökümü alınamayan bir veritabanı
        // burada sessizce düşüyordu (bkz. aşağıdaki TryAddDatabaseDumpAsync dalı).
        var icerikUyarilari = new List<BackupRestoreWarningDto>();
        var mailMessages = MailMessageOutcome.NotRequested;
        await using (var zipStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var yazilan = AddFilesToArchive(archive, taranan, yazilacak, atlananlar);
            if (yazilan > 0)
            {
                includedParts.Add(artimsal ? $"files(+{yazilan})" : "files");
            }

            if (request.IncludeConfig)
            {
                var configJson = System.Text.Json.JsonSerializer.Serialize(site, BackupJsonOptions);
                AddTextEntry(archive, "config/site.json", configJson);
                includedParts.Add("config");
            }

            foreach (var db in request.Databases ?? [])
            {
                if (string.IsNullOrWhiteSpace(db.Engine) || string.IsNullOrWhiteSpace(db.DatabaseName))
                {
                    continue;
                }

                if (await TryAddDatabaseDumpAsync(archive, db.Engine, db.DatabaseName, "databases", cancellationToken))
                {
                    includedParts.Add($"db:{db.DatabaseName}");
                }
                else
                {
                    // ESKİDEN SESSİZDİ: dökümü boş çıkan veritabanı yalnızca manifest'teki
                    // "parts" listesinden DÜŞÜYORDU. Kullanıcı manifesti açıp okumaz; panelde
                    // gördüğü tek şey "created" damgasıydı. Abonelik yedeği bu uyarıyı zaten
                    // üretiyordu — site yedeğinde aynı gerçek kayboluyordu.
                    icerikUyarilari.Add(new BackupRestoreWarningDto
                    {
                        MessageKey = "backup.database_dump_empty",
                        Target = $"{db.Engine}/{db.DatabaseName}"
                    });
                }
            }

            if (request.IncludeMail)
            {
                var mailDomain = site.Name.ToLowerInvariant();

                try
                {
                    // (1) ENVANTER: hesap/alias/yönlendirme listesi. TEK BİR İLETİ İÇERMEZ.
                    // İletiler (2)'de ayrıca arşivlenir; envanter, iletiler alınamasa bile hesapların
                    // yeniden kurulabilmesi için değerlidir ve bu yüzden korunuyor.
                    var mailWorkspace = await mailService.GetWorkspaceAsync(mailDomain, cancellationToken);
                    var mailJson = System.Text.Json.JsonSerializer.Serialize(mailWorkspace, BackupJsonOptions);
                    AddTextEntry(archive, $"mail/{mailDomain}.json", mailJson);
                    includedParts.Add("mail-inventory");
                }
                catch
                {
                    // Mail kurulu değilse/alan adı yoksa sessizce atla — yedek yine de tamam.
                }

                // (2) GERÇEK POSTA (İLETİLER). Hesap başına vandelay arşivi; sunucu ÇALIŞIRKEN.
                //
                // KISMİ BAŞARI DÜRÜSTÇE TAŞINIR: tek bir hesap bile alınamazsa "mail-messages"
                // parçası EKLENMEZ ve manifest neyin eksik olduğunu söyler. Geri yüklenemeyecek bir
                // arşiv üretip "yedeklendi" demek, hiç yedeklememekten KÖTÜDÜR.
                mailMessages = await TryArchiveMailMessagesAsync(archive, mailDomain, cancellationToken);
                if (mailMessages.Included)
                {
                    includedParts.Add("mail-messages");
                }
            }

            WriteChainEntries(archive, plan.Stamp, indeks, fark);

            var manifest = System.Text.Json.JsonSerializer.Serialize(new
            {
                site = site.Name,
                createdAtUtc = DateTime.UtcNow,
                parts = includedParts,
                mode = plan.Stamp?.Mode ?? BackupChainPlanner.ModeFull,
                chainId = plan.Stamp?.ChainId ?? string.Empty,
                chainIndex = plan.Stamp?.ChainIndex ?? 0,
                note = artimsal
                    ? "Incremental link: files/ carries CHANGED files only; database dumps are always FULL."
                    : "Full backup.",
                // POSTANIN GERÇEĞİ TEK CÜMLEDE. Arşivi açan kişi "mail/" klasörünü görüp
                // postasının yedeklendiğini SANMAMALI; burada ne olduğu ve neyin OLMADIĞI açıkça
                // yazılır. Yanlış güven, felaket anında geri getirilemeyen posta demektir.
                mailNote = MailNote(includedParts, mailMessages),
                // İletiler istendi ama alınamadıysa NEDENİ de arşivde durur: "posta neden yok"
                // sorusu yedeğin kendisinden cevaplanabilmeli.
                mailMessagesError = mailMessages.Reason
            }, BackupJsonOptions);
            AddTextEntry(archive, "manifest.json", manifest);
        }

        return await FinalizeBackupAsync(
            targetPath,
            fileName,
            "site",
            site.Name,
            "zip",
            request,
            placement,
            atlananlar,
            cancellationToken,
            plan.Stamp,
            plan.Stamp is null ? null : indeks,
            contentWarnings: icerikUyarilari);
    }

    // =====================================================================================
    // SİTE YEDEĞİNDE POSTA (İLETİLER)
    // =====================================================================================

    /// <summary>Site arşivinde hesap arşivlerinin durduğu klasör.</summary>
    internal const string MailMessagesPrefix = "mail-messages/";

    /// <summary>Posta iletilerinin arşive girip girmediği ve GİRMEDİYSE nedeni.</summary>
    internal readonly record struct MailMessageOutcome(bool Included, string Reason)
    {
        internal static readonly MailMessageOutcome NotRequested = new(false, string.Empty);
    }

    /// <summary>
    /// Alan adının bütün posta kutularını (iletiler dahil) arşive koyar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>HEPSİ YA DA HİÇBİRİ.</b> Bir hesap bile alınamazsa arşive HİÇBİR hesap arşivi konmaz ve
    /// parça "dahil" işaretlenmez. Yarım bir posta arşivi en tehlikeli çıktıdır: kullanıcı
    /// postasının yedeklendiğini sanır, geri yüklerken eksiği ancak o an görür.
    /// </para>
    /// <para>
    /// Arşivleyici verilmemişse (dar kapsamlı testler, vandelay kurulu değil) bu bir HATA değildir;
    /// yedek yine tamamlanır ama "posta dahil" DEMEZ ve nedeni manifeste yazılır.
    /// </para>
    /// </remarks>
    private async Task<MailMessageOutcome> TryArchiveMailMessagesAsync(
        ZipArchive archive,
        string mailDomain,
        CancellationToken cancellationToken)
    {
        if (mailArchiver is null)
        {
            return new MailMessageOutcome(false,
                "Posta arşivleyici (vandelay) yapılandırılmamış; bu yedek posta İLETİLERİNİ içermiyor.");
        }

        // Hesap arşivleri önce geçici bir klasöre yazılır: vandelay dosyaya yazan bir araçtır,
        // doğrudan zip akışına yazamaz.
        var temp = Path.Combine(Path.GetTempPath(), "ada-mail-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sonuc = await mailArchiver.ArchiveDomainAsync(mailDomain, temp, cancellationToken);
            if (!sonuc.IsComplete)
            {
                var neden = sonuc.Failures.Count > 0
                    ? string.Join(" | ", sonuc.Failures)
                    : $"'{mailDomain}' için arşivlenecek posta hesabı bulunamadı.";
                logger?.LogWarning("Site yedeğine posta iletileri KONMADI ({Domain}): {Reason}", mailDomain, neden);
                return new MailMessageOutcome(false, neden);
            }

            foreach (var hesap in sonuc.Accounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                archive.CreateEntryFromFile(
                    hesap.FilePath,
                    $"{MailMessagesPrefix}{mailDomain}/{Path.GetFileName(hesap.FilePath)}",
                    CompressionLevel.Optimal);
            }

            return new MailMessageOutcome(true, string.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogWarning(exception, "Site yedeğine posta iletileri konamadı: {Domain}", mailDomain);
            return new MailMessageOutcome(false, "Posta arşivlenemedi: " + exception.Message);
        }
        finally
        {
            try { if (Directory.Exists(temp)) { Directory.Delete(temp, recursive: true); } } catch { /* en iyi çaba */ }
        }
    }

    /// <summary>
    /// Manifest'in posta cümlesi — TEK kaynak. Üç durum vardır ve üçü de farklı şey söyler:
    /// iletiler var, iletiler istendi ama alınamadı, posta hiç istenmedi.
    /// </summary>
    internal static string MailNote(IReadOnlyCollection<string> includedParts, MailMessageOutcome mailMessages)
    {
        if (mailMessages.Included)
        {
            return "mail-messages/ holds PER-ACCOUNT mailbox archives (messages included) and can be restored for THIS DOMAIN ALONE. mail/ additionally holds the account/alias inventory.";
        }

        if (!string.IsNullOrWhiteSpace(mailMessages.Reason))
        {
            return "WARNING: mailbox MESSAGES are NOT included in this archive (" + mailMessages.Reason +
                   "). mail/ holds the ACCOUNT AND ALIAS INVENTORY only. Mailbox contents are covered ONLY by the server-wide mail backup.";
        }

        return includedParts.Contains("mail-inventory")
            ? "mail/ holds the ACCOUNT AND ALIAS INVENTORY only. It contains NO messages/mailbox data. Mailbox contents live in the Stalwart store and are covered ONLY by the server-wide mail backup."
            : string.Empty;
    }

    // =====================================================================================
    // ARTIMSAL (INCREMENTAL) YEDEK ALTYAPISI
    //
    // Model Plesk'ten alınmıştır: ilk yedek her zaman TAM'dır; sonrakiler yalnızca son yedekten
    // beri DEĞİŞENİ taşır ve tam yedekle birlikte bir ZİNCİR oluşturur. Değişiklik kararı bir
    // İNDEKS üzerinden verilir (dosya arşivde var mıydı, boyutu/değişiklik tarihi aynı mı).
    //
    // ŞİFRELEME + ARTIMSAL: bir zincirin bütün halkaları aynı politikayla şifrelenir. Parola
    // kaybolursa TEK bir halka değil ZİNCİRİN TAMAMI (tam yedek dahil) geri getirilemez.
    // =====================================================================================

    /// <summary>Arşive girecek tek bir dosya: arşiv adı + disk yolu + indeks ölçüleri.</summary>
    private sealed record ArchiveFile(string EntryName, string FullPath, long Size, long ModifiedUtcTicks);

    /// <summary>Kayda basılacak zincir damgası.</summary>
    private sealed record ChainStamp(string ChainId, int ChainIndex, string Mode, string BaseBackupId);

    /// <summary>
    /// Bu yedeğin zincirdeki yeri + karşılaştırma temeli. <see cref="Stamp"/> <c>null</c> ise
    /// yedek zincire ait değildir (klasik, kendi başına yeterli tam yedek).
    /// </summary>
    private sealed record ChainPlan(ChainStamp? Stamp, BackupFileIndex? PreviousIndex);

    /// <summary>Arşiv içindeki indeks girdisi (zincirin kendi kendini anlatması için).</summary>
    private const string ArchiveIndexEntry = "index.json";

    /// <summary>Arşiv içindeki "bu halkada SİLİNMİŞ dosyalar" girdisi.</summary>
    private const string ArchiveDeletedEntry = "deleted.json";

    private static string IndexSidecarPath(string backupId) => $"backups/index/{backupId}.json";

    /// <summary>
    /// Yeni yedeğin TAM mı ARTIMSAL mı olacağını çözer ve artımsalsa karşılaştırma temelini yükler.
    /// </summary>
    private async Task<ChainPlan> ResolveChainPlanAsync(
        string type,
        string source,
        BackupArchiveRequest request,
        CancellationToken cancellationToken)
    {
        // Artımsal istenmiyorsa zincir de kurulmaz: zincirsiz yedek tek başına geri yüklenir ve
        // hiçbir budama kuralını kısıtlamaz. (Eski davranış birebir korunur.)
        if (!request.Incremental)
        {
            return new ChainPlan(null, null);
        }

        var kayitlar = await GetBackupsAsync(cancellationToken);
        var sonZincir = kayitlar
            .Where(record => string.Equals(record.Type, type, StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.Source, source, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(record.ChainId))
            .OrderByDescending(record => record.CreatedAtUtc)
            .Select(record => record.ChainId)
            .FirstOrDefault();

        BackupRecordDto? sonHalka = null;
        DateTime? tamYedekZamani = null;
        BackupFileIndex? oncekiIndeks = null;

        if (!string.IsNullOrWhiteSpace(sonZincir))
        {
            var zincir = BackupChainPlanner.GetChain(kayitlar, sonZincir);
            sonHalka = zincir.LastOrDefault();
            tamYedekZamani = zincir.FirstOrDefault(record => record.ChainIndex == 0)?.CreatedAtUtc;

            // Son halkanın DOSYASI yerinde değilse üstüne artımsal koymak, doğduğu anda kopuk bir
            // zincir üretmek olurdu. Ölçülmeyen şeye güvenilmez.
            if (sonHalka is not null && IsUsableBackup(sonHalka))
            {
                oncekiIndeks = await ReadFileIndexAsync(sonHalka, cancellationToken);
            }
        }

        var tamGerekli = BackupChainPlanner.TamYedekGerekli(
            sonHalka,
            tamYedekZamani,
            request.FullBackupEveryDays,
            oncekiIndeks is not null,
            DateTime.UtcNow);

        if (tamGerekli)
        {
            return new ChainPlan(
                new ChainStamp(Guid.NewGuid().ToString("N"), 0, BackupChainPlanner.ModeFull, string.Empty),
                null);
        }

        return new ChainPlan(
            new ChainStamp(sonHalka!.ChainId, sonHalka.ChainIndex + 1, BackupChainPlanner.ModeIncremental, sonHalka.Id),
            oncekiIndeks);
    }

    /// <summary>
    /// Bir halkanın dosya indeksini okur: önce diskteki yan kayıt, olmazsa (ŞİFRESİZ) arşivin
    /// içindeki <c>index.json</c>. İkisi de yoksa <c>null</c> → çağıran TAM yedeğe düşer.
    /// </summary>
    private async Task<BackupFileIndex?> ReadFileIndexAsync(BackupRecordDto record, CancellationToken cancellationToken)
    {
        try
        {
            var sidecar = await fileStore.ReadAsync<BackupFileIndex?>(IndexSidecarPath(record.Id), null, cancellationToken);
            if (sidecar is not null)
            {
                return sidecar;
            }
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            logger?.LogWarning(exception, "Yedek indeksi okunamadi: {Backup}", record.Name);
        }

        // Yan kayıt yoksa arşivden okumayı dene. ŞİFRELİ arşivde bu, bütün dosyayı geçici diske
        // çözmek demektir (50 GB'lık bir yedekte kabul edilemez) → şifreli arşivde denenmez ve
        // sonuç zorunlu tam yedektir. Bu, güvenlik ile maliyetin ölçülmüş dengesidir.
        if (record.Encrypted || BackupArchiveCipher.IsEncryptedFile(record.Path))
        {
            return null;
        }

        try
        {
            using var archive = ZipFile.OpenRead(record.Path);
            var entry = archive.GetEntry(ArchiveIndexEntry);
            if (entry is null)
            {
                return null;
            }

            // TAVAN: indeks de ARŞİVDEN gelir ve tamamen belleğe okunur (gerekçe BackupArchiveLimits).
            // Burada uyarı üretilmez — bu yol yedek ALIRKEN çalışır ve indeksi okuyamamak zaten
            // "artımsal yapamıyorum, TAM yedek al" demektir (aşağıdaki catch ile aynı sonuç).
            if (BackupArchiveLimits.ExceedsMetadataLimit(entry))
            {
                logger?.LogWarning(
                    "Yedek arsivindeki indeks tavani asiyor, artimsal zincir kurulmayacak: {Backup} ({Detail})",
                    record.Name,
                    BackupArchiveLimits.DescribeTooLarge(entry));
                return null;
            }

            await using var stream = entry.Open();
            return await System.Text.Json.JsonSerializer.DeserializeAsync<BackupFileIndex>(
                stream,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            logger?.LogWarning(exception, "Yedek arsivinden indeks okunamadi: {Backup}", record.Name);
            return null;
        }
    }

    /// <summary>
    /// Bir dizini tarar ve indeks ölçülerini (boyut + değişiklik tarihi) toplar.
    /// Okunamayan dosya ATLANIR — ama <paramref name="skipped"/>'a YAZILARAK: erişilemeyen tek bir
    /// dosya bütün yedeği düşürmemeli, ama sessizce kaybolmamalı da.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Burada atlanan dosya arşive HİÇ ADAY OLMAZ (indekse girmez), yani
    /// <c>AddFilesToArchive</c> onu bir daha göremez. Bu yüzden atlamanın kaydı tam burada
    /// tutulmalı; bir adım sonrasında ölçülemez.
    /// </para>
    /// <para>
    /// <b>YÜRÜYÜŞ ARTIK BURADA YAZILI DEĞİL.</b> Burada elle yazılmış bir <c>Stack</c> yürüyüşü
    /// vardı; yığın kısmı doğruydu ama İKİ koruma eksikti ve ikisi de kiracının YAZABİLDİĞİ bir
    /// ağaçta yürüdüğü için sömürülebilirdi:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///   <b>Bağlantı (junction/symlink) TAKİP EDİLİYORDU.</b> Kiracının site köküne kendi kökünü
    ///   gösteren bir kavşak koyması, gece çalışan yedek işini SONSUZ döngüye sokmaya yeterdi
    ///   (ya da yol uzunluğu sınırına kadar aynı ağacı defalarca arşive yazıp diski doldurmaya).
    ///   Komşuyu gösteren bir kavşak ise BAŞKA BİR KİRACININ dosyalarını bu arşive sokardı.
    ///   </description></item>
    ///   <item><description>
    ///   <b>BÜTÇE YOKTU.</b> Tarama, ağaç ne kadar büyükse o kadar sürerdi; üst sınırı yoktu.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Aynı hata sınıfı kota yolunda ölçülmüş ve orada çözüm <see cref="SafeDirectoryWalk"/> ile
    /// TEK yürüyüşe inmek olmuştu. Burada üçüncü bir güvenli yürüyüş yazmak, "birini sıkılaştırıp
    /// diğerini unutma" hatasını üçüncü kez kurmak demekti.
    /// </para>
    /// <para>
    /// <b>Kavşak atlanması ARŞİVDE EKSİK SAYILMAZ ve uyarı üretmez.</b> Kavşak bir VERİ değil,
    /// bir işaretçidir: hedefi ya bu ağacın içindedir (o zaman gerçek adıyla zaten arşivdedir ve
    /// takip etmek aynı baytları ikinci kez yazmak olurdu) ya da dışındadır — ki o veri bu
    /// kiracının yedeğine GİRMEMELİDİR. Her iki durumda da "eksik" damgası yanlış olurdu.
    /// </para>
    /// </remarks>
    /// <param name="butce">Taramanın üst sınırı. Abonelik yedeğinde <b>aynı nesne bütün sitelerde
    /// paylaşılır</b>: site başına verilseydi tavan site sayısıyla çarpılır, yani fiilen kalkardı.</param>
    private static List<ArchiveFile> ScanFiles(
        string root,
        string entryPrefix,
        BackupSkipLog skipped,
        SafeDirectoryWalkBudget butce,
        CancellationToken cancellationToken)
    {
        var sonuc = new List<ArchiveFile>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            // KÖK YOKSA SESSİZ KALINMAZ.
            //
            // ÖLÇÜLEN HATA: burada yalnızca boş liste dönülüyordu. Sitesinin fiziksel yolu
            // silinmiş/yeniden adlandırılmış bir kullanıcı, içinde TEK BİR DOSYASI OLMAYAN bir
            // arşivi "created" damgasıyla alıyordu — her gece, aylarca, hiçbir uyarı olmadan.
            // Yedek çağrısı zaten "bu ağacı yedekle" diyor; ağacın orada olmaması bir SONUÇ
            // değil, kullanıcının bilmesi gereken bir BULGUDUR.
            //
            // İstisna atılmaz: abonelik yedeğinde tek bir sitenin kökü kaybolduğunda bütün
            // aboneliğin yedeğini iptal etmek, sağlam siteleri de yedeksiz bırakırdı. Kanal
            // atlanan dosyalarla aynı (BackupSkipLog → uyarı + created_with_errors).
            // Ad ARŞİV GİRDİSİ adıyla verilir (kesilen taramalarla aynı sözleşme): kullanıcıya
            // sunucu yolunu değil, yedeğin içinde arayacağı yeri söyler.
            skipped.AddMissingRoot(string.IsNullOrWhiteSpace(entryPrefix) ? root : entryPrefix);
            return sonuc;
        }

        var tam = Path.GetFullPath(root);

        var yuruyus = SafeDirectoryWalk.Yuru(
            tam,
            butce,
            oge =>
            {
                if (oge is not FileInfo bilgi)
                {
                    return;
                }

                var girdi = GirdiAdi(tam, entryPrefix, bilgi.FullName);
                try
                {
                    // Uzunluk ve tarih sıralamanın KENDİ meta verisinden gelir; yine de dosya bu
                    // arada silinmiş olabilir. Silinmiş dosya atlanan değildir (arşivlenecek bir
                    // şey yok), bu yüzden deftere yazılmaz.
                    if (!bilgi.Exists)
                    {
                        return;
                    }

                    sonuc.Add(new ArchiveFile(girdi, bilgi.FullName, bilgi.Length, bilgi.LastWriteTimeUtc.Ticks));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    skipped.Add(girdi, exception);
                }
            },
            // Okunamayan klasör: BÜTÜN alt ağacı yedeğin dışında kaldı. Eskiden bu kökte olduğunda
            // sessizce BOŞ liste dönülüyordu — yani sitenin hiçbir dosyası olmayan bir arşiv
            // "başarılı" diye kaydediliyordu. Adı deftere yazılır ki kullanıcı NEREYE bakacağını
            // bilsin.
            (klasor, exception) => skipped.Add(GirdiAdi(tam, entryPrefix, klasor), exception),
            cancellationToken);

        if (yuruyus.Truncated && yuruyus.TruncatedReasonKey is not SafeDirectoryWalk.SebepErisimReddi)
        {
            // BÜTÇE DOLDU = ağacın GEZİLMEMİŞ bir parçası var, yani bu arşiv EKSİK.
            //
            // KARAR: yedek yine ALINIR ama "created_with_errors" damgası yer ve kesilen kök
            // uyarıda ADIYLA görünür. Ölçülmüş gerekçe:
            //  * İSTİSNA ATIP HİÇ ALMAMAK yanlış olurdu — gece çalışan iş sessizce hiç yedek
            //    üretmez, kullanıcı bunu ancak felaket anında öğrenirdi. Bu depoda tam olarak bu
            //    hata sınıfı ("yedek eksik ama kayıt 'created' diyor") bir kez P0 olarak kapatıldı;
            //    çözümü sessizlik değil, DAMGA olmuştu.
            //  * SESSİZCE KESMEK de yasak: eksik bir arşivi tam sanmak, geri yükleme gününde veri
            //    kaybıdır. O yüzden erişim reddiyle AYNI kanaldan (BackupSkipLog → uyarı + durum)
            //    yükseltilir; kullanıcı listede sağlam yedeklerden AYIRT EDEBİLİR.
            // Erişim reddi burada yeniden bildirilmez: onu zaten yukarıdaki geri çağrı, kesilen
            // klasörün ADIYLA deftere yazdı — aynı olayı ikinci kez, daha az bilgiyle söylemek olurdu.
            skipped.AddTruncatedScan(GirdiAdi(tam, entryPrefix, tam), yuruyus.TruncatedReasonKey);
        }

        return sonuc;
    }

    private static BackupFileIndex BuildIndex(ChainStamp? stamp, IReadOnlyCollection<ArchiveFile> files) => new()
    {
        ChainId = stamp?.ChainId ?? string.Empty,
        ChainIndex = stamp?.ChainIndex ?? 0,
        CreatedAtUtc = DateTime.UtcNow,
        Entries = files
            .Select(file => new BackupFileIndexEntry
            {
                Path = file.EntryName,
                Size = file.Size,
                ModifiedUtcTicks = file.ModifiedUtcTicks
            })
            .ToList()
    };

    /// <summary>
    /// Seçili dosyaları arşive ekler ve GERÇEKTEN eklenen sayısını döner. Eklenemeyen her dosya
    /// <paramref name="skipped"/>'a yazılır — "yedeklendi" denip arşivde olmayan bir müşteri
    /// dosyası, geri yükleme gününe saklanan bir sürprizdir.
    /// </summary>
    private static int AddFilesToArchive(
        ZipArchive archive,
        IReadOnlyCollection<ArchiveFile> files,
        HashSet<string> selected,
        BackupSkipLog skipped)
    {
        var eklenen = 0;
        foreach (var file in files)
        {
            if (!selected.Contains(file.EntryName))
            {
                continue;
            }

            try
            {
                archive.CreateEntryFromFile(file.FullPath, file.EntryName, CompressionLevel.Optimal);
                eklenen++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                skipped.Add(file.EntryName, exception);
            }
        }

        return eklenen;
    }

    /// <summary>
    /// Zincir girdilerini arşive yazar: TAM indeks + bu halkada silinen dosyalar.
    /// </summary>
    /// <remarks>
    /// İndeks artımsal halkada da TAM tutulur; aksi hâlde bir sonraki halka "neyin silindiğini"
    /// hesaplayamazdı. Silinenler taşınmazsa geri yükleme, kullanıcının bilerek kaldırdığı
    /// dosyaları (ör. temizlenmiş bir zararlı eklentiyi) geri getirirdi.
    /// </remarks>
    private static void WriteChainEntries(
        ZipArchive archive,
        ChainStamp? stamp,
        BackupFileIndex index,
        BackupChainPlanner.IndexDiff diff)
    {
        if (stamp is null)
        {
            return;
        }

        AddTextEntry(archive, ArchiveIndexEntry, System.Text.Json.JsonSerializer.Serialize(index, BackupJsonOptions));
        if (diff.Deleted.Count > 0)
        {
            AddTextEntry(archive, ArchiveDeletedEntry, System.Text.Json.JsonSerializer.Serialize(diff.Deleted, BackupJsonOptions));
        }
    }

    /// <summary>
    /// Bir veritabanının dökümünü arşive ekler. Dökümü alınamayan veritabanı için <c>false</c> döner
    /// (çağıran bunu uyarıya çevirir); yedeğin geri kalanı durmaz.
    /// </summary>
    private async Task<bool> TryAddDatabaseDumpAsync(
        ZipArchive archive,
        string engine,
        string databaseName,
        string entryPrefix,
        CancellationToken cancellationToken)
    {
        var ext = engine.Equals("mssql", StringComparison.OrdinalIgnoreCase) ||
                  engine.Equals("sqlserver", StringComparison.OrdinalIgnoreCase) ? "bak" : "sql";
        var tempDump = Path.Combine(Path.GetTempPath(), $"adabk-{Guid.NewGuid():N}.{ext}");
        try
        {
            await databaseService.ExportDumpAsync(new AdaPanel.Core.Dtos.Databases.DatabaseDumpRequest
            {
                Engine = engine,
                DatabaseName = databaseName,
                FilePath = tempDump
            }, cancellationToken);

            // DOSYANIN VARLIĞI DEĞİL, İÇİ SORULUR.
            //
            // ÖLÇÜLEN HATA: burada yalnız File.Exists vardı. Dışa aktarıcı dosyayı YARATIP boş
            // bırakabilir (yetkisiz mysqldump, kapalı servis, dolmuş disk); o durumda arşive
            // sıfır baytlık bir ".sql" giriyor ve panel "db:shop yedeklendi" diyordu. Bunun
            // yalan olduğu ancak geri yüklerken — boş bir veritabanıyla — anlaşılırdı.
            //
            // "backup.database_dump_empty" uyarısı zaten yazılmıştı ama bu ölçüm olmadığı için
            // HİÇ TETİKLENEMİYORDU: kural vardı, uygulaması yoktu.
            var bilgi = new FileInfo(tempDump);
            if (!bilgi.Exists || bilgi.Length == 0)
            {
                return false;
            }

            archive.CreateEntryFromFile(tempDump, $"{entryPrefix}/{engine}-{databaseName}.{ext}", CompressionLevel.Optimal);
            return true;
        }
        finally
        {
            if (File.Exists(tempDump))
            {
                File.Delete(tempDump);
            }
        }
    }

    public async Task<BackupRecordDto> BackupDatabaseAsync(BackupDatabaseRequest request, CancellationToken cancellationToken = default)
    {
        if (request.UseConfiguredFtpTarget)
        {
            // Resolve once, before exporting. A changed destination must require a fresh confirmation.
            var (type, url, credentials) = await RemoteCredentials.ReadUploadTargetAsync(cancellationToken);
            if (type != "ftp" || string.IsNullOrWhiteSpace(url)
                || !string.Equals(request.RemoteUrl, url, StringComparison.Ordinal))
                throw new AppException("backup.retry_target_changed", 409, 409);
            var configuredTarget = ResolveTarget(type, url, credentials.UserName, credentials.Password, credentials.Region);
            if (configuredTarget.Uri is null || IsPinnedArchiveTarget(Uri.UnescapeDataString(configuredTarget.Uri.AbsolutePath).TrimEnd('/').Split('/').Last()))
                throw new AppException("backup.remote_target_invalid", 400, 400);
            await EnsureRemoteTargetAllowedAsync(configuredTarget, cancellationToken);
            // Do not copy secrets into the incoming request or take credentials from the browser.
            request = new BackupDatabaseRequest
            {
                Engine = request.Engine, DatabaseName = request.DatabaseName,
                EncryptionPassword = request.EncryptionPassword,
                TargetType = type, RemoteUrl = url, RemoteUserName = credentials.UserName,
                RemotePassword = credentials.Password, RemoteRegion = credentials.Region
            };
        }
        var preferences = await preferencesService.GetAsync(cancellationToken);
        var safeDatabaseName = string.IsNullOrWhiteSpace(request.DatabaseName) ? "all" : request.DatabaseName.Trim();

        // KAYNAK KLASÖRÜ dosya adının önekiyle AYNI ("mysql-shop_db"): diskten bakan biri klasör
        // adıyla dosya adı arasında zihinsel çeviri yapmak zorunda kalmasın.
        var kaynakKlasoru = $"{request.Engine}-{safeDatabaseName}";

        // VERİTABANI BİR SİTEYE AİTSE onun klasörüne girer: kullanıcının isteği "bir sitenin her
        // şeyi bir arada dursun". Sahiplik ÖLÇÜLEREK okunur (aşağıya bakınız); okunamıyorsa yedek
        // kategorinin kendi klasörüne düşer — yanlış siteye koymaktansa gruplamamak yeğdir.
        var sahipSite = await ResolveDatabaseSiteAsync(request.Engine, request.DatabaseName, cancellationToken);
        var placement = sahipSite.Length == 0
            ? BackupLayout.Ensure(preferences.DefaultBackupRoot, "databases", kaynakKlasoru)
            : BackupLayout.Ensure(preferences.DefaultBackupRoot, "sites", sahipSite, "databases", kaynakKlasoru);
        var backupsDirectory = placement.LocalDirectory;
        var extension = request.Engine.Equals("mssql", StringComparison.OrdinalIgnoreCase) ||
                        request.Engine.Equals("sqlserver", StringComparison.OrdinalIgnoreCase)
            ? "bak"
            : "sql";
        // Klasörle AYNI (temizlenmiş) önek: veritabanı adı da kullanıcı verisidir, ham hâliyle
        // Path.Combine'a verilmesi yedeği hedef klasörün dışına yazabilirdi.
        var fileName = $"{placement.Source}-{DateTime.UtcNow:yyyyMMddHHmmss}.{extension}";
        var targetPath = Path.Combine(backupsDirectory, fileName);
        var dump = await databaseService.ExportDumpAsync(new AdaPanel.Core.Dtos.Databases.DatabaseDumpRequest
        {
            Engine = request.Engine,
            DatabaseName = request.DatabaseName,
            FilePath = targetPath
        }, cancellationToken);

        return await FinalizeBackupAsync(
            targetPath,
            fileName,
            "database",
            $"{dump.Engine}:{dump.DatabaseName}",
            extension,
            request,
            placement,
            // Dosya TARAMASI yok: döküm tek dosyadır ve alınamazsa ExportDumpAsync zaten patlar.
            skipped: null,
            cancellationToken);
    }

    /// <summary>
    /// Veritabanının bağlı olduğu site (yoksa boş metin). Yalnızca YEDEĞİN KLASÖRÜNÜ seçmek için
    /// kullanılır; erişim/kapsam kararı DEĞİLDİR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden sahiplik kaydı, ad benzerliği değil:</b> "shop_db şu siteye aittir" tahmini bu
    /// depoda daha önce gerçek bir veri kaybına yol açtı. Bağ, panelin kendi OTORİTE kaydından
    /// (<see cref="IDatabaseOwnershipService"/>, <c>config/db-ownership.json</c>) okunur; kayıt
    /// yoksa bağ da YOKTUR.
    /// </para>
    /// <para>
    /// <b>Neden hata yutulmuyor ama yedek de durdurulmuyor:</b> sahiplik okunamazsa bilinen tek
    /// gerçek "hangi siteye ait olduğunu bilmiyoruz"tur. Bu yüzden yedek eski düzendeki
    /// <c>databases/</c> klasörüne alınır (yedek KAYBOLMAZ) ve sebep günlüğe yazılır — sessizce
    /// bir siteye koymak, o siteyi geri yükleyen kişiye ait olmayan bir veritabanını sunardı.
    /// </para>
    /// <para>
    /// <b>Boş veritabanı adı</b> (motorun tamamının dökümü) hiçbir siteye ait olamaz; sahiplik hiç
    /// sorgulanmaz.
    /// </para>
    /// </remarks>
    private async Task<string> ResolveDatabaseSiteAsync(string engine, string? databaseName, CancellationToken cancellationToken)
    {
        if (databaseOwnership is null || string.IsNullOrWhiteSpace(databaseName))
        {
            return string.Empty;
        }

        try
        {
            var kayitlar = await databaseOwnership.GetAllAsync(cancellationToken);
            var ad = databaseName.Trim();
            foreach (var kayit in kayitlar)
            {
                if (string.Equals(kayit.Engine, engine?.Trim(), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(kayit.DatabaseName, ad, StringComparison.OrdinalIgnoreCase))
                {
                    return (kayit.SiteName ?? string.Empty).Trim();
                }
            }

            return string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogWarning(
                exception,
                "Veritabani sahipligi okunamadi; yedek site klasorune degil 'databases' klasorune alinacak: {Engine}/{Database}",
                engine,
                databaseName);
            return string.Empty;
        }
    }

    public async Task<BackupRecordDto> BackupPanelAsync(BackupPanelRequest request, CancellationToken cancellationToken = default)
    {
        var preferences = await preferencesService.GetAsync(cancellationToken);
        // KAYNAK KLASÖRÜ YOK, bilerek: panel yedeğinin kaynağı TEKTİR (panelin kendisi).
        // "panel\panel\" gibi bir seviye hiçbir şeyi ayırmaz, yalnızca bir tık daha derine gömer.
        var placement = BackupLayout.Ensure(preferences.DefaultBackupRoot, "panel");
        var backupsDirectory = placement.LocalDirectory;
        var fileName = $"panel-config-{DateTime.UtcNow:yyyyMMddHHmmss}.zip";
        var targetPath = Path.Combine(backupsDirectory, fileName);
        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        // Panelin KENDİ durumu: adapanel.db (kullanıcı/2FA) + config/*.json (planlar, scope'lar, otomasyon...).
        // DataProtection ANAHTARLARI (keys/) ve sertifikalar BİLEREK DIŞARIDA — makineye bağlı/hassas, taşınamaz.
        var configDirectory = fileStore.ResolvePath("config");
        var dbPath = fileStore.ResolvePath("data/adapanel.db");
        var includedParts = new List<string>();
        var atlananlar = new BackupSkipLog();

        await using (var zipStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            // adapanel.db (+ WAL/SHM) — çalışan panel kilitleyebilir → paylaşımlı okuma ile ekle.
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = dbPath + suffix;
                if (File.Exists(file))
                {
                    // "parts" listesi arşivin İÇİNDEKİ manifesttir; GERÇEKTEN eklenmeyeni oraya
                    // yazmak, arşivi açan kişiye de yalan söylemek olurdu.
                    if (AddSharedFileToArchive(archive, file, $"data/{Path.GetFileName(file)}", atlananlar))
                    {
                        includedParts.Add(Path.GetFileName(file));
                    }
                }
            }

            // config/ tüm JSON durum dosyaları (backups.json dahil — zararsız).
            if (Directory.Exists(configDirectory))
            {
                AddDirectoryToArchive(archive, configDirectory, "config", atlananlar);
                includedParts.Add("config");
            }

            // Planlı görevler (\AdaPanel\ klasörü): Windows Görev Zamanlayıcı'da yaşarlar, panel dosyalarında DEĞİL —
            // ham görev XML'i (principal/tetikleyici/eylem birebir) yedeğe girer; panel geri yüklemesinde otomatik kaydedilir.
            var exportedTasks = await TryExportScheduledTasksAsync(cancellationToken);
            if (exportedTasks.Count > 0)
            {
                var index = new List<object>();
                for (var i = 0; i < exportedTasks.Count; i++)
                {
                    var entryName = $"tasks/{i:000}.xml";
                    AddTextEntry(archive, entryName, exportedTasks[i].Xml);
                    index.Add(new { name = exportedTasks[i].Name, entry = entryName });
                }

                AddTextEntry(archive, "tasks/index.json", System.Text.Json.JsonSerializer.Serialize(index, BackupJsonOptions));
                includedParts.Add($"tasks({exportedTasks.Count})");
            }

            // Tam-sunucu DR: IIS site envanteri (ad + fiziksel yol + havuz + bağlamalar). Geri yüklemede
            // eksik siteler bu envanterden yeniden kurulur; içerik site yedeklerinden döner. Best-effort.
            try
            {
                var sites = await iisSiteService.GetSitesAsync(cancellationToken);
                if (sites.Count > 0)
                {
                    AddTextEntry(archive, "iis/sites.json", System.Text.Json.JsonSerializer.Serialize(sites, BackupJsonOptions));
                    includedParts.Add($"iis-sites({sites.Count})");
                }
            }
            catch
            {
                // IIS okunamıyorsa (rol yok vb.) panel yedeği yine de oluşur.
            }

            var manifest = System.Text.Json.JsonSerializer.Serialize(new
            {
                kind = "panel-config",
                createdAtUtc = DateTime.UtcNow,
                parts = includedParts,
                note = "DataProtection keys and certificates are intentionally excluded (machine-bound)."
            }, BackupJsonOptions);
            AddTextEntry(archive, "manifest.json", manifest);
        }

        return await FinalizeBackupAsync(targetPath, fileName, "panel", "panel", "zip", request, placement, atlananlar, cancellationToken);
    }

    // =====================================================================================
    // ABONELİK YEDEĞİ (Plesk paritesi)
    //
    // Plesk site başına değil ABONELİK başına yedek alır: bir müşterinin bütün siteleri +
    // veritabanları + posta alan adları + FTP hesapları TEK arşivde. Bir müşteriyi başka
    // sunucuya taşımak ya da tek hamlede geri getirmek ancak böyle mümkündür.
    //
    // KİRACI İZOLASYONU buradaki tek gerçek risktir ve kararı SubscriptionBackupPlanner verir:
    // arşive giren her kaynak, aboneliğin kapsamından ÖLÇÜLEREK seçilir; ad benzerliğine
    // güvenilmez. Arşivi indiren müşterinin eline başkasının verisi geçemez.
    // =====================================================================================

    /// <summary>Abonelik arşivindeki FTP hesabı kaydı (parola YEDEĞE GİREMEZ, dışa aktarılamaz).</summary>
    private sealed record SubscriptionFtpEntry(string UserName, string SiteName, string HomePath);

    public async Task<BackupRecordDto> BackupSubscriptionAsync(BackupSubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        if (customerScopes is null || databaseOwnership is null)
        {
            // Kapsam/sahiplik okunamıyorsa hangi verinin bu aboneliğe ait olduğunu ÖLÇEMEYİZ.
            // Ölçemediğimiz hâlde arşiv üretmek, başkasının verisini içeren bir yedek üretme
            // riskidir; açıkça reddetmek tek doğru cevaptır.
            //
            // Bu bir KULLANICI HATASI DEĞİL, kapsayıcı yapılandırma hatasıdır: üretimde DI bu iki
            // servisi her zaman verir, yalnızca dar kapsamlı testlerde boş kalabilir. Bu yüzden
            // kullanıcıya "şunu düzelt" diyen bir mesaj anahtarı üretilmez; ortak beklenmeyen-hata
            // yoluna (common.unexpected_error) düşer.
            throw new InvalidOperationException(
                "BackupService abonelik yedegi icin ICustomerScopeService ve IDatabaseOwnershipService gerektirir.");
        }

        var scope = await customerScopes.GetScopeAsync(request.UserId, cancellationToken);
        var mailDomains = request.IncludeMail
            ? await customerScopes.GetAllowedMailDomainsAsync(request.UserId, cancellationToken)
            : [];

        var ownership = request.IncludeDatabases
            ? await databaseOwnership.GetAllAsync(cancellationToken)
            : [];

        IReadOnlyCollection<(string UserName, string SiteName, string HomePath)> ftpUsers = [];
        if (request.IncludeFtp)
        {
            try
            {
                ftpUsers = (await ftpService.GetFtpUsersAsync(cancellationToken))
                    .Select(user => (user.UserName, user.SiteName, user.HomePath))
                    .ToArray();
            }
            catch (Exception exception)
            {
                // FTP rolü kurulu değilse abonelik yedeği yine alınır.
                logger?.LogWarning(exception, "Abonelik yedegi icin FTP hesaplari okunamadi.");
            }
        }

        var plan = SubscriptionBackupPlanner.BuildPlan(request.UserId, scope.Sites, mailDomains, ownership, ftpUsers);
        var kaynak = request.UserId.ToString("N");

        var preferences = await preferencesService.GetAsync(cancellationToken);
        // KAYNAK KLASÖRÜ: aboneliğin TAM kimliği. Dosya adındaki kısaltılmış (8 haneli) önek
        // BİLEREK kullanılmadı — klasör bir zincirin halkalarını topluyor; iki aboneliğin kimliği
        // ilk 8 hanede çakışırsa halkaları aynı klasörde karışırdı.
        var placement = BackupLayout.Ensure(preferences.DefaultBackupRoot, "subscriptions", kaynak);
        var backupsDirectory = placement.LocalDirectory;
        var chainPlan = await ResolveChainPlanAsync("subscription", kaynak, request, cancellationToken);
        var artimsal = chainPlan.Stamp is { } damga && damga.Mode == BackupChainPlanner.ModeIncremental;

        // Dosya adında kullanıcı kimliğinin ilk 8 hanesi: aynı gün içinde alınan farklı
        // aboneliklerin arşivleri diskte birbirinden ayırt edilebilir olmalı.
        var fileName = $"abonelik-{kaynak[..8]}-{DateTime.UtcNow:yyyyMMddHHmmss}{(artimsal ? "-inc" : string.Empty)}.zip";
        var targetPath = Path.Combine(backupsDirectory, fileName);
        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        // Bütün sitelerin dosyaları TEK bir indekste toplanır: zincir kararı abonelik bütünü
        // üzerinden verilir, site site değil. Aksi hâlde bir sitenin tam yedeği başka bir siteyi
        // artımsal bırakır ve zincir anlamını kaybederdi.
        var taranan = new List<ArchiveFile>();
        var siteBilgileri = new Dictionary<string, AdaPanel.Core.Dtos.Sites.SiteDto>(StringComparer.OrdinalIgnoreCase);
        var uyarilar = new List<BackupRestoreWarningDto>();
        var atlananlar = new BackupSkipLog();

        // TEK BÜTÇE, BÜTÜN SİTELER: aynı karar kota yolunda da verildi ve gerekçesi
        // SafeDirectoryWalkBudget'ta yazılı — bütçe site başına verilseydi tavan site sayısıyla
        // ÇARPILIR, yani 50 siteli bir abonelikte fiilen kalkardı.
        var taramaButcesi = YeniTaramaButcesi();

        foreach (var siteAdi in plan.Sites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var site = await iisSiteService.GetSiteAsync(siteAdi, cancellationToken);
                siteBilgileri[siteAdi] = site;

                if (request.IncludeFiles)
                {
                    var kok = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
                    taranan.AddRange(ScanFiles(kok, $"sites/{siteAdi}/files", atlananlar, taramaButcesi, cancellationToken));
                }
            }
            catch (Exception exception)
            {
                // Site IIS'te yoksa/okunamıyorsa SESSİZ GEÇİLMEZ: kullanıcı yedeğinde o sitenin
                // OLMADIĞINI bilmelidir.
                uyarilar.Add(new BackupRestoreWarningDto
                {
                    MessageKey = "backup.site_unavailable",
                    Target = siteAdi,
                    Detail = exception.Message
                });
            }
        }

        var indeks = BuildIndex(chainPlan.Stamp, taranan);
        var fark = BackupChainPlanner.Diff(chainPlan.PreviousIndex, indeks);
        var yazilacak = fark.Changed.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var parcalar = new List<string>();
        await using (var zipStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var yazilanDosya = AddFilesToArchive(archive, taranan, yazilacak, atlananlar);
            if (yazilanDosya > 0)
            {
                parcalar.Add(artimsal ? $"files(+{yazilanDosya})" : $"files({yazilanDosya})");
            }

            foreach (var (siteAdi, site) in siteBilgileri)
            {
                AddTextEntry(
                    archive,
                    $"sites/{siteAdi}/config/site.json",
                    System.Text.Json.JsonSerializer.Serialize(site, BackupJsonOptions));
            }

            if (siteBilgileri.Count > 0)
            {
                parcalar.Add($"sites({siteBilgileri.Count})");
            }

            // VERİTABANLARI: artımsal halkada bile HER ZAMAN TAM döküm. Bir SQL dökümünü "değişen
            // dosyalar" gibi parçalamak, geri yüklenmeyen bir yedek üretirdi.
            var dbSayisi = 0;
            foreach (var db in plan.Databases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (await TryAddDatabaseDumpAsync(archive, db.Engine, db.DatabaseName, "databases", cancellationToken))
                    {
                        dbSayisi++;
                    }
                    else
                    {
                        uyarilar.Add(new BackupRestoreWarningDto
                        {
                            MessageKey = "backup.database_dump_empty",
                            Target = $"{db.Engine}/{db.DatabaseName}"
                        });
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    uyarilar.Add(new BackupRestoreWarningDto
                    {
                        MessageKey = "backup.database_dump_failed",
                        Target = $"{db.Engine}/{db.DatabaseName}",
                        Detail = exception.Message
                    });
                }
            }

            if (dbSayisi > 0)
            {
                parcalar.Add($"databases({dbSayisi})");
            }

            // POSTA: burada taşınan şey posta KUTUSU ENVANTERİDİR (hesaplar, alias'lar, kotalar),
            // iletilerin kendisi DEĞİL. İletiler mail sunucusunun veri dizinindedir ve sunucu
            // geneli posta yedeğiyle (BackupMailAsync) alınır. Bu ayrım manifeste de yazılır;
            // "posta yedeklendi" deyip iletilerin gelmediğini geri yüklerken öğrenmek kabul edilemez.
            var postaSayisi = 0;
            foreach (var domain in plan.MailDomains)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var workspace = await mailService.GetWorkspaceAsync(domain, cancellationToken);
                    AddTextEntry(
                        archive,
                        $"mail/{domain}.json",
                        System.Text.Json.JsonSerializer.Serialize(workspace, BackupJsonOptions));
                    postaSayisi++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    uyarilar.Add(new BackupRestoreWarningDto
                    {
                        MessageKey = "backup.mail_domain_failed",
                        Target = domain,
                        Detail = exception.Message
                    });
                }
            }

            if (postaSayisi > 0)
            {
                parcalar.Add($"mail({postaSayisi})");
            }

            if (plan.FtpUsers.Count > 0)
            {
                var ftpKayitlari = plan.FtpUsers
                    .Select(user => new SubscriptionFtpEntry(user.UserName, user.SiteName, user.HomePath))
                    .ToArray();
                AddTextEntry(archive, "ftp/accounts.json", System.Text.Json.JsonSerializer.Serialize(ftpKayitlari, BackupJsonOptions));
                parcalar.Add($"ftp({ftpKayitlari.Length})");
            }

            AddTextEntry(archive, "subscription.json", System.Text.Json.JsonSerializer.Serialize(new
            {
                userId = kaynak,
                planId = scope.PlanId,
                planName = scope.PlanName,
                expiresAtUtc = scope.ExpiresAtUtc,
                maxSites = scope.MaxSites,
                maxDiskMb = scope.MaxDiskMb,
                sites = plan.Sites,
                databases = plan.Databases.Select(db => $"{db.Engine}:{db.DatabaseName}"),
                mailDomains = plan.MailDomains,
                ftpUsers = plan.FtpUsers.Select(user => user.UserName)
            }, BackupJsonOptions));

            WriteChainEntries(archive, chainPlan.Stamp, indeks, fark);

            AddTextEntry(archive, "manifest.json", System.Text.Json.JsonSerializer.Serialize(new
            {
                kind = "subscription",
                userId = kaynak,
                createdAtUtc = DateTime.UtcNow,
                parts = parcalar,
                items = plan.ItemCount,
                mode = chainPlan.Stamp?.Mode ?? BackupChainPlanner.ModeFull,
                chainId = chainPlan.Stamp?.ChainId ?? string.Empty,
                chainIndex = chainPlan.Stamp?.ChainIndex ?? 0,
                note = "Mail entries are MAILBOX INVENTORY (accounts/aliases/quotas), not messages. " +
                       "Database dumps are always FULL, even in incremental links. " +
                       "FTP and mailbox PASSWORDS cannot be exported and must be reset after restore."
            }, BackupJsonOptions));
        }

        return await FinalizeBackupAsync(
            targetPath,
            fileName,
            "subscription",
            kaynak,
            "zip",
            request,
            placement,
            atlananlar,
            cancellationToken,
            chainPlan.Stamp,
            chainPlan.Stamp is null ? null : indeks,
            kaynak,
            plan.ItemCount,
            contentWarnings: uyarilar);
    }

    /// <summary>
    /// Arşive giremeyen MANTIKSAL öğelerin (veritabanı, posta alanı, site) uyarılarını KAYDA
    /// taşır ve durumu düşürür.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden tek yerde:</b> bu blok abonelik yedeğinde yazılıydı, site yedeğinde YOKTU — yani
    /// aynı kural bir türde uygulanıp diğerinde unutulmuştu ve site yedeği dökümü boş çıkan
    /// veritabanını sessizce yutuyordu. İki kopya olsaydı bir sonraki tür de aynı boşlukla
    /// eklenirdi.
    /// </para>
    /// <para>
    /// <b>Neden <c>created_remote_failed</c> ezilmiyor:</b> off-site kopyanın olmadığını söylemek,
    /// eksik öğe uyarısından daha az önemli değildir. İkisi de kayıtta durur; biri durumda,
    /// diğeri uyarı listesinde.
    /// </para>
    /// </remarks>
    private static void ApplyContentWarnings(
        BackupRecordDto record,
        IReadOnlyCollection<BackupRestoreWarningDto> uyarilar)
    {
        if (uyarilar.Count == 0)
        {
            return;
        }

        record.Warnings.AddRange(uyarilar);

        if (record.Status == "created" || record.Status == "created_remote_synced")
        {
            record.Status = "created_with_errors";
        }
    }

    /// <summary>
    /// Çalışan süreç kilitlese bile (ör. SQLite <c>adapanel.db</c>) paylaşımlı okuma ile zip'e
    /// ekler; yine de okunamazsa dosya <paramref name="skipped"/>'a YAZILIR ve dönen değer
    /// <c>false</c> olur.
    /// </summary>
    /// <remarks>
    /// Bu yardımcının atladığı dosya panelin KENDİ VERİTABANIDIR (kullanıcılar, 2FA gizleri,
    /// denetim kayıtları). Sessizce atlanması, panelin kendini yedeklediğini SANDIĞI ama
    /// arşivde veritabanının hiç olmadığı bir durum üretiyordu.
    /// </remarks>
    private static bool AddSharedFileToArchive(ZipArchive archive, string filePath, string entryName, BackupSkipLog skipped)
    {
        try
        {
            using var source = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            source.CopyTo(entryStream);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            skipped.Add(entryName, exception);
            return false;
        }
    }

    /// <summary>
    /// İSTEKTEN gelen geri yükleme hedefini kabul edilebilir bir yazma hedefine çevirir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ÖLÇÜLEN AÇIK:</b> <c>TargetPath</c> mutlak bir yol olabiliyor ve HİÇ denetlenmiyordu.
    /// Panel <c>LocalSystem</c> koştuğu için bu, "yedekten geri yükle" düğmesini sunucudaki
    /// İSTENEN HER YERE yazan bir araca çeviriyordu. Somut sonucu şuydu: hedef
    /// <c>%ProgramData%\AdaPanel</c> verilip arşivdeki <c>config\server-owner.json</c> canlı kaydın
    /// ÜZERİNE yazılabiliyordu — yani sahip olmayan bir yönetici kendini SUNUCU SAHİBİ ilan edip
    /// terminale (SYSTEM kabuğu) giriyordu. Sahiplik korumasının tek dayanağı o dosyanın HTTP
    /// yüzeyinden yazılamamasıydı (bkz. ServerOwnerRegistry).
    /// </para>
    /// <para>
    /// <b>FAZLA KAPATMA NÖBETÇİSİ:</b> denetim YALNIZCA kullanıcının verdiği yola uygulanır.
    /// Panelin KENDİ varsayılan hedefi (veri kökü altındaki <c>restore/…</c> ve yedek kökü
    /// altındaki <c>restored/…</c>) bu kapıdan GEÇMEZ — geçseydi kural kendi kendini reddeder ve
    /// hedef verilmeyen her geri yükleme ölürdü. Sahibin felaket kurtarması da aynen çalışır:
    /// <c>E:\geri-yukleme</c>, <c>D:\siteler\musteri.com</c> gibi hedefler kabul edilir.
    /// </para>
    /// </remarks>
    private string IstenenHedefiDenetle(string rawTargetPath)
    {
        var hedef = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rawTargetPath.Trim()));
        var karar = PanelWriteTargetGuard.Evaluate(hedef, fileStore.ResolvePath(string.Empty));
        if (karar != PanelWriteTargetVerdict.Allowed)
        {
            throw new AppException(
                "backup.target_path_forbidden",
                400,
                400,
                $"{hedef}: {PanelWriteTargetGuard.Describe(karar)}");
        }

        return hedef;
    }

    private sealed record ScheduledTaskExport(string Name, string Xml);

    private sealed record ScheduledTaskIndexEntry(string Name, string Entry);

    // Planlı görevlerin ham XML dökümü (best-effort: dışa aktarılamazsa panel yedeği yine de oluşur).
    private static async Task<List<ScheduledTaskExport>> TryExportScheduledTasksAsync(CancellationToken cancellationToken)
    {
        try
        {
            const string script = @"$list = Get-ScheduledTask -TaskPath '\AdaPanel\' -ErrorAction SilentlyContinue | ForEach-Object {
  [pscustomobject]@{ Name = $_.TaskName; Xml = [string](Export-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath) }
}
$list | ConvertTo-Json -Depth 3";
            var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
            if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return [];
            }

            using var document = System.Text.Json.JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            ScheduledTaskExport[] items = root.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Array => System.Text.Json.JsonSerializer.Deserialize<ScheduledTaskExport[]>(root) ?? [],
                System.Text.Json.JsonValueKind.Object => [System.Text.Json.JsonSerializer.Deserialize<ScheduledTaskExport>(root)!],
                _ => []
            };

            return items
                .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Xml))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Panel yedeğindeki görevleri Görev Zamanlayıcı'ya geri kaydeder (XML: principal/tetikleyici
    /// birebir). BAŞARISIZ olan her görev <paramref name="uyarilar"/>'a yazılır.
    /// </summary>
    /// <remarks>
    /// <b>ÖLÇÜLEN HATA:</b> <c>Register-ScheduledTask</c> çağrısının DÖNÜŞÜ ATILIYORDU ve dış
    /// <c>catch</c> de sessizdi. Yani panel yedeğini geri yükleyen kullanıcı, zamanlanmış
    /// görevlerinin (gecelik yedek dahil) HİÇ KURULMADIĞINI göremiyordu: durum "restored" diyordu.
    /// Bir sonraki felakete kadar yedeksiz kalmanın en sessiz yolu buydu.
    /// <para>
    /// <b>İKİNCİ ÖLÇÜLEN AÇIK — XML'İN İÇERİĞİ HİÇ DENETLENMİYORDU.</b> Yol denetimi ("XML çıkarma
    /// klasörünün altında mı") ve boyut tavanı vardı; ama dosyanın İÇİ olduğu gibi
    /// <c>Register-ScheduledTask -Xml</c>'e veriliyordu. Görev XML'i kendi <c>Principal</c>'ını ve
    /// <c>Exec/Command</c>'ını taşır: arşive konan tek bir XML, sunucuda SYSTEM olarak istenen
    /// komutu çalıştıran bir görev kurar. Arşiv GÜVENİLMEZ GİRDİDİR (yedek kökü yönetici tarafından
    /// seçilebilir ve dosya yöneticisinden ulaşılabilir bir klasör olabilir), yani bu "cron adından
    /// SYSTEM RCE" ile aynı sınıftır.
    /// </para>
    /// <para>
    /// <b>SEÇİLEN SINIR: kaydı YALNIZ SUNUCU SAHİBİ yapar.</b> Doktrin 552b028'de daraltıldı —
    /// "panel yöneticisi bir Windows yöneticisi değildir, TEK istisna sunucu sahibidir". Arşivden
    /// gelen ham görev XML'ini kaydetmek, tam olarak SYSTEM'de kod çalıştırma yetkisidir.
    /// </para>
    /// <para>
    /// <b>Neden "SYSTEM principal'ı olan XML'i reddet" DEĞİL:</b> ölçüldü — panelin KENDİ görevleri
    /// (<c>SchedulerService.BuildCreateScript</c>) bilerek <c>UserId 'SYSTEM' … RunLevel Highest</c>
    /// ile kurulur. Böyle bir kural, meşru bir panel yedeğinin BÜTÜN görevlerini (gecelik yedek
    /// dahil) reddederdi: fazla kapatma, üstelik sessiz veri kaybı yönünde.
    /// </para>
    /// <para>
    /// <b>FAZLA KAPATMA NÖBETÇİSİ:</b> sahibin geri yüklemesi eskisi gibi HER görevi kurar. Sahip
    /// olmayan yöneticide iş SESSİZCE yarım kalmaz — görev başına bir uyarı üretilir
    /// (<c>backup.restore_task_owner_required</c>), XML'ler çıkarma klasöründe DURUR ve durum
    /// "restored_with_errors" olur.
    /// </para>
    /// </remarks>
    internal static async Task TryRestoreScheduledTasksAsync(
        string extractedRoot,
        bool caginSahibiMi,
        List<BackupRestoreWarningDto> uyarilar,
        Func<string, string, CancellationToken, Task<CommandResult>> kaydedici,
        CancellationToken cancellationToken)
    {
        try
        {
            var indexPath = Path.Combine(extractedRoot, "tasks", "index.json");
            if (!File.Exists(indexPath))
            {
                return;
            }

            if (!CikarilmisEnvanterOlculebilir(indexPath, "tasks/index.json", uyarilar))
            {
                return;
            }

            var index = System.Text.Json.JsonSerializer.Deserialize<List<ScheduledTaskIndexEntry>>(
                await File.ReadAllTextAsync(indexPath, cancellationToken),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

            foreach (var item in index)
            {
                // KAPSAM DENETİMİ — `item.Entry` ARŞİVİN İÇİNDEN gelir, yani geri yüklenen paketi
                // hazırlayan kim ise ONUN yazdığı bir dizedir. Denetimsiz `Path.Combine` ile
                // "..\..\Windows\..." yazmak çıkarma klasörünün DIŞINA çıkardı: panel o dosyayı
                // okur ve içeriğini görev XML'i sanıp Register-ScheduledTask'a verirdi. Kural
                // ICAT EDİLMEDİ, deponun tek kaynağı kullanıldı (PathContainment.IsInsideRoot;
                // SiteBuilderPathGuard.ResolveWriteTarget de aynısını çağırır).
                var xmlPath = Path.GetFullPath(Path.Combine(extractedRoot, item.Entry.Replace('/', Path.DirectorySeparatorChar)));
                if (string.IsNullOrWhiteSpace(item.Name)
                    || !PathContainment.IsInsideRoot(extractedRoot, xmlPath)
                    || !File.Exists(xmlPath))
                {
                    continue;
                }

                // BOYUT TAVANI. Aşağıdaki üç satır girdiyi ~2,67 KAT büyütür: Unicode.GetBytes
                // UTF-16 üretir (karakter başına 2 bayt), base64 onu 4/3 ile çarpar, sonuç bir de
                // betik dizesine gömülür. Kaynak dosya SALDIRGANIN ARŞİVİNDEN gelir ve bu bir
                // DÖNGÜ — girdi sayısını da o belirler. Yani 200 MB'lık bir XML tek başına
                // yarım gigabaytın üzerinde bellek demekti.
                //
                // Sınır cömert seçildi: gerçek bir zamanlanmış görev XML'i birkaç KB'dır, 1 MiB
                // meşru hiçbir görevi engellemez. Atlanan görev SESSİZ DEĞİL, uyarı listesine
                // düşer -- geri yükleme "başarılı" deyip görevi eksik bırakmamalı.
                const long EnBuyukGorevXmlBaytI = 1024L * 1024L;
                var xmlBilgi = new FileInfo(xmlPath);
                if (xmlBilgi.Length > EnBuyukGorevXmlBaytI)
                {
                    uyarilar.Add(new BackupRestoreWarningDto
                    {
                        MessageKey = "backup.restore_task_too_large",
                        Target = item.Name,
                        Detail = $"{xmlBilgi.Length} > {EnBuyukGorevXmlBaytI}"
                    });
                    continue;
                }

                // ============================================================================
                // XML'İN İÇERİĞİ SUNUCUDA SYSTEM OLARAK KOŞACAK KOMUTU TAŞIR.
                //
                // Görev XML'i kendi <Principal> ve <Actions><Exec><Command> bölümlerini içerir;
                // panel onu OLDUĞU GİBİ Register-ScheduledTask'a veriyordu. Yani arşive tek bir
                // XML koyabilen kişi, sunucuda SYSTEM olarak istediği komutu kuran bir görev
                // yaratıyordu. Bu, doktrinin "panel yöneticisi Windows yöneticisi DEĞİLDİR --
                // tek istisna sunucu SAHİBİdir" cümlesinin doğrudan ihlaliydi.
                //
                // Kapı SAHİBE AÇIK: felaket kurtarma sahibin işidir ve yarım kurulan bir görev
                // seti (gecelik yedeğin olmaması) en pahalı sonuçtur.
                // ============================================================================
                if (!caginSahibiMi)
                {
                    uyarilar.Add(new BackupRestoreWarningDto
                    {
                        MessageKey = "backup.restore_task_owner_required",
                        Target = item.Name,
                        Detail = xmlPath
                    });
                    continue;
                }

                // XML'i base64 taşı: tırnak/özel karakter kaçış derdi tamamen ortadan kalkar.
                var xml = await File.ReadAllTextAsync(xmlPath, cancellationToken);
                var sonuc = await kaydedici(item.Name, xml, cancellationToken);
                if (!sonuc.Success)
                {
                    // Best-effort DEVAM EDER (bir görev diğerlerini durdurmaz) ama SESSİZ DEĞİL.
                    uyarilar.Add(new BackupRestoreWarningDto
                    {
                        MessageKey = "backup.restore_task_failed",
                        Target = item.Name,
                        Detail = KisaHataMetni(sonuc.StandardError, sonuc.StandardOutput)
                    });
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Envanter okunamadı: HİÇBİR görev geri yüklenmedi. Eskiden bu da sessizdi.
            uyarilar.Add(new BackupRestoreWarningDto
            {
                MessageKey = "backup.restore_task_failed",
                Target = "tasks/index.json",
                Detail = exception.Message
            });
        }
    }

    /// <summary>
    /// Görevi GERÇEKTEN Görev Zamanlayıcı'ya kaydeden üretim yolu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ayrı bir temsilciye alınmasının sebebi ÖLÇÜLEBİLİRLİKTİR: eski hâlde kaydın kendisi
    /// <c>TryRestoreScheduledTasksAsync</c>'in içindeydi ve o metodu bir testten çağırmak bu
    /// makinede GERÇEK bir zamanlanmış görev kurmayı denemek demekti. Sonuç, korumanın davranışının
    /// hiç ölçülememesi ve yerine KAYNAK TARAYAN bir testin yazılmış olmasıydı
    /// (<c>BackupTaskImportGuardTests</c>) — o test, kural kaldırılsa bile metin kaynakta durduğu
    /// sürece yeşil kalırdı.
    /// </para>
    /// <para>
    /// KATI SÜRÜM ZORUNLU, yoksa sonuç HİÇBİR ŞEY ÖLÇMEZ: PowerShell'in sonlandırıcı olmayan
    /// hatası (<c>Register-ScheduledTask</c> "Access denied") ekrana yazılır ama powershell.exe
    /// yine 0 ile çıkar — yani <c>Success</c> daima true olurdu ve uyarı hiç üretilmezdi.
    /// <c>RunPowerShellStrictAsync</c> hatayı çıkış koduna yansıtır (gerekçesi ProcessRunner'da).
    /// </para>
    /// </remarks>
    private static Task<CommandResult> GorevKaydetAsync(string ad, string xml, CancellationToken cancellationToken)
    {
        var xmlBase64 = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(xml));
        var script = $@"$xml = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{xmlBase64}'))
Register-ScheduledTask -TaskName '{ad.Replace("'", "''")}' -TaskPath '\AdaPanel\' -Xml $xml -Force | Out-Null";
        return ProcessRunner.RunPowerShellStrictAsync(script, cancellationToken);
    }

    /// <summary>
    /// Süreç çıktısının KAYDA yazılabilir kısa hâli. Boş çıktıyı boş bırakmaz: sebepsiz bir uyarı,
    /// kullanıcıya "bir şey oldu ama ne olduğunu söylemiyorum" demektir.
    /// </summary>
    private static string KisaHataMetni(string? standardError, string? standardOutput)
    {
        var metin = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
        metin = (metin ?? string.Empty).Trim();
        if (metin.Length == 0)
        {
            return "exit_code=nonzero; output=empty";
        }

        return metin.Length <= 500 ? metin : metin[..500];
    }

    /// <summary>
    /// Tam-sunucu DR: panel yedeğindeki IIS envanterinden (iis/sites.json) eksik siteleri yeniden kurar.
    /// Best-effort — site başına hata diğerlerini durdurmaz; sonuç, çıkarma klasörüne
    /// iis-restore-report.txt olarak yazılır (kullanıcı ne kurulup ne atlandığını görür) VE
    /// başarısızlıklar <paramref name="uyarilar"/> ile KAYDA taşınır.
    /// HTTPS bağlamaları bilerek kurulmaz (sertifika makineye bağlıdır; SSL ekranından yeniden verilir).
    /// <para>
    /// <b>ÖLÇÜLEN HATA:</b> hatalar YALNIZCA rapor DOSYASINA yazılıyordu. O dosya, çıkarılan
    /// klasörün içinde duruyor; panelde hiçbir yerde görünmüyor ve kimse açmıyor. Kayıt ise
    /// koşulsuz "restored" diyordu — yani "sitelerin yarısı kurulamadı" gerçeği, kullanıcının
    /// bakmadığı bir metin dosyasına gömülüyordu.
    /// </para>
    /// </summary>
    private async Task TryRestoreIisSitesAsync(
        string extractedRoot,
        List<string> report,
        List<BackupRestoreWarningDto> uyarilar,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshotPath = Path.Combine(extractedRoot, "iis", "sites.json");
            if (!File.Exists(snapshotPath))
            {
                return;
            }

            if (!CikarilmisEnvanterOlculebilir(snapshotPath, "iis/sites.json", uyarilar))
            {
                return;
            }

            var snapshot = System.Text.Json.JsonSerializer.Deserialize<List<AdaPanel.Core.Dtos.Sites.SiteDto>>(
                await File.ReadAllTextAsync(snapshotPath, cancellationToken),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            if (snapshot.Count == 0)
            {
                return;
            }

            var existing = (await iisSiteService.GetSitesAsync(cancellationToken)).Select(site => site.Name);
            var missing = IisSnapshotRestore.GetMissingSites(snapshot, existing);
            report.Add($"Envanter: {snapshot.Count} site; eksik: {missing.Count}.");

            foreach (var site in missing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var (create, extraBindings) = IisSnapshotRestore.ToProvisioningPlan(site);
                    if (!string.IsNullOrWhiteSpace(create.PhysicalPath))
                    {
                        Directory.CreateDirectory(create.PhysicalPath);
                    }

                    await iisSiteService.CreateSiteAsync(create, cancellationToken);
                    foreach (var binding in extraBindings)
                    {
                        try
                        {
                            await iisSiteService.AddBindingAsync(site.Name, binding, cancellationToken);
                        }
                        catch (Exception bindingException)
                        {
                            report.Add($"  {site.Name}: ek bağlama {binding.HostName}:{binding.Port} eklenemedi — {bindingException.Message}");
                            uyarilar.Add(new BackupRestoreWarningDto
                            {
                                MessageKey = "backup.restore_site_failed",
                                Target = site.Name,
                                Detail = $"binding={binding.HostName}:{binding.Port}; reason={bindingException.Message}"
                            });
                        }
                    }

                    report.Add($"  {site.Name}: yeniden kuruldu ({1 + extraBindings.Count} http bağlama). İçerik için site yedeğini geri yükleyin; HTTPS'i SSL ekranından yeniden verin.");
                }
                catch (Exception siteException)
                {
                    report.Add($"  {site.Name}: KURULAMADI — {siteException.Message}");
                    uyarilar.Add(new BackupRestoreWarningDto
                    {
                        MessageKey = "backup.restore_site_failed",
                        Target = site.Name,
                        Detail = siteException.Message
                    });
                }
            }
        }
        catch (Exception exception)
        {
            report.Add($"IIS envanteri geri yüklenemedi: {exception.Message}");
            uyarilar.Add(new BackupRestoreWarningDto
            {
                MessageKey = "backup.restore_site_failed",
                Target = "iis/sites.json",
                Detail = exception.Message
            });
        }
    }

    private sealed class FtpUserSnapshot
    {
        public string UserName { get; set; } = string.Empty;
        public string HomePath { get; set; } = string.Empty;
        public string SiteName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Tam-sunucu DR: yedekteki config/ftp-users.json kayıtlarından FTP hesaplarını yeniden kurar.
    /// Windows yerel hesap PAROLALARI yedeğe giremez (dışa aktarılamaz) → hesap RASTGELE parolayla
    /// açılır ve rapora "parolayı FTP ekranından sıfırlayın" düşülür. Hesap zaten varsa parolasına
    /// dokunulmaz (CreateFtpUserAsync mevcut hesapta yalnız ACL/ana dizini tazeler — idempotent).
    /// </summary>
    private async Task TryRestoreFtpUsersAsync(
        string extractedRoot,
        List<string> report,
        List<BackupRestoreWarningDto> uyarilar,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshotPath = Path.Combine(extractedRoot, "config", "ftp-users.json");
            if (!File.Exists(snapshotPath))
            {
                return;
            }

            if (!CikarilmisEnvanterOlculebilir(snapshotPath, "config/ftp-users.json", uyarilar))
            {
                return;
            }

            var snapshot = System.Text.Json.JsonSerializer.Deserialize<List<FtpUserSnapshot>>(
                await File.ReadAllTextAsync(snapshotPath, cancellationToken),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            if (snapshot.Count == 0)
            {
                return;
            }

            report.Add($"FTP envanteri: {snapshot.Count} hesap.");
            foreach (var user in snapshot.Where(item => !string.IsNullOrWhiteSpace(item.UserName)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await ftpService.CreateFtpUserAsync(new AdaPanel.Core.Dtos.Ftp.CreateFtpUserRequest
                    {
                        UserName = user.UserName,
                        Password = $"Dr{Guid.NewGuid():N}aA1",
                        SiteName = user.SiteName,
                        HomePath = user.HomePath
                    }, cancellationToken);
                    report.Add($"  FTP {user.UserName}: hazır — parolayı FTP ekranından sıfırlayın (parolalar yedeğe giremez).");
                }
                catch (Exception exception)
                {
                    report.Add($"  FTP {user.UserName}: KURULAMADI — {exception.Message}");
                    uyarilar.Add(new BackupRestoreWarningDto
                    {
                        MessageKey = "backup.restore_ftp_failed",
                        Target = user.UserName,
                        Detail = exception.Message
                    });
                }
            }
        }
        catch (Exception exception)
        {
            report.Add($"FTP envanteri geri yüklenemedi: {exception.Message}");
            uyarilar.Add(new BackupRestoreWarningDto
            {
                MessageKey = "backup.restore_ftp_failed",
                Target = "config/ftp-users.json",
                Detail = exception.Message
            });
        }
    }

    private static async Task TryWriteRestoreReportAsync(string extractedRoot, List<string> report, CancellationToken cancellationToken)
    {
        if (report.Count == 0)
        {
            return;
        }

        try
        {
            await File.WriteAllLinesAsync(Path.Combine(extractedRoot, "iis-restore-report.txt"), report, cancellationToken);
        }
        catch
        {
            // rapor yazılamazsa akış bozulmaz
        }
    }

    private const string StalwartTaskName = "AdaStalwart";

    /// <summary>
    /// Stalwart mail verisinin yedeği. RocksDB canlı kopyalanamayacağından servis
    /// (zamanlanmış görev) kısa süre durdurulur; zip alınır; her durumda yeniden başlatılır.
    /// </summary>
    public async Task<BackupRecordDto> BackupMailAsync(BackupMailRequest request, CancellationToken cancellationToken = default)
    {
        var installDirectory = ResolveStalwartInstallDirectory();
        var dataDirectory = Path.Combine(installDirectory, "data");
        if (!Directory.Exists(dataDirectory))
        {
            throw new AppException("backup.mail_not_installed", 404, 404);
        }

        var preferences = await preferencesService.GetAsync(cancellationToken);
        // KAYNAK KLASÖRÜ YOK, bilerek: bu yedeğin kaynağı TEKTİR — Stalwart'ın veri deposunun
        // TAMAMI. Alan adı başına bir kaynak klasörü açmak, olmayan bir ayrımı varmış gibi
        // gösterirdi (bkz. BackupMailAsync özeti).
        var placement = BackupLayout.Ensure(preferences.DefaultBackupRoot, "mail");
        var backupsDirectory = placement.LocalDirectory;
        var fileName = $"stalwart-mail-{DateTime.UtcNow:yyyyMMddHHmmss}.zip";
        var targetPath = Path.Combine(backupsDirectory, fileName);

        await StopStalwartAsync(installDirectory, cancellationToken);
        // Yeniden baslatma uyarisi TUTULUR: `finally` icinden firlatmak, asil istisnayi maskelerdi.
        // Yedek basarili olsa bile posta kapali kaldiysa kullanici bunu OGRENMELI.
        BackupRestoreWarningDto? postaUyarisi;
        try
        {
            ZipFile.CreateFromDirectory(dataDirectory, targetPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        }
        finally
        {
            postaUyarisi = await StartStalwartAsync(cancellationToken);
        }

        // Dosya TARAMASI yok: ZipFile.CreateFromDirectory tek dosyayı bile atlayamaz, atlarsa
        // istisna atar ve yedek hiç oluşmaz (servis durdurulmuş hâlde alındığı için kilit de yok).
        return await FinalizeBackupAsync(targetPath, fileName, "mail", "stalwart", "zip", request, placement,
            skipped: null, cancellationToken, contentWarnings: postaUyarisi is null ? [] : [postaUyarisi]);
    }

    /// <summary>Posta verisini geri yukler; posta sunucusu acilamadiysa uyari doner.</summary>
    private async Task<BackupRestoreWarningDto?> RestoreMailAsync(string archivePath, CancellationToken cancellationToken)
    {
        var installDirectory = ResolveStalwartInstallDirectory();
        var dataDirectory = Path.Combine(installDirectory, "data");

        await StopStalwartAsync(installDirectory, cancellationToken);
        BackupRestoreWarningDto? postaUyarisi;
        try
        {
            // Mevcut veri yana alınır; geri yükleme başarısız olursa elle dönülebilir.
            if (Directory.Exists(dataDirectory))
            {
                var sideline = dataDirectory + $".old-{DateTime.UtcNow:yyyyMMddHHmmss}";
                Directory.Move(dataDirectory, sideline);
            }

            // Yedek "data/..." girdileri içerir; kurulum köküne çıkarılır.
            ZipFile.ExtractToDirectory(archivePath, installDirectory, overwriteFiles: true);
        }
        finally
        {
            postaUyarisi = await StartStalwartAsync(cancellationToken);
        }

        return postaUyarisi;
    }

    private static string ResolveStalwartInstallDirectory()
    {
        var root = Environment.ExpandEnvironmentVariables("%ProgramData%\\AdaPanel\\runtimes\\stalwart");
        if (!Directory.Exists(root))
        {
            throw new AppException("backup.mail_not_installed", 404, 404);
        }

        return root;
    }

    /// <summary>Süreç gerçekten durdu mu diye kaç tur denenir (her tur arası 1 sn).</summary>
    private const int StalwartStopAttempts = 10;

    /// <summary>
    /// Stalwart'ı durdurur ve DURDUĞUNU DOĞRULAR.
    /// <para>
    /// Eski hâlde <c>schtasks /End</c> sonucu okunmuyor, <c>Kill</c> çıplak bir <c>catch { }</c> ile
    /// yutuluyor ve ardından yalnızca 1 saniye bekleniyordu. Süreç ayaktayken RocksDB dosyaları
    /// kopyalanırsa GERİ YÜKLENEMEYEN bir posta yedeği üretilir — üstelik "yedeklendi" denerek.
    /// Bozuk yedeği başarılı saymaktansa yedek almayı reddetmek gerekir: kullanıcı o zaman gerçek
    /// bir yedeği olmadığını bilir.
    /// </para>
    /// </summary>
    private static async Task StopStalwartAsync(string installDirectory, CancellationToken cancellationToken)
    {
        // schtasks başarısız olabilir (görev yok, zaten durmuş). TEK BAŞINA KARAR VERMEZ; asıl ölçüm
        // aşağıdaki süreç doğrulamasıdır, bu yüzden sonucu yalnızca teşhis metnine taşınır.
        var end = await ProcessRunner.RunAsync(
            "schtasks.exe",
            $"/End /TN \"{StalwartTaskName}\"",
            cancellationToken: cancellationToken);

        var prefix = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var blockers = Array.Empty<string>();
        for (var attempt = 0; attempt < StalwartStopAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            blockers = KillStalwartProcesses(prefix);
            if (blockers.Length == 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        var teshis = $"schtasks /End exit={end.ExitCode} {end.StandardError}".Trim() +
                     $"\r\nHalen calisan/dogrulanamayan surecler: {string.Join(", ", blockers)}";
        throw new AppException("backup.mail_stop_failed", 409, 409, teshis);
    }

    /// <summary>
    /// Kurulum dizininden çalışan stalwart süreçlerini sonlandırmayı dener ve denemeden SONRA hâlâ
    /// engel olanları tanımlarıyla döner. Boş dizi = "dizin gerçekten serbest".
    /// <para>
    /// Yolu okunamayan bir süreç de engel sayılır: onun bizim örneğimiz OLMADIĞINI ölçemiyoruz ve
    /// ölçemediğimiz şeye "sorun yok" demek, bu hatanın ta kendisiydi.
    /// </para>
    /// </summary>
    private static string[] KillStalwartProcesses(string prefix)
    {
        var blockers = new List<string>();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("stalwart"))
        {
            try
            {
                if (process.HasExited)
                {
                    continue;
                }

                string? path;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception exception)
                {
                    blockers.Add($"pid {process.Id} (yol okunamadi: {exception.Message})");
                    continue;
                }

                if (string.IsNullOrEmpty(path) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // başka bir kurulumun süreci — bizim veri dizinimizi tutmuyor
                }

                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10000);
                }
                catch (Exception exception)
                {
                    blockers.Add($"pid {process.Id} sonlandirilamadi: {exception.Message}");
                    continue;
                }

                if (!process.HasExited)
                {
                    blockers.Add($"pid {process.Id} halen calisiyor");
                }
            }
            catch (InvalidOperationException)
            {
                // Süreç bu arada çıkmış; engel değil.
            }
            finally
            {
                process.Dispose();
            }
        }

        return blockers.ToArray();
    }

    /// <summary>
    /// Posta sunucusunu yeniden başlatır ve BAŞLADIĞINI ÖLÇER. Başlayamadıysa uyarı, başladıysa
    /// <c>null</c> döner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Eski hâli dönüşü ATIYORDU</b> — iki çağrı da <c>finally</c> içinde, sonucuna bakmadan.
    /// Yedekleme ve geri yükleme posta sunucusunu bilerek DURDURUYOR (kilitli veri dosyası
    /// kopyalanamaz). Yeniden başlatma başarısız olursa posta KAPALI kalır, ama kayıt "created" /
    /// "restored" der. Yani panel, kendi kapattığı bir servisi açamadığını fark etmiyordu:
    /// müşteri gece yedeğinden sonra posta almamaya başlar ve sebebini panelde bulamaz.
    /// </para>
    /// <para>
    /// <b>Çıkış kodu TEK BAŞINA yetmez</b> — bu oturumun tekrarlanan dersi. <c>schtasks /Run</c>
    /// görevi tetikleyip 0 döner; görevin gerçekten koştuğunu söylemez. Bu yüzden tetiklemeden
    /// sonra durum GERİ OKUNUR.
    /// </para>
    /// <para>
    /// <b>Ölçülemeyen durum uyarıya çevrilmez.</b> Sorgu okunamıyorsa (yetki, dil, biçim) bu
    /// "posta kapalı" demek değildir; uydurma bir uyarı, kullanıcıyı olmayan bir arızanın peşine
    /// düşürürdü. O durumda uyarı verilmez — yalnızca kesin bilinen iki hâl bildirilir.
    /// </para>
    /// </remarks>
    private static async Task<BackupRestoreWarningDto?> StartStalwartAsync(CancellationToken cancellationToken)
    {
        var tetikleme = await ProcessRunner.RunAsync(
            "schtasks.exe",
            $"/Run /TN \"{StalwartTaskName}\"",
            cancellationToken: cancellationToken);

        if (!tetikleme.Success)
        {
            return new BackupRestoreWarningDto
            {
                MessageKey = "backup.mail_service_start_failed",
                Target = StalwartTaskName,
                Detail = string.IsNullOrWhiteSpace(tetikleme.StandardError)
                    ? tetikleme.StandardOutput
                    : tetikleme.StandardError
            };
        }

        var sorgu = await ProcessRunner.RunAsync(
            "schtasks.exe",
            $"/Query /TN \"{StalwartTaskName}\" /FO LIST",
            cancellationToken: cancellationToken);

        if (!sorgu.Success || string.IsNullOrWhiteSpace(sorgu.StandardOutput))
        {
            // ÖLÇÜLEMEDİ: "kapalı" DEMEZ. Bkz. yukarıdaki gerekçe.
            return null;
        }

        // Durum satırı yerelleştirilmiştir ("Running" / "Çalışıyor"), o yüzden ÇALIŞIYOR aranmaz;
        // aranan şey KESİN OLARAK ÇALIŞMADIĞINI söyleyen iki durumdur. Bilinmeyen bir metin
        // "kapalı" sayılmaz — yine ölçülememiş kabul edilir.
        var durdurulmus = sorgu.StandardOutput.Contains("Disabled", StringComparison.OrdinalIgnoreCase)
            || sorgu.StandardOutput.Contains("Devre D", StringComparison.OrdinalIgnoreCase);

        return durdurulmus
            ? new BackupRestoreWarningDto
            {
                MessageKey = "backup.mail_service_not_running",
                Target = StalwartTaskName,
                Detail = sorgu.StandardOutput.Trim()
            }
            : null;
    }

    public async Task<BackupRecordDto> RestoreAsync(RestoreBackupRequest request, CancellationToken cancellationToken = default)
    {
        var backups = (await GetBackupsAsync(cancellationToken)).ToArray();
        var record = backups.FirstOrDefault(item => item.Id == request.BackupId)
            ?? throw new AppException("backup.not_found", 404, 404);

        // "backup.not_found" DEMEYİZ: kayıt duruyor, DOSYASI yok. İkisini aynı mesaja bağlamak,
        // kullanıcıyı felaket anında "yedek listede var ama bulunamadı" çelişkisiyle baş başa
        // bırakırdı. Ayrı anahtar sebebi (elle silinmiş/taşınmış dosya) açıkça söyler.
        if (!File.Exists(record.Path))
        {
            throw new AppException("backup.file_missing", 410, 410, $"{record.Name} -> {record.Path}");
        }

        // Önceki bir geri yüklemeden kalan uyarılar bu çalışmanın sonucu sanılmasın.
        record.Warnings = [];
        record.PreservedPath = string.Empty;

        // ZİNCİR SAĞLAMLIĞI CANLI İÇERİĞE DOKUNMADAN ÖNCE ÖLÇÜLÜR.
        //
        // Artımsal bir halka, tam yedek + aradaki bütün halkalar uygulanmadan anlam ifade etmez.
        // Eksik halkayla "elimizdekini uygulayalım" demek, sitenin hangi dosyalarının eski
        // kaldığını kimsenin bilmemesi demektir. Plesk kopuk zinciri işaretler ve geri yüklerken
        // eksik yedeklerin ADINI söyler; panel de aynısını yapar ama işi hiç başlatmaz.
        var chain = BackupChainPlanner.Resolve(backups, record.Id, IsUsableBackup);
        if (chain.Broken)
        {
            throw new AppException(
                "backup.chain_broken",
                409,
                409,
                $"Eksik halkalar: {string.Join(", ", chain.MissingLinks)}");
        }

        // Şifreliyse ÖNCE çözülür: yanlış parola / kurcalanmış arşiv burada, canlı içeriğe hiç
        // dokunulmadan durur. Çözülmüş geçici kopya iş bitince silinir.
        using var content = await OpenContentAsync(record, request.EncryptionPassword, cancellationToken);

        if (string.Equals(record.Type, "mail", StringComparison.OrdinalIgnoreCase))
        {
            // POSTA GERI YUKLEMESINDE DE "YARIM ISE RESTORED DENMEZ" kurali gecerli. Panel posta
            // sunucusunu kendisi durdurdu; acamadiysa veri donmus olsa bile posta CALISMIYORDUR.
            var postaUyarisi = await RestoreMailAsync(content.FilePath, cancellationToken);
            if (postaUyarisi is not null)
            {
                record.Warnings.Add(postaUyarisi);
            }

            record.Status = record.Warnings.Count == 0 ? "restored" : "restored_with_errors";
            await UpsertRecordAsync(record, cancellationToken);
            return record;
        }

        // ABONELİK yedeği: sitelerin dosyaları kendi köklerine, veritabanları içe aktarılarak,
        // posta/FTP hesapları yeniden kurularak geri yüklenir. Zincirin bütün halkaları sırayla.
        if (string.Equals(record.Type, "subscription", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(record.Format, "zip", StringComparison.OrdinalIgnoreCase))
        {
            var durum = new RestoreProgress();
            try
            {
                await RestoreSubscriptionChainAsync(
                    record,
                    content.FilePath,
                    chain,
                    backups,
                    request.EncryptionPassword,
                    durum,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await KaydiYarimIsaretleAsync(record, durum, exception);
                throw;
            }

            record.Warnings = durum.Warnings;
            record.PreservedPath = durum.PreservedPath;
            record.Status = durum.Warnings.Count == 0 ? "restored" : "restored_with_errors";
            await UpsertRecordAsync(record, cancellationToken);
            return record;
        }

        // profesyonel panel benzeri tam-site yedeği (files/ + databases/ + config/ içeren birleşik .zip):
        // dosyaları sitenin köküne, veritabanlarını içe aktararak gerçek geri yükleme yap.
        if (string.Equals(record.Type, "site", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(record.Format, "zip", StringComparison.OrdinalIgnoreCase) &&
            IsCombinedSiteArchive(content.FilePath))
        {
            var durum = new RestoreProgress();
            try
            {
                await RestoreSiteChainAsync(
                    record,
                    content.FilePath,
                    chain,
                    backups,
                    request.TargetPath,
                    request.EncryptionPassword,
                    durum,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await KaydiYarimIsaretleAsync(record, durum, exception);
                throw;
            }

            record.Warnings = durum.Warnings;
            record.PreservedPath = durum.PreservedPath;

            // YARIM KALAN İŞE "restored" DENMEZ. Dosyalar canlı kökün üzerine zaten yazıldığı için
            // bu durumda kullanıcının elinde ne eski hâl ne de dönmüş bir veritabanı vardır;
            // farkında olmadan devam etmesi en pahalı sonuçtur.
            record.Status = durum.Warnings.Count == 0 ? "restored" : "restored_with_errors";
            await UpsertRecordAsync(record, cancellationToken);
            return record;
        }

        if (string.Equals(record.Format, "zip", StringComparison.OrdinalIgnoreCase))
        {
            var destination = string.IsNullOrWhiteSpace(request.TargetPath)
                ? fileStore.EnsureDirectory($"restore/{record.Source}-{DateTime.UtcNow:yyyyMMddHHmmss}")
                : IstenenHedefiDenetle(request.TargetPath);

            // Hedefin KENDİSİ bağlantı olamaz — gerekçe EnsureRestoreRootIsNotLink'te.
            EnsureRestoreRootIsNotLink(destination);
            Directory.CreateDirectory(destination);

            // ZipFile.ExtractToDirectory DEĞİL: BCL kavşağı görmez ve yazma onu takip eder (ölçüldü).
            var kurulumUyarilari = ExtractArchiveGuarded(content.FilePath, destination, cancellationToken);

            // Panel yedeği: içindeki planlı görevleri (tasks/*.xml) Görev Zamanlayıcı'ya otomatik geri kaydet
            // + tam-sunucu DR: eksik IIS siteleri + FTP hesapları yeniden kurulur (rapor: iis-restore-report.txt).
            if (string.Equals(record.Type, "panel", StringComparison.OrdinalIgnoreCase))
            {
                await TryRestoreScheduledTasksAsync(
                    destination,
                    await CaginSahibiMiAsync(cancellationToken),
                    kurulumUyarilari,
                    GorevKaydetAsync,
                    cancellationToken);
                var report = new List<string>();
                await TryRestoreIisSitesAsync(destination, report, kurulumUyarilari, cancellationToken);
                await TryRestoreFtpUsersAsync(destination, report, kurulumUyarilari, cancellationToken);
                await TryWriteRestoreReportAsync(destination, report, cancellationToken);
            }

            // YARIM KALAN İŞE "restored" DENMEZ — kural bu dosyada zaten YAZILIYDI (birleşik site
            // ve abonelik geri yüklemesinde), yalnız BURADA uygulanmıyordu. Panel yedeği tam-sunucu
            // felaket kurtarmanın kendisidir: görevleri, siteleri ve FTP hesaplarının yarısı
            // kurulamamışken "restored" damgası, kullanıcının ayağa kalktığını SANMASINA yol açar.
            // Artık uyarılar kayda taşınır ve durum onları yansıtır.
            record.Warnings.AddRange(kurulumUyarilari);
            record.Status = kurulumUyarilari.Count == 0 ? "restored" : "restored_with_errors";
            record.Path = destination;
            // Kayıt artık ÇIKARILMIŞ dizini gösteriyor; orası şifreli değildir. Bayrağı "true"
            // bırakmak, bir sonraki geri yüklemede olmayan bir şifre için parola sordururdu.
            record.Encrypted = false;
        }
        else if (string.Equals(record.Format, "sql", StringComparison.OrdinalIgnoreCase))
        {
            var sourceParts = record.Source.Split(':', 2);
            await databaseService.ImportDumpAsync(new AdaPanel.Core.Dtos.Databases.DatabaseDumpRequest
            {
                Engine = sourceParts[0],
                DatabaseName = sourceParts.Length > 1 ? sourceParts[1] : string.Empty,
                FilePath = content.FilePath
            }, cancellationToken);
            record.Status = "restored";
        }

        await UpsertRecordAsync(record, cancellationToken);
        return record;
    }

    /// <summary>
    /// Geri yükleme yarıda KESİLDİĞİNDE kaydı dürüst bırakır: durum + yana alınan içeriğin yeri +
    /// sebep. Sonra istisna olduğu gibi yukarı gider (çağıran denetim kaydını da yazar).
    /// </summary>
    /// <remarks>
    /// <b>ÖLÇÜLEN HATA:</b> çıkarma sırasındaki tek bir istisna (aynı adın hem dosya hem klasör
    /// olması, CRC uyuşmazlığı, kilitli hedef, disk dolu) <c>UpsertRecordAsync</c>'e HİÇ
    /// ulaşmıyordu. O anda canlı içerik ZATEN kardeş klasöre taşınmış oluyor; kayıt "completed"
    /// kalıyor, <c>PreservedPath</c> boş görünüyor ve kullanıcı sitesinin neden boşaldığını ve
    /// eski içeriğin nerede olduğunu panelde HİÇBİR YERDE göremiyordu.
    /// <para>
    /// İptal (<see cref="OperationCanceledException"/>) buraya GELMEZ: orada kullanıcı işi kendisi
    /// durdurmuştur ve <c>CancellationToken.None</c> ile yazmak, iptali yok saymak olurdu — kaydın
    /// yazımı da bilerek <c>None</c> ile yapılır, çünkü token ZATEN iptal edilmiş olabilir ve o
    /// durumda dürüstlük notunu yazamamak en kötü sonuçtur.
    /// </para>
    /// </remarks>
    private async Task KaydiYarimIsaretleAsync(BackupRecordDto record, RestoreProgress durum, Exception exception)
    {
        durum.Warnings.Add(new BackupRestoreWarningDto
        {
            MessageKey = "backup.restore_aborted",
            Target = record.Name,
            Detail = exception is AppException app && !string.IsNullOrWhiteSpace(app.Details)
                ? $"{app.MessageKey}: {app.Details}"
                : exception.Message
        });

        record.Warnings = durum.Warnings;
        record.PreservedPath = durum.PreservedPath;
        record.Status = "restored_with_errors";

        try
        {
            await UpsertRecordAsync(record, CancellationToken.None);
        }
        catch (Exception yazmaHatasi)
        {
            // Kayıt yazılamadıysa ASIL hata gizlenmez; sebep günlüğe düşer.
            logger?.LogError(yazmaHatasi, "Yarim kalan geri yukleme kaydi yazilamadi: {Backup}", record.Id);
        }
    }

    private const int MaxEntriesPerListing = 500;

    /// <summary>
    /// Zip yedeğinin içinde dizin bazlı gezinme: verilen önekin hemen altındaki
    /// klasörler ve dosyalar döner. Girdi adları '\\' veya '/' ayırıcılı olabilir.
    /// </summary>
    public async Task<BackupEntriesDto> GetEntriesAsync(string backupId, string? prefix, CancellationToken cancellationToken = default)
    {
        var record = await FindZipRecordAsync(backupId, cancellationToken);
        var normalizedPrefix = NormalizeArchivePath(prefix ?? string.Empty);
        if (normalizedPrefix.Length > 0 && !normalizedPrefix.EndsWith('/'))
        {
            normalizedPrefix += "/";
        }

        var directories = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<BackupEntryDto>();
        var truncated = false;

        // GÖZATMA yalnız panelde KAYITLI parolayı kullanır. Parolayı bu GET ucuna sorgu dizesiyle
        // taşımak, onu IIS erişim günlüklerine ve tarayıcı geçmişine yazmak olurdu.
        using var content = await OpenContentAsync(record, null, cancellationToken);
        using var archive = ZipFile.OpenRead(content.FilePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = NormalizeArchivePath(entry.FullName);
            if (!name.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase) || name.Length == normalizedPrefix.Length)
            {
                continue;
            }

            var rest = name[normalizedPrefix.Length..];
            var slashIndex = rest.IndexOf('/');
            if (slashIndex >= 0)
            {
                directories.Add(rest[..slashIndex]);
            }
            else if (files.Count < MaxEntriesPerListing)
            {
                files.Add(new BackupEntryDto
                {
                    Name = rest,
                    Path = name,
                    IsDirectory = false,
                    Size = entry.Length
                });
            }
            else
            {
                truncated = true;
            }
        }

        var entries = directories
            .Select(directory => new BackupEntryDto
            {
                Name = directory,
                Path = normalizedPrefix + directory + "/",
                IsDirectory = true
            })
            .Concat(files.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
            .Take(MaxEntriesPerListing)
            .ToArray();

        return new BackupEntriesDto
        {
            BackupId = record.Id,
            Prefix = normalizedPrefix,
            Entries = entries,
            Truncated = truncated || entries.Length >= MaxEntriesPerListing
        };
    }

    /// <summary>
    /// Seçilen arşiv öğelerini geri yükler. Hedef verilmemişse sitenin orijinal kökü
    /// kullanılır: yedek includeBaseDirectory ile alındığından girdiler kök klasör adını
    /// içerir; bu yüzden çıkarma, kökün ÜST dizinine göre yapılır.
    /// </summary>
    public async Task<RestoreBackupItemsResultDto> RestoreItemsAsync(RestoreBackupItemsRequest request, CancellationToken cancellationToken = default)
    {
        var record = await FindZipRecordAsync(request.BackupId, cancellationToken);
        var items = request.Items
            .Select(NormalizeArchivePath)
            .Where(item => item.Length > 0)
            .ToArray();
        if (items.Length == 0)
        {
            throw new AppException("backup.items_required", 400, 400);
        }

        string targetRoot;
        if (!string.IsNullOrWhiteSpace(request.TargetPath))
        {
            targetRoot = IstenenHedefiDenetle(request.TargetPath);
        }
        else
        {
            {
                // AYRILMIŞ BİR ÇIKARMA KLASÖRÜ — HER ARŞİV TÜRÜ İÇİN.
                //
                // Eskiden burada koşulsuz `backup.target_path_required` fırlatılıyordu. Sonuç:
                // kapsamlı kullanıcı (müşteri/bayi) hedef yol VEREMEDİĞİ için — güvenlik gereği
                // o alan zorla boşaltılır — abonelik arşivinden seçerek geri yükleme kiracıda
                // HİÇBİR ZAMAN çalışmıyordu. Özellik vardı, kapısı kilitliydi.
                //
                // Tek bir "doğru" canlı hedef YOKTUR: abonelik arşivi birden çok sitenin
                // dosyalarını, veritabanı dökümlerini ve posta envanterini bir arada taşır.
                // Bu yüzden canlı içeriğin üzerine yazmaya ÇALIŞMIYORUZ. İçerik, panelin yedek kökü
                // altında tarih damgalı ayrı bir klasöre arşivdeki yapısıyla çıkarılır;
                // kullanıcı ne aldığını görür ve istediğini oradan taşır.
                //
                // ===========================================================================
                // SİTE ARŞİVİ DE ARTIK BURAYA GELİYOR — ÖLÇÜLEN ÇAPRAZ-KİRACI YÜZEYİ:
                //
                // Site arşivinde hedef, sitenin UYGULAMA KÖKÜNÜN ÜST DİZİNİ idi. Panelin
                // varsayılan düzeninde (SitesController.ResolveScopedPhysicalPath) kiracı
                // siteleri DÜZ olarak `C:\inetpub\wwwroot\<site>` altına açılır — yani üst
                // dizin `C:\inetpub\wwwroot`: BÜTÜN kiracıların ortak kabı VE IIS'in
                // "Default Web Site" belge kökü.
                //
                // Sonuç: kapsamlı bir müşteri (bu uç kiracıya AÇIKTIR) kendi yedeğinden
                // seçtiği dosyaları o ortak kaba yazdırabiliyordu — panel LocalSystem
                // olduğu için hiçbir ACL durdurmadan. Yazılan içerik varsayılan sitenin
                // belge kökünde HTTP ile servis edilebilir hâle geliyor, ve arşivdeki üst
                // düzey klasör adıyla aynı ada sahip bir komşu site (ör. "files") doğrudan
                // üzerine yazılabiliyordu. Kapsam denetimi doğruydu; DENETLENMEYEN ŞEY
                // HEDEFİN KENDİSİYDİ.
                //
                // Kiracının o üst dizini zaten GÖREMEDİĞİ ölçüldü (FileService sanal alanı
                // site kökleridir), yani oraya çıkarmanın kiracıya bir faydası da yoktu.
                // Ayrılmış klasör tek adres olunca kural tek yerde yazılır ve site/abonelik
                // yolları ayrışamaz.
                // ===========================================================================
                var preferences = await preferencesService.GetAsync(cancellationToken);
                var restoreRoot = BackupLayout.Ensure(preferences.DefaultBackupRoot, "restored").LocalDirectory;
                targetRoot = Path.Combine(restoreRoot, BuildRestoreFolderName(record));
            }
        }

        // Hedefin KENDİSİ bağlantı olamaz (gerekçe EnsureRestoreRootIsNotLink); segment yürüyüşü
        // kökten BAŞLADIĞI için kökü kendisi göremez.
        EnsureRestoreRootIsNotLink(targetRoot);
        Directory.CreateDirectory(targetRoot);
        var restored = 0;

        // Bağlantı denetiminin önbelleği — gerekçesi FileService.ExtractAsync'te yazılı (aynı
        // önek bir arşivde binlerce girdide tekrarlanır; arşiv girdisi bağlantı OLUŞTURAMAZ, yani
        // "temiz" damgalanmış bir önek iş sürerken bağlantıya dönüşemez).
        var denetlenenOnekler = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var content = await OpenContentAsync(record, request.EncryptionPassword, cancellationToken);
        using var archive = ZipFile.OpenRead(content.FilePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = NormalizeArchivePath(entry.FullName);
            if (name.Length == 0 || name.EndsWith('/'))
            {
                continue;
            }

            var matched = items.Any(item => item.EndsWith('/')
                ? name.StartsWith(item, StringComparison.OrdinalIgnoreCase)
                : string.Equals(name, item, StringComparison.OrdinalIgnoreCase));
            if (!matched)
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(targetRoot, name.Replace('/', Path.DirectorySeparatorChar)));
            // Zip-slip koruması: hedef her zaman kök altında kalmalı (tek doğru cevap: PathContainment).
            if (!PathContainment.IsInsideRoot(targetRoot, destination))
            {
                throw new AppException("backup.invalid_entry_path", 400, 400, name);
            }

            // ============================================================================
            // DİZGE DENETİMİ TEK BAŞINA YETMEZ — kaçış DİSKTE olur (ÖLÇÜLDÜ:
            // BackupGranularRestoreTests, gerçek kavşakla).
            //
            // Bu uç kapsamlı kullanıcıya AÇIKTIR (BackupController.RestoreItemsAsync; yalnız hedef
            // yol seçimi yöneticiye özeldir, kiracının hedefi kendi site köküdür). Kiracı kendi
            // belge kökünde bir KAVŞAK (junction) bırakırsa — ki bunu kurmak Windows'ta ayrıcalık
            // gerektirmez ve kendi sitesinde PHP/ASP çalıştıran her kiracı yapabilir — o kavşağın
            // adıyla başlayan her arşiv girdisi önek denetimini SORUNSUZ geçer ve yazma kavşağı
            // TAKİP EDER. Panel LocalSystem olduğu için hedefte hiçbir ACL durdurmaz.
            //
            // Girdinin ADI arşivden gelir, yani denetlenen değeri saldırgan belirler; kötü niyetli
            // bir arşiv üretmek bile GEREKMEZ: sıradan bir site yedeğindeki "wp-content/..." öneki,
            // yedek alındıktan SONRA "wp-content" kavşağa çevrildiğinde kaçış yoluna dönüşür.
            //
            // Kural TEK KAYNAKTAN gelir (SiteBuilderPathGuard): aynı yürüyüşü burada yeniden
            // yazmak, birini sıkılaştırıp diğerini unutmanın yoluydu.
            // ============================================================================
            try
            {
                SiteBuilderPathGuard.EnsureNoReparsePointOnPath(targetRoot, destination, denetlenenOnekler);
            }
            catch (AppException)
            {
                // ANAHTAR ÇEVRİLİR, YUTULMAZ. Ortak kapı kendi bağlamının anahtarını
                // (<c>sitebuilder.target_is_link</c>) atar; onu olduğu gibi geri yükleme ekranına
                // taşımak, kullanıcıya hiç kullanmadığı bir modülün ("site oluşturucu") hatasını
                // göstermek olurdu. Reddin SEBEBİ Detail'de duruyor, yani teşhis kaybolmuyor.
                throw new AppException(
                    "backup.invalid_entry_path",
                    400,
                    400,
                    $"{name} (hedef yolda baglanti/junction var: {destination})");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            restored++;
        }

        if (restored == 0)
        {
            throw new AppException("backup.items_not_found", 404, 404);
        }

        return new RestoreBackupItemsResultDto
        {
            RestoredCount = restored,
            TargetRoot = targetRoot
        };
    }

    private async Task<BackupRecordDto> FindZipRecordAsync(string backupId, CancellationToken cancellationToken)
    {
        var record = (await GetBackupsAsync(cancellationToken)).FirstOrDefault(item => item.Id == backupId)
            ?? throw new AppException("backup.not_found", 404, 404);

        if (!string.Equals(record.Format, "zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("backup.not_browsable", 400, 400);
        }

        // "backup.not_found" DEMEYİZ: kayıt duruyor, DOSYASI yok. İkisini aynı mesaja bağlamak,
        // kullanıcıyı felaket anında "yedek listede var ama bulunamadı" çelişkisiyle baş başa
        // bırakırdı. Ayrı anahtar sebebi (elle silinmiş/taşınmış dosya) açıkça söyler.
        if (!File.Exists(record.Path))
        {
            throw new AppException("backup.file_missing", 410, 410, $"{record.Name} -> {record.Path}");
        }

        return record;
    }

    private static string NormalizeArchivePath(string value) =>
        value.Replace('\\', '/').TrimStart('/');

    public async Task<BackupPruneResultDto> PruneAsync(int retentionDays, BackupPruneRemoteOptions? remote = null, CancellationToken cancellationToken = default)
    {
        if (retentionDays <= 0)
        {
            return new BackupPruneResultDto();
        }

        var records = (await GetBackupsAsync(cancellationToken)).ToArray();
        return await PruneRecordsAsync(records, retentionDays, keepCount: 0, remote, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<BackupPruneResultDto> PruneSubscriptionAsync(
        Guid userId,
        int retentionDays,
        int keepCount,
        BackupPruneRemoteOptions? remote = null,
        CancellationToken cancellationToken = default)
    {
        if (retentionDays <= 0 && keepCount <= 0)
        {
            return new BackupPruneResultDto();
        }

        var kimlik = userId.ToString("N");

        // KAPSAM DARALTILIR: bir aboneliğin saklama politikası YALNIZCA kendi yedeklerini
        // etkileyebilir. Bu daraltma olmadan bir müşterinin "3 gün sakla" ayarı, başka bir
        // müşterinin yedeklerini silen bir düğmeye dönerdi.
        var records = (await GetBackupsAsync(cancellationToken))
            .Where(record => string.Equals(record.Type, "subscription", StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.SubscriptionUserId, kimlik, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return await PruneRecordsAsync(records, retentionDays, keepCount, remote, cancellationToken);
    }

    /// <summary>
    /// Budamanın TEK uygulaması: kararı <see cref="BackupPrunePlanner"/> verir, burada yalnızca
    /// silme ve kayıt güncelleme yapılır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kararın ayrı olmasının sebebi: budama yalnızca tarihe bakarsa, yedekler HİÇ alınamazken de
    /// çalışır. Gecelik otomasyon her kaynağı ayrı try/catch ile yedekler ve hepsi patlasa bile
    /// buraya gelir: MariaDB 14 gün kapalı kalırsa her gece yedek başarısız olur, naif bir budama
    /// eskiyi silmeye devam eder ve 15. gün geri yüklenecek TEK bir yedek kalmaz.
    /// </para>
    /// <para>
    /// Artımsal zincirlerde ikinci bir tuzak vardır: zincirin ortasındaki (özellikle 0 numaralı)
    /// yedeği silmek, sonraki halkaları sessizce çöpe çevirir. Planlayıcı bu yüzden zinciri TEK
    /// bir geri yükleme noktası sayar ve ancak en yeni halkası da süresini doldurduğunda budar.
    /// </para>
    /// </remarks>
    private async Task<BackupPruneResultDto> PruneRecordsAsync(
        IReadOnlyCollection<BackupRecordDto> records,
        int retentionDays,
        int keepCount,
        BackupPruneRemoteOptions? remote,
        CancellationToken cancellationToken)
    {
        // retentionDays <= 0 → süreye göre silme yok; yalnızca sayı limiti çalışsın diye cutoff
        // ULAŞILAMAZ bir geçmişe alınır (hiçbir kayıt "süresi dolmuş" sayılmaz).
        var cutoff = retentionDays > 0 ? DateTime.UtcNow.AddDays(-retentionDays) : DateTime.MinValue;
        var silinecekler = BackupPrunePlanner.Select(records, cutoff, keepCount, IsUsableBackup);
        var removedIds = new HashSet<string>(StringComparer.Ordinal);

        // BAŞARISIZLIK ÇAĞIRANA DA GİDER. Eskiden yalnız günlüğe yazılıyordu; gece işi bunu
        // göremediği için durumunu "completed" bırakıyor ve özetinde yalnız başarıları sayıyordu
        // (bkz. BackupPruneResultDto'nun gerekçesi).
        var sonuclar = new BackupPruneResultDto();

        // ÇAĞIRANIN VERDİĞİ KİMLİK VARSAYILANDIR, ama abonelik yedeklerinin kimliği kendi
        // programından gelir: sunucu geneli budama (AutomationService) kapsamına abonelik
        // yedekleri de girer ve onların hedefi otomasyon ayarlarında TANIMLI DEĞİLDİR. Bu satır
        // olmadan o kayıtlar her budamada "uzak kopya silinemedi" işaretlenip birikirdi.
        var cozumleyici = await RemoteCredentials.ResolverForAsync(remote, cancellationToken);

        foreach (var record in silinecekler)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sonuc = await TryDeleteBackupFilesAsync(record, cozumleyici.For(record), cancellationToken);
            if (sonuc.Success)
            {
                removedIds.Add(record.Id);
            }
            else if (sonuc.LocalFailure is not null)
            {
                sonuclar.LocalDeleteFailed++;
                sonuclar.Failures.Add($"{record.Name}: local_delete_failed; {sonuc.LocalFailure}");
                logger?.LogWarning("Suresi dolmus yedek silinemedi, kayit korundu: {Sebep}", sonuc.LocalFailure);
            }
            else
            {
                // UZAK kopya silinemedi: kaydı çıkarmak o dosyayı YETİM bırakırdı (bir daha ne
                // görülür ne silinir). Kayıt işaretlenir ve bir sonraki budamada YENİDEN denenir.
                await MarkRemoteDeleteFailedAsync(record, sonuc.RemoteFailure!, cancellationToken);
                sonuclar.RemoteDeleteFailed++;
                sonuclar.Failures.Add($"{record.Name}: remote_delete_failed; {sonuc.RemoteFailure}");
                logger?.LogWarning(
                    "Yedegin uzak kopyasi silinemedi, kayit isaretlendi: {Ad}", record.Name);
            }
        }

        if (removedIds.Count > 0)
        {
            // ATOMİK: güncel listeden YALNIZ silinen ID'leri çıkar → eşzamanlı eklenen yeni kayıt (otomatik+elle
            // yedek aynı anda) KORUNUR. Dosya silme kilidin DIŞINDA yapıldı; burada yalnız kısa liste güncellemesi var.
            await fileStore.UpdateAsync(
                IndexPath,
                Array.Empty<BackupRecordDto>(),
                existing => existing.Where(item => !removedIds.Contains(item.Id)).ToArray(),
                cancellationToken);
        }

        sonuclar.Removed = removedIds.Count;
        return sonuclar;
    }

    /// <summary>Bir silme denemesinin İKİ AYRI sonucu: yerel dosya ve uzak kopya.</summary>
    /// <remarks>
    /// Tek bir "başarısız" değeri YETMEZ, çünkü iki başarısızlığın sonucu birbirinden farklıdır:
    /// yerel dosya silinemezse kayıt olduğu gibi korunur (bir sonraki denemede tekrar denenir);
    /// uzak kopya silinemezse yerel dosya GİTMİŞTİR ve kaydı da silmek uzak dosyayı YETİM
    /// bırakırdı — panel onu bir daha ne görebilir ne silebilir.
    /// </remarks>
    private sealed record BackupDeletionOutcome(string? LocalFailure, string? RemoteFailure)
    {
        public bool Success => LocalFailure is null && RemoteFailure is null;
    }

    /// <summary>
    /// Bir yedeğin uzak kopyasını, yerel dosyasını ve indeks yan kaydını siler.
    /// </summary>
    /// <remarks>
    /// <b>SIRA ÖNEMLİ: önce UZAK, sonra yerel.</b> Uzak kopyayı silmek kaydın adresine muhtaçtır;
    /// yereli önce silip uzakta takılmak, elde adresten başka bir şey bırakmazdı. Uzak silme
    /// başarısız olursa yerel dosya YİNE de silinir (kullanıcının istediği buydu) ama kayıt
    /// KORUNUR ve durumu <c>deleted_remote_failed</c> olur.
    /// </remarks>
    private async Task<BackupDeletionOutcome> TryDeleteBackupFilesAsync(
        BackupRecordDto record,
        BackupPruneRemoteOptions? remote,
        CancellationToken cancellationToken)
    {
        var uzakHata = await TryDeleteRemoteCopyAsync(record, remote, cancellationToken);

        try
        {
            if (!string.IsNullOrWhiteSpace(record.Path) && File.Exists(record.Path))
            {
                File.Delete(record.Path);
            }
            else if (!string.IsNullOrWhiteSpace(record.Path) && Directory.Exists(record.Path))
            {
                Directory.Delete(record.Path, recursive: true);
            }

            TryDeleteFile(fileStore.ResolvePath(IndexSidecarPath(record.Id)));
            return new BackupDeletionOutcome(null, uzakHata);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new BackupDeletionOutcome($"{record.Name}: {exception.Message}", uzakHata);
        }
    }

    /// <summary>
    /// Uzak kopyayı siler. Silinecek bir şey yoksa <c>null</c>; silinemediyse GERÇEK SEBEP.
    /// </summary>
    /// <remarks>
    /// Sebebi yutmak, kullanıcıya "sildim" deyip veriyi üçüncü taraf depoda bırakmaktır — hem
    /// gizlilik hem de faturalanan yer açısından yanlış. Bu yüzden "silemedim" hâli GÖRÜNÜR.
    /// </remarks>
    private async Task<string?> TryDeleteRemoteCopyAsync(
        BackupRecordDto record,
        BackupPruneRemoteOptions? remote,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(record.RemoteUri))
        {
            return null;
        }

        if (!SupportsRemoteDeletion(record.TargetType))
        {
            return $"Bu hedef turunun uzak kopyasi silinemiyor: {record.TargetType}";
        }

        if (remote is null)
        {
            // Kayıtlı bir uzak hedef yok (kullanıcı yerele döndü ya da hiç kurmadı): kimlik
            // olmadan silme denenemez. SESSİZ geçmek, uzakta yetim bırakmak demekti.
            return "Kayitli uzak hedef kimligi yok; uzak kopya silinemedi.";
        }

        return await TryDeleteRemoteBackupAsync(record, remote, cancellationToken);
    }

    /// <summary>
    /// Kayıt hâlâ GERÇEK bir geri yükleme noktası mı? Dosyası elle silinmiş bir kaydın "taze yedek
    /// var" sayılması, koruma varmış gibi görünüp eskileri sildirirdi — ölçmediğimiz şeye güvenmeyiz.
    /// </summary>
    private static bool IsUsableBackup(BackupRecordDto record)
    {
        // ARTIK SİLİNMİŞ SAYILAN KAYIT: kullanıcı sildi, yerel dosya gitti, yalnız uzak kopya
        // temizlenemediği için kayıt duruyor. Bunu "geri yükleme noktası" saymak, budamanın
        // kaynak korumasını yanıltır: panel taze bir yedek olduğunu sanıp GERÇEK eskileri silerdi.
        if (string.Equals(record.Status, RemoteDeleteFailedStatus, StringComparison.Ordinal))
        {
            return false;
        }

        // ÖLÇÜLÜP BOZUK BULUNMUŞ ARŞİV GERİ YÜKLEME NOKTASI DEĞİLDİR.
        //
        // Bu satırın asıl işi silmek değil KORUMAKTIR: budamanın "kaynak koruması" bir kaynağın
        // GÜNCEL ve kullanılabilir yedeği varsa eskilerini silmeye izin verir. Sıfır baytlık bir
        // arşiv "taze yedek" sayılsaydı, panel koruma varmış sanıp o sitenin GERÇEK eski
        // yedeklerini silerdi — bu depoda bir kez yaşanmış hata sınıfının ta kendisi.
        //
        // "unknown" (ölçülemedi: şifreli sarmalayıcı, kilitli dosya) BURAYA GİRMEZ; ölçmediğimiz
        // şeyi çöp saymak, sağlam yedeği yok saymak olurdu.
        if (string.Equals(record.IntegrityState, BackupIntegrity.Unsound, StringComparison.Ordinal))
        {
            return false;
        }

        // Uzak kopyanın SON ÖLÇÜMÜ "yok" dediyse ona da güvenilmez. Ölçüm yapılmadıysa (varsayılan)
        // eski davranış korunur — "ölçmedik" ile "yok" karıştırılmaz.
        if (!string.IsNullOrWhiteSpace(record.RemoteUri) && !record.RemoteMissing)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(record.Path) &&
               (File.Exists(record.Path) || Directory.Exists(record.Path));
    }

    /// <summary>
    /// Uzak kopyası silinemeyen kaydı İŞARETLER (silmez).
    /// </summary>
    /// <remarks>
    /// <b>Neden kayıt silinmiyor:</b> kayıt, panelin uzak dosyaya tutunduğu TEK saptır. Onu
    /// silmek dosyayı hedefte yetim bırakır — bir daha ne listelenebilir ne silinebilir, üstelik
    /// müşteri verisi üçüncü taraf depoda kalmaya devam eder. İşaretlenen kayıt hem kullanıcıya
    /// "yerelden silindi, uzaktan silinemedi" der hem de tekrar silmeye basıldığında/bir sonraki
    /// budamada uzak silmenin YENİDEN denenmesini sağlar.
    /// </remarks>
    private async Task MarkRemoteDeleteFailedAsync(
        BackupRecordDto record,
        string reason,
        CancellationToken cancellationToken)
    {
        await fileStore.UpdateAsync(
            IndexPath,
            Array.Empty<BackupRecordDto>(),
            existing =>
            {
                var list = existing.ToList();
                var index = list.FindIndex(item => item.Id == record.Id);
                if (index < 0)
                {
                    return existing;
                }

                var hedef = list[index];
                hedef.Status = RemoteDeleteFailedStatus;
                // Yerel dosya GERÇEKTEN silindi; yolu bırakmak "dosya orada" yanılgısı üretirdi.
                hedef.Path = string.Empty;
                hedef.Warnings.RemoveAll(uyari => uyari.MessageKey == "backup.remote_delete_failed");
                hedef.Warnings.Add(new BackupRestoreWarningDto
                {
                    MessageKey = "backup.remote_delete_failed",
                    Target = hedef.TargetType,
                    Detail = reason
                });

                list[index] = hedef;
                return list.ToArray();
            },
            cancellationToken);

        await WriteAuditAsync(
            "backup.remote_delete_failed",
            record.Name,
            // KİMLİK BİLGİSİ YOK: sebep metni sunucu yanıtıdır, jeton/parola içermez (üretildiği
            // yerlerde adresler SanitizeUri'den geçer).
            $"Yerel kopya silindi, uzak kopya silinemedi ({record.TargetType}): {reason}",
            await ResolveAuditSubjectAsync(record, cancellationToken),
            cancellationToken);
    }

    public async Task<BackupChainDto> GetChainAsync(string backupId, CancellationToken cancellationToken = default)
    {
        var records = await GetBackupsAsync(cancellationToken);
        if (records.All(item => item.Id != backupId))
        {
            throw new AppException("backup.not_found", 404, 404);
        }

        return BackupChainPlanner.Resolve(records, backupId, IsUsableBackup);
    }

    public async Task DeleteBackupAsync(string backupId, CancellationToken cancellationToken = default)
    {
        var records = await GetBackupsAsync(cancellationToken);
        var record = records.FirstOrDefault(item => item.Id == backupId)
            ?? throw new AppException("backup.not_found", 404, 404);

        // ZİNCİR TUZAĞI: bu yedek silinirse ondan SONRAKİ artımsal halkalar geri yüklenemez hâle
        // gelir — ama panelde "yedek" olarak görünmeye devam ederler. Kullanıcı bunu ancak felaket
        // anında öğrenir. Bu yüzden tek yedek silme isteği REDDEDİLİR; bağımlı halkaların adı
        // döner ve kullanıcı ne kaybedeceğini görerek "zinciri sil" der.
        var bagimlilar = BackupChainPlanner.GetDependents(records, record);
        if (bagimlilar.Count > 0)
        {
            throw new AppException(
                "backup.chain_has_dependents",
                409,
                409,
                $"Bu yedek silinirse su artimsal halkalar geri yuklenemez hale gelir: " +
                $"{string.Join(", ", bagimlilar.Select(item => item.Name))}");
        }

        // UZAK KOPYA DA SİLİNİR. Eskiden buraya `null` geçiliyordu, yani elle silme uzak kopyaya
        // HİÇ dokunmuyordu: panel kaydı gidiyor, dosya hedefte kalıyordu. Budama kayıtlar
        // üzerinden çalıştığı için o dosya YETİM oluyordu — panel onu bir daha ne görebiliyor ne
        // silebiliyordu. Kimlik, otomatik temizliğin okuduğu AYNI kaynaktan gelir.
        //
        // KİMLİK KAYIT BAŞINA ÇÖZÜLÜR: abonelik yedeği kendi programının hedefine yüklenir ve
        // ancak o programın kimliğiyle silinebilir (gerekçe: BackupRemoteCredentialResolver).
        var cozumleyici = await RemoteCredentials.ReadResolverAsync(cancellationToken);
        var sonuc = await TryDeleteBackupFilesAsync(record, cozumleyici.For(record), cancellationToken);

        if (sonuc.LocalFailure is not null)
        {
            // Yerel dosya kilitliyse kayıt KORUNUR (kör kayıt kaybı olmasın); kullanıcı gerçek nedeni görür.
            throw new AppException("backup.delete_failed", 409, 409, sonuc.LocalFailure);
        }

        if (sonuc.RemoteFailure is not null)
        {
            // "Sildim" demeyiz: yerel gitti, uzak duruyor. Kayıt işaretlenerek KORUNUR ki kullanıcı
            // durumu görsün ve tekrar deneyebilsin/elle temizleyebilsin.
            await MarkRemoteDeleteFailedAsync(record, sonuc.RemoteFailure, cancellationToken);
            throw new AppException("backup.remote_delete_failed", 409, 409, sonuc.RemoteFailure);
        }

        // ATOMİK: yalnız bu ID çıkarılır → eşzamanlı eklenen yeni kayıtlar (otomatik yedek) korunur.
        await fileStore.UpdateAsync(
            IndexPath,
            Array.Empty<BackupRecordDto>(),
            existing => existing.Where(item => item.Id != backupId).ToArray(),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<BackupDeleteResultDto> DeleteBackupChainAsync(string backupId, CancellationToken cancellationToken = default)
    {
        var records = await GetBackupsAsync(cancellationToken);
        var record = records.FirstOrDefault(item => item.Id == backupId)
            ?? throw new AppException("backup.not_found", 404, 404);

        // Plesk davranışı: bir halka silinince ondan sonraki bütün halkalar da silinir. Fark şu ki
        // panel bunu KENDİLİĞİNDEN yapmaz — kullanıcı bu ucu bilerek çağırır ve neyin gideceğini
        // DeleteBackupAsync'in verdiği hatadan zaten görmüştür.
        var hedefler = new List<BackupRecordDto> { record };
        hedefler.AddRange(BackupChainPlanner.GetDependents(records, record));

        // Zincir silme de UZAK kopyaları siler (tekil silmeyle aynı gerekçe: yetim bırakmamak) ve
        // kimliği HALKA BAŞINA çözer — zincirin halkaları farklı programlara ait olamaz ama
        // kimliği tek yerde çözmek, bir sonraki tür eklendiğinde de doğru kalmasını sağlar.
        var cozumleyici = await RemoteCredentials.ReadResolverAsync(cancellationToken);

        var silinen = new List<BackupRecordDto>();
        var kilitli = new List<string>();
        foreach (var hedef in hedefler)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sonuc = await TryDeleteBackupFilesAsync(hedef, cozumleyici.For(hedef), cancellationToken);

            if (sonuc.Success)
            {
                silinen.Add(hedef);
            }
            else if (sonuc.LocalFailure is not null)
            {
                kilitli.Add(sonuc.LocalFailure);
            }
            else
            {
                // Uzak kopya kaldı: kayıt işaretlenir, listeden ÇIKARILMAZ (yetim üretmemek için).
                await MarkRemoteDeleteFailedAsync(hedef, sonuc.RemoteFailure!, cancellationToken);
                kilitli.Add($"{hedef.Name}: {sonuc.RemoteFailure}");
            }
        }

        if (silinen.Count > 0)
        {
            var silinenIds = silinen.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            await fileStore.UpdateAsync(
                IndexPath,
                Array.Empty<BackupRecordDto>(),
                existing => existing.Where(item => !silinenIds.Contains(item.Id)).ToArray(),
                cancellationToken);
        }

        // Hiçbiri silinemediyse SESSİZ "tamam" DEMEYİZ: kullanıcı diskin boşaldığını sanmamalı.
        if (silinen.Count == 0 && kilitli.Count > 0)
        {
            throw new AppException("backup.delete_failed", 409, 409, string.Join(", ", kilitli));
        }

        return new BackupDeleteResultDto
        {
            DeletedIds = silinen.Select(item => item.Id).ToArray(),
            DeletedNames = silinen.Select(item => item.Name).ToArray()
        };
    }

    /// <summary>
    /// Off-site yedeğin UZAK kopyasını siler. Silindiyse <c>null</c>, silinemediyse SEBEP döner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Eskiden bu metot her hatayı YUTUYORDU</b> ("best-effort") ve çağıran kaydı yine de
    /// siliyordu. Sonuç: uzak dosya hedefte kalır, kaydı gider — yani bir daha ne görülebilir ne
    /// silinebilir bir YETİM. Yutma kaldırıldı; karar çağırana bırakıldı.
    /// </para>
    /// <para>
    /// Hedef türü listesi <see cref="SupportsRemoteDeletion"/> ile TEK kaynaktan gelir; buraya
    /// yeni bir <c>case</c> eklemeyi unutmak, o hedefte her silmenin yetim üretmesi demekti.
    /// </para>
    /// </remarks>
    private async Task<string?> TryDeleteRemoteBackupAsync(
        BackupRecordDto record,
        BackupPruneRemoteOptions remote,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(record.RemoteUri, UriKind.Absolute, out var uri))
        {
            return $"Kayittaki uzak adres cozulemedi: {record.RemoteUri}";
        }

        try
        {
            switch (record.TargetType)
            {
                case "ftp":
                    await DeleteFtpAsync(uri, remote, cancellationToken);
                    return null;
                case "s3" when !string.IsNullOrWhiteSpace(remote.UserName):
                    await DeleteS3SignedAsync(uri, remote, cancellationToken);
                    return null;
                case "webdav":
                case "http":
                    await DeleteHttpAsync(uri, remote, cancellationToken);
                    return null;
                case "dropbox":
                    await DeleteDropboxAsync(uri, remote, cancellationToken);
                    return null;
                case "onedrive":
                    await DeleteOneDriveAsync(uri, remote, cancellationToken);
                    return null;
                case "azure":
                    await DeleteAzureBlobAsync(uri, remote, cancellationToken);
                    return null;
                case "s3":
                    // Kimliksiz S3 (presigned URL): imza atılamaz. Sessizce "sildim" demek yerine
                    // sebebi söyleriz — kullanıcı ya erişim anahtarını girer ya da elle temizler.
                    return "S3 hedefinde silme icin erisim anahtari gerekir (imzali/presigned adres silinemez).";
                default:
                    return $"Bu hedef turunun uzak kopyasi silinemiyor: {record.TargetType}";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AppException exception)
        {
            return string.IsNullOrWhiteSpace(exception.Details) ? exception.MessageKey : exception.Details;
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }

    // S3 (SigV4 imzalı) DELETE. Boş gövde → EmptyPayloadSha256. Olmayan nesnede de 204 (idempotent).
    private async Task DeleteS3SignedAsync(Uri uri, BackupPruneRemoteOptions remote, CancellationToken cancellationToken)
    {
        var region = string.IsNullOrWhiteSpace(remote.Region) ? "us-east-1" : remote.Region;
        var (authorization, amzDate, contentSha) = S3RequestSigner.Sign(
            "DELETE", uri, remote.UserName, remote.Password, region, DateTimeOffset.UtcNow, S3RequestSigner.EmptyPayloadSha256);

        var client = CreateRemoteHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", contentSha);
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureRemoteDeleteSucceededAsync(response, "S3 DELETE", uri, cancellationToken);
    }

    // WebDAV/HTTP DELETE (basic auth).
    private async Task DeleteHttpAsync(Uri uri, BackupPruneRemoteOptions remote, CancellationToken cancellationToken)
    {
        var client = CreateRemoteHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        if (!string.IsNullOrWhiteSpace(remote.UserName))
        {
            var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{remote.UserName}:{remote.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureRemoteDeleteSucceededAsync(response, "WebDAV/HTTP DELETE", uri, cancellationToken);
    }

    /// <summary>
    /// HTTP tabanlı hedeflerde silme sonucunu değerlendirir: başarı ya da <c>404/410</c> →
    /// SİLİNMİŞ SAYILIR; geri kalan her yanıt HATADIR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ÖLÇÜLEN HATA:</b> S3 ve WebDAV/HTTP silme yolları yanıtı HİÇ okumuyordu — yalnızca
    /// <c>using var response = await client.SendAsync(...)</c> vardı ve metot orada bitiyordu.
    /// Yani <c>403 AccessDenied</c>, <c>401</c>, <c>405 Method Not Allowed</c>, hatta <c>500</c>
    /// SESSİZCE "sildim" sayılıyor, kayıt kapanıyor ve dosya hedefte YETİM kalıyordu: panel onu
    /// bir daha ne görebilir ne silebilirdi. Dropbox/OneDrive/Azure yolları bunu ta baştan doğru
    /// yapıyordu; aynı anlamın hedefe göre farklı davranması tam olarak bu hatanın kaynağıdır.
    /// </para>
    /// <para>
    /// <b>Neden 404/410 BAŞARI:</b> silmenin amacı "dosya orada olmasın"dır; zaten yoksa amaç
    /// gerçekleşmiştir. Eşik, uzak varlık ölçümüyle (<c>ProbeHttpAsync</c>) TEK ve AYNI kuraldır:
    /// yalnız <c>NotFound</c>/<c>Gone</c> "yok" demektir, 401/403/405 "bilinmiyor"dur ve
    /// bilinmiyorken "sildim" denmez.
    /// </para>
    /// </remarks>
    private static async Task EnsureRemoteDeleteSucceededAsync(
        HttpResponseMessage response,
        string operation,
        Uri uri,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode
            || response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new AppException(
            "backup.remote_delete_failed",
            (int)response.StatusCode,
            502,
            $"{operation} {SanitizeUri(uri)} -> {(int)response.StatusCode} {response.ReasonPhrase}\r\n{Truncate(body, 500)}");
    }

    /// <summary>
    /// FTP'deki kopyayı siler (DELE). Kimlik uzak seçeneklerden gelir; <c>RemoteUri</c>
    /// temizlenmiş (kimliksiz) saklandığı için adreste kullanıcı/parola yoktur.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ÖLÇÜLEN HATA — 550 ham hâliyle kullanıcıya basılıyordu.</b> Panel şunu diyordu:
    /// <c>"Yerel kopya silindi, uzak kopya silinemedi (ftp): The remote server returned an error:
    /// (550) File unavailable (e.g., file not found, no access)."</c> Bu cümlenin kendisi sorunun
    /// parçasıdır: RFC 959'da 550 HEM "dosya yok" HEM "erişim yok" demektir, .NET'in metni de
    /// ikisini birden sayar. Kullanıcı ne yapacağını bilemez — elle temizlemeye mi gitsin, yetki
    /// mi düzeltsin?
    /// </para>
    /// <para>
    /// <b>Ve daha kötüsü tutarsızlıktı:</b> "uzak kopya zaten yok" durumu HTTP/WebDAV'da (404/410)
    /// ve S3'te (idempotent DELETE) BAŞARI sayılırken FTP'de HATA sayılıyordu. Aynı anlam, üç
    /// farklı davranış: kullanıcının listesinde asla çözülemeyen bir uyarı kalıyordu, çünkü
    /// silinecek bir şey yoktu.
    /// </para>
    /// <para>
    /// <b>Çözüm TAHMİN DEĞİL ÖLÇÜM:</b> 550 alınca üst dizin listelenir (NLST) ve hedef adın orada
    /// olup olmadığına BAKILIR. Üç sonuç vardır ve ikiye indirmek yasaktır:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Listede YOK</b> → uzak kopya zaten gitmiş; silme BAŞARILIDIR (HTTP/S3 ile aynı
    /// anlam) ve kayıt kapanır.</item>
    /// <item><b>Listede VAR</b> → dosya duruyor, silinemedi: gerçek bir yetki/kilit sorunu.
    /// Kullanıcıya AÇIKÇA "duruyor" denir.</item>
    /// <item><b>Listeleme de başarısız</b> → "belirlenemedi". "Sildim" DENMEZ; kayıt işaretli
    /// kalır ve bir sonraki budamada yeniden denenir.</item>
    /// </list>
    /// </remarks>
    /// <summary>Bilinen yedek düzeninde boş klasörleri alttan güne kadar kaldırır; köke çıkmaz.</summary>
    private async Task BosGunKlasorunuKaldirAsync(Uri uri, NetworkCredential credential, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var klasor in BackupFtpCleanup.DirectoriesFor(uri))
            {
                var listeleme = await FtpCommands.ListDirectoryAsync(klasor, credential, cancellationToken);
                if (listeleme.Success && listeleme.Names.Any(name => name is not "." and not ".."))
                {
                    return;
                }
                if (!listeleme.Success && listeleme.Code != FtpCommandResult.FileUnavailable)
                {
                    return;
                }

                // Some servers return 550 for NLST of an empty directory. Do not infer
                // absence from that response: RMD itself succeeds only for an empty directory.
                // Never use recursive deletion; a file created after NLST must remain safe.
                var kaldirma = await FtpCommands.RemoveDirectoryAsync(klasor, credential, cancellationToken);
                if (!kaldirma.Success)
                {
                    logger?.LogInformation(
                        "Bos yedek klasoru kaldirilamadi (yedek dosyasi silindi): {Adres} kod={Kod}",
                        SanitizeUri(klasor), kaldirma.Code);
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Klasor temizligi budamayi DUSURMEZ: dosya gitti, kota geri kazanildi.
            logger?.LogInformation(exception, "Bos gun klasoru temizligi atlandi: {Adres}", SanitizeUri(uri));
        }
    }

    private async Task DeleteFtpAsync(Uri uri, BackupPruneRemoteOptions remote, CancellationToken cancellationToken)
    {
        var credential = new NetworkCredential(remote.UserName, remote.Password);
        var adres = SanitizeUri(uri);

        var silme = await FtpCommands.DeleteFileAsync(uri, credential, cancellationToken);
        if (silme.Success)
        {
            await BosGunKlasorunuKaldirAsync(uri, credential, cancellationToken);
            return;
        }

        if (silme.Code != FtpCommandResult.FileUnavailable)
        {
            // 550 DIŞINDAKİ hatalar (530 oturum açılamadı, 425 veri kanalı yok, 0 = hiç yanıt)
            // belirsiz DEĞİLDİR: dosyanın orada olup olmadığını söylemezler ama yoklamaya da izin
            // vermezler. Yoklama denenmeden durum "bilinmiyor" olarak raporlanır.
            throw new AppException(
                "backup.remote_delete_failed",
                silme.Code,
                502,
                $"ftp_delete={silme.Code}; remote_presence=unknown; probe=skipped; "
                + $"url={adres}; server={Truncate(silme.Description, 300)}");
        }

        var (durum, kanit) = await ProbeFtpPresenceAsync(uri, credential, cancellationToken);
        switch (durum)
        {
            case RemotePresence.Missing:
                // ZATEN YOK = BAŞARI. Çağıran null hata görür, kayıt normal yoldan kapanır.
                logger?.LogInformation(
                    "FTP uzak kopyasi zaten yoktu, silme basarili sayildi: {Adres}", adres);
                // Dosya başka bir turda silinmiş ama klasör kalmış olabilir; burada da denenir.
                await BosGunKlasorunuKaldirAsync(uri, credential, cancellationToken);
                return;

            case RemotePresence.Present:
                throw new AppException(
                    "backup.remote_delete_failed",
                    FtpCommandResult.FileUnavailable,
                    502,
                    $"ftp_delete=550; remote_presence=present; cause=permission_or_lock; "
                    + $"url={adres}; probe={kanit}; server={Truncate(silme.Description, 300)}");

            default:
                throw new AppException(
                    "backup.remote_delete_failed",
                    FtpCommandResult.FileUnavailable,
                    502,
                    $"ftp_delete=550; remote_presence=unknown; "
                    + $"url={adres}; probe={kanit}; server={Truncate(silme.Description, 300)}");
        }
    }

    /// <summary>
    /// FTP'de dosya GERÇEKTEN orada mı? Üst dizin listelenir ve hedef ad aranır.
    /// </summary>
    /// <returns>
    /// Ölçüm sonucu ve KANIT (dilden bağımsız etiket=değer; hata ayrıntısına aynen taşınır).
    /// </returns>
    /// <remarks>
    /// <b>Bilinen sınır — ölçemediğimiz hâl:</b> bazı sunucular BOŞ bir dizinin NLST'sine de
    /// <c>550</c> ("No files found") döner. O durumda listeleme başarısız sayılır ve sonuç
    /// <see cref="RemotePresence.Unreachable"/> olur, yani "belirlenemedi". Bu BİLEREK böyledir:
    /// boş dizin ile erişilemeyen dizin aynı yanıtı verdiğinden "dosya yok" demek ölçmediğimiz
    /// bir şeyi iddia etmek olurdu ve kaydı kapatarak dosyayı yetim bırakabilirdi.
    /// </remarks>
    private async Task<(RemotePresence, string)> ProbeFtpPresenceAsync(
        Uri uri,
        NetworkCredential credential,
        CancellationToken cancellationToken)
    {
        var (dizin, ad) = SplitFtpTarget(uri);
        if (ad.Length == 0)
        {
            return (RemotePresence.Unreachable, "list=skipped:no_file_name");
        }

        var liste = await FtpCommands.ListDirectoryAsync(dizin, credential, cancellationToken);
        if (!liste.Success)
        {
            // A deleted day directory makes NLST of every child fail with 550. Walk upwards
            // until a successful listing proves an ancestor absent. An existing ancestor,
            // denied listing, empty-directory 550 or network failure is never proof of absence.
            var ancestor = dizin;
            for (var depth = 0; depth < 32 && liste.Code == FtpCommandResult.FileUnavailable; depth++)
            {
                var (parent, name) = SplitFtpTarget(ancestor);
                if (name.Length == 0) { break; }
                var parentListing = await FtpCommands.ListDirectoryAsync(parent, credential, cancellationToken);
                if (parentListing.Success)
                {
                    return FtpListingContains(parentListing.Names, name)
                        ? (RemotePresence.Unreachable, $"list=550; ancestor=present:{name}")
                        : (RemotePresence.Missing, $"list=ok; ancestor=missing:{name}");
                }
                ancestor = parent;
                liste = parentListing;
            }
            return (RemotePresence.Unreachable, $"list={liste.Code}:{Truncate(liste.Description, 200)}");
        }

        return FtpListingContains(liste.Names, ad)
            ? (RemotePresence.Present, $"list=ok:entries={liste.Names.Count}")
            : (RemotePresence.Missing, $"list=ok:entries={liste.Names.Count}");
    }

    /// <summary>Adresi (üst dizin, dosya adı) ikilisine ayırır; ad yüzde kodlamasından çözülür.</summary>
    private static (Uri Directory, string Name) SplitFtpTarget(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var name = segments.Length == 0 ? string.Empty : Uri.UnescapeDataString(segments[^1]);

        var builder = new UriBuilder(uri)
        {
            Path = "/" + string.Join('/', segments.Take(Math.Max(0, segments.Length - 1))),
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        };

        return (builder.Uri, name);
    }

    /// <summary>
    /// Listede bu ad var mı?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Eşleşme BİLEREK GENİŞ:</b> karşılaştırma büyük/küçük harf duyarsızdır ve NLST'nin tam
    /// yol döndüren sunucularında (kimi sunucu <c>/yedek/2026-08-01/x.zip</c>, kimi düz
    /// <c>x.zip</c> verir) son segmente bakılır.
    /// </para>
    /// <para>
    /// <b>Asimetri kasıtlı:</b> yanlışlıkla "VAR" demek kaydı açık bırakır — kullanıcı uyarıyı
    /// görür, gerekirse elle temizler. Yanlışlıkla "YOK" demek ise kaydı KAPATIR ve dosya hedefte
    /// sonsuza kadar yetim kalır. İki hatanın bedeli eşit olmadığı için eşleşme geniş tutuldu.
    /// </para>
    /// </remarks>
    private static bool FtpListingContains(IReadOnlyCollection<string> names, string target) =>
        names.Any(entry => string.Equals(FtpEntryName(entry), target, StringComparison.OrdinalIgnoreCase));

    private static string FtpEntryName(string entry)
    {
        var kirpilmis = entry.Trim().TrimEnd('/');
        var index = kirpilmis.LastIndexOfAny(['/', '\\']);
        return index < 0 ? kirpilmis : kirpilmis[(index + 1)..];
    }

    // ATOMİK ekleme: arka plan otomatik yedek + kullanıcı elle yedek AYNI ANDA çalışırsa (UpdateAsync dosya-başına
    // kilit tutar) kayıt kaybolmaz. Eski Read+Write deseni "son yazan kazanır" → kayıp yedek kaydı veriyordu.
    private async Task AppendRecordAsync(BackupRecordDto record, CancellationToken cancellationToken)
    {
        await fileStore.UpdateAsync(
            IndexPath,
            Array.Empty<BackupRecordDto>(),
            existing => existing.Append(record).ToArray(),
            cancellationToken);
    }

    private async Task UpsertRecordAsync(BackupRecordDto record, CancellationToken cancellationToken)
    {
        await fileStore.UpdateAsync(
            IndexPath,
            Array.Empty<BackupRecordDto>(),
            existing =>
            {
                var list = existing.ToList();
                var index = list.FindIndex(item => item.Id == record.Id);
                if (index >= 0)
                {
                    list[index] = record;
                }
                else
                {
                    list.Add(record);
                }

                return list.ToArray();
            },
            cancellationToken);
    }

    /// <summary>
    /// Kayıtların GERÇEKTEN var olup olmadığını ölçer (uzlaştırma).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden ayrı bir uç:</b> uzak ölçüm kayıt başına bir ağ isteğidir; her listelemede yapmak
    /// paneli yavaşlatır ve hedefin hız sınırına takar. Yerel ölçüm ucuz olduğu için listede
    /// zaten yapılıyor (bkz. <see cref="GetBackupsAsync"/>); pahalı olan burada, kullanıcının
    /// bilerek bastığı düğmede toplandı.
    /// </para>
    /// <para>
    /// <b>EN TEHLİKELİ HATA — ulaşılamayan hedefi "yok" saymak.</b> Geçici bir ağ arızası,
    /// kimlik hatası ya da hız sınırı yanıtı "yedek silinmiş" olarak yorumlanırsa kullanıcının
    /// BÜTÜN yedek geçmişi kayıp damgası yer. Bu yüzden ölçümün üç sonucu vardır ve yalnızca
    /// KESİN "yok" (404/410, Dropbox <c>not_found</c>) kayıp sayılır; geri kalan her şey
    /// "ulaşılamadı" olarak RAPORLANIR ve kayda İŞLENMEZ.
    /// </para>
    /// <para>
    /// <b>Hiçbir kayıt SİLİNMEZ.</b> İşaretleme yeterlidir; silme kararı kullanıcınındır.
    /// </para>
    /// </remarks>
    public async Task<BackupVerificationResultDto> VerifyBackupsAsync(CancellationToken cancellationToken = default)
    {
        var records = await fileStore.ReadAsync(IndexPath, Array.Empty<BackupRecordDto>(), cancellationToken);
        // Kimlik KAYIT BAŞINA: abonelik yedeği kendi programının hedefindedir ve sunucu geneli
        // kimlikle yoklanırsa hedef reddeder — panel de sağlam duran off-site kopyayı
        // "ulaşılamadı" diye raporlardı.
        var cozumleyici = await RemoteCredentials.ReadResolverAsync(cancellationToken);
        var sonuc = new BackupVerificationResultDto { Checked = records.Length };

        // Yalnız DEĞİŞEN kayıtlar yazılır: her doğrulamada bütün indeksi yeniden yazmak,
        // eşzamanlı yedek kaydıyla yarışa girmek demektir.
        // Değer BackupPresence sabiti: "ulaşamadım" bu sözlüğe HİÇ girmez (kayda dokunulmaz),
        // böylece geçici arıza son KESİN ölçümün üzerine yazamaz.
        var uzakDurum = new Dictionary<string, string>(StringComparer.Ordinal);

        // ARŞİVİN İÇİ de aynı turda ölçülür ve aynı yazmada kaydedilir (ikinci bir tur, ikinci bir
        // yarış demekti).
        var butunlukDurumu = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (record.Status == RemoteDeleteFailedStatus && string.IsNullOrWhiteSpace(record.Path))
                sonuc.PreviouslyDeletedLocal++;

            if (IsLocalFileMissing(record))
            {
                sonuc.MissingLocal++;
                sonuc.Issues.Add(new BackupVerificationIssueDto
                {
                    BackupId = record.Id,
                    Name = record.Name,
                    Source = record.Source,
                    TargetType = record.TargetType,
                    MessageKey = "backup.verify.missing_local",
                    Detail = record.Path
                });
            }
            else
            {
                // ARŞİV GERÇEKTEN AÇILIYOR MU?
                //
                // ÖLÇÜLEN HATA: bu uç adı "doğrulama" olmasına rağmen arşivi HİÇ AÇMIYORDU;
                // yalnızca dosyanın var olup olmadığına bakıyordu. Sıfır baytlık ya da yarım
                // yazılmış bir arşiv "doğrulamadan geçti" damgası alıyordu — kullanıcıya
                // verilebilecek en pahalı güvence.
                //
                // MALİYET: ZIP'in merkezî dizini okunur, tek bayt açılmaz (gerekçe ve sınırları
                // BackupArchiveProbe'ta yazılı). Bu yüzden liste çizilişinde değil, kullanıcının
                // bilerek bastığı bu düğmede yapılır — uzak yoklamayla aynı maliyet disiplini.
                var olcum = BackupArchiveProbe.Measure(record.Path, record.Format);
                if (olcum.State != BackupIntegrity.Unknown)
                {
                    // "Ölçemedim" KAYDA YAZILMAZ: şifreli/kilitli dosyanın son kesin ölçümünü
                    // silmek, bildiğimizi unutmak olurdu (uzak ölçümdeki kararla aynı).
                    butunlukDurumu[record.Id] = olcum.State;
                }

                if (olcum.Unsound)
                {
                    sonuc.Unsound++;
                    sonuc.Issues.Add(new BackupVerificationIssueDto
                    {
                        BackupId = record.Id,
                        Name = record.Name,
                        Source = record.Source,
                        TargetType = record.TargetType,
                        MessageKey = "backup.verify.unsound",
                        Detail = olcum.Detail
                    });
                }
            }

            if (string.IsNullOrWhiteSpace(record.RemoteUri))
            {
                continue;
            }

            var kayitKimligi = cozumleyici.For(record);
            if (kayitKimligi is null)
            {
                // Kayıtlı hedef kimliği yok: ölçemedik. "Yok" DEMEK YASAK.
                //
                // DETAIL ETİKET-DEĞER, CÜMLE DEĞİL: bu depoda `Detail` ÇEVRİLMEYEN alandır (kendi
                // belgesi "Teknik ayrıntı. Çevrilmez; teşhis içindir." der) ve arayüz onu çevirinin
                // altına HAM basar. Buradaki eski Türkçe cümle ("Kayitli uzak hedef kimligi yok.")
                // İngilizce panelde İngilizce anahtarın altında Türkçe bir satır üretiyordu. Yeni
                // bir i18n anahtarı çözüm DEĞİL: çevirisi olmayan anahtar ekranda HAM görünür
                // (MessageKeyCoverageTests tam bunu kapatıyor). Aynı karar bu dosyada
                // ApplySkippedFiles'ta ve SiteHostingSettingsService'te de verildi.
                //
                // ÜÇ OLGUNUN HİÇBİRİ KAYBOLMADI: neyin eksik olduğu (uzak hedef kimliği),
                // ne yapılmadığı (yoklama atlandı) ve sonucun ne OLMADIĞI ("yok" değil, BİLİNMİYOR).
                // Hangi hedef olduğu zaten aynı kaydın TargetType alanında duruyor; oraya
                // RemoteUri'yi kopyalamak, alanın dilden bağımsızlığını kullanıcının alan adına
                // bağımlı hâle getirirdi.
                sonuc.Unreachable++;
                sonuc.Issues.Add(new BackupVerificationIssueDto
                {
                    BackupId = record.Id,
                    Name = record.Name,
                    Source = record.Source,
                    TargetType = record.TargetType,
                    MessageKey = "backup.verify.unreachable",
                    Detail = "remote_credentials=missing; probe=skipped; remote_presence=unknown"
                });
                continue;
            }

            var (presence, detail) = await ProbeRemoteAsync(record, kayitKimligi, cancellationToken);
            switch (presence)
            {
                case RemotePresence.Present:
                    uzakDurum[record.Id] = BackupPresence.Present;
                    break;

                case RemotePresence.Missing:
                    uzakDurum[record.Id] = BackupPresence.Missing;
                    sonuc.MissingRemote++;
                    sonuc.Issues.Add(new BackupVerificationIssueDto
                    {
                        BackupId = record.Id,
                        Name = record.Name,
                        Source = record.Source,
                        TargetType = record.TargetType,
                        MessageKey = "backup.verify.missing_remote",
                        Detail = record.RemoteUri
                    });
                    break;

                default:
                    // ULAŞILAMADI: kayda DOKUNULMAZ (önceki ölçüm neyse o kalır).
                    sonuc.Unreachable++;
                    sonuc.Issues.Add(new BackupVerificationIssueDto
                    {
                        BackupId = record.Id,
                        Name = record.Name,
                        Source = record.Source,
                        TargetType = record.TargetType,
                        MessageKey = "backup.verify.unreachable",
                        Detail = detail
                    });
                    break;
            }
        }

        if (uzakDurum.Count > 0 || butunlukDurumu.Count > 0)
        {
            var olcumAni = DateTime.UtcNow;
            await fileStore.UpdateAsync(
                IndexPath,
                Array.Empty<BackupRecordDto>(),
                existing =>
                {
                    foreach (var item in existing)
                    {
                        if (butunlukDurumu.TryGetValue(item.Id, out var butunluk))
                        {
                            item.IntegrityState = butunluk;
                        }

                        if (uzakDurum.TryGetValue(item.Id, out var durum))
                        {
                            // RemoteMissing KORUNUYOR: iki değerli eski alan hâlâ okunuyor
                            // olabilir. Üç değerli gerçeğin kaynağı artık RemoteState'tir ve
                            // ikisi TEK yerde, birlikte yazılır — ayrışmasınlar.
                            item.RemoteState = durum;
                            item.RemoteMissing = durum == BackupPresence.Missing;
                            item.RemoteVerifiedUtc = olcumAni;
                        }
                    }

                    return existing;
                },
                cancellationToken);
        }

        // YETİM TARAMASI SUNUCU GENELİ KİMLİKLE yapılır ve bu bilinçli: tarama, kayıtta OLMAYAN
        // dosyaları arar, yani hangi programa ait olduğu SORULAMAZ. Otomasyon hedefinin taban
        // adresi neyse orası taranır; abonelik hedefleri farklı bir adresteyse oradaki yetimler
        // bu listede GÖRÜNMEZ. Yanlış bir şey söylememek için sonuç zaten "taranamayan hedef"
        // ayrımını taşıyor (OrphanScanSkipped) ve hiçbir dosya bu yoldan SİLİNMEZ.
        await AppendOrphansAsync(sonuc, records, cozumleyici.Default, cancellationToken);

        // BOZUK ARŞİV DE DENETİM KAYDINA GİRER: "dosya duruyor ama içi açılmıyor", kayıp dosya
        // kadar ağır bir bulgudur ve izi olmadan geçmesi, geri yükleme gününde kimsenin
        // "ne zaman bozulmuş" sorusuna cevap bulamaması demektir.
        if (sonuc.MissingLocal > 0 || sonuc.MissingRemote > 0 || sonuc.Unsound > 0)
        {
            await WriteAuditAsync(
                "backup.verification",
                "backups",
                $"Yedek dogrulama: {sonuc.MissingLocal} yerel dosya, {sonuc.MissingRemote} uzak kopya bulunamadi, "
                + $"{sonuc.Unsound} arsiv acilamadi/bos "
                + $"({sonuc.Unreachable} kayit icin hedefe ulasilamadi).",
                // KONUSU YOK ve olamaz: doğrulama SUNUCU GENELİNDEKİ bütün kayıtları tarar, tek
                // bir aboneliğe ait değildir. Uç zaten yalnız yöneticiye açık; kapsamlı role
                // görünmemesi doğrudur.
                null,
                cancellationToken);
        }

        return sonuc;
    }

    /// <summary>
    /// TERS YETİM: hedefte duran ama panelde kaydı olmayan dosyalar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bunlar bugüne kadar hiç görünmüyordu: budama kayıtlar üzerinden çalışır, kaydı olmayan
    /// dosyaya kimse bakmazdı. Elle silinen (ve uzak kopyası kalan) her yedek böyle bir dosya
    /// bırakıyordu.
    /// </para>
    /// <para>
    /// <b>OTOMATİK SİLME YOK.</b> Hedefteki klasör kullanıcının başka amaçla da kullandığı bir
    /// yer olabilir; panele ait olmayan dosyayı silmek geri alınamaz bir veri kaybı olurdu.
    /// Liste gösterilir, karar kullanıcınındır.
    /// </para>
    /// </remarks>
    private async Task AppendOrphansAsync(
        BackupVerificationResultDto sonuc,
        IReadOnlyCollection<BackupRecordDto> records,
        BackupPruneRemoteOptions? remote,
        CancellationToken cancellationToken)
    {
        var (targetType, remoteUrl) = await RemoteCredentials.ReadTargetAsync(cancellationToken);
        if (remote is null || string.Equals(targetType, "local", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        BackupTarget hedef;
        try
        {
            hedef = ResolveTarget(targetType, remoteUrl, remote.UserName, remote.Password, remote.Region);
        }
        catch (AppException exception)
        {
            sonuc.OrphanScanSkipped.Add($"{targetType}: {exception.MessageKey}");
            return;
        }

        if (hedef.Uri is null)
        {
            return;
        }

        IReadOnlyCollection<string>? uzaktakiler;
        try
        {
            uzaktakiler = await ListRemoteObjectsAsync(hedef.Type, hedef.Uri, remote, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AppException exception)
        {
            sonuc.OrphanScanSkipped.Add($"{hedef.Type}: {exception.Details}");
            return;
        }
        catch (Exception exception)
        {
            sonuc.OrphanScanSkipped.Add($"{hedef.Type}: {exception.Message}");
            return;
        }

        if (uzaktakiler is null)
        {
            // Tek istekle özyinelemeli listeleme sunmayan hedef. BOŞ LİSTE DÖNMEK "yetim yok"
            // yalanı olurdu; taranmadığını söylüyoruz.
            sonuc.OrphanScanSkipped.Add(hedef.Type);
            return;
        }

        var bilinen = records
            .Where(record => !string.IsNullOrWhiteSpace(record.RemoteUri))
            .Select(record => Uri.TryCreate(record.RemoteUri, UriKind.Absolute, out var uri)
                ? Uri.UnescapeDataString(uri.AbsolutePath)
                : record.RemoteUri)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var yol in uzaktakiler)
        {
            if (bilinen.Contains(yol))
            {
                continue;
            }

            // Bağlantı testinin bıraktığı dosya PANELE AİTTİR ve her testte üzerine yazılır;
            // onu "yetim" diye raporlamak, listeyi her seferinde gürültüyle doldururdu.
            if (yol.Contains("/connection-test/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sonuc.Orphans.Add(new BackupOrphanDto { RemotePath = yol, TargetType = hedef.Type });
        }
    }

    public async Task<BackupRemoteTestResultDto> TestRemoteTargetAsync(BackupRemoteTestRequest request, CancellationToken cancellationToken = default)
    {
        var targetType = (request.TargetType ?? "local").Trim().ToLowerInvariant();
        if (targetType is "local" or "")
        {
            // Yerel hedef için ağ testi yok; her zaman "tamam".
            return new BackupRemoteTestResultDto { Success = true, Message = "backup.remote_test.local" };
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"adapanel-conn-test-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(
                tempFile,
                $"AdaPanel off-site backup connection test {DateTime.UtcNow:O}",
                cancellationToken);

            var target = ResolveTarget(targetType, request.RemoteUrl, request.RemoteUserName, request.RemotePassword, request.RemoteRegion);

            // DENETİM YÜKLEMEDEN ÖNCE. Bu uç verilen adrese HEMEN bir dosya yazar; sıra ters
            // olsaydı yasak hedefe zaten yazılmış olurdu ve "reddedildi" yanıtı yalan olurdu.
            await EnsureRemoteTargetAllowedAsync(target, cancellationToken);

            // TEST DE GERÇEK YEDEĞİN YOLUNU KULLANIR: <taban>/<gün>/connection-test/<sabit ad>.
            //
            // Neden gün klasörü açılıyor: gecelik yedeğin uzak hedefe inememesinin en olası sebebi
            // artık "yazma izni yok" değil, "ALT DİZİN AÇILAMIYOR"dur (FTP'de MKD, WebDAV'da MKCOL
            // izni ayrıdır; chroot'lu hesaplarda sık kapalıdır). Testi tabana düz bir dosya atarak
            // yapmak, bu kurulumlarda "bağlantı tamam" deyip geceleri sessizce başarısız olmak
            // demekti — kullanıcının kontrol düğmesine güvenip yanılmasının tam tarifi.
            //
            // Kategori BİLEREK "connection-test": gerçek kategori klasörlerini kirletmez, insan
            // baktığında ne olduğunu anlar. Dosya adı sabittir → her test öncekinin üzerine yazar,
            // hedefte birikme olmaz.
            var testPlacement = BackupLayout.RemoteOnly("connection-test", DateTime.Now);
            await UploadToRemoteAsync(tempFile, "adapanel-connection-test.txt", target, testPlacement, cancellationToken);
            return new BackupRemoteTestResultDto { Success = true, Message = "backup.remote_test.ok" };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AppException exception)
        {
            return new BackupRemoteTestResultDto { Success = false, Message = exception.MessageKey, Details = exception.Details };
        }
        catch (Exception exception)
        {
            return new BackupRemoteTestResultDto { Success = false, Message = "backup.remote_test.failed", Details = exception.Message };
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
                // geçici dosya temizliği best-effort
            }
        }
    }

    /// <summary>
    /// Dosyayı uzak hedefe yükler ve KAYDA yazılacak (kimliksiz) uzak adresi döndürür.
    /// </summary>
    /// <param name="placement">
    /// Yedeğin yeri; uzak hedefte taban adresin altına <c>&lt;gün&gt;/&lt;kategori&gt;</c> olarak
    /// açılır. ZORUNLUDUR — "yer verilmezse köke yaz" gibi bir yol bırakmak, düzeltilen hatanın
    /// (uzakta düz yığın) sessizce geri gelmesine açık kapı olurdu. Yedeğe ait olmayan yüklemeler
    /// de kendi yerini verir (bkz. bağlantı testi).
    /// </param>
    /// <remarks>
    /// Protokoller dizin konusunda birbirine BENZEMEZ, bu yüzden burada üç ayrı karar var:
    /// <list type="bullet">
    /// <item><b>FTP</b>: ara dizinler kendiliğinden oluşmaz, her seviye için MKD gerekir.</item>
    /// <item><b>WebDAV</b>: ara koleksiyonlar için MKCOL gerekir; yoksa PUT 409 Conflict döner.</item>
    /// <item><b>S3</b>: DİZİN KAVRAMI YOKTUR. "Klasör" yalnızca anahtarın önekidir; MKD/MKCOL
    /// denemek anlamsız (ve S3 API'sinde karşılığı olmayan) bir istektir. Anahtara önek eklemek
    /// yeterlidir — SigV4 imzası anahtarın tamamını kapsar (bkz. <see cref="S3RequestSigner"/>,
    /// kanonik yol segment segment kodlanır), aksi hâlde istek imza uyuşmazlığıyla reddedilirdi.</item>
    /// </list>
    /// </remarks>
    private async Task<string> UploadToRemoteAsync(
        string localPath,
        string fileName,
        BackupTarget target,
        BackupPlacement placement,
        CancellationToken cancellationToken)
    {
        if (target.Type == "local")
        {
            return string.Empty;
        }

        if (target.Uri is null)
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        try
        {
            if (target.Type == "ftp")
            {
                var ftpUri = AppendRemotePath(target.Uri, placement, fileName).Uri;
                await UploadFtpAsync(localPath, ftpUri, target, cancellationToken);
                return SanitizeUri(ftpUri);
            }

            // Erişim anahtarı verilmiş S3: SigV4 imzalı PUT (gerçek S3 / MinIO / R2 / Wasabi).
            // Anahtarsız S3: presigned URL'e düz PUT (geri uyumluluk).
            if (target.Type == "s3" && !string.IsNullOrWhiteSpace(target.UserName))
            {
                // MKD/MKCOL YOK: S3'te dizin diye bir nesne yoktur, önek yeter.
                var s3Uri = AppendRemotePath(target.Uri, placement, fileName).Uri;
                await UploadS3SignedAsync(localPath, s3Uri, target, cancellationToken);
                return SanitizeUri(s3Uri);
            }

            if (target.Type == "webdav")
            {
                var (webdavUri, klasorler) = AppendRemotePath(target.Uri, placement, fileName);
                var koleksiyonSorunu = await EnsureWebDavCollectionsAsync(target.Uri, klasorler, target, cancellationToken);
                await UploadHttpPutAsync(localPath, webdavUri, target, cancellationToken, koleksiyonSorunu);
                return SanitizeUri(webdavUri);
            }

            // BULUT HEDEFLERİ: üçünde de klasör AÇILMAZ (gerekçe BackupService.CloudTargets.cs'te);
            // gün/kategori/kaynak düzeni yine de adresin içinde AYNEN taşınır.
            if (target.Type == "dropbox")
            {
                var dropboxUri = AppendRemotePath(target.Uri, placement, fileName).Uri;
                await UploadDropboxAsync(localPath, dropboxUri, target, cancellationToken);
                return SanitizeUri(dropboxUri);
            }

            if (target.Type == "onedrive")
            {
                var oneDriveUri = AppendRemotePath(target.Uri, placement, fileName).Uri;
                await UploadOneDriveAsync(localPath, oneDriveUri, target, cancellationToken);
                return SanitizeUri(oneDriveUri);
            }

            if (target.Type == "azure")
            {
                // AppendRemotePath taban adresin SORGUSUNU korur; Azure'da SAS jetonu oradadır ve
                // yükleme için gereklidir. SanitizeUri onu KAYDA yazarken düşürür — imza panele
                // ve günlüklere sızmaz (S3 X-Amz-Signature ile aynı sınıf).
                var azureUri = AppendRemotePath(target.Uri, placement, fileName).Uri;
                await UploadAzureBlobAsync(localPath, azureUri, target, cancellationToken);
                return SanitizeUri(azureUri);
            }

            if (target.Type is "s3" or "http")
            {
                // ANAHTARSIZ S3 ve düz HTTP hedefi: adres OLDUĞU GİBİ kullanılır.
                //
                // Ölçülen davranış: bu iki yol kullanıcının verdiği URL'e (imzalı/tek kullanımlık
                // presigned URL ya da yükleme uç noktası) tek bir PUT atar; dosya adı bile
                // eklenmez. Buraya gün/kategori öneki eklemek presigned URL'in imzasını geçersiz
                // kılar (imza tam yolu kapsar) ve "/upload" gibi bir uç noktayı 404'e düşürür —
                // yani ÇALIŞAN kurulumları bozardı. Klasör düzeni bu iki hedefte hedefin
                // sözleşmesi gereği YOKTUR; adres neyi gösteriyorsa yedek oraya gider.
                await UploadHttpPutAsync(localPath, target.Uri, target, cancellationToken);
                return SanitizeUri(target.Uri);
            }
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AppException("backup.remote_upload_failed", 502, 500, BuildDiagnosticDetails(exception));
        }

        throw new AppException("backup.remote_target_invalid", 400, 400);
    }

    private async Task UploadS3SignedAsync(
        string localPath,
        Uri uri,
        BackupTarget target,
        CancellationToken cancellationToken)
    {
        if (uri.Scheme is not ("http" or "https"))
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        var region = string.IsNullOrWhiteSpace(target.Region) ? "us-east-1" : target.Region;
        var (authorization, amzDate, contentSha) = S3RequestSigner.Sign(
            "PUT",
            uri,
            target.UserName,
            target.Password,
            region,
            DateTimeOffset.UtcNow,
            S3RequestSigner.UnsignedPayload);

        var client = CreateRemoteHttpClient();
        // SINIRLI zaman aşımı ŞART: bu yükleme otomatik yedeklemede GLOBAL KİLİDİN İÇİNDE çalışıyor
        // ve aynı kilidi giriş akışı (brute-force denetimi) da alıyor. Yarı açık kalmış bir TCP
        // bağlantısında (NAT zaman aşımı, uzak uç donması) sonsuz bekleme, kilidi kalıcı olarak
        // tutar ve o andan sonra PANELE KİMSE GİREMEZ; tek çare servisi yeniden başlatmaktır.
        // 2 saat, yavaş hatta büyük bir yedeği yüklemeye yeterken sonsuz askıda kalmayı engeller.
        client.Timeout = RemoteUploadTimeout;

        await using var source = File.OpenRead(localPath);
        using var content = new StreamContent(source);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", contentSha);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new AppException(
                "backup.remote_upload_failed",
                (int)response.StatusCode,
                502,
                $"Url={SanitizeUri(uri)}\r\nStatusCode={(int)response.StatusCode} {response.ReasonPhrase}\r\n{Truncate(body, 2000)}");
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    /// <param name="directoryFailure">
    /// Klasör hazırlığında (MKCOL) alınan açıklanamayan yanıt. Yükleme BAŞARILIYSA hiç kullanılmaz
    /// (klasör gerçekten vardı); başarısızsa kök sebep olarak hata ayrıntısına eklenir.
    /// </param>
    private async Task UploadHttpPutAsync(
        string localPath,
        Uri uri,
        BackupTarget target,
        CancellationToken cancellationToken,
        string? directoryFailure = null)
    {
        if (uri.Scheme is not ("http" or "https"))
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        var client = CreateRemoteHttpClient();
        // SINIRLI zaman aşımı ŞART: bu yükleme otomatik yedeklemede GLOBAL KİLİDİN İÇİNDE çalışıyor
        // ve aynı kilidi giriş akışı (brute-force denetimi) da alıyor. Yarı açık kalmış bir TCP
        // bağlantısında (NAT zaman aşımı, uzak uç donması) sonsuz bekleme, kilidi kalıcı olarak
        // tutar ve o andan sonra PANELE KİMSE GİREMEZ; tek çare servisi yeniden başlatmaktır.
        // 2 saat, yavaş hatta büyük bir yedeği yüklemeye yeterken sonsuz askıda kalmayı engeller.
        client.Timeout = RemoteUploadTimeout;

        await using var source = File.OpenRead(localPath);
        using var content = new StreamContent(source);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = content
        };

        var credential = ResolveCredential(target, uri);
        if (credential is not null)
        {
            var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{credential.UserName}:{credential.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new AppException(
                "backup.remote_upload_failed",
                (int)response.StatusCode,
                502,
                $"Url={SanitizeUri(uri)}\r\nStatusCode={(int)response.StatusCode} {response.ReasonPhrase}"
                + (directoryFailure is null ? string.Empty : $"\r\nKlasor hazirligi: {directoryFailure}"));
        }
    }

#pragma warning disable SYSLIB0014
    private static async Task UploadFtpAsync(
        string localPath,
        Uri uri,
        BackupTarget target,
        CancellationToken cancellationToken)
    {
        if (uri.Scheme != "ftp")
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        var credential = ResolveCredential(target, uri)
            ?? throw new AppException("backup.remote_target_invalid", 400, 400);

        await BackupFtpRetry.RunAsync(async token =>
        {
          var dizinSorunu = await EnsureFtpDirectoriesAsync(uri, credential, token);
          try
          {
            await StoreFtpFileAsync(localPath, uri, credential, token);
          }
          catch (WebException exception) when (BackupFtpRetry.IsTransient(exception)) { throw; }
          catch (AppException exception) when (dizinSorunu is not null)
          {
            // Yükleme BAŞARISIZ ve klasör hazırlığında da bir yanıt almışız: kök sebep büyük
            // olasılıkla o yanıttır ("550 Permission denied"). Yutulmaz, mesaja eklenir.
            throw new AppException(
                exception.MessageKey,
                exception.Code,
                exception.HttpStatusCode,
                $"{exception.Details}\r\nKlasor hazirligi: {dizinSorunu}".Trim());
          }
          catch (Exception exception) when (dizinSorunu is not null && !token.IsCancellationRequested)
          {
            throw new AppException(
                "backup.remote_upload_failed",
                502,
                502,
                $"{exception.Message}\r\nKlasor hazirligi: {dizinSorunu}");
          }
        }, cancellationToken);
    }

    private static async Task StoreFtpFileAsync(
        string localPath,
        Uri uri,
        NetworkCredential credential,
        CancellationToken cancellationToken)
    {
        var request = (FtpWebRequest)WebRequest.Create(uri);
        request.Method = WebRequestMethods.Ftp.UploadFile;
        request.UseBinary = true;
        request.UsePassive = true;
        request.KeepAlive = false;
        request.Credentials = credential;
        request.ContentLength = new FileInfo(localPath).Length;
        // FtpWebRequest'in VARSAYILANI sonsuzdur. Bu yükleme global kilidin içinde çalıştığı ve aynı
        // kilidi giriş akışı da aldığı için, donmuş bir FTP bağlantısı paneli kalıcı olarak kilitler.
        // Timeout bağlantı/komut aşamasını, ReadWriteTimeout ise veri akışının durmasını sınırlar —
        // ikincisi olmadan yarı açık bir bağlantıda aktarım sonsuza kadar askıda kalır.
        request.Timeout = (int)RemoteUploadTimeout.TotalMilliseconds;
        request.ReadWriteTimeout = (int)RemoteUploadStallTimeout.TotalMilliseconds;
        // İptal edildiğinde soketi gerçekten kapat: CopyToAsync'in token'ı FtpWebRequest'i durdurmaz.
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(RemoteUploadTimeout);
        using var deadline = new FtpRequestDeadline(request, RemoteUploadStallTimeout, total.Token);
        try
        {
            await using (var source = File.OpenRead(localPath))
            await using (var destination = await request.GetRequestStreamAsync())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
                    deadline.Reset(RemoteUploadStallTimeout);
                }
            }

            using var response = (FtpWebResponse)await request.GetResponseAsync();
            if (response.StatusCode is not (FtpStatusCode.ClosingData or FtpStatusCode.CommandOK))
            {
                throw new AppException(
                    "backup.remote_upload_failed",
                    (int)response.StatusCode,
                    502,
                    $"Url={SanitizeUri(uri)}\r\nStatusCode={(int)response.StatusCode} {response.StatusDescription?.Trim()}");
            }
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (total.IsCancellationRequested)
                throw new WebException("FTP transfer timed out.", WebExceptionStatus.Timeout);
            deadline.ThrowIfExpired();
            throw;
        }
    }

    /// <summary>
    /// FTP'de dosyanın bulunduğu dizine kadar HER seviyeyi MKD ile açar (ara dizinler
    /// kendiliğinden oluşmaz; STOR yolun tamamını hazır bekler).
    /// </summary>
    /// <returns>
    /// "Zaten var" ile açıklanamayan sunucu yanıtlarının özeti; her şey yolundaysa <c>null</c>.
    /// Dönen metin bir HATA DEĞİLDİR — yükleme başarısız olursa ayrıntıya eklenecek kanıttır
    /// (gerekçe: yukarıdaki "UZAK HEDEFTE KLASÖR AÇMA" bölümü).
    /// </returns>
    private static async Task<string?> EnsureFtpDirectoriesAsync(Uri uri, NetworkCredential credential, CancellationToken cancellationToken)
    {
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        if (segments.Length <= 1)
        {
            return null;
        }

        // Taban dizin de dahil, kökten dosyanın dizinine kadar HER seviye denenir: gün/kategori
        // klasörleri taban dizinin altına iner ve ara seviye yokken MKD başarısız olur.
        var path = "/";
        var sorunlar = new List<string>();

        for (var index = 0; index < segments.Length - 1; index++)
        {
            path = CombineUriPath(path, segments[index]);
            var builder = new UriBuilder(uri)
            {
                Path = path,
                Query = string.Empty,
                Fragment = string.Empty,
                UserName = string.Empty,
                Password = string.Empty
            };

            var request = (FtpWebRequest)WebRequest.Create(builder.Uri);
            request.Method = WebRequestMethods.Ftp.MakeDirectory;
            request.Credentials = credential;
            request.UsePassive = true;
            request.KeepAlive = false;
            // Klasör açma da sonsuz beklememeli: bu akış otomatik yedeklemede GLOBAL KİLİDİN
            // içindedir (bkz. yükleme zaman aşımı gerekçesi).
            request.Timeout = (int)RemoteUploadStallTimeout.TotalMilliseconds;
            using var deadline = new FtpRequestDeadline(request, RemoteUploadStallTimeout, cancellationToken);

            try
            {
                using var response = (FtpWebResponse)await request.GetResponseAsync();
            }
            catch (WebException exception)
            {
                deadline.ThrowIfExpired();
                if (BackupFtpRetry.IsTransient(exception)) { throw; }
                using var response = exception.Response as FtpWebResponse;
                var kod = response is null ? "-" : ((int)response.StatusCode).ToString();
                var aciklama = (response?.StatusDescription ?? exception.Message).Trim();

                // "Zaten var" (çoğu sunucuda 550, bazılarında 521) ile "izin yok" AYNI kodu
                // kullanır; hangisi olduğu buradan bilinemez. Yanıt kaydedilir, karar yüklemenin
                // sonucuna bırakılır: yükleme geçerse dizin vardı, geçmezse bu satır kök sebeptir.
                sorunlar.Add($"MKD {SanitizeUri(builder.Uri)} -> {kod} {aciklama}");
            }
        }

        return sorunlar.Count == 0 ? null : string.Join("; ", sorunlar);
    }
#pragma warning restore SYSLIB0014

    private static BackupTarget ResolveTarget(string? targetType, string? remoteUrl, string? remoteUserName, string? remotePassword, string? remoteRegion = null)
    {
        var normalizedType = string.IsNullOrWhiteSpace(targetType)
            ? "local"
            : targetType.Trim().ToLowerInvariant();

        if (normalizedType is "none" or "")
        {
            normalizedType = "local";
        }

        if (normalizedType == "local")
        {
            return new BackupTarget("local", null, string.Empty, string.Empty, string.Empty);
        }

        if (normalizedType is "s3-compatible")
        {
            normalizedType = "s3";
        }

        // Eş adlar TEK yerde çözülür: kullanıcı "azure-blob" ya da "one-drive" yazdığında hedefin
        // sessizce "geçersiz" sayılıp yerele düşmesi, off-site kopyanın hiç alınmaması demekti.
        normalizedType = normalizedType switch
        {
            "azure-blob" or "azureblob" or "azure-storage" => "azure",
            "one-drive" or "msgraph" or "graph" => "onedrive",
            _ => normalizedType
        };

        // "drive" SESSİZCE "webdav"e çevriliyordu. Google Drive WebDAV KONUŞMAZ: yedek hiçbir zaman
        // karşıya gitmez, ama panel gitmiş gibi davranır ve kullanıcı off-site kopyası olduğunu sanır.
        // Sessizce yanlış hedefe yazmak en kötü seçenektir; desteklenmeyen hedef AÇIKÇA reddedilir.
        // (Gerçek Google Drive desteği ayrı bir iştir: OAuth 2.0 + Drive REST API, WebDAV değil.
        //  Dropbox ve OneDrive ARTIK kendi API'leriyle destekleniyor; Drive hâlâ desteklenmiyor ve
        //  bu yüzden burada AÇIKÇA reddedilmeye devam ediyor.)
        if (normalizedType is "drive" or "gdrive" or "google-drive" or "googledrive")
        {
            throw new AppException("backup.remote_target_unsupported", 400, 400, normalizedType);
        }

        if (!RemoteTargetTypes.Contains(normalizedType, StringComparer.Ordinal))
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        var remoteUrlText = remoteUrl?.Trim() ?? string.Empty;

        // Dropbox/OneDrive'da "URL" diye bir kavram yoktur; kullanıcı KLASÖR YOLU yazar
        // ("/yedekler"). Şemayı burada ekliyoruz ki geri kalan bütün akış (yer kararı, adres
        // temizliği, silme) tek ve aynı Uri mekanizmasıyla çalışsın.
        if (IsOAuthTarget(normalizedType))
        {
            remoteUrlText = NormalizeCloudFolderUrl(normalizedType, remoteUrlText);
        }

        if (!Uri.TryCreate(remoteUrlText, UriKind.Absolute, out var uri))
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        if (normalizedType == "ftp" && uri.Scheme != "ftp")
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        if ((normalizedType is "s3" or "webdav" or "http" or "azure") && uri.Scheme is not ("http" or "https"))
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        // Şema hedef türüyle EŞLEŞMELİ: "dropbox" seçip WebDAV adresi vermek, yedeğin hiç
        // gitmediği ama panelin gittiğini sandığı sınıfın ta kendisidir.
        if (IsOAuthTarget(normalizedType) && !string.Equals(uri.Scheme, normalizedType, StringComparison.Ordinal))
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        // Jetonsuz Dropbox/OneDrive hedefi ANLAMSIZDIR: istek 401 ile döner ve kullanıcı bunu
        // ancak gece yedeği başarısız olunca görürdü. Hedef kaydedilirken reddedilir.
        if (IsOAuthTarget(normalizedType) && string.IsNullOrWhiteSpace(remotePassword))
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        // Azure: ya hesap anahtarı ya da adreste SAS olmalı. İkisi de yoksa istek anonim gider ve
        // her zaman 404/403 alır.
        if (normalizedType == "azure"
            && string.IsNullOrWhiteSpace(remoteUserName)
            && string.IsNullOrWhiteSpace(uri.Query))
        {
            throw new AppException("backup.remote_target_invalid", 400, 400);
        }

        return new BackupTarget(
            normalizedType,
            uri,
            remoteUserName?.Trim() ?? string.Empty,
            remotePassword ?? string.Empty,
            remoteRegion?.Trim() ?? string.Empty);
    }

    private static NetworkCredential? ResolveCredential(BackupTarget target, Uri uri)
    {
        if (!string.IsNullOrWhiteSpace(target.UserName))
        {
            return new NetworkCredential(target.UserName, target.Password);
        }

        if (string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            return null;
        }

        var parts = uri.UserInfo.Split(':', 2);
        return new NetworkCredential(
            Uri.UnescapeDataString(parts[0]),
            parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
    }

    /// <summary>
    /// Uzak adresi tamamlar: <c>&lt;taban&gt;/&lt;gün&gt;/&lt;kategori&gt;/&lt;dosya adı&gt;</c>.
    /// </summary>
    /// <returns>
    /// Yüklenecek tam adres ve TABAN ADRESİN ALTINA eklenen klasör seviyeleri — FTP'de MKD,
    /// WebDAV'da MKCOL bu seviyeler için gerekir (S3'te hiçbiri gerekmez).
    /// </returns>
    /// <summary>
    /// Uzak taban adresin son parçası, kullanıcının BİLEREK sabitlediği tek bir yedek DOSYASI mı?
    /// </summary>
    /// <remarks>
    /// ÖLÇÜLDÜ-BOZUK: eski koşul <c>Path.HasExtension(sonParca)</c> idi — ve
    /// <c>Path.HasExtension("musteri.com")</c> <b>true</b> döner. Uzak yedek klasörünü alan adıyla
    /// adlandırmak (<c>ftp://sunucu/yedekler/musteri.com</c>, <c>https://s3…/kova/site.com</c>)
    /// Windows barındırmada en yaygın düzendir; o kurulumlarda kısa devre YANLIŞLIKLA açılıyordu
    /// ve <see cref="BackupPlacement.RemoteFolders"/> (gün/kategori/kaynak) İLE dosya adının ikisi
    /// birden düşüyordu.
    ///
    /// Sonucu sessizdi: her gecelik yedek uzakta AYNI adrese yazılır. S3'te PUT 200 döner, panel
    /// her kayda "uzağa senkronlandı" der, uzakta tek nesne vardır; üstelik bütün kayıtların
    /// <c>RemoteUri</c>'si aynı olduğu için doğrulama hepsini "var" sayar ve BİR kaydı silmek
    /// diğerlerinin tek uzak kopyasını da siler. "Gün gün klasör HER YERDE" kuralı da delinir.
    ///
    /// Artık yalnız panelin KENDİ ürettiği yedek uzantıları sabit dosya sayılır — nokta içeren
    /// her ad değil. Üretilen adlar: <c>…zip</c> (BackupService'teki dört ad üretimi) ve
    /// şifreliyse <c>…zip.enc</c> (<see cref="BackupArchiveCipher.EncryptedExtension"/>).
    /// </remarks>
    private static bool IsPinnedArchiveTarget(string lastSegment)
        => lastSegment.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || lastSegment.EndsWith(".zip" + BackupArchiveCipher.EncryptedExtension, StringComparison.OrdinalIgnoreCase);

    private static (Uri Uri, string[] Folders) AppendRemotePath(Uri uri, BackupPlacement placement, string fileName)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        var lastSegment = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(lastSegment) && IsPinnedArchiveTarget(lastSegment))
        {
            // Kullanıcı TEK BİR DOSYA adresi vermiş (".../yedek.zip"): ne klasör ne de dosya adı
            // eklenir. Adresi bilerek sabitleyen kurulumların ölçülmüş davranışı budur, korunur.
            return (uri, []);
        }

        var folders = placement.RemoteFolders;
        var directoryPath = folders.Aggregate(path, CombineUriPath);

        var builder = new UriBuilder(uri)
        {
            Path = CombineUriPath(directoryPath, fileName),
            UserName = string.Empty,
            Password = string.Empty
        };

        return (builder.Uri, folders);
    }

    // =====================================================================================
    // UZAK HEDEFTE KLASÖR AÇMA (FTP: MKD, WebDAV: MKCOL)
    //
    // İki protokolde de aynı iki tuzak var ve ikisi de sessizliğe davetiye çıkarıyor:
    //
    // (1) "ZATEN VAR" bir hatadır. FTP'de MKD var olan dizin için 550, WebDAV'da MKCOL 405
    //     döndürür. Bu, İLK GECEDEN SONRAKİ HER GECENİN NORMALİ'dir. Hatayı olduğu gibi
    //     yükseltmek, yedeğin ikinci geceden itibaren "uzak hedefe gönderilemedi" ile
    //     bitmesi demek olurdu.
    //
    // (2) "İZİN YOK" da aynı kodları döndürür (550 / 403). Hatayı görmezden gelmek bu kez
    //     ters tarafa düşer: klasör hiç oluşmaz, dosya karşıya gitmez, panel yine de sessiz
    //     kalır ve kullanıcı olmayan bir off-site kopyaya güvenir.
    //
    // KARAR: yanıt koduna bakıp "bu hangisiydi" diye TAHMİN ETMİYORUZ (aynı kod, iki farklı
    // sebep; sunucudan sunucuya metin de değişir). Klasör açma sonucu ÖLÇÜLEBİLİR tek şeye
    // bağlanır: dosya karşıya gidebildi mi?
    //   - Yükleme BAŞARILIYSA klasör gerçekten vardı → MKD/MKCOL hatası zararsızdı, susulur.
    //     (Dizinlerin elle önceden açıldığı ve MKD'nin hiç izinli olmadığı kurulumlar da böylece
    //      çalışmaya devam eder — bunları baştan reddetmek gereksiz bir arıza olurdu.)
    //   - Yükleme BAŞARISIZSA klasör hazırlığındaki sunucu yanıtı hata ayrıntısına EKLENİR; kök
    //     sebep ("550 Permission denied") kullanıcının gördüğü mesajda durur, yutulmaz.
    // =====================================================================================

    /// <summary>
    /// WebDAV'da taban adresin altındaki ara koleksiyonları MKCOL ile açar.
    /// </summary>
    /// <returns>
    /// Klasör hazırlığında alınan ve "zaten var" ile açıklanamayan sunucu yanıtlarının özeti;
    /// her şey yolundaysa <c>null</c>. Dönen metin bir HATA DEĞİLDİR, yükleme başarısız olursa
    /// ayrıntıya eklenecek KANITTIR.
    /// </returns>
    private async Task<string?> EnsureWebDavCollectionsAsync(
        Uri baseUri,
        IReadOnlyList<string> folders,
        BackupTarget target,
        CancellationToken cancellationToken)
    {
        if (folders.Count == 0)
        {
            return null;
        }

        var client = CreateRemoteHttpClient();
        client.Timeout = RemoteUploadTimeout;

        var credential = ResolveCredential(target, baseUri);
        var path = Uri.UnescapeDataString(baseUri.AbsolutePath);
        var sorunlar = new List<string>();

        // YUKARIDAN AŞAĞIYA: ara koleksiyon yokken alt koleksiyonu açmak 409 Conflict verir.
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            path = CombineUriPath(path, folder);

            // Koleksiyon adresi SONU '/' ile biter (RFC 4918); bazı sunucular aksi hâlde
            // koleksiyonu dosya sanıp reddeder.
            var builder = new UriBuilder(baseUri)
            {
                Path = path + "/",
                Query = string.Empty,
                Fragment = string.Empty,
                UserName = string.Empty,
                Password = string.Empty
            };

            using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), builder.Uri);
            if (credential is not null)
            {
                var token = Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes($"{credential.UserName}:{credential.Password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
            }

            try
            {
                using var response = await client.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode
                    // 405 = kaynak zaten var (RFC 4918 9.3.1). İkinci geceden sonra BEKLENEN yanıt.
                    || response.StatusCode == HttpStatusCode.MethodNotAllowed)
                {
                    continue;
                }

                sorunlar.Add($"MKCOL {SanitizeUri(builder.Uri)} -> {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                sorunlar.Add($"MKCOL {SanitizeUri(builder.Uri)} -> {exception.Message}");
            }
        }

        return sorunlar.Count == 0 ? null : string.Join("; ", sorunlar);
    }

    private static string CombineUriPath(string left, string right)
    {
        var normalizedLeft = string.IsNullOrWhiteSpace(left) ? "/" : left;
        if (!normalizedLeft.EndsWith('/'))
        {
            normalizedLeft += "/";
        }

        return normalizedLeft + Uri.EscapeDataString(right);
    }

    /// <summary>
    /// Kayda yazılacak uzak adresten SIR taşıyan her parçayı düşürür.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eskiden yalnızca <c>kullanıcı:parola@</c> kısmı siliniyordu ve bu YETMİYORDU: S3
    /// <i>presigned</i> hedefte adres <c>X-Amz-Signature</c> ve <c>X-Amz-Credential</c>
    /// parametrelerini SORGU DİZESİNDE taşır. O adres <c>GET /backup</c> ile panele geliyor,
    /// yani imza tarayıcının ağ sekmesinde ve ara katman günlüklerinde görünür hâle geliyordu —
    /// imzayı ele geçiren, süresi dolana kadar o hedefe yazabilir.
    /// </para>
    /// <para>
    /// Sorgu ve parça (fragment) tamamen atılır. Kayıp bilgi yok: yedeğin uzakta NEREDE
    /// olduğunu şema + konak + yol zaten söyler; sorgu dizesi kimlik taşır, konum değil.
    /// </para>
    /// </remarks>
    private static string SanitizeUri(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.ToString();
    }

    private static string BuildDiagnosticDetails(Exception exception)
    {
        var details = exception.ToString();
        const int maxLength = 8000;
        return details.Length <= maxLength
            ? details
            : details[..maxLength] + "\r\n... (diagnostic output truncated)";
    }

    /// <summary>
    /// Bir dizini tüm alt içeriğiyle arşive verilen önek altında ekler (örn. "config/...").
    /// Okunamayan her dosya/klasör <paramref name="skipped"/>'a YAZILIR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Atlama SESSİZ OLAMAZ: bu yardımcı panelin kendi <c>config/</c> klasörünü arşive koyan
    /// yoldur; tek bir kilitli dosya yüzünden eksik kalan bir "başarılı" yedek, geri yükleme
    /// gününe kadar fark edilmezdi. <see cref="BackupSkipLog"/> tam bu yüzden ZORUNLU parametre.
    /// </para>
    /// <para>
    /// <b>Neden elle özyineleme:</b> <c>EnumerateFiles(..., AllDirectories)</c> erişilemeyen bir
    /// ALT KLASÖRE geldiğinde istisna atar ve o noktadan sonrasını hiç görmez — yedeğin tamamı
    /// düşerdi. Klasör klasör yürümek, erişilemeyen dalı KAYDEDİP diğer dalları arşive almayı
    /// mümkün kılıyor.
    /// </para>
    /// </remarks>
    private static void AddDirectoryToArchive(ZipArchive archive, string sourceDirectory, string entryPrefix, BackupSkipLog skipped)
    {
        var root = Path.GetFullPath(sourceDirectory);
        var kuyruk = new Stack<string>();
        kuyruk.Push(root);

        while (kuyruk.Count > 0)
        {
            var klasor = kuyruk.Pop();
            string[] dosyalar;
            try
            {
                foreach (var altKlasor in Directory.GetDirectories(klasor))
                {
                    kuyruk.Push(altKlasor);
                }

                dosyalar = Directory.GetFiles(klasor);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Erişilemeyen KLASÖR: içindeki her şey yedeğin dışında kaldı, bu kayda geçer.
                skipped.Add(GirdiAdi(root, entryPrefix, klasor), exception);
                continue;
            }

            foreach (var file in dosyalar)
            {
                var entryName = GirdiAdi(root, entryPrefix, file);
                try
                {
                    archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    skipped.Add(entryName, exception);
                }
            }
        }
    }

    /// <summary>Kökün kendisi için önek; altındakiler için "önek/göreli yol".</summary>
    private static string GirdiAdi(string root, string entryPrefix, string path)
    {
        var goreli = Path.GetRelativePath(root, path).Replace('\\', '/');
        return goreli is "." or "" ? entryPrefix : $"{entryPrefix}/{goreli}";
    }

    private static void AddTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), System.Text.Encoding.UTF8);
        writer.Write(content);
    }

    // Birleşik tam-site arşivi mi? (manifest.json + files/ öneki olan yeni format).
    private static bool IsCombinedSiteArchive(string archivePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            return archive.Entries.Any(entry =>
                entry.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.StartsWith("files/", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.StartsWith("databases/", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Geri yükleme öncesi canlı içeriğin taşındığı kardeş klasörün etiketi.</summary>
    private const string RestoreSidelineLabel = "geri_yukleme_yedegi";

    /// <summary>
    /// Zincir geri yüklemesinin SÜREN durumu: yarım kalan adımlar + yana alınan içeriğin yeri.
    /// </summary>
    /// <remarks>
    /// <b>Neden dönüş değeri DEĞİL:</b> geri yükleme yarıda bir istisnayla kesilirse dönüş değeri
    /// HİÇ oluşmaz. ÖLÇÜLDÜ: canlı içerik o noktada ZATEN kardeş klasöre taşınmıştı, kayıt ise
    /// güncellenmediği için panelde hâlâ "completed" görünüyordu — kullanıcının sitesi boş,
    /// eski içeriğin nerede olduğu hiçbir yerde YAZMIYOR. Bu nesne çağırana ait olduğu için
    /// istisna hâlinde de okunabilir ve kayda geçirilebilir.
    /// </remarks>
    private sealed class RestoreProgress
    {
        public List<BackupRestoreWarningDto> Warnings { get; } = [];

        public List<string> Preserved { get; } = [];

        public string PreservedPath =>
            string.Join(" | ", Preserved.Where(yol => !string.IsNullOrWhiteSpace(yol)));
    }

    // =====================================================================================
    // ZİNCİR GERİ YÜKLEME
    //
    // Üç kural burada geçerlidir:
    // (1) ÖNCE KORU, SONRA YAZ. files/* canlı kökün ÜZERİNE yazılır; yazmaya başladıktan sonra
    //     eski hâl yoktur. Mevcut içerik önce kardeş klasöre taşınır; taşıma başarısızsa geri
    //     yükleme HİÇ BAŞLAMAZ. Zincirde bu YALNIZCA BİR KEZ, ilk halkadan önce yapılır —
    //     her halkada tekrarlansaydı ikinci halka birincinin yazdıklarını yana alır ve sonuç
    //     yalnızca son halkanın taşıdığı birkaç dosyadan ibaret kalırdı.
    // (2) SIRA ÖNEMLİ. Halkalar 0'dan hedefe doğru uygulanır; ters sırada eski dosya yeninin
    //     üzerine yazılır ve geri yükleme sessizce YANLIŞ içerik üretir.
    // (3) SESSİZ BAŞARISIZLIK YOK. Atlanan her adım uyarıya çevrilir; çağıran kaydı
    //     restored_with_errors işaretler.
    // =====================================================================================

    private async Task RestoreSiteChainAsync(
        BackupRecordDto record,
        string targetArchivePath,
        BackupChainDto chain,
        IReadOnlyCollection<BackupRecordDto> allRecords,
        string? targetPath,
        string? password,
        RestoreProgress durum,
        CancellationToken cancellationToken)
    {
        // Hedef: belirtilmişse o yol, değilse sitenin gerçek fiziksel kökü (canlı siteyi geri yükle).
        string siteRoot;
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            siteRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(targetPath.Trim()));
        }
        else
        {
            var site = await iisSiteService.GetSiteAsync(record.Source, cancellationToken);
            siteRoot = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        }

        // HİÇBİR ŞEYE DOKUNMADAN ÖNCE: kök bir bağlantı olamaz (gerekçe EnsureRestoreRootIsNotLink).
        EnsureRestoreRootIsNotLink(siteRoot);
        Directory.CreateDirectory(siteRoot);

        var warnings = durum.Warnings;
        var ilkHalka = true;

        for (var i = 0; i < chain.Links.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var halka = chain.Links[i];
            var halkaKaydi = allRecords.First(item => item.Id == halka.BackupId);
            var sonHalka = i == chain.Links.Length - 1;

            BackupContent? acilan = null;
            try
            {
                // Hedef halkanın arşivi çağıran tarafından ZATEN açıldı (şifreliyse çözüldü);
                // ikinci kez çözmek büyük arşivlerde iki katı süre ve geçici disk demektir.
                var arsivYolu = string.Equals(halkaKaydi.Id, record.Id, StringComparison.Ordinal)
                    ? targetArchivePath
                    : (acilan = await OpenContentAsync(halkaKaydi, password, cancellationToken)).FilePath;

                using var archive = ZipFile.OpenRead(arsivYolu);

                if (ilkHalka)
                {
                    ilkHalka = false;

                    // YANA ALINAN İÇERİĞİN YERİ, YAZMAYA BAŞLAMADAN ÖNCE KAYDA GEÇER.
                    // Sonradan atanan bir yerel değişken, çıkarma yarıda patladığında kayboluyordu
                    // ve kullanıcının sitesi BOŞ kalıp eski içeriğin nerede olduğu hiçbir yerde
                    // yazmıyordu (ölçüldü). Artık çağıran, istisna hâlinde de bunu okuyabilir.
                    durum.Preserved.Add(SidelineIfArchiveReplacesFiles(archive, "files/", siteRoot));
                }

                ExtractPrefixedEntries(archive, "files/", siteRoot, warnings, cancellationToken);
                await ApplyDeletedEntriesAsync(archive, "files/", siteRoot, warnings, cancellationToken);

                // VERİTABANLARI YALNIZCA SON HALKADAN: her halka TAM döküm taşır, hepsini sırayla
                // içe aktarmak aynı veritabanını n kez yazmak (ve n kat beklemek) olurdu.
                if (sonHalka)
                {
                    await ImportDatabasesFromArchiveAsync(archive, "databases/", warnings, cancellationToken);

                    // POSTA da YALNIZ SON HALKADAN: hesap arşivleri her halkada TAM'dır (artımsal
                    // değildir), sırayla uygulamak aynı kutuyu n kez yazmak olurdu.
                    await RestoreMailMessagesFromArchiveAsync(archive, warnings, cancellationToken);
                }
            }
            finally
            {
                acilan?.Dispose();
            }
        }
    }

    /// <summary>
    /// Site arşivindeki hesap posta arşivlerini sunucuya geri yazar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>YALNIZ ARŞİVDEKİ ALAN ADINA YAZAR.</b> Hedef alan adı arşivin klasör yapısından
    /// (<c>mail-messages/&lt;alan&gt;/</c>) okunur ve her hesap arşivi kendi alan adına uygulanır;
    /// arşivde olmayan hiçbir alan adına, hiçbir hesaba DOKUNULMAZ. Sunucu geneli posta geri
    /// yüklemesinin (RestoreMailAsync) aksine burada servis DURDURULMAZ.
    /// </para>
    /// <para>
    /// Arşivde posta yoksa sessizce geçilir — bu bir hata değildir (posta istenmemiş ya da ön
    /// kontrol geçmemiş olabilir; o durum yedeğin manifestinde zaten yazılıdır).
    /// </para>
    /// </remarks>
    private async Task RestoreMailMessagesFromArchiveAsync(
        ZipArchive archive,
        List<BackupRestoreWarningDto> warnings,
        CancellationToken cancellationToken)
    {
        // Alan adı → o alana ait arşiv girdileri.
        var alanlar = new Dictionary<string, List<ZipArchiveEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(MailMessagesPrefix, StringComparison.OrdinalIgnoreCase) ||
                !entry.FullName.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var kalan = entry.FullName[MailMessagesPrefix.Length..];
            var ayirici = kalan.IndexOf('/');
            if (ayirici <= 0)
            {
                continue;
            }

            var alan = kalan[..ayirici];
            if (!alanlar.TryGetValue(alan, out var liste))
            {
                alanlar[alan] = liste = [];
            }

            liste.Add(entry);
        }

        if (alanlar.Count == 0)
        {
            return;
        }

        if (mailArchiver is null)
        {
            // Arşivde posta VAR ama geri yükleyecek araç YOK. SESSİZ GEÇİLMEZ: kullanıcı sitesinin
            // tam geri yüklendiğini sanıp postasının gelmediğini sonra fark ederdi.
            // MEVCUT ve ÇEVİRİSİ OLAN anahtar kullanılıyor ("Posta kutusu yeniden kurulamadı:
            // {target}") — durumu birebir anlatıyor. Yeni bir anahtar uydurmak, çevirisi olmayan
            // HAM bir anahtarın ekranda görünmesi demekti; özgül açıklama Detail'de taşınır.
            // (Aynı karar MailAutodiscoverPublisher'da da verilmişti.)
            //
            // DETAIL ETİKET-DEĞER, CÜMLE DEĞİL — gerekçe VerifyBackupsAsync'teki aynı dönüşümde
            // yazılı. Buradaki eski cümle Türkçe HARF de taşıyordu ("Arşivde... YÜKLENMEDİ"), yani
            // İngilizce panelde yarı çevrilmiş satır en görünür hâliyle çıkıyordu.
            //
            // ÜÇ OLGU DA DURUYOR: arşivde posta kutusu VAR (mailboxes=present), eksik olan bileşen
            // hangisi (archiver=vandelay, state=not_configured) ve sonuç ne (mail=not_restored).
            // Hangi alan adları olduğu Target'ta. "vandelay" adı bilerek KORUNDU: yöneticinin
            // kuracağı aracın adı teşhisin kendisidir ve dile bağlı değildir.
            warnings.Add(new BackupRestoreWarningDto
            {
                MessageKey = "backup.mail_account_failed",
                Target = string.Join(", ", alanlar.Keys),
                Detail = "mailboxes=present; archiver=vandelay; archiver_state=not_configured; mail=not_restored"
            });
            return;
        }

        foreach (var (alan, girdiler) in alanlar)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var temp = Path.Combine(Path.GetTempPath(), "ada-mailrestore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                foreach (var entry in girdiler)
                {
                    // Girdi adı arşivden gelir → yol birleştirmeden ÖNCE yalnız dosya adına indir.
                    // Ham adı Path.Combine'a vermek, ayırıcı içeren bir adın hedef klasörün DIŞINA
                    // yazmasına izin verirdi.
                    var hedef = Path.Combine(temp, Path.GetFileName(entry.FullName));
                    entry.ExtractToFile(hedef, overwrite: true);
                }

                var hatalar = await mailArchiver.RestoreDomainAsync(alan, temp, cancellationToken);
                foreach (var hata in hatalar)
                {
                    warnings.Add(new BackupRestoreWarningDto
                    {
                        MessageKey = "backup.mail_account_failed",
                        Target = alan,
                        Detail = hata
                    });
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add(new BackupRestoreWarningDto
                {
                    MessageKey = "backup.mail_account_failed",
                    Target = alan,
                    Detail = exception.Message
                });
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch { /* en iyi çaba */ }
            }
        }
    }

    /// <summary>
    /// ABONELİK zincirini geri yükler: her sitenin dosyaları kendi köküne, veritabanları içe
    /// aktarılarak, posta ve FTP hesapları yeniden kurularak.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>PAROLALAR YEDEĞE GİREMEZ.</b> Ne Windows yerel hesap parolaları (FTP) ne de posta kutusu
    /// parolaları (sunucu yalnızca özetlerini tutar) dışa aktarılabilir. Bu yüzden hesaplar
    /// RASTGELE parolayla kurulur ve her biri için "parolayı sıfırlayın" uyarısı üretilir —
    /// kullanıcı hesabın çalıştığını sanıp neden giriş yapamadığını aramamalıdır.
    /// </para>
    /// </remarks>
    private async Task RestoreSubscriptionChainAsync(
        BackupRecordDto record,
        string targetArchivePath,
        BackupChainDto chain,
        IReadOnlyCollection<BackupRecordDto> allRecords,
        string? password,
        RestoreProgress durum,
        CancellationToken cancellationToken)
    {
        var warnings = durum.Warnings;
        var korunanlar = durum.Preserved;
        var siteKokleri = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ilkHalka = true;

        for (var i = 0; i < chain.Links.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var halka = chain.Links[i];
            var halkaKaydi = allRecords.First(item => item.Id == halka.BackupId);
            var sonHalka = i == chain.Links.Length - 1;

            BackupContent? acilan = null;
            try
            {
                var arsivYolu = string.Equals(halkaKaydi.Id, record.Id, StringComparison.Ordinal)
                    ? targetArchivePath
                    : (acilan = await OpenContentAsync(halkaKaydi, password, cancellationToken)).FilePath;

                using var archive = ZipFile.OpenRead(arsivYolu);

                if (ilkHalka)
                {
                    ilkHalka = false;
                    await ResolveSubscriptionSiteRootsAsync(archive, siteKokleri, warnings, cancellationToken);

                    foreach (var (siteAdi, kok) in siteKokleri)
                    {
                        var korunan = SidelineIfArchiveReplacesFiles(archive, $"sites/{siteAdi}/files/", kok);
                        if (korunan.Length > 0)
                        {
                            korunanlar.Add(korunan);
                        }
                    }
                }

                foreach (var (siteAdi, kok) in siteKokleri)
                {
                    var onek = $"sites/{siteAdi}/files/";
                    ExtractPrefixedEntries(archive, onek, kok, warnings, cancellationToken);
                    await ApplyDeletedEntriesAsync(archive, onek, kok, warnings, cancellationToken);
                }

                if (sonHalka)
                {
                    await ImportDatabasesFromArchiveAsync(archive, "databases/", warnings, cancellationToken);
                    await RestoreSubscriptionMailAsync(archive, warnings, cancellationToken);
                    await RestoreSubscriptionFtpAsync(archive, warnings, cancellationToken);
                }
            }
            finally
            {
                acilan?.Dispose();
            }
        }
    }

    /// <summary>
    /// Abonelik arşivindeki site adlarını bulur ve her biri için canlı kökü çözer. Site IIS'te
    /// yoksa arşivdeki yapılandırmasından YENİDEN KURULUR (tam-sunucu felaket kurtarma yolu);
    /// kurulamıyorsa o sitenin dosyaları atlanır ve sebebi uyarı olur.
    /// </summary>
    private async Task ResolveSubscriptionSiteRootsAsync(
        ZipArchive archive,
        Dictionary<string, string> siteKokleri,
        List<BackupRestoreWarningDto> warnings,
        CancellationToken cancellationToken)
    {
        var siteAdlari = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var ad = NormalizeArchivePath(entry.FullName);
            if (!ad.StartsWith("sites/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var kalan = ad["sites/".Length..];
            var bolum = kalan.IndexOf('/');
            if (bolum > 0)
            {
                siteAdlari.Add(kalan[..bolum]);
            }
        }

        foreach (var siteAdi in siteAdlari)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var site = await iisSiteService.GetSiteAsync(siteAdi, cancellationToken);
                var kok = string.IsNullOrWhiteSpace(site.ApplicationRootPath)
                    ? site.PhysicalPath
                    : site.ApplicationRootPath;

                // Kök bir bağlantıysa O SİTE atlanır — abonelikteki DİĞER siteler geri yüklenmeye
                // devam eder. Bütün aboneliği tek bir kavşak yüzünden reddetmek, felaket
                // kurtarmayı açığın kendisinden pahalı hâle getirirdi.
                if (SiteBuilderPathGuard.IsReparsePoint(kok))
                {
                    warnings.Add(new BackupRestoreWarningDto
                    {
                        MessageKey = "backup.restore_target_is_link",
                        Target = siteAdi,
                        Detail = kok
                    });
                    continue;
                }

                siteKokleri[siteAdi] = kok;
                Directory.CreateDirectory(kok);
                continue;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Site yok: arşivdeki yapılandırmadan yeniden kurmayı dene.
                logger?.LogInformation(exception, "Abonelik geri yuklemesi: {Site} IIS'te yok, arsivden kurulacak.", siteAdi);
            }

            var kuruldu = await TryRecreateSiteFromArchiveAsync(archive, siteAdi, warnings, cancellationToken);
            if (kuruldu is not null)
            {
                siteKokleri[siteAdi] = kuruldu;
            }
        }
    }

    private async Task<string?> TryRecreateSiteFromArchiveAsync(
        ZipArchive archive,
        string siteAdi,
        List<BackupRestoreWarningDto> warnings,
        CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry($"sites/{siteAdi}/config/site.json");
        if (entry is null)
        {
            warnings.Add(new BackupRestoreWarningDto
            {
                MessageKey = "backup.site_config_missing",
                Target = siteAdi
            });
            return null;
        }

        if (!EnvanterGirdisiOlculebilir(entry, $"sites/{siteAdi}/config/site.json", warnings))
        {
            return null;
        }

        try
        {
            await using var stream = entry.Open();
            var site = await System.Text.Json.JsonSerializer.DeserializeAsync<AdaPanel.Core.Dtos.Sites.SiteDto>(
                stream,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
            if (site is null)
            {
                warnings.Add(new BackupRestoreWarningDto { MessageKey = "backup.site_config_missing", Target = siteAdi });
                return null;
            }

            // Panel yedeğindeki tam-sunucu DR yoluyla AYNI kanıtlanmış plan (HTTPS bağlamaları
            // bilerek kurulmaz; sertifika makineye bağlıdır ve SSL ekranından yeniden verilir).
            var (create, extraBindings) = IisSnapshotRestore.ToProvisioningPlan(site);
            if (!string.IsNullOrWhiteSpace(create.PhysicalPath))
            {
                Directory.CreateDirectory(create.PhysicalPath);
            }

            await iisSiteService.CreateSiteAsync(create, cancellationToken);
            foreach (var binding in extraBindings)
            {
                try
                {
                    await iisSiteService.AddBindingAsync(siteAdi, binding, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    warnings.Add(new BackupRestoreWarningDto
                    {
                        MessageKey = "backup.site_binding_failed",
                        Target = $"{siteAdi}:{binding.Port}",
                        Detail = exception.Message
                    });
                }
            }

            var kok = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
            Directory.CreateDirectory(kok);
            return kok;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            warnings.Add(new BackupRestoreWarningDto
            {
                MessageKey = "backup.site_create_failed",
                Target = siteAdi,
                Detail = exception.Message
            });
            return null;
        }
    }

    /// <summary>
    /// Arşivdeki posta ENVANTERİNDEN alan adlarını, hesapları ve alias'ları yeniden kurar.
    /// İletiler burada DEĞİLDİR (sunucu geneli posta yedeğindedir) ve parolalar sıfırlanmalıdır.
    /// </summary>
    private async Task RestoreSubscriptionMailAsync(
        ZipArchive archive,
        List<BackupRestoreWarningDto> warnings,
        CancellationToken cancellationToken)
    {
        foreach (var entry in archive.Entries.Where(item =>
                     NormalizeArchivePath(item.FullName).StartsWith("mail/", StringComparison.OrdinalIgnoreCase) &&
                     item.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var domain = Path.GetFileNameWithoutExtension(entry.Name);

            if (!EnvanterGirdisiOlculebilir(entry, entry.FullName, warnings))
            {
                continue;
            }

            AdaPanel.Core.Dtos.Mail.MailWorkspaceDto? workspace;
            try
            {
                await using var stream = entry.Open();
                workspace = await System.Text.Json.JsonSerializer.DeserializeAsync<AdaPanel.Core.Dtos.Mail.MailWorkspaceDto>(
                    stream,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add(new BackupRestoreWarningDto { MessageKey = "backup.mail_inventory_unreadable", Target = domain, Detail = exception.Message });
                continue;
            }

            if (workspace is null)
            {
                continue;
            }

            try
            {
                await mailService.CreateDomainAsync(new AdaPanel.Core.Dtos.Mail.CreateMailDomainRequest { DomainName = domain }, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Alan zaten varsa sorun yok; gerçekten kurulamadıysa hesaplar da kurulamaz ve
                // her biri kendi uyarısını üretir.
                logger?.LogInformation(exception, "Posta alani zaten var ya da kurulamadi: {Domain}", domain);
            }

            foreach (var account in workspace.Accounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await mailService.CreateAccountAsync(new AdaPanel.Core.Dtos.Mail.CreateMailAccountRequest
                    {
                        Domain = domain,
                        Address = account.Address,
                        // Parola dışa aktarılamaz → rastgele. Aşağıdaki uyarı bunu SÖYLER.
                        Password = $"Dr{Guid.NewGuid():N}aA1!",
                        MaxSizeMB = account.MaxSizeMB
                    }, cancellationToken);

                    warnings.Add(new BackupRestoreWarningDto
                    {
                        MessageKey = "backup.mail_password_reset_required",
                        Target = account.Address
                    });
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    warnings.Add(new BackupRestoreWarningDto
                    {
                        MessageKey = "backup.mail_account_failed",
                        Target = account.Address,
                        Detail = exception.Message
                    });
                }
            }

            foreach (var alias in workspace.Aliases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await mailService.CreateAliasAsync(new AdaPanel.Core.Dtos.Mail.CreateMailAliasRequest
                    {
                        Domain = domain,
                        Address = alias.Address,
                        Target = alias.Target
                    }, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    warnings.Add(new BackupRestoreWarningDto
                    {
                        MessageKey = "backup.mail_alias_failed",
                        Target = alias.Address,
                        Detail = exception.Message
                    });
                }
            }
        }
    }

    private async Task RestoreSubscriptionFtpAsync(
        ZipArchive archive,
        List<BackupRestoreWarningDto> warnings,
        CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry("ftp/accounts.json");
        if (entry is null)
        {
            return;
        }

        if (!EnvanterGirdisiOlculebilir(entry, "ftp/accounts.json", warnings))
        {
            return;
        }

        SubscriptionFtpEntry[] hesaplar;
        try
        {
            await using var stream = entry.Open();
            hesaplar = await System.Text.Json.JsonSerializer.DeserializeAsync<SubscriptionFtpEntry[]>(
                stream,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken) ?? [];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            warnings.Add(new BackupRestoreWarningDto { MessageKey = "backup.ftp_inventory_unreadable", Detail = exception.Message });
            return;
        }

        foreach (var hesap in hesaplar.Where(item => !string.IsNullOrWhiteSpace(item.UserName)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ftpService.CreateFtpUserAsync(new AdaPanel.Core.Dtos.Ftp.CreateFtpUserRequest
                {
                    UserName = hesap.UserName,
                    Password = $"Dr{Guid.NewGuid():N}aA1",
                    SiteName = hesap.SiteName,
                    HomePath = hesap.HomePath
                }, cancellationToken);

                warnings.Add(new BackupRestoreWarningDto
                {
                    MessageKey = "backup.ftp_password_reset_required",
                    Target = hesap.UserName
                });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add(new BackupRestoreWarningDto
                {
                    MessageKey = "backup.ftp_account_failed",
                    Target = hesap.UserName,
                    Detail = exception.Message
                });
            }
        }
    }

    /// <summary>
    /// Arşiv bu kökün dosyalarını GERÇEKTEN değiştiriyorsa mevcut içeriği kardeş klasöre taşır ve
    /// o klasörü döner; değiştirmiyorsa boş dizge.
    /// </summary>
    /// <remarks>
    /// Yalnızca veritabanı içeren bir yedek, canlı sitenin bütün dosyalarını yana alıp siteyi boş
    /// bırakmamalı — bu yüzden önce arşivde o önek altında gerçek dosya var mı diye BAKILIR.
    /// </remarks>
    private static string SidelineIfArchiveReplacesFiles(ZipArchive archive, string prefix, string root)
    {
        var replacesFiles = archive.Entries.Any(entry =>
            NormalizeArchivePath(entry.FullName).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(entry.Name));

        if (!replacesFiles || !Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any())
        {
            return string.Empty;
        }

        // Site göçündeki (_goc_yedek_) ve bileşen kurulumundaki (PreserveDataDirectories) kalıbın
        // aynısı: üzerine yazmadan önce yana al. Taşıyamıyorsak veriyi koruyamıyoruz demektir ve
        // koruyamadığımız veriyi ezmeye devam etmek kabul edilemez.
        var candidate = string.Empty;
        try
        {
            candidate = ContentSideline.BuildSiblingPath(root, RestoreSidelineLabel);
            ContentSideline.MoveAside(root, candidate);
            return candidate;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AppException("backup.restore_preserve_failed", 500, 500, $"Root={root}\r\nSideline={candidate}\r\n{exception.Message}");
        }
    }

    /// <summary>
    /// Verilen önek altındaki arşiv girdilerini köke çıkarır (önek sıyrılarak). Zip-slip
    /// denetimi burada yapılır; kökün dışına düşen girdi YAZILMAZ ve uyarıya çevrilir.
    /// </summary>
    private static void ExtractPrefixedEntries(
        ZipArchive archive,
        string prefix,
        string root,
        List<BackupRestoreWarningDto> warnings,
        CancellationToken cancellationToken)
    {
        var denetlenenOnekler = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue; // dizin girdisi
            }

            var hedef = ResolveInsideRoot(NormalizeArchivePath(entry.FullName), prefix, root, warnings, denetlenenOnekler);
            if (hedef is null)
            {
                continue;
            }

            // TEK BİR DOSYA BÜTÜN GERİ YÜKLEMEYİ DÜŞÜREMEZ.
            //
            // ÖLÇÜLDÜ: arşivdeki tek bir bozuk girdi (aynı adın hem dosya hem klasör olması, CRC
            // uyuşmazlığı, kilitli hedef) ham bir IOException olarak yukarı çıkıyor ve geri
            // yükleme orada kesiliyordu. O noktada canlı içerik ZATEN yana alınmıştı: kullanıcının
            // sitesi boş, kaydı güncellenmemiş ("completed"), eski içeriğin nerede olduğu
            // panelde hiçbir yerde YAZMIYOR. Felaket anında verilebilecek en pahalı cevap budur.
            //
            // Artık her girdi kendi başına değerlendirilir: yazılamayan dosya UYARI olur (sessiz
            // değil), kalan dosyalar geri yüklenir ve durum "restored_with_errors" olur.
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(hedef)!);
                entry.ExtractToFile(hedef, overwrite: true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add(new BackupRestoreWarningDto
                {
                    MessageKey = "backup.restore_file_failed",
                    Target = entry.FullName,
                    Detail = exception.Message
                });
            }
        }
    }

    /// <summary>
    /// Artımsal halkanın <c>deleted.json</c> listesini uygular: o halkada SİLİNMİŞ dosyalar
    /// hedeften de silinir.
    /// </summary>
    /// <remarks>
    /// Bu adım olmadan geri yükleme, kullanıcının bilerek kaldırdığı dosyaları (temizlenmiş bir
    /// zararlı eklenti, silinmiş eski bir sürüm) geri getirirdi — yani geri yükleme, sitenin
    /// yedeğin alındığı andaki hâline DEĞİL, hâllerin birleşimine dönerdi.
    /// </remarks>
    private static async Task ApplyDeletedEntriesAsync(
        ZipArchive archive,
        string prefix,
        string root,
        List<BackupRestoreWarningDto> warnings,
        CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(ArchiveDeletedEntry);
        if (entry is null)
        {
            return;
        }

        if (!EnvanterGirdisiOlculebilir(entry, ArchiveDeletedEntry, warnings))
        {
            return;
        }

        string[] silinecekler;
        try
        {
            await using var stream = entry.Open();
            silinecekler = await System.Text.Json.JsonSerializer.DeserializeAsync<string[]>(
                stream,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken) ?? [];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            warnings.Add(new BackupRestoreWarningDto { MessageKey = "backup.deleted_list_unreadable", Detail = exception.Message });
            return;
        }

        // SİLME, YAZMADAN DAHA TEHLİKELİDİR ve kavşak denetimi burada YOKTU (ölçüldü: kökteki bir
        // kavşak üzerinden kökün DIŞINDAKİ bir dosya GERÇEKTEN silindi). Ayrıca bu yol, "önce yana
        // al" adımının çalışmadığı halkalarda (arşivde files/ girdisi yoksa kök boşaltılmaz)
        // kavşağı yerinde bulur — yani en sinsi hâlidir.
        var denetlenenOnekler = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var yol in silinecekler)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hedef = ResolveInsideRoot(NormalizeArchivePath(yol), prefix, root, warnings, denetlenenOnekler);
            if (hedef is null)
            {
                continue;
            }

            try
            {
                if (File.Exists(hedef))
                {
                    File.Delete(hedef);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                warnings.Add(new BackupRestoreWarningDto
                {
                    MessageKey = "backup.delete_entry_failed",
                    Target = yol,
                    Detail = exception.Message
                });
            }
        }
    }

    /// <summary>
    /// Arşiv yolunu köke göre çözer. Önek uymuyorsa <c>null</c> (ilgisiz girdi); kökün DIŞINA
    /// düşüyorsa <c>null</c> + uyarı (zip-slip VEYA yol üzerinde bağlantı).
    /// </summary>
    /// <param name="denetlenenOnekler">
    /// Bağlantı denetiminin çağrı başına önbelleği; gerekçesi
    /// <see cref="SiteBuilderPathGuard.EnsureNoReparsePointOnPath"/> belgesinde yazılı.
    /// </param>
    private static string? ResolveInsideRoot(
        string archivePath,
        string prefix,
        string root,
        List<BackupRestoreWarningDto> warnings,
        HashSet<string> denetlenenOnekler)
    {
        if (!archivePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = archivePath[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
        if (relative.Length == 0)
        {
            return null;
        }

        var hedef = Path.GetFullPath(Path.Combine(root, relative));

        // Zip-slip koruması: hedef kökün dışına çıkamaz. Ayırıcı denetimi olmadan "C:\sites\ada"
        // öneki "C:\sites\ada2\..." ile de eşleşirdi; tek doğru cevap PathContainment'tadır.
        if (!PathContainment.IsInsideRoot(root, hedef))
        {
            // Sessizce atlamak, bozuk/kötücül bir arşivi "geri yüklendi" saymak olurdu.
            warnings.Add(new BackupRestoreWarningDto
            {
                MessageKey = "backup.invalid_entry_path",
                Target = archivePath,
                Detail = hedef
            });
            return null;
        }

        return HedefBaglantisizMi(root, hedef, archivePath, warnings, denetlenenOnekler) ? hedef : null;
    }

    // =====================================================================================
    // KAVŞAK (junction/symlink) NÖBETÇİSİ — DİZGE DENETİMİNİN GÖREMEDİĞİ KAÇIŞ
    //
    // ÖLÇÜLDÜ (BackupRestoreArchiveGuardTests, GERÇEK kavşakla): bu koruma yalnızca
    // RestoreItemsAsync'te vardı. Kardeş yollar — birleşik site zinciri, abonelik zinciri,
    // SİLME listesi ve düz zip çıkarması — aynı kapıyı kullanmıyordu ve ikisi kanıtlandı:
    //   * deleted.json, kökteki bir kavşağı takip edip kökün DIŞINDAKİ dosyayı SİLDİ,
    //   * düz zip çıkarması, hedefteki bir kavşağı takip edip hedefin DIŞINA YAZDI.
    //
    // Kural İCAT EDİLMEDİ: deponun tek kavşak nöbetçisi (SiteBuilderPathGuard) çağrılıyor.
    // İkinci bir yürüyüş yazmak, birini sıkılaştırıp diğerini unutmanın yoluydu — nitekim
    // tam olarak bu olmuştu.
    // =====================================================================================

    /// <summary>
    /// Kökten hedefe kadar yol üzerinde bağlantı var mı? Varsa girdi YAZILMAZ ve uyarıya çevrilir
    /// (bütün geri yükleme durmaz: bir kavşak yüzünden felaket kurtarmayı iptal etmek, açığın
    /// kendisinden pahalıya gelirdi).
    /// </summary>
    private static bool HedefBaglantisizMi(
        string root,
        string hedef,
        string girdiAdi,
        List<BackupRestoreWarningDto> warnings,
        HashSet<string> denetlenenOnekler)
    {
        try
        {
            SiteBuilderPathGuard.EnsureNoReparsePointOnPath(root, hedef, denetlenenOnekler);
            return true;
        }
        catch (AppException)
        {
            // ANAHTAR ÇEVRİLİR, YUTULMAZ — aynı karar RestoreItemsAsync'te de verilmişti:
            // ortak kapının kendi bağlamına ait anahtarını ("sitebuilder.target_is_link")
            // geri yükleme ekranına taşımak, kullanıcıya hiç kullanmadığı bir modülün hatasını
            // göstermek olurdu. Reddin SEBEBİ Detail'de durur.
            warnings.Add(new BackupRestoreWarningDto
            {
                MessageKey = "backup.invalid_entry_path",
                Target = girdiAdi,
                Detail = $"reason=link_on_path; path={hedef}"
            });
            return false;
        }
    }

    /// <summary>
    /// Çıkarma KÖKÜNÜN KENDİSİ bağlantı olamaz.
    /// </summary>
    /// <remarks>
    /// Segment segment yoklamak bunu GÖREMEZ (yürüyüş kökten BAŞLAR). Kök bir kavşaksa iki şey
    /// birden olur ve ikisi de yıkıcıdır: "önce yana al" adımı kavşağın HEDEFİNDEKİ içeriği
    /// taşır (yani ilgisiz bir klasörün içini boşaltır) ve sonraki her yazma kökün dışına gider.
    /// ÖLÇÜLDÜ: gerçek kavşakla, arşiv girdisi kavşağın hedefine yazıldı.
    /// <para>
    /// Kural burada da <see cref="SiteBuilderPathGuard"/>'dan gelir (<c>EnsureUsableRoot</c> aynı
    /// soruyu yayın hedefi için soruyor). Sessiz atlama YOK: hedef bağlantıysa iş HİÇ başlamaz ve
    /// sebep açıkça söylenir — yöneticinin yapması gereken, gerçek klasörü hedef vermektir.
    /// </para>
    /// </remarks>
    private static void EnsureRestoreRootIsNotLink(string root)
    {
        if (SiteBuilderPathGuard.IsReparsePoint(root))
        {
            throw new AppException("backup.restore_target_is_link", 400, 400, root);
        }
    }

    /// <summary>
    /// Arşivden okunacak bir ENVANTER girdisi tavanın altında mı? Değilse UYARI üretir ve
    /// <c>false</c> döner — girdi HİÇ AÇILMAZ.
    /// </summary>
    /// <remarks>
    /// Gerekçe ve değer <see cref="BackupArchiveLimits"/>'te yazılı. Sessiz atlama YOK: envanteri
    /// okumamak, o adımın (silmeler, siteler, FTP hesapları) hiç uygulanmaması demektir ve
    /// kullanıcı bunu bilmelidir.
    /// </remarks>
    private static bool EnvanterGirdisiOlculebilir(
        ZipArchiveEntry entry,
        string etiket,
        List<BackupRestoreWarningDto> warnings)
    {
        if (!BackupArchiveLimits.ExceedsMetadataLimit(entry))
        {
            return true;
        }

        warnings.Add(new BackupRestoreWarningDto
        {
            MessageKey = "backup.entry_too_large",
            Target = etiket,
            Detail = BackupArchiveLimits.DescribeTooLarge(entry)
        });
        return false;
    }

    /// <summary>
    /// ÇIKARILMIŞ klasörden okunacak bir envanter dosyası tavanın altında mı? Aynı kural, dosya
    /// hâli: panel yedeği önce diske çıkarılır, envanterler oradan okunur.
    /// </summary>
    private static bool CikarilmisEnvanterOlculebilir(
        string path,
        string etiket,
        List<BackupRestoreWarningDto> warnings)
    {
        long uzunluk;
        try
        {
            uzunluk = new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // ÖLÇEMEDİK. Okumaya devam etmek, tavanı sessizce kapatmak olurdu.
            warnings.Add(new BackupRestoreWarningDto
            {
                MessageKey = "backup.entry_too_large",
                Target = etiket,
                Detail = $"stat=failed; reason={exception.GetType().Name}"
            });
            return false;
        }

        if (uzunluk <= BackupArchiveLimits.MaxMetadataBytes)
        {
            return true;
        }

        warnings.Add(new BackupRestoreWarningDto
        {
            MessageKey = "backup.entry_too_large",
            Target = etiket,
            Detail = $"entry={etiket}; bytes={uzunluk}; limit={BackupArchiveLimits.MaxMetadataBytes}"
        });
        return false;
    }

    /// <summary>
    /// Bir arşivi hedefe çıkarır — zip-slip VE kavşak denetimiyle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden <c>ZipFile.ExtractToDirectory</c> DEĞİL:</b> BCL yalnız DİZGE kaçışını görür
    /// (girdi adındaki <c>..</c>). Hedefin içinde duran bir KAVŞAĞI göremez ve yazma onu TAKİP
    /// EDER — ölçüldü, gerçek kavşakla: hedefin dışına dosya yazıldı. Panel LocalSystem olduğu
    /// için hedefte hiçbir ACL durdurmaz.
    /// </para>
    /// <para>
    /// <b>Neden istisna DEĞİL uyarı:</b> bu yol panel yedeğinin (tam-sunucu felaket kurtarma)
    /// çıkarma adımıdır. Tek bir kötü girdi yüzünden bütün kurtarmayı iptal etmek, açığın
    /// kendisinden pahalıya gelirdi. Atlanan her girdi uyarıya döner ve kayıt
    /// "restored_with_errors" olur.
    /// </para>
    /// </remarks>
    private static List<BackupRestoreWarningDto> ExtractArchiveGuarded(
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        var warnings = new List<BackupRestoreWarningDto>();
        var denetlenenOnekler = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kok = Path.GetFullPath(destination);

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ad = NormalizeArchivePath(entry.FullName);
            if (ad.Length == 0)
            {
                continue;
            }

            var goreli = ad.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar);
            if (goreli.Length == 0)
            {
                continue;
            }

            var hedef = Path.GetFullPath(Path.Combine(kok, goreli));
            if (!PathContainment.IsInsideRoot(kok, hedef))
            {
                warnings.Add(new BackupRestoreWarningDto
                {
                    MessageKey = "backup.invalid_entry_path",
                    Target = entry.FullName,
                    Detail = hedef
                });
                continue;
            }

            if (!HedefBaglantisizMi(kok, hedef, entry.FullName, warnings, denetlenenOnekler))
            {
                continue;
            }

            try
            {
                // Dizin girdisi (adı "/" ile biter ya da Name boştur): yalnız klasör açılır.
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(hedef);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(hedef)!);
                entry.ExtractToFile(hedef, overwrite: true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add(new BackupRestoreWarningDto
                {
                    MessageKey = "backup.restore_file_failed",
                    Target = entry.FullName,
                    Detail = exception.Message
                });
            }
        }

        return warnings;
    }

    /// <summary>Arşivdeki <c>*.sql</c> dökümlerini ilgili motorlara içe aktarır.</summary>
    private async Task ImportDatabasesFromArchiveAsync(
        ZipArchive archive,
        string prefix,
        List<BackupRestoreWarningDto> warnings,
        CancellationToken cancellationToken)
    {
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ad = NormalizeArchivePath(entry.FullName);
            if (!ad.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !ad.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var fileName = Path.GetFileNameWithoutExtension(entry.Name); // "engine-dbname"
            var split = fileName.Split('-', 2);
            if (split.Length != 2)
            {
                warnings.Add(new BackupRestoreWarningDto
                {
                    MessageKey = "backup.invalid_entry_path",
                    Target = entry.FullName,
                    Detail = "Girdi adi 'motor-veritabani' bicimine uymuyor."
                });
                continue;
            }

            var tempDump = Path.Combine(Path.GetTempPath(), $"adabk-restore-{Guid.NewGuid():N}.sql");
            try
            {
                entry.ExtractToFile(tempDump, overwrite: true);
                await databaseService.ImportDumpAsync(new AdaPanel.Core.Dtos.Databases.DatabaseDumpRequest
                {
                    Engine = split[0],
                    DatabaseName = split[1],
                    FilePath = tempDump
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AppException exception)
            {
                // Diğer veritabanları/dosyalarla devam edilir AMA sonuç sessizce "restored"
                // sayılmaz: sebep kullanıcıya döner.
                warnings.Add(new BackupRestoreWarningDto
                {
                    MessageKey = exception.MessageKey,
                    Target = $"{split[0]}/{split[1]}",
                    Detail = exception.Details
                });
            }
            catch (Exception exception)
            {
                warnings.Add(new BackupRestoreWarningDto
                {
                    MessageKey = "database.restore_failed",
                    Target = $"{split[0]}/{split[1]}",
                    Detail = exception.Message
                });
            }
            finally
            {
                if (File.Exists(tempDump))
                {
                    File.Delete(tempDump);
                }
            }
        }
    }

    private sealed record BackupTarget(string Type, Uri? Uri, string UserName, string Password, string Region);
}
