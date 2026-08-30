using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Htaccess.Hosting;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Iis;

/// <summary>
/// panel "Password-Protected Directories" karşılığı: bir dizini HTTP Basic Auth ile korur.
/// </summary>
/// <remarks>
/// <para>
/// <b>2026-08 — TASARIM DEĞİŞTİ, ÇÜNKÜ ESKİSİ PAROLAYI DÜZ METİN SAKLIYORDU.</b>
/// Önceki sürüm korunan klasöre bir <c>web.config</c> yazıyor ve IIS URL Rewrite kuralında
/// <c>{HTTP_AUTHORIZATION}</c> değerini <c>^Basic &lt;base64(kullanıcı:parola)&gt;$</c> kalıbıyla
/// karşılaştırıyordu. Base64 KODLAMADIR, gizleme değil: canlıda ölçüldü —
/// <c>YWRha3VsbGFuaWNpOkdpemxpITIwMjZBZGE=</c> çözülünce <c>adakullanici:Gizli!2026Ada</c>.
/// Dosya MÜŞTERİNİN KENDİ SİTE KÖKÜNDE durduğu için parolayı düz metin okuyabilenler:
/// müşterinin FTP kullanıcısı, sitede çalışan her PHP betiği (yüklenmiş bir eklenti dahil),
/// site yedeğini indiren herkes ve dosyayı gören her panel kullanıcısı. Kullanıcılar aynı
/// parolayı başka yerlerde de kullanır; bu yüzden zarar korunan klasörle sınırlı değildi.
/// </para>
/// <para>
/// <b>NEDEN URL REWRITE TERK EDİLDİ — ölçülmüş bir imkânsızlık:</b> URL Rewrite'ın koşulları
/// yalnız dizge/desen karşılaştırır; özet HESAPLAYAMAZ. Basic Auth'ta tarayıcı
/// <c>base64(kullanıcı:parola)</c> gönderdiği için, kuralın karşılaştıracağı değer ZORUNLU
/// olarak parolanın kendisidir. Yani "URL Rewrite ile ama özetli" diye bir seçenek YOKTUR.
/// IIS'in kendi Basic Authentication modülü de elendi: canlı ölçümde site <c>web.config</c>'ine
/// yazılan <c>anonymousAuthentication</c>/<c>basicAuthentication</c> bölümü
/// <c>HTTP 500.19 (0x80070021 — bölüm üst düzeyde kilitli)</c> verdi; ayrıca Windows hesabı
/// gerektirirdi.
/// </para>
/// <para>
/// <b>SEÇİLEN YOL:</b> parola <c>bcrypt</c> özeti olarak PANELİN VERİ KÖKÜNDE saklanır
/// (<see cref="ProtectedDirectoryFile"/>), doğrulamayı istek yolunda AdaPanel'in IIS modülü
/// yapar (<see cref="ProtectedDirectoryGate"/>) ve doğrulama deponun mevcut
/// <c>Htpasswd</c> altyapısını kullanır. <b>Site kökünde artık hiçbir sır yoktur</b> — özet bile
/// yoktur. Bu, <c>AuthUserFile</c> konumu sorusunun da cevabıdır: motorun <c>.htaccess</c>
/// yolundaki kural parola dosyasını site kökünün İÇİNDE olmaya zorluyor
/// (<c>HtaccessConfigurationBuilder.ParolaDosyasi</c>), yani o yol seçilseydi özet müşterinin
/// FTP'siyle okunabilir olurdu. Kendi betimleyicimiz o kısıtı taşımadığı için dosya DIŞARIDA.
/// </para>
/// <para>
/// <b>NEDEN <c>.htaccess</c> + <c>.htpasswd</c> DEĞİL:</b> iki ölçüm birden eledi.
/// (1) <c>HtaccessEngineBridge.Cevir</c> <c>AuthenticationRequired</c> sonucunu bilerek
/// "karar yok"a çeviriyor — yani bugün bir <c>Require valid-user</c> satırı istek yolunda
/// HİÇBİR ŞEY yapmıyor. (2) <c>.htaccess</c> desteği sunucu genelinde ve site başına
/// VARSAYILAN KAPALI (<see cref="HtaccessRuntimeGate"/>); kapalıyken koruma SESSİZCE çalışmazdı.
/// Korumayı o anahtara bağlamak, "korumalı" yazan bir ekranla herkese açık bir klasör demekti.
/// Bu yüzden korumalı dizinlerin KENDİ kapısı var ve <c>.htaccess</c> kipinden BAĞIMSIZ çalışır.
/// </para>
/// <para>
/// <b>DÜRÜSTLÜK ŞARTI:</b> doğrulamayı modül yaptığı için, modül IIS'e kayıtlı değilse koruma
/// UYGULANMAZ. Servis bunu <see cref="IHtaccessModuleRegistry"/> ile ÖLÇER; ölçüm olumsuzsa
/// koruma KURULMAZ (<c>protected.engine_not_ready</c>) ve mevcut kayıtlar <c>IsActive=false</c>
/// görünür. Sessizce "kurdum" demek, düzeltmeye çalıştığımız hatanın yeni bir sürümü olurdu.
/// </para>
/// </remarks>
public sealed class DirectoryProtectionService : IDirectoryProtectionService
{
    /// <summary>Eski (düz metin) korumanın klasördeki imzası.</summary>
    internal const string LegacyMarker = "adapanel-protected";

    /// <summary>Eski kayıt dosyası. YALNIZCA geçiş için okunur; yeni kayıt buraya yazılmaz.</summary>
    private const string LegacyStatePath = "config/protected-dirs.json";

    private readonly IIisSiteService iisSiteService;
    private readonly Support.JsonFileStore fileStore;
    private readonly IHtaccessModuleRegistry moduleRegistry;
    private readonly ProtectedDirectoryStore store;

    /// <summary>Veri kökü; <see cref="ProtectedDirectoryStore"/> ile AYNI değer olmak zorunda.</summary>
    private readonly string dataRoot;

    public DirectoryProtectionService(
        IIisSiteService iisSiteService,
        Support.JsonFileStore fileStore,
        IHtaccessModuleRegistry moduleRegistry,
        IOptions<PanelRuntimeOptions> runtimeOptions)
    {
        this.iisSiteService = iisSiteService;
        this.fileStore = fileStore;
        this.moduleRegistry = moduleRegistry;
        this.dataRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(runtimeOptions.Value.DataRoot));
        this.store = new ProtectedDirectoryStore(this.dataRoot);
    }

    public async Task<IReadOnlyCollection<ProtectedDirectoryDto>> GetProtectedDirectoriesAsync(
        string siteName, CancellationToken cancellationToken = default)
    {
        var siteRoot = await SiteRootOrNullAsync(siteName, cancellationToken);

        // GEÇİŞ BURADA TETİKLENİR. Yan etkili bir GET olduğu için gerekçesi yazılmalı:
        // panelin site başına koşan bir açılış kancası YOK ve düz metin parolalar sunucuda
        // durdukça açık AÇIK KALIR. "Kullanıcı bir gün listeye bakınca" en geç tetiklenme
        // anıdır; kullanıcıdan parolaları yeniden girmesini istemek ise pratikte hiç
        // yapılmayacak bir işti (her dizin için ayrı parola). Dönüşüm İDEMPOTENT'tir.
        await MigrateLegacyAsync(siteName, siteRoot, cancellationToken);

        var motorHazir = MotorHazirMi();
        var kayitlar = OkuKayitlar(siteName, out var okunabildi);

        var sonuc = new List<ProtectedDirectoryDto>();
        foreach (var kayit in kayitlar.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            sonuc.Add(new ProtectedDirectoryDto
            {
                SiteName = siteName,
                RelativePath = kayit.RelativePath,
                UserName = kayit.UserName,
                Realm = kayit.Realm,
                IsActive = Olc(okunabildi, ParolaDosyasiVar(siteName, kayit), motorHazir)
            });
        }

        // Dönüştürülemeyen (motor hazır değil) eski korumalar KAYBOLMAZ: hâlâ çalışıyorlar ve
        // hâlâ düz metin parola taşıyorlar. Görünmez olsalardı kullanıcı ne açığı ne de
        // korumanın kendisini bilirdi.
        foreach (var eski in await LegacyKayitlarAsync(siteName, siteRoot, cancellationToken))
        {
            if (sonuc.Any(item => string.Equals(item.RelativePath, eski.RelativePath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            eski.IsLegacyPlaintext = true;
            sonuc.Add(eski);
        }

        return sonuc;
    }

    public async Task<ProtectedDirectoryDto> ProtectAsync(
        string siteName, ProtectDirectoryRequest request, CancellationToken cancellationToken = default)
    {
        var relative = SanitizeRelativePath(request.RelativePath);
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
        {
            throw new AppException("protected.credentials_required", 400, 400);
        }

        var userName = SanitizeUserName(request.UserName);

        // ÖNCE MOTOR ÖLÇÜLÜR, SONRA DOSYA YAZILIR. Ters sırada, modül kayıtlı olmayan bir
        // sunucuda panel "kurdum" der, klasör herkese açık kalırdı.
        if (MotorHazirMi() != true)
        {
            throw new AppException("protected.engine_not_ready", 503, 503);
        }

        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var siteRoot = SiteRoot(site);

        // Klasörün VAR OLDUĞU doğrulanır. Eskiden klasör oluşturuluyordu (koruma dosyası oraya
        // yazılacaktı); artık site kökünde hiçbir şey yazmadığımız için var olmayan bir yolu
        // "korudum" demek, kullanıcıya yazım hatasını hiç göstermemek olurdu.
        var folder = ResolveFolder(siteRoot, relative);
        if (!Directory.Exists(folder))
        {
            throw new AppException("protected.directory_not_found", 404, 404);
        }

        var realm = SanitizeRealm(string.IsNullOrWhiteSpace(request.Realm) ? $"{siteName} - {relative}" : request.Realm);

        var kayit = store.Upsert(siteName, relative, userName, request.Password, realm, site.ApplicationPoolName);

        // ESKİ DÜZ METİN DOSYASI SİLİNİR. Bırakılsaydı aynı klasörde hem yeni koruma hem de
        // parolayı okunur hâlde tutan eski dosya dururdu — açığı kapatmış SAYILMAZDIK.
        RemoveLegacyConfig(folder);
        await RemoveLegacyRecordAsync(siteName, relative, cancellationToken);

        return new ProtectedDirectoryDto
        {
            SiteName = siteName,
            RelativePath = kayit.RelativePath,
            UserName = kayit.UserName,
            Realm = kayit.Realm,

            // ÖLÇÜM, yazımdan SONRA ve dosyaların kendisinden. Sabit "true" yazmak, bu servisin
            // daha önce kapattığı hatanın (yazım başarısızken "korumalı" demek) tekrarı olurdu.
            IsActive = Olc(true, ParolaDosyasiVar(siteName, kayit), MotorHazirMi())
        };
    }

    public async Task RemoveSiteAsync(string siteName, CancellationToken cancellationToken = default)
    {
        // Site SİLİNİRKEN çağrılır. Korunan klasörlerin İÇİNE GİRİLMEZ: site dosyaları ya
        // kullanıcının isteğiyle zaten siliniyordur ya da BİLEREK saklanıyordur ve ikinci durumda
        // müşterinin içeriğine dokunmak olurdu. Silinen tek şey PANELİN kendi verisi — parola
        // özetleri silinmiş bir sitenin ardından sunucuda kalmamalı.
        store.RemoveSite(siteName);

        var all = (await fileStore.ReadAsync(LegacyStatePath, Array.Empty<ProtectedDirectoryDto>(), cancellationToken)).ToList();
        var kaldirilan = all.RemoveAll(item => string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase));

        // Yalnız gerçekten bir şey kaldırıldıysa yazılır: gereksiz yazma, eş zamanlı bir
        // güncellemeyi ezme riski taşır ve silme akışı birden çok kez çalışabilir.
        if (kaldirilan > 0)
        {
            await fileStore.WriteAsync(LegacyStatePath, all, cancellationToken);
        }
    }

    public async Task UnprotectAsync(string siteName, string relativePath, CancellationToken cancellationToken = default)
    {
        var relative = SanitizeRelativePath(relativePath);

        store.Remove(siteName, relative);

        // Eski düz metin koruması da kaldırılır: kullanıcı "kaldır" dediğinde klasörün AÇILMASI
        // beklenir. Yalnız yeni kaydı silmek, eski web.config duruyorsa klasörü kilitli bırakırdı.
        var siteRoot = await SiteRootOrNullAsync(siteName, cancellationToken);
        if (siteRoot is not null)
        {
            try
            {
                RemoveLegacyConfig(ResolveFolder(siteRoot, relative));
            }
            catch (AppException)
            {
                // Yol artık çözülemiyor (klasör silinmiş): silinecek dosya da yok.
            }
        }

        await RemoveLegacyRecordAsync(siteName, relative, cancellationToken);
    }

    // ------------------------------------------------------------------ ölçüm

    /// <summary>
    /// Modül IIS'e KAYITLI mı: <c>true</c> evet, <c>false</c> hayır, <c>null</c> ÖLÇÜLEMEDİ.
    /// </summary>
    /// <remarks>
    /// Üç değerli olması şart. <c>Readable=false</c> (applicationHost.config okunamadı) durumunda
    /// "kayıt yok" demek, IIS'in anlık bir erişim sorununu "korumanız çalışmıyor" alarmına
    /// çevirirdi; "kayıt var" demek ise tam tersi bir yalan olurdu.
    /// </remarks>
    internal bool? MotorHazirMi()
    {
        try
        {
            var kayit = moduleRegistry.Read();
            return kayit.Readable ? kayit.Confirmed : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>IsActive</c>'in tek karar noktası: koruma ŞU AN GERÇEKTEN uygulanıyor mu.
    /// </summary>
    /// <remarks>
    /// <c>null</c> = ölçülemedi. Bir bileşen bile "bilinmiyor" ise sonuç "bilinmiyor"dur;
    /// bilinmeyeni iyimser yorumlamak bu dosyanın kapattığı hata sınıfının kendisidir.
    /// </remarks>
    internal static bool? Olc(bool kayitOkunabildi, bool? parolaDosyasiVar, bool? motorHazir)
    {
        if (!kayitOkunabildi || parolaDosyasiVar is null || motorHazir is null)
        {
            return null;
        }

        return parolaDosyasiVar.Value && motorHazir.Value;
    }

    private bool? ParolaDosyasiVar(string siteName, ProtectedDirectoryRecord record)
    {
        try
        {
            return File.Exists(Path.Combine(
                ProtectedDirectoryFile.SiteRoot(dataRoot, siteName), record.PasswordFileName));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private IList<ProtectedDirectoryRecord> OkuKayitlar(string siteName, out bool okunabildi)
    {
        try
        {
            var kayitlar = store.Read(siteName);
            okunabildi = true;
            return kayitlar;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            okunabildi = false;
            return new List<ProtectedDirectoryRecord>();
        }
    }

    // ------------------------------------------------------------------ geçiş (eski düz metin)

    /// <summary>
    /// Eski düz metin korumalarını YENİ biçime dönüştürür.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden otomatik dönüşüm:</b> parola base64'ten geri çözülebildiği için kullanıcıdan
    /// hiçbir şey istemeden aynı parolayla yeni koruma kurulabiliyor. Üç seçenek vardı —
    /// (a) sessizce bırakmak: açık açık kalırdı, ELENDİ; (b) kullanıcıdan yeniden kurmasını
    /// istemek: her dizin için ayrı parola demek, pratikte yapılmaz ve o süre boyunca açık
    /// kalırdı; (c) dönüştürmek: kullanıcı hiçbir şey yapmadan düz metin diskten SİLİNİR.
    /// (c) seçildi.
    /// </para>
    /// <para>
    /// <b>KOŞULLU olmak ZORUNDA:</b> dönüşüm, uygulamayı URL Rewrite'tan modüle taşır. Modül
    /// IIS'e kayıtlı değilse dönüşüm ÇALIŞAN bir korumayı kaldırıp yerine hiçbir şey koymaz —
    /// klasör herkese açılırdı. Bu yüzden motor ölçülmeden hiçbir dosyaya dokunulmaz; dönüşmeyen
    /// kayıtlar listede <c>IsLegacyPlaintext</c> ile GÖRÜNÜR kalır.
    /// </para>
    /// </remarks>
    private async Task MigrateLegacyAsync(string siteName, string? siteRoot, CancellationToken cancellationToken)
    {
        if (siteRoot is null || MotorHazirMi() != true)
        {
            return;
        }

        var eskiler = await LegacyKayitlarAsync(siteName, siteRoot, cancellationToken);
        if (eskiler.Count == 0)
        {
            return;
        }

        SiteDto? site = null;
        foreach (var eski in eskiler)
        {
            string folder;
            try
            {
                folder = ResolveFolder(siteRoot, eski.RelativePath);
            }
            catch (AppException)
            {
                continue;
            }

            var kimlik = LegacyKimlikCoz(Path.Combine(folder, "web.config"));
            if (kimlik is null)
            {
                // Dosya okunamadı ya da kalıp tanınmadı: DOKUNMA. Silinirse koruma gider,
                // dönüştürülemezse de zaten yapılacak bir şey yok.
                continue;
            }

            site ??= await iisSiteService.GetSiteAsync(siteName, cancellationToken);

            store.Upsert(siteName, eski.RelativePath, kimlik.Value.User, kimlik.Value.Password,
                SanitizeRealm(eski.Realm), site.ApplicationPoolName);

            // Yeni koruma yazıldı; ANCAK ŞİMDİ düz metin silinir.
            RemoveLegacyConfig(folder);
            await RemoveLegacyRecordAsync(siteName, eski.RelativePath, cancellationToken);
        }
    }

    /// <summary>Eski kayıt dosyasındaki, klasöründe HÂLÂ düz metin web.config duran kayıtlar.</summary>
    private async Task<List<ProtectedDirectoryDto>> LegacyKayitlarAsync(
        string siteName, string? siteRoot, CancellationToken cancellationToken)
    {
        var sonuc = new List<ProtectedDirectoryDto>();
        if (siteRoot is null)
        {
            return sonuc;
        }

        var all = await fileStore.ReadAsync(LegacyStatePath, Array.Empty<ProtectedDirectoryDto>(), cancellationToken);
        foreach (var kayit in all.Where(item => string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                // SINIRLI OKUMA. Bu bir DÖNGÜ ve okunan dosya KİRACININ KENDİ klasöründe: hem
                // dosya boyutunu hem de kaç dosya okunacağını (korumalı klasör sayısı) kiracı
                // belirliyordu, yani tek istekte sınırsız bellek. Sınırı aşan dosya OKUNAMAMIŞ
                // sayılır -- aşağıdaki catch'in zaten yaptığı şey: "eski koruma hakkında bir şey
                // iddia etmiyoruz". Yanlış bir şey söylemektense hiçbir şey söylememek doğrudur.
                var webConfig = Path.Combine(ResolveFolder(siteRoot, kayit.RelativePath), "web.config");
                if (UntrustedFileText.TryRead(webConfig, out var webConfigMetni)
                    && webConfigMetni.Contains(LegacyMarker, StringComparison.OrdinalIgnoreCase))
                {
                    kayit.IsActive = true;
                    sonuc.Add(kayit);
                }
            }
            catch (Exception exception) when (
                exception is AppException or IOException or UnauthorizedAccessException)
            {
                // Okunamadı: eski koruma hakkında bir şey iddia etmiyoruz.
            }
        }

        return sonuc;
    }

    /// <summary>
    /// Eski <c>web.config</c>'ten kullanıcı/parolayı geri çözer.
    /// </summary>
    /// <remarks>
    /// Bu metodun VAR OLABİLMESİ bulgunun kanıtıdır: dosyada saklanan şey geri çözülebiliyorsa
    /// gizlenmemiş demektir. Kalıp <c>^Basic &lt;kaçırılmış base64&gt;$</c>'tir; kaçırma
    /// karakterleri (<c>\</c>) temizlenmeden çözme denenirse base64 bozuk görünür.
    /// </remarks>
    internal static (string User, string Password)? LegacyKimlikCoz(string webConfigPath)
    {
        string icerik;
        try
        {
            icerik = File.ReadAllText(webConfigPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        const string isaret = "^Basic ";
        var basla = icerik.IndexOf(isaret, StringComparison.Ordinal);
        if (basla < 0)
        {
            return null;
        }

        basla += isaret.Length;
        var bit = icerik.IndexOf('$', basla);
        if (bit <= basla)
        {
            return null;
        }

        var token = icerik[basla..bit].Replace("\\", string.Empty);
        try
        {
            var cozulen = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            var ayrac = cozulen.IndexOf(':');
            return ayrac <= 0 ? null : (cozulen[..ayrac], cozulen[(ayrac + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void RemoveLegacyConfig(string folder)
    {
        var webConfig = Path.Combine(folder, "web.config");
        try
        {
            // YALNIZ bizim imzamızı taşıyan dosya silinir; kullanıcının kendi web.config'ine
            // dokunulmaz (bu kural eski sürümde de vardı ve doğruydu).
            if (File.Exists(webConfig) &&
                File.ReadAllText(webConfig).Contains(LegacyMarker, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(webConfig);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Silinemedi (kilit/ACL). Yeni koruma yine de kurulu; kullanıcı listede eski kaydı
            // görmeye devam eder ve bir sonraki denemede silinir.
        }
    }

    private async Task RemoveLegacyRecordAsync(string siteName, string relative, CancellationToken cancellationToken)
    {
        var all = (await fileStore.ReadAsync(LegacyStatePath, Array.Empty<ProtectedDirectoryDto>(), cancellationToken)).ToList();
        var kaldirilan = all.RemoveAll(item =>
            string.Equals(item.SiteName, siteName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.RelativePath, relative, StringComparison.OrdinalIgnoreCase));

        if (kaldirilan > 0)
        {
            await fileStore.WriteAsync(LegacyStatePath, all, cancellationToken);
        }
    }

    // ------------------------------------------------------------------ yol/ad temizliği

    private async Task<string?> SiteRootOrNullAsync(string siteName, CancellationToken cancellationToken)
    {
        try
        {
            return SiteRoot(await iisSiteService.GetSiteAsync(siteName, cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Site kökü okunamadı (IIS yok/erişilemiyor): ÖLÇÜLEMEDİ. Kayıtları "koruma gitti"
            // diye işaretlemek, IIS'in anlık bir arızasını kalıcı bir alarma çevirirdi.
            return null;
        }
    }

    private static string SiteRoot(SiteDto site)
    {
        var root = string.IsNullOrWhiteSpace(site.ApplicationRootPath) ? site.PhysicalPath : site.ApplicationRootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new AppException("sites.site_not_found", 404, 404);
        }

        return root;
    }

    private static string ResolveFolder(string siteRoot, string relative)
    {
        var rootFull = Path.GetFullPath(siteRoot);
        var combined = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar)));

        // Zincir kontrolü: hedef site kökünün dışına çıkamaz.
        if (!combined.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException("protected.invalid_path", 400, 400);
        }

        return combined;
    }

    internal static string SanitizeRelativePath(string value)
    {
        var raw = (value ?? string.Empty).Trim().Replace('\\', '/');
        var segments = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var clean = new List<string>();
        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.Contains(':'))
            {
                throw new AppException("protected.invalid_path", 400, 400);
            }

            clean.Add(segment);
        }

        if (clean.Count == 0)
        {
            throw new AppException("protected.invalid_path", 400, 400);
        }

        return string.Join('/', clean);
    }

    /// <summary>
    /// Kullanıcı adını doğrular.
    /// </summary>
    /// <remarks>
    /// <b>İki nokta ve satır sonu YASAK.</b> Bunlar sadece "çirkin" değil, biçimin ayraçlarıdır:
    /// <c>.htpasswd</c> satırı <c>kullanıcı:özet</c>'tir ve betimleyici satır tabanlıdır. İki
    /// nokta içeren bir ad, dosyada BAŞKA bir kullanıcı/özet çifti gibi okunabilirdi. Sessizce
    /// temizlemek yerine REDDEDİLİR: kullanıcının girdiği adla giriş yapacağı ad AYNI olmalı.
    /// </remarks>
    internal static string SanitizeUserName(string value)
    {
        var temiz = (value ?? string.Empty).Trim();
        if (temiz.Length == 0 || temiz.Length > 128 ||
            temiz.Any(ch => ch is ':' or '\r' or '\n' || char.IsControl(ch)))
        {
            throw new AppException("protected.invalid_user_name", 400, 400);
        }

        return temiz;
    }

    private static string SanitizeRealm(string value)
    {
        var filtered = new string((value ?? string.Empty)
            .Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '.' or '_')
            .ToArray())
            .Trim();
        return string.IsNullOrWhiteSpace(filtered) ? "Protected" : filtered;
    }
}
