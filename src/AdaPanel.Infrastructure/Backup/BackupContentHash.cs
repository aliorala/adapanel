using System.Security.Cryptography;

namespace AdaPanel.Infrastructure.Backup;

/// <summary>
/// Yedek arşivinin KURCALANMADIĞINI ölçen damga: oluşturma anında hesaplanır, okuma anında
/// doğrulanır.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden gerekliydi — ve neden yalnız ŞİFRESİZ arşivler için:</b> şifreli arşivde bütünlük
/// zaten var; AES-GCM her bloğun etiketini düz metni yazmadan önce doğrular
/// (<see cref="BackupArchiveCipher"/>), yani kurcalanmış bir şifreli arşiv açılmaz. Şifresiz
/// arşivde böyle bir şey YOKTU: <see cref="BackupArchiveProbe"/> arşivin YAPISINI ölçer ("boş mu,
/// açılıyor mu"), ama kurcalanmış bir zip de yapısal olarak kusursuzdur. Yani panel, içeriği
/// değiştirilmiş bir arşivi "sound" damgasıyla listeler ve geri yüklemede saldırganın dosyalarını
/// siteye açardı.
/// </para>
/// <para>
/// <b>Tehdit gerçek çünkü arşiv HER ZAMAN bizde durmuyor:</b> panel yedeği FTP/S3/WebDAV gibi
/// ÜÇÜNCÜ TARAF bir hedefe de yükler. O hedefe erişebilen biri (sağlayıcı çalışanı, sızmış FTP
/// parolası, yanlış yapılandırılmış bir kova) arşivi değiştirebilir. Geri yükleme yolundaki dosya
/// adı/hedef denetimleri bunu görmez — onlar dosyanın NEREYE açıldığını denetler, NE OLDUĞUNU
/// değil.
/// </para>
/// <para>
/// <b>Damga arşivin YANINDA DEĞİL, panelin KENDİ KAYDINDA durur.</b> Bu, korumanın tamamıdır:
/// arşivin yanına yazılan bir <c>.sha256</c> dosyasını, arşivi değiştirebilen aynı kişi
/// değiştirirdi. Uzak hedefi ele geçiren, panelin yerel kaydına erişemez.
/// </para>
/// <para>
/// <b>ÜÇ DEĞERLİ, ikiye indirilemez</b> (<see cref="BackupIntegrityCheck"/>): damgası olmayan
/// ESKİ yedekler "kurcalanmış" DEĞİL, "ölçülemez"dir. Onları reddetmek, bu sürümden önce alınmış
/// bütün yedekleri felaket anında kullanılamaz yapardı — kapatılan riskten kat kat kötüsü.
/// </para>
/// </remarks>
internal static class BackupContentHash
{
    /// <summary>Damganın hesaplandığı algoritma; kayıtta önek olarak durur ki ileride değişebilsin.</summary>
    private const string Prefix = "sha256:";

    /// <summary>
    /// Dosyanın SON hâlinin (sıkıştırma + şifreleme uygulanmış) damgasını hesaplar.
    /// </summary>
    /// <remarks>
    /// <b>Neden diskteki son baytlar:</b> damganın işi "bu dosya bizim yazdığımız dosya mı"
    /// sorusunu cevaplamak. Sıkıştırma/şifreleme öncesi ölçmek, tam da kurcalanmaya açık olan
    /// katmanı ölçüm dışında bırakırdı.
    /// <para>
    /// Hata YUTULUR ve boş dize döner: damga hesaplanamadı diye YEDEK ALINMAMIŞ SAYILMAZ. Sağlam
    /// bir yedeği, yanındaki ölçüm başarısız oldu diye reddetmek bu sınıfın amacının tersi olurdu.
    /// </para>
    /// </remarks>
    public static async Task<string> ComputeAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var akis = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 128,
                useAsync: true);

            var ozet = await SHA256.HashDataAsync(akis, cancellationToken);
            return Prefix + Convert.ToHexString(ozet).ToLowerInvariant();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Kayıttaki damgayı diskteki dosyayla karşılaştırır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sıra önemlidir ve her dal ayrı bir gerçektir:</b>
    /// </para>
    /// <list type="number">
    ///   <item><description>Kayıtta damga YOK → <see cref="BackupIntegrityCheck.NotStamped"/>.
    ///   Bu sürümden önce alınmış yedek; "kurcalanmış" DEĞİL.</description></item>
    ///   <item><description>Yol bir DİZİN → <see cref="BackupIntegrityCheck.NotStamped"/>. Geri
    ///   yüklenmiş kayıt çıkarılan klasörü gösterir (aynı durum
    ///   <see cref="BackupArchiveProbe"/>'ta da yazılı); ölçülecek arşiv yoktur.</description></item>
    ///   <item><description>Dosya okunamadı → <see cref="BackupIntegrityCheck.Unmeasurable"/>.
    ///   Kilitli bir dosyaya "kurcalanmış" demek, çalışan yedeği çöp ilan etmektir.</description></item>
    ///   <item><description>Damgalar EŞLEŞMİYOR → <see cref="BackupIntegrityCheck.Mismatch"/>.
    ///   Ölçülen tek olumsuz karar budur.</description></item>
    /// </list>
    /// </remarks>
    public static async Task<BackupIntegrityCheck> VerifyAsync(
        string? expected,
        string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return BackupIntegrityCheck.NotStamped;
        }

        if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path))
        {
            return BackupIntegrityCheck.NotStamped;
        }

        var olculen = await ComputeAsync(path, cancellationToken);
        if (olculen.Length == 0)
        {
            // ComputeAsync yalnız ÖLÇEMEDİĞİNDE boş döner (kilit/erişim). "Eşleşmedi" demek yanlış
            // olurdu: dosya sağlam olabilir, biz okuyamadık.
            return BackupIntegrityCheck.Unmeasurable;
        }

        return string.Equals(olculen, expected.Trim(), StringComparison.OrdinalIgnoreCase)
            ? BackupIntegrityCheck.Match
            : BackupIntegrityCheck.Mismatch;
    }
}

/// <summary>Damga doğrulamasının sonucu. <b>Dört değerli; "eşleşmedi" tek olumsuz karardır.</b></summary>
internal enum BackupIntegrityCheck
{
    /// <summary>Kayıtta damga yok (eski yedek) ya da hedef bir dizin. Kurcalanma İDDİASI DEĞİL.</summary>
    NotStamped,

    /// <summary>Damga var ama dosya okunamadı. Kurcalanma İDDİASI DEĞİL.</summary>
    Unmeasurable,

    /// <summary>Ölçüldü ve eşleşti.</summary>
    Match,

    /// <summary>Ölçüldü ve EŞLEŞMEDİ: diskteki dosya, panelin yazdığı dosya değil.</summary>
    Mismatch
}
