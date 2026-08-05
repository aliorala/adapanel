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
}
