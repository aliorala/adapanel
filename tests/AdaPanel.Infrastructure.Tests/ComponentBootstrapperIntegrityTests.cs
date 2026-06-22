using AdaPanel.Infrastructure.Bootstrap;
using AdaPanel.Core.Dtos.Components;
using System.Text.Json;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class ComponentBootstrapperIntegrityTests
{
    [Fact]
    public void ParseSha256_ReadsPlainAndPrefixedDigests()
    {
        const string digest = "345b9c6a98da5c30dcbd4b0d99fc8710bf0ae98a3898eea18f7b2ad9dec93f06";

        Assert.Equal(digest, ComponentBootstrapper.ParseSha256(digest));
        Assert.Equal(digest, ComponentBootstrapper.ParseSha256($"sha256:{digest}"));
    }

    [Fact]
    public void ParseSha256ForFile_SelectsTheRequestedArtifact()
    {
        const string expected = "43749d78a28ff11a36cb279407bc13e79bcfb8670e7926e469018d31c2ec6453";
        const string content = """
            aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  node-v24.16.0-x86.msi
            43749d78a28ff11a36cb279407bc13e79bcfb8670e7926e469018d31c2ec6453  node-v24.16.0-x64.msi
            """;

        Assert.Equal(
            expected,
            ComponentBootstrapper.ParseSha256ForFile(content, "node-v24.16.0-x64.msi"));
    }

    [Fact]
    public void DatabasePorts_DoNotConflict()
    {
        Assert.Equal(3306, ComponentBootstrapper.ResolveDatabasePort("mariadb-11"));
        Assert.Equal(3307, ComponentBootstrapper.ResolveDatabasePort("mysql-8"));
    }

    [Fact]
    public void DatabasePackages_ArePortableVerifiedAndDeclareRuntimeDependency()
    {
        var manifestPath = FindRepositoryFile("components.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var components = document.RootElement.GetProperty("components")
            .EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("id").GetString()!,
                item => item,
                StringComparer.OrdinalIgnoreCase);

        AssertDatabasePackage(components["mariadb-11"], "initMariaDbZip");
        AssertDatabasePackage(components["mysql-8"], "initMySqlZip");

        var runtime = components["vc-redist-x64"];
        Assert.Equal("installer", runtime.GetProperty("type").GetString());
        Assert.Equal(
            "https://aka.ms/vs/17/release/vc_redist.x64.exe",
            runtime.GetProperty("downloadUrl").GetString());
    }

    [Fact]
    public void ComponentManifest_IsValidAndDeclaresRequiredHostingDependencies()
    {
        var manifestPath = FindRepositoryFile("components.json");
        var document = JsonSerializer.Deserialize<ComponentManifestDocument>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true
            });
        var components = document?.Components ?? [];

        ComponentBootstrapper.ValidateManifest(components);

        var byId = components.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        AssertDependency(byId, "composer", "php-8.3");
        AssertDependency(byId, "phpmyadmin", "php-8.3");
        AssertDependency(byId, "adminer", "php-8.3");
        AssertDependency(byId, "nodejs-lts", "http-platform-handler");
        AssertDependency(byId, "python-3", "http-platform-handler");
        AssertDependency(byId, "iis-arr", "url-rewrite");
        AssertDependency(byId, "stalwart-mail", "stalwart-cli");
        AssertDependency(byId, "pgadmin4", "postgresql-16");
    }

    [Theory]
    [InlineData("STATE              : 4  RUNNING")]
    [InlineData("  STATE : 4 RUNNING")]
    [InlineData("DURUM : 4 CALISIYOR")]
    public void ServiceState_RecognizesRunningByStableNumericCode(string output)
    {
        Assert.True(ComponentBootstrapper.IsServiceRunning(output));
    }

    [Fact]
    public void ServiceState_RejectsStoppedServices()
    {
        Assert.False(ComponentBootstrapper.IsServiceRunning("STATE : 1  STOPPED"));
    }

    private static void AssertDatabasePackage(JsonElement component, string postInstallStep)
    {
        Assert.Equal("zip", component.GetProperty("type").GetString());
        Assert.Matches(
            "^[0-9a-f]{64}$",
            component.GetProperty("sha256").GetString() ?? string.Empty);
        Assert.Contains(
            postInstallStep,
            component.GetProperty("postInstall").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(
            "vc-redist-x64",
            component.GetProperty("dependencies").EnumerateArray().Select(item => item.GetString()));
    }

    private static void AssertDependency(
        IReadOnlyDictionary<string, ComponentManifestItemDto> components,
        string componentId,
        string dependencyId)
    {
        Assert.Contains(
            dependencyId,
            components[componentId].Dependencies,
            StringComparer.OrdinalIgnoreCase);
    }

    private static string FindRepositoryFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(fileName);
    }

    private sealed class ComponentManifestDocument
    {
        public ComponentManifestItemDto[] Components { get; init; } = [];
    }
}
