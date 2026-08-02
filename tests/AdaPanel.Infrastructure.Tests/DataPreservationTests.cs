using AdaPanel.Infrastructure.Bootstrap;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Kurulum akışı, yeniden kurulumdan önce kurulum dizinini KOMPLE siler. Veritabanı motorlarının
/// veri klasörü o dizinin İÇİNDE olduğu için, bu liste bir bileşen için eksik kalırsa sürüm
/// yükseltmesi ya da onarım kurulumu o bileşenin TÜM VERİSİNİ yok eder.
///
/// Bu test canlı olarak doğrulanmış bir olayın regresyonudur: geliştirme makinesinde MariaDB kurulup
/// içine veri yazıldı, "güncelle" çalıştırıldı ve veri silindi. Düzeltmeden sonra aynı senaryoda
/// veri yerinde kaldı.
/// </summary>
public sealed class DataPreservationTests
{
    [Theory]
    [InlineData("mariadb-11")]
    [InlineData("mysql-8")]
    [InlineData("postgresql-16")]
    public void DataDirectoriesToPreserve_CoversEveryDatabaseEngine(string componentId)
    {
        Assert.Contains("data", ComponentBootstrapper.DataDirectoriesToPreserve(componentId));
    }

    // Stalwart posta kutularını data/, üretilmiş yapılandırmayı (admin parolası dahil) etc/ altında
    // tutar; ikisini de kaybetmek posta sunucusunu sıfırlar.
    [Fact]
    public void DataDirectoriesToPreserve_CoversMailServerDataAndConfiguration()
    {
        var preserved = ComponentBootstrapper.DataDirectoriesToPreserve("stalwart-mail");

        Assert.Contains("data", preserved);
        Assert.Contains("etc", preserved);
    }

    [Theory]
    [InlineData("MariaDB-11")]
    [InlineData("  mysql-8  ")]
    public void DataDirectoriesToPreserve_IsCaseAndWhitespaceInsensitive(string componentId)
    {
        Assert.NotEmpty(ComponentBootstrapper.DataDirectoriesToPreserve(componentId));
    }

    // Durum tutmayan bileşenlerde koruma YAPILMAMALI: gereksiz taşıma, kurulumun bıraktığı eski
    // dosyaların yeni sürümle karışmasına yol açar.
    [Theory]
    [InlineData("phpmyadmin")]
    [InlineData("win-acme")]
    [InlineData("nodejs-lts")]
    [InlineData("php-8.3")]
    [InlineData("bilinmeyen-bilesen")]
    public void DataDirectoriesToPreserve_IsEmpty_ForStatelessComponents(string componentId)
    {
        Assert.Empty(ComponentBootstrapper.DataDirectoriesToPreserve(componentId));
    }
}
