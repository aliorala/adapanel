using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// PowerShell'den okunan metnin karakter kodlamasi.
/// <para>
/// CANLI OLAY: kullanicinin "epey urun cekme botu" adli cron gorevi calistirilamiyordu --
/// <c>Start-ScheduledTask ... HRESULT 0x80070002</c> (dosya bulunamadi). Gorev duruyordu; panel onu
/// BOZUK ADLA okuyordu.
/// </para>
/// <para>
/// Kok neden olculerek bulundu: PowerShell 5.1 yonlendirilmis ciktisini konsolun kod sayfasiyla
/// yazar, ProcessRunner ise UTF-8 ile okuyordu. Iki taraf anlasmadigi icin ASCII disindaki her
/// karakter kayboluyordu. Panel listeyi bozuk adla aliyor, kullanici "Simdi calistir"a basinca o
/// bozuk adi geri gonderiyor ve Gorev Zamanlayici goreve ulasamiyordu.
/// </para>
/// <para>
/// Sorun cron'a OZEL DEGILDI: PowerShell'den okunan Turkce karakterli her deger ayni sekilde
/// bozuluyordu -- site adlari, dosya yollari, hata mesajlari. Bu yuzden duzeltme tek bir yerde,
/// ProcessRunner'da yapildi ve testi de gercekten PowerShell calistirarak yapiliyor: bu hatayi
/// yalnizca UCTAN UCA olcum yakalayabilir, sahte bir nesne degil.
/// </para>
/// </summary>
public sealed class ProcessRunnerEncodingTests
{
    private const string TurkceMetin = "epey ürün çekme botu ĞÜŞİÖÇ ıi";

    [Fact]
    public async Task RunPowerShell_KeepsNonAsciiCharactersIntact()
    {
        var result = await ProcessRunner.RunPowerShellAsync($"Write-Output '{TurkceMetin}'");

        Assert.True(result.Success, result.StandardError);
        Assert.Contains(TurkceMetin, result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// Panelin gercekte kullandigi yol: nesne → ConvertTo-Json → C# tarafinda ayristirma.
    /// Gorev listesi, DNS bolgeleri ve daha bircok yer bu yoldan gecer.
    /// </summary>
    [Fact]
    public async Task RunPowerShell_KeepsNonAsciiInsideJsonOutput()
    {
        var result = await ProcessRunner.RunPowerShellAsync(
            $"[pscustomobject]@{{ Ad = '{TurkceMetin}' }} | ConvertTo-Json -Compress");

        Assert.True(result.Success, result.StandardError);

        using var belge = System.Text.Json.JsonDocument.Parse(result.StandardOutput.Trim());
        Assert.Equal(TurkceMetin, belge.RootElement.GetProperty("Ad").GetString());
    }

    /// <summary>
    /// Uzun scriptler gecici bir .ps1 dosyasindan calisir -- AYRI bir kod yolu. Duzeltmenin
    /// yalnizca kisa yolda yapilmasi, uzun senkron scriptlerinde sorunu sessizce birakirdi.
    /// </summary>
    [Fact]
    public async Task RunPowerShell_KeepsNonAsciiOnTheLongScriptPath()
    {
        // Esigin (8000) ustune cikacak kadar dolgu; ciktiyi degistirmeyen zararsiz atamalar.
        var dolgu = string.Join('\n', Enumerable.Range(0, 700).Select(sira => $"$adaDolgu{sira} = {sira}"));

        var result = await ProcessRunner.RunPowerShellAsync($"{dolgu}\nWrite-Output '{TurkceMetin}'");

        Assert.True(result.Success, result.StandardError);
        Assert.Contains(TurkceMetin, result.StandardOutput, StringComparison.Ordinal);
    }

    // Cikis kodu ve basari bilgisi eklenen onek yuzunden degismemeli.
    [Fact]
    public async Task RunPowerShell_StillReportsFailureExitCodes()
    {
        var result = await ProcessRunner.RunPowerShellAsync("exit 22");

        Assert.False(result.Success);
        Assert.Equal(22, result.ExitCode);
    }
}
