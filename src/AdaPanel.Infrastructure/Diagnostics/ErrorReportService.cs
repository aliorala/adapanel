using System.Runtime.InteropServices;
using AdaPanel.Core.Dtos.Diagnostics;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Diagnostics;

public sealed class ErrorReportService(
    AdaPanelDbContext dbContext,
    ILogger<ErrorReportService> logger) : IErrorReportService
{
    /// <summary>
    /// Tablodaki azami kayıt sayısı. Tekilleştirme tekrar eden hataları zaten tek satıra
    /// indirir; bu tavan ise "her seferinde farklı mesaj üreten" hataya karşı son emniyet.
    /// </summary>
    private const int MaxStoredReports = 500;

    public async Task RecordAsync(
        Exception exception,
        string routePattern,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);

        try
        {
            var sanitized = ErrorReportSanitizer.Sanitize(
                exception.GetType().FullName,
                exception.Message,
                exception.StackTrace,
                routePattern);

            var now = DateTime.UtcNow;

            var existing = await dbContext.ErrorReports
                .FirstOrDefaultAsync(report => report.Fingerprint == sanitized.Fingerprint, cancellationToken);

            if (existing is not null)
            {
                // AYNI hata: yeni satır AÇMA. Döngüye giren bir hata dakikada binlerce kayıt
                // üretip veritabanını şişirirdi; burada yalnız sayaç ve son görülme güncellenir.
                existing.OccurrenceCount++;
                existing.LastSeenAtUtc = now;
                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            }

            await EnforceStorageCapAsync(cancellationToken);

            dbContext.ErrorReports.Add(new ErrorReport
            {
                Fingerprint = sanitized.Fingerprint,
                CreatedAtUtc = now,
                LastSeenAtUtc = now,
                // Sürüm bilgisi "+commit" ekiyle gelebilir; sütun sınırını aşmasın.
                PanelVersion = Clamp(VersionInfo.Current, 64),
                OsVersion = Clamp(RuntimeInformation.OSDescription, 256),
                ExceptionType = sanitized.ExceptionType,
                Message = sanitized.Message,
                StackTrace = sanitized.StackTrace,
                RoutePattern = sanitized.RoutePattern,
                CorrelationId = ClampCorrelationId(ErrorReportSanitizer.Mask(correlationId)),
                OccurrenceCount = 1,
                SentAtUtc = null
            });

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception recordFailure)
        {
            // Hata kaydı YAN İŞTİR: kendi hatası asıl hata yanıtını bozmamalı ve kullanıcıya
            // yansımamalı. Yalnız yerel günlüğe düşer.
            logger.LogDebug(recordFailure, "Error report could not be stored for route {RoutePattern}", routePattern);
        }
    }

    public async Task<IReadOnlyCollection<ErrorReportDto>> GetRecentAsync(int take, CancellationToken cancellationToken = default)
    {
        var cappedTake = Math.Clamp(take, 1, 200);

        var items = await dbContext.ErrorReports
            .AsNoTracking()
            .OrderByDescending(report => report.LastSeenAtUtc)
            .Take(cappedTake)
            .ToListAsync(cancellationToken);

        return items.Select(ToDto).ToArray();
    }

    public async Task<IReadOnlyCollection<ErrorReportDto>> GetUnsentAsync(int take, CancellationToken cancellationToken = default)
    {
        var cappedTake = Math.Clamp(take, 1, 200);

        var items = await dbContext.ErrorReports
            .AsNoTracking()
            .Where(report => report.SentAtUtc == null)
            .OrderBy(report => report.CreatedAtUtc)
            .Take(cappedTake)
            .ToListAsync(cancellationToken);

        return items.Select(ToDto).ToArray();
    }

    public async Task MarkSentAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default)
    {
        if (ids is null || ids.Count == 0)
        {
            return;
        }

        var idSet = ids.Distinct().ToArray();
        var items = await dbContext.ErrorReports
            .Where(report => idSet.Contains(report.Id))
            .ToListAsync(cancellationToken);

        if (items.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            item.SentAtUtc = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> PurgeAsync(int retentionDays = 30, CancellationToken cancellationToken = default)
    {
        var cappedDays = Math.Clamp(retentionDays, 1, 365);
        var cutoff = DateTime.UtcNow.AddDays(-cappedDays);

        // Yalnız GÖNDERİLMİŞ ve eski kayıtlar silinir: gönderilmemiş kayıt (toplayıcı adresi
        // yokken biriken kayıtlar dâhil) silinirse geliştirici o hatayı hiç göremez.
        var stale = await dbContext.ErrorReports
            .Where(report => report.SentAtUtc != null && report.LastSeenAtUtc < cutoff)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return 0;
        }

        dbContext.ErrorReports.RemoveRange(stale);
        await dbContext.SaveChangesAsync(cancellationToken);
        return stale.Count;
    }

    private async Task EnforceStorageCapAsync(CancellationToken cancellationToken)
    {
        var total = await dbContext.ErrorReports.CountAsync(cancellationToken);
        if (total < MaxStoredReports)
        {
            return;
        }

        // Önce gönderilmişleri (artık geliştiricide var), sonra en eskileri at.
        var overflow = total - MaxStoredReports + 1;
        var victims = await dbContext.ErrorReports
            .OrderBy(report => report.SentAtUtc == null)
            .ThenBy(report => report.LastSeenAtUtc)
            .Take(overflow)
            .ToListAsync(cancellationToken);

        dbContext.ErrorReports.RemoveRange(victims);
    }

    private static string ClampCorrelationId(string value) => Clamp(value, 128);

    private static string Clamp(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static ErrorReportDto ToDto(ErrorReport report) => new()
    {
        Id = report.Id,
        Fingerprint = report.Fingerprint,
        CreatedAtUtc = report.CreatedAtUtc,
        LastSeenAtUtc = report.LastSeenAtUtc,
        PanelVersion = report.PanelVersion,
        OsVersion = report.OsVersion,
        ExceptionType = report.ExceptionType,
        Message = report.Message,
        StackTrace = report.StackTrace,
        RoutePattern = report.RoutePattern,
        CorrelationId = report.CorrelationId,
        OccurrenceCount = report.OccurrenceCount,
        SentAtUtc = report.SentAtUtc
    };
}
