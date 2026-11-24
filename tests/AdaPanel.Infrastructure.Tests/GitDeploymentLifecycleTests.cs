using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Git;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Git;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class GitDeploymentLifecycleTests
{
    [Theory]
    [InlineData("init")]
    [InlineData("remote add ")]
    [InlineData("fetch ")]
    [InlineData("checkout ")]
    public async Task InitialDeploymentStopsAtTheFirstFailedStage(string stage)
    {
        using var f = new Fixture(); await f.Configure("one"); f.FailCommandPrefix = stage;
        var result = await f.Service.DeployAsync("site");
        Assert.False(result.Success);
        Assert.Contains("fixture stage failed", result.Output);
        Assert.Equal("failed", (await f.Service.GetAsync("site")).LastDeployStatus);
        Assert.DoesNotContain(f.Commands, args => args.StartsWith("reset --hard "));
        var failed = f.Commands.FindIndex(args => args.StartsWith(stage, StringComparison.Ordinal));
        Assert.All(f.Commands.Skip(failed + 1), args => Assert.StartsWith("rev-parse ", args));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedDeploymentReadsCurrentSettingsAndRechecksRotatedWebhookSecret(bool webhook)
    {
        using var f = new Fixture();
        var old = await f.Configure("old");
        f.Gate.HoldNext = true;
        var changing = f.Configure("new", rotate: true);
        await f.Gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deployment = webhook ? f.Service.DeployViaWebhookAsync("site", old.WebhookSecret) : f.Service.DeployAsync("site");
        await f.Gate.Queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Gate.Release.TrySetResult();
        var current = await changing;
        if (webhook)
        {
            Assert.Equal("git.webhook_unauthorized", (await Assert.ThrowsAsync<AppException>(() => deployment)).MessageKey);
            Assert.Empty(f.Commands);
        }
        else
        {
            Assert.True((await deployment).Success);
            Assert.Contains(f.Commands, args => args.Contains("new.git", StringComparison.Ordinal));
            Assert.DoesNotContain(f.Commands, args => args.Contains("old.git", StringComparison.Ordinal));
        }
        var saved = await f.Service.GetAsync("site");
        Assert.Equal(current.RepositoryUrl, saved.RepositoryUrl);
        Assert.Equal(current.Branch, saved.Branch);
        Assert.Equal(current.WebhookSecret, saved.WebhookSecret);
    }

    [Fact]
    public async Task LongDeploymentDoesNotBlockUnrelatedIisQueue()
    {
        using var f = new Fixture(); await f.Configure("one");
        f.Gate.HoldNext = true;
        var deployment = f.Service.DeployAsync("site");
        await f.Gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await f.Gate.RunExclusiveAsync(() => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { f.Gate.Release.TrySetResult(); }
        await deployment;
    }

    [Fact]
    public async Task ResultPublicationDoesNotOverwriteSettingsChangedByAnotherWriter()
    {
        using var f = new Fixture(); await f.Configure("one");
        f.BeforeCommand = async args =>
        {
            if (!args.StartsWith("rev-parse ", StringComparison.Ordinal)) return;
            var path = GitDeploymentService.ConfigPath("site");
            var state = await f.Store.ReadAsync(path, new JsonObject());
            state["repositoryUrl"] = "https://repository.test/replaced.git";
            state["branch"] = "replacement"; state["webhookSecret"] = "rotated-fixture";
            await f.Store.WriteAsync(path, state);
        };
        Assert.True((await f.Service.DeployAsync("site")).Success);
        var saved = await f.Service.GetAsync("site");
        Assert.Equal("https://repository.test/replaced.git", saved.RepositoryUrl);
        Assert.Equal("replacement", saved.Branch); Assert.Equal("rotated-fixture", saved.WebhookSecret);
        Assert.Equal("succeeded", saved.LastDeployStatus);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-git-lifecycle-" + Guid.NewGuid().ToString("N"));
        internal readonly Gate Gate = new();
        internal readonly List<string> Commands = [];
        internal readonly JsonFileStore Store;
        internal readonly GitDeploymentService Service;
        internal Func<string, Task>? BeforeCommand;
        internal string? FailCommandPrefix;
        internal Fixture()
        {
            Directory.CreateDirectory(root);
            Store = new(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
            var iis = DispatchProxy.Create<IIisSiteService, PostgresDatabaseIdentityConsumersTests.StrictProxy>();
            ((PostgresDatabaseIdentityConsumersTests.StrictProxy)(object)iis).Handler = (method, _) =>
                method.Name == "GetSiteAsync" ? Task.FromResult(new SiteDto { Name = "site", PhysicalPath = root, ApplicationRootPath = root })
                    : throw new NotSupportedException(method.Name);
            Service = new(iis, Store, Gate, NullLogger<GitDeploymentService>.Instance,
                outboundResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }))
            {
                CommandRunner = async (args, _, _) =>
                {
                    Commands.Add(args);
                    if (BeforeCommand is not null) await BeforeCommand(args);
                    if (FailCommandPrefix is not null && args.StartsWith(FailCommandPrefix, StringComparison.Ordinal))
                        return new CommandResult { ExitCode = 1, StandardError = "fixture stage failed" };
                    return new CommandResult { ExitCode = 0, StandardOutput = args.StartsWith("rev-parse ") ? "fixture-commit" : "" };
                }
            };
        }
        internal Task<GitDeploymentDto> Configure(string name, bool rotate = false) => Service.ConfigureAsync("site",
            new() { RepositoryUrl = $"https://repository.test/{name}.git", Branch = name, RegenerateSecret = rotate });
        public void Dispose() => Directory.Delete(root, true);
    }

    private sealed class Gate : IConcurrencyLock
    {
        private readonly Dictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);
        internal bool HoldNext;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Queued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RunExclusiveAsync(Func<Task> operation, CancellationToken token = default) => RunExclusiveAsync(ConcurrencyKeys.Iis, operation, token);
        public Task<T> RunExclusiveAsync<T>(Func<Task<T>> operation, CancellationToken token = default) => RunExclusiveAsync(ConcurrencyKeys.Iis, operation, token);
        public async Task RunExclusiveAsync(string key, Func<Task> operation, CancellationToken token = default) =>
            await RunExclusiveAsync(key, async () => { await operation(); return true; }, token);
        public async Task<T> RunExclusiveAsync<T>(string key, Func<Task<T>> operation, CancellationToken token = default)
        {
            if (!gates.TryGetValue(key, out var gate)) gates[key] = gate = new(1, 1);
            if (gate.CurrentCount == 0) Queued.TrySetResult();
            await gate.WaitAsync(token);
            try
            {
                if (HoldNext) { HoldNext = false; Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
                return await operation();
            }
            finally { gate.Release(); }
        }
    }
}
