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

    public async Task<ErrorReportingSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        await fileStore.ReadAsync(SettingsPath, new ErrorReportingSettingsDto(), cancellationToken);

    public async Task<ErrorReportingSettingsDto> UpdateSettingsAsync(
        ErrorReportingSettingsDto settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var collectorUrl = (settings.CollectorUrl ?? string.Empty).Trim();
        if (collectorUrl.Length > 0 && !IsValidCollectorUrl(collectorUrl))
        {
            throw new AppException("errorReporting.invalid_collector_url", 400, 400);
        }

        return await fileStore.UpdateAsync(
            SettingsPath,
            new ErrorReportingSettingsDto(),
            current => new ErrorReportingSettingsDto
            {
                Enabled = settings.Enabled,
                CollectorUrl = collectorUrl,
                // Onay anı BİR KEZ yazılır; sonraki ayar güncellemeleri onay tarihini ezmez.
                ConsentGrantedAtUtc = settings.Enabled
                    ? current.ConsentGrantedAtUtc ?? DateTime.UtcNow
                    : null,
                BatchSize = Math.Clamp(settings.BatchSize <= 0 ? 20 : settings.BatchSize, 1, 50),
                RetentionDays = Math.Clamp(settings.RetentionDays <= 0 ? 30 : settings.RetentionDays, 1, 365),
                LastSentAtUtc = current.LastSentAtUtc,
                LastError = current.LastError
            },
            cancellationToken);
    }

    public async Task<int> SendPendingAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);

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

        var client = httpClientFactory.CreateClient();
        client.Timeout = SendTimeout;

        try
        {
            using var response = await client.PostAsJsonAsync(collectorUrl, batch, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                await RecordFailureAsync($"HTTP {(int)response.StatusCode}", cancellationToken);
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
            await RecordFailureAsync(exception.Message, cancellationToken);
            throw new AppException("errorReporting.collector_unreachable", 502, 502, exception.Message);
        }

        await errorReportService.MarkSentAsync(pending.Select(report => report.Id).ToArray(), cancellationToken);

        await fileStore.UpdateAsync(
            SettingsPath,
            new ErrorReportingSettingsDto(),
            current =>
            {
                current.LastSentAtUtc = DateTime.UtcNow;
                current.LastError = string.Empty;
                return current;
            },
            cancellationToken);

        // Gönderilenler artık geliştiricide; saklama süresi dolanları yerelde tutmaya gerek yok.
        await errorReportService.PurgeAsync(settings.RetentionDays <= 0 ? 30 : settings.RetentionDays, cancellationToken);

        return pending.Count;
    }

    private async Task RecordFailureAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            await fileStore.UpdateAsync(
                SettingsPath,
                new ErrorReportingSettingsDto(),
                current =>
                {
                    current.LastError = reason.Length > 300 ? reason[..300] : reason;
                    return current;
                },
                cancellationToken);
        }
        catch
        {
            // Teşhis notu yazılamadıysa asıl hatayı gölgeleme.
        }
    }

    private static bool IsValidCollectorUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
