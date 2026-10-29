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
        ["BackupController.cs#DownloadItemsAsync"] = "Seçili arşiv içeriğini geçici ZIP akışıyla indirir; kaynak arşivi, yedek envanterini ve site dosyalarını değiştirmez. Akış tamamlanınca geçici dosya silinir. Davranış testi: ScopedBackupExtractionAccessTests.",
        ["FilesController.cs#CalculateSizeAsync"] = "Klasör boyutu: yalnız meta veriden boyut toplar, dosya içeriğine bile dokunmaz; arayüzde klasör başına tıklanan bir düğme olduğu için her çağrıyı yazmak kaydı gürültüye boğardı.",
        ["MigrationController.cs#DiscoverPleskAsync"] = "Plesk keşfi: UZAK sunucudan domain listesi okur, bu sunucuda hiçbir şey değişmez.",
        ["MigrationController.cs#DiscoverCpanelAsync"] = "cPanel/WHM keşfi: uzak sunucudan hesap listesi okur, bu sunucuda hiçbir şey değişmez.",
        ["MigrationController.cs#ValidateAsync"] = "Göç ön-kontrolü: 'bağlanır mı' der, göçü BAŞLATMAZ.",
        ["SoftwareController.cs#CheckForUpdatesAsync"] = "Güncelleme yoklaması: upstream sürümleri okur, hiçbir bileşene dokunmaz.",
        ["SslRepairController.cs#PlanAsync"] = "SSL onarım önizlemesi yalnız mevcut IIS bağlamasını ve sertifikaları okur, kısa ömürlü korumalı plan belirteci üretir; IIS/sertifika deposu veya kalıcı kayda yazmaz. Davranış testi: SslBindingRepairTests.Preview_is_read_only_and_exposes_both_certificates_for_explicit_selection.",
        ["MailAutodiscoverController.cs#OutlookPostAsync"] = "Outlook autodiscover: istemciye ayar XML'i döner (protokol gereği POST), sunucuda hiçbir şey değişmez.",
        ["NginxController.cs#TestAsync"] = "nginx -t: yapılandırmayı doğrular, uygulamaz.",
        ["AuthController.cs#BeginTwoFactorSetupAsync"] = "2FA kurulumunu BAŞLATIR (QR/gizli üretir); etkinleştirme ayrı uçtadır ve o kayıt yazar.",

        // --- Kaydı ARKA PLAN İŞİ yazan uçlar ---
        //
        // Bu iki uçta kayıt ATLANMIYOR; yanıt yolundan ÇIKARILDI. Sebep ölçüldü: her ikisi de
        // kimliksizdir ve yaptıkları iş, verilen adresin panelde/listede KAYITLI olup olmamasına
        // göre değişiyordu (SMTP bağlantısı + denetim kaydının kiracı çözümlemesi). Gövde aynıydı
        // ama SÜRE değildi — bu makinede SMTP kapalı bir porta bile 2066 ms, yanıt vermeyen adrese
        // 10 sn'de hâlâ bekliyordu. Kaydı yanıt yolunda yazmak, kapatılan sızıntının bir parçasını
        // geri getirirdi. Kayıt ARTIK kuyruğa verilen işin içinde yazılıyor ve bu, kaynak taramasıyla
        // değil DAVRANIŞ testleriyle korunuyor:
        //   - ParolaSifirlamaPostasiTests (auth.password_reset.requested / .mail_failed)
        //   - AuditTenantLinkClosureTests (mail.list.unsubscribe_requested + konu çözümlemesi)
        ["AuthController.cs#ForgotPasswordAsync"] = "Kaydı arka plan işi yazar (ParolaSifirlamaPostasi): yanıt yolunda yazmak, kapatılan zamanlama sızıntısını geri getirirdi. Davranış testi: ParolaSifirlamaPostasiTests.",
        ["MailListPublicController.cs#RequestAsync"] = "Kaydı, ucun kuyruğa verdiği arka plan işi yazar: yanıt yolunda yazmak (kiracı çözümlemesi dahil) abone olup olmadığını süreyle sızdırıyordu. Davranış testi: AuditTenantLinkClosureTests.",
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
        // LİSTE BOŞ — 14 girdinin HEPSİ kapatıldı (bkz. CODEX_MEMORY 218).
        //
        // Kapatılanlar: BackupController (program silme + elle tur), WordPressController'ın altı
        // ucu (bakım kipi, YÖNETİCİ OTURUMU üreten giriş bağlantısı, sertleştirme, klonlama,
        // yıkıcı veri aynalama, güncelleme), UpdateController'ın üç ucu (paket indirme, kurulum
        // zamanlama, güncelleme KAYNAĞINI değiştirme), SitesController.ImportExistingSitesAsync,
        // AuthController.RegenerateRecoveryCodesAsync ve LicenseController.RefreshAsync.
        //
        // Liste boş bırakıldı, SİLİNMEDİ: yapısı, bir sonraki turda ertelenen bir borç çıktığında
        // onu "kalıcı muafiyet" (Muaflar) ile karıştırmadan yazabilmek için duruyor. İkisini aynı
        // listeye koymak, geçici bir borcu sessizce kalıcı bir gerekçeye dönüştürürdü.
    };

    /// <summary>
    /// Kaydın KONUSUNU (kiracıyı) yazmayan çağrılar ve HER BİRİNİN gerekçesi.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden bu nöbetçi var:</b> <c>AuditLog.SubscriptionUserId</c> sütunu ve onu sorgulayan
    /// görünürlük motoru (<c>AuditLogQueryEngine.ApplyVisibility</c>) aylardır vardı ama neredeyse
    /// hiçbir uç DOLDURMUYORDU — ölçüldü: denetleyicilerdeki 104 kayıt çağrısının yalnız 26'sı.
    /// Bir YÖNETİCİ kiracının posta kutusunu sildiğinde kaydın <c>UserId</c>'si yöneticidir ve
    /// kiracının izinli kimlik kümesinde bulunmaz; konu da boş olduğu için kayıt kiracının
    /// görünümünde HİÇ çıkmıyordu. Yani sütunun var olma sebebi olan soru — "benim siteme kim
    /// dokundu" — hâlâ cevapsızdı.
    /// </para>
    /// <para>
    /// <b>Neden HARİÇ TUTMA listesi:</b> kapsam diskten geliyor, yani yeni bir kayıt çağrısının
    /// varsayılanı "konusunu yazmalı"dır. Yazmayacaksa buraya GEREKÇESİYLE yazılmak zorundadır —
    /// karar görünür olur. Dahil etme listesi olsaydı, yeni uçlar sessizce kiracısız doğardı.
    /// </para>
    /// <para>
    /// <b>Anahtar:</b> <c>DosyaAdı#eylemArgümanı#kaynakArgümanı</c> (kaynaktaki metin, boşlukları
    /// sadeleştirilmiş). Metot adı DEĞİL: kayıtların bir kısmı ortak sarmalayıcılardan yazılır
    /// (<c>AuditSiteSecurityAsync</c>, <c>WriteBackupAuditAsync</c>) ve orada uç adı yoktur.
    /// Anahtarların dosya içinde BENZERSİZ olması ayrıca doğrulanır — çakışan iki çağrıdan biri
    /// diğerinin gerekçesine sığınamaz.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> KiraciyaAitOlmayanlar = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["InstallationReportingController.cs#\"installation.reporting.changed\"#\"panel\""] = "Kurulum bildirimi izni panel genelidir; yalnız yönetici değiştirir ve belirli bir aboneliğe ait değildir.",
        // --- AKTÖR ZATEN KONUNUN KENDİSİ ---
        //
        // Kullanıcının kendi hesabına yaptığı işlemler. Kayıt <c>UserId</c> üzerinden zaten o
        // kullanıcının görünümüne düşer; konuyu ayrıca yazmak aynı bilgiyi iki sütuna kopyalamak
        // olurdu ve ikisi bir gün ayrı düşebilirdi.
        ["ApiTokensController.cs#\"apitoken.created\"#\"api-token\""] = "Anahtar çağıranın KENDİ hesabına üretilir (apiTokenService.CreateAsync(User.GetRequiredUserId())); aktör = konu.",
        ["ApiTokensController.cs#\"apitoken.revoked\"#\"api-token\""] = "Anahtar çağıranın kendi hesabına aittir; aktör = konu.",
        ["AuthController.cs#\"auth.logout.succeeded\"#\"auth\""] = "Kimlik doğrulama: kullanıcının kendi oturumu; aktör = konu.",
        ["AuthController.cs#\"auth.impersonate.started\"#\"auth\""] = "Kimliğe bürünme bir PANEL YETKİ işlemidir, bir aboneliğin kaynağı üzerinde değil; hedef kullanıcı ayrıntıda yazılı.",
        ["AuthController.cs#\"auth.impersonate.stopped\"#\"auth\""] = "Yukarıdakinin karşılığı; sunucu/panel düzeyinde yetki işlemi.",

        // --- SUNUCU GENELİ: tek bir aboneliğe ait olmayan kaynaklar ---
        //
        // Bunları bir kiracıya bağlamak, o kiracıya KOMŞULARINI da kapsayan bir işlemi göstermek
        // olurdu: kayıt yanlış kişinin görünümüne düşer. "Bir kiracıyı ilgilendiriyor" ile
        // "bir kiracıya AİT" aynı şey değildir.
        ["AntivirusController.cs#action#\"antivirus\""] = "ClamAV motoru sunucu geneli tek örnektir; tarama ve imza güncellemesi bütün kiracıları kapsar.",
        ["AuditLogsController.cs#\"audit.retention.updated\"#\"audit-logs\""] = "Saklama politikası TÜM kiracıların kayıtlarına uygulanır.",
        ["AuditLogsController.cs#\"audit.retention.purged\"#\"audit-logs\""] = "Temizlik bütün kiracıların kayıtlarını siler; tek bir aboneliğe ait değil.",
        ["AutomationController.cs#\"automation.settings.updated\"#\"automation\""] = "Sunucu geneli otomasyon ayarı (bütün abonelikleri kapsayan turlar).",
        ["AutomationController.cs#\"automation.backup.run\"#\"automation\""] = "Bütün abonelikleri gezen yedek turu; tek konusu yok.",
        ["AutomationController.cs#\"automation.ssl.run\"#\"automation\""] = "Sunucudaki HER sertifikayı yenilemeye çalışan tur.",
        ["AutomationController.cs#\"automation.backup.remote_tested\"#$\"backup-target:{request.TargetType}\""] = "Uzak yedek HEDEFİ panel genelinde tanımlıdır; bir aboneliğe ait değil.",
        ["BackupController.cs#\"backup.encryption_changed\"#\"backup\""] = "Şifreleme ayarı panel genelidir; bundan sonraki HER aboneliğin yedeğine uygulanır.",
        ["BackupNetworkTrustController.cs#\"backup.network_trust.changed\"#\"backup\""] = "FTP hedefi iç ağ izni sunucu genelidir; yalnız gerçek sunucu sahibi değiştirebilir, kimliğe bürünmüş yönetici değiştiremez. Bir kiracıya ait kaynak değildir.",
        ["BackupNetworkTrustController.cs#\"backup.network_trust.denied\"#\"backup\""] = "Sunucu geneli FTP hedefi iznine yetkisiz veya kimliğe bürünmüş erişim denemesidir; tek kiracıya ait işlem değildir.",
        ["BillingController.cs#action#\"billing\""] = "Bu sarmalayıcının İKİ çağıranı ölçüldü ve ikisi de panel genelidir: entegrasyon anahtarı (açılınca BÜTÜN abonelikler dışa aktarılır olur) ve toplu kullanım raporu (tanım gereği her aboneliği kapsar). Hesap hedefli tek kayıt (billing.terminate_checked) konusunu AYRICA yazar.",
        ["CronController.cs#action#\"cron:server\""] = "Makine PATH'ine Python eklemek sunucu genelidir (yalnız yönetici çağırabilir); bütün kiracıların görevlerini aynı anda etkiler. Göreve ait kayıtlar ayrı sarmalayıcıdan yazılır ve sahibini görev adından ölçer.",
        ["BackupController.cs#\"backup.verified\"#\"backup\""] = "Doğrulama sunucudaki bütün yedekleri tarar.",
        ["CacheController.cs#$\"cache.{motor}.{eylem}\"#$\"cache:{motor}\""] = "Redis/Memcached sunucu geneli tek örnektir; bütün siteler aynı hizmeti paylaşır.",
        ["ClusterController.cs#action#\"cluster\""] = "Küme düğümleri sunucu/kurulum düzeyindedir, aboneliğe ait değil.",
        ["DatabasesController.cs#action#target"] = "Bu sarmalayıcı MOTOR ve veritabanı KULLANICISI hedefli kayıtlar içindir (kimlik kaydı, sertleştirme, uzak erişim); veritabanı hedefli olanlar WriteDatabaseAuditAsync ile sahibine bağlanır.",
        ["DiskQuotaController.cs#\"quota.fsrm.installed\"#\"server\""] = "FSRM bir WINDOWS ROLÜDÜR; kurulum sunucu genelidir, tetikleyen site yalnız bağlamdır.",
        ["DockerController.cs#\"docker.container.started\"#$\"docker-container:{id}\""] = "Docker uçları admin-only; panelde konteyner↔abonelik sahipliği KAYDI YOK — sahibi ölçülemez, tahmin edilmez.",
        ["DockerController.cs#\"docker.container.stopped\"#$\"docker-container:{id}\""] = "Konteyner sahipliği panelde kayıtlı değil; ölçülemeyen konu boş bırakılır.",
        ["DockerController.cs#\"docker.container.restarted\"#$\"docker-container:{id}\""] = "Konteyner sahipliği panelde kayıtlı değil.",
        ["DockerController.cs#\"docker.container.removed\"#$\"docker-container:{id}\""] = "Konteyner sahipliği panelde kayıtlı değil.",
        ["DockerController.cs#\"docker.container.run\"#$\"docker-image:{request.Image}\""] = "Yeni konteyner; hiçbir aboneliğe bağlanmamıştır.",
        ["DockerController.cs#\"docker.image.pulled\"#$\"docker-image:{request.Image}\""] = "İmajlar sunucu genelinde paylaşılır.",
        ["DockerController.cs#\"docker.image.removed\"#$\"docker-image:{id}\""] = "İmajlar sunucu genelinde paylaşılır.",
        ["ErrorReportsController.cs#action#\"error-reports\""] = "Hata raporları panelin kendi tanılama verisidir; aboneliğe ait değil.",
        ["FirewallController.cs#\"firewall.rule.allowed\"#$\"firewall:{rule.DisplayName}\""] = "Windows Güvenlik Duvarı kuralları sunucu genelidir; bir portu açmak bütün kiracıları etkiler.",
        ["FirewallController.cs#\"firewall.rule.blocked\"#$\"firewall:{rule.DisplayName}\""] = "Güvenlik duvarı kuralı sunucu geneli.",
        ["FirewallController.cs#\"firewall.rule.removed\"#$\"firewall:{ruleName}\""] = "Güvenlik duvarı kuralı sunucu geneli.",
        ["FtpController.cs#\"ftp.server.ensured\"#\"ftp-server\""] = "FTP sunucusunun KURULUMU server-level bir işlemdir.",
        ["FtpController.cs#\"ftp.certificate.applied\"#\"ftp-server\""] = "Yalnız yöneticinin değiştirebildiği ortak FTPS dinleyicisi sertifikası sunucu genelidir; belirli bir kiracının sertifikasını veya FTP hesabını değiştirmez.",
        ["PanelSettingsController.cs#\"panel.settings.updated\"#\"dns-address-sync\""] = "Genel sunucu IP geçişini yeniden denemek tüm yönetilen DNS bölgelerini kapsar; tek bir kiracıya ait değildir ve yöneticiyle sınırlıdır.",
        ["FtpController.cs#\"ftp.tls.updated\"#\"ftp-server\""] = "FTPS zorunluluğu bütün kiracıların bağlantılarını aynı anda etkiler.",
        ["HtaccessController.cs#\"htaccess.server_mode.changed\"#\"htaccess:server\""] = "Sunucu geneli .htaccess anahtarı (module.switch) TEK dosyadır ve kapı etkin modu iki anahtarın en düşüğü olarak hesaplar: bu düğme sunucudaki HER siteyi aynı anda açar/kapatır. Konusu bir kiracıya bağlansaydı, komşularını da kapsayan bir işlem o kiracının görünümüne düşerdi. Site başına kip değişikliği (htaccess.mode.changed) konusunu AYRICA yazar ve sahibine bağlanır.",
        ["HtaccessController.cs#\"htaccess.module.registered\"#\"htaccess:server\""] = "Modül kaydı applicationHost.config'e yazılır: sunucudaki yönetilen kod çalıştıran HER uygulama havuzunu bir kez geri döndürür ve modülü her siteye yükletir. Tek bir aboneliğin kaynağı değildir; konusu bir kiracıya bağlansaydı, komşularını da kapsayan bir işlem o kiracının denetim görünümüne düşerdi. Kaynak, sunucu geneli anahtarla (htaccess.server_mode.changed) BİLEREK aynı: 'bu sunucuda .htaccess'e kim dokundu' sorusu tek yerden aranabilsin.",
        ["LicenseController.cs#\"license.activated\"#\"license\""] = "Lisans KURULUMA aittir, hiçbir aboneliğe değil.",
        ["LicenseController.cs#\"license.refreshed\"#\"license\""] = "Lisans kuruluma aittir.",
        ["LicenseController.cs#\"license.deactivated\"#\"license\""] = "Lisans kuruluma aittir.",
        ["LogsController.cs#\"logs.rotation_settings_updated\"#\"logs\""] = "Döndürme politikası panelin ve BÜTÜN sitelerin günlüklerine uygulanır.",
        ["LogsController.cs#\"logs.rotation_run\"#\"logs\""] = "Yönetilen HER dizin için tur; site başına olanı ayrı kayıt yazar ve sahibine bağlanır.",
        ["MailController.cs#\"mail.webmail.opened\"#\"webmail\""] = "Webmail (Roundcube) sunucu geneli tek kurulumdur; açan kişi zaten aktör olarak yazılı.",
        ["MailController.cs#\"mail.configured\"#\"mail-server\""] = "Posta SUNUCUSUNUN kurulumu/yapılandırması; bütün alan adlarını kapsar.",
        ["MailController.cs#\"mail.public_base_url.updated\"#\"mail-server\""] = "Değer TÜM kiracıların liste iletilerine yazılır (bu yüzden zaten admin-only).",
        ["MailController.cs#\"mail.certificate.issued\"#string.IsNullOrWhiteSpace(request.HostName) ? request.Domain : request.HostName"] = "POSTA SUNUCUSUNUN kendi TLS sertifikası; her kiracının istemcisi bu sertifikayı görür.",
        ["MailController.cs#\"mail.spam.updated\"#\"mail-server\""] = "Spam filtresi sunucu genelidir (itibar/kara liste riski ortaktır).",
        ["MailController.cs#\"mail.queue.message_deleted\"#id"] = "Kuyruk kimliğinden alan adına gidilemez: silinen ileti kuyruktan da kalkar, sahibi ÖLÇÜLEMEZ — tahmin edilmez.",
        ["MailController.cs#request.RecoverOnly ? \"mail.relay.recovered\" : \"mail.relay.updated\"#request.RecoverOnly ? \"mail-server\" : request.Enabled ? $\"{request.Host}:{request.Port}\" : \"mail-server\""] = "Röle güncellemesi ve yarım kalan işlemin kurtarılması sunucunun TÜM giden postasının teslim yolunu belirler; yalnız yönetici çağırabilir, tek bir kiracıya ait değildir.",
        ["MigrationController.cs#\"migration.discover.trust_exception\"#$\"host:{host}\""] = "Konu, kimliği DOĞRULANAMAYAN bir KAYNAK SUNUCUDUR (Plesk/WHM) ve o sunucu bu panelin değildir: hiçbir aboneliğe ait olamaz, üstelik keşif anında bu sunucuda henüz hiçbir site/abonelik oluşmamıştır. Uç zaten admin-only. Kayıt, yöneticinin doğrulanmamış bir uca API anahtarı göndermeyi bilerek onayladığını yazar; göçün KENDİSİ (migration.site) konusunu AYRICA yazar ve hedef sitenin sahibine bağlanır.",
        ["NotificationsController.cs#\"notifications.settings.updated\"#\"notifications\""] = "Panelin bildirim kanalı (SMTP/webhook) sunucu genelidir.",
        ["NotificationsController.cs#\"notifications.test_sent\"#\"notifications\""] = "Panel bildirim kanalının sınanması.",
        ["PanelAccessController.cs#\"panel.access.setup\"#$\"panel-host:{request.HostName}\""] = "PANELİN KENDİ erişim adresi/sertifikası; bir aboneliğin kaynağı değil.",
        ["PanelSettingsController.cs#\"panel.settings.updated\"#\"panel\""] = "Panel genel ayarları.",
        ["PhpController.cs#\"php.redis.enabled\"#\"php\""] = "PHP eklentisi FastCGI düzeyinde, bütün siteler için etkinleşir.",
        ["PhpController.cs#\"php.install.completed\"#\"php\""] = "PHP çalışma zamanı kurulumu sunucu genelidir; siteye ATAMA ayrı uçtadır ve sahibine bağlanır.",
        ["PhpController.cs#\"php.global.settings.updated\"#\"php\""] = "GENEL php.ini; panelin kendi araçları dahil bütün PHP'yi etkiler.",
        ["SecurityController.cs#\"security.bruteforce_settings_changed\"#\"security:bruteforce\""] = "Kaba-kuvvet koruması PANEL GİRİŞİNİ korur; bir aboneliğin kaynağı değil.",
        ["SecurityController.cs#\"security.bruteforce_unbanned\"#$\"security:bruteforce:{request.IpAddress}\""] = "Yasak bir IP'ye aittir, bir aboneliğe değil; IP'den kiracı türetmek tahmindir.",
        ["ServerHealthController.cs#\"server.domains.refreshed\"#\"server\""] = "Sunucudaki bütün alan adlarının dış-bakış ölçümü.",
        ["ServerHealthController.cs#\"server.service.restarted\"#$\"service:{health.Name}\""] = "Windows hizmeti sunucu genelidir; yeniden başlatmak bütün kiracıları etkiler.",
        ["SetupController.cs#\"setup.initialized\"#\"panel\""] = "İlk kurulum: henüz hiçbir abonelik yok.",
        ["SitesController.cs#\"sites.import.completed\"#\"sites\""] = "İçe aktarılan siteler tanım gereği HENÜZ hiçbir aboneliğe atanmamıştır; atama ayrı bir işlemdir.",
        ["SslController.cs#\"ssl.renew_all\"#\"ssl:*\""] = "Tek çağrıda sunucudaki HER sitenin sertifikası yenilenir; tek konusu yok.",
        ["SubscriptionsController.cs#\"subscription.enforced\"#\"subscriptions\""] = "Bütün abonelikleri gezen tur; tek tek değişiklikler SubscriptionEnforcementService'te kendi sahibine yazılır.",
        ["TerminalController.cs#\"terminal.command.denied\"#\"terminal\""] = "Sunucu sahibi OLMAYAN bir yöneticinin SYSTEM kabuğu denemesi; panel/sunucu düzeyinde, aboneliğe ait değil.",
        ["TerminalController.cs#\"terminal.command.executed\"#\"terminal\""] = "Sunucuda SYSTEM yetkisiyle komut; panel/sunucu düzeyinde, aboneliğe ait değil.",
        ["UpdateController.cs#\"update.package.downloaded\"#\"panel\""] = "PANELİN KENDİ ikilisi; hiçbir aboneliğe ait değil.",
        ["UpdateController.cs#\"update.install.scheduled\"#\"panel\""] = "Panelin kendi kurulumu.",
        ["UpdateController.cs#\"update.source.changed\"#\"panel\""] = "Panelin güncelleme kaynağı.",
        ["WebDbController.cs#\"webdb.deployed\"#$\"webdb:{deployment.Provider}\""] = "phpMyAdmin/Adminer panelin kendi sitesine kurulur; oturum AÇMA ayrı kayıttır ve veritabanının sahibine bağlanır.",
    };

    /// <summary>
    /// KİRACIYA AİT ama konusu bu turda yazılmayan çağrılar — sonraki tura kalan borç.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden <see cref="KiraciyaAitOlmayanlar"/>'dan ayrı:</b> bunlar "kiracıya ait değil"
    /// DEĞİL, "kiracıya ait ama konusu henüz çözülmüyor" demektir. İkisini aynı listeye koymak,
    /// geçici bir borcu kalıcı bir gerekçeye dönüştürürdü ve bir yıl sonra kimse ayrımı bilemezdi
    /// — bu depoda tam olarak bu olmuştu.
    /// </para>
    /// <para>
    /// Liste yalnız KÜÇÜLEBİLİR: <see cref="Kiraci_bagi_borcu_KAPANINCA_listeden_dusurulur"/>.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> KiraciBagiSonrakiTurda = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // LİSTE BOŞ — kalan 5 girdinin HEPSİ kapatıldı. Kapatılan borcun özeti (hepsinde sahiplik
        // ÖLÇÜLÜYOR, çağıranın kimliğinden türetilmiyor):
        //
        //   SiteHostingSettingsController — sarmalayıcı artık SİTE ADINI alıyor; sahip
        //     AuditSubject.SiteOwnerAsync ile çözülüyor (yöneticinin kendi sitesinde null).
        //   CronController — sahip GÖREV ADININ ad alanı önekinden ("u<abonelikId>--") okunuyor;
        //     yani yönetici bir kiracının görevini sildiğinde de doğru konuya yazılıyor. Sunucu
        //     geneli tek kayıt (python_path) ayrı bir sarmalayıcıya alındı ve aşağıda gerekçeli.
        //   BillingController — hesap hedefli tek kayıt (terminate_checked) panelin ÖLÇTÜĞÜ eşleşen
        //     hesabı konu yazıyor; panel geneli iki kayıt aşağıda gerekçeli.
        //   MailListPublicController — kimliksiz uç ama kiracıya ait; konu, liste ADRESİNİN alan
        //     adından çözülüyor. Konu yazılmasaydı kayıt (UserId da boş olduğu için) kiracının
        //     görünümüne HİÇ düşmezdi.
        //
        // Liste boş bırakıldı, SİLİNMEDİ: bir sonraki turda ertelenen bir borç çıktığında onu
        // "kalıcı muafiyet" (KiraciyaAitOlmayanlar) ile karıştırmadan yazabilmek için duruyor.
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

    // ------------------------------------------------------------------
    // KİRACI BAĞI (SubscriptionUserId)
    // ------------------------------------------------------------------

    /// <summary>
    /// Bir denetleyicideki kayıt çağrısının KONUSUNU nasıl yazdığı.
    /// </summary>
    /// <remarks>
    /// Depoda kiracı taşıyabilen iki biçim var; üçüncüsü (<c>WriteAsync</c>) yapısal olarak
    /// taşıyamaz — onu seçmek, "bu kaydın konusu yok" demenin ta kendisidir ve bu yüzden
    /// gerekçelendirilmesi gerekir.
    /// </remarks>
    private sealed record KayitCagrisi(string Dosya, string Anahtar, bool KonuTasiyor, int Satir);

    [Fact]
    public void Kiraciya_ait_kaynaktaki_her_kayit_KONUSUNU_yazar()
    {
        var kok = KaynakKoku();
        var eksik = new List<string>();
        var tasiyan = 0;
        var toplam = 0;

        foreach (var cagri in KayitCagrilari(kok))
        {
            toplam++;
            if (cagri.KonuTasiyor)
            {
                tasiyan++;
                continue;
            }

            if (KiraciyaAitOlmayanlar.ContainsKey(cagri.Anahtar) || KiraciBagiSonrakiTurda.ContainsKey(cagri.Anahtar))
            {
                continue;
            }

            eksik.Add($"{cagri.Anahtar}   (satir {cagri.Satir})");
        }

        // ÖLÇEMEDİK ile SORUN YOK ayrımı: ayrıştırma bozulursa hiçbir çağrı bulunamaz ve test
        // sessizce yeşil kalırdı. Eşik, bu turda ölçülen 139 çağrının epey altında bırakıldı —
        // amaç kaymayı değil, TARAMANIN ÇÖKMESİNİ yakalamak.
        Assert.True(toplam >= 100, $"Kayit cagrisi taramasi bozulmus olabilir: yalnizca {toplam} cagri bulundu.");
        Assert.True(tasiyan >= 60, $"Kiraci bagi taramasi bozulmus olabilir: yalnizca {tasiyan} cagri konu yaziyor.");

        Assert.True(
            eksik.Count == 0,
            $"Denetim kaydinin KONUSU (SubscriptionUserId) yazilmayan cagri(lar) var ({eksik.Count} adet).\n"
            + "Kaynak kiraciya aitse konuyu OLCEREK yazin (AuditSubject.SiteOwnerAsync / MailDomainOwnerAsync / "
            + "DatabaseOwnerAsync); degilse GEREKCESIYLE KiraciyaAitOlmayanlar'a ekleyin.\n"
            + "Konu yazilmayan kaydi kiraci KENDI denetim ekraninda HIC goremez:\n  "
            + string.Join("\n  ", eksik));
    }

    /// <summary>Borç kapandığında girdi listeden DÜŞMEK zorunda.</summary>
    [Fact]
    public void Kiraci_bagi_borcu_KAPANINCA_listeden_dusurulur()
    {
        var kapananlar = KayitCagrilari(KaynakKoku())
            .Where(cagri => cagri.KonuTasiyor && KiraciBagiSonrakiTurda.ContainsKey(cagri.Anahtar))
            .Select(cagri => cagri.Anahtar)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            kapananlar.Length == 0,
            "Bu cagrilar artik kiraci bagini TASIYOR — KiraciBagiSonrakiTurda listesinden CIKARIN:\n  "
            + string.Join("\n  ", kapananlar));
    }

    /// <summary>
    /// Kiracı bağı listeleri çürümüş girdi içeremez ve anahtarlar BENZERSİZ olmalıdır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// İki ayrı çürüme var, ikisi de sessizdir. Birincisi: silinen/değişen bir çağrının gerekçesi
    /// listede kalır ve bir gün aynı anahtarla doğan YENİ bir çağrı sessizce muaf olur. İkincisi:
    /// aynı dosyada aynı anahtarı üreten iki çağrı olursa, biri diğerinin gerekçesine sığınır —
    /// yani tek bir gerekçe iki farklı kararı örter. İkisi de nöbetçiyi yeşil ama kör bırakır.
    /// </para>
    /// </remarks>
    [Fact]
    public void Kiraci_bagi_listeleri_CURUMUS_veya_CAKISAN_girdi_icermez()
    {
        var cagrilar = KayitCagrilari(KaynakKoku()).ToArray();
        var mevcut = new HashSet<string>(cagrilar.Select(cagri => cagri.Anahtar), StringComparer.Ordinal);

        var curuyenler = KiraciyaAitOlmayanlar.Keys
            .Concat(KiraciBagiSonrakiTurda.Keys)
            .Where(anahtar => !mevcut.Contains(anahtar))
            .ToArray();

        Assert.True(
            curuyenler.Length == 0,
            "Kiraci bagi listelerinde artik var olmayan cagrilar var (silin; yoksa ayni anahtarla eklenen yeni bir cagri sessizce muaf olur):\n  "
            + string.Join("\n  ", curuyenler));

        var cakisanlar = cagrilar
            .GroupBy(cagri => cagri.Anahtar, StringComparer.Ordinal)
            .Where(grup => grup.Count() > 1 && grup.Any(cagri => !cagri.KonuTasiyor))
            .Select(grup => $"{grup.Key}  (satirlar: {string.Join(", ", grup.Select(cagri => cagri.Satir))})")
            .ToArray();

        Assert.True(
            cakisanlar.Length == 0,
            "Ayni anahtari ureten birden cok kayit cagrisi var; tek gerekce iki karari orter. "
            + "Kaynak (resource) argumanini HEDEFI soyleyecek sekilde ayirin:\n  "
            + string.Join("\n  ", cakisanlar));
    }

    /// <summary>
    /// Denetim servisi denetleyicilerde TEK bir değişken adıyla çağrılır.
    /// </summary>
    /// <remarks>
    /// Bu, yukarıdaki nöbetçinin ATLANAMAMASI içindir. Tarama, kayıt çağrılarını
    /// <c>auditService.Write…</c> kalıbından bulur; bağımlılık başka bir adla enjekte edilseydi
    /// (<c>audit</c>, <c>log</c>) o denetleyicinin bütün çağrıları taramanın DIŞINDA kalır ve
    /// kiracı bağı kuralı orada sessizce uygulanmaz olurdu. Adın tekliğini ölçmek, kuralın
    /// kapsamını ölçmektir.
    /// </remarks>
    [Fact]
    public void Denetim_servisi_denetleyicilerde_TEK_ADLA_enjekte_edilir()
    {
        var bildirim = new Regex(@"IAuditService\s+(?<ad>\w+)", RegexOptions.Compiled);
        var yanlislar = new List<string>();
        var bulunan = 0;

        foreach (var yol in DenetleyiciDosyalari(KaynakKoku()))
        {
            foreach (Match eslesme in bildirim.Matches(File.ReadAllText(yol)))
            {
                bulunan++;
                var ad = eslesme.Groups["ad"].Value;
                if (!string.Equals(ad, "auditService", StringComparison.Ordinal))
                {
                    yanlislar.Add($"{Path.GetFileName(yol)}: IAuditService {ad}");
                }
            }
        }

        Assert.True(bulunan >= 20, $"IAuditService taramasi bozulmus olabilir: yalnizca {bulunan} bildirim bulundu.");
        Assert.True(
            yanlislar.Count == 0,
            "Denetim servisi 'auditService' disinda bir adla enjekte edilmis; kiraci bagi nobetcisi o dosyayi GOREMEZ:\n  "
            + string.Join("\n  ", yanlislar));
    }

    /// <summary>
    /// Denetleyicilerdeki BÜTÜN kayıt çağrıları: anahtar + konu taşıyor mu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden yalnız iki kalıp:</b> <c>auditService.AuditAsync(HttpContext, …)</c> (HTTP yolu) ve
    /// <c>auditService.Write…Async(…)</c> (doğrudan servis çağrısı). Denetleyiciye özgü
    /// sarmalayıcılar (<c>WriteAuditAsync</c>) BİLEREK taranmaz: onların gövdesi zaten bu iki
    /// kalıptan biriyle biter ve kararın verildiği yer orasıdır. Sarmalayıcı çağrılarını da saymak,
    /// aynı kararı iki kez listelemek olurdu.
    /// </para>
    /// </remarks>
    private static IEnumerable<KayitCagrisi> KayitCagrilari(string kok)
    {
        var kalip = new Regex(@"\.AuditAsync\s*\(|(?<![.\w])auditService\.Write(?<konulu>ForSubscription)?Async\s*\(", RegexOptions.Compiled);

        foreach (var yol in DenetleyiciDosyalari(kok))
        {
            var dosyaAdi = Path.GetFileName(yol);
            var metin = File.ReadAllText(yol);

            foreach (Match eslesme in kalip.Matches(metin))
            {
                var http = eslesme.Value.Contains(".AuditAsync", StringComparison.Ordinal);
                var acilis = metin.IndexOf('(', eslesme.Index);
                var kapanis = MetinFarkindaKapanis(metin, acilis);
                if (kapanis < 0)
                {
                    continue;
                }

                var parcalar = ArgumanlariAyir(metin[(acilis + 1)..kapanis]);

                // HTTP yolunda ilk argüman HttpContext'tir; servis yolunda yoktur.
                var kayma = http ? 1 : 0;
                var eylem = Sadelestir(Parca(parcalar, kayma));
                var kaynak = Sadelestir(Parca(parcalar, kayma + 1));

                // Konu, iki biçimde de 5. argümandır (0 tabanlı 4):
                //   AuditAsync(ctx, action, resource, details, subject)
                //   WriteForSubscriptionAsync(action, resource, details, userId, subject, ip, ct)
                // WriteAsync'te böyle bir argüman YOKTUR — yapısal olarak konu taşıyamaz.
                var konuluBicim = http || eslesme.Groups["konulu"].Success;
                var konu = konuluBicim ? Sadelestir(Parca(parcalar, 4)) : string.Empty;

                yield return new KayitCagrisi(
                    dosyaAdi,
                    $"{dosyaAdi}#{eylem}#{kaynak}",
                    konu.Length > 0 && !string.Equals(konu, "null", StringComparison.Ordinal),
                    metin[..eslesme.Index].Count(karakter => karakter == '\n') + 1);
            }
        }
    }

    private static string Parca(IReadOnlyList<string> parcalar, int indeks) =>
        indeks < parcalar.Count ? parcalar[indeks] : string.Empty;

    private static string Sadelestir(string deger) =>
        Regex.Replace(deger, @"\s+", " ").Trim();

    /// <summary>
    /// Dengeli kapanış parantezi — ama METİN FARKINDA.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden ayrı bir uygulama:</b> ölçüldü — sayaç tabanlı basit eşleştirme, argümanların
    /// içindeki <c>")"</c> gibi bir DİZGİYİ gerçek kapanış sanıp çağrıyı ERKEN bitiriyordu.
    /// Somut sonuç: <c>SslController</c>'ın sertifika kaydı, konusu YAZILI olduğu hâlde
    /// "yazmıyor" görünüyordu. Yani nöbetçi doğru kodu suçlayacak, geliştirici de onu susturmak
    /// için gerekçe listesine yalan bir satır yazacaktı.
    /// </para>
    /// </remarks>
    private static int MetinFarkindaKapanis(string metin, int acilis)
    {
        var derinlik = 0;
        var i = acilis;

        while (i < metin.Length)
        {
            var karakter = metin[i];

            if (karakter == '"')
            {
                var duzMetin = i > 0 && metin[i - 1] == '@';
                i++;
                while (i < metin.Length)
                {
                    if (duzMetin)
                    {
                        if (metin[i] == '"')
                        {
                            // @"..." içinde "" kaçırılmış tırnaktır, dizgi bitmez.
                            if (i + 1 < metin.Length && metin[i + 1] == '"')
                            {
                                i += 2;
                                continue;
                            }

                            i++;
                            break;
                        }
                    }
                    else
                    {
                        if (metin[i] == '\\')
                        {
                            i += 2;
                            continue;
                        }

                        if (metin[i] == '"')
                        {
                            i++;
                            break;
                        }
                    }

                    i++;
                }

                continue;
            }

            if (karakter == '(')
            {
                derinlik++;
            }
            else if (karakter == ')')
            {
                derinlik--;
                if (derinlik == 0)
                {
                    return i;
                }
            }

            i++;
        }

        return -1;
    }

    /// <summary>Argüman metnini ÜST DÜZEY virgüllerden ayırır (dizgi ve iç parantezler korunur).</summary>
    private static IReadOnlyList<string> ArgumanlariAyir(string argumanlar)
    {
        var parcalar = new List<string>();
        var mevcut = new System.Text.StringBuilder();
        var derinlik = 0;
        var i = 0;

        while (i < argumanlar.Length)
        {
            var karakter = argumanlar[i];

            if (karakter == '"')
            {
                var son = MetinSonu(argumanlar, i);
                mevcut.Append(argumanlar[i..son]);
                i = son;
                continue;
            }

            if (karakter is '(' or '[' or '{')
            {
                derinlik++;
            }
            else if (karakter is ')' or ']' or '}')
            {
                derinlik--;
            }
            else if (karakter == ',' && derinlik == 0)
            {
                parcalar.Add(mevcut.ToString());
                mevcut.Clear();
                i++;
                continue;
            }

            mevcut.Append(karakter);
            i++;
        }

        if (mevcut.ToString().Trim().Length > 0)
        {
            parcalar.Add(mevcut.ToString());
        }

        return parcalar;
    }

    /// <summary>Bir dizginin açılış tırnağından bir SONRAKİ karaktere kadar olan indeks.</summary>
    private static int MetinSonu(string metin, int acilis)
    {
        var duzMetin = acilis > 0 && metin[acilis - 1] == '@';
        var i = acilis + 1;

        while (i < metin.Length)
        {
            if (!duzMetin && metin[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (metin[i] == '"')
            {
                if (duzMetin && i + 1 < metin.Length && metin[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return metin.Length;
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
