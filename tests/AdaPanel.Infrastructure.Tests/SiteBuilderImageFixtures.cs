namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Testler için GERÇEK imza baytları taşıyan küçük dosyalar.
/// </summary>
/// <remarks>
/// Doğrulama dosyanın İÇERİĞİNE baktığı için "png" adlı boş bir dosyayla test yazmak, korumayı
/// hiç sınamamak olurdu. Buradaki baytlar biçimlerin gerçek başlıklarıdır.
/// </remarks>
internal static class SiteBuilderImageFixtures
{
    public static byte[] MinimalPng() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01
    ];

    public static byte[] MinimalJpeg() =>
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46,
        0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01
    ];

    public static byte[] MinimalGif() =>
        "GIF89a"u8.ToArray().Concat(new byte[] { 0x01, 0x00, 0x01, 0x00, 0x00 }).ToArray();

    public static byte[] MinimalWebp()
    {
        var bytes = new List<byte>();
        bytes.AddRange("RIFF"u8.ToArray());
        bytes.AddRange(new byte[] { 0x1A, 0x00, 0x00, 0x00 });
        bytes.AddRange("WEBP"u8.ToArray());
        bytes.AddRange("VP8 "u8.ToArray());
        return bytes.ToArray();
    }

    /// <summary>Windows çalıştırılabilir dosyası (MZ başlığı).</summary>
    public static byte[] WindowsExecutable() =>
        "MZ"u8.ToArray().Concat(new byte[] { 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00 }).ToArray();

    public static byte[] PhpScript() => "<?php system($_GET['c']); ?>"u8.ToArray();

    /// <summary>Script taşıyan SVG — "görsel" kapısından XSS geçirmenin en bilinen yolu.</summary>
    public static byte[] ScriptedSvg() =>
        "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray();

    public static byte[] ZipArchive() =>
        "PK"u8.ToArray().Concat(new byte[] { 0x03, 0x04, 0x14, 0x00, 0x00, 0x00 }).ToArray();

    public static byte[] HtmlDocument() => "<!DOCTYPE html><html><body>hi</body></html>"u8.ToArray();
}
