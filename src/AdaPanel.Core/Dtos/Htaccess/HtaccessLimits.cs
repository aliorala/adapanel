namespace AdaPanel.Core.Dtos.Htaccess;

/// <summary>
/// Motorun DEĞERLENDİRME BÜTÇESİ. Apache'nin <c>LimitInternalRecursion</c> karşılığı ve fazlası.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN VAR:</b> <c>.htaccess</c> dosyasını MÜŞTERİ yazar ve bu kod barındırılan her sitenin
/// HER isteğinde çalışır. Sınırsız bırakılan tek bir kaynak (regex süresi, kural sayısı, iç
/// yönlendirme sayısı, dosya boyutu) tek bir müşterinin sunucudaki BÜTÜN siteleri düşürmesine
/// yeter. Bu yüzden bütçe isteğe bağlı bir "ayar" değil, motorun sözleşmesinin parçasıdır:
/// varsayılanlar zaten güvenlidir, barındırma katmanı yalnızca DARALTMAK için dokunmalıdır.
/// </para>
/// <para>
/// Bütçe aşıldığında motor İSTİSNA FIRLATMAZ; değerlendirmeyi durdurur, sonucu
/// <see cref="HtaccessEvaluationResult.EngineFailed"/> ile işaretler ve sebebi bulgu olarak yazar.
/// </para>
/// </remarks>
public sealed record HtaccessLimits
{
    /// <summary>Bütün alanları güvenli varsayılanlarla dolu bütçe.</summary>
    public static HtaccessLimits Default { get; } = new();

    /// <summary>
    /// Tek bir <c>.htaccess</c> dosyasının en büyük boyutu. 256 KB, gerçek dünyadaki en şişkin
    /// dosyaların (WordPress + güvenlik eklentisi + önbellek eklentisi) onlarca katıdır.
    /// </summary>
    public int MaxFileBytes { get; init; } = 256 * 1024;

    /// <summary>Tek satırın en büyük uzunluğu (devam satırları birleştirildikten SONRA).</summary>
    public int MaxLineLength { get; init; } = 8 * 1024;

    /// <summary>Bir dosyadaki en fazla direktif sayısı.</summary>
    public int MaxDirectivesPerFile { get; init; } = 2000;

    /// <summary>
    /// Kök dizinden istenen dizine kadar okunacak en fazla <c>.htaccess</c> sayısı. Derin bir
    /// yol ağacı, tek istekte yüzlerce dosya okuması anlamına gelirdi.
    /// </summary>
    public int MaxCascadeDepth { get; init; } = 32;

    /// <summary>
    /// Tek istekte değerlendirilecek en fazla kural sayısı (bütün iç döngüler dahil).
    /// </summary>
    public int MaxRuleEvaluations { get; init; } = 1000;

    /// <summary>
    /// Kural kümesinin baştan kaç kez çalıştırılabileceği. Apache'nin <c>LimitInternalRecursion</c>
    /// varsayılanı da 10'dur; <c>RewriteRule</c> zincirleri kolayca sonsuz döngü kurar.
    /// </summary>
    public int MaxInternalRedirects { get; init; } = 10;

    /// <summary>
    /// Tek bir <c>Regex.Match</c> çağrısının üst sınırı. ReDoS'a karşı ZORUNLU savunma —
    /// bu değer olmadan <c>(a+)+$</c> gibi tek bir desen bir CPU çekirdeğini süresiz kilitler.
    /// </summary>
    public TimeSpan RegexTimeout { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Tek isteğin toplam değerlendirme süresi. Tek tek hızlı olan yüzlerce eşleşme toplamda
    /// yavaş olabilir; zaman aşımı bu yüzden hem tek desende hem toplamda ölçülür.
    /// </summary>
    public TimeSpan MaxTotalEvaluationTime { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Derlenmiş desen önbelleğinin üst sınırı. Sınır olmasaydı, her istekte farklı desen üreten
    /// bir dosya belleği süresiz şişirirdi.
    /// </summary>
    public int MaxCachedPatterns { get; init; } = 2000;

    /// <summary><c>AuthUserFile</c> (htpasswd) dosyasının en büyük boyutu.</summary>
    public int MaxAuthUserFileBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// bcrypt maliyet (cost) üst sınırı. <c>$2y$31$</c> tek doğrulamada dakikalarca CPU yer;
    /// kendi parola dosyasını yükleyebilen bir müşteri bunu hizmet dışı bırakma silahı yapar.
    /// </summary>
    public int MaxBcryptCost { get; init; } = 15;
}
