namespace AdaPanel.Infrastructure.RunningJobs;

/// <summary>
/// SÜREN bir uzun işin sunucudaki kaydı (<c>config/running-jobs.json</c> içindeki bir yuva).
/// <para>
/// Süreç sahipliği <c>Antivirus/AntivirusRunningScanState.cs</c> ile aynı PID/başlangıç anı
/// çiftini kullanır. JobId, geciken temizliğin aynı zaman damgalı başka bir işe dokunmasını önler.
/// </para>
/// </summary>
public sealed class RunningJobState
{
    /// <summary>Bu işin tekil kimliği; aynı saat damgasına sahip sonraki işlerle karışmaz.</summary>
    public string JobId { get; init; } = string.Empty;

    /// <summary>İş türü: <c>backup</c> | <c>restore</c> | <c>migration</c> | <c>software</c>.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>İşin konusu (site adı, yedek adı, bileşen kimliği).</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>İşin başladığı an (UTC).</summary>
    public DateTime StartedAtUtc { get; init; }

    /// <summary>
    /// İşin ÜZERİNDE yapıldığı kaynağın sahibi ("N" biçiminde abonelik kimliği); sunucu geneli
    /// işlerde boş. Kapsamlı kullanıcıya (müşteri/bayi) hangi işlerin gösterileceği buna bakar —
    /// bu alan olmasa bir müşteri, başka bir müşterinin sitesinin yedeklendiğini görebilirdi.
    /// </summary>
    public string SubjectUserId { get; init; } = string.Empty;

    /// <summary>
    /// İşi yürüten PANEL sürecinin kimliği.
    /// <para>
    /// SAHİPLİK KAYDI, ilerleme kaydı değil: diskteki "sürüyor" işareti tek başına YALAN
    /// SÖYLEYEBİLİR — panel çökerse ya da servis yeniden başlarsa işaret dosyada kalır ve panel
    /// sonsuza kadar "yedek sürüyor" der (üstelik yeni yedek de alınamaz, 409 döner). Sahibi bu
    /// süreçse iş gerçekten bu süreçte koşuyordur (bitişte kapsam işareti temizler); sahibi BAŞKA
    /// bir süreçse iş yarıda kalmıştır. Böylece durum tahmin edilmez, ÖLÇÜLÜR.
    /// </para>
    /// </summary>
    public int OwnerProcessId { get; init; }

    /// <summary>
    /// Sahip sürecin başlangıç anı (tick). PID'ler Windows'ta YENİDEN KULLANILIR; yalnız PID'e
    /// bakmak, aynı numarayı alan yepyeni bir sürecin ölü bir işi "sürüyor" göstermesine yol
    /// açardı. Başlangıç anıyla birlikte çift, süreci tekilleştirir. Ölçülemezse 0 kalır ve
    /// karşılaştırma yalnız PID üzerinden yapılır.
    /// </summary>
    public long OwnerProcessStartedAtTicks { get; init; }
}

/// <summary>
/// YARIDA KALMIŞ işin kaydı: panel süreci iş sürerken yeniden başladı.
/// <para>
/// <b>NEDEN SAKLANIYOR:</b> bayat kaydı sessizce silmek, kullanıcıya iki saat beklediği yedeğin
/// neden ortada olmadığını hiç söylememek olurdu. Kayıt, aynı türden yeni bir iş başlayana kadar
/// durur — o an bilgi zaten geçersizleşir.
/// </para>
/// </summary>
public sealed class InterruptedJobState
{
    public string Kind { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public DateTime StartedAtUtc { get; init; }

    /// <summary>Kesilmenin FARK EDİLDİĞİ an (panel yeniden başladıktan sonraki ilk okuma).</summary>
    public DateTime DetectedAtUtc { get; init; }

    public string SubjectUserId { get; init; } = string.Empty;
}

/// <summary>
/// Çalışan-iş kayıt dosyasının tamamı: iş TÜRÜ başına en fazla bir "sürüyor" ve bir "kesildi".
/// <para>
/// <b>NEDEN TEK DOSYA (antivirüsteki gibi tür başına ayrı dosya değil):</b> dört tür var ve panel
/// açılışında hepsi birden okunuyor. Ayrı dosyalar dört okuma + dört ayrı yarış penceresi
/// demekti; tek dosyada <see cref="Support.JsonFileStore.UpdateAsync"/> dosya başına kilit
/// verdiği için "canlı iş var mı" kontrolü ile yazma AYNI kilidin altında, yani ATOMİK olur.
/// Eşzamanlı iki istek arasında ikisinin de "yer boş" görmesi mümkün değildir.
/// </para>
/// </summary>
public sealed class RunningJobsFile
{
    public Dictionary<string, RunningJobState> Running { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, InterruptedJobState> Interrupted { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
