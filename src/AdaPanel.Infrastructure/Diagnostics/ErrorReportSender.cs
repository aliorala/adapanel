using System.Net.Http.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Diagnostics;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Diagnostics;

/// <summary>
/// Biriken maskelenmiş kayıtları geliştiricinin toplayıcısına gönderir.
/// Onay kurulumda BİR KEZ alındığı için gönderim sessizdir; bu yüzden "kapalı" ve
/// "adres yok" durumları kesin biçimde hiçbir şey göndermeden dönmelidir.
/// </summary>
public sealed class ErrorReportSender(
    JsonFileStore fileStore,
    IHttpClientFactory httpClientFactory,
    IErrorReportService errorReportService) : IErrorReportSender
{
    private const string SettingsPath = "config/error-reporting.json";

    /// <summary>
    /// Açık zaman aşımı: varsayılan HttpClient 100 sn bekler. Toplayıcı erişilemezse
    /// bu iş (otomasyon döngüsünde çalışır) dakikalarca asılı kalmamalı.
    /// </summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PublicationTimeout = TimeSpan.FromSeconds(20);
    private string SettingsFilePath => fileStore.ResolvePath(SettingsPath);

    public async Task<ErrorReportingSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await fileStore.ReadAsync(SettingsPath, new ErrorReportingSettingsDto(), cancellationToken);
        settings.SettingsRevision = RevisionOf(settings);
        return settings;
    }

    public async Task<ErrorReportingSettingsDto> UpdateSettingsAsync(
        ErrorReportingSettingsDto settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var enabled = settings.Enabled;
        var collectorUrl = (settings.CollectorUrl ?? string.Empty).Trim();
        var batchSize = Math.Clamp(settings.BatchSize <= 0 ? 20 : settings.BatchSize, 1, 50);
        var retentionDays = Math.Clamp(settings.RetentionDays <= 0 ? 30 : settings.RetentionDays, 1, 365);
        if (collectorUrl.Length > 0 && !IsValidCollectorUrl(collectorUrl))
        {
            throw new AppException("errorReporting.invalid_collector_url", 400, 400);
        }

        using var lease = await ErrorReportDeliveryLifecycle.EnterSettingsAsync(SettingsFilePath, cancellationToken);
        return await fileStore.UpdateAsync(
            SettingsPath,
            new ErrorReportingSettingsDto(),
            current => new ErrorReportingSettingsDto
            {
                SettingsRevision = SettingsRevision.New(),
                Enabled = enabled,
                CollectorUrl = collectorUrl,
                // Onay anı BİR KEZ yazılır; sonraki ayar güncellemeleri onay tarihini ezmez.
                ConsentGrantedAtUtc = enabled
                    ? current.ConsentGrantedAtUtc ?? DateTime.UtcNow
                    : null,
                BatchSize = batchSize,
                RetentionDays = retentionDays,
                LastSentAtUtc = current.LastSentAtUtc,
                LastError = current.LastError
            },
            cancellationToken);
    }

    public Task<int> SendPendingAsync(CancellationToken cancellationToken = default) =>
        SendPendingCoreAsync(null, cancellationToken);

    public Task<int> SendPendingAsync(string expectedSettingsRevision, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedSettingsRevision))
            throw new AppException("errorReporting.settings_revision_required", 400, 400);
        return SendPendingCoreAsync(expectedSettingsRevision, cancellationToken);
    }

    private async Task<int> SendPendingCoreAsync(string? expectedSettingsRevision, CancellationToken cancellationToken)
    {
        using var delivery = await ErrorReportDeliveryLifecycle.EnterSendAsync(SettingsFilePath, cancellationToken);
        var settings = await GetSettingsAsync(cancellationToken);
        EnsureExpectedRevision(expectedSettingsRevision, settings);

        // Onay yok VEYA toplayıcı adresi boş → hiçbir şey gönderilmez, kayıtlar yerelde birikir.
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.CollectorUrl))
        {
            return 0;
        }

        var collectorUrl = settings.CollectorUrl.Trim();
        if (!IsValidCollectorUrl(collectorUrl))
        {
            throw new AppException("errorReporting.invalid_collector_url", 400, 400);
        }

        var batchSize = Math.Clamp(settings.BatchSize <= 0 ? 20 : settings.BatchSize, 1, 50);
        var pending = await errorReportService.GetUnsentAsync(batchSize, cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        var batch = new ErrorReportBatch
        {
            SchemaVersion = 1,
            Reports = pending.Select(report => new ErrorReportPayload
            {
                Fingerprint = report.Fingerprint,
                PanelVersion = report.PanelVersion,
                OsVersion = report.OsVersion,
                ExceptionType = report.ExceptionType,
                Message = report.Message,
                StackTrace = report.StackTrace,
                RoutePattern = report.RoutePattern,
                CorrelationId = report.CorrelationId,
                OccurrenceCount = report.OccurrenceCount,
                FirstSeenAtUtc = report.CreatedAtUtc,
                LastSeenAtUtc = report.LastSeenAtUtc
            }).ToArray()
        };

        using var client = httpClientFactory.CreateClient(OutboundConnectGuard.GuardedClientName);
        client.Timeout = SendTimeout;

        try
        {
            Task<HttpResponseMessage> submission;
            using (await ErrorReportDeliveryLifecycle.EnterSettingsAsync(SettingsFilePath, cancellationToken))
            {
                var current = await GetSettingsAsync(cancellationToken);
                EnsureExpectedRevision(expectedSettingsRevision, current);
                if (!SameConfiguration(settings, current))
                    throw new AppException("errorReporting.settings_changed", 409, 409);
                cancellationToken.ThrowIfCancellationRequested();
                // Only initiation is ordered against a settings save. The network wait must
                // not prevent consent from being revoked; an initiated POST cannot be recalled.
                submission = client.PostAsJsonAsync(collectorUrl, batch, cancellationToken);
            }
            using var response = await submission;
            if (!response.IsSuccessStatusCode)
            {
                await RecordFailureAsync(settings, $"HTTP {(int)response.StatusCode}", cancellationToken);
                throw new AppException(
                    "errorReporting.send_failed",
                    502,
                    502,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }
        }
        catch (AppException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Panel kapanıyor: kayıtlar gönderilmemiş kalır, sonraki turda yeniden denenir.
            throw;
        }
        catch (Exception exception)
        {
            // Zaman aşımı da buraya düşer (TaskCanceledException). Kayıtlar gönderilmemiş kalır.
            await RecordFailureAsync(settings, exception.Message, cancellationToken);
            throw new AppException("errorReporting.collector_unreachable", 502, 502, exception.Message);
        }

        // A received success is already an external effect. A disconnected HTTP caller must
        // not prevent its bounded local acknowledgment and cause an avoidable repeat send.
        using var publication = new CancellationTokenSource(PublicationTimeout);
        await errorReportService.MarkSentAsync(pending, publication.Token);

        var contextMatches = false;
        using (await ErrorReportDeliveryLifecycle.EnterSettingsAsync(SettingsFilePath, publication.Token))
        {
            await fileStore.UpdateAsync(
                SettingsPath,
                new ErrorReportingSettingsDto(),
                current =>
                {
                    contextMatches = SameConfiguration(settings, current);
                    if (contextMatches)
                    {
                        current.LastSentAtUtc = DateTime.UtcNow;
                        current.LastError = string.Empty;
                    }
                    return current;
                },
                publication.Token);
        }

        // Gönderilenler artık geliştiricide; saklama süresi dolanları yerelde tutmaya gerek yok.
        if (contextMatches)
            await errorReportService.PurgeAsync(settings.RetentionDays <= 0 ? 30 : settings.RetentionDays, publication.Token);

        return pending.Count;
    }

    private async Task RecordFailureAsync(ErrorReportingSettingsDto settings, string reason, CancellationToken cancellationToken)
    {
        try
        {
            var safeReason = SecretRedactor.Redact(reason);
            using var lease = await ErrorReportDeliveryLifecycle.EnterSettingsAsync(SettingsFilePath, cancellationToken);
            await fileStore.UpdateAsync(
                SettingsPath,
                new ErrorReportingSettingsDto(),
                current =>
                {
                    if (SameConfiguration(settings, current))
                        current.LastError = safeReason.Length > 300 ? safeReason[..300] : safeReason;
                    return current;
                },
                cancellationToken);
        }
        catch
        {
            // Teşhis notu yazılamadıysa asıl hatayı gölgeleme.
        }
    }

    private static bool SameConfiguration(ErrorReportingSettingsDto expected, ErrorReportingSettingsDto current) =>
        expected.Enabled == current.Enabled &&
        string.Equals(expected.CollectorUrl?.Trim(), current.CollectorUrl?.Trim(), StringComparison.Ordinal) &&
        expected.ConsentGrantedAtUtc == current.ConsentGrantedAtUtc &&
        expected.BatchSize == current.BatchSize && expected.RetentionDays == current.RetentionDays;

    private static string RevisionOf(ErrorReportingSettingsDto settings) =>
        SettingsRevision.GetOrCompute(settings.SettingsRevision, new
        {
            settings.Enabled,
            CollectorUrl = settings.CollectorUrl ?? string.Empty,
            settings.ConsentGrantedAtUtc,
            settings.BatchSize,
            settings.RetentionDays
        });

    private static void EnsureExpectedRevision(string? expected, ErrorReportingSettingsDto settings)
    {
        if (expected is not null && !string.Equals(expected, settings.SettingsRevision, StringComparison.Ordinal))
            throw new AppException("errorReporting.settings_changed", 409, 409);
    }

    private static bool IsValidCollectorUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
