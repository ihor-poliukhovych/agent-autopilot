using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevAutopilot.Core;
using DevAutopilot.Core.Config;
using DevAutopilot.Core.Pool;
using DevAutopilot.Core.Settings;
using DevAutopilot.Core.Workspace;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace DevAutopilot.Web;

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
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, options.Port));
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
        var guard = new SessionGuard(options.SessionKey);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(guard);
        builder.Services.AddSingleton<ProjectScopes>();
        builder.Services.AddSingleton<SetupService>();
        builder.Services.AddSingleton<TaskOperations>();
        builder.Services.AddSingleton<PoolOperations>();
        builder.Services.AddSingleton<RunOperations>();

        var app = builder.Build();
        guard.Use(app);
        app.Use(ErrorsToJson);
        var files = new ManifestEmbeddedFileProvider(typeof(WebUi).Assembly, "wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        Endpoints.Map(app);
        return app;
    }

    /// <summary>Runs the UI until cancelled and calls <paramref name="started"/> with the sign-in address.</summary>
    public static async Task RunAsync(WebUiOptions options, Action<string> started, CancellationToken ct)
    {
        var app = Build(options);
        await app.StartAsync(ct).ConfigureAwait(false);
        var port = new Uri(app.Urls.First()).Port;
        started($"http://127.0.0.1:{port}/?key={app.Services.GetRequiredService<SessionGuard>().Key}");
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
                _ => (500, ["Something went wrong inside Dev Autopilot. The details are in the terminal that runs 'autopilot ui'."]),
            };
            if (status == 500)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DevAutopilot.Web").LogError(ex, "Unhandled error in {Path}", context.Request.Path);
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
