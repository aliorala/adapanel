namespace AdaPanel.Core.Dtos.Firewall;

/// <summary>
/// Panelin ENGELLEME kurallarini temizleyen erisim kurtarma kolunun SONUCU.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden sayi degil de rapor.</b> Eski donus tipi <c>int</c>'ti ve iki farkli seyi ayni sayiya
/// katliyordu: kaldirma oncesi BULUNAN kural sayisi "kaldirilan" diye yaziliyordu, guvenlik
/// duvari hic okunamadiginda da 0 cikiyor ve bu "temizlenecek kural yoktu" diye okunuyordu.
/// </para>
/// <para>
/// Uc degerli durustluk: <see cref="Measured"/> false ise HICBIR SEY iddia edilmez;
/// olculduyse <see cref="Removed"/> gercekten gidenleri, <see cref="Remaining"/> ise GITMEYENLERI
/// ADIYLA soyler. "Ucu kaldirilamadi" demek yoneticiye HANGI kuralin hala erisimi kestigini
/// soylemez -- ve bu, panele ulasamayan bir yoneticinin elindeki tek bilgidir.
/// </para>
/// </remarks>
/// <param name="Measured">Guvenlik duvari kurallari okunabildi mi.</param>
/// <param name="Removed">Gercekten kaldirilan kural adlari.</param>
/// <param name="Remaining">Kaldirilmaya calisilip HALA DURAN kural adlari.</param>
public sealed record FirewallBlockCleanupReport(
    bool Measured,
    IReadOnlyCollection<string> Removed,
    IReadOnlyCollection<string> Remaining)
{
    /// <summary>Guvenlik duvari okunamadi; ne kaldirildigi BILINMIYOR.</summary>
    public static FirewallBlockCleanupReport Olculemedi { get; } = new(false, [], []);

    /// <summary>Olculdu: kaldirilacak engelleme kurali YOKTU.</summary>
    public static FirewallBlockCleanupReport Bos { get; } = new(true, [], []);

    /// <summary>Her sey yolunda mi: olculdu ve geride kural KALMADI.</summary>
    public bool Temiz => Measured && Remaining.Count == 0;

    /// <summary>
    /// Gunluge yazilacak tek cumle. Her sey yolundaysa SADE kalir; bir sey eksikse ADIYLA soyler.
    /// </summary>
    public string Describe()
    {
        if (!Measured)
        {
            return "guvenlik duvari OKUNAMADI, engelleme kurallarina ne oldugu OLCULEMEDI";
        }

        if (Removed.Count == 0 && Remaining.Count == 0)
        {
            return "kaldirilacak panel engelleme kurali yoktu";
        }

        return Remaining.Count == 0
            ? $"{Removed.Count} panel engelleme kurali kaldirildi"
            : $"{Removed.Count} panel engelleme kurali kaldirildi, KALDIRILAMAYAN {Remaining.Count}: "
              + string.Join(", ", Remaining);
    }
}
