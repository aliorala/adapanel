using System.Security.Cryptography;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Components;
using AdaPanel.Infrastructure.AppStore;
using AdaPanel.Infrastructure.Bootstrap;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>G28/G29: paket butunlugu olmadan kurulum yapilmaz (kapali basarisiz).</summary>
public sealed class AppStoreIntegrityTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), "adapanel-integrity-" + Guid.NewGuid().ToString("N"));

    public AppStoreIntegrityTests() => File.WriteAllBytes(file, [1, 2, 3, 4, 5]);

    public void Dispose() => File.Delete(file);

    [Fact]
    public async Task MatchingSha256Passes()
        => await AppStoreService.VerifyPackageHashAsync(
            file, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), AppStoreService.PackageHashKind.Sha256, default);

    [Fact]
    public async Task WrongSha256IsMismatch()
    {
        var error = await Assert.ThrowsAsync<AppException>(() => AppStoreService.VerifyPackageHashAsync(
            file, new string('a', 64), AppStoreService.PackageHashKind.Sha256, default));
        Assert.Equal("store.checksum_mismatch", error.MessageKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("zz")]
    public async Task MissingOrMalformedExpectedHashIsRefusedNotSkipped(string expected)
    {
        var error = await Assert.ThrowsAsync<AppException>(() => AppStoreService.VerifyPackageHashAsync(
            file, expected, AppStoreService.PackageHashKind.Sha256, default));
        Assert.Equal("store.integrity_unavailable", error.MessageKey);
    }

    [Fact]
    public async Task Sha1HashIsNotAcceptedWhereSha256IsRequired()
    {
        var sha1 = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(file)));
        var error = await Assert.ThrowsAsync<AppException>(() => AppStoreService.VerifyPackageHashAsync(
            file, sha1, AppStoreService.PackageHashKind.Sha256, default));
        Assert.Equal("store.integrity_unavailable", error.MessageKey);
    }

    [Theory]
    [InlineData("grav")]
    [InlineData("drupal")]
    [InlineData("joomla")]
    [InlineData("prestashop")]
    public void EveryDownloadableCatalogTemplateHasAPinnedSha256(string id)
    {
        Assert.True(AppStoreService.PinnedPackageSha256.TryGetValue(id, out var sha));
        Assert.Equal(64, sha.Length);
        Assert.All(sha, c => Assert.True(Uri.IsHexDigit(c)));
    }

    [Theory]
    [InlineData("zip", "")]
    [InlineData("phar", "")]
    public void TrustedHostDoesNotWaiveChecksumForArchivesBeforeDownload(string type, string sha)
    {
        var error = Assert.Throws<AppException>(() => ComponentBootstrapper.EnsureIntegrityMetadataForTests(new ComponentManifestItemDto
        {
            Id = "nginx", Type = type, Sha256 = sha, DownloadUrl = "https://nginx.org/download/nginx-9.9.9.zip"
        }));
        Assert.Equal("software.checksum_required", error.MessageKey);
    }

    [Fact]
    public void ZipWithPinnedShaAndSignedInstallerAreStillAccepted()
    {
        ComponentBootstrapper.EnsureIntegrityMetadataForTests(new ComponentManifestItemDto
        {
            Id = "nginx", Type = "zip", Sha256 = new string('a', 64), DownloadUrl = "https://nginx.org/download/nginx-1.zip"
        });
        ComponentBootstrapper.EnsureIntegrityMetadataForTests(new ComponentManifestItemDto
        {
            Id = "python-3", Type = "installer", Sha256 = "", DownloadUrl = "https://www.python.org/ftp/python/3.12.4/python-3.12.4-amd64.exe"
        });
    }
}
