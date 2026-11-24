using System.Net;
using System.Text;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Migration;
using Renci.SshNet;

namespace AdaPanel.Infrastructure.Migration.Sources;

internal sealed record LiveCommandResult(int? ExitCode, string Output, bool Truncated = false)
{
    public bool CompletionConfirmed => ExitCode.HasValue;
}
internal sealed record LiveRemoteEntry(string Name, bool IsRegularFile, bool IsDirectory, bool IsSymbolicLink, long Bytes);

internal interface ILiveSshSession : IAsyncDisposable
{
    Task<LiveCommandResult> ExecuteAsync(string fixedCommand, TimeSpan timeout, CancellationToken cancellationToken);
    Task CreatePrivateDirectoryAsync(string path, CancellationToken cancellationToken);
    Task<IReadOnlyList<LiveRemoteEntry>> ListAsync(string path, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);
    Task DeleteFileAsync(string path, CancellationToken cancellationToken);
    Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken);
}
internal interface ILiveSshTransport
{
    Task<ILiveSshSession> ConnectAsync(LiveMigrationConnection connection, IPAddress inspectedAddress, CancellationToken cancellationToken);
}

internal sealed class LiveSshTransport(LiveMigrationOptions options) : ILiveSshTransport
{
    public async Task<ILiveSshSession> ConnectAsync(LiveMigrationConnection connection, IPAddress inspectedAddress, CancellationToken cancellationToken)
    {
        // Connect exclusively to the literal address inspected by OutboundAddressGuard. No second DNS lookup.
        using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(connection.PrivateKey));
        using var key = connection.PrivateKey.Length == 0 ? null : new PrivateKeyFile(keyStream, connection.PrivateKeyPassphrase);
        AuthenticationMethod Auth() => key == null
            ? new PasswordAuthenticationMethod(connection.SshUsername, connection.Password)
            : new PrivateKeyAuthenticationMethod(connection.SshUsername, key);
        ConnectionInfo Info() => new(inspectedAddress.ToString(), connection.SshPort, connection.SshUsername, Auth()) { Timeout = TimeSpan.FromSeconds(20) };
        var ssh = new SshClient(Info());
        var sftp = new SftpClient(Info()) { OperationTimeout = TimeSpan.FromSeconds(60) };
        var trust = new RemoteHostKeyTrustGate(connection.AcceptedSshFingerprint);
        ssh.HostKeyReceived += (_, e) => e.CanTrust = trust.Evaluate(e.HostKeyName, e.FingerPrintSHA256, e.KeyLength);
        sftp.HostKeyReceived += (_, e) => e.CanTrust = trust.Evaluate(e.HostKeyName, e.FingerPrintSHA256, e.KeyLength);
        try
        {
            await ssh.ConnectAsync(cancellationToken);
            await sftp.ConnectAsync(cancellationToken);
            return new Session(ssh, sftp, options.MaxCommandOutputBytes);
        }
        catch
        {
            ssh.Dispose(); sftp.Dispose();
            if (trust.Failure is { } failure)
                throw new AppException("migration.hostkey.untrusted", 409, 409, failure.Fingerprint);
            throw;
        }
    }

    private sealed class Session(SshClient ssh, SftpClient sftp, int outputLimit) : ILiveSshSession
    {
        public async Task<LiveCommandResult> ExecuteAsync(string fixedCommand, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var command = ssh.CreateCommand(fixedCommand, Encoding.UTF8);
            command.CommandTimeout = timeout;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var execution = command.ExecuteAsync(deadline.Token);
            var output = DrainAsync(command.OutputStream, outputLimit, deadline.Token);
            var error = DrainAsync(command.ExtendedOutputStream, outputLimit, deadline.Token);
            try
            {
                await execution;
                var captured = await output;
                await error; // Drain stderr concurrently, but never propagate source text into job details.
                return new(command.ExitStatus, captured.Text, captured.Truncated);
            }
            catch
            {
                deadline.Cancel();
                try { await Task.WhenAll(output, error); } catch { }
                // SSH cancellation merely requests a signal. It does not prove backup descendants stopped.
                throw;
            }
        }

        private static async Task<(string Text, bool Truncated)> DrainAsync(Stream stream, int limit, CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            using var capture = new MemoryStream();
            var truncated = false;
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (count == 0) break;
                var keep = Math.Min(count, Math.Max(0, limit - (int)capture.Length));
                if (keep > 0) capture.Write(buffer, 0, keep);
                if (keep != count) truncated = true;
            }
            return (Encoding.UTF8.GetString(capture.ToArray()), truncated);
        }

        public async Task CreatePrivateDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            await sftp.CreateDirectoryAsync(path, cancellationToken);
            sftp.ChangePermissions(path, 448); // 0700; before any backup content is created.
        }
        public async Task<IReadOnlyList<LiveRemoteEntry>> ListAsync(string path, CancellationToken cancellationToken)
        {
            var entries = new List<LiveRemoteEntry>();
            await foreach (var file in sftp.ListDirectoryAsync(path, cancellationToken))
            {
                if (file.Name is "." or "..") continue;
                if (entries.Count >= 10_000) throw new InvalidDataException("Remote workspace entry limit exceeded.");
                entries.Add(new(file.Name, file.IsRegularFile, file.IsDirectory, file.IsSymbolicLink, file.Length));
            }
            return entries;
        }
        public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(sftp.OpenRead(path));
        }
        public Task DeleteFileAsync(string path, CancellationToken cancellationToken) => sftp.DeleteFileAsync(path, cancellationToken);
        public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken) => sftp.DeleteDirectoryAsync(path, cancellationToken);
        public ValueTask DisposeAsync() { ssh.Dispose(); sftp.Dispose(); return ValueTask.CompletedTask; }
    }
}
