using AdaPanel.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdaPanel.Data.Persistence.Configurations;

public sealed class PanelApiTokenConfiguration : IEntityTypeConfiguration<PanelApiToken>
{
    public void Configure(EntityTypeBuilder<PanelApiToken> builder)
    {
        builder.ToTable("api_tokens");
        builder.HasKey(token => token.Id);

        builder.Property(token => token.Name)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(token => token.TokenHash)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(token => token.Suffix)
            .HasMaxLength(8)
            .IsRequired();

        // Her istekte karma ile aranacak: benzersiz indeks hem hızı hem çakışma güvenliğini verir.
        builder.HasIndex(token => token.TokenHash).IsUnique();

        // Kullanıcının anahtarlarını listelemek sık bir işlem.
        builder.HasIndex(token => token.UserId);
    }
}
