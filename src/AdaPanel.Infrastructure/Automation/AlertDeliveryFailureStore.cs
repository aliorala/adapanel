using AdaPanel.Infrastructure.Support;

namespace AdaPanel.Infrastructure.Automation;

/// <summary>Teslim edilemeyen bir uyarının kaydı (yönetici panelde görsün diye kalıcıdır).</summary>
public sealed class AlertDeliveryFailure
{
    public string Key { get; set; } = string.Empty;
    public DateTime FirstFailedUtc { get; set; }
    public DateTime LastAttemptUtc { get; set; }
    public DateTime NextRetryUtc { get; set; }
    public int Attempts { get; set; }
    public string Detail { get; set; } = string.Empty;
}

/// <summary>
/// Hem e-posta hem webhook başarısız olduğunda uyarının her dakika sessizce yeniden denenmesi yerine
/// üstel geri çekilme (5 dk, 10 dk, 20 dk ... en çok 6 saat) uygular ve başarısızlığı kalıcı kaydeder.
/// Kayıt, Dikkat rozetinde "uyarı teslim edilemiyor" maddesi olarak görünür.
/// </summary>
public sealed class AlertDeliveryFailureStore(JsonFileStore fileStore)
{
    internal const string Path = "state/alert-delivery-failures.json";
    internal static readonly TimeSpan BaseDelay = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromHours(6);

    internal static TimeSpan Backoff(int attempts)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 10);
        var delay = TimeSpan.FromTicks(BaseDelay.Ticks * (1L << exponent));
        return delay > MaxDelay ? MaxDelay : delay;
    }

    public Task<Dictionary<string, AlertDeliveryFailure>> ReadAsync(CancellationToken cancellationToken = default) =>
        fileStore.ReadAsync(Path, new Dictionary<string, AlertDeliveryFailure>(), cancellationToken);

    public async Task RecordFailureAsync(string key, string detail, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await fileStore.UpdateAsync(Path, new Dictionary<string, AlertDeliveryFailure>(), map =>
        {
            map.TryGetValue(key, out var existing);
            var attempts = (existing?.Attempts ?? 0) + 1;
            map[key] = new AlertDeliveryFailure
            {
                Key = key,
                FirstFailedUtc = existing?.FirstFailedUtc ?? nowUtc,
                LastAttemptUtc = nowUtc,
                NextRetryUtc = nowUtc + Backoff(attempts),
                Attempts = attempts,
                Detail = detail.Length > 300 ? detail[..300] : detail
            };
            return map;
        }, cancellationToken);
    }

    /// <summary>Teslim edilen ya da koşulu geçen uyarıların kayıtlarını siler.</summary>
    public async Task ClearAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        if (keys.Count == 0) return;
        await fileStore.UpdateAsync(Path, new Dictionary<string, AlertDeliveryFailure>(), map =>
        {
            foreach (var key in keys) map.Remove(key);
            return map;
        }, cancellationToken);
    }
}
