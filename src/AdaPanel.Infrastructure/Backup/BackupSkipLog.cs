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

    /// <summary>Atlanan dosya/klasör kaydı.</summary>
    /// <param name="EntryName">Arşivde olması gereken girdi adı (ör. <c>config/automation.json</c>).</param>
    /// <param name="Reason">Teknik sebep (istisna metni). Çevrilmez; teşhis içindir.</param>
    internal readonly record struct SkippedEntry(string EntryName, string Reason);

    /// <summary>Atlanan TOPLAM sayı (ayrıntısı taşınmayanlar dahil).</summary>
    public int Count { get; private set; }

    /// <summary>Adıyla taşınan ilk kayıtlar.</summary>
    public IReadOnlyList<SkippedEntry> Detailed => _detayli;

    /// <summary>Adı taşınmayan (yalnızca sayılan) kayıt sayısı.</summary>
    public int Truncated => Count - _detayli.Count;

    public bool Any => Count > 0;

    public void Add(string entryName, Exception exception)
    {
        Count++;
        if (_detayli.Count < MaxDetailed)
        {
            _detayli.Add(new SkippedEntry(entryName, exception.Message));
        }
    }

    /// <summary>Günlüğe tek satırda yazılabilir özet.</summary>
    public string Describe() =>
        Truncated > 0
            ? $"{Count} dosya atlandi (ilk {_detayli.Count}: {string.Join(", ", _detayli.Select(kayit => kayit.EntryName))} ... +{Truncated})"
            : $"{Count} dosya atlandi: {string.Join(", ", _detayli.Select(kayit => kayit.EntryName))}";
}
