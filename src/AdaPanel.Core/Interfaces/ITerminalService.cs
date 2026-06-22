using AdaPanel.Core.Dtos.Terminal;

namespace AdaPanel.Core.Interfaces;

public interface ITerminalService
{
    Task<TerminalCommandResultDto> ExecuteAsync(
        ExecuteTerminalCommandRequest request,
        CancellationToken cancellationToken = default);
}
