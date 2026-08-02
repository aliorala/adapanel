using System.Text.Json;
using AdaPanel.Infrastructure.Mail;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Posta kutusu kotasının Stalwart şemasındaki yeri <c>quotas</c> haritasının <c>maxDiskQuota</c>
/// anahtarıdır. Üst düzeyde <c>quota</c> diye bir alan YOKTUR.
///
/// Bu, canlı olarak doğrulanmış bir olayın regresyonudur: panel hem yazarken hem okurken üst düzey
/// <c>quota</c> adını kullanıyordu. Yazma her seferinde sunucudan "unknown field: quota" alıyor ama
/// hata yutulduğu için panel "tamam" diyordu; okuma da her kutuyu kotasız gösteriyordu. Sonuç:
/// yönetici 512 MB kota koyduğunu sanırken posta kutusu SINIRSIZ kalıyordu.
/// </summary>
public sealed class MailAccountQuotaTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ReadAccountQuotaBytes_ReadsTheQuotaFromTheQuotasMap()
    {
        var account = Parse("""
            {"emailAddress":"deneme@test.local","quotas":{"maxDiskQuota":536870912},"usedDiskQuota":0}
            """);

        Assert.Equal(536870912L, MailService.ReadAccountQuotaBytes(account));
    }

    // Kotasız hesap: harita boş ya da anahtar yok → 0 (sınırsız). Burada hata atmak, tek kotasız
    // kutu yüzünden bütün posta listesini düşürürdü.
    [Theory]
    [InlineData("""{"emailAddress":"a@b.c","quotas":{}}""")]
    [InlineData("""{"emailAddress":"a@b.c","quotas":{"maxEmails":1000}}""")]
    [InlineData("""{"emailAddress":"a@b.c"}""")]
    [InlineData("""{"emailAddress":"a@b.c","quotas":null}""")]
    public void ReadAccountQuotaBytes_IsZero_WhenNoDiskQuotaIsSet(string json)
    {
        Assert.Equal(0L, MailService.ReadAccountQuotaBytes(Parse(json)));
    }

    // ESKİ hatalı şema: üst düzey "quota". Bu değer OKUNMAMALI — okunursa hatalı yazma yolu da
    // fark edilmeden geri gelebilir.
    [Fact]
    public void ReadAccountQuotaBytes_IgnoresTheObsoleteTopLevelField()
    {
        var account = Parse("""{"emailAddress":"a@b.c","quota":536870912}""");

        Assert.Equal(0L, MailService.ReadAccountQuotaBytes(account));
    }
}
