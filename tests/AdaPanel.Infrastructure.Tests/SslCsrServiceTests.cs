using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ssl;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Ssl;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>CSR uretimi, imzali sertifikayi CSR anahtariyla eslestirme, anahtarin sunucuda kalmasi.</summary>
public sealed class SslCsrServiceTests : IDisposable
{
    private readonly BulutAlani area = new();
    private readonly ImportProxy import;
    private readonly SslCsrService service;

    public SslCsrServiceTests()
    {
        var proxy = DispatchProxy.Create<ISslService, ImportProxy>();
        import = (ImportProxy)(object)proxy;
        service = new SslCsrService(area.DosyaDeposu, new ReversibleProtector(), proxy);
    }

    public void Dispose() => area.Dispose();

    private static X509Certificate2 Sign(string csrPem, X509Certificate2 ca)
    {
        var request = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions, RSASignaturePadding.Pkcs1);
        var generator = X509SignatureGenerator.CreateForRSA(ca.GetRSAPrivateKey()!, RSASignaturePadding.Pkcs1);
        return request.Create(ca.SubjectName, generator, DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(90), RandomNumberGenerator.GetBytes(8));
    }

    private static X509Certificate2 NewCa()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(2));
        return new X509Certificate2(created.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.Exportable);
    }

    [Fact]
    public async Task CreatingACsrReturnsAValidRequestWithoutAnyPrivateKeyMaterial()
    {
        var dto = await service.CreateAsync(new CreateSslCsrRequest
        {
            HostName = "WWW.Example.com", AltNames = ["example.com", "www.example.com"], Organization = "Acme", Country = "tr"
        });

        Assert.Equal("www.example.com", dto.HostName);
        Assert.Equal(["example.com"], dto.AltNames);
        Assert.Contains("BEGIN CERTIFICATE REQUEST", dto.CsrPem);
        var parsed = CertificateRequest.LoadSigningRequestPem(dto.CsrPem, HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions, RSASignaturePadding.Pkcs1);
        Assert.Contains("CN=www.example.com", parsed.SubjectName.Name);
        Assert.Contains("C=TR", parsed.SubjectName.Name);

        // The private key is never part of the DTO or of any list response.
        Assert.DoesNotContain("PRIVATE", dto.CsrPem);
        var json = System.Text.Json.JsonSerializer.Serialize(await service.ListAsync());
        Assert.DoesNotContain("rivateKey", json);
        // ...but it IS stored (protected) on the server side.
        var stored = await File.ReadAllTextAsync(area.DosyaDeposu.ResolvePath(SslCsrService.StatePath));
        Assert.Contains("protectedPrivateKey", stored, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad host.example.com")]
    [InlineData("example.com,evil.com")]
    [InlineData("localhost")]
    public async Task InvalidHostNamesAreRejected(string host)
    {
        var error = await Assert.ThrowsAsync<AppException>(() => service.CreateAsync(new CreateSslCsrRequest { HostName = host }));
        Assert.Equal("ssl.csr_host_invalid", error.MessageKey);
    }

    [Fact]
    public async Task SubjectFieldsCannotInjectExtraDistinguishedNameParts()
    {
        var dto = await service.CreateAsync(new CreateSslCsrRequest { HostName = "example.com", Organization = "Acme, CN=evil.com" });
        var parsed = CertificateRequest.LoadSigningRequestPem(dto.CsrPem, HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions, RSASignaturePadding.Pkcs1);
        var commonNames = parsed.SubjectName.EnumerateRelativeDistinguishedNames()
            .Count(name => name.GetSingleElementType().Value == "2.5.4.3");
        Assert.Equal(1, commonNames);
    }

    [Fact]
    public async Task ASignedCertificateForTheCsrKeyIsImportedWithTheStoredKeyAndTheKeyIsThenDropped()
    {
        var dto = await service.CreateAsync(new CreateSslCsrRequest { HostName = "www.example.com" });
        using var ca = NewCa();
        using var signed = Sign(dto.CsrPem, ca);

        var imported = await service.ImportSignedAsync(dto.Id, new ImportSignedCertificateRequest
        {
            SiteName = "site", Base64Content = Convert.ToBase64String(signed.Export(X509ContentType.Cert))
        });

        Assert.Equal("www.example.com", import.Request!.HostName);
        Assert.Equal("site", import.Request.SiteName);
        using var pfx = new X509Certificate2(Convert.FromBase64String(import.Request.Base64Content), import.Request.Password);
        Assert.True(pfx.HasPrivateKey);
        Assert.Equal(signed.Thumbprint, pfx.Thumbprint);
        Assert.Equal(imported.Thumbprint, import.Result.Thumbprint);

        var after = Assert.Single(await service.ListAsync());
        Assert.Equal("imported", after.Status);
        var stored = await File.ReadAllTextAsync(area.DosyaDeposu.ResolvePath(SslCsrService.StatePath));
        Assert.DoesNotContain("PROTECTED:", stored);
    }

    [Fact]
    public async Task ACertificateForADifferentKeyIsRejectedAndNothingIsImported()
    {
        var dto = await service.CreateAsync(new CreateSslCsrRequest { HostName = "www.example.com" });
        var other = await service.CreateAsync(new CreateSslCsrRequest { HostName = "www.example.com" });
        using var ca = NewCa();
        using var signedForOther = Sign(other.CsrPem, ca);

        var error = await Assert.ThrowsAsync<AppException>(() => service.ImportSignedAsync(dto.Id, new ImportSignedCertificateRequest
        {
            SiteName = "site", Base64Content = Convert.ToBase64String(signedForOther.Export(X509ContentType.Cert))
        }));

        Assert.Equal("ssl.csr_certificate_mismatch", error.MessageKey);
        Assert.Null(import.Request);
        Assert.All(await service.ListAsync(), item => Assert.Equal("pending", item.Status));
    }

    [Fact]
    public async Task APemBundleWithTheChainIsAcceptedAndTheLeafIsFoundByKey()
    {
        var dto = await service.CreateAsync(new CreateSslCsrRequest { HostName = "www.example.com" });
        using var ca = NewCa();
        using var signed = Sign(dto.CsrPem, ca);
        var pem = ca.ExportCertificatePem() + signed.ExportCertificatePem();

        var failure = await Record.ExceptionAsync(() => service.ImportSignedAsync(dto.Id, new ImportSignedCertificateRequest
        {
            SiteName = "site", Base64Content = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(pem))
        }));
        Assert.True(failure is null, failure is AppException app ? app.MessageKey + ": " + app.Details : failure?.ToString());

        using var pfx = new X509Certificate2(Convert.FromBase64String(import.Request!.Base64Content), import.Request.Password);
        Assert.Equal(signed.Thumbprint, pfx.Thumbprint);
    }

    [Fact]
    public async Task AnAlreadyImportedCsrCannotBeImportedTwice()
    {
        var dto = await service.CreateAsync(new CreateSslCsrRequest { HostName = "www.example.com" });
        using var ca = NewCa();
        using var signed = Sign(dto.CsrPem, ca);
        var request = new ImportSignedCertificateRequest { SiteName = "site", Base64Content = Convert.ToBase64String(signed.Export(X509ContentType.Cert)) };
        await service.ImportSignedAsync(dto.Id, request);

        var error = await Assert.ThrowsAsync<AppException>(() => service.ImportSignedAsync(dto.Id, request));
        Assert.Equal("ssl.csr_not_pending", error.MessageKey);
    }

    [Fact]
    public async Task DeletingAPendingCsrRemovesItAndItsKey()
    {
        var dto = await service.CreateAsync(new CreateSslCsrRequest { HostName = "www.example.com" });
        await service.DeleteAsync(dto.Id);
        Assert.Empty(await service.ListAsync());
        var error = await Assert.ThrowsAsync<AppException>(() => service.DeleteAsync(dto.Id));
        Assert.Equal("ssl.csr_not_found", error.MessageKey);
    }

    private sealed class ReversibleProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "PROTECTED:" + plaintext;
        public string Unprotect(string protectedValue) => protectedValue["PROTECTED:".Length..];
    }

    public class ImportProxy : DispatchProxy
    {
        internal ImportCertificateRequest? Request;
        internal SslCertificateDto Result = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != "ImportCertificateAsync") throw new NotSupportedException(targetMethod.Name);
            Request = (ImportCertificateRequest)args![0]!;
            using var certificate = new X509Certificate2(Convert.FromBase64String(Request.Base64Content), Request.Password);
            Result = new SslCertificateDto { SiteName = Request.SiteName, HostName = Request.HostName, Thumbprint = certificate.Thumbprint };
            return Task.FromResult(Result);
        }
    }
}
