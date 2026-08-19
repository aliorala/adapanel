using System.Collections.Concurrent;
using System.Threading.Channels;
using AdaPanel.Core.Dtos.Seo;

namespace AdaPanel.Infrastructure.Seo;

/// <summary>Kuyruğa alınmış tek bir tarama işi.</summary>
public sealed record SeoScanJob(string Id, string SiteName, string StartPath, int MaxPages, Guid? RequestedBy);

/// <summary>Kuyruğa alma sonucu.</summary>
public enum SeoEnqueueResult
{
    Accepted,

    /// <summary>Bu sitenin taraması zaten sırada ya da sürüyor.</summary>
    AlreadyRunning,

    /// <summary>Sunucu geneli kuyruk dolu.</summary>
    QueueFull
}

/// <summary>
/// Tarama kuyruğu ve CANLI durumu (süreç içi, tek örnek).
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden kuyruk:</b> tarama dakikalarca sürebilir ve bir HTTP isteğini o kadar açık tutmak
/// hem tarayıcı zaman aşımına takılır hem de panelin isteğini bloklar. Denetleyici işi kuyruğa
/// atar ve HEMEN döner; arayüz durumu yoklar.
/// </para>
/// <para>
/// <b>Neden aynı anda tek tarama:</b> tüketici tektir (<see cref="SeoScanWorker"/>). Tarama, CPU
/// ve disk bakımından taranan sitenin kendi sunucusunu yorar; paralel on tarama, müşterilerin
/// sitelerini panelin kendisi yavaşlatırdı. Sıra beklemek görünürdür (<c>QueueLength</c>).
/// </para>
/// <para>
/// <b>Durum neden bellekte:</b> canlı ilerleme (kaç sayfa gezildi) yalnız süren tarama için
/// anlamlıdır. Panel yeniden başlarsa süren tarama da biter; durum sıfırlanır ve arayüz son
/// KAYITLI raporu gösterir. Diske yazılan bir "sürüyor" bayrağı, yeniden başlatmadan sonra
/// sonsuza kadar "sürüyor" diyen bir yalan olurdu.
/// </para>
/// </remarks>
public sealed class SeoScanQueue
{
    /// <summary>Sunucu geneli kuyruk kapasitesi.</summary>
    public const int Capacity = 20;

    // FullMode = Wait OLMAK ZORUNDA. İlk yazımda DropWrite seçilmişti ve test bunu yakaladı:
    // DropWrite'ta kanal doluyken TryWrite **true** döner ve iş SESSİZCE DÜŞER. Sonuç, kullanıcı
    // için en kötü biçimdi — panel "sıraya alındı" der, durum sonsuza kadar "queued" görünür ve
    // tarama hiç çalışmaz. Wait ile TryWrite dolu kanalda false döner ve kullanıcı
    // "kuyruk dolu" yanıtını ALIR.
    private readonly Channel<SeoScanJob> channel = Channel.CreateBounded<SeoScanJob>(
        new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private readonly ConcurrentDictionary<string, LiveState> states = new(StringComparer.OrdinalIgnoreCase);

    public int QueueLength => states.Count(entry => entry.Value.State == "queued");

    public SeoEnqueueResult TryEnqueue(SeoScanJob job)
    {
        var state = new LiveState { PageLimit = job.MaxPages };
        if (!states.TryAdd(job.SiteName, state))
        {
            return SeoEnqueueResult.AlreadyRunning;
        }

        if (!channel.Writer.TryWrite(job))
        {
            states.TryRemove(new KeyValuePair<string, LiveState>(job.SiteName, state));
            return SeoEnqueueResult.QueueFull;
        }

        return SeoEnqueueResult.Accepted;
    }

    public IAsyncEnumerable<SeoScanJob> ReadAllAsync(CancellationToken cancellationToken)
        => channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// İşi çalışır duruma geçirir ve iptal belirtecini döner. Kuyrukta beklerken iptal edildiyse
    /// null döner — iş sessizce düşürülür.
    /// </summary>
    public CancellationTokenSource? TryBeginRun(SeoScanJob job)
    {
        if (!states.TryGetValue(job.SiteName, out var state) || state.Cancelled)
        {
            states.TryRemove(job.SiteName, out _);
            return null;
        }

        state.State = "running";
        state.StartedAtUtc = DateTime.UtcNow;
        state.Cancellation = new CancellationTokenSource();
        return state.Cancellation;
    }

    public void ReportProgress(string siteName, int pagesCrawled)
    {
        if (states.TryGetValue(siteName, out var state))
        {
            state.PagesCrawled = pagesCrawled;
        }
    }

    public void Finish(string siteName)
    {
        if (states.TryRemove(siteName, out var state))
        {
            state.Cancellation?.Dispose();
        }
    }

    /// <summary>Süren ya da sırada bekleyen taramayı iptal eder; iptal edilecek iş yoksa false.</summary>
    public bool Cancel(string siteName)
    {
        if (!states.TryGetValue(siteName, out var state))
        {
            return false;
        }

        state.Cancelled = true;
        state.Cancellation?.Cancel();
        return true;
    }

    public SeoScanStateDto GetState(string siteName)
    {
        if (!states.TryGetValue(siteName, out var state))
        {
            return new SeoScanStateDto { SiteName = siteName, State = "idle" };
        }

        return new SeoScanStateDto
        {
            SiteName = siteName,
            State = state.State,
            PagesCrawled = state.PagesCrawled,
            PageLimit = state.PageLimit,
            QueueLength = QueueLength,
            StartedAtUtc = state.StartedAtUtc
        };
    }

    private sealed class LiveState
    {
        public volatile string State = "queued";

        public volatile int PagesCrawled;

        public int PageLimit { get; init; }

        public DateTime? StartedAtUtc { get; set; }

        public volatile bool Cancelled;

        public CancellationTokenSource? Cancellation { get; set; }
    }
}
