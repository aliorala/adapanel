using AdaPanel.Core.Dtos.Migration;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// GÖÇ KEŞFİNDE BİR ALAN İKİ ANLAMA GELMEZ.
/// <para>
/// ÖLÇÜLEN HATA: <c>PleskDomainDto.HostingType</c> tek alandı ve iki keşif yolu onu FARKLI
/// şeylerle dolduruyordu — <c>DiscoverPleskAsync</c> Plesk'in <c>hosting_type</c> değerini
/// ("virtual"), <c>DiscoverCpanelAsync</c> ise WHM hesabının KULLANICI ADINI ("ali_shop").
/// Arayüz alanı etiketsiz, ham basıyordu; yani ekranda yan yana duran iki satırın hangisinin
/// ne olduğunu anlamanın YOLU YOKTU.
/// </para>
/// <para>
/// Kozmetikten fazlası: cPanel uzak yol şablonu <c>/home/{user}/public_html</c> ve
/// <c>{user}</c> bu alandan dolar. Aynı alanı Plesk'te de <c>{user}</c> yerine geçiren kod,
/// <c>{user}</c> yazan bir Plesk şablonuna <c>/home/virtual/…</c> üretirdi.
/// </para>
/// <para>
/// Bu test alanların BİRLEŞMESİNİ engeller: her keşif yolu YALNIZ kendi alanını doldurmalı.
/// </para>
/// </summary>
public sealed class DiscoveryFieldMeaningTests
{
    [Fact]
    public void PleskDomainDto_iki_anlami_iki_AYRI_alanda_tasir()
    {
        var tip = typeof(PleskDomainDto);

        Assert.NotNull(tip.GetProperty(nameof(PleskDomainDto.HostingType)));
        Assert.NotNull(tip.GetProperty(nameof(PleskDomainDto.AccountUser)));
    }

    [Fact]
    public void PleskDomainDto_alanlari_varsayilan_olarak_BOS_gelir()
    {
        // "Doldurulmadı" ile "boş ölçüldü" ekranda aynı görünür; ikisi de gösterilmemeli.
        // (Şablon `v-if` ile boş alanı hiç çizmiyor; bu test o varsayımın tabanını sabitler.)
        var dto = new PleskDomainDto();

        Assert.Equal(string.Empty, dto.HostingType);
        Assert.Equal(string.Empty, dto.AccountUser);
    }

    [Fact]
    public void cPanel_kesfi_kullanici_adini_AccountUser_alanina_yazar_HostingType_a_DEGIL()
    {
        var govde = MetotGovdesi("DiscoverCpanelAsync");

        // WHM `listaccts` yanıtındaki `user` alanı = hesabın sistem kullanıcı adı.
        Assert.Contains("AccountUser = account.TryGetProperty(\"user\"", govde);

        // Geriye dönüş nöbeti: bu yol barındırma türü ÖLÇMEZ, dolayısıyla o alana hiçbir şey
        // yazamaz. Yazdığı gün ekran yine "virtual" ile "ali_shop"u aynı yerde gösterir.
        Assert.DoesNotContain("HostingType =", govde);
    }

    [Fact]
    public void Plesk_kesfi_barindirma_turunu_HostingType_alanina_yazar_AccountUser_a_DEGIL()
    {
        var govde = MetotGovdesi("DiscoverPleskAsync");

        Assert.Contains("HostingType = element.TryGetProperty(\"hosting_type\"", govde);

        // Plesk REST yanıtında hesabın sistem kullanıcı adı YOK; ölçülmeyen şey doldurulmaz.
        Assert.DoesNotContain("AccountUser =", govde);
    }

    /// <summary>
    /// Adı verilen metodun gövdesi: imzasından, BİR SONRAKİ <c>public</c> üye bildirimine kadar.
    /// Bütün dosyada arama yapmak bu testi işe yaramaz kılardı — iki metot da aynı dosyada ve
    /// aradığımız şey tam olarak "hangi metot hangi alanı yazıyor".
    /// </summary>
    private static string MetotGovdesi(string metotAdi)
    {
        var kaynak = File.ReadAllText(Path.Combine(
            BulKok(), "src", "AdaPanel.Infrastructure", "Migration", "SiteMigrationService.cs"));

        var bas = kaynak.IndexOf($"public async Task<PleskDiscoverResultDto> {metotAdi}(", StringComparison.Ordinal);
        Assert.True(bas >= 0, $"{metotAdi} kaynakta bulunamadi; tarama bozulmus olabilir.");

        var son = kaynak.IndexOf("\n    public ", bas + 1, StringComparison.Ordinal);
        if (son < 0)
        {
            son = kaynak.Length;
        }

        return kaynak[bas..son];
    }

    private static string BulKok()
    {
        var klasor = AppContext.BaseDirectory;
        for (var derinlik = 0; derinlik < 8; derinlik++)
        {
            if (File.Exists(Path.Combine(klasor, "AdaPanel.sln")))
            {
                return Path.GetFullPath(klasor);
            }

            klasor = Path.Combine(klasor, "..");
        }

        throw new DirectoryNotFoundException("Depo koku bulunamadi.");
    }
}
