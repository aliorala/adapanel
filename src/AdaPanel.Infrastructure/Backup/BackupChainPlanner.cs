using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Backup;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// Artımsal yedek ZİNCİRİNİN bütün kuralları. Saf (dosya sistemine dokunmayan) karar fonksiyonları
/// burada toplanır; <see cref="BackupService"/> yalnızca uygular.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı ve saf:</b> zincirin kuralları veri kaybına en yakın kurallardır ("bu yedek
/// silinebilir mi", "bu zincir sağlam mı", "hangi sırayla uygulanacak"). Bunları on bağımlılıklı
/// bir servisin içinden ölçmek zordur; burada tek başına ölçülebilir hâlde dururlar.
/// </para>
/// <para>
/// <b>Model Plesk'ten alınmıştır</b> (icat edilmemiştir): tam yedek + ardışık artımsal halkalar;
/// geri yükleme tam yedekten başlayıp halkaları sırayla uygular; bir halka giderse ondan
/// SONRAKİLER kullanılamaz hâle gelir ve bu kullanıcıya açıkça söylenir.
/// </para>
/// </remarks>
internal static class BackupChainPlanner
{
    public const string ModeFull = "full";
    public const string ModeIncremental = "incremental";

    /// <summary>İki indeks arasındaki fark: yedeğe girecek dosyalar + silinmiş dosyalar.</summary>
    /// <param name="Changed">Yeni ya da değişmiş dosyaların arşiv yolları.</param>
    /// <param name="Deleted">Önceki indekste olup artık olmayan dosyaların arşiv yolları.</param>
    public sealed record IndexDiff(IReadOnlyList<string> Changed, IReadOnlyList<string> Deleted);

    /// <summary>
    /// Önceki indeksle şimdiki durumu karşılaştırır.
    /// <para>
    /// <paramref name="previous"/> <c>null</c> ise (ilk yedek ya da temel indeks okunamadı) HER ŞEY
    /// değişmiş sayılır — yani sonuç TAM yedektir. "Bilmiyorsak her şeyi al" tek güvenli varsayımdır:
    /// tersi, temeli bilinmeyen ve geri yüklenemeyen bir artımsal halka üretirdi.
    /// </para>
    /// </summary>
    public static IndexDiff Diff(BackupFileIndex? previous, BackupFileIndex current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (previous is null)
        {
            return new IndexDiff(current.Entries.Select(entry => entry.Path).ToArray(), []);
        }

        var oncekiler = new Dictionary<string, BackupFileIndexEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in previous.Entries)
        {
            oncekiler[entry.Path] = entry;
        }

        var degisen = new List<string>();
        var kalanlar = new HashSet<string>(oncekiler.Keys, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in current.Entries)
        {
            kalanlar.Remove(entry.Path);

            // Üç soru (Plesk ile aynı ölçü): indekste var mıydı, boyutu aynı mı, tarihi aynı mı.
            if (!oncekiler.TryGetValue(entry.Path, out var onceki) ||
                onceki.Size != entry.Size ||
                onceki.ModifiedUtcTicks != entry.ModifiedUtcTicks ||
                !string.Equals(onceki.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                degisen.Add(entry.Path);
            }
        }

        // Kalanlar = önceki indekste olup şimdi olmayanlar → SİLİNMİŞ. Bunları taşımazsak geri
        // yükleme, kullanıcının bilerek sildiği dosyaları geri getirirdi (WordPress'ten kaldırılan
        // zararlı bir eklenti gibi).
        return new IndexDiff(degisen, kalanlar.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    /// <summary>
    /// Aynı kaynak ve zincirin bütün dallarını döner. Saklama politikası bu grubu tek
    /// geri yükleme noktası sayar; belirli bir yaprağın yolu için Resolve kullanılır.
    /// </summary>
    public static IReadOnlyList<BackupRecordDto> GetChain(
        IEnumerable<BackupRecordDto> all, string chainId, string? type = null, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(chainId))
        {
            return [];
        }

        var records = all
            .Where(record => string.Equals(record.ChainId, chainId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (type is not null || source is not null)
            records = records.Where(record => BackupSourceIdentity.Same(record, type ?? string.Empty, source ?? string.Empty)).ToArray();
        else if (records.Select(BackupSourceIdentity.Key).Distinct(StringComparer.Ordinal).Skip(1).Any())
            return []; // An unscoped, reused chain ID cannot choose one source on the caller's behalf.

        return records
            .OrderBy(record => record.ChainIndex)
            .ThenBy(record => record.CreatedAtUtc)
            .ToArray();
    }

    /// <summary>
    /// <paramref name="backupId"/> kaydının geri yüklenebilmesi için gereken halkaları
    /// TAM YEDEKTEN hedefe doğru sıralı olarak çözer.
    /// </summary>
    /// <param name="dosyaVarMi">
    /// Bir kaydın arşiv dosyasının gerçekten yerinde olup olmadığını ölçen işlev. Kayıt duruyor
    /// diye halkanın var olduğunu VARSAYMAK, kopuk zinciri sağlam sanmak olurdu.
    /// </param>
    public static BackupChainDto Resolve(
        IEnumerable<BackupRecordDto> all,
        string backupId,
        Func<BackupRecordDto, bool> dosyaVarMi)
    {
        var kayitlar = all as IReadOnlyCollection<BackupRecordDto> ?? all.ToArray();
        var hedef = kayitlar.FirstOrDefault(record => string.Equals(record.Id, backupId, StringComparison.Ordinal));
        if (hedef is null)
        {
            return new BackupChainDto { Broken = true, MissingLinks = [backupId] };
        }

        var graph = BuildGraph(kayitlar, hedef);
        if (graph.Errors.Count > 0)
            return new BackupChainDto
            {
                ChainId = hedef.ChainId,
                Links = graph.Records.Where(item => item.ChainIndex <= hedef.ChainIndex)
                    .Select(item => ToLink(item, dosyaVarMi(item))).ToArray(),
                Broken = true,
                MissingLinks = graph.Errors.ToArray()
            };

        // Zinciri olmayan (eski ya da tekil) yedek: kendi başına tam bir geri yükleme noktasıdır.
        if (string.IsNullOrWhiteSpace(hedef.ChainId))
        {
            return new BackupChainDto
            {
                ChainId = string.Empty,
                Links = [ToLink(hedef, dosyaVarMi(hedef))],
                Broken = !dosyaVarMi(hedef),
                MissingLinks = dosyaVarMi(hedef) ? [] : [hedef.Name]
            };
        }

        // Follow only this leaf's parents. Equal indices in an explicit sibling branch
        // are legitimate and do not belong in this restore operation.
        var path = new List<BackupRecordDto>();
        for (BackupRecordDto? current = hedef; current is not null; current = graph.Parents[current.Id])
            path.Add(current);
        path.Reverse();
        var halkalar = path.Select(item => ToLink(item, dosyaVarMi(item))).ToArray();
        var eksikler = halkalar.Where(item => !item.Available).Select(item => item.Name).ToArray();

        return new BackupChainDto
        {
            ChainId = hedef.ChainId,
            Links = halkalar,
            Broken = eksikler.Length > 0,
            MissingLinks = eksikler.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    internal static int SequenceInspectionLimit(int targetIndex, int knownRecords) =>
        (int)Math.Max(0, Math.Min(int.MaxValue, Math.Min((long)targetIndex + 1, (long)knownRecords + 1)));

    private static BackupChainLinkDto ToLink(BackupRecordDto record, bool available) => new()
    {
        BackupId = record.Id,
        Name = record.Name,
        ChainIndex = record.ChainIndex,
        BackupMode = string.IsNullOrWhiteSpace(record.BackupMode) ? ModeFull : record.BackupMode,
        CreatedAtUtc = record.CreatedAtUtc,
        Size = record.Size,
        Available = available
    };

    /// <summary>
    /// <paramref name="record"/> silinirse KULLANILAMAZ hâle gelecek kayıtlar (kendisi hariç):
    /// aynı zincirde ebeveyn bağlantılarıyla ona ulaşan alt halkalar.
    /// </summary>
    /// <remarks>
    /// <b>Tuzağın tam yeri burasıdır:</b> zincirin ortasındaki bir yedeği (özellikle 0 numaralı TAM
    /// yedeği) silmek, sonraki artımsalları sessizce çöpe çevirir. Panelde onlarca "yedek" görünür
    /// ama hiçbiri geri yüklenemez. Plesk bu yüzden silmeyi zincire YAYAR; panel de aynısını yapar
    /// ama önce SORAR: tek yedek silme isteği bağımlı halka varsa REDDEDİLİR
    /// (<c>backup.chain_has_dependents</c>), kullanıcı ne kaybedeceğini görerek zincir silmeye geçer.
    /// </remarks>
    public static IReadOnlyList<BackupRecordDto> GetDependents(IEnumerable<BackupRecordDto> all, BackupRecordDto record)
    {
        var graph = BuildGraph(all, record);
        if (graph.Errors.Count > 0)
            throw new AppException("backup.chain_ambiguous", 409, 409);
        var selected = new HashSet<string>(StringComparer.Ordinal) { record.Id };
        var result = new List<BackupRecordDto>();
        foreach (var item in graph.Records)
            if (graph.Parents.TryGetValue(item.Id, out var parent) && parent is not null && selected.Contains(parent.Id))
            {
                selected.Add(item.Id);
                result.Add(item);
            }
        return result;
    }

    private sealed record ChainGraph(IReadOnlyList<BackupRecordDto> Records,
        Dictionary<string, BackupRecordDto?> Parents, IReadOnlyCollection<string> Errors);

    private static ChainGraph BuildGraph(IEnumerable<BackupRecordDto> all, BackupRecordDto target)
    {
        var records = all as IReadOnlyCollection<BackupRecordDto> ?? all.ToArray();
        IReadOnlyList<BackupRecordDto> scoped = string.IsNullOrWhiteSpace(target.ChainId)
            ? [target] : GetChain(records, target.ChainId, target.Type, target.Source);
        var parents = new Dictionary<string, BackupRecordDto?>(StringComparer.Ordinal);
        var errors = new HashSet<string>(StringComparer.Ordinal);
        var identities = records.GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var item in scoped)
            if (string.IsNullOrWhiteSpace(item.Id) || identities.GetValueOrDefault(item.Id) != 1)
                errors.Add(item.Name);
        if (string.IsNullOrWhiteSpace(target.ChainId)) return new(scoped, parents, errors);

        var byId = scoped.GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var byIndex = scoped.GroupBy(item => item.ChainIndex).ToDictionary(group => group.Key, group => group.ToArray());
        if (!byIndex.TryGetValue(0, out var roots) || roots.Length != 1)
            errors.Add("#0");
        // Preserve useful gap diagnostics without iterating untrusted, potentially huge indices.
        for (var index = 0; index < SequenceInspectionLimit(target.ChainIndex, scoped.Count); index++)
            if (!byIndex.ContainsKey(index)) errors.Add($"#{index}");
        foreach (var item in scoped)
        {
            BackupRecordDto? parent = null;
            if (item.ChainIndex < 0) errors.Add(item.Name);
            else if (item.ChainIndex == 0)
            {
                if (!string.IsNullOrWhiteSpace(item.BaseBackupId) ||
                    (!string.IsNullOrWhiteSpace(item.BackupMode) && !string.Equals(item.BackupMode, ModeFull, StringComparison.OrdinalIgnoreCase)))
                    errors.Add(item.Name);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(item.BaseBackupId)) byId.TryGetValue(item.BaseBackupId, out parent);
                else if (byIndex.TryGetValue(item.ChainIndex - 1, out var candidates) && candidates.Length == 1)
                    parent = candidates[0];
                if (parent is null || parent.ChainIndex != item.ChainIndex - 1) errors.Add(item.Name);
            }
            parents[item.Id] = parent;
        }
        // A strictly decreasing parent index ending at the sole root also excludes cycles.
        return new(scoped, parents, errors);
    }

    /// <summary>
    /// Saklama budaması bu zincire dokunabilir mi?
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kural: <b>zincir bir bütündür; ancak EN YENİ halkası da süresi dolduğunda budanır.</b>
    /// </para>
    /// <para>
    /// Naif bir budama yalnızca tarihe bakar ve 30 gün önceki TAM yedeği siler; dün alınmış
    /// artımsal halka kaydı listede durmaya devam eder ama artık hiçbir işe yaramaz. Kullanıcı
    /// bunu ancak felaket anında, geri yüklemeye çalışırken öğrenir. Tam yedeği tutmanın disk
    /// maliyeti, geri dönülemez veri kaybının yanında hiçbir şeydir.
    /// </para>
    /// </remarks>
    public static bool IsChainPrunable(IReadOnlyList<BackupRecordDto> chain, DateTime cutoffUtc)
    {
        if (chain.Count == 0)
        {
            return true;
        }

        return chain.Max(record => record.CreatedAtUtc) < cutoffUtc;
    }

    /// <summary>
    /// Yeni halkanın TAM mı ARTIMSAL mı olacağına karar verir (Plesk'in "tam yedek periyodu" kuralı).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Üç durumda ZORUNLU olarak TAM yedek alınır:
    /// (1) zincir hiç yoksa — ilk yedek her zaman tamdır;
    /// (2) tam yedek periyodu dolduysa — zincir sonsuza kadar uzarsa geri yükleme onlarca halkanın
    ///     sırayla uygulanmasına dönüşür ve tek bir bozuk halka her şeyi götürür;
    /// (3) temel indeks okunamıyorsa — neyin değiştiğini ÖLÇEMEDİĞİMİZ hâlde artımsal üretmek,
    ///     geri yüklenemeyen bir halka üretmektir.
    /// </para>
    /// </remarks>
    public static bool TamYedekGerekli(
        BackupRecordDto? sonHalka,
        DateTime? zincirinTamYedekZamani,
        int tamYedekPeriyoduGun,
        bool temelIndeksOkunabildi,
        DateTime simdiUtc)
    {
        if (sonHalka is null || string.IsNullOrWhiteSpace(sonHalka.ChainId) || zincirinTamYedekZamani is null)
        {
            return true;
        }

        if (!temelIndeksOkunabildi)
        {
            return true;
        }

        var periyot = Math.Max(1, tamYedekPeriyoduGun);
        return simdiUtc - zincirinTamYedekZamani.Value >= TimeSpan.FromDays(periyot);
    }
}
