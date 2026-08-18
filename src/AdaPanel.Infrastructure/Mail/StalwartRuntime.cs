using System.Text.Json;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Support;
using Microsoft.EntityFrameworkCore;

namespace AdaPanel.Infrastructure.Mail;

/// <summary>
/// Stalwart araçlarının YERİ ve yönetici kimliğinin TEK kaynağı.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden ayrı sınıf:</b> "aracı bul" ve "yönetici parolasını çöz" işleri artık İKİ ayrı
/// tüketiciye hizmet ediyor — <see cref="MailService"/> (stalwart-cli) ve
/// <see cref="StalwartAccountArchiver"/> (vandelay). İkisinde ayrı ayrı yazılsaydı, parolanın
/// saklandığı yer değiştiğinde biri güncellenir diğeri sessizce eski yere bakmaya devam ederdi;
/// bu depoda "kod var ama hiç çalışmıyor" arızalarının kaynağı tam olarak bu tür ikizlenmelerdi.
/// </para>
/// </remarks>
public sealed class StalwartRuntime(
    JsonFileStore fileStore,
    ISecretProtector secretProtector,
    AdaPanelDbContext dbContext,
    IHttpClientFactory httpClientFactory)
{
    internal const string ConfigPath = "config/mail.json";
    internal const string ServerComponentId = "stalwart-mail";
    internal const string GeneratedPasswordKey = "generatedPassword";
    internal const string AdminUser = "admin";

    /// <summary>Stalwart'ın loopback ucu — duyurulan adresi BURADAN okuruz.</summary>
    private const string LocalSessionUrl = "http://localhost:8080/.well-known/jmap";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Bir Stalwart aracının çalıştırılabilirini bulur: önce panelin kurduğu bileşen dizini,
    /// sonra varsayılan kurulum kökü, en son PATH.
    /// </summary>
    /// <param name="componentId">Bileşen kimliği (ör. <c>stalwart-cli</c>, <c>vandelay</c>).</param>
    /// <param name="executableName">Aranan dosya adı (ör. <c>vandelay.exe</c>).</param>
    /// <param name="defaultRoot">Varsayılan kurulum kökü (ortam değişkeni içerebilir).</param>
    internal async Task<string?> ResolveExecutableAsync(
        string componentId,
        string executableName,
        string defaultRoot,
        CancellationToken cancellationToken)
    {
        // 1) Panel üzerinden kurulan bileşenin dizini.
        var component = await dbContext.InstalledComponents
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.ComponentId == componentId && item.Status == ComponentInstallStatus.Installed,
                cancellationToken);

        if (component is not null && !string.IsNullOrWhiteSpace(component.InstallDir) && Directory.Exists(component.InstallDir))
        {
            var found = Directory.EnumerateFiles(component.InstallDir, executableName, SearchOption.AllDirectories).FirstOrDefault();
            if (found is not null)
            {
                return found;
            }
        }

        // 2) Varsayılan kurulum kökü.
        var root = Environment.ExpandEnvironmentVariables(defaultRoot);
        if (Directory.Exists(root))
        {
            var found = Directory.EnumerateFiles(root, executableName, SearchOption.AllDirectories).FirstOrDefault();
            if (found is not null)
            {
                return found;
            }
        }

        // 3) PATH üzerinden.
        try
        {
            var bareName = Path.GetFileNameWithoutExtension(executableName);
            var probe = await ProcessRunner.RunAsync(bareName, "--version", cancellationToken: cancellationToken);
            if (probe.ExitCode == 0)
            {
                return bareName;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // bulunamadı
        }

        return null;
    }

    /// <summary>
    /// Yönetici parolası: önce <c>config/mail.json</c> override'ı, sonra kurulan bileşenin
    /// üretilmiş parolası. İkisi de yoksa <c>null</c> — ÇAĞIRAN bunu "yapılandırılmamış" saymalı,
    /// boş parolayla denememeli.
    /// </summary>
    internal async Task<string?> GetAdminPasswordAsync(CancellationToken cancellationToken)
    {
        var config = await fileStore.ReadAsync<MailAdminConfig?>(ConfigPath, null, cancellationToken);
        if (config is not null && !string.IsNullOrWhiteSpace(config.AdminPasswordProtected))
        {
            try
            {
                return secretProtector.Unprotect(config.AdminPasswordProtected);
            }
            catch
            {
                // bileşen parolasına düş
            }
        }

        var component = await dbContext.InstalledComponents
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.ComponentId == ServerComponentId && item.Status == ComponentInstallStatus.Installed,
                cancellationToken);

        if (component is null || string.IsNullOrWhiteSpace(component.ProtectedConfigurationJson))
        {
            return null;
        }

        try
        {
            var payload = secretProtector.Unprotect(component.ProtectedConfigurationJson);
            var configuration = JsonSerializer.Deserialize<Dictionary<string, string>>(payload, SerializerOptions);
            return configuration is not null && configuration.TryGetValue(GeneratedPasswordKey, out var generated)
                ? generated
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sunucunun JMAP oturumunda DUYURDUĞU <c>apiUrl</c>. Boş = okunamadı.
    /// </summary>
    /// <remarks>
    /// <b>Neden duyurulan adres tek gerçek:</b> <c>vandelay</c> bağlandığı adresi değil, oturumun
    /// duyurduğu adresi kullanmak ZORUNDA (ölçüldü: <c>--url</c>'e tam oturum adresi vermek bile
    /// değiştirmiyor). Hem IIS vekilinin yazılacağı ana ad hem yedek ön kontrolünün yoklayacağı
    /// adres bu tek kaynaktan gelir; iki ayrı yerde tahmin edilseydi biri diğerini yalancı çıkarırdı.
    /// </remarks>
    public async Task<string> GetAdvertisedApiUrlAsync(CancellationToken cancellationToken)
    {
        var password = await GetAdminPasswordAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(password))
        {
            return string.Empty;
        }

        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        using var request = new HttpRequestMessage(HttpMethod.Get, LocalSessionUrl);
        request.Headers.Authorization = BasicHeader(password);

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("apiUrl", out var apiUrl) ? apiUrl.GetString() ?? string.Empty : string.Empty;
    }

    /// <summary>Duyurulan adresin ANA ADI (vekil kuralının yazılacağı site bundan bulunur).</summary>
    public static string HostOf(string apiUrl)
        => Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    /// <summary><c>https://host/jmap/</c> → <c>https://host/.well-known/jmap</c>.</summary>
    public static string SessionUrlOf(string apiUrl)
        => Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Authority}/.well-known/jmap"
            : string.Empty;

    internal static System.Net.Http.Headers.AuthenticationHeaderValue BasicHeader(string password)
        => new("Basic", Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{AdminUser}:{password}")));

    /// <summary><c>config/mail.json</c>'un yalnız burada okunan alanı.</summary>
    internal sealed class MailAdminConfig
    {
        public string? AdminPasswordProtected { get; set; }
    }
}
