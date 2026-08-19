using System.Text.RegularExpressions;

namespace AdaPanel.Infrastructure.SiteBuilder;

/// <summary>
/// Yüklenen görselin gerçekten görsel olduğunu doğrular.
/// </summary>
/// <remarks>
/// <para>
/// <b>Uzantıya ve bildirilen türe GÜVENİLMEZ.</b> İkisi de istemcinin yazdığı metindir;
/// <c>kedi.jpg</c> adlı bir dosya pekâlâ bir PHP betiği olabilir. Yükleme hedefi müşterinin
/// IIS sitesinin altına kopyalanan bir klasör olduğu için, çalıştırılabilir bir dosyanın oraya
/// girmesi doğrudan uzaktan kod çalıştırma demektir. Bu yüzden karar dosyanın İÇERİĞİNE
/// (imza baytlarına) bakılarak verilir ve saklanan uzantı, bildirilen değil ÇÖZÜLEN türden
/// türetilir — böylece "kedi.php.jpg" gibi çift uzantı oyunları tümüyle anlamsızlaşır.
/// </para>
/// <para>
/// <b>SVG bilerek YOK.</b> SVG bir görsel biçimi olduğu kadar bir belge biçimidir: içine
/// <c>&lt;script&gt;</c> ve olay öznitelikleri konabilir ve tarayıcı bunları çalıştırır.
/// "Görsel yükleme" kapısından script geçirmenin en bilinen yolu budur. Bir tanıtım sayfasının
/// SVG'ye ihtiyacı yok; kabul etmenin bedeli, sağladığı faydadan büyük.
/// </para>
/// </remarks>
public static class SiteBuilderImageValidator
{
    /// <summary>Kabul edilen biçimler (bilgi amaçlı; asıl karar imza baytlarıyla verilir).</summary>
    public static readonly IReadOnlyList<string> AllowedExtensions = [".jpg", ".png", ".gif", ".webp"];

    // Panelin ÜRETTİĞİ ad kalıbı: 32 onaltılık karakter + çözülen uzantı. Kullanıcının verdiği ad
    // hiç kullanılmaz, dolayısıyla yol kaçışı ("../"), boşluk, Unicode hilesi diye bir konu yoktur.
    private static readonly Regex StoredNamePattern = new(
        @"^[a-f0-9]{32}\.(jpg|png|gif|webp)$",
        RegexOptions.Compiled);

    public static bool IsStoredNameValid(string? name) =>
        !string.IsNullOrWhiteSpace(name) && StoredNamePattern.IsMatch(name.Trim());

    /// <summary>
    /// İmza baytlarından biçimi çözer. Tanınmayan her şey <c>null</c> döner — "belki görseldir"
    /// diye geçirmek, doğrulamayı hiç yapmamakla aynı kapıya çıkardı.
    /// </summary>
    public static string? ResolveExtension(ReadOnlySpan<byte> header)
    {
        // JPEG: FF D8 FF
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return ".jpg";
        }

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (header.Length >= 8
            && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
            && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            return ".png";
        }

        // GIF: "GIF87a" / "GIF89a"
        if (header.Length >= 6
            && header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F'
            && header[3] == (byte)'8' && (header[4] == (byte)'7' || header[4] == (byte)'9') && header[5] == (byte)'a')
        {
            return ".gif";
        }

        // WEBP: "RIFF" ....(4 bayt uzunluk).... "WEBP"
        if (header.Length >= 12
            && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
            && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
        {
            return ".webp";
        }

        return null;
    }

    /// <summary>
    /// Doğrulama sonucu. Reddedilen dosya için sebep anahtarı taşınır ki kullanıcı "neden olmadı"
    /// sorusunu ekranda görebilsin.
    /// </summary>
    public readonly record struct Result(bool Accepted, string Extension, string ErrorKey)
    {
        public static Result Reject(string errorKey) => new(false, string.Empty, errorKey);

        public static Result Accept(string extension) => new(true, extension, string.Empty);
    }

    /// <summary>
    /// Boyut + içerik doğrulaması. <paramref name="header"/> dosyanın ilk baytlarıdır.
    /// </summary>
    public static Result Validate(long sizeBytes, ReadOnlySpan<byte> header)
    {
        if (sizeBytes <= 0)
        {
            return Result.Reject("sitebuilder.image_empty");
        }

        if (sizeBytes > SiteBuilderCatalog.MaxImageBytes)
        {
            return Result.Reject("sitebuilder.image_too_large");
        }

        var extension = ResolveExtension(header);
        return extension is null
            ? Result.Reject("sitebuilder.image_type_rejected")
            : Result.Accept(extension);
    }

    /// <summary>Doğrulanmış bir görsel için saklanacak ad üretir.</summary>
    public static string BuildStoredName(string extension) => Guid.NewGuid().ToString("N") + extension;
}
