using AdaPanel.Infrastructure.Automation;
using AdaPanel.Infrastructure.Bootstrap;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Sunucuyu güncel tutma politikası: neyin otomatik kurulabileceği, neyin sürümünün canlı
/// sorgulanabildiği ve hangi Windows servisinin "panele ait" sayıldığı.
/// </summary>
public sealed class UpdatePolicyTests
{
    // Veri taşıma / servis kesintisi riski taşıyanlar otomatik güncellenmemeli: bunlarda
    // yükseltme kararı (yedek al, bakım penceresi seç) yöneticinindir.
    [Theory]
    [InlineData("mysql-8")]
    [InlineData("mariadb-11")]
    [InlineData("postgresql-16")]
    [InlineData("mssql-2022")]
    [InlineData("stalwart-mail")]
    [InlineData("stalwart-cli")]
    [InlineData("docker-engine")]
    public void IsAutoUpdatable_IsFalse_ForComponentsWhereAnUpgradeCanBreakDataOrUptime(string componentId)
    {
        Assert.False(AutomationService.IsAutoUpdatable(componentId));
    }

    [Theory]
    [InlineData("phpmyadmin")]
    [InlineData("adminer")]
    [InlineData("roundcube")]
    [InlineData("win-acme")]
    [InlineData("composer")]
    [InlineData("nodejs-lts")]
    [InlineData("clamav")]
    public void IsAutoUpdatable_IsTrue_ForToolsThatCanBeReplacedSafely(string componentId)
    {
        Assert.True(AutomationService.IsAutoUpdatable(componentId));
    }

    // "Güncelleme yok" ile "kontrol edemiyorum" AYNI ŞEY DEĞİL. Sürümü manifeste sabit yazılı
    // bileşenler (PHP, veritabanları, nginx) yeni sürüm çıksa da sessizce eski kalır; panel bunu
    // güncelmiş gibi göstermemeli.
    [Theory]
    [InlineData("roundcube")]
    [InlineData("clamav")]
    [InlineData("win-acme")]
    [InlineData("adminer")]
    [InlineData("memcached")]
    [InlineData("nodejs-lts")]
    [InlineData("stalwart-mail")]
    [InlineData("stalwart-cli")]
    public void SupportsVersionCheck_IsTrue_ForComponentsWithALiveResolver(string componentId)
    {
        Assert.True(ComponentBootstrapper.SupportsVersionCheck(componentId));
    }

    // A checksum or fixed download URL alone does not establish the latest version.
    // Bilinmeyen/gelecekte eklenecek bir bileşen de varsayılan olarak "kontrol edilemiyor" sayılır,
    // yani sessizce "güncel" GÖSTERİLMEZ.
    [Theory]
    [InlineData("mailenable-standard")]
    [InlineData("composer")]
    [InlineData("phpmyadmin")]
    [InlineData("docker-engine")]
    [InlineData("gelecekteki-bilesen")]
    public void SupportsVersionCheck_IsFalse_ForComponentsWithoutAVersionFeed(string componentId)
    {
        Assert.False(ComponentBootstrapper.SupportsVersionCheck(componentId));
    }

    // Regresyon: kurulum ön kontrolü, "Ada" ile başlayan ÜÇÜNCÜ PARTİ servisleri panele aitmiş
    // gibi sayıp gerçek bir çakışmayı yutmamalı.
    [Theory]
    [InlineData("AdaPanel")]
    [InlineData("AdaMariaDB")]
    [InlineData("AdaStalwart")]
    [InlineData("AdaRedis")]
    public void IsOwnService_IsTrue_ForPanelServices(string serviceName)
    {
        Assert.True(ComponentPreflight.IsOwnService(serviceName, null));
    }

    [Theory]
    [InlineData("AdAwareService")]
    [InlineData("AdaptiveSleepService")]
    [InlineData("adaptix-agent")]
    [InlineData("MySQL80")]
    public void IsOwnService_IsFalse_ForThirdPartyServices(string serviceName)
    {
        Assert.False(ComponentPreflight.IsOwnService(serviceName, null));
    }

    [Fact]
    public void IsOwnService_IsTrue_WhenItMatchesTheComponentsOwnServiceName()
    {
        Assert.True(ComponentPreflight.IsOwnService("MySQL80", "mysql80"));
    }
}
