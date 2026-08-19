using AdaPanel.Core.Common;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Infrastructure.Branding;

/// <summary>Doğrulanmış görsel biçimi: panelin KENDİ belirlediği içerik tipi ve uzantı.</summary>
public sealed record BrandingImageFormat(string MediaType, string FileExtension);

/// <summary>
/// Yüklenen marka görselinin İÇERİK doğrulaması.
/// </summary>
/// <remarks>
/// <para>
/// <b>Uzantıya ve istemcinin bildirdiği içerik tipine BAKILMAZ.</b> İkisi de yükleyen tarafın
/// yazdığı metinlerdir; "logo.png" adında bir HTML dosyası göndermek hiçbir engel gerektirmez.
/// Karar yalnızca dosyanın İLK BAYTLARINDAN verilir ve dönen içerik tipi de bizim ürettiğimizdir —
/// yani panel, kullanıcıdan gelen bir tipi hiçbir yerde tekrarlamaz.
/// </para>
/// <para>
/// <b>SVG KABUL EDİLMEZ.</b> SVG bir belge biçimidir: içinde <c>&lt;script&gt;</c>,
/// <c>&lt;foreignObject&gt;</c>, olay öznitelikleri ve dış kaynak referansları bulunabilir. Güvenli
/// hâle getirmek "temizleyici yaz + her SVG sürümünde doğru kalmasını umut et" demektir; bu, bir
/// LOGO özelliğinin taşıyabileceğinden büyük bir risktir. Plesk de logo için GIF/JPEG/PNG ister.
/// Reddetme AYRI bir mesaj anahtarıyla yapılır ki kullanıcı "biçim yanlış" değil, "SVG kabul
/// edilmiyor, PNG verin" cevabını alsın.
/// </para>
/// <para>
/// <b>Boyut sınırı neden dar:</b> görsel, JSON içinde base64 olarak taşınıyor (bkz. BrandingDto) ve
/// base64 %33 şişirir. Ayrıca bu yükleme giriş ekranında HER açılışta indirilir. Plesk'in önerisi
/// 100 KB civarıdır; buradaki sınırlar onun birkaç katı, yani meşru logoları kesmez.
/// </para>
/// </remarks>
public static class BrandingImageRules
{
    /// <summary>Logo için üst sınır (bayt). Base64 sonrası ~350 KB eder.</summary>
    public const int MaxLogoBytes = 256 * 1024;

    /// <summary>Favicon için üst sınır (bayt). Sekme ikonu küçüktür; büyük dosya hatanın işaretidir.</summary>
    public const int MaxFaviconBytes = 64 * 1024;

    public static int MaxBytes(BrandingImageKind kind) =>
        kind == BrandingImageKind.Favicon ? MaxFaviconBytes : MaxLogoBytes;

    /// <summary>
    /// Görseli doğrular; kabul edilirse panelin belirlediği biçimi döner, edilmezse
    /// <see cref="AppException"/> fırlatır.
    /// </summary>
    public static BrandingImageFormat Validate(BrandingImageKind kind, ReadOnlySpan<byte> content)
    {
        if (content.Length == 0)
        {
            throw new AppException("branding.image_empty", 400, 400);
        }

        if (content.Length > MaxBytes(kind))
        {
            throw new AppException("branding.image_too_large", 413, 413);
        }

        // SVG önce sorulur: bayt imzası olmayan bir METİN biçimi olduğu için aşağıdaki imza
        // listesine hiç uymaz ve ayırt edilmezse "bilinmeyen tip" diye reddedilirdi. Kullanıcının
        // ne yapması gerektiğini söyleyen mesajı verebilmek için ayrı tutuluyor.
        if (LooksLikeSvg(content))
        {
            throw new AppException("branding.svg_not_allowed", 415, 415);
        }

        var format = Detect(kind, content);
        if (format is null)
        {
            throw new AppException("branding.image_type_not_allowed", 415, 415);
        }

        return format;
    }

    private static BrandingImageFormat? Detect(BrandingImageKind kind, ReadOnlySpan<byte> content)
    {
        if (IsPng(content))
        {
            return new BrandingImageFormat("image/png", ".png");
        }

        // Favicon: PNG ve ICO. Sekme ikonu için tarayıcı desteği evrensel olan ikili budur;
        // JPEG/GIF favicon pratikte yanlışlıkla yüklenmiş bir logodur.
        if (kind == BrandingImageKind.Favicon)
        {
            return IsIco(content) ? new BrandingImageFormat("image/x-icon", ".ico") : null;
        }

        if (IsJpeg(content))
        {
            return new BrandingImageFormat("image/jpeg", ".jpg");
        }

        if (IsGif(content))
        {
            return new BrandingImageFormat("image/gif", ".gif");
        }

        if (IsWebp(content))
        {
            return new BrandingImageFormat("image/webp", ".webp");
        }

        return null;
    }

    private static bool IsPng(ReadOnlySpan<byte> c) =>
        c.Length >= 8 && c[0] == 0x89 && c[1] == 0x50 && c[2] == 0x4E && c[3] == 0x47 &&
        c[4] == 0x0D && c[5] == 0x0A && c[6] == 0x1A && c[7] == 0x0A;

    private static bool IsJpeg(ReadOnlySpan<byte> c) =>
        c.Length >= 3 && c[0] == 0xFF && c[1] == 0xD8 && c[2] == 0xFF;

    private static bool IsGif(ReadOnlySpan<byte> c) =>
        c.Length >= 6 && c[0] == (byte)'G' && c[1] == (byte)'I' && c[2] == (byte)'F' &&
        c[3] == (byte)'8' && (c[4] == (byte)'7' || c[4] == (byte)'9') && c[5] == (byte)'a';

    private static bool IsWebp(ReadOnlySpan<byte> c) =>
        c.Length >= 12 &&
        c[0] == (byte)'R' && c[1] == (byte)'I' && c[2] == (byte)'F' && c[3] == (byte)'F' &&
        c[8] == (byte)'W' && c[9] == (byte)'E' && c[10] == (byte)'B' && c[11] == (byte)'P';

    /// <summary>
    /// ICO imzası YAPISAL olarak doğrulanır. Ham imza yalnız 4 bayttır (00 00 01 00) ve ikisi
    /// sıfırdır — rastgele bir ikili dosya buna kolayca uyar. Bu yüzden başlıktaki görsel sayısı
    /// ve dizinin dosyaya sığması da kontrol edilir; uydurma bir dosya bu üçünü birden tutturamaz.
    /// </summary>
    private static bool IsIco(ReadOnlySpan<byte> c)
    {
        if (c.Length < 22 || c[0] != 0x00 || c[1] != 0x00 || c[2] != 0x01 || c[3] != 0x00)
        {
            return false;
        }

        var imageCount = c[4] | (c[5] << 8);
        if (imageCount < 1)
        {
            return false;
        }

        // 6 baytlık başlık + her görsel için 16 baytlık dizin girdisi dosyaya SIĞMALI.
        return 6 + (imageCount * 16) <= c.Length;
    }

    /// <summary>
    /// SVG tanıma: ilk baytlarda (BOM ve boşluklar atlanarak) XML bildirimi ya da <c>&lt;svg</c>
    /// etiketi. Dosyanın tamamı taranmaz; buradaki amaç doğru mesajı verebilmek, çünkü kabul kararı
    /// zaten BEYAZ LİSTEDEN geçiyor — tanınmayan her şey nasılsa reddediliyor.
    /// </summary>
    private static bool LooksLikeSvg(ReadOnlySpan<byte> content)
    {
        var head = content[..Math.Min(content.Length, 512)];

        // UTF-8 BOM.
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
        {
            head = head[3..];
        }

        var text = System.Text.Encoding.UTF8.GetString(head).TrimStart();
        return text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("<!doctype svg", StringComparison.OrdinalIgnoreCase);
    }
}
