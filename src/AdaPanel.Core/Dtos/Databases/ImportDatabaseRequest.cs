using System.ComponentModel.DataAnnotations;
using AdaPanel.Core.Common;

namespace AdaPanel.Core.Dtos.Databases;

/// <summary>
/// Var olan (uygulama/elle oluşturulmuş) bir veritabanı + kullanıcıyı panel yönetimine "içe aktarır":
/// kullanıcıya o DB'de tam yetki verir (eksikse) ve parolasını şifreli saklar → phpMyAdmin "bu kullanıcı
/// olarak aç" çalışır ve panel yedek/şifre/yetkiyi yönetebilir. Kullanıcı zaten var olmalıdır.
/// </summary>
public sealed class ImportDatabaseRequest
{
    [Required]
    [StringLength(32, MinimumLength = 3)]
    public string Engine { get; init; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string DatabaseName { get; init; } = string.Empty;

    [Required]
    [StringLength(DatabaseIdentifierLimits.RequestCeiling, MinimumLength = 1)]
    public string UserName { get; init; } = string.Empty;

    /// <summary>
    /// İçe aktarılan hesabın host kısmı. BOŞ BIRAKIN: panel hesabı TAHMİN ETMEZ, motordan okur.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ÖLÇÜLEN HATA:</b> varsayılan <c>%</c> idi ve arayüz de <c>host: "%"</c> SABİTİNİ
    /// gönderiyordu. Oysa panelin kendi açtığı kullanıcılar <c>localhost</c>'ta doğuyor
    /// (<c>MySqlAccountHosts.SafeCreationHost</c>). Sonuç: içe aktarma yetkiyi VAR OLMAYAN
    /// <c>'u'@'%'</c> hesabına yazmaya çalışıyordu — ya "kullanıcı bulunamadı" ile düşüyor, ya da
    /// GRANT'ın hesap oluşturabildiği yapılandırmalarda dünyaya açık İKİNCİ bir hesap doğuruyordu.
    /// Arkasından kimlik kaydına da <c>%</c> yazıldığı için silme <c>DROP USER 'u'@'%'</c> yapıyor
    /// ve gerçek hesap öksüz kalıyordu.
    /// </para>
    /// <para>
    /// <b>Neden <c>%</c> varsayılanı BURADA kaldırıldı ama parola/silme yollarında kalıyor:</b> o
    /// yollar var olan bir hesabı ADRESLER — eski kurulumlardaki <c>'u'@'%'</c> hesapları yönetilebilir
    /// kalmalı. İçe aktarma ise YETKİ YAZAR; yanlış hesaba yazılan yetki sessizce yeni bir erişim
    /// yüzeyi açar. Aynı gerekçeyle <c>GrantDatabaseAccessRequest.AllowedHost</c> de boşaltılmıştı;
    /// bu, o düzeltmenin atlanmış ikizidir.
    /// </para>
    /// <para>
    /// Geriye dönük kırmıyor: hesap gerçekten <c>%</c> ise motordan okunan küme de <c>%</c> döner,
    /// davranış birebir aynıdır. Açıkça host gönderen istemciler de aynen çalışır.
    /// </para>
    /// </remarks>
    [StringLength(255)]
    public string Host { get; init; } = string.Empty;

    [Required]
    [StringLength(128)]
    public string Password { get; init; } = string.Empty;

    /// <summary>Boş değilse bu veritabanı siteye BAĞLANIR → site silinince bu DB de cascade ile gider (Plesk gibi).</summary>
    [StringLength(255)]
    public string SiteName { get; init; } = string.Empty;
}
