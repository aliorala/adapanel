namespace AdaPanel.Core.Dtos.Mail;

/// <summary>
/// Giden posta kuyruğunun panele taşınan görünümü.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden gerekli:</b> posta "gitmediğinde" panelin söyleyecek hiçbir şeyi yoktu. Kuyruk,
/// bir barındırma operatörünün her gün baktığı ekrandır: hangi ileti takıldı, kime gidemedi,
/// bir sonraki deneme ne zaman. Bu görünüm olmadan tek teşhis yolu sunucuya girip günlük
/// okumaktır — yani panel kullanıcısı için teşhis diye bir şey yoktur.
/// </para>
/// <para>
/// <b>Installed/Configured ayrımı:</b> "kuyruk boş" ile "posta sunucusuna hiç bağlanılamadı"
/// aynı şey değildir. İkisi tek bir boş listeye indirgenirse yönetici, sunucu çökmüşken
/// "her şey yolunda, kuyruk boş" görür.
/// </para>
/// </remarks>
public sealed class MailQueueDto
{
    /// <summary>Posta sunucusu (Stalwart) bu makinede kurulu mu?</summary>
    public bool Installed { get; set; }

    /// <summary>Yönetim arayüzüne bağlanılıp kuyruk gerçekten okunabildi mi?</summary>
    public bool Configured { get; set; }

    /// <summary>Sunucudaki toplam kuyruk uzunluğu (listede kırpma olsa bile gerçek sayı).</summary>
    public int TotalCount { get; set; }

    /// <summary>Listeye alınan iletiler (en fazla istenen sayıda).</summary>
    public IReadOnlyCollection<MailQueueMessageDto> Messages { get; set; } = [];
}

/// <summary>Kuyruktaki tek bir ileti.</summary>
public sealed class MailQueueMessageDto
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Zarf göndericisi (MAIL FROM). Boş dize = geri dönüş iletisi (bounce).</summary>
    public string ReturnPath { get; set; } = string.Empty;

    /// <summary>İletinin sunucuya alındığı an (UTC).</summary>
    public DateTimeOffset? CreatedAtUtc { get; set; }

    /// <summary>Bir sonraki teslim denemesi (UTC). Boşsa sunucu yeni bir deneme planlamamıştır.</summary>
    public DateTimeOffset? NextRetryUtc { get; set; }

    /// <summary>İleti boyutu (byte).</summary>
    public long SizeBytes { get; set; }

    /// <summary>İletiyi gönderen istemcinin IP'si — kötüye kullanımda tek somut iz budur.</summary>
    public string ReceivedFromIp { get; set; } = string.Empty;

    /// <summary>Zarf alıcıları ve alıcı başına teslim durumu.</summary>
    public IReadOnlyCollection<MailQueueRecipientDto> Recipients { get; set; } = [];
}

/// <summary>Kuyruktaki iletinin TEK alıcısı için teslim durumu.</summary>
/// <remarks>
/// Bir ileti bir alıcıya teslim edilip diğerine takılabilir; durumu ileti düzeyinde tek bir
/// alana indirmek, "kime gidemedi" sorusunu cevapsız bırakırdı.
/// </remarks>
public sealed class MailQueueRecipientDto
{
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Teslim durumunun KISA adı (sunucunun <c>status</c> varyant adı, ör. bir <c>@type</c> değeri).
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Sunucunun durum nesnesinin HAM metni.
    /// </summary>
    /// <remarks>
    /// Bilerek ham bırakıldı: durum nesnesinin içindeki hata alanlarının adları bu panelde
    /// doğrulanamadı. Uydurma bir alan adından okumak, hata mesajını sessizce BOŞ gösterirdi —
    /// yani "sebebi oku" özelliği hiç çalışmadan var görünürdü. Ham metin en azından her zaman
    /// doğrudur ve şema doğrulandığında burada ayrıştırılabilir.
    /// </remarks>
    public string StatusDetail { get; set; } = string.Empty;

    /// <summary>Yapılmış teslim denemesi sayısı.</summary>
    public int RetryCount { get; set; }

    /// <summary>Bu alıcı için bir sonraki deneme (UTC).</summary>
    public DateTimeOffset? RetryDueUtc { get; set; }

    /// <summary>Alıcının bulunduğu kuyruk adı (sunucu belirler).</summary>
    public string QueueName { get; set; } = string.Empty;
}
