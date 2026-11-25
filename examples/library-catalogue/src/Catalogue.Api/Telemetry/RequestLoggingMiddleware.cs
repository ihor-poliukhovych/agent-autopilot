using System.Globalization;
using System.Text.RegularExpressions;

namespace Catalogue.Api.Telemetry;

/// <summary>Writes one telemetry record per request: route, status code, duration and any exception.</summary>
public sealed partial class RequestLoggingMiddleware(RequestDelegate next, TelemetryWriter writer, TimeProvider clock)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!writer.Enabled)
        {
            await next(context);
            return;
        }

        var started = clock.GetTimestamp();
        try
        {
            await next(context);
            Write(context, context.Response.StatusCode, started, exception: null);
        }
        catch (Exception ex)
        {
            Write(context, StatusCodes.Status500InternalServerError, started, ex);
            throw;
        }
    }

    private void Write(HttpContext context, int statusCode, long started, Exception? exception)
    {
        var durationMs = Math.Round(clock.GetElapsedTime(started).TotalMilliseconds, 1);
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? context.Request.Path.Value ?? "/";
        // "/books/{id:int}/" is logged as "/books/{id}": constraints and trailing slashes are noise in logs.
        route = "/" + RouteConstraint().Replace(route, "{$1}").Trim('/');

        var level = statusCode >= 500 ? "Error" : statusCode >= 400 ? "Warning" : "Information";
        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"HTTP {context.Request.Method} {route} responded {statusCode} in {durationMs} ms");
        writer.Write(new TelemetryRecord(
            clock.GetUtcNow(),
            level,
            message,
            context.Request.Method,
            route,
            statusCode,
            durationMs,
            exception?.ToString()));
    }

    [GeneratedRegex(@"\{(\w+):[^}]*\}")]
    private static partial Regex RouteConstraint();
}
