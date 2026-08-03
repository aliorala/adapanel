using AdaPanel.Infrastructure.Dns;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Uzun TXT kayitlari (DKIM RSA acik anahtari).
/// <para>
/// Canli bulgu: <c>Add-DnsServerResourceRecord -Txt</c> 255 karakteri asan degeri
/// "InputObject for resource record has an invalid value" (WIN32 203) ile REDDEDIYOR. RSA DKIM
/// anahtari ~450 karakterdir, yani RSA DKIM kaydi hicbir kurulumda yazilamiyordu. Ed25519 kaydi
/// kisa oldugu icin geciyor ve sorun yarim gorunuyordu.
/// </para>
/// </summary>
public sealed class DnsLongTxtTests
{
    private static string Key(int length) => "v=DKIM1; k=rsa; p=" + new string('A', length);

    /// <summary>Uretilen komut satirindaki "" ile sarilmis parcalari cikarir.</summary>
    private static IReadOnlyList<string> ExtractChunks(string line) =>
        System.Text.RegularExpressions.Regex.Matches(line, "\"([^\"]*?)\"")
            .Select(match => match.Groups[1].Value)
            .ToList();

    [Fact]
    public void BuildTxtLine_UsesTheProvenCmdletForShortValues()
    {
        var line = DnsService.BuildTxtLine("@", "v=spf1 mx a ~all", 3600);

        Assert.Contains("Add-DnsServerResourceRecord", line, StringComparison.Ordinal);
        Assert.DoesNotContain("dnscmd", line, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildTxtLine_SplitsValuesLongerThan255Characters()
    {
        var line = DnsService.BuildTxtLine("v1-rsa-20260626._domainkey", Key(400), 3600);

        Assert.Contains("dnscmd", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Add-DnsServerResourceRecord", line, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildTxtLine_ProducesChunksThatNeverExceedTheProtocolLimit()
    {
        var line = DnsService.BuildTxtLine("selector._domainkey", Key(700), 3600);

        // dnscmd argumanlari tek tirnakli parcalar hâlinde verilir; hicbiri 255'i asmamali.
        foreach (var chunk in ExtractChunks(line))
        {
            Assert.True(chunk.Length <= 255, $"parca cok uzun: {chunk.Length}");
        }
    }

    [Fact]
    public void BuildTxtLine_KeepsEveryCharacterOfTheOriginalValue()
    {
        var key = Key(600);
        var line = DnsService.BuildTxtLine("selector._domainkey", key, 3600);

        // Parcalar birlestirildiginde ORIJINAL deger cikmali: DKIM dogrulayicisi tam olarak bunu yapar.
        Assert.Equal(key, string.Concat(ExtractChunks(line)));
    }

    // Cikis kodu denetlenmezse kayit yazilmadigi halde "tamam" denirdi.
    [Fact]
    public void BuildTxtLine_FailsLoudlyWhenDnscmdReturnsAnError()
    {
        var line = DnsService.BuildTxtLine("selector._domainkey", Key(400), 3600);

        Assert.Contains("$adaKod", line, StringComparison.Ordinal);
        Assert.Contains("throw", line, StringComparison.Ordinal);
    }
}
