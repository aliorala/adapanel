using AdaPanel.Infrastructure.Bootstrap;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// Y2 — "ÖLÇEMEDİM" ile "KURULU DEĞİL" ayrımı.
/// </summary>
/// <remarks>
/// <para>
/// Eski akış: <c>ComponentBootstrapper.SynchronizeDetectedComponentsAsync</c> keşifte görünmeyen her
/// kaydı SİLİYORDU. Keşfin bütün okuyucuları (registry görünümleri, servis kovanı, PATH,
/// applicationHost.config) hataya karşı BOŞ liste döndüğü için "registry okunamadı" ile "kurulu
/// değil" aynı sonucu üretiyordu. Silinen kayıttaki <c>ProtectedConfigurationJson</c> veritabanı
/// root parolasını + admin kullanıcıyı + portu taşır ve yeniden keşifte GERİ GELMEZ. Bu yol Yazılım
/// ekranının her açılışında ve günlük güncelleme kontrolünde çalışıyordu.
/// </para>
/// <para>
/// Artık yokluğa güvenilip güvenilemeyeceği tek bir yerde, açıkça cevaplanır.
/// </para>
/// </remarks>
public sealed class ComponentDiscoveryHonestyTests
{
    [Fact]
    public void Olcum_eksiksizse_yoklugu_kanit_sayilir()
    {
        var sonuc = new ComponentDiscoveryResult([], [], []);

        Assert.True(sonuc.CanTrustAbsence("mariadb-11"));
    }

    [Fact]
    public void Tek_bir_okuyucu_bile_patladiysa_HICBIR_kaydin_yoklugu_kanit_degildir()
    {
        var sonuc = new ComponentDiscoveryResult([], ["services:enumerate"], []);

        Assert.False(sonuc.CanTrustAbsence("mariadb-11"));
        Assert.False(sonuc.CanTrustAbsence("stalwart-mail"));
    }

    [Fact]
    public void Eslestirmesi_patlayan_bilesenin_yoklugu_kanit_degildir()
    {
        var sonuc = new ComponentDiscoveryResult([], [], ["mariadb-11"]);

        Assert.False(sonuc.CanTrustAbsence("mariadb-11"));
        Assert.False(sonuc.CanTrustAbsence("MARIADB-11"));   // bileşen kimlikleri büyük/küçük harf duyarsız
        Assert.True(sonuc.CanTrustAbsence("mysql-8"));       // diğerleri ölçülebildi
    }

    /// <summary>
    /// Windows dışında hiçbir ölçüm yapılamaz; bu "kurulu değil" DEĞİLDİR.
    /// (Keşif zaten boş liste döner — ama artık bunun neden boş olduğu kayda geçer.)
    /// </summary>
    [Fact]
    public void Olcum_yapilamayan_platformda_yokluk_kanit_degildir()
    {
        var sonuc = new ComponentDiscoveryResult([], ["platform"], []);

        Assert.False(sonuc.CanTrustAbsence("php-8.3"));
    }
}
