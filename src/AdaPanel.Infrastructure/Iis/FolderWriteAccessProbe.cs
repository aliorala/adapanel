using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AdaPanel.Infrastructure.Iis;

/// <summary>
/// Bir klasörde belirli bir hesabın GERÇEKTEN yazma (Modify) hakkı var mı — DİSKTEN OKUYARAK.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEDEN VAR — ölçülmüş sınıf:</b> panel <c>icacls</c>'i çalıştırıp çıkış koduna bakıyor ve
/// "izin verildi" diyordu. Çıkış kodu bu soruyu CEVAPLAMAZ:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>icacls … /T /C</c> bir alt öğeye erişemese bile <b>0 ile çıkar</b> (<c>/C</c> hataları
/// yok sayıp devam eder). Bu depoda daha önce ölçüldü ve
/// <c>WebDbClientService</c> içinde yazılı: "ÇIKIŞ KODUNA GÜVENİLMEZ".
/// </description></item>
/// <item><description>
/// Hesap adı çözülemezse <c>icacls</c> hata 1332 verir ve <b>hiçbir şey uygulamaz</b>.
/// </description></item>
/// <item><description>
/// İzin verildikten SONRA başka bir adım (ör. kiracı yalıtımı, <c>/inheritance:r /grant:r</c>)
/// DACL'i baştan yazabilir. Komutun başarısı, klasörün SON DURUMU demek değildir.
/// </description></item>
/// </list>
/// <para>
/// Bu yüzden ölçülen şey KOMUT DEĞİL, DURUMDUR: DACL diskten okunur. Tersi de doğru sayılır —
/// <c>icacls</c> hata verse bile hak zaten yerindeyse cevap "var"dır; kullanıcıya ilgilendiği şey
/// söylenir ("sitem yazabiliyor mu"), aracın ne dediği değil.
/// </para>
/// <para>
/// <b>SÖZLEŞMENİN SINIRI, bilerek:</b> yalnızca hesabın KENDİ SID'ine yazılmış kurallara bakılır,
/// grup üyeliği üzerinden gelen dolaylı erişime değil. Panelin uyguladığı kural tam olarak budur
/// (havuz kimliğine doğrudan <c>M</c>) ve kiracı yalıtımı (<c>WafService.ApplyFolderAclAsync</c>)
/// mirası kırıp <c>IIS_IUSRS</c>'yi zaten temizler — yani grup üzerinden gelen erişim, olması
/// istenen durum değildir. Dolaylı erişimi "var" saymak, yalıtımı bozuk bir sunucuyu sağlıklı
/// gösterirdi.
/// </para>
/// </remarks>
internal static class FolderWriteAccessProbe
{
    /// <summary>Ölçümün üç değerli sonucu. "Bilinmiyor" ile "yok" AYNI ŞEY DEĞİLDİR.</summary>
    internal enum Access
    {
        /// <summary>DACL okundu ve hak YERİNDE.</summary>
        Granted,

        /// <summary>DACL okundu ve hak YOK (ya da Deny ile kesilmiş).</summary>
        NotGranted,

        /// <summary>Okunamadı — cevap bilinmiyor. "Yok" DEMEK DEĞİLDİR.</summary>
        Unmeasured
    }

    internal readonly record struct Result(Access State, string Detail);

    /// <summary>
    /// <paramref name="principal"/> hesabının <paramref name="path"/> klasöründe Modify hakkı
    /// olup olmadığını DACL'den okur.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static Result Probe(string? path, string? principal)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new Result(Access.Unmeasured, "Windows disi calisma zamani: NTFS izinleri okunamaz.");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return new Result(Access.Unmeasured, "Klasor yolu bos: okunacak bir DACL yok.");
        }

        if (string.IsNullOrWhiteSpace(principal))
        {
            return new Result(Access.Unmeasured, "Hesap adi bos: kimin hakki arandigi bilinmiyor.");
        }

        if (!Directory.Exists(path))
        {
            // Klasör YOKSA hak da yoktur. Bu "ölçemedim" değil, ölçülmüş bir OLUMSUZLUKTUR.
            return new Result(Access.NotGranted, $"Klasor yok: {path}");
        }

        SecurityIdentifier hedef;
        try
        {
            hedef = principal.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                ? new SecurityIdentifier(principal)
                : (SecurityIdentifier)new NTAccount(principal).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            // Hesap ADI ÇÖZÜLEMİYOR. icacls'in 1332 hatasının tam kaynağı budur ve o durumda
            // icacls HİÇBİR ŞEY uygulamaz. Yani bu bir ölçüm eksikliği değil, ölçülmüş bir yokluk.
            return new Result(Access.NotGranted, $"Hesap Windows'ta YOK (ad cozulemedi): {principal}");
        }
        catch (Exception exception) when (exception is SystemException or ArgumentException)
        {
            return new Result(Access.Unmeasured, $"Hesap adi cozumlenemedi: {principal} ({exception.Message})");
        }

        AuthorizationRuleCollection kurallar;
        try
        {
            kurallar = new DirectoryInfo(path)
                .GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PrivilegeNotHeldException or PlatformNotSupportedException)
        {
            // DACL OKUNAMADI. Burada "izin yok" demek YANLIŞ olurdu: hak yerinde olabilir, biz
            // göremiyoruz. Yanlış alarm da yanlış güvence kadar zararlıdır.
            return new Result(Access.Unmeasured, $"Klasor DACL'i okunamadi: {path} ({exception.Message})");
        }

        var izin = default(FileSystemRights);
        var ret = default(FileSystemRights);

        foreach (FileSystemAccessRule kural in kurallar)
        {
            if (kural.IdentityReference is not SecurityIdentifier sid || !sid.Equals(hedef))
            {
                continue;
            }

            if (kural.AccessControlType == AccessControlType.Allow)
            {
                izin |= kural.FileSystemRights;
            }
            else
            {
                ret |= kural.FileSystemRights;
            }
        }

        // DENY ÖNCE BAKILIR: Windows'ta reddetme izne baskındır. Modify'nin herhangi bir parçası
        // reddedilmişse "yazabiliyor" demek yanlış olur.
        if ((ret & FileSystemRights.Modify) != 0)
        {
            return new Result(
                Access.NotGranted,
                $"Hesap icin REDDETME kurali var, izne baskin: {principal} -> {path}");
        }

        // FullControl da Modify'i kapsar; maskenin Modify'i TAMAMEN icermesi aranir.
        if ((izin & FileSystemRights.Modify) == FileSystemRights.Modify)
        {
            return new Result(Access.Granted, $"{principal} -> {path}: Modify yerinde (DACL'den okundu).");
        }

        return new Result(
            Access.NotGranted,
            $"Hesabin Modify hakki YOK: {principal} -> {path} (DACL'de okunan haklar: {(izin == default ? "(hic kural yok)" : izin.ToString())})");
    }
}
