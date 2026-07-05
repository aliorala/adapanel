namespace AdaPanel.Infrastructure.Automation;

/// <summary>
/// IIS site log dizinlerinden eski dosyaları silen saf temizlik mantığı (Plesk log rotation paritesi).
/// AutomationService günde bir çağırır; ayrık ve durumsuz olduğu için birim testiyle doğrulanır.
/// </summary>
public static class SiteLogCleanup
{
    /// <summary>
    /// <paramref name="directory"/> altındaki (alt dizinler dahil) son yazması <paramref name="cutoffUtc"/>'den
    /// eski *.log dosyalarını siler. Kilitli dosya (günün aktif logu) ve izin hataları sessizce atlanır.
    /// </summary>
    public static (int DeletedCount, long FreedBytes) DeleteOldLogs(string directory, DateTime cutoffUtc)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return (0, 0);
        }

        var deleted = 0;
        long freed = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.log", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < cutoffUtc)
                {
                    var size = info.Length;
                    info.Delete();
                    deleted++;
                    freed += size;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Kilitli (IIS'in yazmakta olduğu bugünkü log) veya erişilemeyen dosya atlanır.
            }
        }

        return (deleted, freed);
    }
}
