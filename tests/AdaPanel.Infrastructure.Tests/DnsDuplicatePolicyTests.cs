using System.Collections;
using System.Reflection;
using System.Text.Json;
using AdaPanel.Infrastructure.Dns;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class DnsDuplicatePolicyTests
{
    private static readonly Type ZoneType = typeof(DnsService).GetNestedType("DnsZoneState", BindingFlags.NonPublic)!;
    private static readonly Type RecordType = typeof(DnsService).GetNestedType("DnsRecordState", BindingFlags.NonPublic)!;
    private static object Parse(string json, Type type) => JsonSerializer.Deserialize(json, type)!;

    [Theory]
    [InlineData("v=spf1 mx ~all")]
    [InlineData("v=DMARC1; p=quarantine")]
    public void Repeated_upsert_of_identical_user_policy_keeps_one_record_and_its_id(string value)
    {
        var zone = Parse(JsonSerializer.Serialize(new { Records = new[] { new { Id = "original", Type = "TXT", Host = "@", Value = value, IsManaged = false } } }), ZoneType);
        var method = typeof(DnsService).GetMethod("ApplyRecordToState", BindingFlags.NonPublic | BindingFlags.Static)!;
        for (var i = 0; i < 3; i++)
        {
            var incoming = Parse(JsonSerializer.Serialize(new { Id = "new" + i, Type = "TXT", Host = "@", Value = value }), RecordType);
            method.Invoke(null, [zone, incoming, null]);
        }
        var records = (IList)ZoneType.GetProperty("Records")!.GetValue(zone)!;
        Assert.Single(records.Cast<object>());
        Assert.Equal("original", RecordType.GetProperty("Id")!.GetValue(records[0]));
    }

    [Fact]
    public void Historical_exact_copies_are_collapsed_but_case_sensitive_TXT_and_priorities_survive()
    {
        const string json = """
            {"Records":[
              {"Id":"a","Type":"TXT","Host":"@","Value":"token=Case","Ttl":3600},
              {"Id":"b","Type":"TXT","Host":"@","Value":"token=Case","Ttl":3600},
              {"Id":"c","Type":"TXT","Host":"@","Value":"token=case","Ttl":3600},
              {"Id":"d","Type":"MX","Host":"@","Value":"mail.test","Priority":10},
              {"Id":"e","Type":"MX","Host":"@","Value":"mail.test","Priority":20}
            ]}
            """;
        var zone = Parse(json, ZoneType);
        var sorted = (IEnumerable)typeof(DnsService).GetMethod("SortRecords", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [ZoneType.GetProperty("Records")!.GetValue(zone)])!;
        var ids = sorted.Cast<object>().Select(row => (string)RecordType.GetProperty("Id")!.GetValue(row)!).ToArray();
        Assert.Equal(4, ids.Length);
        Assert.Contains("a", ids);
        Assert.DoesNotContain("b", ids);
        Assert.Contains("c", ids);
        Assert.Contains("d", ids);
        Assert.Contains("e", ids);
    }
}
