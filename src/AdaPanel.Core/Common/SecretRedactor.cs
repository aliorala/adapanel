using System.Text.RegularExpressions;

namespace AdaPanel.Core.Common;

/// <summary>
/// Sır maskelemenin TEK KAYNAĞI. Metin bir kullanıcıya, bir kayda ya da bir günlüğe gitmeden önce
/// buradan geçer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden Core'da:</b> bu kural doğduğunda yalnız denetim kaydı için vardı ve
/// <c>AdaPanel.Infrastructure.Audit</c> altında duruyordu. Ama ölçüldü ki asıl sızıntı kanalı
/// <see cref="AppException.Details"/>'tir ve <c>Details</c> Core'da üretilir: yanıt gövdesine,
/// ilerleme kaydına, denetim kaydına, DTO'lara ve MCP çıktısına oradan dağılır. Kural
/// Infrastructure'da kaldığı sürece <c>AppException</c> onu ÇAĞIRAMAZ (Core hiçbir projeye
/// referans vermez) ve maskeleme ~40 çağrı yerine dağıtılmak zorunda kalırdı. Bu depoda o hata
/// sınıfı defalarca ölçüldü: biri düzelir, öteki sessizce açık kalır. Bu yüzden desenler Core'a
/// taşındı; <c>AdaPanel.Infrastructure.Audit.AuditSecretRedactor</c> artık buraya devreden ince
/// bir cephedir (mevcut çağrı yerleri ve testleri kırılmasın diye adı korundu).
/// </para>
/// <para>
/// <b>Neden anahtar=değer kalıbı:</b> serbest metinden "hangi kelime paroladır" diye tahmin etmek
/// imkânsız ve zararlıdır — terminal komutunun kendisini de bozardı. Bunun yerine yalnızca açıkça
/// sır adı taşıyan bir alanın DEĞERİ maskelenir ("password=Abc123" → "password=[gizlendi]").
/// Anahtarın kendisi korunur ki kayıt "neyin değiştiği" bilgisini kaybetmesin.
/// </para>
/// <para>
/// <b>Neden fazla kapatmaya gidilmedi:</b> <c>Details</c> bu depoda MEŞRU teşhis de taşır — nginx
/// başlatma hatası, yedek geri yükleme uyarısı, bileşen indirme 404'ü. Teşhisi tümden kesmek
/// paneli kör eder; koruma SIRRA göre yapılır, bilgiye göre değil. Bunun ölçüsü testlerdeki
/// "fazla maskeleme nöbetçileri"dir.
/// </para>
/// </remarks>
public static class SecretRedactor
{
    /// <summary>Maskelenen değerin yerine yazılan sabit.</summary>
    public const string Mask = "[gizlendi]";

    /// <summary>Details sütunu 2048; kırpma payı bırakılır (ek "…" ve olası maskeleme için).</summary>
    public const int DetailsMaxLength = 2000;

    /// <summary>Action/Resource sütunları 128.</summary>
    public const int ShortFieldMaxLength = 128;

    // ---------------------------------------------------------------------------------------
    // 1) ANAHTAR = DEĞER
    // ---------------------------------------------------------------------------------------
    // Anahtar İKİ koldan tanınır ve ayrım BİLEREK yapılmıştır:
    //
    //   GEVŞEK KOL (uzun, tek anlamlı adlar): anahtarın ÖNÜNE ve ARKASINA ayraçsız harf gelebilir.
    //   ÖLÇÜLDÜ — eski desen yalnız ayraçla bölünmüş önekleri (`admin_password`) tanıyordu ve
    //   BİTİŞİK yazılan biçimleri TAM OLARAK kaçırıyordu:
    //       `PGPASSWORD=<parola>`     → KAÇIYORDU (ComponentBootstrapper postgres kurulumunda geçer)
    //       `SmtpPassword: <parola>`  → KAÇIYORDU (.NET özellik adları PascalCase BİTİŞİKTİR;
    //                                   bir ayar nesnesi metne dökülürse AdminPassword,
    //                                   SourcePassword, WebhookToken hepsi aynı sebeple kaçardı)
    //   Bu, tek bir kelimeyi değil bütün bir BİÇİMİ kaçırmaktı.
    //
    //   KATI KOL (kısa, çok anlamlı adlar: pass/pwd/sig/bearer): bunlar başka kelimelerin İÇİNDE
    //   geçer — `Bypass=true`, `compass`, `MaxPasses=3`. Gevşek kola konsalardı meşru teşhis
    //   maskelenirdi. Bu yüzden onlar ayraçla bölünmüş önek/sonek kuralında BIRAKILDI.
    //
    // NEDEN {0,64} SINIRI: `[...]*keyword[...]*` biçimi, eşleşmeyen uzun bir dizede (8000 karakterlik
    // bir base64/hex çıktı) her başlangıç noktası için geri izleme yapar — O(n²). Details 8000
    // karaktere kadar çıkabildiği için bu, 1 saniyelik zaman aşımını gerçekten tetikleyebilirdi;
    // zaman aşımı da (aşağıya bakınız) metnin TAMAMINI atar. Gerçek bir anahtar adı 64 karakteri
    // aşmaz, sınır ölçümü değil yalnız geri izlemeyi kesiyor.
    //
    // NEDEN DEĞER KISMINDA "şema + değer" seçeneği: `authorization: Bearer eyJ...` biçiminde değer
    // İKİ parçadır. Yalnız `\S+` alınsaydı maskelenen parça "Bearer" olur, jetonun kendisi kayda
    // OLDUĞU GİBİ düşerdi — maskeleme var görünüp tam da korunacak şeyi bırakırdı.
    private const string LooseKeyWords =
        "password|passwd|parola|sifre|şifre|secret|gizli|token|jeton|api[_.\\-]?key|access[_.\\-]?key|connection[_.\\-]?string|credential|authorization|signature";

    private const string StrictKeyWords = "pass|pwd|sig|bearer";

    private static readonly Regex SecretAssignment = new(
        $$"""
        (?<![A-Za-z0-9_.\-])
        (?<key>
            [A-Za-z0-9_.\-]{0,64}?(?:{{LooseKeyWords}})[A-Za-z0-9_.\-]{0,64}
          | (?:[A-Za-z0-9]+[_.\-])*(?:{{StrictKeyWords}})s?(?:[_.\-][A-Za-z0-9]+)*
        )
        \s*(?<sep>[:=])\s*
        (?<value>"[^"]*"|'[^']*'|(?:Bearer|Basic|Digest|Negotiate|Token|ApiKey)\s+\S+|\S+)
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    // ---------------------------------------------------------------------------------------
    // 2) ANAHTARSIZ KİMLİK ŞEMASI: `Bearer eyJhbGciOi...`
    // ---------------------------------------------------------------------------------------
    // NEDEN AYRI: ayraç burada BOŞLUKTUR. Boşluğu genel ayraç listesine eklemek felaket olurdu —
    // "password was changed" gibi İNSAN CÜMLELERİNDE bir sonraki kelimeyi maskeleyip kaydı
    // okunamaz hâle getirirdi (kaydın var olma sebebi okunabilir olması). Bu yüzden boşluk yalnız
    // BİLİNEN kimlik şemalarının ardından, ve yalnız ardından gelen parça GERÇEKTEN bir kimlik
    // bilgisine benziyorsa (jeton alfabesi, en az 16 karakter) sır sayılır.
    private static readonly Regex SchemeCredential = new(
        """(?<![A-Za-z0-9])(?<scheme>Bearer|Basic|Digest|Negotiate)\s+(?<value>[A-Za-z0-9\-._~+/]{16,}={0,2})""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    // ---------------------------------------------------------------------------------------
    // 3) ADRESE GÖMÜLÜ KİMLİK: `scheme://kullanici:parola@konak`
    // ---------------------------------------------------------------------------------------
    // ÖLÇÜLDÜ — hiçbir desen bunu tanımıyordu, oysa panelin ÜRETTİĞİ biçimlerden biri:
    //   `https://user:ghp_xxx@github.com/o/r.git`  (Git dağıtım adresi, denetim kaydına düz yazılır)
    //   `ftp://kullanici:parola@yedek.example.com` (yedek hedefi)
    //   `postgresql://ada:parola@127.0.0.1:5432/db`
    // KULLANICI ADI KORUNUR: "hangi kimlikle denendi" teşhisin kendisidir; sır olan paroladır.
    // Bir URL'de `user:pass@` parçası HER ZAMAN kimlik bilgisidir — burada fazla maskeleme riski
    // yoktur, bu yüzden kural konağa bakmadan geneldir.
    private static readonly Regex UriUserInfo = new(
        """(?<scheme>[A-Za-z][A-Za-z0-9+.\-]{1,15})://(?<user>[^\s:/@]{1,128}):(?<secret>[^\s/@]{1,256})@""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    // ---------------------------------------------------------------------------------------
    // 4) BİLİNEN WEBHOOK ADRESLERİ: sır YOLUN İÇİNDEDİR
    // ---------------------------------------------------------------------------------------
    // Slack/Discord/Teams/Google Chat webhook'ları böyle çalışır: adresin yolu tek başına
    // "bu odaya mesaj gönder" yetkisi verir, yani parolaya eşdeğerdir. Şema ve KONAK korunur —
    // kayıt "uyarılar Slack'e gidiyordu" bilgisini kaybetmesin diye; giden yalnız yol/sorgudur.
    //
    // NEDEN KONAK LİSTESİ (her URL değil): `Url=https://downloads.mariadb.org/.../mariadb.msi`
    // biçimindeki indirme teşhisi de bir URL'dir ve yolu ATILIRSA teşhis değerini tümden
    // kaybeder ("bir şey indirilemedi" — ne indirilemedi?). Genel bir "her URL yolunu sil" kuralı
    // fazla kapatma olurdu. Kendi alanı gereği sır taşıyan adresler için ayrıca
    // <see cref="RedactUrl"/> vardır ve webhook alanını YAZAN yer onu çağırır.
    private static readonly Regex KnownWebhookUrl = new(
        """(?<prefix>https?://(?:hooks\.slack\.com|discord(?:app)?\.com/api/webhooks|chat\.googleapis\.com|outlook\.office(?:365)?\.com/webhook|[A-Za-z0-9.\-]{0,64}\.webhook\.office\.com))(?<rest>/[^\s'"<>]{1,512})""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    // ---------------------------------------------------------------------------------------
    // 5) PEM ÖZEL ANAHTAR BLOĞU
    // ---------------------------------------------------------------------------------------
    // Panel Stalwart TLS özel anahtarını dosyaya yazar; bir aracın hata çıktısı anahtarı geri
    // basarsa (dış araçlar bunu yapar) bloğun tamamı Details'e düşerdi. Blok başlığı tek anlamlıdır,
    // maskelemek meşru hiçbir teşhisi bozmaz — başlık/son satır korunur ki "burada bir anahtar
    // vardı" görünsün.
    private static readonly Regex PemPrivateKey = new(
        """-----BEGIN (?<tur>[A-Z0-9 ]{0,32}?)PRIVATE KEY-----(?s:.){1,20000}?-----END [A-Z0-9 ]{0,32}?PRIVATE KEY-----""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    /// <summary>Sır değerlerini maskeler ve Details uzunluğuna kırpar.</summary>
    public static string SanitizeDetails(string? value) =>
        Truncate(Redact(value), DetailsMaxLength);

    /// <summary>Action/Resource gibi kısa alanlar için: maskeler ve 128 karaktere kırpar.</summary>
    public static string SanitizeShortField(string? value) =>
        Truncate(Redact(value), ShortFieldMaxLength);

    /// <summary>Yalnız maskeleme (kırpma yok) — test ve yeniden kullanım için ayrı durur.</summary>
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        try
        {
            // SIRA ÖNEMLİ: önce blok/adres biçimleri, sonra anahtar=değer. PEM bloğu içindeki
            // satırlar anahtar=değer desenine takılıp bloğu parça parça bozmasın.
            var maskeli = PemPrivateKey.Replace(value, match =>
                $"-----BEGIN {match.Groups["tur"].Value}PRIVATE KEY----- {Mask} -----END PRIVATE KEY-----");

            maskeli = UriUserInfo.Replace(maskeli, match =>
                $"{match.Groups["scheme"].Value}://{match.Groups["user"].Value}:{Mask}@");

            maskeli = KnownWebhookUrl.Replace(maskeli, match =>
                $"{match.Groups["prefix"].Value}/{Mask}");

            maskeli = SecretAssignment.Replace(maskeli, match =>
                $"{match.Groups["key"].Value}{match.Groups["sep"].Value}{Mask}");

            // Anahtarsız "Bearer <jeton>" biçimi son geçişte yakalanır. Şema ADI korunur ki
            // kayıt "burada bir kimlik bilgisi vardı" bilgisini kaybetmesin.
            return SchemeCredential.Replace(maskeli, match =>
                $"{match.Groups["scheme"].Value} {Mask}");
        }
        catch (RegexMatchTimeoutException)
        {
            // ÖLÇEMEDİK durumu: metni maskeleyemediysek olduğu gibi geçirmek, tam da korumak
            // istediğimiz şeyi sızdırmak olurdu. Kaydı tamamen kaybetmemek için içerik atılır,
            // kaydın kendisi (kim/ne zaman/hangi eylem) korunur.
            return Mask;
        }
    }

    /// <summary>
    /// ALANI GEREĞİ sır taşıyan bir adresi maskeler: şema + konak (+ port) KALIR, yol ve sorgu GİDER.
    /// </summary>
    /// <remarks>
    /// <see cref="KnownWebhookUrl"/> yalnız TANINAN sağlayıcıları yakalar; kendi sunucusuna webhook
    /// kuran bir yönetici için konak bilinmez ama alan yine bir webhook'tur ve yolu yine sırdır.
    /// Bu yüzden webhook adresini KAYDA YAZAN yer bu metodu çağırır: "bu dizge bir webhook'tur"
    /// bilgisi yalnız orada vardır, desenle tahmin edilemez.
    /// <para>
    /// Kimlik gömülü adres (<c>https://kullanici:parola@konak/...</c>) de burada temizlenir:
    /// <see cref="Uri.Authority"/> kullanıcı bilgisini İÇERMEZ.
    /// </para>
    /// </remarks>
    public static string RedactUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            // Adres çözülemedi: dizge yine de sır taşıyor olabilir, genel maskelemeye düşür.
            return Redact(url);
        }

        var yolVar = uri.AbsolutePath.Length > 1 || !string.IsNullOrEmpty(uri.Query);
        return yolVar
            ? $"{uri.Scheme}://{uri.Authority}/{Mask}"
            : $"{uri.Scheme}://{uri.Authority}/";
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
