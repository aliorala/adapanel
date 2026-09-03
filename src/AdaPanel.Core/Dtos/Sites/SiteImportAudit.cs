namespace AdaPanel.Core.Dtos.Sites;

/// <summary>
/// "Mevcut IIS sitelerini devral" işleminin denetim kaydı cümlesi — varsayılanların UYGULANDIĞI
/// İDDİA EDİLMEZ, ÖLÇÜLEN sonuç yazılır.
/// </summary>
/// <remarks>
/// <para>
/// <b>ÖLÇÜLEN AÇIK.</b> <c>SitesController.ImportExistingSitesAsync</c> her site için
/// <c>EnsureDefaultsAsync</c> çağırıp sonucu <c>_ =</c> ile ISKARTAYA ÇIKARIYOR, sonra kayda
/// koşulsuz <i>"N existing IIS site(s) imported <b>and defaults applied</b>"</i> yazıyordu.
/// <see cref="SiteDefaultsResultDto"/> ise başarısız adımları
/// (<see cref="SiteDefaultsResultDto.FailedFeatures"/>) ZATEN ADIYLA döndürüyor: servis doğruyu
/// söylüyor, çağıran taraf duymuyordu.
/// </para>
/// <para>
/// <b>Atılan bilgi önemsiz değil.</b> Adımlar arasında <c>dns</c> (bölge hiç yayınlanmadı),
/// <c>security</c> (kiracı yalıtımı ve klasör ACL'i uygulanmadı — yani devralınan site komşusunun
/// <c>wp-config.php</c>'sini, dolayısıyla veritabanı parolasını okuyabilir), <c>ssl</c>,
/// <c>ftp</c>, <c>aspnet</c>, <c>runtime</c> ve <c>proxy</c> var. Bu uç, IIS'te ELDE kurulmuş
/// siteleri panelin yönetimine alır ve dokunduğu her siteyi sunucuda değiştirir; yönetici
/// "devraldım, varsayılanlar uygulandı" kaydına bakıp yalıtımın yerinde olduğunu SANIR.
/// </para>
/// <para>
/// <b>Kardeş yol zaten doğruyu söylüyordu:</b> tek siteye varsayılan uygulayan uç
/// <c>sites.defaults.partial</c> döndürüyor. Aynı bilginin toplu yolda kaybolması, bu depoda
/// defalarca ölçülen kalıptır — koruma bir yola konur, kardeşi eski hâlde kalır.
/// </para>
/// <para>
/// <b>Eksikler SİTE ADI ve ADIM ADIYLA yazılır.</b> "3 sitede eksik" demek yöneticiye HANGİ
/// sitede NEYİN uygulanmadığını söylemez. <b>Her şey yolundaysa cümle SADE kalır:</b> her
/// devralmaya uyarı iliştirmek, gerçek eksiği olan kayıtları görünmez kılardı.
/// </para>
/// </remarks>
public static class SiteImportAudit
{
    /// <summary>Devralınan tek bir sitenin varsayılan sonucu.</summary>
    /// <param name="SiteName">Site adı.</param>
    /// <param name="FailedFeatures">Uygulanamayan adımlar; boşsa site tamamdır.</param>
    public readonly record struct Outcome(string SiteName, IReadOnlyList<string> FailedFeatures);

    public static string Describe(IReadOnlyList<Outcome> outcomes)
    {
        if (outcomes.Count == 0)
        {
            return "No unmanaged IIS site was found to import.";
        }

        var eksikler = outcomes.Where(item => item.FailedFeatures.Count > 0).ToList();
        var adlar = string.Join(", ", outcomes.Select(item => item.SiteName));

        if (eksikler.Count == 0)
        {
            return $"{outcomes.Count} existing IIS site(s) imported: {adlar}; defaults applied to all.";
        }

        var ayrinti = string.Join(
            "; ",
            eksikler.Select(item => $"{item.SiteName} ({string.Join(", ", item.FailedFeatures)})"));

        return $"{outcomes.Count} existing IIS site(s) imported: {adlar}; "
             + $"defaults INCOMPLETE on {eksikler.Count} of {outcomes.Count}: {ayrinti}.";
    }
}
