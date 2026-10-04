using System.Text;
using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Files;

/// <summary>The editor only publishes text that can be encoded back without guessing its codec.</summary>
internal sealed record FileTextContent(string Content, Encoding? Encoding, long OriginalLength, string Reason)
{
    internal const int PreviewLimit = 1024 * 1024;
    internal static FileTextContent NewFile { get; } = new(string.Empty, new UTF8Encoding(false, true), 0, string.Empty);
    internal bool Loaded => Encoding is not null;

    internal static async Task<FileTextContent> ReadAsync(Stream input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var length = input.Length;
        if (length > PreviewLimit) return new(string.Empty, null, length, "files.content_too_large");
        var bytes = new byte[checked((int)length)];
        await input.ReadExactlyAsync(bytes, token);
        token.ThrowIfCancellationRequested();
        return Decode(bytes);
    }

    private static FileTextContent Decode(byte[] bytes)
    {
        ReadOnlySpan<byte> input = bytes;
        Encoding codec;
        int prefix;
        if (input.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { codec = new UTF32Encoding(false, true, true); prefix = 4; }
        else if (input.StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { codec = new UTF32Encoding(true, true, true); prefix = 4; }
        else if (input.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) { codec = new UTF8Encoding(true, true); prefix = 3; }
        else if (input.StartsWith(new byte[] { 0xff, 0xfe })) { codec = new UnicodeEncoding(false, true, true); prefix = 2; }
        else if (input.StartsWith(new byte[] { 0xfe, 0xff })) { codec = new UnicodeEncoding(true, true, true); prefix = 2; }
        else { codec = new UTF8Encoding(false, true); prefix = 0; }
        try
        {
            var text = codec.GetString(input[prefix..]);
            if (IsText(text)) return new(text, codec, bytes.LongLength, string.Empty);
        }
        catch (DecoderFallbackException) { }
        return new(string.Empty, null, bytes.LongLength, "files.content_not_editable");
    }

    internal byte[] Encode(string content)
    {
        if (Encoding is null)
            throw new AppException(Reason, Reason == "files.content_too_large" ? 413 : 409,
                Reason == "files.content_too_large" ? 413 : 409);
        if (!IsText(content)) throw new AppException("files.content_not_editable", 409, 409);
        try { return [.. Encoding.GetPreamble(), .. Encoding.GetBytes(content)]; }
        catch (EncoderFallbackException) { throw new AppException("files.content_not_editable", 409, 409); }
    }

    private static bool IsText(string text) => !text.Any(value => char.IsControl(value) && value is not ('\r' or '\n' or '\t'));
}
