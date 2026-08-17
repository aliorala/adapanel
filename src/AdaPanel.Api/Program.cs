using System.IO;
using System.Threading.RateLimiting;
using AdaPanel.Api.Middleware;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Data.Extensions;
using AdaPanel.Data.Persistence;
using AdaPanel.Infrastructure.Extensions;
using AdaPanel.Infrastructure.Options;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.RateLimiting;
using AdaPanel.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Settings.Configuration;

// TEPSI PROGRAMI — panel HIC acilmadiginda elde kalan tek arac.
//
// Buradan once hicbir sey yapilmaz: yapilandirma okunmaz, servis kurulmaz, port dinlenmez.
// Amac tam da panelin calismadigi durumda calisabilmek; host kurulumu sirasinda bir hata
// olsaydi kurtarma araci da acilmazdi.
//
// WinForms STA is parcacigi ister; ust duzey deyimlerde [STAThread] kullanilamadigi icin
// kendi is parcacigimizi aciyoruz.
if (AdaPanel.Api.Tray.TepsiUygulamasi.BuBizim(args))
{
    var tepsiSonuc = 0;
    var tepsiIsParcacigi = new Thread(() => tepsiSonuc = AdaPanel.Api.Tray.TepsiUygulamasi.Calistir(args));
    tepsiIsParcacigi.SetApartmentState(ApartmentState.STA);
    tepsiIsParcacigi.Start();
    tepsiIsParcacigi.Join();

    // Environment.Exit, "return" DEGIL: ust duzey deyimlerde tek bir yerden deger dondurmek,
    // dosyanin geri kalanini da deger dondurmeye zorlar ve panelin normal acilis yolunu
    // gereksizce degistirirdi. Burada surec zaten bitiyor.
    Environment.Exit(tepsiSonuc);
    return;
}

// PAROLA BELIRLEME — panele giremeyen yoneticinin tek cikis yolu.
//
// Tepsi programiyla AYNI yerde duruyor ve ayni gerekcesi var: bu yol tam da panel acilamadiginda
// lazim, dolayisiyla web sunucusu/servis grafigi kurulmadan calismali. Yalniz veritabanini acar.
//
// UZAKTAN ERISILEBILIR DEGIL ve olmayacak: sunucuda oturum acmis olmayi sart kosar (yonetici
// yetkisi ayrica denetlenir). Ag uzerinden cagrilabilen bir esdegeri, kimlik dogrulamayi tamamen
// atlayan bir arka kapi olurdu.
if (AdaPanel.Api.Recovery.ParolaBelirlemeKomutu.BuBizim(args))
{
    Environment.Exit(await AdaPanel.Api.Recovery.ParolaBelirlemeKomutu.CalistirAsync(args));
    return;
}


var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = "wwwroot"
});

var bootstrapPanelOptions = builder.Configuration.GetSection(PanelRuntimeOptions.SectionName).Get<PanelRuntimeOptions>()
    ?? new PanelRuntimeOptions();
var bootstrapDataRoot = Environment.ExpandEnvironmentVariables(bootstrapPanelOptions.DataRoot);
Directory.CreateDirectory(Path.Combine(bootstrapDataRoot, "config"));
builder.Configuration.AddJsonFile(
    Path.Combine(bootstrapDataRoot, "config", "runtime-settings.json"),
    optional: true,
    reloadOnChange: true);

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = builder.Configuration[$"{PanelRuntimeOptions.SectionName}:ServiceName"] ?? "AdaPanel";
});

var panelOptions = builder.Configuration.GetSection(PanelRuntimeOptions.SectionName).Get<PanelRuntimeOptions>()
    ?? new PanelRuntimeOptions();
var dataRoot = Environment.ExpandEnvironmentVariables(panelOptions.DataRoot);
var logDirectory = Path.Combine(dataRoot, "logs");
var keyDirectory = Path.Combine(dataRoot, "keys");
var panelCertificate = LoadPanelCertificate(dataRoot);

Directory.CreateDirectory(dataRoot);
Directory.CreateDirectory(logDirectory);
Directory.CreateDirectory(keyDirectory);
// Panel SQLite veritabanı klasörü ({dataRoot}\data\adapanel.db) migration/EnsureCreated'dan ÖNCE var olmalı.
// Yoksa SQLite "unable to open database file" verir: taze kurulumda 'data' klasörünü henüz kimse oluşturmamıştır
// (DataRootHardeningService hosted service olarak app.Run SONRASI çalışır; EnsureDatabaseAsync ise app.Run'dan ÖNCE).
Directory.CreateDirectory(Path.Combine(dataRoot, "data"));

var serilogAssemblies = new[]
{
    typeof(Serilog.ConsoleLoggerConfigurationExtensions).Assembly,
    typeof(Serilog.FileLoggerConfigurationExtensions).Assembly
};

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration, new ConfigurationReaderOptions(serilogAssemblies))
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logDirectory, "adapanel-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true)
    .CreateLogger();

builder.Host.UseSerilog();

if (panelCertificate is not null)
{
    var httpsPort = ResolveListenerPort(builder.Configuration["ASPNETCORE_URLS"] ?? builder.Configuration["urls"], panelOptions.PreferredHttpsPort);
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(httpsPort, listenOptions =>
        {
            listenOptions.UseHttps(panelCertificate.Path, panelCertificate.Password);
        });
    });
}

if (panelCertificate is null)
{
    // Sertifika yoksa HTTPS bağlanamaz; install-service --urls "https://..." verse BİLE HTTP'ye düş —
    // sertifikasız HTTPS'e bağlanmaya çalışıp ÇÖKMEK yerine panel HTTP'de açılsın (kurulum sağlamlığı).
    var fallbackPort = ResolveListenerPort(builder.Configuration["ASPNETCORE_URLS"] ?? builder.Configuration["urls"], panelOptions.PreferredHttpsPort);
    var listenerHost = panelOptions.AllowHttpFallback ? "0.0.0.0" : "127.0.0.1";
    builder.WebHost.UseUrls($"http://{listenerHost}:{fallbackPort}");
}

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))
    .SetApplicationName("AdaPanel")
    .ProtectKeysWithDpapi(protectToLocalMachine: true);

builder.Services.AddAdaPanelData(builder.Configuration);
builder.Services.AddAdaPanelInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // ANAHTARLA GELEN İSTEKLERİN TAVANI — /api/v1/** artık kişisel API anahtarını kabul ediyor.
    //
    // Neden küresel sınırlayıcı: uç sayısı 50'yi aşan controller'a yayılmış durumda; politikayı tek
    // tek eklemek unutulacağı için fiilen eklememekle aynı. Neden yalnız anahtarlı istekler:
    // ÇEREZ yolunda sınır YOK, çünkü panelin tek bir sayfası onlarca istek atar ve arayüzü kendi
    // sınırına takmak, düzeltmenin yaratacağı tek gerçek gerileme olurdu; çerez yolunun kendi
    // korumaları zaten var ("login" politikası, "password-change", oturum doğrulaması).
    //
    // 300/dk her ANAHTAR için: bir WHMCS eşitleme turu ya da izleme aracı tek turda onlarca uç
    // çağırır (MCP'nin 120'lik tavanı yapay zeka istemcisi içindi, dış entegrasyon daha konuşkandır).
    // Amaç meşru kullanımı engellemek değil, SIZAN bir anahtarla veri kazımayı ölçülebilir biçimde
    // yavaşlatmak. /mcp ucu ayrıca kendi 120'lik politikasını taşır; ikisi birlikte uygulandığı için
    // orada etkin sınır yine 120'dir.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        PanelAuthentication.CarriesToken(context.Request)
            ? RateLimitPartition.GetFixedWindowLimiter(
                PanelAuthentication.RateLimitPartitionKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 300,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                })
            : RateLimitPartition.GetNoLimiter("cookie"));

    options.AddPolicy("login", context =>
    {
        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            remoteIp,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
    // MCP: yapay zeka istemcisi otomatik ve hızlı çağırır; anahtar sızarsa da ilk fark edilen şey
    // bu uçtan gelen yığın olur. Bölümleme ANAHTARA göre yapılır (IP'ye göre değil): sağlayıcı
    // paylaşımlı çıkış adresleri kullandığı için bir müşterinin trafiği diğerini kilitlememeli.
    // Sınır cömert — meşru bir oturum dakikada onlarca araç çağırabilir; amaç kötüye kullanımı
    // yavaşlatmak, normal kullanımı engellemek değil.
    options.AddPolicy("mcp", context =>
    {
        return RateLimitPartition.GetFixedWindowLimiter(
            // Bölümleme anahtarı PanelAuthentication'dan: anahtarın kendisi yerine özeti kullanılır
            // ve aynı türetme küresel sınırlayıcıda da geçerli olur (iki kopya, iki farklı bölüm).
            PanelAuthentication.RateLimitPartitionKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
    // Parola degisimi: mevcut parolayi dogruluyoruz, yani bu uc bir parola tahmin yuzeyidir.
    // Cerez calinmis olsa bile mevcut parola deneme yanilmayla asilamasin.
    options.AddPolicy("password-change", context =>
    {
        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            remoteIp,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(15),
                QueueLimit = 0
            });
    });
    options.AddPolicy("setup", context =>
    {
        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            remoteIp,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 3,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0
            });
    });
});

// PANELİN İKİ KİMLİK YOLU TEK VARSAYILAN ŞEMADA BİRLEŞTİRİLDİ.
//
// Önce varsayılan şema doğrudan ÇEREZ'di ve PanelToken yalnızca /mcp ucuna bağlıydı; /api/v1/**
// controller'larının hiçbiri AuthenticationSchemes belirtmediği için "Authorization: Bearer adap_…"
// başlığı HİÇ OKUNMUYORDU. Yani panelin "kişisel API anahtarı" özelliği REST'te fiilen yoktu ve dış
// entegrasyonlar (WHMCS modülü, müşteri betikleri, izleme araçları) çerez oturumu açmak zorunda
// kalıyordu. Varsayılan şema artık bir YÖNLENDİRİCİ: isteğin taşıdığı kimliğe bakıp doğru şemayı
// seçer, böylece tek kayıt bütün uçları kapsar ve yeni controller'da unutulamaz.
// Gerekçenin tamamı PanelAuthentication'da.
builder.Services.AddAuthentication(PanelAuthentication.SchemeName)
    .AddPolicyScheme(PanelAuthentication.SchemeName, PanelAuthentication.SchemeName, options =>
    {
        options.ForwardDefaultSelector = PanelAuthentication.SelectScheme;
    })
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.Cookie.Name = panelOptions.AuthCookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = panelOptions.AllowHttpFallback ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);

        // OTURUM ÇEREZİ ARTIK GERÇEKLE KARŞILAŞTIRILIYOR.
        //
        // Buraya kadar hiçbir olay bağlı değildi: çerez bir kez verildikten sonra 12 saat (kalıcıysa
        // 14 gün) sorgusuz geçerliydi. Parola değiştirmek, kullanıcıyı pasife almak, rolünü düşürmek
        // ve kullanıcıyı SİLMEK açık oturumu düşürmüyordu — yani bir güvenlik olayına verilecek ilk
        // yanıt (parolayı değiştir / hesabı kapat) fiilen çalışmıyordu. Gerekçenin tamamı ve neden
        // UpdatedAtUtc'nin ölçüt OLAMAYACAĞI CookieSessionValidator'da yazılı.
        options.Events.OnSigningIn = CookieSessionValidator.OnSigningInAsync;
        options.Events.OnValidatePrincipal = CookieSessionValidator.OnValidatePrincipalAsync;

        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return context.Response.WriteAsJsonAsync(ApiResponse<object?>.Fail("auth.unauthorized", StatusCodes.Status401Unauthorized));
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return context.Response.WriteAsJsonAsync(ApiResponse<object?>.Fail("auth.forbidden", StatusCodes.Status403Forbidden));
        };
    })
    // Sunucudan sunucuya istekler için ikinci şema: "Authorization: Bearer adap_…".
    // Çerez tarayıcı içindir; tarayıcı dışı bir istemci için 12 saatlik oturum, dakikada 5 giriş
    // denemesi sınırı ve iki adımlı doğrulama onu kullanılamaz kılar. Bu şema çerez şemasıyla AYNI
    // talepleri ürettiği için mevcut yetkilendirme/kapsam/denetim kodu değişmeden çalışır.
    .AddScheme<AuthenticationSchemeOptions, PanelTokenAuthenticationHandler>(
        PanelTokenAuthenticationHandler.SchemeName,
        _ => { });

builder.Services.AddAuthorization();

// MCP sunucusu: yapay zeka istemcisi (Claude vb.) paneli bu uç üzerinden kullanır.
// Stateless bilerek: MCP'nin yeni sürümü protokol seviyesindeki oturumları kaldırdı, baştan
// durumsuz kurmak ileriye dönük en güvenli tercih.
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<AdaPanel.Api.Mcp.AdaPanelReadTools>()
    .WithTools<AdaPanel.Api.Mcp.AdaPanelWriteTools>();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "AdaPanel.AntiCsrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = panelOptions.AllowHttpFallback ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});

builder.Services
    .AddControllersWithViews(options =>
    {
        // CSRF DOGRULAMASI ARTIK KIMLIK YOLUNA GORE KARAR VERIYOR.
        //
        // Onceki kayit AutoValidateAntiforgeryTokenAttribute'du ve karari UC meta verisinden
        // veriyordu, yani istek basina degistirilemiyordu. CSRF korumasi CEREZE karsidir;
        // Authorization basligi tarayici tarafindan kendiliginden gonderilmez, dolayisiyla anahtarla
        // gelen istekte antiforgery koruma degil yalnizca engeldir (dis entegrasyonun once
        // /auth/csrf cagirip cerez tasimasi gerekirdi). Cerezle gelen istekte ise kesinlikle
        // uygulanmaya devam eder. Ayrintili gerekce: PanelAntiforgeryFilter.
        options.Filters.Add<PanelAntiforgeryFilter>();

        // ANAHTARIN YETENEKLERI REST'TE DE BAGLAYICI.
        //
        // AllowDestructive/AllowTerminal bugune kadar yalniz MCP araclarinda sorgulaniyordu; REST
        // anahtari kabul etmeye baslayinca bu denetim olmasaydi "yikici islemlere izin vermeyen"
        // bir anahtar tek HTTP cagrisiyla silme yapabilir, sunucuda komut calistirabilirdi.
        // Ayrintili gerekce: PanelTokenCapabilities.
        options.Filters.Add<PanelTokenCapabilityFilter>();

        // ABONELIK ASKISI TEK KAYNAKTAN DAYATILIR.
        //
        // Aski daha once yalniz customer-scopes.json'a yaziliyor, API'de hicbir yerde
        // SORULMUYORDU: askiya alinan musteri panele girip durdurulmus sitesini yeniden
        // baslatabiliyordu. Kural kuresel filtreye konuyor cunku her controller'a elle eklenen
        // bir denetim, yeni eklenen controller'da unutulacagi icin fiilen yok sayilir.
        // Ayrintili gerekce: SubscriptionSuspension.
        options.Filters.Add<SubscriptionSuspensionFilter>();
    })
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var errors = context.ModelState
                .Where(entry => entry.Value?.Errors.Count > 0)
                .ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value?.Errors.Select(error => error.ErrorMessage).ToArray() ?? []);

            return new BadRequestObjectResult(ApiResponse<object>.Fail("validation.failed", 400, errors));
        };
    });

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseMiddleware<ApiExceptionHandlingMiddleware>();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    // DbTools reverse-proxy: gövdeli (POST/PUT/PATCH/DELETE) isteklerde istek gövdesini TEKRAR-OKUNABİLİR yap.
    // DbToolsProxyController gövdeyi başa sarıp yerel phpMyAdmin'e iletir; aksi halde StreamContent boş okur ve
    // "Sent 0 bytes but Content-Length promised N" 500'ü çıkar (phpMyAdmin POST AJAX'ları patlardı). EnableBuffering
    // küçük gövdeyi bellekte, büyük SQL import'unu diskte tutar (bellek taşmaz).
    if (context.Request.Path.StartsWithSegments("/api/v1/dbtools") && !HttpMethods.IsGet(context.Request.Method))
    {
        context.Request.EnableBuffering();
    }

    // DbTools reverse-proxy (phpMyAdmin/Adminer) YOLUNA panelin KATI güvenlik başlıklarını UYGULAMA:
    // katı CSP (script-src 'self' = inline script YOK) + nosniff, phpMyAdmin'in inline script/CSS/asset'lerini
    // bloklar → menü-genişliği/layout JS'i çalışmaz, ana içerik sol menünün ALTINA kayar (kullanıcı bildirimi).
    // Bu araçlar zaten panelin kimlik-doğrulamalı HTTPS proxy'si arkasında; kendi (phpMyAdmin) başlıkları geçerli olur.
    if (!context.Request.Path.StartsWithSegments("/api/v1/dbtools"))
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    }

    await next();
});
app.UseMiddleware<SecureEntryPathMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();

// ÖNBELLEK BAŞLIKLARI — güncelleme sonrası BEYAZ EKRANIN kök nedeni buradaydı.
//
// Hiç Cache-Control gönderilmiyordu. Başlık yokken tarayıcı SEZGİSEL önbellekleme uygular:
// Last-Modified'a bakıp dosyayı bir süre DOĞRULAMADAN yeniden kullanır. Panel güncellendiğinde
// wwwroot tamamen yenilenir ve varlıkların içerik-hash'li adları değişir; tarayıcıdaki ESKİ
// index.html ise artık var olmayan /assets/index-ESKIHASH.js dosyasını istiyordu → 404 → boş sayfa.
// Kullanıcı tarafında bu "güncelleme paneli bozdu" gibi görünüyordu; oysa sunucudaki dosyalar
// doğruydu, tarayıcı eski giriş noktasını tutuyordu.
//
// Doğru ayrım ikili:
//   • index.html  → içerik-hash'i YOK, her zaman doğrulanmalı  → no-cache
//   • /assets/*   → adı içeriğinden türüyor, değişirse adı da değişir → bir yıl, immutable
// Bu ayrım olmadan ya her güncellemede beyaz ekran olur ya da her açılışta her şey yeniden iner.
// Aynı seçenek İKİ yerde gerekiyor (aşağıda MapFallbackToFile). Tek nesne: biri güncellenip
// diğeri unutulduğunda panelin giriş sayfası sessizce başlıksız kalırdı — nitekim ilk denemede
// tam olarak bu oldu ve ancak başlıklar ölçülünce görüldü.
var statikSecenekler = new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        var yol = context.Context.Request.Path.Value ?? string.Empty;
        var basliklar = context.Context.Response.GetTypedHeaders();

        if (yol.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
        {
            basliklar.CacheControl = new Microsoft.Net.Http.Headers.CacheControlHeaderValue
            {
                Public = true,
                MaxAge = TimeSpan.FromDays(365),
                Extensions = { new Microsoft.Net.Http.Headers.NameValueHeaderValue("immutable") }
            };
            return;
        }

        // index.html ve hash'siz her şey: sunucuya SOR. NoCache "saklama" değil "doğrulamadan
        // kullanma" demektir; dosya değişmediyse 304 döner, maliyeti tek bir koşullu istektir.
        basliklar.CacheControl = new Microsoft.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            MustRevalidate = true
        };
    }
};

app.UseStaticFiles(statikSecenekler);

app.MapControllers();

// MCP ucu: YALNIZCA anahtar (bearer) şemasıyla, oturum çerezi kabul edilmez — tarayıcıdan
// gelen bir isteğin yanlışlıkla yapay zeka araçlarını çağırması mümkün olmamalı.
// Antiforgery bu uçta anlamsız: CSRF koruması ÇEREZE karşıdır, Authorization başlığı ortamdan
// otomatik gitmediği için burada koruma değil yalnız engel olurdu.
app.MapMcp("/mcp")
    .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
    {
        AuthenticationSchemes = AdaPanel.Api.Security.PanelTokenAuthenticationHandler.SchemeName
    })
    .WithMetadata(new Microsoft.AspNetCore.Mvc.IgnoreAntiforgeryTokenAttribute())
    .RequireRateLimiting("mcp");
// Eşleşmeyen API/MCP yolları SPA'ya DÜŞMEMELİ. Öncesinde tek bir MapFallbackToFile vardı ve
// "/api/v1/yanlis" ya da "/mcp/x" gibi her yol 200 + index.html döndürüyordu: istemci JSON beklerken
// HTML alıyor ("Unexpected token '<'" hatası), MCP istemcisi ise sunucuyu ayakta ama bozuk sanıyordu.
// Bu iki kalıp SPA yedeğinden daha özgüldür, bu yüzden önce eşleşir.
app.MapFallback("/api/{**rest}", NotFoundJsonAsync);
app.MapFallback("/mcp/{**rest}", NotFoundJsonAsync);
// SPA yedeği de AYNI başlıkları almalı. Panelin gerçek giriş yolu (gizli /panel) statik dosya
// ara katmanından DEĞİL buradan geçiyor: seçenekler verilmezse index.html başlıksız gider ve
// güncelleme sonrası beyaz ekran — düzeltmenin asıl hedefi — aynen sürerdi.
app.MapFallbackToFile("index.html", statikSecenekler);

await EnsureDatabaseAsync(app, dataRoot);

app.Run();

static Task NotFoundJsonAsync(HttpContext context)
{
    context.Response.StatusCode = StatusCodes.Status404NotFound;
    context.Response.ContentType = "application/json; charset=utf-8";
    return context.Response.WriteAsJsonAsync(
        AdaPanel.Core.Dtos.Common.ApiResponse<object?>.Fail("not_found", StatusCodes.Status404NotFound));
}

static async Task EnsureDatabaseAsync(WebApplication app, string dataRoot)
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AdaPanelDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // K1 GÜVENLİK: panel DB'si AÇILMADAN ÖNCE veri kökünü sertleştir (tenant'lar adapanel.db'yi okuyamasın).
    // Artık GÜVENLİ /inheritance:d yöntemiyle (SYSTEM/Admin/CREATOR-OWNER korunur) → SQLite kırılmaz.
    await AdaPanel.Infrastructure.Bootstrap.DataRootHardeningService.HardenAsync(dataRoot, logger);

    var database = dbContext.Database;

    try
    {
        var applied = (await database.GetAppliedMigrationsAsync()).ToList();
        if (applied.Count == 0)
        {
            // Migration geçmişi yok. Şema zaten varsa (eski EnsureCreated kurulumu) MigrateAsync
            // "table already exists" ile patlar; bunu önlemek için InitialCreate'i BASELINE'la:
            // şema InitialCreate ile birebir olduğundan onu "uygulanmış" işaretleyip kalanları uygula.
            var creator = dbContext.GetService<IRelationalDatabaseCreator>();
            var schemaAlreadyExists = await creator.ExistsAsync() && await creator.HasTablesAsync();
            if (schemaAlreadyExists)
            {
                var initialMigration = (await database.GetPendingMigrationsAsync()).FirstOrDefault();
                if (!string.IsNullOrEmpty(initialMigration))
                {
                    await database.ExecuteSqlRawAsync(
                        "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (" +
                        "\"MigrationId\" TEXT NOT NULL CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, " +
                        "\"ProductVersion\" TEXT NOT NULL);");
                    await database.ExecuteSqlRawAsync(
                        "INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({0}, {1});",
                        initialMigration,
                        "8.0.27");
                    logger.LogInformation("Mevcut şema InitialCreate olarak baseline'landı (EnsureCreated → migration geçişi).");
                }
            }
        }

        // Bekleyen migration'ları uygula: taze kurulumda tüm şema, baseline sonrası/güncellemede yenileri.
        await database.MigrateAsync();
        logger.LogInformation("Veritabanı şeması migration ile güncel.");

        await ApplyAccessRecoveryFlagAsync(
            dataRoot,
            dbContext,
            scope.ServiceProvider.GetRequiredService<AdaPanel.Core.Interfaces.IFirewallService>(),
            logger);
    }
    catch (Exception exception)
    {
        // KURTARMA AĞI: db açılamadıysa (ör. ACL herhangi bir sebeple bozuksa) önce 'data' klasörünün erişimini
        // GARANTİ et (ACL sıfırla), sonra yeniden dene; yine olmazsa en azından şemayı oluştur. Böylece panel
        // db yüzünden ASLA kilitlenmez — kullanıcı elle uğraşmak zorunda kalmaz.
        logger.LogError(exception, "Migration başarısız; veri klasörü erişimi garanti edilip yeniden denenecek.");
        await AdaPanel.Infrastructure.Bootstrap.DataRootHardeningService.EnsureWritableAsync(dataRoot, logger);
        try
        {
            await database.MigrateAsync();
        }
        catch (Exception retryException)
        {
            logger.LogError(retryException, "Migration tekrar başarısız; EnsureCreated ile şema garanti ediliyor.");
            await dbContext.Database.EnsureCreatedAsync();
        }
    }
}

// ERİŞİM KURTARMA (kendini kilitleyen tek yönetici için). Panelde kilidi açacak başka bir
// yönetici yoksa çıkış yolu YALNIZCA beklemekti (LoginLockoutMinutes, varsayılan 15 dk) — SQLite'ı
// elle düzenlemek için sunucuda sqlite aracı da bulunmuyor. Plesk'in "plesk bin admin" muadili:
// veri kökündeki config klasöründe bir bayrak dosyası varsa panel açılışta TÜM kilitleri temizler.
// Dosyayı oluşturmak yönetici ACL'i gerektirir (K1 sertleştirmesi) → sunucuya erişimi olan kişi
// zaten paneli sıfırlayabilirdi; yeni bir güvenlik açığı doğmaz. Bayrak kullanıldıktan sonra SİLİNİR.
static async Task ApplyAccessRecoveryFlagAsync(
    string dataRoot,
    AdaPanelDbContext dbContext,
    AdaPanel.Core.Interfaces.IFirewallService firewallService,
    Microsoft.Extensions.Logging.ILogger<Program> logger)
{
    try
    {
        var flagPath = Path.Combine(dataRoot, "config", "recover-access.flag");
        if (!File.Exists(flagPath))
        {
            return;
        }

        var locked = await dbContext.Users
            .Where(user => user.LockedUntilUtc != null || user.FailedLoginCount > 0)
            .ToListAsync();

        foreach (var user in locked)
        {
            user.LockedUntilUtc = null;
            user.FailedLoginCount = 0;
            user.UpdatedAtUtc = DateTime.UtcNow;
        }

        // Aynı bayrak IP engellerini de kaldırır: art arda hatalı denemeler brute-force korumasını
        // tetikleyip yöneticinin IP'sini firewall'da bloklamış olabilir (o zaman panel hiç açılmaz).
        var banStatePath = Path.Combine(dataRoot, "config", "bruteforce.json");
        var bansCleared = false;
        try
        {
            if (File.Exists(banStatePath))
            {
                await AdaPanel.Infrastructure.Security.BruteForceRecovery.ClearBansAsync(banStatePath, logger);
                bansCleared = true;
            }
        }
        catch (Exception banException)
        {
            logger.LogWarning(banException, "Erişim kurtarma: IP engelleri temizlenemedi.");
        }

        // Panelin kendi ENGELLEME kuralları da temizlenir. Yönetici yanlışlıkla panel portunu
        // (ya da uzak masaüstünü) engellemiş olabilir; Windows'ta engelleme izni ezdiği için
        // panele bir daha ulaşılamaz ve panelden geri almak imkânsız hâle gelirdi. Bu bayrak,
        // sunucu konsolundan atılabilen tek kurtarma kolu olduğu için o kuralları da kaldırır.
        var blockRulesCleared = 0;
        try
        {
            blockRulesCleared = await firewallService.ClearManagedBlockRulesAsync();
        }
        catch (Exception firewallException)
        {
            logger.LogWarning(firewallException, "Erişim kurtarma: engelleme kuralları temizlenemedi.");
        }

        await dbContext.SaveChangesAsync();
        File.Delete(flagPath);

        logger.LogWarning(
            "ERİŞİM KURTARMA uygulandı (recover-access.flag): {Count} kullanıcının giriş kilidi temizlendi, IP engelleri temizlendi={Bans}, {BlockRules} panel engelleme kuralı kaldırıldı. Bayrak dosyası silindi.",
            locked.Count,
            bansCleared,
            blockRulesCleared);
    }
    catch (Exception exception)
    {
        logger.LogError(exception, "Erişim kurtarma bayrağı işlenemedi.");
    }
}

static PanelCertificate? LoadPanelCertificate(string dataRoot)
{
    try
    {
        var certificatePath = Path.Combine(dataRoot, "certs", "panel.pfx");
        var passwordPath = Path.Combine(dataRoot, "certs", "panel.password");

        if (!File.Exists(certificatePath) || !File.Exists(passwordPath))
        {
            return null;
        }

        var password = File.ReadAllText(passwordPath).Trim();
        if (string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        return new PanelCertificate(certificatePath, password);
    }
    catch
    {
        return null;
    }
}

static int ResolveListenerPort(string? configuredUrls, int fallbackPort)
{
    if (string.IsNullOrWhiteSpace(configuredUrls))
    {
        return fallbackPort;
    }

    foreach (var segment in configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (Uri.TryCreate(segment, UriKind.Absolute, out var uri) && uri.Port > 0)
        {
            return uri.Port;
        }
    }

    return fallbackPort;
}

sealed record PanelCertificate(string Path, string Password);
