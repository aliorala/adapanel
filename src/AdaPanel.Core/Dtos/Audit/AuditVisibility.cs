namespace AdaPanel.Core.Dtos.Audit;

/// <summary>
/// Denetim kaydı GÖRÜNÜRLÜK kapsamı: çağıran hangi kullanıcıların kayıtlarını görebilir.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı bir tip:</b> kapsam, isteğe ait sıradan bir filtre DEĞİLDİR — unutulursa bir
/// kiracı diğerinin kaydını görür. Bu yüzden sorgu ölçütlerinden (<see cref="AuditLogQuery"/>)
/// ayrı, zorunlu bir parametre olarak taşınır: çağıran onu vermeyi "unutamaz", derlenmez.
/// </para>
/// <para>
/// <b>Neden kapalı-varsayılan:</b> <see cref="Unrestricted"/> değilken kimlik listesi BOŞ ise
/// hiçbir kayıt dönmez (<see cref="None"/>). Kapsamı doldurmayı atlayan bir hata, "her şeyi göster"
/// değil "hiçbir şey gösterme" ile sonuçlanır — sızıntı yerine boş ekran.
/// </para>
/// </remarks>
public sealed class AuditVisibility
{
    private AuditVisibility(bool unrestricted, IReadOnlyCollection<Guid> userIds)
    {
        Unrestricted = unrestricted;
        UserIds = userIds;
    }

    /// <summary>Kapsam yok: sunucu geneli (Administrator/Operator).</summary>
    public bool Unrestricted { get; }

    /// <summary>Görülebilecek kullanıcı kimlikleri (kapsamlı roller için).</summary>
    public IReadOnlyCollection<Guid> UserIds { get; }

    /// <summary>Sunucu genelini görebilen kapsam.</summary>
    public static AuditVisibility Unlimited { get; } = new(true, Array.Empty<Guid>());

    /// <summary>Hiçbir kaydı görmeyen kapsam (kimliksiz/tanınmayan çağıran).</summary>
    public static AuditVisibility None { get; } = new(false, Array.Empty<Guid>());

    /// <summary>Yalnız verilen kullanıcı kimliklerinin kayıtlarını gören kapsam.</summary>
    public static AuditVisibility ForUsers(IEnumerable<Guid> userIds) =>
        new(false, userIds.Distinct().ToArray());
}
