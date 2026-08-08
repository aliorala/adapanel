namespace AdaPanel.Core.Dtos.Backup;

/// <summary>
/// Geri yüklemenin YARIM KALAN bir adımı.
/// <para>
/// NEDEN VAR: birleşik site yedeği geri yüklenirken veritabanı içe aktarımı çıplak bir
/// <c>catch { }</c> ile yutuluyordu; dosyalar canlı site kökünün üzerine ZATEN yazılmış olduğu
/// hâlde kayıt koşulsuz "restored" işaretleniyor ve 200 dönüyordu. Kullanıcı "geri yüklendi"
/// yazısına güvenip devam ediyor, oysa elinde ne eski hâl ne de dönmüş bir veritabanı var.
/// Başarılı sayılan bir işin sessizce yarım kalması kabul edilemez: her atlanan adım sebebiyle
/// birlikte kullanıcıya döner.
/// </para>
/// </summary>
public sealed class BackupRestoreWarningDto
{
    /// <summary>Arayüzün çevireceği mesaj anahtarı (örn. <c>database.restore_failed</c>).</summary>
    public string MessageKey { get; set; } = string.Empty;

    /// <summary>Hangi öğede oldu? (örn. <c>mysql/shop</c> ya da arşiv girdisinin adı)</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Teknik ayrıntı (istisna metni). Çevrilmez; teşhis içindir.</summary>
    public string Detail { get; set; } = string.Empty;
}
