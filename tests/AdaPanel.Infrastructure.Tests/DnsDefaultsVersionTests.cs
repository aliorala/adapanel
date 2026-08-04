using AdaPanel.Infrastructure.Dns;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Varsayilan set surumu ile yerlesik sablonun BIRLIKTE ilerlemesi.
/// <para>
/// CANLI OLAY: istemci otomatik yapilandirma kayitlari (4 SRV + _smtp._tls) 0.2.36'da yerlesik
/// sablona eklendi ve surum 3'e cikarildi. Ama o sirada BuildDefaultRecordsAsync, kayitli bir DNS
/// sablonu bulunca yerlesik varsayilanlari TAMAMEN golgeliyordu (0.2.62'de duzeltildi). Yani
/// birlestirme calisti, eklenecek kayit olarak yalnizca sablondakileri gordu, SRV'leri hic
/// uretmedi -- ve surum yine de 3 olarak damgalandi. Damga atildigi icin duzeltilmis birlestirme
/// bir daha calismayacakti. Sonuc harici DNS sorgusuyla olculdu: canli sunucudaki iki alan adinin
/// ikisinde de bes kaydin besi de yoktu, panel ise "eksik yok" diyordu.
/// </para>
/// <para>
/// Ders: varsayilan uretimini etkileyen bir duzeltme, surum damgasi artirilmadan ise yaramaz --
/// eski bolgeler duzeltmeyi HIC gormez. Bu dosya o baglantiyi yaziya doker.
/// </para>
/// </summary>
public sealed class DnsDefaultsVersionTests
{
    private static readonly string[] OtomatikYapilandirma =
    [
        "SRV|_submissions._tcp",
        "SRV|_submission._tcp",
        "SRV|_imaps._tcp",
        "SRV|_pop3s._tcp",
        "TXT|_smtp._tls"
    ];

    private static HashSet<string> YerlesikAnahtarlar() =>
        DnsService.BuiltInTemplate().Records
            .Select(record => $"{record.Type}|{record.Host}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void BuiltInTemplate_StillCarriesTheClientAutoconfigurationRecords()
    {
        var yerlesik = YerlesikAnahtarlar();

        Assert.All(OtomatikYapilandirma, key => Assert.Contains(key, yerlesik));
    }

    /// <summary>
    /// 3, golgeleme hatasiyla birlikte damgalanan surumdur. Eski bolgelerin duzeltilmis
    /// birlestirmeyi calistirabilmesi icin damganin ONUN OTESINDE olmasi sart.
    /// </summary>
    [Fact]
    public void DefaultsVersion_AdvancedPastTheReleaseThatStampedTheBrokenMerge()
    {
        Assert.True(
            DnsService.CurrentDefaultsVersion > 3,
            "Varsayilan uretimi duzeltildiginde surum damgasi da artirilmali; yoksa mevcut " +
            "bolgeler birlestirmeyi bir daha calistirmaz ve duzeltmeyi hic gormez.");
    }

    /// <summary>
    /// Port 25 istemcilere DUYURULMAZ: o MTA'lar arasidir. Duyurulursa istemci kimlik
    /// dogrulamasiz, cogu saglayicida engelli bir yoldan gondermeye calisir.
    /// </summary>
    [Fact]
    public void AutoconfigurationSrvRecords_AdvertiseSubmissionPortsOnly()
    {
        var portlar = DnsService.BuiltInTemplate().Records
            .Where(record => string.Equals(record.Type, "SRV", StringComparison.OrdinalIgnoreCase))
            .Select(record => record.Value.Split(' ')[1])
            .ToList();

        Assert.Equal(["465", "587", "993", "995"], portlar);
        Assert.DoesNotContain("25", portlar);
    }
}
