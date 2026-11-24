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
    internal Func<string, CancellationToken, Task<CommandResult?>>? CommandRunner { get; init; }
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

        var cpu = NormalizeCpuLimit(request.CpuLimit);
        if (cpu.Length > 0) builder.Append(" --cpus ").Append(cpu);
        var memory = NormalizeMemoryLimit(request.MemoryLimit);
        if (memory.Length > 0) builder.Append(" --memory ").Append(memory).Append(" --memory-swap ").Append(memory);
        if (!string.IsNullOrWhiteSpace(request.SiteName))
            builder.Append(" --label ").Append(SiteLabel(request.SiteName));

        builder.Append(' ').Append(image);

        await RunRequiredAsync(builder.ToString(), cancellationToken);
        return await GetWorkspaceAsync(cancellationToken);
    }

    public async Task<DockerWorkspaceDto> UpdateContainerLimitsAsync(string id, UpdateContainerLimitsRequest request, CancellationToken cancellationToken = default)
    {
        var safeId = RequireIdentifier(id);
        var cpu = NormalizeCpuLimit(request.CpuLimit);
        var memory = NormalizeMemoryLimit(request.MemoryLimit);
        // Boş değer = "sınırı kaldır": docker update için 0 sınırsızdır.
        var builder = new StringBuilder("update");
        builder.Append(" --cpus ").Append(cpu.Length == 0 ? "0" : cpu);
        // memory-swap bellekten küçük olamaz → ikisi eşit (takas kapalı).
        if (memory.Length == 0) builder.Append(" --memory 0 --memory-swap -1");
        else builder.Append(" --memory ").Append(memory).Append(" --memory-swap ").Append(memory);
        builder.Append(' ').Append(safeId);
        await RunRequiredAsync(builder.ToString(), cancellationToken);
        return await GetWorkspaceAsync(cancellationToken);
    }

    public async Task<DockerContainerLimitsDto> GetContainerLimitsAsync(string id, CancellationToken cancellationToken = default)
    {
        var safeId = RequireIdentifier(id);
        var result = await RunRequiredAsync("inspect --format \"{{.HostConfig.NanoCpus}} {{.HostConfig.Memory}}\" " + safeId, cancellationToken);
        var parts = result.StandardOutput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !long.TryParse(parts[0], out var nano) || !long.TryParse(parts[1], out var memory))
            throw new AppException("docker.command_failed", 502, 502, "Docker inspect output could not be parsed.");
        return new DockerContainerLimitsDto { ContainerId = id, Cpus = nano / 1_000_000_000d, MemoryBytes = memory };
    }

    public async Task<DockerSiteCleanupResult> RemoveSiteContainersAsync(string siteName, CancellationToken cancellationToken = default)
    {
        var label = SiteLabel(siteName);
        var outcome = new DockerSiteCleanupResult();
        var version = await TryRunAsync("version --format \"{{.Server.Version}}\"", cancellationToken);
        if (version is null) return outcome;
        outcome.Installed = true;
        if (!version.Success || string.IsNullOrWhiteSpace(version.StandardOutput)) return outcome;
        outcome.DaemonRunning = true;

        var listed = await RunRequiredAsync("ps -a --filter label=" + label + " --format \"{{json .}}\"", cancellationToken);
        foreach (var container in ParseLines(listed.StandardOutput, element => (Id: GetString(element, "ID"), Name: GetString(element, "Names"))))
        {
            var removal = await TryRunAsync("rm -f " + RequireIdentifier(container.Id), cancellationToken);
            if (removal is { Success: true }) outcome.Removed.Add(container.Name);
            else outcome.Failed.Add($"{container.Name}: {removal?.StandardError.Trim() ?? "docker unavailable"}");
        }
        return outcome;
    }

    private static string SiteLabel(string siteName)
    {
        var trimmed = siteName?.Trim() ?? string.Empty;
        if (!SiteNameRegex().IsMatch(trimmed)) throw new AppException("docker.invalid_site", 400, 400);
        return "adapanel.site=" + trimmed;
    }

    private static string SiteFromLabels(string labels)
    {
        const string prefix = "adapanel.site=";
        foreach (var part in labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (part.StartsWith(prefix, StringComparison.Ordinal)) return part[prefix.Length..];
        return string.Empty;
    }

    private static string NormalizeCpuLimit(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return string.Empty;
        if (!CpuRegex().IsMatch(trimmed) || !double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var cpus) || cpus < 0.01 || cpus > 256)
            throw new AppException("docker.invalid_limit", 400, 400);
        return trimmed;
    }

    private static string NormalizeMemoryLimit(string? value)
    {
        var trimmed = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (trimmed.Length == 0) return string.Empty;
        var match = MemoryRegex().Match(trimmed);
        if (!match.Success) throw new AppException("docker.invalid_limit", 400, 400);
        var amount = long.Parse(match.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var megabytes = match.Groups["u"].Value == "g" ? amount * 1024 : amount;
        // Docker en az 6 MB ister; üst sınır 1 TB ile makul tutulur.
        if (megabytes < 6 || megabytes > 1024 * 1024) throw new AppException("docker.invalid_limit", 400, 400);
        return trimmed;
    }

    private async Task<DockerWorkspaceDto> ContainerActionAsync(string action, string id, CancellationToken cancellationToken)
    {
        var safeId = RequireIdentifier(id);
        await RunRequiredAsync($"{action} {safeId}", cancellationToken);
        return await GetWorkspaceAsync(cancellationToken);
    }

    private async Task<IReadOnlyCollection<DockerContainerDto>> ReadContainersAsync(CancellationToken cancellationToken)
    {
        var result = await RunRequiredAsync("ps -a --format \"{{json .}}\"", cancellationToken);
        return ParseLines(result.StandardOutput, element => new DockerContainerDto
        {
            Id = GetString(element, "ID"),
            Name = GetString(element, "Names"),
            Image = GetString(element, "Image"),
            State = GetString(element, "State"),
            Status = GetString(element, "Status"),
            Ports = GetString(element, "Ports"),
            Site = SiteFromLabels(GetString(element, "Labels"))
        });
    }

    private async Task<IReadOnlyCollection<DockerImageDto>> ReadImagesAsync(CancellationToken cancellationToken)
    {
        var result = await RunRequiredAsync("images --format \"{{json .}}\"", cancellationToken);
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
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    string.IsNullOrWhiteSpace(GetString(document.RootElement, "ID")))
                    throw new JsonException("Docker inventory row has no resource identity.");
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
                "Docker CLI ciktisinda {Atlanan} satir ayristirilamadi; eksik envanter yayinlanmadi.",
                atlanan);
            throw new AppException("docker.command_failed", 502, 502, "Docker inventory output could not be parsed.");
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
        if (CommandRunner is not null) return await CommandRunner(arguments, cancellationToken);
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

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,259}$")]
    private static partial Regex SiteNameRegex();

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,2})?$")]
    private static partial Regex CpuRegex();

    [GeneratedRegex(@"^(?<n>\d{1,7})(?<u>[mg])$")]
    private static partial Regex MemoryRegex();

    [GeneratedRegex(@"^\d{1,5}:\d{1,5}$")]
    private static partial Regex PortMappingRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=.*$")]
    private static partial Regex EnvironmentRegex();
}
