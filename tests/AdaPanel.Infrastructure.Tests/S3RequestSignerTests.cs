using AdaPanel.Infrastructure.Backup;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// AWS Signature V4 imzalayıcısını AWS dokümantasyonundaki resmi test
/// vektörleriyle doğrular (Authenticating Requests, AWS Signature Version 4,
/// "Example: GET Object" ve "Example: PUT Object").
/// </summary>
public sealed class S3RequestSignerTests
{
    private const string AccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private static readonly DateTimeOffset TestDate = new(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sign_MatchesAwsDocumentedGetObjectVector()
    {
        var (authorization, amzDate, contentSha) = S3RequestSigner.Sign(
            "GET",
            new Uri("https://examplebucket.s3.amazonaws.com/test.txt"),
            AccessKey,
            SecretKey,
            "us-east-1",
            TestDate,
            S3RequestSigner.EmptyPayloadSha256,
            new Dictionary<string, string> { ["range"] = "bytes=0-9" });

        Assert.Equal("20130524T000000Z", amzDate);
        Assert.Equal(S3RequestSigner.EmptyPayloadSha256, contentSha);
        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, " +
            "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, " +
            "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41",
            authorization);
    }

    [Fact]
    public void Sign_MatchesAwsDocumentedPutObjectVector()
    {
        // "Welcome to Amazon S3." içeriğinin SHA-256 özeti (dokümandaki değer).
        const string payloadSha = "44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072";

        var (authorization, _, _) = S3RequestSigner.Sign(
            "PUT",
            new Uri("https://examplebucket.s3.amazonaws.com/test%24file.text"),
            AccessKey,
            SecretKey,
            "us-east-1",
            TestDate,
            payloadSha,
            new Dictionary<string, string>
            {
                ["date"] = "Fri, 24 May 2013 00:00:00 GMT",
                ["x-amz-storage-class"] = "REDUCED_REDUNDANCY"
            });

        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, " +
            "SignedHeaders=date;host;x-amz-content-sha256;x-amz-date;x-amz-storage-class, " +
            "Signature=98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd",
            authorization);
    }
}
