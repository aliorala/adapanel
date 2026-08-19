using System.Security.AccessControl;
using System.Security.Principal;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using AdaPanel.Infrastructure.Waf;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

/// <summary>
/// WAF ekraninin dort bayragi HIC olculmuyordu; hepsi <c>config/waf.json</c>'dan geliyordu ve o
/// kayit yalnizca dugmeye basildigi anda yaziliyordu.
/// <para>
/// Sonuc, ekranin KENDI ICINDE celismesiydi: uygulama havuzu adi IIS'ten TAZE okunup gosteriliyor,
/// hemen yanindaki "izole" bayragi ise aylar onceki kayittan geliyordu. Havuz <c>DefaultAppPool</c>'a
/// alindiginda panel "izole" demeye devam ediyordu. <c>BasicRulesEnabled</c> ve
/// <c>RequestsPerMinute</c> ise sitenin <c>web.config</c>'inde yasiyor -- o dosyayi silen/ezen her
/// yol (FTP, dosya yoneticisi, git dagitimi, SiteBuilder yayini, WordPress kurulumu) bu ayarlari da
/// goturuyor ve panel bunu hic gormuyordu.
/// </para>
/// </summary>
public sealed class SiteSecurityMeasurementTests
{
    // --- izolasyon: IIS'ten okunan havuz adindan olculur ---

    [Theory]
    [InlineData("AdaPanel.ornek.com", true)]
    [InlineData("adapanel.ornek.com", true)]
    [InlineData("DefaultAppPool", false)]
    [InlineData("MusteriHavuzu", false)]
    public void IsolationIsMeasuredFromTheLiveApplicationPoolName(string havuz, bool beklenen)
    {
        Assert.Equal(beklenen, WafService.MeasureIsolation(havuz));
    }

    /// <summary>Havuz adi okunamadiysa cevap "izole degil" DEGIL, "bilmiyorum"dur.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WhenThePoolNameCannotBeRead_IsolationIsUnknown(string? havuz)
    {
        Assert.Null(WafService.MeasureIsolation(havuz));
    }

    // --- temel kurallar + hiz siniri: sitenin web.config'inden olculur ---

    [Fact]
    public void RulesWrittenByThePanel_AreMeasuredAsPresent()
    {
        using var alan = new Alan();
        alan.WebConfigYaz(TamWebConfig(maxRequests: 120));

        var olcum = WafService.MeasureWebConfig(alan.WebConfigYolu);

        Assert.True(olcum.BasicRules);
        Assert.Equal(120, olcum.RequestsPerMinute);
    }

    /// <summary>
    /// ASIL BULGU: web.config silindiginde (git dagitimi, SiteBuilder yayini, WordPress kurulumu)
    /// kurallar da hiz siniri da gitmistir. Dosyanin YOKLUGU olculebilir bir gercektir.
    /// </summary>
    [Fact]
    public void WhenTheWebConfigIsDeleted_TheRulesAreMeasuredAsGone()
    {
        using var alan = new Alan();

        var olcum = WafService.MeasureWebConfig(alan.WebConfigYolu);

        Assert.False(olcum.BasicRules);
        Assert.Equal(0, olcum.RequestsPerMinute);
    }

    /// <summary>
    /// Kurallarin YARISI silinmisse cevap "hayir"dir: eksik uygulanmis bir sertlestirmeye "acik"
    /// demek, olmayan bir korumaya guvenmektir.
    /// </summary>
    [Fact]
    public void PartiallyRemovedRules_AreNotReportedAsEnabled()
    {
        using var alan = new Alan();
        alan.WebConfigYaz(
            """
            <configuration>
              <system.webServer>
                <security>
                  <requestFiltering removeServerHeader="true" allowDoubleEscaping="false" />
                </security>
                <httpProtocol>
                  <customHeaders>
                    <add name="X-Frame-Options" value="SAMEORIGIN" />
                  </customHeaders>
                </httpProtocol>
              </system.webServer>
            </configuration>
            """);

        Assert.False(WafService.MeasureWebConfig(alan.WebConfigYolu).BasicRules);
    }

    /// <summary>
    /// Hiz siniri kurali KAPATILMISSA sayinin bir anlami yoktur: <c>maxRequests</c> dosyada dursa
    /// bile sinir uygulanmiyordur ve panel "120/dk" dememelidir.
    /// </summary>
    [Fact]
    public void ADisabledRateLimitRule_MeasuresAsNoLimit()
    {
        using var alan = new Alan();
        alan.WebConfigYaz(
            """
            <configuration>
              <system.webServer>
                <security>
                  <dynamicIpSecurity denyByRequestRate="false" maxRequests="120" requestIntervalInMilliseconds="60000" />
                </security>
              </system.webServer>
            </configuration>
            """);

        Assert.Equal(0, WafService.MeasureWebConfig(alan.WebConfigYolu).RequestsPerMinute);
    }

    /// <summary>
    /// ORTAK KURAL: OLCEMEDIYSEN "YOK" DEME. Bozuk/okunamayan XML, "kurallar kaldirilmis" DEGIL
    /// "olculemedi"dir. Ikisini ayni kefeye koymak, bir dosya kilidini guvenlik alarmina cevirirdi.
    /// </summary>
    [Fact]
    public void AnUnreadableWebConfig_IsUnmeasured_NotEmpty()
    {
        using var alan = new Alan();
        alan.WebConfigYaz("<configuration><system.webServer>bozuk");

        var olcum = WafService.MeasureWebConfig(alan.WebConfigYolu);

        Assert.Null(olcum.BasicRules);
        Assert.Null(olcum.RequestsPerMinute);
    }

    // --- klasor ACL'i: dosya sisteminden olculur ---

    /// <summary>
    /// <c>ApplyFolderAclAsync</c>'in yaptigi sey: mirasi KIR + havuz kimligine acik izin ver.
    /// Olcum de tam bunu arar (gercek bir klasor uzerinde).
    /// </summary>
    [Fact]
    public void AnAppliedFolderAcl_IsMeasuredAsApplied()
    {
        using var alan = new Alan();
        var kimlik = WindowsIdentity.GetCurrent().Name;
        alan.AclUygula(kimlik);

        Assert.True(WafService.MeasureFolderAcl(alan.Kok, kimlik));
    }

    /// <summary>
    /// Miras GERI ACILMISSA izolasyon geri alinmistir (diger sitelerin havuz kimlikleri tekrar
    /// okuyabilir). Yalniz "izin var mi" diye bakmak bu geri alinisi gizlerdi.
    /// </summary>
    [Fact]
    public void WhenInheritanceIsRestored_TheAclIsMeasuredAsNotApplied()
    {
        using var alan = new Alan();
        var kimlik = WindowsIdentity.GetCurrent().Name;
        alan.AclUygula(kimlik);
        alan.MirasiGeriAc();

        Assert.False(WafService.MeasureFolderAcl(alan.Kok, kimlik));
    }

    /// <summary>
    /// IZOLASYON SONRASI DOGRU DAVRANIS: izin ESKI havuz kimligine yazilidir; yeni kimlik
    /// sorulunca cevap "uygulanmamis"tir. Panelin izolasyondan sonra izni yeniden uygulamasinin
    /// (ManagedResourceProtectionTests) dayanagi budur.
    /// </summary>
    [Fact]
    public void TheAclIsMeasuredForTheAskedIdentity_NotAnyIdentity()
    {
        using var alan = new Alan();
        alan.AclUygula(WindowsIdentity.GetCurrent().Name);

        Assert.False(WafService.MeasureFolderAcl(alan.Kok, "IIS AppPool\\AdaPanel.baska-site"));
    }

    [Fact]
    public void AMissingFolder_HasNoAppliedAcl()
    {
        using var alan = new Alan();

        Assert.False(WafService.MeasureFolderAcl(Path.Combine(alan.Kok, "olmayan"), WindowsIdentity.GetCurrent().Name));
    }

    /// <summary>Kimlik ya da yol bilinmiyorsa olcum yapilamaz — "uygulanmamis" DEMEYIZ.</summary>
    [Fact]
    public void WithoutAFolderOrPrincipal_TheAclIsUnknown()
    {
        Assert.Null(WafService.MeasureFolderAcl(null, "IIS AppPool\\AdaPanel.site"));
        Assert.Null(WafService.MeasureFolderAcl("C:\\bir\\yer", "   "));
    }

    // --- BAGLANTI: servis GERCEKTEN olcuyor mu (yoksa hâlâ kayda mi bakiyor) ---

    /// <summary>
    /// ASIL BULGU, ucundan ucuna: <c>waf.json</c> "izole" diyor, IIS ise havuzun
    /// <c>DefaultAppPool</c> oldugunu soyluyor. Panel GERCEGE uymali.
    /// </summary>
    /// <remarks>
    /// Olcum fonksiyonlarini tek tek dogrulamak yetmez: eski kodun hatasi fonksiyonlarin YANLIS
    /// olmasi degil, HIC CAGRILMAMASIYDI. Bu test tam da o baglantiyi korur.
    /// </remarks>
    [Fact]
    public async Task WhenTheRecordSaysIsolatedButTheLivePoolIsNot_TheAnswerComesFromIis()
    {
        using var alan = new Alan();
        alan.WafKaydiYaz(izole: true, temelKurallar: true, hizSiniri: 120);

        var yapilandirma = await alan.WafServisi("DefaultAppPool").GetSiteConfigurationAsync("site");

        Assert.False(yapilandirma.IsIsolated);
        Assert.True(yapilandirma.IsIsolatedMeasured);

        // Ayni ekranin iki alani artik birbiriyle celismiyor.
        Assert.Equal("DefaultAppPool", yapilandirma.ApplicationPoolName);
    }

    /// <summary>
    /// <c>web.config</c> silindiginde (git dagitimi/SiteBuilder/WordPress) kayit "acik" dese de
    /// panel artik "acik" demez.
    /// </summary>
    [Fact]
    public async Task WhenTheWebConfigIsGone_TheRecordedRulesDoNotOverrideReality()
    {
        using var alan = new Alan();
        alan.WafKaydiYaz(izole: true, temelKurallar: true, hizSiniri: 120);

        var yapilandirma = await alan.WafServisi("AdaPanel.site").GetSiteConfigurationAsync("site");

        Assert.False(yapilandirma.BasicRulesEnabled);
        Assert.True(yapilandirma.BasicRulesMeasured);
        Assert.Equal(0, yapilandirma.RequestsPerMinute);
        Assert.True(yapilandirma.RateLimitMeasured);

        // Havuz gercekten AdaPanel'in: izolasyon dogru olcumle "var" cikiyor.
        Assert.True(yapilandirma.IsIsolated);
    }

    /// <summary>Dosya yerindeyken de cevap dosyadan gelir (yanlis negatif uretmiyoruz).</summary>
    [Fact]
    public async Task WhenTheWebConfigIsInPlace_TheMeasuredRulesAreReported()
    {
        using var alan = new Alan();
        alan.WafKaydiYaz(izole: false, temelKurallar: false, hizSiniri: 0);
        alan.WebConfigYaz(TamWebConfig(maxRequests: 90));

        var yapilandirma = await alan.WafServisi("AdaPanel.site").GetSiteConfigurationAsync("site");

        Assert.True(yapilandirma.BasicRulesEnabled);
        Assert.Equal(90, yapilandirma.RequestsPerMinute);
    }

    private static string TamWebConfig(int maxRequests) =>
        $"""
        <configuration>
          <system.webServer>
            <security>
              <requestFiltering removeServerHeader="true" allowDoubleEscaping="false" />
              <dynamicIpSecurity denyByRequestRate="true" maxRequests="{maxRequests}" requestIntervalInMilliseconds="60000" />
            </security>
            <httpProtocol>
              <customHeaders>
                <add name="X-Frame-Options" value="SAMEORIGIN" />
                <add name="X-Content-Type-Options" value="nosniff" />
                <add name="Referrer-Policy" value="same-origin" />
              </customHeaders>
            </httpProtocol>
          </system.webServer>
        </configuration>
        """;

    private sealed class Alan : IDisposable
    {
        private readonly string _ust;

        public Alan()
        {
            _ust = Path.Combine(Path.GetTempPath(), $"adapanel-waf-{Guid.NewGuid():N}");
            Kok = Path.Combine(_ust, "site");
            VeriKoku = Path.Combine(_ust, "veri");
            Directory.CreateDirectory(Kok);
            Directory.CreateDirectory(VeriKoku);
        }

        public string Kok { get; }

        public string VeriKoku { get; }

        public string WebConfigYolu => Path.Combine(Kok, "web.config");

        public void WebConfigYaz(string icerik) => File.WriteAllText(WebConfigYolu, icerik);

        /// <summary>Panelin eski "tek gercek kaynagi": config/waf.json.</summary>
        public void WafKaydiYaz(bool izole, bool temelKurallar, int hizSiniri)
        {
            var klasor = Path.Combine(VeriKoku, "config");
            Directory.CreateDirectory(klasor);
            File.WriteAllText(
                Path.Combine(klasor, "waf.json"),
                $$"""
                [
                  {
                    "siteName": "site",
                    "isIsolated": {{(izole ? "true" : "false")}},
                    "folderAclApplied": true,
                    "basicRulesEnabled": {{(temelKurallar ? "true" : "false")}},
                    "requestsPerMinute": {{hizSiniri}},
                    "principal": "IIS AppPool\\AdaPanel.site"
                  }
                ]
                """);
        }

        public WafService WafServisi(string havuz) =>
            new(new SahteSiteServisi(Kok, havuz),
                new SahteKilit(),
                new JsonFileStore(Microsoft.Extensions.Options.Options.Create(
                    new PanelRuntimeOptions { DataRoot = VeriKoku })));

        /// <summary>ApplyFolderAclAsync'in `icacls /inheritance:r /grant:r` etkisinin .NET karsiligi.</summary>
        public void AclUygula(string kimlik)
        {
            var info = new DirectoryInfo(Kok);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                kimlik,
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            info.SetAccessControl(security);
        }

        public void MirasiGeriAc()
        {
            var info = new DirectoryInfo(Kok);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            info.SetAccessControl(security);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_ust, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class SahteKilit : IConcurrencyLock
    {
        public Task RunExclusiveAsync(Func<Task> operation, CancellationToken cancellationToken = default) => operation();

        public Task<T> RunExclusiveAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default) => operation();

        public Task RunExclusiveAsync(string key, Func<Task> operation, CancellationToken cancellationToken = default) => operation();

        public Task<T> RunExclusiveAsync<T>(string key, Func<Task<T>> operation, CancellationToken cancellationToken = default) => operation();
    }

    private sealed class SahteSiteServisi(string kok, string havuz) : IIisSiteService
    {
        public Task<IReadOnlyCollection<SiteDto>> GetSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> GetSiteAsync(string siteName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteDto
            {
                Name = siteName,
                PhysicalPath = kok,
                ApplicationRootPath = kok,
                ApplicationPoolName = havuz
            });

        public Task<SiteDto> CreateSiteAsync(CreateSiteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> UpdatePhysicalPathAsync(string siteName, string physicalPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> StartSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> StopSiteAsync(string siteName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> AddBindingAsync(string siteName, SiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SiteDto> RemoveBindingAsync(string siteName, RemoveSiteBindingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<SiteDto>> ImportExistingSitesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
