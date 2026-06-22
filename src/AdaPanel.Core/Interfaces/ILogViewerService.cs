using AdaPanel.Core.Dtos.Logs;

namespace AdaPanel.Core.Interfaces;

public interface ILogViewerService
{
    Task<LogTailDto> TailPanelLogAsync(int lines, CancellationToken cancellationToken = default);

    Task<LogTailDto> TailSiteLogAsync(string siteName, int lines, CancellationToken cancellationToken = default);
}
