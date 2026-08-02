using AdaPanel.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Data.Persistence;

public sealed class AdaPanelDbContext(DbContextOptions<AdaPanelDbContext> options) : DbContext(options)
{
    public DbSet<PanelUser> Users => Set<PanelUser>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<InstalledComponent> InstalledComponents => Set<InstalledComponent>();

    public DbSet<PanelSetting> Settings => Set<PanelSetting>();

    /// <summary>Sunucudan sunucuya erişim anahtarları (MCP bağlantıları). Bkz. <see cref="PanelApiToken"/>.</summary>
    public DbSet<PanelApiToken> ApiTokens => Set<PanelApiToken>();

    /// <summary>Maskelenmiş hata kayıtları; parmak izi üzerinden tekilleştirilir. Bkz. <see cref="ErrorReport"/>.</summary>
    public DbSet<ErrorReport> ErrorReports => Set<ErrorReport>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AdaPanelDbContext).Assembly);
    }
}
