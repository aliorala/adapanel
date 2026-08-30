using System.Diagnostics;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;
using AdaPanel.Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Routing;

using AdaPanel.Api.Security;

namespace AdaPanel.Api.Middleware;

public sealed class ApiExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ApiExceptionHandlingMiddleware> logger,
    IServiceScopeFactory scopeFactory)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (AppException exception)
        {
            logger.LogWarning(
                exception,
                "Application rule blocked request {Method} {Path} with message key {MessageKey}",
                context.Request.Method,
                context.Request.Path,
                exception.MessageKey);

            // 4xx kural ihlalleri BEKLENEN akıştır (yanlış parola, kota aşımı...) — kaydedilirse
            // gerçek hatalar gürültüde kaybolur. Yalnız 5xx'e düşen AppException bir kusurdur.
            if (exception.HttpStatusCode >= StatusCodes.Status500InternalServerError)
            {
                QueueErrorReport(context, exception);
            }

            await WriteResponseAsync(context, exception.HttpStatusCode, exception.MessageKey, exception.Code, exception.Details);
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "A business rule prevented request {Method} {Path}", context.Request.Method, context.Request.Path);
            var statusCode = exception.Message == "setup.already_initialized"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest;

            await WriteResponseAsync(context, statusCode, exception.Message);
        }
        catch (AntiforgeryValidationException exception)
        {
            logger.LogWarning(exception, "Invalid CSRF token for request {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteResponseAsync(context, StatusCodes.Status400BadRequest, "security.invalid_csrf_token");
        }
        catch (OperationCanceledException exception) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation(
                exception,
                "Request was canceled by the client for {Method} {Path}",
                context.Request.Method,
                context.Request.Path);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled exception for request {Method} {Path}", context.Request.Method, context.Request.Path);
            QueueErrorReport(context, exception);
            await WriteResponseAsync(context, StatusCodes.Status500InternalServerError, "common.unexpected_error");
        }
    }

    /// <summary>
    /// Hatayı maskelenmiş kayda ARKA PLANDA yazar.
    /// NEDEN arka plan: kayıt (veritabanı yazımı) hata yanıtını GECİKTİRMEMELİ — kullanıcı
    /// zaten bir hata almış durumda, üstüne bir de bekletilmemeli.
    /// NEDEN önce alan çıkarımı: <see cref="HttpContext"/> yanıt tamamlandıktan sonra
    /// geçersizdir; ihtiyaç duyulan değerler görev başlatılmadan ÖNCE okunur.
    /// NEDEN kendi kapsamı: istek kapsamı (ve DbContext'i) yanıtla birlikte yok edilir.
    /// </summary>
    private void QueueErrorReport(HttpContext context, Exception exception)
    {
        string routePattern;
        string correlationId;

        try
        {
            var template = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
            routePattern = ErrorReportSanitizer.BuildRoutePattern(
                context.Request.Method,
                template,
                context.Request.Path.Value);
            correlationId = Activity.Current?.Id ?? context.TraceIdentifier ?? string.Empty;
        }
        catch
        {
            // Bağlam okunamadıysa kayıt uğruna isteği riske atma.
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var reportService = scope.ServiceProvider.GetRequiredService<IErrorReportService>();
                // İstek iptal jetonu KULLANILMAZ: tarayıcı bağlantıyı kapatınca kayıt da iptal olurdu.
                await reportService.RecordAsync(exception, routePattern, correlationId, CancellationToken.None);
            }
            catch (Exception reportFailure)
            {
                // Bildirim mekanizmasının kendi hatası kullanıcıya YANSIMAZ; yalnız yerel günlük.
                logger.LogDebug(reportFailure, "Error report could not be queued for {RoutePattern}", routePattern);
            }
        });
    }

    private static Task WriteResponseAsync(HttpContext context, int statusCode, string message, int? code = null, string details = "")
    {
        context.Response.Clear();

        // BASLIKLAR CLEAR'DAN SONRA YENIDEN UYGULANIR.
        //
        // OLCULDU: `Response.Clear()` yalniz govdeyi degil BASLIKLARI da siler. Bu ara katman
        // guvenlik basligi ara katmanindan DAHA DISTA oldugu icin, buraya dusen her yanit
        // (yanlis parola, kota asimi, CSRF reddi, her 500) CSP / nosniff / X-Frame-Options /
        // Referrer-Policy OLMADAN gidiyordu. Yani panelin en siki korumasi, tam da hata
        // durumlarinda -- saldirganin en cok urettigi yanitlarda -- kapaliydi.
        //
        // Clear() KALDIRILMADI: yarim yazilmis bir govdeyi atmak dogru davranistir. Duzeltme,
        // silinen seyi geri koymaktir. Kaynak tek: PanelSecurityHeaders.
        PanelSecurityHeaders.Apply(context);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        // SIR MASKELEMESI GOVDE YAZILMADAN ONCE, BU TEK NOKTADA.
        //
        // Bu ara katmanin yazdigi her hata yaniti buradan gecer; `details` bugun yalniz
        // `AppException.Details`'ten gelir ve orasi ZATEN maskelenmis durumdadir (bkz.
        // AppException.Details). Yine de burada tekrar uygulanir, cunku bu metodun sozlesmesi
        // "keyfi bir dize al ve istemciye yaz"dir: yarin baska bir catch bloguna eklenen bir
        // `details` argumani (ornegin ham `exception.Message`) sessizce disaridan gecerdi.
        // Maskeleme etkisizdir -- maskelenmis metni yeniden maskelemek ayni metni verir -- yani
        // ikinci uygulamanin bedeli yok, atlanmasinin bedeli bir sizintidir.
        //
        // `message` DE MASKELENIR -- OLCULDU, tahmin degil.
        //
        // `message` normalde bir ceviri anahtaridir ("common.unexpected_error") ve maskeleme ona
        // dokunmaz. AMA bir yol istisnadir: yukaridaki `InvalidOperationException` yakalayicisi
        // `exception.Message`'i DOGRUDAN buraya verir -- yani ham .NET istisna metni istemciye
        // `message` alaninda gider. Bir baglanti dizgesi ("...Uid=root;Pwd=<parola>...") tam olarak
        // boyle disari cikardi ve YALNIZ `details` maskelenseydi bu yol acik kalirdi. Sizinti
        // kanalinin `details` adli alan degil, GOVDEYE YAZMA EYLEMI oldugunu gosteren yer burasi.
        //
        // KURAL BURADA TANIMLI DEGIL: tek kaynak SecretRedactor.
        return context.Response.WriteAsJsonAsync(ApiResponse<object?>.Fail(
            SecretRedactor.Redact(message),
            code ?? statusCode,
            details: SecretRedactor.Redact(details)));
    }
}
