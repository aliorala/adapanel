using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using AdaPanel.Infrastructure.Htaccess.Hosting;

namespace AdaPanel.HtaccessModule;

/// <summary>
/// Modülün günlüğü.
///
/// TASARIM KISITI: bu kod her müşteri sitesinin w3wp'si içinde çalışıyor. Panelin ILogger
/// altyapısı burada YOK (net8 ve DI kabı gerektirir). Dolayısıyla günlük elle yazılıyor ve
/// üç kural altında:
///
/// 1) <b>ASLA FIRLATMAZ.</b> Disk dolsa, ACL bozulsa, dosya kilitli olsa bile günlük yazma
///    girişimi isteği DÜŞÜREMEZ. Tam da engellemeye çalıştığımız arıza bu olurdu.
/// 2) <b>HIZ SINIRLI.</b> Her istekte günlük yazan bir modül, siteyi diske bağlar. Patolojik bir
///    .htaccess saniyede binlerce satır üretebilirdi. Saniyede en fazla bir satır yazılır,
///    bastırılanlar SAYILIR ve bir sonraki satırda bildirilir (sessizce yutulmaz).
/// 3) <b>İSTEK VERİSİ YAZILMAZ.</b> Sorgu dizeleri belirteç/parola taşıyabilir. Yalnızca yol
///    ve hata metni yazılır.
/// </summary>
internal static class ModuleLog
{
    private static readonly object Gate = new object();
    private static long lastWriteTicks = long.MinValue;
    private static long suppressed;
    private static string? logDirectory;
    private static bool logDirectoryResolved;

    /// <summary>Saniyede bir satır. Stopwatch tikleri cinsinden.</summary>
    private static readonly long MinimumIntervalTicks = Stopwatch.Frequency;

    internal static void Write(string message, Exception? exception)
    {
        try
        {
            var now = Stopwatch.GetTimestamp();

            lock (Gate)
            {
                if (now - lastWriteTicks < MinimumIntervalTicks)
                {
                    suppressed++;
                    return;
                }

                var bastirilan = suppressed;
                suppressed = 0;
                lastWriteTicks = now;

                var directory = ResolveDirectory();
                if (directory is null)
                {
                    return;
                }

                var builder = new StringBuilder();
                builder.Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                builder.Append("Z pid=");
                builder.Append(SafeProcessId());
                builder.Append(' ');
                builder.Append(message);

                if (bastirilan > 0)
                {
                    // Bastırılan satır sayısı YAZILIR: "günlükte tek satır var, demek ki tek hata
                    // olmuş" yanılgısı, sessiz arızadan farksız olurdu.
                    builder.Append(" [bastirilan=");
                    builder.Append(bastirilan.ToString(CultureInfo.InvariantCulture));
                    builder.Append(']');
                }

                if (exception != null)
                {
                    builder.Append(" | ");
                    builder.Append(exception.GetType().Name);
                    builder.Append(": ");
                    builder.Append(exception.Message);
                }

                builder.Append('\n');

                var file = Path.Combine(
                    directory,
                    "module-" + DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");

                File.AppendAllText(file, builder.ToString());
            }
        }
        catch
        {
            // BILEREK BOŞ. Bkz. sınıf yorumu, kural 1. Burada bir "yedek günlük" denemesi bile
            // yapılmaz -- her yedek yol, yeni bir düşme yolu demektir.
        }
    }

    private static string? ResolveDirectory()
    {
        if (logDirectoryResolved)
        {
            return logDirectory;
        }

        logDirectoryResolved = true;
        try
        {
            var dataRoot = HtaccessHostingPaths.ResolveDataRootFromEnvironment();
            var directory = HtaccessHostingPaths.LogRoot(dataRoot);

            // Klasör YOKSA OLUŞTURULMAZ. Kurulum onu ACL'iyle birlikte kurar; buradan oluşturmak,
            // w3wp'nin (kiracı kimliği) MİRAS ALINMIŞ izinlerle bir klasör yaratması demekti --
            // 0.6.5 turunda tam olarak bu hata ölçülmüştü.
            logDirectory = Directory.Exists(directory) ? directory : null;
        }
        catch
        {
            logDirectory = null;
        }

        return logDirectory;
    }

    private static string SafeProcessId()
    {
        try
        {
            return Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            return "?";
        }
    }
}
