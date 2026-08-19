namespace AdaPanel.Core.Dtos.Sites;

/// <summary>
/// Bölgeyi BİRİNCİL (master) ↔ İKİNCİL (slave/secondary) kipleri arasında çevirir.
/// </summary>
/// <remarks>
/// <para>
/// Plesk paritesi: Plesk'te bölge DNS ayarlarında "Switch to Secondary" / "Switch to Primary"
/// düğmeleri vardır ve ikincil kipte kullanıcı yalnızca master DNS sunucusunun IP'sini girer;
/// kayıt listesi düzenlenemez hâle gelir çünkü kayıtların kaynağı artık o master'dır.
/// </para>
/// <para>
/// Windows tarafındaki karşılığı <c>Add-DnsServerSecondaryZone</c> /
/// <c>ConvertTo-DnsServerSecondaryZone</c> (<c>-MasterServers</c>) ve geri dönüşte
/// <c>ConvertTo-DnsServerPrimaryZone</c>'dur.
/// </para>
/// </remarks>
public sealed class SetDnsZoneModeRequest
{
    /// <summary>true = ikincil (dış master'dan çekilir), false = birincil (panel yönetir).</summary>
    public bool Secondary { get; set; }

    /// <summary>
    /// İkincil kip için master DNS sunucularının IP adresleri. <see cref="Secondary"/> true iken
    /// EN AZ BİR geçerli IP zorunludur: master'sız bir ikincil bölge hiçbir zaman veri çekemez,
    /// yani alan adı tümüyle çözülemez hâle gelirdi.
    /// </summary>
    public string[] MasterServers { get; set; } = [];
}
