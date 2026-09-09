using System.Net;

namespace AdaPanel.Infrastructure.Backup;

internal static class BackupFtpRetry
{
    // STOR always starts again from the same local archive and target name; no append/resume.
    internal static async Task RunAsync(Func<CancellationToken, Task> transfer, CancellationToken cancellationToken,
        TimeSpan? retryDelay = null)
        => await RunAsync(async token => { await transfer(token); return true; }, cancellationToken, retryDelay: retryDelay);

    internal static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken, Func<T, bool>? retryResult = null, TimeSpan? retryDelay = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await operation(cancellationToken);
                if (attempt == 3 || retryResult?.Invoke(result) != true) { return result; }
            }
            catch (WebException exception) when (attempt < 3 && IsTransient(exception))
            {
                exception.Response?.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            await Task.Delay((retryDelay ?? TimeSpan.FromSeconds(1)) * attempt, cancellationToken);
        }
    }

    internal static bool IsTransientCode(int code) => code is 421 or 425 or 426 or 450 or 451 or 452;

    internal static bool IsTransient(WebException exception) => exception.Response is FtpWebResponse response
        ? IsTransientCode((int)response.StatusCode)
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
