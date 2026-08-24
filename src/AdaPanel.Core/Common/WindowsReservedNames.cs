using System;

namespace AdaPanel.Core.Common;

/// <summary>
/// Windows'un AYRILMIŞ AYGIT ADLARININ (<c>CON</c>, <c>NUL</c>, <c>PRN</c>, <c>AUX</c>,
/// <c>COM1..9</c>, <c>LPT1..9</c>, <c>CLOCK$</c>) TEK KAYNAĞI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı bir kontrol gerekiyor:</b> deponun bütün ad temizleyicileri
/// <see cref="System.IO.Path.GetInvalidFileNameChars"/> tabanlıydı ve o liste aygıt adlarını
/// BİLMEZ — "CON" içinde geçersiz KARAKTER yoktur, ad olarak geçersizdir. Windows bu adları
/// klasör/dosya adı olarak açmayı reddeder; uzantı eklense bile (<c>con.txt</c>, <c>con.json</c>,
/// <c>con.log</c>) kural değişmez, çünkü ayrılmışlık NOKTADAN ÖNCEKİ parça üzerinden bakılır.
/// </para>
/// <para>
/// <b>ÖLÇÜLEN etki — bu makinede, gerçek dosya sistemine yazarak (Windows 10 19045, .NET 8):</b>
/// <list type="bullet">
/// <item><c>Directory.CreateDirectory(&lt;dizin&gt;\CON)</c> → <c>DirectoryNotFoundException</c>. GÜRÜLTÜLÜ.</item>
/// <item>Atomik yazım (geçici dosya + <c>File.Move</c>, <c>JsonFileStore</c>'un yaptığı) <c>CON.json</c> için
/// <c>FileNotFoundException: Could not find file '\\.\CON'</c>. GÜRÜLTÜLÜ.</item>
/// <item><c>File.WriteAllText(&lt;dizin&gt;\CON)</c> HATA VERMEZ, "başarılı" döner — ve
/// <c>File.Exists</c> sonrasında <b>false</b>'tur. Veri aygıta gitti, dosya HİÇ oluşmadı.
/// Aynısı <c>NUL</c>, <c>AUX</c>, <c>COM1</c> ve <c>con.json</c> için de ölçüldü.
/// <b>SESSİZ VERİ KAYBI.</b> (<c>PRN</c> ve <c>LPT9</c> bu makinede bağlı aygıt olmadığı için
/// gürültülü düşüyor — yani "gürültülü mü sessiz mi", makinenin donanımına bağlı.)</item>
/// </list>
/// Önceki denetim bu etkiyi "sessiz veri kaybı DEĞİL, gürültülü çökme; tek sessiz yol
/// <c>\\.\NUL</c> ve o ULAŞILAMAZ" diye kaydetmişti. Ölçüm bunu DÜZELTİYOR: düz
/// <c>File.WriteAllText</c> kullanan HER çağıran için sessizdir ve aygıt adı <c>NUL</c> olmak
/// zorunda değildir. Panelin JSON depoları atomik yazdığı için bugün gürültülü tarafta kalıyor;
/// yani düzeltmenin değeri, "bugün gürültülü" olanın yarın sessiz olmamasını da garanti etmek.
/// </para>
/// <para>
/// <b><c>CLOCK$</c> DÜRÜSTÇE:</b> bu makinede ayrılmış ÇIKMADI — <c>CLOCK$</c> adlı dosya
/// oluşturulabiliyor (ölçüldü). Listede yine de tutuluyor: eski Windows sürümlerinde ayrılmıştı,
/// maliyeti bir karşılaştırma, ve var olan ikinci uygulamayla (<c>HtaccessHostingPaths</c>) BİREBİR
/// aynı kalması sapma nöbetçisinin çalışması için şart.
/// </para>
/// <para>
/// <b>Neden ORTAK dosya, neden her temizleyiciye elle yazılmadı:</b> bu depoda "elle yazılmış
/// liste kayar" hatası bugüne kadar dört kez ısırdı (denetim kapsam listesi, alan adı cascade
/// tür listesi, i18n ölü anahtar listesi, silme kalıntı listesi). Liste bir kez burada durur;
/// yeni bir temizleyici eklendiğinde tek yapılacak buradan geçmektir.
/// </para>
/// <para>
/// <b>Neden <c>Substring</c>, neden aralık (<c>[..n]</c>) yok ve neden <c>System.Linq</c>
/// kullanılmıyor:</b> bu dosyanın ileride <c>AdaPanel.HtaccessModule</c> (net48) projesine
/// KAYNAK OLARAK bağlanabilmesi için. <c>PathContainment.cs</c> aynı klasörden zaten öyle
/// bağlanıyor. C# aralık sözdizimi <c>System.Range</c> ister; net48 BCL'inde o tür YOKTUR.
/// Bugün bağlı değil — bkz. aşağıdaki dürüstlük notu.
/// </para>
/// <para>
/// <b>DÜRÜSTLÜK NOTU — bugün İKİ uygulama var:</b>
/// <c>AdaPanel.Infrastructure.Htaccess.Hosting.HtaccessHostingPaths.IsReservedDeviceName</c> aynı
/// kuralı kendi içinde taşır, çünkü o dosya net48 modül projesinde de derleniyor ve bu dosya
/// oraya HENÜZ bağlanmadı (csproj başka bir ekibin dosyası). Kopyanın sapmaması
/// <c>WindowsReservedNameTests.IkiUygulamaAyniCevabiVeriyor</c> ile ÇİVİLENDİ: iki uygulama
/// tüm sınır girdilerinde ayrışırsa test kırmızıya döner.
/// </para>
/// </remarks>
public static class WindowsReservedNames
{
    /// <summary>
    /// Ayrılmış bir ada eklenen ek. Ad artık ayrılmış değildir ("CON" → "CON_") ve okunur kalır:
    /// bir sorunda yönetici klasöre bakıp hangi kaynak olduğunu görebilmeli.
    /// </summary>
    /// <remarks>
    /// <b>Çakışma sınırı DÜRÜSTÇE:</b> "CON" ile "CON_" adlı iki kaynak aynı ada düşer. Bu risk
    /// bilinçli kabul edildi çünkü (a) çağıranların hepsinde aynı çakışma sınıfı KARAKTER
    /// değiştirmeden zaten var ("a:b" ile "a_b" de aynı ada düşüyor), (b) asıl kapı yukarıda:
    /// <c>WebAdministrationSiteService.NormalizeSiteName</c> ayrılmış adlı yeni siteyi hiç
    /// açtırmıyor, yani buradaki düzeltme geriye dönük artıklar için ikinci savunma hattı.
    /// Çakışmanın bir KİRACI İZOLASYONU sınırı olduğu yerde (site başına anahtar dosyası) çağıran
    /// ayrıca ÖZET ekler — <c>HtaccessHostingPaths.SafeSiteKey</c> tam olarak bunu yapar.
    /// </remarks>
    public const string Suffix = "_";

    /// <summary>
    /// Ad, Windows'un ayrılmış aygıt adlarından biri mi? Uzantılı biçim de sayılır
    /// (<c>con.txt</c> da aygıttır).
    /// </summary>
    public static bool IsReserved(string? value)
    {
        var name = value ?? string.Empty;

        // Ayrılmışlık İLK noktadan önceki parçaya bakar: "con.txt" ve hatta "con.a.b" de aygıttır.
        // Son noktaya bakılsaydı "con.txt" için parça "con.txt" olur ve kontrol kaçırırdı.
        var dot = name.IndexOf('.');
        if (dot >= 0)
        {
            name = name.Substring(0, dot);
        }

        // Windows dosya adının başındaki/sonundaki boşluğu yok sayar: "CON " açılmaya çalışılınca
        // yine aygıta gider. Kırpmadan bakmak, boşluk eklenerek atlatılabilen bir kontrol olurdu.
        name = name.Trim().ToUpperInvariant();

        switch (name)
        {
            case "CON":
            case "PRN":
            case "AUX":
            case "NUL":
            case "CLOCK$":
                return true;
        }

        // COM1..COM9 ve LPT1..LPT9. COM0/LPT0 ayrılmış DEĞİLDİR (ölçülebilir kural: Windows
        // yalnız 1-9'u aygıta bağlar); "COM10" da değildir, bu yüzden uzunluk tam 4 olmalı.
        return name.Length == 4
            && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal))
            && name[3] >= '1'
            && name[3] <= '9';
    }

    /// <summary>
    /// Ayrılmış bir adı diskte açılabilir hâle getirir; ayrılmış değilse adı AYNEN döndürür.
    /// </summary>
    /// <remarks>
    /// Ek, uzantıdan ÖNCEYE konur ("con.json" → "con_.json"): sona konsaydı ("con.json_") uzantı
    /// bozulur, dosya JSON deposu tarafından bir daha bulunamazdı.
    /// </remarks>
    public static string MakeSafe(string? value)
    {
        var name = value ?? string.Empty;
        if (!IsReserved(name))
        {
            return name;
        }

        var dot = name.IndexOf('.');
        return dot < 0
            ? name + Suffix
            : name.Substring(0, dot) + Suffix + name.Substring(dot);
    }
}
