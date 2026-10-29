using AdaPanel.Api.Controllers;
using AdaPanel.Api.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class FileUploadMultipartBoundaryTests
{
    [Theory]
    [InlineData("valid-limit.zip")]
    [InlineData("Şirket belgeleri ve güncel yedek.zip")]
    public void A_file_exactly_at_the_limit_fits_with_its_multipart_envelope(string name)
    {
        using var multipart = new MultipartFormDataContent(new string('b', 70));
        using var metadata = new LengthOnlyStream(PanelServerLimits.FileUploadMaxBytes);
        multipart.Add(new StreamContent(metadata), "file", name);
        var requestLimit = (long)typeof(FilesController).GetMethod(nameof(FilesController.UploadAsync))!
            .GetCustomAttributesData().Single(attribute => attribute.AttributeType == typeof(RequestSizeLimitAttribute))
            .ConstructorArguments[0].Value!;
        var total = multipart.Headers.ContentLength!.Value;
        Assert.True(total > PanelServerLimits.FileUploadMaxBytes);
        Assert.True(total <= requestLimit, $"A valid {metadata.Length}-byte file needs {total} multipart bytes, above request limit {requestLimit}.");
        var form = new FormOptions(); PanelServerLimits.Apply(form);
        Assert.Equal(PanelServerLimits.FileUploadMaxBytes, form.MultipartBodyLengthLimit);
        Assert.Equal(209_715_200L, form.MultipartBodyLengthLimit); // No increase to allowed file content.
    }

    private sealed class LengthOnlyStream(long length) : Stream
    {
        public override long Length => length;
        public override long Position { get; set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Metadata test must not materialize a 200 MiB file.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
