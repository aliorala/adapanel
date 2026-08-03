using AdaPanel.Infrastructure.Diagnostics;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Maskeleyicinin ANAHTAR ADINA BAĞLI OLMAYAN sırları da yakaladığını sabitler.
///
/// Güvenlik denetiminde bulunan kaçaklar: "password=" gibi bir anahtar adı olmadan, metinde çıplak
/// geçen değerler hiçbir kalıba takılmıyordu. En kritiği panelin KENDİ erişim anahtarıydı — bir hata
/// mesajına düşerse toplayıcıya düz metin gider ve o anahtar paneli yönetmeye yeter.
/// </summary>
public sealed class ErrorReportSanitizerLeakTests
{
    /// <summary>Yalnız mesaj alanını maskeleyip sonucu döndürür; testin ilgilendiği tek şey o.</summary>
    private static string Clean(string input) =>
        ErrorReportSanitizer.Sanitize("System.Exception", input, string.Empty, "/api/v1/test").Message;

    // Panelin kendi anahtarı: en can sıkıcı kaçak.
    [Fact]
    public void Sanitize_RedactsPanelApiKey()
    {
        var key = "adap_" + new string('a', 64);
        var cleaned = Clean($"Yetkisiz istek: {key} reddedildi.");

        Assert.DoesNotContain(key, cleaned, StringComparison.Ordinal);
        Assert.Contains("<redacted>", cleaned, StringComparison.Ordinal);
    }

    // Çıplak JWT: "Bearer" öneki olmadan da geçebilir.
    [Fact]
    public void Sanitize_RedactsBareJwt()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        var cleaned = Clean($"The token is: {jwt}");

        Assert.DoesNotContain("eyJzdWIiOiIxMjM0NTY3ODkw", cleaned, StringComparison.Ordinal);
    }

    // Veritabanı adı müşteri adını taşır; bağlantı hatalarında olduğu gibi görünüyordu.
    [Theory]
    [InlineData("Database=musteri_wp_db", "musteri_wp_db")]
    [InlineData("Initial Catalog=AhmetTicaret", "AhmetTicaret")]
    public void Sanitize_RedactsDatabaseNames(string input, string secret)
    {
        Assert.DoesNotContain(secret, Clean($"Bağlantı kurulamadı. {input};"), StringComparison.Ordinal);
    }

    // Panel bu komutları ProcessRunner ile çalıştırıyor; hata metninde komut satırı görünebilir.
    [Fact]
    public void Sanitize_RedactsCommandLinePasswordFlag()
    {
        var cleaned = Clean(@"sqlcmd -S . -U sa -P MyP@ssw0rd! komutu başarısız oldu");

        Assert.DoesNotContain("MyP@ssw0rd!", cleaned, StringComparison.Ordinal);
    }

    // Windows hesabı TEK ters bölülüdür; UNC kalıbı (iki ters bölü) onu yakalamıyordu.
    [Theory]
    [InlineData(@"Login failed for user 'WIN-SRV01\ahmet'", "ahmet")]
    [InlineData(@"IIS APPPOOL\musterisite erişimi reddedildi", "musterisite")]
    public void Sanitize_RedactsWindowsAccountNames(string input, string secret)
    {
        Assert.DoesNotContain(secret, Clean(input), StringComparison.Ordinal);
    }

    // Gevşetme değil sıkılaştırma yapıldı: teşhis için gereken bilgi durmalı, yoksa rapor işe yaramaz.
    [Fact]
    public void Sanitize_KeepsDiagnosticContext()
    {
        var cleaned = Clean("System.IO.FileNotFoundException: yapılandırma okunamadı");

        Assert.Contains("FileNotFoundException", cleaned, StringComparison.Ordinal);
    }
}
