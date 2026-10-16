using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Mail;

internal static class MailCertificateValidation
{
    internal static void Validate(string host, string certificatePem, string privateKeyPem) =>
        Validate(host, certificatePem, privateKeyPem, null);

    // Tests supply an isolated trust root in memory; production always uses the system trust store.
    internal static void Validate(string host, string certificatePem, string privateKeyPem,
        Action<X509ChainPolicy>? configureTrust)
    {
        var supplied = new X509Certificate2Collection();
        try
        {
            using var leaf = X509Certificate2.CreateFromPem(certificatePem, privateKeyPem);
            if (!leaf.MatchesHostname(host, true, false) || leaf.NotBefore.ToUniversalTime() > DateTime.UtcNow
                || leaf.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
                throw new CryptographicException("Certificate name or validity does not match.");
            supplied.ImportFromPem(certificatePem);
            using var chain = new X509Chain();
            chain.ChainPolicy.ExtraStore.AddRange(supplied);
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(5);
            configureTrust?.Invoke(chain.ChainPolicy);
            if (!chain.Build(leaf))
                throw new CryptographicException("Certificate chain is not trusted for server authentication.");
            // Windows can fetch/cache a missing intermediate. That must not hide an incomplete
            // chain which would still break clients after it is sent to Stalwart.
            if (chain.ChainElements.Cast<X509ChainElement>().SkipLast(1).Any(element =>
                !supplied.Cast<X509Certificate2>().Any(cert => cert.Thumbprint == element.Certificate.Thumbprint)))
                throw new CryptographicException("Certificate chain is incomplete.");
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException)
        {
            // Do not include PEM/key material or provider exception details in the response.
            throw new AppException("mail.certificate_invalid", 409, 409, host);
        }
        finally { foreach (var certificate in supplied) certificate.Dispose(); }
    }
}
