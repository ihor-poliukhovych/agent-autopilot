using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Pool;
using Autopilot.Core.Settings;
using Autopilot.Core.Workspace;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging;

namespace Autopilot.Web;

/// <summary>What the UI tells the person once it listens: the address, and the sign-in link when a key is required.</summary>
public sealed record UiStart(int Port, string Address, string? SignInAddress, bool RequiresKey);

/// <summary>Builds and starts the local web UI. Endpoints are thin: each one calls a service and returns its result.</summary>
public static class WebUi
{
    /// <summary>
    /// Builds the app. <paramref name="configureBuilder"/> replaces the default server setup (127.0.0.1 only);
    /// tests pass one that uses an in-memory server.
    /// </summary>
    public static WebApplication Build(WebUiOptions options, Action<WebApplicationBuilder>? configureBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        if (configureBuilder is null)
        {
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Listen(options.BindAddress, options.Port);
                // "localhost" resolves to ::1 first on many systems; with nothing listening there each connection
                // waits (about 200 ms measured on Windows) before it falls back to 127.0.0.1.
                if (options.Port != 0 && Equals(options.BindAddress, IPAddress.Loopback)
                    && System.Net.Sockets.Socket.OSSupportsIPv6 && UiAccess.IsFree(IPAddress.IPv6Loopback, options.Port))
                {
                    k.Listen(IPAddress.IPv6Loopback, options.Port);
                }
            });
        }
        else
        {
            configureBuilder(builder);
        }

        builder.Services.Configure<JsonOptions>(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });
        var settings = new WorkspaceSettings(options.Workspace);
        var loopback = IPAddress.IsLoopback(options.BindAddress);
        var requireKey = !loopback || (options.RequireKey ?? settings.GetBool(WorkspaceSettings.UiRequireKey));
        var guard = new SessionGuard(requireKey ? () => options.SessionKey ?? UiAccess.GetOrCreateKey(settings) : null, anyHost: !loopback);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(guard);
        builder.Services.AddSingleton<ProjectScopes>();
        builder.Services.AddSingleton<SetupService>();
        builder.Services.AddSingleton<TaskOperations>();
        builder.Services.AddSingleton<PoolOperations>();
        builder.Services.AddSingleton<RunOperations>();

        var app = builder.Build();
        app.Use(Timing(options));
        guard.Use(app);
        app.Use(ErrorsToJson);
        EmbeddedSite.Use(app);
        Endpoints.Map(app);
        return app;
    }

    /// <summary>Runs the UI until cancelled and calls <paramref name="started"/> once it listens.</summary>
    public static async Task RunAsync(WebUiOptions options, Action<UiStart> started, CancellationToken ct)
    {
        var app = Build(options);
        await app.StartAsync(ct).ConfigureAwait(false);
        var port = new Uri(app.Urls.First()).Port;
        var guard = app.Services.GetRequiredService<SessionGuard>();
        var key = guard.RequiresKey ? options.SessionKey ?? UiAccess.GetOrCreateKey(new WorkspaceSettings(options.Workspace)) : null;
        started(new UiStart(port, UiAccess.Address(port), key is null ? null : UiAccess.Address(port, key), guard.RequiresKey));
        _ = Task.Run(() => WarmUp(app), CancellationToken.None);
        try
        {
            await app.WaitForShutdownAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped with Ctrl+C.
        }
        finally
        {
            await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the tracker lists of the tracker projects once, so the first page does not wait for them.</summary>
    internal static async Task WarmUp(WebApplication app)
    {
        try
        {
            var options = app.Services.GetRequiredService<WebUiOptions>();
            var scopes = app.Services.GetRequiredService<ProjectScopes>();
            foreach (var project in new ProjectRegistry(options.Workspace).List().Where(p => p.TaskSource != ProjectRegistry.LocalSource))
            {
                try
                {
                    await scopes.Get(project.Key).CreateList().ListAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The page shows the problem when it asks; warming up is only a head start.
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Stopped while warming up.
        }
    }

    /// <summary>Times every request: a Server-Timing header for the page footer and one line for the log.</summary>
    private static Func<HttpContext, RequestDelegate, Task> Timing(WebUiOptions options) => async (context, next) =>
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["Server-Timing"] = $"app;dur={watch.Elapsed.TotalMilliseconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}";
            return Task.CompletedTask;
        });
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            options.RequestLog?.Invoke($"{context.Request.Method} {context.Request.Path} {context.Response.StatusCode} {watch.Elapsed.TotalMilliseconds:0} ms");
        }
    };

    private static async Task ErrorsToJson(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception ex) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            var (status, problems) = ex switch
            {
                NotFoundException => (404, (IReadOnlyList<string>)[ex.Message]),
                ConflictException => (409, [ex.Message]),
                SetupRefusedException e => (400, e.Problems),
                SettingsInvalidException e => (400, e.Problems),
                WorkspaceException or ConfigException or ArgumentException or BadHttpRequestException or JsonException => (400, [ex.Message]),
                RunLockHeldException => (409, [ex.Message]),
                _ => (500, ["Something went wrong inside Autopilot. The details are in the terminal that runs 'autopilot ui'."]),
            };
            if (status == 500)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Autopilot.Web").LogError(ex, "Unhandled error in {Path}", context.Request.Path);
            }

            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new { error = problems[0], problems }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The page went away; nothing to answer.
        }
    }

    internal static string Version => typeof(WebUi).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0";
}
