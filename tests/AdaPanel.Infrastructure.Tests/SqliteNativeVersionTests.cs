using Microsoft.Data.Sqlite;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Panelin kendi veritabanini calistiran YEREL SQLite kitapligi surumunu OLCER.
/// <para>
/// Sebep: <c>dotnet list package --vulnerable</c>, EF Core 8.0.27 ile gelen
/// <c>SQLitePCLRaw.lib.e_sqlite3 2.1.6</c> icin HIGH bildirmisti (CVE-2025-6965 /
/// GHSA-2m69-gcr7-jv3q: SQLite 3.50.2'den once, toplama terimi sayisi sutun sayisini asinca
/// bellek bozulmasi). Paket surumu tek basina KANIT DEGILDIR -- asil soru, calisma aninda hangi
/// yerel SQLite'in yuklendigidir. Bu test onu SQLite'in kendisine sorar.
/// </para>
/// <para>
/// Ayrica geriye dusmeyi engeller: birisi paketi eski surume sabitlerse ya da baska bir bagimlilik
/// eski <c>e_sqlite3</c>'u getirirse burada patlar; sessizce acik surume donulmez.
/// </para>
/// </summary>
public sealed class SqliteNativeVersionTests
{
    // SQLite 3.50.2 = CVE-2025-6965'in kapatildigi surum.
    private static readonly Version AcikKapatanSurum = new(3, 50, 2);

    [Fact]
    public void NativeSqliteLibrary_IsAtLeastTheVersionThatFixesCve20256965()
    {
        using var baglanti = new SqliteConnection("Data Source=:memory:");
        baglanti.Open();
        using var komut = baglanti.CreateCommand();
        komut.CommandText = "select sqlite_version();";
        var ham = komut.ExecuteScalar() as string;

        Assert.False(string.IsNullOrWhiteSpace(ham), "Yerel SQLite surumu okunamadi.");
        var surum = Version.Parse(ham!);

        Assert.True(
            surum >= AcikKapatanSurum,
            $"Yerel SQLite {surum}; CVE-2025-6965 icin en az {AcikKapatanSurum} gerekiyor. " +
            "SQLitePCLRaw bagimliligi geriye dusmus olabilir (src/AdaPanel.Data/AdaPanel.Data.csproj).");
    }
}
