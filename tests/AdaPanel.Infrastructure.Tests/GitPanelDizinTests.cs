using System.Diagnostics;
using System.Net;
using System.Reflection;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Git;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Services;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// G6 tur 2: git dizini kiracı kökünden panele taşınır; kiracının .git/config'i (filter sürücüsü,
/// core.worktree ...) SYSTEM olarak çalışan git'e ulaşamaz. Testler GERÇEK git çalıştırır.
/// </summary>
public sealed class GitPanelDizinTests
{
    private static string Gecici() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ada-gitpd-" + Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>Panelin sertleştirmesi OLMAYAN düz git (kiracının kendi deposunu hazırlaması).</summary>
    private static string DuzGit(string dizin, params string[] argumanlar)
    {
        var bilgi = new ProcessStartInfo("git") { WorkingDirectory = dizin, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arguman in new[] { "-c", "user.name=t", "-c", "user.email=t@t" }.Concat(argumanlar)) bilgi.ArgumentList.Add(arguman);
        using var surec = Process.Start(bilgi)!;
        var cikti = surec.StandardOutput.ReadToEnd();
        surec.WaitForExit();
        Assert.True(surec.ExitCode == 0, surec.StandardError.ReadToEnd());
        return cikti;
    }

    private static (GitDeploymentService Servis, string Kok) Servis()
    {
        var kok = Gecici();
        var depo = new JsonFileStore(Microsoft.Extensions.Options.Options.Create(new PanelRuntimeOptions { DataRoot = Path.Combine(kok, "veri") }));
        var iis = DispatchProxy.Create<IIisSiteService, PostgresDatabaseIdentityConsumersTests.StrictProxy>();
        ((PostgresDatabaseIdentityConsumersTests.StrictProxy)(object)iis).Handler = (m, _) => throw new NotSupportedException(m.Name);
        return (new GitDeploymentService(iis, depo, new ConcurrencyLock(), NullLogger<GitDeploymentService>.Instance), kok);
    }

    /// <summary>Kiracı kökünde, verilen ek config bölümüyle bir depo ve işaret dosyası komutu hazırlar.</summary>
    private static string KiraciDeposu(string site, string ekConfig)
    {
        Directory.CreateDirectory(site);
        DuzGit(site, "init", "-q");
        File.WriteAllText(Path.Combine(site, "index.php"), "<?php echo 1;");
        DuzGit(site, "add", "index.php");
        DuzGit(site, "commit", "-q", "-m", "ilk");
        File.AppendAllText(Path.Combine(site, ".git", "config"), "\n" + ekConfig + "\n");
        return site;
    }

    /// <summary>
    /// Kiracının tehlikeli .git'i ARTIK ne kopyalanır ne kullanılır: web kökünden panel klasörüne TAŞINIR,
    /// panel git dizini oluşturulmaz (sıfırdan init yapılacak), hiçbir sürücü çalışmaz.
    /// </summary>
    [Theory]
    [InlineData("[filter \"evil\"]\n\tsmudge = cmd /c echo x > ISARET.txt\n\tprocess = cmd /c echo x > ISARET.txt")]
    [InlineData("[core]\n\tworktree = C:/Windows/Temp")]
    [InlineData("[core]\n\tfsmonitor = cmd /c echo x > ISARET.txt")]
    [InlineData("[http]\n\tcookieFile = C:/Windows/win.ini\n\tsaveCookies = true")]
    [InlineData("[include]\n\tpath = ../baska")]
    public async Task Kiracinin_tehlikeli_git_dizini_KOPYALANMAZ_web_kokunden_tasinir(string ek)
    {
        var (servis, kok) = Servis();
        var site = KiraciDeposu(Path.Combine(kok, "site"), ek);
        var gitDir = servis.GitDirFor("site");

        var uyari = await servis.EnsureRepositoryLocationAsync(site, gitDir, CancellationToken.None);

        Assert.Null(uyari);
        Assert.False(Directory.Exists(gitDir), "kiracı deposu panel git dizinine kopyalandı");
        Assert.False(Directory.Exists(Path.Combine(site, ".git")), ".git web kökünde kaldı");
        var eski = Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(gitDir)!, "site.tenant-old-*"));
        Assert.True(Directory.Exists(Path.Combine(eski, "objects")), "geçmiş silinmiş");
        Assert.False(File.Exists(Path.Combine(site, "ISARET.txt")));
        Assert.Empty(Directory.GetDirectories(site, ".git*"));
    }

    [Fact]
    public async Task Tasima_basarisizsa_URLdeki_jeton_silinir_ve_UYARI_doner()
    {
        var (servis, kok) = Servis();
        var site = KiraciDeposu(Path.Combine(kok, "site"),
            "[remote \"origin\"]\n\turl = https://kullanici:GIZLIJETON@example.test/a.git\n\tfetch = +refs/heads/*:refs/remotes/origin/*");
        var gitDir = servis.GitDirFor("site");

        // Açık tutulan bir dosya Directory.Move'u bozar (paylaşım ihlali).
        using var kilit = new FileStream(Path.Combine(site, ".git", "HEAD"), FileMode.Open, FileAccess.Read, FileShare.None);

        var uyari = await servis.EnsureRepositoryLocationAsync(site, gitDir, CancellationToken.None);

        Assert.NotNull(uyari);
        Assert.Contains("WARNING", uyari);
        var config = File.ReadAllText(Path.Combine(site, ".git", "config"));
        Assert.DoesNotContain("GIZLIJETON", config);
        Assert.Contains("https://example.test/a.git", config);
    }

    [Theory]
    [InlineData("commondir", "../../baska")]
    [InlineData("gitdir", "C:/baska")]
    [InlineData("objects/info/alternates", "C:/baska/objects")]
    [InlineData("objects/info/http-alternates", "http://evil/objects")]
    public async Task Panel_git_dizininde_commondir_ya_da_alternates_varsa_REDDEDILIR(string dosya, string icerik)
    {
        var (servis, kok) = Servis();
        var site = Path.Combine(kok, "site");
        Directory.CreateDirectory(site);
        var gitDir = servis.GitDirFor("site");
        Directory.CreateDirectory(Path.GetDirectoryName(gitDir)!);
        await GitDeploymentService.RunGitForTests("init", site, CancellationToken.None, gitDir, useWorkTree: false);
        var hedef = Path.Combine(gitDir, dosya.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(hedef)!);
        File.WriteAllText(hedef, icerik);

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.EnsureRepositoryLocationAsync(site, gitDir, CancellationToken.None));

        Assert.Equal("git.unsafe_repository", hata.MessageKey);
        Assert.False(File.Exists(Path.Combine(site, "ISARET.txt")));
    }

    [Fact]
    public async Task Gitfile_reddedilir_ve_dokunulmaz()
    {
        var (servis, kok) = Servis();
        var site = Path.Combine(kok, "site");
        Directory.CreateDirectory(site);
        File.WriteAllText(Path.Combine(site, ".git"), "gitdir: C:/baska");

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.EnsureRepositoryLocationAsync(site, servis.GitDirFor("site"), CancellationToken.None));

        Assert.Equal("git.unsafe_repository", hata.MessageKey);
        Assert.True(File.Exists(Path.Combine(site, ".git")));
    }

    [Fact]
    public async Task Reparse_point_olan_git_reddedilir()
    {
        var (servis, kok) = Servis();
        var site = Path.Combine(kok, "site");
        var hedef = Path.Combine(kok, "hedef");
        Directory.CreateDirectory(site);
        Directory.CreateDirectory(hedef);
        var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(site, ".git")}\" \"{hedef}\"") { RedirectStandardOutput = true, CreateNoWindow = true })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.EnsureRepositoryLocationAsync(site, servis.GitDirFor("site"), CancellationToken.None));

        Assert.Equal("git.unsafe_repository", hata.MessageKey);
        Assert.True(Directory.Exists(hedef));
    }

    /// <summary>
    /// Panelin kendi init + remote add akışı izin listesinden GEÇER (aksi halde ilk dağıtımdan sonraki
    /// her dağıtım reddedilirdi) ve init site köküne .git yazmaz.
    /// </summary>
    [Fact]
    public async Task Panelin_kendi_init_akisi_izin_listesinden_gecer_ve_kokte_git_olusturmaz()
    {
        var (servis, kok) = Servis();
        var site = Path.Combine(kok, "site");
        Directory.CreateDirectory(site);
        var gitDir = servis.GitDirFor("site");
        Directory.CreateDirectory(Path.GetDirectoryName(gitDir)!);

        var init = await GitDeploymentService.RunGitForTests("init", site, CancellationToken.None, gitDir, useWorkTree: false);
        Assert.True(init.Success, init.StandardError);
        var ekle = await GitDeploymentService.RunGitForTests("remote add origin \"https://example.test/a.git\"", site, CancellationToken.None, gitDir);
        Assert.True(ekle.Success, ekle.StandardError);

        Assert.Null(GitConfigAllowList.FindViolation(File.ReadAllText(Path.Combine(gitDir, "config"))));
        Assert.False(Directory.Exists(Path.Combine(site, ".git")));
    }

    /// <summary>Panel git dizinindeki config sonradan bozulursa (yedekten dönüş, elle düzenleme) dağıtım yine reddedilir.</summary>
    [Fact]
    public async Task Panel_git_dizinindeki_bozuk_config_de_reddedilir()
    {
        var (servis, kok) = Servis();
        var site = Path.Combine(kok, "site");
        Directory.CreateDirectory(site);
        var gitDir = servis.GitDirFor("site");
        Directory.CreateDirectory(Path.GetDirectoryName(gitDir)!);
        await GitDeploymentService.RunGitForTests("init", site, CancellationToken.None, gitDir, useWorkTree: false);
        File.AppendAllText(Path.Combine(gitDir, "config"), "\n[filter \"x\"]\n\tsmudge = calc\n");

        var hata = await Assert.ThrowsAsync<AppException>(() => servis.EnsureRepositoryLocationAsync(site, gitDir, CancellationToken.None));
        Assert.Equal("git.unsafe_config", hata.MessageKey);
    }

    [Theory]
    [InlineData("[core]\n\trepositoryformatversion = 0\n\tfilemode = false\n\tbare = false\n\tlogallrefupdates = true\n\tsymlinks = false\n\tignorecase = true\n[remote \"origin\"]\n\turl = https://a/b.git\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n[branch \"main\"]\n\tremote = origin\n\tmerge = refs/heads/main\n")]
    public void Izin_listesi_gercek_git_ciktisini_kabul_eder(string metin)
    {
        Assert.Null(GitConfigAllowList.FindViolation(metin));
    }

    [Theory]
    [InlineData("[core]\n\tfsmonitor = x")]
    [InlineData("[core] worktree = C:/x")]
    [InlineData("[remote \"origin\"]\n\tpushurl = x")]
    [InlineData("[remote \"origin\"]\n\turl = a\n\turl = b")]
    [InlineData("[remote \"baska\"]\n\turl = a")]
    [InlineData("[branch \"m\"]\n\tpushRemote = x")]
    [InlineData("[core]\n\tfilemode = false \\\n\tdevam")]
    public void Izin_listesi_disindakini_reddeder(string metin)
    {
        Assert.NotNull(GitConfigAllowList.FindViolation(metin));
    }
}
