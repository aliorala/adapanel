using AdaPanel.Core.Dtos.Update;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Güncelleme paketinin indirilme ilerlemesini tutar (tekil/singleton).
/// <para>
/// Neden gerekli: paket indirmesi tek bir HTTP isteği içinde, bloklayarak yapılıyordu; istek 40 MB
/// inene kadar dönmüyor ve kullanıcı hiçbir ilerleme göremiyordu. Oysa indirme sırasında panel
/// AYAKTA — yani buradaki yüzde tahmin değil, gerçekten yazılan bayt sayısıdır. (Kurulumun kendisi
/// sırasında panel kapalı olduğu için orada aynı şey mümkün değildir; o aşama gözlemlenen
/// kilometre taşlarıyla gösterilir.)
/// </para>
/// </summary>
public interface IUpdateProgressTracker
{
    /// <summary>İndirme başladı; toplam boyut bilinmiyorsa null.</summary>
    void Start(long? totalBytes);

    /// <summary>O ana kadar diske yazılan toplam bayt.</summary>
    void Report(long receivedBytes);

    /// <summary>İndirme bitti (başarılı ya da hatayla); ilerleme artık etkin değil.</summary>
    void Complete(string? error = null);

    UpdateProgressDto Current { get; }
}
