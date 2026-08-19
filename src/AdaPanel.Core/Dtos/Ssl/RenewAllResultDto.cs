namespace AdaPanel.Core.Dtos.Ssl;

public sealed class RenewAllResultDto
{
    /// <summary>
    /// Yenilenen sertifika sayisi. <see cref="Measured"/> false ise ANLAMSIZDIR.
    /// </summary>
    public int RenewedCount { get; set; }

    /// <summary>
    /// Yenileme GERCEKTEN calistirilabildi mi.
    /// <para>
    /// false = win-acme bulunamadi ya da hic calistirilamadi. Bu durumda "tamamlandi" DENMEZ.
    /// Eski davranis bunun tersiydi: win-acme kurulu degilken de sonuc basariyla donuyor,
    /// otomasyon durumu "completed" yaziyordu. Ekranda her gun "SSL yenileme: tamamlandi"
    /// gorunuyor, gercekte HICBIR sertifika yenilenmiyordu -- ve bu ancak 90 gun sonra tum
    /// siteler ayni anda dustugunde fark edilirdi.
    /// </para>
    /// </summary>
    public bool Measured { get; set; } = true;

    /// <summary>Olculemedi ya da basarisiz olduysa SEBEBI; kullaniciya gosterilir.</summary>
    public string FailureReason { get; set; } = string.Empty;

    public string Output { get; set; } = string.Empty;

    /// <summary>
    /// Yenileme turundan sonra panel kaydı (<c>config/ssl.json</c>) ile sertifika deposu
    /// karşılaştırılıp GERÇEĞE göre tazelenen kayıt sayısı.
    /// </summary>
    /// <remarks>
    /// Yenileme ile doğrulama AYRI şeylerdir ve ayrı sayılır: win-acme "yenilenecek bir şey yok"
    /// dediğinde <see cref="RenewedCount"/> 0'dır, ama kayıtların gerçekle uyuştuğu yine de
    /// ölçülmüş olur. Eskiden bu tur hiç yapılmadığı için yenilenmiş bir sertifika kayıtta eski
    /// bitiş tarihiyle kalıyor ve panel her gün "süresi doldu" uyarısı üretiyordu.
    /// </remarks>
    public int VerifiedCount { get; set; }

    /// <summary>
    /// Kaydı olan ama sertifika deposunda KARŞILIĞI BULUNMAYAN sertifika sayısı (ölçülmüş).
    /// Sıfırdan büyükse panel o kayıtlar için "geçerli" demeyi bırakmalıdır.
    /// </summary>
    public int MissingFromStoreCount { get; set; }

    /// <summary>
    /// ÖLÇÜLEMEYEN kayıt sayısı (depo açılamadı, host adı yok). Bunlar "geçersiz" DEĞİLDİR;
    /// yalnızca bu turda doğrulanamamıştır ve kayıtlarına dokunulmamıştır.
    /// </summary>
    public int UnverifiedCount { get; set; }
}
