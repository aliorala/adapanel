namespace AdaPanel.Infrastructure.SelfUpdate;

/// <summary>
/// Kendi kendini guncelleme zincirinin GUVEN KURALLARI: hangi depodan, hangi adresten indirilir ve
/// indirilen dosya SYSTEM olarak calistirilmadan once neye bakilir.
/// </summary>
/// <remarks>
/// <para>
/// NEDEN AYRI VE SAF BIR SINIF: bu kurallarin hepsi "evet/hayir" kararidir ve karar mantiginin
/// dosya sistemi, ag ya da PowerShell olmadan sinanabilmesi gerekir. Bulasik bir paket bu makinede
/// URETILEMEZ; uretilemeyen bir durumun karari yine de dogrulanmak zorundadir.
/// </para>
/// <para>
/// TEHDIT: indirilen exe <c>WindowsUpdateInstallerLauncher</c> tarafindan SYSTEM kimligiyle
/// calistirilir. Panelin "Administrator" rolu bir WINDOWS yoneticisi DEGILDIR -- <b>TEK istisna
/// sunucu SAHIBIDIR</b> (bkz. <c>AdaPanel.Core.Interfaces.IServerOwnerService</c>): terminal ucu
/// bilerek SYSTEM kabugu verir ama YALNIZ ona acilir, cunku o kisi zaten makinenin sahibidir.
/// Panelden yaratilan EK yonetici hesaplari bu siniri asamaz. Dolayisiyla "paneli yoneten HERKES
/// guncelleme kaynagini serbestce degistirebilsin" demek, bir ek yonetici hesabina dogrudan SYSTEM
/// vermek demektir.
/// </para>
/// <para>
/// DOKTRININ ESKI HALI YANLISTI, burada duzeltildi: "kabuk komutu uclari bilerek LOCAL SERVICE
/// olarak kosuyor" yaziyordu. Bu, <c>LimitedIdentityRunner</c>'dan gecen uclar (composer/artisan/
/// npm/wp-cli) ve sinirli kimlikli zamanlanmis gorevler icin dogru; <c>/api/v1/terminal</c> icin
/// DEGIL -- orada <c>powershell.exe</c> panelin kendi LocalSystem jetonuyla kosar. Yanlis yazilmis
/// bir doktrin, kendisine dayanan her korumanin gerekcesini curutur.
/// </para>
/// <para>
/// AYRICA: bu depoda "gelistirme makinesinde aktif PE bulastiricisi" olayi yasandi. Bulastirici
/// uretilen exe'yi degistirip <c>CompanyName</c>'i "Synaptics Pointing Device Driver" yapiyordu.
/// SHA256 karsilastirmasi bu sinifa KORDUR: yayindaki dosya da ayni bulasik dosyadir, iki ozet de
/// birebir tutar ve dogrulama "temiz" der. Yakalayan tek sey paketin KIMLIGINE bakmaktir.
/// </para>
/// </remarks>
internal static class UpdatePackageTrust
{
    /// <summary>
    /// Uretilen kurulum paketinin tasimasi gereken yayinci. OLCULDU: <c>AdaPanelSetup.exe</c>
    /// (0.5.0) -> CompanyName="AdaPanel", ProductName="AdaPanel". Bu degerler
    /// <c>installer\AdaPanel.iss</c> icindeki <c>MyAppPublisher</c>/<c>MyAppName</c> tanimlarindan
    /// gelir; ikisinin ayrisamamasi <c>UpdatePackageTrustTests</c> icinde ayrica sinanir.
    /// </summary>
    internal const string BeklenenYayinci = "AdaPanel";

    /// <summary>
    /// BULASMA IMZALARI. Ayni liste <c>scripts\verify-publish-output.ps1</c> icinde de var; orasi
    /// URETIM tarafini, burasi TUKETIM tarafini korur. Uretim tarafi atlanirsa (baska bir makinede
    /// derlenmis paket) bu liste son savunmadir.
    /// </summary>
    internal static readonly string[] YasakSirketIzleri = ["Synaptics"];

    /// <summary>
    /// Guncellemenin cekilebilecegi depolar. Bos dize = "GitHub kapali" (gecerli bir durum).
    /// <para>
    /// Liste KODDA: genisletmek icin birinin bakip yeni bir surum yayinlamasi gerekir. Ayni
    /// yaklasim <c>verify-publish-output.ps1</c> icindeki beklenen ikili listesinde de kullaniliyor
    /// ve sebebi aynidir: sessizce degisebilen bir guven listesi guven listesi degildir.
    /// </para>
    /// </summary>
    internal static readonly string[] IzinliDepolar = ["aliorala/adapanel"];

    /// <summary>
    /// Paketin inebilecegi sunucular. GitHub, <c>browser_download_url</c> adresini
    /// <c>objects.githubusercontent.com</c> uzerine yonlendirir; ilk adresi denetliyoruz, sonraki
    /// yonlendirmeler HttpClient tarafindan izlenir.
    /// </summary>
    internal static readonly string[] IzinliIndirmeSunuculari =
    [
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    ];

    /// <summary>
    /// "owner/repo", "github.com/owner/repo", "https://github.com/owner/repo.git" -> ("owner","repo").
    /// Cozulemezse <c>null</c>.
    /// </summary>
    /// <remarks>
    /// TEK AYRISTIRICI: hem depo izin denetimi hem de GitHub API cagrisi bunu kullanir. Iki ayri
    /// ayristirici olsaydi biri "gecerli" digeri "gecersiz" diyebilir ve izin denetimi atlanabilirdi.
    /// </remarks>
    internal static (string Owner, string Name)? DepoyuCozumle(string? repo)
    {
        var temiz = (repo ?? string.Empty).Trim().Trim('/');
        if (temiz.Length == 0)
        {
            return null;
        }

        var isaret = temiz.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
        if (isaret >= 0)
        {
            temiz = temiz[(isaret + "github.com/".Length)..];
        }

        var parcalar = temiz.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parcalar.Length < 2)
        {
            return null;
        }

        var owner = parcalar[0].Trim();
        var name = parcalar[1].Trim();
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return owner.Length == 0 || name.Length == 0 ? null : (owner, name);
    }

    /// <summary>Depo izin listesinde mi? Bos dize ("GitHub kapali") her zaman gecerlidir.</summary>
    internal static bool DepoIzinli(string? repo)
    {
        if (string.IsNullOrWhiteSpace(repo))
        {
            return true;
        }

        var cozum = DepoyuCozumle(repo);
        if (cozum is not { } deger)
        {
            return false;
        }

        var normal = $"{deger.Owner}/{deger.Name}";
        return IzinliDepolar.Any(izinli => string.Equals(izinli, normal, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Indirme adresi kabul edilebilir mi? Sunucu izin listesinde olmali; adres dogrudan
    /// <c>github.com</c> ise yolu da yapilandirilmis deponun release varligina baglanir.
    /// </summary>
    /// <remarks>
    /// Neden gerekli: adres <c>config/update-manifest.json</c> onbelleginden de gelebilir ve o dosya
    /// diskte durur. Sunucu denetimi olmadan, o dosyaya yazabilen biri panelin SYSTEM olarak
    /// calistiracagi exe'nin adresini belirler.
    /// </remarks>
    internal static bool IndirmeAdresiIzinli(string? url, string? repo)
    {
        if (!Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out var adres) ||
            !string.Equals(adres.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            // HTTP bilerek reddedilir: paket SYSTEM olarak calisiyor, aradaki biri onu degistirebilir.
            return false;
        }

        if (!IzinliIndirmeSunuculari.Any(sunucu => string.Equals(sunucu, adres.Host, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!string.Equals(adres.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            // Nesne sunuculari (objects.githubusercontent.com) yolunda depo adi TASIMAZ; imzali ve
            // suresi dolan adreslerdir, sunucu denetimi burada elimizdeki tek olcut.
            return true;
        }

        var cozum = DepoyuCozumle(repo);
        if (cozum is not { } deger)
        {
            return false;
        }

        var beklenenOnek = $"/{deger.Owner}/{deger.Name}/releases/download/";
        return adres.AbsolutePath.StartsWith(beklenenOnek, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Paketin surum kaynagi (VersionInfo) beklenen yayinciyi mi gosteriyor? Kusur varsa aciklamasi,
    /// yoksa <c>null</c> doner.
    /// </summary>
    /// <remarks>
    /// BOS DA REDDEDILIR: uretilen her <c>AdaPanelSetup.exe</c> bu alanlari tasir (olculdu). Alanlarin
    /// silinmis olmasi, dosyanin bizim uretmedigimizin ya da degistirildiginin isaretidir.
    /// </remarks>
    internal static string? PaketKimligiKusuru(string? companyName, string? productName)
    {
        var sirket = (companyName ?? string.Empty).Trim();
        var urun = (productName ?? string.Empty).Trim();

        foreach (var yasak in YasakSirketIzleri)
        {
            if (sirket.Contains(yasak, StringComparison.OrdinalIgnoreCase) ||
                urun.Contains(yasak, StringComparison.OrdinalIgnoreCase))
            {
                return $"BULASMA IMZASI: CompanyName='{sirket}' ProductName='{urun}'.";
            }
        }

        if (!string.Equals(sirket, BeklenenYayinci, StringComparison.OrdinalIgnoreCase))
        {
            return $"Beklenen yayinci '{BeklenenYayinci}', pakette CompanyName='{sirket}'.";
        }

        if (!string.Equals(urun, BeklenenYayinci, StringComparison.OrdinalIgnoreCase))
        {
            return $"Beklenen urun '{BeklenenYayinci}', pakette ProductName='{urun}'.";
        }

        return null;
    }

    /// <summary>
    /// Authenticode kararı. Kusur varsa aciklamasi, yoksa <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IMZA BUGUN ZORUNLU DEGIL, CUNKU OLCULDU: uretilen paket imzasiz (<c>NotSigned</c>) ve depoda
    /// hicbir <c>signtool</c> adimi yok. Imzayi zorunlu kilmak, kurulu HER paneli guncellenemez hale
    /// getirirdi -- yani guvenlik adina isleyen tek guncelleme yolunu kapatmak olurdu.
    /// </para>
    /// <para>
    /// BUGUN YAPILAN: paket imzali GELIYORSA imza GECERLI olmak ZORUNDA. Imzali bir ikilinin imzasini
    /// kaybetmesi, degistirilmis olmasinin en guclu isaretidir; ayni kural uretim tarafinda
    /// <c>verify-publish-output.ps1</c> icinde de uygulaniyor.
    /// </para>
    /// <para>
    /// SERTIFIKA ALINDIGINDA: <paramref name="imzaZorunlu"/> true yapilir (config/update-trust.json)
    /// ve zincir tamamlanir -- kural burada HAZIR duruyor, degistirilmesi gereken tek sey bayrak.
    /// </para>
    /// </remarks>
    internal static string? ImzaKusuru(string? status, string? subject, bool imzaZorunlu)
    {
        var durum = (status ?? string.Empty).Trim();
        var konu = (subject ?? string.Empty).Trim();

        if (string.Equals(durum, "Valid", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (imzaZorunlu)
        {
            return $"Imza zorunlu ama paketin imza durumu '{durum}' (konu: '{konu}').";
        }

        // "NotSigned" = hic imza yok -> bugunku normal durum, gecer.
        if (string.Equals(durum, "NotSigned", StringComparison.OrdinalIgnoreCase) || durum.Length == 0)
        {
            return null;
        }

        // Imza VAR ama gecerli degil (HashMismatch, UnknownError, NotTrusted...): bu bir arizadan cok
        // bir mudahale isaretidir.
        return $"Paket imzali ama imzasi gecerli degil: '{durum}' (konu: '{konu}').";
    }
}
