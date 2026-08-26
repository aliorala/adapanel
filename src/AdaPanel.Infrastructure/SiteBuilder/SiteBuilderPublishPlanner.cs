namespace AdaPanel.Infrastructure.SiteBuilder;

/// <summary>Yayın kararı: neyi ezeceğiz, izin var mı.</summary>
public sealed record PublishDecision(
    IReadOnlyList<string> FilesToWrite,
    IReadOnlyList<string> Conflicts,
    bool RequiresConfirmation,
    bool Allowed);

/// <summary>
/// "Bu yayın müşterinin hangi dosyalarını ezer ve izin var mı?" sorusunun TEK cevabı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden saf bir fonksiyon:</b> bu karar, panelin müşteri verisini yok edebilecek en yakın
/// olduğu yer. Gerçek dosya sistemiyle sınanan bir kural, ancak kurulabilen senaryolar kadar
/// sınanır; saf bir karar ise her sınır durumu için tek satırda sınanır — ve mutasyonla
/// (kuralı kasten bozup testlerin kırmızıya döndüğünü görmek) doğrulanabilir.
/// </para>
/// <para>
/// <b>Neden "yönetilen dosya" listesi:</b> kendi ürettiğimiz <c>index.html</c>'i her yayında
/// "müşterinin dosyası" sayarsak kullanıcı her seferinde onay kutusu görür. Her seferinde çıkan
/// bir uyarı, okunmayan bir uyarıdır: kullanıcı onaylamayı refleks hâline getirir ve uyarı, tam
/// da GERÇEKTEN başkasının dosyası ezilecekken işe yaramaz olur.
/// </para>
/// <para>
/// <b>Silme YOK:</b> bu planlayıcı yalnız YAZILACAK dosyaları döndürür. Hiçbir yerde "artık
/// üretilmeyen dosyayı sil" adımı yoktur. Sebep bu depoda yaşanmış: FTP klasör bağlantısında
/// özyinelemeli silme müşteri sitesini silebiliyordu. Yayın, eskiyen bir dosyayı diskte bırakır —
/// bu, yanlış bir dosyayı silmekten her zaman daha iyidir.
/// </para>
/// <para>
/// <b>Ama SESSİZ de değil (çok sayfa desteğiyle gelen karar):</b> kullanıcı bir sayfayı kurucudan
/// kaldırdığında ya da adını değiştirdiğinde eski HTML adresinde durmaya devam eder. Bunu hiç
/// söylememek, kullanıcının "sildim" sandığı bir sayfanın yayında kalması demekti.
/// <see cref="Orphans"/> o dosyaları ADIYLA döndürür; silmez, gösterir. Kararı dosyanın sahibi
/// verir — geri alınamaz olan taraf silme tarafıdır.
/// </para>
/// </remarks>
public static class SiteBuilderPublishPlanner
{
    public static PublishDecision Decide(
        IReadOnlyCollection<string> filesToWrite,
        IReadOnlyCollection<string> existingRelativePaths,
        IReadOnlyCollection<string> managedFiles,
        bool overwriteConfirmed)
    {
        var writes = filesToWrite
            .Select(Normalize)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // KORUMALI YOLLAR: yazma listesine hiçbir koşulda giremezler. Bu bir "ekstra güvence"
        // değil, kuralın uygulandığı yer: üretici bir gün ".well-known/x" yazmak istese bile
        // yayın onu taşımaz. (Plesk/Sitejet tarafında yayının .well-known'ı silmesi, sertifika
        // yenilemesini bozan bilinen bir arızaydı.)
        writes = writes.Where(path => !SiteBuilderLayout.IsProtected(path)).ToList();

        var existing = new HashSet<string>(
            existingRelativePaths.Select(Normalize).Where(path => path.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        var managed = new HashSet<string>(
            managedFiles.Select(Normalize).Where(path => path.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        // ÇAKIŞMA = orada duruyor VE daha önce bizim yazdığımız bir dosya değil.
        var conflicts = writes
            .Where(path => existing.Contains(path) && !managed.Contains(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var requiresConfirmation = conflicts.Count > 0;
        return new PublishDecision(
            writes,
            conflicts,
            requiresConfirmation,
            !requiresConfirmation || overwriteConfirmed);
    }

    /// <summary>
    /// ÖKSÜZ dosyalar: daha önce KURUCUNUN yazdığı ama bu yayında artık üretilmeyenler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bu fonksiyon hiçbir şey SİLMEZ ve silinecekleri de döndürmez</b> — "artık üretilmiyor"
    /// diyen bir listeyi döndürür. Ad bilinçli olarak <c>FilesToDelete</c> değil: bir gün biri
    /// bu listeyi <c>File.Delete</c>'e beslemek istediğinde, adın kendisi bunun planlanmış bir
    /// davranış OLMADIĞINI söylemeli.
    /// </para>
    /// <para>
    /// <b>Neden yalnız YÖNETİLEN dosyalar:</b> kaynak, önceki yayında bizim yazdığımız dosyaların
    /// listesidir. Hedef klasörü tarayıp "tasarımda karşılığı olmayan her dosya" demek, müşterinin
    /// kendi yüklediği her şeyi öksüz ilan etmek olurdu — ve o liste bir gün bir "temizle"
    /// düğmesine bağlanırsa siteyi silen şey tam olarak o olurdu.
    /// </para>
    /// <para>
    /// <b>Neden görseller de sayılıyor:</b> tasarımdan çıkarılan bir görsel de diskte kalır. Ayrı
    /// bir kural yazıp onu listeden çıkarmak, "hangi dosya öksüz" sorusuna iki farklı cevap
    /// üretirdi; tek kural, tek liste.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Orphans(
        IReadOnlyCollection<string> managedFiles,
        IReadOnlyCollection<string> filesToWrite)
    {
        var writes = new HashSet<string>(
            filesToWrite.Select(Normalize).Where(path => path.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        return managedFiles
            .Select(Normalize)
            .Where(path => path.Length > 0 && !writes.Contains(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Göreli yolu karşılaştırılabilir tek biçime getirir.
    /// </summary>
    /// <remarks>
    /// Ayırıcıyı birleştirmezsek "assets/adapanel/site.css" ile "assets\adapanel\site.css" iki ayrı
    /// dosya sayılır: diskteki dosya çakışma listesinde GÖRÜNMEZ, kullanıcı uyarı almadan ezilir.
    /// Sessiz veri kaybının klasik biçimi budur.
    /// </remarks>
    private static string Normalize(string? path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
}
