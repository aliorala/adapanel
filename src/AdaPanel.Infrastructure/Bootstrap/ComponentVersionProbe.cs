using System.Text.RegularExpressions;

namespace AdaPanel.Infrastructure.Bootstrap;

/// <summary>
/// Kurulumdan SONRA diskteki surumu OKUR ve panelin ne kaydedecegine karar verir.
/// </summary>
/// <remarks>
/// <para>
/// <b>KAPATILAN YALAN.</b> Kurulum bittiginde panel <c>installedComponent.Version =
/// component.Version</c> yaziyordu — yani manifest'ten ISTENEN surumu. Kurulan surum HIC geri
/// okunmuyordu; ustelik <c>ValidateComponentRuntimeAsync</c> zaten <c>--version</c> komutunu
/// KOSTURUYOR ve ciktisini ATIYORDU. Yalniz cikis koduna bakiliyordu.
/// </para>
/// <para>
/// <b>Bedeli somut.</b> Bu depoda birkac bilesenin indirme adresi TAHMIN EDILEREK kuruluyor
/// (MySQL/PostgreSQL/pgAdmin cozumleyicileri surum numarasini URL'e dizerek deniyor). Adres bir
/// yedege duserse, paket baska bir yama surumu tasirsa ya da yukseltme yarim kalip eski paket
/// tekrar kurulursa panel ISTENEN surumu kaydeder. Sonrasi zincirleme: yazilim listesi yanlis
/// surumu gosterir, guncelleme onbellegi (<c>RefreshUpdateCacheEntry</c>) o yanlis surumle
/// beslendigi icin "guncelsiniz" der ve YAMASI CIKMIS bir surum guncel gorunur. Guvenlik
/// yamalarini surum karsilastirmasiyla takip eden bir yonetici icin bu, sessiz bir acik demektir.
/// </para>
/// <para>
/// <b>Uc degerli durustluk.</b> Olculdu (ve istenenle ayni) / olculdu ama FARKLI cikti /
/// olculemedi. Ucuncusu icin panel istenen surumu gostermeye devam eder ama bunun bir OLCUM
/// OLMADIGINI soyler; uydurmayla gercek arasindaki fark kayitta gorunur.
/// </para>
/// </remarks>
internal static partial class ComponentVersionProbe
{
    /// <summary>
    /// <c>--version</c> ciktisindan surumu ayiklar; bulunamazsa <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Araclar surumu cok farkli sarar: <c>v22.11.0</c> (node), <c>Python 3.12.4</c>,
    /// <c>Composer version 2.7.7 2024-06-10</c>, <c>stalwart-cli 0.10.6</c>. Ortak olan tek sey
    /// NOKTALI SAYI DIZISIDIR; ilk oyle jeton alinir. Basindaki <c>v</c> atilir.
    /// <para>
    /// TARIH TUZAGI: Composer ciktisi surumden SONRA <c>2024-06-10</c> yazar. Duz "ilk sayi
    /// dizisi" araniyor olsa bile surum once geldigi icin dogru jeton secilir; yine de tarih
    /// bicimi (<c>\d{4}-\d{2}-\d{2}</c>) surum sayilmasin diye elenir.
    /// </para>
    /// </remarks>
    internal static string? FromCommandOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (Match match in VersionTokenRegex().Matches(output))
        {
            var token = match.Value.TrimStart('v', 'V');
            if (TarihGibiRegex().IsMatch(match.Value))
            {
                continue;
            }

            if (token.Contains('.'))
            {
                return token;
            }
        }

        return null;
    }

    /// <summary>
    /// Kurulum sonrasi kaydedilecek surum + bunun bir OLCUM olup olmadigi.
    /// </summary>
    /// <param name="requestedVersion">Manifest'in istedigi surum.</param>
    /// <param name="measuredVersion">Diskten okunan surum; okunamadiysa <c>null</c>/bos.</param>
    /// <remarks>
    /// <b>FAZLA KAPATMA NOBETI:</b> olculen surum istenenle ayni oldugunda cumle SADE kalir.
    /// Her kurulumda "dogrulandi/dogrulanamadi" diye gurultu yapan bir cozum, uyariyi
    /// degersizlestirir ve gercek sapma gozden kacardi.
    /// </remarks>
    internal static ComponentVersionRecord Decide(string? requestedVersion, string? measuredVersion)
    {
        var requested = (requestedVersion ?? string.Empty).Trim();
        var measured = (measuredVersion ?? string.Empty).Trim();

        if (measured.Length == 0)
        {
            return new ComponentVersionRecord(requested, false, requested);
        }

        // Olculen surum KAYDEDILIR -- istenen degil. Sapma varsa panel gercegi gostermeli.
        return new ComponentVersionRecord(measured, true, requested);
    }

    [GeneratedRegex(@"v?\d+(?:\.\d+)+", RegexOptions.CultureInvariant)]
    private static partial Regex VersionTokenRegex();

    [GeneratedRegex(@"^\d{4}\.\d{2}\.\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex TarihGibiRegex();
}

/// <summary>Kurulan bilesenin surumu hakkinda panelin SOYLEYEBILECEGI sey.</summary>
/// <param name="Version">Kaydedilecek surum (olculduyse olculen, olculemediyse istenen).</param>
/// <param name="Measured">Bu surum diskten GERI OKUNDU mu.</param>
/// <param name="RequestedVersion">Manifest'in istedigi surum (sapma raporlamak icin).</param>
internal readonly record struct ComponentVersionRecord(string Version, bool Measured, string RequestedVersion)
{
    /// <summary>Olculdu ve istenenden FARKLI cikti mi.</summary>
    internal bool Deviated =>
        Measured && !string.Equals(Version, RequestedVersion, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Denetim kaydina yazilacak surum cumlesi. Her sey yolundaysa SADE: "version 1.2.3".
    /// </summary>
    internal string Describe() =>
        !Measured
            ? $"version {RequestedVersion} REQUESTED (installed version could not be measured)"
            : Deviated
                ? $"version {Version} measured on disk (manifest requested {RequestedVersion})"
                : $"version {Version}";
}
