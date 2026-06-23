using AdaPanel.Core.Dtos.Common;
using AdaPanel.Core.Interfaces;

namespace AdaPanel.Api.Middleware;

public sealed class SecureEntryPathMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ISetupService setupService, IPanelSecurityService securityService)
    {
        var path = context.Request.Path;
        if (IsAlwaysPublicPath(path))
        {
            await next(context);
            return;
        }

        var setupStatus = await setupService.GetStatusAsync(context.RequestAborted);
        if (!setupStatus.IsInitialized)
        {
            await next(context);
            return;
        }

        var isAllowedIp = await securityService.IsIpAllowedAsync(context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted);
        if (!isAllowedIp)
        {
            await RejectApiRequestAsync(context, StatusCodes.Status403Forbidden, "security.ip_not_allowed");
            return;
        }

        if (path.StartsWithSegments("/api", out _))
        {
            if (RequiresSecureEntryHeader(path))
            {
                var expectedPath = await securityService.GetSecureEntryPathAsync(context.RequestAborted);
                var providedPath = context.Request.Headers["X-AdaPanel-Entry-Path"].ToString().Trim().Trim('/').ToLowerInvariant();

                if (!string.Equals(providedPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    await RejectApiRequestAsync(context, StatusCodes.Status404NotFound, "security.invalid_entry_path");
                    return;
                }
            }

            await next(context);
            return;
        }

        var secureEntryPath = await securityService.GetSecureEntryPathAsync(context.RequestAborted);
        if (!path.HasValue || path == "/")
        {
            context.Response.Redirect($"/{secureEntryPath}");
            return;
        }

        if (path.StartsWithSegments($"/{secureEntryPath}", out var remainder))
        {
            context.Request.Path = remainder.HasValue ? remainder : "/";
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static bool IsAlwaysPublicPath(PathString path)
    {
        return path.StartsWithSegments("/assets", out _)
            || path.StartsWithSegments("/swagger", out _)
            || path.StartsWithSegments("/favicon", out _)
            || path.StartsWithSegments("/robots.txt", out _)
            || path.StartsWithSegments("/index.html", out _)
            || path.StartsWithSegments("/api/v1/setup", out _)
            || path.StartsWithSegments("/api/v1/health", out _);
    }

    private static bool RequiresSecureEntryHeader(PathString path)
    {
        return path.StartsWithSegments("/api/v1/auth", out _)
            || path.StartsWithSegments("/api/v1/users", out _)
            || path.StartsWithSegments("/api/v1/security", out _)
            || path.StartsWithSegments("/api/v1/audit-logs", out _)
            || path.StartsWithSegments("/api/v1/system", out _)
            || path.StartsWithSegments("/api/v1/php", out _)
            || path.StartsWithSegments("/api/v1/sites", out _)
            || path.StartsWithSegments("/api/v1/db", out _)
            || path.StartsWithSegments("/api/v1/my-databases", out _)
            || path.StartsWithSegments("/api/v1/software", out _)
            || path.StartsWithSegments("/api/v1/webdb", out _)
            || path.StartsWithSegments("/api/v1/cache", out _)
            || path.StartsWithSegments("/api/v1/files", out _)
            || path.StartsWithSegments("/api/v1/ssl", out _)
            || path.StartsWithSegments("/api/v1/ftp", out _)
            || path.StartsWithSegments("/api/v1/mail", out _)
            || path.StartsWithSegments("/api/v1/cron", out _)
            || path.StartsWithSegments("/api/v1/automation", out _)
            || path.StartsWithSegments("/api/v1/firewall", out _)
            || path.StartsWithSegments("/api/v1/nginx", out _)
            || path.StartsWithSegments("/api/v1/docker", out _)
            || path.StartsWithSegments("/api/v1/git", out _)
            || path.StartsWithSegments("/api/v1/logs", out _)
            || path.StartsWithSegments("/api/v1/notify", out _)
            || path.StartsWithSegments("/api/v1/backup", out _)
            || path.StartsWithSegments("/api/v1/store", out _)
            || path.StartsWithSegments("/api/v1/update", out _)
            || path.StartsWithSegments("/api/v1/panel-settings", out _)
            || path.StartsWithSegments("/api/v1/scope", out _)
            || path.StartsWithSegments("/api/v1/terminal", out _);
    }

    private static Task RejectApiRequestAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsJsonAsync(ApiResponse<object?>.Fail(message, statusCode));
    }
}
