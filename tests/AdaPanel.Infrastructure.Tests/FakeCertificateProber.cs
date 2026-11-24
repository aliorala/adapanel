using AdaPanel.Core.Dtos.Cluster;
using AdaPanel.Infrastructure.Cluster;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>Ağa çıkmadan, sabit parmak izi sunan sahte sertifika okuyucu.</summary>
internal sealed class FakeCertificateProber : IClusterCertificateProber
{
    public const string Fingerprint = "SHA256:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";

    /// <summary>Sunulacak parmak izi; testler "araya giren" senaryosu için değiştirir.</summary>
    public string Presented { get; set; } = Fingerprint;

    public int Calls { get; private set; }

    public Exception? Failure { get; set; }

    public Task<ClusterCertificateProbeDto> ProbeCertificateAsync(string address, CancellationToken cancellationToken)
    {
        Calls++;
        if (Failure is not null)
        {
            throw Failure;
        }

        var https = ClusterCertificateStates.IsHttps(address);
        return Task.FromResult(new ClusterCertificateProbeDto
        {
            Address = address,
            Https = https,
            Fingerprint = https ? Presented : string.Empty,
            Subject = "CN=fixture",
            Issuer = "CN=fixture"
        });
    }
}
