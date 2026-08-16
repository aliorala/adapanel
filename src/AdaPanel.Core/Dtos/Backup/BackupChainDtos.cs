namespace AdaPanel.Core.Dtos.Backup;

/// <summary>
/// Artımsal yedeğin karşılaştırma birimi: TEK bir dosyanın kimliği.
/// </summary>
/// <remarks>
/// <para>
/// Plesk artımsal yedekte bir <b>indeks</b> tutar ve bir sonraki yedekte dosyayı şu üç soruyla
/// karşılaştırır: indekste var mıydı, boyutu değişti mi, değişiklik tarihi değişti mi. Panel de
/// aynı ölçüyü kullanır (Windows'ta dosya başına ACL okumak on binlerce dosyada ölçülebilir bir
/// yavaşlama demektir; izin değişikliği yakalanmaz ve bu AÇIKÇA söylenir).
/// </para>
/// <para>
/// İçerik özeti (hash) BİLEREK kullanılmaz: her yedekte bütün siteyi okumak, artımsal yedeğin
/// var oluş sebebini (disk + süre kazancı) ortadan kaldırırdı.
/// </para>
/// </remarks>
public sealed class BackupFileIndexEntry
{
    /// <summary>Arşiv içindeki tam yol ("/" ayırıcı, ör. <c>files/wp-config.php</c>).</summary>
    public string Path { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>Son değişiklik zamanı (UTC tick). Saniye altı fark da değişiklik sayılır.</summary>
    public long ModifiedUtcTicks { get; set; }
}

/// <summary>
/// Bir yedeğin çektiği anın TAM dosya listesi. Artımsal halka yalnızca değişenleri TAŞISA da
/// indeksi TAM tutar: aksi hâlde bir sonraki halka "neyin silindiğini" hesaplayamazdı.
/// </summary>
public sealed class BackupFileIndex
{
    public string ChainId { get; set; } = string.Empty;

    public int ChainIndex { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public List<BackupFileIndexEntry> Entries { get; set; } = [];
}

/// <summary>
/// Zincirdeki tek bir halkanın ekran özeti.
/// </summary>
public sealed class BackupChainLinkDto
{
    public string BackupId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int ChainIndex { get; set; }

    /// <summary>"full" | "incremental".</summary>
    public string BackupMode { get; set; } = "full";

    public DateTime CreatedAtUtc { get; set; }

    public long Size { get; set; }

    /// <summary>Halkanın dosyası GERÇEKTEN yerinde mi (kayıt var ama dosya silinmiş olabilir).</summary>
    public bool Available { get; set; }
}

/// <summary>
/// Bir yedeğin geri yüklenmesi için gereken zincirin ÇÖZÜMÜ.
/// </summary>
/// <remarks>
/// Zincir sağlam değilse geri yükleme BAŞLATILMAZ; eksik halkaların adı kullanıcıya söylenir.
/// Plesk de kopmuş zinciri sarı ünlem işaretiyle işaretler ve geri yüklerken eksik yedeklerin
/// adını gösterir — sessizce "elimizdekiyle idare edelim" demek, geri yüklenen sitenin hangi
/// dosyalarının eski kaldığını kimsenin bilmemesi demektir.
/// </remarks>
public sealed class BackupChainDto
{
    public string ChainId { get; set; } = string.Empty;

    /// <summary>Tam yedekten hedef halkaya kadar SIRALI halkalar.</summary>
    public BackupChainLinkDto[] Links { get; set; } = [];

    /// <summary>Zincir kopuk mu (tam yedek yok ya da aradaki bir halka eksik/dosyası yok).</summary>
    public bool Broken { get; set; }

    /// <summary>Kopuksa eksik olan halkaların açıklaması (ad ya da sıra numarası).</summary>
    public string[] MissingLinks { get; set; } = [];
}

/// <summary>
/// Zincir farkında yedek silme sonucu: silinen kayıtların kimlikleri ve adları.
/// </summary>
public sealed class BackupDeleteResultDto
{
    public string[] DeletedIds { get; set; } = [];

    public string[] DeletedNames { get; set; } = [];
}
