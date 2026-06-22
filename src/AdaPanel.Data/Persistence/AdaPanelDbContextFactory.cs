using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AdaPanel.Data.Persistence;

/// <summary>
/// YALNIZ tasarım zamanı (dotnet ef migrations) için DbContext üretir. Çalışma zamanında DI'daki
/// gerçek yapılandırma (ServiceCollectionExtensions.UseSqlite) kullanılır; buradaki bağlantı yalnız
/// EF aracının modeli oluşturabilmesi içindir, gerçek veritabanına bağlanmaz.
/// </summary>
public sealed class AdaPanelDbContextFactory : IDesignTimeDbContextFactory<AdaPanelDbContext>
{
    public AdaPanelDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AdaPanelDbContext>()
            .UseSqlite("Data Source=adapanel-designtime.db")
            .Options;

        return new AdaPanelDbContext(options);
    }
}
