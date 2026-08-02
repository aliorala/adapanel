using AdaPanel.Core.Dtos.PanelAccess;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Panelin kendi alan adıyla, GEÇERLİ bir sertifikayla dışarıya açılması.
/// <para>
/// Var oluş sebebi: panel varsayılan olarak kendinden imzalı sertifikayla çalışır. Tarayıcı uyarıyı
/// geçebilir, yapay zeka istemcileri (MCP) GEÇEMEZ. Bu servis, mevcut altyapıyı (IIS ters vekil +
/// win-acme) kullanarak paneli yöneticinin belirlediği alan adında yayınlar.
/// </para>
/// </summary>
public interface IPanelPublicAccessService
{
    /// <summary>
    /// Mevcut durumu ÖLÇER (hiçbir şeyi değiştirmez): alan adı ayarlı mı, IIS sitesi ve ters vekil
    /// yerinde mi, 443'te sunulan sertifika geçerli mi (kendinden imzalı mı, kaç gün kaldı),
    /// panel dışarıdan gerçekten açılıyor mu.
    /// </summary>
    Task<PanelAccessStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Kurulumu sırayla yapar ve HER ADIMIN sonucunu ayrı ayrı döndürür. Bir adım başarısızsa
    /// hangisinin başarısız olduğu sonuçtan net görülür; "kuruldu" deyip yarım bırakmaz.
    /// </summary>
    Task<PanelAccessStatusDto> SetupAsync(SetupPanelAccessRequest request, CancellationToken cancellationToken = default);
}
