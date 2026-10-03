using AdaPanel.Core.Dtos.Diagnostics;

namespace AdaPanel.Core.Interfaces;

public interface IErrorReportSender
{
    Task<ErrorReportingSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<ErrorReportingSettingsDto> UpdateSettingsAsync(ErrorReportingSettingsDto settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bekleyen kayıtları toplayıcıya gönderir ve gönderilen sayısını döner.
    /// Onay yoksa veya toplayıcı adresi boşsa 0 döner (gönderim yapılmaz).
    /// </summary>
    Task<int> SendPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>Manual delivery bound to the settings revision reviewed by the caller.</summary>
    Task<int> SendPendingAsync(string expectedSettingsRevision, CancellationToken cancellationToken = default) =>
        SendPendingAsync(cancellationToken);
}
