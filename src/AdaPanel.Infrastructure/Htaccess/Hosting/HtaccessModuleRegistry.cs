using System.Reflection;
using System.Runtime.Versioning;
using AdaPanel.Core.Common;
using Microsoft.Extensions.Logging;
using static AdaPanel.Infrastructure.AspNet.IisConfigurationAccess;

namespace AdaPanel.Infrastructure.Htaccess.Hosting;

/// <summary>
/// Modül derlemesinin GAC'ta bulunup bulunmadığının ÜÇ DEĞERLİ sonucu.
/// </summary>
/// <remarks>
/// Üç değerli, çünkü "yok" ile "bakamadım" aynı şey değil. GAC kökünün kendisi okunamıyorsa
/// (alışılmadık bir Windows kurulumu, yetki) "kurulu değil" demek uydurma olurdu — panel o
/// durumda hiçbir şey iddia etmez.
/// </remarks>
public enum HtaccessModuleAssemblyPresence
{
    /// <summary>GAC kökü okunamadı: kurulu olup olmadığı BİLİNMİYOR.</summary>
    Unknown = 0,

    /// <summary>GAC kökü okundu, derleme YOK.</summary>
    Missing = 1,

    /// <summary>Derleme GAC'ta bulundu.</summary>
    Found = 2
}

/// <summary>Modül derlemesinin GAC'taki ÖLÇÜLMÜŞ hâli.</summary>
public sealed class HtaccessModuleAssemblyState
{
    public HtaccessModuleAssemblyPresence Presence { get; init; }

    /// <summary>Bulunan derlemenin tam yolu (boş = bulunamadı).</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Derlemeden OKUNAN sürüm — klasör adından tahmin EDİLMEZ.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Derlemeden OKUNAN güçlü ad ortak anahtarı.</summary>
    public string PublicKeyToken { get; init; } = string.Empty;
}

/// <summary>
/// <c>applicationHost.config</c>'teki modül girdisinin ÖLÇÜLMÜŞ hâli.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bu tipin var olma sebebi bir YALANDIR.</b> Panel uzun süre <c>EngineLoadedInIis</c> alanını
/// yalnız uygulama havuzunun CLR sürümüne bakarak dolduruyordu: havuz <c>v4.0</c> ise "motor IIS'e
/// yüklü" diyordu. Havuz sürümü bir ÖN KOŞULDUR, kaydın kendisi DEĞİL. Canlıda ölçülen sonuç tam
/// olarak buydu — panel <c>engineLoadedInIis: true</c> diyordu, benzetim doğru cevabı üretiyordu
/// (302), gerçek istek ise 404 dönüyordu; çünkü modül isteğin içine hiç girmiyordu.
/// </para>
/// <para>
/// <b>Üç değerli okuma:</b> <see cref="Readable"/> false ise HİÇBİR iddia geçerli değildir —
/// <see cref="Present"/> de false görünür ama bu "kayıt yok" demek DEĞİLDİR, "bakamadım" demektir.
/// İkisini ayırmayan bir tip, düzeltmeye çalıştığımız yalanın yeni bir sürümünü üretirdi.
/// </para>
/// </remarks>
public sealed class HtaccessModuleRegistrationState
{
    /// <summary><c>applicationHost.config</c>'in <c>&lt;modules&gt;</c> bölümü OKUNABİLDİ mi.</summary>
    public bool Readable { get; init; }

    /// <summary>Koleksiyonda modülün adını taşıyan bir girdi VAR mı.</summary>
    public bool Present { get; init; }

    /// <summary>Girdinin <c>type</c> özniteliği (ham).</summary>
    public string TypeName { get; init; } = string.Empty;

    /// <summary>
    /// Girdinin <c>preCondition</c> özniteliği (ham). Boş dize "ön koşul YOK" demektir ve bu bir
    /// engel değildir — modül o hâlde her istekte koşar.
    /// </summary>
    public string PreCondition { get; init; } = string.Empty;

    /// <summary>Okuma başarısızsa nedeni (arayüzde değil, teşhiste kullanılır).</summary>
    public string ReadError { get; init; } = string.Empty;

    /// <summary>Modül derlemesinin GAC'taki hâli.</summary>
    public HtaccessModuleAssemblyState Assembly { get; init; } = new();

    /// <summary>
    /// Girdinin <c>type</c>'ı GERÇEKTEN bizim modülümüzü mü gösteriyor.
    /// </summary>
    /// <remarks>
    /// Yalnız ADA bakmak yetmez: aynı adla başka bir tipe işaret eden bir girdi (elle düzenleme,
    /// eski bir sürümden kalan kayıt) IIS'in bambaşka bir sınıfı yüklemesi demektir ve bizim
    /// modülümüz hiç çalışmaz. Sürüm/anahtar KARŞILAŞTIRILMIYOR — onlar derlemeden okunur ve
    /// burada elle yazılsalardı "tahmin yasak" kuralını çiğnerdik; ölçülen şey tip adıdır.
    /// </remarks>
    public bool TypeMatches =>
        TypeName.Split(',')[0].Trim().Equals(HtaccessModuleRegistration.TypeName, StringComparison.Ordinal);

    /// <summary>
    /// <b>ÖN KOŞUL, MODÜLÜ TAM DA ÇALIŞMASI GEREKEN İSTEKLERDEN ELİYOR MU.</b>
    /// </summary>
    /// <remarks>
    /// <c>managedHandler</c> ön koşulu, modülün YALNIZ yönetilen işleyicilerin (<c>.aspx</c>)
    /// hizmet ettiği isteklerde koşmasını sağlar. <c>.htaccess</c>'in bütün amacı PHP ve STATİK
    /// dosyalardır — yani bu ön koşul varsa özellik "kurulu" görünür ama HİÇBİR ŞEY yapmaz.
    /// Gerekçenin Microsoft belgelerinden alıntısı <see cref="HtaccessModuleRegistration.PreCondition"/>
    /// üzerinde yazılı; burası onun ÖLÇÜMÜDÜR.
    /// </remarks>
    public bool PreConditionExcludesUnmanagedRequests =>
        PreCondition.Contains("managedHandler", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Kayıt DOĞRULANDI: okundu, var, doğru tipi gösteriyor ve ön koşul isteği elemiyor.
    /// </summary>
    /// <remarks>
    /// Dört koşulun HEPSİ gerekli. Biri bile sağlanmıyorsa panel "yüklü" DEMEZ — ölçemediğini
    /// iyimser yorumlamak, bu dosyanın var olma sebebi olan hatanın tekrarı olurdu.
    /// </remarks>
    public bool Confirmed => Readable && Present && TypeMatches && !PreConditionExcludesUnmanagedRequests;
}

/// <summary>
/// Modülün IIS kaydını OKUR ve (onarım için) YAZAR.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden arayüz:</b> tek uygulaması gerçek IIS'e dokunuyor ve bu geliştirme/test makinesinde
/// yoktur. Arayüz olmadan "kayıt okunamadığında panel ne diyor" sorusu ancak IIS'li bir sunucuda
/// ölçülebilirdi — yani hiç ölçülemezdi.
/// </para>
/// <para>
/// <b>Neden public:</b> <see cref="HtaccessPanelService"/> kurucusunda geçiyor ve o kurucu
/// public (DI onu böyle çözüyor); internal bir arayüz orada erişilebilirlik hatası verirdi.
/// </para>
/// </remarks>
public interface IHtaccessModuleRegistry
{
    /// <summary>Kaydı OKUR. İSTİSNA FIRLATMAZ: okunamama da bir sonuçtur (<c>Readable=false</c>).</summary>
    HtaccessModuleRegistrationState Read();

    /// <summary>
    /// Kaydı YAZAR (onarım). Yazamazsa GÜRÜLTÜLÜ hata verir — "kurdum" deyip hiçbir şey yapmamak,
    /// bu iş kolunun düzeltmeye çalıştığı hatanın ta kendisidir.
    /// </summary>
    HtaccessModuleRegistrationState Register();
}

/// <summary>
/// <see cref="IHtaccessModuleRegistry"/>'nin GERÇEK uygulaması: <c>ServerManager</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden <c>appcmd.exe</c> ya da kurulum betiği ÇAĞRILMIYOR:</b> panel IIS'i zaten
/// <c>ServerManager</c> ile yönetiyor (<c>AspNetService</c>, <c>SiteAliasService</c>, ...).
/// Onarımı kabuktan betikle yapmak, AYNI kaydın İKİ farklı yoldan yazılması demek olurdu ve iki
/// yol zamanla ayrışırdı — bu depoda bedeli defalarca ödenmiş hata sınıfı. Ad/tip/ön koşul
/// sabitleri <see cref="HtaccessModuleRegistration"/>'dan geliyor; betik de aynı değerleri
/// kullanıyor ve eşitlik <c>HtaccessHostingRegistrationTests</c> ile nöbet altında.
/// </para>
/// <para>
/// <b>GAC KURULUMU BURADAN YAPILMAZ ve bu bir eksiklik değil, ölçülmüş bir sınır:</b> betiğin
/// kullandığı <c>System.EnterpriseServices.Internal.Publish.GacInstall</c> .NET Framework'e
/// özgüdür ve panelin çalıştığı .NET 8'de YOKTUR. Bu yüzden onarım, derlemenin GAC'ta OLDUĞUNU
/// önce ÖLÇER ve yoksa REDDEDER. Reddetmemek felaket olurdu: <c>type</c> güçlü adlı bir GAC
/// başvurusudur; derleme yokken kaydı yazmak, sunucudaki yönetilen kod çalıştıran HER havuzda
/// HER isteği 500'e düşürürdü.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class IisHtaccessModuleRegistry(ILogger<IisHtaccessModuleRegistry> gunluk)
    : IHtaccessModuleRegistry
{
    private const string ModulesSection = "system.webServer/modules";

    public HtaccessModuleRegistrationState Read()
    {
        var derleme = HtaccessModuleAssemblyLocator.Locate();

        try
        {
            using var yonetici = CreateServerManager();
            var girdi = Girdi(yonetici, out _);

            if (girdi is null)
            {
                return new HtaccessModuleRegistrationState { Readable = true, Assembly = derleme };
            }

            return new HtaccessModuleRegistrationState
            {
                Readable = true,
                Present = true,
                TypeName = TryGetAttributeString(girdi, "type"),
                PreCondition = TryGetAttributeString(girdi, "preCondition"),
                Assembly = derleme
            };
        }
        catch (Exception istisna)
        {
            // OKUNAMAMA YUTULMUYOR, SONUCA DÖNÜŞÜYOR. Fırlatmak, sitenin bütün .htaccess raporunu
            // düşürürdü (dosya listesi, bulgular, kip) — oysa kaydı okuyamamak o bilgilerin
            // hiçbirini geçersiz kılmaz. Panel "bilinmiyor" der ve engel olarak GÖSTERİR.
            gunluk.LogWarning(istisna,
                "htaccess: applicationHost.config modul kaydi OKUNAMADI; kayit durumu BILINMIYOR olarak raporlaniyor.");

            return new HtaccessModuleRegistrationState
            {
                ReadError = istisna.Message,
                Assembly = derleme
            };
        }
    }

    public HtaccessModuleRegistrationState Register()
    {
        var derleme = HtaccessModuleAssemblyLocator.Locate();
        if (derleme.Presence != HtaccessModuleAssemblyPresence.Found)
        {
            // KAYDI YAZMAK YERİNE REDDET. Gerekçe sınıf yorumunda: GAC'ta olmayan bir derlemeye
            // işaret eden kayıt, sunucudaki her yonetilen havuzu 500'e düşürür.
            throw new AppException(
                "htaccess.module_assembly_missing",
                409,
                409,
                $"'{HtaccessModuleRegistration.AssemblyName}' derlemesi GAC'ta bulunamadi " +
                $"(durum: {derleme.Presence}). Kaydi yazmak sunucudaki butun yonetilen havuzlari " +
                "500'e dusururdu. Once scripts/install-htaccess-module.ps1 calistirilmali.");
        }

        var nitelikliTip = HtaccessModuleRegistration.QualifiedTypeName(derleme.Version, derleme.PublicKeyToken);

        using var yonetici = CreateServerManager();
        var mevcut = Girdi(yonetici, out var koleksiyon);

        // ÖNCE KALDIR SONRA EKLE — betiğin yaptığının aynısı ve aynı gerekçeyle: aksi hâlde
        // yeniden kurulumda "duplicate collection element" hatası alınır ve ASIL ÖNEMLİSİ,
        // bozuk bir tip/ön koşul GÜNCELLENMEZ. Onarım ucunun bütün amacı bozuk girdiyi
        // düzeltmektir; üstüne yazmayan bir onarım hiçbir işe yaramazdı.
        if (mevcut is not null)
        {
            InvokeMethod(koleksiyon, "Remove", mevcut);
        }

        var yeni = InvokeMethod(koleksiyon, "CreateElement", "add");
        SetAttributeValue(yeni, "name", HtaccessModuleRegistration.ModuleName);
        SetAttributeValue(yeni, "type", nitelikliTip);
        SetAttributeValue(yeni, "preCondition", HtaccessModuleRegistration.PreCondition);
        InvokeMethod(koleksiyon, "Add", yeni);

        InvokeMethod(yonetici, "CommitChanges");

        gunluk.LogInformation(
            "htaccess: modul kaydi applicationHost.config'e YAZILDI (tip={Tip}, onKosul={OnKosul}).",
            nitelikliTip,
            HtaccessModuleRegistration.PreCondition);

        // SONUÇ YENİDEN OKUNUYOR, yazdığımız değerler geri verilmiyor: "yazdım" ile "yazıldı"
        // aynı şey değil. Bu ucun bütün amacı ölçmek olduğu için sonucu da ölçüyoruz.
        return Read();
    }

    /// <summary>Modül girdisi (yoksa <c>null</c>) ve içinde bulunduğu koleksiyon.</summary>
    private static object? Girdi(IDisposable yonetici, out object koleksiyon)
    {
        var yapilandirma = InvokeMethod(yonetici, "GetApplicationHostConfiguration");
        var bolum = InvokeMethod(yapilandirma, "GetSection", ModulesSection);
        koleksiyon = GetConfigurationCollection(bolum);

        return Enumerate(koleksiyon).FirstOrDefault(oge =>
            string.Equals(
                TryGetAttributeString(oge, "name"),
                HtaccessModuleRegistration.ModuleName,
                StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Modül derlemesini GAC'ta ARAR ve kimliğini DERLEMEDEN okur.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden GAC, kurulum klasörü değil:</b> IIS modülü <c>applicationHost.config</c>'e GLOBAL
/// kaydedilir ve yüklenirken güçlü ad üzerinden GAC'tan çözülür (gerekçesi
/// <see cref="HtaccessModuleRegistration.RegistrationScopeRationale"/>). Panelin kurulum
/// klasöründeki kopya IIS için hiçbir anlam taşımaz — oraya bakmak "kurulu" derken yanlış dosyayı
/// ölçmek olurdu.
/// </para>
/// <para>
/// <b>Sürüm ve ortak anahtar klasör adından ÇÖZÜLMÜYOR</b> (<c>v4.0_1.0.0.0__be23...</c> biçimi
/// ayrıştırılabilir görünüyor ama bir biçim VARSAYIMIDIR). Değerler
/// <see cref="AssemblyName.GetAssemblyName(string)"/> ile derlemenin kendisinden okunuyor.
/// </para>
/// </remarks>
internal static class HtaccessModuleAssemblyLocator
{
    private const string GacRootTemplate = "%windir%\\Microsoft.NET\\assembly\\GAC_MSIL";

    internal static HtaccessModuleAssemblyState Locate()
        => Locate(Environment.ExpandEnvironmentVariables(GacRootTemplate));

    /// <summary>
    /// GAC kökü DIŞARIDAN verilebilir — yalnızca ölçülebilirlik için.
    /// </summary>
    /// <remarks>
    /// Gerçek kök makinede TEK bir yerdedir ve testlerde oraya dokunmak imkânsızdır (yönetici
    /// yetkisi, üstelik makineyi kirletirdi). Parametreli aşırı yükleme olmadan bu sınıfın üç
    /// değerli sonucundan yalnız BİRİ ölçülebilirdi — yani "bilinmiyor" dalı hiç sınanmazdı ve
    /// tam da o dal, panelin yalan söylememesini sağlayan dal.
    /// </remarks>
    internal static HtaccessModuleAssemblyState Locate(string gacKoku)
    {
        if (!Directory.Exists(gacKoku))
        {
            // GAC KÖKÜ YOKSA "kurulu degil" DENMEZ. .NET Framework kurulu olmayan ya da alışılmadık
            // bir makinede bu klasör bulunmayabilir; yokluğundan modülün yokluğunu çıkarmak ölçüm
            // değil tahmin olurdu.
            return new HtaccessModuleAssemblyState { Presence = HtaccessModuleAssemblyPresence.Unknown };
        }

        var modulKoku = Path.Combine(gacKoku, HtaccessModuleRegistration.AssemblyName);
        if (!Directory.Exists(modulKoku))
        {
            return new HtaccessModuleAssemblyState { Presence = HtaccessModuleAssemblyPresence.Missing };
        }

        var dosyaAdi = HtaccessModuleRegistration.AssemblyName + ".dll";

        foreach (var surumKlasoru in Directory.EnumerateDirectories(modulKoku))
        {
            var yol = Path.Combine(surumKlasoru, dosyaAdi);
            if (!File.Exists(yol))
            {
                continue;
            }

            try
            {
                var kimlik = AssemblyName.GetAssemblyName(yol);
                var jeton = kimlik.GetPublicKeyToken();
                if (jeton is null || jeton.Length == 0)
                {
                    // GAC'ta güçlü ad olmadan bir derleme bulunamaz; buraya düşmek okuma hatasıdır.
                    continue;
                }

                return new HtaccessModuleAssemblyState
                {
                    Presence = HtaccessModuleAssemblyPresence.Found,
                    Path = yol,
                    Version = kimlik.Version?.ToString() ?? string.Empty,
                    PublicKeyToken = Convert.ToHexString(jeton).ToLowerInvariant()
                };
            }
            catch (Exception istisna) when (istisna is IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                // Okunamayan tek bir sürüm klasörü, diğerlerini ölçmeyi engellemez.
            }
        }

        return new HtaccessModuleAssemblyState { Presence = HtaccessModuleAssemblyPresence.Missing };
    }
}
