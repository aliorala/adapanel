namespace AdaPanel.Core.Dtos.Diagnostics;

/// <summary>
/// Toplayıcıya giden TEK kaydın gövdesi. <see cref="ErrorReportDto"/> ile aynı değil:
/// yerel kimlik (Id) ve gönderim durumu (SentAtUtc) dışarıya gitmez — kurulumu izlemeye
/// yarayacak hiçbir fazlalık taşınmaz.
/// </summary>
public sealed class ErrorReportPayload
{
    public string Fingerprint { get; init; } = string.Empty;

    public string PanelVersion { get; init; } = string.Empty;

    public string OsVersion { get; init; } = string.Empty;

    public string ExceptionType { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string StackTrace { get; init; } = string.Empty;

    public string RoutePattern { get; init; } = string.Empty;

    public string CorrelationId { get; init; } = string.Empty;

    public int OccurrenceCount { get; init; }

    public DateTime FirstSeenAtUtc { get; init; }

    public DateTime LastSeenAtUtc { get; init; }
}

public sealed class ErrorReportBatch
{
    /// <summary>Gövde biçimi değişirse toplayıcı eski panelleri ayırt edebilsin.</summary>
    public int SchemaVersion { get; init; } = 1;

    public IReadOnlyCollection<ErrorReportPayload> Reports { get; init; } = [];
}
