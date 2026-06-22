using AdaPanel.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdaPanel.Data.Persistence.Configurations;

public sealed class PanelUserConfiguration : IEntityTypeConfiguration<PanelUser>
{
    public void Configure(EntityTypeBuilder<PanelUser> builder)
    {
        builder.ToTable("panel_users");
        builder.HasKey(user => user.Id);

        builder.Property(user => user.UserName)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(user => user.Email)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(user => user.PasswordHash)
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(user => user.PreferredLanguage)
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(user => user.TwoFactorSecretProtected)
            .HasMaxLength(1024);

        builder.Property(user => user.PendingTwoFactorSecretProtected)
            .HasMaxLength(1024);

        builder.HasIndex(user => user.UserName)
            .IsUnique();

        builder.HasIndex(user => user.Email)
            .IsUnique();
    }
}
