using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Bootstrap;

/// <summary>
/// K1 GÜVENLİK: Veri kökündeki HASSAS alt klasörlere (panel DB, DataProtection anahtarları, sertifikalar,
/// config, yedekler) yalnız SYSTEM + Administrators erişsin; barındırılan tenant'lar (her sitenin PHP'si,
/// IUSR, FTP kullanıcıları = Users / Authenticated Users) adapanel.db'yi (admin hash/2FA) ve anahtarları
/// OKUYAMASIN. 'runtimes' klasörüne DOKUNULMAZ.
///
/// DOĞRU YÖNTEM (kök neden düzeltmesi): Eski sürüm `icacls /inheritance:r` kullanıyordu — bu miras ACE'lerini
/// (CREATOR OWNER dahil) SİLİYOR ve bazı Windows Server'larda DACL'i bozup SYSTEM'in bile db oluşturmasını
/// engelliyordu → "SQLite Error 14: unable to open database file". Artık `/inheritance:d` (mirası KAPAT ama
/// mevcut ACE'leri açık kopya olarak KORU) kullanılır → SYSTEM/Admin/CREATOR-OWNER korunur (SQLite kırılmaz),
/// ardından yalnız Users (S-1-5-32-545) + Authenticated Users (S-1-5-11) KALDIRILIR. db AÇILMADAN önce çağrılır.
/// İdempotent + best-effort. Yine de bir sorun olursa <see cref="EnsureWritableAsync"/> kurtarma ağı devreye girer.
/// </summary>
public static class DataRootHardeningService
{
    private static readonly string[] SensitiveDirectories =
        ["data", "keys", "certs", "config", "backups", "mail-certs"];

    // SYSTEM=S-1-5-18, Administrators=S-1-5-32-544, Users=S-1-5-32-545, Authenticated Users=S-1-5-11 (dil-bağımsız).
    public static async Task HardenAsync(string dataRoot, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataRoot));

            foreach (var sub in SensitiveDirectories)
            {
                var dir = Path.Combine(root, sub);
                Directory.CreateDirectory(dir);

                // 1) Mirası KAPAT ama mevcut ACE'leri KORU (kopyala) — CREATOR OWNER + SYSTEM + Admin korunur.
                //    (icacls /inheritance:r her şeyi siliyordu → DACL bozuluyordu; :d güvenli.)
                await IcaclsAsync(dir, "/inheritance:d /T /C", cancellationToken);

                // 2) SYSTEM + Administrators tam yetki GARANTİ; sonra TENANT erişimini kaldır (Users + Auth Users).
                //    Ayrı çağrı: icacls seçenek sırası garantilensin (önce inheritance, sonra grant/remove).
                await IcaclsAsync(
                    dir,
                    "/grant *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F /remove:g *S-1-5-32-545 *S-1-5-11 /T /C",
                    cancellationToken);
            }

            logger.LogInformation("Veri kökü hassas klasörleri sertleştirildi (tenant erişimi kaldırıldı; SYSTEM/Admin korundu).");
        }
        catch (Exception exception)
        {
            // Sertleştirme başarısızsa bile paneli başlatmaya devam et.
            logger.LogWarning(exception, "Veri kökü ACL sertleştirme atlandı.");
        }
    }

    /// <summary>
    /// KURTARMA AĞI: db hâlâ açılamıyorsa (herhangi bir sebeple ACL bozuksa) yalnız 'data' klasörünün ACL'ini
    /// SIFIRLA (mirası geri ver → SYSTEM/Admin yazabilir) ki panel kilitlenmesin. Migration başarısız olunca çağrılır.
    /// </summary>
    public static async Task EnsureWritableAsync(string dataRoot, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var dataDir = Path.Combine(Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataRoot)), "data");
            Directory.CreateDirectory(dataDir);
            await IcaclsAsync(dataDir, "/reset /T /C", cancellationToken);
            logger.LogWarning("Veri klasörü ACL'i sıfırlandı (db erişim kurtarma — panel kilitlenmesin diye).");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Veri klasörü ACL sıfırlama atlandı.");
        }
    }

    private static async Task IcaclsAsync(string dir, string arguments, CancellationToken cancellationToken)
    {
        await ProcessRunner.RunAsync(
            "icacls.exe",
            $"\"{dir}\" {arguments}",
            cancellationToken: cancellationToken,
            timeout: TimeSpan.FromSeconds(60));
    }
}
