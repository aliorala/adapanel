using AdaPanel.Core.Dtos.Health;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Sunucudaki alan adlarını panelin GÜNCEL varsayılanlarına taşır (Plesk "repair/reconfigure"
/// paritesi).
/// <para>
/// Çözdüğü sorun: yeni sürümlerle gelen ayarlar yalnızca yeni oluşturulan alan adlarına uygulanır.
/// Panel güncellendiğinde eskiden eklenmiş alan adları DMARC, DKIM, MX gibi kayıtlardan habersiz
/// kalır; eksiklik yıllarca sessiz durup ilk belirtisini "giden postalar spam'e düşüyor" olarak
/// verir. Bu servis eksikleri bulur, tamamlar ve NE EKLEDİĞİNİ tek tek raporlar.
/// </para>
/// </summary>
public interface IDomainRefreshService
{
    /// <summary>Tek alan adını tazeler.</summary>
    Task<DomainRefreshResultDto> RefreshAsync(string siteName, CancellationToken cancellationToken = default);

    /// <summary>Sunucudaki tüm alan adlarını tazeler; bir alan adının hatası diğerlerini durdurmaz.</summary>
    Task<DomainRefreshSummaryDto> RefreshAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// HİÇBİR ŞEY DEĞİŞTİRMEDEN kaç alan adının eski kaldığını ölçer.
    /// <para>
    /// Panelin bu eksikliği kendiliğinden fark edip "Dikkat" listesinde göstermesi için gerekli:
    /// uyarı üretmek için durumu değiştirmek kabul edilemez, kullanıcı onarımı KENDİ onaylamalı.
    /// Posta kayıtları burada SAYILMAZ (her sayımda posta sunucusuna sorgu atmak ekranı yavaşlatır);
    /// ölçüm eksik DNS varsayılanları üzerindendir.
    /// </para>
    /// </summary>
    Task<DomainRefreshSummaryDto> InspectAllAsync(CancellationToken cancellationToken = default);
}
