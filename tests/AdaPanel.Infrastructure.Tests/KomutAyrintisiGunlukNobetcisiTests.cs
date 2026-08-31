using System.Text.RegularExpressions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Komut ayrıntısı GÜNLÜĞE ham yazılamaz.
/// </summary>
/// <remarks>
/// <para>
/// 0.8.9'da sır maskeleme <c>AppException</c> kurucusuna kondu, yani <c>Details</c> taşıyan her
/// yol tek bir kapıdan geçiyor. Ama <c>BuildCommandDetails</c> sonucunu doğrudan
/// <c>logger.Log*</c>'a veren bir yer o kapıya HİÇ UĞRAMIYORDU (<c>DnsService</c>, uzun TXT
/// kaydı yolu) — kural vardı, o çağrı yeri kuralın dışındaydı.
/// </para>
/// <para>
/// Bu, bu depoda tekrar eden hata sınıfıdır: <b>koruma yazılır, tek bir yola bağlanır, kardeş yol
/// sessizce dışarıda kalır.</b> Nöbetçi bu yüzden ŞEKLE bakar: komut ayrıntısı bir günlük
/// çağrısının içine giriyorsa, aynı ifadede maskeleyici de geçmelidir.
/// </para>
/// </remarks>
public sealed class KomutAyrintisiGunlukNobetcisiTests
{
    [Fact]
    public void Hicbir_yer_komut_ayrintisini_gunluge_ham_yazmiyor()
    {
        var ihlaller = new List<string>();

        foreach (var dosya in Directory.EnumerateFiles(
                     Path.Combine(KaynakKoku(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            var kaynak = File.ReadAllText(dosya);

            foreach (Match eslesme in Regex.Matches(kaynak, @"Build(Command|Process)Details\s*\("))
            {
                // METOT TANIMI ÇAĞRI DEĞİLDİR -- ve bunu nöbetçinin İLK KOŞUSU yazdırdı: desen
                // `private static string BuildCommandDetails(CommandResult result)` satırını da
                // yakaladı ve önündeki pencerede ilgisiz bir günlük çağrısı bulunca yanlış alarm
                // verdi. Aynı koşuda GERÇEK bir ihlal de buldu (FtpService), yani nöbetçi işe
                // yaradı; ama yanlış alarmı bırakmak onu zamanla susturulan bir araca çevirirdi.
                var oncekiSatirBasi = kaynak.LastIndexOf('\n', Math.Max(0, eslesme.Index - 1)) + 1;
                var satirBasi = kaynak[oncekiSatirBasi..eslesme.Index];
                if (Regex.IsMatch(satirBasi, @"\b(string|static|private|public|internal|protected)\b"))
                {
                    continue;
                }

                // Çağrının ÖNÜNDEKİ pencere: bir günlük çağrısının argüman listesindeysek
                // "logger.LogWarning(" gibi bir açılış birkaç yüz karakter geride durur.
                // Pencere BİLEREK dar: geniş tutmak, aynı metotta bulunan ilgisiz bir günlük
                // satırını yakalayıp yanlış alarm üretirdi.
                var bas = Math.Max(0, eslesme.Index - 400);
                var pencere = kaynak[bas..eslesme.Index];

                var gunlukCagrisi = Regex.IsMatch(pencere, @"\.Log(Trace|Debug|Information|Warning|Error|Critical)\s*\(");
                if (!gunlukCagrisi)
                {
                    continue;
                }

                // Günlük çağrısından SONRA başka bir ifade açılmışsa (örneğin araya bir
                // `throw new AppException(` girmişse) o günlük bu çağrıya ait değildir.
                var sonGunluk = pencere.LastIndexOf(".Log", StringComparison.Ordinal);
                var araya_giren = pencere[sonGunluk..];
                if (araya_giren.Contains("AppException", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!araya_giren.Contains("SecretRedactor", StringComparison.Ordinal))
                {
                    var satir = kaynak[..eslesme.Index].Count(karakter => karakter == '\n') + 1;
                    ihlaller.Add($"{Path.GetFileName(dosya)}:{satir}");
                }
            }
        }

        Assert.True(
            ihlaller.Count == 0,
            "Komut ayrintisi gunluge HAM yaziliyor (SecretRedactor'dan gecirin): " + string.Join(", ", ihlaller));
    }

    /// <summary>
    /// NÖBETÇİNİN KENDİSİ ÖLÜ DOĞMASIN.
    /// </summary>
    /// <remarks>
    /// Bu oturumda beş test "doğuştan ölü" çıktı ve yalnız mutasyon yakaladı; bir tanesi de
    /// kaynak metnini tararken aradığı dizeyi kendi gerekçe yorumunda bulup hep yeşil kalmıştı.
    /// Bu yüzden desen, temsili bir metin üzerinde İKİ YÖNDE birden sınanır: ihlali yakalıyor mu,
    /// ve maskelenmiş hâli geçiriyor mu.
    /// </remarks>
    [Theory]
    [InlineData("logger.LogWarning(\"x {E}\", BuildCommandDetails(result));", true)]
    [InlineData("logger.LogWarning(\"x {E}\", SecretRedactor.SanitizeDetails(BuildCommandDetails(result)));", false)]
    [InlineData("throw new AppException(\"k\", 500, 500, BuildCommandDetails(result));", false)]
    public void Desen_iki_yonde_de_dogru(string ornek, bool ihlalBekleniyor)
    {
        // Gerçek dosyalardaki gibi, günlük çağrısının önünde başka kod olabilir.
        var kaynak = "void M() {\n    var result = Run();\n    " + ornek + "\n}";

        var eslesme = Regex.Match(kaynak, @"Build(Command|Process)Details\s*\(");
        Assert.True(eslesme.Success);

        var pencere = kaynak[Math.Max(0, eslesme.Index - 400)..eslesme.Index];
        var gunluk = Regex.IsMatch(pencere, @"\.Log(Trace|Debug|Information|Warning|Error|Critical)\s*\(");

        var ihlal = false;
        if (gunluk)
        {
            var sonGunluk = pencere.LastIndexOf(".Log", StringComparison.Ordinal);
            var araya_giren = pencere[sonGunluk..];
            ihlal = !araya_giren.Contains("AppException", StringComparison.Ordinal)
                    && !araya_giren.Contains("SecretRedactor", StringComparison.Ordinal);
        }

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
