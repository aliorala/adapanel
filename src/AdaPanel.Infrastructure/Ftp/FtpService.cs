using System.Text.Json;
using System.Text.RegularExpressions;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Ftp;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Options;
using AdaPanel.Infrastructure.Support;
using Microsoft.Extensions.Options;

namespace AdaPanel.Infrastructure.Ftp;

public sealed partial class FtpService(
    IIisSiteService iisSiteService,
    JsonFileStore fileStore,
    IPanelPreferencesService panelPreferencesService,
    IOptions<PanelRuntimeOptions> runtimeOptions) : IFtpService
{
    private const string StatePath = "config/ftp-users.json";
    private const string TlsStatePath = "config/ftp-tls.json";
    private const string InfraStatePath = "config/ftp-infra.json";
    private const string FtpSiteName = "AdaPanel FTP";
    private const int FtpPort = 21;
    private const int PassivePortStart = 50000;
    private const int PassivePortEnd = 51000;
    private readonly string dataRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(runtimeOptions.Value.DataRoot));

    public async Task<FtpServerStatusDto> EnsureServerAsync(CancellationToken cancellationToken = default)
    {
        await EnsureFtpInfrastructureAsync(false, cancellationToken);

        // KURULUMDAN SONRA DURUM ÖLÇÜLÜR, VARSAYILMAZ. Buraya eskiden `IsConfigured = true,
        // StatusMessage = "ftp.server_ready"` sabitleri yazılıyordu; gerekçe FtpServerStatusDto'da.
        return await GetServerStatusAsync(cancellationToken);
    }

    public async Task<FtpServerStatusDto> GetServerStatusAsync(CancellationToken cancellationToken = default)
    {
        var preferences = await panelPreferencesService.GetAsync(cancellationToken);
        var durum = new FtpServerStatusDto
        {
            ServerAddress = preferences.ServerAddress,
            ControlPort = FtpPort,
            PassivePortStart = PassivePortStart,
            PassivePortEnd = PassivePortEnd
        };

        var olcum = await ProbeFtpRuntimeAsync(cancellationToken);
        if (olcum is null)
        {
            // ÖLÇEMEDİK SESSİZCE "SORUN YOK"A DA "ARIZA"YA DA ÇEVRİLMEZ (FirewallService.IsPortCovered
            // deseni). Eski davranışa — altyapı kurulum bayrağına — düşülür ve durum "bilinmiyor" denir.
            durum.IsConfigured = (await fileStore.ReadAsync(InfraStatePath, new FtpInfraState(), cancellationToken)).Configured;
            durum.StatusMessage = "ftp.server_status_unknown";
            return durum;
        }

        durum.ServiceState = olcum.ServiceState;
        durum.SiteState = olcum.SiteState;
        durum.IsSiteRunning = olcum.SiteExists && olcum.SiteState.Equals("Started", StringComparison.OrdinalIgnoreCase);

        var (portDurumu, portSahibi) = ClassifyControlPort(olcum.PortOlculebildi, olcum.Listeners, olcum.ServicePids);
        durum.HasControlPortConflict = portDurumu == FtpControlPortDurumu.YabanciSurec;
        durum.ControlPortOwner = portSahibi;

        // SIRA ÖNEMLİ: en temel eksiklik önce söylenir. "Port çakışması" demek, site hiç yokken
        // yanıltıcı olurdu.
        durum.StatusMessage =
            !olcum.SiteExists ? "ftp.site_not_configured"
            : !olcum.ServiceState.Equals("Running", StringComparison.OrdinalIgnoreCase) ? "ftp.service_not_running"
            : !durum.IsSiteRunning ? "ftp.site_not_running"
            : portDurumu == FtpControlPortDurumu.YabanciSurec ? "ftp.control_port_conflict"
            : portDurumu == FtpControlPortDurumu.DinleyenYok ? "ftp.server_not_listening"
            : "ftp.server_ready";

        durum.IsConfigured = durum.StatusMessage == "ftp.server_ready";
        return durum;
    }

    /// <summary>Denetim portunun kimin elinde olduğuna dair HÜKÜM.</summary>
    internal enum FtpControlPortDurumu
    {
        /// <summary>Ölçüm yapılamadı (cmdlet yok / probe düştü). "Sorun yok" demek DEĞİLDİR.</summary>
        Olculemedi,

        /// <summary>Portu kimse dinlemiyor: hiçbir istemci bağlanamaz.</summary>
        DinleyenYok,

        /// <summary>Portu yalnız Microsoft FTP Service (FTPSVC) tutuyor — beklenen durum.</summary>
        PanelinServisi,

        /// <summary>Portu panelin FTP servisi DIŞINDA bir süreç de tutuyor.</summary>
        YabanciSurec
    }

    /// <summary>Denetim portunda dinleyen tek bir süreç (probe satırı).</summary>
    internal sealed record FtpPortDinleyici(int Pid, string Process, IReadOnlyCollection<string> Services);

    /// <summary>
    /// Denetim portu hükmü. SAF fonksiyon: girdileri dışarıdan alır, yan etkisi yoktur, bu yüzden
    /// gerçek bir port çakışması kurmadan tam doğruluk tablosuyla test edilebilir
    /// (<see cref="ClassifyUserNameClaim"/> ile aynı desen).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>NEDEN "site çalışıyor mu" SORUSU YETMİYOR — ölçüldü (19.08.2026).</b> Bu makinede
    /// <c>Get-Website</c> siteyi <c>Started</c> raporluyor, FTPSVC <c>Running</c> ve 21 portunda
    /// GERÇEKTEN dinliyor (pid 5068). Buna rağmen porta bağlanan istemciyi FileZilla Server 0.9.59
    /// karşılıyor, çünkü FileZilla da aynı portu bağlamış (pid 5036). Yani iki dinleyici bir arada
    /// var olabiliyor ve tek arıza belirtisi portun PAYLAŞILMASI. Sitenin durumuna bakan bir kontrol
    /// bu hatayı GÖREMEZDİ.
    /// </para>
    /// <para>
    /// <b>Neden banner'a bakılmıyor:</b> 21 portundan okunan <c>220-FileZilla Server…</c> selamı
    /// suçluyu doğrudan söylüyor, ama IIS FTP'nin selamı yapılandırmayla değiştirilebilir
    /// (<c>ftpServer.messages.bannerMessage</c>) — metne bakan bir kontrol, selamı özelleştirmiş
    /// dürüst bir kurulumu suçlu ilan ederdi. Süreç kimliği karşılaştırması yapılandırmadan
    /// bağımsızdır.
    /// </para>
    /// <para>
    /// Süreç kimliği İKİ kaynaktan doğrulanır (PID listesi ve o süreçteki servis adları): FTPSVC
    /// paylaşımlı bir <c>svchost</c> içinde çalışır ve tek bir PID sorgusu yarış hâlinde bayatlayıp
    /// KENDİ servisimizi yabancı ilan edebilirdi — yanlış alarm, çakışmayı kaçırmak kadar kötüdür.
    /// </para>
    /// </remarks>
    internal static (FtpControlPortDurumu Durum, string Sahip) ClassifyControlPort(
        bool olculebildi,
        IReadOnlyCollection<FtpPortDinleyici> dinleyiciler,
        IReadOnlyCollection<int> ftpsvcPidleri)
    {
        if (!olculebildi)
        {
            return (FtpControlPortDurumu.Olculemedi, string.Empty);
        }

        if (dinleyiciler.Count == 0)
        {
            return (FtpControlPortDurumu.DinleyenYok, string.Empty);
        }

        var yabancilar = dinleyiciler
            .Where(item => !ftpsvcPidleri.Contains(item.Pid))
            .Where(item => !item.Services.Any(ad => string.Equals(ad, "FTPSVC", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (yabancilar.Count == 0)
        {
            return (FtpControlPortDurumu.PanelinServisi, string.Empty);
        }

        var aciklama = string.Join(", ", yabancilar.Select(item =>
        {
            var ad = string.IsNullOrWhiteSpace(item.Process) ? "bilinmeyen süreç" : item.Process + ".exe";
            var servisler = item.Services.Count == 0 ? string.Empty : $", Windows servisi: {string.Join("/", item.Services)}";
            return $"{ad} (PID {item.Pid}{servisler})";
        }));

        return (FtpControlPortDurumu.YabanciSurec, aciklama);
    }

    /// <summary>Probe'un ham ölçümü; <c>null</c> = hiç ölçülemedi.</summary>
    private sealed record FtpRuntimeOlcum(
        bool SiteExists,
        string SiteState,
        string ServiceState,
        IReadOnlyCollection<int> ServicePids,
        bool PortOlculebildi,
        IReadOnlyCollection<FtpPortDinleyici> Listeners);

    /// <summary>
    /// FTP çalışma zamanını TEK bir PowerShell çağrısında ölçer (servis, site, portu dinleyen tüm
    /// süreçler). Hiçbir şeyi değiştirmez.
    /// </summary>
    /// <remarks>
    /// <c>ComponentPreflight.BuildProbeScript</c> BİLEREK kullanılmadı: o probe port başına
    /// <c>Select-Object -First 1</c> ile TEK dinleyici alır, çünkü sorusu "kurulumdan önce port boş
    /// mu". Buradaki soru farklı ve tam da ölçülen hataya denk geliyor — port PAYLAŞILIYOR mu.
    /// Tek dinleyici alan bir sorgu bu makinede FTPSVC'yi döndürüp FileZilla'yı hiç göremezdi.
    /// </remarks>
    private async Task<FtpRuntimeOlcum?> ProbeFtpRuntimeAsync(CancellationToken cancellationToken)
    {
        var script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            $portOlculdu = [bool](Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue)
            $siteVar = $false
            $siteDurum = ''
            $servisDurum = ''
            $servisPidleri = @()
            $svc = Get-CimInstance Win32_Service -Filter "Name='FTPSVC'" -ErrorAction SilentlyContinue
            if ($svc) {
              $servisDurum = [string]$svc.State
              if ([int]$svc.ProcessId -gt 0) { $servisPidleri = @([int]$svc.ProcessId) }
            }
            Import-Module WebAdministration -ErrorAction SilentlyContinue
            try {
              $site = Get-Website -Name '{{EscapePowerShellLiteral(FtpSiteName)}}' -ErrorAction SilentlyContinue
              if ($site) { $siteVar = $true; $siteDurum = [string]$site.State }
            } catch { }
            $satirlar = @()
            if ($portOlculdu) {
              foreach ($k in @(Get-NetTCPConnection -LocalPort {{FtpPort}} -State Listen -ErrorAction SilentlyContinue)) {
                $sp = Get-Process -Id $k.OwningProcess -ErrorAction SilentlyContinue
                $servisAdlari = @(Get-CimInstance Win32_Service -Filter "ProcessId=$($k.OwningProcess)" -ErrorAction SilentlyContinue | ForEach-Object { [string]$_.Name })
                $satirlar += [pscustomobject]@{ pid = [int]$k.OwningProcess; process = [string]$sp.ProcessName; services = @($servisAdlari) }
              }
            }
            [pscustomobject]@{
              siteExists = $siteVar
              siteState = $siteDurum
              serviceState = $servisDurum
              servicePids = @($servisPidleri)
              portMeasured = $portOlculdu
              listeners = @($satirlar | Group-Object pid | ForEach-Object { $_.Group[0] })
            } | ConvertTo-Json -Depth 5 -Compress
            """;

        var sonuc = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!sonuc.Success || string.IsNullOrWhiteSpace(sonuc.StandardOutput))
        {
            return null;
        }

        try
        {
            using var belge = JsonDocument.Parse(sonuc.StandardOutput);
            var kok = belge.RootElement;

            var dinleyiciler = new List<FtpPortDinleyici>();
            foreach (var satir in DiziOku(kok, "listeners"))
            {
                dinleyiciler.Add(new FtpPortDinleyici(
                    satir.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out var pidDeger) ? pidDeger : 0,
                    MetinOku(satir, "process"),
                    DiziOku(satir, "services").Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToArray()));
            }

            return new FtpRuntimeOlcum(
                kok.TryGetProperty("siteExists", out var siteVar) && siteVar.ValueKind == JsonValueKind.True,
                MetinOku(kok, "siteState"),
                MetinOku(kok, "serviceState"),
                DiziOku(kok, "servicePids").Where(item => item.TryGetInt32(out _)).Select(item => item.GetInt32()).ToArray(),
                kok.TryGetProperty("portMeasured", out var portOlculdu) && portOlculdu.ValueKind == JsonValueKind.True,
                dinleyiciler);
        }
        catch (JsonException)
        {
            // Bozuk/eksik JSON "sorun yok" değildir: çağıran bunu "ölçülemedi" olarak raporlar.
            return null;
        }
    }

    /// <summary>
    /// ConvertTo-Json TEK ÖĞELİ diziyi diziye SARMAZ: <c>@($x) | ConvertTo-Json</c> nesne döndürür.
    /// Bu ayrım ölçülmeden yazılan her ayrıştırıcıyı tek dinleyici/tek PID durumunda sessizce
    /// boşa düşürür — tam da çakışmanın tek yabancı süreçten ibaret olduğu olağan durumda.
    /// </summary>
    private static IEnumerable<JsonElement> DiziOku(JsonElement kok, string ad)
    {
        if (!kok.TryGetProperty(ad, out var deger))
        {
            yield break;
        }

        if (deger.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in deger.EnumerateArray())
            {
                yield return item;
            }
        }
        else if (deger.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            yield return deger;
        }
    }

    private static string MetinOku(JsonElement kok, string ad)
        => kok.TryGetProperty(ad, out var deger) && deger.ValueKind == JsonValueKind.String
            ? deger.GetString() ?? string.Empty
            : string.Empty;

    // M5 (profesyonel paneller gibi opsiyonel "yalnız güvenli FTP"): FTPS zorunlu mu? Varsayılan false = düz + FTPS (kilitlenme yok).
    public async Task<bool> GetRequireSslAsync(CancellationToken cancellationToken = default)
        => (await fileStore.ReadAsync(TlsStatePath, new FtpTlsState(), cancellationToken)).RequireSsl;

    public async Task<bool> SetRequireSslAsync(bool requireSsl, CancellationToken cancellationToken = default)
    {
        await fileStore.WriteAsync(TlsStatePath, new FtpTlsState { RequireSsl = requireSsl }, cancellationToken);
        await EnsureFtpInfrastructureAsync(true, cancellationToken); // SSL politikası değişti → tam yeniden kur (controlChannelPolicy 0/1 + FTPSVC restart)
        return requireSsl;
    }

    private sealed class FtpTlsState
    {
        public bool RequireSsl { get; set; }
    }

    // FTP altyapısı (IIS FTP sitesi + SSL + firewall) bir kez tam kurulduğunda işaretlenir → sonraki kullanıcı
    // eklemeleri ağır kurulumu + FTPSVC yeniden başlatmayı ATLAR (hızlı yol). SSL politikası değişimi force=true ile tam kurar.
    private sealed class FtpInfraState
    {
        public bool Configured { get; set; }
    }

    public async Task<IReadOnlyCollection<FtpUserDto>> GetFtpUsersAsync(CancellationToken cancellationToken = default)
    {
        var records = await fileStore.ReadAsync(StatePath, Array.Empty<FtpUserState>(), cancellationToken);
        var list = new List<FtpUserDto>(records.Length);

        foreach (var record in records.OrderBy(item => item.UserName, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(new FtpUserDto
            {
                UserName = record.UserName,
                HomePath = record.HomePath,
                SiteName = record.SiteName,
                CreatedAtUtc = record.CreatedAtUtc,
                IsEnabled = record.IsEnabled,
                ExistsOnSystem = await UserExistsAsync(record.UserName, cancellationToken),
                ServerAddress = Environment.MachineName,
                Port = FtpPort
            });
        }

        return list;
    }

    public async Task<FtpUserDto> CreateFtpUserAsync(CreateFtpUserRequest request, CancellationToken cancellationToken = default)
    {
        var userName = NormalizeUserName(request.UserName);
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Trim().Length < 8)
        {
            throw new AppException("ftp.password_too_short", 400, 400);
        }

        EnsureCommandSafePassword(request.Password);

        // KULLANICI ADI DA BİR SAHİPLİK İDDİASIDIR — ve hiç sorulmuyordu.
        //
        // Bu uç SİTEYİ doğruluyordu (FtpController.EnsureSiteAsync), ama mutasyona uğrayan nesne
        // site değil GERÇEK BİR WINDOWS YEREL HESABI. Hesabın mevcut sahipliği hiç sorulmadığı için
        // zincir şöyle işliyordu:
        //   1. saldırgan kendi sitesi için UserName="Administrator" ile oluşturma isteği atar;
        //   2. hesap zaten var → `net user /add` atlanır ama akış DEVAM eder;
        //   3. Administrator'a saldırganın klasöründe (OI)(CI)M verilir, LocalUser junction'ı
        //      saldırganın klasörüne yönlendirilir;
        //   4. ftp-users.json kaydındaki SiteName saldırganın sitesiyle EZİLİR;
        //   5. FtpController.EnsureFtpUserScopeAsync sahipliği tam da o ezilmiş kayıttan okuduğu
        //      için kapsam denetimi GEÇER;
        //   6. POST /ftp/users/password → `net user "Administrator" "<yeni parola>" /y`.
        // Yani kapsam denetimi, saldırganın kendi yazdığı kayda soruyordu. Denetlenen değeri
        // kiracının değiştirebilmesi hata sınıfının ta kendisi.
        //
        // Karar SitesController'daki "SİTE ADI DA BİR SAHİPLİK İDDİASIDIR" desenini izler: iddia
        // edilen ad başkasınınsa reddedilir. İki kaynak var ve ikisi de sorulur — panelin kendi
        // kaydı (hangi siteye ait) ve Windows'un kendisi (panel dışı hesap).
        //
        // KONUM ÖNEMLİ: bu denetim klasör oluşturma / IIS FTP altyapısı kurulumu / ACL'den ÖNCE.
        // Reddedilen bir istek sunucuda HİÇBİR yan etki bırakmamalı; eskiden altyapı kurulumu ve
        // dizin oluşturma denetimden önce çalışıyordu.
        await EnsureUserNameIsClaimableAsync(userName, request.SiteName, cancellationToken);

        var homePath = await ResolveHomePathAsync(request, cancellationToken);
        Directory.CreateDirectory(homePath);
        await EnsureFtpInfrastructureAsync(false, cancellationToken);

        // BU ÇAĞRI hesabı gerçekten AÇTI mı? Geri alma yalnız BUNA bakar (gerekçe: RollbackCreateAsync).
        var hesabiBuCagriActi = false;
        if (!await UserExistsAsync(userName, cancellationToken))
        {
            var createResult = await ProcessRunner.RunAsync("net.exe", $"user \"{userName}\" \"{request.Password}\" /add /y", cancellationToken: cancellationToken);
            if (!createResult.Success)
            {
                throw new AppException("ftp.user_create_failed", 500, 500, BuildCommandDetails(createResult));
            }

            hesabiBuCagriActi = true;
        }

        // YARIM KALAN OLUŞTURMA ÖKSÜZ HESAP BIRAKIYORDU (19.08.2026, ölçüldü).
        //
        // Ölçülen zincir: yetki kuralı kaldırma sessizce başarısız olduğu için (bkz.
        // RemoveUserAuthorizationSnippet) ConfigureUserHomeAsync "yinelenen koleksiyon girişi" ile
        // 500 attı. Ama `net user /add` ÇOKTAN çalışmıştı: `net user <ad>` hesabın VAR olduğunu
        // gösteriyor, `GET /ftp/users` onu LİSTELEMİYORDU (kayıt aşağıda, hatadan SONRA yazılıyor).
        // Yani geriye parolası operatörün belirlediği, panelin hiç görmediği, panelden silinemeyen
        // bir Windows yerel hesabı kalıyordu — üstelik ana dizinde ACL'i duruyordu.
        //
        // Kök neden düzeltildi ama tek başına yetmez: aradaki HER adım (ACL, junction, IIS yazımı,
        // disk dolu, IIS kilidi) başarısız olabilir ve hepsi aynı öksüzü doğurur. Bu yüzden hata
        // yolu artık YAPILANLARI GERİ ALIYOR: oluşturma ya tamamlanır ya da sunucuda iz bırakmaz —
        // "SALDIRI_reddedilen_istek_sunucuda_HICBIR_iz_birakmaz" testinin koruduğu ilkenin aynısı,
        // bu kez reddedilen değil YARIM KALAN istek için.
        try
        {
            var aclResult = await ProcessRunner.RunAsync(
                "icacls.exe",
                $"\"{homePath}\" /grant \"{userName}:(OI)(CI)M\" /T /C",
                cancellationToken: cancellationToken);
            if (!aclResult.Success)
            {
                throw new AppException("ftp.acl_failed", 500, 500, BuildCommandDetails(aclResult));
            }

            await ConfigureUserHomeAsync(userName, homePath, cancellationToken);
        }
        catch (AppException hata)
        {
            // Geri alma sonucu HATA METNİNE eklenir: temizlik de başarısız olduysa operatör bunu
            // görmeli, yoksa "geri alındı" varsayıp sunucuda duran hesabı hiç aramaz.
            var geriAlmaNotu = await RollbackCreateAsync(userName, homePath, hesabiBuCagriActi);
            throw new AppException(hata.MessageKey, hata.Code, hata.HttpStatusCode, hata.Details + geriAlmaNotu);
        }
        catch
        {
            // AppException dışındaki her şey de (IO hatası, iptal, beklenmeyen istisna) aynı öksüzü
            // doğurur; geri alma istisnanın TÜRÜNE bağlı olamaz. Özgün istisna olduğu gibi yükselir.
            await RollbackCreateAsync(userName, homePath, hesabiBuCagriActi);
            throw;
        }

        var record = new FtpUserState
        {
            UserName = userName,
            SiteName = request.SiteName.Trim(),
            HomePath = homePath,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow
        };

        // KAYIT ATOMİK YAZILMALI. Kilitsiz oku-değiştir-yaz burada ÖKSÜZ WINDOWS HESABI bırakıyordu:
        // gerçek hesap yukarıda `net user /add` ile çoktan açılıyor, panel kaydı ancak burada
        // yazılıyor. Eşzamanlı bir silme işlemi bu yazımı ezerse hesap Windows'ta parolasıyla
        // var olmaya devam eder ama panel onu HİÇ görmez: listelenemez, silinemez, denetlenemez —
        // ve site klasöründeki ACL'i durur.
        await fileStore.UpdateAsync(
            StatePath,
            Array.Empty<FtpUserState>(),
            mevcut =>
            {
                var liste = mevcut.ToList();
                var index = liste.FindIndex(item => string.Equals(item.UserName, userName, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    liste[index] = record;
                }
                else
                {
                    liste.Add(record);
                }

                return liste.ToArray();
            },
            cancellationToken);
        return new FtpUserDto
        {
            UserName = record.UserName,
            SiteName = record.SiteName,
            HomePath = record.HomePath,
            CreatedAtUtc = record.CreatedAtUtc,
            IsEnabled = true,
            ExistsOnSystem = true,
            ServerAddress = Environment.MachineName,
            Port = FtpPort
        };
    }

    /// <summary>
    /// Yarım kalan bir oluşturmanın SUNUCUDAKİ izlerini geri alır; sonucu hata metnine eklenmek
    /// üzere metin olarak döner (boş dize = geri alacak bir şey yoktu).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>YALNIZ BU ÇAĞRININ YAPTIĞI GERİ ALINIR.</b> Windows hesabı ancak
    /// <paramref name="hesabiBuCagriActi"/> ise silinir. Aksi hâlde meşru "kendi hesabımı yeniden
    /// oluştur" akışı (<see cref="FtpUserNameClaim.KendiKaydi"/>) bir ara adımda takıldığında
    /// müşterinin ÇALIŞAN hesabını silerdik — hatayı düzeltirken ondan beter bir yıkım. Hesap zaten
    /// varken de öksüz doğmaz: hesap bizden önce oradaydı.
    /// </para>
    /// <para>
    /// <b>ANA DİZİNE DOKUNULMAZ.</b> <c>homePath</c> müşterinin SİTE KÖKÜDÜR; burada silinecek tek
    /// şey verilen ACL'dir (<c>icacls /remove:g</c> yalnız o hesabın izin girdilerini kaldırır),
    /// dosyalar değil. Silinen hesabın SID'i ACL'de "hesap bilinmiyor" olarak asılı kalmasın diye
    /// bu adım hesap silmeden ÖNCE yapılır.
    /// </para>
    /// <para>
    /// <b>İPTAL BELİRTECİ KULLANILMAZ (<see cref="CancellationToken.None"/>).</b> Geri almanın en
    /// çok gerektiği durumlardan biri isteğin iptal edilmesidir; iptal edilmiş bir belirteçle
    /// temizlik çalıştırmak, önlemeye çalıştığımız öksüz hesabı garanti ederdi.
    /// </para>
    /// <para>
    /// Adımlar EN İYİ ÇABA'dır ve biri diğerini engellemez: temizlik kendisi patlayıp asıl hata
    /// mesajını gölgelemesin. Ama sessiz de değil — her başarısızlık döndürülen nota yazılır.
    /// </para>
    /// </remarks>
    // internal: testler bu metodu DOGRUDAN kosturur. Tam CreateFtpUserAsync yolundan test etmek
    // MUMKUN DEGIL — o yol EnsureFtpInfrastructureAsync'i cagirir, yani yukseltilmis bir makinede
    // GERCEK "AdaPanel FTP" sitesini yeniden yapilandirip FTPSVC'yi yeniden baslatirdi. Bir testin
    // canli FTP sunucusunu yeniden baslatmasi kabul edilemez; geri alma davranisi burada olculur.
    internal async Task<string> RollbackCreateAsync(string userName, string homePath, bool hesabiBuCagriActi)
    {
        var notlar = new List<string>();

        async Task DeneAsync(string ad, Func<Task> is_)
        {
            try
            {
                await is_();
            }
            catch (Exception istisna)
            {
                notlar.Add($"{ad}: {istisna.Message}");
            }
        }

        await DeneAsync("acl-geri-alma", async () =>
        {
            var sonuc = await ProcessRunner.RunAsync(
                "icacls.exe",
                $"\"{homePath}\" /remove:g \"{userName}\" /T /C",
                cancellationToken: CancellationToken.None);
            if (!sonuc.Success)
            {
                notlar.Add($"acl-geri-alma: ExitCode={sonuc.ExitCode}");
            }
        });

        // Yetki kuralı + junction: ikisi de bu akışta oluşturulmuş olabilir ve ikisi de idempotent
        // kaldırılır (yoksa sorun değil). Kural kalırsa ad bir daha kullanılamaz — düzeltilen
        // hatanın ta kendisi.
        await DeneAsync("yetki-junction-geri-alma", () => RemoveUserAuthorizationAsync(userName, CancellationToken.None));

        if (hesabiBuCagriActi)
        {
            await DeneAsync("hesap-silme", async () =>
            {
                var sonuc = await ProcessRunner.RunAsync("net.exe", $"user \"{userName}\" /delete", cancellationToken: CancellationToken.None);

                // "Komut hata vermedi" yetmez: hesabın GERÇEKTEN gittiği ölçülür.
                if (await UserExistsAsync(userName, CancellationToken.None))
                {
                    notlar.Add($"hesap-silme: hesap HALA DURUYOR (ExitCode={sonuc.ExitCode}) — '{userName}' elle silinmeli.");
                }
            });
        }

        return notlar.Count == 0
            ? string.Empty
            : $"{Environment.NewLine}GeriAlmaUyarilari:{Environment.NewLine}{string.Join(Environment.NewLine, notlar)}";
    }

    public async Task SetFtpPasswordAsync(SetFtpPasswordRequest request, CancellationToken cancellationToken = default)
    {
        var userName = NormalizeUserName(request.UserName);
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Trim().Length < 8)
        {
            throw new AppException("ftp.password_too_short", 400, 400);
        }

        EnsureCommandSafePassword(request.Password);

        // PANELİN YÖNETMEDİĞİ HESABIN PAROLASI DEĞİŞTİRİLEMEZ (K6 zincirinin son halkası).
        //
        // Oluşturma yolu artık ad iddiasını denetliyor; bu ikinci kapı savunma derinliği: kayıt
        // başka bir yoldan (elle düzenlenmiş ftp-users.json, gelecekteki bir çağıran) kirletilse
        // bile panel yalnız KENDİ açtığı hesapların parolasını değiştirir. `net user <ad> <parola>`
        // ayrım yapmaz — Administrator da bir yerel hesaptır.
        //
        // Kayıt kontrolü Windows sorgusundan ÖNCE yapılır ve aynı "bulunamadı" hatası döner:
        // aksi hâlde yanıt farkı, sunucuda hangi yerel hesapların VAR olduğunu sızdıran bir
        // numaralandırma aracına dönüşürdü.
        var kayitlar = await fileStore.ReadAsync(StatePath, Array.Empty<FtpUserState>(), cancellationToken);
        if (!kayitlar.Any(item => string.Equals(item.UserName.Trim(), userName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AppException("ftp.user_not_found", 404, 404);
        }

        if (!await UserExistsAsync(userName, cancellationToken))
        {
            throw new AppException("ftp.user_not_found", 404, 404);
        }

        // net user <ad> <parola> (/add YOK) parolayı değiştirir.
        var result = await ProcessRunner.RunAsync(
            "net.exe",
            $"user \"{userName}\" \"{request.Password}\" /y",
            cancellationToken: cancellationToken);
        if (!result.Success)
        {
            throw new AppException("ftp.password_change_failed", 500, 500, BuildCommandDetails(result));
        }
    }

    public async Task DeleteFtpUserAsync(string userName, CancellationToken cancellationToken = default)
    {
        var normalizedUserName = NormalizeUserName(userName);
        await RemoveUserAuthorizationAsync(normalizedUserName, cancellationToken);

        if (await UserExistsAsync(normalizedUserName, cancellationToken))
        {
            var result = await ProcessRunner.RunAsync("net.exe", $"user \"{normalizedUserName}\" /delete", cancellationToken: cancellationToken);
            if (!result.Success)
            {
                // net.exe başarısızsa (lokalizasyon/kilit) PowerShell Remove-LocalUser ile dene.
                var psScript = $"$ErrorActionPreference='SilentlyContinue'; " +
                    $"Remove-LocalUser -Name '{normalizedUserName.Replace("'", "''")}'";
                await ProcessRunner.RunPowerShellAsync(psScript, cancellationToken);

                // Yine de duruyorsa gerçek hata; yoksa silinmiş kabul et (idempotent).
                if (await UserExistsAsync(normalizedUserName, cancellationToken))
                {
                    throw new AppException("ftp.user_delete_failed", 500, 500, BuildCommandDetails(result));
                }
            }
        }

        // Silme de atomik: aksi hâlde eşzamanlı bir oluşturma bu silmeyi ezip kaldırılmış hesabı
        // panelde geri getirirdi (gerekçe: CreateFtpUserAsync).
        await fileStore.UpdateAsync(
            StatePath,
            Array.Empty<FtpUserState>(),
            mevcut => mevcut
                .Where(item => !string.Equals(item.UserName, normalizedUserName, StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            cancellationToken);
    }

    private async Task EnsureFtpInfrastructureAsync(bool forceReconfigure, CancellationToken cancellationToken)
    {
        // IIS FTP rolü kurulu değilse New-WebFtpSite ve ftpServer config bölümleri bulunmaz → script
        // "ftp.site_configuration_failed" ile patlar (canlı VPS bulgusu). Classic ASP/PHP gibi OTOMATİK KUR (atlama yok).
        await EnsureFtpFeatureInstalledAsync(cancellationToken);

        var ftpRoot = Path.Combine(dataRoot, "ftp-root");
        Directory.CreateDirectory(Path.Combine(ftpRoot, "LocalUser"));

        // HIZLI YOL: altyapı bir kez tam kurulduysa (bayrak) ve FTPSVC çalışıyorsa ağır yeniden-kurulumu + FTPSVC
        // yeniden başlatmayı ATLA → 2. ve sonraki FTP kullanıcısı eklemesi saniyelerce beklemeden, anında olur.
        // (SSL politikası değişimi gibi gerçekten gereken durumlar force=true ile tam yeniden kurar.)
        if (!forceReconfigure && await IsFtpInfrastructureReadyAsync(cancellationToken))
        {
            return;
        }

        // Pasif (PASV) mod: sunucu, veri kanalı için istemciye bir IP bildirir. Bu IP yanlış/iç IP olursa
        // istemci bağlanır, giriş yapar ama DİZİN LİSTESİ/transfer TAKILIR. Panelde tanımlı public IPv4'ü
        // FTP sitesine externalIp4Address olarak yazıyoruz; tanımlı değilse IIS kendi IP'sini kullanır.
        var preferences = await panelPreferencesService.GetAsync(cancellationToken);
        var externalIp = preferences.ServerAddress?.Trim() ?? string.Empty;
        var externalIpLine = System.Net.IPAddress.TryParse(externalIp, out var parsedIp)
                && parsedIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? $"Set-ItemProperty \"IIS:\\Sites\\$siteName\" -Name ftpServer.firewallSupport.externalIp4Address -Value '{EscapePowerShellLiteral(externalIp)}'"
            : "# sunucu IPv4 adresi panelde tanimli degil; pasif mod sunucunun kendi IP'sini kullanir";

        // M5 (profesyonel paneller gibi opsiyonel): FTPS zorunlu mu? 0=SslAllow (düz+FTPS, varsayılan), 1=SslRequire (yalnız FTPS).
        var requireSsl = (await fileStore.ReadAsync(TlsStatePath, new FtpTlsState(), cancellationToken)).RequireSsl;
        var policy = requireSsl ? 1 : 0;
        var policyName = requireSsl ? "SslRequire" : "SslAllow";

        var script = $$"""
            Import-Module WebAdministration -ErrorAction Stop
            $siteName = '{{EscapePowerShellLiteral(FtpSiteName)}}'
            $root = '{{EscapePowerShellLiteral(ftpRoot)}}'
            if (-not (Test-Path "IIS:\Sites\$siteName")) {
              New-WebFtpSite -Name $siteName -Port {{FtpPort}} -PhysicalPath $root -Force | Out-Null
            }
            Set-ItemProperty "IIS:\Sites\$siteName" -Name serverAutoStart -Value $true
            # KRITIK KOK NEDEN (2026-06-21): FTP per-site ayarlari Set-WebConfigurationProperty -Location ile
            # YAZILAMAZ. ftpServer config <site> etiketinin ICINE gomulu; -Location ise <location> bloklarina
            # bakar -> sessizce "configuration object not found at location" UYARISI verip HICBIR SEYI
            # uygulamaz (script yine basarili doner). Sonuc: site IIS varsayilani SslRequire + sertifikasiz +
            # basicAuth kapali'da kalir = "534 Policy requires SSL" KILIDI. Dogru yol: IIS provider noktali
            # ozellik yolu (serverAutoStart'in calismasi gibi). Asagidaki TUM yazimlar provider yolu kullanir.
            Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.authentication.anonymousAuthentication.enabled -Value $false
            Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.authentication.basicAuthentication.enabled -Value $true
            Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.userIsolation.mode -Value 3
            Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.ssl.controlChannelPolicy -Value {{policy}}
            Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.ssl.dataChannelPolicy -Value {{policy}}
            # FTPS sertifikasi: yoksa self-signed olustur ve bagla. serverCertHash bos olursa IIS "AUTH TLS"i
            # reddeder; bagliyken FTPS calisir. controlChannelPolicy=0 ile duz baglanti da kabul -> kimse kilitlenmez.
            $ftpsCert = Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -eq 'AdaPanel FTPS' } | Select-Object -First 1
            if (-not $ftpsCert) {
              try { $ftpsCert = New-SelfSignedCertificate -DnsName $env:COMPUTERNAME -FriendlyName 'AdaPanel FTPS' -CertStoreLocation 'Cert:\LocalMachine\My' -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(5) } catch { $ftpsCert = $null }
            }
            if ($ftpsCert) {
              try {
                Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.ssl.serverCertHash -Value $ftpsCert.Thumbprint
                Set-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.ssl.ssl128 -Value $true
              } catch { }
            }
            # Pasif port araligi SUNUCU seviyesinde (location'siz) -> Set-WebConfigurationProperty dogru calisir.
            Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter 'system.ftpServer/firewallSupport' -Name lowDataChannelPort -Value {{PassivePortStart}}
            Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter 'system.ftpServer/firewallSupport' -Name highDataChannelPort -Value {{PassivePortEnd}}
            {{externalIpLine}}
            # SESSIZ BASARISIZLIK BIR DAHA OLMASIN: ayarin gercekten uygulandigini geri-okuyup dogrula.
            # NOT: bu geri-okuma yalnız UYARI verir, ASLA throw etmez. Bazı IIS sürümlerinde Get-ItemProperty
            # değeri farklı tipte döndürdüğü için (.Value boş/enum-adı) eski throw, Set basarili olsa bile
            # FTP'yi tümden bloke ediyordu (yanlis-pozitif). Set yukarida calisti; dogrulama bilgilendirme amacli.
            $applied = "$((Get-ItemProperty "IIS:\Sites\$siteName" -Name ftpServer.security.ssl.controlChannelPolicy -ErrorAction SilentlyContinue).Value)"
            if ($applied -ne '{{policy}}' -and $applied -ne '{{policyName}}') { Write-Warning "FTP SSL ayari dogrulanamadi (controlChannelPolicy=$applied, beklenen={{policy}})." }
            Start-WebSite -Name $siteName -ErrorAction SilentlyContinue
            # Pasif port aralığı (firewallSupport) ve SSL sertifikası değişikliklerinin uygulanması için
            # Microsoft FTP Service yeniden başlatılır (IIS bu ayarları canlı okumaz).
            Restart-Service FTPSVC -Force -ErrorAction SilentlyContinue
            if (-not (Get-NetFirewallRule -DisplayName 'AdaPanel FTP Control' -ErrorAction SilentlyContinue)) {
              New-NetFirewallRule -DisplayName 'AdaPanel FTP Control' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {{FtpPort}} -Profile Any | Out-Null
            }
            if (-not (Get-NetFirewallRule -DisplayName 'AdaPanel FTP Passive' -ErrorAction SilentlyContinue)) {
              New-NetFirewallRule -DisplayName 'AdaPanel FTP Passive' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {{PassivePortStart}}-{{PassivePortEnd}} -Profile Any | Out-Null
            }
            """;

        var result = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("ftp.site_configuration_failed", 500, 500, BuildCommandDetails(result));
        }

        // Tam kurulum başarılı → bayrağı işaretle ki sonraki kullanıcı eklemeleri hızlı yolu kullansın (servis restart yok).
        await fileStore.WriteAsync(InfraStatePath, new FtpInfraState { Configured = true }, cancellationToken);
    }

    // Altyapı hazır mı (hızlı kontrol): daha önce tam kuruldu (bayrak) VE FTPSVC çalışıyor mu. FTPSVC kontrolü
    // Get-Service ile yapılır (yavaş 'Import-Module WebAdministration' YOK → sub-saniye). Biri bile sağlanmazsa false →
    // çağıran tam kurulumu çalıştırır (kendini onarır). Böylece ağır betik + servis restart yalnız gerektiğinde çalışır.
    private async Task<bool> IsFtpInfrastructureReadyAsync(CancellationToken cancellationToken)
    {
        var state = await fileStore.ReadAsync(InfraStatePath, new FtpInfraState(), cancellationToken);
        if (!state.Configured)
        {
            return false;
        }

        var probe = await ProcessRunner.RunPowerShellAsync(
            "if ((Get-Service FTPSVC -ErrorAction SilentlyContinue).Status -eq 'Running') { 'up' } else { 'down' }",
            cancellationToken);
        return probe.Success && probe.StandardOutput.Contains("up", StringComparison.OrdinalIgnoreCase);
    }

    // IIS FTP sunucu rolü (FTPSVC servisi) yoksa OTOMATİK KURAR. Windows Server: Web-Ftp-Server (+ yönetim
    // araçları); Windows Client (10/11): IIS-FTPServer (-All ile alt özellik IIS-FTPSvc dahil). PHP/Classic ASP
    // on-demand kurulumuyla aynı desen — "atlama yok". Kurulamazsa net hata fırlatır (sessiz başarısızlık yok).
    private static async Task EnsureFtpFeatureInstalledAsync(CancellationToken cancellationToken)
    {
        const string probe = "if (Get-Service FTPSVC -ErrorAction SilentlyContinue) { 'present' } else { 'absent' }";

        var check = await ProcessRunner.RunPowerShellAsync(probe, cancellationToken);
        if (check.Success && check.StandardOutput.Contains("present", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        const string script =
            "if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) { " +
            "Install-WindowsFeature -Name Web-Ftp-Server -IncludeManagementTools -ErrorAction Stop | Out-Null } " +
            "else { Enable-WindowsOptionalFeature -Online -FeatureName IIS-FTPServer -All -NoRestart -ErrorAction Stop | Out-Null }";
        var install = await ProcessRunner.RunPowerShellAsync(script, cancellationToken);

        var verify = await ProcessRunner.RunPowerShellAsync(probe, cancellationToken);
        if (!(verify.Success && verify.StandardOutput.Contains("present", StringComparison.OrdinalIgnoreCase)))
        {
            throw new AppException(
                "ftp.feature_install_failed",
                503,
                503,
                "IIS FTP rolu (Web-Ftp-Server/IIS-FTPServer) otomatik kurulamadi; FTPSVC servisi bulunamadi. "
                + BuildCommandDetails(install));
        }
    }

    /// <summary>
    /// <c>$userRoot</c> klasör bağlantısını GÜVENLE kaldıran PowerShell parçası.
    /// <para>
    /// NEDEN ÖZEL BİR YOL: <c>$userRoot</c> bir junction'dır ve HEDEFİ müşterinin site köküdür
    /// (bkz. <see cref="ResolveHomePathAsync"/>). Windows PowerShell 5.1'in bilinen reparse-point
    /// hatasında <c>Remove-Item -Recurse</c> bağlantıyı TAKİP EDER ve hedefteki dosyaları siler —
    /// yani bir FTP kullanıcısını silmek ya da ana dizinini değiştirmek, müşterinin bütün site
    /// dosyalarını götürebilir. Yedekten dönmeden geri alınamaz.
    /// </para>
    /// <para>
    /// <c>[IO.Directory]::Delete</c> bağlantıyı takip etmez; yalnızca bağlantının kendisini kaldırır.
    /// Bu yüzden önce "bu bir bağlantı mı" diye bakılır. Windows sürümünden bağımsız güvenlidir,
    /// dolayısıyla hatanın giderilmiş olduğu sürümlerde de doğru davranır.
    /// </para>
    /// </summary>
    // internal: testler KOPYASINI degil BU dizeyi calistirir; kopya test etmek hicbir sey kanitlamaz.
    internal const string RemoveUserRootSnippet = """
        if (Test-Path -LiteralPath $userRoot) {
          $adaOge = Get-Item -LiteralPath $userRoot -Force
          if ($adaOge.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            [IO.Directory]::Delete($userRoot)
          } else {
            Remove-Item -LiteralPath $userRoot -Force -Recurse
          }
        }
        """;

    /// <summary>
    /// Kullanıcının FTP yetki kuralını (<c>&lt;add accessType="Allow" users="…"/&gt;</c>) kaldıran
    /// PowerShell parçası. <c>$siteName</c> ve <c>$userName</c> tanımlı olmalıdır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// KÖK NEDEN (19.08.2026, bu makinede ölçüldü): burada <c>Clear-WebConfiguration … -AtElement
    /// @{users=$userName} -ErrorAction SilentlyContinue</c> yazıyordu. <b>Bu cmdlet'in
    /// <c>-AtElement</c> parametresi YOKTUR.</b> Ölçüm — panelin kendi terminal ucundan,
    /// <c>-ErrorAction Stop</c> ile: <c>ParameterBindingException :: A parameter cannot be found
    /// that matches parameter name 'AtElement'</c>. Gerçek parametre listesi:
    /// <c>Clr, Force, Location, Filter, PSPath</c> (+ ortak parametreler). <c>-AtElement</c>'i
    /// destekleyen cmdlet <c>Remove-WebConfigurationProperty</c>'dir
    /// (<c>Name, Clr, AtElement, AtIndex, AtName, Force, Location, Filter, PSPath</c>).
    /// </para>
    /// <para>
    /// <c>-ErrorAction SilentlyContinue</c> bağlama hatasını YUTUYOR ve betik sıfırla çıkıyordu.
    /// İki ağır sonucu ölçüldü:
    /// <list type="number">
    /// <item><b>Silinen kullanıcı bir daha oluşturulamıyordu.</b> Silme <c>200 ftp.user_deleted</c>
    /// dönüyor, Windows hesabı ve junction gerçekten gidiyor, ama <c>applicationHost.config</c>'de
    /// <c>Allow</c> kuralı KALIYORDU. Aynı adla yeniden oluşturma, sonraki satırdaki
    /// <c>Add-WebConfiguration</c>'da "yinelenen koleksiyon girişi" ile <c>500
    /// ftp.site_configuration_failed</c> veriyordu.</item>
    /// <item><b>Silme erişimi gerçekten iptal etmiyordu.</b> Aynı adlı bir yerel hesap başka bir
    /// yolla var olursa FTP kökündeki Read+Write yetkisi panel hiçbir şey yapmadan geri gelir;
    /// kurallar ayrıca sonsuza dek birikirdi.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Neden <c>-ErrorAction Stop</c> güvenli:</b> ölçüldü — kural ZATEN YOKKEN
    /// <c>Remove-WebConfigurationProperty</c> hata değil <b>uyarı</b> üretir ("… özelliği …
    /// dizininde bulunamadı") ve sıfırla çıkar. Yani çağrı idempotenttir; <c>Stop</c> yalnız
    /// GERÇEK arızayı yüzeye çıkarır. Eşleşme büyük/küçük harfe duyarsızdır (ölçüldü:
    /// <c>ADA_Olcum_ZZ</c> ile eklenen kayıt <c>ada_olcum_zz</c> ile kaldırıldı) — Windows yerel
    /// hesap adları da öyle olduğu için doğru davranış budur.
    /// </para>
    /// <para>
    /// <b>GERİ OKUMA ŞART:</b> "cmdlet şikâyet etmedi" ile "kural gitti" aynı şey değildir — bu
    /// hatanın tamamı zaten bu farktan doğdu. Kaldırma sonrası koleksiyon yeniden okunur ve kural
    /// hâlâ duruyorsa istisna fırlatılır, böylece sessiz başarısızlık bir daha mümkün olmaz.
    /// </para>
    /// </remarks>
    // internal: testler KOPYASINI degil BU dizeyi calistirir; kopya test etmek hicbir sey kanitlamaz.
    internal const string RemoveUserAuthorizationSnippet = """
        Remove-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $siteName -Filter 'system.ftpServer/security/authorization' -Name '.' -AtElement @{users=$userName} -ErrorAction Stop
        $adaKalanKurallar = @((Get-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $siteName -Filter 'system.ftpServer/security/authorization').Collection | Where-Object { $_.users -eq $userName })
        if ($adaKalanKurallar.Count -gt 0) { throw "FTP yetki kurali kaldirilamadi (kural hala duruyor): $userName" }
        """;

    private async Task ConfigureUserHomeAsync(string userName, string homePath, CancellationToken cancellationToken)
    {
        var userRoot = Path.Combine(dataRoot, "ftp-root", "LocalUser", userName);

        // KATI çalıştırma (RunPowerShellStrictAsync): bu betiğin ortasında patlayan bir ifade
        // eskiden çıkış koduna yansımıyordu — PowerShell'in çıkış kodu SON ifadenin sonucudur.
        // Yetki kuralı kaldırma yolu tam da böyle sessizce ölmüştü.
        var script = $$"""
            Import-Module WebAdministration -ErrorAction Stop
            $siteName = '{{EscapePowerShellLiteral(FtpSiteName)}}'
            $userName = '{{EscapePowerShellLiteral(userName)}}'
            $userRoot = '{{EscapePowerShellLiteral(userRoot)}}'
            $homePath = '{{EscapePowerShellLiteral(homePath)}}'
            {{RemoveUserRootSnippet}}
            New-Item -ItemType Junction -Path $userRoot -Target $homePath | Out-Null
            {{RemoveUserAuthorizationSnippet}}
            Add-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $siteName -Filter 'system.ftpServer/security/authorization' -Value @{accessType='Allow';users=$userName;permissions=3}
            """;

        var result = await ProcessRunner.RunPowerShellStrictAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("ftp.site_configuration_failed", 500, 500, BuildCommandDetails(result));
        }
    }

    private async Task RemoveUserAuthorizationAsync(string userName, CancellationToken cancellationToken)
    {
        var userRoot = Path.Combine(dataRoot, "ftp-root", "LocalUser", userName);

        // Site varlık denetimi try/catch İÇİNDE: katı kipte ($ErrorActionPreference='Stop') IIS
        // sağlayıcısı hiç yüklenmemişse "IIS:\..." yolu DriveNotFound ile PATLAR ve IIS FTP rolü
        // olmayan bir makinede hesap silme tümden imkânsız hâle gelirdi. Site yoksa kaldıracak
        // kural da yoktur; junction temizliği yine de yapılır.
        var script = $$"""
            Import-Module WebAdministration -ErrorAction SilentlyContinue
            $siteName = '{{EscapePowerShellLiteral(FtpSiteName)}}'
            $userName = '{{EscapePowerShellLiteral(userName)}}'
            $userRoot = '{{EscapePowerShellLiteral(userRoot)}}'
            $adaSiteVar = $false
            try { $adaSiteVar = Test-Path "IIS:\Sites\$siteName" } catch { $adaSiteVar = $false }
            if ($adaSiteVar) {
            {{RemoveUserAuthorizationSnippet}}
            }
            {{RemoveUserRootSnippet}}
            """;

        var result = await ProcessRunner.RunPowerShellStrictAsync(script, cancellationToken);
        if (!result.Success)
        {
            throw new AppException("ftp.user_delete_failed", 500, 500, BuildCommandDetails(result));
        }
    }

    private async Task<string> ResolveHomePathAsync(CreateFtpUserRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.HomePath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(request.HomePath.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(request.SiteName))
        {
            var site = await iisSiteService.GetSiteAsync(request.SiteName, cancellationToken);
            return string.IsNullOrWhiteSpace(site.ApplicationRootPath)
                ? site.PhysicalPath
                : site.ApplicationRootPath;
        }

        return Path.Combine(dataRoot, "ftp", request.UserName.Trim());
    }

    private static async Task<bool> UserExistsAsync(string userName, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync("net.exe", $"user \"{userName}\"", cancellationToken: cancellationToken);
        return result.Success;
    }

    /// <summary>
    /// Bir FTP kullanıcı adının, istenen site adına göre sahiplik durumu.
    /// </summary>
    internal enum FtpUserNameClaim
    {
        /// <summary>Ad boşta: panelde kaydı yok, Windows'ta hesabı yok.</summary>
        Serbest,

        /// <summary>Panel kaydı var ve AYNI siteye ait — yeniden oluşturma/parola sıfırlama meşru.</summary>
        KendiKaydi,

        /// <summary>Panel kaydı var ama BAŞKA bir siteye ait — çapraz kiracı devralma.</summary>
        BaskaSitenin,

        /// <summary>Windows'ta hesap var ama panelin kaydı YOK — panelin açmadığı bir hesap.</summary>
        PanelDisi
    }

    /// <summary>
    /// FTP kullanıcı adı sahiplik hükmü. SAF fonksiyon: girdileri dışarıdan alır, yan etkisi yoktur,
    /// bu yüzden gerçek Windows hesabı açmadan tam doğruluk tablosuyla test edilebilir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sıralama bilinçli: <b>önce panel kaydı</b>. Panel kaydı olan bir ad, hesabın Windows'ta o an
    /// var olup olmamasından bağımsız olarak panele aittir (kayıt varken hesap silinmiş olabilir —
    /// o zaman meşru sahibi hesabı yeniden açabilmeli). Kayıt yoksa soru Windows'a sorulur.
    /// </para>
    /// <para>
    /// Karşılaştırma <see cref="StringComparison.OrdinalIgnoreCase"/>: Windows yerel hesap adları
    /// büyük/küçük harfe duyarsızdır, yani "administrator" ile "Administrator" AYNI hesaptır.
    /// Duyarlı karşılaştırma denetimi harf değiştirerek atlatılabilir bir süse çevirirdi.
    /// </para>
    /// </remarks>
    internal static FtpUserNameClaim ClassifyUserNameClaim(
        string userName,
        string siteName,
        IEnumerable<(string UserName, string SiteName)> kayitlar,
        bool sistemdeVar)
    {
        var ad = (userName ?? string.Empty).Trim();
        var istenenSite = (siteName ?? string.Empty).Trim();

        foreach (var kayit in kayitlar)
        {
            if (!string.Equals((kayit.UserName ?? string.Empty).Trim(), ad, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return string.Equals((kayit.SiteName ?? string.Empty).Trim(), istenenSite, StringComparison.OrdinalIgnoreCase)
                ? FtpUserNameClaim.KendiKaydi
                : FtpUserNameClaim.BaskaSitenin;
        }

        return sistemdeVar ? FtpUserNameClaim.PanelDisi : FtpUserNameClaim.Serbest;
    }

    /// <summary>
    /// İddia edilen FTP kullanıcı adı başkasınınsa reddeder (K6). Gerekçe çağrı yerinde.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>GERİYE DÖNÜK UYUM (ölçülerek karar verildi):</b> mevcut kurulumlarda zaten açılmış FTP
    /// hesaplarının hepsinin <c>ftp-users.json</c>'da kaydı vardır — panel hesabı ancak kaydı da
    /// yazarak açar. Dolayısıyla bugün çalışan iki meşru akış aynen çalışmaya devam eder:
    /// yeni ad (<see cref="FtpUserNameClaim.Serbest"/>) ve kendi hesabını yeniden oluşturma
    /// (<see cref="FtpUserNameClaim.KendiKaydi"/>).
    /// </para>
    /// <para>
    /// Kırılan tek durum ÖKSÜZ hesaptır: Windows'ta duran ama panel kaydı olmayan bir hesabın adı
    /// artık talep edilemez. Bu bilinçli — öksüz hesap ile <c>Administrator</c> panelin gözünde
    /// AYNI şeydir, ikisini ayırt edecek bir veri yok. Geçiş yolu panelde zaten mevcut ve
    /// kapatılmadı: yönetici <c>DELETE /ftp/users/{ad}</c> ile öksüz hesabı siler (silme yolu
    /// bilerek kayıt şartı aramıyor), sonra ad yeniden serbesttir. Kapsamlı roller o yolu
    /// kullanamaz, çünkü <c>EnsureFtpUserScopeAsync</c> kayıtsız adı zaten 403'lüyor.
    /// </para>
    /// <para>
    /// "Ölçemedik" durumu sessizce "sorun yok"a çevrilmiyor: <c>net.exe</c> hiç çalıştırılamazsa
    /// <see cref="ProcessRunner"/> istisna fırlatır ve istek düşer; hesap yoksa çıkış kodu sıfır
    /// olmaz. Yani <c>false</c> yalnızca gerçekten ölçülmüş "hesap yok" anlamına gelir.
    /// </para>
    /// </remarks>
    private async Task EnsureUserNameIsClaimableAsync(string userName, string siteName, CancellationToken cancellationToken)
    {
        var kayitlar = await fileStore.ReadAsync(StatePath, Array.Empty<FtpUserState>(), cancellationToken);
        var hukum = ClassifyUserNameClaim(
            userName,
            siteName,
            kayitlar.Select(item => (item.UserName, item.SiteName)),
            await UserExistsAsync(userName, cancellationToken));

        switch (hukum)
        {
            // Başka bir sitenin FTP hesabı: kapsam denetimi ezilmiş kayıttan okuduğu için
            // devralma buradan geçiyordu.
            case FtpUserNameClaim.BaskaSitenin:
                throw new AppException("ftp.user_name_taken", 409, 409);

            // Panelin açmadığı bir yerel hesap (Administrator, servis hesapları, elle açılmış
            // hesaplar). Ad deseni bunları kabul ediyor ve FTP adları site adından deterministik
            // üretildiği için tahmin de edilebilir.
            case FtpUserNameClaim.PanelDisi:
                throw new AppException("ftp.user_name_reserved", 409, 409);
        }
    }

    // Parola net.exe argümanına çift tırnak içinde geçiyor. Bir '"' ya da SONDAKİ '\' tırnağı kırıp
    // ek argüman enjekte edebilir (kontrol karakterleri de). ProcessRunner shell kullanmasa da
    // (tam komut enjeksiyonu yok) bunları reddederek argüman enjeksiyonunu önlüyoruz.
    private static void EnsureCommandSafePassword(string password)
    {
        if (password.Contains('"') || password.EndsWith('\\') || password.Any(ch => ch < 0x20))
        {
            throw new AppException("ftp.invalid_password", 400, 400);
        }
    }

    private static string NormalizeUserName(string value)
    {
        var normalized = value.Trim();
        if (!UserNamePattern().IsMatch(normalized))
        {
            throw new AppException("ftp.invalid_user_name", 400, 400);
        }

        // FTP kullanıcısı Windows yerel hesabıdır; ad EN FAZLA 20 karakter olabilir (net user limiti).
        if (normalized.Length > 20)
        {
            throw new AppException("ftp.user_name_too_long", 400, 400);
        }

        return normalized;
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");

    private static string BuildCommandDetails(CommandResult result)
    {
        var details = $"""
            ExitCode={result.ExitCode}
            StandardOutput:
            {result.StandardOutput}
            StandardError:
            {result.StandardError}
            """;

        const int maxLength = 8000;
        return details.Length <= maxLength
            ? details
            : details[..maxLength] + "\r\n... (diagnostic output truncated)";
    }

    private sealed class FtpUserState
    {
        public string UserName { get; set; } = string.Empty;

        public string SiteName { get; set; } = string.Empty;

        public string HomePath { get; set; } = string.Empty;

        public bool IsEnabled { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9._-]{1,31}$", RegexOptions.Compiled)]
    private static partial Regex UserNamePattern();
}
