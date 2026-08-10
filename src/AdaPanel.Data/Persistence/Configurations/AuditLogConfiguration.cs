using AdaPanel.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdaPanel.Data.Persistence.Configurations;

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");
        builder.HasKey(log => log.Id);

        builder.Property(log => log.Action)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(log => log.Resource)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(log => log.Details)
            .HasMaxLength(2048)
            .IsRequired();

        builder.Property(log => log.IpAddress)
            .HasMaxLength(64);

        // Kiracı görünürlüğü HER sorguda bu iki sütuna bakar (AuditLogQueryEngine.ApplyVisibility).
        // Dizin olmadan, kayıt tablosu büyüdükçe kiracının kendi denetim ekranı tam tablo taraması
        // yapardı — yani özellik, en çok işe yaradığı yerde (uzun geçmişte) kullanılamaz hâle gelirdi.
        builder.HasIndex(log => log.UserId).HasDatabaseName("ix_audit_logs_user");
        builder.HasIndex(log => log.SubscriptionUserId).HasDatabaseName("ix_audit_logs_subscription");
    }
}

