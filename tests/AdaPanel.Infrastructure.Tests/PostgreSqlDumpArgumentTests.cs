using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// PostgreSQL döküm/geri yükleme komutlarının İKİ hayati bayrağı taşıdığını sabitler.
///
/// 1) <c>-v ON_ERROR_STOP=1</c>: psql, tek tek SQL ifadelerinin hatalarını çıkış koduna YANSITMAZ —
///    bağlantı kurulabildiği sürece 0 döner. Bu bayrak olmadan bozuk bir döküm geri yüklenirken her
///    ifade patlasa bile panel "geri yükleme tamam" der ve yönetici elindeki yedeği silebilir.
/// 2) Tek veritabanı: eskiden <c>pg_dumpall</c> kullanılıyor ve istenen veritabanı adı hiç
///    okunmuyordu; "şu veritabanını geri yükle" demek bütün kümenin (tüm roller ve veritabanları)
///    üzerine yazmak anlamına geliyordu.
/// </summary>
public sealed class PostgreSqlDumpArgumentTests
{
    // Komut dizeleri özel statik metotlarda üretildiği ve dışarıya açılmadığı için sözleşmeyi kaynak
    // dosyadan sabitliyoruz. Kırılgan görünse de yakaladığı regresyon gerçek: bu bayrakların
    // kaldırılması SESSİZ VERİ KAYBINA yol açıyor.
    private static string ReadSource()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "AdaPanel.Infrastructure", "Database", "DatabaseService.cs");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    [Fact]
    public void RestoreUsesOnErrorStop_SoFailedStatementsAreNotReportedAsSuccess()
    {
        var source = ReadSource();
        Assert.False(string.IsNullOrEmpty(source), "DatabaseService.cs okunamadı; test yolu güncellenmeli.");
        Assert.Contains("ON_ERROR_STOP=1", source);
    }

    [Fact]
    public void DumpTargetsASingleDatabase_NotTheWholeCluster()
    {
        var source = ReadSource();
        Assert.False(string.IsNullOrEmpty(source), "DatabaseService.cs okunamadı; test yolu güncellenmeli.");
        Assert.DoesNotContain("pg_dumpall.exe", source);
        Assert.Contains("pg_dump.exe", source);
    }

    // Parola yanlışsa psql/pg_dump stdin'den parola ister; servis bağlamında stdin yok, süreç
    // zaman aşımına kadar asılı kalır ve çağıran istek de bloke olur.
    [Fact]
    public void PostgreSqlCommandsNeverPromptForAPassword()
    {
        var source = ReadSource();
        Assert.False(string.IsNullOrEmpty(source), "DatabaseService.cs okunamadı; test yolu güncellenmeli.");
        Assert.Contains("--no-password", source);
    }
}
