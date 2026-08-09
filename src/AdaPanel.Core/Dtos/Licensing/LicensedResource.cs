namespace AdaPanel.Core.Dtos.Licensing;

/// <summary>
/// Lisans kapısının önüne konduğu YENİ kaynak türleri.
/// </summary>
/// <remarks>
/// Liste bilerek kısadır: kapı yalnızca "yeni bir şey doğuran" işlemlere konur. Okuma, listeleme,
/// yedek alma, geri yükleme, güncelleme, silme ve mevcut kaynakların yönetimi lisanstan BAĞIMSIZ
/// çalışmaya devam eder — müşterinin verisine erişimini lisansla kesmek fidye davranışıdır.
/// </remarks>
public enum LicensedResource
{
    /// <summary>Yeni abonelik (müşteri hesabı). SKU başına "en fazla N abonelik" limiti burada uygulanır.</summary>
    Subscription = 0,

    /// <summary>Yeni web sitesi.</summary>
    Site = 1,

    /// <summary>Yeni veritabanı.</summary>
    Database = 2,

    /// <summary>Yeni posta kutusu / posta alan adı.</summary>
    Mailbox = 3
}
