namespace AdaPanel.Core.Dtos.Sites;

/// <summary>
/// "web.config gercekten DISKE indi ve GECERLI mi?" sorusunun UC DEGERLI cevabi.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN VAR.</b> <c>SiteHostingSettingsService.UpdateWebConfig</c> metodunun SON ifadesi
/// <c>document.Save(webConfigPath)</c> idi: geri okuma yok, yeniden ayristirma yok, IIS tarafi
/// dogrulama yok. Dosyanin kendi yorumlari (ipSecurity/httpRedirect bolumleri) bozuk bir
/// web.config'in sitenin TAMAMINA <c>500.19</c> verdigini anlatiyordu -- ama korumalarin hepsi
/// ONLEYICIYDI, hicbiri DENETLEYICI degildi. <c>Validate</c> ISTEGI denetliyor, URETILEN belgeyi
/// degil. Panel "kaydedildi" diyordu; denetim kaydindaki sayilar servisin BELLEKTE kurdugu
/// DTO'dan geliyordu.
/// </para>
/// <para>
/// <b>UC DEGER.</b> <see cref="Verified"/> "yazildi ve geri okundu ve AYNI belge". <see cref="Failed"/>
/// "yazildi ama geri okunan belge bizim yazdigimiz DEGIL" (bu durumda ONCEKI icerik geri konur).
/// Ikisi de degilse cevap OLCULEMEDI'dir -- ve olculemeyen bir yazma "kaydedildi" diye
/// gosterilMEZ.
/// </para>
/// </remarks>
public sealed class WebConfigWriteMeasurement
{
    /// <summary>
    /// Yazma DENENDI ve DUSTU (ya da hic denenemedi cunku mevcut belge okunamadi).
    /// Bu durumda dosya DEGISMEMISTIR.
    /// </summary>
    public bool WriteFailed { get; init; }

    /// <summary>Dosya diskten TEKRAR okunup XML olarak ayristirilabildi mi?</summary>
    public bool Reparsed { get; init; }

    /// <summary>Diskten okunan belge, yazmak istedigimiz belgeyle AYNI mi?</summary>
    public bool Matches { get; init; }

    /// <summary>Bozuk yazim tespit edilip ONCEKI icerik geri kondu mu?</summary>
    public bool RolledBack { get; init; }

    /// <summary>IIS tarafi ayristirma denendi mi? (IIS yoksa false kalir.)</summary>
    public bool IisProbed { get; init; }

    /// <summary>IIS web.config'i okuyabildi mi? <see cref="IisProbed"/> false iken anlamsizdir.</summary>
    public bool IisAccepted { get; init; }

    /// <summary>Insan tarafindan okunacak kanit (hata metni, yol, fark).</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Panel "kaydedildi" diyebilir mi? YALNIZ geri okundu VE ayni belge ise.</summary>
    public bool Verified => Reparsed && Matches;

    /// <summary>Olculdu ve YAZIM BOZUK/DUSMUS.</summary>
    public bool Failed => !Verified && (WriteFailed || RolledBack || (Reparsed && !Matches));

    /// <summary>
    /// IIS'in de belgeyi kabul ettigi OLCULDU mu? IIS yoksa false -- ama bu "reddetti" DEMEK
    /// DEGILDIR; <see cref="IisProbed"/> ile birlikte okunur.
    /// </summary>
    public bool IisVerified => IisProbed && IisAccepted;

    /// <summary>Gosterilecek uyari anahtari; her sey olculdu ve yolundaysa bos doner.</summary>
    public string NoticeKey => Verified
        ? IisProbed && !IisAccepted
            ? "sites.hosting_settings_rejected_by_iis"
            : IisProbed
                ? string.Empty
                : "sites.hosting_settings_iis_unverified"
        : Failed
            ? "sites.hosting_settings_write_failed"
            : "sites.hosting_settings_unverified";

    public static WebConfigWriteMeasurement Olculemedi(string detail = "")
        => new() { Detail = detail };
}
