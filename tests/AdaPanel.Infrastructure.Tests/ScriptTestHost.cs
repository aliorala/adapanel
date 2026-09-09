using System.Diagnostics;
using System.Text;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// <c>scripts\*.ps1</c> dosyalarini GERCEKTEN kosturan ortak duzenek.
/// </summary>
/// <remarks>
/// <para>
/// Denetimin bulgusu netti: <c>scripts/*.ps1</c> ve <c>installer/*.iss</c> icin HICBIR test yoktu.
/// Bir kaldirma betiginde tek karakterlik bir yol hatasi (<c>configtp-users.json</c>) aylarca
/// yasadi, cunku o kodu kimse kosturmuyordu.
/// </para>
/// <para>
/// Buradaki testler betigin BIR KOPYASINI degil, depodaki GERCEK dosyayi dot-source eder.
/// Kaldirma betigi bunun icin <c>-DefineFunctionsOnly</c> anahtarini tasiyor: fonksiyonlar
/// tanimlanir, ana akis (servis silme, klasor silme) HIC calismaz.
/// </para>
/// </remarks>
internal static class ScriptTestHost
{
    /// <summary>Depo kokunu bulur (AdaPanel.sln'in durdugu klasor).</summary>
    internal static string RepoRoot
    {
        get
        {
            var klasor = new DirectoryInfo(AppContext.BaseDirectory);
            while (klasor is not null)
            {
                if (File.Exists(Path.Combine(klasor.FullName, "AdaPanel.sln")))
                {
                    return klasor.FullName;
                }

                klasor = klasor.Parent;
            }

            throw new DirectoryNotFoundException("AdaPanel.sln bulunamadi; depo koku belirlenemedi.");
        }
    }

    internal static string ScriptPath(params string[] parcalar) =>
        Path.Combine(new[] { RepoRoot }.Concat(parcalar).ToArray());

    internal static string PowerShellLiteral(string deger) => "'" + deger.Replace("'", "''") + "'";

    /// <summary>PowerShell 5.1'i gercekten calistirir; cikis kodunu ve ciktiyi dondurur.</summary>
    internal static ScriptResult Run(string betik, int zamanAsimiMs = 120_000)
        => RunAsync(betik, zamanAsimiMs).GetAwaiter().GetResult();

    private static async Task<ScriptResult> RunAsync(string betik, int zamanAsimiMs)
    {
        var yol = Path.Combine(Path.GetTempPath(), $"ada-betik-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(yol, "$ErrorActionPreference = 'Stop'\n" + betik, new UTF8Encoding(true));
        try
        {
            using var surec = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{yol}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            var cikti = surec.StandardOutput.ReadToEndAsync();
            var hata = surec.StandardError.ReadToEndAsync();
            try
            {
                await Task.WhenAll(cikti, hata, surec.WaitForExitAsync()).WaitAsync(TimeSpan.FromMilliseconds(zamanAsimiMs)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                try { surec.Kill(entireProcessTree: true); } catch { /* en iyi caba */ }
                throw new TimeoutException("PowerShell betigi zaman asimina ugradi.");
            }

            return new ScriptResult(surec.ExitCode, await cikti.ConfigureAwait(false), await hata.ConfigureAwait(false));
        }
        finally
        {
            try { File.Delete(yol); } catch { /* en iyi caba */ }
        }
    }

    internal sealed record ScriptResult(int ExitCode, string StdOut, string StdErr)
    {
        internal string Tumu => StdOut + StdErr;
    }
}
