using AdaPanel.Core.Dtos.Sites;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// "Mevcut IIS sitelerini devral" kaydı, varsayılanların uygulandığını İDDİA ETMEZ; ölçülen
/// sonucu yazar ve eksikleri SİTE ADI + ADIM ADIYLA söyler.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN AÇIK.</b> Uç her site için <c>EnsureDefaultsAsync</c> çağırıp sonucu <c>_ =</c> ile
/// atıyor, sonra kayda koşulsuz <i>"N existing IIS site(s) imported and defaults applied"</i>
/// yazıyordu. <see cref="SiteDefaultsResultDto.FailedFeatures"/> başarısız adımları ZATEN adıyla
/// döndürüyor — servis doğruyu söylüyor, çağıran taraf duymuyordu.
/// </para>
/// <para>
/// <b>Bedeli:</b> atılan adımlar arasında <c>security</c> (kiracı yalıtımı + klasör ACL'i) var.
/// Uygulanmadığında devralınan site komşusunun <c>wp-config.php</c>'sini okuyabilir; yönetici ise
/// kayda bakıp yalıtımın yerinde olduğunu sanar. <c>dns</c> adımı da aynı sınıfta: bölge hiç
/// yayınlanmadığı hâlde "uygulandı" yazılıyordu.
/// </para>
/// </remarks>
public sealed class SiteDevralmaKaydiDurustlukTests
{
    // ------------------------------------------------------------------------------------------
    // FAZLA KAPATMA NÖBETÇİSİ: her şey yolundayken kayıt SADE kalmalı.
    //
    // "Her devralmaya uyarı yaz" çözümü aşağıdaki testleri geçerdi ve gerçek eksiği olan
    // kayıtları görünmez kılardı.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public void Hepsi_tamamsa_kayit_SADE_kalir()
    {
        var metin = SiteImportAudit.Describe(
        [
            new SiteImportAudit.Outcome("a.test", []),
            new SiteImportAudit.Outcome("b.test", [])
        ]);

        Assert.Contains("2 existing IIS site(s) imported: a.test, b.test", metin, StringComparison.Ordinal);
        Assert.Contains("defaults applied to all", metin, StringComparison.Ordinal);
        Assert.DoesNotContain("INCOMPLETE", metin, StringComparison.Ordinal);
    }

    /// <summary>
    /// Eksik kalan adım varsa "uygulandı" YAZILMAZ; hangi sitede hangi adımın eksik kaldığı yazılır.
    /// </summary>
    [Fact]
    public void EKSIK_kalan_adim_SITE_ve_ADIM_adiyla_yazilir()
    {
        var metin = SiteImportAudit.Describe(
        [
            new SiteImportAudit.Outcome("a.test", []),
            new SiteImportAudit.Outcome("b.test", ["dns", "security"])
        ]);

        Assert.Contains("defaults INCOMPLETE on 1 of 2", metin, StringComparison.Ordinal);
        Assert.Contains("b.test (dns, security)", metin, StringComparison.Ordinal);

        // "defaults applied to all" ifadesi ARTIK GECMEMELI: kapatilan yalanin tam kelimeleri.
        Assert.DoesNotContain("defaults applied to all", metin, StringComparison.Ordinal);

        // TAMAM olan site eksikler bolumunde GORUNMEMELI: yoneticiyi saglam bir siteyi
        // kovalamaya gondermek, sayiyi yanlis yazmak kadar zararlidir.
        var uyari = metin[metin.IndexOf("INCOMPLETE", StringComparison.Ordinal)..];
        Assert.DoesNotContain("a.test", uyari, StringComparison.Ordinal);
    }

    /// <summary>Devralınacak site yoksa uydurma bir başarı cümlesi kurulmaz.</summary>
    [Fact]
    public void Devralinacak_site_yoksa_bunu_SOYLER()
    {
        Assert.Equal("No unmanaged IIS site was found to import.", SiteImportAudit.Describe([]));
    }
}
