using System.Text.Json.Serialization;

namespace AdaPanel.Core.Dtos.Licensing;

/// <summary>
/// Lisansın panel açısından anlamı. TEK karar kaynağı <c>LicenseDecision</c>'dır; bu tip yalnızca
/// onun sonucunu taşır.
/// </summary>
/// <remarks>
/// <para>
/// Durumların hiçbiri "siteleri kapat" anlamına GELMEZ. Bir barındırma panelinde lisans, müşterinin
/// üretimdeki sitesini/postasını/veritabanını durdurma gerekçesi olamaz; en fazla YENİ kaynak
/// açılmasını durdurur. Gerekçenin tamamı <c>ILicenseService</c> belgesinde.
/// </para>
/// <para>
/// <b>Dönüştürücü tipin ÜZERİNDE, kayıt yerinde değil.</b> Ölçüldü: panelde genel bir
/// <c>JsonStringEnumConverter</c> kaydı YOK (<c>Program.cs</c> yalnız <c>AddControllersWithViews</c>
/// çağırıyor), yani bu enum varsayılan olarak JSON'a <b>sayı</b> (0..6) olarak yazılırdı ve arayüz
/// <c>state === 3</c> gibi anlamsız bir karşılaştırma yapmak zorunda kalırdı. Global yapılandırmayı
/// değiştirmek ise panelin BÜTÜN mevcut yanıtlarını değiştirir (frontend'in bugün sayı beklediği
/// yerler kırılabilir). Bu yüzden dönüşüm yalnız bu tipe bağlandı.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LicenseState
{
    /// <summary>Doğrulama ucu yapılandırılmamış: lisanslama tamamen KAPALI, panel bugünkü gibi çalışır.</summary>
    NotConfigured = 0,

    /// <summary>Sunucu son doğrulamada "geçerli" dedi ve doğrulama tazedir.</summary>
    Active = 1,

    /// <summary>Sunucuya ulaşılamıyor ama çevrimdışı tolerans süresi HENÜZ dolmadı: panel kısıtsız.</summary>
    Grace = 2,

    /// <summary>Sunucuya ulaşılamıyor ve çevrimdışı tolerans doldu: yalnız YENİ kaynak kısıtlanır.</summary>
    GraceExpired = 3,

    /// <summary>Lisansın süresi dolmuş (sunucu söyledi ya da bilinen bitiş tarihi geçti).</summary>
    Expired = 4,

    /// <summary>Sunucu anahtarı reddetti.</summary>
    Invalid = 5,

    /// <summary>Doğrulama ucu yapılandırılmış ama panelde hiç lisans anahtarı yok.</summary>
    Unlicensed = 6
}
