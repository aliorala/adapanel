using System.Security.Cryptography;
using System.Text;
using AdaPanel.Core.Dtos.Licensing;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Licensing;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>G32: state.json yerel butunluk muhru + (yapilandirilirsa) yanit imzasi dogrulamasi.</summary>
public sealed class LicenseIntegrityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "adapanel-license-int-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { }
    }

    private LicenseStateStore NewStore()
    {
        var fileStore = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = root }));
        return new LicenseStateStore(fileStore, new Reversible());
    }

    private string StatePath => Path.Combine(root, "config", "license", "state.json");

    [Fact]
    public async Task UntouchedStateKeepsItsVerdict()
    {
        var store = NewStore();
        await store.UpdateAsync(r => { r.ProtectedKey = store.ProtectKey("K-1"); r.LastVerdict = "valid"; r.MaxSubscriptions = 5; return r; }, default);

        var read = await NewStore().ReadAsync(default);

        Assert.Equal("valid", read.LastVerdict);
        Assert.Equal(5, read.MaxSubscriptions);
    }

    [Fact]
    public async Task HandEditedVerdictIsDiscardedAndKeyIsKept()
    {
        var store = NewStore();
        await store.UpdateAsync(r => { r.ProtectedKey = store.ProtectKey("K-1"); r.LastVerdict = "invalid"; r.MaxSubscriptions = 2; return r; }, default);
        File.WriteAllText(StatePath, File.ReadAllText(StatePath).Replace("\"invalid\"", "\"valid\"").Replace("\"maxSubscriptions\": 2", "\"maxSubscriptions\": 0"));

        var read = await NewStore().ReadAsync(default);

        Assert.Equal(string.Empty, read.LastVerdict);
        Assert.Null(read.LastVerifiedAtUtc);
        Assert.Equal("state_integrity_failed", read.LastError);
        Assert.NotEmpty(read.ProtectedKey);
        // MaxSubscriptions=0 "sinirsiz" demek; oynanmis kayit sinirsiz BIRAKILMAZ.
        Assert.Equal(1, read.MaxSubscriptions);
        Assert.Null(read.LastAttemptAtUtc);
    }

    [Fact]
    public async Task RemovingTheSealFromASealedFileIsTampering()
    {
        var store = NewStore();
        await store.UpdateAsync(r => { r.ProtectedKey = store.ProtectKey("K-1"); r.LastVerdict = "invalid"; return r; }, default);
        var json = File.ReadAllText(StatePath);
        File.WriteAllText(StatePath, System.Text.RegularExpressions.Regex.Replace(json, @"""seal"":\s*""[^""]*""", "\"seal\": \"\"").Replace("\"invalid\"", "\"valid\""));

        var read = await NewStore().ReadAsync(default);

        Assert.Equal(string.Empty, read.LastVerdict);
    }

    private string KeyPath => Path.Combine(root, "config", "license", "seal.key");

    private async Task<LicenseStateStore> LegacyFileAsync()
    {
        var store = NewStore();
        await store.UpdateAsync(r => { r.ProtectedKey = store.ProtectKey("K-1"); r.LastVerdict = "valid"; r.MaxSubscriptions = 5; return r; }, default);
        File.Delete(KeyPath);
        File.WriteAllText(StatePath, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(StatePath), @"""seal"":\s*""[^""]*""", "\"seal\": \"\""));
        LicenseStateStore.ResetProcessStateForTests();
        return NewStore();
    }

    [Fact]
    public async Task LegacyUnsealedFileIsSealedAtFirstReadAndKeepsItsVerdictOnEveryLaterRead()
    {
        var store = await LegacyFileAsync();

        var first = await store.ReadAsync(default);
        Assert.Equal("valid", first.LastVerdict);
        Assert.DoesNotContain("\"seal\": \"\"", File.ReadAllText(StatePath));

        var second = await store.ReadAsync(default);
        Assert.Equal("valid", second.LastVerdict);
        Assert.Equal(5, second.MaxSubscriptions);

        LicenseStateStore.ResetProcessStateForTests(); // surec yeniden baslatma
        var afterRestart = await NewStore().ReadAsync(default);
        Assert.Equal("valid", afterRestart.LastVerdict);
        Assert.Equal(5, afterRestart.MaxSubscriptions);
    }

    [Fact]
    public async Task ConcurrentFirstAccessCreatesExactlyOneKey()
    {
        // Birden cok tur: yaris nadir tetiklenir; her turda taze veri koku + eszamanli baslangic.
        for (var tur = 0; tur < 25; tur++)
        {
            var turKoku = Path.Combine(root, "tur" + tur);
            var barrier = new Barrier(16);
            var anahtarlar = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Factory.StartNew(() =>
            {
                var fileStore = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = turKoku }));
                var store = new LicenseStateStore(fileStore, new Reversible());
                barrier.SignalAndWait();
                return store.GetSealKey();
            }, TaskCreationOptions.LongRunning)));

            Assert.All(anahtarlar, k => Assert.NotNull(k));
            Assert.Single(anahtarlar.Select(k => Convert.ToBase64String(k!)).Distinct());
            Assert.Empty(Directory.GetFiles(Path.Combine(turKoku, "config", "license"), "*.tmp"));
        }
    }

    [Fact]
    public async Task TransientKeyReadFailureNeitherRegeneratesTheKeyNorResetsTheVerdict()
    {
        var store = NewStore();
        await store.UpdateAsync(r => { r.ProtectedKey = store.ProtectKey("K-1"); r.LastVerdict = "valid"; r.MaxSubscriptions = 5; return r; }, default);
        var before = File.ReadAllText(KeyPath);

        using (new FileStream(KeyPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var read = await NewStore().ReadAsync(default);
            Assert.Equal("valid", read.LastVerdict);
            Assert.Equal(5, read.MaxSubscriptions);
            Assert.Null(NewStore().GetSealKey());
        }

        Assert.Equal(before, File.ReadAllText(KeyPath));
        Assert.Equal("valid", (await NewStore().ReadAsync(default)).LastVerdict);
    }

    // --- yanit imzasi ---

    private static (string PublicKey, ECDsa Key) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), key);
    }

    private static LicenseVerificationRequest Req => new() { Product = "adapanel", Fingerprint = new string('f', 64), LicenseKey = "k" };

    private static LicenseVerificationResponse Signed(ECDsa key, string status = "valid", DateTime? issued = null, bool tamper = false)
    {
        var issuedAt = issued ?? DateTime.UtcNow;
        var unsigned = new LicenseVerificationResponse { Status = status, Sku = "pro", MaxSubscriptions = 10, IssuedAtUtc = issuedAt };
        var sig = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(LicenseResponseValidator.CanonicalText(Req, unsigned)), HashAlgorithmName.SHA256));
        return new LicenseVerificationResponse
        {
            Status = tamper ? "invalid" : status, Sku = "pro", MaxSubscriptions = 10, IssuedAtUtc = issuedAt, Signature = sig
        };
    }

    [Fact]
    public void WithoutConfiguredKeyUnsignedResponsesStillWork()
        => Assert.Null(LicenseResponseValidator.Validate(new LicenseVerificationResponse { Status = "valid" }, Req, "", DateTime.UtcNow, TimeSpan.FromHours(72)));

    [Fact]
    public void ValidSignatureIsAccepted()
    {
        var (pub, key) = NewKey();
        Assert.Null(LicenseResponseValidator.Validate(Signed(key), Req, pub, DateTime.UtcNow, TimeSpan.FromHours(72)));
    }

    [Fact]
    public void TamperedUnsignedAndStaleResponsesAreRejectedWhenKeyConfigured()
    {
        var (pub, key) = NewKey();
        var now = DateTime.UtcNow;
        Assert.Equal("signature_invalid", LicenseResponseValidator.Validate(Signed(key, tamper: true), Req, pub, now, TimeSpan.FromHours(72)));
        Assert.Equal("signature_missing", LicenseResponseValidator.Validate(new LicenseVerificationResponse { Status = "valid" }, Req, pub, now, TimeSpan.FromHours(72)));
        Assert.Equal("response_stale", LicenseResponseValidator.Validate(Signed(key, issued: now.AddDays(-30)), Req, pub, now, TimeSpan.FromHours(72)));
        var (_, other) = NewKey();
        Assert.Equal("signature_invalid", LicenseResponseValidator.Validate(Signed(other), Req, pub, now, TimeSpan.FromHours(72)));
    }

    private sealed class Reversible : ISecretProtector
    {
        public string Protect(string plaintext) => Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string protectedValue) => Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue));
    }
}
