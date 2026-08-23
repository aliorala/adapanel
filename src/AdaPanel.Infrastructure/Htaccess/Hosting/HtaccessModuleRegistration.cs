using System;

namespace AdaPanel.Infrastructure.Htaccess.Hosting;

/// <summary>
/// Modülün IIS'e KAYIT KİMLİĞİ — tek kaynak.
///
/// Bu değerler ÜÇ yerde birden geçerlidir: kurulum betiği (applicationHost.config'e yazan),
/// panel teşhisi (kayıt var mı diye bakan) ve modülün kendi tip adı. Üçü elle yazılsaydı biri
/// değişip diğerleri kalırdı ve sonuç sessiz olurdu: panel "kurulu" der, IIS başka bir ad arar.
/// Bu yüzden burada tanımlanır, betikle eşitliği <c>HtaccessHostingRegistrationTests</c> ile
/// NÖBETÇİ TEST altındadır.
/// </summary>
internal static class HtaccessModuleRegistration
{
    /// <summary>IIS <c>&lt;modules&gt;</c> koleksiyonundaki benzersiz ad.</summary>
    internal const string ModuleName = "AdaPanelHtaccessModule";

    internal const string AssemblyName = "AdaPanel.HtaccessModule";

    internal const string TypeNamespace = "AdaPanel.HtaccessModule";

    internal const string TypeName = TypeNamespace + ".HtaccessHttpModule";

    /// <summary>
    /// Güçlü ad ORTAK ANAHTARI. GAC'a kurulabilmek için derleme güçlü adlı olmak ZORUNDA
    /// (bkz. <c>src/AdaPanel.HtaccessModule/AdaPanel.HtaccessModule.snk</c> ve csproj).
    /// Anahtar değişirse burası da değişmeli; nöbetçi test derlemeden OKUR, buradan tahmin ETMEZ.
    /// </summary>
    internal const string PublicKeyTokenPlaceholder = "<derlemeden-okunur>";

    /// <summary>
    /// <b>ÖN KOŞUL — bu satır bu projenin en kritik tek satırıdır.</b>
    ///
    /// <c>runtimeVersionv4.0</c> ve BAŞKA HİÇBİR ŞEY. Gerekçeler, Microsoft belgelerinden:
    ///
    /// 1) <c>managedHandler</c> KULLANILMIYOR — kullanılsaydı ÖZELLİK HİÇ ÇALIŞMAZDI.
    ///    Belge (modules/add): "Specify the managedHandler value when the module should process
    ///    requests only for managed resources, such as .aspx files, and should not respond to
    ///    requests for non-managed resources, such as .html files."
    ///    Belge (IIS Modules Overview): "If you remove the attribute precondition='managedHandler',
    ///    Forms Authentication also applies to content that is not served by managed handlers,
    ///    such as .html, .jpg, .doc, but also for classic ASP (.asp) or PHP (.php) extensions."
    ///    .htaccess'in bütün amacı PHP ve STATİK dosyalardır. managedHandler tam da onları eler.
    ///
    /// 2) <c>runAllManagedModulesForAllRequests="true"</c> KULLANILMIYOR — aynı sonucu verirdi ama
    ///    bedeli kabul edilemez. Belge (modules): "When you use this property, the 'managedHandler'
    ///    precondition has no effect and all managed modules run for all requests."
    ///    YANİ: yalnız bizim modülümüz değil, o uygulamadaki BÜTÜN yönetilen modüller (Session,
    ///    FormsAuthentication, RoleManager, Profile...) her .jpg ve .css isteği için de koşardı.
    ///    Bir müşterinin sitesinin davranışını ve performansını, bizim özelliğimiz uğruna
    ///    değiştirmek olurdu. Ön koşulu YALNIZ KENDİ girdimizden düşürmek, belgenin de önerdiği yol.
    ///
    /// 3) <c>runtimeVersionv4.0</c> VAR — çünkü modül net48 (CLR 4) derlemesidir.
    ///    Belge (modules/add): "Specify the runtimeVersionv4.0 value when the module should respond
    ///    only to requests in application pools that are configured to use .NET Framework version 4.0."
    ///    Bu, İKİ ayrı felaketi birden önler:
    ///      a) v2.0 havuzları: CLR 4 derlemesi CLR 2 havuzunda YÜKLENEMEZ.
    ///      b) "Yönetilen kod yok" havuzları: <c>AppRuntimeService.EnsureApplicationPoolUsesNoManagedCode</c>
    ///         Node/Python siteleri için havuzu tam olarak bu duruma çeviriyor. Ön koşul olmasaydı,
    ///         global kayıt o havuzlarda da denenirdi. Ön koşulla IIS girdiyi ATLAR.
    ///
    /// 4) <c>bitness32</c>/<c>bitness64</c> YOK — derleme AnyCPU. Bitness yazmak, PHP için çok yaygın
    ///    olan 32-bit havuzlarda (enable32BitAppOnAppPool=true) modülü sessizce devre dışı bırakırdı.
    /// </summary>
    internal const string PreCondition = "runtimeVersionv4.0";

    /// <summary>
    /// Nitelikli tip adı. Sürüm/anahtar çalışma zamanında derlemeden okunur; burada elle
    /// yazılmaz (deponun "tahmin yasak" kuralı).
    /// </summary>
    internal static string QualifiedTypeName(string assemblyVersion, string publicKeyToken)
        => TypeName + ", " + AssemblyName +
           ", Version=" + assemblyVersion +
           ", Culture=neutral, PublicKeyToken=" + publicKeyToken;

    /// <summary>
    /// <b>GLOBAL KAYIT SEÇİLDİ, SİTE BAŞINA web.config KAYDI ELENDİ.</b> Gerekçe ölçülmüştür:
    ///
    /// Depoda site <c>web.config</c>'ine yazan servisler (bu turda SAYILDI — görev tanımındaki
    /// "beş" bir EKSİK SAYIMDIR, gerçek sayı en az on birdir):
    ///   SiteAliasService, SiteHostingSettingsService, WebAppPublisher, MailAutodiscoverPublisher,
    ///   MailAutodiscoverRewriteRules, MailJmapProxyRules, ReverseProxyService, WebDbClientService,
    ///   WafService, AppRuntimeService, DirectoryProtectionService, SiteBuilderService, AppStoreService.
    ///
    /// Hepsi AYNI deseni kullanıyor: <c>XDocument.Load(..., PreserveWhitespace)</c> → hedef
    /// elemanı cerrahi olarak değiştir → <c>Save</c>. Hiçbiri <c>system.webServer</c>'ı komple
    /// değiştirmiyor. Dolayısıyla eleman KAYBI riski yok.
    ///
    /// AMA GERÇEK ÇAKIŞMA BU DEĞİL: <b>hiçbirinde dosya başına kilit YOK</b> (ölçüldü —
    /// <c>SemaphoreSlim</c>/<c>Mutex</c>/<c>lock</c> taraması bu servislerin hiçbirini döndürmüyor).
    /// İki servis aynı web.config'i aynı anda oku-değiştir-yaz yaparsa SON YAZAN KAZANIR ve
    /// diğerinin değişikliği SESSİZCE KAYBOLUR. Bu, depoda ZATEN VAR OLAN bir hata sınıfı;
    /// altıncı (aslında on dördüncü) yazar olmak onu ağırlaştırmaktan başka bir şey yapmazdı.
    ///
    /// ÇÖZÜM: <b>yazar olmamak.</b> Modül <c>applicationHost.config</c>'e BİR KEZ, kurulumda
    /// kaydedilir; site başına açma/kapama web.config'e değil ANAHTAR DOSYASINA yazılır.
    /// Kazanılanlar:
    ///   - Müşterinin web.config'ine hiç dokunulmaz → o dosyayı bozma riski SIFIR.
    ///   - Site açma/kapama yapılandırma yazımı DEĞİL → uygulama yeniden başlamaz, anında etki eder.
    ///   - Kurtarma tek satırlık: applicationHost.config'ten girdiyi kaldır, sunucu temizlenir.
    ///
    /// BEDELİ (dürüstçe): (a) derleme GAC'a kurulmak zorunda — global kayıt için Microsoft'un
    /// belgelediği yol budur; (b) applicationHost.config'i düzenlemek TÜM havuzları bir kez geri
    /// döndürür — kurulumda bir kez olur; (c) modül tipi yönetilen kodu olan HER havuzda yüklenir —
    /// kapı kapalıyken istek başına maliyeti ÖLÇÜLDÜ ve sözlük aramasından ucuzdur.
    /// </summary>
    internal const string RegistrationScopeRationale = "applicationHost.config (global) — bkz. yukarıdaki gerekçe.";

    /// <summary>
    /// Bir sitenin modülü ÇALIŞTIRABİLİP çalıştıramayacağı. Panel bunu kullanıcıya SÖYLEMEK
    /// zorunda: sebebi bilinmeyen "çalışmıyor" en pahalı destek biletidir.
    /// </summary>
    internal static HtaccessSupport ClassifySupport(string? managedRuntimeVersion)
    {
        var value = (managedRuntimeVersion ?? string.Empty).Trim();

        if (value.Length == 0)
        {
            // "Yönetilen kod yok". AppRuntimeService.cs:466 Node/Python siteleri için havuzu
            // BİLEREK bu duruma çeviriyor -> CLR yok -> yönetilen modül YÜKLENEMEZ. Bu bir hata
            // değil, mimari bir sınır; panel bunu böyle söylemeli.
            return HtaccessSupport.UnsupportedNoManagedCode;
        }

        if (value.StartsWith("v4", StringComparison.OrdinalIgnoreCase))
        {
            return HtaccessSupport.Supported;
        }

        // v2.0 havuzu: CLR 2 içinde CLR 4 derlemesi yüklenemez. Ön koşul zaten atlatır.
        return HtaccessSupport.UnsupportedRuntimeVersion;
    }
}

internal enum HtaccessSupport
{
    Supported = 0,

    /// <summary>Havuz "yönetilen kod yok" — Node/Python siteleri. Modül YÜKLENMEZ.</summary>
    UnsupportedNoManagedCode = 1,

    /// <summary>Havuz v4 dışı bir CLR sürümünde.</summary>
    UnsupportedRuntimeVersion = 2
}
