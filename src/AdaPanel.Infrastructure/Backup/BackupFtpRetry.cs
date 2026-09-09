using System.Net;

namespace AdaPanel.Infrastructure.Backup;

internal static class BackupFtpRetry
{
    // STOR always starts again from the same local archive and target name; no append/resume.
    internal static async Task RunAsync(Func<CancellationToken, Task> transfer, CancellationToken cancellationToken,
        TimeSpan? retryDelay = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await transfer(cancellationToken);
                return;
            }
            catch (WebException exception) when (attempt < 3 && IsTransient(exception))
            {
                exception.Response?.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay((retryDelay ?? TimeSpan.FromSeconds(1)) * attempt, cancellationToken);
            }
        }
    }

    internal static bool IsTransient(WebException exception) => exception.Response is FtpWebResponse response
        ? (int)response.StatusCode is 421 or 425 or 426 or 450 or 451 or 452
        : exception.Status is WebExceptionStatus.Timeout or WebExceptionStatus.ConnectFailure
            or WebExceptionStatus.ConnectionClosed or WebExceptionStatus.ReceiveFailure or WebExceptionStatus.SendFailure;
}

// FtpWebRequest.Timeout only bounds synchronous requests. Abort a stalled asynchronous
// request explicitly, including MKD/NLST and the final transfer acknowledgement.
internal sealed class FtpRequestDeadline : IDisposable
{
    private readonly CancellationTokenSource deadline;
    private readonly CancellationTokenRegistration registration;
    private readonly CancellationToken callerToken;

    internal FtpRequestDeadline(WebRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        callerToken = cancellationToken;
        deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        registration = deadline.Token.Register(request.Abort);
    }

    internal CancellationToken Token => deadline.Token;
    internal void Reset(TimeSpan timeout) => deadline.CancelAfter(timeout);

    internal void ThrowIfExpired()
    {
        callerToken.ThrowIfCancellationRequested();
        if (deadline.IsCancellationRequested)
            throw new WebException("FTP operation timed out.", WebExceptionStatus.Timeout);
    }

    public void Dispose()
    {
        registration.Dispose();
        deadline.Dispose();
    }
}
