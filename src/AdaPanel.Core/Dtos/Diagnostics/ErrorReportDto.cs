namespace AdaPanel.Core.Dtos.Diagnostics;

/// <summary>
/// Maskelenmiş hata kaydı. Bu tipteki HER alan toplayıcıya gönderilebilir olmalıdır —
/// buraya yeni alan eklerken "otomatik gönderilse savunulabilir mi" sorusunu geç.
/// </summary>
public sealed class ErrorReportDto
{
    public long Id { get; init; }

    public string Fingerprint { get; init; } = string.Empty;

    public DateTime CreatedAtUtc { get; init; }

    public DateTime LastSeenAtUtc { get; init; }

    public string PanelVersion { get; init; } = string.Empty;

    public string OsVersion { get; init; } = string.Empty;

    public string ExceptionType { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string StackTrace { get; init; } = string.Empty;

    public string RoutePattern { get; init; } = string.Empty;

    public string CorrelationId { get; init; } = string.Empty;

    public int OccurrenceCount { get; init; }

    public DateTime? SentAtUtc { get; init; }
}
