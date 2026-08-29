namespace AdaPanel.Core.Dtos.Migration;

/// <summary>Kaynak panelde (Plesk ya da cPanel/WHM) keşfedilen bir domain (göç sihirbazında seçilince formu doldurur).</summary>
public sealed class PleskDomainDto
{
    public string Name { get; set; } = string.Empty;

    /*
     * TEK ALAN İKİ ANLAM TAŞIYORDU — ÖLÇÜLDÜ, AYRILDI.
     *
     * ÖNCESİ: yalnız `HostingType` vardı ve iki keşif yolu onu FARKLI şeylerle dolduruyordu:
     *   · `DiscoverPleskAsync`  → Plesk JSON'undaki `hosting_type` ("virtual", "none")
     *   · `DiscoverCpanelAsync` → WHM `listaccts` yanıtındaki `user`, yani HESABIN KULLANICI ADI
     * Arayüz alanı etiketsiz, ham basıyordu (`MigrationView.vue`: `{{ d.hostingType }}`). Yani
     * ekranda yan yana duran iki satırdan biri "virtual" diğeri "ali_shop" yazıyor ve panel
     * ikisine de aynı adı veriyordu: kullanıcı NE gördüğünü bilmiyordu.
     *
     * Sonuç yalnız kozmetik de değildi: cPanel'de uzak yol şablonu `/home/{user}/public_html`
     * ve `{user}` bu alandan doldurulur. Aynı alanı Plesk'te de `{user}` yerine geçiren kod,
     * bir gün Plesk şablonuna `{user}` yazan kullanıcıya `/home/virtual/...` üretirdi.
     *
     * Artık her anlam kendi alanında; birinin dolu olması diğerinin boş olmasıdır.
     */

    /// <summary>Plesk barındırma türü (örn. virtual, none) — YALNIZ Plesk keşfinde dolar, bilgilendirme amaçlı.</summary>
    public string HostingType { get; set; } = string.Empty;

    /// <summary>cPanel/WHM hesabının sistem kullanıcı adı — YALNIZ cPanel keşfinde dolar; uzak yol şablonundaki `{user}` bundan gelir.</summary>
    public string AccountUser { get; set; } = string.Empty;
}

/// <summary>Plesk keşif sonucu.</summary>
public sealed class PleskDiscoverResultDto
{
    public List<PleskDomainDto> Domains { get; set; } = [];
}
