namespace AdaPanel.Core.Dtos.Files;

/// <summary>
/// Bir ya da BİRDEN ÇOK ögeyi başka bir klasöre KOPYALAMA isteği.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden taşımanın yanına ayrı bir uç:</b> kopyalama, taşımanın "kaynağı silmeyen" hâli değildir;
/// KURALLARI aynı ama İŞİ farklıdır. Klasör kopyalamak özyinelemeli bir yürüyüştür (taşımada tek bir
/// <c>Directory.Move</c>), ve o yürüyüşün her adımında bağlantı (junction/symlink) kararı yeniden
/// verilmek zorundadır. Bu yüzden istek/sonuç tipleri de ayrıdır: aynı DTO'ya "Copy mi Move mu"
/// bayrağı koymak, iki farklı davranışı tek isimle anlatıp arayüzde de kayıtta da belirsizlik üretirdi.
/// </para>
/// <para>
/// <b>Güvenlik kuralları taşımayla BİREBİR AYNIDIR</b> ve aynı yerden gelir (bkz.
/// <c>FileService.BuildTransferPlanAsync</c>): sanal alan, bağlantı takip etmeme, hedef klasörün var
/// olması, ad alanının yol olmaması. İki akıştan birinde unutulan bir denetim delik demektir.
/// </para>
/// </remarks>
public sealed class CopyFilesRequest
{
    /// <summary>Kopyalanacak ögelerin sanal yolları (ör. <c>sites/a.test/eski/rapor.pdf</c>).</summary>
    public string[] Paths { get; set; } = [];

    /// <summary>Hedef KLASÖRÜN sanal yolu. Var olan bir klasör olmalıdır.</summary>
    public string DestinationPath { get; set; } = string.Empty;

    /// <summary>
    /// Kopyanın yeni adı (yalnız TEK öge kopyalanırken). Yol ayırıcısı içeremez: ad alanı bir yol
    /// alanına dönüşürse hedef klasör denetimi anlamsızlaşır.
    /// <para>
    /// Aynı klasöre <c>NewName</c> ile kopyalamak, dosya yöneticisindeki "çoğalt" işlemidir.
    /// </para>
    /// </summary>
    public string? NewName { get; set; }

    /// <summary>
    /// Hedefte AYNI ADLI bir DOSYA varsa üzerine yazılsın mı? Varsayılan <c>false</c> — sessiz
    /// üzerine yazma, dosya yöneticisinde veri kaybının en kolay yoludur. Klasörler bu bayrakla
    /// bile ezilmez (bkz. IFileService.CopyAsync).
    /// </summary>
    public bool Overwrite { get; set; }
}
