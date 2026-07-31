using AdaPanel.Core.Dtos.Sites;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Yeni bir bölgenin varsayılan kayıt seti. Panel kendi mail sunucusunu ve webmail'ini kuruyor;
/// bölgede MX/SPF/DMARC ve mail/webmail A kayıtları YOKSA o alan adına posta hiç ulaşmaz, giden
/// posta spam'e düşer, webmail adresi çözülmez. (Karşılaştırma tabanı: Plesk'in ürettiği bölge.)
/// </summary>
public sealed class DnsDefaultRecordsTests
{
    private static readonly string[] RequiredHostsForWeb = ["@", "ns1", "ns2", "www", "ftp"];
    private static readonly string[] RequiredHostsForMail = ["mail", "webmail"];

    [Fact]
    public void BuiltInTemplate_CoversWebAndMailRecords()
    {
        var template = ReadBuiltInTemplate();

        foreach (var host in RequiredHostsForWeb.Concat(RequiredHostsForMail))
        {
            Assert.Contains(template.Records, record =>
                string.Equals(record.Host, host, StringComparison.OrdinalIgnoreCase));
        }

        Assert.Contains(template.Records, record => record.Type == "MX");
        Assert.Contains(template.Records, record => record.Type == "TXT" && record.Value.StartsWith("v=spf1", StringComparison.Ordinal));
        Assert.Contains(template.Records, record => record.Host == "_dmarc" && record.Value.StartsWith("v=DMARC1", StringComparison.Ordinal));
    }

    [Fact]
    public void BuiltInTemplate_GivesTheMxRecordAPriority()
    {
        var mx = ReadBuiltInTemplate().Records.Single(record => record.Type == "MX");

        Assert.Equal("mail.{domain}", mx.Value);
        Assert.Equal(10, mx.Priority);
    }

    // Yer tutucular genişletilebilir olmalı; {ip} çözülemezse A kayıtları atlanır ama MX/DMARC kalır.
    [Fact]
    public void BuiltInTemplate_UsesOnlyKnownPlaceholders()
    {
        var known = new[] { "{domain}", "{ip}", "{ns1}", "{ns2}" };

        foreach (var record in ReadBuiltInTemplate().Records)
        {
            var text = record.Host + " " + record.Value;
            var start = text.IndexOf('{');
            while (start >= 0)
            {
                var end = text.IndexOf('}', start);
                Assert.True(end > start, $"Kapanmamış yer tutucu: {text}");
                Assert.Contains(text[start..(end + 1)], known);
                start = text.IndexOf('{', end);
            }
        }
    }

    private static DnsTemplateDto ReadBuiltInTemplate()
    {
        var method = typeof(AdaPanel.Infrastructure.Dns.DnsService)
            .GetMethod("BuiltInTemplate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (DnsTemplateDto)method.Invoke(null, null)!;
    }
}
