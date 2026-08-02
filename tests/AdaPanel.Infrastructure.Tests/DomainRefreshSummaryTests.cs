using AdaPanel.Core.Dtos.Health;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Tazeleme özetinin sayımı: kullanıcı "kaç alan adı eskiydi" sorusuna bu sayıdan cevap alır ve
/// "Dikkat" uyarısı da bu sayıya bakarak çıkar. Yanlış sayım ya boşuna alarm verir ya da gerçek
/// eksiği gizler; ikisi de uyarının güvenilirliğini bitirir.
/// </summary>
public sealed class DomainRefreshSummaryTests
{
    [Fact]
    public void AlreadyCurrent_IsTrue_WhenNothingWasMissing()
    {
        var result = new DomainRefreshResultDto { SiteName = "ornek.com" };

        Assert.True(result.AlreadyCurrent);
    }

    [Theory]
    [InlineData(1, 0)]  // eksik DNS kaydı eklendi
    [InlineData(0, 3)]  // DNS tamdı ama posta kayıtları (DKIM) yazıldı
    [InlineData(2, 4)]
    public void AlreadyCurrent_IsFalse_WhenSomethingWasApplied(int dnsRecords, int mailRecords)
    {
        var result = new DomainRefreshResultDto
        {
            SiteName = "ornek.com",
            AddedDnsRecords = Enumerable.Range(0, dnsRecords).Select(index => $"TXT kayit{index}").ToArray(),
            AppliedMailRecords = mailRecords
        };

        Assert.False(result.AlreadyCurrent);
    }

    // Hatalı alan adı "güncel" SAYILMAMALI: hata yüzünden hiçbir şey eklenememişken "zaten güncel"
    // demek, kullanıcıyı düzeltilmemiş bir alan adının düzeldiğine inandırırdı.
    [Fact]
    public void AlreadyCurrent_IsFalse_WhenTheDomainFailed()
    {
        var result = new DomainRefreshResultDto { SiteName = "ornek.com", Error = "zone okunamadı" };

        Assert.False(result.AlreadyCurrent);
    }
}
