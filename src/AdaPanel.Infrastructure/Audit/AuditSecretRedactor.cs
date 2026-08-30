using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Audit;

/// <summary>
/// Denetim kaydına yazılmadan ÖNCE metni temizler: sır değerlerini maskeler ve sütun sınırına sığdırır.
/// </summary>
/// <remarks>
/// <para>
/// <b>Burada KURAL YOKTUR — desenlerin tamamı <see cref="SecretRedactor"/>'dadır.</b> Bu tip yalnız
/// devreden bir cephedir.
/// </para>
/// <para>
/// <b>Neden ikiye ayrıldı:</b> ölçüldü ki asıl sızıntı kanalı denetim kaydı değil
/// <see cref="AppException.Details"/>'tir — <c>Details</c> yanıt gövdesine, ilerleme kaydına,
/// DTO'lara ve MCP çıktısına da gider ve ~40 çağrı yerinde üretilir. Maskelemenin TEK geçitte
/// kalmasının tek yolu, kuralı <c>AppException</c>'ın kendisinde uygulamaktı; <c>AppException</c>
/// ise <c>AdaPanel.Core</c>'dadır ve Core hiçbir projeye referans veremez. Bu yüzden desenler
/// Core'a taşındı.
/// </para>
/// <para>
/// <b>Neden bu tip DURUYOR:</b> denetim yazma yolu (<see cref="AuditWriter"/>) ve onun testleri bu
/// adı kullanıyor. Adı değiştirmek, güvenlik kuralının kendisiyle ilgisi olmayan onlarca dosyayı
/// aynı anda oynatmak demekti; cephe, o riski almadan kuralı tek kaynağa indirir.
/// </para>
/// </remarks>
public static class AuditSecretRedactor
{
    /// <inheritdoc cref="SecretRedactor.Mask"/>
    public const string Mask = SecretRedactor.Mask;

    /// <inheritdoc cref="SecretRedactor.DetailsMaxLength"/>
    public const int DetailsMaxLength = SecretRedactor.DetailsMaxLength;

    /// <inheritdoc cref="SecretRedactor.ShortFieldMaxLength"/>
    public const int ShortFieldMaxLength = SecretRedactor.ShortFieldMaxLength;

    /// <inheritdoc cref="SecretRedactor.SanitizeDetails"/>
    public static string SanitizeDetails(string? value) => SecretRedactor.SanitizeDetails(value);

    /// <inheritdoc cref="SecretRedactor.SanitizeShortField"/>
    public static string SanitizeShortField(string? value) => SecretRedactor.SanitizeShortField(value);

    /// <inheritdoc cref="SecretRedactor.Redact"/>
    public static string Redact(string? value) => SecretRedactor.Redact(value);

    /// <inheritdoc cref="SecretRedactor.RedactUrl"/>
    public static string RedactUrl(string? url) => SecretRedactor.RedactUrl(url);
}
