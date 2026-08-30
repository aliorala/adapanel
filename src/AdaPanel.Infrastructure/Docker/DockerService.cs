using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Docker;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Docker;

/// <summary>
/// Docker konteyner/imaj yonetimini docker CLI uzerinden saglar. Docker kurulu
/// degilse veya daemon calismiyorsa durum olarak raporlar, hata firlatmaz.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden günlükçü (ILogger) var:</b> "hata firlatmaz" tasarımı kullanıcı için doğru ama teşhis
/// için körlük üretiyordu — üç ayrı yerde başarısızlık SESSİZCE boş sonuca dönüşüyor: docker.exe
/// bulunamaması (kullanıcı "Docker kurulu değil" görür, oysa yalnız PATH bozuk olabilir), konteyner/
/// imaj listelemesinin patlaması (panel BOŞ liste gösterir; çalışan konteynerler varken "hiç
/// konteyner yok" demek yanlış bilgidir) ve ayrıştırılamayan CLI satırları (sürüm farkı yüzünden
/// liste sessizce eksik kalır). Hiçbirinin sebebi hiçbir yere yazılmıyordu.
/// </para>
/// <para>
/// Konteyner/imaj değişikliklerinin DENETİM kaydı burada değil <c>DockerController</c>'da yazılır
/// (kim/nereden bilgisi HTTP bağlamındadır).
/// </para>
/// </remarks>
public sealed partial class DockerService(ILogger<DockerService> logger) : IDockerService
{
    public async Task<DockerWorkspaceDto> GetWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        var version = await TryRunAsync("version --format \"{{.Server.Version}}\"", cancellationToken);
        if (version is null)
        {
            // docker.exe bulunamadi.
            return new DockerWorkspaceDto { Installed = false };
        }

        var workspace = new DockerWorkspaceDto { Installed = true };
        if (!version.Success || string.IsNullOrWhiteSpace(version.StandardOutput))
        {
            // CLI var ama daemon erisilemiyor olabilir.
            workspace.DaemonRunning = false;
            return workspace;
        }

        workspace.DaemonRunning = true;
        workspace.Version = version.StandardOutput.Trim();
        workspace.Containers = await ReadContainersAsync(cancellationToken);
        workspace.Images = await ReadImagesAsync(cancellationToken);
        return workspace;
    }

    public Task<DockerWorkspaceDto> StartContainerAsync(string id, CancellationToken cancellationToken = default) =>
        ContainerActionAsync("start", id, cancellationToken);

    public Task<DockerWorkspaceDto> StopContainerAsync(string id, CancellationToken cancellationToken = default) =>
        ContainerActionAsync("stop", id, cancellationToken);

    public Task<DockerWorkspaceDto> RestartContainerAsync(string id, CancellationToken cancellationToken = default) =>
        ContainerActionAsync("restart", id, cancellationToken);

    public async Task<DockerWorkspaceDto> RemoveContainerAsync(string id, CancellationToken cancellationToken = default)
    {
        var safeId = RequireIdentifier(id);
        await RunRequiredAsync($"rm -f {safeId}", cancellationToken);
        return await GetWorkspaceAsync(cancellationToken);
    }

    public async Task<DockerLogsDto> GetLogsAsync(string id, int tail, CancellationToken cancellationToken = default)
    {
        var safeId = RequireIdentifier(id);
        var lines = Math.Clamp(tail, 10, 2000);
        var result = await RunRequiredAsync($"logs --tail {lines} {safeId}", cancellationToken);
        return new DockerLogsDto
        {
            ContainerId = id,
            Output = string.Join(
                Environment.NewLine,
                new[] { result.StandardOutput, result.StandardError }.Where(value => !string.IsNullOrWhiteSpace(value)))
        };
    }

    public async Task<DockerWorkspaceDto> PullImageAsync(PullImageRequest request, CancellationToken cancellationToken = default)
    {
        var image = RequireImageReference(request.Image);
        await RunRequiredAsync($"pull {image}", cancellationToken);
        return await GetWorkspaceAsync(cancellationToken);
    }

    public async Task<DockerWorkspaceDto> RemoveImageAsync(string id, CancellationToken cancellationToken = default)
    {
        var safeId = RequireIdentifier(id);
        await RunRequiredAsync($"rmi -f {safeId}", cancellationToken);
        return await GetWorkspaceAsync(cancellationToken);
    }

    public async Task<DockerWorkspaceDto> RunContainerAsync(RunContainerRequest request, CancellationToken cancellationToken = default)
    {
        var image = RequireImageReference(request.Image);
        var builder = new StringBuilder("run -d");

        if (request.AutoRestart)
        {
            builder.Append(" --restart unless-stopped");
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            builder.Append(" --name ").Append(RequireIdentifier(request.Name));
        }

        foreach (var port in SplitTokens(request.Ports))
        {
            if (!PortMappingRegex().IsMatch(port))
            {
                throw new AppException("docker.invalid_port", 400, 400);
            }

            builder.Append(" -p ").Append(port);
        }

        foreach (var variable in SplitTokens(request.Environment))
        {
            if (!EnvironmentRegex().IsMatch(variable))
            {
                throw new AppException("docker.invalid_environment", 400, 400);
            }

            // ORTAM DEĞİŞKENİNDEN BAYRAK ENJEKSİYONU. EnvironmentRegex `=` sonrasına `.*` diyor:
            // boşluk, `"`, `\` HEPSİ geçiyor (ölçüldü). Eskiden yalnız `"` → `\"` yapılıyordu ve bu,
            // Windows argüman kuralında YANLIŞ: girdideki `\"` dizisi `\\"` olur, ayrıştırıcı tırnağı
            // KAPATIR ve sonrası yeni argüman olur — yani `-e` değeri `docker run`'a `--privileged
            // -v C:\:/host` ekleyebiliyordu (konteynerden host'a tam erişim = SYSTEM).
            // Değer serbest bırakılıyor (ortam değeri gerçekten serbest metindir); kural
            // TIRNAKLAMADA düzeltildi.
            builder.Append(" -e ").Append(WindowsArgument.Quote(variable));
        }

        builder.Append(' ').Append(image);

        await RunRequiredAsync(builder.ToString(), cancellationToken);
        return await GetWorkspaceAsync(cancellationToken);
    }

    private async Task<DockerWorkspaceDto> ContainerActionAsync(string action, string id, CancellationToken cancellationToken)
    {
        var safeId = RequireIdentifier(id);
        await RunRequiredAsync($"{action} {safeId}", cancellationToken);
        return await GetWorkspaceAsync(cancellationToken);
    }

    private async Task<IReadOnlyCollection<DockerContainerDto>> ReadContainersAsync(CancellationToken cancellationToken)
    {
        var result = await TryRunAsync("ps -a --format \"{{json .}}\"", cancellationToken);
        if (result is null || !result.Success)
        {
            // BOŞ LİSTE ≠ KONTEYNER YOK. Kullanıcıya boş bir tablo göstermek kabul edilebilir
            // (uydurma veri göstermekten iyidir) ama sebebi kaydedilmezse "konteynerlerim kayboldu"
            // şikâyetinin arkasındaki gerçek hata (izin, daemon, sürüm) hiç görünmez.
            logger.LogWarning(
                "Docker konteyner listesi OKUNAMADI; panel bos liste gosterecek. Cikti: {Error}",
                result?.StandardError ?? "docker.exe bulunamadi");
            return [];
        }

        return ParseLines(result.StandardOutput, element => new DockerContainerDto
        {
            Id = GetString(element, "ID"),
            Name = GetString(element, "Names"),
            Image = GetString(element, "Image"),
            State = GetString(element, "State"),
            Status = GetString(element, "Status"),
            Ports = GetString(element, "Ports")
        });
    }

    private async Task<IReadOnlyCollection<DockerImageDto>> ReadImagesAsync(CancellationToken cancellationToken)
    {
        var result = await TryRunAsync("images --format \"{{json .}}\"", cancellationToken);
        if (result is null || !result.Success)
        {
            // Gerekçe: ReadContainersAsync ile aynı.
            logger.LogWarning(
                "Docker imaj listesi OKUNAMADI; panel bos liste gosterecek. Cikti: {Error}",
                result?.StandardError ?? "docker.exe bulunamadi");
            return [];
        }

        return ParseLines(result.StandardOutput, element => new DockerImageDto
        {
            Id = GetString(element, "ID"),
            Repository = GetString(element, "Repository"),
            Tag = GetString(element, "Tag"),
            Size = GetString(element, "Size")
        });
    }

    private IReadOnlyCollection<T> ParseLines<T>(string output, Func<JsonElement, T> map)
    {
        var items = new List<T>();
        var atlanan = 0;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                items.Add(map(document.RootElement));
            }
            catch (JsonException)
            {
                // Beklenmeyen satirlari atla — ama SESSİZCE değil: bir docker sürümü çıktı biçimini
                // değiştirdiğinde liste kendiliğinden eksilir ve panelde bu, "konteyner silinmiş"
                // gibi görünür. Sayı günlüğe düşerse sebep ilk bakışta anlaşılır.
                atlanan++;
            }
        }

        if (atlanan > 0)
        {
            logger.LogWarning(
                "Docker CLI ciktisinda {Atlanan} satir ayristirilamadi; liste EKSIK olabilir (docker surumu cikti bicimini degistirmis olabilir).",
                atlanan);
        }

        return items;
    }

    private async Task<CommandResult> RunRequiredAsync(string arguments, CancellationToken cancellationToken)
    {
        var result = await TryRunAsync(arguments, cancellationToken)
            ?? throw new AppException("docker.not_installed", 409, 409);

        if (!result.Success)
        {
            throw new AppException("docker.command_failed", result.ExitCode, 500,
                string.Join(Environment.NewLine, new[] { result.StandardError, result.StandardOutput }
                    .Where(value => !string.IsNullOrWhiteSpace(value))));
        }

        return result;
    }

    // Panel uzerinden kurulan Docker Engine'in varsayilan konumu (PATH henuz yenilenmemis olabilir).
    private static readonly string FallbackDockerExecutable = Path.Combine(
        Environment.ExpandEnvironmentVariables("%ProgramData%\\AdaPanel\\runtimes\\docker"),
        "docker",
        "docker.exe");

    private async Task<CommandResult?> TryRunAsync(string arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await ProcessRunner.RunAsync("docker", arguments, cancellationToken: cancellationToken);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            // PATH'te yoksa panel kurulum dizinine dus.
            if (File.Exists(FallbackDockerExecutable))
            {
                try
                {
                    return await ProcessRunner.RunAsync(FallbackDockerExecutable, arguments, cancellationToken: cancellationToken);
                }
                catch (System.ComponentModel.Win32Exception yedekHatasi)
                {
                    // Panel Docker'ı KURMUŞ ama çalıştıramıyor — bu "Docker kurulu değil"den bambaşka
                    // bir arızadır (izin, eksik bağımlılık, bozuk kurulum) ve kullanıcıya aynı ekran
                    // gösterildiği için ayrımı yalnız günlük yapabilir.
                    logger.LogWarning(
                        yedekHatasi,
                        "Panelin kurdugu docker.exe CALISTIRILAMADI ({Yol}); panel 'Docker kurulu degil' gosterecek.",
                        FallbackDockerExecutable);
                    return null;
                }
            }

            logger.LogDebug(
                exception,
                "docker.exe PATH'te bulunamadi ve panel kurulum dizininde de yok; Docker kurulu degil sayiliyor.");
            return null;
        }
    }

    private static string RequireIdentifier(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (!IdentifierRegex().IsMatch(trimmed))
        {
            throw new AppException("docker.invalid_identifier", 400, 400);
        }

        return trimmed;
    }

    private static string RequireImageReference(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (!ImageRegex().IsMatch(trimmed))
        {
            throw new AppException("docker.invalid_image", 400, 400);
        }

        return trimmed;
    }

    private static IEnumerable<string> SplitTokens(string value) =>
        (value ?? string.Empty)
            .Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_./:@-]*$")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"^\d{1,5}:\d{1,5}$")]
    private static partial Regex PortMappingRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=.*$")]
    private static partial Regex EnvironmentRegex();
}
