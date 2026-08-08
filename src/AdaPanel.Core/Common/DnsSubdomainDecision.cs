namespace AdaPanel.Core.Common;

/// <summary>
/// Alt alan adı için üst bölgede ZATEN var olan bir kaydın doğru olup olmadığına karar verir.
/// </summary>
/// <remarks>
/// <para>
/// Ayrı ve saf bir işlev olmasının sebebi: bu karar doğrudan kullanıcıya ne söyleneceğini belirliyor
/// ("her şey yolunda" mı, "kayıt başka yeri gösteriyor" uyarısı mı) ve yanlış tarafa düşmesi iki
/// yönde de zarar veriyor — gereksiz uyarı gerçek uyarıların okunmamasına, eksik uyarı ise sitenin
/// neden açılmadığının anlaşılamamasına yol açıyor. DNS servisinin içinde gömülü kalsaydı
/// sınanabilmesi için tüm servisin ayağa kaldırılması gerekirdi.
/// </para>
/// <para>
/// Panelin varsayılan bölge şablonu <c>www</c>, <c>mail</c>, <c>ftp</c>, <c>webmail</c>,
/// <c>ns1</c>, <c>ns2</c> kayıtlarını kendisi oluşturur. Bu ayrım olmadan, <c>mail.ornek.com</c>
/// adında bir site açan kullanıcı panelin KENDİ doğru kaydı için uyarı alıyordu.
/// </para>
/// </remarks>
public static class DnsSubdomainDecision
{
    /// <summary>Var olan kayıt zaten bu sunucuyu gösteriyor mu (ya da panelin kendi kaydı mı)?</summary>
    /// <param name="kayitTuru">Var olan kaydın türü (A, CNAME, …).</param>
    /// <param name="kayitDegeri">Var olan kaydın değeri.</param>
    /// <param name="panelinKendiKaydi">Kayıt panel tarafından yönetilen bir varsayılan mı.</param>
    /// <param name="sunucuAdresi">Panelin bu sunucu için çözdüğü adres; ölçülemediyse boş.</param>
    /// <param name="bolgeAdi">Üst bölgenin adı (CNAME'in bölgenin kendisini göstermesi meşrudur).</param>
    public static bool ZatenDogruMu(
        string? kayitTuru,
        string? kayitDegeri,
        bool panelinKendiKaydi,
        string? sunucuAdresi,
        string? bolgeAdi)
    {
        if (panelinKendiKaydi)
        {
            return true;
        }

        var deger = (kayitDegeri ?? string.Empty).Trim().TrimEnd('.');
        if (deger.Length == 0)
        {
            return false;
        }

        // Adres ÖLÇÜLEMEDİYSE eşleşme iddiasında bulunulmaz. Boş adresi boş değere eşit sayıp
        // "doğru" demek, hiçbir yeri göstermeyen bir kaydı onaylamak olurdu.
        var adres = (sunucuAdresi ?? string.Empty).Trim();
        if (adres.Length > 0 && string.Equals(deger, adres, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Şablonun ürettiği www/ftp kayıtları bölgenin kendisine CNAME'dir; bu da doğrudur.
        var bolge = (bolgeAdi ?? string.Empty).Trim().TrimEnd('.');
        return bolge.Length > 0
            && string.Equals(kayitTuru?.Trim(), "CNAME", StringComparison.OrdinalIgnoreCase)
            && string.Equals(deger, bolge, StringComparison.OrdinalIgnoreCase);
    }
}
