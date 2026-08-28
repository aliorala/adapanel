namespace AdaPanel.Core.Dtos.Billing;

/// <summary>
/// Faturalama sistemine (WHMCS) verilen TOPLU kullanım raporu.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden toplu, neden hesap başına değil:</b> WHMCS'in <c>UsageUpdate</c> işlevi SUNUCU başına
/// çalışır, ürün/hizmet başına DEĞİL (resmî belge: developers.whmcs.com/provisioning-modules/usage-update).
/// Modüle hizmet listesi verilmez; yalnız sunucu kimlik bilgileri gelir. Yani modül "bu sunucudaki
/// bütün hesapların kullanımı" sorusunu TEK çağrıda sormak zorundadır. Hesap başına uç, 500 müşterili
/// bir sunucuda cron turunu 500 isteğe çevirirdi.
/// </para>
/// </remarks>
public sealed class BillingUsageReportDto
{
    /// <summary>Raporun üretildiği an — WHMCS tarafında "lastupdate" bunun karşılığıdır.</summary>
    public DateTime GeneratedAtUtc { get; set; }

    /// <summary>Rapordaki hesaplar (panelde TOP-LEVEL abonelikler; alt kullanıcılar hariç).</summary>
    public IReadOnlyList<BillingUsageAccountDto> Accounts { get; set; } = [];

    /// <summary>
    /// Trafiği ÖLÇÜLEMEYEN hesap sayısı. Arayüz bunu uyarı olarak gösterir: sıfır sanılan bir
    /// trafik, faturalanmayan bir trafiktir.
    /// </summary>
    public int UnmeasuredTrafficCount { get; set; }
}

/// <summary>
/// Tek bir aboneliğin WHMCS'in ANLADIĞI birimlerdeki kullanımı.
/// </summary>
/// <remarks>
/// Bütün büyüklükler <b>MB</b>'dir. Sebep: WHMCS <c>tblhosting</c> tablosunda
/// <c>diskusage/disklimit/bwusage/bwlimit</c> sütunlarını megabayt olarak tutar; çeviriyi panelde
/// yapmak, PHP tarafında birim hatası yapma ihtimalini ortadan kaldırır (orada bir kez yanlış
/// bölünen sayı, müşteriye yanlış fatura demektir).
/// </remarks>
public sealed class BillingUsageAccountDto
{
    public Guid UserId { get; set; }

    /// <summary>
    /// Panel kullanıcı adı. WHMCS eşleştirmesinin ANAHTARI budur (<c>tblhosting.username</c>);
    /// modülün geri kalanı da hesabı bu adla bulur.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Aboneliğe atanmış alan adları — yalnız tanılama/görüntüleme için.</summary>
    public IReadOnlyList<string> Domains { get; set; } = [];

    public string PlanId { get; set; } = string.Empty;

    public string PlanName { get; set; } = string.Empty;

    /// <summary>Kullanılan disk (MB).</summary>
    /// <remarks>
    /// <see cref="DiskMeasured"/> <c>false</c> ise bu bir ALT SINIRDIR ve modül bu satırın diskini
    /// WHMCS'e YAZMAZ.
    /// </remarks>
    public long DiskUsageMb { get; set; }

    /// <summary>
    /// Disk kullanımı TAM ölçülebildi mi.
    /// </summary>
    /// <remarks>
    /// <b>Neden bant genişliğiyle aynı muamele:</b> disk ölçümü bütçeli bir ağaç yürüyüşüdür ve
    /// bütçe dolduğunda ya da bir site okunamadığında sonuç gerçek kullanımın ALT SINIRIDIR. Eksik
    /// bir sayıyı WHMCS'e yazmak, aşım faturalamasını SESSİZCE düşürür (müşteri kullandığından az
    /// öder) ve bir önceki turun doğru değerinin de üstüne yazardı. Ölçemediğimizde eski değeri
    /// korumak, yanlış bir değeri yazmaktan iyidir — bant genişliğinde verilen kararın aynısı
    /// (bkz. <see cref="BandwidthMeasured"/>).
    /// </remarks>
    public bool DiskMeasured { get; set; }

    /// <summary>Disk kotası (MB). <c>0</c> = sınırsız.</summary>
    public long DiskLimitMb { get; set; }

    /// <summary>Bu takvim ayında kullanılan trafik (MB).</summary>
    public long BandwidthUsageMb { get; set; }

    /// <summary>Aylık trafik kotası (MB). <c>0</c> = sınırsız.</summary>
    public long BandwidthLimitMb { get; set; }

    /// <summary>
    /// Trafik gerçekten ÖLÇÜLEBİLDİ mi.
    /// </summary>
    /// <remarks>
    /// <c>false</c> ise <see cref="BandwidthUsageMb"/> gerçek kullanımı göstermez ve modül bu satırın
    /// bant genişliğini WHMCS'e YAZMAZ (eski değeri korur). Ölçemediğimiz bir ayı "0 MB" diye yazmak,
    /// aşım faturalamasını sessizce sıfırlamak olurdu — panelin abonelik motoru da aynı sebeple
    /// ölçülemeyen trafikte kota UYGULAMAZ (bkz. SubscriptionUsageDto.TrafficMeasured).
    /// </remarks>
    public bool BandwidthMeasured { get; set; }

    public bool IsSuspended { get; set; }
}
