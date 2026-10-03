using AdaPanel.Core.Dtos.Diagnostics;

namespace AdaPanel.Core.Interfaces;

public interface IErrorReportService
{
    /// <summary>
    /// Hatayı MASKELEYEREK kaydeder. Aynı parmak izine sahip kayıt varsa yeni satır açmaz,
    /// yalnız tekrar sayısını artırır. Kendi hatasını YUTAR: hata kaydı alınamaması,
    /// kullanıcıya dönen asıl hata yanıtını hiçbir zaman bozmamalıdır.
    /// </summary>
    Task RecordAsync(
        Exception exception,
        string routePattern,
        string correlationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<ErrorReportDto>> GetRecentAsync(int take, CancellationToken cancellationToken = default);

    /// <summary>Henüz toplayıcıya gönderilmemiş kayıtlar (en eskiden yeniye).</summary>
    Task<IReadOnlyCollection<ErrorReportDto>> GetUnsentAsync(int take, CancellationToken cancellationToken = default);

    Task MarkSentAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default);

    Task MarkSentAsync(IReadOnlyCollection<ErrorReportDto> snapshots, CancellationToken cancellationToken = default) =>
        MarkSentAsync(snapshots.Select(report => report.Id).ToArray(), cancellationToken);

    /// <summary>Gönderilmiş ve <paramref name="retentionDays"/> günden eski kayıtları siler; silinen sayısını döner.</summary>
    Task<int> PurgeAsync(int retentionDays = 30, CancellationToken cancellationToken = default);
}
