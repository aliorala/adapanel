namespace AdaPanel.Core.Dtos.Audit;

/// <summary>
/// Denetim kaydı SAKLAMA POLİTİKASI + biriken kaydın ölçümü.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden var:</b> panelde denetim kayıtları hiçbir yerde temizlenmiyordu — yani sonsuza kadar
/// birikiyorlardı. Bu iki yönden sorun: (1) panel veritabanı (SQLite) sınırsız büyür, (2) KVKK/GDPR
/// "gerekenden uzun saklama" ile çelişir. Politika yapılandırılabilir olmalı çünkü doğru süre
/// hukuki/sözleşmesel bir karardır, teknik bir sabit değil.
/// </para>
/// <para>
/// <see cref="TotalCount"/> ve <see cref="OldestAtUtc"/> ÖLÇÜMDÜR: yönetici "ne kadar birikmiş"
/// sorusunu tahmin etmeden görür.
/// </para>
/// </remarks>
public sealed class AuditRetentionSettingsDto
{
    /// <summary>Kaç günden eski kayıtlar silinir. 0 = sınırsız sakla (temizlik yapılmaz).</summary>
    public int RetentionDays { get; set; }

    /// <summary>Son temizliğin zamanı; hiç çalışmadıysa null.</summary>
    public DateTime? LastPurgeAtUtc { get; set; }

    /// <summary>Son temizlikte silinen kayıt sayısı.</summary>
    public int LastPurgedCount { get; set; }

    /// <summary>Şu an tutulan toplam kayıt (ölçüm).</summary>
    public long TotalCount { get; set; }

    /// <summary>En eski kaydın zamanı; hiç kayıt yoksa null (ölçüm).</summary>
    public DateTime? OldestAtUtc { get; set; }
}
