namespace AdaPanel.Core.Dtos.Htaccess;

/// <summary>
/// Ayrıştırılmış bir direktif satırı: <c>RewriteRule ^index\.php$ - [L]</c>.
/// </summary>
/// <remarks>
/// Argümanlar Apache'nin yaptığı gibi TIRNAK ÇÖZÜLMÜŞ hâlde tutulur; <c>RawLine</c> ise
/// kullanıcının yazdığı satırın kendisidir. Panel hata gösterirken ham satırı, motor
/// değerlendirirken çözülmüş argümanları kullanır.
/// </remarks>
public sealed class HtaccessDirective
{
    /// <summary>Direktif adı, kullanıcının yazdığı harf durumuyla (<c>RewriteRule</c>).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Tırnakları çözülmüş argümanlar.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>1'den başlayan satır numarası (devam satırı varsa BAŞLADIĞI satır).</summary>
    public int Line { get; init; }

    /// <summary>Kullanıcının yazdığı satır — devam satırları birleştirilmiş hâliyle.</summary>
    public string RawLine { get; init; } = string.Empty;

    /// <summary>
    /// Bu direktifi çevreleyen bloklar, DIŞTAN İÇE. <c>&lt;IfModule&gt;</c> içindeki bir
    /// <c>&lt;Files&gt;</c> için <c>[IfModule, Files]</c>.
    /// </summary>
    public IReadOnlyList<HtaccessSectionRef> Sections { get; init; } = [];

    public string Argument(int index) =>
        index >= 0 && index < Arguments.Count ? Arguments[index] : string.Empty;

    public override string ToString() => RawLine;
}

/// <summary>Bir direktifi çevreleyen blok hakkındaki BİLGİ (blok ağacının kendisi değil).</summary>
public sealed class HtaccessSectionRef
{
    /// <summary><c>IfModule</c>, <c>Files</c>, <c>FilesMatch</c>, <c>Directory</c>, ...</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Blok argümanı: <c>mod_rewrite.c</c>, <c>"\.(gif|jpg)$"</c>, <c>/var/www/x</c>.</summary>
    public string Argument { get; init; } = string.Empty;

    /// <summary><c>&lt;IfModule !mod_rewrite.c&gt;</c> — argümanın başındaki <c>!</c>.</summary>
    public bool Negated { get; init; }

    public int Line { get; init; }

    public override string ToString() => Negated ? $"<{Name} !{Argument}>" : $"<{Name} {Argument}>";
}

/// <summary>
/// Tek bir <c>.htaccess</c> dosyasının ayrıştırma sonucu.
/// </summary>
/// <remarks>
/// <b>İSTİSNA FIRLATILMAZ.</b> Ayrıştırıcı her koşulda bir sonuç döndürür; okunamayan dosya,
/// kapanmayan blok, boyut sınırı — hepsi <see cref="Diagnostics"/> içinde bildirilir ve
/// <see cref="Usable"/> ile "bu dosya uygulanabilir mi" sorusu tek yerden cevaplanır.
/// </remarks>
public sealed class HtaccessParseResult
{
    /// <summary>Ayrıştırılan dosyanın tam yolu. Bellekten ayrıştırma yapıldıysa boş olabilir.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>
    /// Bu dosyanın kapsadığı URL yolu (site köküne göre, başında ve sonunda <c>/</c>).
    /// Kök dizindeki dosya için <c>/</c>, <c>blog/</c> içindeki dosya için <c>/blog/</c>.
    /// mod_rewrite'ın "dizin ön eki" (per-directory prefix) budur.
    /// </summary>
    public string VirtualPath { get; init; } = "/";

    /// <summary>Sırası KORUNMUŞ direktif listesi. Blok bilgisi her direktifin üzerinde taşınır.</summary>
    public IReadOnlyList<HtaccessDirective> Directives { get; init; } = [];

    public IReadOnlyList<HtaccessDiagnostic> Diagnostics { get; init; } = [];

    public long SizeBytes { get; init; }

    /// <summary>
    /// Dosya bir bütün olarak uygulanabilir mi? <see cref="HtaccessDiagnosticLevel.Fatal"/>
    /// bir bulgu varsa <c>false</c>. Apache böyle bir dosyada 500 döner; motor karar vermez,
    /// yalnız durumu bildirir — isteği bozma kararı barındırma katmanınındır.
    /// </summary>
    public bool Usable => !Diagnostics.Any(t => t.Level == HtaccessDiagnosticLevel.Fatal);

    public bool HasUnsupported => Diagnostics.Any(t => t.Level == HtaccessDiagnosticLevel.Unsupported);
}
