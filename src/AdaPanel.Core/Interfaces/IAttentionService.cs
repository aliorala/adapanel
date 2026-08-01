using AdaPanel.Core.Dtos.Advisor;

namespace AdaPanel.Core.Interfaces;

/// <summary>
/// Menü rozetleri ve bildirim çekmecesi için tek "dikkat" akışı. YENİ VERİ ÜRETMEZ; panelin
/// hâlihazırdaki sinyallerini (Danışman, bileşen/panel güncellemeleri, DNS bind sağlığı, durmuş
/// servisler) toplayıp önceliklendirir ve varsa tek-tık düzeltme ucunu ekler.
/// </summary>
public interface IAttentionService
{
    Task<AttentionSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default);
}
