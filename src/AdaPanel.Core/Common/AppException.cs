namespace AdaPanel.Core.Common;

public sealed class AppException(
    string messageKey,
    int code = 1,
    int httpStatusCode = 400,
    string details = "") : Exception(messageKey)
{
    public string MessageKey { get; } = messageKey;

    public int Code { get; } = code;

    public int HttpStatusCode { get; } = httpStatusCode;

    public string Details { get; } = details;
}
