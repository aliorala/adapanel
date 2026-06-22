using AdaPanel.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdaPanel.Data.Persistence.Configurations;

public sealed class PanelSettingConfiguration : IEntityTypeConfiguration<PanelSetting>
{
    public void Configure(EntityTypeBuilder<PanelSetting> builder)
    {
        builder.ToTable("panel_settings");
        builder.HasKey(setting => setting.Key);

        builder.Property(setting => setting.Key)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(setting => setting.Value)
            .HasMaxLength(4096)
            .IsRequired();
    }
}

