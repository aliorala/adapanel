using System;

namespace AdaPanel.HtaccessModule;

/// <summary>Motorun bir istek için verdiği kararın türü.</summary>
public enum HtaccessActionKind
{
    /// <summary>Hiçbir şey yapma. Motor kural bulamadıysa dönmesi gereken değer.</summary>
    None = 0,

    /// <summary>İç yeniden yazma (URL değişmez, sunulan kaynak değişir).</summary>
    Rewrite = 1,

    /// <summary>İstemciye yönlendirme (301/302/307...).</summary>
    Redirect = 2,

    /// <summary>Erişimi reddet (403 gibi).</summary>
    Deny = 3
}

/// <summary>Motora verilen istek görüntüsü. System.Web tiplerinden ARINDIRILMIŞ olması bilinçli:
/// motor böylece IIS'e bağlanmadan test edilebilir.</summary>
public sealed class HtaccessRequestInfo
{
    public string VirtualPath = string.Empty;
    public string QueryString = string.Empty;
    public string HttpMethod = string.Empty;
    public string PhysicalPath = string.Empty;
    public string DocumentRoot = string.Empty;
    public string RemoteAddress = string.Empty;
    public bool IsSecure;

    /// <summary>
    /// İsteğin <c>Host</c> başlığı (port HARİÇ).
    /// </summary>
    /// <remarks>
    /// Motor bunu <c>%{HTTP_HOST}</c> ve <c>%{SERVER_NAME}</c> olarak görür. Alan sonradan
    /// eklendi ve gerekliydi: <c>RewriteCond %{HTTP_HOST} !^www\.</c> kalıbı gerçek dünyadaki
    /// <c>.htaccess</c> dosyalarının en yaygın kuralıdır (kanonik alan adı yönlendirmesi).
    /// Boş bırakılsaydı o kurallar sessizce eşleşmez, kullanıcı "kural çalışmıyor" derdi.
    /// </remarks>
    public string Host = string.Empty;
}

/// <summary>Motorun kararı.</summary>
public sealed class HtaccessDecision
{
    public static readonly HtaccessDecision None = new HtaccessDecision();

    public HtaccessActionKind Kind = HtaccessActionKind.None;

    /// <summary>Rewrite/Redirect için hedef.</summary>
    public string? Target;

    /// <summary>Redirect/Deny için durum kodu.</summary>
    public int StatusCode;
}

/// <summary>
/// BARINDIRMA KATMANININ ÇEKİRDEK MOTORA BAKAN YÜZÜ (port).
///
/// <b>Bu arayüz bilerek BOŞ bir kanca olarak bırakılmıştır.</b> Çekirdek motoru paralel bir ekip
/// <c>src/AdaPanel.Infrastructure/Htaccess/</c> altında yazıyor ve o kod <b>net8.0</b>'dır;
/// bu modül ise <b>net48</b> olmak ZORUNDA (System.Web yalnız orada var). net8 bir derleme
/// net48 CLR'ında YÜKLENEMEZ — yani motora <c>ProjectReference</c> vermek fiziksel olarak
/// imkânsızdır. Motoru buraya bağlamanın TEK yolu, barındırma dosyalarında yapıldığı gibi
/// motorun <c>.cs</c> dosyalarını bu projeye KAYNAK OLARAK bağlamaktır
/// (bkz. csproj'daki "PAYLASILAN BARINDIRMA MANTIGI" bloğu) ve bu ancak motorun API'si
/// belli olduğunda, net48 uyumluluğu ÖLÇÜLEREK yapılabilir.
///
/// Motor bağlanana kadar <see cref="HtaccessEngineRegistry.Current"/> <c>null</c>'dır ve modül
/// her isteği DOKUNMADAN geçirir — yani entegre edilmemiş olması bile güvenlidir.
///
/// <b>ÇEKİRDEK MOTOR EKİBİNE İKİ YAZILI GEREKSİNİM</b> (barındırma katmanı bunları telafi EDEMEZ,
/// bkz. <c>HtaccessFailSafe</c> sınıf yorumu):
///  1) Her <c>Regex</c> nesnesi <c>matchTimeout</c> İLE kurulmalı. Felaketli geri izleme istisna
///     fırlatmaz, sadece iş parçacığını kilitler; sarmalayıcı süren bir çağrıyı KESEMEZ.
///  2) Ayrıştırıcı özyinelemesi SINIRLI olmalı. <c>StackOverflowException</c> .NET'te
///     yakalanamaz — süreci anında öldürür ve o havuzdaki TÜM siteleri düşürür.
/// </summary>
public interface IHtaccessEngine
{
    /// <summary>
    /// Kararı üretir. <b>Bu metot istisna fırlatabilir</b> — çağıran taraf fail-open sarmalayıcısı
    /// ile korunmaktadır ve fırlatan bir motor isteği BOZMAZ, yalnızca devre kesici sayacını artırır.
    /// </summary>
    HtaccessDecision Evaluate(HtaccessRequestInfo request, object compiled);

    /// <summary>Bir <c>.htaccess</c> dosyasını derler. Sonuç önbelleğe alınır; her istekte çağrılmaz.</summary>
    object? Compile(string htaccessPath);
}

/// <summary>
/// Motorun kayıt yeri.
///
/// STATIC — ve bu izolasyonu BOZMAZ: .NET Framework'te her IIS uygulaması kendi AppDomain'ini alır
/// ve static alanlar AppDomain BAŞINADIR. Aynı uygulama havuzunu paylaşan iki uygulama bile ayrı
/// AppDomain'lerdedir. Dolayısıyla bir kiracının motoru/durumu başka bir kiracıya SIZMAZ.
/// (Buraya süreç geneli bir önbellek konulsaydı bu doğal sınır kırılırdı — bkz. diğer sınıfların
/// hepsinin ÖRNEKLENMİŞ olması.)
/// </summary>
public static class HtaccessEngineRegistry
{
    public static IHtaccessEngine? Current;
}
