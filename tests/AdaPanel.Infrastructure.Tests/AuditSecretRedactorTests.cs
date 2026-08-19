using AdaPanel.Infrastructure.Audit;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Denetim kaydının İKİNCİ savunma hattı: merkezî maskeleme geçidi.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı bir test dosyası:</b> <c>AuditCoverageTests</c> BİRİNCİ hattı korur — çağrı
/// yerlerinin sırrı hiç göndermemesini. Ama o hat "kaynakta görünen" bir kuraldır ve bir gün bir
/// modül sırrı serbest metnin içinde (ör. imzalı bir URL olarak) yazarsa taramaya takılmaz. İkinci
/// hat, yazma yolunun tek geçidinde çalışır ve çağrı yerinin niyetinden bağımsızdır.
/// </para>
/// <para>
/// <b>Bu turda kapatılan boşluklar (denetim raporu P2):</b> geçit <c>refresh_token</c>,
/// <c>x-amz-signature</c>, <c>sig</c> (Azure SAS), <c>authorization</c> ve <c>Bearer &lt;jeton&gt;</c>
/// biçimlerini TANIMIYORDU. Bugün bunları yazan bir çağrı yeri yok — yani aktif bir sızıntı
/// düzeltilmiyor; kapatılan şey, yarın yazan biri çıktığında geçidin sessizce geçirmesiydi.
/// </para>
/// </remarks>
public sealed class AuditSecretRedactorTests
{
    /// <summary>
    /// Sır adı taşıyan alanların DEĞERİ, ad biçimi ne olursa olsun maskelenir.
    /// </summary>
    /// <remarks>
    /// Eski desen anahtarı <c>\b...\b</c> ile sınırlıyordu; alt çizgi KELİME KARAKTERİ olduğu için
    /// <c>refresh_token=</c> gibi en yaygın biçimler tam olarak kaçıyordu.
    /// </remarks>
    [Theory]
    [InlineData("refresh_token=eyJhbGciOiJIUzI1NiJ9", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("access_token: abc123def456", "abc123def456")]
    [InlineData("api.key=sk-1234567890", "sk-1234567890")]
    [InlineData("X-Amz-Signature=9f86d081884c7d659a2f", "9f86d081884c7d659a2f")]
    [InlineData("https://depo.blob.core.windows.net/yedek.zip?sv=2021&sig=abc%2Fdef123", "abc%2Fdef123")]
    [InlineData("authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9")]
    [InlineData("parola=Abc123!", "Abc123!")]
    [InlineData("ConnectionString=Server=x;Password=Abc123!", "Abc123!")]
    public void Sir_adi_tasiyan_alanin_DEGERI_maskelenir(string girdi, string sizmamasiGereken)
    {
        var sonuc = AuditSecretRedactor.Redact(girdi);

        Assert.DoesNotContain(sizmamasiGereken, sonuc, StringComparison.Ordinal);
        Assert.Contains(AuditSecretRedactor.Mask, sonuc, StringComparison.Ordinal);
    }

    /// <summary>
    /// Anahtarsız <c>Bearer &lt;jeton&gt;</c>: ayraç boşluk olduğu için ayrı desen gerekir.
    /// </summary>
    [Fact]
    public void Anahtarsiz_BEARER_jetonu_maskelenir()
    {
        const string jeton = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9";

        var sonuc = AuditSecretRedactor.Redact($"Upstream reddetti: Bearer {jeton}");

        Assert.DoesNotContain(jeton, sonuc, StringComparison.Ordinal);

        // Şema ADI korunur: kayıt "burada bir kimlik bilgisi vardı" bilgisini kaybetmemeli.
        Assert.Contains("Bearer", sonuc, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>authorization: Bearer &lt;jeton&gt;</c> — DEĞER İKİ PARÇADIR.
    /// </summary>
    /// <remarks>
    /// Bu, maskelemenin en sinsi başarısızlık biçimiydi: anahtar tanınsa bile değer olarak yalnız
    /// "Bearer" alınsaydı, maskeleme ÇALIŞMIŞ görünür (kayıtta <c>[gizlendi]</c> yazar) ama jetonun
    /// kendisi hemen yanında dururdu.
    /// </remarks>
    [Fact]
    public void Authorization_basliginda_jeton_da_maskelenir()
    {
        const string jeton = "AbCdEf0123456789GhIjKlMnOpQrStUv";

        var sonuc = AuditSecretRedactor.Redact($"authorization: Bearer {jeton} (istek reddedildi)");

        Assert.DoesNotContain(jeton, sonuc, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kayıt OKUNABİLİR kalmalı: insan cümleleri maskelenmez.
    /// </summary>
    /// <remarks>
    /// Boşluğu genel ayraç saymak, "password was changed" gibi cümlelerde bir sonraki kelimeyi
    /// yiyip kaydı anlamsızlaştırırdı — denetim kaydının değeri okunabilirliğindedir. Bu yüzden
    /// boşluk yalnız BİLİNEN kimlik şemalarından sonra ve yalnız devamı gerçekten kimlik bilgisine
    /// benziyorsa sır sayılır.
    /// </remarks>
    [Theory]
    [InlineData("Mailbox password was changed for user bob.")]
    [InlineData("Bearer token missing")]
    [InlineData("FTPS requirement set to True.")]
    public void Insan_cumleleri_MASKELENMEZ(string girdi) =>
        Assert.Equal(girdi, AuditSecretRedactor.Redact(girdi));

    /// <summary>Boş/null girdi çökertmez (kayıt yazımı denetim uğruna patlamamalı).</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Bos_girdi_cokertmez(string? girdi) =>
        Assert.Equal(string.Empty, AuditSecretRedactor.Redact(girdi));

    /// <summary>
    /// Kırpma maskelemeden SONRA gelir: uzun bir metin sırrı kırpma sınırının ötesinde taşıyor
    /// olabilir ama kırpılan bir kayıt hâlâ yazılır ve sınır içindeki sır maskelenmiş olmalıdır.
    /// </summary>
    [Fact]
    public void Uzun_ayrinti_kirpilir_ama_sir_yine_de_maskelenir()
    {
        var girdi = "password=CokGizli123! " + new string('x', 8000);

        var sonuc = AuditSecretRedactor.SanitizeDetails(girdi);

        Assert.DoesNotContain("CokGizli123!", sonuc, StringComparison.Ordinal);
        Assert.True(sonuc.Length <= AuditSecretRedactor.DetailsMaxLength + 1);
    }
}
