using AdaPanel.Infrastructure.Support;
using AdaPanel.Core.Dtos.Logs;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Logs;

public sealed class LogViewerService(
    IIisSiteService iisSiteService,
    IOptions<PanelRuntimeOptions> runtimeOptions) : ILogViewerService
{
    private readonly string dataRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(runtimeOptions.Value.DataRoot));

    public Task<LogTailDto> TailPanelLogAsync(int lines, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var logsDirectory = Path.Combine(dataRoot, "logs");
        Directory.CreateDirectory(logsDirectory);
        var logFile = new DirectoryInfo(logsDirectory)
            .GetFiles("*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();

        if (logFile is null)
        {
            return Task.FromResult(new LogTailDto
            {
                Name = "AdaPanel",
                Path = logsDirectory
            });
        }

        return Task.FromResult(BuildTail(logFile, "AdaPanel", lines));
    }

    public async Task<LogTailDto> TailSiteLogAsync(string siteName, int lines, CancellationToken cancellationToken = default)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        // IIS'in GERÇEK günlük dizini (sabit kodlu değil) — gerekçe: IisLogPaths.
        var logsDirectory = IisLogPaths.ResolveSiteDirectory(site.Id);

        Directory.CreateDirectory(logsDirectory);
        var logFile = new DirectoryInfo(logsDirectory)
            .GetFiles("*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();

        if (logFile is null)
        {
            return new LogTailDto
            {
                Name = siteName,
                Path = logsDirectory
            };
        }

        return BuildTail(logFile, siteName, lines);
    }

    private static LogTailDto BuildTail(FileInfo fileInfo, string name, int lines)
    {
        var safeLineCount = Math.Clamp(lines, 10, 500);
        var content = ReadTailLines(fileInfo.FullName, safeLineCount);

        return new LogTailDto
        {
            Name = name,
            Path = fileInfo.FullName,
            Lines = content,
            LastModifiedUtc = fileInfo.LastWriteTimeUtc
        };
    }

    /// <summary>Sondan geriye okunacak en fazla bayt. 2 MB, 100 satırlık bir kuyruğa fazlasıyla yeter.</summary>
    private const int TailWindowBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Dosyanın SON satırlarını, dosyanın tamamını okumadan getirir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Önceden dosya baştan sona taranıyordu. Bellek güvenliydi (sınırlı kuyruk) ama G/Ç değildi:
    /// yoğun bir sitenin günlük IIS günlüğü gigabaytlarca olur ve "son 100 satır" isteği o
    /// gigabaytların tamamını diskten okurdu. Sayfa otomatik yenilendiğinde istekler üst üste
    /// binip iş parçacığı havuzunu tüketirdi.
    /// </para>
    /// <para>
    /// Bu, 0.3.6'ya kadar fark edilmiyordu: günlük dizini yanlış hesaplandığı için dosya zaten
    /// hiç bulunamıyordu. Yol düzeltilince bu yol İLK KEZ gerçek dosyalarla çalışır hâle geldi.
    /// </para>
    /// <para>
    /// Artık dosyanın sonundan sabit bir pencere okunur. İlk satır yarım kalmış olabileceği için
    /// atılır — aksi hâlde ekranda bozuk bir satır görünürdü.
    /// </para>
    /// </remarks>
    private static string[] ReadTailLines(string path, int lineCount)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var pencere = Math.Min(stream.Length, TailWindowBytes);
        var bastanMi = pencere == stream.Length;
        stream.Seek(-pencere, SeekOrigin.End);

        using var reader = new StreamReader(stream);
        var tail = new Queue<string>(lineCount);
        var ilkSatir = true;

        while (reader.ReadLine() is { } line)
        {
            // Pencerenin ortasından başladıysak ilk satır yarımdır.
            if (ilkSatir)
            {
                ilkSatir = false;
                if (!bastanMi)
                {
                    continue;
                }
            }

            if (tail.Count == lineCount)
            {
                tail.Dequeue();
            }

            tail.Enqueue(line);
        }

        return tail.ToArray();
    }
}
