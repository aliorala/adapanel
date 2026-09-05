namespace AdaPanel.Core.Dtos.Sites;

/// <summary>Laravel queue worker (queue:work) durumu — panel Kuyruk sekmesi gibi.</summary>
public sealed class QueueWorkerStatusDto
{
    public bool Running { get; set; }

    public string TaskName { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="Running"/> OLCULDU mu, yoksa VARSAYILDI mi?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>NEDEN VAR.</b> Durdurma yollari (<c>StopQueueWorkerAsync</c>,
    /// <c>StopLaravelSchedulerAsync</c>) <c>Running = false</c> iddiasini HIC OLCMEDEN
    /// yaziyordu. Ustelik silme hatasi (<c>AppException</c>) "gorev zaten yok" varsayilarak
    /// YUTULUYORDU -- yani silme GERCEKTEN basarisiz oldugunda da panel "durduruldu" diyordu.
    /// Isci calismaya devam ederken kullanici durdurdugunu sanardi.
    /// </para>
    /// <para>
    /// Varsayilan <c>false</c>'tur: hicbir olcum yapilmadan uretilen bir durum kendini
    /// "olculdu" ilan etmez.
    /// </para>
    /// </remarks>
    public bool Measured { get; set; }

    /// <summary>Olcume dair insan tarafindan okunacak kanit.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>Bos ise durum OLCULDU ve yolunda; dolu ise gosterilecek uyari anahtari.</summary>
    public string NoticeKey => Measured
        ? string.Empty
        : "sites.queue_worker_state_unverified";
}
