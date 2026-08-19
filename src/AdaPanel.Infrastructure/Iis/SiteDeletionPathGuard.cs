using AdaPanel.Core.Common;

namespace AdaPanel.Infrastructure.Iis;

/// <summary>Bir klasörün ÖZYİNELEMELİ olarak silinip silinemeyeceğinin kararı.</summary>
public enum SiteDeletionPathVerdict
{
    /// <summary>Silinebilir: sitenin kendi klasörü olduğu KANITLANDI.</summary>
    Deletable,

    /// <summary>Yol okunamadı / göreli / biçimsel olarak geçersiz.</summary>
    InvalidPath,

    /// <summary>Ağ paylaşımı (UNC) ya da cihaz yolu. Sunucunun diski değil, başkasının verisi olabilir.</summary>
    NetworkPath,

    /// <summary>Sürücü kökü ya da kökün kendisi kadar sığ bir yol.</summary>
    TooShallow,

    /// <summary>Windows, Program Files, ProgramData, panel veri kökü gibi sistem alanı.</summary>
    SystemPath,

    /// <summary>Yolun kendisi bir SİTE KÖKÜ (kapsayıcı): altında bütün siteler yaşar, site klasörü değildir.</summary>
    SiteRootItself,

    /// <summary>Panelin tanıdığı hiçbir site kökünün altında değil ve kendi başına bir site klasörü olduğu KANITLANAMADI.</summary>
    UnknownRoot,

    /// <summary>Yol BAŞKA bir sitenin klasörünü kapsıyor (ya da onunla aynı).</summary>
    SharedWithAnotherSite
}

/// <summary>
/// Silme kararının girdileri. Hepsi ÖLÇÜLEN değerlerdir: panel tercihleri, IIS'in site listesi
/// ve işletim sisteminin kendi klasör yolları. Sabit dizgi listesi tutulmaz.
/// </summary>
public sealed record SiteDeletionPathPolicy
{
    /// <summary>Panelin site kökleri (varsayılan site kökü + IIS'in kendi kökü). Bunların ALTI meşrudur, KENDİLERİ değil.</summary>
    public IReadOnlyCollection<string> KnownSiteRoots { get; init; } = [];

    /// <summary>Kendisi ve ALTI yasak (Windows, Program Files, ProgramData, panel veri kökü).</summary>
    public IReadOnlyCollection<string> ForbiddenSubtrees { get; init; } = [];

    /// <summary>Kendisi yasak ve kapsayıcı olarak da kullanılamaz (C:\, C:\Users, C:\inetpub, C:\inetpub\wwwroot).</summary>
    public IReadOnlyCollection<string> ForbiddenContainers { get; init; } = [];

    /// <summary>BAŞKA sitelerin fiziksel yolları / uygulama kökleri (Y8: paylaşılan klasör silinmez).</summary>
    public IReadOnlyCollection<string> OtherSitePaths { get; init; } = [];

    /// <summary>
    /// Sitenin ADI ve bağlama host adları. Klasör adı bunlardan biriyle birebir aynıysa klasör o
    /// siteye ADANMIŞ sayılır — tek seviyelik yollarda (ör. <c>D:\musteri.com</c>) meşru silmeyi
    /// açan POZİTİF kanıt budur. Kanıt yalnızca kapıyı GENİŞLETİR; yasak listelerini asla açmaz.
    /// </summary>
    public IReadOnlyCollection<string> DedicatedFolderNames { get; init; } = [];
}

/// <summary>
/// Site silmede "bu klasörü özyinelemeli silebilir miyim?" sorusunun TEK adresi.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN VAR (ölçüldü, 19.08.2026 denetimi K9):</b> eski kural bir KARA LİSTEYDİ — yalnız
/// <c>%WINDIR%</c> ve iki segmentlik <c>Program Files</c> yollarını eliyordu. Fonksiyonun birebir
/// kopyası çalıştırıldığında <c>C:\inetpub</c>, <c>C:\Users</c>, <c>C:\ProgramData</c> ve
/// <c>\\sunucu\pay</c> <b>GEÇİYORDU</b>. Yani fiziksel yolu <c>C:\inetpub\wwwroot</c> olan tek bir
/// site, <c>?deleteFiles=true</c> ile sunucudaki BÜTÜN siteleri götürüyor ve panel <c>200 OK</c>
/// dönüyordu. Kara liste her zaman eksiktir: unutulan her yol açık kalır.
/// </para>
/// <para>
/// <b>Yeni kural BEYAZ LİSTEDİR:</b> silinecek yol, panelin TANIDIĞI bir site kökünün ALTINDA
/// olmalı — ya da kendi başına o siteye adanmış bir klasör olduğu kanıtlanmalı. Kapsama sorusu
/// <see cref="PathContainment"/> ile sorulur; "C:\sites\site1" ile "C:\sites\site1-evil" ön ek
/// olarak eşleştiği için düz <c>StartsWith</c> kullanılmaz.
/// </para>
/// <para>
/// <b>Fazla dar olmamak da bir gerekliliktir:</b> panel meşru bir siteyi silemezse yönetici işi
/// elle yapar ve bir dahaki sefere onay ekranını okumadan tıklar. Bu yüzden kapı üç ayrı yoldan
/// açılır ve üçü de POZİTİF kanıta dayanır:
/// </para>
/// <list type="number">
/// <item>yol, panelin bildiği bir site kökünün altındadır (panel kendi açtığı her site için budur);</item>
/// <item>üst klasörü, kök seviyesinde olmayan ve yasak olmayan bir kapsayıcıdır (<c>D:\web\site1</c>);</item>
/// <item>klasörün adı sitenin adı ya da bir host adıdır (<c>D:\musteri.com</c>) — ADANMIŞLIK kanıtı.</item>
/// </list>
/// <para>
/// <b>Y8 aynı kapıdadır:</b> yol başka bir sitenin klasörünü kapsıyorsa silinmez. Havuz paylaşımı
/// zaten denetleniyordu, fiziksel yol paylaşımı hiç sorulmuyordu.
/// </para>
/// </remarks>
public static class SiteDeletionPathGuard
{
    /// <summary>
    /// Kendisi ve ALTI silinemez alanlar. Yollar ORTAMDAN okunur (sabit "C:\Windows" yazılmaz):
    /// bu depoda <c>GetFolderPath</c>'in küçük harf döndürmesi yüzünden sessizce ölmüş üç özellik
    /// yaşandı; karşılaştırmaların tamamı bu yüzden <see cref="StringComparison.OrdinalIgnoreCase"/>.
    /// </summary>
    /// <param name="panelDataRoot">Panelin veri kökü (yedekler, veritabanı, anahtarlar). Taşınmış olabilir.</param>
    public static IReadOnlyCollection<string> SystemForbiddenSubtrees(string? panelDataRoot = null)
    {
        var yollar = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            // ProgramData: panelin veritabanı, anahtarları, sertifikaları VE yedekleri burada.
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            panelDataRoot
        };

        return Normalize(yollar);
    }

    /// <summary>
    /// Kendisi silinemez VE kapsayıcı olarak kullanılamaz alanlar. Bunların ALTI yasak değildir:
    /// <c>C:\inetpub\wwwroot\musteri.com</c> meşru bir site klasörüdür, <c>C:\inetpub\wwwroot</c> değildir.
    /// </summary>
    public static IReadOnlyCollection<string> SystemForbiddenContainers()
    {
        var sistemSurucusu = SystemDrive();
        var kullaniciKoku = PathContainment.Normalize(
            Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

        var yollar = new List<string?>
        {
            Path.Combine(sistemSurucusu, "Users"),
            kullaniciKoku,
            // IIS'in kendi kökü: hem "inetpub" hem de altındaki "wwwroot" BİRER KAPSAYICIDIR.
            // wwwroot ayrıca panelin varsayılan site köküdür; kapsayıcı olması onu meşru bir
            // site kökü olmaktan çıkarmaz — KnownSiteRoots ayrı bir listedir ve ALTINI açar.
            Path.Combine(sistemSurucusu, "inetpub"),
            Path.Combine(sistemSurucusu, "inetpub", "wwwroot")
        };

        return Normalize(yollar);
    }

    /// <summary>IIS'in kurulum varsayılanı olan site kökü — panel tercihleri okunamasa bile bilinir.</summary>
    public static string DefaultIisSiteRoot() => Path.Combine(SystemDrive(), "inetpub", "wwwroot");

    /// <summary>
    /// Yol, üzerinde ÖZYİNELEMELİ silme yapılabilecek bir site klasörü mü?
    /// </summary>
    public static SiteDeletionPathVerdict Evaluate(string? candidate, SiteDeletionPathPolicy policy)
    {
        var yol = PathContainment.Normalize(candidate);
        if (yol is null)
        {
            return SiteDeletionPathVerdict.InvalidPath;
        }

        // UNC (\\sunucu\pay) ve cihaz yolları (\\?\, \\.\): ÖLÇÜLDÜ, eski koruma bunları geçiriyordu.
        // Ağdaki bir paylaşım bu sunucunun diski değildir; oradaki veri panelin sorumluluğunda değil.
        if (!IsLocalDrivePath(yol))
        {
            return SiteDeletionPathVerdict.NetworkPath;
        }

        if (SegmentsBelowRoot(yol) < 1)
        {
            return SiteDeletionPathVerdict.TooShallow;
        }

        if (IsInsideAny(policy.ForbiddenSubtrees, yol))
        {
            return SiteDeletionPathVerdict.SystemPath;
        }

        // Site KÖKÜNÜN KENDİSİ hiçbir zaman bir site klasörü değildir. K9'un tam olarak
        // sömürdüğü durum budur: fiziksel yolu site köküne eşit olan bir site.
        // Sıra bilinçli: aynı yol hem "site kökü" hem "yasak kapsayıcı" olabilir
        // (C:\inetpub\wwwroot) ve yöneticiye anlatılması gereken sebep BİRİNCİSİDİR.
        if (EqualsAny(policy.KnownSiteRoots, yol))
        {
            return SiteDeletionPathVerdict.SiteRootItself;
        }

        if (EqualsAny(policy.ForbiddenContainers, yol))
        {
            return SiteDeletionPathVerdict.SystemPath;
        }

        if (!IsInsideAny(policy.KnownSiteRoots, yol, includeRootItself: false) &&
            !CanContainSites(Path.GetDirectoryName(yol), policy) &&
            !IsDedicatedFolder(yol, policy))
        {
            return SiteDeletionPathVerdict.UnknownRoot;
        }

        // Y8: klasör BAŞKA bir siteye de ait mi? Havuz paylaşımı denetleniyordu, yol paylaşımı hiç.
        foreach (var baskaSite in policy.OtherSitePaths)
        {
            if (PathContainment.IsRootOrInside(yol, baskaSite))
            {
                return SiteDeletionPathVerdict.SharedWithAnotherSite;
            }
        }

        return SiteDeletionPathVerdict.Deletable;
    }

    /// <summary>
    /// Bu klasör BİRDEN ÇOK siteyi barındırabilecek bir kapsayıcı mı? Kapsayıcıysa altındaki bir
    /// klasörü silmek güvenlidir; kapsayıcının KENDİSİNİ silmek değildir.
    /// </summary>
    /// <remarks>
    /// <b>SÜRÜCÜ KÖKÜ KAPSAYICI SAYILMAZ</b> ve bunun tek amacı vardır: sürücünün hemen altındaki
    /// bir klasörün (<c>D:\Yedekler</c>) "sitenin kökü" diye özyinelemeli silinmesini engellemek.
    /// Böyle bir klasör sunucunun ortak veri klasörü olabilir ve bu tam olarak K9'un sınıfıdır.
    /// Meşru olduğu durum, klasör adının siteye ait olduğunun kanıtlandığı durumdur
    /// (<see cref="SiteDeletionPathPolicy.DedicatedFolderNames"/>).
    /// <para>
    /// Buna karşılık <c>D:\web</c> gibi tek seviyelik bir klasör PEKÂLÂ kapsayıcıdır: altındaki
    /// <c>D:\web\musteri.com</c> silinebilir. Kural kapsayıcının derinliğine değil, kapsayıcının
    /// SÜRÜCÜ KÖKÜ OLMAMASINA bakar.
    /// </para>
    /// </remarks>
    public static bool CanContainSites(string? directory, SiteDeletionPathPolicy policy)
    {
        var yol = PathContainment.Normalize(directory);
        if (yol is null || !IsLocalDrivePath(yol) || SegmentsBelowRoot(yol) < 1)
        {
            return false;
        }

        return !IsInsideAny(policy.ForbiddenSubtrees, yol) && !EqualsAny(policy.ForbiddenContainers, yol);
    }

    /// <summary>
    /// Belge kökü bir seviye YUKARI taşınabilir mi (Laravel <c>public</c> kalıbı)?
    /// </summary>
    /// <remarks>
    /// Ayrı bir metottur çünkü soru farklıdır: burada "kapsayıcı mı" değil, "uygulama kökü OLABİLİR
    /// mi" sorulur. <c>D:\laravel\public</c> için üst klasör <c>D:\laravel</c> meşru bir uygulama
    /// köküdür (tek seviye olması sorun değil), ama <c>C:\inetpub\wwwroot\public</c> için üst klasör
    /// bütün sitelerin kökü olurdu — ve cascade önce uygulama kökünü siler.
    /// </remarks>
    public static bool IsUsableApplicationRoot(string? directory, SiteDeletionPathPolicy policy)
    {
        var yol = PathContainment.Normalize(directory);
        if (yol is null || !IsLocalDrivePath(yol) || SegmentsBelowRoot(yol) < 1)
        {
            return false;
        }

        return !IsInsideAny(policy.ForbiddenSubtrees, yol)
               && !EqualsAny(policy.ForbiddenContainers, yol)
               && !EqualsAny(policy.KnownSiteRoots, yol);
    }

    /// <summary>Kullanıcıya gösterilecek GERÇEK sebep — "güvenlik nedeniyle atlandı" tek başına hiçbir şey anlatmıyordu.</summary>
    public static string Describe(SiteDeletionPathVerdict verdict) => verdict switch
    {
        SiteDeletionPathVerdict.Deletable => "silinebilir",
        SiteDeletionPathVerdict.InvalidPath => "yol okunamadı ya da göreli",
        SiteDeletionPathVerdict.NetworkPath => "ağ paylaşımı / cihaz yolu (bu sunucunun diski değil)",
        SiteDeletionPathVerdict.TooShallow => "sürücü kökü kadar sığ",
        SiteDeletionPathVerdict.SystemPath => "sistem klasörü (Windows / Program Files / ProgramData / panel verisi)",
        SiteDeletionPathVerdict.SiteRootItself => "bu yol bir SİTE KÖKÜ: altında başka sitelerin klasörleri var",
        SiteDeletionPathVerdict.UnknownRoot => "panelin tanıdığı bir site kökünün altında değil ve siteye ait olduğu doğrulanamadı",
        SiteDeletionPathVerdict.SharedWithAnotherSite => "klasör BAŞKA bir siteye de ait",
        _ => verdict.ToString()
    };

    private static bool IsDedicatedFolder(string path, SiteDeletionPathPolicy policy)
    {
        var klasorAdi = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(klasorAdi))
        {
            return false;
        }

        return policy.DedicatedFolderNames.Any(ad =>
            !string.IsNullOrWhiteSpace(ad) &&
            string.Equals(ad.Trim(), klasorAdi, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInsideAny(IEnumerable<string> roots, string path, bool includeRootItself = true) =>
        roots.Any(root => includeRootItself
            ? PathContainment.IsRootOrInside(root, path)
            : PathContainment.IsInsideRoot(root, path));

    private static bool EqualsAny(IEnumerable<string> roots, string path) =>
        roots.Any(root => string.Equals(
            PathContainment.Normalize(root)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase));

    /// <summary>Yerel bir sürücü yolu mu (<c>X:\...</c>)? UNC ve cihaz yolları buradan geçmez.</summary>
    private static bool IsLocalDrivePath(string normalizedPath)
    {
        if (normalizedPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        var kok = Path.GetPathRoot(normalizedPath);
        return kok is { Length: 3 } && kok[1] == Path.VolumeSeparatorChar;
    }

    /// <summary>Sürücü kökünün ALTINDAKİ segment sayısı: <c>C:\</c> → 0, <c>C:\a</c> → 1, <c>C:\a\b</c> → 2.</summary>
    private static int SegmentsBelowRoot(string normalizedPath)
    {
        var kok = Path.GetPathRoot(normalizedPath) ?? string.Empty;
        return normalizedPath[kok.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Length;
    }

    private static string SystemDrive()
    {
        var surucu = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        return string.IsNullOrWhiteSpace(surucu) ? @"C:\" : surucu;
    }

    private static IReadOnlyCollection<string> Normalize(IEnumerable<string?> paths) =>
        paths
            .Select(PathContainment.Normalize)
            .Where(yol => !string.IsNullOrWhiteSpace(yol))
            .Select(yol => yol!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
