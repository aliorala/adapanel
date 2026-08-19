namespace AdaPanel.Core.Dtos.Sites;

public sealed class SiteDnsZoneDto
{
    public string SiteName { get; set; } = string.Empty;

    public string ZoneName { get; set; } = string.Empty;

    public string[] NameServers { get; set; } = [];

    /// <summary>İzin verilen AXFR (zone transfer) hedef IP'leri (ikincil DNS sunucuları). Boş = transfer kapalı.</summary>
    public string[] ZoneTransferIps { get; set; } = [];

    /// <summary>
    /// Bölge İKİNCİL (slave/secondary) mi: kayıtların kaynağı bu panel DEĞİL, dış master sunucudur.
    /// Plesk'in bölge başına "Switch to Secondary / Switch to Primary" düğmesinin karşılığı.
    /// </summary>
    public bool IsSecondary { get; set; }

    /// <summary>
    /// İkincil kipteyken bölgenin çekileceği master DNS sunucularının IP'leri. Birincil kipte boştur.
    /// </summary>
    public string[] MasterServers { get; set; } = [];

    /// <summary>
    /// Bölge birincilken AXFR izni + NOTIFY alacak FİİLİ hedef listesi: bölgeye özel
    /// <see cref="ZoneTransferIps"/> ile sunucu-geneli ikincil DNS sunucularının birleşimi.
    /// <para>
    /// Ayrı alan: kullanıcı bölgede tek bir IP görürken sunucu-geneli listeden gelen iki IP daha
    /// transfer alabiliyor olabilir. Neyin gerçekten izinli olduğunu göstermeyen bir ekran,
    /// güvenlik ayarını okunamaz kılar.
    /// </para>
    /// </summary>
    public string[] EffectiveNotifyServers { get; set; } = [];

    /// <summary>
    /// Windows DNS'in bölge için BİLDİRDİĞİ tür (Primary/Secondary/Stub…). Ölçülemediyse boş.
    /// <para>
    /// Panelin kendi kaydından ayrı tutulur: ikisi ayrıldığı an (panel "birincil" derken Windows
    /// "Secondary" diyorsa) sorun tam olarak oradadır. Tek bir alan gösterip "uyuşuyor" varsaymak,
    /// hatayı görünmez yapardı.
    /// </para>
    /// </summary>
    public string WindowsZoneType { get; set; } = string.Empty;

    public SiteDnsRecordDto[] Records { get; set; } = [];

    public bool IsWindowsDnsAvailable { get; set; }

    public bool IsWindowsZonePresent { get; set; }

    public string Source { get; set; } = "panel-managed";

    public string StatusMessage { get; set; } = string.Empty;

    /// <summary>Bölge Windows DNS'te DNSSEC ile imzalı mı (Plesk DNSSEC eşdeğeri).</summary>
    public bool IsDnssecSigned { get; set; }

    /// <summary>Registrar'a (üst bölgeye) verilecek DS kayıtları — imzalıyken doldurulur.</summary>
    public string[] DnssecDsRecords { get; set; } = [];
}
