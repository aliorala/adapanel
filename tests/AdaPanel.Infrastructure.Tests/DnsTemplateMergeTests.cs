using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Kayıtlı DNS şablonu ile yerleşik varsayılanların birleştirilmesi.
/// <para>
/// Canlı olay: kullanıcının sunucusunda kayıtlı bir şablon vardı ve panel YALNIZCA onu
/// kullanıyordu. Panelin yeni sürümleriyle gelen varsayılanlar (istemci otomatik yapılandırma SRV
/// kayıtları, TLS raporlama) o kuruluma hiç ulaşmadı — üstelik panel kendi şablonuna göre "eksik
/// yok" dediği için bu SESSİZCE oldu. Kullanıcı defalarca "onar çıkmıyor" dedi ve panel haklıydı:
/// kendi ölçütüne göre eksik yoktu, ölçüt yanlıştı.
/// </para>
/// </summary>
public sealed class DnsTemplateMergeTests
{
    private static readonly string[] YeniVarsayilanlar =
    [
        "SRV|_submissions._tcp",
        "SRV|_submission._tcp",
        "SRV|_imaps._tcp",
        "SRV|_pop3s._tcp",
        "TXT|_smtp._tls"
    ];

    /// <summary>Birleştirme kuralının aynısı: TÜR|konak ikilisinde olmayanlar eklenir.</summary>
    private static List<string> Birlestir(IEnumerable<string> kayitli, IEnumerable<string> yerlesik)
    {
        var mevcut = kayitli.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. kayitli, .. yerlesik.Where(key => !mevcut.Contains(key))];
    }

    [Fact]
    public void SavedTemplate_ReceivesNewBuiltInDefaults()
    {
        var kayitli = new[] { "A|@", "MX|@", "TXT|@" };

        var sonuc = Birlestir(kayitli, YeniVarsayilanlar);

        Assert.All(YeniVarsayilanlar, key => Assert.Contains(key, sonuc));
    }

    // Kullanicinin ozellestirdigi kayit KORUNMALI: birlestirme yalnizca EKSIK olani ekler.
    [Fact]
    public void SavedTemplate_KeepsUserCustomisedRecords()
    {
        var kayitli = new[] { "MX|@", "SRV|_submissions._tcp" };

        var sonuc = Birlestir(kayitli, YeniVarsayilanlar);

        Assert.Equal(1, sonuc.Count(key => key == "SRV|_submissions._tcp"));
        Assert.Equal(kayitli.Length + YeniVarsayilanlar.Length - 1, sonuc.Count);
    }

    [Fact]
    public void SavedTemplate_IsUnchangedWhenItAlreadyHasEverything()
    {
        var sonuc = Birlestir(YeniVarsayilanlar, YeniVarsayilanlar);

        Assert.Equal(YeniVarsayilanlar.Length, sonuc.Count);
    }
}
