namespace AdaPanel.Core.Dtos.Sites;

/// <summary>
/// "Kanonik yonlendirme kurali GERCEKTEN web.config'e yazildi mi?" sorusunun UC DEGERLI cevabi.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN VAR.</b> <c>SiteAliasService.YonlendirmeKuraliniYaz</c> tumuyle LOG-ONLY bir
/// <c>catch</c> icindeydi ve <c>belge.Save</c> son ifadeydi. Kullanici "birincile yonlendir"
/// kutusunu isaretliyor, panel takma adi kaydediyor ve YONLENDIRME ACIK gorunuyordu -- oysa
/// kural hic yazilmamis olabilirdi. Ziyaretci ikinci alan adinda kaliyor, arama motoru iki
/// kopyayi da zayiflatiyordu.
/// </para>
/// <para>
/// <b>NEDEN FIRLATMIYORUZ.</b> Kural yazilamasa da takma ad CALISIR (IIS baglamasi duruyor);
/// yalnizca kanonik yonlendirme olmaz. Bu yuzden dogru cevap islemi basarisiz saymak degil,
/// SONUCU DOGRU ANLATMAKTIR.
/// </para>
/// </remarks>
public sealed class AliasRedirectRuleMeasurement
{
    /// <summary>Geri okuma yapilabildi mi?</summary>
    public bool Measured { get; init; }

    /// <summary>Bu yapilandirmada kuralin BULUNMASI bekleniyor muydu?</summary>
    public bool RuleExpected { get; init; }

    /// <summary>Diskten okunan web.config'de kural BULUNDU mu?</summary>
    public bool RulePresent { get; init; }

    /// <summary>Kuralin kapsadigi host sayisi (www ciftleri dahil).</summary>
    public int RedirectedHostCount { get; init; }

    public string Detail { get; init; } = string.Empty;

    /// <summary>Olculdu ve beklenen durum TUTUYOR.</summary>
    public bool Applied => Measured && RulePresent == RuleExpected;

    /// <summary>Olculdu ve TUTMUYOR: yonlendirme acik gorunup calismayacak.</summary>
    public bool NotApplied => Measured && RulePresent != RuleExpected;

    /// <summary>Sonuc okunamadi. "Yazilmadi" DEGIL, BILINMIYOR.</summary>
    public bool Unverified => !Measured;

    /// <summary>Gosterilecek anahtar; her sey olculdu ve yolundaysa bos doner.</summary>
    public string NoticeKey => Applied
        ? string.Empty
        : Unverified
            ? "alias.redirect_rule_unverified"
            : "alias.redirect_rule_not_applied";

    public static AliasRedirectRuleMeasurement Olculemedi(string detail = "")
        => new() { Detail = detail };
}
