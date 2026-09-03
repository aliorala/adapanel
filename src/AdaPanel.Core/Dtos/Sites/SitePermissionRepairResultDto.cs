namespace AdaPanel.Core.Dtos.Sites;

/// <summary>
/// "İzinleri Onar" sonucunun ÜÇ DEĞERLİ cevabı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı bir sonuç tipi var:</b> uç eskiden <see cref="SiteDto"/> dönüyordu ve arayüz
/// koşulsuz "Yazma izinleri onarıldı" yazıyordu. İzin veren komut (<c>icacls</c>) başarısız olsa
/// bile hiçbir istisna fırlamıyordu, dolayısıyla kullanıcı HER ZAMAN yeşil bir bildirim
/// görüyordu — hiçbir şey değişmemiş olsa da. <see cref="SiteDto"/>'nun izin durumu diye bir
/// alanı yok; sonucu taşıyabilecek bir yer olmadığı için cevap uydurulur hâle gelmişti.
/// </para>
/// <para>
/// Üç değer bilerek ayrıdır: <c>granted</c> (ölçüldü, yerinde), <c>not_granted</c> (ölçüldü,
/// yok) ve <c>unmeasured</c> (okunamadı). Son ikisini birleştirmek, "bilmiyorum"u "yok" diye
/// göstermek olurdu — yanlış alarm, yanlış güvence kadar zararlıdır.
/// </para>
/// </remarks>
public sealed class SitePermissionRepairResultDto
{
    /// <summary>Ölçüldü, hak yerinde.</summary>
    public const string StateGranted = "granted";

    /// <summary>Ölçüldü, hak YOK.</summary>
    public const string StateNotGranted = "not_granted";

    /// <summary>Okunamadı; cevap bilinmiyor.</summary>
    public const string StateUnmeasured = "unmeasured";

    public SiteDto Site { get; set; } = new();

    /// <summary><see cref="StateGranted"/>, <see cref="StateNotGranted"/> ya da <see cref="StateUnmeasured"/>.</summary>
    public string State { get; set; } = StateUnmeasured;

    /// <summary>İzin verilen (ya da verilemeyen) hesap; boşsa okunamamıştır.</summary>
    public string Principal { get; set; } = string.Empty;

    /// <summary>İnsan tarafından okunacak ölçüm ayrıntısı — denetim kaydına da bu yazılır.</summary>
    public string Detail { get; set; } = string.Empty;

    public bool IsGranted => string.Equals(State, StateGranted, StringComparison.Ordinal);
}
