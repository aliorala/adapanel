namespace AdaPanel.Core.Dtos.Htaccess;

/// <summary>Bir <c>.htaccess</c> bulgusunun ağırlığı.</summary>
/// <remarks>
/// Bu depodaki en pahalı hata sınıfı "çalışıyor gibi görünüp çalışmamak"tır. Motorun Apache'nin
/// tamamını karşılaması imkânsız olduğu için, karşılayamadığı her noktayı SUSMAK yerine buradan
/// bildirir; panel bunu kullanıcıya olduğu gibi gösterir.
/// </remarks>
public enum HtaccessDiagnosticLevel
{
    /// <summary>Bilgi: davranış Apache ile aynı, kullanıcı yalnızca haberdar ediliyor.</summary>
    Info,

    /// <summary>Direktif/bayrak TANINDI ama motor onu uygulamıyor. Kural devre dışıdır.</summary>
    Unsupported,

    /// <summary>Uygulanıyor, ancak Apache'den FARKLI davranabilir. Sebebi mesajda yazar.</summary>
    Warning,

    /// <summary>Söz dizimi hatası: yalnızca o satır/direktif uygulanmaz.</summary>
    Error,

    /// <summary>Dosya bir bütün olarak kullanılamaz (kapanmayan blok, boyut sınırı, okunamadı).</summary>
    Fatal
}

/// <summary>
/// Bulgu kodları. Panel ve testler METNE değil KODA bakar; mesaj metni çeviri/iyileştirmeyle
/// değişebilir, kod değişmez.
/// </summary>
public static class HtaccessDiagnosticCodes
{
    public const string UnknownDirective = "BILINMEYEN_DIREKTIF";
    public const string UnsupportedDirective = "DESTEKLENMEYEN_DIREKTIF";
    public const string UnsupportedSection = "DESTEKLENMEYEN_BLOK";
    public const string UnsupportedFlag = "DESTEKLENMEYEN_BAYRAK";
    public const string UnsupportedVariable = "DESTEKLENMEYEN_DEGISKEN";
    public const string UnsupportedCondition = "DESTEKLENMEYEN_KOSUL";
    public const string UnclosedSection = "KAPANMAYAN_BLOK";
    public const string UnexpectedSectionEnd = "BEKLENMEYEN_BLOK_SONU";
    public const string MismatchedSectionEnd = "ESLESMEYEN_BLOK_SONU";
    public const string UnterminatedQuote = "KAPANMAYAN_TIRNAK";
    public const string MissingArgument = "EKSIK_ARGUMAN";
    public const string TooManyArguments = "FAZLA_ARGUMAN";
    public const string InvalidArgument = "GECERSIZ_ARGUMAN";
    public const string InvalidRegex = "GECERSIZ_DUZENLI_IFADE";
    public const string RiskyRegex = "RISKLI_DUZENLI_IFADE";
    public const string RegexTimeout = "DUZENLI_IFADE_ZAMAN_ASIMI";
    public const string FileTooLarge = "DOSYA_COK_BUYUK";
    public const string TooManyDirectives = "COK_FAZLA_DIREKTIF";
    public const string LineTooLong = "SATIR_COK_UZUN";
    public const string FileUnreadable = "DOSYA_OKUNAMADI";
    public const string PathEscapesSiteRoot = "YOL_SITE_KOKUNUN_DISINDA";
    public const string RuleBudgetExceeded = "KURAL_BUTCESI_ASILDI";
    public const string RecursionBudgetExceeded = "IC_DONGU_BUTCESI_ASILDI";
    public const string TimeBudgetExceeded = "SURE_BUTCESI_ASILDI";
    public const string CascadeDepthExceeded = "BASAMAK_DERINLIGI_ASILDI";
    public const string CondWithoutRule = "KURALSIZ_KOSUL";
    public const string RewriteEngineOff = "REWRITE_MOTORU_KAPALI";
    public const string ApacheWouldReject = "APACHE_REDDEDERDI";
    public const string MixedAccessSyntax = "KARISIK_ERISIM_SOZ_DIZIMI";
    public const string UnsupportedHashScheme = "DESTEKLENMEYEN_PAROLA_BICIMI";
    public const string AuthFileUnreadable = "PAROLA_DOSYASI_OKUNAMADI";
    public const string AuthIncomplete = "EKSIK_KIMLIK_DOGRULAMA_AYARI";
}

/// <summary>Tek bir bulgu: hangi dosyanın hangi satırında, ne, neden.</summary>
public sealed class HtaccessDiagnostic
{
    public HtaccessDiagnosticLevel Level { get; init; }

    /// <summary><see cref="HtaccessDiagnosticCodes"/> içindeki sabitlerden biri.</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>Bulgunun ait olduğu <c>.htaccess</c> dosyasının yolu (basamaklandırmada hangisi olduğu).</summary>
    public string File { get; init; } = string.Empty;

    /// <summary>1'den başlayan satır numarası. Bilinmiyorsa 0.</summary>
    public int Line { get; init; }

    /// <summary>Direktif ya da blok adı (<c>RewriteRule</c>, <c>IfModule</c>).</summary>
    public string Directive { get; init; } = string.Empty;

    /// <summary>Kullanıcının okuyacağı açıklama. NEDEN'i içerir, "desteklenmiyor" demekle yetinmez.</summary>
    public string Message { get; init; } = string.Empty;

    public override string ToString() =>
        $"{Level} {Code} {File}:{Line} {Directive} — {Message}";
}
