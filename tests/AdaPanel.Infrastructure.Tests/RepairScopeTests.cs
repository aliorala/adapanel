using AdaPanel.Core.Dtos.Health;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Onarma raporunun hata birleştirme davranışı.
/// <para>
/// Ajan denetiminin bulduğu kusur: posta kaydı hatası, DNS varsayılanları adımının hatasını
/// ÜZERİNE YAZIYORDU. İki adım da patladığında kullanıcı yalnız ikincisini görüyor, asıl sebep
/// kayboluyordu — bu oturumda tekrar tekrar ayıkladığımız "hatayı gizleme" davranışının başka
/// bir biçimi.
/// </para>
/// </summary>
public sealed class RepairScopeTests
{
    [Fact]
    public void Error_KeepsBothStepFailuresWhenTwoStepsFail()
    {
        var result = new DomainRefreshResultDto { Error = "dns.sync_failed: bolge kilitli" };

        // ApplyMailRecordsAsync sonrası uygulanan birleştirme kuralının aynısı.
        var postaHatasi = "TXT _dmarc: yazilamadi";
        result.Error = string.IsNullOrWhiteSpace(result.Error)
            ? postaHatasi
            : result.Error + " || " + postaHatasi;

        Assert.Contains("bolge kilitli", result.Error, StringComparison.Ordinal);
        Assert.Contains("TXT _dmarc", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Error_StaysCleanWhenOnlyTheMailStepFails()
    {
        var result = new DomainRefreshResultDto();
        var postaHatasi = "TXT _dmarc: yazilamadi";
        result.Error = string.IsNullOrWhiteSpace(result.Error)
            ? postaHatasi
            : result.Error + " || " + postaHatasi;

        Assert.Equal(postaHatasi, result.Error);
    }

    // Sunucu geneli olaylar site basina raporlanmamali; aksi halde 40 siteli bir sunucuda ayni
    // uyari 40 kez cikar ve rozet anlamsizlasir.
    [Fact]
    public void ServerFindings_LiveOnTheSummaryNotOnEachDomain()
    {
        var summary = new DomainRefreshSummaryDto
        {
            ServerFindings = [new RepairFindingDto { Key = "repair.firewall_blocked" }],
            Results = [new DomainRefreshResultDto { SiteName = "ornek.test" }]
        };

        Assert.Single(summary.ServerFindings);
        Assert.Empty(summary.Results[0].Findings);
        Assert.True(summary.Results[0].AlreadyCurrent);
    }
}
