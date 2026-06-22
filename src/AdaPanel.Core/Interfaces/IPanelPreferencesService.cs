using AdaPanel.Core.Dtos.Settings;

namespace AdaPanel.Core.Interfaces;

public interface IPanelPreferencesService
{
    Task<PanelPreferencesDto> GetAsync(CancellationToken cancellationToken = default);

    Task<PanelPreferencesDto> UpdateAsync(UpdatePanelPreferencesRequest request, CancellationToken cancellationToken = default);
}
