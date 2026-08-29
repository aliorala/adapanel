using System;
using System.IO;
using System.Web;
using System.Web.Hosting;
using AdaPanel.Infrastructure.Htaccess.Hosting;

namespace AdaPanel.HtaccessModule;

/// <summary>
/// AdaPanel <c>.htaccess</c> modülü — IIS istek yolundaki giriş noktası.
///
/// ============================ NEDEN BeginRequest ============================
/// ÖLÇÜLMEDİ, BELGEDEN KANITLANDI. IIS entegre yolu bir durum makinesidir ve sırası belgelidir
/// (Microsoft Learn, "IIS 7.0, ASP.NET, pipelines, modules, handlers, and preconditions"):
///
///   BEGIN_REQUEST -> AUTHENTICATE_REQUEST -> AUTHORIZE_REQUEST -> RESOLVE_REQUEST_CACHE
///   -> MAP_REQUEST_HANDLER -> ACQUIRE_REQUEST_STATE -> PRE_EXECUTE_REQUEST_HANDLER
///   -> EXECUTE_REQUEST_HANDLER -> ...
///
/// Aynı belge: "the handler mapping can be changed during request execution in the
/// MAP_REQUEST_HANDLER event. This allows scenarios such as URL rewriting to work."
///
/// Yani isteği hangi işleyicinin (PHP FastCGI mi, StaticFile mi) karşılayacağı
/// MAP_REQUEST_HANDLER'da kesinleşir. .htaccess yeniden yazması bu karardan ÖNCE olmak
/// zorundadır: <c>/urun/5</c> adresini <c>/index.php</c>'ye çevirdiğimizde isteğin PHP
/// işleyicisine gitmesini istiyoruz.
///
/// Bu yüzden <c>PreRequestHandlerExecute</c> (PRE_EXECUTE_REQUEST_HANDLER) YANLIŞ OLURDU:
/// o noktada işleyici ÇOKTAN seçilmiştir; yolu değiştirmek işleyici seçimini değiştirmez ve
/// StaticFileModule <c>/index.php</c>'yi metin olarak sunmaya çalışırdı.
///
/// BeginRequest, doğru olan EN ERKEN olaydır ve erişim reddi (Deny) için de doğrudur:
/// reddedilen bir istekte hiçbir işleyici hiç çalışmamalıdır.
///
/// ============================ NEDEN ÖN KOŞUL managedHandler DEĞİL ============================
/// Bkz. <see cref="HtaccessModuleRegistration.PreCondition"/> — orada belge alıntılarıyla
/// gerekçelendirildi. Özet: managedHandler tam olarak PHP ve statik dosyaları eler; onlar da
/// .htaccess'in bütün amacıdır.
///
/// ============================ FAIL-OPEN ============================
/// Bu dosyadaki HER giriş noktası (Init dahil) yakalanmamış istisna SIZDIRMAZ.
/// <c>Init</c> özellikle önemlidir: orada fırlatılan bir istisna uygulamanın TAMAMINI 500'e
/// düşürür — yani modülün en tehlikeli anı hiç çalışmaya başlamadan öncesidir.
/// </summary>
public sealed class HtaccessHttpModule : IHttpModule
{
    /// <summary>
    /// AppDomain başına durum. STATIC ama İZOLASYONU BOZMAZ: .NET Framework'te her IIS
    /// uygulaması kendi AppDomain'indedir, static alanlar AppDomain başınadır. Bir kiracının
    /// kapısı/önbelleği/devre kesicisi diğerine GÖRÜNMEZ.
    ///
    /// Lazy: <c>Init</c> her uygulama örneği (HttpApplication) için çağrılır — IIS bunlardan
    /// bir HAVUZ tutar (onlarca örnek). Durumu örnek alanında tutsaydık her HttpApplication
    /// kendi önbelleğini kurar ve önbellek isabet oranı çöperdi.
    /// </summary>
    private static readonly object InitGate = new object();
    private static HtaccessRuntimeGate? gate;

    /// <summary>
    /// KORUMALI DİZİN kapısı. <see cref="gate"/>'ten AYRI olması bilinçli ve ölçülmüş:
    /// parola koruması, kullanıcının açıp kapadığı <c>.htaccess</c> anahtarına bağlı OLAMAZ
    /// (o anahtar varsayılan KAPALI'dır). Gerekçenin tamamı <see cref="ProtectedDirectoryGate"/>
    /// sınıf yorumunda.
    /// </summary>
    private static ProtectedDirectoryGate? protectionGate;

    private static HtaccessFileCache<object>? cache;
    private static HtaccessFailSafe? failSafe;
    private static string siteName = string.Empty;
    private static bool initialized;

    /// <summary>Teşhis için: modülün bu havuzda kaç isteğe DOKUNDUĞU.</summary>
    internal static long HandledRequests;

    public void Init(HttpApplication context)
    {
        try
        {
            EnsureInitialized();

            if (context != null)
            {
                context.BeginRequest += OnBeginRequest;
            }
        }
        catch (Exception exception)
        {
            // Init'te fırlatmak = uygulamanın tamamı 500. Modülün var olma sebebi bunu ÖNLEMEK.
            // Yutulan durumda modül basitçe hiçbir olaya abone olmaz -> istekler dokunulmadan akar.
            ModuleLog.Write("htaccess: Init basarisiz - modul bu uygulamada devre disi", exception);
        }
    }

    public void Dispose()
    {
        // Durum static ve AppDomain ömürlü; HttpApplication örneği başına serbest bırakılacak
        // bir kaynak YOK. Boş bırakılması bilinçli.
    }

    private static void EnsureInitialized()
    {
        if (initialized)
        {
            return;
        }

        lock (InitGate)
        {
            if (initialized)
            {
                return;
            }

            var dataRoot = HtaccessHostingPaths.ResolveDataRootFromEnvironment();

            // Site adı IIS'ten okunur. Panel anahtar dosyasını AYNI adla yazar; ikisi de
            // HtaccessHostingPaths.SiteSwitchFileName'i kullanır -> ad eşlemesi tek kaynakta.
            siteName = SafeSiteName();

            // TTL = 2 saniye. Gerekçe ÖLÇÜLDÜ: bu makinede bir dosya damgası okuma ~13,7 us.
            // 2 saniyelik pencerede, saniyede 1.000 istek alan bir sitede istek başına amortize
            // maliyet ~7 ns'dir (sozluk aramasi 48,7 ns'nin altinda). Daha kısa TTL kazanç
            // getirmez; daha uzun TTL "kapatma düğmesi anında" sözünü zayıflatır.
            gate = new HtaccessRuntimeGate(dataRoot, TimeSpan.FromSeconds(2));

            // Korumalı dizinler AYNI TTL ile: panelden "korumayı kaldır" denince klasör iki
            // saniye içinde açılmalı. Daha uzun bir TTL, kullanıcı "kaldırdım" derken klasörün
            // hâlâ kilitli kalması demekti; daha kısası ölçülen bir kazanç getirmez.
            protectionGate = new ProtectedDirectoryGate(dataRoot, TimeSpan.FromSeconds(2));
            cache = new HtaccessFileCache<object>(maxEntries: 512, revalidateAfter: TimeSpan.FromSeconds(2));
            failSafe = new HtaccessFailSafe(
                failureThreshold: 5,
                cooldown: TimeSpan.FromSeconds(60),
                budget: TimeSpan.FromMilliseconds(50),
                log: ModuleLog.Write);

            // ÇEKİRDEK MOTORUN BAĞLANDIĞI TEK SATIR. Uzun süre bu satır YOKTU: motor net8'di ve
            // net48 CLR'ında yüklenemiyordu, dolayısıyla HtaccessEngineRegistry.Current hep null
            // kalıyor ve modül HER isteği dokunmadan geçiriyordu — "kod var, ekranı var, hiç
            // çalışmıyor". Motorun kaynakları artık bu projede de derlendiği için yüklenecek
            // ikinci bir derleme yok (bkz. HtaccessModuleEngineAdapter).
            //
            // Install() fırlatmaz: kurulamazsa kayıt boş kalır ve modül eski (zararsız) davranışına
            // döner. Yani bağlantı, fail-open sözünü BOZMAZ.
            HtaccessModuleEngineAdapter.Install();

            initialized = true;
        }
    }

    private static void OnBeginRequest(object sender, EventArgs e)
    {
        try
        {
            var application = sender as HttpApplication;
            var context = application?.Context;
            if (context == null)
            {
                return;
            }

            // ---- KORUMALI DİZİNLER ----
            // .htaccess kapısından ÖNCE ve ONDAN BAĞIMSIZ. Sebep ölçüldü: .htaccess desteği
            // varsayılan KAPALI ve bu kapının arkasına konulan bir parola koruması, kullanıcı
            // "korumalı" derken klasörü herkese açık bırakırdı. Koruması olmayan sitelerin ödediği
            // bedel, .htaccess kapısıyla aynı sınıfta: bir alan okuması + bir karşılaştırma.
            if (ApplyDirectoryProtection(context))
            {
                // 401 yazıldı ve istek tamamlandı; yolun geri kalanı ÇALIŞMAMALI.
                return;
            }

            // ---- SICAK YOL ----
            // Kapı kapalıysa BURADA biter. Ölçülen maliyet: bir sözlük/alan okuması + bir
            // karşılaştırma. Sunucudaki .htaccess KULLANMAYAN sitelerin ödediği tek bedel budur.
            var currentGate = gate;
            if (currentGate == null)
            {
                return;
            }

            var mode = currentGate.Evaluate(siteName);
            if (mode == HtaccessMode.Off)
            {
                return;
            }

            var engine = HtaccessEngineRegistry.Current;
            if (engine == null)
            {
                // Çekirdek motor henüz bağlanmadı (bkz. IHtaccessEngine). Anahtar açık olsa bile
                // yapacak bir şey yok -> istek dokunulmadan geçer.
                return;
            }

            Apply(context, engine, mode);
        }
        catch (Exception exception)
        {
            // SON SAVUNMA HATTI. Buraya kadar sızan her şey burada durur; istek etkilenmez.
            ModuleLog.Write("htaccess: BeginRequest'te yakalanmamis istisna (istek dokunulmadan gecirildi)", exception);
        }
    }

    /// <summary>
    /// Korumalı dizin kararını uygular. <c>true</c> = istek TAMAMLANDI (401 yazıldı).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>WWW-Authenticate</c> YALNIZCA BURADA, yani yalnızca 401 yanıtında yazılır.</b>
    /// Eski tasarım başlığı <c>web.config</c>'in <c>customHeaders</c> bölümüne koyuyordu ve o
    /// bölüm başlığı HER yanıta ekler — canlıda ölçüldü: parola doğru girildikten sonra dönen
    /// <b>200 yanıtında da</b> başlık gidiyordu. Zararsız bir ayrıntı değil: RFC 9110'a göre
    /// <c>WWW-Authenticate</c> bir MEYDAN OKUMADIR ve 401 dışında anlamı yoktur; gönderilmesi
    /// bazı istemci ve ara belleklerde gereksiz yeniden kimlik sorma turlarına yol açar ve
    /// korunmayan yolları da korumalı gibi gösterir.
    /// </para>
    /// <para>
    /// <c>TrySkipIisCustomErrors</c> AÇILIYOR: açılmasaydı IIS'in özel hata sayfası devreye girip
    /// yanıtı DEĞİŞTİREBİLİRDİ. Durum kodu ve başlık korunur ama gövdenin ne olacağı sunucu
    /// yapılandırmasına kalırdı; kimlik doğrulama akışında bu, tarayıcının parola kutusunu
    /// açmadan hata sayfası göstermesi demek olabilirdi.
    /// </para>
    /// </remarks>
    private static bool ApplyDirectoryProtection(HttpContext context)
    {
        var currentGate = protectionGate;
        if (currentGate == null)
        {
            return false;
        }

        var request = context.Request;
        var response = context.Response;
        var karar = currentGate.Decide(
            siteName,
            SafeDocumentRoot(),
            SafeGet(() => request.PhysicalPath),
            SafeGet(() => request.Path),
            SafeGet(() => request.Headers["Authorization"] ?? string.Empty));

        if (karar.Outcome == ProtectedDirectoryOutcome.Granted)
        {
            // ================= ÇEKİRDEK ÖNBELLEĞİ — CANLIDA ÖLÇÜLMÜŞ BİR AÇIK =================
            // Korunan bir statik dosya doğru parolayla bir kez sunulduğunda IIS o yanıtı
            // HTTP.SYS ÇEKİRDEK ÖNBELLEĞİNE alıyor. Sonraki istekler yönetilen ardışık düzene
            // HİÇ GİRMİYOR — yani bu modül çalışmıyor — ve dosya PAROLASIZ dönüyor.
            //
            // ÖLÇÜM (bu makinede, test.local üzerinde): parolayla bir kez alınan
            // /ada-gizli/index.html, ardından KİMLİKSİZ beş istekte de HTTP 200 döndü; aynı anda
            // sorgu dizesi eklenmiş HALİ (önbellekte olmayan URL) HTTP 401 döndü. Yani koruma
            // çalışıyordu, önbellek onu ATLIYORDU.
            //
            // Düzeltme, korumalı dizinden dönen yanıtları ÖNBELLEKLENEMEZ işaretlemektir; başka
            // yol yok, çünkü modül önbellekten sunulan isteği hiç görmüyor. Bedeli, korumalı
            // dizinlerde çekirdek önbelleğinin kapalı olmasıdır — parola arkasındaki içerik zaten
            // paylaşılabilir bir yanıt değildir.
            //
            // İKİSİ BİRDEN gerekli: kullanıcı kipi (ASP.NET çıktı önbelleği) ve çekirdek kipi
            // (http.sys) AYRI önbelleklerdir; yalnız birini kapatmak diğerinden sunulmayı engellemez.
            SafeGet(() =>
            {
                response.DisableKernelCache();
                response.DisableUserCache();
                return string.Empty;
            });

            return false;
        }

        if (karar.Outcome != ProtectedDirectoryOutcome.Challenge)
        {
            return false;
        }

        response.StatusCode = 401;
        response.StatusDescription = "Unauthorized";
        response.AddHeader("WWW-Authenticate", "Basic realm=\"" + karar.Realm + "\", charset=\"UTF-8\"");
        response.TrySkipIisCustomErrors = true;

        // Response.End() KULLANILMIYOR: ThreadAbortException fırlatır ve bu modülün
        // "istisna üretme" yasağını ihlal ederdi (aynı gerekçe Enforce'ta da yazılı).
        context.ApplicationInstance.CompleteRequest();
        HandledRequests++;
        return true;
    }

    private static void Apply(HttpContext context, IHtaccessEngine engine, HtaccessMode mode)
    {
        var currentCache = cache;
        var currentFailSafe = failSafe;
        if (currentCache == null || currentFailSafe == null)
        {
            return;
        }

        var documentRoot = SafeDocumentRoot();
        if (documentRoot.Length == 0)
        {
            return;
        }

        var htaccessPath = Path.Combine(documentRoot, ".htaccess");

        // Derleme fail-open: bozuk bir .htaccess istisna fırlatırsa yedek değer null olur ve
        // istek dokunulmadan geçer.
        var compiled = currentFailSafe.Execute(
            () => currentCache.GetOrLoad(htaccessPath, (path, _) => engine.Compile(path)),
            null,
            "derleme",
            out _);

        if (compiled == null)
        {
            return;
        }

        var info = BuildRequestInfo(context, documentRoot);

        var decision = currentFailSafe.Execute(
            () => engine.Evaluate(info, compiled) ?? HtaccessDecision.None,
            HtaccessDecision.None,
            "degerlendirme",
            out _);

        if (decision.Kind == HtaccessActionKind.None)
        {
            return;
        }

        if (mode == HtaccessMode.Observe)
        {
            // GÖZLEM MODU: karar HESAPLANDI ama UYGULANMADI. Kademeli açılışın anlamı budur --
            // yönetici gerçek trafikte ne olacağını, hiçbir isteği bozmadan görür.
            ModuleLog.Write(
                "htaccess: [gozlem] " + decision.Kind + " -> " + (decision.Target ?? "-") + " (yol=" + info.VirtualPath + ")",
                null);
            return;
        }

        currentFailSafe.Execute<object?>(
            () =>
            {
                Enforce(context, decision);
                return null;
            },
            null,
            "uygulama",
            out _);

        HandledRequests++;
    }

    private static void Enforce(HttpContext context, HtaccessDecision decision)
    {
        switch (decision.Kind)
        {
            case HtaccessActionKind.Rewrite:
                if (!string.IsNullOrEmpty(decision.Target))
                {
                    // rebaseClientPath: false — BELGEDEN. HttpContext.RewritePath(String) varsayılan
                    // olarak istemci yolunu da yeniden temellendirir; bu, sayfadaki göreli
                    // bağlantıların (css/js/img) çözüldüğü kökü DEĞİŞTİRİR ve /urun/5 -> /index.php
                    // gibi bir yeniden yazmada sitenin bütün varlıklarını kırardı.
                    //
                    // NOT: RewritePath, hedef uygulama kökünün DIŞINDAYSA HttpException fırlatır
                    // (belgede yazılı). Bozuk bir .htaccess bunu kolayca üretebilir; bu yüzden çağrı
                    // fail-open sarmalayıcısının İÇİNDEN yapılıyor ve istisna isteği bozmuyor.
                    context.RewritePath(decision.Target!, false);
                }

                break;

            case HtaccessActionKind.Redirect:
                var status = decision.StatusCode > 0 ? decision.StatusCode : 302;
                context.Response.StatusCode = status;
                if (!string.IsNullOrEmpty(decision.Target))
                {
                    context.Response.RedirectLocation = decision.Target;
                }

                // CompleteRequest: yolun geri kalanı atlanır, işleyici HİÇ çalışmaz.
                // Response.End() KULLANILMIYOR -- o, ThreadAbortException fırlatarak isteği keser
                // ve tam olarak "istisna üretme" yasağının ihlali olurdu.
                context.ApplicationInstance.CompleteRequest();
                break;

            case HtaccessActionKind.Deny:
                context.Response.StatusCode = decision.StatusCode > 0 ? decision.StatusCode : 403;
                context.ApplicationInstance.CompleteRequest();
                break;
        }
    }

    private static HtaccessRequestInfo BuildRequestInfo(HttpContext context, string documentRoot)
    {
        var request = context.Request;
        return new HtaccessRequestInfo
        {
            VirtualPath = SafeGet(() => request.Path),
            QueryString = SafeGet(() => request.QueryString.ToString()),
            HttpMethod = SafeGet(() => request.HttpMethod),
            PhysicalPath = SafeGet(() => request.PhysicalPath),
            DocumentRoot = documentRoot,
            RemoteAddress = SafeGet(() => request.UserHostAddress ?? string.Empty),
            IsSecure = SafeGetBool(() => request.IsSecureConnection),

            // Url.Host, Headers["Host"] DEĞİL: başlık portu da içerir ("ornek.com:8080") ve
            // %{HTTP_HOST} karşılaştırmalarını sessizce bozardı. Url.Host portu ayırmış hâlini verir.
            Host = SafeGet(() => request.Url?.Host ?? string.Empty)
        };
    }

    /// <summary>
    /// Site adı. <c>HostingEnvironment.SiteName</c> IIS site adını verir; panel anahtar dosyasını
    /// aynı adla yazar. Okunamazsa BOŞ döner ve kapı bunu <see cref="HtaccessMode.Off"/> sayar —
    /// yani hangi kiracı olduğumuzu bilmiyorsak hiçbir kural uygulamayız.
    /// </summary>
    private static string SafeSiteName() => SafeGet(() => HostingEnvironment.SiteName ?? string.Empty);

    private static string SafeDocumentRoot()
        => SafeGet(() => HostingEnvironment.ApplicationPhysicalPath ?? string.Empty);

    private static string SafeGet(Func<string> read)
    {
        try
        {
            return read() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool SafeGetBool(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return false;
        }
    }
}
