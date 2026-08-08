using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Extensions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Bağımlılık kaydının bütünlüğü.
/// </summary>
/// <remarks>
/// <para>
/// Burada eskiden <c>Assert.True(true)</c> vardı: her koşulda geçen, hiçbir şey doğrulamayan bir
/// test. Test sayısını şişirmek dışında bir işe yaramıyordu — üstelik "891 test geçiyor" cümlesine
/// duyulan güveni haksız yere besliyordu.
/// </para>
/// <para>
/// Yerine gerçek bir duman testi kondu: <b>eksik bir DI kaydı derleme hatası değildir</b>, panel
/// açılırken ya da o uç ilk çağrıldığında çalışma zamanında patlar. Bir servise yeni bağımlılık
/// eklemek (bu depoda sık yapılıyor: IWafService, IConcurrencyLock, IQueryLogService, ILogger…)
/// tam olarak bu riski doğurur. <c>ValidateOnBuild</c> bağımlılık grafiğini kurulum anında
/// denetler ve eksik kaydı burada, canlıda değil, yakalar.
/// </para>
/// </remarks>
public sealed class SmokeTests
{
    [Fact]
    public void Butun_servisler_cozulebilir_bir_bagimlilik_grafigi_olusturur()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Gerçek bir kurulum gerekmiyor; yalnız seçenek bağlaması için değer lazım.
                ["Panel:DataRoot"] = Path.Combine(Path.GetTempPath(), "ada-di-dogrulama"),
                ["ConnectionStrings:Default"] = "Data Source=:memory:"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // IConfiguration'ı da API kaydeder; bazı servisler doğrudan onu enjekte ediyor.
        services.AddSingleton<IConfiguration>(configuration);

        // DbContext'i API projesi kaydeder, altyapı değil; grafiği doğrulayabilmek için burada
        // eşdeğerini veriyoruz (bellek içi SQLite — disk/veri gerekmez).
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();
        services.AddDbContext<AdaPanelDbContext>(options => options.UseSqlite(connection));

        services.AddAdaPanelInfrastructure(configuration);

        // ValidateOnBuild: her kaydın bağımlılıkları KARŞILANABİLİYOR mu? Nesneleri gerçekten
        // kurmaz (bu testin IIS/veritabanı istememesinin sebebi), yalnız grafiği doğrular.
        var exception = Record.Exception(() => services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        }));

        Assert.Null(exception);
    }
}
