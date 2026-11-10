using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Dtos.Scheduler;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Migration.Full;
using AdaPanel.Infrastructure.Migration.Packages;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using AdaPanel.Infrastructure.Services;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MigrationTaskTargetTests
{
    [Theory]
    [InlineData("command")]
    [InlineData("php")]
    [InlineData("script")]
    [InlineData("url")]
    public async Task Native_private_payload_maps_selected_site_and_creates_disabled_limited_task(string type)
    {
        using var f = new Fixture(); f.Native.TaskType = type;
        if (type is "php" or "script") f.Native.Command = @"C:\source\web\job.ps1";
        if (type == "url") f.Native.Url = "https://example.test/cron";
        await f.Build();
        Assert.Empty((await f.Target.CheckAsync(f.Item, f.Manifest, f.Selection, default)).Blockers);
        var result = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, [], default);
        Assert.True(result.Verified, result.ErrorCode); Assert.False(result.PartialStatePossible);
        Assert.NotNull(f.Created); Assert.True(f.Created.MigrationFreshDisabled); Assert.Equal("limited", f.Created.RunAs);
        Assert.Equal(ScheduledTaskPrivilege.Limited, f.Privilege); Assert.Equal(f.Web, f.Created.WorkingDirectory);
        Assert.Equal(3, result.RequiredActions.Count); Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Plan_does_not_require_target_site_or_files_that_will_be_created_by_site_step()
    {
        using var f = new Fixture(); await f.Build(); Directory.Delete(f.Web, recursive: true); f.SiteAvailable = false;
        Assert.Empty((await f.Target.CheckAsync(f.Item, f.Manifest, f.Selection, default)).Blockers);
        var result = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, [], default);
        Assert.False(result.Verified); Assert.Null(f.Created);
    }

    [Theory]
    [InlineData("arguments")]
    [InlineData("working-prefix")]
    [InlineData("outside-command")]
    [InlineData("traversal")]
    [InlineData("missing-payload")]
    [InlineData("unknown-schedule")]
    public async Task Unsafe_or_unproven_native_inputs_are_explicit_mapping_blockers(string fault)
    {
        using var f = new Fixture();
        if (fault == "arguments") f.Native.Arguments = "--source=C:\\source\\web\\job.ps1 && echo SECRET";
        if (fault == "working-prefix") f.Native.WorkingDirectory = @"C:\source\web-old";
        if (fault == "outside-command") f.Native.Command = @"C:\external\python.exe";
        if (fault == "traversal") f.Native.Arguments = @"C:\source\web\..\secret.py";
        if (fault == "missing-payload") f.Native.Arguments = @"C:\source\web\missing.py";
        if (fault == "unknown-schedule") f.Native.ScheduleUnknown = true;
        await f.Build();
        var check = await f.Target.CheckAsync(f.Item, f.Manifest, f.Selection, default);
        Assert.NotEmpty(check.Blockers); Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(check)); Assert.Null(f.Created);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("format")]
    [InlineData("missing")]
    [InlineData("oversize")]
    public async Task Native_metadata_is_bounded_strict_and_hash_checked(string fault)
    {
        using var f = new Fixture(); await f.Build(fault);
        Assert.NotEmpty((await f.Target.CheckAsync(f.Item, f.Manifest, f.Selection, default)).Blockers);
        Assert.Null(f.Created);
    }

    [Theory]
    [InlineData(PortableMigrationProvider.Cpanel)]
    [InlineData(PortableMigrationProvider.Plesk)]
    public async Task Linux_cron_requires_explicit_Windows_mapping(PortableMigrationProvider provider)
    {
        using var f = new Fixture(); await f.Build(provider: provider);
        Assert.Contains("migration.task_mapping_required", (await f.Target.CheckAsync(f.Item, f.Manifest, f.Selection, default)).Blockers);
        f.Item.Target.TaskMappingConfirmed = true; f.Item.Target.TaskType = "command"; f.Item.Target.TaskCommand = "python.exe";
        f.Item.Target.TaskWorkingDirectory = f.Web; f.Item.Target.TaskArguments = "job.ps1";
        f.Item.Target.TaskScheduleType = "hourly"; f.Item.Target.TaskInterval = 2;
        var result = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, [], default);
        Assert.True(result.Verified, result.ErrorCode); Assert.Equal(ScheduledTaskPrivilege.Limited, f.Privilege);
    }

    [Theory]
    [InlineData("enabled")]
    [InlineData("metadata")]
    [InlineData("principal")]
    [InlineData("failure")]
    public async Task Unexpected_native_readback_is_partial_and_is_never_deleted_or_started(string fault)
    {
        using var f = new Fixture { Failure = fault }; await f.Build();
        var result = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, [], default);
        Assert.False(result.Verified); Assert.True(result.PartialStatePossible);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(result)); Assert.NotNull(f.Created);
    }

    [Fact]
    public async Task Existing_task_is_preserved_without_create_or_disable()
    {
        using var f = new Fixture { Existing = true }; await f.Build();
        var result = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, [], default);
        Assert.Equal("migration.target_task_exists", result.ErrorCode); Assert.Null(f.Created);
    }

    [Fact]
    public async Task Actual_site_replacement_or_path_mismatch_prevents_registration()
    {
        using var f = new Fixture { ChangedPath = true }; await f.Build();
        var result = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, [], default);
        Assert.False(result.Verified); Assert.Null(f.Created);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("php-version", false)]
    public async Task Requested_native_PHP_version_cannot_be_silently_replaced_by_runtime_fallback(string fault, bool expected)
    {
        using var f = new Fixture { Failure = fault };
        f.Native.TaskType = "php"; f.Native.Command = @"C:\source\web\job.ps1"; f.Native.PhpVersion = "8.3";
        await f.Build();
        var result = await f.Target.ApplyAsync(f.Item, f.Manifest, f.Selection, [], default);
        Assert.Equal(expected, result.Verified);
        Assert.NotNull(f.Created); Assert.Equal("8.3", f.Created.PhpVersion);
        if (!expected) { Assert.Equal("migration.task_metadata_unverified", result.ErrorCode); Assert.True(result.PartialStatePossible); }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "ada-task-target-" + Guid.NewGuid().ToString("N"));
        internal string Web => Path.Combine(Root, "web");
        internal bool Existing, ChangedPath, SiteAvailable = true;
        internal string Failure = "";
        internal ScheduledTaskDto Native = new() { Name = "source-job", TaskType = "command", Command = "python.exe", Arguments = "\"C:\\source\\web\\job.ps1\" SECRET", WorkingDirectory = @"C:\source\web", ScheduleType = "hourly", Interval = 2, TimeOfDay = "00:00" };
        internal MigrationTaskTarget Target;
        internal PortableMigrationManifest Manifest = new();
        internal MigrationPlanItemDto Item = new();
        internal MigrationPlanRequest Selection = new() { AllSites = true, IncludeTasks = true };
        internal CreateScheduledTaskRequest? Created;
        internal ScheduledTaskPrivilege Privilege;
        private ScheduledTaskDto? actual;
        private readonly PortableMigrationPackageStore store;
        internal Fixture()
        {
            Directory.CreateDirectory(Web); File.WriteAllText(Path.Combine(Web, "job.ps1"), "fixture script");
            store = new(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Root }), Microsoft.Extensions.Options.Options.Create(new PortableMigrationPackageOptions()));
            var scheduler = Stub<ISchedulerService>((method, args) => method.Name switch
            {
                nameof(ISchedulerService.GetTasksAsync) => Task.FromResult<IReadOnlyCollection<ScheduledTaskDto>>(Existing ? [new() { Name = "target-job" }] : actual is null ? [] : [actual]),
                nameof(ISchedulerService.CreateTaskAsync) => Create((CreateScheduledTaskRequest)args![0]!, (ScheduledTaskPrivilege)args[1]!),
                _ => throw new InvalidOperationException("No run/delete/disable action is allowed: " + method.Name)
            });
            var sites = Stub<IIisSiteService>((_, _) => SiteAvailable ? Task.FromResult(new SiteDto { Id = 123, Name = "target.test", PhysicalPath = Web, ApplicationRootPath = ChangedPath ? Root : Web, ApplicationPoolName = "pool" }) : throw new AppException("sites.not_found", 404, 404));
            Target = new(scheduler, store, sites, new ConcurrencyLock());
        }
        internal async Task Build(string fault = "", PortableMigrationProvider provider = PortableMigrationProvider.Adapanel)
        {
            var id = "task-aaaaaaaaaaaaaaaaaaaaaaaa"; var metadata = "tasks/" + id + "/task.json";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { format = fault == "format" ? "other" : "adapanel-task-v1", task = Native }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (fault == "duplicate") bytes = System.Text.Encoding.UTF8.GetBytes("{\"format\":\"adapanel-task-v1\",\"format\":\"other\",\"task\":{}}");
            if (fault == "unknown") bytes = System.Text.Encoding.UTF8.GetBytes("{\"format\":\"adapanel-task-v1\",\"extra\":true,\"task\":{}}");
            var file = System.Text.Encoding.UTF8.GetBytes("fixture script");
            Manifest = new() { PackageId = Guid.NewGuid(), Provider = provider, Complete = true, SourceName = "fixture", CreatedAtUtc = DateTimeOffset.UtcNow,
                Sites = [new() { Id = "site-one", Name = "source.test", SourcePhysicalPath = @"C:\source\web", ContentPath = "sites/one", FileCount = 1 }],
                Tasks = [new() { Id = id, Name = Native.Name, Kind = "command", SiteIds = ["site-one"], SourcePlatform = "windows", ArtifactPaths = fault == "missing" ? [] : [metadata] }],
                Payloads = [new() { Path = metadata, Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) }, new() { Path = "sites/one/job.ps1", Size = file.Length, Sha256 = Convert.ToHexString(SHA256.HashData(file)) }] };
            using var data = new MemoryStream();
            using (var zip = new ZipArchive(data, ZipArchiveMode.Create, true))
            {
                using (var entry = zip.CreateEntry(metadata).Open()) entry.Write(bytes);
                using (var entry = zip.CreateEntry("sites/one/job.ps1").Open()) entry.Write(file);
                using var json = zip.CreateEntry("manifest.json").Open(); JsonSerializer.Serialize(json, Manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            data.Position = 0; var package = await store.UploadAsync(data); Manifest = package.Manifest;
            if (fault == "hash") Manifest.Payloads[0] = new() { Path = metadata, Size = bytes.Length, Sha256 = new string('0', 64) };
            if (fault == "oversize") Manifest.Payloads[0] = new() { Path = metadata, Size = 1024 * 1024 + 1, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
            Item = new() { PackageId = package.Id, SourceId = id, Kind = "task", Target = new() { TargetName = "target-job" } };
            Selection.Mappings = [new() { ResourceId = MigrationResourceKey.Create(package.Id, "site-one"), TargetName = "target.test", TargetPath = Web }];
        }
        private Task<ScheduledTaskDto> Create(CreateScheduledTaskRequest request, ScheduledTaskPrivilege privilege)
        {
            Created = request; Privilege = privilege;
            if (Failure == "failure") throw new InvalidOperationException("SECRET");
            actual = new() { Name = request.Name, State = Failure == "enabled" ? "Ready" : "Disabled", TaskType = request.TaskType,
                PhpVersion = Failure == "php-version" ? "8.2" : request.PhpVersion,
                Command = request.Command, Arguments = Failure == "metadata" ? "changed" : request.Arguments,
                WorkingDirectory = request.WorkingDirectory, Url = request.Url, RunAs = Failure == "principal" ? "system" : "limited",
                ScheduleType = request.ScheduleType, Interval = request.Interval, TimeOfDay = request.TimeOfDay, TimeLimitHours = request.TimeLimitHours };
            return Task.FromResult(actual);
        }
        public void Dispose() => PortableMigrationStorage.DeleteOwnedDirectory(Root);
    }
    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    { var proxy = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)proxy).Handler = handler; return proxy; }
    public class Proxy : DispatchProxy
    { public Func<MethodInfo, object?[]?, object?> Handler = null!; protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args); }
}
