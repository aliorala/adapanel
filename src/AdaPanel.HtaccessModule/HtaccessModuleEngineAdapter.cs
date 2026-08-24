using System;
using AdaPanel.Infrastructure.Htaccess;
using AdaPanel.Infrastructure.Htaccess.Hosting;

namespace AdaPanel.HtaccessModule;

/// <summary>
/// ÇEKİRDEK MOTORU IIS MODÜLÜNE BAĞLAYAN UYARLAYICI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bu sınıf, uzun süre boş kalan kancanın (<see cref="IHtaccessEngine"/>) gerçek uygulamasıdır.</b>
/// Bağlantı bugüne kadar FİZİKSEL OLARAK yapılamıyordu: motor <c>net8.0-windows</c>, bu modül
/// <c>net48</c> ve bir net8 derlemesi CLR 4 içinde yüklenemez. Çözüm motoru "başka bir derleme"
/// olmaktan çıkarmak oldu — motorun <c>.cs</c> dosyaları bu projede DE derleniyor
/// (bkz. csproj'daki "CEKIRDEK MOTOR" bloğu), yani burada çağrılan <c>HtaccessEngine</c> bu
/// derlemenin İÇİNDEDİR ve yüklenecek ikinci bir derleme yoktur.
/// </para>
/// <para>
/// <b>SINIF BİLEREK ÇOK İNCE.</b> Bütün karar mantığı <see cref="HtaccessEngineBridge"/>'dedir ve
/// o dosya net8 tarafında da derlendiği için testlerden koşar. Burada kalan tek iş, köprünün
/// hedeften bağımsız kararını modülün (<c>System.Web</c>'e bakan) tiplerine kopyalamaktır.
/// Karar mantığı buraya taşınsaydı tek satırı bile test edilemezdi.
/// </para>
/// <para>
/// <b>FAIL-OPEN ÜÇÜNCÜ KEZ.</b> Köprü zaten fırlatmıyor, modülün <c>HtaccessFailSafe</c>'i de
/// yakalıyor; buradaki <c>try</c> gereksiz DEĞİL: bu sınıf iki katmanın ARASINDADIR ve kendi
/// alan kopyalaması (örneğin beklenmedik bir enum değeri) hata verirse, o hata devre kesici
/// sayacına yazılıp motoru sağlam olan bir sitede özelliği 60 saniyeliğine kapatırdı.
/// </para>
/// </remarks>
public sealed class HtaccessModuleEngineAdapter : IHtaccessEngine
{
    private readonly HtaccessEngineBridge _kopru;

    /// <summary>Üretimde kullanılan kurucu: gerçek motoru kurar.</summary>
    public HtaccessModuleEngineAdapter()
        : this(new HtaccessEngine())
    {
    }

    internal HtaccessModuleEngineAdapter(AdaPanel.Core.Interfaces.IHtaccessEngine motor)
        => _kopru = new HtaccessEngineBridge(motor);

    /// <summary>
    /// Motoru kayıt yerine YERLEŞTİRİR. <c>HtaccessHttpModule.Init</c> bunu çağırır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden idempotent ve neden "zaten varsa dokunma":</b> <c>Init</c> her
    /// <c>HttpApplication</c> örneği için çağrılır ve IIS bunlardan bir havuz tutar (onlarca
    /// örnek). Her çağrıda yeni bir motor kurmak, motorun kendi ayrıştırma önbelleğini (dosya
    /// damgalı) sürekli sıfırdan doldurmak demekti.
    /// </para>
    /// <para>
    /// <b>İSTİSNA FIRLATMAZ.</b> Motorun kurucusu bugün fırlatmıyor ama fırlatsaydı bu, uygulamanın
    /// TAMAMINI 500'e düşürürdü — modülün var olma sebebi tam olarak bunu önlemek.
    /// Kurulamazsa kayıt boş kalır ve modül her isteği dokunmadan geçirir.
    /// </para>
    /// </remarks>
    public static void Install()
    {
        try
        {
            if (HtaccessEngineRegistry.Current == null)
            {
                HtaccessEngineRegistry.Current = new HtaccessModuleEngineAdapter();
            }
        }
        catch (Exception exception)
        {
            ModuleLog.Write("htaccess: motor kurulamadi - modul istekleri dokunmadan gecirecek", exception);
        }
    }

    /// <summary>
    /// Bir <c>.htaccess</c> dosyası için derleme belirteci üretir.
    /// </summary>
    /// <remarks>
    /// Motorun kendi ayrıştırma önbelleği var (dosya damgası + boyut ile), bu yüzden burada ikinci
    /// bir derleme YAPILMAZ — iki önbellek bir gün ayrı düşerdi ve "panel kaydetti ama site eski
    /// kuralla çalışıyor" tam olarak o arızadır. Dönen değer yalnız belge kökünü taşır;
    /// <c>null</c> dönmek modülde "isteğe dokunma" anlamına gelir.
    /// </remarks>
    public object? Compile(string htaccessPath) => _kopru.Prepare(htaccessPath);

    /// <summary>Kararı üretir. Bkz. sınıf yorumu: HİÇBİR KOŞULDA fırlatmaz.</summary>
    public HtaccessDecision Evaluate(HtaccessRequestInfo request, object compiled)
    {
        try
        {
            if (request == null)
            {
                return HtaccessDecision.None;
            }

            var karar = _kopru.Decide(
                compiled as string,
                request.DocumentRoot,
                request.VirtualPath,
                request.QueryString,
                request.HttpMethod,
                request.RemoteAddress,
                request.IsSecure,
                request.Host);

            if (karar == null || karar.Kind == HtaccessBridgeActionKind.None)
            {
                return HtaccessDecision.None;
            }

            return new HtaccessDecision
            {
                // (int) DÖNÜŞÜMÜ GÜVENLİ AMA DENETİMSİZ: iki enum ayrı derleme birimlerinde
                // tanımlı, dolayısıyla eşitliklerini derleyici KONTROL EDEMEZ. Sayısal eşitlik
                // HtaccessEngineBridgeTests tarafından, modülün KAYNAK DOSYASI okunarak ölçülüyor.
                Kind = (HtaccessActionKind)(int)karar.Kind,
                Target = karar.Target,
                StatusCode = karar.StatusCode
            };
        }
        catch (Exception exception)
        {
            ModuleLog.Write("htaccess: uyarlayici karari cevirirken hata verdi (istek dokunulmadan gecti)", exception);
            return HtaccessDecision.None;
        }
    }
}
