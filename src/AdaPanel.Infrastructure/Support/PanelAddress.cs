namespace AdaPanel.Infrastructure.Support;

/// <summary>
/// Panelin DIŞARIYA verilen adresini üreten tek yer (hoş geldin e-postası, parola sıfırlama
/// bağlantısı, tek kullanımlık giriş bağlantısı, abonelikten çıkma onay bağlantısı).
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden tek yerde:</b> aynı üç satır (şema + adres + "443 ise portu yazma" + gizli giriş yolu)
/// iki serviste ayrı ayrı yazılıydı. Üçüncü çağrı yeri eklenince kopya sayısı üçe çıkacaktı; birinin
/// düzeltilip diğerinin unutulması, kullanıcıya AÇILMAYAN bir bağlantı göndermek demektir ve bunu
/// kimse hata olarak bildirmez, sadece "panel çalışmıyor" der.
/// </para>
/// <para>
/// <b>ADRES İSTEMCİDEN ALINMAZ — kaldırılan <c>fallbackHost</c> yedeği.</b> Bu tip eskiden, ayar
/// boşsa isteğin <c>Host</c> başlığına düşen bir yedek kabul ediyordu. <c>Host</c> başlığı
/// İSTEMCİNİN yazdığı serbest metindir: <c>appsettings.json</c>'da <c>AllowedHosts</c> <c>"*"</c>
/// ve boru hattında <c>UseHostFiltering</c> YOK (ikisi de bilerek öyle — gerekçesi aşağıda). Yani
/// yedek yolu, saldırganın seçtiği bir ana adı panelin KENDİ itibarıyla giden postaya ve tek
/// kullanımlık giriş bağlantısına sokabilen bir kapıydı.
/// </para>
/// <para>
/// <b>Yedeği kaldırmak neden hiçbir şeyi bozmuyor — ÖLÇÜLDÜ:</b> yedek yalnız
/// <paramref name="serverAddress"/> BOŞKEN devreye girebilirdi, ama panel adresini üreten tek
/// kaynak olan <c>PanelPreferencesService.GetAsync</c> bu alanı asla boş bırakmaz: değer boşsa
/// <c>ResolveServerAddress()</c> makinenin ilk loopback-olmayan IPv4 adresini, o da yoksa
/// <c>Environment.MachineName</c>'i yazar. İkisi birden boş olamaz. Yani yedek yolu üretimde zaten
/// ULAŞILAMAZDI; duran tek şey istismar yüzeyiydi. Bu ölçüm
/// <c>PanelAdresiHostBasligiTests</c> içinde nöbetçi testle sabitlendi — <c>ResolveServerAddress</c>
/// bir gün kaldırılırsa o test kırılır ve buradaki yer tutucunun e-postalara sızacağını haber verir.
/// </para>
/// <para>
/// <b>Neden <c>AllowedHosts</c> kısılmadı (fazla kapatma sınırı):</b> panele meşru olarak
/// <c>localhost</c>, <c>127.0.0.1</c>, makinenin IP'si, makine adı, kurulumda verilen panel alan
/// adı ve müşterinin SONRADAN eklediği alan adı üzerinden erişilir. Bu küme çalışma zamanında
/// değişir; statik bir listeye çevrilseydi listede olmayan her adres 400 alırdı — yani müşteriyi
/// kendi panelinden kilitlerdi. Bu, kapatmaya çalıştığımız açıktan daha kötü bir arızadır.
/// Doğru sınır, isteği reddetmek değil <b>Host başlığını güvenlik-etkili karara hiç sokmamaktır</b>;
/// bu tip tam olarak o sınırdır.
/// </para>
/// </remarks>
public static class PanelAddress
{
    /// <summary>
    /// Adres hiç bilinmiyorsa e-posta metnine yazılan, insanın düzeltmesi beklenen yer tutucu.
    /// </summary>
    /// <remarks>
    /// Üretimde görünmemesi beklenir (yukarıdaki ölçüme bakınız); GÖRÜNÜRSE de zararsızdır:
    /// bağlantı açılmaz ve düzeltilmesi gerektiği okuyanın gözüne çarpar. Yerine istemcinin
    /// verdiği ana adı yazmak ise sessizce ÇALIŞAN ama saldırgana giden bir bağlantı üretirdi —
    /// yanlış yön bilerek "ölü bağlantı" tarafına seçildi.
    /// </remarks>
    public const string UnknownHostPlaceholder = "<sunucu-adresi>";

    /// <summary>
    /// Panelin kök adresi (şema + host + gerekiyorsa port), sonunda eğik çizgi YOK.
    /// </summary>
    /// <param name="serverAddress">
    /// Ayarlardaki panel adresi. Kaynağı <c>PanelPreferencesService</c> olduğunda boş olamaz.
    /// </param>
    /// <param name="httpsPort">Panelin tercih ettiği HTTPS portu (443 ise adrese yazılmaz).</param>
    public static string BuildBaseUrl(string? serverAddress, int httpsPort)
    {
        var adres = (serverAddress ?? string.Empty).Trim();
        if (adres.Length == 0)
        {
            return $"https://{UnknownHostPlaceholder}";
        }

        var portParcasi = httpsPort == 443 ? string.Empty : $":{httpsPort}";
        return $"https://{adres}{portParcasi}";
    }

    /// <summary>Panelin GİRİŞ adresi: kök adres + gizli giriş yolu (sonunda eğik çizgi YOK).</summary>
    public static string BuildEntryUrl(string? serverAddress, int httpsPort, string securePath)
    {
        var yol = (securePath ?? string.Empty).Trim('/');
        var kok = BuildBaseUrl(serverAddress, httpsPort);
        return yol.Length == 0 ? kok : $"{kok}/{yol}";
    }
}
