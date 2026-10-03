namespace AdaPanel.Infrastructure.Backup;

/// <summary>Tracks a manual caller's failed approval without changing the original HTTP exception.</summary>
internal sealed class BackupExecutionApproval(Func<CancellationToken, Task> approve)
{
    private Exception? rejection;

    public async Task CheckAsync(CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            await approve(token);
        }
        catch (Exception error)
        {
            rejection = error;
            throw;
        }
    }

    // Only an approval failure bypasses the existing partial-backup reporting policy.
    // A provider's unrelated authentication or I/O failure keeps its normal outcome.
    public bool Rejected(Exception error) => ReferenceEquals(rejection, error);
}
