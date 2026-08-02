using AdaPanel.Infrastructure.Monitoring;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Aynı sitenin iki veritabanı olabilir ve bunlardan biri BAŞKA bir sunucudaki uygulama tarafından
/// kullanılıyor olabilir. Site bağı yerel görünse de yükü üreten burası değildir; ayrımı yapan tek
/// gözlem istemci adresidir. Yanlış sınıflandırma yöneticiyi yanlış sunucuda hata aratır.
/// </summary>
public sealed class RemoteClientDetectionTests
{
    [Theory]
    [InlineData("localhost:51344")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1:3306")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("[::1]:51344")]
    [InlineData("")]                 // PostgreSQL yerel soket bağlantısında boş döner
    [InlineData("   ")]
    public void IsRemoteClientHost_IsFalse_ForLocalConnections(string clientHost)
    {
        Assert.False(ResourceAttributionService.IsRemoteClientHost(clientHost));
    }

    [Theory]
    [InlineData("185.7.242.225:51344")]
    [InlineData("185.7.242.225")]
    [InlineData("10.0.0.5:3306")]
    [InlineData("[2001:db8::10]:5432")]
    [InlineData("2001:db8::10")]
    public void IsRemoteClientHost_IsTrue_ForRealRemoteAddresses(string clientHost)
    {
        Assert.True(ResourceAttributionService.IsRemoteClientHost(clientHost));
    }

    // ÖNEMLİ: burada eskiden "IP olarak ayrıştırılamayan değer uzak SAYILMAZ" kuralı vardı ve bu kural
    // ekranın varlık nedenini ortadan kaldırıyordu. MySQL'de ad çözümlemesi (skip-name-resolve) varsayılan
    // olarak KAPALI DEĞİLDİR; başka bir sunucudan gelen istemci PROCESSLIST'te IP ile değil ADIYLA
    // ("web02.musteri.com:51234") görünür. Yani asıl işaretlenmesi gereken satırlar — sitesi dışarıda,
    // veritabanı bizde olan bağlantılar — hiçbir zaman işaretlenmiyordu. Canlı testte doğrulandı.
    [Theory]
    [InlineData("web-sunucu-2")]
    [InlineData("ornek.com:3306")]
    [InlineData("web02.musteri.com:51234")]
    public void IsRemoteClientHost_IsTrue_ForHostNamesOfOtherMachines(string clientHost)
    {
        Assert.True(ResourceAttributionService.IsRemoteClientHost(clientHost));
    }

    // Sunucunun KENDİ adı (düz ve FQDN) yereldir: MySQL, yerel TCP bağlantısını çözümleyip makinenin
    // kendi adıyla raporlar. Bu ayrım olmazsa panelin kendi bağlantıları "dış bağlantı" görünürdü.
    [Fact]
    public void IsRemoteClientHost_IsFalse_ForThisServersOwnName()
    {
        Assert.False(ResourceAttributionService.IsRemoteClientHost(Environment.MachineName));
        Assert.False(ResourceAttributionService.IsRemoteClientHost($"{Environment.MachineName}:51344"));
        Assert.False(ResourceAttributionService.IsRemoteClientHost($"{Environment.MachineName}.mshome.net:51344"));
    }
}
