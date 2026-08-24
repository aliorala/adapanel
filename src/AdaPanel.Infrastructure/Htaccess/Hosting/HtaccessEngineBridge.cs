using System;
using System.Collections.Generic;
using System.IO;
using AdaPanel.Core.Dtos.Htaccess;
using AdaPanel.Core.Interfaces;

// AD ÇAKIŞMASI GERÇEK: bu ad alanında (Hosting) zaten bir HtaccessOutcome var —
// HtaccessFailSafe'in "Succeeded/Faulted/Slow/ShortCircuited" sonucu. Aynı ad alanındaki tip,
// using ile gelen tipi EZER; takma ad olmasaydı aşağıdaki switch sessizce YANLIŞ enum'a bakardı
// (ve "Rewritten yok" diye derleme hatası verirdi -- bu kez şanslıyız, ama kural: ezilme riski
// olan yerde tip ADIYLA değil TAKMA ADIYLA anılır).
using CoreOutcome = AdaPanel.Core.Dtos.Htaccess.HtaccessOutcome;

namespace AdaPanel.Infrastructure.Htaccess.Hosting;

/// <summary>
/// Köprünün ürettiği karar TÜRÜ. Barındırma modülündeki
/// <c>AdaPanel.HtaccessModule.HtaccessActionKind</c> ile SAYISAL OLARAK aynıdır.
/// </summary>
/// <remarks>
/// <b>Neden ikinci bir enum:</b> bu dosya net8 tarafında da derleniyor ve orada modülün tipleri
/// YOKTUR (modül net48, System.Web'e bağlı). Köprü mantığının testlerden koşabilmesi için
/// modülden bağımsız bir karar tipi gerekiyordu. İki enumun sayısal eşitliği tahmine bırakılmadı:
/// <c>HtaccessEngineBridgeTests</c> modülün kaynak dosyasını OKUYUP karşılaştırıyor, yani biri
/// değişirse test kırılır.
/// </remarks>
internal enum HtaccessBridgeActionKind
{
    None = 0,
    Rewrite = 1,
    Redirect = 2,
    Deny = 3
}

/// <summary>Köprünün bir istek için verdiği karar (barındırma tiplerinden arınmış).</summary>
internal sealed class HtaccessBridgeDecision
{
    internal static readonly HtaccessBridgeDecision None = new HtaccessBridgeDecision();

    internal HtaccessBridgeActionKind Kind = HtaccessBridgeActionKind.None;

    /// <summary>Rewrite için sanal yol (<c>/index.php?x=1</c>), Redirect için hedef adres.</summary>
    internal string? Target;

    internal int StatusCode;
}

/// <summary>
/// ÇEKİRDEK MOTOR ile IIS MODÜLÜ ARASINDAKİ KÖPRÜ.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı bir sınıf, doğrudan uyarlayıcı değil:</b> modülün gördüğü uyarlayıcı
/// (<c>HtaccessModuleEngineAdapter</c>) yalnız net48'de derlenebiliyor — <c>IHtaccessEngine</c>
/// portu modül derlemesinde tanımlı. Bütün KARAR mantığı orada kalsaydı, tek satırı bile
/// test edilemezdi (test projesi net8). Bu yüzden mantık burada: motoru çağıran, sonucu karara
/// çeviren ve HİÇBİR KOŞULDA fırlatmayan kısım net8 testlerinden koşuyor; net48'de kalan tek şey
/// alan kopyalayan yirmi satır.
/// </para>
/// <para>
/// <b>KÖPRÜNÜN KENDİSİ DE FAIL-OPEN.</b> Barındırma katmanının <c>HtaccessFailSafe</c>'i zaten
/// istisna yutuyor; bu ikinci katman gereksiz DEĞİL, çünkü fail-safe bir arızayı devre kesici
/// sayacına yazar ve beş üst üste arızada modülü o havuzda 60 saniye kapatır. Köprünün kendi
/// hatası (null motor, bozuk sonuç nesnesi, beklenmedik <c>Outcome</c>) yüzünden devrenin açılması,
/// motorun sağlam olduğu bir sitede özelliği sessizce öldürürdü. Burada yakalanan hata isteği de
/// bozmaz, devreyi de açmaz — yalnızca "karar yok" der.
/// </para>
/// <para>
/// <b>Bütçelere DOKUNULMADI:</b> köprü <see cref="HtaccessLimits"/> vermiyor, yani motor
/// <c>HtaccessLimits.Default</c> ile çalışıyor (desen zaman aşımı, kural sayısı, iç döngü ve süre
/// bütçeleri). Barındırma katmanının 50 ms'lik yavaş-çağrı bütçesi bunun ÜSTÜNDE ayrı bir
/// katmandır; köprü ikisinin arasına girmez.
/// </para>
/// <para>
/// <b>İZOLASYON:</b> bu sınıfın static alanı YOKTUR. Her AppDomain (yani her IIS uygulaması)
/// kendi örneğini kurar; bir kiracının motoru/önbelleği diğerine görünmez.
/// </para>
/// </remarks>
internal sealed class HtaccessEngineBridge
{
    private readonly IHtaccessEngine? _motor;

    internal HtaccessEngineBridge(IHtaccessEngine? motor) => _motor = motor;

    /// <summary>
    /// Modülün <c>Compile</c> çağrısının karşılığı: dosyayı DERLEMEZ, yalnız değerlendirilecek
    /// belge kökünü taşıyan bir belirteç üretir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden derlemiyor:</b> motorun ayrıştırma önbelleği kendi içindedir
    /// (<c>HtaccessCascadeLoader</c>, dosya damgası + boyut ile). İkinci bir derleme adımı koymak
    /// aynı işi iki yerde önbelleklemek olurdu ve ikisi bir gün ayrı düşerdi — panel "kaydedildi"
    /// derken sitenin eski kuralla çalışması tam olarak bu sınıftan bir arızadır.
    /// </para>
    /// <para>
    /// <b>Belirteç yine de gerekli:</b> modül <c>Compile</c> <c>null</c> dönerse isteği
    /// DOKUNMADAN geçiriyor ve derleme sonucunu <c>HtaccessFileCache</c>'te tutuyor. O önbellek
    /// dosya YOKKEN yükleyiciyi hiç çağırmıyor (yokluk da önbelleklenir), yani belirteç doğal
    /// olarak "kökte .htaccess VAR" anlamına gelir.
    /// </para>
    /// <para>
    /// <b>DÜRÜST SINIR:</b> modül belge kökündeki <c>.htaccess</c>'e bakıyor. Kökte dosya yoksa
    /// alt dizinlerdeki <c>.htaccess</c> dosyaları da UYGULANMAZ. Bu barındırma katmanının kararı;
    /// köprü onu değiştiremez, ama panel bunu kullanıcıya SÖYLER
    /// (<c>HtaccessSiteStatus.HasRootFile</c>).
    /// </para>
    /// </remarks>
    internal string? Prepare(string? htaccessPath)
    {
        try
        {
            if (_motor is null || string.IsNullOrEmpty(htaccessPath))
            {
                return null;
            }

            var dizin = Path.GetDirectoryName(htaccessPath);
            return string.IsNullOrEmpty(dizin) ? null : dizin;
        }
        catch (Exception)
        {
            // Bozuk yol (geçersiz karakter, çok uzun) -> karar yok. İstek dokunulmadan geçer.
            return null;
        }
    }

    /// <summary>
    /// İsteği motora sorar ve barındırma katmanının uygulayabileceği karara çevirir.
    /// <b>İSTİSNA FIRLATMAZ.</b>
    /// </summary>
    /// <param name="belgeKoku">
    /// Sitenin belge kökü. <see cref="Prepare"/>'in dönüşü buraya geçer; boşsa
    /// <paramref name="modulBelgeKoku"/> kullanılır.
    /// </param>
    internal HtaccessBridgeDecision Decide(
        string? belgeKoku,
        string? modulBelgeKoku,
        string? sanalYol,
        string? sorgu,
        string? yontem,
        string? uzakAdres,
        bool guvenliMi,
        string? host)
    {
        try
        {
            var motor = _motor;
            if (motor is null)
            {
                return HtaccessBridgeDecision.None;
            }

            var kok = string.IsNullOrEmpty(belgeKoku) ? modulBelgeKoku : belgeKoku;
            if (string.IsNullOrEmpty(kok))
            {
                return HtaccessBridgeDecision.None;
            }

            var istek = new HtaccessRequestContext
            {
                SiteRootPath = kok!,
                RequestPath = string.IsNullOrEmpty(sanalYol) ? "/" : sanalYol!,
                QueryString = sorgu ?? string.Empty,
                Method = string.IsNullOrEmpty(yontem) ? "GET" : yontem!,
                HttpHost = host ?? string.Empty,
                ServerName = host ?? string.Empty,
                ServerPort = guvenliMi ? 443 : 80,
                IsHttps = guvenliMi,
                RemoteAddress = uzakAdres ?? string.Empty,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };

            var sonuc = motor.Evaluate(istek);
            return Cevir(sonuc);
        }
        catch (Exception)
        {
            // Motor SÖZLEŞMESİ GEREĞİ fırlatmaz (her üyesi en dışta yakalar). Buraya düşmek
            // sözleşmenin ihlali demektir — ama bunun bedelini müşterinin sitesi ÖDEMEZ.
            return HtaccessBridgeDecision.None;
        }
    }

    /// <summary>
    /// Motorun sonucunu barındırma kararına çevirir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bilinmeyen/başarısız her sonuç <see cref="HtaccessBridgeActionKind.None"/>'dur.</b>
    /// <c>switch</c>'in varsayılan dalı "hiçbir şey yapma"dır: motora ileride yeni bir
    /// <c>Outcome</c> eklendiğinde köprü onu YANLIŞ uygulamak yerine hiç uygulamaz.
    /// </para>
    /// <para>
    /// <b><c>AuthenticationRequired</c> neden 401 DEĞİL:</b> 401 dönmek
    /// tek başına bir yalan olurdu — tarayıcı <c>WWW-Authenticate</c> başlığı olmadan kimlik
    /// sormaz, kullanıcı yalnızca boş bir "Unauthorized" görür ve içeri BİR DAHA giremez. Başlığı
    /// yazmak ve gelen kimlik bilgisini <c>IHtaccessEngine.Authenticate</c>'e vermek barındırma
    /// katmanında ayrı bir akış gerektiriyor (<c>HtaccessHttpModule</c> bugün yalnız
    /// Rewrite/Redirect/Deny uyguluyor). Yarım bir kimlik doğrulama, korunan dizini KİLİTLERDİ.
    /// Bu yüzden karar üretilmiyor ve panel bu durumu bulgu olarak gösteriyor.
    /// </para>
    /// </remarks>
    private static HtaccessBridgeDecision Cevir(HtaccessEvaluationResult? sonuc)
    {
        if (sonuc is null || sonuc.EngineFailed)
        {
            return HtaccessBridgeDecision.None;
        }

        switch (sonuc.Outcome)
        {
            case CoreOutcome.Rewritten:
                var hedef = Birlestir(sonuc.RewrittenPath, sonuc.RewrittenQuery);
                return hedef.Length == 0
                    ? HtaccessBridgeDecision.None
                    : new HtaccessBridgeDecision
                    {
                        Kind = HtaccessBridgeActionKind.Rewrite,
                        Target = hedef
                    };

            case CoreOutcome.Redirect:
                if (string.IsNullOrEmpty(sonuc.RedirectLocation))
                {
                    // Hedefsiz yönlendirme = tarayıcıya "başka yere git" deyip nereye olduğunu
                    // söylememek. Uygulanırsa sayfa ölür; uygulanmazsa yalnız kural çalışmaz.
                    return HtaccessBridgeDecision.None;
                }

                return new HtaccessBridgeDecision
                {
                    Kind = HtaccessBridgeActionKind.Redirect,
                    Target = sonuc.RedirectLocation,
                    StatusCode = sonuc.StatusCode > 0 ? sonuc.StatusCode : 302
                };

            case CoreOutcome.Forbidden:
            case CoreOutcome.AccessDenied:
                return new HtaccessBridgeDecision
                {
                    Kind = HtaccessBridgeActionKind.Deny,
                    StatusCode = sonuc.StatusCode > 0 ? sonuc.StatusCode : 403
                };

            case CoreOutcome.Gone:
                // [G] bayrağı 410'dur ve modülün Deny dalı durum kodunu OLDUĞU GİBİ yazar.
                return new HtaccessBridgeDecision
                {
                    Kind = HtaccessBridgeActionKind.Deny,
                    StatusCode = sonuc.StatusCode > 0 ? sonuc.StatusCode : 410
                };

            default:
                return HtaccessBridgeDecision.None;
        }
    }

    /// <summary>
    /// İç yeniden yazmanın hedefini kurar.
    /// </summary>
    /// <remarks>
    /// <c>HttpContext.RewritePath(string, bool)</c> tek dizge alır ve sorgu dizesini o dizgenin
    /// içinden okur. Sorguyu ayrı geçirmenin yolu yok; birleştirmezsek <c>index.php?q=...</c>
    /// üreten her WordPress/Laravel kuralı sorgusunu KAYBEDERDİ.
    /// </remarks>
    private static string Birlestir(string? yol, string? sorgu)
    {
        var temizYol = yol ?? string.Empty;
        if (temizYol.Length == 0)
        {
            return string.Empty;
        }

        if (temizYol[0] != '/')
        {
            temizYol = "/" + temizYol;
        }

        return string.IsNullOrEmpty(sorgu) ? temizYol : temizYol + "?" + sorgu;
    }
}
