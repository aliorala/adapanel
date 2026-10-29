using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Databases;
using AdaPanel.Core.Dtos.Migration;
using AdaPanel.Core.Dtos.Sites;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Files;
using Microsoft.Extensions.Logging;

namespace AdaPanel.Infrastructure.Migration;

/// <summary>
/// Site göçü orkestratörü (Plesk/cPanel/ham sunucu → AdaPanel): mevcut, test edilmiş parçaları zincirler —
/// site sağlama (ISiteProvisioningService), dosya çekme (IFileMigrationService), DB transfer (IDatabaseService).
/// Site oluşturma sonraki yazmaların önkoşuludur; diğer hatalar kısmi sonuç olarak raporlanır. İptal zinciri durdurur.
/// </summary>
public sealed class SiteMigrationService(
    ISiteProvisioningService siteProvisioningService,
    IFileMigrationService fileMigrationService,
    IDatabaseService databaseService,
    IIisSiteService iisSiteService,
    ILogger<SiteMigrationService> logger,
    // İSTEĞE BAĞLI: .user.ini taramasının ÖGE tavanı. Üretimde HİÇ verilmez; yalnızca testin bütçe
    // korumasını GERÇEKTEN doldurabilmesi için var — üretim değeriyle (200.000) bunu kanıtlamak,
    // testte iki yüz bin dosya yaratmak demekti. Aynı seam kota ve dosya boyutu yollarında da
    // açıldı (bkz. FileService.CalculateSizeAsync'in internal aşırı yüklemesi) ve gerekçesi orada
    // yazılı. Yeni bir HTTP yüzeyi AÇMAZ: yalnız DI kurulumu bu parametreyi görebilir.
    int? userIniScanEntryBudget = null,
    // İSTEĞE BAĞLI: "iç ağ hedeflerine izin ver" açık kapısı. Verilmemişse KAPALI sayılır.
    Microsoft.Extensions.Options.IOptions<Options.PanelRuntimeOptions>? runtimeOptions = null,
    // İSTEĞE BAĞLI: adres çözümleyici. Testte sahte — birim testi gerçek DNS'e çıkmamalı.
    Support.OutboundAddressGuard.OutboundHostResolver? outboundResolver = null) : ISiteMigrationService
{
    internal Func<HttpClient>? DiscoveryClientFactory { get; init; }

    /// <summary>
    /// Plesk/cPanel KEŞİF isteklerinin tek istemcisi: bağlantı ANINDA adres denetimi yapar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bu iki uç DENETİMSİZDİ (ölçüldü).</b> <c>request.Host</c> + <c>request.Port</c> HTTP istek
    /// gövdesinden gelir; kod kendi <c>HttpClientHandler</c>'ını kuruyordu, yani ne
    /// <c>OutboundAddressGuard</c>'dan geçiyordu ne de bir yönlendirme sınırı vardı
    /// (<c>AllowAutoRedirect</c> .NET varsayılanı <c>true</c>). Yedek hedefi, cron adresi ve göç
    /// FTP/SFTP konağı 0.8.7'de kapatılırken bu yol atlanmıştı — üstelik burada istek KULLANICININ
    /// verdiği kimlik bilgisini (<c>X-API-Key</c> / Basic / WHM jetonu) taşır, yani hedefe giden bir
    /// kimlik sızıntısı yüzeyi de vardı.
    /// </para>
    /// <para>
    /// <b>SERTİFİKA DOĞRULAMASI ARTIK AÇIK.</b> Burada eskiden <c>verifyTlsCertificate: false</c>
    /// vardı: doğrulama tümden kapalıydı ve araya giren biri sahte sertifikayla bağlantıyı
    /// karşılayıp yukarıda sayılan kimlik bilgilerini OKUYABİLİRDİ. Gevşetme kasıtlıydı (Plesk/WHM
    /// neredeyse her zaman kendi imzalı sertifika taşır) ama bedeli ölçülmemişti. Yerine
    /// <see cref="RemoteCertificateTrustGate"/> geçti: varsayılan doğrular, reddederken sebebi ve
    /// parmak izini söyler, istisnayı yalnız yöneticinin sabitlediği parmak izine açar.
    /// </para>
    /// <para>
    /// Adres denetimi TLS kararından bağımsızdır: sertifika kararı nereye bağlanılacağını değiştirmez.
    /// </para>
    /// </remarks>
    private HttpClient CreateDiscoveryHttpClient(RemoteCertificateTrustGate certificateTrust)
    {
        if (DiscoveryClientFactory is not null) return DiscoveryClientFactory();
        var handler = Support.OutboundConnectGuard.CreateHandler(
            () => Support.OutboundTargetPolicy.Connecting(runtimeOptions?.Value.AllowInternalOutboundTargets ?? false),
            outboundResolver,
            onBlocked: (host, blocked) => logger.LogWarning(
                "Goc kesif baglantisi REDDEDILDI: {Host} adi {Adres} adresine cozuluyor (ic ag/geri dongu/bulut meta verisi).",
                host,
                blocked),
            certificateValidator: certificateTrust.AsCallback());

        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(25) };
    }

    /// <summary>
    /// Reddedilen uzak kimliği KULLANICININ GÖREBİLECEĞİ bir hataya çevirir.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neden ayrı bir dal:</b> el sıkışma reddedildiğinde .NET sıradan bir
    /// <see cref="HttpRequestException"/> atar ve mesajı "bağlantı kapatıldı" gibi görünür. O metin
    /// yöneticiye "sunucu erişilemiyor" der — yani yanlış şeyi düzeltmeye çalışır. Kapının
    /// tuttuğu kayıt, gerçekte NE olduğunu (sebep + parmak izi) söyleyen tek şeydir.
    /// </para>
    /// <para>
    /// Sabitleme İŞE YARADIĞINDA da iz bırakılır: bir API anahtarı doğrulanmamış bir kimliğe
    /// gönderildiyse bunun sunucuda bir kaydı olmalıdır (denetim kaydını denetleyici yazar).
    /// </para>
    /// </remarks>
    private void GuvenSonucunuIsle(RemoteCertificateTrustGate certificateTrust, string host, int port)
    {
        if (certificateTrust.AcceptedByPin is { } kabulEdilen)
        {
            logger.LogWarning(
                "Goc kesfi: {Host}:{Port} sunucusunun DOGRULANAMAYAN sertifikasi yoneticinin sabitledigi parmak iziyle KABUL EDILDI ({ParmakIzi}).",
                host,
                port,
                kabulEdilen);
        }
    }

    /// <summary>
    /// Keşif konağını BAĞLANMADAN önce denetler.
    /// </summary>
    /// <remarks>
    /// Bağlantı-anı denetimi zaten var; bu ön kapı hata kalitesi içindir: kullanıcı "502 bağlantı
    /// kurulamadı" değil, adresin neden reddedildiğini ve açık kapının nerede olduğunu söyleyen
    /// <c>migration.host_forbidden</c> hatasını görür (aynı metin dosya göçünde de kullanılıyor).
    /// Çözülemeyen ad REDDEDİLİR: çözülemeyen bir konağa zaten bağlanılamaz.
    /// </remarks>
    private async Task EnsureDiscoveryHostAllowedAsync(string host, CancellationToken cancellationToken)
    {
        var engellenen = await Support.OutboundAddressGuard.FindBlockedHostAsync(
            host,
            outboundResolver ?? Support.OutboundAddressGuard.SystemResolver,
            Support.OutboundTargetPolicy.Connecting(runtimeOptions?.Value.AllowInternalOutboundTargets ?? false),
            cancellationToken);

        if (engellenen is not null)
        {
            throw new AppException("migration.host_forbidden", 403, 403, engellenen);
        }
    }

    public async Task<MigrateSiteResultDto> MigrateSiteAsync(
        MigrateSiteRequest request,
        Guid? actorUserId,
        string? ipAddress,
        Func<MigrationProgressEvent, CancellationToken, Task>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<MigrationStepDto>();

        async Task ReportAsync(MigrationProgressEvent progressEvent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (onProgress is not null)
            {
                await onProgress(progressEvent, cancellationToken);
            }
        }

        // Tek bir adımı çalıştırır: önce "start" raporlar → dener → sonucu hem listeye ekler hem "result" raporlar.
        // Adımlar bağımsız: istisna yutulur (göç durmaz, kısmi başarı olabilir). Başarı/başarısızlık değerini döner
        // ki çağıran zincirleme kararı verebilsin (ör. userini yalnızca dosya çekme başarılıysa).
        async Task<bool> ExecuteStepAsync(string stepId, string successMessage, Func<Task<string?>> action)
        {
            await ReportAsync(new MigrationProgressEvent { Phase = "start", Step = stepId });
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var successDetail = await action();
                cancellationToken.ThrowIfCancellationRequested();
                steps.Add(new MigrationStepDto { Step = stepId, Success = true, Message = successMessage, Detail = successDetail });
                await ReportAsync(new MigrationProgressEvent { Phase = "result", Step = stepId, Success = true, Message = successMessage, Detail = successDetail });
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Göç: {Step} adımı başarısız {Site}", stepId, request.SiteName);
                var (message, detail) = DescribeError(exception);
                steps.Add(new MigrationStepDto { Step = stepId, Success = false, Message = message, Detail = detail });
                await ReportAsync(new MigrationProgressEvent { Phase = "result", Step = stepId, Success = false, Message = message, Detail = detail });
                return false;
            }
        }

        // Önce çalışacak adımları bildir (istemci "sırada" satırlarını çizsin). userini dinamiktir (yalnızca dosya
        // adımı başarılıysa) → plana koymuyoruz; istemci o satırı canlı ekler.
        var plan = new List<string>();
        if (request.CreateSite) plan.Add("site");
        if (request.PullFiles) plan.Add("files");
        if (request.TransferDatabase) plan.Add("database");
        await ReportAsync(new MigrationProgressEvent { Phase = "plan", Steps = plan });

        var siteReady = true;
        if (request.CreateSite)
        {
            siteReady = await ExecuteStepAsync("site", "migration.step.site_created", async () =>
            {
                await siteProvisioningService.CreateAndProvisionAsync(
                    new CreateSiteRequest
                    {
                        Name = request.SiteName,
                        PhysicalPath = request.PhysicalPath,
                        HostName = request.SiteName,
                        IpAddress = "*",
                        Port = 80,
                        Protocol = "http",
                        RuntimeProfile = "php",
                        AutoStart = true,
                        ProvisionDefaultServices = false
                    },
                    actorUserId,
                    ipAddress,
                    cancellationToken);
                return null;
            });
        }

        if (!siteReady)
        {
            foreach (var dependent in plan.Where(step => step != "site"))
            {
                const string message = "migration.step.site_required";
                steps.Add(new MigrationStepDto { Step = dependent, Success = false, Message = message });
                await ReportAsync(new MigrationProgressEvent { Phase = "result", Step = dependent, Success = false, Message = message });
            }
        }

        if (request.PullFiles && siteReady)
        {
            var filesPulled = await ExecuteStepAsync("files", "migration.step.files_pulled", async () =>
            {
                var pull = await fileMigrationService.PullAsync(
                    new RemoteFileMigrationRequest
                    {
                        SiteName = request.SiteName,
                        Protocol = request.FileProtocol,
                        Host = request.FileHost,
                        Port = request.FilePort,
                        UserName = request.FileUserName,
                        Password = request.FilePassword,
                        RemotePath = request.RemotePath,
                        // Yöneticinin bu göç için verdiği açık onay AŞAĞIYA da geçer. Geçmeseydi
                        // keşif doğrulanmış olur ama dosyalar hâlâ doğrulanmamış bir uçtan
                        // çekilirdi — yani parolayı taşıyan asıl bağlantı korumasız kalırdı.
                        AcceptHostFingerprint = request.AcceptFileHostFingerprint
                    },
                    cancellationToken);

                // SONUÇ ARTIK ATILMIYOR. Burada `return null` vardı: dosya çekme servisi kaç dosya
                // ve kaç bayt indirdiğini hesaplıyor, adım da bir "Detay" alanı taşıyor, ama sayı
                // arayüze HİÇ ulaşmıyordu. Kullanıcı "Dosyalar çekildi" yazısına bakıp göçün
                // gerçekten olup olmadığını ayırt edemiyordu.
                return DescribeFilePull(pull);
            });

            // Dosyalar çekildiyse göçen .user.ini'leri temizle: eski Plesk/vhost yolları + bozuk open_basedir
            // (kaynaktan gelen) site'yi çalışmaz hale getirir → düzelt/temizle. Yalnızca dosya çekme başarılıysa.
            if (filesPulled)
            {
                // DETAY ARTIK ATILMIYOR: temizlik kaç dosya buldu/temizledi ve tarama YARIDA KALDI
                // mı — hepsi adımın Detay alanına girer. Eksik kalmış bir tarama, göçün eski sunucu
                // yollarını taşımaya devam etmesi demektir ve bunu kullanıcı görmeli.
                await ExecuteStepAsync(
                    "userini",
                    "migration.step.userini_sanitized",
                    () => SanitizeUserIniFilesAsync(request.SiteName, cancellationToken));
            }
        }

        if (request.TransferDatabase && siteReady)
        {
            await ExecuteStepAsync("database", "migration.step.db_transferred", async () =>
            {
                var target = string.IsNullOrWhiteSpace(request.TargetDatabase) ? request.SourceDatabase : request.TargetDatabase;

                // Taşınan uygulamanın bağlanacağı hedef DB kullanıcısı: verilmişse aynen (uygulamanın config'iyle
                // eşleşsin → bağlantı değişmeden çalışır), yoksa site adından türetilir. Parola yoksa güçlü üretilir.
                var dbUser = string.IsNullOrWhiteSpace(request.TargetDatabaseUser)
                    ? DeriveDbUser(request.SiteName, target)
                    : request.TargetDatabaseUser.Trim();
                var passwordGenerated = string.IsNullOrWhiteSpace(request.TargetDatabasePassword);
                var dbPassword = passwordGenerated ? GenerateDbPassword() : request.TargetDatabasePassword;

                // 1) Hedef DB'yi oluştur + siteye bağla (zaten varsa yok say → transfer yine de geri yükler).
                try
                {
                    await databaseService.CreateDatabaseAsync(
                        new CreateDatabaseRequest { Engine = request.DatabaseEngine, Name = target, SiteName = request.SiteName },
                        cancellationToken);
                }
                catch (AppException createException) when (createException.MessageKey == "database.database_already_exists")
                {
                    logger.LogInformation(createException, "Göç: hedef DB zaten var olabilir ({Db}); devam ediyor.", target);
                }

                // 2) A successful creation proves the submitted/generated password and grant.
                // An existing user has neither proof; do not guess its password or reset it.
                cancellationToken.ThrowIfCancellationRequested();
                await databaseService.CreateUserAsync(
                    new CreateDatabaseUserRequest
                    {
                        Engine = request.DatabaseEngine,
                        UserName = dbUser,
                        Password = dbPassword,
                        DatabaseName = target
                    },
                    cancellationToken);

                // 3) Veriyi aktar (mysqldump → mysql).
                cancellationToken.ThrowIfCancellationRequested();
                await databaseService.TransferDatabaseAsync(
                    new DatabaseTransferRequest
                    {
                        Engine = request.DatabaseEngine,
                        SourceHost = request.DbHost,
                        SourcePort = request.DbPort,
                        SourceUserName = request.DbUserName,
                        SourcePassword = request.DbPassword,
                        SourceDatabase = request.SourceDatabase,
                        // Şifreleme tercihi göç formundan GEÇİRİLİR. Aktarılmazsa kullanıcının
                        // "şifrelemesiz" seçimi burada sessizce düşer ve göç, sebebi görünmeyen bir
                        // TLS hatasıyla ölürdü (ya da tersi: seçim yokmuş gibi davranılırdı).
                        SourceTlsMode = request.DbTlsMode,
                        TargetDatabase = target
                    },
                    cancellationToken);

                // Oluşturulan erişim bilgisini adım detayında göster → kullanıcı uygulama config'ini (wp-config.php/.env)
                // güncelleyebilsin. Parola kullanıcı tarafından verildiyse ekranda tekrar göstermeyiz (zaten biliyor).
                return passwordGenerated
                    ? $"DB user: {dbUser}  ·  password: {dbPassword}"
                    : $"DB user: {dbUser}";
            });
        }

        var result = new MigrateSiteResultDto
        {
            SiteName = request.SiteName,
            Steps = steps,
            OverallSuccess = steps.Count > 0 && steps.TrueForAll(step => step.Success)
        };

        await ReportAsync(new MigrationProgressEvent
        {
            Phase = "done",
            OverallSuccess = result.OverallSuccess,
            Message = result.OverallSuccess ? "migration.site.success" : "migration.site.partial"
        });

        return result;
    }

    /// <summary>
    /// Dosya çekme adımının ÖLÇÜLEBİLİR sonucu: kaç dosya, ne kadar veri, nereye. Kullanıcı bu
    /// satıra bakarak "gerçekten geldi mi" sorusunu yanıtlayabilir.
    /// </summary>
    internal static string DescribeFilePull(RemoteFileMigrationResult result)
        => $"{result.FilesDownloaded} dosya · {FormatBytes(result.BytesDownloaded)} → {result.TargetPath}";

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        string[] birimler = ["KB", "MB", "GB", "TB"];
        double deger = bytes;
        var index = -1;
        while (deger >= 1024 && index < birimler.Length - 1)
        {
            deger /= 1024;
            index++;
        }

        // Sabit kültür: sunucunun yerel ayarı ondalık ayırıcıyı değiştirip aynı sayıyı farklı
        // gösterirse (1.5 GB / 1,5 GB) hem kullanıcı hem testler için kaygan zemin olur.
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.#} {1}", deger, birimler[index]);
    }

    // Plesk REST keşfi: GET https://host:8443/api/v2/domains (X-API-Key) → domain listesi.
    // SERTİFİKA DOĞRULANIR (bkz. CreateDiscoveryHttpClient). Plesk genelde kendi imzalı sertifika taşır;
    // o durumda istek migration.tls.untrusted ile REDDEDİLİR ve yöneticiye parmak izi gösterilir —
    // yönetici parmak izini teyit edip AcceptCertificateFingerprint ile tekrar dener.
    // Best-effort: bağlantı/şekil hatası → net AppException; manuel form çalışmaya devam eder.
    public async Task<PleskDiscoverResultDto> DiscoverPleskAsync(PleskDiscoverRequest request, CancellationToken cancellationToken = default)
    {
        var host = request.Host.Trim().TrimEnd('/');
        var port = request.Port > 0 ? request.Port : 8443;

        await EnsureDiscoveryHostAllowedAsync(host, cancellationToken);

        var certificateTrust = new RemoteCertificateTrustGate(request.AcceptCertificateFingerprint);
        using var http = CreateDiscoveryHttpClient(certificateTrust);
        // Kimlik: gizli API anahtarı (X-API-Key) ya da yönetici kullanıcı adı+parola (Basic auth). İkisi de yoksa net hata.
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", request.ApiKey.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(request.UserName) && !string.IsNullOrWhiteSpace(request.Password))
        {
            var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{request.UserName.Trim()}:{request.Password}"));
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Basic {basic}");
        }
        else
        {
            throw new AppException("migration.plesk.auth_required", 400, 400);
        }

        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync($"https://{host}:{port}/api/v2/domains", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (Support.OutboundBlockedException.Cikar(exception) is { } blocked)
        {
            // YÖNLENDİRME/REBINDING DALI: konak ön kapıda temizdi ama zincir iç ağa saptı.
            // Tür DEĞİL zincir yakalanır: .NET geri çağrının istisnasını sarar (ölçüldü).
            // "Bağlanılamadı" demek yanıltıcı olurdu; kullanıcı adresini düzeltemez, çünkü adres
            // doğruydu — reddedilen şey gidilen yerdi.
            throw new AppException("migration.host_forbidden", 403, 403, blocked.BlockedTarget);
        }
        catch (Exception exception) when (certificateTrust.Failure is { } reddedilen)
        {
            // GÜVEN DALI, "bağlanamadım" dalından ÖNCE gelir: el sıkışma reddi de bir bağlantı
            // hatası olarak görünür ve aşağıdaki genel dal onu "Plesk'e ulaşılamadı" diye
            // etiketlerdi — yönetici host/port ile uğraşır, gerçek sebebi hiç öğrenmezdi.
            logger.LogWarning(
                exception,
                "Plesk kesfi REDDEDILDI {Host}:{Port} — sunucu kimligi dogrulanamadi ({Sebep}, {ParmakIzi}).",
                host,
                port,
                string.Join(",", reddedilen.Reasons),
                reddedilen.Fingerprint);
            throw new AppException(reddedilen.MessageKey, 502, 502, reddedilen.ToDetails());
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Plesk keşif bağlantısı başarısız {Host}:{Port}", host, port);
            throw new AppException("migration.plesk.connect_failed", 502, 502, exception.Message);
        }

        GuvenSonucunuIsle(certificateTrust, host, port);

        if (!response.IsSuccessStatusCode)
        {
            // Gerçek nedeni yüzeye çıkar: HTTP kodu + Plesk'in JSON hata gövdesi (Details'te). Yaygın kodları sınıflandır:
            // 401/403 = kimlik reddi (kullanıcı/parola yanlış ya da IP/erişim kısıtı), 404 = bu sürümde REST API v2 yolu yok.
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var snippet = errorBody.Length > 300 ? errorBody[..300] : errorBody;
            var status = (int)response.StatusCode;
            var key = status switch
            {
                401 or 403 => "migration.plesk.auth_failed",
                404 => "migration.plesk.not_found",
                _ => "migration.plesk.api_error"
            };
            throw new AppException(key, 502, 502, $"HTTP {status} {response.ReasonPhrase}\r\n{snippet}".Trim());
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var domains = new List<PleskDomainDto>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object ||
                        !element.TryGetProperty("name", out var nameElement) ||
                        nameElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    domains.Add(new PleskDomainDto
                    {
                        Name = nameElement.GetString() ?? string.Empty,
                        HostingType = element.TryGetProperty("hosting_type", out var hostingType) && hostingType.ValueKind == JsonValueKind.String
                            ? hostingType.GetString() ?? string.Empty
                            : string.Empty
                    });
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Plesk keşif yanıtı ayrıştırılamadı.");
            throw new AppException("migration.plesk.parse_failed", 502, 502, exception.Message);
        }

        return new PleskDiscoverResultDto { Domains = domains };
    }

    // cPanel/WHM keşfi: GET https://host:2087/json-api/listaccts?api.version=1 (Authorization: whm user:TOKEN) →
    // { data: { acct: [ { domain, user } ] } }. SERTİFİKA DOĞRULANIR; WHM genelde kendi imzalı sertifika
    // taşıdığı için yönetici parmak izini teyit edip AcceptCertificateFingerprint ile kabul eder
    // (gerekçe Plesk dalındaki ikiziyle aynı). Plesk keşfiyle aynı sonuç şekli döner.
    public async Task<PleskDiscoverResultDto> DiscoverCpanelAsync(CpanelDiscoverRequest request, CancellationToken cancellationToken = default)
    {
        var host = request.Host.Trim().TrimEnd('/');
        var port = request.Port > 0 ? request.Port : 2087;
        var user = string.IsNullOrWhiteSpace(request.UserName) ? "root" : request.UserName.Trim();

        await EnsureDiscoveryHostAllowedAsync(host, cancellationToken);

        var certificateTrust = new RemoteCertificateTrustGate(request.AcceptCertificateFingerprint);
        using var http = CreateDiscoveryHttpClient(certificateTrust);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"whm {user}:{request.ApiToken}");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync($"https://{host}:{port}/json-api/listaccts?api.version=1", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (Support.OutboundBlockedException.Cikar(exception) is { } blocked)
        {
            // Gerekçe Plesk dalındaki ikiziyle aynı.
            throw new AppException("migration.host_forbidden", 403, 403, blocked.BlockedTarget);
        }
        catch (Exception exception) when (certificateTrust.Failure is { } reddedilen)
        {
            // Gerekçe Plesk dalındaki ikiziyle aynı: güven reddi "ulaşılamadı" diye etiketlenemez.
            logger.LogWarning(
                exception,
                "cPanel/WHM kesfi REDDEDILDI {Host}:{Port} — sunucu kimligi dogrulanamadi ({Sebep}, {ParmakIzi}).",
                host,
                port,
                string.Join(",", reddedilen.Reasons),
                reddedilen.Fingerprint);
            throw new AppException(reddedilen.MessageKey, 502, 502, reddedilen.ToDetails());
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "cPanel/WHM keşif bağlantısı başarısız {Host}:{Port}", host, port);
            throw new AppException("migration.cpanel.connect_failed", 502, 502, exception.Message);
        }

        GuvenSonucunuIsle(certificateTrust, host, port);

        if (!response.IsSuccessStatusCode)
        {
            throw new AppException("migration.cpanel.api_error", 502, 502, $"HTTP {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var domains = new List<PleskDomainDto>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("acct", out var accounts) &&
                accounts.ValueKind == JsonValueKind.Array)
            {
                foreach (var account in accounts.EnumerateArray())
                {
                    if (account.ValueKind != JsonValueKind.Object ||
                        !account.TryGetProperty("domain", out var nameElement) ||
                        nameElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var name = nameElement.GetString();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    domains.Add(new PleskDomainDto
                    {
                        Name = name,
                        // Kendi alanı: `HostingType` DEĞİL. Eskiden kullanıcı adı `HostingType`e
                        // yazılıyordu ve arayüz alanı etiketsiz basıyordu; ekranda "virtual" ile
                        // "ali_shop" aynı yerde, aynı adla görünüyordu (bkz. PleskDomainDto).
                        AccountUser = account.TryGetProperty("user", out var userElement) && userElement.ValueKind == JsonValueKind.String
                            ? userElement.GetString() ?? string.Empty
                            : string.Empty
                    });
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "cPanel/WHM keşif yanıtı ayrıştırılamadı.");
            throw new AppException("migration.cpanel.parse_failed", 502, 502, exception.Message);
        }

        return new PleskDiscoverResultDto { Domains = domains };
    }

    // Göç ÖNCESİ ucuz ön-kontroller: site adı uygunluğu (oluşturulacaksa çakışma yok / hedefse var) + dosya/DB
    // kaynağı TCP erişilebilirliği (kimlik doğrulamaz; "host:port'a bağlanılıyor mu" = en sık göç tıkanması).
    public async Task<MigrationValidationResultDto> ValidateMigrationAsync(MigrateSiteRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checks = new List<MigrationCheckDto>();

        if (string.IsNullOrWhiteSpace(request.SiteName))
        {
            checks.Add(Check("site_name", false, "migration.validate.site_name_empty"));
        }
        else
        {
            var exists = await SiteExistsAsync(request.SiteName, cancellationToken);
            checks.Add(request.CreateSite
                ? Check("site_name", !exists, exists ? "migration.validate.site_exists" : "migration.validate.site_name_ok")
                : Check("site_name", exists, exists ? "migration.validate.site_found" : "migration.validate.site_missing"));
        }

        if (request.PullFiles)
        {
            var filePort = request.FilePort > 0 ? request.FilePort : DefaultFilePort(request.FileProtocol);
            checks.Add(await UlasimKontroluAsync(
                "file_source",
                request.FileHost,
                filePort,
                "migration.validate.file_reachable",
                "migration.validate.file_unreachable",
                cancellationToken));
        }

        if (request.TransferDatabase)
        {
            var dbPort = request.DbPort > 0 ? request.DbPort : 3306;
            checks.Add(await UlasimKontroluAsync(
                "db_source",
                request.DbHost,
                dbPort,
                "migration.validate.db_reachable",
                "migration.validate.db_unreachable",
                cancellationToken));
        }

        if (checks.Count == 0)
        {
            checks.Add(Check("nothing", false, "migration.validate.nothing_selected"));
        }

        return new MigrationValidationResultDto
        {
            Checks = checks,
            AllPassed = checks.TrueForAll(check => check.Ok)
        };
    }

    private async Task<bool> SiteExistsAsync(string siteName, CancellationToken cancellationToken)
    {
        try
        {
            _ = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
            return true;
        }
        catch (AppException exception) when (exception.MessageKey == "sites.site_not_found")
        {
            return false;
        }
    }

    private static int DefaultFilePort(string protocol)
        => string.Equals(protocol?.Trim(), "sftp", StringComparison.OrdinalIgnoreCase) ? 22 : 21;

    /// <summary>
    /// Göç ÖN KONTROLÜNDEKİ "ulaşılabiliyor mu" sorusu — ama önce ADRES kapısından geçer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Yüzey neden var (ölçüldü):</b> <see cref="CanConnectAsync"/> gövdeden gelen
    /// <c>FileHost</c>/<c>DbHost</c> + PORT ikilisine TCP açıyor ve sonucu kullanıcıya
    /// "ulaşılabildi / ulaşılamadı" diye DÖNÜYORDU. Bu, panelin ağından çalışan bir PORT
    /// TARAYICISIDIR: <c>127.0.0.1:3306</c>, <c>10.0.0.5:445</c> gibi hedefler tek tek denenip
    /// ayırt edilebiliyordu. Göçün asıl yolları (<c>FileMigrationService</c>) bu kapıdan zaten
    /// geçiyordu; ön kontrol atlanmıştı — yani kapı kapalı, yanındaki pencere açıktı.
    /// </para>
    /// <para>
    /// Yasak durumu "ulaşılamadı" ile AYNI mesajı vermez: kullanıcı adresini boşuna düzeltmesin,
    /// açık kapının nerede olduğunu görsün diye ayrı bir metin döner.
    /// </para>
    /// </remarks>
    private async Task<MigrationCheckDto> UlasimKontroluAsync(
        string ad,
        string host,
        int port,
        string basariliMetin,
        string basarisizMetin,
        CancellationToken cancellationToken)
    {
        var engellenen = await Support.OutboundAddressGuard.FindBlockedHostAsync(
            host,
            outboundResolver ?? Support.OutboundAddressGuard.SystemResolver,
            Support.OutboundTargetPolicy.Connecting(runtimeOptions?.Value.AllowInternalOutboundTargets ?? false),
            cancellationToken);

        if (engellenen is not null)
        {
            return Check(ad, false, "migration.validate.host_forbidden");
        }

        var ulasildi = await CanConnectAsync(host, port, cancellationToken);
        return Check(ad, ulasildi, ulasildi ? basariliMetin : basarisizMetin);
    }

    private static async Task<bool> CanConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(host) || port is <= 0 or > 65535)
        {
            return false;
        }

        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            await client.ConnectAsync(host.Trim(), port, timeout.Token);
            return client.Connected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return false;
        }
    }

    private static MigrationCheckDto Check(string name, bool ok, string message)
        => new() { Name = name, Ok = ok, Message = message };

    /// <summary>Aranan dosya adı. Ağaçtaki her ögeyle karşılaştırılır.</summary>
    private const string UserIniDosyaAdi = ".user.ini";

    /// <summary>
    /// <c>.user.ini</c> aramasının ÖGE üst sınırı (dosya + klasör).
    /// </summary>
    /// <remarks>
    /// Göç, kullanıcının önünde ilerleyen bir istektir; tavan, dosya yöneticisininkiyle (200.000)
    /// bilerek aynı tutuldu — ikisi de "kullanıcı ekranın başında bekliyor" yolunda çalışır.
    /// </remarks>
    private const int UserIniOgeButcesi = 200_000;

    /// <summary>Aynı aramanın SÜRE üst sınırı.</summary>
    private const int UserIniSureButcesiMs = 60_000;

    /// <summary>
    /// Tek turda yeniden yazılacak en fazla <c>.user.ini</c> sayısı.
    /// </summary>
    /// <remarks>
    /// Eskiden bu sayı <c>.Take(200)</c> ile SONUCA uygulanıyordu ve hiçbir şeyi korumuyordu:
    /// eşleşme 200'den azken (normal durum) bütün ağaç yine gezilirdi. Bugün YÜRÜYÜŞÜ bütçe
    /// sınırlar; bu sabit yalnızca YAZMA işini sınırlar ve aşıldığında SÖYLENİR.
    /// </remarks>
    private const int UserIniYazmaUstSiniri = 200;

    // Göçen .user.ini dosyalarını temizler: open_basedir'i yeni site köküne sıfırlar; mutlak ama bu makinede
    // OLMAYAN yolları (eski Plesk/vhost: session.save_path, upload_tmp_dir, error_log ...) yorum satırına alır →
    // PHP varsayılanlarına döner. Değişiklik yoksa dosyaya dokunmaz. Dosya başına best-effort.
    //
    // AĞACI KENDİ BAŞINA GEZMEZ. Burada `EnumerateFiles(root, ".user.ini", ...AllDirectories)`
    // vardı ve kök, IIS sitesinin fiziksel yolu — yani KİRACININ YAZABİLDİĞİ ağaç. O çağrı bağlantıları
    // (junction/symlink) TAKİP EDER: kiracının kendi site kökünü gösteren tek bir kavşağı, göç isteğinin
    // hiç dönmemesi için yeterliydi. Sondaki `.Take(200)` bunu ENGELLEMİYORDU — o SONUCU sınırlar,
    // yürüyüşü değil; 200'den az eşleşme olduğunda (yani hemen her sitede) ağacın tamamı gezilirdi.
    // Aynı hata sınıfı kota yolunda ölçülüp SafeDirectoryWalk ile TEK yürüyüşe indirildi; burada
    // ikinci bir güvenli yürüyüş yazmak, aynı yamayı iki yerde ayrı ayrı sürdürmek olurdu.
    //
    // Eski `catch { TopDirectoryOnly }` yedeği de kalktı ve bilerek: o, ağacın gezilemediği durumda
    // SESSİZCE yalnız kök klasöre bakıp "temizlendi" diyordu. Ortak yürüyüş erişim reddinde istisna
    // atmaz — eksikliği sonuçta bildirir, biz de aşağıda kullanıcıya söyleriz.
    //
    // DÖNEN DEĞER artık adımın "Detay" alanıdır (eskiden int'ti ve çağıran onu ATIYORDU): tarama
    // eksik kaldıysa göç EKSİK YAPILANDIRMA taşımış olur ve bunu ancak site çalışmayınca öğrenmek
    // en pahalı öğrenme yoludur.
    private async Task<string?> SanitizeUserIniFilesAsync(string siteName, CancellationToken cancellationToken)
    {
        var site = await iisSiteService.GetSiteAsync(siteName, cancellationToken);
        var root = site.PhysicalPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return null;
        }

        var tempDir = Path.GetTempPath().TrimEnd('\\', '/');
        var sanitized = 0;

        var files = new List<string>();
        var bulunan = 0;
        var yuruyus = SafeDirectoryWalk.Yuru(
            root,
            new SafeDirectoryWalkBudget(userIniScanEntryBudget ?? UserIniOgeButcesi, UserIniSureButcesiMs),
            oge =>
            {
                // Ad karşılaştırması BÜYÜK/KÜÇÜK HARF AYIRMAZ: NTFS de ayırmaz, dolayısıyla göçten
                // ".USER.INI" olarak gelen bir dosya PHP tarafından yine okunur ve temizlenmemesi
                // sessiz bir eksik olurdu.
                if (oge is not FileInfo dosya ||
                    !string.Equals(dosya.Name, UserIniDosyaAdi, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                bulunan++;
                if (files.Count < UserIniYazmaUstSiniri)
                {
                    files.Add(dosya.FullName);
                }
            },
            null,
            cancellationToken);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lines = await File.ReadAllLinesAsync(file, cancellationToken);
            var changed = false;
            for (var index = 0; index < lines.Length; index++)
            {
                var rewritten = SanitizeUserIniLine(lines[index], root, tempDir);
                if (rewritten != lines[index])
                {
                    lines[index] = rewritten;
                    changed = true;
                }
            }

            if (changed)
            {
                await File.WriteAllLinesAsync(file, lines, cancellationToken);
                sanitized++;
            }
        }

        return AciklaUserIniTaramasi(sanitized, bulunan, yuruyus);
    }

    /// <summary>
    /// <c>.user.ini</c> temizliğinin adım detayı. <b>Kesilmiş tarama TAM tarama gibi anlatılmaz.</b>
    /// </summary>
    /// <remarks>
    /// KARAR: eksik tarama göçü DÜŞÜRMEZ (istisna atmaz), çünkü dosyalar ve veritabanı zaten
    /// taşınmıştır — o noktada göçü hataya çevirmek, kullanıcıya "bir şey olmadı" izlenimi verip
    /// aslında taşınmış bir siteyi bırakmak olurdu. Ama eksiklik SÖYLENİR: temizlenmemiş bir
    /// <c>.user.ini</c> içindeki eski <c>open_basedir</c>, siteyi 500 ile düşüren ve sebebi hiçbir
    /// yerde yazmayan bir arızadır. Kullanıcı nereye bakacağını bilmeli.
    /// </remarks>
    private static string AciklaUserIniTaramasi(int temizlenen, int bulunan, SafeDirectoryWalkResult yuruyus)
    {
        var metin = $"{bulunan} adet {UserIniDosyaAdi} bulundu, {temizlenen} tanesi temizlendi.";

        if (yuruyus.Truncated)
        {
            metin += $" UYARI: tarama TAMAMLANAMADI ({yuruyus.TruncatedReasonKey}) — site ağacının bir kısmına " +
                     $"hiç bakılmadı, orada kalan {UserIniDosyaAdi} dosyaları ESKİ sunucu yollarını taşıyor olabilir.";
        }

        if (bulunan > temizlenen && bulunan > UserIniYazmaUstSiniri)
        {
            metin += $" UYARI: tek turda en fazla {UserIniYazmaUstSiniri} dosya yazılır; " +
                     $"{bulunan - UserIniYazmaUstSiniri} dosyaya DOKUNULMADI.";
        }

        return metin;
    }

    private static readonly string[] StaleUserIniPathMarkers =
    {
        "\\vhosts\\", "/vhosts/", "inetpub\\vhosts", "/var/www/vhosts", "/var/www/httpd"
    };

    private static readonly HashSet<string> PathBearingUserIniKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "session.save_path", "upload_tmp_dir", "sys_temp_dir", "error_log",
        "sendmail_path", "curl.cainfo", "openssl.cafile", "soap.wsdl_cache_dir",
        "opcache.file_cache", "extension_dir"
    };

    private static string SanitizeUserIniLine(string line, string root, string tempDir)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is ';' or '[' or '#')
        {
            return line;
        }

        var equalsIndex = line.IndexOf('=');
        if (equalsIndex <= 0)
        {
            return line;
        }

        var key = line[..equalsIndex].Trim().ToLowerInvariant();
        var value = line[(equalsIndex + 1)..].Trim().Trim('"').Trim();

        // open_basedir göçten gelirse ESKİ sunucu yollarını gösterir → yeni site kökü + temp olarak yeniden yaz.
        if (key == "open_basedir")
        {
            return $"open_basedir = \"{root};{tempDir}\"";
        }

        if (value.Length == 0 || !PathBearingUserIniKeys.Contains(key))
        {
            return line;
        }

        var stale = StaleUserIniPathMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
        var rooted = Path.IsPathRooted(value) || value.StartsWith('/') || value.Contains(":\\", StringComparison.Ordinal);
        var directory = TryGetDirectoryName(value);
        var missing = rooted && !Directory.Exists(value) && !File.Exists(value) &&
                      (directory is null || directory.Length == 0 || !Directory.Exists(directory));

        return stale || missing
            ? "; " + line + "    ; AdaPanel goc: gecersiz yol devre disi birakildi"
            : line;
    }

    private static string? TryGetDirectoryName(string value)
    {
        try
        {
            return Path.GetDirectoryName(value);
        }
        catch
        {
            return null;
        }
    }

    // Hedef DB kullanıcı adı türetimi (kullanıcı vermediyse): site adı/DB adından harf-rakam süzülür, 24'e kırpılır,
    // "_usr" eklenir. DatabaseService.NormalizeUserName ayrıca son geçerliliği sağlar (MySQL kullanıcı sınırı 32).
    private static string DeriveDbUser(string siteName, string databaseName)
    {
        var basis = !string.IsNullOrWhiteSpace(siteName) ? siteName : databaseName;
        var cleaned = new string(basis.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (cleaned.Length == 0)
        {
            cleaned = "site";
        }
        if (cleaned.Length > 24)
        {
            cleaned = cleaned[..24];
        }
        return cleaned + "_usr";
    }

    // Politika dostu (büyük + küçük + rakam + simge) ve SQL string-literal güvenli (tırnak/ters bölü yok) parola.
    // Entropi 12 rastgele bayttan (24 hex). MySQL/MariaDB validate_password politikalarını da karşılar.
    private static string GenerateDbPassword()
        => "Ap1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    // Hatayı kullanıcıya iki parçaya ayırır: başlık (i18n anahtarı = "ne oldu") + detay (ham teknik neden = "neden").
    // AppException → MessageKey + Details; diğerleri → ham .Message tek başına (başlık olarak). Böylece "Dosya göçü
    // başarısız oldu" gibi sağır mesajlar yerine gerçek sebep ("530 Login incorrect", "/httpdocs yok …) görünür.
    private static (string Message, string? Detail) DescribeError(Exception exception)
        => exception is AppException appException
            ? (appException.MessageKey, string.IsNullOrWhiteSpace(appException.Details) ? null : appException.Details)
            : (exception.Message, null);
}
