namespace AdaPanel.Infrastructure.Migration.Packages;

internal sealed class LockedPayloadStream(Stream payload, Stream gate) : Stream
{
    public override bool CanRead => payload.CanRead;
    public override bool CanSeek => payload.CanSeek;
    public override bool CanWrite => false;
    public override long Length => payload.Length;
    public override long Position { get => payload.Position; set => payload.Position = value; }
    public override void Flush() => payload.Flush();
    public override int Read(byte[] buffer, int offset, int count) => payload.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => payload.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => payload.ReadAsync(buffer, cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => payload.ReadAsync(buffer, offset, count, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => payload.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { payload.Dispose(); } finally { gate.Dispose(); } }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        try { await payload.DisposeAsync(); } finally { await gate.DisposeAsync(); }
        GC.SuppressFinalize(this);
    }
}
