using System.Security.Principal;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Bootstrap;

/// <summary>
/// K1 GÜVENLİK: Panel veri kökündeki HASSAS alt klasörlere (panel DB, DataProtection anahtarları, sertifikalar,
/// config, yedekler) yalnız SYSTEM + Administrators (+ panelin servis kimliği) erişebilsin diye ACL'leri kilitler.
/// Aksi halde C:\ProgramData\AdaPanel, ProgramData'nın varsayılan ACL'ini miras alıp BUILTIN\Users'a (= barındırılan
/// her sitenin PHP'si, IUSR, FTP kullanıcıları) OKUMA verir → adapanel.db (admin hash/2FA) + key-*.xml (DataProtection)
/// sızar → tam sunucu ele geçirme.
///
/// ÖNEMLİ: 'runtimes' (php/node/mariadb/redis/stalwart) klasörüne DOKUNULMAZ — app pool'ların PHP/Node ikililerini
/// okuyabilmesi gerekir; bu yüzden TÜM kökü kilitleyip RX geri vermek yerine yalnız hassas alt klasörler kilitlenir
/// (PHP runtime'ı kırılmaz, ayrıca DB/Redis verisi zaten ağdan erişildiği için dosya okuması gerekmez).
///
/// Servis LocalSystem olarak çalışır (install-service.ps1 -> New-Service, kimlik bayrağı yok = LocalSystem = SYSTEM),
/// yani SYSTEM tam yetki panelin erişimini korur. Yine de güvenlik için mevcut süreç kimliği de eklenir.
/// Her açılışta idempotent çalışır; başarısız olsa bile panel başlatmayı engellemez.
/// </summary>
internal sealed class DataRootHardeningService(
    IOptions<PanelRuntimeOptions> options,
    ILogger<DataRootHardeningService> logger) : IHostedService
{
    // Tenant'lara ASLA açılmaması gereken alt klasörler (güvenlik denetimi K1 listesi).
    private static readonly string[] SensitiveDirectories =
        ["data", "keys", "certs", "config", "backups", "mail-certs"];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.Value.DataRoot));

            // Dil-bağımsız SID'ler: SYSTEM (S-1-5-18) + Administrators (S-1-5-32-544). Türkçe Windows'ta da doğru çalışır.
            var grants = "/grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F";
            var currentSid = GetCurrentSid();
            if (!string.IsNullOrEmpty(currentSid) && currentSid != "S-1-5-18")
            {
                grants += $" *{currentSid}:(OI)(CI)F";
            }

            foreach (var sub in SensitiveDirectories)
            {
                var dir = Path.Combine(root, sub);
                // Yoksa oluştur ki panel DB/anahtarları daha en baştan KİLİTLİ dizine yazsın.
                Directory.CreateDirectory(dir);

                var result = await ProcessRunner.RunAsync(
                    "icacls.exe",
                    $"\"{dir}\" /inheritance:r {grants} /T /C",
                    cancellationToken: cancellationToken,
                    timeout: TimeSpan.FromSeconds(60));

                if (result.ExitCode != 0)
                {
                    logger.LogWarning(
                        "Veri kökü ACL kilidi '{Directory}' için uyarı/hata döndürdü (exit {ExitCode}).",
                        dir, result.ExitCode);
                }
            }

            logger.LogInformation("Veri kökü hassas klasörleri SYSTEM/Administrators'a kilitlendi (K1 güvenlik).");
        }
        catch (Exception exception)
        {
            // Sertleştirme başarısızsa bile paneli başlatmaya devam et (en kötü ihtimalle eski ACL kalır).
            logger.LogWarning(exception, "Veri kökü ACL sertleştirme (K1) atlandı.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string GetCurrentSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
