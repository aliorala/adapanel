namespace AdaPanel.Core.Dtos.Backup;

/// <summary>
/// "Yedekleri doğrula" sonucu: kayıtların GERÇEKTEN var olup olmadığının ölçümü.
/// </summary>
/// <remarks>
/// <para>
/// Bu uç, panelin en tehlikeli yalanını kapatır: listede duran ama artık olmayan yedek.
/// Kullanıcı bunu normalde ancak geri yüklemeye çalışırken — yani felaket anında — öğrenirdi.
/// </para>
/// <para>
/// <b>ÜÇ SONUÇ vardır, ikisi değil.</b> "Ulaşamadım" ile "yok" ayrı sayılır
/// (<see cref="Unreachable"/>). Geçici bir ağ arızasını "yedek silinmiş" saymak, kullanıcının
/// bütün yedek geçmişini kayıp göstermek olurdu; bu yüzden ulaşılamayan hedefte kayıt
/// İŞARETLENMEZ.
/// </para>
/// </remarks>
public sealed class BackupVerificationResultDto
{
    /// <summary>Denetlenen kayıt sayısı.</summary>
    public int Checked { get; set; }

    /// <summary>Yerel dosyası bulunamayan kayıt sayısı.</summary>
    public int MissingLocal { get; set; }

    /// <summary>Yerel yolu artık olmayan, önceki silme işleminden kalmış kayıtlar.</summary>
    public int PreviouslyDeletedLocal { get; set; }

    /// <summary>Uzak kopyası bulunamayan kayıt sayısı.</summary>
    public int MissingRemote { get; set; }

    /// <summary>
    /// Uzak hedefe ULAŞILAMADIĞI için karar verilemeyen kayıt sayısı. Bunlar kayıp SAYILMAZ.
    /// </summary>
    public int Unreachable { get; set; }

    /// <summary>
    /// Dosyası DURAN ama arşivi açılamayan / boş çıkan kayıt sayısı.
    /// </summary>
    /// <remarks>
    /// <b>Neden "eksik" sayacından ayrı:</b> "dosya yok" ile "dosya var ama içi yok" farklı iki
    /// arızadır ve çözümleri de farklıdır (birinde dosya aranır, diğerinde yedek YENİDEN alınır).
    /// Aynı sayaca toplansaydı, doğrulamanın bu turda kazandığı tek yeni gerçek görünmez olurdu.
    /// "Ölçemedim" (şifreli sarmalayıcı, kilitli dosya) buraya GİRMEZ.
    /// </remarks>
    public int Unsound { get; set; }

    /// <summary>Sorunlu kayıtların ayrıntısı.</summary>
    public List<BackupVerificationIssueDto> Issues { get; set; } = [];

    /// <summary>
    /// Hedefte duran ama panelde KAYDI OLMAYAN dosyalar (ters yetim). Yalnız listelenir;
    /// OTOMATİK SİLİNMEZ — hedefte panele ait olmayan dosyalar da olabilir.
    /// </summary>
    public List<BackupOrphanDto> Orphans { get; set; } = [];

    /// <summary>
    /// Yetim taraması YAPILAMAYAN hedeflerin gerekçesi. Boş bir yetim listesi "yetim yok"
    /// anlamına gelmesin diye vardır — taranmadığını söylemek, taradığını sanmaktan iyidir.
    /// </summary>
    public List<string> OrphanScanSkipped { get; set; } = [];
}

/// <summary>Doğrulamada sorunlu bulunan tek bir kayıt.</summary>
public sealed class BackupVerificationIssueDto
{
    public string BackupId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string TargetType { get; set; } = string.Empty;

    /// <summary>
    /// i18n anahtarı: <c>backup.verify.missing_local</c>, <c>backup.verify.missing_remote</c>,
    /// <c>backup.verify.unsound</c> ya da <c>backup.verify.unreachable</c>.
    /// </summary>
    public string MessageKey { get; set; } = string.Empty;

    /// <summary>Ham sebep (sunucu yanıtı); kimlik bilgisi TAŞIMAZ.</summary>
    public string Detail { get; set; } = string.Empty;
}

/// <summary>Hedefte duran, panelde kaydı olmayan dosya.</summary>
public sealed class BackupOrphanDto
{
    /// <summary>Hedefteki yol (kimlik/sorgu içermez).</summary>
    public string RemotePath { get; set; } = string.Empty;

    public string TargetType { get; set; } = string.Empty;
}
