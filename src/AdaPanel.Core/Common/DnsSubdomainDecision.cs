using AdaPanel.Core.Dtos.Sites;

namespace AdaPanel.Core.Common;

/// <summary>
/// Alt alan adı sonucunun site oluşturma ekranında yazılacağı satır ve HANGİ listeye gireceği.
/// </summary>
/// <param name="Uyari">
/// <see langword="true"/> ise <c>Warnings</c>, değilse <c>Created</c> listesine yazılır. Ayrım
/// kozmetik değil: <c>Created</c> "bu iş yapıldı" demektir ve arayüz onu yeşil basar.
/// </param>
/// <param name="Satir">Kullanıcıya gösterilecek metin.</param>
public sealed record SubdomainRaporu(bool Uyari, string Satir);

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

    /// <summary>
    /// Alt alan adı sonucunu kullanıcıya YAZILACAK tek bir satıra indirger; söylenecek bir şey
    /// yoksa <see langword="null"/>.
    /// </summary>
    /// <param name="sonuc">DNS servisinin ölçülmüş sonucu.</param>
    /// <param name="tamAd">Kaydın tam adı (<c>blog.ornek.com</c>).</param>
    /// <remarks>
    /// <para>
    /// <b>Neden burada, denetleyicinin içinde değil.</b> Bu zincir "kullanıcıya ne söylenecek"
    /// kararının tamamıdır ve bir dalı yanlış olduğunda hiçbir istisna atılmaz, hiçbir günlük
    /// düşmez — yalnızca yanlış cümle yazılır. <c>SitesController</c>'ın private bir metodunda
    /// dururken sınanabilmesi için bütün denetleyicinin bağımlılıklarıyla ayağa kaldırılması
    /// gerekiyordu; pratikte hiç sınanmadı ve tam bu yüzden bir dalı yıllarca yanlış kaldı.
    /// <see cref="ZatenDogruMu"/> aynı gerekçeyle buraya alınmıştı.
    /// </para>
    /// <para>
    /// <b>Düzeltilen dal:</b> <see cref="SubdomainRecordResult.Created"/> tek başına "DNS'te var"
    /// demek DEĞİLDİR — kayıt panelin bölge dosyasına yazılmış ama Windows DNS'e yayınlanmamış
    /// olabilir. Yayın uyarısı varken satır <c>Created</c> listesine yazılırsa ekran
    /// "dns: blog.ornek.com A 1.2.3.4" diyor, DNS'te ise hiçbir şey olmuyordu.
    /// </para>
    /// </remarks>
    public static SubdomainRaporu? Anlat(SubdomainRecordResult sonuc, string tamAd)
    {
        ArgumentNullException.ThrowIfNull(sonuc);

        if (sonuc.Created)
        {
            // YAYINLANMADIYSA "oluşturuldu" DEME. Biçim "anahtar — ayrıntı": arayüz anahtarı
            // ayırıp t() ile çevirir, ayrıntıyı olduğu gibi basar.
            return sonuc.PublishWarningKey.Length > 0
                ? new SubdomainRaporu(true, $"{sonuc.PublishWarningKey} — {tamAd} A {sonuc.Address}")
                : new SubdomainRaporu(false, $"dns: {tamAd} A {sonuc.Address}");
        }

        if (sonuc.AlreadyExists)
        {
            // Kayıt zaten doğru (panelin kendi varsayılanı ya da bu sunucuyu gösteriyor):
            // yapılacak bir şey yok, uyarı DEĞİL bilgi. Her şey yolundayken uyarı basmak, gerçek
            // uyarıların da okunmamasına yol açar.
            return sonuc.AlreadyCorrect
                ? new SubdomainRaporu(false, $"dns: {tamAd} zaten mevcut ({sonuc.ExistingRecord})")
                : new SubdomainRaporu(
                    true,
                    $"dns ({tamAd}): record already exists and points elsewhere ({sonuc.ExistingRecord}); left unchanged.");
        }

        if (sonuc.ZoneMissing)
        {
            return new SubdomainRaporu(
                true,
                $"dns ({tamAd}): parent zone is not managed here; add the record at your DNS provider.");
        }

        return sonuc.Error.Length > 0
            ? new SubdomainRaporu(true, $"dns ({tamAd}): {sonuc.Error}")
            : null;
    }
}
