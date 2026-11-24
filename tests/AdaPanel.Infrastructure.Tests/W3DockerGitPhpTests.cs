using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AdaPanel.Api.Controllers;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Core.Dtos.Docker;
using AdaPanel.Core.Dtos.Git;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Entities;
using AdaPanel.Core.Enums;
using AdaPanel.Core.Interfaces;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Docker;
using AdaPanel.Infrastructure.Git;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Php;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>Dalga 3 / Docker limit + site ilişkisi, Git dağıtım anahtarı/jeton/derleme sonrası/geri alma, PHP sürüm kaldırma.</summary>
public sealed class W3DockerGitPhpTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-w3-" + Guid.NewGuid().ToString("N"));

    public W3DockerGitPhpTests() => Directory.CreateDirectory(root);

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, PostgresDatabaseIdentityConsumersTests.StrictProxy>();
        ((PostgresDatabaseIdentityConsumersTests.StrictProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    // ================= Docker =================

    private static DockerService Docker(List<string> commands, Func<string, CommandResult?> respond) =>
        new(NullLogger<DockerService>.Instance)
        { CommandRunner = (args, _) => { commands.Add(args); return Task.FromResult(respond(args)); } };

    private static CommandResult Ok(string output = "") => new() { ExitCode = 0, StandardOutput = output };

    private static CommandResult Dispatch(string args) =>
        args.StartsWith("version ") ? Ok("26.0") : Ok();

    [Fact]
    public async Task RunWithLimitsAndSiteBuildsTheExpectedFlagsAndLabel()
    {
        var commands = new List<string>();
        await Docker(commands, Dispatch).RunContainerAsync(new RunContainerRequest
        { Image = "nginx:1", Name = "web-1", SiteName = "app.test", CpuLimit = "0.5", MemoryLimit = "256M" });
        var run = Assert.Single(commands, c => c.StartsWith("run ", StringComparison.Ordinal));
        Assert.Contains("--cpus 0.5", run);
        Assert.Contains("--memory 256m", run);
        Assert.DoesNotContain("memory-swap", run);
        Assert.Contains("--label adapanel.site=app.test", run);
    }

    [Theory]
    [InlineData("abc", "")]
    [InlineData("0", "")]
    [InlineData("0.001", "")]
    [InlineData("1; rm", "")]
    [InlineData("", "5m")]
    [InlineData("", "10x")]
    [InlineData("", "256m --privileged")]
    [InlineData("", "-1g")]
    public async Task InvalidLimitsAreRejectedBeforeAnyDockerCommandRuns(string cpu, string memory)
    {
        var commands = new List<string>();
        var error = await Assert.ThrowsAsync<AppException>(() => Docker(commands, Dispatch)
            .RunContainerAsync(new RunContainerRequest { Image = "nginx", CpuLimit = cpu, MemoryLimit = memory }));
        Assert.Equal("docker.invalid_limit", error.MessageKey);
        Assert.DoesNotContain(commands, c => c.StartsWith("run ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("x\" --privileged")]
    [InlineData("-evil")]
    public async Task InvalidSiteNamesCannotInjectFlags(string site)
    {
        var commands = new List<string>();
        var error = await Assert.ThrowsAsync<AppException>(() => Docker(commands, Dispatch)
            .RunContainerAsync(new RunContainerRequest { Image = "nginx", SiteName = site }));
        Assert.Equal("docker.invalid_site", error.MessageKey);
        Assert.DoesNotContain(commands, c => c.StartsWith("run ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateLimitsSetsOrClearsBothLimitsWithoutSwapFlags()
    {
        var commands = new List<string>();
        var service = Docker(commands, Dispatch);
        await service.UpdateContainerLimitsAsync("web-1", new UpdateContainerLimitsRequest { CpuLimit = "1.5", MemoryLimit = "512m" });
        Assert.Contains("update --cpus 1.5 --memory 512m web-1", commands);
        await service.UpdateContainerLimitsAsync("web-1", new UpdateContainerLimitsRequest());
        Assert.Contains("update --cpus 0 --memory 0 web-1", commands);
    }

    [Fact]
    public async Task ContainersExposeTheirSiteFromTheLabel()
    {
        var json = "{\"ID\":\"a1\",\"Names\":\"web-1\",\"Image\":\"nginx\",\"State\":\"running\",\"Status\":\"Up\",\"Ports\":\"\",\"Labels\":\"x=y,adapanel.site=app.test\"}\n"
            + "{\"ID\":\"b2\",\"Names\":\"other\",\"Image\":\"redis\",\"State\":\"running\",\"Status\":\"Up\",\"Ports\":\"\",\"Labels\":\"\"}";
        var workspace = await Docker([], args => args.StartsWith("version ") ? Ok("26.0") : args.StartsWith("ps ") ? Ok(json) : Ok()).GetWorkspaceAsync();
        Assert.Equal("app.test", workspace.Containers.Single(c => c.Id == "a1").Site);
        Assert.Equal(string.Empty, workspace.Containers.Single(c => c.Id == "b2").Site);
    }

    [Fact]
    public async Task SiteCleanupRemovesOnlyLabelledContainersAndReportsFailuresByName()
    {
        var commands = new List<string>();
        var ps = "{\"ID\":\"a1\",\"Names\":\"web-1\"}\n{\"ID\":\"b2\",\"Names\":\"db-1\"}";
        var service = Docker(commands, args =>
            args.StartsWith("version ") ? Ok("26.0")
            : args.StartsWith("ps ") ? Ok(ps)
            : args == "rm -f b2" ? new CommandResult { ExitCode = 1, StandardError = "in use" }
            : Ok());
        var result = await service.RemoveSiteContainersAsync("app.test");
        Assert.Contains(commands, c => c.Contains("--filter label=adapanel.site=app.test", StringComparison.Ordinal));
        Assert.Equal(["web-1"], result.Removed);
        Assert.Single(result.Failed);
        Assert.StartsWith("db-1:", result.Failed[0]);
        Assert.True(result.Installed && result.DaemonRunning);
    }

    [Fact]
    public async Task SiteCleanupWithoutDockerOrWithADownEngineSaysSoInsteadOfClaimingSuccess()
    {
        var absent = await Docker([], _ => null).RemoveSiteContainersAsync("app.test");
        Assert.False(absent.Installed);
        var down = await Docker([], _ => new CommandResult { ExitCode = 1 }).RemoveSiteContainersAsync("app.test");
        Assert.True(down.Installed);
        Assert.False(down.DaemonRunning);
    }

    [Fact]
    public async Task DockerLimitEndpointsStayServerOwnerOnly()
    {
        var audit = new RecordingAuditService();
        var controller = new DockerController(null!, new Owner(false), audit)
        { ControllerContext = new ControllerContext { HttpContext = Context("Administrator") } };
        var update = await Assert.ThrowsAsync<AppException>(() =>
            controller.UpdateLimitsAsync("web-1", new UpdateContainerLimitsRequest(), CancellationToken.None));
        var read = await Assert.ThrowsAsync<AppException>(() => controller.GetLimitsAsync("web-1", CancellationToken.None));
        Assert.Equal(403, update.HttpStatusCode);
        Assert.Equal(403, read.HttpStatusCode);
    }

    // ================= Git =================

    private const string PrivateKeyText = "-----BEGIN OPENSSH PRIVATE KEY-----\nSUPERSECRETKEYMATERIAL\n-----END OPENSSH PRIVATE KEY-----\n";
    private static readonly string PublicKeyText = "ssh-ed25519 " + Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray()) + " adapanel-site";

    private sealed class Protector : ISecretProtector
    {
        public string Protect(string plaintext) => "PROT:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string protectedValue) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue["PROT:".Length..]));
    }

    private sealed class GitFixture
    {
        public required GitDeploymentService Service { get; init; }
        public required JsonFileStore Store { get; init; }
        public required List<string> Commands { get; init; }
        public required Dictionary<string, string> LastEnvironment { get; init; }
        public string SitePath { get; init; } = string.Empty;
        public Func<string, CommandResult>? Respond { get; set; }
        public Func<string, TimeSpan, Task<CommandResult>>? Post { get; set; }
        public List<string> PostRuns { get; } = [];
        public string? KeyContentSeenDuringRun { get; set; }
        public string? KeyPathSeenDuringRun { get; set; }
    }

    private GitFixture Git(bool withProtector = true)
    {
        var dataRoot = Path.Combine(root, "data" + Guid.NewGuid().ToString("N")[..6]);
        var sitePath = Path.Combine(root, "site" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(sitePath);
        var store = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = dataRoot }));
        var iis = Fake<IIisSiteService>((method, _) => method.Name == "GetSiteAsync"
            ? Task.FromResult(new SiteDto { Name = "site", PhysicalPath = sitePath, ApplicationRootPath = sitePath })
            : throw new NotSupportedException(method.Name));
        var commands = new List<string>();
        var environment = new Dictionary<string, string>();
        GitFixture? fixture = null;
        var service = new GitDeploymentService(iis, store, new ConcurrencyLock(), NullLogger<GitDeploymentService>.Instance,
            outboundResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
            secretProtector: withProtector ? new Protector() : null)
        {
            CommandRunner = (args, _, _) =>
            {
                commands.Add(args);
                return Task.FromResult(fixture!.Respond?.Invoke(args) ?? Ok(args.StartsWith("rev-parse ") ? "fixture-commit" : ""));
            },
            DeployKeyGenerator = (_, _) => Task.FromResult((PrivateKeyText, PublicKeyText)),
            EnvironmentObserver = env =>
            {
                environment.Clear();
                foreach (var (key, value) in env) environment[key] = value;
                if (env.TryGetValue("GIT_SSH_COMMAND", out var ssh))
                {
                    var start = ssh.IndexOf("-i '", StringComparison.Ordinal) + 4;
                    var path = ssh[start..ssh.IndexOf('\'', start)];
                    fixture!.KeyPathSeenDuringRun = path;
                    fixture.KeyContentSeenDuringRun = File.Exists(path) ? File.ReadAllText(path) : null;
                }
            },
            PostDeployRunner = (id, _, timeout, _) =>
            {
                fixture!.PostRuns.Add(id);
                return fixture.Post?.Invoke(id, timeout) ?? Task.FromResult(Ok("done"));
            }
        };
        return fixture = new GitFixture { Service = service, Store = store, Commands = commands, LastEnvironment = environment, SitePath = sitePath };
    }

    private static string ConfigText(GitFixture f) => File.ReadAllText(f.Store.ResolvePath(GitDeploymentService.ConfigPath("site")));

    private static Task<GitDeploymentDto> Configure(GitFixture f, string url) =>
        f.Service.ConfigureAsync("site", new ConfigureGitDeploymentRequest { RepositoryUrl = url, Branch = "main" });

    [Fact]
    public async Task DeployKeyIsShownAsPublicKeyOnlyAndStoredProtected()
    {
        var f = Git();
        await Configure(f, "git@example.test:me/app.git");
        var dto = await f.Service.GenerateDeployKeyAsync("site");

        Assert.True(dto.HasDeployKey);
        Assert.Equal(PublicKeyText, dto.DeployPublicKey);
        Assert.StartsWith("SHA256:", dto.DeployKeyFingerprint);
        var json = JsonSerializer.Serialize(dto);
        Assert.DoesNotContain("SUPERSECRETKEYMATERIAL", json);
        Assert.DoesNotContain("BEGIN OPENSSH", json);

        var stored = ConfigText(f);
        Assert.DoesNotContain("SUPERSECRETKEYMATERIAL", stored);
        Assert.DoesNotContain("BEGIN OPENSSH", stored);
        Assert.Contains("PROT:", stored);
    }

    [Fact]
    public async Task DeployKeyWithoutASecretStoreFailsClosed()
    {
        var f = Git(withProtector: false);
        await Configure(f, "git@example.test:me/app.git");
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.GenerateDeployKeyAsync("site"));
        Assert.Equal("git.secret_store_unavailable", error.MessageKey);
        Assert.DoesNotContain("SUPERSECRETKEYMATERIAL", ConfigText(f));
    }

    [Fact]
    public async Task SshDeployUsesATemporaryKeyFileThatIsGoneAfterwardAndNeverReachesTheOutput()
    {
        var f = Git();
        await Configure(f, "git@example.test:me/app.git");
        await f.Service.GenerateDeployKeyAsync("site");
        f.Respond = args => args.StartsWith("fetch ") || args.StartsWith("checkout ") || args.StartsWith("reset ")
            ? new CommandResult { ExitCode = 0, StandardOutput = "debug " + PrivateKeyText.Trim() } : Ok("fixture-commit");

        var result = await f.Service.DeployAsync("site");

        Assert.Contains("GIT_SSH_COMMAND", f.LastEnvironment.Keys);
        Assert.Equal(PrivateKeyText, f.KeyContentSeenDuringRun);
        Assert.False(File.Exists(f.KeyPathSeenDuringRun!), "temporary key file must be wiped after the run");
        Assert.DoesNotContain("SUPERSECRETKEYMATERIAL", result.Output);
        Assert.DoesNotContain("SUPERSECRETKEYMATERIAL", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("SUPERSECRETKEYMATERIAL", string.Join('\n', f.Commands));
    }

    [Fact]
    public async Task HttpsDeployKeepsTheTokenOutOfCommandLinesOutputAndStorage()
    {
        var f = Git();
        await Configure(f, "https://repository.test/me/app.git");
        var dto = await f.Service.SetHttpsTokenAsync("site", new SetGitHttpsTokenRequest { Username = "ci-bot", Token = "tok-ABC123-secret" });
        Assert.True(dto.HasHttpsToken);
        Assert.Equal("ci-bot", dto.HttpsUsername);
        Assert.DoesNotContain("tok-ABC123-secret", JsonSerializer.Serialize(dto));
        Assert.DoesNotContain("tok-ABC123-secret", ConfigText(f));
        f.Respond = args => args.StartsWith("fetch ")
            ? new CommandResult { ExitCode = 1, StandardError = "fatal: auth failed for tok-ABC123-secret" } : Ok("fixture-commit");

        var result = await f.Service.DeployAsync("site");

        Assert.False(result.Success);
        Assert.DoesNotContain("tok-ABC123-secret", result.Output);
        Assert.Contains("[redacted]", result.Output);
        Assert.DoesNotContain("tok-ABC123-secret", string.Join('\n', f.Commands));
        var header = f.LastEnvironment["GIT_CONFIG_VALUE_0"];
        Assert.Equal("Authorization: Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("ci-bot:tok-ABC123-secret")), header);
        Assert.Equal("http.extraHeader", f.LastEnvironment["GIT_CONFIG_KEY_0"]);
    }

    [Theory]
    [InlineData("http://repository.test/app.git")]
    [InlineData("git@example.test:me/app.git")]
    public async Task HttpsTokenIsRefusedForNonHttpsRepositories(string url)
    {
        var f = Git();
        await Configure(f, url);
        var error = await Assert.ThrowsAsync<AppException>(() =>
            f.Service.SetHttpsTokenAsync("site", new SetGitHttpsTokenRequest { Username = "u", Token = "t0ken-value" }));
        Assert.Equal("git.token_requires_https", error.MessageKey);
        Assert.DoesNotContain("t0ken-value", ConfigText(f));
    }

    [Theory]
    [InlineData("bad user", "tok")]
    [InlineData("user", "tok en")]
    [InlineData("user", "tok\nen")]
    public async Task MalformedTokenInputIsRejected(string user, string token)
    {
        var f = Git();
        await Configure(f, "https://repository.test/app.git");
        await Assert.ThrowsAsync<AppException>(() => f.Service.SetHttpsTokenAsync("site", new SetGitHttpsTokenRequest { Username = user, Token = token }));
    }

    [Fact]
    public async Task RemovingKeyAndTokenClearsBothFromStorage()
    {
        var f = Git();
        await Configure(f, "https://repository.test/app.git");
        await f.Service.SetHttpsTokenAsync("site", new SetGitHttpsTokenRequest { Username = "u", Token = "tok-to-remove" });
        await f.Service.GenerateDeployKeyAsync("site");
        var dto = await f.Service.RemoveHttpsTokenAsync("site");
        dto = await f.Service.RemoveDeployKeyAsync("site");
        Assert.False(dto.HasHttpsToken);
        Assert.False(dto.HasDeployKey);
        var stored = ConfigText(f);
        Assert.DoesNotContain("PROT:", stored);
        Assert.DoesNotContain("ssh-ed25519", stored);
    }

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("composer install; calc")]
    [InlineData("npm run build && whoami")]
    [InlineData("COMPOSER-INSTALL")]
    [InlineData("")]
    public async Task PostDeployAcceptsOnlyAllowListIds(string command)
    {
        var f = Git();
        await Configure(f, "https://repository.test/app.git");
        var error = await Assert.ThrowsAsync<AppException>(() =>
            f.Service.SetPostDeployAsync("site", new SetGitPostDeployRequest { Commands = [command] }));
        Assert.Equal("git.post_deploy_not_allowed", error.MessageKey);
    }

    [Fact]
    public async Task PostDeployRunsAllowedIdsInOrderAfterTheCodeUpdate()
    {
        var f = Git();
        await Configure(f, "https://repository.test/app.git");
        var dto = await f.Service.SetPostDeployAsync("site", new SetGitPostDeployRequest { Commands = ["composer-install", "npm-ci", "npm-run-build"], TimeoutSeconds = 90 });
        Assert.Equal(["composer-install", "npm-ci", "npm-run-build"], dto.PostDeployCommands);
        Assert.Equal(3, dto.AllowedPostDeployCommands.Length);

        var result = await f.Service.DeployAsync("site");

        Assert.True(result.Success);
        Assert.Equal(["composer-install", "npm-ci", "npm-run-build"], f.PostRuns);
        var entry = Assert.Single(result.Deployment.RecentDeployments);
        Assert.Equal("composer-install: ok; npm-ci: ok; npm-run-build: ok", entry.PostDeploy);
    }

    [Fact]
    public async Task AFailingPostDeployCommandStopsTheChainAndMarksTheDeploymentFailed()
    {
        var f = Git();
        await Configure(f, "https://repository.test/app.git");
        await f.Service.SetPostDeployAsync("site", new SetGitPostDeployRequest { Commands = ["npm-ci", "npm-run-build"] });
        f.Post = (id, _) => Task.FromResult(new CommandResult { ExitCode = 3, StandardError = "boom" });

        var result = await f.Service.DeployAsync("site");

        Assert.False(result.Success);
        Assert.Equal(["npm-ci"], f.PostRuns);
        Assert.Contains("npm-ci: failed (exit 3)", result.Deployment.RecentDeployments.Single().PostDeploy);
        Assert.Equal("failed", result.Deployment.LastDeployStatus);
    }

    [Fact]
    public async Task PostDeployNeverRunsWhenTheGitUpdateFailed()
    {
        var f = Git();
        await Configure(f, "https://repository.test/app.git");
        await f.Service.SetPostDeployAsync("site", new SetGitPostDeployRequest { Commands = ["npm-ci"] });
        f.Respond = args => args.StartsWith("fetch ") ? new CommandResult { ExitCode = 1, StandardError = "nope" } : Ok("fixture-commit");
        var result = await f.Service.DeployAsync("site");
        Assert.False(result.Success);
        Assert.Empty(f.PostRuns);
    }

    [Fact]
    public async Task ATamperedOnDiskCommandIsRejectedAtRunTimeAndNeverExecuted()
    {
        var f = Git();
        await Configure(f, "https://repository.test/app.git");
        await f.Service.SetPostDeployAsync("site", new SetGitPostDeployRequest { Commands = ["npm-ci"] });
        var path = f.Store.ResolvePath(GitDeploymentService.ConfigPath("site"));
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"npm-ci\"", "\"calc.exe\""));

        var result = await f.Service.DeployAsync("site");

        Assert.False(result.Success);
        Assert.Empty(f.PostRuns);
        Assert.Contains("not an allowed command", result.Output);
    }

    private static readonly string ShaOld = new('a', 40);
    private static readonly string ShaNew = new('b', 40);
    private static readonly string ShaSaved = new('c', 40);

    private static void MarkRepository(GitFixture f)
    {
        var gitDir = f.Service.GitDirFor("site");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main");
    }

    [Fact]
    public async Task DeployRecordsHeadAndPreservesLocalChangesBeforeResetHard()
    {
        var f = Git();
        await Configure(f, "https://repository.test/app.git");
        MarkRepository(f);
        var head = ShaOld;
        f.Respond = args =>
        {
            if (args == "rev-parse HEAD") return Ok(head);
            if (args.StartsWith("status --porcelain")) return Ok(" M index.php\n M config.php\n");
            if (args.Contains("stash create")) return Ok(ShaSaved);
            if (args.StartsWith("reset --hard")) { head = ShaNew; return Ok(); }
            return Ok(args.StartsWith("rev-parse ") ? "short" : "");
        };

        var result = await f.Service.DeployAsync("site");

        Assert.True(result.Success);
        var entry = Assert.Single(result.Deployment.RecentDeployments);
        Assert.Equal(ShaOld, entry.PreviousCommit);
        Assert.Equal(ShaNew, entry.NewCommit);
        Assert.Equal(2, entry.LocalChangeCount);
        Assert.StartsWith("refs/adapanel/saved/", entry.SavedChangesRef);
        var stash = f.Commands.FindIndex(c => c.Contains("stash create"));
        var update = f.Commands.FindIndex(c => c.StartsWith("update-ref "));
        var reset = f.Commands.FindIndex(c => c.StartsWith("reset --hard"));
        Assert.True(stash >= 0 && stash < update && update < reset, "changes must be saved BEFORE reset --hard");
        Assert.Contains(ShaSaved, f.Commands[update]);
    }

    [Fact]
    public async Task AFailedSaveOfLocalChangesStopsTheDeploymentBeforeResetHard()
    {
        var f = Git();
        await Configure(f, "https://repository.test/app.git");
        MarkRepository(f);
        f.Respond = args =>
            args == "rev-parse HEAD" ? Ok(ShaOld)
            : args.StartsWith("status --porcelain") ? Ok(" M index.php\n")
            : args.Contains("stash create") ? new CommandResult { ExitCode = 1, StandardError = "no identity" }
            : Ok();
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.DeployAsync("site"));
        Assert.Equal("git.preserve_failed", error.MessageKey);
        Assert.DoesNotContain(f.Commands, c => c.StartsWith("reset --hard"));
    }

    [Fact]
    public async Task RollbackResetsToTheCommitRecordedBeforeThatDeployment()
    {
        var f = Git();
        await Configure(f, "https://repository.test/app.git");
        MarkRepository(f);
        var head = ShaOld;
        f.Respond = args =>
        {
            if (args == "rev-parse HEAD") return Ok(head);
            if (args.StartsWith("reset --hard")) { head = args.Contains(ShaOld) ? ShaOld : ShaNew; return Ok(); }
            return Ok(args.StartsWith("rev-parse ") ? "short" : "");
        };
        var deployed = await f.Service.DeployAsync("site");
        var id = deployed.Deployment.RecentDeployments.Single().Id;
        f.Commands.Clear();

        var rolled = await f.Service.RollbackAsync("site", id);

        Assert.True(rolled.Success);
        Assert.Contains($"reset --hard {ShaOld}", f.Commands);
        Assert.DoesNotContain(f.Commands, c => c.StartsWith("fetch "));
        Assert.Equal("rollback", rolled.Deployment.RecentDeployments.First().Trigger);
    }

    [Fact]
    public async Task RollbackRejectsUnknownIdsAndDeploymentsWithoutAPreviousCommit()
    {
        var f = Git();
        await Configure(f, "https://repository.test/app.git");
        var first = await f.Service.DeployAsync("site"); // ilk dağıtım: önceki commit yok
        var unknown = await Assert.ThrowsAsync<AppException>(() => f.Service.RollbackAsync("site", "nope"));
        Assert.Equal("git.deployment_not_found", unknown.MessageKey);
        var unavailable = await Assert.ThrowsAsync<AppException>(() => f.Service.RollbackAsync("site", first.Deployment.RecentDeployments.Single().Id));
        Assert.Equal("git.rollback_unavailable", unavailable.MessageKey);
    }

    [Fact]
    public async Task RemoveDeletesConfigAndPanelGitDirButKeepsSiteFilesAndTenantCopies()
    {
        var f = Git();
        await Configure(f, "https://repository.test/app.git");
        await f.Service.GenerateDeployKeyAsync("site");
        var gitDir = f.Service.GitDirFor("site");
        Directory.CreateDirectory(Path.Combine(gitDir, "objects", "ab"));
        var packed = Path.Combine(gitDir, "objects", "ab", "cdef");
        File.WriteAllText(packed, "x");
        File.SetAttributes(packed, FileAttributes.ReadOnly);
        var tenantOld = Path.Combine(Path.GetDirectoryName(gitDir)!, "site.tenant-old-20260101000000000");
        Directory.CreateDirectory(tenantOld);
        File.WriteAllText(Path.Combine(f.SitePath, "index.php"), "<?php");

        var result = await f.Service.RemoveAsync("site");

        Assert.Empty(result.Leftovers);
        Assert.Contains("configuration", result.Removed);
        Assert.Contains("panel git directory", result.Removed);
        Assert.False(File.Exists(f.Store.ResolvePath(GitDeploymentService.ConfigPath("site"))));
        Assert.False(Directory.Exists(gitDir));
        Assert.True(Directory.Exists(tenantOld));
        Assert.True(File.Exists(Path.Combine(f.SitePath, "index.php")));
        Assert.Contains(result.Kept, k => k.Contains("tenant-old"));
        var after = await f.Service.GetAsync("site");
        Assert.False(after.Configured);
        Assert.False(after.HasDeployKey);
    }

    [Fact]
    public void TreeDeletionRemovesAJunctionWithoutTouchingItsTarget()
    {
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "precious.txt"), "keep");
        var tree = Path.Combine(root, "tree");
        Directory.CreateDirectory(tree);
        var link = Path.Combine(tree, "link");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using (var process = Process.Start(psi)!) { process.WaitForExit(); Assert.Equal(0, process.ExitCode); }
        Assert.True(Directory.Exists(link));

        GitDeploymentService.DeleteTreeNoFollow(tree);

        Assert.False(Directory.Exists(tree));
        Assert.True(File.Exists(Path.Combine(target, "precious.txt")));
    }

    [Fact]
    public async Task GitRemoveEndpointDemandsTheSiteNameAndAuditsWithoutSecrets()
    {
        var audit = new RecordingAuditService();
        var service = Fake<IGitDeploymentService>((method, _) => method.Name switch
        {
            "RemoveAsync" => Task.FromResult(new GitRemovalResultDto { SiteName = "site" }),
            "SetHttpsTokenAsync" => Task.FromResult(new GitDeploymentDto { SiteName = "site", HasHttpsToken = true }),
            _ => throw new NotSupportedException(method.Name)
        });
        var controller = new GitController(service, null!, audit)
        { ControllerContext = new ControllerContext { HttpContext = Context("Administrator") } };

        var error = await Assert.ThrowsAsync<AppException>(() =>
            controller.RemoveAsync("site", new RemoveGitDeploymentRequest { ConfirmSiteName = "other" }, CancellationToken.None));
        Assert.Equal("git.confirmation_required", error.MessageKey);
        Assert.Empty(audit.Actions);

        await controller.RemoveAsync("site", new RemoveGitDeploymentRequest { ConfirmSiteName = "site" }, CancellationToken.None);
        await controller.SetHttpsTokenAsync("site", new SetGitHttpsTokenRequest { Username = "u", Token = "tok-never-in-audit" }, CancellationToken.None);
        Assert.Contains("git.deployment.removed", audit.Actions);
        Assert.Contains("git.https_token.set", audit.Actions);
        Assert.DoesNotContain(audit.Kayitlar, k => k.Details.Contains("tok-never-in-audit"));
    }

    [Fact]
    public void GitMutationEndpointsRemainAdministratorOnly()
    {
        var klass = typeof(GitController).GetCustomAttribute<AuthorizeAttribute>()!;
        Assert.Equal("Administrator", klass.Roles);
        foreach (var name in new[] { "RemoveAsync", "GenerateDeployKeyAsync", "RemoveDeployKeyAsync", "SetHttpsTokenAsync", "RemoveHttpsTokenAsync", "SetPostDeployAsync", "RollbackAsync" })
        {
            var method = typeof(GitController).GetMethod(name)!;
            Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
            var override_ = method.GetCustomAttribute<AuthorizeAttribute>();
            Assert.True(override_ is null || override_.Roles == "Administrator", name);
        }
    }

    // ================= PHP =================

    private sealed class PhpFixture : IDisposable
    {
        public required PhpService Service { get; init; }
        public required FakeIis Iis { get; init; }
        public required List<string> BootstrapperRemovals { get; init; }
        public required string Executable { get; init; }
        public required SqliteConnection Keeper { get; init; }
        public void Dispose() => Keeper.Dispose();
    }

    private async Task<PhpFixture> Php(IReadOnlyCollection<string>? leftovers = null, params (string Site, string Handler, string Processor)[] siteHandlers)
    {
        var keeper = new SqliteConnection($"Data Source=W3Php-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");
        await keeper.OpenAsync();
        var options = new DbContextOptionsBuilder<AdaPanelDbContext>().UseSqlite(keeper.ConnectionString).Options;
        var db = new AdaPanelDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var installDir = Path.Combine(root, "php-83");
        Directory.CreateDirectory(installDir);
        var executable = Path.Combine(installDir, "php-cgi.exe");
        File.WriteAllText(executable, "fixture");
        db.InstalledComponents.Add(new InstalledComponent { ComponentId = "php-83", DisplayName = "PHP 8.3", InstallDir = installDir, Status = ComponentInstallStatus.Installed });
        await db.SaveChangesAsync();

        var iis = new FakeIis();
        foreach (var (site, handler, processor) in siteHandlers)
        {
            iis.Sites.Add(site);
            var element = new Element();
            element.SetAttributeValue("name", handler);
            element.SetAttributeValue("scriptProcessor", processor);
            iis.Handlers.GetOrAdd(site).Add(element);
        }
        iis.FastCgi.Add(Registered(executable));

        var removals = new List<string>();
        var bootstrapper = Fake<IComponentBootstrapper>((method, args) =>
        {
            if (method.Name != "RemoveAsync") throw new NotSupportedException(method.Name);
            removals.Add((string)args[0]!);
            return Task.FromResult<IReadOnlyCollection<string>>(leftovers ?? []);
        });
        var siteService = Fake<IIisSiteService>((method, _) => method.Name == "GetSitesAsync"
            ? Task.FromResult<IReadOnlyCollection<SiteDto>>(iis.Sites.Select(name => new SiteDto { Name = name }).ToArray())
            : throw new NotSupportedException(method.Name));
        var service = new PhpService(db, bootstrapper, new ConcurrencyLock(), siteService, NullLogger<PhpService>.Instance)
        { SiteAssignmentManagerFactory = () => iis };
        return new PhpFixture { Service = service, Iis = iis, BootstrapperRemovals = removals, Executable = executable, Keeper = keeper };
    }

    private static Element Registered(string path)
    {
        var element = new Element();
        element.SetAttributeValue("fullPath", path);
        return element;
    }

    [Fact]
    public async Task PhpVersionInUseIsRejectedWithTheSiteNamesAndNothingIsRemoved()
    {
        using var f = await Php(null, ("a.test", "AdaPanel.Php.php-83", ""), ("b.test", "AdaPanel.Php.php-82", ""));
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.RemoveVersionAsync("php-83", null, null));
        Assert.Equal("php.version_in_use", error.MessageKey);
        Assert.Equal(409, error.HttpStatusCode);
        Assert.Contains("a.test", error.Details);
        Assert.DoesNotContain("b.test", error.Details);
        Assert.Empty(f.BootstrapperRemovals);
        Assert.Equal(0, f.Iis.Commits);
        Assert.Single(f.Iis.FastCgi.Items);
    }

    [Fact]
    public async Task APhpVersionIsAlsoInUseWhenAHandWrittenHandlerPointsAtItsExecutable()
    {
        using var f = await Php(null, ("c.test", "Custom.Handler", Path.Combine(root, "php-83", "php-cgi.exe")));
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.RemoveVersionAsync("php-83", null, null));
        Assert.Equal("php.version_in_use", error.MessageKey);
        Assert.Contains("c.test", error.Details);
    }

    [Fact]
    public async Task UnusedPhpVersionIsUnregisteredAndRemovedAndLeftoversAreReturned()
    {
        using var f = await Php(["files locked"], ("b.test", "AdaPanel.Php.php-82", ""));
        var leftovers = await f.Service.RemoveVersionAsync("php-83", null, null);
        Assert.Equal(["files locked"], leftovers);
        Assert.Equal(["php-83"], f.BootstrapperRemovals);
        Assert.Empty(f.Iis.FastCgi.Items);
        Assert.Equal(1, f.Iis.Commits);
    }

    [Fact]
    public async Task AnUnreadableSiteBlocksRemovalInsteadOfBeingTreatedAsUnused()
    {
        using var f = await Php(null, ("a.test", "AdaPanel.Php.php-82", ""));
        f.Iis.Unreadable.Add("a.test");
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.RemoveVersionAsync("php-83", null, null));
        Assert.Equal("php.usage_unverified", error.MessageKey);
        Assert.Contains("a.test", error.Details);
        Assert.Empty(f.BootstrapperRemovals);
    }

    [Fact]
    public async Task OnlyPhpComponentsCanBeRemovedThroughThePhpPath()
    {
        using var f = await Php();
        var error = await Assert.ThrowsAsync<AppException>(() => f.Service.RemoveVersionAsync("nodejs-lts", null, null));
        Assert.Equal("php.invalid_component", error.MessageKey);
    }

    [Fact]
    public async Task TheGenericSoftwareRemoveEndpointRoutesPhpThroughTheInUseGuard()
    {
        var phpCalls = new List<string>();
        var php = Fake<IPhpService>((method, args) =>
        {
            if (method.Name != "RemoveVersionAsync") throw new NotSupportedException(method.Name);
            phpCalls.Add((string)args[0]!);
            return Task.FromResult<IReadOnlyCollection<string>>([]);
        });
        var bootstrapper = Fake<IComponentBootstrapper>((method, _) => throw new InvalidOperationException("generic removal must not be used for php: " + method.Name));
        var controller = new SoftwareController(bootstrapper, null!, null!, php)
        { ControllerContext = new ControllerContext { HttpContext = Context("Administrator") } };
        await controller.RemoveAsync("php-83");
        Assert.Equal(["php-83"], phpCalls);
    }

    [Fact]
    public void PhpVersionRemovalEndpointIsAdministratorOnly()
    {
        var method = typeof(PhpController).GetMethod("RemoveVersionAsync")!;
        Assert.Equal("Administrator", method.GetCustomAttribute<AuthorizeAttribute>()!.Roles);
        Assert.NotNull(method.GetCustomAttribute<HttpDeleteAttribute>());
    }

    public sealed class FakeIis : IDisposable
    {
        public List<string> Sites { get; } = [];
        public HandlerMap Handlers { get; } = new();
        public Collection FastCgi { get; } = new();
        public HashSet<string> Unreadable { get; } = [];
        public int Commits { get; private set; }
        public Configuration GetApplicationHostConfiguration() => new(FastCgi);
        public Configuration GetWebConfiguration(string site) =>
            Unreadable.Contains(site) ? throw new IOException("fixture unreadable") : new(Handlers.GetOrAdd(site));
        public void CommitChanges() => Commits++;
        public void Dispose() { }
    }

    public sealed class HandlerMap
    {
        private readonly Dictionary<string, Collection> items = [];
        public Collection GetOrAdd(string site) => items.TryGetValue(site, out var found) ? found : items[site] = new Collection();
    }

    public sealed class Configuration(Collection items) { public Section GetSection(string _) => new(items); }
    public sealed class Section(Collection items) { public Collection Collection => items; }

    public sealed class Element
    {
        private readonly Dictionary<string, object> attributes = [];
        public object GetAttributeValue(string name) => attributes.GetValueOrDefault(name, "");
        public void SetAttributeValue(string name, object value) => attributes[name] = value;
    }

    public sealed class Collection : IEnumerable
    {
        private readonly List<object> items = [];
        public IReadOnlyList<object> Items => items;
        public object CreateElement(string _) => new Element();
        public void Add(object item) => items.Add(item);
        public void AddAt(int index, object item) => items.Insert(index, item);
        public void Remove(object item) => items.Remove(item);
        public IEnumerator GetEnumerator() => items.GetEnumerator();
    }

    // ================= ortak =================

    private sealed class Owner(bool owner) : IServerOwnerService
    {
        public Task<Guid?> GetServerOwnerIdAsync(CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);
        public Task<bool> IsServerOwnerAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(owner);
    }

    private static HttpContext Context(string role) => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, role)], "test"))
    };
}
