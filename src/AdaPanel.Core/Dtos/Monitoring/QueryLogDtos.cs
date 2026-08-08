namespace AdaPanel.Core.Dtos.Monitoring;

/// <summary>
/// BİTMİŞ tek bir sorgu kaydı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bu bir örnekleme kaydıdır, tam denetim günlüğü DEĞİLDİR.</b> Panel motorun oturum listesini
/// belirli aralıklarla okur ve bir sorgunun listeden düştüğünü görünce onu "bitti" diye yazar.
/// Dolayısıyla iki örnekleme arasında başlayıp biten çok kısa sorgular kaydedilmeyebilir. Ekranda
/// da böyle söylenir — "her sorgu burada" demek yanlış olurdu ve kullanıcı eksik veriye dayanıp
/// yanlış sonuca varırdı.
/// </para>
/// <para>
/// Bu yaklaşımın alternatifi motorun kendi genel sorgu günlüğünü açmaktır; o da her sorguyu diske
/// yazdığı için üretim sunucusunu ciddi biçimde yavaşlatır ve diski doldurur. Panel, ölçtüğü
/// süreyi doğru raporlayıp ne ölçmediğini de söylemeyi tercih ediyor.
/// </para>
/// </remarks>
public sealed class QueryLogEntryDto
{
    /// <summary>Sorgunun ilk görüldüğü an (örnekleme anı, sorgunun gerçek başlangıcı değil).</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>Sorgunun listeden düştüğü an.</summary>
    public DateTime EndedAtUtc { get; set; }

    /// <summary>Motorun bildirdiği çalışma süresi (son ölçüm). Örnekleme aralığından bağımsızdır.</summary>
    public int DurationSeconds { get; set; }

    /// <summary>"mysql" | "mariadb" | "postgresql".</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Çözülebildiyse sorguyu çalıştıran site; yoksa boş.</summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>Veritabanı adı.</summary>
    public string Target { get; set; } = string.Empty;

    public string User { get; set; } = string.Empty;

    public string ClientHost { get; set; } = string.Empty;

    /// <summary>Kısaltılmış sorgu metni. Parola içerenler sunucuda maskelenir.</summary>
    public string Statement { get; set; } = string.Empty;

    /// <summary>Metindeki değişmezler gizlilik için maskelendi mi.</summary>
    public bool Redacted { get; set; }

    /// <summary>Bağlantı bu sunucunun dışından mı geliyordu.</summary>
    public bool RemoteClient { get; set; }

    /// <summary>Veritabanı panelde bir müşteriye kayıtlıysa o müşterinin adı.</summary>
    public string OwnerName { get; set; } = string.Empty;
}

/// <summary>Sorgu geçmişi filtreleri. Verilmeyen her alan "kısıtlama yok" demektir.</summary>
public sealed class QueryLogFilterDto
{
    /// <summary>Başlangıç günü (dahil, sunucu yerel tarihi olarak yorumlanır).</summary>
    public DateTime? FromUtc { get; set; }

    /// <summary>Bitiş günü (dahil).</summary>
    public DateTime? ToUtc { get; set; }

    /// <summary>"mysql" | "mariadb" | "postgresql" — boş ise hepsi.</summary>
    public string Source { get; set; } = string.Empty;

    public string SiteName { get; set; } = string.Empty;

    /// <summary>Veritabanı adı.</summary>
    public string Target { get; set; } = string.Empty;

    public string User { get; set; } = string.Empty;

    /// <summary>Bu süreden KISA sorgular elenir (saniye). "Yavaş sorguları göster" filtresi.</summary>
    public int MinDurationSeconds { get; set; }

    /// <summary>Sorgu metninde geçen ifade (büyük/küçük harf duyarsız).</summary>
    public string Search { get; set; } = string.Empty;

    /// <summary>Yalnız dışarıdan gelen bağlantılar.</summary>
    public bool RemoteOnly { get; set; }

    public int Skip { get; set; }

    public int Take { get; set; } = 100;
}

/// <summary>
/// Aynı ŞEKİLDEKİ sorguların özeti — "hangi sorgu çok çalışmış" sorusunun cevabı.
/// <para>
/// Gruplama, sorgunun değişmezleri (sayılar, dizgeler) kaldırılmış hâline göre yapılır; aksi hâlde
/// <c>WHERE id=1</c> ve <c>WHERE id=2</c> ayrı sorgu sayılır ve hiçbir şey öne çıkmazdı.
/// </para>
/// </summary>
public sealed class QueryLogSummaryRowDto
{
    /// <summary>Şekil (değişmezleri temizlenmiş sorgu).</summary>
    public string Shape { get; set; } = string.Empty;

    /// <summary>Bu şekilden kaç kayıt görüldü.</summary>
    public int Count { get; set; }

    /// <summary>Toplam çalışma süresi (saniye) — sunucuyu en çok meşgul edeni bulmak için.</summary>
    public long TotalSeconds { get; set; }

    public int MaxSeconds { get; set; }

    /// <summary>Bu şekli en çok çalıştıran site (varsa).</summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>Örnek bir tam metin (kısaltılmış).</summary>
    public string Sample { get; set; } = string.Empty;
}

/// <summary>Filtrelenmiş sorgu geçmişi sayfası + o filtreye ait özet.</summary>
public sealed class QueryLogPageDto
{
    public IReadOnlyCollection<QueryLogEntryDto> Entries { get; set; } = [];

    /// <summary>Filtreye uyan TOPLAM kayıt (sayfalama için).</summary>
    public int TotalCount { get; set; }

    /// <summary>En çok çalışan/en çok süre harcayan sorgu şekilleri (toplam süreye göre sıralı).</summary>
    public IReadOnlyCollection<QueryLogSummaryRowDto> TopShapes { get; set; } = [];

    /// <summary>Kaydın bulunduğu günler (tarih filtresi için; en yeniden eskiye).</summary>
    public IReadOnlyCollection<string> AvailableDays { get; set; } = [];

    /// <summary>Kayıt kaç gün saklanıyor (ekranda söylenir).</summary>
    public int RetentionDays { get; set; }

    /// <summary>Örnekleme aralığı (saniye) — "bundan kısa sorgular kaçmış olabilir" demek için.</summary>
    public int SampleIntervalSeconds { get; set; }
}
