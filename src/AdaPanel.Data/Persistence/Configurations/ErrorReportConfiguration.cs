using AdaPanel.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdaPanel.Data.Persistence.Configurations;

public sealed class ErrorReportConfiguration : IEntityTypeConfiguration<ErrorReport>
{
    public void Configure(EntityTypeBuilder<ErrorReport> builder)
    {
        builder.ToTable("error_reports");
        builder.HasKey(report => report.Id);

        builder.Property(report => report.Fingerprint)
            .HasMaxLength(64)
            .IsRequired();

        // Her hata kaydında önce parmak iziyle arama yapılır (tekilleştirme); indeks olmadan
        // tablo büyüdükçe bu arama tam tarama olur ve hata anında ek yük yaratır.
        // TEKİL DEĞİL: eşzamanlı iki istek aynı hatayı aynı anda kaydederse kısıt ihlali
        // fırlatıp asıl hatayı gölgelemesin — nadir çift kayıt zararsızdır.
        builder.HasIndex(report => report.Fingerprint)
            .HasDatabaseName("ix_error_reports_fingerprint");

        // Gönderilmemişleri çekme ve temizlik sorgusu bu iki alan üzerinden çalışır.
        builder.HasIndex(report => new { report.SentAtUtc, report.LastSeenAtUtc })
            .HasDatabaseName("ix_error_reports_sent_last_seen");

        builder.Property(report => report.PanelVersion)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(report => report.OsVersion)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(report => report.ExceptionType)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(report => report.Message)
            .HasMaxLength(2048)
            .IsRequired();

        builder.Property(report => report.StackTrace)
            .HasMaxLength(8192)
            .IsRequired();

        builder.Property(report => report.RoutePattern)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(report => report.CorrelationId)
            .HasMaxLength(128)
            .IsRequired();
    }
}
