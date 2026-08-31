namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// Geri yüklemede ARŞİVDEN gelen verinin kaynak tavanlarının TEK kaynağı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden var:</b> geri yükleme, panelde saldırganın verdiği bir dosyanın LocalSystem yetkisiyle
/// işlendiği tek yüzeydir; arşivin içi tamamen onu üretenin denetimindedir. Zamanlanmış görev
/// XML'ine 0.8.13'te 1 MiB tavan konmuştu, ama <b>aynı arşivden okunan diğer ENVANTER dosyaları
/// tavansızdı</b>: <c>deleted.json</c>, <c>index.json</c>, <c>iis/sites.json</c>,
/// <c>config/ftp-users.json</c>, <c>ftp/accounts.json</c>, <c>mail/*.json</c>,
/// <c>sites/&lt;ad&gt;/config/site.json</c>. Hepsi TAMAMEN belleğe okunup deserialize edilir, yani
/// birkaç megabaytlık bir arşiv (zip sıkıştırması sıfırlarda 1000:1'i aşar) gigabaytlarca bellek
/// isteyebiliyordu — bir tavan ölçüsü, tek başına paneli düşürmeye yeter.
/// </para>
/// <para>
/// <b>Neden bu değer:</b> bu dosyalar ENVANTERDİR, veri değil. Ölçülen gerçek boyutlar kilobayt
/// mertebesindedir (bir görev XML'i birkaç KB, yüzlerce siteli bir <c>iis/sites.json</c> birkaç
/// yüz KB). 16 MiB, en büyük gerçek kurulumda bile fazlasıyla cömerttir ve meşru hiçbir yedeği
/// engellemez. <b>DOSYA İÇERİĞİNE (files/*) UYGULANMAZ</b> — orada bir tavan, 40 GB'lık bir siteyi
/// geri yükleyemez hâle getirirdi ve bu, açığın kendisinden pahalıya gelirdi.
/// </para>
/// <para>
/// Ölçü, girdinin arşivde BEYAN EDİLEN açılmış boyutundan (<c>ZipArchiveEntry.Length</c>) okunur;
/// yani tek bayt açılmadan önce karar verilir.
/// </para>
/// </remarks>
internal static class BackupArchiveLimits
{
    /// <summary>Arşivden okunan ENVANTER/JSON girdisinin en büyük açılmış boyutu.</summary>
    internal const long MaxMetadataBytes = 16L * 1024L * 1024L;

    /// <summary>
    /// Girdi tavanı aşıyor mu? <c>null</c> girdi (arşivde yok) hiçbir zaman aşmaz.
    /// </summary>
    internal static bool ExceedsMetadataLimit(System.IO.Compression.ZipArchiveEntry? entry) =>
        entry is not null && entry.Length > MaxMetadataBytes;

    /// <summary>Uyarı ayrıntısı: çevrilmeyen etiket-değer satırı (bu depodaki Detail kuralı).</summary>
    internal static string DescribeTooLarge(System.IO.Compression.ZipArchiveEntry entry) =>
        $"entry={entry.FullName}; bytes={entry.Length}; limit={MaxMetadataBytes}";
}
