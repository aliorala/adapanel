namespace AdaPanel.Infrastructure.Support;

/// <summary>Yana alınmış tek bir üst düzey girdi (dosya ya da klasör).</summary>
/// <param name="Name">Girdinin kök içindeki adı; geri koyarken aynı ada dönülür.</param>
/// <param name="SidelinePath">Girdinin şu an durduğu yer.</param>
/// <param name="IsDirectory">Klasör mü? Geri koyarken doğru taşıma çağrısını seçmek için gerekir.</param>
public sealed record SidelinedEntry(string Name, string SidelinePath, bool IsDirectory);

/// <summary>
/// "Bir klasörün üzerine yazmadan ÖNCE mevcut içeriği yana al" kuralının TEK uygulaması.
/// <para>
/// Panelde bu iş üç ayrı yerde ayrı ayrı yazılmıştı ve her biri farklı derecede eksikti:
/// site göçü içeriği kardeş <c>_goc_yedek_</c> klasörüne taşıyordu (doğru), bileşen kurulumu
/// yalnızca bilinen veri klasörlerini koruyordu (kısmen doğru), uygulama kurulumu ise yalnızca
/// 5 sabit DOSYA adını yedekleyip geri alırken KLASÖRLERİ özyinelemeli siliyordu (veri kaybı).
/// Aynı kuralın üç kopyası, birini düzeltip diğerini unutmanın yoludur.
/// </para>
/// <para>
/// KURAL: taşıma başarısızsa üzerine yazma İŞLEMİ BAŞLAMAZ. Veriyi koruyamıyorsak, koruduğumuzu
/// sanarak devam etmek en kötü seçenektir — kullanıcı "yedeklendi/kuruldu" yazısına güvenir.
/// </para>
/// </summary>
public static class ContentSideline
{
    /// <summary>
    /// Köke KARDEŞ (kökün DIŞINDA) bir yana-alma klasörü yolu üretir. Kökün içinde olsaydı
    /// "klasörü boşalt" işlemi kendi yedeğini de kapsar ve yeni içerikle karışırdı.
    /// </summary>
    public static string BuildSiblingPath(string root, string label)
    {
        var trimmed = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Sürücü kökünde ("C:\") kardeş klasör diye bir şey yoktur; oraya kurulum/geri yükleme
        // yapılmasına da izin verilmemeli. Sessizce "C_yedek_..." üretip şaşırtmaktansa reddet.
        if (Path.GetDirectoryName(trimmed) is null)
        {
            throw new IOException($"'{root}' bir sürücü kökü; yana alma klasörü oluşturulamaz.");
        }

        return $"{trimmed}_{label}_{DateTime.Now:yyyyMMdd_HHmmss}";
    }

    /// <summary>
    /// <paramref name="root"/> içindeki seçili ÜST DÜZEY girdileri (dosya + klasör)
    /// <paramref name="sidelineDirectory"/> altına TAŞIR ve taşınanların listesini döner.
    /// <para>
    /// Herhangi bir taşıma başarısız olursa o ana kadar taşınanlar YERİNE KONUR ve
    /// <see cref="IOException"/> fırlatılır: çağıran işi hiç başlatmamış olur.
    /// </para>
    /// </summary>
    public static IReadOnlyList<SidelinedEntry> MoveAside(
        string root,
        string sidelineDirectory,
        Func<FileSystemInfo, bool>? selector = null)
    {
        var moved = new List<SidelinedEntry>();
        if (!Directory.Exists(root))
        {
            return moved;
        }

        var candidates = new DirectoryInfo(root)
            .EnumerateFileSystemInfos()
            .Where(item => selector is null || selector(item))
            .ToArray();
        if (candidates.Length == 0)
        {
            return moved;
        }

        Directory.CreateDirectory(sidelineDirectory);
        foreach (var item in candidates)
        {
            var destination = Path.Combine(sidelineDirectory, item.Name);
            try
            {
                if (item is DirectoryInfo directory)
                {
                    Directory.Move(directory.FullName, destination);
                }
                else
                {
                    File.Move(item.FullName, destination);
                }

                moved.Add(new SidelinedEntry(item.Name, destination, item is DirectoryInfo));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Yarım kalmış bir "yana alma" en kötü durumdur: kök ne eski ne yeni hâldedir.
                // Önce geri sar, sonra çağırana "başlama" de.
                MoveBack(root, moved);
                throw new IOException(
                    $"'{item.FullName}' yana alınamadı ({exception.Message}). " +
                    $"Yana alma klasörü: {sidelineDirectory}",
                    exception);
            }
        }

        return moved;
    }

    /// <summary>
    /// <see cref="MoveAside"/> ile alınan girdileri kökteki yerlerine geri taşır.
    /// <para>
    /// FIRLATMAZ: geri koyma zaten bir hata yolunda çağrılır ve orada ikinci bir istisna asıl
    /// hatayı gizler. Başarısız girdiler <paramref name="onFailure"/> ile bildirilir; VERİ
    /// KAYBOLMAZ, yana alma klasöründe durmaya devam eder.
    /// </para>
    /// </summary>
    public static void MoveBack(
        string root,
        IEnumerable<SidelinedEntry> entries,
        Action<SidelinedEntry, Exception>? onFailure = null)
    {
        Directory.CreateDirectory(root);
        foreach (var entry in entries)
        {
            var target = Path.Combine(root, entry.Name);
            try
            {
                // Araya giren işlem aynı adla BOŞ bir kabuk bırakmış olabilir (kurulum paketi
                // klasörü açar, dosyayı yazamaz); taşımayı engellemesin.
                if (entry.IsDirectory)
                {
                    if (Directory.Exists(target) && !Directory.EnumerateFileSystemEntries(target).Any())
                    {
                        Directory.Delete(target);
                    }

                    Directory.Move(entry.SidelinePath, target);
                }
                else
                {
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }

                    File.Move(entry.SidelinePath, target);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                onFailure?.Invoke(entry, exception);
            }
        }
    }

    /// <summary>
    /// İçi boşalan yana-alma klasörünü siler. Boş DEĞİLSE dokunmaz: içinde kalan her şey geri
    /// konamamış müşteri verisidir ve silinmesi kaybın kendisi olur.
    /// </summary>
    public static void RemoveIfEmpty(string? sidelineDirectory)
    {
        if (string.IsNullOrWhiteSpace(sidelineDirectory) || !Directory.Exists(sidelineDirectory))
        {
            return;
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(sidelineDirectory).Any())
            {
                Directory.Delete(sidelineDirectory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Boş klasör silinemediyse zararı yok; bir dahaki sefere kalır.
        }
    }
}
