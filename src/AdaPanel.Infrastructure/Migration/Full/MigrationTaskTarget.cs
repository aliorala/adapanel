using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Iis;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Scheduler;

namespace AdaPanel.Infrastructure.Migration.Full;

public sealed class MigrationTaskTarget(ISchedulerService scheduler, IMigrationPackageStore packages,
    IIisSiteService sites, IConcurrencyLock gates) : IMigrationTargetAdapter
{
    public string Kind => "task";
    private const int MetadataLimit = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false, MaxDepth = 16 };

    public IReadOnlyList<MigrationCreatedCredential> PrepareCredentials(MigrationPlanItemDto item, PortableMigrationManifest manifest) => [];

    public async Task<MigrationTargetCheck> CheckAsync(MigrationPlanItemDto item, PortableMigrationManifest manifest,
        MigrationPlanRequest selection, CancellationToken cancellationToken)
    {
        var result = new MigrationTargetCheck();
        try
        {
            await BuildRequestAsync(item, manifest, selection, cancellationToken);
            if ((await scheduler.GetTasksAsync(cancellationToken)).Any(task => task.Name.Equals(item.Target.TargetName, StringComparison.OrdinalIgnoreCase)))
                result.Blockers.Add("migration.target_task_exists");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { result.Blockers.Add(e is AppException a ? a.MessageKey : "migration.task_mapping_required"); }
        result.Warnings.Add("migration.task_created_disabled");
        return result;
    }

    public async Task<MigrationComponentResult> ApplyAsync(MigrationPlanItemDto item, PortableMigrationManifest manifest,
        MigrationPlanRequest selection, IReadOnlyList<MigrationCreatedCredential> credentials, CancellationToken cancellationToken)
    {
        var result = new MigrationComponentResult();
        try
        {
            var checks = await CheckAsync(item, manifest, selection, cancellationToken);
            if (checks.Blockers.Count != 0) { result.ErrorCode = checks.Blockers[0]; return result; }
            var (request, link) = await BuildRequestAsync(item, manifest, selection, cancellationToken);
            using var pinned = await SiteMutationGuard.CaptureAsync(sites, gates, link.Name, cancellationToken);
            using var reservation = await pinned.ReserveAsync(cancellationToken);
            var actualRoot = Root(pinned.Site.ApplicationRootPath);
            if (!actualRoot.Equals(Root(link.Path), StringComparison.OrdinalIgnoreCase)) throw Error("migration.task_target_site_changed");
            if (!Directory.Exists(actualRoot)) throw Error("migration.task_target_files_missing");
            PortableMigrationStorage.Guard(actualRoot);
            ValidateTargetFiles(request, actualRoot);
            await pinned.ValidateAsync(cancellationToken);
            if ((await scheduler.GetTasksAsync(cancellationToken)).Any(task => task.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase)))
                throw Error("migration.target_task_exists");
            // A native registration can partially commit before readback fails. Never remove it by name.
            result.PartialStatePossible = true;
            var created = await scheduler.CreateTaskAsync(request, ScheduledTaskPrivilege.Limited, cancellationToken);
            result.CreatedResources.Add("task:" + request.Name);
            await pinned.ValidateAsync(cancellationToken);
            var actual = (await scheduler.GetTasksAsync(cancellationToken)).SingleOrDefault(task => task.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase));
            if (actual is null || !Matches(request, created) || !Matches(request, actual)) throw Error("migration.task_metadata_unverified");
            result.Verified = true; result.PartialStatePossible = false;
            result.Evidence.Add("migration.task_disabled_metadata_verified");
            result.RequiredActions.AddRange(["migration.task_acl_and_path_review_required", "migration.task_source_disable_and_schedule_review_required", "migration.task_enable_manually_after_cutover"]);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { result.ErrorCode = e is AppException a ? a.MessageKey : "migration.task_import_failed"; }
        return result;
    }

    private sealed record SiteLink(string Name, string Path, PortableMigrationSite Source);
    private async Task<(CreateScheduledTaskRequest Request, SiteLink Link)> BuildRequestAsync(MigrationPlanItemDto item,
        PortableMigrationManifest manifest, MigrationPlanRequest selection, CancellationToken ct)
    {
        var source = manifest.Tasks.Single(task => task.Id == item.SourceId);
        var link = ResolveSite(item, source, manifest, selection);
        CreateScheduledTaskRequest request;
        if (item.Target.TaskMappingConfirmed)
            request = new() { TaskType = item.Target.TaskType, Command = item.Target.TaskCommand, Arguments = item.Target.TaskArguments,
                WorkingDirectory = item.Target.TaskWorkingDirectory, Url = item.Target.TaskUrl, ScheduleType = item.Target.TaskScheduleType,
                Interval = item.Target.TaskInterval, TimeOfDay = item.Target.TaskTimeOfDay, TimeLimitHours = source.TimeLimitHours, PhpVersion = source.PhpVersion };
        else
        {
            if (manifest.Provider != PortableMigrationProvider.Adapanel || source.SourcePlatform != "windows") throw Error("migration.task_mapping_required");
            var native = await ReadNativeAsync(item, source, manifest, ct);
            if (native.ScheduleUnknown) throw Error("migration.task_schedule_mapping_required");
            request = new() { TaskType = native.TaskType, Command = native.Command, Arguments = native.Arguments, WorkingDirectory = native.WorkingDirectory,
                Url = native.Url, PhpVersion = native.PhpVersion, ScheduleType = native.ScheduleType, Interval = native.Interval,
                TimeOfDay = native.TimeOfDay, TimeLimitHours = native.TimeLimitHours };
            request.WorkingDirectory = string.IsNullOrEmpty(native.WorkingDirectory) ? link.Path : MapSource(native.WorkingDirectory, link, manifest, directory: true);
            if (request.TaskType != "url")
            {
                if (request.TaskType is "php" or "script" || IsAbsolute(request.Command))
                    request.Command = MapSource(request.Command, link, manifest, directory: false);
                request.Arguments = MapArguments(request.Arguments, native.WorkingDirectory, link, manifest);
            }
        }
        request.Name = item.Target.TargetName; request.SiteName = link.Name;
        request.RunAs = "limited"; request.MigrationFreshDisabled = true;
        if (request.TaskType == "url") { request.Command = ""; request.Arguments = ""; }
        SchedulerService.ValidateMigrationRequest(request);
        foreach (var oldSite in manifest.Sites)
        {
            var oldRoot = oldSite.SourcePhysicalPath.Replace('\\', '/').TrimEnd('/');
            if (oldRoot.Length > 0 && !oldRoot.Equals(link.Path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(request.Arguments.Replace('\\', '/'), "(?:^|[\\s\"=])" + Regex.Escape(oldRoot) + "(?=$|/)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw Error("migration.task_arguments_mapping_required");
        }
        if (request.TaskType != "url" && (string.IsNullOrWhiteSpace(request.Command) || UnsafeShell(request.Command) || UnsafeShell(request.Arguments)
            || request.Command.StartsWith('/') || request.Command.Contains('"'))) throw Error("migration.task_command_mapping_required");
        if (request.TaskType == "url" && (!Uri.TryCreate(request.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")))
            throw Error("migration.task_url_mapping_required");
        // Plan checks do not require a target site that has not been provisioned yet.
        if (string.IsNullOrWhiteSpace(request.WorkingDirectory)) request.WorkingDirectory = link.Path;
        if (!Inside(link.Path, request.WorkingDirectory, true)) throw Error("migration.task_working_directory_mapping_required");
        if ((request.TaskType is "php" or "script") && !Inside(link.Path, request.Command, false)) throw Error("migration.task_command_mapping_required");
        return (request, link);
    }

    private static SiteLink ResolveSite(MigrationPlanItemDto item, PortableMigrationTask task, PortableMigrationManifest manifest, MigrationPlanRequest selection)
    {
        var candidates = manifest.Sites.Where(site => task.SiteIds.Contains(site.Id) && (selection.AllSites || selection.SiteIds.Contains(MigrationResourceKey.Create(item.PackageId, site.Id)))).ToArray();
        if (!string.IsNullOrEmpty(item.Target.LinkedSiteId)) candidates = candidates.Where(site => MigrationResourceKey.Create(item.PackageId, site.Id) == item.Target.LinkedSiteId).ToArray();
        if (candidates.Length != 1) throw Error("migration.task_site_mapping_required");
        var source = candidates[0]; var key = MigrationResourceKey.Create(item.PackageId, source.Id);
        var mapping = selection.Mappings.SingleOrDefault(value => value.ResourceId == key);
        var name = string.IsNullOrWhiteSpace(mapping?.TargetName) ? source.Name : mapping.TargetName;
        var path = string.IsNullOrWhiteSpace(mapping?.TargetPath) ? Path.Combine(selection.TargetBasePath, name) : mapping.TargetPath;
        return new(name, Root(path), source);
    }

    private sealed class NativeEnvelope
    {
        public string Format { get; set; } = "";
        public ScheduledTaskDto? Task { get; set; }
    }
    private async Task<ScheduledTaskDto> ReadNativeAsync(MigrationPlanItemDto item, PortableMigrationTask task, PortableMigrationManifest manifest, CancellationToken ct)
    {
        var path = "tasks/" + SourceId(task) + "/task.json";
        if (!task.ArtifactPaths.Contains(path)) throw Error("migration.task_artifact_missing");
        var payload = manifest.Payloads.SingleOrDefault(file => file.Path == path);
        if (payload is null || payload.Size is <= 0 or > MetadataLimit) throw Error("migration.task_artifact_invalid");
        await using var source = await packages.OpenPayloadAsync(item.PackageId, path, ct);
        using var data = new MemoryStream(); var buffer = new byte[8192]; long count = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct); if (read == 0) break;
            count += read; if (count > payload.Size || count > MetadataLimit) throw Error("migration.task_artifact_invalid");
            data.Write(buffer, 0, read);
        }
        if (count != payload.Size || !Convert.ToHexString(SHA256.HashData(data.GetBuffer().AsSpan(0, (int)count))).Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase))
            throw Error("migration.payload_changed");
        using var document = JsonDocument.Parse(data.GetBuffer().AsMemory(0, (int)count), new() { MaxDepth = 16 });
        RejectDuplicateProperties(document.RootElement);
        var envelope = JsonSerializer.Deserialize<NativeEnvelope>(data.GetBuffer().AsSpan(0, (int)count), Json);
        if (envelope?.Format != "adapanel-task-v1" || envelope.Task is null || envelope.Task.Name != task.Name) throw Error("migration.task_artifact_invalid");
        return envelope.Task;
    }
    private static string SourceId(PortableMigrationTask task)
    {
        if (!Regex.IsMatch(task.Id, @"\Atask-[a-f0-9]{24}\z", RegexOptions.CultureInvariant)) throw Error("migration.task_artifact_invalid");
        return task.Id;
    }
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in element.EnumerateObject()) { if (!names.Add(prop.Name)) throw Error("migration.task_artifact_invalid"); RejectDuplicateProperties(prop.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) RejectDuplicateProperties(value);
    }
    private static string MapSource(string source, SiteLink link, PortableMigrationManifest manifest, bool directory)
    {
        var root = SourcePath(link.Source.SourcePhysicalPath); var path = SourcePath(source);
        if (!path.Equals(root, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) throw Error("migration.task_path_mapping_required");
        var relative = path.Length == root.Length ? "" : path[(root.Length + 1)..];
        var payload = link.Source.ContentPath.TrimEnd('/') + "/" + relative;
        if (directory ? relative.Length > 0 && !manifest.Payloads.Any(file => file.Path.StartsWith(payload.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
            && !link.Source.EmptyDirectories.Contains(relative) : !manifest.Payloads.Any(file => file.Path.Equals(payload, StringComparison.OrdinalIgnoreCase)))
            throw Error("migration.task_source_files_missing");
        return relative.Length == 0 ? link.Path : Path.Combine(link.Path, relative.Replace('/', Path.DirectorySeparatorChar));
    }
    private static string MapArguments(string arguments, string workingDirectory, SiteLink link, PortableMigrationManifest manifest)
    {
        if (UnsafeShell(arguments)) throw Error("migration.task_arguments_mapping_required");
        var tokens = Regex.Matches(arguments, "\"[^\"]*\"|[^\\s\"]+").Select(match => match.Value).ToArray();
        if (string.Concat(tokens.Select(token => token.Replace(" ", ""))) != Regex.Replace(arguments, @"\s", "")) throw Error("migration.task_arguments_mapping_required");
        return string.Join(" ", tokens.Select(token =>
        {
            var quoted = token.StartsWith('"'); var raw = quoted ? token[1..^1] : token;
            if (IsAbsolute(raw)) return "\"" + MapSource(raw, link, manifest, false) + "\"";
            if (raw.Contains('/') || raw.Contains('\\') || Regex.IsMatch(raw, @"\.(?:php|py|js|ps1|bat|cmd|exe)\z", RegexOptions.IgnoreCase))
                return "\"" + MapSource(workingDirectory.TrimEnd('\\', '/') + "/" + raw, link, manifest, false) + "\"";
            return token;
        }));
    }
    private static string SourcePath(string value)
    {
        value = value.Replace('\\', '/').TrimEnd('/');
        if (!Regex.IsMatch(value, @"\A[A-Za-z]:/", RegexOptions.CultureInvariant) || value[3..].Split('/').Any(segment => segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') || segment.Contains(':')))
            throw Error("migration.task_path_mapping_required");
        return value;
    }
    private static bool IsAbsolute(string value) => Regex.IsMatch(value, @"\A[A-Za-z]:[\\/]") || value.StartsWith('/') || value.StartsWith('\\');
    private static bool UnsafeShell(string value) => value.IndexOfAny(['&','|','<','>','^','%','!','\r','\n','`']) >= 0 || value.Contains("$(", StringComparison.Ordinal);
    private static string Root(string value) => Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
    private static bool Inside(string root, string path, bool equality)
    { var actual = Root(path); return equality && actual.Equals(Root(root), StringComparison.OrdinalIgnoreCase) || actual.StartsWith(Root(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
    private static void ValidateTargetFiles(CreateScheduledTaskRequest request, string root)
    {
        PortableMigrationStorage.Guard(request.WorkingDirectory);
        if (!Inside(root, request.WorkingDirectory, true) || !Directory.Exists(request.WorkingDirectory)) throw Error("migration.task_target_files_missing");
        if (request.TaskType is "php" or "script" || IsAbsolute(request.Command))
        {
            PortableMigrationStorage.Guard(request.Command);
            if (!File.Exists(request.Command)) throw Error("migration.task_target_files_missing");
        }
    }
    private static bool Matches(CreateScheduledTaskRequest request, ScheduledTaskDto actual)
        => actual.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase) && actual.State.Equals("Disabled", StringComparison.OrdinalIgnoreCase)
        && actual.RunAs == "limited" && actual.TaskType == request.TaskType && !actual.ScheduleUnknown
        && (actual.ScheduleType == request.ScheduleType || (actual.ScheduleType is "minute" or "minutely") && (request.ScheduleType is "minute" or "minutely"))
        && (request.ScheduleType == "daily" ? actual.TimeOfDay == request.TimeOfDay : actual.Interval == request.Interval)
        && (request.TaskType != "php" || string.IsNullOrWhiteSpace(request.PhpVersion) || actual.PhpVersion == request.PhpVersion)
        && actual.TimeLimitHours == request.TimeLimitHours && actual.WorkingDirectory == request.WorkingDirectory
        && (request.TaskType == "url" ? actual.Url == request.Url : actual.Command == request.Command && actual.Arguments == request.Arguments);
    private static AppException Error(string code) => new(code, 409, 409);
}
