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
    }
}

