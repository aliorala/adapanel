namespace AdaPanel.Core.Dtos.Files;

/// <summary>
/// Bir klasörün ÖZYİNELEMELİ boyutu — ve o boyutun ne kadar GÜVENİLİR olduğu.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden var:</b> dosya listesi (<see cref="FileEntryDto.Size"/>) klasörler için 0 gösterir ve bu
/// doğrudur — bir klasörün "boyutu" ancak yürünerek bulunur. Ama sonuç olarak kullanıcı, diski
/// hangi klasörün yediğini panelde HİÇ göremiyordu; disk dolduğunda tek çaresi RDP ile sunucuya
/// girip Explorer'da özelliklere bakmaktı. Bu uç o soruyu panelin içinde cevaplar.
/// </para>
/// <para>
/// <b>Neden <see cref="Truncated"/> alanı sözleşmenin PARÇASI:</b> hesap bütçelidir (öge sayısı ve
/// süre) — çünkü bir kiracının <c>node_modules</c> klasöründe milyonlarca öge olabilir ve sınırsız
/// özyineleme panel sürecini TÜM kiracılar için kilitler. Bütçe dolduğunda o ana kadarki toplam
/// döner. O toplamı TAM sonuç gibi göstermek, kullanıcının "bu klasör 2 GB" deyip aslında 40 GB
/// olan bir klasörü silmemesi (ya da yanlış klasörü silmesi) demektir: yani sayı yalnız eksik değil,
/// YANILTICI olurdu. Bu yüzden kısmi sonuç kısmi olduğunu SÖYLER —
/// <see cref="TotalBytes"/> o durumda bir ALT SINIRDIR.
/// </para>
/// </remarks>
public sealed class DirectorySizeDto
{
    /// <summary>Boyutu hesaplanan klasörün SANAL yolu (ör. <c>sites/a.test/wp-content</c>).</summary>
    /// <remarks>
    /// İstekteki yol normalize edilmiş hâliyle geri döner: arayüz, gecikmeli gelen bir cevabı
    /// kullanıcının o sırada gezindiği başka bir klasöre yazmasın diye eşleştirebilsin.
    /// </remarks>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Alt ağaçtaki dosyaların bayt toplamı. <see cref="Truncated"/> <c>true</c> ise bu bir ALT
    /// SINIRDIR, gerçek toplam değildir.
    /// </summary>
    /// <remarks>
    /// Klasör kayıtlarının kendi disk maliyeti (dizin girdileri, NTFS meta verisi) SAYILMAZ:
    /// ölçülebilir değil ve Explorer da saymaz — kullanıcının başka bir araçla karşılaştırdığında
    /// tutmayan bir sayı görmesi, sayıya olan güveni tümden bitirirdi.
    /// </remarks>
    public long TotalBytes { get; set; }

    /// <summary>Sayılan dosya adedi (bağlantılar hariç).</summary>
    public int FileCount { get; set; }

    /// <summary>Sayılan ALT klasör adedi — kökün kendisi dahil DEĞİL (bağlantılar hariç).</summary>
    public int DirectoryCount { get; set; }

    /// <summary>
    /// Hesap EKSİK mi? <c>true</c> ise sayılar bir alt sınırdır ve sebebi
    /// <see cref="TruncatedReasonKey"/> söyler.
    /// </summary>
    public bool Truncated { get; set; }

    /// <summary>
    /// Eksikliğin sebebinin çeviri anahtarı; hesap tamsa boş.
    /// <list type="bullet">
    /// <item><c>files.size_truncated_entries</c> — öge sayısı bütçesi doldu.</item>
    /// <item><c>files.size_truncated_time</c> — süre bütçesi doldu.</item>
    /// <item><c>files.size_truncated_denied</c> — en az bir alt klasör okunamadı (erişim reddedildi).</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Serbest metin değil ANAHTAR döner: sebep kullanıcının dilinde gösterilebilsin (depodaki
    /// bütün hata/uyarı yollarında olduğu gibi).
    /// </remarks>
    public string TruncatedReasonKey { get; set; } = string.Empty;
}
