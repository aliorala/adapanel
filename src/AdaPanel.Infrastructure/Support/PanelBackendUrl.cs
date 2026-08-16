using AdaPanel.Infrastructure.Options;
using Microsoft.Extensions.Configuration;

namespace AdaPanel.Infrastructure.Support;

/// <summary>
/// IIS'teki bir ters vekilin panele bağlanacağı YEREL adres.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ortak sınıf:</b> bu hesap iki yerde birebir aynı yapılıyordu
/// (<c>Program.cs</c>'teki dinleyici çözümlemesinin kopyası + <c>PanelPublicAccessService</c>).
/// Üçüncü bir kopya (posta otomatik yapılandırma vekili) eklenseydi, panelin portu ya da
/// sertifika durumu değiştiğinde kopyalardan biri güncellenip diğeri unutulurdu — ve sonuç
/// sessiz bir 502 olurdu: özellik "kurulu" görünür, hiç çalışmaz. Tek kaynak buradadır.
/// </para>
/// <para>
/// <b>"localhost" bilinçli seçimdir</b> (127.0.0.1 değil): panelin kendinden imzalı sertifikası
/// (bkz. scripts/initialize-panel-certificate.ps1) SAN olarak <c>localhost</c> içerir ve kurulumda
/// <c>LocalMachine\Root</c> deposuna eklenir. Böylece ARR arka uca bağlanırken hem ad hem zincir
/// doğrulaması geçer; IP kullanılsaydı ad uyuşmazlığı 502 üretirdi.
/// </para>
/// </remarks>
public static class PanelBackendUrl
{
    /// <summary>Panelin gerçekte dinlediği yerel adres (şema + ad + port).</summary>
    public static string Resolve(IConfiguration configuration, PanelRuntimeOptions runtimeOptions)
    {
        var dataRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(runtimeOptions.DataRoot));

        // Sertifika DOSYASINDAN okunur, yapılandırmadan tahmin edilmez: Program.cs sertifika
        // yoksa HTTPS'i hiç kurmaz ve HTTP'ye düşer. Şemayı yanlış tahmin etmek, vekilin
        // arka uca hiç bağlanamaması demektir.
        var hasCertificate =
            File.Exists(Path.Combine(dataRoot, "certs", "panel.pfx")) &&
            File.Exists(Path.Combine(dataRoot, "certs", "panel.password"));

        var port = ResolveListenerPort(
            configuration["ASPNETCORE_URLS"] ?? configuration["urls"],
            runtimeOptions.PreferredHttpsPort);

        return hasCertificate ? $"https://localhost:{port}" : $"http://localhost:{port}";
    }

    /// <summary>
    /// Program.cs'teki dinleyici portu çözümlemesinin AYNISI: panel gerçekte hangi portta dinliyor.
    /// </summary>
    public static int ResolveListenerPort(string? configuredUrls, int fallbackPort)
    {
        if (string.IsNullOrWhiteSpace(configuredUrls))
        {
            return fallbackPort;
        }

        foreach (var segment in configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Uri.TryCreate(segment, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                return uri.Port;
            }
        }

        return fallbackPort;
    }
}
