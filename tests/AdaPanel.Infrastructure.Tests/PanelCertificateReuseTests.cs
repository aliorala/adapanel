using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class PanelCertificateReuseTests
{
    [Fact]
    public void Installer_reuses_only_a_current_certificate_with_a_private_key()
    {
        var script = ScriptTestHost.PowerShellLiteral(ScriptTestHost.ScriptPath("scripts", "initialize-panel-certificate.ps1"));
        var result = ScriptTestHost.Run($$"""
            . {{script}} -DefineFunctionsOnly
            $now = [datetime]'2026-10-29T12:00:00'
            function Cert($id, $start, $end, $key = $true, $name = 'AdaPanel Self Signed') {
                [pscustomobject]@{ Id=$id; FriendlyName=$name; NotBefore=$now.AddDays($start); NotAfter=$now.AddDays($end); HasPrivateKey=$key }
            }
            $invalid = @(
                (Cert 'expired' -100 -1),
                (Cert 'boundary' -100 0),
                (Cert 'future' 1 100),
                (Cert 'public-only' -1 100 $false),
                (Cert 'other-app' -1 100 $true 'Other app')
            )
            if ($null -ne (Select-AdaPanelReusableCertificate -Certificates $invalid -Now $now)) { throw 'Invalid certificate reused' }
            $valid = @((Cert 'short' -1 10), (Cert 'long' -1 20))
            $chosen = Select-AdaPanelReusableCertificate -Certificates ($invalid + $valid) -Now $now
            if ($chosen.Id -ne 'long') { throw 'Wrong reusable certificate selected' }
            if ($null -ne (Select-AdaPanelReusableCertificate -Certificates @() -Now $now)) { throw 'Empty store selected a certificate' }
            'PASS'
            """);
        Assert.True(result.ExitCode == 0, result.Tumu);
        Assert.Contains("PASS", result.StdOut);
    }
}
