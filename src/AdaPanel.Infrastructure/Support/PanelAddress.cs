namespace AdaPanel.Infrastructure.Support;

/// <summary>
/// Panelin DIŞARIYA verilen adresini üreten tek yer (hoş geldin e-postası, parola sıfırlama
/// bağlantısı, tek kullanımlık giriş bağlantısı).
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden tek yerde:</b> aynı üç satır (şema + adres + "443 ise portu yazma" + gizli giriş yolu)
/// iki serviste ayrı ayrı yazılıydı. Üçüncü çağrı yeri eklenince kopya sayısı üçe çıkacaktı; birinin
/// düzeltilip diğerinin unutulması, kullanıcıya AÇILMAYAN bir bağlantı göndermek demektir ve bunu
/// kimse hata olarak bildirmez, sadece "panel çalışmıyor" der.
/// </para>
/// <para>
/// <b>Neden yedek olarak istek adresi:</b> <c>ServerAddress</c> kurulumda boş bırakılabiliyor ve
/// e-posta yolunda bu, metnin içine <c>&lt;sunucu-adresi&gt;</c> yer tutucusu yazılmasıyla
/// sonuçlanıyor (insan okuyup düzeltebilir). Tek kullanımlık giriş bağlantısında yer tutucu
/// KABUL EDİLEMEZ: tarayıcı onu açamaz. Bu yüzden çağıran, isteğin geldiği <c>Host</c> başlığını
/// yedek olarak verebilir — faturalama sistemi panele hangi adresten bağlandıysa müşteri de o
/// adrese yönlendirilir. Yedek verilmediğinde davranış bugünküyle BİREBİR aynıdır.
/// </para>
/// </remarks>
public static class PanelAddress
{
    /// <summary>Adres hiç bilinmiyorsa e-posta metnine yazılan, insanın düzeltmesi beklenen yer tutucu.</summary>
    public const string UnknownHostPlaceholder = "<sunucu-adresi>";

    /// <summary>
    /// Panelin kök adresi (şema + host + gerekiyorsa port), sonunda eğik çizgi YOK.
    /// </summary>
    /// <param name="serverAddress">Ayarlardaki panel adresi; boş olabilir.</param>
    /// <param name="httpsPort">Panelin tercih ettiği HTTPS portu (443 ise adrese yazılmaz).</param>
    /// <param name="fallbackHost">
    /// Ayar boşsa kullanılacak adres — tipik olarak isteğin <c>Host</c> başlığı. Host başlığı portu
    /// zaten içerdiğinden bu yolda port EKLENMEZ.
    /// </param>
    public static string BuildBaseUrl(string? serverAddress, int httpsPort, string? fallbackHost = null)
    {
        var adres = (serverAddress ?? string.Empty).Trim();
        if (adres.Length > 0)
        {
            var portParcasi = httpsPort == 443 ? string.Empty : $":{httpsPort}";
            return $"https://{adres}{portParcasi}";
        }

        var yedek = (fallbackHost ?? string.Empty).Trim();
        return yedek.Length > 0
            ? $"https://{yedek}"
            : $"https://{UnknownHostPlaceholder}";
    }

    /// <summary>Panelin GİRİŞ adresi: kök adres + gizli giriş yolu (sonunda eğik çizgi YOK).</summary>
    public static string BuildEntryUrl(string? serverAddress, int httpsPort, string securePath, string? fallbackHost = null)
    {
        var yol = (securePath ?? string.Empty).Trim('/');
        var kok = BuildBaseUrl(serverAddress, httpsPort, fallbackHost);
        return yol.Length == 0 ? kok : $"{kok}/{yol}";
    }
}
