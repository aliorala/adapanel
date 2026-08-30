using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace AdaPanel.Infrastructure.Support;

/// <summary>
/// Sır taşıyan GEÇİCİ dosyaları (betik, JSON yükü, döküm) kiracıya KAPALI olarak oluşturur.
/// </summary>
/// <remarks>
/// <para>
/// <b>KÖK NEDEN — bu makinede ÖLÇÜLDÜ, varsayım değil.</b> Panel servisi LocalSystem olarak koşar;
/// <c>Path.GetTempPath()</c> onun için <c>C:\Windows\TEMP\</c> döner (ölçüldü). O klasörün DACL'i
/// Windows'un varsayılanıdır ve içinde şu ACE vardır:
/// </para>
/// <code>
/// BUILTIN\IIS_IUSRS | ReadData, Synchronize | ContainerInherit, ObjectInherit | Propagation=None
/// </code>
/// <para>
/// İki sonucu birden var: (1) <c>ObjectInherit</c> olduğu için oraya yazılan HER DOSYA bu ACE'i
/// MİRAS ALIR; (2) ACE <c>InheritOnly</c> DEĞİL, yani klasörün kendisine de uygulanır ve dizin
/// nesnesinde <c>ReadData</c> = <c>FILE_LIST_DIRECTORY</c> demektir. Yani IIS_IUSRS klasörü
/// LİSTELEYEBİLİR ve içindeki dosyaları OKUYABİLİR — dosya adındaki GUID'i tahmin etmesine bile
/// gerek yoktur.
/// </para>
/// <para>
/// IIS_IUSRS, barındırılan HER sitenin uygulama havuzu kimliğini kapsar. Yani panel bir posta
/// hesabı parolasını, Stalwart TLS özel anahtarını ya da çözülmüş bir yedeği geçici dosyaya
/// yazdığı sürece, o makinedeki HERHANGİ bir kiracının PHP'si onu okuyabiliyordu.
/// </para>
/// <para>
/// Ölçüm (aynı makinede, panel LocalSystem olarak dosyayı yazdı, ACL geri okundu):
/// <c>C:\Windows\TEMP\&lt;dosya&gt;</c> → <c>O:BAG:SYD:AI(A;ID;0x100001;;;SY)(A;ID;0x100001;;;IS)
/// (A;ID;FA;;;BA)(A;ID;FA;;;SY)</c> — <c>IS</c> = IIS_IUSRS, <c>0x100001</c> = ReadData.
/// </para>
/// <para>
/// <b>Neden "sonra ACL uygula" DEĞİL:</b> önce yazıp sonra <c>SetAccessControl</c> çağırmak, iki
/// çağrı arasında sırrın miras alınmış geniş izinle diskte durduğu bir pencere bırakır. Kiracı
/// klasörü listeleyebildiği için bu pencere teorik değildir. Bu yüzden DACL dosya AÇILIRKEN
/// verilir (<see cref="FileSystemAclExtensions.Create"/>) — içerik hiçbir an geniş izinle durmaz.
/// </para>
/// <para>
/// <b>Neden silmeye güvenilmiyor:</b> çağıran yerlerin hepsinde <c>finally { File.Delete }</c>
/// var ama silme YUTULUYOR ve süreç ortada öldürülürse (timeout, servis yeniden başlatma, çökme)
/// dosya diskte KALIR. Silme bir temizliktir, erişim denetimi değildir.
/// </para>
/// </remarks>
public static class SecureTempFile
{
    /// <summary>
    /// Sır taşıyan geçici dosya için tam yol üretir. Yalnız adı kurar; dosyayı OLUŞTURMAZ.
    /// </summary>
    public static string BuildPath(string prefix, string extension)
    {
        var uzanti = extension.StartsWith('.') ? extension : "." + extension;
        return Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}{uzanti}");
    }

    /// <summary>
    /// İçeriği, DACL'i AÇILIŞ ANINDA kurulmuş yeni bir dosyaya yazar. Dosya varsa hata verir
    /// (GUID'li adlarda çakışma bir saldırı işaretidir, sessizce üzerine yazılmaz).
    /// </summary>
    public static async Task WriteAsync(
        string path,
        string content,
        Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        // ÖNSÖZ (BOM) ELLE YAZILIR — bu satır bir REGRESYONUN düzeltmesidir, süs değil.
        // File.WriteAllTextAsync, verilen Encoding'in önsözünü kendi yazar; Encoding.GetBytes ise
        // ASLA yazmaz. Doğrudan GetBytes'a geçince UTF8Encoding(emitIdentifier: true) ile açılan
        // çağıranların BOM'u sessizce düştü ve PowerShell 5.1 dosyayı sistem kod sayfasıyla
        // okumaya başladı: Türkçe karakterler bozuldu (ProcessRunnerEncodingTests yakaladı).
        var preamble = encoding.GetPreamble();
        var bytes = encoding.GetBytes(content);

        await using var stream = Create(path);
        if (preamble.Length > 0)
        {
            await stream.WriteAsync(preamble, cancellationToken);
        }

        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Yazmaya hazır, kiracıya KAPALI bir dosya akışı açar. Büyük/akış hâlinde üretilen içerik
    /// (döküm, çözülmüş arşiv) için <see cref="WriteAsync"/> yerine bu kullanılır.
    /// </summary>
    public static FileStream Create(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Windows dışında bu ACL modeli yok; panel yalnız Windows'ta koşar, testler diğer
            // platformda da derlenebilsin diye akış normal açılır.
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }

        return CreateWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreateWindows(string path)
    {
        return new FileInfo(path).Create(
            FileMode.CreateNew,
            FileSystemRights.WriteData | FileSystemRights.Write | FileSystemRights.ReadData | FileSystemRights.Delete,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.None,
            BuildSecurity());
    }

    /// <summary>
    /// Geçici sır dosyasının DACL'i: miras KAPALI ve KOPYALANMAZ, yalnız SYSTEM + Administrators +
    /// panelin çalıştığı hesap kalır. <see cref="Bootstrap.DataRootHardeningService"/> ile AYNI
    /// disiplin; saf fonksiyondur (diske dokunmaz) → yükseltilmiş yetki olmadan sınanabilir.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static FileSecurity BuildSecurity()
    {
        var security = new FileSecurity();

        // preserveInheritance: FALSE — C:\Windows\TEMP'in IIS_IUSRS:ReadData ACE'i KOPYALANMAZ.
        // Bu tek satır bulgunun kendisidir; true olsaydı koruma doğuştan ölü olurdu.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var owner in ResolveOwnerSids())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>
    /// Dosyaya erişmesi ZORUNLU olan hesaplar. Servis hesabı TAHMİN EDİLMEZ, süreç kimliğinden
    /// okunur: panel özel bir hesapla koşuyorsa kendi yazdığı dosyayı okuyamaz hâle gelmesin.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<SecurityIdentifier> ResolveOwnerSids()
    {
        var owners = new List<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null)
        };

        try
        {
            var current = WindowsIdentity.GetCurrent().User;
            if (current is not null && !owners.Any(sid => sid.Equals(current)))
            {
                owners.Add(current);
            }
        }
        catch (Exception)
        {
            // Kimlik okunamazsa SYSTEM+Administrators yeter; panel bunlardan biriyle koşar.
        }

        return owners;
    }
}
