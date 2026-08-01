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

    // Adres olarak ayrıştırılamayan bir değer (makine adı, çöp) "uzak" sayılmamalı: emin olmadığımız
    // bir şeyi "dışarıdan bağlanıyor" diye etiketlemek yanlış yönlendirir.
    [Theory]
    [InlineData("web-sunucu-2")]
    [InlineData("ornek.com:3306")]
    [InlineData("bozuk-veri")]
    public void IsRemoteClientHost_IsFalse_WhenTheValueIsNotAnIpAddress(string clientHost)
    {
        Assert.False(ResourceAttributionService.IsRemoteClientHost(clientHost));
    }
}
