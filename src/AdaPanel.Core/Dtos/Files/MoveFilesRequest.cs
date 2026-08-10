namespace AdaPanel.Core.Dtos.Files;

/// <summary>
/// Bir ya da BİRDEN ÇOK ögeyi başka bir klasöre taşıma / yeniden adlandırma isteği.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden tek istekte çoklu öge:</b> dosya yöneticisinde seçim doğal olarak çoğuldur. Her öge
/// için ayrı istek atmak, yarısı başarılı yarısı başarısız bir dizi çağrı üretir ve kullanıcının
/// ne olduğunu anlamasını imkânsız kılar. Tek istek + öge başına sonuç, "8 ögeden 6'sı taşındı"
/// cevabını verebilmenin tek dürüst yoludur.
/// </para>
/// <para>
/// <b>Yeniden adlandırma ayrı bir uç DEĞİL:</b> yeniden adlandırma, hedefi kaynağın kendi klasörü
/// olan bir taşımadır. Ayrı uç olsaydı sanal alan/bağlantı/üzerine-yazma korumaları iki yerde
/// yazılır, biri güncellenip diğeri unutulurdu.
/// </para>
/// </remarks>
public sealed class MoveFilesRequest
{
    /// <summary>Taşınacak ögelerin sanal yolları (ör. <c>sites/a.test/eski/rapor.pdf</c>).</summary>
    public string[] Paths { get; set; } = [];

    /// <summary>Hedef KLASÖRÜN sanal yolu. Var olan bir klasör olmalıdır.</summary>
    public string DestinationPath { get; set; } = string.Empty;

    /// <summary>
    /// Yeni ad (yalnız TEK öge taşınırken). Yol ayırıcısı içeremez: ad alanı bir yol alanına
    /// dönüşürse hedef klasör denetimi anlamsızlaşır.
    /// </summary>
    public string? NewName { get; set; }

    /// <summary>
    /// Hedefte AYNI ADLI bir DOSYA varsa üzerine yazılsın mı? Varsayılan <c>false</c> — sessiz
    /// üzerine yazma, dosya yöneticisinde veri kaybının en kolay yoludur. Klasörler bu bayrakla
    /// bile ezilmez (bkz. IFileService.MoveAsync).
    /// </summary>
    public bool Overwrite { get; set; }
}
