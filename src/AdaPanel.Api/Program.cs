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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Settings.Configuration;

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

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = panelOptions.AuthCookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = panelOptions.AllowHttpFallback ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
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
    });

builder.Services.AddAuthorization();
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
        options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
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
app.UseStaticFiles();

app.MapControllers();
app.MapFallbackToFile("index.html");

await EnsureDatabaseAsync(app, dataRoot);

app.Run();

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
