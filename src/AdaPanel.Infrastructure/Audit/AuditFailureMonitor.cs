namespace AdaPanel.Infrastructure.Audit;

/// <summary>
/// Yazılamayan denetim kayıtlarının görünür sayacı (süreç ömrü boyunca). Denetim yazımı, ZATEN
/// yapılmış bir sistem değişikliğini geri almamalı; ama kayıp da sessiz kalmamalı. Sağlık/durum
/// uçları bu sayaca bakabilir: sıfırdan büyükse denetim izinde BOŞLUK var demektir.
/// </summary>
public static class AuditFailureMonitor
{
    private static long _count;
    private static long _lastTicksUtc;

    public static long FailureCount => Interlocked.Read(ref _count);

    public static DateTime? LastFailureUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastTicksUtc);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <summary>Sayaç sıfırdan büyükse denetim izi eksik olabilir.</summary>
    public static bool HasFailures => FailureCount > 0;

    internal static void Record()
    {
        Interlocked.Increment(ref _count);
        Interlocked.Exchange(ref _lastTicksUtc, DateTime.UtcNow.Ticks);
    }
}
