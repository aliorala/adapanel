using System.Security.AccessControl;
using System.Security.Principal;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Bootstrap;

/// <summary>
/// K1 GÜVENLİK: Veri kökündeki HASSAS alt klasörlere (panel DB, DataProtection anahtarları, sertifikalar,
/// config, yedekler) yalnız SYSTEM + Administrators erişsin; barındırılan tenant'lar (Users / Authenticated
/// Users = her sitenin PHP'si, IUSR, FTP) adapanel.db (admin hash/2FA) ve key-*.xml (DataProtection) okuyamasın.
///
/// KÖK NEDEN DÜZELTMESİ — neden .NET, neden icacls DEĞİL: Eski icacls yaklaşımı (`/inheritance:r` ya da `/grant
/// SID:(OI)(CI)F`) bazı Windows Server'larda izni klasörün KENDİSİNE değil "yalnız miras (inherit-only)" olarak
/// uygulayabiliyor → SYSTEM klasöre dosya YAZAMIYOR → hem "SQLite Error 14" hem antiforgery/CSRF (DataProtection
/// anahtarı yazılamıyor) patlıyordu. .NET <see cref="FileSystemAccessRule"/> ile izin AÇIKÇA "bu klasör + alt
/// klasör + dosyalar"a (PropagationFlags.None) uygulanır → belirsizlik yok. db AÇILMADAN önce çağrılır,
/// idempotent + best-effort. Yine de sorun olursa <see cref="EnsureWritableAsync"/> kurtarma ağı devrede.
/// </summary>
public static class DataRootHardeningService
{
    private static readonly string[] SensitiveDirectories =
        ["data", "keys", "certs", "config", "backups", "mail-certs"];

    public static Task HardenAsync(string dataRoot, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }

        try
        {
            var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataRoot));
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var authUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            foreach (var sub in SensitiveDirectories)
            {
                var dir = Path.Combine(root, sub);
                Directory.CreateDirectory(dir);

                try
                {
                    var info = new DirectoryInfo(dir);
                    var security = info.GetAccessControl();

                    // Mirası KAPAT ama mevcut ACE'leri açık kopya olarak KORU (CREATOR OWNER vb. bozulmaz).
                    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);

                    // SYSTEM + Administrators: KLASÖRÜN KENDİSİNE + alt klasör/dosyalara tam yetki.
                    // PropagationFlags.None => kural NESNENİN KENDİSİNE de uygulanır (icacls'in inherit-only
                    // belirsizliği YOK) → SYSTEM bu klasöre db/anahtar YAZABİLİR.
                    security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                    security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

                    // Tenant erişimini KALDIR (Users + Authenticated Users) → hassas dosyalar sızmaz (K1).
                    security.PurgeAccessRules(users);
                    security.PurgeAccessRules(authUsers);

                    info.SetAccessControl(security);
                }
                catch (Exception dirException)
                {
                    logger.LogWarning(dirException, "Veri kökü ACL ayarı '{Directory}' için atlandı.", dir);
                }
            }

            logger.LogInformation("Veri kökü hassas klasörleri sertleştirildi (tenant kaldırıldı; SYSTEM/Admin bu-klasör+alt-öğeler tam yetki).");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Veri kökü ACL sertleştirme atlandı.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// KURTARMA AĞI: db/anahtar hâlâ açılamıyorsa (ACL herhangi bir sebeple bozuksa) 'data' VE 'keys'
    /// klasörlerinin ACL'ini SIFIRLA (mirası geri ver → SYSTEM yazabilir) ki panel kilitlenmesin.
    /// </summary>
    public static async Task EnsureWritableAsync(string dataRoot, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataRoot));
        foreach (var sub in new[] { "data", "keys" })
        {
            try
            {
                var dir = Path.Combine(root, sub);
                Directory.CreateDirectory(dir);
                await ProcessRunner.RunAsync(
                    "icacls.exe",
                    $"\"{dir}\" /reset /T /C",
                    cancellationToken: cancellationToken,
                    timeout: TimeSpan.FromSeconds(60));
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Veri klasörü ACL sıfırlama atlandı: {Sub}", sub);
            }
        }

        logger.LogWarning("Veri klasörü ACL'leri sıfırlandı (erişim kurtarma — panel kilitlenmesin diye).");
    }
}
