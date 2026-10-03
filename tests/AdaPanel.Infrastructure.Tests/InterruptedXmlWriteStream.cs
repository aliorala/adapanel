namespace AdaPanel.Infrastructure.Tests;

// The production XDocument serializer writes to a real temporary test file. Only the
// second half of a write is interrupted; no XML operation or publication is mocked.
internal sealed class InterruptedXmlWriteStream(Stream inner, bool canceled) : Stream
{
    public int BytesWritten { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (BytesWritten == 0 && buffer.Length > 0)
        {
            var size = Math.Min(17, buffer.Length);
            inner.Write(buffer[..size]);
            inner.Flush();
            BytesWritten += size;
        }
        if (canceled) throw new OperationCanceledException("fixture interrupted XML serialization");
        throw new IOException("fixture interrupted XML serialization");
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
