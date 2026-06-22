using AdaPanel.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Data.Persistence;

public sealed class AdaPanelDbContext(DbContextOptions<AdaPanelDbContext> options) : DbContext(options)
{
    public DbSet<PanelUser> Users => Set<PanelUser>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<InstalledComponent> InstalledComponents => Set<InstalledComponent>();

    public DbSet<PanelSetting> Settings => Set<PanelSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AdaPanelDbContext).Assembly);
    }
}
