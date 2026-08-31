using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Dış bir aracın çıktısı SINIRSIZ okunamaz.
/// </summary>
/// <remarks>
/// <para>
/// 0.8.9'da <c>ProcessRunner</c>'daki sınırsız <c>ReadToEndAsync</c> kapatıldı: akış sonuna kadar
/// tüketilir ama en çok bir tavan kadar biriktirilir. Ama <c>TerminalService</c> kendi sürecini
/// kuruyor (kodlanmış komut + çalışma dizini) ve o kapıdan HİÇ GEÇMİYORDU — yani açık, panelin
/// SYSTEM olarak koştuğu en güçlü ucunda açık kalmıştı. Tek bir
/// <c>Get-ChildItem C:\ -Recurse</c> paneli düşürmeye yeterdi.
/// </para>
/// <para>
/// Bu, bu deponun tekrar eden hata sınıfıdır: <b>koruma yazılır, tek bir yola bağlanır, kardeş yol
/// sessizce dışarıda kalır.</b> Nöbetçi üçüncü bir yolun aynı boşluğa düşmesini yakalar.
/// </para>
/// </remarks>
public sealed class SinirsizCiktiOkumaNobetcisiTests
{
    /// <summary>
    /// Bir sürecin çıktı akışı doğrudan <c>ReadToEndAsync</c> ile okunamaz.
    /// </summary>
    /// <remarks>
    /// Yasak ŞEKLE konur: <c>StandardOutput</c>/<c>StandardError</c> üzerinde doğrudan
    /// <c>ReadToEndAsync</c>. Tavanlı okuyucunun KENDİ içinde bir <c>ReadToEndAsync</c> vardır
    /// (tavan 0 = "sınırsız" dalı) ve o meşrudur — bu yüzden yasak, akış ADINA bağlanır.
    /// </remarks>
    [Fact]
    public void Surec_ciktisi_dogrudan_sonuna_kadar_okunmuyor()
    {
        var ihlaller = new List<string>();

        foreach (var dosya in Directory.EnumerateFiles(
                     Path.Combine(KaynakKoku(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            var kaynak = File.ReadAllText(dosya);

            foreach (Match eslesme in Regex.Matches(
                         kaynak, @"Standard(Output|Error)\s*\.\s*ReadToEndAsync\s*\("))
            {
                var satir = kaynak[..eslesme.Index].Count(karakter => karakter == '\n') + 1;
                ihlaller.Add($"{Path.GetFileName(dosya)}:{satir}");
            }
        }

        Assert.True(
            ihlaller.Count == 0,
            "Surec ciktisi SINIRSIZ okunuyor (ProcessRunner.TavanliOkuAsync kullanin): "
            + string.Join(", ", ihlaller));
    }

    /// <summary>
    /// NÖBETÇİNİN KENDİSİ ÖLÜ DOĞMASIN — desen iki yönde sınanır.
    /// </summary>
    /// <remarks>
    /// Bu oturumda beş test doğuştan ölü çıktı; biri de aradığı dizeyi kendi gerekçe yorumunda
    /// bulup hep yeşil kaldı. Desen temsili metin üzerinde hem yakalaması hem geçirmesi gerekeni
    /// ölçer.
    /// </remarks>
    [Theory]
    [InlineData("var t = process.StandardOutput.ReadToEndAsync(ct);", true)]
    [InlineData("var t = process.StandardError.ReadToEndAsync(ct);", true)]
    [InlineData("var t = ProcessRunner.TavanliOkuAsync(process.StandardOutput, 100, ct);", false)]
    [InlineData("return await reader.ReadToEndAsync(cancellationToken);", false)]
    public void Desen_iki_yonde_de_dogru(string ornek, bool ihlalBekleniyor)
    {
        var ihlal = Regex.IsMatch(ornek, @"Standard(Output|Error)\s*\.\s*ReadToEndAsync\s*\(");

        Assert.Equal(ihlalBekleniyor, ihlal);
    }

    private static string KaynakKoku()
    {
        var yol = AppContext.BaseDirectory;
        while (yol is not null && !File.Exists(Path.Combine(yol, "AdaPanel.sln")))
        {
            yol = Path.GetDirectoryName(yol);
        }

        Assert.NotNull(yol);
        return yol!;
    }
}
