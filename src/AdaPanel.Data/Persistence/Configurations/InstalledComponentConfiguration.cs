using AdaPanel.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdaPanel.Data.Persistence.Configurations;

public sealed class InstalledComponentConfiguration : IEntityTypeConfiguration<InstalledComponent>
{
    public void Configure(EntityTypeBuilder<InstalledComponent> builder)
    {
        builder.ToTable("installed_components");
        builder.HasKey(component => component.Id);

        builder.Property(component => component.ComponentId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(component => component.DisplayName)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(component => component.Version)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(component => component.InstallDir)
            .HasMaxLength(1024)
            .IsRequired();

        builder.Property(component => component.ServiceName)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(component => component.ProtectedConfigurationJson)
            .HasMaxLength(4096)
            .IsRequired();

        builder.Property(component => component.LastError)
            .HasMaxLength(2048)
            .IsRequired();

        builder.HasIndex(component => component.ComponentId)
            .IsUnique();
    }
}
