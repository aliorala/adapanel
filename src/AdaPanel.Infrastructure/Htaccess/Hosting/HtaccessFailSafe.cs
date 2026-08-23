using System;
using System.Diagnostics;
using System.Threading;

namespace AdaPanel.Infrastructure.Htaccess.Hosting;

/// <summary>Bir fail-safe çağrısının sonucu — teşhis ve test için.</summary>
internal enum HtaccessOutcome
{
    /// <summary>İş çalıştı ve sonuç döndü.</summary>
    Succeeded = 0,

    /// <summary>İş istisna fırlattı; istek DOKUNULMADAN geçirildi.</summary>
    Faulted = 1,

    /// <summary>İş bütçeyi aştı; sonucu KULLANILDI ama devre kesici sayacı arttı.</summary>
    Slow = 2,

    /// <summary>Devre kesici AÇIK; iş HİÇ çağrılmadı.</summary>
    ShortCircuited = 3
}

/// <summary>
/// FAIL-OPEN YÜRÜTÜCÜ — bu modülün PAZARLIK DIŞI sözü burada uygulanır:
/// <b>modülde ne olursa olsun istek bozulmaz.</b>
///
/// Üç savunma katmanı, üçü de ayrı bir arıza sınıfı için:
///
/// A) İSTİSNA YUTMA. <see cref="Exception"/> yakalanır ve çağıran yedek değeri alır. Bozuk bir
///    .htaccess, null başvuru, yol istisnası, taşma — hepsi "modül bu istekte yokmuş gibi davran"a
///    dönüşür. Site 500 GÖRMEZ.
///
/// B) DEVRE KESİCİ. Arıza yutmak TEK BAŞINA YETMEZ: sürekli patlayan bir .htaccess her istekte
///    istisna maliyeti + günlük yazımı üretir; site çalışır ama SÜRÜNÜR. Üst üste
///    <see cref="failureThreshold"/> arızadan sonra devre AÇILIR ve soğuma süresince iş HİÇ
///    çağrılmaz — maliyet tam olarak bir karşılaştırmaya iner.
///
/// C) YAVAŞ ÇAĞRI TESPİTİ. Patolojik bir düzenli ifade İSTİSNA FIRLATMAZ, sadece uzun sürer.
///    Süre bütçeyi aşarsa sonuç yine kullanılır (o istek zaten bitti) ama arıza sayılır → aynı
///    .htaccess birkaç kez yavaşlarsa devre açılır ve o site için modül kendini kapatır.
///
/// <b>DÜRÜSTLÜK — bu katmanın KAPATAMADIĞI iki şey:</b>
/// 1) Süren bir çağrıyı KESEMEZ. Devam eden bir geri izleme (catastrophic backtracking) ancak
///    <c>Regex</c> nesnesine <c>matchTimeout</c> verilerek durdurulabilir; bu ÇEKİRDEK MOTORUN
///    sorumluluğudur ve bu katman onu telafi edemez. Burada yapılan, aynı kurbanı İKİNCİ KEZ
///    vermemektir.
/// 2) <see cref="StackOverflowException"/> yakalanamaz (.NET'te süreç anında ölür). Motorun
///    özyinelemesi SINIRLI olmak zorundadır.
/// Bu ikisi çekirdek motor ekibine yazılı gereksinim olarak verilmelidir.
/// </summary>
internal sealed class HtaccessFailSafe
{
    private readonly int failureThreshold;
    private readonly long cooldownTicks;
    private readonly long budgetTicks;
    private readonly Func<long> monotonicTicks;
    private readonly Action<string, Exception?>? log;

    private int consecutiveFailures;
    private long openUntilTicks = long.MinValue;

    /// <summary>Toplam sayaçlar — panelin "bu modül sağlıklı mı" sorusuna cevap verebilmesi için.</summary>
    internal long TotalFaults;
    internal long TotalSlow;
    internal long TotalShortCircuits;

    internal HtaccessFailSafe(
        int failureThreshold = 5,
        TimeSpan? cooldown = null,
        TimeSpan? budget = null,
        Func<long>? monotonicTicks = null,
        Action<string, Exception?>? log = null)
    {
        this.failureThreshold = failureThreshold < 1 ? 1 : failureThreshold;
        this.monotonicTicks = monotonicTicks ?? Stopwatch.GetTimestamp;
        this.log = log;
        this.cooldownTicks = ToTicks(cooldown ?? TimeSpan.FromSeconds(60));
        this.budgetTicks = ToTicks(budget ?? TimeSpan.FromMilliseconds(50));
    }

    internal bool IsOpen => monotonicTicks() < Volatile.Read(ref openUntilTicks);

    /// <summary>
    /// İşi fail-open olarak çalıştırır. <b>Bu metot istisna FIRLATMAZ.</b>
    /// Başarısız her yolda <paramref name="fallback"/> döner — yani "modül yokmuş gibi".
    /// </summary>
    internal T Execute<T>(Func<T> work, T fallback, string operation, out HtaccessOutcome outcome)
    {
        if (work is null)
        {
            outcome = HtaccessOutcome.Faulted;
            return fallback;
        }

        var now = monotonicTicks();
        if (now < Volatile.Read(ref openUntilTicks))
        {
            TotalShortCircuits++;
            outcome = HtaccessOutcome.ShortCircuited;
            return fallback;
        }

        T result;
        long elapsed;
        try
        {
            var started = monotonicTicks();
            result = work();
            elapsed = monotonicTicks() - started;
        }
        catch (Exception exception)
        {
            // BURASI SÖZÜN TUTULDUĞU YER. Filtre YOK: belirli istisna tiplerini seçmek, seçilmeyen
            // bir tipin siteyi düşürmesi demektir. "Beklenmeyen istisna" kavramı bu modülde yasaktır.
            TotalFaults++;
            RegisterFailure(operation, exception);
            outcome = HtaccessOutcome.Faulted;
            return fallback;
        }

        if (elapsed > budgetTicks)
        {
            TotalSlow++;
            RegisterFailure(operation + " (butce asildi)", null);
            outcome = HtaccessOutcome.Slow;
            return result;
        }

        // Başarılı çağrı sayacı SIFIRLAR: eşik "üst üste" arıza içindir. Sıfırlanmasaydı, ayda bir
        // arıza veren sağlıklı bir site günler içinde devreyi açardı.
        Interlocked.Exchange(ref consecutiveFailures, 0);
        outcome = HtaccessOutcome.Succeeded;
        return result;
    }

    private void RegisterFailure(string operation, Exception? exception)
    {
        var count = Interlocked.Increment(ref consecutiveFailures);
        if (count < failureThreshold)
        {
            SafeLog("htaccess: " + operation + " basarisiz (" + count + "/" + failureThreshold + ")", exception);
            return;
        }

        Interlocked.Exchange(ref consecutiveFailures, 0);
        Volatile.Write(ref openUntilTicks, monotonicTicks() + cooldownTicks);
        SafeLog("htaccess: DEVRE ACILDI (" + operation + ") - modul bu uygulama havuzunda gecici olarak devre disi", exception);
    }

    /// <summary>
    /// Günlük yazımı bile fail-open olmak zorunda: disk dolduğunda günlük yazma girişiminin
    /// isteği düşürmesi, tam da engellemeye çalıştığımız şey olurdu.
    /// </summary>
    private void SafeLog(string message, Exception? exception)
    {
        try
        {
            log?.Invoke(message, exception);
        }
        catch
        {
            // Bilerek boş. Bkz. yukarıdaki gerekçe.
        }
    }

    private static long ToTicks(TimeSpan value)
    {
        var ticks = (long)(value.TotalSeconds * Stopwatch.Frequency);
        return ticks < 0 ? 0 : ticks;
    }
}
