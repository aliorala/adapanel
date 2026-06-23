using System.Security.Principal;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Bootstrap;

/// <summary>
/// K1 GÜVENLİK: Panel veri kökündeki HASSAS alt klasörlere (panel DB, DataProtection anahtarları, sertifikalar,
/// config, yedekler) yalnız SYSTEM + Administrators (+ panelin servis kimliği) erişebilsin diye ACL'leri kilitler.
/// Aksi halde C:\ProgramData\AdaPanel, ProgramData'nın varsayılan ACL'ini miras alıp BUILTIN\Users'a (= barındırılan
/// her sitenin PHP'si, IUSR, FTP kullanıcıları) OKUMA verir → adapanel.db (admin hash/2FA) + key-*.xml (DataProtection)
/// sızar → tam sunucu ele geçirme. 'runtimes' klasörüne DOKUNULMAZ (app pool'lar PHP/Node ikililerini okumalı).
///
/// KRİTİK SIRA — db AÇILMADAN ÖNCE çağrılır (Program.cs › EnsureDatabaseAsync başında):
/// Eskiden bu sertleştirme IHostedService idi ve app.Run SONRASI, adapanel.db AÇIKKEN `icacls /T` uyguluyordu.
/// Açık dosyada "/inheritance:r başarılı + /grant başarısız" yarışı dosyayı BOŞ DACL ile bırakabiliyor → sonraki
/// açılışta SYSTEM bile açamıyor ("SQLite Error 14: unable to open database file"). Artık db OLUŞTURULMADAN önce,
/// dosya kapalıyken çalışır: yeni db kilidi miras alarak doğar; her açılışta idempotent re-uygulanır ama hep KAPALI
/// dosyaya. Servis LocalSystem (SYSTEM) olduğundan SYSTEM:F panelin erişimini korur. Hata olsa bile panel başlatmayı engellemez.
/// </summary>
public static class DataRootHardeningService
{
    // Tenant'lara ASLA açılmaması gereken alt klasörler (güvenlik denetimi K1 listesi).
    private static readonly string[] SensitiveDirectories =
        ["data", "keys", "certs", "config", "backups", "mail-certs"];

    public static async Task HardenAsync(string dataRoot, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataRoot));

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
