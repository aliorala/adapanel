namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// Yedeğe GİREMEYEN kaynak dosyaların kaydı.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN VAR (ölçülmüş hata):</b> arşive dosya ekleyen üç yardımcı da (<c>AddFilesToArchive</c>,
/// <c>AddDirectoryToArchive</c>, <c>AddSharedFileToArchive</c>) okunamayan dosyayı çıplak
/// <c>catch (IOException) { }</c> ile atlıyordu. Üçü de <c>static</c> olduğu için ne günlükçüye ne
/// sayaca ne de kaydın durumuna erişebiliyordu: yedek "created" damgasıyla kapanıyor, atlanan
/// dosyanın adı hiçbir yere düşmüyordu. Bu, PANELİN KENDİ YEDEĞİNDE de geçerliydi — çalışan bir
/// süreç <c>adapanel.db</c>'yi kilitlediğinde panel kendi veritabanı OLMAYAN bir arşivi "başarılı"
/// diye kaydediyordu. Bunu ancak geri yüklerken, yani felaket anında öğrenirsin.
/// </para>
/// <para>
/// <b>Neden bir toplayıcı nesne:</b> yardımcıların <c>static</c> kalması bilinçli (saf, test
/// edilebilir, servis durumuna bağlı değil). Durumu servise taşımak yerine, sonucu TAŞIYAN bir
/// nesne geçiriliyor; böylece "atladım ama kimseye söylemedim" durumu derleyici düzeyinde
/// imkânsızlaşıyor — yardımcıyı çağıran, toplayıcıyı vermek zorunda.
/// </para>
/// <para>
/// <b>Neden ayrıntı sayısı sınırlı:</b> bozuk bir ACL yüz binlerce dosyayı birden atlatabilir.
/// Hepsini kayda yazmak <c>config/backups.json</c>'ı şişirip panelin yedek listesini
/// kullanılamaz hâle getirirdi. Bu yüzden ilk <see cref="MaxDetailed"/> dosya ADIYLA, kalanı
/// SAYIYLA taşınır: "kaç tane" bilgisi hiçbir koşulda kaybolmaz.
/// </para>
/// </remarks>
internal sealed class BackupSkipLog
{
    /// <summary>Adıyla taşınacak en fazla dosya sayısı (kalanı yalnızca sayılır).</summary>
    public const int MaxDetailed = 20;

    private readonly List<SkippedEntry> _detayli = [];
    private readonly List<TruncatedScan> _kesilenTaramalar = [];
    private readonly List<string> _eksikKokler = [];

    /// <summary>Atlanan dosya/klasör kaydı.</summary>
    /// <param name="EntryName">Arşivde olması gereken girdi adı (ör. <c>config/automation.json</c>).</param>
    /// <param name="Reason">Teknik sebep (istisna metni). Çevrilmez; teşhis içindir.</param>
    internal readonly record struct SkippedEntry(string EntryName, string Reason);

    /// <summary>Bütçesi dolduğu için YARIDA KESİLEN bir ağaç taraması.</summary>
    /// <param name="Root">Kesilen taramanın kökü (arşiv girdi adıyla, ör. <c>sites/a.test/files</c>).</param>
    /// <param name="ReasonKey">Bütçenin hangi ucunun dolduğu (<c>SafeDirectoryWalk</c> sebep anahtarı).</param>
    internal readonly record struct TruncatedScan(string Root, string ReasonKey);

    /// <summary>Atlanan TOPLAM sayı (ayrıntısı taşınmayanlar dahil).</summary>
    public int Count { get; private set; }

    /// <summary>Adıyla taşınan ilk kayıtlar.</summary>
    public IReadOnlyList<SkippedEntry> Detailed => _detayli;

    /// <summary>Adı taşınmayan (yalnızca sayılan) kayıt sayısı.</summary>
    public int Truncated => Count - _detayli.Count;

    /// <summary>Bütçe dolduğu için yarıda kesilmiş taramalar.</summary>
    /// <remarks>
    /// <b>Neden atlanan dosya listesinden AYRI:</b> ikisi farklı büyüklükte kayıptır ve
    /// karıştırılırsa büyüğü küçüğünün içinde kaybolur. "Bir dosya kilitliydi" adı bilinen, tek
    /// ögelik bir eksiktir; "tarama bütçesi doldu" ise <b>kaç dosyanın eksik olduğu bilinmeyen</b>
    /// bir eksiktir — o ağacın gezilmemiş kısmında ne olduğu ölçülmedi. Aynı kaba atılsaydı
    /// kullanıcı, arşivin bir köşesinin hiç bakılmamış olduğunu tek bir dosya adı sanırdı.
    /// </remarks>
    public IReadOnlyList<TruncatedScan> TruncatedScans => _kesilenTaramalar;

    /// <summary>Yedeklenmesi İSTENEN ama diskte HİÇ BULUNAMAYAN kökler (arşiv girdi adıyla).</summary>
    /// <remarks>
    /// <para>
    /// <b>ÖLÇÜLEN HATA:</b> <c>ScanFiles</c>, kök yoksa sessizce BOŞ liste dönüyordu. Sitesinin
    /// fiziksel yolu silinmiş/yeniden adlandırılmış bir kullanıcı, içinde tek bir dosyası olmayan
    /// bir arşivi "başarılı" olarak alıyordu — hem de her gece, aylarca. Yokluk bir SONUÇ değil,
    /// raporlanması gereken bir BULGUDUR.
    /// </para>
    /// <para>
    /// <b>Neden atlanan dosya listesinden AYRI:</b> kesilen taramayla aynı gerekçe. "Bir dosya
    /// kilitliydi" tek ögelik bir eksiktir; "kök hiç yok" ise o ağacın TAMAMININ yedeğe hiç
    /// girmediği anlamına gelir. Aynı kaba atılsaydı, felaket ölçüsündeki bir eksik tek bir dosya
    /// adı gibi görünürdü.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> MissingRoots => _eksikKokler;

    /// <summary>
    /// Kayıtta bildirilmesi gereken bir eksik var mı?
    /// </summary>
    /// <remarks>
    /// Kesilen tarama da BURAYA dahildir: dahil edilmeseydi bütçe dolduğunda yedek "created"
    /// damgasıyla kapanır ve eksikliği hiçbir yere düşmezdi — bu tipin var oluş sebebinin tam
    /// tersi.
    /// </remarks>
    public bool Any => Count > 0 || _kesilenTaramalar.Count > 0 || _eksikKokler.Count > 0;

    public void Add(string entryName, Exception exception)
    {
        Count++;
        if (_detayli.Count < MaxDetailed)
        {
            _detayli.Add(new SkippedEntry(entryName, exception.Message));
        }
    }

    /// <summary>Bütçesi dolduğu için tamamlanamamış bir taramayı kaydeder.</summary>
    public void AddTruncatedScan(string root, string reasonKey)
        => _kesilenTaramalar.Add(new TruncatedScan(root, reasonKey));

    /// <summary>Yedeklenmesi istenen bir kökün diskte hiç olmadığını kaydeder.</summary>
    public void AddMissingRoot(string root) => _eksikKokler.Add(root);

    /// <summary>Günlüğe tek satırda yazılabilir özet.</summary>
    /// <remarks>
    /// <b>Her parça KOŞULLU ve bu ölçülmüş bir hatanın düzeltmesi:</b> dosya cümlesi eskiden
    /// koşulsuz yazılıyordu. <see cref="Any"/> kesilen taramayı da kapsamaya başlayınca hiç dosya
    /// atlanmadan yalnız tarama kesilen durum mümkün oldu ve özet
    /// <c>"0 dosya atlandi: ; TARAMA KESILDI: ..."</c> hâline geldi — sayı sıfır, liste boş, iki
    /// noktalı virgül. Yalnız sunucu günlüğüne gidiyor, ama günlük OKUNMAK içindir: yangın anında
    /// okuyanın gördüğü ilk şeyin "0 dosya atlandi" olması, asıl bilgiyi (bir ağacın hiç
    /// taranmadığını) gürültünün arkasına saklar.
    /// </remarks>
    public string Describe()
    {
        var parcalar = new List<string>(2);

        if (Count > 0)
        {
            parcalar.Add(Truncated > 0
                ? $"{Count} dosya atlandi (ilk {_detayli.Count}: {string.Join(", ", _detayli.Select(kayit => kayit.EntryName))} ... +{Truncated})"
                : $"{Count} dosya atlandi: {string.Join(", ", _detayli.Select(kayit => kayit.EntryName))}");
        }

        // Kesilen tarama, atlanan dosyalardan SONRA ve AYRI yazılır: özet tek satır olduğu için
        // sunucu günlüğüne bakan kişinin göreceği tek şey budur ve "kaç dosya eksik olduğu
        // bilinmiyor" bilgisinin oradan da düşmemesi gerekir.
        if (_kesilenTaramalar.Count > 0)
        {
            parcalar.Add($"TARAMA KESILDI: {string.Join(", ", _kesilenTaramalar.Select(kayit => $"{kayit.Root} ({kayit.ReasonKey})"))}");
        }

        // EKSİK KÖK EN BAŞA DEĞİL EN SONA yazılır ama ASLA atlanmaz: günlüğe bakan kişi için
        // "kök hiç yok" bu satırların en ağırıdır (o ağacın tamamı yedeğe girmedi).
        if (_eksikKokler.Count > 0)
        {
            parcalar.Add($"KOK YOK: {string.Join(", ", _eksikKokler)}");
        }

        // BOŞ DİZE DÖNMEZ. Çağıran bunu `Any` ile korumalı olsa da (BackupService.ApplySkippedFiles),
        // koruma bir gün kalkarsa günlüğe konusuz bir "Yedek eksik alindi (...): " satırı düşerdi:
        // uyarı seviyesinde, sebepsiz. Sıfır durumu ADIYLA söylenir.
        return parcalar.Count == 0 ? "eksik yok" : string.Join("; ", parcalar);
    }
}
