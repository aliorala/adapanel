using System.ComponentModel.DataAnnotations;
using AdaPanel.Core.Common;

namespace AdaPanel.Core.Dtos.Auth;

/// <summary>
/// Yöneticinin (ya da bayinin, YALNIZ kendi müşterisi için) BAŞKA bir kullanıcının parolasını
/// belirlemesi.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden <see cref="ChangePasswordRequest"/> değil:</b> o istek MEVCUT parolayı ister, çünkü
/// kullanıcının kendi hesabında çalınmış bir çerezin tek başına hesabı devralmasını engeller. Burada
/// mevcut parola İSTENEMEZ — faturalama sistemi müşterinin parolasını zaten bilmiyor, sıfırlamak
/// için geliyor. Yetki bu yüzden parolayla değil AKTÖRÜN ROLÜYLE ve kapsamıyla kanıtlanır.
/// </para>
/// <para>
/// Kullanıcı KENDİ parolasını bu uçtan değiştiremez (uç kendi kimliğini reddeder): aksi hâlde
/// çalınmış bir yönetici çerezi, mevcut parolayı hiç bilmeden hesabı kalıcı olarak devralabilir ve
/// <c>change-password</c> üzerindeki mevcut-parola koruması anlamsızlaşırdı.
/// </para>
/// </remarks>
public sealed class SetUserPasswordRequest
{
    [Required]
    [StringLength(PasswordPolicy.MaximumLength, MinimumLength = PasswordPolicy.MinimumLength)]
    public string NewPassword { get; init; } = string.Empty;
}

/// <summary>Parola belirlemenin sonucu. Düşürülen anahtar sayısı çağırana BİLDİRİLİR.</summary>
public sealed class SetUserPasswordResultDto
{
    /// <summary>Bu değişiklik yüzünden geçersiz kılınan API anahtarı sayısı.</summary>
    public int RevokedApiTokens { get; init; }
}

/// <summary>
/// Tek kullanımlık, kısa ömürlü panel giriş bağlantısı (faturalama sisteminin "Panele git" düğmesi).
/// </summary>
/// <remarks>
/// <para>
/// <b>Belirteç URL'de gider ve bu KAYIT BIRAKIR:</b> IIS/panel istek günlüğüne, ters vekil
/// günlüklerine ve müşterinin tarayıcı geçmişine düşer. Bu yüzden belirtecin tek kullanımlık ve
/// dakikalarla ölçülen ömürde olması bir tercih değil ŞARTTIR: günlüğü sonradan okuyan biri elinde
/// çoktan tüketilmiş ve süresi geçmiş bir dize bulur. Panelin küresel
/// <c>Referrer-Policy: same-origin</c> başlığı da adresin üçüncü taraflara <c>Referer</c> ile
/// sızmasını engeller.
/// </para>
/// </remarks>
public sealed class SsoLinkDto
{
    /// <summary>Müşterinin tarayıcısının YÖNLENDİRİLECEĞİ tam adres (belirteç dahil).</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Belirtecin son geçerlilik anı (UTC). Bu andan sonra bağlantı reddedilir.</summary>
    public DateTime ExpiresAtUtc { get; init; }
}
