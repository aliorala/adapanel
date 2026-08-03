using AdaPanel.Infrastructure.Dns;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Bölge senkron scriptinin dayanıklılığı.
/// <para>
/// Canlı olay: script tek bir PowerShell çalıştırmasıdır ve başında
/// <c>$ErrorActionPreference='Stop'</c> vardır. Önce TÜM gruplar silinip sonra tek tek yeniden
/// eklendiği için, ortadaki bir kayıt patladığında script komple duruyordu — o noktadan sonraki
/// hiçbir kayıt yazılmıyor, başta silinenler de silinmiş kalıyordu. 255 karakteri aşan bir DKIM
/// kaydı yüzünden iki alan adının bölgesinde <c>_dmarc</c> dahil hiçbir şey kalmadı; panel ise
/// "onarıldı" diyordu.
/// </para>
/// </summary>
public sealed class DnsSyncResilienceTests
{
    // Uzun TXT artik dnscmd'e DUSMEZ: cmdlet'e tek metin verilir, parcalar CRLF ile ayrilir.
    [Fact]
    public void BuildTxtLine_LongValue_UsesTheCmdletWithSeparatedChunks()
    {
        var line = DnsService.BuildTxtLine("selector._domainkey", "v=DKIM1; p=" + new string('A', 400), 3600);

        Assert.Contains("Add-DnsServerResourceRecord", line, StringComparison.Ordinal);
        Assert.Contains("[char]13 + [char]10", line, StringComparison.Ordinal);
    }

    // Kisa deger kanitlanmis cmdlet yolunu kullanmaya devam etmeli.
    [Fact]
    public void BuildTxtLine_ShortValue_UsesTheCmdlet()
    {
        var line = DnsService.BuildTxtLine("@", "v=spf1 mx a ~all", 3600);

        Assert.Contains("Add-DnsServerResourceRecord", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TXT", "v=spf1 mx a ip4:192.0.2.1 ~all", "v=spf1")]
    [InlineData("TXT", "v=DMARC1; p=quarantine", "v=DMARC1")]
    [InlineData("TXT", "  v=spf1 a ~all", "v=spf1")]
    public void SingletonPolicies_AreRecognised(string type, string value, string expected)
    {
        Assert.Equal(expected, InvokeResolve(type, value));
    }

    // DKIM tek ornekli DEGILDIR: her secici icin ayri kayit gerekir.
    [Theory]
    [InlineData("TXT", "v=DKIM1; k=rsa; p=AAA")]
    [InlineData("A", "192.0.2.1")]
    [InlineData("MX", "mail.ornek.test")]
    public void NonPolicyRecords_AreNotTreatedAsSingletons(string type, string value)
    {
        Assert.Null(InvokeResolve(type, value));
    }

    private static string? InvokeResolve(string type, string value)
    {
        var method = typeof(DnsService).GetMethod(
            "ResolveSingletonTxtPrefix",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        return (string?)method!.Invoke(null, [type, value]);
    }
}
