using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Common;
using Microsoft.AspNetCore.Antiforgery;

namespace AdaPanel.Api.Middleware;

public sealed class ApiExceptionHandlingMiddleware(RequestDelegate next, ILogger<ApiExceptionHandlingMiddleware> logger)
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
            await WriteResponseAsync(context, StatusCodes.Status500InternalServerError, "common.unexpected_error");
        }
    }

    private static Task WriteResponseAsync(HttpContext context, int statusCode, string message, int? code = null, string details = "")
    {
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        return context.Response.WriteAsJsonAsync(ApiResponse<object?>.Fail(message, code ?? statusCode, details: details));
    }
}
